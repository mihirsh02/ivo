using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ivo;

// The rail's own windows (Focus, Notes, Webhooks), in the shared frame: a header (icon, title, close), the content under it.
public partial class MainWindow
{
    Grid? panelLayer;
    string? panelKey;

    // scroll: the page scrolls as a whole; off, the content arranges itself (Portal's chat scrolls on its own, under a fixed header)
    void OpenPanel(string key, string title, string glyph, string color, FrameworkElement content, bool scroll = true)
    {
        OneAtATime(null);
        if (panelLayer != null) Root.Children.Remove(panelLayer);   // one window over the chat: a new page replaces the last, never stacks on it
        var layer = panelLayer = new Grid { Margin = new Thickness(0, 44, 0, 0) };
        panelKey = key;
        Grid.SetRowSpan(layer, 99); Grid.SetColumnSpan(layer, 99); Panel.SetZIndex(layer, 900);
        var back = new Border(); back.SetResourceReference(Border.BackgroundProperty, "Backdrop");
        back.MouseLeftButtonDown += (_, _) => ClosePanel();
        layer.Children.Add(back);
        var body = new DockPanel();
        // Portal's settings (calls and files) sit by the close button, like the chat's own actions
        Button? actions = null;
        if (key == "portal")
        {
            // a round bubble in the theme, level with the close button, lighting up under the mouse
            actions = IvoFrame.Round("", "Call and file settings", OpenPortalSettings, 30);
            // Windows' own outline gear, at a size that draws crisply (snapped to whole pixels)
            actions.FontSize = 15; actions.Margin = new Thickness(0, 0, 6, 0); actions.VerticalAlignment = VerticalAlignment.Center;
            actions.UseLayoutRounding = true; actions.SnapsToDevicePixels = true;
            TextOptions.SetTextRenderingMode(actions, TextRenderingMode.Grayscale); TextOptions.SetTextFormattingMode(actions, TextFormattingMode.Display);
        }
        var head = PanelHead(title, glyph, color, ClosePanel, (UIElement?)(content as IvoChatView)?.HeaderActions ?? actions, key is "news" or "news-article");
        DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        body.Children.Add(scroll ? new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 4, 24, 24) }
                                 : new Border { Child = content, Padding = new Thickness(24, 4, 24, 24) });
        var card = new Border { Child = body };
        Frame(card, FitFor(key), flexible: key != "ivo");   // Ivo's chat keeps its full height, like a chat
        layer.Children.Add(card);
        Root.Children.Add(layer);
        layer.Focusable = true; layer.Focus();
        layer.KeyDown += (_, e) => { if (e.Key == Key.Escape) { ClosePanel(); e.Handled = true; } };
        MarkRail();
    }

    void ClosePanel()
    {
        if (panelLayer == null) return;
        Root.Children.Remove(panelLayer); panelLayer = null; panelKey = null;
        MarkRail();
        if (backToSettings) { backToSettings = false; SettingsPanel.Visibility = Visibility.Visible; }
    }

    // a panel opened from Settings (a guide, the model store): closing it goes back to Settings, where you were
    bool backToSettings;
    void OpenFromSettings(string key, string title, string glyph, string color, FrameworkElement content, bool scroll = true)
    {
        var from = SettingsPanel.Visibility == Visibility.Visible;
        OpenPanel(key, title, glyph, color, content, scroll);
        backToSettings = from;
    }

    // the one header every window uses: its icon in its colour, the title, and the close button on the right
    FrameworkElement PanelHead(string title, string glyph, string color, Action close, UIElement? actions = null, bool newsIcon = false)
    {
        var x = new Button { Content = "", FontFamily = App.IconFont, FontSize = 14, Style = (Style)FindResource("Ghost"), ToolTip = "Close (Esc)" };
        x.Click += (_, _) => close();
        FrameworkElement icon = newsIcon ? NewsGlyphView(Shell.B(color), 20)
            : new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 20, Foreground = Shell.B(color), VerticalAlignment = VerticalAlignment.Center };
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var name = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var head = new DockPanel { Margin = new Thickness(24, 18, 16, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (actions != null) right.Children.Add(actions);
        right.Children.Add(x);
        DockPanel.SetDock(right, Dock.Right); head.Children.Add(right);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } });
        return head;
    }

    // nothing here yet: a soft icon in a circle, a title and what to do, centred (the same everywhere)
    static FrameworkElement EmptyState(string icon, string title, string hint)
    {
        var mark = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12),
                                Child = new TextBlock { Text = icon, FontSize = 24, FontFamily = ColorEmoji.EmojiFontFamily, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        mark.SetResourceReference(Border.BackgroundProperty, "InputBg");
        var t = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var h = new TextBlock { Text = hint, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 340, Margin = new Thickness(0, 4, 0, 0) };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return new StackPanel { Margin = new Thickness(0, 14, 0, 6), HorizontalAlignment = HorizontalAlignment.Center, Children = { mark, t, h } };
    }

    // a section's heading in a panel: as a Settings card leads, its icon tile in its colour, the title and what it's for
    // (the looks are in CardLooks); one without a look is the title alone
    static FrameworkElement Heading(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        if (!CardLooks.TryGetValue(text, out var look)) { t.Margin = new Thickness(0, 20, 0, 10); return t; }
        var tint = (Color)ColorConverter.ConvertFromString(look.Color);
        var tile = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                                Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B)),
                                Child = new TextBlock { Text = look.Icon, FontSize = 17, FontFamily = ColorEmoji.EmojiFontFamily, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { t } };
        if (look.About != "") { var a = new TextBlock { Text = look.About, FontSize = 12.5, TextWrapping = TextWrapping.Wrap }; a.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); words.Children.Add(a); }
        var row = new DockPanel { Margin = new Thickness(0, 22, 0, 12) };
        DockPanel.SetDock(tile, Dock.Left); row.Children.Add(tile); row.Children.Add(words);
        return row;
    }

    // a text box with its hint inside, faint, until something is typed
    static Grid Hinted(TextBox box, string hint)
    {
        var t = new TextBlock { Text = hint, IsHitTestVisible = false, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = Lines(box) ? VerticalAlignment.Top : VerticalAlignment.Center, FontSize = box.FontSize,
                                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = Lines(box) ? TextWrapping.Wrap : TextWrapping.NoWrap, ToolTip = hint };   // a box of several lines: the whole hint, wrapped
        // a box that takes several lines: the hint goes exactly where the caret starts (the box's style adds its own padding, so it's measured)
        if (Lines(box))
            box.Loaded += (_, _) =>
            {
                var at = box.GetRectFromCharacterIndex(0);
                if (!at.IsEmpty) t.Margin = new Thickness(at.X + 3, at.Y, 12, 0);   // on the caret's line, just past it
            };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        void Show() => t.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Show(); Show();
        // a box of a set width: the grid hugs it, so the hint sits inside the box, not beside it
        return new Grid { Children = { box, t }, Margin = box.Margin, HorizontalAlignment = double.IsNaN(box.Width) ? box.HorizontalAlignment : HorizontalAlignment.Left };
    }

    static bool Lines(TextBox box) => box.AcceptsReturn || box.TextWrapping == TextWrapping.Wrap;   // a box that can hold several lines starts at the top

    static Border Box(UIElement child, double pad = 16)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(pad), Margin = new Thickness(0, 0, 0, 8) };
        b.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        return b;
    }

    // ---------- Focus: a Pomodoro timer and habits ----------
    DispatcherTimer? focusTimer;
    DateTime focusEnds; TimeSpan focusLeft; bool focusBreak, focusRunning;
    TextBlock? focusClock, focusState;
    Button? focusGo;

    void OpenFocus_Click(object s, RoutedEventArgs e) { if (panelKey == "focus") ClosePanel(); else OpenFocus(); }

    void OpenFocus()
    {
        var page = new StackPanel();
        if (!focusRunning && focusLeft == TimeSpan.Zero) focusLeft = TimeSpan.FromMinutes(S.FocusLength);

        // the timer: big digits, the session's kind under them, start / pause and reset
        focusClock = new TextBlock { FontSize = 64, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
        focusClock.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        focusState = new TextBlock { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        focusState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var lengths = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var m in new[] { 15, 25, 45, 60 })
        {
            var chip = new ToggleButton { Content = $"{m}m", Style = (Style)FindResource("Chip"), Margin = new Thickness(3, 0, 3, 0), IsChecked = S.FocusLength == m, ToolTip = $"{m} minutes" };
            chip.Click += (_, _) => { S.FocusLength = m; SaveSettings(); if (!focusRunning) { focusBreak = false; focusLeft = TimeSpan.FromMinutes(m); } OpenFocus(); };
            lengths.Children.Add(chip);
        }
        focusGo = new Button { Width = 120, Height = 40, Margin = new Thickness(4, 0, 4, 0), Style = (Style)FindResource("Shimmer") };
        focusGo.Click += (_, _) => { if (focusRunning) PauseFocus(); else StartFocus(); };
        var reset = new Button { Content = "Reset", Style = (Style)FindResource("GhostText"), Height = 40, FontSize = 14, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(4, 0, 4, 0) };
        reset.Click += (_, _) => { PauseFocus(); focusBreak = false; focusLeft = TimeSpan.FromMinutes(S.FocusLength); DrawFocus(); };
        var timer = new StackPanel { Children = { focusClock, focusState, lengths, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { focusGo, reset } } } };
        page.Children.Add(Box(timer, 24));

        // the last seven days of focus, as bars
        page.Children.Add(Heading("This week"));
        var week = new UniformGrid { Columns = 7, Height = 110 };
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).ToList();
        var most = Math.Max(25, days.Max(d => S.FocusMinutes.GetValueOrDefault(d.ToString("yyyy-MM-dd"))));
        foreach (var d in days)
        {
            var min = S.FocusMinutes.GetValueOrDefault(d.ToString("yyyy-MM-dd"));
            var bar = new Border { Width = 22, Height = Math.Max(3, 70.0 * min / most), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Bottom, ToolTip = $"{min} min" };
            bar.SetResourceReference(Border.BackgroundProperty, min > 0 ? "AccentGrad" : "Separator");
            var label = new TextBlock { Text = d == DateTime.Today ? "Today" : d.ToString("ddd"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var col = new DockPanel(); DockPanel.SetDock(label, Dock.Bottom); col.Children.Add(label); col.Children.Add(bar);
            week.Children.Add(col);
        }
        // nothing focused yet this week: say so, rather than an empty frame of grey stubs
        if (days.All(d => S.FocusMinutes.GetValueOrDefault(d.ToString("yyyy-MM-dd")) == 0))
        {
            var none = new TextBlock { Text = "No focus time yet this week. Start a session above and it shows here.", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 18) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            page.Children.Add(Box(new Grid { Children = { week, none } }));
        }
        else page.Children.Add(Box(week));

        // habits: one row each, the last seven days as dots (tick today, or any day you missed), the streak on the right
        page.Children.Add(Heading("Habits"));
        foreach (var h in S.Habits.ToList()) page.Children.Add(HabitRow(h));
        var add = new TextBox();
        add.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || add.Text.Trim() == "") return;
            S.Habits.Add(new Habit { Name = add.Text.Trim() }); SaveSettings(); OpenFocus();
        };
        page.Children.Add(Hinted(add, "New habit, then Enter (Read 20 pages, Walk, Drink water…)"));

        OpenPanel("focus", "Focus", "", "#F43F5E", page);
        DrawFocus();
    }

    // a habit: its name and days on top (the streak, a ⋯ for its days and reminder, remove), its last seven days as dots
    // under it (tick any day it was due; days it isn't due are faded); click its name for the last five weeks
    readonly HashSet<string> habitOpen = [], habitEditing = [];
    FrameworkElement HabitRow(Habit h)
    {
        var row = new DockPanel();
        var streak = h.Streak(DateTime.Today);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Text = streak > 0 ? $"🔥 {streak}d" : "", ToolTip = streak > 0 ? $"{streak} day{(streak == 1 ? "" : "s")} in a row" : null, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        right.Children.Add(count);
        var more = new Button { Content = "\uE712", FontFamily = App.IconFont, FontSize = 13, Style = (Style)FindResource("Ghost"), ToolTip = "Days and reminder", Margin = new Thickness(6, 0, 0, 0) };
        more.Click += (_, _) => { if (!habitEditing.Remove(h.Id)) habitEditing.Add(h.Id); OpenFocus(); };
        right.Children.Add(more);
        var del = new Button { Content = "\uE74D", FontFamily = App.IconFont, FontSize = 13, Style = (Style)FindResource("Ghost"), ToolTip = "Remove this habit", Margin = new Thickness(2, 0, 0, 0) };
        del.Click += (_, _) => { S.Habits.Remove(h); SaveSettings(); OpenFocus(); };
        right.Children.Add(del);
        DockPanel.SetDock(right, Dock.Right); row.Children.Add(right);
        var icon = new TextBlock { Text = "\uE73E", FontFamily = App.IconFont, FontSize = 16, Foreground = Shell.B("#34D399"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock { Text = h.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand, ToolTip = h.Name + " · click for the last five weeks" };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        name.MouseLeftButtonUp += (_, _) => { if (!habitOpen.Remove(h.Id)) habitOpen.Add(h.Id); OpenFocus(); };
        var sub = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Text = (h.On.Count is > 0 and < 7 ? string.Join(" ", h.On.Order().Select(d => ((DayOfWeek)d).ToString()[..3])) : "Every day") + (h.Remind != "" ? $" · reminder {h.Remind}" : "") };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } });   // takes the rest of the row, trimmed if long
        var week = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 8, 0, 0) };
        for (int i = 6; i >= 0; i--) week.Children.Add(HabitDot(h, DateTime.Today.AddDays(-i), 24));
        var all = new StackPanel { Children = { row, week } };

        // the last five weeks, Monday first, as a grid of the same dots
        if (habitOpen.Contains(h.Id))
        {
            var grid = new UniformGrid { Columns = 7, Margin = new Thickness(28, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var d in new[] { "M", "T", "W", "T", "F", "S", "S" })
            {
                var t = new TextBlock { Text = d, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                grid.Children.Add(t);
            }
            var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7)).AddDays(-28);
            for (var d = monday; d <= DateTime.Today; d = d.AddDays(1)) grid.Children.Add(HabitDot(h, d, 20));
            var done = Enumerable.Range(0, 30).Select(i => DateTime.Today.AddDays(-i)).Where(h.Due).ToList();
            var rate = new TextBlock { FontSize = 12, Margin = new Thickness(28, 8, 0, 0), Text = done.Count > 0 ? $"Done {done.Count(h.Did)} of {done.Count} days it was due in the last 30 · best streak so far: {Best(h)}d" : "" };
            rate.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            all.Children.Add(grid); all.Children.Add(rate);
        }

        // its days and its reminder
        if (habitEditing.Contains(h.Id))
        {
            var days = new WrapPanel { Margin = new Thickness(28, 10, 0, 0) };
            foreach (var (label, dow) in new[] { ("Mon", 1), ("Tue", 2), ("Wed", 3), ("Thu", 4), ("Fri", 5), ("Sat", 6), ("Sun", 0) })
            {
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6), IsChecked = h.On.Count == 0 || h.On.Contains(dow) };
                chip.Click += (_, _) =>
                {
                    var on = h.On.Count == 0 ? Enumerable.Range(0, 7).ToList() : h.On;
                    if (chip.IsChecked == true) { if (!on.Contains(dow)) on.Add(dow); } else on.Remove(dow);
                    h.On = on.Count is 0 or 7 ? [] : on; SaveSettings();
                };
                days.Children.Add(chip);
            }
            var time = new TextBox { Text = h.Remind, Width = 90, ToolTip = "A time like 07:30, or empty for no reminder" };
            var save = new Button { Content = "Save", Style = (Style)FindResource("GhostText"), Margin = new Thickness(8, 0, 0, 0) };
            save.Click += (_, _) =>
            {
                var t = time.Text.Trim();
                h.Remind = TimeSpan.TryParse(t, out var at) && at < TimeSpan.FromDays(1) ? $"{at.Hours:00}:{at.Minutes:00}" : "";
                habitEditing.Remove(h.Id); SaveSettings(); OpenFocus();
            };
            var label2 = new TextBlock { Text = "Remind me at", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            label2.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            all.Children.Add(days);
            all.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 2, 0, 0), Children = { label2, Hinted(time, "07:30"), save } });
        }
        return Box(all, 12);
    }

    static int Best(Habit h)
    {
        int best = 0, run = 0;
        foreach (var d in h.Days.Select(x => DateTime.TryParse(x, out var t) ? t : DateTime.MinValue).Where(t => t > DateTime.MinValue).Order().Select(t => t.Date).Distinct())
        {
            var prev = d.AddDays(-1);
            while (!h.Due(prev) && prev > d.AddDays(-8)) prev = prev.AddDays(-1);
            run = h.Did(prev) ? run + 1 : 1; best = Math.Max(best, run);
        }
        return best;
    }

    // one day of a habit: ticked (violet), due and not (its letter), or a day off (faded); tap to tick or untick
    Border HabitDot(Habit h, DateTime day, double size)
    {
        var key = day.ToString("yyyy-MM-dd"); var on = h.Days.Contains(key); var due = h.Due(day);
        var dot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Margin = new Thickness(2), Cursor = Cursors.Hand,
                               ToolTip = day.ToString("dddd d MMM") + (due ? "" : " · a day off"), BorderThickness = new Thickness(day == DateTime.Today ? 1.5 : 0), Opacity = due || on ? 1 : .35 };
        dot.SetResourceReference(Border.BackgroundProperty, on ? "AccentGrad" : "InputBg");
        dot.SetResourceReference(Border.BorderBrushProperty, "Dim");
        dot.Child = new TextBlock { Text = on ? "\uE73E" : day.ToString("ddd")[..1], FontFamily = on ? App.IconFont : new FontFamily("Segoe UI"), FontSize = on ? size * .55 : size * .48,
                                    Foreground = on ? Brushes.White : (Brush)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dot.MouseLeftButtonUp += (_, _) => { if (!h.Days.Remove(key)) { h.Days.Add(key); Cheer(h); } SaveSettings(); OpenFocus(); };
        return dot;
    }

    // a streak worth a cheer: 3, 7, 14, 30, 50, 100 days and every 100 after
    public void Cheer(Habit h)
    {
        var n = h.Streak(DateTime.Today);
        if (!(n is 3 or 7 or 14 or 30 or 50 or 100 || n > 100 && n % 100 == 0)) return;
        var say = $"{n} days of {h.Name} in a row 🔥 " + (n >= 30 ? "that's a real habit now, I'm so proud 🥹" : n >= 7 ? "look at you go!" : "keep it going ✨");
        Sounds.Chime();
        NotifyCard.Show(say, "Habits · Focus", "\uE734", "#F43F5E", OpenFocus);
        if (panelKey == "ivo") ivoView?.Greet(say);
    }

    // a habit's reminder: at its time, on a day it's due and not done yet, a nudge (once a day)
    readonly Dictionary<string, string> habitNudged = [];
    System.Windows.Threading.DispatcherTimer? habitClock;
    void StartHabitNudges()
    {
        habitClock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        habitClock.Tick += (_, _) =>
        {
            var now = DateTime.Now.ToString("HH:mm"); var today = DateTime.Today.ToString("yyyy-MM-dd");
            foreach (var h in S.Habits)
                if (h.Remind == now && h.Due(DateTime.Today) && !h.Did(DateTime.Today) && habitNudged.GetValueOrDefault(h.Id) != today)
                {
                    habitNudged[h.Id] = today;
                    Sounds.Chime();
                    NotifyCard.Show($"Time for {h.Name} 🔥", h.Streak(DateTime.Today) is var st and > 0 ? $"Keep your {st}-day streak going · tap to tick it" : "Tap to tick it in Focus", "\uE734", "#F43F5E", OpenFocus);
                }
        };
        habitClock.Start();
    }

    void StartFocus()
    {
        focusRunning = true; focusEnds = DateTime.Now + focusLeft;
        focusTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => FocusTick(), Dispatcher);
        focusTimer.Start();
        DrawFocus();
    }

    void PauseFocus()
    {
        if (focusRunning) focusLeft = focusEnds - DateTime.Now;
        focusRunning = false; focusTimer?.Stop();
        DrawFocus();
    }

    void FocusTick()
    {
        focusLeft = focusEnds - DateTime.Now;
        if (focusLeft <= TimeSpan.Zero)
        {
            focusTimer!.Stop(); focusRunning = false;
            if (!focusBreak)
            {
                var day = DateTime.Today.ToString("yyyy-MM-dd");
                S.FocusMinutes[day] = S.FocusMinutes.GetValueOrDefault(day) + S.FocusLength; SaveSettings();
                Webhooks.Post(S, "focus", "Focus session done", $"{S.FocusLength} minutes of focus. {S.FocusMinutes[day]} min today.");
                NotifyCard.Show("Focus session done", $"Take {S.BreakLength} minutes off.", "", "#F43F5E", OpenFocus);
                focusBreak = true; focusLeft = TimeSpan.FromMinutes(S.BreakLength);
                StartFocus();   // the break runs on its own
            }
            else
            {
                NotifyCard.Show("Break's over", "Ready for the next session?", "", "#F43F5E", OpenFocus);
                focusBreak = false; focusLeft = TimeSpan.FromMinutes(S.FocusLength);
            }
            if (panelKey == "focus") OpenFocus();
        }
        DrawFocus();
    }

    void DrawFocus()
    {
        BtnFocus.ToolTip = focusRunning ? $"Focus · {(focusBreak ? "break" : "focusing")}, {Math.Max(0, focusLeft.Minutes)}:{Math.Max(0, focusLeft.Seconds):00} left" : "Focus · timer and habits";
        if (focusClock == null || panelKey != "focus") return;
        var t = focusLeft < TimeSpan.Zero ? TimeSpan.Zero : focusLeft;
        focusClock.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        focusState!.Text = focusBreak ? "Break" : focusRunning ? "Focusing" : "Ready when you are";
        focusGo!.Content = focusRunning ? "Pause" : "Start";
    }

    // ---------- Notes: one scratchpad, saved as you type (sealed with the app lock like your chats) ----------
    static string NotesPath => Path.Combine(Store.Dir, "notes.md");
    DispatcherTimer? notesSave;

    void OpenNotes_Click(object s, RoutedEventArgs e) { if (panelKey == "notes") ClosePanel(); else OpenNotes(); }

    void OpenNotes()
    {
        string? text = null;
        try { text = File.Exists(NotesPath) ? Store.ReadData(NotesPath) : ""; } catch { text = ""; }
        var box = new TextBox { Text = text ?? "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, MinHeight = 420, FontSize = 14,
                                VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(16, 14, 16, 14), IsReadOnly = text == null,
                                BorderThickness = new Thickness(0), Background = Brushes.Transparent };   // the card is its frame (no underlined box)
        box.Height = Math.Max(280, FrameSize(Fit.Tall).Height - 160);   // fills the frame under its header and footer, no scrolling
        // under it: how much there is, and whether it's saved
        var state = new TextBlock { FontSize = 12, Margin = new Thickness(4, 10, 4, 0) };
        state.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        void Count(string saved) { var words = box.Text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length; state.Text = $"{words} word{(words == 1 ? "" : "s")} · {saved}"; }
        Count(text == null ? "locked" : "saved");
        notesSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (t, _) => { ((DispatcherTimer)t!).Stop(); }, Dispatcher);
        box.TextChanged += (_, _) =>
        {
            notesSave.Stop();
            notesSave.Tick -= SaveNow; notesSave.Tick += SaveNow;
            notesSave.Start();
            Count("saving…");
        };
        void SaveNow(object? s, EventArgs e) { Directory.CreateDirectory(Store.Dir); Store.WriteData(NotesPath, box.Text); Count("saved"); }
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = Hinted(box, text == null ? "Unlock Ivo to see your notes." : "Jot anything: ideas, links, a list for later. It saves as you type.") };
        card.SetResourceReference(Border.BackgroundProperty, "InputBg"); card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        OpenPanel("notes", "Notes", "", "#FACC15", new StackPanel { Children = { card, state } });
        box.Focus(); box.CaretIndex = box.Text.Length;
    }

    // ---------- Webhooks: post reminders, task results, the briefing and focus sessions to Slack, Discord, Teams… ----------
    void OpenHooks_Click(object s, RoutedEventArgs e) { if (panelKey == "webhooks") ClosePanel(); else OpenHooks(); }

    void OpenHooks()
    {
        var page = new StackPanel { MaxWidth = 760 };
        var help = new TextBlock { Text = "Ivo posts to a chat app's incoming webhook when something happens. Only that event's text is sent, and only to the addresses here.",
                                   TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        page.Children.Add(help);
        foreach (var h in S.Webhooks.ToList()) page.Children.Add(HookCard(h));
        if (S.Webhooks.Count == 0) page.Children.Add(EmptyState("🪝", "No webhooks yet", "Add one below and Ivo will post reminders, task results and the briefing to that chat."));

        // add one: the service, a name, the address
        page.Children.Add(Heading("Add a webhook"));
        var kind = new ComboBox { Width = 170, ItemsSource = Webhooks.Kinds.Select(k => k.Name).ToList(), SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
        var name = new TextBox();   // fills the rest of its line
        var url = new TextBox();
        var note = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = Shell.B("#F87171"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var add = new Button { Content = "Add", Width = 90, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("Shimmer") };
        add.Click += (_, _) =>
        {
            if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var u) || u.Scheme != "https") { note.Text = "Paste the webhook's https:// address."; note.Visibility = Visibility.Visible; url.Focus(); return; }
            var k = Webhooks.Kinds[Math.Max(0, kind.SelectedIndex)];
            S.Webhooks.Add(new Webhook { Kind = k.Id, Name = name.Text.Trim() == "" ? k.Name : name.Text.Trim(), Url = u.ToString() });
            SaveSettings(); OpenHooks();
        };
        // the service and a name on one line, the address (the long part) and Add on the next
        var line = new DockPanel();
        DockPanel.SetDock(kind, Dock.Left);
        line.Children.Add(kind); line.Children.Add(Hinted(name, "Name (#team, Alerts…)"));
        var line2 = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        url.KeyDown += (_, e) => { if (e.Key == Key.Enter) add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };
        line2.Children.Add(add); line2.Children.Add(Hinted(url, "https://hooks.slack.com/… or https://discord.com/api/webhooks/…"));
        page.Children.Add(Box(new StackPanel { Children = { line, line2, note } }));
        OpenPanel("webhooks", "Webhooks", "", "#38BDF8", page);
    }

    FrameworkElement HookCard(Webhook h)
    {
        var k = Webhooks.Kinds.FirstOrDefault(x => x.Id == h.Kind);
        if (k.Id == null) k = Webhooks.Kinds[^1];
        var icon = new TextBlock { Text = k.Glyph, FontFamily = App.IconFont, FontSize = 18, Foreground = Shell.B(k.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var title = new TextBlock { Text = h.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var where = new TextBlock { Text = $"{k.Name} · {Mask(h.Url)}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var result = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var test = new Button { Content = "Send a test", Style = (Style)FindResource("GhostText") };
        test.Click += async (_, _) =>
        {
            result.Text = "Sending…"; result.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var err = await Webhooks.Send(h, "test", "Ivo is connected", "This is a test from Ivo's Webhooks.");
            result.Text = err == null ? "Sent" : err; result.Foreground = Shell.B(err == null ? "#3FB950" : "#F87171");
        };
        var del = new Button { Content = "", FontFamily = App.IconFont, FontSize = 13, Style = (Style)FindResource("Ghost"), ToolTip = "Remove", Margin = new Thickness(4, 0, 0, 0) };
        del.Click += (_, _) => { S.Webhooks.Remove(h); SaveSettings(); OpenHooks(); };
        var top = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { result, test, del } };
        DockPanel.SetDock(tools, Dock.Right); top.Children.Add(tools);
        DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(title, Dock.Left);
        top.Children.Add(icon); top.Children.Add(title); top.Children.Add(where);   // the address trims before the buttons move
        // which events go there
        var events = new WrapPanel { Margin = new Thickness(30, 10, 0, 0) };
        foreach (var (id, label) in Webhooks.Events)
        {
            var chip = new ToggleButton { Content = label, Style = (Style)FindResource("Chip"), IsChecked = h.Events.Contains(id), Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) => { h.Events.Remove(id); if (chip.IsChecked == true) h.Events.Add(id); SaveSettings(); };
            events.Children.Add(chip);
        }
        return Box(new StackPanel { Children = { top, events } });
    }

    // the address is a secret (anyone with it can post): only its start and end show
    static string Mask(string url) => url.Length <= 36 ? url : url[..28] + "…" + url[^6..];

    void HookWebhooks() => Inbox.Added += i => { if (Webhooks.Event(i) is { Length: > 0 } ev) Dispatcher.BeginInvoke(() => Webhooks.Post(S, ev, i.Title, i.Text)); };
}
