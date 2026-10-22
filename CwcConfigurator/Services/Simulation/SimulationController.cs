using System.IO;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// The plant simulation: the PLC sessions, the plant model of every PLC database (saved next to the project)
/// and the engine that runs them together. The window calls <see cref="StepAsync"/> from its timer.
/// </summary>
public sealed class SimulationController
{
    /// <param name="models">Models already loaded (from a .plantsim package); null = read <paramref name="modelPath"/>.</param>
    public SimulationController(IReadOnlyList<PlcSession> sessions, string? modelPath, Dictionary<string, ProcessModel>? models = null)
    {
        Sessions = sessions;
        ModelPath = modelPath;
        Models = models is not null
            ? new Dictionary<string, ProcessModel>(models, StringComparer.OrdinalIgnoreCase)
            : modelPath is null || SimulationPackage.IsPackage(modelPath) ? new(StringComparer.OrdinalIgnoreCase) : ProcessModelStore.Load(modelPath);
        foreach (var session in sessions)
        {
            Models.TryAdd(session.Name, new ProcessModel());
        }

        Engine = new ProcessEngine(Models);
    }

    public IReadOnlyList<PlcSession> Sessions { get; }

    /// <summary>The model file in use ("&lt;project&gt;.simulator.yaml" or one opened / saved as); null = not chosen yet.</summary>
    public string? ModelPath { get; private set; }

    /// <summary>Raised when the model file or its saved state changes (for the title / file display).</summary>
    public event Action? FileStateChanged;

    public Dictionary<string, ProcessModel> Models { get; }

    public ProcessEngine Engine { get; private set; }

    /// <summary>Writes a ".plantsim" package (project + model + connection settings); set by the window.</summary>
    public Action<string>? PackageWriter { get; set; }

    public bool Running { get; set; }

    public double SimulatedTime { get; private set; }

    public bool Dirty { get; private set; }

    /// <summary>Raised after the model structure changed (objects, types, ports) and the engine was rebuilt.</summary>
    public event Action? ModelChanged;

    /// <summary>Raised when only the presentation changed (groups); the SCADA redraws, the engine is untouched.</summary>
    public event Action? LayoutChanged;

    public void NotifyLayoutChanged() => LayoutChanged?.Invoke();

    public PlcSession? Session(string plc) => Sessions.FirstOrDefault(session => session.Name.Equals(plc, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies structural changes (objects added/removed/renamed, ports, options); running state is kept.</summary>
    public void Rebuild()
    {
        Engine = new ProcessEngine(Models, Engine);
        MarkDirty();
        ModelChanged?.Invoke();
    }

    /// <summary>Parameter changes apply immediately (objects read their parameters every step); only marks the model dirty.</summary>
    public void MarkDirty()
    {
        if (!Dirty)
        {
            Dirty = true;
            FileStateChanged?.Invoke();
        }
    }

    public void Reset()
    {
        Engine.Reset();
        SimulatedTime = 0;
    }

    public void Save()
    {
        if (ModelPath is null)
        {
            throw new InvalidOperationException("Save the Plant Configurator project first; the plant model is stored next to it.");
        }

        Write(ModelPath);
        Dirty = false;
        FileStateChanged?.Invoke();
    }

    /// <summary>Saves the model to another file and keeps working on that file.</summary>
    public void SaveAs(string path)
    {
        Write(path);
        ModelPath = path;
        Dirty = false;
        FileStateChanged?.Invoke();
    }

    private void Write(string path)
    {
        if (SimulationPackage.IsPackage(path))
        {
            (PackageWriter ?? throw new InvalidOperationException("Saving a simulation package is not available here."))(path);
        }
        else
        {
            ProcessModelStore.Save(path, Models);
        }
    }

    /// <summary>
    /// Opens a model file and continues working on it. Returns the PLC names in the file that have no PLC
    /// database in this project (their objects are kept in the file but not shown or run).
    /// </summary>
    public List<string> Open(string path)
    {
        var loaded = SimulationPackage.IsPackage(path) ? SimulationPackage.Load(path).Models : ProcessModelStore.Load(path);
        Models.Clear();
        foreach (var (plc, model) in loaded)
        {
            Models[plc] = model;
        }

        foreach (var session in Sessions)
        {
            Models.TryAdd(session.Name, new ProcessModel());
        }

        ModelPath = path;
        Engine = new ProcessEngine(Models, Engine);
        Dirty = false;
        ModelChanged?.Invoke();
        FileStateChanged?.Invoke();
        return loaded.Keys.Where(plc => Session(plc) is null && loaded[plc].Objects.Count > 0).ToList();
    }

    public bool Reload()
    {
        if (ModelPath is null || !File.Exists(ModelPath))
        {
            return false;
        }

        var loaded = SimulationPackage.IsPackage(ModelPath) ? SimulationPackage.Load(ModelPath).Models : ProcessModelStore.Load(ModelPath);
        Models.Clear();
        foreach (var (plc, model) in loaded)
        {
            Models[plc] = model;
        }

        foreach (var session in Sessions)
        {
            Models.TryAdd(session.Name, new ProcessModel());
        }

        Engine = new ProcessEngine(Models, Engine);
        Dirty = false;
        ModelChanged?.Invoke();
        FileStateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// One cycle: read every connected PLC, step the model (also without a PLC, for testing the model), write the
    /// model outputs to the PLCs.
    /// </summary>
    /// <returns>Communication errors of this cycle; the failing PLC is disconnected.</returns>
    public async Task<List<string>> StepAsync(double dt)
    {
        var errors = new List<string>();
        foreach (var session in Sessions.Where(session => session.IsConnected))
        {
            await Guard(session, session.ReadAsync, errors);
        }

        if (Running)
        {
            SimulatedTime += dt;
            Engine.Step(dt, plc => Session(plc) is { IsConnected: true } session ? session : null);
        }

        // Forces in the signal list are written every cycle and override the model.
        foreach (var session in Sessions.Where(session => session.IsConnected))
        {
            if (session.HasForcedOutputs)
            {
                session.ApplyForces();
            }

            await Guard(session, session.FlushAsync, errors);
        }

        return errors;
    }

    private static async Task Guard(PlcSession session, Func<Task> action, List<string> errors)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            session.Disconnect();
            errors.Add($"{session.Name}: {ex.Message} (disconnected)");
        }
    }
}
