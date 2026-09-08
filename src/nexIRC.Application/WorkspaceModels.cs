using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum WorkspaceViewKind
{
    ServerStatus,
    Channel,
    Query,
    Whois,
    ChannelList,
    BanList
}

public enum WorkspaceActivity
{
    None,
    Unread,
    Important
}

public enum ConversationLifecycleState
{
    HistoricalOnly,
    Joining,
    Joined,
    Parted,
    Kicked,
    Active,
    Disconnected
}

public enum TranscriptEntryKind
{
    Message,
    Notice,
    Action,
    OutgoingMessage,
    OutgoingPrivateMessage,
    OutgoingAction,
    OutgoingNotice,
    Ctcp,
    OutgoingCtcp,
    Capability,
    Authentication,
    Registration,
    Informational,
    List,
    Whois,
    Reconnect,
    Join,
    Part,
    Quit,
    Kick,
    Nick,
    Topic,
    Mode,
    Connection,
    System,
    Error,
    Motd,
    Numeric
}

public enum NetworkDisplayState
{
    Disconnected,
    Connecting,
    TlsNegotiation,
    Connected,
    CapNegotiation,
    Registering,
    Registered,
    Disconnecting,
    ReconnectWaiting,
    Failed
}

public sealed class WorkspaceActivityEventArgs : EventArgs
{
    public WorkspaceActivityEventArgs(Guid networkId, Guid viewId, WorkspaceActivity activity, TranscriptEntry entry)
    {
        NetworkId = networkId;
        ViewId = viewId;
        Activity = activity;
        Entry = entry;
    }

    public Guid NetworkId { get; }

    public Guid ViewId { get; }

    public WorkspaceActivity Activity { get; }

    public TranscriptEntry Entry { get; }
}

public sealed record TranscriptEntry(
    DateTimeOffset Timestamp,
    TranscriptEntryKind Kind,
    string? Sender,
    string Text,
    string? Metadata = null,
    long Sequence = 0,
    DateTimeOffset? ReceivedAt = null)
{
    /// <summary>Durable server identity when IRC supplied one.</summary>
    public string? ServerMessageId { get; init; }

    /// <summary>Origin used by the shared history/transcript boundary.</summary>
    public ConversationEntryProvenance Provenance { get; init; } = ConversationEntryProvenance.Live;

    public ConversationTimestampSource TimestampSource { get; init; } = ConversationTimestampSource.LegacyOrLocalReceiveTime;

    public string? BatchId { get; init; }

    /// <summary>Temporary presentation marker used by explicit history jumps.</summary>
    public bool IsNavigationAnchor { get; init; }

    public string DisplayTime => Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    public bool IsOutgoing => Kind is TranscriptEntryKind.OutgoingMessage
        or TranscriptEntryKind.OutgoingPrivateMessage
        or TranscriptEntryKind.OutgoingAction
        or TranscriptEntryKind.OutgoingNotice
        or TranscriptEntryKind.OutgoingCtcp;

    public bool IsHighlight => string.Equals(Metadata, "highlight", StringComparison.Ordinal);

    public string DisplaySender => string.IsNullOrWhiteSpace(Sender) ? string.Empty : $"<{Sender}>";

    public string DisplayLine => Kind switch
    {
        TranscriptEntryKind.OutgoingAction => $"* {Sender} {Text}",
        TranscriptEntryKind.Action => $"* {Sender} {Text}",
        TranscriptEntryKind.OutgoingPrivateMessage => $"→ {Sender}: {Text}",
        TranscriptEntryKind.OutgoingNotice => $"→ -{Sender}- {Text}",
        TranscriptEntryKind.OutgoingCtcp => $"→ [CTCP {Text}]",
        TranscriptEntryKind.Ctcp => string.IsNullOrWhiteSpace(Sender) ? $"[CTCP {Text}]" : $"[{Sender} CTCP {Text}]",
        TranscriptEntryKind.OutgoingMessage => $"→ {Text}",
        _ => string.IsNullOrWhiteSpace(Sender) ? Text : $"<{Sender}> {Text}"
    };
}

public sealed record HistoryContextEntry(ConversationLogRecord Record, bool IsMatch = false)
{
    public string DisplayTime => Record.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    public string DisplayLine => string.IsNullOrWhiteSpace(Record.Sender)
        ? Record.Text
        : $"<{Record.Sender}> {Record.Text}";
}

public sealed record NetworkConnectionOptions
{
    public Guid? ProfileId { get; init; }

    public required string DisplayName { get; init; }

    public required IrcEndpoint Endpoint { get; init; }

    public required string Nickname { get; init; }

    public string Username { get; init; } = "nexirc";

    public string RealName { get; init; } = "nexIRC 5";

    public string? AlternateNickname { get; init; }

