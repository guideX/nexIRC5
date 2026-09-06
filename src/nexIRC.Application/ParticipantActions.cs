using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum ParticipantActionKind
{
    Submenu,
    OpenQuery,
    Mention,
    Whois,
    Notice,
    CtcpPing,
    CtcpVersion,
    CtcpTime,
    CopyNickname,
    CopyHostmask,
    CopyAccount,
    CopyIdentity,
    GivePrivilege,
    RemovePrivilege,
    Kick,
    Ban,
    KickAndBan,
    Unban,
    Ignore,
    Unignore,
    Invite
}

public sealed record ParticipantActionContext(
    NetworkWorkspace Network,
    ChannelView Channel,
    ChannelMemberView Member,
    IReadOnlyList<ChannelView>? JoinedChannels = null)
{
    public Guid NetworkId => Network.Id;

    public string ChannelName => Channel.Channel;

    public string TargetNickname => Member.Nickname;

    public bool IsConsistent => Channel.NetworkId == Network.Id;

    public bool IsSelf => IrcIdentity.Equals(TargetNickname, Network.Snapshot.Nickname, Network.Snapshot.Features.CaseMapping);

    public bool IsRegistered => IsConsistent
        && Network.Snapshot.Registration == RegistrationState.Registered
        && Network.Snapshot.State is not (ServerSessionState.Disconnected or ServerSessionState.Failed or ServerSessionState.ReconnectWaiting);

    public bool IsJoined => Channel.IsJoined;

    public IrcPrefixGrammar? Prefix => Network.Snapshot.Features.Prefix;

    public IReadOnlySet<char> TargetModes => Member.PrefixModes;

    public IReadOnlySet<char> LocalModes => Channel.LocalPrefixModes;

    public IReadOnlyList<ChannelView> OtherJoinedChannels => (JoinedChannels ?? Array.Empty<ChannelView>())
        .Where(channel => channel.NetworkId == Network.Id && channel.IsJoined && !IrcIdentity.Equals(channel.Channel, ChannelName, Network.Snapshot.Features.CaseMapping))
        .ToArray();

    public IgnoreIdentity Identity => new(
        TargetNickname,
        Member.Username,
        Member.Host,
        Member.Account,
        Network.ProfileId,
        Network.NetworkName ?? Network.Snapshot.Features.NetworkName ?? Network.DisplayName);
}

public sealed record ParticipantMenuItem(
    string Header,
    ParticipantActionKind Action,
    bool IsEnabled = true,
    string? DisabledReason = null,
    char? ModeLetter = null,
    string? TargetChannel = null,
    IReadOnlyList<ParticipantMenuItem>? Children = null)
{
    public string AccessibleText => IsEnabled || string.IsNullOrWhiteSpace(DisabledReason)
        ? Header
        : $"{Header} ({DisabledReason})";

    public bool IsSubmenu => Children is { Count: > 0 };
}

public sealed record ParticipantMenuGroup(string Header, IReadOnlyList<ParticipantMenuItem> Items);

/// <summary>
/// Produces the rich member menu from current protocol projections. It is
/// intentionally WPF-free so enablement and arbitrary PREFIX behavior can be
/// tested deterministically.
/// </summary>
public static class ParticipantActionCatalog
{
    public static IReadOnlyList<ParticipantMenuGroup> Build(ParticipantActionContext context, bool isIgnored)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connected = context.IsRegistered;
        var channel = context.IsJoined;
        var groups = new List<ParticipantMenuGroup>
        {
            new("Conversation", [
                new("Open Query / Message", ParticipantActionKind.OpenQuery, context.IsConsistent && !context.IsSelf, !context.IsConsistent ? "Participant belongs to another network" : context.IsSelf ? "A self-query is not useful" : null),
                new("Mention", ParticipantActionKind.Mention, context.IsConsistent, context.IsConsistent ? null : "Participant belongs to another network"),
                new("Send Notice", ParticipantActionKind.Notice, connected, "Connect to the network first")
            ]),
            new("Information", [
                new("WHOIS", ParticipantActionKind.Whois, connected, "Connect to the network first"),
                new("CTCP", ParticipantActionKind.Submenu, connected, "Connect to the network first", Children: [
                    new("Ping", ParticipantActionKind.CtcpPing, connected, "Connect to the network first"),
                    new("Version", ParticipantActionKind.CtcpVersion, connected, "Connect to the network first"),
                    new("Time", ParticipantActionKind.CtcpTime, connected, "Connect to the network first")
                ]),
                new("Copy Nickname", ParticipantActionKind.CopyNickname),
                new("Copy Hostmask", ParticipantActionKind.CopyHostmask, context.Member.Hostmask is not null, "Host and username are not known"),
                new("Copy Account", ParticipantActionKind.CopyAccount, !string.IsNullOrWhiteSpace(context.Member.Account), "Account is not known"),
                new("Copy Identity Details", ParticipantActionKind.CopyIdentity, context.Member.Hostmask is not null || !string.IsNullOrWhiteSpace(context.Member.Account), "No diagnostic identity details are known")
            ])
        };

