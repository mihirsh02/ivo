using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// The tour, led by Ivo the mascot (after the welcome, from her chat, and Settings → About → Take the tour). Each step
// outlines the real part of Ivo it's about and puts her card beside it, with her waving at its top; the first steps also
// set Ivo up: a look, a model, a lock. Next / Back, ← / →, Esc to end.
public partial class MainWindow
{
    record TourStep(string Title, string Text, Func<FrameworkElement?>? Target = null, Func<FrameworkElement>? Extra = null);

    Grid? tourLayer;

    public void StartTour()
    {
        TourStep[] steps =
        [
            new("Hi, I'm Blub!", "I live right here in Ivo 💜 Let me show you around. It takes about a minute: a quick setup, then where everything is. Use → and ← or the buttons; Esc ends it any time."),
            new("Pick a look", "Default follows Windows' light or dark mode. AMOLED is true black. Ivo is my favourite: violet and cyan with a drifting aurora ✨ You can change it later in Settings → Appearance.",
                () => BtnMode, ThemeChips),
            new("Add a model", "For the big brainy stuff, Ivo talks to the AI models you choose: cloud ones with your own API key, models on this PC (Ollama, LM Studio), or AI command-line tools you already use. Scan this PC finds what's installed.",
                () => BtnSettings, () => TourButton("Open API keys", () => { EndTour(chat: false); OpenSettings("providers"); })),
            new("Chat", "Type here to talk to your model. Quick actions above the box switch how it works (Ctrl+1…6). /compare asks up to three models at once. Ctrl+Shift+Enter hands a task to an agent.",
                () => active.FindName("Beam") as FrameworkElement),
            new("Your chats", "Search finds words inside chats and jumps to them. Right-click a chat to pin, group, archive or make it local only. And look, that's Blub at the top! Click me any time to ask how something works 💜",
                () => Sidebar),
            new("To-do and Planner", "Your to-do list, and the Planner: reminders, daily tasks that run on a schedule and dev tasks for agents. Ctrl+Alt+N adds a to-do or reminder from anywhere.",
                () => BtnPlanner.IsVisible ? BtnPlanner : BtnTodo),
            new("Inbox and the morning briefing", "Reminders and task results land in the Inbox. Every morning the daily tasks arrive together as one briefing 📥",
                () => BtnInbox),
            new("Calendar and starred replies", "The calendar shows holidays, your events and what Ivo has scheduled; click a day to add something. ★ keeps the replies you star, even if their chat is deleted.",
                () => BtnCalendar.IsVisible ? BtnCalendar : BtnStars),
            new("Private by design", "Keys are encrypted to your Windows account, secrets are hidden from cloud models, and agents ask before changing things. An app lock adds a PIN and encrypts your chats 🔒",
                () => active.IncogBtn, () => TourButton("Set up the app lock", () => { EndTour(chat: false); OpenSettings("lock"); })),
            new("You're all set!", "Everything else lives in Settings. I'm always at the top of your chats, and I work offline: ask me to set a reminder, play a song, call a friend or change a setting, or type / to see what I can do. Have fun ✨"),
        ];
        ShowTourStep(steps, 0);
    }

    // the tour's over: her chat opens, and she says hi (unless a step sent you somewhere, like Settings)
    void EndTour(bool chat = true)
    {
        if (tourLayer != null) { Root.Children.Remove(tourLayer); tourLayer = null; }
        S.TourDone = true; SaveSettings();
        if (!chat) return;
        var first = (S.Profile?.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        OpenIvo().Greet($"That's the tour, {(first ?? "bestie")}! 🎉 You're all set. I'm right here whenever you need me, so what shall we do first? 💜");
    }

    void ShowTourStep(TourStep[] steps, int at)
    {
        if (tourLayer != null) Root.Children.Remove(tourLayer);
        var step = steps[at];
        var layer = tourLayer = new Grid();
        Grid.SetRowSpan(layer, 99); Grid.SetColumnSpan(layer, 99); Panel.SetZIndex(layer, 99000);
        var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
        layer.Children.Add(dim);

        // the outline around the part of Ivo this step is about
        var target = step.Target?.Invoke();
        Rect? box = null;
        if (target is { IsVisible: true, ActualWidth: > 0 })
        {
            var p = target.TransformToAncestor(Root).Transform(new Point(0, 0));
            box = new Rect(p.X - 6, p.Y - 6, target.ActualWidth + 12, target.ActualHeight + 12);
            var ring = new Border { Width = box.Value.Width, Height = box.Value.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                                    Margin = new Thickness(box.Value.X, box.Value.Y, 0, 0), CornerRadius = new CornerRadius(14, 14, 14, 14.01), BorderThickness = new Thickness(2.5),
                                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)), IsHitTestVisible = false };
            layer.Children.Add(ring);
        }

