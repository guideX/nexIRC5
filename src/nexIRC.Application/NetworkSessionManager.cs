using System.Collections.ObjectModel;
using nexIRC.Core.Session;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Application;

public sealed class NetworkSessionManager : IAsyncDisposable
{
    private readonly nexIRC.Core.Networking.IIrcTransportFactory _transportFactory;
    private readonly IWorkspaceDispatcher _dispatcher;
    private readonly object _entriesGate = new();
    private readonly Dictionary<Guid, SessionEntry> _entries = [];
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pendingDispatches = [];
    private readonly object _operationsGate = new();
    private readonly Dictionary<Guid, NetworkOperationState> _operations = [];
    private readonly ConversationLoggingService? _logging;
    private readonly IConversationLogStore? _logStore;
    private readonly NotificationCoalescer _notificationCoalescer = new();
    private readonly ConversationNavigationHistory _navigationHistory = new();
    private readonly bool _ownsNotifications;
    private readonly bool _ownsLogStore;
    private long _operationSequence;
    private bool _disposed;

    private const int MaximumOutstandingOperations = 64;

    public NetworkSessionManager(
        nexIRC.Core.Networking.IIrcTransportFactory transportFactory,
        IWorkspaceDispatcher? dispatcher = null,
        IIrcNotificationService? notifications = null,
        HighlightActivityPolicy? highlightPolicy = null,
        ConfigurationService? configuration = null,
        IConversationLogStore? logStore = null,
        ConversationLoggingService? logging = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _dispatcher = dispatcher ?? new ImmediateWorkspaceDispatcher();
        Notifications = notifications ?? new NotificationSubscriptionService();
        _ownsNotifications = notifications is null;
        HighlightPolicy = highlightPolicy ?? new HighlightActivityPolicy();
        Configuration = configuration;
        _logStore = logStore;
        _logging = logging ?? (logStore is not null && configuration is not null
            ? new ConversationLoggingService(logStore, () => configuration.Preferences)
            : null);
        _ownsLogStore = logStore is not null;
        if (configuration is not null)
        {
            ApplyPreferences(configuration.Preferences);
        }
    }

    public ObservableCollection<NetworkWorkspace> Networks { get; } = [];

    public NetworkWorkspace? ActiveNetwork { get; private set; }

    public WorkspaceView? ActiveView { get; private set; }

    public IIrcNotificationService Notifications { get; }

    public HighlightActivityPolicy HighlightPolicy { get; }

    public ConfigurationService? Configuration { get; }

    public IConversationLogStore? LogStore => _logStore;

    public ConversationLoggingService? Logging => _logging;

    public bool IsIgnored(NetworkWorkspace workspace, IrcPrefix? prefix, string? account = null)
    {
        if (Configuration is null || prefix?.Name is null)
        {
            return false;
        }

        var identity = new IgnoreIdentity(
            prefix.Name,
            prefix.User,
            prefix.Host,
            account,
            workspace.ProfileId,
            workspace.NetworkName ?? workspace.Snapshot.Features.NetworkName ?? workspace.DisplayName);
        return Configuration.IsIgnored(identity, workspace.Snapshot.Features.CaseMapping);
    }

    public void ApplyPreferences(ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        HighlightPolicy.HighlightNickname = preferences.HighlightNickname;
        HighlightPolicy.HighlightCustomWords = preferences.HighlightCustomWords;
        HighlightPolicy.CustomWords = preferences.CustomHighlightWords;
        Configuration?.SetPreferences(preferences);
    }

    public event EventHandler<WorkspaceActivityEventArgs>? ActivityRaised;

    public event EventHandler? NavigationChanged;

    public IReadOnlyList<ConversationIdentity> NavigationHistory => _navigationHistory.Entries;

