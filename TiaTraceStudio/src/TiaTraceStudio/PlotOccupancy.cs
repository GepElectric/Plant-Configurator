using SkiaSharp;

namespace TiaTraceStudio;

/// <summary>Conservative screen-space raster of finite trace segments, with constant-time rectangle queries.</summary>
public sealed class PlotOccupancy(SKRect area, float cell = 4)
{
    readonly int width = Math.Max(1, (int)Math.Ceiling(area.Width / cell));
    readonly int height = Math.Max(1, (int)Math.Ceiling(area.Height / cell));
    int[]? sums;
    readonly HashSet<(int X, int Y)> pixels = [];

    public void AddSegment(float x1, float y1, float x2, float y2, float padding = 3)
    {
        if (!float.IsFinite(x1 + y1 + x2 + y2)) return;
        // Liang–Barsky clipping keeps even very long off-screen segments bounded.
        float dx = x2 - x1, dy = y2 - y1, lo = 0, hi = 1;
        bool Clip(float p, float q)
        {
            if (p == 0) return q >= 0;
            float r = q / p;
            if (p < 0) { if (r > hi) return false; lo = Math.Max(lo, r); }
            else { if (r < lo) return false; hi = Math.Min(hi, r); }
            return true;
        }
        if (!Clip(-dx, x1 - area.Left) || !Clip(dx, area.Right - x1) || !Clip(-dy, y1 - area.Top) || !Clip(dy, area.Bottom - y1)) return;
        float startX = x1 + lo * dx, startY = y1 + lo * dy;
        dx *= hi - lo; dy *= hi - lo;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) / cell));
        int radius = Math.Max(1, (int)Math.Ceiling(padding / cell));
        for (int i = 0; i <= steps; i++)
        {
            int x = (int)((startX + dx * i / steps - area.Left) / cell), y = (int)((startY + dy * i / steps - area.Top) / cell);
            for (int a = x - radius; a <= x + radius; a++)
                for (int b = y - radius; b <= y + radius; b++)
                    if (a >= 0 && a < width && b >= 0 && b < height) pixels.Add((a, b));
        }
        sums = null;
    }
    public int Count(SKRect rect)
    {
        if (sums == null)
        {
            sums = new int[(width + 1) * (height + 1)];
            for (int y = 1; y <= height; y++)
                for (int x = 1; x <= width; x++)
                    sums[y * (width + 1) + x] = (pixels.Contains((x - 1, y - 1)) ? 1 : 0) + sums[(y - 1) * (width + 1) + x] + sums[y * (width + 1) + x - 1] - sums[(y - 1) * (width + 1) + x - 1];
        }
        int left = Math.Clamp((int)Math.Floor((rect.Left - area.Left) / cell), 0, width), right = Math.Clamp((int)Math.Ceiling((rect.Right - area.Left) / cell), 0, width);
        int top = Math.Clamp((int)Math.Floor((rect.Top - area.Top) / cell), 0, height), bottom = Math.Clamp((int)Math.Ceiling((rect.Bottom - area.Top) / cell), 0, height);
        return sums[bottom * (width + 1) + right] - sums[top * (width + 1) + right] - sums[bottom * (width + 1) + left] + sums[top * (width + 1) + left];
    }
}
