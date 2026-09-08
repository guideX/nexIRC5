using System.Security.Cryptography;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;

namespace nexIRC.Application.Tests;

public sealed class Phase24HistoryIntegrityTests
{
    [Fact]
    public void ExactGapResponseMustRetainImmutableGapOwnership()
    {
        var network = Guid.NewGuid();
        var conversation = "PrivateConversation:alice";
        var older = new HistoryGapBoundary
        {
            NetworkId = network,
            Conversation = conversation,
            Reference = ChathistoryReference.MessageId("b"),
            Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(1),
            ServerMessageId = "b",
            Provenance = HistoryGapBoundaryProvenance.PreDisconnectCanonical,
            ConnectionGeneration = 1
        };
        var newer = older with
        {
            Reference = ChathistoryReference.MessageId("f"),
            Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(5),
            ServerMessageId = "f",
            Provenance = HistoryGapBoundaryProvenance.PostReconnectCanonical
        };
        var gap = new HistoryGap
        {
            NetworkId = network,
            Conversation = conversation,
            Target = "Alicia",
            Older = older,
            Newer = newer,
            RepairConnectionGeneration = 1
        };
        var request = ChathistoryRequest.ForBetween(network, 1, conversation, "Alicia", older.Reference, newer.Reference, 50) with
        {
            GapKey = gap.Key
        };
        var accepted = HistoryGapPolicy.ValidateBatch(
            gap,
            new ChathistoryResult(1, request, ChathistoryRequestCompletion.Succeeded, Array.Empty<IrcSemanticEvent>())
            {
                BatchType = "chathistory",
                BatchTarget = "Alicia"
            });
        Assert.True(accepted.IsSafe);

        var rejected = HistoryGapPolicy.ValidateBatch(
            gap,
            new ChathistoryResult(1, request with { GapKey = "different-gap" }, ChathistoryRequestCompletion.Succeeded, Array.Empty<IrcSemanticEvent>())
            {
                BatchType = "chathistory",
                BatchTarget = "Alicia"
            });
        Assert.False(rejected.IsSafe);
    }

    [Fact]
    public async Task SameLengthPreservedMtimeReplacementRejectsStaleSearchIndex()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase24-fingerprint-");
        try
        {
            var scope = Guid.NewGuid();
            await using (var store = new JsonlConversationLogStore(directory.FullName))
            {
                for (var index = 0; index < 5_000; index++)
                {
                    var text = index == 4_500
                        ? "old-anchor" + new string('x', 420)
                        : $"filler-{index:00000} {new string('x', 420)}";
                    Assert.True(await store.AppendAsync(Record(scope, "#integrity", text, index + 1)));
                }

                await store.FlushAsync();
                var initial = await store.SearchDetailedAsync(new ConversationLogQuery
                {
                    NetworkId = scope,
                    Text = "old-anchor",
                    MaximumResults = 10
                });
                Assert.Single(initial.Results);
                Assert.Equal(1, initial.Statistics.IndexFilesBuilt);
            }

            var sourcePath = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
            var sourceBefore = await File.ReadAllBytesAsync(sourcePath);
            var originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
            var replacement = System.Text.Encoding.UTF8.GetString(sourceBefore).Replace("old-anchor", "new-anchor", StringComparison.Ordinal);
            Assert.Equal(sourceBefore.Length, System.Text.Encoding.UTF8.GetByteCount(replacement));
            await File.WriteAllTextAsync(sourcePath, replacement);
            File.SetLastWriteTimeUtc(sourcePath, originalWriteTime);

            await using var reopened = new JsonlConversationLogStore(directory.FullName);
            var oldSearch = await reopened.SearchDetailedAsync(new ConversationLogQuery
            {
                NetworkId = scope,
                Text = "old-anchor",
                MaximumResults = 10
            });
            Assert.Empty(oldSearch.Results);
            Assert.True(oldSearch.Statistics.IndexFilesBuilt > 0);
            Assert.Equal(sourceBefore.Length, new FileInfo(sourcePath).Length);

            var newSearch = await reopened.SearchDetailedAsync(new ConversationLogQuery
            {
                NetworkId = scope,
                Text = "new-anchor",
                MaximumResults = 10
            });
            Assert.Single(newSearch.Results);
            Assert.Equal("new-anchor" + new string('x', 420), newSearch.Results[0].Record.Text);
            Assert.Equal(0, newSearch.Statistics.IndexFilesBuilt);
            Assert.True(newSearch.Statistics.IndexFilesUsed > 0);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task OldSearchIndexVersionRebuildsWithoutChangingCanonicalJsonl()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase24-index-version-");
        try
        {
            var scope = Guid.NewGuid();
            await using (var store = new JsonlConversationLogStore(directory.FullName))
            {
                for (var index = 0; index < 4_500; index++)
                {
                    Assert.True(await store.AppendAsync(Record(scope, "#version", $"version-fixture-{index:00000} {new string('y', 420)}", index + 1)));
                }

                await store.FlushAsync();
                _ = await store.SearchDetailedAsync(new ConversationLogQuery { NetworkId = scope, Text = "version-fixture" });
            }

            var sourcePath = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
            var sidecarPath = $"{sourcePath}.hsidx";
            var sourceBefore = await File.ReadAllBytesAsync(sourcePath);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath);
            BitConverter.GetBytes(2).CopyTo(sidecar, 8);
            SHA256.HashData(sidecar.AsSpan(0, sidecar.Length - 32)).CopyTo(sidecar, sidecar.Length - 32);
            await File.WriteAllBytesAsync(sidecarPath, sidecar);

            await using var reopened = new JsonlConversationLogStore(directory.FullName);
            var rebuilt = await reopened.SearchDetailedAsync(new ConversationLogQuery
            {
                NetworkId = scope,
                Text = "version-fixture-04499"
            });
            Assert.Single(rebuilt.Results);
            Assert.True(rebuilt.Statistics.IndexFilesBuilt > 0);
            Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static ConversationLogRecord Record(Guid scope, string conversation, string text, long sequence) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(sequence),
        NetworkId = scope,
        ScopeId = scope,
        ProfileId = scope,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = conversation,
        ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversation),
        Sender = "Alice",
        MessageKind = LogMessageKind.Message,
        Direction = LogDirection.Incoming,
        Text = text,
        DurableSequence = sequence
    };
}
