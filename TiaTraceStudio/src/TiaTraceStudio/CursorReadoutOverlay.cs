using ScottPlot;
using SkiaSharp;

namespace TiaTraceStudio;

/// <summary>One signal between cursors A and B, in physical units: values, difference, rate of change and range.</summary>
public record CursorValueRow(string Name, string Unit, string Color, double? A, double? B, double? Min = null, double? Max = null, double ElapsedDisplay = 0, double? Mean = null)
{
    public double? Delta => B - A;
    /// <summary>Δy / Δt per unit of the time axis (signed: B − A over tB − tA).</summary>
    public double? Rate => ElapsedDisplay != 0 ? Delta / ElapsedDisplay : null;
}

/// <summary>Two live cursors and their readout, shared by the interactive and export renderers.</summary>
public sealed class CursorReadoutOverlay(List<ExtremaSeries> series, ChartOptions options) : IPlottable
{
    public bool IsVisible { get; set; } = true;
    public IAxes Axes { get; set; } = new Axes();
    public IEnumerable<LegendItem> LegendItems => [];
    public AxisLimits GetAxisLimits() => new(double.NaN, double.NaN, double.NaN, double.NaN);
    public List<CursorValueRow> LastRows { get; private set; } = [];
    public List<SKRect> ReservedAreas { get; private set; } = [];
    public double Elapsed => Math.Abs(options.CursorB - options.CursorA);
    public SKRect LastDataArea { get; private set; }
    public float PixelA { get; private set; }
    public float PixelB { get; private set; }
    public SKRect HandleA { get; private set; }
    public SKRect HandleB { get; private set; }
    public int OmittedRows { get; private set; }

    /// <summary>Physical values (after factor and offset) also in the normalized view, so magnitudes can be analysed.</summary>
    public static List<CursorValueRow> ReadValues(IEnumerable<ExtremaSeries> series, ChartOptions options)
    {
        double a = options.ToAxisTime(options.CursorA), b = options.ToAxisTime(options.CursorB);
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        return series.Select(s =>
        {
            double? atA = AnalysisService.ValueAt(s.X, s.Physical, a), atB = AnalysisService.ValueAt(s.X, s.Physical, b);
            // Samples between the cursors plus the interpolated values at the cursors themselves.
            var inside = s.Physical.Where((v, i) => s.X[i] >= lo && s.X[i] <= hi && double.IsFinite(v)).Concat(new[] { atA, atB }.OfType<double>()).ToArray();
            return new CursorValueRow(s.Name, s.Unit, s.Color, atA, atB,
                inside.Length > 0 ? inside.Min() : null, inside.Length > 0 ? inside.Max() : null, b - a, inside.Length > 0 ? inside.Average() : null);
        }).ToList();
    }

