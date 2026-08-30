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

    public MainWindowViewModel(IIrcTransportFactory transportFactory, System.Windows.Threading.Dispatcher dispatcher)
    {
        Sessions = new NetworkSessionManager(transportFactory, new WpfWorkspaceDispatcher(dispatcher));
        _commands = new IrcCommandDispatcher(Sessions);
        NewConnectionCommand = new AsyncRelayCommand(() => NewConnectionRequested?.Invoke() ?? Task.CompletedTask);
        DisconnectCommand = new AsyncRelayCommand(DisconnectSelectedAsync, HasActiveNetwork);
        ReconnectCommand = new AsyncRelayCommand(ReconnectSelectedAsync, HasActiveNetwork);
        JoinCommand = new RelayCommand(() => InputText = "/join ");
        PartCommand = new RelayCommand(() => InputText = "/part", () => ActiveView is ChannelView);
        QueryCommand = new RelayCommand(() => InputText = "/query ");
        NextViewCommand = new RelayCommand(() => ActivateRelativeView(1), () => Sessions.Networks.Count > 0);
        PreviousViewCommand = new RelayCommand(() => ActivateRelativeView(-1), () => Sessions.Networks.Count > 0);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());

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

    public ObservableCollection<NetworkWorkspace> Networks => Sessions.Networks;

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
        set => SetProperty(ref _isNetworkTreeVisible, value);
    }

    public bool IsMemberListVisible
    {
        get => _isMemberListVisible;
        set => SetProperty(ref _isMemberListVisible, value);
    }

    public bool IsToolbarVisible
    {
        get => _isToolbarVisible;
        set => SetProperty(ref _isToolbarVisible, value);
    }

    public bool IsStatusBarVisible
    {
        get => _isStatusBarVisible;
        set => SetProperty(ref _isStatusBarVisible, value);
    }

    public ICommand NewConnectionCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand ReconnectCommand { get; }

    public ICommand JoinCommand { get; }

    public ICommand PartCommand { get; }

    public ICommand QueryCommand { get; }

    public ICommand NextViewCommand { get; }

    public ICommand PreviousViewCommand { get; }

    public ICommand ExitCommand { get; }

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
    }

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
