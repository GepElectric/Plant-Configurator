using System.Text.RegularExpressions;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Validation;

namespace CwcConfigurator.Services;

public sealed class ProjectValidator
{
    public IEnumerable<ProjectIssue> Validate(CwcProject project)
    {
        return Validate(project, ValidationOptions.Default);
    }

    public IEnumerable<ProjectIssue> Validate(CwcProject project, ValidationOptions options)
    {
        foreach (var issue in ValidateDigital("DI", project.DigitalInputs, maxId: HardwareLimits.MaxDigitalInputId))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDigital("DO", project.DigitalOutputs, maxId: HardwareLimits.MaxDigitalOutputId))
        {
            yield return issue;
        }

        foreach (var issue in ValidateAnalog("AI", project.AnalogInputs, maxId: HardwareLimits.MaxAnalogInputId))
        {
            yield return issue;
        }

        foreach (var issue in ValidateAnalog("RTD", project.RtdInputs, maxId: HardwareLimits.MaxRtdInputId))
        {
            yield return issue;
        }

        foreach (var issue in ValidateCounters(project.Counters, maxId: HardwareLimits.MaxCounterId))
        {
            yield return issue;
        }

        foreach (var issue in ValidateAnalogOutputs(project.AnalogOutputs, maxId: HardwareLimits.MaxAnalogOutputId))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicateIds(project))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDbStrings(project.DbCatalog))
        {
            yield return issue;
        }

        foreach (var issue in ValidateRepairs(project.DbCatalog, options))
        {
            yield return issue;
        }
    }

    private static IEnumerable<ProjectIssue> ValidateRepairs(DbCatalog catalog, ValidationOptions options)
    {
        foreach (var row in catalog.DiRows)
        {
            foreach (var issue in EmitFindings(DiRepairEvaluator.TableName, row.Id, DiRepairEvaluator.Evaluate(row)))
            {
                yield return issue;
            }
        }

        foreach (var row in catalog.DoRows)
        {
            foreach (var issue in EmitFindings(DoRepairEvaluator.TableName, row.Id, DoRepairEvaluator.Evaluate(row, options)))
            {
                yield return issue;
            }
        }

        foreach (var row in catalog.MeasRows)
        {
            foreach (var issue in EmitFindings(MeasRepairEvaluator.TableName, row.Id, MeasRepairEvaluator.Evaluate(row)))
            {
                yield return issue;
            }
        }

        foreach (var row in catalog.AoRows)
        {
            foreach (var issue in EmitFindings(AoRepairEvaluator.TableName, row.AoNumber, AoRepairEvaluator.Evaluate(row, options)))
            {
                yield return issue;
            }
        }

        foreach (var row in catalog.AlarmRows)
        {
            foreach (var issue in EmitFindings(AlmRepairEvaluator.TableName, row.Id, AlmRepairEvaluator.Evaluate(row, options)))
            {
                yield return issue;
            }
        }

        foreach (var row in catalog.ParameterRows)
        {
            foreach (var issue in EmitFindings(ParRepairEvaluator.TableName, row.Id, ParRepairEvaluator.Evaluate(row)))
            {
                yield return issue;
            }
        }
    }

    private static IEnumerable<string> SplitFieldNames(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            yield break;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
        }
    }

    private static IEnumerable<ProjectIssue> EmitFindings(string table, int id, IEnumerable<RepairFinding> findings)
    {
        // Group by (severity, slicer hint) so problems that should land on
        // different slicer tabs become separate Problems-pane rows. Findings
        // within the same group are still joined into one message.
        var groups = findings
            .GroupBy(f => (f.Severity, Hint: f.SlicerHint ?? ""))
            .ToList();

        foreach (var group in groups)
        {
            var reasons = string.Join(", ", group.Select(f => f.Reason).Distinct(StringComparer.Ordinal));
            var hint = string.IsNullOrEmpty(group.Key.Hint) ? null : group.Key.Hint;

            // A single RepairFinding can map to several cells by passing a
            // comma-separated FieldName (e.g. "ColorValue0,ColorValue1" for a
            // "Color 0 == Color 1" check). Split + distinct so the resulting
            // ProjectIssue carries each property exactly once.
            var fieldNames = group
                .SelectMany(f => SplitFieldNames(f.FieldName))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            IReadOnlyList<string>? fields = fieldNames.Count > 0 ? fieldNames : null;

            if (group.Key.Severity == IssueSeverity.Error)
            {
                yield return new ProjectIssue("ERROR", table, ModuleNumber: 0, Port: null, reasons, Id: id, SlicerHint: hint, FieldNames: fields);
            }
            else
            {
                yield return new ProjectIssue("WARNING", table, ModuleNumber: 0, Port: null, $"Needs repair: {reasons}", Id: id, SlicerHint: hint, FieldNames: fields);
            }
        }
    }

    private static IEnumerable<ProjectIssue> ValidateDuplicateIds(CwcProject project)
    {
        foreach (var issue in ValidateDuplicates("DI", project.DigitalInputs.Where(row => row.Config != SignalConfigState.Disabled).Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("DO", project.DigitalOutputs.Where(row => row.Config != SignalConfigState.Disabled).Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("AI", project.AnalogInputs.Where(row => row.Config != SignalConfigState.Disabled).Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("RTD", project.RtdInputs.Where(row => row.Config != SignalConfigState.Disabled).Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("COUNTER", project.Counters.Where(row => row.Config != SignalConfigState.Disabled).Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("AO", project.AnalogOutputs.Where(row => row.Config != SignalConfigState.Disabled).Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("DB_DI", project.DbCatalog.DiRows.Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("DB_DO", project.DbCatalog.DoRows.Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("DB_MEAS", project.DbCatalog.MeasRows.Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("DB_AO", project.DbCatalog.AoRows.Select(row => row.AoNumber)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("DB_ALM", project.DbCatalog.AlarmRows.Select(row => row.Id)))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDuplicates("DB_PAR", project.DbCatalog.ParameterRows.Select(row => row.Id)))
        {
            yield return issue;
        }
    }

    private static IEnumerable<ProjectIssue> ValidateDuplicates(string tableName, IEnumerable<int> ids)
    {
        foreach (var duplicate in ids.GroupBy(id => id).Where(group => group.Count() > 1))
        {
            yield return ProjectIssue.DuplicateId(tableName, duplicate.Key, $"ID {duplicate.Key} appears {duplicate.Count()} times.");
        }
    }

    private static IEnumerable<ProjectIssue> ValidateDigital(string tableName, IEnumerable<DigitalSignalConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            if (row.Id < 0 || row.Id > maxId)
            {
                yield return ProjectIssue.Error(tableName, row.ModuleNumber, row.Port, $"ID {row.Id} is outside allowed range 0-{maxId}.");
            }
        }
    }

    private static IEnumerable<ProjectIssue> ValidateAnalog(string tableName, IEnumerable<AnalogSignalConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            if (row.Id < 0 || row.Id > maxId)
            {
                yield return ProjectIssue.Error(tableName, row.ModuleNumber, row.Port, $"ID {row.Id} is outside allowed range 0-{maxId}.");
            }

            // Note: row.LowLimit > row.HighLimit is intentionally NOT an error.
            // Reverse-scaled signals (e.g. inverted 4-20 mA calibration on valves) are legitimate.
        }
    }

    private static IEnumerable<ProjectIssue> ValidateCounters(IEnumerable<CounterConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            if (row.Id < 0 || row.Id > maxId)
            {
                yield return ProjectIssue.Error("COUNTER", row.ModuleNumber, null, $"ID {row.Id} is outside allowed range 0-{maxId}.");
            }
        }
    }

    private static IEnumerable<ProjectIssue> ValidateAnalogOutputs(IEnumerable<AnalogOutputConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            if (row.Id < 0 || row.Id > maxId)
            {
                yield return ProjectIssue.Error("AO", row.ModuleNumber, row.Port, $"ID {row.Id} is outside allowed range 0-{maxId}.");
            }
        }
    }

    private static IEnumerable<ProjectIssue> ValidateDbStrings(DbCatalog catalog)
    {
        foreach (var issue in ValidateStringRows("DB_DI", catalog.DiRows, catalog.DiTextLengths))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_DI", catalog.DiRows, nameof(DbStringRow.ColorValue0), "COLOR VAL 0", DbValidationDefaults.DbDiColorValues, row => row.ColorValue0))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_DI", catalog.DiRows, nameof(DbStringRow.ColorValue1), "COLOR VAL 1", DbValidationDefaults.DbDiColorValues, row => row.ColorValue1))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_DI", catalog.DiRows, nameof(DbStringRow.AlarmConfig), "ALM CONFIG", DbValidationDefaults.DbDiAlarmBoolean, row => row.AlarmConfig))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_DI", catalog.DiRows, nameof(DbStringRow.AlarmInvert), "ALM INVERT", DbValidationDefaults.DbDiAlarmBoolean, row => row.AlarmInvert))
        {
            yield return issue;
        }

        foreach (var issue in ValidateStringRows("DB_DO", catalog.DoRows, catalog.DoTextLengths))
        {
            yield return issue;
        }

        foreach (var issue in ValidateStringRows("DB_MEAS", catalog.MeasRows, catalog.MeasTextLengths))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_MEAS", catalog.MeasRows, nameof(DbStringRow.Unit), "UNIT", DbValidationDefaults.DbMeasUnit, row => row.Unit))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_MEAS", catalog.MeasRows, nameof(DbStringRow.LoggingEnable), "Logging enable", DbValidationDefaults.DbMeasLoggingEnable, row => row.LoggingEnable))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_MEAS", catalog.MeasRows, nameof(DbStringRow.LoggingTime), "Logging time", DbValidationDefaults.DbMeasLoggingTime, row => row.LoggingTime, allowRawNumeric: true))
        {
            yield return issue;
        }

        foreach (var issue in ValidateAoDataValidationValue(catalog, "DB_AO", catalog.AoRows, "UNIT", DbValidationDefaults.DbMeasUnit, row => row.Unit))
        {
            yield return issue;
        }

        foreach (var issue in ValidateStringRows("DB_ALM", catalog.AlarmRows, catalog.AlarmTextLengths))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.Used), "USED", DbValidationDefaults.DbParInUse, row => row.Used))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.AlarmControl), "ALM CONTROL", DbValidationDefaults.DbAlmEnableDisable, row => row.AlarmControl))
        {
            yield return issue;
        }

        foreach (var row in catalog.AlarmRows)
        {
            foreach (var issue in ValidateSingleDataValidationValue(catalog, "DB_ALM", row.Id, "SR ALARM", row.Id is 710 or >= 742 ? DbValidationDefaults.DbAlmEnableDisable : DbValidationDefaults.DbAlmYesNo, row.SrAlarm))
            {
                yield return issue;
            }
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.AlarmClass), "ALARM CLASS", DbValidationDefaults.DbAlmAlarmClass, row => row.AlarmClass))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.ReactToUnit), "REACT TO UNIT", DbValidationDefaults.DbAlmReactToUnit, row => row.ReactToUnit))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.Spare), "SPARE", DbValidationDefaults.DbAlmAlarmClass, row => row.Spare))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.BlockingClass1), "BLOCKING CLASS 1", DbValidationDefaults.DbAlmBlockingClass, row => row.BlockingClass1))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.BlockingClass2), "BLOCKING CLASS 2", DbValidationDefaults.DbAlmBlockingClass, row => row.BlockingClass2))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_ALM", catalog.AlarmRows, nameof(DbStringRow.TimeBase), "TIME base", DbValidationDefaults.DbAlmTimeBase, row => row.TimeBase))
        {
            yield return issue;
        }

        foreach (var issue in ValidatePlcIdentifiers("DB_ALM", catalog.AlarmRows, catalog.AlarmSystemNameRows))
        {
            yield return issue;
        }

        foreach (var issue in ValidateStringRows("DB_PAR", catalog.ParameterRows, catalog.ParameterTextLengths))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_PAR", catalog.ParameterRows, nameof(DbStringRow.InUse), "IN USE", DbValidationDefaults.DbParInUse, row => row.InUse))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_PAR", catalog.ParameterRows, nameof(DbStringRow.ParameterType), "PAR TYPE", DbValidationDefaults.DbParType, row => row.ParameterType))
        {
            yield return issue;
        }

        foreach (var issue in ValidateDataValidationValue(catalog, "DB_PAR", catalog.ParameterRows, nameof(DbStringRow.Unit), "UNIT", DbValidationDefaults.DbMeasUnit, row => row.Unit))
        {
            yield return issue;
        }
    }

    private static IEnumerable<ProjectIssue> ValidateDataValidationValue(
        DbCatalog catalog,
        string tableName,
        IEnumerable<DbStringRow> rows,
        string fieldName,
        string displayName,
        string validationKey,
        Func<DbStringRow, string> valueSelector,
        bool allowRawNumeric = false)
    {
        foreach (var row in rows)
        {
            foreach (var issue in ValidateSingleDataValidationValue(catalog, tableName, row.Id, displayName, validationKey, valueSelector(row), allowRawNumeric))
            {
                yield return issue;
            }
        }
    }

    private static IEnumerable<ProjectIssue> ValidateAoDataValidationValue(
        DbCatalog catalog,
        string tableName,
        IEnumerable<DbAoRow> rows,
        string displayName,
        string validationKey,
        Func<DbAoRow, string> valueSelector)
    {
        foreach (var row in rows)
        {
            foreach (var issue in ValidateSingleDataValidationValue(catalog, tableName, row.AoNumber, displayName, validationKey, valueSelector(row)))
            {
                yield return issue;
            }
        }
    }

    private static IEnumerable<ProjectIssue> ValidateSingleDataValidationValue(
        DbCatalog catalog,
        string tableName,
        int id,
        string displayName,
        string validationKey,
        string value,
        bool allowRawNumeric = false)
    {
        var options = GetValidationOptions(catalog, validationKey);
        if (options.Count == 0)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            yield return ProjectIssue.Error(tableName, id, null, $"{displayName}: no value selected from data validation list.");
            yield break;
        }

        if (allowRawNumeric && double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            yield break;
        }

        if (!options.Any(option =>
                option.Value.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)
                || option.DisplayText.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)
                || option.Text.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            yield return ProjectIssue.Error(tableName, id, null, $"{displayName}: value '{value}' is not in data validation list.");
        }
    }

    private static IReadOnlyList<DbValidationOption> GetValidationOptions(DbCatalog catalog, string key)
    {
        var list = catalog.ValidationLists.FirstOrDefault(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                   ?? DbValidationDefaults.Create().FirstOrDefault(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        return list?.Options ?? [];
    }

    private static IEnumerable<ProjectIssue> ValidateStringRows(
        string tableName,
        IEnumerable<DbStringRow> rows,
        DbTextLengthSettings settings)
    {
        return ValidateStringRows(tableName, rows, settings.StatusSystemEnglishMax, settings.StatusNameEnglishMax, settings.StatusSystemOtherMax, settings.StatusNameOtherMax);
    }

    private static IEnumerable<ProjectIssue> ValidateStringRows(
        string tableName,
        IEnumerable<DbStringRow> rows,
        int systemEnglishMax,
        int nameEnglishMax,
        int systemOtherMax,
        int nameOtherMax)
    {
        foreach (var row in rows)
        {
            foreach (var issue in ValidateTextLength(tableName, row.Id, "SystemNameEnglish", row.SystemNameEnglish, systemEnglishMax))
            {
                yield return issue;
            }

            foreach (var issue in ValidateTextLength(tableName, row.Id, "NameEnglish", row.NameEnglish, nameEnglishMax))
            {
                yield return issue;
            }

            foreach (var issue in ValidateTextLength(tableName, row.Id, "SystemNameOther", row.SystemNameOther, systemOtherMax))
            {
                yield return issue;
            }

            foreach (var issue in ValidateTextLength(tableName, row.Id, "NameOther", row.NameOther, nameOtherMax))
            {
                yield return issue;
            }
        }
    }

    /// <summary>
    /// TIA rejects the whole generated type source when a structure member or structure name does not start
    /// with a letter or underscore (e.g. "6_3_MV_CONTROL_MCB_FAULT"), so such names block generation and push.
    /// </summary>
    private static IEnumerable<ProjectIssue> ValidatePlcIdentifiers(string tableName, IEnumerable<DbStringRow> rows, IEnumerable<DbDiSystemNameRow> systemRows)
    {
        foreach (var row in rows)
        {
            if (!IsValidPlcIdentifier(row.PlcName))
            {
                yield return ProjectIssue.Error(tableName, row.Id, null, $"PLC NAME '{row.PlcName}' is not a valid TIA name. It must start with a letter or underscore and contain only letters, digits and underscores.");
            }
        }

        foreach (var system in systemRows)
        {
            foreach (var (fieldName, value) in new[] { ("SYSTEM PLC SHORT NAME", system.SystemPlcShortName), ("PLC VARIABLE NAME", system.PlcVariableName) })
            {
                if (!IsValidPlcIdentifier(value))
                {
                    yield return ProjectIssue.Error(tableName, system.SystemNumber, null, $"System {system.SystemNumber}: {fieldName} '{value}' is not a valid TIA name. It must start with a letter or underscore and contain only letters, digits and underscores.");
                }
            }
        }
    }

    public static bool IsValidPlcIdentifier(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 || PlcIdentifierRegex.IsMatch(text);
    }

    private static readonly Regex PlcIdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static IEnumerable<ProjectIssue> ValidateTextLength(string tableName, int id, string fieldName, string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            yield break;
        }

        yield return ProjectIssue.Error(tableName, id, null, $"{fieldName} has {value.Length} characters. Maximum is {maxLength}. Text: {value}");
    }
}

