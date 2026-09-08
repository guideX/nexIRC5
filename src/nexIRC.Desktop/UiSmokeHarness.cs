using System.Diagnostics;
using System.Windows;
using System.Globalization;
using System.IO;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Desktop;

/// <summary>
/// Deterministic, in-process GUI-path smoke tests. It uses only DemoScenario's
/// fake transports and invokes the same application services used by WPF
/// actions; it never targets HWNDs or production sessions.
/// </summary>
internal static class UiSmokeHarness
{
    private static readonly string[] Scenarios =
    [
        "participant",
        "moderation",
        "channel-properties",
        "multi-network",
        "lifecycle",
        "read-state",
        "reconnect",
        "burst",
        "burst-fairness",
        "query-nick",
        "ircv3-metadata",
        "chathistory",
        "history-pagination",
        "forward-pagination",
        "history-navigation",
        "history-search",
        "stale-search",
        "index-recovery",
        "index-fingerprint",
        "accessibility",
        "history-gap-repair",
        "history-integrity",
        "history-discovery",
        "history-identity",
        "contextual-actions",
        "sustained-interactivity",
        "close-idle",
        "close-sustained",
        "close-backlog",
        "close-reconnect",
        "close-partial",
        "close-registered",
        "close-persistence",
        "close-interacted"
    ];

    private static readonly string[] ExternalCloseScenarios =
    [
        "close-idle",
        "close-sustained",
        "close-backlog",
        "close-reconnect",
        "close-partial",
        "close-registered",
        "close-persistence",
        "close-interacted"
    ];

    public static bool IsKnownScenario(string? scenario) =>
        scenario is not null && Scenarios.Contains(scenario, StringComparer.OrdinalIgnoreCase);

    public static bool IsExternalCloseScenario(string? scenario) =>
        scenario is not null && ExternalCloseScenarios.Contains(scenario, StringComparer.OrdinalIgnoreCase);

    public static async Task RunAsync(string scenario, MainWindow window, DemoScenario demo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(demo);

        var state = await demo.SeedSmokeAsync(window.ViewModel).ConfigureAwait(true);
        switch (scenario.ToLowerInvariant())
        {
            case "participant":
                await ParticipantAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "moderation":
                await ModerationAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "channel-properties":
                await ChannelPropertiesAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "multi-network":
                await MultiNetworkAsync(window, demo, state.Alpha, state.Beta).ConfigureAwait(true);
                break;
            case "lifecycle":
                await LifecycleAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "read-state":
                await ReadStateAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "reconnect":
                await ReconnectAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "burst":
                await BurstAsync(window, demo, state.Alpha, 2_000).ConfigureAwait(true);
                break;
            case "burst-fairness":
                await BurstAsync(window, demo, state.Alpha, 5_000).ConfigureAwait(true);
                break;
            case "query-nick":
                await QueryNickAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "ircv3-metadata":
                await Ircv3MetadataAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "chathistory":
                await ChathistoryAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "history-pagination":
                await HistoryPaginationAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "forward-pagination":
                await ForwardPaginationAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "history-navigation":
                await HistoryNavigationAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "history-search":
                await HistorySearchAsync(window, demo, state.Alpha, state.Beta).ConfigureAwait(true);
                break;
            case "stale-search":
                StaleSearchAsync();
                break;
            case "index-recovery":
                await IndexRecoveryAsync().ConfigureAwait(true);
                break;
            case "index-fingerprint":
                await IndexFingerprintAsync().ConfigureAwait(true);
                break;
            case "accessibility":
                await AccessibilityAsync(window, state.Alpha).ConfigureAwait(true);
                break;
            case "history-gap-repair":
                await HistoryGapRepairAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "history-integrity":
                await HistoryIntegrityAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "history-discovery":
                await HistoryDiscoveryAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "history-identity":
                await HistoryIdentityAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "contextual-actions":
                await ContextualActionsAsync(window, demo, state.Alpha).ConfigureAwait(true);
                break;
            case "sustained-interactivity":
                await SustainedInteractivityAsync(window, demo, state.Alpha, state.Beta).ConfigureAwait(true);
                break;
            case "close-idle":
            case "close-sustained":
            case "close-backlog":
            case "close-reconnect":
            case "close-partial":
            case "close-registered":
            case "close-persistence":
            case "close-interacted":
                await CloseProbeAsync(scenario.ToLowerInvariant(), window, demo, state.Alpha, state.Beta).ConfigureAwait(true);
                break;
            default:
                throw new ArgumentException($"Unknown UI smoke scenario '{scenario}'.", nameof(scenario));
        }

        Console.WriteLine($"PASS_UI_SMOKE {scenario.ToLowerInvariant()}");
    }

    private static async Task ParticipantAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        var member = RequiredMember(channel, "Alex");
        var context = viewModel.CreateParticipantContext(network, channel, member);
        var groups = ParticipantActionCatalog.Build(context, isIgnored: false);
        Require(groups.SelectMany(group => group.Items).Any(item => item.Action == ParticipantActionKind.OpenQuery && item.IsEnabled), "participant query action was not enabled");
        Require(groups.SelectMany(group => group.Items).Any(item => item.Action == ParticipantActionKind.Whois && item.IsEnabled), "participant WHOIS action was not enabled");

        var outboundBefore = demo.AlphaTransport.OutboundLines.Count;
        viewModel.OpenParticipantQuery(context);
        var queryView = viewModel.ActiveView as QueryView;
        Require(queryView is not null, "participant query surface was not created");
        var query = queryView!;
        Require(ReferenceEquals(viewModel.ActiveView, query), "participant query did not activate through the view-model path");
        Require(demo.AlphaTransport.OutboundLines.Count == outboundBefore, "opening a participant query sent unexpected IRC traffic");

