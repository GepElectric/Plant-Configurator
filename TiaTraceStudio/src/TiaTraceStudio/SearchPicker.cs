using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace TiaTraceStudio;

/// <summary>
/// Editable ComboBox of measurements that filters while typing: every typed word must appear somewhere in the item
/// ("tlak cjev", "168", "bearing temp"), not only at the start. Enter picks the single or first match.
/// </summary>
public static class SearchPicker
{
    public static void Attach(ComboBox combo, Func<object, string> text, Action? enter = null)
    {
        combo.IsEditable = true; combo.IsTextSearchEnabled = false; combo.StaysOpenOnEdit = true;
        combo.ToolTip ??= "Upiši dio naziva, taga ili ID-a (više riječi u bilo kojem redoslijedu); Enter dodaje.";
        bool busy = false;
        combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (busy || combo.ItemsSource == null || combo.Template.FindName("PART_EditableTextBox", combo) is not TextBox box) return;
            string typed = box.Text;
            if (combo.SelectedItem != null && text(combo.SelectedItem) == typed) return;
            busy = true;
            try
            {
                int caret = box.CaretIndex;
                var words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                CollectionViewSource.GetDefaultView(combo.ItemsSource).Filter = item => words.All(w => text(item).Contains(w, StringComparison.OrdinalIgnoreCase));
                if (box.Text != typed) box.Text = typed;
                box.SelectionLength = 0; box.CaretIndex = Math.Min(caret, typed.Length);
                if (box.IsKeyboardFocusWithin && typed.Length > 0) combo.IsDropDownOpen = true;
            }
            finally { busy = false; }
        }));
        combo.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || combo.ItemsSource == null) return;
            if (combo.SelectedItem == null && CollectionViewSource.GetDefaultView(combo.ItemsSource).Cast<object>().FirstOrDefault() is object first)
            { busy = true; combo.SelectedItem = first; busy = false; }
            combo.IsDropDownOpen = false; enter?.Invoke(); e.Handled = true;
        };
        // A new list (other PLC) starts unfiltered.
        combo.DropDownClosed += (_, _) => { if (combo.SelectedItem != null && combo.ItemsSource != null) ClearFilter(combo); };
    }

    public static void ClearFilter(ComboBox combo)
    {
        if (combo.ItemsSource != null) CollectionViewSource.GetDefaultView(combo.ItemsSource).Filter = null;
    }

    /// <summary>The picked item, or the only / first item still matching the typed text.</summary>
    public static T? Picked<T>(ComboBox combo) where T : class =>
        combo.SelectedItem as T ?? (combo.ItemsSource == null || string.IsNullOrWhiteSpace(combo.Text) ? null :CollectionViewSource.GetDefaultView(combo.ItemsSource).OfType<T>().FirstOrDefault());
}
