using static CwcConfigurator.Services.Simulation.Process.Def;

namespace CwcConfigurator.Services.Simulation.Process;

// Cabinet / AC distribution objects. Every object has a Supply input (voltage present upstream; not connected = live)
// and a Live output (voltage passed on downstream), so a small single-line diagram can be built by linking them:
// AuxSupply.Live → MCB.Supply, MCB.Live → Contactor.Supply, Contactor.Live → Motor.Supply.

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
            Option("Position", "Handle position set by the operator (on the SCADA). Off → On also resets a trip.", "On", "Off"),
            Has("MotorDrive", "Motor-operated breaker: OnCmd / OffCmd from the PLC."),
            Has("ShuntTrip", "Shunt trip coil: TripCmd from the PLC opens the breaker as tripped."),
            Has("LoadInput", "Load current connected (e.g. @Pump1.Current): overload and short-circuit trips.")
        ],
        [
            Param("Rating", 16, "A", "Rated current.").When("LoadInput", "Yes"),
            Param("OverloadTime", 10, "s", "Trip time at 1.5 × rating (shorter for a higher overload).").When("LoadInput", "Yes"),
            Param("ShortCircuitFactor", 10, "× In", "Instant trip above this multiple of the rating.").When("LoadInput", "Yes")
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
/// Contactor: the PLC switches the coil (maintained output, or ON/OFF pulses with self-hold); the main contacts
/// follow after a short delay and the auxiliary contact reports back to the PLC. Optional thermal overload relay.
/// </summary>
public sealed class ContactorObject : SimObject
{
    private bool _closed;
    private bool _held;
    private bool _overloadTripped;
    private double _timer;
    private double _overload;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Contactor", "Contactor", "Electrical",
        "Contactor of a pump, fan or heater: coil from the PLC, main contacts pass the supply on (Live), auxiliary " +
        "contact back to the PLC. Optional control supply (e.g. from a control MCB) and thermal overload relay.",
        [
            Option("Command", "Coil: the DO holds the coil. Pulses: OnCmd / OffCmd pulses with self-hold.", "Coil", "Pulses"),
            Has("ControlSupply", "The coil is supplied through a separate control circuit (ControlSupply input, e.g. @F2.Live)."),
            Has("OverloadRelay", "Thermal overload relay on the load current (Load input).")
        ],
        [
            Param("CloseDelay", 0.05, "s", "Time from the command to the closed main contacts."),
            Param("OpenDelay", 0.03, "s", "Time from the coil off to the open main contacts."),
            Param("Rating", 10, "A", "Overload relay setting.").When("OverloadRelay", "Yes"),
            Param("OverloadTime", 8, "s", "Trip time at 1.5 × setting.").When("OverloadRelay", "Yes")
        ],
        [
            BoolIn("Coil", "Coil command (DO), e.g. ..._CONT_ON.").When("Command", "Coil"),
            BoolIn("OnCmd", "ON pulse (DO).").When("Command", "Pulses"),
            BoolIn("OffCmd", "OFF pulse (DO).").When("Command", "Pulses"),
            BoolIn("Supply", "Main circuit voltage (link, e.g. @F1.Live). Not connected = live.", true),
            BoolIn("ControlSupply", "Coil voltage (link, e.g. @F2.Live).", true).When("ControlSupply", "Yes"),
            AnalogIn("Load", "A", "Load current (e.g. @Pump1.Current).").When("OverloadRelay", "Yes"),
            BoolIn("OverloadReset", "Reset of the overload relay (DO or =1 for automatic reset).").When("OverloadRelay", "Yes"),
            BoolOut("Closed", "Auxiliary NO contact: contactor closed (to a DI, e.g. ..._CONT_ON)."),
            BoolOut("Open", "Auxiliary NC contact."),
            BoolOut("Live", "Voltage after the main contacts (link to the motor / pump)."),
            BoolOut("Overload", "Overload relay tripped (to a DI).")
        ],
        [],
        [],
        () => new ContactorObject());

    public override void Reset()
    {
        _closed = false;
        _held = false;
        _overloadTripped = false;
        _timer = 0;
        _overload = 0;
        Publish();
    }

    public override void Step(double dt)
    {
        if (InB("OverloadReset"))
        {
            _overloadTripped = false;
        }

        var coilVoltage = InB("ControlSupply") && !_overloadTripped;
        bool energised;
        if (OptIs("Command", "Pulses"))
        {
            // Self-hold through the own auxiliary contact: ON sets, OFF or loss of the coil voltage releases.
            if (InB("OffCmd") || !coilVoltage)
            {
                _held = false;
            }
            else if (InB("OnCmd"))
            {
                _held = true;
            }

            energised = _held;
        }
        else
        {
            energised = InB("Coil") && coilVoltage;
        }

        if (energised != _closed)
        {
            _timer += dt;
            if (_timer >= (energised ? P("CloseDelay") : P("OpenDelay")))
            {
                _closed = energised;
                _timer = 0;
            }
        }
        else
        {
            _timer = 0;
        }

        if (Connected("Load") && _closed)
        {
            var multiple = Math.Abs(In("Load")) / Math.Max(0.1, P("Rating"));
            _overload = SimMath.Thermal(_overload, multiple, P("OverloadTime"), dt);
            if (_overload >= 1)
            {
                _overloadTripped = true;
                _overload = 0;
            }
        }

        Publish();
    }

    private void Publish()
    {
        Out("Closed", _closed);
        Out("Open", !_closed);
        Out("Live", _closed && InB("Supply"));
        Out("Overload", _overloadTripped);
    }
}

/// <summary>
/// Motor driving a pump, fan or compressor: runs up while supplied (e.g. from a contactor), starting current,
/// load current ∝ speed³ for a pump/fan, flow ∝ speed, pressure ∝ speed².
/// </summary>
public sealed class MotorObject : SimObject
{
    private double _speed;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Motor", "Motor / pump", "Electrical",
        "Motor of a pump, fan or compressor, supplied by a contactor (Supply = @K1.Live): run-up, starting current, " +
        "current, flow and pressure. Running goes to a DI or to the reservoir / HPU as a link.",
        [
            Option("Load", "Pump / fan: torque ∝ speed² (current falls at low speed). Constant: constant torque (compressor, conveyor).", "Pump", "Constant")
        ],
        [
            Param("RatedCurrent", 11, "A", "Current at rated load."),
            Param("StartCurrent", 6, "× In", "Starting current (direct on line)."),
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
            AnalogOut("Current", "A", "Motor current (link to the MCB / contactor Load or to an AI)."),
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
