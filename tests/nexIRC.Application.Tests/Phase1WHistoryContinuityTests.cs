using nexIRC.Application;

namespace nexIRC.Application.Tests;

public sealed class Phase1WHistoryContinuityTests
{
    [Fact]
    public async Task ServerMessageIdReplayIsExactAndNetworkScoped()
    {
        await using var store = new InMemoryConversationLogStore();
        var first = Record(Guid.NewGuid(), DateTimeOffset.UtcNow, "one") with { ServerMessageId = "m-1" };
        var otherNetwork = first with { NetworkId = Guid.NewGuid(), Text = "same id on another network" };

        Assert.True(await store.AppendAsync(first));
        Assert.True(await store.AppendAsync(first with { Text = "replayed" }));
        Assert.True(await store.AppendAsync(otherNetwork));

        Assert.Equal(2, store.Records.Count);
        Assert.Contains(store.Records, item => item.NetworkId == first.NetworkId && item.Text == "one");
        Assert.Contains(store.Records, item => item.NetworkId == otherNetwork.NetworkId);
    }

    [Fact]
    public async Task RepeatedNoIdMessagesAndDifferentKindsRemainDistinct()
    {
        await using var store = new InMemoryConversationLogStore();
        var baseRecord = Record(Guid.NewGuid(), DateTimeOffset.UtcNow, "repeat");
        await store.AppendAsync(baseRecord);
        await store.AppendAsync(baseRecord with { MessageKind = LogMessageKind.Action });
        await store.AppendAsync(baseRecord with { MessageKind = LogMessageKind.Notice });
        await store.AppendAsync(baseRecord);

        Assert.Equal(4, store.Records.Count);
    }

    [Fact]
    public async Task EqualTimeRecordsUseDurableSequenceAndOlderPlaybackMergesChronologically()
    {
        await using var store = new InMemoryConversationLogStore();
        var now = DateTimeOffset.UtcNow;
        var live = Record(Guid.NewGuid(), now, "live");
        var equal = Record(live.NetworkId, now, "equal");
        var older = Record(live.NetworkId, now.AddMinutes(-5), "older") with
        {
            Provenance = ConversationEntryProvenance.ServerPlayback
        };
        await store.AppendAsync(live);
        await store.AppendAsync(equal);
        await store.AppendAsync(older);

        var page = await store.ReadPageWindowAsync(Request(live.ScopeId, 10));
        Assert.Equal(["equal", "live", "older"], page.Records.Select(item => item.Text));
        Assert.True(page.Records[0].DurableSequence > page.Records[1].DurableSequence);

        var merged = ConversationHistoryMerge.Merge(
            [live],
            [ConversationEntryCandidate.FromServerPlayback(older)]);
        Assert.Equal(["older", "live"], merged.Select(item => item.Text));
    }

    [Fact]
    public void TimestampAndBodyAreNotAnIdentityAndOrderingHasAnExplicitFallback()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var first = Record(Guid.NewGuid(), timestamp, "same");
        var second = first with { Sender = "other" };

        Assert.False(ConversationEntryIdentity.IsExactServerDuplicate(first, second));
        Assert.NotEqual(0, ConversationHistoryOrdering.Compare(first, second));
        Assert.Equal(2, ConversationHistoryMerge.DeduplicateExact([first, second]).Count);
    }

    [Fact]
    public void ProvenanceControlsSideEffectsButNotConversationIdentity()
    {
        var record = Record(Guid.NewGuid(), DateTimeOffset.UtcNow, "history");
        var local = ConversationEntryCandidate.FromLocalHistory(record);
        var playback = ConversationEntryCandidate.FromServerPlayback(record with { ServerMessageId = "server-1" });
        var live = ConversationEntryCandidate.FromLive(record);

        Assert.False(local.Persist);
        Assert.False(local.TriggerLiveSideEffects);
        Assert.False(playback.TriggerLiveSideEffects);
        Assert.True(live.Persist);
        Assert.True(ConversationHistoryMerge.ShouldTriggerLiveSideEffects(live));
        Assert.False(ConversationHistoryMerge.ShouldTriggerLiveSideEffects(playback));
        Assert.Equal(record.ConversationKey, playback.Record.ConversationKey);
    }

    [Fact]
    public async Task JsonlHistoryKeepsSchemaCompatibleFieldsSequenceAndMalformedLinesBounded()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1w-jsonl-");
        try
        {
            var record = Record(Guid.NewGuid(), DateTimeOffset.UtcNow, "server-time") with
            {
                ServerMessageId = "stable-1",
                TimestampSource = ConversationTimestampSource.ServerTime,
                Provenance = ConversationEntryProvenance.ServerPlayback,
                BatchId = "history-batch"
            };
            await using (var store = new JsonlConversationLogStore(directory.FullName))
            {
                await store.AppendAsync(record);
                await store.AppendAsync(record);
                await store.FlushAsync();
            }

            var path = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
            await File.AppendAllTextAsync(path, "{truncated\n");
            await using var reopened = new JsonlConversationLogStore(directory.FullName);
            var page = await reopened.ReadPageWindowAsync(Request(record.ScopeId, 10));

            var loaded = Assert.Single(page.Records);
            Assert.Equal(ConversationHistorySchema.CurrentVersion, loaded.SchemaVersion);
            Assert.Equal(record.ServerMessageId, loaded.ServerMessageId);
            Assert.Equal(ConversationTimestampSource.ServerTime, loaded.TimestampSource);
            Assert.Equal(ConversationEntryProvenance.ServerPlayback, loaded.Provenance);
            Assert.True(loaded.DurableSequence > 0);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task SearchHidesExactIdDuplicatesButKeepsNoIdRepeats()
    {
        await using var store = new InMemoryConversationLogStore();
        var scope = Guid.NewGuid();
        await store.AppendAsync(Record(scope, DateTimeOffset.UtcNow, "needle") with { ServerMessageId = "id" });
        await store.AppendAsync(Record(scope, DateTimeOffset.UtcNow.AddSeconds(1), "needle"));
        await store.AppendAsync(Record(scope, DateTimeOffset.UtcNow.AddSeconds(2), "needle"));

        var page = await store.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = scope,
            NetworkId = scope,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            Text = "needle"
        });

        Assert.Equal(3, page.Results.Count);
        Assert.Equal(3, page.Statistics.MatchingRecords);
    }

    [Fact]
    public async Task DeterministicHistoryVolumeReadRemainsBounded()
    {
        await using var store = new InMemoryConversationLogStore();
        var scope = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        for (var index = 0; index < 2_000; index++)
        {
            await store.AppendAsync(Record(scope, start.AddSeconds(index), $"volume-{index}"));
        }

        var page = await store.ReadPageWindowAsync(Request(scope, 100));
        Assert.Equal(100, page.Records.Count);
        Assert.Equal("volume-1999", page.Records[0].Text);
        Assert.True(page.HasOlder);
    }

    private static ConversationLogRecord Record(Guid scope, DateTimeOffset timestamp, string text) => new()
    {
        Timestamp = timestamp,
        NetworkId = scope,
        ScopeId = scope,
        ProfileId = scope,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#room"),
        Sender = "Mira",
        MessageKind = LogMessageKind.Message,
        Direction = LogDirection.Incoming,
        Text = text
    };

    private static HistoryPageRequest Request(Guid scope, int pageSize) => new()
    {
        ScopeId = scope,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#room"),
        PageSize = pageSize
    };
}
