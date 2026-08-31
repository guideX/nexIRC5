using nexIRC.Application;

namespace nexIRC.Application.Tests;

public sealed class Phase1JTests
{
    [Fact]
    public async Task RotationPreservesLogicalOrderingAcrossPagesAndExport()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1j-rotation-");
        try
        {
            var scope = Guid.Parse("00000000-0000-0000-0000-000000000101");
            await using var store = new JsonlConversationLogStore(directory.FullName, maximumSegmentBytes: 2_048);
            for (var index = 0; index < 40; index++)
            {
                Assert.True(await store.AppendAsync(Record(scope, scope, "#room", index, $"segment-message-{index}")));
            }

            await store.FlushAsync();
            var sources = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).ToArray();
            var archived = sources.Where(path => Path.GetFileNameWithoutExtension(path).Contains(".s", StringComparison.Ordinal)).ToArray();
            Assert.True(archived.Length >= 2, $"Expected rotation, found {sources.Length} source(s).");
            Assert.Contains(sources, path => Path.GetFileName(path).EndsWith(".jsonl", StringComparison.Ordinal));

            var newest = await store.ReadPageWindowAsync(Request(scope, pageSize: 7));
            Assert.Equal(7, newest.Records.Count);
            Assert.Equal("segment-message-39", newest.Records[0].Text);
            Assert.Equal("segment-message-33", newest.Records[^1].Text);
            Assert.True(newest.HasOlder);

            var older = await store.ReadPageWindowAsync(Request(scope, pageSize: 7) with { Before = newest.OldestTimestamp });
            Assert.Equal("segment-message-32", older.Records[0].Text);
            Assert.Equal("segment-message-26", older.Records[^1].Text);

            var oldest = await store.ReadPageWindowAsync(Request(scope, pageSize: 7) with { Oldest = true });
            Assert.Equal("segment-message-6", oldest.Records[0].Text);
            Assert.Equal("segment-message-0", oldest.Records[^1].Text);
            Assert.True(oldest.HasNewer);

            var around = await store.ReadPageWindowAsync(Request(scope, pageSize: 5) with
            {
                Around = DateTimeOffset.UnixEpoch.AddMinutes(20),
                AroundWindow = TimeSpan.FromMinutes(1)
            });
            Assert.Contains(around.Records, record => record.Text == "segment-message-20");

            var exported = await store.ReadRangeAsync(new HistoryExportRequest
            {
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#room",
                MaximumRecords = 10_000
            });
            Assert.False(exported.IsTruncated);
            Assert.Equal(40, exported.Records.Count);
            Assert.Equal("segment-message-0", exported.Records[0].Text);
            Assert.Equal("segment-message-39", exported.Records[^1].Text);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task SearchFansOutAcrossSegmentsAndRebuildsMissingSegmentSidecars()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1j-search-");
        try
        {
            var scope = Guid.Parse("00000000-0000-0000-0000-000000000102");
            await using var store = new JsonlConversationLogStore(directory.FullName, maximumSegmentBytes: 1_100_000);
            for (var index = 0; index < 12_000; index++)
            {
                var marker = index == 0 || index == 11_999 ? "segmented-marker" : "common-payload";
                Assert.True(await store.AppendAsync(Record(scope, scope, "#search", index, $"{marker}-{index:00000}")));
            }

            await store.FlushAsync();
            var sourcePaths = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).ToArray();
            Assert.True(sourcePaths.Length >= 2);

