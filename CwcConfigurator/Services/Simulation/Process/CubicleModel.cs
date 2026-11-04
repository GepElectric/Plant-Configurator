using YamlDotNet.Serialization;

namespace CwcConfigurator.Services.Simulation.Process;

public static class CubicleItemKind
{
    public const string PushButton = "PushButton";

    /// <summary>Emergency stop: a mushroom head that latches when pressed and is released by a second click (turn).</summary>
    public const string EmergencyStop = "EmergencyStop";
    public const string Switch = "Switch";
    public const string Lamp = "Lamp";
    public const string Label = "Label";
}

/// <summary>
/// Front of a cubicle (cabinet door) with its operator devices: push buttons, selector switches, indicator lamps and
/// label plates, placed in millimetres from the top-left corner of the door. A selector switch is the
/// <see cref="RelaySwitch"/> with the same tag, so its contacts on the relay-logic sheets follow it.
/// </summary>
public sealed class Cubicle
{
    public string Name { get; set; } = "";

    /// <summary>Door width in mm.</summary>
    public double Width { get; set; } = 600;

    /// <summary>Door height in mm.</summary>
    public double Height { get; set; } = 800;

    public List<CubicleItem> Items { get; set; } = [];
}

/// <summary>One device on the cubicle door.</summary>
public sealed class CubicleItem
{
    public string Kind { get; set; } = CubicleItemKind.PushButton;

    /// <summary>Device tag, e.g. -S11 (push button / switch) or -H1 (lamp).</summary>
    public string Tag { get; set; } = "";

    /// <summary>Text of the label plate (lines separated by new lines).</summary>
    public string Text { get; set; } = "";

    /// <summary>Colour of the button or lamp lens: Green, Red, Yellow, White, Blue, Black.</summary>
    public string Color { get; set; } = "Green";

    /// <summary>Centre of the device on the door, in mm.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Lamp or illuminated push button: what lights it — a relay or push-button tag (-K101), a signal (DO:12, DI:3) or an object port (@Gate1.Open).</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Push button: the PLC inputs it switches while pressed ("DI:69" or "DI:69, DI:70").
    /// Selector switch: the inputs per position, "LOCAL=DI:10; AUTO=DI:11, DI:12" (1 in that position, 0 otherwise).
    /// </summary>
    public string Outputs { get; set; } = "";

    /// <summary>Push button contact: "NO" (1 while pressed) or "NC" (0 while pressed, e.g. a stop button).</summary>
    public string Contact { get; set; } = "NO";

    /// <summary>Signals per position of a switch: position → bindings.</summary>
    public Dictionary<string, string> OutputsByPosition()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Outputs.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals > 0)
            {
                result[part[..equals].Trim()] = part[(equals + 1)..].Trim();
            }
        }

        return result;
    }

    public void SetOutputsByPosition(Dictionary<string, string> outputs) =>
        Outputs = string.Join("; ", outputs.Where(pair => pair.Value.Trim().Length > 0).Select(pair => $"{pair.Key}={pair.Value.Trim()}"));

    /// <summary>Push button held down in the view (not saved).</summary>
    [YamlIgnore]
    public bool Pressed { get; set; }

    [YamlIgnore]
    public bool IsSwitch => Kind.Equals(CubicleItemKind.Switch, StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsLamp => Kind.Equals(CubicleItemKind.Lamp, StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsButton => Kind.Equals(CubicleItemKind.PushButton, StringComparison.OrdinalIgnoreCase) || IsEmergencyStop;

    [YamlIgnore]
    public bool IsEmergencyStop => Kind.Equals(CubicleItemKind.EmergencyStop, StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsLabel => Kind.Equals(CubicleItemKind.Label, StringComparison.OrdinalIgnoreCase);
}
