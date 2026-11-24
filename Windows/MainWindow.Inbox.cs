using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// The Inbox: everything reminders and tasks deliver, in a floating window (MainWindow.InboxWheel.cs) with search, filters,
// sorting and the desktop-notifications switch. Cards and the reader live here.
public partial class MainWindow
{
    public void OpenInbox()
    {
        if (MainHidden && floating != WheelLayer) { Float(WheelLayer, 920, 740, OpenWheel); return; }   // window closed: the Inbox floats on its own
        if (floating == WheelLayer) { popout!.Activate(); return; }
        ShowWindow();
        ScheduleLayer.Visibility = Visibility.Collapsed;
        if (!WheelLayer.IsVisible) OpenWheel();
    }

    void OpenInbox_Click(object s, RoutedEventArgs e) { if (WheelLayer.IsVisible) CloseWheel_Click(s, e); else OpenInbox(); }

    void UpdateInboxBadge()
    {
        var n = Inbox.Unread;
        InboxDot.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxCount.Text = n > 9 ? "9+" : n.ToString();
        BtnInbox.ToolTip = n > 0 ? $"Inbox: {n} new" : "Inbox";
    }

    // after any change to an item: the badge, and the window if it's open
    void DrawInbox()
    {
        UpdateInboxBadge();
        if (WheelLayer.IsVisible) FillWheel();
    }

    void InboxBell_Click(object s, RoutedEventArgs e)
    {
        S.DesktopNotify = !S.DesktopNotify; SaveSettings();
        loading = true; NotifyBox.IsChecked = S.DesktopNotify; loading = false;
        DrawBell();
    }

    void DrawBell()
    {
        InboxBell.Opacity = S.DesktopNotify ? 1 : 0.4;
        InboxBell.Foreground = S.DesktopNotify ? Shell.B("#A78BFA") : (Brush)FindResource("Dim");
        InboxBell.ToolTip = S.DesktopNotify ? "Notifications on" : "Notifications off";
    }

