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
        RecordUndoSnapshot();       // the dialog may also edit the dictionary of the project
        var dialog = new EplanAutomapDialog(_project.PlcDatabases, _project.ActivePlcDatabaseId, _project.EplanImport) { Owner = EditorOwner };
        if (dialog.ShowDialog() != true || dialog.Database is null)
        {
            if (dialog.DictionaryChanged)
            {
                ShowMessage("Automap dictionary saved.");
            }

            return;
        }

        var changed = EplanIdAutomap.Apply(dialog.Chosen);
        var learned = EplanIdAutomap.Learn(_project.EplanImport, dialog.Chosen);
        LoadActivePlcDatabase();
        BindProject();
        ShowMessage($"Automap: {changed} hardware IDs written in {dialog.Database.Name}" + (learned > 0 ? $", {learned} new name pairs learned." : "."));
    }
}
