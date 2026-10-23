using System.Globalization;
using System.IO;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>
/// Icon lists used by the SCADA configuration. Geppert SCADA: the WinCC Unified graphic lists of the device
/// (MAIN_NAV_BUTTONS for the main navigation, TOP_NAV_ICONS for the top-nav cards, TREND_ICONS for the location
/// systems), pulled with their images. Prime Volt SCADA: fixed icons of the CWC, only on the main navigation.
/// </summary>
public static class ScadaGraphicLists
{
    public const string MainNav = "MAIN_NAV_BUTTONS";
    public const string TopNav = "TOP_NAV_ICONS";
    public const string Trends = "TREND_ICONS";
    public static readonly string[] Names = [MainNav, TopNav, Trends];

    /// <summary>Prime Volt main-nav icons (icon_settings[] in the CWC).</summary>
    public static readonly IReadOnlyList<ScadaGraphicEntry> PrimeVoltMainNav =
    [
        new() { Value = 1, Graphic = "common_icon.svg" },
        new() { Value = 2, Graphic = "unit_icon.svg" },
        new() { Value = 3, Graphic = "alarm_icon.svg" },
        new() { Value = 4, Graphic = "trend_icon.svg" },
        new() { Value = 5, Graphic = "configuration_icon.svg" },
        new() { Value = 6, Graphic = "reports_icon.svg" }
    ];

    /// <summary>Icons that can be chosen for a list in the SCADA's style (empty = icons are not used there).</summary>
    public static IReadOnlyList<ScadaGraphicEntry> EntriesFor(ScadaDatabase database, string list)
    {
        if (database.IsPrimeVolt)
        {
            return list == MainNav ? PrimeVoltMainNav : [];
        }

        return database.GraphicLists.FirstOrDefault(item => item.Name.Equals(list, StringComparison.OrdinalIgnoreCase))?.Entries ?? [];
    }

    /// <summary>
    /// Reads an export of the device's graphic lists: the *.hmi.yml of the lists and graphics\&lt;name&gt; files\ with
    /// the images. Only <see cref="Names"/> are kept.
    /// </summary>
    public static List<ScadaGraphicList> Read(string folder)
    {
        var lists = new List<ScadaGraphicList>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.hmi.yml", SearchOption.TopDirectoryOnly))
        {
            ScadaGraphicList? current = null;
            ScadaGraphicEntry? entry = null;
            var listIndent = -1;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.TrimEnd();
                var text = line.TrimStart();
                var indent = line.Length - text.Length;
                if (text.Length == 0 || text.StartsWith('#'))
                {
                    continue;
                }

                // "      MAIN_NAV_BUTTONS:" directly under ResourceLists starts a list.
                if (text.EndsWith(':') && Names.Contains(text.TrimEnd(':'), StringComparer.OrdinalIgnoreCase))
                {
                    current = new ScadaGraphicList { Name = text.TrimEnd(':') };
                    lists.Add(current);
                    listIndent = indent;
                    entry = null;
                    continue;
                }

                if (current is not null && indent <= listIndent)
                {
                    current = null; // the next list (not one of ours) or the end of the lists
                }

                if (current is null)
                {
                    continue;
                }

                if (text.StartsWith("Graphic_list_entry", StringComparison.OrdinalIgnoreCase) && text.EndsWith(':'))
                {
                    entry = new ScadaGraphicEntry();
                    current.Entries.Add(entry);
                }
                else if (entry is not null && text.StartsWith("Value:", StringComparison.Ordinal)
                         && int.TryParse(text["Value:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    entry.Value = value;
                }
                else if (entry is not null && text.StartsWith("Graphic:", StringComparison.Ordinal))
                {
                    var name = text["Graphic:".Length..].Trim().Trim('"', '\'');
                    var dot = name.IndexOf('.');
                    entry.Graphic = dot >= 0 ? name[(dot + 1)..] : name;
                }
            }
        }

        // Images exported next to graphics\<name>.xml in "<name> files" (DefaultImageStream.svg / .png).
        var graphics = Path.Combine(folder, "graphics");
        foreach (var entry in lists.SelectMany(list => list.Entries))
        {
            var files = Path.Combine(graphics, MakeFileName(entry.Graphic) + " files");
            var image = Directory.Exists(files)
                ? Directory.EnumerateFiles(files).OrderBy(path => Path.GetFileName(path).StartsWith("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault()
                : null;
            if (image is null)
            {
                continue;
            }

            entry.ImageType = Path.GetExtension(image).TrimStart('.').ToLowerInvariant();
            entry.Image = Convert.ToBase64String(File.ReadAllBytes(image));
        }

        foreach (var list in lists)
        {
            list.Entries.Sort((a, b) => a.Value.CompareTo(b.Value));
        }

        return lists;
    }

    private static string MakeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
