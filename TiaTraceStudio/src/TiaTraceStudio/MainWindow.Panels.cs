using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TiaTraceStudio;

/// <summary>
/// One recording shown as several graphs in one column (panels 1–4), each with its own time axis and all linked on
/// time: the signals are spread over the panels in one table, or with a quick layout (digital signals apart, one panel
/// per unit).
/// </summary>
public partial class MainWindow
{
    void Panels_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (project.Data.Time.Length == 0) throw new InvalidDataException("Prvo učitaj snimku.");
        Sync();
        var rows = signals.Select((signal, index) => new PanelRow(signal, SignalDisplay.PlotName(signal, index))).ToList();

        var layout = new DockPanel { Margin = new Thickness(16) };
        var hint = new TextBlock
        {
            Text = "Svaki panel je zaseban graf ispod prethodnog, sa svojom vremenskom osi; zoom i pomak vremena vrijede za sve panele. Isto vrijedi za izvoz slike i Word izvještaj.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);

        var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(quick, Dock.Top); layout.Children.Add(quick);
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
        grid.Columns.Add(new DataGridTextColumn { Header = "Signal", Binding = new Binding(nameof(PanelRow.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Jedinica", Binding = new Binding(nameof(PanelRow.Unit)), IsReadOnly = true, Width = 90 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Panel", ItemsSource = new[] { 1, 2, 3, 4 }, SelectedItemBinding = new Binding(nameof(PanelRow.Panel)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 80 });

        void Set(Func<PanelRow, int> panel) { foreach (var row in rows) row.Panel = Math.Clamp(panel(row), 1, 4); grid.Items.Refresh(); }
        Action(quick, "Sve u jedan graf", (_, _) => Set(_ => 1));
        Action(quick, "Digitalni (0/1) u zaseban panel", (_, _) => Set(row => row.IsDigital ? 2 : 1));
        Action(quick, "Svaka jedinica svoj panel", (_, _) =>
        {
            var units = rows.Select(row => row.IsDigital ? "0/1" : row.Unit).Distinct().ToList();
            Set(row => units.IndexOf(row.IsDigital ? "0/1" : row.Unit) + 1);
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(grid);

        var dialog = new Window
        {
            Owner = this, Title = "Paneli · više grafova s istom vremenskom osi", Content = layout, Width = Dialogs.Width(720), Height = Dialogs.Height(620),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        Action(buttons, "Odustani", (_, _) => dialog.Close());
        Action(buttons, "Primijeni", (_, _) => Guard(() =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            foreach (var row in rows) row.Signal.Panel = row.Panel;
            dialog.Close();
            Commit();
            int count = PlotService.Panels(project).Length;
            Status(count > 1 ? $"Graf je podijeljen u {count} panela s istom vremenskom osi." : "Svi signali su u jednom grafu.");
        }), true);
        dialog.ShowDialog();
    });

    sealed class PanelRow(Signal signal, string name)
    {
        public Signal Signal { get; } = signal;
        public string Name { get; } = name;
        public string Unit { get; } = signal.Unit.Trim();
        public int Panel { get; set; } = Math.Clamp(signal.Panel, 1, 4);
        /// <summary>A binary signal: unit 0/1 or only 0 and 1 in the recording.</summary>
        public bool IsDigital => Unit == "0/1" || (Signal.Values.Length > 0 && Signal.Values.All(v => double.IsNaN(v) || v == 0 || v == 1));
    }
}
