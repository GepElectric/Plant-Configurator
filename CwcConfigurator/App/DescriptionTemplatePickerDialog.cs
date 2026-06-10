using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

/// <summary>
/// Lightweight searchable picker used by the ALM/PAR grid context menu to
/// apply a description template to the selected rows.
/// </summary>
public sealed class DescriptionTemplatePickerDialog : Window
{
    private readonly List<DescriptionTemplateEntry> _all;
    private readonly ListBox _list = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _search = new() { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };

    public DescriptionTemplateEntry? SelectedTemplate => (_list.SelectedItem as PickerItem)?.Entry;

    private sealed record PickerItem(DescriptionTemplateEntry Entry)
    {
        public override string ToString()
        {
            var text = (Entry.TextEnglish ?? "").Trim();
            if (text.Length > 90)
            {
                text = text[..90] + "…";
            }

            return text.Length == 0 ? Entry.Key : $"{Entry.Key}  —  {text}";
        }
    }

    public DescriptionTemplatePickerDialog(DescriptionTemplateCatalog catalog, string scope)
    {
        Title = $"Apply description template ({scope})";
        Width = 640;
        Height = 480;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all = catalog.ForScope(scope)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var root = new DockPanel { Margin = new Thickness(14) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "Apply", Width = 92, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 92, IsCancel = true };
        ok.Click += (_, _) => TryAccept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var searchRow = new DockPanel();
        var searchLabel = new TextBlock
        {
            Text = "Search:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchRow.Children.Add(searchLabel);
        searchRow.Children.Add(_search);
        DockPanel.SetDock(searchRow, Dock.Top);
        root.Children.Add(searchRow);

        _search.TextChanged += (_, _) => PopulateList();
        _list.MouseDoubleClick += (_, _) => TryAccept();
        _list.ToolTip = "Double-click a template to apply it.";
        root.Children.Add(_list);

        Content = root;
        PopulateList();
        Loaded += (_, _) => _search.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is not null)
            {
                TryAccept();
                e.Handled = true;
            }
        };
    }

    private void PopulateList()
    {
        var search = (_search.Text ?? "").Trim();
        var items = _all
            .Where(item => string.IsNullOrWhiteSpace(search)
                           || string.Join(" ", item.Key, item.TextEnglish, item.TextOther, item.Notes)
                               .Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(item => new PickerItem(item))
            .ToList();
        _list.ItemsSource = items;
        if (items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    private void TryAccept()
    {
        if (SelectedTemplate is null)
        {
            MessageBox.Show(this, "Select a template first.", "Apply template", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
