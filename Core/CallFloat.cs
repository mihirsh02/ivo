using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ivo;

// The call, floating: a small always-on-top tile in the corner of the screen whenever the call isn't on show (the app
// minimised, in the tray, behind another app, or the call tucked away inside it), as Discord's pop-out. Their video (or
// their initial), their name, the call's time and a red "live" dot; on hover: mute, camera, share screen, back to the
// call in the app, full screen, and hang up. Drag it anywhere; a double click opens the call in the app.
public class CallFloat : Window
{
    readonly Portal portal;
    readonly Image theirs = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock time = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDA)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button mute, cam, screen;
    readonly DispatcherTimer tick;
    readonly Func<string> elapsed;
    WriteableBitmap? bmp, smallBmp;
    // their screen and camera both on: one big, the other small in the corner; a click on the small one swaps them
    readonly Image small = new() { Stretch = Stretch.UniformToFill };
    readonly Border smallBox;
    bool cameraBig;
    bool pending;

    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public CallFloat(Portal portal, string peer, Func<string> elapsed, Action open, Action fullScreen, Func<string, Task> video, Action hangUp, string? pic = null)
    {
        this.portal = portal; this.elapsed = elapsed;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Width = 340; Height = 210; Title = $"Call with {peer}";
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20; Top = area.Bottom - Height - 20;

        var initial = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                   Background = new LinearGradientBrush(Color.FromRgb(0x8B, 0x7C, 0xFF), Color.FromRgb(0x4F, 0x46, 0xE5), 45),
                                   Child = new TextBlock { Text = peer.Length > 0 ? peer[..1].ToUpper() : "?", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (PortalCallView.PicBrush(pic, 128) is { } photo) { initial.Background = photo; initial.Child = null; }   // their picture, when they have one

        // always there: a red live dot, their name, the time
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var name = new TextBlock { Text = peer, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        var label = new Border { CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Padding = new Thickness(9, 4, 10, 5), Margin = new Thickness(10),
                                 HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                                 Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, name, time } } };

        // on hover: the controls, over a dark fade at the bottom
        mute = Round("", "Mute", "#2B3641", () => { portal.Mute(!portal.Muted); Refresh(); });
        cam = Round("", "Turn camera on", "#2B3641", async () => { await video(portal.CameraOn ? "camera-off" : "camera"); Refresh(); });
        screen = Round("", "Share your screen", "#2B3641", async () => { await video(portal.Sharing != null ? "screen-off" : "screen"); Refresh(); });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12),
                                     Children = { mute, cam, screen, Round("", "Open the call in Ivo", "#2B3641", open), Round("", "Full screen", "#2B3641", fullScreen), Round("", "Hang up", "#F43F5E", hangUp) } };
        var shade = new Border { Height = 90, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
                                 Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xC0, 0, 0, 0), 90) };
        var hover = new Grid { Opacity = 0, Children = { shade, tools } };
        void Fade(double to) => hover.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));

        // ✕ in the corner: the tile goes, the call doesn't (it's back the next time the call goes out of sight)
        var close = Round("", "Close (the call goes on)", "#80000000", () => { Dismissed = true; Close(); });
        close.Width = close.Height = 28; close.FontSize = 10; close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; close.Margin = new Thickness(0, 10, 10, 0);
        hover.Children.Add(close);
        smallBox = new Border { Width = 96, Height = 60, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Brushes.Black, Child = small, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
                                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 44, 10, 0), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                                ToolTip = "Swap the screen and the camera" };
        smallBox.SizeChanged += (_, _) => small.Clip = new RectangleGeometry(new Rect(0, 0, smallBox.ActualWidth, smallBox.ActualHeight), 8, 8);
        smallBox.MouseLeftButtonDown += (_, e) => { cameraBig = !cameraBig; e.Handled = true; };
        var inner = new Grid { Background = new RadialGradientBrush(Color.FromRgb(0x2A, 0x21, 0x63), Color.FromRgb(0x0B, 0x0F, 0x14)), Children = { initial, theirs, label, hover, smallBox } };
        inner.SizeChanged += (_, _) => inner.Clip = new RectangleGeometry(new Rect(0, 0, inner.ActualWidth, inner.ActualHeight), 16, 16);
        var card = new Border { Margin = new Thickness(10), CornerRadius = new CornerRadius(16), Child = inner, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xA7, 0x8B, 0xFA)),
                                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black } };
        Content = card;
        card.MouseEnter += (_, _) => Fade(1);
        card.MouseLeave += (_, _) => Fade(0);

        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) open(); else try { DragMove(); } catch { } };
        portal.TheirFrame += Frame;
        tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        tick.Start();
        Closed += (_, _) => { tick.Stop(); portal.TheirFrame -= Frame; };
        Refresh();
    }

    public bool Dismissed { get; private set; }

    public void TheirVideoStopped() { bmp = smallBmp = null; theirs.Source = small.Source = null; smallBox.Visibility = Visibility.Collapsed; }

    // each switch shows what it is now: lit (violet) when on, red when you're muted
    void Refresh()
    {
        time.Text = elapsed();
        mute.Content = portal.Muted ? "" : ""; mute.ToolTip = portal.Muted ? "Unmute" : "Mute";
        mute.Background = Fill(portal.Muted ? "#F43F5E" : "#2B3641");
        cam.Content = portal.CameraOn ? "" : ""; cam.ToolTip = portal.CameraOn ? "Turn camera off" : "Turn camera on";
        cam.Background = Fill(portal.CameraOn ? "#7C5CFF" : "#2B3641");
        screen.ToolTip = portal.Sharing != null ? "Stop sharing" : "Share your screen";
        screen.Background = Fill(portal.Sharing != null ? "#7C5CFF" : "#2B3641");
    }
    static Brush Fill(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    void Frame(byte[] bgr, int w, int h)
    {
        if (pending) return; pending = true;
        Dispatcher.BeginInvoke(() =>
        {
            pending = false;
            var (s, c) = portal.TheirSplit;
            int sw = (int)Math.Round(w * s), ch = Math.Min(h, (int)Math.Round(h * c));
            if (sw > 0 && sw < w && ch > 0)
            {
                var scr = PortalCallView.Crop(bgr, w, 0, 0, sw, h); var cam = PortalCallView.Crop(bgr, w, sw, 0, w - sw, ch);
                bmp = cameraBig ? PortalCallView.Paint(bmp, cam, w - sw, ch) : PortalCallView.Paint(bmp, scr, sw, h);
                smallBmp = cameraBig ? PortalCallView.Paint(smallBmp, scr, sw, h) : PortalCallView.Paint(smallBmp, cam, w - sw, ch);
                small.Source = smallBmp; smallBox.Visibility = Visibility.Visible;
            }
            else { bmp = PortalCallView.Paint(bmp, bgr, w, h); smallBox.Visibility = Visibility.Collapsed; }
            theirs.Source = bmp;
        });
    }

    static Button Round(string glyph, string tip, string hex, Action click)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        var b = new Button { Content = glyph, FontFamily = Icons, FontSize = 14, Foreground = Brushes.White, Width = 36, Height = 36, Margin = new Thickness(3, 0, 3, 0), ToolTip = tip, Cursor = Cursors.Hand, Focusable = false,
                             Background = Fill(hex), Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.MouseEnter += (_, _) => b.Opacity = 0.85; b.MouseLeave += (_, _) => b.Opacity = 1;
        b.Click += (_, _) => click();
        return b;
    }
}