            var first = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                HistoryScopeId = scope,
                NetworkId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#search",
                Text = "segmented-marker",
                MaximumResults = 10
            });
            Assert.Equal(2, first.Results.Count);
            Assert.Equal(sourcePaths.Length, first.Statistics.FilesExamined);
            Assert.All(first.Results, result => Assert.NotNull(result.Location.SourceOffset));

            foreach (var sidecar in Directory.EnumerateFiles(directory.FullName, "*.hsidx", SearchOption.AllDirectories).ToArray())
            {
                File.Delete(sidecar);
            }

            var rebuilt = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Text = "segmented-marker",
                NetworkId = scope,
                MaximumResults = 10
            });
            Assert.Equal(2, rebuilt.Results.Count);
            Assert.True(rebuilt.Statistics.IndexFilesBuilt > 0);
            Assert.NotEmpty(Directory.EnumerateFiles(directory.FullName, "*.hsidx", SearchOption.AllDirectories));

            var corruptSidecar = Directory.EnumerateFiles(directory.FullName, "*.hsidx", SearchOption.AllDirectories).First();
            var corruptBytes = await File.ReadAllBytesAsync(corruptSidecar);
            corruptBytes[50] ^= 0x01;
            await File.WriteAllBytesAsync(corruptSidecar, corruptBytes);
            var recovered = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Text = "segmented-marker",
                NetworkId = scope,
                MaximumResults = 10
            });
            Assert.Equal(2, recovered.Results.Count);
            Assert.NotEqual(Convert.ToHexString(corruptBytes), Convert.ToHexString(await File.ReadAllBytesAsync(corruptSidecar)));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.SearchDetailedAsync(
                new ConversationLogQuery { Text = "segmented-marker", NetworkId = scope }, cancellation.Token));

            var broad = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Text = "common-payload",
                NetworkId = scope,
                MaximumResults = 3
            });
            Assert.Equal(3, broad.Results.Count);
            Assert.True(broad.Statistics.MatchingRecords > broad.Results.Count);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task RetentionDeletesWholeOldSegmentsRewritesBoundaryAndCleansSidecars()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1j-retention-");
        try
        {
            var scope = Guid.Parse("00000000-0000-0000-0000-000000000103");
            await using var store = new JsonlConversationLogStore(directory.FullName, maximumSegmentBytes: 1_100_000);
            for (var index = 0; index < 12_000; index++)
            {
                Assert.True(await store.AppendAsync(Record(scope, scope, "#retention", index, $"retention-{index}")));
            }

            await store.FlushAsync();
            _ = await store.SearchDetailedAsync(new ConversationLogQuery { Text = "retention", NetworkId = scope });
            Assert.NotEmpty(Directory.EnumerateFiles(directory.FullName, "*.hsidx", SearchOption.AllDirectories));

            var removed = await store.CleanupAsync(DateTimeOffset.UnixEpoch.AddMinutes(9_000));
            Assert.Equal(9_000, removed);
            Assert.True(store.LastCleanupStatistics.SegmentsExamined >= 2);
            Assert.True(store.LastCleanupStatistics.SegmentsDeleted > 0);
            Assert.True(store.LastCleanupStatistics.RecordsRemoved == removed);
            Assert.True(store.LastCleanupStatistics.BytesRead > 0);
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, "*.tmp", SearchOption.AllDirectories));

            var sourceBeforeCancelledCleanup = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => File.ReadAllBytes(path), StringComparer.OrdinalIgnoreCase);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.CleanupAsync(DateTimeOffset.UnixEpoch, cancellation.Token));
            Assert.All(sourceBeforeCancelledCleanup, item => Assert.Equal(item.Value, File.ReadAllBytes(item.Key)));

            var range = await store.ReadRangeAsync(new HistoryExportRequest
            {
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#retention",
                MaximumRecords = 10_000
            });
            Assert.Equal(3_000, range.Records.Count);
            Assert.Equal("retention-9000", range.Records[0].Text);
            Assert.Equal("retention-11999", range.Records[^1].Text);
            var remainingSources = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(Directory.EnumerateFiles(directory.FullName, "*.hsidx", SearchOption.AllDirectories), sidecar =>
                Assert.Contains(sidecar[..^".hsidx".Length], remainingSources, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task SegmentedInspectionDoesNotMutateLifecycleActivityOrDrafts()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1j-lifecycle-");
        try
        {
            var factory = new nexIRC.Networking.Testing.FakeIrcTransportFactory();
            var transport = new nexIRC.Networking.Testing.FakeIrcTransport(new nexIRC.Core.Networking.IrcEndpoint("phase1j.invalid", 6667, false));
            factory.Add(transport);
            await using var manager = new NetworkSessionManager(factory);
            var network = manager.Add(new NetworkConnectionOptions
            {
                DisplayName = "Phase 1J",
                Endpoint = transport.Endpoint,
                Nickname = "alice",
                Username = "alice",
                RequestedCapabilities = Array.Empty<string>(),
                Reconnect = new nexIRC.Core.Session.ReconnectPolicy(Enabled: false)
            });
            var channel = manager.EnsureChannel(network.Id, "#room");
            channel.MarkActivity(WorkspaceActivity.Important | WorkspaceActivity.Unread);
            var lifecycle = channel.LifecycleState;
            var activity = channel.Activity;
            var outboundBefore = transport.OutboundLines.Count;

            await using var store = new JsonlConversationLogStore(directory.FullName, maximumSegmentBytes: 1_024);
            for (var index = 0; index < 12; index++)
            {
                Assert.True(await store.AppendAsync(Record(network.Id, network.Id, "#room", index, $"lifecycle-{index}")));
            }

            await store.FlushAsync();
            _ = await store.ReadPageWindowAsync(Request(network.Id, 5));
            _ = await store.SearchDetailedAsync(new ConversationLogQuery { Text = "lifecycle", NetworkId = network.Id });
            _ = await store.CleanupAsync(DateTimeOffset.UnixEpoch.AddMinutes(4));

            Assert.Equal(lifecycle, channel.LifecycleState);
            Assert.Equal(activity, channel.Activity);
            Assert.Equal(outboundBefore, transport.OutboundLines.Count);
            Assert.Single(network.Channels);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    private static HistoryPageRequest Request(Guid scope, int pageSize) => new()
    {
        ScopeId = scope,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        PageSize = pageSize
    };

    private static ConversationLogRecord Record(Guid networkId, Guid scopeId, string name, int minute, string text) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(minute),
        NetworkId = networkId,
        ScopeId = scopeId,
        ProfileId = scopeId,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = name,
        ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, name),
        Sender = "Mira",
        MessageKind = LogMessageKind.Message,
        Direction = LogDirection.Incoming,
        Text = text
    };
}
