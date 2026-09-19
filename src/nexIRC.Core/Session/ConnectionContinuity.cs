using nexIRC.Core.Networking;

namespace nexIRC.Core.Session;

/// <summary>
/// Product-level continuity state.  This deliberately remains separate from
/// the detailed IRC transport/registration state: a socket can be connected
/// while the replacement session is still being reconciled.
/// </summary>
public enum ConnectionContinuityState
{
    Disconnected,
    Interrupted,
    Recovering,
    Synchronizing,
    Synchronized,
    Terminal
}

public enum ConnectionContinuityTransitionReason
{
    None,
    InitialConnection,
    ReplacementConnection,
    TransportInterrupted,
    RegistrationCompleted,
    SynchronizationCompleted,
    IntentionalDisconnect,
    TerminalFailure,
    Shutdown
}

public enum ContinuitySynchronizationOutcome
{
    None,
    Recovered,
    NoGapObserved,
    Unsupported,
    Partial,
    Failed
}

/// <summary>
/// Bounded metadata for one continuity episode.  It contains no transport
/// objects or workspace snapshot; durable conversation state remains owned by
/// the normal session/application stores.
/// </summary>
public sealed record ConnectionContinuitySnapshot(
    ConnectionContinuityState State,
    int ConnectionGeneration,
    int? PreviousGeneration,
    bool RegistrationComplete,
    bool SynchronizationRequired,
    bool SynchronizationComplete,
    bool ReconnectActive,
    bool IsIntentional,
    ConnectionFailureKind? InterruptionFailure,
    ContinuitySynchronizationOutcome SynchronizationOutcome,
    DateTimeOffset? EpisodeStartedAt,
    ConnectionContinuityTransitionReason LastTransitionReason,
    string? Detail)
{
    public static ConnectionContinuitySnapshot Initial { get; } = new(
        ConnectionContinuityState.Disconnected,
        0,
        null,
        false,
        false,
        false,
        false,
        false,
        null,
        ContinuitySynchronizationOutcome.None,
        null,
        ConnectionContinuityTransitionReason.None,
        null);
}

public sealed record ConnectionContinuityTransition(
    ConnectionContinuityState Previous,
    ConnectionContinuityState Current,
    ConnectionContinuitySnapshot Snapshot);

/// <summary>
/// Deterministic, generation-owned continuity transition model.  A rejected
/// generation check is intentionally a no-op so late callbacks can safely
/// complete without affecting the current session.
/// </summary>
public sealed class ConnectionContinuityStateMachine
{
    private readonly object _gate = new();
    private ConnectionContinuitySnapshot _snapshot = ConnectionContinuitySnapshot.Initial;

