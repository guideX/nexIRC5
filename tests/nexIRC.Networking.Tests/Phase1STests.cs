using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase1STests
{
    [Fact]
    public async Task DisposeAfterRegistrationFlushesQuitBeforeCancellingTheWriter()
    {
        var transport = new FakeIrcTransport(new IrcEndpoint("phase1s.example", 6667, false));
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        var options = new ServerSessionOptions
        {
            Endpoint = transport.Endpoint,
            Nickname = "phase1s",
            Username = "phase1s",
            RealName = "Phase 1S shutdown",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        };

        await using var session = new ServerSession(options, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":phase1s.example CAP * LS :");
        transport.EnqueueInboundLine(":phase1s.example 001 phase1s :Welcome to Phase 1S");
        await WaitForAsync(() => session.Snapshot.State == ServerSessionState.Registered);

        await session.DisposeAsync();
        await run;

        Assert.Equal(1, transport.OutboundLines.Count(line => line.StartsWith("QUIT", StringComparison.Ordinal)));
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(0, transport.CallbackSubscriptionCount);
        Assert.Equal(0, transport.ActiveReadCount);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1S networking condition did not complete.");
            }

            await Task.Delay(5);
        }
    }
}
