using System.Globalization;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase34NativeResumeStrategyTests
{
    [Fact]
    public async Task SelectorPrefersNativeResumeOnlyWithNegotiatedCapabilityAndSession()
    {
        await using var fixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase34-selector.example", 6667, false),
            Profile = DeterministicReplayProfile.NativeResume
        });
        var factory = new FakeIrcTransportFactory();
        factory.Add(fixture.Transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = fixture.Transport.Endpoint,
            Nickname = "nex",
            RequestedCapabilities = IrcCapabilityCatalog.PreferredPhase1Y,
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        _ = session.RunAsync();
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered && session.NativeResumeSession is not null);

        var boundary = ConnectionRecoveryBoundary.Empty(1, DateTimeOffset.UtcNow);
        var context = new ConnectionRecoveryStrategyContext(
            session,
            boundary,
            _ => ValueTask.FromResult(ConnectionRecoveryExecutionResult.Unsupported));
        var selector = new ConnectionRecoveryStrategySelector();

        var selected = selector.Select(context, recoveryRequired: true);

        Assert.Equal(ConnectionRecoveryStrategyId.NexIrcResume, selected.Selection.Strategy);
        Assert.IsType<NexIrcResumeRecoveryStrategy>(selected.Strategy);
        Assert.Equal(ConnectionRecoveryStrategyId.Ircv3ChatHistory, selector.SelectFallback(context, true).Selection.Strategy);

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task NativeStrategyMapsCompletionToStrongReplayWithoutLifecycleOwnership()
    {
        var endpoint = new IrcEndpoint("phase34-strategy.example", 6667, false);
        var factory = new FakeIrcTransportFactory();
        factory.Add(new FakeIrcTransport(endpoint));
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var boundary = ConnectionRecoveryBoundary.Empty(4, DateTimeOffset.Parse("2026-09-26T12:00:00Z", CultureInfo.InvariantCulture));
        var native = new NexIrcResumeExecutionResult(
            NexIrcResumeOutcome.Completed,
            CapabilityNegotiated: true,
            RequestSent: true,
            ReplayAccepted: true,
            ReplayCompleted: true,
            ExactBoundaryRecovered: true,
            ReplayedEventCount: 4,
            DuplicateEventsSuppressed: 1,
            RequestedBoundary: "resume-4",
            FinalBoundary: "resume-8",
            Detail: "fixture native replay",
            RejectionReason: null,
            FallbackSafe: false);
        var context = new ConnectionRecoveryStrategyContext(
            session,
            boundary,
            _ => ValueTask.FromResult(ConnectionRecoveryExecutionResult.Unsupported),
            _ => ValueTask.FromResult(new ConnectionRecoveryExecutionResult(
                ContinuitySynchronizationOutcome.Recovered,
                HistoryAvailable: true,
                RecoveryRequestSent: true,
                ReplayCompleted: true,
                ExactGapRecovered: true,
                RecoveryImpossible: false,
                Detail: "fixture native replay",
                RecoveredEventCount: 4,
                CommandsIssued: 1)
            {
                NativeResume = native
            }));

        var result = await new NexIrcResumeRecoveryStrategy().RecoverAsync(
            context,
            "native capability and session",
            CancellationToken.None);

        Assert.Equal(ConnectionRecoveryStrategyId.NexIrcResume, result.Strategy);
        Assert.Equal(ContinuityRecoveryResultKind.Recovered, result.Kind);
        Assert.Equal(ContinuityEvidenceLevel.StrongReplay, result.Evidence);
        Assert.True(result.CanClaimLosslessContinuity);
        Assert.Equal(ConnectionContinuityState.Disconnected, session.Continuity.State);
    }

    [Fact]
    public async Task NativeRejectionRequestsOneBoundedFallbackAndDoesNotClaimNativeSuccess()
    {
        var endpoint = new IrcEndpoint("phase34-reject.example", 6667, false);
        var factory = new FakeIrcTransportFactory();
        factory.Add(new FakeIrcTransport(endpoint));
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var native = new NexIrcResumeExecutionResult(
            NexIrcResumeOutcome.Rejected,
            CapabilityNegotiated: true,
            RequestSent: true,
            ReplayAccepted: false,
            ReplayCompleted: false,
            ExactBoundaryRecovered: false,
            ReplayedEventCount: 0,
            DuplicateEventsSuppressed: 0,
            RequestedBoundary: "resume-1",
            FinalBoundary: "resume-1",
            Detail: "unknown token",
            RejectionReason: NexIrcResumeRejectionReason.UnknownToken,
            FallbackSafe: true);
        var context = new ConnectionRecoveryStrategyContext(
            session,
            ConnectionRecoveryBoundary.Empty(2, DateTimeOffset.UtcNow),
            _ => ValueTask.FromResult(new ConnectionRecoveryExecutionResult(
                ContinuitySynchronizationOutcome.Recovered,
                HistoryAvailable: true,
                RecoveryRequestSent: true,
                ReplayCompleted: true,
                ExactGapRecovered: true,
                RecoveryImpossible: false,
                Detail: "fallback")),
            _ => ValueTask.FromResult(new ConnectionRecoveryExecutionResult(
                ContinuitySynchronizationOutcome.Unsupported,
                HistoryAvailable: true,
                RecoveryRequestSent: true,
                ReplayCompleted: false,
                ExactGapRecovered: false,
                RecoveryImpossible: true,
                Detail: "unknown token")
            {
                NativeResume = native
            }));

        var result = await new NexIrcResumeRecoveryStrategy().RecoverAsync(
            context,
            "native selected",
            CancellationToken.None);

        Assert.Equal(ConnectionRecoveryStrategyId.NexIrcResume, result.Strategy);
        Assert.Equal(ContinuityRecoveryResultKind.Unsupported, result.Kind);
        Assert.True(result.FallbackRecommended);
        Assert.Equal(NexIrcResumeRejectionReason.UnknownToken, result.NativeResumeRejectionReason);
        Assert.False(result.CanClaimLosslessContinuity);
    }

    [Fact]
    public void ContinuityLifecycleEnumIsUnchangedByNativeStrategy()
    {
        Assert.Equal(
            ["Disconnected", "Interrupted", "Recovering", "Synchronizing", "Synchronized", "Terminal"],
            Enum.GetNames<ConnectionContinuityState>());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 34 strategy condition was not reached.");
            }

            await Task.Delay(5);
        }
    }
}
