namespace CwcConfigurator.Services.Simulation;

/// <summary>Device structure types of the devices DB (DB27 "SIMULATOR_DEVICES", standard access).</summary>
public enum DeviceType
{
    /// <summary>DT_DEVICE_MEASUREMENT (Sentron, PAC): ALM_COMM, MEAS[0..15] Real, ENERGY[0..3] Real — 82 bytes.</summary>
    Measurement,

    /// <summary>DT_DEVICE_GENERATOR_PROTECTION / LINE_PROTECTION: ALM_COMM, ALARMS[0..31], MEAS[0..23], ENERGY[0..3] — 118 bytes.</summary>
    Protection,

    /// <summary>DT_DEVICE_TRAFO_PROTECTION: ALM_COMM, ALARMS[0..31], MEAS[0..19] — 86 bytes.</summary>
    TrafoProtection,

    /// <summary>DT_DEVICE_EXCITATION: ALM_COMM, MEAS[0..15] — 66 bytes.</summary>
    Excitation
}

/// <summary>
/// One device in the devices DB. The unit PLC copies MEAS[i] of the device to the measurement ID
/// FirstMeasId + i (and ENERGY[i] to FirstEnergyId + i), so the device fields carry those MEAS names.
/// </summary>
public sealed class DeviceDefinition
{
    public string Name { get; set; } = "";
    public DeviceType Type { get; set; }
    public int FirstMeasId { get; set; }
    public int FirstEnergyId { get; set; }

    public DeviceDefinition Clone() => new() { Name = Name, Type = Type, FirstMeasId = FirstMeasId, FirstEnergyId = FirstEnergyId };
}

public static class DeviceLayout
{
    public const int DefaultDbNumber = 27;

    /// <summary>The unit DB27 of our standard PLC program (same order and types as in TIA).</summary>
    public static List<DeviceDefinition> UnitTemplate() =>
    [
        new() { Name = "GEN_SENTRON", Type = DeviceType.Measurement, FirstMeasId = 48, FirstEnergyId = 64 },
        new() { Name = "AUX_SENTRON", Type = DeviceType.Measurement, FirstMeasId = 216, FirstEnergyId = 232 },
        new() { Name = "GEN_PROT", Type = DeviceType.Protection, FirstMeasId = 72, FirstEnergyId = 0 },
        new() { Name = "LINE_PROT", Type = DeviceType.Protection, FirstMeasId = 144, FirstEnergyId = 164 },
        new() { Name = "TRAFO_PROT", Type = DeviceType.TrafoProtection, FirstMeasId = 120, FirstEnergyId = 0 },
        new() { Name = "EXC", Type = DeviceType.Excitation, FirstMeasId = 96, FirstEnergyId = 0 }
    ];

    public static int Size(DeviceType type) => type switch
    {
        DeviceType.Measurement => 82,
        DeviceType.Protection => 118,
        DeviceType.TrafoProtection => 86,
        _ => 66
    };

    public static int MeasCount(DeviceType type) => type switch
    {
        DeviceType.Protection => 24,
        DeviceType.TrafoProtection => 20,
        _ => 16
    };

    public static int EnergyCount(DeviceType type) => type is DeviceType.Measurement or DeviceType.Protection ? 4 : 0;

    public static int AlarmCount(DeviceType type) => type is DeviceType.Protection or DeviceType.TrafoProtection ? 32 : 0;

    /// <summary>Byte offset of MEAS[0] inside the device (after ALM_COMM and the ALARMS bits, on an even byte).</summary>
    public static int MeasOffset(DeviceType type) => AlarmCount(type) > 0 ? 6 : 2;

    public static int EnergyOffset(DeviceType type) => MeasOffset(type) + MeasCount(type) * 4;

    public static int TotalBytes(IEnumerable<DeviceDefinition> devices) => devices.Sum(device => Size(device.Type));

    /// <summary>Signal ID of a device bit: 100 per device, ALARMS[n] = n, ALM_COMM = 99.</summary>
    public static int BitId(int deviceIndex, int bit) => deviceIndex * 100 + bit;
}
