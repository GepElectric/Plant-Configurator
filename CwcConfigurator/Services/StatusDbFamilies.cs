using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>
/// DB CTRL_STS (control status, CTRL_STATUS DB6) and DB SEQ_CMD (sequence commands, SEQ_CMDs DB7): flat blocks of
/// DT_STATUS_SOLO elements like in the unit PLC. Each row is one element (ID = position, PLC name = element name);
/// NAME ENG / OTHER go to the string-name blocks, TEXT LIST NAME ENG / OTHER to the text lists. The rows are the same
/// lists the sequence uses (<see cref="SequenceCatalog.ControlStatusRows"/>, <see cref="SequenceCatalog.CommandRows"/>).
/// </summary>
public static class StatusDbFamilies
{
    public sealed record Family(
        string DbName, string Title, int Count, int ConfigNumber, string FileName, string DataBlockName, string TypeName,
        string StringFileName, string StringTypeName, string StringEnglish, string StringOther, string IndirectName, int IndirectDb,
        string TracePrefix)
    {
        public List<DbStringRow> Rows(DbCatalog catalog) => this == ControlStatus ? catalog.Sequence.ControlStatusRows : catalog.Sequence.CommandRows;
        public int LastId => Count - 1;
    }

    public static readonly Family ControlStatus = new(
        "DB CTRL_STS", "Control status", 64, 6, "CTRL_STATUS.udt", "CTRL_STATUS", "DT_CONTROL_STATUS_ALL",
        "STRINGS_CTRL_STATUS.db", "DT_CTRL_STATUS_STRING_NAMES", "CTRL_STATUS_STRING_NAMES_ENG", "CTRL_STATUS_STRING_NAMES_GER",
        "CTRL_STATUS_INDIRECT", 1006, "CTRL_STATUS");

    public static readonly Family SequenceCommand = new(
        "DB SEQ_CMD", "Sequence commands", 32, 7, "SEQ_CMDs.udt", "SEQ_CMDs", "DT_SEQUENCE_COMMAND_ALL",
        "STRINGS_SEQ_CMDs.db", "DT_SEQ_CMDs_STRING_NAMES", "SEQ_CMDs_STRING_NAMES_ENG", "SEQ_CMDs_STRING_NAMES_GER",
        "SEQ_CMDs_INDIRECT", 1007, "SEQ_CMDs");

    public static readonly Family[] All = [ControlStatus, SequenceCommand];

    /// <summary>Length of a string-name entry (String[72] as in the unit PLC).</summary>
    public const int StringLength = 72;

    public static Family? Find(string dbName) =>
        All.FirstOrDefault(family => Normalize(family.DbName).Equals(Normalize(dbName), StringComparison.OrdinalIgnoreCase));

    public static bool IsFamily(string dbName) => Find(dbName) is not null;

    static string Normalize(string name) => (name ?? "").Replace('_', ' ').Trim();

    // ------------------------------------------------------------------ defaults (from the unit PLC)

