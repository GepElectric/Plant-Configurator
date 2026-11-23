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
            Text = "Svaki panel je zaseban graf ispod prethodnog, sa svojom vremenskom osi i legendom; zoom, pomak vremena i kursori vrijede za sve panele. Kvačica označava panel u kojem je signal vidljiv (može i u više njih; bez kvačice signal je skriven). Isto vrijedi za izvoz slike i Word izvještaj.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);

        // Names: the whole graph and every panel.
        var names = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        names.ColumnDefinitions.Add(new() { Width = new GridLength(150) }); names.ColumnDefinitions.Add(new());
        TextBox NameRow(string label, string value)
        {
            int r = names.RowDefinitions.Count; names.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(text, r); Grid.SetRow(box, r); Grid.SetColumn(box, 1);
            names.Children.Add(text); names.Children.Add(box); return box;
        }
        var titleBox = NameRow("Naziv grafa", project.Options.Title);
        var panelBoxes = Enumerable.Range(1, 4).Select(p => NameRow($"Naziv panela {p}", project.Options.PanelName(p))).ToArray();
        DockPanel.SetDock(names, Dock.Top); layout.Children.Add(names);

        var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(quick, Dock.Top); layout.Children.Add(quick);
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
        grid.Columns.Add(new DataGridTextColumn { Header = "Signal", Binding = new Binding(nameof(PanelRow.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Jedinica", Binding = new Binding(nameof(PanelRow.Unit)), IsReadOnly = true, Width = 90 });
        // A tick for every panel the signal is shown in (several ticks: the same curve in several graphs; none: hidden).
        for (int p = 1; p <= 4; p++)
        {
            var column = new DataGridCheckBoxColumn { Header = $"Panel {p}", Binding = new Binding($"In{p}") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 70 };
            grid.Columns.Add(column);
        }

        void Set(Func<PanelRow, int> panel) { foreach (var row in rows) row.Only(Math.Clamp(panel(row), 1, 4)); grid.Items.Refresh(); }
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
            Owner = this, Title = "Paneli · više grafova s istom vremenskom osi", Content = layout, Width = Dialogs.Width(760), Height = Dialogs.Height(760),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        Action(buttons, "Odustani", (_, _) => dialog.Close());
        Action(buttons, "Primijeni", (_, _) => Guard(() =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            foreach (var row in rows) row.Apply();
            project.Options.Title = titleBox.Text.Trim();
            project.Options.PanelNames = Enumerable.Range(1, 4).Where(p => panelBoxes[p - 1].Text.Trim().Length > 0).ToDictionary(p => p, p => panelBoxes[p - 1].Text.Trim());
            dialog.Close();
            RebindOptions();   // the options editor shows the new title (Commit reads the editors back)
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
        readonly bool[] panels = Enumerable.Range(1, 4).Select(p => signal.Visible && signal.InPanel(p)).ToArray();
        public bool In1 { get => panels[0]; set => panels[0] = value; }
        public bool In2 { get => panels[1]; set => panels[1] = value; }
        public bool In3 { get => panels[2]; set => panels[2] = value; }
        public bool In4 { get => panels[3]; set => panels[3] = value; }
        public void Only(int panel) { for (int i = 0; i < 4; i++) panels[i] = i + 1 == panel; }
        /// <summary>Ticked panels: the first is the signal's panel, the others show it as well; no tick hides the signal.</summary>
        public void Apply()
        {
            var ticked = Enumerable.Range(1, 4).Where(p => panels[p - 1]).ToList();
            Signal.Visible = ticked.Count > 0;
            if (ticked.Count == 0) return;
            Signal.Panel = ticked[0];
            Signal.AlsoInPanels = ticked.Skip(1).ToList();
        }
        /// <summary>A binary signal: unit 0/1 or only 0 and 1 in the recording.</summary>
        public bool IsDigital => Unit == "0/1" || (Signal.Values.Length > 0 && Signal.Values.All(v => double.IsNaN(v) || v == 0 || v == 1));
    }
}