        var whois = await viewModel.ParticipantActions.SendWhoisAsync(context).ConfigureAwait(true);
        Require(whois.Succeeded && whois.View is WhoisView, "participant WHOIS request was not created");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 311 nexAlpha Alex alex alpha.example * :Alex Smoke User");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 318 nexAlpha Alex :End of WHOIS list");
        var whoisView = (WhoisView)whois.View!;
        await WaitForAsync(viewModel.Sessions, () => whoisView.IsCompleted, "participant WHOIS did not complete").ConfigureAwait(true);

        Require(viewModel.Sessions.CloseView(query.Id), "participant query surface did not close cleanly");
        Require(viewModel.Sessions.CloseView(whoisView.Id), "WHOIS surface did not close cleanly");
    }

    private static async Task ContextualActionsAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        var member = RequiredMember(channel, "Alex");
        var context = viewModel.CreateParticipantContext(network, channel, member);

        var networkActions = viewModel.Actions.BuildNetworkActions(network);
        Require(networkActions.Single(item => item.Action == WorkspaceActionId.Connect).IsEnabled == false,
            "registered network still exposed Connect as enabled");
        Require(networkActions.Single(item => item.Action == WorkspaceActionId.Disconnect).IsEnabled,
            "registered network did not expose Disconnect");

        var channelActions = viewModel.Actions.BuildChannelActions(network, channel);
        Require(channelActions.Single(item => item.Action == WorkspaceActionId.PartChannel).IsEnabled,
            "joined channel did not expose Part");
        Require(channelActions.Single(item => item.Action == WorkspaceActionId.RefreshNames).IsEnabled,
            "joined channel did not expose Refresh member list");

        var opened = await viewModel.Actions.ExecuteNetworkAsync(network, WorkspaceActionId.OpenNetwork).ConfigureAwait(true);
        Require(opened.Succeeded && ReferenceEquals(viewModel.Sessions.ActiveView, network.StatusView),
            "network context action did not activate server status");

        var refresh = await viewModel.Actions.ExecuteChannelAsync(network, channel, WorkspaceActionId.RefreshNames).ConfigureAwait(true);
        Require(refresh.Succeeded, "channel Refresh member list action failed");
        Require(demo.AlphaTransport.OutboundLines.Contains("NAMES #general"),
            "channel Refresh member list action did not route NAMES");

        viewModel.OpenParticipantQuery(context);
        Require(viewModel.ActiveView is QueryView query && query.Nickname == "Alex",
            "nick Open Query action did not reuse the production query path");
        var whois = await viewModel.Actions.ParticipantActions.SendWhoisAsync(context).ConfigureAwait(true);
        Require(whois.Succeeded && whois.View is WhoisView, "nick WHOIS action failed");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 311 nexAlpha Alex alex alpha.example * :Alex Context User");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 318 nexAlpha Alex :End of WHOIS list");
        await WaitForAsync(viewModel.Sessions, () => ((WhoisView)whois.View!).IsCompleted, "contextual WHOIS did not complete").ConfigureAwait(true);
    }

    private static async Task Ircv3MetadataAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        var refresh = await viewModel.Actions.ExecuteChannelAsync(network, channel, WorkspaceActionId.RefreshNames).ConfigureAwait(true);
        Require(refresh.Succeeded, "IRCv3 metadata smoke could not start a NAMES refresh");

        const string serverTime = "2026-09-07T15:04:05.123Z";
        var expectedServerTimestamp = DateTimeOffset.Parse(serverTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        demo.AlphaTransport.EnqueueInboundLine($"@time={serverTime} :Alex!demo@alpha.server JOIN #general alex-account :Alex Metadata User");
        demo.AlphaTransport.EnqueueInboundLine($"@time={serverTime} :alpha.server 353 nexAlpha = #general :@+Alex");
        demo.AlphaTransport.EnqueueInboundLine($"@time={serverTime} :Alex!demo@alpha.server AWAY :at lunch");
        demo.AlphaTransport.EnqueueInboundLine($"@time={serverTime} :alpha.server 366 nexAlpha #general :End of names");
        demo.AlphaTransport.EnqueueInboundLine($"@time={serverTime} :Alex!demo@alpha.server PRIVMSG #general :server-timed metadata");

        bool MetadataConverged()
        {
            var member = channel.Members.FirstOrDefault(candidate => candidate.Nickname == "Alex");
            return member is not null
                && member.Account == "alex-account"
                && member.RealName == "Alex Metadata User"
                && member.IsAway
                && member.PrefixModes.Contains('o')
                && member.PrefixModes.Contains('v')
                && channel.EntriesSnapshot.Any(entry => entry.Text == "server-timed metadata" && entry.Timestamp == expectedServerTimestamp);
        }

        try
        {
            await WaitForAsync(viewModel.Sessions, MetadataConverged, "IRCv3 metadata did not converge in the WPF projection").ConfigureAwait(true);
        }
        catch (TimeoutException exception)
        {
            var member = channel.Members.FirstOrDefault(candidate => candidate.Nickname == "Alex");
            throw new TimeoutException(
                $"{exception.Message} member={(member is null ? "<missing>" : $"account={member.Account ?? "<none>"}, real={member.RealName ?? "<none>"}, away={member.IsAway}, prefixes={string.Join(',', member.PrefixModes)}")}, entries={channel.EntriesSnapshot.Count}",
                exception);
        }

        var participant = RequiredMember(channel, "Alex");
        var context = viewModel.CreateParticipantContext(network, channel, participant);
        var actions = ParticipantActionCatalog.Build(context, isIgnored: false);
        Require(actions.SelectMany(group => group.Items).Single(item => item.Action == ParticipantActionKind.CopyAccount).IsEnabled,
            "IRCv3 account metadata did not enable Copy Account");
        Require(participant.DetailsText.Contains("alex-account", StringComparison.Ordinal)
            && participant.DetailsText.Contains("away: at lunch", StringComparison.Ordinal),
            "IRCv3 participant details did not expose current account and away state");
        Console.WriteLine($"IRCV3_UI_TRACE account={participant.Account} away={participant.IsAway} prefixes={participant.PrefixModes} server_time={serverTime}");
    }

    private static async Task ChathistoryAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var channel = RequiredChannel(network);
        var beforeMembers = channel.Members.Count;
        demo.AlphaTransport.EnqueueInboundLine("@msgid=live-boundary;time=2026-09-07T12:00:00.000Z :Alex!u@alpha PRIVMSG #general :live boundary");
        await WaitForAsync(window.ViewModel.Sessions, () => channel.EntriesSnapshot.Any(entry => entry.ServerMessageId == "live-boundary"), "CHATHISTORY smoke boundary did not arrive").ConfigureAwait(true);
        var beforeUnread = channel.UnreadCount;

        var descriptor = window.ViewModel.Actions.BuildChannelActions(network, channel)
            .Single(item => item.Action == WorkspaceActionId.LoadOlderMessages);
        Require(descriptor.IsEnabled, "CHATHISTORY older-history action was not enabled");
        CommandDispatchResult? result = null;
        Task<CommandDispatchResult>? pendingRequest = null;
        for (var page = 0; page < 64; page++)
        {
            pendingRequest = window.ViewModel.Actions.ExecuteChannelAsync(network, channel, WorkspaceActionId.LoadOlderMessages).AsTask();
            await Task.WhenAny(pendingRequest, Task.Delay(50)).ConfigureAwait(true);
            if (demo.AlphaTransport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BEFORE #general", StringComparison.Ordinal)))
            {
                break;
            }

            result = await pendingRequest.ConfigureAwait(true);
            await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
            Require(
                result.Succeeded,
                $"local history pagination failed before CHATHISTORY fallback: {result.Message}; outbound={string.Join(" | ", demo.AlphaTransport.OutboundLines.TakeLast(4))}");
        }

        Require(pendingRequest is not null && !pendingRequest.IsCompleted, "local history pagination did not reach a remote fallback request");
        await WaitForPollingAsync(() => demo.AlphaTransport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BEFORE #general", StringComparison.Ordinal)), "CHATHISTORY command was not sent").ConfigureAwait(true);
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server BATCH +history chathistory #general");
        demo.AlphaTransport.EnqueueInboundLine("@batch=history;msgid=live-boundary;time=2026-09-07T12:00:00.000Z :Alex!u@alpha PRIVMSG #general :live boundary");
        demo.AlphaTransport.EnqueueInboundLine("@batch=history :Alex!u@alpha PRIVMSG #general :legitimate repeat");
        demo.AlphaTransport.EnqueueInboundLine("@batch=history :Alex!u@alpha PRIVMSG #general :legitimate repeat");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server BATCH -history");

        result = await pendingRequest!.ConfigureAwait(true);
        await window.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        Require(result.Succeeded && result.Message.Contains("2 older", StringComparison.Ordinal), "CHATHISTORY playback feedback did not report the bounded merge");
        Require(channel.UnreadCount == beforeUnread, "CHATHISTORY playback changed unread state");
        Require(channel.Members.Count == beforeMembers, "CHATHISTORY playback changed current members");
        Require(channel.EntriesSnapshot.Count(entry => entry.Text == "legitimate repeat") == 2, "CHATHISTORY no-ID repeats were not retained");
        Require(channel.EntriesSnapshot.Count(entry => entry.ServerMessageId == "live-boundary") == 1, "CHATHISTORY authoritative overlap was not deduplicated");
        Console.WriteLine($"CHATHISTORY_UI_TRACE entries={channel.EntryCount} unread={channel.UnreadCount} members={channel.Members.Count}");
    }

    private static async Task HistoryPaginationAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var sessions = window.ViewModel.Sessions;
        var logs = sessions.LogStore ?? throw new InvalidOperationException("The history-pagination smoke requires the durable log store.");
        var conversationName = "#phase21-pagination";
        var conversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversationName);
        var scope = network.ProfileId ?? network.Id;
        var baseTime = DateTimeOffset.UnixEpoch.AddDays(20);
        for (var index = 1; index <= 300; index++)
        {
            await logs.AppendAsync(new ConversationLogRecord
            {
                Timestamp = baseTime.AddMinutes(index),
                NetworkId = network.Id,
                ScopeId = scope,
                ProfileId = network.ProfileId,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = conversationName,
                ConversationKey = conversationKey,
                Sender = "alice",
                MessageKind = LogMessageKind.Message,
                Direction = LogDirection.Incoming,
                Text = $"local-{index:000}",
                ServerMessageId = $"local-{index:000}",
                TimestampSource = ConversationTimestampSource.ServerTime
            }).ConfigureAwait(true);
        }

        await logs.FlushAsync().ConfigureAwait(true);
        var view = (ChannelView)sessions.OpenHistoricalConversation(network.Id, DestinationKind.Channel, conversationName);
        await WaitForPollingAsync(() => view.EntryCount == ConfigurationLimits.HistoryLocalProjectionPageSize, "history-pagination initial local page did not project").ConfigureAwait(true);
        Require(view.EntriesSnapshot[0].Text == "local-251" && view.EntriesSnapshot[^1].Text == "local-300", "initial local projection was not the newest bounded page");
        var chathistoryBefore = demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY", StringComparison.Ordinal));

        for (var page = 0; page < 5; page++)
        {
            var result = await sessions.LoadOlderMessagesAsync(network, view).ConfigureAwait(true);
            Require(result.Succeeded, "local-first pagination failed");
        }

        await sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        var localEntries = view.EntriesSnapshot;
        Require(localEntries.Count == 300, $"local pagination did not project all canonical rows (count={localEntries.Count}, first={(localEntries.Count == 0 ? "none" : localEntries[0].Text)}, last={(localEntries.Count == 0 ? "none" : localEntries[^1].Text)})");
        Require(localEntries[0].Text == "local-001" && localEntries[^1].Text == "local-300", $"local pagination ordering was not canonical (first={localEntries[0].Text}, last={localEntries[^1].Text})");
        Require(demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY", StringComparison.Ordinal)) == chathistoryBefore, "local pagination issued unexpected IRC history traffic");

        var remote = sessions.LoadOlderMessagesAsync(network, view).AsTask();
        await WaitForPollingAsync(() => demo.AlphaTransport.OutboundLines.Any(line => line.StartsWith($"CHATHISTORY BEFORE {conversationName}", StringComparison.Ordinal)), "remote pagination BEFORE request was not sent").ConfigureAwait(true);
        var coalesced = Enumerable.Range(0, 10)
            .Select(_ => sessions.LoadOlderMessagesAsync(network, view).AsTask())
            .ToArray();
        Require(coalesced.All(task => ReferenceEquals(task, remote)), "repeated top-scroll pagination requests did not coalesce");
        demo.AlphaTransport.EnqueueInboundLine($"@draft/chathistory-end :alpha.server BATCH +phase21-page chathistory {conversationName}");
        demo.AlphaTransport.EnqueueInboundLine($"@batch=phase21-page;msgid=remote-001;time={baseTime.AddMinutes(-1):O} :alice!u@alpha PRIVMSG {conversationName} :remote-001");
        demo.AlphaTransport.EnqueueInboundLine($"@batch=phase21-page;msgid=remote-001;time={baseTime.AddMinutes(-1):O} :alice!u@alpha PRIVMSG {conversationName} :remote-001 duplicate");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server BATCH -phase21-page");
        var remoteResult = await remote.ConfigureAwait(true);
        await sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        Require(
            remoteResult.Succeeded
            && view.EntriesSnapshot.Count(entry => entry.ServerMessageId == "remote-001") == 1,
            "remote pagination page was not canonically deduplicated and projected");
        Require(sessions.GetHistoryCoverage(network.Id, conversationKey).RemoteExhausted, "explicit server end did not establish remote exhaustion");

        var requestsAfterEnd = demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith($"CHATHISTORY BEFORE {conversationName}", StringComparison.Ordinal));
        await sessions.LoadOlderMessagesAsync(network, view).ConfigureAwait(true);
        Require(demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith($"CHATHISTORY BEFORE {conversationName}", StringComparison.Ordinal)) == requestsAfterEnd, "remote exhaustion did not suppress a repeated BEFORE request");
        Console.WriteLine($"HISTORY_PAGINATION_UI_TRACE local_pages=6 local_rows=300 remote_pages=1 coalesced_requests=10 end_reached=true repeated_before_suppressed=true");
    }

    private static async Task ForwardPaginationAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var sessions = window.ViewModel.Sessions;
        var logs = sessions.LogStore ?? throw new InvalidOperationException("The forward-pagination smoke requires the durable log store.");
        const string conversationName = "#phase22-forward";
        var conversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversationName);
        var scope = network.ProfileId ?? network.Id;
        var baseTime = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        for (var index = 1; index <= 500; index++)
        {
            await logs.AppendAsync(new ConversationLogRecord
            {
                Timestamp = baseTime.AddMinutes(index),
                NetworkId = network.Id,
                ScopeId = scope,
                ProfileId = network.ProfileId,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = conversationName,
                ConversationKey = conversationKey,
                Sender = "alice",
                MessageKind = LogMessageKind.Message,
                Direction = LogDirection.Incoming,
                Text = $"forward-{index:000}",
                ServerMessageId = $"forward-{index:000}",
                TimestampSource = ConversationTimestampSource.ServerTime
            }).ConfigureAwait(true);
        }

        await logs.FlushAsync().ConfigureAwait(true);
        var view = (ChannelView)sessions.OpenHistoricalConversation(network.Id, DestinationKind.Channel, conversationName);
        await WaitForPollingAsync(() => view.EntryCount == ConfigurationLimits.HistoryLocalProjectionPageSize, "forward-pagination initial local page did not project").ConfigureAwait(true);
        Require(view.EntriesSnapshot[^1].ServerMessageId == "forward-500", "forward-pagination did not open the newest local edge");

        await sessions.JumpToHistoryMessageAsync(network, view, "forward-125").ConfigureAwait(true);
        var localTraffic = demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY", StringComparison.Ordinal));
        var localPages = 0;
        while (view.EntriesSnapshot[^1].ServerMessageId != "forward-500")
        {
            var result = await sessions.LoadNewerMessagesAsync(network, view).ConfigureAwait(true);
            Require(result.Succeeded, "forward-pagination local newer page failed");
            localPages++;
            Require(localPages <= 10, "forward-pagination local newer pages did not converge");
        }

        Require(demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith("CHATHISTORY", StringComparison.Ordinal)) == localTraffic, "forward-pagination local pages contacted IRC");

        var firstRemote = sessions.LoadNewerMessagesAsync(network, view).AsTask();
        await WaitForPollingAsync(() => demo.AlphaTransport.OutboundLines.Any(line => line.StartsWith($"CHATHISTORY AFTER {conversationName} msgid=forward-500", StringComparison.Ordinal)), "forward-pagination first AFTER request was not sent").ConfigureAwait(true);
        demo.AlphaTransport.EnqueueInboundLine($":alpha.server BATCH +forward-one chathistory {conversationName}");
        for (var index = 501; index <= 525; index++)
        {
            demo.AlphaTransport.EnqueueInboundLine($"@batch=forward-one;msgid=forward-{index:000};time={baseTime.AddMinutes(index):O} :Alex!u@alpha PRIVMSG {conversationName} :forward-{index:000}");
        }

        demo.AlphaTransport.EnqueueInboundLine(":alpha.server BATCH -forward-one");
        var firstResult = await firstRemote.ConfigureAwait(true);
        Require(firstResult.Succeeded && view.EntriesSnapshot[^1].ServerMessageId == "forward-525", "forward-pagination first AFTER page did not advance the projected frontier");

        await logs.FlushAsync().ConfigureAwait(true);
        var secondRemote = sessions.LoadNewerMessagesAsync(network, view).AsTask();
        await WaitForPollingAsync(() => demo.AlphaTransport.OutboundLines.Any(line => line.StartsWith($"CHATHISTORY AFTER {conversationName} msgid=forward-525", StringComparison.Ordinal)), "forward-pagination second AFTER request did not advance its selector").ConfigureAwait(true);
        demo.AlphaTransport.EnqueueInboundLine($"@draft/chathistory-end :alpha.server BATCH +forward-two chathistory {conversationName}");
        for (var index = 526; index <= 550; index++)
        {
            demo.AlphaTransport.EnqueueInboundLine($"@batch=forward-two;msgid=forward-{index:000};time={baseTime.AddMinutes(index):O} :Alex!u@alpha PRIVMSG {conversationName} :forward-{index:000}");
        }

        demo.AlphaTransport.EnqueueInboundLine(":alpha.server BATCH -forward-two");
        var secondResult = await secondRemote.ConfigureAwait(true);
        Require(secondResult.Succeeded && view.EntriesSnapshot[^1].ServerMessageId == "forward-550", "forward-pagination terminal AFTER page did not reach the live edge");
        var afterRequests = demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith($"CHATHISTORY AFTER {conversationName}", StringComparison.Ordinal));
        var repeated = await sessions.LoadNewerMessagesAsync(network, view).ConfigureAwait(true);
        Require(repeated.Succeeded && afterRequests == demo.AlphaTransport.OutboundLines.Count(line => line.StartsWith($"CHATHISTORY AFTER {conversationName}", StringComparison.Ordinal)), "forward-pagination repeated AFTER request ignored directional exhaustion");
        Require(sessions.GetHistoryCoverage(network.Id, conversationKey).RemoteForwardExhausted, "forward-pagination end marker did not establish forward exhaustion");
        Console.WriteLine($"FORWARD_PAGINATION_UI_TRACE local_pages={localPages} local_traffic=0 remote_after_requests={afterRequests} frontier_advanced=true end_reached=true repeated_after_suppressed=true");
    }

    private static async Task HistoryNavigationAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var sessions = window.ViewModel.Sessions;
        var logs = sessions.LogStore ?? throw new InvalidOperationException("The history-navigation smoke requires the durable log store.");
        const string conversationName = "#phase22-navigation";
        var conversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversationName);
        var scope = network.ProfileId ?? network.Id;
        var baseTime = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        ConversationLogRecord Record(int index, string? text = null, string? messageId = null, DateTimeOffset? timestamp = null) => new()
        {
            Timestamp = timestamp ?? baseTime.AddMinutes(index),
            NetworkId = network.Id,
            ScopeId = scope,
            ProfileId = network.ProfileId,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = conversationName,
            ConversationKey = conversationKey,
            Sender = "alice",
            MessageKind = LogMessageKind.Message,
            Direction = LogDirection.Incoming,
            Text = text ?? $"nav-{index:0000}",
            ServerMessageId = messageId ?? $"nav-{index:0000}",
            TimestampSource = ConversationTimestampSource.ServerTime,
            Provenance = ConversationEntryProvenance.Live
        };

        for (var index = 1; index <= 1_000; index++)
        {
            await logs.AppendAsync(Record(index)).ConfigureAwait(true);
        }

        await logs.FlushAsync().ConfigureAwait(true);
        var view = (ChannelView)sessions.OpenHistoricalConversation(network.Id, DestinationKind.Channel, conversationName);
        await WaitForPollingAsync(() => view.EntryCount == ConfigurationLimits.HistoryLocalProjectionPageSize, "history-navigation newest local window did not project").ConfigureAwait(true);
        Require(view.EntriesSnapshot[0].ServerMessageId == "nav-0951" && view.EntriesSnapshot[^1].ServerMessageId == "nav-1000", "history-navigation did not open the newest 50 canonical rows");

        var trafficBeforeJump = demo.AlphaTransport.OutboundLines.Count;
        var localMessage = await sessions.JumpToHistoryMessageAsync(network, view, "nav-0425").ConfigureAwait(true);
        Require(localMessage.Outcome == HistoryNavigationOutcome.ExactLocalMatch && localMessage.IsExact, "local msgid navigation did not resolve exactly");
        Require(demo.AlphaTransport.OutboundLines.Count == trafficBeforeJump, "local msgid navigation issued IRC traffic");
        Require(view.EntriesSnapshot[0].ServerMessageId == "nav-0375" && view.EntriesSnapshot[^1].ServerMessageId == "nav-0475", "local msgid navigation did not project a bounded context window");
        Require(view.EntriesSnapshot.Count(entry => entry.IsNavigationAnchor) == 1 && view.NavigationAnchor?.ServerMessageId == "nav-0425", "local msgid navigation did not mark its anchor");

        var localTime = await sessions.JumpToHistoryTimestampAsync(network, view, baseTime.AddMinutes(700).AddSeconds(30)).ConfigureAwait(true);
        Require(localTime.Outcome == HistoryNavigationOutcome.NearestLocalMatch && localTime.Anchor.Anchor?.Record.ServerMessageId == "nav-0700", "timestamp navigation did not use the deterministic earlier tie-break");
        Require(demo.AlphaTransport.OutboundLines.Count == trafficBeforeJump, "local timestamp navigation issued IRC traffic");

        var localTrafficBeforePaging = demo.AlphaTransport.OutboundLines.Count;
        var localPages = 0;
        while (view.EntriesSnapshot[^1].ServerMessageId != "nav-1000")
        {
            var result = await sessions.LoadNewerMessagesAsync(network, view).ConfigureAwait(true);
            Require(result.Succeeded, "local newer navigation failed");
            localPages++;
            Require(localPages <= 10, "local newer navigation did not converge within the bounded fixture");
        }

        Require(demo.AlphaTransport.OutboundLines.Count == localTrafficBeforePaging, "local newer navigation contacted the server");
        await sessions.JumpToHistoryTimestampAsync(network, view, baseTime.AddMinutes(700).AddSeconds(30)).ConfigureAwait(true);
        var membersBeforeLive = channelState(view);
        var unreadBeforeLive = view.UnreadCount;

        for (var index = 1; index <= 5; index++)
        {
            demo.AlphaTransport.EnqueueInboundLine($"@msgid=nav-live-{index:000};time={baseTime.AddMinutes(1000 + index):O} :Alex!u@alpha PRIVMSG {conversationName} :live-{index:000}");
        }

        await WaitForAsync(sessions, () => view.EntriesSnapshot.Count(entry => entry.ServerMessageId is not null && entry.ServerMessageId.StartsWith("nav-live-", StringComparison.Ordinal)) == 5, "live messages did not reach the history view").ConfigureAwait(true);
        Require(view.IsViewingHistory && !view.IsFollowingLive && view.HasNewerLiveMessages, "live traffic pulled the viewport away from the historical position");
        Require(view.UnreadCount == unreadBeforeLive, "historical navigation changed unread state while live traffic arrived");
        Require(channelState(view) == membersBeforeLive, "historical navigation changed current channel state");

        await logs.FlushAsync().ConfigureAwait(true);
        var returned = await sessions.ReturnToLatestAsync(network, view).ConfigureAwait(true);
        Require(returned.Outcome == HistoryNavigationOutcome.LocalEndReached && view.IsFollowingLive && !view.IsViewingHistory, "return-to-latest did not restore live-follow mode");
        Require(view.NavigationAnchor is null && view.EntriesSnapshot[^1].ServerMessageId == "nav-live-005", "return-to-latest did not project the current canonical edge");
        Require(view.UnreadCount == unreadBeforeLive, "return-to-latest changed unread state");

        var trafficBeforeRemote = demo.AlphaTransport.OutboundLines.Count;
        var remoteJump = sessions.JumpToHistoryMessageAsync(network, view, "nav-remote-0404").AsTask();
        await WaitForPollingAsync(() => demo.AlphaTransport.OutboundLines.Any(line => line.StartsWith($"CHATHISTORY AROUND {conversationName} msgid=nav-remote-0404", StringComparison.Ordinal)), "remote AROUND navigation request was not sent").ConfigureAwait(true);
        demo.AlphaTransport.EnqueueInboundLine($":alpha.server BATCH +phase22-around chathistory {conversationName}");
        demo.AlphaTransport.EnqueueInboundLine($"@batch=phase22-around;msgid=nav-remote-0405;time={baseTime.AddMinutes(2000):O} :Alex!u@alpha PRIVMSG {conversationName} :remote-405");
        demo.AlphaTransport.EnqueueInboundLine($"@batch=phase22-around;draft/chathistory-context=1;msgid=nav-remote-0403;time={baseTime.AddMinutes(1999):O} :Alex!u@alpha PRIVMSG {conversationName} :remote-403 context");
        demo.AlphaTransport.EnqueueInboundLine($"@batch=phase22-around;msgid=nav-remote-0404;time={baseTime.AddMinutes(2000):O} :Alex!u@alpha PRIVMSG {conversationName} :remote-404");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server BATCH -phase22-around");
        var remoteResult = await remoteJump.ConfigureAwait(true);
        Require(remoteResult.Outcome == HistoryNavigationOutcome.RemotelyRetrievedExactMatch && remoteResult.IsExact, "remote AROUND navigation did not establish the returned exact anchor");
        Require(view.NavigationAnchor?.ServerMessageId == "nav-remote-0404", "remote AROUND navigation did not position the returned anchor");
        var trafficAfterRemote = demo.AlphaTransport.OutboundLines.Count;
        var repeatedRemoteJump = await sessions.JumpToHistoryMessageAsync(network, view, "nav-remote-0404").ConfigureAwait(true);
        Require(repeatedRemoteJump.Outcome == HistoryNavigationOutcome.ExactLocalMatch && demo.AlphaTransport.OutboundLines.Count == trafficAfterRemote, "a retrieved remote anchor was not local-only on repeat");
        Require(trafficAfterRemote == trafficBeforeRemote + 1, "remote AROUND navigation issued more than one request");
        Console.WriteLine($"HISTORY_NAVIGATION_UI_TRACE local_msgid_exact=true local_timestamp_tie=earlier local_newer_pages={localPages} remote_around_requests=1 repeat_local=true live_follow_restored=true unread_unchanged=true current_state_unchanged=true");

        static string channelState(ChannelView channel) =>
            string.Join("|", channel.MembersSnapshot.Select(member => $"{member.Nickname}:{string.Join(',', member.PrefixModes.OrderBy(value => value))}").OrderBy(value => value, StringComparer.Ordinal))
            + $";topic={channel.Topic};modes={string.Concat(channel.Modes.OrderBy(value => value))};sync={channel.Synchronization}";
    }

    private static async Task HistoryGapRepairAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var sessions = window.ViewModel.Sessions;
        var channel = RequiredChannel(network);
        demo.AlphaTransport.EnqueueInboundLine("@msgid=gap-a;time=2026-09-07T12:00:00.000Z :Alex!u@alpha PRIVMSG #general :A");
        demo.AlphaTransport.EnqueueInboundLine("@msgid=gap-b;time=2026-09-07T12:01:00.000Z :Alex!u@alpha PRIVMSG #general :B");
        await WaitForAsync(sessions, () => channel.EntriesSnapshot.Count(entry => entry.ServerMessageId is "gap-a" or "gap-b") == 2, "history gap pre-disconnect anchors did not arrive").ConfigureAwait(true);

        var replacement = demo.AddAlphaReconnectTransport();
        demo.AlphaTransport.EnqueueRemoteDisconnect();
        await WaitForAsync(sessions, () => replacement.ConnectCount == 1 && channel.IsStale, "history gap reconnect did not establish a new generation").ConfigureAwait(true);
        demo.EnqueuePhase1YRegistration(replacement);
        replacement.EnqueueInboundLine(":nexAlpha!demo@alpha.server JOIN #general");
        replacement.EnqueueInboundLine("@msgid=gap-f;time=2026-09-07T12:05:00.000Z :Alex!u@alpha PRIVMSG #general :F");
        try
        {
            await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BETWEEN #general msgid=gap-b msgid=gap-f ", StringComparison.Ordinal)), "exact BETWEEN gap repair was not sent").ConfigureAwait(true);
        }
        catch (TimeoutException exception)
        {
            var gaps = string.Join(" | ", sessions.GetHistoryGaps(network.Id).Select(gap => $"{gap.State}:{gap.LastReason}"));
            throw new TimeoutException($"{exception.Message}; outbound={string.Join(" | ", replacement.OutboundLines)}; gaps={gaps}", exception);
        }

        var unreadBeforeHistory = channel.UnreadCount;

        replacement.EnqueueInboundLine("@draft/chathistory-end :alpha.server BATCH +gap-repair chathistory #general");
        replacement.EnqueueInboundLine("@batch=gap-repair;msgid=gap-c;time=2026-09-07T12:02:00.000Z :Alex!u@alpha PRIVMSG #general :C");
        replacement.EnqueueInboundLine("@batch=gap-repair;msgid=gap-d;time=2026-09-07T12:03:00.000Z :Alex!u@alpha PRIVMSG #general :D");
        replacement.EnqueueInboundLine("@batch=gap-repair;msgid=gap-e;time=2026-09-07T12:04:00.000Z :Alex!u@alpha PRIVMSG #general :E");
        replacement.EnqueueInboundLine("@batch=gap-repair;msgid=gap-b;time=2026-09-07T12:01:00.000Z :Alex!u@alpha PRIVMSG #general :B");
        replacement.EnqueueInboundLine(":alpha.server BATCH -gap-repair");

        await WaitForAsync(
            sessions,
            () =>
            {
                var texts = channel.EntriesSnapshot
                    .Where(entry => entry.ServerMessageId is not null)
                    .Select(entry => entry.Text)
                    .ToHashSet(StringComparer.Ordinal);
                return new[] { "A", "B", "C", "D", "E", "F" }.All(texts.Contains);
            },
            "exact gap history did not converge in canonical chronology").ConfigureAwait(true);
        await WaitForPollingAsync(() => sessions.GetHistoryGaps(network.Id).SingleOrDefault()?.State == HistoryGapRepairState.Repaired, "exact gap repair did not reach repaired state").ConfigureAwait(true);
        Require(channel.EntriesSnapshot.Count(entry => entry.ServerMessageId == "gap-b") == 1, "the repeated older boundary was projected twice");
        Require(channel.EntriesSnapshot.Count(entry => entry.ServerMessageId == "gap-f") == 1, "the newer live boundary was projected twice");
        Require(channel.UnreadCount == unreadBeforeHistory, "historical gap playback changed unread state");
        Require(replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN #general", StringComparison.Ordinal)) == 1, "the repaired gap triggered more than one exact network request");
        Console.WriteLine($"HISTORY_GAP_REPAIR_UI_TRACE state=repaired between_requests=1 chronology=A,B,C,D,E,F unread_unchanged=true");
    }

    private static async Task HistoryIntegrityAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var sessions = viewModel.Sessions;
        var logs = sessions.LogStore ?? throw new InvalidOperationException("The history-integrity smoke requires the durable log store.");
        var query = sessions.EnsureQuery(network.Id, "Alice");
        var aliceKey = query.HistoryConversationKey;
        viewModel.SelectView(network.StatusView);

        demo.AlphaTransport.EnqueueInboundLine("@account=alice123;msgid=integrity-b;time=2026-09-07T12:01:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :B");
        await WaitForAsync(sessions, () => query.IdentityEvidence.HasAccount("alice123") && query.EntriesSnapshot.Any(entry => entry.ServerMessageId == "integrity-b"), "account-backed pre-disconnect query evidence did not arrive").ConfigureAwait(true);
        await logs.FlushAsync().ConfigureAwait(true);
        var beforeSearch = await logs.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = network.ProfileId ?? network.Id,
            NetworkId = network.Id,
            ConversationKind = LogConversationKind.PrivateConversation,
            ConversationName = "Alice",
            ConversationKey = aliceKey,
            Text = "B",
            MaximumResults = 10
        }).ConfigureAwait(true);
        Require(beforeSearch.Results.Count == 1, "history-integrity could not search the pre-disconnect durable query");

        var replacement = demo.AddAlphaReconnectTransport();
        demo.AlphaTransport.EnqueueRemoteDisconnect();
        await WaitForAsync(sessions, () => replacement.ConnectCount == 1, "history-integrity reconnect did not establish a replacement session").ConfigureAwait(true);
        demo.EnqueuePhase1YRegistration(replacement);
        replacement.EnqueueInboundLine("@account=alice123;msgid=integrity-f;time=2026-09-07T12:05:00.000Z :Alicia!u@alpha PRIVMSG nexAlpha :F");
        await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BETWEEN Alicia msgid=integrity-b msgid=integrity-f ", StringComparison.Ordinal)), "history-integrity did not bind the safe current Alicia target to the durable Alice query").ConfigureAwait(true);
        replacement.EnqueueInboundLine("@draft/chathistory-end :alpha.server BATCH +integrity-gap chathistory Alicia");
        replacement.EnqueueInboundLine("@batch=integrity-gap;msgid=integrity-c;time=2026-09-07T12:03:00.000Z :Alicia!u@alpha PRIVMSG nexAlpha :C");
        replacement.EnqueueInboundLine("@batch=integrity-gap;msgid=integrity-d;time=2026-09-07T12:03:30.000Z :Alicia!u@alpha PRIVMSG nexAlpha :D");
        replacement.EnqueueInboundLine("@batch=integrity-gap;msgid=integrity-e;time=2026-09-07T12:04:00.000Z :Alicia!u@alpha PRIVMSG nexAlpha :E");
        replacement.EnqueueInboundLine("@batch=integrity-gap;msgid=integrity-b;time=2026-09-07T12:01:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :B");
        replacement.EnqueueInboundLine(":alpha.server BATCH -integrity-gap");

        await WaitForAsync(sessions, () => query.Nickname == "Alicia"
            && query.EntriesSnapshot.Where(entry => entry.ServerMessageId is not null).Select(entry => entry.Text).SequenceEqual(["B", "C", "D", "E", "F"]), "history-integrity did not converge the account-backed exact gap once").ConfigureAwait(true);
        await WaitForPollingAsync(() => sessions.GetHistoryGaps(network.Id).SingleOrDefault()?.State == HistoryGapRepairState.Repaired, "history-integrity exact gap was not marked repaired").ConfigureAwait(true);
        Require(query.HistoryConversationKey == aliceKey && sessions.GetHistoryGaps(network.Id).Count == 1, "history-integrity changed durable ownership or created duplicate gap state");

        await logs.FlushAsync().ConfigureAwait(true);
        var trafficBeforeNavigation = replacement.OutboundLines.Count;
        var dSearch = await logs.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = network.ProfileId ?? network.Id,
            NetworkId = network.Id,
            ConversationKind = LogConversationKind.PrivateConversation,
            ConversationName = "Alicia",
            ConversationKey = aliceKey,
            Text = "D",
            MaximumResults = 10
        }).ConfigureAwait(true);
        var dResult = dSearch.Results.SingleOrDefault() ?? throw new InvalidOperationException("history-integrity search did not find repaired D");
        Require(await viewModel.RouteLogSearchResultAsync(dResult).ConfigureAwait(true)
            && ReferenceEquals(viewModel.ActiveView, query)
            && query.NavigationAnchor?.Text == "D"
            && query.EntriesSnapshot.Any(entry => entry.Text == "D")
            && replacement.OutboundLines.Count == trafficBeforeNavigation,
            "history-integrity search-to-navigation did not remain on the durable query or local path");

        var betweenCount = replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN", StringComparison.Ordinal));
        replacement.EnqueueInboundLine("@account=other;msgid=integrity-conflict;time=2026-09-07T12:06:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :conflicting Alice");
        await WaitForAsync(sessions, () => network.Queries.Count == 2, "history-integrity did not isolate the conflicting current Alice").ConfigureAwait(true);
        Require(!query.EntriesSnapshot.Any(entry => entry.Text == "conflicting Alice")
            && replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN", StringComparison.Ordinal)) == betweenCount,
            "history-integrity let a conflicting account contaminate the repaired query");

        await logs.FlushAsync().ConfigureAwait(true);
        var oldSearch = await logs.SearchAsync(new ConversationLogQuery { Text = "B", NetworkId = network.Id, ConversationKey = aliceKey });
        var oldResult = oldSearch.Single(result => result.ServerMessageId == "integrity-b");
        Require(await viewModel.RouteLogSearchResultAsync(oldResult).ConfigureAwait(true)
            && ReferenceEquals(viewModel.ActiveView, query)
            && query.Nickname == "Alicia", "history-integrity historical Alice result did not reopen Alicia continuity");

        var latest = await sessions.ReturnToLatestAsync(network, query).ConfigureAwait(true);
        Require(latest.Outcome == HistoryNavigationOutcome.LocalEndReached && query.IsFollowingLive, "history-integrity Return to Latest did not restore live follow state");
        Console.WriteLine($"HISTORY_INTEGRITY_UI_TRACE durable_key={aliceKey} chronology=B,C,D,E,F between_requests={betweenCount} search_d_local=true navigation_same_query=true conflicting_account_isolated=true return_to_latest=true irc_after_repair={(replacement.OutboundLines.Count - trafficBeforeNavigation)}");
    }

    private static async Task HistorySearchAsync(
        MainWindow window,
        DemoScenario demo,
        NetworkWorkspace alpha,
        NetworkWorkspace beta)
    {
        var viewModel = window.ViewModel;
        var sessions = viewModel.Sessions;
        var logs = sessions.LogStore ?? throw new InvalidOperationException("The history-search smoke requires the durable log store.");
        var alphaScope = alpha.ProfileId ?? alpha.Id;
        var betaScope = beta.ProfileId ?? beta.Id;
        var alphaKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#alpha");
        var betaKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#lounge");
        var baseTime = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        ConversationLogRecord Record(
            NetworkWorkspace network,
            Guid scope,
            LogConversationKind conversationKind,
            string conversationName,
            string conversationKey,
            string text,
            string sender,
            LogMessageKind messageKind = LogMessageKind.Message,
            string? messageId = null,
            DateTimeOffset? timestamp = null) => new()
            {
                Timestamp = timestamp ?? baseTime,
                NetworkId = network.Id,
                ScopeId = scope,
                ProfileId = network.ProfileId,
                ConversationKind = conversationKind,
                ConversationName = conversationName,
                ConversationKey = conversationKey,
                Sender = sender,
                MessageKind = messageKind,
                Direction = LogDirection.Incoming,
                Text = text,
                ServerMessageId = messageId,
                TimestampSource = ConversationTimestampSource.ServerTime,
                Provenance = ConversationEntryProvenance.Live
            };

        for (var index = 0; index < 600; index++)
        {
            await logs.AppendAsync(Record(
                alpha,
                alphaScope,
                LogConversationKind.Channel,
                "#alpha",
                alphaKey,
                $"kernel search row {index:000}",
                index % 2 == 0 ? "Alice" : "Rook",
                timestamp: baseTime.AddMinutes(index))).ConfigureAwait(true);
        }

        var alphaUnique = Record(
            alpha,
            alphaScope,
            LogConversationKind.Channel,
            "#alpha",
            alphaKey,
            "phase twenty three unique message",
            "Alice",
            messageId: "phase23-alpha-unique",
            timestamp: baseTime.AddMinutes(-2000));
        var betaUnique = Record(
            beta,
            betaScope,
            LogConversationKind.Channel,
            "#lounge",
            betaKey,
            "phase twenty three unique message",
            "Alice",
            messageId: "phase23-beta-unique",
            timestamp: baseTime.AddMinutes(-1999));
        var historicalEvent = Record(
            alpha,
            alphaScope,
            LogConversationKind.Channel,
            "#alpha",
            alphaKey,
            "phase twenty three unique event",
            "Oper",
            LogMessageKind.Topic,
            "phase23-topic",
            baseTime.AddMinutes(-1998));
        var aliceKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.PrivateConversation, "Alice");
        var aliceHistory = Record(
            alpha,
            alphaScope,
            LogConversationKind.PrivateConversation,
            "Alice",
            aliceKey,
            "Alice account alice123 historical message",
            "Alice",
            messageId: "phase23-alice",
            timestamp: baseTime.AddMinutes(-1997));

        await logs.AppendAsync(alphaUnique).ConfigureAwait(true);
        await logs.AppendAsync(betaUnique).ConfigureAwait(true);
        await logs.AppendAsync(historicalEvent).ConfigureAwait(true);
        await logs.AppendAsync(aliceHistory).ConfigureAwait(true);
        await logs.FlushAsync().ConfigureAwait(true);

        var current = await logs.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = alphaScope,
            NetworkId = alpha.Id,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#alpha",
            ConversationKey = alphaKey,
            Text = "kernel",
            MaximumResults = 50
        }).ConfigureAwait(true);
        Require(current.Results.Count == 50 && current.Statistics.ResultsTruncated, "history-search did not bound common-term results");

        var crossNetwork = await logs.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.AllHistory,
            Text = "phase twenty three unique message",
            MaximumResults = 10
        }).ConfigureAwait(true);
        Require(crossNetwork.Results.Count == 2
            && crossNetwork.Results.Select(result => result.NetworkId).Distinct().Count() == 2, "history-search did not isolate same-text results by network");

        var historicalQuery = (QueryView)sessions.OpenHistoricalConversation(alpha.Id, DestinationKind.Query, "Alicia", aliceKey);
        var aliceSearch = await logs.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.AllHistory,
            Text = "Alice account alice123",
            Sender = "Alice",
            MaximumResults = 10
        }).ConfigureAwait(true);
        var aliceResult = AssertSingle(aliceSearch.Results, "history-search Alice result");
        var queryOpened = await viewModel.RouteLogSearchResultAsync(aliceResult).ConfigureAwait(true);
        Require(queryOpened && ReferenceEquals(viewModel.ActiveView, historicalQuery) && historicalQuery.Nickname == "Alicia", "history-search did not preserve the durable query identity");

        var target = crossNetwork.Results.Single(result => result.NetworkId == alpha.Id);
        var channel = (ChannelView)sessions.OpenHistoricalConversation(alpha.Id, DestinationKind.Channel, "#alpha", alphaKey);
        var trafficBeforeNavigation = demo.AlphaTransport.OutboundLines.Count;
        var opened = await viewModel.RouteLogSearchResultAsync(target).ConfigureAwait(true);
        Require(opened
            && ReferenceEquals(viewModel.ActiveView, channel)
            && channel.NavigationAnchor?.ServerMessageId == target.ServerMessageId
            && channel.EntryCount is > 0 and <= 101,
            "history-search did not navigate a trimmed transcript through the canonical anchor");
        Require(demo.AlphaTransport.OutboundLines.Count == trafficBeforeNavigation, "history-search local result navigation issued IRC traffic");

        var topicBefore = channel.Topic;
        var eventSearch = await logs.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = alphaScope,
            NetworkId = alpha.Id,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#alpha",
            ConversationKey = alphaKey,
            Text = "phase twenty three unique event",
            MaximumResults = 5
        }).ConfigureAwait(true);
        var eventResult = AssertSingle(eventSearch.Results, "history-search event result");
        Require(eventResult.EventType == LogMessageKind.Topic, "history-search did not classify the historical event");
        Require(await viewModel.RouteLogSearchResultAsync(eventResult).ConfigureAwait(true)
            && channel.NavigationAnchor?.ServerMessageId == "phase23-topic"
            && channel.Topic == topicBefore, "historical event navigation changed current channel state");

        var messageJump = await viewModel.NavigateHistoryMessageAsync(
            alpha.Id,
            LogConversationKind.Channel,
            "#alpha",
            alphaKey,
            "  phase23-alpha-unique  ").ConfigureAwait(true);
        Require(messageJump.Outcome == HistoryNavigationOutcome.ExactLocalMatch
            && demo.AlphaTransport.OutboundLines.Count == trafficBeforeNavigation, "history-search msgid jump did not remain local and exact");

        var timestampJump = await viewModel.NavigateHistoryTimestampAsync(
            alpha.Id,
            LogConversationKind.Channel,
            "#alpha",
            alphaKey,
            target.Timestamp).ConfigureAwait(true);
        Require(timestampJump.IsExact, "history-search UTC timestamp jump did not resolve the exact anchor");

        var latest = await sessions.ReturnToLatestAsync(alpha, channel).ConfigureAwait(true);
        Require(latest.Outcome == HistoryNavigationOutcome.LocalEndReached && channel.IsFollowingLive, "history-search did not restore Return to Latest");
        Console.WriteLine($"HISTORY_SEARCH_UI_TRACE common_results={current.Results.Count} common_truncated={current.Statistics.ResultsTruncated} cross_network_results={crossNetwork.Results.Count} trimmed_context={channel.EntryCount} local_irc_requests=0 durable_query_continuity=true historical_event_firewall=true return_to_latest=true");

        static ConversationLogSearchResult AssertSingle(IReadOnlyList<ConversationLogSearchResult> results, string label) =>
            results.Count == 1 ? results[0] : throw new InvalidOperationException($"{label} expected one result but received {results.Count}.");
    }

    private static void StaleSearchAsync()
    {
        var generations = new HistorySearchGeneration();
        var first = generations.Begin();
        var second = generations.Begin();
        Require(!generations.IsCurrent(first) && generations.IsCurrent(second), "stale-search allowed an older completion to own the result surface");
        generations.Invalidate();
        Require(!generations.IsCurrent(second), "stale-search allowed a completion after surface invalidation");
        Console.WriteLine("STALE_SEARCH_UI_TRACE first_completion_ignored=true second_completion_owned=true close_invalidation=true");
    }

    private static async Task IndexRecoveryAsync()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase23-index-recovery-");
        try
        {
            var network = Guid.NewGuid();
            await using var logs = new JsonlConversationLogStore(root.FullName, maximumSegmentBytes: 64 * 1024 * 1024);
            var scope = Guid.NewGuid();
            var conversation = "#recovery";
            var conversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversation);
            for (var index = 0; index < 5_000; index++)
            {
                await logs.AppendAsync(new ConversationLogRecord
                {
                    Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(index),
                    NetworkId = network,
                    ScopeId = scope,
                    ProfileId = scope,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = conversation,
                    ConversationKey = conversationKey,
                    Sender = "Recovery",
                    MessageKind = LogMessageKind.Message,
                    Direction = LogDirection.Incoming,
                    Text = $"index recovery canonical marker {index:0000} {new string('x', 240)}",
                    ServerMessageId = $"index-recovery-{index:0000}"
                }).ConfigureAwait(true);
            }

            await logs.FlushAsync().ConfigureAwait(true);
            var query = new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                HistoryScopeId = scope,
                NetworkId = network,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = conversation,
                ConversationKey = conversationKey,
                Text = "index recovery canonical marker",
                MaximumResults = 10
            };
            var baseline = await logs.SearchDetailedAsync(query).ConfigureAwait(true);
            var sourceLengths = Directory.EnumerateFiles(logs.RootPath, "*.jsonl", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);
            var sidecars = Directory.EnumerateFiles(logs.RootPath, "*.hsidx", SearchOption.AllDirectories).ToArray();
            foreach (var sidecar in sidecars)
            {
                File.Delete(sidecar);
            }

            await using var reopened = new JsonlConversationLogStore(logs.RootPath, maximumSegmentBytes: 64 * 1024 * 1024);
            var recovered = await reopened.SearchDetailedAsync(query).ConfigureAwait(true);
            var unchanged = sourceLengths.All(item => File.Exists(item.Key) && new FileInfo(item.Key).Length == item.Value);
            Require(
                baseline.Results.Count > 0 && recovered.Results.Count > 0 && recovered.Statistics.IndexFilesBuilt > 0 && unchanged,
                $"index-recovery did not rebuild from unchanged canonical JSONL (baseline={baseline.Results.Count}, results={recovered.Results.Count}, built={recovered.Statistics.IndexFilesBuilt}, files={recovered.Statistics.FilesExamined}, skipped={recovered.Statistics.FilesSkipped}, unchanged={unchanged})");
            Console.WriteLine($"INDEX_RECOVERY_UI_TRACE deleted_sidecars={sidecars.Length} rebuilt={recovered.Statistics.IndexFilesBuilt} canonical_sources_unchanged={unchanged}");
        }
        finally
        {
            try
            {
                Directory.Delete(root.FullName, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task AccessibilityAsync(MainWindow owner, NetworkWorkspace network)
    {
        var historyWindow = new LogViewerWindow(owner.ViewModel, RequiredChannel(network)) { Owner = owner };
        try
        {
            historyWindow.Show();
            historyWindow.UpdateLayout();

            var query = (System.Windows.Controls.TextBox)historyWindow.FindName("QueryBox")!;
            var scope = (System.Windows.Controls.ComboBox)historyWindow.FindName("ScopeBox")!;
            var networkBox = (System.Windows.Controls.ComboBox)historyWindow.FindName("NetworkBox")!;
            var conversation = (System.Windows.Controls.TextBox)historyWindow.FindName("ConversationBox")!;
            var from = (System.Windows.Controls.TextBox)historyWindow.FindName("FromDateBox")!;
            var to = (System.Windows.Controls.TextBox)historyWindow.FindName("ToDateBox")!;
            var jumpDate = (System.Windows.Controls.TextBox)historyWindow.FindName("JumpDateBox")!;
            var msgid = (System.Windows.Controls.TextBox)historyWindow.FindName("MsgidBox")!;
            var results = (System.Windows.Controls.ListBox)historyWindow.FindName("ResultsList")!;
            var goToMsgid = (System.Windows.Controls.Button)historyWindow.FindName("GoToMsgidButton")!;

            Require(System.Windows.Automation.AutomationProperties.GetAutomationId(historyWindow) == "HistorySearchWindow"
                && System.Windows.Automation.AutomationProperties.GetName(historyWindow) == "IRC history and search",
                "history search window accessibility identity is incomplete");
            Require(
                new (System.Windows.FrameworkElement Element, string Id)[]
                {
                    (query, "HistorySearch.Query"),
                    (scope, "HistorySearch.Scope"),
                    (networkBox, "HistorySearch.Network"),
                    (conversation, "HistorySearch.Conversation"),
                    (from, "HistorySearch.FromUtc"),
                    (to, "HistorySearch.ToUtc"),
                    (jumpDate, "HistorySearch.JumpUtcInput"),
                    (msgid, "HistorySearch.MsgidInput"),
                    (results, "HistorySearch.Results")
                }.All(item => System.Windows.Automation.AutomationProperties.GetAutomationId(item.Element) == item.Id),
                "history search controls are missing stable accessibility identities");
            Require(
                new System.Windows.FrameworkElement[] { query, scope, networkBox, conversation, from, to, jumpDate, msgid, results }
                    .Select(element => System.Windows.Input.KeyboardNavigation.GetTabIndex(element))
                    .SequenceEqual(new[] { 0, 1, 2, 3, 9, 10, 15, 17, 20 }),
                "history search keyboard tab order is not deterministic");

            var record = new ConversationLogRecord
            {
                Timestamp = DateTimeOffset.UnixEpoch,
                NetworkId = network.Id,
                ScopeId = network.ProfileId ?? network.Id,
                ProfileId = network.ProfileId,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = RequiredChannel(network).Channel,
                ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, RequiredChannel(network).Channel),
                Text = "accessibility result"
            };
            var resultItem = new LogResultListItem(new ConversationLogSearchResult(record, record.Text), network.DisplayName);
            results.ItemsSource = new[] { resultItem };
            historyWindow.UpdateLayout();
            var container = (System.Windows.Controls.ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(0)!;
            Require(System.Windows.Automation.AutomationProperties.GetName(container) == resultItem.DisplayText, "history search result item is not exposed as a named list item");

            msgid.Text = " ";
            goToMsgid.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await owner.ViewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
            Require(ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, msgid), "invalid msgid input did not return focus to the editable field");
            Console.WriteLine("ACCESSIBILITY_UI_TRACE automation_ids=true tab_order=true result_item_named=true invalid_msgid_focus=true deterministic_wpf_properties=true");
        }
        finally
        {
            historyWindow.Close();
        }
    }

    private static async Task IndexFingerprintAsync()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase24-index-fingerprint-");
        try
        {
            var network = Guid.NewGuid();
            var scope = Guid.NewGuid();
            const string conversation = "#fingerprint";
            const string oldMarker = "phase24-old-anchor";
            const string newMarker = "phase24-new-anchor";
            var conversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversation);
            long coldBuildMilliseconds;
            await using (var logs = new JsonlConversationLogStore(root.FullName, maximumSegmentBytes: 64 * 1024 * 1024))
            {
                for (var index = 0; index < 50_000; index++)
                {
                    await logs.AppendAsync(new ConversationLogRecord
                    {
                        Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(index),
                        NetworkId = network,
                        ScopeId = scope,
                        ProfileId = scope,
                        ConversationKind = LogConversationKind.Channel,
                        ConversationName = conversation,
                        ConversationKey = conversationKey,
                        Sender = "Fingerprint",
                        MessageKind = LogMessageKind.Message,
                        Direction = LogDirection.Incoming,
                        Text = index == 49_123 ? oldMarker : $"fingerprint fixture row {index:00000}",
                        ServerMessageId = $"fingerprint-{index:00000}"
                    }).ConfigureAwait(true);
                }

                await logs.FlushAsync().ConfigureAwait(true);
                var baselineWatch = Stopwatch.StartNew();
                var baseline = await logs.SearchDetailedAsync(new ConversationLogQuery
                {
                    Scope = ConversationLogSearchScope.CurrentConversation,
                    HistoryScopeId = scope,
                    NetworkId = network,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = conversation,
                    ConversationKey = conversationKey,
                    Text = oldMarker,
                    MaximumResults = 10
                }).ConfigureAwait(true);
                baselineWatch.Stop();
                coldBuildMilliseconds = baselineWatch.ElapsedMilliseconds;
                Require(baseline.Results.Count == 1 && baseline.Statistics.IndexFilesBuilt > 0, "index-fingerprint could not build its baseline sidecar");
            }

            var source = Directory.EnumerateFiles(root.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
            var sourceLength = new FileInfo(source).Length;
            var sourceMtime = File.GetLastWriteTimeUtc(source);
            var replacement = (await File.ReadAllTextAsync(source).ConfigureAwait(true)).Replace(oldMarker, newMarker, StringComparison.Ordinal);
            Require(oldMarker.Length == newMarker.Length
                && !replacement.Contains(oldMarker, StringComparison.Ordinal)
                && replacement.Contains(newMarker, StringComparison.Ordinal),
                "index-fingerprint replacement changed fixture shape unexpectedly");
            await File.WriteAllTextAsync(source, replacement).ConfigureAwait(true);
            File.SetLastWriteTimeUtc(source, sourceMtime);

            await using var reopened = new JsonlConversationLogStore(root.FullName, maximumSegmentBytes: 64 * 1024 * 1024);
            var oldWatch = Stopwatch.StartNew();
            var oldResults = await reopened.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                HistoryScopeId = scope,
                NetworkId = network,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = conversation,
                ConversationKey = conversationKey,
                Text = oldMarker,
                MaximumResults = 10
            }).ConfigureAwait(true);
            oldWatch.Stop();
            var newWatch = Stopwatch.StartNew();
            var newResults = await reopened.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                HistoryScopeId = scope,
                NetworkId = network,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = conversation,
                ConversationKey = conversationKey,
                Text = newMarker,
                MaximumResults = 10
            }).ConfigureAwait(true);
            newWatch.Stop();
            Require(oldResults.Results.Count == 0
                && oldResults.Statistics.IndexFilesBuilt > 0
                && newResults.Results.Count == 1
                && newResults.Results[0].Preview.Contains(newMarker, StringComparison.Ordinal)
                && newResults.Statistics.IndexFilesUsed > 0
                && new FileInfo(source).Length == sourceLength,
                "index-fingerprint accepted stale canonical offsets or changed the JSONL source");
            Console.WriteLine($"INDEX_FINGERPRINT_UI_TRACE rows=50000 cold_build_ms={coldBuildMilliseconds} replacement_rebuild_ms={oldWatch.ElapsedMilliseconds} warm_new_ms={newWatch.ElapsedMilliseconds} old_results=0 new_results=1 preserved_mtime=true canonical_length_unchanged=true");
        }
        finally
        {
            try
            {
                Directory.Delete(root.FullName, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task HistoryDiscoveryAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var sessions = window.ViewModel.Sessions;
        var channel = RequiredChannel(network);
        var knownQuery = sessions.EnsureQuery(network.Id, "Alice");
        window.ViewModel.SelectView(network.StatusView);
        demo.AlphaTransport.EnqueueInboundLine("@msgid=pre-channel;time=2026-09-07T11:58:00.000Z :Alex!u@alpha PRIVMSG #general :before channel reconnect");
        demo.AlphaTransport.EnqueueInboundLine("@msgid=pre-disconnect;time=2026-09-07T11:59:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :before reconnect");
        await WaitForAsync(sessions, () => channel.EntriesSnapshot.Any(entry => entry.ServerMessageId == "pre-channel") && knownQuery.EntriesSnapshot.Any(entry => entry.ServerMessageId == "pre-disconnect"), "pre-disconnect history evidence did not arrive").ConfigureAwait(true);

        var replacement = demo.AddAlphaReconnectTransport();
        demo.AlphaTransport.EnqueueRemoteDisconnect();
        await WaitForAsync(sessions, () => replacement.ConnectCount == 1 && channel.IsStale, "history discovery reconnect did not establish a new generation").ConfigureAwait(true);

        demo.EnqueuePhase1YRegistration(replacement);
        replacement.EnqueueInboundLine(":nexAlpha!demo@alpha.server JOIN #general");
        replacement.EnqueueInboundLine(":alpha.server 332 nexAlpha #general :Current topic after reconnect");
        replacement.EnqueueInboundLine(":alpha.server 324 nexAlpha #general +nt");
        replacement.EnqueueInboundLine(":alpha.server 353 nexAlpha = #general :@nexAlpha +Alex");
        replacement.EnqueueInboundLine(":alpha.server 366 nexAlpha #general :End of names");
        await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY LATEST #general", StringComparison.Ordinal)), "channel reconnect history request was not sent").ConfigureAwait(true);
        await WaitForAsync(sessions, () => channel.Synchronization == ChannelSynchronizationState.Synchronized, "current channel state did not resynchronize").ConfigureAwait(true);

        var currentMembers = channel.MembersSnapshot.Select(member => $"{member.Nickname}:{string.Join(',', member.PrefixModes.OrderBy(value => value))}").OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var currentTopic = channel.Topic;
        var currentModes = channel.Modes.OrderBy(value => value).ToArray();
        var currentUnread = channel.UnreadCount;
        var playbackStart = Stopwatch.StartNew();
        replacement.EnqueueInboundLine(":alpha.server BATCH +channel-history chathistory #general");
        for (var index = 0; index < 40; index++)
        {
            replacement.EnqueueInboundLine($"@batch=channel-history;msgid=phase1y-message-{index:00};time=2026-09-07T10:{index / 2:00}:{index % 60:00}.000Z :ReplayUser!u@history PRIVMSG #general :mixed-playback-{index:00}");
        }

        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-join;time=2026-09-07T10:20:00.000Z :OldAlice!u@history JOIN #general");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-part;time=2026-09-07T10:20:01.000Z :OldAlice!u@history PART #general :old");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-quit;time=2026-09-07T10:20:02.000Z :OldAlice!u@history QUIT :old");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-nick;time=2026-09-07T10:20:03.000Z :Alice!u@history NICK Alicia");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-mode;time=2026-09-07T10:20:04.000Z :OldOp!u@history MODE #general +o OldAlice");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-topic;time=2026-09-07T10:20:05.000Z :OldOp!u@history TOPIC #general :historical topic");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-away;time=2026-09-07T10:20:06.000Z :OldAlice!u@history AWAY :historical away");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-account;time=2026-09-07T10:20:07.000Z :OldAlice!u@history ACCOUNT old-account");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-kick;time=2026-09-07T10:20:08.000Z :OldOp!u@history KICK #general OldAlice :historical kick");
        replacement.EnqueueInboundLine("@batch=channel-history;msgid=phase1y-tagmsg;time=2026-09-07T10:20:09.000Z :OldAlice!u@history TAGMSG #general");
        replacement.EnqueueInboundLine(":alpha.server BATCH -channel-history");

        await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY TARGETS timestamp=", StringComparison.Ordinal)), "TARGETS discovery request was not sent").ConfigureAwait(true);
        replacement.EnqueueInboundLine(":alpha.server BATCH +targets draft/chathistory-targets");
        replacement.EnqueueInboundLine("@batch=targets :alpha.server CHATHISTORY TARGETS Alice 2026-09-07T12:15:00.000Z");
        replacement.EnqueueInboundLine("@batch=targets :alpha.server CHATHISTORY TARGETS Alice 2026-09-07T12:15:00.000Z");
        replacement.EnqueueInboundLine("@batch=targets :alpha.server CHATHISTORY TARGETS Bob 2026-09-07T12:16:00.000Z");
        replacement.EnqueueInboundLine(":alpha.server BATCH -targets");

        await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY LATEST Alice", StringComparison.Ordinal)), "known-query history recovery was not sent").ConfigureAwait(true);
        replacement.EnqueueInboundLine("@msgid=live-after-reconnect;time=2026-09-07T12:17:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :live after reconnect");
        replacement.EnqueueInboundLine(":alpha.server BATCH +alice-history chathistory Alice");
        replacement.EnqueueInboundLine("@batch=alice-history;msgid=alice-old;time=2026-09-07T12:10:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :offline Alice history");
        replacement.EnqueueInboundLine(":alpha.server BATCH -alice-history");

        await WaitForPollingAsync(() => !network.Session.GetChathistoryState(knownQuery.HistoryConversationKey).RequestActive, "known-query history request did not complete").ConfigureAwait(true);
        await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY LATEST Bob", StringComparison.Ordinal)), $"new recovered-query history request was not sent; outbound={string.Join(" | ", replacement.OutboundLines)}").ConfigureAwait(true);
        replacement.EnqueueInboundLine(":alpha.server BATCH +bob-history chathistory Bob");
        replacement.EnqueueInboundLine("@batch=bob-history;msgid=bob-old;time=2026-09-07T12:12:00.000Z :Bob!u@alpha PRIVMSG nexAlpha :offline Bob history");
        replacement.EnqueueInboundLine(":alpha.server BATCH -bob-history");

        await WaitForAsync(sessions, () => network.Queries.Any(query => query.Nickname == "Bob" && query.RecoveredHistoryCount > 0), "recovered query was not projected").ConfigureAwait(true);
        await sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        playbackStart.Stop();

        var afterMembers = channel.MembersSnapshot.Select(member => $"{member.Nickname}:{string.Join(',', member.PrefixModes.OrderBy(value => value))}").OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Require(channel.EntriesSnapshot.Any(entry => entry.Text == "mixed-playback-39"), "mixed historical message playback did not reach the channel");
        Require(channel.EntriesSnapshot.Any(entry => entry.Text == "joined #general" && entry.Provenance == ConversationEntryProvenance.ServerPlayback), "historical JOIN was not rendered");
        Require(Enumerable.SequenceEqual(currentMembers, afterMembers), "historical state playback changed current members or privileges");
        Require(channel.Topic == currentTopic, "historical TOPIC playback changed the current topic");
        Require(channel.Modes.OrderBy(value => value).SequenceEqual(currentModes), "historical MODE playback changed current channel modes");
        Require(channel.UnreadCount == currentUnread, "historical mixed playback changed live unread state");
        Require(network.Queries.Count == 2 && network.Queries.Count(query => query.Nickname == "Alice") == 1, "TARGETS/recovery created a duplicate known query");
        Require(knownQuery.Nickname == "Alice" && knownQuery.EntriesSnapshot.Any(entry => entry.Text == "live after reconnect"), "live query traffic did not reuse the known query");
        Console.WriteLine($"HISTORY_DISCOVERY_UI_METRICS messages=40 state_events=10 playback_ms={playbackStart.Elapsed.TotalMilliseconds:F3} queries={network.Queries.Count} recovered_bob={network.Queries.Single(query => query.Nickname == "Bob").RecoveredHistoryCount} members_unchanged=true current_topic_unchanged=true current_modes_unchanged=true");
    }

    private static async Task HistoryIdentityAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var sessions = window.ViewModel.Sessions;
        var query = sessions.EnsureQuery(network.Id, "Alice");
        window.ViewModel.SelectView(network.StatusView);
        demo.AlphaTransport.EnqueueInboundLine("@account=alice-account;msgid=identity-1;time=2026-09-07T11:58:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :identity one");
        demo.AlphaTransport.EnqueueInboundLine("@account=alice-account;msgid=identity-2;time=2026-09-07T11:59:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :identity two");
        demo.AlphaTransport.EnqueueInboundLine("@account=alice-account;msgid=identity-3;time=2026-09-07T12:00:00.000Z :Alice!u@alpha PRIVMSG nexAlpha :identity three");
        await WaitForAsync(sessions, () => query.EntryCount == 3 && query.IdentityEvidence.HasAccount("alice-account"), "live account evidence did not arrive").ConfigureAwait(true);

        var reconnectGeneration = network.Snapshot.ConnectionGeneration;
        var replacement = demo.AddAlphaReconnectTransport();
        demo.AlphaTransport.EnqueueRemoteDisconnect();
        await WaitForAsync(sessions, () => replacement.ConnectCount == 1 && !query.IsIdentityBoundToCurrentSession, "identity smoke reconnect did not cross the generation boundary").ConfigureAwait(true);
        demo.EnqueuePhase1YRegistration(replacement);
        await WaitForAsync(sessions, () => network.Snapshot.ConnectionGeneration > reconnectGeneration && network.State == NetworkDisplayState.Registered, "identity smoke reconnect did not register").ConfigureAwait(true);

        replacement.EnqueueInboundLine("@account=alice-account;msgid=identity-4;time=2026-09-07T12:01:00.000Z :Alicia!u@alpha PRIVMSG nexAlpha :same account after reconnect");
        await WaitForAsync(sessions, () => query.Nickname == "Alicia" && network.Queries.Count == 1 && query.EntryCount == 4, "strong account evidence did not reuse the query after reconnect").ConfigureAwait(true);
        await WaitForPollingAsync(
            () => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BETWEEN Alicia msgid=identity-3 msgid=identity-4 ", StringComparison.Ordinal)),
            "identity exact reconnect request was not sent").ConfigureAwait(true);
        replacement.EnqueueInboundLine("@draft/chathistory-end :alpha.server BATCH +identity-gap chathistory Alicia");
        replacement.EnqueueInboundLine(":alpha.server BATCH -identity-gap");

        replacement.EnqueueInboundLine("@account=other-account;msgid=identity-5;time=2026-09-07T12:02:00.000Z :Alice!other@alpha PRIVMSG nexAlpha :conflicting account");
        await WaitForAsync(sessions, () => network.Queries.Count == 2 && network.Queries.Any(item => item.EntryCount == 1), "conflicting account was incorrectly merged").ConfigureAwait(true);
        var conflicting = network.Queries.Single(item => !ReferenceEquals(item, query));
        Require(conflicting.IdentityEvidence.HasAccount("other-account"), "conflicting account evidence was not retained on the safe duplicate");

        await sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        await sessions.LogStore!.FlushAsync().ConfigureAwait(true);
        var selected = query.EntriesSnapshot.Single(item => item.ServerMessageId == "identity-2");
        var contextResult = await sessions.LoadContextAroundAsync(network, query, selected).ConfigureAwait(true);
        Require(contextResult.Succeeded, "local context around the selected msgid was not loaded");
        Require(query.HistoryContext.Select(item => item.Record.ServerMessageId).SequenceEqual(["identity-1", "identity-2", "identity-3", "identity-4"]), "local msgid context was not canonical or bounded");
        var unread = query.UnreadCount;

        var channel = RequiredChannel(network);
        await WaitForPollingAsync(
            () => !network.Session.GetChathistoryState(ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, channel.Channel)).RequestActive
                && !network.Session.GetChathistoryState(query.HistoryConversationKey).RequestActive,
            "automatic reconnect history requests did not drain").ConfigureAwait(true);
        var members = channel.MembersSnapshot.Select(member => member.Nickname).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var topic = channel.Topic;
        await WaitForPollingAsync(
            () => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY TARGETS timestamp=", StringComparison.Ordinal)),
            "identity smoke discovery request was not sent").ConfigureAwait(true);
        replacement.EnqueueInboundLine(":alpha.server BATCH +identity-targets draft/chathistory-targets");
        replacement.EnqueueInboundLine(":alpha.server BATCH -identity-targets");
        await Task.Delay(25).ConfigureAwait(true);
        var pending = network.Session.RequestHistoryAsync(new nexIRC.Core.Protocol.ChathistoryRequest
        {
            NetworkId = network.Id,
            ConnectionGeneration = network.Snapshot.ConnectionGeneration,
            Conversation = "Channel:#general",
            Target = "#general",
            Operation = nexIRC.Core.Protocol.ChathistoryOperation.Around,
            Reference = nexIRC.Core.Protocol.ChathistoryReference.MessageId("identity-remote-anchor"),
            Limit = 12,
            Purpose = nexIRC.Core.Protocol.ChathistoryRequestPurpose.LoadContext
        }).AsTask();
        await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY AROUND #general", StringComparison.Ordinal)), "identity firewall history request was not sent").ConfigureAwait(true);
        replacement.EnqueueInboundLine(":alpha.server BATCH +identity-history chathistory #general");
        replacement.EnqueueInboundLine("@batch=identity-history;msgid=identity-historical-join;time=2026-09-07T10:00:00.000Z :Historical!u@history JOIN #general");
        replacement.EnqueueInboundLine("@batch=identity-history;msgid=identity-historical-account;time=2026-09-07T10:00:01.000Z :Alicia!u@history ACCOUNT historical-account");
        replacement.EnqueueInboundLine("@batch=identity-history;msgid=identity-historical-nick;time=2026-09-07T10:00:02.000Z :Alicia!u@history NICK HistoricalAlicia");
        replacement.EnqueueInboundLine("@batch=identity-history;msgid=identity-historical-message;time=2026-09-07T10:00:03.000Z :Historical!u@history PRIVMSG #general :historical context");
        replacement.EnqueueInboundLine(":alpha.server BATCH -identity-history");
        var history = await pending.ConfigureAwait(true);
        await sessions.FlushStateDispatchAsync().ConfigureAwait(true);

        var queryHistory = network.Session.RequestHistoryAsync(new nexIRC.Core.Protocol.ChathistoryRequest
        {
            NetworkId = network.Id,
            ConnectionGeneration = network.Snapshot.ConnectionGeneration,
            Conversation = query.HistoryConversationKey,
            Target = "Alicia",
            Operation = nexIRC.Core.Protocol.ChathistoryOperation.Around,
            Reference = nexIRC.Core.Protocol.ChathistoryReference.MessageId("identity-query-anchor"),
            Limit = 8,
            Purpose = nexIRC.Core.Protocol.ChathistoryRequestPurpose.LoadContext
        }).AsTask();
        await WaitForPollingAsync(() => replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY AROUND Alicia", StringComparison.Ordinal)), "historical query identity request was not sent").ConfigureAwait(true);
        replacement.EnqueueInboundLine(":alpha.server BATCH +identity-query-history chathistory Alicia");
        replacement.EnqueueInboundLine("@batch=identity-query-history;msgid=identity-query-account;time=2026-09-07T10:00:01.000Z :Alicia!u@history ACCOUNT historical-account");
        replacement.EnqueueInboundLine("@batch=identity-query-history;msgid=identity-query-nick;time=2026-09-07T10:00:02.000Z :Alicia!u@history NICK HistoricalAlicia");
        replacement.EnqueueInboundLine("@batch=identity-query-history;msgid=identity-query-message;time=2026-09-07T10:00:03.000Z :Historical!u@history PRIVMSG Alicia :historical query context");
        replacement.EnqueueInboundLine(":alpha.server BATCH -identity-query-history");
        var queryHistoryResult = await queryHistory.ConfigureAwait(true);
        await sessions.FlushStateDispatchAsync().ConfigureAwait(true);

        Require(history.Succeeded, "identity firewall history playback did not complete");
        Require(queryHistoryResult.Succeeded, "historical query identity playback did not complete");
        Require(Enumerable.SequenceEqual(members, channel.MembersSnapshot.Select(member => member.Nickname).OrderBy(item => item, StringComparer.Ordinal)), "historical JOIN changed current members");
        Require(channel.Topic == topic, "historical playback changed current topic");
        Require(query.Nickname == "Alicia" && query.IdentityEvidence.HasAccount("alice-account"), "historical identity evidence rewrote current query identity");
        Require(query.HistoricalIdentityEvidence.HasAccount("historical-account"), "historical account evidence was not retained as provenance");
        Require(query.UnreadCount == unread, "historical identity playback changed unread state");
        Console.WriteLine($"HISTORY_IDENTITY_UI_METRICS queries={network.Queries.Count} context={query.HistoryContext.Count} account_reuse=true conflict_separate=true historical_firewall=true unread_unchanged=true");
    }

    private static async Task ModerationAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        var member = RequiredMember(channel, "Alex");
        var context = viewModel.CreateParticipantContext(network, channel, member);
        var groups = ParticipantActionCatalog.Build(context, isIgnored: false);
        var moderation = RequiredGroup(groups, "Moderation");
        Require(moderation.Items.All(item => item.IsEnabled), "moderation actions were not enabled for AlphaNet operator");
        var privileges = RequiredGroup(groups, "Channel Privileges");
        Require(privileges.Items.Single(item => item.ModeLetter == 'v').IsEnabled, "voice action was not enabled");

        var voice = await viewModel.ParticipantActions.SetPrivilegeAsync(context, 'v', adding: true).ConfigureAwait(true);
        Require(voice.Succeeded && voice.Operation is not null, "synthetic privilege operation did not start");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server MODE #general +v Alex");
        await WaitForAsync(viewModel.Sessions, () => viewModel.Sessions.TryGetOperation(voice.Operation!.Id, out var result) && result!.State == IrcOperationState.Confirmed, "synthetic privilege confirmation was not reconciled").ConfigureAwait(true);

        var ban = await viewModel.ParticipantActions.BanAsync(context, "*!*@smoke.invalid").ConfigureAwait(true);
        Require(ban.Succeeded && ban.Operation is not null, "synthetic ban operation did not start");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 482 nexAlpha #general :You need channel operator privileges");
        await WaitForAsync(viewModel.Sessions, () => viewModel.Sessions.TryGetOperation(ban.Operation!.Id, out var result) && result!.State == IrcOperationState.Rejected, "synthetic permission rejection was not reconciled").ConfigureAwait(true);
        Require(channel.Modes.Contains('n'), "permission rejection changed channel projection state");

        var banList = await viewModel.ParticipantActions.OpenBanListAsync(context).ConfigureAwait(true);
        Require(banList.Succeeded && banList.View is BanListView, "Ban List action was not available");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 367 nexAlpha #general *!*@existing.invalid setter 1700000000");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 368 nexAlpha #general :End of channel ban list");
        var list = (BanListView)banList.View!;
        await WaitForAsync(viewModel.Sessions, () => list.IsCompleted && list.Result.Entries.Count == 1, "Ban List did not complete").ConfigureAwait(true);
        list.Result.SelectedEntry = list.Result.EntriesSnapshot[0];
        Require(list.Result.SelectedEntry is not null, "Ban List selection was not available");
        var selectedMask = list.Result.SelectedEntry!.Mask;
        var remove = await viewModel.ParticipantActions.RemoveBanAsync(list, selectedMask).ConfigureAwait(true);
        Require(remove.Succeeded && remove.Operation is not null, "selected ban action was not available");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server MODE #general -b *!*@existing.invalid");
        await WaitForAsync(viewModel.Sessions, () => viewModel.Sessions.TryGetOperation(remove.Operation!.Id, out var result) && result!.State == IrcOperationState.Confirmed, "ban removal confirmation was not reconciled").ConfigureAwait(true);
    }

    private static async Task ChannelPropertiesAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        var properties = new ChannelPropertiesViewModel(viewModel.Sessions, network, channel);
        Require(properties.NetworkName == "AlphaNet" && properties.ChannelName == "#general", "Channel Properties identity was incorrect");
        Require(properties.TopicText == "Alpha topic" && properties.TopicSetterText.Contains("alpha-setter", StringComparison.Ordinal), "Channel Properties topic projection was incorrect");
        Require(properties.Modes.Any(mode => mode.Mode == 'n' && mode.IsActive) && properties.Modes.Any(mode => mode.Mode == 't' && mode.IsActive), "Channel Properties did not project initial modes");
        Require(!properties.IsReadOnly, "AlphaNet Channel Properties unexpectedly reported read-only authority");

        var dialog = new ChannelPropertiesWindow(properties) { Owner = window };
        dialog.Show();
        dialog.Close();

        var flag = await properties.SetFlagModeAsync('i', adding: true).ConfigureAwait(true);
        Require(flag.Succeeded && flag.Operation is not null, "flag-mode request did not start");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server MODE #general +i");
        await WaitForAsync(viewModel.Sessions, () => channel.Modes.Contains('i') && viewModel.Sessions.TryGetOperation(flag.Operation!.Id, out var result) && result!.State == IrcOperationState.Confirmed, "flag-mode confirmation was not projected").ConfigureAwait(true);

        properties.TopicDraft = "Smoke topic";
        var topic = await properties.SaveTopicAsync().ConfigureAwait(true);
        Require(topic.Succeeded && topic.Operation is not null, "topic edit request did not start");
        demo.AlphaTransport.EnqueueInboundLine(":nexAlpha!demo@alpha.server TOPIC #general :Smoke topic");
        await WaitForAsync(viewModel.Sessions, () => channel.Topic == "Smoke topic" && viewModel.Sessions.TryGetOperation(topic.Operation!.Id, out var result) && result!.State == IrcOperationState.Confirmed, "topic confirmation was not projected").ConfigureAwait(true);

        var editor = new TopicEditorWindow(new ChannelPropertiesViewModel(viewModel.Sessions, network, channel)) { Owner = window };
        editor.Close();
    }

    private static async Task MultiNetworkAsync(MainWindow window, DemoScenario demo, NetworkWorkspace alpha, NetworkWorkspace beta)
    {
        var viewModel = window.ViewModel;
        var alphaChannel = RequiredChannel(alpha);
        var betaChannel = RequiredChannel(beta);
        var alphaAlex = RequiredMember(alphaChannel, "Alex");
        var betaAlex = RequiredMember(betaChannel, "Alex");
        var alphaAuthority = ChannelAuthority.Evaluate(alpha, alphaChannel, alphaAlex);
        var betaAuthority = ChannelAuthority.Evaluate(beta, betaChannel, betaAlex);
        Require(alpha.Snapshot.Features.Prefix?.RawValue == "(qaohv)~&@%+" && beta.Snapshot.Features.Prefix?.RawValue == "(ov)@+", "duplicate smoke users did not retain distinct PREFIX grammars");
        Require(alpha.Snapshot.Features.ChannelModes?.RawValue != beta.Snapshot.Features.ChannelModes?.RawValue, "duplicate smoke channels did not retain distinct CHANMODES grammars");
        Require(alphaAuthority.Kick.IsAllowed && betaAuthority.Kick.IsDenied, "duplicate smoke users did not retain distinct authority");

        var alphaMenu = ParticipantActionCatalog.Build(viewModel.CreateParticipantContext(alpha, alphaChannel, alphaAlex), false);
        var betaMenu = ParticipantActionCatalog.Build(viewModel.CreateParticipantContext(beta, betaChannel, betaAlex), false);
        Require(RequiredGroup(alphaMenu, "Moderation").Items.All(item => item.IsEnabled), "AlphaNet moderation menu was not enabled");
        Require(RequiredGroup(betaMenu, "Moderation").Items.All(item => !item.IsEnabled), "BetaNet moderation menu was not conservatively disabled");

        var alphaBefore = demo.AlphaTransport.OutboundLines.Count;
        var betaBefore = demo.BetaTransport.OutboundLines.Count;
        var alphaMode = await new ChannelActionService(viewModel.Sessions).SetFlagModeAsync(alpha, alphaChannel, 'm', true).ConfigureAwait(true);
        Require(alphaMode.Succeeded, "AlphaNet mode operation did not start");
        var betaMode = await new ChannelActionService(viewModel.Sessions).SetFlagModeAsync(beta, betaChannel, 'm', true).ConfigureAwait(true);
        Require(!betaMode.Succeeded && demo.BetaTransport.OutboundLines.Count == betaBefore, "BetaNet mode operation crossed its authority boundary");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server MODE #general +m");
        await WaitForAsync(viewModel.Sessions, () => alphaChannel.Modes.Contains('m'), "AlphaNet mode confirmation was not isolated").ConfigureAwait(true);
        Require(!betaChannel.Modes.Contains('m') && demo.AlphaTransport.OutboundLines.Count > alphaBefore, "cross-network mode projection mutation occurred");

        var alphaTopic = await new ChannelActionService(viewModel.Sessions).EditTopicAsync(alpha, alphaChannel, "Alpha-only topic").ConfigureAwait(true);
        Require(alphaTopic.Succeeded, "AlphaNet topic operation did not start");
        var betaTopic = await new ChannelActionService(viewModel.Sessions).EditTopicAsync(beta, betaChannel, "Beta attempt").ConfigureAwait(true);
        Require(!betaTopic.Succeeded && demo.BetaTransport.OutboundLines.Count == betaBefore, "BetaNet topic operation crossed its authority boundary");
        demo.AlphaTransport.EnqueueInboundLine(":nexAlpha!demo@alpha.server TOPIC #general :Alpha-only topic");
        await WaitForAsync(viewModel.Sessions, () => alphaChannel.Topic == "Alpha-only topic", "AlphaNet topic confirmation was not isolated").ConfigureAwait(true);
        Require(betaChannel.Topic == "Beta topic", "cross-network topic projection mutation occurred");
    }

    private static async Task LifecycleAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        viewModel.SelectView(channel);
        var beforeClose = demo.AlphaTransport.OutboundLines.Count;
        Require(viewModel.CloseActiveView(), "lifecycle channel did not close through the view-model path");
        Require(!channel.IsViewOpen, "lifecycle close left the view open");
        Require(demo.AlphaTransport.OutboundLines.Count == beforeClose
            || !demo.AlphaTransport.OutboundLines.Skip(beforeClose).Any(line => line.StartsWith("PART", StringComparison.Ordinal)), "closing the view unexpectedly PARTed the channel");

        demo.AlphaTransport.EnqueueInboundLine(":Lifecycle!u@demo PRIVMSG #general :lifecycle live message");
        await WaitForAsync(viewModel.Sessions, () => channel.EntriesSnapshot.Any(entry => entry.Text == "lifecycle live message"), "lifecycle message was not received").ConfigureAwait(true);
        Require(viewModel.ReopenConversation(channel), "lifecycle conversation did not reopen");
        Require(ReferenceEquals(viewModel.ActiveView, channel), "lifecycle reopen did not restore the same conversation");

        await viewModel.PartAndCloseActiveAsync().ConfigureAwait(true);
        Require(channel.LifecycleState == ConversationLifecycleState.Parted, "lifecycle PART did not produce parted state");
        var beforeHistory = demo.AlphaTransport.OutboundLines.Count;
        var historical = viewModel.Sessions.OpenHistoricalConversation(network.Id, DestinationKind.Channel, "#general");
        Require(ReferenceEquals(historical, channel) && historical.IsViewOpen, "historical reopen created a duplicate channel view");
        Require(demo.AlphaTransport.OutboundLines.Count == beforeHistory, "opening channel history unexpectedly sent IRC traffic");

        await viewModel.Sessions.RejoinChannelAsync(network.Id, "#general").ConfigureAwait(true);
        demo.AlphaTransport.EnqueueInboundLine(":nexAlpha!demo@alpha.server JOIN #general");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 353 nexAlpha = #general :@nexAlpha");
        demo.AlphaTransport.EnqueueInboundLine(":alpha.server 366 nexAlpha #general :End of names");
        await WaitForAsync(viewModel.Sessions, () => channel.IsJoined && channel.Synchronization == ChannelSynchronizationState.Synchronized, "explicit rejoin did not restore the channel").ConfigureAwait(true);
    }

    private static async Task ReadStateAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        await viewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        // The shared demo seed includes a live MODE line. Establish a clean
        // read-state baseline before exercising accumulation semantics.
        viewModel.SelectView(channel);
        viewModel.SelectView(network.StatusView);
        demo.AlphaTransport.EnqueueInboundLine(":ReadState!u@demo PRIVMSG #general :ordinary read-state message");
        await WaitForAsync(viewModel.Sessions, () => channel.Activity == WorkspaceActivity.Unread && channel.UnreadCount == 1, "read-state unread activity was not classified").ConfigureAwait(true);
        demo.AlphaTransport.EnqueueInboundLine(":ReadState!u@demo PRIVMSG #general :nexAlpha read-state highlight");
        await WaitForAsync(viewModel.Sessions, () => channel.Activity == WorkspaceActivity.Important && channel.HighlightCount == 1, "read-state highlight was not classified").ConfigureAwait(true);
        Require(channel.UnreadCount == 2, "read-state unread count was not accumulated");

        viewModel.SelectView(channel);
        Require(channel.Activity == WorkspaceActivity.None && channel.UnreadCount == 0 && channel.HighlightCount == 0, "selecting the visible conversation did not mark it read");
        channel.SetHistoryContext(Array.Empty<HistoryContextEntry>(), "historical inspection");
        Require(channel.Activity == WorkspaceActivity.None && channel.UnreadCount == 0, "historical inspection changed read state");

        viewModel.Sessions.Configuration?.AddIgnore(new IgnoreRule { Nickname = "IgnoredReadState" });
        demo.AlphaTransport.EnqueueInboundLine(":IgnoredReadState!u@demo JOIN #general");
        await WaitForAsync(viewModel.Sessions, () => channel.MembersSnapshot.Any(member => member.Nickname == "IgnoredReadState"), "ignored structural join was not reconciled").ConfigureAwait(true);
        demo.AlphaTransport.EnqueueInboundLine(":IgnoredReadState!u@demo PRIVMSG #general :ignored read-state message");
        await viewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        Require(channel.Activity == WorkspaceActivity.None && channel.UnreadCount == 0 && channel.HighlightCount == 0, "ignored content changed read state");
        Require(!channel.EntriesSnapshot.Any(entry => entry.Text.Contains("ignored read-state", StringComparison.Ordinal)), "ignored content was presented");
    }

    private static async Task ReconnectAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        var replacement = demo.AddAlphaReconnectTransport();
        demo.AlphaTransport.EnqueueRemoteDisconnect();
        await WaitForAsync(viewModel.Sessions, () => replacement.ConnectCount == 1 && channel.IsStale, "reconnect did not establish a new session generation").ConfigureAwait(true);

        Register(replacement, "nexAlpha");
        replacement.EnqueueInboundLine(":nexAlpha!demo@alpha.server JOIN #general");
        replacement.EnqueueInboundLine(":alpha.server 353 nexAlpha = #general :@nexAlpha");
        replacement.EnqueueInboundLine(":alpha.server 366 nexAlpha #general :End of names");
        await WaitForAsync(viewModel.Sessions, () => channel.Synchronization == ChannelSynchronizationState.Synchronized, "reconnect resynchronization did not complete").ConfigureAwait(true);

        await demo.AlphaTransport.EmitCallbackAsync(new IrcTransportInboundLineCallback(":old.server MODE #general +s"));
        replacement.EnqueueInboundLine(":alpha.server MODE #general +m");
        await WaitForAsync(viewModel.Sessions, () => channel.Modes.Contains('m'), "new-generation MODE was not projected").ConfigureAwait(true);
        Require(!channel.Modes.Contains('s'), "an old-generation MODE contaminated the reconnected channel");
    }

    private static async Task BurstAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network, int burstSize)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        viewModel.SelectView(channel);
        viewModel.SelectView(network.StatusView);

        for (var index = 0; index < burstSize; index++)
        {
            demo.AlphaTransport.EnqueueInboundLine($":BurstUser!burst@demo PRIVMSG #general :burst-{index:0000}");
        }

        // This is the same boundary as a real WPF selection event. It is
        // deliberately posted at Input after the protocol burst has been
        // accepted, while protocol-derived state work is still draining at
        // Background priority. The selection itself remains a normal
        // presentation/read-state action; it never reorders protocol events.
        await Task.Yield();
        var interactionId = window.PresentationTiming.BeginInteraction();
        var selectionPosted = Stopwatch.GetTimestamp();
        var selectionWpfWaitMilliseconds = 0d;
        var selectionExecutionMilliseconds = 0d;
        var selectionBeforeBurstComplete = false;
        await window.Dispatcher.InvokeAsync(() =>
        {
            var selectionStarted = Stopwatch.GetTimestamp();
            selectionWpfWaitMilliseconds = TicksToMilliseconds(selectionStarted - selectionPosted);
            viewModel.SelectView(channel);
            window.PresentationTiming.MarkWpfStateChanged(interactionId);
            selectionBeforeBurstComplete = !BurstTailReached(channel, burstSize - 1);
            selectionExecutionMilliseconds = TicksToMilliseconds(Stopwatch.GetTimestamp() - selectionStarted);
        }, System.Windows.Threading.DispatcherPriority.Input).Task.ConfigureAwait(true);
        Require(selectionBeforeBurstComplete, "burst selection was not serviced before the complete derived backlog drained");
        var presentationOpportunity = await window.PresentationTiming
            .WaitForNextOpportunityAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(true);

        try
        {
            await WaitForAsync(viewModel.Sessions, () =>
            {
                return BurstTailReached(channel, burstSize - 1);
            }, "burst input did not reach its deterministic tail", 15_000).ConfigureAwait(true);
        }
        catch (TimeoutException exception)
        {
            var currentEntries = channel.EntriesSnapshot;
            throw new InvalidOperationException($"Burst tail did not settle: count={currentEntries.Count} last={(currentEntries.Count == 0 ? string.Empty : currentEntries[^1].Text)} queued={viewModel.Sessions.Diagnostics.QueuedStateActions} processed={viewModel.Sessions.Diagnostics.ProcessedStateActions}", exception);
        }
        await viewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
        var entries = channel.EntriesSnapshot;
        Require(entries.Count == WorkspaceView.MaximumEntries, "burst projection did not retain its bounded transcript window");
        Require(entries[0].Text == $"burst-{burstSize - WorkspaceView.MaximumEntries:0000}" && entries[^1].Text == $"burst-{burstSize - 1:0000}", "burst transcript ordering or tail retention was incorrect");
        Require(entries.Zip(entries.Skip(1)).All(pair => string.CompareOrdinal(pair.First.Text, pair.Second.Text) < 0), "burst transcript ordering was not FIFO");

        var unreadBeforeRead = channel.UnreadCount;
        viewModel.SelectView(channel);
        Require(channel.Activity == WorkspaceActivity.None && channel.UnreadCount == 0, "burst selection did not clear accumulated unread state");
        viewModel.SelectView(network.StatusView);
        demo.AlphaTransport.EnqueueInboundLine(":BurstUser!burst@demo PRIVMSG #general :post-burst unread");
        await WaitForAsync(viewModel.Sessions, () => channel.UnreadCount == 1, "post-burst unread state did not accumulate").ConfigureAwait(true);
        viewModel.SelectView(channel);
        Require(channel.Activity == WorkspaceActivity.None && channel.UnreadCount == 0, "post-burst selection did not restore read state");

        var diagnostics = viewModel.Sessions.Diagnostics.StateDispatch;
        var boundary = diagnostics.BoundaryDiagnostics;
        var presentation = viewModel.PresentationDiagnostics;
        Console.WriteLine(
            $"BURST_UI_METRICS events={burstSize} selection_authority_wait_ms=n/a selection_wpf_wait_ms={selectionWpfWaitMilliseconds:F3} selection_wpf_execution_ms={selectionExecutionMilliseconds:F3} "
            + $"presentation_opportunity_available={presentationOpportunity is not null} interaction_to_opportunity_ms={FormatMilliseconds(presentationOpportunity?.InteractionToOpportunityMilliseconds)} "
            + $"interaction_to_wpf_state_ms={FormatMilliseconds(presentationOpportunity?.InteractionToWpfStateMilliseconds)} wpf_state_to_opportunity_ms={FormatMilliseconds(presentationOpportunity?.WpfStateToPresentationOpportunityMilliseconds)} "
            + $"selection_before_burst_complete={selectionBeforeBurstComplete} unread_before_read={unreadBeforeRead} max_authoritative_queue_depth={diagnostics.MaximumQueueDepth} "
            + $"max_wpf_pending={diagnostics.MaximumWpfPendingWorkItems} wpf_posted={diagnostics.WpfWorkItemsPosted} wpf_executed={diagnostics.WpfWorkItemsExecuted} "
            + $"p50_wpf_schedule_wait_ms={diagnostics.P50WpfScheduleWaitMilliseconds:F3} p95_wpf_schedule_wait_ms={diagnostics.P95WpfScheduleWaitMilliseconds:F3} p99_wpf_schedule_wait_ms={diagnostics.P99WpfScheduleWaitMilliseconds:F3} "
            + $"p50_queue_wait_ms={diagnostics.P50QueueWaitMilliseconds:F3} p95_queue_wait_ms={diagnostics.P95QueueWaitMilliseconds:F3} p99_queue_wait_ms={diagnostics.P99QueueWaitMilliseconds:F3} "
            + $"p95_mutation_ms={diagnostics.P95MutationDurationMilliseconds:F3} cooperative_slices={diagnostics.CooperativeSliceCount} cooperative_yields={diagnostics.CooperativeYieldCount} "
            + $"max_slice_work_items={diagnostics.MaximumCooperativeSliceWorkItems} max_slice_ms={diagnostics.MaximumCooperativeSliceDurationMilliseconds:F3} "
            + $"navigation_refresh_requests={presentation.NavigationRefreshRequests} navigation_refresh_executions={presentation.NavigationRefreshExecutions} "
            + $"navigation_refresh_coalesced={presentation.NavigationRefreshCoalescedRequests} tail_ordered=true");
    }

    private static string FormatMilliseconds(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a";

    private static bool BurstTailReached(ChannelView channel, int lastIndex)
    {
        var entries = channel.EntriesSnapshot;
        return entries.Count == WorkspaceView.MaximumEntries
            && entries[^1].Text == $"burst-{lastIndex:0000}";
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static async Task SustainedInteractivityAsync(MainWindow window, DemoScenario demo, NetworkWorkspace alpha, NetworkWorkspace beta)
    {
        var viewModel = window.ViewModel;
        var alphaChannel = RequiredChannel(alpha);
        var betaChannel = RequiredChannel(beta);
        var alphaQuery = viewModel.Sessions.EnsureQuery(alpha.Id, "Alex");
        var existingQuery = viewModel.Sessions.OpenHistoricalConversation(alpha.Id, DestinationKind.Query, "ExistingPeer");

        viewModel.SelectView(alpha.StatusView);
        demo.BetaTransport.EnqueueInboundLine(":QuietMarker!u@beta PRIVMSG #general :phase1s-quiet-marker");
        await WaitForAsync(viewModel.Sessions, () => betaChannel.UnreadCount > 0, "quiet network did not accumulate an unread marker").ConfigureAwait(true);

        const int noisyTrafficCount = 3_600;
        const int quietTrafficCount = 360;
        var trafficStart = Stopwatch.GetTimestamp();
        var trafficRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noisyTraffic = ProduceTrafficAsync(
            demo.AlphaTransport,
            "#general",
            "phase1s-alpha",
            noisyTrafficCount,
            batchSize: 32,
            pacingMilliseconds: 1,
            releaseAfterFirstBatch: trafficRelease.Task);
        var quietTraffic = ProduceTrafficAsync(
            demo.BetaTransport,
            "#general",
            "phase1s-beta",
            quietTrafficCount,
            batchSize: 24,
            pacingMilliseconds: 2);
        await Task.Yield();

        var measurements = new List<InteractionMeasurement>();
        var quietUnreadObserved = false;
        try
        {
            measurements.Add(await MeasureInteractionAsync(
            window,
            "conversation-selection",
            () => viewModel.SelectView(alphaChannel),
            () => ReferenceEquals(viewModel.ActiveView, alphaChannel),
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));
            measurements.Add(await MeasureInteractionAsync(
            window,
            "network-selection",
            () => viewModel.SelectView(betaChannel),
            () => ReferenceEquals(viewModel.ActiveView, betaChannel) && ReferenceEquals(viewModel.Sessions.ActiveNetwork, beta),
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            var navigatorTarget = viewModel.ConversationNavigator.FirstOrDefault(item =>
            item.Identity.NetworkId == alpha.Id
            && item.Kind == WorkspaceViewKind.Channel
            && item.Name == alphaChannel.Channel);
            Require(navigatorTarget is not null, "sustained smoke did not expose the Alpha conversation in the navigator");
            measurements.Add(await MeasureInteractionAsync(
            window,
            "navigator-selection",
            () => viewModel.ActivateConversationCommand.Execute(navigatorTarget),
            () => ReferenceEquals(viewModel.ActiveView, alphaChannel),
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            const string draft = "phase1s draft input";
            measurements.Add(await MeasureInteractionAsync(
            window,
            "draft-edit",
            () => viewModel.PrepareInput(draft),
            () => viewModel.InputText == draft,
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            measurements.Add(await MeasureInteractionAsync(
            window,
            "draft-switch-and-restore",
            () =>
            {
                viewModel.SelectView(betaChannel);
                viewModel.SelectView(alphaChannel);
            },
            () => ReferenceEquals(viewModel.ActiveView, alphaChannel) && viewModel.InputText == draft,
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            viewModel.SelectView(alphaChannel);
            Require(ReferenceEquals(viewModel.ActiveView, alphaChannel) && ReferenceEquals(viewModel.Sessions.ActiveView, alphaChannel), "sustained smoke could not leave the quiet conversation before measuring unread selection");
            demo.BetaTransport.EnqueueInboundLine(":QuietMarker!u@beta PRIVMSG #general :phase1s-quiet-selection-marker");
            await WaitForAsync(viewModel.Sessions, () => betaChannel.UnreadCount > 0, $"quiet network did not accumulate the selection unread marker (active={viewModel.ActiveView?.Title}, betaActive={betaChannel.IsActive}, entries={betaChannel.EntryCount})").ConfigureAwait(true);
            measurements.Add(await MeasureInteractionAsync(
            window,
            "unread-selection",
            () =>
            {
                quietUnreadObserved = betaChannel.UnreadCount > 0;
                viewModel.SelectView(betaChannel);
            },
            () => quietUnreadObserved && betaChannel.UnreadCount == 0 && betaChannel.Activity == WorkspaceActivity.None,
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            measurements.Add(await MeasureInteractionAsync(
            window,
            "existing-query-destination",
            () => viewModel.SelectView(existingQuery),
            () => ReferenceEquals(viewModel.ActiveView, existingQuery) && existingQuery.IsViewOpen,
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            measurements.Add(await MeasureInteractionAsync(
            window,
            "rapid-conversation-switch",
            () =>
            {
                viewModel.SelectView(alphaChannel);
                viewModel.SelectView(betaChannel);
                viewModel.SelectView(alphaQuery);
            },
            () => ReferenceEquals(viewModel.ActiveView, alphaQuery),
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            var secondNavigatorTarget = viewModel.ConversationNavigator.FirstOrDefault(item =>
            item.Identity.NetworkId == beta.Id
            && item.Kind == WorkspaceViewKind.Channel
            && item.Name == betaChannel.Channel);
            Require(secondNavigatorTarget is not null, "sustained smoke did not retain the quiet network navigator item");
            measurements.Add(await MeasureInteractionAsync(
            window,
            "navigator-restore",
            () => viewModel.ActivateConversationCommand.Execute(secondNavigatorTarget),
            () => ReferenceEquals(viewModel.ActiveView, betaChannel),
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            measurements.Add(await MeasureInteractionAsync(
            window,
            "draft-input-after-navigation",
            () => viewModel.PrepareInput("phase1s post-navigation input"),
            () => viewModel.InputText == "phase1s post-navigation input",
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));

            measurements.Add(await MeasureInteractionAsync(
            window,
            "conversation-selection-final",
            () => viewModel.SelectView(alphaChannel),
            () => ReferenceEquals(viewModel.ActiveView, alphaChannel) && ReferenceEquals(viewModel.Sessions.ActiveNetwork, alpha),
            () => !noisyTraffic.IsCompleted || !quietTraffic.IsCompleted).ConfigureAwait(true));
        }
        finally
        {
            trafficRelease.TrySetResult();
        }

        await Task.WhenAll(noisyTraffic, quietTraffic).ConfigureAwait(true);
        var trafficElapsedMilliseconds = TicksToMilliseconds(Stopwatch.GetTimestamp() - trafficStart);
        try
        {
            await WaitForAsync(viewModel.Sessions, () =>
                AlphaTrafficTailReached(alphaChannel, noisyTrafficCount)
                && BetaTrafficTailReached(betaChannel, quietTrafficCount),
                "sustained traffic tails did not converge", 45_000).ConfigureAwait(true);
        }
        catch (TimeoutException exception)
        {
            var failedDiagnostics = viewModel.Sessions.Diagnostics;
            var failedAlphaEntries = alphaChannel.EntriesSnapshot;
            var failedBetaEntries = betaChannel.EntriesSnapshot;
            throw new InvalidOperationException(
                $"sustained traffic tails did not converge (alpha_last={(failedAlphaEntries.Count == 0 ? "<none>" : failedAlphaEntries[^1].Text)}, beta_last={(failedBetaEntries.Count == 0 ? "<none>" : failedBetaEntries[^1].Text)}, queued={failedDiagnostics.QueuedStateActions}, processed={failedDiagnostics.ProcessedStateActions}, queue={failedDiagnostics.CurrentQueueDepth}, max_queue={failedDiagnostics.MaximumQueueDepth})",
                exception);
        }
        await viewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);

        var alphaEntries = alphaChannel.EntriesSnapshot
            .Where(entry => entry.Text.StartsWith("phase1s-alpha-", StringComparison.Ordinal))
            .Select(entry => entry.Text)
            .ToArray();
        var betaEntries = betaChannel.EntriesSnapshot
            .Where(entry => entry.Text.StartsWith("phase1s-beta-", StringComparison.Ordinal))
            .Select(entry => entry.Text)
            .ToArray();
        Require(alphaEntries.SequenceEqual(alphaEntries.OrderBy(static value => value, StringComparer.Ordinal)), "noisy Alpha traffic lost FIFO ordering");
        Require(betaEntries.SequenceEqual(betaEntries.OrderBy(static value => value, StringComparer.Ordinal)), "quiet Beta traffic lost FIFO ordering");
        Require(!betaEntries.Any(entry => entry.StartsWith("phase1s-alpha-", StringComparison.Ordinal)), "noisy Alpha traffic crossed into Beta");
        Require(measurements.Count(item => item.TrafficWasActive) >= 8, "too few user interactions overlapped active traffic");
        Require(quietUnreadObserved, "quiet network unread state was not observed before selection");

        var diagnostics = viewModel.Sessions.Diagnostics;
        var stateDispatch = diagnostics.StateDispatch;
        var recentSamples = stateDispatch.RecentSamples ?? Array.Empty<WorkspaceDispatchSample>();
        var boundary = stateDispatch.BoundaryDiagnostics;
        var trafficEvents = noisyTrafficCount + quietTrafficCount + 1;
        var rate = trafficEvents / Math.Max(trafficElapsedMilliseconds / 1_000d, 0.001d);
        Console.WriteLine(
            $"SUSTAINED_UI_METRICS observations={measurements.Count} active_overlaps={measurements.Count(item => item.TrafficWasActive)} "
            + $"traffic_events={trafficEvents} noisy_alpha_events={noisyTrafficCount} quiet_beta_events={quietTrafficCount + 1} traffic_elapsed_ms={trafficElapsedMilliseconds:F3} traffic_rate_eps={rate:F3} "
            + FormatDistribution("queue_depth_at_interaction", measurements.Select(item => (double)item.QueueDepthAtInteraction)) + " "
            + FormatDistribution("oldest_queued_age_ms", measurements.Select(item => item.OldestQueuedWorkAgeMilliseconds)) + " "
            + FormatDistribution("wpf_callback_wait_ms", measurements.Select(item => item.WpfCallbackWaitMilliseconds)) + " "
            + FormatDistribution("interaction_to_state_ms", measurements.Select(item => item.InteractionToStateMilliseconds)) + " "
            + FormatDistribution("state_to_presentation_ms", measurements.Select(item => item.StateToPresentationOpportunityMilliseconds)) + " "
            + FormatDistribution("interaction_to_presentation_ms", measurements.Select(item => item.InteractionToPresentationOpportunityMilliseconds)) + " "
            + FormatDistribution("authority_residence_ms", recentSamples.Select(static sample => sample.QueueWaitMilliseconds)) + " "
            + FormatDistribution("dispatcher_boundary_ms", recentSamples.Select(static sample => sample.WpfScheduleWaitMilliseconds)) + " "
            + $"max_authoritative_queue_depth={stateDispatch.MaximumQueueDepth} max_wpf_pending={stateDispatch.MaximumWpfPendingWorkItems} "
            + $"wpf_posted={stateDispatch.WpfWorkItemsPosted} wpf_executed={stateDispatch.WpfWorkItemsExecuted} "
            + $"cooperative_slices={stateDispatch.CooperativeSliceCount} cooperative_yields={stateDispatch.CooperativeYieldCount} "
            + $"max_slice_work_items={stateDispatch.MaximumCooperativeSliceWorkItems} max_slice_ms={stateDispatch.MaximumCooperativeSliceDurationMilliseconds:F3} "
            + $"authority_samples={recentSamples.Count} alpha_tail_ordered=true beta_tail_ordered=true network_isolated=true unread_cleared=true "
            + $"navigation_refresh_requests={viewModel.PresentationDiagnostics.NavigationRefreshRequests} navigation_refresh_executions={viewModel.PresentationDiagnostics.NavigationRefreshExecutions} "
            + $"navigation_refresh_coalesced={viewModel.PresentationDiagnostics.NavigationRefreshCoalescedRequests}");
    }

    private static async Task<InteractionMeasurement> MeasureInteractionAsync(
        MainWindow window,
        string name,
        Action action,
        Func<bool> stateAssertion,
        Func<bool> trafficActive)
    {
        var stateDispatch = window.ViewModel.Sessions.Diagnostics.StateDispatch;
        var interactionId = window.PresentationTiming.BeginInteraction();
        var callbackStart = 0L;
        PresentationTimingSample? presentation = null;
        try
        {
            await Task.Run(() => window.Dispatcher.InvokeAsync(() =>
            {
                callbackStart = Stopwatch.GetTimestamp();
                action();
                window.PresentationTiming.MarkWpfStateChanged(interactionId);
            }, System.Windows.Threading.DispatcherPriority.Input).Task).ConfigureAwait(true);
            if (!stateAssertion())
            {
                throw new InvalidOperationException(
                    $"sustained interaction '{name}' did not reach its expected WPF state (view-model active={window.ViewModel.ActiveView?.Title ?? "<none>"}, session active={window.ViewModel.Sessions.ActiveView?.Title ?? "<none>"})");
            }
            presentation = await window.PresentationTiming
                .WaitForNextOpportunityAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(true);
            Require(presentation is not null, $"sustained interaction '{name}' had no bounded presentation opportunity");
            var completedPresentation = presentation!;
            return new InteractionMeasurement(
                name,
                stateDispatch.CurrentQueueDepth,
                stateDispatch.CurrentOldestQueuedWorkAgeMilliseconds,
                TicksToMilliseconds(callbackStart - completedPresentation.InteractionTimestamp),
                completedPresentation.InteractionToWpfStateMilliseconds,
                completedPresentation.WpfStateToPresentationOpportunityMilliseconds,
                completedPresentation.InteractionToOpportunityMilliseconds,
                trafficActive());
        }
        finally
        {
            if (presentation is null)
            {
                window.PresentationTiming.CancelPendingInteraction();
            }
        }
    }

    private static async Task ProduceTrafficAsync(
        FakeIrcTransport transport,
        string channel,
        string prefix,
        int count,
        int batchSize,
        int pacingMilliseconds,
        Task? releaseAfterFirstBatch = null,
        CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transport.EnqueueInboundLine($":{prefix}!stream@demo PRIVMSG {channel} :{prefix}-{index:00000}");
            if ((index + 1) % batchSize == 0)
            {
                if (index + 1 == batchSize && releaseAfterFirstBatch is not null)
                {
                    await releaseAfterFirstBatch.ConfigureAwait(true);
                }

                await Task.Delay(pacingMilliseconds, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    private static bool AlphaTrafficTailReached(ChannelView channel, int count) =>
        channel.EntriesSnapshot.Any(entry => entry.Text == $"phase1s-alpha-{count - 1:00000}");

    private static bool BetaTrafficTailReached(ChannelView channel, int count) =>
        channel.EntriesSnapshot.Any(entry => entry.Text == $"phase1s-beta-{count - 1:00000}");

    private static string FormatDistribution(string prefix, IEnumerable<double> values)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        Require(ordered.Length > 0, $"no samples were collected for {prefix}");
        return $"{prefix}_min={ordered[0]:F3} {prefix}_p50={Percentile(ordered, 0.50):F3} {prefix}_p95={Percentile(ordered, 0.95):F3} {prefix}_p99={Percentile(ordered, 0.99):F3} {prefix}_max={ordered[^1]:F3}";
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private sealed record InteractionMeasurement(
        string Name,
        int QueueDepthAtInteraction,
        double OldestQueuedWorkAgeMilliseconds,
        double WpfCallbackWaitMilliseconds,
        double InteractionToStateMilliseconds,
        double StateToPresentationOpportunityMilliseconds,
        double InteractionToPresentationOpportunityMilliseconds,
        bool TrafficWasActive);

    private static async Task CloseProbeAsync(
        string scenario,
        MainWindow window,
        DemoScenario demo,
        NetworkWorkspace alpha,
        NetworkWorkspace beta)
    {
        var viewModel = window.ViewModel;
        var trackedTransports = new List<FakeIrcTransport> { demo.AlphaTransport, demo.BetaTransport };
        using var trafficCancellation = new CancellationTokenSource();
        Task traffic = Task.CompletedTask;

        switch (scenario)
        {
            case "close-idle":
                await viewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                viewModel.SelectView(alpha.StatusView);
                await viewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                break;
            case "close-sustained":
                traffic = Task.WhenAll(
                    ProduceTrafficAsync(demo.AlphaTransport, "#general", "phase1s-close-alpha", 6_000, 32, 1, cancellationToken: trafficCancellation.Token),
                    ProduceTrafficAsync(demo.BetaTransport, "#general", "phase1s-close-beta", 600, 24, 2, cancellationToken: trafficCancellation.Token));
                await Task.Yield();
                break;
            case "close-backlog":
                EnqueueBacklog(demo.AlphaTransport, "#general", "phase1s-backlog-alpha", 10_000);
                EnqueueBacklog(demo.BetaTransport, "#general", "phase1s-backlog-beta", 2_000);
                await WaitForQueuePressureAsync(viewModel.Sessions, 256).ConfigureAwait(true);
                break;
            case "close-reconnect":
                demo.AlphaTransport.EnqueueRemoteDisconnect();
                await WaitForAsync(
                    viewModel.Sessions,
                    () => alpha.State == NetworkDisplayState.ReconnectWaiting || alpha.State == NetworkDisplayState.Connecting,
                    "the reconnect close probe did not reach a reconnect boundary", 4_000).ConfigureAwait(true);
                break;
            case "close-partial":
                {
                    var partialTransport = new FakeIrcTransport(new IrcEndpoint("demo.partial.invalid", 6667, false));
                    demo.AddTransport(partialTransport);
                    trackedTransports.Add(partialTransport);
                    var partial = viewModel.Sessions.Add(new NetworkConnectionOptions
                    {
                        DisplayName = "PartialNet",
                        Endpoint = partialTransport.Endpoint,
                        Nickname = "nexPartial",
                        Username = "nexPartial",
                        RealName = "Phase 1S partial connection close",
                        RequestedCapabilities = Array.Empty<string>(),
                        Reconnect = new ReconnectPolicy(Enabled: false)
                    });
                    await viewModel.Sessions.ConnectAsync(partial.Id).ConfigureAwait(true);
                    await WaitForAsync(viewModel.Sessions, () => partialTransport.ConnectCount == 1, "partial transport did not connect", 4_000).ConfigureAwait(true);
                    await WaitForAsync(
                        viewModel.Sessions,
                        () => partial.State is NetworkDisplayState.Connecting or NetworkDisplayState.Connected or NetworkDisplayState.CapNegotiation or NetworkDisplayState.Registering,
                        "partial connection did not remain before registration", 4_000).ConfigureAwait(true);
                    Require(partial.State != NetworkDisplayState.Registered, "partial close probe unexpectedly registered its session");
                    break;
                }
            case "close-registered":
                Require(alpha.State == NetworkDisplayState.Registered && beta.State == NetworkDisplayState.Registered, "registered close probe did not reach registered sessions");
                await viewModel.Sessions.FlushStateDispatchAsync().ConfigureAwait(true);
                break;
            case "close-persistence":
                EnqueueBacklog(demo.AlphaTransport, "#general", "phase1s-persistence", 1_000);
                await WaitForQueuePressureAsync(viewModel.Sessions, 64).ConfigureAwait(true);
                break;
            case "close-interacted":
                {
                    var channel = RequiredChannel(alpha);
                    var query = viewModel.Sessions.EnsureQuery(alpha.Id, "CloseProbePeer");
                    viewModel.SelectView(channel);
                    viewModel.PrepareInput("draft retained until natural close");
                    viewModel.SelectView(beta.StatusView);
                    viewModel.SelectView(query);
                    Require(viewModel.InputText.Length == 0, "the interacted close probe did not switch to the query input state");
                    viewModel.SelectView(channel);
                    Require(viewModel.InputText == "draft retained until natural close", "the interacted close probe lost its draft");
                    break;
                }
            default:
                throw new ArgumentException($"Unknown close probe '{scenario}'.", nameof(scenario));
        }

        var readyDiagnostics = viewModel.Sessions.Diagnostics;
        Console.WriteLine(
            $"READY_UI_CLOSE scenario={scenario} state={alpha.State} queue_depth={readyDiagnostics.CurrentQueueDepth} "
            + $"oldest_queued_age_ms={readyDiagnostics.StateDispatch.CurrentOldestQueuedWorkAgeMilliseconds:F3} max_queue_depth={readyDiagnostics.MaximumQueueDepth} "
            + $"traffic_active={!traffic.IsCompleted}");

        await window.WaitForClosedAsync().ConfigureAwait(true);
        trafficCancellation.Cancel();
        try
        {
            await traffic.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (trafficCancellation.IsCancellationRequested)
        {
        }

        Require(!window.IsPresentationRenderingSubscribed, $"close probe '{scenario}' left the rendering handler subscribed");
        Require(trackedTransports.All(transport => transport.CallbackSubscriptionCount == 0 && transport.ActiveReadCount == 0), $"close probe '{scenario}' left transport ownership active");
        var quitCount = trackedTransports.Sum(transport => transport.OutboundLines.Count(line => line.StartsWith("QUIT", StringComparison.Ordinal)));
        var disposedCount = trackedTransports.Count(transport => transport.IsDisposed);
        Console.WriteLine(
            $"CLOSE_UI_METRICS scenario={scenario} max_queue_depth={readyDiagnostics.MaximumQueueDepth} "
            + $"queue_at_ready={readyDiagnostics.CurrentQueueDepth} quit_count={quitCount} disposed_transports={disposedCount}/{trackedTransports.Count} "
            + $"rendering_handler_attached={window.IsPresentationRenderingSubscribed} callback_subscriptions={trackedTransports.Sum(transport => transport.CallbackSubscriptionCount)} "
            + $"active_reads={trackedTransports.Sum(transport => transport.ActiveReadCount)}");
    }

    private static void EnqueueBacklog(FakeIrcTransport transport, string channel, string prefix, int count)
    {
        for (var index = 0; index < count; index++)
        {
            transport.EnqueueInboundLine($":{prefix}!stream@demo PRIVMSG {channel} :{prefix}-{index:00000}");
        }
    }

    private static async Task WaitForQueuePressureAsync(NetworkSessionManager sessions, int minimumMaximumQueueDepth)
    {
        for (var attempt = 0; attempt < 96 && sessions.Diagnostics.MaximumQueueDepth < minimumMaximumQueueDepth; attempt++)
        {
            await Task.Yield();
        }

        Require(
            sessions.Diagnostics.MaximumQueueDepth >= minimumMaximumQueueDepth,
            $"the close probe did not create the required accepted-work pressure (max={sessions.Diagnostics.MaximumQueueDepth})");
    }

    private static async Task QueryNickAsync(MainWindow window, DemoScenario demo, NetworkWorkspace network)
    {
        var viewModel = window.ViewModel;
        var channel = RequiredChannel(network);
        var query = viewModel.Sessions.EnsureQuery(network.Id, "Alex");
        var historyKey = query.HistoryConversationKey;
        viewModel.SelectView(query);
        const string draft = "draft survives the query nickname transition";
        viewModel.PrepareInput(draft);
        viewModel.SelectView(network.StatusView);

        demo.AlphaTransport.EnqueueInboundLine(":Alex!demo@alpha NICK Alex2");
        await WaitForAsync(viewModel.Sessions, () => query.Nickname == "Alex2" && channel.MembersSnapshot.Any(member => member.Nickname == "Alex2"), "query nickname transition was not projected").ConfigureAwait(true);
        Require(ReferenceEquals(query, network.Queries.Single(item => item.Nickname == "Alex2")), "query nickname transition created a duplicate view");
        Require(query.HistoryConversationKey == historyKey, "query nickname transition changed the stable history identity");
        Require(query.EntriesSnapshot.Any(entry => entry.Kind == TranscriptEntryKind.Nick && entry.Text.Contains("Alex2", StringComparison.Ordinal)), "query nickname transition was not rendered in the query transcript");

        viewModel.SelectView(query);
        Require(viewModel.InputText == draft, "query draft was not restored after the nickname transition");
        Require(viewModel.CloseActiveView(), "renamed query did not close through the UI view-model path");
        var renamedMember = RequiredMember(channel, "Alex2");
        viewModel.OpenParticipantQuery(viewModel.CreateParticipantContext(network, channel, renamedMember));
        Require(ReferenceEquals(viewModel.ActiveView, query), "participant reopen created a duplicate query after nickname transition");
        Require(viewModel.InputText == draft, "query draft was not restored after close and participant reopen");
        Console.WriteLine($"QUERY_NICK_UI_METRICS same_view=true history_key_stable=true draft_restored=true query_count={network.Queries.Count}");
    }

    private static void Register(FakeIrcTransport transport, string nickname)
    {
        transport.EnqueueInboundLine(":alpha.server CAP * LS :");
        transport.EnqueueInboundLine($":alpha.server 005 {nickname} PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst :features");
        transport.EnqueueInboundLine($":alpha.server 001 {nickname} :Welcome");
    }

    private static ChannelView RequiredChannel(NetworkWorkspace network) =>
        network.Channels.Single(channel => channel.Channel == "#general");

    private static ChannelMemberView RequiredMember(ChannelView channel, string nickname) =>
        channel.Members.Single(member => member.Nickname == nickname);

    private static ParticipantMenuGroup RequiredGroup(IReadOnlyList<ParticipantMenuGroup> groups, string header) =>
        groups.Single(group => group.Header == header);

    private static async Task WaitForAsync(NetworkSessionManager sessions, Func<bool> condition, string failure, int timeoutMilliseconds = 4_000)
    {
        if (condition())
        {
            return;
        }

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Signal(object? sender, EventArgs args)
        {
            if (condition())
            {
                completed.TrySetResult(true);
            }
        }

        sessions.NavigationChanged += Signal;
        sessions.OperationFeedback.PropertyChanged += Signal;
        try
        {
            Signal(null, EventArgs.Empty);
            await Task.WhenAny(completed.Task, Task.Delay(timeoutMilliseconds)).ConfigureAwait(true);
            if (!condition())
            {
                throw new TimeoutException(failure);
            }
        }
        finally
        {
            sessions.NavigationChanged -= Signal;
            sessions.OperationFeedback.PropertyChanged -= Signal;
        }
    }

    private static async Task WaitForPollingAsync(Func<bool> condition, string failure, int timeoutMilliseconds = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(failure);
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
