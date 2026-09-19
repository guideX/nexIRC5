using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase32RecoveryQualityTests
{
    [Fact]
    public async Task CanonicalMessageIdentityDeduplicatesLiveAndReplacementDelivery()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("phase32-dedup.example", 6667, false));
        var second = new FakeIrcTransport(first.Endpoint);
        factory.Add(first);
        factory.Add(second);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Phase32 dedup",
            Endpoint = first.Endpoint,
            Nickname = "alice",
            Username = "alice",
            RealName = "Phase 32 test",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))
        });

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "srv", "alice");
        first.EnqueueInboundLine("@msgid=phase32-message-1 :bob!u@h PRIVMSG alice :same canonical message");
        await WaitForAsync(() => network.Queries.Count == 1 && network.Queries[0].EntryCount == 1);
        var query = network.Queries.Single();

        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);
        Register(second, "srv", "alice");
        await WaitForAsync(() => network.ContinuityState == ConnectionContinuityState.Synchronized);
        second.EnqueueInboundLine("@msgid=phase32-message-1 :bob!u@h PRIVMSG alice :same canonical message");
        await WaitForAsync(() => manager.Diagnostics.DuplicateSemanticEventsDiscarded > 0);

        Assert.Same(query, Assert.Single(network.Queries));
        Assert.Single(query.EntriesSnapshot, entry => entry.ServerMessageId == "phase32-message-1");
        Assert.Equal(ContinuityRecoveryResultKind.Unsupported, network.RecoveryQuality);
        Assert.False(network.RecoveryResult.CanClaimLosslessContinuity);
    }

    [Fact]
    public async Task UnsupportedHistoryProjectsStructuredQualityWithoutChangingLifecycleReadiness()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("phase32-quality.example", 6667, false));
        var second = new FakeIrcTransport(first.Endpoint);
        factory.Add(first);
        factory.Add(second);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Phase32 quality",
            Endpoint = first.Endpoint,
            Nickname = "alice",
            Username = "alice",
            RealName = "Phase 32 test",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2))
        });

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "srv", "alice");
        await WaitForAsync(() => network.ContinuityState == ConnectionContinuityState.Synchronized);
        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);
        Register(second, "srv", "alice");

        await WaitForAsync(() => network.ContinuityState == ConnectionContinuityState.Synchronized);
        Assert.Equal(ConnectionContinuityState.Synchronized, network.StatusView.ContinuityState);
        Assert.Equal(ContinuityRecoveryResultKind.Unsupported, network.StatusView.RecoveryQuality);
        Assert.Equal(ContinuityEvidenceLevel.BestEffort, network.StatusView.RecoveryEvidence);
        Assert.Equal(ContinuityRecoveryResultKind.Unsupported, network.Snapshot.Continuity.RecoveryResult.Kind);
        Assert.Contains("history", network.Snapshot.Continuity.RecoveryResult.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static void Register(FakeIrcTransport transport, string server, string nickname)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 32 condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
