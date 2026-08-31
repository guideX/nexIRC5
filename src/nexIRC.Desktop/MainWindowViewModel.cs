using System.Collections.ObjectModel;
using System.Windows.Input;
using nexIRC.Application;
using nexIRC.Core.Networking;

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
    private bool _shutdownStarted;
    private readonly DesktopNotificationAdapter? _notificationAdapter;
    private readonly System.Windows.Threading.Dispatcher _uiDispatcher;
    private readonly Dictionary<Guid, MemorySaslCredentialProvider> _sessionCredentials = [];
    private readonly Dictionary<Guid, MemoryServerPasswordProvider> _sessionServerPasswords = [];
    private readonly Dictionary<(Guid NetworkId, Guid ViewId), string> _drafts = [];

    public MainWindowViewModel(
        IIrcTransportFactory transportFactory,
        System.Windows.Threading.Dispatcher dispatcher,
        ConfigurationService? configuration = null,
        ProfileCredentialService? credentials = null,
        IConversationLogStore? logStore = null)
    {
        _uiDispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Configuration = configuration;
        Credentials = credentials ?? new ProfileCredentialService(
            OperatingSystem.IsWindows()
                ? new WindowsCredentialStore()
                : new InMemoryProfileCredentialStore());
        Sessions = new NetworkSessionManager(transportFactory, new WpfWorkspaceDispatcher(dispatcher), configuration: configuration, logStore: logStore);
        if (configuration is not null)
        {
            _notificationAdapter = new DesktopNotificationAdapter(Sessions.Notifications, () => CurrentPreferences, notification => { RouteNotification(notification); });
        }
        _commands = new IrcCommandDispatcher(Sessions);
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
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());

        HighlightPolicy.PropertyChanged += (_, _) => SavePreferencesInBackground();

        Sessions.Networks.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
            {
                foreach (var oldNetwork in args.OldItems.OfType<NetworkWorkspace>())
                {
                    RemoveDraftsForNetwork(oldNetwork.Id);
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
        RefreshConversationNavigator();
    }

    public event Func<Task>? NewConnectionRequested;

    public event Action? ExitRequested;

    public NetworkSessionManager Sessions { get; }

    public ConfigurationService? Configuration { get; }

    public ProfileCredentialService Credentials { get; }

    public ProfilePortabilityService? ProfilePortability => Configuration is null ? null : new ProfilePortabilityService(Configuration);

    public IReadOnlyList<AliasDefinition> Aliases => Configuration?.Aliases ?? Array.Empty<AliasDefinition>();

    public ObservableCollection<NetworkWorkspace> Networks => Sessions.Networks;

    public ObservableCollection<ConversationNavigationItem> ConversationNavigator { get; } = [];

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
        set => SetProperty(ref _inputText, (value ?? string.Empty)[..Math.Min(value?.Length ?? 0, ConfigurationLimits.MaximumDraftLength)]);
    }

    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value);
    }

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

    public async Task<bool> RouteLogSearchResultAsync(ConversationLogSearchResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var network = Sessions.Networks.FirstOrDefault(item => item.Id == result.NetworkId)
            ?? (result.ProfileId is Guid profileId ? Sessions.Networks.FirstOrDefault(item => item.ProfileId == profileId) : null);
        if (network is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceView view = result.ConversationKind switch
            {
                LogConversationKind.Channel => Sessions.OpenHistoricalConversation(network.Id, DestinationKind.Channel, result.ConversationName),
                LogConversationKind.PrivateConversation => Sessions.OpenHistoricalConversation(network.Id, DestinationKind.Query, result.ConversationName),
                _ => network.StatusView
            };
            SelectView(view);
            if (Sessions.LogStore is { } store)
            {
                var page = await store.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = result.Location.ScopeId,
                    ConversationKind = result.ConversationKind,
                    ConversationName = result.ConversationName,
                    PageSize = ConfigurationLimits.MaximumHistoryContextEntries,
                    Around = result.Timestamp
                }, cancellationToken).ConfigureAwait(true);
                view.SetHistoryContext(
                    page.Records.Select(record => new HistoryContextEntry(record, IsSameHistoryRecord(record, result))),
                    result.Preview);
            }
            else
            {
                view.SetHistoryContext([new HistoryContextEntry(result.Record, true)], result.Preview);
            }

            StatusText = $"Opened {result.ConversationName} with surrounding history.";
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
        Sessions.ActivateView(view.Id);
        if (!ReferenceEquals(Sessions.ActiveView, view))
        {
            return;
        }
        ActiveView = view;
        InputText = _drafts.TryGetValue((view.NetworkId, view.Id), out var draft) ? draft : string.Empty;
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

        InputText = string.Empty;
        InputHistory.Submit(input);
        var result = await _commands.DispatchAsync(Sessions.ActiveNetwork, ActiveView, input).ConfigureAwait(true);
        StatusText = result.Message;
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
            InputText = ActiveView is not null && _drafts.TryGetValue((ActiveView.NetworkId, ActiveView.Id), out var draft) ? draft : string.Empty;
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
            InputText = ActiveView is not null && _drafts.TryGetValue((ActiveView.NetworkId, ActiveView.Id), out var draft) ? draft : string.Empty;
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
            _drafts.Remove((view.NetworkId, view.Id));
            ActiveView = Sessions.ActiveView;
            InputText = ActiveView is not null && _drafts.TryGetValue((ActiveView.NetworkId, ActiveView.Id), out var draft) ? draft : string.Empty;
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

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        StatusText = "Closing sessions…";
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
        if (ActiveView is null || string.IsNullOrEmpty(InputText))
        {
            if (ActiveView is not null) _drafts.Remove((ActiveView.NetworkId, ActiveView.Id));
            return;
        }

        _drafts[(ActiveView.NetworkId, ActiveView.Id)] = InputText[..Math.Min(InputText.Length, ConfigurationLimits.MaximumDraftLength)];
    }

    private void RemoveDraftsForNetwork(Guid networkId)
    {
        foreach (var key in _drafts.Keys.Where(key => key.NetworkId == networkId).ToArray())
        {
            _drafts.Remove(key);
        }
    }

    private void ActivateConversation(object? parameter)
    {
        if (parameter is ConversationNavigationItem item)
        {
            SelectView(item.ViewId);
        }
    }

    private void OnNavigationChanged(object? sender, EventArgs e)
    {
        if (_uiDispatcher.CheckAccess())
        {
            RefreshConversationNavigator();
            RefreshCommandStates();
            return;
        }

        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            RefreshConversationNavigator();
            RefreshCommandStates();
        }));
    }

    private void RefreshConversationNavigator()
    {
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
