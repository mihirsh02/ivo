using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ivo;

// A group call, as Google Meet lays one out: everyone's camera (or their initial) as a tile, and every shared screen as
// its own tile; Auto puts a shared screen big with the cameras beside it, otherwise a grid. Pin any tile; pick a layout.
// On the right, People (everyone in the group: in the call, joining, or not, with their mic) and the group's own chat.
// One bar: mic, camera, present, people, chat, leave.
public class GroupCallView : Grid
{
    sealed class Tile
    {
        public readonly Border Box = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = B("#18FFFFFF") };
        public readonly Image Pic = new() { Stretch = Stretch.UniformToFill };
        public readonly Border MicOff;
        public readonly Viewbox Face;
        public readonly Button Pin;
        public WriteableBitmap? Bmp;
        public readonly bool Screen;
        public readonly string Key;
        public Tile(string key, string name, bool screen, Action<Tile> pin)
        {
            Key = key; Screen = screen;
            if (screen) Pic.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(Pic, BitmapScalingMode.Linear);
            var face = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                    Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#8B7CFF"), (Color)ColorConverter.ConvertFromString("#4F46E5"), 45),
                                    Child = new TextBlock { Text = name.Length > 0 ? name[..1].ToUpper() : "?", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            MicOff = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = B("#F43F5E"), Margin = new Thickness(0, 0, 7, 0), Visibility = Visibility.Collapsed,
                                  Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var tag = new Border { CornerRadius = new CornerRadius(8), Background = B("#99000000"), Padding = new Thickness(6, 3, 9, 4), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                                   Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { MicOff, new TextBlock { Text = screen ? $"{name} is presenting" : name, FontSize = 12.5, Foreground = Brushes.White } } } };
            Pin = Btn("", "Pin", "#99000000", () => pin(this), 32);
            Pin.HorizontalAlignment = HorizontalAlignment.Right; Pin.VerticalAlignment = VerticalAlignment.Top; Pin.Margin = new Thickness(8); Pin.Opacity = 0;
            Face = new Viewbox { Child = face, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(12, 12, 12, 38) };
            var inner = new Grid { Background = B("#1F2630"), Children = { Face, Pic, tag, Pin } };
            inner.SizeChanged += (_, _) => inner.Clip = new RectangleGeometry(new Rect(0, 0, inner.ActualWidth, inner.ActualHeight), 14, 14);
            Box.Child = inner;
            Box.MouseEnter += (_, _) => Pin.Opacity = 1; Box.MouseLeave += (_, _) => Pin.Opacity = 0;
        }
        public void Show(byte[] bgr, int w, int h) { Bmp = PortalCallView.Paint(Bmp, bgr, w, h); Pic.Source = Bmp; Face.Visibility = Visibility.Collapsed; }
        public void Clear() { Bmp = null; Pic.Source = null; Face.Visibility = Visibility.Visible; }
    }

    readonly GroupCall call;
    readonly string me;
    readonly Func<string, Task> video;
    readonly Dictionary<string, (Tile Cam, Tile Screen)> tiles = [];   // "" is you
    readonly HashSet<string> pending = [];
    Tile? pinned;
    string layout = "auto", shape = "";
    readonly Grid area = new();
    readonly TextBlock time = new() { FontSize = 13, Foreground = B("#A0AAB6"), VerticalAlignment = VerticalAlignment.Center }, count = new() { FontSize = 13, Foreground = B("#A0AAB6"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly Button mute, cam, screen, peopleBtn, layoutBtn, full;
    Button? chatBtn;
    readonly Border side;
    readonly StackPanel people = new();
    readonly FrameworkElement? chat;
    string sideShows = "people";
    readonly DispatcherTimer tick;
    public event Action? Ticked;

    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static Brush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    public string Elapsed { get { var t = DateTime.Now - call.Started; return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); } }

    public GroupCallView(GroupCall call, string me, Func<string, Task> video, Action leave, Action fullScreen, Action? backToApp, FrameworkElement? chat)
    {
        this.call = call; this.me = me; this.video = video; this.chat = chat;
        ClipToBounds = true;
        Background = new RadialGradientBrush((Color)ColorConverter.ConvertFromString("#2A2163"), (Color)ColorConverter.ConvertFromString("#0B0F14")) { RadiusX = 0.75, RadiusY = 0.75 };
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        tiles[""] = (new Tile("", "You", false, Pin), new Tile("", "You", true, Pin));

        // the top: the group, the time and how many are in; layout, back and full screen on the right
        var title = new TextBlock { Text = call.Thread.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        layoutBtn = Btn("", "Change layout", "#33000000", () => { }, 38);
        layoutBtn.Click += (_, _) => LayoutMenu().IsOpen = true;
        full = Btn("", "Full screen", "#33000000", fullScreen, 38);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { layoutBtn } };
        if (backToApp != null) right.Children.Add(Btn("", "Back to Ivo (the call goes on)", "#33000000", backToApp, 38));
        right.Children.Add(full);
        foreach (Button b in right.Children) { b.Margin = new Thickness(4, 0, 0, 0); System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true); }
        var top = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 14, 18, 0), Children = { new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { title, time, count } }, right } };

        mute = Btn("", "Mute", "#2B3641", () => call.Mute(!call.Muted), 54);
        cam = Btn("", "Camera", "#2B3641", () => _ = video(call.Local.CameraOn ? "camera-off" : "camera"), 54);
        screen = Btn("", "Present your screen", "#2B3641", () => _ = video(call.Local.Sending == "screen" ? "screen-off" : "screen"), 54);
        peopleBtn = Btn("", "People", "#2B3641", () => Side("people"), 54);
        var end = Btn("", "Leave the call", "#F43F5E", leave, 54); end.Width = 68;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { mute, cam, screen, peopleBtn } };
        if (chat != null) buttons.Children.Add(chatBtn = Btn("", "Chat", "#2B3641", () => Side("chat"), 54));
        buttons.Children.Add(end);
        foreach (Button b in buttons.Children) b.Margin = new Thickness(6, 0, 6, 0);
        barButtons = buttons; endButton = end;
        var bar = new Border { CornerRadius = new CornerRadius(36), Padding = new Thickness(14, 10, 14, 10), Background = B("#CC161B22"), HorizontalAlignment = HorizontalAlignment.Center,
                               VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20), BorderBrush = B("#22FFFFFF"), BorderThickness = new Thickness(1), Child = buttons };

        var stage = new Grid { Children = { area, top, bar } };
        stage.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) fullScreen(); };
        side = new Border { Width = 340, Background = B("#E6161B22"), BorderBrush = B("#22FFFFFF"), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(18) };
        ColumnDefinitions.Add(new ColumnDefinition()); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(side, 1);
        Children.Add(stage); Children.Add(side);
        Side("people");

        call.Local.MyFrame += MyFrame;
        call.PeerAdded += (id, p) => Dispatcher.BeginInvoke(() => Hook(id, p));
        call.PeerGone += id => Dispatcher.BeginInvoke(() => { tiles.Remove(id); if (pinned?.Key == id) pinned = null; Refresh(); });
        call.Changed += () => Dispatcher.BeginInvoke(Refresh);
        tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { Refresh(); Ticked?.Invoke(); }, Dispatcher);
        tick.Start();
        Refresh();
    }

    public void Detach() { tick.Stop(); call.Local.MyFrame -= MyFrame; }
    // Add people: a contact picked is added to the group and pulled into this call
    StackPanel? barButtons; Button? endButton;
    public void AddPeopleButton(Action<FrameworkElement> add)
    {
        if (barButtons == null || endButton == null) return;
        Button? b = null;
        b = Btn("", "Add people", "#2B3641", () => add(b!), 54); b.Margin = new Thickness(6, 0, 6, 0);
        barButtons.Children.Insert(barButtons.Children.IndexOf(endButton), b);
    }

    public void FullScreenShown(bool on) { full.Content = on ? "" : ""; full.ToolTip = on ? "Leave full screen" : "Full screen"; }

    // someone's line opened: their tiles, fed by their frames
    void Hook(string id, Portal p)
    {
        var name = call.NameOf(id);
        tiles[id] = (new Tile(id, name, false, Pin), new Tile(id, name, true, Pin));
        p.TheirFrame += (bgr, w, h) =>
        {
            lock (pending) if (!pending.Add(id)) return;
            Dispatcher.BeginInvoke(() =>
            {
                lock (pending) pending.Remove(id);
                if (!tiles.TryGetValue(id, out var t)) return;
                Split(t, bgr, w, h, p.TheirKind, p.TheirSplit);
                Changed();
            });
        };
        p.Message += o => { if ((string?)o["t"] == "video" && (bool?)o["on"] == false) Dispatcher.BeginInvoke(() => { if (tiles.TryGetValue(id, out var t)) { t.Cam.Clear(); t.Screen.Clear(); Refresh(); } }); };
        p.TheirMic += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    // one picture into tiles: a camera, a screen, or both side by side
    static void Split((Tile Cam, Tile Screen) t, byte[] bgr, int w, int h, string kind, (double Screen, double Camera) split)
    {
        int sw = (int)Math.Round(w * split.Screen), ch = Math.Min(h, (int)Math.Round(h * split.Camera));
        if (kind == "screen" && split.Screen > 0 && sw > 0 && sw < w && ch > 0) { t.Screen.Show(PortalCallView.Crop(bgr, w, 0, 0, sw, h), sw, h); t.Cam.Show(PortalCallView.Crop(bgr, w, sw, 0, w - sw, ch), w - sw, ch); }
        else if (kind == "screen") { t.Screen.Show(bgr, w, h); t.Cam.Clear(); }
        else { t.Cam.Show(bgr, w, h); t.Screen.Clear(); }
    }

    bool myPending;
    void MyFrame(byte[] bgr, int w, int h)
    {
        if (myPending) return; myPending = true;
        Dispatcher.BeginInvoke(() => { myPending = false; Split(tiles[""], bgr, w, h, call.Local.Sending, call.Local.MySplit); Changed(); });
    }

    // a frame only re-lays out when a tile came or went; People and the rest follow the once-a-second tick
    string lastLive = "";
    void Changed()
    {
        var live = string.Concat(tiles.Values.Select(x => $"{x.Cam.Pic.Source != null}{x.Screen.Pic.Source != null}"));
        if (live != lastLive) { lastLive = live; Arrange(); }
    }

    void Pin(Tile t) { pinned = pinned == t ? null : t; shape = ""; Refresh(); }

    void Side(string what)
    {
        if (side.Visibility == Visibility.Visible && sideShows == what) side.Visibility = Visibility.Collapsed;
        else { sideShows = what; side.Visibility = Visibility.Visible; side.Child = what == "chat" && chat != null ? chat : new ScrollViewer { Content = people, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; }
        Lit(peopleBtn, side.Visibility == Visibility.Visible && sideShows == "people");
        if (chatBtn != null) Lit(chatBtn, side.Visibility == Visibility.Visible && sideShows == "chat");
    }

    // everyone in the group: you, then who's in the call, joining, or not in it; with a mic-off mark
    void People()
    {
        people.Children.Clear();
        people.Children.Add(new TextBlock { Text = "People", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 14) });
        FrameworkElement Row(string name, string state, string color, bool micOff)
        {
            var face = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                                    Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#8B7CFF"), (Color)ColorConverter.ConvertFromString("#4F46E5"), 45),
                                    Child = new TextBlock { Text = name.Length > 0 ? name[..1].ToUpper() : "?", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var mic = new TextBlock { Text = micOff ? "" : "", FontFamily = Icons, FontSize = 13, Foreground = B("#F43F5E"), VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(face, Dock.Left); DockPanel.SetDock(mic, Dock.Right);
            row.Children.Add(face); row.Children.Add(mic);
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Text = name, FontSize = 13.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = state, FontSize = 11.5, Foreground = B(color) } } });
            return row;
        }
        var inCall = new List<FrameworkElement> { Row($"{(me == "" ? "You" : me)} (you)", "In the call", "#22C55E", call.Muted) };
        var others = new List<FrameworkElement>();
        foreach (var m in call.Thread.Members.Where(m => m.Id != call.MyId && m.Name != ""))
        {
            Portal? p; lock (call.Peers) call.Peers.TryGetValue(m.Id, out p);
            bool isIn; lock (call.In) isIn = call.In.Contains(m.Id);
            if (p is { Open: true }) inCall.Add(Row(m.Name, "In the call", "#22C55E", p.TheirMuted));
            else if (isIn) inCall.Add(Row(m.Name, "Joining…", "#F59E0B", false));
            else others.Add(Row(m.Name, "Not in the call", "#8B95A3", false));
        }
        people.Children.Add(new TextBlock { Text = $"IN THE CALL · {inCall.Count}", FontSize = 11, Foreground = B("#8B95A3"), Margin = new Thickness(0, 0, 0, 10) });
        foreach (var r in inCall) people.Children.Add(r);
        if (others.Count > 0)
        {
            people.Children.Add(new TextBlock { Text = $"NOT IN THE CALL · {others.Count}", FontSize = 11, Foreground = B("#8B95A3"), Margin = new Thickness(0, 10, 0, 10) });
            foreach (var r in others) people.Children.Add(r);
        }
    }

    void Refresh()
    {
        time.Text = Elapsed;
        int n; lock (call.Peers) n = call.Peers.Values.Count(p => p.Open) + 1;
        count.Text = $"{n} in the call";
        mute.Content = call.Muted ? "" : ""; mute.ToolTip = call.Muted ? "Unmute" : "Mute"; Lit(mute, call.Muted, "#F43F5E");
        cam.Content = call.Local.CameraOn ? "" : ""; cam.ToolTip = call.Local.CameraOn ? "Turn the camera off" : "Turn the camera on"; Lit(cam, call.Local.CameraOn);
        screen.ToolTip = call.Local.Sending == "screen" ? "Stop presenting" : "Present your screen"; Lit(screen, call.Local.Sending == "screen");
        if (!call.Local.CameraOn) tiles[""].Cam.Clear();
        if (call.Local.Sending != "screen") tiles[""].Screen.Clear();
        tiles[""].Cam.MicOff.Visibility = call.Muted ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, t) in tiles)
            if (id != "") { Portal? p; lock (call.Peers) call.Peers.TryGetValue(id, out p); t.Cam.MicOff.Visibility = p?.TheirMuted == true ? Visibility.Visible : Visibility.Collapsed; if (p?.TheirKind != "screen") t.Screen.Clear(); }
        foreach (var t in tiles.Values.SelectMany(x => new[] { x.Cam, x.Screen })) { t.Pin.Content = pinned == t ? "" : ""; t.Pin.ToolTip = pinned == t ? "Unpin" : "Pin"; }
        if (sideShows == "people" && side.Visibility == Visibility.Visible) People();
        Arrange();
    }

    // the tiles: every camera (you first), and every screen being shared
    void Arrange()
    {
        var up = new List<Tile>();
        foreach (var (id, t) in tiles) if (t.Screen.Pic.Source != null) up.Add(t.Screen);
        up.Add(tiles[""].Cam);
        foreach (var (id, t) in tiles) if (id != "") up.Add(t.Cam);
        var main = pinned != null && up.Contains(pinned) ? pinned : up.FirstOrDefault(t => t.Screen) ?? up[0];
        var mode = layout != "auto" ? layout : pinned != null || up.Any(t => t.Screen) ? "sidebar" : "tiled";
        var key = $"{mode}|{string.Join(',', up.Select(t => t.Key + t.Screen))}|{main.Key}{main.Screen}";
        if (key == shape) return;
        shape = key;
        foreach (var t in tiles.Values.SelectMany(x => new[] { x.Cam, x.Screen })) { if (t.Box.Parent is Panel p) p.Children.Remove(t.Box); t.Box.Width = t.Box.Height = double.NaN; t.Box.Margin = new Thickness(0); t.Box.HorizontalAlignment = HorizontalAlignment.Stretch; t.Box.VerticalAlignment = VerticalAlignment.Stretch; }
        area.Children.Clear();
        var room = new Thickness(16, 60, 16, 100);
        if (mode == "spotlight") { main.Box.Margin = room; area.Children.Add(main.Box); return; }
        if (mode == "tiled")
        {
            var grid = new UniformGrid { Columns = (int)Math.Ceiling(Math.Sqrt(up.Count)), Margin = room };
            foreach (var t in up)
            {
                var cell = new Grid { Margin = new Thickness(6), Children = { t.Box } };   // each tile 16:9, as big as fits
                t.Box.HorizontalAlignment = HorizontalAlignment.Center; t.Box.VerticalAlignment = VerticalAlignment.Center;
                var box = t.Box;
                cell.SizeChanged += (_, _) => { var w = Math.Min(cell.ActualWidth, cell.ActualHeight * 16 / 9); box.Width = w; box.Height = w * 9 / 16; };
                grid.Children.Add(cell);
            }
            area.Children.Add(grid); return;
        }
        var g = new Grid { Margin = room };
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        main.Box.Margin = new Thickness(0, 0, 12, 0); g.Children.Add(main.Box);
        var column = new StackPanel();
        foreach (var t in up.Where(t => t != main)) { t.Box.Width = 260; t.Box.Height = 146; t.Box.Margin = new Thickness(0, 0, 0, 10); column.Children.Add(t.Box); }
        var scroll = new ScrollViewer { Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        SetColumn(scroll, 1); g.Children.Add(scroll);
        area.Children.Add(g);
    }

    ContextMenu LayoutMenu()
    {
        var menu = new ContextMenu { PlacementTarget = layoutBtn, Placement = PlacementMode.Bottom };
        foreach (var (key, label) in new[] { ("auto", "Auto"), ("tiled", "Tiled"), ("spotlight", "Spotlight"), ("sidebar", "Sidebar") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = layout == key };
            item.Click += (_, _) => { layout = key; shape = ""; Refresh(); };
            menu.Items.Add(item);
        }
        return menu;
    }

    static Button Btn(string glyph, string tip, string hex, Action click, double size)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        var b = new Button { Background = B(hex), Tag = B(hex), Content = glyph, FontFamily = Icons, FontSize = size > 40 ? 20 : 13, Foreground = Brushes.White, Width = size, Height = size, ToolTip = tip, Cursor = Cursors.Hand, Focusable = false,
                             Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.MouseEnter += (_, _) => b.Opacity = 0.85; b.MouseLeave += (_, _) => b.Opacity = 1;
        b.Click += (_, _) => click();
        return b;
    }
    static void Lit(Button b, bool on, string color = "#7C5CFF") => b.Background = on ? B(color) : (Brush)b.Tag;
}
