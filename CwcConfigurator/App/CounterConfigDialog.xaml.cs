using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class CounterConfigDialog : Window
{
    private readonly ICollectionView _dbView;
    private readonly List<DbStringRow> _dbRows;

    public SignalConfigState SelectedConfig { get; private set; }
    public int SelectedId { get; private set; }
    public double SelectedFilterTime { get; private set; }

    public CounterConfigDialog(CounterConfig row, IReadOnlyList<DbStringRow> dbRows)
    {
        InitializeComponent();
        _dbRows = dbRows.ToList();

        RowTitleText.Text = $"COUNTER module {row.ModuleNumber}";
        RowSubtitleText.Text = $"{row.ModuleName}  |  {row.Structure}  |  {row.SignalName}";
        ConfigCombo.ItemsSource = new[] { SignalConfigState.Disabled, SignalConfigState.Enabled };
        ConfigCombo.SelectedItem = row.Config == SignalConfigState.Enabled ? SignalConfigState.Enabled : SignalConfigState.Disabled;
        SelectedIdText.Text = row.Id.ToString(CultureInfo.InvariantCulture);
        FilterTimeText.Text = row.FilterTime.ToString("0.0###", CultureInfo.InvariantCulture);

        _dbView = CollectionViewSource.GetDefaultView(_dbRows);
        _dbView.Filter = FilterDbRow;
        DbGrid.ItemsSource = _dbView;
        SelectCurrentDbRow(row.Id);
    }

    private void DbSlicerText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _dbView.Refresh();
    }

    private void DbGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbGrid.SelectedItem is DbStringRow row)
        {
            SelectedIdText.Text = row.Id.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        DbSlicerText.Text = "";
        _dbView.Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SelectedIdText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedId))
        {
            MessageBox.Show(this, "ID must be a number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseDouble(FilterTimeText.Text, out var filterTime))
        {
            MessageBox.Show(this, "Filter Time must be a number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CountDecimals(FilterTimeText.Text) > 4)
        {
            MessageBox.Show(this, "Filter Time can have maximum 4 decimals.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedConfig = ConfigCombo.SelectedItem is SignalConfigState config ? config : SignalConfigState.Disabled;
        SelectedId = selectedId;
        SelectedFilterTime = filterTime;
        DialogResult = true;
    }

    private bool FilterDbRow(object item)
    {
        if (item is not DbStringRow row)
        {
            return false;
        }

        var slicer = DbSlicerText.Text.Trim();
        return string.IsNullOrWhiteSpace(slicer)
            || row.Id.ToString(CultureInfo.InvariantCulture).Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.PlcStructure.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.PlcName.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.SystemNameEnglish.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.NameEnglish.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.Unit.Contains(slicer, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectCurrentDbRow(int id)
    {
        foreach (var item in _dbView)
        {
            if (item is DbStringRow row && row.Id == id)
            {
                DbGrid.SelectedItem = row;
                DbGrid.ScrollIntoView(row);
                break;
            }
        }
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static int CountDecimals(string text)
    {
        var separatorIndex = Math.Max(text.LastIndexOf('.'), text.LastIndexOf(','));
        return separatorIndex < 0 ? 0 : text.Length - separatorIndex - 1;
    }
}
