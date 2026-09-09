using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase26ReactionApplicationTests
{
    [Fact]
    public async Task NoEchoReactionIsOptimisticAndNeverCreatesATranscriptRow()
    {
        var endpoint = new IrcEndpoint("reaction-app.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Reactions",
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = [IrcCapabilityCatalog.MessageTags],
            Reconnect = new ReconnectPolicy(Enabled: false)
        });
        var router = new WorkspaceActionRouter(manager);

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :message-tags");
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => network.Snapshot.Registration == RegistrationState.Registered);
        transport.EnqueueInboundLine(":alice!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.Any(item => item.IsJoined));
        transport.EnqueueInboundLine("@msgid=P :bob!u@h PRIVMSG #room :Parent");

        var channel = network.Channels.Single();
        await WaitForAsync(() => channel.EntriesSnapshot.Any(entry => entry.ServerMessageId == "P"));
        var parent = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P");
        var entryCountBeforeReaction = channel.EntryCount;
        var result = await router.SendReactionAsync(network, channel, parent, "👍");

        Assert.True(result.Succeeded, result.Message);
        await WaitForAsync(() => channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P").ReactionSummary.Count == 1);
        var updated = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P");
        Assert.Equal(1, updated.ReactionSummary[0].Count);
        Assert.True(updated.ReactionSummary[0].CurrentUserReacted);
        Assert.Equal(entryCountBeforeReaction, channel.EntryCount);
        Assert.Contains("@+reply=P;+draft/react=👍 TAGMSG #room", transport.OutboundLines);

        transport.EnqueueInboundLine("@+reply=P;+draft/react=👍;account=alice;msgid=Echo.ID :alice!u@h TAGMSG #room");
        await WaitForAsync(() => ReactionCount(channel) == 1);
        Assert.Equal(1, channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P").ReactionSummary[0].Count);

        var removed = await router.SendReactionAsync(network, channel, updated, "👍", unreaction: true);
        Assert.True(removed.Succeeded, removed.Message);
        await WaitForAsync(() => channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P").ReactionSummary.Count == 0);
        Assert.Equal(entryCountBeforeReaction, channel.EntryCount);

        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task EchoReactionAggregatesActorsAndUnreactIsIdempotentWithoutUnreadActivity()
    {
        var endpoint = new IrcEndpoint("reaction-echo.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Reaction echoes",
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = [IrcCapabilityCatalog.MessageTags, IrcCapabilityCatalog.EchoMessage],
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
        await WaitForAsync(() => network.Channels.Any(item => item.IsJoined));
        transport.EnqueueInboundLine("@msgid=P :bob!u@h PRIVMSG #room :Parent");
        var channel = network.Channels.Single();
        await WaitForAsync(() => channel.EntriesSnapshot.Any(entry => entry.ServerMessageId == "P"));
        var parent = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P");
        channel.MarkRead();
        var entryCountBeforeReactions = channel.EntryCount;

        var result = await router.SendReactionAsync(network, channel, parent, "heart");
        Assert.True(result.Succeeded, result.Message);
        await WaitForAsync(() => transport.OutboundLines.Any(line => line == "@+reply=P;+draft/react=heart TAGMSG #room"));
        Assert.Empty(channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P").ReactionSummary);

        transport.EnqueueInboundLine("@+reply=P;+draft/react=heart;msgid=R1 :alice!u@h TAGMSG #room");
        await WaitForAsync(() => ReactionCount(channel) == 1);
        var ownEcho = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P").ReactionSummary[0];
        Assert.True(ownEcho.CurrentUserReacted);
        Assert.Equal(WorkspaceActivity.None, channel.Activity);

        transport.EnqueueInboundLine("@+reply=P;+draft/react=heart;msgid=R2 :bob!u@h TAGMSG #room");
        await WaitForAsync(() => ReactionCount(channel) == 2);
        transport.EnqueueInboundLine("@+reply=P;+draft/unreact=heart;msgid=R3 :bob!u@h TAGMSG #room");
        await WaitForAsync(() => ReactionCount(channel) == 1);
        transport.EnqueueInboundLine("@+reply=P;+draft/unreact=heart;msgid=R4 :bob!u@h TAGMSG #room");
        await Task.Delay(50);
        Assert.Equal(1, channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "P").ReactionSummary[0].Count);
        Assert.Equal(entryCountBeforeReactions, channel.EntryCount);

        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task QueryReactionUsesThePrivateConversationTargetAndReconcilesTheEcho()
    {
        var endpoint = new IrcEndpoint("reaction-query.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Query reactions",
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = [IrcCapabilityCatalog.MessageTags, IrcCapabilityCatalog.EchoMessage],
            Reconnect = new ReconnectPolicy(Enabled: false)
        });
        var router = new WorkspaceActionRouter(manager);

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :message-tags echo-message");
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags echo-message");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => network.Snapshot.Registration == RegistrationState.Registered);
        transport.EnqueueInboundLine("@msgid=Q-P :bob!u@h PRIVMSG alice :Private parent");

        await WaitForAsync(() => network.Queries.Any(query => query.Nickname == "bob" && query.EntriesSnapshot.Any(entry => entry.ServerMessageId == "Q-P")));
        var query = network.Queries.Single(item => item.Nickname == "bob");
        var parent = query.EntriesSnapshot.Single(entry => entry.ServerMessageId == "Q-P");
        var result = await router.SendReactionAsync(network, query, parent, "heart");

        Assert.True(result.Succeeded, result.Message);
        await WaitForAsync(() => transport.OutboundLines.Any(line => line == "@+reply=Q-P;+draft/react=heart TAGMSG bob"));
        Assert.Empty(query.EntriesSnapshot.Single(entry => entry.ServerMessageId == "Q-P").ReactionSummary);

        transport.EnqueueInboundLine("@+reply=Q-P;+draft/react=heart;msgid=Q-R :alice!u@h TAGMSG bob");
        await WaitForAsync(() => ReactionCount(query, "Q-P") == 1);
        var reaction = query.EntriesSnapshot.Single(entry => entry.ServerMessageId == "Q-P").ReactionSummary.Single();
        Assert.True(reaction.CurrentUserReacted);
        Assert.Equal(1, query.EntryCount);

        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task DurableReactionRecordsHydrateOnlyTheirNetworkAndConversation()
    {
        var store = new InMemoryConversationLogStore();
        var network = Guid.NewGuid();
        var otherNetwork = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var key = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#room");
        await store.AppendAsync(ReactionRecord(network, scope, key, "Event.1", "P", "heart", "actor-a", DurableReactionOperation.React));
        await store.AppendAsync(ReactionRecord(otherNetwork, scope, key, "Event.2", "P", "heart", "actor-b", DurableReactionOperation.React));
        await store.AppendAsync(ReactionRecord(network, scope, key, "Event.3", "P", "heart", "actor-a", DurableReactionOperation.Unreact));

        var records = await store.ReadReactionEventsAsync(network, scope, LogConversationKind.Channel, "#room", key);
        var state = new ReactionStateStore();
        foreach (var record in records)
        {
            state.ApplyRecord(record);
        }

        Assert.Empty(state.GetSummary("P"));
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(network, record.NetworkId));
    }

    [Fact]
    public void ReactionAggregationIsIdempotentSupportsMultipleValuesAndPreservesOrder()
    {
        var network = Guid.NewGuid();
        var state = new ReactionStateStore();
        var alice = ReactionActorIdentity.ForLocal(network, "alice");
        var bob = ReactionActorIdentity.ForLocal(network, "bob");
        state.SetCurrentActor(alice);

        Assert.Equal(1, state.Apply("Parent.ID", "👍", ReactionOperation.React, alice));
        Assert.Equal(0, state.Apply("Parent.ID", "👍", ReactionOperation.React, alice));
        Assert.Equal(1, state.Apply("Parent.ID", "👍", ReactionOperation.React, bob));
        Assert.Equal(1, state.Apply("Parent.ID", "🎉", ReactionOperation.React, alice));
        var summary = state.GetSummary("Parent.ID");
        Assert.Equal(["👍", "🎉"], summary.Select(item => item.Value));
        Assert.Equal(2, summary[0].Count);
        Assert.Equal(1, summary[1].Count);
        Assert.True(summary[0].CurrentUserReacted);

        Assert.Equal(1, state.Apply("Parent.ID", "👍", ReactionOperation.Unreact, bob));
        Assert.Equal(0, state.Apply("Parent.ID", "👍", ReactionOperation.Unreact, bob));
        Assert.Equal(1, state.GetSummary("Parent.ID")[0].Count);
    }

    [Fact]
    public async Task ReactionBeforeParentAttachesWhenHistoricalParentIsProjected()
    {
        var endpoint = new IrcEndpoint("reaction-history.example", 6667, false);
        var factory = new FakeIrcTransportFactory();
        factory.Add(new FakeIrcTransport(endpoint));
        var store = new InMemoryConversationLogStore();
        await using var manager = new NetworkSessionManager(factory, logStore: store);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Reaction history",
            Endpoint = endpoint,
            Nickname = "alice"
        });
        var channel = manager.EnsureChannel(network.Id, "#room");
        var key = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#room");
        var parentRecord = new ConversationLogRecord
        {
            NetworkId = network.Id,
            ScopeId = network.Id,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            ConversationKey = key,
            Timestamp = DateTimeOffset.UtcNow,
            ServerMessageId = "Parent.ID",
            Sender = "bob",
            MessageKind = LogMessageKind.Message,
            Text = "Parent"
        };
        await store.AppendAsync(parentRecord);
        await store.AppendAsync(ReactionRecord(network.Id, network.Id, key, "Reaction.ID", "Parent.ID", "heart", "actor-a", DurableReactionOperation.React));

        var navigation = await manager.NavigateToHistorySearchResultAsync(network, channel, new ConversationLogSearchResult(parentRecord, parentRecord.Text));
        Assert.True(navigation.Succeeded, navigation.Message);
        await manager.FlushStateDispatchAsync();

        var parent = channel.EntriesSnapshot.Single(entry => entry.ServerMessageId == "Parent.ID");
        Assert.Equal(1, parent.ReactionSummary.Single().Count);
        Assert.Equal(1, channel.EntryCount);
    }

    [Fact]
    public async Task JsonlReactionRecordsSurviveStoreReopenWithoutRewritingMessageHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nexirc-phase26-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var network = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var key = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#room");
        try
        {
            await using (var first = new JsonlConversationLogStore(root))
            {
                await first.AppendAsync(ReactionRecord(network, scope, key, "Event.1", "Parent.ID", "heart", "actor-a", DurableReactionOperation.React));
                await first.FlushAsync();
            }

            await using var reopened = new JsonlConversationLogStore(root);
            var records = await reopened.ReadReactionEventsAsync(network, scope, LogConversationKind.Channel, "#room", key);
            Assert.Single(records);
            Assert.True(records[0].IsReactionEvent);
            Assert.Equal(ConversationHistorySchema.CurrentVersion, records[0].SchemaVersion);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ScaleFixtureKeepsRelationshipRebuildBoundedAndIndependentOfOrdinaryMessageCount()
    {
        var ordinaryMessages = Enumerable.Range(0, 50_000)
            .Select(index => $"Message.{index}")
            .ToArray();
        var network = Guid.NewGuid();
        var state = new ReactionStateStore();
        var actors = Enumerable.Range(0, 20)
            .Select(index => ReactionActorIdentity.ForLocal(network, $"actor-{index}"))
            .ToArray();

        for (var index = 0; index < 5_000; index++)
        {
            var parent = $"Parent.{index % 1_000}";
            var value = index % 3 == 0 ? "👍" : index % 3 == 1 ? "😂" : "🎉";
            state.Apply(parent, value, ReactionOperation.React, actors[index % actors.Length]);
            if (index % 11 == 0)
            {
                state.Apply(parent, value, ReactionOperation.Unreact, actors[index % actors.Length]);
            }
        }

        Assert.Equal(50_000, ordinaryMessages.Length);
        Assert.True(state.ParentCount <= ReactionStateStore.MaximumPendingParents);
        Assert.NotEmpty(state.GetSummary("Parent.42"));
    }

    private static ConversationLogRecord ReactionRecord(
        Guid network,
        Guid scope,
        string key,
        string eventId,
        string parent,
        string value,
        string actor,
        DurableReactionOperation operation) => new()
    {
        NetworkId = network,
        ScopeId = scope,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        ConversationKey = key,
        Timestamp = DateTimeOffset.UtcNow,
        ServerMessageId = eventId,
        Sender = actor,
        MessageKind = LogMessageKind.Reaction,
        Text = value,
        ReactionParentMessageId = parent,
        ReactionValue = value,
        ReactionOperation = operation,
        ReactionActorKey = actor,
        ReactionActorDisplay = actor
    };

    private static int ReactionCount(ChannelView channel)
    {
        foreach (var entry in channel.EntriesSnapshot)
        {
            if (entry.ServerMessageId == "P")
            {
                return entry.ReactionSummary.Count == 0 ? 0 : entry.ReactionSummary[0].Count;
            }
        }

        return 0;
    }

    private static int ReactionCount(WorkspaceView view, string parentMessageId)
    {
        var summary = view.EntriesSnapshot.SingleOrDefault(entry => entry.ServerMessageId == parentMessageId)?.ReactionSummary;
        return summary is { Count: > 0 } ? summary[0].Count : 0;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The deterministic reaction application condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
