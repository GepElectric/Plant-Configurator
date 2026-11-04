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
        Assert.Equal("DO:0", outputRelay.Coil.Input);           // relay 2-K05 driven directly by DO:0
        Assert.Empty(outputRelay.Elements);                     // no contact in its path
        Assert.Equal("DI:65", inputRelay.Coil.Outputs);         // 1-K01 drives DI:65
        Assert.Equal("", inputRelay.Elements[0].Tag);           // its own supply (field contact) is set by hand
        Assert.Equal("DI:3", RelayDevices.Find(model, "2-K04")!.Input); // device 2-K04 (coil not drawn) set by hand: kept, all its contacts follow it
        Assert.Null(handSet.Source);                            // a contact has only its tag
        Assert.Null(RelayDevices.Find(model, "2-K05"));         // 2-K05 has its coil on the sheets: follows it
        Assert.Null(RelayDevices.Find(model, "2-K09"));         // disabled output
        Assert.Equal("DI:64", RelayDevices.Find(model, "1-K02")!.Input); // input relay 1-K02 (coil not drawn) reads DI:64
        Assert.Equal(0, RelayPlcSignals.Link(model, outputs, inputs)); // nothing left to fill
    }

    [Fact]
    public void Migrate_OldDoContactBecomesTheRelayInput_AndAuxiliaryContactsFollowTheRelay()
    {
        var output = new RelayCircuit { Elements = [new RelayElement { Source = "DO:160", Text = "PLC OUTPUT" }], Coil = new RelayCoil { Tag = "2-K01" } };
        var user = new RelayCircuit { Elements = [new RelayElement { Tag = "2-K01" }], Coil = new RelayCoil { Tag = "-K10" } };
        var userNc = new RelayCircuit { Elements = [new RelayElement { Tag = "2-K01", Contact = "NC" }], Coil = new RelayCoil { Tag = "-K11" } };
        var bySource = new RelayCircuit { Elements = [new RelayElement { Source = "@Relays.2-K01" }], Coil = new RelayCoil { Tag = "-K12" } };
        var model = new ProcessModel { RelaySheets = [new RelaySheet { Circuits = [user, userNc, bySource, output] }] };

        Assert.Equal(1, RelayPlcSignals.MigrateInputs(model));
        Assert.Equal("DO:160", output.Coil.Input);
        Assert.Empty(output.Elements);

        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Unit"] = model });
        Assert.Empty(engine.Problems);
        var io = new FakeIo();
        io.Values[(CwcConfigurator.Services.Simulation.SimulatorSignalKind.DO, 160)] = 1;
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsOn("Unit", "2-K01"));
        Assert.True(engine.Relays.IsOn("Unit", "-K10"));        // NO auxiliary contact closed in the same step
        Assert.False(engine.Relays.IsOn("Unit", "-K11"));       // NC auxiliary contact opened
        Assert.True(engine.Relays.IsOn("Unit", "-K12"));        // contact with source @Relays.2-K01 follows the relay too
        Assert.Equal(3, engine.Relays.ContactsOf("Unit", "2-K01").Count);

        io.Values[(CwcConfigurator.Services.Simulation.SimulatorSignalKind.DO, 160)] = 0;
        engine.Step(0.1, _ => io);
        Assert.False(engine.Relays.IsOn("Unit", "2-K01"));
        Assert.False(engine.Relays.IsOn("Unit", "-K10"));
        Assert.True(engine.Relays.IsOn("Unit", "-K11"));
    }

    private sealed class FakeIo : ISignalIo
    {
        public Dictionary<(CwcConfigurator.Services.Simulation.SimulatorSignalKind, int), double> Values { get; } = [];

        public bool TryRead(CwcConfigurator.Services.Simulation.SimulatorSignalKind kind, int id, out double value) => Values.TryGetValue((kind, id), out value);

        public void Write(CwcConfigurator.Services.Simulation.SimulatorSignalKind kind, int id, double value) => Values[(kind, id)] = value;
    }
}
