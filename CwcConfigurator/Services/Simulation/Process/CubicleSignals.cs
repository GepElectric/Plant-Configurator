using System.Globalization;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Names and signals for the operator devices of a cubicle door, from the PLC signal table and the relay logic:
/// <list type="bullet">
/// <item>push buttons and switches drive the DI that names them ("Gate 1 pushbutton open (-S210)") — unless their
/// contacts are on the relay sheets (then the relay logic wires them); a switch per position ("… (-S3) - Local" → LOCAL);</item>
/// <item>the plate text is that signal name without the tag; a lamp gets the name of what lights it (the DI / DO of
/// the contacts or relays in its path, e.g. "Gate 1 motor protection MCB ON").</item>
/// </list>
/// Texts and signals set already are kept.
/// </summary>
public static class CubicleSignals
{
    private static readonly Regex TagInName = new(@"\s*\(([^)]*)\)", RegexOptions.Compiled);

    /// <returns>Number of devices named or connected.</returns>
    public static int Link(ProcessModel model, IEnumerable<CubicleItem> items, IEnumerable<DigitalSignalConfig> inputs, IEnumerable<DigitalSignalConfig> outputs)
    {
        var diList = Usable(inputs);
        var doList = Usable(outputs);

        // Devices with contacts on the relay sheets are wired through the relay logic (to PLC inputs or relays).
        var wired = model.RelaySheets.SelectMany(sheet => sheet.Circuits).SelectMany(circuit => RelayLogicRunner.AllElements(circuit.Elements))
            .Where(element => !element.IsParallel).Select(element => element.Tag.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = 0;
        foreach (var item in items.Where(item => item.Tag.Trim().Length > 0))
        {
            var tag = item.Tag.Trim();
            var named = diList.Where(channel => Names(channel.SignalName, tag)).ToList();
            var before = (item.Text, item.Outputs, item.Source);
            if (item.IsButton || item.IsSwitch)
            {
                if (named.Count > 0 && item.Text.Trim().Length == 0)
                {
                    item.Text = Plate(named[0].SignalName, item.IsSwitch);
                }

                if (!wired.Contains(tag) && item.Outputs.Trim().Length == 0 && named.Count > 0)
                {
                    if (item.IsSwitch && model.RelaySwitches.FirstOrDefault(selector => selector.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase)) is { } selector)
                    {
                        // "… (-S3) - Local" → position LOCAL, "- Automatic" → AUTO.
                        var byPosition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var channel in named)
                        {
                            var suffix = channel.SignalName[(channel.SignalName.LastIndexOf(')') + 1)..].Trim(' ', '-').Replace(" position", "", StringComparison.OrdinalIgnoreCase);
                            var position = selector.PositionList().FirstOrDefault(name => Matches(name, suffix));
                            if (position is not null)
                            {
                                byPosition[position] = "DI:" + channel.Id.ToString(CultureInfo.InvariantCulture);
                            }
                        }

                        item.SetOutputsByPosition(byPosition);
                    }
                    else if (item.IsButton)
                    {
                        item.Outputs = "DI:" + named[0].Id.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            else if (item.IsLamp && item.Text.Trim().Length == 0)
            {
                item.Text = LampName(model, item, diList, doList);
            }

            if (before != (item.Text, item.Outputs, item.Source))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>The name of what lights the lamp: the signals of the contacts / relays in its path.</summary>
    private static string LampName(ProcessModel model, CubicleItem lamp, List<DigitalSignalConfig> diList, List<DigitalSignalConfig> doList)
    {
        var coil = lamp.Source.Trim();
        var circuit = model.RelaySheets.SelectMany(sheet => sheet.Circuits).FirstOrDefault(item => item.Coil.Tag.Trim().Equals(coil, StringComparison.OrdinalIgnoreCase));
        if (circuit is null)
        {
            return "";
        }

        foreach (var contact in RelayLogicRunner.AllElements(circuit.Elements).Where(element => !element.IsParallel).Select(element => element.Tag.Trim()))
        {
            // A relay driven by a DO: the DO name; otherwise a DI that names the device.
            var relay = model.RelaySheets.SelectMany(sheet => sheet.Circuits).FirstOrDefault(item => item.Coil.Tag.Trim().Equals(contact, StringComparison.OrdinalIgnoreCase));
            if (relay is not null && PortBinding.Parse(relay.Coil.Input) is { Kind: BindingKind.Signal } input
                && doList.FirstOrDefault(channel => channel.Id == input.SignalId) is { SignalName.Length: > 0 } output)
            {
                return Plate(output.SignalName, false);
            }

            if (diList.FirstOrDefault(channel => Names(channel.SignalName, contact)) is { } named)
            {
                return Plate(named.SignalName, false);
            }
        }

        return "";
    }

    private static List<DigitalSignalConfig> Usable(IEnumerable<DigitalSignalConfig> channels) =>
        channels.Where(channel => channel.Config != SignalConfigState.Disabled && channel.Id >= 0 && !channel.SignalName.Contains("SPARE", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The signal name mentions the tag: "Gate 1 pushbutton open (-S210)", "(-F34, -F38)".</summary>
    private static bool Names(string signalName, string tag)
    {
        var bare = tag.Trim().TrimStart('-');
        return TagInName.Matches(signalName ?? "").Any(match => match.Groups[1].Value.Split(',')
            .Any(part => part.Trim().TrimStart('-').Equals(bare, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Plate text from a signal name: without the tag (and for a switch without its position).</summary>
    private static string Plate(string signalName, bool selector)
    {
        var text = signalName ?? "";
        var close = text.LastIndexOf(')');
        if (selector && close > 0)
        {
            text = text[..(close + 1)]; // "Dam pump switch (-S3) - Local" → "Dam pump switch (-S3)"
        }

        return TagInName.Replace(text, "").Trim(' ', '-');
    }

    /// <summary>"LOCAL" ~ "Local", "AUTO" ~ "Automatic", "1 - ON" ~ "ON position".</summary>
    private static bool Matches(string position, string suffix)
    {
        var a = Regex.Replace(position, @"^\d+\s*-\s*", "").Trim();
        var b = suffix.Trim();
        return a.Length > 0 && b.Length > 0
               && (b.StartsWith(a, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b, StringComparison.OrdinalIgnoreCase));
    }
}
