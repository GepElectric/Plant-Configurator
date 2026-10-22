namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// The plant model of one PLC database: a list of configured objects. Saved as YAML next to the project
/// (see <see cref="ProcessModelStore"/>) so it can be edited in the Simulator or by hand.
/// </summary>
public sealed class ProcessModel
{
    public List<ObjectConfig> Objects { get; set; } = [];

    /// <summary>Saved forces of the signal list: "KIND:ID:ADDRESS TRUE|FALSE|SET value|WIREBREAK".</summary>
    public List<string> Forces { get; set; } = [];

    /// <summary>SCADA screens in their order (objects without a screen are on the first one).</summary>
    public List<string> Screens { get; set; } = [];

    public const string MainScreen = "Main";

    /// <summary>Screens in order: the listed ones plus any used by an object; at least "Main".</summary>
    public List<string> AllScreens()
    {
        var screens = new List<string>();
        foreach (var name in Screens.Concat(Objects.Select(item => item.Screen ?? "")).Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (!screens.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                screens.Add(name);
            }
        }

        if (screens.Count == 0 || Objects.Any(item => item.Screen is null) && !screens.Contains(MainScreen, StringComparer.OrdinalIgnoreCase))
        {
            screens.Insert(0, MainScreen);
        }

        return screens;
    }

    /// <summary>Screen of an object (objects without one are on the first screen).</summary>
    public string ScreenOf(ObjectConfig item) => item.Screen ?? AllScreens()[0];

    public ObjectConfig? Find(string name) =>
        Objects.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One object instance: its type, chosen variants, parameters, port bindings and SCADA position.</summary>
public sealed class ObjectConfig
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<double>> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Port → binding text: "DI:23", "DO:188", "AI:4", "RTD:27", "COUNTER:30", "AO:2", "@Object.Port" or "=value".</summary>
    public Dictionary<string, string> Ports { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Group (e.g. "Nozzles"): a node in the object tree and a named frame on the SCADA; null = no group.</summary>
    public string? Group { get; set; }

    /// <summary>SCADA screen of the object (e.g. "AC distribution"); null = the first screen.</summary>
    public string? Screen { get; set; }
}

public enum PortDirection
{
    Input,
    Output
}

public enum PortType
{
    Bool,
    Analog
}

public sealed record PortDef(string Name, PortDirection Direction, PortType Type, string Description, double Default = 0, string Unit = "")
{
    /// <summary>The port exists only in these variants; otherwise it is hidden and works as not connected (its default).</summary>
    public ShownWhen? ShownWhen { get; init; }
}

/// <summary>Variant condition: option <paramref name="Option"/> has one of <paramref name="Values"/>.</summary>
public sealed record ShownWhen(string Option, IReadOnlyList<string> Values);

public sealed record ParamDef(string Name, double Default, string Unit, string Description)
{
    /// <summary>The parameter is used (and shown) only in these variants.</summary>
    public ShownWhen? ShownWhen { get; init; }
}

public sealed record OptionDef(string Name, IReadOnlyList<string> Values, string Description)
{
    public string Default => Values[0];

    /// <summary>The option is shown only in these variants.</summary>
    public ShownWhen? ShownWhen { get; init; }

    /// <summary>A "No"/"Yes" option that switches equipment (and its ports) on.</summary>
    public bool IsSwitch => Values.Count == 2 && Values[0] == "No" && Values[1] == "Yes";

