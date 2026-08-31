using nexIRC.Core.State;

namespace nexIRC.Application;

public enum IrcQueryCorrelationMode
{
    LabeledResponse,
    SerializedUnlabeled,
    CoalescedUnlabeled
}

public sealed record IrcQueryOperation(
    Guid Id,
    Guid NetworkId,
    string Kind,
    string Target,
    string? RequestLabel,
    IrcQueryCorrelationMode Correlation,
    DateTimeOffset StartedAt);

public sealed record IrcQueryRequestResult(
    WorkspaceView View,
    IrcQueryOperation Operation,
    bool WasCoalesced);

internal sealed class NetworkOperationState
{
    public Dictionary<string, ActiveOperation> LabeledWhois { get; } = new(StringComparer.Ordinal);

    public ActiveOperation? UnlabeledWhois { get; set; }

    public Queue<ActiveOperation> QueuedWhois { get; } = new();

    public ActiveOperation? ActiveList { get; set; }

    public bool HasAnyWhois => UnlabeledWhois is not null || QueuedWhois.Count > 0 || LabeledWhois.Count > 0;
}

internal sealed class ActiveOperation
{
    public required IrcQueryOperation Operation { get; init; }

    public required WorkspaceView View { get; init; }

    public CancellationTokenSource Lifetime { get; } = new();

    public int Retired;
}

internal static class IrcIdentity
{
    public static bool Equals(string left, string right, IrcCaseMapping mapping) => IrcCaseMappingComparer.Equals(left, right, mapping);
}
