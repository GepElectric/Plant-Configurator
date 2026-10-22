using ScottPlot;
using ScottPlot.LayoutEngines;
using SkiaSharp;

namespace TiaTraceStudio;

/// <summary>
/// Legend drawn below the graph (under the time axis), wrapped into rows, so it never covers the curves.
/// It is a render step after the axes, outside the data area clip.
/// </summary>
public sealed class BottomLegend(float fontSize, float offset) : IRenderAction
{
    /// <summary>Adds (or replaces) the legend step of a plot, right after its axes are drawn.</summary>
    public static void Attach(Plot plot, BottomLegend? legend)
    {
        var actions = plot.RenderManager.RenderActions;
        actions.RemoveAll(a => a is BottomLegend);
        if (legend == null) return;
        int panels = actions.FindIndex(a => a.GetType().Name == "RenderPanels");
        actions.Insert(panels >= 0 ? panels + 1 : actions.Count, legend);
    }

    const float SymbolWidth = 26, SymbolGap = 7, ItemGap = 24;
    static float LineHeight(float size) => size * 1.65f;

    static SKPaint Paint(float size) => new() { TextSize = size, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Segoe UI"), Color = SKColors.Black };

    static List<LegendItem> Items(Plot plot) => plot.GetPlottables()
        .Where(p => p.IsVisible)
        .SelectMany(p => p.LegendItems)
        .Concat(plot.Legend.ManualItems)
        .Where(i => i.IsVisible && !string.IsNullOrWhiteSpace(i.LabelText))
        .ToList();

    static List<List<(LegendItem Item, float Width)>> Rows(List<LegendItem> items, float width, SKPaint paint)
    {
        var rows = new List<List<(LegendItem, float)>>(); var row = new List<(LegendItem, float)>(); float used = 0;
        foreach (var item in items)
        {
            float itemWidth = SymbolWidth + SymbolGap + paint.MeasureText(item.LabelText);
            if (row.Count > 0 && used + ItemGap + itemWidth > width) { rows.Add(row); row = []; used = 0; }
            used += (row.Count > 0 ? ItemGap : 0) + itemWidth; row.Add((item, itemWidth));
        }
        if (row.Count > 0) rows.Add(row);
        return rows;
    }

    /// <summary>Height the legend needs below the time axis for the given drawing width.</summary>
    public static float Height(Plot plot, float width, float size)
    {
        var items = Items(plot);
        if (items.Count == 0) return 0;
        using var paint = Paint(size);
        return Rows(items, Math.Max(100, width), paint).Count * LineHeight(size) + 8;
    }

    public void Render(RenderPack rp)
    {
        var items = Items(rp.Plot);
        if (items.Count == 0) return;
        using var paint = Paint(fontSize);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        rp.CanvasState.DisableClipping();
        float top = rp.DataRect.Bottom + offset;
        foreach (var row in Rows(items, rp.DataRect.Width, paint))
        {
            float total = row.Sum(r => r.Width) + ItemGap * (row.Count - 1);
            float x = rp.DataRect.Left + Math.Max(0, (rp.DataRect.Width - total) / 2);
            float middle = top + LineHeight(fontSize) / 2;
            foreach (var (item, width) in row)
            {
                var color = item.LineWidth > 0 ? item.LineColor : item.MarkerFillColor;
                line.Color = color.ToSKColor(); line.StrokeWidth = Math.Max(2, item.LineWidth);
                rp.Canvas.DrawLine(x, middle, x + SymbolWidth, middle, line);
                if (item.MarkerShape != MarkerShape.None && item.MarkerSize > 0)
                { dot.Color = item.MarkerFillColor.ToSKColor(); rp.Canvas.DrawCircle(x + SymbolWidth / 2, middle, Math.Max(2.5f, item.MarkerSize / 2), dot); }
                rp.Canvas.DrawText(item.LabelText, x + SymbolWidth + SymbolGap, middle + fontSize * 0.36f, paint);
                x += width + ItemGap;
            }
            top += LineHeight(fontSize);
        }
    }
}

/// <summary>Fixed side margins (aligned panels) with the bottom margin grown by the legend height for the actual image width.</summary>
public sealed class LegendBelowLayout(PixelPadding padding, float fontSize, bool legend) : ILayoutEngine
{
    public Layout GetLayout(PixelRect figure, Plot plot)
    {
        float extra = legend ? BottomLegend.Height(plot, figure.Width - padding.Left - padding.Right, fontSize) : 0;
        return new FixedPadding(new PixelPadding(padding.Left, padding.Right, padding.Bottom + extra, padding.Top)).GetLayout(figure, plot);
    }
}
