using nexIRC.Core.Protocol;
using nexIRC.Core.Session;

namespace nexIRC.Application;

/// <summary>
/// Where a bounded repair boundary came from.  The values deliberately name
/// observation provenance rather than a WPF object or nickname spelling.
/// </summary>
public enum HistoryGapBoundaryProvenance
{
    PreDisconnectCanonical,
    PostReconnectCanonical,
    DirectlyObservedLive,
    DirectlyObservedPreDisconnect,
    CanonicalServerPlayback,
    LocalCanonicalIndex
}

public enum HistoryGapBoundaryTrust
{
    Untrusted,
    TimestampBounded,
    ExactServerMessageId
}

public enum HistoryGapRepairState
{
    Discovered,
    Eligible,
    Queued,
    Requesting,
    PartiallyRepaired,
    Repaired,
    RepairedLocally,
    Exhausted,
    Unsupported,
    Failed,
    Cancelled,
    Stale
}

/// <summary>
/// A canonical local record plus the server selector that may be used to
/// address it.  It composes the existing CHATHISTORY reference abstraction;
/// it does not create a second wire-anchor type.
/// </summary>
public sealed record HistoryGapBoundary
{
    public required Guid NetworkId { get; init; }

    public required string Conversation { get; init; }

    public required ChathistoryReference Reference { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public ConversationTimestampSource TimestampSource { get; init; } = ConversationTimestampSource.ServerTime;

    public string? ServerMessageId { get; init; }

    public long DurableSequence { get; init; }

    public required HistoryGapBoundaryProvenance Provenance { get; init; }

    public int ConnectionGeneration { get; init; }

    public bool IsCanonical { get; init; } = true;

    public bool IsStale { get; init; }

    public HistoryGapBoundaryTrust Trust =>
        !IsCanonical || IsStale
            ? HistoryGapBoundaryTrust.Untrusted
            : Reference.IsConcrete && Reference.Type == ChathistoryReferenceType.MessageId && !string.IsNullOrWhiteSpace(ServerMessageId)
                ? HistoryGapBoundaryTrust.ExactServerMessageId
                : Reference.IsConcrete && Reference.Type == ChathistoryReferenceType.Timestamp && TimestampSource == ConversationTimestampSource.ServerTime
                    ? HistoryGapBoundaryTrust.TimestampBounded
                    : HistoryGapBoundaryTrust.Untrusted;

    public static HistoryGapBoundary FromTranscript(
        Guid networkId,
        string conversation,
        TranscriptEntry entry,
        ChathistoryReference reference,
        HistoryGapBoundaryProvenance provenance,
        int connectionGeneration) => new()
        {
            NetworkId = networkId,
            Conversation = conversation,
            Reference = reference,
            Timestamp = entry.Timestamp,
            TimestampSource = entry.TimestampSource,
            ServerMessageId = entry.ServerMessageId,
            DurableSequence = entry.Sequence,
            Provenance = provenance,
            ConnectionGeneration = connectionGeneration
        };
}

public sealed record HistoryGap
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid NetworkId { get; init; }

    /// <summary>Durable conversation/history identity, never a view id.</summary>
    public required string Conversation { get; init; }

    /// <summary>Current server spelling used only for this request.</summary>
    public required string Target { get; init; }

    public required HistoryGapBoundary Older { get; init; }

    public required HistoryGapBoundary Newer { get; init; }

    public required int RepairConnectionGeneration { get; init; }

    public HistoryGapRepairState State { get; init; } = HistoryGapRepairState.Discovered;

    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastProgressAt { get; init; } = DateTimeOffset.UtcNow;

    public int Rounds { get; init; }

    public int RecoveredEntries { get; init; }

    public string? LastReason { get; init; }

    public bool IsTerminal => State is HistoryGapRepairState.Repaired
        or HistoryGapRepairState.RepairedLocally
        or HistoryGapRepairState.Exhausted
        or HistoryGapRepairState.Unsupported
        or HistoryGapRepairState.Failed
        or HistoryGapRepairState.Cancelled
        or HistoryGapRepairState.Stale;

    public string Key => HistoryGapKey.For(this);
}

public sealed record HistoryGapRepairBudget(
    int MaximumActiveRepairs = 1,
    int MaximumRoundsPerGap = 4,
    int MaximumEntriesPerGap = 200,
    int MaximumOperationsPerReconnect = 32,
    int MaximumOutstandingGaps = 32,
    TimeSpan? MaximumGapLifetime = null)
{
    public TimeSpan EffectiveMaximumGapLifetime => MaximumGapLifetime ?? TimeSpan.FromMinutes(5);

    public static HistoryGapRepairBudget Default { get; } = new();
}

