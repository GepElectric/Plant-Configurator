using ClosedXML.Excel;
using System.IO;
using System.Globalization;
using CwcConfigurator.Domain;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CwcConfigurator.Services;

public sealed class DescriptionTextListService
{
    public DescriptionTextListData ImportWorkbook(string path)
    {
        using var workbook = new XLWorkbook(path);
        var worksheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.Equals("TextListEntry", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Workbook does not contain required 'TextListEntry' sheet.");
        var headers = ReadHeaderMap(worksheet);
        var parentColumn = FindHeader(headers, "Parent")
                           ?? throw new InvalidOperationException("TextListEntry sheet does not contain 'Parent' column.");
        var valueColumn = FindHeader(headers, "Value")
                          ?? throw new InvalidOperationException("TextListEntry sheet does not contain 'Value' column.");
        var languageColumns = FindLanguageColumns(worksheet);
        if (languageColumns.Count == 0)
        {
            throw new InvalidOperationException("TextListEntry sheet does not contain any 'Text [language]' columns.");
        }

        var result = new DescriptionTextListData { SourcePath = path };
        result.Languages = languageColumns.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            var listName = worksheet.Cell(row, parentColumn).GetString().Trim();
            if (string.IsNullOrWhiteSpace(listName))
            {
                continue;
            }

            if (!int.TryParse(worksheet.Cell(row, valueColumn).GetString().Trim(), out var id))
            {
                continue;
            }

            foreach (var (column, language) in languageColumns)
            {
                var text = worksheet.Cell(row, column).GetString();
                if (string.Equals(text, "<No value>", StringComparison.OrdinalIgnoreCase))
                {
                    text = "";
                }

                if (!string.IsNullOrEmpty(text))
                {
                    result.SetText(listName, id, language, text);
                }

                result.SetReference(listName, id, language, row, column);
            }
        }

        result.AllListNames = result.Lists.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        ReadTextListComments(workbook, result);
        return result;
    }

    public int ApplyDescriptionsToProject(CwcProject project)
    {
        project.DescriptionTextLists ??= new DescriptionTextListConfig();
        project.DescriptionTextLists.SyncWithDatabases(project.PlcDatabases);
        var changed = 0;
        var languages = GetLanguagePair(project.DescriptionTextLists.TextLists);
        foreach (var database in project.PlcDatabases)
        {
            EnsureTextLists(database.DbCatalog);
            var mapping = project.DescriptionTextLists.GetOrCreate(database.Id, database.Name);
            changed += ApplyRows(
                database.DbCatalog.AlarmRows,
                mapping.AlmDescriptionsList,
                languages,
                project.DescriptionTextLists.TextLists);
            changed += ApplyRows(
                database.DbCatalog.ParameterRows,
                mapping.ParDescriptionsList,
                languages,
                project.DescriptionTextLists.TextLists);
            changed += ApplyNameRows(
                database.DbCatalog.Sequence.ControlStatusRows,
                mapping.SequenceControlStatusList,
                languages,
                project.DescriptionTextLists.TextLists);
            changed += ApplyNameRows(
                database.DbCatalog.Sequence.CommandRows,
                mapping.SequenceCommandList,
                languages,
                project.DescriptionTextLists.TextLists);
            changed += ApplyNameRows(database.DbCatalog.TextLists.AlmBlockingClassRows, mapping.AlmBlockingClassList, languages, project.DescriptionTextLists.TextLists);
            changed += ApplyNameRows(database.DbCatalog.TextLists.CabinetRows, mapping.CabinetsList, languages, project.DescriptionTextLists.TextLists);
            changed += ApplyNameRows(database.DbCatalog.TextLists.PortConfigurationRows, mapping.PortConfigurationList, languages, project.DescriptionTextLists.TextLists);
            changed += ApplyNameRows(database.DbCatalog.TextLists.SeqCmdRows, mapping.SeqCmdList, languages, project.DescriptionTextLists.TextLists);
            changed += ApplyNameRows(database.DbCatalog.TextLists.SeqConfigRows, mapping.SeqConfigList, languages, project.DescriptionTextLists.TextLists);
        }

        changed += new DescriptionTemplateService().ApplyTemplates(project);
        return changed;
    }

