using System.Windows;
using nexIRC.Application;

namespace nexIRC.Desktop;

/// <summary>
/// Deterministic, in-process GUI-path smoke tests. It uses only DemoScenario's
/// fake transports and invokes the same application services used by WPF
/// actions; it never targets HWNDs or production sessions.
/// </summary>
internal static class UiSmokeHarness
{
    private static readonly string[] Scenarios = ["participant", "moderation", "channel-properties", "multi-network"];

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

    private static ChannelView RequiredChannel(NetworkWorkspace network) =>
        network.Channels.Single(channel => channel.Channel == "#general");

    private static ChannelMemberView RequiredMember(ChannelView channel, string nickname) =>
        channel.Members.Single(member => member.Nickname == nickname);

    private static ParticipantMenuGroup RequiredGroup(IReadOnlyList<ParticipantMenuGroup> groups, string header) =>
        groups.Single(group => group.Header == header);

    private static async Task WaitForAsync(NetworkSessionManager sessions, Func<bool> condition, string failure)
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
            await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(true);
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
