using System.Runtime.InteropServices;

namespace CwcConfigurator.Services.TiaSync;

public static class TiaPortalWindowService
{
    private const int SwMinimize = 6;

    public static void MinimizePortalWindow(TiaAttachedSession? session)
    {
        if (session is null || session.ProcessId <= 0)
        {
            return;
        }

        foreach (var handle in EnumerateTopLevelWindows(session.ProcessId))
        {
            ShowWindow(handle, SwMinimize);
        }
    }

    private static IEnumerable<IntPtr> EnumerateTopLevelWindows(int processId)
    {
        var handles = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var windowProcessId);
            if (windowProcessId == processId && IsWindowVisible(handle))
            {
                handles.Add(handle);
            }

            return true;
        }, IntPtr.Zero);
        return handles;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
