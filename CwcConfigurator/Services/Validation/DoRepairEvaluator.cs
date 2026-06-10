using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Validation;

public static class DoRepairEvaluator
{
    public const string TableName = "DB_DO";

    public static IEnumerable<RepairFinding> Evaluate(DbStringRow row, ValidationOptions options)
    {
        // DB DO grid binds the "USED" column to TrueText.
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

        if (row.CrossReferenceValWriteCount.GetValueOrDefault() > 1)
        {
            yield return new RepairFinding(
                IssueSeverity.Error,
                $"Cross-reference VAL writes = {row.CrossReferenceValWriteCount} (must be ≤ 1)",
                SlicerHint: "XRef",
                FieldName: nameof(DbStringRow.CrossReferenceValDisplay));
        }
    }
}
