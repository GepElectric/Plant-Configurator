using System.Globalization;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.PidTuning;

/// <summary>Kind of process model fitted to a recording.</summary>
public enum ProcessKind
{
    /// <summary>Self-regulating: y = K·u(t−L) through a first-order lag T (power, pressure, opening, excitation).</summary>
    FirstOrder,

    /// <summary>Integrating: dy/dt = Ki·u(t−L) (reservoir level, servo position driven by a valve).</summary>
    Integrating
}

public enum ControllerKind
{
    P,
    PI
}

/// <summary>
/// How a kind of measurement is tuned: its process model, controller, the closed-loop time aimed at (λ / τc) and
/// how to excite it. Tuning depends on the measurement — a reservoir level is not tuned like a nozzle position.
/// </summary>
public sealed record LoopProfile(
    string Key,
    string Title,
    ProcessKind Process,
    ControllerKind Controller,
    double MinClosedLoopTime,
    double StepPercent,
    double RecordSeconds,
    string Hint)
{
    public override string ToString() => Title;
}

/// <summary>Fitted process model (all values in % of the measurement and % of the controller output).</summary>
public sealed record ProcessModel(ProcessKind Kind, double Gain, double TimeConstant, double DeadTime, double Fit, double U0, double Y0)
{
    public string Describe() => Kind == ProcessKind.Integrating
        ? $"integrating: Ki = {Gain:0.#####} %/s per % output, dead time L = {DeadTime:0.##} s, fit R² = {Fit:0.000}"
        : $"first order: K = {Gain:0.###} %/%, T = {TimeConstant:0.##} s, dead time L = {DeadTime:0.##} s, fit R² = {Fit:0.000}";
}

/// <summary>Proposed controller settings in the form of the PLC PID (GAIN multiplies the P, I and D parts).</summary>
public sealed record PidProposal(double Gain, double Ti, double Td, bool PAction, bool IAction, bool DAction, double ClosedLoopTime);