    public IReadOnlyList<string> NicknameFallbacks { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequestedCapabilities { get; init; } = IrcCapabilityCatalog.PreferredPhase1Y;

    public int MaximumChathistoryRequestSize { get; init; } = ChathistorySupport.DefaultClientMaximumRequestSize;

    public TimeSpan ChathistoryRequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public IReadOnlySet<string> DesiredChannels { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool AutoConnect { get; init; }

    public ReconnectPolicy Reconnect { get; init; } = new();

    public ServerProfileSet? Profiles { get; init; }

    public string? ManualNetworkName { get; init; }

    public IrcdFamily? ManualIrcd { get; init; }

    public ISaslCredentialProvider? SaslCredentialProvider { get; init; }

    public IServerPasswordProvider? PasswordProvider { get; init; }

    public IReadOnlyList<ISaslMechanism> SaslMechanisms { get; init; } = [new SaslPlainMechanism()];

    public SaslAuthenticationPolicy SaslPolicy { get; init; } = SaslAuthenticationPolicy.Disabled;

    public ServerSessionOptions ToSessionOptions(Guid? networkId = null) => new()
    {
        NetworkId = networkId,
        Endpoint = Endpoint,
        Nickname = Nickname,
        Username = Username,
        RealName = RealName,
        AlternateNickname = AlternateNickname,
        NicknameFallbacks = NicknameFallbacks,
        RequestedCapabilities = RequestedCapabilities,
        MaximumChathistoryRequestSize = MaximumChathistoryRequestSize,
        ChathistoryRequestTimeout = ChathistoryRequestTimeout,
        DesiredChannels = DesiredChannels,
        Reconnect = Reconnect,
        Profiles = Profiles,
        ManualNetworkName = ManualNetworkName,
        ManualIrcd = ManualIrcd,
        SaslCredentialProvider = SaslCredentialProvider,
        PasswordProvider = PasswordProvider,
        SaslMechanisms = SaslMechanisms,
        SaslPolicy = SaslPolicy
    };
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public abstract class WorkspaceView : ObservableObject
{
    public const int MaximumEntries = 500;

    private readonly object _entriesGate = new();
    private WorkspaceActivity _activity;
    private int _unreadCount;
    private int _importantCount;
    private int _highlightCount;
    private bool _recoveredUnread;
    private int _recoveredHistoryCount;
    private bool _isActive;
    private bool _isViewOpen = true;
    private bool _isLoadingOlderHistory;
    private bool _isLoadingNewerHistory;
    private bool _isViewingHistory;
    private bool _isFollowingLive = true;
    private bool _hasNewerLiveMessages;
    private TranscriptEntry? _navigationAnchor;
    private HistoryCoverageSnapshot? _historyCoverage;
    private ConversationLifecycleState _lifecycleState = ConversationLifecycleState.HistoricalOnly;
    private DateTimeOffset _lastActivity;
    private long _lastActivitySequence;
    private string? _historyContextMatch;
    private string _title;

    protected WorkspaceView(Guid networkId, Guid id, WorkspaceViewKind kind, string title)
    {
        NetworkId = networkId;
        Id = id;
        Kind = kind;
        _title = title;
    }

    public Guid NetworkId { get; }

    public Guid Id { get; }

    public WorkspaceViewKind Kind { get; }

    public string Title => _title;

    protected void SetTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (SetProperty(ref _title, title))
        {
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string DisplayLabel => Activity switch
    {
        WorkspaceActivity.Important => $"! {Title}",
        WorkspaceActivity.Unread => $"• {Title}",
        _ when RecoveredUnread => $"◌ {Title}",
        _ => Title
    };

    public WorkspaceActivity Activity
    {
        get => _activity;
        private set
        {
            if (SetProperty(ref _activity, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    /// <summary>
    /// Saturated count of live, unread activity. Historical inspection and
    /// synchronization projections never change this value.
    /// </summary>
    public int UnreadCount => _unreadCount;

    /// <summary>
    /// Saturated count of unread activity classified as important. This
    /// includes highlights, private messages, errors, and notices.
    /// </summary>
    public int ImportantCount => _importantCount;

    /// <summary>
    /// Saturated count of unread channel mentions.
    /// </summary>
    public int HighlightCount => _highlightCount;

    public bool HasUnread => _unreadCount > 0;

    public bool IsImportant => _importantCount > 0;

    public bool RecoveredUnread => _recoveredUnread;

    public int RecoveredHistoryCount => _recoveredHistoryCount;

    public DateTimeOffset LastActivity
    {
        get => _lastActivity;
        private set => SetProperty(ref _lastActivity, value);
    }

    public long LastActivitySequence
    {
        get => _lastActivitySequence;
        private set => SetProperty(ref _lastActivitySequence, value);
    }

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    public bool IsViewOpen
    {
        get => _isViewOpen;
        private set => SetProperty(ref _isViewOpen, value);
    }

    public bool IsLoadingOlderHistory
    {
        get => _isLoadingOlderHistory;
        internal set
        {
            if (SetProperty(ref _isLoadingOlderHistory, value))
            {
                OnPropertyChanged(nameof(OlderHistoryStatus));
            }
        }
    }

    public bool IsLoadingNewerHistory
    {
        get => _isLoadingNewerHistory;
        internal set
        {
            if (SetProperty(ref _isLoadingNewerHistory, value))
            {
                OnPropertyChanged(nameof(NewerHistoryStatus));
            }
        }
    }

    public bool IsViewingHistory
    {
        get => _isViewingHistory;
        private set
        {
            if (SetProperty(ref _isViewingHistory, value))
            {
                OnPropertyChanged(nameof(NavigationStatus));
            }
        }
    }

    public bool IsFollowingLive
    {
        get => _isFollowingLive;
        private set => SetProperty(ref _isFollowingLive, value);
    }

    public bool HasNewerLiveMessages
    {
        get => _hasNewerLiveMessages;
        private set => SetProperty(ref _hasNewerLiveMessages, value);
    }

    public TranscriptEntry? NavigationAnchor
    {
        get => _navigationAnchor;
        private set
        {
            if (SetProperty(ref _navigationAnchor, value))
            {
                OnPropertyChanged(nameof(NavigationAnchorIdentity));
            }
        }
    }

    public string? NavigationAnchorIdentity => NavigationAnchor?.ServerMessageId;

    public HistoryCoverageSnapshot? HistoryCoverage => _historyCoverage;

    public string OlderHistoryStatus => IsLoadingOlderHistory
        ? "Loading older history…"
        : _historyCoverage?.State switch
        {
            HistoryCoverageState.RemoteExhausted => "Beginning of server history reached",
            HistoryCoverageState.Unsupported => "Older history is local-only",
            HistoryCoverageState.NoProgress => "Older history is temporarily unavailable",
            HistoryCoverageState.LocalBeginningReached => "Local history exhausted",
            _ => "Load older messages"
        };

    public string NewerHistoryStatus => IsLoadingNewerHistory
        ? "Loading newer history…"
        : HistoryCoverage?.RemoteForwardExhausted == true
            ? "Latest server history reached"
            : "Load newer messages";

    public string NavigationStatus => IsViewingHistory
        ? HasNewerLiveMessages ? "Viewing history · newer live messages available" : "Viewing history"
        : "Following latest";

    public ConversationLifecycleState LifecycleState
    {
        get => _lifecycleState;
        private set
        {
            if (SetProperty(ref _lifecycleState, value))
            {
                OnPropertyChanged(nameof(LifecycleText));
                OnPropertyChanged(nameof(StateMarker));
            }
        }
    }

    public string LifecycleText => LifecycleState switch
    {
        ConversationLifecycleState.Joining => "joining",
        ConversationLifecycleState.Joined => "joined",
        ConversationLifecycleState.Parted => "parted",
        ConversationLifecycleState.Kicked => "kicked",
        ConversationLifecycleState.Active => "active",
        ConversationLifecycleState.Disconnected => "disconnected",
        _ => "historical"
    };

    public string StateMarker => LifecycleState switch
    {
        ConversationLifecycleState.Joining => "◐",
        ConversationLifecycleState.Joined or ConversationLifecycleState.Active => "●",
        ConversationLifecycleState.Disconnected => "◌",
        ConversationLifecycleState.Parted or ConversationLifecycleState.Kicked => "○",
        _ => "◇"
    };

    public ThreadSafeObservableCollection<TranscriptEntry> Entries { get; } = [];

    public ThreadSafeObservableCollection<HistoryContextEntry> HistoryContext { get; } = [];

    public bool HasHistoryContext => HistoryContext.Count > 0;

    public string? HistoryContextMatch => _historyContextMatch;

    public IReadOnlyList<TranscriptEntry> EntriesSnapshot
    {
        get
        {
            lock (_entriesGate)
            {
                return Entries.ToArray();
            }
        }
    }

    internal (TranscriptEntry? Oldest, TranscriptEntry? Newest) EntryBoundariesSnapshot
    {
        get
        {
            lock (_entriesGate)
            {
                return Entries.Count == 0 ? (null, null) : (Entries[0], Entries[^1]);
            }
        }
    }

    public int EntryCount => Entries.Count;

    internal void Append(
        TranscriptEntry entry,
        bool markActivity = true,
        WorkspaceActivity? activity = null,
        bool updateLastActivity = true)
    {
        if (updateLastActivity)
        {
            LastActivity = entry.Timestamp;
            LastActivitySequence = entry.Sequence;
        }
        lock (_entriesGate)
        {
            Entries.Add(entry);
            while (Entries.Count > MaximumEntries)
            {
                Entries.RemoveAt(0);
            }
        }

        if (entry.Provenance == ConversationEntryProvenance.Live && IsViewingHistory)
        {
            HasNewerLiveMessages = true;
        }

        if (markActivity && !IsActive)
        {
            MarkActivity(activity ?? (entry.Kind is TranscriptEntryKind.Error or TranscriptEntryKind.Notice
                ? WorkspaceActivity.Important
                : WorkspaceActivity.Unread));
        }
    }

    /// <summary>
    /// Inserts a candidate using the canonical conversation order. Historical
    /// candidates are projection-only: they cannot mark activity or change
    /// the live last-activity cursor.
    /// </summary>
    internal bool AppendConversationCandidate(
        ConversationEntryCandidate candidate,
        TranscriptEntry entry,
        bool updateLastActivity = true)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(entry);
        if (candidate.IsHistorical)
        {
            entry = entry with { Provenance = candidate.Provenance };
        }

        return AppendConversationEntry(entry, candidate.IsHistorical, updateLastActivity);
    }

    internal bool AppendConversationEntry(
        TranscriptEntry entry,
        bool historical = false,
        bool updateLastActivity = true)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (updateLastActivity && !historical)
        {
            LastActivity = entry.Timestamp;
            LastActivitySequence = entry.Sequence;
        }

        lock (_entriesGate)
        {
            if (entry.ServerMessageId is { Length: > 0 } messageId
                && Entries.Any(existing => string.Equals(existing.ServerMessageId, messageId, StringComparison.Ordinal)))
            {
                return false;
            }

            if (Entries.Count == 0
                || CompareTranscriptEntries(Entries[^1], entry) <= 0)
            {
                Entries.Add(entry);
                while (Entries.Count > MaximumEntries)
                {
                    Entries.RemoveAt(historical ? Entries.Count - 1 : 0);
                }

                if (!historical && IsViewingHistory)
                {
                    HasNewerLiveMessages = true;
                }

                return true;
            }

            var entries = Entries.ToList();
            entries.Add(entry);
            entries.Sort(static (left, right) => CompareTranscriptEntries(left, right));
            if (entries.Count > MaximumEntries)
            {
                if (historical)
                {
                    entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
                }
                else
                {
                    entries.RemoveRange(0, entries.Count - MaximumEntries);
                }
            }

            using (WorkspaceProjectionBatch.Begin())
            {
                Entries.Clear();
                foreach (var ordered in entries)
                {
                    Entries.Add(ordered);
                }
            }
        }

        if (!historical && IsViewingHistory)
        {
            HasNewerLiveMessages = true;
        }

        return true;
    }

    internal bool AppendHistoryRecord(ConversationLogRecord record, ConversationEntryProvenance provenance)
    {
        var candidate = new ConversationEntryCandidate
        {
            Record = record,
            Provenance = provenance,
            Persist = provenance == ConversationEntryProvenance.ServerPlayback
        };
        return AppendConversationCandidate(candidate, ConversationHistoryProjection.ToTranscriptEntry(record, provenance), updateLastActivity: false);
    }

    /// <summary>
    /// Projects one bounded historical page in one collection mutation. The
    /// canonical records are sorted before insertion; no historical row may
    /// mark activity or mutate the live state projection.
    /// </summary>
    internal int AppendHistoryRecords(
        IEnumerable<ConversationLogRecord> records,
        ConversationEntryProvenance provenance = ConversationEntryProvenance.LocalHistory,
        bool preserveOlderWindow = true)
    {
        ArgumentNullException.ThrowIfNull(records);
        var additions = ConversationHistoryOrdering.OrderAscending(records)
            .Select(record => ConversationHistoryProjection.ToTranscriptEntry(record, provenance))
            .ToArray();
        if (additions.Length == 0)
        {
            return 0;
        }

        lock (_entriesGate)
        {
            var existingIdentities = Entries
                .Select(entry => ConversationEntryIdentity.GetServerIdentityKey(new ConversationLogRecord
                {
                    NetworkId = NetworkId,
                    ServerMessageId = entry.ServerMessageId
                }))
                .Where(static identity => identity is not null)
                .ToHashSet(StringComparer.Ordinal);
            var accepted = additions.Where(entry =>
            {
                var identity = ConversationEntryIdentity.GetServerIdentityKey(new ConversationLogRecord
                {
                    NetworkId = NetworkId,
                    ServerMessageId = entry.ServerMessageId
                });
                return identity is null || existingIdentities.Add(identity);
            }).ToArray();
            if (accepted.Length == 0)
            {
                return 0;
            }

            var merged = Entries.Concat(accepted)
                .OrderBy(entry => entry, Comparer<TranscriptEntry>.Create(CompareTranscriptEntries))
                .ToList();
            if (merged.Count > MaximumEntries)
            {
                if (preserveOlderWindow)
                {
                    merged.RemoveRange(MaximumEntries, merged.Count - MaximumEntries);
                }
                else
                {
                    merged.RemoveRange(0, merged.Count - MaximumEntries);
                }
            }

            using (WorkspaceProjectionBatch.Begin())
            {
                Entries.Clear();
                foreach (var entry in merged)
                {
                    Entries.Add(entry);
                }
            }

            return accepted.Length;
        }
    }

    internal void ReplaceHistoryWindow(
        IEnumerable<ConversationLogRecord> records,
        ConversationEntryProvenance provenance,
        ConversationLogRecord? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var projected = ConversationHistoryOrdering.OrderAscending(records)
            .TakeLast(MaximumEntries)
            .Select(record => ConversationHistoryProjection.ToTranscriptEntry(record, provenance))
            .ToArray();
        var anchorEntry = projected.FirstOrDefault(entry => IsSameNavigationRecord(entry, anchor));
        lock (_entriesGate)
        {
            using (WorkspaceProjectionBatch.Begin())
            {
                Entries.Clear();
                foreach (var entry in projected)
                {
                    Entries.Add(entry with { IsNavigationAnchor = ReferenceEquals(entry, anchorEntry) });
                }
            }
        }

        NavigationAnchor = anchorEntry is null ? null : anchorEntry with { IsNavigationAnchor = true };
    }

    internal void SetNavigationAnchor(ConversationLogRecord? anchor)
    {
        lock (_entriesGate)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                var entry = Entries[index];
                Entries[index] = entry with { IsNavigationAnchor = IsSameNavigationRecord(entry, anchor) };
            }

            NavigationAnchor = Entries.FirstOrDefault(entry => IsSameNavigationRecord(entry, anchor));
        }
    }

    public void EnterHistoryView()
    {
        IsViewingHistory = true;
        IsFollowingLive = false;
        OnPropertyChanged(nameof(NavigationStatus));
    }

    public void SetLiveFollow(bool following)
    {
        IsFollowingLive = following;
        IsViewingHistory = !following;
        if (following)
        {
            HasNewerLiveMessages = false;
        }
    }

    internal void ReturnToLatest()
    {
        NavigationAnchor = null;
        lock (_entriesGate)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                if (Entries[index].IsNavigationAnchor)
                {
                    Entries[index] = Entries[index] with { IsNavigationAnchor = false };
                }
            }
        }

        HasNewerLiveMessages = false;
        IsViewingHistory = false;
        IsFollowingLive = true;
        OnPropertyChanged(nameof(NavigationStatus));
    }

    private static bool IsSameNavigationRecord(TranscriptEntry entry, ConversationLogRecord? record)
    {
        if (record is null)
        {
            return false;
        }

        return record.ServerMessageId is { Length: > 0 }
            ? string.Equals(entry.ServerMessageId, record.ServerMessageId, StringComparison.Ordinal)
            : entry.Timestamp == record.Timestamp && entry.Sequence == record.DurableSequence;
    }

    internal void SetHistoryCoverage(HistoryCoverageSnapshot coverage, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        _historyCoverage = coverage;
        if (!notify)
        {
            return;
        }

        OnPropertyChanged(nameof(HistoryCoverage));
        OnPropertyChanged(nameof(OlderHistoryStatus));
    }

    private static int CompareTranscriptEntries(TranscriptEntry left, TranscriptEntry right)
    {
        // For two local-delivery entries, the manager's protocol sequence is
        // the safer clock-regression tie breaker.  A valid server timestamp
        // remains authoritative whenever either side carries one.
        if (left.TimestampSource != ConversationTimestampSource.ServerTime
            && right.TimestampSource != ConversationTimestampSource.ServerTime)
        {
            var localSequence = left.Sequence.CompareTo(right.Sequence);
            if (localSequence != 0)
            {
                return localSequence;
            }
        }

        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        if (timestamp != 0) return timestamp;
        var sequence = left.Sequence.CompareTo(right.Sequence);
        if (sequence != 0) return sequence;
        var id = string.Compare(left.ServerMessageId, right.ServerMessageId, StringComparison.Ordinal);
        if (id != 0) return id;
        var sender = string.Compare(left.Sender, right.Sender, StringComparison.Ordinal);
        return sender != 0 ? sender : string.Compare(left.Text, right.Text, StringComparison.Ordinal);
    }

    public void MarkActivity(WorkspaceActivity activity)
    {
        if (activity == WorkspaceActivity.None)
        {
            return;
        }

        IncrementCounter(ref _unreadCount, nameof(UnreadCount));
        if (activity == WorkspaceActivity.Important)
        {
            IncrementCounter(ref _importantCount, nameof(ImportantCount));
        }

        RefreshActivity();
    }

    internal void MarkHighlight()
    {
        IncrementCounter(ref _highlightCount, nameof(HighlightCount));
    }

    private void IncrementCounter(ref int counter, string propertyName)
    {
        if (counter < ConfigurationLimits.MaximumUnreadCount)
        {
            counter++;
            OnPropertyChanged(propertyName);
        }
    }

    private void RefreshActivity()
    {
        Activity = _importantCount > 0
            ? WorkspaceActivity.Important
            : _unreadCount > 0
                ? WorkspaceActivity.Unread
                : WorkspaceActivity.None;
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(IsImportant));
    }

    public void MarkRead()
    {
        _unreadCount = 0;
        _importantCount = 0;
        _highlightCount = 0;
        _recoveredUnread = false;
        _recoveredHistoryCount = 0;
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(ImportantCount));
        OnPropertyChanged(nameof(HighlightCount));
        OnPropertyChanged(nameof(RecoveredUnread));
        OnPropertyChanged(nameof(RecoveredHistoryCount));
        RefreshActivity();
    }

