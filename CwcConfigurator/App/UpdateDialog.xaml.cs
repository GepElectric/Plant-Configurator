using System.ComponentModel;
using System.Windows;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>
/// Shows a found update and downloads it. DialogResult true means the update is downloaded and the
/// caller should apply it; for a mandatory update false means the user chose to exit instead.
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly AppUpdateService _service;
    private readonly AppUpdate _update;
    private bool _downloading;

    public UpdateDialog(AppUpdateService service, AppUpdate update)
    {
        InitializeComponent();
        _service = service;
        _update = update;

        VersionText.Text = $"Installed: {service.CurrentVersion}    New: {update.Version}";
        NotesText.Text = string.IsNullOrWhiteSpace(update.Notes) ? "(no release notes)" : update.Notes;
        if (update.IsMandatory)
        {
            HeadlineText.Text = "A required update is available";
            MandatoryBanner.Visibility = Visibility.Visible;
            LaterButton.Content = "Exit app";
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _downloading = true;
        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading update...";
        try
        {
            await _service.DownloadAsync(_update, percent => Dispatcher.BeginInvoke(() =>
            {
                DownloadProgress.Value = percent;
                StatusText.Text = $"Downloading update... {percent} %";
            }));
            StatusText.Text = "Download complete. Plant Configurator will close and restart with the new version.";
            _downloading = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("UpdateDialog.Download", ex);
            _downloading = false;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = "Download failed: " + ex.Message;
        }
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // The download cannot be interrupted halfway; closing during it would leave a partial package.
        if (_downloading)
        {
            e.Cancel = true;
        }
    }
}
