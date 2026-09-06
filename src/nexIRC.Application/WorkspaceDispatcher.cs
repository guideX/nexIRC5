namespace nexIRC.Application;

public interface IWorkspaceDispatcher
{
    ValueTask InvokeAsync(Action action);
}

public sealed class ImmediateWorkspaceDispatcher : IWorkspaceDispatcher
{
    public ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Adds one FIFO application-state mutation stream in front of the UI
/// dispatcher.  The wrapped dispatcher still owns the thread on which
/// ObservableCollection and property notifications run; this type only
/// guarantees that submitted state actions are observed in submission order.
/// </summary>
public sealed class SerializedWorkspaceDispatcher : IWorkspaceDispatcher
{
    private readonly object _gate = new();
    private readonly IWorkspaceDispatcher _inner;
    private Task _tail = Task.CompletedTask;
    private bool _accepting = true;
    private long _queuedActions;
    private long _processedActions;
    private int _queueDepth;
    private int _maximumQueueDepth;

    public SerializedWorkspaceDispatcher(IWorkspaceDispatcher inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public WorkspaceDispatchDiagnostics Diagnostics => new(
        Interlocked.Read(ref _queuedActions),
        Interlocked.Read(ref _processedActions),
        Volatile.Read(ref _queueDepth),
        Volatile.Read(ref _maximumQueueDepth));

    public ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);

            previous = _tail;
            Interlocked.Increment(ref _queuedActions);
            var depth = Interlocked.Increment(ref _queueDepth);
            UpdateMaximum(depth);
            _tail = RunAsync(previous, action, completion);
        }

        return new ValueTask(completion.Task);
    }

    /// <summary>
    /// Stops accepting new work and waits for already accepted state actions.
    /// The wrapped UI dispatcher is owned by the caller and is not disposed.
    /// </summary>
    public async ValueTask CompleteAsync()
    {
        Task tail;
        lock (_gate)
        {
            _accepting = false;
            tail = _tail;
        }

        await tail.ConfigureAwait(false);
    }

    public async ValueTask FlushAsync()
    {
        Task tail;
        lock (_gate)
        {
            tail = _tail;
        }

        await tail.ConfigureAwait(false);
    }

    private async Task RunAsync(Task previous, Action action, TaskCompletionSource completion)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await _inner.InvokeAsync(action).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            Interlocked.Increment(ref _processedActions);
            Interlocked.Decrement(ref _queueDepth);
        }
    }

    private void UpdateMaximum(int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumQueueDepth);
            if (value <= current || Interlocked.CompareExchange(ref _maximumQueueDepth, value, current) == current)
            {
                return;
            }
        }
    }
}

public sealed record WorkspaceDispatchDiagnostics(
    long QueuedActions,
    long ProcessedActions,
    int CurrentQueueDepth,
    int MaximumQueueDepth);

public sealed record NetworkSessionDiagnostics(
    WorkspaceDispatchDiagnostics StateDispatch,
    long StaleGenerationEventsDiscarded,
    long DuplicateSemanticEventsDiscarded,
    long ResynchronizationEventsSuppressed)
{
    public long QueuedStateActions => StateDispatch.QueuedActions;

    public long ProcessedStateActions => StateDispatch.ProcessedActions;

    public int CurrentQueueDepth => StateDispatch.CurrentQueueDepth;

    public int MaximumQueueDepth => StateDispatch.MaximumQueueDepth;
}
