using System.Collections.Concurrent;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase50ReadStateOrderingTests
{
    [Fact]
    public async Task LiveUpdateReceivedBeforeSnapshotIsProjectedAfterTheAtomicSnapshot()
    {
        var endpoint = new IrcEndpoint("state.example", 6697, true);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "me",
            RequestedCapabilities = ["nexirc/state"],
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var stateEvents = new ConcurrentQueue<IrcNexIrcReadStateEvent>();
        session.SemanticEventReceived += (_, item) =>
        {
            if (item.Event is IrcNexIrcReadStateEvent readState) stateEvents.Enqueue(readState);
        };

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :nexirc/state=1");
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CAP REQ :nexirc/state", StringComparison.Ordinal)));
        transport.EnqueueInboundLine(":srv CAP * ACK :nexirc/state=1");
        await WaitForAsync(() => transport.OutboundLines.Contains("CAP END") && transport.OutboundLines.Contains("NICK me"));
        transport.EnqueueInboundLine(":srv 001 me :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        transport.EnqueueInboundLine(":srv NEXIRC STATE UPDATE READ Y2hhbm5lbDojcm9vbQ s2 bTI 2 1");
        transport.EnqueueInboundLine(":srv NEXIRC STATE BEGIN 1 1");
        transport.EnqueueInboundLine(":srv NEXIRC STATE ENTRY READ Y2hhbm5lbDojcm9vbQ s1 bTE 1 1");
        transport.EnqueueInboundLine(":srv NEXIRC STATE END 1");

        await WaitForAsync(() => stateEvents.Count == 2);
        var projected = stateEvents.ToArray();
        Assert.NotNull(projected[0].Snapshot);
        Assert.Equal(1, Assert.Single(projected[0].Snapshot!.ReadMarkers).Sequence);
        Assert.Equal(2, projected[1].Marker!.Sequence);

        await session.DisconnectAsync();
        await run;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The Phase 50 read-state condition was not reached.");
            await Task.Delay(10);
        }
    }
}
