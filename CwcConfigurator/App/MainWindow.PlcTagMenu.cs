using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

/// <summary>
/// Right-click on a PLC NAME cell offers a "Copy PLC tag" submenu with the
/// fully-qualified S7 tag and its common sub-fields (.ID / .VAL / .ALM_ID /
/// .R_VAL etc.) so the user can drop the path into TIA, Excel or chat.
/// The set of suffixes is per-DB to match the actual UDT shape.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Class-level hook: every cell's ContextMenuOpening event bubbles up to
    /// the grid. We swap the grid's normal Copy/Paste menu for a PLC-tag menu
    /// when the user right-clicked a PLC NAME cell, then restore on close.
    /// </summary>
    private void DbGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        var cell = FindAutoFillCellFromEvent(grid, e.OriginalSource);
        if (cell?.Column is null || cell.DataContext is null)
        {
            return;
        }

        if (!IsPlcNameColumn(grid, cell.Column))
        {
            return;
        }

        var entries = BuildPlcTagEntries(grid, cell.DataContext).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var (label, tagPath) in entries)
        {
            var item = new MenuItem { Header = label };
            var capturedPath = tagPath;
            item.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(capturedPath);
                    ShowMessage($"Copied: {capturedPath}");
                }
                catch (Exception ex)
                {
                    CwcApplication.LogException("Clipboard.SetText (PLC tag)", ex);
                }
            };
            menu.Items.Add(item);
        }

        // Suppress the grid's normal Copy/Cut/Paste menu so the user sees only
        // the PLC-tag actions on this specific column. The contained menu lives
        // just for this opening — placement target is the clicked cell.
        e.Handled = true;
        menu.PlacementTarget = cell;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static bool IsPlcNameColumnNameMatch(string? property, string expected)
    {
        return property is not null && property.Equals(expected, StringComparison.Ordinal);
    }

    private bool IsPlcNameColumn(DataGrid grid, DataGridColumn column)
    {
        if (ReferenceEquals(column, DbDiPlcNameColumn)) return true;
        if (ReferenceEquals(column, DbDoPlcNameColumn)) return true;
        if (ReferenceEquals(column, DbMeasPlcNameColumn)) return true;
        if (ReferenceEquals(column, DbAoPlcNameColumn)) return true;
        if (ReferenceEquals(column, DbAlmPlcNameColumn)) return true;
        if (ReferenceEquals(column, DbParPlcNameColumn)) return true;
        return false;
    }

    private IEnumerable<(string Label, string TagPath)> BuildPlcTagEntries(DataGrid grid, object rowObj)
    {
        if (ReferenceEquals(grid, DbDiGrid) && rowObj is DbStringRow di)
        {
            var basePath = QuoteAndJoin("DI", di.PlcStructure, di.PlcName);
            yield return ("Copy base tag", basePath);
            yield return ("Copy .ID", $"{basePath}.ID");
            yield return ("Copy .VAL", $"{basePath}.VAL");
        }
        else if (ReferenceEquals(grid, DbDoGrid) && rowObj is DbStringRow doRow)
        {
            // DB DO wraps its IO in a .DATA member; matches the cross-reference
            // path used by the xref scanner in MainWindow.xaml.cs.
            var basePath = QuoteAndJoin("DO", doRow.PlcStructure, doRow.PlcName);
            yield return ("Copy base tag", basePath);
            yield return ("Copy .DATA.ID", $"{basePath}.DATA.ID");
            yield return ("Copy .DATA.VAL", $"{basePath}.DATA.VAL");
        }
        else if (ReferenceEquals(grid, DbMeasGrid) && rowObj is DbStringRow meas)
        {
            var basePath = QuoteAndJoin("MEAS", meas.PlcStructure, meas.PlcName);
            yield return ("Copy base tag", basePath);
            yield return ("Copy .MEAS_ID", $"{basePath}.MEAS_ID");
            yield return ("Copy .VAL", $"{basePath}.VAL");
        }
        else if (ReferenceEquals(grid, DbAoGrid) && rowObj is DbAoRow ao)
        {
            var basePath = QuoteAndJoin("AO", ao.AoSystemName, ao.AoPlcName);
            yield return ("Copy base tag", basePath);
            yield return ("Copy .ANA_CTRL_ID", $"{basePath}.ANA_CTRL_ID");
            yield return ("Copy .VAL", $"{basePath}.VAL");
        }
        else if (ReferenceEquals(grid, DbAlmGrid) && rowObj is DbStringRow alm)
        {
            var basePath = QuoteAndJoin("ALM", alm.PlcStructure, alm.PlcName);
            yield return ("Copy base tag", basePath);
            yield return ("Copy .ALM_ID", $"{basePath}.ALM_ID");
            yield return ("Copy .ALM", $"{basePath}.ALM");
            yield return ("Copy .TEMP", $"{basePath}.TEMP");
        }
        else if (ReferenceEquals(grid, DbParGrid) && rowObj is DbStringRow par)
        {
            var basePath = QuoteAndJoin("PAR", par.PlcStructure, par.PlcName);
            yield return ("Copy base tag", basePath);
            yield return ("Copy .ID", $"{basePath}.ID");
            yield return ("Copy .R_VAL", $"{basePath}.R_VAL");
            yield return ("Copy .I_VAL", $"{basePath}.I_VAL");
            yield return ("Copy .T_VAL", $"{basePath}.T_VAL");
        }
    }

    /// <summary>
    /// Formats a Siemens S7 absolute reference: <c>"DB".STRUCT.MEMBER</c>.
    /// Quotes go around the DB name so paste lands as-is in TIA Portal.
    /// Empty parts are skipped (e.g. unconfigured rows) so we never emit
    /// stray dots like <c>"PAR"..</c>.
    /// </summary>
    private static string QuoteAndJoin(string dbName, params string?[] members)
    {
        var quoted = $"\"{dbName}\"";
        var tail = string.Join(".", members
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));
        return string.IsNullOrEmpty(tail) ? quoted : $"{quoted}.{tail}";
    }
}
