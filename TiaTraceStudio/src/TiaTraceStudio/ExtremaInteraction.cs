using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using ScottPlot.WPF;

namespace TiaTraceStudio;

/// <summary>Consumes label gestures before ScottPlot can pan the underlying graph.</summary>
public sealed class ExtremaInteraction
{
    readonly WpfPlot plot;
    readonly Action changed;
    ExtremeLabel? dragging;
    ExtremaOverlay? overlay;
    float grabX, grabY, startX, startY;
    bool moved, inputWasEnabled, suppressRightUp;
    public bool IsDragging => dragging != null;

    public ExtremaInteraction(WpfPlot plot, Action changed)
    {
        this.plot = plot; this.changed = changed;
        plot.PreviewMouseDown += OnMouseDown;
        plot.PreviewMouseMove += OnMouseMove;
        plot.PreviewMouseUp += OnMouseUp;
        plot.LostMouseCapture += (_, _) => EndDrag();
        plot.PreviewMouseWheel += (_, e) => { if (IsDragging) e.Handled = true; };
    }
    (float X, float Y) Position(MouseEventArgs e)
    {
        var pixel = plot.GetPlotPixelPosition(e);
        float scale = (float)plot.Plot.ScaleFactor;
        return (pixel.X / scale, pixel.Y / scale);
    }
    ExtremaOverlay? CurrentOverlay => plot.Plot.GetPlottables<ExtremaOverlay>().FirstOrDefault();

    // Coordinates use ScottPlot logical pixels (after DPI and plot-scale conversion).
    public bool BeginDrag(float x, float y)
    {
        overlay = CurrentOverlay;
        dragging = overlay?.HitTest(x, y);
        if (dragging == null) return false;
        grabX = x - dragging.Bounds.Left; grabY = y - dragging.Bounds.Top; startX = x; startY = y; moved = false;
        inputWasEnabled = plot.UserInputProcessor.IsEnabled;
        plot.UserInputProcessor.Disable();
        plot.Cursor = Cursors.SizeAll;
        return true;
    }
    public void DragTo(float x, float y)
    {
        if (dragging == null || overlay == null) return;
        if (!moved && Math.Abs(x - startX) + Math.Abs(y - startY) < 3) return;
        moved = true;
        overlay.MoveLabel(dragging, x - grabX, y - grabY);
        plot.Refresh();
    }
    public void EndDrag()
    {
        if (dragging == null) return;
        bool commit = moved;
        dragging = null; overlay = null; moved = false;
        if (inputWasEnabled) plot.UserInputProcessor.Enable();
        plot.Cursor = Cursors.Arrow;
        if (plot.IsMouseCaptured) plot.ReleaseMouseCapture();
        if (commit) changed();
    }
    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        var (x, y) = Position(e);
        if (e.ChangedButton == MouseButton.Left && BeginDrag(x, y))
        { e.Handled = true; plot.CaptureMouse(); }
        else if (e.ChangedButton == MouseButton.Right)
        {
            var label = CurrentOverlay?.HitTest(x, y);
            if (label?.Point.Options == null) return;
            e.Handled = true; suppressRightUp = true;
            bool wasEnabled = plot.UserInputProcessor.IsEnabled;
            plot.UserInputProcessor.Disable();
            var menu = CreateMenu(label);
            menu.PlacementTarget = plot; menu.Placement = PlacementMode.MousePoint;
            menu.Closed += (_, _) => { suppressRightUp = false; if (wasEnabled) plot.UserInputProcessor.Enable(); };
            menu.IsOpen = true;
        }
    }
    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.Handled) return;
        var (x, y) = Position(e);
        if (IsDragging) { DragTo(x, y); e.Handled = true; return; }
        plot.Cursor = CurrentOverlay?.HitTest(x, y) != null ? Cursors.SizeAll : Cursors.Arrow;
    }
    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right && suppressRightUp) { suppressRightUp = false; e.Handled = true; }
        if (e.ChangedButton == MouseButton.Left && IsDragging) { e.Handled = true; EndDrag(); }
    }
    public ContextMenu CreateMenu(ExtremeLabel label)
    {
        var menu = new ContextMenu();
        var hide = new MenuItem { Header = "Sakrij ovaj oblačić" };
        hide.Click += (_, _) => { if (label.Point.Options != null) { label.Point.Options.Hidden = true; changed(); } };
        var reset = new MenuItem { Header = "Vrati automatski položaj", IsEnabled = label.Point.Options?.IsManual == true };
        reset.Click += (_, _) => { label.Point.Options?.ResetPosition(); changed(); };
        menu.Items.Add(hide); menu.Items.Add(reset);
        return menu;
    }
}
