using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Validation;

public static class DiRepairEvaluator
{
    public const string TableName = "DB_DI";

    public static IEnumerable<RepairFinding> Evaluate(DbStringRow row)
    {
        // DB DI grid binds the "USED" column to TrueText (not the Used field);
        // mirror that here so user-toggled rows actually get evaluated.
        if (!RepairTextRules.IsUsed(row.TrueText))
        {
            yield break;
        }

        if (RepairTextRules.IsSpareLike(row.SystemNameEnglish))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "System name ENG", SlicerHint: "All", FieldName: nameof(DbStringRow.SystemNameEnglish));
        }

        if (RepairContext.IsMultilingual && RepairTextRules.IsSpareLike(row.SystemNameOther))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "System name OTHER", SlicerHint: "Other language", FieldName: nameof(DbStringRow.SystemNameOther));
        }

        if (RepairTextRules.IsSpareLike(row.NameEnglish))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Name ENG", SlicerHint: "All", FieldName: nameof(DbStringRow.NameEnglish));
        }

        if (RepairContext.IsMultilingual && RepairTextRules.IsSpareLike(row.NameOther))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Name OTHER", SlicerHint: "Other language", FieldName: nameof(DbStringRow.NameOther));
        }

        if (RepairTextRules.IsNoColor(row.ColorValue0))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Color value 0 undefined", SlicerHint: "Coloring", FieldName: nameof(DbStringRow.ColorValue0));
        }

        if (RepairTextRules.IsNoColor(row.ColorValue1))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Color value 1 undefined", SlicerHint: "Coloring", FieldName: nameof(DbStringRow.ColorValue1));
        }

        if (!RepairTextRules.IsNoColor(row.ColorValue0)
            && !RepairTextRules.IsNoColor(row.ColorValue1)
            && string.Equals(row.ColorValue0, row.ColorValue1, StringComparison.OrdinalIgnoreCase))
        {
            // Both color cells are involved — comma-separated field names get
            // split by EmitFindings so both columns receive the red marker.
            yield return new RepairFinding(
                IssueSeverity.Warning,
                "Color 0 == Color 1",
                SlicerHint: "Coloring",
                FieldName: $"{nameof(DbStringRow.ColorValue0)},{nameof(DbStringRow.ColorValue1)}");
        }
    }
}
