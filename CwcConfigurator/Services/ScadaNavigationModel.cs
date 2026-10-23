using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>Base for the editable navigation items: raises PropertyChanged so the editor updates live.</summary>
public abstract class ScadaNavItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(name);
        foreach (var other in also)
        {
            Raise(other);
        }
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One top-nav card under a main-nav button: the screen it opens plus its label and icon.</summary>
public sealed class ScadaTopNavCard : ScadaNavItem
{
    private string _screen = "";
    private string _labelEn = "";
    private string _labelOther = "";
    private string _iconId = "0";
    private bool _screenKnown = true;

    public string Screen { get => _screen; set => Set(ref _screen, value ?? ""); }
    public string LabelEn { get => _labelEn; set => Set(ref _labelEn, value ?? ""); }
    public string LabelOther { get => _labelOther; set => Set(ref _labelOther, value ?? ""); }
    public string IconId { get => _iconId; set => Set(ref _iconId, value ?? "0"); } // index into the TOP_NAV_ICONS graphic list

    /// <summary>False when the screen does not exist on the SCADA (set by the editor from the TIA screens).</summary>
    public bool ScreenKnown { get => _screenKnown; set => Set(ref _screenKnown, value); }
}

/// <summary>
/// One main-navigation button and everything configured for it: identity (screen prefix),
/// label (2 languages), icon, content layout shape, side-content default screen, first open
/// top-nav, and its ordered top-nav cards.
/// </summary>
public sealed class ScadaNavButton : ScadaNavItem
{
    private string _identity = "";
    private string _labelEn = "";
    private string _labelOther = "";
    private string _iconId = "0";
    private int _layoutShape;
    private string _firstTopNav = "1";
    private string _sideContentScreen = "";
    private bool _visible = true;

    public ScadaNavButton()
    {
        Cards.CollectionChanged += (_, _) => Raise(nameof(Display));
    }

    public int Index { get; init; }
    public string Identity { get => _identity; set => Set(ref _identity, value ?? "", nameof(Identity), nameof(Display)); }       // MAIN_NAV_BUTTONS_ENTRY_X (prefix; empty = button hidden)
    public string LabelEn { get => _labelEn; set => Set(ref _labelEn, value ?? "", nameof(LabelEn), nameof(Display)); }
    public string LabelOther { get => _labelOther; set => Set(ref _labelOther, value ?? ""); }
    public string IconId { get => _iconId; set => Set(ref _iconId, value ?? "0"); }        // index into the MAIN_NAV_BUTTONS graphic list
    public int LayoutShape { get => _layoutShape; set => Set(ref _layoutShape, value); }   // 0 with top nav, 1 without top nav, 2 without side content, 3 content only
    public string FirstTopNav { get => _firstTopNav; set => Set(ref _firstTopNav, value ?? "1"); }
    public string SideContentScreen { get => _sideContentScreen; set => Set(ref _sideContentScreen, value ?? ""); }
    public ObservableCollection<ScadaTopNavCard> Cards { get; } = new();

    /// <summary>Within the visible buttons (MAIN_NAV_BUTTONS_NUM).</summary>
    public bool Visible { get => _visible; set => Set(ref _visible, value, nameof(Visible), nameof(Display)); }

    public string Display
    {
        get
        {
            var label = !string.IsNullOrWhiteSpace(LabelEn) ? LabelEn
                : !string.IsNullOrWhiteSpace(Identity) ? Identity
                : "(empty)";
            return $"{Index}.  {label}   [{Cards.Count}]{(Visible ? "" : "   (hidden)")}";
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

    private int _buttonCount;

    /// <summary>Number of visible main-nav buttons (MAIN_NAV_BUTTONS_NUM); buttons after it are hidden.</summary>
    public int ButtonCount
    {
        get => _buttonCount;
        set
        {
            _buttonCount = Clamp(value, 0, MaxButtons);
            foreach (var button in Buttons)
            {
                button.Visible = button.Index <= _buttonCount;
            }
        }
    }

    public static ScadaNavigationModel FromDatabase(ScadaDatabase database)
    {
        var values = database.Variables
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value ?? "", StringComparer.OrdinalIgnoreCase);

        string Get(string name) => values.TryGetValue(name, out var value) ? value : "";
        bool Has(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value);

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

            // The number of screens set for the button (BUTTON_x_SCREEN_NUM) decides how many cards it has;
            // without it, a card exists where a screen is set.
            var counted = Has($"BUTTON_{b}_SCREEN_NUM");
            var cardCount = counted ? Clamp(ParseInt(Get($"BUTTON_{b}_SCREEN_NUM")), 0, MaxCards) : MaxCards;
            for (var c = 1; c <= cardCount; c++)
            {
                var screen = Get($"SCREENS_Button_{b}_ENTRY_{c}");
                if (!counted && string.IsNullOrWhiteSpace(screen))
                {
                    continue;
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

        model.ButtonCount = Has("MAIN_NAV_BUTTONS_NUM")
            ? ParseInt(Get("MAIN_NAV_BUTTONS_NUM"))
            : model.Buttons.Count(button => !string.IsNullOrWhiteSpace(button.Identity));
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

        Set("MAIN_NAV_BUTTONS_NUM", ButtonCount.ToString(CultureInfo.InvariantCulture), "INT", "MAIN");
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

            // Every card counts (also one whose screen is still empty), like BUTTON_x_SCREEN_NUM on the MAINNAV tab.
            var cards = button.Cards.Take(MaxCards).ToList();
            Set($"BUTTON_{b}_SCREEN_NUM", cards.Count.ToString(CultureInfo.InvariantCulture), "INT", "MAIN");
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
