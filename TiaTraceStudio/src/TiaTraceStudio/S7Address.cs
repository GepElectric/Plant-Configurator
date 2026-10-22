using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TiaTraceStudio;

public enum S7Area { DataBlock, Memory, Input, Output }

/// <summary>Absolute S7 address of a non-optimized tag, e.g. %DB3.DBD268, %MW10, %I0.1.</summary>
public sealed record S7Address(S7Area Area, int Db, int Byte, int Bit, string Type)
{
    public static readonly string[] Types = ["REAL", "DINT", "UDINT", "DWORD", "INT", "UINT", "WORD", "BYTE", "BOOL"];
    static readonly Regex DbPattern = new(@"^%?DB(?<db>\d+)\.DB(?<size>[XBWD])(?<byte>\d+)(\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AreaPattern = new(@"^%?(?<area>[MIQE])(?<size>[BWD]?)(?<byte>\d+)(\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Embedded = new(@"\[(?<address>%[^\]]+)\]\s*$", RegexOptions.Compiled);

    public int Size => Type switch { "REAL" or "DINT" or "UDINT" or "DWORD" => 4, "INT" or "UINT" or "WORD" => 2, _ => 1 };

    /// <summary>Parses an address; <paramref name="type"/> overrides the type implied by the size letter (DBD defaults to REAL).</summary>
    public static S7Address Parse(string text, string? type = null)
    {
        string raw = text.Trim().Replace(" ", "");
        Match m = DbPattern.Match(raw); S7Area area = S7Area.DataBlock; int db = 0;
        if (m.Success) db = int.Parse(m.Groups["db"].Value, CultureInfo.InvariantCulture);
        else
        {
            m = AreaPattern.Match(raw);
            if (!m.Success) throw new InvalidDataException($"Neispravna S7 adresa '{text}'. Primjeri: %DB3.DBD268, %DB31.DBW10, %DB5.DBX2.1, %MW20, %I0.0.");
            area = char.ToUpperInvariant(m.Groups["area"].Value[0]) switch { 'M' => S7Area.Memory, 'Q' => S7Area.Output, _ => S7Area.Input };
        }
        string size = m.Groups["size"].Value.ToUpperInvariant();
        bool hasBit = m.Groups["bit"].Success;
        if ((size is "X" or "") != hasBit) throw new InvalidDataException($"Adresa '{text}': bit adresa mora imati oblik bajt.bit (npr. DBX2.1 ili M10.3).");
        string implied = size switch { "D" => "REAL", "W" => "INT", "B" => "BYTE", _ => "BOOL" };
        string chosen = string.IsNullOrWhiteSpace(type) ? implied : type.Trim().ToUpperInvariant();
        if (!Types.Contains(chosen)) throw new InvalidDataException($"Nepoznat tip '{type}'. Podržano: {string.Join(", ", Types)}.");
        var address = new S7Address(area, db, int.Parse(m.Groups["byte"].Value, CultureInfo.InvariantCulture), hasBit ? m.Groups["bit"].Value[0] - '0' : 0, chosen);
        int expected = size switch { "D" => 4, "W" => 2, _ => 1 };
        if (address.Size != expected) throw new InvalidDataException($"Adresa '{text}' ({expected} B) ne odgovara tipu {chosen} ({address.Size} B).");
        return address;
    }

    /// <summary>Returns the absolute address TIA appends to trace column headers, e.g. "TAG[%DB3.DBD268]".</summary>
    public static string? FromTraceHeader(string source)
    {
        var m = Embedded.Match(source.Trim().Trim('"'));
        return m.Success ? m.Groups["address"].Value : null;
    }

    public double Decode(ReadOnlySpan<byte> data) => Type switch
    {
        "REAL" => BinaryPrimitives.ReadSingleBigEndian(data),
        "DINT" => BinaryPrimitives.ReadInt32BigEndian(data),
        "UDINT" or "DWORD" => BinaryPrimitives.ReadUInt32BigEndian(data),
        "INT" => BinaryPrimitives.ReadInt16BigEndian(data),
        "UINT" or "WORD" => BinaryPrimitives.ReadUInt16BigEndian(data),
        "BYTE" => data[0],
        _ => (data[0] >> Bit) & 1
    };

    public override string ToString()
    {
        string size = Type switch { "BOOL" => "X", "BYTE" => "B", "INT" or "UINT" or "WORD" => "W", _ => "D" };
        string bit = Type == "BOOL" ? "." + Bit : "";
        return Area switch
        {
            S7Area.DataBlock => $"%DB{Db}.DB{size}{Byte}{bit}",
            _ => $"%{(Area == S7Area.Memory ? "M" : Area == S7Area.Input ? "I" : "Q")}{(Type == "BOOL" ? "" : size)}{Byte}{bit}"
        };
    }
}

/// <summary>Tag → absolute address, learned from TIA trace CSV headers or entered by the user.</summary>
public sealed class TagAddress
{
    public string Address { get; set; } = "";
    public string Type { get; set; } = "";
    public S7Address Parse() => S7Address.Parse(Address, Type);
}
