using System.Globalization;
using System.Windows;
using nexIRC.Application;
using nexIRC.Core.State;

namespace nexIRC.Desktop;

public sealed record LogResultListItem(ConversationLogSearchResult Result, string? NetworkDisplayName = null)
{
    public string DisplayText => $"{Result.Timestamp.ToLocalTime():g} · {NetworkDisplayName ?? Result.NetworkId.ToString("N")[..8]} · {Result.ConversationName} · {Result.Sender ?? "system"} · {Result.Preview}";
}

public sealed record LogSearchScopeOption(ConversationLogSearchScope Scope, string DisplayName);

public sealed record LogNetworkFilterOption(NetworkWorkspace? Network, string DisplayName);

public sealed record LogConversationKindOption(LogConversationKind? Kind, string DisplayName);

public sealed record LogMessageKindOption(LogMessageKind? Kind, string DisplayName);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001", Justification = "The window owns and disposes its cancellation source when it closes.")]
public partial class LogViewerWindow : Window
{
    private static readonly LogSearchScopeOption[] ScopeOptions =
    [
        new(ConversationLogSearchScope.CurrentConversation, "Current conversation"),
        new(ConversationLogSearchScope.CurrentNetwork, "Current network"),
        new(ConversationLogSearchScope.AllHistory, "All history")
    ];

    private readonly MainWindowViewModel _viewModel;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _searchCancellation;
    private HistoryPageRequest? _historyRequest;
    private HistoryPage? _historyPage;

    public LogViewerWindow(MainWindowViewModel viewModel, WorkspaceView? initialView = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ScopeBox.ItemsSource = ScopeOptions;
        ScopeBox.SelectedItem = initialView is ChannelView or QueryView ? ScopeOptions[0] : ScopeOptions[2];

        var initialNetwork = _viewModel.Networks.FirstOrDefault(network => network.Id == initialView?.NetworkId)
            ?? _viewModel.Sessions.ActiveNetwork;
        var networkOptions = new[] { new LogNetworkFilterOption(null, "All networks") }
            .Concat(_viewModel.Networks.Select(network => new LogNetworkFilterOption(network, network.DisplayName)))
            .ToArray();
        NetworkBox.ItemsSource = networkOptions;
        NetworkBox.SelectedItem = networkOptions.FirstOrDefault(item => item.Network?.Id == initialNetwork?.Id) ?? networkOptions[0];

        ConversationKindBox.ItemsSource = new[]
        {
            new LogConversationKindOption(null, "All conversations"),
            new LogConversationKindOption(LogConversationKind.Channel, "Channels"),
            new LogConversationKindOption(LogConversationKind.PrivateConversation, "Private"),
            new LogConversationKindOption(LogConversationKind.Status, "Status")
        };
        ConversationKindBox.SelectedIndex = 0;

        MessageKindBox.ItemsSource = new[] { new LogMessageKindOption(null, "All event kinds") }
            .Concat(Enum.GetValues<LogMessageKind>().Select(kind => new LogMessageKindOption(kind, kind.ToString())))
            .ToArray();
        MessageKindBox.SelectedIndex = 0;

        ConversationBox.Text = initialView switch
        {
            ChannelView channel => channel.Channel,
            QueryView query => query.Nickname,
            _ => string.Empty
        };
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (!TryCreateSearchQuery(out var query))
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var previous = Interlocked.Exchange(ref _searchCancellation, cancellation);
        previous?.Cancel();
        CancelSearchButton.IsEnabled = true;
        StatusText.Text = "Searching history…";
        try
        {
            var page = await _viewModel.SearchLogsDetailedAsync(query, cancellation.Token);
            if (!ReferenceEquals(_searchCancellation, cancellation))
            {
                return;
            }

            ResultsList.ItemsSource = page.Results
                .Select(result => new LogResultListItem(result, _viewModel.Sessions.Networks.FirstOrDefault(network => network.Id == result.NetworkId)?.DisplayName))
                .ToArray();
            ResultsList.SelectedIndex = page.Results.Count > 0 ? 0 : -1;
            var statistics = page.Statistics;
            var limited = statistics.ResultsTruncated ? " Result limit reached." : string.Empty;
            var fileLimit = statistics.FilesTruncated ? " File limit reached." : string.Empty;
            var index = statistics.IndexFilesUsed > 0
                ? $" Used {statistics.IndexFilesUsed} search index(es); skipped {statistics.RecordsSkippedByIndex:N0} record(s)."
                : string.Empty;
            var indexBuild = statistics.IndexFilesBuilt > 0
                ? $" Built {statistics.IndexFilesBuilt} disposable index(es) in {statistics.IndexBuildMilliseconds:N0} ms."
                : string.Empty;
            StatusText.Text = $"{statistics.ResultsProduced} result(s) from {statistics.MatchingRecords} match(es); examined {statistics.RecordsExamined:N0} record(s) in {statistics.FilesExamined:N0} file(s).{index}{indexBuild}{limited}{fileLimit}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                StatusText.Text = "Search cancelled.";
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                StatusText.Text = $"History search failed safely: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCancellation, null, cancellation), cancellation))
            {
                CancelSearchButton.IsEnabled = false;
            }

            cancellation.Dispose();
        }
    }

