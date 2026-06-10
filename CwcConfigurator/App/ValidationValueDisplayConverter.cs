using System.Globalization;
using System.Windows.Data;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public sealed class ValidationValueDisplayConverter : IValueConverter
{
    private static Dictionary<string, List<DbValidationOption>> _optionsByKey = new(StringComparer.OrdinalIgnoreCase);

    public static void SetOptions(IEnumerable<DbValidationListSetting> settings)
    {
        _optionsByKey = settings
            .Where(setting => !string.IsNullOrWhiteSpace(setting.Key))
            .ToDictionary(
                setting => setting.Key.Trim(),
                setting => DbValidationListSetting.SplitOptions(setting.ValuesText),
                StringComparer.OrdinalIgnoreCase);
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = parameter?.ToString() ?? "";
        var rawValue = value?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawValue) || !_optionsByKey.TryGetValue(key, out var options))
        {
            return rawValue;
        }

        return options.FirstOrDefault(option =>
                   option.Value.Equals(rawValue, StringComparison.OrdinalIgnoreCase)
                   || option.DisplayText.Equals(rawValue, StringComparison.OrdinalIgnoreCase)
                   || option.Text.Equals(rawValue, StringComparison.OrdinalIgnoreCase))?.Text
               ?? rawValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
