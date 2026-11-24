using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace Ivo;

// Ivo's Spotify panel (opened like the Inbox, floating on its own when Ivo is in the tray): a remote for your Spotify account. It plays on whatever is open (phone, speaker, the Spotify app
// on this PC; with nothing open, the app here starts minimised). Pages on the left, the player along the bottom.
public class SpotifyView : Border
{
    readonly MainWindow main;
    readonly StackPanel page = new() { Margin = new Thickness(26, 18, 26, 26) };
    readonly ScrollViewer scroll;
    readonly StackPanel queuePanel = new() { Margin = new Thickness(14, 16, 12, 16) };
    string? queuedFor;   // the song the queue was last drawn for
    readonly TextBlock status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly DispatcherTimer clock;
    JsonNode? player;   // the last /me/player
    DateTime polled;
    int ticks;
    string section = "home";

    // back: every playlist, album, artist or folder you open remembers the page you came from; the arrow in the title bar
    // walks back through them. Picking a page on the left starts afresh.
    readonly Stack<Func<Task>> history = new();
    Func<Task>? here;
    bool returning;
    readonly Button backBtn;
    void Visit(Func<Task> show)
    {
        if (!returning && here != null) history.Push(here);
        returning = false; here = show;
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;   // Now playing turns it off: it fits the panel
        backBtn.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task Back()
    {
        if (history.Count == 0) return;
        returning = true;
        await history.Pop()();
    }

    // player bar
    readonly Border art = new() { Width = 56, Height = 56, CornerRadius = new CornerRadius(8), ClipToBounds = true };
    readonly TextBlock song = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock singer = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button heart, shuffle, prev, play, next, repeat, deviceBtn;
    readonly Popup devicePop;
    readonly Slider seek = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider volume = new() { Minimum = 0, Maximum = 100, Width = 110, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock at = new() { FontSize = 12, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock length = new() { FontSize = 12, Width = 40, VerticalAlignment = VerticalAlignment.Center };
    bool seeking, liked;

    public SpotifyView(MainWindow main, Action closeMe)
    {
        this.main = main;

        // ---- header: title, status line, close ----
        var close = new Button { Style = (Style)FindResource("Ghost"), Content = "", FontSize = 13, ToolTip = "Close", Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => closeMe();
        var header = new DockPanel { Height = 48, Background = Brushes.Transparent, Margin = new Thickness(18, 0, 10, 0) };
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        DockPanel.SetDock(status, Dock.Right); header.Children.Add(status);
        backBtn = new Button { Style = (Style)FindResource("Ghost"), Content = "\uE72B", FontSize = 14, ToolTip = "Back", Width = 32, Height = 32, Margin = new Thickness(-8, 0, 6, 0), Visibility = Visibility.Collapsed };
        backBtn.Click += async (_, _) => await Back();
        MouseDown += async (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; await Back(); } };   // the mouse's back button
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children = { backBtn, Logo(), Text("Spotify", 14, "Fg", FontWeights.SemiBold, new Thickness(9, 0, 0, 0)) },
        });
        Res(status, TextBlock.ForegroundProperty, "Dim");

        // ---- pages on the left ----
        var nav = new StackPanel { Margin = new Thickness(12, 4, 8, 12) };
        foreach (var (tag, icon, name) in new[] { ("home", "\uE80F", "Home"), ("playlists", "\uE90B", "Your playlists"),
                                                  ("liked", "\uEB51", "Liked songs"), ("top", "\uE9D9", "Your top songs"), ("artists", "\uE77B", "Artists you follow"),
                                                  ("shows", "\uEC05", "Podcasts"), ("recent", "\uE81C", "Recently played") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("NavItem"), GroupName = "SpotifyNav", Tag = tag, IsChecked = tag == section,
                                       Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Icon(icon, 17, null), new TextBlock { Text = name, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } } } };
            rb.Checked += (_, _) => { section = tag; _ = Show(); };
            navButtons[tag] = rb;
            nav.Children.Add(rb);
        }

        scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };
        var body = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(200) }, new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(300) } } };
        body.Children.Add(nav);
        var content = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = scroll };
        Res(content, Border.BorderBrushProperty, "Separator");
        Grid.SetColumn(content, 1); body.Children.Add(content);
        // the queue, always in view on the right: what's playing, then what's next (updates as songs change)
        var side = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = queuePanel } };
        Res(side, Border.BorderBrushProperty, "Separator");
        Grid.SetColumn(side, 2); body.Children.Add(side);

        // ---- the player along the bottom ----
        heart = Small("", "Save to Liked songs", 14); heart.Click += async (_, _) => await ToggleLike();
        shuffle = Small("", "Shuffle", 14); shuffle.Click += (_, _) => _ = Do("PUT", $"/me/player/shuffle?state={(player?["shuffle_state"]?.GetValue<bool>() != true).ToString().ToLowerInvariant()}");
        prev = Small("", "Previous", 15); prev.Click += (_, _) => _ = Do("POST", "/me/player/previous");
        play = new Button { Width = 40, Height = 40, Content = "", FontFamily = App.IconFont, FontSize = 15, Foreground = Brushes.Black, Background = Brushes.White, Cursor = Cursors.Hand,
                            Margin = new Thickness(10, 0, 10, 0), Template = Round() };
        play.Click += (_, _) => _ = Do("PUT", Playing ? "/me/player/pause" : "/me/player/play");
        next = Small("", "Next", 15); next.Click += (_, _) => _ = Do("POST", "/me/player/next");
        repeat = Small("", "Repeat", 14); repeat.Click += (_, _) => _ = Do("PUT", "/me/player/repeat?state=" + ((string?)player?["repeat_state"] switch { "off" => "context", "context" => "track", _ => "off" }));
        deviceBtn = Small("", "Devices", 14); deviceBtn.Click += async (_, _) => await ShowDevices();
        devicePop = new Popup { PlacementTarget = deviceBtn, Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true, VerticalOffset = -8, HorizontalOffset = -140 };

        seek.PreviewMouseDown += (_, _) => seeking = true;
        seek.PreviewMouseUp += (_, _) => seeking = false;
        MainWindow.OnDrag(seek, v => Do("PUT", $"/me/player/seek?position_ms={(long)v}"));
        MainWindow.OnDrag(volume, v => Do("PUT", $"/me/player/volume?volume_percent={(int)v}", refresh: false));
        volume.Focusable = seek.Focusable = false;   // so no key can nudge them
        Res(at, TextBlock.ForegroundProperty, "Dim"); Res(length, TextBlock.ForegroundProperty, "Dim");
        Res(song, TextBlock.ForegroundProperty, "Fg"); Res(singer, TextBlock.ForegroundProperty, "Dim");

        var nowInfo = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        art.Margin = new Thickness(0, 0, 12, 0); DockPanel.SetDock(art, Dock.Left); nowInfo.Children.Add(art);
        DockPanel.SetDock(heart, Dock.Right); nowInfo.Children.Add(heart);
        nowInfo.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { song, singer } });
        Res(art, Border.BackgroundProperty, "Glass");

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { shuffle, prev, play, next, repeat } };
        var progress = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(at, Dock.Left); progress.Children.Add(at);
        DockPanel.SetDock(length, Dock.Right); progress.Children.Add(length);
        seek.Margin = new Thickness(8, 0, 8, 0); progress.Children.Add(seek);
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { controls, progress } };

        // sleep timer: pause after a while, or when this song ends
        // lyrics in a window of their own
        lyricsBtn = Small("\uED1E", "Lyrics", 14);
        lyricsBtn.Click += (_, _) => OpenLyrics();
        sleepBtn = Small("", "Sleep timer", 14);
        sleepBtn.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = sleepBtn, Placement = PlacementMode.Top };
            foreach (var (label, minutes) in new[] { ("15 minutes", 15), ("30 minutes", 30), ("45 minutes", 45), ("1 hour", 60), ("When this song ends", -1) })
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => Sleep(minutes);
                menu.Items.Add(item);
            }
            if (sleepAt != null) { var off = new MenuItem { Header = "Turn off the timer" }; off.Click += (_, _) => Sleep(0); menu.Items.Add(new Separator()); menu.Items.Add(off); }
            menu.IsOpen = true;
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                                     Children = { lyricsBtn, sleepBtn, deviceBtn, Icon("", 13, null, new Thickness(8, 0, 6, 0)), volume } };
        var bar = new Grid { Height = 96, Margin = new Thickness(18, 0, 22, 0), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
        bar.Children.Add(nowInfo);
        Grid.SetColumn(middle, 1); bar.Children.Add(middle);
        Grid.SetColumn(right, 2); bar.Children.Add(right);
        var barLine = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
        Res(barLine, Border.BorderBrushProperty, "Separator");

        var layout = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        layout.Children.Add(header);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        Grid.SetRow(barLine, 2); layout.Children.Add(barLine);
        // flat: one solid panel like the Inbox and Planner; thin dividers between its parts
        var frame = new Border { CornerRadius = new CornerRadius(16), Child = new Grid { Children = { new Border { CornerRadius = new CornerRadius(16), Background = Wash }, layout } }, ClipToBounds = true };
        Res(frame, Border.BackgroundProperty, "BoxBg");
        Child = frame;

        // a second-by-second clock for the progress bar; Spotify itself is asked every few seconds, only while this is open
        clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => { if (++ticks % 4 == 0) await Refresh(); else Tick(); }, Dispatcher);
        // only while it's on screen
        IsVisibleChanged += async (_, _) => { if (IsVisible) { clock.Start(); await Refresh(); await Show(); } else clock.Stop(); };
        Focusable = true; FocusVisualStyle = null;
        PreviewMouseDown += (_, _) => { if (Keyboard.FocusedElement is not TextBox) Focus(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { closeMe(); return; }
            if (Keyboard.FocusedElement is TextBox) return;
            switch (e.Key)
            {
                case Key.Space: e.Handled = true; play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                case Key.Left or Key.Right when (player?["item"] != null):   // 10 seconds back or on
                    e.Handled = true;
                    var to = Math.Clamp(seek.Value + (e.Key == Key.Right ? 10_000 : -10_000), 0, seek.Maximum);
                    seek.Value = to; _ = Do("PUT", $"/me/player/seek?position_ms={(long)to}", refresh: false);
                    break;
                case Key.Up or Key.Down when volume.IsEnabled:   // volume in steps of 10
                    e.Handled = true;
                    volume.Value = Math.Clamp(volume.Value + (e.Key == Key.Up ? 10 : -10), 0, 100);
                    _ = Do("PUT", $"/me/player/volume?volume_percent={(int)volume.Value}", refresh: false);
                    break;
            }
        };
    }

    bool Playing => player?["is_playing"]?.GetValue<bool>() == true;

    // ---------- sleep timer ----------
    Button sleepBtn = null!;
    Button lyricsBtn = null!;
    readonly Dictionary<string, RadioButton> navButtons = [];

    // ---------- lyrics (LRCLIB), on Now playing's wheel and in their own window ----------
    LyricsWheel? nowLyrics;
    string? lyricsFor;          // the song the lyrics were fetched for
    List<Lyrics.Line>? lyrics;

    readonly TextBlock lyricsSong = new() { FontSize = 26, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock lyricsBy = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };

    async Task LoadLyrics(JsonNode? item)
    {
        var uri = (string?)item?["uri"];
        if (uri == lyricsFor) return;
        lyricsFor = uri; lyrics = null;
        lyricsSong.Text = (string?)item?["name"] ?? "Nothing playing"; lyricsBy.Text = item == null ? "" : Artists(item);
        if (item == null) { nowLyrics?.Show(null, "Nothing playing."); return; }
        nowLyrics?.Show(null, "Looking for the lyrics…");
        var found = await Lyrics.For(item);
        if (lyricsFor != uri) return;   // the song changed meanwhile
        lyrics = found;
        nowLyrics?.Show(found);
    }

    LyricsWheel MakeLyricsWheel(double size)
    {
        var w = new LyricsWheel(size) { SeekTo = s => _ = Do("PUT", $"/me/player/seek?position_ms={(int)(s * 1000)}") };
        if (lyricsFor != null && lyricsFor == (string?)player?["item"]?["uri"]) w.Show(lyrics, "Looking for the lyrics…");
        return w;
    }

    // the lyrics page has no place in the menu: the lyrics button opens it, and a page on the left leaves it
    void OpenLyrics()
    {
        foreach (var rb in navButtons.Values) rb.IsChecked = false;
        section = "lyrics"; _ = Show();
    }

    // the Lyrics page: the song, then its lyrics on a wheel as tall as the panel (no scrolling)
    async Task LyricsPage()
    {
        await Refresh();
        if (section != "lyrics") return;
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Res(lyricsSong, TextBlock.ForegroundProperty, "Fg"); Res(lyricsBy, TextBlock.ForegroundProperty, "Dim");
        var item = player?["item"];
        lyricsSong.Text = (string?)item?["name"] ?? "Nothing playing"; lyricsBy.Text = item == null ? "" : Artists(item);
        var fit = new Grid { Height = Math.Max(360, scroll.ActualHeight - 44), RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        // the song as Apple Music heads it: the cover, then its name over the artist
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        (lyricsSong.Parent as Panel)?.Children.Remove(lyricsSong); (lyricsBy.Parent as Panel)?.Children.Remove(lyricsBy);
        words.Children.Add(lyricsSong); words.Children.Add(lyricsBy);
        var head = new DockPanel { Margin = new Thickness(20, 0, 0, 8) };
        if (Cover(item, small: false) is { } art)
        {
            var pic = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 14, 0), Background = Cover(art) };
            DockPanel.SetDock(pic, Dock.Left); head.Children.Add(pic);
        }
        head.Children.Add(words);
        fit.Children.Add(head);
        nowLyrics = MakeLyricsWheel(30);
        Grid.SetRow(nowLyrics, 1); fit.Children.Add(nowLyrics);
        page.Children.Add(fit);
        // the page takes on the cover's colour, like Now playing
        if (Cover(item, small: false) is { } url && await Image(url) is { } img && section == "lyrics") Colour(Tint(img));
        await LoadLyrics(item);
    }
    DateTime? sleepAt;
    DispatcherTimer? sleepTimer;
    void Sleep(int minutes)   // 0 = off; -1 = at the end of the song playing now
    {
        sleepTimer?.Stop(); sleepAt = null;
        if (minutes != 0)
        {
            var wait = minutes > 0 ? TimeSpan.FromMinutes(minutes)
                : TimeSpan.FromMilliseconds(Math.Max(1000, ((double?)player?["item"]?["duration_ms"] ?? 0) - ((double?)player?["progress_ms"] ?? 0) - (DateTime.Now - polled).TotalMilliseconds));
            sleepAt = DateTime.Now + wait;
            sleepTimer = new DispatcherTimer { Interval = wait };
            sleepTimer.Tick += (_, _) => { sleepTimer!.Stop(); sleepAt = null; Mark(sleepBtn, false); sleepBtn.ToolTip = "Sleep timer"; _ = Do("PUT", "/me/player/pause"); };
            sleepTimer.Start();
        }
        Mark(sleepBtn, sleepAt != null);
        sleepBtn.ToolTip = sleepAt is { } at ? $"Pausing at {When.Time(at)}" : "Sleep timer";
        Say(sleepAt is { } t ? $"Sleep timer: pausing at {When.Time(t)}" : "");
    }

    // ---------- the player bar ----------
    public async Task Refresh()
    {
        try { player = await SpotifyApi.Get("/me/player"); polled = DateTime.Now; } catch { return; }
        main.ShowMusic(player);
        // playback moved to another device: the Devices page follows
        var device = (string?)player?["device"]?["id"];
        if ((string?)player?["item"]?["id"] is var song1 && song1 != queuedFor) { queuedFor = song1; _ = DrawQueue(); }
        if (section is "home" or "lyrics") _ = LoadLyrics(player?["item"]);
        if (device != shownDevice) { var was = shownDevice; shownDevice = device; if (was != null && devicePop.IsOpen) _ = ShowDevices(); }
        var item = player?["item"];
        song.Text = (string?)item?["name"] ?? "Nothing playing";
        singer.Text = item == null ? "Pick something below" : Artists(item);
        art.Background = Cover(item, small: false) is { } url ? Cover(url) : (Brush?)FindResource("Glass");
        var glyph = Playing ? "" : "";
        if (play.Content as string != glyph)   // play / pause: the button gives a small pulse as it switches
        {
            play.Content = glyph;
            var pulse = new ScaleTransform(1, 1); play.RenderTransform = pulse; play.RenderTransformOrigin = new Point(0.5, 0.5);
            var a = new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            pulse.BeginAnimation(ScaleTransform.ScaleXProperty, a); pulse.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        Mark(shuffle, player?["shuffle_state"]?.GetValue<bool>() == true);
        var rep = (string?)player?["repeat_state"] ?? "off";
        repeat.Content = rep == "track" ? "" : ""; Mark(repeat, rep != "off");
        deviceBtn.ToolTip = player?["device"] is { } d ? $"Playing on {(string?)d["name"]}" : "Devices";
        volume.IsEnabled = player?["device"]?["volume_percent"] != null && player?["device"]?["supports_volume"]?.GetValue<bool>() != false;
        if (player?["device"]?["volume_percent"] is { } v && !volume.IsMouseCaptureWithin) volume.Value = (int)v;
        Tick();
        if (item?["id"] is { } id && (string?)id != likedId) { likedId = (string?)id; liked = (await SpotifyApi.Get($"/me/tracks/contains?ids={likedId}"))?[0]?.GetValue<bool>() == true; }
        heart.Content = liked ? "" : ""; Mark(heart, liked);
    }
    string? likedId, shownDevice;

    void Tick()
    {
        if (seeking || player?["item"] is not { } item) { if (player?["item"] == null) { at.Text = length.Text = ""; seek.Value = 0; } return; }
        var total = (double?)item["duration_ms"] ?? 1;
        var pos = (double?)player["progress_ms"] ?? 0;
        if (Playing) pos += (DateTime.Now - polled).TotalMilliseconds;
        seek.Maximum = total; seek.Value = Math.Min(pos, total);
        at.Text = Time(pos); length.Text = Time(total);
        nowLyrics?.At(pos / 1000);
    }

    async Task ToggleLike()
    {
        if (likedId == null) return;
        await Do(liked ? "DELETE" : "PUT", $"/me/tracks?ids={likedId}", control: false);
        likedId = null;   // look again
        await Refresh();
    }

    // one call; "control" wakes the Spotify app here when nothing is open anywhere
    async Task Do(string method, string path, JsonNode? body = null, bool control = true, bool refresh = true)
    {
        try
        {
            var (code, json) = await SpotifyApi.Send(method, path, body, control);
            Say(code switch
            {
                >= 200 and < 300 => "",
                403 => "Needs Spotify Premium.",
                404 => "No device: open Spotify somewhere first.",
                _ => (string?)json?["error"]?["message"] ?? $"Spotify said {code}.",
            });
        }
        catch (Exception ex) { Say(ex.Message); }
        if (!refresh) return;
        await Task.Delay(350);
        await Refresh();
    }

    void Say(string text) => status.Text = text;

    // ---------- pages ----------
    // Spotify's wash: the focused cover's colour across the whole view, strongest at the top, fading into the page
    static readonly LinearGradientBrush Wash = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 0.75),
        GradientStops = { new(Color.FromArgb(0, 0, 0, 0), 0), new(Color.FromArgb(0, 0, 0, 0), 1) } };
    public static void Colour(Color? c)
    {
        var to = c ?? Color.FromArgb(0, 0, 0, 0);
        Wash.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(c == null ? (byte)0 : (byte)0x8C, to.R, to.G, to.B), TimeSpan.FromMilliseconds(500)));
        Wash.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0, to.R, to.G, to.B), TimeSpan.FromMilliseconds(500)));
    }

    async Task Show()
    {
        page.Children.Clear(); scroll.ScrollToTop();
        scroll.Background = Brushes.Transparent;
        Colour(null);   // a page with a wheel colours it again
        if (!returning) { history.Clear(); here = null; }
        nowLyrics = null;
        Visit(Show);
        var me = section;
        try
        {
            switch (section)
            {
                case "home": await HomePage(); break;
                case "lyrics": await LyricsPage(); break;
                case "top": await TrackList("Your top songs", await SpotifyApi.Get("/me/top/tracks?limit=50&time_range=medium_term"), x => x, null); break;
                case "artists": await FollowedArtists(); break;
                case "shows": await Shows(); break;
                case "playlists": await Playlists(); break;
                case "liked": await TrackList("Liked songs", await SpotifyApi.Get("/me/tracks?limit=50"), x => x?["track"], null); break;
                case "recent": await RecentPage(); break;
            }
        }
        catch (Exception ex) { if (me == section) page.Children.Add(Text("Couldn't load this: " + ex.Message, 13, "Dim")); }
        if (queuePanel.Children.Count == 0) _ = DrawQueue();
        // the page eases in: a short fade and a small rise
        var rise = new TranslateTransform(0, 14); page.RenderTransform = rise;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    // Recently played as a timeline: grouped by day (Today, Yesterday, then the date), each with when it played
    async Task RecentPage()
    {
        page.Children.Add(Heading("Recently played"));
        var items = ((await SpotifyApi.Get("/me/player/recently-played?limit=50"))?["items"] as JsonArray ?? []).Where(x => x?["track"] != null).ToList();
        if (section != "recent") return;
        if (items.Count == 0) { page.Children.Add(Text("Songs you play show up here.", 13, "Dim")); return; }
        foreach (var day in items.GroupBy(x => DateTime.TryParse((string?)x!["played_at"], out var t) ? t.ToLocalTime().Date : DateTime.Today))
        {
            page.Children.Add(Text(When.Day(day.Key, longForm: true), 15, "Fg", FontWeights.SemiBold, new Thickness(0, 18, 0, 6)));
            foreach (var x in day)
            {
                var t = x!["track"]!;
                var row = TrackRow(t, () => Do("PUT", "/me/player/play", new JsonObject { ["uris"] = new JsonArray((JsonNode?)(string?)t["uri"]) }));
                if (DateTime.TryParse((string?)x["played_at"], out var at)) row.ToolTip = $"Played at {When.Time(at.ToLocalTime())}";
                page.Children.Add(row);
            }
        }
    }

    // Now playing: the cover wheel as a timeline. Songs you just played on the left, the one playing at the front, the queue
    // on the right. Click a past song to play it again, the front one to play / pause, a coming one to skip ahead to it.
    // what's playing, at the top of Home: the cover wheel (played · now · up next), the album and year, the lyrics under them
    async Task NowBlock()
    {
        await Refresh();
        var item = player?["item"];
        if (item == null) return;
        var recentTask = SpotifyApi.Get("/me/player/recently-played?limit=12");
        var queueTask = SpotifyApi.Get("/me/player/queue");
        await Task.WhenAll(recentTask, queueTask);
        if (section != "home") return;
        var nowUri = (string?)item["uri"];
        var past = ((await recentTask)?["items"] as JsonArray ?? []).Select(x => x?["track"]).Where(t => t?["uri"] != null && (string?)t["uri"] != nowUri)
            .GroupBy(t => (string)t!["uri"]!).Select(g => g.First()!).Take(8).Reverse().ToList();   // oldest on the far left
        var next = ((await queueTask)?["queue"] as JsonArray ?? []).Where(t => t != null).Select(t => t!).Take(10).ToList();
        string? Art(JsonNode t) => (string?)t["album"]?["images"]?[0]?["url"];
        Func<Task> PlayUri(JsonNode t) => () => Do("PUT", "/me/player/play", new JsonObject { ["uris"] = new JsonArray((JsonNode?)(string?)t["uri"]) });
        var items = new List<CoverWheel.Item>();
        items.AddRange(past.Select(t => new CoverWheel.Item(Art(t), (string?)t["name"] ?? "", "Played · " + Artists(t), () => _ = PlayUri(t)(), () => _ = Album(t["album"]!))));
        items.Add(new CoverWheel.Item(Art(item), (string?)item["name"] ?? "", Artists(item),
            () => _ = Do("PUT", Playing ? "/me/player/pause" : "/me/player/play"), () => _ = Album(item["album"]!)));
        for (int k = 0; k < next.Count; k++)
        {
            var t = next[k]; var skips = k + 1;
            items.Add(new CoverWheel.Item(Art(t), (string?)t["name"] ?? "", (k == 0 ? "Next · " : "Up next · ") + Artists(t),
                () => _ = Skip(skips), () => _ = Album(t["album"]!)));
        }
        // the same wheel as "Made by you" (same size, same look), under its heading; the album, the year and the lyrics follow
        page.Children.Add(Text("Now playing", 18, "Fg", FontWeights.Bold, new Thickness(0, 8, 0, 0)));
        page.Children.Add(new CoverWheel(items, first: past.Count) { Margin = new Thickness(0, 4, 0, 0) });
        var album = Text((string?)item["album"]?["name"] ?? "", 12.5, "Dim"); album.HorizontalAlignment = HorizontalAlignment.Center; album.TextTrimming = TextTrimming.CharacterEllipsis;
        var year = Text(((string?)item["album"]?["release_date"] ?? "") is { Length: >= 4 } d ? d[..4] : "", 12.5, "Dim");
        year.HorizontalAlignment = HorizontalAlignment.Center; year.Margin = new Thickness(0, 3, 0, 0);   // the same 3 px as between song and artist
        page.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0), Children = { album, year } });
        // only the line being sung; a click opens them full size (where a click on a line jumps the song there)
        nowLyrics = MakeLyricsWheel(19); nowLyrics.OneLine = true; nowLyrics.Height = 19 * 2.9; nowLyrics.Margin = new Thickness(0, 6, 0, 12);
        nowLyrics.SeekTo = null; nowLyrics.Cursor = System.Windows.Input.Cursors.Hand;
        nowLyrics.MouseLeftButtonUp += (_, _) => OpenLyrics();
        page.Children.Add(nowLyrics);
        _ = LoadLyrics(item);
    }

    // jump ahead to a song in the queue: skip that many times (Spotify has no "play this queued song")
    async Task Skip(int times)
    {
        for (int i = 0; i < times; i++) await Do("POST", "/me/player/next", refresh: i == times - 1);
        if (section == "home") await Show();
    }

    // Spotify's search bar, at the top of Home: type to search; Enter (or the button on the right) asks Ivo to make a playlist
    // of what you typed. While there are results they take the page's place.
    FrameworkElement SearchBar()
    {
        var box = new TextBox { FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
        box.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost") };
        Res(box, TextBox.ForegroundProperty, "Fg"); Res(box, TextBox.CaretBrushProperty, "Fg");
        var hint = Text("What do you want to play?", 14, "Dim"); hint.IsHitTestVisible = false; hint.VerticalAlignment = VerticalAlignment.Center;
        var ask = new Button { Style = (Style)FindResource("Ghost"), Content = Icon("\uE945", 17, null), ToolTip = "Ask Ivo to make a playlist of this" };
        var line = new Border { Width = 1, Margin = new Thickness(10, 12, 6, 12) }; Res(line, Border.BackgroundProperty, "Separator");
        var glass = Icon("\uE721", 18, null, new Thickness(0, 0, 12, 0)); Res(glass, TextBlock.ForegroundProperty, "Dim");
        var dock = new DockPanel();
        DockPanel.SetDock(glass, Dock.Left); DockPanel.SetDock(ask, Dock.Right); DockPanel.SetDock(line, Dock.Right);
        dock.Children.Add(glass); dock.Children.Add(ask); dock.Children.Add(line); dock.Children.Add(new Grid { Children = { hint, box } });
        var bar = new Border { CornerRadius = new CornerRadius(24), Height = 48, Padding = new Thickness(16, 0, 8, 0), Margin = new Thickness(0, 0, 0, 18), Child = dock,
                               Cursor = Cursors.IBeam };
        Res(bar, Border.BackgroundProperty, "Glass");
        bar.MouseLeftButtonDown += (_, _) => box.Focus();
        var results = new StackPanel();
        var wrap = new StackPanel { Children = { bar, results } };

        void Ask()
        {
            if (box.Text.Trim() is not { Length: > 0 } m) return;
            box.Clear();
            main.AskIvo($"Using Spotify, make me a playlist of about 15 songs for: {m}. Pick well-known songs that really fit, name the playlist after it, add the songs, and start playing it.");
        }
        ask.Click += (_, _) => Ask();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Ask(); } };

        // results as you type: a moment after the last key, so each word isn't a request; the rest of Home steps aside meanwhile
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        string? shown = null;
        box.TextChanged += (_, _) =>
        {
            var empty = box.Text.Length == 0;
            hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            foreach (UIElement c in page.Children) if (c != wrap) c.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            wait.Stop(); wait.Start();
        };
        wait.Tick += async (_, _) =>
        {
            wait.Stop();
            var q = box.Text.Trim();
            if (q == shown) return;
            shown = q; results.Children.Clear();
            if (q == "") return;
            var r = await SpotifyApi.Get($"/search?q={Uri.EscapeDataString(q)}&type=track,artist,album,playlist&limit=8");
            if (q != shown) return;   // typed on meanwhile
            if (r?["tracks"]?["items"] is JsonArray tracks && tracks.Count > 0)
            {
                results.Children.Add(Heading("Songs", 8));
                var uris = tracks.Select(t => (string?)t?["uri"]).OfType<string>().ToList();
                for (int i = 0; i < tracks.Count; i++) { var n = i; results.Children.Add(TrackRow(tracks[i]!, () => PlayUris(uris, n))); }
            }
            if (r?["artists"]?["items"] is JsonArray artists && artists.Count > 0) results.Children.Add(Shelf("Artists", artists.Where(a => a != null).Select(a => ArtistTile(a!))));
            if (r?["albums"]?["items"] is JsonArray albums && albums.Count > 0) results.Children.Add(Shelf("Albums", albums.Where(a => a != null).Select(a => AlbumTile(a!))));
            if (r?["playlists"]?["items"] is JsonArray lists && lists.Count > 0) results.Children.Add(Shelf("Playlists", lists.Where(p => p != null).Select(p => PlaylistTile(p!))));
            if (results.Children.Count == 0) results.Children.Add(Text("Nothing found. Press Enter and Ivo will make a playlist of it.", 13, "Dim"));
        };
        return wrap;
    }

    async Task Playlists()
    {
        page.Children.Add(Heading("Your playlists"));
        var add = PlayButton("New playlist", async () =>
        {
            if (await main.AskText("New playlist", "Name", "", "Create") is not { Length: > 0 } name) return;
            var (code, made) = await SpotifyApi.Send("POST", "/me/playlists", new JsonObject { ["name"] = name, ["public"] = false });
            if (code is >= 200 and < 300 && made != null) { mine = null; await Playlist(made); } else Say((string?)made?["error"]?["message"] ?? $"Spotify said {code}.");
        }, "\uE710");
        var folder = Ghost("New folder", "\uE8F4", async () =>
        {
            if (await main.AskText("New folder", "Ivo only; Spotify won't see it.", "", "Create") is not { Length: > 0 } name) return;
            if (!Folders.ContainsKey(name)) { Folders[name] = []; main.SaveSettings(); }
            await Show();
        });
        page.Children.Add(Actions(add, folder));
        var all = await AllPlaylists();
        if (section != "playlists") return;
        if (all.Count == 0 && Folders.Count == 0) { page.Children.Add(Text("No playlists yet.", 13, "Dim")); return; }
        // the ones you made, on the wheel
        var myId = (string?)(await Me())?["id"];
        var made = all.Where(p => (string?)p["owner"]?["id"] == myId).ToList();
        if (section != "playlists") return;
        if (made.Count > 0)
        {
            page.Children.Add(Text("Made by you", 18, "Fg", FontWeights.Bold, new Thickness(0, 8, 0, 0)));
            page.Children.Add(new CoverWheel(made.Select(p => new CoverWheel.Item(
                (string?)p["images"]?[0]?["url"], (string?)p["name"] ?? "", $"{Songs(p)} songs",
                () => _ = Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = (string?)p["uri"] }),
                () => _ = Playlist(p)))) { Margin = new Thickness(0, 4, 0, 18) });
        }
        var filed = Folders.Values.SelectMany(x => x).ToHashSet();
        var tiles = new List<FrameworkElement>();
        foreach (var (name, ids) in Folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)) tiles.Add(FolderTile(name, all.Where(p => ids.Contains((string?)p["id"] ?? "")).ToList()));
        tiles.AddRange(all.Where(p => !filed.Contains((string?)p["id"] ?? "")).Select(p => PlaylistTile(p)));
        page.Children.Add(Tiles(tiles));
    }

    // ---------- Ivo's own folders (Spotify's API has none) ----------
    Dictionary<string, List<string>> Folders => main.S.SpotifyFolders;

    // every playlist in your library, page by page (Spotify gives 50 at a time)
    async Task<List<JsonNode>> AllPlaylists()
    {
        var all = new List<JsonNode>();
        for (int offset = 0; offset < 1000; offset += 50)
        {
            var r = await SpotifyApi.Get($"/me/playlists?limit=50&offset={offset}");
            var items = (r?["items"] as JsonArray ?? []).Where(p => p != null).Select(p => p!).ToList();
            all.AddRange(items);
            if (items.Count < 50) break;
        }
        return all;
    }

    void MoveTo(string id, string? folder)
    {
        foreach (var f in Folders.Values) f.Remove(id);
        if (folder != null) { if (!Folders.TryGetValue(folder, out var ids)) Folders[folder] = ids = []; ids.Add(id); }
        main.SaveSettings();
    }

    // a folder tile: up to four of its covers in a square; drop a playlist on it to file it there
    FrameworkElement FolderTile(string name, List<JsonNode> lists)
    {
        const double size = 148;
        var grid = new UniformGrid { Rows = 2, Columns = 2, Width = size, Height = size };
        foreach (var p in lists.Take(4)) grid.Children.Add(new Border { Background = Cover(p, false) is { } u ? Cover(u) : null });
        var pic = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = lists.Count > 0 ? grid : Icon("\uE8B7", 46, null) };
        if (lists.Count == 0) ((TextBlock)pic.Child).HorizontalAlignment = HorizontalAlignment.Center;
        Res(pic, Border.BackgroundProperty, "Glass");
        var title = Text(name, 13, "Fg", FontWeights.SemiBold, new Thickness(0, 8, 0, 2)); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var line = Text($"Folder · {lists.Count} playlist{(lists.Count == 1 ? "" : "s")}", 12, "Dim");
        var card = new Border { Width = size + 20, Padding = new Thickness(10), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                                Margin = new Thickness(0, 0, 6, 8), Child = new StackPanel { Children = { pic, title, line } }, AllowDrop = true };
        card.MouseEnter += (_, _) => Res(card, Border.BackgroundProperty, "Glass");
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += async (_, _) => await FolderPage(name);
        card.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("ivo-playlist") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        card.DragEnter += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        card.DragLeave += (_, _) => card.BorderBrush = null;
        card.BorderThickness = new Thickness(1.5);
        card.Drop += async (_, e) => { if (e.Data.GetData("ivo-playlist") is string id) { MoveTo(id, name); await Show(); } };
        return card;
    }

    async Task FolderPage(string name)
    {
        Visit(() => FolderPage(name));
        var all = await AllPlaylists();
        var ids = Folders.GetValueOrDefault(name) ?? [];
        var lists = all.Where(p => ids.Contains((string?)p["id"] ?? "")).ToList();
        page.Children.Clear(); scroll.ScrollToTop();
        page.Children.Add(Header(null, "Folder in Ivo", name, $"{lists.Count} playlist{(lists.Count == 1 ? "" : "s")} · Ivo only", round: false));
        var rename = Ghost("Rename", "\uE70F", async () =>
        {
            if (await main.AskText("Rename folder", "New name", name, "Rename") is not { Length: > 0 } now || now == name || Folders.ContainsKey(now)) return;
            Folders[now] = Folders[name]; Folders.Remove(name); main.SaveSettings();
            await FolderPage(now);
        });
        var remove = Ghost("Delete folder", "\uE74D", async () =>
        {
            if (!await main.Confirm("Delete this folder?", "Its playlists stay.", "Delete folder", danger: true)) return;
            Folders.Remove(name); main.SaveSettings();
            section = "playlists"; await Show();
        });
        var back = Ghost("Your playlists", "\uE72B", async () => { section = "playlists"; await Show(); });
        page.Children.Add(Actions(back, rename, remove));
        if (lists.Count == 0) { page.Children.Add(Text("Empty. Drag playlists here.", 13, "Dim")); return; }
        page.Children.Add(Tiles(lists.Select(p => PlaylistTile(p))));
    }

    async Task Playlist(JsonNode p)
    {
        Visit(() => Playlist(p));
        var id = (string?)p["id"]; var uri = (string?)p["uri"];
        var full = await SpotifyApi.Get($"/playlists/{id}?fields=id,name,uri,images,owner(id,display_name),tracks.total,items.total,description") ?? p;
        var own = (string?)full["owner"]?["id"] == (await Me())?["id"]?.ToString();
        var buttons = new List<FrameworkElement>();
        if (own)
        {
            buttons.Add(Ghost("Rename", "\uE70F", async () =>
            {
                if (await main.AskText("Rename playlist", "New name", (string?)full["name"] ?? "", "Rename") is not { Length: > 0 } name) return;
                var (code, _) = await SpotifyApi.Send("PUT", $"/playlists/{id}", new JsonObject { ["name"] = name });
                if (code is >= 200 and < 300) { full["name"] = name; mine = null; await Playlist(full); }
            }));
            buttons.Add(Ghost("Delete", "\uE74D", async () =>
            {
                if (!await main.Confirm("Delete this playlist?", $"\"{(string?)full["name"]}\" will be removed from your library.", "Delete", danger: true)) return;
                var (code, _) = await SpotifyApi.Send("DELETE", $"/playlists/{id}/followers");   // Spotify's way of deleting your own playlist
                if (code is >= 200 and < 300) { mine = null; section = "playlists"; await Show(); }
            }));
        }
        var header = Header(Cover(full, false), "Playlist", (string?)full["name"] ?? "Playlist",
                            $"{(string?)full["owner"]?["display_name"]} · {Songs(full)} songs", round: false);
        // a playlist's songs: Spotify's current address is /items (each song under "item"); older answers use /tracks ("track")
        var (sc, songs) = await SpotifyApi.Send("GET", $"/playlists/{id}/items?limit=100");
        if (sc is not (>= 200 and < 300)) (sc, songs) = await SpotifyApi.Send("GET", $"/playlists/{id}/tracks?limit=100");
        await TrackList(header, songs, x => x?["item"] ?? x?["track"], uri, buttons, removeFrom: own ? id : null);
    }

    async Task Album(JsonNode a)
    {
        Visit(() => Album(a));
        var id = (string?)a["id"];
        var full = await SpotifyApi.Get($"/albums/{id}") ?? a;
        var saved = (await SpotifyApi.Get($"/me/albums/contains?ids={id}"))?[0]?.GetValue<bool>() == true;
        Button? save = null;
        save = Ghost(saved ? "Saved" : "Save", saved ? "\uEB52" : "\uEB51", async () =>
        {
            var (code, _) = await SpotifyApi.Send(saved ? "DELETE" : "PUT", $"/me/albums?ids={id}");
            if (code is not (>= 200 and < 300)) { Say("Spotify didn't take that."); return; }
            saved = !saved; Label(save!, saved ? "Saved" : "Save", saved ? "\uEB52" : "\uEB51"); Mark(save!, saved);
        });
        Mark(save, saved);
        var year = ((string?)full["release_date"] ?? "") is { Length: >= 4 } d ? d[..4] : "";
        var header = Header(Cover(full, false), ((string?)full["album_type"] ?? "album") switch { "single" => "Single", "compilation" => "Compilation", _ => "Album" },
                            (string?)full["name"] ?? "Album", $"{Artists(full)} · {year} · {(int?)full["total_tracks"] ?? 0} songs", round: false);
        await TrackList(header, full["tracks"], x => x == null ? null : Album(x, full), (string?)full["uri"], [save]);
    }
    static JsonNode Album(JsonNode track, JsonNode album) { var t = track.DeepClone(); t["album"] = new JsonObject { ["images"] = album["images"]?.DeepClone(), ["name"] = (string?)album["name"] }; return t; }

    async Task Artist(JsonNode a)
    {
        Visit(() => Artist(a));
        var id = (string?)a["id"];
        var full = await SpotifyApi.Get($"/artists/{id}") ?? a;
        var top = await SpotifyApi.Get($"/artists/{id}/top-tracks");
        var albums = await SpotifyApi.Get($"/artists/{id}/albums?include_groups=album,single&limit=20");
        var (fc, fj) = await SpotifyApi.Send("GET", $"/me/following/contains?type=artist&ids={id}");
        var following = fc is >= 200 and < 300 && fj?[0]?.GetValue<bool>() == true;
        page.Children.Clear(); scroll.ScrollToTop();
        var followers = (int?)full["followers"]?["total"] is { } n ? $"{n:N0} followers" : "Artist";
        page.Children.Add(Header(Cover(full, false), "Artist", (string?)full["name"] ?? "Artist", followers, round: true));
        Button? follow = null;
        follow = Ghost(following ? "Following" : "Follow", following ? "\uE73E" : "\uE710", async () =>
        {
            var (code, _) = await SpotifyApi.Send(following ? "DELETE" : "PUT", $"/me/following?type=artist&ids={id}");
            if (code == 403) { Say("Sign in to Spotify again to follow artists."); return; }
            if (code is not (>= 200 and < 300)) { Say($"Spotify said {code}."); return; }
            following = !following; Label(follow!, following ? "Following" : "Follow", following ? "\uE73E" : "\uE710"); Mark(follow!, following);
        });
        Mark(follow, following);
        page.Children.Add(Actions(PlayButton("Play", () => Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = (string?)full["uri"] })), follow));
        if (top?["tracks"] is JsonArray tracks && tracks.Count > 0)
        {
            page.Children.Add(Heading("Popular", 18));
            var uris = tracks.Select(t => (string?)t?["uri"]).OfType<string>().ToList();
            for (int k = 0; k < Math.Min(tracks.Count, 10); k++) { var m = k; page.Children.Add(TrackRow(tracks[k]!, () => PlayUris(uris, m))); }
        }
        if (albums?["items"] is JsonArray list && list.Count > 0)
            page.Children.Add(Shelf("Albums and singles", list.Where(x => x != null).Select(x => AlbumTile(x!))));
    }

    // ---------- Home and your albums ----------
    async Task HomePage()
    {
        page.Children.Add(SearchBar());
        var recent = SpotifyApi.Get("/me/player/recently-played?limit=50");
        var topTracks = SpotifyApi.Get("/me/top/tracks?limit=12&time_range=short_term");
        var topArtists = SpotifyApi.Get("/me/top/artists?limit=12&time_range=short_term");
        var followed = SpotifyApi.Get("/me/following?type=artist&limit=12");
        await NowBlock();
        await Task.WhenAll(recent, topTracks, topArtists, followed);
        if (section != "home") return;

        // recently played: each song once, newest first
        var played = ((await recent)?["items"] as JsonArray ?? []).Select(x => x?["track"]).Where(t => t?["uri"] != null)
            .GroupBy(t => (string)t!["uri"]!).Select(g => g.First()!).Take(12).ToList();
        if (played.Count > 0) page.Children.Add(Shelf("Recently played", played.Select(t => TrackTile(t, played))));
        if ((await topTracks)?["items"] is JsonArray tt && tt.Count > 0)
        {
            var list = tt.Where(t => t != null).Select(t => t!).ToList();
            page.Children.Add(Shelf("Your top songs this month", list.Select(t => TrackTile(t, list))));
        }
        if ((await topArtists)?["items"] is JsonArray ta && ta.Count > 0) page.Children.Add(Shelf("Your top artists this month", ta.Where(a => a != null).Select(a => ArtistTile(a!))));
        if ((await followed)?["artists"]?["items"] is JsonArray fa && fa.Count > 0) page.Children.Add(Shelf("Artists you follow", fa.Where(a => a != null).Select(a => ArtistTile(a!))));
        if (page.Children.Count == 2) page.Children.Add(Text("Nothing here yet.", 13, "Dim"));
    }

    async Task FollowedArtists()
    {
        page.Children.Add(Heading("Artists you follow"));
        var r = await SpotifyApi.Get("/me/following?type=artist&limit=50");
        var list = (r?["artists"]?["items"] as JsonArray ?? []).Where(a => a != null).ToList();
        if (list.Count == 0) { page.Children.Add(Text("Artists you follow show up here.", 13, "Dim")); return; }
        page.Children.Add(Tiles(list.Select(a => ArtistTile(a!))));
    }

    async Task Shows()
    {
        page.Children.Add(Heading("Podcasts"));
        var r = await SpotifyApi.Get("/me/shows?limit=50");
        var list = (r?["items"] as JsonArray ?? []).Select(x => x?["show"]).Where(x => x != null).ToList();
        if (list.Count == 0) { page.Children.Add(Text("Podcasts you follow show up here.", 13, "Dim")); return; }
        page.Children.Add(Tiles(list.Select(p => Tile(Cover(p, false), (string?)p!["name"] ?? "", (string?)p["publisher"] ?? "",
            () => Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = (string?)p["uri"] }),
            () => Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = (string?)p["uri"] }), round: false))));
    }

    // a list of songs; with a context (playlist, album) a song plays on from there, otherwise from this list
    Task TrackList(string title, JsonNode? list, Func<JsonNode?, JsonNode?> track, string? context) => TrackList(Heading(title), list, track, context);

    async Task TrackList(FrameworkElement header, JsonNode? list, Func<JsonNode?, JsonNode?> track, string? context, List<FrameworkElement>? extra = null, string? removeFrom = null)
    {
        page.Children.Clear(); scroll.ScrollToTop();
        page.Children.Add(header);
        var tracks = (list?["items"] as JsonArray ?? []).Select(track).Where(t => t?["uri"] != null && !((string?)t!["uri"])!.StartsWith("spotify:local")).ToList();
        var uris = tracks.Select(t => (string)t!["uri"]!).ToList();
        var playAll = PlayButton("Play", () => context != null ? Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = context }) : PlayUris(uris, 0));
        page.Children.Add(Actions([playAll, .. extra ?? []]));
        if (tracks.Count == 0) { page.Children.Add(Text("Nothing here yet.", 13, "Dim")); return; }
        for (int i = 0; i < tracks.Count; i++)
        {
            var n = i; var t = tracks[i]!;
            var row = TrackRow(t, () => context != null
                ? Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = context, ["offset"] = new JsonObject { ["uri"] = (string?)t["uri"] } })
                : PlayUris(uris, n));
            if (removeFrom != null && row.ContextMenu is { } menu)
            {
                var remove = new MenuItem { Header = "Remove from this playlist" };
                remove.Click += async (_, _) =>
                {
                    var (code, _) = await SpotifyApi.Send("DELETE", $"/playlists/{removeFrom}/tracks",
                        new JsonObject { ["tracks"] = new JsonArray(new JsonObject { ["uri"] = (string?)t["uri"] }) });
                    if (code is >= 200 and < 300) page.Children.Remove(row); else Say("Spotify didn't remove it.");
                };
                menu.Items.Add(new Separator()); menu.Items.Add(remove);
            }
            page.Children.Add(row);
        }
        await Task.CompletedTask;
    }

    // A list of songs with no album or playlist behind it (your top songs, search results): Spotify's app accepts a bare list
    // of songs and then plays nothing, but always plays from an album. So the clicked song starts from its own album, and
    // the songs after it in the list go into the queue, in the background.
    async Task PlayUris(List<string> uris, int index)
    {
        var list = uris.Skip(index).Take(21).ToList();
        if (list.Count == 0) return;
        var track = await SpotifyApi.Get($"/tracks/{list[0].Split(':').Last()}?market=from_token");
        if (track?["is_playable"]?.GetValue<bool>() == false) { Say("That song isn't available in your country."); return; }
        var song = (string?)track?["uri"] ?? list[0];
        await Do("PUT", "/me/player/play", (string?)track?["album"]?["uri"] is { } album
            ? new JsonObject { ["context_uri"] = album, ["offset"] = new JsonObject { ["uri"] = song } }
            : new JsonObject { ["uris"] = new JsonArray((JsonNode)JsonValue.Create(song)!) });
        _ = Task.Run(async () =>
        {
            foreach (var next in list.Skip(1)) await SpotifyApi.Send("POST", $"/me/player/queue?uri={Uri.EscapeDataString(next)}");
            await Dispatcher.InvokeAsync(() => DrawQueue());
        });
    }

    async Task DrawQueue()
    {
        JsonNode? q;
        try { q = await SpotifyApi.Get("/me/player/queue"); } catch { return; }
        queuePanel.Children.Clear();
        queuePanel.Children.Add(Text("Queue", 16, "Fg", FontWeights.Bold, new Thickness(0, 0, 0, 10)));
        if (q?["currently_playing"] is { } cur)
        {
            queuePanel.Children.Add(Text("NOW PLAYING", 11, "Dim", FontWeights.SemiBold, new Thickness(0, 0, 0, 4)));
            queuePanel.Children.Add(TrackRow(cur, () => Task.CompletedTask));
        }
        if (q?["queue"] is not JsonArray next || next.Count == 0) { queuePanel.Children.Add(Text("Nothing queued.", 12, "Dim", null, new Thickness(0, 10, 0, 0))); return; }
        queuePanel.Children.Add(Text("NEXT UP", 11, "Dim", FontWeights.SemiBold, new Thickness(0, 14, 0, 4)));
        var ahead = 0;
        foreach (var t in next.Where(t => t != null).Take(30))
        {
            var skip = ++ahead;   // a click skips ahead to it
            queuePanel.Children.Add(TrackRow(t!, async () => { for (int i = 0; i < skip; i++) await SpotifyApi.Send("POST", "/me/player/next"); await Refresh(); }));
        }
    }

    // ---------- devices: a picker above the speaker button, like Spotify's own ----------
    async Task ShowDevices()
    {
        var list = new StackPanel { Margin = new Thickness(8) };
        list.Children.Add(Text("Play on", 14, "Fg", FontWeights.SemiBold, new Thickness(8, 4, 8, 8)));
        var d = await SpotifyApi.Get("/me/player/devices");
        var devices = d?["devices"] as JsonArray ?? [];
        // which one is playing: the playback itself says (the list's own "active" flag can lag behind or name the wrong one)
        var now = (string?)(await SpotifyApi.Get("/me/player"))?["device"]?["id"];
        foreach (var dev in devices.Where(x => x != null))
        {
            var active = now != null ? (string?)dev!["id"] == now : dev!["is_active"]?.GetValue<bool>() == true;
            var icon = (string?)dev["type"] switch { "Computer" => "", "Smartphone" => "", "Speaker" => "", "TV" => "", _ => "" };
            var row = Row(null, (string?)dev["name"] ?? "", (active ? "Playing here · " : "") + (string?)dev["type"],
                          async () => { devicePop.IsOpen = false; await Do("PUT", "/me/player", new JsonObject { ["device_ids"] = new JsonArray((JsonNode)JsonValue.Create((string?)dev["id"])!), ["play"] = true }); }, icon);
            if (active) ((Border)row).BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            list.Children.Add(row);
        }
        if (devices.Count == 0) list.Children.Add(Text("No Spotify app open.", 12, "Dim", null, new Thickness(8, 0, 8, 6)));
        if (!devices.Any(x => (string?)x?["type"] == "Computer"))
        {
            var wake = new Button { Content = "Start Spotify on this PC", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 8, 8, 4) };
            wake.Click += async (_, _) =>
            {
                wake.IsEnabled = false; Say("Starting Spotify…");
                var id = await SpotifyApi.WakeApp();
                Say(id == null ? "Spotify isn't installed." : "");
                if (id != null) await ShowDevices();
            };
            list.Children.Add(wake);
        }
        var card = new Border { Width = 300, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = list };
        Res(card, Border.BackgroundProperty, "PopupBg"); Res(card, Border.BorderBrushProperty, "EdgeStroke");
        devicePop.Child = card;
        devicePop.IsOpen = true;
    }

    // ---------- pieces ----------
    public static string Artists(JsonNode item) => string.Join(", ", (item["artists"] as JsonArray ?? []).Select(a => (string?)a?["name"]).Where(n => n != null).Take(2));   // the first two: enough to know the song

    // how many songs a playlist has ("items" in Spotify's current API, "tracks" in older answers)
    static int Songs(JsonNode p) => (int?)(p["items"]?["total"] ?? p["tracks"]?["total"]) ?? 0;

    // Spotify's mark: the green circle and its three sound waves
    static FrameworkElement Logo()
    {
        var waves = Geometry.Parse("M6.2,9.4 C10.2,8.1 14.6,8.4 17.9,10.3 M7,12.6 C10.3,11.6 13.9,11.9 16.6,13.5 M7.7,15.6 C10.3,14.9 13.1,15.1 15.3,16.4");
        return new Viewbox { Width = 20, Height = 20, Child = new Canvas { Width = 24, Height = 24, Children = {
            new System.Windows.Shapes.Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60)) },
            new System.Windows.Shapes.Path { Data = waves, Stroke = Brushes.Black, StrokeThickness = 1.9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } } } };
    }

    public static string? Cover(JsonNode? item, bool small)
    {
        var images = (item?["album"]?["images"] ?? item?["images"]) as JsonArray;
        if (images == null || images.Count == 0) return null;
        return (string?)(small ? images[^1] : images[0])?["url"];
    }

    // Covers: downloaded first, then shown (a picture given only its web address could arrive after it was drawn and never
    // appear). Kept for the session; the brush fills in when the download lands.
    static readonly Dictionary<string, Task<BitmapSource?>> images = [];
    static readonly System.Net.Http.HttpClient web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(20) };

    public static Task<BitmapSource?> Image(string url)
    {
        if (images.TryGetValue(url, out var known)) return known;
        if (images.Count > 300) images.Clear();
        return images[url] = Load(url);
        static async Task<BitmapSource?> Load(string url)
        {
            try
            {
                var bytes = await web.GetByteArrayAsync(url);
                var img = new BitmapImage();
                img.BeginInit(); img.StreamSource = new System.IO.MemoryStream(bytes); img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }
    }

    // covers fade in as they arrive instead of popping in
    public static async void Fill(ImageBrush brush, string url)
    {
        if (await Image(url) is not { } img) return;
        brush.ImageSource = img;
        if (!brush.IsFrozen) brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
    }

    public static ImageBrush Cover(string url)
    {
        var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
        Fill(brush, url);
        return brush;
    }

    // the average colour of a cover, from a tiny copy of it
    internal static Color Tint(BitmapSource img)
    {
        var small = new TransformedBitmap(new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0), new ScaleTransform(8.0 / img.PixelWidth, 8.0 / img.PixelHeight));
        var px = new byte[small.PixelWidth * small.PixelHeight * 4];
        small.CopyPixels(px, small.PixelWidth * 4, 0);
        long b = 0, g = 0, rr = 0; var n = px.Length / 4;
        for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; rr += px[i + 2]; }
        return Color.FromRgb((byte)(rr / n), (byte)(g / n), (byte)(b / n));
    }

    // your own playlists (and ones you can edit), for "Add to playlist"; fetched once while the panel is open
    List<JsonNode>? mine;
    JsonNode? me;
    async Task<JsonNode?> Me() => me ??= await SpotifyApi.Get("/me");
    async Task<List<JsonNode>> MyPlaylists()
    {
        if (mine != null) return mine;
        var myId = (string?)(await Me())?["id"];
        var r = await SpotifyApi.Get("/me/playlists?limit=50");
        return mine = (r?["items"] as JsonArray ?? []).Where(p => p != null && ((string?)p["owner"]?["id"] == myId || p["collaborative"]?.GetValue<bool>() == true)).Select(p => p!).ToList();
    }

    // a page header: big cover (round for artists), kind, title, a line under it
    FrameworkElement Header(string? image, string kind, string title, string sub, bool round)
    {
        var pic = new Border { Width = 168, Height = 168, CornerRadius = new CornerRadius(round ? 84 : 10), ClipToBounds = true, Margin = new Thickness(0, 0, 22, 0) };
        if (image != null) pic.Background = Cover(image); else Res(pic, Border.BackgroundProperty, "Glass");
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        info.Children.Add(Text(kind.ToUpperInvariant(), 11, "Dim", FontWeights.SemiBold));
        var name = Text(title, title.Length > 28 ? 26 : 36, "Fg", FontWeights.Bold, new Thickness(0, 4, 0, 6)); name.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(name);
        info.Children.Add(Text(sub, 13, "Dim"));
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(pic, Dock.Left);
        dock.Children.Add(pic); dock.Children.Add(info);
        return dock;
    }

    static FrameworkElement Actions(params FrameworkElement[] items)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var x in items) { x.Margin = new Thickness(0, 0, 10, 10); x.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(x); }
        return row;
    }

    // a titled row of tiles that wraps onto more lines as needed
    FrameworkElement Shelf(string title, IEnumerable<FrameworkElement> tiles) =>
        new StackPanel { Margin = new Thickness(0, 8, 0, 6), Children = { Heading(title, 8), Tiles(tiles) } };

    static WrapPanel Tiles(IEnumerable<FrameworkElement> tiles)
    {
        var wrap = new WrapPanel();
        foreach (var t in tiles) wrap.Children.Add(t);
        return wrap;
    }

    FrameworkElement TrackTile(JsonNode t, List<JsonNode> list)
    {
        var uris = list.Select(x => (string?)x["uri"]).OfType<string>().ToList();
        var at = list.IndexOf(t);
        return Tile(Cover(t, false), (string?)t["name"] ?? "", Artists(t), () => PlayUris(uris, Math.Max(at, 0)), () => PlayUris(uris, Math.Max(at, 0)), round: false);
    }
    FrameworkElement AlbumTile(JsonNode a) =>
        Tile(Cover(a, false), (string?)a["name"] ?? "", $"{(((string?)a["release_date"] ?? "") is { Length: >= 4 } d ? d[..4] + " · " : "")}{Artists(a)}",
             () => Album(a), () => Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = (string?)a["uri"] }), round: false);
    FrameworkElement ArtistTile(JsonNode a) =>
        Tile(Cover(a, false), (string?)a["name"] ?? "", "Artist", () => Artist(a), () => Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = (string?)a["uri"] }), round: true);
    FrameworkElement PlaylistTile(JsonNode p)
    {
        var tile = Tile(Cover(p, false), (string?)p["name"] ?? "", $"{Songs(p)} songs · {(string?)p["owner"]?["display_name"]}",
                        () => Playlist(p), () => Do("PUT", "/me/player/play", new JsonObject { ["context_uri"] = (string?)p["uri"] }), round: false);
        var id = (string?)p["id"] ?? "";
        // drag it onto a folder
        Point? from = null;
        tile.PreviewMouseLeftButtonDown += (_, e) => from = e.GetPosition(tile);
        tile.PreviewMouseMove += (_, e) =>
        {
            if (from is not { } f || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(tile) - f).Length < 8) return;
            from = null;
            DragDrop.DoDragDrop(tile, new DataObject("ivo-playlist", id), DragDropEffects.Move);
        };
        // or right-click: Move to folder
        var move = new MenuItem { Header = "Move to folder" };
        tile.ContextMenuOpening += (_, _) =>
        {
            move.Items.Clear();
            var inFolder = Folders.FirstOrDefault(f => f.Value.Contains(id)).Key;
            foreach (var name in Folders.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var item = new MenuItem { Header = name, IsChecked = name == inFolder };
                item.Click += async (_, _) => { MoveTo(id, name); await Show(); };
                move.Items.Add(item);
            }
            if (Folders.Count > 0) move.Items.Add(new Separator());
            var made = new MenuItem { Header = "New folder…" };
            made.Click += async (_, _) =>
            {
                if (await main.AskText("New folder", "Ivo only; Spotify won't see it.", "", "Create") is not { Length: > 0 } name) return;
                MoveTo(id, name); await Show();
            };
            move.Items.Add(made);
            if (inFolder != null)
            {
                var take = new MenuItem { Header = $"Take out of \"{inFolder}\"" };
                take.Click += async (_, _) => { MoveTo(id, null); await Show(); };
                move.Items.Add(take);
            }
        };
        tile.ContextMenu = new ContextMenu { Items = { move } };
        return tile;
    }

    // a cover card: click it to open; on hover a green play button appears over the cover, which plays it straight away
    FrameworkElement Tile(string? image, string title, string sub, Func<Task> open, Func<Task> play, bool round)
    {
        const double size = 148;
        var pic = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(round ? size / 2 : 8), ClipToBounds = true };
        if (image != null) pic.Background = Cover(image); else Res(pic, Border.BackgroundProperty, "Glass");
        var go = new Button { Width = 40, Height = 40, Content = "\uE768", FontFamily = App.IconFont, FontSize = 14, Foreground = Brushes.Black, Cursor = Cursors.Hand,
                              Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), Template = Round(), HorizontalAlignment = HorizontalAlignment.Right,
                              VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 8), Opacity = 0, ToolTip = "Play" };
        go.Click += async (_, e) => { e.Handled = true; await play(); };
        var cover = new Grid { Children = { pic, go } };
        var name = Text(title, 13, "Fg", FontWeights.SemiBold, new Thickness(0, 8, 0, 2)); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var line = Text(sub, 12, "Dim"); line.TextWrapping = TextWrapping.NoWrap; line.TextTrimming = TextTrimming.CharacterEllipsis;
        var card = new Border { Width = size + 20, Padding = new Thickness(10), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                                Margin = new Thickness(0, 0, 6, 8), Child = new StackPanel { Children = { cover, name, line } } };
        // hover: the card lifts a little and its play button fades in
        var lift = new ScaleTransform(1, 1); card.RenderTransform = lift; card.RenderTransformOrigin = new Point(0.5, 0.5);
        void Lift(double to) { var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }; lift.BeginAnimation(ScaleTransform.ScaleXProperty, a); lift.BeginAnimation(ScaleTransform.ScaleYProperty, a); }
        card.MouseEnter += (_, _) => { Res(card, Border.BackgroundProperty, "BoxCard"); go.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140))); Lift(1.035); };
        card.MouseLeave += (_, _) => { card.Background = Brushes.Transparent; go.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))); Lift(1); };
        card.MouseLeftButtonUp += async (_, e) => { if (e.OriginalSource is DependencyObject d && IsIn(d, go)) return; await open(); };
        return card;
    }

    static bool IsIn(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x)) if (x == parent) return true;   // Runs aren't visuals
        return false;
    }

    // a quiet button with an icon (Follow, Save, Rename…)
    Button Ghost(string text, string glyph, Func<Task> click)
    {
        var b = new Button { Padding = new Thickness(14, 7, 16, 7), Cursor = Cursors.Hand, Template = Pill(), BorderThickness = new Thickness(1) };
        Res(b, Control.BackgroundProperty, "Glass"); Res(b, Control.ForegroundProperty, "Fg");
        Label(b, text, glyph);
        b.Click += async (_, _) => await click();
        return b;
    }

    static void Label(Button b, string text, string glyph) =>
        b.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, new TextBlock { Text = text } } };

    static string Time(double ms) { var t = TimeSpan.FromMilliseconds(ms); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }

    FrameworkElement TrackRow(JsonNode t, Func<Task> play)
    {
        var row = Row(Cover(t, true), (string?)t["name"] ?? "", Artists(t), play, trailing: Time((double?)t["duration_ms"] ?? 0));
        var uri = (string?)t["uri"]; var id = (string?)t["id"];
        var menu = new ContextMenu();
        var queue = new MenuItem { Header = "Add to queue" }; queue.Click += async (_, _) => { await Do("POST", $"/me/player/queue?uri={Uri.EscapeDataString(uri ?? "")}", refresh: false); await DrawQueue(); };
        var like = new MenuItem { Header = "Save to Liked songs" }; like.Click += (_, _) => _ = Do("PUT", $"/me/tracks?ids={id}", control: false);
        // add to one of your playlists (the list is fetched when the menu first opens)
        var addTo = new MenuItem { Header = "Add to playlist" };
        addTo.Items.Add(new MenuItem { Header = "Loading…", IsEnabled = false });
        addTo.SubmenuOpened += async (_, _) =>
        {
            if (addTo.Items.Count != 1 || addTo.Items[0] is not MenuItem { IsEnabled: false }) return;
            var lists = await MyPlaylists();
            addTo.Items.Clear();
            foreach (var pl in lists)
            {
                var item = new MenuItem { Header = (string?)pl["name"] };
                item.Click += async (_, _) =>
                {
                    var (code, _) = await SpotifyApi.Send("POST", $"/playlists/{(string?)pl["id"]}/tracks", new JsonObject { ["uris"] = new JsonArray((JsonNode)JsonValue.Create(uri)!) });
                    Say(code is >= 200 and < 300 ? $"Added to {(string?)pl["name"]}." : "Spotify didn't add it.");
                };
                addTo.Items.Add(item);
            }
            if (lists.Count == 0) addTo.Items.Add(new MenuItem { Header = "No playlists yet", IsEnabled = false });
        };
        menu.Items.Add(queue); menu.Items.Add(like); menu.Items.Add(addTo);
        row.ContextMenu = menu;
        return row;
    }

    FrameworkElement Row(string? image, string title, string sub, Func<Task> click, string? icon = null, string? trailing = null)
    {
        var pic = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0) };
        if (image != null) pic.Background = Cover(image);
        else { Res(pic, Border.BackgroundProperty, "Glass"); pic.Child = Icon(icon ?? "", 16, null); ((TextBlock)pic.Child).HorizontalAlignment = HorizontalAlignment.Center; }
        var dock = new DockPanel();
        DockPanel.SetDock(pic, Dock.Left); dock.Children.Add(pic);
        if (trailing != null) { var tr = Text(trailing, 12, "Dim", null, new Thickness(12, 0, 4, 0)); tr.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(tr, Dock.Right); dock.Children.Add(tr); }
        var title1 = Text(title, 14, "Fg"); title1.TextTrimming = TextTrimming.CharacterEllipsis;
        var sub1 = Text(sub, 12, "Dim"); sub1.TextTrimming = TextTrimming.CharacterEllipsis;
        dock.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title1, sub1 } });
        var row = new Border { Child = dock, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                               BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 1, 0, 1) };
        row.MouseEnter += (_, _) => Res(row, Border.BackgroundProperty, "Glass");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += async (_, _) => await click();
        return row;
    }

    Button PlayButton(string text, Func<Task> click, string glyph = "")
    {
        var b = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, new TextBlock { Text = text } } },
                             Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), Foreground = Brushes.Black, Padding = new Thickness(18, 8, 18, 8), FontWeight = FontWeights.SemiBold,
                             HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14), Cursor = Cursors.Hand, Template = Pill() };   // (Actions() resets its margin in a row)
        b.Click += async (_, _) => await click();
        return b;
    }

    TextBlock Heading(string text, double top = 0) => Text(text, top == 0 ? 24 : 16, "Fg", FontWeights.Bold, new Thickness(0, top, 0, 12));

    TextBlock Text(string text, double size, string brush, FontWeight? weight = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        Res(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    TextBlock Icon(string glyph, double size, string? color, Thickness? margin = null)
    {
        var t = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        if (color != null) t.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); else Res(t, TextBlock.ForegroundProperty, "Fg");
        return t;
    }

    Button Small(string glyph, string tip, double size) =>
        new() { Style = (Style)FindResource("Ghost"), Content = glyph, FontSize = size, ToolTip = tip, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };

    static void Mark(Button b, bool on)
    {
        if (on) b.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)); else b.ClearValue(Control.ForegroundProperty);
    }

    static void Res(FrameworkElement el, DependencyProperty p, string key) => el.SetResourceReference(p, key);

    static ControlTemplate Round()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp); t.VisualTree = bd;
        return t;
    }

    static ControlTemplate Pill()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp); t.VisualTree = bd;
        return t;
    }
}
