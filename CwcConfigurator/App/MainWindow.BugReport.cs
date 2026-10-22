using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>Help → Report a bug: collects a screenshot, the project, the log and the environment.</summary>
public partial class MainWindow
{
    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        // Taken before the dialog opens so it shows what the user was looking at.
        var screenshot = CaptureWindowPng();
        var context = new BugReportContext
        {
            Screenshot = screenshot,
            ProjectFileName = _projectOpen ? BugReportService.SafeName(Path.GetFileName(_projectPath ?? "project.plantcfg")) : null,
            ProjectContent = _projectOpen ? SerializeCurrentProject : null,
            LogFilePath = CwcApplication.LogFilePath
        };
        context.Environment.AddRange(CollectBugReportEnvironment());

        var dialog = new BugReportDialog(new BugReportService(BugReportService.LoadToken()), context) { Owner = this };
        dialog.ShowDialog();
    }

    private byte[] SerializeCurrentProject()
    {
        SyncPathFieldsToProject();
        SaveActivePlcDatabase();
        SaveActiveScadaDatabase();
        return Encoding.UTF8.GetBytes(_projectFileService.Serialize(_project));
    }

    private List<KeyValuePair<string, string>> CollectBugReportEnvironment()
    {
        var session = _tiaSessionService.CurrentSession;
        var items = new List<KeyValuePair<string, string>>
        {
            new("App version", _updateService.CurrentVersion + (_updateService.IsInstalled ? "" : " (not installed with Setup.exe)")),
            new("Windows", RuntimeInformation.OSDescription),
            new(".NET", RuntimeInformation.FrameworkDescription),
            new("Computer", Environment.MachineName),
            new("Windows user", Environment.UserName),
            new("Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            new("Project", _projectOpen ? Path.GetFileName(_projectPath ?? "(unsaved project)") : "(no project open)"),
            new("Unsaved changes", _projectOpen && HasUnsavedChanges() ? "yes" : "no"),
            new("Active PLC database", GetActivePlcDatabase()?.Name ?? ""),
            new("Active tab", MainTabs.SelectedItem is TabItem tab ? GetTabKey(tab) : ""),
            new("TIA Portal", session is null ? "not attached" : $"V{session.PortalVersion} – {session.ProjectName}")
        };
        return items;
    }

    private byte[]? CaptureWindowPng()
    {
        try
        {
            if (Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
            {
                return null;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("CaptureWindowPng", ex);
            return null;
        }
    }
}
