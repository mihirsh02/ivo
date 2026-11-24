using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ivo;

// A call, as one screen (inside Ivo, or filling Ivo Portal's window), laid out as Google Meet does. Everyone's camera and
// every shared screen is its own tile: theirs, and yours (what you send, shown back to you, with its size and frame
// rate). Layouts: Auto (their video filling it with you in the corner; with a screen shared, the screen big and the
// cameras beside it), Tiled, Spotlight and Sidebar; pin any tile to make it the big one. While you present, a banner
// says so, with Stop presenting. The name and time at the top; one bar at the bottom with mic, camera, present, chat
// and hang up. Answering starts at "Ready to join?": your camera preview (sent to no one yet), then Join now.
public class PortalCallView : Grid
{
    // ---------- a tile: one camera or one screen ----------
    sealed class Tile
    {
        public readonly Border Box;
        public readonly Image Pic;
        public readonly TextBlock Name, Stats;
        public readonly Border MicOff, StatsChip;
        public readonly Button Pin;
        public readonly bool Screen;
        public WriteableBitmap? Bmp;
        public bool Live => Pic.Source != null;
        public FrameworkElement? Face;   // hidden while the picture's there
        public Tile(Border box, Image pic, TextBlock name, Border micOff, TextBlock stats, Border statsChip, Button pin, bool screen) { Box = box; Pic = pic; Name = name; MicOff = micOff; Stats = stats; StatsChip = statsChip; Pin = pin; Screen = screen; }
    }

