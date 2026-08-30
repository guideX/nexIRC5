using System.Collections.ObjectModel;
using nexIRC.Core.Session;

namespace nexIRC.Application;

public sealed class NetworkSessionManager : IAsyncDisposable
{
    private readonly nexIRC.Core.Networking.IIrcTransportFactory _transportFactory;
    private readonly IWorkspaceDispatcher _dispatcher;
    private readonly object _entriesGate = new();
    private readonly Dictionary<Guid, SessionEntry> _entries = [];
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pendingDispatches = [];
    private bool _disposed;

    public NetworkSessionManager(
        nexIRC.Core.Networking.IIrcTransportFactory transportFactory,
        IWorkspaceDispatcher? dispatcher = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _dispatcher = dispatcher ?? new ImmediateWorkspaceDispatcher();
    }

    public ObservableCollection<NetworkWorkspace> Networks { get; } = [];

    public NetworkWorkspace? ActiveNetwork { get; private set; }

    public WorkspaceView? ActiveView { get; private set; }

    public event EventHandler<WorkspaceActivityEventArgs>? ActivityRaised;

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

    public void ActivateView(Guid viewId)
    {
        if (!TryGetView(viewId, out var workspace, out var view) || workspace is null || view is null)
        {
            return;
        }

        workspace.Activate(view);
        ActiveNetwork = workspace;
        ActiveView = view;
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
        var desiredChannels = entry.Session.Snapshot.DesiredChannels.ToHashSet(StringComparer.Ordinal);
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

        await StopEntryAsync(entry, "nexIRC network removed").ConfigureAwait(false);
        if (entry.Options.SaslCredentialProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
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
            if (entry.Options.SaslCredentialProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        Task[] pending;
        lock (_pendingGate)
        {
            pending = _pendingDispatches.ToArray();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    internal void AppendLocal(WorkspaceView view, TranscriptEntry entry) => view.Append(entry, markActivity: false);

    private void Attach(SessionEntry entry)
    {
        entry.Session.StateChanged += OnSessionStateChanged;
        entry.Session.SemanticEventReceived += OnSessionSemanticEvent;
        entry.Workspace.ApplySnapshot(entry.Session.Snapshot);
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
            var kind = change.Current == ServerSessionState.Failed ? TranscriptEntryKind.Error : TranscriptEntryKind.Connection;
            var text = $"{change.Previous} → {change.Current}";
            if (snapshot.LastFailure is not null)
            {
                text += $": {snapshot.LastFailure.Message}";
            }

            entry.Workspace.StatusView.Append(new TranscriptEntry(DateTimeOffset.Now, kind, null, text));
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
        switch (semanticEvent)
        {
            case IrcPrivmsgEvent message when snapshot.Features.ChannelTypes.Contains(message.Target.FirstOrDefault()):
                AppendRendered(workspace.EnsureChannel(message.Target), semanticEvent, snapshot);
                break;
            case IrcQueryMessageEvent query:
                AppendRendered(workspace.EnsureQuery(query.Nickname), semanticEvent, snapshot, WorkspaceActivity.Important);
                break;
            case IrcJoinEvent join:
                AppendRendered(workspace.EnsureChannel(join.Channel), semanticEvent, snapshot);
                break;
            case IrcPartEvent part:
                AppendRendered(workspace.EnsureChannel(part.Channel), semanticEvent, snapshot);
                break;
            case IrcKickEvent kick:
                AppendRendered(workspace.EnsureChannel(kick.Channel), semanticEvent, snapshot);
                break;
            case IrcTopicEvent topic:
                AppendRendered(workspace.EnsureChannel(topic.Channel), semanticEvent, snapshot);
                break;
            case IrcTopicUnsetEvent topic:
                AppendRendered(workspace.EnsureChannel(topic.Channel), semanticEvent, snapshot);
                break;
            case IrcNamesEvent names:
                AppendRendered(workspace.EnsureChannel(names.Channel), semanticEvent, snapshot);
                break;
            case IrcNamesCompleteEvent names:
                AppendRendered(workspace.EnsureChannel(names.Channel), semanticEvent, snapshot);
                break;
            case IrcWhoEvent who:
                AppendRendered(workspace.EnsureChannel(who.Channel), semanticEvent, snapshot);
                break;
            case IrcModeEvent mode:
                AppendRendered(workspace.EnsureChannel(mode.Channel), semanticEvent, snapshot);
                break;
            case IrcChannelSynchronizationEvent synchronization:
                AppendRendered(workspace.EnsureChannel(synchronization.Channel), semanticEvent, snapshot);
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
            case IrcNumericEvent numeric when numeric.Numeric is 332 or 331 or 324 or 353 or 366 or 375 or 372 or 376 or 422:
                break;
            default:
                AppendRendered(workspace.StatusView, semanticEvent, snapshot);
                break;
        }
    }

    private void AppendRendered(WorkspaceView view, IrcSemanticEvent semanticEvent, ServerSessionSnapshot snapshot, WorkspaceActivity? activity = null)
    {
        var entry = IrcEventPresentation.Render(semanticEvent, snapshot);
        if (entry is null)
        {
            return;
        }

        var previousActivity = view.Activity;
        view.Append(entry);
        if (activity.HasValue && !view.IsActive)
        {
            view.MarkActivity(activity.Value);
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
    }

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
