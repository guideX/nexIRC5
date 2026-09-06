using System.Diagnostics;

namespace nexIRC.Application;

public interface IWorkspaceDispatcher
{
    ValueTask InvokeAsync(Action action);

    ValueTask InvokeAsync(Action action, WorkspaceDispatchActionCategory category) => InvokeAsync(action);
}

/// <summary>
/// Optional diagnostics exposed by a presentation dispatcher. The serialized
/// dispatcher consumes this without depending on WPF (or any other UI stack).
/// </summary>
public interface IWorkspaceDispatcherDiagnosticsProvider
{
    WorkspaceDispatcherBoundaryDiagnostics Diagnostics { get; }
}

public interface IWorkspaceDispatcherBatchProvider
{
    IDisposable BeginBatch();
}

public sealed record WorkspaceDispatcherBoundaryDiagnostics(
    long WorkItemsPosted,
    long WorkItemsExecuted,
    long WorkItemsSuperseded,
    long CurrentPendingWorkItems,
    long MaximumPendingWorkItems);

public enum WorkspaceDispatchActionCategory
{
    Other,
    IncomingMessage,
    Membership,
    ModeOrTopic,
    Lifecycle,
    ReadState,
    UserSelection,
    OperationFeedback,
    Notification,
    HistoryProjection,
    UiDemoCommand
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
    public const int CooperativeSliceWorkItemLimit = 32;

    public const double CooperativeSliceBudgetMilliseconds = 4;

    private readonly object _gate = new();
    private readonly IWorkspaceDispatcher _inner;
    private readonly IWorkspaceDispatcherDiagnosticsProvider? _boundaryDiagnostics;
    private readonly Queue<PendingDispatch> _pending = new();
    private readonly List<FlushWaiter> _flushWaiters = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _accepting = true;
    private bool _drainRunning;
    private long _nextSequence;
    private long _processedSequence;
    private long _queuedActions;
    private long _processedActions;
    private int _queueDepth;
    private int _maximumQueueDepth;
    private long _cooperativeSlices;
    private long _cooperativeYields;
    private int _maximumSliceWorkItems;
    private long _maximumSliceDurationTicks;

    public SerializedWorkspaceDispatcher(IWorkspaceDispatcher inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _boundaryDiagnostics = inner as IWorkspaceDispatcherDiagnosticsProvider;
    }

    private readonly object _sampleGate = new();
    private readonly Queue<WorkspaceDispatchSample> _samples = new();
    private long _maximumQueueWaitTicks;
    private long _maximumProcessingDurationTicks;
    private long _maximumWpfScheduleWaitTicks;
    private long _maximumTotalDurationTicks;

    public const int RecentSampleWindowSize = 2_048;

    public WorkspaceDispatchDiagnostics Diagnostics
    {
        get
        {
            lock (_sampleGate)
            {
                var samples = _samples.ToArray();
                return new WorkspaceDispatchDiagnostics(
                    Interlocked.Read(ref _queuedActions),
                    Interlocked.Read(ref _processedActions),
                    Volatile.Read(ref _queueDepth),
                    Volatile.Read(ref _maximumQueueDepth),
                    Interlocked.Read(ref _maximumQueueWaitTicks),
                    Interlocked.Read(ref _maximumProcessingDurationTicks),
                    Interlocked.Read(ref _maximumWpfScheduleWaitTicks),
                    Interlocked.Read(ref _maximumTotalDurationTicks),
                    PercentileMilliseconds(samples, static sample => sample.QueueWaitTicks, 0.50),
                    PercentileMilliseconds(samples, static sample => sample.QueueWaitTicks, 0.95),
                    PercentileMilliseconds(samples, static sample => sample.QueueWaitTicks, 0.99),
                    PercentileMilliseconds(samples, static sample => sample.MutationDurationTicks, 0.50),
                    PercentileMilliseconds(samples, static sample => sample.MutationDurationTicks, 0.95),
                    PercentileMilliseconds(samples, static sample => sample.MutationDurationTicks, 0.99),
                    PercentileMilliseconds(samples, static sample => sample.WpfScheduleWaitTicks, 0.50),
                    PercentileMilliseconds(samples, static sample => sample.WpfScheduleWaitTicks, 0.95),
                    PercentileMilliseconds(samples, static sample => sample.WpfScheduleWaitTicks, 0.99),
                    samples,
                    Interlocked.Read(ref _cooperativeSlices),
                    Interlocked.Read(ref _cooperativeYields),
                    Volatile.Read(ref _maximumSliceWorkItems),
                    StopwatchTicksToMilliseconds(Interlocked.Read(ref _maximumSliceDurationTicks)),
                    _boundaryDiagnostics?.Diagnostics);
            }
        }
    }

