using System.Globalization;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// The SCADA screen "Measurement": every measurement mapped in the project (AI, RTD, counters and the values of the
/// chosen devices of the devices DB) as a Measurement object, grouped by system — a device's values together.
/// Measurements another object already writes are shown (Display); the others are simulated with a value set here.
/// </summary>
public static class MeasurementScreen
{
    public const string Screen = "Measurement";
    private const int Columns = 6;
    private const double ColumnWidth = 175;
    private const double RowHeight = 105;

    /// <param name="devices">Names of the devices (devices DB) whose values are on the screen.</param>
    /// <returns>Number of measurements on the screen.</returns>
    public static int Build(ProcessModel model, IEnumerable<SimulatorSignal> signals, ISet<string> devices)
    {
        // A new build replaces the measurements of the screen made before.
        model.Objects.RemoveAll(item => item.Type == "Measurement" && Screen.Equals(item.Screen, StringComparison.OrdinalIgnoreCase));

        // Signals the model writes already (objects' outputs): only shown.
        var written = new HashSet<(SimulatorSignalKind, int)>();
        foreach (var item in model.Objects)
        {
            var info = ObjectCatalog.Find(item.Type);
            foreach (var (port, binding) in item.Ports)
            {
                if (info?.Port(port)?.Direction != PortDirection.Output)
                {
                    continue;
                }

                foreach (var target in PortBinding.ParseList(binding).Where(target => target.Kind == BindingKind.Signal))
                {
                    written.Add((target.SignalKind, target.SignalId));
                }
            }
        }

        var chosen = signals
            .Where(signal => signal.IsEnabled && signal.Id >= 0 && !signal.Name.Contains("SPARE", StringComparison.OrdinalIgnoreCase)
                             && (signal.Kind is SimulatorSignalKind.AI or SimulatorSignalKind.RTD or SimulatorSignalKind.COUNTER
                                 || (signal.Kind == SimulatorSignalKind.DEV && devices.Contains(signal.System))))
            .GroupBy(signal => (signal.Kind, signal.Id)).Select(group => group.First())
            .ToList();

        var names = model.Objects.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Unique(string name)
        {
            var candidate = name;
            for (var i = 2; !names.Add(candidate); i++)
            {
                candidate = $"{name}_{i}";
            }

            return candidate;
        }

        // Groups: the plant systems first, then each device; a group starts on a new row.
        var groups = chosen
            .GroupBy(signal => signal.Kind == SimulatorSignalKind.DEV ? signal.System : signal.System.Trim().Length > 0 ? signal.System.Trim() : signal.Kind.ToString())
            .OrderBy(group => group.First().Kind == SimulatorSignalKind.DEV ? 1 : 0)
            .ThenBy(group => group.Min(signal => signal.Kind == SimulatorSignalKind.DEV ? signal.Module : signal.Id));
        var row = 0;
        var count = 0;
        foreach (var group in groups)
        {
            var column = 0;
            foreach (var signal in group.OrderBy(signal => signal.Kind).ThenBy(signal => signal.Kind == SimulatorSignalKind.DEV ? signal.ByteOffset : signal.Id))
            {
                if (column == Columns)
                {
                    column = 0;
                    row++;
                }

                var binding = $"{signal.Kind}:{signal.Id.ToString(CultureInfo.InvariantCulture)}";
                var display = written.Contains((signal.Kind, signal.Id));
                var config = new ObjectConfig
                {
                    // A device value is named after its device: GEN_SENTRON_I_1.
                    Name = Unique(Identifier((signal.Kind == SimulatorSignalKind.DEV ? signal.System + "_" : "")
                                             + (signal.PlcName.Length > 0 ? signal.PlcName : $"{signal.Kind}_{signal.Id}"))),
                    Type = "Measurement",
                    Description = $"{Title(signal.Name)}{(signal.Unit.Trim().Length > 0 ? $" [{signal.Unit.Trim()}]" : "")} — {binding}",
                    Group = group.Key,
                    Screen = Screen,
                    X = 40 + column * ColumnWidth,
                    Y = 40 + row * RowHeight
                };
                if (display)
                {
                    config.Options["Mode"] = "Display";
                    config.Ports["Measured"] = binding;
                }
                else
                {
                    config.Ports["Out"] = binding;
                    config.Params["Value"] = StartValue(signal);
                }

                model.Objects.Add(config);
                column++;
                count++;
            }

            row++;
        }

        if (count > 0 && !model.Screens.Contains(Screen, StringComparer.OrdinalIgnoreCase))
        {
            model.Screens = model.AllScreens();
            model.Screens.Add(Screen);
        }

        return count;
    }

    /// <summary>Middle of the measuring range (raw 13824 of 0..27648), or 0 without scaling.</summary>
    private static double StartValue(SimulatorSignal signal) =>
        signal.HasScaling ? Math.Round(signal.Scaling!.ToEngineering(13824), 3) : 0;

    /// <summary>"MEAS[0] → Phase current 1" → "Phase current 1".</summary>
    private static string Title(string name) => name.Contains('→') ? name[(name.IndexOf('→') + 1)..].Trim() : name.Trim();

    private static string Identifier(string text)
    {
        var name = new string(text.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()).Trim('_');
        return name.Length == 0 ? "Measurement" : char.IsDigit(name[0]) ? "M_" + name : name;
    }
}
