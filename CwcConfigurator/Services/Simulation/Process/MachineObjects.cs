using static CwcConfigurator.Services.Simulation.Process.Def;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Turbine + generator rotor. Speed from the torque balance J·dω/dt = M_water − M_friction.
/// Pelton: M = η·ρ·Q_jet·D·(v_jet − ω·D/2), the deflector cuts the jet between its deadbands.
/// Reaction (Francis/Kaplan): M = η·ρ·g·Q·H/ω_n·(2 − ω/ω_n). On the grid the speed follows the grid frequency.
/// </summary>
public sealed class RotorObject : SimObject
{
    private double _omega;
    private bool _overspeed;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Rotor", "Rotor (speed)", "Turbine",
        "Speed of the unit from the water torque, friction and inertia; locked to the grid when the breaker is closed.",
        [
            Option("Runner", "Turbine type for the torque.", "Pelton", "Reaction")
        ],
        [
            Param("RatedSpeed", 750, "rpm", "Rated speed."),
            Param("PolePairs", 4, "", "Generator pole pairs (frequency = n·pp/60)."),
            Param("Inertia", 375, "kg·m²", "Moment of inertia of turbine + generator."),
            Param("RunnerDiameter", 0.695, "m", "Pelton pitch circle diameter."),
            Param("RatedFlow", 1.45, "m³/s", "Rated turbine flow."),
            Param("RatedHead", 167.8, "m", "Rated net head."),
            Param("Efficiency", 90, "%", "Turbine efficiency."),
            Param("NoLoadFlow", 3, "%", "Flow (in % of rated) that holds the rated speed without load (friction, ventilation)."),
            Param("DeflectorClosedBelow", 5, "%", "Pelton: the deflector cuts the whole jet below this position."),
            Param("DeflectorOpenAbove", 95, "%", "Pelton: the deflector is out of the jet above this position."),
            Param("OverspeedTrip", 120, "%", "Overspeed relay pick-up."),
            Param("SpeedSwitch1", 95, "%", "Speed switch 1 (on at or above)."),
            Param("SpeedSwitch2", 30, "%", "Speed switch 2."),
            Param("SpeedSwitch3", 110, "%", "Speed switch 3."),
            Param("RatedFrequency", 50, "Hz", "Grid rated frequency.")
        ],
        [
            AnalogIn("Flow", "m³/s", "Turbine flow (link from the turbine flow)."),
            AnalogIn("Pressure", "bar", "Pressure at the turbine (link). Not connected = rated head."),
            AnalogIn("Deflector", "%", "Pelton deflector position (link). Not connected = out of the jet."),
            BoolIn("BreakerClosed", "Generator breaker closed: speed locked to the grid (link)."),
            AnalogIn("GridFrequency", "Hz", "Grid frequency. Not connected = rated frequency."),
            AnalogOut("Speed", "rpm", "Speed."),
            AnalogOut("SpeedPercent", "%", "Speed in % of rated."),
            AnalogOut("Frequency", "Hz", "Generator frequency."),
            AnalogOut("ShaftPower", "kW", "Mechanical power on the shaft."),
            AnalogOut("JetFlow", "m³/s", "Flow that drives the runner (after the deflector)."),
            BoolOut("Overspeed", "Overspeed relay."),
            BoolOut("Standstill", "Speed below 1 %."),
            BoolOut("SpeedSwitch1", "Speed ≥ SpeedSwitch1."),
            BoolOut("SpeedSwitch2", "Speed ≥ SpeedSwitch2."),
            BoolOut("SpeedSwitch3", "Speed ≥ SpeedSwitch3.")
        ],
        [],
        ["Speed", "Frequency", "ShaftPower"],
        () => new RotorObject());

    public override void Reset()
    {
        _omega = 0;
        _overspeed = false;
        Publish(0, 0);
    }

    public override void Step(double dt)
    {
        var omegaN = P("RatedSpeed") * 2 * Math.PI / 60;
        var head = Connected("Pressure") ? SimMath.BarToMeters(Math.Max(0, In("Pressure"))) : P("RatedHead");
        var flow = Math.Max(0, In("Flow"));
        var efficiency = P("Efficiency") / 100;
        double jetFlow;
        double torque;
        double friction;
        if (OptIs("Runner", "Pelton"))
        {
            var closed = P("DeflectorClosedBelow");
            var open = P("DeflectorOpenAbove");
            var deflector = Connected("Deflector") ? In("Deflector") : 100;
            var share = open > closed ? SimMath.Clamp((deflector - closed) / (open - closed), 0, 1) : 1;
            jetFlow = flow * share;
            var diameter = Math.Max(0.05, P("RunnerDiameter"));
            var jetSpeed = Math.Sqrt(2 * SimMath.G * head);
            torque = efficiency * SimMath.WaterDensity * jetFlow * diameter * (jetSpeed - _omega * diameter / 2);
            var jetSpeedRated = Math.Sqrt(2 * SimMath.G * P("RatedHead"));
            friction = efficiency * P("NoLoadFlow") / 100 * SimMath.WaterDensity * P("RatedFlow") * diameter * (jetSpeedRated - omegaN * diameter / 2);
        }
        else
        {
            jetFlow = flow;
            torque = efficiency * SimMath.WaterDensity * SimMath.G * jetFlow * head / omegaN * (2 - _omega / omegaN);
            friction = efficiency * P("NoLoadFlow") / 100 * SimMath.WaterDensity * SimMath.G * P("RatedFlow") * P("RatedHead") / omegaN;
        }

        var frictionNow = friction * (0.1 + Math.Pow(_omega / omegaN, 2));
        if (InB("BreakerClosed"))
        {
            var grid = Connected("GridFrequency") ? In("GridFrequency") : P("RatedFrequency");
            _omega = omegaN * grid / Math.Max(1, P("RatedFrequency"));
        }
        else
        {
            _omega = Math.Max(0, _omega + (torque - frictionNow) / Math.Max(1, P("Inertia")) * dt);
        }

        Publish(jetFlow, Math.Max(0, (torque - frictionNow) * _omega / 1000));
    }

    private void Publish(double jetFlow, double shaftPower)
    {
        var rpm = _omega * 60 / (2 * Math.PI);
        var percent = rpm / Math.Max(1, P("RatedSpeed")) * 100;
        _overspeed = SimMath.Hysteresis(_overspeed, percent, P("OverspeedTrip"), P("OverspeedTrip") - 5);
        Out("Speed", rpm);
        Out("SpeedPercent", percent);
        Out("Frequency", rpm * P("PolePairs") / 60);
        Out("ShaftPower", shaftPower);
        Out("JetFlow", jetFlow);
        Out("Overspeed", _overspeed);
        Out("Standstill", percent < 1);
        Out("SpeedSwitch1", percent >= P("SpeedSwitch1"));
        Out("SpeedSwitch2", percent >= P("SpeedSwitch2"));
        Out("SpeedSwitch3", percent >= P("SpeedSwitch3"));
    }
}

