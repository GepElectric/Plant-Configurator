namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>Short builders for object metadata.</summary>
internal static class Def
{
    public static PortDef BoolIn(string name, string description, bool defaultValue = false) =>
        new(name, PortDirection.Input, PortType.Bool, description, defaultValue ? 1 : 0);

    public static PortDef AnalogIn(string name, string unit, string description, double defaultValue = 0) =>
        new(name, PortDirection.Input, PortType.Analog, description, defaultValue, unit);

    public static PortDef BoolOut(string name, string description) =>
        new(name, PortDirection.Output, PortType.Bool, description);

    public static PortDef AnalogOut(string name, string unit, string description) =>
        new(name, PortDirection.Output, PortType.Analog, description, 0, unit);

    public static ParamDef Param(string name, double defaultValue, string unit, string description) =>
        new(name, defaultValue, unit, description);

    public static OptionDef Option(string name, string description, params string[] values) =>
        new(name, values, description);

    /// <summary>Port only in the variants where <paramref name="option"/> is one of <paramref name="values"/>.</summary>
    public static PortDef When(this PortDef port, string option, params string[] values) => port with { ShownWhen = new(option, values) };

    public static OptionDef When(this OptionDef def, string option, params string[] values) => def with { ShownWhen = new(option, values) };

    public static ParamDef When(this ParamDef def, string option, params string[] values) => def with { ShownWhen = new(option, values) };

    /// <summary>Energy connection (Supply / Live).</summary>
    public static PortDef AsPower(this PortDef port) => port with { Power = true };

    public const int MaxSwitches = 6;

    /// <summary>
    /// Adds digital switch outputs on <paramref name="source"/> (e.g. level switches for drainage): the Switches
    /// option sets how many, and every switch has an ON and an OFF limit. ON above OFF = active above (rising),
    /// ON below OFF = active below; the difference is the hysteresis.
    /// </summary>
    public static ObjectTypeInfo WithSwitches(this ObjectTypeInfo info, string source)
    {
        var unit = info.Port(source)?.Unit ?? "";
        var all = Enumerable.Range(1, MaxSwitches).ToList();
        return info with
        {
            Options = [.. info.Options, Count("Switches", $"Digital outputs switched by {source} (e.g. level switches, pressure switches).", 0, MaxSwitches, 0)],
            Params =
            [
                .. info.Params,
                .. all.SelectMany(i => new[]
                {
                    Param($"Switch{i}On", 0, unit, $"Switch {i} turns ON when {source} reaches this value (above OFF = active above, below OFF = active below).")
                        .When("Switches", AtLeast(i, MaxSwitches)),
                    Param($"Switch{i}Off", 0, unit, $"Switch {i} turns OFF when {source} goes back past this value.")
                        .When("Switches", AtLeast(i, MaxSwitches))
                })
            ],
            Ports = [.. info.Ports, .. all.Select(i => BoolOut($"Switch{i}", $"Switch {i} on {source} (to a DI).").When("Switches", AtLeast(i, MaxSwitches)))],
            SwitchSource = source
        };
    }

    /// <summary>Energy input carrying the voltage in V (0 = dead).</summary>
    public static PortDef PowerIn(string name, string description, double defaultVolts = 0) =>
        AnalogIn(name, "V", description, defaultVolts).AsPower();

    /// <summary>Energy output carrying the voltage in V (0 = dead).</summary>
    public static PortDef PowerOut(string name, string description) => AnalogOut(name, "V", description).AsPower();

    /// <summary>Option "min".."max" telling how many numbered pieces of equipment exist (first value = default).</summary>
    public static OptionDef Count(string name, string description, int min, int max, int defaultValue) =>
        new(name, [defaultValue.ToString(), .. Enumerable.Range(min, max - min + 1).Where(i => i != defaultValue).Select(i => i.ToString())], description);

    /// <summary>Count values from <paramref name="from"/> to <paramref name="max"/>: "unit 3 exists when Units is 3 or more".</summary>
    public static string[] AtLeast(int from, int max) => Enumerable.Range(from, max - from + 1).Select(i => i.ToString()).ToArray();

    /// <summary>"No"/"Yes" option that says whether a piece of equipment exists.</summary>
    public static OptionDef Has(string name, string description) => new(name, ["No", "Yes"], description);

    public static IEnumerable<PortDef> Numbered(int count, Func<int, PortDef> create) =>
        Enumerable.Range(1, count).Select(create);
}
