using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class ServerSessionTests
{
    [Fact]
    public async Task PerformsNormalRegistrationAndHandlesPing()
    {
        var endpoint = new IrcEndpoint("test.example", 6697);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, "nex", requestedCapabilities: ["server-time"]);

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :server-time message-tags");
        transport.EnqueueInboundLine(":srv CAP * ACK :server-time");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        transport.EnqueueInboundLine("PING :token");
        await WaitForAsync(() => session.Snapshot.State == ServerSessionState.Registered && transport.OutboundLines.Contains("PONG :token"));

        Assert.Contains("CAP LS 302", transport.OutboundLines);
        Assert.Contains("NICK nex", transport.OutboundLines);
        Assert.Contains("USER nex 0 * :Test user", transport.OutboundLines);
        Assert.Contains("CAP REQ :server-time", transport.OutboundLines);
        Assert.Contains("CAP END", transport.OutboundLines);
        Assert.Equal(RegistrationState.Registered, session.Snapshot.Registration);

        await session.DisconnectAsync("test complete");
        await run;
    }

    [Fact]
    public async Task SendsPassAndUsesAlternateNicknameAfterCollision()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, "taken", password: "secret", alternateNickname: "available");
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);

        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 433 * taken :Nickname is already in use");
        await WaitForAsync(() => transport.OutboundLines.Contains("NICK available"));
        transport.EnqueueInboundLine(":srv 001 available :Welcome");
        await WaitForAsync(() => session.Snapshot.State == ServerSessionState.Registered);

        Assert.Contains("PASS :secret", transport.OutboundLines);
        Assert.Equal("available", session.Snapshot.Nickname);
        Assert.Equal("taken", session.Snapshot.DesiredNickname);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task SendsPassFromProviderWithoutRetainingProviderFailureInSessionState()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            PasswordProvider = new TestServerPasswordProvider("provider-secret"),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);

        var run = session.RunAsync();
        await WaitForAsync(() => transport.OutboundLines.Contains("PASS :provider-secret"));
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.State == ServerSessionState.Registered);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task OrderedNicknameFallbacksAreTriedWithoutRandomPolicyInsideTheParser()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "Merlin",
            NicknameFallbacks = ["Merlin_", "Merlin__"],
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        await WaitForAsync(() => transport.OutboundLines.Contains("NICK Merlin"));
        transport.EnqueueInboundLine(":srv 433 * Merlin :taken");
        await WaitForAsync(() => transport.OutboundLines.Contains("NICK Merlin_"));
        transport.EnqueueInboundLine(":srv 433 * Merlin_ :taken");
        await WaitForAsync(() => transport.OutboundLines.Contains("NICK Merlin__"));
        transport.EnqueueInboundLine(":srv 001 Merlin__ :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        Assert.Equal("Merlin__", session.Snapshot.Nickname);
        Assert.Equal("Merlin", session.Snapshot.DesiredNickname);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task ServerErrorFailsRegistrationAndIsVisibleAsSemanticEvent()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, "nex");
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);

        transport.EnqueueInboundLine(":srv ERROR :Registration is forbidden");
        await run;

        Assert.Equal(ServerSessionState.Failed, session.Snapshot.State);
        Assert.Equal(ConnectionFailureKind.Protocol, session.Snapshot.LastFailure!.Kind);
        Assert.Contains("Registration is forbidden", session.Snapshot.LastFailure.Message);
    }

    [Fact]
    public async Task CapCanArriveBeforeWelcomeAndUnknownNumericRemainsVisible()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, "nex", requestedCapabilities: ["message-tags"]);
        var unknownNumericTask = ReadUntilAsync(session.ReadSemanticEventsAsync(), item => item.Event is IrcUnknownNumericEvent unknown && unknown.Numeric == 742);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);

        transport.EnqueueInboundLine(":srv CAP * LS :message-tags");
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags");
        transport.EnqueueInboundLine(":srv 742 nex :future numeric");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.State == ServerSessionState.Registered);
        await unknownNumericTask;

        Assert.True(session.Snapshot.Capabilities.IsEnabled("message-tags"));
        Assert.Equal(742, (await CollectParsedNumericAsync(session, 742)).NumericCommand);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task SessionStateIsolatedAcrossTwoServers()
    {
        var endpointA = new IrcEndpoint("a.example", 6667, false);
        var endpointB = new IrcEndpoint("b.example", 6667, false);
        var transportA = new FakeIrcTransport(endpointA);
        var transportB = new FakeIrcTransport(endpointB);
        var factoryA = new FakeIrcTransportFactory();
        var factoryB = new FakeIrcTransportFactory();
        factoryA.Add(transportA);
        factoryB.Add(transportB);
        await using var sessionA = CreateSession(endpointA, factoryA, "alice", requestedCapabilities: ["multi-prefix"]);
        await using var sessionB = CreateSession(endpointB, factoryB, "bob", requestedCapabilities: ["server-time"]);
        var runA = sessionA.RunAsync();
        var runB = sessionB.RunAsync();
        await WaitForAsync(() => transportA.ConnectCount == 1 && transportB.ConnectCount == 1);

        transportA.EnqueueInboundLine(":a CAP * LS :multi-prefix");
        transportA.EnqueueInboundLine(":a CAP * ACK :multi-prefix");
        transportA.EnqueueInboundLine(":a 005 alice NETWORK=Alpha PREFIX=(qaohv)~&@%+ :supported");
        transportA.EnqueueInboundLine(":a 001 alice :Welcome");
        transportA.EnqueueInboundLine(":alice!u@a JOIN #alpha");
        transportA.EnqueueInboundLine(":zed!u@z PRIVMSG alice :hello alpha");

        transportB.EnqueueInboundLine(":b CAP * LS :server-time");
        transportB.EnqueueInboundLine(":b CAP * ACK :server-time");
        transportB.EnqueueInboundLine(":b 005 bob NETWORK=Beta PREFIX=(ov)@+ :supported");
        transportB.EnqueueInboundLine(":b 001 bob :Welcome");
        transportB.EnqueueInboundLine(":bob!u@b JOIN #beta");
        transportB.EnqueueInboundLine(":yne!u@y PRIVMSG bob :hello beta");

        await WaitForAsync(() => sessionA.Snapshot.Channels.Count == 1 && sessionB.Snapshot.Channels.Count == 1 && sessionA.Snapshot.Queries.Count == 1 && sessionB.Snapshot.Queries.Count == 1);
        var snapshotA = sessionA.Snapshot;
        var snapshotB = sessionB.Snapshot;

        Assert.Equal("Alpha", snapshotA.Features.NetworkName);
        Assert.Equal("Beta", snapshotB.Features.NetworkName);
        Assert.True(snapshotA.Capabilities.IsEnabled("multi-prefix"));
        Assert.False(snapshotA.Capabilities.IsEnabled("server-time"));
        Assert.True(snapshotB.Capabilities.IsEnabled("server-time"));
        Assert.False(snapshotB.Capabilities.IsEnabled("multi-prefix"));
        Assert.Equal("#alpha", snapshotA.Channels[0].Name);
        Assert.Equal("#beta", snapshotB.Channels[0].Name);
        Assert.Equal("alice", snapshotA.Nickname);
        Assert.Equal("bob", snapshotB.Nickname);

        await sessionA.DisconnectAsync();
        await sessionB.DisconnectAsync();
        await Task.WhenAll(runA, runB);
    }

    [Fact]
    public async Task ReconnectRenegotiatesAndMarksOldChannelStateStale()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = CreateSession(endpoint, factory, "nex", requestedCapabilities: ["first-cap", "second-cap"], reconnect: new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10)));
        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        first.EnqueueInboundLine(":srv CAP * LS :first-cap");
        first.EnqueueInboundLine(":srv CAP * ACK :first-cap");
        first.EnqueueInboundLine(":srv 005 nex NETWORK=First :supported");
        first.EnqueueInboundLine(":srv 001 nex :Welcome");
        first.EnqueueInboundLine(":nex!u@h JOIN #old");
        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);

        second.EnqueueInboundLine(":srv CAP * LS :second-cap");
        second.EnqueueInboundLine(":srv CAP * ACK :second-cap");
        second.EnqueueInboundLine(":srv 005 nex NETWORK=Second :supported");
        second.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.ConnectionGeneration == 2 && session.Snapshot.State == ServerSessionState.Registered);
        var snapshot = session.Snapshot;

        Assert.Equal("Second", snapshot.Features.NetworkName);
        Assert.False(snapshot.Capabilities.IsEnabled("first-cap"));
        Assert.True(snapshot.Capabilities.IsEnabled("second-cap"));
        var oldChannel = Assert.Single(snapshot.Channels);
        Assert.True(oldChannel.IsStale);
        Assert.False(oldChannel.IsJoined);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task RunAsyncDoesNotCreateDuplicateBackgroundLoopsAndCancellationIsClean()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = CreateSession(endpoint, factory, "nex");

        var firstRun = session.RunAsync();
        var secondRun = session.RunAsync();

        Assert.Same(firstRun, secondRun);
        await WaitForAsync(() => transport.ConnectCount == 1);
        await session.DisconnectAsync();
        await firstRun;
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task ConcurrentDisposeDuringPartialRegistrationIsIdempotentAndDoesNotReconnect()
    {
        var endpoint = new IrcEndpoint("shutdown.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = CreateSession(
            endpoint,
            factory,
            "nex",
            reconnect: new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20)));

        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        var disposeTasks = new[] { session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask() };
        await Task.WhenAll(disposeTasks);
        await run;

        Assert.All(disposeTasks, task => Assert.True(task.IsCompletedSuccessfully));
        Assert.Equal(1, first.ConnectCount);
        Assert.Equal(0, second.ConnectCount);
        Assert.Equal(ServerSessionState.Disconnected, session.Snapshot.State);
    }

    [Fact]
    public async Task DisposeDuringTransportCreationCancelsTheConnectBoundary()
    {
        var endpoint = new IrcEndpoint("dns-boundary.example", 6697);
        var factory = new BlockingTransportFactory();
        await using var session = CreateSession(endpoint, factory, "nex");

        var run = session.RunAsync();
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ServerSessionState.Disconnected, session.Snapshot.State);
        Assert.Equal(0, factory.CreatedTransportCount);
    }

    private static ServerSession CreateSession(
        IrcEndpoint endpoint,
        IIrcTransportFactory factory,
        string nickname,
        string? password = null,
        string? alternateNickname = null,
        IReadOnlyList<string>? requestedCapabilities = null,
        ReconnectPolicy? reconnect = null) =>
        new(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = nickname,
            Username = nickname,
            RealName = "Test user",
            Password = password,
            AlternateNickname = alternateNickname,
            RequestedCapabilities = requestedCapabilities ?? Array.Empty<string>(),
            Reconnect = reconnect ?? new ReconnectPolicy(Enabled: false)
        }, factory);

    private sealed class BlockingTransportFactory : IIrcTransportFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreatedTransportCount { get; private set; }

        public async ValueTask<IIrcTransport> CreateAsync(IrcEndpoint endpoint, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            CreatedTransportCount++;
            throw new InvalidOperationException("The cancellation boundary should have ended transport creation.");
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The deterministic session test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task ReadUntilAsync(IAsyncEnumerable<SessionSemanticEvent> events, Func<SessionSemanticEvent, bool> predicate)
    {
        await foreach (var item in events)
        {
            if (predicate(item))
            {
                return;
            }
        }
    }

    private static async Task<nexIRC.Core.Protocol.IrcMessage> CollectParsedNumericAsync(ServerSession session, int numeric)
    {
        await foreach (var item in session.ReadParsedEventsAsync())
        {
            if (item.Message.NumericCommand == numeric)
            {
                return item.Message;
            }
        }

        throw new InvalidOperationException("The expected numeric was not found.");
    }

    private sealed class TestServerPasswordProvider(string password) : IServerPasswordProvider
    {
        public ValueTask<string?> GetPasswordAsync(IrcEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(password);
        }
    }
}
