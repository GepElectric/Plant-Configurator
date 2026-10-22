using S7.Net;

namespace CwcConfigurator.Services.Simulation;

/// <summary>S7 (PUT/GET) access to the simulator DB of one PLC.</summary>
public sealed class SimulatorS7Client : IDisposable
{
    private Plc? _plc;

    public bool IsConnected => _plc?.IsConnected == true;

    public async Task ConnectAsync(string ipAddress, short rack, short slot, CancellationToken cancellationToken = default)
    {
        Disconnect();
        var plc = new Plc(CpuType.S71500, ipAddress.Trim(), rack, slot) { ReadTimeout = 3000, WriteTimeout = 3000 };
        await plc.OpenAsync(cancellationToken);
        _plc = plc;
    }

    public void Disconnect()
    {
        _plc?.Close();
        _plc = null;
    }

    public Task<byte[]> ReadBytesAsync(int dbNumber, int start, int count, CancellationToken cancellationToken = default)
    {
        return EnsureConnected().ReadBytesAsync(DataType.DataBlock, dbNumber, start, count, cancellationToken);
    }

    /// <summary>True when the DB has at least <paramref name="length"/> bytes (a read past its end fails).</summary>
    public async Task<bool> HasLengthAsync(int dbNumber, int length, CancellationToken cancellationToken = default)
    {
        try
        {
            await ReadBytesAsync(dbNumber, length - 1, 1, cancellationToken);
            return true;
        }
        // S7netplus reports "Address out of range" as a plain Exception; the connection stays usable.
        catch (Exception ex) when (ex is PlcException || ex.Message.Contains("out of range", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    public Task WriteBitAsync(int dbNumber, int byteOffset, int bitOffset, bool value, CancellationToken cancellationToken = default)
    {
        return EnsureConnected().WriteBitAsync(DataType.DataBlock, dbNumber, byteOffset, bitOffset, value, cancellationToken);
    }

    public Task WriteIntAsync(int dbNumber, int byteOffset, short value, CancellationToken cancellationToken = default)
    {
        var bytes = new[] { (byte)((ushort)value >> 8), (byte)value };
        return EnsureConnected().WriteBytesAsync(DataType.DataBlock, dbNumber, byteOffset, bytes, cancellationToken);
    }

    public Task WriteBytesAsync(int dbNumber, int byteOffset, byte[] bytes, CancellationToken cancellationToken = default)
    {
        return EnsureConnected().WriteBytesAsync(DataType.DataBlock, dbNumber, byteOffset, bytes, cancellationToken);
    }

    public Task WriteRealAsync(int dbNumber, int byteOffset, float value, CancellationToken cancellationToken = default)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        var bytes = new[] { (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits };
        return EnsureConnected().WriteBytesAsync(DataType.DataBlock, dbNumber, byteOffset, bytes, cancellationToken);
    }

    public void Dispose()
    {
        Disconnect();
    }

    private Plc EnsureConnected()
    {
        return _plc?.IsConnected == true ? _plc : throw new InvalidOperationException("Not connected to the PLC.");
    }
}
