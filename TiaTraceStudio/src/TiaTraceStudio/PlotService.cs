using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScottPlot;

namespace TiaTraceStudio;

public static class PlotService
{
    /// <summary>Texts drawn on the graph in English (Word report).</summary>
    public static bool English { get; set; }
    public static int[] Panels(StudioProject project) => project.Data.Signals.Where(s => s.Visible).Select(s => s.Panel).Distinct().Order().ToArray();
    public static void Configure(Plot plot, StudioProject project, int panel)
    {
        plot.Clear();
        foreach (var oldAxis in plot.Axes.GetYAxes().Where(a => a != plot.Axes.Left && a != plot.Axes.Right).ToArray()) plot.Axes.Remove(oldAxis);
        var o = project.Options;
        bool percent = o.DisplayMode == ChartOptions.PercentMode;
        var x = project.Data.Time.Select(o.ToAxisTime).ToArray();
        var visible = project.Data.Signals.Select((s, i) => (Signal: s, Index: i)).Where(s => s.Signal.Visible && s.Signal.Panel == panel).ToArray();
        var axes = new Dictionary<string, IYAxis>();
        var extrema = new List<ExtremaSeries>();
        var named = new List<ScottPlot.Plottables.Scatter>();
        foreach (var (signal, index) in visible)
        {
            string key = percent ? "percent" : SignalDisplay.AxisKey(signal, index);
            if (!axes.TryGetValue(key, out var axis))
            {
                int n = axes.Count;
                axis = n == 0 ? plot.Axes.Left : n == 1 ? plot.Axes.Right : n % 2 == 0 ? plot.Axes.AddLeftAxis() : plot.Axes.AddRightAxis();
                axes.Add(key, axis);
                axis.Label.Text = percent ? (visible.Any(v => SignalDisplay.IsTemperature(v.Signal)) ? (English ? "Normalized [%] · temperatures [°C]" : "Normalizirano [%] · temperature [°C]") : English ? "Normalized [%]" : "Normalizirano [%]") : string.IsNullOrWhiteSpace(signal.Unit) ? (string.IsNullOrWhiteSpace(signal.Name) ? $"Y{index + 1}" : signal.Name.Trim()) : signal.Unit.Trim();
                axis.Label.FontSize = o.FontSize;
                axis.TickLabelStyle.FontSize = o.FontSize - 2;
                plot.Axes.Color(axis, axes.Count == 1 && percent ? ScottPlot.Colors.Black : ScottPlot.Color.FromHex(signal.Color));
            }
            var y = SignalDisplay.Values(signal, o);
            var scatter = plot.Add.Scatter(x, y);
            string name = SignalDisplay.PlotName(signal, index);
            scatter.LegendText = string.IsNullOrWhiteSpace(signal.Unit) ? name : $"{name} [{signal.Unit}]";
            scatter.Color = ScottPlot.Color.FromHex(signal.Color);
            scatter.LineWidth = (float)signal.Width;
            scatter.MarkerSize = 0;
            scatter.LinePattern = signal.Style == "Crtkana" ? LinePattern.Dashed : signal.Style == "Točkasta" ? LinePattern.Dotted : LinePattern.Solid;
            scatter.Axes.YAxis = axis;
            named.Add(scatter);
            AddInvalid(plot, x, y, axis, (float)signal.Width);
            extrema.Add(new(name, signal.Unit, signal.Color, x, y, signal.Scaled(), axis, signal));
        }
        plot.XLabel(string.IsNullOrWhiteSpace(o.TimeAxisLabel) ? $"[{o.TimeUnit}]" : $"{o.TimeAxisLabel.Trim()} [{o.TimeUnit}]");
        plot.Axes.Left.IsVisible = true;
        plot.Axes.Right.IsVisible = axes.Count > 1;
        if (axes.Count < 2) plot.Axes.Right.Label.Text = "";
        plot.Axes.Bottom.Label.FontSize = o.FontSize;
        plot.Axes.Bottom.TickLabelStyle.FontSize = o.FontSize - 2;
        plot.Title(Panels(project).Length > 1 ? $"{o.Title} · {panel}" : o.Title);
        plot.Axes.Title.Label.FontSize = o.FontSize + 3;
        plot.Axes.Title.Label.ForeColor = ScottPlot.Colors.Black;
        // Shared margins keep panel time axes aligned; allocate space for every unit axis.
        int maxAxes = Panels(project).Select(p => project.Data.Signals.Select((s, i) => (s, i)).Where(a => a.s.Visible && a.s.Panel == p).Select(a => SignalDisplay.AxisKey(a.s, a.i)).Distinct().Count()).DefaultIfEmpty(1).Max();
        if (percent) maxAxes = 1;
        float margin = Math.Max(70, o.FontSize * 5.5f);
        var plotPadding = new PixelPadding(margin * ((maxAxes + 1) / 2), maxAxes > 1 ? margin * (maxAxes / 2) : 20, 45 + o.FontSize * 1.2f, 55);
        plot.Grid.IsVisible = o.Grid;
        plot.Legend.FontSize = o.FontSize - 1;
        // Min/max cards and cursor tables replace the signal legend; the recording-error key stays on every graph.
        bool replaced = o.ShowExtrema || o.Cursors;
        if (replaced) foreach (var scatter in named) scatter.LegendText = "";
        AddInvalidLegend(plot);
        // The legend sits below the time axis so it never covers the curves; the bottom margin grows with its rows.
        plot.Legend.IsVisible = false;
        BottomLegend.Attach(plot, o.Legend ? new BottomLegend(o.FontSize - 1, 45 + o.FontSize * 0.6f) : null);
        plot.Layout.LayoutEngine = new LegendBelowLayout(plotPadding, o.FontSize - 1, o.Legend);
        plot.Axes.AutoScale();
        // Autoscale only finite values inside the selected time range, per physical unit.
        foreach (var axis in axes.Values)
        {
            var values = extrema.Where(s => s.YAxis == axis).SelectMany(s => s.Y.Where((v, i) => project.Data.Time[i] >= o.Start && project.Data.Time[i] <= o.End && double.IsFinite(v))).ToArray();
            // Invalid samples are drawn at 0, so 0 must stay visible on that axis.
            bool invalid = extrema.Where(s => s.YAxis == axis).Any(s => s.Y.Where((v, i) => project.Data.Time[i] >= o.Start && project.Data.Time[i] <= o.End).Any(double.IsNaN));
            if (values.Length == 0) { if (invalid) { axis.Min = -1; axis.Max = 1; } continue; }
            double min = values.Min(), max = values.Max();
            if (invalid) { min = Math.Min(0, min); max = Math.Max(0, max); }
            if (percent) { min = Math.Min(0, min); max = Math.Max(100, max); }
            double padding = max > min ? (max - min) * .08 : Math.Max(1, Math.Abs(min) * .08);
            axis.Min = min - padding; axis.Max = max + padding;
        }
        plot.Axes.SetLimitsX(o.ToAxisTime(o.Start), o.ToAxisTime(o.End));
        foreach (var note in project.Notes.Where(n => n.Panel == panel))
        {
            var line = plot.Add.VerticalLine(o.ToAxisTime(note.Time)); line.Color = ScottPlot.Color.FromHex("#94A3B8"); line.LinePattern = LinePattern.Dotted;
            var label = plot.Add.Text(note.Text, o.ToAxisTime(note.Time), note.Value);
            label.LabelFontSize = o.FontSize; label.LabelFontColor = ScottPlot.Color.FromHex("#172B45");
        }
        CursorReadoutOverlay? cursors = null;
        // Horizontal cursors first, so the A/B statistics table stays on top of their lines.
        if (o.HCursors && panel == Panels(project).FirstOrDefault()) plot.Add.Plottable(new HorizontalCursorOverlay(o, extrema));
        if (o.Cursors) { cursors = new CursorReadoutOverlay(extrema, o); plot.Add.Plottable(cursors); }
        if (o.ShowExtrema) plot.Add.Plottable(new ExtremaOverlay(extrema, o.FontSize, percent, cursors, o.TimeUnit));
    }
    public static BitmapSource Render(StudioProject project)
    {
        Validate(project);
        SignalDisplay.RequireNames(project);
        var o = project.Options; var panels = Panels(project);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, o.ExportWidth, o.ExportHeight));
            double scale = o.ExportWidth / 1400.0;
            int footer = string.IsNullOrWhiteSpace(o.Subtitle) ? 0 : (int)(64 * scale);
            int height = (o.ExportHeight - footer) / panels.Length;
            for (int i = 0; i < panels.Length; i++)
            {
                using var plot = new Plot();
                Configure(plot, project, panels[i]);
                plot.ScaleFactor = (float)scale;
                byte[] bytes = plot.GetImageBytes(o.ExportWidth, height, ImageFormat.Png);
                if (plot.GetPlottables<CursorReadoutOverlay>().Any(c => c.OmittedRows > 0))
                    throw new InvalidDataException("Povećaj visinu izvoza ili rasporedi signale u više panela: tablica kursora mora prikazati sve signale.");
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                dc.DrawImage(bitmap, new Rect(0, i * height, o.ExportWidth, height));
            }
            if (footer > 0)
            {
                var text = new FormattedText(o.Subtitle.Replace('\n', ' '), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 13 * scale, new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)), 1)
                    { MaxTextWidth = o.ExportWidth - 60 * scale, MaxTextHeight = footer, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new System.Windows.Point(30 * scale, o.ExportHeight - footer + 12 * scale));
            }
        }
        var output = new RenderTargetBitmap(o.ExportWidth, o.ExportHeight, 96, 96, PixelFormats.Pbgra32); output.Render(visual); output.Freeze(); return output;
    }
    public static void Export(StudioProject project, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = project.Options.Quality };
        encoder.Frames.Add(BitmapFrame.Create(Render(project)));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = File.Create(temporary)) encoder.Save(stream); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Draws NaN samples (invalid measurement) at 0 in the reserved colour.</summary>
    public static void AddInvalid(Plot plot, double[] x, double[] y, IYAxis? axis, float width)
    {
        if (SignalDisplay.InvalidMask(y) is not double[] mask) return;
        var invalid = plot.Add.Scatter(x, mask);
        invalid.Color = ScottPlot.Color.FromHex(SignalDisplay.InvalidColor);
        invalid.LineWidth = Math.Max(2, width); invalid.MarkerSize = 4; invalid.LegendText = "";
        if (axis != null) invalid.Axes.YAxis = axis;
    }
    public static void AddInvalidLegend(Plot plot) => plot.Legend.ManualItems.Add(new LegendItem
    {
        LabelText = English ? "Recording error (NaN, shown at 0)" : "Greška snimanja (NaN, prikaz 0)", LineColor = ScottPlot.Color.FromHex(SignalDisplay.InvalidColor), LineWidth = 3,
        MarkerShape = MarkerShape.FilledCircle, MarkerFillColor = ScottPlot.Color.FromHex(SignalDisplay.InvalidColor), MarkerSize = 6
    });

    public static void Validate(StudioProject p)
    {
        var o = p.Options;
        if (p.Data.Signals.FirstOrDefault(s => SignalDisplay.IsReserved(s.Color)) is Signal reserved)
            throw new InvalidDataException($"Boja {SignalDisplay.InvalidColor} rezervirana je za grešku snimanja (NaN). Odaberi drugu boju za signal '{reserved.DisplayName}'.");
        if (p.Data.Time.Length < 2) throw new InvalidDataException("Prvo učitaj trace.");
        if (p.Data.Signals.Any(s => s.Values.Length != p.Data.Time.Length)) throw new InvalidDataException("Duljina signala ne odgovara vremenskoj osi.");
        if (p.Data.Time.Any(t => !double.IsFinite(t)) || p.Data.Time.Skip(1).Where((v, i) => v <= p.Data.Time[i]).Any()) throw new InvalidDataException("Vremenska os mora biti konačna i strogo rastuća.");
        if (!double.IsFinite(o.Start) || !double.IsFinite(o.End) || o.Start >= o.End || !double.IsFinite(o.Zero)) throw new InvalidDataException("Početak mora biti manji od kraja; vrijeme mora biti konačan broj.");
        if (o.End < p.Data.Time[0] || o.Start > p.Data.Time[^1]) throw new InvalidDataException("Odabrani interval ne sadrži snimku.");
        if (!Panels(p).Any()) throw new InvalidDataException("Uključi barem jedan signal.");
        if (o.DisplayMode != ChartOptions.UnitsMode && o.DisplayMode != ChartOptions.PercentMode) throw new InvalidDataException("Odaberi način prikaza osi.");
        if (o.TimeUnit is not ("s" or "min" or "h" or "d")) throw new InvalidDataException("Odaberi vremensku jedinicu: s, min, h ili d.");
        if (o.DisplayMode == ChartOptions.PercentMode) foreach (var s in p.Data.Signals.Where(s => s.Visible)) SignalDisplay.Values(s, o);
        if (!float.IsFinite(o.FontSize) || o.FontSize < 8 || o.FontSize > 32) throw new InvalidDataException("Font mora biti između 8 i 32.");
        foreach (var pair in new[] { (o.YMin, o.YMax), (o.RightMin, o.RightMax) })
        {
            if (pair.Item1.HasValue && !double.IsFinite(pair.Item1.Value) || pair.Item2.HasValue && !double.IsFinite(pair.Item2.Value) || pair.Item1.HasValue && pair.Item2.HasValue && pair.Item1 >= pair.Item2) throw new InvalidDataException("Granice Y-osi moraju biti konačne i min < max.");
        }
        if (!double.IsFinite(o.CursorA) || !double.IsFinite(o.CursorB) || !double.IsFinite(o.HCursorA) || !double.IsFinite(o.HCursorB)) throw new InvalidDataException("Kursori moraju imati konačne vrijednosti.");
        foreach (var s in p.Data.Signals)
        {
            foreach (var label in new[] { s.MinimumLabel, s.MaximumLabel, s.ConstantLabel })
                if (label == null || label.X.HasValue != label.Y.HasValue || label.X.HasValue && (!double.IsFinite(label.X.Value) || !double.IsFinite(label.Y!.Value) || label.X < 0 || label.X > 1 || label.Y < 0 || label.Y > 1))
                    throw new InvalidDataException("Neispravna spremljena pozicija min/max oblačića.");
            if (s.Panel < 1 || s.Panel > 4 || !double.IsFinite(s.Width) || s.Width < 0.5 || s.Width > 10 || !double.IsFinite(s.Factor) || !double.IsFinite(s.Offset)) throw new InvalidDataException($"Neispravne postavke signala {s.Name}. Panel: 1–4, debljina: 0,5–10.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(s.Color, "^#[0-9a-fA-F]{6}$")) throw new InvalidDataException($"Boja signala {s.Name} mora biti #RRGGBB.");
            if (s.Values.Any(double.IsInfinity) || s.Scaled().Any(double.IsInfinity)) throw new InvalidDataException($"Prevelike vrijednosti signala {s.Name}.");
        }
        if (p.Notes.Any(n => !double.IsFinite(n.Time) || !double.IsFinite(n.Value) || n.Panel < 1 || n.Panel > 4)) throw new InvalidDataException("Provjeri vrijeme, vrijednost i panel bilješke.");
        if (o.ExportWidth < 800 || o.ExportWidth > 8000 || o.ExportHeight < 600 || o.ExportHeight > 8000 || (long)o.ExportWidth * o.ExportHeight > 32000000 || o.Quality < 1 || o.Quality > 100) throw new InvalidDataException("Izvoz: širina 800–8000 px, visina 600–8000 px, najviše 32 MP; kvaliteta 1–100.");
        if (o.ExportHeight / Panels(p).Length < 250) throw new InvalidDataException("Povećaj visinu izvoza: svaki panel treba barem 250 px.");
        double scale = o.ExportWidth / 1400.0;
        double footer = string.IsNullOrWhiteSpace(o.Subtitle) ? 0 : 64 * scale;
        if ((o.ExportHeight - footer) / Panels(p).Length / scale < 180) throw new InvalidDataException("Povećaj visinu ili smanji širinu izvoza kako bi svi paneli i oznake bili čitljivi.");
    }
}
