using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CwcConfigurator.App;

/// <summary>
/// Result window for long-running operations (TIA pull/push, generation...).
/// Shows a short summary plus the warnings up front; the full per-file log is
/// tucked away in a collapsed expander instead of being dumped into a
/// MessageBox nobody can read.
/// </summary>
public sealed class OperationResultDialog : Window
{
    public OperationResultDialog(string title, string summary, IReadOnlyList<string> warnings, IReadOnlyList<string> details)
    {
        Title = title;
        Width = 760;
        Height = 520;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(16) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var copy = new Button { Content = "Copy details", Width = 110 };
        var close = new Button { Content = "Close", Width = 92, IsCancel = true, IsDefault = true };
        copy.Click += (_, _) =>
        {
            var all = new List<string> { summary };
            if (warnings.Count > 0)
            {
                all.Add("");
                all.Add("Warnings:");
                all.AddRange(warnings.Select(warning => "- " + warning));
            }

            if (details.Count > 0)
            {
                all.Add("");
                all.Add("Log:");
                all.AddRange(details);
            }

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, all));
            }
            catch
            {
                // Clipboard can be locked by another process; not worth crashing over.
            }
        };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var summaryText = new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(summaryText, 0);
        grid.Children.Add(summaryText);

        if (warnings.Count > 0)
        {
            var warningsPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var warningsLabel = new TextBlock
            {
                Text = $"Warnings ({warnings.Count}):",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            DockPanel.SetDock(warningsLabel, Dock.Top);
            warningsPanel.Children.Add(warningsLabel);
            var warningsList = new ListBox
            {
                ItemsSource = warnings,
                MaxHeight = 170,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xDC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE))
            };
            warningsPanel.Children.Add(warningsList);
            Grid.SetRow(warningsPanel, 1);
            grid.Children.Add(warningsPanel);
        }

        var detailsBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, details),
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE))
        };
        var expander = new Expander
        {
            Header = $"Full log ({details.Count} line(s))",
            IsExpanded = warnings.Count == 0,
            Content = detailsBox
        };
        Grid.SetRow(expander, 2);
        grid.Children.Add(expander);

        root.Children.Add(grid);
        Content = root;
    }
}
