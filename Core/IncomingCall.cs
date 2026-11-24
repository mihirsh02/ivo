using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Ivo;

// Someone calling, in Ivo and Ivo Portal: the whole window blurs behind their picture (or initial), their name and the
// kind of call, with Decline, Ignore and Accept. Show returns what takes it away again.
public static class IncomingCall
{
    // call waiting: acceptLabel "Merge" (adds them to the call you're on), and a note in place of the kind of call
    public static Action Show(Grid host, FrameworkElement under, string who, string kind, string? pic, Action accept, Action decline, Action ignore, string acceptLabel = "Accept", string? note = null)
    {
        // the window as it is, blurred, under a dark veil
        var dpi = VisualTreeHelper.GetDpi(under);
        var shot = new RenderTargetBitmap(Math.Max(1, (int)(under.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(under.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        shot.Render(under);
        var picture = new Image { Source = shot, Stretch = Stretch.Fill, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 28, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance } };
        var veil = new Border { Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x08, 0x06, 0x12)) };

        // them: their picture or a glowing initial in a pulsing ring, their name, what kind of call
        var ring = new Border { Width = 132, Height = 132, CornerRadius = new CornerRadius(66), BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xA7, 0x8B, 0xFA)), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
        var face = new Border { Width = 104, Height = 104, CornerRadius = new CornerRadius(52), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                Background = new LinearGradientBrush(Color.FromRgb(0x8B, 0x7C, 0xFF), Color.FromRgb(0x4F, 0x46, 0xE5), 45),
                                Child = new TextBlock { Text = who.Length > 0 ? who[..1].ToUpper() : "?", FontSize = 44, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (PortalCallView.PicBrush(pic, 208) is { } photo) { face.Background = photo; face.Child = null; }
        var pulse = new DoubleAnimation(1, 1.18, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        var avatar = new Grid { Width = 150, Height = 150, HorizontalAlignment = HorizontalAlignment.Center, Children = { ring, face } };
        var name = new TextBlock { Text = who, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        var what = new TextBlock { Text = note ?? kind switch { "video" => "Incoming video call", "screen" => "Wants to share their screen", "voice" => "Incoming voice call", "listenjoin" => "Wants to listen along", _ => "Wants to connect" },
                                   FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center };

        Grid? layer = null;
        void Remove() { if (layer != null) { host.Children.Remove(layer); layer = null; } }
        // the three choices: round buttons with a word under each
        FrameworkElement Choice(string glyph, string label, string color, Action click)
        {
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(32));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = bd };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "Bd"));
            t.Triggers.Add(hover);
            var b = new Button { Width = 64, Height = 64, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = label, Template = t, Background = (Brush)new BrushConverter().ConvertFromString(color)!,
                                 Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22, Foreground = Brushes.White } };
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Click += (_, _) => { Remove(); click(); };
            return new StackPanel { Margin = new Thickness(18, 0, 18, 0), Children = { b, new TextBlock { Text = label, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) } } };
        }
        var choices = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 44, 0, 0), Children = {
            Choice("", "Decline", "#F43F5E", decline),
            Choice("", "Ignore", "#3A4250", ignore),
            Choice(kind == "video" ? "" : "", acceptLabel, "#22C55E", accept) } };
        if (acceptLabel == "Merge") ((TextBlock)((Button)((StackPanel)choices.Children[2]).Children[0]).Content).Text = "";   // add a person

        var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { avatar, name, what, choices } };
        layer = new Grid { Children = { picture, veil, card } };
        Grid.SetRowSpan(layer, 99); Grid.SetColumnSpan(layer, 99); Panel.SetZIndex(layer, 3000);
        host.Children.Add(layer);
        layer.Opacity = 0;
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        card.RenderTransform = new TranslateTransform(0, 24);
        card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        return Remove;
    }
}