/// <summary>Current settings of one PID of the project (PID_* structure of DB PAR).</summary>
public sealed class PidController
{
    public string Structure { get; init; } = "";
    public string Description { get; init; } = "";
    public Dictionary<string, DbStringRow> Rows { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public double Gain => PidTuner.Real(Rows.GetValueOrDefault("GAIN"));
    public double Ti => PidTuner.Seconds(Rows.GetValueOrDefault("TI")?.TimeValue);
    public double Td => PidTuner.Seconds(Rows.GetValueOrDefault("TD")?.TimeValue);
    public bool PAction => PidTuner.Flag(Rows.GetValueOrDefault("P_SEL"));
    public bool IAction => PidTuner.Flag(Rows.GetValueOrDefault("I_SEL"));
    public bool DAction => PidTuner.Flag(Rows.GetValueOrDefault("D_SEL"));
    public override string ToString() => string.IsNullOrWhiteSpace(Description) ? Structure : $"{Structure} — {Description}";
}

public static class PidTuner
{
    public static readonly IReadOnlyList<LoopProfile> Profiles =
    [
        new("LEVEL", "Water level (integrating, PI, slow)", ProcessKind.Integrating, ControllerKind.PI, 180, 5, 900,
            "Reservoir / intake level: integrating process. Put the controller in manual, change the output by about 5 % and record until the level changes clearly (several minutes); or make a reference step in automatic."),
        new("SERVO", "Servo position, hydraulic (nozzle, deflector, guide vanes, runner — P)", ProcessKind.Integrating, ControllerKind.P, 0.5, 5, 30,
            "Position loop of a hydraulic actuator: the cylinder integrates the valve signal, so a P controller is used. Make a reference step of about 5 % (or an output step in manual) and record until the position is steady."),
        new("GATE", "Intake gate / flap position (motor or VFD — P, slow)", ProcessKind.Integrating, ControllerKind.P, 5, 5, 180,
            "Gate driven by a motor or frequency converter: the position integrates the drive speed, so a P controller is used; the dead band (DEAD_W) keeps the drive from hunting. Make a reference step of about 5 % and record until the gate stops."),
        new("POWER", "Active power (first order, PI)", ProcessKind.FirstOrder, ControllerKind.PI, 10, 5, 180,
            "Unit on the grid. Change the opening / output by about 5 % and record until the power is steady. Water hammer makes the power move the wrong way first — it is taken into the dead time."),
        new("SPEED", "Speed (first order, PI)", ProcessKind.FirstOrder, ControllerKind.PI, 2, 3, 60,
            "No-load speed. Make a small output step (2–3 %) and record until the speed is steady again."),
        new("FREQUENCY", "Frequency (first order, PI)", ProcessKind.FirstOrder, ControllerKind.PI, 2, 3, 60,
            "Frequency control (island / isolated grid or no load). Make a small output step (2–3 %) and record until the frequency is steady. On a stiff grid the frequency does not follow the unit — tune it in island or no-load operation."),
        new("VOLTAGE", "Generator voltage (first order, PI, fast)", ProcessKind.FirstOrder, ControllerKind.PI, 0.5, 2, 20,
            "Generator at no load with excitation. Make a small voltage reference step (1–2 %) and record until the voltage is steady."),
        new("REACTIVE", "Reactive power / power factor (first order, PI)", ProcessKind.FirstOrder, ControllerKind.PI, 2, 2, 60,
            "Unit on the grid. Make a small reference step of reactive power / power factor (or output step) and record until steady."),
        new("PRESSURE", "Water pressure (first order, PI)", ProcessKind.FirstOrder, ControllerKind.PI, 5, 5, 120,
            "Change the output by about 5 % and record until the pressure is steady."),
        new("OPENING", "Opening (first order, PI)", ProcessKind.FirstOrder, ControllerKind.PI, 2, 5, 60,
            "Change the output by about 5 % and record until the opening is steady."),
        new("GENERAL", "General (first order, PI)", ProcessKind.FirstOrder, ControllerKind.PI, 2, 5, 120,
            "Change the output by a few % (manual) or the reference (automatic) and record until the measurement is steady.")
    ];

    /// <summary>The profile of a controller by its name (PID_WATER_LEVEL → level, PID_PELTON_NOZZLE_1 → servo…).</summary>
    public static LoopProfile ProfileFor(string structure)
    {
        var name = (structure ?? "").ToUpperInvariant();
        string key =
            name.Contains("LEVEL") ? "LEVEL"
            : name.Contains("WICKET") ? "SERVO"
            : name.Contains("GATE") || name.Contains("FLAP") ? "GATE"
            : new[] { "NOZZLE", "DEFLECTOR", "DISTRIBUTOR", "RUNNER", "CROSS_FLOW", "VALVE", "SERVO" }.Any(name.Contains) ? "SERVO"
            : name.Contains("REACTIVE") || name.Contains("POWER_FACTOR") || name.Contains("COS") ? "REACTIVE"
            : name.Contains("VOLTAGE") || name.Contains("EXC") ? "VOLTAGE"
            : name.Contains("POWER") ? "POWER"
            : name.Contains("FREQ") ? "FREQUENCY"
            : name.Contains("SPEED") ? "SPEED"
            : name.Contains("PRESSURE") ? "PRESSURE"
            : name.Contains("OPEN") ? "OPENING"
            : "GENERAL";
        return Profiles.First(profile => profile.Key == key);
    }

