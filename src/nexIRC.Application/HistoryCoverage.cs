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
    NoProgress
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
    int ConnectionGeneration);

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
    int ZeroProgressTerminations);

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
                foreach (var record in ordered.TakeLast(MaximumConversationEntries))
                {
                    entry.LocalServerIds.Add(ConversationEntryIdentity.NormalizeServerMessageId(record.ServerMessageId));
                }
                entry.LocalPagesLoaded++;
                entry.LocalBeginningReached = !hasOlder;
                if (entry.RemoteBackwardFrontier is null)
                {
                    entry.State = entry.LocalBeginningReached ? HistoryCoverageState.LocalBeginningReached : HistoryCoverageState.LocalData;
                }
            }
            else if (!hasOlder)
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
            if (frontier.NetworkId != key.NetworkId
                || !string.Equals(frontier.Conversation, key.Conversation, StringComparison.Ordinal)
                || frontier.ConnectionGeneration != connectionGeneration)
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
            if (entry.PendingRequest is not null && entry.PendingRequest.ConnectionGeneration != connectionGeneration)
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
            if (frontier is not null)
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
            else if (frontier is null)
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

    private sealed class CoverageEntry(HistoryCoverageKey key)
    {
        public HistoryCoverageKey Key { get; } = key;
        public List<HistoryCoverageWindow> LocalWindows { get; } = [];
        public HashSet<string?> LocalServerIds { get; } = [];
        public bool LocalBeginningReached { get; set; }
        public bool RemoteExhausted { get; set; }
        public bool NoProgressTerminated { get; set; }
        public int Generation { get; set; }
        public HistoryCoverageState State { get; set; } = HistoryCoverageState.Unknown;
        public HistoryCoverageAnchor? RemoteBackwardFrontier { get; set; }
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
            ZeroProgressTerminations);
    }
}
