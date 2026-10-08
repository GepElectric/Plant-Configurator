using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.AlarmTesting;

public sealed class AlarmTestPlanBuilder
{
    private const int SimulatorDbNumber = 30;
    private const int AlarmDbNumber = 5;
    private const int DefaultDigitalModules = 16;
    private const int DefaultDigitalChannels = 16;
    private const int DefaultAnalogModules = 6;
    private const int DefaultAnalogChannels = 8;
    private const int DefaultCounterModules = 4;
    private const int AlarmSoloSizeBytes = 24;
    private const int AlarmSoloAlarmByteOffset = 2;
    private const int AlarmSoloAlarmBitOffset = 4;

    public AlarmTestPlan Build(CwcProject project)
    {
        var points = new Dictionary<string, AlarmTestCase>(StringComparer.OrdinalIgnoreCase);

        foreach (var alarm in project.DbCatalog.AlarmRows.Where(IsUsed).OrderBy(row => row.Id))
        {
            var sourceDigId = LeadingIntOrNull(alarm.SourceDigId);
            var sourceAnaId = LeadingIntOrNull(alarm.SourceAnaId);
            AlarmTestCase? point = null;

            if (sourceDigId.HasValue)
            {
                point = BuildDigitalPoint(project, sourceDigId.Value);
            }
            else if (sourceAnaId.HasValue)
            {
                point = BuildAnalogPoint(project, sourceAnaId.Value);
            }

            if (point is null)
            {
                continue;
            }

            if (!points.TryGetValue(point.TestPointId, out var existing))
            {
                existing = point;
                points.Add(existing.TestPointId, existing);
            }

            existing.ExpectedAlarms.Add(BuildExpectedAlarm(project, alarm));
        }

        var plan = new AlarmTestPlan();
        foreach (var point in points.Values
                     .OrderBy(point => point.SourceKindLabel, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(point => point.ModuleNumber ?? int.MaxValue)
                     .ThenBy(point => point.Port ?? int.MaxValue)
                     .ThenBy(point => point.SignalId ?? int.MaxValue))
        {
            point.Status = point.ExpectedAlarms.Count == 0 ? AlarmTestStatus.Skipped : AlarmTestStatus.Ready;
            plan.Cases.Add(point);
        }

        return plan;
    }

    private static AlarmTestCase? BuildDigitalPoint(CwcProject project, int sourceId)
    {
        var dbDi = project.DbCatalog.DiRows.FirstOrDefault(row => row.Id == sourceId);
        var signal = project.DigitalInputs.FirstOrDefault(row => row.Id == sourceId);
        if (signal is null)
        {
            return null;
        }

        return new AlarmTestCase
        {
            TestPointId = $"DI:{sourceId}:{signal.ModuleNumber}:{signal.Port}",
            SourceKind = AlarmTestSourceKind.DigitalInput,
            SourceId = sourceId.ToString(CultureInfo.InvariantCulture),
            SignalId = signal.Id,
            VariableName = FirstNonEmpty(dbDi?.PlcName, signal.StructName),
            SystemName = FirstNonEmpty(dbDi?.SystemNameEnglish, dbDi?.PlcStructure, signal.Structure),
            SignalName = FirstNonEmpty(dbDi?.NameEnglish, signal.SignalName),
            ModuleNumber = signal.ModuleNumber,
            Port = signal.Port,
            SimulatorPath = $"SIMULATOR.DI.MODUL[{signal.ModuleNumber}].CH[{signal.Port}]",
            SimulatorAddress = ResolveDigitalSimulatorAddress(project, signal),
            TriggerValue = signal.Config == SignalConfigState.Inverted ? "FALSE" : "TRUE",
            ResetValue = signal.Config == SignalConfigState.Inverted ? "TRUE" : "FALSE",
            Notes = "Test point grouped by physical/simulator DI source."
        };
    }

    private static AlarmTestCase? BuildAnalogPoint(CwcProject project, int sourceId)
    {
        var dbMeas = project.DbCatalog.MeasRows.FirstOrDefault(row => row.Id == sourceId);
        var ai = project.AnalogInputs.FirstOrDefault(row => row.Id == sourceId);
        if (ai is not null)
        {
            return BuildAnalogPoint(project, sourceId, dbMeas, ai, AlarmTestSourceKind.AnalogInput, "AI");
        }

        var rtd = project.RtdInputs.FirstOrDefault(row => row.Id == sourceId);
        return rtd is null
            ? null
            : BuildAnalogPoint(project, sourceId, dbMeas, rtd, AlarmTestSourceKind.RtdInput, "RTD");
    }

    private static AlarmTestCase BuildAnalogPoint(
        CwcProject project,
        int sourceId,
        DbStringRow? dbMeas,
        AnalogSignalConfig signal,
        AlarmTestSourceKind kind,
        string simulatorSection)
    {
        return new AlarmTestCase
        {
            TestPointId = $"{simulatorSection}:{sourceId}:{signal.ModuleNumber}:{signal.Port}",
            SourceKind = kind,
            SourceId = sourceId.ToString(CultureInfo.InvariantCulture),
            SignalId = signal.Id,
            VariableName = FirstNonEmpty(dbMeas?.PlcName, signal.SignalName),
            SystemName = FirstNonEmpty(dbMeas?.SystemNameEnglish, dbMeas?.PlcStructure, signal.Structure),
            SignalName = FirstNonEmpty(dbMeas?.NameEnglish, signal.MeasurementName, signal.SignalName),
            ModuleNumber = signal.ModuleNumber,
            Port = signal.Port,
            SimulatorPath = $"SIMULATOR.{simulatorSection}.MODUL[{signal.ModuleNumber}].CH[{signal.Port}]",
            SimulatorAddress = ResolveAnalogSimulatorAddress(project, signal, kind),
            TriggerValue = ResolveAnalogTrigger(dbMeas),
            ResetValue = "0",
            Notes = "Test point grouped by physical/simulator analog source."
        };
    }

    private static ExpectedAlarm BuildExpectedAlarm(CwcProject project, DbStringRow alarm)
    {
        var alarmPath = $"ALM.{alarm.PlcStructure}.{alarm.PlcName}";
        return new ExpectedAlarm
        {
            AlarmId = alarm.Id,
            AlarmPath = alarmPath,
            AlarmName = alarm.PlcName,
            AlarmDescription = FirstNonEmpty(alarm.DescriptionEnglish, alarm.NameEnglish),
            AlarmBoolPath = $"{alarmPath}.ALM",
            AlarmBoolAddress = ResolveAlarmBoolAddress(project, alarm),
            SourceType = alarm.SourceType,
            Used = alarm.Used
        };
    }

    private static string ResolveAnalogTrigger(DbStringRow? measurement)
    {
        if (measurement is not null && TryParseDouble(measurement.MaxValue, out var max))
        {
            return Math.Ceiling(max + 1).ToString(CultureInfo.InvariantCulture);
        }

        return "1";
    }

    private static S7Address ResolveDigitalSimulatorAddress(CwcProject project, DigitalSignalConfig signal)
    {
        var channels = ChannelsPerModule(project.DigitalInputs, DefaultDigitalChannels);
        var bytesPerModule = (channels + 7) / 8;
        return new S7Address(SimulatorDbNumber, (signal.ModuleNumber * bytesPerModule) + (signal.Port / 8), signal.Port % 8, S7ValueKind.Bool);
    }

    private static S7Address ResolveAnalogSimulatorAddress(CwcProject project, AnalogSignalConfig signal, AlarmTestSourceKind kind)
    {
        var diModules = ModuleCount(project.DigitalInputs, DefaultDigitalModules);
        var diChannels = ChannelsPerModule(project.DigitalInputs, DefaultDigitalChannels);
        var diBytes = diModules * ((diChannels + 7) / 8);

        var aiModules = ModuleCount(project.AnalogInputs, DefaultAnalogModules);
        var aiChannels = ChannelsPerModule(project.AnalogInputs, DefaultAnalogChannels);
        var aiBytes = aiModules * aiChannels * 2;

        var counterModules = Math.Max(project.Counters.Select(row => row.ModuleNumber).DefaultIfEmpty(DefaultCounterModules - 1).Max() + 1, DefaultCounterModules);
        var counterBytes = counterModules * 4;

        var channels = kind == AlarmTestSourceKind.RtdInput
            ? ChannelsPerModule(project.RtdInputs, DefaultAnalogChannels)
            : aiChannels;
        var sectionBase = kind == AlarmTestSourceKind.RtdInput
            ? diBytes + aiBytes + counterBytes
            : diBytes;
        return new S7Address(SimulatorDbNumber, sectionBase + (((signal.ModuleNumber * channels) + signal.Port) * 2), null, S7ValueKind.Int);
    }

    private static S7Address ResolveAlarmBoolAddress(CwcProject project, DbStringRow alarm)
    {
        var systemIndex = project.DbCatalog.AlarmSystemNameRows
            .OrderBy(row => row.SystemNumber)
            .Select((row, index) => new { row.SystemPlcShortName, Index = index })
            .FirstOrDefault(item => item.SystemPlcShortName.Equals(alarm.PlcStructure, StringComparison.OrdinalIgnoreCase))
            ?.Index ?? Math.Max(0, alarm.Id / 32);
        var alarmIndex = Math.Abs(alarm.Id % 32);
        var byteOffset = (systemIndex * 32 * AlarmSoloSizeBytes) + (alarmIndex * AlarmSoloSizeBytes) + AlarmSoloAlarmByteOffset;
        return new S7Address(AlarmDbNumber, byteOffset, AlarmSoloAlarmBitOffset, S7ValueKind.Bool);
    }

    private static int ModuleCount(IEnumerable<DigitalSignalConfig> rows, int fallback)
    {
        return Math.Max(rows.Select(row => row.ModuleNumber).DefaultIfEmpty(fallback - 1).Max() + 1, fallback);
    }

    private static int ModuleCount(IEnumerable<AnalogSignalConfig> rows, int fallback)
    {
        return Math.Max(rows.Select(row => row.ModuleNumber).DefaultIfEmpty(fallback - 1).Max() + 1, fallback);
    }

    private static int ChannelsPerModule(IEnumerable<DigitalSignalConfig> rows, int fallback)
    {
        return rows.GroupBy(row => row.ModuleNumber)
            .Select(group => group.Select(row => row.Port).DefaultIfEmpty(fallback - 1).Max() + 1)
            .DefaultIfEmpty(fallback)
            .Max();
    }

    private static int ChannelsPerModule(IEnumerable<AnalogSignalConfig> rows, int fallback)
    {
        return rows.GroupBy(row => row.ModuleNumber)
            .Select(group => group.Select(row => row.Port).DefaultIfEmpty(fallback - 1).Max() + 1)
            .DefaultIfEmpty(fallback)
            .Max();
    }

    private static bool IsUsed(DbStringRow row)
    {
        return LeadingIntOrNull(row.Used) == 1
               || row.Used.Equals("true", StringComparison.OrdinalIgnoreCase)
               || row.Used.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int? LeadingIntOrNull(string value)
    {
        value = (value ?? "").Trim();
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryParseDouble(string value, out double parsed)
    {
        return double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }
}
