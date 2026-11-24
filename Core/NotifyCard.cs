using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Ivo;

// A desktop notification: Windows' own (Toasts), so it shows in Windows' notification area and follows Focus and its
// settings. When Windows won't take it, a card in Ivo's own look instead: bottom-right, above the taskbar, slides in, stays
// while the mouse is on it and leaves after 8 s. Clicking either opens what it's about; × dismisses. It never takes focus.
public class NotifyCard : Window
{
    static readonly List<NotifyCard> open = [];
    // shared by Ivo and Ivo Portal: each app gives its name, its icon font and its text look (Ivo: Crisp)
    public static string AppName = "Ivo";
    public static FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static Action<Window>? Look;
    // a colour from the app's theme, or Ivo's dark look where there's no theme (Ivo Portal)
    static Brush R(string key, string fallback) => Application.Current?.TryFindResource(key) as Brush ?? (Brush)new BrushConverter().ConvertFromString(fallback)!;
    // in Ivo: follow its theme (a live link to the colour); elsewhere the fallback
    static void Paint(FrameworkElement e, DependencyProperty p, string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) != null) e.SetResourceReference(p, key);
        else e.SetValue(p, new BrushConverter().ConvertFromString(fallback));
    }
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(8) };

    // who: a person (a chat message): their picture, or their initial in a violet circle, leads the card instead of an icon
    public static void Show(string title, string body, string icon, string color, Action click, string? who = null, string? pic = null)
    {
        Toasts.Show(title, body, click, pic, quiet: true);   // also in Windows' notification area, quietly (no second banner): this card is the one you see
        var card = new NotifyCard(title, body, icon, color, click, who, pic);
        open.Add(card);
        while (open.Count > 3) open[0].Dismiss();
        card.Show();
    }

    NotifyCard(string title, string body, string icon, string color, Action click, string? who, string? pic)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Width = 380; SizeToContent = SizeToContent.Height; Opacity = 0;
        Look?.Invoke(this);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontWeight = FontWeights.Medium;   // the same text as Ivo
        var tint = (Color)ColorConverter.ConvertFromString(color);

        TextBlock Text(string t, double size, string brush, FontWeight? weight = null)
        {
            var tb = new TextBlock { Text = t, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
            Paint(tb, TextBlock.ForegroundProperty, brush, brush == "Fg" ? "#E6EDF3" : "#A0AAB6");
            return tb;
        }
        var badge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
                                 Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B)),
                                 Child = new TextBlock { Text = icon, FontFamily = IconFont, FontSize = 19, Foreground = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (who != null)
        {
            // a person: their picture, or their initial
            badge.Child = new TextBlock { Text = who.Length > 0 ? who[..1].ToUpper() : "?", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Paint(badge, Border.BackgroundProperty, "AccentGrad", "#7C5CFF");
            if (pic is { Length: > 0 })
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage(); img.BeginInit(); img.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(pic)); img.DecodePixelWidth = 96; img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.EndInit(); img.Freeze();
                    badge.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; badge.Child = null;
                    RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.HighQuality);
                }
                catch { }
            // and a small app mark on its corner, so it's clear where it came from
            var mark = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -3),
                                    Child = new TextBlock { Text = icon, FontFamily = IconFont, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            Paint(mark, Border.BorderBrushProperty, "BoxBg", "#1E2730"); mark.BorderThickness = new Thickness(2);
            var face = badge; face.Margin = new Thickness(0);
            badge = new Border { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0), Child = new Grid { Children = { face, mark } } };
        }
        var ghost = Application.Current?.TryFindResource("Ghost") as Style;
        var close = ghost != null
            ? new Button { Content = "", FontFamily = IconFont, FontSize = 10, Style = ghost, ToolTip = "Dismiss", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, -4, -6, 0) }
            : new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = R("Dim", "#A0AAB6"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Width = 26, Height = 26, Cursor = Cursors.Hand, ToolTip = "Dismiss", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, -4, -6, 0), Template = Plain() };
        close.Click += (_, e) => { e.Handled = true; Dismiss(); };
        var app = Text($"{AppName}  ·  now", 11, "Dim");
        var head = Text(title, 14.5, "Fg", FontWeights.SemiBold); head.Margin = new Thickness(0, 2, 0, 3); head.MaxHeight = 40;
        var text = Text(body, 13, "Fg"); text.MaxHeight = 38; text.LineHeight = 19; text.Opacity = 0.82;   // up to two lines of what it says
        DockPanel.SetDock(badge, Dock.Left); DockPanel.SetDock(close, Dock.Right);
        var content = new DockPanel { Children = { badge, close, new StackPanel { Children = { app, head, text } } } };

        // one solid panel with a soft wash of the kind's colour from its top-left: no outline, no shadow (they show as a faded
        // rim on a transparent window), so its edge and words stay sharp
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 13, 14, 14), Cursor = Cursors.Hand };   // its child: the wash and the words, below
        Paint(card, Border.BackgroundProperty, "BoxBg", "#1E2730");
        var wash = new Border { CornerRadius = new CornerRadius(16), IsHitTestVisible = false,
                                Background = new LinearGradientBrush(Color.FromArgb(0x30, tint.R, tint.G, tint.B), Color.FromArgb(0, tint.R, tint.G, tint.B), new Point(0, 0), new Point(0.7, 1)) };
        wash.Margin = new Thickness(-14, -13, -14, -14);   // under the words, across the whole card
        card.Child = new Grid { Children = { wash, content } };
        Content = new Grid { Margin = new Thickness(14), Children = { card } };

        card.MouseLeftButtonUp += (_, _) => { Dismiss(); click(); };
        MouseEnter += (_, _) => timer.Stop();
        MouseLeave += (_, _) => { if (!leaving) timer.Start(); };
        timer.Tick += (_, _) => Dismiss();
        Loaded += (_, _) =>
        {
            Arrange();
            var t = new TranslateTransform(40, 0);
            card.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase() });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            timer.Start();
        };
    }

    // a bare button (round on hover)
    static ControlTemplate Plain()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)), "Bd"));
        t.Triggers.Add(hover);
        return t;
    }

    bool leaving;
    void Dismiss()
    {
        if (leaving) return;
        leaving = true; timer.Stop(); open.Remove(this);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { Close(); Arrange(); };
        BeginAnimation(OpacityProperty, fade);
    }

    // newest at the bottom, the older ones stacked above it
    static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var y = area.Bottom;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            var c = open[i];
            if (!c.IsLoaded) continue;   // not measured yet: it places everyone again once it loads
            y -= c.ActualHeight;
            c.Left = area.Right - c.Width; c.Top = y;
            y += 10;   // the cards' shadow margins overlap a little
        }
    }
}