    internal void MarkRecoveredHistory(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _recoveredUnread = true;
        _recoveredHistoryCount = Math.Min(ConfigurationLimits.MaximumUnreadCount, _recoveredHistoryCount + count);
        OnPropertyChanged(nameof(RecoveredUnread));
        OnPropertyChanged(nameof(RecoveredHistoryCount));
        OnPropertyChanged(nameof(DisplayLabel));
    }

    public void ClearEntries()
    {
        lock (_entriesGate)
        {
            Entries.Clear();
            HistoryContext.Clear();
        }

        _historyContextMatch = null;
        OnPropertyChanged(nameof(HasHistoryContext));
        OnPropertyChanged(nameof(HistoryContextMatch));
    }

    public void ClearHistoryContext()
    {
        lock (_entriesGate)
        {
            HistoryContext.Clear();
        }

        _historyContextMatch = null;
        OnPropertyChanged(nameof(HasHistoryContext));
        OnPropertyChanged(nameof(HistoryContextMatch));
    }

    public void Activate()
    {
        IsActive = true;
        MarkRead();
    }

    internal void Deactivate() => IsActive = false;

    internal void CloseView()
    {
        IsViewOpen = false;
        Deactivate();
    }

    internal void ReopenView() => IsViewOpen = true;

    internal void SetLifecycleState(ConversationLifecycleState state) => LifecycleState = state;

