using static CwcConfigurator.Services.Simulation.Process.Def;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Temperature of a winding, bearing, oil or transformer: first-order thermal model. The final temperature rise
/// grows with the square (or linearly) of the driving quantity (current, speed, power); without cooling the rise
/// is larger. Heating and cooling are exponential.
/// </summary>
public sealed class TemperatureObject : SimObject
{
    private double _temperature;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Temperature", "Temperature (thermal)", "Generator",
        "Temperature rising and falling exponentially towards a value set by the load (current, speed or power).",
        [
            Option("Law", "Temperature rise versus the driving quantity: Square (I²R losses) or Linear.", "Square", "Linear")
        ],
        [
            Param("DriverRated", 1925, "", "Value of the Driver input at which NominalTemp is reached (e.g. rated current or speed)."),
            Param("AmbientTemp", 20, "°C", "Temperature at zero load."),
            Param("NominalTemp", 85, "°C", "Final temperature at the rated driver with cooling on."),
            Param("NoCoolingFactor", 1.6, "", "Temperature rise multiplier without cooling."),
            Param("TimeConstant", 1800, "s", "Heating time constant."),
            Param("CoolingTimeFactor", 1.5, "", "Cooling time constant = TimeConstant × this factor."),
            Param("AlarmTemp", 110, "°C", "Alarm."),
            Param("TripTemp", 120, "°C", "Trip."),
            Param("Noise", 0.1, "°C", "Measurement noise.")
        ],
        [
            AnalogIn("Driver", "", "Load quantity: current, speed or power (link)."),
            BoolIn("Cooling", "Cooling running. Not connected = always cooled.", true),
            AnalogIn("Ambient", "°C", "Ambient temperature. Not connected = AmbientTemp."),
            AnalogOut("Temperature", "°C", "Temperature."),
            BoolOut("Alarm", "Temperature alarm."),
            BoolOut("Trip", "Temperature trip.")
        ],
        [],
        ["Temperature"],
        () => new TemperatureObject());

    public override void Reset() => _temperature = P("AmbientTemp");

    public override void Step(double dt)
    {
        var ambient = Connected("Ambient") ? In("Ambient") : P("AmbientTemp");
        var load = Math.Abs(In("Driver")) / Math.Max(1e-6, P("DriverRated"));
        var rise = (P("NominalTemp") - P("AmbientTemp")) * (OptIs("Law", "Linear") ? load : load * load);
        if (Connected("Cooling") && !InB("Cooling"))
        {
            rise *= P("NoCoolingFactor");
        }

        var final = ambient + rise;
        var tau = final >= _temperature ? P("TimeConstant") : P("TimeConstant") * P("CoolingTimeFactor");
        _temperature = SimMath.Lag(_temperature, final, tau, dt);
        Out("Temperature", _temperature + Noise(P("Noise")));
        Out("Alarm", _temperature >= P("AlarmTemp"));
        Out("Trip", _temperature >= P("TripTemp"));
    }
}

/// <summary>Bearing vibration: level from a speed curve, with a slowly wandering random component.</summary>
public sealed class VibrationObject : SimObject
{
    private double _wander;
    private double _value;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Vibration", "Vibration", "Generator",
        "Vibration level from the speed (normal at rated speed, higher in overspeed) with random variation.",
        [],
        [
            Param("AtRatedSpeed", 1.5, "mm/s", "Vibration at rated speed."),
            Param("AtOverspeed", 4.5, "mm/s", "Vibration at 120 % speed."),
            Param("LoadIncrease", 0.3, "mm/s", "Additional vibration at rated load."),
            Param("Variation", 15, "%", "Random variation of the level."),
            Param("TimeConstant", 3, "s", "How fast the random variation changes."),
            Param("AlarmLevel", 4.5, "mm/s", "Alarm."),
            Param("TripLevel", 7.1, "mm/s", "Trip.")
        ],
        [
            AnalogIn("SpeedPercent", "%", "Speed (link from the rotor)."),
            AnalogIn("LoadPercent", "%", "Load (link from the generator)."),
            AnalogOut("Vibration", "mm/s", "Vibration."),
            BoolOut("Alarm", "Vibration alarm."),
            BoolOut("Trip", "Vibration trip.")
        ],
        [],
        ["Vibration"],
        () => new VibrationObject());

    public override void Step(double dt)
    {
        var speed = Math.Max(0, In("SpeedPercent"));
        var level = SimMath.Interpolate([0, 10, 100, 120, 150], [0, 0.1 * P("AtRatedSpeed"), P("AtRatedSpeed"), P("AtOverspeed"), P("AtOverspeed") * 1.8], speed)
                    + P("LoadIncrease") * SimMath.Clamp(In("LoadPercent") / 100, 0, 1.2);
        _wander = SimMath.Lag(_wander, Noise(1), P("TimeConstant"), dt);
        _value = Math.Max(0, level * (1 + P("Variation") / 100 * _wander * 2));
        Out("Vibration", _value);
        Out("Alarm", _value >= P("AlarmLevel"));
        Out("Trip", _value >= P("TripLevel"));
    }
}

