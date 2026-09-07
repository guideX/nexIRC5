using System.Diagnostics;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

[CollectionDefinition("Phase1T endurance", DisableParallelization = true)]
public sealed class Phase1TCollectionDefinition
{
}

[Collection("Phase1T endurance")]
public sealed class Phase1TTests
{
    [Fact]
    public async Task AcceleratedEnduranceKeepsReconnectChurnAndRetainedStateBounded()
    {
        const int networkCount = 3;
        const int channelCount = 4;
        const int initialTrafficPerNetwork = 720;
        const int reconnectGenerations = 20;
        const int reconnectTrafficPerGeneration = 600;
        const int historyCycles = 6;

        var root = Directory.CreateTempSubdirectory("nexirc5-phase1t-accelerated-");
        NetworkSessionManager? manager = null;
        try
        {
            var factory = new FakeIrcTransportFactory();
            var plans = Enumerable.Range(0, networkCount)
                .Select(index => new EnduranceNetworkPlan(index, BuildChannels(index, channelCount)))
                .ToArray();
            var allTransports = plans.SelectMany(plan => plan.Transports).ToList();
            foreach (var plan in plans)
            {
                factory.Add(plan.CurrentTransport);
            }

            var configuration = await CreateLoggingConfigurationAsync().ConfigureAwait(true);
            var logStore = new JsonlConversationLogStore(root.FullName, maximumSegmentBytes: 4 * 1024 * 1024);
            manager = new NetworkSessionManager(factory, configuration: configuration, logStore: logStore);
            var checkpoints = new List<EnduranceCheckpoint>
            {
                CaptureCheckpoint("startup", manager, logStore, allTransports)
            };

            foreach (var plan in plans)
            {
                plan.Network = manager.Add(BuildOptions(plan, plan.CurrentTransport.Endpoint));
            }

            foreach (var plan in plans)
            {
                await manager.ConnectAsync(plan.Network.Id).ConfigureAwait(true);
                await WaitForAsync(() => plan.CurrentTransport.ConnectCount == 1, "accelerated initial transport did not connect");
                await WaitForAsync(() => plan.Network.State == NetworkDisplayState.Connecting || plan.Network.State == NetworkDisplayState.CapNegotiation, "accelerated initial session did not start");
                plan.InitialEventCount += EnqueueRegistration(plan.CurrentTransport, plan.Network.Snapshot.Nickname, $"phase1t-{plan.Index}.server");
                await WaitForAsync(() => plan.Network.State == NetworkDisplayState.Registered, "accelerated initial registration did not complete");
                plan.InitialEventCount += EnqueueWorkspace(plan, plan.CurrentTransport, 0);
            }

            foreach (var plan in plans)
            {
                await WaitForWorkspaceAsync(plan, "accelerated workspace did not converge");
                plan.InitialEventCount += EnqueueTraffic(plan, plan.CurrentTransport, 0, initialTrafficPerNetwork);
                await WaitForConvergenceAsync(plan, $"phase1t-tail-00-{plan.Index}", "accelerated initial traffic did not converge");
                manager.ActivateView(plan.Network.Channels[plan.Index % plan.Network.Channels.Count].Id);
            }

            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            checkpoints.Add(CaptureCheckpoint("populated", manager, logStore, allTransports));

            var quietPlan = plans[^1];
            manager.ActivateView(plans[0].Network.Channels[0].Id);
            quietPlan.CurrentTransport.EnqueueInboundLine($"@msgid=phase1t-quiet-marker :quiet!u@host PRIVMSG {quietPlan.Channels[0]} :phase1t-quiet-unread");
            await WaitForAsync(() => quietPlan.Network.Channels[0].UnreadCount > 0, "accelerated quiet-network unread marker did not accumulate");
            Assert.NotEqual(WorkspaceActivity.None, quietPlan.Network.Channels[0].Activity);
            _ = manager.NavigateNextUnread();
            manager.ActivateView(quietPlan.Network.Channels[0].Id);
            Assert.Equal(WorkspaceActivity.None, quietPlan.Network.Channels[0].Activity);

            var acceleratedEvents = plans.Sum(plan => plan.InitialEventCount);
            checkpoints.Add(CaptureCheckpoint("heavy-traffic", manager, logStore, allTransports));
            for (var generation = 1; generation <= reconnectGenerations; generation++)
            {
                var plan = plans[(generation - 1) % 2];
                var oldTransport = plan.CurrentTransport;
                var replacement = new FakeIrcTransport(oldTransport.Endpoint);
                plan.Transports.Add(replacement);
                allTransports.Add(replacement);
                factory.Add(replacement);

                await manager.ReconnectAsync(plan.Network.Id).ConfigureAwait(true);
                await WaitForAsync(
                    () => replacement.ConnectCount == 1
                        && oldTransport.CallbackSubscriptionCount == 0
                        && oldTransport.ActiveReadCount == 0,
                    $"accelerated reconnect generation {generation} did not retire its previous transport");

                await oldTransport.EmitCallbackAsync(new IrcTransportInboundLineCallback(
                    $"@msgid=phase1t-stale-{generation} :stale!u@host PRIVMSG {plan.Channels[0]} :stale-generation-{generation}"));
                plan.EventCount += EnqueueRegistration(replacement, plan.Network.Snapshot.Nickname, $"phase1t-{plan.Index}-r{generation}.server");
                await WaitForAsync(() => plan.Network.State == NetworkDisplayState.Registered, $"accelerated reconnect generation {generation} did not register");
                var generationEvents = EnqueueWorkspace(plan, replacement, generation);
                generationEvents += EnqueueTraffic(plan, replacement, generation, reconnectTrafficPerGeneration);
                plan.EventCount += generationEvents;
                acceleratedEvents += generationEvents;
                await WaitForConvergenceAsync(plan, $"phase1t-tail-{generation:00}-{plan.Index}", $"accelerated reconnect generation {generation} did not converge");
                await manager.FlushStateDispatchAsync().ConfigureAwait(true);

                var other = plans[2];
                if (!ReferenceEquals(other, plan) && generation % 4 == 0)
                {
                    other.CurrentTransport.EnqueueInboundLine(
                        $"@msgid=phase1t-cross-{generation} :other!u@host PRIVMSG {other.Channels[0]} :phase1t-other-network-{generation}");
                    acceleratedEvents++;
                }

                manager.ActivateView(plan.Network.Channels[generation % plan.Network.Channels.Count].Id);
            }

            acceleratedEvents += 1;
            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            checkpoints.Add(CaptureCheckpoint("reconnect-churn", manager, logStore, allTransports));

            foreach (var plan in plans)
            {
                Assert.Equal(13, plan.Network.Channels[0].MembersSnapshot.Count);
                Assert.All(plan.Network.Channels, channel =>
                    Assert.DoesNotContain(channel.EntriesSnapshot, entry => entry.Text.StartsWith("stale-generation-", StringComparison.Ordinal)));
                foreach (var channel in plan.Network.Channels)
                {
                    var messages = channel.EntriesSnapshot
                        .Where(entry => entry.Text.StartsWith("phase1t-msg-", StringComparison.Ordinal))
                        .Select(entry => entry.Text)
                        .ToArray();
                    Assert.Equal(messages.OrderBy(static value => value, StringComparer.Ordinal), messages);
                }
            }

            var historyRecords = await AppendHistoryChurnAsync(logStore, plans[0].Network, 2_400).ConfigureAwait(true);
            await logStore.FlushAsync().ConfigureAwait(true);
            var historyFilesAfterFlush = CountFiles(root.FullName);
            var firstSearchFiles = historyFilesAfterFlush;
            for (var cycle = 0; cycle < historyCycles; cycle++)
            {
                var channel = plans[0].Network.Channels[0];
                var page = await logStore.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = plans[0].Network.Id,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = channel.Channel,
                    PageSize = 50
                }).ConfigureAwait(true);
                Assert.NotEmpty(page.Records);

                var search = await logStore.SearchDetailedAsync(new ConversationLogQuery
                {
                    Scope = ConversationLogSearchScope.CurrentNetwork,
                    NetworkId = plans[0].Network.Id,
                    Text = "phase1t-msg",
                    MaximumResults = 25
                }).ConfigureAwait(true);
                Assert.NotEmpty(search.Results);
                Assert.True(search.Statistics.MatchingRecords >= search.Results.Count);
                if (cycle == 0)
                {
                    firstSearchFiles = CountFiles(root.FullName);
                }
            }

