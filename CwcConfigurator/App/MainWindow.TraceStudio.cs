using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace CwcConfigurator.App;

/// <summary>
/// Tools → TIA Trace Studio: commissioning recordings and graphs. The app is started with
/// --plantcfg so names, units and addresses come from this project's PLC databases.
/// </summary>
public partial class MainWindow
{
    private const string TraceStudioExecutable = "TiaTraceStudio.exe";

    private static string TraceStudioSettingFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantConfigurator", "tia-trace-studio-path.txt");

    private void TraceStudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = FindTraceStudioExecutable() ?? AskTraceStudioExecutable();
            if (exePath is null)
            {
                return;
            }

            string? projectFile = null;
            if (_projectOpen)
            {
                // Trace Studio reads the saved .plantcfg, so pending edits are saved first.
                if (HasUnsavedChanges())
                {
                    var answer = MessageBox.Show(this,
                        "TIA Trace Studio reads the saved project file. Save the project now?",
                        "TIA Trace Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Cancel)
                    {
                        return;
                    }

                    if (answer == MessageBoxResult.Yes)
                    {
                        SaveProject();
                    }
                }

                if (!string.IsNullOrWhiteSpace(_project.ProjectFilePath) && File.Exists(_project.ProjectFilePath))
                {
                    projectFile = _project.ProjectFilePath;
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };
            if (projectFile is not null)
            {
                startInfo.ArgumentList.Add("--plantcfg");
                startInfo.ArgumentList.Add(projectFile);
            }

            Process.Start(startInfo);
            ShowMessage(projectFile is null
                ? $"TIA Trace Studio opened: {exePath}"
                : $"TIA Trace Studio opened with {Path.GetFileName(projectFile)}: {exePath}");
        }
        catch (Exception ex)
        {
            ShowError("Could not open TIA Trace Studio: " + ex.Message);
        }
    }

    /// <summary>Tools\TiaTraceStudio next to the app (built with it), a remembered path, then the newest release in the Trace Studio source folder.</summary>
    private static string? FindTraceStudioExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "TiaTraceStudio", TraceStudioExecutable);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        try
        {
            if (File.Exists(TraceStudioSettingFile) && File.ReadAllText(TraceStudioSettingFile).Trim() is { Length: > 0 } remembered && File.Exists(remembered))
            {
                return remembered;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var releases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Sheme", "Tia Trace studio", "04_TIA_Trace_Studio", "SourceCode", "TIA_Traces_exporter", "artifacts");
        try
        {
            if (!Directory.Exists(releases))
            {
                return null;
            }

            return Directory.GetDirectories(releases, "release-v*")
                .Select(folder => (Folder: folder, Version: Version.TryParse(Regex.Match(Path.GetFileName(folder), @"\d+(\.\d+)+").Value, out var version) ? version : null))
                .Where(item => item.Version is not null && File.Exists(Path.Combine(item.Folder, TraceStudioExecutable)))
                .OrderByDescending(item => item.Version)
                .Select(item => Path.Combine(item.Folder, TraceStudioExecutable))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? AskTraceStudioExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate TIA Trace Studio",
            Filter = $"TIA Trace Studio ({TraceStudioExecutable})|{TraceStudioExecutable}",
            FileName = TraceStudioExecutable
        };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TraceStudioSettingFile)!);
        File.WriteAllText(TraceStudioSettingFile, dialog.FileName);
        return dialog.FileName;
    }
}
