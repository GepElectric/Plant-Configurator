using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CwcConfigurator.App;

/// <summary>
/// Lightweight adorner drawn over a DataGridCellsPresenter while the user is
/// drag-filling a column. Shows a dashed rectangle from the source cell down
/// (or up) to the current pointer cell — same visual cue Excel uses.
/// </summary>
internal sealed class DragFillAdorner : Adorner
{
    private static readonly Pen DashPen = CreatePen();

    private Rect _bounds;

    public DragFillAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    public void UpdateBounds(Rect bounds)
    {
        if (_bounds == bounds)
        {
            return;
        }

        _bounds = bounds;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_bounds.Width <= 0 || _bounds.Height <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(null, DashPen, _bounds);
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41)), 2)
        {
            DashStyle = new DashStyle(new double[] { 3, 2 }, 0)
        };
        pen.Freeze();
        return pen;
    }
}
