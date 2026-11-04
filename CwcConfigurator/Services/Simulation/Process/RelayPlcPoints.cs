using System.Globalization;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Connects the PLC card points of imported sheets to the simulator signals, by the EPLAN terminal stored on every
/// DI / DO of the project (EplanTerminalRef "function|A611|1") or by module name and port:
/// <list type="bullet">
/// <item>an input point ("-A611:DI0" at the end of a path) becomes the PLC input object: a path that drives DI:n;</item>
/// <item>an output point ("-A631:DQ0" at the start of a path) becomes DO:n — a relay wired straight to it is driven by
/// the DO (its input).</item>
/// </list>
/// </summary>
public static class RelayPlcPoints
{
    /// <returns>Number of points connected; <paramref name="missing"/> lists the points without a signal.</returns>
    public static int Resolve(ProcessModel model, IReadOnlyDictionary<string, EplanPlcPoint> points, IEnumerable<DigitalSignalConfig> inputs,
        IEnumerable<DigitalSignalConfig> outputs, List<string> missing)
    {
        // Spare channels are not connected: set by hand.
        var diList = Usable(inputs);
        var doList = Usable(outputs);
        var resolved = 0;
        foreach (var circuit in model.RelaySheets.SelectMany(sheet => sheet.Circuits))
        {
            if (points.TryGetValue(circuit.Coil.Tag.Trim(), out var input) && !input.Output)
            {
                if (Find(diList, input) is { } signal)
                {
                    circuit.Coil.Outputs = "DI:" + signal.Id.ToString(CultureInfo.InvariantCulture);
                    circuit.Coil.Text = Join(circuit.Coil.Text, signal.SignalName);
                    circuit.Coil.Tag = "";
                    resolved++;
                }
                else
                {
                    // Spare input: the PLC input object stays, its DI is set by hand (Drives).
                    circuit.Coil.Text = Join(circuit.Coil.Text, "SPARE " + input.Address);
                    missing.Add($"{circuit.Coil.Tag} ({input.Address})");
                }
            }

            foreach (var element in RelayLogicRunner.AllElements(circuit.Elements).Where(element => !element.IsParallel))
            {
                if (points.TryGetValue(element.Tag.Trim(), out var output) && output.Output)
                {
                    if (Find(doList, output) is { } signal)
                    {
                        element.Tag = "DO:" + signal.Id.ToString(CultureInfo.InvariantCulture);
                        resolved++;
                    }
                    else
                    {
                        missing.Add($"{element.Tag} ({output.Address})");
                    }
                }
            }
        }

        return resolved;
    }

    private static List<DigitalSignalConfig> Usable(IEnumerable<DigitalSignalConfig> channels)
    {
        // A spare: disabled, or named SPARE (spares usually share one ID).
        return channels.Where(channel => channel.Config != SignalConfigState.Disabled && channel.Id >= 0
                                         && !channel.SignalName.Contains("SPARE", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static DigitalSignalConfig? Find(List<DigitalSignalConfig> channels, EplanPlcPoint point)
    {
        var byTerminal = channels.Where(channel => point.Terminal.Length > 0
                                                   && channel.EplanTerminalRef.EndsWith($"|{point.Card}|{point.Terminal}", StringComparison.OrdinalIgnoreCase));
        var byModule = channels.Where(channel => channel.ModuleName.Trim().TrimStart('-').Equals(point.Card, StringComparison.OrdinalIgnoreCase) && channel.Port == point.Port);
        return byTerminal.Concat(byModule)
            .Where(channel => channel.Id >= 0)
            .OrderBy(channel => channel.Config == SignalConfigState.Disabled ? 1 : 0)
            .FirstOrDefault();
    }

    private static string Join(string text, string name)
    {
        var line = (name ?? "").Trim();
        return line.Length == 0 || text.Contains(line, StringComparison.OrdinalIgnoreCase) ? text : text.Trim().Length == 0 ? line : text.Trim() + "\n" + line;
    }
}
