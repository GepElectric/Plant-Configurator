using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.TiaSync;

/// <summary>
/// Structure of the controller blocks REFERENCES (DB9, references of the regulations) and REG_INTF (DB31, regulator
/// interface) from their TIA sources: element names, descriptions and units, and the absolute address of every REAL
/// value (standard / non-optimized layout), so other apps (TIA Trace Studio) can read them from the PLC.
/// </summary>
public static class ControlBlockLayout
{
    public const string References = "REFERENCES", RegulatorInterface = "REG_INTF";
    public const int ReferencesDb = 9, RegulatorInterfaceDb = 31;
    public static readonly string[] Blocks = [References, RegulatorInterface];

    public sealed class Member
    {
        public string Name { get; init; } = "";
        /// <summary>Elementary type ("Real", "Bool", "String[32]"…), a PLC type name, "Struct" or "Array".</summary>
        public string Type { get; init; } = "";
        public List<Member> Children { get; } = [];
        /// <summary>Array bounds and element type (Array[1..6] of "X").</summary>
        public int Low { get; init; }
        public int High { get; init; }
        public string ElementType { get; init; } = "";
    }

    public sealed class Source
    {
        /// <summary>PLC types by name ("DT_REFERENCE_SOLO").</summary>
        public Dictionary<string, List<Member>> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Data blocks: their structure (own STRUCT or a PLC type) and start values ("GATE_1.DATA.FINAL" → "50.0").</summary>
        public Dictionary<string, (string TypeName, List<Member> Members, Dictionary<string, string> Values)> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>PLC types used but not read yet (the next export round).</summary>
        public IEnumerable<string> MissingTypes()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(IEnumerable<Member> members)
            {
                foreach (var member in members)
                {
                    if (IsTypeReference(member.Type)) used.Add(Unquote(member.Type));
                    if (IsTypeReference(member.ElementType)) used.Add(Unquote(member.ElementType));
                    Walk(member.Children);
                }
            }

            foreach (var block in Blocks.Values)
            {
                if (block.TypeName.Length > 0) used.Add(block.TypeName);
                Walk(block.Members);
            }

            foreach (var type in Types.Values) Walk(type);
            return used.Where(name => !Types.ContainsKey(name)).ToList();
        }

