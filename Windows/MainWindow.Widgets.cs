using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Ivo;

// Desktop widgets (Settings → Widgets): small cards that live on the desktop, under every window,
// through Win+D, out of the taskbar and Alt+Tab, where you drag them (or locked in place). Each refreshes on a slow clock.
//   ivo: Blub, waving at your reminders and new songs · clock: the time, date and weather (Open-Meteo, no key)
//   music: what Spotify is playing · habits: today's habits, click to tick · reminders: what's coming up next
public class WidgetSpot { public bool On { get; set; } public double? X { get; set; } public double? Y { get; set; } }

public partial class MainWindow
{
    public static readonly (string Id, string Name)[] WidgetKinds =
        [("ivo", "Blub"), ("clock", "Clock & weather"), ("music", "Now playing"), ("habits", "Today's habits"), ("reminders", "Next reminders")];
    readonly Dictionary<string, DeskWidget> widgets = [];

    WidgetSpot Spot(string id) => S.Widgets.TryGetValue(id, out var s) ? s : S.Widgets[id] = new WidgetSpot();

    // shows the widgets that are on and closes the ones that aren't
    public void StartWidgets()
    {
        foreach (var (id, _) in WidgetKinds)
        {
            var on = Spot(id).On;
            if (on && !widgets.ContainsKey(id)) { var w = new DeskWidget(this, id, Spot(id)); widgets[id] = w; w.Show(); }
            else if (!on && widgets.Remove(id, out var gone)) gone.Close();
        }
    }

    public void SetWidget(string id, bool on) { Spot(id).On = on; SaveSettings(); StartWidgets(); }

    // the Settings row: a switch per widget, and a lock that keeps them where they are
    FrameworkElement WidgetsList()
    {
        var list = new StackPanel();
        foreach (var (id, name) in WidgetKinds)
        {
            var box = new CheckBox { Content = name, IsChecked = Spot(id).On, Margin = new Thickness(0, 0, 0, 4) };
            box.SetResourceReference(Control.ForegroundProperty, "Fg");
            System.Windows.Automation.AutomationProperties.SetName(box, name + " widget");   // "turn on the clock widget"
            box.Click += (_, _) => SetWidget(id, box.IsChecked == true);
            list.Children.Add(box);
        }
        var locked = new CheckBox { Content = "Lock them in place", IsChecked = S.WidgetsLocked, Margin = new Thickness(0, 4, 0, 0), ToolTip = "Off: drag a widget to move it" };
        locked.SetResourceReference(Control.ForegroundProperty, "Dim");
        System.Windows.Automation.AutomationProperties.SetName(locked, "Lock widgets");
        locked.Click += (_, _) => { S.WidgetsLocked = locked.IsChecked == true; SaveSettings(); };
        list.Children.Add(locked);
        return list;
    }

    internal bool WidgetsLocked => S.WidgetsLocked;
    internal Settings WidgetSettings => S;
    internal void WidgetSaved() => SaveSettings();
    internal void WidgetCheer(Habit h) => Cheer(h);
    internal bool WidgetSpotify => SpotifyOn;
    internal Task WidgetMusicControl(string action, bool isPlaying) => action switch
    {
        "previous" => MusicDo("POST", "/me/player/previous"),
        "toggle" => MusicDo("PUT", isPlaying ? "/me/player/pause" : "/me/player/play"),
        "next" => MusicDo("POST", "/me/player/next"),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
    internal Task WidgetMusicSeek(double positionMs) => MusicDo("PUT", $"/me/player/seek?position_ms={(long)positionMs}");
}

public class DeskWidget : Window
{
    const double IvoFaceSize = 96;
    readonly MainWindow main;
    readonly string id;
    readonly WidgetSpot spot;
    readonly StackPanel body = new();
    readonly DispatcherTimer clock;
    public static event Action<string>? Happened;   // a new song or a reminder coming up: Blub reacts