    /// <summary>Control status of the unit PLC (HPP Vihtakoski, TIA V21): PLC name and English text, by ID.</summary>
    static readonly (string Name, string Text)[] UnitControlStatus =
    [
        ("FALSE", "FALSE"), ("TRUE", "TRUE"), ("HPP_STARTED", "HPP Started"), ("HPP_STOPPED", "HPP Stopped"),
        ("NO_SHUTDOWN_ACTIVE", "No Shutdown Active"), ("NO_START_INTERLOCK_ACTIVE", "No Start Interlock Active"), ("STANDSTILL", "Standstill"),
        ("HPU_ON", "HPU ON"), ("HPU_READY", "HPU Start Completed"), ("LOU_ON", "Lubrication ON"), ("LOU_READY", "Lubrication Start Completed"),
        ("CWS_ON", "Cooling Water System ON"), ("CWS_READY", "Cooling Water System Start Completed"), ("BYPASS_OPEN", "Bypass Open"),
        ("BYPASS_CLOSED", "Bypass Closed"), ("MIV_OPEN", "MIV Open"), ("MIV_CLOSED", "MIV Closed"), ("TURBINE_ON", "Turbine Governor ON"),
        ("TURBINE_CLOSED", "Turbine Valves Closed"), ("TURBINE_READY", "Turbine Governor Start Completed"), ("EXC_ON", "Excitation ON"),
        ("EXC_READY", "Excitation Start Completed"), ("SYNC_ON", "Synchronization ON"), ("SYNC_READY", "Synchronization Start Completed"),
        ("TURBINE_ON_MIN_POWER", "Turbine Below Minimal Power"), ("REGULATION_ON", "Regulation ON"), ("TURBINE_PRESSURE_OK", "Turbine Pressure OK"),
        ("GCB_OFF", "GCB Open"), ("GCB_ON", "GCB Closed"), ("ESD_LINE_OK", "Emergency Shutdown Line OK"), ("QSD_LINE_OK", "Quick Shutdown Line OK"),
        ("SEALING_OFF", "Sealing OFF"), ("SEALING_ON", "Sealing ON"), ("BRAKE_ACTIVATION", "Brake Activation"), ("BRAKE_OFF", "Brake OFF"),
        ("MIV_PUMP_READY", "MIV HPU Start Completed"), ("SYNC_ENB", "Synchronization Enabled")
    ];

    /// <summary>Sequence commands of the unit PLC, by ID.</summary>
    static readonly (string Name, string Text)[] UnitSequenceCommands =
    [
        ("HPP", "HPP"), ("INTAKE", "Intake"), ("HPU", "HPU"), ("LIFTING", "Lifting"), ("CWS", "Cooling Water System"), ("BYPASS", "Bypass"),
        ("MIV", "MIV"), ("TURBINE", "Turbine"), ("EXC", "Excitation"), ("SYNC", "Synchronization"), ("ENABLE_GCB", "Enable GCB"),
        ("REGULATION", "Regulation"), ("SEALING", "Sealing"), ("BRAKE", "Brake")
    ];

    static string Placeholder(Family family, int id) => family == ControlStatus ? $"CTRL_STS_{id:00}" : $"SEQ_CMD_{id:00}";

    /// <summary>
    /// Fills a family still holding the placeholder rows (CTRL_STS_00…, no texts: a PLC without sequence such as the
    /// intake) with the elements of the unit PLC; elements past the unit list become SPARE_nn. Rows the user or TIA
    /// already named are kept.
    /// </summary>
    public static bool ApplyUnitDefaults(DbCatalog catalog, Family family)
    {
        var rows = family.Rows(catalog);
        var placeholder = rows.All(row =>
            (row.PlcName.Length == 0 || row.PlcName.Equals(Placeholder(family, row.Id), StringComparison.OrdinalIgnoreCase))
            && row.NameEnglish.Length == 0 && row.NameOther.Length == 0 && row.StringNameEnglish.Length == 0);
        if (!placeholder)
        {
            return false;
        }

        var defaults = family == ControlStatus ? UnitControlStatus : UnitSequenceCommands;
        foreach (var row in rows)
        {
            if (row.Id < defaults.Length)
            {
                row.PlcName = defaults[row.Id].Name;
                row.StringNameEnglish = defaults[row.Id].Text;
                row.NameEnglish = defaults[row.Id].Text;
            }
            else
            {
                row.PlcName = $"SPARE_{row.Id}";
            }
        }

        return true;
    }

