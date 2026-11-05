using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class SignalLinksTests
{
    [Fact]
    public void Links_ListObjectsRelayLogicDevicesAndCubicle()
    {
        var model = new ProcessModel
        {
            Objects = [new ObjectConfig { Name = "Gate1", Type = "Gate", Ports = new() { ["Open"] = "DI:64" } }],
            RelaySheets =
            [
                new RelaySheet
                {
                    Page = 28,
                    Circuits =
                    [
                        new RelayCircuit { Coil = new RelayCoil { Tag = "2-K01", Input = "DO:160" } },
                        new RelayCircuit { Elements = [new RelayElement { Tag = "-F53" }], Coil = new RelayCoil { Outputs = "DI:356" } }
                    ]
                }
            ],
            RelayDevices = [new RelayDevice { Tag = "-K112", Input = "DI:3" }],
            Cubicles = [new Cubicle { Items = [new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S1", Outputs = "LOCAL=DI:370;AUTO=DI:371" }] }]
        };

        var links = SignalLinks.Of(model);

        Assert.Equal(["@Gate1.Open"], links[(SimulatorSignalKind.DI, 64)]);
        Assert.Equal(["relay 2-K01 /28.1"], links[(SimulatorSignalKind.DO, 160)]);
        Assert.Equal(["PLC input /28.2"], links[(SimulatorSignalKind.DI, 356)]);
        Assert.Equal(["device -K112"], links[(SimulatorSignalKind.DI, 3)]);
        Assert.Equal(["cubicle -S1 AUTO"], links[(SimulatorSignalKind.DI, 371)]);
    }

    [Fact]
    public void Conflict_WhenMoreThanOnePlaceWritesTheSameInput()
    {
        var model = new ProcessModel
        {
            Objects =
            [
                new ObjectConfig { Name = "Logic1", Type = "Logic", Ports = new() { ["In1"] = "DI:5", ["Out"] = "DI:67" } },
                new ObjectConfig { Name = "Logic2", Type = "Logic", Ports = new() { ["Out"] = "DI:83" } }
            ],
            RelaySheets =
            [
                new RelaySheet
                {
                    Name = "DIGITAL INPUTS", Page = 21,
                    Circuits = [new RelayCircuit { Elements = [new RelayElement()], Coil = new RelayCoil { Outputs = "DI:67" } }]
                }
            ]
        };
        var signals = new[]
        {
            new SimulatorSignal { Kind = SimulatorSignalKind.DI, Module = 0, Channel = 0, ByteOffset = 0, Id = 67 },
            new SimulatorSignal { Kind = SimulatorSignalKind.DI, Module = 0, Channel = 0, ByteOffset = 0, Id = 83 },
            new SimulatorSignal { Kind = SimulatorSignalKind.DI, Module = 0, Channel = 0, ByteOffset = 0, Id = 5 }
        };

        Assert.Equal(1, SignalLinks.Apply(signals, model));
        Assert.True(signals[0].HasLinkConflict);
        Assert.Contains("PLC input /21.1", signals[0].LinkWarning);
        Assert.Contains("@Logic1.Out", signals[0].LinkWarning);
        Assert.StartsWith("⚠", signals[0].Links);
        Assert.False(signals[1].HasLinkConflict);                     // one writer
        Assert.False(signals[2].HasLinkConflict);                     // only read
        Assert.Equal(2, signals[0].LinkTargets.Count);
    }
}
