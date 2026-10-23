using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class ScadaConfigFileService
{
    private static readonly Regex SectionRegex = new(@"^/{5,}\s*(?<section>[^/]+?)\s*/{5,}\s*$", RegexOptions.Compiled);
    private static readonly Regex VariableRegex = new(
        "^\\s*let\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>\"(?:\\\\.|[^\"])*\"|-?\\d+)\\s*;\\s*(?://\\s*(?<comment>.*))?\\s*$",
        RegexOptions.Compiled);
    private static readonly Regex TagWriteRegex = new(
        "Tags\\.SysFct\\.SetTagValue\\(\\s*\"(?<tag>(?:\\\\.|[^\"])*)\"\\s*,\\s*(?<variable>[A-Za-z_][A-Za-z0-9_]*)\\s*\\)\\s*;",
        RegexOptions.Compiled);
    private static readonly Regex MainNavVariableRegex = new(
        "^(?<prefix>.*MAIN_NAV.*?)(?<index>\\d+)(?<suffix>_.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MainNavTagButtonRegex = new(
        "^(?<prefix>MAIN_NAV_BTTN_)(?<index>\\d+)(?<suffix>\\..+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MainNavTagArrayRegex = new(
        "^(?<prefix>MAIN_NAV_SCREEN_SHAPE\\[)(?<index>\\d+)(?<suffix>\\])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MainNavTagEntryRegex = new(
        "^(?<prefix>MAIN_NAV_BUTTONS\\.ENTRY_)(?<index>\\d+)(?<suffix>)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MainNavButtonsEntryVariableRegex = new(
        "^MAIN_NAV_BUTTONS_ENTRY_(?<button>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocationVariableRegex = new(
        "^(?<prefix>.*LOCATION_)(?<index>\\d+)(?<suffix>_.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocationTagRegex = new(
        "^(?<prefix>LOCATION_)(?<index>\\d+)(?<suffix>(?:\\.|_).+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderTabsVariableRegex = new(
        "^(?<prefix>HEADER_TABS_ENTRY_)(?<index>\\d+)(?<suffix>)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderTabsTagRegex = new(
        "^(?<prefix>HEADER_TABS\\.ENTRY_)(?<index>\\d+)(?<suffix>)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderTabVariableRegex = new(
        "^(?<prefix>HEADER_TAB_)(?<index>\\d+)(?<suffix>_.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderTabTagRegex = new(
        "^(?<prefix>HEADER_TAB_)(?<index>\\d+)(?<suffix>\\..+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OldScreenEntryVariableRegex = new(
        "^SCREENS_(?!Button_)(?<screen>.+)_ENTRY_(?<entry>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OldScreenEntryTagRegex = new(
        "^SCREENS_(?!Button_)(?<screen>.+)\\.ENTRY_(?<entry>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScreenButtonVariableRegex = new(
        "^SCREENS_Button_(?<button>\\d+)_ENTRY_(?<entry>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScreenButtonTagRegex = new(
        "^SCREENS_Button_(?<button>\\d+)\\.ENTRY_(?<entry>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OldTopNavCardVariableRegex = new(
        "^(?!Button_)(?<screen>.+)_TOP_NAV_CARD_(?<card>\\d+)_(?<field>TEXT_LANGUAGE_[12]|GRAPHIC_LIST_ID)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OldTopNavCardTagRegex = new(
        "^(?!Button_)(?<screen>.+)_TOP_NAV_CARD_(?<card>\\d+)\\.(?<field>TEXT_LANGUAGE_[12]|GRAPHIC_LIST_ID)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TopNavCardVariableRegex = new(
        "^Button_(?<button>\\d+)_TOP_NAV_CARD_(?<card>\\d+)_(?<field>TEXT_LANGUAGE_[12]|GRAPHIC_LIST_ID)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TopNavCardTagRegex = new(
        "^Button_(?<button>\\d+)_TOP_NAV_CARD_(?<card>\\d+)\\.(?<field>TEXT_LANGUAGE_[12]|GRAPHIC_LIST_ID)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OldPlcSlotVariableRegex = new(
        "^(?<prefix>Common|Unit_1|Unit_3|Intake)_(?<rack>CENTRAL_RACK_BASE_UNIT|DISTRIB_RACK_[1-5]_BASE_UNIT)_SLOT_(?<slot>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OldPlcSlotTagRegex = new(
        "^(?<prefix>Common|Unit 1|Unit 3|Intake)_(?<rack>CENTRAL_RACK_BASE_UNIT|DISTRIB_RACK_[1-5]_BASE_UNIT)\\.SLOT_(?<slot>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocationPlcSlotVariableRegex = new(
        "^LOCATION_(?<location>\\d+)_(?<rack>CENTRAL_RACK_BASE_UNIT|DISTRIB_RACK_[1-5]_BASE_UNIT)_SLOT_(?<slot>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocationPlcSlotTagRegex = new(
        "^LOCATION_(?<location>\\d+)_(?<rack>CENTRAL_RACK_BASE_UNIT|DISTRIB_RACK_[1-5]_BASE_UNIT)\\.SLOT_(?<slot>\\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] TopNavCardFields = ["TEXT_LANGUAGE_1", "TEXT_LANGUAGE_2", "GRAPHIC_LIST_ID"];

    public ScadaDatabase Import(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("SCADA configuration text file was not found.", path);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var database = ScadaDatabase.CreateDefault();
        database.SourceTextPath = path;
        database.LastImportedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        // The variables at the top of _SET_INTERNAL_TAG (RUNTIME_NAME, LOCATION_x_...) have no section header in
        // the SCADA script: they belong to MAIN, the section the app keeps them in (and writes first on export).
        var currentSection = "MAIN";
        var nextVariableId = 1;
        var nextTagId = 1;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var sectionMatch = SectionRegex.Match(line);
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups["section"].Value.Trim();
                continue;
            }

            var variableMatch = VariableRegex.Match(line);
            if (variableMatch.Success)
            {
                var rawValue = variableMatch.Groups["value"].Value.Trim();
                var isString = rawValue.StartsWith("\"", StringComparison.Ordinal);
                database.Variables.Add(new ScadaConfigVariable
                {
                    Id = nextVariableId++,
                    Section = currentSection,
                    Name = variableMatch.Groups["name"].Value.Trim(),
                    Type = isString ? "STRING" : "INT",
                    Value = isString ? Unquote(rawValue) : rawValue,
                    Comment = variableMatch.Groups["comment"].Success ? variableMatch.Groups["comment"].Value.Trim() : "",
                    SourceLine = index + 1
                });
            }

            var tagMatch = TagWriteRegex.Match(line);
            if (tagMatch.Success)
            {
                database.TagWrites.Add(new ScadaTagWrite
                {
                    Id = nextTagId++,
                    Section = currentSection,
                    Tag = tagMatch.Groups["tag"].Value.Trim(),
                    VariableName = tagMatch.Groups["variable"].Value.Trim(),
                    SourceLine = index + 1
                });
            }
        }

        NormalizeIndexedGroups(database);
        ApplyTagsToVariables(database);
        database.EnsureDefaults();
        return database;
    }

    public void Export(ScadaDatabase database, string path)
    {
        if (database is null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        database.EnsureDefaults();
        NormalizeIndexedGroups(database);
        ApplyTagsToVariables(database);

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var builder = new StringBuilder();
        foreach (var section in GetSectionOrder(database))
        {
            builder.AppendLine(BuildSectionHeader(section));
            foreach (var variable in database.Variables.Where(row => row.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).OrderBy(row => GetVariableSortOrder(database, row)).ThenBy(row => row.Id))
            {
                builder.Append("let ");
                builder.Append(variable.Name.Trim());
                builder.Append(" = ");
                builder.Append(FormatValue(variable));
                builder.Append(';');
                if (!string.IsNullOrWhiteSpace(variable.Comment))
                {
                    builder.Append("  //  ");
                    builder.Append(variable.Comment.Trim());
                }

                builder.AppendLine();
            }

            builder.AppendLine();
        }

        builder.AppendLine(BuildSectionHeader("TAG WRITES"));
        foreach (var tagWrite in BuildTagWrites(database))
        {
            builder.Append("Tags.SysFct.SetTagValue(\"");
            builder.Append(EscapeString(tagWrite.Tag));
            builder.Append("\",");
            builder.Append(tagWrite.VariableName.Trim());
            builder.AppendLine(");");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        database.LastGeneratedPath = path;
    }

    public static void ApplyTagsToVariables(ScadaDatabase database)
    {
        NormalizeIndexedGroups(database);
        var tagByVariable = database.TagWrites
            .Where(row => !string.IsNullOrWhiteSpace(row.VariableName) && !string.IsNullOrWhiteSpace(row.Tag))
            .GroupBy(row => row.VariableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(", ", group.Select(row => row.Tag).Distinct(StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

        foreach (var variable in database.Variables)
        {
            variable.Tag = tagByVariable.TryGetValue(variable.Name, out var tag) ? tag : "";
        }

        UpdateTagWriteDisplayValues(database, database.TagWrites);
    }

    public static void NormalizeIndexedGroups(ScadaDatabase database)
    {
        NormalizeMainNavVariables(database);
        NormalizeMainNavTagWrites(database);
        NormalizeLocationVariables(database);
        NormalizeLocationTagWrites(database);
        NormalizePlcSlotLocations(database);
        NormalizeHeaderTabsVariables(database);
        NormalizeHeaderTabsTagWrites(database);
        NormalizeHeaderTabVariables(database);
        NormalizeHeaderTabTagWrites(database);
        NormalizeScreenButtonEntries(database);
        NormalizeTopNavCards(database);
        EnsureLocationSourceEntryTags(database);
        EnsurePlcSlotLocationMatrix(database);
        EnsureScadaGeneralGeneratedTags(database);
        database.EnsureDefaults();
    }

    public static void NormalizeMainNavGroups(ScadaDatabase database)
    {
        NormalizeIndexedGroups(database);
    }

    public static List<ScadaSectionSummary> BuildSummary(ScadaDatabase database)
    {
        database.EnsureDefaults();
        var tagWriteCounts = database.TagWrites
            .GroupBy(row => row.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return database.Variables
            .GroupBy(row => row.Section, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ScadaSectionSummary
            {
                Section = group.Key,
                Variables = group.Count(),
                IntValues = group.Count(row => row.Type.Equals("INT", StringComparison.OrdinalIgnoreCase)),
                StringValues = group.Count(row => row.Type.Equals("STRING", StringComparison.OrdinalIgnoreCase)),
                TagWrites = tagWriteCounts.GetValueOrDefault(group.Key)
            })
            .OrderBy(row => row.Section, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<ScadaTagWrite> BuildOrderedTagWrites(ScadaDatabase database)
    {
        return BuildTagWrites(database).ToList();
    }

    public static void ApplyTagWriteValuesToVariables(ScadaDatabase database)
    {
        database.EnsureDefaults();
        foreach (var tagWrite in database.TagWrites)
        {
            var variable = database.Variables.FirstOrDefault(row => row.Name.Equals(tagWrite.VariableName, StringComparison.OrdinalIgnoreCase));
            if (variable is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tagWrite.Type))
            {
                variable.Type = ScadaDatabase.NormalizeType(tagWrite.Type, tagWrite.Value);
            }

            variable.Value = variable.Type.Equals("INT", StringComparison.OrdinalIgnoreCase)
                ? NormalizeIntValue(tagWrite.Value)
                : tagWrite.Value ?? "";
        }
    }

    private static IReadOnlyList<ScadaTagWrite> BuildTagWrites(ScadaDatabase database)
    {
        NormalizeIndexedGroups(database);
        var result = database.TagWrites
            .Where(row => !string.IsNullOrWhiteSpace(row.VariableName) && !string.IsNullOrWhiteSpace(row.Tag))
            .OrderBy(row => GetTagWriteSortOrder(database, row))
            .ThenBy(row => row.Id)
            .ToList();
        UpdateTagWriteDisplayValues(database, result);

        var knownVariables = result
            .Select(row => row.VariableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in database.Variables.Where(row => !string.IsNullOrWhiteSpace(row.Tag) && !knownVariables.Contains(row.Name)).OrderBy(row => row.Id))
        {
            foreach (var tag in variable.Tag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.Add(new ScadaTagWrite
                {
                    Id = result.Count + 1,
                    Section = variable.Section,
                    Tag = tag,
                    VariableName = variable.Name,
                    LinkedVariable = variable
                });
            }
        }

        return result;
    }

    private static void UpdateTagWriteDisplayValues(ScadaDatabase database, IEnumerable<ScadaTagWrite> tagWrites)
    {
        var variablesByName = database.Variables.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var tagWrite in tagWrites)
        {
            if (!variablesByName.TryGetValue(tagWrite.VariableName, out var variable))
            {
                tagWrite.LinkedVariable = null;
                tagWrite.Type = "";
                tagWrite.Value = "";
                continue;
            }

            tagWrite.LinkedVariable = variable;
        }
    }

    private static string NormalizeIntValue(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private static double GetTagWriteSortOrder(ScadaDatabase database, ScadaTagWrite tagWrite)
    {
        var variable = database.Variables.FirstOrDefault(row => row.Name.Equals(tagWrite.VariableName, StringComparison.OrdinalIgnoreCase));
        if (variable is null)
        {
            return tagWrite.Id;
        }

        var groupMatch = MainNavVariableRegex.Match(variable.Name);
        if (!groupMatch.Success || !IsMainNavTagWrite(tagWrite))
        {
            var plcSlotMatch = LocationPlcSlotVariableRegex.Match(variable.Name);
            if (plcSlotMatch.Success && IsLocationPlcSlotTagWrite(tagWrite))
            {
                return GetPlcSlotSortOrder(database, plcSlotMatch);
            }

            groupMatch = LocationVariableRegex.Match(variable.Name);
            if (!groupMatch.Success || !IsLocationTagWrite(tagWrite))
            {
                groupMatch = HeaderTabsVariableRegex.Match(variable.Name);
                if (!groupMatch.Success || !IsHeaderTabsTagWrite(tagWrite))
                {
                    groupMatch = HeaderTabVariableRegex.Match(variable.Name);
                    if (!groupMatch.Success || !IsHeaderTabTagWrite(tagWrite))
                    {
                        var screenButtonMatch = ScreenButtonVariableRegex.Match(variable.Name);
                        if (screenButtonMatch.Success && IsScreenButtonTagWrite(tagWrite))
                        {
                            return GetScreenButtonSortOrder(database, screenButtonMatch);
                        }

                        var topNavCardMatch = TopNavCardVariableRegex.Match(variable.Name);
                        if (topNavCardMatch.Success && IsTopNavCardTagWrite(tagWrite))
                        {
                            return GetTopNavCardSortOrder(database, topNavCardMatch);
                        }

                        return tagWrite.Id;
                    }

                    return GetIndexedSortOrder(database, groupMatch, HeaderTabVariableRegex, indexOffset: 0);
                }

                return GetIndexedSortOrder(database, groupMatch, HeaderTabsVariableRegex, indexOffset: 0);
            }

            return GetIndexedSortOrder(database, groupMatch, LocationVariableRegex, indexOffset: 1);
        }

        return GetIndexedSortOrder(database, groupMatch, MainNavVariableRegex, indexOffset: 0);
    }

    private static double GetVariableSortOrder(ScadaDatabase database, ScadaConfigVariable variable)
    {
        var mainNavMatch = MainNavVariableRegex.Match(variable.Name);
        if (mainNavMatch.Success)
        {
            var groupKey = BuildIndexedGroupKey(mainNavMatch);
            var groupRows = database.Variables
                .Select(row => new { Row = row, Match = MainNavVariableRegex.Match(row.Name) })
                .Where(item => item.Match.Success && BuildIndexedGroupKey(item.Match).Equals(groupKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var anchor = GetAnchorRow(groupRows.Select(item => item.Row));
            var index = int.Parse(mainNavMatch.Groups["index"].Value, CultureInfo.InvariantCulture);
            return anchor.Id + index / 1000.0;
        }

        var locationMatch = LocationVariableRegex.Match(variable.Name);
        if (locationMatch.Success)
        {
            var plcSlotMatch = LocationPlcSlotVariableRegex.Match(variable.Name);
            if (plcSlotMatch.Success)
            {
                return GetPlcSlotSortOrder(database, plcSlotMatch);
            }

            var groupKey = BuildIndexedGroupKey(locationMatch);
            var groupRows = database.Variables
                .Select(row => new { Row = row, Match = LocationVariableRegex.Match(row.Name) })
                .Where(item => item.Match.Success && BuildIndexedGroupKey(item.Match).Equals(groupKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var anchor = GetAnchorRow(groupRows.Select(item => item.Row));
            var index = int.Parse(locationMatch.Groups["index"].Value, CultureInfo.InvariantCulture);
            return anchor.Id + (index + 1) / 1000.0;
        }

        var headerTabsMatch = HeaderTabsVariableRegex.Match(variable.Name);
        if (headerTabsMatch.Success)
        {
            return GetIndexedSortOrder(database, headerTabsMatch, HeaderTabsVariableRegex, indexOffset: 0);
        }

        var headerTabMatch = HeaderTabVariableRegex.Match(variable.Name);
        if (headerTabMatch.Success)
        {
            return GetIndexedSortOrder(database, headerTabMatch, HeaderTabVariableRegex, indexOffset: 0);
        }

        var screenButtonMatch = ScreenButtonVariableRegex.Match(variable.Name);
        if (screenButtonMatch.Success)
        {
            return GetScreenButtonSortOrder(database, screenButtonMatch);
        }

        var topNavCardMatch = TopNavCardVariableRegex.Match(variable.Name);
        if (topNavCardMatch.Success)
        {
            return GetTopNavCardSortOrder(database, topNavCardMatch);
        }

        return variable.Id;
    }

    private static double GetScreenButtonSortOrder(ScadaDatabase database, Match match)
    {
        var groupRows = database.Variables
            .Where(row => ScreenButtonVariableRegex.IsMatch(row.Name))
            .ToList();
        var anchor = GetAnchorRow(groupRows);
        var button = int.Parse(match.Groups["button"].Value, CultureInfo.InvariantCulture);
        var entry = int.Parse(match.Groups["entry"].Value, CultureInfo.InvariantCulture);
        return anchor.Id + button / 1000.0 + entry / 1000000.0;
    }

    private static double GetTopNavCardSortOrder(ScadaDatabase database, Match match)
    {
        var groupRows = database.Variables
            .Where(row => TopNavCardVariableRegex.IsMatch(row.Name))
            .ToList();
        var anchor = GetAnchorRow(groupRows);
        var button = int.Parse(match.Groups["button"].Value, CultureInfo.InvariantCulture);
        var card = int.Parse(match.Groups["card"].Value, CultureInfo.InvariantCulture);
        var field = match.Groups["field"].Value.ToUpperInvariant();
        var fieldOffset = Array.IndexOf(TopNavCardFields, field);
        return anchor.Id + button / 1000.0 + card / 1000000.0 + Math.Max(0, fieldOffset) / 1000000000.0;
    }

    private static double GetPlcSlotSortOrder(ScadaDatabase database, Match match)
    {
        var groupRows = database.Variables
            .Where(row => LocationPlcSlotVariableRegex.IsMatch(row.Name))
            .ToList();
        var anchor = GetAnchorRow(groupRows);
        var location = int.Parse(match.Groups["location"].Value, CultureInfo.InvariantCulture);
        var rack = match.Groups["rack"].Value;
        var slot = int.Parse(match.Groups["slot"].Value, CultureInfo.InvariantCulture);
        var rackOffset = rack.Equals("CENTRAL_RACK_BASE_UNIT", StringComparison.OrdinalIgnoreCase)
            ? 0
            : int.Parse(Regex.Match(rack, @"DISTRIB_RACK_(\d+)_BASE_UNIT", RegexOptions.IgnoreCase).Groups[1].Value, CultureInfo.InvariantCulture);
        return anchor.Id + location / 1000.0 + rackOffset / 1000000.0 + slot / 1000000000.0;
    }

    private static double GetIndexedSortOrder(ScadaDatabase database, Match match, Regex regex, int indexOffset)
    {
        var groupKey = BuildIndexedGroupKey(match);
        var groupRows = database.Variables
            .Select(row => new { Row = row, Match = regex.Match(row.Name) })
            .Where(item => item.Match.Success && BuildIndexedGroupKey(item.Match).Equals(groupKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var anchor = GetAnchorRow(groupRows.Select(item => item.Row));
        var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
        return anchor.Id + (index + indexOffset) / 1000.0;
    }

    private static ScadaConfigVariable GetAnchorRow(IEnumerable<ScadaConfigVariable> groupRows)
    {
        var rows = groupRows.ToList();
        return rows
            .Where(item => item.SourceLine > 0)
            .OrderBy(item => item.SourceLine)
            .ThenBy(item => item.Id)
            .FirstOrDefault()
            ?? rows.OrderBy(item => item.Id).First();
    }

    private static void NormalizeMainNavVariables(ScadaDatabase database)
    {
        var nextId = database.Variables.Count == 0 ? 1 : database.Variables.Max(row => row.Id) + 1;
        var groups = database.Variables
            .Select(row => new { Row = row, Match = MainNavVariableRegex.Match(row.Name) })
            .Where(item => item.Match.Success)
            .GroupBy(item => BuildMainNavVariableGroupKey(item.Match), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var template = group.OrderBy(item => item.Row.Id).First().Row;
            var byIndex = group
                .GroupBy(item => int.Parse(item.Match.Groups["index"].Value, CultureInfo.InvariantCulture))
                .ToDictionary(item => item.Key, item => item.OrderBy(x => x.Row.Id).First().Row);

            for (var index = 1; index <= 16; index++)
            {
                if (byIndex.ContainsKey(index))
                {
                    continue;
                }

                var name = BuildMainNavVariableName(group.First().Match, index);
                database.Variables.Add(new ScadaConfigVariable
                {
                    Id = nextId++,
                    Section = template.Section,
                    Name = name,
                    Type = template.Type,
                    Value = template.Type.Equals("STRING", StringComparison.OrdinalIgnoreCase) ? "" : "0",
                    Comment = template.Comment,
                    SourceLine = 0
                });
            }
        }
    }

    private static void NormalizeMainNavTagWrites(ScadaDatabase database)
    {
        var nextId = database.TagWrites.Count == 0 ? 1 : database.TagWrites.Max(row => row.Id) + 1;
        var variableNames = database.Variables
            .Select(row => row.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var template in database.TagWrites.Where(IsMainNavTagWrite).OrderBy(row => row.Id).ToList())
        {
            for (var index = 1; index <= 16; index++)
            {
                var variableName = BuildMainNavIndexedName(template.VariableName, index);
                var tag = BuildMainNavIndexedTag(template.Tag, index);
                if (string.IsNullOrWhiteSpace(variableName)
                    || string.IsNullOrWhiteSpace(tag)
                    || !variableNames.Contains(variableName)
                    || database.TagWrites.Any(row => row.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
                                                     && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                database.TagWrites.Add(new ScadaTagWrite
                {
                    Id = nextId++,
                    Section = template.Section,
                    Tag = tag,
                    VariableName = variableName,
                    SourceLine = 0
                });
            }
        }
    }

    private static void NormalizeLocationVariables(ScadaDatabase database)
    {
        var nextId = database.Variables.Count == 0 ? 1 : database.Variables.Max(row => row.Id) + 1;
        var groups = database.Variables
            .Select(row => new { Row = row, Match = LocationVariableRegex.Match(row.Name) })
            .Where(item => item.Match.Success && !LocationPlcSlotVariableRegex.IsMatch(item.Row.Name))
            .GroupBy(item => BuildIndexedGroupKey(item.Match), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var template = group.OrderBy(item => item.Row.Id).First().Row;
            var byIndex = group
                .GroupBy(item => int.Parse(item.Match.Groups["index"].Value, CultureInfo.InvariantCulture))
                .ToDictionary(item => item.Key, item => item.OrderBy(x => x.Row.Id).First().Row);

            for (var index = 0; index <= 10; index++)
            {
                if (byIndex.ContainsKey(index))
                {
                    continue;
                }

                database.Variables.Add(new ScadaConfigVariable
                {
                    Id = nextId++,
                    Section = template.Section,
                    Name = BuildIndexedName(group.First().Match, index),
                    Type = template.Type,
                    Value = template.Type.Equals("STRING", StringComparison.OrdinalIgnoreCase) ? "" : "0",
                    Comment = template.Comment,
                    SourceLine = 0
                });
            }
        }
    }

    private static void NormalizeLocationTagWrites(ScadaDatabase database)
    {
        var nextId = database.TagWrites.Count == 0 ? 1 : database.TagWrites.Max(row => row.Id) + 1;
        var variableNames = database.Variables
            .Select(row => row.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var template in database.TagWrites.Where(row => IsLocationTagWrite(row) && !IsLocationPlcSlotTagWrite(row)).OrderBy(row => row.Id).ToList())
        {
            for (var index = 0; index <= 10; index++)
            {
                var variableName = BuildLocationIndexedName(template.VariableName, index);
                var tag = BuildLocationIndexedTag(template.Tag, index);
                if (string.IsNullOrWhiteSpace(variableName)
                    || string.IsNullOrWhiteSpace(tag)
                    || !variableNames.Contains(variableName)
                    || database.TagWrites.Any(row => row.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
                                                     && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                database.TagWrites.Add(new ScadaTagWrite
                {
                    Id = nextId++,
                    Section = template.Section,
                    Tag = tag,
                    VariableName = variableName,
                    SourceLine = 0
                });
            }
        }
    }

    private static void NormalizeHeaderTabsVariables(ScadaDatabase database)
    {
        NormalizeIndexedVariables(database, HeaderTabsVariableRegex, 1, 10);
    }

    private static void NormalizeHeaderTabsTagWrites(ScadaDatabase database)
    {
        NormalizeIndexedTagWrites(database, IsHeaderTabsTagWrite, BuildHeaderTabsIndexedName, BuildHeaderTabsIndexedTag, 1, 10);
    }

    private static void NormalizeHeaderTabVariables(ScadaDatabase database)
    {
        NormalizeIndexedVariables(database, HeaderTabVariableRegex, 1, 10);
    }

    private static void NormalizeHeaderTabTagWrites(ScadaDatabase database)
    {
        NormalizeIndexedTagWrites(database, IsHeaderTabTagWrite, BuildHeaderTabIndexedName, BuildHeaderTabIndexedTag, 1, 10);
    }

    private static void NormalizeScreenButtonEntries(ScadaDatabase database)
    {
        var screenButtonMap = BuildScreenButtonMap(database);
        RenameOldScreenVariables(database, screenButtonMap);
        RenameOldScreenTagWrites(database, screenButtonMap);
        CleanupOldScreenTags(database, screenButtonMap);
        EnsureScreenButtonMatrix(database);
    }

    private static void NormalizeTopNavCards(ScadaDatabase database)
    {
        var screenButtonMap = BuildScreenButtonMap(database);
        RenameOldTopNavCardVariables(database, screenButtonMap);
        RenameOldTopNavCardTagWrites(database, screenButtonMap);
        CleanupOldTopNavCardTags(database, screenButtonMap);
        EnsureTopNavCardMatrix(database);
    }

    private static void NormalizePlcSlotLocations(ScadaDatabase database)
    {
        foreach (var variable in database.Variables.ToList())
        {
            var match = OldPlcSlotVariableRegex.Match(variable.Name);
            if (!match.Success || !TryMapOldPlcPrefix(match.Groups["prefix"].Value, out var location))
            {
                continue;
            }

            variable.Name = BuildLocationPlcSlotVariableName(location, match.Groups["rack"].Value, int.Parse(match.Groups["slot"].Value, CultureInfo.InvariantCulture));
            variable.Section = "PLC";
            variable.Type = "INT";
        }

        foreach (var tagWrite in database.TagWrites.ToList())
        {
            var variableMatch = OldPlcSlotVariableRegex.Match(tagWrite.VariableName);
            if (variableMatch.Success && TryMapOldPlcPrefix(variableMatch.Groups["prefix"].Value, out var variableLocation))
            {
                tagWrite.VariableName = BuildLocationPlcSlotVariableName(variableLocation, variableMatch.Groups["rack"].Value, int.Parse(variableMatch.Groups["slot"].Value, CultureInfo.InvariantCulture));
            }

            var tagMatch = OldPlcSlotTagRegex.Match(tagWrite.Tag);
            if (tagMatch.Success && TryMapOldPlcPrefix(tagMatch.Groups["prefix"].Value, out var tagLocation))
            {
                tagWrite.Tag = BuildLocationPlcSlotTag(tagLocation, tagMatch.Groups["rack"].Value, int.Parse(tagMatch.Groups["slot"].Value, CultureInfo.InvariantCulture));
                tagWrite.Section = "PLC";
            }
        }
    }

    private static bool TryMapOldPlcPrefix(string prefix, out int location)
    {
        location = prefix.Trim().Replace('_', ' ').ToUpperInvariant() switch
        {
            "COMMON" => 0,
            "UNIT 1" => 1,
            "UNIT 3" => 2,
            "INTAKE" => 3,
            _ => -1
        };
        return location >= 0;
    }

    private static bool IsMainNavTagWrite(ScadaTagWrite row)
    {
        return row.VariableName.Contains("MAIN_NAV", StringComparison.OrdinalIgnoreCase)
               && (MainNavTagButtonRegex.IsMatch(row.Tag)
                   || MainNavTagArrayRegex.IsMatch(row.Tag)
                   || MainNavTagEntryRegex.IsMatch(row.Tag));
    }

    private static bool IsLocationTagWrite(ScadaTagWrite row)
    {
        return LocationVariableRegex.IsMatch(row.VariableName) && LocationTagRegex.IsMatch(row.Tag);
    }

    private static bool IsHeaderTabsTagWrite(ScadaTagWrite row)
    {
        return HeaderTabsVariableRegex.IsMatch(row.VariableName) && HeaderTabsTagRegex.IsMatch(row.Tag);
    }

    private static bool IsHeaderTabTagWrite(ScadaTagWrite row)
    {
        return HeaderTabVariableRegex.IsMatch(row.VariableName) && HeaderTabTagRegex.IsMatch(row.Tag);
    }

    private static bool IsScreenButtonTagWrite(ScadaTagWrite row)
    {
        return ScreenButtonVariableRegex.IsMatch(row.VariableName) && ScreenButtonTagRegex.IsMatch(row.Tag);
    }

    private static bool IsTopNavCardTagWrite(ScadaTagWrite row)
    {
        return TopNavCardVariableRegex.IsMatch(row.VariableName) && TopNavCardTagRegex.IsMatch(row.Tag);
    }

    private static bool IsLocationPlcSlotTagWrite(ScadaTagWrite row)
    {
        return LocationPlcSlotVariableRegex.IsMatch(row.VariableName) && LocationPlcSlotTagRegex.IsMatch(row.Tag);
    }

    private static void EnsureScadaGeneralGeneratedTags(ScadaDatabase database)
    {
        EnsureGeneratedVariableAndTag(
            database,
            "MAIN_NAV_BUTTONS_NUM",
            "INT",
            CountMainNavButtons(database).ToString(CultureInfo.InvariantCulture),
            "MAIN",
            "Number of configured main navigation buttons");

        for (var button = 1; button <= 16; button++)
        {
            var buttonText = button.ToString(CultureInfo.InvariantCulture);
            EnsureGeneratedVariableAndTag(
                database,
                $"BUTTON_{buttonText}_SCREEN_NUM",
                "INT",
                CountButtonScreens(database, button).ToString(CultureInfo.InvariantCulture),
                "MAIN",
                "Number of configured screens for this main navigation button");
            EnsureGeneratedVariableAndTag(
                database,
                $"SIDE_CONTENT_DEFAULT_SCREENS_ENTRY_{buttonText}",
                $"SIDE_CONTENT_DEFAULT_SCREENS.ENTRY_{buttonText}",
                "STRING",
                "",
                "SIDE_CONTENT",
                "Default side content screen");
        }
    }

    private static void EnsureLocationSourceEntryTags(ScadaDatabase database)
    {
        for (var location = 0; location <= 10; location++)
        {
            var text = location.ToString(CultureInfo.InvariantCulture);
            EnsureGeneratedVariableAndTag(
                database,
                $"LOCATION_{text}_SOURCE",
                $"LOCATION_{text}.SOURCE",
                "INT",
                "0",
                "MAIN",
                "Location source");
            EnsureGeneratedVariableAndTag(
                database,
                $"LOCATION_{text}_ENTRY",
                $"LOCATION_{text}.ENTRY",
                "STRING",
                "",
                "MAIN",
                "Location entry");
        }
    }

    private static void EnsurePlcSlotLocationMatrix(ScadaDatabase database)
    {
        for (var location = 0; location <= 9; location++)
        {
            for (var slot = 2; slot <= 30; slot++)
            {
                EnsureGeneratedVariableAndTag(
                    database,
                    BuildLocationPlcSlotVariableName(location, "CENTRAL_RACK_BASE_UNIT", slot),
                    BuildLocationPlcSlotTag(location, "CENTRAL_RACK_BASE_UNIT", slot),
                    "INT",
                    "0",
                    "PLC",
                    "0 - GREY MODULE, 1 - WHITE MODULE");
            }

            for (var rack = 1; rack <= 5; rack++)
            {
                var rackName = $"DISTRIB_RACK_{rack.ToString(CultureInfo.InvariantCulture)}_BASE_UNIT";
                for (var slot = 1; slot <= 15; slot++)
                {
                    EnsureGeneratedVariableAndTag(
                        database,
                        BuildLocationPlcSlotVariableName(location, rackName, slot),
                        BuildLocationPlcSlotTag(location, rackName, slot),
                        "INT",
                        "0",
                        "PLC",
                        "0 - GREY MODULE, 1 - WHITE MODULE");
                }
            }
        }
    }

    private static void EnsureGeneratedVariableAndTag(ScadaDatabase database, string name, string type, string defaultValue, string section, string comment)
    {
        EnsureGeneratedVariableAndTag(database, name, name, type, defaultValue, section, comment);
    }

    private static void EnsureGeneratedVariableAndTag(ScadaDatabase database, string name, string tag, string type, string defaultValue, string section, string comment)
    {
        var variable = database.Variables.FirstOrDefault(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (variable is null)
        {
            database.Variables.Add(new ScadaConfigVariable
            {
                Id = database.Variables.Count == 0 ? 1 : database.Variables.Max(row => row.Id) + 1,
                Section = section,
                Name = name,
                Type = type,
                Value = defaultValue,
                Comment = comment,
                SourceLine = 0
            });
        }
        else
        {
            // Never turn a text that is in the script into a number (it would be written as 0).
            if (!(type == "INT" && variable.Type == "STRING" && !int.TryParse(variable.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                variable.Type = type;
            }

            if (string.IsNullOrWhiteSpace(variable.Section))
            {
                variable.Section = section;
            }

            if (string.IsNullOrWhiteSpace(variable.Value))
            {
                variable.Value = defaultValue;
            }

            if (string.IsNullOrWhiteSpace(variable.Comment))
            {
                variable.Comment = comment;
            }
        }

        if (database.TagWrites.Any(row => row.VariableName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                          && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        database.TagWrites.Add(new ScadaTagWrite
        {
            Id = database.TagWrites.Count == 0 ? 1 : database.TagWrites.Max(row => row.Id) + 1,
            Section = section,
            Tag = tag,
            VariableName = name,
            SourceLine = 0
        });
    }

    private static int CountMainNavButtons(ScadaDatabase database)
    {
        return database.Variables.Count(row => MainNavButtonsEntryVariableRegex.IsMatch(row.Name) && !string.IsNullOrWhiteSpace(row.Value));
    }

    private static int CountButtonScreens(ScadaDatabase database, int button)
    {
        return database.Variables
            .Select(row => new { Row = row, Match = ScreenButtonVariableRegex.Match(row.Name) })
            .Count(item => item.Match.Success
                           && int.Parse(item.Match.Groups["button"].Value, CultureInfo.InvariantCulture) == button
                           && !string.IsNullOrWhiteSpace(item.Row.Value));
    }

    private static Dictionary<string, int> BuildScreenButtonMap(ScadaDatabase database)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in database.Variables
                     .Select(row => new { Row = row, Match = MainNavButtonsEntryVariableRegex.Match(row.Name) })
                     .Where(item => item.Match.Success)
                     .OrderBy(item => item.Row.Id))
        {
            var button = int.Parse(variable.Match.Groups["button"].Value, CultureInfo.InvariantCulture);
            if (button < 1 || button > 16 || string.IsNullOrWhiteSpace(variable.Row.Value))
            {
                continue;
            }

            map[NormalizeScreenKey(variable.Row.Value)] = button;
        }

        var usedButtons = map.Values.ToHashSet();
        var oldScreens = database.Variables
            .Select(row => new { Row = row, Match = OldScreenEntryVariableRegex.Match(row.Name) })
            .Where(item => item.Match.Success)
            .Select(item => new
            {
                Screen = item.Match.Groups["screen"].Value,
                Sort = item.Row.SourceLine > 0 ? item.Row.SourceLine : item.Row.Id
            })
            .Concat(database.TagWrites
                .Select(row => new { Row = row, Match = OldScreenEntryTagRegex.Match(row.Tag) })
                .Where(item => item.Match.Success)
                .Select(item => new
                {
                    Screen = item.Match.Groups["screen"].Value,
                    Sort = item.Row.SourceLine > 0 ? item.Row.SourceLine : item.Row.Id
                }))
            .Concat(database.Variables
                .Select(row => new { Row = row, Match = OldTopNavCardVariableRegex.Match(row.Name) })
                .Where(item => item.Match.Success)
                .Select(item => new
                {
                    Screen = item.Match.Groups["screen"].Value,
                    Sort = item.Row.SourceLine > 0 ? item.Row.SourceLine : item.Row.Id
                }))
            .Concat(database.TagWrites
                .Select(row => new { Row = row, Match = OldTopNavCardTagRegex.Match(row.Tag) })
                .Where(item => item.Match.Success)
                .Select(item => new
                {
                    Screen = item.Match.Groups["screen"].Value,
                    Sort = item.Row.SourceLine > 0 ? item.Row.SourceLine : item.Row.Id
                }))
            .GroupBy(item => NormalizeScreenKey(item.Screen), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(item => item.Sort))
            .ToList();

        foreach (var group in oldScreens)
        {
            if (map.ContainsKey(group.Key))
            {
                continue;
            }

            var nextButton = Enumerable.Range(1, 16).FirstOrDefault(button => !usedButtons.Contains(button));
            if (nextButton == 0)
            {
                continue;
            }

            map[group.Key] = nextButton;
            usedButtons.Add(nextButton);
        }

        return map;
    }

    private static void RenameOldScreenVariables(ScadaDatabase database, Dictionary<string, int> screenButtonMap)
    {
        foreach (var variable in database.Variables.ToList())
        {
            var match = OldScreenEntryVariableRegex.Match(variable.Name);
            if (!match.Success)
            {
                continue;
            }

            if (!TryBuildScreenButtonNames(screenButtonMap, match.Groups["screen"].Value, match.Groups["entry"].Value, out var variableName, out _))
            {
                continue;
            }

            var existing = database.Variables.FirstOrDefault(row => !ReferenceEquals(row, variable)
                                                                    && row.Name.Equals(variableName, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                variable.Name = variableName;
                continue;
            }

            MergeScreenVariable(existing, variable);
            database.Variables.Remove(variable);
        }
    }

    private static void RenameOldScreenTagWrites(ScadaDatabase database, Dictionary<string, int> screenButtonMap)
    {
        foreach (var tagWrite in database.TagWrites.ToList())
        {
            var tagMatch = OldScreenEntryTagRegex.Match(tagWrite.Tag);
            var variableMatch = OldScreenEntryVariableRegex.Match(tagWrite.VariableName);
            if (!tagMatch.Success && !variableMatch.Success)
            {
                continue;
            }

            var screen = tagMatch.Success ? tagMatch.Groups["screen"].Value : variableMatch.Groups["screen"].Value;
            var entry = tagMatch.Success ? tagMatch.Groups["entry"].Value : variableMatch.Groups["entry"].Value;
            if (!TryBuildScreenButtonNames(screenButtonMap, screen, entry, out var variableName, out var tag))
            {
                continue;
            }

            var existing = database.TagWrites.FirstOrDefault(row => !ReferenceEquals(row, tagWrite)
                                                                    && row.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
                                                                    && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                database.TagWrites.Remove(tagWrite);
                continue;
            }

            tagWrite.VariableName = variableName;
            tagWrite.Tag = tag;
        }
    }

    private static void EnsureScreenButtonMatrix(ScadaDatabase database)
    {
        var templateVariable = database.Variables
            .Where(row => ScreenButtonVariableRegex.IsMatch(row.Name))
            .OrderBy(row => row.Id)
            .FirstOrDefault();
        if (templateVariable is null)
        {
            return;
        }

        var nextVariableId = database.Variables.Count == 0 ? 1 : database.Variables.Max(row => row.Id) + 1;
        for (var button = 1; button <= 16; button++)
        {
            for (var entry = 1; entry <= 16; entry++)
            {
                var variableName = BuildScreenButtonVariableName(button, entry);
                if (database.Variables.Any(row => row.Name.Equals(variableName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                database.Variables.Add(new ScadaConfigVariable
                {
                    Id = nextVariableId++,
                    Section = templateVariable.Section,
                    Name = variableName,
                    Type = "STRING",
                    Value = "",
                    Comment = templateVariable.Comment,
                    SourceLine = 0
                });
            }
        }

        var templateTagWrite = database.TagWrites
            .Where(row => ScreenButtonTagRegex.IsMatch(row.Tag))
            .OrderBy(row => row.Id)
            .FirstOrDefault();
        var tagSection = templateTagWrite?.Section ?? templateVariable.Section;
        var nextTagId = database.TagWrites.Count == 0 ? 1 : database.TagWrites.Max(row => row.Id) + 1;
        for (var button = 1; button <= 16; button++)
        {
            for (var entry = 1; entry <= 16; entry++)
            {
                var variableName = BuildScreenButtonVariableName(button, entry);
                var tag = BuildScreenButtonTag(button, entry);
                if (database.TagWrites.Any(row => row.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
                                                 && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                database.TagWrites.Add(new ScadaTagWrite
                {
                    Id = nextTagId++,
                    Section = tagSection,
                    Tag = tag,
                    VariableName = variableName,
                    SourceLine = 0
                });
            }
        }
    }

    private static void CleanupOldScreenTags(ScadaDatabase database, Dictionary<string, int> screenButtonMap)
    {
        foreach (var variable in database.Variables)
        {
            var tags = variable.Tag
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => ConvertOldScreenTag(tag, screenButtonMap))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var buttonMatch = ScreenButtonVariableRegex.Match(variable.Name);
            if (buttonMatch.Success)
            {
                var button = int.Parse(buttonMatch.Groups["button"].Value, CultureInfo.InvariantCulture);
                var entry = int.Parse(buttonMatch.Groups["entry"].Value, CultureInfo.InvariantCulture);
                var expectedTag = BuildScreenButtonTag(button, entry);
                if (!tags.Contains(expectedTag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Insert(0, expectedTag);
                }
            }

            variable.Tag = string.Join(", ", tags);
        }
    }

    private static string ConvertOldScreenTag(string tag, Dictionary<string, int> screenButtonMap)
    {
        var match = OldScreenEntryTagRegex.Match(tag);
        if (!match.Success)
        {
            return tag.StartsWith("SCREENS_", StringComparison.OrdinalIgnoreCase)
                && !ScreenButtonTagRegex.IsMatch(tag)
                    ? ""
                    : tag;
        }

        return TryBuildScreenButtonNames(screenButtonMap, match.Groups["screen"].Value, match.Groups["entry"].Value, out _, out var newTag)
            ? newTag
            : "";
    }

    private static void RenameOldTopNavCardVariables(ScadaDatabase database, Dictionary<string, int> screenButtonMap)
    {
        foreach (var variable in database.Variables.ToList())
        {
            var match = OldTopNavCardVariableRegex.Match(variable.Name);
            if (!match.Success)
            {
                continue;
            }

            if (!TryBuildTopNavCardNames(screenButtonMap, match.Groups["screen"].Value, match.Groups["card"].Value, match.Groups["field"].Value, out var variableName, out _))
            {
                continue;
            }

            var existing = database.Variables.FirstOrDefault(row => !ReferenceEquals(row, variable)
                                                                    && row.Name.Equals(variableName, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                variable.Name = variableName;
                variable.Type = GetTopNavCardFieldType(match.Groups["field"].Value);
                continue;
            }

            MergeScreenVariable(existing, variable);
            database.Variables.Remove(variable);
        }
    }

    private static void RenameOldTopNavCardTagWrites(ScadaDatabase database, Dictionary<string, int> screenButtonMap)
    {
        foreach (var tagWrite in database.TagWrites.ToList())
        {
            var tagMatch = OldTopNavCardTagRegex.Match(tagWrite.Tag);
            var variableMatch = OldTopNavCardVariableRegex.Match(tagWrite.VariableName);
            if (!tagMatch.Success && !variableMatch.Success)
            {
                continue;
            }

            var screen = tagMatch.Success ? tagMatch.Groups["screen"].Value : variableMatch.Groups["screen"].Value;
            var card = tagMatch.Success ? tagMatch.Groups["card"].Value : variableMatch.Groups["card"].Value;
            var field = tagMatch.Success ? tagMatch.Groups["field"].Value : variableMatch.Groups["field"].Value;
            if (!TryBuildTopNavCardNames(screenButtonMap, screen, card, field, out var variableName, out var tag))
            {
                continue;
            }

            var existing = database.TagWrites.FirstOrDefault(row => !ReferenceEquals(row, tagWrite)
                                                                    && row.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
                                                                    && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                database.TagWrites.Remove(tagWrite);
                continue;
            }

            tagWrite.VariableName = variableName;
            tagWrite.Tag = tag;
        }
    }

    private static void CleanupOldTopNavCardTags(ScadaDatabase database, Dictionary<string, int> screenButtonMap)
    {
        foreach (var variable in database.Variables)
        {
            var tags = variable.Tag
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => ConvertOldTopNavCardTag(tag, screenButtonMap))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var match = TopNavCardVariableRegex.Match(variable.Name);
            if (match.Success)
            {
                var button = int.Parse(match.Groups["button"].Value, CultureInfo.InvariantCulture);
                var card = int.Parse(match.Groups["card"].Value, CultureInfo.InvariantCulture);
                var field = match.Groups["field"].Value;
                var expectedTag = BuildTopNavCardTag(button, card, field);
                if (!tags.Contains(expectedTag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Insert(0, expectedTag);
                }
            }

            variable.Tag = string.Join(", ", tags);
        }
    }

    private static void EnsureTopNavCardMatrix(ScadaDatabase database)
    {
        var templateVariable = database.Variables
            .Where(row => TopNavCardVariableRegex.IsMatch(row.Name))
            .OrderBy(row => row.Id)
            .FirstOrDefault();
        if (templateVariable is null)
        {
            return;
        }

        var nextVariableId = database.Variables.Count == 0 ? 1 : database.Variables.Max(row => row.Id) + 1;
        for (var button = 1; button <= 16; button++)
        {
            for (var card = 1; card <= 16; card++)
            {
                foreach (var field in TopNavCardFields)
                {
                    var variableName = BuildTopNavCardVariableName(button, card, field);
                    if (database.Variables.Any(row => row.Name.Equals(variableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var type = GetTopNavCardFieldType(field);
                    database.Variables.Add(new ScadaConfigVariable
                    {
                        Id = nextVariableId++,
                        Section = templateVariable.Section,
                        Name = variableName,
                        Type = type,
                        Value = type.Equals("STRING", StringComparison.OrdinalIgnoreCase) ? "" : "0",
                        Comment = templateVariable.Comment,
                        SourceLine = 0
                    });
                }
            }
        }

        var templateTagWrite = database.TagWrites
            .Where(row => TopNavCardTagRegex.IsMatch(row.Tag))
            .OrderBy(row => row.Id)
            .FirstOrDefault();
        var tagSection = templateTagWrite?.Section ?? templateVariable.Section;
        var nextTagId = database.TagWrites.Count == 0 ? 1 : database.TagWrites.Max(row => row.Id) + 1;
        for (var button = 1; button <= 16; button++)
        {
            for (var card = 1; card <= 16; card++)
            {
                foreach (var field in TopNavCardFields)
                {
                    var variableName = BuildTopNavCardVariableName(button, card, field);
                    var tag = BuildTopNavCardTag(button, card, field);
                    if (database.TagWrites.Any(row => row.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
                                                     && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    database.TagWrites.Add(new ScadaTagWrite
                    {
                        Id = nextTagId++,
                        Section = tagSection,
                        Tag = tag,
                        VariableName = variableName,
                        SourceLine = 0
                    });
                }
            }
        }
    }

    private static bool TryBuildTopNavCardNames(Dictionary<string, int> screenButtonMap, string screen, string cardText, string field, out string variableName, out string tag)
    {
        variableName = "";
        tag = "";
        if (!screenButtonMap.TryGetValue(NormalizeScreenKey(screen), out var button)
            || button < 1
            || button > 16
            || !int.TryParse(cardText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var card)
            || card < 1
            || card > 16
            || !TopNavCardFields.Contains(field, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        variableName = BuildTopNavCardVariableName(button, card, field);
        tag = BuildTopNavCardTag(button, card, field);
        return true;
    }

    private static string ConvertOldTopNavCardTag(string tag, Dictionary<string, int> screenButtonMap)
    {
        var match = OldTopNavCardTagRegex.Match(tag);
        if (!match.Success)
        {
            return tag.Contains("_TOP_NAV_CARD_", StringComparison.OrdinalIgnoreCase)
                && !TopNavCardTagRegex.IsMatch(tag)
                    ? ""
                    : tag;
        }

        return TryBuildTopNavCardNames(screenButtonMap, match.Groups["screen"].Value, match.Groups["card"].Value, match.Groups["field"].Value, out _, out var newTag)
            ? newTag
            : "";
    }

    private static string BuildTopNavCardVariableName(int button, int card, string field)
    {
        return $"Button_{button.ToString(CultureInfo.InvariantCulture)}_TOP_NAV_CARD_{card.ToString(CultureInfo.InvariantCulture)}_{field.ToUpperInvariant()}";
    }

    private static string BuildTopNavCardTag(int button, int card, string field)
    {
        return $"Button_{button.ToString(CultureInfo.InvariantCulture)}_TOP_NAV_CARD_{card.ToString(CultureInfo.InvariantCulture)}.{field.ToUpperInvariant()}";
    }

    private static string GetTopNavCardFieldType(string field)
    {
        return field.Equals("GRAPHIC_LIST_ID", StringComparison.OrdinalIgnoreCase) ? "INT" : "STRING";
    }

    private static bool TryBuildScreenButtonNames(Dictionary<string, int> screenButtonMap, string screen, string entryText, out string variableName, out string tag)
    {
        variableName = "";
        tag = "";
        if (!screenButtonMap.TryGetValue(NormalizeScreenKey(screen), out var button)
            || button < 1
            || button > 16
            || !int.TryParse(entryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var entry)
            || entry < 1
            || entry > 16)
        {
            return false;
        }

        variableName = BuildScreenButtonVariableName(button, entry);
        tag = BuildScreenButtonTag(button, entry);
        return true;
    }

    private static void MergeScreenVariable(ScadaConfigVariable target, ScadaConfigVariable source)
    {
        if (string.IsNullOrWhiteSpace(target.Value) && !string.IsNullOrWhiteSpace(source.Value))
        {
            target.Value = source.Value;
        }

        if (string.IsNullOrWhiteSpace(target.Comment) && !string.IsNullOrWhiteSpace(source.Comment))
        {
            target.Comment = source.Comment;
        }

        if (string.IsNullOrWhiteSpace(target.Section) && !string.IsNullOrWhiteSpace(source.Section))
        {
            target.Section = source.Section;
        }

        if (target.SourceLine <= 0 || (source.SourceLine > 0 && source.SourceLine < target.SourceLine))
        {
            target.SourceLine = source.SourceLine;
        }
    }

    private static string BuildScreenButtonVariableName(int button, int entry)
    {
        return $"SCREENS_Button_{button.ToString(CultureInfo.InvariantCulture)}_ENTRY_{entry.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildScreenButtonTag(int button, int entry)
    {
        return $"SCREENS_Button_{button.ToString(CultureInfo.InvariantCulture)}.ENTRY_{entry.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeScreenKey(string value)
    {
        return string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    private static string BuildMainNavVariableGroupKey(Match match)
    {
        return BuildIndexedGroupKey(match);
    }

    private static string BuildMainNavVariableName(Match match, int index)
    {
        return BuildIndexedName(match, index);
    }

    private static string BuildMainNavIndexedName(string variableName, int index)
    {
        var match = MainNavVariableRegex.Match(variableName);
        return match.Success ? BuildMainNavVariableName(match, index) : "";
    }

    private static string BuildMainNavIndexedTag(string tag, int index)
    {
        foreach (var regex in new[] { MainNavTagButtonRegex, MainNavTagArrayRegex, MainNavTagEntryRegex })
        {
            var match = regex.Match(tag);
            if (match.Success)
            {
                return match.Groups["prefix"].Value + index.ToString(CultureInfo.InvariantCulture) + match.Groups["suffix"].Value;
            }
        }

        return "";
    }

    private static string BuildIndexedGroupKey(Match match)
    {
        return match.Groups["prefix"].Value + "#" + match.Groups["suffix"].Value;
    }

    private static string BuildIndexedName(Match match, int index)
    {
        return match.Groups["prefix"].Value + index.ToString(CultureInfo.InvariantCulture) + match.Groups["suffix"].Value;
    }

    private static string BuildLocationIndexedName(string variableName, int index)
    {
        var match = LocationVariableRegex.Match(variableName);
        return match.Success ? BuildIndexedName(match, index) : "";
    }

    private static string BuildLocationIndexedTag(string tag, int index)
    {
        var match = LocationTagRegex.Match(tag);
        return match.Success
            ? match.Groups["prefix"].Value + index.ToString(CultureInfo.InvariantCulture) + match.Groups["suffix"].Value
            : "";
    }

    private static string BuildLocationPlcSlotVariableName(int location, string rack, int slot)
    {
        return $"LOCATION_{location.ToString(CultureInfo.InvariantCulture)}_{rack.ToUpperInvariant()}_SLOT_{slot.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildLocationPlcSlotTag(int location, string rack, int slot)
    {
        return $"LOCATION_{location.ToString(CultureInfo.InvariantCulture)}_{rack.ToUpperInvariant()}.SLOT_{slot.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildHeaderTabsIndexedName(string variableName, int index)
    {
        var match = HeaderTabsVariableRegex.Match(variableName);
        return match.Success ? BuildIndexedName(match, index) : "";
    }

    private static string BuildHeaderTabsIndexedTag(string tag, int index)
    {
        var match = HeaderTabsTagRegex.Match(tag);
        return match.Success
            ? match.Groups["prefix"].Value + index.ToString(CultureInfo.InvariantCulture) + match.Groups["suffix"].Value
            : "";
    }

    private static string BuildHeaderTabIndexedName(string variableName, int index)
    {
        var match = HeaderTabVariableRegex.Match(variableName);
        return match.Success ? BuildIndexedName(match, index) : "";
    }

    private static string BuildHeaderTabIndexedTag(string tag, int index)
    {
        var match = HeaderTabTagRegex.Match(tag);
        return match.Success
            ? match.Groups["prefix"].Value + index.ToString(CultureInfo.InvariantCulture) + match.Groups["suffix"].Value
            : "";
    }

    private static void NormalizeIndexedVariables(ScadaDatabase database, Regex regex, int firstIndex, int lastIndex)
    {
        var nextId = database.Variables.Count == 0 ? 1 : database.Variables.Max(row => row.Id) + 1;
        var groups = database.Variables
            .Select(row => new { Row = row, Match = regex.Match(row.Name) })
            .Where(item => item.Match.Success)
            .GroupBy(item => BuildIndexedGroupKey(item.Match), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var template = group.OrderBy(item => item.Row.Id).First().Row;
            var byIndex = group
                .GroupBy(item => int.Parse(item.Match.Groups["index"].Value, CultureInfo.InvariantCulture))
                .ToDictionary(item => item.Key, item => item.OrderBy(x => x.Row.Id).First().Row);

            for (var index = firstIndex; index <= lastIndex; index++)
            {
                if (byIndex.ContainsKey(index))
                {
                    continue;
                }

                database.Variables.Add(new ScadaConfigVariable
                {
                    Id = nextId++,
                    Section = template.Section,
                    Name = BuildIndexedName(group.First().Match, index),
                    Type = template.Type,
                    Value = template.Type.Equals("STRING", StringComparison.OrdinalIgnoreCase) ? "" : "0",
                    Comment = template.Comment,
                    SourceLine = 0
                });
            }
        }
    }

    private static void NormalizeIndexedTagWrites(
        ScadaDatabase database,
        Func<ScadaTagWrite, bool> isMatchingTagWrite,
        Func<string, int, string> buildVariableName,
        Func<string, int, string> buildTag,
        int firstIndex,
        int lastIndex)
    {
        var nextId = database.TagWrites.Count == 0 ? 1 : database.TagWrites.Max(row => row.Id) + 1;
        var variableNames = database.Variables
            .Select(row => row.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var template in database.TagWrites.Where(isMatchingTagWrite).OrderBy(row => row.Id).ToList())
        {
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                var variableName = buildVariableName(template.VariableName, index);
                var tag = buildTag(template.Tag, index);
                if (string.IsNullOrWhiteSpace(variableName)
                    || string.IsNullOrWhiteSpace(tag)
                    || !variableNames.Contains(variableName)
                    || database.TagWrites.Any(row => row.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase)
                                                     && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                database.TagWrites.Add(new ScadaTagWrite
                {
                    Id = nextId++,
                    Section = template.Section,
                    Tag = tag,
                    VariableName = variableName,
                    SourceLine = 0
                });
            }
        }
    }

    private static IReadOnlyList<string> GetSectionOrder(ScadaDatabase database)
    {
        return database.Variables
            .Select(row => row.Section)
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildSectionHeader(string section)
    {
        return $"///////////////////////////////////////// {section} /////////////////////////////////////////";
    }

    private static string FormatValue(ScadaConfigVariable variable)
    {
        if (variable.Type.Equals("STRING", StringComparison.OrdinalIgnoreCase))
        {
            return "\"" + EscapeString(variable.Value) + "\"";
        }

        return int.TryParse(variable.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private static string Unquote(string value)
    {
        var inner = value.Length >= 2 ? value[1..^1] : value;
        return inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string EscapeString(string value)
    {
        return (value ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
