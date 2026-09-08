using nexIRC.Core.Protocol;

namespace nexIRC.Application;

/// <summary>
/// Knowledge about a conversation's history.  Coverage is deliberately
/// evidence based: a projected WPF row is not proof that the local store is
/// complete, and a short server response is not proof of remote exhaustion.
/// </summary>
public enum HistoryCoverageState
{
    Unknown,
    LocalData,
    LocalBeginningReached,
    RemoteMayExist,
    RemoteExhausted,
    Pending,
    Unsupported,
    Failed,
    Cancelled,
    Stale,
    NoProgress,
    AtLatest
}

public enum HistoryCoverageDirection
{
    Backward,
    Forward
}

public enum HistoryCoverageProvenance
{
    CanonicalHistory,
    IndexedHistory,
    LocalProjection,
    ServerPlayback,
    ExplicitServerEnd,
    RequestResult
}

/// <summary>Durable, network-isolated coverage identity.</summary>
public sealed record HistoryCoverageKey(Guid NetworkId, string Conversation)
{
    public static HistoryCoverageKey Create(Guid networkId, string conversation)
    {
        if (networkId == Guid.Empty)
        {
            throw new ArgumentException("A coverage key requires a network id.", nameof(networkId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(conversation);
        return new HistoryCoverageKey(networkId, conversation);
    }

    public override string ToString() => $"{NetworkId:N}\0{Conversation}";
}

/// <summary>
/// A trustworthy edge used to advance a remote selector.  Message ids are
/// opaque; no ordering is inferred from their spelling.
/// </summary>
public sealed record HistoryCoverageAnchor
{
    public required Guid NetworkId { get; init; }
    public required string Conversation { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? ServerMessageId { get; init; }
    public required ChathistoryReference Reference { get; init; }
    public required int ConnectionGeneration { get; init; }
    public string? Target { get; init; }
    public HistoryCoverageProvenance Provenance { get; init; }
}

/// <summary>
/// An observed local window.  Windows are not merged merely because their
/// timestamps are close; this preserves the distinction between two loaded
/// windows with an unknown region between them.
/// </summary>
public sealed record HistoryCoverageWindow(
    DateTimeOffset Oldest,
    DateTimeOffset Newest,
    int ObservedRows,
    HistoryCoverageProvenance Provenance);

public sealed record HistoryCoverageRequestState(
    string RequestKey,
    HistoryCoverageAnchor Frontier,
    int ConnectionGeneration)
{
    public HistoryCoverageDirection Direction { get; init; } = HistoryCoverageDirection.Backward;
}

public sealed record HistoryCoverageSnapshot(
    HistoryCoverageKey Key,
    HistoryCoverageState State,
    IReadOnlyList<HistoryCoverageWindow> LocalWindows,
    bool LocalBeginningReached,
    bool RemoteExhausted,
    bool NoProgressTerminated,
    HistoryCoverageAnchor? RemoteBackwardFrontier,
    HistoryCoverageRequestState? PendingRequest,
    string? LastPaginationResult,
    int LocalPagesLoaded,
    int RemotePagesRequested,
    int RemoteRowsAccepted,
    int RemoteRowsDeduplicated,
    int CoalescedRequests,
    int ZeroProgressTerminations)
{
    public bool LocalNewerAvailable { get; init; }

    public bool LocalEndReached { get; init; }

    public bool RemoteBackwardExhausted { get; init; }

    public bool RemoteForwardExhausted { get; init; }

    public bool ForwardNoProgressTerminated { get; init; }

    public HistoryCoverageAnchor? RemoteForwardFrontier { get; init; }

    public HistoryCoverageAnchor? CanonicalOldest { get; init; }

    public HistoryCoverageAnchor? CanonicalNewest { get; init; }

    public HistoryCoverageAnchor? ProjectedOldest { get; init; }

    public HistoryCoverageAnchor? ProjectedNewest { get; init; }

    public HistoryCoverageDirection? PendingDirection { get; init; }

    public bool AtLatest => LocalEndReached
        && PendingDirection != HistoryCoverageDirection.Forward;
}

/// <summary>
/// Bounded runtime ledger for backward and future forward history loading.
/// Canonical records and the rebuildable index remain the source of truth;
/// this class only remembers small, disposable server-frontier evidence and
/// observed windows for the current network generation.
/// </summary>
public sealed class HistoryCoverageLedger
{
    private const int MaximumWindows = 64;
    private const int MaximumConversationEntries = 256;
    private readonly object _gate = new();
    private readonly Dictionary<HistoryCoverageKey, CoverageEntry> _entries = [];

    public HistoryCoverageSnapshot GetOrCreate(HistoryCoverageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return GetOrCreateUnsafe(key).Snapshot();
        }
    }

    public IReadOnlyList<HistoryCoverageSnapshot> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Select(static entry => entry.Snapshot()).ToArray();
            }
        }
    }

