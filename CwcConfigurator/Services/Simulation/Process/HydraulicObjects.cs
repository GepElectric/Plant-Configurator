using static CwcConfigurator.Services.Simulation.Process.Def;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Servo-driven actuator: Pelton nozzle or deflector, wicket gate, Kaplan blades. Commanded digitally
/// (OPEN/CLOSE), by an analog position setpoint or by an analog speed command; optional ESD valve, power
/// supply / proportional-valve enable and hydraulic pressure. The servo accelerates with a time constant and
/// moves slower when the oil pressure is low (valve flow ∝ √Δp).
/// </summary>
public sealed class ActuatorObject : SimObject
{
    private double _position;
    private double _speed;
    private int _pulseDirection;
    private bool _lastOpen;
    private bool _lastClose;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Actuator", "Actuator (nozzle, deflector, gate)", "Turbine",
        "Servomotor of a nozzle, deflector, wicket gate or runner blades. Position follows the PLC commands.",
        [
            Option("Command", "How the PLC commands the servo: OPEN/CLOSE outputs, AO position setpoint, AO speed command, or chosen by the AnalogMode input.",
                "Discrete", "AnalogPosition", "AnalogSpeed", "Selectable"),
            Option("DiscreteStyle", "Maintained: moves only while OPEN/CLOSE is on. Pulse: a pulse starts the movement to the end position.",
                "Maintained", "Pulse").When("Command", "Discrete", "Selectable"),
            Has("EsdValve", "The servo has an ESD valve (Esd input). No = never ESD."),
            Option("EsdActive", "Level of the Esd input that means ESD (Low = de-energised ESD valve).", "Low", "High").When("EsdValve", "Yes"),
            Has("PowerSupply", "The servo has a switched supply / proportional valve enable (PowerOn input). No = always on."),
            Has("FullyCloseCommand", "The PLC has a separate fully close / lock command (FullyClose input)."),
            Has("OilPressureInput", "The servo pressure comes from an HPU object (OilPressure input). No = always available."),
            Has("MoreOutputs", "Also the Intermediate switch and the servo speed.")
        ],
        [
            Param("OpenTime", 30, "s", "Full stroke time when opening at nominal oil pressure."),
            Param("CloseTime", 30, "s", "Full stroke time when closing at nominal oil pressure."),
            Param("EsdCloseTime", 10, "s", "Full stroke time when the ESD valve closes the servo.").When("EsdValve", "Yes"),
            Param("AccelTime", 0.4, "s", "Time constant of the servo speed (start/stop is not instantaneous)."),
            Param("PositionTau", 0.6, "s", "Time constant of following the setpoint.").When("Command", "AnalogPosition", "Selectable"),
            Param("Deadband", 0, "%", "Setpoint/speed deadband.").When("Command", "AnalogPosition", "AnalogSpeed", "Selectable"),
            Param("MinPressure", 20, "bar", "Oil pressure below which the servo cannot move.").When("OilPressureInput", "Yes"),
            Param("NominalPressure", 80, "bar", "Oil pressure at which the stroke times apply.").When("OilPressureInput", "Yes"),
            Param("SetpointMin", 0, "", "AO value that means 0 % (AnalogPosition) or −100 % speed (AnalogSpeed).").When("Command", "AnalogPosition", "AnalogSpeed", "Selectable"),
            Param("SetpointMax", 100, "", "AO value that means 100 % (AnalogPosition) or +100 % speed (AnalogSpeed).").When("Command", "AnalogPosition", "AnalogSpeed", "Selectable"),
            Param("OpenedAt", 99.5, "%", "Opened limit switch."),
            Param("ClosedAt", 0.5, "%", "Closed limit switch."),
            Param("IntermediateAt", 50, "%", "Intermediate position switch (on at or above).").When("MoreOutputs", "Yes"),
            Param("InitialPosition", 0, "%", "Position after reset.")
        ],
        [
            BoolIn("OpenCmd", "OPEN command (DO).").When("Command", "Discrete", "Selectable"),
            BoolIn("CloseCmd", "CLOSE command (DO).").When("Command", "Discrete", "Selectable"),
            AnalogIn("Setpoint", "", "Position setpoint or speed command (AO).").When("Command", "AnalogPosition", "AnalogSpeed", "Selectable"),
            BoolIn("AnalogMode", "TRUE = analog setpoint, FALSE = OPEN/CLOSE.").When("Command", "Selectable"),
            BoolIn("Esd", "ESD valve (DO).").When("EsdValve", "Yes"),
            BoolIn("PowerOn", "Supply / proportional valve enable (DO).", true).When("PowerSupply", "Yes"),
            BoolIn("FullyClose", "Fully close / lock command (DO).").When("FullyCloseCommand", "Yes"),
            AnalogIn("OilPressure", "bar", "Hydraulic pressure (link from HPU).").When("OilPressureInput", "Yes"),
            AnalogOut("Position", "%", "Servo position."),
            BoolOut("Opened", "Fully open limit switch."),
            BoolOut("Closed", "Fully closed limit switch."),
            BoolOut("Moving", "Servo is moving (consumes oil)."),
            BoolOut("Intermediate", "Intermediate position switch.").When("MoreOutputs", "Yes"),
            AnalogOut("Speed", "%/s", "Servo speed.").When("MoreOutputs", "Yes")
        ],
        [],
        ["Position"],
        () => new ActuatorObject());

    public override void Reset()
    {
        _position = P("InitialPosition");
        _speed = 0;
        _pulseDirection = 0;
        Publish();
    }

    public override void Step(double dt)
    {
        var open = InB("OpenCmd");
        var close = InB("CloseCmd");
        var pressure = In("OilPressure");
        var hasPressure = !Connected("OilPressure") || pressure >= P("MinPressure");
        var pressureFactor = Connected("OilPressure") ? Math.Sqrt(SimMath.Clamp(pressure / Math.Max(1, P("NominalPressure")), 0, 1)) : 1;
        var powered = !Connected("PowerOn") || InB("PowerOn");
        var esd = Connected("Esd") && (OptIs("EsdActive", "Low") ? !InB("Esd") : InB("Esd"));
        var openRate = 100 / Math.Max(0.1, P("OpenTime"));
        var closeRate = 100 / Math.Max(0.1, P("CloseTime"));

        double target;
        if (esd)
        {
            target = -100 / Math.Max(0.1, P("EsdCloseTime"));
            _pulseDirection = 0;
        }
        else if (!powered || !hasPressure)
        {
            target = 0;
        }
        else if (InB("FullyClose"))
        {
            target = -closeRate;
        }
        else
        {
            var mode = Opt("Command");
            if (mode == "Selectable")
            {
                mode = InB("AnalogMode") ? "AnalogPosition" : "Discrete";
            }

            target = mode switch
            {
                "AnalogPosition" => PositionSpeed(openRate, closeRate),
                "AnalogSpeed" => CommandedSpeed(openRate, closeRate),
                _ => DiscreteSpeed(open, close, openRate, closeRate)
            };
            target *= pressureFactor;
        }

        _lastOpen = open;
        _lastClose = close;
        _speed = SimMath.Lag(_speed, target, P("AccelTime"), dt);
        _position += _speed * dt;
        if (_position <= 0 || _position >= 100)
        {
            _position = SimMath.Clamp(_position, 0, 100);
            _speed = 0;
            _pulseDirection = 0;
        }

        Publish();
    }

    private double DiscreteSpeed(bool open, bool close, double openRate, double closeRate)
    {
        if (OptIs("DiscreteStyle", "Pulse"))
        {
            if (open && close)
            {
                _pulseDirection = 0;
            }
            else if (open && !_lastOpen)
            {
                _pulseDirection = 1;
            }
            else if (close && !_lastClose)
            {
                _pulseDirection = -1;
            }

            return _pulseDirection > 0 ? openRate : _pulseDirection < 0 ? -closeRate : 0;
        }

        return open && !close ? openRate : close && !open ? -closeRate : 0;
    }

    private double PositionSpeed(double openRate, double closeRate)
    {
        var span = P("SetpointMax") - P("SetpointMin");
        var setpoint = span == 0 ? 0 : SimMath.Clamp((In("Setpoint") - P("SetpointMin")) / span * 100, 0, 100);
        var error = setpoint - _position;
        if (Math.Abs(error) <= P("Deadband"))
        {
            return 0;
        }

        return SimMath.Clamp(error / Math.Max(0.05, P("PositionTau")), -closeRate, openRate);
    }

    private double CommandedSpeed(double openRate, double closeRate)
    {
        var span = P("SetpointMax") - P("SetpointMin");
        var command = span == 0 ? 0 : SimMath.Clamp((In("Setpoint") - P("SetpointMin")) / span * 200 - 100, -100, 100);
        var deadband = P("Deadband");
        if (Math.Abs(command) <= deadband)
        {
            return 0;
        }

        command = (command - Math.Sign(command) * deadband) * 100 / Math.Max(1, 100 - deadband);
        return command >= 0 ? command / 100 * openRate : command / 100 * closeRate;
    }

    private void Publish()
    {
        Out("Position", _position);
        Out("Speed", _speed);
        Out("Opened", _position >= P("OpenedAt"));
        Out("Closed", _position <= P("ClosedAt"));
        Out("Intermediate", _position >= P("IntermediateAt"));
        Out("Moving", Math.Abs(_speed) > 0.05);
    }
}