        if (context.Prefix is { Modes.Count: > 0 } && channel)
        {
            var privilegeItems = new List<ParticipantMenuItem>();
            foreach (var mode in context.Prefix.Modes)
            {
                var hasMode = context.TargetModes.Contains(mode);
                var canChange = connected && !context.IsSelf && PrivilegeModel.CanChange(context.LocalModes, mode, context.Prefix);
                var reason = !connected
                    ? "Connect to the network first"
                    : context.IsSelf
                        ? "Self moderation is not offered here"
                        : canChange ? null : "Current channel privilege is insufficient or unknown";
                privilegeItems.Add(new ParticipantMenuItem(
                    $"{(hasMode ? "Remove" : "Give")} {PrivilegeModel.FriendlyName(mode)}",
                    hasMode ? ParticipantActionKind.RemovePrivilege : ParticipantActionKind.GivePrivilege,
                    canChange,
                    reason,
                    mode));
            }

            groups.Add(new ParticipantMenuGroup("Channel Privileges", privilegeItems));
        }

        if (channel && !context.IsSelf)
        {
            var canModerate = connected && context.Prefix is not null && PrivilegeModel.CanModerate(context.LocalModes, context.Prefix);
            var reason = !connected
                ? "Connect to the network first"
                : canModerate ? null : "Current channel privilege is insufficient or unknown";
            var moderationItems = new List<ParticipantMenuItem>
            {
                new("Kick…", ParticipantActionKind.Kick, canModerate, reason),
                new("Ban…", ParticipantActionKind.Ban, canModerate, reason),
                new("Kick and Ban…", ParticipantActionKind.KickAndBan, canModerate, reason)
            };
            if (context.Member.Hostmask is not null)
            {
                moderationItems.Add(new ParticipantMenuItem("Unban…", ParticipantActionKind.Unban, canModerate, reason));
            }

            groups.Add(new ParticipantMenuGroup("Moderation", moderationItems));
        }

        groups.Add(new ParticipantMenuGroup("Ignore", [
            new(isIgnored ? "Unignore" : "Ignore", isIgnored ? ParticipantActionKind.Unignore : ParticipantActionKind.Ignore, !context.IsSelf, context.IsSelf ? "Ignoring yourself is not useful" : null)
        ]));

        var inviteItems = context.OtherJoinedChannels
            .Select(channelView => new ParticipantMenuItem(
                $"Invite to {channelView.Channel}",
                ParticipantActionKind.Invite,
                connected,
                connected ? null : "Connect to the network first",
                TargetChannel: channelView.Channel))
            .ToArray();
        if (inviteItems.Length > 0)
        {
            groups.Add(new ParticipantMenuGroup("Invite", inviteItems));
        }

        return groups;
    }
}

public static class PrivilegeModel
{
    public static string FriendlyName(char mode) => mode switch
    {
        'q' => "Owner",
        'a' => "Admin",
        'o' => "Operator",
        'h' => "Half-operator",
        'v' => "Voice",
        _ => $"Mode {mode}"
    };

