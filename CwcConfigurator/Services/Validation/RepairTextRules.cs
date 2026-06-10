using System.Globalization;

namespace CwcConfigurator.Services.Validation;

/// <summary>
/// Predicates shared by all DB row evaluators. Centralises what "placeholder",
/// "no color" and "used" mean across tables.
/// </summary>
public static class RepairTextRules
{
    public static bool IsSpareLike(string text)
    {
        return string.IsNullOrWhiteSpace(text)
               || text.Contains("spare", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DB DI ColorValue0/ColorValue1 store full validation strings like
    /// "0 - NO COLOR", "3 - RED". Treat empty or any string containing
    /// "NO COLOR" as undefined.
    /// </summary>
    public static bool IsNoColor(string colorValue)
    {
        return string.IsNullOrWhiteSpace(colorValue)
               || colorValue.Contains("NO COLOR", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUsed(string usedValue)
    {
        var trimmed = (usedValue ?? "").Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number != 0;
        }

        return trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("YES", StringComparison.OrdinalIgnoreCase);
    }
}
