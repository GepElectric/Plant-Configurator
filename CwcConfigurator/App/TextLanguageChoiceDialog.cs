using System.Windows;
using System.Windows.Controls;

namespace CwcConfigurator.App;

/// <summary>
/// Asked on a text-list pull / push when the TIA project has more than one language: which TIA language is the
/// app's other language (English always goes to English).
/// </summary>
public sealed class TextLanguageChoiceDialog : Window
{
    private const string EnglishOnly = "(none — English only)";
    private readonly ComboBox _languages = new() { MinHeight = 28, Margin = new Thickness(0, 8, 0, 0) };

    private TextLanguageChoiceDialog(IReadOnlyList<string> tiaLanguages, string english, string other, bool pull)
    {
        Title = "Text list languages";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"The TIA project has the languages {string.Join(", ", tiaLanguages)}.\n" + (pull
                ? $"English texts are read from {english}. Which TIA language should be read as the other language of the descriptions?"
                : $"English texts go to {english}. Into which TIA language should the other-language descriptions ({other}) go?")
        });

        var options = tiaLanguages.Where(language => !language.Equals(english, StringComparison.OrdinalIgnoreCase)).ToList();
        options.Add(EnglishOnly);
        _languages.ItemsSource = options;
        _languages.SelectedItem = options.FirstOrDefault(language => language.Equals(other, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        root.Children.Add(_languages);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = pull ? "Pull" : "Push", Width = 92, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 92, IsCancel = true });
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>The TIA language for the other-language texts, "" for English only, null when cancelled.</summary>
    public static string? Choose(Window owner, IReadOnlyList<string> tiaLanguages, string english, string other, bool pull)
    {
        var dialog = new TextLanguageChoiceDialog(tiaLanguages, english, other, pull) { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var selected = dialog._languages.SelectedItem as string ?? EnglishOnly;
        return selected == EnglishOnly ? "" : selected;
    }
}
