using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using TiaTraceStudio;

internal static partial class Program
{
    static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static StudioProject Active(MainWindow window) => (StudioProject)Get(window, "project");
    static void AddFiles(MainWindow window, params string[] paths)
    {
        var op = window.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow).GetMethod("AddPaths", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [paths])!);
        var task = op.Task.Unwrap(); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    static void LayoutAndDocumentChecks(string sample)
    {
        var area = new SKRect(0, 0, 800, 500); var map = new PlotOccupancy(area);
        map.AddSegment(-100000, 220, 100000, 220, 5);
        Check(map.Count(new SKRect(0, 210, 800, 230)) > 0 && map.Count(new SKRect(0, 50, 800, 100)) == 0, "Curve map clips long segments and distinguishes empty plot space");
        using var paint = new SKPaint { TextSize = 14 };
        var points = new List<ExtremePoint> { new("A", "max 10 bar", SKColors.Blue, 400, 220), new("B", "max 50 rpm", SKColors.Red, 410, 220) };
        // Block the nearest candidate positions, forcing a search farther from the extrema.
        for (int y = 165; y <= 285; y += 5) map.AddSegment(220, y, 600, y);
        var cards = ExtremaOverlay.Place(points, area, paint, occupancy: map);
        Check(cards.Count == 2 && cards.All(c => map.Count(c.Bounds) == 0) && !cards[0].Bounds.IntersectsWith(cards[1].Bounds), "Automatic cards search beyond nearby candidates to avoid curves and each other");
        var full = new PlotOccupancy(area);
        for (int y = 0; y <= 500; y += 4) full.AddSegment(0, y, 800, y);
        cards = ExtremaOverlay.Place(points, area, paint, occupancy: full);
        Check(cards.Count == 2 && !cards[0].Bounds.IntersectsWith(cards[1].Bounds), "Crowded plot fallback still prevents card-to-card overlap");

        string libraryFile = Path.Combine(output, "test-signal-names.json");
        var library = new SignalNameLibrary(); var a = MainWindow.NewProject(TraceCsv.Read(sample));
        string source = a.Data.Signals[0].Source;
        library.Rename(source, "Shared name", [a]); library.Save(libraryFile);
        var b = MainWindow.NewProject(TraceCsv.Read(sample)); SignalNameLibrary.Load(libraryFile).Apply(b);
        Check(b.Data.Signals[0].Name == "Shared name" && b.Data.Signals[1].Name == "", "Persistent library applies only exact known tags and leaves unknown tags unnamed");
        b.Data.Signals[0].NameOverride = true; b.Data.Signals[0].Name = "Local name";
        library.Rename(source, "Changed shared name", [a, b]);
        Check(a.Data.Signals[0].Name == "Changed shared name" && b.Data.Signals[0].Name == "Local name", "Local override survives shared library rename");
        b.Data.Signals[1].Source = source + "_different"; library.Apply(b);
        Check(b.Data.Signals[1].Name == "", "Similar tag names never match implicitly");
        a.Data.Signals[0].Unit = "bar"; a.Data.Signals[0].Factor = .1; a.Data.Signals[0].Offset = -2;
        library.Share(a.Data.Signals[0], [a, b]); library.Save(libraryFile);
        var scaledImport = MainWindow.NewProject(TraceCsv.Read(sample)); SignalNameLibrary.Load(libraryFile).Apply(scaledImport);
        Check(scaledImport.Data.Signals[0].Unit == "bar" && scaledImport.Data.Signals[0].Factor == .1 && scaledImport.Data.Signals[0].Offset == -2 && b.Data.Signals[0].Factor == 1, "Shared unit and scaling persist, apply on import, and respect local overrides");
        var legacyLibrary = ProjectStore.Deserialize<SignalNameLibrary>("{\"Names\":{}}");
        legacyLibrary.Apply(a);
        Check(a.Data.Signals[0].Factor == .1, "Old name-only libraries preserve existing scaling");
        var oldFresh = MainWindow.NewProject(TraceCsv.Read(sample));
        var oldConfigured = MainWindow.NewProject(TraceCsv.Read(sample));
        oldConfigured.Data.Signals[0].Unit = "%"; oldConfigured.Data.Signals[0].Factor = .01; oldConfigured.Data.Signals[0].Offset = 5;
        var oldLocal = MainWindow.NewProject(TraceCsv.Read(sample)); oldLocal.Data.Signals[0].NameOverride = true;
        string oldSessionPath = Path.Combine(output, "Old-name-only.tracestudio");
        var oldSession = new TraceSession { Projects = [oldFresh, oldConfigured, oldLocal], NameLibrary = new() { Names = new() { [source] = "Shared name" } } };
        var oldJson = System.Text.Json.Nodes.JsonNode.Parse(ProjectStore.Serialize(oldSession))!;
        oldJson["NameLibrary"]!.AsObject().Remove("Scaling"); File.WriteAllText(oldSessionPath, oldJson.ToJsonString());
        var migrated = TraceSession.Load(oldSessionPath);
        var afterMigration = MainWindow.NewProject(TraceCsv.Read(sample)); migrated.NameLibrary.Apply(afterMigration);
        Check(afterMigration.Data.Signals[0].Unit == "%" && afterMigration.Data.Signals[0].Factor == .01 && afterMigration.Data.Signals[0].Offset == 5, "Version 2 name-only projects seed shared scaling from configured recordings, even after an empty recording");
        Check(migrated.Projects[0].Data.Signals[0].Unit == "%" && migrated.Projects[0].Data.Signals[0].Factor == .01 && migrated.Projects[2].Data.Signals[0].Unit == "", "Migration repairs previously imported default scaling and preserves local exceptions");
        migrated.NameLibrary.Scaling[source] = new("bar", 3, -1); ProjectStore.Save(oldSessionPath, migrated);
        var explicitScaling = TraceSession.Load(oldSessionPath).NameLibrary.Scaling[source];
        Check(explicitScaling == new SignalScaling("bar", 3, -1), "Migration never replaces explicitly saved shared scaling");

        var window = new MainWindow(false) { ShowActivated = false }; window.Show();
        string second = Path.Combine(output, "Second-trace.csv"); File.Copy(sample, second, true);
        AddFiles(window, sample, second);
        var picker = (ComboBox)window.FindName("DocumentPicker");
        Check(picker.Items.Count == 2 && picker.Visibility == Visibility.Visible, "Multi-file import keeps both documents and exposes a compact picker");
        var first = Active(window); first.Data.Signals[0].Name = "Shared pressure"; Invoke(window, "Commit");
        var firstPlot = (ScottPlot.WPF.WpfPlot)((Grid)window.FindName("PlotHost")).Children[0]; firstPlot.Plot.Axes.SetLimitsX(10, 30);
        picker.SelectedIndex = 1; var secondProject = Active(window);
        Check(secondProject != first && secondProject.Data.Signals[0].Name == "Shared pressure", "Editing a name in the GUI propagates to already loaded CSVs");
        secondProject.Data.Signals[0].Unit = "bar"; Invoke(window, "Commit");
        secondProject.Data.Signals[0].Factor = .25; secondProject.Data.Signals[0].Offset = -3; Invoke(window, "Commit");
        Check(first.Data.Signals[0].Unit == "bar" && first.Data.Signals[0].Factor == .25 && first.Data.Signals[0].Offset == -3, "Unit-only and scaling-only GUI changes propagate without renaming");
        secondProject.Data.Signals[1].Factor = 2; Invoke(window, "Commit");
        Check(first.Data.Signals[1].Factor == 2 && first.Data.Signals[1].Name == "", "Scaling shares even before a signal is named");
        secondProject.Data.Signals[0].NameOverride = true; secondProject.Data.Signals[0].Name = "Local pressure"; Invoke(window, "Commit");
        picker.SelectedIndex = 0;
        var restoredPlot = (ScottPlot.WPF.WpfPlot)((Grid)window.FindName("PlotHost")).Children[0];
        Check(restoredPlot.Plot.Axes.GetLimits().Left == 10 && restoredPlot.Plot.Axes.GetLimits().Right == 30, "Switching CSVs preserves each graph's current zoom");
        first.Data.Signals[0].Name = "Updated pressure"; Invoke(window, "Commit");
        Check(secondProject.Data.Signals[0].Name == "Local pressure", "GUI shared rename preserves a per-CSV exception");
        AddFiles(window, sample); var third = Active(window);
        Check(picker.Items.Count == 3 && third.Data.Signals[0].Name == "Updated pressure", "Later imports automatically reuse the user-entered library name");
        Check(third.Data.Signals[0].Unit == "bar" && third.Data.Signals[0].Factor == .25 && third.Data.Signals[0].Offset == -3, "Later imports reuse shared unit, factor and offset");
        third.Data.Signals[0].Unit = "kPa"; third.Data.Signals[0].Factor = 25; third.Data.Signals[0].Offset = 0;
        third.Data.Signals[0].Name = "All pressure"; Invoke(window, "NameAll_Click", window, new RoutedEventArgs());
        Check(first.Data.Signals[0].Name == "All pressure" && secondProject.Data.Signals[0].Name == "All pressure" && !secondProject.Data.Signals[0].NameOverride, "Explicit all-CSV rename also replaces local exceptions");
        Check(secondProject.Data.Signals[0].Unit == "kPa" && secondProject.Data.Signals[0].Factor == 25 && secondProject.Data.Signals[0].Offset == 0, "Explicit all-CSV action replaces local units and scaling too");
        third.Data.Signals[0].NameOverride = true; third.Data.Signals[0].Name = "Third only"; Invoke(window, "Commit");
        var session = (TraceSession)typeof(MainWindow).GetMethod("CurrentSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [false])!;
        string sessionPath = Path.Combine(output, "Multi-trace.tracestudio"); Invoke(window, "SaveWorkspaceTo", sessionPath);
        var restored = TraceSession.Load(sessionPath);
        Check(restored.Projects.Count == 3 && restored.ActiveIndex == 2 && restored.Projects[2].Data.Signals[0].NameOverride && restored.Projects[2].Data.Signals[0].Name == "Third only", "Session round trip preserves all CSVs, selection, and local overrides");
        AddFiles(window, sessionPath);
        Check(picker.Items.Count == 3 && Active(window).Data.Signals[0].Name == "Third only", "Opening a project replaces the workspace and restores all recordings and local names");
        Check(restored.NameLibrary.Names[source] == "All pressure", "Project file embeds its shared name library, independently of local exceptions");
        Active(window).Options.TimeUnit = "min"; Invoke(window, "RebindOptions");
        Invoke(window, "SaveProject_Click", window, new RoutedEventArgs());
        var savedFromToolbar = TraceSession.Load(sessionPath);
        Check(savedFromToolbar.Projects.Count == 3 && savedFromToolbar.Projects[2].Options.TimeUnit == "min" && savedFromToolbar.NameLibrary.Names[source] == "All pressure", "Main Save Project command saves every recording, its units, and the project library");
        AddFiles(window, sample);
        Check(Active(window).Data.Signals[0].Name == "All pressure", "A CSV imported after reopening uses the name library saved inside the project");
        Check(Active(window).Data.Signals[0].Unit == "kPa" && Active(window).Data.Signals[0].Factor == 25 && Active(window).Data.Signals[0].Offset == 0, "Project round trip retains shared scaling for future imports");
        var namedProject = MainWindow.NewProject(TraceCsv.Read(sample)); namedProject.Data.Signals[1].Name = "Previously saved channel";
        string namedPath = Path.Combine(output, "Saved-names.tiatrace"); ProjectStore.Save(namedPath, namedProject);
        Invoke(window, "SaveWorkspaceTo", sessionPath);
        AddFiles(window, namedPath); AddFiles(window, sample); picker.SelectedIndex = picker.Items.Count - 1;
        Check(Active(window).Data.Signals[1].Name == "Previously saved channel" && Active(window).Data.Signals[0].Name == "", "Legacy project migrates its names while remaining isolated from the previous project's library");
        var shown = Active(window);
        for (int i = 0; i < shown.Data.Signals.Count; i++) { shown.Data.Signals[i].Name = $"Kanal {i + 1}"; shown.Data.Signals[i].Unit = "%"; shown.Data.Signals[i].NameOverride = true; }
        shown.Options.ShowExtrema = true; Invoke(window, "RebindOptions"); Invoke(window, "Commit"); window.UpdateLayout();
        var screenshot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var stream = File.Create(Path.Combine(output, "Studio-multiple.png"))) encoder.Save(stream);
        PlotService.Export(shown, Path.Combine(output, "Curve-aware-labels.jpg"));
        Invoke(window, "CaptureDocument");
        foreach (object doc in (System.Collections.IEnumerable)Get(window, "documents"))
        {
            var p = (StudioProject)doc.GetType().GetProperty("Project")!.GetValue(doc)!;
            doc.GetType().GetProperty("Saved")!.SetValue(doc, ProjectStore.Serialize(p));
        }
        typeof(MainWindow).GetField("savedSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, ProjectStore.Serialize(shown));
        Invoke(window, "SaveWorkspaceTo", Path.Combine(output, "Final-workspace.tracestudio")); window.Close();
    }
}
