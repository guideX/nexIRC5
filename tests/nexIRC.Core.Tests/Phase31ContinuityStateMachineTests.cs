using System.Globalization;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;

namespace nexIRC.Core.Tests;

public sealed class Phase31ContinuityStateMachineTests
{
    [Fact]
    public void InitialRegistrationWithoutRecoveryReachesSynchronized()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);

        Assert.Equal(ConnectionContinuityState.Recovering, machine.BeginRecovery(1, false, null, now)!.Current);
        var registered = machine.MarkRegistrationComplete(1, false, now.AddSeconds(1));

        Assert.Equal(ConnectionContinuityState.Synchronized, registered!.Current);
        Assert.Equal(ContinuitySynchronizationOutcome.NoGapObserved, machine.Snapshot.SynchronizationOutcome);
        Assert.True(machine.Snapshot.RegistrationComplete);
        Assert.True(machine.Snapshot.SynchronizationComplete);
    }

    [Fact]
    public void ReplacementRegistrationCannotClaimSynchronizedBeforeBarrier()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddSeconds(1));
        machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "EOF"), now.AddSeconds(2));

        var recovering = machine.BeginRecovery(2, false, null, now.AddSeconds(3));
        var synchronizing = machine.MarkRegistrationComplete(2, true, now.AddSeconds(4));

        Assert.Equal(ConnectionContinuityState.Recovering, recovering!.Current);
        Assert.Equal(ConnectionContinuityState.Synchronizing, synchronizing!.Current);
        Assert.False(machine.Snapshot.SynchronizationComplete);
        Assert.Null(machine.CompleteSynchronization(1, ContinuitySynchronizationOutcome.Recovered, now.AddSeconds(5)));
        Assert.Equal(ConnectionContinuityState.Synchronizing, machine.Snapshot.State);
    }

    [Fact]
    public void StaleGenerationInterruptionAndHistoryCompletionAreIgnored()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddSeconds(1));
        machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.Network, "reset"), now.AddSeconds(2));
        machine.BeginRecovery(2, false, null, now.AddSeconds(3));
        machine.MarkRegistrationComplete(2, true, now.AddSeconds(4));

        Assert.Null(machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.Network, "late"), now.AddSeconds(5)));
        Assert.Null(machine.CompleteSynchronization(1, ContinuitySynchronizationOutcome.Recovered, now.AddSeconds(5)));
        Assert.Equal(2, machine.Snapshot.ConnectionGeneration);
        Assert.Equal(ConnectionContinuityState.Synchronizing, machine.Snapshot.State);
    }

    [Fact]
    public void UnsupportedHistoryCompletesAsDegradedSynchronizedState()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.BeginRecovery(4, true, 3, now);
        machine.MarkRegistrationComplete(4, true, now.AddSeconds(1));

        var completed = machine.CompleteSynchronization(4, ContinuitySynchronizationOutcome.Unsupported, now.AddSeconds(2), "CHATHISTORY unavailable");

        Assert.Equal(ConnectionContinuityState.Synchronized, completed!.Current);
        Assert.Equal(ContinuitySynchronizationOutcome.Unsupported, machine.Snapshot.SynchronizationOutcome);
        Assert.False(machine.Snapshot.ReconnectActive);
        Assert.Contains("unavailable", machine.Snapshot.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedInterruptionOnlyNewestGenerationCanWin()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddSeconds(1));
        machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "first"), now.AddSeconds(2));
        machine.BeginRecovery(2, false, null, now.AddSeconds(3));
        machine.MarkInterrupted(2, new ConnectionFailure(ConnectionFailureKind.Network, "second"), now.AddSeconds(4));
        machine.BeginRecovery(3, false, null, now.AddSeconds(5));
        machine.MarkRegistrationComplete(3, true, now.AddSeconds(6));

        Assert.Equal(3, machine.Snapshot.ConnectionGeneration);
        Assert.Equal(2, machine.Snapshot.PreviousGeneration);
        Assert.Equal(ConnectionContinuityState.Synchronizing, machine.Snapshot.State);
        Assert.Null(machine.CompleteSynchronization(2, ContinuitySynchronizationOutcome.Recovered, now.AddSeconds(7)));
    }

    [Fact]
    public void IntentionalDisconnectDoesNotEnterInterruptedRecoveryPath()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.BeginRecovery(1, false, null, now);
        machine.MarkRegistrationComplete(1, false, now.AddSeconds(1));

        var disconnected = machine.MarkIntentionalDisconnect(1, now.AddSeconds(2), "user disconnect");

        Assert.Equal(ConnectionContinuityState.Disconnected, disconnected!.Current);
        Assert.True(machine.Snapshot.IsIntentional);
        Assert.False(machine.Snapshot.ReconnectActive);
        Assert.Null(machine.MarkInterrupted(1, new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "late"), now.AddSeconds(3)));
    }

    [Fact]
    public void TerminalFailureStopsRecoveryAndRecordsFailureOutcome()
    {
        var machine = new ConnectionContinuityStateMachine();
        var now = DateTimeOffset.UtcNow;
        machine.BeginRecovery(1, false, null, now);

        var terminal = machine.MarkTerminal(1, now.AddSeconds(1), "SASL rejected");

        Assert.Equal(ConnectionContinuityState.Terminal, terminal!.Current);
        Assert.Equal(ContinuitySynchronizationOutcome.Failed, machine.Snapshot.SynchronizationOutcome);
        Assert.False(machine.Snapshot.ReconnectActive);
        Assert.Null(machine.BeginRecovery(2, false, null, now.AddSeconds(2)));
    }
}
