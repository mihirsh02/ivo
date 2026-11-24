using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// Right-click a message, in Portal's chats and in Ivo's own chat: the window blurs, the message lifts out a little, its
// reactions above (where there are any) and what you can do below, with when it was sent on top. A click anywhere else,
// a right-click or Esc puts it back. A message too tall to fit with them shows only its top, faded where it's cut.
public static class MessageLift
{
    public sealed record Item(string Glyph, string Text, Action Go, bool Danger = false);

    static Brush Res(string key, string fallback) => Application.Current?.TryFindResource(key) as Brush ?? (Brush)new BrushConverter().ConvertFromString(fallback)!;

    public static string When(DateTime sent) =>
        sent.Date == DateTime.Today ? $"Today, {sent:t}" : sent.Year == DateTime.Today.Year ? $"{sent:ddd, d MMM} · {sent:t}" : $"{sent:d MMM yyyy} · {sent:t}";

    public static void Show(FrameworkElement body, bool mine, DateTime sent, IList<Item> items,
                            IReadOnlyList<string>? reactions = null, string? yours = null, Action<string>? react = null)
    {
        Grid? host = null;
        for (DependencyObject? d = body; d != null; d = VisualTreeHelper.GetParent(d)) if (d is Grid g) host = g;   // the whole window
        if (host == null || body.ActualWidth <= 0 || host.ActualWidth <= 0) return;
        var at = body.TranslatePoint(new Point(0, 0), host);
        double w = body.ActualWidth, h = body.ActualHeight, W = host.ActualWidth, H = host.ActualHeight;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var pop = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        DoubleAnimation To(double v, int ms, IEasingFunction? f = null) => new(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = f ?? ease };
        var fg = Res("Fg", "#E6EDF3"); var dim = Res("Dim", "#9AA4AF"); var card = Res("PopupBg", "#1B2128"); var stroke = Res("GlassStroke", "#33FFFFFF"); var hover = Res("InputBg", "#26303A");
        System.Windows.Media.Effects.DropShadowEffect Shadow() => new() { BlurRadius = 18, ShadowDepth = 3, Opacity = .45, Color = Colors.Black };

        // the window as it is, blurring in behind a dim veil
        var dpi = VisualTreeHelper.GetDpi(host);
        var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)(W * dpi.DpiScaleX)), Math.Max(1, (int)(H * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        shot.Render(host);
        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        var picture = new System.Windows.Controls.Image { Source = shot, Width = W, Height = H, Effect = blur };
        var layer = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), Opacity = 0, Focusable = true, ClipToBounds = true };
        Grid.SetRowSpan(layer, 99); Grid.SetColumnSpan(layer, 99); Panel.SetZIndex(layer, 2600);
        layer.Children.Add(picture);
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, To(14, 180));

        // the message, drawn again on top (sharp) and lifted
        var lifted = new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = new VisualBrush(body) { Stretch = Stretch.None },
                                                           RenderTransformOrigin = new Point(mine ? 1 : 0, 0.5), RenderTransform = new ScaleTransform(1, 1) };

        // the menu: when it was sent, then the choices
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = When(sent), FontSize = 12, Foreground = dim, Margin = new Thickness(10, 4, 10, 6) });
        var menu = new Border { Child = list, CornerRadius = new CornerRadius(12), Padding = new Thickness(6), MinWidth = 210, BorderThickness = new Thickness(1), Background = card, BorderBrush = stroke, Effect = Shadow(),
                                RenderTransformOrigin = new Point(mine ? 1 : 0, 0), RenderTransform = new ScaleTransform(0.6, 0.6), Opacity = 0 };

        var closing = false;
        void Close(Action? then = null)
        {
            if (closing) return;
            closing = true;
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, To(0, 160));
            ((ScaleTransform)lifted.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, To(1, 160));
            ((ScaleTransform)lifted.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, To(1, 160));
            var fade = To(0, 160);
            fade.Completed += (_, _) => { host.Children.Remove(layer); then?.Invoke(); };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        var red = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E));
        foreach (var item in items)
        {
            var brush = item.Danger ? red : fg;
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 14, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                                   Child = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                                       new TextBlock { Text = item.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = brush, Width = 24, VerticalAlignment = VerticalAlignment.Center },
                                       new TextBlock { Text = item.Text, FontSize = 13, Foreground = brush, VerticalAlignment = VerticalAlignment.Center } } } };
            row.MouseEnter += (_, _) => row.Background = hover; row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var go = item.Go;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(go); };
            list.Children.Add(row);
        }

        // reactions: a rounded bar of emoji above the message; yours is ringed
        Border? bar = null;
        if (reactions is { Count: > 0 } && react != null)
        {
            var faces = new StackPanel { Orientation = Orientation.Horizontal };
            bar = new Border { Child = faces, CornerRadius = new CornerRadius(22), Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(1), Background = card, BorderBrush = stroke, Effect = Shadow(),
                               RenderTransformOrigin = new Point(mine ? 1 : 0, 1), RenderTransform = new ScaleTransform(0.6, 0.6), Opacity = 0 };
            foreach (var emoji in reactions)
            {
                var face = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Cursor = Cursors.Hand, Margin = new Thickness(1, 0, 1, 0),
                                        Background = emoji == yours ? hover : Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
                                        Child = IvoChatsView.Emoji(emoji, 28), ToolTip = emoji == yours ? "Take back" : null };
                face.MouseEnter += (_, _) => { ((ScaleTransform)face.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, To(1.25, 120, pop)); ((ScaleTransform)face.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, To(1.25, 120, pop)); };
                face.MouseLeave += (_, _) => { ((ScaleTransform)face.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, To(1, 120)); ((ScaleTransform)face.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, To(1, 120)); };
                face.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(() => react(emoji)); };
                faces.Children.Add(face);
            }
        }

        // where it all goes: reactions above, the menu below; the whole stack slides to fit inside the window
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bar?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double rh = bar?.DesiredSize.Height ?? 0, mh = menu.DesiredSize.Height, gap = 10, rg = bar != null ? gap : 0;
        var room = H - 16 - rh - rg - mh - gap;
        if (h > room)   // too tall to fit with them: only its top lifts (as on a phone), so the menu is always in reach
        {
            h = Math.Max(48, room);
            lifted.Height = h;
            ((VisualBrush)lifted.Fill).AlignmentY = AlignmentY.Top;
            lifted.OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Black, 0), new(Colors.Black, Math.Max(0, 1 - 36 / h)), new(Colors.Transparent, 1) }, 90);
        }
        var top = at.Y - rh - rg;
        var bottom = at.Y + h + gap + mh;
        var shift = top < 8 ? 8 - top : bottom > H - 8 ? H - 8 - bottom : 0;
        if (top + shift < 8) shift = 8 - top;
        double X(double width) => Math.Clamp(mine ? at.X + w - width : at.X, 8, Math.Max(8, W - width - 8));
        Canvas.SetLeft(lifted, at.X); Canvas.SetTop(lifted, at.Y + shift);
        Canvas.SetLeft(menu, X(menu.DesiredSize.Width)); Canvas.SetTop(menu, at.Y + h + gap + shift);
        // a solid backing behind a see-through bubble, so the blur doesn't show through it
        if (body is Border { Background: SolidColorBrush { Color.A: > 0 and < 255 } })
        {
            var behind = Window.GetWindow(body)?.Background is SolidColorBrush { Color.A: 255 } solid ? solid : new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x1C));
            var backing = new Border { Width = w, Height = h, Background = behind, CornerRadius = ((Border)body).CornerRadius, RenderTransformOrigin = lifted.RenderTransformOrigin, RenderTransform = lifted.RenderTransform, OpacityMask = lifted.OpacityMask };
            Canvas.SetLeft(backing, at.X); Canvas.SetTop(backing, at.Y + shift);
            layer.Children.Add(backing);
        }
        layer.Children.Add(lifted); layer.Children.Add(menu);
        if (bar != null) { Canvas.SetLeft(bar, X(bar.DesiredSize.Width)); Canvas.SetTop(bar, top + shift); layer.Children.Add(bar); }
        layer.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource == layer || e.OriginalSource == picture) Close(); };
        layer.MouseRightButtonUp += (_, e) => { e.Handled = true; Close(); };
        layer.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        host.Children.Add(layer);
        layer.Focus();

        // in: the window dims and blurs, the message pops up a touch, the reactions and menu spring out of it
        layer.BeginAnimation(UIElement.OpacityProperty, To(1, 150));
        var lift = (ScaleTransform)lifted.RenderTransform;
        lift.BeginAnimation(ScaleTransform.ScaleXProperty, To(1.05, 260, pop)); lift.BeginAnimation(ScaleTransform.ScaleYProperty, To(1.05, 260, pop));
        foreach (var part in new FrameworkElement?[] { bar, menu })
        {
            if (part == null) continue;
            var grow = (ScaleTransform)part.RenderTransform;
            var g2 = To(1, 280, pop); g2.BeginTime = TimeSpan.FromMilliseconds(40);
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, g2); grow.BeginAnimation(ScaleTransform.ScaleYProperty, g2);
            part.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(40) });
        }
    }
}
