using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1LServerFeedbackTests
{
    [Theory]
    [InlineData(401, "ERR_NOSUCHNICK")]
    [InlineData(403, "ERR_NOSUCHCHANNEL")]
    [InlineData(404, "ERR_CANNOTSENDTOCHAN")]
    [InlineData(442, "ERR_NOTONCHANNEL")]
    [InlineData(443, "ERR_USERONCHANNEL")]
    [InlineData(461, "ERR_NEEDMOREPARAMS")]
    [InlineData(472, "ERR_UNKNOWNMODE")]
    [InlineData(481, "ERR_NOPRIVILEGES")]
    [InlineData(482, "ERR_CHANOPRIVSNEEDED")]
    public void NumericCatalogProvidesFriendlyAndProtocolDetails(int numeric, string name)
    {
        var message = IrcMessageParser.Parse(numeric switch
        {
            401 => ":srv 401 me Alex :No such nick",
            403 => ":srv 403 me #missing :No such channel",
            404 => ":srv 404 me #room :Cannot send",
            442 => ":srv 442 me #room :Not on channel",
            443 => ":srv 443 me Alex #room :Already there",
            461 => ":srv 461 me MODE :Need more params",
            472 => ":srv 472 me z :Unknown mode",
            481 => ":srv 481 me :No privileges",
            482 => ":srv 482 me #room :Need channel op",
            _ => throw new ArgumentOutOfRangeException(nameof(numeric))
        }).Message!;

        Assert.True(IrcNumericCatalog.TryInterpret(message, out var interpretation));
        Assert.Equal(numeric, interpretation.Numeric);
        Assert.Equal(name, interpretation.Name);
        Assert.NotEmpty(interpretation.FriendlyExplanation);
        Assert.NotEmpty(interpretation.ProtocolText);
        Assert.True(interpretation.IsError);
    }

    [Fact]
    public void InviteAcknowledgementKeepsChannelAndNicknameTargets()
    {
        var message = IrcMessageParser.Parse(":srv 341 me Alex #room :Inviting").Message!;

        Assert.True(IrcNumericCatalog.TryInterpret(message, out var interpretation));
        Assert.Equal("Alex", interpretation.TargetNickname);
        Assert.Equal("#room", interpretation.TargetChannel);
        Assert.True(interpretation.IsSuccess);
    }

    [Fact]
    public void UnknownNumericRemainsAvailableToRawPresentation()
    {
        var message = IrcMessageParser.Parse(":srv 742 me :Future reply").Message!;

        Assert.False(IrcNumericCatalog.TryInterpret(message, out _));
        var rendered = IrcEventPresentation.Render(
            new IrcUnknownNumericEvent(message, 742),
            EmptySnapshot());

        Assert.NotNull(rendered);
        Assert.Contains("742", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("Future reply", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BanListViewModelIsBoundedAndDuplicateMasksReconcile()
    {
        var result = new BanListResult(maximumEntries: 2);
        result.Begin();
        result.Apply(BanItem(":srv 367 me #room *!*@one setter 1700000000"), Guid.NewGuid());
        result.Apply(BanItem(":srv 367 me #room *!*@two"), Guid.NewGuid());
        result.Apply(BanItem(":srv 367 me #room *!*@one replacement 1700000001"), Guid.NewGuid());
        result.Apply(BanItem(":srv 367 me #room *!*@three"), Guid.NewGuid());

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("replacement", result.EntriesSnapshot.Single(item => item.Mask == "*!*@one").SetBy);
        Assert.True(result.WasTruncated);
        Assert.Equal("—", result.EntriesSnapshot.Single(item => item.Mask == "*!*@two").SetByText);
        Assert.Equal("—", result.EntriesSnapshot.Single(item => item.Mask == "*!*@two").SetAtText);
    }

    [Fact]
    public async Task ModeConfirmationUpdatesProjectionAndOperationFeedback()
    {
        var (manager, network, transport) = await ConnectedNetworkAsync("mode-feedback.example", "me", "#room");
        await using (manager)
        {
            var channel = network.Channels.Single();
            var member = channel.Members.Single(item => item.Nickname == "Alex");
            var context = new ParticipantActionContext(network, channel, member, network.Channels);
            var service = new ParticipantActionService(manager);

            var result = await service.SetPrivilegeAsync(context, 'v', adding: false);
            Assert.NotNull(result.Operation);
            await WaitForAsync(() => transport.OutboundLines.Contains("MODE #room -v Alex"));
            transport.EnqueueInboundLine(":srv MODE #room -v Alex");

            await WaitForAsync(() => manager.TryGetOperation(result.Operation!.Id, out var operation)
                && operation!.State == IrcOperationState.Confirmed);
            Assert.DoesNotContain('v', channel.Members.Single(item => item.Nickname == "Alex").PrefixModes);
            var menu = ParticipantActionCatalog.Build(
                new ParticipantActionContext(network, channel, channel.Members.Single(item => item.Nickname == "Alex"), network.Channels),
                isIgnored: false);
            Assert.Contains(menu.SelectMany(group => group.Items), item => item.ModeLetter == 'v' && item.Header.StartsWith("Give", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task NumericRejectionRetainsFriendlyExplanationAndRawLine()
    {
        var (manager, network, transport) = await ConnectedNetworkAsync("rejection.example", "me", "#room");
        await using (manager)
        {
            var channel = network.Channels.Single();
            var member = channel.Members.Single(item => item.Nickname == "Alex");
            var result = await new ParticipantActionService(manager).SetPrivilegeAsync(
                new ParticipantActionContext(network, channel, member, network.Channels),
                'o',
                adding: true);
            await WaitForAsync(() => transport.OutboundLines.Contains("MODE #room +o Alex"));
            transport.EnqueueInboundLine(":srv 482 me #room :You need channel operator privileges");

            await WaitForAsync(() => manager.TryGetOperation(result.Operation!.Id, out var operation)
                && operation!.State == IrcOperationState.Rejected);
            Assert.True(manager.TryGetOperation(result.Operation!.Id, out var rejected));
            Assert.Equal(482, rejected!.Numeric);
            Assert.Contains("permission", rejected.Explanation!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("482", rejected.ProtocolDetail!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("You need channel operator privileges", rejected.RawServerLine!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task KickConfirmationHandlesNicknameRaceAndSelfKickAuthoritatively()
    {
        var (manager, network, transport) = await ConnectedNetworkAsync("kick-feedback.example", "me", "#room");
        await using (manager)
        {
            var channel = network.Channels.Single();
            var member = channel.Members.Single(item => item.Nickname == "Alex");
            var result = await new ParticipantActionService(manager).KickAsync(
                new ParticipantActionContext(network, channel, member, network.Channels),
                "cleanup");
            await WaitForAsync(() => transport.OutboundLines.Contains("KICK #room Alex :cleanup"));
            transport.EnqueueInboundLine(":Alex!u@h NICK Alex2");
            transport.EnqueueInboundLine(":srv KICK #room Alex2 :cleanup");

            await WaitForAsync(() => !channel.MembersSnapshot.Any(item => item.Nickname == "Alex2"));
            await WaitForAsync(() => manager.TryGetOperation(result.Operation!.Id, out var operation)
                && operation!.State == IrcOperationState.Confirmed);

            var joinsBeforeSelfKick = transport.OutboundLines.Count(line => line == "JOIN #room");
            transport.EnqueueInboundLine(":srv KICK #room me :you were removed");
            await WaitForAsync(() => !channel.IsJoined);
            Assert.Equal(joinsBeforeSelfKick, transport.OutboundLines.Count(line => line == "JOIN #room"));
        }
    }

    [Fact]
    public async Task InviteAcknowledgementAndRejectionAreCorrelated()
    {
        var (manager, network, transport) = await ConnectedNetworkAsync("invite-feedback.example", "me", "#room");
        await using (manager)
        {
            transport.EnqueueInboundLine(":me!u@h JOIN #other");
            await WaitForAsync(() => network.Snapshot.Channels.Any(item => item.Name == "#other" && item.IsJoined));
            var channel = network.Channels.Single(item => item.Channel == "#room");
            var member = channel.Members.Single(item => item.Nickname == "Alex");
            var service = new ParticipantActionService(manager);
            var invited = await service.InviteAsync(new ParticipantActionContext(network, channel, member, network.Channels), "#other");
            await WaitForAsync(() => transport.OutboundLines.Contains("INVITE Alex #other"));
            transport.EnqueueInboundLine(":srv 341 me Alex #other :Inviting");
            await WaitForAsync(() => manager.TryGetOperation(invited.Operation!.Id, out var operation)
                && operation!.State == IrcOperationState.Confirmed);

            var alreadyThere = await service.InviteAsync(new ParticipantActionContext(network, channel, member, network.Channels), "#other");
            transport.EnqueueInboundLine(":srv 443 me Alex #other :is already on channel");
            await WaitForAsync(() => manager.TryGetOperation(alreadyThere.Operation!.Id, out var operation)
                && operation!.State == IrcOperationState.Rejected);
        }
    }

    [Fact]
    public async Task BanListEntriesRouteAndCompleteWithoutCrossNetworkContamination()
    {
        var (manager, network, transport) = await ConnectedNetworkAsync("ban-list.example", "me", "#room");
        await using (manager)
        {
            var request = await manager.RequestBanListAsync(network.Id, "#room");
            Assert.IsType<BanListView>(request.View);
            await WaitForAsync(() => transport.OutboundLines.Contains("MODE #room +b"));
            transport.EnqueueInboundLine(":srv 367 me #room *!*@bad.example setter 1700000000");
            transport.EnqueueInboundLine(":srv 367 me #room *!*@quiet.example");
            transport.EnqueueInboundLine(":srv 368 me #room :End of channel ban list");

            var view = (BanListView)request.View;
            await WaitForAsync(() => view.IsCompleted);
            Assert.Equal(["*!*@bad.example", "*!*@quiet.example"], view.Result.EntriesSnapshot.Select(item => item.Mask));
            Assert.Equal("setter", view.Result.EntriesSnapshot[0].SetBy);
            Assert.Null(view.Result.EntriesSnapshot[1].SetBy);
            Assert.True(manager.TryGetOperation(request.Operation.Id, out var operation));
            Assert.Equal(IrcOperationState.Confirmed, operation!.State);
        }
    }

    [Fact]
    public async Task BanAndUnbanCommandsWaitForModeConfirmationAndDoNotMutateRowsOptimistically()
    {
        var (manager, network, transport) = await ConnectedNetworkAsync("ban-edit.example", "me", "#room");
        await using (manager)
        {
            var request = await manager.RequestBanListAsync(network.Id, "#room");
            var view = (BanListView)request.View;
            await WaitForAsync(() => transport.OutboundLines.Contains("MODE #room +b"));
            transport.EnqueueInboundLine(":srv 367 me #room *!*@old.example setter");
            transport.EnqueueInboundLine(":srv 368 me #room :End");
            await WaitForAsync(() => view.IsCompleted);
            view.Result.SelectedEntry = view.Result.EntriesSnapshot.Single();

            var service = new ParticipantActionService(manager);
            var invalid = await service.AddBanAsync(view, "bad\r\nmask");
            Assert.False(invalid.Succeeded);
            Assert.DoesNotContain(transport.OutboundLines, line => line.Contains("bad", StringComparison.Ordinal));

            var add = await service.AddBanAsync(view, "*!*@new.example");
            Assert.Single(view.Result.EntriesSnapshot);
            await WaitForAsync(() => transport.OutboundLines.Contains("MODE #room +b *!*@new.example"));
            transport.EnqueueInboundLine(":srv MODE #room +b *!*@new.example");
            await WaitForAsync(() => manager.TryGetOperation(add.Operation!.Id, out var operation)
                && operation!.State == IrcOperationState.Confirmed);

            var remove = await service.RemoveBanAsync(view, "*!*@old.example");
            Assert.Single(view.Result.EntriesSnapshot);
            await WaitForAsync(() => transport.OutboundLines.Contains("MODE #room -b *!*@old.example"));
            transport.EnqueueInboundLine(":srv MODE #room -b *!*@old.example");
            await WaitForAsync(() => manager.TryGetOperation(remove.Operation!.Id, out var operation)
                && operation!.State == IrcOperationState.Confirmed);
        }
    }

    [Fact]
    public async Task TimeoutAndDisconnectResolvePendingFeedback()
    {
        var endpoint = new IrcEndpoint("lifecycle-feedback.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(
            factory,
            operationTimeouts: new IrcOperationTimeoutPolicy(Moderation: TimeSpan.FromMilliseconds(35)));
        var network = manager.Add(Options(endpoint, "me", "#room"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "me");
        transport.EnqueueInboundLine(":me!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.Single().IsJoined);

        var timedOut = manager.StartOperation(IrcOperationType.ModeChange, network.Id, "#room", "Alex", "+o", command: "MODE");
        await WaitForAsync(() => manager.TryGetOperation(timedOut.Id, out var completed)
            && completed!.State == IrcOperationState.TimedOut);

        var disconnected = manager.StartOperation(IrcOperationType.Invite, network.Id, "#room", "Alex", command: "INVITE");
        await manager.DisconnectAsync(network.Id);
        await WaitForAsync(() => manager.TryGetOperation(disconnected.Id, out var completed)
            && completed!.State == IrcOperationState.Disconnected);
    }

    [Fact]
    public async Task WhoisNoSuchNicknameFailsViewAndOperation()
    {
        var (manager, network, transport) = await ConnectedNetworkAsync("whois-failure.example", "me");
        await using (manager)
        {
            var request = await manager.RequestWhoisAsync(network.Id, "MissingNick");
            transport.EnqueueInboundLine(":srv 401 me MissingNick :No such nick/channel");
            var view = (WhoisView)request.View;
            await WaitForAsync(() => !view.IsLoading);
            Assert.Equal(RichResultState.Failed, view.Result.State);
            Assert.True(manager.TryGetOperation(request.Operation.Id, out var operation));
            Assert.Equal(IrcOperationState.Rejected, operation!.State);
            Assert.Equal(401, operation.Numeric);
        }
    }

    private static IrcBanListItemEvent BanItem(string line)
    {
        var message = IrcMessageParser.Parse(line).Message!;
        var setter = message.Parameters.Count > 3 ? message.Parameters[3] : null;
        DateTimeOffset? setAt = null;
        if (message.Parameters.Count > 4 && long.TryParse(message.Parameters[4], out var unix))
        {
            setAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return new IrcBanListItemEvent(message, new IrcBanListEntry("#room", message.Parameters[2], setter, setAt));
    }

    private static async Task<(NetworkSessionManager Manager, NetworkWorkspace Network, FakeIrcTransport Transport)> ConnectedNetworkAsync(
        string host,
        string nickname,
        string? channel = null)
    {
        var endpoint = new IrcEndpoint(host, 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint, nickname, channel));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, nickname);
        if (channel is not null)
        {
            transport.EnqueueInboundLine($":{nickname}!u@h JOIN {channel}");
            transport.EnqueueInboundLine($":srv 353 {nickname} = {channel} :@{nickname} +Alex");
            transport.EnqueueInboundLine($":srv 366 {nickname} {channel} :End");
            await WaitForAsync(() => network.Channels.Any(item => item.Channel == channel && item.IsJoined && item.Members.Any(member => member.Nickname == "Alex")));
        }

        return (manager, network, transport);
    }

    private static NetworkConnectionOptions Options(IrcEndpoint endpoint, string nickname, string? channel) => new()
    {
        DisplayName = endpoint.Host,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1L test",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = channel is null ? new HashSet<string>(StringComparer.Ordinal) : [channel],
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine($":srv 005 {nickname} PREFIX=(qaohv)~&@%+ CHANMODES=b,k,l,imnpst :features");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;

    private static ServerSessionSnapshot EmptySnapshot() => new(
        ServerSessionState.Disconnected,
        RegistrationState.NotStarted,
        "me",
        "user",
        "real",
        new IrcEndpoint("empty.example", 6667, false),
        0,
        CapabilitySnapshot.Empty,
        ISupportSnapshot.Empty,
        ServerIdentity.Unknown,
        ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, null, ServerIdentity.Unknown),
        null,
        Array.Empty<IrcChannelSnapshot>(),
        Array.Empty<IrcQuerySnapshot>(),
        new HashSet<string>());

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1L test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
