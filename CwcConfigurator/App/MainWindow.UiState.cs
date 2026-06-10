using System.Windows;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    private readonly UiStateService _uiStateService = new();

    private void RestoreUiState()
    {
        var state = _uiStateService.Load();

        if (state.WindowWidth >= MinWidth && state.WindowHeight >= MinHeight)
        {
            Width = state.WindowWidth;
            Height = state.WindowHeight;
        }

        // Only restore a position that is still at least partially on the
        // virtual desktop (monitors may have been unplugged since last run).
        if (!double.IsNaN(state.WindowLeft) && !double.IsNaN(state.WindowTop)
            && state.WindowLeft > SystemParameters.VirtualScreenLeft - Width + 80
            && state.WindowTop > SystemParameters.VirtualScreenTop - 40
            && state.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
            && state.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = state.WindowLeft;
            Top = state.WindowTop;
        }

        if (state.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        if (state.PlcPaneWidth >= PlcPaneColumn.MinWidth)
        {
            PlcPaneColumn.Width = new GridLength(state.PlcPaneWidth);
        }
    }

    private void SaveUiState()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        _uiStateService.Save(new UiState
        {
            WindowLeft = bounds.Left,
            WindowTop = bounds.Top,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            WindowMaximized = WindowState == WindowState.Maximized,
            PlcPaneWidth = PlcPaneColumn.Width.IsAbsolute ? PlcPaneColumn.Width.Value : 0
        });
    }
}