/// <summary>
/// Valve with a travel time: main inlet valve (MIV), bypass, intake gate, drain valve. The flow opening follows
/// the valve type characteristic (a butterfly or spherical valve passes little water at small travel).
/// </summary>
public sealed class ValveObject : SimObject
{
    private double _position;
    private double _speed;
    private int _latch;
    private bool _lastOpen;
    private bool _lastClose;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Valve", "Valve (MIV, bypass, gate)", "Waterway",
        "Valve driven to open/closed with travel times, permissives (oil pressure, pump, pressure equalised) and fail-safe closing.",
        [
            Option("Command", "Maintained: opens while OpenCmd is on and closes when it drops (fail-close, like an MIV). " +
                              "Pulse: OPEN/CLOSE pulses start a full movement. OpenClose: moves only while a command is on.",
                "Maintained", "Pulse", "OpenClose"),
            Has("EsdValve", "The valve has an ESD / hold-open valve (Esd input). No = never ESD."),
            Option("EsdActive", "Level of the Esd input that means ESD.", "Low", "High").When("EsdValve", "Yes"),
            Has("Interlocks", "Opening needs oil pressure, a running pump or equalised pressure (extra inputs)."),
            Has("MoreOutputs", "Also Intermediate, Opening (through the valve characteristic) and the open / close movement outputs."),
            Option("Characteristic", "Flow opening versus travel.", "Linear", "Butterfly", "Spherical").When("MoreOutputs", "Yes")
        ],
        [
            Param("OpenTime", 90, "s", "Opening time."),
            Param("CloseTime", 40, "s", "Closing time."),
            Param("AccelTime", 0.5, "s", "Time constant of start/stop of the movement."),
            Param("MinPressure", 0, "bar", "Oil pressure needed to open (0 = not required).").When("Interlocks", "Yes"),
            Param("EqualisedPercent", 0, "%", "Downstream pressure needed as % of upstream before opening (0 = not checked).").When("Interlocks", "Yes"),
            Param("OpenedAt", 99.9, "%", "Open limit switch."),
            Param("ClosedAt", 0.1, "%", "Closed limit switch."),
            Param("IntermediateAt", 94.44, "%", "Intermediate switch (e.g. MIV 85° of 90°).").When("MoreOutputs", "Yes"),
            Param("InitialPosition", 0, "%", "Position after reset.")
        ],
        [
            BoolIn("OpenCmd", "OPEN command (DO)."),
            BoolIn("CloseCmd", "CLOSE command (DO)."),
            BoolIn("Esd", "ESD / hold-open valve (DO).").When("EsdValve", "Yes"),
            BoolIn("PumpRunning", "Opening needs the HPU pump running. Not connected = not required.", true).When("Interlocks", "Yes"),
            AnalogIn("OilPressure", "bar", "Hydraulic pressure (link from HPU).").When("Interlocks", "Yes"),
            AnalogIn("UpstreamPressure", "bar", "Pressure before the valve (for the equalised permissive).").When("Interlocks", "Yes"),
            AnalogIn("DownstreamPressure", "bar", "Pressure after the valve.").When("Interlocks", "Yes"),
            AnalogOut("Position", "%", "Valve / gate position."),
            BoolOut("Open", "Open limit switch."),
            BoolOut("Closed", "Closed limit switch."),
            BoolOut("Moving", "Valve is moving."),
            BoolOut("Intermediate", "Intermediate switch.").When("MoreOutputs", "Yes"),
            AnalogOut("Opening", "%", "Flow opening (travel through the valve characteristic).").When("MoreOutputs", "Yes"),
            BoolOut("OpenMoving", "Moving in the open direction (open contactor).").When("MoreOutputs", "Yes"),
            BoolOut("CloseMoving", "Moving in the close direction (close contactor).").When("MoreOutputs", "Yes")
        ],
        [],
        ["Position"],
        () => new ValveObject());

    public override void Reset()
    {
        _position = P("InitialPosition");
        _speed = 0;
        _latch = 0;
        Publish();
    }

    public override void Step(double dt)
    {
        var open = InB("OpenCmd");
        var close = InB("CloseCmd");
        var esd = Connected("Esd") && (OptIs("EsdActive", "Low") ? !InB("Esd") : InB("Esd"));
        var pressureOk = P("MinPressure") <= 0 || !Connected("OilPressure") || In("OilPressure") >= P("MinPressure");
        var pumpOk = !Connected("PumpRunning") || InB("PumpRunning");
        var equalised = P("EqualisedPercent") <= 0 || !Connected("UpstreamPressure")
                        || In("DownstreamPressure") >= In("UpstreamPressure") * P("EqualisedPercent") / 100;
        var openRate = 100 / Math.Max(0.1, P("OpenTime"));
        var closeRate = 100 / Math.Max(0.1, P("CloseTime"));

        int direction;
        switch (Opt("Command"))
        {
            case "Maintained":
                direction = open && !close ? 1 : -1;
                break;
            case "Pulse":
                if (open && close)
                {
                    _latch = 0;
                }
                else if (open && !_lastOpen)
                {
                    _latch = 1;
                }
                else if (close && !_lastClose)
                {
                    _latch = -1;
                }

                direction = _latch;
                break;
            default:
                direction = open && !close ? 1 : close && !open ? -1 : 0;
                break;
        }

        _lastOpen = open;
        _lastClose = close;
        if (esd || (Opt("Command") == "Maintained" && !pressureOk && Connected("OilPressure")))
        {
            direction = -1;
        }
        else if (direction > 0 && (!pressureOk || !pumpOk || (_position <= P("ClosedAt") && !equalised)))
        {
            direction = 0;
        }

        var target = direction > 0 ? openRate : direction < 0 ? -closeRate : 0;
        _speed = SimMath.Lag(_speed, target, P("AccelTime"), dt);
        _position += _speed * dt;
        if (_position <= 0 || _position >= 100)
        {
            _position = SimMath.Clamp(_position, 0, 100);
            _speed = 0;
            _latch = 0;
        }

        Publish();
    }

    private void Publish()
    {
        var x = _position / 100;
        var opening = Opt("Characteristic") switch
        {
            "Butterfly" => 1 - Math.Cos(x * Math.PI / 2),
            "Spherical" => x * x * (3 - 2 * x),
            _ => x
        };
        Out("Position", _position);
        Out("Opening", opening * 100);
        Out("Open", _position >= P("OpenedAt"));
        Out("Closed", _position <= P("ClosedAt"));
        Out("Intermediate", _position >= P("IntermediateAt"));
        Out("Moving", Math.Abs(_speed) > 0.01);
        Out("OpenMoving", _speed > 0.01);
        Out("CloseMoving", _speed < -0.01);
    }
}

