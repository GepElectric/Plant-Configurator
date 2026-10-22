using System.Windows.Input;
using ScottPlot;
using ScottPlot.WPF;
using SkiaSharp;

namespace TiaTraceStudio;

/// <summary>
/// Draggable vertical cursor on the screen (not in exports) with the values of all visible signals of the panel at
/// its position; "Primijeni nulu grafa" moves time zero to it.
/// </summary>
public sealed class ZeroCursorOverlay(StudioProject project, int panel) : IPlottable
{
    public bool IsVisible { get; set; } = true;
    public IAxes Axes { get; set; } = new ScottPlot.Axes();
    public IEnumerable<LegendItem> LegendItems => [];
    public AxisLimits GetAxisLimits() => new(double.NaN, double.NaN, double.NaN, double.NaN);
    float lastX = float.NaN;
    ChartOptions options => project.Options;

    public bool HitTest(float x) => float.IsFinite(lastX) && Math.Abs(x - lastX) <= 7;

    public void Render(RenderPack rp)
    {
        float x = Axes.GetPixelX(options.ToAxisTime(options.ZeroCursor));
        lastX = x;
        if (x < rp.DataRect.Left - 1 || x > rp.DataRect.Right + 1) return;
        using var line = new SKPaint { Color = new SKColor(0xDC, 0x26, 0x26), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash([8, 5], 0) };
        rp.Canvas.DrawLine(x, rp.DataRect.Top, x, rp.DataRect.Bottom, line);
        string text = $"{options.ToAxisTime(options.ZeroCursor):0.###} {options.TimeUnit}";
        using var paint = new SKPaint { TextSize = 13, IsAntialias = true, Color = SKColors.White, Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) };
        float width = paint.MeasureText(text) + 12;
        float left = Math.Clamp(x - width / 2, rp.DataRect.Left, rp.DataRect.Right - width);
        using var fill = new SKPaint { Color = new SKColor(0xDC, 0x26, 0x26), IsAntialias = true };
        rp.Canvas.DrawRoundRect(new SKRect(left, rp.DataRect.Top + 4, left + width, rp.DataRect.Top + 24), 4, 4, fill);
        rp.Canvas.DrawText(text, left + 6, rp.DataRect.Top + 19, paint);
        DrawValues(rp, x);
    }

    /// <summary>Values of the panel's visible signals at the cursor (physical units, linear interpolation, NaN = invalid).</summary>
    void DrawValues(RenderPack rp, float x)
    {
        var rows = new List<(SKColor Color, string Text)>();
        var time = project.Data.Time;
        foreach (var (signal, index) in project.Data.Signals.Select((s, i) => (s, i)).Where(p => p.s.Visible && p.s.Panel == panel))
        {
            var value = AnalysisService.ValueAt(time, signal.Scaled(), options.ZeroCursor);
            string number = value is double v ? v.ToString("G6", System.Globalization.CultureInfo.CurrentCulture) + (string.IsNullOrWhiteSpace(signal.Unit) ? "" : " " + signal.Unit.Trim()) : "NaN (greška)";
            rows.Add((SKColor.TryParse(signal.Color, out var color) ? color : SKColors.Gray, $"{SignalDisplay.PlotName(signal, index)}: {number}"));
        }
        if (rows.Count == 0) return;
        using var text = new SKPaint { TextSize = 12.5f, IsAntialias = true, Color = new SKColor(0x17, 0x2B, 0x45), Typeface = SKTypeface.FromFamilyName("Segoe UI") };
        float lineHeight = 18, width = rows.Max(r => text.MeasureText(r.Text)) + 30, height = rows.Count * lineHeight + 10;
        // Next to the cursor, on the side with more room, below the time label.
        float left = x + 10 + width <= rp.DataRect.Right ? x + 10 : Math.Max(rp.DataRect.Left + 2, x - 10 - width);
        float top = rp.DataRect.Top + 30;
        using var box = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true };
        using var border = new SKPaint { Color = new SKColor(0xDC, 0x26, 0x26), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        var rect = new SKRect(left, top, left + width, top + height);
        rp.Canvas.DrawRoundRect(rect, 5, 5, box); rp.Canvas.DrawRoundRect(rect, 5, 5, border);
        using var dot = new SKPaint { IsAntialias = true };
        float y = top + 5;
        foreach (var (color, line) in rows)
        {
            dot.Color = color; rp.Canvas.DrawCircle(left + 12, y + lineHeight / 2, 4, dot);
            rp.Canvas.DrawText(line, left + 22, y + lineHeight - 5, text);
            y += lineHeight;
        }
    }
}

public sealed class ZeroCursorInteraction
{
    readonly WpfPlot plot;
    readonly ChartOptions options;
    readonly double first, last;
    readonly Action preview, completed;
    bool dragging, moved, inputWasEnabled;

    public ZeroCursorInteraction(WpfPlot plot, ChartOptions options, double first, double last, Action preview, Action completed)
    {
        this.plot = plot; this.options = options; this.first = first; this.last = last; this.preview = preview; this.completed = completed;
        plot.PreviewMouseDown += (_, e) =>
        {
            if (e.Handled || e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None || Overlay is not ZeroCursorOverlay overlay) return;
            if (!overlay.HitTest(X(e))) return;
            dragging = true; moved = false; inputWasEnabled = plot.UserInputProcessor.IsEnabled;
            plot.UserInputProcessor.Disable(); plot.Cursor = Cursors.SizeWE; plot.CaptureMouse(); e.Handled = true;
        };
        plot.PreviewMouseMove += (_, e) =>
        {
            if (e.Handled || Overlay is not ZeroCursorOverlay overlay) return;
            float x = X(e);
            if (dragging)
            {
                double time = Math.Clamp(options.FromAxisTime(overlay.Axes.GetCoordinateX(x)), first, last);
                if (time != options.ZeroCursor) { options.ZeroCursor = time; moved = true; preview(); }
                e.Handled = true;
            }
            else if (overlay.HitTest(x)) plot.Cursor = Cursors.SizeWE;
        };
        plot.PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && dragging) { e.Handled = true; End(); } };
        plot.LostMouseCapture += (_, _) => End();
    }

    ZeroCursorOverlay? Overlay => plot.Plot.GetPlottables<ZeroCursorOverlay>().FirstOrDefault();
    float X(MouseEventArgs e) { var p = plot.GetPlotPixelPosition(e); return p.X / (float)plot.Plot.ScaleFactor; }

    void End()
    {
        if (!dragging) return;
        dragging = false; bool commit = moved; moved = false;
        if (inputWasEnabled) plot.UserInputProcessor.Enable();
        plot.Cursor = Cursors.Arrow; if (plot.IsMouseCaptured) plot.ReleaseMouseCapture();
        if (commit) completed();
    }
}