    readonly Portal portal;
    readonly Func<string, Task> video;
    readonly string peer;
    readonly Tile theirCam, theirScreen, myCam, myScreen;
    Tile? pinned;
    string layout = "auto";   // auto · tiled · spotlight · sidebar
    readonly Grid area = new();
    readonly TextBlock time = new() { FontSize = 13, Foreground = B("#A0AAB6"), VerticalAlignment = VerticalAlignment.Center };
    readonly Button mute, cam, screen, full, layoutBtn;
    readonly Border presenting;
    Button? chatBtn;
    Border? side;
    readonly Grid stage;
    Grid? lobby;
    readonly DateTime started = DateTime.Now;
    readonly DispatcherTimer tick;
    WriteableBitmap? myBmp;   // the whole of what you send (the "Ready to join?" preview)
    bool theirPending, minePending;
    int myFrames, myFps;
    (int W, int H) myCamSize, myScreenSize;
    string shape = "";   // which tiles are up, the layout and the pin: re-laid out only when this changes

    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public string Elapsed { get { var t = DateTime.Now - started; return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); } }
    public event Action? Ticked;   // once a second (Ivo's title-bar pill follows it)

    static Brush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    // join: answering a call starts at "Ready to join?"; Join now hands over the mic and camera as set there
    public PortalCallView(Portal portal, string peer, Action hangUp, Func<string, Task> video, Action fullScreen, Action? backToApp, FrameworkElement? chat = null,
                          string me = "", Func<bool, bool, Task>? join = null, string? theirPic = null, string? myPic = null)
    {
        this.portal = portal; this.video = video; this.peer = peer;
        ClipToBounds = true;
        // a deep background with a violet glow behind them
        Background = new RadialGradientBrush((Color)ColorConverter.ConvertFromString("#2A2163"), (Color)ColorConverter.ConvertFromString("#0B0F14")) { RadiusX = 0.75, RadiusY = 0.75, Center = new Point(0.5, 0.45), GradientOrigin = new Point(0.5, 0.45) };
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        theirCam = MakeTile(peer, Face(peer, 132, breathing: true, theirPic), screen: false);
        theirScreen = MakeTile($"{peer} is presenting", Face(peer, 72, breathing: false, theirPic), screen: true);
        myCam = MakeTile("You", Face(me.Length > 0 ? me : "You", 72, breathing: false, myPic), screen: false);
        SelfControls();
        myScreen = MakeTile("You are presenting", Face(me.Length > 0 ? me : "You", 72, breathing: false, myPic), screen: true);

        // the top: name and time; layout, back to the app and full screen on the right
        var who = new TextBlock { Text = peer, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        full = Small("", "Full screen", fullScreen);
        layoutBtn = Small("", "Change layout", () => { });
        layoutBtn.Click += (_, _) => LayoutMenu().IsOpen = true;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { layoutBtn } };
        if (backToApp != null) right.Children.Add(Small("", "Back to Ivo (the call goes on)", backToApp));
        right.Children.Add(full);
        // presenting: Meet's banner, with Stop presenting
        var stop = new Button { Content = "Stop presenting", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = B("#7C5CFF"), Height = 32, Padding = new Thickness(16, 0, 16, 0), Cursor = Cursors.Hand, Margin = new Thickness(14, 0, 0, 0), Template = Pill() };
        stop.Click += (_, _) => _ = Switch("screen-off");
        presenting = new Border { CornerRadius = new CornerRadius(20), Background = B("#E61F2630"), Padding = new Thickness(16, 6, 6, 6), HorizontalAlignment = HorizontalAlignment.Center, BorderBrush = B("#407C5CFF"), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
                                 Child = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                                     new TextBlock { Text = "", FontFamily = Icons, FontSize = 14, Foreground = B("#A78BFA"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
                                     new TextBlock { Text = $"You're presenting to {peer}", FontSize = 13.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }, stop } } };
        var top = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 14, 18, 0),
                             Children = { new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { who, time } }, right } };
        presenting.VerticalAlignment = VerticalAlignment.Top; presenting.Margin = new Thickness(16, 62, 16, 0);   // its own line under the top, never over the buttons

        mute = Round("", "Mute", B("#2B3641"), () => { portal.Mute(!portal.Muted); Refresh(); });
        cam = Round("", "Camera", B("#2B3641"), () => _ = Switch(portal.CameraOn ? "camera-off" : "camera"));
        screen = Round("", "Share screen", B("#2B3641"), () => _ = Switch(portal.Sending == "screen" ? "screen-off" : "screen"));
        var end = Round("", "Hang up", B("#F43F5E"), hangUp);
        end.Width = 68;
        var bar = new Border { CornerRadius = new CornerRadius(36), Padding = new Thickness(14, 10, 14, 10), Background = B("#CC161B22"), HorizontalAlignment = HorizontalAlignment.Center,
                               VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20), BorderBrush = B("#22FFFFFF"), BorderThickness = new Thickness(1),
                               Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { mute, cam, screen } } };
        var buttons = barButtons = (StackPanel)bar.Child;
        // the chat, in the bar as Meet has it: shows or hides the panel on the right
        if (chat != null) buttons.Children.Add(chatBtn = Round("", "Hide the chat", B("#2B3641"), () =>
        {
            side!.Visibility = side.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            chatBtn!.ToolTip = side.Visibility == Visibility.Visible ? "Hide the chat" : "Show the chat"; Lit(chatBtn, side.Visibility == Visibility.Visible);
        }));
        buttons.Children.Add(endButton = end);

        // the call on the left; the chat (when there is one) in a panel on the right
        stage = new Grid { Children = { area, top, presenting, bar } };
        area.SizeChanged += (_, _) => Arrange();   // narrow or wide: the layout follows
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(stage);
        if (chat != null)
        {
            side = new Border { Width = 340, Background = B("#E6161B22"), BorderBrush = B("#22FFFFFF"), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(18, 18, 18, 18), Child = chat };
            SetColumn(side, 1); Children.Add(side);
            Lit(chatBtn!, true);
        }
        stage.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) fullScreen(); };

        // answering: "Ready to join?" first, over everything, until Join now
        if (join != null)
        {
            portal.Preview = true;
            var big = new Border { Width = 560, Height = 315, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 0, 16) };
            var preview = new Image { Stretch = Stretch.UniformToFill };
            var noCam = new TextBlock { Text = "Your camera is off", FontSize = 15, Foreground = B("#A0AAB6"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            big.Child = new Grid { Background = B("#1F2630"), Children = { noCam, preview } };
            RoundClip((FrameworkElement)big.Child, 18);
            portal.MyFrame += (bgr, w, h) => Dispatcher.BeginInvoke(() => { if (lobby?.Visibility == Visibility.Visible) preview.Source = myBmp; });
            Button? lMic = null, lCam = null;
            void Show() { lMic!.Content = portal.Muted ? "" : ""; Lit(lMic, portal.Muted); lCam!.Content = portal.Sending == "camera" ? "" : ""; Lit(lCam, portal.Sending != "camera"); if (portal.Sending != "camera") preview.Source = null; }
            lMic = Round("", "Microphone", B("#2B3641"), () => { portal.Mute(!portal.Muted); Show(); });
            lCam = Round("", "Camera", B("#2B3641"), async () => { await video(portal.Sending == "camera" ? "" : "camera"); Show(); });
            Show();
            var joinBtn = new Button { Content = "Join now", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = B("#7C5CFF"), Height = 48, Width = 170, Cursor = Cursors.Hand, Margin = new Thickness(0, 22, 0, 0),
                                       Template = Pill() };
            var notNow = new Button { Content = "Not now", FontSize = 14, Foreground = B("#D0D6DE"), Background = B("#2B3641"), Height = 44, Width = 170, Cursor = Cursors.Hand, Margin = new Thickness(0, 10, 0, 0), Template = Pill() };
            joinBtn.Click += async (_, _) => { portal.Preview = false; lobby!.Visibility = Visibility.Collapsed; stage.Visibility = Visibility.Visible; if (side != null) side.Visibility = Visibility.Visible; await join(!portal.Muted, portal.Sending == "camera"); Refresh(); };
            notNow.Click += (_, _) => { portal.Preview = false; _ = video(""); hangUp(); };
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 48, 0), Children = { big, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { lMic, lCam } } } };
            var ask = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Text = "Ready to join?", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
                new TextBlock { Text = $"{peer} is calling", FontSize = 14, Foreground = B("#A0AAB6"), Margin = new Thickness(0, 6, 0, 0) }, joinBtn, notNow } };
            lobby = new Grid { Children = { new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { left, ask } } } };
            SetColumnSpan(lobby, 2); Children.Add(lobby);
            stage.Visibility = Visibility.Collapsed; if (side != null) side.Visibility = Visibility.Collapsed;
        }
        portal.TheirMic += TheirMicChanged;

        portal.TheirFrame += TheirFrame; portal.MyFrame += MyFrame;
        tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { myFps = myFrames; myFrames = 0; Refresh(); Ticked?.Invoke(); }, Dispatcher);
        tick.Start();
        Refresh();
    }

    // the call is over: stop listening for frames and ticking
    // Add people: a contact picked joins, and the call becomes a group call
    StackPanel? barButtons; Button? endButton;
    public void AddPeopleButton(Action<FrameworkElement> add)
    {
        if (barButtons == null || endButton == null) return;
        Button? b = null;
        b = Round("\uE8FA", "Add people", B("#2B3641"), () => add(b!));
        barButtons.Children.Insert(barButtons.Children.IndexOf(endButton), b);
    }

    public void Detach() { tick.Stop(); portal.TheirFrame -= TheirFrame; portal.MyFrame -= MyFrame; portal.TheirMic -= TheirMicChanged; portal.Preview = false; }
    void TheirMicChanged() => Dispatcher.BeginInvoke(Refresh);

    // ---------- tiles ----------

    // a profile picture (base64, as chats keep it) as a round fill, decoded at the size it's shown; null when there's none
    public static ImageBrush? PicBrush(string? pic, int px)
    {
        if (pic is not { Length: > 0 }) return null;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage(); img.BeginInit(); img.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(pic)); img.DecodePixelWidth = px;
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.EndInit(); img.Freeze();
            return new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        }
        catch { return null; }
    }

    // someone without video: their profile picture, or their initial, in a circle (the big one breathes, as a call waiting on them)
    static FrameworkElement Face(string name, double size, bool breathing, string? pic = null)
    {
        var initial = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                   Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#8B7CFF"), (Color)ColorConverter.ConvertFromString("#4F46E5"), 45),
                                   Child = new TextBlock { Text = name.Length > 0 ? name[..1].ToUpper() : "?", FontSize = size * 0.42, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var face = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (breathing)
        {
            var ring = new Border { Width = size * 1.27, Height = size * 1.27, CornerRadius = new CornerRadius(size * 0.64), BorderBrush = B("#8B7CFF"), BorderThickness = new Thickness(2), Opacity = 0.5,
                                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var pulse = new DoubleAnimation(1, 1.18, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(pulse, 30);
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            var fade = new DoubleAnimation(0.55, 0.12, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(fade, 30);
            ring.BeginAnimation(OpacityProperty, fade);
            face.Children.Add(ring);
        }
        if (PicBrush(pic, (int)(size * 2)) is { } photo) { initial.Background = photo; initial.Child = null; RenderOptions.SetBitmapScalingMode(initial, BitmapScalingMode.HighQuality); }
        face.Children.Add(initial);
        return face;
    }

    // a tile: the picture (or their face), their name and a mic-off mark at the bottom left, the picture's size and frame
    // rate at the top left (your own tiles: how your video is going), and a pin at the top right on hover
    Tile MakeTile(string name, FrameworkElement face, bool screen)
    {
        var pic = new Image { Stretch = screen ? Stretch.Uniform : Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.Linear);   // smooth enough for video, and light on the GPU
        var micOff = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = B("#F43F5E"), Margin = new Thickness(0, 0, 7, 0), Visibility = Visibility.Collapsed,
                                  Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var label = new TextBlock { Text = name, FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var tag = new Border { CornerRadius = new CornerRadius(8), Background = B("#99000000"), Padding = new Thickness(6, 3, 9, 4), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                               Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { micOff, label } } };
        var stats = new TextBlock { FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = B("#22C55E"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var statsChip = new Border { CornerRadius = new CornerRadius(8), Background = B("#99000000"), Padding = new Thickness(7, 3, 9, 4), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                                     Visibility = Visibility.Collapsed, IsHitTestVisible = false, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, stats } } };
        var pin = Round("", "Pin", B("#99000000"), () => { });
        pin.Width = pin.Height = 34; pin.FontSize = 13; pin.Margin = new Thickness(8); pin.HorizontalAlignment = HorizontalAlignment.Right; pin.VerticalAlignment = VerticalAlignment.Top; pin.Opacity = 0;
        face = new Viewbox { Child = face, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(14, 14, 14, 40) };   // a small tile: a smaller face, never cut off
        var inner = new Grid { Background = B("#1F2630"), Children = { face, pic, tag, statsChip, pin } };
        var box = new Border { CornerRadius = new CornerRadius(16), Child = inner, BorderThickness = new Thickness(1), BorderBrush = B("#18FFFFFF") };
        RoundClip(inner, 16);
        var t = new Tile(box, pic, label, micOff, stats, statsChip, pin, screen) { Face = face };
        pin.Click += (_, _) => { pinned = pinned == t ? null : t; Arrange(); };
        box.MouseEnter += (_, _) => pin.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        box.MouseLeave += (_, _) => pin.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        return t;
    }

    static void Show(Tile t, byte[] bgr, int w, int h) { t.Bmp = Paint(t.Bmp, bgr, w, h); t.Pic.Source = t.Bmp; if (t.Face != null) t.Face.Visibility = Visibility.Collapsed; }
    static void Clear(Tile t) { t.Bmp = null; t.Pic.Source = null; if (t.Face != null) t.Face.Visibility = Visibility.Visible; }

    // the tiles on show: both cameras always (a face without video), each shared screen while it's shared
    List<Tile> Up()
    {
        var up = new List<Tile>();
        if (portal.TheirKind == "screen") up.Add(theirScreen);
        if (portal.Sending == "screen") up.Add(myScreen);
        up.Add(theirCam);
        if (pinned == myCam) up.Add(myCam);   // you float over the call, unless pinned into it
        return up;
    }

    // the big one: what's pinned, else a shared screen (theirs first), else them
    Tile Main(List<Tile> up) => pinned != null && up.Contains(pinned) ? pinned : up[0];

    // lay the tiles out (only when which are up, the layout or the pin changed)
    void Arrange()
    {
        var up = Up();
        var main = Main(up);
        var mode = layout != "auto" ? layout : up.Count > 1 ? "sidebar" : "spotlight";
        var all = new[] { theirCam, theirScreen, myCam, myScreen };
        var narrow = area.ActualWidth is > 0 and < 760;
        var key = $"{mode}|{string.Join(',', up.Select(t => Array.IndexOf(all, t)))}|{Array.IndexOf(all, main)}|{narrow}|{portal.Sending == "screen"}|{selfMin}";
        foreach (var t in all)
        {
            t.Pin.ToolTip = pinned == t ? "Unpin" : "Pin";
            t.Pin.Content = pinned == t ? "" : "";
            Lit(t.Pin, pinned == t);
        }
        if (key == shape) return;
        shape = key;
        foreach (var t in new[] { theirCam, theirScreen, myCam, myScreen }) { if (t.Box.Parent is Panel p) p.Children.Remove(t.Box); t.Box.Width = t.Box.Height = double.NaN; t.Box.Margin = new Thickness(0); t.Box.Effect = null; t.Box.HorizontalAlignment = HorizontalAlignment.Stretch; t.Box.VerticalAlignment = VerticalAlignment.Stretch; t.Box.Visibility = Visibility.Visible; t.Box.Cursor = null; if (t != myCam) t.Box.RenderTransform = null; }
        if (selfPill?.Parent is Panel pp) pp.Children.Remove(selfPill);
        area.Children.Clear(); area.RowDefinitions.Clear(); area.ColumnDefinitions.Clear();
        var room = new Thickness(16, portal.Sending == "screen" ? 112 : 60, 16, 100);   // under the top line (and the presenting banner), over the bar
        switch (mode)
        {
            case "spotlight":   // only the big one
                main.Box.Margin = room; area.Children.Add(main.Box);
                break;
            case "tiled":   // all the same size, in a grid
            {
                var cols = (int)Math.Ceiling(Math.Sqrt(up.Count));
                var grid = new UniformGrid { Columns = cols, Margin = room };
                foreach (var t in up) grid.Children.Add(Cell(t.Box));
                area.Children.Add(grid);
                break;
            }
            case "sidebar" when narrow:   // a narrow window: the big one on top, the rest in a row under it
            {
                var grid = new Grid { Margin = room };
                grid.RowDefinitions.Add(new RowDefinition());
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
                main.Box.Margin = new Thickness(0, 0, 0, 10);
                grid.Children.Add(main.Box);
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var t in up.Where(t => t != main)) { t.Box.Width = 192; t.Box.Height = 108; t.Box.Margin = new Thickness(5, 0, 5, 0); row.Children.Add(t.Box); }
                SetRow(row, 1); grid.Children.Add(row);
                area.Children.Add(grid);
                break;
            }
            default:   // sidebar: the big one, the rest in a column on the right
            {
                var grid = new Grid { Margin = room };
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
                main.Box.Margin = new Thickness(0, 0, 12, 0);
                grid.Children.Add(main.Box);
                var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                foreach (var t in up.Where(t => t != main)) { t.Box.Width = 280; t.Box.Height = 158; t.Box.Margin = new Thickness(0, 0, 0, 12); column.Children.Add(t.Box); }
                SetColumn(column, 1); grid.Children.Add(column);
                area.Children.Add(grid);
                break;
            }
        }
        FloatSelf();
    }

    // you, floating: a 16:9 tile over the call, dragged anywhere; minimised, a small "You" pill in the corner
    bool selfMin;
    readonly TranslateTransform selfMove = new();
    Border? selfPill;
    void FloatSelf()
    {
        if (pinned == myCam) { myCam.Box.RenderTransform = null; myCam.Box.Cursor = null; return; }   // pinned: a tile in the layout like any other
        var b = myCam.Box;
        b.Width = 240; b.Height = 135; b.HorizontalAlignment = HorizontalAlignment.Right; b.VerticalAlignment = VerticalAlignment.Bottom; b.Margin = new Thickness(0, 0, 32, 110);
        b.RenderTransform = selfMove; b.Cursor = Cursors.SizeAll;
        b.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black };
        b.Visibility = selfMin ? Visibility.Collapsed : Visibility.Visible;
        area.Children.Add(b);
        selfPill ??= SelfPill();
        selfPill.Visibility = selfMin ? Visibility.Visible : Visibility.Collapsed;
        area.Children.Add(selfPill);
    }
    Border SelfPill()
    {
        var pill = new Border { CornerRadius = new CornerRadius(18), Background = B("#E61F2630"), BorderBrush = B("#407C5CFF"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 7, 14, 8), Cursor = Cursors.Hand,
                                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 32, 110), ToolTip = "Show your tile",
                                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                                    new TextBlock { Text = "\uE714", FontFamily = Icons, FontSize = 13, Foreground = B("#A78BFA"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                                    new TextBlock { Text = "You", FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center } } } };
        pill.MouseLeftButtonDown += (_, e) => { selfMin = false; shape = ""; Arrange(); e.Handled = true; };
        return pill;
    }
    // your tile's own controls: minimise (top left, on hover), and dragging it about
    void SelfControls()
    {
        var min = Round("\uE921", "Minimise your tile", B("#99000000"), () => { selfMin = true; shape = ""; Arrange(); });
        min.Width = min.Height = 34; min.FontSize = 12; min.Margin = new Thickness(8); min.HorizontalAlignment = HorizontalAlignment.Left; min.VerticalAlignment = VerticalAlignment.Top; min.Opacity = 0;
        ((Grid)myCam.Box.Child).Children.Add(min);
        myCam.Box.MouseEnter += (_, _) => min.Opacity = 1; myCam.Box.MouseLeave += (_, _) => min.Opacity = 0;
        Point? from = null; Point start = default;
        myCam.Box.MouseLeftButtonDown += (_, e) => { if (pinned == myCam || e.OriginalSource is DependencyObject d && FindButton(d)) return; from = e.GetPosition(area); start = new Point(selfMove.X, selfMove.Y); myCam.Box.CaptureMouse(); e.Handled = true; };
        myCam.Box.MouseMove += (_, e) =>
        {
            if (from is not { } f) return;
            var now = e.GetPosition(area);
            // kept inside the call
            var right = 32.0; var bottom = 110.0;
            selfMove.X = Math.Clamp(start.X + now.X - f.X, -(area.ActualWidth - 240 - right), right - 4);
            selfMove.Y = Math.Clamp(start.Y + now.Y - f.Y, -(area.ActualHeight - 135 - bottom), bottom - 4);
        };
        myCam.Box.MouseLeftButtonUp += (_, _) => { from = null; myCam.Box.ReleaseMouseCapture(); };
        static bool FindButton(DependencyObject d) { for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : null) if (x is Button) return true; return false; }
    }

    // a grid cell holding one tile at 16:9, as big as fits, centred: never a tall thin strip
    static Grid Cell(Border box)
    {
        var cell = new Grid { Margin = new Thickness(6), Children = { box } };
        box.HorizontalAlignment = HorizontalAlignment.Center; box.VerticalAlignment = VerticalAlignment.Center;
        cell.SizeChanged += (_, _) => { var w = Math.Min(cell.ActualWidth, cell.ActualHeight * 16 / 9); box.Width = w; box.Height = w * 9 / 16; };
        return cell;
    }

    ContextMenu LayoutMenu()
    {
        var menu = new ContextMenu { PlacementTarget = layoutBtn, Placement = PlacementMode.Bottom };
        foreach (var (key, label) in new[] { ("auto", "Auto"), ("tiled", "Tiled"), ("spotlight", "Spotlight"), ("sidebar", "Sidebar") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = layout == key };
            item.Click += (_, _) => { layout = key; Arrange(); };
            menu.Items.Add(item);
        }
        if (pinned != null)
        {
            menu.Items.Add(new Separator());
            var unpin = new MenuItem { Header = "Unpin" };
            unpin.Click += (_, _) => { pinned = null; Arrange(); };
            menu.Items.Add(unpin);
        }
        return menu;
    }

    // ---------- controls ----------

    // clip an element to rounded corners (a Border's own corners don't clip what's inside it)
    static void RoundClip(FrameworkElement e, double r) => e.SizeChanged += (_, _) => e.Clip = new RectangleGeometry(new Rect(0, 0, e.ActualWidth, e.ActualHeight), r, r);

    static ControlTemplate Pill()
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(24));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        face.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));   // the button's own room round its words
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        return new ControlTemplate(typeof(Button)) { VisualTree = face };
    }

    public void FullScreenShown(bool on) { full.Content = on ? "" : ""; full.ToolTip = on ? "Leave full screen" : "Full screen"; }

    Button Small(string glyph, string tip, Action click)
    {
        var b = Round(glyph, tip, B("#33000000"), click);
        b.Width = b.Height = 38; b.FontSize = 14; b.Margin = new Thickness(4, 0, 0, 0);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true);   // in full screen they sit where the title bar's drag area is: a click is theirs, not a drag
        return b;
    }

    Button Round(string glyph, string tip, Brush fill, Action click)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(27));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        var b = new Button { Background = fill, Tag = fill, Content = glyph, FontFamily = Icons, FontSize = 20, Foreground = Brushes.White,
                             Width = 54, Height = 54, Margin = new Thickness(6, 0, 6, 0), ToolTip = tip, Cursor = Cursors.Hand, Focusable = false,
                             Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        b.MouseEnter += (_, _) => b.Opacity = 0.85; b.MouseLeave += (_, _) => b.Opacity = 1;
        b.Click += (_, _) => click();
        return b;
    }

    // the camera and the screen go on and off on their own; both on, they're two tiles on each side
    async Task Switch(string what)
    {
        await video(what);
        if (!portal.CameraOn) Clear(myCam);
        if (portal.Sending != "screen") Clear(myScreen);
        if (portal.Sending == "") myBmp = null;
        Refresh();
    }

    // a lit (violet) button is on: muted, the camera, sharing, pinned
    static void Lit(Button b, bool on) => b.Background = on ? B("#7C5CFF") : (Brush)b.Tag;

    void Refresh()
    {
        time.Text = Elapsed;
        mute.Content = portal.Muted ? "" : ""; mute.ToolTip = portal.Muted ? "Unmute" : "Mute";
        Lit(mute, portal.Muted); Lit(cam, portal.CameraOn); Lit(screen, portal.Sending == "screen");
        cam.Content = portal.CameraOn ? "" : "";
        cam.ToolTip = portal.CameraOn ? "Turn the camera off" : "Turn the camera on";
        screen.ToolTip = portal.Sending == "screen" ? "Stop presenting" : "Present your screen";
        presenting.Visibility = portal.Sending == "screen" ? Visibility.Visible : Visibility.Collapsed;
        theirCam.MicOff.Visibility = portal.TheirMuted ? Visibility.Visible : Visibility.Collapsed;
        myCam.MicOff.Visibility = portal.Muted ? Visibility.Visible : Visibility.Collapsed;
        // how your video is going: its size and frame rate on your tiles while it's on
        myCam.Name.Text = portal.CameraOn ? "You · camera on" : "You";
        Stat(myCam, portal.CameraOn && myCam.Live, myCamSize);
        Stat(myScreen, portal.Sending == "screen" && myScreen.Live, myScreenSize);
        if (portal.TheirKind != "screen") Clear(theirScreen);
        Arrange();
    }

    void Stat(Tile t, bool on, (int W, int H) size)
    {
        t.StatsChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        t.Stats.Text = $"{size.H}p · {myFps} fps";
        ((System.Windows.Shapes.Ellipse)((StackPanel)t.StatsChip.Child).Children[0]).Fill = myFps >= 12 ? B("#22C55E") : myFps >= 6 ? B("#F59E0B") : B("#F43F5E");   // smooth · choppy · struggling
        t.StatsChip.ToolTip = myFps >= 12 ? "Your video is going smoothly" : myFps >= 6 ? "Your video is a little choppy" : "Your video is struggling: a slow PC or connection";
    }

    // their picture: one camera, one screen, or both side by side (split into two tiles)
    void TheirFrame(byte[] bgr, int w, int h)
    {
        if (theirPending) return; theirPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            theirPending = false;
            var (s, c) = portal.TheirSplit;
            int sw = (int)Math.Round(w * s), ch = Math.Min(h, (int)Math.Round(h * c));
            if (s > 0 && sw > 0 && sw < w && ch > 0) { Show(theirScreen, Crop(bgr, w, 0, 0, sw, h), sw, h); Show(theirCam, Crop(bgr, w, sw, 0, w - sw, ch), w - sw, ch); }
            else if (portal.TheirKind == "screen") { Show(theirScreen, bgr, w, h); Clear(theirCam); }
            else Show(theirCam, bgr, w, h);
            Changed();
        });
    }

    // what you send, shown back to you the same way
    void MyFrame(byte[] bgr, int w, int h)
    {
        myFrames++;
        if (minePending) return; minePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            minePending = false;
            myBmp = Paint(myBmp, bgr, w, h);
            var (s, c) = portal.MySplit;
            int sw = (int)Math.Round(w * s), ch = Math.Min(h, (int)Math.Round(h * c));
            if (portal.Sending == "screen" && s > 0 && sw > 0 && sw < w && ch > 0)
            {
                Show(myScreen, Crop(bgr, w, 0, 0, sw, h), sw, h); myScreenSize = (sw, h);
                Show(myCam, Crop(bgr, w, sw, 0, w - sw, ch), w - sw, ch); myCamSize = (w - sw, ch);
            }
            else if (portal.Sending == "screen") { Show(myScreen, bgr, w, h); myScreenSize = (w, h); }
            else if (portal.Sending == "camera") { Show(myCam, bgr, w, h); myCamSize = (w, h); }
            Changed();
        });
    }

    // a frame only redraws the controls when a tile came or went (the once-a-second tick does the rest): video stays smooth
    string lastLive = "";
    void Changed()
    {
        var live = $"{theirCam.Live}{theirScreen.Live}{myCam.Live}{myScreen.Live}{portal.TheirKind}{portal.Sending}";
        if (live != lastLive) { lastLive = live; Refresh(); }
    }

    public void TheirVideoStopped() { Clear(theirCam); Clear(theirScreen); Refresh(); }

    // a part of a BGR picture, as its own
    internal static byte[] Crop(byte[] bgr, int w, int x0, int y0, int cw, int ch)
    {
        var o = new byte[cw * ch * 3];
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(bgr, ((y0 + y) * w + x0) * 3, o, y * cw * 3, cw * 3);
        return o;
    }

    internal static WriteableBitmap Paint(WriteableBitmap? bmp, byte[] bgr, int w, int h)
    {
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h) bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), bgr, w * 3, 0);
        return bmp;
    }
}
