using System.IO;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using IrcCapabilityCatalog = nexIRC.Core.State.IrcCapabilityCatalog;
using nexIRC.Networking.Testing;

namespace nexIRC.Desktop;

public sealed class DemoScenario
{
    private readonly FakeIrcTransportFactory _factory;
    private readonly FakeIrcTransport _alpha;
    private readonly FakeIrcTransport _beta;

    private DemoScenario(FakeIrcTransportFactory factory, FakeIrcTransport alpha, FakeIrcTransport beta)
    {
        _factory = factory;
        _alpha = alpha;
        _beta = beta;
    }

    internal FakeIrcTransport AlphaTransport => _alpha;

    internal FakeIrcTransport BetaTransport => _beta;

    internal FakeIrcTransport AddAlphaReconnectTransport()
    {
        var reconnect = new FakeIrcTransport(_alpha.Endpoint);
        _factory.Add(reconnect);
        return reconnect;
    }

    internal void EnqueuePhase1YRegistration(FakeIrcTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        Register(transport, "alpha.server", "nexAlpha", "AlphaNet", "(qaohv)~&@%+", "beI,k,l,imnpst", "batch", "draft/chathistory", "draft/event-playback", "message-tags", "server-time");
    }

    internal void AddTransport(FakeIrcTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _factory.Add(transport);
    }

    internal async Task<(NetworkWorkspace Alpha, NetworkWorkspace Beta)> SeedSmokeAsync(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var sessions = viewModel.Sessions;
        var alpha = sessions.Add(Options("AlphaNet", _alpha.Endpoint, "nexAlpha", "#general") with
        {
            RequestedCapabilities = IrcCapabilityCatalog.PreferredPhase1Y,
            Reconnect = new ReconnectPolicy(
                Enabled: true,
                MaximumAttempts: 3,
                InitialDelay: TimeSpan.FromMilliseconds(10),
                MaximumDelay: TimeSpan.FromMilliseconds(50))
        });
        var beta = sessions.Add(Options("BetaNet", _beta.Endpoint, "nexBeta", "#general") with
        {
            RequestedCapabilities = Array.Empty<string>()
        });

        await sessions.ConnectAsync(alpha.Id).ConfigureAwait(true);
        await sessions.ConnectAsync(beta.Id).ConfigureAwait(true);
        await WaitForConditionAsync(sessions, () => _alpha.ConnectCount == 1 && _beta.ConnectCount == 1, "fake transports did not connect").ConfigureAwait(true);

        Register(_alpha, "alpha.server", "nexAlpha", "AlphaNet", "(qaohv)~&@%+", "beI,k,l,imnpst", "batch", "draft/chathistory", "message-tags", "server-time");
        Register(_beta, "beta.server", "nexBeta", "BetaNet", "(ov)@+", "be,k,s,im");
        EnqueueSmokeChannel(_alpha, "alpha.server", "nexAlpha", "#general", "Alpha topic", "alpha-setter", "@nexAlpha +Alex", "+nt");
        EnqueueSmokeChannel(_beta, "beta.server", "nexBeta", "#general", "Beta topic", "beta-setter", "+nexBeta Alex", "+i");

        await WaitForConditionAsync(sessions, () =>
            alpha.State == NetworkDisplayState.Registered
            && beta.State == NetworkDisplayState.Registered
            && alpha.Channels.Any(channel => channel.IsJoined && channel.Members.Any(member => member.Nickname == "Alex"))
            && beta.Channels.Any(channel => channel.IsJoined && channel.Members.Any(member => member.Nickname == "Alex")),
            "smoke channels did not reach their deterministic joined state").ConfigureAwait(true);
        return (alpha, beta);
    }

    public static IIrcTransportFactory CreateFactory(out DemoScenario scenario)
    {
        var factory = new FakeIrcTransportFactory();
        var alpha = new FakeIrcTransport(new IrcEndpoint("demo.alpha.invalid", 6667, false));
        var beta = new FakeIrcTransport(new IrcEndpoint("demo.beta.invalid", 6667, false));
        factory.Add(alpha);
        factory.Add(beta);
        scenario = new DemoScenario(factory, alpha, beta);
        return factory;
    }

