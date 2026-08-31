using System.Windows;
using nexIRC.Application;
using MessageBox = System.Windows.MessageBox;

namespace nexIRC.Desktop;

public partial class NetworkProfilesWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private Guid _selectedId;

    public NetworkProfilesWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        RefreshProfiles();
    }

    private void RefreshProfiles(Guid? selectId = null)
    {
        ProfilesList.ItemsSource = _viewModel.Configuration?.Profiles.Profiles.ToArray() ?? Array.Empty<NetworkProfile>();
        var selected = selectId is Guid id
            ? ProfilesList.Items.Cast<NetworkProfile>().FirstOrDefault(profile => profile.Id == id)
            : ProfilesList.Items.Cast<NetworkProfile>().FirstOrDefault();
        if (selected is not null)
        {
            ProfilesList.SelectedItem = selected;
        }
        else
        {
            ClearForm();
        }
    }

    private void OnProfileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is NetworkProfile profile)
        {
            _selectedId = profile.Id;
            DisplayNameBox.Text = profile.DisplayName;
            HostBox.Text = profile.Host;
            PortBox.Text = profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TlsBox.IsChecked = profile.UseTls;
            NicknameBox.Text = profile.Nickname;
            AlternateNicknamesBox.Text = string.Join(' ', profile.AlternateNicknames);
            UsernameBox.Text = profile.Username;
            RealNameBox.Text = profile.RealName;
            ChannelsBox.Text = string.Join(Environment.NewLine, profile.AutoJoinChannels);
            AutoConnectBox.IsChecked = profile.AutoConnect;
            ReconnectBox.IsChecked = profile.ReconnectEnabled;
        }
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _selectedId = Guid.NewGuid();
        ClearForm();
        DisplayNameBox.Focus();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var profile = ReadProfile();
        if (profile is null)
        {
            return;
        }

        try
        {
            await _viewModel.SaveProfileAsync(profile);
            RefreshProfiles(profile.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Saved Network Profiles", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedId == Guid.Empty)
        {
            return;
        }

        var answer = MessageBox.Show(this, "Delete this saved profile? Any open session for it will also be removed.", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.DeleteProfileAsync(_selectedId);
        _selectedId = Guid.Empty;
        RefreshProfiles();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var profile = ReadProfile();
        if (profile is null)
        {
            return;
        }

        await _viewModel.SaveProfileAsync(profile);
        await _viewModel.ConnectProfileAsync(profile.Id);
    }

    private NetworkProfile? ReadProfile()
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be a number between 1 and 65535.", "Saved Network Profiles", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var profile = ConfigurationValidator.NormalizeProfile(new NetworkProfile
        {
            Id = _selectedId == Guid.Empty ? Guid.NewGuid() : _selectedId,
            DisplayName = DisplayNameBox.Text,
            Host = HostBox.Text,
            Port = port,
            UseTls = TlsBox.IsChecked == true,
            Nickname = NicknameBox.Text,
            AlternateNicknames = SplitValues(AlternateNicknamesBox.Text),
            Username = UsernameBox.Text,
            RealName = RealNameBox.Text,
            AutoJoinChannels = SplitValues(ChannelsBox.Text),
            AutoConnect = AutoConnectBox.IsChecked == true,
            ReconnectEnabled = ReconnectBox.IsChecked == true
        });
        if (profile is null)
        {
            MessageBox.Show(this, "Enter a valid hostname and nickname.", "Saved Network Profiles", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return profile;
    }

    private void ClearForm()
    {
        DisplayNameBox.Text = "New network";
        HostBox.Text = "irc.libera.chat";
        PortBox.Text = "6697";
        TlsBox.IsChecked = true;
        NicknameBox.Text = "nexIRC5";
        AlternateNicknamesBox.Clear();
        UsernameBox.Text = "nexirc";
        RealNameBox.Text = "nexIRC 5";
        ChannelsBox.Clear();
        AutoConnectBox.IsChecked = false;
        ReconnectBox.IsChecked = true;
    }

    private static List<string> SplitValues(string value) => value
        .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}
