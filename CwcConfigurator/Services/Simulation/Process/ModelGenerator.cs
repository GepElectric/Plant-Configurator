using System.Globalization;
using System.Text.RegularExpressions;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Builds a starting plant model for one PLC database from its signal names (our standard PLC names such as
/// NOZZ_1_OPEN, MIV_ON, HPU_PUMP_1_ON, GATE_1_OPEN_CMD, NOZZLE_1, PENSTOCK_WATER_PRESSURE, SPEED_1 ...).
/// Everything it binds is listed in <see cref="Result.Report"/>; it never replaces objects the user already has.
/// </summary>
public static class ModelGenerator
{
    public sealed class Result
    {
        public List<ObjectConfig> Objects { get; } = [];
        public List<string> Report { get; } = [];
    }

    public static Result Generate(PlcSession session, ProcessModel existing)
    {
        var result = new Result();
        var finder = new Finder(session.Signals.Where(signal => signal.IsEnabled).ToList());
        var model = new Builder(result, existing);

        Gates(finder, model);
        Reservoir(session, finder, model);
        Unit(finder, model);
        Devices(finder, model);
        Feedbacks(finder, model);
        foreach (var item in result.Objects)
        {
            ObjectCatalog.Find(item.Type)?.UpgradeSwitches(item);   // found ESD / supply signals switch that equipment on
        }

        Layout(result.Objects, existing);
        if (result.Objects.Count == 0)
        {
            result.Report.Add("No known signal names found; add objects by hand.");
        }

        return result;
    }

    private static void Gates(Finder find, Builder model)
    {
        for (var n = 1; n <= 8; n++)
        {
            var open = find.Bind(Kinds.Do, $@"^GATE_{n}_OPEN(_CMD)?$");
            if (open is null)
            {
                continue;
            }

            var gate = model.Add($"Gate{n}", "Valve", $"Intake gate / flap {n}");
            if (gate is null)
            {
                continue;
            }

            gate.Options["Command"] = "OpenClose";
            gate.Params["OpenTime"] = 120;
            gate.Params["CloseTime"] = 120;
            gate.Params["IntermediateAt"] = 50;
            model.Port(gate, "OpenCmd", open);
            model.Port(gate, "CloseCmd", find.Bind(Kinds.Do, $@"^GATE_{n}_CLOSE(_CMD)?$"));
            model.Port(gate, "Position", find.Bind(Kinds.Measure, $@"^GATE_{n}(_POS(ITION)?)?$"));
            model.Port(gate, "Open", find.Bind(Kinds.Di, $@"^GATE_{n}_(LIM_SWITCH_)?OPEN(ED)?$"));
            model.Port(gate, "Closed", find.Bind(Kinds.Di, $@"^GATE_{n}_(LIM_SWITCH_)?CLOSED$"));
            model.Port(gate, "Moving", find.BindAll(Kinds.Di, $@"^GATE_{n}_(VFD_RUNNING|RUNNING|MOVING)$"));
            model.Port(gate, "OpenMoving", find.Bind(Kinds.Di, $@"^GATE_{n}_(OPEN_CONT_ON|OPENING)$"));
            model.Port(gate, "CloseMoving", find.Bind(Kinds.Di, $@"^GATE_{n}_(CLOSE_CONT_ON|CLOSING)$"));
        }
    }

    private static void Reservoir(PlcSession session, Finder find, Builder model)
    {
        var levelSignal = find.One(Kinds.Measure, @"^(WATER_LEVEL(_1)?|RESERVOIR_LEVEL|LEVEL_1|UPSTREAM_LEVEL)$");
        if (levelSignal is null)
        {
            return;
        }

        var reservoir = model.Add("Reservoir", "Reservoir", "Reservoir / intake level: river inflow − unit flows − gates − spillway");
        if (reservoir is null)
        {
            return;
        }

        model.Port(reservoir, "Level", $"{levelSignal.Kind}:{levelSignal.Id}");
        if (levelSignal.Scaling is { IsValid: true } scaling)
        {
            var (low, high) = scaling.EngineeringRange;
            var span = Math.Max(1, high - low);
            reservoir.Params["BottomElevation"] = Math.Round(low, 2);
            reservoir.Params["MaxDepth"] = Math.Round(span, 2);
            reservoir.Params["InitialDepth"] = Math.Round(span * 0.6, 2);
            reservoir.Params["SpillCrestDepth"] = Math.Round(span * 0.8, 2);
            reservoir.Params["LowLevelDepth"] = Math.Round(span * 0.2, 2);
            reservoir.Params["HighLevelDepth"] = Math.Round(span * 0.85, 2);
            reservoir.Params["GateReferenceDepth"] = Math.Round(span * 0.6, 2);
            model.Note($"Reservoir: level range {low:0.##}…{high:0.##} m from the scaling of {levelSignal.PlcName}; check Area, inflow and gate capacities.");
        }

        for (var n = 1; n <= 4; n++)
        {
            if (model.Exists($"Gate{n}"))
            {
                model.Port(reservoir, $"Gate{n}Opening", $"@Gate{n}.Opening");
            }
        }

        model.Note("Reservoir: link each unit's turbine flow to Unit1Flow…Unit8Flow (e.g. @Unit 1/Flow.Flow).");
    }

    private static void Unit(Finder find, Builder model)
    {
        var nozzles = Enumerable.Range(1, 6).Where(n => find.Exists(Kinds.Do, $@"^NOZZ(LE)?_{n}_OPEN$") || find.Exists(Kinds.Measure, $@"^NOZZLE_{n}$")).ToList();
        var deflector = find.Exists(Kinds.Do, @"^DEF(LECTOR)?_1_") || find.Exists(Kinds.Measure, @"DEFLECTOR");
        var miv = find.Exists(Kinds.Do, @"^MIV_(ON|OPEN)");
        if (nozzles.Count == 0 && !miv && !find.Exists(Kinds.Do, @"^HPU_PUMP_\d_ON$"))
        {
            return;
        }

        var hpu = model.Add("HPU", "Hpu", "Hydraulic power unit");
        if (hpu is not null)
        {
            for (var p = 1; p <= 3; p++)
            {
                model.Port(hpu, $"Pump{p}Cmd", find.Bind(Kinds.Do, $@"^(HPU_)?PUMP_{p}_ON$"));
                model.Port(hpu, $"Pump{p}Running", find.BindAll(Kinds.Di, $@"^(HPU_)?PUMP_{p}_(CONT_ON|RUNNING|ON)$"));
            }

            model.Port(hpu, "AccumulatorPressure", find.Bind(Kinds.Measure, @"^HPU_ACCUMULATOR_OIL_PRESSURE"));
            model.Port(hpu, "SystemPressure", find.Bind(Kinds.Measure, @"^HPU_SYSTEM_OIL_PRESSURE"));
            model.Port(hpu, "OilLevel", find.Bind(Kinds.Measure, @"^HPU_OIL_LEVEL"));
            model.Port(hpu, "OilTemperature", find.Bind(Kinds.Measure, @"^HPU_OIL_(TANK_)?TEMP"));
            model.Port(hpu, "AccuPressureOk", find.Bind(Kinds.Di, @"PRESS(URE)?_ACCU(MULATOR)?_(ESD|OK)"));
            model.Port(hpu, "PumpDemand", find.Bind(Kinds.Di, @"PRESS(URE)?_ACCU(MULATOR)?_START_STOP"));
            model.Port(hpu, "SystemPressureOk", find.Bind(Kinds.Di, @"PRESS(URE)?_SYS(TEM)?_OK"));
            model.Port(hpu, "OilLevelLow", find.Bind(Kinds.Di, @"OIL_LEVEL_LOW"));
            model.Port(hpu, "OilTemperatureHigh", find.Bind(Kinds.Di, @"OIL_TEMP(ERATURE)?_HIGH"));
        }

        var consumers = new List<string>();
        foreach (var n in nozzles)
        {
            var nozzle = model.Add($"Nozzle{n}", "Actuator", $"Pelton nozzle {n}");
            if (nozzle is null)
            {
                continue;
            }

            nozzle.Params["OpenTime"] = 30;
            nozzle.Params["CloseTime"] = 30;
            model.Port(nozzle, "OpenCmd", find.Bind(Kinds.Do, $@"^NOZZ(LE)?_{n}_OPEN$"));
            model.Port(nozzle, "CloseCmd", find.Bind(Kinds.Do, $@"^NOZZ(LE)?_{n}_CLOSE$"));
            model.Port(nozzle, "Esd", find.Bind(Kinds.Do, $@"^NOZZ(LE)?_{n}_ESD$"));
            model.Port(nozzle, "PowerOn", find.Bind(Kinds.Do, $@"^NOZZ(LE)?_{n}_PS_ON$"));
            model.Port(nozzle, "FullyClose", find.Bind(Kinds.Do, $@"^NOZZ(LE)?_{n}_FULLY_CLOSE$"));
            model.Port(nozzle, "OilPressure", "@HPU.SystemPressure");
            model.Port(nozzle, "Position", find.Bind(Kinds.Measure, $@"^NOZZLE_{n}$"));
            model.Port(nozzle, "Closed", find.Bind(Kinds.Di, $@"^NOZZ(LE)?_{n}_CLOSED$"));
            model.Port(nozzle, "Opened", find.Bind(Kinds.Di, $@"^NOZZ(LE)?_{n}_OPEN(ED)?$"));
            consumers.Add($"@Nozzle{n}.Moving");
        }

        if (deflector)
        {
            var item = model.Add("Deflector", "Actuator", "Pelton deflector");
            if (item is not null)
            {
                item.Params["OpenTime"] = 5;
                item.Params["CloseTime"] = 5;
                item.Params["EsdCloseTime"] = 3;
                var command = find.Bind(Kinds.Ao, @"DEFLECTOR");
                if (command is not null)
                {
                    item.Options["Command"] = "AnalogPosition";
                    model.Port(item, "Setpoint", command);
                }

                model.Port(item, "OpenCmd", find.Bind(Kinds.Do, @"^DEF(LECTOR)?_1_OPEN$"));
                model.Port(item, "CloseCmd", find.Bind(Kinds.Do, @"^DEF(LECTOR)?_1_CLOSE$"));
                model.Port(item, "Esd", find.Bind(Kinds.Do, @"^DEF(LECTOR)?_1_ESD$"));
                model.Port(item, "PowerOn", find.Bind(Kinds.Do, @"^DEF(LECTOR)?_1_PS_ON$"));
                model.Port(item, "OilPressure", "@HPU.SystemPressure");
                model.Port(item, "Position", find.Bind(Kinds.Measure, @"DEFLECTOR"));
                consumers.Add("@Deflector.Moving");
            }
        }

        if (miv)
        {
            var item = model.Add("MIV", "Valve", "Main inlet valve");
            if (item is not null)
            {
                item.Options["Command"] = "Maintained";
                item.Options["Characteristic"] = "Spherical";
                item.Params["MinPressure"] = 20;
                item.Params["EqualisedPercent"] = 90;
                model.Port(item, "OpenCmd", find.Bind(Kinds.Do, @"^MIV_(ON|OPEN)(_CMD)?$"));
                model.Port(item, "CloseCmd", find.Bind(Kinds.Do, @"^MIV_(OFF|CLOSE)(_CMD)?$"));
                model.Port(item, "OilPressure", "@HPU.SystemPressure");
                model.Port(item, "UpstreamPressure", "@Penstock.Pressure");
                model.Port(item, "DownstreamPressure", "@TurbinePressure.Pressure");
                model.Port(item, "Open", find.Bind(Kinds.Di, @"^MIV_OPEN(ED)?$"));
                model.Port(item, "Closed", find.Bind(Kinds.Di, @"^MIV_CLOSED$"));
                model.Port(item, "Intermediate", find.Bind(Kinds.Di, @"^MIV_(INTERMEDIATE|85)"));
                consumers.Add("@MIV.Moving");
            }
        }

        if (find.Exists(Kinds.Do, @"^BYPASS_(ON|OPEN)"))
        {
            var item = model.Add("Bypass", "Valve", "Bypass valve");
            if (item is not null)
            {
                item.Options["Command"] = "Pulse";
                item.Params["OpenTime"] = 15;
                item.Params["CloseTime"] = 15;
                model.Port(item, "OpenCmd", find.Bind(Kinds.Do, @"^BYPASS_(ON|OPEN)(_CMD)?$"));
                model.Port(item, "CloseCmd", find.Bind(Kinds.Do, @"^BYPASS_(OFF|CLOSE)(_CMD)?$"));
                model.Port(item, "Open", find.Bind(Kinds.Di, @"^BYPASS_OPEN(ED)?$"));
                model.Port(item, "Closed", find.Bind(Kinds.Di, @"^BYPASS_CLOSED$"));
                model.Port(item, "OpenMoving", find.Bind(Kinds.Di, @"^BYPASS_CONT_OPEN"));
                model.Port(item, "CloseMoving", find.Bind(Kinds.Di, @"^BYPASS_CONT_CLOSE"));
            }
        }

        if (hpu is not null)
        {
            for (var index = 0; index < Math.Min(8, consumers.Count); index++)
            {
                model.Port(hpu, $"Consumer{index + 1}", consumers[index]);
            }
        }

        var penstock = model.Add("Penstock", "Penstock", "Penstock: level → pressure, friction loss, water hammer");
        if (penstock is not null)
        {
            model.Port(penstock, "Flow", "@Flow.Flow");
            model.Port(penstock, "Pressure", find.Bind(Kinds.Measure, @"^PENSTOCK_(WATER_)?PRESSURE"));
            model.Note("Penstock: link ReservoirLevel to the Common/Intake reservoir (e.g. @Common/Reservoir.Level) and set TurbineElevation; until then StaticHead is used.");
        }

        var turbinePressure = model.Add("TurbinePressure", "TurbinePressure", "Pressure after the MIV");
        if (turbinePressure is not null)
        {
            model.Port(turbinePressure, "PenstockPressure", "@Penstock.Pressure");
            if (model.Exists("MIV"))
            {
                model.Port(turbinePressure, "MivOpening", "@MIV.Opening");
            }

            if (model.Exists("Bypass"))
            {
                model.Port(turbinePressure, "BypassOpening", "@Bypass.Opening");
            }

            model.Port(turbinePressure, "TurbineFlow", "@Flow.Flow");
            model.Port(turbinePressure, "Pressure", find.BindAll(Kinds.Measure, @"^TURBINE_(WATER_)?PRESSURE"));
        }

        var flow = model.Add("Flow", "TurbineFlow", "Turbine flow from the nozzle openings (Q/Y)");
        if (flow is not null)
        {
            flow.Params["Nozzles"] = Math.Max(1, nozzles.Count);
            for (var index = 0; index < nozzles.Count; index++)
            {
                model.Port(flow, $"Opening{index + 1}", $"@Nozzle{nozzles[index]}.Position");
            }

            model.Port(flow, "Pressure", "@TurbinePressure.Pressure");
        }

        var rotor = model.Add("Rotor", "Rotor", "Turbine + generator speed");
        if (rotor is not null)
        {
            model.Port(rotor, "Flow", "@Flow.Flow");
            model.Port(rotor, "Pressure", "@TurbinePressure.Pressure");
            if (model.Exists("Deflector"))
            {
                model.Port(rotor, "Deflector", "@Deflector.Position");
            }

            model.Port(rotor, "BreakerClosed", "@GCB.Closed");
            model.Port(rotor, "Speed", find.BindAll(Kinds.Measure, @"^SPEED_\d$"));
            model.Port(rotor, "Overspeed", find.Bind(Kinds.Di, @"^OVERSPEED(_TRIP)?$"));
        }

        var excitation = model.Add("Excitation", "Excitation", "Excitation / AVR");
        if (excitation is not null)
        {
            model.Port(excitation, "Start", find.Bind(Kinds.Do, @"^EXC_(AVR_)?START$"));
            model.Port(excitation, "Stop", find.Bind(Kinds.Do, @"^EXC_(AVR_)?STOP$"));
            model.Port(excitation, "Raise", find.Bind(Kinds.Do, @"^EXC_(AVR_)?(HIGHER|RAISE)$"));
            model.Port(excitation, "Lower", find.Bind(Kinds.Do, @"^EXC_(AVR_)?LOWER$"));
            model.Port(excitation, "SpeedPercent", "@Rotor.SpeedPercent");
            model.Port(excitation, "BreakerClosed", "@GCB.Closed");
            model.Port(excitation, "Excited", find.Bind(Kinds.Di, @"^EXC_(AVR_)?(CONT(ACTOR)?_ON|ON|EXCITED)$"));
            model.Port(excitation, "FieldCurrent", find.Bind(Kinds.Measure, @"FIELD_CURRENT|EXCITATION_CURRENT"));
        }

        var gcb = model.Add("GCB", "Breaker", "Generator circuit breaker");
        if (gcb is not null)
        {
            gcb.Options["Permissive"] = "SyncOk";
            model.Port(gcb, "CloseCmd", find.Bind(Kinds.Do, @"^GCB_(ON|CLOSE)(_CMD)?$"));
            model.Port(gcb, "OpenCmd", find.Bind(Kinds.Do, @"^GCB_(OFF|OPEN)(_1|_CMD)?$"));
            model.Port(gcb, "UvCoil", find.Bind(Kinds.Do, @"^GCB_UV(_ON)?$"));
            model.Port(gcb, "SyncOk", "@Sync.SyncOk");
            model.Port(gcb, "Closed", find.Bind(Kinds.Di, @"^GCB_(ON|CLOSED)$"));
            model.Port(gcb, "Open", find.Bind(Kinds.Di, @"^GCB_(OFF|OPEN)$"));
        }

        var sync = model.Add("Sync", "Synchroniser", "Synchroniser / sync check");
        if (sync is not null)
        {
            model.Port(sync, "Enable", find.Bind(Kinds.Do, @"^SYNC_(ON|START)$"));
            model.Port(sync, "GeneratorFrequency", "@Rotor.Frequency");
            model.Port(sync, "GeneratorVoltage", "@Excitation.Voltage");
            model.Port(sync, "BreakerClosed", "@GCB.Closed");
        }

        var generator = model.Add("Generator", "Generator", "Generator electrical values");
        if (generator is not null)
        {
            model.Port(generator, "ShaftPower", "@Rotor.ShaftPower");
            model.Port(generator, "ReactivePower", "@Excitation.ReactivePower");
            model.Port(generator, "Voltage", "@Excitation.Voltage");
            model.Port(generator, "Frequency", "@Rotor.Frequency");
            model.Port(generator, "BreakerClosed", "@GCB.Closed");
            model.Port(generator, "P", find.Bind(Kinds.Measure, @"^(P|ACTIVE_POWER|GEN_P)$"));
            model.Port(generator, "Q", find.Bind(Kinds.Measure, @"^(Q|REACTIVE_POWER|GEN_Q)$"));
            model.Port(generator, "I", find.Bind(Kinds.Measure, @"^(I|CURRENT|GEN_I)$"));
            model.Port(generator, "U12", find.Bind(Kinds.Measure, @"^(U(_LL)?|VOLTAGE|GEN_U)$"));
            model.Port(generator, "FGen", find.Bind(Kinds.Measure, @"^(F_?GEN|GEN_F|FREQUENCY)$"));
        }

        foreach (var phase in new[] { "U", "V", "W" })
        {
            var signal = find.Bind(Kinds.Measure, $@"^WINDING_{phase}_TEMP");
            if (signal is null)
            {
                continue;
            }

            var item = model.Add($"Winding{phase}", "Temperature", $"Stator winding {phase} temperature");
            if (item is not null)
            {
                item.Params["DriverRated"] = 1925;
                item.Params["NominalTemp"] = 90;
                model.Port(item, "Driver", "@Generator.I");
                model.Port(item, "Temperature", signal);
            }
        }

        for (var n = 1; n <= 6; n++)
        {
            var signal = find.Bind(Kinds.Measure, $@"^BEARING_TEMPERATURE_{n}$");
            if (signal is null)
            {
                continue;
            }

            var item = model.Add($"Bearing{n}", "Temperature", $"Bearing {n} temperature");
            if (item is not null)
            {
                item.Params["DriverRated"] = 100;
                item.Params["NominalTemp"] = 55;
                item.Params["TimeConstant"] = 2400;
                model.Port(item, "Driver", "@Rotor.SpeedPercent");
                model.Port(item, "Temperature", signal);
            }
        }

        for (var n = 1; n <= 4; n++)
        {
            var signal = find.Bind(Kinds.Measure, $@"^VIBRATION_{n}$");
            if (signal is null)
            {
                continue;
            }

            var item = model.Add($"Vibration{n}", "Vibration", $"Vibration {n}");
            if (item is not null)
            {
                model.Port(item, "SpeedPercent", "@Rotor.SpeedPercent");
                model.Port(item, "LoadPercent", "@Generator.LoadPercent");
                model.Port(item, "Vibration", signal);
            }
        }
    }

    /// <summary>
    /// Devices DB: the generator values go to the generator-side devices (GEN_SENTRON, GEN_PROT); P/Q/S/PF/f also to the
    /// line and transformer protection; the auxiliary supply object feeds the AUX Sentron.
    /// </summary>
    private static void Devices(Finder find, Builder model)
    {
        var map = new (string Port, string Pattern)[]
        {
            ("I1", @"^I_?1(_S\d)?$"), ("I2", @"^I_?2(_S\d)?$"), ("I3", @"^I_?3(_S\d)?$"), ("I", @"^I$"),
            ("U12", @"^U_?12$"), ("U23", @"^U_?23$"), ("U31", @"^U_?(13|31)$"), ("U12", @"^U_LL$"),
            ("U1", @"^U_?1$"), ("U2", @"^U_?2$"), ("U3", @"^U_?3$"),
            ("FGen", @"^F(_GEN)?$"), ("FGrid", @"^F_GRID$"), ("GridU", @"^U_GRID$"),
            ("PF", @"^PF$"), ("P", @"^P$"), ("Q", @"^Q$"), ("S", @"^S$"),
            ("EnergyExport", @"^E_ACT_EXP$"), ("EnergyImport", @"^E_ACT_IMP$"), ("EnergyReactive", @"^E_RACT_EXP$")
        };
        if (model.Get("Generator") is { } generator)
        {
            foreach (var (port, pattern) in map)
            {
                model.AddTarget(generator, port, find.BindAllIn(Kinds.Dev, pattern, @"^GEN"));
            }

            foreach (var (port, pattern) in map.Where(item => item.Port is "P" or "Q" or "S" or "PF" or "FGen"))
            {
                model.AddTarget(generator, port, find.BindAllIn(Kinds.Dev, pattern, @"^(LINE|TRAFO)"));
            }
        }

        if (find.Exists(Kinds.Dev, @"^(U_LL|P)$") && find.BindAllIn(Kinds.Dev, @"^U_LL$", @"^AUX", peek: true) is not null)
        {
            var aux = model.Add("AuxSupply", "AuxSupply", "Station service (AUX Sentron)");
            if (aux is not null)
            {
                model.AddTarget(aux, "U", find.BindAllIn(Kinds.Dev, @"^U_?(12|13|23|LL)$", @"^AUX"));
                model.AddTarget(aux, "I", find.BindAllIn(Kinds.Dev, @"^I(_?[123])?$", @"^AUX"));
                foreach (var name in new[] { "P", "Q", "S", "PF" })
                {
                    model.AddTarget(aux, name, find.BindAllIn(Kinds.Dev, $"^{name}$", @"^AUX"));
                }

                model.AddTarget(aux, "F", find.BindAllIn(Kinds.Dev, @"^F$", @"^AUX"));
                model.AddTarget(aux, "Energy", find.BindAllIn(Kinds.Dev, @"^E_ACT_IMP$", @"^AUX"));
            }
        }
    }

    /// <summary>
    /// Remaining feedback contacts: contactor / running feedbacks echo their command; MCB / OK / ready / healthy /
    /// supply / AUTO contacts are TRUE (healthy plant). Alarms, trips and LOCAL positions stay FALSE (not written).
    /// </summary>
    private static void Feedbacks(Finder find, Builder model)
    {
        var echoes = 0;
        foreach (var di in find.Remaining(Kinds.Di).ToList())
        {
            var match = Regex.Match(di.PlcName, @"^(?<base>.+?)_(CONT(ACTOR)?_ON|RUNNING|ON_FB|FEEDBACK)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["base"].Value;
            var command = find.One(Kinds.Do, $@"^{Regex.Escape(name)}(_ON|_CMD|_START)?$");
            if (command is null)
            {
                continue;
            }

            var item = model.Add($"Echo_{name}", "Logic", $"{di.PlcName} follows {command.PlcName}");
            if (item is null)
            {
                continue;
            }

            item.Options["Function"] = "Echo";
            item.Params["OnDelay"] = 0.3;
            item.Params["OffDelay"] = 0.2;
            model.Port(item, "In1", $"{command.Kind}:{command.Id}");
            model.Port(item, "Out", find.Take(di));
            echoes++;
        }

        var healthy = find.Remaining(Kinds.Di)
            .Where(di => Regex.IsMatch(di.PlcName, @"(MCB_(ON|OK)|_OK$|_OK_|HEALT|READY|SAFETY_SWITCH_ON|VOLTAGE_OK|SUPPLY_(OK|ON)|_AUTO$|MAIN_CB_ON|PS_ON$)", RegexOptions.IgnoreCase)
                         && !Regex.IsMatch(di.PlcName, @"(NOT_OK|FAULT|TRIP$|ALARM$)", RegexOptions.IgnoreCase))
            .ToList();
        if (healthy.Count > 0)
        {
            var item = model.Add("HealthyContacts", "Logic", "Contacts that are TRUE in a healthy plant (MCB ON, OK, READY, AUTO …)");
            if (item is not null)
            {
                item.Options["Function"] = "True";
                model.Port(item, "Out", string.Join(", ", healthy.Select(find.Take)));
                model.Note($"HealthyContacts: {healthy.Count} DI set TRUE ({string.Join(", ", healthy.Take(8).Select(di => di.PlcName))}{(healthy.Count > 8 ? ", …" : "")}).");
            }
        }

        if (echoes > 0)
        {
            model.Note($"{echoes} contactor/running feedback(s) echo their command.");
        }
    }

    /// <summary>Places new objects on the SCADA canvas in rows by category, below the existing objects.</summary>
    private static void Layout(List<ObjectConfig> objects, ProcessModel existing)
    {
        var top = existing.Objects.Count == 0 ? 20 : existing.Objects.Max(item => item.Y) + 170;
        var categories = new[] { "Waterway", "Hydraulics", "Turbine", "Electrical", "Generator", "General" };
        var row = 0;
        foreach (var category in categories)
        {
            var items = objects.Where(item => ObjectCatalog.Find(item.Type)?.Category == category).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            for (var index = 0; index < items.Count; index++)
            {
                items[index].X = 20 + (index % 7) * 200;
                items[index].Y = top + (row + index / 7) * 150;
            }

            row += (items.Count + 6) / 7;
        }
    }

    private static class Kinds
    {
        public static readonly SimulatorSignalKind[] Di = [SimulatorSignalKind.DI];
        public static readonly SimulatorSignalKind[] Do = [SimulatorSignalKind.DO];
        public static readonly SimulatorSignalKind[] Ao = [SimulatorSignalKind.AO];
        public static readonly SimulatorSignalKind[] Measure = [SimulatorSignalKind.AI, SimulatorSignalKind.RTD, SimulatorSignalKind.COUNTER];
        public static readonly SimulatorSignalKind[] Dev = [SimulatorSignalKind.DEV];
    }

    private sealed class Finder(List<SimulatorSignal> signals)
    {
        private readonly HashSet<SimulatorSignal> _used = [];

        public bool Exists(SimulatorSignalKind[] kinds, string pattern) => Matches(kinds, pattern).Any();

        public SimulatorSignal? One(SimulatorSignalKind[] kinds, string pattern) => Matches(kinds, pattern).FirstOrDefault();

        /// <summary>First unused matching signal as "KIND:ID" (inputs may share a DO, so DO/AO are not consumed).</summary>
        public string? Bind(SimulatorSignalKind[] kinds, string pattern)
        {
            var signal = Matches(kinds, pattern).FirstOrDefault(item => !_used.Contains(item));
            return signal is null ? null : Take(signal);
        }

        /// <summary>All unused matching signals, comma-separated (an output may drive several, e.g. SPEED_1 and SPEED_2).</summary>
        public string? BindAll(SimulatorSignalKind[] kinds, string pattern)
        {
            var list = Matches(kinds, pattern).Where(item => !_used.Contains(item)).ToList();
            return list.Count == 0 ? null : string.Join(", ", list.Select(Take));
        }

        /// <summary>All unused matching signals of devices whose name matches <paramref name="devicePattern"/>.</summary>
        public string? BindAllIn(SimulatorSignalKind[] kinds, string pattern, string devicePattern, bool peek = false)
        {
            var list = Matches(kinds, pattern)
                .Where(item => !_used.Contains(item) && Regex.IsMatch(item.System, devicePattern, RegexOptions.IgnoreCase))
                .ToList();
            if (list.Count == 0)
            {
                return null;
            }

            return peek ? "" : string.Join(", ", list.Select(Take));
        }

        public string Take(SimulatorSignal signal)
        {
            if (signal.IsWritable)
            {
                _used.Add(signal);
            }

            return $"{signal.Kind}:{signal.Id}";
        }

        public IEnumerable<SimulatorSignal> Remaining(SimulatorSignalKind[] kinds) =>
            signals.Where(signal => kinds.Contains(signal.Kind) && !_used.Contains(signal));

        private IEnumerable<SimulatorSignal> Matches(SimulatorSignalKind[] kinds, string pattern) =>
            signals.Where(signal => kinds.Contains(signal.Kind) && Regex.IsMatch(signal.PlcName, pattern, RegexOptions.IgnoreCase))
                .OrderBy(signal => signal.PlcName.Length);
    }

    private sealed class Builder(Result result, ProcessModel existing)
    {
        public ObjectConfig? Add(string name, string type, string description)
        {
            if (existing.Find(name) is not null || Exists(name))
            {
                result.Report.Add($"{name}: already in the model, not changed.");
                return null;
            }

            var item = new ObjectConfig { Name = name, Type = type, Description = description };
            result.Objects.Add(item);
            return item;
        }

        public ObjectConfig? Get(string name) => result.Objects.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds signal targets to an output port (an output may drive several signals).</summary>
        public void AddTarget(ObjectConfig item, string port, string? binding)
        {
            if (string.IsNullOrWhiteSpace(binding))
            {
                return;
            }

            item.Ports[port] = item.Ports.TryGetValue(port, out var current) && current.Length > 0 ? current + ", " + binding : binding;
            result.Report.Add($"{item.Name}.{port} += {binding}");
        }

        public bool Exists(string name) =>
            existing.Find(name) is not null || result.Objects.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public void Port(ObjectConfig item, string port, string? binding)
        {
            if (string.IsNullOrWhiteSpace(binding))
            {
                return;
            }

            item.Ports[port] = binding;
            if (!binding.StartsWith('@') && !binding.StartsWith('='))
            {
                result.Report.Add($"{item.Name}.{port} = {binding}");
            }
        }

        public void Note(string text) => result.Report.Add(text);
    }

    internal static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
