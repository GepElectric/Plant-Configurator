using static CwcConfigurator.Services.Simulation.Process.Def;

namespace CwcConfigurator.Services.Simulation.Process;

// Cabinet / AC distribution objects. Every object has a Supply input (voltage present upstream; not connected = live)
// and a Live output (voltage passed on downstream), so a single-line diagram is built by linking them:
// Busbar.Live → MCB.Supply, MCB.Live → Contactor.Supply, Contactor.Live → Motor.Supply.

/// <summary>Busbar of a distribution: live when a supply is live. On an SLD screen it is drawn as a bar.</summary>
public sealed class BusbarObject : SimObject
{
    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Busbar", "Busbar", "Electrical",
        "Busbar of a distribution: live when a supply is live. Every outgoing feeder has its own output (Feeder1, Feeder2, …).",
        [
            Count("Supplies", "Number of incoming supplies (any live supply makes the busbar live).", 1, 3, 1),
            Count("Feeders", "Number of outgoing feeders.", 1, Feeders, 4)
        ],
        [],
        [
            BoolIn("Supply1", "Incoming supply 1 (link). Not connected = live.", true).AsPower().When("Supplies", AtLeast(1, 3)),
            BoolIn("Supply2", "Incoming supply 2 (link).").AsPower().When("Supplies", AtLeast(2, 3)),
            BoolIn("Supply3", "Incoming supply 3 (link).").AsPower().When("Supplies", AtLeast(3, 3)),
            .. Numbered(Feeders, i => BoolOut($"Feeder{i}", $"Outgoing feeder {i} (link to the Supply of its MCB / disconnector).").AsPower()
                .When("Feeders", AtLeast(i, Feeders))),
            BoolOut("Live", "Busbar live (to a DI or an indication).")
        ],
        [],
        [],
        () => new BusbarObject());

    private const int Feeders = 12;

    public override void Step(double dt)
    {
        var count = int.TryParse(Opt("Supplies"), out var value) ? value : 1;
        var live = Enumerable.Range(1, count).Any(i => InB($"Supply{i}"));
        Out("Live", live);
        for (var i = 1; i <= Feeders; i++)
        {
            Out($"Feeder{i}", live);
        }
    }
}

/// <summary>Switching device set by hand on the SCADA (Position): its state goes to the PLC, Live passes the voltage on.</summary>
public abstract class ManualSwitchObject : SimObject
{
    protected bool Closed => OptIs("Position", ClosedValue);

    protected abstract string ClosedValue { get; }

    public override void Step(double dt) => Publish();

    public override void Reset() => Publish();

    protected abstract void Publish();
}

/// <summary>MCB / circuit breaker: On, Off or Tripped, set on the SCADA; the state goes to the PLC.</summary>
public sealed class McbObject : ManualSwitchObject
{
    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Mcb", "MCB / circuit breaker", "Electrical",
        "Circuit breaker of a feeder: set On, Off or Tripped on the SCADA. On and Tripped go to the PLC (DI).",
        [
            Option("Position", "State of the breaker (set on the SCADA).", "On", "Off", "Tripped")
        ],
        [],
        [
            BoolIn("Supply", "Voltage upstream (link, e.g. @Busbar.Live). Not connected = live.", true).AsPower(),
            BoolOut("On", "Breaker on (to a DI, e.g. ..._MCB_ON)."),
            BoolOut("Tripped", "Breaker tripped (to a DI)."),
            BoolOut("Live", "Voltage after the breaker.").AsPower()
        ],
        [],
        [],
        () => new McbObject());

    protected override string ClosedValue => "On";

    protected override void Publish()
    {
        Out("On", Closed);
        Out("Tripped", OptIs("Position", "Tripped"));
        Out("Live", Closed && InB("Supply"));
    }
}

/// <summary>Disconnector: Closed or Open, set on the SCADA; the position goes to the PLC.</summary>
public sealed class DisconnectorObject : ManualSwitchObject
{
    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Disconnector", "Disconnector", "Electrical",
        "Disconnector / isolator: set Closed or Open on the SCADA. Closed and Open go to the PLC (DI).",
        [
            Option("Position", "Position of the disconnector (set on the SCADA).", "Closed", "Open")
        ],
        [],
        [
            BoolIn("Supply", "Voltage upstream (link). Not connected = live.", true).AsPower(),
            BoolOut("Closed", "Closed (to a DI)."),
            BoolOut("Open", "Open (to a DI)."),
            BoolOut("Live", "Voltage after the disconnector.").AsPower()
        ],
        [],
        [],
        () => new DisconnectorObject());

    protected override string ClosedValue => "Closed";

    protected override void Publish()
    {
        Out("Closed", Closed);
        Out("Open", !Closed);
        Out("Live", Closed && InB("Supply"));
    }
}

