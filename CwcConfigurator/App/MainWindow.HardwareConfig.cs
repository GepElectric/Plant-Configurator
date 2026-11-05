using System.Windows;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    /// <summary>Tools → Hardware configuration in TIA (from EPLAN).</summary>
    private void HardwareConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowError("Open or create a project first.");
            return;
        }

        SaveActivePlcDatabase();
        RecordUndoSnapshot();
        var dialog = new HardwareConfigDialog(_project, _tiaSessionService) { Owner = EditorOwner };
        dialog.ShowDialog();
        BindProject();
        UpdateTiaVisualState();
    }
}