    public void SetHistoryContext(IEnumerable<HistoryContextEntry> entries, string? match)
    {
        lock (_entriesGate)
        {
            HistoryContext.Clear();
            foreach (var entry in entries.Take(ConfigurationLimits.MaximumHistoryContextEntries))
            {
                HistoryContext.Add(entry);
            }
        }

        _historyContextMatch = match;
        OnPropertyChanged(nameof(HasHistoryContext));
        OnPropertyChanged(nameof(HistoryContextMatch));
    }

    public override string ToString() => DisplayLabel;
}

public sealed class ServerStatusView : WorkspaceView
{
    private NetworkDisplayState _connectionState = NetworkDisplayState.Disconnected;
    private string? _networkName;
    private string _endpointText = string.Empty;
    private string _capabilitiesText = "(none negotiated)";

    internal ServerStatusView(Guid networkId, Guid id, string title, IrcEndpoint endpoint)
        : base(networkId, id, WorkspaceViewKind.ServerStatus, title)
    {
        _endpointText = endpoint.ToString();
    }

    public NetworkDisplayState ConnectionState
    {
        get => _connectionState;
        internal set => SetProperty(ref _connectionState, value);
    }

    public string? NetworkName
    {
        get => _networkName;
        internal set => SetProperty(ref _networkName, value);
    }

    public string EndpointText
    {
        get => _endpointText;
        private set => SetProperty(ref _endpointText, value);
    }

    public string StateText => ConnectionState.ToString();

    public string CapabilitiesText => _capabilitiesText;

    internal void ApplySnapshot(ServerSessionSnapshot snapshot)
    {
        ConnectionState = ToDisplayState(snapshot.State);
        NetworkName = snapshot.Features.NetworkName ?? snapshot.Identity.NetworkName;
        EndpointText = snapshot.Endpoint.ToString();
        var capabilitiesText = snapshot.Capabilities.Enabled.Count == 0
            ? "(none negotiated)"
            : string.Join(Environment.NewLine, snapshot.Capabilities.Enabled.OrderBy(static capability => capability, StringComparer.Ordinal));
        if (!string.Equals(_capabilitiesText, capabilitiesText, StringComparison.Ordinal))
        {
            _capabilitiesText = capabilitiesText;
            OnPropertyChanged(nameof(CapabilitiesText));
        }
        OnPropertyChanged(nameof(StateText));
    }

    internal static NetworkDisplayState ToDisplayState(ServerSessionState state) => state switch
    {
        ServerSessionState.Connecting => NetworkDisplayState.Connecting,
        ServerSessionState.TlsNegotiation => NetworkDisplayState.TlsNegotiation,
        ServerSessionState.Connected => NetworkDisplayState.Connected,
        ServerSessionState.CapNegotiation => NetworkDisplayState.CapNegotiation,
        ServerSessionState.Registering => NetworkDisplayState.Registering,
        ServerSessionState.Registered => NetworkDisplayState.Registered,
        ServerSessionState.Disconnecting => NetworkDisplayState.Disconnecting,
        ServerSessionState.ReconnectWaiting => NetworkDisplayState.ReconnectWaiting,
        ServerSessionState.Failed => NetworkDisplayState.Failed,
        _ => NetworkDisplayState.Disconnected
    };
}

public sealed class ChannelMemberView : ObservableObject
{
    private string _prefixText = string.Empty;
    private string? _username;
    private string? _host;
    private string? _account;
    private string? _realName;
    private bool _isAway;
    private string? _awayReason;
    private IReadOnlySet<char> _prefixModes = new HashSet<char>();

    internal ChannelMemberView(string nickname)
    {
        Nickname = nickname;
    }

    public string Nickname { get; }

    public string? Username
    {
        get => _username;
        private set => SetProperty(ref _username, value);
    }

    public string? Host
    {
        get => _host;
        private set => SetProperty(ref _host, value);
    }

    public string? Account
    {
        get => _account;
        private set => SetProperty(ref _account, value);
    }

    public string? RealName
    {
        get => _realName;
        private set => SetProperty(ref _realName, value);
    }

    public bool IsAway
    {
        get => _isAway;
        private set => SetProperty(ref _isAway, value);
    }

    public string? AwayReason
    {
        get => _awayReason;
        private set => SetProperty(ref _awayReason, value);
    }

    public string? Hostmask => Username is null || Host is null ? null : $"*!{Username}@{Host}";

    public string PrefixText
    {
        get => _prefixText;
        private set => SetProperty(ref _prefixText, value);
    }

    public IReadOnlySet<char> PrefixModes => _prefixModes;

    public string DisplayText => $"{PrefixText}{Nickname}";

    public string DetailsText
    {
        get
        {
            var details = new List<string> { Nickname };
            if (!string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Host))
            {
                details.Add($"{Username ?? "?"}@{Host ?? "?"}");
            }

            if (!string.IsNullOrWhiteSpace(Account)) details.Add($"account: {Account}");
            if (!string.IsNullOrWhiteSpace(RealName)) details.Add($"real name: {RealName}");
            details.Add(IsAway
                ? string.IsNullOrWhiteSpace(AwayReason) ? "away" : $"away: {AwayReason}"
                : "online");
            return string.Join(Environment.NewLine, details);
        }
    }

    internal void Apply(IrcChannelMemberSnapshot snapshot, IrcPrefixGrammar? grammar)
    {
        Username = snapshot.Username;
        Host = snapshot.Host;
        Account = snapshot.Account;
        RealName = snapshot.RealName;
        IsAway = snapshot.IsAway;
        AwayReason = snapshot.AwayReason;
        _prefixModes = new HashSet<char>(snapshot.PrefixModes);
        PrefixText = HighestPrefix(snapshot.PrefixModes, grammar);
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(DetailsText));
    }

    internal bool IsEquivalent(IrcChannelMemberSnapshot snapshot, IrcPrefixGrammar? grammar)
    {
        var prefixText = HighestPrefix(snapshot.PrefixModes, grammar);
        return string.Equals(Username, snapshot.Username, StringComparison.Ordinal)
            && string.Equals(Host, snapshot.Host, StringComparison.Ordinal)
            && string.Equals(Account, snapshot.Account, StringComparison.Ordinal)
            && string.Equals(RealName, snapshot.RealName, StringComparison.Ordinal)
            && IsAway == snapshot.IsAway
            && string.Equals(AwayReason, snapshot.AwayReason, StringComparison.Ordinal)
            && string.Equals(PrefixText, prefixText, StringComparison.Ordinal)
            && PrefixModes.SetEquals(snapshot.PrefixModes);
    }

    internal static string HighestPrefix(IEnumerable<char> modes, IrcPrefixGrammar? grammar)
    {
        var modeSet = modes.ToHashSet();
        if (modeSet.Count == 0)
        {
            return string.Empty;
        }

        var selectedMode = default(char);
        if (grammar is not null)
        {
            foreach (var mode in grammar.Modes)
            {
                if (modeSet.Contains(mode))
                {
                    selectedMode = mode;
                    break;
                }
            }
        }

        if (selectedMode != default)
        {
            return grammar!.ModeToPrefix.TryGetValue(selectedMode, out var prefix) ? prefix.ToString() : selectedMode.ToString();
        }

        return modeSet.OrderBy(static mode => mode).First().ToString();
    }
}

