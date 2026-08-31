using System.Windows;
using nexIRC.Application;

namespace nexIRC.Desktop;

public sealed record DestinationListItem(
    Guid FavoriteId,
    Guid NetworkId,
    DestinationKind Kind,
    string Name,
    string NetworkName,
    string? Label,
    DateTimeOffset? LastOpened = null)
{
    public string DisplayText => LastOpened is null
        ? $"{NetworkName} · {Name}{(string.IsNullOrWhiteSpace(Label) ? string.Empty : $" — {Label}")}"
        : $"{NetworkName} · {Name} ({LastOpened.Value.ToLocalTime():g})";
}

public partial class FavoritesWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public FavoritesWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var favorites = _viewModel.Networks.SelectMany(network => _viewModel.Sessions.Favorites(network.Id)
                .Select(item => new DestinationListItem(item.Id, network.Id, item.Kind, item.Name, network.DisplayName, item.Label)))
            .ToArray();
        var recents = _viewModel.Networks.SelectMany(network => _viewModel.Sessions.RecentDestinations(network.Id)
                .Select(item => new DestinationListItem(Guid.Empty, network.Id, item.Kind, item.Name, network.DisplayName, null, item.LastOpened)))
            .OrderByDescending(item => item.LastOpened)
            .ToArray();
        FavoritesList.ItemsSource = favorites;
        RecentsList.ItemsSource = recents;
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var item = FavoritesList.SelectedItem as DestinationListItem ?? RecentsList.SelectedItem as DestinationListItem;
        if (item is null) return;
        await _viewModel.OpenDestinationAsync(item.NetworkId, item.Kind, item.Name);
        Close();
    }

    private async void OnAddCurrentClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.AddCurrentFavoriteAsync();
        Refresh();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is DestinationListItem item && item.FavoriteId != Guid.Empty)
        {
            _viewModel.RemoveFavorite(item.FavoriteId);
            Refresh();
        }
    }

    private void OnClearRecentsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Sessions.ActiveNetwork is { } network)
        {
            _viewModel.Sessions.ClearRecent(network.Id);
            Refresh();
        }
    }
}
