using System.Windows;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using MessageBox = System.Windows.MessageBox;

namespace nexIRC.Desktop;

public partial class NewConnectionWindow : Window
{
    public NewConnectionWindow()
    {
        InitializeComponent();
        SaslPolicyBox.ItemsSource = Enum.GetValues<SaslAuthenticationPolicy>();
        SaslPolicyBox.SelectedItem = SaslAuthenticationPolicy.Disabled;
    }

    public NetworkConnectionOptions? Options { get; private set; }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port))
        {
            MessageBox.Show(this, "Port must be a number between 1 and 65535.", "New Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var policy = SaslPolicyBox.SelectedItem is SaslAuthenticationPolicy selected
            ? selected
            : SaslAuthenticationPolicy.Disabled;
        var password = SaslPasswordBox.Password;
        if (policy != SaslAuthenticationPolicy.Disabled && password.Length == 0)
        {
            MessageBox.Show(this, "Enter a SASL password or set the SASL policy to Disabled.", "New Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ISaslCredentialProvider? provider = null;
            if (policy != SaslAuthenticationPolicy.Disabled)
            {
                provider = new MemorySaslCredentialProvider(
                    string.IsNullOrWhiteSpace(SaslUsernameBox.Text) ? NicknameBox.Text.Trim() : SaslUsernameBox.Text.Trim(),
                    password);
            }

            Options = new NetworkConnectionOptions
            {
                DisplayName = DisplayNameBox.Text.Trim(),
                Endpoint = new IrcEndpoint(HostBox.Text.Trim(), port, TlsBox.IsChecked == true),
                Nickname = NicknameBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                RealName = RealNameBox.Text.Trim(),
                DesiredChannels = ChannelsBox.Text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
                SaslPolicy = policy,
                SaslCredentialProvider = provider
            };
            SaslPasswordBox.Clear();
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "New Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
