using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1UContextualActionTests
{
    [Fact]
    public void ParserPreservesTrailingArgumentsAndOptionalColonSyntax()
    {
        var message = IrcCommandParser.Parse(" /MSG Alice hello there  ");
        Assert.Equal("MSG", message.Name);
        Assert.Equal("Alice hello there  ", message.Arguments);
        Assert.Equal("hello there  ", message.RemainderAfterToken(1));
        Assert.Equal(("Alice", "hello there  "), IrcCommandParser.SplitTargetAndText(message.Arguments));

        var topic = IrcCommandParser.Parse("/topic #room this is the new topic");
        Assert.Equal("this is the new topic", topic.RemainderAfterToken(1));

        var away = IrcCommandParser.Parse("/away :gone for lunch");
        Assert.Equal(["gone for lunch"], away.Tokens);
        Assert.Equal("gone for lunch", away.RemainderAfterToken(0));
        Assert.False(IrcCommandParser.TryParse("notice Alice hi", out _, out var error));
        Assert.Contains("start with", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouterUsesNetworkQualifiedContextActionsAndStateEnablement()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("actions.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Actions", transport.Endpoint, "operator", "#room"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "operator", "(qaohv)~&@%+", "be,k,l,imnpst");
        transport.EnqueueInboundLine(":operator!u@host JOIN #room");
        transport.EnqueueInboundLine(":srv 353 operator = #room :@operator +Alex");
        transport.EnqueueInboundLine(":srv 366 operator #room :End");
        await WaitForAsync(() => network.Channels.Single().IsJoined && network.Channels.Single().Members.Count == 2);

        var router = new WorkspaceActionRouter(manager);
        var channel = network.Channels.Single();
        var networkActions = router.BuildNetworkActions(network);
        Assert.False(networkActions.Single(item => item.Action == WorkspaceActionId.Connect).IsEnabled);
        Assert.True(networkActions.Single(item => item.Action == WorkspaceActionId.Disconnect).IsEnabled);
        Assert.True(networkActions.Single(item => item.Action == WorkspaceActionId.Reconnect).IsEnabled);

        var channelActions = router.BuildChannelActions(network, channel);
        Assert.False(channelActions.Single(item => item.Action == WorkspaceActionId.JoinChannel).IsEnabled);
        Assert.True(channelActions.Single(item => item.Action == WorkspaceActionId.PartChannel).IsEnabled);
        Assert.True(channelActions.Single(item => item.Action == WorkspaceActionId.RefreshNames).IsEnabled);

        var refresh = await router.ExecuteChannelAsync(network, channel, WorkspaceActionId.RefreshNames);
        Assert.True(refresh.Succeeded);
        await WaitForAsync(() => transport.OutboundLines.Contains("NAMES #room"));

        await manager.DisconnectAsync(network.Id);
        var disconnected = router.BuildNetworkActions(network);
        Assert.True(disconnected.Single(item => item.Action == WorkspaceActionId.Connect).IsEnabled);
        Assert.False(disconnected.Single(item => item.Action == WorkspaceActionId.Disconnect).IsEnabled);
        Assert.False((await router.ExecuteChannelAsync(network, channel, WorkspaceActionId.PartChannel)).Succeeded);
    }

    [Fact]
    public async Task MatureCommandsRouteAwayWhoNamesNoticeActionAndCasemappedQueries()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("commands-u.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Commands U", transport.Endpoint, "alice", "#room"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "alice", "(ov)@+", "be,k,l,imnpst");
        transport.EnqueueInboundLine(":alice!u@host JOIN #room");
        await WaitForAsync(() => network.Channels.Single().IsJoined);

        var dispatcher = new IrcCommandDispatcher(manager);
        var channel = network.Channels.Single();
        var away = await dispatcher.DispatchAsync(network, channel, "/away :gone for lunch");
        var who = await dispatcher.DispatchAsync(network, channel, "/who");
        var names = await dispatcher.DispatchAsync(network, channel, "/names");
        var notice = await dispatcher.DispatchAsync(network, channel, "/notice #room this is quiet");
        var action = await dispatcher.DispatchAsync(network, channel, "/me waves hello");
        var firstQuery = await dispatcher.DispatchAsync(network, channel, "/query Nick[");
        var secondQuery = await dispatcher.DispatchAsync(network, firstQuery.View, "/msg nick{ hello from the same IRC identity");

        Assert.True(away.Succeeded);
        Assert.True(who.Succeeded);
        Assert.True(names.Succeeded);
        Assert.True(notice.Succeeded);
        Assert.True(action.Succeeded);
        Assert.True(secondQuery.Succeeded);
        Assert.Same(firstQuery.View, secondQuery.View);
        await WaitForAsync(() => transport.OutboundLines.Contains("AWAY :gone for lunch")
            && transport.OutboundLines.Contains("WHO #room")
            && transport.OutboundLines.Contains("NAMES #room")
            && transport.OutboundLines.Contains("NOTICE #room :this is quiet")
            && transport.OutboundLines.Contains("PRIVMSG #room :\u0001ACTION waves hello\u0001")
            && transport.OutboundLines.Contains("PRIVMSG nick{ :hello from the same IRC identity"));
    }

    [Fact]
    public async Task SlashAndContextMessagePathsReuseTheSameQueryIdentity()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("query-u.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Query U", transport.Endpoint, "alice"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "alice", "(ov)@+", "be,k,l,imnpst");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
        var router = new WorkspaceActionRouter(manager);
        var opened = router.OpenQuery(network, "Nick[");
        var sent = await router.SendMessageAsync(network, "nick{", "same identity");

        Assert.True(sent.Succeeded);
        Assert.Same(opened, sent.View);
        Assert.Single(network.Queries);
        Assert.Equal("Nick[", network.Queries.Single().Nickname);
        await WaitForAsync(() => transport.OutboundLines.Contains("PRIVMSG nick{ :same identity"));
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, string? channel = null) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1U test",
        DesiredChannels = channel is null ? new HashSet<string>() : new HashSet<string>([channel]),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname, string prefix, string chanModes)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine($":srv 005 {nickname} CASEMAPPING=rfc1459 PREFIX={prefix} CHANMODES={chanModes} :features");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1U condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
