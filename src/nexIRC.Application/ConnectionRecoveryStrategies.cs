using nexIRC.Core.Session;
using nexIRC.Core.Protocol;

namespace nexIRC.Application;

/// <summary>
/// Bounded work performed by a concrete recovery mechanism.  The continuity
/// owner turns this into the existing typed recovery result; a strategy never
/// transitions lifecycle state itself.
/// </summary>
public sealed record ConnectionRecoveryExecutionResult(
    ContinuitySynchronizationOutcome Outcome,
    bool HistoryAvailable,
    bool RecoveryRequestSent,
    bool ReplayCompleted,
    bool ExactGapRecovered,
    bool RecoveryImpossible,
    string? Detail,
    int RecoveredEventCount = 0,
    bool UnresolvedGap = false,
    int CommandsIssued = 0)
{
    public NexIrcResumeExecutionResult? NativeResume { get; init; }

    public bool FallbackRecommended { get; init; }

    public string? FallbackReason { get; init; }

    public static ConnectionRecoveryExecutionResult Unsupported { get; } = new(
        ContinuitySynchronizationOutcome.Unsupported,
        HistoryAvailable: false,
        RecoveryRequestSent: false,
        ReplayCompleted: false,
        ExactGapRecovered: false,
        RecoveryImpossible: true,
        "The server does not advertise usable CHATHISTORY; continuity is bounded to the live replacement session.",
        UnresolvedGap: true);

    public static ConnectionRecoveryExecutionResult Failed(bool historyAvailable, string detail) => new(
        ContinuitySynchronizationOutcome.Failed,
        historyAvailable,
        RecoveryRequestSent: false,
        ReplayCompleted: false,
        ExactGapRecovered: false,
        RecoveryImpossible: true,
        detail,
        UnresolvedGap: true);
}

/// <summary>
/// The only application-owned dependency supplied to a strategy.  The
/// executor contains durable conversation/history integration but no lifecycle
/// transition authority, allowing a future replay mechanism to use the same
/// barrier.
/// </summary>
public sealed record ConnectionRecoveryStrategyContext(
    ServerSession Session,
    ConnectionRecoveryBoundary Boundary,
    Func<CancellationToken, ValueTask<ConnectionRecoveryExecutionResult>> ExecuteBoundedRecoveryAsync,
    Func<CancellationToken, ValueTask<ConnectionRecoveryExecutionResult>>? ExecuteNativeResumeAsync = null)
{
    public int ConnectionGeneration => Boundary.CurrentGeneration;
}

public interface IConnectionRecoveryStrategy
{
    ConnectionRecoveryStrategyId Id { get; }

    ValueTask<ConnectionContinuityRecoveryResult> RecoverAsync(
        ConnectionRecoveryStrategyContext context,
        string selectionReason,
        CancellationToken cancellationToken);
}

public sealed class NoConnectionRecoveryStrategy : IConnectionRecoveryStrategy
{
    public ConnectionRecoveryStrategyId Id => ConnectionRecoveryStrategyId.None;

    public ValueTask<ConnectionContinuityRecoveryResult> RecoverAsync(
        ConnectionRecoveryStrategyContext context,
        string selectionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionReason);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            ConnectionContinuityRecoveryResult.NotRequired(context.ConnectionGeneration, selectionReason) with
            {
                Strategy = Id,
                StrategyReason = selectionReason
            });
    }
}

public sealed class BestEffortNoHistoryRecoveryStrategy : IConnectionRecoveryStrategy
{
    public ConnectionRecoveryStrategyId Id => ConnectionRecoveryStrategyId.BestEffortNoHistory;

    public ValueTask<ConnectionContinuityRecoveryResult> RecoverAsync(
        ConnectionRecoveryStrategyContext context,
        string selectionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionReason);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            ConnectionContinuityRecoveryResult.FromOutcome(
                context.ConnectionGeneration,
                recoveryRequired: true,
                ContinuitySynchronizationOutcome.Unsupported,
                historyAvailable: false,
                recoveryImpossible: true,
                detail: selectionReason) with
            {
                Strategy = Id,
                StrategyReason = selectionReason,
                UnresolvedGap = true
            });
    }
}

public sealed class Ircv3ChatHistoryRecoveryStrategy : IConnectionRecoveryStrategy
{
    public ConnectionRecoveryStrategyId Id => ConnectionRecoveryStrategyId.Ircv3ChatHistory;

    public async ValueTask<ConnectionContinuityRecoveryResult> RecoverAsync(
        ConnectionRecoveryStrategyContext context,
        string selectionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionReason);
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await context.ExecuteBoundedRecoveryAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ConnectionContinuityRecoveryResult.FromOutcome(
                context.ConnectionGeneration,
                recoveryRequired: true,
                execution.Outcome,
                execution.HistoryAvailable,
                execution.RecoveryRequestSent,
                execution.ReplayCompleted,
                execution.ExactGapRecovered,
                execution.RecoveryImpossible,
                execution.Detail) with
            {
                Strategy = Id,
                StrategyReason = selectionReason,
                RecoveredEventCount = execution.RecoveredEventCount,
                UnresolvedGap = execution.UnresolvedGap,
                CommandsIssued = execution.CommandsIssued
            };
    }
}

/// <summary>
/// Experimental native replay strategy. The strategy owns only protocol
/// execution and result mapping; the continuity state machine remains the
/// owner of the synchronization barrier.
/// </summary>
public sealed class NexIrcResumeRecoveryStrategy : IConnectionRecoveryStrategy
{
    public ConnectionRecoveryStrategyId Id => ConnectionRecoveryStrategyId.NexIrcResume;

