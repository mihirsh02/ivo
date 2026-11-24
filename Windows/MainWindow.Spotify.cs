using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Ivo;

// Spotify in Ivo: the island in the middle of the title bar (what's playing anywhere on your account: phone, speakers,
// this PC), which opens downward into a player like the Dynamic Island, and the Spotify panel, opened like the Inbox.
// Spotify is asked every few seconds while Ivo's window is open, never otherwise.
public partial class MainWindow
{
    DispatcherTimer? musicTimer, islandClock;
    SpotifyView? spotifyView;
    JsonNode? nowPlaying;
    DateTime nowPolled;
    bool islandOpen;
    // the line being sung, in the island (folded: in place of the song's name; open: under it). LRCLIB, as in the Spotify panel.
    List<Lyrics.Line>? islandLyrics;
    string? islandLyricsFor, islandTitle;

    void StartSpotify()
    {
        IsVisibleChanged += (_, _) => MusicTimerOn();
        StateChanged += (_, _) => MusicTimerOn();
        // a click anywhere else folds the island back up
        PreviewMouseDown += (_, e) => { if (islandOpen && !Island.IsMouseOver) FoldIsland(); };
        Deactivated += (_, _) => FoldIsland();
        Island.MouseEnter += (_, _) => IslandTick(); Island.MouseLeave += (_, _) => IslandTick();   // song info on hover, straight away
        OnDrag(IslandSeek, v => MusicDo("PUT", $"/me/player/seek?position_ms={(long)v}"));
        OnDrag(IslandVolume, v => MusicDo("PUT", $"/me/player/volume?volume_percent={(int)v}"));
        MusicTimerOn();
    }

    bool SpotifyOn => Connections.Connected(Connections.All.First(s => s.Id == "spotify"));

    void OpenSpotify_Click(object s, RoutedEventArgs e) { if (SpotifyLayer.IsVisible) CloseSpotify(); else OpenSpotify(); }
    void CloseSpotify_Click(object s, MouseButtonEventArgs e) => CloseSpotify();
    void CloseSpotify() => SpotifyLayer.Visibility = Visibility.Collapsed;

