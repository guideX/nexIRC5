using System.Windows;
using nexIRC.Application;

namespace nexIRC.Desktop;

public partial class TopicEditorWindow : Window
{
    private readonly ChannelPropertiesViewModel _viewModel;

    public TopicEditorWindow(ChannelPropertiesViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _viewModel.SaveTopicAsync();
            StatusText.Text = result.Message;
            if (result.Succeeded)
            {
                DialogResult = true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
