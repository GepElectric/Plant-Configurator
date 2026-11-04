using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class CubicleLayoutTests
{
    private sealed class FakeIo : ISignalIo
    {
        public Dictionary<(SimulatorSignalKind, int), double> Values { get; } = [];

        public bool TryRead(SimulatorSignalKind kind, int id, out double value) => Values.TryGetValue((kind, id), out value);

        public void Write(SimulatorSignalKind kind, int id, double value) => Values[(kind, id)] = value;
    }

    [Fact]
    public void ImportedDevices_GoOnOneDoorOnce_AndTheLampFollowsItsPath()
    {
        var model = new ProcessModel
        {
            RelaySheets = [new RelaySheet { Page = 45, Circuits = [new RelayCircuit { Elements = [new RelayElement { Tag = "-S210" }], Coil = new RelayCoil { Tag = "-H104:LAMP" } }] }],
            Cubicles = [new Cubicle { Name = "+A1", Items = [new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S1" }] }]
        };
        var devices = new[]
        {
            new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S1" },                       // already on a door
            new CubicleItem { Kind = CubicleItemKind.Lamp, Tag = "-H104", Source = "-H104:LAMP", Color = "Green" },
            new CubicleItem { Kind = CubicleItemKind.PushButton, Tag = "-S210" },
            new CubicleItem { Kind = CubicleItemKind.EmergencyStop, Tag = "-S301", Contact = "NC" }
        };

        Assert.Equal(3, CubicleLayout.AddDevices(model, devices, "EPLAN devices"));
        Assert.Equal(0, CubicleLayout.AddDevices(model, devices, "EPLAN devices"));
        var door = model.Cubicles.Single(cubicle => cubicle.Name == "EPLAN devices");
        Assert.Equal(["-S210", "-S301", "-H104"], door.Items.Select(item => item.Tag));
        Assert.True(door.Items[2].Y > door.Items[0].Y);                                       // lamps in their own row

        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();
        door.Items[0].Pressed = true;                                                          // the push button -S210
        engine.Step(0.1, _ => io);
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsLampOn(door.Items[2]));
    }
}
