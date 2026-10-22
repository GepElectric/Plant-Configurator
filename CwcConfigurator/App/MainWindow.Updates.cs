using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>
/// Help menu and application updates: checks GitHub Releases shortly after start and every few hours,
/// offers the update and restarts into the new version. Mandatory releases cannot be postponed.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan MandatoryReminderDelay = TimeSpan.FromMinutes(2);

    private static readonly string AppTitle = $"Plant Configurator {AppUpdateService.AssemblyVersion}";

    private readonly AppUpdateService _updateService = new();
    private readonly DispatcherTimer _updateTimer = new();
    private bool _updateCheckRunning;
    private bool _updateWaitingForExit;

    private void InitializeUpdates()
    {
        Title = AppTitle;
        _updateTimer.Interval = FirstUpdateCheckDelay;
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = UpdateCheckInterval;
            await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckRunning || _updateWaitingForExit)
        {
            return;
        }

        if (!_updateService.IsInstalled)
        {
            if (manual)
            {
                var answer = MessageBox.Show(this,
                    "This copy of Plant Configurator was not installed with Setup.exe, so it cannot update itself.\n\n" +
                    "Install it once from GitHub Releases (GepElectric.PlantConfigurator-win-Setup.exe); after that updates are installed automatically.\n\n" +
                    "Open the releases page now?",
                    "Check for updates", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    OpenUrl(AppUpdateService.ReleasesUrl);
                }
            }

            return;
        }

        _updateCheckRunning = true;
        try
        {
            var update = await _updateService.CheckAsync();
            if (update is null)
            {
                if (manual)
                {
                    MessageBox.Show(this, $"Plant Configurator {_updateService.CurrentVersion} is the latest version.",
                        "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            CwcApplication.Log($"Update available: {_updateService.CurrentVersion} -> {update.Version} (mandatory={update.IsMandatory}).");
            ShowUpdateDialog(update);
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("CheckForUpdates", ex);
            if (manual)
            {
                ShowError("Could not check for updates: " + ex.Message);
            }
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private void ShowUpdateDialog(AppUpdate update)
    {
        var dialog = new UpdateDialog(_updateService, update) { Owner = this };
        var install = dialog.ShowDialog() == true;
        if (install)
        {
            _updateService.ApplyWhenClosed(update);
            _updateWaitingForExit = true;
            CwcApplication.Log($"Update {update.Version} downloaded; closing to install.");
            Close();
            if (!_closingConfirmed)
            {
                MessageBox.Show(this,
                    $"Version {update.Version} is downloaded. It is installed as soon as you close Plant Configurator.",
                    "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        if (!update.IsMandatory)
        {
            return;
        }

        // Mandatory update declined: the app closes; if the user cancels closing, ask again shortly.
        Close();
        if (!_closingConfirmed)
        {
            _updateTimer.Stop();
            _updateTimer.Interval = MandatoryReminderDelay;
            _updateTimer.Start();
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(CwcApplication.LogFilePath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            ShowMessage("Log folder not found.");
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var install = _updateService.IsInstalled ? "Installed with Setup.exe (updates automatically)" : "Not installed with Setup.exe (no automatic updates)";
        MessageBox.Show(this,
            $"Plant Configurator {_updateService.CurrentVersion}\n\n{install}\n\nGeppert Electric\n{AppUpdateService.RepositoryUrl}",
            "About Plant Configurator", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
