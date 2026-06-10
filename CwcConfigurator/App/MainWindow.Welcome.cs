using System.Windows;
using System.Windows.Input;
using CwcConfigurator.Services;
using Microsoft.Win32;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    private void ShowWelcomeScreen()
    {
        if (WelcomeOverlay is null)
        {
            return;
        }

        var recents = _recentProjectsService.Load();
        WelcomeRecentList.ItemsSource = recents;
        WelcomeNoRecentText.Visibility = recents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeOverlay.Visibility = Visibility.Visible;
    }

    private void HideWelcomeScreen()
    {
        if (WelcomeOverlay is not null)
        {
            WelcomeOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void WelcomeNew_Click(object sender, RoutedEventArgs e)
    {
        NewProject_Click(sender, e);
        HideWelcomeScreen();
    }

    private void WelcomeOpen_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSavePendingChanges())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Plant Configurator project (*.plantcfg)|*.plantcfg|JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "plantcfg"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        OpenProjectFromPath(dialog.FileName);
    }

    private void WelcomeImportExcel_Click(object sender, RoutedEventArgs e)
    {
        ImportExcel_Click(sender, e);
        HideWelcomeScreen();
    }

    private void WelcomeRecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedRecent();
    }

    private void WelcomeRecentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedRecent();
            e.Handled = true;
        }
    }

    private void OpenSelectedRecent()
    {
        if (WelcomeRecentList.SelectedItem is not RecentProjectEntry entry)
        {
            return;
        }

        if (!ConfirmSavePendingChanges())
        {
            return;
        }

        OpenProjectFromPath(entry.Path);
    }

    private async void OpenProjectFromPath(string path)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            // Decrypt + deserialize off the UI thread; binding stays on it.
            _project = await RunBusyAsync($"Opening {System.IO.Path.GetFileName(path)}...", () => _projectFileService.Load(path));
            EnsureProjectDefaults();
            _projectPath = path;
            _projectOpen = true;
            BindProject();
            MarkProjectClean();
            _recentProjectsService.Add(path);
            ShowMessage($"Opened project: {path}");
            HideWelcomeScreen();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }
}
