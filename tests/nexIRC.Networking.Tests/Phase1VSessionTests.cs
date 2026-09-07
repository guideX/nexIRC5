using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase1VSessionTests
{
    [Fact]
    public async Task MetadataLifecycleSupportsExtendedJoinAwayAccountAndMultiPrefix()
    {
        var endpoint = new IrcEndpoint("metadata.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Username = "nex",
            RequestedCapabilities = ["server-time", "away-notify", "extended-join", "multi-prefix", "account-notify"],
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var semantic = new List<SessionSemanticEvent>();
        session.SemanticEventReceived += (_, item) => semantic.Add(item);

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :server-time away-notify extended-join multi-prefix account-notify");
        transport.EnqueueInboundLine(":srv CAP * ACK :server-time away-notify extended-join multi-prefix account-notify");
        transport.EnqueueInboundLine(":srv 005 nex PREFIX=(qaohv)~&@%+ CASEMAPPING=rfc1459 CHANTYPES=# :supported");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        transport.EnqueueInboundLine(":nex!u@h JOIN #room account :Real Name With Spaces");
        transport.EnqueueInboundLine(":srv 353 nex = #room :@+nex @+Peer Ordinary");
        transport.EnqueueInboundLine(":srv 366 nex #room :End of NAMES");
        await WaitForAsync(() => session.Snapshot.Channels.SingleOrDefault()?.Synchronization == ChannelSynchronizationState.Synchronized);

        var snapshot = session.Snapshot;
        var channel = snapshot.Channels.Single();
        var local = channel.Members["nex"];
        Assert.Equal("account", local.Account);
        Assert.Equal("Real Name With Spaces", local.RealName);
        Assert.Contains('o', local.PrefixModes);
        Assert.Contains('v', local.PrefixModes);
        Assert.Contains("account-notify", snapshot.Capabilities.Enabled);
        Assert.Equal("CAP", semantic.First(item => item.Event is IrcCapabilityChangedEvent).Event.Message.Command);

        transport.EnqueueInboundLine(":Peer!p@h AWAY :lunch\\ssoon");
        transport.EnqueueInboundLine(":Peer!p@h ACCOUNT peer-account");
        await WaitForAsync(() =>
        {
            var member = session.Snapshot.Channels.Single().Members["Peer"];
            return member.IsAway && member.Account == "peer-account";
        });

        var peer = session.Snapshot.Channels.Single().Members["Peer"];
        Assert.Equal("lunch\\ssoon", peer.AwayReason);
        Assert.Contains(semantic, item => item.Event is IrcAwayEvent { IsAway: true });
        Assert.Contains(semantic, item => item.Event is IrcAccountEvent { Account: "peer-account" });

        transport.EnqueueInboundLine(":Peer!p@h NICK Peer2");
        transport.EnqueueInboundLine(":Peer2!p@h AWAY");
        transport.EnqueueInboundLine(":Peer2!p@h ACCOUNT *");
        await WaitForAsync(() =>
        {
            var members = session.Snapshot.Channels.Single().Members;
            return members.ContainsKey("Peer2") && !members["Peer2"].IsAway && members["Peer2"].Account is null;
        });

        Assert.DoesNotContain("Peer", session.Snapshot.Channels.Single().Members.Keys);
        Assert.Equal("Peer2", session.Snapshot.Channels.Single().Members["Peer2"].Nickname);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task NamesFinalizationDoesNotReAddAReplyForACompletedOlderCycle()
    {
        var endpoint = new IrcEndpoint("names-race.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, transport, "nex");
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex", "multi-prefix");
        transport.EnqueueInboundLine(":nex!u@h JOIN #room");
        transport.EnqueueInboundLine(":srv 353 nex = #room :@nex Peer");
        transport.EnqueueInboundLine(":srv 366 nex #room :End");
        await WaitForAsync(() => session.Snapshot.Channels.SingleOrDefault()?.Synchronization == ChannelSynchronizationState.Synchronized);

        transport.EnqueueInboundLine(":Peer!u@h PART #room :gone");
        await WaitForAsync(() => !session.Snapshot.Channels.Single().Members.ContainsKey("Peer"));
        transport.EnqueueInboundLine(":srv 353 nex = #room :@nex Peer");
        transport.EnqueueInboundLine(":srv 366 nex #room :late old reply");
        await Task.Delay(50);

        Assert.DoesNotContain("Peer", session.Snapshot.Channels.Single().Members.Keys);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task NamesRefreshDoesNotEraseAJoinAfterTheRequestStarts()
    {
        var endpoint = new IrcEndpoint("names-join-race.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, transport, "nex");
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        await session.RequestNamesAsync("#room");
        transport.EnqueueInboundLine(":Peer!u@h JOIN #room");
        transport.EnqueueInboundLine(":srv 353 nex = #room :@nex");
        transport.EnqueueInboundLine(":srv 366 nex #room :End");
        await WaitForAsync(() => session.Snapshot.Channels.SingleOrDefault()?.Synchronization == ChannelSynchronizationState.Synchronized);

        Assert.Contains("Peer", session.Snapshot.Channels.Single().Members.Keys);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task PartAndQuitRemovePresenceMetadata()
    {
        var endpoint = new IrcEndpoint("presence.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, transport, "nex");
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        transport.EnqueueInboundLine(":nex!u@h JOIN #room");
        transport.EnqueueInboundLine(":someone!u@h JOIN #room");
        await WaitForAsync(() => CurrentChannel(session)?.Members.ContainsKey("someone") == true);
        transport.EnqueueInboundLine(":someone!u@h AWAY :busy");
        await WaitForAsync(() => CurrentChannel(session)?.Members.TryGetValue("someone", out var member) == true && member.IsAway);
        transport.EnqueueInboundLine(":someone!u@h PART #room :left");
        await WaitForAsync(() => CurrentChannel(session)?.Members.ContainsKey("someone") != true);
        transport.EnqueueInboundLine(":other!u@h JOIN #room");
        await WaitForAsync(() => CurrentChannel(session)?.Members.ContainsKey("other") == true);
        transport.EnqueueInboundLine(":other!u@h QUIT :gone");
        await WaitForAsync(() => CurrentChannel(session)?.Members.ContainsKey("other") != true);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task CaplessServerCanRejectCapAndContinueRegistration()
    {
        var endpoint = new IrcEndpoint("legacy.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            CapabilityNegotiationTimeout = TimeSpan.FromSeconds(1),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv 421 nex CAP :Unknown command");
        await WaitForAsync(() => transport.OutboundLines.Contains("NICK nex"));
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        Assert.Equal(1, transport.OutboundLines.Count(line => line == "CAP END"));
        Assert.Empty(session.Snapshot.Capabilities.Enabled);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task CapabilityTimeoutFallsBackWithoutASecondCapEnd()
    {
        var endpoint = new IrcEndpoint("silent-cap.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            CapabilityNegotiationTimeout = TimeSpan.FromMilliseconds(25),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.OutboundLines.Contains("NICK nex"));

        Assert.Equal(1, transport.OutboundLines.Count(line => line == "CAP END"));
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task StaleGenerationCapabilityTrafficCannotMutateTheCurrentSession()
    {
        var endpoint = new IrcEndpoint("cap-generation.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            RequestedCapabilities = ["old-cap", "new-cap"],
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5))
        }, factory);

        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "nex", "old-cap");
        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);

        await first.EmitCallbackAsync(new IrcTransportInboundLineCallback(":old.server CAP * ACK :old-cap"));
        second.EnqueueInboundLine(":srv CAP * LS :new-cap");
        second.EnqueueInboundLine(":srv CAP * ACK :new-cap");
        second.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.ConnectionGeneration == 2 && session.Snapshot.Registration == RegistrationState.Registered);

        Assert.True(session.Snapshot.Capabilities.IsEnabled("new-cap"));
        Assert.False(session.Snapshot.Capabilities.IsEnabled("old-cap"));
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task BatchFramingIsTypedAndScopedToTheLiveSession()
    {
        var endpoint = new IrcEndpoint("batch.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, transport, "nex");
        var events = new List<IrcBatchEvent>();
        session.SemanticEventReceived += (_, item) =>
        {
            if (item.Event is IrcBatchEvent batch) events.Add(batch);
        };
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        transport.EnqueueInboundLine(":srv BATCH +b chathistory #room");
        transport.EnqueueInboundLine(":srv BATCH -b");
        await WaitForAsync(() => events.Count == 2);

        Assert.Equal("b", events[0].BatchId);
        Assert.True(events[0].IsStart);
        Assert.Equal("chathistory", events[0].Type);
        Assert.False(events[1].IsStart);
        await session.DisconnectAsync();
        await run;
    }

    private static ServerSession CreateSession(IrcEndpoint endpoint, FakeIrcTransportFactory factory, FakeIrcTransport transport, string nickname, IReadOnlyList<string>? capabilities = null) =>
        new(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = nickname,
            Username = nickname,
            RequestedCapabilities = capabilities ?? Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);

    private static void Register(FakeIrcTransport transport, string nickname, params string[] capabilities)
    {
        transport.EnqueueInboundLine($":srv CAP * LS :{string.Join(' ', capabilities)}");
        if (capabilities.Length > 0)
        {
            transport.EnqueueInboundLine($":srv CAP * ACK :{string.Join(' ', capabilities)}");
        }

        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 1V session condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static IrcChannelSnapshot? CurrentChannel(ServerSession session)
    {
        var channels = session.Snapshot.Channels;
        return channels.Count == 0 ? null : channels[0];
    }
}
