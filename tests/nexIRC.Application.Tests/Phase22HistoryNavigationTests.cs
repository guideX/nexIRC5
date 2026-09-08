using System.Diagnostics;
using System.Globalization;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase22HistoryNavigationTests
{
    [Fact]
    public async Task LocalMsgidJumpProjectsBoundedContextWithoutRemoteTraffic()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("phase22-local-jump.example", 6667, false));
        factory.Add(transport);
        await using var logs = new InMemoryConversationLogStore();
        await using var manager = new NetworkSessionManager(factory, logStore: logs);
        var network = manager.Add(Options(transport.Endpoint));
        var channel = manager.EnsureChannel(network.Id, "#room");

        for (var index = 1; index <= 1_000; index++)
        {
            await logs.AppendAsync(Record(network.Id, index));
        }

        var before = transport.OutboundLines.Count;
        var result = await manager.JumpToHistoryMessageAsync(network, channel, "msg-425");

        Assert.Equal(HistoryNavigationOutcome.ExactLocalMatch, result.Outcome);
        Assert.Equal(before, transport.OutboundLines.Count);
        Assert.Equal("msg-425", channel.NavigationAnchor?.ServerMessageId);
        Assert.Contains(channel.EntriesSnapshot, entry => entry.ServerMessageId == "msg-425" && entry.IsNavigationAnchor);
        Assert.InRange(channel.EntryCount, 1, WorkspaceView.MaximumEntries);
        Assert.Equal("msg-375", channel.EntriesSnapshot[0].ServerMessageId);
        Assert.Equal("msg-475", channel.EntriesSnapshot[^1].ServerMessageId);
    }

    [Fact]
    public async Task TimestampJumpUsesDeterministicNearestTieAndLocalNewerPaging()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("phase22-local-forward.example", 6667, false));
        factory.Add(transport);
        await using var logs = new InMemoryConversationLogStore();
        await using var manager = new NetworkSessionManager(factory, logStore: logs);
        var network = manager.Add(Options(transport.Endpoint));
        var channel = manager.EnsureChannel(network.Id, "#room");
        var start = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        for (var index = 1; index <= 500; index++)
        {
            await logs.AppendAsync(Record(network.Id, index, start));
        }

        for (var index = 101; index <= 150; index++)
        {
            channel.Entries.Add(new TranscriptEntry(
                start.AddMinutes(index),
                TranscriptEntryKind.Message,
                "alice",
                $"message-{index}",
                Sequence: index)
            {
                ServerMessageId = $"msg-{index}",
                TimestampSource = ConversationTimestampSource.ServerTime
            });
        }
        channel.EnterHistoryView();
        var before = transport.OutboundLines.Count;

        var timestampResult = await manager.JumpToHistoryTimestampAsync(network, channel, start.AddMinutes(200.75));
        var newerResult = await manager.LoadNewerMessagesAsync(network, channel);
        var newerEntries = channel.EntriesSnapshot;
        var latestResult = await manager.ReturnToLatestAsync(network, channel);

        Assert.Equal(HistoryNavigationOutcome.NearestLocalMatch, timestampResult.Outcome);
        Assert.Equal("msg-201", timestampResult.Anchor.Anchor?.Record.ServerMessageId);
        Assert.Equal(before, transport.OutboundLines.Count);
        Assert.True(newerResult.Succeeded);
        Assert.Contains(newerEntries, entry => entry.ServerMessageId == "msg-151");
        Assert.Equal(HistoryNavigationOutcome.LocalEndReached, latestResult.Outcome);
        Assert.Equal("msg-500", channel.EntriesSnapshot[^1].ServerMessageId);
        Assert.True(channel.IsFollowingLive);
        Assert.False(channel.IsViewingHistory);
    }

    [Fact]
    public void CoverageTracksForwardFrontierAndExhaustionIndependently()
    {
        var network = Guid.NewGuid();
        var key = HistoryCoverageKey.Create(network, "Channel:#room");
        var ledger = new HistoryCoverageLedger();
        var first = Anchor(network, key.Conversation, 10, 1);
        Assert.True(ledger.TryBeginForwardRequest(key, first, "after-10", 1, out _));

        var second = Anchor(network, key.Conversation, 20, 1);
        var snapshot = ledger.CompleteForwardRequest(key, 1, [second], explicitEnd: false, failed: false, "page");
        Assert.Equal("msg-20", snapshot.RemoteForwardFrontier?.ServerMessageId);
        Assert.False(snapshot.RemoteForwardExhausted);
        Assert.False(snapshot.RemoteBackwardExhausted);

        Assert.True(ledger.TryBeginForwardRequest(key, second, "after-20", 1, out _));
        snapshot = ledger.CompleteForwardRequest(key, 1, [], explicitEnd: true, failed: false, "end");
        Assert.True(snapshot.RemoteForwardExhausted);
        Assert.False(snapshot.RemoteBackwardExhausted);
        Assert.False(ledger.TryBeginForwardRequest(key, second, "after-20", 1, out _));
    }

    [Fact]
    public void DuplicateOnlyForwardPageTerminatesWithoutRepeatingTheSelector()
    {
        var network = Guid.NewGuid();
        var key = HistoryCoverageKey.Create(network, "Channel:#room");
        var ledger = new HistoryCoverageLedger();
        var frontier = Anchor(network, key.Conversation, 10, 1);

        Assert.True(ledger.TryBeginForwardRequest(key, frontier, "after-10", 1, out _));
        var snapshot = ledger.CompleteForwardRequest(key, 1, [frontier], explicitEnd: false, failed: false, "duplicate-only");

        Assert.True(snapshot.ForwardNoProgressTerminated);
        Assert.False(ledger.TryBeginForwardRequest(key, frontier, "after-10", 1, out _));
    }

    [Fact]
    public async Task RemoteAfterAdvancesOnceAndEndIsDirectional()
    {
        var endpoint = new IrcEndpoint("phase22-after.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint) with
        {
            RequestedCapabilities = [IrcCapabilityCatalog.Chathistory]
        });
        var channel = manager.EnsureChannel(network.Id, "#room");
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        await WaitForAsync(() => network.Snapshot.Registration == RegistrationState.Registered);
        channel.Entries.Add(new TranscriptEntry(
            DateTimeOffset.Parse("2026-09-07T12:00:00Z", CultureInfo.InvariantCulture),
            TranscriptEntryKind.Message,
            "alice",
            "five-hundred",
            Sequence: 500)
        {
            ServerMessageId = "msg-500",
            TimestampSource = ConversationTimestampSource.ServerTime
        });

        var request = manager.LoadNewerMessagesAsync(network, channel).AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY AFTER #room msgid=msg-500", StringComparison.Ordinal)));
        transport.EnqueueInboundLine("@draft/chathistory-end :srv BATCH +after chathistory #room");
        transport.EnqueueInboundLine("@batch=after;msgid=msg-501;time=2026-09-07T12:01:00.000Z :alice!u@h PRIVMSG #room :five-hundred-one");
        transport.EnqueueInboundLine("@batch=after;msgid=msg-502;time=2026-09-07T12:02:00.000Z :alice!u@h PRIVMSG #room :five-hundred-two");
        transport.EnqueueInboundLine(":srv BATCH -after");

        var result = await request;
        Assert.True(result.Succeeded);
        Assert.Contains(channel.EntriesSnapshot, entry => entry.ServerMessageId == "msg-501");
        var afterCount = transport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY AFTER #room", StringComparison.Ordinal));
        var repeated = await manager.LoadNewerMessagesAsync(network, channel);

        Assert.True(repeated.Succeeded);
        Assert.Equal(afterCount, transport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY AFTER #room", StringComparison.Ordinal)));
        Assert.True(manager.GetHistoryCoverage(network.Id, "Channel:#room").RemoteForwardExhausted);
        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task RemoteAroundCanonicalizesReverseContextAndRepeatsLocally()
    {
        var endpoint = new IrcEndpoint("phase22-around.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint) with
        {
            RequestedCapabilities = [IrcCapabilityCatalog.Chathistory]
        });
        var channel = manager.EnsureChannel(network.Id, "#room");
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        await WaitForAsync(() => network.Snapshot.Registration == RegistrationState.Registered);

        var request = manager.JumpToHistoryMessageAsync(network, channel, "msg-remote-2").AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY AROUND #room msgid=msg-remote-2", StringComparison.Ordinal)));
        transport.EnqueueInboundLine(":srv BATCH +around chathistory #room");
        transport.EnqueueInboundLine("@batch=around;msgid=msg-remote-3;time=2026-09-07T12:03:00Z :alice!u@h PRIVMSG #room :three");
        transport.EnqueueInboundLine("@batch=around;draft/chathistory-context=1;msgid=msg-remote-1;time=2026-09-07T12:01:00Z :alice!u@h PRIVMSG #room :one context");
        transport.EnqueueInboundLine("@batch=around;msgid=msg-remote-2;time=2026-09-07T12:02:00Z :alice!u@h PRIVMSG #room :two");
        transport.EnqueueInboundLine(":srv BATCH -around");

        var result = await request;
        Assert.Equal(HistoryNavigationOutcome.RemotelyRetrievedExactMatch, result.Outcome);
        Assert.Equal("msg-remote-2", channel.NavigationAnchor?.ServerMessageId);
        Assert.Equal(["msg-remote-1", "msg-remote-2", "msg-remote-3"], result.Records.Select(record => record.ServerMessageId!).ToArray());

        var aroundCount = transport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY AROUND #room", StringComparison.Ordinal));
        var repeated = await manager.JumpToHistoryMessageAsync(network, channel, "msg-remote-2");
        Assert.Equal(HistoryNavigationOutcome.ExactLocalMatch, repeated.Outcome);
        Assert.Equal(aroundCount, transport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY AROUND #room", StringComparison.Ordinal)));
        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task RemoteAroundWithoutExactAnchorReportsContextMiss()
    {
        var endpoint = new IrcEndpoint("phase22-around-miss.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint) with
        {
            RequestedCapabilities = [IrcCapabilityCatalog.Chathistory]
        });
        var channel = manager.EnsureChannel(network.Id, "#room");
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        await WaitForAsync(() => network.Snapshot.Registration == RegistrationState.Registered);

        var request = manager.JumpToHistoryMessageAsync(network, channel, "msg-missing").AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY AROUND #room msgid=msg-missing", StringComparison.Ordinal)));
        transport.EnqueueInboundLine(":srv BATCH +around-miss chathistory #room");
        transport.EnqueueInboundLine("@batch=around-miss;draft/chathistory-context=1;msgid=msg-context;time=2026-09-07T12:01:00Z :alice!u@h PRIVMSG #room :context only");
        transport.EnqueueInboundLine("@batch=around-miss;msgid=msg-near;time=2026-09-07T12:02:00Z :alice!u@h PRIVMSG #room :nearest only");
        transport.EnqueueInboundLine(":srv BATCH -around-miss");

        var result = await request;
        Assert.Equal(HistoryNavigationOutcome.RemotelyRetrievedContext, result.Outcome);
        Assert.False(result.IsExact);
        Assert.Contains(result.Records, record => record.ServerMessageId == "msg-near");
        Assert.DoesNotContain(channel.EntriesSnapshot, entry => entry.IsNavigationAnchor);
        await manager.DisconnectAsync(network.Id);
    }

    private static NetworkConnectionOptions Options(IrcEndpoint endpoint) => new()
    {
        DisplayName = "Phase 22",
        Endpoint = endpoint,
        Nickname = "nex",
        Username = "nex",
        RealName = "Phase 22 test",
        RequestedCapabilities = Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static ConversationLogRecord Record(Guid network, int index, DateTimeOffset? start = null) => new()
    {
        Timestamp = (start ?? new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)).AddMinutes(index),
        NetworkId = network,
        ScopeId = network,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        ConversationKey = "Channel:#room",
        Sender = "alice",
        MessageKind = LogMessageKind.Message,
        Direction = LogDirection.Incoming,
        Text = $"message-{index}",
        ServerMessageId = $"msg-{index}",
        TimestampSource = ConversationTimestampSource.ServerTime,
        Provenance = ConversationEntryProvenance.Live
    };

    private static HistoryCoverageAnchor Anchor(Guid network, string conversation, int index, int generation) => new()
    {
        NetworkId = network,
        Conversation = conversation,
        Timestamp = new DateTimeOffset(2026, 9, 7, 12, index, 0, TimeSpan.Zero),
        ServerMessageId = $"msg-{index}",
        Reference = ChathistoryReference.MessageId($"msg-{index}"),
        ConnectionGeneration = generation,
        Target = "#room",
        Provenance = HistoryCoverageProvenance.RequestResult
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 22 condition did not complete.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static void Register(FakeIrcTransport transport, string nickname)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv CAP * ACK :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv 005 nex CHATHISTORY=50 MSGREFTYPES=msgid,timestamp CHANTYPES=# :supported");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }
}