    /// <summary>The PID controllers of a PLC database (parameter structures PID_*).</summary>
    public static List<PidController> Controllers(DbCatalog catalog) => catalog.ParameterRows
        .Where(row => row.PlcStructure.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
        .GroupBy(row => row.PlcStructure, StringComparer.OrdinalIgnoreCase)
        .Select(group => new PidController
        {
            Structure = group.Key,
            Description = Describe(group.FirstOrDefault(row => row.PlcName.Equals("GAIN", StringComparison.OrdinalIgnoreCase))?.NameEnglish ?? ""),
            Rows = group.GroupBy(row => row.PlcName, StringComparer.OrdinalIgnoreCase).ToDictionary(item => item.Key, item => item.First(), StringComparer.OrdinalIgnoreCase)
        })
        .Where(controller => controller.Rows.ContainsKey("GAIN"))
        .OrderBy(controller => controller.Rows["GAIN"].Id)
        .ToList();

    private static string Describe(string gainName) => Regex.Replace(gainName, @"\s*PID\s+proportional gain\s*$", "", RegexOptions.IgnoreCase).Trim();

    // ---------------------------------------------------------------- REG_INTF

    /// <summary>REG_INTF (DB31): DT_REGULATOR_INTERFACE elements of 26 B; VALUES at +14: SCALED_REFERENCE, OUT, SCALED_MEAS (REAL, %).</summary>
    public const int RegInterfaceDb = 31;
    public const int RegValues = 14;

    public static readonly IReadOnlyList<(string Element, int Offset)> RegInterface =
    [
        ("WATER_LEVEL", 0), ("WATER_PRESSURE", 26), ("ACTIVE_POWER", 52), ("OPENNESS", 78), ("SPEED", 104),
        ("KAPLAN_DISTRIBUTOR", 130), ("KAPLAN_RUNNER", 156), ("FRANCIS_DISTRIBUTOR", 182), ("CROSS_FLOW_VALVE_1", 208), ("CROSS_FLOW_VALVE_2", 234),
        .. Enumerable.Range(1, 6).Select(i => ($"PELTON_DEFLECTOR[{i}]", 260 + (i - 1) * 26)),
        .. Enumerable.Range(1, 6).Select(i => ($"PELTON_NOZZLE[{i}]", 416 + (i - 1) * 26)),
        ("EXC_VOLTAGE", 572), ("EXC_REACTIVE_POWER", 598), ("EXC_POWER_FACTOR", 624)
    ];

    /// <summary>MEAS (indirect DB1003): DT_MEASUREMENT_SOLO of 56 B, VAL (REAL) at +44.</summary>
    public const int MeasDb = 1003, MeasSize = 56, MeasValue = 44;

    /// <summary>"MEAS#12" / "MEAS 12" → DB1003.716; "8.20" → DB8.20; null when not readable.</summary>
    public static (int Db, int Offset)? ParseAddress(string text)
    {
        var value = (text ?? "").Trim();
        var meas = Regex.Match(value, @"^MEAS\s*#?\s*(\d+)$", RegexOptions.IgnoreCase);
        if (meas.Success)
        {
            return (MeasDb, int.Parse(meas.Groups[1].Value, CultureInfo.InvariantCulture) * MeasSize + MeasValue);
        }

        var parts = value.Replace("DB", "", StringComparison.OrdinalIgnoreCase).Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out var db) && int.TryParse(parts[1], out var offset) ? (db, offset) : null;
    }

    /// <summary>The REG_INTF element of a controller: PID_WATER_LEVEL → WATER_LEVEL, PID_PELTON_NOZZLE_1 → PELTON_NOZZLE[1].</summary>
    public static string? RegElementFor(string structure)
    {
        static string Plain(string text) => new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var name = Plain(Regex.Replace(structure ?? "", "^PID_?", "", RegexOptions.IgnoreCase));
        var exact = RegInterface.FirstOrDefault(item => Plain(item.Element) == name);
        if (exact.Element is not null)
        {
            return exact.Element;
        }

        var partial = RegInterface.Where(item => Plain(item.Element).Contains(name) || name.Contains(Plain(item.Element))).ToList();
        return partial.Count == 1 ? partial[0].Element : null;
    }

    // ---------------------------------------------------------------- identification