    public static bool CanModerate(IReadOnlySet<char> localModes, IrcPrefixGrammar grammar) =>
        localModes.Count > 0 && grammar.Modes.Take(Math.Max(1, grammar.Modes.Count - 1)).Any(localModes.Contains);

    public static bool CanChange(IReadOnlySet<char> localModes, char targetMode, IrcPrefixGrammar grammar)
    {
        if (localModes.Count == 0)
        {
            return false;
        }

        var targetRank = grammar.Modes
            .Select((mode, index) => (mode, index))
            .Where(item => item.mode == targetMode)
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (targetRank < 0)
        {
            return false;
        }

        var localRank = grammar.Modes
            .Select((mode, index) => (mode, index))
            .Where(item => localModes.Contains(item.mode))
            .Select(item => item.index)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        return localRank <= targetRank && localRank < grammar.Modes.Count - 1;
    }
}

public static class ParticipantMention
{
    public static string Insert(string existingDraft, string nickname, bool isEmptyChannelComposer)
    {
        ArgumentNullException.ThrowIfNull(existingDraft);
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        if (existingDraft.Length == 0 && isEmptyChannelComposer)
        {
            return $"{nickname}: ";
        }

        return existingDraft + nickname;
    }
}

public sealed class ParticipantActionService
{
    private readonly NetworkSessionManager _sessions;

    public ParticipantActionService(NetworkSessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public QueryView OpenQuery(ParticipantActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsConsistent)
        {
            throw new InvalidOperationException("The participant and channel belong to different networks.");
        }

        var query = _sessions.EnsureQuery(context.NetworkId, context.TargetNickname);
        _sessions.ActivateView(query.Id);
        _sessions.RecordRecent(context.Network, DestinationKind.Query, context.TargetNickname);
        return query;
    }

    public async ValueTask<CommandDispatchResult> SendWhoisAsync(ParticipantActionContext context, CancellationToken cancellationToken = default)
    {
        if (!RequireRegistered(context, out var failure))
        {
            return failure!;
        }

        var result = await _sessions.RequestWhoisAsync(context.NetworkId, context.TargetNickname, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"WHOIS requested for {context.TargetNickname}.", result.View);
    }

    public async ValueTask<CommandDispatchResult> SendNoticeAsync(ParticipantActionContext context, string text, CancellationToken cancellationToken = default)
    {
        if (!RequireRegistered(context, out var failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Any(character => character is '\r' or '\n' or '\u0000' or '\u0001'))
        {
            return CommandDispatchResult.Failure("NOTICE text is required and cannot contain line breaks or control characters.", context.Channel);
        }

        var command = IrcParticipantCommandBuilder.BuildNotice(CommandBuilder(context), context.TargetNickname, text);
        await context.Network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        _sessions.AppendLocal(context.Channel, IrcEventPresentation.CreateLocalMessage(context.Network.Snapshot.Nickname, text, OutgoingMessageKind.Notice));
        return CommandDispatchResult.Success($"Notice sent to {context.TargetNickname}.", context.Channel);
    }

    public async ValueTask<CommandDispatchResult> SendCtcpAsync(ParticipantActionContext context, string commandName, string? arguments = null, CancellationToken cancellationToken = default)
    {
        if (!RequireRegistered(context, out var failure))
        {
            return failure!;
        }

        if (arguments?.Any(character => character is '\r' or '\n' or '\u0000' or '\u0001') == true)
        {
            return CommandDispatchResult.Failure("CTCP arguments cannot contain line breaks or control characters.", context.Channel);
        }

        var command = IrcParticipantCommandBuilder.BuildCtcp(CommandBuilder(context), context.TargetNickname, commandName, arguments);
        await context.Network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        _sessions.AppendLocal(context.Channel, IrcEventPresentation.CreateLocalCtcp(commandName, arguments ?? string.Empty));
        return CommandDispatchResult.Success($"CTCP {commandName.ToUpperInvariant()} sent to {context.TargetNickname}.", context.Channel);
    }

    public async ValueTask<CommandDispatchResult> SetPrivilegeAsync(ParticipantActionContext context, char mode, bool adding, CancellationToken cancellationToken = default)
    {
        if (!RequireChannelPrivilege(context, mode, out var failure))
        {
            return failure!;
        }

        var command = IrcParticipantCommandBuilder.BuildMemberMode(CommandBuilder(context), context.ChannelName, mode, adding, context.TargetNickname);
        await context.Network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Member mode {(adding ? "added" : "removed")} for {context.TargetNickname}.", context.Channel);
    }

    public async ValueTask<CommandDispatchResult> KickAsync(ParticipantActionContext context, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!RequireModeration(context, out var failure))
        {
            return failure!;
        }

        if (reason?.Any(character => character is '\r' or '\n' or '\u0000' or '\u0001') == true)
        {
            return CommandDispatchResult.Failure("Kick reasons cannot contain line breaks or control characters.", context.Channel);
        }

        var command = IrcParticipantCommandBuilder.BuildKick(CommandBuilder(context), context.ChannelName, context.TargetNickname, reason);
        await context.Network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Kick requested for {context.TargetNickname}.", context.Channel);
    }

