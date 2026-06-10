using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class AnalogOutputConfigDialog : Window
{
    private readonly ICollectionView _dbAoView;

    public SignalConfigState SelectedConfig { get; private set; }
    public int SelectedId { get; private set; }

    public AnalogOutputConfigDialog(AnalogOutputConfig row, IReadOnlyList<DbAoRow> dbAoRows)
    {
        InitializeComponent();
        RowTitleText.Text = $"AO module {row.ModuleNumber}, port {row.Port}";
        RowSubtitleText.Text = $"{row.ModuleName}  |  {row.SystemName}  |  {row.SignalName}";
        ConfigCombo.ItemsSource = new[] { SignalConfigState.Disabled, SignalConfigState.Enabled };
        ConfigCombo.SelectedItem = row.Config == SignalConfigState.Enabled ? SignalConfigState.Enabled : SignalConfigState.Disabled;
        SelectedIdText.Text = row.Id.ToString(CultureInfo.InvariantCulture);

        _dbAoView = CollectionViewSource.GetDefaultView(dbAoRows);
        _dbAoView.Filter = FilterDbAoRow;
        DbAoGrid.ItemsSource = _dbAoView;
        SelectCurrentDbAoRow(row.Id);
    }

    private void DbAoSlicerText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _dbAoView.Refresh();
    }

    private void DbAoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbAoGrid.SelectedItem is DbAoRow row)
        {
            SelectedIdText.Text = row.AoNumber.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        DbAoSlicerText.Text = "";
        _dbAoView.Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SelectedIdText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedId))
        {
            MessageBox.Show(this, "ID must be a number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedConfig = ConfigCombo.SelectedItem is SignalConfigState config ? config : SignalConfigState.Disabled;
        SelectedId = selectedId;
        DialogResult = true;
    }

    private bool FilterDbAoRow(object item)
    {
        if (item is not DbAoRow row)
        {
            return false;
        }

        var slicer = DbAoSlicerText.Text.Trim();
        if (string.IsNullOrWhiteSpace(slicer))
        {
            return true;
        }

        return row.AoNumber.ToString(CultureInfo.InvariantCulture).Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.AoSystemName.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.NameEnglish.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.NameOther.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.Unit.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.ModuleName.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || (row.ModulePort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Contains(slicer, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectCurrentDbAoRow(int id)
    {
        foreach (var item in _dbAoView)
        {
            if (item is DbAoRow row && row.AoNumber == id)
            {
                DbAoGrid.SelectedItem = row;
                DbAoGrid.ScrollIntoView(row);
                break;
            }
        }
    }
}