public sealed record ChannelModeProjection(
    char Mode,
    string DisplayName,
    IrcChannelModeKind Kind,
    bool IsActive,
    string ParameterText,
    bool IsEditable)
{
    public string AutomationId => $"ChannelMode.{Mode}";

    public bool IsParameterMode => Kind is IrcChannelModeKind.List or IrcChannelModeKind.ParameterAlways or IrcChannelModeKind.ParameterWhenSet;
}

public sealed class ChannelView : WorkspaceView
{
    private readonly object _membersGate = new();
    private bool _isJoined;
    private bool _isStale;
    private string? _topic;
    private ChannelSynchronizationState _synchronization;
    private string _modeSummary = string.Empty;
    private IReadOnlySet<char> _localPrefixModes = new HashSet<char>();
    private string _localPrefixText = string.Empty;
    private IReadOnlySet<char> _modes = new HashSet<char>();
    private IReadOnlyDictionary<char, IReadOnlyList<string>> _modeParameters = new Dictionary<char, IReadOnlyList<string>>();
    private IReadOnlyList<ChannelModeProjection> _modeProjections = Array.Empty<ChannelModeProjection>();
    private string? _topicSetter;
    private DateTimeOffset? _topicSetAt;
    private bool _localMemberKnown;

    internal ChannelView(Guid networkId, Guid id, string channel)
        : base(networkId, id, WorkspaceViewKind.Channel, channel)
    {
        Channel = channel;
    }

    public string Channel { get; }

    public bool IsJoined
    {
        get => _isJoined;
        private set => SetProperty(ref _isJoined, value);
    }

    public bool IsStale
    {
        get => _isStale;
        private set => SetProperty(ref _isStale, value);
    }

    public string? Topic
    {
        get => _topic;
        private set => SetProperty(ref _topic, value);
    }

    public string TopicText => string.IsNullOrWhiteSpace(Topic) ? "(no topic)" : Topic!;

    public string? TopicSetter
    {
        get => _topicSetter;
        private set => SetProperty(ref _topicSetter, value);
    }

    public DateTimeOffset? TopicSetAt
    {
        get => _topicSetAt;
        private set => SetProperty(ref _topicSetAt, value);
    }

    public string TopicMetadataText => TopicSetter is null
        ? "setter/time unknown"
        : $"set by {TopicSetter}{(TopicSetAt is null ? string.Empty : $" at {TopicSetAt:yyyy-MM-dd HH:mm:ss} UTC")}";

    public ChannelSynchronizationState Synchronization
    {
        get => _synchronization;
        private set => SetProperty(ref _synchronization, value);
    }

    public string SynchronizationText => IsStale ? "restoring" : Synchronization.ToString();

    public string ModeSummary
    {
        get => _modeSummary;
        private set => SetProperty(ref _modeSummary, value);
    }

    public IReadOnlySet<char> Modes => _modes;

    public IReadOnlyDictionary<char, IReadOnlyList<string>> ModeParameters => _modeParameters;

    public IReadOnlyList<ChannelModeProjection> ModeProjections => _modeProjections;

    public bool IsLocalMemberKnown => _localMemberKnown;

    public string ApparentPrivilegeText => !IsJoined
        ? "not joined"
        : !_localMemberKnown
            ? "unknown/server-dependent"
            : string.IsNullOrEmpty(LocalPrefixText) ? "ordinary member" : LocalPrefixText;

    public IReadOnlySet<char> LocalPrefixModes => _localPrefixModes;

    public string LocalPrefixText
    {
        get => _localPrefixText;
        private set => SetProperty(ref _localPrefixText, value);
    }

    /// <summary>
    /// Uses the server-advertised PREFIX ordering and treats every rank above
    /// the final (normally voice) rank as moderation-capable.
    /// </summary>
    public bool CanModerate { get; private set; }

    public bool IsConnected => LifecycleState is ConversationLifecycleState.Joined or ConversationLifecycleState.Active;

    public ThreadSafeObservableCollection<ChannelMemberView> Members { get; } = [];

    public IReadOnlyList<ChannelMemberView> MembersSnapshot
    {
        get
        {
            lock (_membersGate)
            {
                return Members.ToArray();
            }
        }
    }

    internal void ApplySnapshot(
        IrcChannelSnapshot? snapshot,
        IrcPrefixGrammar? grammar,
        IrcChannelModeGrammar? channelModes = null,
        string? localNickname = null,
        IrcCaseMapping mapping = IrcCaseMapping.Rfc1459,
        bool networkAvailable = true,
        bool isDesired = true)
    {
        if (snapshot is null)
        {
            IsJoined = false;
            IsStale = true;
            SetLifecycleState(networkAvailable && !isDesired && LifecycleState is not ConversationLifecycleState.HistoricalOnly
                ? ConversationLifecycleState.Parted
                : networkAvailable && !isDesired ? ConversationLifecycleState.HistoricalOnly : ConversationLifecycleState.Disconnected);
            Synchronization = ChannelSynchronizationState.NotRequested;
            Topic = null;
            TopicSetter = null;
            TopicSetAt = null;
            lock (_membersGate)
            {
                Members.Clear();
            }
            ModeSummary = string.Empty;
            _localPrefixModes = new HashSet<char>();
            LocalPrefixText = string.Empty;
            CanModerate = false;
            _modes = new HashSet<char>();
            _modeParameters = new Dictionary<char, IReadOnlyList<string>>();
            _modeProjections = Array.Empty<ChannelModeProjection>();
            _localMemberKnown = false;
        }
        else
        {
            IsJoined = snapshot.IsJoined;
            IsStale = snapshot.IsStale;
            var retainedLifecycle = LifecycleState is ConversationLifecycleState.Parted or ConversationLifecycleState.Kicked;
            SetLifecycleState(snapshot.IsJoined
                ? ConversationLifecycleState.Joined
                : snapshot.IsStale || !networkAvailable
                    ? ConversationLifecycleState.Disconnected
                    : snapshot.Synchronization is ChannelSynchronizationState.Joining or ChannelSynchronizationState.Synchronizing
                        ? ConversationLifecycleState.Joining
                        : retainedLifecycle && isDesired
                            ? LifecycleState
                            : !isDesired && LifecycleState is not ConversationLifecycleState.HistoricalOnly ? ConversationLifecycleState.Parted : ConversationLifecycleState.HistoricalOnly);
            Synchronization = snapshot.Synchronization;
            Topic = snapshot.Topic;
            TopicSetter = snapshot.TopicSetter;
            TopicSetAt = snapshot.TopicSetAt;
            _modes = new HashSet<char>(snapshot.Modes);
            _modeParameters = snapshot.ModeParameters.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray());
            ModeSummary = new string(snapshot.Modes.OrderBy(static mode => mode).ToArray());
            var projected = snapshot.Members.Values
                .OrderBy(member => PrefixRank(member, grammar))
                .ThenBy(static member => member.Nickname, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            lock (_membersGate)
            {
                var rebuild = Members.Count != projected.Length;
                if (!rebuild)
                {
                    for (var index = 0; index < projected.Length; index++)
                    {
                        if (!string.Equals(Members[index].Nickname, projected[index].Nickname, StringComparison.Ordinal))
                        {
                            rebuild = true;
                            break;
                        }
                    }
                }

                if (rebuild)
                {
                    Members.Clear();
                    foreach (var member in projected)
                    {
                        var view = new ChannelMemberView(member.Nickname);
                        view.Apply(member, grammar);
                        Members.Add(view);
                    }
                }
                else
                {
                    for (var index = 0; index < projected.Length; index++)
                    {
                        if (!Members[index].IsEquivalent(projected[index], grammar))
                        {
                            Members[index].Apply(projected[index], grammar);
                        }
                    }
                }
            }

            var localMember = localNickname is null
                ? null
                : snapshot.Members.Values.FirstOrDefault(member => IrcCaseMappingComparer.Equals(member.Nickname, localNickname, mapping));
            _localPrefixModes = localMember?.PrefixModes is { } localModes
                ? new HashSet<char>(localModes)
                : new HashSet<char>();
            _localMemberKnown = localMember is not null;
            LocalPrefixText = ChannelMemberView.HighestPrefix(_localPrefixModes, grammar);
            var moderationModes = grammar?.Modes.Count > 1
                ? grammar.Modes.Take(grammar.Modes.Count - 1).ToHashSet()
                : grammar?.Modes.ToHashSet() ?? new HashSet<char>();
            CanModerate = _localPrefixModes.Any(moderationModes.Contains);
            _modeProjections = BuildModeProjections(snapshot, channelModes);
        }

        OnPropertyChanged(nameof(TopicText));
        OnPropertyChanged(nameof(TopicMetadataText));
        OnPropertyChanged(nameof(SynchronizationText));
        OnPropertyChanged(nameof(ApparentPrivilegeText));
        OnPropertyChanged(nameof(ModeProjections));
    }

