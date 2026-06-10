using System.Windows;
using System.Windows.Controls;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    private DataGrid? _clipboardSourceGrid;
    private readonly HashSet<(object Item, string Property)> _clipboardCellAnchors = new();

    /// <summary>
    /// Records the cells currently held in the OS clipboard so they can be drawn
    /// with marching-ants until the user pastes, edits, or presses Esc.
    /// </summary>
    private void MarkClipboardSelection(DataGrid grid, IEnumerable<(object Item, string Property)> anchors)
    {
        ClearClipboardMarchingAnts();

        _clipboardSourceGrid = grid;
        foreach (var anchor in anchors)
        {
            _clipboardCellAnchors.Add(anchor);
        }

        RefreshClipboardCellMarkers();
    }

    private void ClearClipboardMarchingAnts()
    {
        if (_clipboardSourceGrid is null && _clipboardCellAnchors.Count == 0)
        {
            return;
        }

        var grid = _clipboardSourceGrid;
        _clipboardCellAnchors.Clear();
        _clipboardSourceGrid = null;

        if (grid is null)
        {
            return;
        }

        foreach (var item in grid.Items)
        {
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
            {
                continue;
            }

            foreach (var column in grid.Columns)
            {
                if (column.GetCellContent(row)?.Parent is DataGridCell cell)
                {
                    SpreadsheetCellMarker.SetIsClipboardCopied(cell, false);
                }
            }
        }
    }

    private void RefreshClipboardCellMarkers()
    {
        if (_clipboardSourceGrid is null || _clipboardCellAnchors.Count == 0)
        {
            return;
        }

        var grid = _clipboardSourceGrid;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in grid.Items)
            {
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

                    var property = GetDbColumnPropertyForCell(column);
                    var isCopied = property is not null && _clipboardCellAnchors.Contains((item, property));
                    SpreadsheetCellMarker.SetIsClipboardCopied(cell, isCopied);
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string? GetDbColumnPropertyForCell(DataGridColumn column)
    {
        return GetDbColumnProperty(column);
    }
}
