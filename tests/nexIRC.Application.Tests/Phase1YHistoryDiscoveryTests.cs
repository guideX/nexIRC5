using nexIRC.Application;
using nexIRC.Core.Protocol;

namespace nexIRC.Application.Tests;

public sealed class Phase1YHistoryDiscoveryTests
{
    [Fact]
    public void QueryContinuityRequiresReconnectEvidenceAndNetworkScope()
    {
        var candidate = new HistoryTargetCandidate(
            Guid.NewGuid(),
            2,
            "Alice",
            DateTimeOffset.UtcNow,
            ChathistoryTargetKind.Query);

        Assert.Equal(
            QueryContinuityOutcome.ReuseExistingQuery,
            QueryContinuityPolicy.Evaluate(candidate, new QueryContinuityEvidence(true, true, true), existingQuery: true));
        Assert.Equal(
            QueryContinuityOutcome.LeaveAsCandidate,
            QueryContinuityPolicy.Evaluate(candidate, new QueryContinuityEvidence(true, false, true), existingQuery: true));
        Assert.Equal(
            QueryContinuityOutcome.LeaveAsCandidate,
            QueryContinuityPolicy.Evaluate(candidate, new QueryContinuityEvidence(false, true, true), existingQuery: true));
        Assert.Equal(
            QueryContinuityOutcome.CreateRecoveredQuery,
            QueryContinuityPolicy.Evaluate(candidate, new QueryContinuityEvidence(true, false, false), existingQuery: false));
    }

    [Fact]
    public void ServerPlaybackCandidateNeverTriggersLiveSideEffects()
    {
        var record = new ConversationLogRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            NetworkId = Guid.NewGuid(),
            ScopeId = Guid.NewGuid(),
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            ConversationKey = "Channel:#room",
            MessageKind = LogMessageKind.Join,
            Text = "joined #room"
        };

        Assert.False(ConversationHistoryMerge.ShouldTriggerLiveSideEffects(ConversationEntryCandidate.FromServerPlayback(record)));
    }
}