    public async Task SeedAsync(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var sessions = viewModel.Sessions;
        var alphaProfile = new NetworkProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = "AlphaNet",
            Host = _alpha.Endpoint.Host,
            Port = _alpha.Endpoint.Port,
            UseTls = _alpha.Endpoint.UseTls,
            Nickname = "nexAlpha",
            Username = "nexAlpha",
            RealName = "nexIRC 5 deterministic demo",
            AutoJoinChannels = ["#general", "#alpha", "#lounge"]
        };
        var betaProfile = alphaProfile with
        {
            Id = Guid.NewGuid(),
            DisplayName = "BetaNet",
            Host = _beta.Endpoint.Host,
            Nickname = "nexBeta",
            AutoJoinChannels = ["#general", "#lounge"]
        };
        sessions.Configuration?.Profiles.AddOrUpdate(alphaProfile);
        sessions.Configuration?.Profiles.AddOrUpdate(betaProfile);
        var alpha = sessions.Add(Options("AlphaNet", _alpha.Endpoint, "nexAlpha", "#general", "#alpha", "#lounge") with { ProfileId = alphaProfile.Id });
        var beta = sessions.Add(Options("BetaNet", _beta.Endpoint, "nexBeta", "#general", "#lounge") with { ProfileId = betaProfile.Id });
        await sessions.ConnectAsync(alpha.Id).ConfigureAwait(true);
        await sessions.ConnectAsync(beta.Id).ConfigureAwait(true);
        await WaitForAsync(() => _alpha.ConnectCount == 1 && _beta.ConnectCount == 1).ConfigureAwait(true);

        Register(_alpha, "alpha.server", "nexAlpha", "AlphaNet", "(qaohv)~&@%+", "beI,k,l,imnpst");
        Register(_beta, "beta.server", "nexBeta", "BetaNet", "(ov)@+", "be,k,s,im");

        _alpha.EnqueueInboundLine(":nexAlpha!demo@alpha JOIN #general");
        _alpha.EnqueueInboundLine(":nexAlpha!demo@alpha JOIN #alpha");
        _alpha.EnqueueInboundLine(":nexAlpha!demo@alpha JOIN #lounge");
        _alpha.EnqueueInboundLine(":alpha.server 353 nexAlpha = #general :~nexAlpha @Mira +Alex Rook");
        _alpha.EnqueueInboundLine(":alpha.server 352 nexAlpha #general alex alpha.example alpha.server Alex H :0 Alex Alpha participant");
        _alpha.EnqueueInboundLine(":alpha.server 366 nexAlpha #general :End of names");
        _alpha.EnqueueInboundLine(":alpha.server 332 nexAlpha #general :The participant action showcase");
        _alpha.EnqueueInboundLine(":alpha.server 333 nexAlpha #general alpha-setter 1700000000");
        _alpha.EnqueueInboundLine(":alpha.server 324 nexAlpha #general +nt");
        _alpha.EnqueueInboundLine(":alpha.server 353 nexAlpha = #alpha :~nexAlpha @Mira +Rook");
        _alpha.EnqueueInboundLine(":alpha.server 366 nexAlpha #alpha :End of names");
        _alpha.EnqueueInboundLine(":alpha.server 332 nexAlpha #alpha :A calm place for testing the nexIRC shell");
        _alpha.EnqueueInboundLine(":alpha.server MODE #alpha +nt");
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG #alpha :Welcome to the Alpha network.");
        _alpha.EnqueueInboundLine(":Rook!r@alpha PRIVMSG #alpha :Try /join, /me, or /query in the command line.");
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG #alpha :nexAlpha, this is an important highlight.");
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG nexAlpha :This private query is intentionally highlighted.");
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG nexAlpha :\u0001VERSION\u0001");
        _alpha.EnqueueInboundLine(":Rook!r@alpha NICK RookAway");
        _alpha.EnqueueInboundLine(":alpha.server 311 nexAlpha Mira mira alpha.example * :Mira Demo User");
        _alpha.EnqueueInboundLine(":alpha.server 312 nexAlpha Mira alpha.server :AlphaNet IRC services");
        _alpha.EnqueueInboundLine(":alpha.server 313 nexAlpha Mira :is an IRC operator");
        _alpha.EnqueueInboundLine(":alpha.server 317 nexAlpha Mira 42 1735689600 :seconds idle, signon time");
        _alpha.EnqueueInboundLine(":alpha.server 319 nexAlpha Mira :@#alpha +#lounge");
        _alpha.EnqueueInboundLine(":alpha.server 330 nexAlpha Mira mira-account :is logged in as");
        _alpha.EnqueueInboundLine(":alpha.server 338 nexAlpha Mira :is using a secure connection");
        _alpha.EnqueueInboundLine(":alpha.server 318 nexAlpha Mira :End of WHOIS list");
        _alpha.EnqueueInboundLine(":alpha.server 321 nexAlpha Channel :Users Name");
        _alpha.EnqueueInboundLine(":alpha.server 322 nexAlpha #alpha 12 :A calm place for testing");
        _alpha.EnqueueInboundLine(":alpha.server 322 nexAlpha #lounge 7 :Shared channel name on AlphaNet");
        _alpha.EnqueueInboundLine(":alpha.server 322 nexAlpha #random 0 :");
        _alpha.EnqueueInboundLine(":alpha.server 323 nexAlpha :End of LIST");