    public string? HitTest(float x, float y)
    {
        if (!options.Cursors) return null;
        if (HandleA.Contains(x, y)) return "A";
        if (HandleB.Contains(x, y)) return "B";
        if (!LastDataArea.Contains(x, y) || ReservedAreas.Any(r => r.Contains(x, y))) return null;
        float a = Math.Abs(x - PixelA), b = Math.Abs(x - PixelB);
        if (Math.Min(a, b) > 7) return null;
        return a <= b ? "A" : "B";
    }
    public void Render(RenderPack rp)
    {
        if (!options.Cursors) { ReservedAreas = []; LastRows = []; return; }
        var area = new SKRect(rp.DataRect.Left, rp.DataRect.Top, rp.DataRect.Right, rp.DataRect.Bottom);
        LastDataArea = area;
        PixelA = Axes.XAxis.GetPixel(options.ToAxisTime(options.CursorA), rp.DataRect);
        PixelB = Axes.XAxis.GetPixel(options.ToAxisTime(options.CursorB), rp.DataRect);
        LastRows = ReadValues(series, options);
        OmittedRows = LastRows.Count;
        ReservedAreas = [];
        HandleA = HandleB = SKRect.Empty;
        if (area.Width < 200 || area.Height < 80) return;
        using var paint = new SKPaint { IsAntialias = true };
        using var text = new SKPaint { IsAntialias = true, TextSize = Math.Clamp(options.FontSize - 1, 10, 20), Typeface = SKTypeface.FromFamilyName("Segoe UI") };
        var aColor = SKColor.Parse("#7C3AED"); var bColor = SKColor.Parse("#DB2777");
        float rowHeight = text.TextSize + 9;

        paint.Color = SKColor.Parse("#EDE9FE").WithAlpha(45);
        paint.Style = SKPaintStyle.Fill;
        rp.Canvas.DrawRect(new SKRect(Math.Clamp(Math.Min(PixelA, PixelB), area.Left, area.Right), area.Top, Math.Clamp(Math.Max(PixelA, PixelB), area.Left, area.Right), area.Bottom), paint);
        DrawCursor(PixelA, aColor); DrawCursor(PixelB, bColor);

        // A dimension line and centered duration label between the two cursor positions.
        string duration = $"Δt = {Elapsed / options.TimeScale:G6} {options.TimeUnit}" + (options.TimeUnit == "s" && Elapsed > 0 ? $"  ·  1/Δt = {1 / Elapsed:G4} Hz" : "");
        float durationWidth = text.MeasureText(duration) + 18, durationHeight = rowHeight + 4;
        float center = Math.Clamp((PixelA + PixelB) / 2, area.Left + durationWidth / 2 + 4, area.Right - durationWidth / 2 - 4);
        float dimensionY = area.Top + 17;
        paint.Color = SKColor.Parse("#64748B"); paint.StrokeWidth = 1; paint.Style = SKPaintStyle.Stroke;
        rp.Canvas.DrawLine(Math.Clamp(PixelA, area.Left, area.Right), dimensionY, Math.Clamp(PixelB, area.Left, area.Right), dimensionY, paint);
        var durationBox = new SKRect(center - durationWidth / 2, area.Top + 4, center + durationWidth / 2, area.Top + 4 + durationHeight);
        DrawCard(durationBox); text.Color = SKColor.Parse("#172B45");
        rp.Canvas.DrawText(duration, durationBox.Left + 9, durationBox.Top + text.TextSize + 5, text);
        ReservedAreas.Add(durationBox);

        string[] headings = ["Signal", $"A · {options.ToAxisTime(options.CursorA):G6} {options.TimeUnit}", $"B · {options.ToAxisTime(options.CursorB):G6} {options.TimeUnit}", "Δy = B − A", $"Δy/Δt [/{options.TimeUnit}]", "Min A–B", "Max A–B", "Srednja A–B"];
        const int valueColumns = 7;
        float nameWidth = Math.Clamp(LastRows.Select(r => text.MeasureText(Label(r))).DefaultIfEmpty(110).Max() + 18, 115, 230);
        float valueWidth = Math.Max(92, headings.Skip(1).Max(h => text.MeasureText(h)) + 16);
        float width = Math.Min(area.Width - 12, nameWidth + valueWidth * valueColumns);
        if (width < 200 || area.Height < rowHeight * 4) return;
        nameWidth = Math.Min(nameWidth, width * .3f); valueWidth = (width - nameWidth) / valueColumns;
        float top = durationBox.Bottom + 8;
        int capacity = Math.Max(0, (int)((area.Bottom - top - 38) / rowHeight) - 1);
        int shown = Math.Min(LastRows.Count, capacity);
        OmittedRows = LastRows.Count - shown;
        var table = new SKRect(area.Right - width - 6, top, area.Right - 6, top + rowHeight * (shown + 1) + 8 + (OmittedRows > 0 ? rowHeight : 0));
        DrawCard(table); ReservedAreas.Add(table);
        for (int column = 0; column <= valueColumns; column++)
        {
            text.Color = column == 1 ? aColor : column == 2 ? bColor : SKColor.Parse("#172B45");
            float left = table.Left + (column == 0 ? 0 : nameWidth + (column - 1) * valueWidth);
            rp.Canvas.DrawText(Fit(headings[column], column == 0 ? nameWidth - 12 : valueWidth - 12), left + 6, table.Top + text.TextSize + 5, text);
        }
        for (int i = 0; i < shown; i++)
        {
            var row = LastRows[i]; float baseline = table.Top + rowHeight * (i + 1) + text.TextSize + 5;
            text.Color = SKColor.Parse(row.Color); rp.Canvas.DrawText(Fit(Label(row), nameWidth - 12), table.Left + 6, baseline, text);
            text.Color = SKColor.Parse("#243449");
            foreach (var (value, column) in new[] { (row.A, 0), (row.B, 1), (row.Delta, 2), (row.Rate, 3), (row.Min, 4), (row.Max, 5), (row.Mean, 6) })
                rp.Canvas.DrawText(Fit(value?.ToString("G6") ?? "—", valueWidth - 12), table.Left + nameWidth + column * valueWidth + 6, baseline, text);
        }
        if (OmittedRows > 0)
        {
            text.Color = SKColors.DarkRed;
            rp.Canvas.DrawText(Fit($"Još {OmittedRows} signala — povećaj visinu grafa", table.Width - 12), table.Left + 6, table.Bottom - 6, text);
        }
        foreach (var s in series)
            foreach (var (t, color) in new[] { (options.ToAxisTime(options.CursorA), aColor), (options.ToAxisTime(options.CursorB), bColor) })
            {
                double? value = AnalysisService.ValueAt(s.X, s.Y, t); if (!value.HasValue) continue;
                float x = Axes.XAxis.GetPixel(t, rp.DataRect), y = s.YAxis.GetPixel(value.Value, rp.DataRect);
                if (ReservedAreas.Any(r => r.Contains(x, y))) continue;
                paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White; rp.Canvas.DrawCircle(x, y, 3.5f, paint);
                paint.Style = SKPaintStyle.Stroke; paint.Color = color; paint.StrokeWidth = 1.5f; rp.Canvas.DrawCircle(x, y, 3.5f, paint);
            }
        // Separate handles remain selectable even when A and B are coincident.
        HandleA = Handle(PixelA, "A", aColor, -26); HandleB = Handle(PixelB, "B", bColor, 2);

        void DrawCursor(float x, SKColor color)
        {
            paint.Style = SKPaintStyle.Stroke; paint.Color = color; paint.StrokeWidth = 1.5f;
            using var dash = SKPathEffect.CreateDash([6, 4], 0); paint.PathEffect = dash;
            rp.Canvas.DrawLine(x, area.Top, x, area.Bottom, paint); paint.PathEffect = null;
        }
        void DrawCard(SKRect rect)
        {
            paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White.WithAlpha(245); rp.Canvas.DrawRoundRect(rect, 4, 4, paint);
            paint.Style = SKPaintStyle.Stroke; paint.Color = SKColor.Parse("#CBD5E1"); paint.StrokeWidth = 1; rp.Canvas.DrawRoundRect(rect, 4, 4, paint);
        }
        SKRect Handle(float x, string label, SKColor color, float offset)
        {
            if (x < area.Left || x > area.Right) return SKRect.Empty;
            float left = Math.Clamp(x + offset, area.Left + 1, area.Right - 25);
            // A is above B at the edges as well, so coincident cursors can always be separated.
            float bottom = area.Bottom - (label == "A" ? 26 : 2);
            var rect = new SKRect(left, bottom - 22, left + 24, bottom);
            paint.Style = SKPaintStyle.Fill; paint.Color = color; rp.Canvas.DrawRoundRect(rect, 3, 3, paint);
            text.Color = SKColors.White; rp.Canvas.DrawText(label, rect.Left + 7, rect.Top + text.TextSize + 3, text);
            ReservedAreas.Add(rect); return rect;
        }
        string Fit(string value, float maxWidth)
        {
            if (text.MeasureText(value) <= maxWidth) return value;
            while (value.Length > 0 && text.MeasureText(value + "…") > maxWidth) value = value[..^1];
            return value + "…";
        }
    }
    static string Label(CursorValueRow row) => string.IsNullOrWhiteSpace(row.Unit) ? row.Name : $"{row.Name} [{row.Unit}]";
}
