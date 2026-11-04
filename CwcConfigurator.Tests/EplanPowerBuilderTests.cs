using CwcConfigurator.Services.Eplan;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class EplanPowerBuilderTests
{
    private sealed class FakeIo : ISignalIo
    {
        public Dictionary<(SimulatorSignalKind, int), double> Values { get; } = [];

        public bool TryRead(SimulatorSignalKind kind, int id, out double value) => Values.TryGetValue((kind, id), out value);

        public void Write(SimulatorSignalKind kind, int id, double value) => Values[(kind, id)] = value;
    }

    [Fact]
    public void Build_ChainsIncomerBusbarProtectionContactorAndMotor_AndTheContactorFollowsItsRelay()
    {
        var devices = new[]
        {
            new PowerDevice("-Q1", PowerDeviceKind.Breaker, "80A", "", "3", Incomer: true),
            new PowerDevice("-F53", PowerDeviceKind.MotorProtection, "20-25A", "", "5"),
            new PowerDevice("-K53", PowerDeviceKind.Contactor, "", "-F53", "5"),
            new PowerDevice("-M3", PowerDeviceKind.Motor, "11 kW", "-K53", "5"),
            new PowerDevice("-F55", PowerDeviceKind.Breaker, "25A/C", "", "6")
        };

        var objects = EplanPowerBuilder.Build(devices, "Power", new HashSet<string>(["-K53"], StringComparer.OrdinalIgnoreCase), new HashSet<string>());

        var byName = objects.ToDictionary(item => item.Name);
        Assert.Equal("@Q1.Live", byName["Busbar"].Ports["Supply1"]);                      // one busbar for the supply
        Assert.Equal("@Busbar.Feeder1", byName["F53"].Ports["Supply"]);
        Assert.Equal("@Busbar.Feeder2", byName["F55"].Ports["Supply"]);
        Assert.Equal("2", byName["Busbar"].Options["Feeders"]);
        Assert.Equal("@F53.Live", byName["K53"].Ports["Supply"]);
        Assert.Equal("@Relays.-K53", byName["K53"].Ports["Command"]);
        Assert.Equal("@K53.Live", byName["M3"].Ports["Supply"]);
        Assert.True(byName["M3"].Y > byName["K53"].Y && byName["K53"].Y > byName["F53"].Y);
        Assert.All(objects, item => Assert.Equal("Power", item.Screen));

        // Run it: the relay -K53 (from DO:7) pulls in → contactor closes → motor runs; the protection off stops it.
        var model = new ProcessModel
        {
            Objects = objects,
            RelaySheets = [new RelaySheet { Circuits = [new RelayCircuit { Elements = [new RelayElement { Source = "DO:7" }], Coil = new RelayCoil { Tag = "-K53" } }] }]
        };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        Assert.Empty(engine.Problems);
        var io = new FakeIo();
        io.Values[(SimulatorSignalKind.DO, 7)] = 1;
        for (var i = 0; i < 40; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.True(engine.Find("Intake", "M3")!.Value("Running") >= 0.5);

        byName["F53"].Options["Position"] = "Off";
        for (var i = 0; i < 80; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.True(engine.Find("Intake", "M3")!.Value("Running") < 0.5);
    }

    [Fact]
    public void Build_PutsEverySupplyOnItsOwnBusbar()
    {
        var devices = new[]
        {
            new PowerDevice("-Q1", PowerDeviceKind.Breaker, "", "", "3", Incomer: true),
            new PowerDevice("-F55", PowerDeviceKind.Breaker, "", "", "6"),
            new PowerDevice("-F11", PowerDeviceKind.Breaker, "", "", "11", Source: "L+110VDC"),
            new PowerDevice("-F10", PowerDeviceKind.Breaker, "", "-F11", "11", Source: "L+110VDC"),
            new PowerDevice("-F60", PowerDeviceKind.Breaker, "", "", "7", Source: "?")
        };

        var byName = EplanPowerBuilder.Build(devices, "Power", new HashSet<string>(), new HashSet<string>()).ToDictionary(item => item.Name);

        Assert.Equal("@Busbar.Feeder1", byName["F55"].Ports["Supply"]);
        Assert.Equal("=110", byName["BusbarL110VDC"].Ports["Supply1"]);                    // its voltage, not 1 V
        Assert.Equal("@BusbarL110VDC.Feeder1", byName["F11"].Ports["Supply"]);
        Assert.Equal("@F11.Live", byName["F10"].Ports["Supply"]);
        Assert.Equal("@BusbarNoSupply.Feeder1", byName["F60"].Ports["Supply"]);
        Assert.True(byName["F11"].Y > byName["F55"].Y && byName["F60"].Y > byName["F10"].Y); // levels one below the other
    }

    [Fact]
    public void RemovePrevious_DropsTheOldImport_AndKeepsBreakerPositions()
    {
        var model = new ProcessModel
        {
            Screens = ["Main", "Power (EPLAN)", "Power (EPLAN) 2"],
            SldScreens = ["Power (EPLAN)", "Power (EPLAN) 2"],
            Objects =
            [
                new ObjectConfig { Name = "Gate1", Type = "Gate", Screen = "Main" },
                new ObjectConfig { Name = "F53", Type = "Mcb", Screen = "Power (EPLAN)", Options = new() { ["Position"] = "Off" } },
                new ObjectConfig { Name = "F53_2", Type = "Mcb", Screen = "Power (EPLAN) 2" }
            ],
            RelaySheets = [new RelaySheet { Name = EplanPowerBuilder.OverviewSheet }, new RelaySheet { Name = "CONTROL" }],
            RelayDevices = [new RelayDevice { Tag = "-F53", Input = "@F53.On" }, new RelayDevice { Tag = "-K112", Input = "DI:3" }]
        };

        var positions = EplanPowerBuilder.RemovePrevious(model);

        Assert.Equal("Off", positions["F53"]);
        Assert.Equal(["Gate1"], model.Objects.Select(item => item.Name));
        Assert.Equal(["Main"], model.Screens);
        Assert.Empty(model.SldScreens);
        Assert.Equal(["CONTROL"], model.RelaySheets.Select(sheet => sheet.Name));
        Assert.Equal(["-K112"], model.RelayDevices.Select(device => device.Tag));
    }

    [Fact]
    public void PowerSupplyUnit_FedByItsBreaker_SuppliesItsBusbar_AndReportsOk()
    {
        var devices = new[]
        {
            new PowerDevice("-F10", PowerDeviceKind.Breaker, "RECTIFIER 16A/C", "", "11", Key: "F10", Source: "11-L+110VDC"),
            new PowerDevice("-F34", PowerDeviceKind.Breaker, "COMMUNICATION SWITCH", "", "13", Key: "F34", Source: "24-VDC L+")
        };
        var units = new[] { new PowerUnit("-U101", "RECTIFIER", "F10", "24-VDC L+", "11") };

        var objects = EplanPowerBuilder.Build(devices, "Power", new HashSet<string>(), new HashSet<string>(), out _, units);
        var byName = objects.ToDictionary(item => item.Name);
        Assert.Equal("@F10.Live", byName["U101"].Ports["Input"]);
        Assert.Equal((110.0, 24.0), (byName["U101"].Params["InputVoltage"], byName["U101"].Params["OutputVoltage"]));
        Assert.Equal("@U101.Output", byName["Busbar24VDCL"].Ports["Supply1"]);
        Assert.Equal("COMMUNICATION SWITCH", byName["F34"].Description);

        var model = new ProcessModel { Objects = objects };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["P"] = model });
        for (var i = 0; i < 5; i++)
        {
            engine.Step(0.1, _ => null);
        }

        Assert.Equal(1, engine.Find("P", "U101")!.Value("Ok"));
        Assert.Equal(24, engine.Find("P", "F34")!.Value("Live"));
        byName["F10"].Options["Position"] = "Off";
        for (var i = 0; i < 5; i++)
        {
            engine.Step(0.1, _ => null);
        }

        Assert.Equal(0, engine.Find("P", "U101")!.Value("Ok"));
        Assert.Equal(0, engine.Find("P", "F34")!.Value("Live"));
    }
}