        _beta.EnqueueInboundLine(":nexBeta!demo@beta JOIN #general");
        _beta.EnqueueInboundLine(":nexBeta!demo@beta JOIN #lounge");
        _beta.EnqueueInboundLine(":beta.server 353 nexBeta = #general :@nexBeta Alex +Mira");
        _beta.EnqueueInboundLine(":beta.server 352 nexBeta #general alex beta.example beta.server Alex H :0 Alex Beta participant");
        _beta.EnqueueInboundLine(":beta.server 366 nexBeta #general :End of names");
        _beta.EnqueueInboundLine(":beta.server 332 nexBeta #general :The same #general name on another network");
        _beta.EnqueueInboundLine(":beta.server 333 nexBeta #general beta-setter 1700000100");
        _beta.EnqueueInboundLine(":beta.server 324 nexBeta #general +i");
        _beta.EnqueueInboundLine(":beta.server 353 nexBeta = #lounge :@nexBeta +Mira");
        _beta.EnqueueInboundLine(":beta.server 366 nexBeta #lounge :End of names");
        _beta.EnqueueInboundLine(":beta.server 332 nexBeta #lounge :Beta network topic");
        _beta.EnqueueInboundLine(":Mira!m@beta PRIVMSG #lounge :Events from BetaNet stay in BetaNet.");
        _beta.EnqueueInboundLine(":Mira!m@beta PRIVMSG nexBeta :The same nickname is a different BetaNet query.");
        _beta.EnqueueInboundLine(":beta.server NOTICE nexBeta :Status notices are rendered in the server view.");

        await WaitForAsync(() => alpha.State == NetworkDisplayState.Registered
            && beta.State == NetworkDisplayState.Registered
            && alpha.Channels.Any(channel => channel.Channel == "#general" && channel.Members.Any(member => member.Nickname == "Alex"))
            && beta.Channels.Any(channel => channel.Channel == "#general" && channel.Members.Any(member => member.Nickname == "Alex"))).ConfigureAwait(true);

        sessions.Configuration?.AddOrUpdateAlias(new AliasDefinition { Name = "j", Expansion = "/join $1", Description = "Join a channel quickly." });
        sessions.Configuration?.AddOrUpdateAlias(new AliasDefinition { Name = "w", Expansion = "/whois $1", Description = "Open WHOIS quickly." });
        sessions.Configuration?.AddOrUpdateAlias(new AliasDefinition { Name = "where", Expansion = "/raw NOTICE $target :$network/$profile/$me@$server/$$ $*", Description = "Show safe contextual alias values." });
        sessions.Configuration?.AddFavoriteGroup("Team Nexgen");
        var groupId = sessions.Configuration?.FavoriteGroups.FirstOrDefault(item => item.Name == "Team Nexgen")?.Id
            ?? NavigationDefaults.DefaultFavoriteGroupId;
        sessions.AddFavorite(alpha.Id, DestinationKind.Channel, "#alpha", "Alpha home");
        sessions.AddFavorite(alpha.Id, DestinationKind.Channel, "#lounge", "Alpha parted example", groupId);
        sessions.AddFavorite(beta.Id, DestinationKind.Channel, "#lounge", "Beta lounge", groupId);
        sessions.AddFavorite(alpha.Id, DestinationKind.Query, "Mira", "Alpha private conversation", groupId);
        sessions.AddFavorite(alpha.Id, DestinationKind.Channel, "#history-00", "Historical-only example", groupId);
        sessions.AddFavorite(beta.Id, DestinationKind.Query, "Mira", "Same nickname on BetaNet", groupId);
        sessions.RecordRecent(alpha, DestinationKind.Channel, "#alpha");
        sessions.RecordRecent(alpha, DestinationKind.Query, "Mira");
        sessions.RecordRecent(beta, DestinationKind.Channel, "#lounge");

