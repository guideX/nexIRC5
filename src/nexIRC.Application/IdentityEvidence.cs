using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum IdentityEvidenceSource
{
    CurrentConversationView,
    LiveAccountTag,
    LiveAccountCommand,
    LiveExtendedJoin,
    LivePrefix,
    ReconnectContinuity,
    HistoricalAccountTag,
    HistoricalAccountCommand,
    HistoricalExtendedJoin,
    HistoricalNickname,
    HistoricalPrefix
}

public enum IdentityEvidenceStrength
{
    None,
    NicknameOnly,
    CurrentLiveNickname,
    BoundedContinuity,
    StrongAccount,
    ExactConversation
}

public enum IdentityEvidenceMatch
{
    NoMatch,
    NicknameOnly,
    CurrentLiveNickname,
    BoundedContinuity,
    StrongAccount,
    ConflictingAccount,
    ConflictingNetwork,
    Ambiguous
}

public sealed record ConversationIdentityEvidence(
    Guid NetworkId,
    string Nickname,
    string? Account,
    string? Username,
    string? Host,
    int? ConnectionGeneration,
    DateTimeOffset? ObservedAt,
    IdentityEvidenceSource Source,
    bool IsHistorical = false)
{
    public IdentityEvidenceStrength Strength => Source switch
    {
        IdentityEvidenceSource.CurrentConversationView => IdentityEvidenceStrength.ExactConversation,
        IdentityEvidenceSource.LiveAccountTag
            or IdentityEvidenceSource.LiveAccountCommand
            or IdentityEvidenceSource.LiveExtendedJoin => IdentityEvidenceStrength.StrongAccount,
        IdentityEvidenceSource.ReconnectContinuity => IdentityEvidenceStrength.BoundedContinuity,
        IdentityEvidenceSource.LivePrefix => IdentityEvidenceStrength.CurrentLiveNickname,
        _ => IdentityEvidenceStrength.NicknameOnly
    };

    public string? NormalizedAccount => NormalizeAccount(Account);

    public string? NormalizedNickname => NormalizeNickname(Nickname);

    public static string? NormalizeAccount(string? account) =>
        string.IsNullOrWhiteSpace(account) || string.Equals(account, "*", StringComparison.Ordinal)
            ? null
            : account.Length > ConfigurationLimits.MaximumStringLength ? null : account;

    public static string? NormalizeNickname(string? nickname) =>
        string.IsNullOrWhiteSpace(nickname) || nickname.Length > ConfigurationLimits.MaximumStringLength
            ? null
            : nickname;
}

public sealed record ConversationIdentityEvidenceSnapshot(
    Guid NetworkId,
    IReadOnlyList<ConversationIdentityEvidence> Observations,
    IReadOnlySet<string> Accounts,
    IReadOnlySet<string> Nicknames,
    bool HasConflictingAccounts,
    IdentityEvidenceStrength StrongestStrength)
{
    public bool HasAccount(string account) =>
        Accounts.Contains(ConversationIdentityEvidence.NormalizeAccount(account) ?? string.Empty);

    public bool HasNickname(string nickname, IrcCaseMapping mapping) =>
        Nicknames.Any(item => IrcCaseMappingComparer.Equals(item, nickname, mapping));

    public static ConversationIdentityEvidenceSnapshot Empty(Guid networkId) =>
        new(networkId, Array.Empty<ConversationIdentityEvidence>(), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), false, IdentityEvidenceStrength.None);
}

/// <summary>
/// A bounded ledger for conversation correlation. It deliberately retains
/// observations instead of replacing an old account/nickname: logout, nick
/// changes, and historical playback must not erase what was previously known.
/// </summary>
public sealed class ConversationIdentityEvidenceLedger
{
    public const int MaximumObservations = 24;

    private readonly object _gate = new();
    private readonly Guid _networkId;
    private readonly List<ConversationIdentityEvidence> _observations = [];

    public ConversationIdentityEvidenceLedger(Guid networkId)
    {
        _networkId = networkId;
    }

    public bool Observe(ConversationIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.NetworkId != _networkId
            || ConversationIdentityEvidence.NormalizeNickname(evidence.Nickname) is null)
        {
            return false;
        }

        var normalized = evidence with
        {
            Nickname = ConversationIdentityEvidence.NormalizeNickname(evidence.Nickname)!,
            Account = ConversationIdentityEvidence.NormalizeAccount(evidence.Account)
        };
        lock (_gate)
        {
            if (_observations.Any(existing => existing == normalized))
            {
                return false;
            }

            _observations.Add(normalized);
            while (_observations.Count > MaximumObservations)
            {
                _observations.RemoveAt(0);
            }

            return true;
        }
    }

    public ConversationIdentityEvidenceSnapshot Snapshot()
    {
        lock (_gate)
        {
            var observations = _observations.ToArray();
            var accounts = observations
                .Select(item => item.NormalizedAccount)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            var nicknames = observations
                .Select(item => item.NormalizedNickname)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            return new ConversationIdentityEvidenceSnapshot(
                _networkId,
                observations,
                accounts,
                nicknames,
                accounts.Count > 1,
                observations.Select(item => item.Strength).DefaultIfEmpty(IdentityEvidenceStrength.None).Max());
        }
    }
}

public static class ConversationIdentityEvidencePolicy
{
    public static IdentityEvidenceMatch Assess(
        Guid networkId,
        string nickname,
        string? account,
        ConversationIdentityEvidenceSnapshot existing,
        IrcCaseMapping mapping,
        bool currentNicknameOwnership,
        bool boundedContinuity)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.NetworkId != networkId)
        {
            return IdentityEvidenceMatch.ConflictingNetwork;
        }

        var normalizedAccount = ConversationIdentityEvidence.NormalizeAccount(account);
        if (normalizedAccount is not null && existing.Accounts.Count > 0)
        {
            if (existing.HasConflictingAccounts)
            {
                return IdentityEvidenceMatch.ConflictingAccount;
            }

            return existing.Accounts.Contains(normalizedAccount)
                ? IdentityEvidenceMatch.StrongAccount
                : IdentityEvidenceMatch.ConflictingAccount;
        }

        if (boundedContinuity)
        {
            return IdentityEvidenceMatch.BoundedContinuity;
        }

        if (currentNicknameOwnership
            && existing.HasNickname(nickname, mapping))
        {
            return IdentityEvidenceMatch.CurrentLiveNickname;
        }

        return existing.HasNickname(nickname, mapping)
            ? IdentityEvidenceMatch.NicknameOnly
            : IdentityEvidenceMatch.NoMatch;
    }
}
