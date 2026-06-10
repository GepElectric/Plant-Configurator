using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Validation;

public static class AoRepairEvaluator
{
    public const string TableName = "DB_AO";

    public static IEnumerable<RepairFinding> Evaluate(DbAoRow row, ValidationOptions options)
    {
        // DbAoRow has no Used field; the "Used" flag lives in TrueText
        // ("TRUE" / "") — see UI filter DbAoUsedFilter and TIA importers.
        if (!RepairTextRules.IsUsed(row.TrueText))
        {
            yield break;
        }

        if (RepairTextRules.IsSpareLike(row.NameEnglish))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Name ENG", SlicerHint: "All", FieldName: nameof(DbAoRow.NameEnglish));
        }

        if (RepairContext.IsMultilingual && RepairTextRules.IsSpareLike(row.NameOther))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Name OTHER", SlicerHint: "Other language", FieldName: nameof(DbAoRow.NameOther));
        }

        if (string.IsNullOrWhiteSpace(row.Unit))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Unit", SlicerHint: "All", FieldName: nameof(DbAoRow.Unit));
        }

        if (row.CrossReferenceValWriteCount.GetValueOrDefault() > 1)
        {
            yield return new RepairFinding(
                IssueSeverity.Error,
                $"Cross-reference VAL writes = {row.CrossReferenceValWriteCount} (must be ≤ 1)",
                SlicerHint: "XRef",
                FieldName: nameof(DbAoRow.CrossReferenceValDisplay));
        }
    }
}
