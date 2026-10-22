using System.Windows;

namespace TiaTraceStudio;

/// <summary>Dialog sizes: as large as designed, but never larger than the screen's work area.</summary>
static class Dialogs
{
    public static double Width(double wanted) => Math.Min(wanted, SystemParameters.WorkArea.Width * 0.95);
    public static double Height(double wanted) => Math.Min(wanted, SystemParameters.WorkArea.Height * 0.92);
}
