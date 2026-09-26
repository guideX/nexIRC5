using nexIRC.Core.Protocol;

namespace nexIRC.Core.Session;

/// <summary>
/// Identifies the recovery mechanism selected for one connection generation.
/// The values describe implemented behavior only; future server-specific
/// mechanisms can add a value without adding another continuity lifecycle.
/// </summary>
public enum ConnectionRecoveryStrategyId
{
    None,
    NexIrcResume,
    Ircv3ChatHistory,
    BestEffortNoHistory
}

/// <summary>
/// The protocol-neutral facts captured at an interruption boundary.  It is
/// intentionally limited to durable conversation evidence and does not expose
/// transport objects, UI state, or application lifecycle state.
/// </summary>
public sealed record ConnectionRecoveryBoundary(
    int CurrentGeneration,
    int? PreviousSynchronizedGeneration,
    DateTimeOffset CapturedAt,
    IReadOnlyList<ConnectionRecoveryConversationBoundary> Conversations,
    IReadOnlyList<string> KnownGapKeys)
{
    public static ConnectionRecoveryBoundary Empty(int generation, DateTimeOffset capturedAt) => new(
        generation,
        null,
        capturedAt,
        Array.Empty<ConnectionRecoveryConversationBoundary>(),
        Array.Empty<string>());
}

/// <summary>Durable canonical evidence for one conversation at reconnect.</summary>
public sealed record ConnectionRecoveryConversationBoundary(
    string Conversation,
    string Target,
    string? LastCanonicalMessageId,
    DateTimeOffset LastServerTimestamp,
    bool IsChannel,
    long DurableSequence);

public sealed record ConnectionRecoveryStrategySelection(
    ConnectionRecoveryStrategyId Strategy,
    string Reason)
{
    public static ConnectionRecoveryStrategySelection InitialConnection { get; } = new(
        ConnectionRecoveryStrategyId.None,
        "Initial IRC registration has no interruption boundary to recover.");
}

/// <summary>
/// Pure, capability-driven strategy selection policy.  It consumes only the
/// current generation's negotiated history support and never server identity,
/// hostname, software brand, or msgid shape.
/// </summary>
public static class ConnectionRecoveryStrategyPolicy
{
    public static ConnectionRecoveryStrategySelection Select(
        bool recoveryRequired,
        ChathistorySupport historySupport)
    {
        ArgumentNullException.ThrowIfNull(historySupport);
        if (!recoveryRequired)
        {
            return ConnectionRecoveryStrategySelection.InitialConnection;
        }

        return historySupport.IsUsable
            ? new(
                ConnectionRecoveryStrategyId.Ircv3ChatHistory,
                "The current generation negotiated usable IRCv3 CHATHISTORY support.")
            : new(
                ConnectionRecoveryStrategyId.BestEffortNoHistory,
                "The current generation did not negotiate usable history; live readiness will use bounded best-effort degradation.");
    }

    public static ConnectionRecoveryStrategySelection Select(
        bool recoveryRequired,
        NexIrcResumeSupport nativeResumeSupport,
        ChathistorySupport historySupport)
    {
        ArgumentNullException.ThrowIfNull(nativeResumeSupport);
        ArgumentNullException.ThrowIfNull(historySupport);
        if (!recoveryRequired)
        {
            return ConnectionRecoveryStrategySelection.InitialConnection;
        }

        if (nativeResumeSupport.IsUsable)
        {
            return new(
                ConnectionRecoveryStrategyId.NexIrcResume,
                "The current generation negotiated nexIRC native resume and retained a usable logical session.");
        }

        return Select(recoveryRequired, historySupport);
    }
}
