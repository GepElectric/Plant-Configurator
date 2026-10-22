using System.Windows.Input;
using ScottPlot;
using ScottPlot.WPF;
using SkiaSharp;

namespace TiaTraceStudio;

/// <summary>
/// Horizontal cursors A and B on the left Y axis of the first panel. Like the vertical cursor, a box lists every
/// signal of the panel with its value at A and B and ΔY = B − A in its own unit (each Y axis, also when normalized).
/// </summary>
public sealed class HorizontalCursorOverlay(ChartOptions options, IReadOnlyList<ExtremaSeries>? series = null) : IPlottable
{
    public bool IsVisible { get; set; } = true;
    public IAxes Axes { get; set; } = new ScottPlot.Axes();
    public IEnumerable<LegendItem> LegendItems => [];
    public AxisLimits GetAxisLimits() => new(double.NaN, double.NaN, double.NaN, double.NaN);
    public float PixelA { get; private set; } = float.NaN;
    public float PixelB { get; private set; } = float.NaN;
    public string AxisUnit { get; private set; } = "";
    public List<string> LastRows { get; private set; } = [];

    public string? HitTest(float y)
    {
        float a = Math.Abs(y - PixelA), b = Math.Abs(y - PixelB);
        if (!(Math.Min(a, b) <= 7)) return null;
        return a <= b ? "A" : "B";
    }

    /// <summary>One row per signal: the cursor heights read on its own Y axis, converted back to its physical unit.</summary>
    public List<(SKColor? Color, string Text)> Rows(PixelRect area)
    {
        string unitOf(string u) => string.IsNullOrWhiteSpace(u) ? "" : " " + u.Trim();
        var rows = new List<(SKColor? Color, string Text)>();
        if (series == null || series.Count == 0)
        {
            string unit = AxisUnit.Length > 0 ? " " + AxisUnit : "";
            rows.Add((null, $"A = {options.HCursorA:G6}{unit}   B = {options.HCursorB:G6}{unit}   ΔY = {options.HCursorB - options.HCursorA:G6}{unit}"));
            return rows;
        }
        rows.Add((null, "Horizontalni kursori · A / B / ΔY = B − A"));
        foreach (var s in series)
        {
            double a = Physical(s, s.YAxis.GetCoordinate(PixelA, area)), b = Physical(s, s.YAxis.GetCoordinate(PixelB, area));
            string u = unitOf(s.Unit);
            rows.Add((SKColor.TryParse(s.Color, out var color) ? color : SKColors.Gray, $"{s.Name}:  A = {a:G5}{u}   B = {b:G5}{u}   ΔY = {b - a:G5}{u}"));
        }
        return rows;
    }

    /// <summary>Displayed value → physical value of the series (normalization and scaling are linear).</summary>
    static double Physical(ExtremaSeries s, double shown)
    {
        int first = -1;
        for (int i = 0; i < s.Y.Length; i++)
        {
            if (!double.IsFinite(s.Y[i]) || !double.IsFinite(s.Physical[i])) continue;
            if (first < 0) { first = i; continue; }
            if (s.Physical[i] != s.Physical[first])
            {
                double slope = (s.Y[i] - s.Y[first]) / (s.Physical[i] - s.Physical[first]);
                return slope == 0 ? shown : s.Physical[first] + (shown - s.Y[first]) / slope;
            }
        }
        return first >= 0 ? shown - s.Y[first] + s.Physical[first] : shown;
    }

