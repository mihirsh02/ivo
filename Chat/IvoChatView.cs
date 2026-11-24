using System.Text.RegularExpressions;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// What Blub's chat can do in the app: ask first (calls, messages, weighty settings), do it (what to say, and its undo),
// open a settings section or panel, and start the tour
// a song, as her music card shows it: cover, title, artist, playing or paused, and how far in (seconds)
public sealed record SongCard(string Title, string Artist, string? Cover, bool Playing, double At, double Length);
// a live card of something of yours under her answer (your music, the weather, your day…): an icon or artwork, a
// headline, rows (each with a picture or a glyph, words, a value on the right, and a share bar), where it opens, and how
// to make it again (redrawn every 20 seconds while it's on screen)
public sealed record InfoRow(string Title, string? Sub, string? Right, ImageSource? Pic = null, double? Share = null, string? Bar = null, string? Glyph = null, bool Round = false, bool Faded = false, bool Highlight = false);
public sealed record InfoCard(string Icon, string Color, string Title, string? Sub, InfoRow[] Rows, string? Open, Func<Task<InfoCard?>>? Again, ImageSource? Art = null, TimeSpan? RefreshEvery = null, bool CompactRows = false);

public sealed record IvoHands(Func<IvoAction, bool> NeedsOk, Func<IvoAction, Task<(string Said, Action? Undo, (string Label, IvoAction Then)[] Pick, SongCard? Song)>> Do,
                              Action<string> Open, Action Tour, Func<Task<string>> SpotifySignIn);

// Blub's own chat: not a model chat, so no pickers or options. Her words are on the left in violet, yours on the right,
// with a few ideas to tap and the box. Offline, on this PC, kept in ivo-chat.json (sealed with the app lock like the rest).
public sealed class IvoChatView : DockPanel
{
    internal sealed record Line(bool Mine, string Text, string? Card = null, DateTime At = default,
                                string? Id = null, string? ReplyText = null, bool ReplyMine = false);   // Card: live card prompt; reply fields keep its quoted text even if deleted
    static string FilePath => Path.Combine(Store.Dir, "ivo-chat.json");

    IvoMind mind;
    readonly string you;
    readonly List<Line> lines;
    readonly StackPanel log = new() { Margin = new Thickness(0, 4, 0, 8) };
    readonly ScrollViewer scroller;
    readonly TextBox box = new();
    readonly IvoHands hands;
    readonly Grid middle = new();
    readonly StackPanel bottomBar = new();   // the box and ideas: hidden while a gallery covers the chat
    public StackPanel HeaderActions { get; }
    Border replyBar = null!;
    TextBlock replyLabel = null!;
    Line? replying;

    public IvoChatView(string you, IvoHands hands)
    {
        this.hands = hands;
        mind = new IvoMind(you); this.you = you;
        _ = IvoModel.Warm();   // Smarter chat: loaded while you type, not after you send
        IvoMind.Memory = IvoMemory.Load(IvoMemory.Path);
        try { lines = Store.ReadData(FilePath) is { Length: > 0 } json ? JsonSerializer.Deserialize<List<Line>>(json) ?? [] : []; } catch { lines = []; }
        if (lines.Count == 0) lines.Add(new(false, IvoMind.Hello(you), At: DateTime.Now));
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Id is not { Length: > 0 } || lines[i].At == default)
                lines[i] = lines[i] with { Id = lines[i].Id is { Length: > 0 } ? lines[i].Id : Guid.NewGuid().ToString("N"),
                                            At = lines[i].At == default ? DateTime.Now : lines[i].At };
        foreach (var l in lines)
            mind.Heard(l.Mine, l.Mine && l.ReplyText is { Length: > 0 } quoted ? ReplyPrompt(l.Text, l.ReplyMine, quoted) : l.Text, l.At);
        foreach (var l in lines.Where(l => l.Mine)) if (System.Text.RegularExpressions.Regex.Match(l.Text, @"(?i)\b(?:my name is|call me)\s+(\p{L}+)") is { Success: true } m) mind.Name = char.ToUpper(m.Groups[1].Value[0]) + m.Groups[1].Value[1..].ToLowerInvariant();

