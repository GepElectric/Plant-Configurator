using System.Windows;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    /// <summary>Tools → PID auto-tuning.</summary>
    private void PidTuning_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowError("Open or create a project first.");
            return;
        }

        SaveActivePlcDatabase();
        RecordUndoSnapshot();
        var window = new PidTuningWindow(_project, () =>
        {
            LoadActivePlcDatabase();
            BindProject();
        }) { Owner = EditorOwner };
        window.ShowDialog();
        LoadActivePlcDatabase();
        BindProject();
    }
}
