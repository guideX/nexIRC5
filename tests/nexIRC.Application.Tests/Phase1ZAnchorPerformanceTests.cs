using System.Diagnostics;

namespace nexIRC.Application.Tests;

public sealed class Phase1ZAnchorPerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task BoundedAnchorLookupReportIsDeterministicWhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEXIRC_RUN_HISTORY_ANCHOR_PERFORMANCE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        const int recordCount = 12_000;
        var root = Directory.CreateTempSubdirectory("nexirc-phase1z-anchor-perf-");
        try
        {
            var scope = Guid.Parse("00000000-0000-0000-0000-000000000301");
            var network = Guid.Parse("00000000-0000-0000-0000-000000000302");
            var start = DateTimeOffset.UnixEpoch;
            await using var store = new JsonlConversationLogStore(root.FullName);
            for (var index = 0; index < recordCount; index++)
            {
                await store.AppendAsync(new ConversationLogRecord
                {
                    Timestamp = start.AddSeconds(index),
                    NetworkId = network,
                    ScopeId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#anchor-perf",
                    ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#anchor-perf"),
                    Sender = index % 2 == 0 ? "Alice" : "Bob",
                    MessageKind = LogMessageKind.Message,
                    Direction = LogDirection.Incoming,
                    Text = $"anchor-performance-{index:00000}",
                    ServerMessageId = $"perf-{index:00000}"
                });
            }

            await store.FlushAsync();
            var address = new HistoryConversationAddress
            {
                NetworkId = network,
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#anchor-perf",
                ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#anchor-perf")
            };

            var cold = Stopwatch.StartNew();
            var exact = await store.FindByServerMessageIdAsync(new HistoryServerMessageAnchorRequest
            {
                Conversation = address,
                ServerMessageId = "perf-06000"
            });
            cold.Stop();

            var timestamp = Stopwatch.StartNew();
            var nearest = await store.FindByTimestampAsync(new HistoryTimestampAnchorRequest
            {
                Conversation = address,
                Timestamp = start.AddSeconds(6_000).AddMilliseconds(500),
                Direction = HistoryAnchorDirection.Around
            });
            timestamp.Stop();

            var context = Stopwatch.StartNew();
            var around = await store.ReadContextAroundAsync(new HistoryContextRequest
            {
                Conversation = address,
                ServerMessageId = "perf-06000",
                BeforeCount = 25,
                AfterCount = 25
            });
            context.Stop();

            var warm = Stopwatch.StartNew();
            var repeat = await store.FindByServerMessageIdAsync(new HistoryServerMessageAnchorRequest
            {
                Conversation = address,
                ServerMessageId = "perf-06000"
            });
            warm.Stop();

            var indexPath = Directory.EnumerateFiles(root.FullName, "*.hidx", SearchOption.AllDirectories).Single();
            Console.WriteLine(
                $"history-anchors records={recordCount} index_entries={store.HistoryIndexCount} index_bytes={new FileInfo(indexPath).Length} " +
                $"cold_msgid_ms={cold.ElapsedMilliseconds} warm_msgid_ms={warm.ElapsedMilliseconds} " +
                $"timestamp_ms={timestamp.ElapsedMilliseconds} context_ms={context.ElapsedMilliseconds} " +
                $"context_records={around.Records.Count} exact={exact.Anchor?.Record.ServerMessageId} nearest={nearest.Anchor?.Record.ServerMessageId} repeat={repeat.Anchor?.Record.ServerMessageId}");

            Assert.Equal("perf-06000", exact.Anchor?.Record.ServerMessageId);
            Assert.Equal("perf-06000", repeat.Anchor?.Record.ServerMessageId);
            Assert.Equal(51, around.Records.Count);
            Assert.True(nearest.Found);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }
}
