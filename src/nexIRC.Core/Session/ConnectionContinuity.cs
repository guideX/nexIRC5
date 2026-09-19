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
/// Describes the result of the current generation's bounded continuity work.
/// This is deliberately separate from <see cref="ConnectionContinuityState"/>:
/// a live replacement session can be ready for normal IRC operation without
/// having enough protocol evidence to claim lossless replay.
/// </summary>
public enum ContinuityRecoveryResultKind
{
    InProgress,
    NotRequired,
    NoGapObserved,
    Recovered,
    Unsupported,
    Partial,
    Failed
}

/// <summary>What the client can safely claim about a recovery result.</summary>
public enum ContinuityEvidenceLevel
{
    InProgress,
    NoRecoveryNecessary,
    StrongReplay,
    BestEffort,
    Partial,
    RecoveryFailure
}

/// <summary>
/// Typed, generation-owned recovery quality.  Boolean fields retain the
/// material facts that a single success flag would collapse.
/// </summary>
public sealed record ConnectionContinuityRecoveryResult(
    int ConnectionGeneration,
    bool RecoveryRequired,
    ContinuityRecoveryResultKind Kind,
    ContinuityEvidenceLevel Evidence,
    bool HistoryAvailable,
    bool RecoveryRequestSent,
    bool ReplayCompleted,
    bool ExactGapRecovered,
    bool RecoveryImpossible,
    bool LosslessContinuitySupported,
    string? Detail)
{
    public bool IsFinal => Kind is not ContinuityRecoveryResultKind.InProgress;

    /// <summary>
    /// True only when canonical replay evidence covered the bounded gap. A
    /// synchronized connection with unsupported or partial history is not a
    /// lossless-continuity claim.
    /// </summary>
    public bool CanClaimLosslessContinuity =>
        LosslessContinuitySupported
        && Evidence == ContinuityEvidenceLevel.StrongReplay
        && ExactGapRecovered
        && ReplayCompleted;

    public static ConnectionContinuityRecoveryResult InProgress(
        int connectionGeneration,
        bool recoveryRequired,
        string? detail = null) => new(
            connectionGeneration,
            recoveryRequired,
            ContinuityRecoveryResultKind.InProgress,
            ContinuityEvidenceLevel.InProgress,
            false,
            false,
            false,
            false,
            false,
            false,
            detail);

    public static ConnectionContinuityRecoveryResult FromOutcome(
        int connectionGeneration,
        bool recoveryRequired,
        ContinuitySynchronizationOutcome outcome,
        bool historyAvailable = false,
        bool recoveryRequestSent = false,
        bool replayCompleted = false,
        bool exactGapRecovered = false,
        bool recoveryImpossible = false,
        string? detail = null)
    {
        var kind = outcome switch
        {
            ContinuitySynchronizationOutcome.None => ContinuityRecoveryResultKind.InProgress,
            ContinuitySynchronizationOutcome.Recovered => ContinuityRecoveryResultKind.Recovered,
            ContinuitySynchronizationOutcome.NoGapObserved => ContinuityRecoveryResultKind.NoGapObserved,
            ContinuitySynchronizationOutcome.Unsupported => ContinuityRecoveryResultKind.Unsupported,
            ContinuitySynchronizationOutcome.Partial => ContinuityRecoveryResultKind.Partial,
            ContinuitySynchronizationOutcome.Failed => ContinuityRecoveryResultKind.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        var evidence = kind switch
        {
            ContinuityRecoveryResultKind.InProgress => ContinuityEvidenceLevel.InProgress,
            ContinuityRecoveryResultKind.NotRequired or ContinuityRecoveryResultKind.NoGapObserved => ContinuityEvidenceLevel.NoRecoveryNecessary,
            ContinuityRecoveryResultKind.Recovered when exactGapRecovered => ContinuityEvidenceLevel.StrongReplay,
            ContinuityRecoveryResultKind.Recovered => ContinuityEvidenceLevel.BestEffort,
            ContinuityRecoveryResultKind.Unsupported => ContinuityEvidenceLevel.BestEffort,
            ContinuityRecoveryResultKind.Partial => ContinuityEvidenceLevel.Partial,
            ContinuityRecoveryResultKind.Failed => ContinuityEvidenceLevel.RecoveryFailure,
            _ => ContinuityEvidenceLevel.InProgress
        };
        return new(
            connectionGeneration,
            recoveryRequired,
            kind,
            evidence,
            historyAvailable,
            recoveryRequestSent,
            replayCompleted,
            exactGapRecovered,
            recoveryImpossible,
            exactGapRecovered && kind == ContinuityRecoveryResultKind.Recovered,
            detail);
    }

    public static ConnectionContinuityRecoveryResult NotRequired(int connectionGeneration, string? detail = null) =>
        FromOutcome(
            connectionGeneration,
            false,
            ContinuitySynchronizationOutcome.NoGapObserved,
            detail: detail) with
        {
            Kind = ContinuityRecoveryResultKind.NotRequired
        };
}

public enum ContinuityDiagnosticKind
{
    GenerationStarted,
    InterruptionObserved,
    RegistrationCompleted,
    RecoveryStrategySelected,
    RecoveryRequestStarted,
    RecoveryRequestCompleted,
    RecoveryRequestCancelled,
    RecoveryRequestFailed,
    EventsAccepted,
    HistoricalDuplicateSuppressed,
    StaleCallbackRejected,
    SynchronizationCompleted,
    IntentionalDisconnect,
    TerminalFailure
}

public sealed record ContinuityDiagnosticEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    int ConnectionGeneration,
    int? RelatedGeneration,
    ContinuityDiagnosticKind Kind,
    string? Detail);

