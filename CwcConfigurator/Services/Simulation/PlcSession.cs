using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// One PLC database in the simulator: its signals, the S7 connection to the PLC and the image of the simulator
/// DB. Implements <see cref="ISignalIo"/> for the plant model: reads come from the last DB read, writes go to an
/// output image of the DI/AI/COUNTER/RTD area that <see cref="FlushAsync"/> sends in one write.
/// </summary>
public sealed class PlcSession : ISignalIo, IDisposable
{
    private readonly SimulatorS7Client _client = new();
    private Dictionary<(SimulatorSignalKind, int), List<SimulatorSignal>> _byId = [];
    private byte[] _data = [];
    private byte[]? _output;

    public PlcSession(string name, CwcProject project, string settingsKey)
    {
        Name = name;
        Project = project;
        SettingsKey = settingsKey;
        Settings = SimulatorSettingsStore.Load(settingsKey);
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
    public List<SimulatorSignal> Signals { get; private set; } = [];
    public bool IsConnected => _client.IsConnected;
    public int DbNumber { get; private set; } = SimulatorLayout.DefaultDbNumber;
    public string? Warning { get; private set; }

    public override string ToString() => Name;

    public void ApplyLayout(SimulatorLayout layout)
    {
        Layout = layout;
        Signals = SimulatorPlanBuilder.Build(Project, layout);
        // Several channels can carry the same ID (e.g. disabled spares); enabled channels come first.
        _byId = Signals
            .GroupBy(signal => (signal.Kind, signal.Id))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(signal => signal.IsEnabled).ToList());
    }

    public void SaveSettings()
    {
        Settings.Constants = Layout.ToConstants();
        SimulatorSettingsStore.Save(SettingsKey, Settings);
    }

    public async Task ConnectAsync(string ip, short rack, short slot, int dbNumber)
    {
        Warning = null;
        await _client.ConnectAsync(ip, rack, slot);
        DbNumber = dbNumber;
        if (!await _client.HasLengthAsync(dbNumber, Layout.TotalBytes))
        {
            _client.Disconnect();
            throw new InvalidOperationException(
                $"DB{dbNumber} is shorter than the expected {Layout.TotalBytes} bytes ({Layout.Describe()}). " +
                "Check the DB number and the PLC constants under 'Simulator DB layout'.");
        }

        if (await _client.HasLengthAsync(dbNumber, Layout.TotalBytes + 2))
        {
            Warning = $"DB{dbNumber} is longer than {Layout.TotalBytes} bytes; check the PLC constants under 'Simulator DB layout'.";
        }

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
        _data = [];
        _output = null;
        foreach (var signal in Signals)
        {
            signal.ClearValue();
        }
    }

    /// <summary>Reads the whole simulator DB and refreshes the signal values.</summary>
    public async Task ReadAsync()
    {
        _data = await _client.ReadBytesAsync(DbNumber, 0, Layout.TotalBytes);
        _output = null;
        foreach (var signal in Signals)
        {
            signal.UpdateFrom(_data);
        }
    }

    /// <summary>Writes the values the model produced (DI, AI, COUNTER, RTD area) in one request.</summary>
    public async Task FlushAsync()
    {
        if (_output is null || !IsConnected)
        {
            return;
        }

        var length = Layout.DoStart;
        var bytes = _output.AsSpan(0, Math.Min(length, _output.Length)).ToArray();
        _output = null;
        await _client.WriteBytesAsync(DbNumber, 0, bytes);
    }

    public Task WriteBitAsync(int byteOffset, int bit, bool value) => _client.WriteBitAsync(DbNumber, byteOffset, bit, value);

    public Task WriteIntAsync(int byteOffset, short value) => _client.WriteIntAsync(DbNumber, byteOffset, value);

    public Task WriteRealAsync(int byteOffset, float value) => _client.WriteRealAsync(DbNumber, byteOffset, value);

    public IReadOnlyList<SimulatorSignal> Find(SimulatorSignalKind kind, int id) =>
        _byId.TryGetValue((kind, id), out var list) ? list : [];

    public bool TryRead(SimulatorSignalKind kind, int id, out double value)
    {
        value = 0;
        var signal = Find(kind, id).FirstOrDefault();
        if (signal is null || _data.Length == 0)
        {
            return false;
        }

        var data = _output ?? _data;
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
        if (_data.Length == 0)
        {
            return;
        }

        _output ??= (byte[])_data.Clone();
        foreach (var signal in Find(kind, id))
        {
            if (!signal.IsWritable || signal.ByteOffset >= Layout.DoStart)
            {
                continue;
            }

            if (signal.IsDigital)
            {
                var mask = (byte)(1 << signal.BitOffset);
                _output[signal.ByteOffset] = value >= 0.5 ? (byte)(_output[signal.ByteOffset] | mask) : (byte)(_output[signal.ByteOffset] & ~mask);
            }
            else if (signal.IsReal)
            {
                var bits = BitConverter.SingleToInt32Bits((float)value);
                _output[signal.ByteOffset] = (byte)(bits >> 24);
                _output[signal.ByteOffset + 1] = (byte)(bits >> 16);
                _output[signal.ByteOffset + 2] = (byte)(bits >> 8);
                _output[signal.ByteOffset + 3] = (byte)bits;
            }
            else
            {
                var raw = signal.HasScaling
                    ? signal.Scaling!.ToRaw(value)
                    : (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
                _output[signal.ByteOffset] = (byte)((ushort)raw >> 8);
                _output[signal.ByteOffset + 1] = (byte)raw;
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
