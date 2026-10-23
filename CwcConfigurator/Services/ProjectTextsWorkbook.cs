using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>
/// The xlsx of TIA "Export project texts" for text lists that are library types. Each row is one entry text:
/// ViewPath <c>...\&lt;type&gt;\V x.y.z\&lt;list&gt;\Text_list_entry_N\Text</c>, where N = ID + 1, and one column per
/// TIA language (the editing language is marked with "*", e.g. "en-US*").
/// </summary>
public static class ProjectTextsWorkbook
{
    private static readonly Regex EntryPath = new(@"\\([^\\]+)\\V [\d.]+\\[^\\]+\\Text_list_entry_(\d+)\\Text$", RegexOptions.Compiled);
    private static readonly Regex LanguageHeader = new(@"^[a-z]{2,3}-[A-Za-z]{2,4}\*?$", RegexOptions.Compiled);

    /// <summary>TIA languages of the workbook, the editing language ("*") first.</summary>
    public static List<string> Languages(string path)
    {
        using var workbook = new XLWorkbook(path);
        return ReadColumns(workbook.Worksheets.First(), out _)
            .OrderBy(column => column.Value.Editing ? 0 : 1)
            .Select(column => column.Value.Language)
            .ToList();
    }

    /// <summary>All texts of the workbook as text-list data: list = type name, ID = entry number - 1.</summary>
    public static DescriptionTextListData Read(string path)
    {
        var data = new DescriptionTextListData { SourcePath = path };
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.First();
        var columns = ReadColumns(sheet, out var pathColumn);
        data.Languages = columns.Values.Select(column => column.Language).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase).ToList();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            var match = EntryPath.Match(sheet.Cell(row, pathColumn).GetString());
            if (!match.Success)
            {
                continue;
            }

            var id = int.Parse(match.Groups[2].Value) - 1;
            foreach (var (column, info) in columns)
            {
                data.SetText(match.Groups[1].Value, id, info.Language, sheet.Cell(row, column).GetString());
            }
        }

        data.AllListNames = data.Lists.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        return data;
    }

    /// <summary>Drops every language except <paramref name="languages"/> (English first, then the other language).</summary>
    public static void KeepLanguages(DescriptionTextListData data, IReadOnlyCollection<string> languages)
    {
        foreach (var byLanguage in data.Lists.Values)
        {
            foreach (var language in byLanguage.Keys.Where(language => !languages.Contains(language, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                byLanguage.Remove(language);
            }
        }

        data.Languages = data.Languages.Where(language => languages.Contains(language, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Counts (and with <paramref name="save"/> writes) the texts that differ from the app's texts, per type.
    /// <paramref name="sources"/> maps a TIA language (column) to the app language whose texts go there; other columns
    /// and texts the app does not have stay as exported.</summary>
    public static Dictionary<string, int> Apply(string path, DescriptionTextListData appData, IReadOnlyDictionary<string, string> sources, bool save)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.First();
        var columns = ReadColumns(sheet, out var pathColumn);
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            var match = EntryPath.Match(sheet.Cell(row, pathColumn).GetString());
            if (!match.Success || !appData.Lists.TryGetValue(match.Groups[1].Value, out var byLanguage))
            {
                continue;
            }

            var listName = match.Groups[1].Value;
            var id = int.Parse(match.Groups[2].Value) - 1;
            result.TryAdd(listName, 0);
            foreach (var (column, info) in columns)
            {
                if (!sources.TryGetValue(info.Language, out var appLanguage)
                    || !byLanguage.TryGetValue(appLanguage, out var byId)
                    || !byId.TryGetValue(id, out var text))
                {
                    continue;
                }

                var cell = sheet.Cell(row, column);
                if (!string.Equals(cell.GetString(), text ?? "", StringComparison.Ordinal))
                {
                    cell.SetValue(text ?? "");
                    result[listName]++;
                }
            }
        }

        if (save)
        {
            var customProperties = ReadEntry(path, CustomPropertiesEntry);
            workbook.Save();
            if (customProperties is not null)
            {
                WriteEntry(path, CustomPropertiesEntry, customProperties);
            }
        }

        return result;
    }

    // TIA checks the custom property TIA_Version (text "1.3"); ClosedXML rewrites it as a number with the current
    // culture ("13"), and TIA then rejects the import ("Invalid XLSX version"). The exported part is put back as is.
    private const string CustomPropertiesEntry = "docProps/custom.xml";

    private static byte[]? ReadEntry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteEntry(string path, string name, byte[] content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    private static Dictionary<int, (string Language, bool Editing)> ReadColumns(IXLWorksheet sheet, out int pathColumn)
    {
        var columns = new Dictionary<int, (string Language, bool Editing)>();
        pathColumn = 0;
        foreach (var cell in sheet.Row(1).CellsUsed())
        {
            var name = cell.GetString().Trim();
            if (name.Equals("ViewPath", StringComparison.OrdinalIgnoreCase))
            {
                pathColumn = cell.Address.ColumnNumber;
            }
            else if (LanguageHeader.IsMatch(name))
            {
                columns[cell.Address.ColumnNumber] = (name.TrimEnd('*'), name.EndsWith('*'));
            }
        }

        if (pathColumn == 0 || columns.Count == 0)
        {
            throw new InvalidOperationException("The exported project texts have no ViewPath or language columns.");
        }

        return columns;
    }
}
