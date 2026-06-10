using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CwcConfigurator.App;

/// <summary>
/// Picks a readable foreground for a cell based on whether it is read-only and
/// whether its row carries a conditional background colour. Read-only cells stay
/// muted grey on uncoloured rows (the "not editable" cue), but switch to near-black
/// on conditionally coloured rows so the text remains legible.
/// </summary>
public sealed class ReadOnlyForegroundConverter : IMultiValueConverter
{
    private static readonly Brush Editable = Brushes.Black;
    private static readonly Brush MutedReadOnly = CreateFrozen(0x7D, 0x85, 0x90);
    private static readonly Brush DarkReadOnly = CreateFrozen(0x1A, 0x1A, 0x1A);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var isReadOnly = values.Length > 0 && values[0] is bool flag && flag;
        if (!isReadOnly)
        {
            return Editable;
        }

        var background = values.Length > 1 ? values[1] as Brush : null;
        return HasConditionalColor(background) ? DarkReadOnly : MutedReadOnly;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static bool HasConditionalColor(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return false;
        }

        var color = solid.Color;
        if (color.A == 0)
        {
            return false;
        }

        return !(color.R == 255 && color.G == 255 && color.B == 255);
    }

    private static Brush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
