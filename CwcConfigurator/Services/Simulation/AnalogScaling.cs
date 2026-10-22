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

    /// <summary>Raw channel value for an engineering value, rounded and limited to the Int range.</summary>
    public short ToRaw(double value)
    {
        double raw;
        if (TenthsOfValue)
        {
            raw = (value / ScaleFactor - Offset) * 10.0;
        }
        else
        {
            raw = RawLow + (value / ScaleFactor - Offset - ValueLow) * (RawHigh - RawLow) / (ValueHigh - ValueLow);
        }

        return (short)Math.Clamp(Math.Round(raw, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
    }

    public string Describe()
    {
        if (!IsValid)
        {
            return "no valid scaling (raw values only)";
        }

        var text = TenthsOfValue
            ? "raw = value × 10"
            : $"raw {RawLow:0.###}…{RawHigh:0.###} = {ValueLow:0.###}…{ValueHigh:0.###}";
        if (Offset != 0)
        {
            text += $", offset {Offset:0.###}";
        }

        if (ScaleFactor != 1)
        {
            text += $", scale factor {ScaleFactor:0.###}";
        }

        return text;
    }
}
