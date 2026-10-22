using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaTraceStudio;

internal static partial class Program
{
    static void PlantChecks(string sample)
    {
        var plant = new PlantDefinition
        {
            Name = "HE Test", Location = "Teren",
            Units =
            [
                new UnitDefinition { Number = 1, Turbine = Turbines.Pelton, Nozzles = 4, Deflectors = 2 },
                new UnitDefinition { Number = 2, Turbine = Turbines.Kaplan, HasBypass = false, LevelControl = false },
                new UnitDefinition { Number = 3, Turbine = Turbines.CrossFlow, GuideVaneSections = 2, HasExcitation = false, LoadRejectionLevels = "50; 100" }
            ]
        };
        plant.Validate();
        var slots = PlantPlanner.Generate(plant);
        Check(slots.Select(s => s.Id).Distinct().Count() == slots.Count, "Generated test ids are unique");
        Check(slots.Count(s => s.Unit == 1 && s.Phase == TestPhases.Dry && s.Group.StartsWith("Turbinski ventili/")) == 18, "Pelton with 4 nozzles and 2 deflectors gets open/close, step and ramp per actuator");
        Check(slots.Count(s => s.Unit == 2 && s.Group.StartsWith("Turbinski ventili/")) == 6 && !slots.Any(s => s.Unit == 2 && s.Kind == "BYPASS_TIMES"), "Kaplan tests wicket gate and runner, and omits a missing bypass");
        Check(!slots.Any(s => s.Unit == 3 && (s.Kind == "FIRST_EXCITATION" || s.Kind.StartsWith("CTRL_VOLTAGE"))) && slots.Count(s => s.Unit == 3 && s.Kind == "LOAD_REJECTION") == 2, "Units without excitation skip excitation tests; custom load rejection levels");
        Check(slots.First(s => s.Kind == "MIV_TIMES").Manual && slots.Any(s => s.Kind == "HEAT_RUN" && s.Phase == TestPhases.Grid) && slots.Any(s => s.Kind == "BEARING_RUN" && s.Phase == TestPhases.Wet), "Manual MIV entry, bearing run in wet test and heat run on grid");
        var defaultsCatalog = TestCatalog.Default();
        var pelton = new UnitDefinition { Number = 1, Turbine = Turbines.Pelton, Nozzles = 2, Deflectors = 1, HasVibration = true };
        var rejection = new TestSlot { Unit = 1, Kind = "LOAD_REJECTION", Index = 50 };
        Check(defaultsCatalog.ExpectedTags(rejection, null, pelton).SequenceEqual(["MEAS#4", "MEAS#5", "MEAS#3", "MEAS#168", "MEAS#171", "MEAS#178", "MEAS#17", "MEAS#18", "MEAS#40", "MEAS#41", "MEAS#42", "MEAS#43", "DI#23", "DI#24"]),
            "Load rejection defaults: turbine valves of the unit, P/U/n from control measurements, penstock and turbine pressure, vibrations, breaker");
        Check(defaultsCatalog.ExpectedTags(new TestSlot { Unit = 1, Kind = "HPU_PRESSURE_DROP" }, null, new UnitDefinition { Turbine = Turbines.Kaplan }).SequenceEqual(["MEAS#13", "MEAS#1", "MEAS#2"])
            && defaultsCatalog.ExpectedTags(new TestSlot { Unit = 1, Kind = "STARTUP" }, null, new UnitDefinition { Turbine = Turbines.Francis }).SequenceEqual(["MEAS#0", "MEAS#178", "MEAS#171"])
            && defaultsCatalog.ExpectedTags(new TestSlot { Unit = 1, Kind = "STOP_ESD" }, null, new UnitDefinition { Turbine = Turbines.CrossFlow, GuideVaneSections = 2 }).Count == 7
            && defaultsCatalog.ExpectedTags(new TestSlot { Unit = 1, Kind = "BEARING_RUN" }, null, new UnitDefinition { Turbine = Turbines.Francis }).Contains("MEAS#45"),
            "HPU, start-up, stop and bearing run defaults follow the turbine type");
        Check(!PlantPlanner.Generate(plant).Any(s => s.Kind == "WATER_HAMMER"), "Water hammer test is no longer generated");
        var oldCatalog = ProjectStore.Deserialize<TestCatalog>("{\"Version\":1,\"Kinds\":[{\"Key\":\"LOAD_REJECTION\",\"Title\":\"x\",\"Help\":\"\",\"Signals\":[\"DI#23\",\"DI#24\"]},{\"Key\":\"STARTUP\",\"Title\":\"x\",\"Help\":\"\",\"Signals\":[\"MY.TAG\"]}]}").WithDefaults();
        Check(oldCatalog.Find("LOAD_REJECTION")!.Signals.Contains("{valves}") && oldCatalog.Find("STARTUP")!.Signals.SequenceEqual(["MY.TAG"]), "Older project catalogues get the new defaults unless the user edited them");
        bool badLevels = false; try { new UnitDefinition { LoadRejectionLevels = "25, abc" }.Validate(); } catch (InvalidDataException) { badLevels = true; }
        Check(badLevels, "Invalid load rejection levels are rejected");

        var catalog = TestCatalog.Default();
        Check(slots.All(s => catalog.Find(s.Kind) is TestKind k && k.Help.Length > 40), "Every generated test has instructions in the default catalogue");
        var nozzle2 = slots.First(s => s.Kind == "NOZZLE_STEP" && s.Index == 2);
        Check(catalog.ExpectedTags(nozzle2).Contains("MEAS.TURBINE_AND_HPU.NOZZLE_2.VAL"), "Tag templates expand the actuator number");
        Check(TagMatcher.Matches("REG_INTF.PELTON_NOZZLE[1].VALUES.OUT", "\"REG_INTF.PELTON_NOZZLE[1].VALUES.OUT[%DB31.DBD434]\""), "TIA column addresses and quotes are ignored when matching tags");
        Check(!TagMatcher.Matches("MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL", "MEAS.TURBINE_AND_HPU.NOZZLE_11.VAL"), "Tag matching is exact apart from the address");

        // Changing the definition keeps entries and keeps tests that hold data.
        slots.First(s => s.Kind == "MIV_TIMES" && s.Unit == 1).OpeningTime = 42;
        var removedNozzle = slots.First(s => s.Unit == 1 && s.Kind == "NOZZLE_RAMP" && s.Index == 4);
        plant.Units[0].Nozzles = 2;
        var merged = PlantPlanner.Merge(slots, PlantPlanner.Generate(plant), id => id == removedNozzle.Id);
        Check(merged.First(s => s.Kind == "MIV_TIMES" && s.Unit == 1).OpeningTime == 42, "Regenerating the tree keeps manual entries");
        Check(merged.Single(s => s.Id == removedNozzle.Id).Retired && !merged.Any(s => s.Unit == 1 && s.Kind == "NOZZLE_STEP" && s.Index == 4), "Removed tests survive only while they hold recordings");

        // GUI: recordings assigned to tests survive a project round trip.
        plant.Units[0].Nozzles = 4;
        var window = new MainWindow(false) { ShowActivated = false, Width = 1720, Height = 980 }; window.Show();
        plant.Units[0].NominalRanges = [new NominalRange { Tag = "MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL", Name = "Nozzle 1 position", Unit = "%", Min = 0, Max = 50 }];
        var session = new TraceSession { Plant = plant, Slots = PlantPlanner.Generate(plant), Catalog = catalog };
        Invoke(window, "ReplaceWorkspace", session, null!);
        string target = session.Slots.First(s => s.Unit == 1 && s.Kind == "NOZZLE_RAMP" && s.Index == 1).Id;
        var op = window.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow).GetMethod("AddRecordings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new[] { sample }, target])!);
        var task = op.Task.Unwrap(); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var tree = (TreeView)window.FindName("PlantTree");
        Check(tree.Items.Count == 4 && ((TreeViewItem)tree.Items[3]).Tag as string == "P0/" + TestPhases.Common && ((TreeViewItem)tree.SelectedItem)?.Tag?.GetType().Name == "OpenTrace", "Project tree shows every unit plus common tests and selects the loaded recording");
        var rated = Active(window).Data.Signals[0];
        Check(rated.PercentFromProject && rated.PercentMin == 0 && rated.PercentMax == 50, "Rated values of the unit (plant definition) are the default 0 % / 100 % of its recordings");
        rated.PercentFromProject = false; rated.PercentMax = 80; Invoke(window, "Commit");
        Check(rated.PercentMax == 80, "Switching off 'Raspon iz projekta' keeps the graph's own range");
        rated.PercentFromProject = true; Invoke(window, "Commit");
        Check(rated.PercentMax == 50, "Switching it back on returns to the project's rated value");
        rated.PercentFromProject = false; rated.PercentMin = 5; rated.PercentMax = 95; Invoke(window, "Commit");
        Invoke(window, "SaveRangeToProject");
        Check(rated.PercentFromProject && plant.Units[0].NominalFor("MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL")!.Max == 95 && rated.PercentMax == 95, "A manual range can be saved as the unit's rated value for all its recordings");
        var cursorProject = Active(window);
        cursorProject.Options.ZeroCursorVisible = true; Invoke(window, "ZeroCursor_Click", window, new System.Windows.RoutedEventArgs());
        double cursorTime = cursorProject.Options.ZeroCursor;
        var cursorPlot = (ScottPlot.WPF.WpfPlot)((Grid)window.FindName("PlotHost")).Children[0];
        Check(cursorTime > cursorProject.Data.Time[0] && cursorPlot.Plot.GetPlottables<ZeroCursorOverlay>().Count() == 1, "Vertical cursor appears in the middle of the view with the values of all signals");
        Invoke(window, "ApplyZero_Click", window, new System.Windows.RoutedEventArgs());
        Check(cursorProject.Options.Zero == cursorTime && Math.Abs(cursorProject.Options.ToAxisTime(cursorTime)) < 1e-9, "Primijeni nulu grafa moves time zero to the cursor");
        using (var exported = new ScottPlot.Plot()) { PlotService.Configure(exported, cursorProject, 1); Check(!exported.GetPlottables<ZeroCursorOverlay>().Any(), "The vertical cursor is a screen aid and is not exported"); }
        cursorProject.Options.HCursors = true; Invoke(window, "HorizontalCursors_Click", window, new System.Windows.RoutedEventArgs());
        var firstPlot = (ScottPlot.WPF.WpfPlot)((Grid)window.FindName("PlotHost")).Children[0];
        Check(firstPlot.Plot.GetPlottables<HorizontalCursorOverlay>().Count() == 1 && cursorProject.Options.HCursorB > cursorProject.Options.HCursorA, "Horizontal A/B cursors appear on the left axis with ΔY");
        window.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(200));
        var cursorShot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); cursorShot.Render(window);
        var cursorPng = new PngBitmapEncoder(); cursorPng.Frames.Add(BitmapFrame.Create(cursorShot)); using (var stream = File.Create(Path.Combine(output, "Zero-cursor.png"))) cursorPng.Save(stream);
        var popup = (System.Windows.Window)Get(window, "settingsWindow");
        Invoke(window, "ShowSettings", 0); popup.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(250));
        var popupShot = new RenderTargetBitmap((int)popup.ActualWidth, (int)popup.ActualHeight, 96, 96, PixelFormats.Pbgra32); popupShot.Render(popup);
        var popupPng = new PngBitmapEncoder(); popupPng.Frames.Add(BitmapFrame.Create(popupShot)); using (var stream = File.Create(Path.Combine(output, "Settings-range.png"))) popupPng.Save(stream);
        popup.Hide();
        CaptureDialog(window, "Wizard-nominal.png"); Invoke(window, "EditPlant_Click", window, new System.Windows.RoutedEventArgs());
        Check(File.Exists(Path.Combine(output, "Wizard-nominal.png")), "Plant definition shows rated values per unit");
        var heading = ((TextBlock)window.FindName("FileTitle")).Text;
        Check(heading.Contains("Mlaznica 1") && heading.Contains("Agregat 1"), "Graph heading names the test the recording belongs to");
        window.UpdateLayout();
        var screenshot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var stream = File.Create(Path.Combine(output, "Studio-plant.png"))) encoder.Save(stream);
        string path = Path.Combine(output, "Plant.tracestudio"); Invoke(window, "SaveWorkspaceTo", path);
        var restored = TraceSession.Load(path);
        Check(restored.Plant?.Units.Count == 3 && restored.Slots.Count == session.Slots.Count && restored.Views.Single().SlotId == target && restored.Catalog?.Find("NOZZLE_RAMP") != null, "Project file stores plant definition, test tree, catalogue and recording assignment");
        // Modal dialogs: capture them, then close (closing = cancel / do not load).
        int before = ((System.Collections.ICollection)Get(window, "documents")).Count;
        CaptureDialog(window, "Assign-dialog.png");
        var skip = window.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow).GetMethod("AddRecordings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new[] { sample }, null!])!);
        var skipTask = skip.Task.Unwrap(); var skipFrame = new DispatcherFrame();
        _ = skipTask.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => skipFrame.Continue = false));
        Dispatcher.PushFrame(skipFrame); skipTask.GetAwaiter().GetResult();
        Check(((System.Collections.ICollection)Get(window, "documents")).Count == before && File.Exists(Path.Combine(output, "Assign-dialog.png")), "Loading a CSV into a plant project asks for its test; closing the dialog skips the file");
        var renamePlant = new PlantDefinition { Name = "R", Units = [new UnitDefinition { Number = 1 }] };
        var renamed = PlantPlanner.Generate(renamePlant); var renamedSlot = renamed.First(s => s.Kind == "NOZZLE_STEP");
        renamedSlot.Title = renamedSlot.CustomTitle = "Mlaznica 1 – step 10 %"; renamedSlot.Channels = [new SlotChannel("MEAS#4", "")];
        renamePlant.Units[0].Nozzles = 3;
        var regenerated = PlantPlanner.Merge(renamed, PlantPlanner.Generate(renamePlant), _ => false).Single(s => s.Id == renamedSlot.Id);
        Check(regenerated.Title == "Mlaznica 1 – step 10 %" && regenerated.Channels!.Single().Tag == "MEAS#4", "A renamed test keeps its name and measurements when the plant definition changes");
        string? asked = null; var linked = Get(window, "plantConfig");
        typeof(PlantWizard).GetField("PickConfigFile", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, (Func<System.Windows.Window, string?, string?>)((_, current) => { asked = current ?? ""; return null; }));
        CaptureDialog(window, "Plant-wizard.png");
        Invoke(window, "NewWorkspace_Click", window, new System.Windows.RoutedEventArgs());
        Check(File.Exists(Path.Combine(output, "Plant-wizard.png")) && ((System.Collections.ICollection)Get(window, "documents")).Count == before, "New project opens the plant wizard; cancelling keeps the current project");
        Check(asked != null && Get(window, "plantConfig") == linked, "New project asks right away which Plant Configurator project to link; cancelling keeps the current link");
        Invoke(window, "SaveWorkspaceTo", path); window.Close();
    }
    static void CaptureDialog(System.Windows.Window owner, string file, int attempt = 0) => owner.Dispatcher.BeginInvoke(() =>
    {
        // The dialog may open only after background work (e.g. reading a CSV); wait for it.
        var dialog = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().LastOrDefault(w => w != owner && w.IsVisible && w.Owner != null);
        if (dialog == null)
        {
            if (attempt > 200) throw new Exception("Dialog did not open: " + file);
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            retry.Tick += (_, _) => { retry.Stop(); CaptureDialog(owner, file, attempt + 1); };
            retry.Start(); return;
        }
        dialog.UpdateLayout();
        var image = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(dialog);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(Path.Combine(output, file))) png.Save(stream);
        dialog.Close();
    }, DispatcherPriority.ApplicationIdle);
}