        public void Read(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = StripComment(lines[index]).Trim();
                var type = Regex.Match(line, "^TYPE\\s+\"?([^\"]+)\"?", RegexOptions.IgnoreCase);
                var block = Regex.Match(line, "^DATA_BLOCK\\s+\"?([^\"]+)\"?", RegexOptions.IgnoreCase);
                if (type.Success)
                {
                    index = SkipTo(lines, index + 1, "STRUCT");
                    var members = ParseStruct(lines, ref index);
                    Types[type.Groups[1].Value.Trim()] = members;
                }
                else if (block.Success)
                {
                    string typeName = "";
                    var members = new List<Member>();
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (index++; index < lines.Length; index++)
                    {
                        var header = StripComment(lines[index]).Trim();
                        if (header.Equals("STRUCT", StringComparison.OrdinalIgnoreCase)) { index++; members = ParseStruct(lines, ref index); continue; }
                        if (header.StartsWith('"') && header.EndsWith('"')) typeName = Unquote(header);
                        if (header.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)) break;
                    }

                    for (index++; index < lines.Length; index++)
                    {
                        var assignment = StripComment(lines[index]).Trim();
                        if (assignment.StartsWith("END_DATA_BLOCK", StringComparison.OrdinalIgnoreCase)) break;
                        var match = Regex.Match(assignment, "^(.+?)\\s*:=\\s*(.*?);?$");
                        if (match.Success) values[match.Groups[1].Value.Replace("\"", "").Trim()] = match.Groups[2].Value.Trim().Trim('\'');
                    }

                    Blocks[block.Groups[1].Value.Trim()] = (typeName, members, values);
                }
            }
        }
    }

    /// <summary>One REAL value of the block at its absolute address.</summary>
    public sealed record Value(string Element, string Path, string Type, int ByteOffset, int Bit);

    /// <summary>All elementary values of a block's top-level elements with their addresses.</summary>
    public static List<Value> Layout(Source source, string blockName)
    {
        if (!source.Blocks.TryGetValue(blockName, out var block)) return [];
        var members = block.TypeName.Length > 0 && source.Types.TryGetValue(block.TypeName, out var typed) ? typed : block.Members;
        var result = new List<Value>();
        var bit = 0;
        foreach (var element in members)
        {
            var leaves = new List<(string Path, string Type, int Bit)>();
            bit = Place(source, element, bit, "", leaves);
            // An array element is an element of its own: PELTON_NOZZLE[1] · VALUES.OUT.
            result.AddRange(leaves.Select(leaf =>
            {
                var path = leaf.Path;
                var name = element.Name;
                if (path.StartsWith('['))
                {
                    var end = path.IndexOf(']');
                    name += path[..(end + 1)];
                    path = path[(end + 1)..].TrimStart('.');
                }

                return new Value(name, path, leaf.Type, leaf.Bit / 8, leaf.Bit % 8);
            }));
        }

        return result;
    }

    /// <summary>
    /// Signals for the PLC database: the REAL values of the configured elements (spares and references with
    /// CONFIGURED = FALSE left out), described by the reference NAME and S_UNIT start values.
    /// </summary>
    public static List<ControlBlockSignal> Signals(Source source)
    {
        var signals = new List<ControlBlockSignal>();
        source.Blocks.TryGetValue(References, out var references);
        var referenceValues = references.Values ?? new Dictionary<string, string>();
        string Start(string element, string path) => referenceValues.TryGetValue($"{element}.{path}", out var value) ? value : "";
        foreach (var (blockName, db) in new[] { (References, ReferencesDb), (RegulatorInterface, RegulatorInterfaceDb) })
        {
            var layout = Layout(source, blockName);
            // Recording reads the indirect block (DB + 1000): REFERENCES_INDIRECT (DB1009) is Array[0..31] of
            // DT_REFERENCE_SOLO, the element at its ID. REG_INTF has no indirect block and is read directly (DB31).
            var starts = layout.GroupBy(value => value.Element).ToDictionary(group => group.Key, group => group.Min(value => value.ByteOffset));
            var ordered = starts.Values.Order().ToList();
            var elementSize = ordered.Count > 1 ? ordered[1] - ordered[0] : 0;
            var indirect = blockName == References && elementSize > 0;
            foreach (var value in layout.Where(value => value.Type.Equals("Real", StringComparison.OrdinalIgnoreCase)))
            {
                // Spares and unnamed elements ("Element_25") are not regulations.
                if (value.Element.StartsWith("SPARE", StringComparison.OrdinalIgnoreCase) || value.Element.StartsWith("Element_", StringComparison.OrdinalIgnoreCase)) continue;
                if (Start(value.Element, "DATA.CONFIGURED").Equals("FALSE", StringComparison.OrdinalIgnoreCase)) continue;
                var offset = value.ByteOffset;
                var block = db;
                if (indirect)
                {
                    var id = int.TryParse(Start(value.Element, "ID"), out var number) ? number : (starts[value.Element] - ordered[0]) / elementSize;
                    offset = id * elementSize + value.ByteOffset - starts[value.Element];
                    block = db + 1000;
                }

                signals.Add(new ControlBlockSignal
                {
                    Block = blockName,
                    Db = block,
                    Element = value.Element,
                    Member = value.Path,
                    DataType = "Real",
                    ByteOffset = offset,
                    Description = Start(value.Element, "NAME"),
                    Unit = blockName == RegulatorInterface ? "%" : Start(value.Element, "S_UNIT")
                });
            }
        }

        return signals;
    }

    // ------------------------------------------------------------------ layout (standard memory layout)

    static int Place(Source source, Member member, int bit, string prefix, List<(string, string, int)> leaves)
    {
        var path = prefix.Length == 0 ? "" : prefix;
        var type = member.Type;
        if (type.Equals("Bool", StringComparison.OrdinalIgnoreCase))
        {
            leaves.Add((path, "Bool", bit));
            return bit + 1;
        }

        if (type.Equals("Array", StringComparison.OrdinalIgnoreCase))
        {
            bit = AlignWord(bit);
            var count = Math.Max(0, member.High - member.Low + 1);
            var element = new Member { Name = member.Name, Type = member.ElementType };
            if (member.ElementType.Equals("Bool", StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < count; i++) leaves.Add(($"{path}[{member.Low + i}]", "Bool", bit + i));
                return AlignWord(bit + count);
            }

            for (var i = 0; i < count; i++)
            {
                bit = Place(source, element, bit, $"{path}[{member.Low + i}]", leaves);
            }

            return AlignWord(bit);
        }

        var size = ElementarySize(type);
        if (size > 0)
        {
            bit = size == 1 ? AlignByte(bit) : AlignWord(bit);
            leaves.Add((path, Elementary(type), bit));
            return bit + size * 8;
        }

        // Struct or PLC type: starts and ends on an even byte.
        var children = type.Equals("Struct", StringComparison.OrdinalIgnoreCase) ? member.Children
            : source.Types.TryGetValue(Unquote(type), out var udt) ? udt
            : throw new InvalidDataException($"PLC type {type} of {member.Name} was not found.");
        bit = AlignWord(bit);
        foreach (var child in children)
        {
            bit = Place(source, child, bit, path.Length == 0 ? child.Name : $"{path}.{child.Name}", leaves);
        }

        return AlignWord(bit);
    }

    static int AlignByte(int bit) => (bit + 7) / 8 * 8;
    static int AlignWord(int bit) => (bit + 15) / 16 * 16;

    static string Elementary(string type) => Regex.Replace(type, "\\[.*$", "");

    /// <summary>Size in bytes of an elementary type, 0 for a struct / PLC type.</summary>
    static int ElementarySize(string type)
    {
        var name = Elementary(type).ToUpperInvariant();
        var length = Regex.Match(type, "\\[(\\d+)\\]");
        return name switch
        {
            "BYTE" or "CHAR" or "SINT" or "USINT" => 1,
            "INT" or "WORD" or "UINT" or "DATE" or "WCHAR" or "S5TIME" => 2,
            "DINT" or "DWORD" or "UDINT" or "REAL" or "TIME" or "TOD" or "TIME_OF_DAY" => 4,
            "LREAL" or "LINT" or "LWORD" or "ULINT" or "LTIME" or "DT" or "DATE_AND_TIME" => 8,
            "STRING" => (length.Success ? int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture) : 254) + 2,
            _ => 0
        };
    }

    // ------------------------------------------------------------------ source parsing

    static List<Member> ParseStruct(string[] lines, ref int index)
    {
        var members = new List<Member>();
        for (; index < lines.Length; index++)
        {
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("END_STRUCT", StringComparison.OrdinalIgnoreCase)) return members;
            var match = Regex.Match(line, "^\"?([^\"{:]+?)\"?\\s*(\\{[^}]*\\})?\\s*:\\s*(.+?)\\s*(:=.*)?;?$");
            if (!match.Success) continue;
            var name = match.Groups[1].Value.Trim();
            var type = match.Groups[3].Value.Trim().TrimEnd(';').Trim();
            if (type.Equals("Struct", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                var member = new Member { Name = name, Type = "Struct" };
                member.Children.AddRange(ParseStruct(lines, ref index));
                members.Add(member);
                continue;
            }

            var array = Regex.Match(type, "^Array\\s*\\[\\s*(-?\\d+)\\s*\\.\\.\\s*(-?\\d+)\\s*\\]\\s+of\\s+(.+)$", RegexOptions.IgnoreCase);
            if (array.Success)
            {
                var elementType = array.Groups[3].Value.Trim();
                var member = new Member
                {
                    Name = name, Type = "Array", ElementType = elementType,
                    Low = int.Parse(array.Groups[1].Value, CultureInfo.InvariantCulture), High = int.Parse(array.Groups[2].Value, CultureInfo.InvariantCulture)
                };
                if (elementType.Equals("Struct", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Array of Struct ({name}) is not supported.");
                }

                members.Add(member);
                continue;
            }

            members.Add(new Member { Name = name, Type = type });
        }

        return members;
    }

    static int SkipTo(string[] lines, int index, string keyword)
    {
        for (; index < lines.Length; index++)
        {
            if (StripComment(lines[index]).Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase)) return index + 1;
        }

        return index;
    }

    static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '\'') inString = !inString;
            if (!inString && line[i] == '/' && line[i + 1] == '/') return line[..i];
        }

        return line;
    }

    static bool IsTypeReference(string type) => type.StartsWith('"');
    static string Unquote(string text) => text.Trim().Trim('"');
}
