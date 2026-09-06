using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1RTests
{
    [Fact]
    public async Task ProductionShapedLongSessionStabilizesResourcesAndConvergesAfterReconnectChurn()
    {
        const int networkCount = 3;
        const int channelCount = 8;
        const int initialTrafficPerNetwork = 3_600;
        const int reconnectTrafficPerNetwork = 800;
        const int reconnectCyclesPerNetwork = 2;

        var root = Directory.CreateTempSubdirectory("nexirc5-phase1r-");
        NetworkSessionManager? manager = null;
        try
        {
            var factory = new FakeIrcTransportFactory();
            var plans = new List<SessionPlan>(networkCount);
            for (var networkIndex = 0; networkIndex < networkCount; networkIndex++)
            {
                var initial = new FakeIrcTransport(new IrcEndpoint($"phase1r-{networkIndex}.example", 6667, false));
                var replacements = Enumerable.Range(1, reconnectCyclesPerNetwork)
                    .Select(_ => new FakeIrcTransport(initial.Endpoint))
                    .ToArray();
                factory.Add(initial);
                plans.Add(new SessionPlan(networkIndex, initial, replacements, BuildChannels(networkIndex, channelCount)));
            }

            foreach (var plan in plans)
            {
                foreach (var replacement in plan.Replacements)
                {
                    factory.Add(replacement);
                }
            }

            var configuration = new ConfigurationService(new InMemoryConfigurationStore(new NexIrcConfiguration
            {
                Preferences = new ApplicationPreferences
                {
                    ConversationLoggingEnabled = true,
                    PrivateMessageLoggingEnabled = true,
                    StatusLoggingEnabled = true,
                    NotificationsEnabled = false
                }
            }));
            await configuration.LoadAsync();
            var logStore = new JsonlConversationLogStore(root.FullName, maximumSegmentBytes: 64 * 1024);
            var boundary = new CountingWorkspaceDispatcher();
            manager = new NetworkSessionManager(
                factory,
                boundary,
                configuration: configuration,
                logStore: logStore);

            var totalInboundLines = 0;
            var totalReconnectCycles = 0;
            var baselineActiveTransportCount = 0;
            var postReconnectActiveTransportCount = 0;
            foreach (var plan in plans)
            {
                plan.Network = manager.Add(CreateOptions(plan));
                manager.AddFavorite(plan.Network.Id, DestinationKind.Channel, plan.Channels[0], $"{plan.Network.DisplayName} home");
                manager.AddFavorite(plan.Network.Id, DestinationKind.Channel, plan.Channels[1], $"{plan.Network.DisplayName} work");
                manager.RecordRecent(plan.Network, DestinationKind.Channel, plan.Channels[0]);
                manager.RecordRecent(plan.Network, DestinationKind.Query, $"peer{plan.NetworkIndex}-0");
            }

            foreach (var plan in plans)
            {
                await manager.ConnectAsync(plan.Network.Id);
            }

            foreach (var plan in plans)
            {
                await WaitForAsync(() => plan.Initial.ConnectCount == 1, "initial transport did not connect");
                totalInboundLines += EnqueueRegistration(plan.Initial, plan.Network.Snapshot.Nickname, $"srv-{plan.NetworkIndex}");
                await WaitForAsync(() => plan.Network.State == NetworkDisplayState.Registered, "initial registration did not complete");

                var whois = await manager.RequestWhoisAsync(plan.Network.Id, $"peer{plan.NetworkIndex}-0");
                var whoisView = Assert.IsType<WhoisView>(whois.View);
                totalInboundLines += EnqueueWhois(plan.Initial, plan.Network.Snapshot.Nickname, $"peer{plan.NetworkIndex}-0");
                await WaitForAsync(() => whoisView.IsCompleted, "WHOIS did not complete");
                manager.CloseView(whoisView.Id);

                totalInboundLines += EnqueueConnectionWorkload(
                    plan,
                    plan.Initial,
                    generation: 0,
                    initialTrafficPerNetwork);
                await WaitForPlanConvergenceAsync(plan, "initial workload did not converge");
                await manager.FlushStateDispatchAsync();
            }

            baselineActiveTransportCount = plans
                .Select(plan => plan.Initial)
                .Count(transport => transport.IsConnected);
            Assert.Equal(networkCount, baselineActiveTransportCount);
            Assert.All(plans.Select(plan => plan.Initial), transport =>
            {
                Assert.Equal(1, transport.CallbackSubscriptionCount);
                Assert.Equal(1, transport.ActiveReadCount);
            });

            foreach (var plan in plans)
            {
                for (var cycle = 0; cycle < reconnectCyclesPerNetwork; cycle++)
                {
                    var oldTransport = cycle == 0 ? plan.Initial : plan.Replacements[cycle - 1];
                    var replacement = plan.Replacements[cycle];
                    await manager.ReconnectAsync(plan.Network.Id);
                    totalReconnectCycles++;
                    await WaitForAsync(() => replacement.ConnectCount == 1, "replacement transport did not connect");
                    await WaitForAsync(() => oldTransport.CallbackSubscriptionCount == 0 && oldTransport.ActiveReadCount == 0, "old transport remained active after reconnect");
                    totalInboundLines += EnqueueRegistration(replacement, plan.Network.Snapshot.Nickname, $"srv-{plan.NetworkIndex}-r{cycle + 1}");
                    await WaitForAsync(() => plan.Network.State == NetworkDisplayState.Registered, "replacement registration did not complete");
                    totalInboundLines += EnqueueConnectionWorkload(
                        plan,
                        replacement,
                        generation: cycle + 1,
                        reconnectTrafficPerNetwork);
                    await WaitForPlanConvergenceAsync(plan, $"reconnect workload {cycle + 1} did not converge");
                    await manager.FlushStateDispatchAsync();
                }
            }

            postReconnectActiveTransportCount = plans
                .Select(plan => plan.Replacements[^1])
                .Count(transport => transport.IsConnected);
            Assert.Equal(networkCount, postReconnectActiveTransportCount);
            Assert.All(plans.Select(plan => plan.Replacements[^1]), transport =>
            {
                Assert.Equal(1, transport.CallbackSubscriptionCount);
                Assert.Equal(1, transport.ActiveReadCount);
            });

            foreach (var plan in plans)
            {
                var closedQuery = plan.Network.Queries.FirstOrDefault();
                if (closedQuery is not null)
                {
                    Assert.True(manager.CloseView(closedQuery.Id));
                    Assert.True(manager.ReopenView(closedQuery.Id));
                    Assert.True(closedQuery.IsViewOpen);
                }

                var historical = manager.OpenHistoricalConversation(
                    plan.Network.Id,
                    DestinationKind.Channel,
                    $"#historical-{plan.NetworkIndex}");
                Assert.Equal(ConversationLifecycleState.HistoricalOnly, historical.LifecycleState);
                Assert.True(manager.RemoveHistoricalConversation(historical.Id));

                Assert.NotEmpty(plan.Network.Channels);
                Assert.Contains(plan.Network.Channels, channel => channel.Activity != WorkspaceActivity.None);
                manager.ActivateView(plan.Network.Channels[0].Id);
                Assert.Equal(WorkspaceActivity.None, plan.Network.Channels[0].Activity);

                foreach (var view in plan.Network.Views.Take(24).ToArray())
                {
                    manager.ActivateView(view.Id);
                }

                Assert.InRange(
                    manager.NavigationHistory.Count,
                    0,
                    ConfigurationLimits.MaximumConversationNavigationHistory);
            }

            await logStore.FlushAsync();
            var historyFileCountAfterFlush = Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories).Count();
            foreach (var plan in plans)
            {
                var channel = plan.Network.Channels[0];
                var page = await logStore.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = plan.Network.Id,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = channel.Channel,
                    PageSize = 50
                });
                Assert.NotEmpty(page.Records);

                var search = await logStore.SearchDetailedAsync(new ConversationLogQuery
                {
                    Scope = ConversationLogSearchScope.CurrentNetwork,
                    NetworkId = plan.Network.Id,
                    Text = "phase1r-",
                    MaximumResults = 25
                });
                Assert.NotEmpty(search.Results);
                Assert.True(search.Statistics.MatchingRecords >= search.Results.Count);
            }

            var historyFileCountAfterFirstSearch = Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories).Count();
            for (var repeat = 0; repeat < 3; repeat++)
            {
                foreach (var plan in plans)
                {
                    _ = await logStore.SearchDetailedAsync(new ConversationLogQuery
                    {
                        Scope = ConversationLogSearchScope.CurrentNetwork,
                        NetworkId = plan.Network.Id,
                        Text = "phase1r-",
                        MaximumResults = 10
                    });
                }
            }

            Assert.Equal(historyFileCountAfterFirstSearch, Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories).Count());
            Assert.True(historyFileCountAfterFirstSearch >= historyFileCountAfterFlush);

            var readdedTransport = new FakeIrcTransport(new IrcEndpoint("phase1r-readded.example", 6667, false));
            factory.Add(readdedTransport);
            var removedPlan = plans[^1];
            await manager.RemoveAsync(removedPlan.Network.Id);
            Assert.DoesNotContain(manager.Networks, network => network.Id == removedPlan.Network.Id);
            Assert.Equal(0, removedPlan.Replacements[^1].CallbackSubscriptionCount);
            Assert.Equal(0, removedPlan.Replacements[^1].ActiveReadCount);

            var readdedPlan = new SessionPlan(
                networkIndex: networkCount,
                readdedTransport,
                Array.Empty<FakeIrcTransport>(),
                BuildChannels(networkCount, 2));
            readdedPlan.Network = manager.Add(CreateOptions(readdedPlan));
            await manager.ConnectAsync(readdedPlan.Network.Id);
            await WaitForAsync(() => readdedTransport.ConnectCount == 1, "re-added transport did not connect");
            totalInboundLines += EnqueueRegistration(readdedTransport, readdedPlan.Network.Snapshot.Nickname, "srv-readded");
            await WaitForAsync(() => readdedPlan.Network.State == NetworkDisplayState.Registered, "re-added network did not register");
            totalInboundLines += EnqueueConnectionWorkload(readdedPlan, readdedTransport, 0, 120);
            await WaitForPlanConvergenceAsync(readdedPlan, "re-added network did not converge");
            await manager.FlushStateDispatchAsync();
            await manager.RemoveAsync(readdedPlan.Network.Id);

            var diagnosticsBeforeShutdown = manager.Diagnostics;
            Assert.Equal(0, diagnosticsBeforeShutdown.CurrentQueueDepth);
            Assert.True(diagnosticsBeforeShutdown.StateDispatch.CooperativeSliceCount > 1);
            Assert.Equal(1, diagnosticsBeforeShutdown.StateDispatch.MaximumWpfPendingWorkItems);
            Assert.True(diagnosticsBeforeShutdown.StaleGenerationEventsDiscarded > 0);
            Assert.Equal(0, diagnosticsBeforeShutdown.DuplicateSemanticEventsDiscarded);
            Assert.All(
                plans.SelectMany(plan => plan.AllTransports)
                    .Except(plans.Take(networkCount - 1).Select(plan => plan.Replacements[^1])),
                transport => Assert.Equal(0, transport.CallbackSubscriptionCount));

            var stableActiveTransports = plans
                .Select(plan => plan.Replacements[^1])
                .Count(transport => transport.IsConnected);
            Assert.Equal(networkCount - 1, stableActiveTransports);
            Assert.Equal(networkCount - 1, manager.Networks.Count);

            await manager.DisposeAsync();
            await manager.DisposeAsync();

            var diagnosticsAfterShutdown = manager.Diagnostics;
            Assert.Equal(0, diagnosticsAfterShutdown.CurrentQueueDepth);
            Assert.All(plans.SelectMany(plan => plan.AllTransports), transport =>
            {
                Assert.True(transport.IsDisposed);
                Assert.Equal(0, transport.CallbackSubscriptionCount);
                Assert.Equal(0, transport.ActiveReadCount);
                Assert.InRange(transport.MaximumActiveReadCount, 0, 1);
            });
            Assert.True(readdedTransport.IsDisposed);
            Assert.Equal(0, readdedTransport.CallbackSubscriptionCount);
            Assert.Equal(0, readdedTransport.ActiveReadCount);
            Assert.Equal(networkCount * (1 + reconnectCyclesPerNetwork) + 1, factory.CreatedTransportCount);

            var boundaryDiagnostics = diagnosticsAfterShutdown.StateDispatch.BoundaryDiagnostics;
            Assert.NotNull(boundaryDiagnostics);
            Console.WriteLine(
                $"PHASE1R_LONG_SESSION events={totalInboundLines} networks={networkCount + 1} conversations={plans.Sum(plan => plan.Network.Views.Count)} members={plans.Sum(plan => plan.Network.Channels.Sum(channel => channel.MembersSnapshot.Count))} " +
                $"reconnects={totalReconnectCycles} transports={factory.CreatedTransportCount} queueMax={diagnosticsAfterShutdown.MaximumQueueDepth} " +
                $"callbacksMax={boundaryDiagnostics!.MaximumPendingWorkItems} slices={diagnosticsAfterShutdown.StateDispatch.CooperativeSliceCount} yields={diagnosticsAfterShutdown.StateDispatch.CooperativeYieldCount} " +
                $"p50={diagnosticsAfterShutdown.P50QueueWaitMilliseconds:F3} p95={diagnosticsAfterShutdown.P95QueueWaitMilliseconds:F3} p99={diagnosticsAfterShutdown.P99QueueWaitMilliseconds:F3} " +
                $"wpfP50={diagnosticsAfterShutdown.P50WpfScheduleWaitMilliseconds:F3} wpfP95={diagnosticsAfterShutdown.P95WpfScheduleWaitMilliseconds:F3} wpfP99={diagnosticsAfterShutdown.P99WpfScheduleWaitMilliseconds:F3} " +
                $"historyFilesAfterFlush={historyFileCountAfterFlush} historyFiles={historyFileCountAfterFirstSearch} baselineActive={baselineActiveTransportCount} postReconnectActive={postReconnectActiveTransportCount} finalActive={stableActiveTransports} " +
                $"staleGenerationDiscards={diagnosticsAfterShutdown.StaleGenerationEventsDiscarded}");
        }
        finally
        {
            if (manager is not null)
            {
                await manager.DisposeAsync();
            }

            if (Directory.Exists(root.FullName))
            {
                Directory.Delete(root.FullName, recursive: true);
            }
        }
    }

    private static NetworkConnectionOptions CreateOptions(SessionPlan plan) => new()
    {
        DisplayName = $"Phase 1R Net {plan.NetworkIndex}",
        Endpoint = plan.Initial.Endpoint,
        Nickname = $"nex{plan.NetworkIndex}",
        Username = $"nex{plan.NetworkIndex}",
        RealName = "Phase 1R production-shaped soak",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = plan.Channels.ToHashSet(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))
    };

    private static string[] BuildChannels(int networkIndex, int count) =>
        Enumerable.Range(0, count).Select(index => $"#phase1r-{networkIndex}-{index:00}").ToArray();

    private static int EnqueueRegistration(FakeIrcTransport transport, string nickname, string server)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :message-tags server-time multi-prefix");
        transport.EnqueueInboundLine($":{server} 005 {nickname} PREFIX=(ov)@+ NETWORK=Phase1R CHANTYPES=#& CASEMAPPING=rfc1459");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to the Phase 1R deterministic network");
        return 3;
    }

    private static int EnqueueWhois(FakeIrcTransport transport, string nickname, string target)
    {
        transport.EnqueueInboundLine($":srv 311 {nickname} {target} ident host.example * :Phase 1R test user");
        transport.EnqueueInboundLine($":srv 318 {nickname} {target} :End of WHOIS list");
        return 2;
    }

    private static int EnqueueConnectionWorkload(SessionPlan plan, FakeIrcTransport transport, int generation, int trafficCount)
    {
        var count = 0;
        var nickname = plan.Network.Snapshot.Nickname;
        foreach (var channel in plan.Channels)
        {
            transport.EnqueueInboundLine($":{nickname}!local@{plan.NetworkIndex}.example JOIN {channel}");
            count++;
            var names = Enumerable.Range(0, 48)
                .Select(index => index == 0 ? $"@{nickname}" : $"user{plan.NetworkIndex}-{index:00}")
                .ToArray();
            foreach (var chunk in names.Chunk(16))
            {
                transport.EnqueueInboundLine($":srv 353 {nickname} = {channel} :{string.Join(' ', chunk)}");
                count++;
            }

            transport.EnqueueInboundLine($":srv 366 {nickname} {channel} :End of NAMES list");
            transport.EnqueueInboundLine($":srv 332 {nickname} {channel} :Phase 1R topic {generation}");
            transport.EnqueueInboundLine($":srv 324 {nickname} {channel} +nt");
            transport.EnqueueInboundLine($":srv 352 {nickname} {channel} user host srv user0 H :0 Phase 1R user");
            transport.EnqueueInboundLine($":srv 315 {nickname} {channel} :End of WHO list");
            count += 5;
        }

        transport.EnqueueInboundLine($":user{plan.NetworkIndex}-01!u@host NICK :user{plan.NetworkIndex}-renamed");
        count++;
        for (var index = 0; index < 12; index++)
        {
            var channel = plan.Channels[index % plan.Channels.Count];
            var churn = $"churn{plan.NetworkIndex}-{generation}-{index:00}";
            transport.EnqueueInboundLine($":{churn}!u@host JOIN {channel}");
            transport.EnqueueInboundLine($":{churn}!u@host PART {channel} :churn");
            count += 2;
        }

        for (var index = 0; index < trafficCount; index++)
        {
            var channel = plan.Channels[index % plan.Channels.Count];
            var text = $"phase1r-{generation}-{plan.NetworkIndex}-{index:00000}";
            if (index % 19 == 0)
            {
                var peer = $"peer{plan.NetworkIndex}-{index % 24}";
                transport.EnqueueInboundLine($":{peer}!u@host PRIVMSG {nickname} :{text}");
            }
            else if (index % 17 == 0)
            {
                transport.EnqueueInboundLine($":notice{plan.NetworkIndex}!u@host NOTICE {channel} :{text}");
                plan.ExpectedChannelMessages[channel].Add(text);
            }
            else
            {
                var sender = $"user{plan.NetworkIndex}-{(index % 48):00}";
                transport.EnqueueInboundLine($":{sender}!u@host PRIVMSG {channel} :{text}");
                plan.ExpectedChannelMessages[channel].Add(text);
            }

            count++;
        }

        return count;
    }

    private static async Task WaitForPlanConvergenceAsync(SessionPlan plan, string failure)
    {
        await WaitForAsync(
            () => plan.Network.Channels.All(channel => channel.Synchronization == ChannelSynchronizationState.Synchronized)
                && plan.ExpectedChannelMessages.All(pair => pair.Value.Count == 0 || plan.Network.Channels.Single(channel => channel.Channel == pair.Key).EntriesSnapshot.Any(entry => entry.Text == pair.Value[^1])),
            failure);

        foreach (var pair in plan.ExpectedChannelMessages)
        {
            var actual = plan.Network.Channels.Single(channel => channel.Channel == pair.Key)
                .EntriesSnapshot
                .Where(entry => entry.Text.StartsWith("phase1r-", StringComparison.Ordinal))
                .Select(entry => entry.Text)
                .ToArray();
            var expectedTail = pair.Value.TakeLast(actual.Length).ToArray();
            Assert.Equal(expectedTail, actual);
        }

        Assert.All(plan.Network.Channels, channel => Assert.Equal(49, channel.MembersSnapshot.Count));
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure, int timeoutMilliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Phase 1R condition timed out: {failure}");
            }

            await Task.Delay(5);
        }
    }

    private sealed class SessionPlan(
        int networkIndex,
        FakeIrcTransport initial,
        IReadOnlyList<FakeIrcTransport> replacements,
        IReadOnlyList<string> channels)
    {
        public int NetworkIndex { get; } = networkIndex;

        public FakeIrcTransport Initial { get; } = initial;

        public IReadOnlyList<FakeIrcTransport> Replacements { get; } = replacements;

        public IReadOnlyList<string> Channels { get; } = channels;

        public NetworkWorkspace Network { get; set; } = null!;

        public Dictionary<string, List<string>> ExpectedChannelMessages { get; } = channels.ToDictionary(channel => channel, _ => new List<string>(), StringComparer.Ordinal);

        public IEnumerable<FakeIrcTransport> AllTransports => [Initial, .. Replacements];
    }

    private sealed class CountingWorkspaceDispatcher : IWorkspaceDispatcher, IWorkspaceDispatcherDiagnosticsProvider
    {
        private long _posted;
        private long _executed;
        private long _pending;
        private long _maximumPending;

        public WorkspaceDispatcherBoundaryDiagnostics Diagnostics => new(
            Interlocked.Read(ref _posted),
            Interlocked.Read(ref _executed),
            0,
            Interlocked.Read(ref _pending),
            Interlocked.Read(ref _maximumPending));

        public ValueTask InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            Interlocked.Increment(ref _posted);
            var pending = Interlocked.Increment(ref _pending);
            UpdateMaximum(ref _maximumPending, pending);
            return new ValueTask(Task.Run(async () =>
            {
                await Task.Yield();
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _executed);
                action();
            }));
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
}
