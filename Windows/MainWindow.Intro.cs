using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// First launch: Blub introduces herself before she asks anything. A dark stage with a slow violet glow, her mascot
// rising in, her words typed out line by line, then one button into the welcome questions.
public partial class MainWindow
{
    void ShowIntro(bool thenWelcome = true)
    {
        var first = new[]
        {
            "Hi, I'm Blub 👋",
            "I live right here, in Ivo.",
            "Chats with any model, your plans, music, notes and more, all in one place. And me, to help with all of it 💜",
            "Let me get to know you a little, then I'll show you around ✨",
        };
        var glow = new RadialGradientBrush(Color.FromArgb(110, 139, 92, 246), Colors.Transparent) { RadiusX = .55, RadiusY = .55, Center = new Point(.5, .4), GradientOrigin = new Point(.5, .4) };
        var layer = new Grid { Background = new SolidColorBrush(Color.FromRgb(11, 11, 18)), Opacity = 0 };
        Panel.SetZIndex(layer, 1000);
        layer.Children.Add(new Border { Background = glow });
        glow.BeginAnimation(RadialGradientBrush.RadiusXProperty, new DoubleAnimation(.45, .65, TimeSpan.FromSeconds(4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });

        var mascot = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "blub.png");
        var her = new Image { Height = 360, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 56, 0), VerticalAlignment = VerticalAlignment.Center,
                              Source = System.IO.File.Exists(mascot) ? new System.Windows.Media.Imaging.BitmapImage(new Uri(mascot)) : null,
                            };
        RenderOptions.SetBitmapScalingMode(her, BitmapScalingMode.HighQuality);
        var rise = new TranslateTransform(0, 40); her.RenderTransform = rise; her.Opacity = 0;
        var lines = new StackPanel { MaxWidth = 440 };
        var go = new Button { Content = "Nice to meet you  →", Style = (Style)FindResource("Shimmer"), Padding = new Thickness(26, 10, 26, 10), FontSize = 15,
                              HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 30, 0, 0), Opacity = 0, IsHitTestVisible = false };
        var skip = new Button { Content = "Skip intro", Style = (Style)FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 18, 22, 0) };
        layer.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(40),
            Child = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                                     Children = { her, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 440, Children = { lines, go } } } } });
        layer.Children.Add(skip);
        Root.Children.Add(layer);

        var done = false;
        void Finish(bool skip)
        {
            if (done) return; done = true;
            layer.IsHitTestVisible = false;
            if (skip) Root.Children.Remove(layer);
            else
            {
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
                fade.Completed += (_, _) => Root.Children.Remove(layer);
                layer.BeginAnimation(OpacityProperty, fade);
            }
            if (thenWelcome) ShowWelcome(first: true);
        }
        go.Click += (_, _) => Finish(skip: false);
        skip.Click += (_, _) => Finish(skip: true);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        layer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)));
        her.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(250) });
        var up = new DoubleAnimation(0, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = ease };
        up.Completed += (_, _) => rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, TimeSpan.FromSeconds(2.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });   // she floats
        rise.BeginAnimation(TranslateTransform.YProperty, up);

        // her words, typed out one line after another (the first one big)
        _ = Type();
        async Task Type()
        {
            await Task.Delay(1100);
            for (var i = 0; i < first.Length; i++)
            {
                if (done) return;
                var t = new TextBlock { Foreground = i == 0 ? Brushes.White : (Brush)FindResource("Fg"), FontSize = i == 0 ? 34 : 16, FontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal,
                                        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, Margin = new Thickness(0, 0, 0, i == 0 ? 14 : 8), Opacity = i == 0 ? 1 : .85 };
                lines.Children.Add(t);
                foreach (var ch in System.Globalization.StringInfo.GetTextElementEnumerator(first[i]) is var e ? Elements(e) : [])
                {
                    if (done) return;
                    ColorEmoji.Append(t.Inlines, ch, t.FontSize); await Task.Delay(i == 0 ? 45 : 18);
                }
                await Task.Delay(i == 0 ? 500 : 350);
            }
            go.IsHitTestVisible = true;
            go.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)));
            go.Focus();
        }
        static IEnumerable<string> Elements(System.Globalization.TextElementEnumerator e) { while (e.MoveNext()) yield return e.GetTextElement(); }
    }
}
