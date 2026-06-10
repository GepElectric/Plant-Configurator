using ClosedXML.Excel;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class DbTextWorkbookService
{
    private const string IdHeader = "ID";
    private const string PlcStructureHeader = "PLC STRUCT NAME";
    private const string PlcNameHeader = "PLC DEFINITION NAME";
    private const string StatusSystemEngHeader = "STATUS SYSTEM ENG";
    private const string StatusNameEngHeader = "STATUS NAME ENG";
    private const string StatusSystemOtherHeader = "STATUS SYSTEM OTHER";
    private const string StatusNameOtherHeader = "STATUS NAME OTHER";
    private const string DescriptionEnglishHeader = "DESCRIPTION ENGLISH";
    private const string DescriptionOtherHeader = "DESCRIPTION OTHER";
    private const string DescriptionDirtyHeader = "DESCRIPTION DIRTY";
    private const string RepairStateHeader = "REPAIR STATE";
    private const string AoSystemHeader = "AO SYSTEM NAME";
    private const string AoPlcHeader = "AO PLC NAME";
    private const string SequenceControlStatusSheet = "SEQUENCE_CONTROL_STATUS";
    private const string SequenceCommandsSheet = "SEQUENCE_COMMANDS";
    private static readonly string[] DbNames = ["DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR", "DB SEQUENCE"];
    private readonly DescriptionTemplateService _descriptionTemplateService = new();

    public void ExportAll(CwcProject project, string path)
    {
        using var workbook = new XLWorkbook();
        foreach (var dbName in DbNames)
        {
            WriteDbSheet(workbook, project, dbName);
        }

        WriteDescriptionListSheet(workbook, "ALM_DESCRIPTIONS", "DB ALM", project.DbCatalog.AlarmRows, project.DescriptionTemplates);
        WriteDescriptionListSheet(workbook, "PAR_DESCRIPTIONS", "DB PAR", project.DbCatalog.ParameterRows, project.DescriptionTemplates);
        workbook.SaveAs(path);
    }

    public int ImportAll(CwcProject project, string path)
    {
        using var workbook = new XLWorkbook(path);
        var changed = 0;
        foreach (var dbName in DbNames)
        {
            var worksheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.Equals(SanitizeWorksheetName(dbName), StringComparison.OrdinalIgnoreCase));
            if (NormalizeDbName(dbName) == "DB SEQUENCE"
                && (workbook.Worksheets.Any(sheet => sheet.Name.Equals(SequenceControlStatusSheet, StringComparison.OrdinalIgnoreCase))
                    || workbook.Worksheets.Any(sheet => sheet.Name.Equals(SequenceCommandsSheet, StringComparison.OrdinalIgnoreCase))))
            {
                changed += ImportWorksheet(project, dbName, workbook.Worksheets.First());
                continue;
            }

            if (worksheet is not null)
            {
                changed += ImportWorksheet(project, dbName, worksheet);
            }
        }

        changed += ImportDescriptionListSheet(workbook, "ALM_DESCRIPTIONS", project.DbCatalog.AlarmRows);
        changed += ImportDescriptionListSheet(workbook, "PAR_DESCRIPTIONS", project.DbCatalog.ParameterRows);
        return changed;
    }

    public void Export(CwcProject project, string dbName, string path)
    {
        using var workbook = new XLWorkbook();
        WriteDbSheet(workbook, project, dbName);
        if (NormalizeDbName(dbName) == "DB ALM")
        {
            WriteDescriptionListSheet(workbook, "ALM_DESCRIPTIONS", "DB ALM", project.DbCatalog.AlarmRows, project.DescriptionTemplates);
        }
        else if (NormalizeDbName(dbName) == "DB PAR")
        {
            WriteDescriptionListSheet(workbook, "PAR_DESCRIPTIONS", "DB PAR", project.DbCatalog.ParameterRows, project.DescriptionTemplates);
        }

        workbook.SaveAs(path);
    }

    public int Import(CwcProject project, string dbName, string path)
    {
        using var workbook = new XLWorkbook(path);
        var worksheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.Equals(SanitizeWorksheetName(dbName), StringComparison.OrdinalIgnoreCase))
                        ?? workbook.Worksheets.First();
        var changed = ImportWorksheet(project, dbName, worksheet);
        if (NormalizeDbName(dbName) == "DB ALM")
        {
            changed += ImportDescriptionListSheet(workbook, "ALM_DESCRIPTIONS", project.DbCatalog.AlarmRows);
        }
        else if (NormalizeDbName(dbName) == "DB PAR")
        {
            changed += ImportDescriptionListSheet(workbook, "PAR_DESCRIPTIONS", project.DbCatalog.ParameterRows);
        }

        return changed;
    }

    private void WriteDbSheet(XLWorkbook workbook, CwcProject project, string dbName)
    {
        if (NormalizeDbName(dbName) == "DB SEQUENCE")
        {
            WriteSequenceSheet(workbook, SequenceControlStatusSheet, project.DbCatalog.Sequence.ControlStatusRows);
            WriteSequenceSheet(workbook, SequenceCommandsSheet, project.DbCatalog.Sequence.CommandRows);
            return;
        }

        var worksheet = workbook.Worksheets.Add(SanitizeWorksheetName(dbName));
        var columns = GetColumns(dbName);
        for (var column = 0; column < columns.Count; column++)
        {
            worksheet.Cell(1, column + 1).Value = columns[column].Header;
        }

        var rowNumber = 2;
        foreach (var row in GetRows(project, dbName))
        {
            for (var column = 0; column < columns.Count; column++)
            {
                worksheet.Cell(rowNumber, column + 1).Value = columns[column].Get(row);
            }

            rowNumber++;
        }

        var range = worksheet.Range(1, 1, Math.Max(1, rowNumber - 1), columns.Count);
        range.CreateTable(SanitizeTableName(dbName) + "_TEXT");
        worksheet.SheetView.FreezeRows(1);
        worksheet.Columns().AdjustToContents();
    }

    private int ImportWorksheet(CwcProject project, string dbName, IXLWorksheet worksheet)
    {
        if (NormalizeDbName(dbName) == "DB SEQUENCE")
        {
            var sequenceChanged = 0;
            var controlSheet = worksheet.Workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.Equals(SequenceControlStatusSheet, StringComparison.OrdinalIgnoreCase));
            var commandSheet = worksheet.Workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.Equals(SequenceCommandsSheet, StringComparison.OrdinalIgnoreCase));
            if (controlSheet is not null)
            {
                sequenceChanged += ImportSequenceSheet(project.DbCatalog.Sequence.ControlStatusRows, controlSheet);
            }

            if (commandSheet is not null)
            {
                sequenceChanged += ImportSequenceSheet(project.DbCatalog.Sequence.CommandRows, commandSheet);
            }

            return sequenceChanged;
        }

        var headerMap = ReadHeaderMap(worksheet);
        if (!headerMap.TryGetValue(IdHeader, out var idColumn))
        {
            throw new InvalidOperationException($"Excel file does not contain required '{IdHeader}' column.");
        }

        var rows = GetRows(project, dbName)
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var columns = GetColumns(dbName).Where(column => column.Set is not null).ToList();
        var changed = 0;
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            var idText = worksheet.Cell(rowNumber, idColumn).GetString().Trim();
            if (!int.TryParse(idText, out var id) || !rows.TryGetValue(id, out var target))
            {
                continue;
            }

            foreach (var column in columns)
            {
                if (!headerMap.TryGetValue(column.Header, out var sourceColumn))
                {
                    continue;
                }

                var value = worksheet.Cell(rowNumber, sourceColumn).GetString();
                if (!string.Equals(column.Get(target), value, StringComparison.Ordinal))
                {
                    column.Set?.Invoke(target, value);
                    changed++;
                }
            }
        }

        return changed;
    }

    private static List<TextColumn> GetColumns(string dbName)
    {
        return NormalizeDbName(dbName) switch
        {
            "DB DI" or "DB DO" => CommonStatusColumns(includeDescriptions: false),
            "DB MEAS" => MeasurementColumns(),
            "DB AO" => AoColumns(),
            "DB ALM" => CommonStatusColumns(includeDescriptions: true),
            "DB PAR" => ParameterColumns(),
            "DB SEQUENCE" => SequenceColumns(),
            _ => throw new NotSupportedException($"DB text export/import is not supported for '{dbName}'.")
        };
    }

    private static List<TextColumn> CommonStatusColumns(bool includeDescriptions)
    {
        var columns = new List<TextColumn>
        {
            IdColumn(),
            ContextColumn(PlcStructureHeader, row => row.PlcStructure),
            ContextColumn(PlcNameHeader, row => row.PlcName),
            EditableColumn(StatusSystemEngHeader, row => row.SystemNameEnglish, (row, value) => row.SetSystemNameEnglish?.Invoke(value)),
            EditableColumn(StatusNameEngHeader, row => row.NameEnglish, (row, value) => row.SetNameEnglish?.Invoke(value)),
            EditableColumn(StatusSystemOtherHeader, row => row.SystemNameOther, (row, value) => row.SetSystemNameOther?.Invoke(value)),
            EditableColumn(StatusNameOtherHeader, row => row.NameOther, (row, value) => row.SetNameOther?.Invoke(value))
        };
        if (includeDescriptions)
        {
            columns.Add(EditableColumn(DescriptionEnglishHeader, row => row.DescriptionEnglish, (row, value) => row.SetDescriptionEnglish?.Invoke(value)));
            columns.Add(EditableColumn(DescriptionOtherHeader, row => row.DescriptionOther, (row, value) => row.SetDescriptionOther?.Invoke(value)));
            columns.Add(ContextColumn(DescriptionDirtyHeader, row => row.DescriptionDirty ? "TRUE" : ""));
            columns.Add(ContextColumn(RepairStateHeader, BuildRepairState));
        }

        return columns;
    }

    private static List<TextColumn> MeasurementColumns()
    {
        return
        [
            IdColumn(),
            ContextColumn(PlcStructureHeader, row => row.PlcStructure),
            ContextColumn(PlcNameHeader, row => row.PlcName),
            EditableColumn(StatusNameEngHeader, row => row.NameEnglish, (row, value) => row.SetNameEnglish?.Invoke(value)),
            EditableColumn(StatusNameOtherHeader, row => row.NameOther, (row, value) => row.SetNameOther?.Invoke(value))
        ];
    }

    private static List<TextColumn> AoColumns()
    {
        return
        [
            IdColumn(),
            ContextColumn(AoSystemHeader, row => row.PlcStructure),
            ContextColumn(AoPlcHeader, row => row.PlcName),
            EditableColumn(StatusNameEngHeader, row => row.NameEnglish, (row, value) => row.SetNameEnglish?.Invoke(value)),
            EditableColumn(StatusNameOtherHeader, row => row.NameOther, (row, value) => row.SetNameOther?.Invoke(value))
        ];
    }

    private static List<TextColumn> ParameterColumns()
    {
        return
        [
            IdColumn(),
            ContextColumn(PlcStructureHeader, row => row.PlcStructure),
            ContextColumn(PlcNameHeader, row => row.PlcName),
            EditableColumn(StatusNameEngHeader, row => row.NameEnglish, (row, value) => row.SetNameEnglish?.Invoke(value)),
            EditableColumn(StatusNameOtherHeader, row => row.NameOther, (row, value) => row.SetNameOther?.Invoke(value)),
            EditableColumn(DescriptionEnglishHeader, row => row.DescriptionEnglish, (row, value) => row.SetDescriptionEnglish?.Invoke(value)),
            EditableColumn(DescriptionOtherHeader, row => row.DescriptionOther, (row, value) => row.SetDescriptionOther?.Invoke(value)),
            ContextColumn(DescriptionDirtyHeader, row => row.DescriptionDirty ? "TRUE" : ""),
            ContextColumn(RepairStateHeader, BuildRepairState)
        ];
    }

    private static List<TextColumn> SequenceColumns()
    {
        return
        [
            IdColumn(),
            ContextColumn(PlcNameHeader, row => row.PlcName),
            EditableColumn(StatusNameEngHeader, row => row.NameEnglish, (row, value) => row.SetNameEnglish?.Invoke(value)),
            EditableColumn(StatusNameOtherHeader, row => row.NameOther, (row, value) => row.SetNameOther?.Invoke(value))
        ];
    }

    private IEnumerable<TextRow> GetRows(CwcProject project, string dbName)
    {
        return NormalizeDbName(dbName) switch
        {
            "DB DI" => project.DbCatalog.DiRows.OrderBy(row => row.Id).Select(row => ToTextRow(row)),
            "DB DO" => project.DbCatalog.DoRows.OrderBy(row => row.Id).Select(row => ToTextRow(row)),
            "DB MEAS" => project.DbCatalog.MeasRows.OrderBy(row => row.Id).Select(row => ToTextRow(row)),
            "DB AO" => project.DbCatalog.AoRows.OrderBy(row => row.AoNumber).Select(row => ToTextRow(row)),
            "DB ALM" => project.DbCatalog.AlarmRows.OrderBy(row => row.Id).Select(row => ToTextRow(row, project.DescriptionTemplates, "ALM")),
            "DB PAR" => project.DbCatalog.ParameterRows.OrderBy(row => row.Id).Select(row => ToTextRow(row, project.DescriptionTemplates, "PAR")),
            "DB SEQUENCE" => project.DbCatalog.Sequence.ControlStatusRows
                .Concat(project.DbCatalog.Sequence.CommandRows)
                .OrderBy(row => row.Id)
                .Select(row => ToTextRow(row)),
            _ => throw new NotSupportedException($"DB text export/import is not supported for '{dbName}'.")
        };
    }

    private void WriteSequenceSheet(XLWorkbook workbook, string sheetName, IEnumerable<DbStringRow> rows)
    {
        var worksheet = workbook.Worksheets.Add(sheetName);
        var columns = SequenceColumns();
        for (var column = 0; column < columns.Count; column++)
        {
            worksheet.Cell(1, column + 1).Value = columns[column].Header;
        }

        var rowNumber = 2;
        foreach (var row in rows.OrderBy(row => row.Id).Select(row => ToTextRow(row)))
        {
            for (var column = 0; column < columns.Count; column++)
            {
                worksheet.Cell(rowNumber, column + 1).Value = columns[column].Get(row);
            }

            rowNumber++;
        }

        worksheet.Range(1, 1, Math.Max(1, rowNumber - 1), columns.Count).CreateTable(SanitizeTableName(sheetName) + "_TEXT");
        worksheet.SheetView.FreezeRows(1);
        worksheet.Columns().AdjustToContents();
    }

    private static int ImportSequenceSheet(IEnumerable<DbStringRow> targetRows, IXLWorksheet worksheet)
    {
        var headerMap = ReadHeaderMap(worksheet);
        if (!headerMap.TryGetValue(IdHeader, out var idColumn))
        {
            throw new InvalidOperationException($"Excel file does not contain required '{IdHeader}' column.");
        }

        var rows = targetRows
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var nameColumn = FindHeader(headerMap, PlcNameHeader);
        var engColumn = FindHeader(headerMap, StatusNameEngHeader);
        var otherColumn = FindHeader(headerMap, StatusNameOtherHeader);
        var changed = 0;
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            if (!int.TryParse(worksheet.Cell(rowNumber, idColumn).GetString().Trim(), out var id)
                || !rows.TryGetValue(id, out var target))
            {
                continue;
            }

            changed += SetIfDifferent(target.PlcName, nameColumn is null ? null : worksheet.Cell(rowNumber, nameColumn.Value).GetString(), value => target.PlcName = value);
            changed += SetIfDifferent(target.NameEnglish, engColumn is null ? null : worksheet.Cell(rowNumber, engColumn.Value).GetString(), value => target.NameEnglish = value);
            changed += SetIfDifferent(target.NameOther, otherColumn is null ? null : worksheet.Cell(rowNumber, otherColumn.Value).GetString(), value => target.NameOther = value);
            target.PlcStructure = "";
        }

        return changed;
    }

    private TextRow ToTextRow(DbStringRow row, DescriptionTemplateCatalog? templates = null, string scope = "")
    {
        var descriptionEnglish = row.DescriptionEnglish;
        var descriptionOther = row.DescriptionOther;
        if (templates is not null && !string.IsNullOrWhiteSpace(scope))
        {
            descriptionEnglish = _descriptionTemplateService.ResolveDescription(row, templates, scope, "en-US");
            descriptionOther = _descriptionTemplateService.ResolveDescription(row, templates, scope, "Other");
        }

        return new TextRow(
            row.Id,
            row.PlcStructure,
            row.PlcName,
            row.SystemNameEnglish,
            row.NameEnglish,
            row.SystemNameOther,
            row.NameOther,
            descriptionEnglish,
            descriptionOther,
            row.DescriptionTemplateKey,
            row.DescriptionDirty,
            (value) => row.SystemNameEnglish = value,
            (value) => row.NameEnglish = value,
            (value) => row.SystemNameOther = value,
            (value) => row.NameOther = value,
            (value) =>
            {
                row.DescriptionEnglish = value;
                row.DescriptionTemplateKey = "";
                row.DescriptionDirty = true;
            },
            (value) =>
            {
                row.DescriptionOther = value;
                row.DescriptionTemplateKey = "";
                row.DescriptionDirty = true;
            },
            (value) =>
            {
                row.DescriptionTemplateKey = value;
                row.DescriptionDirty = true;
            });
    }

    private static TextRow ToTextRow(DbAoRow row)
    {
        return new TextRow(
            row.AoNumber,
            row.AoSystemName,
            row.AoPlcName,
            "",
            row.NameEnglish,
            "",
            row.NameOther,
            "",
            "",
            "",
            false,
            null,
            (value) => row.NameEnglish = value,
            null,
            (value) => row.NameOther = value,
            null,
            null,
            null);
    }

    private void WriteDescriptionListSheet(XLWorkbook workbook, string sheetName, string dbName, IEnumerable<DbStringRow> rows, DescriptionTemplateCatalog templates)
    {
        var worksheet = workbook.Worksheets.Add(sheetName);
        var headers = new[] { "TEXT LIST", IdHeader, "en-US", "Other", RepairStateHeader };
        for (var index = 0; index < headers.Length; index++)
        {
            worksheet.Cell(1, index + 1).Value = headers[index];
        }

        var textListName = dbName == "DB ALM" ? "ALM_DESCRIPTIONS" : "PAR_DESCRIPTIONS";
        var rowNumber = 2;
        foreach (var row in rows.OrderBy(row => row.Id))
        {
            var scope = dbName == "DB ALM" ? "ALM" : "PAR";
            worksheet.Cell(rowNumber, 1).Value = textListName;
            worksheet.Cell(rowNumber, 2).Value = row.Id;
            worksheet.Cell(rowNumber, 3).Value = _descriptionTemplateService.ResolveDescription(row, templates, scope, "en-US");
            worksheet.Cell(rowNumber, 4).Value = _descriptionTemplateService.ResolveDescription(row, templates, scope, "Other");
            worksheet.Cell(rowNumber, 5).Value = BuildRepairState(ToTextRow(row));
            rowNumber++;
        }

        worksheet.Range(1, 1, Math.Max(1, rowNumber - 1), headers.Length).CreateTable(sheetName + "_TABLE");
        worksheet.SheetView.FreezeRows(1);
        worksheet.Columns().AdjustToContents();
    }

    private static int ImportDescriptionListSheet(XLWorkbook workbook, string sheetName, IEnumerable<DbStringRow> targetRows)
    {
        var worksheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
        if (worksheet is null)
        {
            return 0;
        }

        var headers = ReadHeaderMap(worksheet);
        if (!headers.TryGetValue(IdHeader, out var idColumn))
        {
            return 0;
        }

        var engColumn = FindHeader(headers, "en-US", DescriptionEnglishHeader);
        var otherColumn = FindHeader(headers, "Other", DescriptionOtherHeader, "nb-NO", "LANG2");
        var rows = targetRows.GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.First());
        var changed = 0;
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            if (!int.TryParse(worksheet.Cell(rowNumber, idColumn).GetString().Trim(), out var id)
                || !rows.TryGetValue(id, out var target))
            {
                continue;
            }

            changed += SetIfDifferent(target.DescriptionEnglish, engColumn is null ? null : worksheet.Cell(rowNumber, engColumn.Value).GetString(), value =>
            {
                target.DescriptionEnglish = value;
                target.DescriptionTemplateKey = "";
                target.DescriptionDirty = true;
            });
            changed += SetIfDifferent(target.DescriptionOther, otherColumn is null ? null : worksheet.Cell(rowNumber, otherColumn.Value).GetString(), value =>
            {
                target.DescriptionOther = value;
                target.DescriptionTemplateKey = "";
                target.DescriptionDirty = true;
            });
        }

        return changed;
    }

    private static int SetIfDifferent(string current, string? incoming, Action<string> set)
    {
        if (incoming is null || string.Equals(current, incoming, StringComparison.Ordinal))
        {
            return 0;
        }

        set(incoming);
        return 1;
    }

    private static int? FindHeader(IReadOnlyDictionary<string, int> headers, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (headers.TryGetValue(candidate, out var column))
            {
                return column;
            }
        }

        return null;
    }

    private static string BuildRepairState(TextRow row)
    {
        var reasons = new List<string>();
        if (IsSpareLike(row.NameEnglish) && IsSpareLike(row.NameOther))
        {
            reasons.Add("Name");
        }

        if (IsSpareLike(row.DescriptionEnglish) && IsSpareLike(row.DescriptionOther))
        {
            reasons.Add("Description");
        }

        return reasons.Count == 0 ? "OK" : string.Join(", ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsSpareLike(string text)
    {
        return string.IsNullOrWhiteSpace(text)
               || text.Contains("spare", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> ReadHeaderMap(IXLWorksheet worksheet)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastColumn = worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = worksheet.Cell(1, column).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(header) && !map.ContainsKey(header))
            {
                map.Add(header, column);
            }
        }

        return map;
    }

    private static TextColumn IdColumn()
    {
        return ContextColumn(IdHeader, row => row.Id.ToString());
    }

    private static TextColumn ContextColumn(string header, Func<TextRow, string> get)
    {
        return new TextColumn(header, get, null);
    }

    private static TextColumn EditableColumn(string header, Func<TextRow, string> get, Action<TextRow, string> set)
    {
        return new TextColumn(header, get, set);
    }

    private static string NormalizeDbName(string dbName)
    {
        var value = dbName.Trim().ToUpperInvariant().Replace('_', ' ');
        return value is "SEQUENCE" or "DB SEQ" or "DB SEQ PAR" or "DB SEQUENCE PAR" ? "DB SEQUENCE" : value;
    }

    private static string SanitizeWorksheetName(string value)
    {
        var name = value.Replace('/', '-').Replace('\\', '-').Replace('*', '-').Replace('[', '(').Replace(']', ')').Replace(':', '-').Replace('?', '-').Trim();
        return string.IsNullOrWhiteSpace(name) ? "DB TEXT" : name.Length > 31 ? name[..31] : name;
    }

    private static string SanitizeTableName(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray();
        var name = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(name) ? "DB_TEXT" : name;
    }

    private sealed record TextColumn(string Header, Func<TextRow, string> Get, Action<TextRow, string>? Set);

    private sealed record TextRow(
        int Id,
        string PlcStructure,
        string PlcName,
        string SystemNameEnglish,
        string NameEnglish,
        string SystemNameOther,
        string NameOther,
        string DescriptionEnglish,
        string DescriptionOther,
        string DescriptionTemplateKey,
        bool DescriptionDirty,
        Action<string>? SetSystemNameEnglish,
        Action<string>? SetNameEnglish,
        Action<string>? SetSystemNameOther,
        Action<string>? SetNameOther,
        Action<string>? SetDescriptionEnglish,
        Action<string>? SetDescriptionOther,
        Action<string>? SetDescriptionTemplateKey);
}