    public int PushDescriptionsIntoTextLists(CwcProject project)
    {
        project.DescriptionTextLists ??= new DescriptionTextListConfig();
        project.DescriptionTextLists.SyncWithDatabases(project.PlcDatabases);
        var data = project.DescriptionTextLists.TextLists;
        var languages = GetLanguagePair(data);
        var templateService = new DescriptionTemplateService();
        var changed = 0;
        foreach (var database in project.PlcDatabases)
        {
            EnsureTextLists(database.DbCatalog);
            var mapping = project.DescriptionTextLists.GetOrCreate(database.Id, database.Name);
            changed += PushRows(database.DbCatalog.AlarmRows, mapping.AlmDescriptionsList, languages, data, templateService, project.DescriptionTemplates, "ALM");
            changed += PushRows(database.DbCatalog.ParameterRows, mapping.ParDescriptionsList, languages, data, templateService, project.DescriptionTemplates, "PAR");
            changed += PushNameRows(database.DbCatalog.Sequence.ControlStatusRows, mapping.SequenceControlStatusList, languages, data);
            changed += PushNameRows(database.DbCatalog.Sequence.CommandRows, mapping.SequenceCommandList, languages, data);
            changed += PushNameRows(database.DbCatalog.TextLists.AlmBlockingClassRows, mapping.AlmBlockingClassList, languages, data);
            changed += PushNameRows(database.DbCatalog.TextLists.CabinetRows, mapping.CabinetsList, languages, data);
            changed += PushNameRows(database.DbCatalog.TextLists.PortConfigurationRows, mapping.PortConfigurationList, languages, data);
            changed += PushNameRows(database.DbCatalog.TextLists.SeqCmdRows, mapping.SeqCmdList, languages, data);
            changed += PushNameRows(database.DbCatalog.TextLists.SeqConfigRows, mapping.SeqConfigList, languages, data);
        }

        return changed;
    }

