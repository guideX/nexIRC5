using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase1YHistoryDiscoveryTests
{
    [Fact]
    public async Task TargetsBatchReturnsBoundedDeduplicatedDiscoveryRows()
    {
        var (session, transport, run) = await StartRegisteredAsync(includeEventPlayback: false);
        var request = ChathistoryRequest.ForTargets(
            Guid.Empty,
            session.Snapshot.ConnectionGeneration,
            ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)),
            ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 13, 0, 0, TimeSpan.Zero)),
            10);
        var pending = session.RequestHistoryAsync(request).AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY TARGETS timestamp=2026-09-07T12:00:00.000Z", StringComparison.Ordinal)));

        transport.EnqueueInboundLine(":srv BATCH +targets draft/chathistory-targets");
        transport.EnqueueInboundLine("@batch=targets :srv CHATHISTORY TARGETS Alice 2026-09-07T12:15:00.000Z");
        transport.EnqueueInboundLine("@batch=targets :srv CHATHISTORY TARGETS Alice 2026-09-07T12:16:00.000Z");
        transport.EnqueueInboundLine("@batch=targets :srv CHATHISTORY TARGETS #room 2026-09-07T12:20:00.000Z");
        transport.EnqueueInboundLine(":srv BATCH -targets");

        var result = await pending;
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Targets.Count);
        Assert.Equal("Alice", result.Targets.Single(target => target.Kind == ChathistoryTargetKind.Query).Target);
        Assert.Equal("#room", result.Targets.Single(target => target.Kind == ChathistoryTargetKind.Channel).Target);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 12, 16, 0, TimeSpan.Zero), result.Targets.Single(target => target.Target == "Alice").LatestTimestamp);
        Assert.Empty(session.Snapshot.Channels);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task HistoricalStateEventsProjectWithoutChangingCurrentSessionState()
    {
        var (session, transport, run) = await StartRegisteredAsync(includeEventPlayback: true);
        transport.EnqueueInboundLine(":alice!u@h JOIN #room");
        transport.EnqueueInboundLine(":srv 353 nex = #room :@alice");
        transport.EnqueueInboundLine(":srv 366 nex #room :End");
        transport.EnqueueInboundLine(":alice!u@h MODE #room +o alice");
        transport.EnqueueInboundLine(":alice!u@h TOPIC #room :current topic");
        await WaitForAsync(() => session.Snapshot.Channels.SingleOrDefault() is { Members.Count: 1, Topic: "current topic" });

        var before = session.Snapshot;
        var request = new ChathistoryRequest
        {
            NetworkId = Guid.Empty,
            ConnectionGeneration = before.ConnectionGeneration,
            Conversation = "Channel:#room",
            Target = "#room",
            Operation = ChathistoryOperation.Around,
            Reference = ChathistoryReference.MessageId("anchor"),
            Limit = 20,
            Purpose = ChathistoryRequestPurpose.LoadContext
        };
        var pending = session.RequestHistoryAsync(request).AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY AROUND #room msgid=anchor", StringComparison.Ordinal)));

        transport.EnqueueInboundLine(":srv BATCH +history chathistory #room");
        transport.EnqueueInboundLine("@batch=history;msgid=join-1;time=2026-09-07T10:00:00.000Z :bob!u@h JOIN #room");
        transport.EnqueueInboundLine("@batch=history;msgid=part-1;time=2026-09-07T10:01:00.000Z :bob!u@h PART #room :old");
        transport.EnqueueInboundLine("@batch=history;msgid=quit-1;time=2026-09-07T10:02:00.000Z :bob!u@h QUIT :old");
        transport.EnqueueInboundLine("@batch=history;msgid=nick-1;time=2026-09-07T10:03:00.000Z :alice!u@h NICK alicia");
        transport.EnqueueInboundLine("@batch=history;msgid=mode-1;time=2026-09-07T10:04:00.000Z :op!u@h MODE #room +o bob");
        transport.EnqueueInboundLine("@batch=history;msgid=topic-1;time=2026-09-07T10:05:00.000Z :op!u@h TOPIC #room :old topic");
        transport.EnqueueInboundLine(":srv BATCH -history");

        var result = await pending;
        Assert.True(result.Succeeded);
        Assert.Contains(result.Messages, item => item is IrcJoinEvent { IsHistorical: true });
        Assert.Contains(result.Messages, item => item is IrcPartEvent { IsHistorical: true });
        Assert.Contains(result.Messages, item => item is IrcQuitEvent { IsHistorical: true });
        Assert.Contains(result.Messages, item => item is IrcNicknameChangedEvent { IsHistorical: true });
        Assert.Contains(result.Messages, item => item is IrcModeEvent { IsHistorical: true });
        Assert.Contains(result.Messages, item => item is IrcTopicEvent { IsHistorical: true });

        var after = session.Snapshot;
        var beforeChannel = before.Channels.Single();
        var afterChannel = after.Channels.Single();
        Assert.Equal(beforeChannel.IsJoined, afterChannel.IsJoined);
        Assert.Equal(beforeChannel.Topic, afterChannel.Topic);
        Assert.Equal(beforeChannel.Members.Keys, afterChannel.Members.Keys);
        Assert.Equal(beforeChannel.Members["alice"].PrefixModes, afterChannel.Members["alice"].PrefixModes);
        Assert.DoesNotContain(after.Queries, query => query.Nickname == "alicia");

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task EventPlaybackNakKeepsOrdinaryMessageOnlyHistorySafe()
    {
        var (session, transport, run) = await StartRegisteredAsync(includeEventPlayback: true, advertiseEventPlayback: true, ackEventPlayback: false);
        Assert.False(session.Snapshot.Features.Chathistory.EventPlaybackEnabled);

        var request = new ChathistoryRequest
        {
            NetworkId = Guid.Empty,
            ConnectionGeneration = session.Snapshot.ConnectionGeneration,
            Conversation = "Channel:#room",
            Target = "#room",
            Operation = ChathistoryOperation.Before,
            Reference = ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)),
            Limit = 10
        };
        var pending = session.RequestHistoryAsync(request).AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BEFORE #room timestamp=", StringComparison.Ordinal)));
        transport.EnqueueInboundLine(":srv BATCH +history chathistory #room");
        transport.EnqueueInboundLine("@batch=history;msgid=join-1;time=2026-09-07T10:00:00.000Z :bob!u@h JOIN #room");
        transport.EnqueueInboundLine(":srv BATCH -history");

        var result = await pending;
        Assert.True(result.Succeeded);
        Assert.Empty(result.Messages);
        Assert.Empty(session.Snapshot.Channels);
        await session.DisconnectAsync();
        await run;
    }

    private static async Task<(ServerSession Session, FakeIrcTransport Transport, Task Run)> StartRegisteredAsync(
        bool includeEventPlayback,
        bool advertiseEventPlayback = false,
        bool ackEventPlayback = true)
    {
        var endpoint = new IrcEndpoint("history-discovery.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        IReadOnlyList<string> requested = includeEventPlayback ? [IrcCapabilityCatalog.Chathistory, IrcCapabilityCatalog.EventPlayback] : [IrcCapabilityCatalog.Chathistory];
        var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            RequestedCapabilities = requested,
            ChathistoryRequestTimeout = TimeSpan.FromSeconds(1),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        var advertised = advertiseEventPlayback ? " draft/event-playback" : string.Empty;
        transport.EnqueueInboundLine($":srv CAP * LS :batch draft/chathistory message-tags server-time{advertised}");
        var acknowledged = ackEventPlayback && includeEventPlayback ? " draft/event-playback" : string.Empty;
        transport.EnqueueInboundLine($":srv CAP * ACK :batch draft/chathistory message-tags server-time{acknowledged}");
        transport.EnqueueInboundLine(":srv 005 nex CHATHISTORY=50 MSGREFTYPES=msgid,timestamp CHANTYPES=# PREFIX=(ov)@+ CHANMODES=be,k,l,imnpst :supported");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);
        return (session, transport, run);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 1Y condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
