using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Validation;

public static class ParRepairEvaluator
{
    public const string TableName = "DB_PAR";

    public static IEnumerable<RepairFinding> Evaluate(DbStringRow row)
    {
        // DB PAR uses its own "InUse" flag (validation list DbParInUse) instead
        // of the generic "Used" used by DI/DO/MEAS/ALM. Mirrors UI filter.
        if (!RepairTextRules.IsUsed(row.InUse))
        {
            yield break;
        }

        if (RepairTextRules.IsSpareLike(row.NameEnglish))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Name ENG", SlicerHint: "All", FieldName: nameof(DbStringRow.NameEnglish));
        }

        if (RepairContext.IsMultilingual && RepairTextRules.IsSpareLike(row.NameOther))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Name OTHER", SlicerHint: "Other language", FieldName: nameof(DbStringRow.NameOther));
        }

        if (string.IsNullOrWhiteSpace(row.Unit))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Unit", SlicerHint: "Settings", FieldName: nameof(DbStringRow.Unit));
        }

        if (RepairTextRules.IsSpareLike(row.DescriptionEnglish))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Description ENG", SlicerHint: "Description", FieldName: nameof(DbStringRow.DescriptionEnglish));
        }

        if (RepairContext.IsMultilingual && RepairTextRules.IsSpareLike(row.DescriptionOther))
        {
            yield return new RepairFinding(IssueSeverity.Warning, "Description OTHER", SlicerHint: "Description", FieldName: nameof(DbStringRow.DescriptionOther));
        }
    }
}
