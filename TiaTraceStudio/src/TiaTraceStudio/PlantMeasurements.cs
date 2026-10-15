using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TiaTraceStudio;

/// <summary>An analog measurement (MEAS, REAL), digital input (DI, BOOL) or digital output (DO, BOOL) of a Plant Configurator PLC database.</summary>
public sealed record PlantMeasurement(int Id, string System, string PlcName, string Name, string Unit, string Kind = "MEAS",
    double? RangeLow = null, double? RangeHigh = null, string RangeSource = "")
{
    /// <summary>0 % / 100 % of the measurement from its Plant Configurator parameters (ParVal1Id / ParVal2Id).</summary>
    [JsonIgnore] public bool HasRange => RangeLow is double low && RangeHigh is double high && high > low;
    [JsonIgnore] public bool IsDigital => Kind is "DI" or "DO";
    [JsonIgnore] public bool IsRegulator => Kind == "REG";
    [JsonIgnore] public string Tag => Kind switch
    {
        "DI" or "DO" => $"{Kind}.{System}.{PlcName}.DATA.VAL",
        "REG" => $"REG_INTF.{System}.VALUES.{PlcName}",
        _ => $"MEAS.{System}.{PlcName}.VAL"
    };
    [JsonIgnore] public string Display => IsRegulator ? $"REG · {System}.{PlcName} · {Name}" : $"{Kind} {Id,3} · {System}.{PlcName} · {Name}" + (Unit.Length > 0 ? $" [{Unit}]" : "");
}

