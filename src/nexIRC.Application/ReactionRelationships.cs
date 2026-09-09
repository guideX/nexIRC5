using System.Globalization;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum ReactionOperation
{
    React,
    Unreact
}

/// <summary>
/// Conservative actor evidence used by reaction aggregation. The key is an
/// internal, network-scoped key and is never presented directly in WPF.
/// </summary>
public sealed record ReactionActorIdentity
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? Account { get; init; }

    public string? Nickname { get; init; }

    public string? User { get; init; }

    public string? Host { get; init; }

    public bool IsAccountBacked => !string.IsNullOrWhiteSpace(Account);

    public static ReactionActorIdentity FromMessage(Guid networkId, IrcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var prefix = message.Prefix;
        var account = message.TagValues.TryGetValue("account", out var accountTag)
            && !string.IsNullOrWhiteSpace(accountTag)
            && !string.Equals(accountTag, "*", StringComparison.Ordinal)
                ? Bound(accountTag, 256)
                : null;
        var nickname = Bound(prefix?.Name, 128);
        var user = Bound(prefix?.User, 128);
        var host = Bound(prefix?.Host, 256);
        var scope = networkId.ToString("N", CultureInfo.InvariantCulture);
        var key = account is not null
            ? $"{scope}\0account:{account}"
            : prefix is not null
                ? $"{scope}\0prefix:{Bound(prefix.Raw, 512)}"
                : $"{scope}\0nick:{IrcCaseMappingComparer.Fold(nickname ?? "?", IrcCaseMapping.Rfc1459)}";
        return new ReactionActorIdentity
        {
            Key = key,
            DisplayName = account ?? nickname ?? prefix?.Raw ?? "unknown user",
            Account = account,
            Nickname = nickname,
            User = user,
            Host = host
        };
    }

    public static ReactionActorIdentity ForLocal(Guid networkId, string nickname, string? account = null) => new()
    {
        Key = account is { Length: > 0 }
            ? $"{networkId.ToString("N", CultureInfo.InvariantCulture)}\0account:{account}"
            : $"{networkId.ToString("N", CultureInfo.InvariantCulture)}\0nick:{IrcCaseMappingComparer.Fold(nickname, IrcCaseMapping.Rfc1459)}",
        DisplayName = nickname,
        Nickname = nickname,
        Account = account
    };

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= maximum ? value : value[..maximum];
}

/// <summary>A bounded, typed reaction pill projection for WPF.</summary>
public sealed record ReactionSummaryItem(
    string Value,
    int Count,
    IReadOnlyList<string> ActorNames,
    bool CurrentUserReacted)
{
    public string DisplayText => $"{Value} {Count}";

    public string AttributionText => ActorNames.Count == 0
        ? $"{Count} reactions"
        : ActorNames.Count < Count
            ? $"{string.Join(", ", ActorNames)}; {Count} reactions"
            : string.Join(", ", ActorNames);

    public string AccessibleText => CurrentUserReacted
        ? $"{Value}, {Count}; you reacted. Select to remove your reaction. {AttributionText}"
        : $"{Value}, {Count}; select to react. {AttributionText}";

    public bool CanToggle => true;
}

/// <summary>
/// Per-conversation relationship state. The owning WorkspaceView supplies the
/// network and conversation boundary, so this is never a global reaction graph.
/// </summary>
public sealed class ReactionStateStore
{
    public const int MaximumPendingParents = 1024;
    public const int MaximumGroupsPerParent = 128;
    public const int MaximumActorsPerGroup = 4096;
    public const int MaximumAttributionNames = 12;

    private readonly Dictionary<string, ParentState> _parents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentActorKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentActorKeysByNickname = new(StringComparer.Ordinal);
    private long _occurrence;

    public int ParentCount => _parents.Count;

    public int Apply(
        string parentMessageId,
        string value,
        ReactionOperation operation,
        ReactionActorIdentity actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentMessageId);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentNullException.ThrowIfNull(actor);
        if (!IrcReplyReference.TryParse(parentMessageId, out var parent))
        {
            return 0;
        }