/// <summary>Auxiliary supply measurement (e.g. Sentron of the station service): voltage, load, power factor with noise.</summary>
public sealed class AuxSupplyObject : SimObject
{
    private double _energy;
    private double _wander;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "AuxSupply", "Auxiliary supply", "Electrical",
        "Station service measurement: U, I, P, Q, S, PF, f and energy while supplied.",
        [],
        [
            Param("Voltage", 400, "V", "Rated voltage (line-to-line)."),
            Param("Load", 50, "kVA", "Average apparent load."),
            Param("PowerFactor", 0.85, "", "Average power factor."),
            Param("Frequency", 50, "Hz", "Frequency."),
            Param("LoadVariation", 10, "%", "Slow random load variation."),
            Param("NoiseVoltage", 1, "V", "Voltage noise.")
        ],
        [
            BoolIn("Supplied", "Supply breaker closed (link or DO). Not connected = supplied.", true),
            AnalogIn("ExtraLoad", "kVA", "Additional load (e.g. HPU pump running)."),
            AnalogOut("U", "V", "Voltage."),
            AnalogOut("I", "A", "Current."),
            AnalogOut("P", "kW", "Active power."),
            AnalogOut("Q", "kvar", "Reactive power."),
            AnalogOut("S", "kVA", "Apparent power."),
            AnalogOut("PF", "", "Power factor."),
            AnalogOut("F", "Hz", "Frequency."),
            AnalogOut("Energy", "kWh", "Active energy."),
            BoolOut("Live", "Busbar live (link to the MCBs of the distribution: @AuxSupply.Live).").AsPower()
        ],
        [],
        ["U", "P"],
        () => new AuxSupplyObject());

    public override void Step(double dt)
    {
        var supplied = !Connected("Supplied") || InB("Supplied");
        _wander = SimMath.Lag(_wander, Noise(1), 20, dt);
        var s = supplied ? Math.Max(0, P("Load") * (1 + P("LoadVariation") / 100 * _wander * 2) + In("ExtraLoad")) : 0;
        var pf = SimMath.Clamp(P("PowerFactor"), 0, 1);
        var p = s * pf;
        var u = supplied ? P("Voltage") + Noise(P("NoiseVoltage")) : 0;
        _energy += p * dt / 3600;
        Out("U", u);
        Out("I", u > 1 ? s * 1000 / (Math.Sqrt(3) * u) : 0);
        Out("P", p);
        Out("Q", s * Math.Sqrt(1 - pf * pf));
        Out("S", s);
        Out("PF", supplied ? pf : 0);
        Out("F", supplied ? P("Frequency") : 0);
        Out("Energy", _energy);
        Out("Live", supplied);
    }
}

