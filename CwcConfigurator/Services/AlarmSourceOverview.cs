using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>Category of an alarm by its SOURCE TYPE (TRACE_TYPE pulled from the PLC); analog inputs split into AI and RTD.</summary>
public enum AlarmSourceCategory
{
    Program,
    DigitalInput,
    AnalogInput,
    Rtd
}

/// <summary>A used DB ALM alarm in one source category, with the module and port of its source input (if any).</summary>
public sealed record AlarmSourceLink(
    AlarmSourceCategory Category,
    string Module,
    int? Port,
    int? SourceId,
    DbStringRow? Source,
    DbStringRow Alarm)
{
    /// <summary>Module as a number for ordering (unknown modules last).</summary>
    public int ModuleOrder => int.TryParse(Module, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue;
}

/// <summary>
/// Used DB ALM alarms grouped by source of alarm (SOURCE TYPE: 0 program, 1 digital input, 2 analog input, 3 both).
/// Digital, AI and RTD alarms are ordered by module (0, 1, …), port and alarm ID; program alarms by ID. An alarm with
/// source "both" is listed under its digital input and under its analog input. Module and port come from the HW
/// configuration of the source input (same ID, not disabled), otherwise from the DB DI / DB MEAS module assignment.
/// </summary>
public static class AlarmSourceOverview
{
    public static List<AlarmSourceLink> Build(
        IEnumerable<DbStringRow> alarmRows,
        IEnumerable<DbStringRow> diRows,
        IEnumerable<DbStringRow> measRows,
        IEnumerable<DigitalSignalConfig> hwDigitalInputs,
        IEnumerable<AnalogSignalConfig> hwAnalogInputs,
        IEnumerable<AnalogSignalConfig> hwRtdInputs)
    {
        var digital = ById(hwDigitalInputs.Where(input => input.Config != SignalConfigState.Disabled), input => input.Id);
        var analog = ById(hwAnalogInputs.Where(input => input.Config != SignalConfigState.Disabled), input => input.Id);
        var rtd = ById(hwRtdInputs.Where(input => input.Config != SignalConfigState.Disabled), input => input.Id);
        var inputs = ById(diRows, row => row.Id);
        var measurements = ById(measRows, row => row.Id);
        var links = new List<AlarmSourceLink>();
        foreach (var alarm in alarmRows.Where(row => IsUsed(row.Used)))
        {
            // With SOURCE TYPE the source IDs may be 0 (DI 0 / MEAS 0 are real inputs); without it (not pulled yet)
            // the type follows from the source IDs, where 0 means "none".
            var type = SourceType(alarm.SourceType);
            var digitalId = type is null ? PositiveInt(alarm.SourceDigId) : NonNegativeInt(alarm.SourceDigId);
            var analogId = type is null ? PositiveInt(alarm.SourceAnaId) : NonNegativeInt(alarm.SourceAnaId);
            type ??= (digitalId is not null ? 1 : 0) + (analogId is not null ? 2 : 0);
            if (type is 1 or 3)
            {
                var input = digitalId is null ? null : inputs.GetValueOrDefault(digitalId.Value);
                var (module, port) = digitalId is not null && digital.TryGetValue(digitalId.Value, out var hw)
                    ? (hw.ModuleNumber.ToString(CultureInfo.InvariantCulture), (int?)hw.Port)
                    : Assigned(input);
                links.Add(new AlarmSourceLink(AlarmSourceCategory.DigitalInput, module, port, digitalId, input, alarm));
            }

            if (type is 2 or 3)
            {
                var measurement = analogId is null ? null : measurements.GetValueOrDefault(analogId.Value);
                if (analogId is not null && rtd.TryGetValue(analogId.Value, out var rtdInput) && !analog.ContainsKey(analogId.Value))
                {
                    links.Add(new AlarmSourceLink(AlarmSourceCategory.Rtd, rtdInput.ModuleNumber.ToString(CultureInfo.InvariantCulture), rtdInput.Port, analogId, measurement, alarm));
                }
                else
                {
                    var (module, port) = analogId is not null && analog.TryGetValue(analogId.Value, out var aiInput)
                        ? (aiInput.ModuleNumber.ToString(CultureInfo.InvariantCulture), (int?)aiInput.Port)
                        : Assigned(measurement);
                    links.Add(new AlarmSourceLink(AlarmSourceCategory.AnalogInput, module, port, analogId, measurement, alarm));
                }
            }

            if (type == 0)
            {
                links.Add(new AlarmSourceLink(AlarmSourceCategory.Program, "", null, null, null, alarm));
            }
        }

        return links
            .OrderBy(link => link.Category)
            .ThenBy(link => link.Category == AlarmSourceCategory.Program ? 0 : link.ModuleOrder)
            .ThenBy(link => link.Category == AlarmSourceCategory.Program ? 0 : link.Port ?? int.MaxValue)
            .ThenBy(link => link.Alarm.Id)
            .ToList();
    }

    // Module / port assigned in DB DI or DB MEAS (MODULE NAME, MODULE PORT) when the input has no HW configuration.
    // "no HW" when the input has neither a HW configuration nor a module assignment.
    private static (string Module, int? Port) Assigned(DbStringRow? row)
    {
        var module = row?.ModuleName.Trim() ?? "";
        return module.Length == 0 ? (NoHardware, null) : (module, row!.ModulePort);
    }

    public const string NoHardware = "no HW";

    private static Dictionary<int, T> ById<T>(IEnumerable<T> items, Func<T, int> id)
    {
        return items.GroupBy(id).ToDictionary(group => group.Key, group => group.First());
    }

    private static int? SourceType(string value)
    {
        return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 3 ? number : null;
    }

    private static int? NonNegativeInt(string value)
    {
        return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
    }

    private static int? PositiveInt(string value)
    {
        return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    // USED of DB ALM: "1" / "TRUE" / "YES" (any non-zero number).
    private static bool IsUsed(string value)
    {
        var text = (value ?? "").Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number != 0
            : text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Equals("YES", StringComparison.OrdinalIgnoreCase);
    }
}
