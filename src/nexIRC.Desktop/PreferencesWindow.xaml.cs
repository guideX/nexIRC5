using System.Windows;
using nexIRC.Application;

namespace nexIRC.Desktop;

public partial class PreferencesWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public PreferencesWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        var preferences = viewModel.CurrentPreferences;
        HighlightNicknameBox.IsChecked = preferences.HighlightNickname;
        HighlightCustomWordsBox.IsChecked = preferences.HighlightCustomWords;
        CustomWordsBox.Text = string.Join(Environment.NewLine, preferences.CustomHighlightWords);
        NotificationsBox.IsChecked = preferences.NotificationsEnabled;
        HighlightNotificationsBox.IsChecked = preferences.HighlightNotifications;
        PrivateMessageNotificationsBox.IsChecked = preferences.PrivateMessageNotifications;
        ConnectionNotificationsBox.IsChecked = preferences.ConnectionNotifications;
        ConversationLoggingBox.IsChecked = preferences.ConversationLoggingEnabled;
        PrivateLoggingBox.IsChecked = preferences.PrivateMessageLoggingEnabled;
        StatusLoggingBox.IsChecked = preferences.StatusLoggingEnabled;
        RetentionDaysBox.Text = preferences.LogRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var preferences = _viewModel.CurrentPreferences with
        {
            HighlightNickname = HighlightNicknameBox.IsChecked == true,
            HighlightCustomWords = HighlightCustomWordsBox.IsChecked == true,
            CustomHighlightWords = CustomWordsBox.Text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            NotificationsEnabled = NotificationsBox.IsChecked == true,
            HighlightNotifications = HighlightNotificationsBox.IsChecked == true,
            PrivateMessageNotifications = PrivateMessageNotificationsBox.IsChecked == true,
            ConnectionNotifications = ConnectionNotificationsBox.IsChecked == true,
            ConversationLoggingEnabled = ConversationLoggingBox.IsChecked == true,
            PrivateMessageLoggingEnabled = PrivateLoggingBox.IsChecked == true,
            StatusLoggingEnabled = StatusLoggingBox.IsChecked == true,
            LogRetentionDays = int.TryParse(RetentionDaysBox.Text, out var retention) ? retention : 30
        };
        await _viewModel.ApplyPreferencesAsync(preferences);
        DialogResult = true;
    }
}