    public ValueTask InvokeAsync(Action action)
        => InvokeAsync(action, WorkspaceDispatchActionCategory.Other);

    public ValueTask InvokeAsync(Action action, WorkspaceDispatchActionCategory category)
    {
        ArgumentNullException.ThrowIfNull(action);

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedTimestamp = Stopwatch.GetTimestamp();
        var startDrain = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);

            Interlocked.Increment(ref _queuedActions);
            var depth = Interlocked.Increment(ref _queueDepth);
            UpdateMaximum(depth);
            _pending.Enqueue(new PendingDispatch(
                Interlocked.Increment(ref _nextSequence),
                action,
                completion,
                category,
                queuedTimestamp,
                depth));
            if (!_drainRunning)
            {
                _drainRunning = true;
                startDrain = true;
            }
        }

        if (startDrain)
        {
            _ = DrainAsync();
        }

        return new ValueTask(completion.Task);
    }

    /// <summary>
    /// Stops accepting new work and waits for every already accepted state
    /// action. Completion is the presentation shutdown fence: after it
    /// finishes, no queued callback can execute through this dispatcher.
    /// The wrapped UI dispatcher is owned by the caller and is not disposed.
    /// </summary>
    public async ValueTask CompleteAsync()
    {
        Task drained;
        lock (_gate)
        {
            _accepting = false;
            if (!_drainRunning && _pending.Count == 0)
            {
                _drained.TrySetResult();
            }

            drained = _drained.Task;
        }

        await drained.ConfigureAwait(false);
    }

    public async ValueTask FlushAsync()
    {
        Task flushed;
        lock (_gate)
        {
            var targetSequence = _nextSequence;
            if (_processedSequence >= targetSequence)
            {
                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushWaiters.Add(new FlushWaiter(targetSequence, completion));
            flushed = completion.Task;
        }

        await flushed.ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                PendingDispatch[] batch;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        _drainRunning = false;
                        if (!_accepting)
                        {
                            _drained.TrySetResult();
                        }

                        return;
                    }

                    var count = Math.Min(CooperativeSliceWorkItemLimit, _pending.Count);
                    batch = new PendingDispatch[count];
                    for (var index = 0; index < count; index++)
                    {
                        batch[index] = _pending.Dequeue();
                    }
                }

                var sliceStart = Stopwatch.GetTimestamp();
                var callbackStart = 0L;
                var callbackCompletion = 0L;
                var executed = 0;
                try
                {
                    await _inner.InvokeAsync(() =>
                    {
                        callbackStart = Stopwatch.GetTimestamp();
                        var deadline = callbackStart + (long)(CooperativeSliceBudgetMilliseconds / 1000d * Stopwatch.Frequency);
                        var batchScope = (_inner as IWorkspaceDispatcherBatchProvider)?.BeginBatch();
                        try
                        {
                            for (var index = 0; index < batch.Length; index++)
                            {
                                if (index > 0 && Stopwatch.GetTimestamp() >= deadline)
                                {
                                    break;
                                }

                                ExecutePending(batch[index], callbackStart);
                                executed++;
                            }
                        }
                        finally
                        {
                            batchScope?.Dispose();
                        }

                        callbackCompletion = Stopwatch.GetTimestamp();
                    }, batch[0].Category).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var failedAt = Stopwatch.GetTimestamp();
                    foreach (var pending in batch.Skip(executed))
                    {
                        CompletePendingWithoutExecution(pending, exception, callbackStart == 0 ? sliceStart : callbackStart, failedAt);
                    }
                }

                if (executed < batch.Length)
                {
                    lock (_gate)
                    {
                        foreach (var pending in batch.Skip(executed).Reverse())
                        {
                            var remaining = _pending.ToArray();
                            _pending.Clear();
                            _pending.Enqueue(pending);
                            foreach (var item in remaining)
                            {
                                _pending.Enqueue(item);
                            }
                        }
                    }
                }

                var sliceCompletion = callbackCompletion == 0 ? Stopwatch.GetTimestamp() : callbackCompletion;
                Interlocked.Increment(ref _cooperativeSlices);
                UpdateMaximum(ref _maximumSliceWorkItems, executed);
                UpdateMaximum(ref _maximumSliceDurationTicks, Math.Max(0, sliceCompletion - (callbackStart == 0 ? sliceStart : callbackStart)));
                var hasMoreWork = false;
                lock (_gate)
                {
                    hasMoreWork = _pending.Count > 0;
                }

                if (hasMoreWork)
                {
                    Interlocked.Increment(ref _cooperativeYields);
                    // Give the UI dispatcher a new scheduling opportunity after
                    // every bounded slice. On WPF the next callback is posted
                    // at the category's priority, allowing Input work between
                    // protocol-derived Background slices.
                    await Task.Yield();
                }
            }
        }
        catch (Exception exception)
        {
            PendingDispatch[] abandoned;
            lock (_gate)
            {
                abandoned = _pending.ToArray();
                _pending.Clear();
                _drainRunning = false;
                if (!_accepting)
                {
                    _drained.TrySetResult();
                }
            }

            var failedAt = Stopwatch.GetTimestamp();
            foreach (var pending in abandoned)
            {
                CompletePendingWithoutExecution(pending, exception, failedAt, failedAt);
            }
        }
    }

    private void ExecutePending(PendingDispatch pending, long dispatchStart)
    {
        var mutationStart = Stopwatch.GetTimestamp();
        var mutationCompletion = mutationStart;
        try
        {
            pending.Action();
            pending.Completion.TrySetResult();
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
        }
        finally
        {
            mutationCompletion = Stopwatch.GetTimestamp();
            RecordSample(pending, dispatchStart, mutationStart, mutationCompletion, mutationCompletion);
        }
    }

    private void CompletePendingWithoutExecution(PendingDispatch pending, Exception exception, long dispatchStart, long completion)
    {
        pending.Completion.TrySetException(exception);
        RecordSample(pending, dispatchStart, dispatchStart, completion, completion);
    }

    private void RecordSample(PendingDispatch pending, long dispatchStart, long mutationStart, long mutationCompletion, long dispatchCompletion)
    {
        var sample = new WorkspaceDispatchSample(
            pending.QueuedTimestamp,
            dispatchStart == 0 ? pending.QueuedTimestamp : dispatchStart,
            mutationStart,
            mutationCompletion,
            dispatchCompletion,
            pending.QueueDepthOnEnqueue,
            pending.Category);
        UpdateMaximum(ref _maximumQueueWaitTicks, sample.QueueWaitTicks);
        UpdateMaximum(ref _maximumProcessingDurationTicks, sample.MutationDurationTicks);
        UpdateMaximum(ref _maximumWpfScheduleWaitTicks, sample.WpfScheduleWaitTicks);
        UpdateMaximum(ref _maximumTotalDurationTicks, sample.TotalDurationTicks);
        lock (_sampleGate)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > RecentSampleWindowSize)
            {
                _samples.Dequeue();
            }
        }

        Interlocked.Increment(ref _processedActions);
        Interlocked.Decrement(ref _queueDepth);
        lock (_gate)
        {
            _processedSequence = Math.Max(_processedSequence, pending.Sequence);
            for (var index = _flushWaiters.Count - 1; index >= 0; index--)
            {
                if (_flushWaiters[index].TargetSequence <= _processedSequence)
                {
                    _flushWaiters[index].Completion.TrySetResult();
                    _flushWaiters.RemoveAt(index);
                }
            }
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

    private static void UpdateMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private static double PercentileMilliseconds(
        WorkspaceDispatchSample[] samples,
        Func<WorkspaceDispatchSample, long> selector,
        double percentile)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var values = samples.Select(selector).OrderBy(static value => value).ToArray();
        var index = (int)Math.Ceiling(values.Length * percentile) - 1;
        return StopwatchTicksToMilliseconds(values[Math.Clamp(index, 0, values.Length - 1)]);
    }

    internal static double StopwatchTicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private sealed record PendingDispatch(
        long Sequence,
        Action Action,
        TaskCompletionSource Completion,
        WorkspaceDispatchActionCategory Category,
        long QueuedTimestamp,
        int QueueDepthOnEnqueue);

    private sealed record FlushWaiter(long TargetSequence, TaskCompletionSource Completion);
}