    private static ChannelModeProjection[] BuildModeProjections(IrcChannelSnapshot snapshot, IrcChannelModeGrammar? grammar)
    {
        var effective = grammar ?? IrcChannelModeGrammar.Default;
        var modes = effective.AllModes.Concat(snapshot.Modes).Distinct().OrderBy(static mode => mode).ToArray();
        return modes.Select(mode =>
        {
            var kind = effective.ListModes.Contains(mode) ? IrcChannelModeKind.List
                : effective.ParameterAlwaysModes.Contains(mode) ? IrcChannelModeKind.ParameterAlways
                : effective.ParameterWhenSetModes.Contains(mode) ? IrcChannelModeKind.ParameterWhenSet
                : effective.NoParameterModes.Contains(mode) ? IrcChannelModeKind.NoParameter
                : IrcChannelModeKind.Unknown;
            var active = snapshot.Modes.Contains(mode);
            var parameterText = mode == 'k' && active
                ? "set (hidden)"
                : snapshot.ModeParameters.TryGetValue(mode, out var values)
                    ? string.Join(", ", values)
                    : string.Empty;
            var displayName = mode is 'b' or 'e' or 'I' ? $"{PrivilegeModel.FriendlyName(mode)} list" : PrivilegeModel.FriendlyName(mode);
            return new ChannelModeProjection(mode, displayName, kind, active, parameterText, kind is IrcChannelModeKind.NoParameter or IrcChannelModeKind.ParameterAlways or IrcChannelModeKind.ParameterWhenSet);
        }).ToArray();
    }

    private static int PrefixRank(IrcChannelMemberSnapshot member, IrcPrefixGrammar? grammar)
    {
        if (grammar is null)
        {
            return int.MaxValue;
        }

        var rank = int.MaxValue;
        foreach (var mode in member.PrefixModes)
        {
            var index = 0;
            foreach (var advertisedMode in grammar.Modes)
            {
                if (advertisedMode == mode)
                {
                    rank = Math.Min(rank, index);
                    break;
                }

                index++;
            }
        }

        return rank;
    }
}

public sealed class QueryView : WorkspaceView
{
    private string _nickname;
    private readonly ConversationIdentityEvidenceLedger _identityEvidence;
    private readonly ConversationIdentityEvidenceLedger _historicalIdentityEvidence;

    internal QueryView(Guid networkId, Guid id, string nickname, string? historyConversationKey = null)
        : base(networkId, id, WorkspaceViewKind.Query, nickname)
    {
        _nickname = nickname;
        _identityEvidence = new ConversationIdentityEvidenceLedger(networkId);
        _historicalIdentityEvidence = new ConversationIdentityEvidenceLedger(networkId);
        HistoryConversationKey = historyConversationKey
            ?? ConversationLoggingService.BuildConversationKey(LogConversationKind.PrivateConversation, nickname);
    }

    public string Nickname => _nickname;

    /// <summary>
    /// The runtime logical identity is the view id. This key keeps JSONL
    /// history on the original, network-scoped conversation path while the
    /// visible peer nickname changes during an active session.
    /// </summary>
    public string HistoryConversationKey { get; }

    public int IdentityConnectionGeneration { get; private set; }

    public bool IsIdentityBoundToCurrentSession { get; private set; }

    /// <summary>
    /// Set only for a query that was open and had a real conversation before a
    /// reconnect.  A matching live target in the new generation may reuse it;
    /// a historical nickname by itself never clears an identity boundary.
    /// </summary>
    public bool CanReuseAfterReconnect { get; private set; }

    public ConversationIdentityEvidenceSnapshot IdentityEvidence => _identityEvidence.Snapshot();

    public ConversationIdentityEvidenceSnapshot HistoricalIdentityEvidence => _historicalIdentityEvidence.Snapshot();

    internal void ObserveIdentityEvidence(ConversationIdentityEvidence evidence)
    {
        if (evidence.IsHistorical)
        {
            _historicalIdentityEvidence.Observe(evidence);
        }
        else
        {
            _identityEvidence.Observe(evidence);
        }
    }

    internal bool HasAccountEvidence(string account) => IdentityEvidence.HasAccount(account);

    internal bool HasObservedNickname(string nickname, IrcCaseMapping mapping) =>
        IdentityEvidence.Nicknames.Any(item => IrcCaseMappingComparer.Equals(item, nickname, mapping));

    internal bool HasConflictingAccountEvidence => IdentityEvidence.HasConflictingAccounts;

    internal bool RebindNicknameFromStrongAccount(string nickname, IrcCaseMapping mapping, int connectionGeneration)
    {
        if (string.IsNullOrWhiteSpace(nickname)
            || IrcCaseMappingComparer.Equals(Nickname, nickname, mapping))
        {
            return false;
        }

        _nickname = nickname;
        SetTitle(nickname);
        IdentityConnectionGeneration = connectionGeneration;
        IsIdentityBoundToCurrentSession = true;
        CanReuseAfterReconnect = false;
        OnPropertyChanged(nameof(Nickname));
        return true;
    }

    internal bool CanApplyNicknameChange(string previousNickname, string newNickname, IrcCaseMapping mapping) =>
        IrcCaseMappingComparer.Equals(Nickname, previousNickname, mapping)
        && !IrcCaseMappingComparer.Equals(Nickname, newNickname, mapping);

    internal bool TryApplyNicknameChange(string previousNickname, string newNickname, IrcCaseMapping mapping, int connectionGeneration)
    {
        if (!IrcCaseMappingComparer.Equals(Nickname, previousNickname, mapping))
        {
            return false;
        }

        if (IrcCaseMappingComparer.Equals(Nickname, newNickname, mapping))
        {
            return true;
        }

        _nickname = newNickname;
        SetTitle(newNickname);
        IdentityConnectionGeneration = connectionGeneration;
        IsIdentityBoundToCurrentSession = true;
        CanReuseAfterReconnect = false;
        OnPropertyChanged(nameof(Nickname));
        OnPropertyChanged(nameof(HistoryConversationKey));
        return true;
    }

