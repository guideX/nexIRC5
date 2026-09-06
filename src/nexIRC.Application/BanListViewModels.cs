using System.Collections.ObjectModel;
using nexIRC.Core.Session;

namespace nexIRC.Application;

public sealed record BanListEntryView(
    Guid NetworkId,
    string Channel,
    string Mask,
    string? SetBy,
    DateTimeOffset? SetAt)
{
    public string SetByText => string.IsNullOrWhiteSpace(SetBy) ? "—" : SetBy!;

    public string SetAtText => SetAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
}

public sealed class BanListResult : ObservableObject
{
    public const int DefaultMaximumEntries = 512;

    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private RichResultState _state;
    private bool _wasTruncated;
    private string _filterText = string.Empty;
    private string _newMask = string.Empty;
    private BanListEntryView? _selectedEntry;
    private string? _errorText;

    public BanListResult(int maximumEntries = DefaultMaximumEntries)
    {
        if (maximumEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _maximumEntries = maximumEntries;
    }

    public ObservableCollection<BanListEntryView> Entries { get; } = [];

    public IReadOnlyList<BanListEntryView> EntriesSnapshot
    {
        get
        {
            lock (_gate)
            {
                return Entries.ToArray();
            }
        }
    }

    public IEnumerable<BanListEntryView> VisibleEntries => string.IsNullOrWhiteSpace(FilterText)
        ? Entries
        : Entries.Where(item => item.Mask.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    public int MaximumEntries => _maximumEntries;

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
        RichResultState.Loading => "Loading ban list…",
        RichResultState.Completed when WasTruncated => $"Complete · first {_maximumEntries:N0} entries shown",
        RichResultState.Completed => $"Complete · {Entries.Count:N0} ban(s)",
        RichResultState.Failed => string.IsNullOrWhiteSpace(ErrorText) ? "Ban list failed" : $"Ban list failed: {ErrorText}",
        _ => "Ban list not requested"
    };

    public bool IsLoading => State == RichResultState.Loading;

    public bool IsCompleted => State == RichResultState.Completed;

    public bool WasTruncated
    {
        get => _wasTruncated;
        private set
        {
            if (SetProperty(ref _wasTruncated, value)) OnPropertyChanged(nameof(StateText));
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty)) OnPropertyChanged(nameof(VisibleEntries));
        }
    }

    public string NewMask
    {
        get => _newMask;
        set => SetProperty(ref _newMask, value ?? string.Empty);
    }

    public BanListEntryView? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(StateText));
        }
    }

    public void Begin()
    {
        lock (_gate) Entries.Clear();
        SelectedEntry = null;
        ErrorText = null;
        WasTruncated = false;
        State = RichResultState.Loading;
        OnPropertyChanged(nameof(VisibleEntries));
    }

    public void Apply(IrcBanListItemEvent item, Guid networkId)
    {
        if (State == RichResultState.Idle) Begin();

        var entry = new BanListEntryView(networkId, item.Entry.Channel, item.Entry.Mask, item.Entry.Setter, item.Entry.SetAt);
        lock (_gate)
        {
            var existing = Entries.FirstOrDefault(candidate => string.Equals(candidate.Mask, entry.Mask, StringComparison.Ordinal));
            if (existing is not null)
            {
                Entries[Entries.IndexOf(existing)] = entry;
            }
            else if (Entries.Count >= _maximumEntries)
            {
                WasTruncated = true;
            }
            else
            {
                Entries.Add(entry);
            }
        }

        OnPropertyChanged(nameof(VisibleEntries));
    }

    public void Complete() => State = RichResultState.Completed;

    public void Fail(string message)
    {
        ErrorText = string.IsNullOrWhiteSpace(message) ? "The ban list request did not complete." : message;
        State = RichResultState.Failed;
    }
}

public sealed class BanListView : WorkspaceView
{
    internal BanListView(Guid networkId, Guid id, string channel)
        : base(networkId, id, WorkspaceViewKind.BanList, $"Ban List {channel}")
    {
        Channel = channel;
        Result.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsCompleted));
        };
    }

    public string Channel { get; }

    public BanListResult Result { get; } = new();

    public string StateText => Result.StateText;

    public bool IsLoading => Result.IsLoading;

    public bool IsCompleted => Result.IsCompleted;

    internal void BeginRequest() => Result.Begin();

    internal void Apply(IrcBanListItemEvent item) => Result.Apply(item, NetworkId);

    internal void CompleteRequest() => Result.Complete();

    internal void Fail(string message) => Result.Fail(message);
}
