using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

public abstract class RowBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected static string Format(double value) => Math.Abs(value) >= 1e6 || (value != 0 && Math.Abs(value) < 1e-3)
        ? value.ToString("0.###E+0", CultureInfo.InvariantCulture)
        : value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>One parameter of an object in the editor; changes apply to the running model immediately.</summary>
public sealed class ParamRow(ParamDef def, ObjectConfig config, Action changed) : RowBase
{
    public string Name => def.Name;
    public string Unit => def.Unit;
    public string Description => def.Description;
    public string Default => Format(def.Default);
    public bool IsChanged => config.Params.ContainsKey(def.Name);

    public string Value
    {
        get => Format(config.Params.TryGetValue(def.Name, out var value) ? value : def.Default);
        set
        {
            var text = (value ?? "").Trim().Replace(',', '.');
            if (text.Length == 0)
            {
                config.Params.Remove(def.Name);
            }
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                if (number == def.Default)
                {
                    config.Params.Remove(def.Name);
                }
                else
                {
                    config.Params[def.Name] = number;
                }
            }

            Raise();
            Raise(nameof(IsChanged));
            changed();
        }
    }
}

/// <summary>One port of an object in the editor: binding text (signal, link or constant) and the live value.</summary>
public sealed class PortRow(PortDef def, ObjectConfig config, IReadOnlyList<string> suggestions, Action changed) : RowBase
{
    public const string Separator = "  —  ";
    private string _live = "";

    public PortDef Def => def;
    public string Name => def.Name;
    public string Direction => def.Direction == PortDirection.Input ? "in" : "out";
    public string Kind => def.Type == PortType.Bool ? "bool" : string.IsNullOrEmpty(def.Unit) ? "analog" : def.Unit;
    public string Description => def.Description;
    public IReadOnlyList<string> Suggestions => suggestions;

    /// <summary>
    /// "DO:0  —  text, DO:1  —  text" → "DO:0, DO:1". A comma inside a description is skipped: only pieces that look
    /// like a binding (signal, link or constant) start a new one.
    /// </summary>
    public static string StripDescriptions(string value)
    {
        var parts = new List<string>();
        var inDescription = false;
        foreach (var piece in value.Split(','))
        {
            var dash = piece.IndexOf(Separator.Trim(), StringComparison.Ordinal);
            var code = (dash >= 0 ? piece[..dash] : piece).Trim();
            if (inDescription && dash < 0 && !BindingToken.IsMatch(code))
            {
                continue;
            }

            if (code.Length > 0)
            {
                parts.Add(code);
            }

            inDescription = dash >= 0;
        }

        return string.Join(", ", parts);
    }

    private static readonly System.Text.RegularExpressions.Regex BindingToken = new(
        @"^((DI|DO|AI|AO|RTD|COUNTER|DEV|DEVBIT)(:\d+)?|@\S+|=\S+)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string Stored => config.Ports.TryGetValue(def.Name, out var text) ? text : "";

    /// <summary>The binding with the full signal text, e.g. "DO:0  —  PLC · Name" (several separated by ", ").</summary>
    public string Binding
    {
        get => string.Join(", ", Stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => suggestions.FirstOrDefault(item => item.StartsWith(part + Separator, StringComparison.OrdinalIgnoreCase)) ?? part));
        set
        {
            var text = StripDescriptions(value ?? "");
            if (text == Stored)
            {
                return;
            }

            if (text.Length == 0)
            {
                config.Ports.Remove(def.Name);
            }
            else
            {
                config.Ports[def.Name] = text;
            }

            Raise();
            changed();
        }
    }

    public string Live
    {
        get => _live;
        set
        {
            if (_live != value)
            {
                _live = value;
                Raise();
            }
        }
    }

    public void Refresh(SimObject? instance)
    {
        if (instance is null || !instance.Values.TryGetValue(def.Name, out var value))
        {
            Live = "";
            return;
        }

        Live = def.Type == PortType.Bool ? (value >= 0.5 ? "TRUE" : "FALSE") : Format(value) + (string.IsNullOrEmpty(def.Unit) ? "" : " " + def.Unit);
    }
}

/// <summary>An option (variant) of an object.</summary>
public sealed class OptionRow(OptionDef def, ObjectConfig config, Action changed) : RowBase
{
    public string Name => def.Name;
    public string Description => def.Description;
    public IReadOnlyList<string> Values => def.Values;

    public string Value
    {
        get => config.Options.TryGetValue(def.Name, out var value) && def.Values.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : def.Default;
        set
        {
            if (value == def.Default)
            {
                config.Options.Remove(def.Name);
            }
            else
            {
                config.Options[def.Name] = value;
            }

            Raise();
            changed();
        }
    }
}

/// <summary>A characteristic table as editable text "x:y; x:y; …".</summary>
public sealed class TableRow(TableDef def, ObjectConfig config, Action changed) : RowBase
{
    public string Name => def.Name;
    public string Header => $"{def.Name}: {def.XLabel} → {def.YLabel}";
    public string Description => def.Description;

