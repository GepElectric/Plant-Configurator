using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CwcConfigurator.App;

/// <summary>
/// Find &amp; Replace bar logic. Operates on the DataGrid that backs the
/// currently active main tab — works the same way for DI/DO/AI/RTD/COUNTER/AO/
/// DEVICES signal grids and every DB grid. Searches across all cells, including
/// read-only ones; replacement skips read-only cells with a status message so
/// users aren't silently no-op'd.
/// </summary>
public partial class MainWindow
{
    private (DataGrid Grid, object Item, DataGridColumn Column)? _lastFindMatch;

    private void OpenFindBar(bool replaceMode)
    {
        if (FindReplaceBar is null)
        {
            return;
        }

        FindReplaceBar.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = replaceMode ? Visibility.Visible : Visibility.Collapsed;
        if (ToggleReplaceModeButton is not null)
        {
            ToggleReplaceModeButton.IsChecked = replaceMode;
        }

        // If the user had a cell selected, pre-fill find input with its text
        // so the common "find this value" workflow is one keystroke faster.
        var grid = ResolveActiveGrid();
        if (grid is not null && string.IsNullOrEmpty(FindInput.Text))
        {
            var cell = grid.SelectedCells.FirstOrDefault();
            if (cell.IsValid && cell.Item is not null)
            {
                var existing = GetDbGridCellText(cell.Item, cell.Column);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    FindInput.Text = existing;
                }
            }
        }