public sealed record HistoryGapRepairDiagnostics(
    int Discovered,
    int Eligible,
    int Queued,
    int RequestsSent,
    int Rounds,
    int RecoveredEntries,
    int RepairedLocally,
    int Repaired,
    int Exhausted,
    int Unsupported,
    int Failed,
    int Cancelled,
    int Stale)
{
    public static HistoryGapRepairDiagnostics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record HistoryGapValidationResult(bool IsEligible, HistoryGapBoundaryTrust Trust, string? Reason)
{
    public static HistoryGapValidationResult Reject(string reason) => new(false, HistoryGapBoundaryTrust.Untrusted, reason);
}

public enum HistoryGapItemDisposition
{
    Interior,
    BoundaryDuplicate,
    Unrelated,
    Malformed
}

public sealed record HistoryGapBatchValidation(
    IReadOnlyList<IrcSemanticEvent> Interior,
    int BoundaryDuplicates,
    int AllowedContext,
    int Unrelated,
    int Malformed,
    bool IsSafe,
    string? Failure)
{
    public bool HasInterior => Interior.Count > 0;
}

/// <summary>Pure eligibility and ordering rules for exact repair.</summary>
public static class HistoryGapPolicy
{
    public static HistoryGapBatchValidation ValidateBatch(
        HistoryGap gap,
        ChathistoryResult result,
        int maximumItems = 512)
    {
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            return new(Array.Empty<IrcSemanticEvent>(), 0, 0, 0, 0, false, result.Failure ?? "The CHATHISTORY request failed.");
        }

        if (!string.Equals(result.BatchType, "chathistory", StringComparison.OrdinalIgnoreCase))
        {
            return new(Array.Empty<IrcSemanticEvent>(), 0, 0, 0, 0, false, "The response was not a chathistory batch.");
        }

        if (result.Request.NetworkId != gap.NetworkId
            || result.Request.Operation != ChathistoryOperation.Between
            || !string.Equals(result.Request.Conversation, gap.Conversation, StringComparison.Ordinal)
            || !string.Equals(result.Request.Target, gap.Target, StringComparison.Ordinal)
            || result.BatchTarget is { Length: > 0 } batchTarget && !string.Equals(batchTarget, gap.Target, StringComparison.Ordinal)
            || !string.Equals(result.Request.Reference.SerializeWire(), gap.Older.Reference.SerializeWire(), StringComparison.Ordinal)
            || result.Request.SecondaryReference is not { } secondary
            || !string.Equals(secondary.SerializeWire(), gap.Newer.Reference.SerializeWire(), StringComparison.Ordinal))
        {
            return new(Array.Empty<IrcSemanticEvent>(), 0, 0, 0, 0, false, "The response did not belong to the active exact gap request.");
        }

        if (result.Messages.Count > Math.Max(1, maximumItems))
        {
            return new(Array.Empty<IrcSemanticEvent>(), 0, 0, 0, result.Messages.Count, false, "The history response exceeded the bounded item budget.");
        }

        var interior = new List<IrcSemanticEvent>();
        var boundaryDuplicates = 0;
        var allowedContext = 0;
        var unrelated = 0;
        var malformed = 0;
        foreach (var item in result.Messages)
        {
            if (!item.IsHistorical || item.NetworkId != gap.NetworkId || !string.Equals(item.HistoricalConversation, gap.Conversation, StringComparison.Ordinal))
            {
                unrelated++;
                continue;
            }

            var serverId = item.Message.ServerMessageId;
            if (string.Equals(serverId, gap.Older.ServerMessageId, StringComparison.Ordinal)
                || string.Equals(serverId, gap.Newer.ServerMessageId, StringComparison.Ordinal))
            {
                boundaryDuplicates++;
                continue;
            }

            if (item.Message.TagValues.ContainsKey("draft/chathistory-context"))
            {
                allowedContext++;
            }
            else if (IsInterior(gap, item))
            {
                interior.Add(item);
            }
            else
            {
                if (item.Message.ServerTimestamp is null)
                {
                    malformed++;
                }
                else
                {
                    unrelated++;
                }
            }
        }

        var safe = unrelated == 0 && malformed == 0;
        return new(
            interior,
            boundaryDuplicates,
            allowedContext,
            unrelated,
            malformed,
            safe,
            safe ? null : "The response contained unrelated or malformed items outside the trusted gap.");
    }

    public static HistoryGapValidationResult Validate(
        HistoryGapBoundary older,
        HistoryGapBoundary newer,
        ChathistorySupport support,
        Guid networkId,
        string conversation,
        int repairConnectionGeneration)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        ArgumentNullException.ThrowIfNull(support);

        if (networkId == Guid.Empty || older.NetworkId != networkId || newer.NetworkId != networkId)
        {
            return HistoryGapValidationResult.Reject("Gap boundaries are not in the active network.");
        }

        if (string.IsNullOrWhiteSpace(conversation)
            || !string.Equals(older.Conversation, conversation, StringComparison.Ordinal)
            || !string.Equals(newer.Conversation, conversation, StringComparison.Ordinal))
        {
            return HistoryGapValidationResult.Reject("Gap boundaries do not share one durable conversation identity.");
        }

        if (repairConnectionGeneration < 1
            || older.ConnectionGeneration < 1
            || newer.ConnectionGeneration != repairConnectionGeneration
            || newer.IsStale
            || older.IsStale)
        {
            return HistoryGapValidationResult.Reject("A gap boundary belongs to a stale or invalid connection generation.");
        }

        if (!support.IsUsable)
        {
            return HistoryGapValidationResult.Reject("The negotiated CHATHISTORY capability set is not usable.");
        }

        if (!ChathistoryReference.AreCompatible(older.Reference, newer.Reference)
            || !support.Supports(older.Reference.Type))
        {
            return HistoryGapValidationResult.Reject("The two boundaries do not form a supported compatible selector pair.");
        }

        if (older.Reference.Type == ChathistoryReferenceType.MessageId
            && (!string.Equals(older.ServerMessageId, older.Reference.Value, StringComparison.Ordinal)
                || !string.Equals(newer.ServerMessageId, newer.Reference.Value, StringComparison.Ordinal)))
        {
            return HistoryGapValidationResult.Reject("A msgid selector is not backed by the same canonical server message id.");
        }

        if (older.Reference.Type == ChathistoryReferenceType.Timestamp
            && (older.TimestampSource != ConversationTimestampSource.ServerTime
                || newer.TimestampSource != ConversationTimestampSource.ServerTime))
        {
            return HistoryGapValidationResult.Reject("Timestamp repair requires server-time evidence at both boundaries.");
        }

        if (older.Reference.Type == ChathistoryReferenceType.Timestamp
            && (!DateTimeOffset.TryParse(older.Reference.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var olderReferenceTimestamp)
                || !DateTimeOffset.TryParse(newer.Reference.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var newerReferenceTimestamp)
                || olderReferenceTimestamp != older.Timestamp.ToUniversalTime()
                || newerReferenceTimestamp != newer.Timestamp.ToUniversalTime()))
        {
            return HistoryGapValidationResult.Reject("A timestamp selector does not match its canonical boundary timestamp.");
        }

        if (older.Timestamp == newer.Timestamp
            && (older.DurableSequence == 0
                || newer.DurableSequence == 0
                || older.DurableSequence >= newer.DurableSequence))
        {
            return HistoryGapValidationResult.Reject("Equal-time boundaries require a deterministic local durable sequence.");
        }

        var order = CompareCanonical(older, newer);
        if (order >= 0)
        {
            return HistoryGapValidationResult.Reject("The newer boundary is not after the older canonical boundary.");
        }

        return new HistoryGapValidationResult(
            true,
            older.Reference.Type == ChathistoryReferenceType.MessageId
                ? HistoryGapBoundaryTrust.ExactServerMessageId
                : HistoryGapBoundaryTrust.TimestampBounded,
            null);
    }

    public static int CompareCanonical(HistoryGapBoundary left, HistoryGapBoundary right)
    {
        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        if (timestamp != 0)
        {
            return timestamp;
        }

        if (left.DurableSequence != 0 || right.DurableSequence != 0)
        {
            var sequence = ConversationHistoryOrdering.CompareSequence(left.DurableSequence, right.DurableSequence);
            if (sequence != 0)
            {
                return sequence;
            }
        }

        // A server msgid is an opaque selector. Its lexical representation is
        // never evidence that one equal-time boundary precedes another.
        return 0;
    }

    public static bool IsBoundaryOrInterior(HistoryGap gap, IrcSemanticEvent item)
    {
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsHistorical || item.NetworkId != gap.NetworkId)
        {
            return false;
        }

        if (!string.Equals(item.HistoricalConversation, gap.Conversation, StringComparison.Ordinal))
        {
            return false;
        }

        var message = item.Message;
        if (string.Equals(message.ServerMessageId, gap.Older.ServerMessageId, StringComparison.Ordinal)
            || string.Equals(message.ServerMessageId, gap.Newer.ServerMessageId, StringComparison.Ordinal))
        {
            return true;
        }

        if (message.ServerTimestamp is not { } timestamp)
        {
            return gap.Older.Reference.Type == ChathistoryReferenceType.MessageId
                && message.ServerMessageId is { Length: > 0 };
        }

        // A timestamp reference is a protocol boundary, not a unique local
        // event.  Permit equal-time interior events and rely on exact msgid
        // dedupe when present; reject only clearly outside content.
        return timestamp >= gap.Older.Timestamp && timestamp <= gap.Newer.Timestamp;
    }

    public static bool IsInterior(HistoryGap gap, IrcSemanticEvent item)
    {
        if (!IsBoundaryOrInterior(gap, item))
        {
            return false;
        }

        var id = item.Message.ServerMessageId;
        return !string.Equals(id, gap.Older.ServerMessageId, StringComparison.Ordinal)
            && !string.Equals(id, gap.Newer.ServerMessageId, StringComparison.Ordinal);
    }
}

