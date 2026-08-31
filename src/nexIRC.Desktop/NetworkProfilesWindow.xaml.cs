using System.Windows;
using Microsoft.Win32;
using nexIRC.Application;
using nexIRC.Core.Session;
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
        SaslPolicyBox.ItemsSource = Enum.GetValues<SaslAuthenticationPolicy>();
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

    private async void OnProfileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
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
            SaslPolicyBox.SelectedItem = profile.SaslPolicy;
            SaslUsernameBox.Text = profile.SaslUsername ?? string.Empty;
            SaslPasswordBox.Clear();
            ClearCredentialBox.IsChecked = false;
            ServerPasswordEnabledBox.IsChecked = profile.ServerPasswordEnabled;
            ServerPasswordBox.Clear();
            ClearServerPasswordBox.IsChecked = false;
            ChannelsBox.Text = string.Join(Environment.NewLine, profile.AutoJoinChannels);
            AutoConnectBox.IsChecked = profile.AutoConnect;
            ReconnectBox.IsChecked = profile.ReconnectEnabled;
            await RefreshCredentialStateAsync(profile.Id);
            await RefreshServerPasswordStateAsync(profile.Id);
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
            await _viewModel.SaveProfileAsync(
                profile,
                SaslPasswordBox.Password,
                ClearCredentialBox.IsChecked == true,
                ServerPasswordBox.Password,
                ClearServerPasswordBox.IsChecked == true,
                updateServerPassword: true);
            SaslPasswordBox.Clear();
            ClearCredentialBox.IsChecked = false;
            ServerPasswordBox.Clear();
            ClearServerPasswordBox.IsChecked = false;
            await RefreshCredentialStateAsync(profile.Id);
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

        await _viewModel.SaveProfileAsync(
            profile,
            SaslPasswordBox.Password,
            ClearCredentialBox.IsChecked == true,
            ServerPasswordBox.Password,
            ClearServerPasswordBox.IsChecked == true,
            updateServerPassword: true);
        SaslPasswordBox.Clear();
        ClearCredentialBox.IsChecked = false;
        ServerPasswordBox.Clear();
        ClearServerPasswordBox.IsChecked = false;
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
            SaslPolicy = SaslPolicyBox.SelectedItem is SaslAuthenticationPolicy policy ? policy : SaslAuthenticationPolicy.Disabled,
            SaslUsername = string.IsNullOrWhiteSpace(SaslUsernameBox.Text) ? null : SaslUsernameBox.Text,
            ServerPasswordEnabled = ServerPasswordEnabledBox.IsChecked == true,
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
        SaslPolicyBox.SelectedItem = SaslAuthenticationPolicy.Disabled;
        SaslUsernameBox.Clear();
        SaslPasswordBox.Clear();
        ClearCredentialBox.IsChecked = false;
        CredentialStateText.Text = "No credential stored.";
        ServerPasswordEnabledBox.IsChecked = false;
        ServerPasswordBox.Clear();
        ClearServerPasswordBox.IsChecked = false;
        ServerPasswordStateText.Text = "No server password stored.";
        ChannelsBox.Clear();
        AutoConnectBox.IsChecked = false;
        ReconnectBox.IsChecked = true;
    }

    private static List<string> SplitValues(string value) => value
        .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private async Task RefreshCredentialStateAsync(Guid profileId)
    {
        try
        {
            var result = await _viewModel.LoadCredentialStateAsync(profileId);
            result.Credential?.Dispose();
            CredentialStateText.Text = result.Status switch
            {
                CredentialStoreStatus.Stored => "A SASL credential is stored in Windows Credential Manager. Its value is never displayed.",
                CredentialStoreStatus.Missing => "No SASL credential is stored.",
                _ => result.Diagnostic ?? "Secure credential status is unavailable; entering a password is session-only until storage succeeds."
            };
        }
        catch (Exception exception)
        {
            CredentialStateText.Text = $"Secure credential status unavailable: {exception.Message}";
        }
    }

    private async Task RefreshServerPasswordStateAsync(Guid profileId)
    {
        try
        {
            var result = await _viewModel.LoadServerPasswordStateAsync(profileId);
            result.Credential?.Dispose();
            ServerPasswordStateText.Text = result.Status switch
            {
                CredentialStoreStatus.Stored => "A server password is stored in the secure credential vault. Its value is never displayed.",
                CredentialStoreStatus.Missing => "No server password is stored.",
                _ => result.Diagnostic ?? "Secure server-password status is unavailable; entering a password is session-only until storage succeeds."
            };
        }
        catch (Exception exception)
        {
            ServerPasswordStateText.Text = $"Secure server-password status unavailable: {exception.Message}";
        }
    }

    private async void OnExportSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ProfilePortability is null) return;
        if (_selectedId == Guid.Empty)
        {
            MessageBox.Show(this, "Select a saved profile first.", "Profile export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "nexIRC profile export (*.json)|*.json", FileName = "nexirc-profiles.json" };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ExportProfilesAsync(dialog.FileName, [_selectedId]);
            MessageBox.Show(this, "Profiles exported without credentials.", "Profile export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnExportAllClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ProfilePortability is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "nexIRC profile export (*.json)|*.json", FileName = "nexirc-profiles.json" };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ExportProfilesAsync(dialog.FileName);
            MessageBox.Show(this, "Profiles exported without credentials.", "Profile export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ProfilePortability is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "nexIRC profile export (*.json)|*.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var result = await _viewModel.ImportProfilesAsync(dialog.FileName);
            RefreshProfiles();
            MessageBox.Show(this, $"Imported {result.ImportedProfiles.Count} profile(s); skipped {result.SkippedProfiles}.", "Profile import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Profile import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