    // One item: a round coloured badge, then the name and time, what it's about, and the text (long text folds);
    // actions show on hover. No borders or stripes: rows are set apart by space and a faint hover tint.
    FrameworkElement InboxCard(InboxItem i)
    {
        var (name, icon, color) = i.Failed ? ("Failed", "\uE783", "#F85149")
            : i.Kind switch
            {
                "reminder" => ("Reminder", "\uEA8F", "#F2B84B"),
                "briefing" => ("Morning briefing", "\uE706", "#FB923C"),
                "dev" => ("Dev task", "\uE943", "#58A6FF"),
                "later" => ("Read later", "", "#3FB950"),
                _ => Category(i.Category) is var c ? (c.Name, c.Icon, c.Color) : default,
            };
        var tint = ((SolidColorBrush)Shell.B(color)).Color;

        Button Act(string glyph, string tip, Action run)
        {
            var b = new Button { Content = glyph, FontFamily = App.IconFont, FontSize = 14, Style = (Style)FindResource("Ghost"), ToolTip = tip };
            b.Click += (_, _) => run();
            return b;
        }
        Button Pill(string label, Action run, bool primary = false)
        {
            var b = new Button { Content = label, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            if (primary) b.Style = (Style)FindResource("Shimmer");
            b.Click += (_, _) => run();
            return b;
        }
        TextBlock Dim(string text, double size)
        {
            var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            return t;
        }

        // left: the badge
        var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0),
                                 Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B)),
                                 Child = new TextBlock { Text = icon, FontFamily = App.IconFont, FontSize = 15, Foreground = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };

        // top line: name, unread dot · time · model; actions (on hover) at the right
        var nameText = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Shell.B("#A78BFA"), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                               Visibility = i.Read && !(i.Kind == "reminder" && !i.Done) ? Visibility.Collapsed : Visibility.Visible };
        var when = Dim($"  ·  {When.Time(i.At)}" + (i.Model != "" ? $"  ·  {i.Model}" : ""), 11.5);
        when.ToolTip = When.Full(i.At);
        when.TextTrimming = TextTrimming.CharacterEllipsis;
        var star = Act(i.Starred ? "\uE735" : "\uE734", i.Starred ? "Unstar" : "Star (kept for good)", () => { i.Starred = !i.Starred; Inbox.Save(); DrawInbox(); });
        if (i.Starred) star.Foreground = Shell.B("#F2B84B");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -4, 0) };
        if (i.Kind != "reminder" && i.Text != "")
        {
            actions.Children.Add(Act("\uE8C8", "Copy", () => Clipboard.SetText(i.Text)));
            if (S.ReadAloud) { Button? speak = null; speak = Act("\uE767", "Read aloud", () => ToggleRead(speak!, i.Text)); Speaker(speak, i.Text); actions.Children.Add(speak); }
        }
        actions.Children.Add(Act("\uE74D", "Delete", () => { Inbox.All.Remove(i); Inbox.Save(); DrawInbox(); }));
        actions.Children.Add(star);
        foreach (UIElement a in actions.Children) if (a != star || !i.Starred) a.Opacity = 0;   // a star you set stays visible
        DockPanel.SetDock(actions, Dock.Right);
        var top = new DockPanel { Children = { actions, new StackPanel { Orientation = Orientation.Horizontal, Children = { nameText, dot, when } } } };

        var content = new StackPanel { Children = { top } };
        if (i.Kind == "reminder")
        {
            var words = new TextBlock { Text = i.Title, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 22 };
            words.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            content.Children.Add(words);
            if (!i.Done)
                content.Children.Add(new WrapPanel
                {
                    Margin = new Thickness(0, 12, 0, 0),
                    Children =
                    {
                        Pill("Done", () => { i.Done = true; i.Read = true; Inbox.Save(); DrawInbox(); }, primary: true),
                        Pill("In 10 min", () => { Snooze(i.Title, 10); Inbox.All.Remove(i); Inbox.Save(); DrawInbox(); }),
                        Pill("In 1 hour", () => { Snooze(i.Title, 60); Inbox.All.Remove(i); Inbox.Save(); DrawInbox(); }),
                        Pill("After lunch", () => { SnoozeUntil(i.Title, "after lunch"); Inbox.All.Remove(i); Inbox.Save(); DrawInbox(); }),
                        Pill("This evening", () => { SnoozeUntil(i.Title, "this evening"); Inbox.All.Remove(i); Inbox.Save(); DrawInbox(); }),
                        Pill("Tomorrow", () => { SnoozeUntil(i.Title, "tomorrow morning"); Inbox.All.Remove(i); Inbox.Save(); DrawInbox(); }),
                    },
                });
        }
        else
        {
            if (i.Kind == "briefing")
            {
                var head = new TextBlock { Text = i.Title.Replace("Morning briefing · ", ""), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
                head.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                content.Children.Add(head);
            }
            else
            {
                var asked = Dim(i.Title, 12);
                asked.Margin = new Thickness(0, 3, 0, 0); asked.TextTrimming = TextTrimming.CharacterEllipsis; asked.ToolTip = i.Title;
                content.Children.Add(asked);
            }
            // the text, folded to a preview when long (a fade and Show more)
            var text = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var el in active.RenderMarkdown(i.Text, user: false)) text.Children.Add(el);
            var longText = i.Text.Length > 700 && i.Kind != "dev";
            const double folded = 230;
            var holder = new Grid { Children = { text }, ClipToBounds = true, MaxHeight = longText ? folded : double.PositiveInfinity };
            // the folded text fades out itself (an opacity mask), so it looks right on whatever card it's on
            var fade = new LinearGradientBrush(new GradientStopCollection { new(Colors.Black, 0), new(Colors.Black, 1 - 46 / folded), new(Colors.Transparent, 1) }, 90);
            if (longText) holder.OpacityMask = fade;
            content.Children.Add(holder);
            var more = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (longText)
            {
                var toggle = new Button { Content = "Show more", Style = (Style)FindResource("GhostText"), Foreground = Shell.B("#A78BFA"), Margin = new Thickness(-7, 0, 6, 0) };
                toggle.Click += (_, _) =>
                {
                    var open = double.IsPositiveInfinity(holder.MaxHeight);
                    holder.MaxHeight = open ? folded : double.PositiveInfinity;
                    holder.OpacityMask = open ? fade : null;
                    toggle.Content = open ? "Show more" : "Show less";
                };
                more.Children.Add(toggle);
            }
            if (i.Kind == "dev" && i.ChatId is { } id)
                more.Children.Add(Pill("Open the chat", () => { if (allChats.FirstOrDefault(c => c.Id == id) is { } c) { CloseWheel_Click(this, new RoutedEventArgs()); ShowWindow(); OpenChat(c); } }, primary: true));
            else if (i.Kind is "daily" or "briefing")
            {
                var go = new Button { Content = "Continue in a chat  →", Style = (Style)FindResource("GhostText"), Margin = new Thickness(-7, 0, 0, 0) };
                go.Click += (_, _) => ContinueInChat(i);
                more.Children.Add(go);
            }
            if (more.Children.Count > 0) content.Children.Add(more);
        }

        DockPanel.SetDock(badge, Dock.Left);
        var row = new Border { Padding = new Thickness(12, 12, 12, 12), Margin = new Thickness(-12, 0, -12, 6), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent,
                               Child = new DockPanel { Children = { badge, content } }, Opacity = i.Kind == "reminder" && i.Done ? 0.55 : 1 };
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != row; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ButtonBase or System.Windows.Documents.Hyperlink or TextBox) return;
            OpenReader(i);
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x90));
        row.MouseEnter += (_, _) => { row.Background = hover; foreach (UIElement a in actions.Children) a.Opacity = 1; };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; foreach (UIElement a in actions.Children) if (a != star || !i.Starred) a.Opacity = 0; };
        return row;
    }

    // ---------- the reader: one item at full width, nothing folded ----------
    void ReaderTitle_Click(object s, MouseButtonEventArgs e) => ReaderTitle.MaxHeight = ReaderTitle.MaxHeight == 20 ? double.PositiveInfinity : 20;

    void OpenReader(InboxItem i)
    {
        var (name, icon, color) = i.Failed ? ("Failed", "\uE783", "#F85149")
            : i.Kind switch
            {
                "reminder" => ("Reminder", "\uEA8F", "#F2B84B"),
                "briefing" => ("Morning briefing", "\uE706", "#FB923C"),
                "dev" => ("Dev task", "\uE943", "#58A6FF"),
                "later" => ("Read later", "", "#3FB950"),
                _ => Category(i.Category) is var c ? (c.Name, c.Icon, c.Color) : default,
            };
        var tint = ((SolidColorBrush)Shell.B(color)).Color;
        ReaderBadge.Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B));
        ReaderIcon.Text = icon; ReaderIcon.Foreground = new SolidColorBrush(tint);
        ReaderName.Text = i.Kind == "briefing" ? i.Title : name;
        ReaderMeta.Text = $"{When.Day(i.At)}, {When.Time(i.At)}" + (i.Model != "" ? $"  ·  {i.Model}" : "");
        ReaderTitle.Text = i.Kind is "briefing" ? "" : i.Title;
        ReaderTitle.MaxHeight = 20;   // the prompt starts folded to one line
        ReaderTitle.Visibility = ReaderTitle.Text == "" ? Visibility.Collapsed : Visibility.Visible;

        ReaderBody.Children.Clear();
        if (i.Kind == "reminder")
            ReaderBody.Children.Add(new TextBlock { Text = i.Title, FontSize = 18, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Fg") });
        if (i.Kind != "reminder") foreach (var el in active.RenderMarkdown(i.Text, user: false)) ReaderBody.Children.Add(el);
        ReaderTitle.Visibility = i.Kind == "reminder" ? Visibility.Collapsed : ReaderTitle.Visibility;

        Button Act(string glyph, string tip, Action run)
        {
            var b = new Button { Content = glyph, FontFamily = App.IconFont, FontSize = 15, Style = (Style)FindResource("Ghost"), ToolTip = tip };
            b.Click += (_, _) => run();
            return b;
        }
        ReaderActions.Children.Clear();
        var star = Act(i.Starred ? "\uE735" : "\uE734", i.Starred ? "Unstar" : "Star (kept for good)", () => { i.Starred = !i.Starred; Inbox.Save(); OpenReader(i); DrawInbox(); });
        if (i.Starred) star.Foreground = Shell.B("#F2B84B");
        if (i.Kind != "reminder" && i.Text != "")
        {
            ReaderActions.Children.Add(Act("\uE8C8", "Copy", () => Clipboard.SetText(i.Text)));
            if (S.ReadAloud) { Button? speak = null; speak = Act("\uE767", "Read aloud", () => ToggleRead(speak!, i.Text)); Speaker(speak, i.Text); ReaderActions.Children.Add(speak); }
        }
        ReaderActions.Children.Add(star);
        ReaderActions.Children.Add(Act("\uE74D", "Delete", () => { Inbox.All.Remove(i); Inbox.Save(); CloseReader_Click(this, new RoutedEventArgs()); DrawInbox(); }));
        ReaderActions.Children.Add(Act("\uE8BB", "Close (Esc)", () => CloseReader_Click(this, new RoutedEventArgs())));

        ReaderFoot.Children.Clear();
        Button Pill(string label, Action run, bool primary = false)
        {
            var b = new Button { Content = label, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            if (primary) b.Style = (Style)FindResource("Shimmer");
            b.Click += (_, _) => run();
            return b;
        }
        if (i.Kind == "reminder" && !i.Done)
        {
            ReaderFoot.Children.Add(Pill("Done", () => { i.Done = true; Inbox.Save(); CloseReader_Click(this, new RoutedEventArgs()); DrawInbox(); }, primary: true));
            ReaderFoot.Children.Add(Pill("In 10 min", () => { Snooze(i.Title, 10); Inbox.All.Remove(i); Inbox.Save(); CloseReader_Click(this, new RoutedEventArgs()); DrawInbox(); }));
            ReaderFoot.Children.Add(Pill("In 1 hour", () => { Snooze(i.Title, 60); Inbox.All.Remove(i); Inbox.Save(); CloseReader_Click(this, new RoutedEventArgs()); DrawInbox(); }));
            foreach (var (label, when) in new[] { ("After lunch", "after lunch"), ("This evening", "this evening"), ("Tomorrow", "tomorrow morning") })
                ReaderFoot.Children.Add(Pill(label, () => { SnoozeUntil(i.Title, when); Inbox.All.Remove(i); Inbox.Save(); CloseReader_Click(this, new RoutedEventArgs()); DrawInbox(); }));
        }
        else if (i.Kind == "dev" && i.ChatId is { } id)
            ReaderFoot.Children.Add(Pill("Open the chat", () => { if (allChats.FirstOrDefault(c => c.Id == id) is { } c) { ReaderLayer.Visibility = Visibility.Collapsed; CloseWheel_Click(this, new RoutedEventArgs()); ShowWindow(); OpenChat(c); } }, primary: true));
        else if (i.Kind == "later")
        {
            var url = System.Text.RegularExpressions.Regex.Match(i.Text, @"\((https?://[^)\s]+)\)").Groups[1].Value;
            if (url != "") ReaderFoot.Children.Add(Pill("Open", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }, primary: true));
            if (!i.Done) ReaderFoot.Children.Add(Pill("Read it", () => { i.Done = true; Inbox.Save(); CloseReader_Click(this, new RoutedEventArgs()); DrawInbox(); }));
        }
        else if (i.Kind is "daily" or "briefing")
        {
            ReaderFoot.Children.Add(Pill("Continue in a chat", () => { ReaderLayer.Visibility = Visibility.Collapsed; ContinueInChat(i); }));
            var links = System.Text.RegularExpressions.Regex.Matches(i.Text, @"\[([^\]]+)\]\((https?://[^)\s]+)\)").Select(m => (Title: m.Groups[1].Value, Url: m.Groups[2].Value)).DistinctBy(l => l.Url).ToList();
            if (links.Count > 0) ReaderFoot.Children.Add(Pill($"Save {links.Count} link{(links.Count == 1 ? "" : "s")} for later", () => SaveForLater(links)));
        }
        if (i.Kind != "reminder" && i.Text != "") ReaderFoot.Children.Add(Pill("Draft email", () => DraftEmail(i.Kind == "briefing" ? "Briefing" : i.Title, i.Text)));
        ReaderFoot.Visibility = ReaderFoot.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!i.Read) { i.Read = true; Inbox.Save(); }
        ReaderScroll.ScrollToTop();
        ReaderLayer.Visibility = Visibility.Visible;
        ReaderCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    void CloseReader_Click(object s, RoutedEventArgs e) => ReaderLayer.Visibility = Visibility.Collapsed;

    // the read-later queue: one Inbox card per link, under the "Read later" chip until marked read
    void SaveForLater(List<(string Title, string Url)> links)
    {
        var have = Inbox.All.Where(x => x.Kind == "later").Select(x => x.Text).ToHashSet();
        var added = 0;
        foreach (var (title, url) in links)
        {
            var text = $"[{title}]({url})";
            if (have.Contains(text)) continue;
            Inbox.Add(new InboxItem { Kind = "later", Title = title, Text = text, Read = true }); added++;
        }
        NotifyCard.Show("Saved for later", added == 0 ? "Already in the queue" : $"{added} link{(added == 1 ? "" : "s")} under Read later in the Inbox", "\uE7BC", "#3FB950", () => OpenInbox());
        DrawInbox();
    }

    // a draft in the default mail app (Outlook, Mail…): mailto keeps it short, so long text goes on the clipboard too
    public void DraftEmail(string subject, string markdown, string to = "")
    {
        var body = System.Text.RegularExpressions.Regex.Replace(markdown, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "$1 ($2)");
        body = System.Text.RegularExpressions.Regex.Replace(body, @"(?m)^#+\s*|\*\*|__|`", "");
        var tooLong = body.Length > 1500;
        if (tooLong) Clipboard.SetText(body);
        var url = $"mailto:{Uri.EscapeDataString(to)}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(tooLong ? body[..1400] + "\n\n(The full text is on the clipboard: paste it here.)" : body)}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _ = Notice("Couldn't open the mail app", ex.Message); }
    }

    // only when you want to talk about it: a chat with the question and the answer, opened in Ivo
    void ContinueInChat(InboxItem i)
    {
        var c = NewChat(); c.Space = "chat"; c.Workspace = null;
        if (S.Providers.Any(p => p.Name == i.Provider)) { c.Provider = i.Provider; c.Model = i.Model; }
        c.Title = i.Kind == "briefing" ? i.Title : $"{(i.Category == "" ? "Daily task" : i.Category)} · {i.At:d MMM}";
        c.Messages.Add(new("user", i.Kind == "briefing" ? "My morning briefing" : i.Title, At: i.At));
        c.Messages.Add(new("assistant", i.Text, At: i.At));
        Store.SaveChat(c);
        CloseWheel_Click(this, new RoutedEventArgs());
        ShowWindow();
        OpenChat(c);
    }

    void InboxReadAll_Click(object s, RoutedEventArgs e)
    {
        // the briefing (or the newest answer) aloud; click again to stop
        var item = Inbox.All.FirstOrDefault(i => i.Kind == "briefing" && i.At.Date == DateTime.Today) ?? Inbox.All.FirstOrDefault(i => i.Kind == "daily");
        if (item != null || readingButton == s) ToggleRead((Button)s, item?.Text ?? "");
    }

    // ---------- read aloud: every speaker button is an on/off toggle; starting one stops any other ----------
    Button? readingButton;
    // The reading is known by its text, so a speaker button made later for the same text (a card reopened, a chat redrawn)
    // shows it's playing (Speaker). The title bar has pause/play and stop while anything is being read.
    string? readingText;
    (object Content, object ToolTip) readingIdle;
    int readingId;

    public async void ToggleRead(Button b, string text, string? voice = null)
    {
        if (readingButton == b || readingText == text && Voice.Busy) { Voice.Stop(); return; }   // its Speak finishes and resets everything
        Voice.Stop();
        var me = ++readingId;
        readingText = text;
        Adopt(b);
        ShowNowPlaying(text);
        try { await Voice.Speak(text, voice ?? S.Voice, S.VoiceRate, () => Dispatcher.Invoke(() => { if (me == readingId) EndReading(); })); }
        catch (Voice.NoVoiceException)
        {
            if (me == readingId) EndReading();
            if (await Confirm("Choose a voice first", "Pick one to read aloud.", "Choose a voice")) OpenSettings("voice");
        }
        catch (Exception ex) { if (me == readingId) EndReading(); _ = Notice("Couldn't read it aloud", ex.Message); }
    }

    // Every speaker button for some text (a card in the Inbox, the same card in the reader, a chat reply) is registered, so
    // all of them show the stop sign while that text is read, wherever the reading was started.
    readonly List<(WeakReference<Button> Button, string Text)> speakers = [];

    public void Speaker(Button b, string text)
    {
        speakers.RemoveAll(x => !x.Button.TryGetTarget(out _));
        speakers.Add((new(b), text));
        if (readingText == text && Voice.Busy) Mark(b, true);
    }

    static void Mark(Button b, bool reading) => (b.Content, b.ToolTip) = reading ? ("\uE71A", "Stop reading") : ("\uE767", "Read aloud");

    void MarkSpeakers(string? text)
    {
        foreach (var (w, t) in speakers) if (w.TryGetTarget(out var b)) Mark(b, t == text);
    }

    void Adopt(Button b)
    {
        if (readingButton == b) return;
        if (readingButton != null) (readingButton.Content, readingButton.ToolTip) = readingIdle;
        readingIdle = (b.Content, b.ToolTip);
        readingButton = b; b.Content = "\uE71A"; b.ToolTip = "Stop reading";
        MarkSpeakers(readingText);
    }

    void EndReading()
    {
        if (readingButton != null) (readingButton.Content, readingButton.ToolTip) = readingIdle;
        readingButton = null; readingText = null;
        MarkSpeakers(null);
        ReadingBar.Visibility = Visibility.Collapsed;
        Bars(false);
    }

    // the title-bar pill: the first words of what's being read, and its sound bars
    void ShowNowPlaying(string text)
    {
        var first = Voice.Plain(text).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l != "") ?? "";
        ReadTitle.Text = first.Length > 60 ? first[..60] : first;
        ReadingBar.ToolTip = first;
        (ReadPauseBtn.Content, ReadPauseBtn.ToolTip) = ("\uE769", "Pause");
        ReadingBar.Visibility = Visibility.Visible;
        Bars(true);
    }

    // each bar bobs at its own pace while playing; paused or stopped, they sit low and still
    void Bars(bool on)
    {
        double[] secs = [0.42, 0.55, 0.36, 0.48];
        for (int k = 0; k < ReadBars.Children.Count; k++)
        {
            var t = (ScaleTransform)((Border)ReadBars.Children[k]).RenderTransform;
            t.BeginAnimation(ScaleTransform.ScaleYProperty, on
                ? App.Loop(new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(secs[k])) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase(), BeginTime = TimeSpan.FromSeconds(k * 0.09) })
                : new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(200)));
        }
    }

    void ReadPause_Click(object s, RoutedEventArgs e)
    {
        if (Voice.Paused) Voice.Resume(); else Voice.Pause();
        (ReadPauseBtn.Content, ReadPauseBtn.ToolTip) = Voice.Paused ? ("\uE768", "Play") : ("\uE769", "Pause");
        Bars(!Voice.Paused);
    }

    void ReadStop_Click(object s, RoutedEventArgs e) => Voice.Stop();

    // Settings → Voice → Audio: try each (they play even when switched off, so you can hear what you'd get)
    void TryReadAloud_Click(object s, RoutedEventArgs e)
    {
        var name = S.Profile?.Name is { Length: > 0 } n ? n.Split(' ')[0] : "there";
        ToggleRead(TryReadAloud, SampleText(S.Voice));
    }

    void TryAppSound_Click(object s, RoutedEventArgs e) => Sounds.Chime(force: true);

    // Settings → Voice → Audio
    void ReadAloud_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        S.ReadAloud = ReadAloudBox.IsChecked == true; S.AppSounds = AppSoundsBox.IsChecked == true; Sounds.Enabled = S.AppSounds; SaveSettings();
        if (!S.ReadAloud) Voice.Stop();
        InboxReadBtn.Visibility = S.ReadAloud ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in AllPanes) p.ShowChat(p.Chat);   // replies redraw with or without their speaker button
        DrawInbox();
    }

    async void InboxClear_Click(object s, RoutedEventArgs e)
    {
        if (Inbox.All.Count == 0 || !await Confirm("Clear the inbox?", "Starred items and pending reminders stay.", "Clear", danger: true)) return;
        Inbox.All.RemoveAll(i => !i.Starred && !(i.Kind == "reminder" && !i.Done));
        Inbox.Save(); DrawInbox();
    }
}
