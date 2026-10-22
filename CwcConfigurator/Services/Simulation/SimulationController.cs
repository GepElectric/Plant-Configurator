using System.IO;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// The plant simulation: the PLC sessions, the plant model of every PLC database (saved next to the project)
/// and the engine that runs them together. The window calls <see cref="StepAsync"/> from its timer.
/// </summary>
public sealed class SimulationController
{
    public SimulationController(IReadOnlyList<PlcSession> sessions, string? modelPath)
    {
        Sessions = sessions;
        ModelPath = modelPath;
        Models = modelPath is null ? new(StringComparer.OrdinalIgnoreCase) : ProcessModelStore.Load(modelPath);
        foreach (var session in sessions)
        {
            Models.TryAdd(session.Name, new ProcessModel());
        }

        Engine = new ProcessEngine(Models);
    }

    public IReadOnlyList<PlcSession> Sessions { get; }

    /// <summary>"&lt;project&gt;.simulator.yaml"; null when the project has not been saved yet.</summary>
    public string? ModelPath { get; }

    public Dictionary<string, ProcessModel> Models { get; }

    public ProcessEngine Engine { get; private set; }

    public bool Running { get; set; }

    public double SimulatedTime { get; private set; }

    public bool Dirty { get; private set; }

    /// <summary>Raised after the model structure changed (objects, types, ports) and the engine was rebuilt.</summary>
    public event Action? ModelChanged;

    public PlcSession? Session(string plc) => Sessions.FirstOrDefault(session => session.Name.Equals(plc, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies structural changes (objects added/removed/renamed, ports, options); running state is kept.</summary>
    public void Rebuild()
    {
        Engine = new ProcessEngine(Models, Engine);
        Dirty = true;
        ModelChanged?.Invoke();
    }

    /// <summary>Parameter changes apply immediately (objects read their parameters every step); only marks the model dirty.</summary>
    public void MarkDirty() => Dirty = true;

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

        ProcessModelStore.Save(ModelPath, Models);
        Dirty = false;
    }

    public bool Reload()
    {
        if (ModelPath is null || !File.Exists(ModelPath))
        {
            return false;
        }

        var loaded = ProcessModelStore.Load(ModelPath);
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

        if (!Running)
        {
            return errors;
        }

        SimulatedTime += dt;
        Engine.Step(dt, plc => Session(plc) is { IsConnected: true } session ? session : null);
        foreach (var session in Sessions.Where(session => session.IsConnected))
        {
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
