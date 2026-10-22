using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public class ProcessModelTests
{
    private static ObjectConfig Obj(string name, string type, Dictionary<string, string>? ports = null, Dictionary<string, double>? parameters = null, Dictionary<string, string>? options = null) => new()
    {
        Name = name,
        Type = type,
        Ports = new(ports ?? [], StringComparer.OrdinalIgnoreCase),
        Params = new(parameters ?? [], StringComparer.OrdinalIgnoreCase),
        Options = new(options ?? [], StringComparer.OrdinalIgnoreCase)
    };

    private static ProcessEngine Engine(params ObjectConfig[] objects) =>
        new(new Dictionary<string, ProcessModel> { ["Unit"] = new() { Objects = objects.ToList() } });

    private static void Run(ProcessEngine engine, double seconds, double dt = 0.1)
    {
        for (var t = 0.0; t < seconds; t += dt)
        {
            engine.Step(dt, _ => null);
        }
    }

    [Fact]
    public void Hpu_FillsAccumulatorAlongTheGasLaw()
    {
        var engine = Engine(Obj("HPU", "Hpu", new() { ["Pump1Cmd"] = "=1" }));
        var hpu = engine.Find("Unit", "HPU")!;

        Run(engine, 5);
        var early = hpu.Value("AccumulatorPressure");
        Run(engine, 300);
        var late = hpu.Value("AccumulatorPressure");

        Assert.InRange(early, 90, 120);          // fast up to the precharge, then slow
        Assert.Equal(185, late, 0);              // relief valve
        Assert.True(hpu.Value("AccuPressureOk") > 0.5);
        Assert.True(hpu.Value("Pump1Running") > 0.5);
        Assert.Equal(80, hpu.Value("SystemPressure"), 0); // reducing valve
        Assert.True(hpu.Value("OilLevel") < 70);   // oil moved into the accumulator
    }

    [Fact]
    public void Actuator_OpensInItsStrokeTimeAndStopsAtTheEnd()
    {
        var engine = Engine(Obj("Nozzle1", "Actuator", new() { ["OpenCmd"] = "=1" }, new() { ["OpenTime"] = 20 }));
        var nozzle = engine.Find("Unit", "Nozzle1")!;

        Run(engine, 10);
        Assert.InRange(nozzle.Value("Position"), 45, 52);
        Run(engine, 15);
        Assert.Equal(100, nozzle.Value("Position"), 3);
        Assert.True(nozzle.Value("Opened") > 0.5);
        Assert.False(nozzle.Value("Moving") > 0.5);
    }

    [Fact]
    public void Actuator_EsdClosesAndLowOilPressureBlocks()
    {
        var engine = Engine(
            Obj("Pressure", "Signal", parameters: new() { ["Value"] = 5 }),
            Obj("Deflector", "Actuator", new() { ["OpenCmd"] = "=1", ["OilPressure"] = "@Pressure.Out" }, new() { ["InitialPosition"] = 50 },
                new() { ["OilPressureInput"] = "Yes" }));
        var deflector = engine.Find("Unit", "Deflector")!;

        Run(engine, 5);
        Assert.Equal(50, deflector.Value("Position"), 1); // 5 bar < MinPressure 20 bar

        deflector.Config.Ports["Esd"] = "=0";              // ESD valve de-energised (active low)
        deflector.Config.Options["EsdValve"] = "Yes";
        engine = new ProcessEngine(engine.Models, engine);
        Run(engine, 6);
        Assert.Equal(0, engine.Find("Unit", "Deflector")!.Value("Position"), 1);
    }

    [Fact]
    public void Reservoir_SettlesWhereSpillwayTakesTheSurplus()
    {
        var engine = Engine(Obj("Reservoir", "Reservoir", parameters: new()
        {
            ["Area"] = 500, ["InitialLevel"] = 5, ["SpillwayCrest"] = 5, ["SpillwayWidth"] = 10, ["RiverInflow"] = 10
        }, ports: new() { ["Unit1Flow"] = "=2" }));
        var reservoir = engine.Find("Unit", "Reservoir")!;

        Run(engine, 3600, 0.5);

        // Spill = 1.7·10·h^1.5 = 10 − 2 → h ≈ 0.60 m above the crest.
        Assert.Equal(8, reservoir.Value("Spill"), 1);
        Assert.InRange(reservoir.Value("Depth"), 5.55, 5.65);
    }

    [Fact]
    public void ReservoirGate_DrainsFasterThanTheTurbines()
    {
        var engine = Engine(
            Obj("Gate1", "Valve", new() { ["OpenCmd"] = "=1" }, new() { ["OpenTime"] = 10 }, new() { ["Command"] = "OpenClose" }),
            Obj("Reservoir", "Reservoir", new() { ["Gate1Opening"] = "@Gate1.Opening" }, new() { ["Area"] = 2000, ["RiverInflow"] = 1, ["InitialLevel"] = 4 },
                new() { ["Gates"] = "1" }));

        Run(engine, 120);

        var reservoir = engine.Find("Unit", "Reservoir")!;
        Assert.True(reservoir.Value("GateFlow") > 10);
        Assert.True(reservoir.Value("Depth") < 3.6);
    }

    [Fact]
    public void PeltonUnit_StartsSynchronisesAndProducesPower()
    {
        var engine = Engine(
            Obj("HPU", "Hpu", new() { ["Pump1Cmd"] = "=1" }),
            Obj("MIV", "Valve", new() { ["OpenCmd"] = "=1", ["OilPressure"] = "@HPU.SystemPressure" },
                new() { ["OpenTime"] = 20, ["MinPressure"] = 20 }, new() { ["Characteristic"] = "Spherical" }),
            Obj("Penstock", "Penstock", new() { ["Flow"] = "@Flow.Flow" }, new() { ["StaticHead"] = 168 }),
            Obj("TurbinePressure", "TurbinePressure", new() { ["PenstockPressure"] = "@Penstock.Pressure", ["MivOpening"] = "@MIV.Opening", ["TurbineFlow"] = "@Flow.Flow" }),
            Obj("Nozzle1", "Actuator", new() { ["Setpoint"] = "@Governor.Out", ["OilPressure"] = "@HPU.SystemPressure" },
                new() { ["OpenTime"] = 20 }, new() { ["Command"] = "AnalogPosition" }),
            // Stands in for the PLC speed governor: a little above rated speed so the phase angle keeps turning.
            Obj("Governor", "Pid", new() { ["Measurement"] = "@Rotor.SpeedPercent" },
                new() { ["Setpoint"] = 100.3, ["Kp"] = 0.3, ["Ti"] = 4, ["OutMax"] = 30 }),
            Obj("Flow", "TurbineFlow", new() { ["Opening1"] = "@Nozzle1.Position", ["Pressure"] = "@TurbinePressure.Pressure" },
                new() { ["Nozzles"] = 1, ["RatedFlow"] = 1.45, ["RatedPressure"] = 16.4 }),
            Obj("Rotor", "Rotor", new() { ["Flow"] = "@Flow.Flow", ["Pressure"] = "@TurbinePressure.Pressure", ["BreakerClosed"] = "@GCB.Closed" }),
            Obj("Excitation", "Excitation", new() { ["Start"] = "=1", ["SpeedPercent"] = "@Rotor.SpeedPercent", ["BreakerClosed"] = "@GCB.Closed" },
                options: new() { ["Command"] = "Maintained" }),
            Obj("Sync", "Synchroniser", new() { ["GeneratorFrequency"] = "@Rotor.Frequency", ["GeneratorVoltage"] = "@Excitation.Voltage", ["BreakerClosed"] = "@GCB.Closed" },
                new() { ["MaxSlip"] = 0.3, ["MaxAngle"] = 20 }),
            Obj("GCB", "Breaker", new() { ["CloseCmd"] = "=1", ["SyncOk"] = "@Sync.SyncOk" }, options: new() { ["Permissive"] = "SyncOk", ["Command"] = "Maintained" }),
            Obj("Generator", "Generator", new() { ["ShaftPower"] = "@Rotor.ShaftPower", ["ReactivePower"] = "@Excitation.ReactivePower",
                ["Voltage"] = "@Excitation.Voltage", ["Frequency"] = "@Rotor.Frequency", ["BreakerClosed"] = "@GCB.Closed" }));
        var rotor = engine.Find("Unit", "Rotor")!;
        var gcb = engine.Find("Unit", "GCB")!;

        // Startup: oil pressure, MIV opens, the nozzle goes to its start opening and the unit runs up.
        var synchronised = false;
        for (var t = 0.0; t < 600 && !synchronised; t += 0.05)
        {
            engine.Step(0.05, _ => null);
            synchronised = gcb.Value("Closed") > 0.5;
        }

        Assert.True(engine.Find("Unit", "MIV")!.Value("Open") > 0.5, "MIV open");
        Assert.True(synchronised, $"GCB closed (speed {rotor.Value("SpeedPercent"):0.0} %, slip {engine.Find("Unit", "Sync")!.Value("Slip"):0.00} Hz)");

        // Loading: open the nozzle further; the speed is held by the grid and the power follows the flow.
        engine.Find("Unit", "Governor")!.Config.Params["OutMin"] = 80;
        engine.Find("Unit", "Governor")!.Config.Params["OutMax"] = 80;
        Run(engine, 120, 0.05);
        Assert.Equal(750, rotor.Value("Speed"), 1);
        var power = engine.Find("Unit", "Generator")!.Value("P");
        Assert.InRange(power, 800, 2600);
    }

    [Fact]
    public void Rotor_LoadRejectionOverspeedsUntilTheDeflectorCutsTheJet()
    {
        var engine = Engine(
            Obj("Flow", "Signal", parameters: new() { ["Value"] = 1.2 }),
            Obj("Deflector", "Actuator", new() { ["Setpoint"] = "@DeflectorCmd.Out" }, new() { ["InitialPosition"] = 100, ["OpenTime"] = 2, ["CloseTime"] = 2 },
                new() { ["Command"] = "AnalogPosition" }),
            Obj("DeflectorCmd", "Signal", parameters: new() { ["Value"] = 100 }),
            Obj("Breaker", "Logic", options: new() { ["Function"] = "True" }),
            Obj("Rotor", "Rotor", new() { ["Flow"] = "@Flow.Out", ["Deflector"] = "@Deflector.Position", ["BreakerClosed"] = "@Breaker.Out" }));
        Run(engine, 2);
        var rotor = engine.Find("Unit", "Rotor")!;
        Assert.Equal(100, rotor.Value("SpeedPercent"), 1);

        engine.Find("Unit", "Breaker")!.Config.Options["Function"] = "False";   // GCB trips
        engine.Find("Unit", "DeflectorCmd")!.Config.Params["Value"] = 0;         // governor cuts the jet
        var peak = 0.0;
        for (var t = 0.0; t < 30; t += 0.02)
        {
            engine.Step(0.02, _ => null);
            peak = Math.Max(peak, rotor.Value("SpeedPercent"));
        }

        // J = 375 kg·m² (Mireshi) gives a mechanical time constant of about 1.2 s: a fast, large overspeed.
        Assert.InRange(peak, 110, 175);
        Assert.True(rotor.Value("SpeedPercent") < peak);
    }

    [Fact]
    public void Links_WorkAcrossPlcs()
    {
        var models = new Dictionary<string, ProcessModel>
        {
            ["Common"] = new() { Objects = [Obj("Reservoir", "Reservoir", new() { ["Unit1Flow"] = "@Unit 1/Flow.Out" }, new() { ["RiverInflow"] = 0, ["Area"] = 100, ["InitialLevel"] = 4 })] },
            ["Unit 1"] = new() { Objects = [Obj("Flow", "Signal", parameters: new() { ["Value"] = 1 }), Obj("Penstock", "Penstock", new() { ["ReservoirLevel"] = "@Common/Reservoir.Level" })] }
        };
        var engine = new ProcessEngine(models);

        Run(engine, 100);

        Assert.Empty(engine.Problems);
        Assert.Equal(3, engine.Find("Common", "Reservoir")!.Value("Depth"), 1);           // 4 m − 1 m³/s · 100 s / 100 m²
        Assert.Equal(3, engine.Find("Unit 1", "Penstock")!.Value("GrossHead"), 1);
    }

    [Fact]
    public void Engine_WritesBoundSignalsAndReadsCommands()
    {
        var io = new FakeIo();
        io.Values[(SimulatorSignalKind.DO, 66)] = 1;
        var engine = Engine(
            Obj("Nozzle1", "Actuator", new() { ["OpenCmd"] = "DO:66", ["Position"] = "AI:4", ["Moving"] = "DI:300, DI:301" }, new() { ["OpenTime"] = 10 }),
            Obj("Bad", "Actuator", new() { ["Position"] = "DO:5" }));

        for (var i = 0; i < 20; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.InRange(io.Values[(SimulatorSignalKind.AI, 4)], 15, 21);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 300)]);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 301)]);
        Assert.Contains(engine.Problems, problem => problem.Contains("DO:5"));
    }

    [Fact]
    public void Package_KeepsProjectModelAndConnections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sim_{Guid.NewGuid():N}.plantsim");
        try
        {
            var models = new Dictionary<string, ProcessModel>
            {
                ["Intake"] = new() { Objects = [Obj("Lake", "Reservoir", new() { ["Level"] = "AI:4" }, new(), new())], Forces = ["DI:23:x|TRUE"] }
            };
            var connections = new Dictionary<string, SimulatorConnectionSettings>
            {
                ["Intake"] = new() { IpAddress = "10.25.4.14", DbNumber = 30 }
            };
            var project = new byte[] { 1, 2, 3, 4 };

            SimulationPackage.Save(path, new SimulationPackage.Content { Project = project, Models = models, Connections = connections, ProjectName = "HPP" });
            var loaded = SimulationPackage.Load(path);

            Assert.Equal(project, loaded.Project);
            Assert.Equal("AI:4", loaded.Models["intake"].Find("Lake")!.Ports["Level"]);
            Assert.Equal("DI:23:x|TRUE", Assert.Single(loaded.Models["intake"].Forces));
            Assert.Equal("10.25.4.14", loaded.Connections["INTAKE"].IpAddress);
            Assert.Equal("HPP", loaded.ProjectName);
            Assert.Equal(@"C:\x\HPP.plantsim", SimulationPackage.PathFor(@"C:\x\HPP.plantcfg"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("DO:0  —  Intake · Gate open", "DO:0")]
    [InlineData("DO:0  —  Intake · Gate 1, open, DO:1  —  Intake · Gate 2", "DO:0, DO:1")]
    [InlineData("DO:0, DO:1", "DO:0, DO:1")]
    [InlineData("@Lake.Level  —  Reservoir: level, m", "@Lake.Level")]
    [InlineData("=5", "=5")]
    public void PortBindingText_DropsDescriptions(string text, string expected) =>
        Assert.Equal(expected, CwcConfigurator.App.Simulation.PortRow.StripDescriptions(text));

    [Fact]
    public void Actuator_VariantHidesPortsAndOldModelsKeepTheirEquipment()
    {
        var info = ObjectCatalog.Find("Actuator")!;
        var gate = Obj("Gate", "Actuator", new() { ["OpenCmd"] = "=1", ["Esd"] = "=0" }, new() { ["OpenTime"] = 10 });
        Assert.DoesNotContain(info.Ports, port => port.Name == "Setpoint" && info.IsActive(port.ShownWhen, gate));   // Discrete: no AO
        Assert.DoesNotContain(info.Ports, port => port.Name == "PowerOn" && info.IsActive(port.ShownWhen, gate));
        Assert.DoesNotContain(info.Options, option => option.Name == "EsdActive" && info.IsActive(option.ShownWhen, gate));

        var engine = Engine(gate);
        Run(engine, 5);
        Assert.InRange(engine.Find("Unit", "Gate")!.Value("Position"), 40, 55);   // opening; no ESD valve, so Esd "=0" is ignored

        info.UpgradeSwitches(gate);                         // an older model with Esd connected keeps its ESD valve
        Assert.Equal("Yes", gate.Options["EsdValve"]);
        Assert.False(gate.Options.ContainsKey("PowerSupply"));
    }

    [Fact]
    public void Cabinet_McbContactorMotorChain()
    {
        var io = new FakeIo();
        io.Values[(SimulatorSignalKind.DO, 10)] = 1;
        var engine = new ProcessEngine(new Dictionary<string, ProcessModel>
        {
            ["Unit"] = new()
            {
                Objects =
                [
                    Obj("Aux", "AuxSupply"),
                    Obj("F1", "Mcb", new() { ["Supply"] = "@Aux.Live", ["On"] = "DI:20", ["Load"] = "@M1.Current" }, new() { ["Rating"] = 16 }, new() { ["LoadInput"] = "Yes" }),
                    Obj("K1", "Contactor", new() { ["Coil"] = "DO:10", ["Supply"] = "@F1.Live", ["Closed"] = "DI:21" }),
                    Obj("M1", "Motor", new() { ["Supply"] = "@K1.Live", ["Running"] = "DI:22" }, new() { ["RatedCurrent"] = 5 })
                ]
            }
        });

        for (var i = 0; i < 50; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 20)]);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 21)]);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 22)]);
        Assert.InRange(engine.Find("Unit", "M1")!.Value("Current"), 4.5, 5.5);

        engine.Find("Unit", "F1")!.Config.Options["Position"] = "Off";   // operator switches the MCB off
        for (var i = 0; i < 80; i++)
        {
            engine.Step(0.1, _ => io);
        }

        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 20)]);
        Assert.Equal(1, io.Values[(SimulatorSignalKind.DI, 21)]);   // the contactor stays closed, but without voltage
        Assert.Equal(0, io.Values[(SimulatorSignalKind.DI, 22)]);
    }

    [Fact]
    public void SignalSource_StepAfterDelay()
    {
        var engine = Engine(Obj("River", "Signal", parameters: new() { ["Value"] = 2, ["Amplitude"] = 3, ["Delay"] = 10 }, options: new() { ["Shape"] = "Step" }),
            Obj("Reservoir", "Reservoir", new() { ["RiverInflow"] = "@River.Out" }, options: new() { ["RiverInflowInput"] = "Yes" }));
        Run(engine, 5);
        Assert.Equal(2, engine.Find("Unit", "Reservoir")!.Value("Inflow"), 3);
        Run(engine, 10);
        Assert.Equal(5, engine.Find("Unit", "Reservoir")!.Value("Inflow"), 3);
    }

    [Fact]
    public void OldModel_ConnectedPortsSwitchTheirVariantOn()
    {
        var reservoir = Obj("Reservoir", "Reservoir", new() { ["Unit3Flow"] = "@U3.Flow", ["RiverInflow"] = "AI:4" });
        ObjectCatalog.Find("Reservoir")!.UpgradeSwitches(reservoir);
        Assert.Equal("3", reservoir.Options["Units"]);
        Assert.Equal("Yes", reservoir.Options["RiverInflowInput"]);
    }

    [Fact]
    public void Screens_ListUsedAndEmptyScreens()
    {
        var model = new ProcessModel { Objects = [Obj("A", "Signal"), Obj("B", "Signal")], Screens = ["Main", "HPU"] };
        model.Objects[1].Screen = "AC distribution";
        Assert.Equal(new[] { "Main", "HPU", "AC distribution" }, model.AllScreens());
        Assert.Equal("Main", model.ScreenOf(model.Objects[0]));
    }

    [Fact]
    public void Model_RoundTripsThroughYaml()
    {
        var path = Path.Combine(Path.GetTempPath(), $"model_{Guid.NewGuid():N}.simulator.yaml");
        try
        {
            var models = new Dictionary<string, ProcessModel>
            {
                ["Unit 1"] = new()
                {
                    Objects =
                    [
                        Obj("Nozzle1", "Actuator", new() { ["OpenCmd"] = "DO:66", ["Position"] = "AI:4" }, new() { ["OpenTime"] = 25 }, new() { ["Command"] = "AnalogPosition" })
                    ]
                }
            };
            models["Unit 1"].Objects[0].Tables["QYX"] = [0, 50, 100];
            models["Unit 1"].Objects[0].X = 120;

            ProcessModelStore.Save(path, models);
            var loaded = ProcessModelStore.Load(path);

            var nozzle = loaded["unit 1"].Find("nozzle1")!;
            Assert.Equal("Actuator", nozzle.Type);
            Assert.Equal("DO:66", nozzle.Ports["opencmd"]);
            Assert.Equal(25, nozzle.Params["OPENTIME"]);
            Assert.Equal("AnalogPosition", nozzle.Options["Command"]);
            Assert.Equal(new double[] { 0, 50, 100 }, nozzle.Tables["QYX"]);
            Assert.Equal(120, nozzle.X);
            Assert.StartsWith("# Plant Configurator", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Generator_BindsGatesReservoirAndHealthyContacts()
    {
        var project = new CwcProject();
        void Di(int module, int port, int id, string name) { project.DigitalInputs.Add(new DigitalSignalConfig { ModuleNumber = module, Port = port, Id = id, Config = SignalConfigState.Enabled }); project.DbCatalog.DiRows.Add(new DbStringRow { Id = id, PlcName = name }); }
        void Do(int module, int port, int id, string name) { project.DigitalOutputs.Add(new DigitalSignalConfig { ModuleNumber = module, Port = port, Id = id, Config = SignalConfigState.Enabled }); project.DbCatalog.DoRows.Add(new DbStringRow { Id = id, PlcName = name }); }
        Do(0, 0, 0, "GATE_1_OPEN_CMD");
        Do(0, 1, 1, "GATE_1_CLOSE_CMD");
        Do(0, 2, 54, "DAM_PUMP_ON");
        Di(0, 0, 64, "GATE_1_LIM_SWITCH_OPEN");
        Di(0, 1, 65, "GATE_1_LIM_SWITCH_CLOSED");
        Di(0, 2, 67, "GATE_1_VFD_RUNNING");
        Di(0, 3, 13, "AC_VOLTAGE_MCB_ON");
        Di(0, 4, 357, "DAM_PUMP_CONT_ON");
        Di(0, 5, 108, "GATE_1_OVERCURRENT");
        project.AnalogInputs.Add(new AnalogSignalConfig { ModuleNumber = 0, Port = 0, Id = 18, Config = SignalConfigState.Enabled });
        project.DbCatalog.MeasRows.Add(new DbStringRow { Id = 18, PlcName = "WATER_LEVEL_1" });
        project.Counters.Add(new CounterConfig { ModuleNumber = 0, Id = 0, Config = SignalConfigState.Enabled });
        project.DbCatalog.MeasRows.Add(new DbStringRow { Id = 0, PlcName = "GATE_1" });
        var session = new PlcSession("Intake", project, "test|" + Guid.NewGuid());

        var result = ModelGenerator.Generate(session, new ProcessModel());

        var gate = result.Objects.Single(item => item.Name == "Gate1");
        Assert.Equal("DO:0", gate.Ports["OpenCmd"]);
        Assert.Equal("DO:1", gate.Ports["CloseCmd"]);
        Assert.Equal("DI:64", gate.Ports["Open"]);
        Assert.Equal("DI:65", gate.Ports["Closed"]);
        Assert.Equal("DI:67", gate.Ports["Moving"]);
        Assert.Equal("COUNTER:0", gate.Ports["Position"]);
        var reservoir = result.Objects.Single(item => item.Name == "Reservoir");
        Assert.Equal("AI:18", reservoir.Ports["Level"]);
        Assert.Equal("@Gate1.Opening", reservoir.Ports["Gate1Opening"]);
        Assert.Equal("DI:13", result.Objects.Single(item => item.Name == "HealthyContacts").Ports["Out"]);
        var echo = result.Objects.Single(item => item.Name == "Echo_DAM_PUMP");
        Assert.Equal("DO:54", echo.Ports["In1"]);
        Assert.Equal("DI:357", echo.Ports["Out"]);
        Assert.DoesNotContain(result.Objects, item => item.Ports.Values.Any(value => value.Contains("DI:108")));

        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Intake"] = new() { Objects = result.Objects } });
        Assert.Empty(engine.Problems);
    }

    [Fact]
    public void DevicesDb_UnitTemplateMatchesTia()
    {
        // TIA unit DB27: GEN_SENTRON 82 + AUX_SENTRON 82 + GEN_PROT 118 + LINE_PROT 118 + TRAFO_PROT 86 + EXC 66 = 552 bytes (read on PLCSIM).
        Assert.Equal(552, DeviceLayout.TotalBytes(DeviceLayout.UnitTemplate()));

        var project = new CwcProject();
        project.DbCatalog.MeasRows.Add(new DbStringRow { Id = 48, PlcName = "I_1", NameEnglish = "Generator current L1" });
        project.DbCatalog.MeasRows.Add(new DbStringRow { Id = 61, PlcName = "P", NameEnglish = "Active power" });
        var session = new PlcSession("Unit", project, "test|" + Guid.NewGuid());
        var devices = session.Signals.Where(signal => signal.InDeviceDb).ToList();

        var current = devices.Single(signal => signal.Kind == SimulatorSignalKind.DEV && signal.Id == 48);
        Assert.Equal(2, current.ByteOffset);
        Assert.Equal("I_1", current.PlcName);
        Assert.Equal("GEN_SENTRON", current.System);
        Assert.Equal(2 + 13 * 4, devices.Single(signal => signal.Kind == SimulatorSignalKind.DEV && signal.Id == 61).ByteOffset);
        Assert.Equal(66, devices.Single(signal => signal.Kind == SimulatorSignalKind.DEV && signal.Id == 64).ByteOffset);  // ENERGY[0]
        Assert.Equal(82 + 2, devices.Single(signal => signal.Kind == SimulatorSignalKind.DEV && signal.Id == 216).ByteOffset); // AUX MEAS[0]
        var alarm = devices.Single(signal => signal.Kind == SimulatorSignalKind.DEVBIT && signal.Id == DeviceLayout.BitId(2, 9));
        Assert.Equal((164 + 2 + 1, 1), (alarm.ByteOffset, alarm.BitOffset));                                                     // GEN_PROT ALARMS[9]
        Assert.Equal(164 + 6, devices.Single(signal => signal.Kind == SimulatorSignalKind.DEV && signal.Id == 72).ByteOffset);  // GEN_PROT MEAS[0]
        Assert.Equal(486 + 2, devices.Single(signal => signal.Kind == SimulatorSignalKind.DEV && signal.Id == 96).ByteOffset);  // EXC MEAS[0]
    }

    [Fact]
    public void Generator_BindsGeneratorValuesToDevices()
    {
        var project = new CwcProject();
        project.DigitalOutputs.Add(new DigitalSignalConfig { ModuleNumber = 0, Port = 0, Id = 195, Config = SignalConfigState.Enabled });
        project.DbCatalog.DoRows.Add(new DbStringRow { Id = 195, PlcName = "MIV_ON" });
        foreach (var (id, name) in new[] { (48, "I_1"), (51, "I"), (55, "U_LL"), (61, "P"), (72, "I_1_S1"), (76, "I_1_S2"), (89, "P"), (157, "P"), (229, "P"), (223, "U_LL") })
        {
            project.DbCatalog.MeasRows.Add(new DbStringRow { Id = id, PlcName = name });
        }

        var session = new PlcSession("Unit", project, "test|" + Guid.NewGuid());
        var result = ModelGenerator.Generate(session, new ProcessModel());

        var generator = result.Objects.Single(item => item.Name == "Generator");
        Assert.Equal("DEV:48, DEV:72, DEV:76", generator.Ports["I1"]);
        Assert.Equal("DEV:61, DEV:89, DEV:157", generator.Ports["P"]);
        Assert.Equal("DEV:55", generator.Ports["U12"]);
        var aux = result.Objects.Single(item => item.Name == "AuxSupply");
        Assert.Equal("DEV:229", aux.Ports["P"]);
        Assert.Equal("DEV:223", aux.Ports["U"]);

        var engine = new ProcessEngine(new Dictionary<string, ProcessModel> { ["Unit"] = new() { Objects = result.Objects } });
        Assert.Empty(engine.Problems);
    }

    [Fact]
    public void Forces_WinOverPlcAndModel()
    {
        var project = new CwcProject();
        project.DigitalOutputs.Add(new DigitalSignalConfig { ModuleNumber = 0, Port = 0, Id = 66, Config = SignalConfigState.Enabled });
        project.AnalogOutputs.Add(new AnalogOutputConfig { ModuleNumber = 0, Port = 0, Id = 1, Config = SignalConfigState.Enabled });
        project.DigitalInputs.Add(new DigitalSignalConfig { ModuleNumber = 0, Port = 0, Id = 23, Config = SignalConfigState.Enabled });
        var session = new PlcSession("Unit", project, "test|" + Guid.NewGuid());
        var command = session.Find(SimulatorSignalKind.DO, 66).Single();
        var setpoint = session.Find(SimulatorSignalKind.AO, 1).Single();
        var feedback = session.Find(SimulatorSignalKind.DI, 23).Single();

        command.ForceFalse = true;
        command.ForceTrue = true;                      // both pressed: TRUE wins
        Assert.True(session.TryRead(SimulatorSignalKind.DO, 66, out var value));
        Assert.Equal(1, value);

        setpoint.ForceValue = 42;
        setpoint.ForceValueOn = true;
        Assert.True(session.TryRead(SimulatorSignalKind.AO, 1, out value));
        Assert.Equal(42, value);

        feedback.ForceFalse = true;
        Assert.Equal(0d, feedback.ForcedValue);
        Assert.True(feedback.IsForced && session.HasForcedOutputs);

        var analog = new SimulatorSignal { Kind = SimulatorSignalKind.AI, Module = 0, Channel = 0, Id = 4, ByteOffset = 32 };
        analog.ForceValue = 10;
        analog.ForceValueOn = true;
        analog.WireBreak = true;                      // wire break wins over SET
        Assert.True(double.IsNaN(analog.ForcedValue!.Value));

        command.ClearForces();
        Assert.False(command.IsForced);
    }

    private sealed class FakeIo : ISignalIo
    {
        public Dictionary<(SimulatorSignalKind, int), double> Values { get; } = [];

        public bool TryRead(SimulatorSignalKind kind, int id, out double value) => Values.TryGetValue((kind, id), out value);

        public void Write(SimulatorSignalKind kind, int id, double value) => Values[(kind, id)] = value;
    }
}