    /// <summary>
    /// Project load: the data-block names of both families (also in projects made before they existed), unit
    /// defaults for unnamed lists, and the string names taken over once from the text-list names.
    /// </summary>
    public static void EnsureDefaults(DbCatalog catalog)
    {
        catalog.Sequence ??= SequenceCatalog.CreateDefault();
        catalog.Sequence.EnsureDefaults();
        catalog.DataBlockNames ??= [];
        foreach (var setting in DefaultDataBlockNames())
        {
            if (!catalog.DataBlockNames.Any(row => Normalize(row.DbName).Equals(Normalize(setting.DbName), StringComparison.OrdinalIgnoreCase)
                                                   && row.BlockKind.Equals(setting.BlockKind, StringComparison.OrdinalIgnoreCase)
                                                   && row.Number == setting.Number))
            {
                catalog.DataBlockNames.Add(setting);
            }
        }

        foreach (var family in All)
        {
            ApplyUnitDefaults(catalog, family);
        }

        if (!catalog.Sequence.StringNamesTakenFromTextLists)
        {
            foreach (var row in catalog.Sequence.ControlStatusRows.Concat(catalog.Sequence.CommandRows))
            {
                if (row.StringNameEnglish.Length == 0) row.StringNameEnglish = row.NameEnglish;
                if (row.StringNameOther.Length == 0) row.StringNameOther = row.NameOther;
            }

            catalog.Sequence.StringNamesTakenFromTextLists = true;
        }
    }

    public static IEnumerable<DbDataBlockNameSetting> DefaultDataBlockNames()
    {
        foreach (var family in All)
        {
            yield return new DbDataBlockNameSetting
            {
                Number = family.ConfigNumber, DbName = family.DbName, BlockKind = DbDataBlockNameDefaults.ConfigurationKind,
                FileName = family.FileName, DataBlockName = family.DataBlockName, DataTypeName = family.TypeName
            };
            yield return new DbDataBlockNameSetting
            {
                Number = 1, DbName = family.DbName, BlockKind = DbDataBlockNameDefaults.StringKind, FileName = family.StringFileName,
                DataBlockName = family.StringEnglish, DataTypeName = family.StringTypeName, SourceColumn = "NAME ENG", LastId = family.LastId
            };
            yield return new DbDataBlockNameSetting
            {
                Number = 2, DbName = family.DbName, BlockKind = DbDataBlockNameDefaults.StringKind, FileName = family.StringFileName,
                DataBlockName = family.StringOther, DataTypeName = family.StringTypeName, SourceColumn = "NAME OTHER", LastId = family.LastId
            };
        }
    }

    /// <summary>The configuration and string-name settings of a family (as edited in the data-block names).</summary>
    public static (DbDataBlockNameSetting Configuration, DbDataBlockNameSetting English, DbDataBlockNameSetting Other) Settings(DbCatalog catalog, Family family)
    {
        var defaults = DefaultDataBlockNames().Where(row => row.DbName == family.DbName).ToList();
        DbDataBlockNameSetting Get(string kind, int number) =>
            catalog.DataBlockNames.FirstOrDefault(row => Normalize(row.DbName).Equals(Normalize(family.DbName), StringComparison.OrdinalIgnoreCase)
                                                         && row.BlockKind.Equals(kind, StringComparison.OrdinalIgnoreCase)
                                                         && (kind == DbDataBlockNameDefaults.ConfigurationKind || row.Number == number))
            ?? defaults.First(row => row.BlockKind == kind && (kind == DbDataBlockNameDefaults.ConfigurationKind || row.Number == number));
        return (Get(DbDataBlockNameDefaults.ConfigurationKind, 0), Get(DbDataBlockNameDefaults.StringKind, 1), Get(DbDataBlockNameDefaults.StringKind, 2));
    }

    // ------------------------------------------------------------------ generation

    /// <summary>PLC names that can be generated: empty or duplicate names become SPARE_id.</summary>
    public static List<(int Id, string Name, DbStringRow Row)> Elements(DbCatalog catalog, Family family)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(int, string, DbStringRow)>();
        foreach (var row in family.Rows(catalog).OrderBy(row => row.Id))
        {
            var name = Regex.Replace(row.PlcName.Trim(), "[^A-Za-z0-9_]", "_");
            if (name.Length == 0 || !used.Add(name))
            {
                name = $"SPARE_{row.Id}";
                used.Add(name);
            }

            result.Add((row.Id, name, row));
        }

