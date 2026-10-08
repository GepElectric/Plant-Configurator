using System.Windows;
using CwcConfigurator.Services.Eplan;

namespace CwcConfigurator.App;

/// <summary>
/// Tools → Automap IDs: writes into hardware rows the ID of the DB signal whose English name matches
/// the EPLAN signal name (DB tables are not changed).
/// </summary>
public partial class MainWindow
{
    private void AutomapEplanIds_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowError("Open or create a project first.");
            return;
        }

        SaveActivePlcDatabase();
        var dialog = new EplanAutomapDialog(_project.PlcDatabases, _project.ActivePlcDatabaseId) { Owner = EditorOwner };
        if (dialog.ShowDialog() != true || dialog.Database is null)
        {
            return;
        }

        RecordUndoSnapshot();
        var changed = EplanIdAutomap.Apply(dialog.Chosen);
        LoadActivePlcDatabase();
        BindProject();
        ShowMessage($"Automap: {changed} hardware IDs written in {dialog.Database.Name}.");
    }
}