        var dispatcher = new IrcCommandDispatcher(sessions);
        var completion = new CompletionEngine(() => sessions.Configuration?.Aliases ?? Array.Empty<AliasDefinition>());
        if (!completion.Complete("/j", 2, alpha, alpha.Channels[0]).Completed)
        {
            throw new InvalidOperationException("The deterministic demo alias completion did not return a result.");
        }
        var alphaChannel = alpha.Channels.First(channel => channel.Channel == "#alpha");
        var participantChannel = alpha.Channels.First(channel => channel.Channel == "#general");
        var participant = participantChannel.Members.First(member => member.Nickname == "Alex");
        var syntheticWhois = await viewModel.ParticipantActions.SendWhoisAsync(
            viewModel.CreateParticipantContext(alpha, participantChannel, participant)).ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server 311 nexAlpha Alex alex alpha.example * :Alex Alpha demo participant");
        _alpha.EnqueueInboundLine(":alpha.server 312 nexAlpha Alex alpha.server :AlphaNet synthetic WHOIS");
        _alpha.EnqueueInboundLine(":alpha.server 313 nexAlpha Alex :is an IRC operator");
        _alpha.EnqueueInboundLine(":alpha.server 317 nexAlpha Alex 12 1735689600 :idle and signon");
        _alpha.EnqueueInboundLine(":alpha.server 319 nexAlpha Alex :@#general +#alpha");
        _alpha.EnqueueInboundLine(":alpha.server 330 nexAlpha Alex alex-account :is logged in as");
        _alpha.EnqueueInboundLine(":alpha.server 671 nexAlpha Alex :is using a secure connection");
        _alpha.EnqueueInboundLine(":alpha.server 318 nexAlpha Alex :End of WHOIS list");
        await WaitForAsync(() => syntheticWhois.View is WhoisView whois && whois.IsCompleted).ConfigureAwait(true);

        var actionService = viewModel.ParticipantActions;
        var alexContext = viewModel.CreateParticipantContext(alpha, participantChannel, participant);
        var removeVoice = await actionService.SetPrivilegeAsync(alexContext, 'v', adding: false).ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server MODE #general -v Alex");
        await WaitForAsync(() => removeVoice.Operation is not null
            && sessions.TryGetOperation(removeVoice.Operation.Id, out var removedVoice)
            && removedVoice!.State == IrcOperationState.Confirmed).ConfigureAwait(true);
        var giveVoice = await actionService.SetPrivilegeAsync(alexContext, 'v', adding: true).ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server MODE #general +v Alex");
        await WaitForAsync(() => giveVoice.Operation is not null
            && sessions.TryGetOperation(giveVoice.Operation.Id, out var givenVoice)
            && givenVoice!.State == IrcOperationState.Confirmed).ConfigureAwait(true);

        var failedBan = await actionService.BanAsync(alexContext, "*!*@demo.invalid").ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server 482 nexAlpha #general :You need channel operator privileges");
        await WaitForAsync(() => failedBan.Operation is not null
            && sessions.TryGetOperation(failedBan.Operation.Id, out var rejectedBan)
            && rejectedBan!.State == IrcOperationState.Rejected).ConfigureAwait(true);

        var invite = await actionService.InviteAsync(alexContext, "#alpha").ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server 341 nexAlpha Alex #alpha :Inviting");
        await WaitForAsync(() => invite.Operation is not null
            && sessions.TryGetOperation(invite.Operation.Id, out var inviting)
            && inviting!.State == IrcOperationState.Confirmed).ConfigureAwait(true);
        var alreadyInvited = await actionService.InviteAsync(alexContext, "#alpha").ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server 443 nexAlpha Alex #alpha :is already on channel");
        await WaitForAsync(() => alreadyInvited.Operation is not null
            && sessions.TryGetOperation(alreadyInvited.Operation.Id, out var alreadyThere)
            && alreadyThere!.State == IrcOperationState.Rejected).ConfigureAwait(true);

        var banListResult = await actionService.OpenBanListAsync(alexContext).ConfigureAwait(true);
        if (banListResult.View is not BanListView banList)
        {
            throw new InvalidOperationException("The deterministic demo could not open its ban-list view.");
        }

