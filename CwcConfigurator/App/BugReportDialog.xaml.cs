using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CwcConfigurator.Services;
using Microsoft.Win32;

namespace CwcConfigurator.App;

/// <summary>What the main window hands to the bug report dialog.</summary>
public sealed class BugReportContext
{
    public byte[]? Screenshot { get; init; }
    public string? ProjectFileName { get; init; }
    public Func<byte[]>? ProjectContent { get; init; }
    public string? LogFilePath { get; init; }
    public List<KeyValuePair<string, string>> Environment { get; } = [];
}

public partial class BugReportDialog : Window
{
    private const string DescriptionTemplate = "What happened:\r\n\r\n\r\nSteps to reproduce:\r\n1. \r\n2. \r\n\r\nWhat did you expect:\r\n";

    private readonly BugReportService _service;
    private readonly BugReportContext _context;
    private readonly List<BugReportAttachment> _files = [];

    public BugReportDialog(BugReportService service, BugReportContext context)
    {
        InitializeComponent();
        _service = service;
        _context = context;

        ReporterBox.Text = System.Environment.UserName;
        DescriptionBox.Text = DescriptionTemplate;
        ScreenshotCheck.IsChecked = context.Screenshot is not null;
        ScreenshotCheck.IsEnabled = context.Screenshot is not null;
        ProjectCheck.IsChecked = context.ProjectContent is not null;
        ProjectCheck.IsEnabled = context.ProjectContent is not null;
        LogCheck.IsChecked = File.Exists(context.LogFilePath);
        LogCheck.IsEnabled = File.Exists(context.LogFilePath);
        if (context.Screenshot is not null)
        {
            ScreenshotPreview.Source = LoadImage(context.Screenshot);
        }

        if (!service.CanSubmit)
        {
            StatusText.Text = "Sending is not configured on this PC (no GitHub token). Use 'Save as .zip...' or Send to open GitHub in the browser.";
        }

        Loaded += (_, _) => TitleBox.Focus();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Attach files", Multiselect = true, Filter = "All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var path in dialog.FileNames)
        {
            var size = new FileInfo(path).Length;
            if (size > BugReportService.MaxAttachmentBytes)
            {
                MessageBox.Show(this, $"{Path.GetFileName(path)} is larger than {BugReportService.MaxAttachmentBytes / 1024 / 1024} MB and was skipped.",
                    "Report a bug", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            AddFile(new BugReportAttachment(Path.GetFileName(path), File.ReadAllBytes(path)));
        }
    }

    private void PasteImage_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
        {
            StatusText.Text = "The clipboard does not contain an image.";
            return;
        }

        AddFile(new BugReportAttachment($"pasted_{DateTime.Now:HHmmss}.png", EncodePng(image)));
    }

    private void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        var index = FilesList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _files.RemoveAt(index);
        FilesList.Items.RemoveAt(index);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildReport(out var report))
        {
            return;
        }

        if (!_service.CanSubmit)
        {
            OfferManualReport(report, "Sending is not configured on this PC (no GitHub token).");
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _service.SubmitAsync(report, new Progress<string>(text => StatusText.Text = text));
            CwcApplication.Log($"Bug report sent: {result.IssueUrl}");
            var answer = MessageBox.Show(this, $"Thank you! The report was sent as issue #{result.IssueNumber}.\n\n{result.IssueUrl}\n\nOpen it in the browser?",
                "Report a bug", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                OpenUrl(result.IssueUrl);
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("BugReport.Send", ex);
            SetBusy(false);
            OfferManualReport(report, "The report could not be sent: " + ex.Message);
        }
    }

    private void SaveZip_Click(object sender, RoutedEventArgs e)
    {
        if (TryBuildReport(out var report))
        {
            SaveZip(report);
        }
    }

    private void OfferManualReport(BugReport report, string reason)
    {
        var answer = MessageBox.Show(this,
            reason + "\n\nThe report will be saved as a .zip file and the GitHub 'new issue' page opens in the browser. " +
            "Drag the files from the zip into the issue there.\n\nContinue?",
            "Report a bug", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (SaveZip(report))
        {
            OpenUrl(BugReportService.BuildNewIssueUrl(report));
        }
    }

    private bool SaveZip(BugReport report)
    {
        try
        {
            var folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "PlantConfigurator bug reports");
            var path = BugReportService.SaveZip(report, folder);
            StatusText.Text = "Saved: " + path;
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("BugReport.SaveZip", ex);
            MessageBox.Show(this, "Could not save the report: " + ex.Message, "Report a bug", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool TryBuildReport(out BugReport report)
    {
        report = new BugReport
        {
            Title = TitleBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            Reporter = ReporterBox.Text.Trim()
        };
        if (string.IsNullOrWhiteSpace(report.Title))
        {
            MessageBox.Show(this, "Enter a short title for the bug.", "Report a bug", MessageBoxButton.OK, MessageBoxImage.Information);
            TitleBox.Focus();
            return false;
        }

        report.Environment.AddRange(_context.Environment);
        if (ScreenshotCheck.IsChecked == true && _context.Screenshot is not null)
        {
            report.Attachments.Add(new BugReportAttachment("screenshot.png", _context.Screenshot));
        }

        if (ProjectCheck.IsChecked == true && _context.ProjectContent is not null)
        {
            report.Attachments.Add(new BugReportAttachment(_context.ProjectFileName ?? "project.plantcfg", _context.ProjectContent()));
        }

        if (LogCheck.IsChecked == true && File.Exists(_context.LogFilePath))
        {
            report.Attachments.Add(new BugReportAttachment(Path.GetFileName(_context.LogFilePath), ReadSharedFile(_context.LogFilePath!)));
        }

        report.Attachments.AddRange(_files);
        return true;
    }

    private void AddFile(BugReportAttachment attachment)
    {
        _files.Add(attachment);
        FilesList.Items.Add($"{attachment.FileName}  ({FormatSize(attachment.Content.Length)})");
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        SaveZipButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private static byte[] ReadSharedFile(string path)
    {
        // The log is still open for appending by this process.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapImage LoadImage(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} kB";
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
