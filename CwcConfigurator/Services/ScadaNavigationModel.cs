using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>One top-nav card under a main-nav button: the screen it opens plus its label and icon.</summary>
public sealed class ScadaTopNavCard
{
    public string Screen { get; set; } = "";
    public string LabelEn { get; set; } = "";
    public string LabelOther { get; set; } = "";
    public string IconId { get; set; } = "0"; // index into the TOP_NAV_ICONS graphic list
}

/// <summary>
/// One main-navigation button and everything configured for it: identity (screen prefix),
/// label (2 languages), icon, content layout shape, side-content default screen, first open
/// top-nav, and its ordered top-nav cards.
/// </summary>
public sealed class ScadaNavButton
{
    public int Index { get; init; }
    public string Identity { get; set; } = "";       // MAIN_NAV_BUTTONS_ENTRY_X (prefix; empty = button hidden)
    public string LabelEn { get; set; } = "";
    public string LabelOther { get; set; } = "";
    public string IconId { get; set; } = "0";        // index into the MAIN_NAV_BUTTONS graphic list
    public int LayoutShape { get; set; }             // 0 with top nav, 1 without top nav, 2 without side content, 3 content only
    public string FirstTopNav { get; set; } = "1";
    public string SideContentScreen { get; set; } = "";
    public List<ScadaTopNavCard> Cards { get; } = new();

    public string Display
    {
        get
        {
            var label = !string.IsNullOrWhiteSpace(LabelEn) ? LabelEn
                : !string.IsNullOrWhiteSpace(Identity) ? Identity
                : "(empty)";
            return $"{Index}.  {label}   [{Cards.Count}]";
        }
    }
}

/// <summary>
/// Friendly two-level model (main-nav buttons -> top-nav cards) over the flat
/// <see cref="ScadaDatabase"/> navigation variables. Counters (BUTTON_x_SCREEN_NUM,
/// MAIN_NAV_BUTTONS_NUM) are derived by <see cref="ScadaConfigFileService"/> on normalize.
/// </summary>
public sealed class ScadaNavigationModel
{
    public const int MaxButtons = 16;
    public const int MaxCards = 16;

    public List<ScadaNavButton> Buttons { get; } = new();

    public static ScadaNavigationModel FromDatabase(ScadaDatabase database)
    {
        var values = database.Variables
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value ?? "", StringComparer.OrdinalIgnoreCase);

        string Get(string name) => values.TryGetValue(name, out var value) ? value : "";

        var model = new ScadaNavigationModel();
        for (var b = 1; b <= MaxButtons; b++)
        {
            var button = new ScadaNavButton
            {
                Index = b,
                Identity = Get($"MAIN_NAV_BUTTONS_ENTRY_{b}"),
                LabelEn = Get($"MAIN_NAV_BTTN_{b}_TEXT_LANGUAGE_1"),
                LabelOther = Get($"MAIN_NAV_BTTN_{b}_TEXT_LANGUAGE_2"),
                IconId = NumOrZero(Get($"MAIN_NAV_BTTN_{b}_GRAPHIC_LIST_ID")),
                LayoutShape = Clamp(ParseInt(Get($"MAIN_NAV_SCREEN_SHAPE_{b}")), 0, 3),
                FirstTopNav = NumOrDefault(Get($"BUTTON_{b}_FIRST_TOP_NAV"), "1"),
                SideContentScreen = Get($"SIDE_CONTENT_DEFAULT_SCREENS_ENTRY_{b}")
            };

            for (var c = 1; c <= MaxCards; c++)
            {
                var screen = Get($"SCREENS_Button_{b}_ENTRY_{c}");
                if (string.IsNullOrWhiteSpace(screen))
                {
                    continue; // a card exists only where it has a screen
                }

                button.Cards.Add(new ScadaTopNavCard
                {
                    Screen = screen.Trim(),
                    LabelEn = Get($"Button_{b}_TOP_NAV_CARD_{c}_TEXT_LANGUAGE_1"),
                    LabelOther = Get($"Button_{b}_TOP_NAV_CARD_{c}_TEXT_LANGUAGE_2"),
                    IconId = NumOrZero(Get($"Button_{b}_TOP_NAV_CARD_{c}_GRAPHIC_LIST_ID"))
                });
            }

            model.Buttons.Add(button);
        }

        return model;
    }

    public void ApplyTo(ScadaDatabase database)
    {
        var byName = database.Variables
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        void Set(string name, string value, string type, string section)
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
                Type = type,
                Value = value
            };
            database.Variables.Add(created);
            byName[name] = created;
        }

        foreach (var button in Buttons)
        {
            var b = button.Index;
            Set($"MAIN_NAV_BUTTONS_ENTRY_{b}", button.Identity ?? "", "STRING", "MAIN_NAV_BUTTONS");
            Set($"MAIN_NAV_BTTN_{b}_TEXT_LANGUAGE_1", button.LabelEn ?? "", "STRING", "TABS");
            Set($"MAIN_NAV_BTTN_{b}_TEXT_LANGUAGE_2", button.LabelOther ?? "", "STRING", "TABS");
            Set($"MAIN_NAV_BTTN_{b}_GRAPHIC_LIST_ID", NumOrZero(button.IconId), "INT", "MAIN_NAV_BUTTONS");
            Set($"MAIN_NAV_SCREEN_SHAPE_{b}", Clamp(button.LayoutShape, 0, 3).ToString(CultureInfo.InvariantCulture), "INT", "MAIN_NAV_BUTTONS");
            Set($"BUTTON_{b}_FIRST_TOP_NAV", NumOrDefault(button.FirstTopNav, "1"), "INT", "MAIN");
            Set($"SIDE_CONTENT_DEFAULT_SCREENS_ENTRY_{b}", button.SideContentScreen ?? "", "STRING", "SIDE_CONTENT");

            var cards = button.Cards.Where(card => !string.IsNullOrWhiteSpace(card.Screen)).Take(MaxCards).ToList();
            for (var c = 1; c <= MaxCards; c++)
            {
                var card = c <= cards.Count ? cards[c - 1] : null;
                Set($"SCREENS_Button_{b}_ENTRY_{c}", card?.Screen?.Trim() ?? "", "STRING", "SCREEN_ENTRY");
                Set($"Button_{b}_TOP_NAV_CARD_{c}_TEXT_LANGUAGE_1", card?.LabelEn ?? "", "STRING", "TOP_NAV_CARDS");
                Set($"Button_{b}_TOP_NAV_CARD_{c}_TEXT_LANGUAGE_2", card?.LabelOther ?? "", "STRING", "TOP_NAV_CARDS");
                Set($"Button_{b}_TOP_NAV_CARD_{c}_GRAPHIC_LIST_ID", card is null ? "0" : NumOrZero(card.IconId), "INT", "TOP_NAV_CARDS");
            }
        }
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string NumOrZero(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : "0";

    private static string NumOrDefault(string value, string fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : fallback;

    private static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;
}
