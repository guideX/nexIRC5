namespace nexIRC.Application;

internal sealed class PendingActionOperation
{
    public required IrcOperationResult Result { get; set; }

    public required string Command { get; init; }

    public string? CurrentNickname { get; set; }

    public CancellationTokenSource Lifetime { get; } = new();

    public int Retired;
}