    public IReadOnlyList<ConversationNavigationItem> GetConversationNavigator(ConversationOrderingMode ordering = ConversationOrderingMode.Workspace)
    {
        var items = Networks
            .SelectMany(network => network.Views.Select(view => CreateNavigationItem(network, view)))
            .ToArray();
        return ordering == ConversationOrderingMode.RecentActivity
            ? items.OrderByDescending(item => item.LastActivity).ThenBy(item => item.NetworkDisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : items;
    }

    public NetworkWorkspace Add(NetworkConnectionOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var session = new ServerSession(options.ToSessionOptions(), _transportFactory);
        var workspace = new NetworkWorkspace(Guid.NewGuid(), options, session);
        var entry = new SessionEntry(workspace, options, session);
        lock (_entriesGate)
        {
            _entries.Add(workspace.Id, entry);
        }
        Attach(entry);
        Networks.Add(workspace);
        if (ActiveView is null)
        {
            ActivateView(workspace.StatusView.Id);
        }

        return workspace;
    }

    public bool TryGet(Guid networkId, out NetworkWorkspace? workspace)
    {
        lock (_entriesGate)
        {
            var found = _entries.TryGetValue(networkId, out var entry);
            workspace = entry?.Workspace;
            return found;
        }
    }

    public bool TryGetView(Guid viewId, out NetworkWorkspace? workspace, out WorkspaceView? view)
    {
        foreach (var item in Networks)
        {
            var candidate = item.Views.FirstOrDefault(viewItem => viewItem.Id == viewId);
            if (candidate is not null)
            {
                workspace = item;
                view = candidate;
                return true;
            }
        }

        workspace = null;
        view = null;
        return false;
    }

    public ChannelView EnsureChannel(Guid networkId, string channel)
    {
        var workspace = GetWorkspace(networkId);
        return workspace.EnsureChannel(channel);
    }

    public QueryView EnsureQuery(Guid networkId, string nickname)
    {
        var workspace = GetWorkspace(networkId);
        return workspace.EnsureQuery(nickname);
    }

    public WorkspaceView OpenHistoricalConversation(Guid networkId, DestinationKind kind, string name)
    {
        var workspace = GetWorkspace(networkId);
        var existing = kind == DestinationKind.Channel
            ? workspace.Channels.Any(channel => IrcCaseMappingComparer.Equals(channel.Channel, name, workspace.Snapshot.Features.CaseMapping))
            : workspace.Queries.Any(query => IrcCaseMappingComparer.Equals(query.Nickname, name, workspace.Snapshot.Features.CaseMapping));
        WorkspaceView view = kind == DestinationKind.Channel
            ? workspace.EnsureChannel(name)
            : workspace.EnsureQuery(name);
        if (!existing && view is ChannelView channel && !channel.IsJoined)
        {
            view.SetLifecycleState(ConversationLifecycleState.HistoricalOnly);
        }
        else if (!existing && view is QueryView)
        {
            view.SetLifecycleState(ConversationLifecycleState.HistoricalOnly);
        }

        if (!view.IsViewOpen)
        {
            workspace.ReopenView(view);
        }

        ActivateView(view.Id);
        return view;
    }

    public WhoisView BeginWhois(Guid networkId, string nickname)
    {
        var workspace = GetWorkspace(networkId);
        var view = workspace.EnsureWhois(nickname, beginRequest: true);
        ActivateView(view.Id);
        return view;
    }

    public ChannelListView BeginChannelList(Guid networkId)
    {
        var workspace = GetWorkspace(networkId);
        var view = workspace.EnsureChannelList(beginRequest: true);
        ActivateView(view.Id);
        return view;
    }

    public async ValueTask<IrcQueryRequestResult> RequestWhoisAsync(
        Guid networkId,
        string nickname,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var workspace = GetWorkspace(networkId);
        var supportsLabels = workspace.Snapshot.Capabilities.IsEnabled("labeled-response");
        ActiveOperation? operationToStart = null;
        IrcQueryRequestResult result;
        lock (_operationsGate)
        {
            var state = GetOperationStateUnsafe(networkId);
            if (!supportsLabels)
            {
                if (state.UnlabeledWhois is { } active && IrcIdentity.Equals(active.Operation.Target, nickname, workspace.Snapshot.Features.CaseMapping))
                {
                    result = new IrcQueryRequestResult(active.View, active.Operation with { Correlation = IrcQueryCorrelationMode.CoalescedUnlabeled }, true);
                    goto Completed;
                }

                var queued = state.QueuedWhois.FirstOrDefault(item => IrcIdentity.Equals(item.Operation.Target, nickname, workspace.Snapshot.Features.CaseMapping));
                if (queued is not null)
                {
                    result = new IrcQueryRequestResult(queued.View, queued.Operation with { Correlation = IrcQueryCorrelationMode.CoalescedUnlabeled }, true);
                    goto Completed;
                }

                var outstandingUnlabeled = (state.UnlabeledWhois is null ? 0 : 1) + state.QueuedWhois.Count;
                if (outstandingUnlabeled >= MaximumOutstandingOperations)
                {
                    throw new InvalidOperationException("Too many WHOIS operations are already outstanding on this network.");
                }
            }
            else if (state.LabeledWhois.Count >= MaximumOutstandingOperations)
            {
                throw new InvalidOperationException("Too many WHOIS operations are already outstanding on this network.");
            }

            var forceNewView = supportsLabels && state.LabeledWhois.Values.Any(item => IrcIdentity.Equals(item.Operation.Target, nickname, workspace.Snapshot.Features.CaseMapping));
            var view = workspace.EnsureWhois(nickname, beginRequest: true, forceNew: forceNewView);
            var label = supportsLabels ? NextOperationLabelUnsafe(state.LabeledWhois.Keys) : null;
            var operation = new ActiveOperation
            {
                Operation = new IrcQueryOperation(
                    Guid.NewGuid(),
                    networkId,
                    "WHOIS",
                    nickname,
                    label,
                    supportsLabels ? IrcQueryCorrelationMode.LabeledResponse : IrcQueryCorrelationMode.SerializedUnlabeled,
                    DateTimeOffset.UtcNow),
                View = view
            };
            if (supportsLabels)
            {
                state.LabeledWhois.Add(label!, operation);
                operationToStart = operation;
            }
            else if (state.UnlabeledWhois is null)
            {
                state.UnlabeledWhois = operation;
                operationToStart = operation;
            }
            else
            {
                state.QueuedWhois.Enqueue(operation);
            }

            result = new IrcQueryRequestResult(view, operation.Operation, false);
        Completed:
            ;
        }

        if (operationToStart is not null)
        {
            ScheduleOperationExpiry(networkId, operationToStart);
            await SendWhoisOperationAsync(workspace, operationToStart, cancellationToken).ConfigureAwait(false);
        }

        ActivateView(result.View.Id);
        return result;
    }

    public async ValueTask<IrcQueryRequestResult> RequestChannelListAsync(
        Guid networkId,
        IReadOnlyList<string>? filters = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = GetWorkspace(networkId);
        var supportsLabels = workspace.Snapshot.Capabilities.IsEnabled("labeled-response");
        ActiveOperation? previous = null;
        ActiveOperation operation;
        lock (_operationsGate)
        {
            var state = GetOperationStateUnsafe(networkId);
            if (!supportsLabels && state.ActiveList is { } active)
            {
                var activeResult = new IrcQueryRequestResult(active.View, active.Operation with { Correlation = IrcQueryCorrelationMode.CoalescedUnlabeled }, true);
                ActivateView(active.View.Id);
                return activeResult;
            }

            if (supportsLabels && state.ActiveList is { } old)
            {
                previous = old;
            }

            var view = workspace.EnsureChannelList(beginRequest: true);
            var label = supportsLabels ? NextOperationLabelUnsafe(state.LabeledWhois.Keys.Append(state.ActiveList?.Operation.RequestLabel ?? string.Empty)) : null;
            operation = new ActiveOperation
            {
                Operation = new IrcQueryOperation(
                    Guid.NewGuid(),
                    networkId,
                    "LIST",
                    "LIST",
                    label,
                    supportsLabels ? IrcQueryCorrelationMode.LabeledResponse : IrcQueryCorrelationMode.SerializedUnlabeled,
                    DateTimeOffset.UtcNow),
                View = view
            };
            state.ActiveList = operation;
            if (previous is not null && previous.Operation.RequestLabel is { } oldLabel)
            {
                // A replacement has its own result lifetime; late responses
                // carrying the old label are ignored by RouteListEvent.
                state.LabeledWhois.Remove(oldLabel);
            }
        }

        if (previous is not null)
        {
            RetireOperation(previous);
        }
        ScheduleOperationExpiry(networkId, operation);
        var commandParts = filters ?? Array.Empty<string>();
        try
        {
            if (operation.Operation.RequestLabel is { } requestLabel)
            {
                await workspace.Session.SendTaggedCommandAsync(
                    new Dictionary<string, string?> { ["label"] = requestLabel },
                    "LIST",
                    commandParts,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await workspace.Session.SendCommandAsync("LIST", commandParts, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            CompleteListOperation(networkId, operation);
            throw;
        }

        ActivateView(operation.View.Id);
        return new IrcQueryRequestResult(operation.View, operation.Operation, false);
    }

    public void ActivateView(Guid viewId)
    {
        ActivateViewCore(viewId, recordNavigation: true);
    }

    public bool NavigateBack()
    {
        if (!_navigationHistory.TryGoBack(IsOpenIdentityAvailable, out var identity) || identity is null)
        {
            return false;
        }

        return TryActivateIdentity(identity);
    }

    public bool NavigateForward()
    {
        if (!_navigationHistory.TryGoForward(IsOpenIdentityAvailable, out var identity) || identity is null)
        {
            return false;
        }

        return TryActivateIdentity(identity);
    }

    public bool NavigateNextConversation() => ActivateRelativeConversation(1);

    public bool NavigatePreviousConversation() => ActivateRelativeConversation(-1);

    public bool NavigateNextUnread() => ActivateMatchingView(view => view.Activity != WorkspaceActivity.None, 1);

    public bool NavigatePreviousUnread() => ActivateMatchingView(view => view.Activity != WorkspaceActivity.None, -1);

    public bool NavigateNextHighlight() => ActivateMatchingView(view => view.Activity == WorkspaceActivity.Important, 1);

    public bool NavigatePreviousHighlight() => ActivateMatchingView(view => view.Activity == WorkspaceActivity.Important, -1);

    private void ActivateViewCore(Guid viewId, bool recordNavigation)
    {
        if (!TryGetView(viewId, out var workspace, out var view) || workspace is null || view is null)
        {
            return;
        }

        if (recordNavigation && ActiveView is not null && !ReferenceEquals(ActiveView, view))
        {
            _navigationHistory.Record(ConversationIdentity.From(ActiveView));
        }

        workspace.Activate(view);
        ActiveNetwork = workspace;
        ActiveView = view;
        _navigationHistory.Record(ConversationIdentity.From(view));
        NotifyNavigationChanged();
    }

    public bool ReopenView(Guid viewId)
    {
        foreach (var workspace in Networks)
        {
            var view = workspace.Channels.Cast<WorkspaceView>()
                .Concat(workspace.Queries)
                .Concat(workspace.WhoisViews)
                .Concat(workspace.ChannelListViews)
                .FirstOrDefault(item => item.Id == viewId);
            if (view is null)
            {
                continue;
            }

            workspace.ReopenView(view);
            ActivateView(view.Id);
            NotifyNavigationChanged();
            return true;
        }

        return false;
    }

    public bool CloseView(Guid viewId)
    {
        if (!TryGetView(viewId, out var workspace, out var view) || workspace is null || view is null || view.Kind == WorkspaceViewKind.ServerStatus)
        {
            return false;
        }

        if (view is ChannelView or QueryView)
        {
            // Closing a view only changes presentation. The logical
            // conversation remains addressable for reopen/history routing.
            RecordRecent(workspace, view is ChannelView ? DestinationKind.Channel : DestinationKind.Query, ConversationIdentity.From(view).Name);
            workspace.Close(view);
        }
        else if (view is WhoisView whois)
        {
            workspace.WhoisViews.Remove(whois);
            workspace.Close(whois);
            ClearOperations(workspace.Id);
        }
        else if (view is ChannelListView list)
        {
            workspace.ChannelListViews.Remove(list);
            workspace.Close(list);
            ClearOperations(workspace.Id);
        }

        if (ReferenceEquals(ActiveView, view))
        {
            ActivateView(workspace.StatusView.Id);
        }

        NotifyNavigationChanged();

        return true;
    }

    public async ValueTask<bool> PartAndCloseAsync(Guid viewId, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetView(viewId, out var workspace, out var view) || workspace is null || view is not ChannelView channel)
        {
            return false;
        }

        await workspace.Session.PartChannelAsync(channel.Channel, reason, cancellationToken).ConfigureAwait(false);
        channel.SetLifecycleState(ConversationLifecycleState.Parted);
        return CloseView(viewId);
    }

    public bool RemoveHistoricalConversation(Guid viewId)
    {
        if (!TryGetView(viewId, out var workspace, out var view) || workspace is null || view is null
            || view is not (ChannelView or QueryView)
            || view.LifecycleState is ConversationLifecycleState.Joined or ConversationLifecycleState.Active)
        {
            return false;
        }

        if (Configuration is not null)
        {
            Configuration.RemoveRecent(new RecentDestination
            {
                ScopeId = workspace.ProfileId ?? workspace.Id,
                Kind = view is ChannelView ? DestinationKind.Channel : DestinationKind.Query,
                Name = ConversationIdentity.From(view).Name
            });
            SaveConfigurationInBackground();
        }

        var wasActive = ReferenceEquals(ActiveView, view);
        workspace.RemoveConversation(view);
        if (wasActive)
        {
            ActiveView = null;
        }

        _navigationHistory.Remove(ConversationIdentity.From(view));
        if (wasActive)
        {
            ActivateView(workspace.StatusView.Id);
        }

        NotifyNavigationChanged();
        return true;
    }

    public async ValueTask RejoinChannelAsync(Guid networkId, string channel, CancellationToken cancellationToken = default)
    {
        var workspace = GetWorkspace(networkId);
        var view = workspace.EnsureChannel(channel);
        if (!view.IsViewOpen)
        {
            workspace.ReopenView(view);
        }

        ActivateView(view.Id);
        RecordRecent(workspace, DestinationKind.Channel, channel);
        await workspace.Session.RejoinChannelAsync(view.Channel, cancellationToken).ConfigureAwait(false);
        NotifyNavigationChanged();
    }

    public IReadOnlyList<FavoriteDestination> Favorites(Guid networkId, DestinationKind? kind = null)
    {
        var workspace = GetWorkspace(networkId);
        if (Configuration is null)
        {
            return Array.Empty<FavoriteDestination>();
        }

        var scope = workspace.ProfileId ?? workspace.Id;
        var groupOrder = Configuration.FavoriteGroups.ToDictionary(group => group.Id, group => group.SortOrder);
        return Configuration.Favorites
            .Where(item => item.ScopeId == scope && (kind is null || item.Kind == kind))
            .OrderBy(item => groupOrder.TryGetValue(item.GroupId, out var order) ? order : int.MaxValue)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToArray();
    }

    public IReadOnlyList<FavoriteGroup> FavoriteGroups() => Configuration?.FavoriteGroups ?? Array.Empty<FavoriteGroup>();

    public IReadOnlyList<RecentDestination> RecentDestinations(Guid networkId, DestinationKind? kind = null)
    {
        var workspace = GetWorkspace(networkId);
        if (Configuration is null)
        {
            return Array.Empty<RecentDestination>();
        }

        var scope = workspace.ProfileId ?? workspace.Id;
        return Configuration.RecentDestinations.Where(item => item.ScopeId == scope && (kind is null || item.Kind == kind)).OrderByDescending(item => item.LastOpened).ToArray();
    }

    public bool AddFavorite(Guid networkId, DestinationKind kind, string name, string? label = null)
        => AddFavorite(networkId, kind, name, label, NavigationDefaults.DefaultFavoriteGroupId);

    public bool AddFavorite(Guid networkId, DestinationKind kind, string name, string? label, Guid groupId)
    {
        var workspace = GetWorkspace(networkId);
        if (Configuration is null)
        {
            return false;
        }

        var added = Configuration.AddOrUpdateFavorite(new FavoriteDestination
        {
            ScopeId = workspace.ProfileId ?? workspace.Id,
            Kind = kind,
            Name = name,
            Label = label,
            GroupId = groupId
        });
        if (added)
        {
            SaveConfigurationInBackground();
        }

        return added;
    }

    public bool MoveFavorite(Guid favoriteId, Guid groupId)
    {
        if (Configuration is null)
        {
            return false;
        }

        var moved = Configuration.MoveFavorite(favoriteId, groupId);
        if (moved) SaveConfigurationInBackground();
        return moved;
    }

    public bool ReorderFavorite(Guid favoriteId, int delta)
    {
        if (Configuration is null)
        {
            return false;
        }

        var moved = Configuration.ReorderFavorite(favoriteId, delta);
        if (moved) SaveConfigurationInBackground();
        return moved;
    }

    public bool RemoveFavorite(Guid favoriteId)
    {
        if (Configuration is null)
        {
            return false;
        }

        var removed = Configuration.RemoveFavorite(favoriteId);
        if (removed) SaveConfigurationInBackground();
        return removed;
    }

    public void ClearRecent(Guid networkId, DestinationKind? kind = null)
    {
        if (Configuration is null || !TryGet(networkId, out var workspace) || workspace is null)
        {
            return;
        }

        Configuration.ClearRecent(workspace.ProfileId ?? workspace.Id, kind);
        SaveConfigurationInBackground();
    }

    public bool RemoveRecent(Guid networkId, RecentDestination destination)
    {
        if (Configuration is null || !TryGet(networkId, out var workspace) || workspace is null)
        {
            return false;
        }

        var scoped = destination with { ScopeId = workspace.ProfileId ?? workspace.Id };
        var removed = Configuration.RemoveRecent(scoped);
        if (removed) SaveConfigurationInBackground();
        return removed;
    }

    public void RecordRecent(NetworkWorkspace workspace, DestinationKind kind, string name)
    {
        if (Configuration is null)
        {
            return;
        }

        Configuration.RecordRecent(new RecentDestination
        {
            ScopeId = workspace.ProfileId ?? workspace.Id,
            Kind = kind,
            Name = name,
            LastOpened = DateTimeOffset.UtcNow
        });
        SaveConfigurationInBackground();
    }

    public bool ActivateNotification(IrcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (TryGetView(notification.ViewId, out var workspace, out var view) && workspace is not null && view is not null)
        {
            ActivateView(view.Id);
            return true;
        }

        if (notification.Activation is { } target
            && ResolveActivationWorkspace(target) is { } targetWorkspace)
        {
            WorkspaceView targetView = target.ViewKind switch
            {
                WorkspaceViewKind.Channel when !string.IsNullOrWhiteSpace(target.ConversationName) => targetWorkspace.EnsureChannel(target.ConversationName),
                WorkspaceViewKind.Query when !string.IsNullOrWhiteSpace(target.ConversationName) => targetWorkspace.EnsureQuery(target.ConversationName),
                _ => targetWorkspace.StatusView
            };
            ActivateView(targetView.Id);
            return true;
        }

        return false;
    }

    public async ValueTask ConnectAsync(Guid networkId, CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(networkId);
        if (!entry.Started && (entry.NeedsReplacement || entry.RunTask?.IsCompleted == true))
        {
            ReplaceSession(entry);
        }

        if (!entry.Started)
        {
            entry.Started = true;
            entry.RunTask = entry.Session.RunAsync(cancellationToken);
            _ = ObserveCompletionAsync(entry, entry.RunTask);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(Guid networkId)
    {
        var entry = GetEntry(networkId);
        await entry.Session.DisconnectAsync().ConfigureAwait(false);
        await entry.Session.DisposeAsync().ConfigureAwait(false);
        entry.Started = false;
        entry.NeedsReplacement = true;
    }

    public async ValueTask ReconnectAsync(Guid networkId, CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(networkId);
        var currentSnapshot = entry.Session.Snapshot;
        var desiredChannels = currentSnapshot.DesiredChannels.ToHashSet(IrcCaseMappingComparer.For(currentSnapshot.Features.CaseMapping));
        await StopEntryAsync(entry, "nexIRC reconnect").ConfigureAwait(false);
        entry.Options = entry.Options with { DesiredChannels = desiredChannels };
        entry.NeedsReplacement = true;
        await ConnectAsync(networkId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(Guid networkId)
    {
        var entry = GetEntry(networkId);
        lock (_entriesGate)
        {
            _entries.Remove(networkId);
        }
        Networks.Remove(entry.Workspace);
        if (ReferenceEquals(ActiveNetwork, entry.Workspace))
        {
            ActiveNetwork = Networks.FirstOrDefault();
            ActiveView = ActiveNetwork?.StatusView;
            if (ActiveNetwork is not null)
            {
                ActivateView(ActiveNetwork.StatusView.Id);
            }
        }

        _navigationHistory.RemoveNetwork(networkId);
        NotifyNavigationChanged();

        await StopEntryAsync(entry, "nexIRC network removed").ConfigureAwait(false);
        DisposeCredentialProviders(entry.Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SessionEntry[] entries;
        lock (_entriesGate)
        {
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }
        Networks.Clear();
        foreach (var entry in entries)
        {
            await StopEntryAsync(entry, "nexIRC shutting down").ConfigureAwait(false);
            DisposeCredentialProviders(entry.Options);
        }

        Task[] pending;
        lock (_pendingGate)
        {
            pending = _pendingDispatches.ToArray();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);

        if (_ownsNotifications)
        {
            Notifications.Dispose();
        }

        if (_ownsLogStore && _logStore is not null)
        {
            await _logStore.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void AppendLocal(WorkspaceView view, TranscriptEntry entry)
    {
        view.Append(entry, markActivity: false);
        if (TryGet(view.NetworkId, out var workspace) && workspace is not null)
        {
            _logging?.Record(workspace.Id, workspace.ProfileId, view, entry);
        }

        NotifyNavigationChanged();
    }

    internal WhoisView? RouteWhoisEvent(NetworkWorkspace workspace, IrcWhoisEvent item)
    {
        ActiveOperation? operation = null;
        var mapping = workspace.Snapshot.Features.CaseMapping;
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(workspace.Id, out var state))
            {
                return null;
            }

            if (item.RequestLabel is { } label)
            {
                state.LabeledWhois.TryGetValue(label, out operation);
            }
            else if (state.UnlabeledWhois is { } unlabeled && IrcIdentity.Equals(unlabeled.Operation.Target, item.Nickname, mapping))
            {
                operation = unlabeled;
            }
            else if (state.LabeledWhois.Count == 1)
            {
                var candidate = state.LabeledWhois.Values.Single();
                if (IrcIdentity.Equals(candidate.Operation.Target, item.Nickname, mapping))
                {
                    operation = candidate;
                }
            }
        }

        if (operation?.View is not WhoisView view)
        {
            return null;
        }

        view.Apply(item);
        if (item.Numeric == 318)
        {
            _ = CompleteWhoisOperationAsync(workspace, operation);
        }

        return view;
    }

    internal WhoisView? RouteWhoisAdditionalEvent(NetworkWorkspace workspace, IrcUnknownNumericEvent item)
    {
        ActiveOperation? operation = null;
        var label = item.Message.TagValues.TryGetValue("label", out var requestLabel) ? requestLabel : null;
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(workspace.Id, out var state))
            {
                return null;
            }

            if (label is not null)
            {
                state.LabeledWhois.TryGetValue(label, out operation);
            }
            else if (state.UnlabeledWhois is not null)
            {
                operation = state.UnlabeledWhois;
            }
            else if (state.LabeledWhois.Count == 1)
            {
                operation = state.LabeledWhois.Values.Single();
            }
        }

        if (operation?.View is not WhoisView view)
        {
            return null;
        }

        view.ApplyAdditional(item.Numeric, MessageText(item.Message));
        return view;
    }

    internal WorkspaceView? RouteListEvent(NetworkWorkspace workspace, string? requestLabel, bool completes, bool starts)
    {
        ActiveOperation? operation = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(workspace.Id, out var state))
            {
                if (requestLabel is not null)
                {
                    if (state.ActiveList?.Operation.RequestLabel == requestLabel)
                    {
                        operation = state.ActiveList;
                    }
                }
                else
                {
                    operation = state.ActiveList;
                }
            }
        }

        if (operation is not null)
        {
            if (completes && ReferenceEquals(GetActiveList(workspace.Id), operation))
            {
                if (operation.View is ChannelListView listView)
                {
                    listView.CompleteRequest();
                }

                CompleteListOperation(workspace.Id, operation);
            }

            return operation.View;
        }

        if (requestLabel is not null)
        {
            return null;
        }

        if (starts)
        {
            var view = workspace.EnsureChannelList();
            if (!view.IsLoading)
            {
                view.BeginRequest();
            }

            return view;
        }

        return null;
    }

    private async Task SendWhoisOperationAsync(NetworkWorkspace workspace, ActiveOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            if (operation.Operation.RequestLabel is { } label)
            {
                await workspace.Session.SendTaggedCommandAsync(
                    new Dictionary<string, string?> { ["label"] = label },
                    "WHOIS",
                    [operation.Operation.Target],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await workspace.Session.SendCommandAsync("WHOIS", [operation.Operation.Target], cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            RemoveWhoisOperation(workspace.Id, operation);
            throw;
        }
    }

    private void ScheduleOperationExpiry(Guid networkId, ActiveOperation operation)
    {
        _ = ExpireOperationAsync(networkId, operation);
    }

    private async Task ExpireOperationAsync(Guid networkId, ActiveOperation operation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), operation.Lifetime.Token).ConfigureAwait(false);
            if (operation.Operation.Kind == "WHOIS")
            {
                if (operation.View is WhoisView whois)
                {
                    whois.Fail("Timed out waiting for numeric 318.");
                }

                RemoveWhoisOperation(networkId, operation);
            }
            else
            {
                CompleteListOperation(networkId, operation);
            }
        }
        catch (OperationCanceledException) when (operation.Lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task CompleteWhoisOperationAsync(NetworkWorkspace workspace, ActiveOperation completed)
    {
        ActiveOperation? next = null;
        var wasLabeled = completed.Operation.RequestLabel is not null;
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(workspace.Id, out var state))
            {
                return;
            }

            if (wasLabeled)
            {
                if (completed.Operation.RequestLabel is { } label)
                {
                    if (!state.LabeledWhois.Remove(label))
                    {
                        return;
                    }
                }
            }
            else
            {
                if (state.UnlabeledWhois is null || !ReferenceEquals(state.UnlabeledWhois, completed))
                {
                    return;
                }

                state.UnlabeledWhois = null;
                if (state.QueuedWhois.Count > 0)
                {
                    next = state.QueuedWhois.Dequeue();
                    state.UnlabeledWhois = next;
                }
            }
        }

        RetireOperation(completed);
        if (next is not null)
        {
            ScheduleOperationExpiry(workspace.Id, next);
            await SendWhoisOperationAsync(workspace, next, CancellationToken.None).ConfigureAwait(false);
        }
        RemoveEmptyOperationState(workspace.Id);
    }

    private void RemoveWhoisOperation(Guid networkId, ActiveOperation operation)
    {
        ActiveOperation? next = null;
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(networkId, out var state))
            {
                return;
            }

            if (operation.Operation.RequestLabel is { } label)
            {
                state.LabeledWhois.Remove(label);
            }
            else if (ReferenceEquals(state.UnlabeledWhois, operation))
            {
                state.UnlabeledWhois = null;
                if (state.QueuedWhois.Count > 0)
                {
                    next = state.QueuedWhois.Dequeue();
                    state.UnlabeledWhois = next;
                }
            }
        }

        RetireOperation(operation);
        if (next is not null && TryGet(networkId, out var workspace) && workspace is not null)
        {
            ScheduleOperationExpiry(networkId, next);
            _ = SendWhoisOperationAsync(workspace, next, CancellationToken.None);
        }

        RemoveEmptyOperationState(networkId);
    }

    private void CompleteListOperation(Guid networkId, ActiveOperation operation)
    {
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(networkId, out var state))
            {
                return;
            }

            if (ReferenceEquals(state.ActiveList, operation))
            {
                state.ActiveList = null;
            }
        }

