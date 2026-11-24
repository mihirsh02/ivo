using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Ivo;

// Ivo's launch animation: the crystal's four facets fly in and lock together, a glint of light sweeps across it, then
// "Ivo" slides in beside it; the whole thing fades as the app appears. Drawn live (sharp at any scale), about 1.8 s.
// Backgrounds: "oled" (true black card), "aurora" (the Aurora theme's violet/cyan light), "clear" (just the logo, no card).
public class Splash : Window
{
    readonly Action? reveal;

    public static void Play(string style, Action? reveal = null) => new Splash(style, reveal).Show();

    Splash(string style, Action? reveal)
    {
        this.reveal = reveal;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Width = 560; Height = 280; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Ivo";

        // ---- the crystal: 4 facets of the icon's diamond (256 grid, drawn at 128) ----
        Point top = new(128, 8), left = new(22, 128), right = new(234, 128), bottom = new(128, 248), mid = new(128, 128);
        var crystal = new Canvas { Width = 256, Height = 256, LayoutTransform = new ScaleTransform(0.5, 0.5), RenderTransformOrigin = new Point(0.5, 0.5) };
        (Point[] Pts, string Color, Vector From, double Turn)[] facets =
        [
            ([top, left, mid], "#8467FF", new(-70, -60), -28),
            ([left, bottom, mid], "#7155F5", new(-70, 60), 24),
            ([top, right, mid], "#5ADBEE", new(70, -60), 26),
            ([right, bottom, mid], "#22D3EE", new(70, 60), -22),
        ];
        var sb = new Storyboard();
        int k = 0;
        foreach (var (pts, color, from, turn) in facets)
        {
            var poly = new Polygon { Points = new PointCollection(pts), Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Opacity = 0,
                                     RenderTransformOrigin = new Point(0.5, 0.5) };
            var move = new TranslateTransform(from.X, from.Y); var spin = new RotateTransform(turn, mid.X, mid.Y);
            poly.RenderTransform = new TransformGroup { Children = { spin, move } };
            crystal.Children.Add(poly);
            var at = TimeSpan.FromMilliseconds(80 + k * 70); var dur = TimeSpan.FromMilliseconds(560);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            Anim(sb, poly, "Opacity", 0, 1, at, TimeSpan.FromMilliseconds(280), null);
            Anim(move, TranslateTransform.XProperty, from.X, 0, at, dur, ease);
            Anim(move, TranslateTransform.YProperty, from.Y, 0, at, dur, ease);
            Anim(spin, RotateTransform.AngleProperty, turn, 0, at, dur, ease);
            k++;
        }
        // the glint: a soft white band sweeping across, clipped to the diamond
        var glintMove = new TranslateTransform(-260, 0);
        var glint = new Rectangle
        {
            Width = 256, Height = 256, IsHitTestVisible = false, RenderTransform = glintMove,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                GradientStops = { new(Color.FromArgb(0, 255, 255, 255), 0.35), new(Color.FromArgb(0xC0, 255, 255, 255), 0.5), new(Color.FromArgb(0, 255, 255, 255), 0.65) },
            },
        };
        var holder = new Canvas { Width = 256, Height = 256, Clip = new PathGeometry([new PathFigure(top, [new PolyLineSegment([right, bottom, left], true)], true)]) };
        holder.Children.Add(glint);
        crystal.Children.Add(holder);
        Anim(glintMove, TranslateTransform.XProperty, -260, 260, TimeSpan.FromMilliseconds(720), TimeSpan.FromMilliseconds(620), new SineEase { EasingMode = EasingMode.EaseInOut });
        // a small pulse as the facets lock
        var pulse = new ScaleTransform(1, 1);
        crystal.RenderTransform = pulse;
        var bump = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(600) };
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1.07, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase()));
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new QuadraticEase()));
        pulse.BeginAnimation(ScaleTransform.ScaleXProperty, bump); pulse.BeginAnimation(ScaleTransform.ScaleYProperty, bump);

        // ---- the wordmark ----
        var wordMove = new TranslateTransform(18, 0);
        var word = new TextBlock
        {
            Text = "Ivo", FontSize = 68, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = style == "clear" ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xF2, 0xF0, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, -6, 0, 0), Opacity = 0, RenderTransform = wordMove,
        };
        if (style == "clear") word.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black };
        Anim(sb, word, "Opacity", 0, 1, TimeSpan.FromMilliseconds(820), TimeSpan.FromMilliseconds(420), null);
        Anim(wordMove, TranslateTransform.XProperty, 18, 0, TimeSpan.FromMilliseconds(820), TimeSpan.FromMilliseconds(520), new CubicEase { EasingMode = EasingMode.EaseOut });

        var logo = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { crystal, word } };

        // ---- the background ----
        var stage = new Grid();
        if (style == "oled")
            stage.Children.Add(new Border { CornerRadius = new CornerRadius(26), Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromArgb(0x3D, 255, 255, 255)), BorderThickness = new Thickness(1) });
        else if (style == "aurora")
        {
            var card = new Grid { ClipToBounds = true };
            card.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x07, 0x07, 0x0C)) });
            // soft light: radial gradients fading to nothing (a blur effect would be cut off square at its edges)
            Ellipse Glow(string c, double w, double h, double x, double y)
            {
                var col = (Color)ColorConverter.ConvertFromString(c);
                return new Ellipse
                {
                    Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x, y, 0, 0),
                    Fill = new RadialGradientBrush { GradientStops = { new(Color.FromArgb(0x90, col.R, col.G, col.B), 0), new(Color.FromArgb(0x38, col.R, col.G, col.B), 0.5), new(Color.FromArgb(0, col.R, col.G, col.B), 1) } },
                };
            }
            card.Children.Add(Glow("#7C5CFF", 460, 380, -120, -20));
            card.Children.Add(Glow("#22D3EE", 400, 320, 300, -120));
            card.Children.Add(Glow("#F472B6", 300, 240, 260, 150));
            card.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), 26, 26);
            stage.Children.Add(card);
            stage.Children.Add(new Border { CornerRadius = new CornerRadius(26), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), BorderThickness = new Thickness(1) });
        }
        stage.Children.Add(logo);
        Content = stage;
        Opacity = 0;

        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            sb.Begin(this);
            // hold a moment on the finished logo, then hand over to the app and fade
            var done = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(1650) };
            done.CurrentTimeInvalidated += (_, _) => { };
            var handed = false;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            timer.Tick += (_, _) => { timer.Stop(); if (!handed) { handed = true; this.reveal?.Invoke(); } };
            timer.Start();
            done.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, done, HandoffBehavior.Compose);
        };
    }

    static void Anim(Storyboard sb, UIElement target, string prop, double from, double to, TimeSpan at, TimeSpan dur, IEasingFunction? ease)
    {
        var a = new DoubleAnimation(from, to, dur) { BeginTime = at, EasingFunction = ease };
        Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, new PropertyPath(prop));
        sb.Children.Add(a);
    }

    static void Anim(Animatable target, DependencyProperty prop, double from, double to, TimeSpan at, TimeSpan dur, IEasingFunction? ease) =>
        target.BeginAnimation(prop, new DoubleAnimation(from, to, dur) { BeginTime = at, EasingFunction = ease });
}
