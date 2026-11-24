using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Ivo;

// Covers on a horizontal wheel (Spotify → Home → Your albums), moving like the Inbox's drum: the front cover full size,
// the ones beside it turning away (narrower, smaller, fainter, softer). A critically damped spring eases it between
// covers, so it never jumps or wobbles. Scroll, ← →, drag and flick to turn; click a side cover to bring it to the front,
// click the front one to open it, or its green button to play it. The front cover's colour washes down the whole Spotify view, and its name and artist show under it.
public class CoverWheel : Grid
{
    public record Item(string? Image, string Title, string Sub, Action Play, Action Open);

    readonly List<Item> items;
    readonly Canvas canvas = new() { ClipToBounds = false };
    readonly List<Border> cards = [];
    readonly TextBlock title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock sub = new() { FontSize = 12.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
    readonly double Cover;                 // the front cover's size
    const double Step = 34;                // degrees between covers on the wheel
    double pos, target, speed;
    int ticked;
    bool springing, dragging;
    DateTime frame;
    int shown = -1;
    readonly Button play;   // Spotify's green play button, on the front cover's corner

    // first: the cover at the front to begin with (Now playing starts on the song playing); cover: the front cover's size
    bool OnCard(DependencyObject? d)
    {
        for (; d != null && d != this; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (d is Border b && cards.Contains(b)) return true;
        return false;
    }

    public CoverWheel(IEnumerable<Item> source, int first = 0, double cover = 210)
    {
        items = source.ToList();
        Cover = cover;
        pos = target = Math.Clamp(first, 0, Math.Max(0, items.Count - 1));
        Height = Cover + 96;
        Focusable = true; FocusVisualStyle = null;
        Background = Brushes.Transparent;   // takes the wheel, drags and clicks anywhere on it
        Children.Add(canvas);
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(40, 0, 40, 4), Children = { title, sub }, IsHitTestVisible = false });
        for (int i = 0; i < items.Count; i++) cards.Add(Card(i));
        play = new Button { Width = 48, Height = 48, Content = "\uE768", FontFamily = App.IconFont, FontSize = 18, Foreground = Brushes.Black, Cursor = Cursors.Hand,
                            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60)), ToolTip = "Play", Focusable = false,
                            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                                "<Border Background='{TemplateBinding Background}' CornerRadius='24'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='2,0,0,0'/></Border></ControlTemplate>") };
        play.Click += (_, e) => { e.Handled = true; if (items.Count > 0) items[(int)Math.Round(target)].Play(); };
        Panel.SetZIndex(play, 5000);
        if (items.Count > 0) canvas.Children.Add(play);
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => { Layout(); Show((int)target); };
        // mouse wheel and touchpad: deltas add up, one cover per notch (120), so a touchpad's small steps turn it too
        // only over the covers themselves: elsewhere the page scrolls as usual
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) => { if (!OnCard(e.OriginalSource as DependencyObject)) return; e.Handled = true; Turn(-e.Delta); }), true);
        // a touchpad's sideways swipe comes as WM_MOUSEHWHEEL, which WPF doesn't route
        Loaded += (_, _) => (PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.AddHook(Hook);
        Unloaded += (_, _) => (PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.RemoveHook(Hook);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Right) { To(target + 1); e.Handled = true; }
            else if (e.Key == Key.Left) { To(target - 1); e.Handled = true; }
            else if (e.Key is Key.Enter or Key.Space && items.Count > 0) { items[(int)Math.Round(target)].Play(); e.Handled = true; }
        };
        // drag: the wheel follows the pointer; let go and it carries on with the flick's speed, then settles
        Point start = default; double startPos = 0, lastX = 0; DateTime lastT = default; var moved = false;
        MouseLeftButtonDown += (_, e) => { Focus(); start = e.GetPosition(this); startPos = pos; lastX = start.X; lastT = DateTime.Now; moved = false; dragging = true; speed = 0; CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var x = e.GetPosition(this).X;
            if (Math.Abs(x - start.X) > 4) moved = true;
            var now = DateTime.Now; var dt = (now - lastT).TotalSeconds;
            if (dt > 0) speed = -(x - lastX) / (Cover * 0.6) / dt;
            lastX = x; lastT = now;
            pos = Math.Clamp(Magnet.Pull(startPos - (x - start.X) / (Cover * 0.6)), -0.4, items.Count - 0.6);   // detents under the finger
            Layout();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false; ReleaseMouseCapture();
            if (moved) { To(pos + Math.Clamp(speed * 0.25, -6, 6)); return; }
            // a click: the front cover opens; one beside it comes to the front
            var x = e.GetPosition(this).X - ActualWidth / 2;
            var hit = (int)Math.Round(target + Math.Asin(Math.Clamp(x / Radius, -0.99, 0.99)) * 180 / Math.PI / Step);
            if (hit == (int)Math.Round(target) && Math.Abs(x) < Cover / 2) items[hit].Open(); else To(hit);
        };
        MouseRightButtonUp += (_, e) => { if (items.Count > 0) { items[(int)Math.Round(target)].Open(); e.Handled = true; } };
    }

    double notch;
    void Turn(double delta)
    {
        notch += delta;
        var steps = (int)(notch / 120);
        if (steps == 0) return;
        notch -= steps * 120;
        To(target + steps);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != 0x020E || !IsMouseOver) return IntPtr.Zero;   // WM_MOUSEHWHEEL over the wheel
        Turn((short)((long)w >> 16));
        handled = true;
        return IntPtr.Zero;
    }

    double Radius => Math.Max(260, ActualWidth / 2 - 40);

    Border Card(int i)
    {
        var it = items[i];
        var art = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Width = Cover, Height = Cover };
        art.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        if (it.Image is { } url)
            _ = SpotifyView.Image(url).ContinueWith(t =>
            {
                if (t.Result is not { } img) return;
                art.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                art.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));   // covers fade in as they arrive
                if (i == shown) Glow(img);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        var card = new Border { Child = art, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), IsHitTestVisible = false };
        canvas.Children.Add(card);
        return card;
    }

    // each cover's place from its angle to the front: across, size, turn (narrower), fade, softness, depth order
    void Layout()
    {
        var w = ActualWidth; if (w == 0) return;
        var front = (int)Math.Round(pos);
        Magnet.Passed(ref ticked, pos);
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var angle = (i - pos) * Step;
            if (Math.Abs(angle) >= 86 || Math.Abs(i - front) > 6) { card.Visibility = Visibility.Hidden; continue; }
            var rad = angle * Math.PI / 180;
            var depth = Math.Cos(rad);
            var scale = 0.55 + 0.45 * depth;
            card.Visibility = Visibility.Visible;
            Canvas.SetLeft(card, w / 2 + Math.Sin(rad) * Radius - Cover / 2);
            Canvas.SetTop(card, 8 + (1 - scale) * Cover / 2);
            var t = (ScaleTransform)card.RenderTransform;
            t.ScaleX = scale * (0.45 + 0.55 * depth);   // narrower as it turns away
            t.ScaleY = scale;
            card.Opacity = Math.Pow(depth, 1.6);
            Panel.SetZIndex(card, (int)(depth * 1000));
        }
        // the play button rides the front cover's corner, fading while the wheel is between covers
        Canvas.SetLeft(play, w / 2 + Cover / 2 - 60); Canvas.SetTop(play, 8 + Cover - 60);
        play.Opacity = Math.Clamp(1 - Math.Abs(pos - Math.Round(pos)) * 4, 0, 1);
        play.IsHitTestVisible = play.Opacity > 0.5;
        var now = Math.Clamp((int)Math.Round(pos), 0, items.Count - 1);
        if (now != shown) Show(now);
    }

    // the front cover's name and artist (a quick crossfade), and its colour behind the wheel
    void Show(int i)
    {
        if (items.Count == 0) return;
        shown = i;
        title.Text = items[i].Title; sub.Text = items[i].Sub;
        title.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220)));
        sub.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)));
        if (items[i].Image is { } url) _ = SpotifyView.Image(url).ContinueWith(t => { if (shown == i && t.Result is { } img) Glow(img); }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Glow(BitmapSource img)
    {
        SpotifyView.Colour(SpotifyView.Tint(img));   // colours the whole Spotify view
    }

    // ---------- turning: one spring toward the target (the Inbox wheel's) ----------
    void To(double t)
    {
        target = Math.Clamp(Math.Round(t), 0, Math.Max(0, items.Count - 1));
        if (springing) return;
        springing = true; frame = DateTime.Now;
        CompositionTarget.Rendering += Spring;
    }

    void Spring(object? s, EventArgs e)
    {
        var now = DateTime.Now; var dt = Math.Min(0.05, (now - frame).TotalSeconds); frame = now;
        const double k = 300; var c = 1.7 * Math.Sqrt(k);   // a touch under critical: it clicks into place with the faintest overshoot
        for (int i = 0; i < 4; i++) { var h = dt / 4; speed += (k * (target - pos) - c * speed) * h; pos += speed * h; }
        if (Math.Abs(target - pos) < 0.002 && Math.Abs(speed) < 0.02) { pos = target; speed = 0; springing = false; CompositionTarget.Rendering -= Spring; Sounds.Snap(); }
        Layout();
    }
}
