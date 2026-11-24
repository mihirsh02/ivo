using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// A row of tabs at their natural widths, scrolled sideways by the mouse wheel (up/down or a tilt/touchpad swipe)
// when they don't all fit. No scrollbar; the active tab is kept in view.
public class TabStrip : Panel
{
    const double Max = 240;
    double offset, total;

    public TabStrip() { Background = Brushes.Transparent; ClipToBounds = true; }

    protected override Size MeasureOverride(Size available)
    {
        total = 0; double h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(Max, available.Height));
            total += Math.Min(Max, c.DesiredSize.Width); h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? total : Math.Min(total, available.Width), h);
    }

    protected override Size ArrangeOverride(Size size)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, total - size.Width));
        double x = -offset;
        foreach (UIElement c in InternalChildren)
        {
            var w = Math.Min(Max, c.DesiredSize.Width);
            c.Arrange(new Rect(x, 0, w, size.Height)); x += w;
        }
        return size;
    }

    public void ScrollBy(double delta) { offset += delta; InvalidateArrange(); }

    // Scroll just enough for this tab to be fully visible.
    public void Reveal(UIElement tab)
    {
        UpdateLayout();
        double x = 0;
        foreach (UIElement c in InternalChildren)
        {
            var w = Math.Min(Max, c.DesiredSize.Width);
            if (c == tab)
            {
                if (x < offset) offset = x;
                else if (x + w > offset + ActualWidth) offset = x + w - ActualWidth;
                InvalidateArrange();
                return;
            }
            x += w;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (total <= ActualWidth) return;
        ScrollBy(-e.Delta * 0.8);
        e.Handled = true;
    }
}
