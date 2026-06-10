using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App.Settings;

public sealed class DbSystemNameSettingsCommit
{
    public required Action<IReadOnlyList<DbDiSystemNameRow>, string, string> Commit { get; init; }
}

public partial class DbSystemNameSettingsView : SettingsEditorViewBase
{
    private readonly bool _showSimulatorPrefix;
    private readonly DbSystemNameSettingsCommit _commit;
    public ObservableCollection<DbDiSystemNameRow> Rows { get; }

    public string StructTypePrefix => StructPrefixBox.Text;
    public string SimulatorTypePrefix => SimulatorPrefixBox.Text;

    public DbSystemNameSettingsView(
        IEnumerable<DbDiSystemNameRow> rows,
        string structTypePrefix,
        string simulatorTypePrefix,
        bool showSimulatorPrefix,
        DbSystemNameSettingsCommit commit)
    {
        InitializeComponent();
        _showSimulatorPrefix = showSimulatorPrefix;
        _commit = commit;
        StructPrefixBox.Text = structTypePrefix ?? "";
        SimulatorPrefixBox.Text = simulatorTypePrefix ?? "";
        SimulatorPrefixLabel.Visibility = showSimulatorPrefix ? Visibility.Visible : Visibility.Collapsed;
        SimulatorPrefixBox.Visibility = showSimulatorPrefix ? Visibility.Visible : Visibility.Collapsed;
        SimulatorColumn.Visibility = showSimulatorPrefix ? Visibility.Visible : Visibility.Collapsed;
        Rows = new ObservableCollection<DbDiSystemNameRow>(rows.Select(r => r.Clone()));
        RecalculateFormulaColumns();
        SystemNamesGrid.ItemsSource = Rows;
    }

    public override bool Apply()
    {
        RecalculateFormulaColumns();
        _commit.Commit(Rows.Select(r => r.Clone()).ToList(), StructTypePrefix, SimulatorTypePrefix);
        return true;
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

    private void RecalculateFormulaColumns()
    {
        if (Rows is null) return;
        for (var index = 0; index < Rows.Count; index++)
        {
            var row = Rows[index];
            if (row.SystemNumber <= 0) row.SystemNumber = index + 1;
            row.SystemPlcShortName = SanitizeName(row.SystemPlcShortName);
            row.SystemPlcLongName = JoinPrefixAndName(StructTypePrefix, row.SystemPlcShortName);
            row.PlcVariableName = row.SystemPlcShortName;
            if (string.IsNullOrWhiteSpace(row.HmiSystemName))
            {
                row.HmiSystemName = row.SystemPlcShortName;
            }
            row.NameLength = row.HmiSystemName?.Length ?? 0;
            row.SimulatorDataType = _showSimulatorPrefix && !string.IsNullOrWhiteSpace(SimulatorTypePrefix)
                ? JoinPrefixAndName(SimulatorTypePrefix, row.SystemPlcShortName)
                : "";
        }
        SystemNamesGrid.Items.Refresh();
    }

    private static string SanitizeName(string value) => value.Trim().Replace(' ', '_');

    private static string JoinPrefixAndName(string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return name.Trim().TrimStart('_');
        }
        return $"{prefix.Trim().TrimEnd('_')}_{name.Trim().TrimStart('_')}";
    }
}
