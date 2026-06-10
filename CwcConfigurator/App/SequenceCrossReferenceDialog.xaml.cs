using System.Windows;
using CwcConfigurator.Services.TiaSync;

namespace CwcConfigurator.App;

public partial class SequenceCrossReferenceDialog : Window
{
    private readonly Func<string, TiaCrossReferenceItem?, Task>? _openCrossReferenceAsync;

    public SequenceCrossReferenceDialog(
        string idCrossReferenceVariable,
        string valCrossReferenceVariable,
        Func<string, TiaCrossReferenceItem?, Task>? openCrossReferenceAsync = null)
    {
        InitializeComponent();
        _openCrossReferenceAsync = openCrossReferenceAsync;
        IdCrossReferenceVariableText.Text = idCrossReferenceVariable;
        ValCrossReferenceVariableText.Text = valCrossReferenceVariable;
        IdCrossReferenceSummaryText.Text = string.IsNullOrWhiteSpace(idCrossReferenceVariable)
            ? "No variable path is available for this row."
            : "Cross-reference scan will start when this window opens.";
        ValCrossReferenceSummaryText.Text = string.IsNullOrWhiteSpace(valCrossReferenceVariable)
            ? "No variable path is available for this row."
            : "Cross-reference scan will start when this window opens.";
    }

    public void SetCrossReferenceLoading(string member)
    {
        var target = GetCrossReferenceControls(member);
        target.Summary.Text = $"Scanning TIA blocks for {member} usage...";
        target.Grid.ItemsSource = null;
        target.OpenButton.IsEnabled = false;
    }

    public void SetCrossReferenceResult(string member, TiaCrossReferenceResult result)
    {
        var target = GetCrossReferenceControls(member);
        target.Summary.Text = result.Warnings.Count == 0
            ? $"Found {result.UsageCount} usage(s), shown as {result.Items.Count} row(s)."
            : $"Found {result.UsageCount} usage(s), shown as {result.Items.Count} row(s). Warnings: {string.Join(" | ", result.Warnings.Take(3))}";
        target.Grid.ItemsSource = result.Items;
        target.Grid.SelectedItem = null;
        target.OpenButton.IsEnabled = false;
    }

    public void SetCrossReferenceUnavailable(string member, string message)
    {
        var target = GetCrossReferenceControls(member);
        target.Summary.Text = message;
        target.Grid.ItemsSource = null;
        target.OpenButton.IsEnabled = false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyOwnerRelativeSize();
    }

    private async void OpenIdTiaCrossReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenTiaCrossReferenceAsync("ID", IdCrossReferenceGrid, OpenIdTiaCrossReferenceButton);
    }

    private async void OpenValTiaCrossReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenTiaCrossReferenceAsync("VAL", ValCrossReferenceGrid, OpenValTiaCrossReferenceButton);
    }

    private async Task OpenTiaCrossReferenceAsync(string member, System.Windows.Controls.DataGrid grid, System.Windows.Controls.Button button)
    {
        if (_openCrossReferenceAsync is null)
        {
            return;
        }

        if (grid.SelectedItem is not TiaCrossReferenceItem selectedItem)
        {
            button.IsEnabled = false;
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _openCrossReferenceAsync(member, selectedItem);
        }
        finally
        {
            button.IsEnabled = _openCrossReferenceAsync is not null && grid.SelectedItem is TiaCrossReferenceItem;
        }
    }

    private void IdCrossReferenceGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        OpenIdTiaCrossReferenceButton.IsEnabled = _openCrossReferenceAsync is not null && IdCrossReferenceGrid.SelectedItem is TiaCrossReferenceItem;
    }

    private void ValCrossReferenceGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        OpenValTiaCrossReferenceButton.IsEnabled = _openCrossReferenceAsync is not null && ValCrossReferenceGrid.SelectedItem is TiaCrossReferenceItem;
    }

    private (System.Windows.Controls.TextBlock Summary, System.Windows.Controls.DataGrid Grid, System.Windows.Controls.Button OpenButton) GetCrossReferenceControls(string member)
    {
        return member.Equals("VAL", StringComparison.OrdinalIgnoreCase)
            ? (ValCrossReferenceSummaryText, ValCrossReferenceGrid, OpenValTiaCrossReferenceButton)
            : (IdCrossReferenceSummaryText, IdCrossReferenceGrid, OpenIdTiaCrossReferenceButton);
    }

    private void ApplyOwnerRelativeSize()
    {
        var ownerWidth = Owner?.ActualWidth > 0 ? Owner.ActualWidth : SystemParameters.WorkArea.Width;
        var ownerHeight = Owner?.ActualHeight > 0 ? Owner.ActualHeight : SystemParameters.WorkArea.Height;
        Width = ownerWidth * 0.8;
        Height = ownerHeight * 0.85;

        if (Owner is null)
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2.0;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2.0;
            return;
        }

        if (Owner.WindowState == WindowState.Maximized)
        {
            Left = SystemParameters.WorkArea.Left + ((SystemParameters.WorkArea.Width - Width) / 2.0);
            Top = SystemParameters.WorkArea.Top + ((SystemParameters.WorkArea.Height - Height) / 2.0);
            return;
        }

        Left = Owner.Left + ((Owner.ActualWidth - Width) / 2.0);
        Top = Owner.Top + ((Owner.ActualHeight - Height) / 2.0);
    }
}
