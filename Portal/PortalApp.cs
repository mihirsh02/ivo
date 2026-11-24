using System.Text.Json;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ivo;

// Portal, standalone: one small window. Your name, then start or join with two codes, then chat, call, video and screen
// sharing. Talks to Ivo's Portal and to other Portal apps alike. Nothing is stored but your name.
public static class PortalApp
{
    [STAThread]
    public static void Main()
    {
        // one Ivo Portal: opening it again (while it's in the tray) brings the running one forward
        using var one = new Mutex(true, "IvoPortal-one", out var first);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "IvoPortal-wake");
        if (!first) { wake.Set(); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };   // closing the window can leave it in the tray
        // just installed, and an account from before is here: carry on with it, or start a new one (before anything loads it)
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ivo Portal");
        var nameFile = Path.Combine(data, "name.txt");
        AccountChoice.AskIfNeeded("Ivo Portal", data, File.Exists(nameFile) ? File.ReadAllText(nameFile).Trim() : "");
        var w = new PortalWindow();
        new Thread(() => { while (wake.WaitOne()) app.Dispatcher.BeginInvoke(w.Bring); }) { IsBackground = true }.Start();
        app.Dispatcher.BeginInvoke(ColorEmoji.Warm, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (Environment.GetCommandLineArgs().Contains("--tray")) app.Run(); else { w.Show(); app.Run(); }
    }
}