    private void OnCancelSearchClick(object sender, RoutedEventArgs e) => _searchCancellation?.Cancel();

    private void OnPreviousResultClick(object sender, RoutedEventArgs e) => MoveResultSelection(-1);

    private void OnNextResultClick(object sender, RoutedEventArgs e) => MoveResultSelection(1);

    private void MoveResultSelection(int direction)
    {
        if (ResultsList.Items.Count == 0)
        {
            return;
        }

        var selected = ResultsList.SelectedIndex < 0 ? 0 : ResultsList.SelectedIndex + direction;
        ResultsList.SelectedIndex = Math.Clamp(selected, 0, ResultsList.Items.Count - 1);
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private async void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (!TryCreateHistoryRequest(out var request))
        {
            StatusText.Text = "Choose a saved network and enter a channel or nickname for paged history.";
            return;
        }

        await LoadHistoryAsync(request);
    }

    private async void OnNewestClick(object sender, RoutedEventArgs e)
    {
        if (TryCreateHistoryRequest(out var request)) await LoadHistoryAsync(request);
    }

    private async void OnOldestClick(object sender, RoutedEventArgs e)
    {
        if (TryCreateHistoryRequest(out var request)) await LoadHistoryAsync(request with { Oldest = true });
    }

    private async void OnOlderClick(object sender, RoutedEventArgs e)
    {
        if (_historyRequest is null || _historyPage is null || !_historyPage.HasOlder || _historyPage.OldestTimestamp is not DateTimeOffset oldest)
        {
            StatusText.Text = "There is no older history page.";
            return;
        }

        await LoadHistoryAsync(_historyRequest with { Before = oldest, After = null, Oldest = false, Around = null });
    }

    private async void OnNewerClick(object sender, RoutedEventArgs e)
    {
        if (_historyRequest is null || _historyPage is null || !_historyPage.HasNewer || _historyPage.NewestTimestamp is not DateTimeOffset newest)
        {
            StatusText.Text = "There is no newer history page.";
            return;
        }

        await LoadHistoryAsync(_historyRequest with { After = newest, Before = null, Oldest = false, Around = null });
    }

    private async void OnJumpDateClick(object sender, RoutedEventArgs e)
    {
        if (!TryCreateHistoryRequest(out var request))
        {
            StatusText.Text = "Choose a saved network and enter a channel or nickname first.";
            return;
        }

        if (!TryParseDate(JumpDateBox.Text, out var timestamp))
        {
            StatusText.Text = "Enter a valid local date and time.";
            return;
        }

        await LoadHistoryAsync(request with { Around = timestamp, Before = null, After = null, Oldest = false });
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not LogResultListItem item)
        {
            return;
        }

        try
        {
            if (await _viewModel.RouteLogSearchResultAsync(item.Result, _lifetimeCancellation.Token))
            {
                StatusText.Text = "Conversation activated with surrounding context.";
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not open history result: {exception.Message}";
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (!TryCreateHistoryRequest(out var request))
        {
            StatusText.Text = "Choose a saved network and enter a conversation before exporting.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Plain text (*.txt)|*.txt|JSONL (*.jsonl)|*.jsonl",
            FileName = SafeFileName($"{request.ConversationName}-history.txt")
        };
        if (dialog.ShowDialog(this) != true) return;
        var format = dialog.FilterIndex == 2 ? HistoryExportFormat.Jsonl : HistoryExportFormat.PlainText;
        var result = await _viewModel.ExportHistoryAsync(new HistoryExportRequest
        {
            ScopeId = request.ScopeId,
            ConversationKind = request.ConversationKind,
            ConversationName = request.ConversationName,
            ConversationKey = request.ConversationKey,
            From = request.Around is null ? null : request.Around.Value - request.AroundWindow,
            To = request.Around is null ? null : request.Around.Value + request.AroundWindow
        }, dialog.FileName, format, _lifetimeCancellation.Token);
        StatusText.Text = result.IsTruncated ? "History export reached the configured bound." : $"Exported {result.Records.Count} record(s).";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is LogResultListItem item)
        {
            System.Windows.Clipboard.SetText(item.DisplayText);
            StatusText.Text = "Selected history result copied.";
        }
    }

