using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class RelayPlcSignalsTests
{
    [Fact]
    public void Link_ConnectsOutputRelaysInputRelaysAndTheirContacts()
    {
        var outputs = new[]
        {
            new DigitalSignalConfig { Id = 0, Config = SignalConfigState.Enabled, EplanConnectedTo = "-2K05:A1" },
            new DigitalSignalConfig { Id = 1, Config = SignalConfigState.Enabled, SignalName = "Gate 1 close command (-2K04)" },
            new DigitalSignalConfig { Id = 7, Config = SignalConfigState.Disabled, EplanConnectedTo = "-2K09:A1" }
        };
        var inputs = new[]
        {
            new DigitalSignalConfig { Id = 65, Config = SignalConfigState.Enabled, EplanConnectedTo = "-1K01:14" },
            new DigitalSignalConfig { Id = 64, Config = SignalConfigState.Enabled, EplanConnectedTo = "-1K02:14" }
        };
        var outputRelay = new RelayCircuit { Elements = [new RelayElement()], Coil = new RelayCoil { Tag = "2-K05" } };
        var inputRelay = new RelayCircuit { Elements = [new RelayElement()], Coil = new RelayCoil { Tag = "1-K01" } };
        var handSet = new RelayElement { Tag = "2-K04", Source = "DI:3" };
        var gate = new RelayCircuit
        {
            Elements = [new RelayElement { Tag = "2-K04" }, handSet, new RelayElement { Tag = "2-K05" }, new RelayElement { Tag = "2-K09" }, new RelayElement { Tag = "1-K02" }],
            Coil = new RelayCoil { Tag = "-K101" }
        };
        var model = new ProcessModel { RelaySheets = [new RelaySheet { Circuits = [outputRelay, inputRelay, gate] }] };

        var filled = RelayPlcSignals.Link(model, outputs, inputs);

        Assert.Equal(4, filled);
        Assert.Equal("DO:0", outputRelay.Elements[0].Source);   // coil of 2-K05 driven by DO:0
        Assert.Equal("DI:65", inputRelay.Coil.Outputs);         // 1-K01 drives DI:65
        Assert.Equal("", inputRelay.Elements[0].Source);        // its own supply (field contact) is set by hand
        Assert.Equal("DO:1", gate.Elements[0].Source);          // contact of 2-K04 (coil not drawn) reads DO:1
        Assert.Equal("DI:3", handSet.Source);                   // set by hand: unchanged
        Assert.Equal("", gate.Elements[2].Source);              // 2-K05 has its coil on the sheets: follows it
        Assert.Equal("", gate.Elements[3].Source);              // disabled output
        Assert.Equal("DI:64", gate.Elements[4].Source);         // contact of input relay 1-K02 (coil not drawn) reads DI:64
        Assert.Equal(0, RelayPlcSignals.Link(model, outputs, inputs)); // nothing left to fill
    }
}
