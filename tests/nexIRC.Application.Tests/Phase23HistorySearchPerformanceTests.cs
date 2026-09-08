using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nexIRC.Application.Tests;

public sealed class Phase23HistorySearchPerformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    [Trait("Category", "Performance")]
    public async Task FiftyThousandCanonicalRowsProduceBoundedIndexedSearchMeasurements()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase23-50k-");
        try
        {
            var scope = Guid.NewGuid();
            var conversations = new[] { "#kernel", "#release", "#support", "#random" };
            var paths = conversations.Select(name =>
            {
                var key = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, name);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
                var path = Path.Combine(directory.FullName, scope.ToString("N"), $"{hash}.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return path;
            }).ToArray();

            await using (var stream = new FileStream(paths[0], FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream))
            {
                for (var index = 0; index < 50_000; index++)
                {
                    var conversationIndex = index % conversations.Length;
                    var conversation = conversations[conversationIndex];
                    var text = index == 49_123
                        ? "phase23-rare-unique benchmark payload"
                        : "benchmark kernel repeated payload with bounded deterministic fixture";
                    var record = new ConversationLogRecord
                    {
                        Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(index / 5),
                        NetworkId = Guid.Parse(index % 2 == 0 ? "00000000-0000-0000-0000-000000000001" : "00000000-0000-0000-0000-000000000002"),
                        ScopeId = scope,
                        ProfileId = scope,
                        ConversationKind = LogConversationKind.Channel,
                        ConversationName = conversation,
                        ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversation),
                        Sender = index % 2 == 0 ? "Alice" : "Bob",
                        MessageKind = LogMessageKind.Message,
                        Direction = LogDirection.Incoming,
                        Text = text,
                        DurableSequence = index + 1,
                        ServerMessageId = index % 7 == 0 ? $"fixture-{index}" : null
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
                }
            }

            // Split the single deterministic stream into the four canonical
            // conversation files without changing record content. The source
            // files are then independently indexed and remain network-safe.
            var allRecords = await File.ReadAllLinesAsync(paths[0]);
            File.Delete(paths[0]);
            var grouped = conversations.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var line in allRecords)
            {
                var record = JsonSerializer.Deserialize<ConversationLogRecord>(line, JsonOptions)!;
                grouped[record.ConversationName].Add(line);
            }

            foreach (var path in paths)
            {
                var conversation = conversations[Array.IndexOf(paths, path)];
                await File.WriteAllLinesAsync(path, grouped[conversation]);
            }

            await using var store = new JsonlConversationLogStore(directory.FullName);
            var coldWatch = Stopwatch.StartNew();
            var cold = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Text = "benchmark",
                MaximumResults = 100
            });
            coldWatch.Stop();

            var warmWatch = Stopwatch.StartNew();
            var rare = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Text = "phase23-rare-unique",
                MaximumResults = 10
            });
            warmWatch.Stop();
            var filtered = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Text = "benchmark",
                Sender = "alice",
                From = DateTimeOffset.UnixEpoch.AddMinutes(1_000),
                To = DateTimeOffset.UnixEpoch.AddMinutes(2_000),
                MaximumResults = 25
            });

            Assert.Equal(100, cold.Results.Count);
            Assert.True(cold.Statistics.ResultsTruncated);
            Assert.Equal(4, cold.Statistics.IndexFilesBuilt);
            Assert.Equal(50_000, cold.Statistics.MatchingRecords);
            Assert.Single(rare.Results);
            Assert.True(rare.Statistics.IndexFilesUsed >= 4);
            Assert.True(rare.Statistics.RecordsExamined < 50_000);
            Assert.True(rare.Statistics.RecordsSkippedByIndex > 0);
            Assert.InRange(filtered.Results.Count, 1, 25);
            Assert.All(filtered.Results, result => Assert.Equal("Alice", result.Sender));

            Console.WriteLine(
                $"phase23-performance records=50000 files=4 coldBuild={coldWatch.ElapsedMilliseconds}ms "
                + $"warmRare={warmWatch.ElapsedMilliseconds}ms commonExamined={cold.Statistics.RecordsExamined} "
                + $"rareExamined={rare.Statistics.RecordsExamined} rareSkipped={rare.Statistics.RecordsSkippedByIndex} "
                + $"filtered={filtered.Results.Count}");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