        var parentId = parent!.MessageId;
        if (operation == ReactionOperation.Unreact && !_parents.ContainsKey(parentId))
        {
            return 0;
        }

        var effectiveActorKey = EffectiveActorKey(actor);

        if (!_parents.TryGetValue(parentId, out var state))
        {
            if (_parents.Count >= MaximumPendingParents)
            {
                return 0;
            }

            state = new ParentState();
            _parents.Add(parentId, state);
        }

        if (!state.Groups.TryGetValue(value, out var group))
        {
            if (operation == ReactionOperation.Unreact || state.Groups.Count >= MaximumGroupsPerParent)
            {
                return 0;
            }

            group = new GroupState(NextOccurrence());
            state.Groups.Add(value, group);
        }

        if (operation == ReactionOperation.React)
        {
            if (group.Actors.TryGetValue(effectiveActorKey, out var existingActor))
            {
                // Refresh friendly evidence without changing count or order.
                group.Actors[effectiveActorKey] = existingActor with { Actor = actor };
                return 0;
            }

            if (TryFindCurrentNicknameAlias(group, actor, effectiveActorKey, out var aliasedActor))
            {
                // A no-echo local send starts with nickname evidence. If the
                // later own echo supplies an authenticated account, upgrade
                // the same actor in place instead of counting it twice.
                group.Actors.Remove(aliasedActor!.Key);
                group.Actors[effectiveActorKey] = aliasedActor with { Key = effectiveActorKey, Actor = actor };
                return 0;
            }

            if (group.Actors.Count >= MaximumActorsPerGroup)
            {
                return 0;
            }

            group.Actors.Add(effectiveActorKey, new ActorState(effectiveActorKey, actor, NextOccurrence()));
            return 1;
        }

        var removed = group.Actors.Remove(effectiveActorKey);
        if (!removed
            && TryFindCurrentNicknameAlias(group, actor, effectiveActorKey, out var unreactAlias))
        {
            removed = group.Actors.Remove(unreactAlias!.Key);
        }
        if (!removed)
        {
            return 0;
        }

        if (group.Actors.Count == 0)
        {
            state.Groups.Remove(value);
        }

        if (state.Groups.Count == 0)
        {
            _parents.Remove(parentId);
        }

