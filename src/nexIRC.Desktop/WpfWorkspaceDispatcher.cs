using System.Windows.Threading;
using nexIRC.Application;

namespace nexIRC.Desktop;

public sealed class WpfWorkspaceDispatcher(Dispatcher dispatcher) : IWorkspaceDispatcher, IWorkspaceDispatcherDiagnosticsProvider, IWorkspaceDispatcherBatchProvider
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private long _workItemsPosted;
    private long _workItemsExecuted;
    private long _currentPendingWorkItems;
    private long _maximumPendingWorkItems;

    public WorkspaceDispatcherBoundaryDiagnostics Diagnostics => new(
        Interlocked.Read(ref _workItemsPosted),
        Interlocked.Read(ref _workItemsExecuted),
        0,
        Interlocked.Read(ref _currentPendingWorkItems),
        Interlocked.Read(ref _maximumPendingWorkItems));

    public IDisposable BeginBatch() => WorkspaceProjectionBatch.Begin();

    public ValueTask InvokeAsync(Action action)
        => InvokeAsync(action, WorkspaceDispatchActionCategory.Other);

    public ValueTask InvokeAsync(Action action, WorkspaceDispatchActionCategory category)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            Interlocked.Increment(ref _workItemsExecuted);
            action();
            return ValueTask.CompletedTask;
        }

        var priority = category is WorkspaceDispatchActionCategory.UserSelection
            or WorkspaceDispatchActionCategory.ReadState
            or WorkspaceDispatchActionCategory.OperationFeedback
            or WorkspaceDispatchActionCategory.UiDemoCommand
            ? DispatcherPriority.Input
            : DispatcherPriority.Background;
        Interlocked.Increment(ref _workItemsPosted);
        var pending = Interlocked.Increment(ref _currentPendingWorkItems);
        UpdateMaximum(ref _maximumPendingWorkItems, pending);
        var callbackEntered = 0;
        try
        {
            var operation = _dispatcher.InvokeAsync(() =>
            {
                if (Interlocked.Exchange(ref callbackEntered, 1) == 0)
                {
                    Interlocked.Decrement(ref _currentPendingWorkItems);
                    Interlocked.Increment(ref _workItemsExecuted);
                }

                action();
            }, priority);
            return new ValueTask(ObserveAsync(operation.Task, () =>
            {
                if (Interlocked.Exchange(ref callbackEntered, 1) == 0)
                {
                    Interlocked.Decrement(ref _currentPendingWorkItems);
                }
            }));
        }
        catch
        {
            if (Interlocked.Exchange(ref callbackEntered, 1) == 0)
            {
                Interlocked.Decrement(ref _currentPendingWorkItems);
            }

            throw;
        }
    }

    private static async Task ObserveAsync(Task operation, Action onNotEntered)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        finally
        {
            onNotEntered();
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
}
