using System.Globalization;
using System.Windows.Data;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

/// <summary>
/// Turns a row's DescriptionTemplateKey into a hover tooltip showing the full
/// resolved template texts, so the user can see what a templated (bold)
/// description cell will actually generate without opening the Template
/// Editor. Returns null (no tooltip) for non-templated cells.
/// </summary>
public sealed class DescriptionTemplateTooltipConverter : IValueConverter
{
    public DescriptionTemplateCatalog? Catalog { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string)?.Trim();
        if (string.IsNullOrWhiteSpace(key) || Catalog?.Templates is null)
        {
            return null;
        }

        var template = Catalog.Templates.FirstOrDefault(item =>
            item.Key.Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return $"Template '{key}' was not found in the template catalog.";
        }

        var lines = new List<string> { $"Template: {template.Key}" };
        if (!string.IsNullOrWhiteSpace(template.TextEnglish))
        {
            lines.Add($"ENG: {template.TextEnglish}");
        }

        if (!string.IsNullOrWhiteSpace(template.TextOther))
        {
            lines.Add($"Other: {template.TextOther}");
        }

        if (!string.IsNullOrWhiteSpace(template.Notes))
        {
            lines.Add($"Notes: {template.Notes}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
