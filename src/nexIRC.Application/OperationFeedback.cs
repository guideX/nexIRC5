using System.Collections.ObjectModel;

namespace nexIRC.Application;

public enum IrcOperationType
{
    ModeChange,
    Kick,
    Ban,
    Unban,
    Invite,
    Whois,
    Notice,
    Ctcp,
    ChannelModeQuery,
    BanListQuery
}

public enum IrcOperationState
{
    Pending,
    Confirmed,
    Rejected,
    TimedOut,
    Cancelled,
    Disconnected
}

public sealed record IrcOperationTimeoutPolicy(
    TimeSpan? Whois = null,
    TimeSpan? BanList = null,
    TimeSpan? Moderation = null,
    TimeSpan? Invite = null)
{
    public TimeSpan EffectiveWhois => Whois ?? TimeSpan.FromSeconds(30);

    public TimeSpan EffectiveBanList => BanList ?? TimeSpan.FromSeconds(30);

    public TimeSpan EffectiveModeration => Moderation ?? TimeSpan.FromSeconds(30);

    public TimeSpan EffectiveInvite => Invite ?? TimeSpan.FromSeconds(30);
}

public sealed record IrcOperationResult(
    Guid Id,
    Guid NetworkId,
    int ConnectionGeneration,
    IrcOperationType Type,
    string TargetConversation,
    string? TargetNickname,
    string? RequestedMode,
    string? RequestedMask,
    DateTimeOffset StartedAt,
    IrcOperationState State,
    bool ServerConfirmed,
    int? Numeric,
    string? Explanation,
    string? ProtocolDetail,
    string? RawServerLine)
{
    public bool IsPending => State == IrcOperationState.Pending;

    public string StateText => State switch
    {
        IrcOperationState.Pending => "pending server confirmation",
        IrcOperationState.Confirmed => "confirmed by server",
        IrcOperationState.Rejected => "rejected by server",
        IrcOperationState.TimedOut => "confirmation not observed before timeout",
        IrcOperationState.Cancelled => "cancelled",
        IrcOperationState.Disconnected => "cancelled by disconnect",
        _ => State.ToString()
    };

    public string DisplayText => string.IsNullOrWhiteSpace(Explanation)
        ? $"{Type} · {TargetConversation} · {StateText}"
        : $"{Explanation}{(string.IsNullOrWhiteSpace(ProtocolDetail) ? string.Empty : $" [{ProtocolDetail}]")} ({StateText})";
}

/// <summary>
/// Small presentation model for the latest bounded operation outcomes. It is
/// deliberately separate from IRC transcript messages so feedback never looks
/// like something another user said.
/// </summary>
public sealed class OperationFeedbackViewModel : ObservableObject
{
    public const int MaximumRetainedResults = 128;

    private readonly object _gate = new();
    private IrcOperationResult? _latest;

    public ObservableCollection<IrcOperationResult> Results { get; } = [];

    public IrcOperationResult? Latest
    {
        get => _latest;
        private set
        {
            if (SetProperty(ref _latest, value))
            {
                OnPropertyChanged(nameof(LatestText));
            }
        }
    }

    public string LatestText => Latest?.DisplayText ?? "No operation feedback yet.";

    public IReadOnlyList<IrcOperationResult> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return Results.ToArray();
            }
        }
    }

    internal void AddOrUpdate(IrcOperationResult result)
    {
        lock (_gate)
        {
            var existing = Results.FirstOrDefault(item => item.Id == result.Id);
            var index = existing is null ? -1 : Results.IndexOf(existing);
            if (index >= 0)
            {
                Results[index] = result;
            }
            else
            {
                Results.Add(result);
            }

            while (Results.Count > MaximumRetainedResults)
            {
                Results.RemoveAt(0);
            }
        }

        Latest = result;
    }

    internal void ClearNetwork(Guid networkId)
    {
        lock (_gate)
        {
            for (var index = Results.Count - 1; index >= 0; index--)
            {
                if (Results[index].NetworkId == networkId)
                {
                    Results.RemoveAt(index);
                }
            }
        }

        Latest = Results.LastOrDefault();
    }
}
