using ClosedXML.Excel;
using System.IO;
using System.Text;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class ExcelProjectImporter
{
    private static readonly Encoding LegacyExcelEncoding = CreateLegacyExcelEncoding();

    public List<DbStringRow> ImportDbDiRows(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbDiRows(workbook);
    }

    public List<DbStringRow> ImportDbDoRows(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbDoRows(workbook);
    }

    public List<DbStringRow> ImportDbMeasRows(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbMeasRows(workbook);
    }

    public List<DbAoRow> ImportDbAoRows(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbAoRows(workbook);
    }

    public List<DbDiSystemNameRow> ImportDbDiSystemNameRows(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbDiSystemNameRows(workbook);
    }

    public List<DbDiSystemNameRow> ImportDbDoSystemNameRows(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbDoSystemNameRows(workbook);
    }

    public List<DbColorSetting> ImportDbDiColorValues(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbDiColorValues(workbook);
    }

    public List<DbValidationListSetting> ImportDbValidationLists(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDbValidationLists(workbook);
    }

    public ModuleCatalog ImportModuleCatalog(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadModuleCatalog(workbook);
    }

    public List<DeviceConfig> ImportDeviceRows(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        return ReadDeviceTable(workbook);
    }

    public CwcProject Import(string workbookPath)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Excel workbook does not exist.", workbookPath);
        }

        using var workbook = OpenWorkbookShared(workbookPath);
        var project = CwcProject.CreateEmpty();
        project.ProjectName = Path.GetFileNameWithoutExtension(workbookPath);
        project.ModuleCatalog = ReadModuleCatalog(workbook);
        project.DigitalInputs = ReadDigitalTable(workbook, "DI", "CONFIG_DI");
        project.DigitalOutputs = ReadDigitalTable(workbook, "DO", "CONFIG_DO");
        project.AnalogInputs = ReadAnalogTable(workbook, "AI", "CONFIG_AI");
        project.RtdInputs = ReadAnalogTable(workbook, "RTD", "CONFIG_RTD");
        project.Counters = ReadCounterTable(workbook, "COUNTER", "CONFIG_COUNTER");
        project.AnalogOutputs = ReadAnalogOutputTable(workbook, "AO", "CONFIG_AO");
        project.Devices = ReadDeviceTable(workbook);
        project.DbCatalog = ReadDbCatalog(workbook);
        new ConfigurationCalculator().Recalculate(project);
        return project;
    }

    private static XLWorkbook OpenWorkbookShared(string workbookPath)
    {
        using var source = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    private static List<DigitalSignalConfig> ReadDigitalTable(XLWorkbook workbook, string sheetName, string tableName)
    {
        var table = workbook.Worksheet(sheetName).Table(tableName);
        return table.DataRange.Rows()
            .Select(row => new DigitalSignalConfig
            {
                ModuleNumber = ReadInt(row, 1),
                Port = ReadInt(row, 3),
                Config = ReadConfig(row, 4),
                Id = ReadInt(row, 5),
                Structure = ReadText(row, 6),
                StatusNameDefinition = ReadText(row, 7),
                CalcConfig = ReadInt(row, 8),
                CalcId = ReadInt(row, 9),
                StructName = ReadText(row, 10),
                SignalName = ReadText(row, 11)
            })
            .ToList();
    }

    private static List<AnalogSignalConfig> ReadAnalogTable(XLWorkbook workbook, string sheetName, string tableName)
    {
        var table = workbook.Worksheet(sheetName).Table(tableName);
        return table.DataRange.Rows()
            .Select(row => new AnalogSignalConfig
            {
                ModuleNumber = ReadInt(row, 1),
                Port = ReadInt(row, 3),
                Config = ReadConfig(row, 4),
                Id = ReadInt(row, 5),
                Structure = ReadText(row, 6),
                MeasurementName = ReadText(row, 7),
                FilterTime = ReadDouble(row, 8),
                CustomLimits = ReadConfig(row, 9),
                LowLimit = ReadDouble(row, 10),
                HighLimit = ReadDouble(row, 11),
                CalcEnable = ReadInt(row, 12),
                CalcConfig = ReadInt(row, 13),
                CalcMeasurement = ReadInt(row, 14),
                SignalName = ReadText(row, 15),
                Unit = ReadText(row, 16)
            })
            .ToList();
    }

    private static List<CounterConfig> ReadCounterTable(XLWorkbook workbook, string sheetName, string tableName)
    {
        var table = workbook.Worksheet(sheetName).Table(tableName);
        return table.DataRange.Rows()
            .Select(row => new CounterConfig
            {
                ModuleNumber = ReadInt(row, 1),
                Config = ReadConfig(row, 3),
                Id = ReadInt(row, 4),
                Structure = ReadText(row, 5),
                MeasurementName = ReadText(row, 6),
                FilterTime = ReadDouble(row, 7),
                CalcConfig = ReadInt(row, 8),
                CalcMeasurement = ReadInt(row, 9),
                SignalName = ReadText(row, 10),
                Unit = ReadText(row, 11)
            })
            .ToList();
    }

    private static List<AnalogOutputConfig> ReadAnalogOutputTable(XLWorkbook workbook, string sheetName, string tableName)
    {
        var table = workbook.Worksheet(sheetName).Table(tableName);
        return table.DataRange.Rows()
            .Select(row => new AnalogOutputConfig
            {
                ModuleNumber = ReadInt(row, 1),
                Port = ReadInt(row, 3),
                Config = ReadConfig(row, 4),
                Id = ReadInt(row, 5)
            })
            .ToList();
    }

    private static ModuleCatalog ReadModuleCatalog(XLWorkbook workbook)
    {
        return new ModuleCatalog
        {
            DigitalInputs = ReadModuleNameRows(workbook, "MODULE_NAMES", "DI_MODUL_NAMES", 1, 2, 16),
            DigitalOutputs = ReadModuleNameRows(workbook, "MODULE_NAMES", "DO_MODUL_NAMES", 4, 5, 16),
            AnalogInputs = ReadModuleNameRows(workbook, "MODULE_NAMES", "AI_MODUL_NAMES", 7, 8, 6),
            RtdInputs = ReadModuleNameRows(workbook, "MODULE_NAMES", "RTD_MODUL_NAMES", 13, 14, 6),
            Counters = ReadModuleNameRows(workbook, "MODULE_NAMES", "COUNTER_MODUL_NAMES", 10, 11, 4),
            AnalogOutputs = ReadModuleNameRows(workbook, "MODULE_NAMES", "AO_MODUL_NAMES", 16, 17, 2)
        };
    }

    private static List<ModuleNameRow> ReadModuleNameRows(
        XLWorkbook workbook,
        string sheetName,
        string tableName,
        int moduleNumberColumn,
        int moduleNameColumn,
        int defaultCount)
    {
        var defaultRows = ModuleCatalog.CreateDefaultRows(defaultCount);
        if (!workbook.TryGetWorksheet(sheetName, out var worksheet))
        {
            return defaultRows;
        }

        var tableRows = worksheet.Tables.TryGetTable(tableName, out var table)
            ? table.DataRange.Rows()
            : null;

        var rows = tableRows is not null
            ? tableRows
            .Select(row => new ModuleNameRow
            {
                ModuleNumber = ReadInt(row, 1),
                ModuleName = ReadText(row, 2)
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.ModuleName))
            .ToList()
            : ReadModuleNameRowsFromColumns(worksheet, moduleNumberColumn, moduleNameColumn, defaultCount);

        return MergeModuleRows(defaultRows, rows);
    }

    private static List<ModuleNameRow> ReadModuleNameRowsFromColumns(
        IXLWorksheet worksheet,
        int moduleNumberColumn,
        int moduleNameColumn,
        int defaultCount)
    {
        var rows = new List<ModuleNameRow>();
        for (var rowNumber = 7; rowNumber < 7 + defaultCount; rowNumber++)
        {
            var moduleNumberCell = worksheet.Cell(rowNumber, moduleNumberColumn);
            var moduleNameCell = worksheet.Cell(rowNumber, moduleNameColumn);
            var moduleNumber = moduleNumberCell.Value.IsNumber
                ? Convert.ToInt32(moduleNumberCell.GetDouble())
                : rowNumber - 7;

            rows.Add(new ModuleNameRow
            {
                ModuleNumber = moduleNumber,
                ModuleName = moduleNameCell.GetFormattedString()
            });
        }

        return rows;
    }

    private static List<ModuleNameRow> MergeModuleRows(List<ModuleNameRow> defaultRows, List<ModuleNameRow> importedRows)
    {
        foreach (var defaultRow in defaultRows)
        {
            var importedRow = importedRows.FirstOrDefault(row => row.ModuleNumber == defaultRow.ModuleNumber);
            if (importedRow is not null)
            {
                defaultRow.ModuleName = importedRow.ModuleName;
            }
        }

        return defaultRows;
    }

    private static DbCatalog ReadDbCatalog(XLWorkbook workbook)
    {
        return new DbCatalog
        {
            DiRows = ReadDbDiRows(workbook),
            DiSystemNameRows = ReadDbDiSystemNameRows(workbook),
            DiColorSettings = ReadDbDiColorValues(workbook),
            DoRows = ReadDbDoRows(workbook),
            DoSystemNameRows = ReadDbDoSystemNameRows(workbook),
            MeasRows = ReadDbMeasRows(workbook),
            MeasSystemNameRows = ReadDbMeasSystemNameRows(workbook),
            AoRows = ReadDbAoRows(workbook),
            AoSystemNameRows = ReadDbAoSystemNameRows(workbook),
            AlarmRows = ReadDbAlarmRows(workbook),
            AlarmSystemNameRows = ReadDbAlarmSystemNameRows(workbook),
            ParameterRows = ReadDbParameterRows(workbook),
            ParameterSystemNameRows = ReadDbParameterSystemNameRows(workbook),
            ValidationLists = ReadDbValidationLists(workbook)
        };
    }

    private static List<DbValidationListSetting> ReadDbValidationLists(XLWorkbook workbook)
    {
        return
        [
            CreateValidationRow(workbook, DbValidationDefaults.DbDiColorValues, "DB DI - COLOR VAL 0/1", "DB DI", "AF14:AF19"),
            CreateValidationRow(workbook, DbValidationDefaults.DbDiAlarmBoolean, "DB DI - ALM CONFIG / ALM INVERT", "DB DI", "AH14:AH15"),
            CreateValidationRow(workbook, DbValidationDefaults.DbMeasLoggingEnable, "DB MEAS - Logging enable", "DB MEAS", "AG6:AG7"),
            CreateValidationRow(workbook, DbValidationDefaults.DbMeasUnit, "DB MEAS / DB AO - UNIT", "DB MEAS", "AH6:AH33"),
            CreateValidationRow(workbook, DbValidationDefaults.DbMeasLoggingTime, "DB MEAS - Logging time (s)", "DB MEAS", "AI7:AI13"),
            CreateValidationRow(workbook, DbValidationDefaults.DbMeasScope, "DB MEAS - source scope", "DB MEAS", "AJ6:AJ10"),
            CreateValidationRow(workbook, DbValidationDefaults.DbAlmColorMode, "DB ALM - coloring mode", "DB ALM", "AO5:AO6"),
            CreateValidationRow(workbook, DbValidationDefaults.DbAlmEnableDisable, "DB ALM - Enable/Disable", "ALM SETTINGS", "F3:F4"),
            CreateValidationRow(workbook, DbValidationDefaults.DbAlmYesNo, "DB ALM - NOT/YES", "ALM SETTINGS", "G2:G3"),
            CreateValidationRow(workbook, DbValidationDefaults.DbAlmTimeBase, "DB ALM - TIME base / source columns", "ALM SETTINGS", "H2:H7"),
            CreateValidationRow(workbook, DbValidationDefaults.DbAlmAlarmClass, "DB ALM - ALARM CLASS / SPARE", "ALM SETTINGS", "C3:C7"),
            CreateValidationRow(workbook, DbValidationDefaults.DbAlmBlockingClass, "DB ALM - BLOCKING CLASS", "ALARM BLOCKING CLASS", "B2:B16"),
            CreateValidationRow(workbook, DbValidationDefaults.DbAlmReactToUnit, "DB ALM - REACT TO UNIT", "ALARM BLOCKING CLASS", "E2:E8"),
            CreateValidationRow(workbook, DbValidationDefaults.DbParInUse, "DB PAR - IN USE", "DB PAR", "AB5:AB6"),
            CreateValidationRow(workbook, DbValidationDefaults.DbParType, "DB PAR - PAR TYPE", "DB PAR", "AA7:AA9"),
            CreateValidationRow(workbook, DbValidationDefaults.ListParProgress, "LIST - PAR - progress", "LIST - PAR", "AA1:AA3")
        ];
    }

    private static DbValidationListSetting CreateValidationRow(XLWorkbook workbook, string key, string name, string sheetName, string rangeAddress)
    {
        var values = workbook.TryGetWorksheet(sheetName, out var worksheet)
            ? worksheet.Range(rangeAddress).Cells().Select(cell => NormalizeExcelText(cell.GetFormattedString())).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        return new DbValidationListSetting
        {
            Key = key,
            Name = name,
            ValuesText = string.Join(Environment.NewLine, values)
        };
    }

    private static List<DeviceConfig> ReadDeviceTable(XLWorkbook workbook)
    {
        if (!workbook.TryGetWorksheet("DEVICES", out var worksheet)
            || !worksheet.Tables.TryGetTable("SENTRON", out var table))
        {
            return DeviceConfig.CreateDefaultRows();
        }

        var headers = table.HeadersRow().Cells()
            .Select((cell, index) => new { Header = NormalizeHeader(cell.GetString()), Index = index + 1 })
            .Where(item => !string.IsNullOrWhiteSpace(item.Header))
            .GroupBy(item => item.Header, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

        return table.DataRange.Rows()
            .Select(row => new DeviceConfig
            {
                Config = ReadConfig(row, FindColumn(headers, 1, "CONFIG")),
                SentronName = ReadText(row, FindColumn(headers, 2, "SENTRON NAME", "SENTRON")),
                FirstValueId = ReadInt(row, FindColumn(headers, 3, "FIRST VALUE ID")),
                PowerScalingFactor = ReadDouble(row, FindColumn(headers, 4, "POWER SCALING FACTOR")),
                VoltageScaleFactor = ReadDouble(row, FindColumn(headers, 5, "VOLTAGE SCALE FACTOR")),
                CurrentScalingFactor = ReadDouble(row, FindColumn(headers, 6, "CURRENT SCALING FACTOR"))
            })
            .ToList();
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> headers, int fallback, params string[] candidates)
    {
        foreach (var candidate in candidates.Select(NormalizeHeader))
        {
            if (headers.TryGetValue(candidate, out var index))
            {
                return index;
            }
        }

        return fallback;
    }

    private static string NormalizeHeader(string value)
    {
        return string.Join(" ", (value ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    private static List<DbAoRow> ReadDbAoRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB AO").Table("DB_AO");
        return table.DataRange.Rows()
            .Select(row => new DbAoRow
            {
                AoSystemName = ReadText(row, 1),
                AoPlcName = ReadText(row, 1),
                AoNumber = ReadInt(row, 2),
                NameEnglish = ReadText(row, 3),
                NameOther = ReadText(row, 4),
                Unit = ReadText(row, 5),
                TrueText = ReadText(row, 6),
                ModuleName = ReadText(row, 7),
                ModulePort = ReadInt(row, 8),
                Low = ReadDouble(row, 9),
                High = ReadDouble(row, 10),
                Offset = ReadDouble(row, 11),
                HighLimit = ReadDouble(row, 12)
            })
            .ToList();
    }

    private static List<DbStringRow> ReadDbMeasRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB MEAS").Table("DB_MEAS");
        return table.DataRange.Rows()
            .Select(row => new DbStringRow
            {
                PlcStructure = ReadText(row, 1),
                Id = ReadInt(row, 2),
                PlcName = ReadText(row, 3),
                NameEnglish = ReadText(row, 4),
                NameOther = ReadText(row, 5),
                Unit = ReadText(row, 6),
                LoggingEnable = ReadText(row, 7),
                LoggingTime = ReadText(row, 8),
                TrueText = ReadText(row, 9),
                Type = ReadText(row, 10),
                ModuleName = ReadText(row, 11),
                ModulePort = ReadInt(row, 12)
            })
            .ToList();
    }

    private static List<DbStringRow> ReadDbAlarmRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB ALM").Table("DB_ALM");
        var descriptions = ReadAlarmDescriptions(workbook);
        return table.DataRange.Rows()
            .Select(row => new DbStringRow
            {
                PlcStructure = ReadText(row, 1),
                Id = ReadInt(row, 2),
                PlcName = ReadText(row, 3),
                SystemNameEnglish = ReadText(row, 4),
                NameEnglish = ReadText(row, 5),
                SystemNameOther = ReadText(row, 6),
                NameOther = ReadText(row, 7),
                AlarmControl = ReadText(row, 8),
                SrAlarm = ReadText(row, 9),
                AlarmClass = ReadText(row, 10),
                ReactToUnit = ReadText(row, 11),
                Spare = ReadText(row, 12),
                BlockingClass1 = ReadText(row, 13),
                BlockingClass2 = ReadText(row, 14),
                Delay = ReadText(row, 15),
                TimeBase = ReadText(row, 16),
                SourceType = ReadText(row, 17),
                SourceDigId = ReadText(row, 18),
                SourceAnaId = ReadText(row, 19),
                Used = ReadText(row, 20),
                DescriptionEnglish = descriptions.TryGetValue(ReadInt(row, 2), out var description) ? description.English : "",
                DescriptionOther = descriptions.TryGetValue(ReadInt(row, 2), out description) ? description.Other : ""
            })
            .ToList();
    }

    private static List<DbStringRow> ReadDbParameterRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB PAR").Table("DB_PAR");
        var descriptions = ReadParameterDescriptions(workbook);
        return table.DataRange.Rows()
            .Select(row => new DbStringRow
            {
                InUse = ReadText(row, 1),
                Id = ReadInt(row, 2),
                PlcStructure = ReadText(row, 3),
                PlcName = ReadText(row, 4),
                NameEnglish = ReadText(row, 5),
                NameOther = ReadText(row, 6),
                ParameterType = ReadText(row, 7),
                IntegerValue = ReadText(row, 8),
                TimeValue = ReadText(row, 9),
                RealValue = ReadText(row, 10),
                Unit = ReadText(row, 11),
                MinValue = ReadText(row, 12),
                MaxValue = ReadText(row, 13),
                Ordinator = ReadText(row, 14),
                DescriptionEnglish = descriptions.TryGetValue(ReadInt(row, 2), out var description) ? description.English : "",
                DescriptionOther = descriptions.TryGetValue(ReadInt(row, 2), out description) ? description.Other : ""
            })
            .ToList();
    }

    private static Dictionary<int, (string English, string Other)> ReadAlarmDescriptions(XLWorkbook workbook)
    {
        if (!workbook.TryGetWorksheet("LIST - ALM", out var worksheet) || !worksheet.Tables.TryGetTable("Tablica98", out var table))
        {
            return [];
        }

        return table.DataRange.Rows()
            .GroupBy(row => ReadInt(row, 1))
            .ToDictionary(group => group.Key, group => (ReadText(group.First(), 6), ReadText(group.First(), 8)));
    }

    private static Dictionary<int, (string English, string Other)> ReadParameterDescriptions(XLWorkbook workbook)
    {
        if (!workbook.TryGetWorksheet("LIST - PAR", out var worksheet) || !worksheet.Tables.TryGetTable("Tablica98100", out var table))
        {
            return [];
        }

        return table.DataRange.Rows()
            .GroupBy(row => ReadInt(row, 1))
            .ToDictionary(group => group.Key, group => (ReadText(group.First(), 6), ReadText(group.First(), 11)));
    }

    private static List<DbStringRow> ReadDbDiRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB DI").Table("DB_DI");
        return table.DataRange.Rows()
            .Select(row => new DbStringRow
            {
                Id = ReadInt(row, 1),
                PlcStructure = ReadText(row, 3),
                PlcName = ReadText(row, 4),
                SystemNameEnglish = ReadText(row, 5),
                NameEnglish = ReadText(row, 6),
                SystemNameOther = ReadText(row, 7),
                NameOther = ReadText(row, 8),
                TrueText = ReadText(row, 9),
                ModuleName = ReadText(row, 10),
                ModulePort = ReadInt(row, 11),
                ColorValue0 = ReadText(row, 12),
                ColorValue1 = ReadText(row, 13),
                AlarmConfig = ReadText(row, 14),
                AlarmInvert = ReadText(row, 15),
                AlarmId = ReadText(row, 16),
                ExtraColumn1 = ReadText(row, 17),
                ExtraColumn2 = ReadText(row, 18)
            })
            .ToList();
    }

    private static List<DbStringRow> ReadDbDoRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB DO").Table("DB_DO");
        return table.DataRange.Rows()
            .Select(row => new DbStringRow
            {
                Id = ReadInt(row, 1),
                PlcStructure = ReadText(row, 3),
                PlcName = ReadText(row, 4),
                SystemNameEnglish = ReadText(row, 5),
                NameEnglish = ReadText(row, 6),
                SystemNameOther = ReadText(row, 7),
                NameOther = ReadText(row, 8),
                TrueText = ReadText(row, 9),
                ModuleName = ReadText(row, 10),
                ModulePort = ReadInt(row, 11)
            })
            .Take(288)
            .ToList();
    }

    private static List<DbDiSystemNameRow> ReadDbDiSystemNameRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB DI").Table("DB_DI_SYS_NAMES");
        return ReadDbSystemNameRows(table);
    }

    private static List<DbDiSystemNameRow> ReadDbDoSystemNameRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB DO").Table("DB_DO_SYS_NAMES");
        return ReadDbSystemNameRows(table);
    }

    private static List<DbDiSystemNameRow> ReadDbMeasSystemNameRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB MEAS").Table("DB_MEAS_SYS_NAMES");
        return ReadDbSystemNameRows(table);
    }

    private static List<DbDiSystemNameRow> ReadDbAoSystemNameRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB AO").Table("DB_AO_SYS_NAMES");
        return ReadDbSystemNameRows(table);
    }

    private static List<DbDiSystemNameRow> ReadDbAlarmSystemNameRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB ALM").Table("DB_ALM_SYS_NAMES");
        return ReadDbSystemNameRows(table);
    }

    private static List<DbDiSystemNameRow> ReadDbParameterSystemNameRows(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB PAR").Table("DB_PAR_SYS_NAMES");
        return ReadDbSystemNameRows(table);
    }

    private static List<DbDiSystemNameRow> ReadDbSystemNameRows(IXLTable table)
    {
        return table.DataRange.Rows()
            .Select(row => new DbDiSystemNameRow
            {
                SystemNumber = ReadInt(row, 1),
                SystemPlcShortName = ReadText(row, 2),
                SystemPlcLongName = ReadText(row, 3),
                PlcVariableName = ReadText(row, 4),
                HmiSystemName = ReadText(row, 5),
                NameLength = ReadInt(row, 6),
                SimulatorDataType = ReadText(row, 7)
            })
            .ToList();
    }

    private static List<DbColorSetting> ReadDbDiColorValues(XLWorkbook workbook)
    {
        var table = workbook.Worksheet("DB DI").Table("DB_DI_COLOR");
        return table.DataRange.Rows()
            .Select(row =>
            {
                var name = ReadText(row, 2);
                return new DbColorSetting
                {
                    Value = ReadInt(row, 1),
                    Name = name,
                    Color = DefaultColorForName(name)
                };
            })
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string DefaultColorForName(string name)
    {
        return name.Trim().ToUpperInvariant() switch
        {
            "WHITE" => "White",
            "YELLOW" => "Yellow",
            "RED" => "Red",
            "GREEN" => "LimeGreen",
            "BLUE" => "DodgerBlue",
            _ => "Transparent"
        };
    }

    private static List<DbStringRow> ReadDbStringRows(
        XLWorkbook workbook,
        string sheetName,
        string tableName,
        int idColumn,
        int plcStructureColumn,
        int plcNameColumn,
        int systemEnglishColumn,
        int nameEnglishColumn,
        int systemOtherColumn,
        int nameOtherColumn,
        int unitColumn)
    {
        var table = workbook.Worksheet(sheetName).Table(tableName);
        return table.DataRange.Rows()
            .Select(row => new DbStringRow
            {
                Id = ReadInt(row, idColumn),
                PlcStructure = ReadText(row, plcStructureColumn),
                PlcName = ReadText(row, plcNameColumn),
                SystemNameEnglish = ReadText(row, systemEnglishColumn),
                NameEnglish = ReadText(row, nameEnglishColumn),
                SystemNameOther = ReadText(row, systemOtherColumn),
                NameOther = ReadText(row, nameOtherColumn),
                Unit = unitColumn > 0 ? ReadText(row, unitColumn) : ""
            })
            .ToList();
    }

    private static int ReadInt(IXLRangeRow row, int column)
    {
        var cell = row.Cell(column);
        if (cell.Value.IsNumber)
        {
            return Convert.ToInt32(cell.GetDouble());
        }

        return int.TryParse(cell.GetFormattedString(), out var result) ? result : 0;
    }

    private static double ReadDouble(IXLRangeRow row, int column)
    {
        var cell = row.Cell(column);
        if (cell.Value.IsNumber)
        {
            return cell.GetDouble();
        }

        return double.TryParse(cell.GetFormattedString(), out var result) ? result : 0;
    }

    private static string ReadText(IXLRangeRow row, int column)
    {
        return NormalizeExcelText(row.Cell(column).GetFormattedString());
    }

    private static string NormalizeText(string value)
    {
        return value.Replace("Â°C", "°C");
    }

    private static SignalConfigState ReadConfig(IXLRangeRow row, int column)
    {
        var value = ReadText(row, column).Trim().ToUpperInvariant();
        return value switch
        {
            "2" or "INVERTED" => SignalConfigState.Inverted,
            "1" or "ENABLED" => SignalConfigState.Enabled,
            _ => SignalConfigState.Disabled
        };
    }

    private static string NormalizeExcelText(string value)
    {
        if (value.IndexOfAny(['Ã', 'Â', 'â']) < 0)
        {
            return value;
        }

        try
        {
            return Encoding.UTF8.GetString(LegacyExcelEncoding.GetBytes(value));
        }
        catch (DecoderFallbackException)
        {
            return value.Replace("Ã‚Â°C", "Â°C").Replace("Â°C", "°C");
        }
    }

    private static Encoding CreateLegacyExcelEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
