using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace CwcConfigurator.App.Simulation;

/// <summary>
/// Editable ComboBox whose drop-down list narrows while typing: an item stays when it contains every typed word
/// (e.g. "gate 1 open" finds "DO:0 — GATE_1_OPEN_CMD · Gate 1 open command").
/// </summary>
public static class ComboFilter
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ComboFilter), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ComboBox combo || e.NewValue is not true)
        {
            return;
        }

        combo.IsEditable = true;
        combo.IsTextSearchEnabled = false;
        combo.StaysOpenOnEdit = true;
        combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ApplyFilter(combo)));
        combo.DropDownClosed += (_, _) => ClearFilter(combo);
        combo.LostKeyboardFocus += (_, _) => ClearFilter(combo);
        combo.PreviewKeyDown += (_, args) =>
        {
            // Down opens the narrowed list; Enter takes the only (or highlighted) match.
            if (args.Key == Key.Down && !combo.IsDropDownOpen)
            {
                combo.IsDropDownOpen = true;
            }
            else if (args.Key == Key.Enter && combo.IsDropDownOpen && combo.SelectedItem is null
                     && combo.Items.Count > 0 && CollectionViewSource.GetDefaultView(combo.ItemsSource)?.Filter is not null)
            {
                combo.SelectedIndex = 0;
            }
        };
    }

    private static void ApplyFilter(ComboBox combo)
    {
        if (combo.Template.FindName("PART_EditableTextBox", combo) is not TextBox box || !box.IsKeyboardFocusWithin
            || CollectionViewSource.GetDefaultView(combo.ItemsSource) is not { } view)
        {
            return;
        }

        // The selected item text is not a search: typing starts after it changed.
        var text = box.Text;
        if (combo.SelectedItem is not null && Equals(combo.SelectedItem.ToString(), text))
        {
            return;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var caret = box.CaretIndex;
        view.Filter = words.Length == 0
            ? null
            : item => item?.ToString() is { } value && words.All(word => value.Contains(word, StringComparison.OrdinalIgnoreCase)
                                                                         || value.Replace('_', ' ').Contains(word, StringComparison.OrdinalIgnoreCase));
        // Refreshing may drop the selection and with it the typed text: put both back.
        if (box.Text != text)
        {
            box.Text = text;
        }

        box.CaretIndex = Math.Min(caret, text.Length);
        box.SelectionLength = 0;
        if (!combo.IsDropDownOpen && words.Length > 0)
        {
            combo.IsDropDownOpen = true;
            box.CaretIndex = Math.Min(caret, text.Length);
            box.SelectionLength = 0;
        }
    }

    private static void ClearFilter(ComboBox combo)
    {
        if (CollectionViewSource.GetDefaultView(combo.ItemsSource) is { Filter: not null } view)
        {
            var text = combo.Text;
            view.Filter = null;
            if (combo.Text != text)
            {
                combo.Text = text;
            }
        }
    }
}
