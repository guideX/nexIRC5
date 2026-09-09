using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase25ApplicationReplyTests
{
    [Fact]
    public async Task ReplyComposerSendsOneTaggedMessageAndEchoProjectsOneCanonicalEntry()
    {
        var endpoint = new IrcEndpoint("reply-app.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Replies",
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = ["message-tags", "echo-message"],
            Reconnect = new ReconnectPolicy(Enabled: false)
        });
        var router = new WorkspaceActionRouter(manager);

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :message-tags echo-message");
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags echo-message");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => network.Snapshot.Registration == RegistrationState.Registered);
        transport.EnqueueInboundLine(":alice!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.FirstOrDefault()?.IsJoined == true);

        transport.EnqueueInboundLine("@msgid=A :alice!u@h PRIVMSG #room :Original message");
        await WaitForAsync(() => network.Channels.Single().EntriesSnapshot.Any(entry => entry.ServerMessageId == "A"));
        var channel = network.Channels.Single();
        var parent = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "A");

        Assert.True(router.TryCreateReplyComposer(network, channel, parent, out var composer, out var reason), reason);
        var sent = await router.SendReplyAsync(network, channel, composer!, "This is my reply");
        Assert.True(sent.Succeeded, sent.Message);
        await WaitForAsync(() => transport.OutboundLines.Count(line => line.Contains("PRIVMSG #room", StringComparison.Ordinal) && line.Contains("+reply=A", StringComparison.Ordinal)) == 1);

        transport.EnqueueInboundLine("@msgid=B;+reply=A :alice!u@h PRIVMSG #room :This is my reply");
        await WaitForAsync(() => channel.EntriesSnapshot.Count(entry => entry.ServerMessageId is "A" or "B") == 2);
        var reply = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "B");
        Assert.Equal("A", reply.ReplyParentMessageId);
        Assert.Equal(ReplyResolutionState.ResolvedLocally, reply.ReplyResolution);
        Assert.Equal(2, channel.EntriesSnapshot.Count(entry => entry.ServerMessageId is "A" or "B"));

        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task MissingParentUsesLocalProjectedNavigationWithoutCreatingAnotherRequest()
    {
        var endpoint = new IrcEndpoint("reply-local.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Local reply",
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = ["message-tags"],
            Reconnect = new ReconnectPolicy(Enabled: false)
        });
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :message-tags");
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => network.Snapshot.Registration == RegistrationState.Registered);
        transport.EnqueueInboundLine(":alice!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.FirstOrDefault()?.IsJoined == true);
        transport.EnqueueInboundLine("@msgid=P :bob!u@h PRIVMSG #room :Parent");
        transport.EnqueueInboundLine("@msgid=R;+reply=P :alice!u@h PRIVMSG #room :Reply");
        var channel = network.Channels.Single();
        await WaitForAsync(() => channel.EntriesSnapshot.Any(entry => entry.ServerMessageId == "R"));

        var reply = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "R");
        var outboundBefore = transport.OutboundLines.Count;
        var result = await manager.NavigateReplyParentAsync(network, channel, reply);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("P", channel.NavigationAnchor?.ServerMessageId);
        Assert.Equal(outboundBefore, transport.OutboundLines.Count);
        await manager.DisconnectAsync(network.Id);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The deterministic application reply condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
