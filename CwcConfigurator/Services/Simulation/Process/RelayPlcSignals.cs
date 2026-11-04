using System.Globalization;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Connects the relay logic to the PLC from the start, by the device each PLC channel is wired to (EPLAN hardware
/// import, e.g. DO:0 → -2K05:A1, DI:65 ← -1K01:14; or "(-2K05)" in the signal name):
/// <list type="bullet">
/// <item>output relay (2-K…): the relay's input is the DO itself (no contact in its path);</item>
/// <item>input relay (1-K…): the coil drives the DI (its contact is wired to the PLC input);</item>
/// <item>such a relay whose coil is not drawn on the sheets becomes a device with the DO / DI as its input.</item>
/// </list>
/// Only empty inputs and outputs are filled — nothing set by hand is changed.
/// </summary>
public static class RelayPlcSignals
{
    private static readonly Regex TagInName = new(@"\((-?[0-9A-Za-z.]*[A-Za-z][0-9A-Za-z.]*)\)", RegexOptions.Compiled);

    /// <summary>Relay tag (normalised, see <see cref="Normalize"/>) → channel id.</summary>
    public static Dictionary<string, int> Map(IEnumerable<DigitalSignalConfig> channels)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels.Where(channel => channel.Config != SignalConfigState.Disabled))
        {
            var device = channel.EplanConnectedTo.Split(':')[0];
            if (device.Trim().Length == 0 && TagInName.Match(channel.SignalName) is { Success: true } match)
            {
                device = match.Groups[1].Value;
            }

            var tag = Normalize(device);
            if (tag.Length > 0)
            {
                map.TryAdd(tag, channel.Id);
            }
        }

        return map;
    }

    /// <summary>
    /// Paths that are only a contact operated by a PLC output ("DO:160", the old way of driving an output relay)
    /// become the relay's direct input.
    /// </summary>
    public static int MigrateInputs(ProcessModel model)
    {
        RelayDevices.Unify(model);
        var changed = 0;
        foreach (var circuit in model.RelaySheets.SelectMany(sheet => sheet.Circuits))
        {
            if (!circuit.Coil.HasInput && circuit.Feed.Length == 0 && circuit.Coil.Tag.Trim().Length > 0
                && circuit.Elements is [{ IsParallel: false, IsNormallyClosed: false } contact]
                && PortBinding.Parse(contact.Tag) is { Kind: BindingKind.Signal, SignalKind: SimulatorSignalKind.DO })
            {
                circuit.Coil.Input = contact.Tag.Trim();
                circuit.Elements.Clear();
                changed++;
            }
        }

        return changed;
    }

    /// <summary>"2-K05", "-2K05", "-2K05:A1" → "2K05" (the same relay however EPLAN writes the tag).</summary>
    public static string Normalize(string tag) => new((tag ?? "").Split(':')[0].Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <returns>Number of sources and outputs filled.</returns>
    public static int Link(ProcessModel model, IEnumerable<DigitalSignalConfig> outputs, IEnumerable<DigitalSignalConfig> inputs)
    {
        var filled = RelayDevices.Unify(model) + MigrateInputs(model);
        var doMap = Map(outputs);
        var diMap = Map(inputs);
        if (doMap.Count + diMap.Count == 0)
        {
            return filled;
        }

        var coils = model.RelaySheets.SelectMany(sheet => sheet.Circuits).Select(circuit => Normalize(circuit.Coil.Tag))
            .Where(tag => tag.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var circuit in model.RelaySheets.SelectMany(sheet => sheet.Circuits))
        {
            // "-TM2:DI1" (input of a drive), "-H104:LAMP": not a relay wired to the PLC.
            var coilTag = circuit.Coil.Tag.Contains(':') ? "" : Normalize(circuit.Coil.Tag);

            // Output relay: driven by the PLC output itself; its path has no contact (or only an empty one).
            if (doMap.TryGetValue(coilTag, out var output) && !circuit.Coil.HasInput && circuit.Feed.Length == 0
                && circuit.Elements is [] or [{ IsParallel: false, Tag.Length: 0 }])
            {
                circuit.Elements.Clear();
                circuit.Coil.Input = "DO:" + output.ToString(CultureInfo.InvariantCulture);
                filled++;
            }

            // Input relay: its contact is wired to the PLC input.
            if (diMap.TryGetValue(coilTag, out var input) && circuit.Coil.Outputs.Trim().Length == 0)
            {
                circuit.Coil.Outputs = "DI:" + input.ToString(CultureInfo.InvariantCulture);
                filled++;
            }

            foreach (var contact in RelayLogicRunner.AllElements(circuit.Elements).Where(element => !element.IsParallel))
            {
                var tag = Normalize(contact.Tag);
                if (tag.Length == 0 || coils.Contains(tag) || PortBinding.Parse(contact.Tag).Kind != BindingKind.None)
                {
                    continue;
                }

                // A relay whose coil is not drawn: a device driven by its PLC output / reading its PLC input.
                if (doMap.TryGetValue(tag, out var contactOutput) && RelayDevices.Define(model, contact.Tag, "DO:" + contactOutput.ToString(CultureInfo.InvariantCulture)))
                {
                    filled++;
                }
                else if (diMap.TryGetValue(tag, out var contactInput) && RelayDevices.Define(model, contact.Tag, "DI:" + contactInput.ToString(CultureInfo.InvariantCulture)))
                {
                    filled++;
                }
            }
        }

        return filled;
    }
}
