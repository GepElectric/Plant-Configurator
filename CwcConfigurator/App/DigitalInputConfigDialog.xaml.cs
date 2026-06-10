using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DigitalInputConfigDialog : Window
{
    private readonly ICollectionView _dbDiView;
    private readonly List<DbStringRow> _dbDiRows;
    private bool _loadingFilters;

    public SignalConfigState SelectedConfig { get; private set; }
    public int SelectedId { get; private set; }

    public DigitalInputConfigDialog(
        DigitalSignalConfig row,
        IReadOnlyList<DbStringRow> dbRows,
        string signalType = "DI",
        bool includeInverted = true)
    {
        InitializeComponent();
        _dbDiRows = dbRows.ToList();

        SelectedConfig = row.Config;
        SelectedId = row.Id;

        Title = $"{signalType} Configuration";
        DbSlicerLabel.Text = $"DB {signalType} slicer";
        RowTitleText.Text = $"{signalType} module {row.ModuleNumber}, port {row.Port}";
        RowSubtitleText.Text = $"{row.ModuleName}  |  {row.Structure}  |  {row.SignalName}";

        ConfigCombo.ItemsSource = includeInverted
            ? Enum.GetValues<SignalConfigState>()
            : new[] { SignalConfigState.Disabled, SignalConfigState.Enabled };
        ConfigCombo.SelectedItem = row.Config;
        SelectedIdText.Text = row.Id.ToString();

        LoadColumnFilters();

        _dbDiView = CollectionViewSource.GetDefaultView(_dbDiRows);
        _dbDiView.Filter = FilterDbDiRow;
        DbDiGrid.ItemsSource = _dbDiView;
        SelectCurrentDbRow(row.Id);
    }

    private void DbDiSlicerText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingFilters)
        {
            return;
        }

        UpdateColumnFilters();
        _dbDiView.Refresh();
    }

    private void ColumnFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilters)
        {
            return;
        }

        UpdateColumnFilters();
        _dbDiView.Refresh();
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        _loadingFilters = true;
        DbDiSlicerText.Text = "";
        IdFilter.SelectedIndex = 0;
        PlcStructureFilter.SelectedIndex = 0;
        PlcNameFilter.SelectedIndex = 0;
        SystemNameFilter.SelectedIndex = 0;
        NameFilter.SelectedIndex = 0;
        _loadingFilters = false;
        UpdateColumnFilters();
        _dbDiView.Refresh();
    }

    private void DbDiGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbDiGrid.SelectedItem is DbStringRow row)
        {
            SelectedIdText.Text = row.Id.ToString();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ConfigCombo.SelectedItem is SignalConfigState config)
        {
            SelectedConfig = config;
        }

        if (!int.TryParse(SelectedIdText.Text.Trim(), out var selectedId))
        {
            MessageBox.Show(this, "ID must be a number.", "DI Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedId = selectedId;
        DialogResult = true;
    }

    private bool FilterDbDiRow(object item)
    {
        if (item is not DbStringRow row)
        {
            return false;
        }

        return MatchesGeneralSlicer(row)
            && MatchesFilter(IdFilter, row.Id.ToString())
            && MatchesFilter(PlcStructureFilter, row.PlcStructure)
            && MatchesFilter(PlcNameFilter, row.PlcName)
            && MatchesFilter(SystemNameFilter, row.SystemNameEnglish)
            && MatchesFilter(NameFilter, row.NameEnglish);
    }

    private void LoadColumnFilters()
    {
        _loadingFilters = true;
        SetFilterItems(IdFilter, BuildFilterItems(_dbDiRows.Select(row => row.Id.ToString()), "All IDs"), null);
        SetFilterItems(PlcStructureFilter, BuildFilterItems(_dbDiRows.Select(row => row.PlcStructure), "All PLC structures"), null);
        SetFilterItems(PlcNameFilter, BuildFilterItems(_dbDiRows.Select(row => row.PlcName), "All PLC names"), null);
        SetFilterItems(SystemNameFilter, BuildFilterItems(_dbDiRows.Select(row => row.SystemNameEnglish), "All system names"), null);
        SetFilterItems(NameFilter, BuildFilterItems(_dbDiRows.Select(row => row.NameEnglish), "All names"), null);
        _loadingFilters = false;
    }

    private void UpdateColumnFilters()
    {
        _loadingFilters = true;
        UpdateFilterItems(IdFilter, "All IDs", row => row.Id.ToString());
        UpdateFilterItems(PlcStructureFilter, "All PLC structures", row => row.PlcStructure);
        UpdateFilterItems(PlcNameFilter, "All PLC names", row => row.PlcName);
        UpdateFilterItems(SystemNameFilter, "All system names", row => row.SystemNameEnglish);
        UpdateFilterItems(NameFilter, "All names", row => row.NameEnglish);
        _loadingFilters = false;
    }

    private void UpdateFilterItems(ComboBox targetFilter, string allText, Func<DbStringRow, string> valueSelector)
    {
        var currentValue = targetFilter.SelectedItem as string;
        var availableRows = _dbDiRows.Where(row => MatchesFiltersExcept(row, targetFilter));
        var items = BuildFilterItems(availableRows.Select(valueSelector), allText);
        SetFilterItems(targetFilter, items, currentValue);
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
        var slicer = DbDiSlicerText.Text.Trim();
        if (string.IsNullOrWhiteSpace(slicer))
        {
            return true;
        }

        return row.Id.ToString().Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.PlcStructure.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.PlcName.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.SystemNameEnglish.Contains(slicer, StringComparison.OrdinalIgnoreCase)
            || row.NameEnglish.Contains(slicer, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFilter(ComboBox filter, string value)
    {
        return filter.SelectedIndex <= 0
            || string.Equals(filter.SelectedItem as string, value, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesFiltersExcept(DbStringRow row, ComboBox excludedFilter)
    {
        return MatchesGeneralSlicer(row)
            && (ReferenceEquals(excludedFilter, IdFilter) || MatchesFilter(IdFilter, row.Id.ToString()))
            && (ReferenceEquals(excludedFilter, PlcStructureFilter) || MatchesFilter(PlcStructureFilter, row.PlcStructure))
            && (ReferenceEquals(excludedFilter, PlcNameFilter) || MatchesFilter(PlcNameFilter, row.PlcName))
            && (ReferenceEquals(excludedFilter, SystemNameFilter) || MatchesFilter(SystemNameFilter, row.SystemNameEnglish))
            && (ReferenceEquals(excludedFilter, NameFilter) || MatchesFilter(NameFilter, row.NameEnglish));
    }

    private void SelectCurrentDbRow(int id)
    {
        foreach (var item in _dbDiView)
        {
            if (item is DbStringRow row && row.Id == id)
            {
                DbDiGrid.SelectedItem = row;
                DbDiGrid.ScrollIntoView(row);
                break;
            }
        }
    }
}
