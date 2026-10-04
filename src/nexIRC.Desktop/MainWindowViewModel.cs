using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;

namespace nexIRC.Desktop;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IrcCommandDispatcher _commands;
    private WorkspaceView? _activeView;
    private string _inputText = string.Empty;
    private string _statusText = "Ready. Add a network to begin.";
    private bool _isNetworkTreeVisible = true;
    private bool _isMemberListVisible = true;
    private bool _isToolbarVisible = true;
    private bool _isStatusBarVisible = true;
    private readonly object _shutdownGate = new();
    private Task? _shutdownTask;
    private int _navigationRefreshPending;
    private long _navigationRefreshRequests;
    private long _navigationRefreshExecutions;
    private long _navigationRefreshCoalescedRequests;
    private readonly DesktopNotificationAdapter? _notificationAdapter;
    private readonly System.Windows.Threading.Dispatcher _uiDispatcher;
    private readonly IResumeStateStore? _resumeStateStore;
    private readonly IResumeSecretProtector? _resumeSecretProtector;
    private readonly IConversationDraftStore _conversationDraftStore;
    private readonly Dictionary<Guid, MemorySaslCredentialProvider> _sessionCredentials = [];
    private readonly Dictionary<Guid, MemoryServerPasswordProvider> _sessionServerPasswords = [];
    private readonly Dictionary<(Guid NetworkId, Guid ViewId), ComposerDraftState> _drafts = [];
    private readonly Dictionary<(Guid ProfileId, string ConversationKey), LocalConversationDraft> _persistedDrafts = [];
    private readonly Dictionary<(Guid NetworkId, Guid ViewId), CancellationTokenSource> _draftDebounces = [];
    private bool _applyingDraftProjection;
    private ReplyComposerState? _replyComposer;

    public MainWindowViewModel(
        IIrcTransportFactory transportFactory,
        System.Windows.Threading.Dispatcher dispatcher,
        ConfigurationService? configuration = null,
        ProfileCredentialService? credentials = null,
        IConversationLogStore? logStore = null,
        IConversationDraftStore? conversationDraftStore = null)
    {
        _uiDispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Configuration = configuration;
        Credentials = credentials ?? new ProfileCredentialService(
            OperatingSystem.IsWindows()
                ? new WindowsCredentialStore()
                : new InMemoryProfileCredentialStore());
        if (OperatingSystem.IsWindows() && configuration?.Store is JsonConfigurationStore)
        {
            try
            {
                _resumeStateStore = new JsonResumeStateStore(ResumeStatePaths.GetDefaultRoot());
                _resumeSecretProtector = new WindowsDpapiResumeSecretProtector();
            }
            catch
            {
                // A protected-state location failure must not prevent ordinary
                // IRC connectivity or the existing fallback strategies.
                _resumeStateStore = null;
                _resumeSecretProtector = null;
            }
        }

        _conversationDraftStore = conversationDraftStore ?? CreateConversationDraftStore(_resumeSecretProtector);
        try
        {
            foreach (var draft in _conversationDraftStore.Load())
                _persistedDrafts[(draft.ProfileId, draft.ConversationKey)] = draft;
        }
        catch
        {
            // Local draft recovery is best effort; the protected store itself
            // rejects corrupt snapshots without reconstructing draft text.
        }

        Sessions = new NetworkSessionManager(
            transportFactory,
            new WpfWorkspaceDispatcher(dispatcher),
            configuration: configuration,
            logStore: logStore,
            resumeStateStore: _resumeStateStore,
            resumeSecretProtector: _resumeSecretProtector);
        if (configuration is not null)
        {
            _notificationAdapter = new DesktopNotificationAdapter(Sessions.Notifications, () => CurrentPreferences, notification => { RouteNotification(notification); });
        }
        Actions = new WorkspaceActionRouter(Sessions);
        _commands = new IrcCommandDispatcher(Sessions, Actions);
        ParticipantActions = Actions.ParticipantActions;
        InputHistory = new InputHistory();
        Completion = new CompletionEngine(() => Configuration?.Aliases ?? Array.Empty<AliasDefinition>());
        if (configuration is not null)
        {
            var preferences = configuration.Preferences;
            _isNetworkTreeVisible = preferences.IsNetworkTreeVisible;
            _isMemberListVisible = preferences.IsMemberListVisible;
            _isToolbarVisible = preferences.IsToolbarVisible;
            _isStatusBarVisible = preferences.IsStatusBarVisible;
        }
        NewConnectionCommand = new AsyncRelayCommand(() => NewConnectionRequested?.Invoke() ?? Task.CompletedTask);
        DisconnectCommand = new AsyncRelayCommand(DisconnectSelectedAsync, HasActiveNetwork);
        ReconnectCommand = new AsyncRelayCommand(ReconnectSelectedAsync, HasActiveNetwork);
        JoinCommand = new RelayCommand(() => InputText = "/join ");
        PartCommand = new RelayCommand(() => InputText = "/part", () => ActiveView is ChannelView);
        QueryCommand = new RelayCommand(() => InputText = "/query ");
        ListCommand = new RelayCommand(() => InputText = "/list ");
        NextViewCommand = new RelayCommand(() => ActivateRelativeView(1), () => Sessions.Networks.Count > 0);
        PreviousViewCommand = new RelayCommand(() => ActivateRelativeView(-1), () => Sessions.Networks.Count > 0);
        ActivateConversationCommand = new ParameterizedRelayCommand(ActivateConversation, parameter => parameter is ConversationNavigationItem);
        NextConversationCommand = new RelayCommand(() => { Sessions.NavigateNextConversation(); }, () => Sessions.Networks.Count > 0);
        PreviousConversationCommand = new RelayCommand(() => { Sessions.NavigatePreviousConversation(); }, () => Sessions.Networks.Count > 0);
        NextUnreadCommand = new RelayCommand(() => { Sessions.NavigateNextUnread(); }, () => Sessions.Networks.Count > 0);
        PreviousUnreadCommand = new RelayCommand(() => { Sessions.NavigatePreviousUnread(); }, () => Sessions.Networks.Count > 0);
        NextHighlightCommand = new RelayCommand(() => { Sessions.NavigateNextHighlight(); }, () => Sessions.Networks.Count > 0);
        PreviousHighlightCommand = new RelayCommand(() => { Sessions.NavigatePreviousHighlight(); }, () => Sessions.Networks.Count > 0);
        BackConversationCommand = new RelayCommand(() => { Sessions.NavigateBack(); }, () => Sessions.Networks.Count > 0);
        ForwardConversationCommand = new RelayCommand(() => { Sessions.NavigateForward(); }, () => Sessions.Networks.Count > 0);
        CancelReplyCommand = new RelayCommand(() => CancelReply(), () => IsReplying);
        KeepServerDraftCommand = new RelayCommand(KeepServerDraft, HasActiveDraftConflict);
        ReplaceServerDraftCommand = new RelayCommand(ReplaceServerDraftWithLocal, HasActiveDraftConflict);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());

        HighlightPolicy.PropertyChanged += (_, _) => SavePreferencesInBackground();

        Sessions.Networks.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
            {
                foreach (var oldNetwork in args.OldItems.OfType<NetworkWorkspace>())
                {
                    RemoveDraftsForNetwork(oldNetwork.Id);
                    if (_replyComposer?.NetworkId == oldNetwork.Id)
                    {
                        ClearReplyComposer(updateStatus: false);
                    }
                }
            }

            if (ActiveView is null && Sessions.Networks.Count > 0)
            {
                SelectView(Sessions.Networks[0].StatusView);
            }

            RefreshCommandStates();
            RefreshConversationNavigator();
        };
        Sessions.NavigationChanged += OnNavigationChanged;
        Sessions.SynchronizedDraftStateReceived += OnSynchronizedDraftStateReceived;
        RefreshConversationNavigator();
    }

    public event Func<Task>? NewConnectionRequested;

    public event Action? ExitRequested;

    public NetworkSessionManager Sessions { get; }

    public ParticipantActionService ParticipantActions { get; }

    public WorkspaceActionRouter Actions { get; }

    public ConfigurationService? Configuration { get; }

    public ProfileCredentialService Credentials { get; }

    public ProfilePortabilityService? ProfilePortability => Configuration is null ? null : new ProfilePortabilityService(Configuration);

    public IReadOnlyList<AliasDefinition> Aliases => Configuration?.Aliases ?? Array.Empty<AliasDefinition>();

    public ObservableCollection<NetworkWorkspace> Networks => Sessions.Networks;

    public ObservableCollection<ConversationNavigationItem> ConversationNavigator { get; } = [];

    public MainWindowPresentationDiagnostics PresentationDiagnostics => new(
        Interlocked.Read(ref _navigationRefreshRequests),
        Interlocked.Read(ref _navigationRefreshExecutions),
        Interlocked.Read(ref _navigationRefreshCoalescedRequests));

    public InputHistory InputHistory { get; }

    public CompletionEngine Completion { get; }

    public HighlightActivityPolicy HighlightPolicy => Sessions.HighlightPolicy;

    public WorkspaceView? ActiveView
    {
        get => _activeView;
        private set => SetProperty(ref _activeView, value);
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _inputText, normalized) && !_applyingDraftProjection)
                CaptureActiveDraft(normalized);
        }
    }

    public bool IsDraftConflictVisible => ActiveView is not null
        && _drafts.TryGetValue((ActiveView.NetworkId, ActiveView.Id), out var state) && state.HasConflict;

    public string DraftConflictText => ActiveView is not null
        && _drafts.TryGetValue((ActiveView.NetworkId, ActiveView.Id), out var state) && state.HasConflict
            ? $"This draft conflicts with server revision {state.ServerRevision}. Your local text is preserved."
            : string.Empty;

    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value);
    }

    public ReplyComposerState? ReplyComposer => _replyComposer;

    public bool IsReplying => _replyComposer is not null;

    public string ReplyBannerText => _replyComposer?.AccessibleText ?? string.Empty;

    public bool IsNetworkTreeVisible
    {
        get => _isNetworkTreeVisible;
        set
        {
            if (SetProperty(ref _isNetworkTreeVisible, value)) SavePreferencesInBackground();
        }
    }

    public bool IsMemberListVisible
    {
        get => _isMemberListVisible;
        set
        {
            if (SetProperty(ref _isMemberListVisible, value)) SavePreferencesInBackground();
        }
    }

    public bool IsToolbarVisible
    {
        get => _isToolbarVisible;
        set
        {
            if (SetProperty(ref _isToolbarVisible, value)) SavePreferencesInBackground();
        }
    }

    public bool IsStatusBarVisible
    {
        get => _isStatusBarVisible;
        set
        {
            if (SetProperty(ref _isStatusBarVisible, value)) SavePreferencesInBackground();
        }
    }

    public ICommand NewConnectionCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand ReconnectCommand { get; }

    public ICommand JoinCommand { get; }

    public ICommand PartCommand { get; }

    public ICommand QueryCommand { get; }

    public ICommand ListCommand { get; }

    public ICommand NextViewCommand { get; }

    public ICommand PreviousViewCommand { get; }

    public ICommand ActivateConversationCommand { get; }

    public ICommand NextConversationCommand { get; }

    public ICommand PreviousConversationCommand { get; }

    public ICommand NextUnreadCommand { get; }

    public ICommand PreviousUnreadCommand { get; }

    public ICommand NextHighlightCommand { get; }

    public ICommand PreviousHighlightCommand { get; }

    public ICommand BackConversationCommand { get; }

    public ICommand ForwardConversationCommand { get; }

    public ICommand CancelReplyCommand { get; }

    public ICommand KeepServerDraftCommand { get; }

    public ICommand ReplaceServerDraftCommand { get; }

    public ICommand ExitCommand { get; }

    public async Task RestoreProfilesAsync()
    {
        if (Configuration is null)
        {
            return;
        }

        foreach (var profile in Configuration.Profiles.Profiles)
        {
            try
            {
                var workspace = Sessions.Add(profile.ToConnectionOptions(Credentials.Store));
                if (profile.AutoConnect)
                {
                    await Sessions.ConnectAsync(workspace.Id).ConfigureAwait(true);
                }
            }
            catch (Exception exception)
            {
                StatusText = $"Could not restore {profile.DisplayName}: {exception.Message}";
            }
        }

        var selectedProfile = Configuration.Preferences.LastSelectedNetworkProfileId;
        var selected = selectedProfile is Guid profileId
            ? Sessions.Networks.FirstOrDefault(network => network.ProfileId == profileId)
            : null;
        if (selected is not null)
        {
            SelectView(selected.StatusView);
        }
    }

    public async Task ConnectProfileAsync(Guid profileId)
    {
        if (Configuration is null || !Configuration.Profiles.TryGet(profileId, out var profile) || profile is null)
        {
            return;
        }

        var options = profile.ToConnectionOptions(Credentials.Store);
        if (_sessionCredentials.TryGetValue(profile.Id, out var sessionCredential))
        {
            options = options with { SaslCredentialProvider = sessionCredential };
        }
        if (_sessionServerPasswords.TryGetValue(profile.Id, out var sessionServerPassword))
        {
            options = options with { PasswordProvider = sessionServerPassword };
        }
        var workspace = Sessions.Networks.FirstOrDefault(network => network.ProfileId == profileId);
        var runtimeProviderChanged = workspace is not null
            && ((workspace.Options.SaslCredentialProvider is MemorySaslCredentialProvider) != _sessionCredentials.ContainsKey(profile.Id)
                || (workspace.Options.PasswordProvider is MemoryServerPasswordProvider) != _sessionServerPasswords.ContainsKey(profile.Id));
        if (workspace is not null && (!IsEquivalent(workspace.Options, options) || runtimeProviderChanged))
        {
            await Sessions.RemoveAsync(workspace.Id).ConfigureAwait(true);
            _sessionCredentials.Remove(profile.Id);
            _sessionServerPasswords.Remove(profile.Id);
            workspace = null;
        }

        workspace ??= Sessions.Add(options);
        SelectView(workspace.StatusView);
        await Sessions.ConnectAsync(workspace.Id).ConfigureAwait(true);
        StatusText = $"Connecting to {profile.DisplayName}.";
    }

    public async Task SaveProfileAsync(NetworkProfile profile)
    {
        if (Configuration is null)
        {
            return;
        }

        if (!Configuration.Profiles.AddOrUpdate(profile))
        {
            StatusText = "The maximum number of saved profiles has been reached.";
            return;
        }

        await Configuration.SaveAsync().ConfigureAwait(true);
        StatusText = $"Saved profile {profile.DisplayName}.";
    }

    public async Task SaveProfileAsync(NetworkProfile profile, string? saslPassword, bool clearSaslCredential)
    {
        await SaveProfileAsync(profile, saslPassword, clearSaslCredential, null, false, false).ConfigureAwait(true);
    }

    public async Task SaveProfileAsync(
        NetworkProfile profile,
        string? saslPassword,
        bool clearSaslCredential,
        string? serverPassword,
        bool clearServerPassword,
        bool updateServerPassword)
    {
        await SaveProfileAsync(profile).ConfigureAwait(true);
        if (profile.SaslPolicy != nexIRC.Core.Session.SaslAuthenticationPolicy.Disabled)
        {
            if (clearSaslCredential)
            {
                var deleted = await Credentials.DeleteAsync(profile.Id, ProfileCredentialKind.Sasl).ConfigureAwait(true);
                if (!deleted.Succeeded) StatusText = deleted.Diagnostic ?? "The stored SASL credential could not be deleted.";
            }
            else if (!string.IsNullOrEmpty(saslPassword))
            {
                var username = string.IsNullOrWhiteSpace(profile.SaslUsername) ? profile.Nickname : profile.SaslUsername;
                var saved = await Credentials.SaveAsync(profile.Id, ProfileCredentialKind.Sasl, username, saslPassword).ConfigureAwait(true);
                if (!saved.Succeeded)
                {
                    if (_sessionCredentials.Remove(profile.Id, out var previous)) previous.Dispose();
                    _sessionCredentials[profile.Id] = new MemorySaslCredentialProvider(username, saslPassword);
                    StatusText = saved.Diagnostic ?? "The SASL credential could not be stored securely; it will be used for this runtime session only.";
                }
                else if (_sessionCredentials.Remove(profile.Id, out var previous))
                {
                    previous.Dispose();
                }
            }
        }

        if (updateServerPassword)
        {
            if (clearServerPassword)
            {
                var deleted = await Credentials.DeleteAsync(profile.Id, ProfileCredentialKind.ServerPassword).ConfigureAwait(true);
                if (!deleted.Succeeded) StatusText = deleted.Diagnostic ?? "The stored server password could not be deleted.";
            }
            else if (!string.IsNullOrEmpty(serverPassword))
            {
                var saved = await Credentials.SaveAsync(profile.Id, ProfileCredentialKind.ServerPassword, profile.Nickname, serverPassword).ConfigureAwait(true);
                if (!saved.Succeeded)
                {
                    if (_sessionServerPasswords.Remove(profile.Id, out var previous)) previous.Dispose();
                    _sessionServerPasswords[profile.Id] = new MemoryServerPasswordProvider(serverPassword);
                    StatusText = saved.Diagnostic ?? "The server password could not be stored securely; it will be used for this runtime session only.";
                }
                else if (_sessionServerPasswords.Remove(profile.Id, out var previous))
                {
                    previous.Dispose();
                }
            }
        }
    }

    public ValueTask<CredentialLoadResult> LoadCredentialStateAsync(Guid profileId) =>
        Credentials.LoadAsync(profileId, ProfileCredentialKind.Sasl);

    public ValueTask<bool> CredentialExistsAsync(Guid profileId) =>
        Credentials.ExistsAsync(profileId, ProfileCredentialKind.Sasl);

    public ValueTask<CredentialLoadResult> LoadServerPasswordStateAsync(Guid profileId) =>
        Credentials.LoadAsync(profileId, ProfileCredentialKind.ServerPassword);

    public async Task DeleteProfileAsync(Guid profileId)
    {
        if (Configuration is null || !Configuration.Profiles.Remove(profileId))
        {
            return;
        }

        foreach (var key in _persistedDrafts.Keys.Where(key => key.ProfileId == profileId).ToArray())
            _persistedDrafts.Remove(key);
        foreach (var key in _drafts.Where(pair => pair.Value.ProfileId == profileId).Select(static pair => pair.Key).ToArray())
            _drafts.Remove(key);
        _ = _conversationDraftStore.Save(_persistedDrafts.Values.ToArray());

        var workspace = Sessions.Networks.FirstOrDefault(network => network.ProfileId == profileId);
        if (workspace is not null)
        {
            await Sessions.RemoveAsync(workspace.Id).ConfigureAwait(true);
        }

        await Credentials.ClearProfileAsync(profileId).ConfigureAwait(true);
        if (_sessionCredentials.Remove(profileId, out var sessionCredential)) sessionCredential.Dispose();
        if (_sessionServerPasswords.Remove(profileId, out var serverPassword)) serverPassword.Dispose();

        await Configuration.SaveAsync().ConfigureAwait(true);
    }

    public ApplicationPreferences CurrentPreferences
    {
        get
        {
            var existing = Configuration?.Preferences ?? new ApplicationPreferences();
            return existing with
            {
                HighlightNickname = HighlightPolicy.HighlightNickname,
                HighlightCustomWords = HighlightPolicy.HighlightCustomWords,
                CustomHighlightWords = HighlightPolicy.CustomWords.ToList(),
                IsNetworkTreeVisible = IsNetworkTreeVisible,
                IsMemberListVisible = IsMemberListVisible,
                IsToolbarVisible = IsToolbarVisible,
                IsStatusBarVisible = IsStatusBarVisible
            };
        }
    }

    public async Task ApplyPreferencesAsync(ApplicationPreferences preferences)
    {
        Sessions.ApplyPreferences(preferences);
        if (Configuration is not null)
        {
            await Configuration.SaveAsync().ConfigureAwait(true);
        }
    }

    public async Task AddCurrentFavoriteAsync(string? label = null)
        => await AddCurrentFavoriteAsync(label, NavigationDefaults.DefaultFavoriteGroupId).ConfigureAwait(true);

    public async Task AddCurrentFavoriteAsync(string? label, Guid groupId)
    {
        if (Sessions.ActiveNetwork is null || ActiveView is not (ChannelView or QueryView))
        {
            StatusText = "Select a channel or query first.";
            return;
        }

        var kind = ActiveView is ChannelView ? DestinationKind.Channel : DestinationKind.Query;
        var name = ActiveView is ChannelView channel ? channel.Channel : ((QueryView)ActiveView).Nickname;
        StatusText = Sessions.AddFavorite(Sessions.ActiveNetwork.Id, kind, name, label, groupId)
            ? $"Added {name} to favorites."
            : "That destination is already a favorite or the favorite limit was reached.";
        if (Configuration is not null)
        {
            await Configuration.SaveAsync().ConfigureAwait(true);
        }
    }

    public void RemoveFavorite(Guid favoriteId) => Sessions.RemoveFavorite(favoriteId);

    public bool MoveFavorite(Guid favoriteId, Guid groupId) => Sessions.MoveFavorite(favoriteId, groupId);

    public bool ReorderFavorite(Guid favoriteId, int delta) => Sessions.ReorderFavorite(favoriteId, delta);

    public async Task OpenDestinationAsync(Guid networkId, DestinationKind kind, string name, bool joinIfNeeded = false)
    {
        if (!Sessions.TryGet(networkId, out var network) || network is null)
        {
            return;
        }

        var view = Sessions.OpenHistoricalConversation(networkId, kind, name);
        SelectView(view);
        Sessions.RecordRecent(network, kind, name);
        if (joinIfNeeded && kind == DestinationKind.Channel && view is ChannelView channel && !channel.IsJoined)
        {
            await network.Session.JoinChannelAsync(name).ConfigureAwait(true);
        }
    }

    public async Task<IReadOnlyList<ConversationLogSearchResult>> SearchLogsAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        if (Sessions.LogStore is null) return Array.Empty<ConversationLogSearchResult>();
        return await Sessions.LogStore.SearchAsync(query, cancellationToken).ConfigureAwait(true);
    }

    public async Task<ConversationLogSearchPage> SearchLogsDetailedAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        if (Sessions.LogStore is null)
        {
            return new ConversationLogSearchPage(
                Array.Empty<ConversationLogSearchResult>(),
                new ConversationLogSearchStatistics(0, 0, 0, 0, 0, false, false));
        }

        return await Sessions.LogStore.SearchDetailedAsync(query, cancellationToken).ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<ConversationLogRecord>> LoadHistoryPageAsync(
        Guid scopeId,
        LogConversationKind kind,
        string conversationName,
        int pageSize = ConfigurationLimits.MaximumHistoryPageSize,
        DateTimeOffset? before = null)
    {
        if (Sessions.LogStore is null) return Array.Empty<ConversationLogRecord>();
        return await Sessions.LogStore.ReadPageAsync(scopeId, kind, conversationName, pageSize, before).ConfigureAwait(true);
    }

    public async Task<HistoryPage> LoadHistoryWindowAsync(HistoryPageRequest request, CancellationToken cancellationToken = default)
    {
        if (Sessions.LogStore is null) return HistoryPage.Empty;
        return await Sessions.LogStore.ReadPageWindowAsync(request, cancellationToken).ConfigureAwait(true);
    }

    public async Task<ConversationHistoryRange> ExportHistoryAsync(HistoryExportRequest request, string path, HistoryExportFormat format, CancellationToken cancellationToken = default)
    {
        if (Sessions.LogStore is null)
        {
            return new ConversationHistoryRange(Array.Empty<ConversationLogRecord>(), false);
        }

        var result = await ConversationLoggingService.ExportAsync(Sessions.LogStore, request, path, format, cancellationToken: cancellationToken).ConfigureAwait(true);
        StatusText = result.IsTruncated ? "History exported with the configured size bound." : "History exported.";
        return result;
    }

    public bool RouteLogSearchResult(ConversationLogSearchResult result)
        => RouteLogSearchResultAsync(result).GetAwaiter().GetResult();

    public async Task<HistoryNavigationResult> NavigateHistoryMessageAsync(
        Guid networkId,
        LogConversationKind kind,
        string conversationName,
        string? conversationKey,
        string serverMessageId,
        CancellationToken cancellationToken = default)
    {
        if (!Sessions.TryGet(networkId, out var network) || network is null || kind is LogConversationKind.Status)
        {
            return HistoryNavigationResult.Create(
                HistoryNavigationRequest.ForMessage(new HistoryConversationAddress
                {
                    NetworkId = networkId,
                    ScopeId = network?.ProfileId ?? networkId,
                    ConversationKind = kind,
                    ConversationName = conversationName,
                    ConversationKey = conversationKey
                }, string.Empty),
                HistoryNavigationOutcome.Failed,
                "Choose a valid network, channel, or private conversation.");
        }

        var view = Sessions.OpenHistoricalConversation(
            networkId,
            kind == LogConversationKind.Channel ? DestinationKind.Channel : DestinationKind.Query,
            conversationName,
            conversationKey);
        SelectView(view);
        return await Sessions.JumpToHistoryMessageAsync(network, view, serverMessageId, cancellationToken).ConfigureAwait(true);
    }

    public async Task<HistoryNavigationResult> NavigateHistoryTimestampAsync(
        Guid networkId,
        LogConversationKind kind,
        string conversationName,
        string? conversationKey,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        if (!Sessions.TryGet(networkId, out var network) || network is null || kind is LogConversationKind.Status)
        {
            var request = HistoryNavigationRequest.ForTimestamp(new HistoryConversationAddress
            {
                NetworkId = networkId,
                ScopeId = network?.ProfileId ?? networkId,
                ConversationKind = kind,
                ConversationName = conversationName,
                ConversationKey = conversationKey
            }, timestamp);
            return HistoryNavigationResult.Create(request, HistoryNavigationOutcome.Failed, "Choose a valid network, channel, or private conversation.");
        }

        var view = Sessions.OpenHistoricalConversation(
            networkId,
            kind == LogConversationKind.Channel ? DestinationKind.Channel : DestinationKind.Query,
            conversationName,
            conversationKey);
        SelectView(view);
        return await Sessions.JumpToHistoryTimestampAsync(network, view, timestamp, cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    public async Task<bool> RouteLogSearchResultAsync(ConversationLogSearchResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var network = Sessions.Networks.FirstOrDefault(item => item.Id == result.NetworkId);
        if (network is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceView view = result.ConversationKind switch
            {
                LogConversationKind.Channel => Sessions.OpenHistoricalConversation(network.Id, DestinationKind.Channel, result.ConversationName, result.DurableConversationKey),
                LogConversationKind.PrivateConversation => network.Queries.FirstOrDefault(query =>
                        string.Equals(query.HistoryConversationKey, result.DurableConversationKey, StringComparison.Ordinal))
                    ?? Sessions.OpenHistoricalConversation(network.Id, DestinationKind.Query, result.ConversationName, result.DurableConversationKey),
                _ => network.StatusView
            };
            if (!view.IsViewOpen)
            {
                Sessions.ReopenView(view.Id);
            }
            SelectView(view);
            if (view is ChannelView or QueryView)
            {
                var navigation = await Sessions.NavigateToHistorySearchResultAsync(network, view, result, cancellationToken).ConfigureAwait(true);
                if (!navigation.Succeeded)
                {
                    StatusText = navigation.Message;
                    return false;
                }

                StatusText = navigation.Message;
            }
            else
            {
                view.SetHistoryContext([new HistoryContextEntry(result.Record, true)], result.Preview);
            }

            return true;
        }

        return false;
    }

    public void CaptureViewState(double width, double height, double left, double top, bool maximized, double navigationPaneWidth)
    {
        if (Configuration is null) return;
        var state = ViewStateValidator.Normalize(CurrentPreferences.ViewState with
        {
            WindowWidth = width,
            WindowHeight = height,
            WindowLeft = left,
            WindowTop = top,
            IsMaximized = maximized,
            NavigationPaneWidth = navigationPaneWidth
        });
        Configuration.SetPreferences(CurrentPreferences with { ViewState = state });
    }

    public async Task ExportProfilesAsync(string path, IEnumerable<Guid>? selectedIds = null)
    {
        if (ProfilePortability is null) return;
        await ProfilePortability.ExportAsync(path, selectedIds).ConfigureAwait(true);
        StatusText = "Profiles exported without credentials.";
    }

    public async Task<ProfileImportResult> ImportProfilesAsync(string path)
    {
        if (ProfilePortability is null) return new ProfileImportResult([], 0, ["Configuration is unavailable."]);
        var result = await ProfilePortability.ImportAsync(path).ConfigureAwait(true);
        await Configuration!.SaveAsync().ConfigureAwait(true);
        StatusText = result.Diagnostics.Count == 0 ? $"Imported {result.ImportedProfiles.Count} profile(s)." : string.Join(" ", result.Diagnostics);
        return result;
    }

    public bool RouteNotification(IrcNotification notification)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(new Action(() => RouteNotification(notification)));
            return true;
        }

        var routed = Sessions.ActivateNotification(notification);
        if (routed) NotificationActivationRequested?.Invoke(notification);
        return routed;
    }

    public event Action<IrcNotification>? NotificationActivationRequested;

    public async Task SaveAliasAsync(AliasDefinition alias)
    {
        if (Configuration is null || !AliasValidator.Validate(alias).IsValid || !Configuration.AddOrUpdateAlias(alias))
        {
            StatusText = AliasValidator.Validate(alias).Error ?? "The alias could not be saved.";
            return;
        }

        await Configuration.SaveAsync().ConfigureAwait(true);
        StatusText = $"Saved alias /{alias.Name.Trim().TrimStart('/')}.";
    }

    public async Task DeleteAliasAsync(Guid aliasId)
    {
        if (Configuration is null) return;
        Configuration.RemoveAlias(aliasId);
        await Configuration.SaveAsync().ConfigureAwait(true);
    }

    public void SelectView(object? selectedItem)
    {
        if (selectedItem is NetworkWorkspace network)
        {
            selectedItem = network.StatusView;
        }

        if (selectedItem is not WorkspaceView view)
        {
            return;
        }

        SaveDraft();
        if (_replyComposer is { } reply
            && (reply.NetworkId != view.NetworkId || reply.ViewId != view.Id))
        {
            ClearReplyComposer(updateStatus: false);
        }
        Sessions.ActivateView(view.Id);
        if (!ReferenceEquals(Sessions.ActiveView, view))
        {
            return;
        }
        ActiveView = view;
        SetProjectedInput(GetOrCreateDraftState(view).Text);
        RefreshDraftConflictState();
        StatusText = $"{view.Title} · {view.Kind}";
        if (Configuration is not null && Sessions.ActiveNetwork?.ProfileId is Guid profileId)
        {
            Configuration.SetPreferences(CurrentPreferences with { LastSelectedNetworkProfileId = profileId });
            SavePreferencesInBackground();
        }
        RefreshCommandStates();
        RefreshConversationNavigator();
    }

    public async Task SubmitInputAsync()
    {
        var input = InputText.Trim();
        if (input.Length == 0)
        {
            return;
        }

        var reply = _replyComposer;
        if (reply is not null && input[0] != '/')
        {
            if (ActiveView is null || !Sessions.TryGet(reply.NetworkId, out var replyNetwork) || replyNetwork is null)
            {
                StatusText = "The reply conversation is no longer available.";
                return;
            }

            var replyResult = await Actions.SendReplyAsync(replyNetwork, ActiveView, reply, input).ConfigureAwait(true);
            StatusText = replyResult.Message;
            if (replyResult.Succeeded)
            {
                ClearComposerAfterAcceptedSend();
                InputHistory.Submit(input);
                ClearReplyComposer(updateStatus: false);
            }
            if (replyResult.View is not null)
            {
                SelectView(replyResult.View);
            }
            return;
        }

        var result = await _commands.DispatchAsync(Sessions.ActiveNetwork, ActiveView, input).ConfigureAwait(true);
        StatusText = result.Message;
        if (result.Succeeded)
        {
            ClearComposerAfterAcceptedSend();
            InputHistory.Submit(input);
        }
        if (result.View is not null)
        {
            SelectView(result.View);
        }
    }

    public async Task ExecuteInputAsync(string input)
    {
        InputText = input;
        await SubmitInputAsync().ConfigureAwait(true);
    }

    public void PrepareInput(string text) => InputText = text;

    public bool BeginReply(TranscriptEntry entry)
    {
        string? reason = null;
        if (ActiveView is null)
        {
            StatusText = "Replies are unavailable without an active conversation.";
            return false;
        }

        if (!Sessions.TryGet(ActiveView.NetworkId, out var network) || network is null
            || !Actions.TryCreateReplyComposer(network, ActiveView, entry, out var state, out reason))
        {
            StatusText = reason ?? "Replies are unavailable for this message.";
            return false;
        }

        _replyComposer = state;
        OnPropertyChanged(nameof(ReplyComposer));
        OnPropertyChanged(nameof(IsReplying));
        OnPropertyChanged(nameof(ReplyBannerText));
        RefreshCommandStates();
        StatusText = state!.BannerText;
        return true;
    }

    public void CancelReply()
    {
        if (_replyComposer is not null)
        {
            ClearReplyComposer(updateStatus: true);
        }
    }

    public async Task<HistoryNavigationResult?> NavigateReplyParentAsync(TranscriptEntry entry, CancellationToken cancellationToken = default)
    {
        if (ActiveView is null
            || !Sessions.TryGet(ActiveView.NetworkId, out var network)
            || network is null)
        {
            StatusText = "The reply conversation is no longer available.";
            return null;
        }

        var result = await Sessions.NavigateReplyParentAsync(network, ActiveView, entry, cancellationToken).ConfigureAwait(true);
        StatusText = result.Message;
        return result;
    }

    public async Task<CommandDispatchResult> ToggleReactionAsync(
        TranscriptEntry entry,
        string value,
        bool remove,
        CancellationToken cancellationToken = default)
    {
        if (ActiveView is not (ChannelView or QueryView)
            || !Sessions.TryGet(ActiveView.NetworkId, out var network)
            || network is null)
        {
            var unavailable = CommandDispatchResult.Failure("Reactions are unavailable without an active conversation.", ActiveView);
            StatusText = unavailable.Message;
            return unavailable;
        }

        var result = await Actions.SendReactionAsync(
            network,
            ActiveView,
            entry,
            value,
            remove,
            cancellationToken).ConfigureAwait(true);
        StatusText = result.Message;
        return result;
    }

    public ParticipantActionContext CreateParticipantContext(NetworkWorkspace network, ChannelView channel, ChannelMemberView member) =>
        new(network, channel, member, network.Channels.Where(candidate => candidate.IsJoined).ToArray());

    public void InsertParticipantMention(ParticipantActionContext context)
    {
        if (!ReferenceEquals(ActiveView, context.Channel))
        {
            SelectView(context.Channel);
        }

        InputText = ParticipantMention.Insert(InputText, context.TargetNickname, InputText.Length == 0);
    }

    public void OpenParticipantQuery(ParticipantActionContext context)
    {
        var query = ParticipantActions.OpenQuery(context);
        SelectView(query);
    }

    public void ClearActiveView()
    {
        ActiveView?.ClearEntries();
        StatusText = "Conversation display cleared; stored history was kept.";
    }

    public bool CloseActiveView()
    {
        if (ActiveView is null || ActiveView.Kind == WorkspaceViewKind.ServerStatus)
        {
            return false;
        }

        var closing = ActiveView;
        SaveDraft();
        var closed = Sessions.CloseView(closing.Id);
        if (closed)
        {
            ActiveView = Sessions.ActiveView;
        SetProjectedInput(ActiveView is null ? string.Empty : GetOrCreateDraftState(ActiveView).Text);
            StatusText = $"Closed {closing.Title}; the conversation remains available to reopen.";
            RefreshCommandStates();
        }

        return closed;
    }

    public async Task<bool> PartAndCloseActiveAsync()
    {
        if (ActiveView is not ChannelView channel)
        {
            return false;
        }

        SaveDraft();
        var closed = await Sessions.PartAndCloseAsync(channel.Id).ConfigureAwait(true);
        if (closed)
        {
            ActiveView = Sessions.ActiveView;
            SetProjectedInput(ActiveView is null ? string.Empty : GetOrCreateDraftState(ActiveView).Text);
            StatusText = $"Parted and closed {channel.Channel}.";
            RefreshCommandStates();
        }

        return closed;
    }

    public bool ReopenConversation(WorkspaceView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var reopened = Sessions.ReopenView(view.Id);
        if (reopened)
        {
            SelectView(view);
            StatusText = $"Reopened {view.Title}.";
        }

        return reopened;
    }

    public bool RemoveHistoricalConversation(WorkspaceView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var removed = Sessions.RemoveHistoricalConversation(view.Id);
        if (removed)
        {
            ActiveView = Sessions.ActiveView;
            SetProjectedInput(ActiveView is null ? string.Empty : GetOrCreateDraftState(ActiveView).Text);
            StatusText = $"Removed {view.Title} from the workspace; stored history was kept.";
            RefreshCommandStates();
        }

        return removed;
    }

    public void NavigateInputHistory(InputHistoryDirection direction)
    {
        InputText = InputHistory.Navigate(direction, InputText);
    }

    public int CompleteInput(int caretIndex)
    {
        var result = Completion.Complete(InputText, caretIndex, Sessions.ActiveNetwork, ActiveView);
        if (!result.Completed)
        {
            return caretIndex;
        }

        InputText = result.Text;
        return result.CaretIndex;
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(ShutdownAsync());
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownGate)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        StatusText = "Closing sessions…";
        SaveDraft();
        Sessions.SynchronizedDraftStateReceived -= OnSynchronizedDraftStateReceived;
        foreach (var cancellation in _draftDebounces.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _draftDebounces.Clear();
        await FlushDraftMutationsAsync().ConfigureAwait(true);
        await Sessions.DisposeAsync().ConfigureAwait(true);
        _notificationAdapter?.Dispose();
        if (Configuration is not null)
        {
            await Configuration.SaveAsync().ConfigureAwait(true);
        }
        foreach (var provider in _sessionCredentials.Values)
        {
            provider.Dispose();
        }
        _sessionCredentials.Clear();
        foreach (var provider in _sessionServerPasswords.Values)
        {
            provider.Dispose();
        }
        _sessionServerPasswords.Clear();
        if (_resumeStateStore is IDisposable disposableResumeStore)
        {
            disposableResumeStore.Dispose();
        }
    }

    private async Task FlushDraftMutationsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var writes = new List<Task<bool>>();
        foreach (var pair in _drafts.ToArray())
        {
            if (!Sessions.TryGetView(pair.Key.ViewId, out _, out var view) || view is null) continue;
            var state = pair.Value;
            if (state.HasConflict || !state.HasSnapshot
                || !NexIrcDraftStateProtocol.TryEncodePayload(state.Text, out _)
                || !Sessions.TryGet(view.NetworkId, out var workspace) || workspace is null
                || NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) is not { } key
                || !string.Equals(key, state.ConversationKey, StringComparison.Ordinal))
                continue;
            if (state.PendingMutationId is null)
            {
                if (state.Text == state.ServerText) continue;
                state.PendingMutationId = Guid.NewGuid().ToString("N");
                state.PendingBaseRevision = state.BaseRevision;
                state.PendingText = state.Text;
                PersistDraftState(state);
            }
            if (state.PendingBaseRevision is { } revision && state.PendingText is { } text
                && state.PendingMutationId is { } mutationId)
                writes.Add(Sessions.TryUpdateSynchronizedDraftAsync(view, revision, mutationId, text, timeout.Token).AsTask());
        }
        if (writes.Count == 0) return;
        try { await Task.WhenAll(writes).WaitAsync(timeout.Token).ConfigureAwait(true); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    private void SavePreferencesInBackground()
    {
        if (Configuration is null)
        {
            return;
        }

        Configuration.SetPreferences(CurrentPreferences);
        _ = SavePreferencesAsync();
    }

    private void SaveDraft()
    {
        if (ActiveView is null) return;
        var state = GetOrCreateDraftState(ActiveView);
        state.Text = InputText;
        PersistDraftState(state);
    }

#pragma warning disable CA1859 // The method intentionally selects either protected disk storage or an in-memory fallback.
    private static IConversationDraftStore CreateConversationDraftStore(IResumeSecretProtector? protector)
    {
        if (!OperatingSystem.IsWindows() || protector is null) return new MemoryConversationDraftStore();
        try
        {
            return new ProtectedJsonConversationDraftStore(Path.Combine(ResumeStatePaths.GetDefaultRoot(), "conversation-drafts.json"), protector);
        }
        catch
        {
            return new MemoryConversationDraftStore();
        }
    }
#pragma warning restore CA1859

    private void CaptureActiveDraft(string text)
    {
        if (ActiveView is not (ChannelView or QueryView)) return;
        var state = GetOrCreateDraftState(ActiveView);
        state.Text = text;
        state.HasLocalChanges = true;
        PersistDraftState(state);
        ScheduleDraftSynchronization(ActiveView, state);
    }

    private void ClearComposerAfterAcceptedSend()
    {
        if (ActiveView is (ChannelView or QueryView))
        {
            var state = GetOrCreateDraftState(ActiveView);
            state.ClearAfterSuccessfulSend = !state.HasSnapshot;
        }
        InputText = string.Empty;
    }

    private ComposerDraftState GetOrCreateDraftState(WorkspaceView view)
    {
        var identity = (view.NetworkId, view.Id);
        if (_drafts.TryGetValue(identity, out var existing)) return existing;

        if (!Sessions.TryGet(view.NetworkId, out var workspace) || workspace is null)
            return _drafts[identity] = new ComposerDraftState(view.NetworkId, Guid.Empty, "", "");

        var profileId = workspace.ProfileId ?? workspace.Id;
        var conversationKey = GetLocalConversationKey(view, workspace);
        _persistedDrafts.TryGetValue((profileId, conversationKey), out var persisted);
        var state = persisted is null
            ? new ComposerDraftState(view.NetworkId, profileId, conversationKey, "")
            : new ComposerDraftState(view.NetworkId, profileId, conversationKey, persisted.Text)
            {
                BaseRevision = persisted.BaseRevision,
                ServerText = persisted.ServerText,
                ServerRevision = persisted.ServerRevision,
                HasConflict = persisted.HasConflict,
                PendingMutationId = persisted.PendingMutationId,
                PendingBaseRevision = persisted.PendingBaseRevision,
                PendingText = persisted.PendingText,
                HasLocalChanges = persisted.HasLocalChanges,
                ClearAfterSuccessfulSend = persisted.ClearAfterSuccessfulSend
            };
        _drafts[identity] = state;

        if (Sessions.TryGetSynchronizedDraftState(view, out var remoteDraft))
        {
            state.HasSnapshot = true;
            ApplyDraftAuthority(view, state, remoteDraft ?? new NexIrcDraft(
                NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) ?? conversationKey,
                string.Empty, 0), isSnapshot: true);
        }
        return state;
    }

    private static string GetLocalConversationKey(WorkspaceView view, NetworkWorkspace workspace)
    {
        if (NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) is { } key) return key;
        return view switch
        {
            ChannelView channel => $"local-channel:{nexIRC.Core.State.IrcCaseMappingComparer.Fold(channel.Channel, workspace.Snapshot.Features.CaseMapping)}",
            QueryView query => $"local-query:{nexIRC.Core.State.IrcCaseMappingComparer.Fold(query.Nickname, workspace.Snapshot.Features.CaseMapping)}",
            _ => $"local-view:{view.Id:N}"
        };
    }

    private void PersistDraftState(ComposerDraftState state)
    {
        if (state.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(state.ConversationKey)) return;
        var key = (state.ProfileId, state.ConversationKey);
        if (state.Text.Length == 0 && state.ServerText.Length == 0 && state.BaseRevision == 0
            && state.ServerRevision == 0 && !state.HasConflict && state.PendingMutationId is null
            && !state.HasLocalChanges && !state.ClearAfterSuccessfulSend)
        {
            _persistedDrafts.Remove(key);
        }
        else
        {
            _persistedDrafts[key] = new LocalConversationDraft(state.ProfileId, state.ConversationKey, state.Text,
                state.BaseRevision, state.ServerText, state.ServerRevision, state.HasConflict,
                state.PendingMutationId, state.PendingBaseRevision, state.PendingText,
                state.HasLocalChanges, state.ClearAfterSuccessfulSend);
        }

        if (!_conversationDraftStore.Save(_persistedDrafts.Values.ToArray()))
            StatusText = "Local draft storage could not be updated; this draft remains available until the application closes.";
    }

    private void SetProjectedInput(string text)
    {
        _applyingDraftProjection = true;
        try { InputText = text; }
        finally { _applyingDraftProjection = false; }
    }

    private void ScheduleDraftSynchronization(WorkspaceView view, ComposerDraftState state)
    {
        if (state.HasConflict || !state.HasSnapshot || state.PendingAwaitingResponse
            || !NexIrcDraftStateProtocol.TryEncodePayload(state.Text, out _)
            || state.Text == state.ServerText && state.PendingMutationId is null
            || !Sessions.TryGet(view.NetworkId, out var workspace) || workspace is null
            || NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) is not { } key
            || !string.Equals(key, state.ConversationKey, StringComparison.Ordinal)
            || !workspace.Session.SynchronizedDraftStateAvailable)
            return;

        var identity = (view.NetworkId, view.Id);
        if (_draftDebounces.Remove(identity, out var oldCancellation))
        {
            oldCancellation.Cancel();
            oldCancellation.Dispose();
        }
        var cancellation = new CancellationTokenSource();
        _draftDebounces[identity] = cancellation;
        _ = DebounceDraftSynchronizationAsync(view, state, identity, cancellation);
    }

    private async Task DebounceDraftSynchronizationAsync(WorkspaceView view, ComposerDraftState state,
        (Guid NetworkId, Guid ViewId) identity, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellation.Token).ConfigureAwait(false);
            _ = _uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (_draftDebounces.TryGetValue(identity, out var current) && ReferenceEquals(current, cancellation))
                {
                    _draftDebounces.Remove(identity);
                    cancellation.Dispose();
                    StartDraftMutation(view, state);
                }
            }));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void StartDraftMutation(WorkspaceView view, ComposerDraftState state)
    {
        if (state.HasConflict || !state.HasSnapshot || state.PendingAwaitingResponse
            || !NexIrcDraftStateProtocol.TryEncodePayload(state.Text, out _)
            || !Sessions.TryGet(view.NetworkId, out var workspace) || workspace is null
            || NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) is not { } key
            || !string.Equals(key, state.ConversationKey, StringComparison.Ordinal))
            return;

        if (state.PendingMutationId is null)
        {
            if (state.Text == state.ServerText) return;
            state.PendingMutationId = Guid.NewGuid().ToString("N");
            state.PendingBaseRevision = state.BaseRevision;
            state.PendingText = state.Text;
        }
        if (state.PendingBaseRevision is not { } baseRevision || state.PendingText is not { } pendingText) return;
        var mutationId = state.PendingMutationId;
        state.PendingAwaitingResponse = true;
        PersistDraftState(state);
        _ = SendDraftMutationAsync(view, state, mutationId, baseRevision, pendingText);
    }

    private async Task SendDraftMutationAsync(WorkspaceView view, ComposerDraftState state, string mutationId,
        long baseRevision, string text)
    {
        var sent = false;
        try
        {
            sent = await Sessions.TryUpdateSynchronizedDraftAsync(view, baseRevision, mutationId, text).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        _ = _uiDispatcher.BeginInvoke(new Action(() =>
        {
            if (string.Equals(state.PendingMutationId, mutationId, StringComparison.Ordinal))
            {
                state.PendingAwaitingResponse = sent;
                PersistDraftState(state);
            }
        }));
    }

    internal void OnSynchronizedDraftStateReceived(NetworkWorkspace workspace, IrcNexIrcDraftStateEvent stateEvent)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _uiDispatcher.BeginInvoke(new Action(() => OnSynchronizedDraftStateReceived(workspace, stateEvent)));
            return;
        }

        var views = workspace.Channels.Cast<WorkspaceView>().Concat(workspace.Queries).ToArray();
        if (stateEvent.Snapshot is { } snapshot)
        {
            var remote = snapshot.Drafts.ToDictionary(static draft => draft.ConversationKey, StringComparer.Ordinal);
            foreach (var view in views)
            {
                if (NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) is not { } key) continue;
                var authority = remote.TryGetValue(key, out var draft) ? draft : new NexIrcDraft(key, string.Empty, 0);
                ApplyDraftAuthority(view, GetOrCreateDraftState(view), authority, isSnapshot: true);
            }
        }
        else if (stateEvent.Draft is { } update)
        {
            foreach (var view in views.Where(view => string.Equals(
                NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot), update.ConversationKey, StringComparison.Ordinal)))
                ApplyDraftAuthority(view, GetOrCreateDraftState(view), update, isSnapshot: false);
        }
        else if (stateEvent.ConversationKey is { } key && stateEvent.AuthoritativeRevision is { } revision)
        {
            var targetView = views.FirstOrDefault(view => string.Equals(
                NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot), key, StringComparison.Ordinal));
            if (targetView is not null)
                ApplyDraftAcknowledgement(targetView, GetOrCreateDraftState(targetView), stateEvent, key, revision);
        }
        RefreshDraftConflictState();
    }

    private void ApplyDraftAuthority(WorkspaceView view, ComposerDraftState state, NexIrcDraft authority, bool isSnapshot)
    {
        if (state.ConversationKey != authority.ConversationKey
            && state.ConversationKey.StartsWith("local-query:", StringComparison.Ordinal)
            && state.Text.Length > 0)
        {
            state.HasSnapshot = true;
            state.ServerText = authority.Text;
            state.ServerRevision = authority.Revision;
            state.HasConflict = true;
            state.HasLocalChanges = true;
            PersistDraftState(state);
            RefreshDraftConflictState();
            return;
        }

        if (state.ConversationKey != authority.ConversationKey)
        {
            _persistedDrafts.Remove((state.ProfileId, state.ConversationKey));
            state.ConversationKey = authority.ConversationKey;
        }

        state.HasSnapshot = true;
        if (isSnapshot) state.PendingAwaitingResponse = false;

        if (state.PendingMutationId is { } pendingId
            && state.PendingBaseRevision is { } pendingBase
            && state.PendingText is { } pendingText)
        {
            if (authority.Revision > pendingBase && string.Equals(authority.Text, pendingText, StringComparison.Ordinal))
            {
                state.ServerText = authority.Text;
                state.ServerRevision = authority.Revision;
                state.BaseRevision = authority.Revision;
                state.HasLocalChanges = state.Text != pendingText;
                state.ClearAfterSuccessfulSend = false;
                ClearPendingMutation(state);
                state.HasConflict = false;
            }
            else if (authority.Revision == pendingBase && string.Equals(authority.Text, state.ServerText, StringComparison.Ordinal))
            {
                state.ServerText = authority.Text;
                state.ServerRevision = authority.Revision;
                state.BaseRevision = authority.Revision;
                state.PendingAwaitingResponse = false;
                PersistDraftState(state);
                ScheduleDraftSynchronization(view, state);
                return;
            }
            else if (authority.Revision != pendingBase || !string.Equals(authority.Text, state.ServerText, StringComparison.Ordinal))
            {
                state.ServerText = authority.Text;
                state.ServerRevision = authority.Revision;
                state.HasConflict = true;
                state.HasLocalChanges = true;
                state.ClearAfterSuccessfulSend = false;
                ClearPendingMutation(state);
            }
            else
            {
                state.PendingAwaitingResponse = false;
                ScheduleDraftSynchronization(view, state);
                return;
            }
        }
        else if (state.ClearAfterSuccessfulSend)
        {
            var serverStillAtKnownBase = authority.Revision == state.BaseRevision
                && string.Equals(authority.Text, state.ServerText, StringComparison.Ordinal);
            state.ClearAfterSuccessfulSend = false;
            state.ServerText = authority.Text;
            state.ServerRevision = authority.Revision;
            state.HasConflict = !serverStillAtKnownBase && state.Text != authority.Text;
            state.HasLocalChanges = state.Text != authority.Text;
            if (!state.HasConflict) state.BaseRevision = authority.Revision;
        }
        else if (state.HasConflict)
        {
            state.ServerText = authority.Text;
            state.ServerRevision = authority.Revision;
        }
        else if (!state.HasLocalChanges)
        {
            state.Text = authority.Text;
            state.ServerText = authority.Text;
            state.ServerRevision = authority.Revision;
            state.BaseRevision = authority.Revision;
        }
        else if (state.Text == authority.Text)
        {
            state.ServerText = authority.Text;
            state.ServerRevision = authority.Revision;
            state.BaseRevision = authority.Revision;
            state.HasLocalChanges = false;
        }
        else if (state.BaseRevision == authority.Revision
            && string.Equals(state.ServerText, authority.Text, StringComparison.Ordinal))
        {
            state.ServerText = authority.Text;
            state.ServerRevision = authority.Revision;
        }
        else
        {
            state.ServerText = authority.Text;
            state.ServerRevision = authority.Revision;
            state.HasConflict = true;
            state.HasLocalChanges = true;
        }

        if (ReferenceEquals(ActiveView, view) && !state.HasConflict)
            SetProjectedInput(state.Text);
        PersistDraftState(state);
        if (!state.HasConflict && state.Text != state.ServerText)
            ScheduleDraftSynchronization(view, state);
    }

    private void ApplyDraftAcknowledgement(WorkspaceView view, ComposerDraftState state,
        IrcNexIrcDraftStateEvent stateEvent, string conversationKey, long revision)
    {
        if (!string.Equals(stateEvent.Status, "CONFLICT", StringComparison.OrdinalIgnoreCase)
            && (state.PendingMutationId is null || !string.Equals(state.PendingMutationId, stateEvent.MutationId, StringComparison.Ordinal)))
            return;

        if (string.Equals(stateEvent.Status, "CONFLICT", StringComparison.OrdinalIgnoreCase))
        {
            state.ServerText = stateEvent.AuthoritativeText ?? string.Empty;
            state.ServerRevision = revision;
            state.HasConflict = true;
            state.HasLocalChanges = true;
            state.ClearAfterSuccessfulSend = false;
            ClearPendingMutation(state);
        }
        else if (string.Equals(stateEvent.Status, "APPLIED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stateEvent.Status, "DUPLICATE", StringComparison.OrdinalIgnoreCase))
        {
            state.ServerText = state.PendingText ?? state.Text;
            state.ServerRevision = revision;
            state.BaseRevision = revision;
            state.HasConflict = false;
            state.HasLocalChanges = state.Text != state.ServerText;
            state.ClearAfterSuccessfulSend = false;
            ClearPendingMutation(state);
        }
        else
        {
            state.PendingAwaitingResponse = false;
            PersistDraftState(state);
            return;
        }

        if (ReferenceEquals(ActiveView, view) && !state.HasConflict)
            SetProjectedInput(state.Text);
        PersistDraftState(state);
        RefreshDraftConflictState();
        if (!state.HasConflict && state.Text != state.ServerText)
            ScheduleDraftSynchronization(view, state);
        _ = conversationKey;
    }

    private static void ClearPendingMutation(ComposerDraftState state)
    {
        state.PendingMutationId = null;
        state.PendingBaseRevision = null;
        state.PendingText = null;
        state.PendingAwaitingResponse = false;
    }

    private bool HasActiveDraftConflict() => ActiveView is not null
        && _drafts.TryGetValue((ActiveView.NetworkId, ActiveView.Id), out var state) && state.HasConflict;

    private void KeepServerDraft()
    {
        if (ActiveView is not { } view || !HasActiveDraftConflict() || !_drafts.TryGetValue((view.NetworkId, view.Id), out var state)) return;
        if (Sessions.TryGet(view.NetworkId, out var workspace) && workspace is not null
            && NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) is { } key
            && state.ConversationKey != key)
        {
            _persistedDrafts.Remove((state.ProfileId, state.ConversationKey));
            state.ConversationKey = key;
        }
        state.Text = state.ServerText;
        state.BaseRevision = state.ServerRevision;
        state.HasConflict = false;
        state.HasLocalChanges = false;
        state.ClearAfterSuccessfulSend = false;
        ClearPendingMutation(state);
        SetProjectedInput(state.Text);
        PersistDraftState(state);
        RefreshDraftConflictState();
    }

    private void ReplaceServerDraftWithLocal()
    {
        if (ActiveView is not { } view || !HasActiveDraftConflict() || !_drafts.TryGetValue((view.NetworkId, view.Id), out var state)
            || !Sessions.TryGet(view.NetworkId, out var workspace) || workspace is null
            || NetworkSessionManager.SynchronizedDraftConversationKey(view, workspace.Snapshot) is not { } key)
            return;

        _persistedDrafts.Remove((state.ProfileId, state.ConversationKey));
        state.ConversationKey = key;
        state.BaseRevision = state.ServerRevision;
        state.HasConflict = false;
        state.HasLocalChanges = true;
        state.ClearAfterSuccessfulSend = false;
        ClearPendingMutation(state);
        if (state.Text != state.ServerText)
        {
            state.PendingMutationId = Guid.NewGuid().ToString("N");
            state.PendingBaseRevision = state.BaseRevision;
            state.PendingText = state.Text;
        }
        PersistDraftState(state);
        RefreshDraftConflictState();
        StartDraftMutation(view, state);
    }

    private void RefreshDraftConflictState()
    {
        OnPropertyChanged(nameof(IsDraftConflictVisible));
        OnPropertyChanged(nameof(DraftConflictText));
        (KeepServerDraftCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (ReplaceServerDraftCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
    }

    private void ClearReplyComposer(bool updateStatus)
    {
        if (_replyComposer is null)
        {
            return;
        }

        _replyComposer = null;
        OnPropertyChanged(nameof(ReplyComposer));
        OnPropertyChanged(nameof(IsReplying));
        OnPropertyChanged(nameof(ReplyBannerText));
        if (updateStatus)
        {
            StatusText = "Reply canceled.";
        }
        RefreshCommandStates();
    }

    private void RemoveDraftsForNetwork(Guid networkId)
    {
        foreach (var key in _drafts.Keys.Where(key => key.NetworkId == networkId).ToArray())
        {
            _drafts.Remove(key);
            if (_draftDebounces.Remove(key, out var cancellation))
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
        }
    }

    private void ActivateConversation(object? parameter)
    {
        if (parameter is ConversationNavigationItem item
            && Sessions.TryGetView(item.ViewId, out _, out var view)
            && view is not null)
        {
            SelectView(view);
        }
    }

    private void OnNavigationChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _navigationRefreshRequests);
        if (Interlocked.Exchange(ref _navigationRefreshPending, 1) != 0)
        {
            Interlocked.Increment(ref _navigationRefreshCoalescedRequests);
            return;
        }

        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                RefreshConversationNavigator();
                RefreshCommandStates();
            }
            finally
            {
                Volatile.Write(ref _navigationRefreshPending, 0);
            }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RefreshConversationNavigator()
    {
        Interlocked.Increment(ref _navigationRefreshExecutions);
        ConversationNavigator.Clear();
        foreach (var item in Sessions.GetConversationNavigator())
        {
            ConversationNavigator.Add(item);
        }
    }

    private static bool IsSameHistoryRecord(ConversationLogRecord left, ConversationLogRecord right) =>
        left.Timestamp == right.Timestamp
        && string.Equals(left.Sender, right.Sender, StringComparison.Ordinal)
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && left.Direction == right.Direction;

    private static bool IsSameHistoryRecord(ConversationLogRecord record, ConversationLogSearchResult result) =>
        record.Timestamp == result.Timestamp
        && string.Equals(record.Sender, result.Sender, StringComparison.Ordinal)
        && record.MessageKind == result.MessageKind
        && record.Text.Contains(result.Preview.TrimStart('…'), StringComparison.Ordinal)
        && record.ConversationKind == result.ConversationKind
        && nexIRC.Core.State.IrcCaseMappingComparer.Equals(record.ConversationName, result.ConversationName, nexIRC.Core.State.IrcCaseMapping.Rfc1459);

    private async Task SavePreferencesAsync()
    {
        try
        {
            if (Configuration is not null)
            {
                await Configuration.SaveAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // A transient preference-save failure is reported by the next
            // explicit save/shutdown path and never interrupts IRC processing.
        }
    }

    private static bool IsEquivalent(NetworkConnectionOptions left, NetworkConnectionOptions right) =>
        left.DisplayName == right.DisplayName
        && left.Endpoint == right.Endpoint
        && left.Nickname == right.Nickname
        && left.Username == right.Username
        && left.RealName == right.RealName
        && left.SaslPolicy == right.SaslPolicy
        && left.PasswordProvider?.GetType() == right.PasswordProvider?.GetType()
        && left.AutoConnect == right.AutoConnect
        && left.Reconnect == right.Reconnect
        && left.NicknameFallbacks.SequenceEqual(right.NicknameFallbacks, StringComparer.Ordinal)
        && left.DesiredChannels.SetEquals(right.DesiredChannels);

    private async Task DisconnectSelectedAsync()
    {
        if (Sessions.ActiveNetwork is null)
        {
            return;
        }

        await Sessions.DisconnectAsync(Sessions.ActiveNetwork.Id).ConfigureAwait(true);
        StatusText = "Disconnect requested.";
    }

    private async Task ReconnectSelectedAsync()
    {
        if (Sessions.ActiveNetwork is null)
        {
            return;
        }

        StatusText = "Reconnecting…";
        await Sessions.ReconnectAsync(Sessions.ActiveNetwork.Id).ConfigureAwait(true);
        ActiveView = Sessions.ActiveNetwork.StatusView;
        RefreshCommandStates();
    }

    private bool HasActiveNetwork() => Sessions.ActiveNetwork is not null;

    private void ActivateRelativeView(int delta)
    {
        var views = Sessions.Networks.SelectMany(network => network.Views).ToArray();
        if (views.Length == 0)
        {
            return;
        }

        var index = ActiveView is null ? 0 : Array.IndexOf(views, ActiveView);
        var next = (index + delta + views.Length) % views.Length;
        SelectView(views[next]);
    }

    private void RefreshCommandStates()
    {
        (DisconnectCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (ReconnectCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (PartCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (NextViewCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (PreviousViewCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (NextConversationCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (PreviousConversationCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (NextUnreadCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (PreviousUnreadCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (NextHighlightCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (PreviousHighlightCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (BackConversationCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (ForwardConversationCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (CancelReplyCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (KeepServerDraftCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (ReplaceServerDraftCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
    }

    private sealed class ComposerDraftState(Guid networkId, Guid profileId, string conversationKey, string text)
    {
        public Guid NetworkId { get; } = networkId;

        public Guid ProfileId { get; } = profileId;

        public string ConversationKey { get; set; } = conversationKey;

        public string Text { get; set; } = text;

        public long BaseRevision { get; set; }

        public string ServerText { get; set; } = string.Empty;

        public long ServerRevision { get; set; }

        public bool HasConflict { get; set; }

        public bool HasLocalChanges { get; set; }

        public bool ClearAfterSuccessfulSend { get; set; }

        public bool HasSnapshot { get; set; }

        public bool PendingAwaitingResponse { get; set; }

        public string? PendingMutationId { get; set; }

        public long? PendingBaseRevision { get; set; }

        public string? PendingText { get; set; }
    }

    private abstract class RelayCommandBase : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public abstract bool CanExecute(object? parameter);

        public abstract void Execute(object? parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : RelayCommandBase
    {
        private readonly Action _execute = execute;
        private readonly Func<bool> _canExecute = canExecute ?? (() => true);

        public override bool CanExecute(object? parameter) => _canExecute();

        public override void Execute(object? parameter) => _execute();
    }

    private sealed class ParameterizedRelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : RelayCommandBase
    {
        private readonly Action<object?> _execute = execute;
        private readonly Func<object?, bool> _canExecute = canExecute ?? (_ => true);

        public override bool CanExecute(object? parameter) => _canExecute(parameter);

        public override void Execute(object? parameter) => _execute(parameter);
    }

    private sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : RelayCommandBase
    {
        private readonly Func<Task> _execute = execute;
        private readonly Func<bool> _canExecute = canExecute ?? (() => true);
        private bool _running;

        public override bool CanExecute(object? parameter) => !_running && _canExecute();

        public override async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _running = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute().ConfigureAwait(true);
            }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }
    }
}

public sealed record MainWindowPresentationDiagnostics(
    long NavigationRefreshRequests,
    long NavigationRefreshExecutions,
    long NavigationRefreshCoalescedRequests);
