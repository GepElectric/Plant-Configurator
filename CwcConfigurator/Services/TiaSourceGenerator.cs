using System.Globalization;
using System.IO;
using System.Text;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class TiaSourceGenerator
{
    private readonly ProjectValidator _validator = new();
    private static readonly Encoding TiaFileEncoding = CreateTiaFileEncoding();
    private static readonly Encoding TiaStringFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public GenerationResult GenerateAll(CwcProject project)
    {
        return GenerateSelected(project, GenerationSelection.All());
    }

    public GenerationResult GenerateHwIdBlock(CwcProject project, string outputFolder)
    {
        var result = new GenerationResult();
        new ConfigurationCalculator().Recalculate(project);

        foreach (var issue in _validator.Validate(project).Where(issue => issue.Table.ToUpperInvariant() is "DI" or "DO" or "AI" or "RTD" or "COUNTER" or "AO"))
        {
            result.AddIssue(issue);
        }

        if (!result.Success)
        {
            return result;
        }

        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, "HW_ID_s.db");
        File.WriteAllText(path, BuildHwIdBlockSource(project), TiaStringFileEncoding);
        result.AddFile(path);
        return result;
    }

    public GenerationResult GenerateSelected(CwcProject project, GenerationSelection selection)
    {
        var result = new GenerationResult();
        new ConfigurationCalculator().Recalculate(project);

        foreach (var issue in _validator.Validate(project).Where(issue => ShouldValidateIssue(issue, selection)))
        {
            result.AddIssue(issue);
        }

        if (!result.Success)
        {
            return result;
        }

        if (string.IsNullOrWhiteSpace(project.Paths.OutputFolder))
        {
            project.Paths.OutputFolder = Path.Combine(Environment.CurrentDirectory, "Generated");
        }

        var folders = CreateOutputFolders(project.Paths.OutputFolder);
        if (selection.Summary) WriteSummary(project, result);
        if (selection.DigitalInputs) WriteDigitalConfig("FB_DI_x.udt", "DT_DI_PLC", "DI_MODUL_NUM", "DI_CH_NUM", project.DigitalInputs, result, folders.FbFolder);
        if (selection.DigitalOutputs) WriteDigitalConfig("FB_DO_x.udt", "DT_DO_PLC", "DO_MODUL_NUM", "DO_CH_NUM", project.DigitalOutputs, result, folders.FbFolder);
        if (selection.AnalogInputs) WriteAnalogConfig("FB_AI_x.udt", "DT_AI_PLC", "AI_MODUL_NUM", "AI_CH_NUM", project.AnalogInputs, result, folders.FbFolder, includeType: false);
        if (selection.RtdInputs) WriteAnalogConfig("FB_RTD_x.udt", "DT_RTD_PLC", "RTD_MODUL_NUM", "RTD_CH_NUM", project.RtdInputs, result, folders.FbFolder, includeType: true);
        if (selection.Counters) WriteCounterConfig(project.Counters, result, folders.FbFolder);
        if (selection.AnalogOutputs) WriteAnalogOutputConfig(project.AnalogOutputs, result, folders.FbFolder);
        if (selection.DbDiConfiguration) WriteDbDigitalInputTypeConfiguration(project.DbCatalog, result, folders.DbFolder);
        if (selection.DbDoConfiguration) WriteDbDigitalOutputTypeConfiguration(project.DbCatalog, result, folders.DbFolder);
        if (selection.DbMeasConfiguration) WriteDbMeasurementTypeConfiguration(project.DbCatalog, result, folders.DbFolder);
        if (selection.DbAoConfiguration) WriteDbAoTypeConfiguration(project.DbCatalog, result, folders.DbFolder);
        if (selection.DbAlmConfiguration) WriteDbAlarmTypeConfiguration(project.DbCatalog, result, folders.DbFolder);
        if (selection.DbParConfiguration) WriteDbParameterTypeConfiguration(project.DbCatalog, result, folders.DbFolder);
        if (selection.DbDiStrings || selection.DbDoStrings || selection.DbMeasStrings || selection.DbAlmStrings || selection.DbParStrings)
        {
            WriteDbStringFiles(project, selection, result, folders.StringFolder);
        }
        if (selection.DbDiGroupNames || selection.DbDoGroupNames || selection.DbMeasGroupNames || selection.DbAlmGroupNames || selection.DbParGroupNames)
        {
            WriteDbGroupNameFiles(project, selection, result, folders.StringFolder);
        }

        return result;
    }

    private static void WriteSummary(CwcProject project, GenerationResult result)
    {
        var path = Path.Combine(project.Paths.OutputFolder, "project_summary.txt");
        File.WriteAllText(path, ProjectSummaryWriter.Write(project), TiaFileEncoding);
        result.AddFile(path);
    }

    private static Encoding CreateTiaFileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static bool ShouldValidateIssue(ProjectIssue issue, GenerationSelection selection)
    {
        return issue.Table.ToUpperInvariant() switch
        {
            "DI" => selection.DigitalInputs,
            "DO" => selection.DigitalOutputs,
            "AI" => selection.AnalogInputs,
            "RTD" => selection.RtdInputs,
            "COUNTER" => selection.Counters,
            "AO" => selection.AnalogOutputs,
            "DB_DI" => selection.DbDiConfiguration || selection.DbDiStrings || selection.DbDiGroupNames,
            "DB_DO" => selection.DbDoConfiguration || selection.DbDoStrings || selection.DbDoGroupNames,
            "DB_MEAS" => selection.DbMeasConfiguration || selection.DbMeasStrings || selection.DbMeasGroupNames,
            "DB_ALM" => selection.DbAlmConfiguration || selection.DbAlmStrings || selection.DbAlmGroupNames,
            "DB_PAR" => selection.DbParConfiguration || selection.DbParStrings || selection.DbParGroupNames,
            "PROJECT" => true,
            _ => true
        };
    }

    private static OutputFolders CreateOutputFolders(string outputFolder)
    {
        var dtFolder = Path.Combine(outputFolder, "DT-ovi");
        var folders = new OutputFolders(
            DbFolder: Path.Combine(dtFolder, "DB"),
            StringFolder: Path.Combine(dtFolder, "DB_STRING"),
            FbFolder: Path.Combine(dtFolder, "FB_DI_DO_AI_AO"));
        Directory.CreateDirectory(folders.DbFolder);
        Directory.CreateDirectory(folders.StringFolder);
        Directory.CreateDirectory(folders.FbFolder);
        return folders;
    }

    private static void WriteDigitalConfig(string fileName, string typeName, string moduleConstant, string channelConstant, IEnumerable<DigitalSignalConfig> rows, GenerationResult result, string outputFolder)
    {
        var list = rows.ToList();
        var builder = new StringBuilder();
        AppendTypeHeader(builder, typeName);
        builder.AppendLine();
        builder.AppendLine($"ID : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(row => row.Id.ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"CONFIG : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(row => ((int)row.Config).ToString(CultureInfo.InvariantCulture)))}];");
        AppendFbTypeFooter(builder);

        var path = Path.Combine(outputFolder, fileName);
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void WriteAnalogConfig(string fileName, string typeName, string moduleConstant, string channelConstant, IEnumerable<AnalogSignalConfig> rows, GenerationResult result, string outputFolder, bool includeType)
    {
        var list = rows.ToList();
        var builder = new StringBuilder();
        AppendTypeHeader(builder, typeName);
        builder.AppendLine($"ID : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(row => row.Id.ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"CONFIG : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(row => ((int)row.Config).ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"CUSTOM_LIMITS_ENABLE : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(row => ((int)row.CustomLimits).ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"CUSTOM_LOW_LIMIT : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(row => row.LowLimit.ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"CUSTOM_HIGH_LIMIT : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(row => row.HighLimit.ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"FILTER_TIME : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Real := [{string.Join(" ,", list.Select(row => FormatFilterTime(row.FilterTime)))}];");
        if (includeType)
        {
            builder.AppendLine();
            builder.AppendLine($"\"TYPE\" : Array[0..\"{moduleConstant}\", 0..\"{channelConstant}\"] of Int := [{string.Join(" ,", list.Select(_ => "0"))}];");
        }
        AppendFbTypeFooter(builder);

        var path = Path.Combine(outputFolder, fileName);
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void WriteCounterConfig(IEnumerable<CounterConfig> rows, GenerationResult result, string outputFolder)
    {
        var list = rows.ToList();
        var builder = new StringBuilder();
        AppendTypeHeader(builder, "DT_COUNTER_PLC");
        builder.AppendLine($"ID : Array[0..\"COUNTER_MODUL_NUM\"] of Int := [{string.Join(" ,", list.Select(row => row.Id.ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"CONFIG : Array[0..\"COUNTER_MODUL_NUM\"] of Int := [{string.Join(" ,", list.Select(row => ((int)row.Config).ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine($"FILTER_TIME : Array[0..\"COUNTER_MODUL_NUM\"] of Real := [{string.Join(" ,", list.Select(row => FormatFilterTime(row.FilterTime)))}];");
        AppendFbTypeFooter(builder);

        var path = Path.Combine(outputFolder, "FB_COUNTER_x.udt");
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void WriteAnalogOutputConfig(IEnumerable<AnalogOutputConfig> rows, GenerationResult result, string outputFolder)
    {
        var list = rows.ToList();
        var builder = new StringBuilder();
        AppendTypeHeader(builder, "DT_AO_PLC");
        builder.AppendLine();
        builder.AppendLine($"ID : Array[0..\"AO_MODUL_NUM\", 0..\"AO_CH_NUM\"] of Int := [{string.Join(" ,", list.Select(row => row.Id.ToString(CultureInfo.InvariantCulture)))}];");
        builder.AppendLine();
        builder.AppendLine($"CONFIG : Array[0..\"AO_MODUL_NUM\", 0..\"AO_CH_NUM\"] of Int := [{string.Join(" ,", list.Select(row => ((int)row.Config).ToString(CultureInfo.InvariantCulture)))}];");
        AppendFbTypeFooter(builder);

        var path = Path.Combine(outputFolder, "FB_AO_x.udt");
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void WriteDbDigitalInputTypeConfiguration(DbCatalog catalog, GenerationResult result, string outputFolder)
    {
        var builder = new StringBuilder();
        var rows = catalog.DiRows.OrderBy(row => row.Id).ToList();
        var groups = BuildSystemGroups(catalog.DiSystemNameRows, rows, 32, catalog.DiStructTypePrefix, "DI");
        foreach (var group in groups)
        {
            AppendDbTypeHeader(builder, group.TypeName);
            foreach (var row in group.Rows)
            {
                var used = ToTiaBooleanLiteral(row.TrueText, trueText: "TRUE", trueNumber: 1);
                var inputGroupId = IsTrueValue(row.TrueText) ? 0 : 32000;
                var inputId1 = ToTiaInputInt(row.ModuleName);
                var inputId2 = row.ModulePort ?? 32000;
                var color0 = ResolveColorValue(catalog, row.ColorValue0);
                var color1 = ResolveColorValue(catalog, row.ColorValue1);
                var alarmConfigured = ToTiaBooleanLiteral(row.AlarmConfig, trueText: "TRUE", trueNumber: 1);
                var alarmInvert = IsTrueValue(row.AlarmConfig) ? ToTiaBooleanLiteral(row.AlarmInvert, trueText: "TRUE", trueNumber: 1) : "FALSE";
                var alarmId = IsTrueValue(row.AlarmConfig) ? ToLeadingInt(row.AlarmId) : -1;
                builder.AppendLine($"{TiaName(row.PlcName)} {{ S7_SetPoint := 'False'}} : \"DT_STATUS_SOLO\" :=  (({used},1,{row.Id}), ({inputGroupId}, {inputId1}, {inputId2}), (0, {color0}, {color1}), ({alarmConfigured}, {alarmInvert}, {alarmId}));");
            }

            AppendDbTypeFooter(builder);
        }

        AppendAllType(builder, GetConfigurationDataBlockName(catalog, "DB DI", "DT_DIGITAL_INPUTS_ALL"), groups);

        var path = Path.Combine(outputFolder, GetConfigurationFileName(catalog, "DB DI", "DB_DI.udt"));
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void WriteDbDigitalOutputTypeConfiguration(DbCatalog catalog, GenerationResult result, string outputFolder)
    {
        var builder = new StringBuilder();
        var rows = catalog.DoRows.OrderBy(row => row.Id).ToList();
        var groups = BuildSystemGroups(catalog.DoSystemNameRows, rows, 32, catalog.DoStructTypePrefix, "DO");

        AppendDbTypeHeader(builder, "DT_DIGITAL_OUTPUT_SYSTEM_NAMES");
        builder.AppendLine($"SYS : Array[1..19] of String[32] := [{string.Join(", ", groups.Select(group => $"'{EscapeTiaString(group.HmiName)}'"))}];");
        AppendDbTypeFooter(builder);

        foreach (var group in groups)
        {
            AppendDbTypeHeader(builder, group.TypeName);
            foreach (var row in group.Rows)
            {
                var used = ToTiaBooleanLiteral(row.TrueText, trueText: "TRUE", trueNumber: 1);
                var inputGroupId = IsTrueValue(row.TrueText) ? 0 : 32000;
                var inputId1 = ToTiaInputInt(row.ModuleName);
                var inputId2 = row.ModulePort ?? 32000;
                builder.AppendLine($"{TiaName(row.PlcName)} {{ S7_SetPoint := 'False'}} : \"DT_STATUS_SOLO\" :=  (({used},1,{row.Id}), ({inputGroupId}, {inputId1}, {inputId2}), (0, 0, 0), (FALSE, FALSE, -1));");
            }

            AppendDbTypeFooter(builder);
        }

        AppendAllType(builder, GetConfigurationDataBlockName(catalog, "DB DO", "DT_DIGITAL_OUTPUTS_ALL"), groups);

        var path = Path.Combine(outputFolder, GetConfigurationFileName(catalog, "DB DO", "DB_DO.udt"));
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static string EscapeCsv(string value)
    {
        return value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static void WriteDbMeasurementTypeConfiguration(DbCatalog catalog, GenerationResult result, string outputFolder)
    {
        var builder = new StringBuilder();
        var rows = catalog.MeasRows.OrderBy(row => row.Id).ToList();
        var groups = BuildSystemGroups(catalog.MeasSystemNameRows, rows, 24, catalog.MeasStructTypePrefix, "MEAS");
        foreach (var group in groups)
        {
            AppendDbTypeHeader(builder, group.TypeName);
            foreach (var row in group.Rows)
            {
                var loggingEnable = ToBooleanText(row.LoggingEnable, trueText: "TRUE", trueNumber: 1);
                var loggingTime = ResolveLoggingSeconds(row.LoggingTime);
                var unit = ResolveValidationText(catalog, DbValidationDefaults.DbMeasUnit, row.Unit);
                var groupInputId = IsTrueValue(row.TrueText) ? 0 : 32000;
                var inputId1 = ToTiaInputInt(row.ModuleName);
                var inputId2 = row.ModulePort ?? 32000;
                builder.AppendLine($"{TiaName(row.PlcName)} {{ S7_SetPoint := 'False'}} : \"DT_MEASUREMENT_SOLO\" :=  ((), {BuildMeasStatusEnable(row)}, {loggingEnable}, {loggingTime}, 0, {row.Id}, {groupInputId}, {inputId1}, {inputId2}, {ToTiaParameterId(row.AlarmErrId)}, {ToTiaParameterId(row.AlarmLlId)}, {ToTiaParameterId(row.AlarmLoId)}, {ToTiaParameterId(row.AlarmHiId)}, {ToTiaParameterId(row.AlarmHhId)}, {ToTiaParameterId(row.ParVal1Id)}, {ToTiaParameterId(row.ParVal2Id)}, {ToTiaParameterId(row.ParOffsetId)}, {ToTiaParameterId(row.ParScaleFactorId)}, {ToTiaParameterId(row.ParLlId)}, {ToTiaParameterId(row.ParLoId)}, {ToTiaParameterId(row.ParHiId)}, {ToTiaParameterId(row.ParHhId)}, 0.0, '{EscapeTiaString(unit)}');");
            }

            AppendDbTypeFooter(builder);
        }

        AppendAllType(builder, GetConfigurationDataBlockName(catalog, "DB MEAS", "DT_MEASUREMENT_ALL"), groups);

        var path = Path.Combine(outputFolder, GetConfigurationFileName(catalog, "DB MEAS", "DB_MEAS.udt"));
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void WriteDbAoTypeConfiguration(DbCatalog catalog, GenerationResult result, string outputFolder)
    {
        var builder = new StringBuilder();
        var list = catalog.AoRows.OrderBy(row => row.AoNumber).ToList();
        var groups = BuildAoGroups(catalog.AoSystemNameRows, list, 32, catalog.AoStructTypePrefix);
        foreach (var group in groups)
        {
            AppendDbTypeHeader(builder, group.TypeName);
            foreach (var row in group.Rows)
            {
                var groupInputId = IsTrueValue(row.TrueText) ? 0 : 32000;
                var inputId1 = ToTiaInputInt(row.ModuleName);
                var inputId2 = row.ModulePort ?? 32000;
                var unit = ResolveValidationText(catalog, DbValidationDefaults.DbMeasUnit, row.Unit);
                var plcName = FirstNonEmpty(row.AoPlcName, $"AO_{row.AoNumber.ToString(CultureInfo.InvariantCulture)}");
                builder.AppendLine($"{TiaName(plcName)} {{ S7_SetPoint := 'False'}} : \"DT_ANA_CTRL_SOLO\" :=  ({row.AoNumber}, {groupInputId}, {inputId1}, {inputId2}, 0, {ToTiaParameterId(row.ParValLowId)}, {ToTiaParameterId(row.ParValHighId)}, {ToTiaParameterId(row.ParOffsetId)}, {ToTiaParameterId(row.ParScaleFactorId)}, 0.0, '{EscapeTiaString(unit)}', '{EscapeTiaString(row.NameEnglish)}');");
            }

            AppendDbTypeFooter(builder);
        }

        AppendAoAllType(builder, "DT_ANA_CTRL_ALL", groups);

        var path = Path.Combine(outputFolder, "DB_AO.udt");
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void WriteDbAlarmTypeConfiguration(DbCatalog catalog, GenerationResult result, string outputFolder)
    {
        var builder = new StringBuilder();
        var rows = catalog.AlarmRows.OrderBy(row => row.Id).ToList();
        var groups = BuildSystemGroups(catalog.AlarmSystemNameRows, rows, 32, catalog.AlarmStructTypePrefix, "ALM");
        foreach (var group in groups)
        {
            AppendDbTypeHeader(builder, group.TypeName);
            foreach (var row in group.Rows)
            {
                var used = ToTiaBooleanLiteral(row.Used, trueText: "TRUE", trueNumber: 1);
                var enable = ToTiaBooleanLiteral(row.AlarmControl, trueText: "Enable", trueNumber: 0);
                var srAlarm = ToTiaBooleanLiteral(row.SrAlarm, trueText: "Yes", trueNumber: 1);
                var alarmClass = ResolveValidationNumber(catalog, DbValidationDefaults.DbAlmAlarmClass, row.AlarmClass);
                var reactToUnit = ResolveValidationNumber(catalog, DbValidationDefaults.DbAlmReactToUnit, row.ReactToUnit);
                var spare = ResolveValidationNumber(catalog, DbValidationDefaults.DbAlmAlarmClass, row.Spare);
                var blocking1 = ResolveValidationNumber(catalog, DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass1);
                var blocking2 = ResolveValidationNumber(catalog, DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass2);
                var delay = FormatAlarmDelay(row.Delay, row.TimeBase);
                var traceType = ToLeadingInt(row.SourceType);
                var traceDigId = ToLeadingInt(row.SourceDigId);
                var traceMeasId = ToLeadingInt(row.SourceAnaId);
                builder.AppendLine($"{TiaName(row.PlcName)} {{ S7_SetPoint := 'False'}} : \"DT_ALM_SOLO\" :=  ({row.Id}, {used}, {enable}, {srAlarm}, FALSE, FALSE, {blocking1}, {blocking2}, {alarmClass}, {reactToUnit}, {spare}, {traceType}, {traceDigId}, {traceMeasId}, {delay});");
            }

            AppendDbTypeFooter(builder);
        }

        AppendAllType(builder, GetConfigurationDataBlockName(catalog, "DB ALM", "DT_ALM_ALL"), groups);

        var path = Path.Combine(outputFolder, GetConfigurationFileName(catalog, "DB ALM", "DB_ALM.udt"));
        File.WriteAllText(path, builder.ToString(), TiaFileEncoding);
        result.AddFile(path);
    }

    private static void WriteDbParameterTypeConfiguration(DbCatalog catalog, GenerationResult result, string outputFolder)
    {
        var builder = new StringBuilder();
        var rows = catalog.ParameterRows.OrderBy(row => row.Id).ToList();
        var groups = BuildSystemGroups(catalog.ParameterSystemNameRows, rows, 32, catalog.ParameterStructTypePrefix, "PAR");
        foreach (var group in groups)
        {
            AppendDbTypeHeader(builder, group.TypeName);
            foreach (var row in group.Rows)
            {
                var used = ToTiaBooleanLiteral(FirstNonEmpty(row.InUse, row.Used), trueText: "TRUE", trueNumber: 1);
                var parType = ResolveValidationNumber(catalog, DbValidationDefaults.DbParType, row.ParameterType);
                var rVal = ToNumericText(row.RealValue, "0.0");
                var iVal = ToNumericText(row.IntegerValue, "0");
                var tVal = FormatTiaTime(row.TimeValue);
                var lowLimit = ToNumericText(row.MinValue, "0.0");
                var highLimit = ToNumericText(row.MaxValue, "0.0");
                var unit = ResolveParameterUnit(catalog, row.Unit);
                builder.AppendLine($"{TiaName(row.PlcName)} {{ S7_SetPoint := 'False'}} : \"DT_PARAM_SOLO\" :=  ({used}, {row.Id}, {parType}, {rVal}, {iVal}, {tVal}, {lowLimit}, {highLimit}, '{EscapeTiaString(unit)}');");
            }

            AppendDbTypeFooter(builder);
        }

        AppendAllType(builder, GetConfigurationDataBlockName(catalog, "DB PAR", "DT_PARAM_ALL"), groups);

        var path = Path.Combine(outputFolder, GetConfigurationFileName(catalog, "DB PAR", "DB_PAR.udt"));
        File.WriteAllText(path, builder.ToString(), TiaFileEncoding);
        result.AddFile(path);
    }

    private static void WriteDbStringFiles(CwcProject project, GenerationSelection selection, GenerationResult result, string folder)
    {
        if (selection.DbDiStrings)
        {
            WriteStringBlock(folder, GetStringFileName(project.DbCatalog, "DB DI", "STRINGS_DI.db"), project.DbCatalog.DiRows, result,
                GetStringDefinitions(project.DbCatalog, "DB DI"));
        }

        if (selection.DbDoStrings)
        {
            WriteStringBlock(folder, GetStringFileName(project.DbCatalog, "DB DO", "STRINGS_DO.db"), project.DbCatalog.DoRows, result,
                GetStringDefinitions(project.DbCatalog, "DB DO"));
        }

        if (selection.DbMeasStrings)
        {
            WriteStringBlock(folder, GetStringFileName(project.DbCatalog, "DB MEAS", "STRINGS_MEAS.db"), project.DbCatalog.MeasRows, result,
                GetStringDefinitions(project.DbCatalog, "DB MEAS"));
        }

        if (selection.DbAlmStrings)
        {
            WriteAlarmStringType(project.DbCatalog, result, Path.GetDirectoryName(folder) is { } dtFolder ? Path.Combine(dtFolder, "DB") : folder);
            WriteStringBlock(folder, GetStringFileName(project.DbCatalog, "DB ALM", "STRINGS_ALM.db"), project.DbCatalog.AlarmRows, result,
                GetStringDefinitions(project.DbCatalog, "DB ALM"));
        }

        if (selection.DbParStrings)
        {
            WriteStringBlock(folder, GetStringFileName(project.DbCatalog, "DB PAR", "STRINGS_PAR.db"), project.DbCatalog.ParameterRows, result,
                GetStringDefinitions(project.DbCatalog, "DB PAR"));
        }
    }

    private static void WriteDbGroupNameFiles(CwcProject project, GenerationSelection selection, GenerationResult result, string folder)
    {
        if (selection.DbDiGroupNames)
        {
            WriteGroupNameBlocks(folder, project.DbCatalog, "DB DI", project.DbCatalog.DiRows, project.DbCatalog.DiSystemNameRows, project.DbCatalog.DiStructTypePrefix, result);
        }

        if (selection.DbDoGroupNames)
        {
            WriteGroupNameBlocks(folder, project.DbCatalog, "DB DO", project.DbCatalog.DoRows, project.DbCatalog.DoSystemNameRows, project.DbCatalog.DoStructTypePrefix, result);
        }

        if (selection.DbMeasGroupNames)
        {
            WriteGroupNameBlocks(folder, project.DbCatalog, "DB MEAS", project.DbCatalog.MeasRows, project.DbCatalog.MeasSystemNameRows, project.DbCatalog.MeasStructTypePrefix, result);
        }

        if (selection.DbAlmGroupNames)
        {
            WriteGroupNameBlocks(folder, project.DbCatalog, "DB ALM", project.DbCatalog.AlarmRows, project.DbCatalog.AlarmSystemNameRows, project.DbCatalog.AlarmStructTypePrefix, result);
        }

        if (selection.DbParGroupNames)
        {
            WriteGroupNameBlocks(folder, project.DbCatalog, "DB PAR", project.DbCatalog.ParameterRows, project.DbCatalog.ParameterSystemNameRows, project.DbCatalog.ParameterStructTypePrefix, result);
        }
    }

    private static string GetConfigurationDataBlockName(DbCatalog catalog, string dbName, string fallback)
    {
        var row = GetDataBlockNameRows(catalog, dbName, DbDataBlockNameDefaults.ConfigurationKind)
            .FirstOrDefault();

        return row?.DataTypeName.EmptyToNull()
            ?? row?.DataBlockName.EmptyToNull()
            ?? fallback;
    }

    private static string GetConfigurationFileName(DbCatalog catalog, string dbName, string fallback)
    {
        return GetDataBlockNameRows(catalog, dbName, DbDataBlockNameDefaults.ConfigurationKind)
            .FirstOrDefault()?.FileName
            .EmptyToNull()
            ?? fallback;
    }

    private static string GetStringFileName(DbCatalog catalog, string dbName, string fallback)
    {
        return GetDataBlockNameRows(catalog, dbName, DbDataBlockNameDefaults.StringKind)
            .FirstOrDefault()?.FileName
            .EmptyToNull()
            ?? fallback;
    }

    private static StringBlockDefinition[] GetStringDefinitions(DbCatalog catalog, string dbName)
    {
        return GetDataBlockNameRows(catalog, dbName, DbDataBlockNameDefaults.StringKind)
            .OrderBy(row => row.Number)
            .Select(row => new StringBlockDefinition(
                row.DataBlockName.EmptyToNull() ?? $"STRING_{row.Number}",
                row.DataTypeName.EmptyToNull() ?? "DT_STRING_NAMES",
                row.LastId > 0 ? row.LastId : DefaultLastId(dbName),
                CreateStringValueSelector(row.DataBlockName, row.SourceColumn)))
            .ToArray();
    }

    private static void WriteGroupNameBlocks(string folder, DbCatalog catalog, string dbName, IEnumerable<DbStringRow> sourceRows, IEnumerable<DbDiSystemNameRow> systemRows, string structPrefix, GenerationResult result)
    {
        var definitions = GetGroupNameDefinitions(catalog, dbName);
        var rows = BuildGroupNameRows(sourceRows, systemRows, structPrefix);

        foreach (var definition in definitions)
        {
            WriteGroupNameBlock(folder, definition, rows, result);
        }
    }

    private static List<DbDiSystemNameRow> BuildGroupNameRows(IEnumerable<DbStringRow> sourceRows, IEnumerable<DbDiSystemNameRow> systemRows, string structPrefix)
    {
        var savedByShortName = systemRows
            .Where(row => !string.IsNullOrWhiteSpace(row.SystemPlcShortName))
            .ToDictionary(row => row.SystemPlcShortName.Trim(), StringComparer.OrdinalIgnoreCase);

        return sourceRows
            .Where(row => !string.IsNullOrWhiteSpace(row.PlcStructure))
            .GroupBy(row => row.PlcStructure.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select((group, index) =>
            {
                var shortName = group.Key;
                var row = savedByShortName.TryGetValue(shortName, out var saved) ? saved.Clone() : new DbDiSystemNameRow();
                row.SystemNumber = row.SystemNumber <= 0 ? index + 1 : row.SystemNumber;
                row.SystemPlcShortName = shortName;
                row.PlcVariableName = FirstNonEmpty(row.PlcVariableName, shortName);
                row.SystemPlcLongName = FirstNonEmpty(row.SystemPlcLongName, JoinPrefixAndName(structPrefix, shortName));
                row.HmiSystemName = FirstNonEmpty(row.HmiSystemName, group.First().SystemNameEnglish, shortName);
                row.HmiSystemNameOther = FirstNonEmpty(row.HmiSystemNameOther, group.First().SystemNameOther, row.HmiSystemName);
                row.NameLength = row.HmiSystemName.Length;
                return row;
            })
            .OrderBy(row => row.SystemNumber)
            .ToList();
    }

    private static GroupNameBlockDefinition[] GetGroupNameDefinitions(DbCatalog catalog, string dbName)
    {
        return GetDataBlockNameRows(catalog, dbName, DbDataBlockNameDefaults.GroupNamesKind)
            .OrderBy(row => row.Number)
            .Select(row => new GroupNameBlockDefinition(
                row.FileName.EmptyToNull() ?? $"{dbName.Replace(" ", "_")}_GROUP_NAMES.db",
                row.DataBlockName.EmptyToNull() ?? $"{dbName.Replace(" ", "_")}_GROUP_NAMES",
                row.LastId))
            .ToArray();
    }

    private static List<DbDataBlockNameSetting> GetDataBlockNameRows(DbCatalog catalog, string dbName, string blockKind)
    {
        var source = catalog.DataBlockNames is { Count: > 0 }
            ? catalog.DataBlockNames
            : DbDataBlockNameDefaults.Create();

        var rows = source
            .Where(row =>
                row.DbName.Trim().Equals(dbName, StringComparison.OrdinalIgnoreCase)
                && row.BlockKind.Trim().Equals(blockKind, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return rows.Count > 0
            ? rows
            : DbDataBlockNameDefaults.Create()
                .Where(row =>
                    row.DbName.Trim().Equals(dbName, StringComparison.OrdinalIgnoreCase)
                    && row.BlockKind.Trim().Equals(blockKind, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private static int DefaultLastId(string dbName)
    {
        return dbName.ToUpperInvariant() switch
        {
            "DB DI" => 607,
            "DB DO" => 287,
            "DB MEAS" => 287,
            "DB ALM" => 991,
            "DB PAR" => 1125,
            _ => 0
        };
    }

    private static Func<DbStringRow, string> CreateStringValueSelector(string dataBlockName, string sourceColumn)
    {
        var blockName = (dataBlockName ?? "").Trim().ToUpperInvariant();
        if (blockName.EndsWith("_SYSTEM_STRING_NAMES_ENG", StringComparison.OrdinalIgnoreCase) ||
            blockName.EndsWith("_STRING_SYSTEMS_ENG_LANGUAGE", StringComparison.OrdinalIgnoreCase))
        {
            return row => row.SystemNameEnglish;
        }

        if (blockName.EndsWith("_SYSTEM_STRING_NAMES_GER", StringComparison.OrdinalIgnoreCase) ||
            blockName.EndsWith("_STRING_SYSTEMS_GER_LANGUAGE", StringComparison.OrdinalIgnoreCase))
        {
            return row => row.SystemNameOther;
        }

        var column = sourceColumn.Trim().ToUpperInvariant();
        return column switch
        {
            "STATUS SYSTEM ENG" or "ALARM SYSTEM ENG" or "SYSTEM ENG" => row => row.SystemNameEnglish,
            "STATUS NAME ENG" or "ALARM TEXT ENG" or "MEAS NAME ENG" or "PARAMETER NAME ENG" or "PARAMETAR NAME ENG" or "NAME ENG" => row => row.NameEnglish,
            "STATUS SYSTEM OTHER" or "ALARM SYSTEM OTHER" or "SYSTEM OTHER" => row => row.SystemNameOther,
            "STATUS NAME OTHER" or "ALARM TEXT OTHER" or "MEAS NAME OTHER" or "PARAMETER NAME OTHER" or "PARAMETAR NAME OTHER" or "NAME OTHER" => row => row.NameOther,
            "ALM CONTROL" or "ALARM CONTROL" => row => row.AlarmControl,
            _ => row => row.NameEnglish
        };
    }

    private static void WriteStringBlock(string folder, string fileName, IEnumerable<DbStringRow> rows, GenerationResult result, params StringBlockDefinition[] blocks)
    {
        var path = Path.Combine(folder, fileName);
        var builder = new StringBuilder();
        var list = rows.OrderBy(row => row.Id).ToList();

        foreach (var block in blocks)
        {
            AppendStringDataBlock(builder, block, list);
        }

        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void AppendStringDataBlock(StringBuilder builder, StringBlockDefinition block, IReadOnlyList<DbStringRow> rows)
    {
        var rowsById = rows
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());

        builder.AppendLine($"DATA_BLOCK \"{block.DataBlockName}\"");
        builder.AppendLine("{ S7_Optimized_Access := 'FALSE' }");
        builder.AppendLine("VERSION : 0.1");
        builder.AppendLine("NON_RETAIN");
        builder.AppendLine($"\"{block.DataTypeName}\"");
        builder.AppendLine();
        builder.AppendLine("BEGIN");
        for (var id = 0; id <= block.LastId; id++)
        {
            var value = rowsById.TryGetValue(id, out var row) ? block.ValueSelector(row) : "";
            builder.AppendLine(FormattableString.Invariant($"   NUM[{id}] := '{EscapeTiaString(value)}';"));
        }

        builder.AppendLine();
        builder.AppendLine("END_DATA_BLOCK");
    }

    private static void WriteGroupNameBlock(string folder, GroupNameBlockDefinition definition, IReadOnlyList<DbDiSystemNameRow> rows, GenerationResult result)
    {
        var path = Path.Combine(folder, definition.FileName);
        var lastIndex = definition.LastId > 0
            ? definition.LastId
            : rows.Count == 0 ? 0 : rows.Max(row => Math.Max(row.SystemNumber, 1));

        var rowsByIndex = rows
            .Where(row => row.SystemNumber > 0)
            .GroupBy(row => row.SystemNumber - 1)
            .ToDictionary(group => group.Key, group => group.First());

        var builder = new StringBuilder();
        builder.AppendLine($"DATA_BLOCK \"{definition.DataBlockName}\"");
        builder.AppendLine("{ S7_Optimized_Access := 'FALSE' }");
        builder.AppendLine("VERSION : 0.1");
        builder.AppendLine("NON_RETAIN");
        builder.AppendLine("   STRUCT ");
        builder.AppendLine($"      LANGUAGE : Array[0..{lastIndex}] of String[32];");
        builder.AppendLine($"      LANGUAGE_1 : Array[0..{lastIndex}] of String[32];");
        builder.AppendLine("   END_STRUCT;");
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("BEGIN");

        for (var index = 0; index <= lastIndex; index++)
        {
            var value = rowsByIndex.TryGetValue(index, out var row) ? row.HmiSystemName : "";
            builder.AppendLine(FormattableString.Invariant($"   LANGUAGE[{index}] := '{EscapeTiaString(value)}';"));
        }

        for (var index = 0; index <= lastIndex; index++)
        {
            var value = rowsByIndex.TryGetValue(index, out var row) ? row.HmiSystemNameOther : "";
            builder.AppendLine(FormattableString.Invariant($"   LANGUAGE_1[{index}] := '{EscapeTiaString(value)}';"));
        }

        builder.AppendLine();
        builder.AppendLine("END_DATA_BLOCK");
        builder.AppendLine();

        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static string BuildHwIdBlockSource(CwcProject project)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DATA_BLOCK \"HW_ID_s\"");
        builder.AppendLine("{ S7_Optimized_Access := 'FALSE' }");
        builder.AppendLine("VERSION : 0.1");
        builder.AppendLine("NON_RETAIN");
        builder.AppendLine("   STRUCT ");
        builder.AppendLine("      DI { S7_SetPoint := 'False'} : \"DT_DI_PLC\";");
        builder.AppendLine("      DO { S7_SetPoint := 'False'} : \"DT_DO_PLC\";");
        builder.AppendLine("      AI { S7_SetPoint := 'False'} : \"DT_AI_PLC\";");
        builder.AppendLine("      \"COUNTER\" { S7_SetPoint := 'False'} : \"DT_COUNTER_PLC\";");
        builder.AppendLine("      RTD { S7_SetPoint := 'False'} : \"DT_RTD_PLC\";");
        builder.AppendLine("      AO { S7_SetPoint := 'False'} : \"DT_AO_PLC\";");
        builder.AppendLine("   END_STRUCT;");
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("BEGIN");
        AppendDigitalHwAssignments(builder, "DI", project.DigitalInputs);
        AppendDigitalHwAssignments(builder, "DO", project.DigitalOutputs);
        AppendAnalogHwAssignments(builder, "AI", project.AnalogInputs, includeType: false);
        AppendCounterHwAssignments(builder, project.Counters);
        AppendAnalogHwAssignments(builder, "RTD", project.RtdInputs, includeType: true);
        AppendAnalogOutputHwAssignments(builder, project.AnalogOutputs);
        builder.AppendLine();
        builder.AppendLine("END_DATA_BLOCK");
        return builder.ToString();
    }

    private static void AppendDigitalHwAssignments(StringBuilder builder, string section, IEnumerable<DigitalSignalConfig> rows)
    {
        foreach (var row in rows.OrderBy(row => row.ModuleNumber).ThenBy(row => row.Port))
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.ID[{row.ModuleNumber},{row.Port}] := {row.Id};"));
        }

        foreach (var row in rows.OrderBy(row => row.ModuleNumber).ThenBy(row => row.Port))
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.CONFIG[{row.ModuleNumber},{row.Port}] := {(int)row.Config};"));
        }
    }

    private static void AppendAnalogHwAssignments(StringBuilder builder, string section, IEnumerable<AnalogSignalConfig> rows, bool includeType)
    {
        var orderedRows = rows.OrderBy(row => row.ModuleNumber).ThenBy(row => row.Port).ToList();
        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.ID[{row.ModuleNumber},{row.Port}] := {row.Id};"));
        }

        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.CONFIG[{row.ModuleNumber},{row.Port}] := {(int)row.Config};"));
        }

        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.CUSTOM_LIMITS_ENABLE[{row.ModuleNumber},{row.Port}] := {(int)row.CustomLimits};"));
        }

        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.CUSTOM_LOW_LIMIT[{row.ModuleNumber},{row.Port}] := {row.LowLimit};"));
        }

        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.CUSTOM_HIGH_LIMIT[{row.ModuleNumber},{row.Port}] := {row.HighLimit};"));
        }

        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   {section}.FILTER_TIME[{row.ModuleNumber},{row.Port}] := {FormatFilterTime(row.FilterTime)};"));
        }

        if (includeType)
        {
            foreach (var row in orderedRows)
            {
                builder.AppendLine(FormattableString.Invariant($"   {section}.TYPE[{row.ModuleNumber},{row.Port}] := 0;"));
            }
        }
    }

    private static void AppendCounterHwAssignments(StringBuilder builder, IEnumerable<CounterConfig> rows)
    {
        var orderedRows = rows.OrderBy(row => row.ModuleNumber).ToList();
        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   \"COUNTER\".ID[{row.ModuleNumber}] := {row.Id};"));
        }

        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   \"COUNTER\".CONFIG[{row.ModuleNumber}] := {(int)row.Config};"));
        }

        foreach (var row in orderedRows)
        {
            builder.AppendLine(FormattableString.Invariant($"   \"COUNTER\".FILTER_TIME[{row.ModuleNumber}] := {FormatFilterTime(row.FilterTime)};"));
        }
    }

    private static void AppendAnalogOutputHwAssignments(StringBuilder builder, IEnumerable<AnalogOutputConfig> rows)
    {
        foreach (var row in rows.OrderBy(row => row.ModuleNumber).ThenBy(row => row.Port))
        {
            builder.AppendLine(FormattableString.Invariant($"   AO.ID[{row.ModuleNumber},{row.Port}] := {row.Id};"));
        }

        foreach (var row in rows.OrderBy(row => row.ModuleNumber).ThenBy(row => row.Port))
        {
            builder.AppendLine(FormattableString.Invariant($"   AO.CONFIG[{row.ModuleNumber},{row.Port}] := {(int)row.Config};"));
        }
    }

    private static void WriteAlarmStringType(DbCatalog catalog, GenerationResult result, string dbFolder)
    {
        Directory.CreateDirectory(dbFolder);
        var rows = catalog.AlarmRows.OrderBy(row => row.Id).ToList();
        var builder = new StringBuilder();
        AppendStringType(builder, "DT_ALM_STRING_SYSTEM_NAMES", rows.Select(row => row.NameOther), 32);
        AppendStringType(builder, "DT_ALM_STRING_NAMES", rows.Select(row => row.AlarmControl), 64);

        var path = Path.Combine(dbFolder, "DB_ALM_STRING.udt");
        File.WriteAllText(path, builder.ToString(), TiaStringFileEncoding);
        result.AddFile(path);
    }

    private static void AppendStringType(StringBuilder builder, string typeName, IEnumerable<string> values, int length)
    {
        builder.AppendLine($"TYPE \"{typeName}\"");
        builder.AppendLine("VERSION : 0.1");
        builder.AppendLine("STRUCT");
        builder.Append("NUM : Array[0..991] of String[");
        builder.Append(length.ToString(CultureInfo.InvariantCulture));
        builder.Append("] := [ ");
        builder.Append(string.Join(", ", values.Take(992).Select(value => $"'{Truncate(value, length)}'")));
        builder.AppendLine("];");
        builder.AppendLine("END_STRUCT;");
        builder.AppendLine();
        builder.AppendLine("END_TYPE");
        builder.AppendLine();
    }

    private static string EscapeTiaString(string value)
    {
        return value.Replace("'", "''");
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static string FormatFilterTime(double value)
    {
        return value.ToString("0.0###", CultureInfo.InvariantCulture);
    }

    private static void AppendTypeHeader(StringBuilder builder, string typeName)
    {
        builder.AppendLine($"TYPE \"{typeName}\"");
        builder.AppendLine("VERSION : 0.1");
        builder.AppendLine("STRUCT");
    }

    private static void AppendFbTypeFooter(StringBuilder builder)
    {
        builder.AppendLine("END_STRUCT");
        builder.AppendLine();
        builder.AppendLine("END_TYPE");
    }

    private static void AppendDbTypeHeader(StringBuilder builder, string typeName)
    {
        builder.AppendLine($"TYPE \"{typeName}\"");
        builder.AppendLine("VERSION : 0.1");
        builder.AppendLine("STRUCT");
    }

    private static void AppendDbTypeFooter(StringBuilder builder)
    {
        builder.AppendLine("END_STRUCT;");
        builder.AppendLine();
        builder.AppendLine("END_TYPE");
        builder.AppendLine();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "ITEM";
    }

    private static string SanitizeTiaIdentifier(string value)
    {
        var sanitized = new string(value.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray()).Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "ITEM";
        }

        return char.IsDigit(sanitized[0]) ? $"ITEM_{sanitized}" : sanitized;
    }

    private static string TiaName(string value)
    {
        return value?.Trim() ?? "";
    }

    private static void AppendAllType(StringBuilder builder, string typeName, IReadOnlyList<DbStructGroup> groups)
    {
        AppendDbTypeHeader(builder, typeName);
        foreach (var group in groups)
        {
            builder.AppendLine($"{TiaName(group.VariableName)} : \"{group.TypeName}\";");
        }

        AppendDbTypeFooter(builder);
    }

    private static void AppendAoAllType(StringBuilder builder, string typeName, IReadOnlyList<DbAoStructGroup> groups)
    {
        AppendDbTypeHeader(builder, typeName);
        foreach (var group in groups)
        {
            builder.AppendLine($"{TiaName(group.VariableName)} : \"{group.TypeName}\";");
        }

        AppendDbTypeFooter(builder);
    }

    private static List<DbStructGroup> BuildSystemGroups(IEnumerable<DbDiSystemNameRow> systemRows, IReadOnlyList<DbStringRow> rows, int chunkSize, string typePrefix, string fallbackPrefix)
    {
        var systems = systemRows.OrderBy(row => row.SystemNumber).ToList();
        if (systems.Count == 0)
        {
            return BuildRowGroups(rows, chunkSize, typePrefix, fallbackPrefix);
        }

        var groups = new List<DbStructGroup>();
        for (var index = 0; index < systems.Count; index++)
        {
            var system = systems[index];
            var groupRows = rows.Skip(index * chunkSize).Take(chunkSize).ToList();
            if (groupRows.Count == 0)
            {
                continue;
            }

            groups.Add(new DbStructGroup(
                TypeName: FirstNonEmpty(system.SystemPlcLongName, JoinPrefixAndName(typePrefix, system.SystemPlcShortName), $"{typePrefix.TrimEnd('_')}_{index + 1}"),
                VariableName: FirstNonEmpty(system.PlcVariableName, system.SystemPlcShortName, $"{fallbackPrefix}_{index + 1}"),
                HmiName: system.HmiSystemName,
                Rows: groupRows));
        }

        return groups;
    }

    private static List<DbStructGroup> BuildRowGroups(IReadOnlyList<DbStringRow> rows, int chunkSize, string typePrefix, string fallbackPrefix)
    {
        var groups = new List<DbStructGroup>();
        var totalGroups = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)chunkSize));
        for (var index = 0; index < totalGroups; index++)
        {
            var groupRows = rows.Skip(index * chunkSize).Take(chunkSize).ToList();
            if (groupRows.Count == 0)
            {
                continue;
            }

            var first = groupRows[0];
            var memberName = FirstNonEmpty(first.PlcStructure, $"{fallbackPrefix}_{index + 1}");
            var typeName = JoinPrefixAndName(typePrefix, memberName);
            groups.Add(new DbStructGroup(
                TypeName: typeName,
                VariableName: memberName,
                HmiName: FirstNonEmpty(first.SystemNameEnglish, typeName),
                Rows: groupRows));
        }

        return groups;
    }

    private static List<DbAoStructGroup> BuildAoGroups(IEnumerable<DbDiSystemNameRow> systemRows, IReadOnlyList<DbAoRow> rows, int chunkSize, string typePrefix)
    {
        var systems = systemRows.OrderBy(row => row.SystemNumber).ToList();
        if (systems.Count > 0)
        {
            return systems
                .Select((system, index) => new DbAoStructGroup(
                    FirstNonEmpty(system.SystemPlcLongName, JoinPrefixAndName(typePrefix, system.SystemPlcShortName), $"{typePrefix.TrimEnd('_')}_{index + 1}"),
                    FirstNonEmpty(system.PlcVariableName, system.SystemPlcShortName, $"AO_{index + 1}"),
                    rows.Skip(index * chunkSize).Take(chunkSize).ToList()))
                .Where(group => group.Rows.Count > 0)
                .ToList();
        }

        var groups = new List<DbAoStructGroup>();
        var totalGroups = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)chunkSize));
        for (var index = 0; index < totalGroups; index++)
        {
            var groupRows = rows.Skip(index * chunkSize).Take(chunkSize).ToList();
            if (groupRows.Count == 0)
            {
                continue;
            }

            var memberName = index == 0
                ? "MECH"
                : FirstNonEmpty(groupRows[0].AoSystemName, $"AO_{index + 1}");
            groups.Add(new DbAoStructGroup(JoinPrefixAndName(typePrefix, memberName), memberName, groupRows));
        }

        return groups;
    }

    private static string BuildAllMemberName(string typeName, string fallbackPrefix, int index)
    {
        var name = SanitizeTiaIdentifier(typeName);
        foreach (var prefix in new[] { "DT_DIGITAL_INPUT_", "DT_DIGITAL_OUTPUT_", "DT_MEASUREMENT_", "DT_ALM_SYSTEM_", "DT_PARAM_", "DT_AO_SYSTEM_", "DT_AO_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(name) ? $"{fallbackPrefix}_{index}" : name;
    }

    private static string JoinPrefixAndName(string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return name.Trim().TrimStart('_');
        }

        return $"{prefix.Trim().TrimEnd('_')}_{name.Trim().TrimStart('_')}";
    }

    private static int ResolveColorValue(DbCatalog catalog, string value)
    {
        if (TryParseLeadingInt(value, out var direct))
        {
            return direct;
        }

        var match = catalog.DiColorSettings.FirstOrDefault(setting =>
            string.Equals(setting.Name, value, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match.Value;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "WHITE" => 1,
            "YELLOW" => 2,
            "RED" => 3,
            "GREEN" => 4,
            "BLUE" => 5,
            _ => 0
        };
    }

    private static string ToBooleanText(string value, string trueText, int trueNumber)
    {
        if (TryParseLeadingInt(value, out var number))
        {
            return number == trueNumber ? "True" : "False";
        }

        return string.Equals(DisplayOnlyText(value), trueText, StringComparison.OrdinalIgnoreCase)
            ? "True"
            : "False";
    }

    private static string ToTiaBooleanLiteral(string value, string trueText, int trueNumber)
    {
        return ToBooleanText(value, trueText, trueNumber).Equals("True", StringComparison.OrdinalIgnoreCase)
            ? "TRUE"
            : "FALSE";
    }

    private static int ToLeadingInt(string value)
    {
        return TryParseLeadingInt(value, out var number) ? number : 0;
    }

    private static int ToTiaInputInt(string value)
    {
        return TryParseLeadingInt(value, out var number) ? number : 32000;
    }

    private static int ToTiaParameterId(string value)
    {
        return TryParseLeadingInt(value, out var number) ? number : 0;
    }

    private static int BuildMeasStatusEnable(DbStringRow row)
    {
        var value = 0;
        if (row.LimitLlEnabled) value |= 1 << 4;
        if (row.LimitLoEnabled) value |= 1 << 5;
        if (row.LimitHiEnabled) value |= 1 << 6;
        if (row.LimitHhEnabled) value |= 1 << 7;
        return value;
    }

    private static bool IsTrueValue(string value)
    {
        if (TryParseLeadingInt(value, out var number))
        {
            return number != 0;
        }

        return value.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("YES", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("ENABLED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseLeadingInt(string value, out int number)
    {
        value = value.Trim();
        var digits = new string(value.TakeWhile(character => char.IsDigit(character) || character == '-').ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static int ResolveLoggingSeconds(string value)
    {
        if (TryParseLeadingInt(value, out var number) && !value.Contains(" - ", StringComparison.Ordinal))
        {
            return number;
        }

        return DisplayOnlyText(value).Trim().ToLowerInvariant() switch
        {
            "2 s" => 2,
            "5 s" => 5,
            "10 s" => 10,
            "30 s" => 30,
            "1 min" => 60,
            "5 min" => 300,
            _ => 0
        };
    }

    private static string ResolveValidationText(DbCatalog catalog, string key, string value)
    {
        var text = DisplayOnlyText(value).Trim();
        var list = catalog.ValidationLists.FirstOrDefault(setting => setting.Key == key);
        if (list is null)
        {
            return text;
        }

        var match = list.Options.FirstOrDefault(option =>
            string.Equals(option.Value, value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayText, value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Text, text, StringComparison.OrdinalIgnoreCase));

        return match?.Text ?? text;
    }

    private static int ResolveValidationNumber(DbCatalog catalog, string key, string value)
    {
        if (TryParseLeadingInt(value, out var direct) && value.Contains(" - ", StringComparison.Ordinal))
        {
            return direct;
        }

        var text = DisplayOnlyText(value).Trim();
        var list = catalog.ValidationLists.FirstOrDefault(setting => setting.Key == key);
        if (list is null)
        {
            return TryParseLeadingInt(value, out direct) ? direct : 0;
        }

        var match = list.Options.FirstOrDefault(option =>
            string.Equals(option.Value, value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayText, value.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Text, text, StringComparison.OrdinalIgnoreCase));

        return match is not null && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }

    private static string ResolveTimeBase(string value)
    {
        if (TryParseLeadingInt(value, out var number))
        {
            return number switch
            {
                1 => "S",
                2 => "M",
                3 => "H",
                _ => "MS"
            };
        }

        var text = DisplayOnlyText(value).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "MS";
        }

        return text switch
        {
            "MIN" => "M",
            "_" => "MS",
            "-MS" => "MS",
            "_MS" => "MS",
            _ => text
        };
    }

    private static string FormatAlarmDelay(string delayValue, string timeBaseValue)
    {
        var delay = ToNumericText(delayValue, "0")
            .Trim()
            .TrimEnd('_');
        if (delay.StartsWith("T#", StringComparison.OrdinalIgnoreCase))
        {
            return delay;
        }

        if (delay.Any(char.IsLetter))
        {
            return $"T#{delay}";
        }

        return $"T#{delay}{ResolveTimeBase(timeBaseValue)}";
    }

    private static string ToNumericText(string value, string fallback)
    {
        var text = DisplayOnlyText(value).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Replace(',', '.');
    }

    private static string FormatTiaTime(string value)
    {
        var text = ToNumericText(value, "T#0MS").Trim();
        return text.StartsWith("T#", StringComparison.OrdinalIgnoreCase) ? text : $"T#{text}";
    }

    private static string ResolveParameterUnit(DbCatalog catalog, string value)
    {
        var unit = ResolveValidationText(catalog, DbValidationDefaults.DbMeasUnit, value);
        return TryParseLeadingInt(value, out var number) && number == 0 && unit == "-"
            ? ""
            : unit;
    }

    private static string DisplayOnlyText(string value)
    {
        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 3)..] : value;
    }

    private sealed record DbStructGroup(string TypeName, string VariableName, string HmiName, IReadOnlyList<DbStringRow> Rows);

    private sealed record DbAoStructGroup(string TypeName, string VariableName, IReadOnlyList<DbAoRow> Rows);

    private sealed record StringBlockDefinition(string DataBlockName, string DataTypeName, int LastId, Func<DbStringRow, string> ValueSelector);

    private sealed record GroupNameBlockDefinition(string FileName, string DataBlockName, int LastId);

    private sealed record OutputFolders(string DbFolder, string StringFolder, string FbFolder);
}

public sealed class GenerationSelection
{
    public bool Summary { get; set; }
    public bool DigitalInputs { get; set; }
    public bool DigitalOutputs { get; set; }
    public bool AnalogInputs { get; set; }
    public bool RtdInputs { get; set; }
    public bool Counters { get; set; }
    public bool AnalogOutputs { get; set; }
    public bool DbDiConfiguration { get; set; }
    public bool DbDoConfiguration { get; set; }
    public bool DbMeasConfiguration { get; set; }
    public bool DbAoConfiguration { get; set; }
    public bool DbAlmConfiguration { get; set; }
    public bool DbParConfiguration { get; set; }
    public bool DbDiStrings { get; set; }
    public bool DbDoStrings { get; set; }
    public bool DbMeasStrings { get; set; }
    public bool DbAlmStrings { get; set; }
    public bool DbParStrings { get; set; }
    public bool DbDiGroupNames { get; set; }
    public bool DbDoGroupNames { get; set; }
    public bool DbMeasGroupNames { get; set; }
    public bool DbAlmGroupNames { get; set; }
    public bool DbParGroupNames { get; set; }

    public static GenerationSelection All()
    {
        return new GenerationSelection
        {
            Summary = true,
            DigitalInputs = true,
            DigitalOutputs = true,
            AnalogInputs = true,
            RtdInputs = true,
            Counters = true,
            AnalogOutputs = true,
            DbDiConfiguration = true,
            DbDoConfiguration = true,
            DbMeasConfiguration = true,
            DbAoConfiguration = true,
            DbAlmConfiguration = true,
            DbParConfiguration = true,
            DbDiStrings = true,
            DbDoStrings = true,
            DbMeasStrings = true,
            DbAlmStrings = true,
            DbParStrings = true,
            DbDiGroupNames = true,
            DbDoGroupNames = true,
            DbMeasGroupNames = true,
            DbAlmGroupNames = true,
            DbParGroupNames = true
        };
    }
}

file static class TiaSourceGeneratorStringExtensions
{
    public static string? EmptyToNull(this string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
