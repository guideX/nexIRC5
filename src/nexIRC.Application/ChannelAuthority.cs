using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum ChannelAuthorityCertainty
{
    Unknown,
    Allowed,
    Denied
}

public sealed record ChannelAuthorityDecision(ChannelAuthorityCertainty Certainty, string Reason)
{
    public bool IsAllowed => Certainty == ChannelAuthorityCertainty.Allowed;

    public bool IsDenied => Certainty == ChannelAuthorityCertainty.Denied;

    public bool IsUnknown => Certainty == ChannelAuthorityCertainty.Unknown;

    public string StateText => Certainty switch
    {
        ChannelAuthorityCertainty.Allowed => "likely allowed",
        ChannelAuthorityCertainty.Denied => "clearly insufficient",
        _ => "unknown/server-dependent"
    };

    public static ChannelAuthorityDecision Allowed(string reason) => new(ChannelAuthorityCertainty.Allowed, reason);

    public static ChannelAuthorityDecision Denied(string reason) => new(ChannelAuthorityCertainty.Denied, reason);

    public static ChannelAuthorityDecision Unknown(string reason) => new(ChannelAuthorityCertainty.Unknown, reason);
}

/// <summary>
/// Shared, WPF-free advisory authority calculation. A decision is scoped to a
/// network session and channel projection; it is never a security boundary and
/// the server remains authoritative for every operation.
/// </summary>
public sealed record ChannelAuthority(
    Guid NetworkId,
    string Channel,
    string? CurrentUserPrivilege,
    ChannelAuthorityDecision ChangeChannelModes,
    ChannelAuthorityDecision GrantOrRemovePrivilege,
    ChannelAuthorityDecision Kick,
    ChannelAuthorityDecision Ban,
    ChannelAuthorityDecision Unban,
    ChannelAuthorityDecision Invite,
    ChannelAuthorityDecision ChangeTopic,
    ChannelAuthorityDecision QueryListModes)
{
    public int ConnectionGeneration { get; init; }

    public IReadOnlyDictionary<char, ChannelAuthorityDecision> MemberPrivilegeChanges { get; init; } =
        new Dictionary<char, ChannelAuthorityDecision>();

    public static ChannelAuthority Evaluate(NetworkWorkspace network, ChannelView channel, ChannelMemberView? target = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(channel);

        var snapshot = network.Snapshot;
        var connected = snapshot.Registration == RegistrationState.Registered
            && snapshot.State is not (ServerSessionState.Disconnected or ServerSessionState.Failed or ServerSessionState.ReconnectWaiting);
        if (!connected)
        {
            return Create(network, channel, "unknown", ChannelAuthorityDecision.Denied("The network is not registered."), null, false);
        }

        if (!channel.IsJoined)
        {
            return Create(network, channel, "unknown", ChannelAuthorityDecision.Denied("You are not joined to this channel."), null, false);
        }

        var prefix = snapshot.Features.Prefix;
        var localKnown = channel.MembersSnapshot.Any(member => IrcIdentity.Equals(member.Nickname, snapshot.Nickname, snapshot.Features.CaseMapping));
        if (prefix is null || !localKnown)
        {
            var reason = prefix is null ? "The server did not advertise PREFIX." : "The current user's channel membership is not known yet.";
            return Create(network, channel, "unknown", ChannelAuthorityDecision.Unknown(reason), prefix, localKnown);
        }

        var localRank = BestRank(channel.LocalPrefixModes, prefix);
        var localPrivilege = localRank == int.MaxValue ? "ordinary member" : PrivilegeModel.FriendlyName(prefix.Modes[localRank]);
        var modeAuthority = IsModerationRank(localRank, prefix)
            ? ChannelAuthorityDecision.Allowed($"Current user has {localPrivilege} privilege.")
            : ChannelAuthorityDecision.Denied("Current user is an ordinary member or voice-only.");

        var relative = target is null
            ? (Grant: modeAuthority, Moderation: modeAuthority)
            : RelativeTargetDecision(channel, target, prefix, localRank, snapshot.Features.CaseMapping);
        var topic = channel.Modes.Contains('t')
            ? modeAuthority
            : IsModerationRank(localRank, prefix)
                ? ChannelAuthorityDecision.Allowed("Current user has an advertised moderation privilege.")
                : ChannelAuthorityDecision.Unknown("Topic policy is server-dependent because topic protection is not known.");
        var invite = IsModerationRank(localRank, prefix)
            ? ChannelAuthorityDecision.Allowed("Current user has an advertised moderation privilege.")
            : ChannelAuthorityDecision.Unknown("Invite policy is server-dependent for this channel.");

        var authority = new ChannelAuthority(
            network.Id,
            channel.Channel,
            localPrivilege,
            modeAuthority,
            relative.Grant,
            relative.Moderation,
            relative.Moderation,
            relative.Moderation,
            invite,
            topic,
            ChannelAuthorityDecision.Allowed("The server can be queried for advertised list modes."))
        {
            ConnectionGeneration = snapshot.ConnectionGeneration,
            MemberPrivilegeChanges = prefix.Modes.ToDictionary(
                mode => mode,
                mode => target is null
                    ? modeAuthority
                    : EvaluatePrivilegeChange(channel, target, prefix, localRank, mode, snapshot.Features.CaseMapping))
        };
        return authority;
    }

    private static ChannelAuthority Create(
        NetworkWorkspace network,
        ChannelView channel,
        string privilege,
        ChannelAuthorityDecision modeAuthority,
        IrcPrefixGrammar? prefix,
        bool localKnown)
    {
        var unknown = prefix is null || !localKnown
            ? ChannelAuthorityDecision.Unknown(prefix is null ? "PREFIX is missing or malformed." : "Current-user privilege is not known yet.")
            : modeAuthority;
        var denied = modeAuthority.IsDenied ? modeAuthority : unknown;
        return new ChannelAuthority(
            network.Id,
            channel.Channel,
            privilege,
            modeAuthority,
            unknown,
            denied,
            denied,
            denied,
            unknown,
            unknown,
            unknown)
        {
            ConnectionGeneration = network.Snapshot.ConnectionGeneration
        };
    }

    private static (ChannelAuthorityDecision Grant, ChannelAuthorityDecision Moderation) RelativeTargetDecision(
        ChannelView channel,
        ChannelMemberView? target,
        IrcPrefixGrammar prefix,
        int localRank,
        IrcCaseMapping mapping)
    {
        if (target is null)
        {
            return (ChannelAuthorityDecision.Unknown("No target privilege is available."), ChannelAuthorityDecision.Unknown("No target privilege is available."));
        }

        var targetKnown = channel.MembersSnapshot.Any(member => IrcIdentity.Equals(member.Nickname, target.Nickname, mapping));
        if (!targetKnown)
        {
            return (ChannelAuthorityDecision.Unknown("The target's advertised privilege is not known."), ChannelAuthorityDecision.Unknown("The target's advertised privilege is not known."));
        }

        var targetRank = BestRank(target.PrefixModes, prefix);
        if (!IsModerationRank(localRank, prefix))
        {
            return (ChannelAuthorityDecision.Denied("Current user is not a channel moderator."), ChannelAuthorityDecision.Denied("Current user is not a channel moderator."));
        }

        if (targetRank < localRank)
        {
            return (ChannelAuthorityDecision.Denied("The target has a higher advertised privilege."), ChannelAuthorityDecision.Denied("The target has a higher advertised privilege."));
        }

        if (targetRank == localRank)
        {
            return (ChannelAuthorityDecision.Unknown("Equal-ranked target; server policy decides."), ChannelAuthorityDecision.Unknown("Equal-ranked target; server policy decides."));
        }

        return (ChannelAuthorityDecision.Allowed("Target is lower-ranked or ordinary."), ChannelAuthorityDecision.Allowed("Target is lower-ranked or ordinary."));
    }

    private static ChannelAuthorityDecision EvaluatePrivilegeChange(
        ChannelView channel,
        ChannelMemberView target,
        IrcPrefixGrammar prefix,
        int localRank,
        char targetMode,
        IrcCaseMapping mapping)
    {
        if (!IsModerationRank(localRank, prefix))
        {
            return ChannelAuthorityDecision.Denied("Current user is not a channel moderator.");
        }

        var targetMember = channel.MembersSnapshot.FirstOrDefault(member => IrcIdentity.Equals(member.Nickname, target.Nickname, mapping));
        if (targetMember is null)
        {
            return ChannelAuthorityDecision.Unknown("The target's advertised privilege is not known.");
        }

        var requestedRank = prefix.RankOf(targetMode);
        var targetRank = BestRank(target.PrefixModes, prefix);
        if (requestedRank < 0 || targetRank < localRank)
        {
            return ChannelAuthorityDecision.Denied("The requested privilege is above the current user or the target is higher-ranked.");
        }

        if (targetRank == localRank)
        {
            return ChannelAuthorityDecision.Unknown("Equal-ranked target; server policy decides.");
        }

        return localRank <= requestedRank
            ? ChannelAuthorityDecision.Allowed("The requested privilege is at or below the current user's rank.")
            : ChannelAuthorityDecision.Denied("The requested privilege is higher-ranked than the current user.");
    }

    private static int BestRank(IEnumerable<char> modes, IrcPrefixGrammar prefix)
    {
        var rank = int.MaxValue;
        foreach (var mode in modes)
        {
            var candidate = prefix.RankOf(mode);
            if (candidate >= 0)
            {
                rank = Math.Min(rank, candidate);
            }
        }

        return rank;
    }

    private static bool IsModerationRank(int rank, IrcPrefixGrammar prefix) =>
        rank != int.MaxValue && (prefix.Modes.Count == 1 || rank < prefix.Modes.Count - 1);
}