    public ConnectionContinuitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public ConnectionContinuityTransition? BeginRecovery(
        int generation,
        bool replacementRecovery,
        int? previousGeneration,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (generation <= _snapshot.ConnectionGeneration
                || _snapshot.State is ConnectionContinuityState.Terminal && _snapshot.ConnectionGeneration > 0)
            {
                return null;
            }

            var replacingGeneration = replacementRecovery || _snapshot.ConnectionGeneration > 0;
            return TransitionUnsafe(
                ConnectionContinuityState.Recovering,
                replacingGeneration ? ConnectionContinuityTransitionReason.ReplacementConnection : ConnectionContinuityTransitionReason.InitialConnection,
                now,
                snapshot => snapshot with
                {
                    ConnectionGeneration = generation,
                    PreviousGeneration = replacingGeneration
                        ? previousGeneration ?? (snapshot.ConnectionGeneration > 0 ? snapshot.ConnectionGeneration : null)
                        : null,
                    RegistrationComplete = false,
                    SynchronizationRequired = replacingGeneration,
                    SynchronizationComplete = false,
                    ReconnectActive = replacingGeneration,
                    IsIntentional = false,
                    InterruptionFailure = null,
                    SynchronizationOutcome = ContinuitySynchronizationOutcome.None,
                    EpisodeStartedAt = snapshot.EpisodeStartedAt ?? now,
                    Detail = null
                });
        }
    }

    public ConnectionContinuityTransition? MarkInterrupted(
        int generation,
        ConnectionFailure failure,
        DateTimeOffset now,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (generation != _snapshot.ConnectionGeneration
                || _snapshot.State is ConnectionContinuityState.Disconnected or ConnectionContinuityState.Terminal)
            {
                return null;
            }

            return TransitionUnsafe(
                ConnectionContinuityState.Interrupted,
                ConnectionContinuityTransitionReason.TransportInterrupted,
                now,
                snapshot => snapshot with
                {
                    RegistrationComplete = false,
                    SynchronizationComplete = false,
                    ReconnectActive = true,
                    IsIntentional = false,
                    InterruptionFailure = failure.Kind,
                    SynchronizationOutcome = ContinuitySynchronizationOutcome.None,
                    EpisodeStartedAt = snapshot.EpisodeStartedAt ?? now,
                    Detail = detail ?? failure.Message
                });
        }
    }

    public ConnectionContinuityTransition? MarkRegistrationComplete(
        int generation,
        bool synchronizationRequired,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (generation != _snapshot.ConnectionGeneration
                || _snapshot.State != ConnectionContinuityState.Recovering)
            {
                return null;
            }

            var next = synchronizationRequired
                ? ConnectionContinuityState.Synchronizing
                : ConnectionContinuityState.Synchronized;
            return TransitionUnsafe(
                next,
                ConnectionContinuityTransitionReason.RegistrationCompleted,
                now,
                snapshot => snapshot with
                {
                    RegistrationComplete = true,
                    SynchronizationRequired = synchronizationRequired,
                    SynchronizationComplete = !synchronizationRequired,
                    ReconnectActive = synchronizationRequired,
                    IsIntentional = false,
                    SynchronizationOutcome = synchronizationRequired
                        ? ContinuitySynchronizationOutcome.None
                        : ContinuitySynchronizationOutcome.NoGapObserved,
                    Detail = synchronizationRequired ? "IRC registration completed; continuity recovery is pending." : "Initial IRC registration completed."
                });
        }
    }

    public ConnectionContinuityTransition? CompleteSynchronization(
        int generation,
        ContinuitySynchronizationOutcome outcome,
        DateTimeOffset now,
        string? detail = null)
    {
        if (outcome is ContinuitySynchronizationOutcome.None)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        lock (_gate)
        {
            if (generation != _snapshot.ConnectionGeneration
                || _snapshot.State != ConnectionContinuityState.Synchronizing
                || !_snapshot.RegistrationComplete)
            {
                return null;
            }

            return TransitionUnsafe(
                ConnectionContinuityState.Synchronized,
                ConnectionContinuityTransitionReason.SynchronizationCompleted,
                now,
                snapshot => snapshot with
                {
                    SynchronizationComplete = true,
                    ReconnectActive = false,
                    IsIntentional = false,
                    SynchronizationOutcome = outcome,
                    Detail = detail
                });
        }
    }

    public ConnectionContinuityTransition? MarkIntentionalDisconnect(
        int generation,
        DateTimeOffset now,
        string? detail = null)
    {
        lock (_gate)
        {
            if (generation != _snapshot.ConnectionGeneration
                || _snapshot.State == ConnectionContinuityState.Disconnected)
            {
                return null;
            }

            return TransitionUnsafe(
                ConnectionContinuityState.Disconnected,
                ConnectionContinuityTransitionReason.IntentionalDisconnect,
                now,
                snapshot => snapshot with
                {
                    RegistrationComplete = false,
                    SynchronizationComplete = false,
                    ReconnectActive = false,
                    IsIntentional = true,
                    SynchronizationOutcome = ContinuitySynchronizationOutcome.None,
                    Detail = detail
                });
        }
    }

    public ConnectionContinuityTransition? MarkTerminal(
        int generation,
        DateTimeOffset now,
        string detail,
        ContinuitySynchronizationOutcome outcome = ContinuitySynchronizationOutcome.Failed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        lock (_gate)
        {
            if (generation != _snapshot.ConnectionGeneration
                || _snapshot.State == ConnectionContinuityState.Terminal)
            {
                return null;
            }

            return TransitionUnsafe(
                ConnectionContinuityState.Terminal,
                ConnectionContinuityTransitionReason.TerminalFailure,
                now,
                snapshot => snapshot with
                {
                    RegistrationComplete = false,
                    SynchronizationComplete = false,
                    ReconnectActive = false,
                    IsIntentional = false,
                    SynchronizationOutcome = outcome,
                    Detail = detail
                });
        }
    }

    private ConnectionContinuityTransition TransitionUnsafe(
        ConnectionContinuityState next,
        ConnectionContinuityTransitionReason reason,
        DateTimeOffset now,
        Func<ConnectionContinuitySnapshot, ConnectionContinuitySnapshot> update)
    {
        var previous = _snapshot.State;
        var updated = update(_snapshot) with
        {
            State = next,
            LastTransitionReason = reason,
            EpisodeStartedAt = _snapshot.EpisodeStartedAt ?? now
        };
        _snapshot = updated;
        return new ConnectionContinuityTransition(previous, next, updated);
    }
}
