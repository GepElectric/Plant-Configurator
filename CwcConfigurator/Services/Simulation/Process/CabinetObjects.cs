using static CwcConfigurator.Services.Simulation.Process.Def;

namespace CwcConfigurator.Services.Simulation.Process;

// Cabinet / AC distribution objects. Every object has a Supply input (voltage present upstream; not connected = live)
// and a Live output (voltage passed on downstream), so a small single-line diagram can be built by linking them:
// AuxSupply.Live â†’ MCB.Supply, MCB.Live â†’ Contactor.Supply, Contactor.Live â†’ Motor.Supply.

/// <summary>
/// Miniature circuit breaker / motor protection switch: switched by hand on the SCADA (Position), optionally by a
/// motor drive or shunt trip from the PLC; trips on overload or short circuit when the load current is connected.
/// </summary>
public sealed class McbObject : SimObject
{
    private bool _closed;
    private bool _tripped;
    private double _overload;
    private string _lastPosition = "";
    private bool _lastOn;
    private bool _lastOff;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Mcb", "MCB / circuit breaker", "Electrical",
        "Circuit breaker of a feeder: ON/OFF by hand (Position on the SCADA), optional motor drive and shunt trip from the PLC. " +
        "Trips on overload (thermal) or short circuit when Load is connected. After a trip set Position to Off and On again.",
        [
            Option("Position", "Handle position set by the operator (on the SCADA). Off â†’ On also resets a trip.", "On", "Off"),
            Has("MotorDrive", "Motor-operated breaker: OnCmd / OffCmd from the PLC."),
            Has("ShuntTrip", "Shunt trip coil: TripCmd from the PLC opens the breaker as tripped."),
            Has("LoadInput", "Load current connected (e.g. @Pump1.Current): overload and short-circuit trips.")
        ],
        [
            Param("Rating", 16, "A", "Rated current.").When("LoadInput", "Yes"),
            Param("OverloadTime", 10, "s", "Trip time at 1.5 Ã— rating (shorter for a higher overload).").When("LoadInput", "Yes"),
            Param("ShortCircuitFactor", 10, "Ã— In", "Instant trip above this multiple of the rating.").When("LoadInput", "Yes")
        ],
        [
            BoolIn("Supply", "Voltage upstream (link, e.g. @AuxSupply.Live). Not connected = live.", true),
            BoolIn("OnCmd", "Close command (DO).").When("MotorDrive", "Yes"),
            BoolIn("OffCmd", "Open command (DO).").When("MotorDrive", "Yes"),
            BoolIn("TripCmd", "Shunt trip (DO).").When("ShuntTrip", "Yes"),
            AnalogIn("Load", "A", "Current through the breaker.").When("LoadInput", "Yes"),
            BoolOut("On", "Auxiliary contact: breaker closed (to a DI, e.g. ..._MCB_ON)."),
            BoolOut("Tripped", "Alarm contact: breaker tripped."),
            BoolOut("Live", "Voltage after the breaker (link to the next object).")
        ],
        [],
        [],
        () => new McbObject());

    public override void Reset()
    {
        _closed = OptIs("Position", "On");
        _tripped = false;
        _overload = 0;
        _lastPosition = Opt("Position");
        Publish();
    }

    public override void Step(double dt)
    {
        var position = Opt("Position");
        if (!position.Equals(_lastPosition, StringComparison.OrdinalIgnoreCase))
        {
            _closed = position.Equals("On", StringComparison.OrdinalIgnoreCase);
            _tripped = false;
            _overload = 0;
            _lastPosition = position;
        }

        var on = InB("OnCmd");
        var off = InB("OffCmd");
        if (on && !_lastOn && !_tripped)
        {
            _closed = true;
        }

        if (off && !_lastOff)
        {
            _closed = false;
            _tripped = false;
        }

        _lastOn = on;
        _lastOff = off;
        if (InB("TripCmd"))
        {
            Trip();
        }

        if (Connected("Load") && _closed)
        {
            var multiple = Math.Abs(In("Load")) / Math.Max(0.1, P("Rating"));
            if (multiple >= P("ShortCircuitFactor"))
            {
                Trip();
            }

            _overload = SimMath.Thermal(_overload, multiple, P("OverloadTime"), dt);
            if (_overload >= 1)
            {
                Trip();
            }
        }

        Publish();
    }

    private void Trip()
    {
        _closed = false;
        _tripped = true;
        _overload = 0;
    }

    private void Publish()
    {
        Out("On", _closed);
        Out("Tripped", _tripped);
        Out("Live", _closed && InB("Supply"));
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
            BoolIn("Supply", "Voltage before the contactor (link, e.g. @F1.Live). Not connected = live.", true),
            BoolOut("State", "Contactor closed, back to the PLC (DI)."),
            BoolOut("Live", "Voltage after the contactor (link to the motor / pump).")
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

/// <summary>
/// Motor driving a pump, fan or compressor: runs up while supplied (e.g. from a contactor), starting current,
/// load current âˆ speedÂ³ for a pump/fan, flow âˆ speed, pressure âˆ speedÂ².
/// </summary>
public sealed class MotorObject : SimObject
{
    private double _speed;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Motor", "Motor / pump", "Electrical",
        "Motor of a pump, fan or compressor, supplied by a contactor (Supply = @K1.Live): run-up, starting current, " +
        "current, flow and pressure. Running goes to a DI or to the reservoir / HPU as a link.",
        [
            Option("Load", "Pump / fan: torque âˆ speedÂ² (current falls at low speed). Constant: constant torque (compressor, conveyor).", "Pump", "Constant")
        ],
        [
            Param("RatedCurrent", 11, "A", "Current at rated load."),
            Param("StartCurrent", 6, "Ã— In", "Starting current (direct on line)."),
            Param("StartTime", 2, "s", "Run-up time to rated speed."),
            Param("StopTime", 5, "s", "Run-down time."),
            Param("RatedFlow", 100, "l/min", "Flow at rated speed."),
            Param("RatedPressure", 5, "bar", "Pressure at rated speed."),
            Param("RunningAt", 90, "%", "Speed from which Running is TRUE.")
        ],
        [
            BoolIn("Supply", "Voltage at the motor (link, e.g. @K1.Live)."),
            BoolIn("PressureRelease", "Discharge open / valve open: TRUE = full flow, FALSE = no flow (pressure only). Not connected = open.", true),
            BoolOut("Running", "Motor running (speed above RunningAt)."),
            AnalogOut("Speed", "%", "Speed."),
            AnalogOut("Current", "A", "Motor current (link to the MCB Load or to an AI)."),
            AnalogOut("Flow", "l/min", "Flow."),
            AnalogOut("Pressure", "bar", "Discharge pressure.")
        ],
        [],
        ["Speed", "Current", "Flow"],
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
        var n = _speed / 100;
        var load = OptIs("Load", "Pump") ? 0.25 + 0.75 * n * n * n : 1;
        // During run-up the current falls from the starting current to the load current.
        var current = supplied ? P("RatedCurrent") * (n < 0.95 ? P("StartCurrent") - (P("StartCurrent") - load) * Math.Pow(n / 0.95, 4) : load) : 0;
        var open = InB("PressureRelease");
        Out("Running", _speed >= P("RunningAt"));
        Out("Speed", _speed);
        Out("Current", current);
        Out("Flow", open ? P("RatedFlow") * n : 0);
        Out("Pressure", P("RatedPressure") * n * n * (open ? 1 : 1.2));
    }
}
