using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class PlcInputSourcesTests
{
    private sealed class FakeIo : ISignalIo
    {
        public Dictionary<(SimulatorSignalKind, int), double> Values { get; } = [];

        public bool TryRead(SimulatorSignalKind kind, int id, out double value) => Values.TryGetValue((kind, id), out value);

        public void Write(SimulatorSignalKind kind, int id, double value) => Values[(kind, id)] = value;
    }

    private static RelayCircuit PlcInput(string drives, string tag = "") => new()
    {
        Elements = [new RelayElement { Tag = tag, Text = "SUPPLY ?\n(set the source)" }],
        Coil = new RelayCoil { Outputs = drives, Text = "DI9" }
    };

    [Fact]
    public void PlcInputWithoutSource_DoesNotBlockTheDi()
    {
        var model = new ProcessModel { RelaySheets = [new RelaySheet { Name = "DI", Page = 21, Circuits = [PlcInput("DI:67")] }] };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();
        io.Values[(SimulatorSignalKind.DI, 67)] = 1;   // e.g. written by an object before the relay logic
        engine.Step(0.1, _ => io);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 67)]);   // not overwritten with 0
    }

    [Fact]
    public void ObjectMappedToThePlcInput_BecomesTheSourceOfItsContact()
    {
        var logic = new ObjectConfig { Name = "Logic1", Type = "Logic", Ports = new() { ["In1"] = "=1", ["Out"] = "DI:67, DI:5" } };
        var circuit = PlcInput("DI:67");
        var model = new ProcessModel { Objects = [logic], RelaySheets = [new RelaySheet { Name = "DIGITAL INPUTS", Page = 21, Circuits = [circuit] }] };

        var notices = PlcInputSources.Assign(model);

        Assert.Equal("@Logic1.Out", circuit.Elements[0].Tag);
        Assert.Equal("", circuit.Elements[0].Text);
        Assert.Equal("DI:5", logic.Ports["Out"]);                       // the other target stays
        Assert.Contains("source of the PLC input /21.1", Assert.Single(notices).Text);

        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();
        engine.Step(0.1, _ => io);
        engine.Step(0.1, _ => io);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 67)]);        // the DI follows Logic1 through the relay logic
        Assert.Empty(PlcInputSources.Assign(model));                     // nothing left to do
    }

    [Fact]
    public void ThroughARelayWithoutSource_TheSourceGoesToTheRelaysPath()
    {
        // /27.7: 1-K14 contact → DI:377; /27.8: "SUPPLY ?" → coil 1-K14 (the external signal comes in there).
        var input = new RelayCircuit { Elements = [new RelayElement { Tag = "1-K14" }], Coil = new RelayCoil { Outputs = "DI:377" } };
        var relay = PlcInput("");
        relay.Coil = new RelayCoil { Tag = "1-K14" };
        var level = new ObjectConfig { Name = "Drainage water level", Type = "Logic", Ports = new() { ["In1"] = "=1", ["Out"] = "DI:377" } };
        var model = new ProcessModel { Objects = [level], RelaySheets = [new RelaySheet { Page = 27, Circuits = [input, relay] }] };

        Assert.Equal(2, RelayLogicRunner.Unsourced(model).Count);           // neither writes before a source is given
        var notice = Assert.Single(PlcInputSources.Assign(model)).Text;

        Assert.Equal("@Drainage water level.Out", relay.Elements[0].Tag);
        Assert.Equal("1-K14", input.Elements[0].Tag);
        Assert.Contains("through relay 1-K14 /27.2", notice);
        Assert.Empty(RelayLogicRunner.Unsourced(model));

        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();
        engine.Step(0.1, _ => io);
        engine.Step(0.1, _ => io);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 377)]);
    }

    [Fact]
    public void ThroughADeviceWithoutInput_TheSourceBecomesItsInput()
    {
        var input = new RelayCircuit { Elements = [new RelayElement { Tag = "-B12" }], Coil = new RelayCoil { Outputs = "DI:40" } };
        var device = new RelayDevice { Tag = "-B12", Input = "" };
        var level = new ObjectConfig { Name = "Level", Type = "Logic", Ports = new() { ["Out"] = "DI:40" } };
        var model = new ProcessModel { Objects = [level], RelaySheets = [new RelaySheet { Page = 5, Circuits = [input] }], RelayDevices = [device] };

        PlcInputSources.Assign(model);

        Assert.Equal("@Level.Out", device.Input);
        Assert.False(level.Ports.ContainsKey("Out"));
    }

    [Fact]
    public void MappingToAPlcInputWithASource_IsRefused()
    {
        var logic = new ObjectConfig { Name = "Logic1", Type = "Logic", Ports = new() { ["Out"] = "DI:67" } };
        var circuit = PlcInput("DI:67", "-K12");
        var model = new ProcessModel { Objects = [logic], RelaySheets = [new RelaySheet { Page = 21, Circuits = [circuit] }] };

        var notices = PlcInputSources.Assign(model);

        Assert.Equal("-K12", circuit.Elements[0].Tag);
        Assert.False(logic.Ports.ContainsKey("Out"));
        Assert.Contains("not connected", Assert.Single(notices).Text);
        Assert.True(notices[0].Refused);
        Assert.Same(circuit.Coil, notices[0].Item);
    }

    [Fact]
    public void CubiclePushButtonMappedToThePlcInput_BecomesItsContact()
    {
        var circuit = PlcInput("DI:70");
        var button = new CubicleItem { Kind = CubicleItemKind.PushButton, Tag = "-S1", Contact = "NC", Outputs = "DI:70" };
        var model = new ProcessModel { RelaySheets = [new RelaySheet { Page = 21, Circuits = [circuit] }], Cubicles = [new Cubicle { Items = [button] }] };

        PlcInputSources.Assign(model);

        Assert.Equal("-S1", circuit.Elements[0].Tag);
        Assert.Equal("NC", circuit.Elements[0].Contact);
        Assert.Equal("", button.Outputs);
    }
}
