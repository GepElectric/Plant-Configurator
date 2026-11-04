using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

/// <summary>
/// Searchable binding field shared by the relay-logic and cubicle views: an editable list narrowed while typing (all
/// words must match, like the Program editor) that shows the stored codes with their texts and stores only the codes
/// ("DO:0, DI:3", "-K107").
/// </summary>
public static class SignalSearchField
{
    public static ComboBox Create(string value, List<string> suggestions, Action<string> apply)
    {
        string Display(string stored) => string.Join(", ", stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => suggestions.FirstOrDefault(item => item.StartsWith(part + PortRow.Separator, StringComparison.OrdinalIgnoreCase)) ?? part));
        var combo = new ComboBox
        {
            ItemsSource = suggestions,
            MinHeight = 24,
            ToolTip = "Type to narrow the list (all words must match); Down opens it, Enter takes the first match."
        };
        ComboFilter.SetIsEnabled(combo, true);
        combo.Text = Display(value);
        var original = value;
        void Commit()
        {
            var text = Code(combo.Text);
            if (text != original)
            {
                original = text;
                apply(text);
            }
        }

        combo.LostKeyboardFocus += (_, _) =>
        {
            if (!combo.IsKeyboardFocusWithin)
            {
                Commit();
            }
        };
        combo.DropDownClosed += (_, _) =>
        {
            if (combo.SelectedItem is string)
            {
                Commit();
            }
        };
        combo.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        return combo;
    }

    /// <summary>"DO:0  —  text, DI:3  —  text" → "DO:0, DI:3"; "-K107  —  text" → "-K107".</summary>
    public static string Code(string text) => PortRow.StripDescriptions(text ?? "").Trim() is { Length: > 0 } stripped && !stripped.StartsWith('-')
        ? stripped
        : (text ?? "").Split(PortRow.Separator.Trim())[0].Trim();

    /// <summary>Signals, objects and constants as in the Program editor ("DO:0  —  PLC name · text").</summary>
    public static List<string> Signals(SimulationController? controller, string plc, PortDirection direction) =>
        controller is null ? [] : BindingSuggestions.For(new PortDef("Contact", direction, PortType.Bool, ""), plc, controller);

    /// <summary>Relays (coils), selector switches and push buttons of the PLC: "-K107  —  GATE 1 OPEN (CMD)  /37.1".</summary>
    public static List<string> Devices(ProcessModel? model)
    {
        var list = new List<string>();
        for (var sheetIndex = 0; sheetIndex < (model?.RelaySheets.Count ?? 0); sheetIndex++)
        {
            var sheet = model!.RelaySheets[sheetIndex];
            for (var path = 0; path < sheet.Circuits.Count; path++)
            {
                var coil = sheet.Circuits[path].Coil;
                if (coil.Tag.Trim().Length > 0)
                {
                    list.Add($"{coil.Tag.Trim()}{PortRow.Separator}{OneLine(coil.Text)}  {RelayLogicRunner.Location(sheet, sheetIndex, path)}");
                }
            }
        }

        foreach (var selector in model?.RelaySwitches ?? [])
        {
            if (selector.Tag.Trim().Length > 0)
            {
                list.Add($"{selector.Tag.Trim()}{PortRow.Separator}{OneLine(selector.Text)}  switch {selector.Positions}");
            }
        }

        foreach (var button in model?.Cubicles.SelectMany(cubicle => cubicle.Items).Where(item => item.IsButton) ?? [])
        {
            if (button.Tag.Trim().Length > 0)
            {
                list.Add($"{button.Tag.Trim()}{PortRow.Separator}{OneLine(button.Text)}  push button");
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Label, searchable field and hint added to a properties panel.</summary>
    public static void Add(Panel panel, string label, string value, List<string> suggestions, Action<string> apply, string? hint, System.Windows.Media.Brush hintBrush)
    {
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        panel.Children.Add(Create(value, suggestions, apply));
        if (hint is not null)
        {
            panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = hintBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) });
        }
    }

    private static string OneLine(string text) => text.Replace("\r", "").Replace('\n', ' ').Trim();
}