    public async ValueTask<CommandDispatchResult> BanAsync(ParticipantActionContext context, string banMask, CancellationToken cancellationToken = default)
    {
        if (!RequireModeration(context, out var failure))
        {
            return failure!;
        }

        if (!IsValidBanMask(banMask))
        {
            return CommandDispatchResult.Failure("Enter a non-empty ban mask without whitespace or control characters.", context.Channel);
        }

        var command = IrcParticipantCommandBuilder.BuildBan(CommandBuilder(context), context.ChannelName, banMask, ResolveBanMode(context));
        await context.Network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Ban requested for {context.TargetNickname} using {banMask}.", context.Channel);
    }

    public async ValueTask<CommandDispatchResult> KickAndBanAsync(ParticipantActionContext context, string banMask, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!RequireModeration(context, out var failure))
        {
            return failure!;
        }

        if (!IsValidBanMask(banMask) || reason?.Any(character => character is '\r' or '\n' or '\u0000' or '\u0001') == true)
        {
            return CommandDispatchResult.Failure("The ban mask or kick reason is invalid.", context.Channel);
        }

        var builder = CommandBuilder(context);
        var ban = IrcParticipantCommandBuilder.BuildBan(builder, context.ChannelName, banMask, ResolveBanMode(context));
        var kick = IrcParticipantCommandBuilder.BuildKick(builder, context.ChannelName, context.TargetNickname, reason);
        await context.Network.Session.SendCommandAsync(ban, cancellationToken).ConfigureAwait(false);
        await context.Network.Session.SendCommandAsync(kick, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Ban and kick requested for {context.TargetNickname}.", context.Channel);
    }

    public async ValueTask<CommandDispatchResult> UnbanAsync(ParticipantActionContext context, string banMask, CancellationToken cancellationToken = default)
    {
        if (!RequireModeration(context, out var failure))
        {
            return failure!;
        }

        if (!IsValidBanMask(banMask))
        {
            return CommandDispatchResult.Failure("Enter a non-empty ban mask without whitespace or control characters.", context.Channel);
        }

        var command = IrcParticipantCommandBuilder.BuildUnban(CommandBuilder(context), context.ChannelName, banMask, ResolveBanMode(context));
        await context.Network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Unban requested for {banMask}.", context.Channel);
    }

    public async ValueTask<CommandDispatchResult> InviteAsync(ParticipantActionContext context, string channel, CancellationToken cancellationToken = default)
    {
        if (!RequireRegistered(context, out var failure))
        {
            return failure!;
        }

        if (!context.OtherJoinedChannels.Any(candidate => IrcIdentity.Equals(candidate.Channel, channel, context.Network.Snapshot.Features.CaseMapping)))
        {
            return CommandDispatchResult.Failure("Invite requires another joined channel in this network.", context.Channel);
        }

        var command = IrcParticipantCommandBuilder.BuildInvite(CommandBuilder(context), context.TargetNickname, channel);
        await context.Network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Invite requested for {context.TargetNickname} to {channel}.", context.Channel);
    }