    private async Task LoadHistoryAsync(HistoryPageRequest request)
    {
        try
        {
            _historyRequest = request;
            _historyPage = await _viewModel.LoadHistoryWindowAsync(request, _lifetimeCancellation.Token);
            ResultsList.ItemsSource = _historyPage.Records
                .Select(record => new LogResultListItem(new ConversationLogSearchResult(record, record.Text)))
                .ToArray();
            StatusText.Text = _historyPage.Records.Count == 0
                ? "No history records matched that scope."
                : $"Loaded {_historyPage.Records.Count} record(s). Older: {_historyPage.HasOlder}; newer: {_historyPage.HasNewer}.";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"History read failed safely: {exception.Message}";
        }
    }

    private bool TryCreateSearchQuery(out ConversationLogQuery query)
    {
        var scope = (ScopeBox.SelectedItem as LogSearchScopeOption)?.Scope ?? ConversationLogSearchScope.AllHistory;
        var network = (NetworkBox.SelectedItem as LogNetworkFilterOption)?.Network;
        var conversation = string.IsNullOrWhiteSpace(ConversationBox.Text) ? null : ConversationBox.Text.Trim();
        if (scope is ConversationLogSearchScope.CurrentConversation or ConversationLogSearchScope.CurrentNetwork && network is null)
        {
            StatusText.Text = "Select a network for the current conversation or current network scope.";
            query = null!;
            return false;
        }

        if (scope == ConversationLogSearchScope.CurrentConversation && conversation is null)
        {
            StatusText.Text = "Enter a channel or nickname for the current conversation scope.";
            query = null!;
            return false;
        }

        if (!TryParseOptionalDate(FromDateBox.Text, out var from) || !TryParseOptionalDate(ToDateBox.Text, out var to))
        {
            StatusText.Text = "Enter valid local from/to dates or leave them blank.";
            query = null!;
            return false;
        }

        if (from is not null && to is not null && from > to)
        {
            StatusText.Text = "The from date must not be later than the to date.";
            query = null!;
            return false;
        }

        var kind = (ConversationKindBox.SelectedItem as LogConversationKindOption)?.Kind;
        if (kind is null && scope == ConversationLogSearchScope.CurrentConversation && network is not null && conversation is not null && conversation.Length > 0)
        {
            kind = network.Snapshot.Features.ChannelTypes.Contains(conversation[0])
                ? LogConversationKind.Channel
                : LogConversationKind.PrivateConversation;
        }

        query = new ConversationLogQuery
        {
            Scope = scope,
            HistoryScopeId = network is null ? null : network.ProfileId ?? network.Id,
            Text = QueryBox.Text,
            NetworkId = network?.Id,
            ConversationKind = kind,
            ConversationName = conversation,
            ConversationKey = kind == LogConversationKind.PrivateConversation
                ? network?.Queries.FirstOrDefault(query => IrcCaseMappingComparer.Equals(query.Nickname, conversation, network.Snapshot.Features.CaseMapping))?.HistoryConversationKey
                : null,
            Sender = string.IsNullOrWhiteSpace(SenderBox.Text) ? null : SenderBox.Text.Trim(),
            MessageKind = (MessageKindBox.SelectedItem as LogMessageKindOption)?.Kind,
            From = from,
            To = to,
            MaximumResults = ConfigurationLimits.MaximumSearchResults
        };
        return true;
    }

    private bool TryCreateHistoryRequest(out HistoryPageRequest request)
    {
        var network = (NetworkBox.SelectedItem as LogNetworkFilterOption)?.Network;
        var conversation = ConversationBox.Text.Trim();
        if (network is null || conversation.Length == 0)
        {
            request = null!;
            return false;
        }

        request = new HistoryPageRequest
        {
            ScopeId = network.ProfileId ?? network.Id,
            ConversationKind = network.Snapshot.Features.ChannelTypes.Contains(conversation[0]) ? LogConversationKind.Channel : LogConversationKind.PrivateConversation,
            ConversationName = conversation,
            ConversationKey = network.Queries.FirstOrDefault(query =>
                    IrcCaseMappingComparer.Equals(query.Nickname, conversation, network.Snapshot.Features.CaseMapping))?.HistoryConversationKey
        };
        return true;
    }

    private static bool TryParseOptionalDate(string value, out DateTimeOffset? timestamp)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            timestamp = null;
            return true;
        }

        if (TryParseDate(value, out var parsed))
        {
            timestamp = parsed;
            return true;
        }

        timestamp = null;
        return false;
    }

    private static bool TryParseDate(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out timestamp);

    protected override void OnClosed(EventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _searchCancellation?.Cancel();
        _lifetimeCancellation.Dispose();
        base.OnClosed(e);
    }

    private static string SafeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Length > 120 ? safe[..120] : safe;
    }
}
