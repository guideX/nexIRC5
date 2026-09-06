using System.Diagnostics;

namespace nexIRC.Application;

public interface IWorkspaceDispatcher
{
    ValueTask InvokeAsync(Action action);

    ValueTask InvokeAsync(Action action, WorkspaceDispatchActionCategory category) => InvokeAsync(action);
}

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
                    samples);
            }
        }
    }

    public ValueTask InvokeAsync(Action action)
        => InvokeAsync(action, WorkspaceDispatchActionCategory.Other);

    public ValueTask InvokeAsync(Action action, WorkspaceDispatchActionCategory category)
    {
        ArgumentNullException.ThrowIfNull(action);

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        var queuedTimestamp = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);

            previous = _tail;
            Interlocked.Increment(ref _queuedActions);
            var depth = Interlocked.Increment(ref _queueDepth);
            UpdateMaximum(depth);
            _tail = RunAsync(previous, action, completion, category, queuedTimestamp, depth);
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

    private async Task RunAsync(
        Task previous,
        Action action,
        TaskCompletionSource completion,
        WorkspaceDispatchActionCategory category,
        long queuedTimestamp,
        int queueDepth)
    {
        var dispatchStart = 0L;
        var mutationStart = 0L;
        var mutationCompletion = 0L;
        var dispatchCompletion = 0L;
        try
        {
            await previous.ConfigureAwait(false);
            dispatchStart = Stopwatch.GetTimestamp();
            await _inner.InvokeAsync(() =>
            {
                mutationStart = Stopwatch.GetTimestamp();
                try
                {
                    action();
                }
                finally
                {
                    mutationCompletion = Stopwatch.GetTimestamp();
                }
            }, category).ConfigureAwait(false);
            dispatchCompletion = Stopwatch.GetTimestamp();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            dispatchCompletion = Stopwatch.GetTimestamp();
            completion.TrySetException(exception);
        }
        finally
        {
            var completed = dispatchCompletion == 0 ? Stopwatch.GetTimestamp() : dispatchCompletion;
            var mutationBegan = mutationStart == 0 ? dispatchStart : mutationStart;
            var mutationEnded = mutationCompletion == 0 ? completed : mutationCompletion;
            var sample = new WorkspaceDispatchSample(
                queuedTimestamp,
                dispatchStart == 0 ? queuedTimestamp : dispatchStart,
                mutationBegan,
                mutationEnded,
                completed,
                queueDepth,
                category);
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
    IReadOnlyList<WorkspaceDispatchSample>? RecentSamples = null)
{
    public double MaximumQueueWaitMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumQueueWaitTicks);

    public double MaximumProcessingDurationMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumProcessingDurationTicks);

    public double MaximumWpfScheduleWaitMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumWpfScheduleWaitTicks);

    public double MaximumTotalDurationMilliseconds => SerializedWorkspaceDispatcher.StopwatchTicksToMilliseconds(MaximumTotalDurationTicks);
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
