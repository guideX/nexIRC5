using nexIRC.Core.State;

namespace nexIRC.Application;

/// <summary>
/// The durable conversation scope used by anchor lookup.  NetworkId is
/// intentionally required even when the storage scope is a profile: a
/// server-supplied msgid is never meaningful outside its network.
/// </summary>
public sealed record HistoryConversationAddress
{
    public required Guid NetworkId { get; init; }

    public required Guid ScopeId { get; init; }

    public required LogConversationKind ConversationKind { get; init; }

    public required string ConversationName { get; init; }

    public string? ConversationKey { get; init; }

    internal string EffectiveConversationKey => ConversationKey
        ?? ConversationLoggingService.BuildConversationKey(ConversationKind, ConversationName);
}

public enum HistoryAnchorDirection
{
    AtOrBefore,
    AtOrAfter,
    Around
}

public enum HistoryAnchorMatch
{
    Exact,
    NearestAtOrBefore,
    NearestAtOrAfter,
    NearestAround,
    BoundedMiss
}

public sealed record HistoryServerMessageAnchorRequest
{
    public required HistoryConversationAddress Conversation { get; init; }

    public required string ServerMessageId { get; init; }
}

public sealed record HistoryTimestampAnchorRequest
{
    public required HistoryConversationAddress Conversation { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public HistoryAnchorDirection Direction { get; init; } = HistoryAnchorDirection.Around;
}

public sealed record HistoryAnchorLocation(
    ConversationLogRecord Record,
    long SourceOffset,
    int SourceLength)
{
    internal string? SourcePath { get; init; }
}

public sealed record HistoryAnchorResult(
    HistoryAnchorMatch Match,
    HistoryAnchorLocation? Anchor,
    HistoryAnchorLocation? Before = null,
    HistoryAnchorLocation? After = null)
{
    public bool Found => Anchor is not null;

    public static HistoryAnchorResult Missing { get; } = new(HistoryAnchorMatch.BoundedMiss, null);
}

public sealed record HistoryContextRequest
{
    public required HistoryConversationAddress Conversation { get; init; }

    public string? ServerMessageId { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public HistoryAnchorDirection TimestampDirection { get; init; } = HistoryAnchorDirection.Around;

    public int BeforeCount { get; init; } = 50;

    public int AfterCount { get; init; } = 50;
}

public sealed record HistoryContextResult(
    HistoryAnchorResult Anchor,
    IReadOnlyList<ConversationLogRecord> Records,
    bool IsCompleteLocally)
{
    public static HistoryContextResult Missing { get; } = new(HistoryAnchorResult.Missing, Array.Empty<ConversationLogRecord>(), false);
}

internal static class HistoryAnchorPolicy
{
    public static bool IsAddressMatch(ConversationLogRecord record, HistoryConversationAddress address) =>
        record.ScopeId == address.ScopeId
        && record.NetworkId == address.NetworkId
        && record.ConversationKind == address.ConversationKind
        && (string.Equals(
                ConversationLoggingService.EffectiveConversationKey(record),
                address.EffectiveConversationKey,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(address.ConversationKey)
                && IrcCaseMappingComparer.Equals(record.ConversationName, address.ConversationName, IrcCaseMapping.Rfc1459));

    public static int Compare(HistoryAnchorLocation left, HistoryAnchorLocation right) =>
        ConversationHistoryOrdering.Compare(left.Record, right.Record) is var result && result != 0
            ? result
            : left.SourceOffset.CompareTo(right.SourceOffset);

    public static HistoryAnchorResult SelectTimestamp(
        IEnumerable<HistoryAnchorLocation> source,
        DateTimeOffset timestamp,
        HistoryAnchorDirection direction)
    {
        var records = source.ToArray();
        if (records.Length == 0)
        {
            return HistoryAnchorResult.Missing;
        }

        var before = records
            .Where(item => item.Record.Timestamp <= timestamp)
            .OrderByDescending(item => item.Record, Comparer<ConversationLogRecord>.Create(ConversationHistoryOrdering.Compare))
            .ThenByDescending(item => item.SourceOffset)
            .FirstOrDefault();
        var after = records
            .Where(item => item.Record.Timestamp >= timestamp)
            .OrderBy(item => item.Record, Comparer<ConversationLogRecord>.Create(ConversationHistoryOrdering.Compare))
            .ThenBy(item => item.SourceOffset)
            .FirstOrDefault();

        var anchor = direction switch
        {
            HistoryAnchorDirection.AtOrBefore => before,
            HistoryAnchorDirection.AtOrAfter => after,
            _ => SelectNearest(before, after, timestamp)
        };
        if (anchor is null)
        {
            return new HistoryAnchorResult(HistoryAnchorMatch.BoundedMiss, null, before, after);
        }

        var match = direction switch
        {
            HistoryAnchorDirection.AtOrBefore => HistoryAnchorMatch.NearestAtOrBefore,
            HistoryAnchorDirection.AtOrAfter => HistoryAnchorMatch.NearestAtOrAfter,
            _ => anchor.Record.Timestamp == timestamp ? HistoryAnchorMatch.Exact : HistoryAnchorMatch.NearestAround
        };
        return new HistoryAnchorResult(match, anchor, before, after);
    }

    private static HistoryAnchorLocation? SelectNearest(
        HistoryAnchorLocation? before,
        HistoryAnchorLocation? after,
        DateTimeOffset timestamp)
    {
        if (before is null) return after;
        if (after is null) return before;

        var beforeDistance = Distance(before.Record.Timestamp.UtcTicks, timestamp.UtcTicks);
        var afterDistance = Distance(after.Record.Timestamp.UtcTicks, timestamp.UtcTicks);
        if (beforeDistance != afterDistance)
        {
            return beforeDistance < afterDistance ? before : after;
        }

        // A tie is resolved toward the earlier canonical record. This makes
        // timestamp navigation stable even when a server emits equal-time
        // messages on separate reconnect/playback paths.
        return Compare(before, after) <= 0 ? before : after;
    }

    private static long Distance(long left, long right)
    {
        var difference = left - right;
        return difference == long.MinValue ? long.MaxValue : Math.Abs(difference);
    }
}
