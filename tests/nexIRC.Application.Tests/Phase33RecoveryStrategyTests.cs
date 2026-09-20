using System.Globalization;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase33RecoveryStrategyTests
{
    [Fact]
    public void CurrentGenerationPolicySelectsOnlyFromNegotiatedEvidence()
    {
        var noHistory = ConnectionRecoveryStrategyPolicy.Select(true, ChathistorySupport.Unavailable);
        var history = ConnectionRecoveryStrategyPolicy.Select(true, new ChathistorySupport
        {
            CapabilityEnabled = true,
            BatchEnabled = true,
            ServerTimeEnabled = true,
            MessageTagsEnabled = true
        });
        var initial = ConnectionRecoveryStrategyPolicy.Select(false, ChathistorySupport.Unavailable);

        Assert.Equal(ConnectionRecoveryStrategyId.BestEffortNoHistory, noHistory.Strategy);
        Assert.Equal(ConnectionRecoveryStrategyId.Ircv3ChatHistory, history.Strategy);
        Assert.Equal(ConnectionRecoveryStrategyId.None, initial.Strategy);
        Assert.DoesNotContain("hostname", noHistory.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("software", noHistory.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StrategiesReturnTypedResultsWithoutChangingLifecycle()
    {
        var endpoint = new IrcEndpoint("phase33-strategy.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Username = "nex",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var boundary = ConnectionRecoveryBoundary.Empty(7, DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture));
        var context = new ConnectionRecoveryStrategyContext(
            session,
            boundary,
            _ => ValueTask.FromResult(new ConnectionRecoveryExecutionResult(
                ContinuitySynchronizationOutcome.Recovered,
                HistoryAvailable: true,
                RecoveryRequestSent: true,
                ReplayCompleted: true,
                ExactGapRecovered: true,
                RecoveryImpossible: false,
                Detail: "fixture replay",
                RecoveredEventCount: 3,
                CommandsIssued: 2)));

        var strategy = new Ircv3ChatHistoryRecoveryStrategy();
        var result = await strategy.RecoverAsync(context, "current generation history", CancellationToken.None);

        Assert.Equal(ConnectionRecoveryStrategyId.Ircv3ChatHistory, result.Strategy);
        Assert.Equal(ContinuityRecoveryResultKind.Recovered, result.Kind);
        Assert.Equal(ContinuityEvidenceLevel.StrongReplay, result.Evidence);
        Assert.Equal(3, result.RecoveredEventCount);
        Assert.Equal(2, result.CommandsIssued);
        Assert.Equal(ConnectionContinuityState.Disconnected, session.Continuity.State);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringStrategyPreventsPublication()
    {
        var endpoint = new IrcEndpoint("phase33-cancel.example", 6667, false);
        var factory = new FakeIrcTransportFactory();
        factory.Add(new FakeIrcTransport(endpoint));
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Username = "nex",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var boundary = ConnectionRecoveryBoundary.Empty(4, DateTimeOffset.UtcNow);
        var invoked = false;
        var context = new ConnectionRecoveryStrategyContext(
            session,
            boundary,
            async token =>
            {
                invoked = true;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return ConnectionRecoveryExecutionResult.Unsupported;
            });
        var strategy = new Ircv3ChatHistoryRecoveryStrategy();
        using var beforeCancellation = new CancellationTokenSource();
        beforeCancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => strategy.RecoverAsync(context, "cancelled", beforeCancellation.Token).AsTask());
        Assert.False(invoked);

        using var duringCancellation = new CancellationTokenSource();
        var pending = strategy.RecoverAsync(context, "cancel during request", duringCancellation.Token).AsTask();
        await WaitForAsync(() => invoked);
        duringCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(ConnectionContinuityState.Disconnected, session.Continuity.State);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic strategy condition was not reached.");
            }

            await Task.Delay(5);
        }
    }
}
