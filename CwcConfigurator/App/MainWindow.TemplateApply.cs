using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

/// <summary>
/// Bulk apply/remove of description templates from the ALM/PAR grid context
/// menu. Works on the current cell/row selection.
/// </summary>
public partial class MainWindow
{
    private void DbAlmApplyTemplate_Click(object sender, RoutedEventArgs e)
    {
        ApplyTemplateToSelection(DbAlmGrid, "ALM", "DB ALM");
    }

    private void DbParApplyTemplate_Click(object sender, RoutedEventArgs e)
    {
        ApplyTemplateToSelection(DbParGrid, "PAR", "DB PAR");
    }

    private void DbAlmRemoveTemplate_Click(object sender, RoutedEventArgs e)
    {
        RemoveTemplateFromSelection(DbAlmGrid, "DB ALM");
    }

    private void DbParRemoveTemplate_Click(object sender, RoutedEventArgs e)
    {
        RemoveTemplateFromSelection(DbParGrid, "DB PAR");
    }

    private static List<DbStringRow> GetSelectedStringRows(DataGrid grid)
    {
        return grid.SelectedCells
            .Select(cell => cell.Item)
            .OfType<DbStringRow>()
            .Distinct()
            .ToList();
    }

    private void ApplyTemplateToSelection(DataGrid grid, string scope, string sectionName)
    {
        var rows = GetSelectedStringRows(grid);
        if (rows.Count == 0)
        {
            ShowMessage("Select one or more rows first.");
            return;
        }

        _project.DescriptionTemplates ??= new DescriptionTemplateCatalog();
        var picker = new DescriptionTemplatePickerDialog(_project.DescriptionTemplates, scope)
        {
            Owner = EditorOwner
        };
        if (picker.ShowDialog() != true || picker.SelectedTemplate is not { } template)
        {
            return;
        }

        RecordUndoSnapshot();
        foreach (var row in rows)
        {
            row.DescriptionTemplateKey = template.Key;
            row.DescriptionTemplateOptOut = false;
            row.DescriptionDirty = false;
        }

        ApplyDescriptionTemplatesAndRefresh();
        MarkTiaSectionState(sectionName, "Differs", $"{sectionName} descriptions changed (template '{template.Key}').");
        ShowMessage($"Template '{template.Key}' applied to {rows.Count} row(s).");
    }

    private void RemoveTemplateFromSelection(DataGrid grid, string sectionName)
    {
        var rows = GetSelectedStringRows(grid)
            .Where(row => !string.IsNullOrWhiteSpace(row.DescriptionTemplateKey))
            .ToList();
        if (rows.Count == 0)
        {
            ShowMessage("No selected row uses a description template.");
            return;
        }

        RecordUndoSnapshot();
        var templates = _project.DescriptionTemplates?.Templates ?? [];
        foreach (var row in rows)
        {
            var template = templates.FirstOrDefault(item =>
                item.Key.Trim().Equals(row.DescriptionTemplateKey.Trim(), StringComparison.OrdinalIgnoreCase));
            // Materialise the full text so unlinking never loses the
            // description (the cell held only the key until now).
            if (template is not null)
            {
                row.DescriptionEnglish = template.TextEnglish ?? "";
                row.DescriptionOther = template.TextOther ?? "";
            }

            row.DescriptionTemplateKey = "";
            row.DescriptionTemplateOptOut = true;
            row.DescriptionDirty = true;
        }

        ApplyDescriptionTemplatesAndRefresh();
        MarkTiaSectionState(sectionName, "Differs", $"{sectionName} descriptions changed (template removed).");
        ShowMessage($"Template removed from {rows.Count} row(s); full text kept.");
    }
}
