using System.Collections.Concurrent;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task TwoNetworksRouteEventsIntoIndependentTreesAndRemovingOneKeepsTheOtherAlive()
    {
        var factory = new FakeIrcTransportFactory();
        var transportA = new FakeIrcTransport(new IrcEndpoint("a.example", 6667, false));
        var transportB = new FakeIrcTransport(new IrcEndpoint("b.example", 6667, false));
        factory.Add(transportA);
        factory.Add(transportB);
        await using var manager = new NetworkSessionManager(factory);
        var networkA = manager.Add(Options("Alpha", transportA.Endpoint, "alice", "#lobby"));
        var networkB = manager.Add(Options("Beta", transportB.Endpoint, "alice", "#lobby"));

        await manager.ConnectAsync(networkA.Id);
        await manager.ConnectAsync(networkB.Id);
        await WaitForAsync(() => transportA.ConnectCount == 1 && transportB.ConnectCount == 1);
        Register(transportA, "a", "alice");
        Register(transportB, "b", "alice");
        transportA.EnqueueInboundLine(":alice!u@a JOIN #lobby");
        transportA.EnqueueInboundLine(":sam!u@a PRIVMSG #lobby :message from alpha");
        transportA.EnqueueInboundLine(":sam!u@a PRIVMSG alice :private from alpha");
        transportB.EnqueueInboundLine(":alice!u@b JOIN #lobby");
        transportB.EnqueueInboundLine(":sam!u@b PRIVMSG #lobby :message from beta");
        transportB.EnqueueInboundLine(":sam!u@b PRIVMSG alice :private from beta");

        await WaitForAsync(() => networkA.Channels.Single().EntriesSnapshot.Any(entry => entry.Text.Contains("alpha", StringComparison.OrdinalIgnoreCase))
            && networkB.Channels.Single().EntriesSnapshot.Any(entry => entry.Text.Contains("beta", StringComparison.OrdinalIgnoreCase))
            && networkA.Queries.Count == 1
            && networkB.Queries.Count == 1);

        Assert.NotSame(networkA.Channels.Single(), networkB.Channels.Single());
        Assert.Equal("#lobby", networkA.Channels.Single().Channel);
        Assert.Equal("#lobby", networkB.Channels.Single().Channel);
        Assert.Contains(networkA.Channels.Single().EntriesSnapshot, entry => entry.Text.Contains("alpha", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(networkA.Channels.Single().EntriesSnapshot, entry => entry.Text.Contains("beta", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(networkA.Queries.Single().EntriesSnapshot, entry => entry.Text.Contains("alpha", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(networkB.Queries.Single().EntriesSnapshot, entry => entry.Text.Contains("beta", StringComparison.OrdinalIgnoreCase));

        await manager.DisconnectAsync(networkA.Id);
        await WaitForAsync(() => networkA.State is NetworkDisplayState.Disconnected or NetworkDisplayState.Failed);
        Assert.Equal(NetworkDisplayState.Registered, networkB.State);
        Assert.Contains(networkB.Channels.Single().EntriesSnapshot, entry => entry.Text.Contains("beta", StringComparison.OrdinalIgnoreCase));

        await manager.RemoveAsync(networkA.Id);
        Assert.Single(manager.Networks);
        Assert.Same(networkB, manager.Networks.Single());
        Assert.Equal("Beta", manager.Networks.Single().DisplayName);
    }

    [Fact]
    public async Task ChannelProjectionUsesAdaptivePrefixAndReconnectStateDoesNotKeepOldMembers()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("irc.example", 6667, false));
        var second = new FakeIrcTransport(first.Endpoint);
        factory.Add(first);
        factory.Add(second);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Adaptive", first.Endpoint, "alice", "#room") with
        {
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))
        });

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "srv", "alice", "005 alice PREFIX=(qaohv)~&@%+ NETWORK=Adaptive");
        first.EnqueueInboundLine(":alice!u@h JOIN #room");
        first.EnqueueInboundLine(":srv 353 alice = #room :~alice @bob +carol");
        first.EnqueueInboundLine(":srv 366 alice #room :End");
        first.EnqueueInboundLine(":srv 332 alice #room :Adaptive topic");

        await WaitForAsync(() => network.Channels.Single().Synchronization == ChannelSynchronizationState.Synchronized);
        var channel = network.Channels.Single();
        Assert.Equal("Adaptive topic", channel.Topic);
        Assert.Equal("~", channel.MembersSnapshot.Single(member => member.Nickname == "alice").PrefixText);
        Assert.Equal("@", channel.MembersSnapshot.Single(member => member.Nickname == "bob").PrefixText);
        Assert.Equal("+", channel.MembersSnapshot.Single(member => member.Nickname == "carol").PrefixText);

        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1 && channel.IsStale);
        Assert.Empty(channel.MembersSnapshot);
        Assert.Equal("restoring", channel.SynchronizationText);

        Register(second, "srv", "alice", "005 alice PREFIX=(ov)@+ NETWORK=Adaptive");
        second.EnqueueInboundLine(":alice!u@h JOIN #room");
        second.EnqueueInboundLine(":srv 353 alice = #room :@alice +dana");
        second.EnqueueInboundLine(":srv 366 alice #room :End");
        await WaitForAsync(() => channel.Synchronization == ChannelSynchronizationState.Synchronized && channel.MembersSnapshot.Count == 2);
        Assert.DoesNotContain(channel.MembersSnapshot, member => member.Nickname == "bob");
        Assert.Equal("@", channel.MembersSnapshot.Single(member => member.Nickname == "alice").PrefixText);
        Assert.Equal("+", channel.MembersSnapshot.Single(member => member.Nickname == "dana").PrefixText);
    }

    [Fact]
    public async Task CommandDispatcherRoutesMessagesAndSupportedCommandsThroughSession()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("commands.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Commands", transport.Endpoint, "alice"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "alice");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
        var dispatcher = new IrcCommandDispatcher(manager);

        var joined = await dispatcher.DispatchAsync(network, network.StatusView, "/join #commands");
        var message = await dispatcher.DispatchAsync(network, joined.View, "/msg bob hello world");
        await dispatcher.DispatchAsync(network, message.View, "/me waves");
        await dispatcher.DispatchAsync(network, message.View, "/nick alice_");
        await dispatcher.DispatchAsync(network, message.View, "/raw NOTICE bob :diagnostic");
        await dispatcher.DispatchAsync(network, joined.View, "/part #commands finished");

        Assert.True(joined.Succeeded);
        Assert.True(message.Succeeded);
        await WaitForAsync(() => transport.OutboundLines.Contains("JOIN #commands")
            && transport.OutboundLines.Contains("PRIVMSG bob :hello world")
            && transport.OutboundLines.Contains("PRIVMSG bob :\u0001ACTION waves\u0001")
            && transport.OutboundLines.Contains("NICK alice_")
            && transport.OutboundLines.Contains("NOTICE bob :diagnostic")
            && transport.OutboundLines.Contains("PART #commands :finished"));
        Assert.Equal("bob", network.Queries.Single().Nickname);
        Assert.Contains(network.Queries.Single().EntriesSnapshot, entry => entry.Text == "hello world");
    }

    [Fact]
    public async Task InactiveViewsExposeUnreadAndImportantActivityUntilActivated()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("activity.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var activityEvents = new ConcurrentQueue<WorkspaceActivityEventArgs>();
        manager.ActivityRaised += (_, args) => activityEvents.Enqueue(args);
        var network = manager.Add(Options("Activity", transport.Endpoint, "alice", "#activity"));
        manager.ActivateView(network.StatusView.Id);
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "alice");
        transport.EnqueueInboundLine(":alice!u@h JOIN #activity");
        transport.EnqueueInboundLine(":bob!u@h PRIVMSG #activity :ordinary activity");
        transport.EnqueueInboundLine(":bob!u@h PRIVMSG alice :private activity");
        await WaitForAsync(() => network.Channels.Single().Activity == WorkspaceActivity.Unread && network.Queries.Count == 1);

        Assert.Equal(WorkspaceActivity.Important, network.Queries.Single().Activity);
        Assert.Contains(activityEvents, item => item.ViewId == network.Queries.Single().Id && item.Activity == WorkspaceActivity.Important);
        manager.ActivateView(network.Channels.Single().Id);
        Assert.Equal(WorkspaceActivity.None, network.Channels.Single().Activity);
        manager.ActivateView(network.Queries.Single().Id);
        Assert.Equal(WorkspaceActivity.None, network.Queries.Single().Activity);
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, params string[] desiredChannels) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Application test",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = desiredChannels.ToHashSet(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string server, string nickname, string? isupport = null)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        if (isupport is not null)
        {
            transport.EnqueueInboundLine($":{server} {isupport}");
        }

        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to nexIRC test network");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The application workspace condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
