using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class AnalogConfigDialog : Window
{
    private readonly ICollectionView _dbView;
    private readonly List<DbStringRow> _dbRows;
    private bool _loadingFilters;

    public SignalConfigState SelectedConfig { get; private set; }
    public int SelectedId { get; private set; }
    public double SelectedFilterTime { get; private set; }
    public SignalConfigState SelectedCustomLimits { get; private set; }
    public double SelectedLowLimit { get; private set; }
    public double SelectedHighLimit { get; private set; }

    public AnalogConfigDialog(AnalogSignalConfig row, IReadOnlyList<DbStringRow> dbRows, string signalType)
    {
        InitializeComponent();
        _dbRows = dbRows.ToList();

        SelectedConfig = row.Config;
        SelectedId = row.Id;
        SelectedFilterTime = row.FilterTime;
        SelectedCustomLimits = row.CustomLimits;
        SelectedLowLimit = row.LowLimit;
        SelectedHighLimit = row.HighLimit;

        Title = $"{signalType} Configuration";
        DbSlicerLabel.Text = "DB MEAS slicer";
        RowTitleText.Text = $"{signalType} module {row.ModuleNumber}, port {row.Port}";
        RowSubtitleText.Text = $"{row.ModuleName}  |  {row.Structure}  |  {row.SignalName}";

        var onOffStates = new[] { SignalConfigState.Disabled, SignalConfigState.Enabled };
        ConfigCombo.ItemsSource = onOffStates;
        CustomLimitsCombo.ItemsSource = onOffStates;
        ConfigCombo.SelectedItem = row.Config == SignalConfigState.Enabled ? SignalConfigState.Enabled : SignalConfigState.Disabled;
        CustomLimitsCombo.SelectedItem = row.CustomLimits == SignalConfigState.Enabled ? SignalConfigState.Enabled : SignalConfigState.Disabled;
        SelectedIdText.Text = row.Id.ToString(CultureInfo.InvariantCulture);
        FilterTimeText.Text = row.FilterTime.ToString("0.0###", CultureInfo.InvariantCulture);
        LowLimitText.Text = row.LowLimit.ToString(CultureInfo.InvariantCulture);
        HighLimitText.Text = row.HighLimit.ToString(CultureInfo.InvariantCulture);

        LoadColumnFilters();

        _dbView = CollectionViewSource.GetDefaultView(_dbRows);
        _dbView.Filter = FilterDbRow;
        DbGrid.ItemsSource = _dbView;
        SelectCurrentDbRow(row.Id);
    }

    private void DbSlicerText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingFilters)
        {
            return;
        }

        UpdateColumnFilters();
        _dbView.Refresh();
    }

    private void ColumnFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilters)
        {
            return;
        }

        UpdateColumnFilters();
        _dbView.Refresh();
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        _loadingFilters = true;
        DbSlicerText.Text = "";
        IdFilter.SelectedIndex = 0;
        PlcStructureFilter.SelectedIndex = 0;
        PlcNameFilter.SelectedIndex = 0;
        SystemNameFilter.SelectedIndex = 0;
        NameFilter.SelectedIndex = 0;
        UnitFilter.SelectedIndex = 0;
        _loadingFilters = false;
        UpdateColumnFilters();
        _dbView.Refresh();
    }

    private void DbGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbGrid.SelectedItem is DbStringRow row)
        {
            SelectedIdText.Text = row.Id.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SelectedIdText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedId))
        {
            MessageBox.Show(this, "ID must be a number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseDouble(FilterTimeText.Text, out var filterTime)
            || !TryParseDouble(LowLimitText.Text, out var lowLimit)
            || !TryParseDouble(HighLimitText.Text, out var highLimit))
        {
            MessageBox.Show(this, "Filter Time, Low Limit and High Limit must be numbers.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        SelectedCustomLimits = CustomLimitsCombo.SelectedItem is SignalConfigState customLimits ? customLimits : SignalConfigState.Disabled;
        SelectedLowLimit = lowLimit;
        SelectedHighLimit = highLimit;
        DialogResult = true;
    }

    private bool FilterDbRow(object item)
    {
        return item is DbStringRow row
            && MatchesGeneralSlicer(row)
            && MatchesFilter(IdFilter, row.Id.ToString(CultureInfo.InvariantCulture))
            && MatchesFilter(PlcStructureFilter, row.PlcStructure)
            && MatchesFilter(PlcNameFilter, row.PlcName)
            && MatchesFilter(SystemNameFilter, row.SystemNameEnglish)
            && MatchesFilter(NameFilter, row.NameEnglish)
            && MatchesFilter(UnitFilter, row.Unit);
    }

    private void LoadColumnFilters()
    {
        _loadingFilters = true;
        SetFilterItems(IdFilter, BuildFilterItems(_dbRows.Select(row => row.Id.ToString(CultureInfo.InvariantCulture)), "All IDs"), null);
        SetFilterItems(PlcStructureFilter, BuildFilterItems(_dbRows.Select(row => row.PlcStructure), "All PLC structures"), null);
        SetFilterItems(PlcNameFilter, BuildFilterItems(_dbRows.Select(row => row.PlcName), "All PLC names"), null);
        SetFilterItems(SystemNameFilter, BuildFilterItems(_dbRows.Select(row => row.SystemNameEnglish), "All system names"), null);
        SetFilterItems(NameFilter, BuildFilterItems(_dbRows.Select(row => row.NameEnglish), "All names"), null);
        SetFilterItems(UnitFilter, BuildFilterItems(_dbRows.Select(row => row.Unit), "All units"), null);
        _loadingFilters = false;
    }

    private void UpdateColumnFilters()
    {
        _loadingFilters = true;
        UpdateFilterItems(IdFilter, "All IDs", row => row.Id.ToString(CultureInfo.InvariantCulture));
        UpdateFilterItems(PlcStructureFilter, "All PLC structures", row => row.PlcStructure);
        UpdateFilterItems(PlcNameFilter, "All PLC names", row => row.PlcName);
        UpdateFilterItems(SystemNameFilter, "All system names", row => row.SystemNameEnglish);
        UpdateFilterItems(NameFilter, "All names", row => row.NameEnglish);
        UpdateFilterItems(UnitFilter, "All units", row => row.Unit);
        _loadingFilters = false;
    }

    private void UpdateFilterItems(ComboBox targetFilter, string allText, Func<DbStringRow, string> valueSelector)
    {
        var currentValue = targetFilter.SelectedItem as string;
        var availableRows = _dbRows.Where(row => MatchesFiltersExcept(row, targetFilter));
        SetFilterItems(targetFilter, BuildFilterItems(availableRows.Select(valueSelector), allText), currentValue);
    }

    private static void SetFilterItems(ComboBox filter, List<string> items, string? preferredValue)
    {
        filter.ItemsSource = items;
        filter.SelectedItem = preferredValue is not null && items.Contains(preferredValue, StringComparer.OrdinalIgnoreCase)
            ? preferredValue
            : items.FirstOrDefault();
    }

    private static List<string> BuildFilterItems(IEnumerable<string> values, string allText)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Prepend(allText)
            .ToList();
    }

    private bool MatchesGeneralSlicer(DbStringRow row)
    {
        var slicer = DbSlicerText.Text.Trim();
        if (string.IsNullOrWhiteSpace(slicer))
        {
            return true;
        }

        return row.Id.ToString(CultureInfo.InvariantCulture).Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.PlcStructure.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.PlcName.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.SystemNameEnglish.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.NameEnglish.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.Unit.Contains(slicer, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFilter(ComboBox filter, string value)
    {
        return filter.SelectedIndex <= 0
            || string.Equals(filter.SelectedItem as string, value, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesFiltersExcept(DbStringRow row, ComboBox excludedFilter)
    {
        return MatchesGeneralSlicer(row)
            && (ReferenceEquals(excludedFilter, IdFilter) || MatchesFilter(IdFilter, row.Id.ToString(CultureInfo.InvariantCulture)))
            && (ReferenceEquals(excludedFilter, PlcStructureFilter) || MatchesFilter(PlcStructureFilter, row.PlcStructure))
            && (ReferenceEquals(excludedFilter, PlcNameFilter) || MatchesFilter(PlcNameFilter, row.PlcName))
            && (ReferenceEquals(excludedFilter, SystemNameFilter) || MatchesFilter(SystemNameFilter, row.SystemNameEnglish))
            && (ReferenceEquals(excludedFilter, NameFilter) || MatchesFilter(NameFilter, row.NameEnglish))
            && (ReferenceEquals(excludedFilter, UnitFilter) || MatchesFilter(UnitFilter, row.Unit));
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
