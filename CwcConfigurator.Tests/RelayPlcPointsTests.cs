using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class RelayPlcPointsTests
{
    [Fact]
    public void CardPoints_BecomePlcInputAndOutputObjects_ByTheirEplanTerminal()
    {
        var inputs = new[]
        {
            new DigitalSignalConfig { Id = 356, Config = SignalConfigState.Enabled, ModuleName = "A611", Port = 2, EplanTerminalRef = "0LPY01|A611|11", SignalName = "Dam pump MCB ON (-F53)" },
            new DigitalSignalConfig { Id = 607, Config = SignalConfigState.Enabled, ModuleName = "A611", Port = 7, EplanTerminalRef = "0LPY01|A611|16", SignalName = "SPARE" }
        };
        var outputs = new[] { new DigitalSignalConfig { Id = 160, Config = SignalConfigState.Enabled, ModuleName = "A631", Port = 0, EplanTerminalRef = "0LPY01|A631|1" } };
        var points = new Dictionary<string, EplanPlcPoint>
        {
            ["-A611:DI10"] = new("A611", "11", 10, false, "I1.2"),
            ["-A611:DI15"] = new("A611", "16", 15, false, "I1.7"),
            ["-A631:DQ0"] = new("A631", "1", 0, true, "Q0.0")
        };
        var mcb = new RelayCircuit { Elements = [new RelayElement { Tag = "-F53" }], Coil = new RelayCoil { Tag = "-A611:DI10", Text = "DI10" } };
        var spare = new RelayCircuit { Elements = [new RelayElement { Tag = "-S9" }], Coil = new RelayCoil { Tag = "-A611:DI15" } };
        var relay = new RelayCircuit { Elements = [new RelayElement { Tag = "-A631:DQ0" }], Coil = new RelayCoil { Tag = "2-K01" } };
        var model = new ProcessModel
        {
            RelaySheets = [new RelaySheet { Circuits = [mcb, spare, relay] }],
            Objects = [new ObjectConfig { Name = "F53", Type = "Mcb" }]
        };
        var missing = new List<string>();

        Assert.Equal(2, RelayPlcPoints.Resolve(model, points, inputs, outputs, missing));
        Assert.Equal(("", "DI:356"), (mcb.Coil.Tag, mcb.Coil.Outputs));                  // PLC input object
        Assert.Contains("Dam pump MCB ON", mcb.Coil.Text);
        Assert.Equal("-A611:DI15", spare.Coil.Tag);                                          // spare: set by hand
        Assert.Single(missing);
        Assert.Equal("DO:160", relay.Elements[0].Tag);

        RelayPlcSignals.Link(model, outputs, inputs);
        Assert.Equal("DO:160", relay.Coil.Input);                                            // relay driven by its DO
        Assert.Equal(1, RelayPlcSignals.LinkPowerObjects(model));
        Assert.Equal("@F53.On", RelayDevices.Find(model, "-F53")!.Input);                    // MCB aux contact follows the MCB
    }
}
