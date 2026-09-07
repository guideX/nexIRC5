using nexIRC.Core.State;

namespace nexIRC.Application.Tests;

public sealed class Phase1ZIdentityAndAnchorTests
{
    [Fact]
    public void StrongAccountEvidenceWinsAcrossNickChangeButConflictsDoNotMerge()
    {
        var network = Guid.NewGuid();
        var ledger = new ConversationIdentityEvidenceLedger(network);
        ledger.Observe(Evidence(network, "Alice", "alice-account", IdentityEvidenceSource.LiveAccountTag));

        var strong = ConversationIdentityEvidencePolicy.Assess(
            network,
            "Alicia",
            "alice-account",
            ledger.Snapshot(),
            IrcCaseMapping.Rfc1459,
            currentNicknameOwnership: false,
            boundedContinuity: false);
        var conflict = ConversationIdentityEvidencePolicy.Assess(
            network,
            "Alice",
            "another-account",
            ledger.Snapshot(),
            IrcCaseMapping.Rfc1459,
            currentNicknameOwnership: true,
            boundedContinuity: false);

        Assert.Equal(IdentityEvidenceMatch.StrongAccount, strong);
        Assert.Equal(IdentityEvidenceMatch.ConflictingAccount, conflict);
        Assert.Equal(
            QueryContinuityOutcome.ReuseExistingQuery,
            QueryContinuityPolicy.Evaluate(
                new HistoryTargetCandidate(network, 2, "Alicia", DateTimeOffset.UtcNow, nexIRC.Core.Protocol.ChathistoryTargetKind.Query),
                new QueryContinuityEvidence(
                    SameNetwork: true,
                    QueryWasOpenBeforeDisconnect: true,
                    CurrentTargetMatches: false,
                    IdentityMatch: strong),
                existingQuery: true));
        Assert.Equal(
            QueryContinuityOutcome.LeaveAsCandidate,
            QueryContinuityPolicy.Evaluate(
                new HistoryTargetCandidate(network, 2, "Alice", DateTimeOffset.UtcNow, nexIRC.Core.Protocol.ChathistoryTargetKind.Query),
                new QueryContinuityEvidence(
                    SameNetwork: true,
                    QueryWasOpenBeforeDisconnect: true,
                    CurrentTargetMatches: true,
                    IdentityMatch: conflict),
                existingQuery: true));

        ledger.Observe(Evidence(network, "Alice", "another-account", IdentityEvidenceSource.LiveAccountCommand));
        var conflictingLedger = ConversationIdentityEvidencePolicy.Assess(
            network,
            "Alicia",
            "alice-account",
            ledger.Snapshot(),
            IrcCaseMapping.Rfc1459,
            currentNicknameOwnership: true,
            boundedContinuity: false);
        Assert.Equal(IdentityEvidenceMatch.ConflictingAccount, conflictingLedger);
    }

    [Fact]
    public void NicknameOnlyEvidenceStaysAmbiguousAndNetworksStayIsolated()
    {
        var network = Guid.NewGuid();
        var ledger = new ConversationIdentityEvidenceLedger(network);
        ledger.Observe(Evidence(network, "Alice", null, IdentityEvidenceSource.LivePrefix));

        var nicknameOnly = ConversationIdentityEvidencePolicy.Assess(
            network,
            "Alice",
            null,
            ledger.Snapshot(),
            IrcCaseMapping.Rfc1459,
            currentNicknameOwnership: false,
            boundedContinuity: false);
        var otherNetwork = ConversationIdentityEvidencePolicy.Assess(
            Guid.NewGuid(),
            "Alice",
            null,
            ledger.Snapshot(),
            IrcCaseMapping.Rfc1459,
            currentNicknameOwnership: true,
            boundedContinuity: false);

        Assert.Equal(IdentityEvidenceMatch.NicknameOnly, nicknameOnly);
        Assert.Equal(IdentityEvidenceMatch.ConflictingNetwork, otherNetwork);
        Assert.Equal(
            QueryContinuityOutcome.LeaveAsCandidate,
            QueryContinuityPolicy.Evaluate(
                new HistoryTargetCandidate(network, 2, "Alice", DateTimeOffset.UtcNow, nexIRC.Core.Protocol.ChathistoryTargetKind.Query),
                new QueryContinuityEvidence(
                    SameNetwork: true,
                    QueryWasOpenBeforeDisconnect: true,
                    CurrentTargetMatches: true,
                    IdentityMatch: nicknameOnly,
                    PlausibleExistingCandidates: 2),
                existingQuery: true));
    }

