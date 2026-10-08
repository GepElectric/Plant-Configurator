using System.Windows;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;

namespace CwcConfigurator.App;

/// <summary>
/// File → Import hardware from EPLAN: fills the EPLAN columns of the hardware tabs and MODULE NAMES
/// of the chosen PLC databases from an EPLAN PXF export (.epj).
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
        var dialog = new EplanImportDialog(_project.PlcDatabases, _project.EplanImport) { Owner = EditorOwner };
        if (dialog.ShowDialog() != true || dialog.Project is null)
        {
            return;
        }

        RecordUndoSnapshot();
        var report = new List<string>();
        var warnings = new List<string>();
        foreach (var assignment in dialog.Assignments.Where(a => a.PlcDatabaseId.Length > 0))
        {
            var database = _project.PlcDatabases.First(d => d.Id == assignment.PlcDatabaseId);
            var plan = EplanHardwareMapper.BuildPlan(dialog.Project, assignment);
            var result = EplanHardwareMapper.Apply(plan, database);
            report.Add($"={assignment.CpuFunction} → {database.Name}: {result.ChannelsWritten} rows, " +
                       $"{result.ModuleNameChanges.Count} module names changed" +
                       (assignment.DistributedFunctions.Count > 0 ? $" (stations ={string.Join(", =", assignment.DistributedFunctions)})" : ""));
            warnings.AddRange(plan.Warnings.Select(w => $"={assignment.CpuFunction}: {w}"));
            warnings.AddRange(result.ModuleNameChanges.Select(c => $"{database.Name}: MODULE NAME {c}"));
        }

        _project.EplanImport = new EplanImportConfig
        {
            LastEpjPath = dialog.Project.SourcePath,
            Assignments = dialog.Assignments.ToList()
        };

        LoadActivePlcDatabase();
        BindProject();

        var details = warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", warnings.Take(40)) + (warnings.Count > 40 ? $"\n... and {warnings.Count - 40} more" : "");
        MessageBox.Show(EditorOwner, string.Join("\n", report) + details, "EPLAN import", MessageBoxButton.OK, MessageBoxImage.Information);
        ShowMessage($"EPLAN import from {System.IO.Path.GetFileName(dialog.Project.SourcePath)} done.");
    }
}
