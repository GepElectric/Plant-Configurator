using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// Builds the simulator signal list of one PLC database: every hardware channel with its DB30 address,
/// names from DB DI / DB DO / DB MEAS / DB AO and the analog scaling from the DB PAR parameters.
/// </summary>
public static class SimulatorPlanBuilder
{
    public static List<SimulatorSignal> Build(CwcProject project, SimulatorLayout layout)
    {
        var catalog = project.DbCatalog;
        var diRows = ById(catalog.DiRows);
        var doRows = ById(catalog.DoRows);
        var measRows = ById(catalog.MeasRows);
        var parRows = ById(catalog.ParameterRows);
        var aoRows = catalog.AoRows.GroupBy(row => row.AoNumber).ToDictionary(group => group.Key, group => group.First());
        var signals = new List<SimulatorSignal>();
        var modules = project.ModuleCatalog;

        foreach (var row in project.DigitalInputs.Where(row => row.ModuleNumber < layout.DiModules && row.Port < layout.DiChannels))
        {
            var db = Find(diRows, row.Id, row.Config);
            var (offset, bit) = layout.DiBit(row.ModuleNumber, row.Port);
            signals.Add(new SimulatorSignal
            {
                Kind = SimulatorSignalKind.DI,
                Module = row.ModuleNumber,
                ModuleName = ModuleNames.Resolve(modules?.DigitalInputs, row.ModuleNumber),
                Channel = row.Port,
                Id = row.Id,
                Config = row.Config,
                System = FirstNonEmpty(db?.SystemNameEnglish, db?.PlcStructure, row.Structure),
                Name = FirstNonEmpty(db?.NameEnglish, row.SignalName),
                PlcName = FirstNonEmpty(db?.PlcName, row.StructName),
                ByteOffset = offset,
                BitOffset = bit
            });
        }

        foreach (var row in project.AnalogInputs.Where(row => row.ModuleNumber < layout.AiModules && row.Port < layout.AiChannels))
        {
            var meas = Find(measRows, row.Id, row.Config);
            signals.Add(new SimulatorSignal
            {
                Kind = SimulatorSignalKind.AI,
                Module = row.ModuleNumber,
                ModuleName = ModuleNames.Resolve(modules?.AnalogInputs, row.ModuleNumber),
                Channel = row.Port,
                Id = row.Id,
                Config = row.Config,
                System = FirstNonEmpty(meas?.SystemNameEnglish, meas?.PlcStructure, row.Structure),
                Name = FirstNonEmpty(meas?.NameEnglish, row.MeasurementName, row.SignalName),
                PlcName = meas?.PlcName ?? "",
                Unit = ResolveUnit(catalog, FirstNonEmpty(meas?.Unit, row.Unit)),
                ByteOffset = layout.AiWord(row.ModuleNumber, row.Port),
                Scaling = AnalogInputScaling(row, meas, parRows)
            });
        }

        foreach (var row in project.RtdInputs.Where(row => row.ModuleNumber < layout.RtdModules && row.Port < layout.RtdChannels))
        {
            var meas = Find(measRows, row.Id, row.Config);
            signals.Add(new SimulatorSignal
            {
                Kind = SimulatorSignalKind.RTD,
                Module = row.ModuleNumber,
                ModuleName = ModuleNames.Resolve(modules?.RtdInputs, row.ModuleNumber),
                Channel = row.Port,
                Id = row.Id,
                Config = row.Config,
                System = FirstNonEmpty(meas?.SystemNameEnglish, meas?.PlcStructure, row.Structure),
                Name = FirstNonEmpty(meas?.NameEnglish, row.MeasurementName, row.SignalName),
                PlcName = meas?.PlcName ?? "",
                Unit = ResolveUnit(catalog, FirstNonEmpty(meas?.Unit, row.Unit)),
                ByteOffset = layout.RtdWord(row.ModuleNumber, row.Port),
                Scaling = RtdScaling(meas, parRows)
            });
        }

        foreach (var row in project.Counters.Where(row => row.ModuleNumber < layout.CounterModules))
        {
            var meas = Find(measRows, row.Id, row.Config);
            signals.Add(new SimulatorSignal
            {
                Kind = SimulatorSignalKind.COUNTER,
                Module = row.ModuleNumber,
                ModuleName = ModuleNames.Resolve(modules?.Counters, row.ModuleNumber),
                Channel = 0,
                Id = row.Id,
                Config = row.Config,
                System = FirstNonEmpty(meas?.SystemNameEnglish, meas?.PlcStructure, row.Structure),
                Name = FirstNonEmpty(meas?.NameEnglish, row.MeasurementName, row.SignalName),
                PlcName = meas?.PlcName ?? "",
                Unit = ResolveUnit(catalog, FirstNonEmpty(meas?.Unit, row.Unit)),
                ByteOffset = layout.CounterReal(row.ModuleNumber)
            });
        }

        foreach (var row in project.DigitalOutputs.Where(row => row.ModuleNumber < layout.DoModules && row.Port < layout.DoChannels))
        {
            var db = Find(doRows, row.Id, row.Config);
            var (offset, bit) = layout.DoBit(row.ModuleNumber, row.Port);
            signals.Add(new SimulatorSignal
            {
                Kind = SimulatorSignalKind.DO,
                Module = row.ModuleNumber,
                ModuleName = ModuleNames.Resolve(modules?.DigitalOutputs, row.ModuleNumber),
                Channel = row.Port,
                Id = row.Id,
                Config = row.Config,
                System = FirstNonEmpty(db?.SystemNameEnglish, db?.PlcStructure, row.Structure),
                Name = FirstNonEmpty(db?.NameEnglish, row.SignalName),
                PlcName = FirstNonEmpty(db?.PlcName, row.StructName),
                ByteOffset = offset,
                BitOffset = bit
            });
        }

        foreach (var row in project.AnalogOutputs.Where(row => row.ModuleNumber < layout.AoModules && row.Port < layout.AoChannels))
        {
            aoRows.TryGetValue(row.Id, out var ao);
            if (row.Config == SignalConfigState.Disabled)
            {
                ao = null;
            }

            signals.Add(new SimulatorSignal
            {
                Kind = SimulatorSignalKind.AO,
                Module = row.ModuleNumber,
                ModuleName = ModuleNames.Resolve(modules?.AnalogOutputs, row.ModuleNumber),
                Channel = row.Port,
                Id = row.Id,
                Config = row.Config,
                System = FirstNonEmpty(ao?.AoSystemName, row.SystemName),
                Name = FirstNonEmpty(ao?.NameEnglish, row.SignalName),
                PlcName = ao?.AoPlcName ?? "",
                Unit = ResolveUnit(catalog, ao?.Unit),
                ByteOffset = layout.AoWord(row.ModuleNumber, row.Port),
                Scaling = AnalogOutputScaling(ao, parRows)
            });
        }

        return signals
            .OrderBy(signal => signal.Kind)
            .ThenBy(signal => signal.Module)
            .ThenBy(signal => signal.Channel)
            .ToList();
    }

