using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>One main-navigation button: its label and the ordered screens opened under it.</summary>
public sealed class ScadaNavButton
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public List<string> Screens { get; } = new();

    /// <summary>Label shown in the navigation builder's button list.</summary>
    public string Display => $"{Index}.  {(string.IsNullOrWhiteSpace(Name) ? "(empty)" : Name)}   [{Screens.Count}]";
}

/// <summary>
/// A friendly button→screens view over the flat <see cref="ScadaDatabase"/> navigation
/// variables (<c>MAIN_NAV_BUTTONS_ENTRY_X</c> = button label, <c>SCREENS_Button_X_ENTRY_Y</c>
/// = screens under button X). The <c>MAIN_NAV_BUTTONS_NUM</c> / <c>BUTTON_X_SCREEN_NUM</c>
/// counters are derived automatically by <see cref="ScadaConfigFileService"/> on normalize,
/// so this model only reads/writes labels and screen slots.
/// </summary>
public sealed class ScadaNavigationModel
{
    public const int MaxButtons = 16;
    public const int MaxScreensPerButton = 16;

    private const string ButtonNameComment = "Prefix screenova za koji se otvaraju na odabranu tipku, ako nema teksta tipka se ne vidi";
    private const string ScreenComment = "Screenovi koji se otvaraju na zadanu tipku na main nav";

    public List<ScadaNavButton> Buttons { get; } = new();

    public static ScadaNavigationModel FromDatabase(ScadaDatabase database)
    {
        var values = database.Variables
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value ?? "", StringComparer.OrdinalIgnoreCase);

        string Get(string name) => values.TryGetValue(name, out var value) ? value : "";

        var model = new ScadaNavigationModel();
        for (var button = 1; button <= MaxButtons; button++)
        {
            var item = new ScadaNavButton
            {
                Index = button,
                Name = Get($"MAIN_NAV_BUTTONS_ENTRY_{button}")
            };

            for (var entry = 1; entry <= MaxScreensPerButton; entry++)
            {
                var screen = Get($"SCREENS_Button_{button}_ENTRY_{entry}");
                if (!string.IsNullOrWhiteSpace(screen))
                {
                    item.Screens.Add(screen.Trim());
                }
            }

            model.Buttons.Add(item);
        }

        return model;
    }

    /// <summary>
    /// Writes button labels and screen slots back into the database. Screens are packed from
    /// the top (no gaps); empty trailing slots are cleared. Counters are left to
    /// <see cref="ScadaConfigFileService"/> normalization.
    /// </summary>
    public void ApplyTo(ScadaDatabase database)
    {
        var byName = database.Variables
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        void Set(string name, string value, string section, string comment)
        {
            if (byName.TryGetValue(name, out var existing))
            {
                existing.Value = value;
                return;
            }

            var created = new ScadaConfigVariable
            {
                Id = database.Variables.Count == 0 ? 1 : database.Variables.Max(variable => variable.Id) + 1,
                Section = section,
                Name = name,
                Type = "STRING",
                Value = value,
                Comment = comment
            };
            database.Variables.Add(created);
            byName[name] = created;
        }

        foreach (var button in Buttons)
        {
            Set($"MAIN_NAV_BUTTONS_ENTRY_{button.Index}", button.Name ?? "", "MAIN_NAV", ButtonNameComment);

            var screens = button.Screens
                .Where(screen => !string.IsNullOrWhiteSpace(screen))
                .Select(screen => screen.Trim())
                .Take(MaxScreensPerButton)
                .ToList();

            for (var entry = 1; entry <= MaxScreensPerButton; entry++)
            {
                var value = entry <= screens.Count ? screens[entry - 1] : "";
                Set($"SCREENS_Button_{button.Index}_ENTRY_{entry}", value, "SCREENS", ScreenComment);
            }
        }
    }
}
