using System.Globalization;

namespace nexIRC.Application;

/// <summary>
/// Origin of an entry in the conversation substrate.  This is intentionally
/// separate from conversation identity: a recovered server message still
/// belongs to the same channel or query as its live counterpart.
/// </summary>
public enum ConversationEntryProvenance
{
    Live,
    LocalHistory,
    ServerPlayback
}

public enum ConversationTimestampSource
{
    LegacyOrLocalReceiveTime,
    ServerTime
}

public static class ConversationHistorySchema
{
    public const int CurrentVersion = 2;
}

/// <summary>
/// A candidate entering the shared history policy.  Local history candidates
/// are never persisted again, and only live candidates may raise live UI
/// activity.  The record's Provenance remains the durable origin; the
/// candidate provenance describes this delivery path.
/// </summary>
public sealed record ConversationEntryCandidate
{
    public required ConversationLogRecord Record { get; init; }
    public required ConversationEntryProvenance Provenance { get; init; }
    public bool Persist { get; init; }

    public bool IsHistorical => Provenance is ConversationEntryProvenance.LocalHistory or ConversationEntryProvenance.ServerPlayback;

    public bool TriggerLiveSideEffects => Provenance == ConversationEntryProvenance.Live;

    public static ConversationEntryCandidate FromLive(ConversationLogRecord record) => new()
    {
        Record = record with { Provenance = ConversationEntryProvenance.Live },
        Provenance = ConversationEntryProvenance.Live,
        Persist = true
    };

    public static ConversationEntryCandidate FromLocalHistory(ConversationLogRecord record) => new()
    {
        Record = record,
        Provenance = ConversationEntryProvenance.LocalHistory,
        Persist = false
    };

    public static ConversationEntryCandidate FromServerPlayback(ConversationLogRecord record) => new()
    {
        Record = record with { Provenance = ConversationEntryProvenance.ServerPlayback },
        Provenance = ConversationEntryProvenance.ServerPlayback,
        Persist = true
    };
}

/// <summary>
/// Exact identity is deliberately narrow.  A server message id is scoped by
/// the logical network id; absent an id, every observation remains distinct.
/// Timestamp, sender, text, message kind, and provenance are ordering or
/// display data, never an authoritative duplicate key.
/// </summary>
public static class ConversationEntryIdentity
{
    public static string? NormalizeServerMessageId(string? messageId) =>
        string.IsNullOrWhiteSpace(messageId) || messageId.Length > 256
            ? null
            : messageId;

    public static string? GetServerIdentityKey(ConversationLogRecord record)
    {
        var messageId = NormalizeServerMessageId(record.ServerMessageId);
        return messageId is null
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{record.NetworkId:N}\0{messageId}");
    }

    public static bool IsExactServerDuplicate(ConversationLogRecord left, ConversationLogRecord right) =>
        GetServerIdentityKey(left) is { } leftKey
        && string.Equals(leftKey, GetServerIdentityKey(right), StringComparison.Ordinal);

    public static bool HasAuthoritativeIdentity(ConversationLogRecord record) =>
        NormalizeServerMessageId(record.ServerMessageId) is not null;
}

/// <summary>
/// Canonical ordering for conversation records.  Server time is already in
/// Timestamp when TimestampSource is ServerTime; the source field is retained
/// to document that legacy receive time is less authoritative, not to let a
/// late playback item jump ahead of a newer timestamp.  DurableSequence is
/// the deterministic equal-time tie breaker.
/// </summary>
public static class ConversationHistoryOrdering
{
    public static int Compare(ConversationLogRecord left, ConversationLogRecord right)
    {
        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        if (timestamp != 0)
        {
            return timestamp;
        }

        var sequence = CompareSequence(left.DurableSequence, right.DurableSequence);
        if (sequence != 0)
        {
            return sequence;
        }

        var identity = string.Compare(
            ConversationEntryIdentity.GetServerIdentityKey(left),
            ConversationEntryIdentity.GetServerIdentityKey(right),
            StringComparison.Ordinal);
        if (identity != 0)
        {
            return identity;
        }

        var sender = string.Compare(left.Sender, right.Sender, StringComparison.Ordinal);
        if (sender != 0)
        {
            return sender;
        }

        var kind = left.MessageKind.CompareTo(right.MessageKind);
        if (kind != 0)
        {
            return kind;
        }

        // This is only a deterministic fallback for legacy records which do
        // not yet have a durable sequence.  It intentionally does not imply
        // that identical no-id records are duplicates.
        return string.Compare(left.Text, right.Text, StringComparison.Ordinal);
    }

