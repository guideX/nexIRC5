using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using nexIRC.Application;

namespace nexIRC.Application.Tests;

public sealed class HistoryPerformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    [Fact]
    [Trait("Category", "Performance")]
    public async Task JsonlHistoryPerformanceReportIsDeterministicWhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEXIRC_RUN_HISTORY_PERFORMANCE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var sizes = new[] { 1_000, 10_000, 100_000, 250_000, 500_000, 1_000_000 };
        foreach (var size in sizes)
        {
            var directory = Directory.CreateTempSubdirectory($"nexirc-history-perf-{size}-");
            try
            {
                var scope = Guid.Parse("00000000-0000-0000-0000-000000000001");
                var split = size > 500_000;
                string[] conversationNames = split ? ["#performance-0", "#performance-1"] : ["#performance"];
                var remaining = size;
                for (var file = 0; file < conversationNames.Length; file++)
                {
                    var count = split ? size / conversationNames.Length : remaining;
                    var path = GetPath(directory.FullName, scope, LogConversationKind.Channel, conversationNames[file]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await WriteDatasetAsync(path, scope, count, conversationNames[file], file * count, includeMarkers: true);
                    remaining -= count;
                }

                await using var store = new JsonlConversationLogStore(directory.FullName);
                var request = new HistoryPageRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = conversationNames[0],
                    PageSize = 100
                };
                var measurements = new List<Measurement>();
                await MeasureAsync("cold-newest", () => store.ReadPageWindowAsync(request), measurements);
                await MeasureAsync("warm-newest", () => store.ReadPageWindowAsync(request), measurements);
                var newest = await store.ReadPageWindowAsync(request);
                await MeasureAsync("oldest", () => store.ReadPageWindowAsync(request with { Oldest = true }), measurements);
                var oldest = await store.ReadPageWindowAsync(request with { Oldest = true });
                await MeasureAsync("previous-page", () => store.ReadPageWindowAsync(request with { Before = newest.OldestTimestamp }), measurements);
                await MeasureAsync("next-page", () => store.ReadPageWindowAsync(request with { After = oldest.NewestTimestamp }), measurements);
                var around = DateTimeOffset.UnixEpoch.AddMinutes(Math.Max(1, size / 2));
                await MeasureAsync("date-seek", () => store.ReadPageWindowAsync(request with { Around = around }), measurements);
                await MeasureAsync("bounded-export", () => store.ReadRangeAsync(new HistoryExportRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = conversationNames[0],
                    MaximumRecords = 500
                }), measurements);

                await MeasureSearchAsync("search-early", store, Search(scope, conversationNames[0], "early-marker"), measurements);
                await MeasureSearchAsync("search-middle", store, Search(scope, conversationNames[0], "middle-marker"), measurements);
                await MeasureSearchAsync("search-late", store, Search(scope, conversationNames[0], "late-marker"), measurements);
                await MeasureSearchAsync("search-none", store, Search(scope, conversationNames[0], "no-such-term"), measurements);
                await MeasureSearchAsync("search-common", store, Search(scope, null, "benchmark"), measurements);
                await MeasureSearchAsync("search-narrow-date", store, Search(scope, null, "benchmark") with
                {
                    From = DateTimeOffset.UnixEpoch.AddMinutes(size / 3),
                    To = DateTimeOffset.UnixEpoch.AddMinutes(size / 3 + 100)
                }, measurements);
                await MeasureSearchAsync("search-broad-date", store, Search(scope, null, "benchmark") with
                {
                    From = DateTimeOffset.UnixEpoch,
                    To = DateTimeOffset.UnixEpoch.AddMinutes(size)
                }, measurements);
                await MeasureSearchAsync("search-current-conversation", store, Search(scope, conversationNames[0], "middle-marker") with
                {
                    Scope = ConversationLogSearchScope.CurrentConversation,
                    HistoryScopeId = scope
                }, measurements);
                await MeasureSearchAsync("search-current-network", store, Search(scope, null, "middle-marker") with
                {
                    Scope = ConversationLogSearchScope.CurrentNetwork,
                    HistoryScopeId = scope
                }, measurements);
                await MeasureSearchAsync("search-workspace-global", store, Search(null, null, "middle-marker") with
                {
                    Scope = ConversationLogSearchScope.AllHistory
                }, measurements);
                if (split)
                {
                    await MeasureSearchAsync("search-cross-file", store, Search(null, null, "benchmark") with
                    {
                        Scope = ConversationLogSearchScope.AllHistory
                    }, measurements);
                }

                var fileBytes = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
                Console.WriteLine($"history-performance size={size} layout={(split ? "split" : "single")} files={conversationNames.Length} fileBytes={fileBytes} "
                    + string.Join(' ', measurements.Select(item => $"{item.Name}={item.Milliseconds}ms/{item.Count}/examined{item.RecordsExamined}/files{item.FilesExamined}/matches{item.MatchingRecords}/skipped{item.RecordsSkippedByIndex}/index{item.IndexFilesUsed}/built{item.IndexFilesBuilt}/build{item.IndexBuildMilliseconds}ms")));
            }
            finally
            {
                Directory.Delete(directory.FullName, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonlHistoryIndexRebuildsAfterCorruptionAndTruncatedTail()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-history-index-");
        try
        {
            var scope = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var path = GetPath(directory.FullName, scope, LogConversationKind.Channel, "#index");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteDatasetAsync(path, scope, 4_000, "#index");

            HistoryPage page;
            await using (var store = new JsonlConversationLogStore(directory.FullName))
            {
                page = await store.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#index",
                    PageSize = 25
                });
            }

            var sidecarPath = $"{path}.hidx";
            Assert.True(File.Exists(sidecarPath));
            Assert.Equal(25, page.Records.Count);
            Assert.StartsWith("message-3999", page.Records[0].Text, StringComparison.Ordinal);

            await using (var indexedStore = new JsonlConversationLogStore(directory.FullName))
            {
                var oldest = await indexedStore.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#index",
                    PageSize = 25,
                    Oldest = true
                });
                Assert.StartsWith("message-0", oldest.Records[^1].Text, StringComparison.Ordinal);

                var older = await indexedStore.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#index",
                    PageSize = 25,
                    Before = page.OldestTimestamp
                });
                Assert.StartsWith("message-3974", older.Records[0].Text, StringComparison.Ordinal);

                var around = await indexedStore.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#index",
                    PageSize = 25,
                    Around = DateTimeOffset.UnixEpoch.AddMinutes(2_000),
                    AroundWindow = TimeSpan.FromMinutes(1)
                });
                Assert.Contains(around.Records, record => record.Text.StartsWith("message-2000", StringComparison.Ordinal));

                var exported = await indexedStore.ReadRangeAsync(new HistoryExportRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#index",
                    From = DateTimeOffset.UnixEpoch.AddMinutes(100),
                    To = DateTimeOffset.UnixEpoch.AddMinutes(200),
                    MaximumRecords = 7
                });
                Assert.Equal(7, exported.Records.Count);
                Assert.StartsWith("message-100", exported.Records[0].Text, StringComparison.Ordinal);
                Assert.True(exported.IsTruncated);
            }

            await File.WriteAllTextAsync(sidecarPath, "corrupt index");
            await File.AppendAllTextAsync(path, "{\"timestamp\":\"broken");

            await using var recovered = new JsonlConversationLogStore(directory.FullName);
            var rebuilt = await recovered.ReadPageWindowAsync(new HistoryPageRequest
            {
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#index",
                PageSize = 25
            });
            Assert.Equal(25, rebuilt.Records.Count);
            Assert.StartsWith("message-3999", rebuilt.Records[0].Text, StringComparison.Ordinal);
            Assert.True(new FileInfo(sidecarPath).Length > "corrupt index".Length);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static ConversationLogQuery Search(Guid? scope, string? conversationName, string text) => new()
    {
        HistoryScopeId = scope,
        NetworkId = scope,
        ConversationKind = conversationName is null ? null : LogConversationKind.Channel,
        ConversationName = conversationName,
        Text = text,
        MaximumResults = 10
    };

    private static async Task MeasureSearchAsync(
        string name,
        JsonlConversationLogStore store,
        ConversationLogQuery query,
        List<Measurement> measurements)
    {
        var stopwatch = Stopwatch.StartNew();
        var page = await store.SearchDetailedAsync(query);
        stopwatch.Stop();
        measurements.Add(new Measurement(
            name,
            stopwatch.ElapsedMilliseconds,
            page.Results.Count,
            page.Statistics.RecordsExamined,
            page.Statistics.FilesExamined,
            page.Statistics.MatchingRecords,
            page.Statistics.RecordsSkippedByIndex,
            page.Statistics.IndexFilesUsed,
            page.Statistics.IndexBuildMilliseconds,
            page.Statistics.IndexFilesBuilt));
    }

    private static async Task MeasureAsync<T>(string name, Func<ValueTask<T>> operation, List<Measurement> measurements)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await operation();
        stopwatch.Stop();
        var count = result switch
        {
            HistoryPage page => page.Records.Count,
            ConversationHistoryRange range => range.Records.Count,
            _ => 0
        };
        measurements.Add(new Measurement(name, stopwatch.ElapsedMilliseconds, count, 0, 0, 0));
    }

    private static async Task WriteDatasetAsync(string path, Guid scope, int count, string conversationName, int startIndex = 0, bool includeMarkers = false)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        for (var index = 0; index < count; index++)
        {
            var globalIndex = startIndex + index;
            var text = includeMarkers && globalIndex == startIndex
                ? "early-marker benchmark"
                : includeMarkers && globalIndex == startIndex + count / 2
                    ? "middle-marker benchmark"
                    : includeMarkers && globalIndex == startIndex + count - 1
                        ? "late-marker benchmark"
                        : $"message-{globalIndex} deterministic benchmark payload";
            var record = new ConversationLogRecord
            {
                Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(globalIndex),
                NetworkId = scope,
                ScopeId = scope,
                ProfileId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = conversationName,
                ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversationName),
                Sender = globalIndex % 2 == 0 ? "Mira" : "Rook",
                MessageKind = LogMessageKind.Message,
                Direction = LogDirection.Incoming,
                Text = text
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            await stream.WriteAsync(json);
            await stream.WriteAsync(NewLine);
        }

        await stream.FlushAsync();
    }

    private static string GetPath(string root, Guid scope, LogConversationKind kind, string name)
    {
        var key = ConversationLoggingService.BuildConversationKey(kind, name);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(root, scope.ToString("N"), $"{hash}.jsonl");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record Measurement(
        string Name,
        long Milliseconds,
        int Count,
        long RecordsExamined,
        int FilesExamined,
        long MatchingRecords,
        long RecordsSkippedByIndex = 0,
        int IndexFilesUsed = 0,
        long IndexBuildMilliseconds = 0,
        int IndexFilesBuilt = 0);
}