    public bool IsIgnored(ParticipantActionContext context) =>
        _sessions.Configuration?.IsIgnored(context.Identity, context.Network.Snapshot.Features.CaseMapping) == true;

    public async ValueTask<CommandDispatchResult> SetIgnoredAsync(ParticipantActionContext context, bool ignored, CancellationToken cancellationToken = default)
    {
        if (_sessions.Configuration is not { } configuration)
        {
            return CommandDispatchResult.Failure("Ignore rules require a configuration store.", context.Channel);
        }

        var rule = new IgnoreRule
        {
            Nickname = context.TargetNickname,
            Hostmask = context.Member.Hostmask,
            Account = context.Member.Account,
            NetworkProfileId = context.Network.ProfileId,
            NetworkName = context.Network.ProfileId is null
                ? context.Network.NetworkName ?? context.Network.Snapshot.Features.NetworkName ?? context.Network.DisplayName
                : null
        };
        var changed = ignored
            ? configuration.AddIgnore(rule)
            : configuration.IgnoreRules
                .FirstOrDefault(existing => IgnoreMatcher.Matches(existing, context.Identity, context.Network.Snapshot.Features.CaseMapping)) is { } existingRule
                    && configuration.RemoveIgnore(existingRule);
        if (changed)
        {
            await configuration.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return CommandDispatchResult.Success(ignored ? $"Ignoring {context.TargetNickname}." : $"No longer ignoring {context.TargetNickname}.", context.Channel);
    }

    public static string? GetConservativeBanMask(ChannelMemberView member) => member.Hostmask;

    public static bool IsValidBanMask(string? banMask) =>
        !string.IsNullOrWhiteSpace(banMask)
        && banMask.Length <= ConfigurationLimits.MaximumStringLength
        && !banMask.Any(char.IsWhiteSpace)
        && !banMask.Any(character => character is '\r' or '\n' or '\u0000' or '\u0001');

    private static char ResolveBanMode(ParticipantActionContext context) =>
        context.Network.Snapshot.Features.ChannelModes?.ListModes.FirstOrDefault() ?? 'b';

    private static IrcCommandBuilder CommandBuilder(ParticipantActionContext context)
    {
        var maximum = Math.Min(context.Network.Session.MaximumOutboundLineBytes, context.Network.Snapshot.Features.LineLength);
        return new IrcCommandBuilder(Math.Max(3, maximum));
    }

    private static bool RequireRegistered(ParticipantActionContext context, out CommandDispatchResult? failure)
    {
        if (!context.IsRegistered)
        {
            failure = CommandDispatchResult.Failure("The network is not registered.", context.Channel);
            return false;
        }

        failure = null;
        return true;
    }

    private static bool RequireChannelPrivilege(ParticipantActionContext context, char mode, out CommandDispatchResult? failure)
    {
        if (!context.IsJoined)
        {
            failure = CommandDispatchResult.Failure("You are not joined to this channel.", context.Channel);
            return false;
        }

        if (!RequireRegistered(context, out failure))
        {
            return false;
        }

        if (context.Prefix is null || !PrivilegeModel.CanChange(context.LocalModes, mode, context.Prefix) || context.IsSelf)
        {
            failure = CommandDispatchResult.Failure("The current privilege state does not authorize this action.", context.Channel);
            return false;
        }

        return true;
    }

    private static bool RequireModeration(ParticipantActionContext context, out CommandDispatchResult? failure)
    {
        if (!context.IsJoined)
        {
            failure = CommandDispatchResult.Failure("You are not joined to this channel.", context.Channel);
            return false;
        }

        if (!RequireRegistered(context, out failure))
        {
            return false;
        }

        if (context.IsSelf || context.Prefix is null || !PrivilegeModel.CanModerate(context.LocalModes, context.Prefix))
        {
            failure = CommandDispatchResult.Failure("The current privilege state does not authorize moderation.", context.Channel);
            return false;
        }

        return true;
    }
}
