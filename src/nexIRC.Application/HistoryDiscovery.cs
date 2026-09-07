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
    bool TargetIsValidQuery = true,
    IdentityEvidenceMatch IdentityMatch = IdentityEvidenceMatch.NoMatch,
    int PlausibleExistingCandidates = 1);

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

        if (!evidence.SameNetwork
            || evidence.IdentityMatch is IdentityEvidenceMatch.ConflictingAccount or IdentityEvidenceMatch.ConflictingNetwork
            || evidence.PlausibleExistingCandidates < 1)
        {
            return QueryContinuityOutcome.LeaveAsCandidate;
        }

        var strongIdentity = evidence.IdentityMatch is IdentityEvidenceMatch.StrongAccount
            or IdentityEvidenceMatch.BoundedContinuity
            or IdentityEvidenceMatch.CurrentLiveNickname
            || evidence.AccountConsistent;
        var nicknameOnlyAmbiguous = evidence.IdentityMatch is IdentityEvidenceMatch.NicknameOnly
            && evidence.PlausibleExistingCandidates > 1;
        if (existingQuery
            && evidence.QueryWasOpenBeforeDisconnect
            && !nicknameOnlyAmbiguous
            && (strongIdentity
                || evidence.CurrentSessionMessageObserved
                || evidence.CurrentTargetMatches && evidence.PlausibleExistingCandidates == 1))
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
