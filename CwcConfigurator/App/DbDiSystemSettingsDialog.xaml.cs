using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DbDiSystemSettingsDialog : Window
{
    public ObservableCollection<DbDiSystemNameRow> Rows { get; }
    private readonly string _dbName;
    private readonly bool _showSimulatorPrefix;

    public string StructTypePrefix => StructPrefixBox.Text;
    public string SimulatorTypePrefix => SimulatorPrefixBox.Text;

    public DbDiSystemSettingsDialog(IEnumerable<DbDiSystemNameRow> rows, string dbName = "DB DI", string? structTypePrefix = null, string? simulatorTypePrefix = null, bool showSimulatorPrefix = true)
    {
        InitializeComponent();
        Title = $"{dbName} System Settings";
        _dbName = dbName;
        _showSimulatorPrefix = showSimulatorPrefix;
        StructPrefixBox.Text = structTypePrefix ?? GetDefaultLongNamePrefix();
        SimulatorPrefixBox.Text = simulatorTypePrefix ?? GetDefaultSimulatorPrefix();
        SimulatorPrefixLabel.Visibility = _showSimulatorPrefix ? Visibility.Visible : Visibility.Collapsed;
        SimulatorPrefixBox.Visibility = _showSimulatorPrefix ? Visibility.Visible : Visibility.Collapsed;
        SimulatorColumn.Visibility = _showSimulatorPrefix ? Visibility.Visible : Visibility.Collapsed;
        Rows = new ObservableCollection<DbDiSystemNameRow>(rows.Select(row => row.Clone()));
        RecalculateFormulaColumns();
        SystemNamesGrid.ItemsSource = Rows;
    }

    private void SystemNamesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(RecalculateFormulaColumns, DispatcherPriority.Background);
    }

    private void SystemNamesGrid_CurrentCellChanged(object sender, EventArgs e)
    {
        RecalculateFormulaColumns();
    }

    private void PrefixBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RecalculateFormulaColumns();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        RecalculateFormulaColumns();
        DialogResult = true;
    }

    private void RecalculateFormulaColumns()
    {
        if (Rows is null)
        {
            return;
        }

        for (var index = 0; index < Rows.Count; index++)
        {
            var row = Rows[index];
            if (row.SystemNumber <= 0)
            {
                row.SystemNumber = index + 1;
            }

            row.SystemPlcShortName = SanitizeName(row.SystemPlcShortName);
            row.SystemPlcLongName = JoinPrefixAndName(StructTypePrefix, row.SystemPlcShortName);
            row.PlcVariableName = row.SystemPlcShortName;
            if (string.IsNullOrWhiteSpace(row.HmiSystemName))
            {
                row.HmiSystemName = row.SystemPlcShortName;
            }

            row.NameLength = row.HmiSystemName?.Length ?? 0;
            if (_showSimulatorPrefix && !string.IsNullOrWhiteSpace(SimulatorTypePrefix))
            {
                row.SimulatorDataType = JoinPrefixAndName(SimulatorTypePrefix, row.SystemPlcShortName);
            }
            else
            {
                row.SimulatorDataType = "";
            }
        }

        SystemNamesGrid.Items.Refresh();
    }

    private string GetDefaultLongNamePrefix()
    {
        return _dbName switch
        {
            "DB DI" => "DT_DIGITAL_INPUT_",
            "DB DO" => "DT_DIGITAL_OUTPUT_",
            "DB MEAS" => "DT_MEASUREMENT_SYSTEM_",
            "DB ALM" => "DT_ALM_SYSTEM_",
            "DB PAR" => "DT_PAR_",
            _ => ""
        };
    }

    private string GetDefaultSimulatorPrefix()
    {
        return _dbName switch
        {
            "DB DI" => "DT_SIMULATOR_DIGITAL_OUTPUT_",
            "DB DO" => "DT_SIMULATOR_DIGITAL_INPUTS_",
            "DB MEAS" => "DT_SIMULATOR_ANA_CTRL_",
            _ => ""
        };
    }

    private static string SanitizeName(string value)
    {
        return value.Trim().Replace(' ', '_');
    }

    private static string JoinPrefixAndName(string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return name.Trim().TrimStart('_');
        }

        return $"{prefix.Trim().TrimEnd('_')}_{name.Trim().TrimStart('_')}";
    }
}
