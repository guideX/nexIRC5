using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using nexIRC.Core.Session;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Application;

public sealed class NetworkSessionManager : IAsyncDisposable
{
    private readonly nexIRC.Core.Networking.IIrcTransportFactory _transportFactory;
    private readonly SerializedWorkspaceDispatcher _dispatcher;
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
    private readonly IrcOperationTimeoutPolicy _operationTimeouts;
    private readonly object _disposeGate = new();
    private long _operationSequence;
    private long _activitySequence;
    private long _staleGenerationEventsDiscarded;
    private long _duplicateSemanticEventsDiscarded;
    private long _resynchronizationEventsSuppressed;
    private bool _disposed;
    private Task? _disposeTask;

    private const int MaximumOutstandingOperations = 64;

    public NetworkSessionManager(
        nexIRC.Core.Networking.IIrcTransportFactory transportFactory,
        IWorkspaceDispatcher? dispatcher = null,
        IIrcNotificationService? notifications = null,
        HighlightActivityPolicy? highlightPolicy = null,
        ConfigurationService? configuration = null,
        IConversationLogStore? logStore = null,
        ConversationLoggingService? logging = null,
        IrcOperationTimeoutPolicy? operationTimeouts = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _dispatcher = new SerializedWorkspaceDispatcher(dispatcher ?? new ImmediateWorkspaceDispatcher());
        Notifications = notifications ?? new NotificationSubscriptionService();
        _ownsNotifications = notifications is null;
        HighlightPolicy = highlightPolicy ?? new HighlightActivityPolicy();
        Configuration = configuration;
        _logStore = logStore;
        _logging = logging ?? (logStore is not null && configuration is not null
            ? new ConversationLoggingService(logStore, () => configuration.Preferences)
            : null);
        _ownsLogStore = logStore is not null;
        _operationTimeouts = operationTimeouts ?? new IrcOperationTimeoutPolicy();
        if (configuration is not null)
        {
            ApplyPreferences(configuration.Preferences);
        }
    }

    public ThreadSafeObservableCollection<NetworkWorkspace> Networks { get; } = [];

    public NetworkWorkspace? ActiveNetwork { get; private set; }

    public WorkspaceView? ActiveView { get; private set; }

    public IIrcNotificationService Notifications { get; }

    public HighlightActivityPolicy HighlightPolicy { get; }

    public ConfigurationService? Configuration { get; }

    public IConversationLogStore? LogStore => _logStore;

    public ConversationLoggingService? Logging => _logging;

    public OperationFeedbackViewModel OperationFeedback { get; } = new();

    public IrcOperationTimeoutPolicy OperationTimeouts => _operationTimeouts;

    public NetworkSessionDiagnostics Diagnostics => new(
        _dispatcher.Diagnostics,
        Interlocked.Read(ref _staleGenerationEventsDiscarded),
        Interlocked.Read(ref _duplicateSemanticEventsDiscarded),
        Interlocked.Read(ref _resynchronizationEventsSuppressed));

    /// <summary>
    /// Number of manager-owned dispatch tasks that have not retired yet.
    /// This is diagnostic state only and does not retain completed work.
    /// </summary>
    public int PendingDispatchCount
    {
        get
        {
            lock (_pendingGate)
            {
                return _pendingDispatches.Count;
            }
        }
    }

    /// <summary>
    /// Number of session entries currently owned by this manager.
    /// This is diagnostic state only and does not retain completed work.
    /// </summary>
    public int ManagedSessionCount
    {
        get
        {
            lock (_entriesGate)
            {
                return _entries.Count;
            }
        }
    }