    public HistoryCoverageSnapshot ObserveLocalPage(
        HistoryCoverageKey key,
        IReadOnlyList<ConversationLogRecord> records,
        bool hasOlder,
        bool hasNewer,
        HistoryCoverageProvenance provenance = HistoryCoverageProvenance.IndexedHistory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            if (records.Count > 0)
            {
                var ordered = ConversationHistoryOrdering.OrderAscending(records).ToArray();
                entry.AddWindow(new HistoryCoverageWindow(ordered[0].Timestamp, ordered[^1].Timestamp, ordered.Length, provenance));
                entry.LocalNewerAvailable = hasNewer;
                entry.LocalEndReached = !hasNewer;
                var oldest = ToAnchor(key, ordered[0], entry.Generation, provenance);
                var newest = ToAnchor(key, ordered[^1], entry.Generation, provenance);
                entry.CanonicalOldest = entry.CanonicalOldest is null || CompareAnchors(oldest, entry.CanonicalOldest) < 0
                    ? oldest
                    : entry.CanonicalOldest;
                entry.CanonicalNewest = entry.CanonicalNewest is null || CompareAnchors(newest, entry.CanonicalNewest) > 0
                    ? newest
                    : entry.CanonicalNewest;
                foreach (var record in ordered.TakeLast(MaximumConversationEntries))
                {
                    entry.LocalServerIds.Add(ConversationEntryIdentity.NormalizeServerMessageId(record.ServerMessageId));
                }
                entry.LocalPagesLoaded++;
                entry.LocalBeginningReached = !hasOlder;
                if (entry.RemoteBackwardFrontier is null)
                {
                    entry.State = !hasNewer
                        ? HistoryCoverageState.AtLatest
                        : entry.LocalBeginningReached
                            ? HistoryCoverageState.LocalBeginningReached
                            : HistoryCoverageState.LocalData;
                }
            }

            if (!hasOlder && records.Count == 0)
            {
                entry.LocalBeginningReached = true;
                if (entry.RemoteBackwardFrontier is null && !entry.RemoteExhausted)
                {
                    entry.State = HistoryCoverageState.LocalBeginningReached;
                }
            }

            if (!hasNewer && entry.LocalWindows.Count == 0 && records.Count > 0)
            {
                entry.LocalWindows.Add(new HistoryCoverageWindow(records[0].Timestamp, records[^1].Timestamp, records.Count, provenance));
            }

            return entry.Snapshot();
        }
    }

    /// <summary>
    /// Resets ephemeral request/frontier state for a new server generation.
    /// Local windows remain valid because they come from durable history.
    /// </summary>
    public void BeginGeneration(HistoryCoverageKey key, int connectionGeneration)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            if (entry.Generation == connectionGeneration)
            {
                return;
            }

            entry.Generation = connectionGeneration;
            entry.PendingRequest = null;
            entry.RemoteBackwardFrontier = null;
            entry.RemoteExhausted = false;
            entry.RemoteForwardFrontier = null;
            entry.RemoteForwardExhausted = false;
            entry.ForwardNoProgressTerminated = false;
            entry.NoProgressTerminated = false;
            entry.State = entry.LocalBeginningReached ? HistoryCoverageState.LocalBeginningReached : HistoryCoverageState.LocalData;
            entry.LastPaginationResult = "Server generation changed; remote frontier was discarded.";
        }
    }

    public bool TryBeginBackwardRequest(
        HistoryCoverageKey key,
        HistoryCoverageAnchor frontier,
        string requestKey,
        int connectionGeneration,
        out HistoryCoverageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(frontier);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            if (!IsValidFrontier(key, frontier, connectionGeneration))
            {
                snapshot = entry.Snapshot();
                return false;
            }

            if (entry.PendingRequest is not null)
            {
                entry.CoalescedRequests++;
                snapshot = entry.Snapshot();
                return false;
            }

            if (entry.RemoteExhausted || entry.NoProgressTerminated)
            {
                snapshot = entry.Snapshot();
                return false;
            }

            entry.Generation = connectionGeneration;
            entry.PendingRequest = new HistoryCoverageRequestState(requestKey, frontier, connectionGeneration);
            entry.RemotePagesRequested++;
            entry.State = HistoryCoverageState.Pending;
            snapshot = entry.Snapshot();
            return true;
        }
    }

    public bool TryBeginForwardRequest(
        HistoryCoverageKey key,
        HistoryCoverageAnchor frontier,
        string requestKey,
        int connectionGeneration,
        out HistoryCoverageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(frontier);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            if (!IsValidFrontier(key, frontier, connectionGeneration))
            {
                snapshot = entry.Snapshot();
                return false;
            }

            if (entry.PendingRequest is not null)
            {
                entry.CoalescedRequests++;
                snapshot = entry.Snapshot();
                return false;
            }

            if (entry.RemoteForwardExhausted || entry.ForwardNoProgressTerminated)
            {
                snapshot = entry.Snapshot();
                return false;
            }

            entry.Generation = connectionGeneration;
            entry.PendingRequest = new HistoryCoverageRequestState(requestKey, frontier, connectionGeneration)
            {
                Direction = HistoryCoverageDirection.Forward
            };
            entry.RemotePagesRequested++;
            entry.State = HistoryCoverageState.Pending;
            snapshot = entry.Snapshot();
            return true;
        }
    }

    public HistoryCoverageSnapshot CompleteBackwardRequest(
        HistoryCoverageKey key,
        int connectionGeneration,
        IReadOnlyList<HistoryCoverageAnchor> observations,
        bool explicitEnd,
        bool failed,
        string result,
        int deduplicatedRows = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observations);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            var pending = entry.PendingRequest;
            if (entry.PendingRequest is not null
                && (entry.PendingRequest.ConnectionGeneration != connectionGeneration
                    || entry.PendingRequest.Direction != HistoryCoverageDirection.Backward))
            {
                entry.LastPaginationResult = "Stale pagination completion was ignored.";
                entry.State = HistoryCoverageState.Stale;
                return entry.Snapshot();
            }

            entry.PendingRequest = null;
            entry.LastPaginationResult = result;
            entry.RemoteRowsDeduplicated += Math.Max(0, deduplicatedRows);
            if (failed)
            {
                entry.State = HistoryCoverageState.Failed;
                return entry.Snapshot();
            }

            var frontier = observations
                .Where(item => item.NetworkId == key.NetworkId
                    && string.Equals(item.Conversation, key.Conversation, StringComparison.Ordinal)
                    && item.ConnectionGeneration == connectionGeneration)
                .OrderBy(item => item.Timestamp)
                .FirstOrDefault();
            var progressed = frontier is not null
                && (pending is null || CompareAnchors(frontier, pending.Frontier) < 0);
            if (frontier is not null && progressed)
            {
                entry.RemoteBackwardFrontier = frontier with { Provenance = HistoryCoverageProvenance.ServerPlayback };
                entry.RemoteRowsAccepted += observations.Count;
            }

            if (explicitEnd)
            {
                entry.RemoteExhausted = true;
                entry.NoProgressTerminated = false;
                entry.State = HistoryCoverageState.RemoteExhausted;
            }
            else if (!progressed)
            {
                entry.NoProgressTerminated = true;
                entry.ZeroProgressTerminations++;
                entry.State = HistoryCoverageState.NoProgress;
            }
            else
            {
                entry.State = HistoryCoverageState.RemoteMayExist;
            }

            return entry.Snapshot();
        }
    }

    public HistoryCoverageSnapshot CompleteForwardRequest(
        HistoryCoverageKey key,
        int connectionGeneration,
        IReadOnlyList<HistoryCoverageAnchor> observations,
        bool explicitEnd,
        bool failed,
        string result,
        int deduplicatedRows = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observations);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            var pending = entry.PendingRequest;
            if (entry.PendingRequest is not null
                && (entry.PendingRequest.ConnectionGeneration != connectionGeneration
                    || entry.PendingRequest.Direction != HistoryCoverageDirection.Forward))
            {
                entry.LastPaginationResult = "Stale forward pagination completion was ignored.";
                entry.State = HistoryCoverageState.Stale;
                return entry.Snapshot();
            }

            entry.PendingRequest = null;
            entry.LastPaginationResult = result;
            entry.RemoteRowsDeduplicated += Math.Max(0, deduplicatedRows);
            if (failed)
            {
                entry.State = HistoryCoverageState.Failed;
                return entry.Snapshot();
            }

            var frontier = observations
                .Where(item => item.NetworkId == key.NetworkId
                    && string.Equals(item.Conversation, key.Conversation, StringComparison.Ordinal)
                    && item.ConnectionGeneration == connectionGeneration)
                .OrderByDescending(item => item.Timestamp)
                .ThenByDescending(item => item.Reference.SerializeWire(), StringComparer.Ordinal)
                .ThenByDescending(item => item.ServerMessageId, StringComparer.Ordinal)
                .FirstOrDefault();
            var progressed = frontier is not null
                && (pending is null || CompareAnchors(frontier, pending.Frontier) > 0);
            if (frontier is not null && progressed)
            {
                entry.RemoteForwardFrontier = frontier with { Provenance = HistoryCoverageProvenance.ServerPlayback };
                entry.RemoteRowsAccepted += observations.Count;
            }

            if (explicitEnd)
            {
                entry.RemoteForwardExhausted = true;
                entry.ForwardNoProgressTerminated = false;
                entry.State = HistoryCoverageState.RemoteExhausted;
            }
            else if (!progressed)
            {
                entry.ForwardNoProgressTerminated = true;
                entry.ZeroProgressTerminations++;
                entry.State = HistoryCoverageState.NoProgress;
            }
            else
            {
                entry.State = HistoryCoverageState.RemoteMayExist;
            }

            return entry.Snapshot();
        }
    }

    public HistoryCoverageSnapshot ObserveCanonicalContext(
        HistoryCoverageKey key,
        IReadOnlyList<ConversationLogRecord> records,
        HistoryCoverageProvenance provenance = HistoryCoverageProvenance.IndexedHistory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            if (records.Count > 0)
            {
                var ordered = ConversationHistoryOrdering.OrderAscending(records).ToArray();
                var oldest = ToAnchor(key, ordered[0], entry.Generation, provenance);
                var newest = ToAnchor(key, ordered[^1], entry.Generation, provenance);
                entry.CanonicalOldest = entry.CanonicalOldest is null || CompareAnchors(oldest, entry.CanonicalOldest) < 0
                    ? oldest
                    : entry.CanonicalOldest;
                entry.CanonicalNewest = entry.CanonicalNewest is null || CompareAnchors(newest, entry.CanonicalNewest) > 0
                    ? newest
                    : entry.CanonicalNewest;
            }

            return entry.Snapshot();
        }
    }

    public HistoryCoverageSnapshot ObserveProjectedWindow(
        HistoryCoverageKey key,
        IReadOnlyList<ConversationLogRecord> records,
        bool followingLatest,
        HistoryCoverageProvenance provenance = HistoryCoverageProvenance.LocalProjection)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            if (records.Count == 0)
            {
                entry.ProjectedOldest = null;
                entry.ProjectedNewest = null;
            }
            else
            {
                var ordered = ConversationHistoryOrdering.OrderAscending(records).ToArray();
                entry.ProjectedOldest = ToAnchor(key, ordered[0], entry.Generation, provenance);
                entry.ProjectedNewest = ToAnchor(key, ordered[^1], entry.Generation, provenance);
            }

            entry.LocalEndReached = followingLatest;
            entry.LocalNewerAvailable = !followingLatest;
            return entry.Snapshot();
        }
    }

    public HistoryCoverageSnapshot ObserveProjectedBoundaries(
        HistoryCoverageKey key,
        ConversationLogRecord? oldest,
        ConversationLogRecord? newest,
        bool followingLatest,
        HistoryCoverageProvenance provenance = HistoryCoverageProvenance.LocalProjection)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            entry.ProjectedOldest = oldest is null ? null : ToAnchor(key, oldest, entry.Generation, provenance);
            entry.ProjectedNewest = newest is null ? null : ToAnchor(key, newest, entry.Generation, provenance);
            entry.LocalEndReached = followingLatest;
            entry.LocalNewerAvailable = !followingLatest;
            return entry.Snapshot();
        }
    }

    public HistoryCoverageSnapshot MarkUnsupported(HistoryCoverageKey key, string result)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            entry.PendingRequest = null;
            entry.State = HistoryCoverageState.Unsupported;
            entry.LastPaginationResult = result;
            return entry.Snapshot();
        }
    }

    public HistoryCoverageSnapshot MarkCancelled(HistoryCoverageKey key, string result)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var entry = GetOrCreateUnsafe(key);
            entry.PendingRequest = null;
            entry.State = HistoryCoverageState.Cancelled;
            entry.LastPaginationResult = result;
            return entry.Snapshot();
        }
    }

    private CoverageEntry GetOrCreateUnsafe(HistoryCoverageKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (_entries.Count >= MaximumConversationEntries)
            {
                var evicted = _entries.Keys.First();
                _entries.Remove(evicted);
            }

            entry = new CoverageEntry(key);
            _entries.Add(key, entry);
        }

        return entry;
    }

    private static bool IsValidFrontier(HistoryCoverageKey key, HistoryCoverageAnchor frontier, int connectionGeneration) =>
        frontier.NetworkId == key.NetworkId
        && string.Equals(frontier.Conversation, key.Conversation, StringComparison.Ordinal)
        && frontier.ConnectionGeneration == connectionGeneration
        && frontier.Reference.IsConcrete;

    private static int CompareAnchors(HistoryCoverageAnchor left, HistoryCoverageAnchor right)
    {
        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        if (timestamp != 0)
        {
            return timestamp;
        }

        var reference = string.Compare(left.Reference.SerializeWire(), right.Reference.SerializeWire(), StringComparison.Ordinal);
        return reference != 0
            ? reference
            : string.Compare(left.ServerMessageId, right.ServerMessageId, StringComparison.Ordinal);
    }

    private static HistoryCoverageAnchor ToAnchor(
        HistoryCoverageKey key,
        ConversationLogRecord record,
        int generation,
        HistoryCoverageProvenance provenance) => new()
        {
            NetworkId = key.NetworkId,
            Conversation = key.Conversation,
            Timestamp = record.Timestamp,
            ServerMessageId = ConversationEntryIdentity.NormalizeServerMessageId(record.ServerMessageId),
            Reference = ConversationEntryIdentity.NormalizeServerMessageId(record.ServerMessageId) is { } messageId
            ? ChathistoryReference.MessageId(messageId)
            : ChathistoryReference.Timestamp(record.Timestamp),
            ConnectionGeneration = generation,
            Provenance = provenance
        };

    private sealed class CoverageEntry(HistoryCoverageKey key)
    {
        public HistoryCoverageKey Key { get; } = key;
        public List<HistoryCoverageWindow> LocalWindows { get; } = [];
        public HashSet<string?> LocalServerIds { get; } = [];
        public bool LocalBeginningReached { get; set; }
        public bool RemoteExhausted { get; set; }
        public bool RemoteForwardExhausted { get; set; }
        public bool ForwardNoProgressTerminated { get; set; }
        public bool LocalNewerAvailable { get; set; }
        public bool LocalEndReached { get; set; }
        public bool NoProgressTerminated { get; set; }
        public int Generation { get; set; }
        public HistoryCoverageState State { get; set; } = HistoryCoverageState.Unknown;
        public HistoryCoverageAnchor? RemoteBackwardFrontier { get; set; }
        public HistoryCoverageAnchor? RemoteForwardFrontier { get; set; }
        public HistoryCoverageAnchor? CanonicalOldest { get; set; }
        public HistoryCoverageAnchor? CanonicalNewest { get; set; }
        public HistoryCoverageAnchor? ProjectedOldest { get; set; }
        public HistoryCoverageAnchor? ProjectedNewest { get; set; }
        public HistoryCoverageRequestState? PendingRequest { get; set; }
        public string? LastPaginationResult { get; set; }
        public int LocalPagesLoaded { get; set; }
        public int RemotePagesRequested { get; set; }
        public int RemoteRowsAccepted { get; set; }
        public int RemoteRowsDeduplicated { get; set; }
        public int CoalescedRequests { get; set; }
        public int ZeroProgressTerminations { get; set; }

        public void AddWindow(HistoryCoverageWindow window)
        {
            LocalWindows.Add(window);
            if (LocalWindows.Count > MaximumWindows)
            {
                LocalWindows.RemoveAt(0);
            }
        }

        public HistoryCoverageSnapshot Snapshot() => new(
            Key,
            State,
            LocalWindows.ToArray(),
            LocalBeginningReached,
            RemoteExhausted,
            NoProgressTerminated,
            RemoteBackwardFrontier,
            PendingRequest,
            LastPaginationResult,
            LocalPagesLoaded,
            RemotePagesRequested,
            RemoteRowsAccepted,
            RemoteRowsDeduplicated,
            CoalescedRequests,
            ZeroProgressTerminations)
        {
            LocalNewerAvailable = LocalNewerAvailable,
            LocalEndReached = LocalEndReached,
            RemoteBackwardExhausted = RemoteExhausted,
            RemoteForwardExhausted = RemoteForwardExhausted,
            ForwardNoProgressTerminated = ForwardNoProgressTerminated,
            RemoteForwardFrontier = RemoteForwardFrontier,
            CanonicalOldest = CanonicalOldest,
            CanonicalNewest = CanonicalNewest,
            ProjectedOldest = ProjectedOldest,
            ProjectedNewest = ProjectedNewest,
            PendingDirection = PendingRequest?.Direction
        };
    }
}