/// <summary>
/// Runtime/session-owned ledger.  It is intentionally not persisted: JSONL
/// history and rebuildable .hidx metadata remain authoritative after restart.
/// </summary>
public sealed class HistoryGapLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HistoryGap> _gaps = new(StringComparer.Ordinal);
    private readonly HistoryGapRepairBudget _budget;
    private int _activeRepairs;
    private int _operations;
    private int _discovered;
    private int _eligible;
    private int _queued;
    private int _requests;
    private int _rounds;
    private int _recoveredEntries;
    private int _repairedLocally;
    private int _repaired;
    private int _exhausted;
    private int _unsupported;
    private int _failed;
    private int _cancelled;
    private int _stale;

    public HistoryGapLedger(HistoryGapRepairBudget? budget = null)
    {
        _budget = budget ?? HistoryGapRepairBudget.Default;
    }

    public IReadOnlyList<HistoryGap> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _gaps.Values.OrderBy(gap => gap.DiscoveredAt).ToArray();
            }
        }
    }

    public HistoryGapRepairDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new(
                    _discovered,
                    _eligible,
                    _queued,
                    _requests,
                    _rounds,
                    _recoveredEntries,
                    _repairedLocally,
                    _repaired,
                    _exhausted,
                    _unsupported,
                    _failed,
                    _cancelled,
                    _stale);
            }
        }
    }

    public bool TryDiscover(
        HistoryGapBoundary older,
        HistoryGapBoundary newer,
        string target,
        ChathistorySupport support,
        int repairConnectionGeneration,
        out HistoryGap gap,
        out HistoryGapValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        ArgumentNullException.ThrowIfNull(target);
        validation = HistoryGapPolicy.Validate(older, newer, support, newer.NetworkId, newer.Conversation, repairConnectionGeneration);
        if (!validation.IsEligible)
        {
            gap = new HistoryGap
            {
                NetworkId = newer.NetworkId,
                Conversation = newer.Conversation,
                Target = target,
                Older = older,
                Newer = newer,
                RepairConnectionGeneration = repairConnectionGeneration,
                State = HistoryGapRepairState.Unsupported,
                LastReason = validation.Reason
            };
            return false;
        }

        var candidate = new HistoryGap
        {
            NetworkId = newer.NetworkId,
            Conversation = newer.Conversation,
            Target = target,
            Older = older,
            Newer = newer,
            RepairConnectionGeneration = repairConnectionGeneration,
            State = HistoryGapRepairState.Eligible
        };
        lock (_gate)
        {
            var key = HistoryGapKey.For(candidate);
            if (_gaps.TryGetValue(key, out gap!))
            {
                return true;
            }

            if (_gaps.Count >= _budget.MaximumOutstandingGaps)
            {
                gap = candidate with { State = HistoryGapRepairState.Exhausted, LastReason = "The outstanding gap budget is exhausted." };
                return false;
            }

            _gaps.Add(key, candidate);
            _discovered++;
            _eligible++;
            gap = candidate;
            return true;
        }
    }

    public bool TryQueue(HistoryGap gap, out HistoryGap queued)
    {
        ArgumentNullException.ThrowIfNull(gap);
        lock (_gate)
        {
            if (!_gaps.TryGetValue(gap.Key, out var current))
            {
                queued = gap with { State = HistoryGapRepairState.Stale, LastReason = "The gap is no longer owned by this ledger." };
                return false;
            }

            if (current.IsTerminal)
            {
                queued = current;
                return false;
            }

            if (_activeRepairs >= _budget.MaximumActiveRepairs || _operations >= _budget.MaximumOperationsPerReconnect)
            {
                queued = current with { State = HistoryGapRepairState.Exhausted, LastReason = "The bounded repair operation budget is exhausted." };
                _gaps[current.Key] = queued;
                _exhausted++;
                return false;
            }

            queued = current with { State = HistoryGapRepairState.Queued, LastProgressAt = DateTimeOffset.UtcNow };
            _gaps[current.Key] = queued;
            _activeRepairs++;
            _queued++;
            return true;
        }
    }

    public HistoryGap MarkRequesting(HistoryGap gap)
    {
        lock (_gate)
        {
            if (!_gaps.TryGetValue(gap.Key, out var current)) return gap with { State = HistoryGapRepairState.Stale };
            var updated = current with { State = HistoryGapRepairState.Requesting, Rounds = current.Rounds + 1, LastProgressAt = DateTimeOffset.UtcNow };
            _gaps[current.Key] = updated;
            _requests++;
            _rounds++;
            _operations++;
            return updated;
        }
    }

    public HistoryGap MarkProgress(HistoryGap gap, int recoveredEntries, bool complete, string? reason = null)
    {
        lock (_gate)
        {
            if (!_gaps.TryGetValue(gap.Key, out var current)) return gap with { State = HistoryGapRepairState.Stale };
            var updated = current with
            {
                State = complete ? HistoryGapRepairState.Repaired : HistoryGapRepairState.PartiallyRepaired,
                RecoveredEntries = current.RecoveredEntries + Math.Max(0, recoveredEntries),
                LastProgressAt = DateTimeOffset.UtcNow,
                LastReason = reason
            };
            _gaps[current.Key] = updated;
            _recoveredEntries += Math.Max(0, recoveredEntries);
            if (complete)
            {
                _repaired++;
                _activeRepairs = Math.Max(0, _activeRepairs - 1);
            }

            return updated;
        }
    }

    public HistoryGap MarkLocallyFilled(HistoryGap gap, string? reason = null)
    {
        lock (_gate)
        {
            if (!_gaps.TryGetValue(gap.Key, out var current)) return gap with { State = HistoryGapRepairState.Stale };
            var updated = current with { State = HistoryGapRepairState.RepairedLocally, LastProgressAt = DateTimeOffset.UtcNow, LastReason = reason };
            _gaps[current.Key] = updated;
            _repairedLocally++;
            if (current.State is HistoryGapRepairState.Queued or HistoryGapRepairState.Requesting or HistoryGapRepairState.PartiallyRepaired)
            {
                _activeRepairs = Math.Max(0, _activeRepairs - 1);
            }
            return updated;
        }
    }

    public HistoryGap MarkTerminal(HistoryGap gap, HistoryGapRepairState state, string reason)
    {
        if (state is not (HistoryGapRepairState.Exhausted or HistoryGapRepairState.Unsupported or HistoryGapRepairState.Failed or HistoryGapRepairState.Cancelled or HistoryGapRepairState.Stale))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        lock (_gate)
        {
            if (!_gaps.TryGetValue(gap.Key, out var current)) return gap with { State = HistoryGapRepairState.Stale, LastReason = reason };
            var updated = current with { State = state, LastReason = reason, LastProgressAt = DateTimeOffset.UtcNow };
            _gaps[current.Key] = updated;
            _activeRepairs = Math.Max(0, _activeRepairs - 1);
            switch (state)
            {
                case HistoryGapRepairState.Exhausted: _exhausted++; break;
                case HistoryGapRepairState.Unsupported: _unsupported++; break;
                case HistoryGapRepairState.Failed: _failed++; break;
                case HistoryGapRepairState.Cancelled: _cancelled++; break;
                case HistoryGapRepairState.Stale: _stale++; break;
            }

            return updated;
        }
    }

    public bool IsWithinLifetime(HistoryGap gap) => DateTimeOffset.UtcNow - gap.DiscoveredAt <= _budget.EffectiveMaximumGapLifetime;

    public bool HasRoundBudget(HistoryGap gap) => gap.Rounds < _budget.MaximumRoundsPerGap;

    public bool HasEntryBudget(HistoryGap gap) => gap.RecoveredEntries < _budget.MaximumEntriesPerGap;

    public void BeginReconnect()
    {
        lock (_gate)
        {
            _operations = 0;
        }
    }

}

internal static class HistoryGapKey
{
    public static string For(HistoryGap gap) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{gap.NetworkId:N}\0{gap.Conversation}\0{gap.Older.Reference.SerializeWire()}\0{gap.Newer.Reference.SerializeWire()}");
}
