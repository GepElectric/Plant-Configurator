namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// Conversion between a raw analog channel value (Int, e.g. 0..27648) and the engineering value, the same
/// way the PLC blocks do it:
/// <list type="bullet">
/// <item>FB_AI: VAL = (VAL_1 + (PIW − X1)·(VAL_2 − VAL_1)/(X2 − X1) + OFFSET) · SCALE_FACTOR</item>
/// <item>FB_RTD (temperature types): VAL = (PIW / 10 + OFFSET) · SCALE_FACTOR, so 55.6 °C = 556</item>
/// <item>FB_AO: PQW = X1 + (VAL − LOW)·(X2 − X1)/(HIGH − LOW)</item>
/// </list>
/// X1/X2 are 0/27648 unless custom HW limits are enabled on the channel.
/// </summary>
public sealed record AnalogScaling(
    double RawLow,
    double RawHigh,
    double ValueLow,
    double ValueHigh,
    double Offset = 0,
    double ScaleFactor = 1,
    bool TenthsOfValue = false)
{
    public const double NominalRawHigh = 27648;

    /// <summary>FB_AI / FB_RTD treat PIW = −32768 or PIW ≥ 32000 as a measurement error.</summary>
    public static bool IsErrorRaw(int raw) => raw == short.MinValue || raw >= 32000;

    public bool IsValid => TenthsOfValue
        ? ScaleFactor != 0
        : ScaleFactor != 0 && RawHigh != RawLow && ValueHigh != ValueLow;

    public double ToEngineering(int raw)
    {
        if (TenthsOfValue)
        {
            return (raw / 10.0 + Offset) * ScaleFactor;
        }

        return (ValueLow + (raw - RawLow) * (ValueHigh - ValueLow) / (RawHigh - RawLow) + Offset) * ScaleFactor;
    }

    /// <summary>Exact (unrounded) raw value for an engineering value.</summary>
    public double ToRawExact(double value)
    {
        return TenthsOfValue
            ? (value / ScaleFactor - Offset) * 10.0
            : RawLow + (value / ScaleFactor - Offset - ValueLow) * (RawHigh - RawLow) / (ValueHigh - ValueLow);
    }

    /// <summary>Raw channel value for an engineering value, rounded and limited to the Int range.</summary>
    public short ToRaw(double value)
    {
        return (short)Math.Clamp(Math.Round(ToRawExact(value), MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
    }

    /// <summary>Engineering values at the ends of the raw range (X1…X2), lower first.</summary>
    public (double Low, double High) EngineeringRange
    {
        get
        {
            var a = ToEngineering((int)RawLow);
            var b = ToEngineering((int)RawHigh);
            return a <= b ? (a, b) : (b, a);
        }
    }

    /// <summary>Short form for the grid, e.g. "69.69…89.69 m = raw 0…27648".</summary>
    public string Describe(string unit = "")
    {
        if (!IsValid)
        {
            return "no valid scaling (raw values only)";
        }

        if (TenthsOfValue)
        {
            return Offset != 0 || ScaleFactor != 1 ? "raw = value × 10 (with offset / scale factor)" : "raw = value × 10";
        }

        var (low, high) = EngineeringRange;
        var suffix = string.IsNullOrWhiteSpace(unit) ? "" : " " + unit;
        return $"{Format(low)}…{Format(high)}{suffix} = raw {RawLow:0}…{RawHigh:0}";
    }

    /// <summary>All parameters, for the tooltip.</summary>
    public string Details()
    {
        if (TenthsOfValue)
        {
            return $"VAL = (raw / 10 + OFFSET) × SCALE FACTOR; OFFSET {Format(Offset)}, SCALE FACTOR {Format(ScaleFactor)}";
        }

        return "VAL = (VAL_1 + (raw − X1)·(VAL_2 − VAL_1)/(X2 − X1) + OFFSET) × SCALE FACTOR; " +
               $"X1 {RawLow:0}, X2 {RawHigh:0}, VAL_1 {Format(ValueLow)}, VAL_2 {Format(ValueHigh)}, OFFSET {Format(Offset)}, SCALE FACTOR {Format(ScaleFactor)}";
    }

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
