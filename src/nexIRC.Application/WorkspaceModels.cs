using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum WorkspaceViewKind
{
    ServerStatus,
    Channel,
    Query,
    Whois,
    ChannelList
}

public enum WorkspaceActivity
{
    None,
    Unread,
    Important
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
    string? Metadata = null)
{
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

    public IReadOnlyList<string> RequestedCapabilities { get; init; } = ["message-tags", "server-time", "multi-prefix", "labeled-response"];

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

    public ServerSessionOptions ToSessionOptions() => new()
    {
        Endpoint = Endpoint,
        Nickname = Nickname,
        Username = Username,
        RealName = RealName,
        AlternateNickname = AlternateNickname,
        NicknameFallbacks = NicknameFallbacks,
        RequestedCapabilities = RequestedCapabilities,
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
    private readonly object _entriesGate = new();
    private WorkspaceActivity _activity;
    private bool _isActive;

    protected WorkspaceView(Guid networkId, Guid id, WorkspaceViewKind kind, string title)
    {
        NetworkId = networkId;
        Id = id;
        Kind = kind;
        Title = title;
    }

    public Guid NetworkId { get; }

    public Guid Id { get; }

    public WorkspaceViewKind Kind { get; }

    public string Title { get; }

    public string DisplayLabel => Activity switch
    {
        WorkspaceActivity.Important => $"! {Title}",
        WorkspaceActivity.Unread => $"• {Title}",
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

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    public ObservableCollection<TranscriptEntry> Entries { get; } = [];

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

    internal void Append(TranscriptEntry entry, bool markActivity = true, WorkspaceActivity? activity = null)
    {
        lock (_entriesGate)
        {
            Entries.Add(entry);
            while (Entries.Count > 500)
            {
                Entries.RemoveAt(0);
            }
        }

        if (markActivity && !IsActive)
        {
            MarkActivity(activity ?? (entry.Kind is TranscriptEntryKind.Error or TranscriptEntryKind.Notice
                ? WorkspaceActivity.Important
                : WorkspaceActivity.Unread));
        }
    }

    public void MarkActivity(WorkspaceActivity activity)
    {
        if (activity > Activity)
        {
            Activity = activity;
        }
    }

    public void ClearEntries()
    {
        lock (_entriesGate)
        {
            Entries.Clear();
        }
    }

    public void Activate()
    {
        IsActive = true;
        Activity = WorkspaceActivity.None;
    }

    internal void Deactivate() => IsActive = false;

    public override string ToString() => DisplayLabel;
}

public sealed class ServerStatusView : WorkspaceView
{
    private NetworkDisplayState _connectionState = NetworkDisplayState.Disconnected;
    private string? _networkName;
    private string _endpointText = string.Empty;

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

    internal void ApplySnapshot(ServerSessionSnapshot snapshot)
    {
        ConnectionState = ToDisplayState(snapshot.State);
        NetworkName = snapshot.Features.NetworkName ?? snapshot.Identity.NetworkName;
        EndpointText = snapshot.Endpoint.ToString();
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

    public string PrefixText
    {
        get => _prefixText;
        private set => SetProperty(ref _prefixText, value);
    }

    public IReadOnlySet<char> PrefixModes => _prefixModes;

    public string DisplayText => $"{PrefixText}{Nickname}";

    internal void Apply(IrcChannelMemberSnapshot snapshot, IrcPrefixGrammar? grammar)
    {
        Username = snapshot.Username;
        Host = snapshot.Host;
        _prefixModes = new HashSet<char>(snapshot.PrefixModes);
        PrefixText = HighestPrefix(snapshot.PrefixModes, grammar);
        OnPropertyChanged(nameof(DisplayText));
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

    public ObservableCollection<ChannelMemberView> Members { get; } = [];

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

    internal void ApplySnapshot(IrcChannelSnapshot? snapshot, IrcPrefixGrammar? grammar, string? localNickname = null, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        if (snapshot is null)
        {
            IsJoined = false;
            IsStale = true;
            Synchronization = ChannelSynchronizationState.NotRequested;
            Topic = null;
            lock (_membersGate)
            {
                Members.Clear();
            }
            ModeSummary = string.Empty;
            _localPrefixModes = new HashSet<char>();
            LocalPrefixText = string.Empty;
            CanModerate = false;
        }
        else
        {
            IsJoined = snapshot.IsJoined;
            IsStale = snapshot.IsStale;
            Synchronization = snapshot.Synchronization;
            Topic = snapshot.Topic;
            ModeSummary = new string(snapshot.Modes.OrderBy(static mode => mode).ToArray());
            var projected = snapshot.Members.Values
                .OrderBy(member => PrefixRank(member, grammar))
                .ThenBy(static member => member.Nickname, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            lock (_membersGate)
            {
                Members.Clear();
                foreach (var member in projected)
                {
                    var view = new ChannelMemberView(member.Nickname);
                    view.Apply(member, grammar);
                    Members.Add(view);
                }
            }

            var localMember = localNickname is null
                ? null
                : snapshot.Members.Values.FirstOrDefault(member => IrcCaseMappingComparer.Equals(member.Nickname, localNickname, mapping));
            _localPrefixModes = localMember?.PrefixModes is { } localModes
                ? new HashSet<char>(localModes)
                : new HashSet<char>();
            LocalPrefixText = ChannelMemberView.HighestPrefix(_localPrefixModes, grammar);
            var moderationModes = grammar?.Modes.Count > 1
                ? grammar.Modes.Take(grammar.Modes.Count - 1).ToHashSet()
                : grammar?.Modes.ToHashSet() ?? new HashSet<char>();
            CanModerate = _localPrefixModes.Any(moderationModes.Contains);
        }

        OnPropertyChanged(nameof(TopicText));
        OnPropertyChanged(nameof(SynchronizationText));
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
    internal QueryView(Guid networkId, Guid id, string nickname)
        : base(networkId, id, WorkspaceViewKind.Query, nickname)
    {
        Nickname = nickname;
    }

    public string Nickname { get; }
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
            channel.ApplySnapshot(null, _snapshot.Features.Prefix, _snapshot.Nickname, _snapshot.Features.CaseMapping);
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

    public ObservableCollection<WorkspaceView> Views { get; } = [];

    public ObservableCollection<ChannelView> Channels { get; } = [];

    public ObservableCollection<QueryView> Queries { get; } = [];

    public ObservableCollection<WhoisView> WhoisViews { get; } = [];

    public ObservableCollection<ChannelListView> ChannelListViews { get; } = [];

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

        foreach (var channel in snapshot.Channels)
        {
            EnsureChannel(channel.Name).ApplySnapshot(channel, snapshot.Features.Prefix, snapshot.Nickname, snapshot.Features.CaseMapping);
        }

        foreach (var channel in Channels)
        {
            if (!snapshot.Channels.Any(item => IrcCaseMappingComparer.Equals(item.Name, channel.Channel, snapshot.Features.CaseMapping)) &&
                snapshot.DesiredChannels.All(item => !IrcCaseMappingComparer.Equals(item, channel.Channel, snapshot.Features.CaseMapping)))
            {
                channel.ApplySnapshot(null, snapshot.Features.Prefix, snapshot.Nickname, snapshot.Features.CaseMapping);
            }
        }

        foreach (var query in snapshot.Queries)
        {
            EnsureQuery(query.Nickname);
        }
    }

    internal ChannelView EnsureChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var existing = Channels.FirstOrDefault(item => IrcCaseMappingComparer.Equals(item.Channel, channel, _snapshot.Features.CaseMapping));
        if (existing is not null)
        {
            return existing;
        }

        var view = new ChannelView(Id, Guid.NewGuid(), channel);
        Channels.Add(view);
        InsertView(view);
        return view;
    }

    internal QueryView EnsureQuery(string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var existing = Queries.FirstOrDefault(item => IrcCaseMappingComparer.Equals(item.Nickname, nickname, _snapshot.Features.CaseMapping));
        if (existing is not null)
        {
            return existing;
        }

        var view = new QueryView(Id, Guid.NewGuid(), nickname);
        Queries.Add(view);
        InsertView(view);
        return view;
    }

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

    private void InsertView(WorkspaceView view)
    {
        if (view.Kind is WorkspaceViewKind.Query or WorkspaceViewKind.Whois or WorkspaceViewKind.ChannelList)
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