/// <summary>
/// Hydraulic power unit. Pumps fill a gas-loaded accumulator: above the precharge pressure the pressure rises
/// with the isothermal gas law (slow at first, steep near the top), consumers (moving servos/valves) and
/// leakage take oil away. A pressure-reducing valve gives the system pressure; switches follow both pressures.
/// </summary>
public sealed class HpuObject : SimObject
{
    private const int Pumps = 3;
    private const int Consumers = 8;
    private double _pressure;
    private double _oilTemp;
    private double _accumulatorOil;
    private readonly double[] _pumpRunTime = new double[Pumps];
    private bool _pumpDemand;
    private bool _systemOk;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Hpu", "HPU (hydraulic power unit)", "Hydraulics",
        "Oil pumps, accumulator, pressure-reducing valve and pressure / level / temperature switches.",
        [
            Option("Accumulator", "Gas accumulator (pressure rises along the gas law) or stiff system.", "Yes", "No")
        ],
        [
            Param("PumpFlow", 25, "l/min", "Delivery of one pump."),
            Param("AccumulatorVolume", 50, "l", "Gas volume of the accumulator(s)."),
            Param("PrechargePressure", 90, "bar", "Gas precharge pressure."),
            Param("ReliefPressure", 185, "bar", "Relief valve: maximum pressure."),
            Param("ConsumerFlow", 15, "l/min", "Oil taken by each moving consumer."),
            Param("Leakage", 0.3, "l/min", "Continuous internal leakage."),
            Param("ReducerSetpoint", 80, "bar", "System pressure after the reducing valve (0 = no reducer)."),
            Param("PumpStartPressure", 110, "bar", "Pump demand on below this accumulator pressure."),
            Param("PumpStopPressure", 170, "bar", "Pump demand off above this accumulator pressure."),
            Param("AccuLowPressure", 105, "bar", "Accumulator pressure low / ESD switch."),
            Param("AccuHighPressure", 180, "bar", "Accumulator pressure high switch."),
            Param("SystemLowPressure", 75, "bar", "System pressure OK window: low limit."),
            Param("SystemHighPressure", 85, "bar", "System pressure OK window: high limit."),
            Param("TankVolume", 250, "l", "Oil tank volume."),
            Param("TankLevel", 70, "%", "Tank level with an empty accumulator."),
            Param("TankLowLevel", 40, "%", "Oil level low switch."),
            Param("AmbientTemp", 25, "°C", "Oil temperature at rest."),
            Param("PumpHeating", 20, "°C", "Oil temperature rise with a pump running continuously."),
            Param("OilTempTau", 1200, "s", "Time constant of the oil temperature."),
            Param("OilHighTemp", 60, "°C", "Oil temperature high switch."),
            Param("PumpFeedbackDelay", 0.5, "s", "Pump running feedback after the command.")
        ],
        [
            .. Numbered(Pumps, i => BoolIn($"Pump{i}Cmd", $"Pump {i} ON command (DO).")),
            BoolIn("Unload", "Unloading / dump valve open: pump delivery returns to the tank."),
            .. Numbered(Consumers, i => BoolIn($"Consumer{i}", $"Consumer {i} moving (link from an actuator or valve).")),
            AnalogOut("AccumulatorPressure", "bar", "Accumulator pressure."),
            AnalogOut("SystemPressure", "bar", "System pressure after the reducing valve."),
            .. Numbered(Pumps, i => BoolOut($"Pump{i}Running", $"Pump {i} running (contactor feedback).")),
            BoolOut("PumpDemand", "Pump start/stop demand from the pressure switch (hysteresis)."),
            BoolOut("AccuPressureOk", "Accumulator pressure above the low / ESD limit."),
            BoolOut("AccuPressureHigh", "Accumulator pressure high."),
            BoolOut("SystemPressureOk", "System pressure inside the OK window."),
            AnalogOut("OilLevel", "%", "Tank oil level."),
            BoolOut("OilLevelLow", "Tank oil level low."),
            AnalogOut("OilTemperature", "°C", "Oil temperature."),
            BoolOut("OilTemperatureHigh", "Oil temperature high.")
        ],
        [],
        ["AccumulatorPressure", "SystemPressure", "OilLevel"],
        () => new HpuObject());

    public override void Reset()
    {
        _pressure = 0;
        _accumulatorOil = 0;
        _oilTemp = P("AmbientTemp");
        Array.Clear(_pumpRunTime);
        _pumpDemand = true;
        Publish();
    }

    public override void Step(double dt)
    {
        var running = 0;
        for (var pump = 0; pump < Pumps; pump++)
        {
            _pumpRunTime[pump] = InB($"Pump{pump + 1}Cmd") ? _pumpRunTime[pump] + dt : 0;
            if (_pumpRunTime[pump] >= P("PumpFeedbackDelay"))
            {
                running++;
            }
        }

        var consumers = Enumerable.Range(1, Consumers).Count(index => InB($"Consumer{index}"));
        var inflow = InB("Unload") ? 0 : running * P("PumpFlow");
        var outflow = consumers * P("ConsumerFlow") + P("Leakage") + (InB("Unload") && _pressure > 0 ? 200 : 0);
        var volume = (inflow - outflow) / 60 * dt; // litres in this step

        var precharge = Math.Max(1, P("PrechargePressure"));
        var gas = OptIs("Accumulator", "Yes") ? Math.Max(0.1, P("AccumulatorVolume")) : 0.1;
        if (_pressure < precharge || _accumulatorOil <= 0)
        {
            // Below the precharge the oil only fills the pipes: a stiff, fast rise up to the precharge.
            _pressure = SimMath.Clamp(_pressure + volume * precharge / 0.5, 0, precharge);
            if (_pressure >= precharge && volume > 0)
            {
                _accumulatorOil = 1e-6;
            }
        }

        if (_pressure >= precharge)
        {
            _accumulatorOil = Math.Max(0, _accumulatorOil + volume);
            _pressure = _accumulatorOil <= 0 ? precharge * 0.999 : precharge * gas / Math.Max(1e-6, gas - Math.Min(_accumulatorOil, gas * 0.95));
        }

        if (_pressure > P("ReliefPressure"))
        {
            _pressure = P("ReliefPressure");
            _accumulatorOil = Math.Max(0, gas - precharge * gas / _pressure);
        }

        var duty = running > 0 ? 1 : 0;
        _oilTemp = SimMath.Lag(_oilTemp, P("AmbientTemp") + duty * P("PumpHeating"), P("OilTempTau"), dt);
        _pumpDemand = SimMath.Hysteresis(_pumpDemand, -_pressure, -P("PumpStartPressure"), -P("PumpStopPressure"));
        Publish();
    }

    private void Publish()
    {
        var reducer = P("ReducerSetpoint");
        var system = reducer > 0 ? Math.Min(_pressure, reducer) : _pressure;
        _systemOk = system >= P("SystemLowPressure") && system <= P("SystemHighPressure");
        var level = P("TankLevel") - _accumulatorOil / Math.Max(1, P("TankVolume")) * 100;
        Out("AccumulatorPressure", _pressure);
        Out("SystemPressure", system);
        for (var pump = 0; pump < Pumps; pump++)
        {
            Out($"Pump{pump + 1}Running", _pumpRunTime[pump] >= P("PumpFeedbackDelay"));
        }

        Out("PumpDemand", _pumpDemand);
        Out("AccuPressureOk", _pressure > P("AccuLowPressure"));
        Out("AccuPressureHigh", _pressure >= P("AccuHighPressure"));
        Out("SystemPressureOk", _systemOk);
        Out("OilLevel", level);
        Out("OilLevelLow", level <= P("TankLowLevel"));
        Out("OilTemperature", _oilTemp);
        Out("OilTemperatureHigh", _oilTemp >= P("OilHighTemp"));
    }
}