    internal void MarkIdentityBoundary(int connectionGeneration)
    {
        IdentityConnectionGeneration = connectionGeneration;
        IsIdentityBoundToCurrentSession = false;
    }

    internal void MarkReconnectCandidate() => CanReuseAfterReconnect = true;

    internal void MarkCurrentSessionIdentity(int connectionGeneration)
    {
        IdentityConnectionGeneration = connectionGeneration;
        IsIdentityBoundToCurrentSession = true;
        CanReuseAfterReconnect = false;
    }

    internal void ApplyConnectionState(bool connected)
    {
        SetLifecycleState(connected ? ConversationLifecycleState.Active : ConversationLifecycleState.Disconnected);
    }
}

public sealed class NetworkWorkspace : ObservableObject
{
    private string _displayName;
    private NetworkDisplayState _state = NetworkDisplayState.Disconnected;
    private string? _networkName;
    private ServerSessionSnapshot _snapshot;
    private ServerSession _session;
    private WorkspaceView? _activeView;

    internal NetworkWorkspace(Guid id, NetworkConnectionOptions options, ServerSession session)
    {
        Id = id;
        _displayName = options.DisplayName;
        Options = options;
        _session = session;
        StatusView = new ServerStatusView(id, Guid.NewGuid(), options.DisplayName, options.Endpoint);
        Views.Add(StatusView);
        _snapshot = session.Snapshot;
        ApplySnapshot(_snapshot);

        foreach (var channel in options.DesiredChannels)
        {
            EnsureChannel(channel);
        }
    }

    internal void ResetForNewSession(ServerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _snapshot = session.Snapshot;
        State = ServerStatusView.ToDisplayState(_snapshot.State);
        NetworkName = _snapshot.Features.NetworkName ?? _snapshot.Identity.NetworkName;
        StatusView.ApplySnapshot(_snapshot);
        foreach (var channel in Channels)
        {
            channel.ApplySnapshot(null, _snapshot.Features.Prefix, _snapshot.Features.ChannelModes, _snapshot.Nickname, _snapshot.Features.CaseMapping, networkAvailable: false, isDesired: true);
        }

        foreach (var query in Queries)
        {
            query.ApplyConnectionState(connected: false);
            query.MarkIdentityBoundary(session.Snapshot.ConnectionGeneration);
        }
    }

    public Guid Id { get; }

    public Guid? ProfileId => Options.ProfileId;

    public NetworkConnectionOptions Options { get; internal set; }

    public ServerSession Session
    {
        get => _session;
        internal set => SetProperty(ref _session, value);
    }

    public string DisplayName
    {
        get => _displayName;
        internal set => SetProperty(ref _displayName, value);
    }

    public string DisplayLabel => $"{DisplayName}  [{StateText}]";

    public NetworkDisplayState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    public string StateText => State.ToString();

    public string? NetworkName
    {
        get => _networkName;
        private set => SetProperty(ref _networkName, value);
    }

    public ServerSessionSnapshot Snapshot => _snapshot;

    public ServerStatusView StatusView { get; }

    public ThreadSafeObservableCollection<WorkspaceView> Views { get; } = [];

    public ThreadSafeObservableCollection<ChannelView> Channels { get; } = [];

    public ThreadSafeObservableCollection<QueryView> Queries { get; } = [];

    public ThreadSafeObservableCollection<WhoisView> WhoisViews { get; } = [];

    public ThreadSafeObservableCollection<ChannelListView> ChannelListViews { get; } = [];

    public ThreadSafeObservableCollection<BanListView> BanListViews { get; } = [];

    public WorkspaceView? ActiveView
    {
        get => _activeView;
        internal set => SetProperty(ref _activeView, value);
    }

    internal void ApplySnapshot(ServerSessionSnapshot snapshot)
    {
        if (snapshot.ConnectionGeneration < _snapshot.ConnectionGeneration)
        {
            return;
        }

        _snapshot = snapshot;
        State = ServerStatusView.ToDisplayState(snapshot.State);
        NetworkName = snapshot.Features.NetworkName ?? snapshot.Identity.NetworkName;
        StatusView.ApplySnapshot(snapshot);

        var networkAvailable = snapshot.State is not (ServerSessionState.Disconnected or ServerSessionState.Failed or ServerSessionState.ReconnectWaiting);
        foreach (var channel in snapshot.Channels)
        {
            var isDesired = snapshot.DesiredChannels.Any(item => IrcCaseMappingComparer.Equals(item, channel.Name, snapshot.Features.CaseMapping));
            EnsureChannel(channel.Name, reopen: false).ApplySnapshot(channel, snapshot.Features.Prefix, snapshot.Features.ChannelModes, snapshot.Nickname, snapshot.Features.CaseMapping, networkAvailable, isDesired);
        }

        foreach (var channel in Channels)
        {
            if (!snapshot.Channels.Any(item => IrcCaseMappingComparer.Equals(item.Name, channel.Channel, snapshot.Features.CaseMapping)) &&
                snapshot.DesiredChannels.All(item => !IrcCaseMappingComparer.Equals(item, channel.Channel, snapshot.Features.CaseMapping)))
            {
                channel.ApplySnapshot(
                    null,
                    snapshot.Features.Prefix,
                    snapshot.Features.ChannelModes,
                    snapshot.Nickname,
                    snapshot.Features.CaseMapping,
                    networkAvailable,
                    snapshot.DesiredChannels.Any(item => IrcCaseMappingComparer.Equals(item, channel.Channel, snapshot.Features.CaseMapping)));
            }
        }

        foreach (var query in Queries)
        {
            query.ApplyConnectionState(networkAvailable);
            if (query.IdentityConnectionGeneration != snapshot.ConnectionGeneration)
            {
                query.MarkIdentityBoundary(snapshot.ConnectionGeneration);
            }
        }
    }

    internal ChannelView EnsureChannel(string channel, bool reopen = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var existing = Channels.FirstOrDefault(item => IrcCaseMappingComparer.Equals(item.Channel, channel, _snapshot.Features.CaseMapping));
        if (existing is not null)
        {
            if (reopen)
            {
                ReopenView(existing);
            }

            return existing;
        }

        var view = new ChannelView(Id, Guid.NewGuid(), channel);
        Channels.Add(view);
        InsertView(view);
        return view;
    }

    internal QueryView EnsureQuery(string nickname, bool reopen = true, bool includeUnboundIdentity = true, bool forceNew = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var existing = forceNew
            ? null
            : Queries.FirstOrDefault(item =>
                (includeUnboundIdentity || item.IsIdentityBoundToCurrentSession)
                && IrcCaseMappingComparer.Equals(item.Nickname, nickname, _snapshot.Features.CaseMapping));
        if (existing is not null)
        {
            if (reopen)
            {
                ReopenView(existing);
            }

            return existing;
        }

        var viewId = Guid.NewGuid();
        var historyKey = forceNew
            ? $"{ConversationLoggingService.BuildConversationKey(LogConversationKind.PrivateConversation, nickname)}:isolated-{viewId:N}"
            : null;
        var view = new QueryView(Id, viewId, nickname, historyKey);
        view.MarkCurrentSessionIdentity(_snapshot.ConnectionGeneration);
        view.ApplyConnectionState(_snapshot.State is not (ServerSessionState.Disconnected or ServerSessionState.Failed or ServerSessionState.ReconnectWaiting));
        Queries.Add(view);
        InsertView(view);
        return view;
    }

    internal QueryView EnsureHistoricalQuery(string nickname, string historyConversationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyConversationKey);
        var existing = Queries.FirstOrDefault(query =>
            string.Equals(query.HistoryConversationKey, historyConversationKey, StringComparison.Ordinal));
        if (existing is not null)
        {
            ReopenView(existing);
            return existing;
        }

