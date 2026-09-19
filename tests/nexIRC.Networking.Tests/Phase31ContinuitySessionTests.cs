using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase31ContinuitySessionTests
{
    [Fact]
    public async Task InitialRegistrationAndReconnectExposeOrderedContinuityBarriers()
    {
        var endpoint = new IrcEndpoint("phase31.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            RequestedCapabilities = ["message-tags"],
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10))
        }, factory);

        var transitions = new List<ConnectionContinuityStateChangedEvent>();
        session.ContinuityStateChanged += (_, change) => transitions.Add(change);
        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        first.EnqueueInboundLine(":srv CAP * LS :message-tags");
        first.EnqueueInboundLine(":srv CAP * ACK :message-tags");
        first.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Continuity.State == ConnectionContinuityState.Synchronized);

        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);
        second.EnqueueInboundLine(":srv CAP * LS :message-tags");
        second.EnqueueInboundLine(":srv CAP * ACK :message-tags");
        second.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Continuity.State == ConnectionContinuityState.Synchronizing);

        Assert.Equal(
            [
                ConnectionContinuityState.Recovering,
                ConnectionContinuityState.Synchronized,
                ConnectionContinuityState.Interrupted,
                ConnectionContinuityState.Recovering,
                ConnectionContinuityState.Synchronizing
            ],
            transitions.Select(change => change.Current).ToArray());
        Assert.Equal(2, session.Continuity.ConnectionGeneration);
        Assert.False(session.Continuity.SynchronizationComplete);

        Assert.True(session.CompleteSynchronization(2, ContinuitySynchronizationOutcome.Unsupported, "history unavailable"));
        await WaitForAsync(() => session.Continuity.State == ConnectionContinuityState.Synchronized);
        Assert.Equal(ContinuitySynchronizationOutcome.Unsupported, session.Continuity.SynchronizationOutcome);
        Assert.False(session.CompleteSynchronization(1, ContinuitySynchronizationOutcome.Recovered));

        await session.DisconnectAsync("test complete");
        await run;
    }

    [Fact]
    public async Task IntentionalDisconnectDoesNotScheduleReplacementTransport()
    {
        var endpoint = new IrcEndpoint("phase31-intentional.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5))
        }, factory);

        var transitions = new List<ConnectionContinuityStateChangedEvent>();
        session.ContinuityStateChanged += (_, change) => transitions.Add(change);
        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        first.EnqueueInboundLine(":srv CAP * LS :");
        first.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Continuity.State == ConnectionContinuityState.Synchronized);

        await session.DisconnectAsync("user requested");
        await run;

        Assert.Equal(1, factory.CreatedTransportCount);
        Assert.DoesNotContain(transitions, change => change.Current == ConnectionContinuityState.Interrupted);
        Assert.Equal(ConnectionContinuityState.Disconnected, session.Continuity.State);
        Assert.True(session.Continuity.IsIntentional);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The deterministic Phase 31 condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