        // the card: icon, title, text, an optional action, dots, Back / Next
        var icon = new IvoAvatar { Width = 78, Height = 78, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, -4, 0, 6) };   // Blub leads, waving at each step
        var title = new TextBlock { Text = step.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var text = new TextBlock { Text = step.Text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var col = new StackPanel { Children = { icon, title, text } };
        if (step.Extra?.Invoke() is { } extra) { extra.Margin = new Thickness(0, 14, 0, 0); col.Children.Add(extra); }

        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < steps.Length; i++)
        {
            var d = new Border { Width = i == at ? 18 : 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0) };
            if (i == at) d.Background = Shell.B("#8B7CFF"); else d.SetResourceReference(Border.BackgroundProperty, "Separator");
            dots.Children.Add(d);
        }
        var last = at == steps.Length - 1;
        var next = new Button { Content = last ? "Start using Ivo" : at == 0 ? "Let's go" : "Next", Style = (Style)FindResource("Shimmer"), Padding = new Thickness(18, 7, 18, 7), VerticalAlignment = VerticalAlignment.Center };
        next.Click += (_, _) => { if (last) EndTour(); else ShowTourStep(steps, at + 1); };
        var back = new Button { Content = at == 0 ? "Skip the tour" : "Back", Style = (Style)FindResource("GhostText"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) => { if (at == 0) EndTour(); else ShowTourStep(steps, at - 1); };
        var foot = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { back, next } };
        DockPanel.SetDock(buttons, Dock.Right); foot.Children.Add(buttons); foot.Children.Add(dots);
        col.Children.Add(foot);
        var card = new Border { Child = col, Width = 400, Padding = new Thickness(22, 20, 22, 18), CornerRadius = new CornerRadius(18),
                                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        card.SetResourceReference(Border.BackgroundProperty, "BoxBg");
        card.BorderThickness = new Thickness(1); card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");   // the same edge as the windows
        layer.Children.Add(card);

        // beside the outlined part (below it when there's room, else above), or centred
        layer.Loaded += (_, _) =>
        {
            card.Measure(new Size(400, double.PositiveInfinity));
            double w = 400, h = card.DesiredSize.Height, W = Root.ActualWidth, H = Root.ActualHeight;
            double x, y;
            if (box is { } b)
            {
                x = Math.Clamp(b.X + b.Width / 2 - w / 2, 16, W - w - 16);
                y = b.Bottom + 12 + h < H - 16 ? b.Bottom + 12 : Math.Max(16, b.Y - h - 12);
                if (b.Height > H * 0.5) { x = b.Right + 16 + w < W - 16 ? b.Right + 16 : Math.Max(16, b.X - w - 16); y = Math.Clamp(b.Y + 40, 16, H - h - 16); }   // a tall area: beside it
            }
            else { x = (W - w) / 2; y = (H - h) / 2; }
            card.Margin = new Thickness(x, y, 0, 0);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        };
        layer.Focusable = true;
        layer.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { EndTour(); e.Handled = true; }
            else if (e.Key is Key.Right or Key.Enter) { e.Handled = true; if (last) EndTour(); else ShowTourStep(steps, at + 1); }
            else if (e.Key == Key.Left && at > 0) { e.Handled = true; ShowTourStep(steps, at - 1); }
        };
        Root.Children.Add(layer);
        Dispatcher.BeginInvoke(() => layer.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // the "Pick a look" step: the three themes, applied as you click
    FrameworkElement ThemeChips()
    {
        var track = new Border { CornerRadius = new CornerRadius(11, 11, 11, 11.01), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBg");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, tag) in new[] { ("Default", "soft"), ("AMOLED", "oled"), ("Ivo", "glow") })
        {
            var r = new RadioButton { Content = label, Style = (Style)FindResource("Switch"), GroupName = "TourTheme", IsChecked = S.Theme == tag || tag == "soft" && S.Theme == "softdark" };
            r.Checked += (_, _) => SetTheme(tag == "soft" ? (S.Theme == "softdark" ? "softdark" : "soft") : tag);
            row.Children.Add(r);
        }
        track.Child = row;
        return track;
    }

    Button TourButton(string label, Action run)
    {
        var b = new Button { Content = label, Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => run();
        return b;
    }
}
