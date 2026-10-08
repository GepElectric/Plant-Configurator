using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.AlarmTesting;

public enum AlarmTestSourceKind
{
    Unknown,
    DigitalInput,
    AnalogInput,
    RtdInput,
    Program
}

public enum AlarmTestStatus
{
    Ready,
    Waiting,
    Skipped,
    Passed,
    Failed
}

public sealed class AlarmTestCase
{
    public string TestPointId { get; init; } = "";
    public AlarmTestSourceKind SourceKind { get; init; }
    public string SourceId { get; init; } = "";
    public int? ModuleNumber { get; init; }
    public int? Port { get; init; }
    public int? SignalId { get; init; }
    public string VariableName { get; init; } = "";
    public string SystemName { get; init; } = "";
    public string SignalName { get; init; } = "";
    public string SimulatorPath { get; init; } = "";
    public S7Address? SimulatorAddress { get; init; }
    public string TriggerValue { get; init; } = "";
    public string ResetValue { get; init; } = "";
    public string Notes { get; init; } = "";
    public AlarmTestStatus Status { get; set; }
    public bool IsLastTested { get; set; }
    public bool? CurrentBoolState { get; set; }
    public DateTimeOffset? LastActionUtc { get; set; }
    public string LastResult { get; set; } = "";
    public List<ExpectedAlarm> ExpectedAlarms { get; } = [];
    public bool CanTest => Status != AlarmTestStatus.Skipped;

    public string SourceKindLabel => SourceKind switch
    {
        AlarmTestSourceKind.DigitalInput => "DI",
        AlarmTestSourceKind.AnalogInput => "AI",
        AlarmTestSourceKind.RtdInput => "RTD",
        AlarmTestSourceKind.Program => "Program",
        _ => "Unknown"
    };

    public string ModuleLabel => ModuleNumber.HasValue ? ModuleNumber.Value.ToString() : "";
    public string PortLabel => Port.HasValue ? Port.Value.ToString() : "";
    public string SimulatorAddressLabel => SimulatorAddress?.ToString() ?? "";
    public string CurrentStateLabel => CurrentBoolState.HasValue ? (CurrentBoolState.Value ? "TRUE" : "FALSE") : "?";
    public string ToggleLabel => CurrentBoolState == true ? "Set FALSE" : "Set TRUE";
    public int ExpectedAlarmCount => ExpectedAlarms.Count;
    public string ExpectedAlarmSummary => ExpectedAlarms.Count == 0
        ? ""
        : string.Join(", ", ExpectedAlarms.Take(3).Select(alarm => $"{alarm.AlarmId} {alarm.AlarmName}"))
          + (ExpectedAlarms.Count > 3 ? $" +{ExpectedAlarms.Count - 3}" : "");
}

public sealed class AlarmTestPlan
{
    public List<AlarmTestCase> Cases { get; } = [];
    public IEnumerable<AlarmTestModule> DigitalModules => Cases
        .Where(item => item.SourceKind == AlarmTestSourceKind.DigitalInput && item.ModuleNumber.HasValue)
        .GroupBy(item => item.ModuleNumber!.Value)
        .OrderBy(group => group.Key)
        .Select(group => new AlarmTestModule(
            group.Key,
            group.OrderBy(item => item.Port ?? int.MaxValue).ThenBy(item => item.SignalId ?? int.MaxValue).ToList()));

    public int ReadyCount => Cases.Count(item => item.Status == AlarmTestStatus.Ready);
    public int WaitingCount => Cases.Count(item => item.Status == AlarmTestStatus.Waiting);
    public int SkippedCount => Cases.Count(item => item.Status == AlarmTestStatus.Skipped);
    public int PassedCount => Cases.Count(item => item.Status == AlarmTestStatus.Passed);
    public int FailedCount => Cases.Count(item => item.Status == AlarmTestStatus.Failed);
}

public sealed record AlarmTestModule(int ModuleNumber, List<AlarmTestCase> Signals)
{
    public string Header => $"DI Module {ModuleNumber}";
    public bool IsExpanded { get; set; } = true;
}

public sealed class ExpectedAlarm
{
    public int AlarmId { get; init; }
    public string AlarmPath { get; init; } = "";
    public string AlarmName { get; init; } = "";
    public string AlarmDescription { get; init; } = "";
    public string Used { get; init; } = "";
    public string SourceType { get; init; } = "";
    public string AlarmBoolPath { get; init; } = "";
    public S7Address? AlarmBoolAddress { get; init; }
    public bool MessageReceived { get; set; }
    public bool DebugBoolActive { get; set; }

    public string AlarmBoolAddressLabel => AlarmBoolAddress?.ToString() ?? "";
}

public sealed class PlcAlarmEvent
{
    public string Id { get; init; } = "";
    public int AlarmNumber { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? Timestamp { get; init; }
    public string Producer { get; init; } = "";
    public string AcknowledgementState { get; init; } = "";
    public string AlarmText { get; init; } = "";
    public string InfoText { get; init; } = "";
    public bool IsExpected { get; set; }

    // Populated by the OpenPipe source (richer matching/display than the Web API).
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string SourceType { get; init; } = "";
    public string Origin { get; init; } = "";
    public string AlarmClassName { get; init; } = "";
    public string AlarmSystem { get; init; } = "";
    public IReadOnlyList<string> Parameters { get; init; } = [];
    public string Source { get; init; } = "WebApi";

    // Program alarms (Program_Alarm) arrive in classes ending "_ALM"; the unit
    // prefix varies (I_ALM = Intake, U1_ALM, C_ALM, ...). "_DI" etc. are other
    // event classes we don't test here.
    public bool IsProgramAlarmClass => AlarmClassName.EndsWith("_ALM", StringComparison.OrdinalIgnoreCase);

    // A program alarm coming "in" (a real raise) vs. a clear/going event.
    public bool IsIncoming => Status.Equals("Incoming", StringComparison.OrdinalIgnoreCase);

    // Local arrival time, used to separate alarms that fire after a signal toggle.
    public DateTimeOffset ReceivedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string TimeLabel => Timestamp?.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "";
    public string MatchLabel => IsExpected ? "Expected" : "Unexpected";
}

/// <summary>One selectable PLC in the tester, with its own pre-built test plan so
/// signals from different PLC databases never mix.</summary>
public sealed class AlarmTestPlcOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public AlarmTestPlan Plan { get; init; } = new();

    public override string ToString() => Name;
}

public enum S7ValueKind
{
    Bool,
    Int
}

public sealed record S7Address(int DbNumber, int ByteOffset, int? BitOffset, S7ValueKind ValueKind)
{
    public override string ToString()
    {
        return ValueKind == S7ValueKind.Bool
            ? $"DB{DbNumber}.DBX{ByteOffset}.{BitOffset.GetValueOrDefault()}"
            : $"DB{DbNumber}.DBW{ByteOffset}";
    }
}
