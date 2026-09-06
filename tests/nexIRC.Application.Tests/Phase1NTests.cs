using System.Collections.Concurrent;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1NTests
{
    [Fact]
    public async Task SerializedWorkspaceDispatcherPreservesExplicitSubmissionOrderAndDrains()
    {
        var dispatcher = new SerializedWorkspaceDispatcher(new ImmediateWorkspaceDispatcher());
        var order = new List<int>();
        var work = new List<Task>();

        for (var index = 0; index < 128; index++)
        {
            var captured = index;
            work.Add(dispatcher.InvokeAsync(() => order.Add(captured)).AsTask());
        }

        await Task.WhenAll(work);
        await dispatcher.FlushAsync();

        Assert.Equal(Enumerable.Range(0, 128), order);
        Assert.Equal(128, dispatcher.Diagnostics.QueuedActions);
        Assert.Equal(128, dispatcher.Diagnostics.ProcessedActions);
        Assert.Equal(0, dispatcher.Diagnostics.CurrentQueueDepth);
        await dispatcher.CompleteAsync();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            var task = dispatcher.InvokeAsync(static () => { }).AsTask();
            task.GetAwaiter().GetResult();
        });
    }

    [Fact]
    public async Task ResynchronizationIsStructuralOnlyAndLiveActivityUsesBoundedReadState()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("resync.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var notifications = new ConcurrentQueue<IrcNotification>();
        using var subscription = manager.Notifications.Subscribe(notifications.Enqueue);
        var network = manager.Add(Options("Resync", transport.Endpoint, "alice", "#room"));

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(manager, () => transport.ConnectCount == 1);
        Register(transport, "alice");
        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        transport.EnqueueInboundLine(":srv 353 alice = #room :@alice +bob");
        transport.EnqueueInboundLine(":srv 366 alice #room :End of names");
        transport.EnqueueInboundLine(":srv 332 alice #room :Restored topic");
        transport.EnqueueInboundLine(":srv 324 alice #room +nt");

        await WaitForAsync(manager, () => network.Channels.Single().Synchronization == nexIRC.Core.Session.ChannelSynchronizationState.Synchronized);
        await manager.FlushStateDispatchAsync();
        var channel = network.Channels.Single();
        Assert.Equal(WorkspaceActivity.None, channel.Activity);
        Assert.Equal(0, channel.UnreadCount);
        Assert.Equal(0, channel.ImportantCount);
        Assert.Equal(0, channel.HighlightCount);
        Assert.DoesNotContain(notifications, item => item.ViewId == channel.Id);

        transport.EnqueueInboundLine(":bob!u@host PRIVMSG #room :ordinary live message");
        transport.EnqueueInboundLine(":bob!u@host PRIVMSG #room :alice, this is live");
        await WaitForAsync(manager, () => channel.Activity == WorkspaceActivity.Important && channel.UnreadCount == 2 && channel.HighlightCount == 1);
        Assert.Equal(1, channel.ImportantCount);

        manager.ActivateView(channel.Id);
        Assert.Equal(WorkspaceActivity.None, channel.Activity);
        Assert.Equal(0, channel.UnreadCount);
        Assert.Equal(0, channel.ImportantCount);
        Assert.Equal(0, channel.HighlightCount);

        channel.SetHistoryContext([
            new HistoryContextEntry(new ConversationLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
                NetworkId = network.Id,
                ScopeId = network.Id,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#room",
                Text = "historical"
            })
        ], "historical");
        Assert.Equal(WorkspaceActivity.None, channel.Activity);
        Assert.Equal(0, channel.UnreadCount);

        transport.EnqueueInboundLine(":bob!u@host PRIVMSG #room :active-window message");
        await WaitForAsync(manager, () => channel.EntriesSnapshot.Any(item => item.Text == "active-window message"));
        Assert.Equal(WorkspaceActivity.None, channel.Activity);
        Assert.Equal(0, channel.UnreadCount);
    }

    [Fact]
    public async Task ClosingAndReopeningAViewNeverPartsAndExplicitRejoinRestoresIt()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("lifecycle-n.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Lifecycle N", transport.Endpoint, "alice"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(manager, () => transport.ConnectCount == 1);
        Register(transport, "alice");
        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        await WaitForAsync(manager, () => network.Channels.FirstOrDefault() is { IsJoined: true });

        var channel = network.Channels.Single();
        var outboundBeforeClose = transport.OutboundLines.Count;
        Assert.True(manager.CloseView(channel.Id));
        Assert.False(channel.IsViewOpen);
        Assert.DoesNotContain(transport.OutboundLines.Skip(outboundBeforeClose), line => line.StartsWith("PART", StringComparison.Ordinal));
        Assert.True(manager.ReopenView(channel.Id));
        Assert.Same(channel, manager.ActiveView);

        await manager.PartAndCloseAsync(channel.Id);
        await WaitForAsync(manager, () => transport.OutboundLines.Any(line => line.StartsWith("PART #room", StringComparison.Ordinal)));
        Assert.False(channel.IsViewOpen);
        Assert.Equal(ConversationLifecycleState.Parted, channel.LifecycleState);

        var beforeHistoricalOpen = transport.OutboundLines.Count;
        var reopened = manager.OpenHistoricalConversation(network.Id, DestinationKind.Channel, "#room");
        Assert.Same(channel, reopened);
        Assert.Equal(beforeHistoricalOpen, transport.OutboundLines.Count);

        await manager.RejoinChannelAsync(network.Id, "#room");
        await WaitForAsync(manager, () => transport.OutboundLines.Skip(beforeHistoricalOpen).Contains("JOIN #room"));
        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        transport.EnqueueInboundLine(":srv 353 alice = #room :alice");
        transport.EnqueueInboundLine(":srv 366 alice #room :End of names");
        await WaitForAsync(manager, () => channel.IsJoined && channel.LifecycleState == ConversationLifecycleState.Joined);
        Assert.Single(network.Channels);
    }

    [Fact]
    public async Task SelfKickRetainsTheConversationAsKickedUntilExplicitRejoin()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("kick-n.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Kick N", transport.Endpoint, "alice", "#room"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(manager, () => transport.ConnectCount == 1);
        Register(transport, "alice");
        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        await WaitForAsync(manager, () => network.Channels.FirstOrDefault() is { IsJoined: true });

        var channel = network.Channels.Single();
        transport.EnqueueInboundLine(":op!u@host KICK #room alice :removed");
        await WaitForAsync(manager, () => channel.LifecycleState == ConversationLifecycleState.Kicked);
        Assert.False(channel.IsJoined);
        Assert.Contains("#room", network.Snapshot.DesiredChannels);

        var beforeRejoin = transport.OutboundLines.Count;
        manager.OpenHistoricalConversation(network.Id, DestinationKind.Channel, "#room");
        Assert.Equal(beforeRejoin, transport.OutboundLines.Count);
        await manager.RejoinChannelAsync(network.Id, "#room");
        await WaitForAsync(manager, () => transport.OutboundLines.Skip(beforeRejoin).Contains("JOIN #room"));
    }

    [Fact]
    public async Task MsgidDuplicateDeliveryIsProjectedOnceWhileDistinctMessagesRemainDistinct()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("dedupe.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var notifications = new ConcurrentQueue<IrcNotification>();
        using var subscription = manager.Notifications.Subscribe(notifications.Enqueue);
        var network = manager.Add(Options("Dedupe", transport.Endpoint, "alice"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(manager, () => transport.ConnectCount == 1);
        Register(transport, "alice");

        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        await WaitForAsync(manager, () => network.Channels.FirstOrDefault() is { IsJoined: true });
        manager.ActivateView(network.Channels.Single().Id);
        transport.EnqueueInboundLine("@msgid=one :bob!u@host PRIVMSG #room :same text");
        transport.EnqueueInboundLine("@msgid=one :bob!u@host PRIVMSG #room :same text");
        transport.EnqueueInboundLine("@msgid=two :bob!u@host PRIVMSG #room :same text");

        var channel = network.Channels.Single();
        await WaitForAsync(manager, () => channel.EntriesSnapshot.Count(entry => entry.Text == "same text") == 2);
        Assert.Equal(2, channel.EntriesSnapshot.Count(entry => entry.Text == "same text"));
        await WaitForAsync(manager, () => notifications.Count(item => item.ViewId == channel.Id && item.Type == IrcNotificationType.Message) == 2);
        Assert.True(manager.Diagnostics.DuplicateSemanticEventsDiscarded >= 1);
    }

    [Fact]
    public async Task DeterministicLiveBurstIsSerializedWithoutDroppingOrReordering()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("burst.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Burst", transport.Endpoint, "alice"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(manager, () => transport.ConnectCount == 1);
        Register(transport, "alice");
        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        await WaitForAsync(manager, () => network.Channels.FirstOrDefault() is { IsJoined: true });

        var channel = network.Channels.Single();
        const int burstSize = 2_000;
        for (var index = 0; index < burstSize; index++)
        {
            transport.EnqueueInboundLine($":bob!u@host PRIVMSG #room :burst-{index:D4}");
        }

        await WaitForAsync(manager, () => channel.UnreadCount == burstSize, 10_000);
        await manager.FlushStateDispatchAsync();
        var burst = channel.EntriesSnapshot
            .Where(entry => entry.Text.StartsWith("burst-", StringComparison.Ordinal))
            .Select(entry => entry.Text)
            .ToArray();

        Assert.Equal(500, burst.Length);
        Assert.Equal(Enumerable.Range(burstSize - burst.Length, burst.Length).Select(index => $"burst-{index:D4}"), burst);
        Assert.Equal(burstSize, channel.UnreadCount);
        Assert.Equal(0, manager.Diagnostics.CurrentQueueDepth);
        Assert.True(manager.Diagnostics.MaximumQueueDepth >= 1);
    }

    [Fact]
    public async Task IgnoredStructuralEventsStillReconcileMembersWithoutActivityOrNotifications()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("ignore-n.example", 6667, false));
        factory.Add(transport);
        var configuration = new ConfigurationService(new InMemoryConfigurationStore());
        await using var manager = new NetworkSessionManager(factory, configuration: configuration);
        var network = manager.Add(Options("Ignore N", transport.Endpoint, "alice"));
        configuration.AddIgnore(new IgnoreRule { Nickname = "bob" });
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(manager, () => transport.ConnectCount == 1);
        Register(transport, "alice");
        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        transport.EnqueueInboundLine(":bob!u@host JOIN #room");
        await WaitForAsync(manager, () => network.Channels.FirstOrDefault() is { } channel
            && channel.MembersSnapshot.Any(member => member.Nickname == "bob"));

        var channel = network.Channels.Single();
        transport.EnqueueInboundLine(":bob!u@host PRIVMSG #room :hidden");
        transport.EnqueueInboundLine(":bob!u@host NICK robert");
        await WaitForAsync(manager, () => channel.MembersSnapshot.Any(member => member.Nickname == "robert"));
        Assert.DoesNotContain(channel.EntriesSnapshot, entry => entry.Text.Contains("hidden", StringComparison.Ordinal));
        Assert.Equal(WorkspaceActivity.None, channel.Activity);
        Assert.Equal(0, channel.UnreadCount);
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, params string[] desiredChannels) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1N test",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = desiredChannels.ToHashSet(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(NetworkSessionManager manager, Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        if (condition())
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Signal(object? sender, EventArgs args)
        {
            if (condition())
            {
                completion.TrySetResult(true);
            }
        }

        manager.NavigationChanged += Signal;
        manager.OperationFeedback.PropertyChanged += Signal;
        try
        {
            Signal(null, EventArgs.Empty);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.WhenAny(completion.Task, Task.Delay(remaining));
            }

            if (!condition())
            {
                throw new TimeoutException("The Phase 1N condition was not reached.");
            }
        }
        finally
        {
            manager.NavigationChanged -= Signal;
            manager.OperationFeedback.PropertyChanged -= Signal;
        }
    }
}
