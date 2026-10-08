using System.Globalization;
using S7.Net;

namespace CwcConfigurator.Services.AlarmTesting;

public sealed class AlarmTesterS7Client : IDisposable
{
    private Plc? _plc;

    public bool IsConnected => _plc?.IsConnected == true;

    public async Task ConnectAsync(string ipAddress, short rack, short slot, CancellationToken cancellationToken)
    {
        Disconnect();
        _plc = new Plc(CpuType.S71500, ipAddress.Trim(), rack, slot)
        {
            ReadTimeout = 3000,
            WriteTimeout = 3000
        };
        await _plc.OpenAsync(cancellationToken);
    }

    public void Disconnect()
    {
        _plc?.Close();
        _plc = null;
    }

    public async Task WriteAsync(S7Address address, string value, CancellationToken cancellationToken)
    {
        var plc = EnsureConnected();
        if (address.ValueKind == S7ValueKind.Bool)
        {
            if (!TryParseBool(value, out var boolValue))
            {
                throw new InvalidOperationException($"'{value}' is not a valid BOOL value.");
            }

            await plc.WriteBitAsync(DataType.DataBlock, address.DbNumber, address.ByteOffset, address.BitOffset.GetValueOrDefault(), boolValue, cancellationToken);
            return;
        }

        if (!short.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            throw new InvalidOperationException($"'{value}' is not a valid INT value.");
        }

        await plc.WriteAsync(DataType.DataBlock, address.DbNumber, address.ByteOffset, intValue, 1, cancellationToken);
    }

    public async Task<bool> ReadBoolAsync(S7Address address, CancellationToken cancellationToken)
    {
        if (address.ValueKind != S7ValueKind.Bool)
        {
            throw new InvalidOperationException("Address is not a BOOL address.");
        }

        var plc = EnsureConnected();
        var value = await plc.ReadAsync(
            DataType.DataBlock,
            address.DbNumber,
            address.ByteOffset,
            VarType.Bit,
            1,
            (byte)address.BitOffset.GetValueOrDefault(),
            cancellationToken);
        return value is bool boolValue && boolValue;
    }

    public void Dispose()
    {
        Disconnect();
    }

    private Plc EnsureConnected()
    {
        if (_plc?.IsConnected != true)
        {
            throw new InvalidOperationException("Not connected to PLC.");
        }

        return _plc;
    }

    private static bool TryParseBool(string value, out bool parsed)
    {
        value = value.Trim();
        if (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            parsed = true;
            return true;
        }

        if (value.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            parsed = false;
            return true;
        }

        parsed = false;
        return false;
    }
}
