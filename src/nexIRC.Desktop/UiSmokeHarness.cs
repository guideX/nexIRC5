using System.Diagnostics;
using System.Windows;
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
    private static readonly string[] Scenarios = ["participant", "moderation", "channel-properties", "multi-network", "lifecycle", "read-state", "reconnect", "burst", "burst-fairness", "query-nick"];

    public static bool IsKnownScenario(string? scenario) =>
        scenario is not null && Scenarios.Contains(scenario, StringComparer.OrdinalIgnoreCase);

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
        var selectionPosted = Stopwatch.GetTimestamp();
        var selectionWpfWaitMilliseconds = 0d;
        var selectionExecutionMilliseconds = 0d;
        var selectionBeforeBurstComplete = false;
        await window.Dispatcher.InvokeAsync(() =>
        {
            var selectionStarted = Stopwatch.GetTimestamp();
            selectionWpfWaitMilliseconds = TicksToMilliseconds(selectionStarted - selectionPosted);
            viewModel.SelectView(channel);
            selectionBeforeBurstComplete = !BurstTailReached(channel, burstSize - 1);
            selectionExecutionMilliseconds = TicksToMilliseconds(Stopwatch.GetTimestamp() - selectionStarted);
        }, System.Windows.Threading.DispatcherPriority.Input).Task.ConfigureAwait(true);
        Require(selectionBeforeBurstComplete, "burst selection was not serviced before the complete derived backlog drained");

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
            $"BURST_UI_METRICS events={burstSize} selection_authority_wait_ms=0.000 selection_wpf_wait_ms={selectionWpfWaitMilliseconds:F3} selection_wpf_execution_ms={selectionExecutionMilliseconds:F3} "
            + $"selection_before_burst_complete={selectionBeforeBurstComplete} unread_before_read={unreadBeforeRead} max_authoritative_queue_depth={diagnostics.MaximumQueueDepth} "
            + $"max_wpf_pending={diagnostics.MaximumWpfPendingWorkItems} wpf_posted={diagnostics.WpfWorkItemsPosted} wpf_executed={diagnostics.WpfWorkItemsExecuted} "
            + $"p50_wpf_schedule_wait_ms={diagnostics.P50WpfScheduleWaitMilliseconds:F3} p95_wpf_schedule_wait_ms={diagnostics.P95WpfScheduleWaitMilliseconds:F3} p99_wpf_schedule_wait_ms={diagnostics.P99WpfScheduleWaitMilliseconds:F3} "
            + $"p50_queue_wait_ms={diagnostics.P50QueueWaitMilliseconds:F3} p95_queue_wait_ms={diagnostics.P95QueueWaitMilliseconds:F3} p99_queue_wait_ms={diagnostics.P99QueueWaitMilliseconds:F3} "
            + $"p95_mutation_ms={diagnostics.P95MutationDurationMilliseconds:F3} cooperative_slices={diagnostics.CooperativeSliceCount} cooperative_yields={diagnostics.CooperativeYieldCount} "
            + $"max_slice_work_items={diagnostics.MaximumCooperativeSliceWorkItems} max_slice_ms={diagnostics.MaximumCooperativeSliceDurationMilliseconds:F3} "
            + $"navigation_refresh_requests={presentation.NavigationRefreshRequests} navigation_refresh_executions={presentation.NavigationRefreshExecutions} "
            + $"navigation_refresh_coalesced={presentation.NavigationRefreshCoalescedRequests} tail_ordered=true");
    }

    private static bool BurstTailReached(ChannelView channel, int lastIndex)
    {
        var entries = channel.EntriesSnapshot;
        return entries.Count == WorkspaceView.MaximumEntries
            && entries[^1].Text == $"burst-{lastIndex:0000}";
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
