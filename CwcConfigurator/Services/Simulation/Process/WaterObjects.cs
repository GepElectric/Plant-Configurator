using static CwcConfigurator.Services.Simulation.Process.Def;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Reservoir / forebay (usually on the Common or Intake PLC). Level from the water balance: river inflow in;
/// the turbine flow of every unit, the intake gates / flaps and the spillway out. Gates and flaps pass much more
/// water than the turbines, depending on the water depth (∝ √depth).
/// </summary>
public sealed class ReservoirObject : SimObject
{
    private const int Units = 8;
    private const int Gates = 4;
    private double _depth;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Reservoir", "Reservoir / intake level", "Waterway",
        "Water level from the river inflow, the flow of every unit, the intake gates/flaps and the spillway.",
        [
            Option("Mode", "Balance: level follows the water balance. Constant: level stays at the initial value.", "Balance", "Constant")
        ],
        [
            Param("Area", 2000, "m²", "Water surface area of the reservoir / forebay."),
            Param("BottomElevation", 0, "m", "Elevation of the reservoir bottom (Level = BottomElevation + depth)."),
            Param("InitialDepth", 4, "m", "Water depth after reset."),
            Param("MaxDepth", 6, "m", "Depth at which the reservoir is full."),
            Param("RiverInflow", 0.8, "m³/s", "River inflow when the RiverInflow input is not connected."),
            Param("SpillCrestDepth", 5, "m", "Depth of the spillway crest (0 = no spillway)."),
            Param("SpillWidth", 10, "m", "Spillway width (Q = 1.7·width·h^1.5)."),
            .. Enumerable.Range(1, Gates).Select(i => Param($"Gate{i}Capacity", 20, "m³/s", $"Gate/flap {i}: discharge fully open at the reference depth.")),
            Param("GateReferenceDepth", 4, "m", "Depth at which the gate capacities apply."),
            Param("LowLevelDepth", 2, "m", "Level low switch (depth)."),
            Param("HighLevelDepth", 5.2, "m", "Level high switch (depth).")
        ],
        [
            AnalogIn("RiverInflow", "m³/s", "River inflow (link or AO). Not connected = parameter RiverInflow."),
            .. Numbered(Units, i => AnalogIn($"Unit{i}Flow", "m³/s", $"Turbine flow of unit {i} (link, e.g. @Unit {i}/Flow.Flow).")),
            .. Numbered(Gates, i => AnalogIn($"Gate{i}Opening", "%", $"Opening of intake gate / flap {i} (link from a valve or actuator).")),
            AnalogIn("OtherOutflow", "m³/s", "Any other outflow."),
            AnalogOut("Level", "m", "Water level (BottomElevation + depth)."),
            AnalogOut("Depth", "m", "Water depth."),
            AnalogOut("Inflow", "m³/s", "Total inflow."),
            AnalogOut("TurbineFlow", "m³/s", "Sum of the unit flows."),
            AnalogOut("GateFlow", "m³/s", "Discharge through the gates / flaps."),
            AnalogOut("Spill", "m³/s", "Spillway discharge."),
            BoolOut("LevelLow", "Level low."),
            BoolOut("LevelHigh", "Level high.")
        ],
        [],
        ["Level", "Inflow", "TurbineFlow", "GateFlow"],
        () => new ReservoirObject());

    public override void Reset()
    {
        _depth = P("InitialDepth");
        Publish(Connected("RiverInflow") ? In("RiverInflow") : P("RiverInflow"), 0, 0, 0);
    }

    public override void Step(double dt)
    {
        var inflow = Connected("RiverInflow") ? In("RiverInflow") : P("RiverInflow");
        var turbines = Enumerable.Range(1, Units).Sum(i => Math.Max(0, In($"Unit{i}Flow")));
        var depthFactor = Math.Sqrt(Math.Max(0, _depth) / Math.Max(0.1, P("GateReferenceDepth")));
        var gates = Enumerable.Range(1, Gates).Sum(i => P($"Gate{i}Capacity") * SimMath.Clamp(In($"Gate{i}Opening"), 0, 100) / 100) * depthFactor;
        var crest = P("SpillCrestDepth");
        var spill = crest > 0 && _depth > crest ? 1.7 * P("SpillWidth") * Math.Pow(_depth - crest, 1.5) : 0;
        if (OptIs("Mode", "Balance"))
        {
            var outflow = turbines + gates + spill + Math.Max(0, In("OtherOutflow"));
            _depth = SimMath.Clamp(_depth + (inflow - outflow) / Math.Max(1, P("Area")) * dt, 0, P("MaxDepth"));
        }

        Publish(inflow, turbines, gates, spill);
    }

    private void Publish(double inflow, double turbines, double gates, double spill)
    {
        Out("Level", P("BottomElevation") + _depth);
        Out("Depth", _depth);
        Out("Inflow", inflow);
        Out("TurbineFlow", turbines);
        Out("GateFlow", gates);
        Out("Spill", spill);
        Out("LevelLow", _depth <= P("LowLevelDepth"));
        Out("LevelHigh", _depth >= P("HighLevelDepth"));
    }
}

