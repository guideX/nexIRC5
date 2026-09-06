using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1QTests
{
    [Theory]
    [InlineData(2_000)]
    [InlineData(5_000)]
    public async Task ShutdownDuringBurstDrainsAcceptedAuthorityWithoutResurrection(int eventCount)
    {
        var inner = new YieldingRecordingDispatcher();
        var dispatcher = new SerializedWorkspaceDispatcher(inner);
        var order = new List<int>();
        var work = Enumerable.Range(0, eventCount)
            .Select(index => dispatcher.InvokeAsync(() =>
            {
                lock (order)
                {
                    order.Add(index);
                }
            }, WorkspaceDispatchActionCategory.IncomingMessage).AsTask())
            .ToArray();

        var completion = dispatcher.CompleteAsync().AsTask();
        await Task.WhenAll(work.Append(completion));
        await dispatcher.CompleteAsync();

        Assert.Equal(Enumerable.Range(0, eventCount), order);
        Assert.Equal(eventCount, dispatcher.Diagnostics.ProcessedActions);
        Assert.Equal(0, dispatcher.Diagnostics.CurrentQueueDepth);
        Assert.True(dispatcher.Diagnostics.CooperativeSliceCount > 1);
        Assert.Throws<ObjectDisposedException>(() => dispatcher.InvokeAsync(static () => { }).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task ShutdownProcessesAnAlreadyQueuedInteractiveActionAndIsIdempotent()
    {
        var inner = new YieldingRecordingDispatcher();
        var dispatcher = new SerializedWorkspaceDispatcher(inner);
        var order = new List<string>();
        var incoming = Enumerable.Range(0, 2_000)
            .Select(index => dispatcher.InvokeAsync(() =>
            {
                lock (order)
                {
                    order.Add($"incoming-{index}");
                }
            }, WorkspaceDispatchActionCategory.IncomingMessage).AsTask())
            .ToArray();
        var selection = dispatcher.InvokeAsync(() =>
        {
            lock (order)
            {
                order.Add("selection");
            }
        }, WorkspaceDispatchActionCategory.UserSelection).AsTask();

        await dispatcher.CompleteAsync();
        await Task.WhenAll(incoming.Append(selection));
        await dispatcher.CompleteAsync();

        Assert.Equal(2_001, order.Count);
        Assert.Equal("selection", order[^1]);
        Assert.Equal(0, dispatcher.Diagnostics.CurrentQueueDepth);
    }

    [Fact]
    public async Task ApplicationShutdownCanBeRequestedConcurrentlyWithoutDuplicateSessionDisposal()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("shutdown.example", 6667, false));
        factory.Add(transport);
        var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Shutdown",
            Endpoint = transport.Endpoint,
            Nickname = "alice",
            Username = "alice",
            RealName = "Phase 1Q shutdown test",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        });

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var first = manager.DisposeAsync().AsTask();
        var second = manager.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(ServerSessionState.Disconnected, network.Session.Snapshot.State);
        Assert.Equal(1, transport.ConnectCount);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1Q shutdown condition did not complete.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class YieldingRecordingDispatcher : IWorkspaceDispatcher
    {
        public ValueTask InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return new ValueTask(Task.Run(async () =>
            {
                await Task.Yield();
                action();
            }));
        }
    }

}
