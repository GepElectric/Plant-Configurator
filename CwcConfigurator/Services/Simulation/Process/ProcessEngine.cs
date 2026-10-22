namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>Access to the simulator signals of one connected PLC (one value per signal kind and ID).</summary>
public interface ISignalIo
{
    /// <summary>DO/DI as 0/1, AO/AI/RTD in engineering units, COUNTER as the Real.</summary>
    bool TryRead(SimulatorSignalKind kind, int id, out double value);

    void Write(SimulatorSignalKind kind, int id, double value);
}

/// <summary>
/// Base of every simulator object. The engine fills the input port values, calls <see cref="Step"/> and then
/// passes the output port values on to links and signals.
/// </summary>
public abstract class SimObject
{
    private readonly Dictionary<string, double> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _connected = new(StringComparer.OrdinalIgnoreCase);

    public ObjectConfig Config { get; internal set; } = new();
    public ObjectTypeInfo Info { get; internal set; } = null!;
    public string Name => Config.Name;

    /// <summary>PLC database the object belongs to (its signals are that PLC's DB30).</summary>
    public string Plc { get; internal set; } = "";

    public string QualifiedName => Plc + "/" + Name;

    /// <summary>Output values forced from the SCADA view (port → value).</summary>
    public Dictionary<string, double> Forces { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, double> Values => _values;

    protected Random Random { get; } = new();

    public double Value(string port) => _values.TryGetValue(port, out var value) ? value : 0;

    /// <summary>Called once after the object is created and whenever the simulation is reset.</summary>
    public virtual void Reset()
    {
    }

    public abstract void Step(double dt);

    internal void SetInput(string port, double value, bool connected)
    {
        _values[port] = value;
        if (connected)
        {
            _connected.Add(port);
        }
        else
        {
            _connected.Remove(port);
        }
    }

    internal void ApplyForces()
    {
        foreach (var (port, value) in Forces)
        {
            _values[port] = value;
        }
    }

    protected double In(string port) => Value(port);

    protected bool InB(string port) => Value(port) >= 0.5;

    /// <summary>True when the input is bound to a signal, link or constant.</summary>
    protected bool Connected(string port) => _connected.Contains(port);

    protected void Out(string port, double value) => _values[port] = double.IsFinite(value) ? value : 0;

    protected void Out(string port, bool value) => _values[port] = value ? 1 : 0;

    public double P(string name)
    {
        if (Config.Params.TryGetValue(name, out var value))
        {
            return value;
        }

        return Info.Params.FirstOrDefault(param => param.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Default ?? 0;
    }

    public string Opt(string name)
    {
        var def = Info.Options.FirstOrDefault(option => option.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Config.Options.TryGetValue(name, out var value) && def is not null
            && def.Values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return def.Values.First(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        return def?.Default ?? "";
    }

    protected bool OptIs(string name, string value) => Opt(name).Equals(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Table points (configured, else the type default). X then Y.</summary>
    public (IReadOnlyList<double> X, IReadOnlyList<double> Y) Table(string name)
    {
        var def = Info.Tables.First(table => table.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Config.Tables.TryGetValue(name + "X", out var x) && Config.Tables.TryGetValue(name + "Y", out var y)
            && x.Count >= 2 && x.Count == y.Count)
        {
            return (x, y);
        }

        return (def.DefaultX, def.DefaultY);
    }

    protected double Noise(double amplitude) => amplitude == 0 ? 0 : (Random.NextDouble() * 2 - 1) * amplitude;
}

/// <summary>
/// Runs the plant: the models of all PLC databases together, so objects can be linked across PLCs
/// ("@Common/Reservoir.Level"). Objects are ordered by their links; a link to an object later in the order
/// (a loop) sees the value of the previous step. Each object reads and writes the signals of its own PLC.
/// </summary>
public sealed class ProcessEngine
{
    private readonly List<(SimObject Object, List<(PortDef Port, PortBinding Binding)> Inputs, List<(PortDef Port, List<PortBinding> Targets)> Outputs)> _plan = [];
    private readonly Dictionary<string, SimObject> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="previous">Objects of a previous engine with the same PLC, name and type are reused, so
    /// changing the model keeps the running state (positions, pressures, speed).</param>
    public ProcessEngine(IReadOnlyDictionary<string, ProcessModel> models, ProcessEngine? previous = null)
    {
        Models = models;
        var objects = new List<SimObject>();
        foreach (var (plc, model) in models)
        {
            foreach (var config in model.Objects)
            {
                var info = ObjectCatalog.Find(config.Type);
                if (info is null)
                {
                    Problems.Add($"{plc}/{config.Name}: unknown object type '{config.Type}'.");
                    continue;
                }

                var reused = previous?.Find(plc, config.Name) is { } old && old.Info == info && !objects.Contains(old) ? old : null;
                var instance = reused ?? info.Create();
                instance.Config = config;
                instance.Info = info;
                instance.Plc = plc;
                if (reused is null)
                {
                    instance.Reset();
                }
                if (!_byName.TryAdd(instance.QualifiedName, instance))
                {
                    Problems.Add($"{instance.QualifiedName}: duplicate object name.");
                    continue;
                }

                objects.Add(instance);
            }
        }

        Objects = Order(objects);
        foreach (var instance in Objects)
        {
            var inputs = new List<(PortDef, PortBinding)>();
            var outputs = new List<(PortDef, List<PortBinding>)>();
            foreach (var port in instance.Info.Ports)
            {
                instance.Config.Ports.TryGetValue(port.Name, out var text);
                if (port.Direction == PortDirection.Output)
                {
                    // An output can drive several signals: "DI:13, DI:46".
                    var targets = new List<PortBinding>();
                    foreach (var target in PortBinding.ParseList(text))
                    {
                        if (target.Kind != BindingKind.Signal)
                        {
                            continue;
                        }

                        if (target.SignalKind is SimulatorSignalKind.DO or SimulatorSignalKind.AO)
                        {
                            Problems.Add($"{instance.QualifiedName}.{port.Name}: {target} is written by the PLC and cannot be an output.");
                            continue;
                        }

                        targets.Add(target);
                    }

                    outputs.Add((port, targets));
                    continue;
                }

                var binding = PortBinding.Parse(text);
                if (binding.Kind == BindingKind.Link && Source(instance, binding) is null)
                {
                    Problems.Add($"{instance.QualifiedName}.{port.Name}: linked object '{binding.LinkObject}' does not exist.");
                    binding = PortBinding.None;
                }

                inputs.Add((port, binding));
                instance.SetInput(port.Name, port.Default, binding.Kind != BindingKind.None);
            }

            _plan.Add((instance, inputs, outputs));
        }
    }

    public IReadOnlyDictionary<string, ProcessModel> Models { get; }

    public IReadOnlyList<SimObject> Objects { get; }

    public List<string> Problems { get; } = [];

    public SimObject? Find(string plc, string name) => _byName.TryGetValue(plc + "/" + name, out var item) ? item : null;

    public void Reset()
    {
        foreach (var instance in Objects)
        {
            instance.Forces.Clear();
            instance.Reset();
        }
    }

    /// <summary>One simulation step. <paramref name="io"/> gives the signal access of a PLC (null = not connected).</summary>
    public void Step(double dt, Func<string, ISignalIo?> io)
    {
        foreach (var (instance, inputs, outputs) in _plan)
        {
            var plcIo = io(instance.Plc);
            foreach (var (port, binding) in inputs)
            {
                instance.SetInput(port.Name, ReadBinding(instance, binding, port, plcIo), binding.Kind != BindingKind.None);
            }

            instance.Step(dt);
            instance.ApplyForces();
            if (plcIo is null)
            {
                continue;
            }

            foreach (var (port, targets) in outputs)
            {
                foreach (var target in targets)
                {
                    plcIo.Write(target.SignalKind, target.SignalId, instance.Value(port.Name));
                }
            }
        }
    }

    private SimObject? Source(SimObject instance, PortBinding link)
    {
        var name = link.LinkObject.Contains('/') ? link.LinkObject : instance.Plc + "/" + link.LinkObject;
        return _byName.TryGetValue(name, out var source) ? source : null;
    }

    private double ReadBinding(SimObject instance, PortBinding binding, PortDef port, ISignalIo? io)
    {
        switch (binding.Kind)
        {
            case BindingKind.Constant:
                return binding.Constant;
            case BindingKind.Link:
                return Source(instance, binding) is { } source ? source.Value(binding.LinkPort) : port.Default;
            case BindingKind.Signal:
                return io is not null && io.TryRead(binding.SignalKind, binding.SignalId, out var value) ? value : port.Default;
            default:
                return port.Default;
        }
    }

    /// <summary>Objects whose outputs feed other objects come first (depth-first topological order).</summary>
    private List<SimObject> Order(List<SimObject> objects)
    {
        var ordered = new List<SimObject>();
        var state = new HashSet<SimObject>();

        void Visit(SimObject item)
        {
            if (!state.Add(item))
            {
                return; // already placed or in progress (loop: the link sees the previous step)
            }

            foreach (var port in item.Info.Ports.Where(port => port.Direction == PortDirection.Input))
            {
                if (item.Config.Ports.TryGetValue(port.Name, out var text)
                    && PortBinding.Parse(text) is { Kind: BindingKind.Link } link
                    && Source(item, link) is { } source)
                {
                    Visit(source);
                }
            }

            ordered.Add(item);
        }

        foreach (var item in objects)
        {
            Visit(item);
        }

        return ordered;
    }
}

/// <summary>Small numeric helpers shared by the objects.</summary>
public static class SimMath
{
    public const double G = 9.81;
    public const double WaterDensity = 1000;

    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    /// <summary>First-order lag: value approaches target with time constant tau (exact for a constant target).</summary>
    public static double Lag(double value, double target, double tau, double dt) =>
        tau <= 0 ? target : target + (value - target) * Math.Exp(-dt / tau);

    public static double RateLimit(double value, double target, double rateUp, double rateDown, double dt) =>
        value + Clamp(target - value, -rateDown * dt, rateUp * dt);

    /// <summary>Piecewise-linear interpolation; holds the end values outside the table.</summary>
    public static double Interpolate(IReadOnlyList<double> x, IReadOnlyList<double> y, double value)
    {
        if (x.Count == 0)
        {
            return value;
        }

        if (value <= x[0])
        {
            return y[0];
        }

        for (var index = 1; index < x.Count; index++)
        {
            if (value <= x[index])
            {
                var x0 = x[index - 1];
                var x1 = x[index];
                return x1 == x0 ? y[index] : y[index - 1] + (value - x0) * (y[index] - y[index - 1]) / (x1 - x0);
            }
        }

        return y[^1];
    }

    /// <summary>On/off with hysteresis: on at or above <paramref name="on"/>, off below <paramref name="off"/> (or the mirror for on &lt; off).</summary>
    public static bool Hysteresis(bool state, double value, double on, double off) =>
        on >= off ? (state ? value > off : value >= on) : (state ? value < off : value <= on);

    /// <summary>Bar ↔ metres of water column.</summary>
    public static double BarToMeters(double bar) => bar * 1e5 / (WaterDensity * G);

    public static double MetersToBar(double meters) => meters * WaterDensity * G / 1e5;
}
