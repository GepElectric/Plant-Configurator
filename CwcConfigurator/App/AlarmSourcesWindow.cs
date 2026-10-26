using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>One line of the alarms-by-source overview: the source input (module, port, ID, name) and the DB ALM columns up to SETTINGS.</summary>
public sealed record AlarmSourceViewRow(
    AlarmSourceCategory Category,
    string Module,
    string Port,
    string SourceId,
    string SourceName,
    int AlarmId,
    string PlcStructure,
    string PlcName,
    string SystemEnglish,
    string TextEnglish,
    string SystemOther,
    string TextOther,
    string Control,
    string SrAlarm,
    string AlarmClass,
    string ReactToUnit,
    string Spare,
    string BlockingClass1,
    string BlockingClass2,
    string Delay,
    string TimeBase);

/// <summary>
/// Used DB ALM alarms by source of alarm: digital input, AI and RTD by module and port, program alarms by ID
/// (read-only). An alarm with source "both" is in the digital and in the analog tab.
/// </summary>
public sealed class AlarmSourcesWindow : Window
{
    public AlarmSourcesWindow(string plcName, IReadOnlyList<AlarmSourceViewRow> rows)
    {
        Title = $"Alarms by source — {plcName}";
        Width = 1600;
        Height = 780;
        MinWidth = 900;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl();
        AddTab(tabs, "Digital input", "DI", rows, AlarmSourceCategory.DigitalInput);
        AddTab(tabs, "AI", "MEAS", rows, AlarmSourceCategory.AnalogInput);
        AddTab(tabs, "RTD", "MEAS", rows, AlarmSourceCategory.Rtd);
        AddTab(tabs, "Program", "", rows, AlarmSourceCategory.Program);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var close = new Button { Content = "Close", Width = 86, IsCancel = true };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Text = "Used alarms by SOURCE TYPE (pull DB ALM from TIA first). Inputs by module, port and alarm ID; program alarms by ID. " +
                   "Source \"both\" is listed under its digital and its analog input. Ctrl+C copies the selection."
        });

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
    }

    private static void AddTab(TabControl tabs, string title, string sourceKind, IReadOnlyList<AlarmSourceViewRow> rows, AlarmSourceCategory category)
    {
        var items = rows.Where(row => row.Category == category).ToList();
        var hardware = category != AlarmSourceCategory.Program;
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            FrozenColumnCount = hardware ? 5 : 1,
            ItemsSource = items
        };
        if (hardware)
        {
            AddColumn(grid, "MOD", nameof(AlarmSourceViewRow.Module), 50);
            AddColumn(grid, "PORT", nameof(AlarmSourceViewRow.Port), 50);
            AddColumn(grid, sourceKind + " ID", nameof(AlarmSourceViewRow.SourceId), 65);
            AddColumn(grid, sourceKind + " NAME", nameof(AlarmSourceViewRow.SourceName), 170);
        }

        AddColumn(grid, "ALM ID", nameof(AlarmSourceViewRow.AlarmId), 65);
        AddColumn(grid, "PLC STRUCT", nameof(AlarmSourceViewRow.PlcStructure), 120);
        AddColumn(grid, "PLC NAME", nameof(AlarmSourceViewRow.PlcName), 170);
        AddColumn(grid, "ALARM SYSTEM ENG", nameof(AlarmSourceViewRow.SystemEnglish), 160);
        AddColumn(grid, "ALARM TEXT ENG", nameof(AlarmSourceViewRow.TextEnglish), 220);
        AddColumn(grid, "ALARM SYSTEM OTHER", nameof(AlarmSourceViewRow.SystemOther), 160);
        AddColumn(grid, "ALARM TEXT OTHER", nameof(AlarmSourceViewRow.TextOther), 220);
        AddColumn(grid, "ALM CONTROL", nameof(AlarmSourceViewRow.Control), 95);
        AddColumn(grid, "SR ALARM", nameof(AlarmSourceViewRow.SrAlarm), 80);
        AddColumn(grid, "ALARM CLASS", nameof(AlarmSourceViewRow.AlarmClass), 120);
        AddColumn(grid, "REACT TO UNIT", nameof(AlarmSourceViewRow.ReactToUnit), 105);
        AddColumn(grid, "SPARE", nameof(AlarmSourceViewRow.Spare), 70);
        AddColumn(grid, "BLOCKING CLASS 1", nameof(AlarmSourceViewRow.BlockingClass1), 120);
        AddColumn(grid, "BLOCKING CLASS 2", nameof(AlarmSourceViewRow.BlockingClass2), 120);
        AddColumn(grid, "DELAY", nameof(AlarmSourceViewRow.Delay), 70);
        AddColumn(grid, "TIME BASE", nameof(AlarmSourceViewRow.TimeBase), 80);
        tabs.Items.Add(new TabItem { Header = $"{title} ({items.Count})", Content = grid });
    }

    private static void AddColumn(DataGrid grid, string header, string path, double width)
    {
        grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
    }
}
