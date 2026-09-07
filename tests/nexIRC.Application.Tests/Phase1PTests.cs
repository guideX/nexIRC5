using System.Collections.Specialized;
using nexIRC.Application;

namespace nexIRC.Application.Tests;

public sealed class Phase1PTests
{
    [Fact]
    public async Task CooperativeSlicesKeepOneFifoAuthorityAndBoundEachPresentationCallback()
    {
        var inner = new YieldingRecordingDispatcher();
        var dispatcher = new SerializedWorkspaceDispatcher(inner);
        var order = new List<int>();
        var work = Enumerable.Range(0, 257)
            .Select(index => dispatcher.InvokeAsync(() =>
            {
                lock (order)
                {
                    order.Add(index);
                }
            }, WorkspaceDispatchActionCategory.IncomingMessage).AsTask())
            .ToArray();

        await Task.WhenAll(work);
        await dispatcher.CompleteAsync();

        Assert.Equal(Enumerable.Range(0, 257), order);
        Assert.Equal(257, dispatcher.Diagnostics.QueuedActions);
        Assert.Equal(257, dispatcher.Diagnostics.ProcessedActions);
        Assert.Equal(0, dispatcher.Diagnostics.CurrentQueueDepth);
        Assert.True(dispatcher.Diagnostics.CooperativeSliceCount > 1);
        Assert.Equal(dispatcher.Diagnostics.CooperativeSliceCount - 1, dispatcher.Diagnostics.CooperativeYieldCount);
        Assert.InRange(dispatcher.Diagnostics.MaximumCooperativeSliceWorkItems, 1, SerializedWorkspaceDispatcher.CooperativeSliceWorkItemLimit);
        Assert.Equal(dispatcher.Diagnostics.CooperativeSliceCount, inner.CallbackCount);
        Assert.Equal(257, dispatcher.Diagnostics.RecentSamples!.Count);
        Assert.All(dispatcher.Diagnostics.RecentSamples!, sample => Assert.Equal(WorkspaceDispatchActionCategory.IncomingMessage, sample.Category));
    }

    [Fact]
    public async Task FlushIsASequenceBarrierAndDoesNotWaitForLaterProtocolWork()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new GatedRecordingDispatcher(gate.Task);
        var dispatcher = new SerializedWorkspaceDispatcher(inner);
        var first = dispatcher.InvokeAsync(static () => { }).AsTask();
        var flush = dispatcher.FlushAsync().AsTask();
        var laterGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = dispatcher.InvokeAsync(() => laterGate.Task.GetAwaiter().GetResult()).AsTask();

        gate.SetResult();
        await flush;
        Assert.True(first.IsCompleted);
        Assert.False(later.IsCompleted);

        laterGate.SetResult();
        await later;
        await dispatcher.CompleteAsync();
    }

    [Fact]
    public void ProjectionBatchCoalescesOnlyBindingInvalidationsAndRetainsEveryValue()
    {
        var values = new ThreadSafeObservableCollection<int>();
        var collectionChanges = 0;
        var lastChange = NotifyCollectionChangedAction.Reset;
        values.CollectionChanged += (_, args) =>
        {
            collectionChanges++;
            lastChange = args.Action;
        };

        using (WorkspaceProjectionBatch.Begin())
        {
            values.Add(1);
            values.Add(2);
            values.Add(3);
            Assert.Empty(values.Except([1, 2, 3]));
            Assert.Equal(0, collectionChanges);
        }

        Assert.Equal([1, 2, 3], values.ToArray());
        Assert.Equal(1, collectionChanges);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastChange);

        using (WorkspaceProjectionBatch.Begin())
        {
            values.RemoveAt(0);
            values.Add(4);
            Assert.Equal([2, 3, 4], values.ToArray());
            Assert.Equal(1, collectionChanges);
        }

        Assert.Equal(2, collectionChanges);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastChange);
    }

    private sealed class YieldingRecordingDispatcher : IWorkspaceDispatcher
    {
        private int _callbackCount;

        public int CallbackCount => Volatile.Read(ref _callbackCount);

        public ValueTask InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return new ValueTask(Task.Run(async () =>
            {
                await Task.Yield();
                Interlocked.Increment(ref _callbackCount);
                action();
            }));
        }
    }

    private sealed class GatedRecordingDispatcher(Task gate) : IWorkspaceDispatcher
    {
        public ValueTask InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return new ValueTask(Task.Run(async () =>
            {
                await gate.ConfigureAwait(false);
                action();
            }));
        }
    }
}
