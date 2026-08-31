using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.State;
using ContextMenu = System.Windows.Controls.ContextMenu;
using ListBox = System.Windows.Controls.ListBox;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace nexIRC.Desktop;

public partial class MainWindow : Window
{
    private bool _closing;

    public MainWindow(
        IIrcTransportFactory transportFactory,
        ConfigurationService? configuration = null,
        ProfileCredentialService? credentials = null,
        IConversationLogStore? logStore = null)
    {
        InitializeComponent();
        ViewModel = new MainWindowViewModel(
            transportFactory,
            Dispatcher,
            configuration ?? new ConfigurationService(new InMemoryConfigurationStore()),
            credentials,
            logStore);
        DataContext = ViewModel;
        ViewModel.NewConnectionRequested += ShowNewConnectionAsync;
        ViewModel.ExitRequested += Close;
        ViewModel.NotificationActivationRequested += _ =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Focus();
        };
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewRightClick), true);
    }

    public MainWindowViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var state = ViewModel.CurrentPreferences.ViewState;
        var bounds = SystemParameters.WorkArea;
        var valid = ViewStateValidator.Normalize(state, new ViewportBounds(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        Width = valid.WindowWidth;
        Height = valid.WindowHeight;
        if (valid.WindowLeft is double left) Left = left;
        if (valid.WindowTop is double top) Top = top;
        NavigationColumn.Width = new GridLength(valid.NavigationPaneWidth);
        if (valid.IsMaximized) WindowState = WindowState.Maximized;
    }

    private async Task ShowNewConnectionAsync()
    {
        var dialog = new NewConnectionWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Options is null)
        {
            return;
        }

        var workspace = ViewModel.Sessions.Add(dialog.Options);
        ViewModel.SelectView(workspace.StatusView);
        await ViewModel.Sessions.ConnectAsync(workspace.Id).ConfigureAwait(true);
        ViewModel.StatusText = $"Connecting to {dialog.Options.Endpoint}.";
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => ViewModel.SelectView(e.NewValue);

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SubmitInputAsync();

    private async void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down)
        {
            ViewModel.NavigateInputHistory(e.Key == Key.Up ? InputHistoryDirection.Older : InputHistoryDirection.Newer);
            InputBox.CaretIndex = InputBox.Text.Length;
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Tab)
        {
            InputBox.CaretIndex = ViewModel.CompleteInput(InputBox.CaretIndex);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        await SubmitInputAsync();
    }

    private async Task SubmitInputAsync()
    {
        await ViewModel.SubmitInputAsync();
        InputBox.Focus();
    }

    private void OnServerStatusClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sessions.ActiveNetwork is { } network)
        {
            ViewModel.SelectView(network.StatusView);
        }
    }

    private void OnNicknameClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareInput("/nick ");
        InputBox.Focus();
    }

    private void OnRawCommandClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareInput("/raw ");
        InputBox.Focus();
    }

    private void OnWhoisClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareInput("/whois ");
        InputBox.Focus();
    }

    private void OnListClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareInput("/list ");
        InputBox.Focus();
    }

    private async void OnRejoinClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sessions.ActiveNetwork is { } network && ViewModel.ActiveView is ChannelView channel)
        {
            await ViewModel.ExecuteInputAsync($"/rejoin {channel.Channel}");
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel.ClearActiveView();

    private void OnCloseViewClick(object sender, RoutedEventArgs e) => ViewModel.CloseActiveView();

    private void OnProfilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NetworkProfilesWindow(ViewModel) { Owner = this };
        dialog.ShowDialog();
    }

    private async void OnPreferencesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PreferencesWindow(ViewModel) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.StatusText = "Preferences saved.";
            await Task.CompletedTask;
        }
    }

    private void OnFavoritesClick(object sender, RoutedEventArgs e) =>
        new FavoritesWindow(ViewModel) { Owner = this }.ShowDialog();

    private void OnAliasesClick(object sender, RoutedEventArgs e) =>
        new AliasesWindow(ViewModel) { Owner = this }.ShowDialog();

    private void OnLogsClick(object sender, RoutedEventArgs e) =>
        new LogViewerWindow(ViewModel) { Owner = this }.ShowDialog();

    private async void OnChannelListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is ChannelListRow row)
        {
            await ViewModel.ExecuteInputAsync($"/join {row.Channel}");
        }
    }

    private void OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var treeItem = FindAncestor<TreeViewItem>(source);
        if (treeItem?.DataContext is NetworkWorkspace network)
        {
            treeItem.IsSelected = true;
            ViewModel.SelectView(network.StatusView);
            OpenContextMenu(treeItem, [
                ("Connect", new Func<Task>(() => ViewModel.Sessions.ConnectAsync(network.Id).AsTask())),
                ("Disconnect", new Func<Task>(() => ViewModel.Sessions.DisconnectAsync(network.Id).AsTask())),
                ("Reconnect", new Func<Task>(() => ViewModel.Sessions.ReconnectAsync(network.Id).AsTask())),
                ("Open Status", () => { ViewModel.SelectView(network.StatusView); return Task.CompletedTask; }),
                ("Remove", new Func<Task>(() => ViewModel.Sessions.RemoveAsync(network.Id).AsTask()))
            ]);
            e.Handled = true;
            return;
        }

        if (treeItem?.DataContext is WorkspaceView view)
        {
            if (!ViewModel.Sessions.TryGet(view.NetworkId, out var viewNetwork) || viewNetwork is null)
            {
                return;
            }

            treeItem.IsSelected = true;
            ViewModel.SelectView(view);
            var actions = new List<(string, Func<Task>)>
            {
                ("Activate", () => { ViewModel.SelectView(view); return Task.CompletedTask; })
            };
            if (view is ChannelView channel)
            {
                actions.Add((channel.IsJoined ? "Part" : "Join", () => ViewModel.ExecuteInputAsync(channel.IsJoined ? $"/part {channel.Channel}" : $"/join {channel.Channel}")));
                actions.Add(("Rejoin", () => ViewModel.ExecuteInputAsync($"/rejoin {channel.Channel}")));
                actions.Add((IsFavorite(viewNetwork, DestinationKind.Channel, channel.Channel) ? "Remove favorite" : "Add to favorites", () => ToggleFavorite(viewNetwork, channel, DestinationKind.Channel, channel.Channel)));
                actions.Add(("Copy channel name", () => CopyText(channel.Channel)));
                actions.Add(("Request channel modes", () => ViewModel.ExecuteInputAsync($"/mode {channel.Channel}")));
                actions.Add(("Request topic", () => ViewModel.ExecuteInputAsync($"/topic {channel.Channel}")));
                actions.Add(("Open LIST", () => ViewModel.ExecuteInputAsync("/list")));
                actions.Add(("Open history", () => OpenHistory(channel)));
                actions.Add(("Search this conversation", () => OpenHistory(channel)));
                actions.Add(("Clear conversation display", () => { channel.ClearEntries(); return Task.CompletedTask; }));
                actions.Add(("Close view (stay joined)", () => { ViewModel.CloseActiveView(); return Task.CompletedTask; }));
            }
            else if (view is QueryView query)
            {
                actions.Add((IsFavorite(viewNetwork, DestinationKind.Query, query.Nickname) ? "Remove favorite" : "Add to favorites", () => ToggleFavorite(viewNetwork, query, DestinationKind.Query, query.Nickname)));
                actions.Add(("Copy nickname", () => CopyText(query.Nickname)));
                actions.Add(("Open history", () => OpenHistory(query)));
                actions.Add(("Search this conversation", () => OpenHistory(query)));
                actions.Add(("Clear conversation display", () => { query.ClearEntries(); return Task.CompletedTask; }));
                actions.Add(("Close query", () => { ViewModel.CloseActiveView(); return Task.CompletedTask; }));
            }

            OpenContextMenu(treeItem, actions);
            e.Handled = true;
            return;
        }

        var memberItem = FindAncestor<ListBoxItem>(source);
        if (memberItem?.DataContext is ChannelMemberView member)
        {
            var channel = FindAncestor<ListBox>(memberItem)?.DataContext as ChannelView;
            if (channel is not null)
            {
                ViewModel.SelectView(channel);
                var actions = new List<(string, Func<Task>)>
                {
                    ("Query / open private conversation", () => ViewModel.ExecuteInputAsync($"/query {member.Nickname}")),
                    ("WHOIS", () => ViewModel.ExecuteInputAsync($"/whois {member.Nickname}")),
                    ("Mention / insert nickname", () => { ViewModel.PrepareInput($"{member.Nickname}: "); InputBox.Focus(); return Task.CompletedTask; }),
                    ("Copy nickname", () => CopyText(member.Nickname)),
                    ("Notice", () => { ViewModel.PrepareInput($"/notice {member.Nickname} "); InputBox.Focus(); return Task.CompletedTask; }),
                    ("CTCP VERSION", () => ViewModel.ExecuteInputAsync($"/ctcp {member.Nickname} VERSION")),
                    ("CTCP TIME", () => ViewModel.ExecuteInputAsync($"/ctcp {member.Nickname} TIME")),
                    ("CTCP PING", () => ViewModel.ExecuteInputAsync($"/ctcp {member.Nickname} PING"))
                };
                if (channel.CanModerate)
                {
                    actions.Add(("Give operator", () => ViewModel.ExecuteInputAsync($"/op {member.Nickname}")));
                    actions.Add(("Remove operator", () => ViewModel.ExecuteInputAsync($"/deop {member.Nickname}")));
                    actions.Add(("Give voice", () => ViewModel.ExecuteInputAsync($"/voice {member.Nickname}")));
                    actions.Add(("Remove voice", () => ViewModel.ExecuteInputAsync($"/devoice {member.Nickname}")));
                    actions.Add(("Kick", () => ViewModel.ExecuteInputAsync($"/kick {member.Nickname} ")));
                }

                OpenContextMenu(memberItem, actions);
                e.Handled = true;
            }
        }
    }

    private void OpenContextMenu(FrameworkElement target, IEnumerable<(string Header, Func<Task> Action)> actions)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        foreach (var (header, action) in actions)
        {
            var item = new MenuItem { Header = header, Tag = action };
            item.Click += OnGeneratedContextMenuClick;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static async void OnGeneratedContextMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Func<Task> action })
        {
            await action();
        }
    }

    private static Task CopyText(string text)
    {
        System.Windows.Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    private bool IsFavorite(NetworkWorkspace network, DestinationKind kind, string name) =>
        ViewModel.Sessions.Favorites(network.Id, kind).Any(item => IrcCaseMappingComparer.Equals(item.Name, name, network.Snapshot.Features.CaseMapping));

    private async Task ToggleFavorite(NetworkWorkspace network, WorkspaceView view, DestinationKind kind, string name)
    {
        var favorite = ViewModel.Sessions.Favorites(network.Id, kind).FirstOrDefault(item => IrcCaseMappingComparer.Equals(item.Name, name, network.Snapshot.Features.CaseMapping));
        if (favorite is not null)
        {
            ViewModel.RemoveFavorite(favorite.Id);
            ViewModel.StatusText = $"Removed {name} from favorites.";
        }
        else
        {
            await ViewModel.AddCurrentFavoriteAsync(null).ConfigureAwait(true);
        }
    }

    private Task OpenHistory(WorkspaceView view)
    {
        new LogViewerWindow(ViewModel, view) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "nexIRC 5\nA reconnect-aware, multi-network IRC client shell.", "About nexIRC", MessageBoxButton.OK, MessageBoxImage.Information);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        ViewModel.CaptureViewState(ActualWidth, ActualHeight, Left, Top, WindowState == WindowState.Maximized, NavigationColumn.ActualWidth);
        await ViewModel.ShutdownAsync();
        Close();
    }
}
