using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Ivo;

// Ivo's own frame for a window, in Ivo and Ivo Portal: no Windows title bar, a rounded dark card with the window's
// title and a round close at the top (drag it by the top, Esc closes), and Ivo's message box made the same way.
public static class IvoFrame
{
    static Brush Res(string key, string fallback) => Application.Current?.TryFindResource(key) as Brush ?? (Brush)new BrushConverter().ConvertFromString(fallback)!;

    // web: the window holds a web view, which can't draw in a see-through window; it gets Windows 11's own rounded
    // corners on a solid dark window instead of the floating card
    public static void Apply(Window w, bool web = false)
    {
        var content = w.Content as UIElement;
        w.Content = null;
        w.WindowStyle = WindowStyle.None;
        if (!web) { w.AllowsTransparency = true; w.Background = Brushes.Transparent; }
        else
        {
            w.Background = Res("BoxCard", "#1B2128");
            w.SourceInitialized += (_, _) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; int round = 2, dark = 1;
                DwmSetWindowAttribute(h, 33, ref round, 4); DwmSetWindowAttribute(h, 20, ref dark, 4);
            };
        }
        WindowChrome.SetWindowChrome(w, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                                                           ResizeBorderThickness = w.ResizeMode == ResizeMode.CanResize ? new Thickness(8) : new Thickness(0), UseAeroCaptionButtons = false });
        var fg = Res("Fg", "#E6EDF3");
        var title = new TextBlock { Text = w.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center };
        var close = Round("", "Close", () => w.Close());
        var head = new DockPanel { Margin = new Thickness(18, 12, 12, 6), Background = Brushes.Transparent };
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close); head.Children.Add(title);
        head.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) try { w.DragMove(); } catch { } };
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        if (content != null) body.Children.Add(content);
        w.Content = web ? body : new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(12), BorderThickness = new Thickness(1), ClipToBounds = true,
                                 Background = Res("BoxCard", "#1B2128"), BorderBrush = Res("GlassStroke", "#33FFFFFF"), Child = body,
                                 Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black } };
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } };
    }

    // a round button with one glyph, in the theme: a soft bubble that lights up under the mouse
    public static Button Round(string glyph, string tip, Action click, double size = 32)
    {
        var face = new FrameworkElementFactory(typeof(Border), "Bd");
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        var soft = Res("InputBg", "#26303A"); var lit = new SolidColorBrush(Color.FromArgb(0x55, 0x8B, 0x7C, 0xFF));
        var b = new Button { Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size * 0.36, Width = size, Height = size, Cursor = Cursors.Hand, ToolTip = tip,
                             Foreground = Res("Fg", "#E6EDF3"), Background = soft, Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.MouseEnter += (_, _) => b.Background = lit; b.MouseLeave += (_, _) => b.Background = soft;
        b.Click += (_, _) => click();
        return b;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    // a short list to pick one from, in a rounded popup by `at`; nothing to pick says so
    public static void PickFrom(FrameworkElement at, string heading, IList<string> names, Action<string> picked)
    {
        var list = new StackPanel { Margin = new Thickness(6) };
        var pop = new System.Windows.Controls.Primitives.Popup { PlacementTarget = at, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false, AllowsTransparency = true, VerticalOffset = -8,
                                                               PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
                                                               Child = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), MinWidth = 220, Background = Res("BoxCard", "#1B2128"), BorderBrush = Res("GlassStroke", "#33FFFFFF"), Child = list,
                                                                                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black } } };
        list.Children.Add(new TextBlock { Text = heading, FontSize = 12, Foreground = Res("Dim", "#9AA4AF"), Margin = new Thickness(10, 4, 10, 6) });
        if (names.Count == 0) list.Children.Add(new TextBlock { Text = "No one else in your contacts", FontSize = 13, Foreground = Res("Fg", "#E6EDF3"), Margin = new Thickness(10, 4, 10, 8) });
        foreach (var n in names)
        {
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 14, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                                   Child = new TextBlock { Text = n, FontSize = 13.5, Foreground = Res("Fg", "#E6EDF3") } };
            row.MouseEnter += (_, _) => row.Background = Res("InputBg", "#26303A"); row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { pop.IsOpen = false; picked(n); };
            list.Children.Add(row);
        }
        pop.IsOpen = true;
    }

    // Ivo's message box: its words, then OK (and Cancel when asked for); true when OK was chosen
    public static bool Message(Window? owner, string text, string title, string ok = "OK", string? cancel = null)
    {
        var chosen = false;
        var w = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                             WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, Owner = owner?.IsVisible == true ? owner : null,
                             FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        Button Pill(string label, bool main)
        {
            var face = new FrameworkElementFactory(typeof(Border));
            face.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
            face.SetValue(Border.PaddingProperty, new Thickness(18, 7, 18, 8));
            face.SetValue(Border.BackgroundProperty, main ? Res("AccentGrad", "#7C5CFF") : Res("InputBg", "#26303A"));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            face.AppendChild(cp);
            var b = new Button { Content = label, FontWeight = FontWeights.SemiBold, Foreground = main ? Brushes.White : Res("Fg", "#E6EDF3"), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                                 IsDefault = main, IsCancel = !main, Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
            b.MouseEnter += (_, _) => b.Opacity = 0.85; b.MouseLeave += (_, _) => b.Opacity = 1;
            b.Click += (_, _) => { chosen = main; w.Close(); };
            return b;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (cancel != null) buttons.Children.Add(Pill(cancel, false));
        buttons.Children.Add(Pill(ok, true));
        w.Content = new StackPanel { Margin = new Thickness(20, 4, 20, 18), Children = {
            new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = Res("Fg", "#E6EDF3") }, buttons } };
        Apply(w);
        w.ShowDialog();
        return chosen;
    }
}