    [Fact]
    public async Task JsonlAnchorsAreNetworkAndConversationScopedAndRebuildAcrossRestart()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase1z-anchor-");
        try
        {
            var scope = Guid.NewGuid();
            var networkA = Guid.NewGuid();
            var networkB = Guid.NewGuid();
            var start = DateTimeOffset.UtcNow.AddMinutes(-10);
            await using (var store = new JsonlConversationLogStore(root.FullName))
            {
                await store.AppendAsync(Record(scope, networkA, start, "a-1", "alpha"));
                await store.AppendAsync(Record(scope, networkA, start, "a-2", "same-time"));
                await store.AppendAsync(Record(scope, networkB, start, "a-1", "beta"));
                await store.AppendAsync(Record(scope, networkA, start.AddMinutes(1), "a-3", "tail"));
                await store.FlushAsync();

                var path = Directory.EnumerateFiles(root.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
                var before = await File.ReadAllBytesAsync(path);
                var addressA = Address(scope, networkA);
                var exact = await store.FindByServerMessageIdAsync(new HistoryServerMessageAnchorRequest
                {
                    Conversation = addressA,
                    ServerMessageId = "a-1"
                });
                var otherNetwork = await store.FindByServerMessageIdAsync(new HistoryServerMessageAnchorRequest
                {
                    Conversation = Address(scope, networkB),
                    ServerMessageId = "a-1"
                });
                var beforeTimestamp = await store.FindByTimestampAsync(new HistoryTimestampAnchorRequest
                {
                    Conversation = addressA,
                    Timestamp = start,
                    Direction = HistoryAnchorDirection.AtOrBefore
                });
                var afterTimestamp = await store.FindByTimestampAsync(new HistoryTimestampAnchorRequest
                {
                    Conversation = addressA,
                    Timestamp = start,
                    Direction = HistoryAnchorDirection.AtOrAfter
                });
                var context = await store.ReadContextAroundAsync(new HistoryContextRequest
                {
                    Conversation = addressA,
                    ServerMessageId = "a-2",
                    BeforeCount = 1,
                    AfterCount = 1
                });

                Assert.Equal("alpha", exact.Anchor?.Record.Text);
                Assert.Equal("beta", otherNetwork.Anchor?.Record.Text);
                Assert.Equal("same-time", beforeTimestamp.Anchor?.Record.Text);
                Assert.Equal("alpha", afterTimestamp.Anchor?.Record.Text);
                Assert.Equal(["alpha", "same-time", "tail"], context.Records.Select(item => item.Text));
                Assert.True(File.Exists($"{path}.hidx"));
                Assert.Equal(before, await File.ReadAllBytesAsync(path));
            }

            await using var reopened = new JsonlConversationLogStore(root.FullName);
            var reopenedResult = await reopened.FindByServerMessageIdAsync(new HistoryServerMessageAnchorRequest
            {
                Conversation = Address(scope, networkA),
                ServerMessageId = "a-3"
            });
            Assert.Equal("tail", reopenedResult.Anchor?.Record.Text);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAnchorsAreTypedAndOverlappingContextDoesNotDuplicate()
    {
        await using var store = new InMemoryConversationLogStore();
        var scope = Guid.NewGuid();
        var network = Guid.NewGuid();
        var address = Address(scope, network);
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index < 5; index++)
        {
            await store.AppendAsync(Record(scope, network, start.AddSeconds(index), $"m-{index}", $"message-{index}"));
        }

        var missing = await store.FindByServerMessageIdAsync(new HistoryServerMessageAnchorRequest
        {
            Conversation = address,
            ServerMessageId = "not-present"
        });
        var context = await store.ReadContextAroundAsync(new HistoryContextRequest
        {
            Conversation = address,
            ServerMessageId = "m-2",
            BeforeCount = 10,
            AfterCount = 10
        });

        Assert.Equal(HistoryAnchorMatch.BoundedMiss, missing.Match);
        Assert.False(missing.Found);
        Assert.Equal(5, context.Records.Count);
        Assert.Equal(5, context.Records.Select(item => item.ServerMessageId).Distinct().Count());
        Assert.Equal(["message-0", "message-1", "message-2", "message-3", "message-4"], context.Records.Select(item => item.Text));
    }

    private static ConversationIdentityEvidence Evidence(
        Guid network,
        string nickname,
        string? account,
        IdentityEvidenceSource source) =>
        new(network, nickname, account, "user", "host", 1, DateTimeOffset.UtcNow, source);

    private static HistoryConversationAddress Address(Guid scope, Guid network) => new()
    {
        NetworkId = network,
        ScopeId = scope,
        ConversationKind = LogConversationKind.PrivateConversation,
        ConversationName = "Alice",
        ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.PrivateConversation, "Alice")
    };

    private static ConversationLogRecord Record(
        Guid scope,
        Guid network,
        DateTimeOffset timestamp,
        string messageId,
        string text) => new()
        {
            Timestamp = timestamp,
            NetworkId = network,
            ScopeId = scope,
            ConversationKind = LogConversationKind.PrivateConversation,
            ConversationName = "Alice",
            ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.PrivateConversation, "Alice"),
            Sender = "Alice",
            MessageKind = LogMessageKind.Message,
            Direction = LogDirection.Incoming,
            Text = text,
            ServerMessageId = messageId
        };
}