    /// <summary>A count ("0".."4"): how many of a numbered equipment exist.</summary>
    public bool IsCount => Values.All(value => int.TryParse(value, out _));
}

/// <summary>A characteristic given as X/Y points (e.g. nozzle stroke → flow), interpolated linearly between points.</summary>
public sealed record TableDef(string Name, string XLabel, string YLabel, IReadOnlyList<double> DefaultX, IReadOnlyList<double> DefaultY, string Description);

/// <summary>Metadata of an object type, used by the editor, the YAML store and the engine.</summary>
public sealed record ObjectTypeInfo(
    string Type,
    string Title,
    string Category,
    string Description,
    IReadOnlyList<OptionDef> Options,
    IReadOnlyList<ParamDef> Params,
    IReadOnlyList<PortDef> Ports,
    IReadOnlyList<TableDef> Tables,
    IReadOnlyList<string> ScadaValues,
    Func<SimObject> Create)
{
    /// <summary>Value of an option of an object (the default when not set or unknown).</summary>
    public string OptionValue(ObjectConfig config, string name)
    {
        var def = Options.FirstOrDefault(option => option.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (def is null)
        {
            return config.Options.TryGetValue(name, out var raw) ? raw : "";
        }

        return config.Options.TryGetValue(name, out var value) && def.Values.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : def.Default;
    }

    /// <summary>The variant of <paramref name="config"/> has this port / option.</summary>
    public bool IsActive(ShownWhen? when, ObjectConfig config) =>
        when is null || when.Values.Contains(OptionValue(config, when.Option), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Older models: equipment switches ("No"/"Yes") that were added later are switched on when a port they show
    /// is already connected, so the model keeps working the same.
    /// </summary>
    public void UpgradeSwitches(ObjectConfig config)
    {
        foreach (var option in Options.Where(option => (option.IsSwitch || option.IsCount) && !config.Options.ContainsKey(option.Name)))
        {
            var bound = Ports.Where(port => port.ShownWhen?.Option.Equals(option.Name, StringComparison.OrdinalIgnoreCase) == true
                                            && config.Ports.TryGetValue(port.Name, out var text) && !string.IsNullOrWhiteSpace(text)).ToList();
            if (bound.Count == 0 || bound.All(port => IsActive(port.ShownWhen, config)))
            {
                continue;
            }

            // The first variant (No -> Yes, or the smallest count) that shows every connected port.
            var value = option.Values.FirstOrDefault(value => bound.All(port => port.ShownWhen!.Values.Contains(value, StringComparer.OrdinalIgnoreCase)));
            if (value is not null)
            {
                config.Options[option.Name] = value;
            }
        }
    }

    public PortDef? Port(string name) => Ports.FirstOrDefault(port => port.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Kind of a binding target.</summary>
public enum BindingKind
{
    None,
    Signal,
    Link,
    Constant
}

/// <summary>Parsed port binding.</summary>
public readonly record struct PortBinding(BindingKind Kind, SimulatorSignalKind SignalKind, int SignalId, string LinkObject, string LinkPort, double Constant)
{
    public static PortBinding None => new(BindingKind.None, default, 0, "", "", 0);

    public static PortBinding Parse(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            return None;
        }

        if (value[0] == '=')
        {
            var number = value[1..].Trim();
            if (number.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return new PortBinding(BindingKind.Constant, default, 0, "", "", 1);
            }

            if (number.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return new PortBinding(BindingKind.Constant, default, 0, "", "", 0);
            }

            return double.TryParse(number.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var constant)
                ? new PortBinding(BindingKind.Constant, default, 0, "", "", constant)
                : None;
        }

        if (value[0] == '@')
        {
            var dot = value.LastIndexOf('.');
            return dot > 1 ? new PortBinding(BindingKind.Link, default, 0, value[1..dot], value[(dot + 1)..], 0) : None;
        }

        var colon = value.IndexOf(':');
        if (colon > 0
            && Enum.TryParse<SimulatorSignalKind>(value[..colon].Trim(), true, out var kind)
            && int.TryParse(value[(colon + 1)..].Trim(), out var id))
        {
            return new PortBinding(BindingKind.Signal, kind, id, "", "", 0);
        }

        return None;
    }

    /// <summary>Comma-separated bindings (an output may drive several signals).</summary>
    public static IReadOnlyList<PortBinding> ParseList(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .Where(binding => binding.Kind != BindingKind.None)
            .ToList();

    public override string ToString() => Kind switch
    {
        BindingKind.Signal => $"{SignalKind}:{SignalId}",
        BindingKind.Link => $"@{LinkObject}.{LinkPort}",
        BindingKind.Constant => "=" + Constant.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => ""
    };
}
