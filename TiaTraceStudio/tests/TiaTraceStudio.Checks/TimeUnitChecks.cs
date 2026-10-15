using System.IO;
using TiaTraceStudio;

internal static partial class Program
{
    static void TimeUnitChecks()
    {
        var p = MainWindow.NewProject(new TraceData { FileName = "Two-days.csv", Time = [0, 3600, 7200, 86400, 172800],
            Signals = [new Signal { Source = "A", Name = "Test", Unit = "bar", Values = [0, 10, 20, 30, 40] }] });
        p.Options.ShowExtrema = true; p.Options.Cursors = true; p.Options.CursorA = 3600; p.Options.CursorB = 86400; p.Options.Zero = 1800;
        p.Notes.Add(new() { Time = 7200, Value = 20, Text = "Test note" });
        var control = new ScottPlot.WPF.WpfPlot();
        foreach (string unit in new[] { "s", "min", "h", "d" })
        {
            p.Options.TimeUnit = unit; PlotService.Configure(control.Plot, p, 1); control.Plot.GetImageBytes(1200, 650, ScottPlot.ImageFormat.Png);
            var limits = control.Plot.Axes.GetLimits();
            var cursor = control.Plot.GetPlottables<CursorReadoutOverlay>().Single();
            Check(control.Plot.Axes.Bottom.Label.Text == $"Time [{unit}]" && Math.Abs(limits.Right - p.Options.ToAxisTime(172800)) < 1e-8, $"Time axis converts its coordinates and caption to {unit}");
            Check(cursor.LastRows[0].A == 10 && cursor.LastRows[0].B == 30 && cursor.Elapsed == 82800, $"Cursor values and physical elapsed time remain unchanged in {unit}");
            Check(control.Plot.GetPlottables<ExtremaOverlay>().Single().LastPlacements.All(l => l.Point.Detail.EndsWith(" " + unit)), $"Extrema timestamps use {unit}");
            var note = control.Plot.GetPlottables<ScottPlot.Plottables.VerticalLine>().Single();
            Check(Math.Abs(note.X - p.Options.ToAxisTime(7200)) < 1e-8, $"Note position converts correctly to {unit}");
            var drag = new CursorInteraction(control, p.Options, 0, 172800, () => { }, () => { });
            Check(drag.BeginDrag(cursor.HandleA.MidX, cursor.HandleA.MidY), $"Cursor remains selectable in {unit}");
            drag.DragTo(cursor.Axes.GetPixelX(p.Options.ToAxisTime(7200))); drag.EndDrag();
            Check(Math.Abs(p.Options.CursorA - 7200) < .1, $"Dragging in {unit} stores the correct physical time in seconds");
            p.Options.CursorA = 3600;
        }
        PlotService.Export(p, Path.Combine(output, "Time-days.jpg"));
        var session = new TraceSession { Projects = [p] }; string path = Path.Combine(output, "Time-units.tracestudio"); ProjectStore.Save(path, session);
        Check(TraceSession.Load(path).Projects[0].Options.TimeUnit == "d", "Project round trip retains each graph's time unit");
        p.Options.TimeUnit = "invalid"; bool rejected = false; try { PlotService.Validate(p); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid time units are rejected");
        control.Plot.Dispose();
    }
}
