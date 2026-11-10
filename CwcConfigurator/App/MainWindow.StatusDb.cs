using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>
/// DB CTRL_STS (control status) and DB SEQ_CMD (sequence commands) tabs: the same rows the sequence uses, with the
/// PLC element name, the string-name texts (NAME ENG / OTHER) and the text-list names (TEXT LIST NAME ENG / OTHER).
/// </summary>
public partial class MainWindow
{
    private sealed class StatusDbTab
    {
        public required StatusDbFamilies.Family Family { get; init; }
        public required TabItem Tab { get; init; }
        public required DataGrid Grid { get; init; }
        public required TextBox Filter { get; init; }
        public required TextBlock StatusText { get; init; }
        public required TextBlock BlockInfo { get; init; }
        public ICollectionView? View { get; set; }
    }

    private readonly List<StatusDbTab> _statusDbTabs = [];

    /// <summary>Builds both tabs and inserts them after DB PAR (before the main tabs are captured).</summary>
    private void InitializeStatusDbTabs()
    {
        foreach (var family in StatusDbFamilies.All)
        {
            var layout = new DockPanel();
            var bar = new Border { Padding = new Thickness(8), BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE)), BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(bar, Dock.Top);
            var barContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bar.Child = barContent;
            barContent.Children.Add(new TextBlock
            {
                Text = $"{family.Title} IDs 0-{family.LastId}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
            });
            var filter = new TextBox { Width = 180, Height = 26, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Text filter" };
            barContent.Children.Add(filter);
            var clear = new Button { Content = "Clear filter", Margin = new Thickness(6, 0, 0, 0) };
            barContent.Children.Add(clear);
            var xref = new Button { Content = "Refresh XRef", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Scan the control status and sequence command rows in TIA and update the cross-reference usage counts" };
            barContent.Children.Add(xref);
            var info = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            barContent.Children.Add(info);
            var status = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            barContent.Children.Add(status);
            layout.Children.Add(bar);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader, SelectionMode = DataGridSelectionMode.Extended
            };
            DataGridTextColumn Column(string header, string path, double width, bool readOnly = false) => new()
            {
                Header = header, Width = width, IsReadOnly = readOnly,
                Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            };
            grid.Columns.Add(Column("ID", nameof(DbStringRow.Id), 70, readOnly: true));
            // PLC NAME is protected as in the other DBs: changed only through the PLC-definition button (gear).
            var plcButton = new FrameworkElementFactory(typeof(Button));
            plcButton.SetValue(ContentControl.ContentProperty, "");
            plcButton.SetValue(Control.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
            plcButton.SetValue(FrameworkElement.WidthProperty, 32.0);
            plcButton.SetValue(FrameworkElement.HeightProperty, 26.0);
            plcButton.SetValue(Control.PaddingProperty, new Thickness(0));
            plcButton.SetValue(FrameworkElement.ToolTipProperty, "Configure PLC variable name");
            plcButton.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ConfigureStatusDbPlcName_Click));
            grid.Columns.Add(new DataGridTemplateColumn { Header = "", Width = 52, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = plcButton } });
            grid.Columns.Add(Column("PLC NAME", nameof(DbStringRow.PlcName), 220, readOnly: true));
            grid.Columns.Add(Column("NAME ENG", nameof(DbStringRow.StringNameEnglish), 240));
            grid.Columns.Add(Column("NAME OTHER", nameof(DbStringRow.StringNameOther), 240));
            grid.Columns.Add(Column("TEXT LIST NAME ENG", nameof(DbStringRow.NameEnglish), 240));
            grid.Columns.Add(Column("TEXT LIST NAME OTHER", nameof(DbStringRow.NameOther), 240));
            grid.Columns.Add(Column("XREF ID", nameof(DbStringRow.CrossReferenceIdCount), 90, readOnly: true));
            grid.Columns.Add(Column("XREF VAL", nameof(DbStringRow.CrossReferenceValCount), 90, readOnly: true));
            layout.Children.Add(grid);

            // A TextBlock header: in a plain string header WPF takes "_" as the access key ("DB CTRLSTS").
            var tab = new TabItem { Header = new TextBlock { Text = family.DbName }, Tag = family.DbName, Content = layout };
            var entry = new StatusDbTab { Family = family, Tab = tab, Grid = grid, Filter = filter, StatusText = status, BlockInfo = info };
            _statusDbTabs.Add(entry);

