using ScottPlot;
using SkiaSharp;

namespace TiaTraceStudio;

public record ExtremaSeries(string Name, string Unit, string Color, double[] X, double[] Y, double[] Physical, IYAxis YAxis, Signal? Signal = null);
public record ExtremePoint(string Name, string Detail, SKColor Color, float X, float Y, ExtremaLabelOptions? Options = null);
public record ExtremeLabel(ExtremePoint Point, SKRect Bounds);

/// <summary>Measures labels in screen space on every render, so zoom and export share collision rules.</summary>
public sealed class ExtremaOverlay(List<ExtremaSeries> series, float fontSize, bool percent, CursorReadoutOverlay? cursors = null, string timeUnit = "s") : IPlottable
{
    public bool IsVisible { get; set; } = true;
    public IAxes Axes { get; set; } = new Axes();
    public IEnumerable<LegendItem> LegendItems => [];
    public AxisLimits GetAxisLimits() => new(double.NaN, double.NaN, double.NaN, double.NaN);
    public List<ExtremeLabel> LastPlacements { get; private set; } = [];
    public int OmittedCount { get; private set; }
    public int CurveOverlapCount { get; private set; }
    public SKRect LastArea { get; private set; }
    PlotOccupancy? cachedOccupancy;
    (SKRect Area, double XMin, double XMax)? cachedView;
    double[] cachedYLimits = [];

    public ExtremeLabel? HitTest(float x, float y) => LastPlacements.LastOrDefault(p => p.Bounds.Contains(x, y));
    public void MoveLabel(ExtremeLabel label, float left, float top)
    {
        if (label.Point.Options == null) return;
        double width = LastArea.Width - label.Bounds.Width, height = LastArea.Height - 26 - label.Bounds.Height;
        label.Point.Options.X = width > 0 ? Math.Clamp((left - LastArea.Left) / width, 0, 1) : 0;
        label.Point.Options.Y = height > 0 ? Math.Clamp((top - LastArea.Top - 26) / height, 0, 1) : 0;
    }

