namespace TiaTraceStudio;

public static class AnalysisService
{
    public static List<StatisticsRow> Statistics(TraceData data, ChartOptions options)
    {
        return data.Signals.Where(s => s.Visible).Select(s =>
        {
            double[] scaled = s.Scaled();
            var selected = scaled.Where((v, i) => data.Time[i] >= options.Start && data.Time[i] <= options.End && double.IsFinite(v)).ToArray();
            double mean = selected.Length > 0 ? selected.Average() : double.NaN;
            double? a = ValueAt(data.Time, scaled, options.CursorA), b = ValueAt(data.Time, scaled, options.CursorB);
            return new StatisticsRow(s.Label, selected.Length, selected.Length > 0 ? selected.Min() : double.NaN,
                selected.Length > 0 ? selected.Max() : double.NaN, mean,
                selected.Length > 0 ? Math.Sqrt(selected.Average(v => (v - mean) * (v - mean))) : double.NaN, a, b, b - a);
        }).ToList();
    }
    // Linear interpolation, never extrapolation or bridging missing samples.
    public static double? ValueAt(double[] time, double[] values, double t)
    {
        if (t < time[0] || t > time[^1]) return null;
        int i = Array.BinarySearch(time, t);
        if (i >= 0) return double.IsFinite(values[i]) ? values[i] : null;
        i = ~i;
        if (i == 0 || i >= time.Length || !double.IsFinite(values[i - 1]) || !double.IsFinite(values[i])) return null;
        return values[i - 1] + (values[i] - values[i - 1]) * (t - time[i - 1]) / (time[i] - time[i - 1]);
    }
    public static Signal Difference(Signal a, Signal b) => new()
    {
        Source = $"derived:difference:{a.Source}:{b.Source}", Unit = a.Unit,
        Color = "#A855F7", Panel = 2, Values = a.Scaled().Zip(b.Scaled(), (x, y) => x - y).ToArray()
    };
    public static Signal Derivative(Signal signal, double[] time)
    {
        var values = signal.Scaled(); var result = new double[time.Length]; result[0] = double.NaN;
        for (int i = 1; i < result.Length; i++) result[i] = (values[i] - values[i - 1]) / (time[i] - time[i - 1]);
        return new Signal { Source = $"derived:derivative:{signal.Source}", Unit = $"{signal.Unit}/s", Color = "#0891B2", Panel = 2, Values = result };
    }
    public static List<Signal> Compare(TraceData original, TraceData other, double shift)
    {
        var time = other.Time.Select(t => t + shift).ToArray();
        return other.Signals.Select(s =>
        {
            var match = original.Signals.FirstOrDefault(a => a.Source == s.Source);
            return new Signal
            {
                Source = $"comparison:{other.FileName}:{s.Source}",
                Color = match?.Color ?? s.Color, Unit = match?.Unit ?? s.Unit, Panel = match?.Panel ?? 1, Axis = match?.Axis ?? "Lijeva",
                Factor = match?.Factor ?? 1, Offset = match?.Offset ?? 0, Style = "Crtkana",
                PercentMin = match?.PercentMin, PercentMax = match?.PercentMax,
                Values = original.Time.Select(t => ValueAt(time, s.Values, t) ?? double.NaN).ToArray()
            };
        }).ToList();
    }
}
