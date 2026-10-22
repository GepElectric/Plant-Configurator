namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// The plant model of one PLC database: a list of configured objects. Saved as YAML next to the project
/// (see <see cref="ProcessModelStore"/>) so it can be edited in the Simulator or by hand.
/// </summary>
public sealed class ProcessModel
{
    public List<ObjectConfig> Objects { get; set; } = [];

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

public sealed record PortDef(string Name, PortDirection Direction, PortType Type, string Description, double Default = 0, string Unit = "");

public sealed record ParamDef(string Name, double Default, string Unit, string Description);

public sealed record OptionDef(string Name, IReadOnlyList<string> Values, string Description)
{
    public string Default => Values[0];
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