    public string Text
    {
        get
        {
            var x = config.Tables.TryGetValue(def.Name + "X", out var cx) ? cx : def.DefaultX;
            var y = config.Tables.TryGetValue(def.Name + "Y", out var cy) ? cy : def.DefaultY;
            return string.Join("; ", x.Zip(y, (a, b) => $"{Format(a)}:{Format(b)}"));
        }
        set
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var pair in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split(':');
                if (parts.Length == 2
                    && double.TryParse(parts[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(parts[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }

            if (xs.Count >= 2 && xs.Zip(xs.Skip(1), (a, b) => b > a).All(ok => ok))
            {
                config.Tables[def.Name + "X"] = xs;
                config.Tables[def.Name + "Y"] = ys;
                changed();
            }

            Raise();
        }
    }
}

/// <summary>Output of an object in the SCADA view with a force value.</summary>
public sealed class ForceRow(PortDef def, SimObject instance, Action changed) : RowBase
{
    private string _live = "";

    public string Name => def.Name;
    public string Unit => def.Unit;
    public bool IsBool => def.Type == PortType.Bool;

    public bool Forced
    {
        get => instance.Forces.ContainsKey(def.Name);
        set
        {
            if (value)
            {
                instance.Forces[def.Name] = ParseForce();
            }
            else
            {
                instance.Forces.Remove(def.Name);
            }

            Raise();
            changed();
        }
    }

    private string _forceText = "";

    public string ForceValue
    {
        get => _forceText.Length > 0 ? _forceText : instance.Forces.TryGetValue(def.Name, out var value) ? Format(value) : "";
        set
        {
            _forceText = value ?? "";
            if (Forced)
            {
                instance.Forces[def.Name] = ParseForce();
            }

            Raise();
        }
    }

    public string Live
    {
        get => _live;
        private set
        {
            if (_live != value)
            {
                _live = value;
                Raise();
            }
        }
    }

    public void Refresh()
    {
        var value = instance.Value(def.Name);
        Live = IsBool ? (value >= 0.5 ? "TRUE" : "FALSE") : Format(value) + (string.IsNullOrEmpty(def.Unit) ? "" : " " + def.Unit);
    }

    private double ParseForce()
    {
        var text = _forceText.Trim().Replace(',', '.');
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : instance.Value(def.Name);
    }
}

/// <summary>Binding suggestions for a port: matching signals of the object's PLC, outputs of other objects, constants.</summary>
public static class BindingSuggestions
{
    public static List<string> For(PortDef port, string plc, SimulationController controller)
    {
        var list = new List<string>();
        var session = controller.Session(plc);
        if (session is not null)
        {
            var kinds = port.Direction == PortDirection.Input
                ? port.Type == PortType.Bool ? new[] { SimulatorSignalKind.DO, SimulatorSignalKind.DI } : [SimulatorSignalKind.AO, SimulatorSignalKind.AI, SimulatorSignalKind.RTD, SimulatorSignalKind.COUNTER]
                : port.Type == PortType.Bool ? [SimulatorSignalKind.DI, SimulatorSignalKind.DEVBIT] : [SimulatorSignalKind.AI, SimulatorSignalKind.RTD, SimulatorSignalKind.COUNTER, SimulatorSignalKind.DEV];
            foreach (var signal in session.Signals.Where(signal => signal.IsEnabled && kinds.Contains(signal.Kind))
                         .OrderBy(signal => Array.IndexOf(kinds, signal.Kind)).ThenBy(signal => signal.Id))
            {
                var where = signal.InDeviceDb ? signal.System + " · " : "";
                list.Add($"{signal.Kind}:{signal.Id}{PortRow.Separator}{where}{signal.PlcName} · {signal.Name}");
            }
        }

        if (port.Direction == PortDirection.Input)
        {
            foreach (var (otherPlc, model) in controller.Models)
            {
                foreach (var item in model.Objects)
                {
                    var info = ObjectCatalog.Find(item.Type);
                    if (info is null)
                    {
                        continue;
                    }

                    var prefix = otherPlc.Equals(plc, StringComparison.OrdinalIgnoreCase) ? "@" + item.Name : $"@{otherPlc}/{item.Name}";
                    foreach (var output in info.Ports.Where(output => output.Direction == PortDirection.Output && output.Type == port.Type))
                    {
                        list.Add($"{prefix}.{output.Name}{PortRow.Separator}{info.Title}: {output.Description}");
                    }
                }
            }

            list.Add(port.Type == PortType.Bool ? "=1" + PortRow.Separator + "constant TRUE" : "=0" + PortRow.Separator + "constant value (edit the number)");
            if (port.Type == PortType.Bool)
            {
                list.Add("=0" + PortRow.Separator + "constant FALSE");
            }
        }

        return list;
    }
}
