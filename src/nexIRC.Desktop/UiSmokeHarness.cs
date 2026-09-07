using System.Diagnostics;
using System.Windows;
using System.Globalization;
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
        var noisyTraffic = ProduceTrafficAsync(
            demo.AlphaTransport,
            "#general",
            "phase1s-alpha",
            noisyTrafficCount,
            batchSize: 32,
            pacingMilliseconds: 1);
        var quietTraffic = ProduceTrafficAsync(
            demo.BetaTransport,
            "#general",
            "phase1s-beta",
            quietTrafficCount,
            batchSize: 24,
            pacingMilliseconds: 2);
        await Task.Yield();

        var measurements = new List<InteractionMeasurement>();
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
        var quietUnreadObserved = false;
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
        CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transport.EnqueueInboundLine($":{prefix}!stream@demo PRIVMSG {channel} :{prefix}-{index:00000}");
            if ((index + 1) % batchSize == 0)
            {
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
                    ProduceTrafficAsync(demo.AlphaTransport, "#general", "phase1s-close-alpha", 6_000, 32, 1, trafficCancellation.Token),
                    ProduceTrafficAsync(demo.BetaTransport, "#general", "phase1s-close-beta", 600, 24, 2, trafficCancellation.Token));
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