            Assert.Equal(firstSearchFiles, CountFiles(root.FullName));
            Assert.True(logStore.HistoryIndexCount > 0, "accelerated history navigation did not exercise a disposable history index");
            Assert.True(logStore.SearchIndexCount > 0, "accelerated search did not exercise a disposable search index");
            checkpoints.Add(CaptureCheckpoint("history-search", manager, logStore, allTransports));

            var removedPlan = plans[^1];
            await manager.RemoveAsync(removedPlan.Network.Id).ConfigureAwait(true);
            Assert.DoesNotContain(manager.Networks, network => network.Id == removedPlan.Network.Id);
            Assert.Equal(0, removedPlan.CurrentTransport.CallbackSubscriptionCount);
            Assert.Equal(0, removedPlan.CurrentTransport.ActiveReadCount);

            var readdedTransport = new FakeIrcTransport(new IrcEndpoint("phase1t-readded.example", 6667, false));
            allTransports.Add(readdedTransport);
            factory.Add(readdedTransport);
            var readded = new EnduranceNetworkPlan(3, ["#phase1t-readded"]);
            readded.Network = manager.Add(BuildOptions(readded, readdedTransport.Endpoint));
            await manager.ConnectAsync(readded.Network.Id).ConfigureAwait(true);
            await WaitForAsync(() => readdedTransport.ConnectCount == 1, "accelerated re-added network did not connect");
            readded.EventCount += EnqueueRegistration(readdedTransport, readded.Network.Snapshot.Nickname, "phase1t-readded.server");
            await WaitForAsync(() => readded.Network.State == NetworkDisplayState.Registered, "accelerated re-added network did not register");
            readded.EventCount += EnqueueWorkspace(readded, readdedTransport, 0);
            readded.EventCount += EnqueueTraffic(readded, readdedTransport, 0, 80);
            await WaitForConvergenceAsync(readded, "phase1t-tail-00-3", "accelerated re-added network did not converge");
            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            await manager.RemoveAsync(readded.Network.Id).ConfigureAwait(true);
            acceleratedEvents += readded.EventCount;

