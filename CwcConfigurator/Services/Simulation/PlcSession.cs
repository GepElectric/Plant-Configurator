using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// One PLC database in the simulator: its signals, the S7 connection to the PLC and the images of the simulator
/// DB (DB30) and the devices DB (DB27). Implements <see cref="ISignalIo"/> for the plant model: reads come from the
/// last DB reads, writes go to output images that <see cref="FlushAsync"/> sends in one write per DB.
/// </summary>
public sealed class PlcSession : ISignalIo, IDisposable
{
    private readonly SimulatorS7Client _client = new();
    private Dictionary<(SimulatorSignalKind, int), List<SimulatorSignal>> _byId = [];
    private byte[] _data = [];
    private byte[] _deviceData = [];
    private byte[]? _output;
    private byte[]? _deviceOutput;

    public PlcSession(string name, CwcProject project, string settingsKey)
    {
        Name = name;
        Project = project;
        SettingsKey = settingsKey;
        Settings = SimulatorSettingsStore.Load(settingsKey);
        Devices = Settings.Devices?.Select(device => device.Clone()).ToList() ?? DeviceLayout.UnitTemplate();
        var layout = SimulatorLayout.FromProject(project);
        if (Settings.Constants is { } constants)
        {
            try
            {
                layout = SimulatorLayout.FromConstants(constants);
            }
            catch (ArgumentException)
            {
            }
        }

        ApplyLayout(layout);
    }

    public string Name { get; }
    public CwcProject Project { get; }
    public string SettingsKey { get; }
    public SimulatorConnectionSettings Settings { get; }
    public SimulatorLayout Layout { get; private set; } = SimulatorLayout.Default;

    /// <summary>Devices of the devices DB in DB order (empty = no devices DB).</summary>
    public List<DeviceDefinition> Devices { get; private set; }

    public List<SimulatorSignal> Signals { get; private set; } = [];
    public bool IsConnected => _client.IsConnected;
    public int DbNumber { get; private set; } = SimulatorLayout.DefaultDbNumber;
    public int DevicesDbNumber => Settings.DevicesDbNumber;

    /// <summary>The devices DB was found with the expected size and is read/written.</summary>
    public bool DevicesActive { get; private set; }

    public string? Warning { get; private set; }

    public override string ToString() => Name;