    public static IOrderedEnumerable<ConversationLogRecord> OrderAscending(IEnumerable<ConversationLogRecord> records) =>
        records.OrderBy(record => record, Comparer<ConversationLogRecord>.Create(Compare));

    public static IOrderedEnumerable<ConversationLogRecord> OrderDescending(IEnumerable<ConversationLogRecord> records) =>
        records.OrderByDescending(record => record, Comparer<ConversationLogRecord>.Create(Compare));

    public static int CompareSequence(long left, long right)
    {
        if (left == right)
        {
            return 0;
        }

        // Zero means an old record without the new field.  Keep those records
        // stable relative to one another while making newly sequenced records
        // deterministic against them.
        if (left == 0) return -1;
        if (right == 0) return 1;
        return left.CompareTo(right);
    }
}

/// <summary>
/// Shared, conservative merge policy for local history and future playback.
/// Exact server-id duplicates are removed; no-id observations are never
/// removed by content heuristics.
/// </summary>
public static class ConversationHistoryMerge
{
    public static IReadOnlyList<ConversationLogRecord> DeduplicateExact(IEnumerable<ConversationLogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var result = new List<ConversationLogRecord>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var identity = ConversationEntryIdentity.GetServerIdentityKey(record);
            if (identity is not null && !identities.Add(identity))
            {
                continue;
            }

            result.Add(record);
        }

        return result;
    }

    public static IReadOnlyList<ConversationLogRecord> Merge(
        IEnumerable<ConversationLogRecord> existing,
        IEnumerable<ConversationEntryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidates);
        return ConversationHistoryOrdering.OrderAscending(
                DeduplicateExact(existing.Concat(candidates.Select(candidate => candidate.Record))))
            .ToArray();
    }

    public static bool ShouldTriggerLiveSideEffects(ConversationEntryCandidate candidate) =>
        candidate.TriggerLiveSideEffects && !candidate.IsHistorical;
}

/// <summary>
/// Converts durable history back to the same transcript value used by live
/// presentation.  It deliberately does not call logging, notifications,
/// unread counters, participant reconciliation, or automation hooks.
/// </summary>
public static class ConversationHistoryProjection
{
    public static TranscriptEntry ToTranscriptEntry(
        ConversationLogRecord record,
        ConversationEntryProvenance provenance = ConversationEntryProvenance.LocalHistory) =>
        new(
            record.Timestamp,
            ToTranscriptKind(record.MessageKind, record.Direction),
            record.Sender,
            record.Text,
            record.IsHighlight ? "highlight" : null,
            record.DurableSequence,
            record.ReceivedAt)
        {
            ServerMessageId = record.ServerMessageId,
            Provenance = provenance,
            TimestampSource = record.TimestampSource,
            BatchId = record.BatchId
        };

    private static TranscriptEntryKind ToTranscriptKind(LogMessageKind kind, LogDirection direction) => kind switch
    {
        LogMessageKind.Action => direction == LogDirection.Outgoing ? TranscriptEntryKind.OutgoingAction : TranscriptEntryKind.Action,
        LogMessageKind.Notice => direction == LogDirection.Outgoing ? TranscriptEntryKind.OutgoingNotice : TranscriptEntryKind.Notice,
        LogMessageKind.Ctcp => direction == LogDirection.Outgoing ? TranscriptEntryKind.OutgoingCtcp : TranscriptEntryKind.Ctcp,
        LogMessageKind.Join => TranscriptEntryKind.Join,
        LogMessageKind.Part => TranscriptEntryKind.Part,
        LogMessageKind.Quit => TranscriptEntryKind.Quit,
        LogMessageKind.Kick => TranscriptEntryKind.Kick,
        LogMessageKind.Nick => TranscriptEntryKind.Nick,
        LogMessageKind.Topic => TranscriptEntryKind.Topic,
        LogMessageKind.Mode => TranscriptEntryKind.Mode,
        LogMessageKind.Error => TranscriptEntryKind.Error,
        LogMessageKind.System => TranscriptEntryKind.System,
        _ => direction == LogDirection.Outgoing ? TranscriptEntryKind.OutgoingMessage : TranscriptEntryKind.Message
    };
}
