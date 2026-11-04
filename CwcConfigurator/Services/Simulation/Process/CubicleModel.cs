using YamlDotNet.Serialization;

namespace CwcConfigurator.Services.Simulation.Process;

public static class CubicleItemKind
{
    public const string PushButton = "PushButton";
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

    /// <summary>Lamp: what lights it (relay tag, signal or object), for a later connection.</summary>
    public string Source { get; set; } = "";

    /// <summary>Push button held down in the view (not saved).</summary>
    [YamlIgnore]
    public bool Pressed { get; set; }

    [YamlIgnore]
    public bool IsSwitch => Kind.Equals(CubicleItemKind.Switch, StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsLamp => Kind.Equals(CubicleItemKind.Lamp, StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsButton => Kind.Equals(CubicleItemKind.PushButton, StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsLabel => Kind.Equals(CubicleItemKind.Label, StringComparison.OrdinalIgnoreCase);
}