    public DeskWidget(MainWindow main, string id, WidgetSpot spot)
    {
        this.main = main; this.id = id; this.spot = spot;
        Title = "Blub widget"; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; ShowActivated = false;
        FontFamily = main.FontFamily;
        var area = SystemParameters.WorkArea;
        var i = Array.FindIndex(MainWindow.WidgetKinds, k => k.Id == id);
        Left = spot.X ?? area.Right - 300;
        Top = spot.Y ?? area.Top + 24 + i * 150;
        // one glass card for all (Blub stands free): the same width, corners and padding; the edge lights up under the mouse
        var card = new Border { Child = body, Padding = id == "music" ? new Thickness(12) : new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Width = id == "music" ? 350 : 252 };
        if (id == "ivo") { card.Background = Brushes.Transparent; card.BorderThickness = new Thickness(0); card.Padding = new Thickness(6); card.Width = double.NaN; }
        else
        {
            card.SetResourceReference(Border.BackgroundProperty, "BoxBg"); card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "AccentGrad");
            MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        }
        Content = card;

        // press and drag to move a widget
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source &&
                (FindAncestor<Button>(source) != null || FindAncestor<Slider>(source) != null)) return;
            // Ivo's widget: a tap (press and let go without moving her) opens or tucks away her chat
            // moving her with her chat open: the chat tucks away as she moves and opens again where she's put down
            var (x, y) = (Left, Top);
            var chatOpen = id == "ivo" && main.IvoWindowOpen;
            void Moving(object? _, EventArgs __) { if (main.IvoWindowOpen && (Math.Abs(Left - x) >= 3 || Math.Abs(Top - y) >= 3)) main.TuckIvoWindow(); }
            if (chatOpen) LocationChanged += Moving;
            dragging = true;
            if (!main.WidgetsLocked && e.ButtonState == MouseButtonState.Pressed)
            {
                if (her != null) her.React(IvoAvatarState.Thinking);
                try { DragMove(); } catch { }
                if (her != null) her.React(IvoAvatarState.Excited);
            }
            dragging = false;
            LocationChanged -= Moving;
            if (id != "ivo") return;
            var moved = Math.Abs(Left - x) >= 3 || Math.Abs(Top - y) >= 3;
            if (!moved || chatOpen) { e.Handled = true; Talk(); }   // reopening raises her again
            if (lowerAfterDrag) Raise(false);                          // her chat closed while she was held, and stays closed
        };
        LocationChanged += (_, _) => { spot.X = Left; spot.Y = Top; saveSoon.Stop(); saveSoon.Start(); };
        saveSoon.Tick += (_, _) => { saveSoon.Stop(); main.WidgetSaved(); };
        var menu = new ContextMenu { Template = MenuLook, ItemContainerStyle = ItemLook };
        menu.Items.Add(Item("Talk to Blub", Talk));
        if (id == "ivo")
        {
            var wander = Item("Wander", () => { main.WidgetSettings.IvoWandering = !main.WidgetSettings.IvoWandering; main.WidgetSaved(); ConfigureIvo(); });
            wander.IsCheckable = true; wander.IsChecked = main.WidgetSettings.IvoWandering; menu.Items.Add(wander);
            var opacity = new MenuItem { Header = "Opacity" };
            foreach (var value in new[] { .5, .75, 1.0 })
            {
                var v = value; var item = new MenuItem { Header = (int)(v * 100) + "%" };
                item.Click += (_, _) => { main.WidgetSettings.IvoOpacity = v; main.WidgetSaved(); ConfigureIvo(); };
                opacity.Items.Add(item);
            }
            menu.Items.Add(opacity);
            var size = new MenuItem { Header = "Size" };
            foreach (var name in new[] { "small", "medium", "large" })
            {
                var n = name; var item = new MenuItem { Header = char.ToUpper(n[0]) + n[1..] };
                item.Click += (_, _) => { main.WidgetSettings.IvoSize = n; main.WidgetSaved(); ConfigureIvo(); };
                size.Items.Add(item);
            }
            menu.Items.Add(size);
            foreach (var state in new[] { ("Thinking", "thinking"), ("Sleep", "sleep"), ("Celebrate", "celebrate"), ("Error", "error") })
            {
                var name = state.Item1; var value = state.Item2;
                menu.Items.Add(Item(name, () => IvoAvatar.SetIvoState(value)));
            }
            menu.Items.Add(Item("Quit Ivo", main.QuitIvo));
        }
        menu.Items.Add(Item("Hide this widget", () => main.SetWidget(id, false)));
        ContextMenu = menu;
        // Ivo minimised or in the tray: her chat opens on its own; otherwise in her panel in Ivo
        void Talk() { if (!main.IsVisible || main.WindowState == WindowState.Minimized) main.ToggleIvoWindow(new Rect(Left, Top, ActualWidth, ActualHeight), Raise); else { main.Activate(); main.OpenIvo(); } }

        // slow clocks: the clock each 20 s, music each 10 s, the rest each minute
        clock = new DispatcherTimer(TimeSpan.FromSeconds(id switch { "clock" => 20, "music" => 10, _ => 60 }), DispatcherPriority.Background, async (_, _) => await Draw(), Dispatcher);
        if (id == "music") musicProgress = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateMusicProgress(), Dispatcher);
        wanderTimer.Tick += (_, _) => Wander();
        Loaded += async (_, _) => { clock.Start(); musicProgress?.Start(); await Draw(); ConfigureIvo(); };
        Closed += (_, _) => { clock.Stop(); musicProgress?.Stop(); wanderTimer.Stop(); Happened -= React; };
        if (id == "ivo") Happened += React;
    }

    readonly DispatcherTimer saveSoon = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer wanderTimer = new() { Interval = TimeSpan.FromSeconds(18) };
    readonly Random wanderRandom = new();
    DispatcherTimer? musicProgress;
    Slider? musicSeek;
    TextBlock? musicAt, musicLength;
    void ConfigureIvo()
    {
        if (id != "ivo") return;
        Opacity = Math.Clamp(main.WidgetSettings.IvoOpacity <= 0 ? 1 : main.WidgetSettings.IvoOpacity, .5, 1);
        if (her != null)
        {
            var scale = main.WidgetSettings.IvoSize switch { "small" => .75, "large" => 1.25, _ => 1 };
            var size = IvoFaceSize * scale;
            her.Width = her.Height = size;
            if (ring != null) ring.Width = ring.Height = size;
            if (face != null)
            {
                face.Width = face.Height = size;
                face.Clip = new EllipseGeometry(new Rect(0, 0, size, size));
            }
        }
        wanderTimer.Stop();
        if (main.WidgetSettings.IvoWandering) wanderTimer.Start();
    }

    void Wander()
    {
        if (id != "ivo" || her == null || main.IvoWindowOpen || Quiet.Now) return;
        var work = SystemParameters.WorkArea;
        var dx = wanderRandom.Next(-90, 91);
        var targetLeft = Math.Clamp(Left + dx, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
        var targetTop = Math.Clamp(Top + wanderRandom.Next(-18, 19), work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
        her.React(IvoAvatarState.Excited);
        var x = new DoubleAnimation(Left, targetLeft, TimeSpan.FromMilliseconds(900))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
        var y = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(900))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        BeginAnimation(Window.LeftProperty, x);
        BeginAnimation(Window.TopProperty, y);
    }
    double musicDuration, musicPosition;
    DateTime musicPolled;
    bool musicPlaying;
    static MenuItem Item(string text, Action click) { var m = new MenuItem { Header = text }; m.Click += (_, _) => click(); return m; }
    static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var node = source; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node) : System.Windows.LogicalTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    static readonly ControlTemplate TransportButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
  <Border x:Name="Surface" Background="{TemplateBinding Background}" CornerRadius="20">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.84" /></Trigger>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.65" /></Trigger>
    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45" /></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
    static readonly ControlTemplate WidgetSeekTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
  <Grid Background="Transparent">
    <ProgressBar Height="4" VerticalAlignment="Center" IsHitTestVisible="False"
                 Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
      <ProgressBar.Template>
        <ControlTemplate TargetType="ProgressBar">
          <Grid>
            <Border x:Name="PART_Track" CornerRadius="2" Background="#665C596B" />
            <Border x:Name="PART_Indicator" CornerRadius="2" Background="#795BFF" HorizontalAlignment="Left" />
          </Grid>
        </ControlTemplate>
      </ProgressBar.Template>
    </ProgressBar>
    <Track x:Name="PART_Track">
      <Track.DecreaseRepeatButton>
        <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
          <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
          <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb x:Name="Knob" Width="10" Height="10" Opacity="0">
          <Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="#FFFFFF" /></ControlTemplate></Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Knob" Property="Opacity" Value="1" /></Trigger>
    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.35" /></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");

    void UpdateMusicProgress()
    {
        if (musicDuration <= 0 || musicSeek == null || musicSeek.IsMouseCaptureWithin) return;
        var position = musicPosition + (musicPlaying ? (DateTime.UtcNow - musicPolled).TotalMilliseconds : 0);
        position = Math.Clamp(position, 0, musicDuration);
        musicSeek.Value = position;
        if (musicAt != null) musicAt.Text = MusicClock(position);
    }

    static string MusicClock(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    // the right-click menu in the app's look: a dark rounded card, plain rows that light up, no icon gutter
    static readonly ControlTemplate MenuLook = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ContextMenu">
             <Border Background="{DynamicResource PopupBg}" BorderBrush="{DynamicResource GlassStroke}" BorderThickness="1" CornerRadius="10" Padding="4">
               <StackPanel IsItemsHost="True" /></Border></ControlTemplate>
""");
    static readonly Style ItemLook = (Style)System.Windows.Markup.XamlReader.Parse("""
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuItem">
             <Setter Property="Foreground" Value="{DynamicResource Fg}" /><Setter Property="FontSize" Value="13" /><Setter Property="Cursor" Value="Hand" />
             <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
               <Border x:Name="b" Background="Transparent" CornerRadius="6" Padding="12,7,18,7" MinWidth="150">
                 <ContentPresenter ContentSource="Header" /></Border>
               <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource Panel}" /></Trigger></ControlTemplate.Triggers>
             </ControlTemplate></Setter.Value></Setter></Style>