        _alpha.EnqueueInboundLine(":alpha.server 367 nexAlpha #general *!*@old.demo setter 1700000000");
        _alpha.EnqueueInboundLine(":alpha.server 367 nexAlpha #general *!*@quiet.demo");
        _alpha.EnqueueInboundLine(":alpha.server 367 nexAlpha #general *!*@temp.demo");
        _alpha.EnqueueInboundLine(":alpha.server 368 nexAlpha #general :End of channel ban list");
        await WaitForAsync(() => banList.IsCompleted && banList.Result.Entries.Count == 3).ConfigureAwait(true);
        banList.Result.SelectedEntry = banList.Result.EntriesSnapshot[0];
        var unban = await actionService.RemoveBanAsync(banList, "*!*@old.demo").ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server MODE #general -b *!*@old.demo");
        await WaitForAsync(() => unban.Operation is not null
            && sessions.TryGetOperation(unban.Operation.Id, out var confirmedUnban)
            && confirmedUnban!.State == IrcOperationState.Confirmed).ConfigureAwait(true);
        await actionService.RefreshBanListAsync(banList).ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server 367 nexAlpha #general *!*@quiet.demo");
        _alpha.EnqueueInboundLine(":alpha.server 367 nexAlpha #general *!*@temp.demo");
        _alpha.EnqueueInboundLine(":alpha.server 368 nexAlpha #general :End of channel ban list");
        await WaitForAsync(() => banList.IsCompleted && banList.Result.Entries.Count == 2).ConfigureAwait(true);

        var missingWhois = await sessions.RequestWhoisAsync(alpha.Id, "MissingDemo").ConfigureAwait(true);
        _alpha.EnqueueInboundLine(":alpha.server 401 nexAlpha MissingDemo :No such nickname");
        await WaitForAsync(() => missingWhois.View is WhoisView missing && !missing.IsLoading).ConfigureAwait(true);

        var kickedMember = participantChannel.Members.FirstOrDefault(member => member.Nickname == "RookAway");
        if (kickedMember is not null)
        {
            var kick = await actionService.KickAsync(viewModel.CreateParticipantContext(alpha, participantChannel, kickedMember), "demo cleanup").ConfigureAwait(true);
            _alpha.EnqueueInboundLine(":alpha.server KICK #general RookAway :demo cleanup");
            await WaitForAsync(() => kick.Operation is not null
                && sessions.TryGetOperation(kick.Operation.Id, out var confirmedKick)
                && confirmedKick!.State == IrcOperationState.Confirmed).ConfigureAwait(true);
        }

        await dispatcher.DispatchAsync(alpha, alphaChannel, "/j #lounge").ConfigureAwait(true);
        await dispatcher.DispatchAsync(alpha, alphaChannel, "Local AlphaNet message").ConfigureAwait(true);
        await dispatcher.DispatchAsync(alpha, alphaChannel, "/me demonstrates a local action").ConfigureAwait(true);
        await dispatcher.DispatchAsync(alpha, alphaChannel, "/msg Mira A local private message").ConfigureAwait(true);
        await dispatcher.DispatchAsync(alpha, alphaChannel, "/where demo-context").ConfigureAwait(true);
        await dispatcher.DispatchAsync(alpha, alpha.Channels.First(channel => channel.Channel == "#lounge"), "/part #lounge demo parted view").ConfigureAwait(true);
        sessions.OpenHistoricalConversation(alpha.Id, DestinationKind.Query, "OldMira");

