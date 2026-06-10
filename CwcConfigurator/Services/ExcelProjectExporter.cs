using ClosedXML.Excel;
using CwcConfigurator.Domain;
using System.IO;

namespace CwcConfigurator.Services;

public sealed class ExcelProjectExporter
{
    private const string ValidationSheetName = "_CWC_VALIDATION";

    public void ExportOnePlc(CwcProject project, string templatePath, string outputPath)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Excel template does not exist.", templatePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
        File.Copy(templatePath, outputPath, overwrite: true);

        using var workbook = new XLWorkbook(outputPath);
        WriteModuleCatalog(workbook, project.ModuleCatalog);
        WriteDigitalTable(workbook, "DI", "CONFIG_DI", project.DigitalInputs);
        WriteDigitalTable(workbook, "DO", "CONFIG_DO", project.DigitalOutputs);
        WriteAnalogTable(workbook, "AI", "CONFIG_AI", project.AnalogInputs);
        WriteAnalogTable(workbook, "RTD", "CONFIG_RTD", project.RtdInputs);
        WriteCounterTable(workbook, "COUNTER", "CONFIG_COUNTER", project.Counters);
        WriteAnalogOutputTable(workbook, "AO", "CONFIG_AO", project.AnalogOutputs);
        WriteDevices(workbook, project.Devices);
        var validationRanges = WriteValidationSourceSheet(workbook, project.DbCatalog);
        WriteDbDiRows(workbook, "DB DI", "DB_DI", project.DbCatalog.DiRows, project.DbCatalog, validationRanges);
        WriteDbDoRows(workbook, "DB DO", "DB_DO", project.DbCatalog.DoRows);
        WriteDbSystemNameRows(workbook, "DB DI", "DB_DI_SYS_NAMES", project.DbCatalog.DiSystemNameRows);
        WriteDbSystemNameRows(workbook, "DB DO", "DB_DO_SYS_NAMES", project.DbCatalog.DoSystemNameRows);
        WriteDbMeasRows(workbook, project.DbCatalog.MeasRows, project.DbCatalog, validationRanges);
        WriteDbSystemNameRows(workbook, "DB MEAS", "DB_MEAS_SYS_NAMES", project.DbCatalog.MeasSystemNameRows);
        WriteDbAoRows(workbook, project.DbCatalog.AoRows, project.DbCatalog, validationRanges);
        WriteDbSystemNameRows(workbook, "DB AO", "DB_AO_SYS_NAMES", project.DbCatalog.AoSystemNameRows);
        WriteDbAlarmRows(workbook, project.DbCatalog.AlarmRows, project.DbCatalog, validationRanges);
        WriteDbSystemNameRows(workbook, "DB ALM", "DB_ALM_SYS_NAMES", project.DbCatalog.AlarmSystemNameRows);
        WriteDbParameterRows(workbook, project.DbCatalog.ParameterRows, project.DbCatalog, validationRanges);
        WriteDbSystemNameRows(workbook, "DB PAR", "DB_PAR_SYS_NAMES", project.DbCatalog.ParameterSystemNameRows);
        workbook.Save();
    }

    private static void WriteModuleCatalog(XLWorkbook workbook, ModuleCatalog catalog)
    {
        WriteModuleRows(workbook, "MODULE_NAMES", "DI_MODUL_NAMES", catalog.DigitalInputs);
        WriteModuleRows(workbook, "MODULE_NAMES", "DO_MODUL_NAMES", catalog.DigitalOutputs);
        WriteModuleRows(workbook, "MODULE_NAMES", "AI_MODUL_NAMES", catalog.AnalogInputs);
        WriteModuleRows(workbook, "MODULE_NAMES", "RTD_MODUL_NAMES", catalog.RtdInputs);
        WriteModuleRows(workbook, "MODULE_NAMES", "COUNTER_MODUL_NAMES", catalog.Counters);
        WriteModuleRows(workbook, "MODULE_NAMES", "AO_MODUL_NAMES", catalog.AnalogOutputs);
    }

    private static void WriteModuleRows(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<ModuleNameRow> sourceRows)
    {
        WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.ModuleNumber);
            SetCell(rangeRow, 2, source.ModuleName);
        });
    }

    private static void WriteDigitalTable(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<DigitalSignalConfig> sourceRows)
    {
        WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.ModuleNumber);
            SetCell(rangeRow, 2, source.ModuleName);
            SetCell(rangeRow, 3, source.Port);
            SetCell(rangeRow, 4, ConfigText(source.Config));
            SetCell(rangeRow, 5, source.Id);
            SetCell(rangeRow, 6, source.Structure);
            SetCell(rangeRow, 7, source.StatusNameDefinition);
            SetCell(rangeRow, 8, source.CalcConfig);
            SetCell(rangeRow, 9, source.CalcId);
            SetCell(rangeRow, 10, source.StructName);
            SetCell(rangeRow, 11, source.SignalName);
        });
    }

    private static void WriteAnalogTable(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<AnalogSignalConfig> sourceRows)
    {
        WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.ModuleNumber);
            SetCell(rangeRow, 2, source.ModuleName);
            SetCell(rangeRow, 3, source.Port);
            SetCell(rangeRow, 4, ConfigText(source.Config));
            SetCell(rangeRow, 5, source.Id);
            SetCell(rangeRow, 6, source.Structure);
            SetCell(rangeRow, 7, source.MeasurementName);
            SetCell(rangeRow, 8, source.FilterTime);
            SetCell(rangeRow, 9, ConfigText(source.CustomLimits));
            SetCell(rangeRow, 10, source.LowLimit);
            SetCell(rangeRow, 11, source.HighLimit);
            SetCell(rangeRow, 12, source.CalcEnable);
            SetCell(rangeRow, 13, source.CalcConfig);
            SetCell(rangeRow, 14, source.CalcMeasurement);
            SetCell(rangeRow, 15, source.SignalName);
            SetCell(rangeRow, 16, source.Unit);
        });
    }

    private static void WriteCounterTable(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<CounterConfig> sourceRows)
    {
        WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.ModuleNumber);
            SetCell(rangeRow, 2, source.ModuleName);
            SetCell(rangeRow, 3, ConfigText(source.Config));
            SetCell(rangeRow, 4, source.Id);
            SetCell(rangeRow, 5, source.Structure);
            SetCell(rangeRow, 6, source.MeasurementName);
            SetCell(rangeRow, 7, source.FilterTime);
            SetCell(rangeRow, 8, source.CalcConfig);
            SetCell(rangeRow, 9, source.CalcMeasurement);
            SetCell(rangeRow, 10, source.SignalName);
            SetCell(rangeRow, 11, source.Unit);
        });
    }

    private static void WriteAnalogOutputTable(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<AnalogOutputConfig> sourceRows)
    {
        WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.ModuleNumber);
            SetCell(rangeRow, 2, source.ModuleName);
            SetCell(rangeRow, 3, source.Port);
            SetCell(rangeRow, 4, ConfigText(source.Config));
            SetCell(rangeRow, 5, source.Id);
            SetCell(rangeRow, 6, source.SystemName);
            SetCell(rangeRow, 7, source.SignalName);
        });
    }

    private static void WriteDevices(XLWorkbook workbook, IReadOnlyList<DeviceConfig> sourceRows)
    {
        WriteRows(workbook, "DEVICES", "SENTRON", sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, ConfigText(source.Config));
            SetCell(rangeRow, 2, source.SentronName);
            SetCell(rangeRow, 3, source.FirstValueId);
            SetCell(rangeRow, 4, source.PowerScalingFactor);
            SetCell(rangeRow, 5, source.VoltageScaleFactor);
            SetCell(rangeRow, 6, source.CurrentScalingFactor);
        });
    }

    private static void WriteDbDiRows(
        XLWorkbook workbook,
        string sheetName,
        string tableName,
        IReadOnlyList<DbStringRow> sourceRows,
        DbCatalog catalog,
        IReadOnlyDictionary<string, IXLRange> validationRanges)
    {
        var table = WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.Id);
            SetCell(rangeRow, 3, source.PlcStructure);
            SetCell(rangeRow, 4, source.PlcName);
            SetCell(rangeRow, 5, source.SystemNameEnglish);
            SetCell(rangeRow, 6, source.NameEnglish);
            SetCell(rangeRow, 7, source.SystemNameOther);
            SetCell(rangeRow, 8, source.NameOther);
            SetCell(rangeRow, 9, source.TrueText);
            SetCell(rangeRow, 10, source.ModuleName);
            SetCell(rangeRow, 11, source.ModulePort);
            SetCell(rangeRow, 12, ValidationDisplay(catalog, DbValidationDefaults.DbDiColorValues, source.ColorValue0));
            SetCell(rangeRow, 13, ValidationDisplay(catalog, DbValidationDefaults.DbDiColorValues, source.ColorValue1));
            SetCell(rangeRow, 14, ValidationDisplay(catalog, DbValidationDefaults.DbDiAlarmBoolean, source.AlarmConfig));
            SetCell(rangeRow, 15, ValidationDisplay(catalog, DbValidationDefaults.DbDiAlarmBoolean, source.AlarmInvert));
            SetCell(rangeRow, 16, source.AlarmId);
            SetCell(rangeRow, 17, source.ExtraColumn1);
            SetCell(rangeRow, 18, source.ExtraColumn2);
        });

        ApplyListValidation(table, 12, DbValidationDefaults.DbDiColorValues, validationRanges);
        ApplyListValidation(table, 13, DbValidationDefaults.DbDiColorValues, validationRanges);
        ApplyListValidation(table, 14, DbValidationDefaults.DbDiAlarmBoolean, validationRanges);
        ApplyListValidation(table, 15, DbValidationDefaults.DbDiAlarmBoolean, validationRanges);
    }

    private static void WriteDbDoRows(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<DbStringRow> sourceRows)
    {
        WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.Id);
            SetCell(rangeRow, 3, source.PlcStructure);
            SetCell(rangeRow, 4, source.PlcName);
            SetCell(rangeRow, 5, source.SystemNameEnglish);
            SetCell(rangeRow, 6, source.NameEnglish);
            SetCell(rangeRow, 7, source.SystemNameOther);
            SetCell(rangeRow, 8, source.NameOther);
            SetCell(rangeRow, 9, source.TrueText);
            SetCell(rangeRow, 10, source.ModuleName);
            SetCell(rangeRow, 11, source.ModulePort);
        });
    }

    private static void WriteDbSystemNameRows(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<DbDiSystemNameRow> sourceRows)
    {
        WriteRows(workbook, sheetName, tableName, sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.SystemNumber);
            SetCell(rangeRow, 2, source.SystemPlcShortName);
            SetCell(rangeRow, 3, source.SystemPlcLongName);
            SetCell(rangeRow, 4, source.PlcVariableName);
            SetCell(rangeRow, 5, source.HmiSystemName);
            SetCell(rangeRow, 6, source.NameLength);
            SetCell(rangeRow, 7, source.SimulatorDataType);
        });
    }

    private static void WriteDbMeasRows(
        XLWorkbook workbook,
        IReadOnlyList<DbStringRow> sourceRows,
        DbCatalog catalog,
        IReadOnlyDictionary<string, IXLRange> validationRanges)
    {
        var table = WriteRows(workbook, "DB MEAS", "DB_MEAS", sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.PlcStructure);
            SetCell(rangeRow, 2, source.Id);
            SetCell(rangeRow, 3, source.PlcName);
            SetCell(rangeRow, 4, source.NameEnglish);
            SetCell(rangeRow, 5, source.NameOther);
            SetCell(rangeRow, 6, ValidationDisplay(catalog, DbValidationDefaults.DbMeasUnit, source.Unit));
            SetCell(rangeRow, 7, ValidationDisplay(catalog, DbValidationDefaults.DbMeasLoggingEnable, source.LoggingEnable));
            SetCell(rangeRow, 8, ValidationDisplay(catalog, DbValidationDefaults.DbMeasLoggingTime, source.LoggingTime));
            SetCell(rangeRow, 9, source.TrueText);
            SetCell(rangeRow, 10, source.Type);
            SetCell(rangeRow, 11, source.ModuleName);
            SetCell(rangeRow, 12, source.ModulePort);
        });

        ApplyListValidation(table, 6, DbValidationDefaults.DbMeasUnit, validationRanges);
        ApplyListValidation(table, 7, DbValidationDefaults.DbMeasLoggingEnable, validationRanges);
        ApplyListValidation(table, 8, DbValidationDefaults.DbMeasLoggingTime, validationRanges);
    }

    private static void WriteDbAoRows(
        XLWorkbook workbook,
        IReadOnlyList<DbAoRow> sourceRows,
        DbCatalog catalog,
        IReadOnlyDictionary<string, IXLRange> validationRanges)
    {
        var table = WriteRows(workbook, "DB AO", "DB_AO", sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, string.IsNullOrWhiteSpace(source.AoPlcName) ? source.AoSystemName : source.AoPlcName);
            SetCell(rangeRow, 2, source.AoNumber);
            SetCell(rangeRow, 3, source.NameEnglish);
            SetCell(rangeRow, 4, source.NameOther);
            SetCell(rangeRow, 5, ValidationDisplay(catalog, DbValidationDefaults.DbMeasUnit, source.Unit));
            SetCell(rangeRow, 6, source.TrueText);
            SetCell(rangeRow, 7, source.ModuleName);
            SetCell(rangeRow, 8, source.ModulePort);
            SetCell(rangeRow, 9, source.Low);
            SetCell(rangeRow, 10, source.High);
            SetCell(rangeRow, 11, source.Offset);
            SetCell(rangeRow, 12, source.HighLimit);
        });

        ApplyListValidation(table, 5, DbValidationDefaults.DbMeasUnit, validationRanges);
    }

    private static void WriteDbAlarmRows(
        XLWorkbook workbook,
        IReadOnlyList<DbStringRow> sourceRows,
        DbCatalog catalog,
        IReadOnlyDictionary<string, IXLRange> validationRanges)
    {
        var table = WriteRows(workbook, "DB ALM", "DB_ALM", sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, source.PlcStructure);
            SetCell(rangeRow, 2, source.Id);
            SetCell(rangeRow, 3, source.PlcName);
            SetCell(rangeRow, 4, source.SystemNameEnglish);
            SetCell(rangeRow, 5, source.NameEnglish);
            SetCell(rangeRow, 6, source.SystemNameOther);
            SetCell(rangeRow, 7, source.NameOther);
            SetCell(rangeRow, 8, ValidationDisplay(catalog, DbValidationDefaults.DbAlmEnableDisable, source.AlarmControl));
            SetCell(rangeRow, 9, ValidationDisplay(catalog, DbValidationDefaults.DbAlmYesNo, source.SrAlarm));
            SetCell(rangeRow, 10, ValidationDisplay(catalog, DbValidationDefaults.DbAlmAlarmClass, source.AlarmClass));
            SetCell(rangeRow, 11, ValidationDisplay(catalog, DbValidationDefaults.DbAlmReactToUnit, source.ReactToUnit));
            SetCell(rangeRow, 12, ValidationDisplay(catalog, DbValidationDefaults.DbAlmAlarmClass, source.Spare));
            SetCell(rangeRow, 13, ValidationDisplay(catalog, DbValidationDefaults.DbAlmBlockingClass, source.BlockingClass1));
            SetCell(rangeRow, 14, ValidationDisplay(catalog, DbValidationDefaults.DbAlmBlockingClass, source.BlockingClass2));
            SetCell(rangeRow, 15, source.Delay);
            SetCell(rangeRow, 16, ValidationDisplay(catalog, DbValidationDefaults.DbAlmTimeBase, source.TimeBase));
            SetCell(rangeRow, 17, source.SourceType);
            SetCell(rangeRow, 18, source.SourceDigId);
            SetCell(rangeRow, 19, source.SourceAnaId);
            SetCell(rangeRow, 20, ValidationDisplay(catalog, DbValidationDefaults.DbParInUse, source.Used));
        });
        ApplyListValidation(table, 8, DbValidationDefaults.DbAlmEnableDisable, validationRanges);
        ApplyListValidation(table, 9, DbValidationDefaults.DbAlmYesNo, validationRanges);
        ApplyListValidation(table, 10, DbValidationDefaults.DbAlmAlarmClass, validationRanges);
        ApplyListValidation(table, 11, DbValidationDefaults.DbAlmReactToUnit, validationRanges);
        ApplyListValidation(table, 12, DbValidationDefaults.DbAlmAlarmClass, validationRanges);
        ApplyListValidation(table, 13, DbValidationDefaults.DbAlmBlockingClass, validationRanges);
        ApplyListValidation(table, 14, DbValidationDefaults.DbAlmBlockingClass, validationRanges);
        ApplyListValidation(table, 16, DbValidationDefaults.DbAlmTimeBase, validationRanges);
        ApplyListValidation(table, 20, DbValidationDefaults.DbParInUse, validationRanges);
        WriteDescriptions(workbook, "LIST - ALM", "Tablica98", sourceRows);
    }

    private static void WriteDbParameterRows(
        XLWorkbook workbook,
        IReadOnlyList<DbStringRow> sourceRows,
        DbCatalog catalog,
        IReadOnlyDictionary<string, IXLRange> validationRanges)
    {
        var table = WriteRows(workbook, "DB PAR", "DB_PAR", sourceRows, (rangeRow, source) =>
        {
            SetCell(rangeRow, 1, ValidationDisplay(catalog, DbValidationDefaults.DbParInUse, source.InUse));
            SetCell(rangeRow, 2, source.Id);
            SetCell(rangeRow, 3, source.PlcStructure);
            SetCell(rangeRow, 4, source.PlcName);
            SetCell(rangeRow, 5, source.NameEnglish);
            SetCell(rangeRow, 6, source.NameOther);
            SetCell(rangeRow, 7, ValidationDisplay(catalog, DbValidationDefaults.DbParType, source.ParameterType));
            SetCell(rangeRow, 8, source.IntegerValue);
            SetCell(rangeRow, 9, source.TimeValue);
            SetCell(rangeRow, 10, source.RealValue);
            SetCell(rangeRow, 11, ValidationDisplay(catalog, DbValidationDefaults.DbMeasUnit, source.Unit));
            SetCell(rangeRow, 12, source.MinValue);
            SetCell(rangeRow, 13, source.MaxValue);
            SetCell(rangeRow, 14, source.Ordinator);
        });
        ApplyListValidation(table, 1, DbValidationDefaults.DbParInUse, validationRanges);
        ApplyListValidation(table, 7, DbValidationDefaults.DbParType, validationRanges);
        ApplyListValidation(table, 11, DbValidationDefaults.DbMeasUnit, validationRanges);
        WriteDescriptions(workbook, "LIST - PAR", "Tablica98100", sourceRows);
    }

    private static void WriteDescriptions(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<DbStringRow> sourceRows)
    {
        if (!workbook.TryGetWorksheet(sheetName, out var worksheet) || !worksheet.Tables.TryGetTable(tableName, out var table))
        {
            return;
        }

        var rows = table.DataRange.Rows().ToList();
        for (var i = 0; i < rows.Count && i < sourceRows.Count; i++)
        {
            SetCell(rows[i], 1, sourceRows[i].Id);
            SetCell(rows[i], 6, sourceRows[i].DescriptionEnglish);
            SetCell(rows[i], sheetName.EndsWith("PAR", StringComparison.OrdinalIgnoreCase) ? 11 : 8, sourceRows[i].DescriptionOther);
        }
    }

    private static IXLTable WriteRows<T>(XLWorkbook workbook, string sheetName, string tableName, IReadOnlyList<T> sourceRows, Action<IXLRangeRow, T> writeRow)
    {
        var table = workbook.Worksheet(sheetName).Table(tableName);
        table.ShowAutoFilter = false;
        var targetRows = table.DataRange.Rows().ToList();
        var count = Math.Min(targetRows.Count, sourceRows.Count);
        for (var i = 0; i < count; i++)
        {
            writeRow(targetRows[i], sourceRows[i]);
        }

        for (var i = count; i < targetRows.Count; i++)
        {
            ClearRow(targetRows[i], table);
        }

        return table;
    }

    private static Dictionary<string, IXLRange> WriteValidationSourceSheet(XLWorkbook workbook, DbCatalog catalog)
    {
        if (workbook.TryGetWorksheet(ValidationSheetName, out var existing))
        {
            existing.Delete();
        }

        var worksheet = workbook.AddWorksheet(ValidationSheetName);
        var ranges = new Dictionary<string, IXLRange>(StringComparer.OrdinalIgnoreCase);
        var validationLists = MergeValidationLists(catalog.ValidationLists);
        var column = 1;

        foreach (var setting in validationLists)
        {
            var options = DbValidationListSetting.SplitOptions(setting.ValuesText);
            if (options.Count == 0)
            {
                continue;
            }

            worksheet.Cell(1, column).SetValue(setting.Key);
            for (var row = 0; row < options.Count; row++)
            {
                worksheet.Cell(row + 2, column).SetValue(options[row].DisplayText);
            }

            var range = worksheet.Range(2, column, options.Count + 1, column);
            var rangeName = ValidationRangeName(setting.Key);
            if (workbook.DefinedNames.Contains(rangeName))
            {
                workbook.DefinedNames.Delete(rangeName);
            }

            workbook.DefinedNames.Add(rangeName, range);
            ranges[setting.Key] = range;
            column++;
        }

        worksheet.Columns().AdjustToContents();
        worksheet.Hide();
        return ranges;
    }

    private static List<DbValidationListSetting> MergeValidationLists(IEnumerable<DbValidationListSetting>? projectLists)
    {
        var rows = DbValidationDefaults.Create();
        if (projectLists is null)
        {
            return rows;
        }

        foreach (var projectList in projectLists)
        {
            var existing = rows.FirstOrDefault(row => row.Key.Equals(projectList.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                rows.Add(projectList.Clone());
            }
            else
            {
                existing.Name = projectList.Name;
                existing.ValuesText = projectList.ValuesText;
            }
        }

        return rows;
    }

    private static void ApplyListValidation(IXLTable table, int column, string validationKey, IReadOnlyDictionary<string, IXLRange> validationRanges)
    {
        if (table.DataRange is null || !validationRanges.TryGetValue(validationKey, out var sourceRange))
        {
            return;
        }

        var targetRange = table.DataRange.Column(column);
        var validation = targetRange.CreateDataValidation();
        validation.IgnoreBlanks = true;
        validation.InCellDropdown = true;
        validation.ShowErrorMessage = true;
        validation.ErrorTitle = "Invalid value";
        validation.ErrorMessage = "Select a value from the list.";
        validation.List("=" + ValidationRangeName(validationKey));
    }

    private static string ValidationDisplay(DbCatalog catalog, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var setting = catalog.ValidationLists?.FirstOrDefault(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? DbValidationDefaults.Create().FirstOrDefault(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var option = setting?.Options.FirstOrDefault(candidate =>
            candidate.Value.Equals(value, StringComparison.OrdinalIgnoreCase)
            || candidate.Text.Equals(value, StringComparison.OrdinalIgnoreCase)
            || candidate.DisplayText.Equals(value, StringComparison.OrdinalIgnoreCase));

        return option?.DisplayText ?? value;
    }

    private static string ValidationRangeName(string validationKey)
    {
        var safeName = new string(validationKey.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
        return "CWC_" + safeName;
    }

    private static void ClearRow(IXLRangeRow row, IXLTable table)
    {
        var columnCount = table.RangeAddress.LastAddress.ColumnNumber - table.RangeAddress.FirstAddress.ColumnNumber + 1;
        for (var column = 1; column <= columnCount; column++)
        {
            row.Cell(column).SetValue("");
        }
    }

    private static void SetCell(IXLRangeRow row, int column, object? value)
    {
        var cell = row.Cell(column);
        switch (value)
        {
            case null:
                cell.SetValue("");
                break;
            case int intValue:
                cell.SetValue(intValue);
                break;
            case double doubleValue:
                cell.SetValue(doubleValue);
                break;
            case bool boolValue:
                cell.SetValue(boolValue);
                break;
            default:
                cell.SetValue(value.ToString() ?? "");
                break;
        }
    }

    private static string ConfigText(SignalConfigState state)
    {
        return state switch
        {
            SignalConfigState.Enabled => "Enabled",
            SignalConfigState.Inverted => "Inverted",
            _ => "Disabled"
        };
    }
}
