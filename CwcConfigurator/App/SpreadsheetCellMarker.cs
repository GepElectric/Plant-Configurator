using System.Windows;

namespace CwcConfigurator.App;

/// <summary>
/// Attached property used to flag DataGrid cells that are part of the current
/// "copy" clipboard selection — the marching-ants Excel visual.
/// </summary>
public static class SpreadsheetCellMarker
{
    public static readonly DependencyProperty IsClipboardCopiedProperty =
        DependencyProperty.RegisterAttached(
            "IsClipboardCopied",
            typeof(bool),
            typeof(SpreadsheetCellMarker),
            new PropertyMetadata(false));

    public static bool GetIsClipboardCopied(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsClipboardCopiedProperty);
    }

    public static void SetIsClipboardCopied(DependencyObject obj, bool value)
    {
        obj.SetValue(IsClipboardCopiedProperty, value);
    }

    /// <summary>
    /// Persistent red outline drawn on cells that the validator reported as
    /// problematic for a specific field. Stays visible until validation
    /// re-runs and the issue is cleared, so the user can see at a glance
    /// which cells need attention without opening the Problems pane.
    /// </summary>
    public static readonly DependencyProperty IsProblemCellProperty =
        DependencyProperty.RegisterAttached(
            "IsProblemCell",
            typeof(bool),
            typeof(SpreadsheetCellMarker),
            new PropertyMetadata(false));

    public static bool GetIsProblemCell(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsProblemCellProperty);
    }

    public static void SetIsProblemCell(DependencyObject obj, bool value)
    {
        obj.SetValue(IsProblemCellProperty, value);
    }
}
