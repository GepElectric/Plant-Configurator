using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class TiaPortalExportImporter
{
    private static readonly Encoding TiaEncoding = CreateTiaEncoding();
    private static readonly Regex AssignmentRegex = new(@"^\s*(?<path>.+?)\s*:=\s*(?<value>.*?);\s*$", RegexOptions.Compiled);
    private static readonly Regex ArrayAssignmentRegex = new(@"^\s*(?:""(?<quotedSection>[^""]+)""|(?<section>[A-Z_]+))\.(?<field>[A-Z_]+)\[(?<module>\d+)(?:,(?<port>\d+))?\]\s*:=\s*(?<value>.*?);\s*$", RegexOptions.Compiled);
    private static readonly Regex TypeHeaderRegex = new(@"^\s*TYPE\s+""(?<name>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex AoAllMemberRegex = new(@"^\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*:\s*""(?<type>DT_ANA_CTRL_[^""]+)""\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex AoSoloMemberRegex = new(@"^\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*(?:\{[^}]*\})?\s*:\s*""DT_ANA_CTRL_SOLO""\s*:=\s*\((?<values>.*)\)\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex GenericTypeMemberRegex = new(@"^\s*(?<name>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*(?:\{[^}]*\})?\s*:\s*""(?<type>[^""]+)""(?:\s*:=\s*(?<value>.*?))?\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex NumRegex = new(@"^\s*NUM\[(?<id>\d+)\]\s*:=\s*'(?<value>(?:''|[^'])*)';\s*$", RegexOptions.Compiled);
    private static readonly Regex LanguageRegex = new(@"^\s*(?<lang>LANGUAGE(?:_1)?)\[(?<id>\d+)\]\s*:=\s*'(?<value>(?:''|[^'])*)';\s*$", RegexOptions.Compiled);

    public List<TiaPortalImportFile> Scan(string folder)
    {
        var definitions = CreateDefinitions();
        return definitions
            .Select(definition => new TiaPortalImportFile(
                definition.Key,
                definition.DisplayName,
                definition.FileName,
                Path.Combine(folder, definition.FileName),
                File.Exists(Path.Combine(folder, definition.FileName))))
            .Where(item => item.Exists)
            .ToList();
    }

    public TiaPortalImportResult Import(CwcProject project, IEnumerable<TiaPortalImportFile> selectedFiles)
    {
        var catalog = project.DbCatalog;
        var selected = selectedFiles.Where(file => file.IsSelected).ToList();
        var result = new TiaPortalImportResult();
        foreach (var file in selected)
        {
            switch (file.Key)
            {
                case "HW_IDS": result.Add(file, ImportHwIds(project, file.FullPath)); break;
                case "DI_CONFIG": result.Add(file, ImportDiConfig(catalog, file.FullPath)); break;
                case "DO_CONFIG": result.Add(file, ImportDoConfig(catalog, file.FullPath)); break;
                case "MEAS_CONFIG": result.Add(file, ImportMeasConfig(catalog, file.FullPath)); break;
                case "AO_CONFIG": result.Add(file, ImportAoConfig(catalog, file.FullPath)); break;
                case "ALM_CONFIG": result.Add(file, ImportAlmConfig(catalog, file.FullPath)); break;
                case "PAR_CONFIG": result.Add(file, ImportParConfig(catalog, file.FullPath)); break;
                case "DI_NAMES_ENG": result.Add(file, ImportDbStringValues(catalog.DiRows, file.FullPath, (row, value) => row.NameEnglish = value)); break;
                case "DI_NAMES_OTHER": result.Add(file, ImportDbStringValues(catalog.DiRows, file.FullPath, (row, value) => row.NameOther = value)); break;
                case "DI_SYSTEM_ENG": result.Add(file, ImportDbStringValues(catalog.DiRows, file.FullPath, (row, value) => row.SystemNameEnglish = value)); break;
                case "DI_SYSTEM_OTHER": result.Add(file, ImportDbStringValues(catalog.DiRows, file.FullPath, (row, value) => row.SystemNameOther = value)); break;
                case "DO_NAMES_ENG": result.Add(file, ImportDbStringValues(catalog.DoRows, file.FullPath, (row, value) => row.NameEnglish = value)); break;
                case "DO_NAMES_OTHER": result.Add(file, ImportDbStringValues(catalog.DoRows, file.FullPath, (row, value) => row.NameOther = value)); break;
                case "DO_SYSTEM_ENG": result.Add(file, ImportDbStringValues(catalog.DoRows, file.FullPath, (row, value) => row.SystemNameEnglish = value)); break;
                case "DO_SYSTEM_OTHER": result.Add(file, ImportDbStringValues(catalog.DoRows, file.FullPath, (row, value) => row.SystemNameOther = value)); break;
                case "MEAS_NAMES_ENG": result.Add(file, ImportDbStringValues(catalog.MeasRows, file.FullPath, (row, value) => row.NameEnglish = value)); break;
                case "MEAS_NAMES_OTHER": result.Add(file, ImportDbStringValues(catalog.MeasRows, file.FullPath, (row, value) => row.NameOther = value)); break;
                case "AO_NAMES_ENG": result.Add(file, ImportAoStringValues(catalog.AoRows, file.FullPath, (row, value) => row.NameEnglish = value)); break;
                case "AO_NAMES_OTHER": result.Add(file, ImportAoStringValues(catalog.AoRows, file.FullPath, (row, value) => row.NameOther = value)); break;
                case "ALM_NAMES_ENG": result.Add(file, ImportDbStringValues(catalog.AlarmRows, file.FullPath, (row, value) => row.NameEnglish = value)); break;
                case "ALM_NAMES_OTHER": result.Add(file, ImportDbStringValues(catalog.AlarmRows, file.FullPath, (row, value) => row.NameOther = value)); break;
                case "ALM_SYSTEM_ENG": result.Add(file, ImportDbStringValues(catalog.AlarmRows, file.FullPath, (row, value) => row.SystemNameEnglish = value)); break;
                case "ALM_SYSTEM_OTHER": result.Add(file, ImportDbStringValues(catalog.AlarmRows, file.FullPath, (row, value) => row.SystemNameOther = value)); break;
                case "PAR_NAMES_ENG": result.Add(file, ImportDbStringValues(catalog.ParameterRows, file.FullPath, (row, value) => row.NameEnglish = value)); break;
                case "PAR_NAMES_OTHER": result.Add(file, ImportDbStringValues(catalog.ParameterRows, file.FullPath, (row, value) => row.NameOther = value)); break;
                case "DI_GROUPS": result.Add(file, ImportGroupNames(catalog.DiRows, catalog.DiSystemNameRows, catalog.DiStructTypePrefix, file.FullPath)); break;
                case "DO_GROUPS": result.Add(file, ImportGroupNames(catalog.DoRows, catalog.DoSystemNameRows, catalog.DoStructTypePrefix, file.FullPath)); break;
                case "MEAS_GROUPS": result.Add(file, ImportGroupNames(catalog.MeasRows, catalog.MeasSystemNameRows, catalog.MeasStructTypePrefix, file.FullPath)); break;
                case "ALM_GROUPS": result.Add(file, ImportGroupNames(catalog.AlarmRows, catalog.AlarmSystemNameRows, catalog.AlarmStructTypePrefix, file.FullPath)); break;
                case "PAR_GROUPS": result.Add(file, ImportGroupNames(catalog.ParameterRows, catalog.ParameterSystemNameRows, catalog.ParameterStructTypePrefix, file.FullPath)); break;
            }
        }

        return result;
    }

    public int ImportAoFromFolder(DbCatalog catalog, string folder)
    {
        var dbFile = Path.Combine(folder, "ANA_CTRL.db");
        var imported = ImportAoTypeConfig(catalog, folder);
        imported += File.Exists(dbFile) ? ImportAoConfig(catalog, dbFile) : 0;
        return imported;
    }

    public int ImportDbTypeNamesFromFolder(DbCatalog catalog, string dbName, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        return NormalizeDbName(dbName) switch
        {
            "DB DI" => ImportDbStringRowTypeNames(catalog.DiRows, catalog.DiSystemNameRows, catalog.DiStructTypePrefix, catalog, "DB DI", folder),
            "DB DO" => ImportDbStringRowTypeNames(catalog.DoRows, catalog.DoSystemNameRows, catalog.DoStructTypePrefix, catalog, "DB DO", folder),
            "DB MEAS" => ImportDbStringRowTypeNames(catalog.MeasRows, catalog.MeasSystemNameRows, catalog.MeasStructTypePrefix, catalog, "DB MEAS", folder),
            "DB ALM" => ImportDbStringRowTypeNames(catalog.AlarmRows, catalog.AlarmSystemNameRows, catalog.AlarmStructTypePrefix, catalog, "DB ALM", folder),
            "DB PAR" => ImportDbStringRowTypeNames(catalog.ParameterRows, catalog.ParameterSystemNameRows, catalog.ParameterStructTypePrefix, catalog, "DB PAR", folder),
            _ => 0
        };
    }

    public int ImportDbDefaultValuesFromFolder(DbCatalog catalog, string dbName, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        return NormalizeDbName(dbName) switch
        {
            "DB DI" => ImportDbTypeDefaultRows(catalog.DiRows, catalog, "DB DI", folder, CreateDiRow),
            "DB DO" => ImportDbTypeDefaultRows(catalog.DoRows, catalog, "DB DO", folder, CreateDoRow),
            "DB MEAS" => ImportDbTypeDefaultRows(catalog.MeasRows, catalog, "DB MEAS", folder, fields => CreateMeasRow(catalog, fields)),
            "DB ALM" => ImportDbTypeDefaultRows(catalog.AlarmRows, catalog, "DB ALM", folder, CreateAlmRow),
            "DB PAR" => ImportDbTypeDefaultRows(catalog.ParameterRows, catalog, "DB PAR", folder, fields => CreateParRow(catalog, fields)),
            _ => 0
        };
    }

    private static int ImportDiConfig(DbCatalog catalog, string path)
    {
        return ImportDbStructRows(catalog.DiRows, path, CreateDiRow);
    }

    private static int ImportDoConfig(DbCatalog catalog, string path)
    {
        return ImportDbStructRows(catalog.DoRows, path, CreateDoRow);
    }

    private static int ImportMeasConfig(DbCatalog catalog, string path)
    {
        return ImportDbStructRows(catalog.MeasRows, path, fields => CreateMeasRow(catalog, fields));
    }

    private static int ImportAlmConfig(DbCatalog catalog, string path)
    {
        return ImportDbStructRows(catalog.AlarmRows, path, CreateAlmRow);
    }

    private static int ImportParConfig(DbCatalog catalog, string path)
    {
        return ImportDbStructRows(catalog.ParameterRows, path, fields => CreateParRow(catalog, fields));
    }

    private static int ImportAoConfig(DbCatalog catalog, string path)
    {
        var groups = ReadStructAssignments(path);
        var imported = 0;
        var importedStructures = new List<string>();
        foreach (var fields in groups)
        {
            if (!TryReadInt(fields, "ANA_CTRL_ID", out var id) && !TryReadInt(fields, "ID", out id) && !TryReadInt(fields, "AO_ID", out id))
            {
                continue;
            }

            var row = catalog.AoRows.FirstOrDefault(item => item.AoNumber == id);
            if (row is null)
            {
                row = new DbAoRow { AoNumber = id };
                catalog.AoRows.Add(row);
            }

            row.AoSystemName = fields.Structure;
            row.AoPlcName = fields.Name;
            row.AoNumber = id;
            row.TrueText = TiaInt(fields, "GROUP_INPUT_ID") != 32000 ? "TRUE" : "";
            row.ModuleName = TiaValueOrBlank(fields, "INPUT_ID_1");
            row.ModulePort = TiaIntOrNull(fields, "INPUT_ID_2");
            row.ParValLowId = TiaValue(fields, "PAR_VAL_LOW");
            row.ParValHighId = TiaValue(fields, "PAR_VAL_HIGH");
            row.ParOffsetId = TiaValue(fields, "PAR_OFFSET_ID");
            row.ParScaleFactorId = TiaValue(fields, "PAR_SCAL_FACTOR_ID");
            row.Unit = ValidationValueOrAdd(catalog, DbValidationDefaults.DbMeasUnit, Unquote(TiaValue(fields, "S_UNIT")), blankValue: "0");
            if (!string.IsNullOrWhiteSpace(fields.Structure) && !importedStructures.Contains(fields.Structure, StringComparer.OrdinalIgnoreCase))
            {
                importedStructures.Add(fields.Structure);
            }

            imported++;
        }

        if (importedStructures.Count > 0)
        {
            MergeAoStructureNames(catalog, importedStructures);
        }

        catalog.AoRows = catalog.AoRows.OrderBy(row => row.AoNumber).ToList();
        return imported;
    }

    private static void MergeAoStructureNames(DbCatalog catalog, IReadOnlyList<string> structureNames)
    {
        for (var index = 0; index < structureNames.Count; index++)
        {
            var structureName = structureNames[index].Trim();
            var row = catalog.AoSystemNameRows.FirstOrDefault(item =>
                item.SystemPlcShortName.Equals(structureName, StringComparison.OrdinalIgnoreCase)
                || item.PlcVariableName.Equals(structureName, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new DbDiSystemNameRow();
                catalog.AoSystemNameRows.Add(row);
            }

            row.SystemNumber = index + 1;
            row.SystemPlcShortName = structureName;
            row.PlcVariableName = structureName;
            row.SystemPlcLongName = string.IsNullOrWhiteSpace(row.SystemPlcLongName)
                ? $"{catalog.AoStructTypePrefix.Trim().TrimEnd('_')}_{structureName}"
                : row.SystemPlcLongName;
            row.HmiSystemName = string.IsNullOrWhiteSpace(row.HmiSystemName) ? structureName : row.HmiSystemName;
            row.HmiSystemNameOther = string.IsNullOrWhiteSpace(row.HmiSystemNameOther) ? row.HmiSystemName : row.HmiSystemNameOther;
            row.NameLength = row.HmiSystemName.Length;
            row.SimulatorDataType = "";
        }

        catalog.AoSystemNameRows = catalog.AoSystemNameRows
            .OrderBy(row => row.SystemNumber)
            .ToList();
    }

    private static int ImportAoTypeConfig(DbCatalog catalog, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var files = Directory.GetFiles(folder, "*.udt", SearchOption.TopDirectoryOnly)
            .Where(file =>
            {
                var fileName = Path.GetFileName(file);
                return fileName.Contains("ANA_CTRL", StringComparison.OrdinalIgnoreCase)
                       || fileName.Contains("DB_AO", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        if (files.Count == 0)
        {
            return 0;
        }

        var typeLines = files
            .SelectMany(ReadTiaTypeBlocks)
            .ToDictionary(block => block.TypeName, block => block.Lines, StringComparer.OrdinalIgnoreCase);
        var groupTypeToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (typeLines.TryGetValue("DT_ANA_CTRL_ALL", out var allLines))
        {
            foreach (var line in allLines)
            {
                var match = AoAllMemberRegex.Match(line);
                if (match.Success)
                {
                    groupTypeToName[match.Groups["type"].Value.Trim()] = UnquoteIdentifier(match.Groups["name"].Value);
                }
            }
        }

        var imported = 0;
        var importedStructures = new List<string>();
        foreach (var (typeName, lines) in typeLines)
        {
            if (typeName.Equals("DT_ANA_CTRL_ALL", StringComparison.OrdinalIgnoreCase)
                || !typeName.StartsWith("DT_ANA_CTRL_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var structureName = groupTypeToName.TryGetValue(typeName, out var groupName)
                ? groupName
                : typeName["DT_ANA_CTRL_".Length..];
            if (!importedStructures.Contains(structureName, StringComparer.OrdinalIgnoreCase))
            {
                importedStructures.Add(structureName);
            }

            foreach (var line in lines)
            {
                var match = AoSoloMemberRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var values = SplitTiaTuple(match.Groups["values"].Value);
                if (values.Count < 12 || !int.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                var row = catalog.AoRows.FirstOrDefault(item => item.AoNumber == id);
                if (row is null)
                {
                    row = new DbAoRow { AoNumber = id };
                    catalog.AoRows.Add(row);
                }

                row.AoSystemName = structureName;
                row.AoPlcName = UnquoteIdentifier(match.Groups["name"].Value);
                row.TrueText = ToInt(values[1]) != 32000 ? "TRUE" : "";
                row.ModuleName = values[2].Trim() == "32000" ? "" : values[2].Trim();
                row.ModulePort = ToInt(values[3]) == 32000 ? null : ToInt(values[3]);
                row.ParValLowId = values[5].Trim();
                row.ParValHighId = values[6].Trim();
                row.ParOffsetId = values[7].Trim();
                row.ParScaleFactorId = values[8].Trim();
                row.Unit = ValidationValueOrAdd(catalog, DbValidationDefaults.DbMeasUnit, Unquote(values[10]), blankValue: "0");
                if (string.IsNullOrWhiteSpace(row.NameEnglish))
                {
                    row.NameEnglish = Unquote(values[11]);
                }

                imported++;
            }
        }

        if (importedStructures.Count > 0)
        {
            MergeAoStructureNames(catalog, importedStructures);
        }

        catalog.AoRows = catalog.AoRows.OrderBy(row => row.AoNumber).ToList();
        return imported;
    }

    private static IEnumerable<(string TypeName, List<string> Lines)> ReadTiaTypeBlocks(string path)
    {
        string currentType = "";
        List<string>? lines = null;
        foreach (var line in ReadLines(path))
        {
            var header = TypeHeaderRegex.Match(line);
            if (header.Success)
            {
                if (!string.IsNullOrWhiteSpace(currentType) && lines is not null)
                {
                    yield return (currentType, lines);
                }

                currentType = header.Groups["name"].Value.Trim();
                lines = [];
                continue;
            }

            if (line.Trim().Equals("END_TYPE", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(currentType) && lines is not null)
                {
                    yield return (currentType, lines);
                }

                currentType = "";
                lines = null;
                continue;
            }

            lines?.Add(line);
        }

        if (!string.IsNullOrWhiteSpace(currentType) && lines is not null)
        {
            yield return (currentType, lines);
        }
    }

    private static int ImportDbStringRowTypeNames(
        List<DbStringRow> rows,
        List<DbDiSystemNameRow> systemRows,
        string structTypePrefix,
        DbCatalog catalog,
        string dbName,
        string folder)
    {
        var typeLines = ReadAllTypeBlocks(folder);
        if (typeLines.Count == 0)
        {
            return 0;
        }

        var allTypeName = GetConfigurationTypeName(catalog, dbName);
        if (string.IsNullOrWhiteSpace(allTypeName) || !typeLines.TryGetValue(allTypeName, out var allLines))
        {
            allTypeName = typeLines.Keys.FirstOrDefault(name => IsLikelyAllTypeForDb(name, dbName)) ?? "";
            if (string.IsNullOrWhiteSpace(allTypeName) || !typeLines.TryGetValue(allTypeName, out allLines))
            {
                return 0;
            }
        }

        var groups = allLines
            .Select(ParseGenericMember)
            .Where(member => member is not null)
            .Select(member => member!.Value)
            .ToList();
        if (groups.Count == 0)
        {
            return 0;
        }

        var orderedRows = rows.OrderBy(row => row.Id).ToList();
        var rowIndex = 0;
        var imported = 0;
        for (var groupIndex = 0; groupIndex < groups.Count && rowIndex < orderedRows.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            UpsertSystemRow(systemRows, structTypePrefix, group.Name, group.TypeName, groupIndex);
            if (!typeLines.TryGetValue(group.TypeName, out var memberLines))
            {
                continue;
            }

            foreach (var member in memberLines.Select(ParseGenericMember).Where(member => member is not null).Select(member => member!.Value))
            {
                if (!IsSoloMemberType(member.TypeName) || rowIndex >= orderedRows.Count)
                {
                    continue;
                }

                orderedRows[rowIndex].PlcStructure = group.Name;
                orderedRows[rowIndex].PlcName = member.Name;
                rowIndex++;
                imported++;
            }
        }

        systemRows.Sort((left, right) => left.SystemNumber.CompareTo(right.SystemNumber));
        return imported;
    }

    private static Dictionary<string, List<string>> ReadAllTypeBlocks(string folder)
    {
        return Directory.GetFiles(folder, "*.udt", SearchOption.AllDirectories)
            .SelectMany(ReadTiaTypeBlocks)
            .GroupBy(block => block.TypeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Lines, StringComparer.OrdinalIgnoreCase);
    }

    private static (string Name, string TypeName)? ParseGenericMember(string line)
    {
        var match = GenericTypeMemberRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return (UnquoteIdentifier(match.Groups["name"].Value), match.Groups["type"].Value.Trim());
    }

    private static bool IsSoloMemberType(string typeName)
    {
        return typeName.Contains("_SOLO", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpsertSystemRow(List<DbDiSystemNameRow> rows, string structTypePrefix, string variableName, string typeName, int index)
    {
        var row = rows.FirstOrDefault(item =>
            item.SystemPlcShortName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
            || item.PlcVariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
            || item.SystemPlcLongName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new DbDiSystemNameRow();
            rows.Add(row);
        }

        row.SystemNumber = index + 1;
        row.SystemPlcShortName = variableName;
        row.PlcVariableName = variableName;
        row.SystemPlcLongName = typeName;
        row.HmiSystemName = string.IsNullOrWhiteSpace(row.HmiSystemName) ? variableName : row.HmiSystemName;
        row.HmiSystemNameOther = string.IsNullOrWhiteSpace(row.HmiSystemNameOther) ? row.HmiSystemName : row.HmiSystemNameOther;
        row.NameLength = row.HmiSystemName.Length;
        row.SimulatorDataType = string.IsNullOrWhiteSpace(row.SimulatorDataType) ? "" : row.SimulatorDataType;
    }

    private static string GetConfigurationTypeName(DbCatalog catalog, string dbName)
    {
        return catalog.DataBlockNames
            .FirstOrDefault(row => NormalizeDbName(row.DbName).Equals(NormalizeDbName(dbName), StringComparison.OrdinalIgnoreCase)
                                   && row.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
            ?.DataTypeName
            ?? "";
    }

    private static bool IsLikelyAllTypeForDb(string typeName, string dbName)
    {
        var normalized = NormalizeDbName(dbName);
        return normalized switch
        {
            "DB DI" => typeName.Contains("DIGITAL_INPUT", StringComparison.OrdinalIgnoreCase) && typeName.EndsWith("_ALL", StringComparison.OrdinalIgnoreCase),
            "DB DO" => typeName.Contains("DIGITAL_OUTPUT", StringComparison.OrdinalIgnoreCase) && typeName.EndsWith("_ALL", StringComparison.OrdinalIgnoreCase),
            "DB MEAS" => typeName.Contains("MEASUREMENT", StringComparison.OrdinalIgnoreCase) && typeName.EndsWith("_ALL", StringComparison.OrdinalIgnoreCase),
            "DB ALM" => typeName.Contains("ALM", StringComparison.OrdinalIgnoreCase) && typeName.EndsWith("_ALL", StringComparison.OrdinalIgnoreCase),
            "DB PAR" => typeName.Contains("PARAM", StringComparison.OrdinalIgnoreCase) && typeName.EndsWith("_ALL", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static DbStringRow CreateDiRow(StructFields fields)
    {
        var row = new DbStringRow();
        SetCommonDbRow(row, fields, "DATA.ID");
        row.TrueText = TiaInt(fields, "INPUT_DATA.GROUP_ID") != 32000 ? "TRUE" : "";
        row.ModuleName = TiaValueOrBlank(fields, "INPUT_DATA.ID_1");
        row.ModulePort = TiaIntOrNull(fields, "INPUT_DATA.ID_2");
        row.ColorValue0 = TiaInt(fields, "COLOR.VAL_0").ToString(CultureInfo.InvariantCulture);
        row.ColorValue1 = TiaInt(fields, "COLOR.VAL_1").ToString(CultureInfo.InvariantCulture);
        row.AlarmConfig = TiaBoolNumber(fields, "ALM.CONFIGURED");
        row.AlarmInvert = TiaBoolNumber(fields, "ALM.INVERT");
        row.AlarmId = row.AlarmConfig == "1" ? TiaValue(fields, "ALM.ID") : "";
        return row;
    }

    private static DbStringRow CreateDoRow(StructFields fields)
    {
        var row = new DbStringRow();
        SetCommonDbRow(row, fields, "DATA.ID");
        row.TrueText = TiaInt(fields, "INPUT_DATA.GROUP_ID") != 32000 ? "TRUE" : "";
        row.ModuleName = TiaValueOrBlank(fields, "INPUT_DATA.ID_1");
        row.ModulePort = TiaIntOrNull(fields, "INPUT_DATA.ID_2");
        return row;
    }

    private static DbStringRow CreateMeasRow(DbCatalog catalog, StructFields fields)
    {
        var row = new DbStringRow();
        SetCommonDbRow(row, fields, "MEAS_ID");
        row.LoggingEnable = TiaBoolNumber(fields, "LOGGING_ENB");
        row.LoggingTime = TiaValue(fields, "LOGGING_CYCLE");
        row.TrueText = TiaInt(fields, "GROUP_INPUT_ID") != 32000 ? "TRUE" : "";
        row.ModuleName = TiaValueOrBlank(fields, "INPUT_ID_1");
        row.ModulePort = TiaIntOrNull(fields, "INPUT_ID_2");
        row.LimitLlEnabled = TiaStatusBit(fields, "STS_ENB", 4);
        row.LimitLoEnabled = TiaStatusBit(fields, "STS_ENB", 5);
        row.LimitHiEnabled = TiaStatusBit(fields, "STS_ENB", 6);
        row.LimitHhEnabled = TiaStatusBit(fields, "STS_ENB", 7);
        row.AlarmErrId = TiaValue(fields, "ALM_ERR_ID");
        row.AlarmLlId = TiaValue(fields, "ALM_LL_ID");
        row.AlarmLoId = TiaValue(fields, "ALM_LO_ID");
        row.AlarmHiId = TiaValue(fields, "ALM_HI_ID");
        row.AlarmHhId = TiaValue(fields, "ALM_HH_ID");
        row.ParVal1Id = TiaValue(fields, "PAR_VAL_1_ID");
        row.ParVal2Id = TiaValue(fields, "PAR_VAL_2_ID");
        row.ParOffsetId = TiaValue(fields, "PAR_OFFSET_ID");
        row.ParScaleFactorId = TiaValue(fields, "PAR_SCAL_FACTOR_ID");
        row.ParLlId = TiaValue(fields, "PAR_LL_ID");
        row.ParLoId = TiaValue(fields, "PAR_LO_ID");
        row.ParHiId = TiaValue(fields, "PAR_HI_ID");
        row.ParHhId = TiaValue(fields, "PAR_HH_ID");
        row.Unit = ValidationValueOrAdd(catalog, DbValidationDefaults.DbMeasUnit, Unquote(TiaValue(fields, "S_UNIT")), blankValue: "0");
        return row;
    }

    private static DbStringRow CreateAlmRow(StructFields fields)
    {
        var row = new DbStringRow();
        SetCommonDbRow(row, fields, "ALM_ID");
        row.Used = TiaBoolNumber(fields, "USED");
        row.AlarmControl = TiaBool(fields, "ENB") ? "0" : "1";
        row.SrAlarm = TiaBoolNumber(fields, "SR_ALM");
        row.BlockingClass1 = TiaValue(fields, "BLOCK_CLASS_1");
        row.BlockingClass2 = TiaValue(fields, "BLOCK_CLASS_2");
        row.AlarmClass = TiaValue(fields, "PRIORITY_CLASS");
        row.ReactToUnit = TiaValue(fields, "REACT_TO_UNIT");
        row.Spare = TiaValue(fields, "SPARE");
        row.SourceType = TiaValue(fields, "TRACE_TYPE");
        row.SourceDigId = TiaValue(fields, "TRACE_DIG_ID");
        row.SourceAnaId = TiaValue(fields, "TRACE_MEAS_ID");
        SplitTime(TiaValue(fields, "DELAY"), out var delay, out var timeBase);
        row.Delay = delay;
        row.TimeBase = timeBase;
        return row;
    }

    private static DbStringRow CreateParRow(DbCatalog catalog, StructFields fields)
    {
        var row = new DbStringRow();
        SetCommonDbRow(row, fields, "ID");
        row.InUse = TiaBoolNumber(fields, "USED");
        row.Used = row.InUse;
        row.ParameterType = TiaValue(fields, "\"TYPE\"");
        row.RealValue = TiaValue(fields, "R_VAL");
        row.IntegerValue = TiaValue(fields, "I_VAL");
        row.TimeValue = TiaValue(fields, "T_VAL");
        row.MinValue = TiaValue(fields, "LOW_LIMIT");
        row.MaxValue = TiaValue(fields, "H_LIMIT");
        row.Unit = ValidationValueOrAdd(catalog, DbValidationDefaults.DbMeasUnit, Unquote(TiaValue(fields, "UNIT")), blankValue: "0");
        return row;
    }

    private static string NormalizeDbName(string dbName)
    {
        return string.Join(" ", (dbName ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public int ImportHwIdsSection(CwcProject project, string path, string section)
    {
        return ImportHwIds(project, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { section });
    }

    private static int ImportHwIds(CwcProject project, string path, IReadOnlySet<string>? sections = null)
    {
        var imported = 0;
        foreach (var line in ReadLines(path))
        {
            var match = ArrayAssignmentRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var section = FirstNonEmpty(match.Groups["quotedSection"].Value, match.Groups["section"].Value);
            if (sections is not null && !sections.Contains(section))
            {
                continue;
            }

            var field = match.Groups["field"].Value;
            var module = int.Parse(match.Groups["module"].Value, CultureInfo.InvariantCulture);
            var port = match.Groups["port"].Success
                ? int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture)
                : 0;
            var value = match.Groups["value"].Value.Trim();

            switch (section.ToUpperInvariant())
            {
                case "DI":
                    imported += ApplyDigitalHwValue(project.DigitalInputs, module, port, field, value);
                    break;
                case "DO":
                    imported += ApplyDigitalHwValue(project.DigitalOutputs, module, port, field, value);
                    break;
                case "AI":
                    imported += ApplyAnalogHwValue(project.AnalogInputs, module, port, field, value);
                    break;
                case "RTD":
                    imported += ApplyAnalogHwValue(project.RtdInputs, module, port, field, value);
                    break;
                case "COUNTER":
                    imported += ApplyCounterHwValue(project.Counters, module, field, value);
                    break;
            }
        }

        return imported;
    }

    private static int ApplyDigitalHwValue(List<DigitalSignalConfig> rows, int module, int port, string field, string value)
    {
        var row = rows.FirstOrDefault(item => item.ModuleNumber == module && item.Port == port);
        if (row is null)
        {
            row = new DigitalSignalConfig { ModuleNumber = module, Port = port };
            rows.Add(row);
        }

        switch (field)
        {
            case "ID":
                row.Id = ToInt(value);
                return 1;
            case "CONFIG":
                row.Config = ToConfigState(value);
                return 1;
            default:
                return 0;
        }
    }

    private static int ApplyAnalogHwValue(List<AnalogSignalConfig> rows, int module, int port, string field, string value)
    {
        var row = rows.FirstOrDefault(item => item.ModuleNumber == module && item.Port == port);
        if (row is null)
        {
            row = new AnalogSignalConfig { ModuleNumber = module, Port = port };
            rows.Add(row);
        }

        switch (field)
        {
            case "ID":
                row.Id = ToInt(value);
                return 1;
            case "CONFIG":
                row.Config = ToConfigState(value);
                return 1;
            case "FILTER_TIME":
                row.FilterTime = ToDouble(value);
                return 1;
            case "CUSTOM_LIMITS_ENABLE":
                row.CustomLimits = ToConfigState(value);
                return 1;
            case "CUSTOM_LOW_LIMIT":
                row.LowLimit = ToDouble(value);
                return 1;
            case "CUSTOM_HIGH_LIMIT":
                row.HighLimit = ToDouble(value);
                return 1;
            default:
                return 0;
        }
    }

    private static int ApplyCounterHwValue(List<CounterConfig> rows, int module, string field, string value)
    {
        var row = rows.FirstOrDefault(item => item.ModuleNumber == module);
        if (row is null)
        {
            row = new CounterConfig { ModuleNumber = module };
            rows.Add(row);
        }

        switch (field)
        {
            case "ID":
                row.Id = ToInt(value);
                return 1;
            case "CONFIG":
                row.Config = ToConfigState(value);
                return 1;
            case "FILTER_TIME":
                row.FilterTime = ToDouble(value);
                return 1;
            default:
                return 0;
        }
    }

    private static int ImportDbStructRows(List<DbStringRow> rows, string path, Func<StructFields, DbStringRow> createRow)
    {
        var imported = 0;
        foreach (var fields in ReadStructAssignments(path))
        {
            var importedRow = createRow(fields);
            if (importedRow.Id < 0)
            {
                continue;
            }

            var row = rows.FirstOrDefault(item => item.Id == importedRow.Id);
            if (row is null)
            {
                row = new DbStringRow { Id = importedRow.Id };
                rows.Add(row);
            }

            CopyImportedValues(row, importedRow);
            imported++;
        }

        rows.Sort((left, right) => left.Id.CompareTo(right.Id));
        return imported;
    }

    private static int ImportDbTypeDefaultRows(
        List<DbStringRow> rows,
        DbCatalog catalog,
        string dbName,
        string folder,
        Func<StructFields, DbStringRow> createRow)
    {
        var fields = ReadDefaultStructFieldsFromTypes(catalog, dbName, folder);
        return ImportDbStructRows(rows, fields, createRow);
    }

    private static int ImportDbStructRows(List<DbStringRow> rows, IEnumerable<StructFields> fieldGroups, Func<StructFields, DbStringRow> createRow)
    {
        var imported = 0;
        foreach (var fields in fieldGroups)
        {
            var importedRow = createRow(fields);
            if (importedRow.Id < 0)
            {
                continue;
            }

            var row = rows.FirstOrDefault(item => item.Id == importedRow.Id);
            if (row is null)
            {
                row = new DbStringRow { Id = importedRow.Id };
                rows.Add(row);
            }

            CopyImportedValues(row, importedRow);
            imported++;
        }

        rows.Sort((left, right) => left.Id.CompareTo(right.Id));
        return imported;
    }

    private static List<StructFields> ReadDefaultStructFieldsFromTypes(DbCatalog catalog, string dbName, string folder)
    {
        var typeLines = ReadAllTypeBlocks(folder);
        if (typeLines.Count == 0)
        {
            return [];
        }

        var allTypeName = GetConfigurationTypeName(catalog, dbName);
        if (string.IsNullOrWhiteSpace(allTypeName) || !typeLines.TryGetValue(allTypeName, out var allLines))
        {
            allTypeName = typeLines.Keys.FirstOrDefault(name => IsLikelyAllTypeForDb(name, dbName)) ?? "";
            if (string.IsNullOrWhiteSpace(allTypeName) || !typeLines.TryGetValue(allTypeName, out allLines))
            {
                return [];
            }
        }

        var result = new List<StructFields>();
        foreach (var group in allLines.Select(ParseGenericMember).Where(member => member is not null).Select(member => member!.Value))
        {
            if (!typeLines.TryGetValue(group.TypeName, out var memberLines))
            {
                continue;
            }

            foreach (var member in memberLines.Select(ParseGenericMemberWithValue).Where(member => member is not null).Select(member => member!.Value))
            {
                if (!IsSoloMemberType(member.TypeName) || string.IsNullOrWhiteSpace(member.Value))
                {
                    continue;
                }

                var fields = new StructFields(group.Name, member.Name);
                ApplyTypeDefaultTuple(fields, dbName, member.Value);
                result.Add(fields);
            }
        }

        return result;
    }

    private static (string Name, string TypeName, string Value)? ParseGenericMemberWithValue(string line)
    {
        var match = GenericTypeMemberRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return (
            UnquoteIdentifier(match.Groups["name"].Value),
            match.Groups["type"].Value.Trim(),
            match.Groups["value"].Success ? match.Groups["value"].Value.Trim() : "");
    }

    private static void ApplyTypeDefaultTuple(StructFields fields, string dbName, string value)
    {
        var values = SplitTopLevelTuple(value);
        switch (NormalizeDbName(dbName))
        {
            case "DB DI":
            case "DB DO":
                ApplyStatusSoloDefaults(fields, values);
                break;
            case "DB MEAS":
                ApplyMeasurementSoloDefaults(fields, values);
                break;
            case "DB ALM":
                ApplyAlarmSoloDefaults(fields, values);
                break;
            case "DB PAR":
                ApplyParameterSoloDefaults(fields, values);
                break;
        }
    }

    private static void ApplyStatusSoloDefaults(StructFields fields, IReadOnlyList<string> values)
    {
        SetTupleFields(fields, "DATA", values.ElementAtOrDefault(0), ["VAL", "TYPE", "ID"]);
        SetTupleFields(fields, "INPUT_DATA", values.ElementAtOrDefault(1), ["GROUP_ID", "ID_1", "ID_2"]);
        SetTupleFields(fields, "COLOR", values.ElementAtOrDefault(2), ["TYPE", "VAL_0", "VAL_1"]);
        SetTupleFields(fields, "ALM", values.ElementAtOrDefault(3), ["CONFIGURED", "INVERT", "ID"]);
    }

    private static void ApplyMeasurementSoloDefaults(StructFields fields, IReadOnlyList<string> values)
    {
        Set(fields, "STS_ENB", values.ElementAtOrDefault(1));
        Set(fields, "LOGGING_ENB", values.ElementAtOrDefault(2));
        Set(fields, "LOGGING_CYCLE", values.ElementAtOrDefault(3));
        Set(fields, "MEAS_ID", values.ElementAtOrDefault(5));
        Set(fields, "GROUP_INPUT_ID", values.ElementAtOrDefault(6));
        Set(fields, "INPUT_ID_1", values.ElementAtOrDefault(7));
        Set(fields, "INPUT_ID_2", values.ElementAtOrDefault(8));
        Set(fields, "ALM_ERR_ID", values.ElementAtOrDefault(9));
        Set(fields, "ALM_LL_ID", values.ElementAtOrDefault(10));
        Set(fields, "ALM_LO_ID", values.ElementAtOrDefault(11));
        Set(fields, "ALM_HI_ID", values.ElementAtOrDefault(12));
        Set(fields, "ALM_HH_ID", values.ElementAtOrDefault(13));
        Set(fields, "PAR_VAL_1_ID", values.ElementAtOrDefault(14));
        Set(fields, "PAR_VAL_2_ID", values.ElementAtOrDefault(15));
        Set(fields, "PAR_OFFSET_ID", values.ElementAtOrDefault(16));
        Set(fields, "PAR_SCAL_FACTOR_ID", values.ElementAtOrDefault(17));
        Set(fields, "PAR_LL_ID", values.ElementAtOrDefault(18));
        Set(fields, "PAR_LO_ID", values.ElementAtOrDefault(19));
        Set(fields, "PAR_HI_ID", values.ElementAtOrDefault(20));
        Set(fields, "PAR_HH_ID", values.ElementAtOrDefault(21));
        Set(fields, "S_UNIT", values.ElementAtOrDefault(23));
    }

    private static void ApplyAlarmSoloDefaults(StructFields fields, IReadOnlyList<string> values)
    {
        Set(fields, "ALM_ID", values.ElementAtOrDefault(0));
        Set(fields, "USED", values.ElementAtOrDefault(1));
        Set(fields, "ENB", values.ElementAtOrDefault(2));
        Set(fields, "SR_ALM", values.ElementAtOrDefault(3));
        Set(fields, "BLOCK_CLASS_1", values.ElementAtOrDefault(6));
        Set(fields, "BLOCK_CLASS_2", values.ElementAtOrDefault(7));
        Set(fields, "PRIORITY_CLASS", values.ElementAtOrDefault(8));
        Set(fields, "REACT_TO_UNIT", values.ElementAtOrDefault(9));
        Set(fields, "SPARE", values.ElementAtOrDefault(10));
        Set(fields, "TRACE_TYPE", values.ElementAtOrDefault(11));
        Set(fields, "TRACE_DIG_ID", values.ElementAtOrDefault(12));
        Set(fields, "TRACE_MEAS_ID", values.ElementAtOrDefault(13));
        Set(fields, "DELAY", values.ElementAtOrDefault(14));
    }

    private static void ApplyParameterSoloDefaults(StructFields fields, IReadOnlyList<string> values)
    {
        Set(fields, "USED", values.ElementAtOrDefault(0));
        Set(fields, "ID", values.ElementAtOrDefault(1));
        Set(fields, "\"TYPE\"", values.ElementAtOrDefault(2));
        Set(fields, "TYPE", values.ElementAtOrDefault(2));
        Set(fields, "R_VAL", values.ElementAtOrDefault(3));
        Set(fields, "I_VAL", values.ElementAtOrDefault(4));
        Set(fields, "T_VAL", values.ElementAtOrDefault(5));
        Set(fields, "LOW_LIMIT", values.ElementAtOrDefault(6));
        Set(fields, "H_LIMIT", values.ElementAtOrDefault(7));
        Set(fields, "UNIT", values.ElementAtOrDefault(8));
    }

    private static void SetTupleFields(StructFields fields, string prefix, string? tuple, IReadOnlyList<string> names)
    {
        var values = SplitTopLevelTuple(tuple ?? "");
        for (var index = 0; index < names.Count && index < values.Count; index++)
        {
            Set(fields, $"{prefix}.{names[index]}", values[index]);
        }
    }

    private static void Set(StructFields fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Values[key] = value.Trim();
        }
    }

    private static void SetCommonDbRow(DbStringRow row, StructFields fields, string idField)
    {
        row.Id = TryReadInt(fields, idField, out var id) ? id : -1;
        row.PlcStructure = fields.Structure;
        row.PlcName = fields.Name;
    }

    private static void CopyImportedValues(DbStringRow target, DbStringRow source)
    {
        target.PlcStructure = source.PlcStructure;
        target.PlcName = source.PlcName;
        target.TrueText = source.TrueText;
        target.ModuleName = source.ModuleName;
        target.ModulePort = source.ModulePort;
        target.ColorValue0 = source.ColorValue0;
        target.ColorValue1 = source.ColorValue1;
        target.AlarmConfig = source.AlarmConfig;
        target.AlarmInvert = source.AlarmInvert;
        target.AlarmId = source.AlarmId;
        target.LoggingEnable = source.LoggingEnable;
        target.LoggingTime = source.LoggingTime;
        target.Unit = source.Unit;
        target.Used = source.Used;
        target.InUse = source.InUse;
        target.AlarmControl = source.AlarmControl;
        target.SrAlarm = source.SrAlarm;
        target.AlarmClass = source.AlarmClass;
        target.ReactToUnit = source.ReactToUnit;
        target.Spare = source.Spare;
        target.BlockingClass1 = source.BlockingClass1;
        target.BlockingClass2 = source.BlockingClass2;
        target.Delay = source.Delay;
        target.TimeBase = source.TimeBase;
        target.SourceType = source.SourceType;
        target.SourceDigId = source.SourceDigId;
        target.SourceAnaId = source.SourceAnaId;
        target.ParameterType = source.ParameterType;
        target.IntegerValue = source.IntegerValue;
        target.TimeValue = source.TimeValue;
        target.RealValue = source.RealValue;
        target.MinValue = source.MinValue;
        target.MaxValue = source.MaxValue;
        target.LimitLlEnabled = source.LimitLlEnabled;
        target.LimitLoEnabled = source.LimitLoEnabled;
        target.LimitHiEnabled = source.LimitHiEnabled;
        target.LimitHhEnabled = source.LimitHhEnabled;
        target.AlarmErrId = source.AlarmErrId;
        target.AlarmLlId = source.AlarmLlId;
        target.AlarmLoId = source.AlarmLoId;
        target.AlarmHiId = source.AlarmHiId;
        target.AlarmHhId = source.AlarmHhId;
        target.ParVal1Id = source.ParVal1Id;
        target.ParVal2Id = source.ParVal2Id;
        target.ParOffsetId = source.ParOffsetId;
        target.ParScaleFactorId = source.ParScaleFactorId;
        target.ParLlId = source.ParLlId;
        target.ParLoId = source.ParLoId;
        target.ParHiId = source.ParHiId;
        target.ParHhId = source.ParHhId;
    }

    private static int ImportDbStringValues(List<DbStringRow> rows, string path, Action<DbStringRow, string> apply)
    {
        var imported = 0;
        foreach (var (id, value) in ReadNumStrings(path))
        {
            var row = rows.FirstOrDefault(item => item.Id == id);
            if (row is null)
            {
                row = new DbStringRow { Id = id };
                rows.Add(row);
            }

            apply(row, value);
            imported++;
        }

        rows.Sort((left, right) => left.Id.CompareTo(right.Id));
        return imported;
    }

    private static int ImportAoStringValues(List<DbAoRow> rows, string path, Action<DbAoRow, string> apply)
    {
        var imported = 0;
        foreach (var (id, value) in ReadNumStrings(path))
        {
            var row = rows.FirstOrDefault(item => item.AoNumber == id);
            if (row is null)
            {
                row = new DbAoRow { AoNumber = id };
                rows.Add(row);
            }

            apply(row, value);
            imported++;
        }

        rows.Sort((left, right) => left.AoNumber.CompareTo(right.AoNumber));
        return imported;
    }

    private static int ImportGroupNames(List<DbStringRow> sourceRows, List<DbDiSystemNameRow> targetRows, string structPrefix, string path)
    {
        var names = ReadGroupNames(path);
        var structures = sourceRows
            .Where(row => !string.IsNullOrWhiteSpace(row.PlcStructure))
            .GroupBy(row => row.PlcStructure.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new { Name = group.Key, Index = index })
            .ToList();

        var imported = 0;
        foreach (var structure in structures)
        {
            var row = targetRows.FirstOrDefault(item => item.SystemPlcShortName.Equals(structure.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new DbDiSystemNameRow { SystemPlcShortName = structure.Name };
                targetRows.Add(row);
            }

            row.SystemNumber = structure.Index + 1;
            row.PlcVariableName = structure.Name;
            row.SystemPlcLongName = $"{structPrefix.Trim().TrimEnd('_')}_{structure.Name}";
            if (names.English.TryGetValue(structure.Index, out var english))
            {
                row.HmiSystemName = english;
            }

            if (names.Other.TryGetValue(structure.Index, out var other))
            {
                row.HmiSystemNameOther = other;
            }

            row.NameLength = row.HmiSystemName.Length;
            imported++;
        }

        targetRows.Sort((left, right) => left.SystemNumber.CompareTo(right.SystemNumber));
        return imported;
    }

    private static List<StructFields> ReadStructAssignments(string path)
    {
        var groups = new Dictionary<string, StructFields>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadLines(path))
        {
            var match = AssignmentRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var rawPath = match.Groups["path"].Value.Trim();
            var value = match.Groups["value"].Value.Trim();
            var parts = rawPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var structure = UnquoteIdentifier(parts[0]);
            var name = UnquoteIdentifier(parts[1]);
            var field = string.Join('.', parts.Skip(2));
            var key = $"{structure}.{name}";
            if (!groups.TryGetValue(key, out var fields))
            {
                fields = new StructFields(structure, name);
                groups[key] = fields;
            }

            fields.Values[field] = value;
        }

        return groups.Values.ToList();
    }

    private static Dictionary<int, string> ReadNumStrings(string path)
    {
        var values = new Dictionary<int, string>();
        foreach (var line in ReadLines(path))
        {
            var match = NumRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups["id"].Value, out var id))
            {
                values[id] = Unescape(match.Groups["value"].Value);
            }
        }

        return values;
    }

    private static (Dictionary<int, string> English, Dictionary<int, string> Other) ReadGroupNames(string path)
    {
        var english = new Dictionary<int, string>();
        var other = new Dictionary<int, string>();
        foreach (var line in ReadLines(path))
        {
            var match = LanguageRegex.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, out var id))
            {
                continue;
            }

            var value = Unescape(match.Groups["value"].Value);
            if (match.Groups["lang"].Value == "LANGUAGE_1")
            {
                other[id] = value;
            }
            else
            {
                english[id] = value;
            }
        }

        return (english, other);
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        return File.ReadLines(path, TiaEncoding);
    }

    private static bool TryReadInt(StructFields fields, string key, out int value)
    {
        return int.TryParse(TiaValue(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int TiaInt(StructFields fields, string key)
    {
        return TryReadInt(fields, key, out var value) ? value : 0;
    }

    private static int? TiaIntOrNull(StructFields fields, string key)
    {
        var value = TiaInt(fields, key);
        return value == 32000 ? null : value;
    }

    private static string TiaValue(StructFields fields, string key)
    {
        if (fields.Values.TryGetValue(key, out var value))
        {
            return value;
        }

        if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
        {
            var unquoted = key[1..^1];
            return fields.Values.TryGetValue(unquoted, out value) ? value : "";
        }

        var quoted = "\"" + key + "\"";
        return fields.Values.TryGetValue(quoted, out value) ? value : "";
    }

    private static string UnquoteIdentifier(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : trimmed;
    }

    private static List<string> SplitTiaTuple(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inString = false;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\'')
            {
                current.Append(ch);
                if (inString && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    current.Append(value[index + 1]);
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (ch == ',' && !inString)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static List<string> SplitTopLevelTuple(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '(' && text[^1] == ')')
        {
            text = text[1..^1].Trim();
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inString = false;
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '\'')
            {
                current.Append(ch);
                if (inString && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    current.Append(text[index + 1]);
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')' && depth > 0)
                {
                    depth--;
                }
                else if (ch == ',' && depth == 0)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }

            current.Append(ch);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static string TiaValueOrBlank(StructFields fields, string key)
    {
        var value = TiaValue(fields, key).Trim();
        return value == "32000" ? "" : value;
    }

    private static bool TiaBool(StructFields fields, string key)
    {
        return TiaValue(fields, key).Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }

    private static string TiaBoolNumber(StructFields fields, string key)
    {
        return TiaBool(fields, key) ? "1" : "0";
    }

    private static int ToInt(string value)
    {
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }

    private static double ToDouble(string value)
    {
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0.0;
    }

    private static SignalConfigState ToConfigState(string value)
    {
        return ToInt(value) switch
        {
            1 => SignalConfigState.Enabled,
            2 => SignalConfigState.Inverted,
            _ => SignalConfigState.Disabled
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static bool TiaStatusBit(StructFields fields, string key, int bit)
    {
        if (fields.Values.TryGetValue($"{key}.%X{bit}", out var bitValue))
        {
            return bitValue.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        }

        return TryReadInt(fields, key, out var value) && (value & (1 << bit)) != 0;
    }

    private static string ValidationValueOrRaw(DbCatalog catalog, string key, string textOrValue, string blankValue = "")
    {
        var value = textOrValue.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return blankValue;
        }

        var options = catalog.ValidationLists
            .FirstOrDefault(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?.Options;
        if (options is null)
        {
            return value;
        }

        return options.FirstOrDefault(option =>
                   option.Value.Equals(value, StringComparison.OrdinalIgnoreCase)
                   || option.Text.Equals(value, StringComparison.OrdinalIgnoreCase)
                   || option.DisplayText.Equals(value, StringComparison.OrdinalIgnoreCase))?.Value
               ?? value;
    }

    private static string ValidationValueOrAdd(DbCatalog catalog, string key, string textOrValue, string blankValue = "")
    {
        var value = textOrValue.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return blankValue;
        }

        var list = catalog.ValidationLists.FirstOrDefault(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (list is null)
        {
            list = new DbValidationListSetting { Key = key, Name = key };
            catalog.ValidationLists.Add(list);
        }

        var options = DbValidationListSetting.SplitOptions(list.ValuesText);
        var existing = options.FirstOrDefault(option =>
            option.Value.Equals(value, StringComparison.OrdinalIgnoreCase)
            || option.Text.Equals(value, StringComparison.OrdinalIgnoreCase)
            || option.DisplayText.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Value;
        }

        var nextCode = options
            .Select(option => int.TryParse(option.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var newOption = new DbValidationOption(nextCode.ToString(CultureInfo.InvariantCulture), value);
        list.ValuesText = string.IsNullOrWhiteSpace(list.ValuesText)
            ? newOption.DisplayText
            : list.ValuesText.TrimEnd() + Environment.NewLine + newOption.DisplayText;
        return newOption.Value;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? Unescape(value[1..^1])
            : value;
    }

    private static string Unescape(string value)
    {
        return value.Replace("''", "'");
    }

    private static void SplitTime(string value, out string delay, out string timeBase)
    {
        var text = value.Trim();
        if (text.StartsWith("T#", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var suffixStart = text.Length;
        while (suffixStart > 0 && char.IsLetter(text[suffixStart - 1]))
        {
            suffixStart--;
        }

        delay = suffixStart > 0 ? text[..suffixStart] : "0";
        timeBase = text[suffixStart..].ToUpperInvariant() switch
        {
            "S" => "1",
            "M" => "2",
            "MIN" => "2",
            "H" => "3",
            _ => "0"
        };
    }

    private static Encoding CreateTiaEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static List<TiaPortalImportDefinition> CreateDefinitions()
    {
        return
        [
            new("HW_IDS", "HW IDs", "HW_ID_s.db"),
            new("DI_CONFIG", "DB DI configuration", "DI.db"),
            new("DO_CONFIG", "DB DO configuration", "DO.db"),
            new("MEAS_CONFIG", "DB MEAS configuration", "MEAS.db"),
            new("AO_CONFIG", "DB AO configuration", "ANA_CTRL.db"),
            new("ALM_CONFIG", "DB ALM configuration", "ALM.db"),
            new("PAR_CONFIG", "DB PAR configuration", "PAR.db"),
            new("DI_GROUPS", "DB DI group names", "DI_GROUP_NAMES.db"),
            new("DO_GROUPS", "DB DO group names", "DO_GROUP_NAMES.db"),
            new("MEAS_GROUPS", "DB MEAS group names", "MEAS_GROUP_NAMES.db"),
            new("ALM_GROUPS", "DB ALM group names", "ALM_GROUP_NAMES.db"),
            new("PAR_GROUPS", "DB PAR group names", "PAR_GROUP_NAMES.db"),
            new("DI_NAMES_ENG", "DB DI names ENG", "DI_STRING_NAMES_ENG.db"),
            new("DI_NAMES_OTHER", "DB DI names other", "DI_STRING_NAMES_GER.db"),
            new("DI_SYSTEM_ENG", "DB DI system names ENG", "DI_SYSTEM_STRING_NAMES_ENG.db"),
            new("DI_SYSTEM_OTHER", "DB DI system names other", "DI_SYSTEM_STRING_NAMES_GER.db"),
            new("DO_NAMES_ENG", "DB DO names ENG", "DO_STRING_NAMES_ENG.db"),
            new("DO_NAMES_OTHER", "DB DO names other", "DO_STRING_NAMES_GER.db"),
            new("DO_SYSTEM_ENG", "DB DO system names ENG", "DO_SYSTEM_STRING_NAMES_ENG.db"),
            new("DO_SYSTEM_OTHER", "DB DO system names other", "DO_SYSTEM_STRING_NAMES_GER.db"),
            new("MEAS_NAMES_ENG", "DB MEAS names ENG", "MEAS_STRING_NAMES_ENG.db"),
            new("MEAS_NAMES_OTHER", "DB MEAS names other", "MEAS_STRING_NAMES_GER.db"),
            new("AO_NAMES_ENG", "DB AO names ENG", "ANA_CTRL_STRING_NAMES_ENG.db"),
            new("AO_NAMES_OTHER", "DB AO names other", "ANA_CTRL_STRING_NAMES_GER.db"),
            new("ALM_NAMES_ENG", "DB ALM names ENG", "ALM_STRING_NAMES_ENG_LANGUGE.db"),
            new("ALM_NAMES_OTHER", "DB ALM names other", "ALM_STRING_NAMES_GER_LANGUGE.db"),
            new("ALM_SYSTEM_ENG", "DB ALM system names ENG", "ALM_STRING_SYSTEMS_ENG_LANGUAGE.db"),
            new("ALM_SYSTEM_OTHER", "DB ALM system names other", "ALM_STRING_SYSTEMS_GER_LANGUAGE.db"),
            new("PAR_NAMES_ENG", "DB PAR names ENG", "PAR_STRING_NAMES_ENG_LANGUGE.db"),
            new("PAR_NAMES_OTHER", "DB PAR names other", "PAR_STRING_NAMES_OTH_LANG.db")
        ];
    }

    private sealed class StructFields(string structure, string name)
    {
        public string Structure { get; } = structure;
        public string Name { get; } = name;
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record TiaPortalImportDefinition(string Key, string DisplayName, string FileName);
}

public sealed class TiaPortalImportFile(string key, string displayName, string fileName, string fullPath, bool exists)
{
    public string Key { get; } = key;
    public string DisplayName { get; } = displayName;
    public string FileName { get; } = fileName;
    public string FullPath { get; } = fullPath;
    public bool Exists { get; } = exists;
    public bool IsSelected { get; set; } = true;
}

public sealed class TiaPortalImportResult
{
    public List<string> Messages { get; } = [];
    public int ImportedCount { get; private set; }

    public void Add(TiaPortalImportFile file, int count)
    {
        ImportedCount += count;
        Messages.Add($"{file.FileName}: {count} row(s)");
    }
}
