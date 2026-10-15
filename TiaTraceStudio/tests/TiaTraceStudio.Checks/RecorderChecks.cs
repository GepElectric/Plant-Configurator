using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaTraceStudio;

internal static partial class Program
{
    /// <summary>In-memory PLC: DB3 REAL at 268 counts up on every read; DB31 holds a fixed INT.</summary>
    sealed class FakePlc : IPlcReader
    {
        public readonly Dictionary<(S7Area, int), byte[]> Memory = new() { [(S7Area.DataBlock, 3)] = new byte[16384], [(S7Area.DataBlock, 1003)] = new byte[16384], [(S7Area.DataBlock, 1004)] = new byte[16384], [(S7Area.DataBlock, 1001)] = new byte[16384], [(S7Area.DataBlock, 1002)] = new byte[16384], [(S7Area.DataBlock, 31)] = new byte[1024] };
        public int Reads, FailAt = -1; public bool IsConnected { get; private set; }
        public void Connect() => IsConnected = true;
        public void Disconnect() => IsConnected = false;
        public void Dispose() => IsConnected = false;
        public byte[] Read(S7Area area, int db, int start, int count)
        {
            if (++Reads == FailAt) throw new IOException("simulirani prekid veze");
            var block = Memory[(area, db)];
            if (db is 3 or 1003) BinaryPrimitives.WriteSingleBigEndian(block.AsSpan(268), Reads);
            return block.AsSpan(start, count).ToArray();
        }
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }

