using System.Diagnostics;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1OBenchmarkTests
{
    [Phase1OPerformanceFact]
    [Trait("Category", "Phase1OPerformance")]
    public async Task DeterministicBurstMatrixReportsResponsivenessAndOrdering()
    {
        foreach (var eventCount in new[] { 100, 1_000, 5_000 })
        {
            foreach (var networkCount in new[] { 1, 2 })
            {
                foreach (var conversationCount in new[] { 1, 32 })
                {
                    var result = await RunScenarioAsync(eventCount, networkCount, conversationCount);
                    Console.WriteLine(
                        $"BURST_BENCHMARK events={result.EventCount} networks={result.NetworkCount} conversations={result.ConversationCount} "
                        + $"wall_ms={result.WallMilliseconds:F3} max_queue_depth={result.MaximumQueueDepth} "
                        + $"max_queue_wait_ms={result.MaximumQueueWaitMilliseconds:F3} p50_queue_wait_ms={result.P50QueueWaitMilliseconds:F3} p95_queue_wait_ms={result.P95QueueWaitMilliseconds:F3} "
                        + $"p99_queue_wait_ms={result.P99QueueWaitMilliseconds:F3} max_mutation_ms={result.MaximumMutationMilliseconds:F3} "
                        + $"user_selection_queue_wait_ms={result.UserSelectionQueueWaitMilliseconds:F3} ordered={result.Ordered}");
                }
            }
        }
    }

    private static async Task<BurstBenchmarkResult> RunScenarioAsync(int eventCount, int networkCount, int conversationCount)
    {
        var factory = new FakeIrcTransportFactory();
        var transports = Enumerable.Range(0, networkCount)
            .Select(index => new FakeIrcTransport(new IrcEndpoint($"burst-{index}.example", 6667, false)))
            .ToArray();
        foreach (var transport in transports)
        {
            factory.Add(transport);
        }

        await using var manager = new NetworkSessionManager(factory);
        var networks = transports
            .Select((transport, index) => manager.Add(Options($"Burst {index}", transport.Endpoint, $"burst{index}")))
            .ToArray();
        foreach (var network in networks)
        {
            await manager.ConnectAsync(network.Id);
        }

        await WaitForAsync(() => transports.All(transport => transport.ConnectCount == 1));
        foreach (var (transport, index) in transports.Select((transport, index) => (transport, index)))
        {
            Register(transport, $"burst{index}");
        }

        await WaitForAsync(() => networks.All(network => network.State == NetworkDisplayState.Registered));
        var channels = new Dictionary<(int Network, int Conversation), ChannelView>();
        foreach (var (network, networkIndex) in networks.Select((network, index) => (network, index)))
        {
            for (var conversationIndex = 0; conversationIndex < conversationCount; conversationIndex++)
            {
                channels[(networkIndex, conversationIndex)] = manager.EnsureChannel(network.Id, ChannelName(conversationIndex));
            }
        }

        manager.ActivateView(networks[0].StatusView.Id);
        var expectedCounts = new int[networkCount, conversationCount];
        var expectedLastIndexes = new int[networkCount, conversationCount];
        for (var networkIndex = 0; networkIndex < networkCount; networkIndex++)
        {
            for (var conversationIndex = 0; conversationIndex < conversationCount; conversationIndex++)
            {
                expectedLastIndexes[networkIndex, conversationIndex] = -1;
            }
        }
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < eventCount; index++)
        {
            var networkIndex = index % networkCount;
            var conversationIndex = index % conversationCount;
            expectedCounts[networkIndex, conversationIndex]++;
            expectedLastIndexes[networkIndex, conversationIndex] = index;
            transports[networkIndex].EnqueueInboundLine(
                $":BurstUser!burst@demo PRIVMSG {ChannelName(conversationIndex)} :burst-{index:0000}");
        }

        try
        {
            await WaitForAsync(() => channels.All(item =>
            {
                var entries = item.Value.EntriesSnapshot;
                var expectedLast = expectedLastIndexes[item.Key.Network, item.Key.Conversation];
                return entries.Count == Math.Min(expectedCounts[item.Key.Network, item.Key.Conversation], WorkspaceView.MaximumEntries)
                    && (expectedLast < 0 || entries.Count > 0 && entries[^1].Text == $"burst-{expectedLast:0000}");
            }), 15_000);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("Burst tail did not settle before the benchmark timeout.", exception);
        }
        await manager.FlushStateDispatchAsync().ConfigureAwait(false);
        timer.Stop();

        var ordered = channels.All(item =>
        {
            var entries = item.Value.EntriesSnapshot;
            var expectedLast = expectedLastIndexes[item.Key.Network, item.Key.Conversation];
            if (expectedLast < 0)
            {
                return entries.Count == 0;
            }

            return entries.Count <= WorkspaceView.MaximumEntries
                && entries.Count > 0
                && entries[^1].Text == $"burst-{expectedLast:0000}"
                && entries.Zip(entries.Skip(1)).All(pair => string.CompareOrdinal(pair.First.Text, pair.Second.Text) < 0);
        });

        var selectionQueueWait = await MeasureQueuedSelectionAsync(Math.Min(eventCount, 5_000)).ConfigureAwait(false);
        var diagnostics = manager.Diagnostics.StateDispatch;
        return new BurstBenchmarkResult(
            eventCount,
            networkCount,
            conversationCount,
            timer.Elapsed.TotalMilliseconds,
            diagnostics.MaximumQueueDepth,
            diagnostics.MaximumQueueWaitMilliseconds,
            diagnostics.P50QueueWaitMilliseconds,
            diagnostics.P95QueueWaitMilliseconds,
            diagnostics.P99QueueWaitMilliseconds,
            diagnostics.MaximumProcessingDurationMilliseconds,
            selectionQueueWait,
            ordered);
    }

    private static async Task<double> MeasureQueuedSelectionAsync(int incomingCount)
    {
        var dispatcher = new SerializedWorkspaceDispatcher(new YieldingWorkspaceDispatcher());
        var incoming = Enumerable.Range(0, incomingCount)
            .Select(_ => dispatcher.InvokeAsync(() => Thread.SpinWait(10_000), WorkspaceDispatchActionCategory.IncomingMessage).AsTask())
            .ToArray();
        await WaitForAsync(() => dispatcher.Diagnostics.CurrentQueueDepth > Math.Min(4, incomingCount / 2), 4_000).ConfigureAwait(false);

        var timer = Stopwatch.StartNew();
        var selection = dispatcher.InvokeAsync(static () => { }, WorkspaceDispatchActionCategory.UserSelection).AsTask();
        await selection.ConfigureAwait(false);
        timer.Stop();
        await Task.WhenAll(incoming).ConfigureAwait(false);
        await dispatcher.CompleteAsync().ConfigureAwait(false);

        var sample = dispatcher.Diagnostics.RecentSamples!
            .Last(item => item.Category == WorkspaceDispatchActionCategory.UserSelection);
        return sample.QueueWaitMilliseconds;
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1O benchmark",
        RequestedCapabilities = Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static string ChannelName(int index) => $"#burst-{index:00}";

    private static void Register(FakeIrcTransport transport, string nickname)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMilliseconds / 1000d * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The Phase 1O benchmark condition did not complete.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private sealed record BurstBenchmarkResult(
        int EventCount,
        int NetworkCount,
        int ConversationCount,
        double WallMilliseconds,
        int MaximumQueueDepth,
        double MaximumQueueWaitMilliseconds,
        double P50QueueWaitMilliseconds,
        double P95QueueWaitMilliseconds,
        double P99QueueWaitMilliseconds,
        double MaximumMutationMilliseconds,
        double UserSelectionQueueWaitMilliseconds,
        bool Ordered);

    private sealed class YieldingWorkspaceDispatcher : IWorkspaceDispatcher
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

public sealed class Phase1OPerformanceFactAttribute : FactAttribute
{
    public Phase1OPerformanceFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEXIRC_RUN_PHASE1O_PERFORMANCE"), "1", StringComparison.Ordinal))
        {
            Skip = "Opt-in deterministic Phase 1O performance matrix; set NEXIRC_RUN_PHASE1O_PERFORMANCE=1.";
        }
    }
}