        return 1;
    }

    public void SetCurrentActor(ReactionActorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _currentActorKeys.Add(actor.Key);
        if (actor.Nickname is { Length: > 0 } nickname)
        {
            _currentActorKeysByNickname[IrcCaseMappingComparer.Fold(nickname, IrcCaseMapping.Rfc1459)] = actor.Key;
        }
    }

    public void ApplyRecord(ConversationLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsReactionEvent
            || record.ReactionParentMessageId is not { } parent
            || record.ReactionValue is not { } value
            || record.ReactionActorKey is not { } key)
        {
            return;
        }

        var actor = new ReactionActorIdentity
        {
            Key = key,
            DisplayName = string.IsNullOrWhiteSpace(record.ReactionActorDisplay) ? record.Sender ?? "unknown user" : record.ReactionActorDisplay,
            Account = record.ReactionActorAccount,
            Nickname = record.ReactionActorNickname,
            User = record.ReactionActorUser,
            Host = record.ReactionActorHost
        };
        Apply(parent, value, record.ReactionOperation == DurableReactionOperation.Unreact ? ReactionOperation.Unreact : ReactionOperation.React, actor);
    }

    public IReadOnlyList<ReactionSummaryItem> GetSummary(string? parentMessageId)
    {
        if (string.IsNullOrWhiteSpace(parentMessageId)
            || !_parents.TryGetValue(parentMessageId, out var state))
        {
            return Array.Empty<ReactionSummaryItem>();
        }

        return state.Groups
            .OrderBy(pair => pair.Value.FirstOccurrence)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var actors = pair.Value.Actors.Values
                    .OrderBy(actor => actor.FirstOccurrence)
                    .ThenBy(actor => actor.Key, StringComparer.Ordinal)
                    .ToArray();
                var names = actors
                    .Take(MaximumAttributionNames)
                    .Select(actor => _currentActorKeys.Contains(actor.Key) ? "you" : actor.Actor.DisplayName)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var current = actors.Any(actor => _currentActorKeys.Contains(actor.Key));
                return new ReactionSummaryItem(pair.Key, actors.Length, names, current);
            })
            .ToArray();
    }

    private long NextOccurrence() => ++_occurrence;

    private string EffectiveActorKey(ReactionActorIdentity actor) =>
        actor.Nickname is { Length: > 0 } nickname
            && _currentActorKeysByNickname.TryGetValue(
                IrcCaseMappingComparer.Fold(nickname, IrcCaseMapping.Rfc1459),
                out var currentKey)
                ? currentKey
                : actor.Key;

    private bool TryFindCurrentNicknameAlias(
        GroupState group,
        ReactionActorIdentity actor,
        string effectiveActorKey,
        out ActorState? alias)
    {
        alias = null;
        if (actor.Nickname is not { Length: > 0 } nickname)
        {
            return false;
        }

        var foldedNickname = IrcCaseMappingComparer.Fold(nickname, IrcCaseMapping.Rfc1459);
        alias = group.Actors.Values.FirstOrDefault(candidate =>
            !string.Equals(candidate.Key, effectiveActorKey, StringComparison.Ordinal)
            && _currentActorKeys.Contains(candidate.Key)
            && candidate.Actor.Nickname is { Length: > 0 } candidateNickname
            && string.Equals(
                IrcCaseMappingComparer.Fold(candidateNickname, IrcCaseMapping.Rfc1459),
                foldedNickname,
                StringComparison.Ordinal));
        return alias is not null;
    }

    private sealed class ParentState
    {
        public Dictionary<string, GroupState> Groups { get; } = new(StringComparer.Ordinal);
    }

    private sealed record GroupState(long FirstOccurrence)
    {
        public Dictionary<string, ActorState> Actors { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ActorState(string Key, ReactionActorIdentity Actor, long FirstOccurrence);
}

/// <summary>Application-scoped reaction event after protocol semantics exist.</summary>
public sealed record ReactionEvent
{
    public required Guid NetworkId { get; init; }

    public required string ConversationKey { get; init; }

    public required string ParentMessageId { get; init; }

    public required string Value { get; init; }

    public required ReactionOperation Operation { get; init; }

    public required ReactionActorIdentity Actor { get; init; }

    public string? EventMessageId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }

    public bool IsLocal { get; init; }

    public ConversationEntryProvenance Provenance { get; init; } = ConversationEntryProvenance.Live;

    public ConversationTimestampSource TimestampSource { get; init; } = ConversationTimestampSource.LegacyOrLocalReceiveTime;

    public string? BatchId { get; init; }

    public static ReactionEvent FromCore(
        Guid networkId,
        string conversationKey,
        IrcReactionEvent semanticEvent,
        ConversationEntryProvenance provenance,
        DateTimeOffset? receivedAt = null) => new()
    {
        NetworkId = networkId,
        ConversationKey = conversationKey,
        ParentMessageId = semanticEvent.ParentMessageId,
        Value = semanticEvent.Value,
        Operation = semanticEvent.Operation == IrcReactionOperation.React ? ReactionOperation.React : ReactionOperation.Unreact,
        Actor = ReactionActorIdentity.FromMessage(networkId, semanticEvent.Message),
        EventMessageId = semanticEvent.Message.ServerMessageId,
        Timestamp = semanticEvent.Message.ServerTimestamp ?? receivedAt ?? DateTimeOffset.UtcNow,
        ReceivedAt = receivedAt,
        Provenance = provenance,
        TimestampSource = semanticEvent.Message.ServerTimestamp is not null
            ? ConversationTimestampSource.ServerTime
            : ConversationTimestampSource.LegacyOrLocalReceiveTime,
        BatchId = semanticEvent.Message.BatchId
    };
}
