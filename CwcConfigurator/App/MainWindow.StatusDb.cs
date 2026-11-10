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
            var blocks = new Button { Content = "Block names…", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Type, data block and string-name blocks of this DB" };
            barContent.Children.Add(blocks);
            var unit = new Button { Content = "Defaults from unit", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Fill the rows with the elements and texts of the unit PLC" };
            barContent.Children.Add(unit);
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
            blocks.Click += (_, _) => EditStatusDbBlockNames(entry);
            unit.Click += (_, _) => ApplyStatusDbUnitDefaults(entry);

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
        entry.BlockInfo.Text = $"{configuration.DataBlockName} ({configuration.DataTypeName}) · strings {english.DataBlockName} / {other.DataBlockName}";
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

    private void ApplyStatusDbUnitDefaults(StatusDbTab entry)
    {
        if (MessageBox.Show(this,
                $"Replace PLC names and texts of {entry.Family.DbName} with the {entry.Family.Title.ToLowerInvariant()} of the unit PLC?",
                entry.Family.DbName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        foreach (var row in entry.Family.Rows(_project.DbCatalog))
        {
            row.PlcName = "";
            row.StringNameEnglish = row.StringNameOther = row.NameEnglish = row.NameOther = "";
        }

        StatusDbFamilies.ApplyUnitDefaults(_project.DbCatalog, entry.Family);
        AfterStatusDbEdit(entry);
        ShowMessage($"{entry.Family.DbName}: defaults from the unit PLC.");
    }

    /// <summary>Type, configuration DB and string-name DBs of the family (as in the data-block names of the other DBs).</summary>
    private void EditStatusDbBlockNames(StatusDbTab entry)
    {
        var (configuration, english, other) = StatusDbFamilies.Settings(_project.DbCatalog, entry.Family);
        var panel = new StackPanel { Margin = new Thickness(16) };
        TextBox Field(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var box = new TextBox { Text = value, MinWidth = 360 };
            panel.Children.Add(box);
            return box;
        }

        var type = Field("Structure (PLC data type)", configuration.DataTypeName);
        var block = Field("Data block", configuration.DataBlockName);
        var file = Field("Source file of the type", configuration.FileName);
        var stringType = Field("String-name data type", english.DataTypeName);
        var englishBlock = Field("String-name DB (NAME ENG)", english.DataBlockName);
        var otherBlock = Field("String-name DB (NAME OTHER)", other.DataBlockName);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Save", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = $"{entry.Family.DbName} block names", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = EditorOwner
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        static string Clean(TextBox box, string fallback) => string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim().Trim('"');
        configuration.DataTypeName = Clean(type, entry.Family.TypeName);
        configuration.DataBlockName = Clean(block, entry.Family.DataBlockName);
        configuration.FileName = Clean(file, entry.Family.FileName);
        english.DataTypeName = other.DataTypeName = Clean(stringType, entry.Family.StringTypeName);
        english.DataBlockName = Clean(englishBlock, entry.Family.StringEnglish);
        other.DataBlockName = Clean(otherBlock, entry.Family.StringOther);
        ShowStatusDbBlockInfo(entry);
        ShowMessage($"{entry.Family.DbName}: block names saved.");
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
