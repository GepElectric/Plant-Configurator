using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

/// <summary>
/// Helpers for "Excel-like" bulk operations on DataGrids (copy/paste/cut/fill/clear).
/// Centralizes commit-edit-before-bulk, refresh safety, and pasted value coercion
/// so individual grid handlers stay small and consistent.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<DataGridColumn, string> _columnValidationKeys = new();

    /// <summary>
    /// Commits any in-flight cell/row edit so a subsequent Refresh() won't throw
    /// "Refresh is not allowed during an AddNew or EditItem transaction".
    /// Safe to call even when nothing is being edited.
    /// </summary>
    private static void CommitActiveEdits(DataGrid grid)
    {
        try
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch
        {
            // Best-effort; if commit fails the caller's try/catch around refresh
            // will still protect against a crash.
        }
    }

    /// <summary>
    /// Wraps a refresh-style call in try/catch so a stray EditItem transaction
    /// or virtualization race never propagates to the user as a fatal dialog.
    /// </summary>
    private static void SafeRefresh(Action refresh)
    {
        try
        {
            refresh();
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Grid refresh", ex);
        }
    }

    private void RegisterValidationColumn(DataGridColumn column, string validationKey)
    {
        _columnValidationKeys[column] = validationKey;
    }

    /// <summary>
    /// Returns the text-length settings for a DB grid, or null if the grid
    /// has no per-column length rules (signal grids, sequence grids, etc.).
    /// </summary>
    private DbTextLengthSettings? GetTextLengthSettingsForGrid(DataGrid grid)
    {
        if (ReferenceEquals(grid, DbDiGrid)) return _project.DbCatalog.DiTextLengths;
        if (ReferenceEquals(grid, DbDoGrid)) return _project.DbCatalog.DoTextLengths;
        if (ReferenceEquals(grid, DbMeasGrid)) return _project.DbCatalog.MeasTextLengths;
        if (ReferenceEquals(grid, DbAoGrid)) return _project.DbCatalog.AoTextLengths;
        if (ReferenceEquals(grid, DbAlmGrid)) return _project.DbCatalog.AlarmTextLengths;
        if (ReferenceEquals(grid, DbParGrid)) return _project.DbCatalog.ParameterTextLengths;
        return null;
    }

    /// <summary>
    /// Applies length + validation-list coercion to a raw pasted value.
    /// Returns the value to actually store, or null if the paste must be skipped.
    /// On rejection, fills <paramref name="rejection"/> with a user-facing reason.
    /// </summary>
    private string? CoercePastedValue(DataGrid grid, DataGridColumn column, object row, string raw, out string? rejection)
    {
        rejection = null;
        var propertyName = GetDbColumnProperty(column);

        // 1) Validation-list (combo) columns: accept either the raw Value or the
        // formatted Text; reject everything else so we don't silently insert
        // garbage that later breaks TIA push.
        if (_columnValidationKeys.TryGetValue(column, out var validationKey))
        {
            var options = GetValidationOptions(validationKey);
            if (options.Count > 0)
            {
                var trimmed = (raw ?? "").Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    return trimmed; // empty paste clears combo — allowed
                }

                var match = options.FirstOrDefault(option =>
                    option.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                    || option.Text.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    rejection = $"\"{trimmed}\" is not in allowed values for {ColumnDisplayName(column)}";
                    return null;
                }

                return match.Value;
            }
        }

        // 2) Length-limited text columns: reject if the paste exceeds the per-grid limit.
        var settings = GetTextLengthSettingsForGrid(grid);
        if (settings is not null && propertyName is not null)
        {
            int? max = row is DbAoRow
                ? GetDbAoMaxTextLength(propertyName, settings)
                : GetDbMaxTextLength(propertyName, settings);
            if (max.HasValue && (raw?.Length ?? 0) > max.Value)
            {
                rejection = $"\"{ColumnDisplayName(column)}\" max {max.Value} chars (got {raw!.Length})";
                return null;
            }
        }

        return raw;
    }

    private static string ColumnDisplayName(DataGridColumn column)
    {
        return column.Header?.ToString() ?? GetDbColumnProperty(column) ?? "(column)";
    }

    /// <summary>
    /// Double-click on the resize gripper between two column headers — Excel
    /// convention for "fit column width to widest content". Switching width
    /// to <see cref="DataGridLengthUnitType.Auto"/> tells WPF to measure both
    /// header and visible cells.
    /// </summary>
    private static void EnableAutoFitOnHeaderGripper(DataGrid grid)
    {
        grid.AddHandler(Thumb.MouseDoubleClickEvent,
            new MouseButtonEventHandler((sender, args) =>
            {
                if (args.OriginalSource is not Thumb thumb)
                {
                    return;
                }

                var header = FindVisualParent<DataGridColumnHeader>(thumb);
                if (header?.Column is null)
                {
                    return;
                }

                AutoFitColumnWidth(header.Column);
                args.Handled = true;
            }),
            handledEventsToo: true);
    }

    private static void AutoFitColumnWidth(DataGridColumn column)
    {
        column.Width = DataGridLength.Auto;
    }

    /// <summary>
    /// Builds and attaches a context menu to a spreadsheet-like grid with
    /// the standard Excel-ish actions (Copy/Cut/Paste/Clear/Fill down).
    /// </summary>
    private void AttachSpreadsheetContextMenu(DataGrid grid)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => RefreshSpreadsheetContextMenu(grid, menu);

        menu.Items.Add(BuildContextItem("Copy", "Ctrl+C", () => CopyDbGridSelectionToClipboard(grid), () => grid.SelectedCells.Any(IsValidGridCell)));
        menu.Items.Add(BuildContextItem("Cut", "Ctrl+X", () => CutDbGridSelectionToClipboard(grid), () => grid.SelectedCells.Any(IsValidGridCell)));
        menu.Items.Add(BuildContextItem("Paste", "Ctrl+V", () => PasteDbGridClipboard(grid), () => Clipboard.ContainsText() && grid.SelectedCells.Any(IsValidGridCell)));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildContextItem("Clear", "Del", () => ClearDbGridSelection(grid), () => grid.SelectedCells.Any(c => IsValidGridCell(c) && IsEditableSpreadsheetColumn(grid, c.Column))));
        menu.Items.Add(BuildContextItem("Fill down", "Ctrl+D", () => FillDbGridSelectionDown(grid), () => grid.SelectedCells.Where(IsValidGridCell).Count() > 1));

        grid.ContextMenu = menu;
    }

    private static MenuItem BuildContextItem(string text, string gesture, Action invoke, Func<bool> canExecute)
    {
        var item = new MenuItem { Header = text, InputGestureText = gesture, Tag = canExecute };
        item.Click += (_, _) => invoke();
        return item;
    }

    /// <summary>
    /// Re-evaluates IsEnabled for each context menu item just before the menu
    /// is shown. Avoids storing per-item state and lets us reflect transient
    /// conditions (clipboard, selection size, editable column).
    /// </summary>
    private static void RefreshSpreadsheetContextMenu(DataGrid grid, ContextMenu menu)
    {
        foreach (var item in menu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Tag is Func<bool> canExecute)
            {
                try
                {
                    menuItem.IsEnabled = canExecute();
                }
                catch
                {
                    menuItem.IsEnabled = false;
                }
            }
        }
    }

    /// <summary>
    /// Updates the bottom status bar with a brief selection summary (count,
    /// sum/avg for numeric cells). Suppressed when there is no meaningful
    /// selection so the status bar isn't noisy for single clicks.
    /// </summary>
    private void UpdateSelectionStatus(DataGrid grid)
    {
        if (StatusSelectionText is null)
        {
            return;
        }

        var cells = grid.SelectedCells.Where(IsValidGridCell).ToList();
        if (cells.Count <= 1)
        {
            StatusSelectionText.Text = "";
            return;
        }

        var distinctRows = cells.Select(c => c.Item).Distinct().Count();
        var distinctCols = cells.Select(c => c.Column).Distinct().Count();

        double sum = 0;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        int numericCount = 0;
        foreach (var cell in cells)
        {
            var text = GetDbGridCellText(cell.Item, cell.Column);
            if (double.TryParse(text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                sum += value;
                if (value < min) min = value;
                if (value > max) max = value;
                numericCount++;
            }
        }

        var baseText = $"Sel: {cells.Count} cells ({distinctRows}×{distinctCols})";
        if (numericCount >= 2)
        {
            var avg = sum / numericCount;
            StatusSelectionText.Text = $"{baseText}  |  Σ {sum:0.###}  ⌀ {avg:0.###}  min {min:0.###}  max {max:0.###}";
        }
        else
        {
            StatusSelectionText.Text = baseText;
        }
    }

    /// <summary>
    /// When the view sorts/filters/scrolls in a way that drops the items
    /// we marked, the dashed outline can become "phantom" — anchored to
    /// rows the user can no longer see. Clear it on these transitions.
    /// </summary>
    private void ClearMarchingAntsOnViewMutation(DataGrid grid)
    {
        if (!ReferenceEquals(grid, _clipboardSourceGrid))
        {
            return;
        }

        ClearClipboardMarchingAnts();
    }

    /// <summary>
    /// True when the grid's view has at least one active SortDescription.
    /// Used to warn the user before paste so they understand row order may
    /// differ from the underlying model.
    /// </summary>
    private static bool IsGridViewSorted(DataGrid grid)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        return view?.SortDescriptions.Count > 0;
    }

    // Colors used by filter visual indicators. Active filter = blue accent
    // matching the existing Excel-like green-on-selection palette.
    private static readonly Brush ActiveFilterBorderBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x69, 0xDA));
    private static readonly Brush ActiveFilterBackgroundBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xEE, 0xFF));

    /// <summary>
    /// Highlights every filter combo whose selection is not the "All" reset
    /// item, so an active filter is visible at a glance even when the user
    /// has scrolled past the column horizontally.
    /// </summary>
    private static void UpdateFilterVisualIndicators(IEnumerable<ComboBox> filters, TextBox? textFilter = null)
    {
        foreach (var filter in filters)
        {
            var active = filter.SelectedIndex > 0;
            if (active)
            {
                filter.BorderBrush = ActiveFilterBorderBrush;
                filter.Background = ActiveFilterBackgroundBrush;
            }
            else
            {
                filter.ClearValue(Control.BorderBrushProperty);
                filter.ClearValue(Control.BackgroundProperty);
            }
        }

        if (textFilter is not null)
        {
            var active = !string.IsNullOrWhiteSpace(textFilter.Text);
            if (active)
            {
                textFilter.BorderBrush = ActiveFilterBorderBrush;
                textFilter.Background = ActiveFilterBackgroundBrush;
            }
            else
            {
                textFilter.ClearValue(Control.BorderBrushProperty);
                textFilter.ClearValue(Control.BackgroundProperty);
            }
        }
    }

    /// <summary>
    /// Refreshes filter visual indicators (border + background on active
    /// filters) and the bottom-status row count / sort indicator for whichever
    /// DB tab is currently visible. Cheap to call on every filter/sort change.
    /// </summary>
    private void RefreshActiveTabFilterUi()
    {
        if (MainTabs?.SelectedItem is not TabItem tab)
        {
            return;
        }

        switch (GetTabKey(tab))
        {
            case "DB DI":
                UpdateFilterVisualIndicators(GetDbDiFilterSpecs().Select(s => s.Filter), DbDiTextFilter);
                UpdateGridStatus("DB DI", _dbDiView, _project.DbCatalog.DiRows.Count);
                break;
            case "DB DO":
                UpdateFilterVisualIndicators(GetDbDoFilterSpecs().Select(s => s.Filter), DbDoTextFilter);
                UpdateGridStatus("DB DO", _dbDoView, _project.DbCatalog.DoRows.Count);
                break;
            case "DB MEAS":
                UpdateFilterVisualIndicators(GetDbMeasFilterSpecs().Select(s => s.Filter), DbMeasTextFilter);
                UpdateGridStatus("DB MEAS", _dbMeasView, _project.DbCatalog.MeasRows.Count);
                break;
            case "DB AO":
                UpdateFilterVisualIndicators(GetDbAoFilterSpecs().Select(s => s.Filter), DbAoTextFilter);
                UpdateGridStatus("DB AO", _dbAoRowsView, _project.DbCatalog.AoRows.Count);
                break;
            case "DB ALM":
                UpdateFilterVisualIndicators(GetDbAlmFilterSpecs().Select(s => s.Filter), DbAlmTextFilter);
                UpdateGridStatus("DB ALM", _dbAlmView, _project.DbCatalog.AlarmRows.Count);
                break;
            case "DB PAR":
                UpdateFilterVisualIndicators(GetDbParFilterSpecs().Select(s => s.Filter), DbParTextFilter);
                UpdateGridStatus("DB PAR", _dbParView, _project.DbCatalog.ParameterRows.Count);
                break;
        }
    }

    /// <summary>
    /// Updates the status bar with shown/total row counts plus active sort
    /// (if any) for the grid the user is currently viewing. Run after every
    /// refresh of the underlying collection view.
    /// </summary>
    private void UpdateGridStatus(string tableName, ICollectionView? view, int totalRowCount)
    {
        if (StatusRowCountsText is null)
        {
            return;
        }

        if (view is null)
        {
            StatusRowCountsText.Text = totalRowCount > 0 ? $"{tableName}: {totalRowCount} rows" : "";
            return;
        }

        var shown = view.Cast<object>().Count();
        var prefix = $"{tableName}: ";
        var counts = shown == totalRowCount
            ? $"{totalRowCount} rows"
            : $"{shown} of {totalRowCount} rows (filtered)";

        var sort = view.SortDescriptions.Count > 0
            ? $"   |   Sort: {string.Join(", ", view.SortDescriptions.Select(s => $"{s.PropertyName} {(s.Direction == System.ComponentModel.ListSortDirection.Ascending ? "↑" : "↓")}"))}"
            : "";

        StatusRowCountsText.Text = prefix + counts + sort;
    }
}
