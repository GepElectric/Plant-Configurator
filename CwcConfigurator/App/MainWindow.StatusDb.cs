using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>
/// DB CTRL_STS (control status) and DB SEQ_CMD (sequence commands) tabs: the same rows the sequence uses, with the
/// PLC element name, the string-name texts (NAME ENG / OTHER) and the text-list names (TEXT LIST NAME ENG / OTHER).
/// They behave like the other DB tabs: column filters synchronized with the columns, text filter, spreadsheet editing
/// (copy / paste / fill / context menu), INFO, text and PLC-name dialogs, Find / Replace, text export / import.
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
        public required ScrollViewer FilterScroll { get; init; }
        public List<DbFilterSpec> Specs { get; } = [];
        public List<ColumnFilterLayout> Layouts { get; } = [];
        public ICollectionView? View { get; set; }
        public bool LoadingFilters;
    }

    private readonly List<StatusDbTab> _statusDbTabs = [];

    private IEnumerable<DataGrid> StatusDbGrids => _statusDbTabs.Select(tab => tab.Grid);

    private bool IsStatusDbGrid(DataGrid grid) => _statusDbTabs.Any(tab => ReferenceEquals(tab.Grid, grid));

    private StatusDbTab? StatusDbTabOf(DataGrid grid) => _statusDbTabs.FirstOrDefault(tab => ReferenceEquals(tab.Grid, grid));

    /// <summary>Builds both tabs and inserts them after DB PAR (before the main tabs and grid handlers are set up).</summary>
    private void InitializeStatusDbTabs()
    {
        foreach (var family in StatusDbFamilies.All)
        {
            var layout = new DockPanel();
            var bar = new Border { Padding = new Thickness(8), BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE)), BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(bar, Dock.Top);
            var barContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bar.Child = barContent;
            barContent.Children.Add(new TextBlock { Text = $"{family.Title} IDs 0-{family.LastId}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var filter = new TextBox { Width = 180, Height = 26, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Text filter" };
            barContent.Children.Add(filter);
            var clear = new Button { Content = "Clear filter" };
            barContent.Children.Add(clear);
            var xref = new Button { Content = "Refresh XRef", ToolTip = "Scan the control status and sequence command rows in TIA and update the cross-reference usage counts" };
            barContent.Children.Add(xref);
            var info = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            barContent.Children.Add(info);
            var status = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            barContent.Children.Add(status);
            layout.Children.Add(bar);

            // Column filters in a strip above the grid, scrolled and sized with the columns (as in the other DB tabs).
            var filterScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            DockPanel.SetDock(filterScroll, Dock.Top);
            var filterStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            filterScroll.Content = filterStrip;
            layout.Children.Add(filterScroll);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader, SelectionMode = DataGridSelectionMode.Extended
            };
            layout.Children.Add(grid);

            // A TextBlock header: in a plain string header WPF takes "_" as the access key ("DB CTRLSTS").
            var tab = new TabItem { Header = new TextBlock { Text = family.DbName }, Tag = family.DbName, Content = layout };
            var entry = new StatusDbTab { Family = family, Tab = tab, Grid = grid, Filter = filter, StatusText = status, BlockInfo = info, FilterScroll = filterScroll };
            _statusDbTabs.Add(entry);

            var cellStyle = TryFindResource("CenteredCellText") as Style;
            var editStyle = TryFindResource("CenteredEditText") as Style;
            void TextColumn(string header, string path, double width, bool readOnly, string allText, Func<DbStringRow, string> value, bool stretch = true, bool number = false)
            {
                var column = new DataGridTextColumn
                {
                    Header = header, Width = width, IsReadOnly = readOnly, ElementStyle = cellStyle,
                    Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
                };
                if (!readOnly && editStyle is not null) column.EditingElementStyle = editStyle;
                grid.Columns.Add(column);
                var combo = new ComboBox { Width = width };
                combo.SelectionChanged += (_, _) => StatusDbFilterChanged(entry);
                filterStrip.Children.Add(combo);
                entry.Specs.Add(new DbFilterSpec(combo, allText, value, SortAsNumber: number));
                entry.Layouts.Add(new ColumnFilterLayout(column, combo, width, stretch));
            }

            void ButtonColumn(string glyph, bool symbolFont, string toolTip, RoutedEventHandler click)
            {
                var button = new FrameworkElementFactory(typeof(Button));
                button.SetValue(ContentControl.ContentProperty, glyph);
                if (symbolFont) button.SetValue(Control.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
                button.SetValue(FrameworkElement.WidthProperty, 32.0);
                button.SetValue(FrameworkElement.HeightProperty, 26.0);
                button.SetValue(Control.PaddingProperty, new Thickness(0));
                button.SetValue(FrameworkElement.ToolTipProperty, toolTip);
                button.AddHandler(ButtonBase.ClickEvent, click);
                var column = new DataGridTemplateColumn { Header = symbolFont ? "" : "INFO", Width = 52, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = button } };
                grid.Columns.Add(column);
                var spacer = new Border { Width = 52 };
                filterStrip.Children.Add(spacer);
                entry.Layouts.Add(new ColumnFilterLayout(column, spacer, 52, false));
            }

            TextColumn("ID", nameof(DbStringRow.Id), 70, true, "All IDs", row => row.Id.ToString(CultureInfo.InvariantCulture), stretch: false, number: true);
            ButtonColumn("i", false, "Info", family == StatusDbFamilies.ControlStatus ? SequenceControlStatusInfo_Click : SequenceCommandInfo_Click);
            ButtonColumn("", true, "Configure names and text list names", ConfigureStatusDbText_Click);
            // PLC NAME is protected as in the other DBs: changed only through the PLC-definition button (gear).
            ButtonColumn("", true, "Configure PLC variable name", ConfigureStatusDbPlcName_Click);
            TextColumn("PLC NAME", nameof(DbStringRow.PlcName), 200, true, "All PLC names", row => row.PlcName);
            TextColumn("NAME ENG", nameof(DbStringRow.StringNameEnglish), 220, false, "All ENG names", row => row.StringNameEnglish);
            TextColumn("NAME OTHER", nameof(DbStringRow.StringNameOther), 220, false, "All other names", row => row.StringNameOther);
            TextColumn("TEXT LIST NAME ENG", nameof(DbStringRow.NameEnglish), 220, false, "All ENG text list names", row => row.NameEnglish);
            TextColumn("TEXT LIST NAME OTHER", nameof(DbStringRow.NameOther), 220, false, "All other text list names", row => row.NameOther);
            TextColumn("XREF ID", nameof(DbStringRow.CrossReferenceIdCount), 90, true, "All XREF ID", row => row.CrossReferenceIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", stretch: false, number: true);
            TextColumn("XREF VAL", nameof(DbStringRow.CrossReferenceValCount), 90, true, "All XREF VAL", row => row.CrossReferenceValCount?.ToString(CultureInfo.InvariantCulture) ?? "", stretch: false, number: true);

            grid.BeginningEdit += DbGrid_BeginningEdit;
            grid.PreviewKeyDown += DbGrid_PreviewKeyDown;
            grid.CellEditEnding += (_, e) =>
            {
                if (e.EditAction != DataGridEditAction.Commit) return;
                Dispatcher.BeginInvoke(() => AfterStatusDbEdit(entry), System.Windows.Threading.DispatcherPriority.Background);
            };
            grid.SizeChanged += (_, _) => ResizeColumnsToViewport(grid, entry.Layouts);
            grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
            {
                if (Math.Abs(e.HorizontalChange) > 0) filterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            }));
            AttachColumnFilterSync(grid, () => entry.Layouts);

            filter.TextChanged += (_, _) => StatusDbFilterChanged(entry);
            clear.Click += (_, _) =>
            {
                filter.Text = "";
                ClearDbFilters(entry.Specs, ref entry.LoadingFilters, entry.View, () => SortDbViewById(entry.View), () => UpdateStatusDbFilterItems(entry));
            };
            // Control status and sequence commands share their rows with the sequence: its cross-reference scan updates them.
            xref.Click += async (_, _) =>
            {
                await UpdateDbCrossReferenceCountsAsync("DB SEQUENCE");
                RefreshStatusDbTabs();
            };

            var index = MainTabs.Items.OfType<TabItem>().ToList().FindIndex(item => GetTabKey(item).Equals("DB PAR", StringComparison.OrdinalIgnoreCase));
            MainTabs.Items.Insert(index < 0 ? MainTabs.Items.Count : index + _statusDbTabs.Count, tab);
        }
    }

    private void StatusDbFilterChanged(StatusDbTab entry)
    {
        if (entry.LoadingFilters) return;
        UpdateStatusDbFilterItems(entry);
        entry.View?.Refresh();
        UpdateStatusDbGridStatus(entry);
    }

    private void UpdateStatusDbFilterItems(StatusDbTab entry) =>
        UpdateDbFilterItems(entry.Specs, entry.Family.Rows(_project.DbCatalog), ref entry.LoadingFilters);

    private void UpdateStatusDbGridStatus(StatusDbTab entry)
    {
        UpdateFilterVisualIndicators(entry.Specs.Select(spec => spec.Filter), entry.Filter);
        UpdateGridStatus(entry.Family.DbName, entry.View, entry.Family.Rows(_project.DbCatalog).Count);
    }

    private bool FilterStatusDbRow(StatusDbTab entry, object item) =>
        item is DbStringRow row
        && MatchesDbTextFilter(row, entry.Filter.Text, entry.Specs)
        && entry.Specs.All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));

    /// <summary>NAME ENG / OTHER (string-name DBs) and TEXT LIST NAME ENG / OTHER of one row.</summary>
    private void ConfigureStatusDbText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row } button || StatusDbTabOf(FindParent<DataGrid>(button)!) is not { } entry)
        {
            return;
        }

        var fields = new[]
        {
            Field(nameof(DbStringRow.StringNameEnglish), "NAME ENG", row.StringNameEnglish),
            Field(nameof(DbStringRow.StringNameOther), "NAME OTHER", row.StringNameOther),
            Field(nameof(DbStringRow.NameEnglish), "TEXT LIST NAME ENG", row.NameEnglish),
            Field(nameof(DbStringRow.NameOther), "TEXT LIST NAME OTHER", row.NameOther)
        };
        if (!ShowDbStringRowDialog($"{entry.Family.Title} names", row, fields, DbTextLengthSettings.CreateDigitalOutputDefaults(), "DB SEQUENCE"))
        {
            return;
        }

        AfterStatusDbEdit(entry);
        entry.Grid.Items.Refresh();
    }

    /// <summary>The PLC element name of a control status / sequence command, edited in the PLC-name dialog.</summary>
    private void ConfigureStatusDbPlcName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row } button || StatusDbTabOf(FindParent<DataGrid>(button)!) is not { } entry)
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
            entry.View.Filter = item => FilterStatusDbRow(entry, item);
            LoadDbFilterItems(entry.Specs, rows, ref entry.LoadingFilters);
            entry.Grid.ItemsSource = entry.View;
            ShowStatusDbBlockInfo(entry);
        }
    }

    private void ShowStatusDbBlockInfo(StatusDbTab entry)
    {
        var (configuration, english, other) = StatusDbFamilies.Settings(_project.DbCatalog, entry.Family);
        entry.BlockInfo.Text = $"{configuration.DataBlockName} ({configuration.DataTypeName}) · strings {english.DataBlockName} / {other.DataBlockName} · names: Settings → DB Names and Numbers";
    }

    /// <summary>The sequence shows the same rows: its grids, filters and the step dropdowns follow the edit.</summary>
    private void AfterStatusDbEdit(StatusDbTab entry)
    {
        UpdateStatusDbFilterItems(entry);
        entry.View?.Refresh();
        UpdateSequenceReferenceOptions();
        UpdateSequenceControlStatusFilterItems();
        UpdateSequenceCommandFilterItems();
        SequenceControlStatusGrid.Items.Refresh();
        SequenceCommandGrid.Items.Refresh();
    }

    private void RefreshStatusDbTabs()
    {
        foreach (var entry in _statusDbTabs)
        {
            UpdateStatusDbFilterItems(entry);
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