public sealed record WorkspaceDispatchSample(
    long QueuedTimestamp,
    long DispatchStartTimestamp,
    long MutationStartTimestamp,
    long MutationCompletionTimestamp,
    long DispatchCompletionTimestamp,
    int QueueDepthOnEnqueue,
    WorkspaceDispatchActionCategory Category)
{
    public long QueueWaitTicks => Math.Max(0, DispatchStartTimestamp - QueuedTimestamp);

    public long WpfScheduleWaitTicks => Math.Max(0, MutationStartTimestamp - DispatchStartTimestamp);

    public long MutationDurationTicks => Math.Max(0, MutationCompletionTimestamp - MutationStartTimestamp);

    public long TotalDurationTicks => Math.Max(0, DispatchCompletionTimestamp - QueuedTimestamp);

    public double QueueWaitMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(QueueWaitTicks);

    public double WpfScheduleWaitMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(WpfScheduleWaitTicks);

    public double MutationDurationMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MutationDurationTicks);

    public double TotalDurationMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(TotalDurationTicks);
}

public sealed record WorkspaceDispatchDiagnostics(
    long QueuedActions,
    long ProcessedActions,
    int CurrentQueueDepth,
    int MaximumQueueDepth,
    long MaximumQueueWaitTicks = 0,
    long MaximumProcessingDurationTicks = 0,
    long MaximumWpfScheduleWaitTicks = 0,
    long MaximumTotalDurationTicks = 0,
    double P50QueueWaitMilliseconds = 0,
    double P95QueueWaitMilliseconds = 0,
    double P99QueueWaitMilliseconds = 0,
    double P50MutationDurationMilliseconds = 0,
    double P95MutationDurationMilliseconds = 0,
    double P99MutationDurationMilliseconds = 0,
    double P50WpfScheduleWaitMilliseconds = 0,
    double P95WpfScheduleWaitMilliseconds = 0,
    double P99WpfScheduleWaitMilliseconds = 0,
    IReadOnlyList<WorkspaceDispatchSample>? RecentSamples = null,
    long CooperativeSliceCount = 0,
    long CooperativeYieldCount = 0,
    int MaximumCooperativeSliceWorkItems = 0,
    double MaximumCooperativeSliceDurationMilliseconds = 0,
    WorkspaceDispatcherBoundaryDiagnostics? BoundaryDiagnostics = null)
{
    public double MaximumQueueWaitMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumQueueWaitTicks);

    public double MaximumProcessingDurationMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumProcessingDurationTicks);

    public double MaximumWpfScheduleWaitMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumWpfScheduleWaitTicks);

    public double MaximumTotalDurationMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumTotalDurationTicks);

    public long WpfWorkItemsPosted => BoundaryDiagnostics?.WorkItemsPosted ?? 0;

    public long WpfWorkItemsExecuted => BoundaryDiagnostics?.WorkItemsExecuted ?? 0;

    public long MaximumWpfPendingWorkItems => BoundaryDiagnostics?.MaximumPendingWorkItems ?? 0;
}

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

    public double P50QueueWaitMilliseconds => StateDispatch.P50QueueWaitMilliseconds;

    public double P95QueueWaitMilliseconds => StateDispatch.P95QueueWaitMilliseconds;

    public double P99QueueWaitMilliseconds => StateDispatch.P99QueueWaitMilliseconds;

    public double MaximumQueueWaitMilliseconds => StateDispatch.MaximumQueueWaitMilliseconds;

    public double MaximumMutationDurationMilliseconds => StateDispatch.MaximumProcessingDurationMilliseconds;

    public double P50WpfScheduleWaitMilliseconds => StateDispatch.P50WpfScheduleWaitMilliseconds;

    public double P95WpfScheduleWaitMilliseconds => StateDispatch.P95WpfScheduleWaitMilliseconds;

    public double P99WpfScheduleWaitMilliseconds => StateDispatch.P99WpfScheduleWaitMilliseconds;
}
