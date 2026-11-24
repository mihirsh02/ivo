using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Starred replies (title bar ★): a panel like the Inbox. Each reply is a card with its chat and date; click to read it in
// full, then open its chat, copy it, or unstar it. Replies stay here even after their chat is deleted.
// Closes with ×, Esc, or a click outside.
public partial class MainWindow
{
    Grid? starsLayer;

    void ShowStars()
    {
        foreach (var (c, m) in allChats.SelectMany(c => c.Messages.Where(m => m.Starred).Select(m => (c, m))))
            if (!S.Stars.Any(x => x.ChatId == c.Id && x.Text == m.Text)) S.Stars.Add(new(m.Text, c.Id, c.Title, m.At ?? c.Updated));
        S.Stars = [.. S.Stars.OrderByDescending(x => x.At)];
        SaveSettings();

        OneAtATime(null);
        CloseStars();
        var layer = starsLayer = new Grid { Margin = new Thickness(0, 44, 0, 0) };
        Grid.SetRowSpan(layer, 99); Grid.SetColumnSpan(layer, 99); Panel.SetZIndex(layer, 900);
        var backdrop = new Border(); backdrop.SetResourceReference(Border.BackgroundProperty, "Backdrop");
        backdrop.MouseLeftButtonDown += (_, _) => CloseStars();
        layer.Children.Add(backdrop);

        var list = new StackPanel { Margin = new Thickness(22, 0, 22, 22) };
        void Draw()
        {
            list.Children.Clear();
            if (S.Stars.Count == 0) { list.Children.Add(Line("Nothing starred yet. Hover a reply and click ☆ to keep it here.", 13, "Dim")); return; }
            foreach (var h in S.Stars) list.Children.Add(StarCard(h, Draw));
        }
        Draw();

        var close = new Button { Content = "", FontFamily = App.IconFont, FontSize = 14, Style = (Style)FindResource("Ghost"), Width = 34, Height = 34, ToolTip = "Close (Esc)" };
        close.Click += (_, _) => CloseStars();
        var title = Line("Starred replies", 15, "Fg"); title.FontWeight = FontWeights.SemiBold; title.TextAlignment = TextAlignment.Left; title.Margin = new Thickness(0);
        var icon = new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 16, Foreground = Shell.B("#FBBF24"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var head = new DockPanel { Margin = new Thickness(22, 14, 12, 12) };
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { icon, title } });

        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var card = new Border { Child = body };
        Frame(card, Fit.Small, flexible: true);
        layer.Children.Add(card);
        Root.Children.Add(layer);
        layer.Focusable = true; layer.Focus();
        layer.KeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseStars(); e.Handled = true; } };
    }

    void CloseStars() { if (starsLayer != null) { Root.Children.Remove(starsLayer); starsLayer = null; } }

    FrameworkElement StarCard(Starred h, Action redraw)
    {
        var chat = allChats.FirstOrDefault(c => c.Id == h.ChatId);
        var meta = Line($"{h.ChatTitle}{(chat == null ? " (chat deleted)" : "")}  ·  {When.Time(h.At)}", 11.5, "Dim"); meta.TextAlignment = TextAlignment.Left; meta.Margin = new Thickness(0, 0, 0, 6);
        var oneLine = System.Text.RegularExpressions.Regex.Replace(h.Text, @"\s+", " ").Trim();
        var text = new TextBlock { Text = oneLine, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 58, TextTrimming = TextTrimming.WordEllipsis, LineHeight = 19 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        Button Act(string glyph, string label, Action run)
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Padding = new Thickness(10, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0),
                                 Content = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                                     new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 15, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center },
                                     new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center } } } };
            b.Click += (_, e) => { e.Handled = true; run(); };
            return b;
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        if (chat != null) actions.Children.Add(Act("", "Open chat", () => { CloseStars(); OpenChat(chat); }));
        actions.Children.Add(Act("", "Copy", () => Clipboard.SetText(h.Text)));
        actions.Children.Add(Act("", "Unstar", () =>
        {
            S.Stars.Remove(h);
            if (chat?.Messages.FirstOrDefault(m => m.Starred && m.Text == h.Text) is { } msg) { chat.Messages[chat.Messages.IndexOf(msg)] = msg with { Starred = false }; Store.SaveChat(chat); }
            SaveSettings(); redraw();
        }));

        var card = new Border { Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(12, 12, 12, 12.01), Cursor = Cursors.Hand,
                                Child = new StackPanel { Children = { meta, text, actions } } };
        card.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        // click: the whole reply and its actions; again: back to the preview
        card.MouseLeftButtonUp += (_, _) =>
        {
            var open = actions.Visibility != Visibility.Visible;
            actions.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            text.Text = open ? h.Text : oneLine;
            text.MaxHeight = open ? double.PositiveInfinity : 58;
        };
        return card;
    }
}
