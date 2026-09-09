using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.State;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace nexIRC.Desktop;

public partial class MainWindow : Window
{
    private bool _closing;
    private bool _shutdownComplete;
    private bool _renderingSubscribed;
    private bool _viewportRestoreQueued;
    private readonly PresentationTimingProbe _presentationTiming = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<ListBox> _historyScrollArmed = [];
    private readonly HashSet<ListBox> _historyScrollLoading = [];
    private readonly Dictionary<ListBox, (double Offset, double Extent)> _historyScrollPositions = [];

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
        ViewModel.Sessions.NavigationChanged += OnConversationNavigationChanged;
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewRightClick), true);
        Closed += OnClosed;
    }

    public MainWindowViewModel ViewModel { get; }

    internal PresentationTimingProbe PresentationTiming => _presentationTiming;

    internal bool IsPresentationRenderingSubscribed => _renderingSubscribed;

    internal bool IsPresentationTimingPending => _presentationTiming.HasPendingInteraction;

    internal Task WaitForClosedAsync() => _closed.Task;

    internal async Task CloseAfterSmokeAsync()
    {
        if (!_closing)
        {
            _closing = true;
            await ViewModel.ShutdownAsync().ConfigureAwait(true);
        }

        _shutdownComplete = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_renderingSubscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingSubscribed = true;
        }

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

    private void OnConversationPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ListBox list && e.Delta > 0)
        {
            _historyScrollArmed.Add(list);
            (list.DataContext as WorkspaceView)?.EnterHistoryView();
        }
    }

    private void OnConversationPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is ListBox list && e.Key is Key.Up or Key.PageUp or Key.Home)
        {
            _historyScrollArmed.Add(list);
            (list.DataContext as WorkspaceView)?.EnterHistoryView();
        }
    }

    private async void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var list = sender as ListBox ?? FindAncestor<ListBox>(e.OriginalSource as DependencyObject);
        if (list?.DataContext is WorkspaceView stateView
            && !_historyScrollLoading.Contains(list)
            && e.ExtentHeight > e.ViewportHeight)
        {
            stateView.SetLiveFollow(e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1);
        }
        if (list is null
            || e.VerticalOffset > 1
            || !_historyScrollArmed.Contains(list)
            || _historyScrollLoading.Contains(list)
            || list.DataContext is not WorkspaceView view
            || !ViewModel.Sessions.TryGet(view.NetworkId, out var network)
            || network is null)
        {
            return;
        }

        var scroll = e.OriginalSource as ScrollViewer ?? FindDescendant<ScrollViewer>(list);
        if (scroll is null)
        {
            return;
        }

        _historyScrollLoading.Add(list);
        _historyScrollPositions[list] = (scroll.VerticalOffset, scroll.ExtentHeight);
        try
        {
            await ExecuteHistoryActionAsync(network, view, WorkspaceActionId.LoadOlderMessages).ConfigureAwait(true);
            await ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
            await Dispatcher.InvokeAsync(
                () =>
                {
                    scroll.UpdateLayout();
                    var previous = _historyScrollPositions[list];
                    var delta = scroll.ExtentHeight - previous.Extent;
                    scroll.ScrollToVerticalOffset(Math.Max(0, previous.Offset + delta));
                },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        finally
        {
            _historyScrollLoading.Remove(list);
            _historyScrollPositions.Remove(list);
            _historyScrollArmed.Remove(list);
        }
    }

    private async void OnLoadOlderMessagesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: WorkspaceView view }
            || !ViewModel.Sessions.TryGet(view.NetworkId, out var network)
            || network is null)
        {
            return;
        }

        var result = await ExecuteHistoryActionAsync(network, view, WorkspaceActionId.LoadOlderMessages).ConfigureAwait(true);
        ViewModel.StatusText = result.Message;
    }

    private async void OnLoadNewerMessagesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: WorkspaceView view }
            || !ViewModel.Sessions.TryGet(view.NetworkId, out var network)
            || network is null)
        {
            return;
        }

        var result = await ExecuteHistoryActionAsync(network, view, WorkspaceActionId.LoadNewerMessages).ConfigureAwait(true);
        ViewModel.StatusText = result.Message;
    }

    private async void OnReturnToLatestClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: WorkspaceView view }
            || !ViewModel.Sessions.TryGet(view.NetworkId, out var network)
            || network is null)
        {
            return;
        }

        var result = await ExecuteHistoryActionAsync(network, view, WorkspaceActionId.ReturnToLatest).ConfigureAwait(true);
        ViewModel.StatusText = result.Message;
        RestoreConversationViewport();
    }

    private ValueTask<CommandDispatchResult> ExecuteHistoryActionAsync(
        NetworkWorkspace network,
        WorkspaceView view,
        WorkspaceActionId action) => view switch
        {
            ChannelView channel => ViewModel.Actions.ExecuteChannelAsync(network, channel, action),
            QueryView query => ViewModel.Actions.ExecuteQueryAsync(network, query, action),
            _ => ValueTask.FromResult(CommandDispatchResult.Failure("History navigation is available only for channels and queries.", view))
        };

    private void OnConversationNavigationChanged(object? sender, EventArgs e)
    {
        if (ViewModel.ActiveView is not { IsViewingHistory: true })
        {
            return;
        }

        if (_viewportRestoreQueued)
        {
            return;
        }

        _viewportRestoreQueued = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _viewportRestoreQueued = false;
                RestoreConversationViewport();
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RestoreConversationViewport()
    {
        if (ViewModel.ActiveView is not { } view)
        {
            return;
        }

        var list = EnumerateDescendants<ListBox>(this)
            .FirstOrDefault(candidate => ReferenceEquals(candidate.ItemsSource, view.Entries));
        if (list is null)
        {
            return;
        }

        if (view.NavigationAnchor is { } anchor)
        {
            var item = list.Items.Cast<object>().FirstOrDefault(candidate => candidate is TranscriptEntry entry
                && (entry.ServerMessageId is not null
                    ? string.Equals(entry.ServerMessageId, anchor.ServerMessageId, StringComparison.Ordinal)
                    : entry.Timestamp == anchor.Timestamp && entry.Sequence == anchor.Sequence));
            if (item is not null)
            {
                list.SelectedItem = item;
                list.ScrollIntoView(item);
            }
        }
        else if (view.IsFollowingLive)
        {
            FindDescendant<ScrollViewer>(list)?.ScrollToEnd();
        }
    }

    private void OnRendering(object? sender, EventArgs e) => _presentationTiming.RecordRendering(Stopwatch.GetTimestamp());

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_renderingSubscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingSubscribed = false;
        }

        _presentationTiming.CancelPendingInteraction();
        _closed.TrySetResult();
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
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape && ViewModel.IsReplying)
        {
            ViewModel.CancelReply();
            e.Handled = true;
            return;
        }

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

    private async void OnReplyParentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptEntry entry })
        {
            await ViewModel.NavigateReplyParentAsync(entry).ConfigureAwait(true);
        }
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

    private void OnWhoisOpenQueryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is WhoisView whois)
        {
            var query = ViewModel.Sessions.EnsureQuery(whois.NetworkId, whois.RequestedNickname);
            ViewModel.SelectView(query);
        }
    }

    private void OnWhoisCopyNicknameClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is WhoisView whois)
        {
            CopyText(whois.Result.Nickname);
            ViewModel.StatusText = "Copied nickname to the clipboard.";
        }
    }

    private void OnWhoisCopyHostmaskClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is WhoisView whois)
        {
            var result = CopyTextResult(whois.Result.Hostmask, whois, "No hostmask was returned by WHOIS.");
            ViewModel.StatusText = result.Message;
        }
    }

    private void OnWhoisCopyAccountClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is WhoisView whois)
        {
            var result = CopyTextResult(whois.Result.Account, whois, "No account was returned by WHOIS.");
            ViewModel.StatusText = result.Message;
        }
    }

    private void OnWhoisCopyFormattedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is WhoisView whois)
        {
            CopyText(whois.Result.FormattedText);
            ViewModel.StatusText = "Copied formatted WHOIS information to the clipboard.";
        }
    }

    private void OnListClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareInput("/list ");
        InputBox.Focus();
    }

    private async void OnBanListRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is BanListView view)
        {
            var result = await ViewModel.ParticipantActions.RefreshBanListAsync(view).ConfigureAwait(true);
            ViewModel.StatusText = result.Message;
            if (result.View is not null)
            {
                ViewModel.SelectView(result.View);
            }
        }
    }

    private async void OnBanListAddClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is not BanListView view)
        {
            return;
        }

        var mask = view.Result.NewMask;
        var result = await ViewModel.ParticipantActions.AddBanAsync(view, mask).ConfigureAwait(true);
        ViewModel.StatusText = result.Message;
        if (result.Succeeded)
        {
            view.Result.NewMask = string.Empty;
        }
    }

    private async void OnBanListRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is not BanListView view || view.Result.SelectedEntry is not { } selected)
        {
            ViewModel.StatusText = "Select an exact ban mask to remove.";
            return;
        }

        if (MessageBox.Show(this, $"Remove ban mask {selected.Mask}?", "Remove ban", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await ViewModel.ParticipantActions.RemoveBanAsync(view, selected.Mask).ConfigureAwait(true);
        ViewModel.StatusText = result.Message;
    }

    private void OnBanListCopyClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveView is BanListView view && view.Result.SelectedEntry is { } selected)
        {
            CopyText(selected.Mask);
            ViewModel.StatusText = "Copied the selected ban mask to the clipboard.";
        }
        else
        {
            ViewModel.StatusText = "Select a ban mask to copy.";
        }
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

        var transcriptItem = FindAncestor<ListBoxItem>(source);
        if (transcriptItem?.DataContext is TranscriptEntry transcriptEntry
            && FindAncestor<ListBox>(transcriptItem)?.DataContext is WorkspaceView transcriptView
            && transcriptView is ChannelView or QueryView
            && ViewModel.Sessions.TryGet(transcriptView.NetworkId, out var transcriptNetwork)
            && transcriptNetwork is not null)
        {
            ViewModel.SelectView(transcriptView);
            var canReply = ViewModel.Actions.CanReplyTo(transcriptNetwork, transcriptView, transcriptEntry, out var replyReason);
            var canShowOriginal = transcriptEntry.ReplyResolution == ReplyResolutionState.ResolvedLocally
                || ViewModel.Sessions.CanRecoverReplyParent(transcriptNetwork, transcriptView);
            var originalReason = canShowOriginal
                ? null
                : "The parent message is not in local history and remote history is unavailable.";
            var actions = new List<ContextMenuAction>
            {
                new("Reply", () =>
                {
                    ViewModel.BeginReply(transcriptEntry);
                    return Task.CompletedTask;
                }, canReply, replyReason),
                new("Show original", () => ViewModel.NavigateReplyParentAsync(transcriptEntry), transcriptEntry.HasReplyRelationship && canShowOriginal, originalReason)
            };
            OpenContextMenu(transcriptItem, actions, "MessageContextMenu");
            e.Handled = true;
            return;
        }

        var treeItem = FindAncestor<TreeViewItem>(source);
        if (treeItem?.DataContext is NetworkWorkspace network)
        {
            treeItem.IsSelected = true;
            ViewModel.SelectView(network.StatusView);
            var actions = ViewModel.Actions.BuildNetworkActions(network)
                .Select(action => new ContextMenuAction(
                    action.Label,
                    () => ExecuteWorkspaceActionAsync(network, action.Action),
                    action.IsEnabled,
                    action.DisabledReason))
                .Append(new ContextMenuAction("Remove", new Func<Task>(() => ViewModel.Sessions.RemoveAsync(network.Id).AsTask())))
                .ToArray();
            OpenContextMenu(treeItem, actions, "NetworkContextMenu");
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
                if (channel.IsJoined)
                {
                    actions.Add(("PART (keep view)", () => ExecuteChannelActionAsync(viewNetwork, channel, WorkspaceActionId.PartChannel)));
                    actions.Add(("PART and close", () => ViewModel.PartAndCloseActiveAsync()));
                    actions.Add(("Close view without PART", () => { ViewModel.CloseActiveView(); return Task.CompletedTask; }));
                }
                else
                {
                    actions.Add(("Reopen view", () => { ViewModel.ReopenConversation(channel); return Task.CompletedTask; }));
                    actions.Add(("Rejoin channel", () => ExecuteChannelActionAsync(viewNetwork, channel, WorkspaceActionId.RejoinChannel)));
                    if (channel.LifecycleState is ConversationLifecycleState.HistoricalOnly or ConversationLifecycleState.Parted)
                    {
                        actions.Add(("Remove historical view", () => { ViewModel.RemoveHistoricalConversation(channel); return Task.CompletedTask; }));
                    }
                }
                actions.Insert(0, ("Channel Properties…", () =>
                {
                    OpenChannelProperties(viewNetwork, channel);
                    return Task.CompletedTask;
                }
                ));
                actions.Add((IsFavorite(viewNetwork, DestinationKind.Channel, channel.Channel) ? "Remove favorite" : "Add to favorites", () => ToggleFavorite(viewNetwork, channel, DestinationKind.Channel, channel.Channel)));
                actions.Add(("Copy channel name", () => CopyText(channel.Channel)));
                if (channel.IsJoined)
                {
                    actions.Add(("Request channel modes", () => ExecuteChannelActionAsync(viewNetwork, channel, WorkspaceActionId.RequestModes)));
                    actions.Add(("Request topic", () => ExecuteChannelActionAsync(viewNetwork, channel, WorkspaceActionId.RequestTopic)));
                    actions.Add(("Edit Topic…", () =>
                    {
                        OpenTopicEditor(viewNetwork, channel);
                        return Task.CompletedTask;
                    }
                    ));
                    actions.Add(("Open Ban List", () => ViewModel.ExecuteInputAsync($"/banlist {channel.Channel}")));
                }
                actions.Add(("Open LIST", () => ViewModel.ExecuteInputAsync("/list")));
                actions.Add(("History / search", () => OpenHistory(channel)));
                actions.Add(("Export history", () => OpenHistory(channel)));
                actions.Add(("Clear conversation display", () => { channel.ClearEntries(); return Task.CompletedTask; }));
            }
            else if (view is QueryView query)
            {
                actions.Add((IsFavorite(viewNetwork, DestinationKind.Query, query.Nickname) ? "Remove favorite" : "Add to favorites", () => ToggleFavorite(viewNetwork, query, DestinationKind.Query, query.Nickname)));
                actions.Add(("Copy nickname", () => CopyText(query.Nickname)));
                actions.Add(("History / search", () => OpenHistory(query)));
                actions.Add(("Export history", () => OpenHistory(query)));
                actions.Add(("Clear conversation display", () => { query.ClearEntries(); return Task.CompletedTask; }));
                actions.Add(("Close query", () => { ViewModel.CloseActiveView(); return Task.CompletedTask; }));
            }

            OpenContextMenu(treeItem, actions, view is ChannelView ? "ChannelContextMenu" : "WorkspaceContextMenu");
            e.Handled = true;
            return;
        }

        var memberItem = FindAncestor<ListBoxItem>(source);
        if (memberItem?.DataContext is ChannelMemberView member)
        {
            var channel = FindAncestor<ListBox>(memberItem)?.DataContext as ChannelView;
            if (channel is not null)
            {
                if (!ViewModel.Sessions.TryGet(channel.NetworkId, out var memberNetwork) || memberNetwork is null)
                {
                    return;
                }

                ViewModel.SelectView(channel);
                var context = ViewModel.CreateParticipantContext(memberNetwork, channel, member);
                OpenParticipantContextMenu(memberItem, context);
                e.Handled = true;
            }
        }
    }

    private void OnMemberListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list
            || list.DataContext is not ChannelView channel
            || list.SelectedItem is not ChannelMemberView member
            || !ViewModel.Sessions.TryGet(channel.NetworkId, out var network)
            || network is null)
        {
            return;
        }

        ViewModel.OpenParticipantQuery(ViewModel.CreateParticipantContext(network, channel, member));
        e.Handled = true;
    }

    private void OpenParticipantContextMenu(FrameworkElement target, ParticipantActionContext context)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        AutomationProperties.SetAutomationId(menu, "ParticipantContextMenu");
        AutomationProperties.SetName(menu, $"Actions for {context.TargetNickname}");
        var groups = ParticipantActionCatalog.Build(context, ViewModel.ParticipantActions.IsIgnored(context));
        var firstGroup = true;
        foreach (var group in groups)
        {
            if (!firstGroup)
            {
                menu.Items.Add(new Separator());
            }

            firstGroup = false;
            var submenu = new MenuItem { Header = group.Header };
            foreach (var action in group.Items)
            {
                submenu.Items.Add(CreateParticipantMenuItem(context, action));
            }

            menu.Items.Add(submenu);
        }

        menu.IsOpen = true;
    }

    private MenuItem CreateParticipantMenuItem(ParticipantActionContext context, ParticipantMenuItem action)
    {
        var item = new MenuItem
        {
            Header = action.Header,
            IsEnabled = action.IsEnabled,
            ToolTip = action.DisabledReason,
            Tag = new ParticipantMenuInvocation(context, action)
        };
        AutomationProperties.SetAutomationId(item, $"MemberAction.{action.Action}.{action.ModeLetter?.ToString() ?? "default"}");
        AutomationProperties.SetName(item, action.AccessibleText);
        if (action.Children is { Count: > 0 })
        {
            foreach (var child in action.Children)
            {
                item.Items.Add(CreateParticipantMenuItem(context, child));
            }
        }
        else
        {
            item.Click += OnParticipantMenuClick;
        }

        return item;
    }

    private async void OnParticipantMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ParticipantMenuInvocation invocation })
        {
            CommandDispatchResult result;
            try
            {
                result = await ExecuteParticipantActionAsync(invocation.Context, invocation.Action);
            }
            catch (ArgumentException exception)
            {
                result = CommandDispatchResult.Failure(exception.Message, invocation.Context.Channel);
            }
            catch (InvalidOperationException exception)
            {
                result = CommandDispatchResult.Failure(exception.Message, invocation.Context.Channel);
            }

            ViewModel.StatusText = result.Message;
            if (result.View is not null)
            {
                ViewModel.SelectView(result.View);
            }
        }
    }

    private async Task<CommandDispatchResult> ExecuteParticipantActionAsync(ParticipantActionContext context, ParticipantMenuItem action)
    {
        switch (action.Action)
        {
            case ParticipantActionKind.OpenQuery:
                ViewModel.OpenParticipantQuery(context);
                return CommandDispatchResult.Success($"Query opened for {context.TargetNickname}.", ViewModel.ActiveView);
            case ParticipantActionKind.Mention:
                ViewModel.InsertParticipantMention(context);
                InputBox.Focus();
                return CommandDispatchResult.Success($"Inserted {context.TargetNickname} into the composer.", context.Channel);
            case ParticipantActionKind.Whois:
                return await ViewModel.ParticipantActions.SendWhoisAsync(context);
            case ParticipantActionKind.Notice:
                {
                    var text = ParticipantInputWindow.Show(this, "Send NOTICE", $"Message to {context.TargetNickname}:");
                    return text is null
                        ? CommandDispatchResult.Failure("NOTICE canceled.", context.Channel)
                        : await ViewModel.ParticipantActions.SendNoticeAsync(context, text);
                }
            case ParticipantActionKind.CtcpPing:
                return await ViewModel.ParticipantActions.SendCtcpAsync(context, "PING");
            case ParticipantActionKind.CtcpVersion:
                return await ViewModel.ParticipantActions.SendCtcpAsync(context, "VERSION");
            case ParticipantActionKind.CtcpTime:
                return await ViewModel.ParticipantActions.SendCtcpAsync(context, "TIME");
            case ParticipantActionKind.CopyNickname:
                return CopyTextResult(context.TargetNickname, context.Channel);
            case ParticipantActionKind.CopyHostmask:
                return CopyTextResult(context.Member.Hostmask, context.Channel, "No hostmask is known.");
            case ParticipantActionKind.CopyAccount:
                return CopyTextResult(context.Member.Account, context.Channel, "No account is known.");
            case ParticipantActionKind.CopyIdentity:
                return CopyTextResult(
                    string.Join(
                        Environment.NewLine,
                        new[]
                        {
                            $"Nickname: {context.TargetNickname}",
                            context.Member.Hostmask is null ? null : $"Hostmask: {context.Member.Hostmask}",
                            string.IsNullOrWhiteSpace(context.Member.Account) ? null : $"Account: {context.Member.Account}"
                        }.Where(line => line is not null)!),
                    context.Channel);
            case ParticipantActionKind.GivePrivilege:
            case ParticipantActionKind.RemovePrivilege:
                return action.ModeLetter is char mode
                    ? await ViewModel.ParticipantActions.SetPrivilegeAsync(context, mode, action.Action == ParticipantActionKind.GivePrivilege)
                    : CommandDispatchResult.Failure("The advertised privilege mode is unavailable.", context.Channel);
            case ParticipantActionKind.Kick:
                {
                    var reason = ParticipantInputWindow.Show(this, "Kick participant", $"Optional reason for kicking {context.TargetNickname}:");
                    return reason is null
                        ? CommandDispatchResult.Failure("Kick canceled.", context.Channel)
                        : await ViewModel.ParticipantActions.KickAsync(context, reason);
                }
            case ParticipantActionKind.Ban:
                {
                    var mask = ParticipantInputWindow.Show(this, "Ban participant", $"Ban mask for {context.TargetNickname} (edit before sending):", ParticipantActionService.GetConservativeBanMask(context.Member), selectAll: context.Member.Hostmask is not null);
                    return mask is null
                        ? CommandDispatchResult.Failure("Ban canceled.", context.Channel)
                        : await ViewModel.ParticipantActions.BanAsync(context, mask);
                }
            case ParticipantActionKind.KickAndBan:
                {
                    var mask = ParticipantInputWindow.Show(this, "Ban and kick participant", $"Ban mask for {context.TargetNickname} (edit before sending):", ParticipantActionService.GetConservativeBanMask(context.Member), selectAll: context.Member.Hostmask is not null);
                    if (mask is null)
                    {
                        return CommandDispatchResult.Failure("Ban and kick canceled.", context.Channel);
                    }

                    var reason = ParticipantInputWindow.Show(this, "Ban and kick participant", $"Optional reason for kicking {context.TargetNickname}:");
                    return reason is null
                        ? CommandDispatchResult.Failure("Ban and kick canceled.", context.Channel)
                        : await ViewModel.ParticipantActions.KickAndBanAsync(context, mask, reason);
                }
            case ParticipantActionKind.Unban:
                {
                    var mask = ParticipantInputWindow.Show(this, "Unban participant", $"Ban mask to remove for {context.TargetNickname}:", ParticipantActionService.GetConservativeBanMask(context.Member), selectAll: context.Member.Hostmask is not null);
                    return mask is null
                        ? CommandDispatchResult.Failure("Unban canceled.", context.Channel)
                        : await ViewModel.ParticipantActions.UnbanAsync(context, mask);
                }
            case ParticipantActionKind.Ignore:
                return await ViewModel.ParticipantActions.SetIgnoredAsync(context, true);
            case ParticipantActionKind.Unignore:
                return await ViewModel.ParticipantActions.SetIgnoredAsync(context, false);
            case ParticipantActionKind.Invite:
                return action.TargetChannel is null
                    ? CommandDispatchResult.Failure("No joined target channel was selected.", context.Channel)
                    : await ViewModel.ParticipantActions.InviteAsync(context, action.TargetChannel);
            default:
                return CommandDispatchResult.Failure("The participant action is not supported.", context.Channel);
        }
    }

    private static CommandDispatchResult CopyTextResult(string? text, WorkspaceView view, string missing = "Nothing is available to copy.")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CommandDispatchResult.Failure(missing, view);
        }

        System.Windows.Clipboard.SetText(text);
        return CommandDispatchResult.Success("Copied to the clipboard.", view);
    }

    private void OpenChannelProperties(NetworkWorkspace network, ChannelView channel)
    {
        var dialog = new ChannelPropertiesWindow(new ChannelPropertiesViewModel(ViewModel.Sessions, network, channel)) { Owner = this };
        dialog.ShowDialog();
    }

    private void OpenTopicEditor(NetworkWorkspace network, ChannelView channel)
    {
        var dialog = new TopicEditorWindow(new ChannelPropertiesViewModel(ViewModel.Sessions, network, channel)) { Owner = this };
        dialog.ShowDialog();
    }

    private void OpenContextMenu(FrameworkElement target, IEnumerable<(string Header, Func<Task> Action)> actions, string automationId = "WorkspaceContextMenu")
    {
        OpenContextMenu(target, actions.Select(action => new ContextMenuAction(action.Header, action.Action)), automationId);
    }

    private void OpenContextMenu(FrameworkElement target, IEnumerable<ContextMenuAction> actions, string automationId = "WorkspaceContextMenu")
    {
        var menu = new ContextMenu { PlacementTarget = target };
        AutomationProperties.SetAutomationId(menu, automationId);
        AutomationProperties.SetName(menu, automationId == "ChannelContextMenu" ? "Channel actions" : "Workspace actions");
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Header, Tag = action.Action, IsEnabled = action.IsEnabled, ToolTip = action.DisabledReason };
            AutomationProperties.SetAutomationId(item, $"ContextAction.{action.Header.Replace("…", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)}");
            AutomationProperties.SetName(item, action.IsEnabled || string.IsNullOrWhiteSpace(action.DisabledReason)
                ? action.Header
                : $"{action.Header} ({action.DisabledReason})");
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

    private async Task ExecuteWorkspaceActionAsync(NetworkWorkspace network, WorkspaceActionId action)
    {
        var result = await ViewModel.Actions.ExecuteNetworkAsync(network, action).ConfigureAwait(true);
        ViewModel.StatusText = result.Message;
        if (result.View is not null)
        {
            ViewModel.SelectView(result.View);
        }
    }

    private async Task ExecuteChannelActionAsync(NetworkWorkspace network, ChannelView channel, WorkspaceActionId action)
    {
        var result = await ViewModel.Actions.ExecuteChannelAsync(network, channel, action).ConfigureAwait(true);
        ViewModel.StatusText = result.Message;
        if (result.View is not null)
        {
            ViewModel.SelectView(result.View);
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

    private static T? FindDescendant<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in EnumerateDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "nexIRC 5\nA reconnect-aware, multi-network IRC client shell.", "About nexIRC", MessageBoxButton.OK, MessageBoxImage.Information);

    private sealed record ParticipantMenuInvocation(ParticipantActionContext Context, ParticipantMenuItem Action);

    private sealed record ContextMenuAction(
        string Header,
        Func<Task> Action,
        bool IsEnabled = true,
        string? DisabledReason = null);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _closing = true;
        ViewModel.CaptureViewState(ActualWidth, ActualHeight, Left, Top, WindowState == WindowState.Maximized, NavigationColumn.ActualWidth);
        try
        {
            await ViewModel.ShutdownAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WPF shutdown completed with a recoverable error: {exception.Message}");
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
