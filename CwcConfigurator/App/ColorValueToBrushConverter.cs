using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public sealed class ColorValueToBrushConverter : IValueConverter
{
    private static Dictionary<string, Brush> _configuredBrushes = new(StringComparer.OrdinalIgnoreCase);

    public static void SetConfiguredColors(IEnumerable<(string Name, string Color)> colors)
    {
        _configuredBrushes = colors
            .Where(color => !string.IsNullOrWhiteSpace(color.Name))
            .Select(color => (color.Name, Brush: TryCreateBrush(color.Color)))
            .Where(color => color.Brush is not null)
            .ToDictionary(color => color.Name.Trim(), color => color.Brush!, StringComparer.OrdinalIgnoreCase);
    }

    public static void SetConfiguredColors(IEnumerable<(string Code, string Name, string Color)> colors)
    {
        var brushes = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        foreach (var color in colors)
        {
            var brush = TryCreateBrush(color.Color);
            if (brush is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(color.Code))
            {
                brushes[color.Code.Trim()] = brush;
            }

            if (!string.IsNullOrWhiteSpace(color.Name))
            {
                brushes[color.Name.Trim()] = brush;
            }
        }

        _configuredBrushes = brushes;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return ResolveBrush(value);
    }

    public static Brush ResolveBrush(object? value)
    {
        var colorName = NormalizeColorKey(value);
        if (!string.IsNullOrWhiteSpace(colorName) && _configuredBrushes.TryGetValue(colorName, out var configuredBrush))
        {
            return configuredBrush;
        }

        return colorName?.ToUpperInvariant() switch
        {
            "0" => Brushes.Transparent,
            "1" => Brushes.White,
            "2" => Brushes.Yellow,
            "3" => Brushes.Red,
            "4" => Brushes.LimeGreen,
            "5" => Brushes.DodgerBlue,
            "NO COLOR" => Brushes.Transparent,
            "WHITE" => Brushes.White,
            "YELLOW" => Brushes.Yellow,
            "RED" => Brushes.Red,
            "GREEN" => Brushes.LimeGreen,
            "BLUE" => Brushes.DodgerBlue,
            _ => Brushes.Transparent
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }

    private static Brush? TryCreateBrush(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        try
        {
            return (Brush)new BrushConverter().ConvertFromString(color.Trim())!;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeColorKey(object? value)
    {
        var text = value switch
        {
            DbValidationOption option => option.Value.Trim(),
            string raw => raw.Trim(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (_configuredBrushes.ContainsKey(text))
        {
            return text;
        }

        var separatorIndex = text.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            var code = text[..separatorIndex].Trim();
            if (_configuredBrushes.ContainsKey(code))
            {
                return code;
            }

            var name = text[(separatorIndex + 1)..].Trim();
            if (_configuredBrushes.ContainsKey(name))
            {
                return name;
            }

            return name;
        }

        return text;
    }
}