            filter.TextChanged += (_, _) => entry.View?.Refresh();
            clear.Click += (_, _) => filter.Text = "";
            grid.CellEditEnding += (_, e) =>
            {
                if (e.EditAction != DataGridEditAction.Commit) return;
                Dispatcher.BeginInvoke(() => AfterStatusDbEdit(entry), System.Windows.Threading.DispatcherPriority.Background);
            };
            // Control status and sequence commands share their rows with the sequence: its cross-reference scan updates them.
            xref.Click += async (_, _) =>
            {
                await UpdateDbCrossReferenceCountsAsync("DB SEQUENCE");
                RefreshStatusDbTabs();
            };

            var index = MainTabs.Items.OfType<TabItem>().ToList().FindIndex(item => GetTabKey(item).Equals("DB PAR", StringComparison.OrdinalIgnoreCase));
            MainTabs.Items.Insert(index < 0 ? MainTabs.Items.Count : index + 1 + _statusDbTabs.Count - 1, tab);
        }
    }

    /// <summary>The PLC element name of a control status / sequence command, edited in the PLC-name dialog.</summary>
    private void ConfigureStatusDbPlcName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row } button
            || _statusDbTabs.FirstOrDefault(tab => ReferenceEquals(tab.Grid, FindParent<DataGrid>(button))) is not { } entry)
        {
            return;
        }

        var fields = new[] { Field(nameof(DbStringRow.PlcName), "PLC NAME", row.PlcName) };
        if (!ShowDbStringRowDialog($"{entry.Family.Title} PLC name", row, fields, DbTextLengthSettings.CreateParameterDefaults(), "DB SEQUENCE"))
        {
            return;
        }

        row.PlcStructure = "";
        AfterStatusDbEdit(entry);
        entry.Grid.Items.Refresh();
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T found) return found;
        }

        return null;
    }

    private StatusDbTab? StatusDbTabFor(string dbName) =>
        StatusDbFamilies.Find(dbName) is { } family ? _statusDbTabs.FirstOrDefault(tab => tab.Family == family) : null;

    private void BindStatusDbTabs()
    {
        StatusDbFamilies.EnsureDefaults(_project.DbCatalog);
        foreach (var entry in _statusDbTabs)
        {
            var rows = entry.Family.Rows(_project.DbCatalog);
            entry.View = CollectionViewSource.GetDefaultView(rows);
            entry.View.Filter = item => item is DbStringRow row && MatchesStatusDbFilter(row, entry.Filter.Text);
            entry.Grid.ItemsSource = entry.View;
            ShowStatusDbBlockInfo(entry);
        }
    }

    private static bool MatchesStatusDbFilter(DbStringRow row, string filter)
    {
        var text = filter.Trim();
        return text.Length == 0 || new[] { row.Id.ToString(), row.PlcName, row.StringNameEnglish, row.StringNameOther, row.NameEnglish, row.NameOther }
            .Any(value => value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowStatusDbBlockInfo(StatusDbTab entry)
    {
        var (configuration, english, other) = StatusDbFamilies.Settings(_project.DbCatalog, entry.Family);
        entry.BlockInfo.Text = $"{configuration.DataBlockName} ({configuration.DataTypeName}) · strings {english.DataBlockName} / {other.DataBlockName} · names: Settings → DB Names and Numbers";
    }

    /// <summary>The sequence shows the same rows: its grids and the step dropdowns follow the edit.</summary>
    private void AfterStatusDbEdit(StatusDbTab entry)
    {
        entry.View?.Refresh();
        UpdateSequenceReferenceOptions();
        SequenceControlStatusGrid.Items.Refresh();
        SequenceCommandGrid.Items.Refresh();
    }

    private void RefreshStatusDbTabs()
    {
        foreach (var entry in _statusDbTabs)
        {
            entry.View?.Refresh();
        }
    }

    private async Task<bool> RunStatusDbToolbarAsync(string dbName, bool push)
    {
        if (StatusDbFamilies.Find(dbName) is not { } family)
        {
            return false;
        }

        if (push && MessageBox.Show(this,
                $"Push {family.DbName} will generate {family.DataBlockName}, its type and string-name DBs and import them into the mapped TIA PLC (created there if missing).",
                $"Push {family.DbName}", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return true;
        }

        await RunDbFamilyTiaOperationAsync(
            family.DbName,
            push ? $"Pushing {family.DbName} to TIA..." : $"Pulling {family.DbName} from TIA...",
            service => push ? service.PushStatusDbAsync(_project, family) : service.PullStatusDbAsync(_project, family),
            rebindAfterSuccess: !push,
            progressTitle: $"{family.DbName} TIA {(push ? "push" : "pull")}",
            progressStatus: push ? $"Generating {family.DbName} sources and importing them into TIA..." : $"Exporting {family.DbName} from TIA...");
        return true;
    }
}