        FindInput.Focus();
        FindInput.SelectAll();
        UpdateFindMatchCount();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    private void ToggleReplaceMode_Click(object sender, RoutedEventArgs e)
    {
        var show = ToggleReplaceModeButton?.IsChecked == true;
        ReplaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            ReplaceInput.Focus();
            ReplaceInput.SelectAll();
        }
    }

    private void CloseFindBar()
    {
        if (FindReplaceBar is null)
        {
            return;
        }

        FindReplaceBar.Visibility = Visibility.Collapsed;
        _lastFindMatch = null;
    }

    private void FindInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFindMatchCount();
    }

    private void FindInput_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    StepFind(forward: false);
                }
                else
                {
                    StepFind(forward: true);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFindBar();
                e.Handled = true;
                break;
        }
    }

    private void ReplaceInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void FindOptions_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFindMatchCount();
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => StepFind(forward: true);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => StepFind(forward: false);

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindInput.Text) || _lastFindMatch is null)
        {
            StepFind(forward: true);
            return;
        }

        var (grid, item, column) = _lastFindMatch.Value;
        if (!IsEditableSpreadsheetColumn(grid, column))
        {
            ShowMessage("Cell is read-only — skipping.");
            StepFind(forward: true);
            return;
        }

        CommitActiveEdits(grid);
        RecordUndoSnapshot();
        var current = GetDbGridCellText(item, column);
        var replaced = ReplaceMatched(current, FindInput.Text, ReplaceInput.Text);
        if (!string.Equals(replaced, current, StringComparison.Ordinal))
        {
            TrySetDbGridCellValue(item, column, replaced);
            SafeRefresh(() => RefreshAfterDbGridExcelEdit(grid));
        }

        StepFind(forward: true);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var grid = ResolveActiveGrid();
        if (grid is null || string.IsNullOrEmpty(FindInput.Text))
        {
            return;
        }

        CommitActiveEdits(grid);
        RecordUndoSnapshot();

        var rows = GetDbGridVisibleRows(grid);
        var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        var skippedReadOnly = 0;
        var replaced = 0;

        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                var text = GetDbGridCellText(row, column);
                if (!FindTextContains(text, FindInput.Text))
                {
                    continue;
                }

                if (!IsEditableSpreadsheetColumn(grid, column))
                {
                    skippedReadOnly++;
                    continue;
                }

                var newText = ReplaceMatched(text, FindInput.Text, ReplaceInput.Text);
                if (!string.Equals(newText, text, StringComparison.Ordinal)
                    && TrySetDbGridCellValue(row, column, newText))
                {
                    replaced++;
                }
            }
        }

        if (replaced > 0)
        {
            SafeRefresh(() => RefreshAfterDbGridExcelEdit(grid));
        }

        var summary = $"Replaced {replaced} match(es).";
        if (skippedReadOnly > 0)
        {
            summary += $" {skippedReadOnly} read-only cell(s) skipped.";
        }
        ShowMessage(summary);
        UpdateFindMatchCount();
    }

    private void StepFind(bool forward)
    {
        var grid = ResolveActiveGrid();
        if (grid is null || string.IsNullOrEmpty(FindInput.Text))
        {
            return;
        }

        var rows = GetDbGridVisibleRows(grid);
        if (rows.Count == 0)
        {
            return;
        }

        var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        var positions = new List<(int RowIndex, DataGridColumn Column)>(rows.Count * columns.Count);
        foreach (var (row, rowIndex) in rows.Select((r, i) => (r, i)))
        {
            foreach (var column in columns)
            {
                positions.Add((rowIndex, column));
            }
        }

        var startIndex = FindStartIndex(grid, rows, positions, forward);
        var found = FindNextMatch(positions, rows, startIndex, forward);
        if (found is null)
        {
            ShowMessage("No more matches.");
            return;
        }

        var (rowIdx, col) = found.Value;
        var item = rows[rowIdx];
        grid.SelectedCells.Clear();
        grid.SelectedCells.Add(new DataGridCellInfo(item, col));
        grid.CurrentCell = new DataGridCellInfo(item, col);
        grid.ScrollIntoView(item, col);
        _lastFindMatch = (grid, item, col);
        UpdateFindMatchCount();
    }

    private int FindStartIndex(DataGrid grid, List<object> rows, List<(int RowIndex, DataGridColumn Column)> positions, bool forward)
    {
        if (_lastFindMatch is { } prev && ReferenceEquals(prev.Grid, grid))
        {
            var prevIdx = positions.FindIndex(p => ReferenceEquals(rows[p.RowIndex], prev.Item) && ReferenceEquals(p.Column, prev.Column));
            if (prevIdx >= 0)
            {
                return forward ? prevIdx + 1 : prevIdx - 1;
            }
        }

        var currentCell = grid.CurrentCell;
        if (currentCell.IsValid && currentCell.Item is not null)
        {
            var idx = positions.FindIndex(p => ReferenceEquals(rows[p.RowIndex], currentCell.Item) && ReferenceEquals(p.Column, currentCell.Column));
            if (idx >= 0)
            {
                return forward ? idx + 1 : idx - 1;
            }
        }

        return forward ? 0 : positions.Count - 1;
    }

    private (int RowIndex, DataGridColumn Column)? FindNextMatch(List<(int RowIndex, DataGridColumn Column)> positions, List<object> rows, int start, bool forward)
    {
        var needle = FindInput.Text;
        var count = positions.Count;
        if (count == 0)
        {
            return null;
        }

        // Wrap-around scan: visit every position once starting from `start`.
        for (var step = 0; step < count; step++)
        {
            var idx = forward
                ? (start + step + count) % count
                : (start - step + count) % count;
            var (rowIndex, column) = positions[idx];
            var text = GetDbGridCellText(rows[rowIndex], column);
            if (FindTextContains(text, needle))
            {
                return (rowIndex, column);
            }
        }
        return null;
    }

    private bool FindTextContains(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return false;
        }

        var comparison = FindMatchCaseToggle.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (FindWholeWordToggle.IsChecked == true)
        {
            return IsWholeWordMatch(haystack, needle, comparison);
        }

        return haystack.IndexOf(needle, comparison) >= 0;
    }

    private static bool IsWholeWordMatch(string haystack, string needle, StringComparison comparison)
    {
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, comparison)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var afterPos = index + needle.Length;
            var after = afterPos >= haystack.Length || !char.IsLetterOrDigit(haystack[afterPos]);
            if (before && after)
            {
                return true;
            }
            index++;
        }
        return false;
    }

    private string ReplaceMatched(string source, string needle, string replacement)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return source;
        }

        var comparison = FindMatchCaseToggle.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (FindWholeWordToggle.IsChecked == true)
        {
            return ReplaceWholeWord(source, needle, replacement, comparison);
        }

        // Manual replace using IndexOf so case-insensitive replace preserves the
        // existing match-case semantics. string.Replace overload with comparison
        // exists in .NET 8 but using IndexOf keeps the code symmetric with Find.
        var builder = new System.Text.StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            var match = source.IndexOf(needle, index, comparison);
            if (match < 0)
            {
                builder.Append(source, index, source.Length - index);
                break;
            }
            builder.Append(source, index, match - index);
            builder.Append(replacement);
            index = match + needle.Length;
        }
        return builder.ToString();
    }

    private static string ReplaceWholeWord(string source, string needle, string replacement, StringComparison comparison)
    {
        var builder = new System.Text.StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            var match = source.IndexOf(needle, index, comparison);
            if (match < 0)
            {
                builder.Append(source, index, source.Length - index);
                break;
            }

            var before = match == 0 || !char.IsLetterOrDigit(source[match - 1]);
            var afterPos = match + needle.Length;
            var after = afterPos >= source.Length || !char.IsLetterOrDigit(source[afterPos]);
            if (before && after)
            {
                builder.Append(source, index, match - index);
                builder.Append(replacement);
                index = match + needle.Length;
            }
            else
            {
                builder.Append(source, index, match - index + 1);
                index = match + 1;
            }
        }
        return builder.ToString();
    }

    private void UpdateFindMatchCount()
    {
        if (FindMatchCountText is null)
        {
            return;
        }

        var grid = ResolveActiveGrid();
        if (grid is null || string.IsNullOrEmpty(FindInput.Text))
        {
            FindMatchCountText.Text = "";
            return;
        }

        var rows = GetDbGridVisibleRows(grid);
        var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        var count = 0;
        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                if (FindTextContains(GetDbGridCellText(row, column), FindInput.Text))
                {
                    count++;
                }
            }
        }

        FindMatchCountText.Text = count == 0 ? "No results" : $"{count} match(es)";
    }

    private DataGrid? ResolveActiveGrid()
    {
        if (MainTabs?.SelectedItem is not TabItem tab)
        {
            return null;
        }

        return GetTabKey(tab) switch
        {
            "DI" => DiGrid,
            "DO" => DoGrid,
            "AI" => AiGrid,
            "RTD" => RtdGrid,
            "COUNTER" => CounterGrid,
            "AO" => AoGrid,
            "DEVICES" => DevicesGrid,
            "DB DI" => DbDiGrid,
            "DB DO" => DbDoGrid,
            "DB MEAS" => DbMeasGrid,
            "DB AO" => DbAoGrid,
            "DB ALM" => DbAlmGrid,
            "DB PAR" => DbParGrid,
            _ => null
        };
    }
}