    public void Render(RenderPack rp)
    {
        var points = new List<ExtremePoint>();
        var area = new SKRect(rp.DataRect.Left + 5, rp.DataRect.Top + 5, rp.DataRect.Right - 5, rp.DataRect.Bottom - 5);
        LastArea = area;
        foreach (var s in series)
        {
            int min = -1, max = -1;
            for (int i = 0; i < s.X.Length; i++)
            {
                if (s.X[i] < Axes.XAxis.Min || s.X[i] > Axes.XAxis.Max || !double.IsFinite(s.Y[i])) continue;
                if (min < 0 || s.Y[i] < s.Y[min]) min = i;
                if (max < 0 || s.Y[i] > s.Y[max]) max = i;
            }
            if (min < 0) continue;
            Add(min, s.Y[min] == s.Y[max] ? "min = max" : "min");
            if (s.Y[min] != s.Y[max]) Add(max, "max");
            void Add(int i, string kind)
            {
                var options = kind == "min" ? s.Signal?.MinimumLabel : kind == "max" ? s.Signal?.MaximumLabel : s.Signal?.ConstantLabel;
                if (options?.Hidden == true) return;
                float x = Axes.XAxis.GetPixel(s.X[i], rp.DataRect), y = s.YAxis.GetPixel(s.Y[i], rp.DataRect);
                if (!float.IsFinite(x) || !float.IsFinite(y) || y < rp.DataRect.Top || y > rp.DataRect.Bottom) return;
                string unit = percent ? "%" : s.Unit;
                string detail = $"{kind} {s.Y[i]:G5} {unit} · {s.X[i]:G5} {timeUnit}";
                if (percent && !string.IsNullOrWhiteSpace(s.Unit)) detail += $" ({s.Physical[i]:G5} {s.Unit})";
                points.Add(new(s.Name, detail, SKColor.Parse(s.Color), x, y, options));
            }
        }
        using var textPaint = new SKPaint { IsAntialias = true, TextSize = Math.Clamp(fontSize - 1, 10, 20), Typeface = SKTypeface.FromFamilyName("Segoe UI") };
        var view = (area, Axes.XAxis.Min, Axes.XAxis.Max);
        var yLimits = series.SelectMany(s => new[] { s.YAxis.Min, s.YAxis.Max, s.Signal?.Width ?? 2 }).ToArray();
        if (cachedOccupancy == null || cachedView != view || !cachedYLimits.SequenceEqual(yLimits))
        {
            cachedOccupancy = new PlotOccupancy(area); cachedView = view; cachedYLimits = yLimits;
            foreach (var s in series)
                for (int i = 1; i < s.X.Length; i++)
                {
                    if (s.X[i] < Axes.XAxis.Min || s.X[i - 1] > Axes.XAxis.Max || !double.IsFinite(s.Y[i - 1]) || !double.IsFinite(s.Y[i])) continue;
                    cachedOccupancy.AddSegment(Axes.XAxis.GetPixel(s.X[i - 1], rp.DataRect), s.YAxis.GetPixel(s.Y[i - 1], rp.DataRect),
                        Axes.XAxis.GetPixel(s.X[i], rp.DataRect), s.YAxis.GetPixel(s.Y[i], rp.DataRect), (float)(s.Signal?.Width ?? 2) / 2 + 3);
                }
        }
        var occupancy = cachedOccupancy;
        LastPlacements = Place(points, area, textPaint, cursors?.ReservedAreas, occupancy);
        CurveOverlapCount = LastPlacements.Count(p => p.Point.Options?.IsManual != true && occupancy.Count(p.Bounds) > 0);
        OmittedCount = points.Count - LastPlacements.Count;
        using var paint = new SKPaint { IsAntialias = true };
        // Draw connectors first, then opaque label cards; a connector cannot cross a label visually.
        foreach (var label in LastPlacements)
        {
            paint.Color = label.Point.Color.WithAlpha(170); paint.StrokeWidth = 1; paint.Style = SKPaintStyle.Stroke;
            float endX = Math.Clamp(label.Point.X, label.Bounds.Left, label.Bounds.Right);
            float endY = Math.Clamp(label.Point.Y, label.Bounds.Top, label.Bounds.Bottom);
            rp.Canvas.DrawLine(label.Point.X, label.Point.Y, endX, endY, paint);
        }
        foreach (var p in points)
        {
            paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White; rp.Canvas.DrawCircle(p.X, p.Y, 4, paint);
            paint.Style = SKPaintStyle.Stroke; paint.Color = p.Color; paint.StrokeWidth = 2; rp.Canvas.DrawCircle(p.X, p.Y, 4, paint);
        }
        foreach (var label in LastPlacements)
        {
            paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White; rp.Canvas.DrawRoundRect(label.Bounds, 4, 4, paint);
            paint.Style = SKPaintStyle.Stroke; paint.Color = label.Point.Color.WithAlpha(190); paint.StrokeWidth = 1; rp.Canvas.DrawRoundRect(label.Bounds, 4, 4, paint);
            textPaint.Color = label.Point.Color;
            rp.Canvas.DrawText(Truncate(label.Point.Name, textPaint, label.Bounds.Width - 16), label.Bounds.Left + 8, label.Bounds.Top + textPaint.TextSize + 4, textPaint);
            textPaint.Color = SKColor.Parse("#243449");
            rp.Canvas.DrawText(Truncate(label.Point.Detail, textPaint, label.Bounds.Width - 16), label.Bounds.Left + 8, label.Bounds.Top + 2 * textPaint.TextSize + 8, textPaint);
        }
        if (OmittedCount > 0 || CurveOverlapCount > 0)
        {
            paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
            rp.Canvas.DrawRect(area.Left, area.Top, area.Width, 22, paint);
            textPaint.Color = SKColors.DarkRed;
            rp.Canvas.DrawText(OmittedCount > 0 ? $"{OmittedCount} oznaka ne stane — povećaj graf ili smanji broj signala." : "Nema slobodnog prostora za sve oznake — povećaj graf ili pomakni oblačiće.", area.Left + 4, area.Top + 16, textPaint);
        }
    }
    public static List<ExtremeLabel> Place(List<ExtremePoint> points, SKRect area, SKPaint paint, IReadOnlyList<SKRect>? reserved = null, PlotOccupancy? occupancy = null)
    {
        var result = new List<ExtremeLabel>();
        float height = paint.TextSize * 2 + 16;
        // Reserve user positions first; automatic labels continue avoiding occupied cards.
        foreach (var p in points.OrderByDescending(p => p.Options?.IsManual == true))
        {
            float width = Math.Min(Math.Min(290, area.Width - 4), Math.Max(110, Math.Max(paint.MeasureText(p.Name), paint.MeasureText(p.Detail)) + 16));
            if (width <= 0 || height > area.Height - 26) continue;
            if (p.Options?.IsManual == true)
            {
                float left = area.Left + (float)p.Options.X!.Value * (area.Width - width);
                float top = area.Top + 26 + (float)p.Options.Y!.Value * (area.Height - 26 - height);
                result.Add(new(p, new SKRect(left, top, left + width, top + height)));
                continue;
            }
            SKRect? best = null; double bestScore = double.PositiveInfinity;
            // Leave a small top strip free for a capacity warning on very small views.
            void Consider(float left, float top)
            {
                left = Math.Clamp(left, area.Left, area.Right - width); top = Math.Clamp(top, area.Top + 26, area.Bottom - height);
                var candidate = new SKRect(left, top, left + width, top + height);
                var padded = candidate; padded.Inflate(5, 5);
                if (result.Any(r => padded.IntersectsWith(r.Bounds))) return;
                if (reserved?.Any(r => padded.IntersectsWith(r)) == true) return;
                if (points.Any(q => padded.Contains(q.X, q.Y))) return;
                double dx = p.X - Math.Clamp(p.X, candidate.Left, candidate.Right), dy = p.Y - Math.Clamp(p.Y, candidate.Top, candidate.Bottom);
                // A single occupied cell costs more than any connector distance in this plot.
                double score = (occupancy?.Count(padded) ?? 0) * ((double)area.Width * area.Width + (double)area.Height * area.Height + 1) + dx * dx + dy * dy;
                if (score < bestScore) { best = candidate; bestScore = score; }
            }
            foreach (float dx in new[] { 12f, -width - 12, -width / 2 })
                foreach (float dy in new[] { 12f, -height - 12 }) Consider(p.X + dx, p.Y + dy);
            if (best == null || (occupancy?.Count(best.Value) ?? 0) > 0)
                for (float y = area.Top + 26; y <= area.Bottom - height; y += 12)
                    for (float x = area.Left; x <= area.Right - width; x += 12) Consider(x, y);
            if (best.HasValue) result.Add(new(p, best.Value));
        }
        return result;
    }
    static string Truncate(string text, SKPaint paint, float width)
    {
        if (paint.MeasureText(text) <= width) return text;
        while (text.Length > 0 && paint.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }
}
