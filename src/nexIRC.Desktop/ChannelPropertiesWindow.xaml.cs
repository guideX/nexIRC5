using System.Windows;
using System.Windows.Controls;
using nexIRC.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace nexIRC.Desktop;

public partial class ChannelPropertiesWindow : Window
{
    private readonly ChannelPropertiesViewModel _viewModel;

    public ChannelPropertiesWindow(ChannelPropertiesViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Channel.PropertyChanged += OnChannelPropertyChanged;
    }

    private void OnChannelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelView.ModeProjections) or nameof(ChannelView.Topic) or nameof(ChannelView.TopicSetter) or nameof(ChannelView.TopicSetAt))
        {
            _viewModel.Refresh();
        }
    }

    private async void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ChannelModeProjection mode } checkBox || mode.Kind != nexIRC.Core.State.IrcChannelModeKind.NoParameter)
        {
            return;
        }

        await ApplyAsync(_viewModel.SetFlagModeAsync(mode.Mode, checkBox.IsChecked == true));
    }

    private async void OnParameterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChannelModeProjection mode })
        {
            return;
        }

        if (mode.IsActive)
        {
            await ApplyAsync(_viewModel.SetParameterizedModeAsync(mode.Mode, false, null));
            return;
        }

        var initial = mode.Mode == 'l' ? "50" : string.Empty;
        var parameter = ParticipantInputWindow.Show(this, $"Set mode +{mode.Mode}", $"Parameter for {mode.DisplayName} (not retained):", initial, selectAll: false);
        if (parameter is not null)
        {
            await ApplyAsync(_viewModel.SetParameterizedModeAsync(mode.Mode, true, parameter));
        }
    }

    private async void OnSaveTopicClick(object sender, RoutedEventArgs e) => await ApplyAsync(_viewModel.SaveTopicAsync());

    private async void OnBanListClick(object sender, RoutedEventArgs e)
    {
        var result = await _viewModel.OpenBanListAsync();
        StatusText.Text = result.Message;
        if (result.View is not null && Owner is MainWindow mainWindow)
        {
            mainWindow.ViewModel.SelectView(result.View);
        }
    }

    private async Task ApplyAsync(ValueTask<CommandDispatchResult> pending)
    {
        try
        {
            var result = await pending;
            StatusText.Text = result.Message;
            _viewModel.Refresh();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Channel.PropertyChanged -= OnChannelPropertyChanged;
        base.OnClosed(e);
    }
}
