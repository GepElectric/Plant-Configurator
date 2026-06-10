using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class ConfigurationCalculator
{
    public void Recalculate(CwcProject project)
    {
        RecalculateDigital(project.DigitalInputs, project.ModuleCatalog.DigitalInputs, project.DbCatalog.DiRows, disabledId: HardwareLimits.MaxDigitalInputId, includeInverted: true);
        RecalculateDigital(project.DigitalOutputs, project.ModuleCatalog.DigitalOutputs, project.DbCatalog.DoRows, disabledId: HardwareLimits.MaxDigitalOutputId, includeInverted: false);
        RecalculateAnalog(project.AnalogInputs, project.ModuleCatalog.AnalogInputs, project.DbCatalog.MeasRows);
        RecalculateAnalog(project.RtdInputs, project.ModuleCatalog.RtdInputs, project.DbCatalog.MeasRows);
        RecalculateCounters(project.Counters, project.ModuleCatalog.Counters, project.DbCatalog.MeasRows);
        RecalculateAnalogOutputs(project.AnalogOutputs, project.ModuleCatalog.AnalogOutputs, project.DbCatalog.AoRows);
    }

    private static void RecalculateDigital(
        IEnumerable<DigitalSignalConfig> rows,
        IReadOnlyList<ModuleNameRow> modules,
        IReadOnlyList<DbStringRow> dbRows,
        int disabledId,
        bool includeInverted)
    {
        foreach (var row in rows)
        {
            row.ModuleName = FindModuleName(modules, row.ModuleNumber) ?? row.ModuleName;
            row.CalcConfig = row.Config switch
            {
                SignalConfigState.Inverted when includeInverted => 2,
                SignalConfigState.Enabled => 1,
                _ => 0
            };
            row.CalcId = row.CalcConfig > 0 ? row.Id : disabledId;

            var dbRow = FindDbRow(dbRows, row.Id);
            row.Structure = dbRow?.PlcStructure ?? row.Structure;
            row.StatusNameDefinition = dbRow?.PlcName ?? row.StatusNameDefinition;
            row.StructName = dbRow?.SystemNameEnglish ?? row.StructName;
            row.SignalName = dbRow?.NameEnglish ?? row.SignalName;
        }
    }

    private static void RecalculateAnalog(
        IEnumerable<AnalogSignalConfig> rows,
        IReadOnlyList<ModuleNameRow> modules,
        IReadOnlyList<DbStringRow> dbRows)
    {
        foreach (var row in rows)
        {
            row.ModuleName = FindModuleName(modules, row.ModuleNumber) ?? row.ModuleName;
            row.CalcEnable = row.CustomLimits == SignalConfigState.Enabled ? 1 : 0;
            row.CalcConfig = row.Config == SignalConfigState.Enabled ? 1 : 0;
            row.CalcMeasurement = row.Config == SignalConfigState.Enabled ? row.Id : HardwareLimits.MaxAnalogInputId;

            var dbRow = FindDbRow(dbRows, row.Id);
            row.Structure = dbRow?.PlcStructure ?? row.Structure;
            row.MeasurementName = dbRow?.PlcName ?? row.MeasurementName;
            row.SignalName = dbRow?.NameEnglish ?? row.SignalName;
            row.Unit = dbRow?.Unit ?? row.Unit;
        }
    }

    private static void RecalculateCounters(
        IEnumerable<CounterConfig> rows,
        IReadOnlyList<ModuleNameRow> modules,
        IReadOnlyList<DbStringRow> dbRows)
    {
        foreach (var row in rows)
        {
            row.ModuleName = FindModuleName(modules, row.ModuleNumber) ?? row.ModuleName;
            row.CalcConfig = row.Config == SignalConfigState.Enabled ? 1 : 0;
            row.CalcMeasurement = row.Config == SignalConfigState.Enabled ? row.Id : HardwareLimits.MaxCounterId;

            var dbRow = FindDbRow(dbRows, row.Id);
            row.Structure = dbRow?.PlcStructure ?? row.Structure;
            row.MeasurementName = dbRow?.PlcName ?? row.MeasurementName;
            row.SignalName = dbRow?.NameEnglish ?? row.SignalName;
            row.Unit = dbRow?.Unit ?? row.Unit;
        }
    }

    private static void RecalculateAnalogOutputs(
        IEnumerable<AnalogOutputConfig> rows,
        IReadOnlyList<ModuleNameRow> modules,
        IReadOnlyList<DbAoRow> dbRows)
    {
        foreach (var row in rows)
        {
            row.ModuleName = FindModuleName(modules, row.ModuleNumber) ?? row.ModuleName;
            var dbRow = FindDbAoRow(dbRows, row.Id);
            row.SystemName = dbRow?.AoSystemName ?? row.SystemName;
            row.SignalName = dbRow?.NameEnglish ?? row.SignalName;
        }
    }

    private static string? FindModuleName(IReadOnlyList<ModuleNameRow> modules, int moduleNumber)
    {
        return modules.FirstOrDefault(row => row.ModuleNumber == moduleNumber)?.ModuleName;
    }

    private static DbStringRow? FindDbRow(IReadOnlyList<DbStringRow> rows, int id)
    {
        return id >= 0 && id < rows.Count
            ? rows[id]
            : rows.FirstOrDefault(row => row.Id == id);
    }

    private static DbAoRow? FindDbAoRow(IReadOnlyList<DbAoRow> rows, int id)
    {
        return rows.FirstOrDefault(row => row.AoNumber == id);
    }
}