    public void ApplyLayout(SimulatorLayout layout)
    {
        Layout = layout;
        Signals = SimulatorPlanBuilder.Build(Project, layout);
        Signals.AddRange(BuildDeviceSignals());
        // Several channels can carry the same ID (e.g. disabled spares); enabled channels come first.
        _byId = Signals
            .GroupBy(signal => (signal.Kind, signal.Id))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(signal => signal.IsEnabled).ToList());
    }

    public void ApplyDevices(IEnumerable<DeviceDefinition> devices, int dbNumber)
    {
        Devices = devices.Select(device => device.Clone()).ToList();
        Settings.Devices = Devices.Select(device => device.Clone()).ToList();
        Settings.DevicesDbNumber = dbNumber;
        ApplyLayout(Layout);
        SaveSettings();
    }

    public void SaveSettings()
    {
        Settings.Constants = Layout.ToConstants();
        SimulatorSettingsStore.Save(SettingsKey, Settings);
    }

    public async Task ConnectAsync(string ip, short rack, short slot, int dbNumber)
    {
        Warning = null;
        DevicesActive = false;
        await _client.ConnectAsync(ip, rack, slot);
        DbNumber = dbNumber;
        if (!await _client.HasLengthAsync(dbNumber, Layout.TotalBytes))
        {
            _client.Disconnect();
            throw new InvalidOperationException(
                $"DB{dbNumber} is shorter than the expected {Layout.TotalBytes} bytes ({Layout.Describe()}). " +
                "Check the DB number and the PLC constants under 'Simulator DB layout'.");
        }

        var warnings = new List<string>();
        if (await _client.HasLengthAsync(dbNumber, Layout.TotalBytes + 2))
        {
            warnings.Add($"DB{dbNumber} is longer than {Layout.TotalBytes} bytes; check the PLC constants under 'Simulator DB layout'.");
        }

        var deviceBytes = DeviceLayout.TotalBytes(Devices);
        if (deviceBytes > 0)
        {
            if (!await _client.HasLengthAsync(DevicesDbNumber, deviceBytes))
            {
                warnings.Add($"DB{DevicesDbNumber} (devices) is shorter than the {deviceBytes} bytes of the device list; device values are not written. Check 'Devices'.");
            }
            else
            {
                DevicesActive = true;
                if (await _client.HasLengthAsync(DevicesDbNumber, deviceBytes + 2))
                {
                    warnings.Add($"DB{DevicesDbNumber} (devices) is longer than the {deviceBytes} bytes of the device list; check 'Devices'.");
                }
            }
        }

        Warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        Settings.IpAddress = ip.Trim();
        Settings.Rack = rack;
        Settings.Slot = slot;
        Settings.DbNumber = dbNumber;
        SaveSettings();
        await ReadAsync();
    }

    public void Disconnect()
    {
        _client.Disconnect();
        DevicesActive = false;
        _data = [];
        _deviceData = [];
        _output = null;
        _deviceOutput = null;
        foreach (var signal in Signals)
        {
            signal.ClearValue();
        }
    }

    /// <summary>Reads the simulator DB (and the devices DB) and refreshes the signal values.</summary>
    public async Task ReadAsync()
    {
        _data = await _client.ReadBytesAsync(DbNumber, 0, Layout.TotalBytes);
        _deviceData = DevicesActive ? await _client.ReadBytesAsync(DevicesDbNumber, 0, DeviceLayout.TotalBytes(Devices)) : [];
        _output = null;
        _deviceOutput = null;
        foreach (var signal in Signals)
        {
            signal.UpdateFrom(_data, _deviceData);
        }
    }

    /// <summary>Writes the values the model produced: DI/AI/COUNTER/RTD area of the simulator DB, and the devices DB.</summary>
    public async Task FlushAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        if (_output is not null)
        {
            var bytes = _output.AsSpan(0, Math.Min(Layout.DoStart, _output.Length)).ToArray();
            _output = null;
            await _client.WriteBytesAsync(DbNumber, 0, bytes);
        }

        if (_deviceOutput is not null && DevicesActive)
        {
            var bytes = _deviceOutput;
            _deviceOutput = null;
            await _client.WriteBytesAsync(DevicesDbNumber, 0, bytes);
        }
    }

    public Task WriteBitAsync(SimulatorSignal signal, bool value) =>
        _client.WriteBitAsync(DbOf(signal), signal.ByteOffset, signal.BitOffset, value);

    public Task WriteIntAsync(SimulatorSignal signal, short value) => _client.WriteIntAsync(DbOf(signal), signal.ByteOffset, value);

    public Task WriteRealAsync(SimulatorSignal signal, float value) => _client.WriteRealAsync(DbOf(signal), signal.ByteOffset, value);

    public IReadOnlyList<SimulatorSignal> Find(SimulatorSignalKind kind, int id) =>
        _byId.TryGetValue((kind, id), out var list) ? list : [];

    public bool TryRead(SimulatorSignalKind kind, int id, out double value)
    {
        value = 0;
        var signal = Find(kind, id).FirstOrDefault();
        if (signal?.ForcedValue is { } forced)
        {
            // A force in the signal list wins over what the PLC sends (DO/AO) and over the model (DI/AI…).
            value = double.IsNaN(forced) ? 0 : forced;
            return true;
        }

        var data = signal is null ? [] : signal.InDeviceDb ? _deviceOutput ?? _deviceData : _output ?? _data;
        if (signal is null || data.Length <= signal.ByteOffset)
        {
            return false;
        }

        if (signal.IsDigital)
        {
            value = (data[signal.ByteOffset] & (1 << signal.BitOffset)) != 0 ? 1 : 0;
            return true;
        }

        if (signal.IsReal)
        {
            value = BitConverter.Int32BitsToSingle((data[signal.ByteOffset] << 24) | (data[signal.ByteOffset + 1] << 16)
                                                   | (data[signal.ByteOffset + 2] << 8) | data[signal.ByteOffset + 3]);
            return true;
        }

        var raw = (short)((data[signal.ByteOffset] << 8) | data[signal.ByteOffset + 1]);
        value = signal.HasScaling ? signal.Scaling!.ToEngineering(raw) : raw;
        return true;
    }

    public void Write(SimulatorSignalKind kind, int id, double value)
    {
        foreach (var signal in Find(kind, id))
        {
            if (!signal.IsWritable || signal.IsForced)
            {
                continue;
            }

            WriteImage(signal, value);
        }
    }

    public bool HasForcedOutputs => Signals.Any(signal => signal.IsWritable && signal.IsForced);

    /// <summary>Writes every forced simulator output (DI/AI/RTD/COUNTER/device) into the output images.</summary>
    public void ApplyForces()
    {
        foreach (var signal in Signals)
        {
            if (signal.IsWritable && signal.ForcedValue is { } value)
            {
                WriteImage(signal, value);
            }
        }
    }

    private void WriteImage(SimulatorSignal signal, double value)
    {
        {
            byte[] target;
            if (signal.InDeviceDb)
            {
                if (_deviceData.Length == 0)
                {
                    return;
                }

                target = _deviceOutput ??= (byte[])_deviceData.Clone();
            }
            else
            {
                if (_data.Length == 0 || signal.ByteOffset >= Layout.DoStart)
                {
                    return;
                }

                target = _output ??= (byte[])_data.Clone();
            }

            if (signal.IsDigital)
            {
                var mask = (byte)(1 << signal.BitOffset);
                target[signal.ByteOffset] = value >= 0.5 ? (byte)(target[signal.ByteOffset] | mask) : (byte)(target[signal.ByteOffset] & ~mask);
            }
            else if (signal.IsReal)
            {
                var bits = BitConverter.SingleToInt32Bits((float)value);
                target[signal.ByteOffset] = (byte)(bits >> 24);
                target[signal.ByteOffset + 1] = (byte)(bits >> 16);
                target[signal.ByteOffset + 2] = (byte)(bits >> 8);
                target[signal.ByteOffset + 3] = (byte)bits;
            }
            else
            {
                // Wire break (NaN): 32767 = overflow, which the PLC treats as a measurement error.
                var raw = double.IsNaN(value) ? short.MaxValue
                    : signal.HasScaling ? signal.Scaling!.ToRaw(value)
                    : (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
                target[signal.ByteOffset] = (byte)((ushort)raw >> 8);
                target[signal.ByteOffset + 1] = (byte)raw;
            }
        }
    }

    public void Dispose() => _client.Dispose();

    private int DbOf(SimulatorSignal signal) => signal.InDeviceDb ? DevicesDbNumber : DbNumber;

    /// <summary>Signals of the devices DB, named after the measurements the PLC copies them to.</summary>
    private IEnumerable<SimulatorSignal> BuildDeviceSignals()
    {
        var meas = SimulatorPlanBuilder.ById(Project.DbCatalog.MeasRows);
        var offset = 0;
        for (var index = 0; index < Devices.Count; index++)
        {
            var device = Devices[index];
            yield return DeviceBit(device, index, offset, 0, DeviceLayout.BitId(index, 99), "ALM_COMM", "Communication alarm (TRUE = no communication)");
            for (var bit = 0; bit < DeviceLayout.AlarmCount(device.Type); bit++)
            {
                yield return DeviceBit(device, index, offset + 2 + bit / 8, bit % 8, DeviceLayout.BitId(index, bit), $"ALARMS[{bit}]", $"Protection alarm {bit}");
            }

            var measOffset = offset + DeviceLayout.MeasOffset(device.Type);
            for (var i = 0; i < DeviceLayout.MeasCount(device.Type); i++)
            {
                yield return DeviceReal(device, index, device.FirstMeasId + i, measOffset + i * 4, $"MEAS[{i}]", meas);
            }

            var energyOffset = offset + DeviceLayout.EnergyOffset(device.Type);
            for (var i = 0; i < DeviceLayout.EnergyCount(device.Type); i++)
            {
                var id = device.FirstEnergyId > 0 ? device.FirstEnergyId + i : -1 - index * 10 - i;
                yield return DeviceReal(device, index, id, energyOffset + i * 4, $"ENERGY[{i}]", meas);
            }

            offset += DeviceLayout.Size(device.Type);
        }
    }

    private static SimulatorSignal DeviceBit(DeviceDefinition device, int index, int byteOffset, int bit, int id, string field, string description) => new()
    {
        Kind = SimulatorSignalKind.DEVBIT,
        Module = index,
        Channel = 0,
        Id = id,
        Config = SignalConfigState.Enabled,
        System = device.Name,
        Name = description,
        PlcName = field,
        ByteOffset = byteOffset,
        BitOffset = bit,
        InDeviceDb = true
    };

    private SimulatorSignal DeviceReal(DeviceDefinition device, int index, int measId, int byteOffset, string field, IReadOnlyDictionary<int, DbStringRow> meas)
    {
        meas.TryGetValue(measId, out var row);
        return new SimulatorSignal
        {
            Kind = SimulatorSignalKind.DEV,
            Module = index,
            Channel = 0,
            Id = measId,
            Config = row is null && measId < 0 ? SignalConfigState.Disabled : SignalConfigState.Enabled,
            System = device.Name,
            Name = row is null ? field : $"{field} → {row.NameEnglish}".Trim(),
            PlcName = row?.PlcName is { Length: > 0 } plcName ? plcName : field,
            Unit = SimulatorPlanBuilder.ResolveUnit(Project.DbCatalog, row?.Unit),
            ByteOffset = byteOffset,
            InDeviceDb = true
        };
    }
}
