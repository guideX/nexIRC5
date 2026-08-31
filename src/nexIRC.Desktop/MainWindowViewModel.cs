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

    public MainWindowViewModel(
        IIrcTransportFactory transportFactory,
        System.Windows.Threading.Dispatcher dispatcher,
        ConfigurationService? configuration = null)
    {
        Configuration = configuration;
        Sessions = new NetworkSessionManager(transportFactory, new WpfWorkspaceDispatcher(dispatcher), configuration: configuration);
        if (configuration is not null)
        {
            _notificationAdapter = new DesktopNotificationAdapter(Sessions.Notifications, () => CurrentPreferences);
        }
        _commands = new IrcCommandDispatcher(Sessions);
        InputHistory = new InputHistory();
        Completion = new CompletionEngine();
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
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());

        HighlightPolicy.PropertyChanged += (_, _) => SavePreferencesInBackground();

        Sessions.Networks.CollectionChanged += (_, _) =>
        {
            if (ActiveView is null && Sessions.Networks.Count > 0)
            {
                SelectView(Sessions.Networks[0].StatusView);
            }

            RefreshCommandStates();
        };
    }

    public event Func<Task>? NewConnectionRequested;

    public event Action? ExitRequested;

    public NetworkSessionManager Sessions { get; }

    public ConfigurationService? Configuration { get; }

    public ObservableCollection<NetworkWorkspace> Networks => Sessions.Networks;

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
        set => SetProperty(ref _inputText, value);
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
                var workspace = Sessions.Add(profile.ToConnectionOptions());
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

        var options = profile.ToConnectionOptions();
        var workspace = Sessions.Networks.FirstOrDefault(network => network.ProfileId == profileId);
        if (workspace is not null && !IsEquivalent(workspace.Options, options))
        {
            await Sessions.RemoveAsync(workspace.Id).ConfigureAwait(true);
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

        Sessions.ActivateView(view.Id);
        ActiveView = view;
        StatusText = $"{view.Title} · {view.Kind}";
        if (Configuration is not null && Sessions.ActiveNetwork?.ProfileId is Guid profileId)
        {
            Configuration.SetPreferences(CurrentPreferences with { LastSelectedNetworkProfileId = profileId });
            SavePreferencesInBackground();
        }
        RefreshCommandStates();
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
            ActiveView = result.View;
            RefreshCommandStates();
        }
    }

    public async Task ExecuteInputAsync(string input)
    {
        InputText = input;
        await SubmitInputAsync().ConfigureAwait(true);
    }

    public void PrepareInput(string text) => InputText = text;

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
