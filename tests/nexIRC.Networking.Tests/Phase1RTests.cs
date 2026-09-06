using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase1RTests
{
    [Fact]
    public async Task ReconnectCyclesDetachTransportCallbacksAndDisposeEachTransportOnce()
    {
        var first = new FakeIrcTransport(new IrcEndpoint("phase1r-first.example", 6667, false));
        var second = new FakeIrcTransport(first.Endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        var options = new ServerSessionOptions
        {
            Endpoint = first.Endpoint,
            Nickname = "phase1r",
            Username = "phase1r",
            RealName = "Phase 1R callback ownership",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))
        };

        await using var session = new ServerSession(options, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "first", "phase1r");
        await WaitForAsync(() => session.Snapshot.State == ServerSessionState.Registered);
        Assert.Equal(1, first.CallbackSubscriptionCount);

        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);
        await WaitForAsync(() => first.CallbackSubscriptionCount == 0);
        Register(second, "second", "phase1r");
        await WaitForAsync(() => session.Snapshot.State == ServerSessionState.Registered);
        Assert.Equal(1, second.CallbackSubscriptionCount);

        await session.DisconnectAsync("Phase 1R callback cleanup");
        await run;
        await session.DisposeAsync();

        Assert.Equal(0, first.CallbackSubscriptionCount);
        Assert.Equal(0, second.CallbackSubscriptionCount);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(0, first.ActiveReadCount);
        Assert.Equal(0, second.ActiveReadCount);
    }

    private static void Register(FakeIrcTransport transport, string server, string nickname)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to Phase 1R");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1R networking condition did not complete.");
            }

            await Task.Delay(5);
        }
    }
}