/// <summary>
/// General logic for feedback contacts and permissives (what the old simulator had in the cabinet blocks):
/// echo of a command with delays, NOT/AND/OR, set/reset latch, pulse, analog threshold or a fixed state.
/// </summary>
public sealed class LogicObject : SimObject
{
    private bool _output;
    private bool _latch;
    private double _onTimer;
    private double _offTimer;
    private double _pulseTimer;
    private bool _lastIn;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Logic", "Logic / feedback", "General",
        "Feedback contacts and simple logic: Echo (DI follows a DO), NOT, AND, OR, Latch, Pulse, Threshold, fixed TRUE/FALSE.",
        [
            Option("Function", "Echo: Out = In1 (with delays). Not, And, Or of In1..In4. Latch: Set/Reset. Pulse on a rising In1. " +
                               "Threshold: Analog ≥ Threshold. True/False: fixed state (MCB OK, no alarm).",
                "Echo", "Not", "And", "Or", "Latch", "Pulse", "Threshold", "True", "False")
        ],
        [
            Param("OnDelay", 0, "s", "Output switches on only after the condition was true this long."),
            Param("OffDelay", 0, "s", "Output switches off only after the condition was false this long."),
            Param("PulseTime", 1, "s", "Pulse length."),
            Param("Threshold", 50, "", "Threshold: switching level for the Analog input."),
            Param("Hysteresis", 1, "", "Threshold: switching hysteresis.")
        ],
        [
            BoolIn("In1", "Input 1."),
            BoolIn("In2", "Input 2."),
            BoolIn("In3", "Input 3."),
            BoolIn("In4", "Input 4."),
            BoolIn("Set", "Latch: set."),
            BoolIn("Reset", "Latch: reset (dominant)."),
            AnalogIn("Analog", "", "Threshold: analog value."),
            BoolOut("Out", "Result.")
        ],
        [],
        ["Out"],
        () => new LogicObject());

    public override void Reset()
    {
        _output = Opt("Function") == "True";
        _latch = false;
        Out("Out", _output);
    }

    public override void Step(double dt)
    {
        var inputs = Enumerable.Range(1, 4).Where(i => Connected($"In{i}")).Select(i => InB($"In{i}")).ToList();
        var in1 = InB("In1");
        bool condition;
        switch (Opt("Function"))
        {
            case "Not":
                condition = !in1;
                break;
            case "And":
                condition = inputs.Count > 0 && inputs.All(value => value);
                break;
            case "Or":
                condition = inputs.Any(value => value);
                break;
            case "Latch":
                if (InB("Reset"))
                {
                    _latch = false;
                }
                else if (InB("Set"))
                {
                    _latch = true;
                }

                condition = _latch;
                break;
            case "Pulse":
                if (in1 && !_lastIn)
                {
                    _pulseTimer = P("PulseTime");
                }

                _pulseTimer = Math.Max(0, _pulseTimer - dt);
                condition = _pulseTimer > 0;
                break;
            case "Threshold":
                condition = SimMath.Hysteresis(_output, In("Analog"), P("Threshold"), P("Threshold") - P("Hysteresis"));
                break;
            case "True":
                condition = true;
                break;
            case "False":
                condition = false;
                break;
            default:
                condition = in1;
                break;
        }

        _lastIn = in1;
        if (condition)
        {
            _offTimer = 0;
            _onTimer += dt;
            if (_onTimer >= P("OnDelay"))
            {
                _output = true;
            }
        }
        else
        {
            _onTimer = 0;
            _offTimer += dt;
            if (_offTimer >= P("OffDelay"))
            {
                _output = false;
            }
        }

        Out("Out", _output);
    }
}

/// <summary>
/// Signal source: constant, ramp, sine, random walk or steps, optionally added to an input. Used for disturbances
/// such as a changing river inflow or grid frequency.
/// </summary>
public sealed class SignalObject : SimObject
{
    private double _time;
    private double _value;
    private double _wander;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Signal", "Signal source", "General",
        "Test signal for a control loop (e.g. river inflow, grid frequency): constant, step, ramp, sine, square steps or " +
        "random around an offset, added to the In input. Reset starts it again.",
        [
            Option("Shape", "Constant: Value. Step: Value, after Delay Value + Amplitude. Ramp: after Delay from Value by Rate (limited by Min/Max). " +
                            "Sine: Value + Amplitude·sin. Steps: Value / Value + Amplitude every Period/2. Random: Value + slow random.",
                "Constant", "Step", "Ramp", "Sine", "Steps", "Random")
        ],
        [
            Param("Value", 0, "", "Offset / base value."),
            Param("Amplitude", 0, "", "Step height (Step, Steps), amplitude (Sine, Random).").When("Shape", "Step", "Sine", "Steps", "Random"),
            Param("Delay", 60, "s", "Time after reset when the step / ramp starts.").When("Shape", "Step", "Ramp"),
            Param("Rate", 0.01, "/s", "Ramp rate (negative = falling).").When("Shape", "Ramp"),
            Param("Period", 600, "s", "Period (Sine, Steps), time constant (Random).").When("Shape", "Sine", "Steps", "Random"),
            Param("Min", -1e9, "", "Lower limit of the output."),
            Param("Max", 1e9, "", "Upper limit of the output.")
        ],
        [
            AnalogIn("In", "", "Value added to the signal."),
            BoolIn("Enable", "Signal active. Not connected = always active.", true),
            AnalogOut("Out", "", "Signal.")
        ],
        [],
        ["Out"],
        () => new SignalObject());

    public override void Reset()
    {
        _time = 0;
        _value = P("Value");
        _wander = 0;
    }

    public override void Step(double dt)
    {
        if (Connected("Enable") && !InB("Enable"))
        {
            Out("Out", In("In"));
            return;
        }

        _time += dt;
        var period = Math.Max(0.1, P("Period"));
        _value = Opt("Shape") switch
        {
            "Sine" => P("Value") + P("Amplitude") * Math.Sin(2 * Math.PI * _time / period),
            "Step" => P("Value") + (_time >= P("Delay") ? P("Amplitude") : 0),
            "Ramp" => P("Value") + P("Rate") * Math.Max(0, _time - P("Delay")),
            "Random" => P("Value") + P("Amplitude") * (_wander = SimMath.Lag(_wander, Noise(1), period, dt)) * 2,
            "Steps" => P("Value") + ((int)(_time / (period / 2)) % 2 == 1 ? P("Amplitude") : 0),
            _ => P("Value")
        };
        Out("Out", SimMath.Clamp(_value + In("In"), P("Min"), P("Max")));
    }
}

