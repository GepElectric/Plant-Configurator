using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>
/// Units of DB MEAS, DB AO and DB PAR are plain text, exactly the string written to the PLC (S_UNIT / UNIT).
/// Older projects stored a code of the DB_MEAS.Unit list instead; <see cref="Migrate"/> turns those codes into the
/// text once, and <see cref="Text"/> still reads such a catalogue the old way until it is migrated.
/// </summary>
public static class UnitTexts
{
    /// <summary>The unit string for the PLC / documents.</summary>
    public static string Text(DbCatalog catalog, string? value, bool parameter = false) =>
        catalog.UnitsAreText ? (value ?? "").Trim() : Legacy(catalog, value, parameter);

    /// <summary>Old meaning: a code (or "code - text") of the DB_MEAS.Unit list; code 0 of a parameter is an empty unit.</summary>
    public static string Legacy(DbCatalog catalog, string? value, bool parameter = false)
    {
        var raw = (value ?? "").Trim();
        var separator = raw.IndexOf(" - ", StringComparison.Ordinal);
        var text = separator >= 0 ? raw[(separator + 3)..].Trim() : raw;
        var list = catalog.ValidationLists?.FirstOrDefault(setting => setting.Key == DbValidationDefaults.DbMeasUnit)
                   ?? DbValidationDefaults.Create().First(setting => setting.Key == DbValidationDefaults.DbMeasUnit);
        var match = DbValidationListSetting.SplitOptions(list.ValuesText).FirstOrDefault(option =>
                string.Equals(option.Value, raw, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.DisplayText, raw, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Text, text, StringComparison.OrdinalIgnoreCase));
        var unit = (match?.Text ?? text).Trim();
        return parameter && unit == "-" && int.TryParse(match?.Value ?? raw, out var code) && code == 0 ? "" : unit;
    }

    /// <summary>Replaces the stored unit codes by their text once; afterwards the units are edited as free text.</summary>
    public static bool Migrate(DbCatalog? catalog)
    {
        if (catalog is null || catalog.UnitsAreText)
        {
            return false;
        }

        foreach (var row in catalog.MeasRows ?? [])
        {
            row.Unit = Legacy(catalog, row.Unit);
        }

        foreach (var row in catalog.AoRows ?? [])
        {
            row.Unit = Legacy(catalog, row.Unit);
        }

        foreach (var row in catalog.ParameterRows ?? [])
        {
            row.Unit = Legacy(catalog, row.Unit, parameter: true);
        }

        catalog.UnitsAreText = true;
        return true;
    }
}
