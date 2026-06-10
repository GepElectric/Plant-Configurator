using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using CwcConfigurator.Services.Validation;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    private List<ProblemsRow> _allProblemsRows = [];
    // (item, property) pairs that the validator flagged. Used by the cell
    // template's IsProblemCell trigger to draw a persistent red border on
    // each problematic cell — survives virtualization, scroll, sort.
    private readonly HashSet<(object Item, string Property)> _problemCellAnchors = new();

    private void PopulateProblemsPane(IReadOnlyList<ProjectIssue> issues)
    {
        if (ProblemsList is null)
        {
            return;
        }

        _allProblemsRows = issues.Select(ProblemsRow.From).ToList();
        ApplyProblemsFilter();
        RebuildProblemCellAnchors(issues);
        ApplyProblemCellMarkersToAllGrids();

        if (issues.Count == 0)
        {
            ProblemsCountText.Text = "No issues.";
            ProblemsPane.Visibility = Visibility.Collapsed;
            return;
        }

        var errorCount = issues.Count(issue => issue.Severity == "ERROR");
        var warningCount = issues.Count(issue => issue.Severity == "WARNING");
        ProblemsCountText.Text = $"{errorCount} error(s), {warningCount} warning(s)";
        ProblemsPane.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Builds the (item, property) set the cell-marker triggers read from.
    /// Resolves each issue's table + Id to the actual row object so we can
    /// match it against grid items at render time.
    /// </summary>
    private void RebuildProblemCellAnchors(IReadOnlyList<ProjectIssue> issues)
    {
        _problemCellAnchors.Clear();
        foreach (var issue in issues)
        {
            if (issue.FieldNames is not { Count: > 0 })
            {
                continue;
            }

            var row = ResolveProblemRow(issue);
            if (row is null)
            {
                continue;
            }

            foreach (var field in issue.FieldNames)
            {
                _problemCellAnchors.Add((row, field));
            }
        }
    }

    private object? ResolveProblemRow(ProjectIssue issue)
    {
        if (issue.Id is not int id)
        {
            return null;
        }

        return issue.Table switch
        {
            "DI" => _project.DigitalInputs.FirstOrDefault(r => r.Id == id),
            "DO" => _project.DigitalOutputs.FirstOrDefault(r => r.Id == id),
            "AI" => _project.AnalogInputs.FirstOrDefault(r => r.Id == id),
            "RTD" => _project.RtdInputs.FirstOrDefault(r => r.Id == id),
            "COUNTER" => _project.Counters.FirstOrDefault(r => r.Id == id),
            "AO" => _project.AnalogOutputs.FirstOrDefault(r => r.Id == id),
            "DB_DI" => _project.DbCatalog.DiRows.FirstOrDefault(r => r.Id == id),
            "DB_DO" => _project.DbCatalog.DoRows.FirstOrDefault(r => r.Id == id),
            "DB_MEAS" => _project.DbCatalog.MeasRows.FirstOrDefault(r => r.Id == id),
            "DB_AO" => _project.DbCatalog.AoRows.FirstOrDefault(r => r.AoNumber == id),
            "DB_ALM" => _project.DbCatalog.AlarmRows.FirstOrDefault(r => r.Id == id),
            "DB_PAR" => _project.DbCatalog.ParameterRows.FirstOrDefault(r => r.Id == id),
            _ => null
        };
    }

    private void ApplyProblemCellMarkersToAllGrids()
    {
        foreach (var grid in GetSpreadsheetLikeGrids())
        {
            ApplyProblemCellMarkers(grid);
        }
    }

    /// <summary>
    /// Walks the realised rows of a grid and toggles the IsProblemCell
    /// attached property on each cell so the red-border template trigger
    /// stays in sync with the validator anchors.
    /// </summary>
    private void ApplyProblemCellMarkers(DataGrid grid)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in grid.Items)
            {
                if (ReferenceEquals(item, System.Windows.Data.CollectionView.NewItemPlaceholder))
                {
                    continue;
                }

                if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
                {
                    continue;
                }

                foreach (var column in grid.Columns)
                {
                    if (column.GetCellContent(row)?.Parent is not DataGridCell cell)
                    {
                        continue;
                    }

                    var property = GetDbColumnProperty(column);
                    var marked = property is not null && _problemCellAnchors.Contains((item, property));
                    SpreadsheetCellMarker.SetIsProblemCell(cell, marked);
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ProblemsFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyProblemsFilter();
    }

    private void ApplyProblemsFilter()
    {
        if (ProblemsList is null)
        {
            return;
        }

        IEnumerable<ProblemsRow> visible = _allProblemsRows;
        if (ProblemsFilterErrors?.IsChecked == true)
        {
            visible = visible.Where(row => row.Severity == "ERROR");
        }
        else if (ProblemsFilterWarnings?.IsChecked == true)
        {
            visible = visible.Where(row => row.Severity == "WARNING");
        }

        ProblemsList.ItemsSource = visible.ToList();
    }

    private void ProblemsHide_Click(object sender, RoutedEventArgs e)
    {
        ProblemsPane.Visibility = Visibility.Collapsed;
    }

    private void ProblemsCopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_allProblemsRows.Count == 0)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, _allProblemsRows.Select(row => row.Issue.ToString()));
        try
        {
            Clipboard.SetText(text);
            ShowMessage("Issues copied to clipboard.");
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Clipboard.SetText", ex);
        }
    }

    private void ProblemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        JumpToSelectedProblem();
    }

    private void ProblemsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            JumpToSelectedProblem();
            e.Handled = true;
        }
    }

    private void JumpToSelectedProblem()
    {
        if (ProblemsList.SelectedItem is not ProblemsRow row)
        {
            return;
        }

        var (tab, grid) = ResolveTabAndGridFor(row.Issue.Table);
        if (tab is null || grid is null)
        {
            return;
        }

        MainTabs.SelectedItem = tab;
        // Smart slicer switching: only override the user's current slicer if
        // the problem cells would otherwise be hidden. If every cell flagged
        // by this issue is already visible, leave the view alone — useful when
        // the user is browsing problems and doesn't want their layout yanked.
        if (!AreAllProblemCellsVisible(grid, row.Issue.FieldNames))
        {
            SwitchSlicerToHint(row.Issue.Table, row.Issue.SlicerHint ?? "Repair");
        }
        var item = FindRowForIssue(row.Issue);
        if (item is null)
        {
            return;
        }

        grid.UpdateLayout();
        grid.SelectedItem = item;
        grid.ScrollIntoView(item);
        grid.Focus();

        Dispatcher.BeginInvoke(() => FlashProblemLocation(grid, item, row.Issue.FieldNames),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Briefly highlights the problematic location so the user can spot it
    /// after navigating from the Problems pane. When the issue knows which
    /// fields it relates to, flashes those individual cells; otherwise falls
    /// back to flashing the whole row.
    /// </summary>
    private static void FlashProblemLocation(DataGrid grid, object item, IReadOnlyList<string>? fieldNames)
    {
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
        {
            return;
        }

        var cells = fieldNames is { Count: > 0 }
            ? ResolveCellsForFields(grid, row, fieldNames)
            : new List<DataGridCell>();

        if (cells.Count > 0)
        {
            foreach (var cell in cells)
            {
                FlashElementBackground(cell);
            }
            return;
        }

        FlashElementBackground(row);
    }

    private static List<DataGridCell> ResolveCellsForFields(DataGrid grid, DataGridRow row, IReadOnlyList<string> fieldNames)
    {
        var presenter = FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter is null)
        {
            return new List<DataGridCell>();
        }

        var fieldSet = new HashSet<string>(fieldNames, StringComparer.Ordinal);
        var result = new List<DataGridCell>();
        foreach (var column in grid.Columns)
        {
            var property = GetDbColumnProperty(column);
            if (property is null || !fieldSet.Contains(property))
            {
                continue;
            }

            if (presenter.ItemContainerGenerator.ContainerFromIndex(column.DisplayIndex) is DataGridCell cell)
            {
                result.Add(cell);
            }
        }
        return result;
    }

    private static void FlashElementBackground(Control element)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x9A));
        element.Background = brush;
        var animation = new ColorAnimation
        {
            From = Color.FromRgb(0xFF, 0xF1, 0x9A),
            To = Color.FromArgb(0, 0xFF, 0xF1, 0x9A),
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            AutoReverse = false
        };
        animation.Completed += (_, _) => element.ClearValue(Control.BackgroundProperty);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    /// <summary>
    /// Returns true when every column corresponding to the issue's field names
    /// is currently Visible on the grid. Used to decide whether to override the
    /// user's slicer selection during Problems-pane navigation.
    /// </summary>
    private static bool AreAllProblemCellsVisible(DataGrid grid, IReadOnlyList<string>? fieldNames)
    {
        if (fieldNames is null || fieldNames.Count == 0)
        {
            // No field info — we don't know what to look for, so let the
            // legacy "switch to slicer hint" path run.
            return false;
        }

        var fieldSet = new HashSet<string>(fieldNames, StringComparer.Ordinal);
        foreach (var column in grid.Columns)
        {
            var property = GetDbColumnProperty(column);
            if (property is null || !fieldSet.Contains(property))
            {
                continue;
            }

            if (column.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Activate the named slicer item on the table's column slicer; if not
    /// found, fall back to "Repair" so the user still sees something useful.
    /// Non-DB tabs are no-ops.
    /// </summary>
    private void SwitchSlicerToHint(string table, string hint)
    {
        var slicer = table switch
        {
            "DB_DI"   => DbDiColumnSlicer,
            "DB_DO"   => DbDoColumnSlicer,
            "DB_MEAS" => DbMeasColumnSlicer,
            "DB_AO"   => DbAoColumnSlicer,
            "DB_ALM"  => DbAlmColumnSlicer,
            "DB_PAR"  => DbParColumnSlicer,
            _ => null
        };

        if (slicer is null)
        {
            return;
        }

        var target = FindSlicerItem(slicer, hint) ?? FindSlicerItem(slicer, "Repair");
        if (target is null || target.IsSelected)
        {
            return;
        }

        slicer.SelectedItems.Clear();
        target.IsSelected = true;
    }

    private static ListBoxItem? FindSlicerItem(ListBox slicer, string content)
    {
        foreach (var entry in slicer.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(entry.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    private (TabItem? Tab, DataGrid? Grid) ResolveTabAndGridFor(string table)
    {
        return table switch
        {
            "DI" => (DiTab, DiGrid),
            "DO" => (DoTab, DoGrid),
            "AI" => (AiTab, AiGrid),
            "RTD" => (RtdTab, RtdGrid),
            "COUNTER" => (CounterTab, CounterGrid),
            "AO" => (AoTab, AoGrid),
            "DB_DI" => (DbDiTab, DbDiGrid),
            "DB_DO" => (DbDoTab, DbDoGrid),
            "DB_MEAS" => (DbMeasTab, DbMeasGrid),
            "DB_AO" => (DbAoTab, DbAoGrid),
            "DB_ALM" => (DbAlmTab, DbAlmGrid),
            "DB_PAR" => (DbParTab, DbParGrid),
            _ => (null, null)
        };
    }

    private object? FindRowForIssue(ProjectIssue issue)
    {
        // Issues with an explicit Id (e.g. duplicate-Id reports) locate the first row
        // bearing that Id rather than a row at issue.ModuleNumber/Port.
        if (issue.Id is int id)
        {
            return issue.Table switch
            {
                "DI" => _project.DigitalInputs.FirstOrDefault(r => r.Id == id && r.Config != SignalConfigState.Disabled),
                "DO" => _project.DigitalOutputs.FirstOrDefault(r => r.Id == id && r.Config != SignalConfigState.Disabled),
                "AI" => _project.AnalogInputs.FirstOrDefault(r => r.Id == id && r.Config != SignalConfigState.Disabled),
                "RTD" => _project.RtdInputs.FirstOrDefault(r => r.Id == id && r.Config != SignalConfigState.Disabled),
                "COUNTER" => _project.Counters.FirstOrDefault(r => r.Id == id && r.Config != SignalConfigState.Disabled),
                "AO" => _project.AnalogOutputs.FirstOrDefault(r => r.Id == id && r.Config != SignalConfigState.Disabled),
                "DB_DI" => _project.DbCatalog.DiRows.FirstOrDefault(r => r.Id == id),
                "DB_DO" => _project.DbCatalog.DoRows.FirstOrDefault(r => r.Id == id),
                "DB_MEAS" => _project.DbCatalog.MeasRows.FirstOrDefault(r => r.Id == id),
                "DB_AO" => _project.DbCatalog.AoRows.FirstOrDefault(r => r.AoNumber == id),
                "DB_ALM" => _project.DbCatalog.AlarmRows.FirstOrDefault(r => r.Id == id),
                "DB_PAR" => _project.DbCatalog.ParameterRows.FirstOrDefault(r => r.Id == id),
                _ => null
            };
        }

        return issue.Table switch
        {
            "DI" => _project.DigitalInputs.FirstOrDefault(r => r.ModuleNumber == issue.ModuleNumber && (issue.Port is null || r.Port == issue.Port)),
            "DO" => _project.DigitalOutputs.FirstOrDefault(r => r.ModuleNumber == issue.ModuleNumber && (issue.Port is null || r.Port == issue.Port)),
            "AI" => _project.AnalogInputs.FirstOrDefault(r => r.ModuleNumber == issue.ModuleNumber && (issue.Port is null || r.Port == issue.Port)),
            "RTD" => _project.RtdInputs.FirstOrDefault(r => r.ModuleNumber == issue.ModuleNumber && (issue.Port is null || r.Port == issue.Port)),
            "COUNTER" => _project.Counters.FirstOrDefault(r => r.ModuleNumber == issue.ModuleNumber),
            "AO" => _project.AnalogOutputs.FirstOrDefault(r => r.ModuleNumber == issue.ModuleNumber && (issue.Port is null || r.Port == issue.Port)),
            "DB_DI" => _project.DbCatalog.DiRows.FirstOrDefault(r => r.Id == issue.ModuleNumber),
            "DB_DO" => _project.DbCatalog.DoRows.FirstOrDefault(r => r.Id == issue.ModuleNumber),
            "DB_MEAS" => _project.DbCatalog.MeasRows.FirstOrDefault(r => r.Id == issue.ModuleNumber),
            "DB_AO" => _project.DbCatalog.AoRows.FirstOrDefault(r => r.AoNumber == issue.ModuleNumber),
            "DB_ALM" => _project.DbCatalog.AlarmRows.FirstOrDefault(r => r.Id == issue.ModuleNumber),
            "DB_PAR" => _project.DbCatalog.ParameterRows.FirstOrDefault(r => r.Id == issue.ModuleNumber),
            _ => null
        };
    }

    private sealed class ProblemsRow
    {
        public ProblemsRow(ProjectIssue issue, string @ref)
        {
            Issue = issue;
            Severity = issue.Severity;
            Table = issue.Table;
            Ref = @ref;
            Message = issue.Message;
        }

        public ProjectIssue Issue { get; }
        public string Severity { get; }
        public string Table { get; }
        public string Ref { get; }
        public string Message { get; }

        public static ProblemsRow From(ProjectIssue issue)
        {
            string reference;
            if (issue.Id is int id)
            {
                reference = $"ID {id}";
            }
            else if (issue.Table.StartsWith("DB_", StringComparison.OrdinalIgnoreCase))
            {
                reference = $"ID {issue.ModuleNumber}";
            }
            else if (issue.Table == "PROJECT")
            {
                reference = "";
            }
            else
            {
                reference = issue.Port is null
                    ? $"M{issue.ModuleNumber}"
                    : $"M{issue.ModuleNumber}/P{issue.Port}";
            }

            return new ProblemsRow(issue, reference);
        }
    }
}