        var clear = new Button { Content = "", FontFamily = App.IconFont, FontSize = 14, Padding = new Thickness(8), Cursor = Cursors.Hand, ToolTip = "Clear chat",
                                 HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        clear.SetResourceReference(Control.ForegroundProperty, "Dim");
        clear.Click += (_, _) => Clear(you);
        var corner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        // incognito: a private chat, nothing saved or remembered; the button lit and a banner while it's on
        incognito = new Button { Content = "\uED1A", FontSize = 15, Padding = new Thickness(8), Cursor = Cursors.Hand, ToolTip = "Incognito: nothing saved or remembered",
                                 Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        incognito.SetResourceReference(Control.FontFamilyProperty, "TitleIconFont");
        incognito.SetResourceReference(Control.ForegroundProperty, "Dim");
        incognito.Click += (_, _) => Private(!IvoMind.Incognito);
        System.Windows.Automation.AutomationProperties.SetName(incognito, "Incognito");
        corner.Children.Add(incognito);
        corner.Children.Add(clear);
        HeaderActions = corner;

        // ideas to tap, then the box
        // one idea at a time, small, changing every few seconds; tap it to say it
        string[] all = ["Show me around", "What can you do?", "What's on today?", "Remind me at 6 to stretch", "Play some music", "Switch theme to AMOLED", "Tell me a joke"];
        var at = 0;
        var chip = Chip(all[0], () => Say(all[at]));
        var ideas = new Border { Child = chip, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        var turn = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        turn.Tick += (_, _) =>
        {
            if (chip.IsMouseOver) return;   // stays put while you're pointing at it
            at = (at + 1) % all.Length; chip.Content = all[at];
            chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        };
        Loaded += (_, _) => turn.Start(); Unloaded += (_, _) => turn.Stop();
        var send = new Button { Content = "", FontFamily = App.IconFont, FontSize = 16, Width = 44, Height = 44, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.White, Cursor = Cursors.Hand, ToolTip = "Send" };
        send.Template = Round();
        send.SetResourceReference(BackgroundProperty, "AccentGrad");
        send.Click += (_, _) => Say(box.Text);
        box.FontSize = 14; box.Height = 44; box.Padding = new Thickness(16, 0, 16, 0); box.VerticalContentAlignment = VerticalAlignment.Center;
        box.SetResourceReference(BackgroundProperty, "BoxCard"); box.SetResourceReference(Control.ForegroundProperty, "Fg"); box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        box.SetResourceReference(Control.BorderBrushProperty, "GlassStroke"); box.BorderThickness = new Thickness(1);
        box.Template = Pill();
        box.FocusVisualStyle = null;   // its own outline lights up instead of a second, square one round it
        box.GotKeyboardFocus += (_, _) => box.SetResourceReference(Control.BorderBrushProperty, "AccentGrad");
        box.LostKeyboardFocus += (_, _) => box.SetResourceReference(Control.BorderBrushProperty, "GlassStroke");
        box.PreviewKeyDown += (_, e) =>
        {
            // a command with nothing after it: Enter shakes the box and she says what goes there
            if (e.Key == Key.Enter && System.Text.RegularExpressions.Regex.Match(box.Text.Trim(), @"^(/\w+)$") is { Success: true } bare
                && IvoCommands.Slashes.FirstOrDefault(x => x.Slash.Equals(bare.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is { Slash: not null } only && slashPicked)
            { Shake(); Hint($"Tell me the {only.Args.Replace("  ", " and ")} after {only.Slash}, like: {only.Hint.Split(" · ").Last()} 💜"); e.Handled = true; return; }
            if (slashList.Visibility != Visibility.Visible || slashMatches.Count == 0 || slashPicked) return;
            // half a command: ↑ ↓ choose, Enter or Tab fills in the one that's lit
            if (e.Key is Key.Down or Key.Up) { slashAt = (slashAt + (e.Key == Key.Down ? 1 : -1) + slashMatches.Count) % slashMatches.Count; DrawSlash(); e.Handled = true; }
            else if (e.Key is Key.Tab or Key.Enter) { PickSlash(slashMatches[slashAt].Slash); e.Handled = true; }
            else if (e.Key == Key.Escape) { slashList.Visibility = Visibility.Collapsed; e.Handled = true; }
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Say(box.Text); e.Handled = true; } };
        box.TextChanged += (_, _) => { UpdateSlash(); if (hint.IsOpen && box.Text.Contains(' ') && box.Text.Trim().Contains(' ')) hint.IsOpen = false; };
        Unloaded += (_, _) => hint.IsOpen = false;
        var row = new DockPanel();
        DockPanel.SetDock(send, Dock.Right);
        var placeholder = Text("Message Blub…", 14, dim: true);
        placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false;   // clicks go through to the box
        // it starts just past where the caret really is (measured, whatever the box's padding and the screen's scaling)
        box.SizeChanged += (_, _) => { var at = box.GetRectFromCharacterIndex(0); if (!at.IsEmpty) placeholder.Margin = new Thickness(at.X + 5, 0, 0, 0); };
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(send); row.Children.Add(new Grid { Children = { box, placeholder } });
        slashList.SetResourceReference(Border.BackgroundProperty, "PopupBg");
        slashList.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        privacy = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed,
                               Background = new SolidColorBrush(Color.FromArgb(0x26, 0xF4, 0x72, 0xB6)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF4, 0x72, 0xB6)), BorderThickness = new Thickness(1),
                               Child = Text("🕶️ Incognito · this chat isn't saved, and she won't remember it, journal it or log your mood", 12) };
        ((TextBlock)privacy.Child).TextWrapping = TextWrapping.Wrap;
        replyBar = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 0, 0, 7), Visibility = Visibility.Collapsed,
                                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xA7, 0x8B, 0xFA)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xA7, 0x8B, 0xFA)), BorderThickness = new Thickness(1) };
        replyLabel = Text("", 11.5, dim: true); replyLabel.TextTrimming = TextTrimming.CharacterEllipsis; replyLabel.VerticalAlignment = VerticalAlignment.Center;
        var replyClose = new Button { Content = "×", FontSize = 16, Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Cancel reply" };
        replyClose.SetResourceReference(Control.ForegroundProperty, "Dim"); replyClose.Click += (_, _) => SetReply(null);
        var replyRow = new DockPanel(); DockPanel.SetDock(replyClose, Dock.Right); replyRow.Children.Add(replyClose); replyRow.Children.Add(replyLabel); replyBar.Child = replyRow;
        foreach (var x in new UIElement[] { privacy, replyBar, slashList, ideas, row }) bottomBar.Children.Add(x);
        SetDock(bottomBar, Dock.Bottom); Children.Add(bottomBar);

        // the messages sit at the bottom, like a messenger, until there are enough to fill it
        log.VerticalAlignment = VerticalAlignment.Bottom;
        var fill = new Grid { Children = { log } };
        scroller = new ScrollViewer { Content = fill, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };   // scrolls, no bar
        fill.SetBinding(MinHeightProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight)) { Source = scroller });
        middle.Children.Add(scroller);
        Children.Add(middle);
        if (IvoMind.Incognito) Private(true);
        else Saved();
        Loaded += (_, _) => scroller.ScrollToEnd();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
        };
    }

    // "/": what she can do, like Discord's command list: a floating card over the box, a row per command (its emoji,
    // the command and what goes after it, what it does), the one you're on lit; ↑ ↓ to move, Tab or Enter to pick, Esc
    // to close. Once you've picked one, the card shows just that one, as a reminder of what to type.
    readonly Border slashList = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(6, 8, 6, 6), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed,
                                        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = .35, Color = Colors.Black } };
    List<(string Slash, string Args, string Hint, string Icon)> slashMatches = [];
    int slashAt;
    bool slashPicked;

    void UpdateSlash()
    {
        var t = box.Text;
        var word = t.Split(' ')[0];
        slashPicked = t.StartsWith('/') && t.Contains(' ');
        slashMatches = !t.StartsWith('/') ? []
            : slashPicked ? IvoCommands.Slashes.Where(x => x.Slash.Equals(word, StringComparison.OrdinalIgnoreCase)).ToList()
            : IvoCommands.Slashes.Where(x => x.Slash.StartsWith(word, StringComparison.OrdinalIgnoreCase)).ToList();
        slashAt = Math.Clamp(slashAt, 0, Math.Max(0, slashMatches.Count - 1));
        slashList.Visibility = slashMatches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawSlash();
    }

    // a word from her that floats over the box for a few seconds, not a chat message
    readonly System.Windows.Controls.Primitives.Popup hint = new() { AllowsTransparency = true, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = true, VerticalOffset = -6 };
    System.Windows.Threading.DispatcherTimer? hintTimer;
    void Hint(string text)
    {
        var words = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        ColorEmoji.Append(words.Inlines, text, 12.5);
        words.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var card = new Border { CornerRadius = new CornerRadius(14, 14, 14, 4), Padding = new Thickness(10, 8, 12, 8), BorderThickness = new Thickness(1), Margin = new Thickness(4, 4, 4, 10),
                                Child = words,
                                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .35, Color = Colors.Black } };
        card.SetResourceReference(Border.BackgroundProperty, "PopupBg");
        card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xA7, 0x8B, 0xFA));
        hint.Child = card; hint.PlacementTarget = box; hint.IsOpen = true;
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        hintTimer?.Stop();
        hintTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        hintTimer.Tick += (_, _) =>
        {
            hintTimer!.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => hint.IsOpen = false;
            card.BeginAnimation(OpacityProperty, fade);
        };
        hintTimer.Start();
    }

    // the latest music card follows the music: a new song, or play / pause from anywhere, and it redraws
    Panel? liveRow;
    FrameworkElement? liveCard;
    SongCard? liveSong;
    public void NowPlaying(SongCard? s)
    {
        if (s == null || liveRow == null || liveCard == null || liveSong is { } was && was.Title == s.Title && was.Artist == s.Artist && was.Playing == s.Playing) return;
        var at = liveRow.Children.IndexOf(liveCard);
        if (at < 0) return;
        liveSong = s;
        liveRow.Children.RemoveAt(at);
        liveRow.Children.Insert(at, liveCard = Music(s));
        liveCard.BeginAnimation(OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(260)));
    }

    // the music card: the album art blurred behind it, the cover, the song and who sings it, a progress bar that keeps
    // moving while it plays, and round ⏮ ⏯ ⏭ buttons (the app's own icons) that work right there
    FrameworkElement Music(SongCard s)
    {
        var holder = new Border();
        System.Windows.Media.Imaging.BitmapImage? art = null;
        if (s.Cover != null) { art = new(); art.BeginInit(); art.UriSource = new Uri(s.Cover); art.DecodePixelWidth = 300; art.EndInit(); }

        // the back: the cover, blown up and blurred, under a dark wash so the words read
        var back = new Grid { ClipToBounds = true };
        if (art != null)   // a brush, not an image: it fills the card without making it any taller
            back.Children.Add(new Border { Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill }, Opacity = .55, Margin = new Thickness(-24),
                                           Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 36 } });
        back.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0xD8, 0x14, 0x12, 0x24), Color.FromArgb(0xB0, 0x1C, 0x14, 0x3A), 0) });

        // the cover, with a soft shadow
        var cover = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                                 Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x55, 0x8B, 0x6C, 0xFF)),
                                 Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .5, Color = Colors.Black } };

        // "now playing", with a little equaliser that dances while it plays
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Height = 10, Margin = new Thickness(0, 0, 6, 1) };
        var accent = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
        for (var i = 0; i < 3; i++)
        {
            var bar = new Border { Width = 2.5, Height = 10, Margin = new Thickness(0, 0, 1.5, 0), CornerRadius = new CornerRadius(1), Background = accent, VerticalAlignment = VerticalAlignment.Bottom,
                                   RenderTransformOrigin = new Point(.5, 1), RenderTransform = new ScaleTransform(1, s.Playing ? .4 : .3) };
            if (s.Playing)
                bar.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(380 + i * 130)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            bars.Children.Add(bar);
        }
        var label = new TextBlock { Text = s.Playing ? "NOW PLAYING" : "PAUSED", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = accent };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2), Children = { bars, label } };

        var title = new TextBlock { Text = s.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        var artist = new TextBlock { Text = s.Artist, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };

        // progress: a thin bar with a glowing end, and the times under it, ticking along while it plays
        var at = s.At;
        var track = new Grid { Height = 3, Margin = new Thickness(0, 7, 0, 0) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)) });
        var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = .8, Color = Color.FromRgb(0xA7, 0x8B, 0xFA) } };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentGrad");
        track.Children.Add(fill);
        static string Clock(double sec) => $"{(int)sec / 60}:{(int)sec % 60:00}";
        var dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var from = new TextBlock { FontSize = 10.5, Foreground = dim };
        var to = new TextBlock { FontSize = 10.5, Foreground = dim, Text = s.Length > 0 ? Clock(s.Length) : "" };
        var times = new DockPanel { Margin = new Thickness(0, 2, 0, 0), LastChildFill = false };
        DockPanel.SetDock(to, Dock.Right); times.Children.Add(to); times.Children.Add(from);
        void Draw() { from.Text = Clock(at); fill.Width = s.Length > 0 && track.ActualWidth > 0 ? track.ActualWidth * Math.Clamp(at / s.Length, 0, 1) : 0; }
        track.SizeChanged += (_, _) => Draw();
        Draw();
        if (s.Playing && s.Length > 0)
        {
            var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (_, _) => { if (at < s.Length) { at++; Draw(); } else tick.Stop(); };
            holder.Loaded += (_, _) => tick.Start(); holder.Unloaded += (_, _) => tick.Stop();
        }

        // the controls: round, the play/pause one bigger and in Ivo's gradient
        Button Round(string glyph, double size, double font, bool main, string tip, string kind)
        {
            var b = new Button { Width = size, Height = size, Content = glyph, FontFamily = App.IconFont, FontSize = font, Foreground = Brushes.White, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border)); bd.Name = "bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp); t.VisualTree = bd;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, .85));
            var down = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(RenderTransformProperty, new ScaleTransform(.92, .92)));
            down.Setters.Add(new Setter(RenderTransformOriginProperty, new Point(.5, .5)));
            t.Triggers.Add(hover); t.Triggers.Add(down);
            b.Template = t;
            if (main)
            {
                b.SetResourceReference(BackgroundProperty, "AccentGrad");
                b.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = .7, Color = Color.FromRgb(0x8B, 0x6C, 0xFF) };
            }
            else b.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                var (said, _, _, next) = await hands.Do(new IvoAction(kind, kind == "play" ? new() { ["query"] = "" } : []));
                await Task.Delay(500);
                next = (await hands.Do(new IvoAction("playing", []))).Song ?? next;
                holder.Child = next != null ? Music(next) : Text(said, 12.5, dim: true);
            };
            return b;
        }
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { Round("", 26, 10.5, false, "Previous", "previous"), Round(s.Playing ? "" : "", 32, 12.5, true, s.Playing ? "Pause" : "Play", s.Playing ? "pause" : "play"), Round("", 26, 10.5, false, "Next", "next") } };
        foreach (var c in controls.Children.OfType<Button>()) c.VerticalAlignment = VerticalAlignment.Center;

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 186, Children = { head, title, artist, track, times, controls } };
        var front = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 10, 12, 10), Children = { cover, words } };
        back.Children.Add(front);
        var card = new Border { CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1), Child = back, Margin = new Thickness(0, 2, 0, 4),
                                BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xA7, 0x8B, 0xFA)),
                                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = .35, Color = Colors.Black } };
        back.Clip = null;
        card.Loaded += (_, _) => back.Clip = new RectangleGeometry(new Rect(0, 0, back.ActualWidth, back.ActualHeight), 16, 16);
        back.SizeChanged += (_, _) => back.Clip = new RectangleGeometry(new Rect(0, 0, back.ActualWidth, back.ActualHeight), 16, 16);
        holder.Child = card;
        return holder;
    }

    // a little no: the box shakes side to side
    void Shake()
    {
        var move = new TranslateTransform(); box.RenderTransform = move;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        foreach (var (x, t) in new[] { (0.0, 0), (-9.0, 50), (9.0, 110), (-7.0, 170), (6.0, 230), (-3.0, 290), (0.0, 360) })
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        move.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    void PickSlash(string slash) { box.Text = slash + " "; box.CaretIndex = box.Text.Length; box.Focus(); }

    void DrawSlash()
    {
        if (slashMatches.Count == 0) return;
        var list = new StackPanel();
        var head = Text(slashPicked ? "USING" : "IVO CAN DO", 10.5, bold: true, dim: true);
        head.Margin = new Thickness(10, 0, 0, 6);
        list.Children.Add(head);
        for (var i = 0; i < slashMatches.Count; i++)
        {
            var (slash, args, hint, icon) = slashMatches[i];
            var lit = i == slashAt && !slashPicked;
            var badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 12, 0), Background = new SolidColorBrush(Color.FromArgb(0x33, 0x8B, 0x6C, 0xFF)) };
            var glyph = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ColorEmoji.Append(glyph.Inlines, icon, 15);
            badge.Child = glyph;
            var title = new TextBlock { FontSize = 13.5 };
            title.Inlines.Add(new System.Windows.Documents.Run(slash) { FontWeight = FontWeights.SemiBold });
            var argRun = new System.Windows.Documents.Run("  " + args) { FontSize = 12.5 };
            argRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Dim");
            title.Inlines.Add(argRun);
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var what = Text(hint, 12, dim: true); what.TextTrimming = TextTrimming.CharacterEllipsis; what.Margin = new Thickness(0, 1, 0, 0);
            var row = new DockPanel { Children = { badge, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, what } } } };
            DockPanel.SetDock(badge, Dock.Left);
            var item = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Cursor = Cursors.Hand,
                                    Background = lit ? new SolidColorBrush(Color.FromArgb(0x30, 0x8B, 0x6C, 0xFF)) : Brushes.Transparent };
            var at = i;
            item.MouseEnter += (_, _) => { if (!slashPicked && slashAt != at) { slashAt = at; DrawSlash(); } };
            item.MouseLeftButtonUp += (_, _) => PickSlash(slash);
            list.Children.Add(item);
        }
        // as tall as fits (about six rows), the rest a scroll away (no bar); the lit one always in view
        var scroll = new ScrollViewer { Content = list, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var keep = slashList.Child is ScrollViewer was ? was.VerticalOffset : 0;
        slashList.Child = scroll;
        scroll.Loaded += (_, _) =>
        {
            scroll.ScrollToVerticalOffset(keep);
            if (!slashPicked && slashAt + 1 < list.Children.Count) ((FrameworkElement)list.Children[slashAt + 1]).BringIntoView();   // +1: the header
        };
    }


    // a fresh start: Blub's hello, and nothing else (the words she has learnt stay)
    void Clear(string you)
    {
        SetReply(null);
        if (CanPersist(IvoMind.Incognito)) lines.Clear();   // clearing a private session must not erase the saved conversation
        log.Children.Clear();
        Add(new(false, IvoMind.Hello(you)));
    }

    // Blub saying something on her own (after the tour, say)
    public void Greet(string text) => Add(new(false, text));

    async void Say(string text)
    {
        text = text.Trim();
        if (text == "") return;
        var quote = replying;
        SetReply(null);
        box.Clear();
        var promptText = quote == null || IvoCommands.Parse(text, DateTime.Now) != null
            ? text : ReplyPrompt(text, quote.Mine, quote.Text);
        Add(new(true, text, ReplyText: quote?.Text, ReplyMine: quote?.Mine ?? false));
        var typing = Bubble(new(false, "…"));
        log.Children.Add(typing); scroller.ScrollToEnd();
        // asked for one of your agents or skills by name: it goes straight to it
        if (HandOffFor(text) is { } named)
        {
            var done = await HandOff(named.Agent, named.Skill, named.Task);
            log.Children.Remove(typing);
            Add(new(false, done));
            mind.Heard(true, promptText); mind.Heard(false, done);
            return;
        }
        var learnt = JsonSerializer.Serialize(IvoMind.Memory);
        var asked = DateTime.Now;
        IvoMind.Answer answer;
        try { answer = await mind.ReplyAsync(promptText); }
        catch { answer = new("Hmm, I lost my train of thought 🥺 say that again?"); }
        // A live card is the answer for read-only music queries; don't also run the command that prints the same list.
        var card = CardFor != null && (answer.Do == null || IsCardReadAction(answer.Do)) ? await CardFor(text) : null;
        if (card != null)
            answer = answer with { Text = card.Title + " 👇", Chat = false, Exact = true, Do = IsCardReadAction(answer.Do) ? null : answer.Do };
        // something she doesn't know how to do: with a chat model set up, the general agent takes it, with your skills
        var handedOff = false;
        if (answer.Lost && Planner.S?.Provider is { Length: > 0 } && promptText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3)
        { answer = answer with { Text = await HandOff("general", null, promptText), Lost = false }; handedOff = true; }
        // Smarter chat: her small talk in the model's words (actions, your data and app help stay hers)
        if (!handedOff && IvoMind.ModelSays(answer)) try { answer = answer with { Text = await mind.TalkAsync(promptText, answer) }; } catch { }   // the model failed: her own reply
        mind.Heard(true, promptText); mind.Heard(false, answer.Text);
        if (!IvoMind.Incognito && JsonSerializer.Serialize(IvoMind.Memory) != learnt) IvoMind.Memory.Save(IvoMemory.Path);
        var wait = Math.Clamp(answer.Text.Length * 6, 450, 1100) - (DateTime.Now - asked).TotalMilliseconds;   // a moment to "type", so it reads like her
        if (wait > 0) await Task.Delay((int)wait);
        log.Children.Remove(typing);
        var answerLine = Add(new(false, answer.Text));
        var spotifyConnections = answer.Text.Contains("Spotify", StringComparison.OrdinalIgnoreCase)
            && answer.Text.Contains("Settings → Connections", StringComparison.OrdinalIgnoreCase);
        if (answer.Buttons is { Length: > 0 } buttons)
        {
            if (spotifyConnections && buttons.Any(b => b.Action.Equals("open:connections", StringComparison.OrdinalIgnoreCase)))
                Under(new WrapPanel(), [RedirectCard("Connect Spotify", "Sign in to connect your account for music in Blub chat.", "🎧", [("Sign in with Spotify", "spotify-signin")])]);
            else Under(new WrapPanel(), [RedirectCard("Go to the right place", "Take a shortcut to continue.", "🔗", buttons)]);
        }
        else if (spotifyConnections)
            Under(new WrapPanel(), [RedirectCard("Connect Spotify", "Sign in to connect your account for music in Blub chat.", "🎧", [("Sign in with Spotify", "spotify-signin")])]);
        else if (answer.Text.Contains("Settings → Connections", StringComparison.OrdinalIgnoreCase))
            Under(new WrapPanel(), [RedirectCard("Open Connections", "Connect or manage a service account.", "🔌", [("Open Connections", "open:connections")])]);
        if (answer.Do is { } todo) await Act(todo);
        // the card for what you asked (not when the music card already shows what you asked about)
        if (card == null && CardFor != null && answer.Do != null) card = await CardFor(text);   // after an action (the queue…), its card too
        if (card != null)
        {
            var cardRow = new WrapPanel { Tag = answerLine.Id };
            Under(cardRow, [Live(card)]);
            UpdateLine(answerLine.Id, line => line with { Card = text });
        }
        if (answer.Offer is { } offer)
        {
            var row = new WrapPanel();
            Under(row, [Chip("Yes, do it", () => { log.Children.Remove(row); Say("yes"); }), Chip("No thanks", () => { log.Children.Remove(row); Say("no thanks"); })]);
        }

        if (answer.Action == "tour") { await Task.Delay(700); hands.Tour(); }
    }

    // ---------- her helpers: your agents and skills ----------
    // "/agent reviewer …", "ask the reviewer agent to …", "/skill pdf …", "use the pdf skill to …": which one, and the task
    public static (string Agent, string? Skill, string Task)? HandOffFor(string text)
    {
        var t = text.Trim();
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        if (Regex.Match(t, @"^/agent\s+(\S+)\s+(.+)$", o) is { Success: true } a) return Known(a.Groups[1].Value, null, a.Groups[2].Value);
        if (Regex.Match(t, @"^/skill\s+(\S+)\s+(.+)$", o) is { Success: true } k) return Known("general", k.Groups[1].Value, k.Groups[2].Value);
        if (Regex.Match(t, @"^(?:ask|get|have)\s+(?:the\s+|my\s+)?(\S+)\s+agent\s+(?:to\s+)?(.+)$", o) is { Success: true } a2) return Known(a2.Groups[1].Value, null, a2.Groups[2].Value);
        if (Regex.Match(t, @"^use\s+(?:the\s+|my\s+)?(\S+)\s+skill\s+(?:to\s+|for\s+|on\s+)?(.+)$", o) is { Success: true } k2) return Known("general", k2.Groups[1].Value, k2.Groups[2].Value);
        return null;
        // only ones that exist (otherwise it's an ordinary message for her)
        static (string, string?, string)? Known(string agent, string? skill, string task)
        {
            if (skill != null && !Skills.Enabled().Any(x => x.Name.Equals(skill, StringComparison.OrdinalIgnoreCase))) return null;
            if (!agent.Equals("general", StringComparison.OrdinalIgnoreCase) && !Subagents.All().Any(x => x.Name.Equals(agent, StringComparison.OrdinalIgnoreCase))) return null;
            return (agent, skill, task.Trim());
        }
    }

    // hand a task to an agent on your chat model (Settings → Chats); a pill under her typing says what it's doing as it
    // goes ("Using the pdf skill"), and her answer ends with which skills and agent it took
    async Task<string> HandOff(string agent, string? skill, string task)
    {
        var s = Planner.S;
        if (s == null || s.Provider == "") return "I'd need a chat model for that 💜 pick one in Settings → Chats and I'll hand it to your agents.";
        var parent = new Chat { Provider = s.Provider, Model = s.Models.GetValueOrDefault(s.Provider), Incognito = true };
        var skills = Skills.Enabled();
        var ask = skill != null ? $"Load the \"{skill}\" skill with use_skill first, then follow it to do this: {task}"
                                : task + (skills.Count > 0 ? "\n\n" + Skills.Prompt(skills) : "");
        var label = new TextBlock { Text = agent == "general" ? "🤖 Working on it…" : $"🤖 Asking the {agent} agent…", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var pill = new Border { Child = label, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 12, 5), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1) };
        pill.SetResourceReference(Border.BackgroundProperty, "InputBg"); pill.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        log.Children.Add(pill); scroller.ScrollToEnd();
        var used = new List<string>();
        void Follow() => Dispatcher.BeginInvoke(() =>
        {
            if (Subagents.RunsOf(parent.Id).LastOrDefault() is not { } run) return;
            var doing = run.Doing;
            if (Regex.Match(doing, @"^Use skill (\S+)") is { Success: true } sk)
            {
                if (!used.Contains(sk.Groups[1].Value)) used.Add(sk.Groups[1].Value);
                label.Text = $"🧩 Using the {sk.Groups[1].Value} skill";
            }
            else if (doing.StartsWith("Thinking")) label.Text = agent == "general" ? "🤖 Thinking…" : $"🤖 The {agent} agent is thinking…";
            else if (doing is not ("Done" or "Stopped" or "Starting")) label.Text = "⚙️ " + doing;
        });
        Subagents.Changed += Follow;
        string said;
        try { said = await Subagents.Run(parent, agent, ask, CancellationToken.None); }
        catch (Exception ex) { said = "That didn't work 🥺 " + ex.Message; }
        finally { Subagents.Changed -= Follow; log.Children.Remove(pill); }
        if (skill != null && !used.Contains(skill)) used.Insert(0, skill);
        var how = new List<string>();
        if (used.Count > 0) how.Add(used.Count == 1 ? $"the {used[0]} skill" : $"the {string.Join(", ", used)} skills");
        if (agent != "general") how.Add($"the {agent} agent");
        return how.Count == 0 ? said : said.TrimEnd() + $"\n\n🧩 Used {string.Join(" and ", how)}";
    }

    Button incognito = null!;
    Border privacy = null!;

    // the saved chat, the latest of it (older lines stay saved), each with its live card
    void Saved()
    {
        log.Children.Clear();
        foreach (var l in lines.TakeLast(60))
        {
            log.Children.Add(Bubble(l));
            if (l.Card is { } asked && CardFor != null) CardAgain(asked, l.Id);
        }
        scroller?.ScrollToEnd();   // (not there yet while the chat is first built)
    }

    // incognito on: a fresh, private chat (nothing saved, nothing remembered); off: it's gone, the saved chat's back, and
    // anything she picked up in it is forgotten
    void Private(bool on)
    {
        var was = IvoMind.Incognito;
        SetReply(null);
        IvoMind.Incognito = on;
        incognito.Foreground = on ? new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)) : (Brush)FindResource("Dim");
        incognito.ToolTip = on ? "Leave incognito (this chat goes)" : "Incognito: nothing saved or remembered";
        privacy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        mind = new IvoMind(you) { Name = mind.Name };   // a fresh start either way: the private chat isn't carried in or out
        if (on) { log.Children.Clear(); log.Children.Add(Bubble(new(false, "Incognito 🕶️ nothing here is saved, and I won't remember it. What's up?"))); }
        else
        {
            if (was) IvoMind.Memory = IvoMemory.Load(IvoMemory.Path);   // forget what she picked up while it was on
            Saved();
        }
    }

    // the app's cards for what you asked (MainWindow.IvoCards)
    public static Func<string, Task<InfoCard?>>? CardFor;

    // a live card: redrawn every 20 seconds while it's on screen; a tap opens where it lives
    FrameworkElement Live(InfoCard first)
    {
        var holder = new Border { Child = Info(first) };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = first.RefreshEvery ?? TimeSpan.FromSeconds(20) };
        var current = first;
        var refreshing = false;
        timer.Tick += async (_, _) =>
        {
            if (current.Again == null) { timer.Stop(); return; }
            if (refreshing) return;
            refreshing = true;
            try { if (await current.Again() is { } fresh) { current = fresh; timer.Interval = fresh.RefreshEvery ?? TimeSpan.FromSeconds(20); holder.Child = Info(fresh); } }
            catch { }
            finally { refreshing = false; }
        };
        holder.Loaded += (_, _) => timer.Start();
        holder.Unloaded += (_, _) => timer.Stop();
        return holder;
    }

    // the card, as rich as the music one: the artwork blurred behind it (or a soft wash of its colour), the cover or an
    // icon tile, the headline, then its rows
    FrameworkElement Info(InfoCard c)
    {
        var tint = (Color)ColorConverter.ConvertFromString(c.Color);
        var onArt = c.Art != null;
        Brush Ink(double a = 1) => onArt ? new SolidColorBrush(Color.FromArgb((byte)(255 * a), 255, 255, 255)) : (Brush)FindResource(a < 1 ? "Dim" : "Fg");
        TextBlock T(string t, double size, bool bold = false, double a = 1, bool wrap = false) => new() { Text = t, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Ink(a), TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Border Tile(ImageSource? pic, string? glyph, double size, bool round, double corner)
        {
            var radius = new CornerRadius(round ? size / 2 : corner);
            if (pic != null)
            {
                var img = new Border { Width = size, Height = size, CornerRadius = radius, Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return img;
            }
            return new Border { Width = size, Height = size, CornerRadius = radius, Background = new SolidColorBrush(Color.FromArgb(0x33, tint.R, tint.G, tint.B)),
                                Child = new TextBlock { Text = glyph ?? "•", FontSize = size * 0.45, FontFamily = ColorEmoji.EmojiFontFamily, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        }

        // the top: cover (or icon), headline, what it is
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, c.Rows.Length > 0 ? 12 : 0) };
        var top = Tile(c.Art, c.Icon, 56, false, 12); top.Margin = new Thickness(0, 0, 14, 0);
        if (onArt) top.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .5, Color = Colors.Black };
        DockPanel.SetDock(top, Dock.Left); head.Children.Add(top);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T(c.Title, 17, bold: true) } };
        if (c.Sub is { Length: > 0 } sub) words.Children.Add(T(sub, 12, a: .75));
        head.Children.Add(words);

        var body = new StackPanel { Children = { head } };
        foreach (var r in c.Rows)
        {
            var row = new DockPanel { Margin = new Thickness(0, c.CompactRows ? 1 : 4, 0, c.CompactRows ? 1 : 4), Opacity = r.Faded ? .5 : 1 };
            if (!c.CompactRows)
            {
                var pic = Tile(r.Pic, r.Glyph, 34, r.Round, 8); pic.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(pic, Dock.Left); row.Children.Add(pic);
            }
            if (r.Right is { Length: > 0 } right) { var rt = T(right, 11.5, a: .7); rt.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(rt, Dock.Right); row.Children.Add(rt); }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T(r.Title, 13, bold: r.Highlight, a: r.Faded ? .55 : 1, wrap: c.CompactRows) } };
            if (r.Sub is { Length: > 0 } rs) text.Children.Add(T(rs, 11, a: .65));
            if (r.Share is { } share)
            {
                var track = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
                track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)) });
                var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(r.Bar ?? c.Color)) };
                track.SizeChanged += (_, _) => fill.Width = Math.Max(3, track.ActualWidth * share);
                track.Children.Add(fill);
                text.Children.Add(track);
            }
            row.Children.Add(text);
            body.Children.Add(row);
        }
        if (c.Open != null)
        {
            var open = T("Open  ›", 12, bold: true); if (!onArt) open.Foreground = new SolidColorBrush(tint); open.HorizontalAlignment = HorizontalAlignment.Right; open.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(open);
        }

        // the back: the artwork blown up and blurred under a dark wash, or a soft wash of the card's colour
        var back = new Grid { ClipToBounds = true };
        if (onArt)
        {
            back.Children.Add(new Border { Background = new ImageBrush(c.Art) { Stretch = Stretch.UniformToFill }, Opacity = .6, Margin = new Thickness(-30), Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40 } });
            back.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0x99, 10, 10, 18), Color.FromArgb(0xE6, 10, 10, 18), 90) });
        }
        else back.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0x38, tint.R, tint.G, tint.B), Color.FromArgb(0x08, tint.R, tint.G, tint.B), 45) });
        var inner = new Grid { Children = { back, new Border { Padding = new Thickness(16, 14, 16, 14), Child = body } } };
        var card = new Border { CornerRadius = new CornerRadius(18), Width = 300, Child = inner, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, tint.R, tint.G, tint.B)) };
        card.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        inner.SizeChanged += (_, _) => inner.Clip = new RectangleGeometry(new Rect(0, 0, inner.ActualWidth, inner.ActualHeight), 17, 17);
        if (c.Open is { } where) { card.Cursor = Cursors.Hand; card.MouseLeftButtonUp += (_, _) => hands.Open(where); }
        return card;
    }

    // a saved card, made again under its bubble (in its place, even though it arrives a moment later)
    async void CardAgain(string asked, string? id = null)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6), Tag = id };
        log.Children.Add(row);
        try { if (await CardFor!(asked) is { } card) row.Children.Add(Live(card)); else log.Children.Remove(row); } catch { log.Children.Remove(row); }
    }

    // things under her last bubble: buttons, or what she did
    void Under(Panel row, IEnumerable<UIElement> items)
    {
        row.Margin = new Thickness(0, 0, 0, 6);
        foreach (var i in items) row.Children.Add(i);
        log.Children.Add(row); scroller.ScrollToEnd();
    }

    // something she's doing for you: done at once with an Undo, or (calls, messages, weighty settings) Do it / Cancel first
    async Task Act(IvoAction todo)
    {
        var row = new WrapPanel();
        async Task Run()
        {
            row.Children.Clear();
            row.Children.Add(Text("…", 12.5, dim: true));
            (string said, Action? undo, (string Label, IvoAction Then)[] pick, SongCard? song) = ("", null, [], null);
            try { (said, undo, pick, song) = await hands.Do(todo); } catch (Exception ex) { said = $"That didn't work: {ex.Message} 🥺"; }
            row.Children.Clear();
            if (song != null) { liveRow = row; liveSong = song; row.Children.Add(liveCard = Music(song)); return; }
            if (said.StartsWith("Connect Spotify first:", StringComparison.OrdinalIgnoreCase)
                || said.Contains("reconnect it in Settings → Connections", StringComparison.OrdinalIgnoreCase))
            {
                row.Children.Add(RedirectCard("Connect Spotify", "Sign in to connect your account for music in Blub chat.", "🎧", [("Sign in with Spotify", "spotify-signin")]));
                return;
            }
            var done = Text(said, 12.5, dim: true); done.VerticalAlignment = VerticalAlignment.Center; done.Margin = new Thickness(2, 0, 8, 0); done.TextWrapping = TextWrapping.Wrap; done.MaxWidth = 230;
            row.Children.Add(done);
            if (undo != null)
            {
                Button? back = null;
                back = Chip("Undo", () => { try { undo(); done.Text = "Undone ↩️"; } catch (Exception ex) { done.Text = $"Couldn't undo: {ex.Message}"; } row.Children.Remove(back!); });
                row.Children.Add(back);
            }
            foreach (var (label, then) in pick)
                row.Children.Add(Chip(label, then.Kind == "url" ? () => _ = hands.Do(then)   // a link: it opens, and the others stay
                                                                : () => { row.Children.Clear(); row.Children.Add(Text(label, 12.5, dim: true)); _ = Act(then); }));
        }
        if (hands.NeedsOk(todo))
        {
            Under(row, [Chip("Do it", () => _ = Run()), Chip("Cancel", () => { row.Children.Clear(); row.Children.Add(Text("Cancelled", 12.5, dim: true)); })]);
            return;
        }
        Under(row, []);
        await Run();
    }

    // double-click: she looks your message up and answers it again, without sending it again
    async void Relearn(string said)
    {
        var typing = Bubble(new(false, "…"));
        log.Children.Add(typing); scroller.ScrollToEnd();
        var learnt = JsonSerializer.Serialize(IvoMind.Memory);
        IvoMind.Answer answer;
        try { answer = await mind.ReplyAsync("/learn " + said); } catch { answer = new("Hmm, I couldn't look that up right now 🥺"); }
        if (!IvoMind.Incognito && JsonSerializer.Serialize(IvoMind.Memory) != learnt) IvoMind.Memory.Save(IvoMemory.Path);
        log.Children.Remove(typing);
        var note = Text($"🔎 Looked up \"{(said.Length > 40 ? said[..40] + "…" : said)}\"", 11.5, dim: true); note.Margin = new Thickness(40, 6, 0, 0);
        log.Children.Add(note);
        Add(new(false, answer.Text));
    }

    internal static string ReplyPrompt(string text, bool replyMine, string quoted) =>
        $"The user is replying to {(replyMine ? "their own earlier message" : "your earlier message")}: “{(quoted.Length > 600 ? quoted[..600] + "…" : quoted)}”. Their message is: {text}";

    internal static string MessageTime(DateTime at, DateTime today) =>
        at.Date == today.Date ? at.ToString("h:mm tt") : at.ToString("d MMM · h:mm tt");

    internal static bool CanPersist(bool incognito) => !incognito;

    internal static bool IsCardReadAction(IvoAction? action) => action?.Kind switch
    {
        "lyrics" or "song" or "artist" or "playing" => true,
        "music" => action.Args.GetValueOrDefault("op") is "top" or "queue" or "recent",
        _ => false,
    };

    void SetReply(Line? line)
    {
        replying = line;
        replyBar.Visibility = line == null ? Visibility.Collapsed : Visibility.Visible;
        if (line != null)
        {
            var quote = line.Text.Length > 90 ? line.Text[..90] + "…" : line.Text;
            replyLabel.Text = $"Replying to {(line.Mine ? "you" : "Blub")}: {quote}";
        }
    }

    void UpdateLine(string? id, Func<Line, Line> update)
    {
        if (id == null) return;
        var at = lines.FindIndex(x => x.Id == id);
        if (at >= 0) { lines[at] = update(lines[at]); Persist(); }
    }

    void Persist()
    {
        if (!CanPersist(IvoMind.Incognito)) return;
        try { Directory.CreateDirectory(Store.Dir); Store.WriteData(FilePath, JsonSerializer.Serialize(lines)); } catch { }
    }

    void DeleteLine(string? id)
    {
        if (id == null) return;
        lines.RemoveAll(x => x.Id == id);
        foreach (var row in log.Children.OfType<FrameworkElement>().Where(x => x.Tag as string == id).ToArray())
            log.Children.Remove(row);
        if (replying?.Id == id) SetReply(null);
        Persist();
    }

    Line Add(Line l)
    {
        l = l with { At = l.At == default ? DateTime.Now : l.At, Id = l.Id is { Length: > 0 } ? l.Id : Guid.NewGuid().ToString("N") };
        if (CanPersist(IvoMind.Incognito))   // incognito: on screen only
        {
            lines.Add(l);
            if (lines.Count > 300) lines.RemoveRange(0, lines.Count - 300);
            Persist();
        }
        var b = Bubble(l);
        log.Children.Add(b);
        b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        scroller.ScrollToEnd();
        return l;
    }

    FrameworkElement Bubble(Line l)
    {
        FrameworkElement content;
        content = TextBubble(l);
        // Temporary typing bubbles have no id; real text lines get their right-click menu (with the time).
        if (l.Text == "" || l.Id == null) return content;

        // right-click it: the window blurs, it lifts out, with when it was sent and Copy, Reply, Delete (as in Portal's chats)
        var message = new StackPanel { HorizontalAlignment = l.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Tag = l.Id };
        content.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var lifted = BubbleOf(content);
            MessageLift.Show(lifted, l.Mine, l.At == default ? DateTime.Now : l.At, [
                new("\uE8C8", "Copy", () => { try { Clipboard.SetText(l.Text); } catch { } }),
                new("\uE97A", "Reply", () => SetReply(l)),
                new("\uE74D", "Delete", () => DeleteLine(l.Id), Danger: true)]);
        };
        if (l.ReplyText is { Length: > 0 } quoted)
        {
            var quote = Text($"{(l.ReplyMine ? "You" : "Blub")}: {(quoted.Length > 90 ? quoted[..90] + "…" : quoted)}", 10.5, dim: true);
            quote.TextWrapping = TextWrapping.Wrap; quote.MaxWidth = 270;
            message.Children.Add(new Border { Child = quote, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 2, 0, 0),
                                              HorizontalAlignment = l.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                                              CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(0x20, 0xA7, 0x8B, 0xFA)) });
        }
        message.Children.Add(content);
        return message;
    }

    // the rounded bubble holding a line's words
    static FrameworkElement BubbleOf(FrameworkElement e) =>
        e is Border { Child: TextBlock } ? e
        : e is Panel p ? p.Children.OfType<FrameworkElement>().Select(BubbleOf).FirstOrDefault(x => x is Border { Child: TextBlock }) ?? e
        : e;

    FrameworkElement TextBubble(Line l)
    {
        var words = Text("", 14);
        ColorEmoji.Append(words.Inlines, l.Text, 14);
        words.TextWrapping = TextWrapping.Wrap; words.LineHeight = 21;
        if (l.Mine) words.Foreground = Brushes.White;
        var b = new Border { Child = words, Padding = new Thickness(14, 10, 14, 10), MaxWidth = 290, Margin = new Thickness(0, 4, 0, 4),
                             CornerRadius = l.Mine ? new CornerRadius(18, 18, 4, 18) : new CornerRadius(18, 18, 18, 4),
                             HorizontalAlignment = l.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        // double-click a message, now or from before: she looks it up online and learns how to answer it (her reply:
        // the message of yours it answered)
        b.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || IvoMind.Incognito) return;
            e.Handled = true;
            var at = lines.LastIndexOf(l);
            var mine = l.Mine ? l : lines.Take(at < 0 ? lines.Count : at).LastOrDefault(x => x.Mine && x.Text != "");
            if (mine is { Text: { Length: > 0 } said } && !said.StartsWith('/')) Relearn(said);
        };
        if (l.Mine) b.SetResourceReference(Border.BackgroundProperty, "AccentGrad");
        else { b.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x8B, 0x6C, 0xFF)); b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xA7, 0x8B, 0xFA)); b.BorderThickness = new Thickness(1); }
        return b;
    }

    Button Chip(string text, Action click)
    {
        var words = new TextBlock(); ColorEmoji.Append(words.Inlines, text, 11.5);   // use the installed Windows emoji font, like the bubbles
        var b = new Button { Content = words, FontSize = 11.5, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2), Cursor = Cursors.Hand };
        b.SetResourceReference(Control.ForegroundProperty, "Fg");
        b.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x8B, 0x6C, 0xFF));
        b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xA7, 0x8B, 0xFA));
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = bd;
        b.Template = t;
        b.Click += (_, _) => click();
        return b;
    }

    FrameworkElement RedirectCard(string title, string description, string icon, IEnumerable<(string Label, string Action)> actions)
    {
        var body = new StackPanel();
        var glyph = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ColorEmoji.Append(glyph.Inlines, icon, 18);
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0),
                                 Background = new SolidColorBrush(Color.FromArgb(0x33, 0x8B, 0x6C, 0xFF)), Child = glyph };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Text(title, 14, bold: true) } };
        var detail = Text(description, 11.5, dim: true); detail.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(detail);
        body.Children.Add(new DockPanel { Children = { badge, words } });

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, action) in actions)
        {
            if (!action.StartsWith("open:", StringComparison.OrdinalIgnoreCase)
                && !action.Equals("spotify-signin", StringComparison.OrdinalIgnoreCase)) continue;
            var button = new Button { Content = label, Style = (Style)FindResource("Shimmer"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            if (action.Equals("spotify-signin", StringComparison.OrdinalIgnoreCase))
            {
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    detail.Text = "Waiting for Spotify sign-in in your browser…";
                    try
                    {
                        var who = await hands.SpotifySignIn();
                        detail.Text = $"Connected as {who}.";
                        button.Content = "Spotify connected";
                    }
                    catch (Exception ex)
                    {
                        detail.Text = string.IsNullOrWhiteSpace(ex.Message) || ex.Message.Length > 120
                            ? "Sign-in didn't finish. Try again."
                            : ex.Message;
                        button.IsEnabled = true;
                    }
                };
            }
            else if (action.StartsWith("open:", StringComparison.OrdinalIgnoreCase))
            {
                var target = action["open:".Length..];
                button.Click += (_, _) => hands.Open(target);
            }
            else continue;
            buttons.Children.Add(button);
        }
        if (buttons.Children.Count > 0) body.Children.Add(buttons);

        var card = new Border { Width = 300, Padding = new Thickness(12), Margin = new Thickness(0, 2, 0, 4), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        return card;
    }

    static ControlTemplate Round()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp); t.VisualTree = bd;
        return t;
    }

    static ControlTemplate Pill()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var host = new FrameworkElementFactory(typeof(ScrollViewer)); host.Name = "PART_ContentHost";
        host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(host); t.VisualTree = bd;
        return t;
    }

    static TextBlock Text(string s, double size, bool bold = false, bool dim = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "Dim" : "Fg");
        return t;
    }
}