    public void Render(RenderPack rp)
    {
        var area = rp.DataRect;
        PixelA = Axes.GetPixelY(options.HCursorA); PixelB = Axes.GetPixelY(options.HCursorB);
        // "Normalizirano [%]" → "%", "bar" → "bar".
        string axisLabel = Axes.YAxis.Label.Text ?? "";
        var bracket = System.Text.RegularExpressions.Regex.Match(axisLabel, @"\[([^\]]+)\]");
        AxisUnit = bracket.Success ? bracket.Groups[1].Value : axisLabel;
        var aColor = SKColor.Parse("#7C3AED"); var bColor = SKColor.Parse("#DB2777");
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
        using var fill = new SKPaint { IsAntialias = true };
        using var text = new SKPaint { IsAntialias = true, TextSize = Math.Clamp(options.FontSize - 1, 10, 20), Typeface = SKTypeface.FromFamilyName("Segoe UI") };
        float top = Math.Clamp(Math.Min(PixelA, PixelB), area.Top, area.Bottom), bottom = Math.Clamp(Math.Max(PixelA, PixelB), area.Top, area.Bottom);
        fill.Color = SKColor.Parse("#EDE9FE").WithAlpha(40); rp.Canvas.DrawRect(new SKRect(area.Left, top, area.Right, bottom), fill);
        foreach (var (y, color, label) in new[] { (PixelA, aColor, "A"), (PixelB, bColor, "B") })
        {
            if (y < area.Top || y > area.Bottom) continue;
            line.Color = color; rp.Canvas.DrawLine(area.Left, y, area.Right, y, line);
            var handle = new SKRect(area.Left + 2, y - 11, area.Left + 26, y + 11);
            fill.Color = color; rp.Canvas.DrawRoundRect(handle, 3, 3, fill);
            text.Color = SKColors.White; rp.Canvas.DrawText(label, handle.Left + 7, handle.Top + text.TextSize + 2, text);
        }
        var rows = Rows(area); LastRows = rows.Select(r => r.Text).ToList();
        float rowHeight = text.TextSize + 7, width = rows.Max(r => text.MeasureText(r.Text)) + 34;
        var card = new SKRect(area.Right - width - 8, area.Top + 8, area.Right - 8, area.Top + 8 + rows.Count * rowHeight + 10);
        fill.Color = SKColors.White.WithAlpha(240); rp.Canvas.DrawRoundRect(card, 5, 5, fill);
        line.PathEffect = null; line.Color = aColor; line.StrokeWidth = 1; rp.Canvas.DrawRoundRect(card, 5, 5, line);
        float rowTop = card.Top + 5;
        foreach (var (color, row) in rows)
        {
            if (color is SKColor dot) { fill.Color = dot; rp.Canvas.DrawCircle(card.Left + 12, rowTop + rowHeight / 2, 4, fill); }
            text.Color = color == null ? aColor : SKColor.Parse("#172B45");
            rp.Canvas.DrawText(row, card.Left + 22, rowTop + rowHeight - 6, text);
            rowTop += rowHeight;
        }
    }
}

public sealed class HorizontalCursorInteraction
{
    readonly WpfPlot plot;
    readonly ChartOptions options;
    readonly Action preview, completed;
    string? dragging; bool moved, inputWasEnabled;

    public HorizontalCursorInteraction(WpfPlot plot, ChartOptions options, Action preview, Action completed)
    {
        this.plot = plot; this.options = options; this.preview = preview; this.completed = completed;
        plot.PreviewMouseDown += (_, e) =>
        {
            if (e.Handled || e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None || Overlay is not HorizontalCursorOverlay overlay) return;
            dragging = overlay.HitTest(Y(e)); if (dragging == null) return;
            moved = false; inputWasEnabled = plot.UserInputProcessor.IsEnabled;
            plot.UserInputProcessor.Disable(); plot.Cursor = Cursors.SizeNS; plot.CaptureMouse(); e.Handled = true;
        };
        plot.PreviewMouseMove += (_, e) =>
        {
            if (e.Handled || Overlay is not HorizontalCursorOverlay overlay) return;
            float y = Y(e);
            if (dragging != null)
            {
                double value = overlay.Axes.GetCoordinateY(y);
                if (dragging == "A") options.HCursorA = value; else options.HCursorB = value;
                moved = true; preview(); e.Handled = true;
            }
            else if (overlay.HitTest(y) != null) plot.Cursor = Cursors.SizeNS;
        };
        plot.PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && dragging != null) { e.Handled = true; End(); } };
        plot.LostMouseCapture += (_, _) => End();
    }

    HorizontalCursorOverlay? Overlay => plot.Plot.GetPlottables<HorizontalCursorOverlay>().FirstOrDefault();
    float Y(MouseEventArgs e) { var p = plot.GetPlotPixelPosition(e); return p.Y / (float)plot.Plot.ScaleFactor; }

    void End()
    {
        if (dragging == null) return;
        dragging = null; bool commit = moved; moved = false;
        if (inputWasEnabled) plot.UserInputProcessor.Enable();
        plot.Cursor = Cursors.Arrow; if (plot.IsMouseCaptured) plot.ReleaseMouseCapture();
        if (commit) completed();
    }
}