public sealed record ContinuityDiagnosticCounters(
    long RecoveryAttempts,
    long CompletedRecoveries,
    long UnsupportedRecoveries,
    long CancelledRecoveries,
    long FailedRecoveries,
    long StaleCallbacksRejected,
    long HistoricalDuplicatesSuppressed)
{
    public static ContinuityDiagnosticCounters Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed record ConnectionContinuityDiagnosticsSnapshot(
    IReadOnlyList<ContinuityDiagnosticEntry> Entries,
    ContinuityDiagnosticCounters Counters)
{
    public static ConnectionContinuityDiagnosticsSnapshot Empty { get; } = new(
        Array.Empty<ContinuityDiagnosticEntry>(),
        ContinuityDiagnosticCounters.Empty);
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
    public ConnectionContinuityRecoveryResult RecoveryResult { get; init; } =
        ConnectionContinuityRecoveryResult.InProgress(ConnectionGeneration, SynchronizationRequired);

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
        null)
    {
        RecoveryResult = ConnectionContinuityRecoveryResult.InProgress(0, false)
    };
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
    private const int MaximumDiagnostics = 256;
    private readonly object _gate = new();
    private readonly Queue<ContinuityDiagnosticEntry> _diagnostics = new();
    private ConnectionContinuitySnapshot _snapshot = ConnectionContinuitySnapshot.Initial;
    private long _diagnosticSequence;
    private long _recoveryAttempts;
    private long _completedRecoveries;
    private long _unsupportedRecoveries;
    private long _cancelledRecoveries;
    private long _failedRecoveries;
    private long _staleCallbacksRejected;
    private long _historicalDuplicatesSuppressed;

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

    public ConnectionContinuityDiagnosticsSnapshot Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new ConnectionContinuityDiagnosticsSnapshot(
                    _diagnostics.ToArray(),
                    new ContinuityDiagnosticCounters(
                        _recoveryAttempts,
                        _completedRecoveries,
                        _unsupportedRecoveries,
                        _cancelledRecoveries,
                        _failedRecoveries,
                        _staleCallbacksRejected,
                        _historicalDuplicatesSuppressed));
            }
        }
    }

    public void RecordDiagnostic(
        int generation,
        ContinuityDiagnosticKind kind,
        string? detail = null,
        int? relatedGeneration = null)
    {
        lock (_gate)
        {
            RecordDiagnosticUnsafe(generation, kind, detail, relatedGeneration);
        }
    }

    public void RecordStaleCallback(int generation, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        lock (_gate)
        {
            _staleCallbacksRejected++;
            RecordDiagnosticUnsafe(generation, ContinuityDiagnosticKind.StaleCallbackRejected, detail);
        }
    }

    public void RecordHistoricalDuplicateSuppressed(int generation, string? detail = null)
    {
        lock (_gate)
        {
            _historicalDuplicatesSuppressed++;
            RecordDiagnosticUnsafe(generation, ContinuityDiagnosticKind.HistoricalDuplicateSuppressed, detail);
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
                || _snapshot.State is ConnectionContinuityState.Terminal && _snapshot.ConnectionGeneration > 0
                || _snapshot.IsIntentional && _snapshot.State == ConnectionContinuityState.Disconnected)
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
                    RecoveryResult = ConnectionContinuityRecoveryResult.InProgress(generation, replacingGeneration),
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
                    RecoveryResult = ConnectionContinuityRecoveryResult.InProgress(snapshot.ConnectionGeneration, true, detail ?? failure.Message),
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
                    RecoveryResult = synchronizationRequired
                        ? ConnectionContinuityRecoveryResult.InProgress(generation, true)
                        : ConnectionContinuityRecoveryResult.NotRequired(generation, "Initial IRC registration completed."),
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
        return CompleteSynchronization(
            generation,
            ConnectionContinuityRecoveryResult.FromOutcome(
                generation,
                recoveryRequired: true,
                outcome,
                historyAvailable: outcome is not ContinuitySynchronizationOutcome.Unsupported,
                recoveryRequestSent: outcome is ContinuitySynchronizationOutcome.Recovered or ContinuitySynchronizationOutcome.Partial or ContinuitySynchronizationOutcome.Failed,
                replayCompleted: outcome is ContinuitySynchronizationOutcome.Recovered,
                recoveryImpossible: outcome is ContinuitySynchronizationOutcome.Unsupported,
                detail: detail),
            now,
            detail);
    }

    public ConnectionContinuityTransition? CompleteSynchronization(
        int generation,
        ConnectionContinuityRecoveryResult result,
        DateTimeOffset now,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ConnectionGeneration != generation)
        {
            throw new ArgumentException("The recovery result belongs to another connection generation.", nameof(result));
        }

        if (!result.IsFinal || result.Kind == ContinuityRecoveryResultKind.InProgress)
        {
            throw new ArgumentException("Synchronization requires a final recovery result.", nameof(result));
        }

        lock (_gate)
        {
            if (generation != _snapshot.ConnectionGeneration)
            {
                _staleCallbacksRejected++;
                RecordDiagnosticUnsafe(generation, ContinuityDiagnosticKind.StaleCallbackRejected, "A stale synchronization completion was ignored.");
                return null;
            }

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
                    SynchronizationOutcome = ToSynchronizationOutcome(result.Kind),
                    RecoveryResult = result with { Detail = detail ?? result.Detail },
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
                    RecoveryResult = ConnectionContinuityRecoveryResult.InProgress(generation, false, detail),
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
        if (outcome is ContinuitySynchronizationOutcome.None)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

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
                    RecoveryResult = ConnectionContinuityRecoveryResult.FromOutcome(
                        generation,
                        snapshot.SynchronizationRequired,
                        outcome,
                        recoveryImpossible: outcome == ContinuitySynchronizationOutcome.Unsupported,
                        detail: detail),
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
        RecordDiagnosticUnsafe(
            updated.ConnectionGeneration,
            reason switch
            {
                ConnectionContinuityTransitionReason.TransportInterrupted => ContinuityDiagnosticKind.InterruptionObserved,
                ConnectionContinuityTransitionReason.RegistrationCompleted => ContinuityDiagnosticKind.RegistrationCompleted,
                ConnectionContinuityTransitionReason.SynchronizationCompleted => ContinuityDiagnosticKind.SynchronizationCompleted,
                ConnectionContinuityTransitionReason.IntentionalDisconnect => ContinuityDiagnosticKind.IntentionalDisconnect,
                ConnectionContinuityTransitionReason.TerminalFailure => ContinuityDiagnosticKind.TerminalFailure,
                _ => ContinuityDiagnosticKind.GenerationStarted
            },
            updated.Detail,
            updated.PreviousGeneration);
        if (next == ConnectionContinuityState.Recovering && updated.PreviousGeneration is not null)
        {
            _recoveryAttempts++;
        }

        if (next == ConnectionContinuityState.Synchronized && updated.RecoveryResult.RecoveryRequired)
        {
            _completedRecoveries++;
            switch (updated.RecoveryResult.Kind)
            {
                case ContinuityRecoveryResultKind.Unsupported:
                    _unsupportedRecoveries++;
                    break;
                case ContinuityRecoveryResultKind.Failed:
                    _failedRecoveries++;
                    break;
            }
        }

        _snapshot = updated;
        return new ConnectionContinuityTransition(previous, next, updated);
    }

    private void RecordDiagnosticUnsafe(
        int generation,
        ContinuityDiagnosticKind kind,
        string? detail,
        int? relatedGeneration = null)
    {
        _diagnosticSequence++;
        _diagnostics.Enqueue(new ContinuityDiagnosticEntry(
            _diagnosticSequence,
            DateTimeOffset.UtcNow,
            generation,
            relatedGeneration,
            kind,
            detail));
        while (_diagnostics.Count > MaximumDiagnostics)
        {
            _diagnostics.Dequeue();
        }

        if (kind == ContinuityDiagnosticKind.RecoveryRequestCancelled)
        {
            _cancelledRecoveries++;
        }
    }

    private static ContinuitySynchronizationOutcome ToSynchronizationOutcome(ContinuityRecoveryResultKind kind) => kind switch
    {
        ContinuityRecoveryResultKind.NotRequired => ContinuitySynchronizationOutcome.NoGapObserved,
        ContinuityRecoveryResultKind.NoGapObserved => ContinuitySynchronizationOutcome.NoGapObserved,
        ContinuityRecoveryResultKind.Recovered => ContinuitySynchronizationOutcome.Recovered,
        ContinuityRecoveryResultKind.Unsupported => ContinuitySynchronizationOutcome.Unsupported,
        ContinuityRecoveryResultKind.Partial => ContinuitySynchronizationOutcome.Partial,
        ContinuityRecoveryResultKind.Failed => ContinuitySynchronizationOutcome.Failed,
        _ => ContinuitySynchronizationOutcome.None
    };
}