/// <summary>
/// Excitation / AVR. Off the grid the field current follows what is needed for the voltage reference, with the
/// field time constant, and the voltage follows the field current through a saturating no-load curve and the
/// speed. On the grid RAISE/LOWER (or the VAR mode) move the reactive power.
/// </summary>
public sealed class ExcitationObject : SimObject
{
    private bool _excited;
    private double _field;
    private double _reference;
    private double _reactive;
    private double _reactiveReference;
    private bool _lastRaise;
    private bool _lastLower;
    private bool _lastStart;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Excitation", "Excitation (AVR)", "Generator",
        "Field current, generator voltage and reactive power from START/STOP and RAISE/LOWER commands.",
        [
            Option("Command", "Pulse: START/STOP pulses switch the excitation on/off. Maintained: excited while START is on.", "Pulse", "Maintained"),
            Option("RaiseLower", "Pulse: each RAISE/LOWER edge is one step. Hold: the reference ramps while the command is on.", "Hold", "Pulse")
        ],
        [
            Param("RatedVoltage", 690, "V", "Generator rated voltage (line-to-line)."),
            Param("NoLoadFieldCurrent", 3.4, "A", "Field current for rated voltage at rated speed, no load."),
            Param("MaxFieldCurrent", 7, "A", "Field current limit."),
            Param("FieldTimeConstant", 1.5, "s", "Open-circuit field time constant (voltage build-up)."),
            Param("SaturationKnee", 0.85, "pu", "Voltage (pu) above which the no-load curve saturates."),
            Param("ReferenceRate", 1, "%/s", "Hold: change of the voltage reference while RAISE/LOWER is on."),
            Param("ReferenceStep", 0.7, "%", "Pulse: change of the voltage reference per RAISE/LOWER."),
            Param("ReferenceMin", 90, "%", "Lowest voltage reference."),
            Param("ReferenceMax", 110, "%", "Highest voltage reference."),
            Param("RatedReactivePower", 1000, "kvar", "Reactive power at full over-excitation."),
            Param("ReactiveRate", 50, "kvar/s", "Change of the reactive power reference while RAISE/LOWER is on (on the grid)."),
            Param("ReactiveTimeConstant", 2, "s", "Reactive power follows its reference with this time constant."),
            Param("ResidualVoltage", 2, "%", "Remanent voltage when not excited.")
        ],
        [
            BoolIn("Start", "Excitation START (DO)."),
            BoolIn("Stop", "Excitation STOP (DO)."),
            BoolIn("Raise", "Voltage / reactive power RAISE (DO)."),
            BoolIn("Lower", "Voltage / reactive power LOWER (DO)."),
            AnalogIn("Setpoint", "V", "Analog voltage setpoint (AO). Not connected = RAISE/LOWER reference."),
            AnalogIn("SpeedPercent", "%", "Speed (link from the rotor).", 100),
            BoolIn("BreakerClosed", "Generator breaker closed (link)."),
            AnalogIn("GridVoltage", "V", "Grid voltage at the generator. Not connected = rated voltage."),
            BoolOut("Excited", "Excitation on (contactor / field breaker)."),
            AnalogOut("FieldCurrent", "A", "Field current."),
            AnalogOut("Voltage", "V", "Generator terminal voltage."),
            AnalogOut("ReactivePower", "kvar", "Reactive power (on the grid)."),
            AnalogOut("Reference", "%", "Voltage reference."),
            BoolOut("RaiseActive", "Raise command echo."),
            BoolOut("LowerActive", "Lower command echo.")
        ],
        [],
        ["Voltage", "FieldCurrent", "ReactivePower"],
        () => new ExcitationObject());

    public override void Reset()
    {
        _excited = false;
        _field = 0;
        _reference = 100;
        _reactive = 0;
        _reactiveReference = 0;
        Publish(0);
    }

    public override void Step(double dt)
    {
        var start = InB("Start");
        if (OptIs("Command", "Maintained"))
        {
            _excited = start && !InB("Stop");
        }
        else
        {
            if (InB("Stop"))
            {
                _excited = false;
            }
            else if (start && !_lastStart)
            {
                _excited = true;
            }
        }

        _lastStart = start;
        var raise = InB("Raise");
        var lower = InB("Lower");
        var onGrid = InB("BreakerClosed");
        var direction = raise && !lower ? 1 : lower && !raise ? -1 : 0;
        if (OptIs("RaiseLower", "Pulse"))
        {
            var edge = raise && !_lastRaise ? 1 : lower && !_lastLower ? -1 : 0;
            if (onGrid)
            {
                _reactiveReference += edge * P("RatedReactivePower") * P("ReferenceStep") / 10;
            }
            else
            {
                _reference += edge * P("ReferenceStep");
            }
        }
        else if (onGrid)
        {
            _reactiveReference += direction * P("ReactiveRate") * dt;
        }
        else
        {
            _reference += direction * P("ReferenceRate") * dt;
        }

        _lastRaise = raise;
        _lastLower = lower;
        _reference = SimMath.Clamp(_reference, P("ReferenceMin"), P("ReferenceMax"));
        var rated = Math.Max(1, P("RatedVoltage"));
        if (Connected("Setpoint") && In("Setpoint") > 0)
        {
            _reference = SimMath.Clamp(In("Setpoint") / rated * 100, P("ReferenceMin"), P("ReferenceMax"));
        }

        var qMax = Math.Max(1, P("RatedReactivePower"));
        _reactiveReference = SimMath.Clamp(_reactiveReference, -0.6 * qMax, qMax);
        var speed = Math.Max(0, In("SpeedPercent")) / 100;
        var noLoadField = Math.Max(0.01, P("NoLoadFieldCurrent"));
        double fieldTarget;
        if (!_excited)
        {
            fieldTarget = 0;
            _reactiveReference = 0;
        }
        else if (onGrid)
        {
            fieldTarget = noLoadField * (1 + 0.6 * _reactiveReference / qMax);
        }
        else
        {
            var neededPu = InverseSaturation(_reference / 100);
            fieldTarget = speed > 0.05 ? noLoadField * neededPu / speed : noLoadField * 0.1;
        }

        fieldTarget = SimMath.Clamp(fieldTarget, 0, P("MaxFieldCurrent"));
        _field = SimMath.Lag(_field, fieldTarget, P("FieldTimeConstant"), dt);
        _reactive = onGrid && _excited ? SimMath.Lag(_reactive, _reactiveReference, P("ReactiveTimeConstant"), dt) : 0;
        var voltage = onGrid
            ? (Connected("GridVoltage") ? In("GridVoltage") : rated)
            : rated * Math.Max(Saturation(_field / noLoadField), _excited ? 0 : P("ResidualVoltage") / 100) * speed;
        Publish(voltage);
    }

    private double Saturation(double fieldPu)
    {
        var knee = SimMath.Clamp(P("SaturationKnee"), 0.3, 1);
        return fieldPu <= knee ? fieldPu : knee + (fieldPu - knee) / (1 + 1.5 * (fieldPu - knee));
    }

    private double InverseSaturation(double voltagePu)
    {
        var knee = SimMath.Clamp(P("SaturationKnee"), 0.3, 1);
        if (voltagePu <= knee)
        {
            return voltagePu;
        }

        var excess = voltagePu - knee;
        return excess >= 1 / 1.5 ? 10 : knee + excess / (1 - 1.5 * excess);
    }

    private void Publish(double voltage)
    {
        Out("Excited", _excited);
        Out("FieldCurrent", _field);
        Out("Voltage", voltage);
        Out("ReactivePower", _reactive);
        Out("Reference", _reference);
        Out("RaiseActive", InB("Raise"));
        Out("LowerActive", InB("Lower"));
    }
}