            var beforeQuiet = manager.Diagnostics;
            await logStore.FlushAsync().ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);
            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            await logStore.FlushAsync().ConfigureAwait(true);
            var afterQuiet = manager.Diagnostics;
            Assert.Equal(0, afterQuiet.CurrentQueueDepth);
            Assert.Equal(0, manager.PendingDispatchCount);
            Assert.Equal(beforeQuiet.ProcessedStateActions, afterQuiet.ProcessedStateActions);
            Assert.Equal(0, logStore.PendingWriteCount);
            Assert.False(logStore.IsWriterCompleted);
            checkpoints.Add(CaptureCheckpoint("quiet", manager, logStore, allTransports));
            checkpoints.Add(CaptureCheckpoint("pre-shutdown", manager, logStore, allTransports));

            var diagnosticsBeforeShutdown = manager.Diagnostics;
            var maximumQueueDepth = diagnosticsBeforeShutdown.MaximumQueueDepth;
            var staleEvents = diagnosticsBeforeShutdown.StaleGenerationEventsDiscarded;
            var duplicateEvents = diagnosticsBeforeShutdown.DuplicateSemanticEventsDiscarded;
            Assert.True(duplicateEvents > 0, "accelerated duplicate semantic traffic was not fenced");
            Assert.Equal(0, diagnosticsBeforeShutdown.CurrentQueueDepth);
            Assert.Equal(0, manager.PendingDispatchCount);
            Assert.Equal(networkCount - 1, manager.Networks.Count);
            Assert.Equal(networkCount - 1, allTransports.Count(transport => transport.IsConnected));

            await manager.DisposeAsync().ConfigureAwait(true);
            var afterShutdown = CaptureCheckpoint("post-shutdown", manager, logStore, allTransports);
            Assert.Equal(0, afterShutdown.LiveSessionCount);
            Assert.Equal(0, afterShutdown.ActiveTransportCount);
            Assert.Equal(0, afterShutdown.TransportSubscriptionCount);
            Assert.Equal(0, afterShutdown.ActiveReadCount);
            Assert.Equal(0, manager.PendingDispatchCount);
            Assert.True(logStore.IsWriterCompleted);
            Assert.Equal(0, logStore.PendingWriteCount);
            Assert.Equal(0, logStore.HistoryIndexCount);
            Assert.Equal(0, logStore.SearchIndexCount);
            Assert.All(allTransports, transport =>
            {
                Assert.True(transport.IsDisposed);
                Assert.Equal(1, transport.DisposeCount);
                Assert.Equal(0, transport.CallbackSubscriptionCount);
                Assert.Equal(0, transport.ActiveReadCount);
                Assert.InRange(transport.MaximumActiveReadCount, 0, 1);
                Assert.Equal(1, transport.OutboundLines.Count(line => line.StartsWith("QUIT", StringComparison.Ordinal)));
            });

            Console.WriteLine(FormatMetrics(
                "PHASE1T_ACCELERATED",
                acceleratedEvents,
                networkCount,
                plans.Sum(plan => plan.Network?.Views.Count ?? 0),
                plans.Sum(plan => plan.Network?.Channels.Sum(channel => channel.MembersSnapshot.Count) ?? 0),
                reconnectGenerations,
                historyCycles,
                checkpoints,
                maximumQueueDepth,
                diagnosticsBeforeShutdown.CurrentQueueDepth,
                staleEvents,
                duplicateEvents,
                historyFilesAfterFlush,
                firstSearchFiles,
                afterShutdown,
                historyRecords));
        }
        finally
        {
            if (manager is not null)
            {
                await manager.DisposeAsync().ConfigureAwait(true);
            }

            if (Directory.Exists(root.FullName))
            {
                Directory.Delete(root.FullName, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionPacedEnduranceSettlesDuringQuietPeriods()
    {
        const int networkCount = 2;
        const int channelCount = 3;
        const int pacedTrafficPerNetwork = 180;
        const int reconnectTraffic = 60;
        const int historyCycles = 4;
        const int quietMilliseconds = 750;

        var root = Directory.CreateTempSubdirectory("nexirc5-phase1t-paced-");
        NetworkSessionManager? manager = null;
        try
        {
            var factory = new FakeIrcTransportFactory();
            var plans = Enumerable.Range(0, networkCount)
                .Select(index => new EnduranceNetworkPlan(index, BuildChannels(index, channelCount)))
                .ToArray();
            var allTransports = plans.SelectMany(plan => plan.Transports).ToList();
            foreach (var plan in plans)
            {
                factory.Add(plan.CurrentTransport);
            }

            var configuration = await CreateLoggingConfigurationAsync().ConfigureAwait(true);
            var logStore = new JsonlConversationLogStore(root.FullName, maximumSegmentBytes: 32 * 1024);
            manager = new NetworkSessionManager(factory, configuration: configuration, logStore: logStore);
            var checkpoints = new List<EnduranceCheckpoint>
            {
                CaptureCheckpoint("startup", manager, logStore, allTransports)
            };

            foreach (var plan in plans)
            {
                plan.Network = manager.Add(BuildOptions(plan, plan.CurrentTransport.Endpoint));
                await manager.ConnectAsync(plan.Network.Id).ConfigureAwait(true);
                await WaitForAsync(() => plan.CurrentTransport.ConnectCount == 1, "production-paced transport did not connect");
                plan.EventCount += EnqueueRegistration(plan.CurrentTransport, plan.Network.Snapshot.Nickname, $"phase1t-paced-{plan.Index}.server");
                await WaitForAsync(() => plan.Network.State == NetworkDisplayState.Registered, "production-paced registration did not complete");
                plan.EventCount += EnqueueWorkspace(plan, plan.CurrentTransport, 0);
            }

            foreach (var plan in plans)
            {
                await WaitForWorkspaceAsync(plan, "production-paced workspace did not converge");
            }

            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            checkpoints.Add(CaptureCheckpoint("populated", manager, logStore, allTransports));
            manager.ActivateView(plans[0].Network.Channels[0].Id);
            var producers = plans.Select(plan => ProducePacedTrafficAsync(plan, pacedTrafficPerNetwork, 0)).ToArray();
            var interactions = 0;
            while (producers.Any(task => !task.IsCompleted))
            {
                var plan = plans[interactions % plans.Length];
                manager.ActivateView(plan.Network.Channels[interactions % plan.Network.Channels.Count].Id);
                _ = manager.EnsureQuery(plan.Network.Id, $"paced-peer-{interactions % 3}");
                if (interactions % 3 == 0)
                {
                    manager.NavigateNextConversation();
                    manager.NavigatePreviousConversation();
                }

                interactions++;
                await Task.Delay(35).ConfigureAwait(true);
            }

            var pacedEventCounts = await Task.WhenAll(producers).ConfigureAwait(true);
            for (var index = 0; index < plans.Length; index++)
            {
                plans[index].EventCount += pacedEventCounts[index];
            }
            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            checkpoints.Add(CaptureCheckpoint("paced-heavy", manager, logStore, allTransports));

            manager.ActivateView(plans[0].Network.Channels[0].Id);
            plans[1].CurrentTransport.EnqueueInboundLine(
                "@msgid=phase1t-paced-unread :paced-quiet!u@host PRIVMSG #phase1t-01-00 :phase1t-paced-unread");
            await WaitForAsync(() => plans[1].Network.Channels[0].UnreadCount > 0, "production-paced unread marker did not accumulate");
            manager.ActivateView(plans[1].Network.Channels[0].Id);
            Assert.Equal(0, plans[1].Network.Channels[0].UnreadCount);

            var replacement = new FakeIrcTransport(plans[0].CurrentTransport.Endpoint);
            var oldTransport = plans[0].CurrentTransport;
            plans[0].Transports.Add(replacement);
            allTransports.Add(replacement);
            factory.Add(replacement);
            await manager.ReconnectAsync(plans[0].Network.Id).ConfigureAwait(true);
            await WaitForAsync(
                () => replacement.ConnectCount == 1
                    && oldTransport.CallbackSubscriptionCount == 0
                    && oldTransport.ActiveReadCount == 0,
                "production-paced reconnect did not retire the old transport");
            plans[0].EventCount += EnqueueRegistration(replacement, plans[0].Network.Snapshot.Nickname, "phase1t-paced-reconnect.server");
            await WaitForAsync(() => plans[0].Network.State == NetworkDisplayState.Registered, "production-paced reconnect did not register");
            plans[0].EventCount += EnqueueWorkspace(plans[0], replacement, 1);
            plans[0].EventCount += await ProducePacedTrafficAsync(plans[0], reconnectTraffic, 1, 10).ConfigureAwait(true);
            await WaitForConvergenceAsync(plans[0], "phase1t-tail-01-0", "production-paced reconnect traffic did not converge");
            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            checkpoints.Add(CaptureCheckpoint("reconnect", manager, logStore, allTransports));

            await logStore.FlushAsync().ConfigureAwait(true);
            var historyFilesAfterFlush = CountFiles(root.FullName);
            var filesAfterFirstSearch = historyFilesAfterFlush;
            for (var cycle = 0; cycle < historyCycles; cycle++)
            {
                var page = await logStore.ReadPageWindowAsync(new HistoryPageRequest
                {
                    ScopeId = plans[0].Network.Id,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = plans[0].Network.Channels[0].Channel,
                    PageSize = 50
                }).ConfigureAwait(true);
                Assert.NotEmpty(page.Records);
                var search = await logStore.SearchDetailedAsync(new ConversationLogQuery
                {
                    Scope = ConversationLogSearchScope.CurrentNetwork,
                    NetworkId = plans[0].Network.Id,
                    Text = "phase1t-msg",
                    MaximumResults = 25
                }).ConfigureAwait(true);
                Assert.NotEmpty(search.Results);
                if (cycle == 0)
                {
                    filesAfterFirstSearch = CountFiles(root.FullName);
                }
            }

            Assert.Equal(filesAfterFirstSearch, CountFiles(root.FullName));
            await logStore.FlushAsync().ConfigureAwait(true);
            checkpoints.Add(CaptureCheckpoint("history-search", manager, logStore, allTransports));

            await SettleAsync(manager, logStore, allTransports).ConfigureAwait(true);
            var quietStartDiagnostics = manager.Diagnostics;
            var quietStartTransports = allTransports.Count(transport => transport.IsConnected);
            await Task.Delay(quietMilliseconds).ConfigureAwait(true);
            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            await logStore.FlushAsync().ConfigureAwait(true);
            var quietDiagnostics = manager.Diagnostics;
            Assert.Equal(0, quietDiagnostics.CurrentQueueDepth);
            Assert.Equal(0, manager.PendingDispatchCount);
            Assert.Equal(0, logStore.PendingWriteCount);
            Assert.Equal(quietStartDiagnostics.ProcessedStateActions, quietDiagnostics.ProcessedStateActions);
            Assert.Equal(quietStartTransports, allTransports.Count(transport => transport.IsConnected));
            checkpoints.Add(CaptureCheckpoint("quiet", manager, logStore, allTransports));
            checkpoints.Add(CaptureCheckpoint("pre-shutdown", manager, logStore, allTransports));

            var beforeShutdown = manager.Diagnostics;
            await manager.DisposeAsync().ConfigureAwait(true);
            var afterShutdown = CaptureCheckpoint("post-shutdown", manager, logStore, allTransports);
            Assert.Equal(0, afterShutdown.LiveSessionCount);
            Assert.Equal(0, afterShutdown.ActiveTransportCount);
            Assert.Equal(0, afterShutdown.TransportSubscriptionCount);
            Assert.Equal(0, afterShutdown.ActiveReadCount);
            Assert.True(logStore.IsWriterCompleted);
            Assert.Equal(0, logStore.HistoryIndexCount);
            Assert.Equal(0, logStore.SearchIndexCount);
            Assert.All(allTransports, transport =>
            {
                Assert.True(transport.IsDisposed);
                Assert.Equal(0, transport.CallbackSubscriptionCount);
                Assert.Equal(0, transport.ActiveReadCount);
                Assert.Equal(1, transport.OutboundLines.Count(line => line.StartsWith("QUIT", StringComparison.Ordinal)));
            });

            Console.WriteLine(FormatMetrics(
                "PHASE1T_PRODUCTION_PACED",
                plans.Sum(plan => plan.EventCount) + 1,
                networkCount,
                plans.Sum(plan => plan.Network?.Views.Count ?? 0),
                plans.Sum(plan => plan.Network?.Channels.Sum(channel => channel.MembersSnapshot.Count) ?? 0),
                1,
                historyCycles,
                checkpoints,
                beforeShutdown.MaximumQueueDepth,
                beforeShutdown.CurrentQueueDepth,
                beforeShutdown.StaleGenerationEventsDiscarded,
                beforeShutdown.DuplicateSemanticEventsDiscarded,
                historyFilesAfterFlush,
                filesAfterFirstSearch,
                afterShutdown,
                historyRecords: 0));
        }
        finally
        {
            if (manager is not null)
            {
                await manager.DisposeAsync().ConfigureAwait(true);
            }

            if (Directory.Exists(root.FullName))
            {
                Directory.Delete(root.FullName, recursive: true);
            }
        }
    }

    private static async Task<ConfigurationService> CreateLoggingConfigurationAsync()
    {
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
        await configuration.LoadAsync().ConfigureAwait(true);
        return configuration;
    }

    private static NetworkConnectionOptions BuildOptions(EnduranceNetworkPlan plan, IrcEndpoint endpoint) => new()
    {
        DisplayName = $"Phase 1T Net {plan.Index}",
        Endpoint = endpoint,
        Nickname = $"nexT{plan.Index}",
        Username = $"nexT{plan.Index}",
        RealName = "Phase 1T deterministic endurance",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = plan.Channels.ToHashSet(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static string[] BuildChannels(int networkIndex, int count) =>
        Enumerable.Range(0, count).Select(index => $"#phase1t-{networkIndex}-{index:00}").ToArray();

    private static int EnqueueRegistration(FakeIrcTransport transport, string nickname, string server)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :message-tags server-time multi-prefix");
        transport.EnqueueInboundLine($":{server} 005 {nickname} PREFIX=(ov)@+ NETWORK=Phase1T CHANTYPES=#& CASEMAPPING=rfc1459");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to the Phase 1T deterministic network");
        return 3;
    }

    private static int EnqueueWorkspace(EnduranceNetworkPlan plan, FakeIrcTransport transport, int generation)
    {
        var count = 0;
        var nickname = plan.Network.Snapshot.Nickname;
        foreach (var channel in plan.Channels)
        {
            transport.EnqueueInboundLine($":{nickname}!local@host JOIN {channel}");
            var names = Enumerable.Range(0, 12).Select(index => $"user{plan.Index}-{index:00}");
            transport.EnqueueInboundLine($":srv 353 {nickname} = {channel} :@{nickname} {string.Join(' ', names)}");
            transport.EnqueueInboundLine($":srv 366 {nickname} {channel} :End of NAMES list");
            transport.EnqueueInboundLine($":srv 332 {nickname} {channel} :Phase 1T topic generation {generation}");
            transport.EnqueueInboundLine($":srv 324 {nickname} {channel} +nt");
            transport.EnqueueInboundLine($":srv 352 {nickname} {channel} user{plan.Index}-00 host srv user{plan.Index}-00 H :0 Phase 1T user");
            transport.EnqueueInboundLine($":srv 315 {nickname} {channel} :End of WHO list");
            count += 7;
        }

        return count;
    }

    private static int EnqueueTraffic(EnduranceNetworkPlan plan, FakeIrcTransport transport, int generation, int trafficCount)
    {
        var count = 0;
        var nickname = plan.Network.Snapshot.Nickname;
        for (var index = 0; index < trafficCount; index++)
        {
            var channel = plan.Channels[index % plan.Channels.Count];
            var messageId = $"phase1t-{plan.Index}-{generation}-{index:0000}";
            if (index % 53 == 0)
            {
                var churn = $"churn{plan.Index}-{generation}-{index:0000}";
                transport.EnqueueInboundLine($":{churn}!u@host JOIN {channel}");
                transport.EnqueueInboundLine($":{churn}!u@host PART {channel} :churn");
                count += 2;
            }
            else if (index % 47 == 0)
            {
                transport.EnqueueInboundLine($":user{plan.Index}-00!u@host NICK :renamed{plan.Index}-{generation}-{index:0000}");
                count++;
            }
            else if (index % 41 == 0)
            {
                transport.EnqueueInboundLine($":operator!u@host TOPIC {channel} :phase1t topic {messageId}");
                count++;
            }
            else if (index % 37 == 0)
            {
                transport.EnqueueInboundLine($":operator!u@host MODE {channel} +m");
                count++;
            }
            else if (index % 29 == 0)
            {
                transport.EnqueueInboundLine($"@msgid={messageId}-notice :notice!u@host NOTICE {channel} :phase1t-notice-{messageId}");
                count++;
            }
            else if (index % 23 == 0)
            {
                var peer = $"peer{plan.Index}-{index % 7}";
                transport.EnqueueInboundLine($"@msgid={messageId}-query :{peer}!u@host PRIVMSG {nickname} :phase1t-query-{messageId}");
                count++;
            }
            else
            {
                transport.EnqueueInboundLine($"@msgid={messageId} :user{plan.Index}-{index % 12:00}!u@host PRIVMSG {channel} :phase1t-msg-{generation:00}-{index:0000}");
                count++;
                if (index == 5)
                {
                    transport.EnqueueInboundLine($"@msgid={messageId} :user{plan.Index}-{index % 12:00}!u@host PRIVMSG {channel} :phase1t-msg-{generation:00}-{index:0000}");
                    count++;
                }
            }
        }

        var tail = $"phase1t-tail-{generation:00}-{plan.Index}";
        transport.EnqueueInboundLine($"@msgid={tail} :tail!u@host PRIVMSG {plan.Channels[0]} :{tail}");
        return count + 1;
    }

    private static async Task<int> ProducePacedTrafficAsync(
        EnduranceNetworkPlan plan,
        int trafficCount,
        int generation,
        int normalDelayMilliseconds = 8)
    {
        var count = 0;
        var nickname = plan.Network.Snapshot.Nickname;
        for (var index = 0; index < trafficCount; index++)
        {
            var channel = plan.Channels[index % plan.Channels.Count];
            var id = $"phase1t-paced-{plan.Index}-{generation}-{index:0000}";
            if (index % 53 == 0)
            {
                var churn = $"paced-churn{plan.Index}-{generation}-{index:0000}";
                plan.CurrentTransport.EnqueueInboundLine($":{churn}!u@host JOIN {channel}");
                plan.CurrentTransport.EnqueueInboundLine($":{churn}!u@host PART {channel} :paced churn");
                count += 2;
            }
            else if (index % 29 == 0)
            {
                plan.CurrentTransport.EnqueueInboundLine($"@msgid={id}-notice :paced-notice!u@host NOTICE {channel} :phase1t-paced-notice-{id}");
                count++;
            }
            else if (index % 19 == 0)
            {
                plan.CurrentTransport.EnqueueInboundLine($"@msgid={id}-query :paced-peer!u@host PRIVMSG {nickname} :phase1t-paced-query-{id}");
                count++;
            }
            else
            {
                plan.CurrentTransport.EnqueueInboundLine($"@msgid={id} :paced-user!u@host PRIVMSG {channel} :phase1t-msg-{generation:00}-{index:0000}",
                    index % 20 < 4 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(normalDelayMilliseconds));
                count++;
            }

            if (index % 60 == 59)
            {
                await Task.Delay(150).ConfigureAwait(true);
            }
        }

        plan.CurrentTransport.EnqueueInboundLine($"@msgid=phase1t-tail-{generation:00}-{plan.Index} :paced-tail!u@host PRIVMSG {plan.Channels[0]} :phase1t-tail-{generation:00}-{plan.Index}");
        return count + 1;
    }

    private static async Task<int> AppendHistoryChurnAsync(
        JsonlConversationLogStore logStore,
        NetworkWorkspace network,
        int recordCount)
    {
        var channel = network.Channels[0];
        var conversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, channel.Channel);
        var textSuffix = new string('h', 480);
        for (var index = 0; index < recordCount; index++)
        {
            var accepted = await logStore.AppendAsync(new ConversationLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(index),
                NetworkId = network.Id,
                ScopeId = network.Id,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = channel.Channel,
                ConversationKey = conversationKey,
                Sender = "history-worker",
                MessageKind = LogMessageKind.Message,
                Direction = LogDirection.Incoming,
                Text = $"phase1t-history-{index:0000} {textSuffix}"
            }).ConfigureAwait(true);
            Assert.True(accepted);
        }

        return recordCount;
    }

    private static async Task WaitForConvergenceAsync(EnduranceNetworkPlan plan, string tail, string failure)
    {
        await WaitForAsync(
            () => plan.Network.State == NetworkDisplayState.Registered
                && plan.Network.Channels.All(channel => channel.Synchronization == ChannelSynchronizationState.Synchronized)
                && plan.Network.Channels.Any(channel => channel.EntriesSnapshot.Any(entry => entry.Text == tail)),
            failure,
            30_000).ConfigureAwait(true);
    }

    private static async Task WaitForWorkspaceAsync(EnduranceNetworkPlan plan, string failure)
    {
        await WaitForAsync(
            () => plan.Network.State == NetworkDisplayState.Registered
                && plan.Network.Channels.All(channel => channel.Synchronization == ChannelSynchronizationState.Synchronized),
            failure,
            30_000).ConfigureAwait(true);
    }

    private static async Task SettleAsync(
        NetworkSessionManager manager,
        JsonlConversationLogStore logStore,
        IReadOnlyList<FakeIrcTransport> transports)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await manager.FlushStateDispatchAsync().ConfigureAwait(true);
            await logStore.FlushAsync().ConfigureAwait(true);
            await WaitForAsync(
                () => transports.All(transport => transport.IsDisposed || transport.PendingInboundItemCount == 0)
                    && manager.Diagnostics.CurrentQueueDepth == 0
                    && manager.PendingDispatchCount == 0
                    && logStore.PendingWriteCount == 0,
                "endurance activity did not settle before quiet measurement");
            var processed = manager.Diagnostics.ProcessedStateActions;
            await Task.Delay(100).ConfigureAwait(true);
            if (processed == manager.Diagnostics.ProcessedStateActions)
            {
                return;
            }
        }

        throw new TimeoutException("endurance activity continued during the quiescence fence");
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure, int timeoutMilliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Phase 1T condition timed out: {failure}");
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private static int CountFiles(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
        : 0;

    private static EnduranceCheckpoint CaptureCheckpoint(
        string name,
        NetworkSessionManager manager,
        JsonlConversationLogStore logStore,
        IReadOnlyList<FakeIrcTransport> transports)
    {
        var managedBytes = StabilizeManagedMemory();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var diagnostics = manager.Diagnostics;
        return new EnduranceCheckpoint(
            name,
            managedBytes,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            manager.ManagedSessionCount,
            transports.Count(transport => transport.IsConnected),
            transports.Sum(transport => transport.CallbackSubscriptionCount),
            transports.Sum(transport => transport.ActiveReadCount),
            manager.Networks.SelectMany(network => network.Views).Count(),
            manager.Networks.SelectMany(network => network.Channels).Sum(channel => channel.MembersSnapshot.Count),
            diagnostics.CurrentQueueDepth,
            diagnostics.StateDispatch.MaximumWpfPendingWorkItems,
            manager.PendingDispatchCount,
            logStore.PendingWriteCount,
            logStore.HistoryIndexCount,
            logStore.SearchIndexCount,
            process.WorkingSet64,
            process.PrivateMemorySize64);
    }

    private static long StabilizeManagedMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static string FormatMetrics(
        string label,
        int events,
        int networks,
        int conversations,
        int members,
        int reconnects,
        int historyCycles,
        IReadOnlyList<EnduranceCheckpoint> checkpoints,
        int maximumQueueDepth,
        int finalQueueDepth,
        long staleEvents,
        long duplicateEvents,
        int historyFilesAfterFlush,
        int historyFilesAfterFirstSearch,
        EnduranceCheckpoint afterShutdown,
        int historyRecords)
    {
        var startup = checkpoints.First(item => item.Name == "startup");
        var populated = checkpoints.First(item => item.Name == "populated");
        var heavy = checkpoints.FirstOrDefault(item => item.Name is "heavy-traffic" or "paced-heavy") ?? populated;
        var quiet = checkpoints.First(item => item.Name == "quiet");
        var preShutdown = checkpoints.First(item => item.Name == "pre-shutdown");
        var peakManaged = checkpoints.Max(item => item.ManagedBytes);
        var peakWorkingSet = checkpoints.Max(item => item.WorkingSetBytes);
        var peakPrivateMemory = checkpoints.Max(item => item.PrivateMemoryBytes);
        var allocatedStart = startup.AllocatedBytes;
        var finalAllocated = preShutdown.AllocatedBytes - allocatedStart;
        var gen0 = preShutdown.Gen0Collections - startup.Gen0Collections;
        var gen1 = preShutdown.Gen1Collections - startup.Gen1Collections;
        var gen2 = preShutdown.Gen2Collections - startup.Gen2Collections;
        var early = checkpoints.Where(item => item.Name is "populated" or "paced-heavy").Select(item => item.CurrentQueueDepth).DefaultIfEmpty().First();
        var late = quiet.CurrentQueueDepth;
        return $"{label} events={events} history_records={historyRecords} networks={networks} conversations={conversations} members={members} reconnects={reconnects} history_cycles={historyCycles} "
            + $"startup_managed={startup.ManagedBytes} populated_managed={populated.ManagedBytes} heavy_managed={heavy.ManagedBytes} post_churn_managed={preShutdown.ManagedBytes} quiet_managed={quiet.ManagedBytes} peak_managed={peakManaged} "
            + $"total_allocated={finalAllocated} gen0={gen0} gen1={gen1} gen2={gen2} working_set_baseline={startup.WorkingSetBytes} working_set_peak={peakWorkingSet} working_set_final={afterShutdown.WorkingSetBytes} "
            + $"private_memory_baseline={startup.PrivateMemoryBytes} private_memory_peak={peakPrivateMemory} private_memory_final={afterShutdown.PrivateMemoryBytes} "
            + $"managed_session_entries_baseline={startup.LiveSessionCount} managed_session_entries_peak={checkpoints.Max(item => item.LiveSessionCount)} managed_session_entries_final={afterShutdown.LiveSessionCount} "
            + $"active_transports_baseline={startup.ActiveTransportCount} active_transports_peak={checkpoints.Max(item => item.ActiveTransportCount)} active_transports_final={afterShutdown.ActiveTransportCount} "
            + $"transport_subscriptions_baseline={startup.TransportSubscriptionCount} transport_subscriptions_peak={checkpoints.Max(item => item.TransportSubscriptionCount)} transport_subscriptions_final={afterShutdown.TransportSubscriptionCount} "
            + $"active_readers_baseline={startup.ActiveReadCount} active_readers_peak={checkpoints.Max(item => item.ActiveReadCount)} active_readers_final={afterShutdown.ActiveReadCount} "
            + $"conversation_projections_baseline={startup.ConversationProjectionCount} conversation_projections_peak={checkpoints.Max(item => item.ConversationProjectionCount)} conversation_projections_final={afterShutdown.ConversationProjectionCount} "
            + $"member_projections_baseline={startup.MemberProjectionCount} member_projections_peak={checkpoints.Max(item => item.MemberProjectionCount)} member_projections_final={afterShutdown.MemberProjectionCount} "
            + $"queue_max={maximumQueueDepth} queue_final={finalQueueDepth} pending_wpf_max={checkpoints.Max(item => item.MaximumPendingWpfWorkItems)} pending_wpf_final={afterShutdown.MaximumPendingWpfWorkItems} "
            + $"pending_dispatch_baseline={startup.PendingDispatchCount} pending_dispatch_peak={checkpoints.Max(item => item.PendingDispatchCount)} pending_dispatch_final={afterShutdown.PendingDispatchCount} "
            + $"pending_writes_baseline={startup.PendingWriteCount} pending_writes_peak={checkpoints.Max(item => item.PendingWriteCount)} pending_writes_final={afterShutdown.PendingWriteCount} "
            + $"history_indexes_peak={checkpoints.Max(item => item.HistoryIndexCount)} search_indexes_peak={checkpoints.Max(item => item.SearchIndexCount)} history_indexes_final={afterShutdown.HistoryIndexCount} search_indexes_final={afterShutdown.SearchIndexCount} "
            + $"history_files_after_flush={historyFilesAfterFlush} history_files_after_first_search={historyFilesAfterFirstSearch} stale_events={staleEvents} duplicate_events={duplicateEvents} "
            + $"quiet_queue={late} early_queue={early} checkpoints={string.Join(',', checkpoints.Select(item => $"{item.Name}:{item.ManagedBytes}"))}";
    }

    private sealed class EnduranceNetworkPlan(int index, IReadOnlyList<string> channels)
    {
        public int Index { get; } = index;

        public IReadOnlyList<string> Channels { get; } = channels;

        public List<FakeIrcTransport> Transports { get; } =
        [new FakeIrcTransport(new IrcEndpoint($"phase1t-{index}.example", 6667, false))];

        public NetworkWorkspace Network { get; set; } = null!;

        public FakeIrcTransport CurrentTransport => Transports[^1];

        public int EventCount { get; set; }

        public int InitialEventCount { get; set; }
    }

    private sealed record EnduranceCheckpoint(
        string Name,
        long ManagedBytes,
        long AllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        int LiveSessionCount,
        int ActiveTransportCount,
        int TransportSubscriptionCount,
        int ActiveReadCount,
        int ConversationProjectionCount,
        int MemberProjectionCount,
        int CurrentQueueDepth,
        long MaximumPendingWpfWorkItems,
        int PendingDispatchCount,
        int PendingWriteCount,
        int HistoryIndexCount,
        int SearchIndexCount,
        long WorkingSetBytes,
        long PrivateMemoryBytes);
}