/// <summary>
/// Penstock: pressure at the turbine inlet from the reservoir level, minus the friction loss (∝ Q²), plus the
/// water hammer of the rigid water column (Δp = ρ·L/A·dQ/dt) when the flow changes quickly.
/// </summary>
public sealed class PenstockObject : SimObject
{
    private double _flowRate;
    private double _lastFlow;
    private bool _first = true;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "Penstock", "Penstock", "Waterway",
        "Static pressure from the water level, friction loss and water hammer.",
        [],
        [
            Param("TurbineElevation", 0, "m", "Elevation of the pressure measurement at the turbine."),
            Param("StaticHead", 160, "m", "Gross head used when ReservoirLevel is not connected."),
            Param("LossAtRatedFlow", 0.5, "bar", "Friction loss at the rated flow."),
            Param("RatedFlow", 1.45, "m³/s", "Rated flow of the penstock."),
            Param("Length", 500, "m", "Penstock length (water hammer)."),
            Param("Diameter", 0.8, "m", "Penstock diameter (water hammer)."),
            Param("HammerTau", 0.3, "s", "Smoothing of the flow change for the water hammer.")
        ],
        [
            AnalogIn("ReservoirLevel", "m", "Water level (link, e.g. @Common/Reservoir.Level). Not connected = StaticHead."),
            AnalogIn("Flow", "m³/s", "Flow through the penstock (link from the turbine flow)."),
            AnalogOut("Pressure", "bar", "Pressure at the turbine inlet (before the MIV)."),
            AnalogOut("GrossHead", "m", "Level minus the turbine elevation."),
            AnalogOut("NetHead", "m", "Head after friction loss and water hammer.")
        ],
        [],
        ["Pressure", "NetHead"],
        () => new PenstockObject());

    public override void Reset()
    {
        _flowRate = 0;
        _first = true;
    }

    public override void Step(double dt)
    {
        var flow = Math.Max(0, In("Flow"));
        if (_first)
        {
            _lastFlow = flow;
            _first = false;
        }

        _flowRate = SimMath.Lag(_flowRate, dt > 0 ? (flow - _lastFlow) / dt : 0, P("HammerTau"), dt);
        _lastFlow = flow;
        var gross = Connected("ReservoirLevel") ? In("ReservoirLevel") - P("TurbineElevation") : P("StaticHead");
        var rated = Math.Max(0.01, P("RatedFlow"));
        var lossMeters = SimMath.BarToMeters(P("LossAtRatedFlow")) * Math.Pow(flow / rated, 2);
        var area = Math.PI * Math.Pow(Math.Max(0.05, P("Diameter")), 2) / 4;
        var hammerMeters = -P("Length") / (SimMath.G * area) * _flowRate;
        var net = Math.Max(0, gross - lossMeters + hammerMeters);
        Out("GrossHead", gross);
        Out("NetHead", net);
        Out("Pressure", SimMath.MetersToBar(net));
    }
}

/// <summary>
/// Pressure in the turbine manifold after the MIV. It fills through the MIV and the bypass and empties through
/// the nozzles and leakage, approaching the balance pressure exponentially (not linearly).
/// </summary>
public sealed class TurbinePressureObject : SimObject
{
    private double _pressure;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "TurbinePressure", "Turbine pressure (after MIV)", "Waterway",
        "Pressure between the MIV and the nozzles / wicket gate; used for pressure equalisation before the MIV opens.",
        [],
        [
            Param("FillTimeMiv", 1, "s", "Time constant of filling through a fully open MIV."),
            Param("FillTimeBypass", 15, "s", "Time constant of filling through a fully open bypass."),
            Param("DrainTime", 2, "s", "Time constant of emptying through the turbine at rated flow."),
            Param("LeakTime", 300, "s", "Time constant of the leakage with everything closed."),
            Param("RatedFlow", 1.45, "m³/s", "Turbine rated flow."),
            Param("EqualisedPercent", 95, "%", "Pressure equalised when the turbine pressure reaches this % of the penstock pressure.")
        ],
        [
            AnalogIn("PenstockPressure", "bar", "Pressure before the MIV (link from the penstock)."),
            AnalogIn("MivOpening", "%", "MIV flow opening (link from the MIV valve)."),
            AnalogIn("BypassOpening", "%", "Bypass flow opening (link from the bypass valve)."),
            AnalogIn("TurbineFlow", "m³/s", "Flow taken by the turbine (link from the turbine flow)."),
            AnalogOut("Pressure", "bar", "Turbine pressure (after the MIV)."),
            AnalogOut("DeltaP", "bar", "Penstock pressure minus turbine pressure."),
            BoolOut("Equalised", "Pressure equalised across the MIV.")
        ],
        [],
        ["Pressure", "DeltaP"],
        () => new TurbinePressureObject());

    public override void Reset() => _pressure = 0;

    public override void Step(double dt)
    {
        var upstream = Math.Max(0, In("PenstockPressure"));
        var fill = SimMath.Clamp(In("MivOpening"), 0, 100) / 100 / Math.Max(0.05, P("FillTimeMiv"))
                   + SimMath.Clamp(In("BypassOpening"), 0, 100) / 100 / Math.Max(0.05, P("FillTimeBypass"));
        var drain = 1 / Math.Max(1, P("LeakTime"))
                    + Math.Max(0, In("TurbineFlow")) / Math.Max(0.01, P("RatedFlow")) / Math.Max(0.05, P("DrainTime"));
        var balance = fill + drain > 0 ? upstream * fill / (fill + drain) : 0;
        _pressure = SimMath.Lag(_pressure, balance, 1 / (fill + drain), dt);
        Out("Pressure", _pressure);
        Out("DeltaP", upstream - _pressure);
        Out("Equalised", upstream > 0.1 && _pressure >= upstream * P("EqualisedPercent") / 100);
    }
}