    /// <summary>
    /// Fits the process model of the profile to a recording of the controller output u and the measurement y (both %):
    /// a first-order lag with dead time, or an integrator with dead time. Grid search over the dead time (and the time
    /// constant), least squares for the gain.
    /// </summary>
    public static ProcessModel Identify(IReadOnlyList<double> time, IReadOnlyList<double> u, IReadOnlyList<double> y, ProcessKind kind)
    {
        var n = time.Count;
        if (n < 20 || u.Count != n || y.Count != n)
        {
            throw new InvalidOperationException("The recording is too short (at least 20 samples).");
        }

        var duration = time[^1] - time[0];
        var dt = duration / (n - 1);
        if (!(dt > 0))
        {
            throw new InvalidOperationException("The recording has no time span.");
        }

        if (u.Max() - u.Min() < 0.5)
        {
            throw new InvalidOperationException("The controller output did not change (less than 0.5 %): make an output step (manual) or a reference step (automatic) during the recording.");
        }

        // Steady values before the change: the first samples up to the first move of the output.
        var first = u[0];
        var start = Enumerable.Range(0, n).FirstOrDefault(i => Math.Abs(u[i] - first) > 0.2);
        var head = Math.Max(1, Math.Min(start, n / 5));
        var u0 = u.Take(head).Average();
        var y0 = y.Take(head).Average();
        var variance = y.Sum(value => (value - y.Average()) * (value - y.Average()));
        if (variance <= 1e-12)
        {
            throw new InvalidOperationException("The measurement did not change during the recording.");
        }

        var maxShift = (int)Math.Min(n / 3, Math.Ceiling(Math.Max(60, duration * 0.3) / dt));
        var shiftStep = Math.Max(1, maxShift / 80);
        var best = (Sse: double.MaxValue, Gain: 0.0, T: 0.0, Shift: 0, Bias: 0.0);
        var delta = new double[n];
        var x = new double[n];
        for (var shift = 0; shift <= maxShift; shift += shiftStep)
        {
            for (var i = 0; i < n; i++)
            {
                delta[i] = (i - shift >= 0 ? u[i - shift] : u[0]) - u0;
            }

            if (kind == ProcessKind.Integrating)
            {
                // y − y0 = Ki·∫Δu dt + b·t  (b: a slow drift / leak already there)
                double sum = 0, sxx = 0, sxt = 0, stt = 0, sxy = 0, sty = 0;
                for (var i = 0; i < n; i++)
                {
                    if (i > 0)
                    {
                        sum += delta[i - 1] * dt;
                    }

                    var t = time[i] - time[0];
                    var e = y[i] - y0;
                    sxx += sum * sum;
                    sxt += sum * t;
                    stt += t * t;
                    sxy += sum * e;
                    sty += t * e;
                    x[i] = sum;
                }

                var det = sxx * stt - sxt * sxt;
                if (Math.Abs(det) < 1e-12)
                {
                    continue;
                }

                var ki = (sxy * stt - sty * sxt) / det;
                var bias = (sty * sxx - sxy * sxt) / det;
                var sse = 0.0;
                for (var i = 0; i < n; i++)
                {
                    var r = y[i] - y0 - ki * x[i] - bias * (time[i] - time[0]);
                    sse += r * r;
                }

                if (sse < best.Sse)
                {
                    best = (sse, ki, 0, shift, bias);
                }
            }
            else
            {
                for (var k = 0; k < 40; k++)
                {
                    // Time constants from 2 samples to the whole recording, logarithmically.
                    var tau = 2 * dt * Math.Pow(Math.Max(duration / (2 * dt), 1.0001), k / 39.0);
                    var a = Math.Exp(-dt / tau);
                    double sxx = 0, sxy = 0;
                    x[0] = 0;
                    for (var i = 1; i < n; i++)
                    {
                        x[i] = a * x[i - 1] + (1 - a) * delta[i - 1];
                    }

                    for (var i = 0; i < n; i++)
                    {
                        sxx += x[i] * x[i];
                        sxy += x[i] * (y[i] - y0);
                    }

                    if (sxx < 1e-12)
                    {
                        continue;
                    }

                    var gain = sxy / sxx;
                    var sse = 0.0;
                    for (var i = 0; i < n; i++)
                    {
                        var r = y[i] - y0 - gain * x[i];
                        sse += r * r;
                    }

                    if (sse < best.Sse)
                    {
                        best = (sse, gain, tau, shift, 0);
                    }
                }
            }
        }

        if (best.Sse == double.MaxValue || Math.Abs(best.Gain) < 1e-12)
        {
            throw new InvalidOperationException("No process model could be fitted to the recording.");
        }

        return new ProcessModel(kind, best.Gain, best.T, best.Shift * dt, Math.Max(0, 1 - best.Sse / variance), u0, y0);
    }

