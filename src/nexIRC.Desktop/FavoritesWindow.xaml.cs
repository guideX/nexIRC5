using System.Windows;
using nexIRC.Application;
using nexIRC.Core.State;

namespace nexIRC.Desktop;

public sealed record DestinationListItem(
    Guid FavoriteId,
    Guid NetworkId,
    DestinationKind Kind,
    string Name,
    string NetworkName,
    string? Label,
    DateTimeOffset? LastOpened = null,
    Guid GroupId = default,
    string? GroupName = null,
    string? LifecycleText = null)
{
    public string DisplayText => LastOpened is null
        ? $"{GroupName ?? "General"} · {NetworkName} · {Name}{(string.IsNullOrWhiteSpace(Label) ? string.Empty : $" — {Label}")} [{LifecycleText ?? "not open"}]"
        : $"{NetworkName} · {Name} [{LifecycleText ?? "not open"}] ({LastOpened.Value.ToLocalTime():g})";
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
        var groups = _viewModel.Sessions.FavoriteGroups().ToArray();
        GroupBox.ItemsSource = groups;
        if (GroupBox.SelectedItem is null) GroupBox.SelectedItem = groups.FirstOrDefault();
        var favorites = _viewModel.Networks.SelectMany(network => _viewModel.Sessions.Favorites(network.Id)
                .Select(item => new DestinationListItem(item.Id, network.Id, item.Kind, item.Name, network.DisplayName, item.Label, null,
                    item.GroupId, groups.FirstOrDefault(group => group.Id == item.GroupId)?.Name,
                    FindLifecycle(network, item.Kind, item.Name))))
            .ToArray();
        var recents = _viewModel.Networks.SelectMany(network => _viewModel.Sessions.RecentDestinations(network.Id)
                .Select(item => new DestinationListItem(Guid.Empty, network.Id, item.Kind, item.Name, network.DisplayName, null, item.LastOpened,
                    LifecycleText: FindLifecycle(network, item.Kind, item.Name))))
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

    private async void OnJoinClick(object sender, RoutedEventArgs e)
    {
        var item = FavoritesList.SelectedItem as DestinationListItem ?? RecentsList.SelectedItem as DestinationListItem;
        if (item is null || item.Kind != DestinationKind.Channel)
        {
            return;
        }

        await _viewModel.OpenDestinationAsync(item.NetworkId, item.Kind, item.Name, joinIfNeeded: true);
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

    private void OnRemoveRecentClick(object sender, RoutedEventArgs e)
    {
        if (RecentsList.SelectedItem is DestinationListItem item)
        {
            _viewModel.Sessions.RemoveRecent(item.NetworkId, new RecentDestination { Kind = item.Kind, Name = item.Name });
            Refresh();
        }
    }

    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is DestinationListItem item
            && item.FavoriteId != Guid.Empty
            && GroupBox.SelectedItem is FavoriteGroup group)
        {
            _viewModel.MoveFavorite(item.FavoriteId, group.Id);
            Refresh();
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is DestinationListItem item && item.FavoriteId != Guid.Empty)
        {
            _viewModel.ReorderFavorite(item.FavoriteId, -1);
            Refresh();
        }
    }

    private void OnMoveDownClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is DestinationListItem item && item.FavoriteId != Guid.Empty)
        {
            _viewModel.ReorderFavorite(item.FavoriteId, 1);
            Refresh();
        }
    }

    private async void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Configuration is null || !_viewModel.Configuration.AddFavoriteGroup(NewGroupBox.Text))
        {
            return;
        }

        NewGroupBox.Clear();
        if (_viewModel.Configuration is not null) await _viewModel.Configuration.SaveAsync();
        Refresh();
    }

    private void OnClearChannelsClick(object sender, RoutedEventArgs e) => ClearRecent(DestinationKind.Channel);

    private void OnClearQueriesClick(object sender, RoutedEventArgs e) => ClearRecent(DestinationKind.Query);

    private void ClearRecent(DestinationKind kind)
    {
        if (_viewModel.Sessions.ActiveNetwork is { } network)
        {
            _viewModel.Sessions.ClearRecent(network.Id, kind);
            Refresh();
        }
    }

    private static string? FindLifecycle(NetworkWorkspace network, DestinationKind kind, string name) =>
        network.Channels.Cast<WorkspaceView>()
            .Concat(network.Queries)
            .Where(view =>
            view.Kind == (kind == DestinationKind.Channel ? WorkspaceViewKind.Channel : WorkspaceViewKind.Query)
            && IrcCaseMappingComparer.Equals(view.Title, name, network.Snapshot.Features.CaseMapping))
            .Select(view => view.IsViewOpen ? view.LifecycleText : $"{view.LifecycleText}, closed view")
            .FirstOrDefault();
}
