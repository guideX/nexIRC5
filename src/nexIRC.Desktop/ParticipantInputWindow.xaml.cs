using System.Windows;

namespace nexIRC.Desktop;

public partial class ParticipantInputWindow : Window
{
    public ParticipantInputWindow(string title, string prompt, string? initialValue = null, bool selectAll = false)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue ?? string.Empty;
        if (selectAll)
        {
            Loaded += (_, _) =>
            {
                ValueBox.Focus();
                ValueBox.SelectAll();
            };
        }
    }

    public string Value => ValueBox.Text;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    public static string? Show(Window owner, string title, string prompt, string? initialValue = null, bool selectAll = false)
    {
        var dialog = new ParticipantInputWindow(title, prompt, initialValue, selectAll) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
