namespace CwcConfigurator.Domain;

/// <summary>
/// Hardware-dictated upper bounds for signal IDs.
/// Centralized so validator/UI/import all agree on the same physical limits.
/// </summary>
public static class HardwareLimits
{
    public const int MaxDigitalInputId = 607;
    public const int MaxDigitalOutputId = 287;
    public const int MaxAnalogInputId = 287;
    public const int MaxRtdInputId = 287;
    public const int MaxCounterId = 287;
    public const int MaxAnalogOutputId = 31;
}