    /// <summary>
    /// A recording on an even time grid (the median sample time), with gaps of a lost connection bridged linearly,
    /// as <see cref="Identify"/> expects.
    /// </summary>
    public static (List<double> Time, List<double> U, List<double> Y) Resample(IReadOnlyList<double> time, IReadOnlyList<double> u, IReadOnlyList<double> y)
    {
        var points = Enumerable.Range(0, time.Count).Where(i => double.IsFinite(u[i]) && double.IsFinite(y[i])).ToList();
        if (points.Count < 2)
        {
            return ([], [], []);
        }

        var steps = points.Zip(points.Skip(1), (a, b) => time[b] - time[a]).Where(step => step > 0).OrderBy(step => step).ToList();
        var dt = steps.Count == 0 ? 0.1 : steps[steps.Count / 2];
        var start = time[points[0]];
        var end = time[points[^1]];
        var (rt, ru, ry) = (new List<double>(), new List<double>(), new List<double>());
        var j = 0;
        for (var t = start; t <= end + 1e-9; t += dt)
        {
            while (j + 1 < points.Count - 1 && time[points[j + 1]] < t)
            {
                j++;
            }

            int a = points[j], b = points[Math.Min(j + 1, points.Count - 1)];
            var f = time[b] > time[a] ? Math.Clamp((t - time[a]) / (time[b] - time[a]), 0, 1) : 0;
            rt.Add(t);
            ru.Add(u[a] + (u[b] - u[a]) * f);
            ry.Add(y[a] + (y[b] - y[a]) * f);
        }

        return (rt, ru, ry);
    }

    // ---------------------------------------------------------------- tuning

    /// <summary>Aggressiveness: fast, normal, robust → factor on the closed-loop time.</summary>
    public static readonly IReadOnlyList<(string Name, double Factor)> Aggressiveness = [("Fast", 1), ("Normal", 2), ("Robust", 4)];

    /// <summary>
    /// SIMC tuning (Skogestad) for the PLC PID, where GAIN multiplies all parts:
    /// first order PI: GAIN = T / (K(τc+L)), TI = min(T, 4(τc+L)); integrating PI: GAIN = 1 / (Ki(τc+L)), TI = 4(τc+L);
    /// integrating P (servo): GAIN = 1 / (Ki(τc+L)), no I. τc = factor · max(L, profile minimum). The sign of the gain
    /// follows the process (a level that falls when the output rises gets a negative GAIN).
    /// </summary>
    public static PidProposal Tune(ProcessModel model, LoopProfile profile, double factor)
    {
        var tauC = factor * Math.Max(model.DeadTime, profile.MinClosedLoopTime);
        var sum = tauC + model.DeadTime;
        if (model.Kind == ProcessKind.Integrating)
        {
            var gain = 1 / (model.Gain * sum);
            return profile.Controller == ControllerKind.P
                ? new PidProposal(gain, 0, 0, true, false, false, tauC)
                : new PidProposal(gain, 4 * sum, 0, true, true, false, tauC);
        }

        var kc = model.TimeConstant / (model.Gain * sum);
        if (profile.Controller == ControllerKind.P)
        {
            return new PidProposal(kc, 0, 0, true, false, false, tauC);
        }

        return new PidProposal(kc, Math.Max(Math.Min(model.TimeConstant, 4 * sum), 0.1), 0, true, true, false, tauC);
    }

