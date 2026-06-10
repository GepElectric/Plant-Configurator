using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Validation;

public static class MeasRepairEvaluator
{
    public const string TableName = "DB_MEAS";

    /// <summary>
    /// Alarm-slot fields that must hold distinct alarm IDs within a single MEAS row.
    /// Scaling parameters (Val1/Val2/Offset/ScaleFactor) are intentionally excluded.
    /// ERR is always treated as enabled; LL/LO/HI/HH are gated by their Limit*Enabled flag
    /// so disabled slots can't generate false-positive duplicates from leftover "0" values.
    /// </summary>
    // PropertyName is included so the validator can flag the exact cells
    // whose IDs collide. EmitFindings splits comma-separated FieldName values.
    private static readonly (string Field, Func<DbStringRow, string> Get, Func<DbStringRow, bool> IsEnabled, string PropertyName)[] AlarmIdSlots =
    [
        ("ERR", row => row.AlarmErrId, _ => true, nameof(DbStringRow.AlarmErrId)),
        ("LL",  row => row.AlarmLlId,  row => row.LimitLlEnabled, nameof(DbStringRow.AlarmLlId)),
        ("LO",  row => row.AlarmLoId,  row => row.LimitLoEnabled, nameof(DbStringRow.AlarmLoId)),
        ("HI",  row => row.AlarmHiId,  row => row.LimitHiEnabled, nameof(DbStringRow.AlarmHiId)),
        ("HH",  row => row.AlarmHhId,  row => row.LimitHhEnabled, nameof(DbStringRow.AlarmHhId))
    ];

    /// <summary>
    /// Parameter slots for alarm-limit parameters only. The scaling-parameter
    /// slots (ParVal1Id, ParVal2Id, ParOffsetId, ParScaleFactorId) are NOT
    /// included because reusing the same scaling parameter across limits is legal.
    /// Gated by the same Limit*Enabled flags as the alarm slots.
    /// </summary>
    private static readonly (string Field, Func<DbStringRow, string> Get, Func<DbStringRow, bool> IsEnabled, string PropertyName)[] AlarmLimitParameterIdSlots =
    [
        ("Par LL", row => row.ParLlId, row => row.LimitLlEnabled, nameof(DbStringRow.ParLlId)),
        ("Par LO", row => row.ParLoId, row => row.LimitLoEnabled, nameof(DbStringRow.ParLoId)),
        ("Par HI", row => row.ParHiId, row => row.LimitHiEnabled, nameof(DbStringRow.ParHiId)),
        ("Par HH", row => row.ParHhId, row => row.LimitHhEnabled, nameof(DbStringRow.ParHhId))
    ];

    public static IEnumerable<RepairFinding> Evaluate(DbStringRow row)
    {
        // DB MEAS grid binds the "USED" column to TrueText.
        if (!RepairTextRules.IsUsed(row.TrueText))
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

        foreach (var finding in DetectDuplicateIds("Alarm", AlarmIdSlots, row, slicerHint: "Alarm Numbers"))
        {
            yield return finding;
        }

        foreach (var finding in DetectDuplicateIds("Parameter", AlarmLimitParameterIdSlots, row, slicerHint: "Alarm Parameters"))
        {
            yield return finding;
        }
    }

    private static IEnumerable<RepairFinding> DetectDuplicateIds(
        string subject,
        (string Field, Func<DbStringRow, string> Get, Func<DbStringRow, bool> IsEnabled, string PropertyName)[] slots,
        DbStringRow row,
        string slicerHint)
    {
        var byId = new Dictionary<int, List<(string Field, string PropertyName)>>();
        foreach (var slot in slots)
        {
            if (!slot.IsEnabled(row))
            {
                continue;
            }

            if (!TryParseAssignedId(slot.Get(row), out var id))
            {
                continue;
            }

            if (!byId.TryGetValue(id, out var list))
            {
                list = [];
                byId[id] = list;
            }

            list.Add((slot.Field, slot.PropertyName));
        }

        foreach (var pair in byId.Where(entry => entry.Value.Count > 1))
        {
            var slotsList = string.Join(", ", pair.Value.Select(v => v.Field));
            // Comma-separate the colliding properties so each cell gets the
            // red border (EmitFindings splits the string back into a set).
            var fields = string.Join(",", pair.Value.Select(v => v.PropertyName));
            yield return new RepairFinding(
                IssueSeverity.Error,
                $"{subject} ID {pair.Key} used in multiple slots ({slotsList})",
                SlicerHint: slicerHint,
                FieldName: fields);
        }
    }

    /// <summary>
    /// Empty string and "0" both mean "no ID assigned" — neither participates in
    /// the duplicate-detection set. 0 is treated as a sentinel because spare MEAS
    /// rows have all alarm/par fields default-initialised to 0.
    /// </summary>
    private static bool TryParseAssignedId(string value, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
        {
            return false;
        }

        return id != 0;
    }
}
