using System.Windows;
using nexIRC.Application;

namespace nexIRC.Desktop;

public partial class AliasesWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private Guid _selectedId;

    public AliasesWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Refresh();
    }

    private void Refresh(Guid? selectedId = null)
    {
        AliasesList.ItemsSource = _viewModel.Aliases.ToArray();
        var selected = selectedId is Guid id
            ? _viewModel.Aliases.FirstOrDefault(alias => alias.Id == id)
            : _viewModel.Aliases.Count > 0 ? _viewModel.Aliases[0] : null;
        AliasesList.SelectedItem = selected;
        if (selected is null) ClearForm(); else Apply(selected);
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AliasesList.SelectedItem is AliasDefinition alias) Apply(alias);
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _selectedId = Guid.NewGuid();
        ClearForm();
        NameBox.Focus();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var alias = new AliasDefinition
        {
            Id = _selectedId == Guid.Empty ? Guid.NewGuid() : _selectedId,
            Name = NameBox.Text,
            Expansion = ExpansionBox.Text,
            Description = DescriptionBox.Text,
            IsEnabled = true
        };
        var validation = AliasValidator.Validate(alias);
        if (!validation.IsValid)
        {
            StatusText.Text = validation.Error;
            return;
        }

        await _viewModel.SaveAliasAsync(alias);
        _selectedId = AliasValidator.Normalize(alias)!.Id;
        Refresh(_selectedId);
        StatusText.Text = "Alias saved.";
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (AliasesList.SelectedItem is not AliasDefinition alias) return;
        await _viewModel.SaveAliasAsync(alias with { IsEnabled = !alias.IsEnabled });
        Refresh(alias.Id);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (AliasesList.SelectedItem is not AliasDefinition alias) return;
        await _viewModel.DeleteAliasAsync(alias.Id);
        _selectedId = Guid.Empty;
        Refresh();
    }

    private void Apply(AliasDefinition alias)
    {
        _selectedId = alias.Id;
        NameBox.Text = alias.Name;
        ExpansionBox.Text = alias.Expansion;
        DescriptionBox.Text = alias.Description ?? string.Empty;
        StatusText.Text = alias.IsEnabled ? "Enabled" : "Disabled";
    }

    private void ClearForm()
    {
        NameBox.Clear();
        ExpansionBox.Clear();
        DescriptionBox.Clear();
        StatusText.Text = string.Empty;
    }
}
