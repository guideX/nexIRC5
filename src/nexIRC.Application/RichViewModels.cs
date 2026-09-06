using System.Collections.ObjectModel;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum RichResultState
{
    Idle,
    Loading,
    Completed,
    Failed
}

public sealed record ChannelListRow(string Channel, int VisibleUsers, string Topic)
{
    public string UsersText => VisibleUsers > 0 ? VisibleUsers.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";

    public string TopicText => string.IsNullOrWhiteSpace(Topic) ? "(no topic)" : Topic;
}

public sealed class ChannelListResult : ObservableObject
{
    public const int DefaultMaximumRows = 2_000;

    private readonly object _gate = new();
    private readonly int _maximumRows;
    private RichResultState _state;
    private bool _wasTruncated;

    public ChannelListResult(int maximumRows = DefaultMaximumRows)
    {
        if (maximumRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        }

        _maximumRows = maximumRows;
    }

    public ObservableCollection<ChannelListRow> Rows { get; } = [];

    public IReadOnlyList<ChannelListRow> RowsSnapshot
    {
        get
        {
            lock (_gate)
            {
                return Rows.ToArray();
            }
        }
    }

    public int MaximumRows => _maximumRows;

    public RichResultState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsCompleted));
            }
        }
    }

    public string StateText => State switch
    {
        RichResultState.Loading => "Loading channel list…",
        RichResultState.Completed when WasTruncated => $"Complete · first {_maximumRows:N0} rows shown",
        RichResultState.Completed => $"Complete · {Rows.Count:N0} channels",
        _ => "Waiting for channel list…"
    };

    public bool IsLoading => State == RichResultState.Loading;

    public bool IsCompleted => State == RichResultState.Completed;

    public bool WasTruncated
    {
        get => _wasTruncated;
        private set
        {
            if (SetProperty(ref _wasTruncated, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public void Begin()
    {
        lock (_gate)
        {
            Rows.Clear();
        }

        WasTruncated = false;
        State = RichResultState.Loading;
    }

    public void Apply(IrcListItemEvent item, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        if (State == RichResultState.Idle)
        {
            Begin();
        }

        lock (_gate)
        {
            var existing = Rows.FirstOrDefault(row => IrcCaseMappingComparer.Equals(row.Channel, item.Channel, mapping));
            if (existing is not null)
            {
                Rows[Rows.IndexOf(existing)] = new ChannelListRow(item.Channel, item.VisibleUsers, item.Topic);
                return;
            }

            if (Rows.Count >= _maximumRows)
            {
                WasTruncated = true;
                return;
            }

            var row = new ChannelListRow(item.Channel, item.VisibleUsers, item.Topic);
            var insertionIndex = 0;
            while (insertionIndex < Rows.Count
                && StringComparer.Ordinal.Compare(
                    IrcCaseMappingComparer.Fold(Rows[insertionIndex].Channel, mapping),
                    IrcCaseMappingComparer.Fold(row.Channel, mapping)) < 0)
            {
                insertionIndex++;
            }

            Rows.Insert(insertionIndex, row);
        }
    }

    public void Complete() => State = RichResultState.Completed;
}

public sealed class ChannelListView : WorkspaceView
{
    internal ChannelListView(Guid networkId, Guid id, string title)
        : base(networkId, id, WorkspaceViewKind.ChannelList, title)
    {
        Result.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsCompleted));
        };
    }

    public ChannelListResult Result { get; } = new();

    public string StateText => Result.StateText;

    public bool IsLoading => Result.IsLoading;

    public bool IsCompleted => Result.IsCompleted;

    internal void BeginRequest() => Result.Begin();

    internal void Apply(IrcListItemEvent item, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459) => Result.Apply(item, mapping);

    internal void CompleteRequest() => Result.Complete();
}
