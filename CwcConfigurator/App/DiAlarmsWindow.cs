using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CwcConfigurator.App;

/// <summary>One line of the DI alarms overview: the input (module, port, ID, name) and the DB ALM columns up to SETTINGS.</summary>
public sealed record DiAlarmViewRow(
    string Module,
    string Port,
    int InputId,
    string InputName,
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

/// <summary>Used DB ALM alarms caused by digital inputs, ordered by module 0 and its ports, module 1 … (read-only).</summary>
public sealed class DiAlarmsWindow : Window
{
    public DiAlarmsWindow(string plcName, IReadOnlyList<DiAlarmViewRow> rows)
    {
        Title = $"DI alarms by module / port — {plcName}";
        Width = 1600;
        Height = 760;
        MinWidth = 900;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

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
            FrozenColumnCount = 4,
            ItemsSource = rows
        };
        AddColumn(grid, "MOD", nameof(DiAlarmViewRow.Module), 50);
        AddColumn(grid, "PORT", nameof(DiAlarmViewRow.Port), 50);
        AddColumn(grid, "DI ID", nameof(DiAlarmViewRow.InputId), 60);
        AddColumn(grid, "DI NAME", nameof(DiAlarmViewRow.InputName), 170);
        AddColumn(grid, "ALM ID", nameof(DiAlarmViewRow.AlarmId), 65);
        AddColumn(grid, "PLC STRUCT", nameof(DiAlarmViewRow.PlcStructure), 120);
        AddColumn(grid, "PLC NAME", nameof(DiAlarmViewRow.PlcName), 170);
        AddColumn(grid, "ALARM SYSTEM ENG", nameof(DiAlarmViewRow.SystemEnglish), 160);
        AddColumn(grid, "ALARM TEXT ENG", nameof(DiAlarmViewRow.TextEnglish), 220);
        AddColumn(grid, "ALARM SYSTEM OTHER", nameof(DiAlarmViewRow.SystemOther), 160);
        AddColumn(grid, "ALARM TEXT OTHER", nameof(DiAlarmViewRow.TextOther), 220);
        AddColumn(grid, "ALM CONTROL", nameof(DiAlarmViewRow.Control), 95);
        AddColumn(grid, "SR ALARM", nameof(DiAlarmViewRow.SrAlarm), 80);
        AddColumn(grid, "ALARM CLASS", nameof(DiAlarmViewRow.AlarmClass), 120);
        AddColumn(grid, "REACT TO UNIT", nameof(DiAlarmViewRow.ReactToUnit), 105);
        AddColumn(grid, "SPARE", nameof(DiAlarmViewRow.Spare), 70);
        AddColumn(grid, "BLOCKING CLASS 1", nameof(DiAlarmViewRow.BlockingClass1), 120);
        AddColumn(grid, "BLOCKING CLASS 2", nameof(DiAlarmViewRow.BlockingClass2), 120);
        AddColumn(grid, "DELAY", nameof(DiAlarmViewRow.Delay), 70);
        AddColumn(grid, "TIME BASE", nameof(DiAlarmViewRow.TimeBase), 80);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var close = new Button { Content = "Close", Width = 86, IsCancel = true };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Text = rows.Count == 0
                ? "No used alarm has a digital input as its source (SOURCE DIG ID) — pull DB ALM from TIA first."
                : $"{rows.Count} used alarm(s) raised by digital inputs (SOURCE DIG ID), ordered by module and port. Ctrl+C copies the selection."
        });

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(grid);
        Content = root;
    }

    private static void AddColumn(DataGrid grid, string header, string path, double width)
    {
        grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
    }
}
