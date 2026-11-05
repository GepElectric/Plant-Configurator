using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class LogicObjectTests
{
    private sealed class FakeIo : ISignalIo
    {
        public Dictionary<(SimulatorSignalKind, int), double> Values { get; } = [];

        public bool TryRead(SimulatorSignalKind kind, int id, out double value) => Values.TryGetValue((kind, id), out value);

        public void Write(SimulatorSignalKind kind, int id, double value) => Values[(kind, id)] = value;
    }

    private static (ProcessEngine Engine, FakeIo Io) Build(ObjectConfig logic)
    {
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["P"] = new() { Objects = [logic] } });
        Assert.Empty(engine.Problems);
        return (engine, new FakeIo());
    }

    private static bool Out(ProcessEngine engine, FakeIo io)
    {
        engine.Step(0.1, _ => io);
        return engine.Find("P", "L")!.Value("Out") >= 0.5;
    }

    [Fact]
    public void And_WithSixInputs_OneInverted()
    {
        var logic = new ObjectConfig { Name = "L", Type = "Logic", Options = new() { ["Function"] = "And", ["Inputs"] = "6", ["In6Mode"] = "Inverted" } };
        for (var i = 1; i <= 6; i++)
        {
            logic.Ports[$"In{i}"] = $"DO:{i}";
        }

        var (engine, io) = Build(logic);
        for (var i = 1; i <= 5; i++)
        {
            io.Values[(SimulatorSignalKind.DO, i)] = 1;
        }

        Assert.True(Out(engine, io));                       // In1..In5 TRUE, In6 FALSE inverted → TRUE
        io.Values[(SimulatorSignalKind.DO, 6)] = 1;
        Assert.False(Out(engine, io));                      // In6 TRUE inverted → FALSE
        io.Values[(SimulatorSignalKind.DO, 6)] = 0;
        io.Values[(SimulatorSignalKind.DO, 3)] = 0;
        Assert.False(Out(engine, io));
    }

    [Fact]
    public void Or_InvertedInput_AndEcho_InvertedIn1()
    {
        var or = new ObjectConfig { Name = "L", Type = "Logic", Options = new() { ["Function"] = "Or", ["Inputs"] = "2", ["In2Mode"] = "Inverted" }, Ports = new() { ["In1"] = "DO:1", ["In2"] = "DO:2" } };
        var (engine, io) = Build(or);
        Assert.True(Out(engine, io));                       // In2 FALSE inverted
        io.Values[(SimulatorSignalKind.DO, 2)] = 1;
        Assert.False(Out(engine, io));

        var echo = new ObjectConfig { Name = "L", Type = "Logic", Options = new() { ["Function"] = "Echo", ["In1Mode"] = "Inverted" }, Ports = new() { ["In1"] = "DO:1" } };
        (engine, io) = Build(echo);
        Assert.True(Out(engine, io));
        io.Values[(SimulatorSignalKind.DO, 1)] = 1;
        Assert.False(Out(engine, io));
    }

    [Fact]
    public void OlderModel_WithoutInputsOption_UsesItsConnectedInputs()
    {
        // Loaded from a file saved before the Inputs option existed.
        var yaml = ProcessModelStore.ToYaml(new Dictionary<string, ProcessModel>
        {
            ["P"] = new() { Objects = [new ObjectConfig { Name = "L", Type = "Logic", Options = new() { ["Function"] = "And" }, Ports = new() { ["In1"] = "DO:1", ["In4"] = "DO:4" } }] }
        });
        var logic = ProcessModelStore.FromYaml(yaml)["P"].Objects.Single();
        Assert.Equal("4", logic.Options["Inputs"]);
        var (engine, io) = Build(logic);
        io.Values[(SimulatorSignalKind.DO, 1)] = 1;
        Assert.False(Out(engine, io));                      // In4 still counts
        io.Values[(SimulatorSignalKind.DO, 4)] = 1;
        Assert.True(Out(engine, io));
    }
}
