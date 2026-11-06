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

    [Fact]
    public void Sync_KeepsDevicesOnTheirDoor_AddsTheMissing_RemovesTheOld()
    {
        var model = new ProcessModel
        {
            Cubicles =
            [
                new Cubicle
                {
                    Name = "+1 Cubicle",
                    Items =
                    [
                        new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S1", Text = "Gate control", X = 120, Y = 200 },
                        new CubicleItem { Kind = CubicleItemKind.Lamp, Tag = "-H9", Text = "Old" },
                        new CubicleItem { Kind = CubicleItemKind.Label, Text = "GATE 1" }
                    ]
                }
            ]
        };
        var devices = new[]
        {
            new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S1", Text = "GATE 1 CONTROL" },
            new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S4" }
        };

        Assert.Equal((2, 1, 1), CubicleLayout.Sync(model, devices, "EPLAN devices"));
        var own = model.Cubicles.Single(cubicle => cubicle.Name == "+1 Cubicle");
        var s1 = own.Items.Single(item => item.Tag == "-S1");
        Assert.Equal(("Gate control", 120.0, 200.0), (s1.Text, s1.X, s1.Y));               // stays as arranged
        Assert.Contains(own.Items, item => item.IsLabel);
        Assert.DoesNotContain(own.Items, item => item.Tag == "-H9");                          // not in the project
        Assert.Equal("-S4", model.Cubicles.Single(cubicle => cubicle.Name == "EPLAN devices").Items.Single().Tag);
    }
}