/// <summary>
/// PID controller, for control loops the simulated plant needs but no PLC provides (e.g. a governor when testing
/// the model offline, or a level control that the real plant does elsewhere).
/// </summary>
public sealed class PidObject : SimObject
{
    private double _integral;
    private double _lastError;
    private bool _first = true;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Pid", "PID controller", "General",
        "PID controller: Out follows Setpoint − Measurement (Direct) or the opposite (Reverse), limited to OutMin…OutMax.",
        [
            Option("Direction", "Direct: output rises when the measurement is below the setpoint. Reverse: the opposite.", "Direct", "Reverse")
        ],
        [
            Param("Setpoint", 100, "", "Setpoint when the Setpoint input is not connected."),
            Param("Kp", 1, "", "Proportional gain (output per unit of error)."),
            Param("Ti", 10, "s", "Integral time (0 = no integral action)."),
            Param("Td", 0, "s", "Derivative time."),
            Param("OutMin", 0, "", "Output lower limit."),
            Param("OutMax", 100, "", "Output upper limit."),
            Param("Initial", 0, "", "Output after reset.")
        ],
        [
            AnalogIn("Setpoint", "", "Setpoint. Not connected = parameter Setpoint."),
            AnalogIn("Measurement", "", "Measured value (link)."),
            BoolIn("Enable", "Control active. Not connected = always; disabled = output holds Tracking.", true),
            AnalogIn("Tracking", "", "Output while disabled."),
            AnalogOut("Out", "", "Controller output."),
            AnalogOut("Error", "", "Setpoint − measurement.")
        ],
        [],
        ["Out", "Error"],
        () => new PidObject());

    public override void Reset()
    {
        _integral = P("Initial");
        _first = true;
        Out("Out", _integral);
    }

    public override void Step(double dt)
    {
        var setpoint = Connected("Setpoint") ? In("Setpoint") : P("Setpoint");
        var error = (setpoint - In("Measurement")) * (OptIs("Direction", "Reverse") ? -1 : 1);
        var min = P("OutMin");
        var max = Math.Max(min, P("OutMax"));
        if (Connected("Enable") && !InB("Enable"))
        {
            _integral = SimMath.Clamp(In("Tracking"), min, max);
            _first = true;
            Out("Out", _integral);
            Out("Error", error);
            return;
        }

        var derivative = _first || dt <= 0 ? 0 : (error - _lastError) / dt;
        _first = false;
        _lastError = error;
        var proportional = P("Kp") * error;
        if (P("Ti") > 0)
        {
            _integral = SimMath.Clamp(_integral + P("Kp") * error / P("Ti") * dt, min - proportional, max - proportional);
        }
        else
        {
            _integral = 0;
        }

        Out("Out", SimMath.Clamp(proportional + _integral + P("Kp") * P("Td") * derivative, min, max));
        Out("Error", error);
    }
}

/// <summary>All object types offered by the simulator.</summary>
public static class ObjectCatalog
{
    public static IReadOnlyList<ObjectTypeInfo> All { get; } =
    [
        ActuatorObject.TypeInfo,
        ValveObject.TypeInfo,
        HpuObject.TypeInfo,
        ReservoirObject.TypeInfo,
        TurbineOpeningObject.TypeInfo,
        PenstockObject.TypeInfo,
        TurbinePressureObject.TypeInfo,
        TurbineFlowObject.TypeInfo,
        RotorObject.TypeInfo,
        ExcitationObject.TypeInfo,
        BreakerObject.TypeInfo,
        SynchroniserObject.TypeInfo,
        GeneratorObject.TypeInfo,
        TemperatureObject.TypeInfo,
        VibrationObject.TypeInfo,
        AuxSupplyObject.TypeInfo,
        BusbarObject.TypeInfo,
        DisconnectorObject.TypeInfo,
        EarthSwitchObject.TypeInfo,
        McbObject.TypeInfo,
        ContactorObject.TypeInfo,
        MotorObject.TypeInfo,
        LogicObject.TypeInfo,
        SignalObject.TypeInfo,
        PidObject.TypeInfo
    ];

    public static ObjectTypeInfo? Find(string type) =>
        All.FirstOrDefault(info => info.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
}
