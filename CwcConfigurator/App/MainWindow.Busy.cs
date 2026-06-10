using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CwcConfigurator.App;

/// <summary>
/// Busy-overlay plumbing for long-running operations plus transient toast
/// notifications. Long operations run via <see cref="RunBusyAsync"/> on a
/// background thread while the overlay blocks the workspace; the menu and
/// toolbar are disabled so no second operation can start mid-flight.
/// </summary>
public partial class MainWindow
{
    private int _busyDepth;
    private DispatcherTimer? _toastTimer;

    private bool IsBusy => _busyDepth > 0;

    private async Task RunBusyAsync(string message, Action work)
    {
        ShowBusyOverlay(message);
        try
        {
            await Task.Run(work);
        }
        finally
        {
            HideBusyOverlay();
        }
    }

    private async Task<T> RunBusyAsync<T>(string message, Func<T> work)
    {
        ShowBusyOverlay(message);
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            HideBusyOverlay();
        }
    }

    private void ShowBusyOverlay(string message)
    {
        _busyDepth++;
        BusyOverlayText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;
        MainMenuBar.IsEnabled = false;
        MainToolbar.IsEnabled = false;
    }

    private void HideBusyOverlay()
    {
        _busyDepth = Math.Max(0, _busyDepth - 1);
        if (_busyDepth > 0)
        {
            return;
        }

        BusyOverlay.Visibility = Visibility.Collapsed;
        MainMenuBar.IsEnabled = true;
        MainToolbar.IsEnabled = true;
    }

    private void ShowToast(string message)
    {
        if (ToastHost is null || ToastText is null)
        {
            return;
        }

        ToastText.Text = message;

        if (_toastTimer is null)
        {
            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.2)
            };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                ToastHost.BeginAnimation(OpacityProperty, fadeOut);
            };
        }

        _toastTimer.Stop();
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(150));
        ToastHost.BeginAnimation(OpacityProperty, fadeIn);
        _toastTimer.Start();
    }
}