    /// <summary>
    /// Closed-loop response to a reference step of 1 % with the model and a PID of the PLC form
    /// (u = GAIN·(e + 1/TI∫e)), for the preview.
    /// </summary>
    public static (double[] Time, double[] Measurement, double[] Output) ClosedLoopStep(ProcessModel model, double gain, double ti, bool integral, double duration)
    {
        var steps = 600;
        var dt = duration / steps;
        var delay = (int)Math.Round(model.DeadTime / dt);
        var outputs = new double[steps + 1];
        var time = new double[steps + 1];
        var y = new double[steps + 1];
        double state = 0, integralSum = 0;
        for (var k = 0; k <= steps; k++)
        {
            time[k] = k * dt;
            y[k] = state;
            var error = 1 - state;
            if (integral && ti > 0)
            {
                integralSum += error * dt / ti;
            }

            outputs[k] = gain * (error + (integral ? integralSum : 0));
            var applied = k - delay >= 0 ? outputs[k - delay] : 0;
            state = model.Kind == ProcessKind.Integrating
                ? state + model.Gain * applied * dt
                : state + (model.Gain * applied - state) * (1 - Math.Exp(-dt / Math.Max(model.TimeConstant, dt)));
        }

        return (time, y, outputs);
    }

    // ---------------------------------------------------------------- parameters

    public static double Real(DbStringRow? row) =>
        row is not null && double.TryParse(row.RealValue.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static bool Flag(DbStringRow? row) => row is not null && (row.IntegerValue.Trim() is "1" or "TRUE" or "true" or "True");

    /// <summary>"T#1H_2M_3S_500MS" / "T#10M" / "T#4S" → seconds.</summary>
    public static double Seconds(string? text)
    {
        var value = (text ?? "").Trim().ToUpperInvariant().Replace("TIME#", "").Replace("T#", "");
        var total = 0.0;
        foreach (Match match in Regex.Matches(value, @"(\d+(?:\.\d+)?)(MS|D|H|M|S)"))
        {
            var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            total += match.Groups[2].Value switch { "D" => number * 86400, "H" => number * 3600, "M" => number * 60, "S" => number, _ => number / 1000 };
        }

        return total;
    }

    /// <summary>Seconds → "T#2M_30S_500MS".</summary>
    public static string TimeText(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
        if (ms == 0)
        {
            return "T#0MS";
        }

        var parts = new List<string>();
        void Part(long unit, string name)
        {
            if (ms >= unit)
            {
                parts.Add($"{ms / unit}{name}");
                ms %= unit;
            }
        }

        Part(86_400_000, "D");
        Part(3_600_000, "H");
        Part(60_000, "M");
        Part(1000, "S");
        Part(1, "MS");
        return "T#" + string.Join("_", parts);
    }

    /// <summary>Writes the proposal into the PID's parameter rows of the project (GAIN, TI, TD, P/I/D action).</summary>
    public static List<string> Apply(PidController controller, PidProposal proposal)
    {
        var changed = new List<string>();
        void Set(string name, Action<DbStringRow> write, Func<DbStringRow, string> read)
        {
            if (controller.Rows.TryGetValue(name, out var row))
            {
                var before = read(row);
                write(row);
                if (before != read(row))
                {
                    changed.Add($"{name}: {before} → {read(row)}");
                }
            }
        }

        Set("GAIN", row => row.RealValue = Math.Round(proposal.Gain, 4).ToString("0.0###", CultureInfo.InvariantCulture), row => row.RealValue);
        Set("TI", row => row.TimeValue = TimeText(proposal.Ti), row => row.TimeValue);
        Set("TD", row => row.TimeValue = TimeText(proposal.Td), row => row.TimeValue);
        Set("P_SEL", row => row.IntegerValue = proposal.PAction ? "1" : "0", row => row.IntegerValue);
        Set("I_SEL", row => row.IntegerValue = proposal.IAction ? "1" : "0", row => row.IntegerValue);
        Set("D_SEL", row => row.IntegerValue = proposal.DAction ? "1" : "0", row => row.IntegerValue);
        return changed;
    }
}
