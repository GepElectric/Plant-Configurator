using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>A DB ALM alarm whose source (pulled from the PLC: TRACE_DIG_ID, "SOURCE OF ALARM" in Info) is a digital input.</summary>
public sealed record DiAlarmLink(int? Module, int? Port, int InputId, DbStringRow? Input, DbStringRow Alarm);

/// <summary>
/// Used alarms caused by digital inputs, ordered by hardware: module 0 and its ports, then module 1 … The module and port
/// come from the HW configuration of the input (same ID, not disabled); without it from the DB DI input assignment.
/// </summary>
public static class DiAlarmOverview
{
    public static List<DiAlarmLink> Build(IEnumerable<DbStringRow> alarmRows, IEnumerable<DbStringRow> diRows, IEnumerable<DigitalSignalConfig> hwInputs)
    {
        var hw = hwInputs.Where(input => input.Config != SignalConfigState.Disabled)
            .GroupBy(input => input.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var inputs = diRows.GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.First());
        var links = new List<DiAlarmLink>();
        foreach (var alarm in alarmRows)
        {
            if (!IsUsed(alarm.Used)
                || !int.TryParse((alarm.SourceDigId ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var inputId) || inputId <= 0)
            {
                continue;
            }

            var input = inputs.GetValueOrDefault(inputId);
            int? module = null;
            int? port = null;
            if (hw.TryGetValue(inputId, out var config))
            {
                module = config.ModuleNumber;
                port = config.Port;
            }
            else if (input is not null && int.TryParse(input.ModuleName.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleNumber))
            {
                module = moduleNumber;
                port = input.ModulePort;
            }

            links.Add(new DiAlarmLink(module, port, inputId, input, alarm));
        }

        // Inputs without a module go last.
        return links
            .OrderBy(link => link.Module ?? int.MaxValue)
            .ThenBy(link => link.Port ?? int.MaxValue)
            .ThenBy(link => link.InputId)
            .ThenBy(link => link.Alarm.Id)
            .ToList();
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