/// <summary>Earthing switch: Earthed or NotEarthed, set on the SCADA; while earthed no voltage is passed on.</summary>
public sealed class EarthSwitchObject : ManualSwitchObject
{
    public static ObjectTypeInfo TypeInfo { get; } = new(
        "EarthSwitch", "Earthing switch", "Electrical",
        "Earthing switch: set Earthed or NotEarthed on the SCADA. Earthed goes to the PLC (DI); while earthed Live is FALSE.",
        [
            Option("Position", "Position of the earthing switch (set on the SCADA).", "NotEarthed", "Earthed")
        ],
        [],
        [
            BoolIn("Supply", "Voltage of the earthed part (link). Not connected = live.", true).AsPower(),
            BoolOut("Earthed", "Earthed (to a DI)."),
            BoolOut("Live", "Voltage passed on (FALSE while earthed).").AsPower()
        ],
        [],
        [],
        () => new EarthSwitchObject());

    protected override string ClosedValue => "Earthed";

    protected override void Publish()
    {
        Out("Earthed", Closed);
        Out("Live", !Closed && InB("Supply"));
    }
}

/// <summary>
/// Contactor: the PLC command switches it, the state goes back to the PLC. Live passes the supply on to a motor.
/// </summary>
public sealed class ContactorObject : SimObject
{
    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Contactor", "Contactor", "Electrical",
        "Contactor of a pump, fan or heater: Command from the PLC (DO), State back to the PLC (DI). " +
        "Live = closed and supplied, for the motor (Supply = @K1.Live).",
        [],
        [],
        [
            BoolIn("Command", "Command from the PLC (DO), e.g. ..._CONT_ON."),
            BoolIn("Supply", "Voltage before the contactor (link, e.g. @F1.Live). Not connected = live.", true).AsPower(),
            BoolOut("State", "Contactor closed, back to the PLC (DI)."),
            BoolOut("Live", "Voltage after the contactor (link to the motor / pump).").AsPower()
        ],
        [],
        [],
        () => new ContactorObject());

    public override void Step(double dt)
    {
        var closed = InB("Command");
        Out("State", closed);
        Out("Live", closed && InB("Supply"));
    }
}

/// <summary>Motor of a pump or fan: runs up while supplied (e.g. from a contactor), Running and current.</summary>
public sealed class MotorObject : SimObject
{
    private double _speed;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Motor", "Motor / pump", "Electrical",
        "Motor of a pump or fan, supplied by a contactor (Supply = @K1.Live): runs up and down, Running and current.",
        [],
        [
            Param("RatedCurrent", 11, "A", "Current when running."),
            Param("StartTime", 2, "s", "Run-up time."),
            Param("StopTime", 5, "s", "Run-down time.")
        ],
        [
            BoolIn("Supply", "Voltage at the motor (link, e.g. @K1.Live).").AsPower(),
            BoolOut("Running", "Motor running (to a DI or to the HPU / reservoir as a link)."),
            AnalogOut("Speed", "%", "Speed."),
            AnalogOut("Current", "A", "Motor current (to an AI).")
        ],
        [],
        ["Speed", "Current"],
        () => new MotorObject());

    public override void Reset()
    {
        _speed = 0;
        Publish(false);
    }

    public override void Step(double dt)
    {
        var supplied = InB("Supply");
        _speed = supplied
            ? Math.Min(100, _speed + 100 / Math.Max(0.1, P("StartTime")) * dt)
            : Math.Max(0, _speed - 100 / Math.Max(0.1, P("StopTime")) * dt);
        Publish(supplied);
    }

    private void Publish(bool supplied)
    {
        // Starting current 6 × rated, falling to the rated current at full speed.
        var n = _speed / 100;
        Out("Running", _speed >= 90);
        Out("Speed", _speed);
        Out("Current", supplied ? P("RatedCurrent") * (n < 0.95 ? 6 - 5 * Math.Pow(n / 0.95, 4) : 1) : 0);
    }
}