/// <summary>Circuit breaker (GCB, line, auxiliary) or disconnector with operating times, trips, undervoltage coil and sync check.</summary>
public sealed class BreakerObject : SimObject
{
    private bool _closed;
    private bool _tripped;
    private double _timer;
    private int _moving;
    private double _spring;
    private bool _lastClose;
    private bool _lastOpen;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Breaker", "Breaker / switch", "Electrical",
        "Closes on the close command when allowed (sync OK, undervoltage coil, ready), opens on the open command or a trip.",
        [
            Option("Command", "Pulse: close/open on command edges. Maintained: closed while CloseCmd is on.", "Pulse", "Maintained"),
            Option("Permissive", "Closing condition: none, or the SyncOk input (generator breaker).", "None", "SyncOk")
        ],
        [
            Param("CloseTime", 0.08, "s", "Closing time."),
            Param("OpenTime", 0.05, "s", "Opening time."),
            Param("SpringChargeTime", 6, "s", "Spring charging after a close (0 = no spring feedback)."),
            Param("InitiallyClosed", 0, "0/1", "State after reset.")
        ],
        [
            BoolIn("CloseCmd", "Close command (DO)."),
            BoolIn("OpenCmd", "Open command (DO)."),
            BoolIn("Trip1", "Trip 1 (TRUE = trip)."),
            BoolIn("Trip2", "Trip 2 (TRUE = trip)."),
            BoolIn("Trip3", "Trip 3 (TRUE = trip)."),
            BoolIn("UvCoil", "Undervoltage coil supplied (DO). Not connected = supplied.", true),
            BoolIn("SyncOk", "Synchronising OK (link from the synchroniser)."),
            BoolIn("Ready", "External close-ready condition. Not connected = ready.", true),
            BoolOut("Closed", "Breaker closed."),
            BoolOut("Open", "Breaker open."),
            BoolOut("Tripped", "Opened by a trip (until the next close)."),
            BoolOut("SpringCharged", "Closing spring charged."),
            BoolOut("ReadyToClose", "All close conditions present.")
        ],
        [],
        ["Closed"],
        () => new BreakerObject());

    public override void Reset()
    {
        _closed = P("InitiallyClosed") >= 0.5;
        _tripped = false;
        _moving = 0;
        _spring = P("SpringChargeTime");
        Publish(false);
    }

    public override void Step(double dt)
    {
        var close = InB("CloseCmd");
        var open = InB("OpenCmd");
        var trip = InB("Trip1") || InB("Trip2") || InB("Trip3");
        var uv = !Connected("UvCoil") || InB("UvCoil");
        var spring = P("SpringChargeTime") <= 0 || _spring >= P("SpringChargeTime");
        var ready = uv && spring && !trip && (!Connected("Ready") || InB("Ready"))
                    && (OptIs("Permissive", "None") || InB("SyncOk"));
        var maintained = OptIs("Command", "Maintained");

        if (_closed && (trip || !uv || (maintained ? !close : open && !_lastOpen) || (maintained && open)))
        {
            if (_moving != -1)
            {
                _moving = -1;
                _timer = 0;
                _tripped = trip || !uv;
            }
        }
        else if (!_closed && _moving == 0 && ready && (maintained ? close && !open : close && !_lastClose))
        {
            _moving = 1;
            _timer = 0;
        }

        _lastClose = close;
        _lastOpen = open;
        if (_moving != 0)
        {
            _timer += dt;
            if (_moving > 0 && _timer >= P("CloseTime"))
            {
                _closed = true;
                _tripped = false;
                _spring = 0;
                _moving = 0;
            }
            else if (_moving < 0 && _timer >= P("OpenTime"))
            {
                _closed = false;
                _moving = 0;
            }
        }

        _spring += dt;
        Publish(ready);
    }

    private void Publish(bool ready)
    {
        Out("Closed", _closed);
        Out("Open", !_closed);
        Out("Tripped", _tripped);
        Out("SpringCharged", P("SpringChargeTime") <= 0 || _spring >= P("SpringChargeTime"));
        Out("ReadyToClose", ready && !_closed);
    }
}

