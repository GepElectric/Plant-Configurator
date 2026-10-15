using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaTraceStudio;

internal static partial class Program
{
    static int count;
    static string output = "";
    static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); count++; }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            output = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/checks"); Directory.CreateDirectory(output);
            string sample = args.Length > 0 ? args[0] : throw new Exception("Pass real trace CSV path.");
            var data = TraceCsv.Read(sample);
            Check(data.Time.Length == 393 && data.Signals.Count == 3, "Real TIA export: 393 samples / 3 channels");
            Check(Math.Abs(data.Time[^1] - 98.000335016) < 1e-9 && data.Time[0] == 0, "Decimal timestamp subtraction preserves sub-millisecond precision");
            Check(data.Warnings.Count == 0, "Real trace imports without warnings");
            Check(data.Signals.All(s => s.Name == "" && s.Unit == ""), "CSV import never infers signal names or units");
            bool missingNamesRejected = false;
            try { SignalDisplay.RequireNames(MainWindow.NewProject(data)); } catch (InvalidDataException) { missingNamesRejected = true; }
            Check(missingNamesRejected, "Export requires user-entered names");
            string fixture = Path.Combine(output, "semicolon.csv");
            File.WriteAllText(fixture, "Siemens trace\nSample;X(ms);\"Signal; A\";B\n0;1000,1;1,5;true\n1;1250,1;;false\n2;1500,1;3,5;true\n3;1500,1;9;false\n4;2250,1;4,5;false\nbad;time;row\n");
            var semicolon = TraceCsv.Read(fixture);
            Check(semicolon.Time.Length == 4 && semicolon.Signals[0].Values[0] == 1.5 && double.IsNaN(semicolon.Signals[0].Values[1]), "Semicolon, decimal comma, quoted delimiter, missing values");
            Check(semicolon.Signals[1].Values[0] == 1 && semicolon.Signals[1].Values[1] == 0 && semicolon.Warnings.Count == 4, "Boolean values and warnings for malformed, duplicate, missing and gapped records");
            Check(AnalysisService.ValueAt([0, 1, 2], [0, 2, 4], .5) == 1 && AnalysisService.ValueAt([0, 1], [0, double.NaN], .5) == null, "Cursor interpolation never bridges NaN");
            var analytic = new TraceData { Time = [0, 1, 2], Signals = [new() { Name = "A", Values = [1, 2, 3], Factor = 2, Offset = 1 }] };
            var stats = AnalysisService.Statistics(analytic, new() { Start = 0, End = 2, CursorA = .5, CursorB = 1.5 })[0];
            Check(stats.Mean == 5 && stats.Minimum == 3 && stats.Maximum == 7 && stats.Delta == 2 && Math.Abs(stats.StdDev - Math.Sqrt(8.0 / 3)) < 1e-12, "Scaled interval statistics and cursor delta");
            Check(AnalysisService.Derivative(analytic.Signals[0], analytic.Time).Values[1] == 2, "Derivative uses physical time and scaled values");
            var difference = AnalysisService.Difference(analytic.Signals[0], new() { Values = [1, 1, 1] });
            Check(difference.Values.SequenceEqual(new double[] { 2, 4, 6 }), "Tracking error calculation");
            var project = MainWindow.NewProject(data);
            project.Options.Title = "CSV trace · test prikaza";
            // Explicit test input simulates user-entered neutral names, never semantic inference.
            for (int i = 0; i < data.Signals.Count; i++) data.Signals[i].Name = $"Kanal {i + 1}";
            project.Options.ShowExtrema = true;
            string projectFile = Path.Combine(output, "Nozzle1.tiatrace"); ProjectStore.Save(projectFile, project);
            var loaded = ProjectStore.Load(projectFile);
            Check(loaded.Data.Time.SequenceEqual(data.Time) && loaded.Notes.Count == 0, "Portable project round trip; no automatic event annotations");
            var missingProject = MainWindow.NewProject(semicolon); string missingPath = Path.Combine(output, "missing.tiatrace"); ProjectStore.Save(missingPath, missingProject);
            Check(double.IsNaN(ProjectStore.Load(missingPath).Data.Signals[0].Values[1]), "NaN values survive project round trip");
            var preset = ProjectStore.Preset(project); var next = MainWindow.NewProject(TraceCsv.Read(sample)); next.Options.End = 30;
            Check(ProjectStore.ApplyPreset(next, preset) == 3 && next.Options.End == 30 && !next.Options.Cursors, "Preset matches source tags and preserves target range");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            DisplayChecks();
            EditingChecks();
            CursorChecks();
            LayoutAndDocumentChecks(sample);
            ToolbarChecks(sample);
            TimeUnitChecks();
            PlantChecks(sample);
            RecorderChecks();
            PlotService.Export(project, Path.Combine(output, "Nozzle1_Reg_Ramp.jpg"));
            PlotService.Export(project, Path.Combine(output, "Nozzle1_Reg_Ramp.png"));
            var jpg = new BitmapImage(new Uri(Path.Combine(output, "Nozzle1_Reg_Ramp.jpg")));
            Check(jpg.PixelWidth == 3000 && jpg.PixelHeight == 1800 && new FileInfo(Path.Combine(output, "Nozzle1_Reg_Ramp.jpg")).Length > 50000, "Real JPEG rendered and decoded at 3000 × 1800");
            project.Data.Signals[2].Panel = 2; project.Options.Zero = 5;
            PlotService.Export(project, Path.Combine(output, "Nozzle1_Stacked.jpg"));
            Check(File.Exists(Path.Combine(output, "Nozzle1_Stacked.jpg")), "Stacked panel JPEG with relative zero and right axis");
            var invalid = ProjectStore.Clone(project); invalid.Options.End = invalid.Options.Start;
            bool rejected = false; try { PlotService.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Reject invalid time range");
            var window = new MainWindow(false) { ShowActivated = false };
            // Exercise real WPF construction, bindings, plot hosting and change history.
            typeof(MainWindow).GetField("project", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, project);
            Invoke(window, "Bind"); Invoke(window, "ResetHistory"); Invoke(window, "Render");
            window.Width = 1480; window.Height = 960;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check(((Grid)window.FindName("PlotHost")).Children.Count == 2, "WPF window builds with two live plot panels");
            var copyButton = (Button)window.FindName("CopyImageButton");
            var buttonRow = (StackPanel)copyButton.Parent;
            Check(copyButton.IsVisible && buttonRow.Children[buttonRow.Children.IndexOf(copyButton) + 1] is Button export && export.Content.ToString()!.StartsWith("Izvezi"), "Copy image is directly beside Export");
            var measurement = (CheckBox)window.FindName("MeasurementToggle");
            measurement.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            measurement.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(project.Options.Cursors && project.Options.CursorA > project.Options.Start && project.Options.CursorB < project.Options.End, "Main toolbar enables two cursors inside the visible interval");
            var originalTitle = project.Options.Title; project.Options.Title = "Edited title"; Invoke(window, "RebindOptions"); Invoke(window, "Commit");
            Invoke(window, "Undo_Click", window, new RoutedEventArgs());
            var current = (StudioProject)typeof(MainWindow).GetField("project", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Check(current.Options.Title == originalTitle, "Undo restores confirmed chart state");
            Invoke(window, "Redo_Click", window, new RoutedEventArgs());
            current = (StudioProject)typeof(MainWindow).GetField("project", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Check(current.Options.Title == "Edited title", "Redo restores edited chart state");
            var compared = AnalysisService.Compare(analytic, new TraceData { FileName = "Second.csv", Time = [0, 1, 2], Signals = [new() { Values = [10, 20, 30] }] }, 1);
            Check(double.IsNaN(compared[0].Values[0]) && compared[0].Values[1] == 10 && compared[0].Values[2] == 20, "Comparison shifts physical time and leaves non-overlap missing");
            current.Options.Title = originalTitle; Invoke(window, "Render");
            window.UpdateLayout();
            var screenshot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var stream = File.Create(Path.Combine(output, "Studio-preview.png"))) encoder.Save(stream);
            Check(!((Expander)window.FindName("AdvancedTools")).IsExpanded, "Advanced analysis is collapsed in the simplified UI");
            current = MainWindow.NewProject(TraceCsv.Read(sample));
            typeof(MainWindow).GetField("project", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, current);
            Invoke(window, "Bind"); Invoke(window, "ResetHistory"); Invoke(window, "Render"); window.UpdateLayout();
            Check(current.Data.Signals.All(s => s.Name == "") && ((TextBlock)window.FindName("NamingHint")).Visibility == Visibility.Visible, "Import GUI leaves names empty and shows required-name guidance");
            screenshot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(window);
            encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var stream = File.Create(Path.Combine(output, "Studio-import.png"))) encoder.Save(stream);
            File.WriteAllText(Path.Combine(output, "results.txt"), $"{count} checks passed at {DateTimeOffset.Now:O}\n");
            Console.WriteLine($"\n{count} checks passed. Artifacts: {output}");
            typeof(MainWindow).GetField("savedSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, ProjectStore.Serialize(current));
            Invoke(window, "SaveWorkspaceTo", Path.Combine(output, "Final-single.tracestudio")); window.Close(); application.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }
    static void DisplayChecks()
    {
        var data = new TraceData
        {
            FileName = "Synthetic.csv", Time = Enumerable.Range(0, 101).Select(i => (double)i).ToArray(),
            Signals = [
                new() { Source = "A", Name = "A", Unit = "bar", Color = "#2563EB", Values = Enumerable.Range(0, 101).Select(i => i / 10.0).ToArray(), PercentMin = 0, PercentMax = 10 },
                new() { Source = "B", Name = "B", Unit = "m", Color = "#E88821", Values = Enumerable.Range(0, 101).Select(i => (double)i).ToArray(), PercentMin = 0, PercentMax = 100 },
                new() { Source = "C", Name = "C", Unit = "rpm", Color = "#109C83", Values = Enumerable.Range(0, 101).Select(i => i * 15.0).ToArray(), PercentMin = 0, PercentMax = 1500 }
            ]
        };
        var p = MainWindow.NewProject(data); p.Options.Title = "Sintetički test · bar / m / rpm"; p.Options.ShowExtrema = true;
        using var plot = new ScottPlot.Plot(); PlotService.Configure(plot, p, 1);
        Check(plot.GetPlottables<ScottPlot.Plottables.Scatter>().Select(s => s.Axes.YAxis).Distinct().Count() == 3, "Three physical units create three independent Y axes");
        plot.GetImageBytes(1100, 600, ScottPlot.ImageFormat.Png);
        var overlay = plot.GetPlottables<ExtremaOverlay>().Single();
        Check(overlay.LastPlacements.Count == 6 && overlay.OmittedCount == 0, "Six coincident extrema receive six labels");
        Check(!overlay.LastPlacements.Where((a, i) => overlay.LastPlacements.Skip(i + 1).Any(b => a.Bounds.IntersectsWith(b.Bounds))).Any(), "Extrema labels do not overlap in actual rendering");
        PlotService.Export(p, Path.Combine(output, "Mixed-units.jpg"));
        p.Options.DisplayMode = ChartOptions.PercentMode;
        Check(SignalDisplay.Values(data.Signals[0], p.Options)[50] == 50 && SignalDisplay.Values(data.Signals[2], p.Options)[100] == 100, "Normalization uses explicit physical ranges");
        var extrapolated = new Signal { Values = [-5, 15], PercentMin = 0, PercentMax = 10 };
        Check(SignalDisplay.Values(extrapolated, p.Options).SequenceEqual(new double[] { -50, 150 }), "Normalization does not clamp out-of-range values");
        bool badRange = false; try { SignalDisplay.Values(new Signal { Values = [1, 2] }, p.Options); } catch (InvalidDataException) { badRange = true; }
        Check(badRange, "Percent mode rejects missing user range");
        var position = new Signal { Source = "MEAS.TURBINE_AND_HPU.NOZZLE_1.VAL", Unit = "%", Values = [0, 55, 100] };
        var temperature = new Signal { Source = "MEAS.GENERATOR_MECHANICAL.BEARING_TEMPERATURE_1.VAL", Unit = "°C", Values = [20, 85] };
        var regOutput = new Signal { Source = "PLC agregata 1: REG_INTF.SPEED.VALUES.OUT[%DB31.DBD122]", Values = [-100, 40] };
        Check(SignalDisplay.Values(position, p.Options).SequenceEqual(position.Values) && SignalDisplay.Values(temperature, p.Options).SequenceEqual(temperature.Values) && SignalDisplay.Values(regOutput, p.Options).SequenceEqual(regOutput.Values),
            "Positions (%), temperatures (°C) and regulator outputs (−100…100 %) need no range in the normalized graph");
        var ownRange = new Signal { Unit = "%", Values = [0, 50], PercentFromProject = false, PercentMin = 0, PercentMax = 50 };
        Check(SignalDisplay.Values(ownRange, p.Options)[1] == 100, "An explicit range of the graph still applies to a % signal");
        var mixed = MainWindow.NewProject(new TraceData { FileName = "mix.csv", Time = [0, 1], Signals = [new Signal { Source = "P", Name = "Mlaznica", Unit = "%", Values = [0, 100] }, new Signal { Source = "T", Name = "Ležaj", Unit = "°C", Values = [20, 85] }] });
        mixed.Options.DisplayMode = ChartOptions.PercentMode;
        using (var mixedPlot = new ScottPlot.Plot()) { PlotService.Configure(mixedPlot, mixed, 1); Check(mixedPlot.Axes.Left.Label.Text == "Normalizirano [%] · temperature [°C]", "Normalized axis is labelled for percent and temperature"); }
        PlotService.Configure(plot, p, 1);
        Check(plot.GetPlottables<ScottPlot.Plottables.Scatter>().Select(s => s.Axes.YAxis).Distinct().Count() == 1, "Percent mode uses one common Y axis and removes extra axes");
        plot.GetImageBytes(900, 500, ScottPlot.ImageFormat.Png);
        overlay = plot.GetPlottables<ExtremaOverlay>().Single();
        Check(overlay.LastPlacements.Count == 6 && overlay.OmittedCount == 0, "Coincident normalized extrema stay readable in a smaller view");
        PlotService.Export(p, Path.Combine(output, "Normalized.jpg"));
        // Recorded from a PLC: a gap of NaN samples, units mode, zero cursor and horizontal cursors on.
        var gap = MainWindow.NewProject(new TraceData { FileName = "gap.csv", Time = Enumerable.Range(0, 101).Select(i => i / 10.0).ToArray(), Signals = [
            new() { Source = "P", Name = "Snaga", Unit = "kW", Color = "#2563EB", Values = Enumerable.Range(0, 101).Select(i => i is > 40 and < 46 ? double.NaN : 2070 * Math.Sin(i / 30.0)).ToArray() },
            new() { Source = "N", Name = "Brzina", Unit = "rpm", Color = "#E88821", Values = Enumerable.Range(0, 101).Select(i => 1500 + 60 * Math.Cos(i / 12.0)).ToArray() }] });
        gap.Options.Title = "NaN i min/max"; gap.Options.ShowExtrema = true; gap.Options.ZeroCursorVisible = true; gap.Options.ZeroCursor = 3; gap.Options.HCursors = true; gap.Options.HCursorA = 500; gap.Options.HCursorB = 1500;
        using (var gapPlot = new ScottPlot.Plot())
        {
            PlotService.Configure(gapPlot, gap, 1); gapPlot.GetImageBytes(1100, 600, ScottPlot.ImageFormat.Png);
            var gapLabels = gapPlot.GetPlottables<ExtremaOverlay>().Single().LastPlacements;
            Check(gapLabels.Count == 4 && gapLabels.Any(l => l.Point.Name == "Snaga" && l.Point.Detail.StartsWith("max 2070 kW")) && gapLabels.Any(l => l.Point.Name == "Snaga" && l.Point.Detail.Replace("−", "-").StartsWith("min -394")) && !gapLabels.Any(l => l.Point.Detail.Contains("NaN")),
                "Min/max ignore NaN samples and show real extrema with cursors on");
        }
        PlotService.Export(gap, Path.Combine(output, "Extrema-gap.jpg"));
        var preset = ProjectStore.Preset(p); var copy = MainWindow.NewProject(ProjectStore.Clone(data)); ProjectStore.ApplyPreset(copy, preset);
        Check(copy.Data.Signals[2].PercentMax == 1500 && copy.Options.DisplayMode == ChartOptions.PercentMode, "User normalization ranges persist in presets");
        p.Options.Start = 20; p.Options.End = 40; PlotService.Configure(plot, p, 1); plot.GetImageBytes(1100, 600, ScottPlot.ImageFormat.Png);
        overlay = plot.GetPlottables<ExtremaOverlay>().Single();
        Check(overlay.LastPlacements.All(l => l.Point.Detail.Contains("20 s") || l.Point.Detail.Contains("40 s")), "Extrema reflect the selected time interval");
        p.Options.DisplayMode = ChartOptions.UnitsMode; data.Signals[0].Unit = "m"; PlotService.Configure(plot, p, 1);
        Check(plot.GetPlottables<ScottPlot.Plottables.Scatter>().Select(s => s.Axes.YAxis).Distinct().Count() == 2, "Identical entered units share an axis");
        data.Signals = [new() { Name = "Konstanta", Unit = "bar", Values = Enumerable.Repeat(5d, 101).ToArray() }];
        PlotService.Configure(plot, p, 1); plot.GetImageBytes(900, 500, ScottPlot.ImageFormat.Png);
        Check(plot.GetPlottables<ExtremaOverlay>().Single().LastPlacements.Single().Point.Detail.StartsWith("min = max"), "Constant signals use one combined min=max label");
    }
    static void EditingChecks()
    {
        var p = MainWindow.NewProject(new TraceData { FileName = "Editing.csv", Time = [0, 1, 2], Signals = [
            new() { Name = "A", Unit = "bar", Values = [1, 3, 2] },
            new() { Name = "B", Unit = "bar", Color = "#E88821", Values = [2, 4, 3] }
        ] });
        p.Options.ShowExtrema = true;
        var control = new ScottPlot.WPF.WpfPlot();
        PlotService.Configure(control.Plot, p, 1);
        Check(control.Plot.Axes.Bottom.Label.Text == "Time [s]", "Default time-axis caption is English");
        p.Options.TimeAxisLabel = "Elapsed time";
        PlotService.Configure(control.Plot, p, 1);
        Check(control.Plot.Axes.Bottom.Label.Text == "Elapsed time [s]", "Custom time-axis caption reaches the renderer");
        control.Plot.GetImageBytes(900, 500, ScottPlot.ImageFormat.Png);
        var overlay = control.Plot.GetPlottables<ExtremaOverlay>().Single();
        var min = overlay.LastPlacements.Single(l => ReferenceEquals(l.Point.Options, p.Data.Signals[0].MinimumLabel));
        Check(overlay.HitTest(min.Bounds.MidX, min.Bounds.MidY) == min, "Label hit testing identifies the actual drawn card");
        int commits = 0;
        var interaction = new ExtremaInteraction(control, () => commits++);
        var originalLimits = control.Plot.Axes.GetLimits();
        Check(interaction.BeginDrag(min.Bounds.MidX, min.Bounds.MidY) && !control.UserInputProcessor.IsEnabled, "Label drag suppresses graph navigation");
        interaction.DragTo(450, 220); interaction.EndDrag();
        Check(commits == 1 && control.UserInputProcessor.IsEnabled && p.Data.Signals[0].MinimumLabel.IsManual && control.Plot.Axes.GetLimits().Equals(originalLimits), "Dragging changes label position once without panning axes");
        control.Plot.GetImageBytes(900, 500, ScottPlot.ImageFormat.Png);
        min = overlay.LastPlacements.Single(l => ReferenceEquals(l.Point.Options, p.Data.Signals[0].MinimumLabel));
        double nx = p.Data.Signals[0].MinimumLabel.X!.Value, ny = p.Data.Signals[0].MinimumLabel.Y!.Value;
        control.Plot.ScaleFactor = 2;
        control.Plot.GetImageBytes(1800, 1000, ScottPlot.ImageFormat.Png);
        var doubled = overlay.LastPlacements.Single(l => ReferenceEquals(l.Point.Options, p.Data.Signals[0].MinimumLabel));
        Check(Math.Abs(min.Bounds.Left - doubled.Bounds.Left) < .01 && Math.Abs(min.Bounds.Top - doubled.Bounds.Top) < .01, "Manual label geometry survives a 2x pixel scale");
        control.Plot.GetImageBytes(3000, 1800, ScottPlot.ImageFormat.Png);
        var resized = overlay.LastPlacements.Single(l => ReferenceEquals(l.Point.Options, p.Data.Signals[0].MinimumLabel));
        Check(Math.Abs((resized.Bounds.Left - overlay.LastArea.Left) / (overlay.LastArea.Width - resized.Bounds.Width) - nx) < 1e-5 && Math.Abs((resized.Bounds.Top - overlay.LastArea.Top - 26) / (overlay.LastArea.Height - 26 - resized.Bounds.Height) - ny) < 1e-5, "Manual positions remain relative across different export dimensions");
        var max = overlay.LastPlacements.Single(l => ReferenceEquals(l.Point.Options, p.Data.Signals[0].MaximumLabel));
        var menu = interaction.CreateMenu(max);
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        control.Plot.GetImageBytes(3000, 1800, ScottPlot.ImageFormat.Png);
        Check(p.Data.Signals[0].MaximumLabel.Hidden && overlay.LastPlacements.Count == 3 && overlay.OmittedCount == 0, "Right-click hide removes only that label and is not counted as overflow");
        p.Data.Signals[0].Name = "Renamed A";
        p.Data.Signals.Reverse();
        string path = Path.Combine(output, "Custom-labels.tiatrace"); ProjectStore.Save(path, p);
        var loaded = ProjectStore.Load(path);
        Check(loaded.Options.TimeAxisLabel == "Elapsed time" && loaded.Data.Signals[1].MinimumLabel.X == nx && loaded.Data.Signals[1].MaximumLabel.Hidden, "Positions and hidden state survive project save, rename and reorder");
        using var finalPlot = new ScottPlot.Plot(); PlotService.Configure(finalPlot, loaded, 1);
        finalPlot.GetImageBytes(3000, 1800, ScottPlot.ImageFormat.Png);
        var finalLabels = finalPlot.GetPlottables<ExtremaOverlay>().Single().LastPlacements;
        Check(finalLabels.Count == 3 && finalLabels.Any(l => l.Point.Name == "Renamed A" && l.Point.Options!.IsManual), "Fresh final renderer honors stored positions and hidden labels");
        PlotService.Export(loaded, Path.Combine(output, "Custom-labels.jpg"));
        var resetMenu = interaction.CreateMenu(resized);
        ((MenuItem)resetMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(!p.Data.Signals[1].MinimumLabel.IsManual, "Context-menu reset restores automatic positioning");
        p.Data.Signals[1].MaximumLabel.Hidden = false;
        control.Plot.GetImageBytes(3000, 1800, ScottPlot.ImageFormat.Png);
        Check(overlay.LastPlacements.Count == 4, "Hidden extrema can be restored");
        Check(ProjectStore.Clone(ProjectStore.Preset(loaded)).Options.TimeAxisLabel == "Elapsed time", "Time-axis caption is retained by templates");
        control.Plot.Dispose();
    }
    static void CursorChecks()
    {
        var p = MainWindow.NewProject(new TraceData { FileName = "Cursor-test.csv", Time = [0, 1, 2, 3], Signals = [
            new() { Name = "Signal A", Unit = "bar", Values = [0, 2, 4, 6], Factor = 2, Offset = 1, PercentMin = 1, PercentMax = 13 },
            new() { Name = "Signal B", Unit = "rpm", Color = "#E88821", Values = [0, 100, 200, 300], PercentMin = 0, PercentMax = 300 },
            new() { Name = "Hidden", Values = [1, 1, 1, 1], Visible = false }
        ] });
        p.Options.Cursors = true; p.Options.CursorA = .5; p.Options.CursorB = 2.5; p.Options.Zero = .25; p.Options.ShowExtrema = true;
        var control = new ScottPlot.WPF.WpfPlot();
        PlotService.Configure(control.Plot, p, 1); control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
        var cursor = control.Plot.GetPlottables<CursorReadoutOverlay>().Single();
        Check(cursor.Elapsed == 2 && cursor.LastRows.Count == 2 && cursor.LastRows[0].A == 3 && cursor.LastRows[0].B == 11 && cursor.LastRows[1].A == 50 && cursor.LastRows[1].B == 250, "Cursor table shows interpolated scaled values for all visible variables and units");
        Check(cursor.LastRows[0].Delta == 8 && cursor.LastRows[1].Unit == "rpm" && cursor.OmittedRows == 0, "Cursor readout includes signed signal differences without omitted rows");
        Check(cursor.LastRows[0].Rate == 4 && cursor.LastRows[0].Min == 3 && cursor.LastRows[0].Max == 11 && cursor.LastRows[0].Mean == 7, "Cursor analysis: Δy/Δt and minimum / maximum between the cursors");
        var extrema = control.Plot.GetPlottables<ExtremaOverlay>().Single();
        Check(extrema.LastPlacements.Count == 4 && !extrema.LastPlacements.Any(l => cursor.ReservedAreas.Any(r => r.IntersectsWith(l.Bounds))), "Automatic extrema avoid cursor values and duration labels");
        p.Options.CursorA = 2.5; p.Options.CursorB = .5; control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
        Check(cursor.Elapsed == 2 && cursor.LastRows[0].Delta == -8, "Crossing cursors keeps positive elapsed time and signed B-minus-A values");
        p.Options.CursorA = p.Options.CursorB = 1; control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
        Check(cursor.Elapsed == 0 && cursor.HitTest(cursor.HandleA.MidX, cursor.HandleA.MidY) == "A" && cursor.HitTest(cursor.HandleB.MidX, cursor.HandleB.MidY) == "B", "Coincident cursors have distinct draggable handles");
        int previews = 0, commits = 0; var limits = control.Plot.Axes.GetLimits();
        var interaction = new CursorInteraction(control, p.Options, 0, 3, () => previews++, () => commits++);
        Check(interaction.BeginDrag(cursor.HandleA.MidX, cursor.HandleA.MidY) && !control.UserInputProcessor.IsEnabled, "Cursor drag disables graph pan");
        interaction.DragTo(cursor.Axes.GetPixelX(.75 - p.Options.Zero)); interaction.EndDrag();
        Check(Math.Abs(p.Options.CursorA - .75) < 1e-6 && previews == 1 && commits == 1 && control.UserInputProcessor.IsEnabled && limits.Equals(control.Plot.Axes.GetLimits()), "Cursor drag honors shifted time origin and preserves graph view");
        control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
        Check(Math.Abs(cursor.LastRows[0].A!.Value - 4) < 1e-6, "Readout updates live without rebuilding the plot");
        p.Options.CursorA = -.5; control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
        Check(cursor.LastRows.All(r => r.A == null), "Out-of-record cursors do not extrapolate");
        p.Options.CursorA = .5; p.Options.CursorB = 2.5;
        p.Data.Signals[0].Values[1] = double.NaN;
        PlotService.Configure(control.Plot, p, 1); control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
        Check(control.Plot.GetPlottables<CursorReadoutOverlay>().Single().LastRows[0].A == null, "Missing samples are shown as unavailable cursor values");
        p.Data.Signals[0].Values[1] = 2;
        p.Options.DisplayMode = ChartOptions.PercentMode;
        PlotService.Configure(control.Plot, p, 1); control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
        cursor = control.Plot.GetPlottables<CursorReadoutOverlay>().Single();
        var physicalA = AnalysisService.ValueAt(p.Data.Time, p.Data.Signals[0].Scaled(), p.Options.CursorA);
        Check(cursor.LastRows[0].Unit == p.Data.Signals[0].Unit && cursor.LastRows[0].A == physicalA, "Cursor table shows physical values and units also in the normalized view");
        p.Options.DisplayMode = ChartOptions.UnitsMode;
        PlotService.Export(p, Path.Combine(output, "Cursor-measurement.jpg"));
        string path = Path.Combine(output, "Cursor-measurement.tiatrace"); ProjectStore.Save(path, p);
        var loaded = ProjectStore.Load(path);
        Check(loaded.Options.Cursors && loaded.Options.CursorA == .5 && loaded.Options.CursorB == 2.5, "Cursor measurement persists in projects");
        p.Options.Cursors = false; PlotService.Configure(control.Plot, p, 1);
        Check(!control.Plot.GetPlottables<CursorReadoutOverlay>().Any(), "Disabling cursors removes measurement from final rendering");
        control.Plot.Dispose();
    }
    static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(target, args);
}
