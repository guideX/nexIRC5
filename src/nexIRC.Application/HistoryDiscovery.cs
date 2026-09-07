using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Application;

/// <summary>
/// A TARGETS row is discovery metadata, not proof of a permanent person
/// identity.  Keeping it typed prevents the protocol row from becoming a
/// workspace tab or a participant mutation by accident.
/// </summary>
public sealed record HistoryTargetCandidate(
    Guid NetworkId,
    int ConnectionGeneration,
    string Target,
    DateTimeOffset LatestTimestamp,
    ChathistoryTargetKind Kind);

public enum QueryContinuityOutcome
{
    ReuseExistingQuery,
    CreateRecoveredQuery,
    LeaveAsCandidate,
    Reject
}

public sealed record QueryContinuityEvidence(
    bool SameNetwork,
    bool QueryWasOpenBeforeDisconnect,
    bool CurrentTargetMatches,
    bool AccountConsistent = false,
    bool CurrentSessionMessageObserved = false,
    bool TargetIsValidQuery = true);

/// <summary>
/// Conservative query identity policy.  A nickname string alone is never
/// enough to reuse an old query.  A known open query plus a current-session
/// target match is sufficient for this bounded reconnect window; account
/// evidence or a live message strengthens the same result.  An unknown valid
/// query target becomes a recovered conversation without merging into another
/// identity.
/// </summary>
public static class QueryContinuityPolicy
{
    public static QueryContinuityOutcome Evaluate(
        HistoryTargetCandidate candidate,
        QueryContinuityEvidence evidence,
        bool existingQuery)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evidence);
        if (candidate.Kind != ChathistoryTargetKind.Query || !evidence.TargetIsValidQuery || string.IsNullOrWhiteSpace(candidate.Target))
        {
            return QueryContinuityOutcome.Reject;
        }

        if (existingQuery
            && evidence.SameNetwork
            && evidence.QueryWasOpenBeforeDisconnect
            && evidence.CurrentTargetMatches
            && (evidence.AccountConsistent || evidence.CurrentSessionMessageObserved || evidence.CurrentTargetMatches))
        {
            return QueryContinuityOutcome.ReuseExistingQuery;
        }

        if (!existingQuery && evidence.SameNetwork && evidence.TargetIsValidQuery)
        {
            return QueryContinuityOutcome.CreateRecoveredQuery;
        }

        return QueryContinuityOutcome.LeaveAsCandidate;
    }
}
