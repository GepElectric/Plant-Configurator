using ClosedXML.Excel;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class DescriptionTemplateWorkbookService
{
    private const string SheetName = "DescriptionTemplates";

    public void Export(DescriptionTemplateCatalog catalog, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName);
        var headers = new[] { "KEY", "SCOPE", "TEXT ENGLISH", "TEXT OTHER", "NOTES" };
        for (var index = 0; index < headers.Length; index++)
        {
            sheet.Cell(1, index + 1).Value = headers[index];
        }

        var rowNumber = 2;
        foreach (var template in catalog.Templates.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            sheet.Cell(rowNumber, 1).Value = template.Key;
            sheet.Cell(rowNumber, 2).Value = template.Scope;
            sheet.Cell(rowNumber, 3).Value = template.TextEnglish;
            sheet.Cell(rowNumber, 4).Value = template.TextOther;
            sheet.Cell(rowNumber, 5).Value = template.Notes;
            rowNumber++;
        }

        sheet.Range(1, 1, Math.Max(1, rowNumber - 1), headers.Length).CreateTable("DESCRIPTION_TEMPLATES");
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    public int Import(DescriptionTemplateCatalog catalog, string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.FirstOrDefault(item => item.Name.Equals(SheetName, StringComparison.OrdinalIgnoreCase))
                    ?? workbook.Worksheets.First();
        var headers = ReadHeaders(sheet);
        var keyColumn = FindHeader(headers, "KEY", "Key") ?? throw new InvalidOperationException("Template workbook must contain KEY column.");
        var scopeColumn = FindHeader(headers, "SCOPE", "Scope");
        var englishColumn = FindHeader(headers, "TEXT ENGLISH", "English", "ENG");
        var otherColumn = FindHeader(headers, "TEXT OTHER", "Other", "Other language");
        var notesColumn = FindHeader(headers, "NOTES", "Notes");

        catalog.Templates ??= [];
        var changed = 0;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            var key = sheet.Cell(row, keyColumn).GetString().Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var template = catalog.Templates.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (template is null)
            {
                template = new DescriptionTemplateEntry { Key = key };
                catalog.Templates.Add(template);
                changed++;
            }

            changed += SetIfDifferent(template.Scope, scopeColumn is null ? null : sheet.Cell(row, scopeColumn.Value).GetString(), value => template.Scope = NormalizeScope(value));
            changed += SetIfDifferent(template.TextEnglish, englishColumn is null ? null : sheet.Cell(row, englishColumn.Value).GetString(), value => template.TextEnglish = value);
            changed += SetIfDifferent(template.TextOther, otherColumn is null ? null : sheet.Cell(row, otherColumn.Value).GetString(), value => template.TextOther = value);
            changed += SetIfDifferent(template.Notes, notesColumn is null ? null : sheet.Cell(row, notesColumn.Value).GetString(), value => template.Notes = value);
        }

        catalog.Templates = catalog.Templates
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return changed;
    }

    private static int SetIfDifferent(string current, string? incoming, Action<string> set)
    {
        if (incoming is null || string.Equals(current ?? "", incoming, StringComparison.Ordinal))
        {
            return 0;
        }

        set(incoming);
        return 1;
    }

    private static string NormalizeScope(string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "ALM" => "ALM",
            "PAR" => "PAR",
            _ => "Both"
        };
    }

    private static Dictionary<string, int> ReadHeaders(IXLWorksheet sheet)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastColumn = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = sheet.Cell(1, column).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(header) && !headers.ContainsKey(header))
            {
                headers.Add(header, column);
            }
        }

        return headers;
    }

    private static int? FindHeader(IReadOnlyDictionary<string, int> headers, params string[] names)
    {
        foreach (var name in names)
        {
            if (headers.TryGetValue(name, out var column))
            {
                return column;
            }
        }

        return null;
    }
}
