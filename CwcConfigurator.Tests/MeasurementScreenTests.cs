using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class MeasurementScreenTests
{
    private static SimulatorSignal Signal(SimulatorSignalKind kind, int id, string system, string plcName, string name = "", int offset = 0) => new()
    {
        Kind = kind, Module = 0, Channel = 0, Id = id, ByteOffset = offset, System = system, PlcName = plcName, Name = name,
        Config = CwcConfigurator.Domain.SignalConfigState.Enabled
    };

    [Fact]
    public void Build_AllMeasurementsGroupedByDevice_ShownWhenTheModelWritesThem()
    {
        var model = new ProcessModel { Objects = [new ObjectConfig { Name = "Reservoir", Type = "Reservoir", Ports = new() { ["Level"] = "AI:18" } }] };
        var signals = new[]
        {
            Signal(SimulatorSignalKind.AI, 18, "INTAKE", "WATER_LEVEL_1", "Water level"),
            Signal(SimulatorSignalKind.AI, 19, "INTAKE", "WATER_LEVEL_2", "Water level 2"),
            Signal(SimulatorSignalKind.AI, 20, "INTAKE", "SPARE", "SPARE"),
            Signal(SimulatorSignalKind.DEV, 48, "GEN_SENTRON", "I_1", "MEAS[0] → Phase current 1", 2),
            Signal(SimulatorSignalKind.DEV, 49, "GEN_SENTRON", "I_2", "MEAS[1] → Phase current 2", 6),
            Signal(SimulatorSignalKind.DEV, 216, "AUX_SENTRON", "I_1", "MEAS[0] → Phase current 1", 84)
        };

        Assert.Equal(4, MeasurementScreen.Build(model, signals, new HashSet<string> { "GEN_SENTRON" }));
        var screen = model.Objects.Where(item => item.Screen == MeasurementScreen.Screen).ToList();
        Assert.Contains(MeasurementScreen.Screen, model.Screens);
        Assert.Equal(["INTAKE", "INTAKE", "GEN_SENTRON", "GEN_SENTRON"], screen.Select(item => item.Group));
        var level = screen.Single(item => item.Name == "WATER_LEVEL_1");
        Assert.Equal(("Display", "AI:18"), (level.Options["Mode"], level.Ports["Measured"]));   // the reservoir writes it
        Assert.Equal("AI:19", screen.Single(item => item.Name == "WATER_LEVEL_2").Ports["Out"]);  // simulated here
        var current = screen.Single(item => item.Name == "GEN_SENTRON_I_1");
        Assert.StartsWith("Phase current 1", current.Description);
        Assert.True(screen.Single(item => item.Name == "GEN_SENTRON_I_2").Y > level.Y);          // a device starts its own row

        Assert.Equal(4, MeasurementScreen.Build(model, signals, new HashSet<string> { "GEN_SENTRON" }));
        Assert.Equal(4, model.Objects.Count(item => item.Type == "Measurement"));                 // rebuilt, not doubled
    }

    [Fact]
    public void Measurement_SimulatesItsValue()
    {
        var model = new ProcessModel { Objects = [new ObjectConfig { Name = "M", Type = "Measurement", Params = new() { ["Value"] = 12.5 } }] };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["P"] = model });
        Assert.Empty(engine.Problems);
        engine.Step(0.1, _ => null);
        Assert.Equal(12.5, engine.Find("P", "M")!.Value("Shown"));
    }
}