""");

    // ---------- pinned to the desktop ----------
    const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WM_WINDOWPOSCHANGING = 0x46;
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)((long)GetWindowLongPtr(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));   // not in Alt+Tab
        SetWindowLongPtr(h, GWLP_HWNDPARENT, FindWindow("Progman", null));   // owned by the desktop: Win+D leaves it be
        SetWindowPos(h, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);     // under every window
        HwndSource.FromHwnd(h)!.AddHook((IntPtr _, int msg, IntPtr _, IntPtr l, ref bool _) =>
        {
            if (msg == WM_WINDOWPOSCHANGING && !raised) { var p = Marshal.PtrToStructure<WINDOWPOS>(l); p.hwndInsertAfter = (IntPtr)1; Marshal.StructureToPtr(p, l, false); }   // and it stays under
            return IntPtr.Zero;
        });
    }

    // over every window while her floating chat is open (it sits beside her), back under them after
    bool raised, dragging, lowerAfterDrag;
    public void Raise(bool up)
    {
        if (!up && dragging) { lowerAfterDrag = true; return; }   // her chat closed mid-drag: she stays on top until she's put down
        lowerAfterDrag = false;
        raised = up;
        var h = new WindowInteropHelper(this).Handle;
        if (up) SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        else { SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); SetWindowPos(h, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
    }

    // ---------- what each shows ----------
    TextBlock Line(string text, double size = 13, string brush = "Fg", bool bold = false)
    {
        var t = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        ColorEmoji.Append(t.Inlines, text, size);   // use the installed Windows emoji font
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    // a card's header: its icon and a small caps title
    FrameworkElement Head(string glyph, string title)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentGrad");
        var name = Line(title, 10.5, "Dim", true); name.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), Children = { icon, name } };
    }

    // a thin line between parts of a card
    static Border Rule() { var r = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 0) }; r.SetResourceReference(Border.BackgroundProperty, "GlassStroke"); return r; }

    IvoAvatar? her; TextBlock? bubble; FrameworkElement? say; System.Windows.Shapes.Ellipse? ring; Grid? face;
    readonly DispatcherTimer rest = new() { Interval = TimeSpan.FromSeconds(10) };
    string lastSong = "";
    readonly HashSet<string> told = [];
    readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> art = [];

    async Task Draw()
    {
        var s = main.WidgetSettings;
        switch (id)
        {
            case "ivo":
                if (her == null)
                {
                    // her circle in a thin glowing ring; a little speech bubble above her when she reacts
                    her = new IvoAvatar { Width = IvoFaceSize, Height = IvoFaceSize, RenderTransform = new ScaleTransform(1.35, 1.35), RenderTransformOrigin = new Point(0.5, 0.5), ToolTip = "Blub · click to talk", Cursor = Cursors.Hand };
                    ring = new System.Windows.Shapes.Ellipse { Width = IvoFaceSize, Height = IvoFaceSize, StrokeThickness = 2.5, Opacity = 0.75, IsHitTestVisible = false };
                    ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentGrad");
                    face = new Grid { Width = IvoFaceSize, Height = IvoFaceSize, HorizontalAlignment = HorizontalAlignment.Center, Clip = new EllipseGeometry(new Rect(0, 0, IvoFaceSize, IvoFaceSize)), Children = { her, ring } };
                    bubble = Line("", 12.5); bubble.TextWrapping = TextWrapping.Wrap; bubble.MaxWidth = 190; bubble.TextAlignment = TextAlignment.Center; bubble.TextTrimming = TextTrimming.None;
                    var b = new Border { Child = bubble, Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
                    b.SetResourceReference(Border.BackgroundProperty, "PopupBg"); b.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
                    var tail = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L7,7 L14,0 Z"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };
                    tail.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "PopupBg");
                    say = new StackPanel { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4), Children = { b, tail } };
                    body.Children.Add(say); body.Children.Add(face);
                    // she comes alive to say hello, then rests still: a desktop widget animating all day costs CPU for nothing
                    rest.Tick += (_, _) => { rest.Stop(); her.Stop(); };
                    rest.Start();
                }
                foreach (var r in s.Schedules.Where(x => x.On && x.Kind == "reminder" && x.App == ""))   // a reminder due within 5 minutes
                    if (Next(r) is { } at && at - DateTime.Now is { TotalMinutes: > 0 and <= 5 } && told.Add(r.Id + at.Ticks)) React($"In {Math.Ceiling((at - DateTime.Now).TotalMinutes)} min: {r.Text} ⏰");
                break;
            case "clock":
            {
                body.Children.Clear();
                var now = DateTime.Now;
                var time = Line(now.ToString("h:mm"), 40, bold: true); time.Margin = new Thickness(0, -6, 0, 0);
                var half = Line(now.ToString("tt"), 14, "Dim", true); half.VerticalAlignment = VerticalAlignment.Bottom; half.Margin = new Thickness(6, 0, 0, 8);
                body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { time, half } });
                body.Children.Add(Line(now.ToString("dddd, d MMMM"), 12.5, "Dim"));
                if (await Ivo.Weather.Current() is { } w)
                {
                    var sky = Line(Ivo.Weather.Icon(w.Sky), 26); sky.VerticalAlignment = VerticalAlignment.Center; sky.Margin = new Thickness(0, 0, 10, 0);
                    var temp = Line($"{w.Temp:0}°", 20, bold: true); temp.VerticalAlignment = VerticalAlignment.Center;
                    var more = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Children = { Line($"{w.High:0}° / {w.Low:0}°", 12), Line(w.Sky + (w.Place != "" ? " · " + w.Place : ""), 11.5, "Dim") } };
                    var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
                    DockPanel.SetDock(sky, Dock.Left); DockPanel.SetDock(temp, Dock.Left);
                    row.Children.Add(sky); row.Children.Add(temp); row.Children.Add(more);
                    body.Children.Add(Rule()); body.Children.Add(row);
                }
                break;
            }
            case "music":
            {
                body.Children.Clear();
                var p = main.WidgetSpotify ? await SpotifyApi.Get("/me/player") : null;
                if (p?["item"] is { } item)
                {
                    var name = (string?)item["name"] ?? ""; var who = SpotifyView.Artists(item);
                    var playing = p["is_playing"]?.GetValue<bool>() == true;
                    musicDuration = (double?)item["duration_ms"] ?? 0;
                    musicPosition = (double?)p["progress_ms"] ?? 0;
                    musicPolled = DateTime.UtcNow;
                    musicPlaying = playing;

                    var cover = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 14, 0) };
                    cover.SetResourceReference(Border.BackgroundProperty, "Panel");
                    if ((item["album"]?["images"] as JsonArray)?.LastOrDefault()?["url"]?.ToString() is { } url)
                    {
                        if (!art.TryGetValue(url, out var pic))
                        {
                            art.Clear();
                            pic = new System.Windows.Media.Imaging.BitmapImage();
                            pic.BeginInit(); pic.UriSource = new Uri(url); pic.DecodePixelWidth = 104; pic.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; pic.EndInit();
                            art[url] = pic;
                        }
                        cover.Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
                    }
                    var heading = (StackPanel)Head("", "NOW PLAYING");
                    heading.Margin = new Thickness(0, 0, 0, 2);
                    var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { heading, Line(name, 14, bold: true), Line(who, 12.5, "Dim") } };
                    var top = new DockPanel();
                    DockPanel.SetDock(cover, Dock.Left);
                    top.Children.Add(cover); top.Children.Add(words);
                    body.Children.Add(top);

                    if (musicDuration > 0)
                    {
                        var seek = musicSeek = new Slider
                        {
                            Minimum = 0, Maximum = musicDuration, Value = Math.Clamp(musicPosition, 0, musicDuration),
                            Height = 16, Margin = new Thickness(0, 8, 0, 0), IsMoveToPointEnabled = true,
                            Template = WidgetSeekTemplate, ToolTip = "Seek",
                        };
                        MainWindow.OnDrag(seek, main.WidgetMusicSeek);
                        body.Children.Add(seek);
                        musicAt = new TextBlock { FontSize = 10.5, Text = MusicClock(musicPosition) };
                        musicAt.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                        musicLength = new TextBlock { FontSize = 10.5, Text = MusicClock(musicDuration), TextAlignment = TextAlignment.Right };
                        musicLength.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                        var times = new DockPanel { Margin = new Thickness(0, -1, 0, 0), Children = { musicLength, musicAt } };
                        DockPanel.SetDock(musicLength, Dock.Right);
                        body.Children.Add(times);
                    }

                    Button Transport(string glyph, string tip, string action, bool primary = false)
                    {
                        var button = new Button
                        {
                            Content = glyph, ToolTip = tip, Width = primary ? 40 : 34, Height = primary ? 40 : 34,
                            Margin = new Thickness(6, 0, 6, 0), FontFamily = App.IconFont, FontSize = primary ? 15 : 13,
                            Foreground = Brushes.White,
                            Background = primary ? new SolidColorBrush(Color.FromRgb(0x75, 0x58, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x75, 0xA0, 0x96, 0xAD)),
                            BorderThickness = new Thickness(0), Template = TransportButtonTemplate, Cursor = Cursors.Hand, Focusable = false,
                        };
                        System.Windows.Automation.AutomationProperties.SetName(button, tip);
                        button.Click += (_, _) => _ = main.WidgetMusicControl(action, playing);
                        return button;
                    }
                    var controls = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 6, 0, 0),
                        Children =
                        {
                            Transport("\uE892", "Previous track", "previous"),
                            Transport(playing ? "\uE769" : "\uE768", playing ? "Pause" : "Play", "toggle", primary: true),
                            Transport("\uE893", "Next track", "next"),
                        },
                    };
                    body.Children.Add(controls);

                    if (name != lastSong && lastSong != "") Happened?.Invoke($"Ooh, {name} 🎶");
                    lastSong = name;
                }
                else
                {
                    musicDuration = 0; musicSeek = null; musicAt = musicLength = null;
                    body.Children.Add(Head("", "NOW PLAYING"));
                    body.Children.Add(Line(main.WidgetSpotify ? "Nothing playing right now" : "Connect Spotify in Settings", 13, "Dim"));
                }
                break;
            }
            case "habits":
            {
                body.Children.Clear();
                var today = DateTime.Today; var key = today.ToString("yyyy-MM-dd");
                var due = s.Habits.Where(h => h.Due(today)).ToList();
                body.Children.Add(Head("", due.Count == 0 ? "TODAY'S HABITS" : $"TODAY'S HABITS · {due.Count(h => h.Did(today))}/{due.Count}"));
                if (due.Count == 0) body.Children.Add(Line("Nothing due today", 13, "Dim"));
                foreach (var h in due)
                {
                    // a row you click: a round tick, the habit, its streak
                    var did = h.Did(today);
                    var tick = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(did ? 0 : 1.5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                    if (did) { tick.SetResourceReference(Border.BackgroundProperty, "AccentGrad"); tick.Child = new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
                    else tick.SetResourceReference(Border.BorderBrushProperty, "Dim");
                    var label = Line(h.Name, 13.5, did ? "Dim" : "Fg"); label.VerticalAlignment = VerticalAlignment.Center;
                    if (did) label.TextDecorations = TextDecorations.Strikethrough;
                    var row = new DockPanel();
                    DockPanel.SetDock(tick, Dock.Left); row.Children.Add(tick);
                    if (h.Streak(today) is > 1 and var n) { var streak = Line($"🔥 {n}", 12, "Dim"); streak.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(streak, Dock.Right); row.Children.Add(streak); }
                    row.Children.Add(label);
                    var hit = new Border { Child = row, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = did ? "Done today · click to undo" : "Click when it's done" };
                    hit.MouseEnter += (_, _) => hit.SetResourceReference(Border.BackgroundProperty, "Panel");
                    hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
                    hit.MouseLeftButtonDown += async (_, e) => { e.Handled = true; if (!h.Days.Remove(key)) { h.Days.Add(key); main.WidgetCheer(h); } main.WidgetSaved(); await Draw(); };
                    body.Children.Add(hit);
                }
                break;
            }
            case "reminders":
            {
                body.Children.Clear();
                body.Children.Add(Head("", "COMING UP"));
                var next = s.Schedules.Where(x => x.On && x.Kind == "reminder" && x.App == "").Select(x => (x, at: Next(x))).Where(t => t.at != null).OrderBy(t => t.at).Take(5).ToList();
                if (next.Count == 0) body.Children.Add(Line("No reminders coming up", 13, "Dim"));
                foreach (var (x, at) in next)
                {
                    // the time in a column of its own, the reminder beside it
                    var when = Line(When(at!.Value), 12.5, "AccentGrad", true); when.Width = 104;
                    var what = Line(x.Text, 13); what.ToolTip = x.Text;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                    DockPanel.SetDock(when, Dock.Left); row.Children.Add(when); row.Children.Add(what);
                    body.Children.Add(row);
                }
                break;
            }
        }
    }

    // her reaction: she comes alive and waves, her ring brightens, and a little bubble says why, for a few seconds
    void React(string text)
    {
        if (her == null || bubble == null || say == null || ring == null) return;
        rest.Stop(); her.Start(); rest.Start();
        ring.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.75, TimeSpan.FromSeconds(2.5)));
        bubble.Inlines.Clear(); ColorEmoji.Append(bubble.Inlines, text, bubble.FontSize); say.Visibility = Visibility.Visible;
        var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        hide.Tick += (_, _) => { hide.Stop(); say.Visibility = Visibility.Collapsed; };
        hide.Start();
    }

    static string When(DateTime at) => at.Date == DateTime.Today ? at.ToString("h:mm tt") : at.Date == DateTime.Today.AddDays(1) ? "Tmrw " + at.ToString("h:mm tt") : at.ToString("ddd h:mm tt");   // short: it sits in its own column

    // a reminder's next time from now, or null when it's past and doesn't repeat
    public static DateTime? Next(Scheduled x, DateTime? from = null)
    {
        var now = from ?? DateTime.Now;
        if (x.Repeat == "once") return x.When > now ? x.When : null;
        for (var d = now.Date; d < now.Date.AddDays(8); d = d.AddDays(1))
        {
            var at = d + x.When.TimeOfDay;
            if (at <= now) continue;
            if (x.Repeat == "weekdays" && d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (x.Repeat == "weekly" && d.DayOfWeek != x.When.DayOfWeek) continue;
            return at;
        }
        return null;
    }

}