public enum IssueSeverity
{
    Error,
    Warning
}

public sealed record ProjectIssue(string Severity, string Table, int ModuleNumber, int? Port, string Message, int? Id = null, string? SlicerHint = null, IReadOnlyList<string>? FieldNames = null)
{
    public IssueSeverity SeverityKind => Severity == "WARNING" ? IssueSeverity.Warning : IssueSeverity.Error;

    public static ProjectIssue Error(string table, int moduleNumber, int? port, string message)
    {
        return new ProjectIssue("ERROR", table, moduleNumber, port, message);
    }

    public static ProjectIssue Warning(string table, int id, string message)
    {
        return new ProjectIssue("WARNING", table, ModuleNumber: 0, Port: null, message, Id: id);
    }

    /// <summary>
    /// Error tied to a specific signal/DB Id rather than to a module/port location.
    /// Used e.g. for duplicate-Id reports, where the natural locator is the Id itself.
    /// </summary>
    public static ProjectIssue DuplicateId(string table, int id, string message)
    {
        return new ProjectIssue("ERROR", table, ModuleNumber: 0, Port: null, message, Id: id);
    }

    public override string ToString()
    {
        if (Id is not null)
        {
            return $"{Severity}: {Table}, ID {Id}: {Message}";
        }

        if (Table.StartsWith("DB_", StringComparison.OrdinalIgnoreCase))
        {
            return $"{Severity}: {Table}, ID {ModuleNumber}: {Message}";
        }

        if (Table == "PROJECT")
        {
            return $"{Severity}: {Message}";
        }

        var portText = Port is null ? "" : $", port {Port}";
        return $"{Severity}: {Table}, module {ModuleNumber}{portText}: {Message}";
    }
}
