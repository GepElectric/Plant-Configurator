using System.Globalization;
using System.Windows.Data;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>
/// Icon id → name of its graphic in the SCADA's list (ConverterParameter = list name, e.g. MAIN_NAV_BUTTONS), shown next
/// to the GRAPHIC_LIST_ID fields. Uses the SCADA database being configured (<see cref="Current"/>).
/// </summary>
public sealed class ScadaIconNameConverter : IValueConverter
{
    /// <summary>The SCADA database shown in the SCADA tab / navigation editor.</summary>
    public static ScadaDatabase? Current { get; set; }

    public static string NameOf(ScadaDatabase? database, string list, string? id)
    {
        if (database is null || !int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
        {
            return "";
        }

        var entry = ScadaGraphicLists.EntriesFor(database, list).FirstOrDefault(item => item.Value == value);
        return entry is null ? "(not in the list)" : entry.Graphic;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        NameOf(Current, parameter as string ?? "", value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
