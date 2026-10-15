using System.Windows.Input;
using ScottPlot.WPF;

namespace TiaTraceStudio;

public sealed class CursorInteraction
{
    readonly WpfPlot plot;
    readonly ChartOptions options;
    readonly double first, last;
    readonly Action preview, completed;
    string? dragging;
    bool inputWasEnabled, moved;
    public bool IsDragging => dragging != null;
    public CursorInteraction(WpfPlot plot, ChartOptions options, double first, double last, Action preview, Action completed)
    {
        this.plot = plot; this.options = options; this.first = first; this.last = last; this.preview = preview; this.completed = completed;
        plot.PreviewMouseDown += (_, e) =>
        {
            if (e.Handled || e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None) return;
            var (x, y) = Position(e);
            if (plot.Plot.GetPlottables<ExtremaOverlay>().Any(o => o.HitTest(x, y) != null)) return;
            if (BeginDrag(x, y)) { e.Handled = true; plot.CaptureMouse(); }
        };
        plot.PreviewMouseMove += (_, e) =>
        {
            if (e.Handled) return;
            var (x, y) = Position(e);
            if (IsDragging) { DragTo(x); e.Handled = true; }
            else if (Overlay?.HitTest(x, y) != null) plot.Cursor = Cursors.SizeWE;
        };
        plot.PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && IsDragging) { e.Handled = true; EndDrag(); } };
        plot.LostMouseCapture += (_, _) => EndDrag();
        plot.PreviewMouseWheel += (_, e) => { if (IsDragging) e.Handled = true; };
    }
    CursorReadoutOverlay? Overlay => plot.Plot.GetPlottables<CursorReadoutOverlay>().FirstOrDefault();
    (float X, float Y) Position(MouseEventArgs e)
    { var p = plot.GetPlotPixelPosition(e); float scale = (float)plot.Plot.ScaleFactor; return (p.X / scale, p.Y / scale); }
    public bool BeginDrag(float x, float y)
    {
        dragging = Overlay?.HitTest(x, y); if (dragging == null) return false;
        moved = false; inputWasEnabled = plot.UserInputProcessor.IsEnabled;
        plot.UserInputProcessor.Disable(); plot.Cursor = Cursors.SizeWE; return true;
    }
    public void DragTo(float x)
    {
        if (dragging == null || Overlay == null) return;
        double time = options.FromAxisTime(Overlay.Axes.GetCoordinateX(x));
        time = Math.Clamp(time, first, last);
        if (dragging == "A") { if (options.CursorA == time) return; options.CursorA = time; }
        else { if (options.CursorB == time) return; options.CursorB = time; }
        moved = true; preview();
    }
    public void EndDrag()
    {
        if (dragging == null) return;
        dragging = null; bool commit = moved; moved = false;
        if (inputWasEnabled) plot.UserInputProcessor.Enable();
        plot.Cursor = Cursors.Arrow; if (plot.IsMouseCaptured) plot.ReleaseMouseCapture();
        if (commit) completed();
    }
}