    public void MusicTimerOn()
    {
        BtnSpotify.Visibility = SpotifyOn && !S.HiddenButtons.Contains("spotify") ? Visibility.Visible : Visibility.Collapsed;
        var on = SpotifyOn && IsVisible && WindowState != WindowState.Minimized;
        if (on && musicTimer == null)   // Spotify's clocks exist only once it's connected
        {
            musicTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) => await PollMusic(), Dispatcher);
            islandClock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => IslandTick(), Dispatcher);
        }
        if (on && musicTimer is { IsEnabled: false }) { musicTimer.Start(); _ = PollMusic(); }
        else if (!on) { musicTimer?.Stop(); islandClock?.Stop(); FoldIsland(); }
        if (!SpotifyOn) Island.Visibility = Visibility.Collapsed;
    }

    async Task PollMusic()
    {
        if (!SpotifyOn) return;
        try { ShowMusic(await SpotifyApi.Get("/me/player")); } catch { }
    }

    // ---------- the island ----------
    public void ShowMusic(JsonNode? p)
    {
        nowPlaying = p; nowPolled = DateTime.Now;
        if (panelKey == "ivo") ivoView?.NowPlaying(Card(p));   // her music card follows along
        var item = p?["item"];
        if (item == null) { FoldIsland(); islandClock?.Stop(); Island.Visibility = Visibility.Collapsed; IslandBarsOn(false); return; }
        var playing = p!["is_playing"]?.GetValue<bool>() == true;
        var name = (string?)item["name"] ?? ""; var who = SpotifyView.Artists(item);
        islandTitle = $"{name} · {who}";
        if ((string?)item["uri"] is var uri && uri != islandLyricsFor) { islandLyricsFor = uri; islandLyrics = null; _ = IslandLyrics(item, uri); NewSongForInvites(item); _ = IvoOnNewSong(item); }
        islandClock?.Start();
        IslandSong.Text = name; IslandArtist.Text = who;
        IslandDevice.Text = p["device"] is { } d ? "On " + (string?)d["name"] : "";
        MusicPlayBtn.Content = playing ? "" : "";
        MusicPlayBtn.ToolTip = playing ? "Pause" : "Play";
        if (SpotifyView.Cover(item, small: true) is { } small) SpotifyView.Fill(MusicArt, small);
        if (SpotifyView.Cover(item, small: false) is { } big) { SpotifyView.Fill(IslandArt, big); if (big != islandCover) { islandCover = big; _ = IslandHue(big); } }
        var green = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)); var grey = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xAE));
        IslandShuffle.Foreground = p["shuffle_state"]?.GetValue<bool>() == true ? green : grey;
        var rep = (string?)p["repeat_state"] ?? "off";
        IslandRepeat.Content = rep == "track" ? "" : ""; IslandRepeat.Foreground = rep == "off" ? grey : green;
        // some devices (phones) don't let Spotify set their volume: then there's no slider to touch
        IslandVolume.IsEnabled = p["device"]?["volume_percent"] != null && p["device"]?["supports_volume"]?.GetValue<bool>() != false;
        if (p["device"]?["volume_percent"] is { } v && !IslandVolume.IsMouseCaptureWithin) IslandVolume.Value = (int)v;
        Island.Visibility = Visibility.Visible;
        IslandBarsOn(playing);
        PortalMusic(p);   // Listen together: tell the other side
        IslandTick();
    }

    // the album's colour washes the island from the top, fading to black, as in the Spotify panel
    string? islandCover;
    async Task IslandHue(string url)
    {
        if (await SpotifyView.Image(url) is not { } img || url != islandCover) return;
        var c = SpotifyView.Tint(img);
        var top = Color.FromRgb((byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7));
        Island.Background = new LinearGradientBrush(top, Colors.Black, new Point(0, 0), new Point(0.35, 1));
    }

    async Task IslandLyrics(JsonNode item, string? uri)
    {
        var found = await Lyrics.For(item);
        if (uri == islandLyricsFor) { islandLyrics = found; IslandTick(); }
    }

    void IslandTick()
    {
        if (nowPlaying?["item"] is not { } item) return;
        var total = (double?)item["duration_ms"] ?? 1;
        var pos = (double?)nowPlaying["progress_ms"] ?? 0;
        if (nowPlaying["is_playing"]?.GetValue<bool>() == true) pos += (DateTime.Now - nowPolled).TotalMilliseconds;
        // the line being sung (synced lyrics only); between lines, and without lyrics, the song's name
        var sung = islandLyrics is { Count: > 0 } ls && ls[0].At >= 0 ? ls.LastOrDefault(l => l.At <= pos / 1000 + 0.25)?.Text : null;
        if (string.IsNullOrWhiteSpace(sung)) sung = null;
        // folded: the sung line, but the song's name and artist while the pointer is on it
        var title = sung != null && !Island.IsMouseOver ? "♪  " + sung : islandTitle ?? "";
        if (MusicTitle.Text != title) MusicTitle.Text = title;
        // open: beside the song when its name and artist leave room for it, otherwise under them
        IslandInfo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var side = sung != null && IslandInfo.DesiredSize.Width <= IslandBig.Width - 64 - 14 - 30 - 178;
        IslandLyricSide.Text = side ? sung! : ""; IslandLyricSide.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        IslandLyric.Text = sung != null && !side ? sung : ""; IslandLyric.Visibility = sung != null && !side ? Visibility.Visible : Visibility.Collapsed;
        if (!islandOpen || IslandSeek.IsMouseCaptureWithin) return;
        IslandSeek.Maximum = total; IslandSeek.Value = Math.Min(pos, total);
        IslandAt.Text = Clock(pos); IslandLength.Text = Clock(total);
    }

    // A slider tells Spotify only after you pressed it and let go with a different value, so a stray release over it (the
    // island opening under the pointer, a click that started elsewhere) never sends a value, such as 0, that would mute the music.
    public static void OnDrag(System.Windows.Controls.Slider slider, Func<double, Task> send)
    {
        // (pressed, not "value changed since the press": the slider jumps to the click before any handler of ours runs)
        var pressed = false;
        slider.PreviewMouseLeftButtonDown += (_, _) => pressed = true;
        slider.PreviewMouseLeftButtonUp += (_, _) => { if (pressed) _ = send(slider.Value); pressed = false; };
        slider.MouseLeave += (_, _) => { if (!slider.IsMouseCaptureWithin) pressed = false; };
    }

    static string Clock(double ms) { var t = TimeSpan.FromMilliseconds(ms); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }

    // the little sound bars dance while music plays (one shared animation each, stopped when paused)
    void IslandBarsOn(bool on)
    {
        int i = 0;
        foreach (Border b in IslandBars.Children)
        {
            var s = (ScaleTransform)b.RenderTransform;
            if (on) s.BeginAnimation(ScaleTransform.ScaleYProperty, App.Loop(new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(380 + i++ * 90)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }));
            else s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(200)));
        }
    }

    void Island_Click(object s, MouseButtonEventArgs e)
    {
        if (islandOpen || e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
        OpenIsland();
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        // text pieces (a Run inside a TextBlock) aren't visuals: step out of them through the logical tree
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x)) if (x is T t) return t;
        return null;
    }

    // opening: the pill grows into the player (width and height ease out with a little overshoot, the content crossfades)
    void OpenIsland()
    {
        islandOpen = true;
        var from = new Size(Island.ActualWidth, Island.ActualHeight);
        IslandBig.Visibility = Visibility.Visible;
        IslandBig.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var edge = Island.BorderThickness.Left + Island.BorderThickness.Right;
        var to = new Size(IslandBig.DesiredSize.Width + edge, IslandBig.DesiredSize.Height + edge);   // its margins are in DesiredSize already
        // once open it sizes itself to its content, so nothing inside can be clipped whatever the outline's weight
        Morph(from, to, 30, () => { Island.BeginAnimation(WidthProperty, null); Island.BeginAnimation(HeightProperty, null); Island.Width = Island.Height = double.NaN; });
        Fade(IslandSmall, 0, 120, () => IslandSmall.Visibility = Visibility.Collapsed);
        Fade(IslandBig, 1, 260, null, 120);
        Island.Cursor = Cursors.Arrow;
        islandClock?.Start(); IslandTick();
        _ = PollMusic();
    }

    void FoldIsland()
    {
        if (!islandOpen) return;
        islandOpen = false;
        IslandSmall.Visibility = Visibility.Visible;
        IslandSmall.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var to = new Size(IslandSmall.DesiredSize.Width + 17 + Island.BorderThickness.Left + Island.BorderThickness.Right, 30);
        Morph(new Size(Island.ActualWidth, Island.ActualHeight), to, 15, () => { Island.BeginAnimation(WidthProperty, null); Island.BeginAnimation(HeightProperty, null); Island.Width = double.NaN; Island.Height = 30; });
        Fade(IslandBig, 0, 100, () => IslandBig.Visibility = Visibility.Collapsed);
        Fade(IslandSmall, 1, 220, null, 90);
        Island.Cursor = Cursors.Hand;
    }

    void Morph(Size from, Size to, double radius, Action? done = null)
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        var t = TimeSpan.FromMilliseconds(360);
        var w = new DoubleAnimation(from.Width, to.Width, t) { EasingFunction = ease };
        if (done != null) w.Completed += (_, _) => done();
        Island.BeginAnimation(WidthProperty, w);
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(from.Height, to.Height, t) { EasingFunction = ease });
        Island.CornerRadius = new CornerRadius(radius == 30 ? 26 : 15);
    }

    static void Fade(UIElement el, double to, int ms, Action? done, int delay = 0)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay) };
        if (done != null) a.Completed += (_, _) => done();
        el.BeginAnimation(OpacityProperty, a);
    }

    // ---------- the Spotify panel ----------
    public void OpenSpotify()
    {
        if (!SpotifyOn) { OpenSettings("connections"); return; }
        FoldIsland();
        spotifyView ??= new SpotifyView(this, CloseSpotify);
        if (SpotifyHost.Child != spotifyView) SpotifyHost.Child = spotifyView;
        if (MainHidden && floating != SpotifyLayer) { Float(SpotifyLayer, 1000, 700, () => SpotifyLayer.Visibility = Visibility.Visible); return; }   // Ivo is in the tray: it floats
        if (floating == SpotifyLayer) { popout!.Activate(); return; }
        ShowWindow();
        SpotifyLayer.Visibility = Visibility.Visible;
        spotifyView.Focus();
    }

    async Task MusicDo(string method, string path)
    {
        try
        {
            var (status, _) = await SpotifyApi.Send(method, path, control: true);
            if (status == 403) _ = Notice("Spotify", "Needs Spotify Premium.");
            else if (status == 404) _ = Notice("Spotify", "No device: open Spotify somewhere first.");
        }
        catch (Exception ex) { _ = Notice("Spotify", ex.Message); }
        await Task.Delay(400);   // Spotify takes a moment to report the change
        await PollMusic();
        if (spotifyView?.IsVisible == true) await spotifyView.Refresh();
    }

    bool MusicPlaying => nowPlaying?["is_playing"]?.GetValue<bool>() == true;
    void MusicPrev_Click(object s, RoutedEventArgs e) => _ = MusicDo("POST", "/me/player/previous");
    void MusicNext_Click(object s, RoutedEventArgs e) => _ = MusicDo("POST", "/me/player/next");
    void MusicPlay_Click(object s, RoutedEventArgs e) => _ = MusicDo("PUT", MusicPlaying ? "/me/player/pause" : "/me/player/play");
    void IslandShuffle_Click(object s, RoutedEventArgs e) => _ = MusicDo("PUT", $"/me/player/shuffle?state={(nowPlaying?["shuffle_state"]?.GetValue<bool>() != true).ToString().ToLowerInvariant()}");
    void IslandRepeat_Click(object s, RoutedEventArgs e) => _ = MusicDo("PUT", "/me/player/repeat?state=" + ((string?)nowPlaying?["repeat_state"] switch { "off" => "context", "context" => "track", _ => "off" }));
}
