using System.Windows;
using Microsoft.Win32;
using nexIRC.Application;

namespace nexIRC.Desktop;

public sealed record LogResultListItem(ConversationLogSearchResult Result)
{
    public string DisplayText => $"{Result.Record.Timestamp.ToLocalTime():g} · {Result.Record.ConversationName} · {Result.Record.Sender ?? "system"} · {Result.Preview}";
}

public partial class LogViewerWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private HistoryPageRequest? _historyRequest;
    private HistoryPage? _historyPage;

    public LogViewerWindow(MainWindowViewModel viewModel, WorkspaceView? initialView = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        NetworkBox.ItemsSource = _viewModel.Networks.ToArray();
        NetworkBox.SelectedItem = _viewModel.Networks.FirstOrDefault(network => network.Id == initialView?.NetworkId)
            ?? _viewModel.Sessions.ActiveNetwork;
        ConversationBox.Text = initialView switch
        {
            ChannelView channel => channel.Channel,
            QueryView query => query.Nickname,
            _ => string.Empty
        };
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        var network = NetworkBox.SelectedItem as NetworkWorkspace;
        var query = new ConversationLogQuery
        {
            Text = QueryBox.Text,
            NetworkId = network?.Id,
            ConversationName = string.IsNullOrWhiteSpace(ConversationBox.Text) ? null : ConversationBox.Text.Trim(),
            MaximumResults = ConfigurationLimits.MaximumSearchResults
        };
        var results = await _viewModel.SearchLogsAsync(query);
        ResultsList.ItemsSource = results.Select(result => new LogResultListItem(result)).ToArray();
        StatusText.Text = $"{results.Count} result(s). Results are bounded to {ConfigurationLimits.MaximumSearchResults}.";
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

        if (!DateTimeOffset.TryParse(JumpDateBox.Text, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var timestamp))
        {
            StatusText.Text = "Enter a valid local date and time.";
            return;
        }

        await LoadHistoryAsync(request with { Around = timestamp, Before = null, After = null, Oldest = false });
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is LogResultListItem item && await _viewModel.RouteLogSearchResultAsync(item.Result))
        {
            StatusText.Text = "Conversation activated with surrounding context.";
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
            From = request.Around is null ? null : request.Around.Value - request.AroundWindow,
            To = request.Around is null ? null : request.Around.Value + request.AroundWindow
        }, dialog.FileName, format);
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
        _historyRequest = request;
        _historyPage = await _viewModel.LoadHistoryWindowAsync(request);
        ResultsList.ItemsSource = _historyPage.Records
            .Select(record => new LogResultListItem(new ConversationLogSearchResult(record, record.Text)))
            .ToArray();
        StatusText.Text = _historyPage.Records.Count == 0
            ? "No history records matched that scope."
            : $"Loaded {_historyPage.Records.Count} record(s). Older: {_historyPage.HasOlder}; newer: {_historyPage.HasNewer}.";
    }

    private bool TryCreateHistoryRequest(out HistoryPageRequest request)
    {
        var network = NetworkBox.SelectedItem as NetworkWorkspace;
        var conversation = ConversationBox.Text.Trim();
        if (network is null || network.ProfileId is not Guid profileId || conversation.Length == 0)
        {
            request = null!;
            return false;
        }

        request = new HistoryPageRequest
        {
            ScopeId = profileId,
            ConversationKind = network.Snapshot.Features.ChannelTypes.Contains(conversation[0]) ? LogConversationKind.Channel : LogConversationKind.PrivateConversation,
            ConversationName = conversation
        };
        return true;
    }

    private static string SafeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Length > 120 ? safe[..120] : safe;
    }
}