        RetireOperation(operation);
        RemoveEmptyOperationState(networkId);
    }

    private void ClearOperations(Guid networkId)
    {
        ActiveOperation[] operations;
        lock (_operationsGate)
        {
            if (!_operations.Remove(networkId, out var state))
            {
                return;
            }

            operations = state.LabeledWhois.Values
                .Concat(state.UnlabeledWhois is null ? Array.Empty<ActiveOperation>() : [state.UnlabeledWhois])
                .Concat(state.QueuedWhois)
                .Concat(state.ActiveList is null ? Array.Empty<ActiveOperation>() : [state.ActiveList])
                .ToArray();
        }

        foreach (var operation in operations)
        {
            if (operation.View is WhoisView whois && whois.IsLoading)
            {
                whois.Fail("The network disconnected before numeric 318.");
            }

            RetireOperation(operation);
        }
    }

    private ActiveOperation? GetActiveList(Guid networkId)
    {
        lock (_operationsGate)
        {
            return _operations.TryGetValue(networkId, out var state) ? state.ActiveList : null;
        }
    }

    private void RetireOperation(ActiveOperation operation)
    {
        if (Interlocked.Exchange(ref operation.Retired, 1) == 0)
        {
            operation.Lifetime.Cancel();
            operation.Lifetime.Dispose();
        }
    }

    private NetworkOperationState GetOperationStateUnsafe(Guid networkId)
    {
        if (!_operations.TryGetValue(networkId, out var state))
        {
            state = new NetworkOperationState();
            _operations.Add(networkId, state);
        }

        return state;
    }

    private void RemoveEmptyOperationState(Guid networkId)
    {
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(networkId, out var state) && !state.HasAnyWhois && state.ActiveList is null)
            {
                _operations.Remove(networkId);
            }
        }
    }

    private string NextOperationLabelUnsafe(IEnumerable<string> occupiedLabels)
    {
        var occupied = occupiedLabels.ToHashSet(StringComparer.Ordinal);
        for (var attempt = 0; attempt < MaximumOutstandingOperations * 2; attempt++)
        {
            var candidate = $"nexirc-{Interlocked.Increment(ref _operationSequence) % 1_000_000L:000000}";
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No bounded IRC correlation label is available.");
    }

    private void Attach(SessionEntry entry)
    {
        entry.Session.StateChanged += OnSessionStateChanged;
        entry.Session.SemanticEventReceived += OnSessionSemanticEvent;
        entry.Workspace.ApplySnapshot(entry.Session.Snapshot);
    }

    private static void DisposeCredentialProviders(NetworkConnectionOptions options)
    {
        if (options.SaslCredentialProvider is IDisposable saslProvider)
        {
            saslProvider.Dispose();
        }

        if (options.PasswordProvider is IDisposable passwordProvider)
        {
            passwordProvider.Dispose();
        }
    }

    private void ReplaceSession(SessionEntry entry)
    {
        entry.Session = new ServerSession(entry.Options.ToSessionOptions(), _transportFactory);
        entry.Workspace.Options = entry.Options;
        entry.Workspace.Session = entry.Session;
        entry.Workspace.ResetForNewSession(entry.Session);
        entry.Started = false;
        entry.RunTask = null;
        entry.NeedsReplacement = false;
        Attach(entry);
    }

    private async Task StopEntryAsync(SessionEntry entry, string reason)
    {
        ClearOperations(entry.Workspace.Id);
        entry.Session.StateChanged -= OnSessionStateChanged;
        entry.Session.SemanticEventReceived -= OnSessionSemanticEvent;
        try
        {
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            entry.Workspace.StatusView.Append(IrcEventPresentation.CreateLocalCommand($"Session shutdown: {reason}"), markActivity: false);
        }
    }

    private async Task ObserveCompletionAsync(SessionEntry entry, Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Dispatch(() =>
            {
                if (!ReferenceEquals(entry.Session, entry.Workspace.Session))
                {
                    return;
                }

                entry.Workspace.StatusView.Append(new TranscriptEntry(DateTimeOffset.Now, TranscriptEntryKind.Error, null, $"Session stopped unexpectedly: {exception.Message}"));
            });
        }
        finally
        {
            if (ReferenceEquals(entry.RunTask, runTask))
            {
                entry.Started = false;
                entry.NeedsReplacement = true;
            }
        }
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEvent change)
    {
        if (sender is not ServerSession session || !TryGetEntry(session, out var entry))
        {
            return;
        }

        Dispatch(() =>
        {
            if (!IsCurrentGeneration(entry, session, change.ConnectionGeneration))
            {
                return;
            }

            var snapshot = session.Snapshot;
            entry.Workspace.ApplySnapshot(snapshot);
            if (change.Current is ServerSessionState.Disconnected or ServerSessionState.Failed)
            {
                ClearOperations(entry.Workspace.Id);
            }
            var kind = change.Current == ServerSessionState.Failed
                ? TranscriptEntryKind.Error
                : change.Current is ServerSessionState.ReconnectWaiting or ServerSessionState.Connecting
                    && change.Previous is ServerSessionState.ReconnectWaiting or ServerSessionState.Failed
                    ? TranscriptEntryKind.Reconnect
                    : TranscriptEntryKind.Connection;
            var text = FormatStateTransition(change, snapshot);
            if (snapshot.LastFailure is not null)
            {
                text += $": {snapshot.LastFailure.Message}";
            }

            entry.Workspace.StatusView.Append(new TranscriptEntry(DateTimeOffset.Now, kind, null, text));
            NotifyNavigationChanged();
            if (change.Current == ServerSessionState.Failed)
            {
                Notifications.Publish(new IrcNotification(
                    entry.Workspace.Id,
                    entry.Workspace.StatusView.Id,
                    WorkspaceViewKind.ServerStatus,
                    IrcNotificationType.Error,
                    WorkspaceActivity.Important,
                    null,
                    text,
                    DateTimeOffset.UtcNow,
                    entry.Workspace.StatusView.IsActive,
                    nameof(SessionStateChangedEvent)));
            }
        });
    }

    private void OnSessionSemanticEvent(object? sender, SessionSemanticEvent item)
    {
        if (sender is not ServerSession session || !TryGetEntry(session, out var entry))
        {
            return;
        }

        Dispatch(() =>
        {
            if (!IsCurrentGeneration(entry, session, item.ConnectionGeneration))
            {
                return;
            }

            var snapshot = session.Snapshot;
            entry.Workspace.ApplySnapshot(snapshot);
            RouteSemanticEvent(entry.Workspace, item.Event, snapshot);
        });
    }

    private void RouteSemanticEvent(NetworkWorkspace workspace, IrcSemanticEvent semanticEvent, ServerSessionSnapshot snapshot)
    {
        if (semanticEvent switch
        {
            IrcPrivmsgEvent message => IsIgnored(workspace, message.Message.Prefix, AccountTag(message.Message)),
            IrcQueryMessageEvent query => IsIgnored(workspace, query.Message.Prefix, AccountTag(query.Message)),
            IrcCtcpEvent ctcp => IsIgnored(workspace, ctcp.Message.Prefix, AccountTag(ctcp.Message)),
            _ => false
        })
        {
            // StateStore has already applied structural protocol state.  Ignore
            // only presentation/activity/notification for matching identities.
            return;
        }

        switch (semanticEvent)
        {
            case IrcCtcpEvent ctcp when snapshot.Features.ChannelTypes.Contains(ctcp.Target.FirstOrDefault()):
                AppendRendered(workspace.EnsureChannel(ctcp.Target, reopen: false), semanticEvent, snapshot);
                break;
            case IrcCtcpEvent ctcp when IrcIdentity.Equals(ctcp.Message.Prefix?.Name ?? string.Empty, snapshot.Nickname, snapshot.Features.CaseMapping):
                AppendRendered(workspace.EnsureQuery(ctcp.Target, reopen: false), semanticEvent, snapshot);
                break;
            case IrcCtcpEvent ctcp when ctcp.Message.Prefix?.Name is { } sender:
                AppendRendered(workspace.EnsureQuery(sender, reopen: false), semanticEvent, snapshot, WorkspaceActivity.Important);
                break;
            case IrcPrivmsgEvent message when snapshot.Features.ChannelTypes.Contains(message.Target.FirstOrDefault()):
                AppendRendered(workspace.EnsureChannel(message.Target, reopen: false), semanticEvent, snapshot);
                break;
            case IrcPrivmsgEvent message when message.IsNotice && message.Message.Prefix?.User is null:
                AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                break;
            case IrcPrivmsgEvent:
                // A direct message is rendered by the corresponding query
                // event below; do not duplicate it in server status.
                break;
            case IrcQueryMessageEvent query:
                AppendRendered(workspace.EnsureQuery(query.Nickname, reopen: false), semanticEvent, snapshot, WorkspaceActivity.Important);
                break;
            case IrcJoinEvent join:
                AppendRendered(workspace.EnsureChannel(join.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcPartEvent part:
                AppendRendered(workspace.EnsureChannel(part.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcKickEvent kick:
                AppendRendered(workspace.EnsureChannel(kick.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcTopicEvent topic:
                AppendRendered(workspace.EnsureChannel(topic.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcTopicUnsetEvent topic:
                AppendRendered(workspace.EnsureChannel(topic.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcNamesEvent names:
                AppendRendered(workspace.EnsureChannel(names.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcNamesCompleteEvent names:
                AppendRendered(workspace.EnsureChannel(names.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcWhoEvent who:
                AppendRendered(workspace.EnsureChannel(who.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcModeEvent mode:
                AppendRendered(workspace.EnsureChannel(mode.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcChannelSynchronizationEvent synchronization:
                AppendRendered(workspace.EnsureChannel(synchronization.Channel, reopen: false), semanticEvent, snapshot);
                break;
            case IrcListStartEvent listStartEvent:
                if (RouteListEvent(workspace, listStartEvent.RequestLabel, completes: false, starts: true) is not null)
                {
                    AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                }

                break;
            case IrcListItemEvent listItem:
                if (RouteListEvent(workspace, listItem.RequestLabel, completes: false, starts: false) is ChannelListView itemList)
                {
                    itemList.Apply(listItem, snapshot.Features.CaseMapping);
                }
                else if (listItem.RequestLabel is null)
                {
                    workspace.EnsureChannelList().Apply(listItem, snapshot.Features.CaseMapping);
                }

                break;
            case IrcListEndEvent listEndEvent:
                if (RouteListEvent(workspace, listEndEvent.RequestLabel, completes: true, starts: false) is not null)
                {
                    AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                }

                break;
            case IrcWhoisEvent whois:
                var whoisView = RouteWhoisEvent(workspace, whois);
                if (whoisView is null && whois.RequestLabel is null)
                {
                    whoisView = workspace.FindWhois(whois.Nickname) ?? workspace.EnsureWhois(whois.Nickname);
                    whoisView.Apply(whois);
                }

                if (whoisView is not null)
                {
                    AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                }

                break;
            case IrcUnknownNumericEvent unknownWhois when WhoisResult.IsPotentialAdditionalNumeric(unknownWhois.Numeric):
                var pendingWhois = RouteWhoisAdditionalEvent(workspace, unknownWhois);
                if (pendingWhois is null && !unknownWhois.Message.TagValues.ContainsKey("label"))
                {
                    pendingWhois = workspace.WhoisViews.LastOrDefault(view => view.IsLoading);
                }
                if (pendingWhois is not null)
                {
                    pendingWhois.ApplyAdditional(unknownWhois.Numeric, MessageText(unknownWhois.Message));
                }

                AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                break;
            case IrcQuitEvent quit:
                foreach (var channel in workspace.Channels)
                {
                    AppendRendered(channel, semanticEvent, snapshot);
                }

                break;
            case IrcNicknameChangedEvent:
                foreach (var channel in workspace.Channels)
                {
                    AppendRendered(channel, semanticEvent, snapshot);
                }

                AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                break;
            case IrcNumericEvent numeric when numeric.Numeric is 332 or 331 or 324 or 353 or 366 or 375 or 372 or 376 or 422
                || WhoisResult.IsKnownWhoisNumeric(numeric.Numeric):
                break;
            default:
                AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                break;
        }
    }

    private static string? AccountTag(IrcMessage message) =>
        message.TagValues.TryGetValue("account", out var account) && !string.Equals(account, "*", StringComparison.Ordinal)
            ? account
            : null;

    private void AppendRendered(WorkspaceView view, IrcSemanticEvent semanticEvent, ServerSessionSnapshot snapshot, WorkspaceActivity? activity = null)
    {
        var entry = IrcEventPresentation.Render(semanticEvent, snapshot);
        if (entry is null)
        {
            return;
        }

        var previousActivity = view.Activity;
        var isOwnMessage = semanticEvent switch
        {
            IrcPrivmsgEvent message => IrcIdentity.Equals(message.Message.Prefix?.Name ?? string.Empty, snapshot.Nickname, snapshot.Features.CaseMapping),
            IrcQueryMessageEvent query => IrcIdentity.Equals(query.Nickname, snapshot.Nickname, snapshot.Features.CaseMapping),
            IrcCtcpEvent ctcp => IrcIdentity.Equals(ctcp.Message.Prefix?.Name ?? string.Empty, snapshot.Nickname, snapshot.Features.CaseMapping),
            _ => false
        };
        var effectiveActivity = isOwnMessage ? WorkspaceActivity.None : activity ?? HighlightPolicy.Classify(view, semanticEvent, snapshot);
        if (semanticEvent is IrcPrivmsgEvent { IsNotice: false } channelMessage
            && view is ChannelView
            && HighlightPolicy.IsHighlight(channelMessage.Text, snapshot.Nickname, snapshot.Features.CaseMapping))
        {
            entry = entry with { Metadata = "highlight" };
        }

        view.Append(entry, markActivity: false);
        if (view is ChannelView channel && semanticEvent is IrcJoinEvent)
        {
            RecordRecent(GetWorkspace(view.NetworkId), DestinationKind.Channel, channel.Channel);
        }
        else if (view is QueryView query && view.EntriesSnapshot.Count == 1 && semanticEvent is IrcQueryMessageEvent or IrcCtcpEvent)
        {
            RecordRecent(GetWorkspace(view.NetworkId), DestinationKind.Query, query.Nickname);
        }

        _logging?.Record(view.NetworkId, GetWorkspace(view.NetworkId).ProfileId, view, entry);
        if (!view.IsActive && effectiveActivity != WorkspaceActivity.None)
        {
            view.MarkActivity(effectiveActivity);
        }

        if (!view.IsActive && view.Activity > previousActivity)
        {
            try
            {
                ActivityRaised?.Invoke(this, new WorkspaceActivityEventArgs(view.NetworkId, view.Id, view.Activity, entry));
            }
            catch
            {
            }
        }

        NotifyNavigationChanged();

        var notification = new IrcNotification(
            view.NetworkId,
            view.Id,
            view.Kind,
            NotificationType(semanticEvent, effectiveActivity),
            effectiveActivity,
            entry.Sender,
            entry.DisplayLine,
            entry.Timestamp,
            view.IsActive,
            semanticEvent.GetType().Name,
            isOwnMessage,
            new NotificationActivationTarget(
                view.NetworkId,
                TryGet(view.NetworkId, out var targetWorkspace) ? targetWorkspace?.ProfileId : null,
                view.Id,
                view.Kind,
                view is ChannelView targetChannel ? targetChannel.Channel : view is QueryView targetQuery ? targetQuery.Nickname : view.Title));
        if (_notificationCoalescer.ShouldPublish(notification))
        {
            Notifications.Publish(notification);
        }
    }

    private void SaveConfigurationInBackground()
    {
        if (Configuration is null) return;
        _ = SaveConfigurationAsync();
    }

    private async Task SaveConfigurationAsync()
    {
        try { if (Configuration is not null) await Configuration.SaveAsync().ConfigureAwait(false); } catch { }
    }

    private static string FormatStateTransition(SessionStateChangedEvent change, ServerSessionSnapshot snapshot)
    {
        var detail = change.Current switch
        {
            ServerSessionState.Connecting => "Connecting to the IRC server",
            ServerSessionState.TlsNegotiation => "Negotiating TLS",
            ServerSessionState.CapNegotiation => "Negotiating capabilities",
            ServerSessionState.Registering => "Registering",
            ServerSessionState.Registered => "Registered",
            ServerSessionState.ReconnectWaiting => "Waiting before reconnect",
            ServerSessionState.Disconnecting => "Disconnecting",
            ServerSessionState.Disconnected => "Disconnected",
            ServerSessionState.Failed => "Connection failed",
            _ => change.Current.ToString()
        };
        var identity = snapshot.Features.NetworkName ?? snapshot.Identity.NetworkName;
        return identity is null ? detail : $"{detail} · {identity}";
    }

    private static IrcNotificationType NotificationType(IrcSemanticEvent semanticEvent, WorkspaceActivity activity) => semanticEvent switch
    {
        IrcQueryMessageEvent => IrcNotificationType.PrivateMessage,
        IrcPrivmsgEvent { IsNotice: true } => IrcNotificationType.Notice,
        IrcPrivmsgEvent when activity == WorkspaceActivity.Important => IrcNotificationType.Highlight,
        IrcPrivmsgEvent => IrcNotificationType.Message,
        IrcServerErrorEvent or IrcUnknownCommandEvent or IrcUnknownNumericEvent => IrcNotificationType.Error,
        _ => IrcNotificationType.Status
    };

    private ConversationNavigationItem CreateNavigationItem(NetworkWorkspace network, WorkspaceView view) => new(
        ConversationIdentity.From(view),
        view.Id,
        network.DisplayName,
        network.NetworkName ?? network.Snapshot.Features.NetworkName ?? network.Snapshot.Identity.NetworkName ?? network.DisplayName,
        view is ChannelView channel ? channel.Channel : view is QueryView query ? query.Nickname : view.Title,
        view.Kind,
        view.Activity,
        view.LifecycleState,
        view.IsViewOpen,
        view.LastActivity);

    private bool IsOpenIdentityAvailable(ConversationIdentity identity) => Networks
        .Where(network => network.Id == identity.NetworkId)
        .SelectMany(network => network.Views)
        .Any(view => ConversationIdentity.From(view).SameAs(identity));

    private bool TryActivateIdentity(ConversationIdentity identity)
    {
        var view = Networks
            .Where(network => network.Id == identity.NetworkId)
            .SelectMany(network => network.Views)
            .FirstOrDefault(candidate => ConversationIdentity.From(candidate).SameAs(identity));
        if (view is null)
        {
            return false;
        }

        ActivateViewCore(view.Id, recordNavigation: false);
        return true;
    }

    private bool ActivateRelativeConversation(int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        var views = Networks.SelectMany(network => network.Views).ToArray();
        if (views.Length == 0)
        {
            return false;
        }

        var currentIndex = ActiveView is null ? (delta > 0 ? -1 : 0) : Array.IndexOf(views, ActiveView);
        if (currentIndex < 0)
        {
            currentIndex = delta > 0 ? -1 : 0;
        }

        var nextIndex = (currentIndex + delta + views.Length) % views.Length;
        ActivateViewCore(views[nextIndex].Id, recordNavigation: true);
        return true;
    }

    private bool ActivateMatchingView(Func<WorkspaceView, bool> predicate, int delta)
    {
        var views = Networks.SelectMany(network => network.Views).ToArray();
        if (views.Length == 0 || delta == 0)
        {
            return false;
        }

        var currentIndex = ActiveView is null ? (delta > 0 ? -1 : 0) : Array.IndexOf(views, ActiveView);
        if (currentIndex < 0)
        {
            currentIndex = delta > 0 ? -1 : 0;
        }

        for (var offset = 1; offset <= views.Length; offset++)
        {
            var index = (currentIndex + (delta * offset) + (views.Length * 2)) % views.Length;
            if (predicate(views[index]))
            {
                ActivateViewCore(views[index].Id, recordNavigation: true);
                return true;
            }
        }

        return false;
    }

    private void NotifyNavigationChanged()
    {
        try
        {
            NavigationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Navigation observers are presentation boundaries.
        }
    }

    private static string MessageText(nexIRC.Core.Protocol.IrcMessage message) => message.HasTrailingParameter
        ? message.TrailingParameter ?? string.Empty
        : string.Join(' ', message.Parameters);

    private void Dispatch(Action action)
    {
        Task task;
        try
        {
            task = _dispatcher.InvokeAsync(action).AsTask();
        }
        catch
        {
            return;
        }

        lock (_pendingGate)
        {
            _pendingDispatches.Add(task);
        }

        _ = RemovePendingAsync(task);
    }

    private async Task RemovePendingAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            lock (_pendingGate)
            {
                _pendingDispatches.Remove(task);
            }
        }
    }

    private bool TryGetEntry(ServerSession session, out SessionEntry entry)
    {
        lock (_entriesGate)
        {
            foreach (var candidate in _entries.Values)
            {
                if (ReferenceEquals(candidate.Session, session))
                {
                    entry = candidate;
                    return true;
                }
            }
        }

        entry = null!;
        return false;
    }

    private bool IsCurrentGeneration(SessionEntry entry, ServerSession session, int generation) =>
        ReferenceEquals(entry.Session, session)
        && ReferenceEquals(entry.Session, entry.Workspace.Session)
        && generation >= entry.Workspace.Snapshot.ConnectionGeneration;

    private SessionEntry GetEntry(Guid networkId) =>
        TryGetEntry(networkId, out var entry) ? entry : throw new KeyNotFoundException($"No network session exists for {networkId}.");

    private bool TryGetEntry(Guid networkId, out SessionEntry entry)
    {
        lock (_entriesGate)
        {
            if (_entries.TryGetValue(networkId, out entry!))
            {
                return true;
            }
        }

        entry = null!;
        return false;
    }

    private NetworkWorkspace GetWorkspace(Guid networkId) => GetEntry(networkId).Workspace;

    private NetworkWorkspace? ResolveActivationWorkspace(NotificationActivationTarget target) =>
        Networks.FirstOrDefault(item => item.Id == target.NetworkId)
        ?? (target.ProfileId is Guid profileId
            ? Networks.FirstOrDefault(item => item.ProfileId == profileId)
            : null);

    private static void ValidateOptions(NetworkConnectionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Nickname);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RealName);
    }

    private sealed class SessionEntry(NetworkWorkspace workspace, NetworkConnectionOptions options, ServerSession session)
    {
        public NetworkWorkspace Workspace { get; } = workspace;

        public NetworkConnectionOptions Options { get; set; } = options;

        public ServerSession Session { get; set; } = session;

        public bool Started { get; set; }

        public Task? RunTask { get; set; }

        public bool NeedsReplacement { get; set; }
    }
}
