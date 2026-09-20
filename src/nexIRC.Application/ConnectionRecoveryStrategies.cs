using nexIRC.Core.Session;

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
    Func<CancellationToken, ValueTask<ConnectionRecoveryExecutionResult>> ExecuteBoundedRecoveryAsync)
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

    public SelectedConnectionRecoveryStrategy Select(ConnectionRecoveryStrategyContext context, bool recoveryRequired)
    {
        ArgumentNullException.ThrowIfNull(context);
        var selection = ConnectionRecoveryStrategyPolicy.Select(
            recoveryRequired,
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