    static void RecorderChecks()
    {
        var real = S7Address.Parse("%DB3.DBD268");
        Check(real.Type == "REAL" && real.Db == 3 && real.Byte == 268 && real.ToString() == "%DB3.DBD268", "S7 address parsing: DBD defaults to REAL and round-trips");
        Check(S7Address.Parse("DB31.DBW10").Type == "INT" && S7Address.Parse("%DB5.DBX2.1").Bit == 1 && S7Address.Parse("%MD100", "DINT").ToString() == "%MD100" && S7Address.Parse("%I0.3").ToString() == "%I0.3", "Word, bit, memory and input addresses");
        bool rejected = false; try { S7Address.Parse("%DB3.DBD268", "INT"); } catch (InvalidDataException) { rejected = true; }
        bool rejectedBit = false; try { S7Address.Parse("%DB3.DBX4"); } catch (InvalidDataException) { rejectedBit = true; }
        Check(rejected && rejectedBit, "Size/type mismatch and bit addresses without bit number are rejected");
        Check(S7Address.FromTraceHeader("MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL[%DB3.DBD268]") == "%DB3.DBD268" && S7Address.FromTraceHeader("Plain tag") == null, "Absolute address is read from TIA trace column headers");
        var bytes = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(bytes, 12.5f);
        Check(real.Decode(bytes) == 12.5 && S7Address.Parse("%DB1.DBW0", "INT").Decode(new byte[] { 0xFF, 0xFE }) == -2 && S7Address.Parse("%DB1.DBX0.7").Decode(new byte[] { 0x80 }) == 1, "Big-endian REAL, INT and BOOL decoding");

        var plan = ReadPlan.Build([S7Address.Parse("%DB3.DBD268"), S7Address.Parse("%DB3.DBD324"), S7Address.Parse("%DB31.DBD430"), S7Address.Parse("%DB31.DBD434"), S7Address.Parse("%DB3.DBD5000")]);
        Check(plan.Count == 3 && plan.Single(c => c.Db == 31).Count == 8 && plan.First(c => c.Db == 3 && c.Start == 268).Items.Length == 2, "Nearby addresses are read together; distant ones separately");

        // Plant Configurator project: same encryption as the original application.
        string json = """
            {"ProjectName":"Test","PlcDatabases":[
              {"Id":"u1","Name":"Unit 1","DbCatalog":{"DataBlockNames":[{"DbName":"DB MEAS","BlockKind":"Configuration","Number":3}],"MeasRows":[
                {"Id":4,"PlcStructure":"TURBINE_AND_HPU","PlcName":"NOZZLE_1","NameEnglish":"Nozzle 1 position","Unit":"15"},
                {"Id":5,"PlcStructure":"TURBINE_AND_HPU","PlcName":"NOZZLE_2","NameEnglish":"Nozzle 2 position","Unit":"15 - %"},
                {"Id":168,"PlcStructure":"CONTROL","PlcName":"P","NameEnglish":"Active power","Unit":"8","ParVal1Id":"14","ParVal2Id":"1"}],
                "ParameterRows":[{"Id":0,"PlcName":"SPARE_00","RealValue":"3.0"},{"Id":1,"PlcName":"ACTIVE_POWER","RealValue":"2070.0"},{"Id":14,"PlcName":"LOW_LIMIT_EQUAL_0","RealValue":"0.0"},
                  {"Id":903,"PlcStructure":"PID_PELTON_NOZZLE_1","PlcName":"P_SEL","ParameterType":"1","IntegerValue":"1","RealValue":"0.0"},
                  {"Id":908,"PlcStructure":"PID_PELTON_NOZZLE_1","PlcName":"GAIN","ParameterType":"0","RealValue":"35.0"},
                  {"Id":909,"PlcStructure":"PID_PELTON_NOZZLE_1","PlcName":"TI","ParameterType":"2","TimeValue":"T#1M_10S"}],
                "DiRows":[{"Id":23,"PlcStructure":"CTRL_AND_SYNC","PlcName":"GCB_ON","NameEnglish":"Generator CB on"},{"Id":24,"PlcStructure":"CTRL_AND_SYNC","PlcName":"GCB_OFF","NameEnglish":"Generator CB off"}],
                "DoRows":[{"Id":5,"PlcStructure":"TURBINE","PlcName":"NOZZLE_1_OPEN","NameEnglish":"Nozzle 1 open command"}]}},
              {"Id":"in","Name":"Intake","DbCatalog":{"DataBlockNames":[{"DbName":"DB MEAS","BlockKind":"Configuration","Number":4}],"MeasRows":[
                {"Id":0,"PlcStructure":"INTAKE","PlcName":"INTAKE_LEVEL","NameEnglish":"Intake level","Unit":"20"}]}}]}
            """;
        string plantcfg = Path.Combine(output, "Test.plantcfg");
        using (var aes = Aes.Create())
        {
            aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes("PlantConfigurator project file v1")); aes.GenerateIV();
            var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), aes.IV);
            File.WriteAllText(plantcfg, "PLANTCFG1:" + Convert.ToBase64String(aes.IV.Concat(cipher).ToArray()));
        }
        var link = PlantConfigLink.Load(plantcfg);
        var map = link.Plcs[0]; var intake = link.Plcs[1];
        var nozzle = map.Find("MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL[%DB3.DBD268]")!;
        Check(map.Items.Count(i => i.Kind == "MEAS") == 3 && nozzle.Unit == "%" && map.Find("MEAS.TURBINE_AND_HPU.NOZZLE_2.VAL")!.Unit == "%" && map.Db == 3 && map.AddressOf(nozzle).ToString() == "%DB1003.DBD268", "Encrypted .plantcfg import: names, units; VAL read from indirect DB1003 at ID × 56 + 44");
        var power = map.Find("MEAS#168")!;
        Check(power.HasRange && power.RangeLow == 0 && power.RangeHigh == 2070 && power.RangeSource == "LOW_LIMIT_EQUAL_0 / ACTIVE_POWER" && !map.Find("MEAS#4")!.HasRange,
            "Measurement ranges come from the Plant Configurator parameters ParVal1Id / ParVal2Id");
        Check(PlantLayout.Regulators.Count == 75 && PlantLayout.Address("REG", PlantLayout.FindRegulator("REG_INTF.PELTON_NOZZLE[1].VALUES.SCALED_REFERENCE")!.Id).ToString() == "%DB31.DBD430" && PlantLayout.Address("REG", PlantLayout.FindRegulator("REG_INTF.FRANCIS_DISTRIBUTOR.VALUES.SCALED_MEAS")!.Id).ToString() == "%DB31.DBD204",
            "REG_INTF layout: 25 regulators × reference / output / measurement, PELTON_NOZZLE[1] reference = %DB31.DBD430");
        Check(PlantLayout.Address("DI", 23).ToString() == "%DB1001.DBX506.0" && PlantLayout.Address("DI", 24).ToString() == "%DB1001.DBX528.0" && PlantLayout.Address("DO", 5).ToString() == "%DB1002.DBX110.0" && PlantLayout.Address("MEAS", 4).ToString() == "%DB1003.DBD268",
            "Fixed layout: MEAS DB1003 (56 B, VAL +44), DI DB1001 and DO DB1002 (22 B, bit 0) — GCB on/off = DI 23/24");
        Check(map.Find("DI#23")!.Tag == "DI.CTRL_AND_SYNC.GCB_ON.DATA.VAL" && map.AddressOf(map.Find("DI#23")!).ToString() == "%DB1001.DBX506.0" && map.AddressOf(map.Find("DO.TURBINE.NOZZLE_1_OPEN.DATA.VAL")!).ToString() == "%DB1002.DBX110.0",
            "Digital inputs and outputs come from Plant Configurator, also referenced by ID (DI#23)");
        Check(map.Controllers.Count == 3 && map.Controllers.Single(c => c.Name == "GAIN").Value == "35.0" && map.Controllers.Single(c => c.Name == "P_SEL").Value == "1" && map.Controllers.Single(c => c.Name == "TI").Value == "T#1M_10S"
            && (string)typeof(MainWindow).GetMethod("TimeText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["T#1M_10S"])! == "70 s",
            "Controller parameters (PID_*) are read from Plant Configurator: on/off, gain, times (T#1M_10S = 70 s)");
        Check(link.Plcs.Count == 2 && intake.Db == 4 && intake.AddressOf(intake.Items[0]).ToString() == "%DB1003.DBD44" && intake.Items[0].Unit == "m", "Every PLC database keeps its own measurements and MEAS block number");
        string realConfig = @"C:\Users\Mazda\OneDrive - Geppert Electric d.o.o\3000_Aplikacije\01_Plant_Configurator\01_Source_Code\PlantConfigurator.plantcfg";
        if (File.Exists(realConfig))
        {
            var full = PlantConfigLink.Load(realConfig).Plcs[0];
            Check(full.Find("MEAS#168")!.RangeHigh == 2070 && full.Find("MEAS#17")!.RangeHigh == 25 && full.Find("MEAS#178")!.RangeHigh == 1500 && full.Find("MEAS#4")!.RangeHigh == 100,
                "Real project: rated power 2070 kW, penstock 0–25 bar, speed 1500 rpm, nozzle 0–100 % from parameters");
            Check(full.Items.Count(i => i.Kind == "MEAS") == 288 && full.Items.Count(i => i.Kind == "DI") == 608 && full.Items.Count(i => i.Kind == "DO") == 288 && full.Items.First().Id == 0 && full.Items.Where(i => i.Kind == "MEAS").Last().Id == 287 && full.AddressOf(full.Find("MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL")!).ToString() == "%DB1003.DBD268", "Real Plant Configurator project: IDs 0–287, NOZZLE_1 read at %DB1003.DBD268");
        }

        bool tooFast = false; try { _ = new PlcRecorder(new FakePlc(), [new("A", real)], TimeSpan.FromMilliseconds(50), Path.Combine(output, "fast.csv")); } catch (InvalidDataException) { tooFast = true; }
        Check(tooFast, "Sampling faster than 100 ms is refused");

        var fake = new FakePlc { FailAt = 5 };
        BinaryPrimitives.WriteInt16BigEndian(fake.Memory[(S7Area.DataBlock, 31)].AsSpan(430), 77);
        string csvPath = Path.Combine(output, "Recorded.csv");
        var recorder = new PlcRecorder(fake, [new("MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL", real), new("REG_INTF.TEST", S7Address.Parse("%DB31.DBW430"))], TimeSpan.FromMilliseconds(100), csvPath);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1250))) recorder.Run(cancel.Token);
        var recorded = recorder.ToTraceData(); recorder.Dispose();
        var steps = recorded.Time.Skip(1).Select((t, i) => t - recorded.Time[i]).ToArray();
        Check(recorded.Time.Length is >= 10 and <= 14 && steps.Average() is > 0.09 and < 0.115, $"100 ms recording: {recorded.Time.Length} samples, mean step {steps.Average() * 1000:0.0} ms");
        Check(recorded.Signals[0].Source == "MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL[%DB3.DBD268]" && recorded.Signals[1].Values.Where(double.IsFinite).All(v => v == 77), "Recorded columns use TIA trace names, values decode correctly");
        Check(recorder.Errors == 1 && recorded.Signals.Any(s => s.Values.Any(double.IsNaN)) && recorded.Signals[0].Values.Last() > 5, "A failed read leaves a gap, reconnects and continues");
        Check(recorded.Warnings.Any(w => w.StartsWith("Neispravna mjerenja (NaN") && w.Contains(SignalDisplay.InvalidColor)), "Invalid (NaN) samples are reported per measurement");
        var reservedColor = MainWindow.NewProject(recorded); reservedColor.Data.Signals[0].Color = "#ff00ff";
        bool reservedRejected = false; try { PlotService.Validate(reservedColor); } catch (InvalidDataException) { reservedRejected = true; }
        Check(reservedRejected && SignalDisplay.InvalidMask([1, double.NaN, 2])!.SequenceEqual(new[] { double.NaN, 0, double.NaN }), "NaN is drawn at 0 in the reserved colour, which no signal may use");
        var reread = TraceCsv.Read(csvPath);
        Check(reread.Time.Length == recorded.Time.Length && reread.Signals[0].Source == recorded.Signals[0].Source, "Recording CSV is written while recording and can be imported again");

        // GUI: record from the slot panel with a simulated PLC and save under the test.
        var plant = new PlantDefinition { Name = "HE Snimanje", Units = [new UnitDefinition { Number = 1, Turbine = Turbines.Pelton, Nozzles = 2, Deflectors = 0 }, new UnitDefinition { Number = 2, Turbine = Turbines.Francis }] };
        PlantPlanner.LinkPlcs(plant, link);
        var unitPlc = plant.PlcForUnit(1)!; unitPlc.Ip = "192.168.0.10";
        var intakePlc = plant.Plcs.Single(p => p.UnitNumbers().Length == 0);
        Check(plant.Plcs.Count == 3 && unitPlc.CatalogId == "u1" && plant.PlcForUnit(2)!.CatalogId == "" && intakePlc.Name == "Intake" && intakePlc.CatalogId == "in", "PLCs: one per unit, Unit n databases linked by number, Common/Intake databases added without units");
        var window = new MainWindow(false) { ShowActivated = false, Width = 1720, Height = 980 }; window.Show();
        Invoke(window, "ReplaceWorkspace", new TraceSession { Plant = plant, Slots = PlantPlanner.Generate(plant), Catalog = TestCatalog.Default(), PlantConfig = link }, null!);
        plant = (PlantDefinition)Get(window, "plant"); unitPlc = plant.PlcForUnit(1)!;
        // Keep recordings inside the check output instead of the user's Documents folder.
        typeof(MainWindow).GetField("workspacePath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Path.Combine(output, "Recorder.tracestudio"));
        var gui = new FakePlc();
        typeof(MainWindow).GetField("CreatePlcReader", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, (Func<string, short, short, IPlcReader>)((_, _, _) => { var plc = new FakePlc(); plc.Memory[(S7Area.DataBlock, 1001)][506] = 1; return plc; }));
        var slots = (List<TestSlot>)Get(window, "slots");
        var slot = slots.First(s => s.Kind == "NOZZLE_STEP" && s.Index == 1);
        Invoke(window, "OpenRecorder", slot);
        var recorderWindow = Application.Current.Windows.OfType<RecorderWindow>().Single();
        var channelRows = (System.Collections.IEnumerable)typeof(RecorderWindow).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorderWindow)!;
        var first = channelRows.Cast<ChannelRow>().First();
        Check(first.Address == "%DB1003.DBD268" && first.Origin == "Plant Configurator" && first.Name == "Nozzle 1 position", "Recorder resolves MEAS tags to addresses and names from Plant Configurator");
        Check(channelRows.Cast<ChannelRow>().Single(r => r.Tag == "REG_INTF.PELTON_NOZZLE[1].VALUES.OUT").Address == "%DB31.DBD434" && channelRows.Cast<ChannelRow>().Count() == 4, "Regulator interface tags resolve to REG_INTF (DB31), matching the TIA trace addresses");
        var picker = (ComboBox)typeof(RecorderWindow).GetField("measurementPicker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recorderWindow)!;
        picker.Text = "position nozzle 2"; Pump(TimeSpan.FromMilliseconds(50));
        int shown = System.Windows.Data.CollectionViewSource.GetDefaultView(picker.ItemsSource).Cast<object>().Count();
        typeof(RecorderWindow).GetMethod("AddMeasurement", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(recorderWindow, null);
        var searched = channelRows.Cast<ChannelRow>().LastOrDefault();
        Check(shown is > 0 and < 20 && searched?.Name.StartsWith("Nozzle 2 position") == true && picker.Text == "", "Measurement picker searches by any words of the name and adds the match");
        ((System.Collections.IList)channelRows).Remove(searched);
        foreach (var row in channelRows.Cast<ChannelRow>().Where(r => r.Address == "")) { row.Address = row.Tag.EndsWith("SCALED_REFERENCE") ? "%DB31.DBD430" : "%DB31.DBD434"; }
        typeof(RecorderWindow).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(recorderWindow, null);
        Pump(TimeSpan.FromMilliseconds(2300));
        recorderWindow.UpdateLayout();
        var image = new RenderTargetBitmap((int)recorderWindow.ActualWidth, (int)recorderWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(recorderWindow);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(Path.Combine(output, "Recorder.png"))) png.Save(stream);
        var finish = (Task)typeof(RecorderWindow).GetMethod("Finish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(recorderWindow, [true])!;
        while (!finish.IsCompleted) Pump(TimeSpan.FromMilliseconds(50));
        finish.GetAwaiter().GetResult();
        var active = Active(window);
        var docs = ((System.Collections.IEnumerable)Get(window, "documents")).Cast<object>().ToList();
        string? savedSlot = (string?)docs.Single().GetType().GetProperty("SlotId")!.GetValue(docs.Single());
        Check(savedSlot == slot.Id && active.Data.Time.Length >= 18 && active.Data.Signals[0].Name == "Nozzle 1 position" && active.Data.Signals[0].Unit == "%", $"Stopping saves the recording under its test ({active.Data.Time.Length} samples) with catalogue names");
        var addresses = (Dictionary<string, TagAddress>)Get(window, "tagAddresses");
        Check(unitPlc.Ip == "192.168.0.10" && active.Data.Signals.Any(x => x.Name == "Pelton mlaznica 1 – izlaz regulatora"), "PLC connection is kept; regulator signals are named");
        // User-defined recording on the intake PLC: names, units and addresses come from that PLC's database.
        var user = PlantPlanner.NewUserSlot("Intake – razina", plant.Plcs.Single(p => p.UnitNumbers().Length == 0).Id);
        user.Channels = [new SlotChannel("MEAS.INTAKE.INTAKE_LEVEL.VAL")]; slots.Add(user);
        Invoke(window, "OpenRecorder", user);
        var userWindow = Application.Current.Windows.OfType<RecorderWindow>().Single();
        var userRow = ((System.Collections.IEnumerable)typeof(RecorderWindow).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(userWindow)!).Cast<ChannelRow>().Single();
        Check(userRow.Address == "%DB1003.DBD44" && userRow.Name == "Intake level", "User recording resolves tags in its own PLC database (Intake)");
        userWindow.Close();
        // Common load rejection: generator breaker of every unit, recorded from both PLCs at once.
        var common = slots.First(s => s.IsCommon && s.Index == 50);
        Check(slots.Count(s => s.IsCommon) == 4 && common.Phase == TestPhases.Common, "Plants with several units get common load rejection tests (25/50/75/100 %)");
        var commonChannels = (System.Collections.IList)typeof(MainWindow).GetMethod("ChannelsOf", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [common])!;
        // Unit 1: Pelton, 2 nozzles, no deflector → 2 valves + P, U, n, penstock, turbine pressure + GCB on/off = 9; unit 2: Francis → 1 + 5 + 2 = 8.
        Check(commonChannels.Count == 17, $"Common load rejection: valves, power, voltage, speed, pressures and breaker of every unit ({commonChannels.Count})");
        plant.PlcForUnit(2)!.Ip = "192.168.0.20";
        Invoke(window, "OpenRecorder", common);
        var commonWindow = Application.Current.Windows.OfType<RecorderWindow>().Single();
        typeof(RecorderWindow).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(commonWindow, null);
        Pump(TimeSpan.FromMilliseconds(1500));
        var commonFinish = (Task)typeof(RecorderWindow).GetMethod("Finish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(commonWindow, [true])!;
        while (!commonFinish.IsCompleted) Pump(TimeSpan.FromMilliseconds(50));
        var both = Active(window);
        var gcbOn = both.Data.Signals.Single(x => x.Source == "PLC agregata 1: DI.CTRL_AND_SYNC.GCB_ON.DATA.VAL[%DB1001.DBX506.0]");
        var gcbOff = both.Data.Signals.Single(x => x.Source == "PLC agregata 1: DI.CTRL_AND_SYNC.GCB_OFF.DATA.VAL[%DB1001.DBX528.0]");
        Check(both.Data.Signals.Count == 17 && both.Data.Signals.Select(x => x.Source.Split(':')[0]).Distinct().Count() == 2 && both.Data.Signals.Any(x => x.Source == "PLC agregata 2: MEAS#178[%DB1003.DBD10012]"),
            "Two PLCs recorded together on one time axis, columns prefixed with the PLC" + (both.Data.Signals.Count == 17 ? "" : " | " + string.Join(" ; ", both.Data.Signals.Select(x => x.Source))));
        var powerSignal = both.Data.Signals.Single(x => x.Source.StartsWith("PLC agregata 1: MEAS.CONTROL.P.VAL"));
        Check(powerSignal.PercentFromProject && powerSignal.PercentMin == 0 && powerSignal.PercentMax == 2070, "Normalized range of a recording is taken from the Plant Configurator parameters");
        Check(gcbOn.Name == "Agr. 1 · Generator CB on" && gcbOn.Values.Where(double.IsFinite).All(v => v == 1) && gcbOff.Values.Where(double.IsFinite).All(v => v == 0), "Breaker state decoded from the DI bit and named per unit");
        // Editing the list of a test: removing one measurement stores the list, restoring the default clears it.
        var setChannels = typeof(MainWindow).GetMethod("SetChannels", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var unitPlc1 = plant.PlcForUnit(1)!;
        setChannels.Invoke(window, [slot, new List<(string, PlcConnection?)> { ("MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL", unitPlc1), ("DI#23", unitPlc1) }]);
        Check(slot.Channels?.Count == 2 && slot.Channels.All(c => c.PlcId == ""), "Edited measurement list is stored per test");
        var defaults = (System.Collections.IList)typeof(MainWindow).GetMethod("DefaultChannels", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [slot])!;
        setChannels.Invoke(window, [slot, defaults.Cast<SlotChannel>().Select(c => (c.Tag, (PlcConnection?)unitPlc1)).ToList()]);
        Check(slot.Channels == null, "A list equal to the catalogue default follows the catalogue again");
        var merged = PlantPlanner.Merge(slots.ToList(), PlantPlanner.Generate(plant), _ => false);
        Check(merged.Contains(user) && !user.Retired, "Changing the plant definition keeps user recordings");
        Check(Directory.GetFiles(Path.Combine(output, "Recorder - snimke"), "*.csv").Length == 2, "Recording CSVs (single and common test) are stored next to the project file");
        // User-built group with its own PLC / database, a sub-group and a recording inside it.
        var intakeGroup = new CustomGroup { Name = "Intake", PlcId = intakePlc.Id, Folders = ["Zapornica"] };
        plant.CustomGroups.Add(intakeGroup);
        var gate = PlantPlanner.NewUserSlot("Zapornica – hod", ""); gate.GroupId = intakeGroup.Id; gate.Phase = intakeGroup.Name; gate.Group = "Zapornica";
        gate.Channels = [new SlotChannel("MEAS.INTAKE.INTAKE_LEVEL.VAL")]; slots.Add(gate);
        Invoke(window, "RefreshTree");
        var planTree = (TreeView)window.FindName("PlantTree");
        var groupNode = planTree.Items.OfType<TreeViewItem>().Single(i => i.Tag as string == "G/" + intakeGroup.Id);
        var folderNode = groupNode.Items.OfType<TreeViewItem>().Single();
        Check(folderNode.Tag as string == $"G/{intakeGroup.Id}/Zapornica" && folderNode.Items.OfType<TreeViewItem>().Single().Tag == gate && groupNode.ContextMenu != null, "User-built group with sub-group and recording appears in the tree, with a right-click menu");
        var plcForGate = (PlcConnection?)typeof(MainWindow).GetMethod("PlcFor", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [gate]);
        Check(plcForGate == intakePlc, "Recordings of a group use the group's PLC and Plant Configurator database");
        var settingsPopup = (Window)Get(window, "settingsWindow");
        Invoke(window, "ShowSettings", 0); settingsPopup.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(200));
        foreach (var (tab, file) in new[] { (0, "Settings-signals.png"), (1, "Settings-graph.png") })
        {
            Invoke(window, "ShowSettings", tab); settingsPopup.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(150));
            var popupShot = new RenderTargetBitmap((int)settingsPopup.ActualWidth, (int)settingsPopup.ActualHeight, 96, 96, PixelFormats.Pbgra32); popupShot.Render(settingsPopup);
            var popupPng = new PngBitmapEncoder(); popupPng.Frames.Add(BitmapFrame.Create(popupShot)); using (var stream = File.Create(Path.Combine(output, file))) popupPng.Save(stream);
        }
        settingsPopup.Hide();
        CaptureDialog(window, "Test-details.png"); Invoke(window, "ShowTestDetails", slot);
        Check(File.Exists(Path.Combine(output, "Settings-signals.png")) && File.Exists(Path.Combine(output, "Test-details.png")), "Graph settings and test details open as larger, sectioned popups");
        CaptureDialog(window, "Channel-editor.png"); Invoke(window, "EditChannels", common);
        Check(File.Exists(Path.Combine(output, "Channel-editor.png")), "Measurement editor opens for a common test");
        CaptureDialog(window, "Plc-dialog.png"); Invoke(window, "EditPlcs");
        typeof(MainWindow).GetField("selectedSlotId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, user.Id);
        Invoke(window, "RefreshTree"); window.UpdateLayout();
        var shot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); shot.Render(window);
        var shotPng = new PngBitmapEncoder(); shotPng.Frames.Add(BitmapFrame.Create(shot)); using (var stream = File.Create(Path.Combine(output, "Studio-user.png"))) shotPng.Save(stream);
        Invoke(window, "SaveWorkspaceTo", Path.Combine(output, "Recorder.tracestudio")); // no "save changes?" prompt on close
        var reopened = TraceSession.Load(Path.Combine(output, "Recorder.tracestudio"));
        Check(reopened.PlantConfig?.Plcs.Count == 2 && reopened.Plant!.Plcs.Count == 3 && reopened.Slots.Count(s => s.IsUser) == 2 && reopened.Plant!.CustomGroups.Single().Folders.Single() == "Zapornica" && reopened.Slots.Single(s => s.GroupId != "").Group == "Zapornica", "Project stores the Plant Configurator snapshot, PLC links, user groups and recordings");

        // Own recordings and sub-groups inside a unit's tree.
        var treePlant = (PlantDefinition)Get(window, "plant"); var treeSlots = (List<TestSlot>)Get(window, "slots");
        treePlant.Units[0].Folders.Add(TestPhases.User + "/Prazna podgrupa");
        var own = PlantPlanner.NewUserSlot("Vlastito snimanje ventila", treePlant.PlcForUnit(1)!.Id); own.Unit = 1; own.Phase = TestPhases.Dry; own.Group = "Ventili/Moje";
        treeSlots.Add(own); Invoke(window, "RefreshTree");
        IEnumerable<TreeViewItem> Nodes(ItemsControl parent) => parent.Items.OfType<TreeViewItem>().SelectMany(i => Nodes(i).Prepend(i));
        var allNodes = Nodes((TreeView)window.FindName("PlantTree")).ToList();
        var ownFolder = allNodes.FirstOrDefault(n => n.Tag as string == $"U1/{TestPhases.Dry}/Ventili/Moje");
        var emptyFolder = allNodes.FirstOrDefault(n => n.Tag as string == $"U1/{TestPhases.User}/Prazna podgrupa");
        Check(ownFolder?.Items.OfType<TreeViewItem>().Any(n => n.Tag == own) == true && emptyFolder != null
            && ownFolder.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Header as string == "Novo snimanje ovdje…") == true
            && emptyFolder.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Header as string == "Obriši podgrupu") == true
            && allNodes.First(n => n.Tag as string == "U1").ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Header as string == "Novo snimanje agregata…") == true,
            "A unit's tree takes own recordings and sub-groups (right-click: new recording here, new sub-group)");
        var kept = PlantPlanner.Merge(treeSlots, PlantPlanner.Generate(treePlant), _ => false);
        Check(kept.Any(s => s.Id == own.Id && s.Unit == 1 && s.Group == "Ventili/Moje") && ((PlcConnection?)typeof(MainWindow).GetMethod("PlcFor", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [own]))?.Id == treePlant.PlcForUnit(1)!.Id,
            "Own recordings of a unit survive a plant definition change and record from the unit's PLC");
        // Renaming any part of the tree: generated phase (display name), user sub-group (moves its recordings), back to the original.
        string? answer = null; Set(window, "AnswerText", (Func<string, string, string?>)((_, _) => answer));
        answer = "Suho ispitivanje"; Invoke(window, "RenameNode", $"U1/{TestPhases.Dry}");
        answer = "Moja podgrupa"; Invoke(window, "RenameNode", $"U1/{TestPhases.User}/Prazna podgrupa");
        answer = "Agregat A"; Invoke(window, "RenameNode", "U1");
        allNodes = Nodes((TreeView)window.FindName("PlantTree")).ToList();
        string HeaderText(TreeViewItem i) => string.Concat(FindVisualLogical(i.Header as DependencyObject));
        Check(HeaderText(allNodes.First(n => n.Tag as string == $"U1/{TestPhases.Dry}")).Contains("Suho ispitivanje") && HeaderText(allNodes.First(n => n.Tag as string == "U1")).Contains("Agregat A")
            && treePlant.Units[0].Folders.Contains(TestPhases.User + "/Moja podgrupa") && allNodes.Any(n => n.Tag as string == $"U1/{TestPhases.User}/Moja podgrupa")
            && allNodes.First(n => n.Tag as string == "U1").ContextMenu?.Items.OfType<MenuItem>().First().Header as string == "Preimenuj…",
            "Every part of the tree can be renamed (unit, phase, sub-group); user sub-groups are renamed for real");
        answer = TestPhases.Dry; Invoke(window, "RenameNode", $"U1/{TestPhases.Dry}");
        Check(!treePlant.TreeNames.ContainsKey($"U1/{TestPhases.Dry}"), "Typing the original name restores a generated node's name");
        treePlant.TreeNames.Clear(); Set(window, "AnswerText", null);

        // Deleting: an own recording leaves the tree; a generated test with a recording is hidden with it and can be restored.
        Set(window, "ConfirmDelete", (Func<string, bool?>)(_ => true));
        Invoke(window, "DeleteSlot", own);
        var withRecording = treeSlots.First(s => !s.IsUser && ((System.Collections.IEnumerable)typeof(MainWindow).GetMethod("RecordingsOf", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [s])!).Cast<object>().Any());
        int docsBefore = ((System.Collections.ICollection)Get(window, "documents")).Count;
        Invoke(window, "DeleteSlot", withRecording);
        allNodes = Nodes((TreeView)window.FindName("PlantTree")).ToList();
        Check(!treeSlots.Contains(own) && withRecording.Deleted && !allNodes.Any(n => n.Tag == withRecording) && ((System.Collections.ICollection)Get(window, "documents")).Count < docsBefore
            && PlantPlanner.Merge(treeSlots, PlantPlanner.Generate(treePlant), _ => false).Single(s => s.Id == withRecording.Id).Deleted,
            "Deleting a recording removes it (and its recordings after confirmation); deleted tests stay deleted when the plant changes");
        typeof(MainWindow).GetMethod("RestoreDeleted", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [(Func<TestSlot, bool>)(s => s.Unit == withRecording.Unit)]);
        Check(!withRecording.Deleted, "Deleted tests of a unit can be restored (right-click on the unit)");
        Set(window, "ConfirmDelete", null);
        treePlant.Units[0].Folders.Clear(); Invoke(window, "RefreshTree");

        // The .plantcfg saved again in Plant Configurator: the tree offers a refresh.
        var linkedConfig = (PlantConfigLink)Get(window, "plantConfig");
        File.SetLastWriteTime(linkedConfig.SourceFile, linkedConfig.LoadedAt.AddMinutes(1));
        Invoke(window, "ShowPlantConfigInfo");
        Check(((Button)window.FindName("PlantConfigRefresh")).Visibility == Visibility.Visible, "A changed .plantcfg shows the refresh button");
        Invoke(window, "PlantConfigRefresh_Click", window, new RoutedEventArgs());
        File.SetLastWriteTime(linkedConfig.SourceFile, DateTime.Now.AddMinutes(-1)); Invoke(window, "ShowPlantConfigInfo");
        Check(((Button)window.FindName("PlantConfigRefresh")).Visibility == Visibility.Collapsed && Get(window, "plantConfig") != linkedConfig, "Refresh reloads the Plant Configurator project and hides the button");

        // Chapter names reviewed before the export are stored in the tree (English names).
        var phaseKey = $"U1/{TestPhases.Dry}";
        typeof(MainWindow).GetMethod("ApplyChapterNames", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new List<(object, string, string)> { (phaseKey, "Dry test", "Dry tests") }]);
        Check(((PlantDefinition)Get(window, "plant")).TreeNames.GetValueOrDefault(phaseKey) == "Dry tests", "Chapter names edited before the export are saved in the tree");
        ((PlantDefinition)Get(window, "plant")).TreeNames.Remove(phaseKey); Invoke(window, "RefreshTree");

        // Word report: chapters follow the tree (numbered, same names and order), graphs of the recordings under their tests.
        string reportPath = Path.Combine(output, "Izvjestaj.docx");
        var reportResult = typeof(MainWindow).GetMethod("WriteReport", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [reportPath, null, null, null])!;
        int graphs = (int)reportResult.GetType().GetField("Item1")!.GetValue(reportResult)!;
        using (var zip = System.IO.Compression.ZipFile.OpenRead(reportPath))
        {
            System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var doc = System.Xml.Linq.XDocument.Load(zip.GetEntry("word/document.xml")!.Open());
            var headings = doc.Descendants(w + "p").Select(p => (Style: p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "", Text: string.Concat(p.Descendants(w + "t").Select(t => t.Value))))
                .Where(h => h.Style.StartsWith("Heading")).ToList();
            var tree = Nodes((TreeView)window.FindName("PlantTree")).Where(n => n.Tag is string key && key != "__unassigned" || n.Tag is TestSlot).ToList();
            var paras = doc.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))).ToList();
            var firstUnit = (PlantDefinition)Get(window, "plant");
            Check(graphs > 0 && zip.Entries.Count(e => e.FullName.StartsWith("word/media/")) == graphs && headings.Count == tree.Count + 1 && headings[^1].Text.EndsWith("Commissioning engineer sign-off") && doc.Descendants(w + "hyperlink").Count() == headings.Count + paras.Count(t => System.Text.RegularExpressions.Regex.IsMatch(t, "^(Figure|Table) [0-9]+: ")) / 2 && doc.Descendants(w + "bookmarkStart").Count() == doc.Descendants(w + "hyperlink").Count()
                && headings[0].Style == "Heading1" && headings[0].Text.StartsWith("1 Agregat 1") && headings[1].Style == "Heading2" && headings[1].Text.StartsWith("1.1 ")
                && headings.Any(h => h.Text.EndsWith(" Zapornica")) && doc.Descendants(w + "instrText").Any(t => t.Value.Contains("TOC")) && paras.Any(t => t.StartsWith("Table ") && t.Contains(": Statistics · ")) && paras.Any(t => t.StartsWith("Figure 1: ") && t.EndsWith(" · Unit 1")) && paras.Any(t => t.StartsWith("Figure 2: ") && t.EndsWith(" · Unit 2")) && paras.Contains("List of figures") && paras.Contains("List of tables") && paras.Any(t => t.StartsWith("Table 1: ")) && graphs == 2 && !doc.Descendants(w + "t").Any(t => t.Value.Contains(".csv") || t.Value.Contains("Napomena") || t.Value.Contains("Mjerenja")) && doc.Descendants(w + "tbl").Count() == graphs + tree.Count(n => n.Tag is TestSlot) + 1 + tree.Count(n => n.Tag is TestSlot t && t.HasTimes) + tree.Select(n => n.Tag as TestSlot).OfType<TestSlot>().Select(t => (t, m: System.Text.RegularExpressions.Regex.Match(t.Kind, "^(NOZZLE|DEFLECTOR|WICKET_GATE|KAPLAN_GATE|RUNNER|CF_SECTION|CTRL_SPEED|CTRL_POWER|CTRL_LEVEL)_(STEP|RAMP)$"))).Where(x => x.m.Success).Select(x => (x.t.Unit, x.t.Phase, x.t.Group, x.m.Groups[1].Value, x.m.Groups[1].Value.StartsWith("CTRL_") ? 0 : x.t.Index)).Distinct().Count() && paras.Any(t => t.Contains(": Controller parameters · ") && t.Contains("PID_")) && !doc.Descendants(w + "t").Any(t => t.Value == "Generator CB on") && doc.Descendants(w + "t").Any(t => t.Value == "Opening time [s]") && doc.Descendants(w + "tbl").Count(t => t.Descendants(w + "t").FirstOrDefault()?.Value == "Remark") == tree.Count(n => n.Tag is TestSlot) && doc.Descendants(w + "pgSz").Any(x => x.Attribute(w + "orient")?.Value == "landscape") && doc.Descendants(w + "sectPr").Count() > 3 && zip.GetEntry("word/footer1.xml") != null,
                $"Word report: {headings.Count} chapters numbered like the tree, {graphs} graphs, table of contents, landscape graph sections, a Remark table per test, clickable contents, sign-off chapter, no statistics for digital signals, common test split per unit (graph and statistics)"
                + (graphs > 0 ? "" : $" · documents {((System.Collections.ICollection)Get(window, "documents")).Count} · failed: " + string.Join(" | ", (List<string>)reportResult.GetType().GetField("Item2")!.GetValue(reportResult)!)) + $" · tree {tree.Count}");
        }

        // Company templates (Geppert Electric, Prime Volt): template header/footer/cover kept, texts replaced, sample content gone,
        // landscape graph pages with a widened copy of the header, remarks in the form of the template's Conclusion table.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (company, templatePath, sample) in new[]
        {
            ("Geppert", Path.Combine(profile, "OneDrive - Geppert Electric d.o.o", "50_KVALITETA", "02_Predlošci", "02_FAT izvještaj", "SCADA SYSTEM-FR-QC-001_FAT report.docx"), "Visual check"),
            ("PrimeVolt", Path.Combine(profile, "OneDrive - PRIME VOLT d.o.o", "KVALITETA[Quality]", "02_Predlošci_obrasci", "FR-QC-001_FAT_report.docx"), "Auxiliary power supply cubicle")
        })
        {
            if (!File.Exists(templatePath)) continue;
            var template = WordTemplate.Load(templatePath);
            // Required document data recognised in the form, written to every place (labels kept, upper case kept).
            var found = template.Fields().ToDictionary(f => f.Name);
            string[] expected = company == "Geppert" ? ["Document number", "Project", "Title", "Author", "Customer", "Section", "Date"] : ["Document number", "Project", "Title", "Date"];
            Check(expected.All(found.ContainsKey) && found["Document number"].Places.Count >= 1 && found["Title"].Places.All(p => p.Key.Contains("FAT", StringComparison.OrdinalIgnoreCase)),
                $"{company} form: required document fields recognised ({string.Join(", ", found.Keys)})");
            var fieldValues = new Dictionary<string, string> { ["Document number"] = "DOC-SAT-0042", ["Project"] = "HPP Testna", ["Title"] = "Commissioning report", ["Author"] = "Ivo Ivić", ["Customer"] = "Kupac d.o.o.", ["Section"] = "Commissioning", ["Date"] = "13.10.2026." };
            var replace = new Dictionary<string, string>();
            foreach (var f in found.Values) foreach (var (key, prefix, upper) in f.Places) replace[key] = prefix + (upper ? fieldValues[f.Name].ToUpperInvariant() : fieldValues[f.Name]);
            string templated = Path.Combine(output, $"Report-{company}.docx");
            typeof(MainWindow).GetMethod("WriteReport", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [templated, template, replace, fieldValues]);
            using var zip = System.IO.Compression.ZipFile.OpenRead(templated);
            string Part(string name) => new StreamReader(zip.GetEntry(name)!.Open()).ReadToEnd();
            string document = Part("word/document.xml"), header = Part("word/header1.xml");
            string landscapeHeader = zip.GetEntry("word/headerLandscape.xml") != null ? Part("word/headerLandscape.xml") : "";
            int Widest(string xml) => System.Text.RegularExpressions.Regex.Matches(xml, "<w:gridCol w:w=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Sum();
            System.Xml.Linq.XDocument.Parse(document);
            string core = Part("docProps/core.xml");
            Check(header.Contains("DOC-SAT-0042") && System.Text.RegularExpressions.Regex.Replace(header, "<[^>]+>", "").Contains("Commissioning report", StringComparison.OrdinalIgnoreCase) && !header.Contains(">FAT report<")
                && core.Contains("<dc:title>Commissioning report</dc:title>") && core.Contains("HPP Testna"),
                $"{company} form: document number, project and title written to the header, cover and file properties");
            Check(true && landscapeHeader.Length > 0 && Widest(landscapeHeader) > Widest(header) * 1.3
                && document.Contains("w:orient=\"landscape\"") && document.Contains("rTrsheader") && !document.Contains(sample) && System.Text.RegularExpressions.Regex.Replace(document, "<[^>]+>", "") is var plainText && plainText.Contains("Remark") && !plainText.Contains("Checked:")
                && zip.Entries.Any(e => e.FullName.StartsWith("word/media/report")),
                $"{company} template: header text replaced, landscape header widened, sample content replaced by the tree, remarks as the template's Conclusion table without signatures");
        }

        // New project: the chosen .plantcfg fills the rated values of the unit's tests right away.
        typeof(PlantWizard).GetField("PickConfigFile", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, (Func<Window, string?, string?>)((_, _) => plantcfg));
        List<NominalRange>? shownRanges = null;
        void Inspect(int attempt) => window.Dispatcher.BeginInvoke(() =>
        {
            var wizard = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w != window && w.IsVisible && w.Owner != null);
            var grid = wizard == null ? null : FindVisual<DataGrid>(wizard).FirstOrDefault(g => g.ItemsSource is IEnumerable<NominalRange> r && r.Any());
            if (grid == null && attempt < 600) { var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; retry.Tick += (_, _) => { retry.Stop(); Inspect(attempt + 1); }; retry.Start(); return; }
            shownRanges = (grid?.ItemsSource as IEnumerable<NominalRange>)?.ToList();
            wizard!.UpdateLayout();
            var image = new RenderTargetBitmap((int)wizard.ActualWidth, (int)wizard.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(wizard);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(Path.Combine(output, "Wizard-prefilled.png"))) png.Save(stream);
            wizard.Close();
        }, DispatcherPriority.ApplicationIdle);
        Invoke(window, "SaveWorkspaceTo", Path.Combine(output, "Recorder.tracestudio")); // no "save changes?" question before the wizard
        Inspect(0);
        Invoke(window, "NewWorkspace_Click", window, new RoutedEventArgs());
        Check(shownRanges?.SingleOrDefault(r => r.Name == "Active power") is { Min: 0, Max: 2070 }, "Choosing the .plantcfg in a new project fills the rated values (active power 0–2070 kW) at once" + (shownRanges?.SingleOrDefault(r => r.Name == "Active power") is { Min: 0, Max: 2070 } ? "" : " · shown: " + (shownRanges == null ? "no grid" : string.Join(", ", shownRanges.Select(r => $"{r.Name} {r.Min}-{r.Max}")))));
        Invoke(window, "SaveWorkspaceTo", Path.Combine(output, "Recorder.tracestudio"));
        window.Close();
    }

    static void Set(object target, string field, object? value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    /// <summary>Texts of all TextBlocks below an element (logical tree, works before rendering).</summary>
    static IEnumerable<string> FindVisualLogical(DependencyObject? root)
    {
        if (root == null) yield break;
        if (root is TextBlock t) yield return t.Text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var text in FindVisualLogical(child)) yield return text;
    }

    static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var inner in FindVisual<T>(child)) yield return inner;
        }
    }
}
