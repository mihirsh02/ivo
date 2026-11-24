using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// The Inbox and the planner, opened from the tray or a notification while Ivo's window is closed: the panel is lent to a
// small floating window of its own (with the reader and dialogs it may open), and goes back when that window closes.
public partial class MainWindow
{
    Window? popout;
    readonly List<(FrameworkElement El, int Index, Thickness Margin, double Backdrop)> lent = [];
    FrameworkElement? floating;

    bool MainHidden => !IsVisible;   // closed to the tray (minimised still counts as open: it's restored and shows them inside)

    void Float(FrameworkElement layer, double width, double height, Action open)
    {
        if (popout != null)
        {
            if (floating == layer) { open(); popout.Activate(); return; }
            popout.Close();   // the other panel was out: bring it home first
        }
        var root = (Panel)layer.Parent;
        var host = new Grid();
        var els = new[] { layer, ReaderLayer, DialogLayer };
        var spots = els.Select(root.Children.IndexOf).ToList();   // where each sat, taken before any is removed
        foreach (var (el, spot) in els.Zip(spots))
        {
            var backdrop = el is Panel { Children.Count: > 0 } p && p.Children[0] is Border b ? b : null;
            lent.Add((el, spot, el.Margin, backdrop?.Opacity ?? 1));
            root.Children.Remove(el);
            el.Margin = new Thickness(0);
            if (backdrop != null && el == layer) backdrop.Opacity = 0;   // no dimmed backdrop: the window is just the card
            host.Children.Add(el);
        }
        var area = SystemParameters.WorkArea;
        popout = new Window
        {
            Title = "Ivo", Icon = Icon, Content = host, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, Width = Math.Min(width, area.Width - 40), Height = Math.Min(height, area.Height - 40),
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true,
        };
        popout.Resources.MergedDictionaries.Add(Resources);
        popout.PreviewKeyDown += (s, e) => Window_KeyDown(s, e);   // Esc closes the reader, a dialog, then the panel
        // drag it by its top edge
        popout.MouseLeftButtonDown += (_, e) =>
        {
            if (e.GetPosition(popout).Y > 64) return;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ButtonBase or TextBox or ComboBox) return;
            try { popout.DragMove(); } catch { }
        };
        popout.Closed += (_, _) => ReturnLent();
        floating = layer;
        open();
        popout.Show(); popout.Activate();
        // when the panel closes (its own ×, Esc, or an action that goes on into a chat), the window goes too
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).AddValueChanged(layer, PanelClosed);
    }

    void PanelClosed(object? s, EventArgs e)
    {
        if (s is UIElement { Visibility: not Visibility.Visible } && popout != null) popout.Close();
    }

    void ReturnLent()
    {
        if (floating != null) DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).RemoveValueChanged(floating, PanelClosed);
        foreach (var (el, index, margin, backdrop) in lent.OrderBy(x => x.Index))
        {
            ((Panel)el.Parent).Children.Remove(el);
            el.Margin = margin;
            if (el is Panel { Children.Count: > 0 } p && p.Children[0] is Border b) b.Opacity = backdrop;
            Root.Children.Insert(Math.Min(index, Root.Children.Count), el);
        }
        lent.Clear(); popout = null; floating = null;
    }

    // the tray's planner items: floating when the window's closed, inside it otherwise
    void OpenPlannerAnywhere(string kind)
    {
        if (MainHidden) Float(kind == "todo" ? TodoLayer : ScheduleLayer, 580, 780, () => OpenPlanner(kind));
        else { ShowWindow(); OpenPlanner(kind); }
    }
}
