using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace CwcConfigurator.App;

/// <summary>
/// Crash protection: while a project is open and dirty, a plain-JSON copy is
/// written to %AppData%\PlantConfigurator every few minutes. On startup, if
/// that copy is newer than the project file it came from, the user is offered
/// recovery. The copy is removed on every successful save and on clean exit.
/// </summary>
public partial class MainWindow
{
    private static readonly string AutosaveFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PlantConfigurator");

    private static readonly string AutosavePath = Path.Combine(AutosaveFolder, "autosave.plantcfg.json");
    private static readonly string AutosaveMetaPath = Path.Combine(AutosaveFolder, "autosave-meta.json");

    private DispatcherTimer? _autosaveTimer;

    private sealed record AutosaveMeta(string? OriginalPath, DateTime SavedAt);

    private void InitializeAutosave()
    {
        _autosaveTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMinutes(3)
        };
        _autosaveTimer.Tick += (_, _) => TryAutosave();
        _autosaveTimer.Start();
    }

    private void TryAutosave()
    {
        if (!_projectOpen || !_lastKnownDirty || IsBusy)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AutosaveFolder);
            File.WriteAllText(AutosavePath, _projectFileService.Serialize(_project));
            File.WriteAllText(AutosaveMetaPath, JsonSerializer.Serialize(new AutosaveMeta(_projectPath, DateTime.Now)));
        }
        catch
        {
            // Autosave is best-effort; never interrupt the user's editing session.
        }
    }

    private void DeleteAutosave()
    {
        try
        {
            if (File.Exists(AutosavePath))
            {
                File.Delete(AutosavePath);
            }

            if (File.Exists(AutosaveMetaPath))
            {
                File.Delete(AutosaveMetaPath);
            }
        }
        catch
        {
        }
    }

    private void OfferAutosaveRecovery()
    {
        if (!File.Exists(AutosavePath))
        {
            return;
        }

        AutosaveMeta? meta = null;
        try
        {
            if (File.Exists(AutosaveMetaPath))
            {
                meta = JsonSerializer.Deserialize<AutosaveMeta>(File.ReadAllText(AutosaveMetaPath));
            }
        }
        catch
        {
        }

        // Stale copy: the original project was saved after the autosave was
        // written (e.g. a previous session ended cleanly via Save As).
        if (meta?.OriginalPath is { } original
            && File.Exists(original)
            && File.GetLastWriteTime(original) >= File.GetLastWriteTime(AutosavePath))
        {
            DeleteAutosave();
            return;
        }

        var originName = string.IsNullOrWhiteSpace(meta?.OriginalPath) ? "an unsaved project" : meta!.OriginalPath;
        var savedAt = meta?.SavedAt ?? File.GetLastWriteTime(AutosavePath);
        var answer = MessageBox.Show(
            this,
            $"An automatically saved copy of {originName} was found (saved {savedAt:g}).\n\n"
            + "The application may have closed before those changes were saved. Restore them?",
            "Recover unsaved changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            DeleteAutosave();
            return;
        }

        try
        {
            _project = _projectFileService.Load(AutosavePath);
            EnsureProjectDefaults();
            _projectPath = string.IsNullOrWhiteSpace(meta?.OriginalPath) ? null : meta!.OriginalPath;
            _project.ProjectFilePath = _projectPath ?? "";
            _projectOpen = true;
            BindProject();
            HideWelcomeScreen();
            // Deliberately NOT marked clean: the recovered state is unsaved by
            // definition, so the dirty indicator and Save stay armed.
            _savedSnapshotHash = [];
            _lastKnownDirty = true;
            ShowMessage("Recovered unsaved changes from autosave. Use Save to keep them.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not restore the autosaved copy: {ex.Message}");
        }
    }
}