    public void SaveWorkbook(CwcProject project, string path)
    {
        if (project.DescriptionTextLists?.TextLists is null)
        {
            throw new InvalidOperationException("No description text lists are loaded.");
        }

        var data = project.DescriptionTextLists.TextLists;
        EnsureExportReferences(project);

        using var document = SpreadsheetDocument.Open(path, true);
        var workbookPart = document.WorkbookPart
                           ?? throw new InvalidOperationException("Workbook does not contain a workbook part.");
        var sheet = workbookPart.Workbook.Sheets?
            .Elements<Sheet>()
            .FirstOrDefault(item => item.Name?.Value?.Equals("TextListEntry", StringComparison.OrdinalIgnoreCase) == true)
                    ?? throw new InvalidOperationException("Workbook does not contain required 'TextListEntry' sheet.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);

        foreach (var (listName, languageMap) in data.Lists)
        {
            foreach (var (language, idMap) in languageMap)
            {
                foreach (var (id, text) in idMap)
                {
                    if (data.TryGetReference(listName, id, language, out var reference))
                    {
                        SetCellText(workbookPart, worksheetPart, reference.Row, reference.Column, text ?? "");
                    }
                }
            }
        }

        worksheetPart.Worksheet.Save();
        workbookPart.SharedStringTablePart?.SharedStringTable.Save();
        workbookPart.Workbook.Save();
    }

    public void EnsureExportReferences(CwcProject project)
    {
        project.DescriptionTextLists ??= new DescriptionTextListConfig();
        var data = project.DescriptionTextLists.TextLists;
        if (data.References.Count == 0)
        {
            throw MissingReferenceException();
        }

        var languages = GetLanguagePair(data);
        foreach (var database in project.PlcDatabases)
        {
            EnsureTextLists(database.DbCatalog);
            var mapping = project.DescriptionTextLists.GetOrCreate(database.Id, database.Name);
            EnsureRowReferences(database.DbCatalog.AlarmRows, mapping.AlmDescriptionsList, languages, data);
            EnsureRowReferences(database.DbCatalog.ParameterRows, mapping.ParDescriptionsList, languages, data);
            if (!string.IsNullOrWhiteSpace(mapping.SequenceControlStatusList))
            {
                EnsureRowReferences(database.DbCatalog.Sequence.ControlStatusRows, mapping.SequenceControlStatusList, languages, data);
            }

            if (!string.IsNullOrWhiteSpace(mapping.SequenceCommandList))
            {
                EnsureRowReferences(database.DbCatalog.Sequence.CommandRows, mapping.SequenceCommandList, languages, data);
            }

            EnsureOptionalRowReferences(database.DbCatalog.TextLists.AlmBlockingClassRows, mapping.AlmBlockingClassList, languages, data);
            EnsureOptionalRowReferences(database.DbCatalog.TextLists.CabinetRows, mapping.CabinetsList, languages, data);
            EnsureOptionalRowReferences(database.DbCatalog.TextLists.PortConfigurationRows, mapping.PortConfigurationList, languages, data);
            EnsureOptionalRowReferences(database.DbCatalog.TextLists.SeqCmdRows, mapping.SeqCmdList, languages, data);
            EnsureOptionalRowReferences(database.DbCatalog.TextLists.SeqConfigRows, mapping.SeqConfigList, languages, data);
        }
    }

    private static void EnsureOptionalRowReferences(IEnumerable<DbStringRow> rows, string listName, (string English, string Other) languages, DescriptionTextListData data)
    {
        if (!string.IsNullOrWhiteSpace(listName))
        {
            EnsureRowReferences(rows, listName, languages, data);
        }
    }

    private static void EnsureTextLists(DbCatalog catalog)
    {
        catalog.TextLists ??= TextListCatalog.CreateDefault();
        catalog.TextLists.EnsureDefaults();
    }

    private static void EnsureRowReferences(IEnumerable<DbStringRow> rows, string listName, (string English, string Other) languages, DescriptionTextListData data)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            throw MissingReferenceException();
        }

