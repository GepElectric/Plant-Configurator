using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class SequenceParDbParser
{
    private static readonly Regex ReferenceDataRegex = new(
        @"^\s*(?:""(?<quotedName>[^""]+)""|(?<name>[A-Za-z_][A-Za-z0-9_]*))\.DATA\.(?<member>ID|VAL)\s*:=\s*(?<value>.*?)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReferenceInlineDataRegex = new(
        @"^\s*(?:""(?<quotedName>[^""]+)""|(?<name>[A-Za-z_][A-Za-z0-9_]*))\s*:=\s*\(.*?\bID\s*:=\s*(?<value>.*?)(?:,|\)).*?\)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReferenceDeclarationRegex = new(
        @"^\s*(?:""(?<quotedName>[^""]+)""|(?<name>[A-Za-z_][A-Za-z0-9_]*))\s*:\s*(?<type>[^;]+);",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LastStepRegex = new(
        @"PAR\.(START|NSD|QSD|ESD)\.LAST_STEP_NUMBER\s*:=\s*(\d+)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ScalarRegex = new(
        @"PAR\.(START|NSD|QSD|ESD)\.STEPS\[(\d+)\]\.(""?NAME""?|NAME_1|CONDITION_NUMBER|COMMAND_NUMBER|DURATION_CHECK|ENTER_DELAY_TIME|DURATION|EXIT_DELAY_TIME|FINAL_CONDITION)\s*:=\s*(.*?)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ArrayRegex = new(
        @"PAR\.(START|NSD|QSD|ESD)\.STEPS\[(\d+)\]\.(CTRL_STS_ID|CTRL_STS_INVERT|FIRST_CONDITION|SECOND_CONDITION|SEQ_CMD_ID|SEQ_CMD_ON_OFF)\[(\d+)\]\s*:=\s*(.*?)\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SequenceCatalog Parse(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("SEQ_PAR.db file does not exist.", filePath);
        }

        var catalog = SequenceCatalog.CreateDefault();
        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            ApplyLastStep(catalog, line);
            ApplyScalar(catalog, line);
            ApplyArray(catalog, line);
        }

        catalog.EnsureDefaults();
        RefreshReferenceCounts(catalog);
        return catalog;
    }

    public int ImportReferenceStructureNames(IList<DbStringRow> rows, string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Sequence reference DB source file does not exist.", filePath);
        }

        var orderedRows = rows
            .Where(row => row.Id >= 0)
            .OrderBy(row => row.Id)
            .ToList();
        var variableNames = ReadReferenceVariableNames(filePath);
        var imported = 0;
        foreach (var variableName in variableNames)
        {
            if (imported >= orderedRows.Count)
            {
                break;
            }

            var row = orderedRows[imported];
            if (!string.Equals(row.PlcName, variableName, StringComparison.Ordinal))
            {
                row.PlcName = variableName;
            }

            if (!string.IsNullOrWhiteSpace(row.PlcStructure))
            {
                row.PlcStructure = "";
            }

            imported++;
        }

        return imported;
    }

    private static List<string> ReadReferenceVariableNames(string filePath)
    {
        var declarationNames = new List<string>();
        var fallbackNames = new List<string>();
        var seenDeclarations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFallback = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inBeginSection = false;

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                inBeginSection = true;
                continue;
            }

            if (!inBeginSection)
            {
                var declaration = ReferenceDeclarationRegex.Match(line);
                if (declaration.Success && IsReferenceDeclarationType(declaration.Groups["type"].Value))
                {
                    var name = GetMatchedName(declaration);
                    if (!string.IsNullOrWhiteSpace(name) && seenDeclarations.Add(name))
                    {
                        declarationNames.Add(name);
                    }
                }

                continue;
            }

            var match = ReferenceDataRegex.Match(line);
            if (!match.Success)
            {
                match = ReferenceInlineDataRegex.Match(line);
            }

            if (!match.Success)
            {
                continue;
            }

            var fallbackName = GetMatchedName(match);
            if (!string.IsNullOrWhiteSpace(fallbackName) && seenFallback.Add(fallbackName))
            {
                fallbackNames.Add(fallbackName);
            }
        }

        return declarationNames.Count > 0 ? declarationNames : fallbackNames;
    }

    private static bool IsReferenceDeclarationType(string type)
    {
        return type.Contains("CTRL", StringComparison.OrdinalIgnoreCase)
               || type.Contains("STATUS", StringComparison.OrdinalIgnoreCase)
               || type.Contains("CMD", StringComparison.OrdinalIgnoreCase)
               || type.Contains("COMMAND", StringComparison.OrdinalIgnoreCase)
               || type.Contains("SEQ", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMatchedName(Match match)
    {
        return match.Groups["quotedName"].Success
            ? match.Groups["quotedName"].Value
            : match.Groups["name"].Value;
    }

    public static void RefreshReferenceCounts(SequenceCatalog catalog)
    {
        catalog.EnsureDefaults();
        foreach (var row in catalog.ControlStatusRows)
        {
            row.CrossReferenceIdCount = catalog.Sequences
                .SelectMany(sequence => sequence.VisibleSteps)
                .SelectMany(step => step.Conditions.Take(Math.Clamp(step.ConditionNumber, 0, 8)))
                .Count(slot => slot.ControlStatusId == row.Id);
            row.CrossReferenceValCount = catalog.Sequences
                .SelectMany(sequence => sequence.VisibleSteps)
                .SelectMany(step => step.Conditions.Take(Math.Clamp(step.ConditionNumber, 0, 8)))
                .Count(slot => slot.ControlStatusId == row.Id && !slot.Invert);
        }

        foreach (var row in catalog.CommandRows)
        {
            row.CrossReferenceIdCount = catalog.Sequences
                .SelectMany(sequence => sequence.VisibleSteps)
                .SelectMany(step => step.Commands.Take(Math.Clamp(step.CommandNumber, 0, 8)))
                .Count(slot => slot.CommandId == row.Id);
            row.CrossReferenceValCount = catalog.Sequences
                .SelectMany(sequence => sequence.VisibleSteps)
                .SelectMany(step => step.Commands.Take(Math.Clamp(step.CommandNumber, 0, 8)))
                .Count(slot => slot.CommandId == row.Id && slot.On);
        }
    }

    private static void ApplyLastStep(SequenceCatalog catalog, string line)
    {
        var match = LastStepRegex.Match(line);
        if (!match.Success)
        {
            return;
        }

        var sequence = catalog.GetOrCreateSequence(match.Groups[1].Value.ToUpperInvariant());
        sequence.LastStepNumber = ParseInt(match.Groups[2].Value);
    }

    private static void ApplyScalar(SequenceCatalog catalog, string line)
    {
        var match = ScalarRegex.Match(line);
        if (!match.Success)
        {
            return;
        }

        var step = GetStep(catalog, match.Groups[1].Value, ParseInt(match.Groups[2].Value));
        var field = match.Groups[3].Value.Trim('"').ToUpperInvariant();
        var value = match.Groups[4].Value.Trim();
        switch (field)
        {
            case "NAME":
                step.Name = ParseString(value);
                break;
            case "NAME_1":
                step.NameOther = ParseString(value);
                break;
            case "CONDITION_NUMBER":
                step.ConditionNumber = ParseInt(value);
                break;
            case "COMMAND_NUMBER":
                step.CommandNumber = ParseInt(value);
                break;
            case "DURATION_CHECK":
                step.DurationCheck = ParseBool(value);
                break;
            case "ENTER_DELAY_TIME":
                step.EnterDelayTime = value;
                break;
            case "DURATION":
                step.Duration = value;
                break;
            case "EXIT_DELAY_TIME":
                step.ExitDelayTime = value;
                break;
            case "FINAL_CONDITION":
                step.FinalCondition = ParseInt(value);
                break;
        }
    }

    private static void ApplyArray(SequenceCatalog catalog, string line)
    {
        var match = ArrayRegex.Match(line);
        if (!match.Success)
        {
            return;
        }

        var step = GetStep(catalog, match.Groups[1].Value, ParseInt(match.Groups[2].Value));
        var field = match.Groups[3].Value.ToUpperInvariant();
        var index = ParseInt(match.Groups[4].Value);
        var value = match.Groups[5].Value.Trim();
        switch (field)
        {
            case "CTRL_STS_ID":
                if (TryGetConditionSlot(step, index, out var controlStatusIdSlot))
                {
                    controlStatusIdSlot.ControlStatusId = ParseInt(value);
                }
                break;
            case "CTRL_STS_INVERT":
                if (TryGetConditionSlot(step, index, out var invertSlot))
                {
                    invertSlot.Invert = ParseBool(value);
                }
                break;
            case "FIRST_CONDITION":
                step.SetFirstCondition(index, ParseInt(value));
                break;
            case "SECOND_CONDITION":
                step.SetSecondCondition(index, ParseInt(value));
                break;
            case "SEQ_CMD_ID":
                if (TryGetCommandSlot(step, index, out var commandIdSlot))
                {
                    commandIdSlot.CommandId = ParseInt(value);
                }
                break;
            case "SEQ_CMD_ON_OFF":
                if (TryGetCommandSlot(step, index, out var commandOnSlot))
                {
                    commandOnSlot.On = ParseBool(value);
                }
                break;
        }
    }

    private static bool TryGetConditionSlot(SequenceStepConfig step, int index, out SequenceConditionSlot slot)
    {
        slot = step.Conditions.FirstOrDefault(item => item.Index == index)!;
        return slot is not null;
    }

    private static bool TryGetCommandSlot(SequenceStepConfig step, int index, out SequenceCommandSlot slot)
    {
        slot = step.Commands.FirstOrDefault(item => item.Index == index)!;
        return slot is not null;
    }

    private static SequenceStepConfig GetStep(SequenceCatalog catalog, string key, int stepNumber)
    {
        var sequence = catalog.GetOrCreateSequence(key.ToUpperInvariant());
        sequence.EnsureDefaults();
        var step = sequence.Steps.FirstOrDefault(item => item.StepNumber == stepNumber);
        if (step is null)
        {
            step = SequenceStepConfig.Create(stepNumber);
            sequence.Steps.Add(step);
        }

        step.EnsureDefaults();
        return step;
    }

    private static int ParseInt(string value)
    {
        var match = Regex.Match(value.Trim(), @"-?\d+");
        var numericValue = match.Success ? match.Value : value.Trim();
        return int.TryParse(numericValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static bool ParseBool(string value)
    {
        return value.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParseString(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return trimmed;
    }
}
