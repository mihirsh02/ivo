using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// The tray icon's right-click menu, drawn in WPF so it matches the app: rounded, soft, themed, with switches.
public class TrayMenu : Window
{
    readonly StackPanel list = new() { Margin = new Thickness(6) };

    public TrayMenu()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        // flat: one solid panel, no outline or shadow (the same look as the Inbox and Planner boxes)
        var card = new Border { CornerRadius = new CornerRadius(14), MinWidth = 270, Child = list };
        card.SetResourceReference(Border.BackgroundProperty, "BoxBg");
        Content = card;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontWeight = FontWeights.Medium;   // the same text as Ivo
        App.Crisp(this);
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    public void Header(string title, string? sub)
    {
        var icon = new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/ivo.ico")), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) };
        var t = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { t } };
        if (sub != null)
        {
            var s = new TextBlock { Text = sub, FontSize = 12, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis };
            s.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            text.Children.Add(s);
        }
        list.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 10), Children = { icon, text } });
    }

    public void Line()
    {
        var b = new Border { Height = 1, Margin = new Thickness(10, 4, 10, 4) };
        b.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        list.Children.Add(b);
    }

    Border Row(string glyph, string text, UIElement? right, Brush? color)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 14, Width = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        if (color != null) icon.Foreground = label.Foreground = color;
        else { icon.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); label.SetResourceReference(TextBlock.ForegroundProperty, "Fg"); }
        var dock = new DockPanel();
        if (right != null) { DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right); }
        dock.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } });
        var row = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 10, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = dock };
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x9C, 0x7C, 0xFF));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        list.Children.Add(row);
        return row;
    }

    public void Item(string glyph, string text, string keys, Action click, Brush? color = null)
    {
        TextBlock? hint = null;
        if (keys != "")
        {
            hint = new TextBlock { Text = keys, FontSize = 12, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        }
        Row(glyph, text, hint, color).MouseLeftButtonUp += (_, _) => { Hide(); click(); };
    }

    // a small pill switch on the right; stays open so the change is visible
    public void Switch(string glyph, string text, bool on, Action<bool> set)
    {
        var knob = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left,
                                RenderTransform = new TranslateTransform(on ? 16 : 0, 0) };
        var track = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Center, Child = knob };
        void Paint()
        {
            if (on) track.Background = (Brush)Application.Current.FindResource("AccentGrad");
            else track.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x90));
            knob.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 16 : 0, TimeSpan.FromMilliseconds(140)));
        }
        Paint();
        Row(glyph, text, track, null).MouseLeftButtonUp += (_, _) => { on = !on; Paint(); set(on); };
    }

    public void Clear() => list.Children.Clear();

    // above the cursor (the taskbar is usually at the bottom), kept on screen
    public void ShowAtCursor()
    {
        Show(); UpdateLayout();
        var p = System.Windows.Forms.Cursor.Position;
        var src = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var pt = src.Transform(new Point(p.X, p.Y));
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(pt.X - ActualWidth, area.Left, area.Right - ActualWidth);
        Top = Math.Clamp(pt.Y - ActualHeight, area.Top, area.Bottom - ActualHeight);
        Activate();
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }
}