/// <summary>
/// Synchroniser / sync check. The phase angle between generator and grid turns with the slip frequency, so
/// SyncOk appears only when the slip and voltage difference are small and the angle passes through zero.
/// </summary>
public sealed class SynchroniserObject : SimObject
{
    private double _angle;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Synchroniser", "Synchroniser", "Electrical",
        "Sync check with slip, voltage difference and a rotating phase angle; speed/voltage raise-lower outputs.",
        [],
        [
            Param("MaxSlip", 0.1, "Hz", "Allowed frequency difference."),
            Param("MaxVoltageDifference", 5, "%", "Allowed voltage difference."),
            Param("MaxAngle", 10, "°", "Allowed phase angle."),
            Param("GridFrequency", 50, "Hz", "Grid frequency when the input is not connected."),
            Param("GridVoltage", 690, "V", "Grid voltage (at generator level) when the input is not connected."),
            Param("SpeedDeadband", 0.05, "Hz", "No speed correction inside this slip.")
        ],
        [
            BoolIn("Enable", "Synchronising ON (DO). Not connected = always active.", true),
            AnalogIn("GeneratorFrequency", "Hz", "Generator frequency (link from the rotor)."),
            AnalogIn("GeneratorVoltage", "V", "Generator voltage (link from the excitation)."),
            AnalogIn("GridFrequency", "Hz", "Grid frequency. Not connected = parameter."),
            AnalogIn("GridVoltage", "V", "Grid voltage. Not connected = parameter."),
            BoolIn("BreakerClosed", "Breaker closed (link): angle and slip are zero."),
            BoolOut("SyncOk", "Synchronising conditions met."),
            BoolOut("Active", "Synchroniser active."),
            BoolOut("SpeedRaise", "Generator too slow."),
            BoolOut("SpeedLower", "Generator too fast."),
            BoolOut("VoltageRaise", "Generator voltage too low."),
            BoolOut("VoltageLower", "Generator voltage too high."),
            AnalogOut("Slip", "Hz", "Generator minus grid frequency."),
            AnalogOut("Angle", "°", "Phase angle."),
            AnalogOut("VoltageDifference", "%", "Generator minus grid voltage.")
        ],
        [],
        ["Slip", "Angle", "VoltageDifference"],
        () => new SynchroniserObject());

    public override void Reset() => _angle = 180;

    public override void Step(double dt)
    {
        var active = !Connected("Enable") || InB("Enable");
        var gridF = Connected("GridFrequency") ? In("GridFrequency") : P("GridFrequency");
        var gridU = Connected("GridVoltage") ? In("GridVoltage") : P("GridVoltage");
        var slip = In("GeneratorFrequency") - gridF;
        var voltageDiff = gridU > 0 ? (In("GeneratorVoltage") - gridU) / gridU * 100 : 0;
        if (InB("BreakerClosed"))
        {
            _angle = 0;
            slip = 0;
        }
        else
        {
            _angle += 360 * slip * dt;
            _angle = ((_angle + 180) % 360 + 360) % 360 - 180;
        }

        var generatorRunning = In("GeneratorFrequency") > 0.5 * gridF;
        var ok = active && generatorRunning && !InB("BreakerClosed") && Math.Abs(slip) <= P("MaxSlip")
                 && Math.Abs(voltageDiff) <= P("MaxVoltageDifference") && Math.Abs(_angle) <= P("MaxAngle");
        var deadband = P("SpeedDeadband");
        Out("SyncOk", ok);
        Out("Active", active);
        Out("SpeedRaise", active && generatorRunning && slip < -deadband);
        Out("SpeedLower", active && generatorRunning && slip > deadband);
        Out("VoltageRaise", active && generatorRunning && voltageDiff < -P("MaxVoltageDifference") / 2);
        Out("VoltageLower", active && generatorRunning && voltageDiff > P("MaxVoltageDifference") / 2);
        Out("Slip", slip);
        Out("Angle", _angle);
        Out("VoltageDifference", voltageDiff);
    }
}