        var view = new QueryView(Id, Guid.NewGuid(), nickname, historyConversationKey);
        view.MarkIdentityBoundary(_snapshot.ConnectionGeneration);
        view.ApplyConnectionState(false);
        Queries.Add(view);
        InsertView(view);
        return view;
    }

    internal QueryView EnsureIncomingQuery(string nickname, string? account = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var mapping = _snapshot.Features.CaseMapping;
        var nicknameMatches = Queries
            .Where(item => IrcCaseMappingComparer.Equals(item.Nickname, nickname, mapping))
            .ToArray();
        var observedNicknameMatches = Queries
            .Where(item => item.HasObservedNickname(nickname, mapping))
            .ToArray();
        var normalizedAccount = ConversationIdentityEvidence.NormalizeAccount(account);
        if (normalizedAccount is not null)
        {
            var accountMatches = Queries.Where(item => item.HasAccountEvidence(normalizedAccount)).ToArray();
            if (accountMatches.Length == 1)
            {
                var accountMatch = accountMatches[0];
                if (accountMatch.HasConflictingAccountEvidence)
                {
                    return EnsureQuery(nickname, reopen: true, includeUnboundIdentity: false, forceNew: true);
                }

                accountMatch.RebindNicknameFromStrongAccount(nickname, mapping, _snapshot.ConnectionGeneration);
                accountMatch.MarkCurrentSessionIdentity(_snapshot.ConnectionGeneration);
                accountMatch.ObserveIdentityEvidence(new ConversationIdentityEvidence(
                    Id,
                    nickname,
                    normalizedAccount,
                    null,
                    null,
                    _snapshot.ConnectionGeneration,
                    DateTimeOffset.UtcNow,
                    IdentityEvidenceSource.LiveAccountTag));
                ReopenView(accountMatch);
                return accountMatch;
            }

            if (accountMatches.Length > 1
                || observedNicknameMatches.Any(item => item.HasConflictingAccountEvidence
                    || item.IdentityEvidence.Accounts.Count > 0
                        && !item.HasAccountEvidence(normalizedAccount)))
            {
                return EnsureQuery(nickname, reopen: true, includeUnboundIdentity: false, forceNew: true);
            }
        }

        var currentMatches = nicknameMatches
            .Where(item => item.IsIdentityBoundToCurrentSession || item.CanReuseAfterReconnect)
            .ToArray();
        if (currentMatches.Length == 1)
        {
            var existing = currentMatches[0];
            existing.MarkCurrentSessionIdentity(_snapshot.ConnectionGeneration);
            existing.ObserveIdentityEvidence(new ConversationIdentityEvidence(
                Id,
                nickname,
                normalizedAccount,
                null,
                null,
                _snapshot.ConnectionGeneration,
                DateTimeOffset.UtcNow,
                normalizedAccount is null ? IdentityEvidenceSource.LivePrefix : IdentityEvidenceSource.LiveAccountTag));
            ReopenView(existing);
            return existing;
        }

        if (currentMatches.Length > 1)
        {
            return EnsureQuery(nickname, reopen: true, includeUnboundIdentity: false, forceNew: true);
        }

        if (observedNicknameMatches.Length > 0)
        {
            return EnsureQuery(nickname, reopen: true, includeUnboundIdentity: false, forceNew: true);
        }

        return EnsureQuery(nickname, reopen: true, includeUnboundIdentity: false);
    }

    internal QueryView EnsureRecoveredQuery(string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var existing = Queries.FirstOrDefault(item =>
            IrcCaseMappingComparer.Equals(item.Nickname, nickname, _snapshot.Features.CaseMapping)
            && !item.IsIdentityBoundToCurrentSession);
        if (existing is not null)
        {
            ReopenView(existing);
            return existing;
        }

        var view = new QueryView(Id, Guid.NewGuid(), nickname);
        view.MarkIdentityBoundary(_snapshot.ConnectionGeneration);
        Queries.Add(view);
        InsertView(view);
        return view;
    }

    internal QueryView? FindQueryByAccount(string account)
    {
        var matches = Queries.Where(item => item.HasAccountEvidence(account)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal QueryView? FindQueryByHistoryKey(string historyKey) =>
        Queries.FirstOrDefault(query => string.Equals(query.HistoryConversationKey, historyKey, StringComparison.Ordinal));

    internal QueryView? FindQuery(string nickname) =>
        Queries.FirstOrDefault(item => IrcCaseMappingComparer.Equals(item.Nickname, nickname, _snapshot.Features.CaseMapping));

    internal int CountQueryCandidates(string nickname) =>
        Queries.Count(item =>
            IrcCaseMappingComparer.Equals(item.Nickname, nickname, _snapshot.Features.CaseMapping)
            && (item.IsIdentityBoundToCurrentSession || item.CanReuseAfterReconnect));

    internal WhoisView EnsureWhois(string nickname, bool beginRequest = false, bool forceNew = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var existing = forceNew
            ? null
            : WhoisViews.LastOrDefault(item => IrcCaseMappingComparer.Equals(item.RequestedNickname, nickname, _snapshot.Features.CaseMapping));
        if (existing is not null)
        {
            if (beginRequest)
            {
                existing.BeginRequest();
            }

            return existing;
        }

        var view = new WhoisView(Id, Guid.NewGuid(), nickname);
        WhoisViews.Add(view);
        InsertView(view);
        if (beginRequest)
        {
            view.BeginRequest();
        }

        return view;
    }

    internal ChannelListView EnsureChannelList(bool beginRequest = false)
    {
        var existing = ChannelListViews.FirstOrDefault();
        if (existing is not null)
        {
            if (beginRequest)
            {
                existing.BeginRequest();
            }

            return existing;
        }

        var view = new ChannelListView(Id, Guid.NewGuid(), "Channel List");
        ChannelListViews.Add(view);
        InsertView(view);
        if (beginRequest)
        {
            view.BeginRequest();
        }

        return view;
    }

    internal BanListView EnsureBanList(string channel, bool beginRequest = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var existing = BanListViews.FirstOrDefault(item => IrcCaseMappingComparer.Equals(item.Channel, channel, _snapshot.Features.CaseMapping));
        if (existing is not null)
        {
            if (beginRequest)
            {
                existing.BeginRequest();
            }

            return existing;
        }

        var view = new BanListView(Id, Guid.NewGuid(), channel);
        BanListViews.Add(view);
        InsertView(view);
        if (beginRequest)
        {
            view.BeginRequest();
        }

        return view;
    }

    internal WhoisView? FindWhois(string nickname) =>
        WhoisViews.LastOrDefault(item => IrcCaseMappingComparer.Equals(item.RequestedNickname, nickname, _snapshot.Features.CaseMapping));

    internal void Activate(WorkspaceView view)
    {
        foreach (var item in Views)
        {
            if (ReferenceEquals(item, view))
            {
                item.Activate();
            }
            else
            {
                item.Deactivate();
            }
        }

        ActiveView = view;
    }

    internal void DeactivateActiveView() => ActiveView?.Deactivate();

    internal void Close(WorkspaceView view)
    {
        view.CloseView();
        Views.Remove(view);
    }

    internal void ReopenView(WorkspaceView view)
    {
        if (Views.Contains(view))
        {
            view.ReopenView();
            return;
        }

        view.ReopenView();
        InsertView(view);
    }

    internal void RemoveConversation(WorkspaceView view)
    {
        view.CloseView();
        Views.Remove(view);
        if (view is ChannelView channel)
        {
            Channels.Remove(channel);
        }
        else if (view is QueryView query)
        {
            Queries.Remove(query);
        }
    }

    private void InsertView(WorkspaceView view)
    {
        if (view.Kind is WorkspaceViewKind.Query or WorkspaceViewKind.Whois or WorkspaceViewKind.ChannelList or WorkspaceViewKind.BanList)
        {
            var insertIndex = Views.TakeWhile(item => item.Kind is not WorkspaceViewKind.Query).Count();
            Views.Insert(insertIndex, view);
        }
        else
        {
            var channelIndex = Views.TakeWhile(item => item.Kind is WorkspaceViewKind.ServerStatus or WorkspaceViewKind.Channel).Count();
            Views.Insert(Math.Max(1, channelIndex), view);
        }
    }
}