        return result;
    }

    /// <summary>Writes the type, the instance DB, the string-name type and the string-name DBs of a family.</summary>
    public static List<string> Generate(DbCatalog catalog, Family family, string dbFolder, string stringFolder, Encoding encoding)
    {
        Directory.CreateDirectory(dbFolder);
        Directory.CreateDirectory(stringFolder);
        var (configuration, english, other) = Settings(catalog, family);
        var typeName = Text(configuration.DataTypeName, family.TypeName);
        var blockName = Text(configuration.DataBlockName, family.DataBlockName);
        var files = new List<string>();

        var type = new StringBuilder();
        type.AppendLine($"TYPE \"{typeName}\"");
        type.AppendLine("VERSION : 0.1");
        type.AppendLine("   STRUCT");
        foreach (var (id, name, _) in Elements(catalog, family))
        {
            type.AppendLine(FormattableString.Invariant(
                $"      \"{name}\" {{ S7_SetPoint := 'False'}} : \"DT_STATUS_SOLO\" := ((FALSE, 1, {id}), ((), (), ()), ((), (), ()), ((), (), ()));"));
        }

        type.AppendLine("   END_STRUCT;");
        type.AppendLine();
        type.AppendLine("END_TYPE");
        files.Add(Write(Path.Combine(dbFolder, Text(configuration.FileName, family.FileName)), type.ToString(), encoding));

        var block = new StringBuilder();
        block.AppendLine($"DATA_BLOCK \"{blockName}\"");
        block.AppendLine("{ S7_Optimized_Access := 'FALSE' }");
        block.AppendLine("VERSION : 0.1");
        block.AppendLine("NON_RETAIN");
        block.AppendLine($"\"{typeName}\"");
        block.AppendLine();
        block.AppendLine("BEGIN");
        block.AppendLine();
        block.AppendLine("END_DATA_BLOCK");
        files.Add(Write(Path.Combine(dbFolder, blockName + ".db"), block.ToString(), encoding));

        var stringType = Text(english.DataTypeName, family.StringTypeName);
        var stringTypeSource = new StringBuilder();
        stringTypeSource.AppendLine($"TYPE \"{stringType}\"");
        stringTypeSource.AppendLine("VERSION : 0.1");
        stringTypeSource.AppendLine("   STRUCT");
        stringTypeSource.AppendLine(FormattableString.Invariant($"      NUM : Array[0..{family.LastId}] of String[{StringLength}];"));
        stringTypeSource.AppendLine("   END_STRUCT;");
        stringTypeSource.AppendLine();
        stringTypeSource.AppendLine("END_TYPE");
        files.Add(Write(Path.Combine(stringFolder, Path.GetFileNameWithoutExtension(Text(english.FileName, family.StringFileName)) + ".udt"), stringTypeSource.ToString(), encoding));

        var strings = new StringBuilder();
        var rows = family.Rows(catalog).ToDictionary(row => row.Id);
        foreach (var (setting, select) in new (DbDataBlockNameSetting, Func<DbStringRow, string>)[] { (english, row => row.StringNameEnglish), (other, row => row.StringNameOther) })
        {
            strings.AppendLine($"DATA_BLOCK \"{Text(setting.DataBlockName, setting == english ? family.StringEnglish : family.StringOther)}\"");
            strings.AppendLine("{ S7_Optimized_Access := 'FALSE' }");
            strings.AppendLine("VERSION : 0.1");
            strings.AppendLine("NON_RETAIN");
            strings.AppendLine($"\"{stringType}\"");
            strings.AppendLine();
            strings.AppendLine("BEGIN");
            for (var id = 0; id <= family.LastId; id++)
            {
                var value = rows.TryGetValue(id, out var row) ? select(row) : "";
                if (value.Length > StringLength) value = value[..StringLength];
                strings.AppendLine(FormattableString.Invariant($"   NUM[{id}] := '{value.Replace("'", "$'")}';"));
            }

            strings.AppendLine();
            strings.AppendLine("END_DATA_BLOCK");
            strings.AppendLine();
        }

        files.Add(Write(Path.Combine(stringFolder, Text(english.FileName, family.StringFileName)), strings.ToString(), encoding));
        return files;
    }

    /// <summary>File names <see cref="Generate"/> writes for a family.</summary>
    public static HashSet<string> GeneratedFileNames(DbCatalog catalog, Family family)
    {
        var (configuration, english, _) = Settings(catalog, family);
        var strings = Text(english.FileName, family.StringFileName);
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Text(configuration.FileName, family.FileName), Text(configuration.DataBlockName, family.DataBlockName) + ".db",
            strings, Path.GetFileNameWithoutExtension(strings) + ".udt"
        };
    }

    static string Write(string path, string text, Encoding encoding)
    {
        File.WriteAllText(path, text, encoding);
        return path;
    }

    static string Text(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    // ------------------------------------------------------------------ import (pull)

    /// <summary>Type name of the configuration DB in its exported source ("DT_CONTROL_STATUS_ALL").</summary>
    public static string? TypeOfBlock(string source)
    {
        var lines = source.Replace("\r", "").Split('\n').Select(line => line.Trim()).ToList();
        var begin = lines.FindIndex(line => line.Equals("BEGIN", StringComparison.OrdinalIgnoreCase));
        return lines.Take(begin < 0 ? lines.Count : begin).LastOrDefault(line => line.StartsWith('"') && line.EndsWith('"'))?.Trim('"');
    }

    /// <summary>
    /// Reads the element names (by position = ID) from the exported type and the texts from the string-name DBs.
    /// Returns the number of rows changed.
    /// </summary>
    public static int Import(DbCatalog catalog, Family family, IEnumerable<string> files)
    {
        var (configuration, english, other) = Settings(catalog, family);
        var rows = family.Rows(catalog).ToDictionary(row => row.Id);
        var changed = 0;
        foreach (var file in files.Where(File.Exists))
        {
            var text = File.ReadAllText(file);
            var typeName = Regex.Match(text, "^\\s*TYPE\\s+\"([^\"]+)\"", RegexOptions.Multiline);
            if (typeName.Success && typeName.Groups[1].Value.Equals(Text(configuration.DataTypeName, family.TypeName), StringComparison.OrdinalIgnoreCase))
            {
                var names = Regex.Matches(text, "^\\s*\"?([A-Za-z0-9_]+)\"?\\s*(\\{[^}]*\\})?\\s*:\\s*\"DT_STATUS_SOLO\"", RegexOptions.Multiline)
                    .Select(match => match.Groups[1].Value).ToList();
                for (var id = 0; id < names.Count && id < family.Count; id++)
                {
                    if (rows.TryGetValue(id, out var row) && row.PlcName != names[id])
                    {
                        row.PlcName = names[id];
                        changed++;
                    }
                }
            }

            foreach (Match block in Regex.Matches(text, "DATA_BLOCK\\s+\"([^\"]+)\"(.*?)END_DATA_BLOCK", RegexOptions.Singleline))
            {
                var name = block.Groups[1].Value;
                Action<DbStringRow, string>? set =
                    name.Equals(english.DataBlockName, StringComparison.OrdinalIgnoreCase) ? (row, value) => row.StringNameEnglish = value
                    : name.Equals(other.DataBlockName, StringComparison.OrdinalIgnoreCase) ? (row, value) => row.StringNameOther = value
                    : null;
                if (set is null) continue;
                foreach (Match entry in Regex.Matches(block.Groups[2].Value, "NUM\\[(\\d+)\\]\\s*:=\\s*'((?:[^']|\\$')*)'"))
                {
                    var id = int.Parse(entry.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (rows.TryGetValue(id, out var row))
                    {
                        set(row, entry.Groups[2].Value.Replace("$'", "'"));
                        changed++;
                    }
                }
            }
        }

        return changed;
    }
}