/// <summary>
/// Generator electrical values: active power follows the shaft power (with a time constant) while on the grid,
/// reactive power from the excitation, currents from S and U, voltages with a small load-dependent rise,
/// frequencies and energy counters. Small measurement noise on the analog values.
/// </summary>
public sealed class GeneratorObject : SimObject
{
    private double _power;
    private double _energyP;
    private double _energyQ;
    private double _energyPImport;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Generator", "Generator (electrical)", "Generator",
        "P, Q, S, PF, currents, voltages, frequencies and energy counters of the generator.",
        [],
        [
            Param("RatedPower", 2070, "kW", "Rated active power."),
            Param("RatedVoltage", 690, "V", "Rated voltage (line-to-line)."),
            Param("RatedCurrent", 1925, "A", "Rated current."),
            Param("Efficiency", 96, "%", "Generator efficiency (shaft power → electrical power)."),
            Param("PowerTimeConstant", 1.5, "s", "Electrical power follows the shaft power with this time constant."),
            Param("GridVoltage", 690, "V", "Grid voltage at generator level when the input is not connected."),
            Param("GridFrequency", 50, "Hz", "Grid frequency when the input is not connected."),
            Param("VoltageRise", 1.5, "%", "Voltage rise at rated power on the grid."),
            Param("CurrentUnbalance", 0.5, "%", "Phase current difference."),
            Param("NoiseVoltage", 1, "V", "Measurement noise on voltages."),
            Param("NoiseCurrent", 1, "A", "Measurement noise on currents."),
            Param("NoisePower", 1, "kW", "Measurement noise on powers.")
        ],
        [
            AnalogIn("ShaftPower", "kW", "Shaft power (link from the rotor)."),
            AnalogIn("ReactivePower", "kvar", "Reactive power (link from the excitation)."),
            AnalogIn("Voltage", "V", "Terminal voltage off the grid (link from the excitation)."),
            AnalogIn("Frequency", "Hz", "Generator frequency (link from the rotor)."),
            BoolIn("BreakerClosed", "Generator breaker closed (link)."),
            AnalogIn("GridVoltage", "V", "Grid voltage. Not connected = parameter."),
            AnalogIn("GridFrequency", "Hz", "Grid frequency. Not connected = parameter."),
            AnalogOut("P", "kW", "Active power."),
            AnalogOut("Q", "kvar", "Reactive power."),
            AnalogOut("S", "kVA", "Apparent power."),
            AnalogOut("PF", "", "Power factor."),
            AnalogOut("I", "A", "Current (average)."),
            AnalogOut("I1", "A", "Current phase 1."),
            AnalogOut("I2", "A", "Current phase 2."),
            AnalogOut("I3", "A", "Current phase 3."),
            AnalogOut("U12", "V", "Voltage L1-L2."),
            AnalogOut("U23", "V", "Voltage L2-L3."),
            AnalogOut("U31", "V", "Voltage L3-L1."),
            AnalogOut("U1", "V", "Voltage L1-N."),
            AnalogOut("U2", "V", "Voltage L2-N."),
            AnalogOut("U3", "V", "Voltage L3-N."),
            AnalogOut("FGen", "Hz", "Generator frequency."),
            AnalogOut("FGrid", "Hz", "Grid frequency."),
            AnalogOut("GridU", "V", "Grid voltage."),
            AnalogOut("LoadPercent", "%", "P in % of rated."),
            AnalogOut("CurrentPercent", "%", "I in % of rated."),
            AnalogOut("EnergyExport", "kWh", "Active energy delivered."),
            AnalogOut("EnergyImport", "kWh", "Active energy taken (motoring)."),
            AnalogOut("EnergyReactive", "kvarh", "Reactive energy.")
        ],
        [],
        ["P", "Q", "I", "U12", "FGen"],
        () => new GeneratorObject());

    public override void Reset()
    {
        _power = 0;
    }

    public override void Step(double dt)
    {
        var closed = InB("BreakerClosed");
        var gridU = Connected("GridVoltage") ? In("GridVoltage") : P("GridVoltage");
        var gridF = Connected("GridFrequency") ? In("GridFrequency") : P("GridFrequency");
        var target = closed ? In("ShaftPower") * P("Efficiency") / 100 : 0;
        _power = closed ? SimMath.Lag(_power, target, P("PowerTimeConstant"), dt) : 0;
        var reactive = closed ? In("ReactivePower") : 0;
        var rated = Math.Max(1, P("RatedPower"));
        var voltage = closed ? gridU * (1 + P("VoltageRise") / 100 * _power / rated) : Math.Max(0, In("Voltage"));
        var apparent = Math.Sqrt(_power * _power + reactive * reactive);
        var current = voltage > 1 ? apparent * 1000 / (Math.Sqrt(3) * voltage) : 0;
        var unbalance = P("CurrentUnbalance") / 100;
        var noiseU = P("NoiseVoltage");
        var noiseI = current > 0 ? P("NoiseCurrent") : 0;
        var noiseP = closed ? P("NoisePower") : 0;
        var p = _power + Noise(noiseP);
        var q = reactive + Noise(noiseP);

        _energyP += Math.Max(0, _power) * dt / 3600;
        _energyPImport += Math.Max(0, -_power) * dt / 3600;
        _energyQ += Math.Abs(reactive) * dt / 3600;
        Out("P", p);
        Out("Q", q);
        Out("S", Math.Sqrt(p * p + q * q));
        Out("PF", apparent > 1 ? _power / apparent : 1);
        Out("I", current + Noise(noiseI));
        Out("I1", current * (1 + unbalance) + Noise(noiseI));
        Out("I2", current + Noise(noiseI));
        Out("I3", current * (1 - unbalance) + Noise(noiseI));
        var u = voltage > 1 ? voltage : 0;
        Out("U12", u > 0 ? u + Noise(noiseU) : 0);
        Out("U23", u > 0 ? u + Noise(noiseU) : 0);
        Out("U31", u > 0 ? u + Noise(noiseU) : 0);
        Out("U1", u > 0 ? u / Math.Sqrt(3) + Noise(noiseU / 2) : 0);
        Out("U2", u > 0 ? u / Math.Sqrt(3) + Noise(noiseU / 2) : 0);
        Out("U3", u > 0 ? u / Math.Sqrt(3) + Noise(noiseU / 2) : 0);
        Out("FGen", closed ? gridF : In("Frequency"));
        Out("FGrid", gridF);
        Out("GridU", gridU + Noise(noiseU));
        Out("LoadPercent", _power / rated * 100);
        Out("CurrentPercent", current / Math.Max(1, P("RatedCurrent")) * 100);
        Out("EnergyExport", _energyP);
        Out("EnergyImport", _energyPImport);
        Out("EnergyReactive", _energyQ);
    }
}
