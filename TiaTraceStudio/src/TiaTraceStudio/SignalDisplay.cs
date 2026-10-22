using System.IO;

namespace TiaTraceStudio;

public static class SignalDisplay
{
    /// <summary>Reserved colour for NaN samples (invalid measurement), drawn at 0; never usable as a signal colour.</summary>
    public const string InvalidColor = "#FF00FF";
    public static bool IsReserved(string color) => string.Equals(color?.Trim(), InvalidColor, StringComparison.OrdinalIgnoreCase);
    /// <summary>0 where the signal is NaN, NaN elsewhere, so only invalid stretches are drawn.</summary>
    public static double[]? InvalidMask(double[] values)
    {
        if (!values.Any(double.IsNaN)) return null;
        return values.Select(v => double.IsNaN(v) ? 0 : double.NaN).ToArray();
    }
    public static bool IsTemperature(Signal signal) => IsTemperatureUnit(signal.Unit);
    public static bool IsTemperatureUnit(string? unit) => (unit ?? "").Trim().Replace(" ", "").ToLowerInvariant() is "°c" or "degc" or "c°" or "°";
    static bool IsRegulatorOutput(string source)
    {
        string tag = TagMatcher.Normalize(source);
        return tag.Contains("REG_INTF.", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// Shown as recorded in the normalized graph: positions (%, always 0–100), regulator interface signals (−100…100 %)
    /// and temperatures (°C, on the same axis). An explicit range of the graph itself still wins.
    /// </summary>
    public static bool IsNativePercent(Signal signal) =>
        signal.PercentFromProject && ((signal.Unit ?? "").Trim() == "%" || IsTemperature(signal) || IsRegulatorOutput(signal.Source));

    public static double[] Values(Signal signal, ChartOptions options)
    {
        var values = signal.Scaled();
        if (options.DisplayMode != ChartOptions.PercentMode || IsNativePercent(signal)) return values;
        if (!signal.PercentMin.HasValue || !signal.PercentMax.HasValue || !double.IsFinite(signal.PercentMin.Value) || !double.IsFinite(signal.PercentMax.Value) || signal.PercentMin >= signal.PercentMax)
            throw new InvalidDataException($"Unesi raspon za 0 % i 100 % signala '{signal.DisplayName}'. Donja granica mora biti manja od gornje.");
        double min = signal.PercentMin.Value, span = signal.PercentMax.Value - min;
        var normalized = values.Select(v => (v - min) / span * 100).ToArray();
        if (normalized.Any(double.IsInfinity)) throw new InvalidDataException($"Raspon za postotke signala '{signal.DisplayName}' je premalen.");
        return normalized;
    }
    public static string AxisKey(Signal signal, int index) => string.IsNullOrWhiteSpace(signal.Unit) ? $"__signal_{index}" : signal.Unit.Trim();
    public static string PlotName(Signal signal, int index) => string.IsNullOrWhiteSpace(signal.Name) ? $"Signal {index + 1} · unesi naziv" : signal.Name.Trim();
    public static void RequireNames(StudioProject project)
    {
        var unnamed = project.Data.Signals.Select((s, i) => (s, i)).Where(x => x.s.Visible && string.IsNullOrWhiteSpace(x.s.Name)).ToArray();
        if (unnamed.Length > 0) throw new InvalidDataException("Prije izvoza unesi naziv za svaki uključeni signal. Nedostaju nazivi: " + string.Join(", ", unnamed.Select(x => $"signal {x.i + 1}")) + ".");
    }
}