public class PortalWindow : Window
{
    // Ivo's dark look, in a handful of colours
    static readonly Brush Bg = B("#1E2730"), Card = B("#2B3641"), Field = B("#38434F"), Fg = B("#E6EDF3"), Dim = B("#A0AAB6"), Red = B("#F43F5E"), Mine = B("#3A2F7A");
    static readonly Brush Accent = Grad();
    // the soft set's swirl (material-symbols:cyclone-rounded), as in Portal's button in Ivo, on a 24-unit square
    const string Swirl = "M12 16q-1.65 0-2.825-1.175T8 12t1.175-2.825T12 8t2.825 1.175T16 12t-1.175 2.825T12 16m0-2q.825 0 1.413-.587T14 12t-.587-1.412T12 10t-1.412.588T10 12t.588 1.413T12 14m0 6q-2.85 0-5.087-.337t-3.788-.713q-.5-.125-.812-.488T2 17.6q0-.4.338-.638t.737-.112q1.05.275 1.863.438l1.312.262q-1.05-1.075-1.65-2.5T4 12q0-2.850.337-5.087t.713-3.788q.125-.5.488-.812T6.4 2q.4 0 .638.338t.112.737q-.3 1.05-.462 1.863T6.45 6.25Q7.525 5.2 8.950 4.6T12 4q2.85 0 5.088.337t3.787.713q.5.125.813.488T22 6.4q0 .4-.337.638t-.738.112q-1.05-.3-1.862-.462T17.75 6.45q1.05 1.075 1.65 2.5T20 12q0 2.85-.337 5.088t-.713 3.787q-.125.5-.488.813T17.6 22q-.4 0-.638-.337t-.112-.738q.275-1.05.438-1.862l.262-1.313q-1.075 1.05-2.5 1.65T12 20m4.25-3.75Q18 14.5 18 12t-1.75-4.25T12 6T7.75 7.75T6 12t1.75 4.25T12 18t4.25-1.75";
    static Brush Grad() { var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#8B7CFF"), (Color)ColorConverter.ConvertFromString("#4F46E5"), 0); g.Freeze(); return g; }
    static Brush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    static readonly string NameFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ivo Portal", "name.txt");
    readonly Border root = new() { Padding = new Thickness(20, 0, 20, 12) };
    Grid layer = new();
    Action? removeIncoming;   // takes the incoming call screen away   // 12 px under the last card, the same as between cards

    Portal? portal;
    string stage = "", code = "", call = "", callKind = "voice";
    bool weRang, delivered;
    // contacts: kept in a file next to your name; listened for while the app is open
    static readonly string ContactsFile = Path.Combine(Path.GetDirectoryName(NameFile)!, "contacts.json");
    List<PortalContact> contacts = PortalContacts.Load(ContactsFile);
    static readonly string BinFile = Path.Combine(Path.GetDirectoryName(NameFile)!, "contacts-deleted.json");
    readonly List<PortalContact> bin = PortalContacts.Load(BinFile);   // Recently deleted
    CancellationTokenSource? listening;
    (PortalContact Who, string Invite, string Kind, DateTime At)? incomingPortal;
    string ringOnOpen = "", answerKind = "", calling = "";
    (string Box, string Key)? offered;
    JsonObject? asked;
    PortalCallView? callView;
    // Ivo Chats: the same messenger as Ivo's, its history in a file next to your name
    static readonly string ChatsFile = Path.Combine(Path.GetDirectoryName(NameFile)!, "chats.json");
    static readonly string SettingsFile = Path.Combine(Path.GetDirectoryName(NameFile)!, "settings.json");
    bool showSettings;
    bool linked;   // using Ivo's account (its contacts come across on start)
    static bool background = true;   // closing the window keeps it running in the tray
    bool quitting;
    PortalTray? tray;

    // the tray icon: click to open; right-click for Open and Quit
    void MakeTray()
    {
        tray = new PortalTray("Ivo Portal");
        tray.Left += Bring; tray.Right += TrayMenu;
    }

    // the tray's menu, in the app's own look: a dark rounded card by the pointer; it goes when you click elsewhere
    void TrayMenu()
    {
        var pos = PortalTray.Pointer();
        var list = new StackPanel { Margin = new Thickness(6) };
        Window? menu = null;
        var shut = false;
        void Shut() { if (shut) return; shut = true; menu!.Close(); }   // once: a click closes it, and losing focus as it closes would close it again
        void Item(string glyph, string text, Action go, Brush? color = null)
        {
            var ic = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = color ?? Dim, Width = 26, VerticalAlignment = VerticalAlignment.Center };
            var label = T(text, 13.5, color); label.VerticalAlignment = VerticalAlignment.Center;
            var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 18, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                                   Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { ic, label } } };
            row.MouseEnter += (_, _) => row.Background = Field; row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { Shut(); go(); };
            list.Children.Add(row);
        }
        Item("\uE8A7", "Open Ivo Portal", Bring);
        Item("\uE713", "Settings", () => { Bring(); showSettings = true; Draw(); });
        list.Children.Add(new Border { Height = 1, Background = Field, Margin = new Thickness(8, 4, 8, 4) });
        Item("\uE7E8", "Quit", Quit, B("#F87171"));
        menu = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = FontFamily, Content = new Border { Background = Bg, BorderBrush = B("#33FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(10), Child = list },
        };
        menu.Deactivated += (_, _) => Shut();
        menu.Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(menu).DpiScaleX;
            menu.Left = pos.X / dpi - menu.ActualWidth + 10; menu.Top = pos.Y / dpi - menu.ActualHeight + 10;   // above and left of the pointer (the tray is at the bottom right)
            menu.Activate();
        };
        menu.Show();
    }

    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(nint process);
    // memory back to Windows when it's hidden, minimised, or just not in use for half a minute (never during a call):
    // the heap compacted and released, then the pages
    int trimId;
    void TrimSoon()
    {
        var hidden = !IsVisible || WindowState == WindowState.Minimized;
        if (!hidden && IsActive) return;
        var me = ++trimId;
        _ = Task.Delay(hidden ? 5000 : 30000).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (me != trimId || IsActive && IsVisible && WindowState != WindowState.Minimized || callView != null || groupView != null) return;   // never mid-call: a full collection would stall the video
            if (IsVisible && WindowState != WindowState.Minimized) Still(true);   // on screen but unused: a still picture of itself
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            EmptyWorkingSet(-1);
        }));
    }

    // on screen but unused: a still picture of itself (looks the same, nothing redraws); back to live on use
    object? liveContent;
    void Still(bool on)
    {
        if (on == (liveContent != null)) return;
        if (on)
        {
            if (Content is not FrameworkElement live || live.ActualWidth < 1) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(live.ActualWidth * dpi.DpiScaleX), (int)(live.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            shot.Render(live); shot.Freeze();
            liveContent = Content;
            var picture = new Image { Source = shot, Stretch = Stretch.Fill };
            picture.MouseDown += (_, _) => Still(false);
            Content = picture;
        }
        else { Content = liveContent; liveContent = null; }
    }

    public void Bring() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }

    void Quit()
    {
        quitting = true;
        End(tell: true); listening?.Cancel(); chats?.Stop();
        tray?.Dispose();
        Application.Current.Shutdown();
    }

    // start with Windows: a line in this user's Run key (in the tray, without a window)
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool StartsWithWindows
    {
        get { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Ivo Portal") != null; }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Ivo Portal", $"\"{Environment.ProcessPath}\" --tray"); else k.DeleteValue("Ivo Portal", false);
        }
    }

    // Ivo's sound settings (Settings → Voice → Audio → Sounds) apply here too: read from Ivo's settings file, again
    // whenever it changes. Without Ivo, every sound plays at full volume.
    static (DateTime At, System.Text.Json.Nodes.JsonNode? Node) ivoSounds;
    static int IvoSoundLevel(string kind)
    {
        try
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ivo", "settings.json");
            var at = File.GetLastWriteTimeUtc(f);
            if (at != ivoSounds.At) ivoSounds = (at, File.Exists(f) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)) : null);
        }
        catch { }
        var n = ivoSounds.Node;
        if (n == null) return 100;
        if ((bool?)n["AppSounds"] == false) return 0;
        return kind == "master" ? (int?)n["SoundVolume"] ?? 100 : (int?)n["SoundLevels"]?[kind] ?? 100;
    }

    static void LoadSettings()
    {
        Sounds.Level = k => PortalDevices.Silenced(k) ? 0 : IvoSoundLevel(k);
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile))!;
            PortalDevices.Camera = (string?)o["camera"]; PortalDevices.Mic = (int?)o["mic"] ?? -1; PortalDevices.Speaker = (int?)o["speaker"] ?? -1;
            if ((string?)o["downloads"] is { Length: > 0 } d) PortalDevices.Downloads = d;
            PortalDevices.MessageSound = (bool?)o["messageSound"] ?? true; PortalDevices.CallSound = (bool?)o["callSound"] ?? true; PortalDevices.ReadReceipts = (bool?)o["readReceipts"] ?? true;
            if (o["reactions"] is JsonArray r && r.Count == 6) PortalDevices.Reactions = [.. r.Select(x => (string?)x ?? "❤️")];
            background = (bool?)o["background"] ?? true;
        }
        catch { }
    }

    static void SaveSettings()
    {
        try { File.WriteAllText(SettingsFile, new JsonObject { ["camera"] = PortalDevices.Camera, ["mic"] = PortalDevices.Mic, ["speaker"] = PortalDevices.Speaker, ["downloads"] = PortalDevices.Downloads, ["messageSound"] = PortalDevices.MessageSound, ["callSound"] = PortalDevices.CallSound, ["readReceipts"] = PortalDevices.ReadReceipts,
            ["reactions"] = new JsonArray([.. PortalDevices.Reactions.Select(x => (JsonNode)x)]), ["linked"] = Current?.linked == true, ["background"] = background }.ToJsonString()); } catch { }
    }
    IvoChats? chats;
    Rect smallBounds;   // the window before a call made it big
    readonly List<(string Who, string Text, bool Ivo, bool Mine, string? ReplyFrom, string? ReplyText, long At)> log = [];
    (string Who, string Text, bool Ivo, bool Mine, string? ReplyFrom, string? ReplyText, long At)? replying;
    StackPanel? list; ScrollViewer? scroll;
    WriteableBitmap? theirs, mine;
    bool theirPending, minePending;

    string Me
    {
        get { try { return File.Exists(NameFile) ? File.ReadAllText(NameFile).Trim() : ""; } catch { return ""; } }
        set { try { Directory.CreateDirectory(Path.GetDirectoryName(NameFile)!); File.WriteAllText(NameFile, value.Trim()); } catch { } }
    }

    static PortalWindow? Current;   // the one window (settings are saved from statics)

    public PortalWindow()
    {
        Current = this;
        NotifyCard.AppName = "Ivo Portal";   // the cards say where they came from
        Title = "Ivo Portal";
        // a tall, narrow window, like a messenger: 480 wide, most of the screen's height (600 to 860)
        Width = 480; Height = Math.Clamp(SystemParameters.WorkArea.Height * 0.85, 600, 860); Foreground = Fg;
        // a normal window (not a transparent one, which loses ClearType), with no title bar of Windows' and Windows 11's own rounded corners
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanMinimize; Background = Bg;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 14; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE: round
            var dark = 1; DwmSetWindowAttribute(h, 20, ref dark, 4);
            var none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(h, 34, ref none, 4);   // DWMWA_BORDER_COLOR: none (no light outline round the edge)
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;   // at 150% and up, smooth (Ideal) text reads better than pixel-snapped
            if (dpi >= 1.5) { TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal); TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale); }
        };

        // the header: the swirl and the name on the left; minimise and close on the right; drag anywhere on it to move
        var logo = new System.Windows.Shapes.Path { Data = Geometry.Parse(Swirl), Fill = B("#A78BFA"), Stretch = Stretch.Uniform, Width = 22, Height = 22, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = T("Ivo Portal", 17, bold: true); name.VerticalAlignment = VerticalAlignment.Center;
        var min = Corner("\uE921", () => WindowState = WindowState.Minimized, "Minimise");
        var close = Corner("\uE8BB", Close, "Close");
        // during a call the window resizes: drag its edges, or maximise it
        maxBtn = Corner("\uE922", ToggleMax, "Maximise");
        maxBtn.Visibility = Visibility.Collapsed;
        var head = new DockPanel { Margin = new Thickness(20, 12, 10, 10), Background = Brushes.Transparent };
        DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(maxBtn, Dock.Right); DockPanel.SetDock(min, Dock.Right);
        head.Children.Add(close); head.Children.Add(maxBtn); head.Children.Add(min);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { logo, name } });
        head.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && callView != null) ToggleMax(); else if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var body = new DockPanel(); DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        DockPanel.SetDock(corner, Dock.Bottom); body.Children.Add(corner);
        body.Children.Add(root);
        layer = new Grid { Children = { body } };   // the incoming call covers it all
        Content = layer;
        // closing: to the tray (it keeps getting messages and calls), unless that's turned off or it's Quit
        Closing += (_, e) =>
        {
            if (!quitting && background) { e.Cancel = true; Hide(); return; }
            if (!quitting) Quit();
        };
        // hidden to the tray or minimised (and not in a call), it gives its memory back to Windows after a few seconds
        IsVisibleChanged += (_, _) => { TrimSoon(); FloatCall(); }; StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) Still(false); TrimSoon(); FloatCall(); };
        Deactivated += (_, _) => TrimSoon();   // unused for half a minute: memory back, a still picture on screen
        Activated += (_, _) => { trimId++; Still(false); };
        MakeTray();
        LoadSettings();
        linked = Linked;
        RefreshFromIvo();
        Listen();
        StartChats();
        me.MouseLeftButtonUp += (_, _) => { if (callView != null) return; showSettings = false; tab = "chats"; Draw(); chatsView?.ShowMe(); };
        Draw();
    }

    // ---------- small building blocks ----------
    static TextBlock T(string text, double size = 14, Brush? brush = null, bool bold = false) =>
        new() { Text = text, FontSize = size, Foreground = brush ?? Fg, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    static Button Btn(string text, Brush? fill, Action click, Brush? fg = null)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
        border.SetValue(Border.BackgroundProperty, fill ?? Field);
        border.SetValue(Border.PaddingProperty, new Thickness(18, 12, 18, 13));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(content);
        var b = new Button { Content = text, Foreground = fg ?? Fg, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), Template = new ControlTemplate(typeof(Button)) { VisualTree = border } };
        b.MouseEnter += (_, _) => b.Opacity = 0.85; b.MouseLeave += (_, _) => b.Opacity = 1;
        b.Click += (_, _) => click();
        return b;
    }

    static Button Corner(string glyph, Action click, string tip)
    {
        var bg = new FrameworkElementFactory(typeof(Border), "Bd");
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bg.AppendChild(cp);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = bg };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, B("#22FFFFFF"), "Bd"));
        t.Triggers.Add(hover);
        var b = new Button { Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = Dim, Width = 32, Height = 32, Margin = new Thickness(2, 0, 0, 0),
                             Cursor = Cursors.Hand, ToolTip = tip, Template = t, Focusable = false };
        b.Click += (_, _) => click();
        return b;
    }

    static TextBox Box(bool wrap = false) => new()
    {
        Background = Field, Foreground = Fg, CaretBrush = Fg, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = B("#5A6572"), Padding = new Thickness(14, 12, 14, 12),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Center,
    };

    static Border Panel(UIElement child) => new() { Background = Card, CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(0, 0, 0, 12), Child = child };

    static StackPanel Stack(params UIElement[] items) { var s = new StackPanel(); foreach (var i in items) s.Children.Add(i); return s; }

    static Border Gap(double h) => new() { Height = h };

    FrameworkElement CodeBox(string text, string label)
    {
        // shown, not editable: no cursor, nothing to type into; Copy takes it whole
        var shown = T(text, 11.5, Dim); shown.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        var box = new Border { Background = Field, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = new ScrollViewer { Content = shown, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Button? copy = null;
        copy = Btn("Copy", Accent, () => { Clipboard.SetText(text); copy!.Content = "Copied"; });
        copy.HorizontalAlignment = HorizontalAlignment.Left; copy.Margin = new Thickness(0, 10, 0, 0);
        return Stack(T(label, 14, bold: true), Gap(8), box, copy);
    }

    // ---------- the screens ----------
    void Draw()
    {
        if (callView != null || groupView != null) return;   // the call has the window
        var page = new DockPanel();
        if (NoticeCard() is { } card) { DockPanel.SetDock(card, Dock.Top); page.Children.Add(card); }
        if (stage != "open" && tab == "session") tab = "chats";
        if (stage == "open" && tab == "new") tab = "session";
        var inSession = stage == "open" && tab == "session";   // in a session, it's a tab beside Chats and Contacts
        if (inSession && Me != "" && !showSettings) { var tabs = Tabs(); DockPanel.SetDock(tabs, Dock.Top); page.Children.Add(tabs); }
        if (inSession)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var end = Btn("End", Red, () => { End(tell: true); Draw(); }); end.Margin = new Thickness(0);
            var who = T($"Connected with {portal!.PeerName}", 15, bold: true); who.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(end, Dock.Right); head.Children.Add(end); head.Children.Add(who);
            DockPanel.SetDock(head, Dock.Top); page.Children.Add(head);
            if (ContactPrompt() is { } prompt) { DockPanel.SetDock(prompt, Dock.Top); page.Children.Add(prompt); }
        }
        page.Children.Add(Me == "" ? Welcome() : showSettings ? Settings() : inSession ? Session() : Start());
        root.Child = page;
    }

    UIElement Start()
    {
        var note = T("", 13, Red);
        if (stage == "inviting")
        {
            var reply = Box(true); reply.MinHeight = 60;
            var go = Btn("Connect", Accent, () => { try { portal!.Accept(reply.Text); note.Text = "Connecting…"; note.Foreground = Dim; } catch (Exception ex) { note.Text = ex.Message; } });
            go.HorizontalAlignment = HorizontalAlignment.Left; go.Margin = new Thickness(0, 10, 0, 0);
            var byHand = Stack(Gap(14), T("Paste the reply code they sent", 14, bold: true), Gap(8), reply, go); byHand.Visibility = Visibility.Collapsed;
            var show = Btn("They sent a reply code?", null, () => { }); show.HorizontalAlignment = HorizontalAlignment.Left; show.Margin = new Thickness(0, 10, 0, 0);
            show.Click += (_, _) => { byHand.Visibility = Visibility.Visible; show.Visibility = Visibility.Collapsed; reply.Focus(); };
            return Stack(Panel(Stack(CodeBox(code, "Send this invite"), Gap(14), T("Waiting for them to join… you'll be connected as soon as they paste it.", 13, Dim), show, byHand, note)), Btn("Cancel", null, () => { End(); Draw(); }));
        }
        if (stage == "calling")
            return Stack(Panel(Stack(T($"Calling {calling}…", 16, bold: true), Gap(6), T("Their Ivo or Ivo Portal rings if it's open. You're connected as soon as they accept.", 13, Dim))), Btn("Cancel", null, () => { End(); Draw(); }));
        if (stage == "joining")
            return Stack(Panel(delivered
                ? Stack(T($"Connecting to {portal?.PeerName}…", 16, bold: true), Gap(6), T("This takes a few seconds.", 13, Dim))
                : Stack(CodeBox(code, $"Send this reply to {portal?.PeerName}"), Gap(10), T("Their side couldn't be reached directly, so send this back to them.", 13, Dim))), Btn("Cancel", null, () => { End(); Draw(); }));

        var name = Box(); name.Text = Me;
        var invite = Box();   // one line: the code is long, but it's pasted, not read
        Button? start = null, join = null;
        start = Btn("Create invite", Accent, async () =>
        {
            if (Me == "") return;
            start!.IsEnabled = false; note.Text = "Finding the way to you…"; note.Foreground = Dim;
            try { New(); code = await portal!.Invite(Me); stage = "inviting"; Draw(); }
            catch (Exception ex) { start.IsEnabled = true; note.Text = ex.Message; note.Foreground = Red; }
        });
        join = Btn("Join", Accent, async () =>
        {
            if (Me == "") return;
            join!.IsEnabled = false;
            try { New(); (code, delivered) = await portal!.Join(invite.Text, Me); stage = "joining"; Draw(); }
            catch (Exception ex) { join.IsEnabled = true; note.Text = ex.Message; note.Foreground = Red; End(); }
        });
        // compact buttons, beside their words: the page fits the window without scrolling
        start.Margin = new Thickness(12, 0, 0, 0); join.Margin = new Thickness(8, 0, 0, 0); start.VerticalAlignment = join.VerticalAlignment = VerticalAlignment.Center;
        var openRow = new DockPanel(); DockPanel.SetDock(start, Dock.Right); openRow.Children.Add(start);
        openRow.Children.Add(Stack(T("Open a portal", 16, bold: true), Gap(4), T("Send an invite; you're in as soon as they join.", 13, Dim)));
        var a = Panel(openRow);
        var joinRow = new DockPanel(); DockPanel.SetDock(join, Dock.Right); joinRow.Children.Add(join); joinRow.Children.Add(Hint(invite, "Paste their invite"));
        var b = Panel(Stack(T("Join a portal", 16, bold: true), Gap(10), joinRow));
        // the top (a call coming in, the tabs), then the tab's page filling what's left
        var page = new DockPanel();
        foreach (var top in new[] { IncomingPortal(), Tabs() }) if (top != null) { DockPanel.SetDock(top, Dock.Top); page.Children.Add(top); }
        if (tab == "chats") page.Children.Add(ChatsTab());
        else if (tab == "contacts") page.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = ContactList() ?? Panel(Stack(T("No contacts yet", 16, bold: true), Gap(6), T("Connect with someone in New portal, then Add to contacts: after that you can chat and call any time, with no codes.", 13, Dim))) });
        else { var rest = Stack(a, b, note); rest.VerticalAlignment = VerticalAlignment.Top; page.Children.Add(rest); }
        return page;
    }

    string tab = "chats";   // session · chats · contacts · new
    IvoChatsView? chatsView;

    // you, in the window's bottom corner: your picture, name and status; click for your picture and status
    readonly Border corner = new() { Margin = new Thickness(20, 0, 20, 16), Padding = new Thickness(10, 8, 6, 8), CornerRadius = new CornerRadius(16) };
    readonly Border me = new() { Cursor = Cursors.Hand, Background = Brushes.Transparent };

    void DrawCorner()
    {
        if (chats == null) { corner.Visibility = Visibility.Collapsed; return; }
        corner.Visibility = Visibility.Visible;
        var st = IvoChatsView.StatusList.FirstOrDefault(s => s.Id == chats.Book.Status);
        FrameworkElement face = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Accent,
                                             Child = new TextBlock { Text = Me.Length > 0 ? Me[..1].ToUpper() : "?", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (chats.Book.Pic is { Length: > 0 } pic)
            try
            {
                var img = new BitmapImage(); img.BeginInit(); img.StreamSource = new MemoryStream(Convert.FromBase64String(pic)); img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit();
                face = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill } };
                RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.HighQuality);
            }
            catch { }
        var dot = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(6), Background = B(st.Color ?? "#3FB950"), BorderBrush = Bg, BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var who = Stack(T(Me, 13.5, bold: true), T(st.Name ?? "Active", 12, Dim)); who.Margin = new Thickness(10, 0, 0, 0); who.VerticalAlignment = VerticalAlignment.Center;
        corner.Background = Card;
        me.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Grid { Width = 34, Height = 34, Children = { face, dot } }, who } };
        me.ToolTip = "Your picture, name and status";
        var gear = Corner("\uE713", () => { showSettings = !showSettings; Draw(); }, "Settings: calls and files");
        gear.FontSize = 15; gear.Width = gear.Height = 36; gear.VerticalAlignment = VerticalAlignment.Center;
        (me.Parent as Panel)?.Children.Remove(me);
        var row = new DockPanel(); DockPanel.SetDock(gear, Dock.Right); row.Children.Add(gear); row.Children.Add(me);
        corner.Child = row;
    }

    void StartChats()
    {
        if (Me == "") return;   // a name first: it's who you are in chats
        IvoChats.App = "Ivo Portal";   // what your contacts see you use
        chats ??= new IvoChats(File.Exists(ChatsFile) ? File.ReadAllText(ChatsFile) : "", text => { try { File.WriteAllText(ChatsFile, text); } catch { } });
        chats.Changed -= ChatArrived; chats.Changed += ChatArrived;
        chats.CallSignal -= GroupSignal; chats.CallSignal += GroupSignal;
        chats.TheyreBusy -= TheyreBusy; chats.TheyreBusy += TheyreBusy;
        chats.Merged -= MergedInto; chats.Merged += MergedInto;
        chats.Unfriended -= Unfriended; chats.Unfriended += Unfriended;
        _ = EmptyBin();
        binTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromHours(6), System.Windows.Threading.DispatcherPriority.Background, (_, _) => _ = EmptyBin(), Dispatcher);   // it may run for days in the tray
        chats.Sync(contacts, Me);
        DrawCorner();
    }

    void ChatArrived(ChatThread t, ChatMsg? m) => Dispatcher.BeginInvoke(() =>
    {
        DrawCorner();   // your picture or status may have changed
        if (m == null) return;
        // a notification unless you're looking at this very conversation (in front, Chats open, that chat open)
        var looking = IsActive && IsVisible && WindowState != WindowState.Minimized && callView == null && tab == "chats" && chatsView?.Showing == t || IsActive && withChat?.Showing == t;
        if (!looking)
        {
            Flash();
            // Windows' own notification: it shows in the notification area too; a click brings the chat up
            Sounds.Chime("message");   // Ivo's chime
            NotifyCard.Show(t.Kind == "group" ? $"{m.FromName} · {t.Name}" : m.FromName, m.Kind switch { "file" => "📎 " + m.File, _ => m.Text }, "\uE8BD", "#A78BFA",
                        () => { Bring(); tab = "chats"; Draw(); chatsView?.OpenThread(t); }, m.FromName, t.Members.FirstOrDefault(x => x.Id == m.From)?.Pic);
        }
        if (tab != "chats" && stage != "open") Draw();   // the tab's count goes up
    });

    FrameworkElement ChatsTab()
    {
        StartChats();
        if (chats == null) return Panel(Stack(T("Add your name first", 16, bold: true), Gap(6), T("It's who you are in chats. Set it on New portal.", 13, Dim)));
        var look = new ChatLook(Fg, Dim, Card, Field, Accent, B("#553A2F7A"));
        var view = chatsView = new IvoChatsView(chats, look, null, (t, kind) => { if (t.Kind == "group") _ = EnterGroupCall(t, Guid.NewGuid().ToString("N")[..10], kind, start: true); else if (contacts.FirstOrDefault(c => c.Box == t.ContactBox) is { } c) _ = CallContact(c, kind); });
        view.Rename = n => { Me = n; chats.Book.MyName = n; _ = chats.SetProfile(null, null); DrawCorner(); };   // your contacts see the new name
        return new Border { Background = Card, CornerRadius = new CornerRadius(16), Padding = new Thickness(16), Child = view };
    }

    // New | Contacts, as one segmented control
    FrameworkElement Tabs()
    {
        var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };   // two equal halves across the whole width
        var unread = chats?.UnreadChats ?? 0;   // chats with something new, not messages
        var chatsTab = ("chats", unread > 0 ? $"Chats  {unread}" : "Chats");
        var all = stage == "open" && portal != null
            ? new[] { ("session", call == "on" ? $"{portal.PeerName} · in call" : portal.PeerName), chatsTab, ("contacts", "Contacts") }
            : new[] { chatsTab, ("contacts", "Contacts"), ("new", "New portal") };
        foreach (var (id, label) in all)
        {
            var on = tab == id;
            var b = Btn(label, on ? Accent : Brushes.Transparent, () => { tab = id; Draw(); }, on ? Fg : Dim);
            b.Margin = new Thickness(0); b.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.Children.Add(b);
        }
        return new Border { Background = Card, CornerRadius = new CornerRadius(18), Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 12), Child = row };
    }

    // a box with its hint inside, faint, until something is typed
    static Grid Hint(TextBox box, string hint)
    {
        var t = T(hint, 14, Dim); t.IsHitTestVisible = false; t.Margin = new Thickness(15, 0, 12, 0); t.VerticalAlignment = VerticalAlignment.Center;
        // a box that wraps onto several lines starts at the top: the hint goes exactly where the caret starts
        if (box.TextWrapping == TextWrapping.Wrap)
        {
            t.VerticalAlignment = VerticalAlignment.Top;
            box.Loaded += (_, _) => { var at = box.GetRectFromCharacterIndex(0); if (!at.IsEmpty) t.Margin = new Thickness(at.X + 3, at.Y, 12, 0); };
        }
        void Show() => t.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Show(); Show();
        return new Grid { Children = { box, t } };
    }

    FrameworkElement Settings() => new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,   // scrolls when the window is short; no bar
        Content = new StackPanel { Children = { new PortalSettingsView(new ChatLook(Fg, Dim, Card, Field, Accent, B("#553A2F7A")), SaveSettings, () => { showSettings = false; Draw(); },
                                                                       (() => bin, RestoreContact)), AppSettings(), Credit() } } };

    // ---------- Recently deleted contacts ----------
    // removed: into the bin (their calls stop, their chat hides); restored: back as before; after 15 days: gone for both
    void RemoveContact(PortalContact c)
    {
        contacts.Remove(c); c.Removed = DateTime.Now; bin.Add(c);
        PortalContacts.Save(ContactsFile, contacts); PortalContacts.Save(BinFile, bin);
        chats?.Bin(c.Box, true); Listen(); Draw();
    }
    void RestoreContact(PortalContact c)
    {
        bin.Remove(c); c.Removed = null; contacts.Add(c);
        PortalContacts.Save(ContactsFile, contacts); PortalContacts.Save(BinFile, bin);
        chats?.Bin(c.Box, false); Listen(); Draw();
    }
    System.Windows.Threading.DispatcherTimer? binTimer;
    async Task EmptyBin()
    {
        foreach (var c in bin.Where(c => c.Expired).ToList())
        {
            if (chats != null) await chats.Unfriend(c.Box);
            bin.Remove(c);
        }
        PortalContacts.Save(BinFile, bin);
    }
    // they deleted us for good: the contact goes here too
    void Unfriended(string box) => Dispatcher.BeginInvoke(() =>
    {
        contacts.RemoveAll(c => c.Box == box); bin.RemoveAll(c => c.Box == box);
        PortalContacts.Save(ContactsFile, contacts); PortalContacts.Save(BinFile, bin);
        Listen(); Draw();
    });

    // the app itself: running in the background when closed, and starting with Windows
    FrameworkElement AppSettings()
    {
        var list = Stack(T("App", 14, bold: true), Gap(10),
            Switch("Keep running when closed", "Closing the window keeps Ivo Portal in the tray, so messages and calls still reach you. Quit from the tray icon.", background, on => { background = on; SaveSettings(); }),
            Gap(12),
            Switch("Start with Windows", "Opens quietly in the tray when you sign in.", StartsWithWindows, on => { try { StartsWithWindows = on; } catch { } }));
        var card = Panel(list); card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    // a setting: its name and what it does on the left, a sliding switch on the right
    FrameworkElement Switch(string name, string about, bool on, Action<bool> changed)
    {
        var knob = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 3, 0) };
        var track = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = knob };
        void Paint() { track.Background = on ? Accent : Field; knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        Paint();
        track.MouseLeftButtonUp += (_, _) => { on = !on; Paint(); changed(on); };
        var words = Stack(T(name, 13.5), T(about, 12, Dim)); words.Margin = new Thickness(0, 0, 16, 0);
        var row = new DockPanel(); DockPanel.SetDock(track, Dock.Right); row.Children.Add(track); row.Children.Add(words);
        return row;
    }

    // who made it, with a link to his site
    FrameworkElement Credit()
    {
        var made = new TextBlock { FontSize = 13, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center };
        made.Inlines.Add("Made by "); made.Inlines.Add(new System.Windows.Documents.Run("Mihir Sharma") { FontWeight = FontWeights.SemiBold, Foreground = Fg }); made.Inlines.Add(" ↗");
        var link = new Border { Child = made, Cursor = Cursors.Hand, Background = Brushes.Transparent, Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 6), ToolTip = "mihirsh02.github.io" };
        link.MouseEnter += (_, _) => link.Opacity = 0.8; link.MouseLeave += (_, _) => link.Opacity = 1;
        link.MouseLeftButtonUp += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://mihirsh02.github.io/") { UseShellExecute = true });
        return link;
    }   // set at the welcome; changed in your profile

    // Ivo is on this PC: use its account, or make a new one
    FrameworkElement IvoChoice(IvoAccount a)
    {
        var swirl = new System.Windows.Shapes.Path { Data = Geometry.Parse(Swirl), Fill = B("#A78BFA"), Stretch = Stretch.Uniform, Width = 52, Height = 52, HorizontalAlignment = HorizontalAlignment.Center };
        var title = T("Ivo is on this PC", 22, bold: true); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 12, 0, 6);
        var line = T(a.Sealed ? "Its account is locked with Ivo's app lock, so Ivo Portal can't use it. Make an account for Ivo Portal, or unlock Ivo and open this again."
                              : "Use the same account as Ivo, or make a separate one for Ivo Portal.", 13.5, Dim);
        line.TextAlignment = TextAlignment.Center; line.Margin = new Thickness(10, 0, 10, 16);
        var page = Stack(swirl, title, line);
        if (!a.Sealed)
        {
            var face = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Accent, Margin = new Thickness(0, 0, 14, 0),
                                    Child = new TextBlock { Text = a.Name.Length > 0 ? a.Name[..1].ToUpper() : "?", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var use = Btn("Use this account", Accent, () => UseIvo(a)); use.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel(); DockPanel.SetDock(face, Dock.Left); DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(face); row.Children.Add(use);
            row.Children.Add(Stack(T(a.Name != "" ? $"{a.Name} · Ivo" : "Your Ivo account", 15, bold: true), T($"{a.Contacts.Count} contact{(a.Contacts.Count == 1 ? "" : "s")}, your chats and picture; messages show in both apps", 12.5, Dim)));
            page.Children.Add(Panel(row));
        }
        var fresh = Btn("Create a new account", null, () => { welcomeNew = true; Draw(); }); fresh.HorizontalAlignment = HorizontalAlignment.Stretch;
        page.Children.Add(Panel(Stack(T("A separate account", 15, bold: true), Gap(4), T("Its own name, contacts and chats, apart from Ivo's.", 12.5, Dim), Gap(12), fresh)));
        return page;
    }

    // ---------- the welcome, on first launch: what Portal is, your name and (if you like) a picture ----------
    string welcomePic = "";
    bool welcomeNew;   // chose a new account over Ivo's

    // ---------- Ivo on this PC: its account (name, contacts, chats), when it can be read (not sealed by Ivo's app lock) ----------
    static readonly string IvoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ivo");
    record IvoAccount(string Name, List<PortalContact> Contacts, string Chats, bool Sealed);

    static IvoAccount? FindIvo()
    {
        try
        {
            var file = Path.Combine(IvoDir, "settings.json");
            if (!File.Exists(file)) return null;
            var s = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            if ((string?)s["Vault"] is { Length: > 0 }) return new("", [], "", true);   // the app lock is on: sealed
            var name = ((string?)s["Profile"]?["Name"] ?? "").Split(' ')[0];
            var contacts = s["PortalContacts"]?.Deserialize<List<PortalContact>>() ?? [];
            var chatsFile = Path.Combine(IvoDir, "chats.json");
            var chats = File.Exists(chatsFile) ? File.ReadAllText(chatsFile) : "";
            if (!chats.TrimStart().StartsWith('{')) chats = "";   // sealed or missing: the identity comes from it, so a new one is made
            if (name == "" && chats == "") return null;
            return new(name, contacts, chats, false);
        }
        catch { return null; }
    }

    static bool Linked { get { try { return File.Exists(SettingsFile) && (bool?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile))?["linked"] == true; } catch { return false; } } }

    // use Ivo's account here: the same you (identity, name, picture), contacts and chats; messages show in both
    void UseIvo(IvoAccount a)
    {
        Me = a.Name != "" ? a.Name : "Me";
        contacts = a.Contacts; PortalContacts.Save(ContactsFile, contacts);
        if (a.Chats != "") File.WriteAllText(ChatsFile, a.Chats);
        chats = null;
        linked = true; SaveSettings();
        Listen(); StartChats(); Draw();
    }

    // linked: Ivo's newer contacts come across each time Ivo Portal starts
    void RefreshFromIvo()
    {
        if (!linked || FindIvo() is not { Sealed: false } a) return;
        foreach (var c in a.Contacts.Where(c => !contacts.Any(x => x.Box == c.Box))) contacts.Add(c);
        PortalContacts.Save(ContactsFile, contacts);
    }

    FrameworkElement Welcome()
    {
        if (!welcomeNew && FindIvo() is { } ivo) return IvoChoice(ivo);
        var swirl = new System.Windows.Shapes.Path { Data = Geometry.Parse(Swirl), Fill = B("#A78BFA"), Stretch = Stretch.Uniform, Width = 52, Height = 52, HorizontalAlignment = HorizontalAlignment.Center };
        var title = T("Welcome to Ivo Portal", 22, bold: true); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 12, 0, 6);
        var line = T("Chat, call, video call and share your screen with anyone on Ivo or Ivo Portal: directly, encrypted end to end, no account.", 13.5, Dim);
        line.TextAlignment = TextAlignment.Center; line.Margin = new Thickness(10, 0, 10, 16);

        // you: a picture (optional) and your name
        var face = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Accent, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Add a picture (optional)",
                                Child = new TextBlock { Text = "\uE722", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 26, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        void ShowPic()
        {
            if (welcomePic == "") return;
            var img = new BitmapImage(); img.BeginInit(); img.StreamSource = new MemoryStream(Convert.FromBase64String(welcomePic)); img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit();
            face.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; face.Child = null;
        }
        ShowPic();
        face.MouseLeftButtonUp += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Your picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
            if (dlg.ShowDialog(this) != true) return;
            try { if (PictureCropper.Crop(dlg.FileName, this) is { } cropped) { welcomePic = cropped; ShowPic(); } } catch { }   // you crop it
        };
        var picNote = T("Add a picture (optional)", 12, Dim); picNote.HorizontalAlignment = HorizontalAlignment.Center; picNote.Margin = new Thickness(0, 6, 0, 12);
        var name = Box(); name.FontSize = 15;
        var note = T("", 13, Red); note.Margin = new Thickness(0, 6, 0, 0);
        var go = Btn("Get started", Accent, () =>
        {
            if (name.Text.Trim() == "") { note.Text = "Your name, so they know it's you."; name.Focus(); return; }
            Me = name.Text.Trim();
            StartChats();
            if (welcomePic != "") _ = chats!.SetProfile(welcomePic, null);
            Draw();
        });
        go.HorizontalAlignment = HorizontalAlignment.Stretch; go.Margin = new Thickness(0, 14, 0, 0);
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; go.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); } };

        // how it works, in three lines
        var how = Stack();
        foreach (var (glyph, text) in new[] { ("\uE8C8", "Send an invite by any app; they paste it and you're connected."), ("\uE77B", "Add each other to contacts: next time, just call or chat."), ("\uE72E", "Everything goes straight between you, sealed end to end.") })
        {
            var ic = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = B("#A78BFA"), Width = 26, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(ic, Dock.Left); row.Children.Add(ic); row.Children.Add(T(text, 13, Dim));
            how.Children.Add(row);
        }
        Dispatcher.BeginInvoke(() => name.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return Stack(swirl, title, line, Panel(Stack(face, picNote, Hint(name, "Your name"), note, go)), Gap(4), how);
    }

    UIElement Session()
    {
        var page = new DockPanel();
        var bar = CallBar(); DockPanel.SetDock(bar, Dock.Top); page.Children.Add(bar);
        page.Children.Add(ChatPane());
        return page;
    }

    // the chat with them: messages (only they scroll), the message bar under them
    PortalContact? with;      // the contact this session is with (none: someone who isn't saved)
    IvoChatsView? withChat;   // that contact's own conversation, while the session shows it

    // the session's chat: with a contact, your usual conversation with them (kept, the same as in Chats);
    // with someone who isn't saved, a fresh one that lasts as long as the session
    FrameworkElement ChatPane()
    {
        if (with is { } c && chats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box) is { } thread)
        {
            var view = withChat = new IvoChatsView(chats, new ChatLook(Fg, Dim, Card, Field, Accent, B("#553A2F7A")), null, null);   // that conversation first; back goes to all your chats, call or not
            view.OpenThread(thread);
            return view;
        }
        var page = new DockPanel();
        var input = Box();
        void Post()
        {
            var text = input.Text.Trim(); if (text == "") return;
            input.Clear();
            var reply = replying; replying = null;
            Say(Me, text, false, true, reply?.ReplyFrom, reply?.ReplyText);
            if (reply != null) Draw();
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Post(); } };
        var send = Btn("Send", Accent, Post); send.Margin = new Thickness(8, 0, 0, 0);
        var row = new DockPanel(); DockPanel.SetDock(send, Dock.Right); row.Children.Add(send); row.Children.Add(input);
        DockPanel.SetDock(row, Dock.Bottom); page.Children.Add(row);
        if (replying is { } to)
        {
            var cancel = Btn("×", null, () => { replying = null; Draw(); }, Dim); cancel.Width = 32; cancel.Padding = new Thickness(0);
            DockPanel.SetDock(cancel, Dock.Right);
            var quote = new StackPanel { Children = { T($"Replying to {to.ReplyFrom}", 11.5, Accent, true), T(IvoChatsView.WrapLongWords(to.ReplyText ?? ""), 12, Dim) } };
            page.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6),
                                           Child = new DockPanel { Children = { cancel, quote } } });
        }

        list = new StackPanel();
        foreach (var m in log) list.Children.Add(Bubble(m));
        scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 12, 0, 12) };   // scrolls without a bar, like every chat
        page.Children.Add(scroll);
        scroll.ScrollToEnd();
        Dispatcher.BeginInvoke(() => input.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return page;
    }

    FrameworkElement CallBar()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };   // the same gap below whatever it shows
        switch (call)
        {
            case "":
                row.Children.Add(IvoChatsView.Round("", "Call", "#34D399", () => Ring("voice"), Fluent));
                row.Children.Add(IvoChatsView.Round("", "Video call", "#60A5FA", () => Ring("video"), Fluent));
                row.Children.Add(IvoChatsView.Round("", "Share your screen", "#22D3EE", () => Ring("screen"), Fluent));
                row.Margin = new Thickness(-6, 0, 0, 14);   // round buttons: their own inset, lined up with the text
                break;
            case "calling":
                var callingText = T($"Ringing {portal!.PeerName}… waiting for them to pick up", 14); callingText.VerticalAlignment = VerticalAlignment.Center; callingText.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(callingText);
                row.Children.Add(Btn("Cancel", Red, () => HangUp(true)));
                break;
            case "ringing":
                var ringingText = T($"{portal!.PeerName} is calling{(callKind == "video" ? " (video)" : callKind == "screen" ? " to share their screen" : "")}", 14); ringingText.VerticalAlignment = VerticalAlignment.Center; ringingText.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(ringingText);
                var accept = Btn("Accept", Accent, () => ShowCall(lobby: true)); accept.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(accept);
                row.Children.Add(Btn("Decline", Red, () => HangUp(true)));
                break;
            case "on":
                row.Children.Add(IvoChatsView.Round("", "Show the call", "#A78BFA", () => ShowCall(), Fluent));
                row.Children.Add(IvoChatsView.Round("", "Hang up", "#F43F5E", () => HangUp(true), Fluent));
                row.Margin = new Thickness(-6, 0, 0, 14);   // round buttons: their own inset, lined up with the text
                break;
        }
        return row;
    }

    FrameworkElement Bubble((string Who, string Text, bool Ivo, bool Mine, string? ReplyFrom, string? ReplyText, long At) m)
    {
        var name = T(m.Ivo ? $"Ivo · for {m.Who}" : m.Who, 11.5, m.Ivo ? Accent : Dim, true);
        var content = new StackPanel();
        content.Children.Add(name);
        var sent = DateTimeOffset.FromUnixTimeSeconds(m.At).LocalDateTime;
        var stamp = T(sent.Date == DateTime.Today ? sent.ToString("t") : sent.ToString("d MMM, t"), 11, Dim);
        stamp.Margin = new Thickness(0, 0, 0, 4); stamp.Visibility = Visibility.Collapsed;
        content.Children.Add(stamp);
        if (m.ReplyText is { Length: > 0 } replyText)
        {
            var quote = new StackPanel { Children = { T($"Reply to {m.ReplyFrom}", 11.5, Accent, true), T(IvoChatsView.WrapLongWords(replyText), 12, Dim) } };
            content.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), BorderBrush = Accent, BorderThickness = new Thickness(3, 0, 0, 0),
                                              CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 0, 0, 6), Child = quote });
        }
        content.Children.Add(T(IvoChatsView.WrapLongWords(m.Text), 14));
        var body = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 9, 14, 10), Margin = new Thickness(0, 0, 0, 8), MaxWidth = 440,
            Background = m.Mine ? Mine : Card, Child = content,
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        Button Action(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = Dim,
                                 Width = 28, Height = 28, Margin = new Thickness(1), Padding = new Thickness(0), ToolTip = tip, Cursor = Cursors.Hand,
                                 Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            b.Click += (_, _) => click();
            return b;
        }
        actions.Children.Add(Action("", "Reply", () =>
        {
            replying = m with { ReplyFrom = m.Ivo ? $"Ivo · for {m.Who}" : m.Who, ReplyText = m.Text.Length > 90 ? m.Text[..90] + "…" : m.Text };
            Draw();
        }));
        actions.Children.Add(Action("", "Copy", () => Clipboard.SetText(m.Text)));
        var row = new Grid { Margin = new Thickness(m.Mine ? 40 : 0, 0, m.Mine ? 0 : 40, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = m.Mine ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = m.Mine ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Grid.SetColumn(actions, m.Mine ? 0 : 1);
        Grid.SetColumn(body, m.Mine ? 1 : 0);
        actions.HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        body.HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        row.Children.Add(actions);
        row.Children.Add(body);
        row.MouseEnter += (_, _) => { actions.Opacity = 1; stamp.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { actions.Opacity = 0; stamp.Visibility = Visibility.Collapsed; };
        return row;
    }

    void Say(string who, string text, bool ivo, bool own, string? replyFrom = null, string? replyText = null, long? at = null)
    {
        var sentAt = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        log.Add((who, text, ivo, own, replyFrom, replyText, sentAt));
        list?.Children.Add(Bubble(log[^1]));
        scroll?.ScrollToEnd();
        if (own) portal?.Send(new JsonObject { ["t"] = "chat", ["n"] = who, ["x"] = text, ["rf"] = replyFrom, ["rt"] = replyText, ["at"] = sentAt });
    }

    // ---------- the connection ----------
    void New()
    {
        End();
        var k = portal = new Portal();
        // the window you shared was minimised or closed: sharing stops, they're told, and you see why
        k.SharingStopped += why => Dispatcher.BeginInvoke(() =>
        {
            if (portal != k) return;
            k.Send(new JsonObject { ["t"] = "video", ["on"] = k.Sending != "" });
            if (k.Sending == "") mine = null;
            Notice("Screen sharing stopped", why + ". Share it again once it's open.");
        });
        k.Opened += () => Dispatcher.BeginInvoke(() =>
        {
            if (portal != k) return;
            stage = "open"; tab = "session"; log.Clear(); k.Send(new JsonObject { ["t"] = "hello", ["n"] = Me, ["a"] = "Ivo Portal" });
            if (ringOnOpen != "") { var kind = ringOnOpen; ringOnOpen = ""; Ring(kind); }   // we called a contact: ring them now
            Draw(); Flash();
        });
        // it dropped: they left, or your own internet went (said so, not blamed on them)
        k.Closed += () => Dispatcher.BeginInvoke(async () => { if (portal == k && stage == "open") { var left = k.PeerName; End(); Draw(); if (await Portal.Online()) Notice($"{left} left", ""); else Notice("Your internet dropped", "The portal closed. It can open again once you're back online."); } });
        k.Message += o => Dispatcher.BeginInvoke(() => { if (portal == k) Received(o); });
        k.TheirFrame += (bgr, w, h) => { if (theirPending) return; theirPending = true; Dispatcher.BeginInvoke(() => { theirPending = false; var fresh = theirs == null; theirs = Paint(theirs, bgr, w, h); if (fresh) Draw(); }); };
        k.MyFrame += (bgr, w, h) => { if (minePending) return; minePending = true; Dispatcher.BeginInvoke(() => { minePending = false; var fresh = mine == null; mine = Paint(mine, bgr, w, h); if (fresh) Draw(); }); };
    }

    void End(bool tell = false)
    {
        Sounds.StopCalling(); removeIncoming?.Invoke(); removeIncoming = null;
        if (stage == "calling" && with is { } ringing && code != "") _ = PortalContacts.CancelRequest(ringing, code);
        if (portal != null && tell) portal.Send(new JsonObject { ["t"] = "bye" });
        portal?.Dispose(); portal = null;
        stage = ""; code = ""; call = ""; weRang = false; theirs = mine = null; list = null; scroll = null;
        ringOnOpen = answerKind = ""; offered = null; asked = null; with = null; withChat = null;
        CloseCall();
    }

    void Received(JsonObject o)
    {
        switch ((string?)o["t"])
        {
            case "chat":
                var text = (string?)o["x"] ?? "";
                Say(portal!.PeerName, text.Length > 20000 ? text[..20000] : text, (bool?)o["ivo"] == true, false, (string?)o["rf"], (string?)o["rt"], MessageTimestamp(o));
                if (!IsActive) Flash();
                break;
            case "ring":
                call = "ringing"; callKind = (string?)o["v"] ?? "voice"; weRang = false;
                if (answerKind != "") { answerKind = ""; portal!.Send(new JsonObject { ["t"] = "answer" }); _ = CallOn(); break; }
                Sounds.Ring(); if (!IsActive || WindowState == WindowState.Minimized || !IsVisible) NotifyCard.Show($"{portal!.PeerName} is calling", "Ivo Portal · call", "\uE717", "#A78BFA", Bring, portal.PeerName);
                Draw(); Flash();
                break;
            case "contact": asked = o; Draw(); break;
            case "contact-ok" when offered is { } mineOffer: SaveContact(portal!.PeerName, mineOffer.Box, mineOffer.Key); offered = null; Draw(); break;
            case "contact-no" when offered is { } back: contacts.RemoveAll(c => c.Box == back.Box); PortalContacts.Save(ContactsFile, contacts); Listen(); offered = null; Draw(); break;   // they said not now
            case "answer" when call == "calling":
                weRang = true; _ = CallOn();
                break;
            case "hangup":
                HangUp(false);
                break;
            case "video" when (bool?)o["on"] != true:
                theirs = null; callView?.TheirVideoStopped(); callFloat?.TheirVideoStopped(); Draw();
                break;
            case "merge":
                _ = JoinMerged((string?)o["g"] ?? "", (string?)o["call"] ?? "", (string?)o["v"] ?? "voice");
                break;
            case "bye":
                var left = portal!.PeerName; End(); Draw(); Notice($"{left} left", "");
                break;
        }
    }

    static long MessageTimestamp(JsonObject o)
    {
        return o["at"] is JsonValue value && value.TryGetValue<long>(out var at) && at is >= -62135596800 and <= 253402300799
            ? at : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    void Ring(string kind) { call = "calling"; callKind = kind; portal?.Send(new JsonObject { ["t"] = "ring", ["v"] = kind }); Sounds.Calling(); Draw(); }

    async Task CallOn()
    {
        Sounds.StopCalling();
        if (portal == null) return;
        call = "on";
        try
        {
            await portal.StartSound();
            if (callKind != "voice" && weRang) { if (callKind == "video") await portal.StartCamera(); else await Video("screen"); }
        }
        catch (Exception ex) { Notice("The call couldn't start", ex.Message); }
        ShowCall();
    }

    async Task Video(string what)
    {
        if (portal == null) return;
        try { if (what == "camera-off") await portal.StopCamera(); else if (what == "screen-off") await portal.StopScreen(); else if (what == "camera") await portal.StartCamera(); else if (what == "screen") { var pick = new ScreenPicker { Owner = this }; pick.ShowDialog(); if (pick.Picked is not { } chosen) return; await portal.StartScreen(chosen); } else { await portal.StopVideo(); mine = null; } }
        catch (Exception ex) { Notice(what == "camera" ? "The camera couldn't start" : "Couldn't share the screen", ex.Message); }
        portal.Send(new JsonObject { ["t"] = "video", ["on"] = portal.Sending != "" });
        Draw();
    }

    void HangUp(bool tell)
    {
        Sounds.StopRing(); Sounds.StopCalling();
        if (tell) portal?.Send(new JsonObject { ["t"] = "hangup" });
        call = ""; weRang = false; theirs = mine = null;
        CloseCall();
        _ = Finish();
        // a call with a contact was the session's only reason: it ends with the call (an invite's portal stays open)
        async Task Finish()
        {
            if (portal != null) await portal.EndCall();
            if (with != null && stage is "open" or "calling" or "inviting") End();
            Draw();
        }
    }

    // ---------- notices: a card at the top of the window, not a Windows box; it goes after a few seconds ----------
    (string Title, string Text)? notice;
    System.Windows.Threading.DispatcherTimer? noticeTimer;

    void Notice(string title, string text)
    {
        notice = (title, text);
        noticeTimer?.Stop();
        noticeTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(6), System.Windows.Threading.DispatcherPriority.Background, (_, _) => { noticeTimer!.Stop(); notice = null; Draw(); }, Dispatcher);
        noticeTimer.Start();
        Flash(); Draw();
    }

    FrameworkElement? NoticeCard()
    {
        if (notice is not { } n) return null;
        var x = Corner("\uE711", () => { notice = null; Draw(); }, "Dismiss");
        x.VerticalAlignment = VerticalAlignment.Top;
        var words = Stack(T(n.Title, 15, bold: true));
        if (n.Text != "") { words.Children.Add(Gap(4)); words.Children.Add(T(n.Text, 13, Dim)); }
        var row = new DockPanel(); DockPanel.SetDock(x, Dock.Right); row.Children.Add(x); row.Children.Add(words);
        var card = Panel(row); card.BorderBrush = B("#55F43F5E"); card.BorderThickness = new Thickness(1);
        return card;
    }

    // ---------- group calls (see Core/GroupCall.cs) ----------
    GroupCall? groupCall;
    GroupCallView? groupView;
    void GroupSignal(ChatThread t, JsonObject o) => Dispatcher.BeginInvoke(() =>
    {
        if ((string?)o["k"] != "ring" || groupCall != null) return;
        var id = (string?)o["call"] ?? ""; var kind = (string?)o["v"] ?? "voice";
        if (merging.Contains(id)) return;   // a merged call we're already joining
        var who = t.Members.FirstOrDefault(m => m.Id == (string?)o["from"])?.Name ?? "Someone";
        Sounds.Ring(); Flash();
        NotifyCard.Show($"{who} started a call in {t.Name}", "Click to join", "\uE716", "#A78BFA", () => { Bring(); _ = EnterGroupCall(t, id, kind, start: false); }, who);
    });

    async Task EnterGroupCall(ChatThread t, string id, string kind, bool start)
    {
        Sounds.StopRing();
        if (groupCall != null || call == "on" || chats == null) { Notice("You're already in a call", "Leave it first, then join this one."); return; }
        groupCall = new GroupCall(chats, t, id, kind, Me);
        var chat = new IvoChatsView(chats, new ChatLook(Fg, Dim, Card, Field, Accent, B("#553A2F7A")), null, null);
        chat.OpenThread(t);
        groupView = new GroupCallView(groupCall, Me, GroupVideo, LeaveGroupCall, ToggleMax, null, chat);
        groupView.AddPeopleButton(PickPeople);
        smallBounds = new Rect(Left, Top, Width, Height);
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1100, area.Width - 40); Height = Math.Min(720, area.Height - 40);
        Left = area.Left + (area.Width - Width) / 2; Top = area.Top + (area.Height - Height) / 2;
        root.Child = groupView; root.Padding = new Thickness(0); corner.Visibility = Visibility.Collapsed;
        Resizable(true);
        try { if (start) await groupCall.Start(); else await groupCall.Join(); if (kind == "video") await GroupVideo("camera"); }
        catch (Exception ex) { Notice("The call couldn't start", ex.Message); LeaveGroupCall(); }
    }

    async Task GroupVideo(string what)
    {
        if (groupCall == null) return;
        try
        {
            if (what == "screen") { var pick = new ScreenPicker { Owner = this }; pick.ShowDialog(); if (pick.Picked is { } chosen) await groupCall.Video("screen", chosen); }
            else await groupCall.Video(what);
        }
        catch (Exception ex) { Notice(what == "camera" ? "The camera couldn't start" : "Couldn't share the screen", ex.Message); }
    }

    void LeaveGroupCall()
    {
        groupView?.Detach(); groupView = null;
        groupCall?.Leave(); groupCall = null;
        WindowState = WindowState.Normal; Resizable(false);
        Left = smallBounds.Left; Top = smallBounds.Top; Width = smallBounds.Width; Height = smallBounds.Height;
        root.Padding = new Thickness(20, 0, 20, 12); DrawCorner(); Draw();
    }

    // the picture of who we're in a call with, from their chat (null until they've shared one)
    string? PeerPic() => portal == null ? null : contacts.FirstOrDefault(c => c.Name == portal.PeerName) is { } c
        ? chats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box)?.Members.FirstOrDefault()?.Pic : null;

    // ---------- the call's big window ----------
    // the call fills this window, which grows to call size; when the call ends it goes back to how it was
    void ShowCall(bool lobby = false)
    {
        Sounds.StopRing();
        if (portal == null || callView != null) return;
        callView = new PortalCallView(portal, portal.PeerName, () => HangUp(true), Video, () => { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; callView?.FullScreenShown(WindowState == WindowState.Maximized); }, null, ChatPane(), Me,
            lobby ? (_, _) => { portal!.Send(new JsonObject { ["t"] = "answer" }); return CallOn(); } : null, PeerPic(), chats?.Book.Pic);
        callView.AddPeopleButton(PickPeople);
        smallBounds = new Rect(Left, Top, Width, Height);
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1100, area.Width - 40); Height = Math.Min(720, area.Height - 40);
        Left = area.Left + (area.Width - Width) / 2; Top = area.Top + (area.Height - Height) / 2;
        root.Child = callView; root.Padding = new Thickness(0); corner.Visibility = Visibility.Collapsed;
        Resizable(true);
    }

    Button maxBtn = null!;
    void ToggleMax()
    {
        MaxHeight = SystemParameters.WorkArea.Height + 14;   // maximised, it stops at the taskbar
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        maxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; maxBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
        callView?.FullScreenShown(WindowState == WindowState.Maximized);
    }
    void Resizable(bool on)
    {
        ResizeMode = on ? ResizeMode.CanResize : ResizeMode.CanMinimize;
        MinWidth = on ? 640 : 0; MinHeight = on ? 420 : 0;
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).ResizeBorderThickness = new Thickness(on ? 6 : 0);
        maxBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { maxBtn.Content = "\uE922"; maxBtn.ToolTip = "Maximise"; }
    }

    void CloseCall()
    {
        if (callView == null) return;
        callView.Detach(); callView = null;
        WindowState = WindowState.Normal; Resizable(false);
        Left = smallBounds.Left; Top = smallBounds.Top; Width = smallBounds.Width; Height = smallBounds.Height;
        root.Padding = new Thickness(20, 0, 20, 12); DrawCorner();
        FloatCall();
    }

    // the call floats in a corner of the screen while the app is minimised or in the tray (until its ✕ is pressed)
    CallFloat? callFloat;
    void FloatCall()
    {
        var away = WindowState == WindowState.Minimized || !IsVisible;
        if (away && call == "on" && callView != null && portal != null)
        {
            if (callFloat != null) return;
            callFloat = new CallFloat(portal, portal.PeerName, () => callView?.Elapsed ?? "", Bring,
                                      () => { Bring(); WindowState = WindowState.Maximized; callView?.FullScreenShown(true); }, Video, () => HangUp(true), PeerPic());
            callFloat.Show();
        }
        else { callFloat?.Close(); callFloat = null; }
    }

    // ---------- contacts ----------
    void Listen()
    {
        listening?.Cancel();
        if (contacts.Count == 0) return;
        listening = new CancellationTokenSource();
        _ = PortalContacts.Listen(contacts.ToList(), (c, invite, kind) => Dispatcher.BeginInvoke(() =>
        {
            if (kind == PortalContacts.Cancelled)   // they hung up while it rang: the ringing stops, a missed call
            {
                if (incomingPortal is { } k && k.Invite == invite) { incomingPortal = null; Sounds.StopRing(); removeIncoming?.Invoke(); removeIncoming = null; NotifyCard.Show($"Missed call from {c.Name}", "Ivo Portal", "\uE717", "#A78BFA", Bring, c.Name); Draw(); }
                return;
            }
            if (chats?.IsBlockedContact(c.Box) == true) return;   // blocked: their call never rings
            if (stage == "open" || groupCall != null)   // already on a call: call waiting (they hear you're busy; you can merge them in)
            {
                if (kind is not ("voice" or "video" or "screen")) return;
                var theirs = chats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box);
                if (theirs != null) _ = chats!.Busy(theirs);
                incomingPortal = (c, invite, kind, DateTime.Now);
                Sounds.Chime("call");
                if (!IsActive || WindowState == WindowState.Minimized || !IsVisible) NotifyCard.Show($"{c.Name} is calling", "You're on another call: open Ivo Portal to merge them in", "\uE717", "#A78BFA", Bring, c.Name, theirs?.Members.FirstOrDefault()?.Pic);
                removeIncoming?.Invoke(); removeIncoming = null;
                if (layer.Children[0] is FrameworkElement under && under.ActualWidth > 0)
                    removeIncoming = IncomingCall.Show(layer, under, c.Name, kind, theirs?.Members.FirstOrDefault()?.Pic,
                        () => { incomingPortal = null; removeIncoming = null; if (theirs != null) _ = MergeCall([theirs]); },
                        DeclineIncoming, () => removeIncoming = null, "Merge", "Calling you · you're on another call");
                return;
            }
            incomingPortal = (c, invite, kind, DateTime.Now);
            Sounds.Ring(); Flash();
            if (!IsActive || WindowState == WindowState.Minimized || !IsVisible) NotifyCard.Show($"{c.Name} is calling", kind == "video" ? "Ivo Portal · video call" : kind == "screen" ? "Ivo Portal · wants to share their screen" : kind == "voice" ? "Ivo Portal · voice call" : "Ivo Portal · wants to connect", "\uE717", "#A78BFA",
                        Bring, c.Name, chats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box)?.Members.FirstOrDefault()?.Pic);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Draw();
            Dispatcher.BeginInvoke(() => ShowIncomingCall(c, kind), System.Windows.Threading.DispatcherPriority.Loaded);
        }), listening.Token);
    }

    // someone calling: Accept or Decline, at the top of the start page
    FrameworkElement? IncomingPortal()
    {
        if (incomingPortal is not { } k || DateTime.Now - k.At > TimeSpan.FromMinutes(2)) return null;
        var accept = Btn("Accept", Accent, () => _ = AcceptIncoming());
        var decline = Btn("Decline", Red, DeclineIncoming);
        accept.Margin = new Thickness(0, 12, 8, 0); decline.Margin = new Thickness(0, 12, 0, 0);
        return Panel(Stack(T($"{k.Who.Name} is calling", 16, bold: true), Gap(4),
            T( k.Kind == "video" ? "A video call" : k.Kind == "screen" ? "They want to share their screen" : k.Kind == "voice" ? "A voice call" : "They want to connect", 13, Dim),
            new StackPanel { Orientation = Orientation.Horizontal, Children = { accept, decline } }));
    }

    // someone calling: the window blurs behind them, with Decline, Ignore (quiet; the card stays on the start page) and Accept
    void ShowIncomingCall(PortalContact c, string kind)
    {
        removeIncoming?.Invoke(); removeIncoming = null;
        if (incomingPortal is not { } k || k.Who != c || !IsVisible || WindowState == WindowState.Minimized || layer.Children.Count == 0 || layer.Children[0] is not FrameworkElement under || under.ActualWidth <= 0) return;
        var pic = chats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box)?.Members.FirstOrDefault()?.Pic;
        removeIncoming = IncomingCall.Show(layer, under, c.Name, kind, pic, () => _ = AcceptIncoming(), DeclineIncoming, () => { Sounds.StopRing(); removeIncoming = null; });
    }

    async Task AcceptIncoming()
    {
        if (incomingPortal is not { } k) return;
        incomingPortal = null; Sounds.StopRing(); removeIncoming?.Invoke(); removeIncoming = null;
        try { New(); answerKind = k.Kind; with = k.Who; (code, delivered) = await portal!.Join(k.Invite, Me); stage = "joining"; }
        catch (Exception ex) { End(); Notice("Couldn't connect", ex.Message); }
        Draw();
    }

    void DeclineIncoming() { incomingPortal = null; Sounds.StopRing(); removeIncoming?.Invoke(); removeIncoming = null; Draw(); }

    // ---------- merging calls and adding people ----------
    // A one-to-one call becomes a group call: a new group with them and whoever's added (handed over in each chat),
    // the person you're with moves over at once (told on the call itself), the others are told in their chat, and
    // you start the group call. Already in a group call: they're added to that group and pulled into its call.
    readonly HashSet<string> merging = [];   // calls we're moving into (their ring isn't shown again)

    ChatThread? PeerThread() => with is { } w ? chats?.Book.Threads.FirstOrDefault(t => t.Kind == "dm" && t.ContactBox == w.Box) : null;

    void PickPeople(FrameworkElement at)
    {
        if (chats == null) return;
        var inCall = groupCall?.Thread.Members.Select(m => m.Name).ToHashSet() ?? [];
        var others = chats.Book.Threads.Where(t => t.Kind == "dm" && t.ContactBox != null && t.ContactBox != with?.Box && !inCall.Contains(t.Name)).OrderBy(t => t.Name).ToList();
        IvoFrame.PickFrom(at, "Add to this call", others.Select(t => t.Name).ToList(), name => { if (others.FirstOrDefault(t => t.Name == name) is { } d) _ = MergeCall([d]); });
    }

    async Task MergeCall(List<ChatThread> add)
    {
        if (chats == null || add.Count == 0) return;
        try
        {
            if (groupCall != null)
            {
                foreach (var d in add) { await chats.Invite(groupCall.Thread, d); await chats.Merge(d, groupCall.Thread, groupCall.Id, groupCall.Kind); }
                return;
            }
            if (PeerThread() is not { } peer) { Notice("This call can't be merged", "Calls merge with people in your contacts. Add them to your contacts first."); return; }
            var kind = portal?.CameraOn == true || callKind == "video" ? "video" : "voice";
            var g = await chats.NewGroup(string.Join(", ", new[] { peer.Name }.Concat(add.Select(a => a.Name))), [peer, .. add]);
            var id = Guid.NewGuid().ToString("N")[..12];
            merging.Add(id);
            portal?.Send(new JsonObject { ["t"] = "merge", ["g"] = g.Id, ["call"] = id, ["v"] = kind });
            await chats.Merge(peer, g, id, kind);   // in case the call's own line is slow
            foreach (var d in add) await chats.Merge(d, g, id, kind);
            await Task.Delay(400);   // the message on its way before the one-to-one call closes
            End(); Draw();
            await EnterGroupCall(g, id, kind, start: true);
        }
        catch (Exception ex) { Notice("Couldn't merge the call", ex.Message); }
    }

    // told in a chat: only from the person we're calling or with (no one else can pull you into a call)
    void MergedInto(ChatThread dm, string groupId, string id, string kind) => Dispatcher.BeginInvoke(() =>
    {
        if (stage is "calling" or "open" or "joining" && with?.Box == dm.ContactBox) _ = JoinMerged(groupId, id, kind);
    });

    async Task JoinMerged(string groupId, string id, string kind)
    {
        if (chats == null || groupId == "" || !merging.Add(id)) return;
        if (stage != "") { End(); Draw(); }
        var g = await chats.WaitForGroup(groupId);
        if (g == null) { Notice("Couldn't join the merged call", "The group didn't arrive. Ask them to add you again."); return; }
        await EnterGroupCall(g, id, kind, start: false);
    }

    // the contact we're calling answered "busy": they're on another call
    void TheyreBusy(ChatThread t) => Dispatcher.BeginInvoke(() =>
    {
        if (stage != "calling" || with?.Box != t.ContactBox) return;
        Notice($"{with.Name} is on another call", "It's still ringing: they can add you to their call.");
    });

    static readonly FontFamily Fluent = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    FrameworkElement? ContactList()
    {
        if (contacts.Count == 0) return null;
        var list = Stack();
        foreach (var c in contacts.OrderBy(c => c.Name))
        {
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            var thread = chats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box);
            if (thread != null)
                tools.Children.Add(IvoChatsView.Round("\uE8BD", $"Chat with {c.Name}", "#A78BFA", () => { tab = "chats"; Draw(); chatsView?.OpenThread(thread); }, Fluent));
            tools.Children.Add(IvoChatsView.Round("\uE717", $"Call {c.Name}", "#34D399", () => _ = CallContact(c, "voice"), Fluent));
            tools.Children.Add(IvoChatsView.Round("\uE714", $"Video call {c.Name}", "#60A5FA", () => _ = CallContact(c, "video"), Fluent));
            tools.Children.Add(IvoChatsView.Round("\uE74D", "Remove from contacts (restorable for 15 days)", "#F87171", () => RemoveContact(c), Fluent));
            tools.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tools, Dock.Right); row.Children.Add(tools);
            var initial = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 10, 0), Background = Accent,
                                       Child = new TextBlock { Text = c.Name[..1].ToUpper(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            if (PortalCallView.PicBrush(thread?.Members.FirstOrDefault()?.Pic, 64) is { } photo) { initial.Background = photo; initial.Child = null; RenderOptions.SetBitmapScalingMode(initial, BitmapScalingMode.HighQuality); }   // their profile picture
            var name = T(c.Name, 14); name.VerticalAlignment = VerticalAlignment.Center; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { initial, name } });
            list.Children.Add(row);
        }
        return Panel(list);
    }

    async Task CallContact(PortalContact c, string kind)
    {
        if (Me == "") { Notice("Add your name first", "So they know it's you."); return; }
        try
        {
            New(); ringOnOpen = kind; calling = c.Name; with = c;
            code = await portal!.Invite(Me);
            stage = "calling"; Draw(); Sounds.Calling();
            if (!await PortalContacts.RequestCall(c, code, kind)) { stage = "inviting"; Sounds.StopCalling(); }   // the relay couldn't be reached: send the invite by hand
        }
        catch (Exception ex) { End(); Notice("Couldn't call", ex.Message); }
        Draw();
    }

    // during a session: Add to contacts, or their request to add you
    FrameworkElement? ContactPrompt()
    {
        if (portal == null) return null;
        if (asked is { } a)
        {
            var add = Btn("Add", Accent, () => { SaveContact(portal.PeerName, (string?)a["box"] ?? "", (string?)a["key"] ?? ""); portal.Send(new JsonObject { ["t"] = "contact-ok" }); asked = null; Draw(); });
            var no = Btn("Not now", null, () => { portal.Send(new JsonObject { ["t"] = "contact-no" }); asked = null; Draw(); });
            add.Margin = new Thickness(0, 10, 8, 0); no.Margin = new Thickness(0, 10, 0, 0);
            return Panel(Stack(T($"{portal.PeerName} wants to add you to their contacts", 13), new StackPanel { Orientation = Orientation.Horizontal, Children = { add, no } }));
        }
        if (offered != null) { var waiting = Btn("Asked to add…", null, () => { }); waiting.IsEnabled = false; waiting.HorizontalAlignment = HorizontalAlignment.Left; waiting.Margin = new Thickness(0, 0, 0, 12); return waiting; }
        if (contacts.Any(c => c.Name == portal.PeerName)) return null;
        var offer = Btn("Add to contacts", null, () =>
        {
            var c = PortalContacts.Make(portal.PeerName);
            offered = (c.Box, c.Key);
            SaveContact(c.Name, c.Box, c.Key);   // kept now: a session that drops before their answer doesn't lose it
            portal.Send(new JsonObject { ["t"] = "contact", ["box"] = c.Box, ["key"] = c.Key });
            Draw();
        });
        offer.HorizontalAlignment = HorizontalAlignment.Left; offer.Margin = new Thickness(0, 0, 0, 12);
        return offer;
    }

    void SaveContact(string name, string box, string key)
    {
        if (box == "" || key == "") return;
        contacts.RemoveAll(c => c.Box == box);
        contacts.Add(new PortalContact { Name = name, Box = box, Key = key });
        PortalContacts.Save(ContactsFile, contacts);
        Listen();
        chats?.Sync(contacts, Me);   // a chat thread for them
    }

    static WriteableBitmap Paint(WriteableBitmap? bmp, byte[] bgr, int w, int h)
    {
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h) bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), bgr, w * 3, 0);
        return bmp;
    }

    // the taskbar button flashes when something arrives while you're elsewhere
    void Flash() { var f = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = new WindowInteropHelper(this).Handle, dwFlags = 3 | 12, uCount = 3 }; FlashWindowEx(ref f); }

    [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public nint hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
