using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class ValidationStateUpdater
{
    public void Update(CwcProject project)
    {
        Clear(project);
        MarkAnalog(project.AnalogInputs, maxId: 287);
        MarkAnalog(project.RtdInputs, maxId: 287);
        MarkDigital(project.DigitalInputs, maxId: 607);
        MarkDigital(project.DigitalOutputs, maxId: 287);
        MarkCounters(project.Counters, maxId: 287);
        MarkAnalogOutputs(project.AnalogOutputs, maxId: 31);
        MarkDbStrings(project.DbCatalog);
    }

    private static void Clear(CwcProject project)
    {
        foreach (var row in project.DigitalInputs.Cast<IValidationState>()
                     .Concat(project.DigitalOutputs)
                     .Concat(project.AnalogInputs)
                     .Concat(project.RtdInputs)
                     .Concat(project.Counters)
                     .Concat(project.AnalogOutputs)
                     .Concat(project.DbCatalog.DiRows)
                     .Concat(project.DbCatalog.DoRows)
                     .Concat(project.DbCatalog.MeasRows)
                     .Concat(project.DbCatalog.AlarmRows)
                     .Concat(project.DbCatalog.ParameterRows))
        {
            row.HasValidationError = false;
        }
    }

    private static void MarkDigital(IEnumerable<DigitalSignalConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            row.HasValidationError = row.Id < 0 || row.Id > maxId;
        }
    }

    private static void MarkAnalog(IEnumerable<AnalogSignalConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            row.HasValidationError = row.Id < 0 || row.Id > maxId || row.LowLimit > row.HighLimit;
        }
    }

    private static void MarkCounters(IEnumerable<CounterConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            row.HasValidationError = row.Id < 0 || row.Id > maxId;
        }
    }

    private static void MarkAnalogOutputs(IEnumerable<AnalogOutputConfig> rows, int maxId)
    {
        foreach (var row in rows)
        {
            row.HasValidationError = row.Id < 0 || row.Id > maxId;
        }
    }

    private static void MarkDbStrings(DbCatalog catalog)
    {
        MarkStringRows(catalog.DiRows, catalog.DiTextLengths);
        MarkStringRows(catalog.DoRows, catalog.DoTextLengths);
        MarkStringRows(catalog.MeasRows, catalog.MeasTextLengths);
        MarkStringRows(catalog.AlarmRows, catalog.AlarmTextLengths);
        MarkStringRows(catalog.ParameterRows, catalog.ParameterTextLengths);
    }

    private static void MarkStringRows(IEnumerable<DbStringRow> rows, DbTextLengthSettings settings)
    {
        MarkStringRows(rows, settings.StatusSystemEnglishMax, settings.StatusNameEnglishMax, settings.StatusSystemOtherMax, settings.StatusNameOtherMax);
    }

    private static void MarkStringRows(IEnumerable<DbStringRow> rows, int systemEnglishMax, int nameEnglishMax, int systemOtherMax, int nameOtherMax)
    {
        foreach (var row in rows)
        {
            row.HasValidationError = row.SystemNameEnglish.Length > systemEnglishMax
                                     || row.NameEnglish.Length > nameEnglishMax
                                     || row.SystemNameOther.Length > systemOtherMax
                                     || row.NameOther.Length > nameOtherMax;
        }
    }
}
