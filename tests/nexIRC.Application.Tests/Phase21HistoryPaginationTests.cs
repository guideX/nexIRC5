using nexIRC.Core.Protocol;

namespace nexIRC.Application.Tests;

public sealed class Phase21HistoryPaginationTests
{
    [Fact]
    public void CoverageKeepsLocalWindowsAndRemoteExhaustionDistinct()
    {
        var network = Guid.NewGuid();
        var key = HistoryCoverageKey.Create(network, "Channel:#room");
        var ledger = new HistoryCoverageLedger();
        var first = Record(network, 1, "one");
        var second = Record(network, 2, "two");

        var snapshot = ledger.ObserveLocalPage(key, [first], hasOlder: true, hasNewer: true);
        snapshot = ledger.ObserveLocalPage(key, [second], hasOlder: false, hasNewer: true);

        Assert.Equal(2, snapshot.LocalWindows.Count);
        Assert.True(snapshot.LocalBeginningReached);
        Assert.False(snapshot.RemoteExhausted);
        Assert.Equal(HistoryCoverageState.LocalBeginningReached, snapshot.State);

        var anchor = Anchor(network, key.Conversation, second, generation: 1);
        Assert.True(ledger.TryBeginBackwardRequest(key, anchor, "1:msg-2", 1, out snapshot));
        snapshot = ledger.CompleteBackwardRequest(key, 1, [], explicitEnd: false, failed: false, "empty without end");

        Assert.True(snapshot.NoProgressTerminated);
        Assert.False(snapshot.RemoteExhausted);
        Assert.Equal(HistoryCoverageState.NoProgress, snapshot.State);
    }

    [Fact]
    public void CoverageCoalescesRequestsAdvancesOpaqueFrontierAndRecordsEnd()
    {
        var network = Guid.NewGuid();
        var key = HistoryCoverageKey.Create(network, "Channel:#room");
        var ledger = new HistoryCoverageLedger();
        var initial = Anchor(network, key.Conversation, Record(network, 20, "twenty"), 4);
        Assert.True(ledger.TryBeginBackwardRequest(key, initial, "4:msg-20", 4, out _));
        Assert.False(ledger.TryBeginBackwardRequest(key, initial, "4:msg-20", 4, out var coalesced));
        Assert.Equal(1, coalesced.CoalescedRequests);

        var older = Anchor(network, key.Conversation, Record(network, 10, "ten"), 4);
        var snapshot = ledger.CompleteBackwardRequest(key, 4, [older], explicitEnd: false, failed: false, "page", deduplicatedRows: 1);
        Assert.Equal("msg-10", snapshot.RemoteBackwardFrontier!.ServerMessageId);
        Assert.Equal(1, snapshot.RemoteRowsDeduplicated);
        Assert.Equal(HistoryCoverageState.RemoteMayExist, snapshot.State);

        Assert.True(ledger.TryBeginBackwardRequest(key, older, "4:msg-10", 4, out _));
        snapshot = ledger.CompleteBackwardRequest(key, 4, [], explicitEnd: true, failed: false, "server end");
        Assert.True(snapshot.RemoteExhausted);
        Assert.Equal(HistoryCoverageState.RemoteExhausted, snapshot.State);
        Assert.False(ledger.TryBeginBackwardRequest(key, older, "4:msg-10", 4, out _));
    }

    [Fact]
    public async Task IndexedLocalPagesAreNetworkScopedAndDoNotNeedRemoteSupport()
    {
        var network = Guid.NewGuid();
        var otherNetwork = Guid.NewGuid();
        var scope = network;
        await using var store = new InMemoryConversationLogStore();
        for (var index = 1; index <= 300; index++)
        {
            await store.AppendAsync(Record(network, index, $"room-{index}"));
        }

        await store.AppendAsync(Record(otherNetwork, 999, "other-network"));
        var newest = await store.ReadPageWindowAsync(new HistoryPageRequest
        {
            NetworkId = network,
            ScopeId = scope,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            ConversationKey = "Channel:#room",
            PageSize = 50
        });

        Assert.Equal(50, newest.Records.Count);
        Assert.Equal("room-300", newest.Records[0].Text);
        Assert.Equal("room-251", newest.Records[^1].Text);
        Assert.True(newest.HasOlder);
        Assert.DoesNotContain(newest.Records, record => record.NetworkId == otherNetwork);

        var older = await store.ReadPageWindowAsync(new HistoryPageRequest
        {
            NetworkId = network,
            ScopeId = scope,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            ConversationKey = "Channel:#room",
            PageSize = 50,
            Before = newest.OldestTimestamp,
            BeforeDurableSequence = newest.Records[^1].DurableSequence
        });

        Assert.Equal("room-250", older.Records[0].Text);
        Assert.Equal("room-201", older.Records[^1].Text);
    }

    private static ConversationLogRecord Record(Guid network, int number, string text) => new()
    {
        Timestamp = DateTimeOffset.UtcNow.Date.AddMinutes(number),
        NetworkId = network,
        ScopeId = network,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        ConversationKey = "Channel:#room",
        Sender = "alice",
        MessageKind = LogMessageKind.Message,
        Direction = LogDirection.Incoming,
        Text = text,
        ServerMessageId = $"msg-{number}",
        TimestampSource = ConversationTimestampSource.ServerTime,
        Provenance = ConversationEntryProvenance.Live
    };

    private static HistoryCoverageAnchor Anchor(Guid network, string conversation, ConversationLogRecord record, int generation) => new()
    {
        NetworkId = network,
        Conversation = conversation,
        Timestamp = record.Timestamp,
        ServerMessageId = record.ServerMessageId,
        Reference = ChathistoryReference.MessageId(record.ServerMessageId!),
        ConnectionGeneration = generation,
        Target = "#room",
        Provenance = HistoryCoverageProvenance.RequestResult
    };
}