        if (sessions.LogStore is { } logs)
        {
            var historyScope = alpha.ProfileId ?? alpha.Id;
            var betaHistoryScope = beta.ProfileId ?? beta.Id;
            foreach (var record in new[]
            {
                DemoRecord(alpha.Id, historyScope, LogConversationKind.Channel, "#lounge", 500_101, "same-target marker from AlphaNet", "Mira"),
                DemoRecord(beta.Id, betaHistoryScope, LogConversationKind.Channel, "#lounge", 500_102, "same-target marker from BetaNet", "Rook"),
                DemoRecord(alpha.Id, historyScope, LogConversationKind.Channel, "#history-00", 500_103, "reopen historical marker", "Mira"),
                DemoRecord(alpha.Id, historyScope, LogConversationKind.PrivateConversation, "Mira", 500_104, "private search marker", "Rook")
            })
            {
                await logs.AppendAsync(record).ConfigureAwait(true);
            }

            for (var index = 0; index < 2_000; index++)
            {
                await logs.AppendAsync(new ConversationLogRecord
                {
                    Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(500_000 - index),
                    NetworkId = alpha.Id,
                    ScopeId = alpha.ProfileId ?? alpha.Id,
                    ProfileId = alpha.ProfileId,
                    ConversationKind = LogConversationKind.Channel,
                    ConversationName = "#alpha",
                    ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#alpha"),
                    Sender = index % 2 == 0 ? "Mira" : "Rook",
                    MessageKind = LogMessageKind.Message,
                    Direction = LogDirection.Incoming,
                    Text = index == 1_999
                        ? "Demo history archive segment marker; this old record remains searchable."
                        : $"Demo history page record {index + 1:000}; this is fake deterministic workspace data."
                }).ConfigureAwait(true);
            }
            await logs.FlushAsync().ConfigureAwait(true);
            if (logs is JsonlConversationLogStore jsonLogs
                && Directory.EnumerateFiles(jsonLogs.RootPath, "*.jsonl", SearchOption.AllDirectories).Count() < 2)
            {
                throw new InvalidOperationException("The deterministic demo did not rotate its segmented history.");
            }
            var currentConversationResults = await logs.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                HistoryScopeId = historyScope,
                NetworkId = alpha.Id,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#alpha",
                Text = "Demo history page",
                Sender = "Mira",
                From = DateTimeOffset.UnixEpoch.AddMinutes(499_000),
                To = DateTimeOffset.UnixEpoch.AddMinutes(500_000)
            }).ConfigureAwait(true);
            var crossConversationResults = await logs.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.AllHistory,
                Text = "same-target marker",
                MaximumResults = 10
            }).ConfigureAwait(true);
            var archivedResults = await logs.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                HistoryScopeId = historyScope,
                NetworkId = alpha.Id,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#alpha",
                Text = "archive segment marker",
                MaximumResults = 10
            }).ConfigureAwait(true);
            if (currentConversationResults.Results.Count == 0
                || crossConversationResults.Results.Select(result => result.NetworkId).Distinct().Count() != 2
                || archivedResults.Results.Count != 1)
            {
                throw new InvalidOperationException("The deterministic demo history search scenarios did not return the expected records.");
            }

            var results = await logs.SearchAsync(new ConversationLogQuery { Text = "Welcome", NetworkId = alpha.Id }).ConfigureAwait(true);
            var result = results.Count > 0 ? results[0] : null;
            if (result is not null)
            {
                sessions.ActivateNotification(new IrcNotification(
                    alpha.Id,
                    Guid.Empty,
                    WorkspaceViewKind.Channel,
                    IrcNotificationType.Status,
                    WorkspaceActivity.None,
                    result.Sender,
                    result.Preview,
                    result.Timestamp,
                    false,
                    nameof(DemoScenario),
                    Activation: new NotificationActivationTarget(alpha.Id, alpha.ProfileId, Guid.Empty, WorkspaceViewKind.Channel, result.ConversationName)));
            }
        }

        for (var index = 0; index < 12; index++)
        {
            var historical = sessions.OpenHistoricalConversation(alpha.Id, DestinationKind.Channel, $"#history-{index:00}");
            if (index % 2 == 0)
            {
                sessions.CloseView(historical.Id);
            }
        }

        if (sessions.LogStore is { } searchLogs)
        {
            var reopened = alpha.Channels.FirstOrDefault(channel => channel.Channel == "#history-00");
            if (reopened is not null)
            {
                sessions.CloseView(reopened.Id);
            }

            var reopenResults = await searchLogs.SearchAsync(new ConversationLogQuery { Text = "reopen historical marker", NetworkId = alpha.Id }).ConfigureAwait(true);
            if (reopenResults.Count == 0 || !await viewModel.RouteLogSearchResultAsync(reopenResults[0]).ConfigureAwait(true))
            {
                throw new InvalidOperationException("The deterministic demo could not reopen a historical search result.");
            }
        }

        for (var index = 0; index < 6; index++)
        {
            sessions.OpenHistoricalConversation(beta.Id, DestinationKind.Query, $"ArchiveNick{index:00}");
        }

        var closedQuery = sessions.EnsureQuery(alpha.Id, "ClosedMira");
        sessions.ActivateView(closedQuery.Id);
        sessions.CloseView(closedQuery.Id);
        sessions.OpenHistoricalConversation(alpha.Id, DestinationKind.Query, "ClosedMira");

        var alphaChannelForDraft = sessions.EnsureChannel(alpha.Id, "#alpha");
        viewModel.SelectView(alphaChannelForDraft);
        viewModel.PrepareInput("Draft stays with AlphaNet/#alpha while navigating.");
        var betaQueryForDraft = sessions.EnsureQuery(beta.Id, "Mira");
        viewModel.SelectView(betaQueryForDraft);
        viewModel.PrepareInput("BetaNet/Mira has an independent draft.");

        var pendingDisconnect = await sessions.RequestWhoisAsync(beta.Id, "PendingDisconnectDemo").ConfigureAwait(true);

        await sessions.DisconnectAsync(beta.Id).ConfigureAwait(true);
        await WaitForAsync(() => sessions.TryGetOperation(pendingDisconnect.Operation.Id, out var disconnected)
            && disconnected!.State == IrcOperationState.Disconnected).ConfigureAwait(true);

        _alpha.EnqueueInboundLine(":alpha.server KICK #alpha nexAlpha :deterministic self-kick");
        await WaitForAsync(() => !alpha.Channels.First(channel => channel.Channel == "#alpha").IsJoined).ConfigureAwait(true);

        sessions.ActivateView(alpha.StatusView.Id);
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, params string[] channels) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "nexIRC 5 deterministic demo",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = channels.ToHashSet(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static ConversationLogRecord DemoRecord(
        Guid networkId,
        Guid scopeId,
        LogConversationKind conversationKind,
        string conversationName,
        int minute,
        string text,
        string sender) => new()
        {
            Timestamp = DateTimeOffset.UnixEpoch.AddMinutes(minute),
            NetworkId = networkId,
            ScopeId = scopeId,
            ProfileId = scopeId,
            ConversationKind = conversationKind,
            ConversationName = conversationName,
            ConversationKey = ConversationLoggingService.BuildConversationKey(conversationKind, conversationName),
            Sender = sender,
            MessageKind = LogMessageKind.Message,
            Direction = LogDirection.Incoming,
            Text = text
        };

    private static void Register(FakeIrcTransport transport, string server, string nickname, string network, string prefix, string chanModes, params string[] capabilities)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :{string.Join(' ', capabilities)}");
        if (capabilities.Length > 0)
        {
            transport.EnqueueInboundLine($":{server} CAP * ACK :{string.Join(' ', capabilities)}");
        }
        var history = capabilities.Any(static capability => capability.Equals("draft/chathistory", StringComparison.OrdinalIgnoreCase))
            ? " CHATHISTORY=50 MSGREFTYPES=msgid,timestamp"
            : string.Empty;
        transport.EnqueueInboundLine($":{server} 005 {nickname} NETWORK={network} PREFIX={prefix} CHANMODES={chanModes} CHANTYPES=#&+!{history} :demo features");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to the nexIRC 5 demo workspace");
    }

    private static void EnqueueSmokeChannel(FakeIrcTransport transport, string server, string nickname, string channel, string topic, string setter, string names, string modes)
    {
        transport.EnqueueInboundLine($":{nickname}!demo@{server} JOIN {channel}");
        transport.EnqueueInboundLine($":{server} 332 {nickname} {channel} :{topic}");
        transport.EnqueueInboundLine($":{server} 333 {nickname} {channel} {setter} 1700000000");
        transport.EnqueueInboundLine($":{server} 324 {nickname} {channel} {modes}");
        transport.EnqueueInboundLine($":{server} 353 {nickname} = {channel} :{names}");
        transport.EnqueueInboundLine($":{server} 366 {nickname} {channel} :End of names");
    }

    private static async Task WaitForConditionAsync(NetworkSessionManager sessions, Func<bool> condition, string failure)
    {
        if (condition())
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Signal(object? sender, EventArgs args)
        {
            if (condition())
            {
                completion.TrySetResult(true);
            }
        }

        sessions.NavigationChanged += Signal;
        try
        {
            Signal(null, EventArgs.Empty);
            await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(true);
            if (!condition())
            {
                throw new TimeoutException(failure);
            }
        }
        finally
        {
            sessions.NavigationChanged -= Signal;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic desktop demo did not start in time.");
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }

}
