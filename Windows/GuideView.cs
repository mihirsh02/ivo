using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// A step-by-step guide, shown as one of Ivo's panels: one step at a time (Back / Next, the dots show where you are), each with what
// to do and, where it helps, an example file with Copy and Save as… so you start from the right format.
public class GuideView : DockPanel
{
    public record Step(string Title, string Text, string? Code = null, string? File = null);

    readonly Step[] steps;
    int at;
    readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    readonly TextBlock text = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    readonly Border codeBox = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 16, 0, 0) };
    readonly TextBox code = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                                    TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 300 };
    readonly Button copy, save, back, next;
    readonly StackPanel dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock count = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    readonly Action close;
    public GuideView(Step[] steps, Action close)
    {
        this.steps = steps; this.close = close;
        Focusable = true;
        foreach (var t in new[] { title, text }) t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        code.SetResourceReference(Control.ForegroundProperty, "Fg");
        count.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        codeBox.SetResourceReference(Border.BackgroundProperty, "InputBg");
        codeBox.SetResourceReference(Border.BorderBrushProperty, "GlassStroke"); codeBox.BorderThickness = new Thickness(1);

        copy = Small("Copy", () => { try { Clipboard.SetText(code.Text); copy!.Content = "Copied ✓"; } catch { } });
        save = Small("Save as…", SaveFile);
        var codeBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var codeTools = new StackPanel { Orientation = Orientation.Horizontal, Children = { copy, save } };
        DockPanel.SetDock(codeTools, Dock.Right); codeBar.Children.Add(codeTools);
        var label = new TextBlock { Text = "Example", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        codeBar.Children.Add(label);
        codeBox.Child = new DockPanel { Children = { codeBar, code } };
        DockPanel.SetDock(codeBar, Dock.Top);

        back = new Button { Content = "Back", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0) };
        next = new Button { Content = "Next", Padding = new Thickness(22, 8, 22, 8), Style = (Style)FindResource("Shimmer") };
        back.Click += (_, _) => Go(at - 1);
        next.Click += (_, _) => { if (at == steps.Length - 1) close(); else Go(at + 1); };
        var nav = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { back, next } };
        DockPanel.SetDock(buttons, Dock.Right); nav.Children.Add(buttons);
        nav.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { dots, count } });

        var body = new StackPanel { Children = { title, text, codeBox } };
        DockPanel.SetDock(nav, Dock.Bottom); Children.Add(nav);
        Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Right && e.OriginalSource is not TextBox) { Go(at + 1); e.Handled = true; } else if (e.Key == Key.Left && e.OriginalSource is not TextBox) { Go(at - 1); e.Handled = true; } };
        Go(0);
    }

    void Go(int i)
    {
        if (i < 0 || i >= steps.Length) return;
        at = i;
        var s = steps[i];
        title.Text = s.Title; text.Text = s.Text;
        codeBox.Visibility = s.Code == null ? Visibility.Collapsed : Visibility.Visible;
        code.Text = s.Code ?? ""; copy.Content = "Copy";
        save.Visibility = s.File == null ? Visibility.Collapsed : Visibility.Visible;
        back.IsEnabled = i > 0; back.Opacity = i > 0 ? 1 : 0.4;
        next.Content = i == steps.Length - 1 ? "Done" : "Next";
        count.Text = $"Step {i + 1} of {steps.Length}";
        dots.Children.Clear();
        for (int d = 0; d < steps.Length; d++)
        {
            var dot = new Border { Width = d == i ? 18 : 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand };
            if (d == i) dot.SetResourceReference(Border.BackgroundProperty, "AccentGrad"); else dot.SetResourceReference(Border.BackgroundProperty, "Separator");
            var to = d; dot.MouseLeftButtonUp += (_, _) => Go(to);
            dots.Children.Add(dot);
        }
    }

    void SaveFile()
    {
        var s = steps[at];
        if (s.File is null || s.Code is null)
        {
            IvoFrame.Message(Window.GetWindow(this), "This guide step has no example file to save.", "Couldn't save");
            return;
        }
        var pick = new Microsoft.Win32.SaveFileDialog { FileName = s.File, Filter = "All files (*.*)|*.*" };
        if (pick.ShowDialog(Window.GetWindow(this)) != true) return;
        try { System.IO.File.WriteAllText(pick.FileName, s.Code); save.Content = "Saved ✓"; } catch (Exception ex) { IvoFrame.Message(Window.GetWindow(this), ex.Message, "Couldn't save"); }
    }

    Button Small(string label, Action click)
    {
        var b = new Button { Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource("Ghost") };
        b.Click += (_, _) => click();
        return b;
    }
}
