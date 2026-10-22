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

    /// <summary>"No"/"Yes" option that says whether a piece of equipment exists.</summary>
    public static OptionDef Has(string name, string description) => new(name, ["No", "Yes"], description);

    public static IEnumerable<PortDef> Numbered(int count, Func<int, PortDef> create) =>
        Enumerable.Range(1, count).Select(create);
}