    public ValueTask FlushStateDispatchAsync() => _dispatcher.FlushAsync();

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
            ? items.OrderByDescending(item => item.LastActivity)
                .ThenByDescending(item => item.LastActivitySequence)
                .ThenBy(item => item.NetworkDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
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
        var supportsLabels = workspace.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.LabeledResponse);
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
                if (state.Count >= MaximumOutstandingOperations)
                {
                    throw new InvalidOperationException("Too many WHOIS operations are already outstanding on this network.");
                }
            }
            else if (state.Count >= MaximumOutstandingOperations)
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

        if (!result.WasCoalesced)
        {
            PublishQueryPending(result.Operation);
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
        var supportsLabels = workspace.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.LabeledResponse);
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
        PublishQueryPending(operation.Operation);
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

    public async ValueTask<IrcQueryRequestResult> RequestBanListAsync(
        Guid networkId,
        string channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var workspace = GetWorkspace(networkId);
        var mapping = workspace.Snapshot.Features.CaseMapping;
        ActiveOperation? operation;
        lock (_operationsGate)
        {
            var state = GetOperationStateUnsafe(networkId);
            operation = state.BanLists.Values.FirstOrDefault(item =>
                IrcIdentity.Equals(item.Operation.Target, channel, mapping));
            if (operation is not null)
            {
                var coalesced = new IrcQueryRequestResult(
                    operation.View,
                    operation.Operation with { Correlation = IrcQueryCorrelationMode.CoalescedUnlabeled },
                    true);
                ActivateView(operation.View.Id);
                return coalesced;
            }

            if (state.Count >= MaximumOutstandingOperations)
            {
                throw new InvalidOperationException("Too many IRC operations are already outstanding on this network.");
            }

            var supportsLabels = workspace.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.LabeledResponse);
            var label = supportsLabels
                ? NextOperationLabelUnsafe(state.LabeledWhois.Keys
                    .Concat(state.BanLists.Values.Select(item => item.Operation.RequestLabel ?? string.Empty))
                    .Append(state.ActiveList?.Operation.RequestLabel ?? string.Empty))
                : null;
            var view = workspace.EnsureBanList(channel, beginRequest: true);
            operation = new ActiveOperation
            {
                Operation = new IrcQueryOperation(
                    Guid.NewGuid(),
                    networkId,
                    "BANLIST",
                    channel,
                    label,
                    supportsLabels ? IrcQueryCorrelationMode.LabeledResponse : IrcQueryCorrelationMode.SerializedUnlabeled,
                    DateTimeOffset.UtcNow),
                View = view
            };
            state.BanLists.Add(operation.Operation.Id, operation);
        }

        ScheduleOperationExpiry(networkId, operation);
        PublishQueryPending(operation.Operation);
        try
        {
            var mode = workspace.Snapshot.Features.ChannelModes?.ListModes.FirstOrDefault() ?? 'b';
            if (operation.Operation.RequestLabel is { } requestLabel)
            {
                await workspace.Session.SendTaggedCommandAsync(
                    new Dictionary<string, string?> { ["label"] = requestLabel },
                    "MODE",
                    [channel, $"+{mode}"],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await workspace.Session.SendCommandAsync("MODE", [channel, $"+{mode}"], cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            CompleteBanListOperation(networkId, operation, IrcOperationState.Cancelled, "Ban-list request could not be sent.", null);
            throw;
        }

        ActivateView(operation.View.Id);
        return new IrcQueryRequestResult(operation.View, operation.Operation, false);
    }

    public IrcOperationResult StartOperation(
        IrcOperationType type,
        Guid networkId,
        string targetConversation,
        string? targetNickname = null,
        string? requestedMode = null,
        string? requestedMask = null,
        string? command = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetConversation);
        var workspace = GetWorkspace(networkId);
        PendingActionOperation operation;
        lock (_operationsGate)
        {
            var state = GetOperationStateUnsafe(networkId);
            if (state.Count >= MaximumOutstandingOperations)
            {
                throw new InvalidOperationException("Too many IRC operations are already outstanding on this network.");
            }

            var result = new IrcOperationResult(
                Guid.NewGuid(),
                networkId,
                workspace.Snapshot.ConnectionGeneration,
                type,
                targetConversation,
                targetNickname,
                requestedMode,
                requestedMask,
                DateTimeOffset.UtcNow,
                IrcOperationState.Pending,
                false,
                null,
                null,
                null,
                null);
            operation = new PendingActionOperation
            {
                Result = result,
                CurrentNickname = targetNickname,
                Command = command ?? type.ToString().ToUpperInvariant()
            };
            state.Actions.Add(result.Id, operation);
        }

        OperationFeedback.AddOrUpdate(operation.Result);
        _ = ExpireActionOperationAsync(networkId, operation);
        return operation.Result;
    }

    public bool CancelOperation(Guid networkId, Guid operationId, IrcOperationState state = IrcOperationState.Cancelled, string? explanation = null)
    {
        if (state is IrcOperationState.Pending or IrcOperationState.Confirmed or IrcOperationState.Rejected)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        PendingActionOperation? operation = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(networkId, out var operations) && operations.Actions.Remove(operationId, out operation))
            {
                operation.Result = operation.Result with
                {
                    State = state,
                    Explanation = explanation ?? (state == IrcOperationState.Disconnected ? "The network disconnected before confirmation." : "The operation was cancelled.")
                };
            }
        }

        if (operation is null)
        {
            return false;
        }

        RetireActionOperation(operation);
        OperationFeedback.AddOrUpdate(operation.Result);
        RemoveEmptyOperationState(networkId);
        return true;
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

        foreach (var networkItem in Networks)
        {
            if (!ReferenceEquals(networkItem, workspace))
            {
                networkItem.DeactivateActiveView();
            }
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
                .Concat(workspace.BanListViews)
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
        else if (view is BanListView banList)
        {
            workspace.BanListViews.Remove(banList);
            workspace.Close(banList);
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
            entry.EventDrainTasks = StartEventDrainers(entry.Session);
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

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
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
        await _dispatcher.CompleteAsync().ConfigureAwait(false);

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
        entry = entry with { Sequence = NextActivitySequence() };
        if (TryGet(view.NetworkId, out var workspace) && workspace is not null)
        {
            entry = entry with { Provenance = ConversationEntryProvenance.Live };
            view.AppendConversationEntry(entry, updateLastActivity: false);
            _logging?.Record(workspace.Id, workspace.ProfileId, view, entry);
        }

        NotifyNavigationChanged();
    }

    private void PublishQueryPending(IrcQueryOperation operation)
    {
        var type = operation.Kind switch
        {
            "WHOIS" => IrcOperationType.Whois,
            "BANLIST" => IrcOperationType.BanListQuery,
            _ => IrcOperationType.ChannelModeQuery
        };
        var workspace = GetWorkspace(operation.NetworkId);
        var result = new IrcOperationResult(
            operation.Id,
            operation.NetworkId,
            workspace.Snapshot.ConnectionGeneration,
            type,
            operation.Target,
            type == IrcOperationType.Whois ? operation.Target : null,
            type == IrcOperationType.BanListQuery
                ? $"+{workspace.Snapshot.Features.ChannelModes?.ListModes.FirstOrDefault() ?? 'b'}"
                : null,
            null,
            operation.StartedAt,
            IrcOperationState.Pending,
            false,
            null,
            null,
            null,
            null);
        if (TryFindActiveQuery(operation.NetworkId, operation.Id, out var active))
        {
            active.Feedback = result;
        }

        OperationFeedback.AddOrUpdate(result);
    }

    public bool TryGetOperation(Guid operationId, out IrcOperationResult? result) =>
        (result = OperationFeedback.Snapshot.LastOrDefault(item => item.Id == operationId)) is not null;

    private bool TryFindActiveQuery(Guid networkId, Guid operationId, out ActiveOperation operation)
    {
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(networkId, out var state))
            {
                operation = state.LabeledWhois.Values
                    .Concat(state.UnlabeledWhois is null ? Array.Empty<ActiveOperation>() : [state.UnlabeledWhois])
                    .Concat(state.QueuedWhois)
                    .Concat(state.BanLists.Values)
                    .Concat(state.ActiveList is null ? Array.Empty<ActiveOperation>() : [state.ActiveList])
                    .FirstOrDefault(item => item.Operation.Id == operationId)!;
                return operation is not null;
            }
        }

        operation = null!;
        return false;
    }

    private void CompleteQueryFeedback(
        ActiveOperation operation,
        IrcOperationState state,
        int? numeric = null,
        string? explanation = null,
        string? protocolDetail = null,
        string? rawServerLine = null)
    {
        if (operation.Feedback is not { } feedback)
        {
            return;
        }

        operation.Feedback = feedback with
        {
            State = state,
            ServerConfirmed = state == IrcOperationState.Confirmed,
            Numeric = numeric,
            Explanation = explanation,
            ProtocolDetail = protocolDetail,
            RawServerLine = rawServerLine
        };
        OperationFeedback.AddOrUpdate(operation.Feedback);
    }

    private void CompleteAction(
        NetworkWorkspace workspace,
        PendingActionOperation operation,
        IrcOperationState state,
        int? numeric = null,
        string? explanation = null,
        string? protocolDetail = null,
        string? rawServerLine = null)
    {
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(workspace.Id, out var stateEntry)
                || !stateEntry.Actions.Remove(operation.Result.Id, out _))
            {
                return;
            }
        }

        operation.Result = operation.Result with
        {
            State = state,
            ServerConfirmed = state == IrcOperationState.Confirmed,
            Numeric = numeric,
            Explanation = explanation,
            ProtocolDetail = protocolDetail,
            RawServerLine = rawServerLine
        };
        RetireActionOperation(operation);
        OperationFeedback.AddOrUpdate(operation.Result);
        RemoveEmptyOperationState(workspace.Id);
    }

    private void ReconcileModeEvent(NetworkWorkspace workspace, IrcModeEvent mode)
    {
        PendingActionOperation? match = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(workspace.Id, out var state))
            {
                match = state.Actions.Values
                    .Where(item => item.Result.ConnectionGeneration == workspace.Snapshot.ConnectionGeneration)
                    .Where(item => IrcIdentity.Equals(item.Result.TargetConversation, mode.Channel, workspace.Snapshot.Features.CaseMapping))
                    .Where(item => item.Result.Type is IrcOperationType.ChannelModeQuery or IrcOperationType.ModeChange or IrcOperationType.Ban or IrcOperationType.Unban)
                    .FirstOrDefault(item => item.Result.Type == IrcOperationType.ChannelModeQuery || mode.Changes.Any(change =>
                        (item.Result.RequestedMode is null || item.Result.RequestedMode.Contains(change.Mode, StringComparison.Ordinal))
                        && (item.Result.TargetNickname is null
                            || IrcIdentity.Equals(item.CurrentNickname ?? item.Result.TargetNickname, change.Parameter ?? string.Empty, workspace.Snapshot.Features.CaseMapping))
                        && (item.Result.RequestedMask is null || string.Equals(item.Result.RequestedMask, change.Parameter, StringComparison.Ordinal))
                        && (item.Result.RequestedMode is null || ((item.Result.RequestedMode.StartsWith('+')) == change.IsAdding))));
            }
        }

        if (match is not null)
        {
            CompleteAction(workspace, match, IrcOperationState.Confirmed, explanation: $"Server confirmed {match.Result.RequestedMode ?? "MODE"} in {mode.Channel}.");
        }
    }

    private void ReconcileKickEvent(NetworkWorkspace workspace, IrcKickEvent kick)
    {
        if (IrcIdentity.Equals(kick.Nickname, workspace.Snapshot.Nickname, workspace.Snapshot.Features.CaseMapping)
            && workspace.Channels.FirstOrDefault(channel => IrcIdentity.Equals(channel.Channel, kick.Channel, workspace.Snapshot.Features.CaseMapping)) is { } selfKicked)
        {
            selfKicked.SetLifecycleState(ConversationLifecycleState.Kicked);
        }

        PendingActionOperation? match = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(workspace.Id, out var state))
            {
                match = state.Actions.Values.FirstOrDefault(item =>
                    item.Result.ConnectionGeneration == workspace.Snapshot.ConnectionGeneration
                    && item.Result.Type == IrcOperationType.Kick
                    && IrcIdentity.Equals(item.Result.TargetConversation, kick.Channel, workspace.Snapshot.Features.CaseMapping)
                    && IrcIdentity.Equals(item.CurrentNickname ?? item.Result.TargetNickname ?? string.Empty, kick.Nickname, workspace.Snapshot.Features.CaseMapping));
            }
        }

        if (match is not null)
        {
            CompleteAction(workspace, match, IrcOperationState.Confirmed, explanation: $"Server confirmed removing {kick.Nickname} from {kick.Channel}.");
        }
    }

    private void ReconcileTopicEvent(NetworkWorkspace workspace, IrcTopicEvent topic)
    {
        if (!string.Equals(topic.Message.Command, "TOPIC", StringComparison.Ordinal))
        {
            return;
        }

        PendingActionOperation? match = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(workspace.Id, out var state))
            {
                match = state.Actions.Values.FirstOrDefault(item =>
                    item.Result.Type == IrcOperationType.TopicChange
                    && item.Result.ConnectionGeneration == workspace.Snapshot.ConnectionGeneration
                    && IrcIdentity.Equals(item.Result.TargetConversation, topic.Channel, workspace.Snapshot.Features.CaseMapping));
            }
        }

        if (match is not null)
        {
            CompleteAction(workspace, match, IrcOperationState.Confirmed, explanation: $"Server confirmed the topic change in {topic.Channel}.");
        }
    }

    private void ReconcileNumericOperation(NetworkWorkspace workspace, IrcServerNumericEvent numeric)
    {
        if (numeric.Numeric == 341)
        {
            PendingActionOperation? invite = null;
            lock (_operationsGate)
            {
                if (_operations.TryGetValue(workspace.Id, out var state))
                {
                    invite = state.Actions.Values.FirstOrDefault(item =>
                        item.Result.Type == IrcOperationType.Invite
                        && item.Result.ConnectionGeneration == workspace.Snapshot.ConnectionGeneration
                        && IrcIdentity.Equals(item.Result.TargetConversation, numeric.Interpretation.TargetChannel ?? string.Empty, workspace.Snapshot.Features.CaseMapping)
                        && IrcIdentity.Equals(item.CurrentNickname ?? item.Result.TargetNickname ?? string.Empty, numeric.Interpretation.TargetNickname ?? string.Empty, workspace.Snapshot.Features.CaseMapping));
                }
            }

            if (invite is not null)
            {
                CompleteAction(workspace, invite, IrcOperationState.Confirmed, 341, numeric.Interpretation.FriendlyExplanation, NumericProtocolDetail(numeric), numeric.Message.RawLine);
            }

            return;
        }

        PendingActionOperation? match = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(workspace.Id, out var state))
            {
                var interpretation = numeric.Interpretation;
                match = state.Actions.Values
                    .Where(item => item.Result.ConnectionGeneration == workspace.Snapshot.ConnectionGeneration)
                    .Where(item => NumericMayReject(item.Result.Type, numeric.Numeric))
                    .Where(item => interpretation.TargetChannel is null
                        || IrcIdentity.Equals(item.Result.TargetConversation, interpretation.TargetChannel, workspace.Snapshot.Features.CaseMapping))
                    .Where(item => interpretation.TargetNickname is null
                        || IrcIdentity.Equals(item.CurrentNickname ?? item.Result.TargetNickname ?? string.Empty, interpretation.TargetNickname, workspace.Snapshot.Features.CaseMapping))
                    .OrderBy(item => item.Result.StartedAt)
                    .FirstOrDefault(item => interpretation.Command is null
                        || item.Command.Equals(interpretation.Command, StringComparison.OrdinalIgnoreCase)
                        || item.Result.Type == IrcOperationType.Whois);
            }
        }

        if (match is not null)
        {
            CompleteAction(workspace, match, IrcOperationState.Rejected, numeric.Numeric, numeric.Interpretation.FriendlyExplanation, NumericProtocolDetail(numeric), numeric.Message.RawLine);
        }

        if (numeric.Numeric == 401)
        {
            var target = numeric.Interpretation.TargetNickname;
            var whois = workspace.WhoisViews.LastOrDefault(view => view.IsLoading
                && IrcIdentity.Equals(view.RequestedNickname, target ?? string.Empty, workspace.Snapshot.Features.CaseMapping));
            foreach (var operation in FindWhoisOperations(workspace.Id, target, workspace.Snapshot.Features.CaseMapping))
            {
                RemoveWhoisOperation(workspace.Id, operation, IrcOperationState.Rejected, numeric.Numeric, numeric.Interpretation.FriendlyExplanation, NumericProtocolDetail(numeric), numeric.Message.RawLine);
            }

            whois?.Fail(numeric.Interpretation.FriendlyExplanation + $" [{numeric.Name} {numeric.Numeric}: {numeric.Interpretation.ProtocolText}]");
        }
    }

    private static bool NumericMayReject(IrcOperationType type, int numeric) => numeric switch
    {
        401 => type is IrcOperationType.Whois or IrcOperationType.Notice or IrcOperationType.Ctcp or IrcOperationType.Invite or IrcOperationType.Kick,
        403 or 404 or 442 or 471 or 473 or 474 or 475 or 476 or 477 or 482 => type is not IrcOperationType.Whois,
        443 => type == IrcOperationType.Invite,
        421 or 461 => true,
        472 => type is IrcOperationType.ModeChange or IrcOperationType.Ban or IrcOperationType.Unban,
        481 or 485 => true,
        _ => false
    };

    private static string NumericProtocolDetail(IrcServerNumericEvent numeric) =>
        $"{numeric.Name} {numeric.Numeric}: {numeric.Interpretation.ProtocolText}";

    private ActiveOperation[] FindWhoisOperations(Guid networkId, string? nickname, IrcCaseMapping mapping)
    {
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(networkId, out var state))
            {
                return Array.Empty<ActiveOperation>();
            }

            return state.LabeledWhois.Values
                .Concat(state.UnlabeledWhois is null ? Array.Empty<ActiveOperation>() : [state.UnlabeledWhois])
                .Where(item => nickname is null || IrcIdentity.Equals(item.Operation.Target, nickname, mapping))
                .ToArray();
        }
    }

    private void ReconcileNicknameChange(NetworkWorkspace workspace, IrcNicknameChangedEvent nick)
    {
        if (nick.PreviousNickname is null)
        {
            return;
        }

        PendingActionOperation[] matches;
        lock (_operationsGate)
        {
            matches = _operations.TryGetValue(workspace.Id, out var state)
                ? state.Actions.Values
                    .Where(item => item.Result.ConnectionGeneration == workspace.Snapshot.ConnectionGeneration)
                    .Where(item => item.CurrentNickname is not null
                        && IrcIdentity.Equals(item.CurrentNickname, nick.PreviousNickname, workspace.Snapshot.Features.CaseMapping))
                    .ToArray()
                : Array.Empty<PendingActionOperation>();
        }

        foreach (var match in matches)
        {
            match.CurrentNickname = nick.NewNickname;
        }
    }

    private QueryView? ReconcileQueryNicknameChange(
        NetworkWorkspace workspace,
        IrcNicknameChangedEvent nick,
        ServerSessionSnapshot snapshot)
    {
        if (nick.PreviousNickname is null)
        {
            return null;
        }

        var query = workspace.FindQuery(nick.PreviousNickname);
        if (query is null
            || !query.IsIdentityBoundToCurrentSession
            || query.IdentityConnectionGeneration != snapshot.ConnectionGeneration)
        {
            return null;
        }

        var collision = workspace.FindQuery(nick.NewNickname);
        if (collision is not null && !ReferenceEquals(collision, query))
        {
            // Two existing query views do not prove that the targets are the
            // same person. Keep both logical conversations separate.
            return null;
        }

        if (!query.CanApplyNicknameChange(nick.PreviousNickname, nick.NewNickname, snapshot.Features.CaseMapping))
        {
            return null;
        }

        _navigationHistory.Rename(
            workspace.Id,
            WorkspaceViewKind.Query,
            nick.PreviousNickname,
            nick.NewNickname,
            snapshot.Features.CaseMapping);
        if (Configuration is not null)
        {
            Configuration.RenameDestination(
                workspace.ProfileId ?? workspace.Id,
                DestinationKind.Query,
                nick.PreviousNickname,
                nick.NewNickname,
                snapshot.Features.CaseMapping);
            SaveConfigurationInBackground();
        }

        query.TryApplyNicknameChange(
            nick.PreviousNickname,
            nick.NewNickname,
            snapshot.Features.CaseMapping,
            snapshot.ConnectionGeneration);

        return query;
    }

    private BanListView? RouteBanListItemEvent(NetworkWorkspace workspace, IrcBanListItemEvent item)
    {
        ActiveOperation? operation = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(workspace.Id, out var state))
            {
                operation = item.RequestLabel is { } label
                    ? state.BanLists.Values.FirstOrDefault(candidate => candidate.Operation.RequestLabel == label)
                    : state.BanLists.Values.FirstOrDefault(candidate => IrcIdentity.Equals(candidate.Operation.Target, item.Entry.Channel, workspace.Snapshot.Features.CaseMapping));
            }
        }

        if (operation?.View is not BanListView view)
        {
            return null;
        }

        view.Apply(item);
        return view;
    }

    private BanListView? RouteBanListEndEvent(NetworkWorkspace workspace, IrcBanListEndEvent item)
    {
        ActiveOperation? operation = null;
        lock (_operationsGate)
        {
            if (_operations.TryGetValue(workspace.Id, out var state))
            {
                operation = item.RequestLabel is { } label
                    ? state.BanLists.Values.FirstOrDefault(candidate => candidate.Operation.RequestLabel == label)
                    : state.BanLists.Values.FirstOrDefault(candidate => IrcIdentity.Equals(candidate.Operation.Target, item.Channel, workspace.Snapshot.Features.CaseMapping));
            }
        }

        if (operation?.View is not BanListView view)
        {
            return null;
        }

        view.CompleteRequest();
        CompleteBanListOperation(workspace.Id, operation, IrcOperationState.Confirmed, "Server completed the ban-list response.", null, item.Message.RawLine);
        return view;
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
            _ = CompleteWhoisOperationAsync(workspace, operation, item.Message.RawLine);
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
            var timeout = operation.Operation.Kind == "WHOIS"
                ? _operationTimeouts.EffectiveWhois
                : operation.Operation.Kind == "BANLIST"
                    ? _operationTimeouts.EffectiveBanList
                    : _operationTimeouts.EffectiveModeration;
            await Task.Delay(timeout, operation.Lifetime.Token).ConfigureAwait(false);
            Dispatch(() =>
            {
                if (operation.Operation.Kind == "WHOIS")
                {
                    if (operation.View is WhoisView whois)
                    {
                        whois.Fail("Confirmation was not observed before the WHOIS timeout.");
                    }

                    RemoveWhoisOperation(networkId, operation, IrcOperationState.TimedOut, null, "Confirmation was not observed before the WHOIS timeout.", null);
                }
                else if (operation.Operation.Kind == "BANLIST")
                {
                    if (operation.View is BanListView banList)
                    {
                        banList.Fail("Confirmation was not observed before the ban-list timeout.");
                    }

                    CompleteBanListOperation(networkId, operation, IrcOperationState.TimedOut, "Confirmation was not observed before the ban-list timeout.", null);
                }
                else
                {
                    CompleteListOperation(networkId, operation);
                }
            }, WorkspaceDispatchActionCategory.OperationFeedback);
        }
        catch (OperationCanceledException) when (operation.Lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task CompleteWhoisOperationAsync(NetworkWorkspace workspace, ActiveOperation completed, string? rawServerLine)
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

        CompleteQueryFeedback(completed, IrcOperationState.Confirmed, 318, "Server completed the WHOIS response.", "End of WHOIS list", rawServerLine);
        RetireOperation(completed);
        if (next is not null)
        {
            ScheduleOperationExpiry(workspace.Id, next);
            await SendWhoisOperationAsync(workspace, next, CancellationToken.None).ConfigureAwait(false);
        }
        RemoveEmptyOperationState(workspace.Id);
    }

    private void RemoveWhoisOperation(
        Guid networkId,
        ActiveOperation operation,
        IrcOperationState completionState = IrcOperationState.Cancelled,
        int? numeric = null,
        string? explanation = null,
        string? protocolDetail = null,
        string? rawServerLine = null)
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

        CompleteQueryFeedback(operation, completionState, numeric, explanation, protocolDetail, rawServerLine);
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

        CompleteQueryFeedback(operation, IrcOperationState.Confirmed, explanation: "Server completed the channel-list response.");
        RetireOperation(operation);
        RemoveEmptyOperationState(networkId);
    }

    private void CompleteBanListOperation(
        Guid networkId,
        ActiveOperation operation,
        IrcOperationState completionState,
        string? explanation,
        string? protocolDetail,
        string? rawServerLine = null)
    {
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(networkId, out var state)
                || !state.BanLists.Remove(operation.Operation.Id, out var current)
                || !ReferenceEquals(current, operation))
            {
                return;
            }
        }

        CompleteQueryFeedback(operation, completionState, completionState == IrcOperationState.Confirmed ? 368 : null, explanation, protocolDetail, rawServerLine);
        RetireOperation(operation);
        RemoveEmptyOperationState(networkId);
    }

    private void ClearOperations(Guid networkId)
    {
        ActiveOperation[] operations;
        PendingActionOperation[] actions;
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
                .Concat(state.BanLists.Values)
                .ToArray();
            actions = state.Actions.Values.ToArray();
        }

        foreach (var operation in operations)
        {
            if (operation.View is WhoisView whois && whois.IsLoading)
            {
                whois.Fail("The network disconnected before numeric 318.");
            }
            else if (operation.View is BanListView banList && banList.IsLoading)
            {
                banList.Fail("The network disconnected before the ban-list response completed.");
            }

            CompleteQueryFeedback(operation, IrcOperationState.Disconnected, explanation: "The network disconnected before confirmation.");
            RetireOperation(operation);
        }

        foreach (var action in actions)
        {
            action.Result = action.Result with
            {
                State = IrcOperationState.Disconnected,
                Explanation = "The network disconnected before confirmation."
            };
            RetireActionOperation(action);
            OperationFeedback.AddOrUpdate(action.Result);
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

    private static void RetireActionOperation(PendingActionOperation operation)
    {
        if (Interlocked.Exchange(ref operation.Retired, 1) == 0)
        {
            operation.Lifetime.Cancel();
            operation.Lifetime.Dispose();
        }
    }

    private async Task ExpireActionOperationAsync(Guid networkId, PendingActionOperation operation)
    {
        try
        {
            var timeout = operation.Result.Type == IrcOperationType.Invite
                ? _operationTimeouts.EffectiveInvite
                : _operationTimeouts.EffectiveModeration;
            await Task.Delay(timeout, operation.Lifetime.Token).ConfigureAwait(false);
            Dispatch(() =>
            {
                if (TryGet(networkId, out var workspace) && workspace is not null)
                {
                    CompleteAction(workspace, operation, IrcOperationState.TimedOut, explanation: "Server confirmation was not observed before the operation timeout.");
                }
            }, WorkspaceDispatchActionCategory.OperationFeedback);
        }
        catch (OperationCanceledException) when (operation.Lifetime.IsCancellationRequested)
        {
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
            if (_operations.TryGetValue(networkId, out var state) && !state.HasAnyWhois && state.ActiveList is null && state.BanLists.Count == 0 && state.Actions.Count == 0)
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
            await Task.WhenAll(entry.EventDrainTasks).ConfigureAwait(false);
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
            }, WorkspaceDispatchActionCategory.Lifecycle);
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

        var snapshot = session.Snapshot;
        Dispatch(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!IsCurrentGeneration(entry, session, change.ConnectionGeneration))
            {
                Interlocked.Increment(ref _staleGenerationEventsDiscarded);
                return;
            }

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

            entry.Workspace.StatusView.Append(new TranscriptEntry(DateTimeOffset.Now, kind, null, text, Sequence: NextActivitySequence()));
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
        }, WorkspaceDispatchActionCategory.Lifecycle);
    }

    private void OnSessionSemanticEvent(object? sender, SessionSemanticEvent item)
    {
        if (sender is not ServerSession session || !TryGetEntry(session, out var entry))
        {
            return;
        }

        var snapshot = session.Snapshot;
        Dispatch(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!IsCurrentGeneration(entry, session, item.ConnectionGeneration))
            {
                Interlocked.Increment(ref _staleGenerationEventsDiscarded);
                return;
            }

            if (!AcceptSemanticEvent(entry, item))
            {
                Interlocked.Increment(ref _duplicateSemanticEventsDiscarded);
                return;
            }

            entry.Workspace.ApplySnapshot(snapshot);
            RouteSemanticEvent(entry.Workspace, item.Event, snapshot, item.ReceivedAt);
        }, DispatchCategory(item.Event));
    }

    private void RouteSemanticEvent(
        NetworkWorkspace workspace,
        IrcSemanticEvent semanticEvent,
        ServerSessionSnapshot snapshot,
        DateTimeOffset? receivedAt)
    {
        if (ShouldSuppressIgnoredPresentation(workspace, semanticEvent))
        {
            // StateStore has already applied structural protocol state.  Ignore
            // only presentation/activity/notification for matching identities.
            return;
        }

        void Append(
            WorkspaceView view,
            IrcSemanticEvent item,
            WorkspaceActivity? activity = null,
            bool publishNotification = true,
            bool updateLastActivity = true) =>
            AppendRendered(view, item, snapshot, activity, publishNotification, updateLastActivity, receivedAt);

        switch (semanticEvent)
        {
            case IrcCtcpEvent ctcp when snapshot.Features.ChannelTypes.Contains(ctcp.Target.FirstOrDefault()):
                Append(workspace.EnsureChannel(ctcp.Target, reopen: false), semanticEvent);
                break;
            case IrcCtcpEvent ctcp when IrcIdentity.Equals(ctcp.Message.Prefix?.Name ?? string.Empty, snapshot.Nickname, snapshot.Features.CaseMapping):
                Append(workspace.EnsureIncomingQuery(ctcp.Target), semanticEvent);
                break;
            case IrcCtcpEvent ctcp when ctcp.Message.Prefix?.Name is { } sender:
                Append(workspace.EnsureIncomingQuery(sender), semanticEvent, WorkspaceActivity.Important);
                break;
            case IrcPrivmsgEvent message when snapshot.Features.ChannelTypes.Contains(message.Target.FirstOrDefault()):
                Append(workspace.EnsureChannel(message.Target, reopen: false), semanticEvent);
                break;
            case IrcPrivmsgEvent message when message.IsNotice && message.Message.Prefix?.User is null:
                Append(workspace.StatusView, semanticEvent);
                break;
            case IrcPrivmsgEvent:
                // A direct message is rendered by the corresponding query
                // event below; do not duplicate it in server status.
                break;
            case IrcQueryMessageEvent query:
                Append(workspace.EnsureIncomingQuery(query.Nickname), semanticEvent, WorkspaceActivity.Important);
                break;
            case IrcJoinEvent join:
                Append(workspace.EnsureChannel(join.Channel, reopen: false), semanticEvent);
                break;
            case IrcPartEvent part:
                Append(workspace.EnsureChannel(part.Channel, reopen: false), semanticEvent);
                break;
            case IrcKickEvent kick:
                ReconcileKickEvent(workspace, kick);
                Append(workspace.EnsureChannel(kick.Channel, reopen: false), semanticEvent);
                break;
            case IrcTopicEvent topic:
                ReconcileTopicEvent(workspace, topic);
                Append(workspace.EnsureChannel(topic.Channel, reopen: false), semanticEvent);
                break;
            case IrcTopicUnsetEvent topic:
                Append(workspace.EnsureChannel(topic.Channel, reopen: false), semanticEvent);
                break;
            case IrcNamesEvent names:
                Append(workspace.EnsureChannel(names.Channel, reopen: false), semanticEvent);
                break;
            case IrcNamesCompleteEvent names:
                Append(workspace.EnsureChannel(names.Channel, reopen: false), semanticEvent);
                break;
            case IrcWhoEvent who:
                Append(workspace.EnsureChannel(who.Channel, reopen: false), semanticEvent);
                break;
            case IrcModeEvent mode:
                ReconcileModeEvent(workspace, mode);
                Append(workspace.EnsureChannel(mode.Channel, reopen: false), semanticEvent);
                break;
            case IrcBanListItemEvent banListItem:
                if (RouteBanListItemEvent(workspace, banListItem) is not null)
                {
                    break;
                }

                Append(workspace.StatusView, semanticEvent);
                break;
            case IrcBanListEndEvent banListEnd:
                if (RouteBanListEndEvent(workspace, banListEnd) is not null)
                {
                    Append(workspace.StatusView, semanticEvent);
                }

                break;
            case IrcChannelSynchronizationEvent synchronization:
                Append(workspace.EnsureChannel(synchronization.Channel, reopen: false), semanticEvent);
                break;
            case IrcListStartEvent listStartEvent:
                if (RouteListEvent(workspace, listStartEvent.RequestLabel, completes: false, starts: true) is not null)
                {
                    Append(workspace.StatusView, semanticEvent);
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
                    Append(workspace.StatusView, semanticEvent);
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
                    Append(workspace.StatusView, semanticEvent);
                }

                break;
            case IrcServerNumericEvent serverNumeric:
                ReconcileNumericOperation(workspace, serverNumeric);
                WorkspaceView targetView = serverNumeric.Interpretation.TargetChannel is { } targetChannel
                    ? workspace.EnsureChannel(targetChannel, reopen: false)
                    : workspace.StatusView;
                Append(targetView, serverNumeric);
                if (!ReferenceEquals(targetView, workspace.StatusView))
                {
                    Append(workspace.StatusView, serverNumeric);
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

                Append(workspace.StatusView, semanticEvent);
                break;
            case IrcQuitEvent quit:
                foreach (var channel in workspace.Channels)
                {
                    Append(channel, semanticEvent);
                }

                break;
            case IrcNicknameChangedEvent nickname:
                ReconcileNicknameChange(workspace, nickname);
                var followedQuery = ReconcileQueryNicknameChange(workspace, nickname, snapshot);
                foreach (var channel in workspace.Channels)
                {
                    Append(channel, semanticEvent);
                }

                if (followedQuery is not null)
                {
                    Append(
                        followedQuery,
                        semanticEvent,
                        WorkspaceActivity.None,
                        publishNotification: false,
                        updateLastActivity: false);
                }

                Append(workspace.StatusView, semanticEvent);
                break;
            case IrcNumericEvent numeric when numeric.Numeric is 332 or 331 or 324 or 353 or 366 or 367 or 368 or 375 or 372 or 376 or 422
                || WhoisResult.IsKnownWhoisNumeric(numeric.Numeric):
                break;
            default:
                Append(workspace.StatusView, semanticEvent);
                break;
        }
    }

    private bool ShouldSuppressIgnoredPresentation(NetworkWorkspace workspace, IrcSemanticEvent semanticEvent) =>
        semanticEvent switch
        {
            IrcPrivmsgEvent message => IsIgnored(workspace, message.Message.Prefix, AccountTag(message.Message)),
            IrcQueryMessageEvent query => IsIgnored(workspace, query.Message.Prefix, AccountTag(query.Message)),
            IrcCtcpEvent ctcp => IsIgnored(workspace, ctcp.Message.Prefix, AccountTag(ctcp.Message)),
            IrcJoinEvent join => IsIgnored(workspace, join.Message.Prefix, AccountTag(join.Message)),
            IrcPartEvent part => IsIgnored(workspace, part.Message.Prefix, AccountTag(part.Message)),
            IrcQuitEvent quit => IsIgnored(workspace, quit.Message.Prefix, AccountTag(quit.Message)),
            IrcNicknameChangedEvent nick => IsIgnored(workspace, nick.Message.Prefix, AccountTag(nick.Message)),
            IrcKickEvent kick => IsIgnored(workspace, kick.Message.Prefix, AccountTag(kick.Message)),
            IrcTopicEvent topic => IsIgnored(workspace, topic.Message.Prefix, AccountTag(topic.Message)),
            IrcTopicUnsetEvent topic => IsIgnored(workspace, topic.Message.Prefix, AccountTag(topic.Message)),
            IrcModeEvent mode => IsIgnored(workspace, mode.Message.Prefix, AccountTag(mode.Message)),
            _ => false
        };

    private static bool IsResynchronizationEvent(WorkspaceView view, IrcSemanticEvent semanticEvent, ServerSessionSnapshot snapshot) =>
        semanticEvent switch
        {
            IrcChannelSynchronizationEvent => true,
            IrcNamesEvent or IrcNamesCompleteEvent or IrcWhoEvent or IrcWhoEndEvent => true,
            IrcJoinEvent join => IrcIdentity.Equals(join.Nickname, snapshot.Nickname, snapshot.Features.CaseMapping),
            IrcTopicEvent topic when topic.Message.NumericCommand is 332 => true,
            IrcTopicUnsetEvent topic when topic.Message.NumericCommand is 331 => true,
            IrcModeEvent mode when mode.Message.NumericCommand is 324 => true,
            IrcModeEvent when view is ChannelView channel && channel.Synchronization != ChannelSynchronizationState.Synchronized => true,
            IrcServerNumericEvent numeric when numeric.Numeric is 332 or 331 or 324 or 353 or 366 => true,
            _ => false
        };

    private static string SemanticIdentity(IrcSemanticEvent semanticEvent)
    {
        var message = semanticEvent.Message;
        var msgid = message.TagValues.TryGetValue("msgid", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : $"delivery:{RuntimeHelpers.GetHashCode(semanticEvent):x8}";
        return $"{semanticEvent.GetType().Name}:{msgid}";
    }

    private static bool AcceptSemanticEvent(SessionEntry entry, SessionSemanticEvent item)
    {
        var message = item.Event.Message;
        if (!message.TagValues.TryGetValue("msgid", out var msgid) || string.IsNullOrWhiteSpace(msgid))
        {
            return true;
        }

        var key = $"{item.ConnectionGeneration}:{item.Event.GetType().Name}:{msgid}";
        if (!entry.RecentSemanticEventIds.Add(key))
        {
            return false;
        }

        entry.RecentSemanticEventOrder.Enqueue(key);
        while (entry.RecentSemanticEventOrder.Count > 512
            && entry.RecentSemanticEventOrder.TryDequeue(out var retired))
        {
            entry.RecentSemanticEventIds.Remove(retired);
        }

        return true;
    }

    private long NextActivitySequence() => Interlocked.Increment(ref _activitySequence);

    private static string? AccountTag(IrcMessage message) =>
        message.TagValues.TryGetValue("account", out var account) && !string.Equals(account, "*", StringComparison.Ordinal)
            ? account
            : null;

    private void AppendRendered(
        WorkspaceView view,
        IrcSemanticEvent semanticEvent,
        ServerSessionSnapshot snapshot,
        WorkspaceActivity? activity = null,
        bool publishNotification = true,
        bool updateLastActivity = true,
        DateTimeOffset? receivedAt = null)
    {
        var entry = IrcEventPresentation.Render(semanticEvent, snapshot, receivedAt);
        if (entry is null)
        {
            return;
        }

        // A transport may finish a callback while the manager is tearing down.
        // Do not let a late presentation callback resolve an already-removed
        // session or mutate recents/logs after disposal has begun.
        if (!TryGet(view.NetworkId, out var workspace) || workspace is null)
        {
            return;
        }

        var previousActivity = view.Activity;
        var isResynchronization = IsResynchronizationEvent(view, semanticEvent, snapshot);
        var isOwnMessage = semanticEvent switch
        {
            IrcPrivmsgEvent message => IrcIdentity.Equals(message.Message.Prefix?.Name ?? string.Empty, snapshot.Nickname, snapshot.Features.CaseMapping),
            IrcQueryMessageEvent query => IrcIdentity.Equals(query.Nickname, snapshot.Nickname, snapshot.Features.CaseMapping),
            IrcCtcpEvent ctcp => IrcIdentity.Equals(ctcp.Message.Prefix?.Name ?? string.Empty, snapshot.Nickname, snapshot.Features.CaseMapping),
            _ => false
        };
        var effectiveActivity = isResynchronization || isOwnMessage
            ? WorkspaceActivity.None
            : activity ?? HighlightPolicy.Classify(view, semanticEvent, snapshot);
        if (isResynchronization)
        {
            Interlocked.Increment(ref _resynchronizationEventsSuppressed);
        }
        if (semanticEvent is IrcPrivmsgEvent { IsNotice: false } channelMessage
            && view is ChannelView
            && !isOwnMessage
            && HighlightPolicy.IsHighlight(channelMessage.Text, snapshot.Nickname, snapshot.Features.CaseMapping))
        {
            entry = entry with { Metadata = "highlight" };
        }

        entry = entry with { Sequence = NextActivitySequence(), Provenance = ConversationEntryProvenance.Live };
        var inserted = entry.ServerMessageId is null
            && entry.TimestampSource == ConversationTimestampSource.LegacyOrLocalReceiveTime
            && entry.BatchId is null
            ? view.AppendConversationEntry(entry, updateLastActivity && !isResynchronization)
            : view.AppendConversationCandidate(
                ConversationEntryCandidate.FromLive(CreateConversationRecord(workspace, view, entry)),
                entry,
                updateLastActivity && !isResynchronization);
        if (!inserted)
        {
            return;
        }
        if (entry.IsHighlight && !isResynchronization)
        {
            view.MarkHighlight();
        }

        if (!isResynchronization && view is ChannelView channel && semanticEvent is IrcJoinEvent)
        {
            RecordRecent(workspace, DestinationKind.Channel, channel.Channel);
        }
        else if (!isResynchronization && view is QueryView query && view.EntryCount == 1 && semanticEvent is (IrcQueryMessageEvent or IrcCtcpEvent))
        {
            RecordRecent(workspace, DestinationKind.Query, query.Nickname);
        }

        _logging?.Record(view.NetworkId, workspace.ProfileId, view, entry);
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

        if (isResynchronization || !publishNotification)
        {
            return;
        }

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
                view is ChannelView targetChannel ? targetChannel.Channel : view is QueryView targetQuery ? targetQuery.Nickname : view.Title),
            SemanticIdentity(semanticEvent));
        if (_notificationCoalescer.ShouldPublish(notification))
        {
            Notifications.Publish(notification);
        }
    }

    private static LogMessageKind ToLogMessageKind(TranscriptEntryKind kind) => kind switch
    {
        TranscriptEntryKind.Action or TranscriptEntryKind.OutgoingAction => LogMessageKind.Action,
        TranscriptEntryKind.Notice or TranscriptEntryKind.OutgoingNotice => LogMessageKind.Notice,
        TranscriptEntryKind.Ctcp or TranscriptEntryKind.OutgoingCtcp => LogMessageKind.Ctcp,
        TranscriptEntryKind.Join => LogMessageKind.Join,
        TranscriptEntryKind.Part => LogMessageKind.Part,
        TranscriptEntryKind.Quit => LogMessageKind.Quit,
        TranscriptEntryKind.Kick => LogMessageKind.Kick,
        TranscriptEntryKind.Nick => LogMessageKind.Nick,
        TranscriptEntryKind.Topic => LogMessageKind.Topic,
        TranscriptEntryKind.Mode => LogMessageKind.Mode,
        TranscriptEntryKind.Error => LogMessageKind.Error,
        TranscriptEntryKind.System or TranscriptEntryKind.Connection or TranscriptEntryKind.Registration => LogMessageKind.System,
        _ => LogMessageKind.Message
    };

    private static ConversationLogRecord CreateConversationRecord(NetworkWorkspace workspace, WorkspaceView view, TranscriptEntry entry) =>
        new()
        {
            Timestamp = entry.Timestamp,
            NetworkId = view.NetworkId,
            ScopeId = workspace.ProfileId ?? workspace.Id,
            ProfileId = workspace.ProfileId,
            ConversationKind = view.Kind switch
            {
                WorkspaceViewKind.Channel => LogConversationKind.Channel,
                WorkspaceViewKind.Query => LogConversationKind.PrivateConversation,
                _ => LogConversationKind.Status
            },
            ConversationName = view is ChannelView channelView
                ? channelView.Channel
                : view is QueryView queryView
                    ? queryView.Nickname
                    : "status",
            ConversationKey = view is QueryView queryIdentity
                ? queryIdentity.HistoryConversationKey
                : ConversationLoggingService.BuildConversationKey(
                    view.Kind switch
                    {
                        WorkspaceViewKind.Channel => LogConversationKind.Channel,
                        WorkspaceViewKind.Query => LogConversationKind.PrivateConversation,
                        _ => LogConversationKind.Status
                    },
                    view is ChannelView channel ? channel.Channel : view is QueryView query ? query.Nickname : "status"),
            Sender = entry.Sender,
            MessageKind = ToLogMessageKind(entry.Kind),
            Direction = entry.IsOutgoing ? LogDirection.Outgoing : LogDirection.Incoming,
            Text = entry.Text,
            IsHighlight = entry.IsHighlight,
            ServerMessageId = entry.ServerMessageId,
            Provenance = entry.Provenance,
            TimestampSource = entry.TimestampSource,
            BatchId = entry.BatchId
        };

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
        IrcServerErrorEvent or IrcServerNumericEvent { IsError: true } or IrcUnknownCommandEvent or IrcUnknownNumericEvent => IrcNotificationType.Error,
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
        view.LastActivity,
        view.LastActivitySequence);

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

    private void Dispatch(Action action, WorkspaceDispatchActionCategory category = WorkspaceDispatchActionCategory.Other)
    {
        Task task;
        try
        {
            task = _dispatcher.InvokeAsync(action, category).AsTask();
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

    private static WorkspaceDispatchActionCategory DispatchCategory(IrcSemanticEvent semanticEvent) => semanticEvent switch
    {
        IrcPrivmsgEvent or IrcQueryMessageEvent or IrcCtcpEvent => WorkspaceDispatchActionCategory.IncomingMessage,
        IrcJoinEvent or IrcPartEvent or IrcQuitEvent or IrcKickEvent or IrcAwayEvent or IrcAccountEvent or IrcNamesEvent or IrcNamesCompleteEvent or IrcWhoEvent or IrcWhoEndEvent => WorkspaceDispatchActionCategory.Membership,
        IrcModeEvent or IrcTopicEvent or IrcTopicUnsetEvent or IrcTopicMetadataEvent => WorkspaceDispatchActionCategory.ModeOrTopic,
        IrcNicknameChangedEvent or IrcChannelSynchronizationEvent or IrcWelcomeEvent or IrcRegistrationStateEvent or IrcCapabilityChangedEvent or IrcSaslStateChangedEvent or IrcBatchEvent or IrcMotdEvent => WorkspaceDispatchActionCategory.Lifecycle,
        IrcWhoisEvent or IrcBanListItemEvent or IrcBanListEndEvent or IrcListStartEvent or IrcListItemEvent or IrcListEndEvent => WorkspaceDispatchActionCategory.HistoryProjection,
        IrcServerErrorEvent or IrcServerNumericEvent or IrcUnknownCommandEvent or IrcUnknownNumericEvent => WorkspaceDispatchActionCategory.Notification,
        _ => WorkspaceDispatchActionCategory.Other
    };

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
        && generation == session.Snapshot.ConnectionGeneration
        && generation >= entry.Workspace.Snapshot.ConnectionGeneration;

    private static Task[] StartEventDrainers(ServerSession session) =>
    [
        DrainAsync(session.ReadRawEventsAsync()),
        DrainAsync(session.ReadParsedEventsAsync()),
        DrainAsync(session.ReadParseErrorsAsync()),
        DrainAsync(session.ReadSemanticEventsAsync()),
        DrainAsync(session.ReadOutboundEventsAsync())
    ];

    private static async Task DrainAsync<T>(IAsyncEnumerable<T> events)
    {
        try
        {
            await foreach (var _ in events.ConfigureAwait(false))
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

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

        public Task[] EventDrainTasks { get; set; } = [];

        public HashSet<string> RecentSemanticEventIds { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<string> RecentSemanticEventOrder { get; } = new();
    }
}
