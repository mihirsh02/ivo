using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// A small floating chat for quick questions, on Ctrl+Alt+Space from anywhere (Ivo can be in the tray).
// Always a chat-mode conversation; the same chat stays until "New" so follow-ups work.
public class Companion : Window
{
    readonly MainWindow main;
    readonly ChatPane pane;

    public Companion(MainWindow main)
    {
        this.main = main;
        Title = "Ivo companion"; Width = 420; Height = 580; Topmost = true; ShowInTaskbar = false;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20; Top = area.Bottom - Height - 20;

        FontFamily = main.FontFamily; FontWeight = main.FontWeight;   // the theme's font and weight (the theme sets them on windows open at that moment)
        pane = new ChatPane(main) { Companion = true };
        // header: Ivo's crystal and the name, like the main title bar; icon buttons the size of the Inbox's
        var crystal = new Canvas { Width = 14, Height = 16 };
        crystal.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,0 L7,16 L0,8 Z"), Fill = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)) });
        crystal.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,0 L14,8 L7,16 Z"), Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)) });
        crystal.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M7,0 L14,8 L7,8 Z"), Fill = Brushes.White, Opacity = 0.22 });
        var mark = new Viewbox { Width = 14, Height = 16, Child = crystal, Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = "Companion", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Button Btn(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, FontFamily = App.IconFont, FontSize = 15, Width = 34, Height = 34, Padding = new Thickness(0), ToolTip = tip, Style = (Style)main.FindResource("Ghost") };
            b.Click += (_, _) => click();
            return b;
        }
        var bar = new DockPanel { Height = 48, Background = Brushes.Transparent };
        bar.Children.Add(mark); DockPanel.SetDock(mark, Dock.Left);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                                       Children = { Btn("", "New chat", NewChat), Btn("", "Open in Ivo", OpenInMain), Btn("", "Hide (Esc)", Hide) } };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons); bar.Children.Add(label);
        bar.MouseLeftButtonDown += (_, _) => DragMove();
        DockPanel.SetDock(bar, Dock.Top);
        // flat: one solid panel, no outline or shadow
        var frame = new Border { CornerRadius = new CornerRadius(18), Margin = new Thickness(10), ClipToBounds = true, Child = new DockPanel { Children = { bar, pane } } };
        frame.SetResourceReference(Border.BackgroundProperty, "Panel");   // the chat's own colour: header and chat read as one surface
        Content = frame;
        pane.ApplyTheme(main.S.Theme);
        UseLayoutRounding = true;
        App.Crisp(this);
        ApplyZoom();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Hide(); } };
        Closing += (_, e) => { e.Cancel = !main.Quitting; Hide(); };
        NewChat();
    }

    void ApplyZoom() => App.Rescale();   // text size: see App.ScaleText

    void NewChat() { pane.ShowChat(main.NewCompanionChat()); pane.FocusInput(); }

    void OpenInMain() { Hide(); main.OpenChat(pane.Chat); }

    // the theme changed: its font and the chat pane's look follow (the colours follow by themselves)
    public void Restyle() { FontFamily = main.FontFamily; FontWeight = main.FontWeight; pane.ApplyTheme(main.S.Theme); }

    public void Open(Chat c)
    {
        pane.ShowChat(c);
        ApplyZoom();
        App.PauseMotion(false);   // Ivo may be minimised, with its animations paused
        Show(); Activate();
        pane.FocusInput();
    }

    public void Toggle()
    {
        if (IsVisible && IsActive) { Hide(); return; }
        ApplyZoom();
        Show(); Activate();
        pane.FocusInput();
    }
}
