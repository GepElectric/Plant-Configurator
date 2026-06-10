using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Validation;

/// <summary>
/// Phase 2 evaluator for DB ALM rows. Wraps the existing
/// <see cref="DbRepairEvaluator"/> rules (still used by UI computed
/// properties) and adds the xref-TEMP E rule.
/// </summary>
public static class AlmRepairEvaluator
{
    public const string TableName = "DB_ALM";

    public static IEnumerable<RepairFinding> Evaluate(DbStringRow row, ValidationOptions options)
    {
        if (!RepairTextRules.IsUsed(row.Used))
        {
            yield break;
        }

        foreach (var reason in DbRepairEvaluator.GetAlarmRepairReasons(row))
        {
            yield return new RepairFinding(
                IssueSeverity.Warning,
                reason,
                SlicerHint: SlicerHintFor(reason),
                FieldName: FieldNameFor(reason));
        }

        if (row.CrossReferenceTempWriteCount.GetValueOrDefault() > 1)
        {
            yield return new RepairFinding(
                IssueSeverity.Error,
                $"Cross-reference TEMP writes = {row.CrossReferenceTempWriteCount} (must be ≤ 1)",
                SlicerHint: "XRef",
                FieldName: nameof(DbStringRow.CrossReferenceTempDisplay));
        }
    }

    private static string SlicerHintFor(string reason)
    {
        // Match the strings emitted by DbRepairEvaluator.GetAlarmRepairReasons.
        if (reason.IndexOf("other", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Other language";
        }
        if (reason.StartsWith("Description", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("Template", StringComparison.OrdinalIgnoreCase))
        {
            return "Description";
        }
        return "All";
    }

    /// <summary>
    /// Maps a repair reason string back to the DbStringRow property it concerns,
    /// so the Problems-pane can flash the offending cell — not just the row.
    /// </summary>
    private static string? FieldNameFor(string reason)
    {
        var lower = reason.ToLowerInvariant();
        if (lower.Contains("system") && lower.Contains("other")) return nameof(DbStringRow.SystemNameOther);
        if (lower.Contains("system")) return nameof(DbStringRow.SystemNameEnglish);
        if (lower.Contains("name") && lower.Contains("other")) return nameof(DbStringRow.NameOther);
        if (lower.Contains("name")) return nameof(DbStringRow.NameEnglish);
        if (lower.Contains("description") && lower.Contains("other")) return nameof(DbStringRow.DescriptionOther);
        if (lower.Contains("description")) return nameof(DbStringRow.DescriptionEnglish);
        if (lower.Contains("template")) return nameof(DbStringRow.DescriptionTemplateKey);
        return null;
    }
}