    public async ValueTask<ConnectionContinuityRecoveryResult> RecoverAsync(
        ConnectionRecoveryStrategyContext context,
        string selectionReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionReason);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.ExecuteNativeResumeAsync is null)
        {
            return CreateResult(
                context,
                selectionReason,
                new ConnectionRecoveryExecutionResult(
                    ContinuitySynchronizationOutcome.Unsupported,
                    HistoryAvailable: false,
                    RecoveryRequestSent: false,
                    ReplayCompleted: false,
                    ExactGapRecovered: false,
                    RecoveryImpossible: true,
                    Detail: "Native resume execution was not supplied by the connection owner.",
                    UnresolvedGap: true)
                {
                    FallbackRecommended = true,
                    FallbackReason = "Native resume execution is unavailable."
                });
        }

        var execution = await context.ExecuteNativeResumeAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var native = execution.NativeResume;
        var fallbackRecommended = native is { Outcome: NexIrcResumeOutcome.Rejected, FallbackSafe: true };
        if (fallbackRecommended)
        {
            execution = execution with
            {
                FallbackRecommended = true,
                FallbackReason = native!.Detail ?? "The server rejected the native resume request before replay began."
            };
        }

        return CreateResult(context, selectionReason, execution);
    }

    private ConnectionContinuityRecoveryResult CreateResult(
        ConnectionRecoveryStrategyContext context,
        string selectionReason,
        ConnectionRecoveryExecutionResult execution)
    {
        var native = execution.NativeResume;
        var outcome = execution.Outcome;
        if (native is { Outcome: NexIrcResumeOutcome.Rejected, FallbackSafe: true })
        {
            outcome = ContinuitySynchronizationOutcome.Unsupported;
        }
        else if (native is { Outcome: NexIrcResumeOutcome.Completed })
        {
            outcome = ContinuitySynchronizationOutcome.Recovered;
        }
        else if (native is { Outcome: NexIrcResumeOutcome.Accepted })
        {
            outcome = ContinuitySynchronizationOutcome.Partial;
        }

        return ConnectionContinuityRecoveryResult.FromOutcome(
                context.ConnectionGeneration,
                recoveryRequired: true,
                outcome,
                historyAvailable: native?.CapabilityNegotiated == true,
                recoveryRequestSent: execution.RecoveryRequestSent,
                replayCompleted: execution.ReplayCompleted,
                exactGapRecovered: execution.ExactGapRecovered,
                recoveryImpossible: execution.RecoveryImpossible,
                detail: execution.Detail) with
            {
                Strategy = Id,
                StrategyReason = selectionReason,
                RecoveredEventCount = execution.RecoveredEventCount,
                UnresolvedGap = execution.UnresolvedGap,
                CommandsIssued = execution.CommandsIssued,
                FallbackRecommended = execution.FallbackRecommended,
                FallbackReason = execution.FallbackReason,
                NativeResumeOutcome = native?.Outcome,
                NativeResumeRejectionReason = native?.RejectionReason
            };
    }
}

public sealed record SelectedConnectionRecoveryStrategy(
    ConnectionRecoveryStrategySelection Selection,
    IConnectionRecoveryStrategy Strategy);

/// <summary>
/// Owns strategy selection for a synchronizing generation.  The selector is
/// deliberately stateless: replacement generations cannot inherit a prior
/// strategy or prior capability snapshot.
/// </summary>
public sealed class ConnectionRecoveryStrategySelector
{
    private readonly IConnectionRecoveryStrategy _noRecovery = new NoConnectionRecoveryStrategy();
    private readonly IConnectionRecoveryStrategy _noHistory = new BestEffortNoHistoryRecoveryStrategy();
    private readonly IConnectionRecoveryStrategy _ircv3History = new Ircv3ChatHistoryRecoveryStrategy();
    private readonly IConnectionRecoveryStrategy _nativeResume = new NexIrcResumeRecoveryStrategy();

    public SelectedConnectionRecoveryStrategy Select(ConnectionRecoveryStrategyContext context, bool recoveryRequired)
    {
        ArgumentNullException.ThrowIfNull(context);
        var selection = ConnectionRecoveryStrategyPolicy.Select(
            recoveryRequired,
            context.Session.NativeResumeSupport,
            context.Session.Snapshot.Features.Chathistory);
        var strategy = selection.Strategy switch
        {
            ConnectionRecoveryStrategyId.None => _noRecovery,
            ConnectionRecoveryStrategyId.NexIrcResume => _nativeResume,
            ConnectionRecoveryStrategyId.Ircv3ChatHistory => _ircv3History,
            ConnectionRecoveryStrategyId.BestEffortNoHistory => _noHistory,
            _ => throw new ArgumentOutOfRangeException()
        };
        return new SelectedConnectionRecoveryStrategy(selection, strategy);
    }

    public SelectedConnectionRecoveryStrategy SelectFallback(ConnectionRecoveryStrategyContext context, bool recoveryRequired)
    {
        ArgumentNullException.ThrowIfNull(context);
        var selection = ConnectionRecoveryStrategyPolicy.Select(
            recoveryRequired,
            NexIrcResumeSupport.Unavailable,
            context.Session.Snapshot.Features.Chathistory);
        var strategy = selection.Strategy switch
        {
            ConnectionRecoveryStrategyId.None => _noRecovery,
            ConnectionRecoveryStrategyId.Ircv3ChatHistory => _ircv3History,
            ConnectionRecoveryStrategyId.BestEffortNoHistory => _noHistory,
            _ => throw new ArgumentOutOfRangeException()
        };
        return new SelectedConnectionRecoveryStrategy(selection, strategy);
    }
}
