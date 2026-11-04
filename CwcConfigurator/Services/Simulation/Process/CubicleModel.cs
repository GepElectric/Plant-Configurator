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

/// <summary>Places imported operator devices on a cubicle door, to be arranged by hand.</summary>
public static class CubicleLayout
{
    private const double Pitch = 70;
    private const double RowPitch = 85;

    /// <summary>
    /// All cubicle doors are replaced by one door with every operator device of the project (EPLAN import). Plate
    /// texts and colours typed by hand are kept where the project has none.
    /// </summary>
    /// <returns>Number of devices on the new door.</returns>
    public static int ReplaceAll(ProcessModel model, IEnumerable<CubicleItem> devices, string name)
    {
        var old = model.Cubicles.SelectMany(cubicle => cubicle.Items).Where(item => item.Tag.Trim().Length > 0)
            .GroupBy(item => item.Tag.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var list = devices.ToList();
        foreach (var item in list)
        {
            if (old.TryGetValue(item.Tag.Trim(), out var previous))
            {
                item.Text = item.Text.Trim().Length > 0 ? item.Text : previous.Text;
                item.Outputs = item.Outputs.Trim().Length > 0 ? item.Outputs : previous.Outputs;
                item.Source = item.Source.Trim().Length > 0 ? item.Source : previous.Source;
            }
        }

        model.Cubicles.Clear();
        return AddDevices(model, list, name);
    }

    /// <summary>
    /// Adds the devices not on any cubicle yet to the door <paramref name="name"/> (created when missing): one row per
    /// kind — switches, push buttons and mushrooms, lamps — in a grid.
    /// </summary>
    /// <returns>Number of devices added.</returns>
    public static int AddDevices(ProcessModel model, IEnumerable<CubicleItem> devices, string name)
    {
        var placed = model.Cubicles.SelectMany(cubicle => cubicle.Items).Select(item => item.Tag.Trim())
            .Where(tag => tag.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = devices.Where(item => item.Tag.Trim().Length > 0 && placed.Add(item.Tag.Trim()))
            .OrderBy(item => item.IsSwitch ? 0 : item.IsButton ? 1 : 2)
            .ThenBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (added.Count == 0)
        {
            return 0;
        }

        var door = model.Cubicles.FirstOrDefault(cubicle => cubicle.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (door is null)
        {
            door = new Cubicle { Name = name, Width = 800, Height = 600 };
            model.Cubicles.Add(door);
        }

        var columns = Math.Max(1, (int)((door.Width - 40) / Pitch));
        var row = door.Items.Count == 0 ? 0 : (int)Math.Ceiling((door.Items.Max(item => item.Y) - 60) / RowPitch) + 1;
        foreach (var group in added.GroupBy(item => item.IsSwitch ? 0 : item.IsButton ? 1 : 2))
        {
            var column = 0;
            foreach (var item in group)
            {
                if (column == columns)
                {
                    column = 0;
                    row++;
                }

                item.X = 55 + column * Pitch;
                item.Y = 60 + row * RowPitch;
                door.Items.Add(item);
                column++;
            }

            row++;
        }

        door.Height = Math.Max(door.Height, 60 + row * RowPitch);
        return added.Count;
    }
}
