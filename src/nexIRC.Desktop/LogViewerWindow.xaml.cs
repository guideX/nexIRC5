using System.Windows;
using nexIRC.Application;

namespace nexIRC.Desktop;

public sealed record LogResultListItem(ConversationLogSearchResult Result)
{
    public string DisplayText => $"{Result.Record.Timestamp.ToLocalTime():g} · {Result.Record.ConversationName} · {Result.Record.Sender ?? "system"} · {Result.Preview}";
}

public partial class LogViewerWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public LogViewerWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        NetworkBox.ItemsSource = _viewModel.Networks.ToArray();
        NetworkBox.SelectedItem = _viewModel.Sessions.ActiveNetwork;
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
        var network = NetworkBox.SelectedItem as NetworkWorkspace;
        if (network is null || network.ProfileId is not Guid profileId || string.IsNullOrWhiteSpace(ConversationBox.Text))
        {
            StatusText.Text = "Choose a saved network and enter a channel or nickname for paged history.";
            return;
        }

        var kind = ConversationBox.Text.TrimStart().StartsWith('#') ? LogConversationKind.Channel : LogConversationKind.PrivateConversation;
        var records = await _viewModel.LoadHistoryPageAsync(profileId, kind, ConversationBox.Text.Trim());
        var results = records.Select(record => new LogResultListItem(new ConversationLogSearchResult(record, record.Text))).ToArray();
        ResultsList.ItemsSource = results;
        StatusText.Text = $"Loaded {results.Length} older message(s); each page is bounded to {ConfigurationLimits.MaximumHistoryPageSize}.";
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is LogResultListItem item && _viewModel.RouteLogSearchResult(item.Result))
        {
            StatusText.Text = "Conversation activated.";
        }
    }
}
