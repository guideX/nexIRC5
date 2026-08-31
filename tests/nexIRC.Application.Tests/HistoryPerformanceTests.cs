using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class HistoryPerformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    [Trait("Category", "Performance")]
    public async Task JsonlHistoryPerformanceReportIsDeterministicWhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEXIRC_RUN_HISTORY_PERFORMANCE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var sizes = new[] { 1_000, 10_000, 100_000, 250_000 };
        foreach (var size in sizes)
        {
            var directory = Directory.CreateTempSubdirectory($"nexirc-history-perf-{size}-");
            try
            {
                var scope = Guid.Parse("00000000-0000-0000-0000-000000000001");
                var path = GetPath(directory.FullName, scope, LogConversationKind.Channel, "#performance");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await WriteDatasetAsync(path, scope, size);
                await using var store = new JsonlConversationLogStore(directory.FullName);
                var factory = new FakeIrcTransportFactory();
                var transport = new FakeIrcTransport(new IrcEndpoint($"history-{size}.invalid", 6667, false));
                factory.Add(transport);
                await using var manager = new NetworkSessionManager(factory);
                var workspace = manager.Add(new NetworkConnectionOptions
                {
                    DisplayName = "History performance",
                    Endpoint = transport.Endpoint,
                    Nickname = "perf",
                    Username = "perf",
                    RequestedCapabilities = Array.Empty<string>(),
                    Reconnect = new ReconnectPolicy(Enabled: false)
                });
                var historical = manager.OpenHistoricalConversation(workspace.Id, DestinationKind.Channel, "#performance");
                manager.CloseView(historical.Id);
                var request = new HistoryPageRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#performance",
                    PageSize = 100
                };
                var search = new ConversationLogQuery
                {
                    NetworkId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#performance",
                    Text = $"message-{size / 2}",
                    MaximumResults = 10
                };
                var around = DateTimeOffset.UnixEpoch.AddMinutes(size / 2);
                var measurements = new List<(string Name, long Milliseconds, int Count)>();
                await MeasureAsync("newest", () => store.ReadPageWindowAsync(request), measurements);
                await MeasureAsync("oldest", () => store.ReadPageWindowAsync(request with { Oldest = true }), measurements);
                var newest = await store.ReadPageWindowAsync(request);
                await MeasureAsync("older", () => store.ReadPageWindowAsync(request with { Before = newest.OldestTimestamp }), measurements);
                await MeasureAsync("newer", () => store.ReadPageWindowAsync(request with { After = around }), measurements);
                await MeasureAsync("around/jump-date", () => store.ReadPageWindowAsync(request with { Around = around }), measurements);
                await MeasureAsync("scoped-search", () => store.SearchAsync(search), measurements);
                await MeasureAsync("bounded-export", () => store.ReadRangeAsync(new HistoryExportRequest
                {
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#performance",
                    MaximumRecords = 500
                }), measurements);
                await MeasureAsync("reopen-historical", () => ValueTask.FromResult(manager.OpenHistoricalConversation(workspace.Id, DestinationKind.Channel, "#performance")), measurements);
                var page = await store.ReadPageWindowAsync(request);
                var fileSize = new FileInfo(path).Length;
                Console.WriteLine($"history-performance size={size} fileBytes={fileSize} "
                    + string.Join(' ', measurements.Select(item => $"{item.Name}={item.Milliseconds}ms/{item.Count}"))
                    + $" newestCount={page.Records.Count}");
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

    private static async Task MeasureAsync<T>(
        string name,
        Func<ValueTask<T>> operation,
        List<(string Name, long Milliseconds, int Count)> measurements)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await operation();
        stopwatch.Stop();
        var count = result switch
        {
            HistoryPage page => page.Records.Count,
            IReadOnlyCollection<ConversationLogSearchResult> results => results.Count,
            ConversationHistoryRange range => range.Records.Count,
            WorkspaceView => 1,
            _ => 0
        };
        measurements.Add((name, stopwatch.ElapsedMilliseconds, count));
    }

    private static async Task WriteDatasetAsync(string path, Guid scope, int count, string conversationName = "#performance")
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        for (var index = 0; index < count; index++)
        {
            var record = new ConversationLogRecord
            {
                Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(index),
                NetworkId = scope,
                ScopeId = scope,
                ProfileId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = conversationName,
                ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversationName),
                Sender = index % 2 == 0 ? "Mira" : "Rook",
                MessageKind = LogMessageKind.Message,
                Direction = LogDirection.Incoming,
                Text = $"message-{index} deterministic benchmark payload"
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            await stream.WriteAsync(json);
            await stream.WriteAsync("\n"u8.ToArray());
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
}
