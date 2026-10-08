using System.Windows;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;

namespace CwcConfigurator.App;

/// <summary>
/// Tools → Import hardware from EPLAN: remembers database ↔ CPU and CPU ↔ distributed stations, and fills
/// MODULE NAMES and the EPLAN columns of the hardware tabs of one PLC database from an EPLAN PXF export (.epj).
/// </summary>
public partial class MainWindow
{
    private void ImportEplan_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowError("Open or create a project first.");
            return;
        }

        SaveActivePlcDatabase();
        var dialog = new EplanImportDialog(_project.PlcDatabases, _project.EplanImport, _project.ActivePlcDatabaseId) { Owner = EditorOwner };
        if (dialog.ShowDialog() != true || dialog.Project is null)
        {
            return;
        }

        RecordUndoSnapshot();
        var assignments = dialog.Assignments();
        _project.EplanImport = new EplanImportConfig { LastEpjPath = dialog.Project.SourcePath, Assignments = assignments };

        var database = _project.PlcDatabases.FirstOrDefault(d => d.Id == dialog.ImportDatabaseId);
        var assignment = assignments.FirstOrDefault(a => database is not null && a.PlcDatabaseId == database.Id);
        if (database is null || assignment is null)
        {
            ShowMessage("EPLAN assignments saved.");
            return;
        }

        var plan = EplanHardwareMapper.BuildPlan(dialog.Project, assignment);
        var result = EplanHardwareMapper.Apply(plan, database);
        LoadActivePlcDatabase();
        BindProject();

        var lines = new List<string>
        {
            $"{database.Name} ← ={assignment.CpuFunction}" +
            (assignment.DistributedFunctions.Count > 0 ? " + =" + string.Join(", =", assignment.DistributedFunctions) : ""),
            $"{result.ChannelsWritten} rows filled, {result.RowsCleared} old EPLAN rows cleared, {result.ModuleNameChanges.Count} module names changed."
        };
        var details = plan.Warnings.Concat(result.ModuleNameChanges.Select(c => "MODULE NAME " + c)).ToList();
        if (details.Count > 0)
        {
            lines.Add("");
            lines.AddRange(details.Take(40));
            if (details.Count > 40)
            {
                lines.Add($"... and {details.Count - 40} more");
            }
        }

        MessageBox.Show(EditorOwner, string.Join("\n", lines), "EPLAN import", MessageBoxButton.OK, MessageBoxImage.Information);
        ShowMessage($"EPLAN import into {database.Name} done.");
    }
}