    /// <summary>FB_AI: X1/X2 = 0/27648 or the custom HW limits; VAL_1/VAL_2, OFFSET and SCALE FACTOR from DB PAR.</summary>
    public static AnalogScaling? AnalogInputScaling(AnalogSignalConfig signal, DbStringRow? meas, IReadOnlyDictionary<int, DbStringRow> parameters)
    {
        if (meas is null)
        {
            return null;
        }

        var value1 = ParameterValue(parameters, meas.ParVal1Id);
        var value2 = ParameterValue(parameters, meas.ParVal2Id);
        if (value1 is null || value2 is null)
        {
            return null;
        }

        var custom = signal.CustomLimits == SignalConfigState.Enabled;
        return new AnalogScaling(
            custom ? signal.LowLimit : 0,
            custom ? signal.HighLimit : AnalogScaling.NominalRawHigh,
            value1.Value,
            value2.Value,
            ParameterValue(parameters, meas.ParOffsetId) ?? 0,
            ParameterValue(parameters, meas.ParScaleFactorId) ?? 1);
    }

    /// <summary>FB_RTD (temperature connection): the channel holds tenths of a degree; OFFSET and SCALE FACTOR from DB PAR.</summary>
    public static AnalogScaling RtdScaling(DbStringRow? meas, IReadOnlyDictionary<int, DbStringRow> parameters)
    {
        return new AnalogScaling(0, 0, 0, 0,
            meas is null ? 0 : ParameterValue(parameters, meas.ParOffsetId) ?? 0,
            meas is null ? 1 : ParameterValue(parameters, meas.ParScaleFactorId) ?? 1,
            TenthsOfValue: true);
    }

    /// <summary>FB_AO: LOW…HIGH (DB PAR, else the DB AO columns) maps to 0…27648.</summary>
    public static AnalogScaling? AnalogOutputScaling(DbAoRow? ao, IReadOnlyDictionary<int, DbStringRow> parameters)
    {
        if (ao is null)
        {
            return null;
        }

        var low = ParameterValue(parameters, ao.ParValLowId) ?? ao.Low;
        var high = ParameterValue(parameters, ao.ParValHighId) ?? ao.High;
        return new AnalogScaling(0, AnalogScaling.NominalRawHigh, low, high);
    }

    /// <summary>Unit text of a DB MEAS / DB AO unit code ("16" or "16 - %") from the unit validation list.</summary>
    public static string ResolveUnit(DbCatalog catalog, string? value)
    {
        var raw = (value ?? "").Trim();
        var separator = raw.IndexOf(" - ", StringComparison.Ordinal);
        var text = separator >= 0 ? raw[(separator + 3)..] : raw;
        var list = catalog.ValidationLists.FirstOrDefault(setting => setting.Key == DbValidationDefaults.DbMeasUnit);
        var match = list?.Options.FirstOrDefault(option =>
            string.Equals(option.Value, raw, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayText, raw, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Text, text, StringComparison.OrdinalIgnoreCase));
        return (match?.Text ?? text).Trim();
    }

    public static Dictionary<int, DbStringRow> ById(IEnumerable<DbStringRow> rows)
    {
        return rows.GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>R_VAL (else I_VAL) of the DB PAR row referenced by an id column such as "12" or "12 - GAIN".</summary>
    public static double? ParameterValue(IReadOnlyDictionary<int, DbStringRow> parameters, string? idText)
    {
        var digits = new string((idText ?? "").Trim().TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            || !parameters.TryGetValue(id, out var row))
        {
            return null;
        }

        return ParseNumber(row.RealValue) ?? ParseNumber(row.IntegerValue);
    }

    private static double? ParseNumber(string? text)
    {
        var value = (text ?? "").Trim().Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static DbStringRow? Find(IReadOnlyDictionary<int, DbStringRow> rows, int id, SignalConfigState config)
    {
        // Disabled channels keep a placeholder ID that belongs to some other signal; do not borrow its names.
        return config != SignalConfigState.Disabled && rows.TryGetValue(id, out var row) ? row : null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }
}