/// <summary>Fixed layout of the indirect blocks, valid in every PLC (verified on PLCSIM Advanced).</summary>
public static class PlantLayout
{
    // MEAS: array of DT_MEASUREMENT_SOLO (56 B), VAL (REAL) at +44, in DB1003.
    public const int MeasDb = 1003, MeasSize = 56, MeasValue = 44;
    // DI (DB1001) and DO (DB1002): arrays of the same 22 B structure, DATA.VAL (BOOL) is bit 0 of byte 0.
    public const int DiDb = 1001, DoDb = 1002, DiSize = 22;
    static readonly System.Text.RegularExpressions.Regex IdTag = new(@"^\s*(MEAS|DI|DO)\s*#\s*(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>"DI#23" / "DO#5" / "MEAS#0": a signal referenced by its Plant Configurator ID.</summary>
    public static (string Kind, int Id)? ParseIdTag(string tag)
    {
        var m = IdTag.Match(tag);
        return m.Success ? (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value)) : null;
    }
    // REG_INTF (DB31): DT_REGULATOR_INTERFACE elements of 26 B, VALUES at +14: SCALED_REFERENCE, OUT, SCALED_MEAS (REAL).
    public const int RegDb = 31, RegSize = 26, RegValues = 14;
    static readonly (string Element, int Offset, string Name)[] RegElements =
    [
        ("WATER_LEVEL", 0, "Razina vode"), ("WATER_PRESSURE", 26, "Tlak vode"), ("ACTIVE_POWER", 52, "Djelatna snaga"), ("OPENNESS", 78, "Otvor"),
        ("SPEED", 104, "Brzina"), ("KAPLAN_DISTRIBUTOR", 130, "Kaplan privodni aparat"), ("KAPLAN_RUNNER", 156, "Kaplan lopatice rotora"),
        ("FRANCIS_DISTRIBUTOR", 182, "Francis privodni aparat"), ("CROSS_FLOW_VALVE_1", 208, "Cross Flow ventil 1"), ("CROSS_FLOW_VALVE_2", 234, "Cross Flow ventil 2"),
        .. Enumerable.Range(1, 6).Select(i => ($"PELTON_DEFLECTOR[{i}]", 260 + (i - 1) * 26, $"Pelton deflektor {i}")),
        .. Enumerable.Range(1, 6).Select(i => ($"PELTON_NOZZLE[{i}]", 416 + (i - 1) * 26, $"Pelton mlaznica {i}")),
        ("EXC_VOLTAGE", 572, "Uzbuda – napon"), ("EXC_REACTIVE_POWER", 598, "Uzbuda – jalova snaga"), ("EXC_POWER_FACTOR", 624, "Uzbuda – faktor snage")
    ];
    /// <summary>Regulator interface signals; Id is the byte offset of the value in REG_INTF.</summary>
    public static readonly IReadOnlyList<PlantMeasurement> Regulators = RegElements.SelectMany(e => new[]
    {
        new PlantMeasurement(e.Offset + RegValues, e.Element, "SCALED_REFERENCE", e.Name + " – referenca", "%", "REG"),
        new PlantMeasurement(e.Offset + RegValues + 4, e.Element, "OUT", e.Name + " – izlaz regulatora", "%", "REG"),
        new PlantMeasurement(e.Offset + RegValues + 8, e.Element, "SCALED_MEAS", e.Name + " – mjerenje", "%", "REG")
    }).ToList();
    public static PlantMeasurement? FindRegulator(string tag)
    {
        string normalized = TagMatcher.Normalize(tag);
        return Regulators.FirstOrDefault(r => string.Equals(r.Tag, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static S7Address Address(string kind, int id, int? db = null) => kind switch
    {
        "DI" => new(S7Area.DataBlock, db ?? DiDb, id * DiSize, 0, "BOOL"),
        "DO" => new(S7Area.DataBlock, db ?? DoDb, id * DiSize, 0, "BOOL"),
        "REG" => new(S7Area.DataBlock, db ?? RegDb, id, 0, "REAL"),
        _ => new(S7Area.DataBlock, db ?? MeasDb, id * MeasSize + MeasValue, 0, "REAL")
    };
}

/// <summary>
/// Measurement catalogue of one PLC database in Plant Configurator. The non-optimized MEAS block is an
/// array of DT_MEASUREMENT_SOLO structures ordered by ID, so VAL lies at DBD(ID × StructSize + ValueOffset).
/// Recording reads the indirect counterpart DB1003 (same layout; DB1003 in every PLC).
/// </summary>
/// <summary>One parameter of a controller in Plant Configurator (e.g. PID_SPEED.GAIN); Type 0 real, 1 on/off or integer, 2 time.</summary>
public sealed record ControllerParameter(string Structure, string Name, string Description, string Value, string Type, string Unit);

public sealed class PlcCatalog
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Configuration block number from Plant Configurator (DB MEAS).</summary>
    public int Db { get; set; } = 3;
    int readDb;
    /// <summary>Block read when recording: the indirect MEAS block, DB1003 in every PLC.</summary>
    public int ReadDb { get => readDb > 0 ? readDb : 1003; set => readDb = value; }
    public int StructSize { get; set; } = 56;
    public int ValueOffset { get; set; } = 44;
    public List<PlantMeasurement> Items { get; set; } = [];
    /// <summary>Controller (PID_*) parameters of the database: P/I/D action, gain, Ti, Td, dead band, output limits.</summary>
    public List<ControllerParameter> Controllers { get; set; } = [];

    int diDb;
    /// <summary>Block read for digital inputs: the indirect DI block, DB1001.</summary>
    public int DiReadDb { get => diDb > 0 ? diDb : PlantLayout.DiDb; set => diDb = value; }

    int regDb;
    /// <summary>Regulator interface block (REG_INTF), DB31.</summary>
    public int RegDb { get => regDb > 0 ? regDb : PlantLayout.RegDb; set => regDb = value; }
    /// <summary>Plant Configurator signals plus the fixed regulator interface.</summary>
    [JsonIgnore] public IEnumerable<PlantMeasurement> AllItems => Items.Concat(PlantLayout.Regulators);

    public PlantMeasurement? Find(string tag)
    {
        if (PlantLayout.ParseIdTag(tag) is var (kind, id)) return Items.FirstOrDefault(m => m.Kind == kind && m.Id == id);
        string normalized = TagMatcher.Normalize(tag);
        return Items.FirstOrDefault(m => string.Equals(m.Tag, normalized, StringComparison.OrdinalIgnoreCase)) ?? PlantLayout.FindRegulator(tag);
    }
    public S7Address AddressOf(PlantMeasurement measurement) => measurement.Kind switch
    {
        "DI" => PlantLayout.Address("DI", measurement.Id, DiReadDb),
        "DO" => PlantLayout.Address("DO", measurement.Id),
        "REG" => PlantLayout.Address("REG", measurement.Id, RegDb),
        _ => new(S7Area.DataBlock, ReadDb, measurement.Id * StructSize + ValueOffset, 0, "REAL")
    };
    public void Validate()
    {
        if (Db is < 1 or > 65535 || ReadDb is < 1 or > 65535 || StructSize is < 4 or > 4096 || ValueOffset < 0 || ValueOffset + 4 > StructSize) throw new InvalidDataException($"{Name}: neispravan raspored bloka mjerenja (DB, veličina strukture, pomak VAL).");
        if (Items.Select(i => (i.Kind, i.Id)).Distinct().Count() != Items.Count) throw new InvalidDataException($"{Name}: popis mjerenja sadrži dvostruke ID-eve.");
    }
}

/// <summary>Snapshot of the PLC databases of a Plant Configurator project, kept inside the Trace Studio project.</summary>
public sealed class PlantConfigLink
{
    public string SourceFile { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public DateTime LoadedAt { get; set; }
    public List<PlcCatalog> Plcs { get; set; } = [];
    public PlcCatalog? Get(string? id) => id == null ? null : Plcs.FirstOrDefault(p => p.Id == id);
    public void Validate() { foreach (var plc in Plcs) plc.Validate(); }

    // Unit codes of the DB_MEAS.Unit validation list in Plant Configurator.
    static readonly string[] Units = ["", "V", "A", "W", "Var", "VA", "kV", "kA", "kW", "kVAr", "kVA", "MW", "MVAr", "MVA", "Hz", "%", "°C", "bar", "mm2/s", "mm", "m", "rpm"];
    public static string UnitText(string code)
    {
        string raw = code.Trim();
        int dash = raw.IndexOf(" - ", StringComparison.Ordinal);
        if (int.TryParse(dash > 0 ? raw[..dash] : raw, out int index)) return index >= 0 && index < Units.Length ? Units[index] : "";
        return raw == "-" ? "" : raw;
    }

    static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("PlantConfigurator project file v1"));

    /// <summary>Reads every PLC database of a Plant Configurator project (.plantcfg, encrypted or plain JSON). Read-only.</summary>
    public static PlantConfigLink Load(string path)
    {
        JsonDocument document;
        using (var stream = File.OpenRead(path))
        {
            var prefix = new byte[10]; int read = stream.Read(prefix, 0, prefix.Length);
            if (read == prefix.Length && Encoding.ASCII.GetString(prefix) == "PLANTCFG1:")
            {
                using var base64 = new CryptoStream(stream, new FromBase64Transform(), CryptoStreamMode.Read);
                var iv = new byte[16]; base64.ReadExactly(iv);
                using var aes = Aes.Create(); aes.Key = Key; aes.IV = iv;
                using var plain = new CryptoStream(base64, aes.CreateDecryptor(), CryptoStreamMode.Read);
                document = JsonDocument.Parse(plain);
            }
            else { stream.Position = 0; document = JsonDocument.Parse(stream); }
        }
        using (document)
        {
            var root = document.RootElement;
            var link = new PlantConfigLink { SourceFile = path, ProjectName = Text(root, "ProjectName"), LoadedAt = DateTime.Now };
            if (TryGet(root, "PlcDatabases", out var databases) && databases.ValueKind == JsonValueKind.Array)
                foreach (var database in databases.EnumerateArray())
                    if (TryGet(database, "DbCatalog", out var catalog)) link.Plcs.Add(ReadCatalog(catalog, Text(database, "Id"), Text(database, "Name")));
            // Projects saved before multi-PLC support keep a single catalogue at the root.
            if (link.Plcs.Count == 0 && TryGet(root, "DbCatalog", out var single)) link.Plcs.Add(ReadCatalog(single, "default", "PLC"));
            link.Plcs.RemoveAll(p => p.Items.Count(i => !i.IsDigital) == 0);
            if (link.Plcs.Count == 0) throw new InvalidDataException("U projektu Plant Configuratora nema mjerenja (DbCatalog.MeasRows). Odaberi projekt Plant Configuratora.");
            link.Validate();
            return link;
        }
    }

    static PlcCatalog ReadCatalog(JsonElement catalog, string id, string name)
    {
        var plc = new PlcCatalog { Id = id.Length > 0 ? id : name, Name = name.Length > 0 ? name : "PLC" };
        if (TryGet(catalog, "DataBlockNames", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            foreach (var block in blocks.EnumerateArray())
                if (Text(block, "DbName") == "DB MEAS" && Text(block, "BlockKind") == "Configuration" && TryGet(block, "Number", out var number) && number.TryGetInt32(out int db) && db > 0) plc.Db = db;
        // Parameter values (RealValue) by ID: measurement ranges are the parameters ParVal1Id (0 %) and ParVal2Id (100 %).
        var parameters = new Dictionary<int, (double? Value, string Name)>();
        if (TryGet(catalog, "ParameterRows", out var parameterRows) && parameterRows.ValueKind == JsonValueKind.Array)
            foreach (var row in parameterRows.EnumerateArray())
                if (TryGet(row, "Id", out var pid) && pid.TryGetInt32(out int parameterId))
                    parameters[parameterId] = (double.TryParse(Text(row, "RealValue"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null, Text(row, "PlcName"));
        // Controller parameters (structures PID_*), as Plant Configurator shows them: real, on/off or time values.
        if (TryGet(catalog, "ParameterRows", out var controllerRows) && controllerRows.ValueKind == JsonValueKind.Array)
            foreach (var row in controllerRows.EnumerateArray())
            {
                string structure = Text(row, "PlcStructure");
                if (!structure.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)) continue;
                string value = Text(row, "ParameterType") switch { "1" => Text(row, "IntegerValue"), "2" => Text(row, "TimeValue"), _ => Text(row, "RealValue") };
                plc.Controllers.Add(new(structure, Text(row, "PlcName"), Text(row, "NameEnglish"), value, Text(row, "ParameterType"), UnitText(Text(row, "Unit"))));
            }
        (double? Value, string Name) Parameter(JsonElement row, string field) =>
            int.TryParse(Text(row, field), out int id) && id > 0 && parameters.TryGetValue(id, out var p) ? p : (null, "");
        if (TryGet(catalog, "MeasRows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryGet(row, "Id", out var idValue) || !idValue.TryGetInt32(out int number)) continue;
                if (Text(row, "PlcName").Length == 0 || Text(row, "PlcStructure").Length == 0) continue;
                var low = Parameter(row, "ParVal1Id"); var high = Parameter(row, "ParVal2Id");
                bool range = low.Value.HasValue && high.Value.HasValue && high.Value > low.Value;
                plc.Items.Add(new(number, Text(row, "PlcStructure"), Text(row, "PlcName"), Text(row, "NameEnglish"), UnitText(Text(row, "Unit")), "MEAS",
                    range ? low.Value : null, range ? high.Value : null, range ? string.Join(" / ", new[] { low.Name, high.Name }.Where(n => n.Length > 0)) : ""));
            }
        foreach (var (rowsName, kind) in new[] { ("DiRows", "DI"), ("DoRows", "DO") })
            if (TryGet(catalog, rowsName, out var digital) && digital.ValueKind == JsonValueKind.Array)
                foreach (var row in digital.EnumerateArray())
                {
                    if (!TryGet(row, "Id", out var idValue) || !idValue.TryGetInt32(out int number)) continue;
                    if (Text(row, "PlcName").Length == 0 || Text(row, "PlcStructure").Length == 0) continue;
                    plc.Items.Add(new(number, Text(row, "PlcStructure"), Text(row, "PlcName"), Text(row, "NameEnglish"), "", kind));
                }
        plc.Items = plc.Items.OrderBy(i => i.Kind == "MEAS" ? 0 : i.Kind == "DI" ? 1 : 2).ThenBy(i => i.Id).ToList();
        return plc;
    }

    /// <summary>Unit number of a Plant Configurator PLC database named like "Unit 2", "U2" or "Agregat 2".</summary>
    public static int? UnitNumber(string name)
    {
        var m = Regex.Match(name, @"^\s*(unit|u|agregat|ag|gen|g)\s*[-_ ]?\s*(\d+)\s*$", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[2].Value) : null;
    }

    static string Text(JsonElement element, string property) => TryGet(element, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
}

/// <summary>A PLC the app can record from, linked to a Plant Configurator PLC database and to the units it serves.</summary>
public sealed class PlcConnection : Observable
{
    string name = "", ip = "", catalogId = "", units = ""; short rack, slot = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => name; set => Set(ref name, value); }
    public string Ip { get => ip; set => Set(ref ip, value); }
    public short Rack { get => rack; set => Set(ref rack, value); }
    public short Slot { get => slot; set => Set(ref slot, value); }
    /// <summary>Id of the Plant Configurator PLC database whose names, units and addresses apply.</summary>
    public string CatalogId { get => catalogId; set => Set(ref catalogId, value); }
    /// <summary>Unit numbers served by this PLC, e.g. "1" or "1, 2"; empty for common / intake PLCs.</summary>
    public string Units { get => units; set => Set(ref units, value); }
    public int[] UnitNumbers() => Units.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out int n) ? n : -1).Where(n => n > 0).ToArray();
}