        foreach (var row in rows)
        {
            if (!data.TryGetReference(listName, row.Id, languages.English, out _)
                || !data.TryGetReference(listName, row.Id, languages.Other, out _))
            {
                throw MissingReferenceException();
            }
        }
    }

    private static InvalidOperationException MissingReferenceException()
    {
        return new InvalidOperationException("Missing TIA Portal text-list references. Save existing texts somewhere, then import a fresh TIA Portal exported Excel file so Plant Configurator can remember the exact reference rows. After that, load that exported file again and export back to a TIA Portal Excel file.");
    }

    private static int ApplyRows(IReadOnlyList<DbStringRow> rows, string listName, (string English, string Other) languages, DescriptionTextListData data)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return 0;
        }

        var changed = 0;
        foreach (var row in rows)
        {
            var english = data.GetText(listName, row.Id, languages.English);
            var other = data.GetText(listName, row.Id, languages.Other);
            if (!string.IsNullOrEmpty(english) && !string.Equals(row.DescriptionEnglish, english, StringComparison.Ordinal))
            {
                row.DescriptionEnglish = english;
                row.DescriptionDirty = false;
                changed++;
            }

            if (!string.IsNullOrEmpty(other) && !string.Equals(row.DescriptionOther, other, StringComparison.Ordinal))
            {
                row.DescriptionOther = other;
                row.DescriptionDirty = false;
                changed++;
            }
        }

        return changed;
    }

    private static int ApplyNameRows(IReadOnlyList<DbStringRow> rows, string listName, (string English, string Other) languages, DescriptionTextListData data)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return 0;
        }

        var changed = 0;
        foreach (var row in rows)
        {
            var english = data.GetText(listName, row.Id, languages.English);
            var other = data.GetText(listName, row.Id, languages.Other);
            if (!string.IsNullOrEmpty(english) && !string.Equals(row.NameEnglish, english, StringComparison.Ordinal))
            {
                row.NameEnglish = english;
                changed++;
            }

            if (!string.IsNullOrEmpty(other) && !string.Equals(row.NameOther, other, StringComparison.Ordinal))
            {
                row.NameOther = other;
                changed++;
            }
        }

        return changed;
    }

    private static int PushRows(
        IReadOnlyList<DbStringRow> rows,
        string listName,
        (string English, string Other) languages,
        DescriptionTextListData data,
        DescriptionTemplateService templateService,
        DescriptionTemplateCatalog templates,
        string scope)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return 0;
        }

        var changed = 0;
        foreach (var row in rows)
        {
            changed += SetIfDifferent(data, listName, row.Id, languages.English, templateService.ResolveDescription(row, templates, scope, languages.English));
            changed += SetIfDifferent(data, listName, row.Id, languages.Other, templateService.ResolveDescription(row, templates, scope, languages.Other));
            row.DescriptionDirty = false;
        }

        return changed;
    }

    private static int SetIfDifferent(DescriptionTextListData data, string listName, int id, string language, string value)
    {
        if (string.IsNullOrWhiteSpace(language)
            || string.Equals(data.GetText(listName, id, language), value ?? "", StringComparison.Ordinal))
        {
            return 0;
        }

        data.SetText(listName, id, language, value ?? "");
        return 1;
    }

    private static int PushNameRows(
        IReadOnlyList<DbStringRow> rows,
        string listName,
        (string English, string Other) languages,
        DescriptionTextListData data)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return 0;
        }

        var changed = 0;
        foreach (var row in rows)
        {
            changed += SetIfDifferent(data, listName, row.Id, languages.English, row.NameEnglish);
            changed += SetIfDifferent(data, listName, row.Id, languages.Other, row.NameOther);
        }

        return changed;
    }

    private static (string English, string Other) GetLanguagePair(DescriptionTextListData data)
    {
        var english = data.Languages.FirstOrDefault(lang => lang.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                      ?? data.Languages.FirstOrDefault()
                      ?? "en-US";
        var other = data.Languages.FirstOrDefault(lang => !lang.Equals(english, StringComparison.OrdinalIgnoreCase))
                    ?? english;
        return (english, other);
    }

    private static Dictionary<string, int> ReadHeaderMap(IXLWorksheet worksheet)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastColumn = worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = worksheet.Cell(1, column).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(header) && !result.ContainsKey(header))
            {
                result.Add(header, column);
            }
        }

        return result;
    }

    private static Dictionary<int, string> FindLanguageColumns(IXLWorksheet worksheet)
    {
        var result = new Dictionary<int, string>();
        var lastColumn = worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = worksheet.Cell(1, column).GetString().Trim();
            if (header.StartsWith("Text [", StringComparison.OrdinalIgnoreCase)
                && header.EndsWith("]", StringComparison.Ordinal)
                && header.Length > 7)
            {
                result[column] = header[6..^1].Trim();
            }
        }

        return result;
    }

    private static int? FindHeader(IReadOnlyDictionary<string, int> headers, string name)
    {
        return headers.TryGetValue(name, out var column) ? column : null;
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

    private static void ReadTextListComments(XLWorkbook workbook, DescriptionTextListData result)
    {
        foreach (var worksheet in workbook.Worksheets.Where(IsTextListDefinitionSheet))
        {
            ReadTextListCommentsFromSheet(worksheet, result, preferColumnC: true);
        }

        foreach (var worksheet in workbook.Worksheets.Where(sheet =>
                     !IsTextListDefinitionSheet(sheet)
                     && (sheet.Name.Equals("TextList", StringComparison.OrdinalIgnoreCase)
                         || sheet.Name.Equals("TextListEntry", StringComparison.OrdinalIgnoreCase)
                         || sheet.Name.Contains("TextList", StringComparison.OrdinalIgnoreCase))))
        {
            ReadTextListCommentsFromSheet(worksheet, result, preferColumnC: false);
        }
    }

    private static bool IsTextListDefinitionSheet(IXLWorksheet worksheet)
    {
        return worksheet.Name.Equals("TextList", StringComparison.OrdinalIgnoreCase)
               || worksheet.Name.Equals("TextLists", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadTextListCommentsFromSheet(IXLWorksheet worksheet, DescriptionTextListData result, bool preferColumnC)
    {
        var headers = ReadHeaderMap(worksheet);
        var nameColumn = ResolveTextListNameColumn(worksheet, headers, result);
        var commentColumn = preferColumnC
            ? 3
            : FindHeader(headers, "Comment", "Comments", "Description", "Text list comment", "TextListComment") ?? FindCommentHeader(headers) ?? 3;
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            var listName = worksheet.Cell(row, nameColumn).GetString().Trim();
            var comment = NormalizeImportedText(worksheet.Cell(row, commentColumn).GetString());
            if (!string.IsNullOrWhiteSpace(listName) && !string.IsNullOrWhiteSpace(comment))
            {
                result.SetComment(listName, comment);
            }
        }
    }

    private static int? FindCommentHeader(IReadOnlyDictionary<string, int> headers)
    {
        foreach (var (header, column) in headers)
        {
            if (header.StartsWith("Comment [", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("Comments [", StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return null;
    }

    private static string NormalizeImportedText(string value)
    {
        var text = (value ?? "").Trim();
        return text.Equals("<No value>", StringComparison.OrdinalIgnoreCase) ? "" : text;
    }

    private static int ResolveTextListNameColumn(IXLWorksheet worksheet, IReadOnlyDictionary<string, int> headers, DescriptionTextListData result)
    {
        var headerColumn = FindHeader(headers, "Name", "Parent", "TextList", "Text list", "Text list name");
        if (headerColumn is not null)
        {
            return headerColumn.Value;
        }

        var knownNames = result.AllListNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (knownNames.Count == 0)
        {
            return 1;
        }

        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        var lastColumn = Math.Min(worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 3, 5);
        var bestColumn = 1;
        var bestScore = 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var score = 0;
            for (var row = 2; row <= lastRow; row++)
            {
                if (knownNames.Contains(worksheet.Cell(row, column).GetString().Trim()))
                {
                    score++;
                }
            }

            if (score > bestScore)
            {
                bestColumn = column;
                bestScore = score;
            }
        }

        return bestColumn;
    }

    private static void SetCellText(WorkbookPart workbookPart, WorksheetPart worksheetPart, int rowNumber, int columnNumber, string text)
    {
        var row = worksheetPart.Worksheet.GetFirstChild<SheetData>()?
                      .Elements<Row>()
                      .FirstOrDefault(item => item.RowIndex?.Value == (uint)rowNumber)
                  ?? throw new InvalidOperationException($"TIA text-list cell reference row {rowNumber} no longer exists.");
        var cellReference = GetCellReference(rowNumber, columnNumber);
        var cell = row.Elements<Cell>()
                       .FirstOrDefault(item => item.CellReference?.Value?.Equals(cellReference, StringComparison.OrdinalIgnoreCase) == true)
                   ?? throw new InvalidOperationException($"TIA text-list cell reference {cellReference} no longer exists.");

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var sharedStringPart = workbookPart.SharedStringTablePart ?? workbookPart.AddNewPart<SharedStringTablePart>();
            sharedStringPart.SharedStringTable ??= new SharedStringTable();
            sharedStringPart.SharedStringTable.AppendChild(new SharedStringItem(CreateText(text)));
            var index = sharedStringPart.SharedStringTable.Elements<SharedStringItem>().Count() - 1;
            cell.CellValue = new CellValue(index.ToString(CultureInfo.InvariantCulture));
            cell.InlineString = null;
            return;
        }

        cell.DataType = CellValues.InlineString;
        cell.CellValue = null;
        cell.InlineString = new InlineString(CreateText(text));
    }

    private static Text CreateText(string text)
    {
        return new Text(text)
        {
            Space = SpaceProcessingModeValues.Preserve
        };
    }

    private static string GetCellReference(int rowNumber, int columnNumber)
    {
        var dividend = columnNumber;
        var columnName = "";
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName + rowNumber.ToString(CultureInfo.InvariantCulture);
    }
}