/// <summary>
/// Turbine flow. Pelton: each nozzle passes flow along the nozzle Q/Y characteristic (strongly non-linear: most
/// of the flow comes in the first half of the stroke), scaled with √(pressure / rated pressure).
/// Gate: one opening (wicket gate) through the same kind of characteristic.
/// </summary>
public sealed class TurbineFlowObject : SimObject
{
    private const int Nozzles = 6;

    public static ObjectTypeInfo TypeInfo { get; } = new(
        "TurbineFlow", "Turbine flow (Q/Y)", "Turbine",
        "Flow from the nozzle or wicket-gate openings through the Q/Y characteristic and the pressure.",
        [
            Option("Type", "Pelton: sum of the nozzles. Gate: one opening (Francis/Kaplan wicket gate, cross-flow).", "Pelton", "Gate")
        ],
        [
            Param("RatedFlow", 1.45, "m³/s", "Flow with all nozzles (or the gate) fully open at the rated pressure."),
            Param("RatedPressure", 16.4, "bar", "Pressure at which RatedFlow applies."),
            Param("Nozzles", 6, "", "Number of nozzles (Pelton).")
        ],
        [
            .. Numbered(Nozzles, i => AnalogIn($"Opening{i}", "%", i == 1 ? "Opening of nozzle 1 or of the gate (link from an actuator)." : $"Opening of nozzle {i}.")),
            AnalogIn("Pressure", "bar", "Pressure at the nozzles (link from the turbine pressure). Not connected = rated pressure."),
            AnalogOut("Flow", "m³/s", "Turbine flow."),
            AnalogOut("FlowPercent", "%", "Flow in % of the rated flow."),
            .. Numbered(Nozzles, i => AnalogOut($"Flow{i}", "m³/s", $"Flow of nozzle {i}."))
        ],
        [
            new TableDef("QY", "Opening %", "Flow %",
                [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100],
                [0, 8.21, 16.1, 23.7, 30.9, 37.8, 44.4, 50.6, 56.4, 61.9, 67.1, 71.9, 76.4, 80.5, 84.3, 87.7, 90.8, 93.6, 96.0, 98.1, 100],
                "Flow of one nozzle (or the gate) in % of its full flow versus the opening.")
        ],
        ["Flow", "FlowPercent"],
        () => new TurbineFlowObject());

    public override void Step(double dt)
    {
        var (x, y) = Table("QY");
        var rated = Math.Max(0, P("RatedFlow"));
        var pressure = Connected("Pressure") ? Math.Max(0, In("Pressure")) : P("RatedPressure");
        var headFactor = Math.Sqrt(pressure / Math.Max(0.01, P("RatedPressure")));
        var pelton = OptIs("Type", "Pelton");
        var count = pelton ? (int)SimMath.Clamp(Math.Round(P("Nozzles")), 1, Nozzles) : 1;
        var total = 0.0;
        for (var nozzle = 1; nozzle <= Nozzles; nozzle++)
        {
            var flow = nozzle <= count
                ? rated / count * SimMath.Interpolate(x, y, SimMath.Clamp(In($"Opening{nozzle}"), 0, 100)) / 100 * headFactor
                : 0;
            Out($"Flow{nozzle}", flow);
            total += flow;
        }

        Out("Flow", total);
        Out("FlowPercent", rated > 0 ? total / rated * 100 : 0);
    }
}
