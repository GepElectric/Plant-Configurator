using System.Windows.Controls;

namespace CwcConfigurator.App;

/// <summary>
/// Base for every editor embedded inside the Settings Hub right pane.
/// Apply commits the local edits into the underlying project model and
/// returns true on success, false if it surfaced its own validation error.
/// </summary>
public abstract class SettingsEditorViewBase : UserControl
{
    public abstract bool Apply();
}
