using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class RelayLogicTests
{
    private sealed class FakeIo : ISignalIo
    {
        public Dictionary<(SimulatorSignalKind, int), double> Values { get; } = [];

        public bool TryRead(SimulatorSignalKind kind, int id, out double value) => Values.TryGetValue((kind, id), out value);

        public void Write(SimulatorSignalKind kind, int id, double value) => Values[(kind, id)] = value;
    }

    private static RelayElement Contact(string tag, string contact = "NO", string source = "") =>
        new() { Tag = tag, Contact = contact, Source = source };

    private static RelayElement Parallel(params List<RelayElement>[] branches) =>
        new() { Kind = RelayElementKind.Parallel, Branches = branches.ToList() };

    private static (ProcessEngine Engine, FakeIo Io) Build(params RelayCircuit[] circuits)
    {
        var model = new ProcessModel { RelaySheets = [new RelaySheet { Name = "Gate 1", Page = 37, Circuits = circuits.ToList() }] };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        return (engine, new FakeIo());
    }

    [Fact]
    public void SelfHoldingRelay_StartsWithPushButtonAndHoldsUntilStop()
    {
        // -K1: (start DO:1 NO || -K1 NO) and stop DO:2 NC; -K1 drives DI:5.
        var circuit = new RelayCircuit
        {
            Elements =
            [
                Parallel([Contact("-S1", source: "DO:1")], [Contact("-K1")]),
                Contact("-S2", "NC", "DO:2")
            ],
            Coil = new RelayCoil { Tag = "-K1", Outputs = "DI:5" }
        };
        var (engine, io) = Build(circuit);

        engine.Step(0.1, _ => io);
        Assert.False(engine.Relays.IsOn("Intake", "-K1"));

        io.Values[(SimulatorSignalKind.DO, 1)] = 1;
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsOn("Intake", "-K1"));
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 5)]);

        io.Values[(SimulatorSignalKind.DO, 1)] = 0;
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsOn("Intake", "-K1"));

        io.Values[(SimulatorSignalKind.DO, 2)] = 1;
        engine.Step(0.1, _ => io);
        Assert.False(engine.Relays.IsOn("Intake", "-K1"));
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 5)]);
    }

    [Fact]
    public void Line_BypassesContactsAndBecomesTheSelfHoldingContact()
    {
        // Drawn: start -S1 (DO:1), stop -S2 NC (DO:2), coil -K1. Then a line from -S1 to -S1 (beside it) and a contact on it.
        var start = Contact("DO:1");
        var stop = Contact("DO:2", "NC");
        var circuit = new RelayCircuit { Elements = [start, stop], Coil = new RelayCoil { Tag = "-K1" } };
        var wire = RelayLines.Bypass(circuit, start, start, out _);
        Assert.NotNull(wire);
        Assert.True(circuit.Elements[0].IsParallel);
        Assert.Same(stop, circuit.Elements[1]);

        var (engine, io) = Build(circuit);
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsOn("Intake", "-K1"));                  // a plain line conducts

        wire!.Kind = RelayElementKind.Contact;                              // the contact put on the line
        wire.Tag = "-K1";
        (engine, io) = Build(circuit);
        engine.Step(0.1, _ => io);
        Assert.False(engine.Relays.IsOn("Intake", "-K1"));
        io.Values[(SimulatorSignalKind.DO, 1)] = 1;
        engine.Step(0.1, _ => io);
        io.Values[(SimulatorSignalKind.DO, 1)] = 0;
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsOn("Intake", "-K1"));                  // holds
        io.Values[(SimulatorSignalKind.DO, 2)] = 1;
        engine.Step(0.1, _ => io);
        Assert.False(engine.Relays.IsOn("Intake", "-K1"));
    }

    [Fact]
    public void Save_KeepsTheLastStateOfObjectsAndRelays()
    {
        var latch = new ObjectConfig { Name = "L1", Type = "Logic" };
        latch.Options["Function"] = "Latch";
        latch.Ports["Set"] = "=1";
        var circuit = new RelayCircuit
        {
            Elements = [Parallel([Contact("DO:1")], [Contact("-K1")])],
            Coil = new RelayCoil { Tag = "-K1" }
        };
        var model = new ProcessModel { Objects = [latch], RelaySheets = [new RelaySheet { Name = "S", Circuits = [circuit] }] };
        var models = new Dictionary<string, ProcessModel>(StringComparer.OrdinalIgnoreCase) { ["Intake"] = model };
        var engine = new ProcessEngine(models);
        var io = new FakeIo();
        io.Values[(SimulatorSignalKind.DO, 1)] = 1;
        engine.Step(0.1, _ => io);
        io.Values[(SimulatorSignalKind.DO, 1)] = 0;
        latch.Ports.Remove("Set");
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsOn("Intake", "-K1"));
        Assert.Equal(1, engine.Find("Intake", "L1")!.Value("Out"));

        SimulationStateIo.Capture(engine);
        var loaded = ProcessModelStore.FromYaml(ProcessModelStore.ToYaml(models));
        var fresh = new ProcessEngine(loaded);
        SimulationStateIo.Restore(fresh);
        fresh.Step(0.1, _ => io);
        Assert.True(fresh.Relays.IsOn("Intake", "-K1"));                   // the self-holding relay is still held
        Assert.Equal(1, fresh.Find("Intake", "L1")!.Value("Out"));        // the latch is still set

        fresh.Reset();
        fresh.Step(0.1, _ => io);
        Assert.Equal(0, fresh.Find("Intake", "L1")!.Value("Out"));        // Reset = the initial state
    }

    [Fact]
    public void Line_AcrossSeveralContactsAndToAnotherPath()
    {
        var a = Contact("=0");
        var b = Contact("=0");
        var c = Contact("=1");
        var circuit = new RelayCircuit { Elements = [a, b, c], Coil = new RelayCoil { Tag = "-K1" } };
        RelayLines.Bypass(circuit, b, a, out _);                           // order of the clicks does not matter
        Assert.Equal(2, circuit.Elements.Count);
        Assert.Equal([a, b], circuit.Elements[0].Branches[0]);

        var other = new RelayCircuit { Elements = [Contact("=1")], Coil = new RelayCoil { Tag = "-K2" } };
        var sheet = new RelaySheet { Circuits = [circuit, other] };
        Assert.True(RelayLines.Feed(sheet, c, other, out _));
        Assert.Equal(c.Id, other.Feed);
        Assert.False(RelayLines.Feed(sheet, other.Elements[0], circuit, out var loop));   // no loop back
        Assert.Contains("loop", loop);
    }

    [Fact]
    public void Interlock_NormallyClosedContactOfTheOtherRelayBlocks()
    {
        var open = new RelayCircuit { Elements = [Contact("-S1", source: "DO:1"), Contact("-K108", "NC")], Coil = new RelayCoil { Tag = "-K107" } };
        var close = new RelayCircuit { Elements = [Contact("-S2", source: "DO:2"), Contact("-K107", "NC")], Coil = new RelayCoil { Tag = "-K108" } };
        var (engine, io) = Build(open, close);

        io.Values[(SimulatorSignalKind.DO, 1)] = 1;
        engine.Step(0.1, _ => io);
        io.Values[(SimulatorSignalKind.DO, 2)] = 1;
        engine.Step(0.1, _ => io);

        Assert.True(engine.Relays.IsOn("Intake", "-K107"));
        Assert.False(engine.Relays.IsOn("Intake", "-K108"));
        Assert.Equal("/37.1", engine.Relays.CoilLocation("Intake", "-K107"));
        Assert.Equal("/37.2", engine.Relays.ContactsOf("Intake", "-K107").Single().Location);
    }

    [Fact]
    public void TimeRelays_OnDelayAndOffDelay()
    {
        var onDelay = new RelayCircuit { Elements = [Contact("", source: "DO:1")], Coil = new RelayCoil { Tag = "-K1", Timer = RelayTimer.OnDelay, Time = 1 } };
        var offDelay = new RelayCircuit { Elements = [Contact("", source: "DO:1")], Coil = new RelayCoil { Tag = "-K2", Timer = RelayTimer.OffDelay, Time = 1 } };
        var (engine, io) = Build(onDelay, offDelay);

        io.Values[(SimulatorSignalKind.DO, 1)] = 1;
        for (var i = 0; i < 5; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.False(engine.Relays.IsOn("Intake", "-K1"));
        Assert.True(engine.Relays.IsOn("Intake", "-K2"));

        for (var i = 0; i < 6; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.True(engine.Relays.IsOn("Intake", "-K1"));

        io.Values[(SimulatorSignalKind.DO, 1)] = 0;
        engine.Step(0.1, _ => io);
        Assert.False(engine.Relays.IsOn("Intake", "-K1"));
        Assert.True(engine.Relays.IsOn("Intake", "-K2"));

        for (var i = 0; i < 11; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.False(engine.Relays.IsOn("Intake", "-K2"));
    }

    [Fact]
    public void ContactStates_ShowWhereCurrentFlows()
    {
        var first = Contact("", source: "=true");
        var second = Contact("", "NC", "=true");
        var circuit = new RelayCircuit { Elements = [first, second], Coil = new RelayCoil { Tag = "-K1" } };
        var (engine, io) = Build(circuit);

        engine.Step(0.1, _ => io);

        Assert.Equal((true, true), engine.Relays.StateOf(first));
        Assert.Equal((true, false), engine.Relays.StateOf(second));
        Assert.False(engine.Relays.IsPowered(circuit));
    }

    [Fact]
    public void SelectorSwitch_ContactsFollowThePositionsOfTheSwitchingTable()
    {
        var local = new RelayCircuit { Elements = [new RelayElement { Tag = "-S3", Position = "LOCAL" }], Coil = new RelayCoil { Outputs = "DI:10" } };
        var notRemote = new RelayCircuit { Elements = [new RelayElement { Tag = "-S3", Contact = "NC", Position = "REMOTE" }], Coil = new RelayCoil { Tag = "-K2" } };
        var model = new ProcessModel
        {
            RelaySheets = [new RelaySheet { Name = "Modes", Circuits = [local, notRemote] }],
            RelaySwitches = [new RelaySwitch { Tag = "-S3", Positions = "LOCAL/0/REMOTE", Position = "0" }]
        };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();

        engine.Step(0.1, _ => io);
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 10)]);
        Assert.True(engine.Relays.IsOn("Intake", "-K2"));

        model.RelaySwitches[0].Position = "LOCAL";
        engine.Step(0.1, _ => io);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 10)]);

        model.RelaySwitches[0].Position = "REMOTE";
        engine.Step(0.1, _ => io);
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 10)]);
        Assert.False(engine.Relays.IsOn("Intake", "-K2"));
        Assert.Equal("LOCAL/0/REMOTE", ProcessModelStore.FromYaml(ProcessModelStore.ToYaml(new Dictionary<string, ProcessModel> { ["Intake"] = model }))["Intake"].RelaySwitches.Single().Positions);
    }

    [Fact]
    public void PathFromContact_BranchesOffBelowItAfterTheSwitch()
    {
        // L+ → -K104 (NC, DO:4) → -S1 LOCAL → -K101;  branch below -K104 → -S1 AUTO → -K102.
        var esd = Contact("-K104", "NC", "DO:4");
        var local = new RelayCircuit { Elements = [esd, new RelayElement { Tag = "-S1", Position = "LOCAL" }], Coil = new RelayCoil { Tag = "-K101" } };
        var auto = new RelayCircuit { Feed = esd.Id, Elements = [new RelayElement { Tag = "-S1", Position = "AUTO" }], Coil = new RelayCoil { Tag = "-K102" } };
        var model = new ProcessModel
        {
            RelaySheets = [new RelaySheet { Name = "Gate 1", Circuits = [auto, local] }], // fed path listed first on purpose
            RelaySwitches = [new RelaySwitch { Tag = "-S1", Positions = "LOCAL/0/AUTO", Position = "AUTO" }]
        };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();

        engine.Step(0.1, _ => io);
        Assert.Empty(engine.Problems);
        Assert.True(engine.Relays.IsOn("Intake", "-K102"));
        Assert.False(engine.Relays.IsOn("Intake", "-K101"));

        io.Values[(SimulatorSignalKind.DO, 4)] = 1; // -K104 opens: both branches lose the supply
        engine.Step(0.1, _ => io);
        Assert.False(engine.Relays.IsOn("Intake", "-K102"));

        var copy = RelaySheetCopy.Copy(model.RelaySheets[0], []);
        var copiedEsd = copy.Circuits[1].Elements[0];
        Assert.Equal(copiedEsd.Id, copy.Circuits[0].Feed);
        Assert.NotEqual(esd.Id, copiedEsd.Id);
    }

    [Fact]
    public void CubicleDevices_DriveInputsOperateContactsAndLightLamps()
    {
        var open = new CubicleItem { Kind = CubicleItemKind.PushButton, Tag = "-S11", Outputs = "DI:69" };
        var stop = new CubicleItem { Kind = CubicleItemKind.PushButton, Tag = "-S10", Contact = "NC", Outputs = "DI:68" };
        var mode = new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S1", Outputs = "LOCAL=DI:10; AUTO=DI:11, DI:12" };
        var lamp = new CubicleItem { Kind = CubicleItemKind.Lamp, Tag = "-H1", Source = "-K107" };
        var plcLamp = new CubicleItem { Kind = CubicleItemKind.Lamp, Tag = "-H2", Source = "DO:5" };
        var model = new ProcessModel
        {
            RelaySheets = [new RelaySheet { Name = "Gate 1", Circuits = [new RelayCircuit { Elements = [Contact("-S11")], Coil = new RelayCoil { Tag = "-K107" } }] }],
            RelaySwitches = [new RelaySwitch { Tag = "-S1", Positions = "LOCAL/0/AUTO", Position = "AUTO" }],
            Cubicles = [new Cubicle { Name = "+LPY01", Items = [open, stop, mode, lamp, plcLamp] }]
        };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();
        io.Values[(SimulatorSignalKind.DO, 5)] = 1;

        engine.Step(0.1, _ => io);
        Assert.Empty(engine.Problems);
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 69)]);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 68)]); // NC stop button, not pressed
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 10)]);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 11)]);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 12)]);
        Assert.True(engine.Relays.IsLampOn(plcLamp));

        open.Pressed = true;
        stop.Pressed = true;
        engine.Step(0.1, _ => io);
        engine.Step(0.1, _ => io); // lamps show the relays of the previous step
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 69)]);
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 68)]);
        Assert.True(engine.Relays.IsOn("Intake", "-K107"));
        Assert.True(engine.Relays.IsLampOn(lamp));
    }

    [Fact]
    public void EmergencyStopAndIlluminatedButton_WorkLikeButtons()
    {
        var stop = new CubicleItem { Kind = CubicleItemKind.EmergencyStop, Tag = "-S301", Contact = "NC", Outputs = "DI:90" };
        var reset = new CubicleItem { Kind = CubicleItemKind.PushButton, Tag = "-S13", Source = "-K301" };
        var model = new ProcessModel
        {
            RelaySheets = [new RelaySheet { Name = "ESD", Circuits = [new RelayCircuit { Elements = [Contact("-S301", "NC")], Coil = new RelayCoil { Tag = "-K301" } }] }],
            Cubicles = [new Cubicle { Items = [stop, reset] }]
        };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();

        engine.Step(0.1, _ => io);
        engine.Step(0.1, _ => io);
        Assert.True(stop.IsButton);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 90)]);
        Assert.True(engine.Relays.IsOn("Intake", "-K301"));
        Assert.True(engine.Relays.IsLampOn(reset)); // lit by -K301

        stop.Pressed = true; // latched
        engine.Step(0.1, _ => io);
        engine.Step(0.1, _ => io);
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 90)]);
        Assert.False(engine.Relays.IsOn("Intake", "-K301"));
        Assert.False(engine.Relays.IsLampOn(reset));
    }

    [Fact]
    public void ContactorObject_FollowsTheRelayCoilWithTheSameTag()
    {
        var model = new ProcessModel
        {
            RelaySheets = [new RelaySheet { Circuits = [new RelayCircuit { Elements = [Contact("", source: "DO:7")], Coil = new RelayCoil { Tag = "-K53" } }] }],
            Objects =
            [
                new ObjectConfig { Name = "K53", Type = "Contactor", Ports = new(StringComparer.OrdinalIgnoreCase) { ["Command"] = "@Relays.-K53" } },
                new ObjectConfig { Name = "M3", Type = "Motor", Ports = new(StringComparer.OrdinalIgnoreCase) { ["Supply"] = "@K53.Live" } }
            ]
        };
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var io = new FakeIo();
        Assert.Empty(engine.Problems);

        io.Values[(SimulatorSignalKind.DO, 7)] = 1;
        for (var i = 0; i < 3; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.True(engine.Find("Intake", "K53")!.Value("State") >= 0.5);
        Assert.True(engine.Find("Intake", "M3")!.Value("Speed") > 0);
    }

    [Fact]
    public void CopySheet_ReplacesTagsSignalsAndTexts()
    {
        var sheet = new RelaySheet
        {
            Name = "GATE 1",
            Page = 37,
            Circuits =
            [
                new RelayCircuit
                {
                    Elements = [Parallel([Contact("-K107")], [Contact("-K108", "NC")]), Contact("DI:64, DI:640")],
                    Coil = new RelayCoil { Tag = "-K105", Text = "GATE 1\nOPEN", Outputs = "DI:64" }
                }
            ]
        };
        var rules = RelaySheetCopy.ParseRules("-K1*=-K2*\nDI:64=DI:84\nGATE 1=GATE 2\n-K108=-K308");

        var copy = RelaySheetCopy.Copy(sheet, rules);

        var circuit = copy.Circuits.Single();
        Assert.Equal("GATE 2", copy.Name);
        Assert.Equal(0, copy.Page);
        Assert.Equal("-K205", circuit.Coil.Tag);
        Assert.Equal("GATE 2\nOPEN", circuit.Coil.Text);
        Assert.Equal("DI:84", circuit.Coil.Outputs);
        Assert.Equal("-K207", circuit.Elements[0].Branches[0][0].Tag);
        Assert.Equal("-K308", circuit.Elements[0].Branches[1][0].Tag); // exact rule wins over the prefix
        Assert.Equal("DI:84, DI:640", circuit.Elements[1].Tag);     // whole items only
        Assert.Equal("-K107", sheet.Circuits[0].Elements[0].Branches[0][0].Tag); // original untouched
    }

    [Fact]
    public void OldContactSources_BecomeTagsAndDevices_AndAreNotSaved()
    {
        const string yaml = """
            plcs:
              Intake:
                relaySheets:
                - name: ESD
                  circuits:
                  - elements:
                    - tag: -F53
                      source: DO:4
                    - source: '@Relays.-K1'
                    coil: { tag: -K2 }
                  - elements:
                    - tag: -F53
                      contact: NC
                    coil: { tag: -K3 }
                  - elements:
                    - source: DO:9
                    coil: { tag: -K1 }
            """;
        var model = ProcessModelStore.FromYaml(yaml)["Intake"];
        var paths = model.RelaySheets.Single().Circuits;

        Assert.Equal("DO:4", RelayDevices.Find(model, "-F53")!.Input);   // defined once for the device
        Assert.Equal("@Relays.-K1", paths[0].Elements[1].Tag);
        Assert.DoesNotContain("source", ProcessModelStore.ToYaml(new Dictionary<string, ProcessModel> { ["Intake"] = model }));

        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        Assert.Empty(engine.Problems);
        var io = new FakeIo();
        io.Values[(SimulatorSignalKind.DO, 4)] = 1;
        io.Values[(SimulatorSignalKind.DO, 9)] = 1;
        engine.Step(0.1, _ => io);
        Assert.True(engine.Relays.IsOn("Intake", "-K1"));
        Assert.True(engine.Relays.IsOn("Intake", "-K2"));          // -F53 NO and the contact of -K1
        Assert.False(engine.Relays.IsOn("Intake", "-K3"));         // -F53 NC: the same device, opened
    }

    [Fact]
    public void RelayDrawnTwice_IsOneRelay()
    {
        var old = new RelayCircuit { Elements = [Contact("=0")], Coil = new RelayCoil { Tag = "2-K01" } };
        var current = new RelayCircuit { Coil = new RelayCoil { Tag = "2-K01", Input = "DO:160" } };
        var esd = new RelayCircuit { Elements = [Contact("2-K01")], Coil = new RelayCoil { Tag = "-K301" } };
        var (engine, io) = Build(old, esd, current);
        io.Values[(SimulatorSignalKind.DO, 160)] = 1;

        engine.Step(0.1, _ => io);

        Assert.Contains(engine.Problems, problem => problem.Contains("drawn more than once"));
        Assert.True(engine.Relays.IsOn(old) && engine.Relays.IsOn(current));
        Assert.True(engine.Relays.IsOn("Intake", "-K301"));
    }

    [Fact]
    public void Cubicles_SurviveYamlRoundTrip()
    {
        var model = new ProcessModel
        {
            Cubicles = [new Cubicle { Name = "+LPY01", Width = 600, Height = 800, Items = [new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S3", Text = "LOCAL / REMOTE", X = 55, Y = 60, Pressed = true }] }]
        };

        var yaml = ProcessModelStore.ToYaml(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var item = ProcessModelStore.FromYaml(yaml)["Intake"].Cubicles.Single().Items.Single();

        Assert.True(item.IsSwitch);
        Assert.Equal(("-S3", 55.0, 60.0), (item.Tag, item.X, item.Y));
        Assert.False(item.Pressed);
        Assert.DoesNotContain("pressed", yaml);
    }

    [Fact]
    public void CoilOutputToPlcOutput_IsReportedAsProblem()
    {
        var circuit = new RelayCircuit { Elements = [Contact("", source: "=true")], Coil = new RelayCoil { Tag = "-K1", Outputs = "DO:3" } };
        var (engine, _) = Build(circuit);

        Assert.Contains(engine.Problems, problem => problem.Contains("DO:3"));
    }

    [Fact]
    public void RelaySheets_SurviveYamlRoundTrip()
    {
        var model = new ProcessModel
        {
            RelaySheets =
            [
                new RelaySheet
                {
                    Name = "Gate 1",
                    Circuits = [new RelayCircuit { Elements = [Parallel([Contact("-K101", "NC")], [Contact("-K107")])], Coil = new RelayCoil { Tag = "-K1", Text = "GATE 1\nOPEN", Outputs = "DI:7" } }]
                }
            ]
        };

        var yaml = ProcessModelStore.ToYaml(new Dictionary<string, ProcessModel> { ["Intake"] = model });
        var loaded = ProcessModelStore.FromYaml(yaml)["Intake"];
        Assert.DoesNotContain("isParallel", yaml);
        Assert.DoesNotContain("isNormallyClosed", yaml);

        var circuit = loaded.RelaySheets.Single().Circuits.Single();
        Assert.Equal("GATE 1\nOPEN", circuit.Coil.Text);
        Assert.Equal("-K101", circuit.Elements.Single().Branches[0][0].Tag);
        Assert.True(circuit.Elements.Single().Branches[0][0].IsNormallyClosed);
    }
}
