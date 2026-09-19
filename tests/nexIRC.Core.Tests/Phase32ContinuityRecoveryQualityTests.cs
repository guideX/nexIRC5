using System.Globalization;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;

namespace nexIRC.Core.Tests;

public sealed class Phase32ContinuityRecoveryQualityTests
{
    [Fact]
    public void InitialAndReplacementGenerationsOwnIndependentRecoveryResults()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);

        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddSeconds(1));
        Assert.Equal(ContinuityRecoveryResultKind.NotRequired, machine.Snapshot.RecoveryResult.Kind);
        Assert.Equal(ContinuityEvidenceLevel.NoRecoveryNecessary, machine.Snapshot.RecoveryResult.Evidence);

        machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "EOF"), now.AddSeconds(2));
        machine.BeginRecovery(2, false, null, now.AddSeconds(3));
        machine.MarkRegistrationComplete(2, true, now.AddSeconds(4));
        Assert.Equal(ContinuityRecoveryResultKind.InProgress, machine.Snapshot.RecoveryResult.Kind);
        Assert.Equal(2, machine.Snapshot.RecoveryResult.ConnectionGeneration);

        var exact = ConnectionContinuityRecoveryResult.FromOutcome(
            2,
            true,
            ContinuitySynchronizationOutcome.Recovered,
            historyAvailable: true,
            recoveryRequestSent: true,
            replayCompleted: true,
            exactGapRecovered: true);
        Assert.True(machine.CompleteSynchronization(2, exact, now.AddSeconds(5)) is not null);
        Assert.True(machine.Snapshot.RecoveryResult.CanClaimLosslessContinuity);

        machine.MarkInterrupted(2, new ConnectionFailure(ConnectionFailureKind.Network, "reset"), now.AddSeconds(6));
        machine.BeginRecovery(3, false, null, now.AddSeconds(7));
        machine.MarkRegistrationComplete(3, true, now.AddSeconds(8));

        Assert.Equal(3, machine.Snapshot.RecoveryResult.ConnectionGeneration);
        Assert.Equal(ContinuityRecoveryResultKind.InProgress, machine.Snapshot.RecoveryResult.Kind);
        Assert.Null(machine.CompleteSynchronization(2, exact, now.AddSeconds(9)));
        Assert.Equal(ContinuityRecoveryResultKind.InProgress, machine.Snapshot.RecoveryResult.Kind);
    }

    [Fact]
    public void RepeatedAdversarialFullCyclesRemainGenerationSafe()
    {
        const int iterations = 512;
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddSeconds(1));

        for (var cycle = 1; cycle <= iterations; cycle++)
        {
            var oldGeneration = cycle;
            var generation = cycle + 1;
            var cycleTime = now.AddSeconds(cycle * 10);
            machine.MarkInterrupted(
                oldGeneration,
                new ConnectionFailure(ConnectionFailureKind.RemoteClosed, $"cycle-{cycle}"),
                cycleTime);
            Assert.NotNull(machine.BeginRecovery(generation, false, null, cycleTime.AddMilliseconds(1)));
            Assert.NotNull(machine.MarkRegistrationComplete(generation, true, cycleTime.AddMilliseconds(2)));

            // A late completion from the previous generation is intentionally
            // attempted before the current generation publishes its result.
            Assert.Null(machine.CompleteSynchronization(
                oldGeneration,
                ContinuitySynchronizationOutcome.Recovered,
                cycleTime.AddMilliseconds(3)));

            var result = ConnectionContinuityRecoveryResult.FromOutcome(
                generation,
                true,
                cycle % 3 == 0 ? ContinuitySynchronizationOutcome.Partial : ContinuitySynchronizationOutcome.Recovered,
                historyAvailable: true,
                recoveryRequestSent: true,
                replayCompleted: true,
                exactGapRecovered: cycle % 3 != 0,
                recoveryImpossible: cycle % 3 == 0);
            Assert.NotNull(machine.CompleteSynchronization(generation, result, cycleTime.AddMilliseconds(4)));
            Assert.Equal(generation, machine.Snapshot.ConnectionGeneration);
            Assert.Equal(ConnectionContinuityState.Synchronized, machine.Snapshot.State);
            Assert.Equal(result.Kind, machine.Snapshot.RecoveryResult.Kind);
            Assert.Equal(generation, machine.Snapshot.RecoveryResult.ConnectionGeneration);
        }

        Assert.Equal(iterations + 1, machine.Snapshot.ConnectionGeneration);
        Assert.Equal(iterations, machine.Diagnostics.Counters.RecoveryAttempts);
        Assert.Equal(iterations, machine.Diagnostics.Counters.CompletedRecoveries);
        Assert.True(machine.Diagnostics.Counters.StaleCallbacksRejected >= iterations);
        Assert.True(machine.Diagnostics.Entries.Count <= 256);
    }

    [Fact]
    public void RecoveryInterruptionAtEachBoundaryInvalidatesOldResult()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddMilliseconds(1));

        // Transport reconnect failure, pre-registration failure, and a
        // synchronization/history interruption all use the same generation
        // fence and converge on the newest successful generation.
        machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.Network, "transport"), now.AddMilliseconds(2));
        machine.BeginRecovery(2, false, null, now.AddMilliseconds(3));
        machine.MarkInterrupted(2, new ConnectionFailure(ConnectionFailureKind.RegistrationRejected, "registration"), now.AddMilliseconds(4));
        machine.BeginRecovery(3, false, null, now.AddMilliseconds(5));
        machine.MarkRegistrationComplete(3, true, now.AddMilliseconds(6));
        machine.MarkInterrupted(3, new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "history"), now.AddMilliseconds(7));
        machine.BeginRecovery(4, false, null, now.AddMilliseconds(8));
        machine.MarkRegistrationComplete(4, true, now.AddMilliseconds(9));

        Assert.Null(machine.CompleteSynchronization(
            3,
            ContinuitySynchronizationOutcome.Failed,
            now.AddMilliseconds(10)));
        Assert.Null(machine.CompleteSynchronization(
            2,
            ContinuitySynchronizationOutcome.Recovered,
            now.AddMilliseconds(11)));
        Assert.Equal(ConnectionContinuityState.Synchronizing, machine.Snapshot.State);
        Assert.Equal(4, machine.Snapshot.RecoveryResult.ConnectionGeneration);

        machine.CompleteSynchronization(
            4,
            ConnectionContinuityRecoveryResult.FromOutcome(
                4,
                true,
                ContinuitySynchronizationOutcome.Unsupported,
                recoveryImpossible: true),
            now.AddMilliseconds(12));
        Assert.Equal(ConnectionContinuityState.Synchronized, machine.Snapshot.State);
        Assert.Equal(ContinuityRecoveryResultKind.Unsupported, machine.Snapshot.RecoveryResult.Kind);
    }

    [Fact]
    public void UnsupportedHistoryIsSynchronizedButCannotClaimLosslessContinuity()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddMilliseconds(1));
        machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "EOF"), now.AddMilliseconds(2));
        machine.BeginRecovery(2, false, null, now.AddMilliseconds(3));
        machine.MarkRegistrationComplete(2, true, now.AddMilliseconds(4));

        var result = ConnectionContinuityRecoveryResult.FromOutcome(
            2,
            true,
            ContinuitySynchronizationOutcome.Unsupported,
            historyAvailable: false,
            recoveryImpossible: true,
            detail: "CHATHISTORY is not available.");
        machine.CompleteSynchronization(2, result, now.AddMilliseconds(5));

        Assert.Equal(ConnectionContinuityState.Synchronized, machine.Snapshot.State);
        Assert.Equal(ContinuityRecoveryResultKind.Unsupported, machine.Snapshot.RecoveryResult.Kind);
        Assert.Equal(ContinuityEvidenceLevel.BestEffort, machine.Snapshot.RecoveryResult.Evidence);
        Assert.False(machine.Snapshot.RecoveryResult.CanClaimLosslessContinuity);
    }

    [Fact]
    public void IntentionalDisconnectClearsInProgressRecoveryAndTerminalStopsRetry()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddMilliseconds(1));
        machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "EOF"), now.AddMilliseconds(2));
        machine.BeginRecovery(2, false, null, now.AddMilliseconds(3));
        machine.MarkRegistrationComplete(2, true, now.AddMilliseconds(4));

        machine.MarkIntentionalDisconnect(2, now.AddMilliseconds(5), "user requested");
        Assert.Equal(ConnectionContinuityState.Disconnected, machine.Snapshot.State);
        Assert.Equal(ContinuityRecoveryResultKind.InProgress, machine.Snapshot.RecoveryResult.Kind);
        Assert.False(machine.Snapshot.ReconnectActive);
        Assert.Null(machine.BeginRecovery(3, false, null, now.AddMilliseconds(6)));

        var terminalMachine = new ConnectionContinuityStateMachine();
        terminalMachine.BeginRecovery(1, false, null, now);
        terminalMachine.MarkTerminal(1, now.AddMilliseconds(1), "retry policy exhausted");
        Assert.Equal(ConnectionContinuityState.Terminal, terminalMachine.Snapshot.State);
        Assert.Null(terminalMachine.BeginRecovery(2, false, null, now.AddMilliseconds(2)));
        Assert.False(terminalMachine.Snapshot.ReconnectActive);
    }
}
