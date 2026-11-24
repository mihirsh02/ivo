using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

public partial class App : Application
{
    // Icons mixed from Hugeicons, Phosphor, Iconoir, Majesticons, Circum and Streamline (Assets/IvoIcons.otf, built at the
    // codepoints the app used with Segoe Fluent, so Segoe still covers anything missing)
    static readonly System.Windows.Media.FontFamily MixIcons = new(new Uri("pack://application:,,,/"), "./Assets/#Ivo Icons, Segoe Fluent Icons");
    // the Soft theme's icons: Google's Material Symbols Rounded at the same codepoints (tools\icons\soft)
    static readonly System.Windows.Media.FontFamily SoftIcons = new(new Uri("pack://application:,,,/"), "./Assets/#Ivo Icons Soft, Segoe Fluent Icons");
    public static System.Windows.Media.FontFamily IconFont { get; private set; } = MixIcons;   // icons drawn from code use it when built; XAML follows live

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--elevated-host", var pipe, var parent] && int.TryParse(parent, out var pid))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await Elevation.Host(pipe, pid); } catch { }
            Shutdown();
            return;
        }
        // One Ivo at a time: opening it again brings the running one (maybe in the tray) to the front.
        single = new Mutex(true, "Ivo.SingleInstance." + Environment.UserName, out var first);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Ivo.Show." + Environment.UserName);
        if (!first)
        {
            show.Set();
            Shutdown();
            return;
        }
        base.OnStartup(e);
        // screen readers: an icon button with a tooltip but no name is read out by its tooltip
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is FrameworkElement { ToolTip: string tip } b && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b)))
                System.Windows.Automation.AutomationProperties.SetName(b, tip);
        }));
        AppDomain.CurrentDomain.UnhandledException += (_, x) => Crashed(x.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, x) => Crashed(x.Exception);
        Resources["IconFont"] = IconFont;
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, _) => Press(s, true)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((s, _) => Press(s, false)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.MouseLeaveEvent, new MouseEventHandler((s, _) => Press(s, false)), true);
        // no focus rectangles (the solid or dotted box Windows draws around whatever has keyboard focus): text boxes show
        // their caret, and everything else lights up on hover
        // One look for every line in Ivo, set here rather than on each element:
        //  - weight: every outline, one-sided line and divider is 1.5 (the chat bar's): a 1px line's corners smooth across
        //    two pixels and came out fainter than its edges, and mixed 1 / 1.5 lines looked uneven side by side;
        //  - corners: every card, panel and box rounds by the same amount; pills and circles (buttons, chips, avatars) keep theirs.
        // (on each outline's first layout: WPF only sends Loaded to elements that ask for it, so a Loaded class handler
        // missed nearly all of them; every element does get SizeChanged when it's first sized)
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Border), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, e) =>
        {
            if (s is not System.Windows.Controls.Border b || e.NewSize.Height <= 0 || styled.TryGetValue(b, out _)) return;
            styled.Add(b, null);
            if (DependencyPropertyHelper.GetValueSource(b, System.Windows.Controls.Border.BorderThicknessProperty).IsExpression) return;   // a section: its edge and corners come from the theme
            static double W(double v) => v > 0 && v <= 1.5 ? Line : v;
            var t = b.BorderThickness;
            if (t.Left is > 0 and <= 1.5 || t.Top is > 0 and <= 1.5 || t.Right is > 0 and <= 1.5 || t.Bottom is > 0 and <= 1.5)
                b.BorderThickness = new Thickness(W(t.Left), W(t.Top), W(t.Right), W(t.Bottom));
            if (b.Child == null && b.Background != null && (b.Height == 1 || b.Width == 1))   // a divider drawn as a thin filled strip
            {
                if (b.Height == 1) b.Height = Line; else b.Width = Line;
                if (b.Background == Current.Resources["GlassStroke"])   // the outline colour (other thin strips keep theirs)
                    b.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Separator");
            }
            Round(b);
        }), true);
        // text size: each element on its first layout (see ScaleText)
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((_, _) => PauseMotion(false)));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, _) => ScaleElement((DependencyObject)s)), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((s, _) => { if (s is FrameworkElement f && f.FocusVisualStyle != FocusRing) f.FocusVisualStyle = FocusRing; }), true);
        Watchdog.Start(Dispatcher);
        var launch = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        // just installed, and an account from before is here: carry on with it, or start a new one (before anything loads it)
        string name = "";
        try { name = (string?)System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(Store.Dir, "settings.json")))?["Profile"]?["Name"] ?? ""; } catch { }
        AccountChoice.AskIfNeeded("Ivo", Store.Dir, name.Split(' ')[0]);
        var window = new MainWindow();
        Watchdog.Log($"launch: window built after {(DateTime.Now - launch).TotalSeconds:0.0} s");
        window.ContentRendered += (_, _) => Watchdog.Log($"launch: on screen after {(DateTime.Now - launch).TotalSeconds:0.0} s");
        var args = Environment.GetCommandLineArgs();
        if (window.S.Splash != "off" && !args.Contains("--tray") && !args.Contains("--quiet")) Splash.Play(window.S.Splash, window.ShowWindow);   // --quiet: relaunched after an update
        else if (args.Contains("--tray")) window.Show(); else window.ShowWindow();
        _ = Dispatcher.BeginInvoke(ColorEmoji.Warm, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        new Thread(() => { while (show.WaitOne()) window.Dispatcher.BeginInvoke(window.ShowWindow); }) { IsBackground = true }.Start();
        StartIvoStateApi(window);
    }

    static Mutex? single;   // held for the app's lifetime
    static void StartIvoStateApi(MainWindow window)
    {
        string last = "";
        var battery = System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent;
        var lowPower = battery is >= 0 and < 20 &&
                       System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Charging;
        IvoAvatar.ReducedMotion = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Control Panel\Accessibility\AnimationEffects",
            "AnimationEffectsEnabled", 1) is not 1 || lowPower || Quiet.Now;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            try
            {
                var file = System.IO.Path.Combine(Store.Dir, "ivo-state.txt");
                if (!System.IO.File.Exists(file)) return;
                var state = System.IO.File.ReadAllText(file).Trim();
                if (state == "" || state == last) return;
                last = state;
                var battery = System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent;
                var lowPower = battery is >= 0 and < 20 &&
                               System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Charging;
                var animationsDisabled = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Control Panel\Accessibility\AnimationEffects",
                    "AnimationEffectsEnabled", 1) is not 1;
                IvoAvatar.ReducedMotion = lowPower || animationsDisabled || Quiet.Now;
                IvoAvatar.SetIvoState(state);
            }
            catch { }
        };
        timer.Start();
    }

    // Text size (Settings, Ctrl + / −) without blur: instead of shrinking the whole window as one picture (which puts every
    // edge between screen pixels), each font size and line height is multiplied by Zoom, so text and icon glyphs are drawn
    // at their real size, snapped to whole pixels. Padding and fixed sizes stay as they are. An element's own size (set on
    // it, by its style or its template) is scaled once on first layout and again when Zoom changes; inherited ones follow.
    public static double Zoom = 1;
    record TextBase(double Size, double Line, double Applied);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, TextBase> bases = new();
    static readonly DependencyProperty FontSizeDp = System.Windows.Documents.TextElement.FontSizeProperty, LineDp = System.Windows.Documents.Block.LineHeightProperty;

    public static void ScaleText(DependencyObject d)
    {
        var cur = (double)d.GetValue(FontSizeDp);
        var had = bases.TryGetValue(d, out var b);
        var icon = IsIcon(d);
        if (had && Math.Abs(cur - Scaled(b!.Size, b.Applied, icon)) < 0.01)   // ours, unchanged since
        {
            if (b.Applied == Zoom) return;
        }
        else
        {
            var src = DependencyPropertyHelper.GetValueSource(d, FontSizeDp).BaseValueSource;
            if (d is not Window && src is not (BaseValueSource.Local or BaseValueSource.Style or BaseValueSource.DefaultStyle or BaseValueSource.ImplicitStyleReference or BaseValueSource.ParentTemplate))
                return;   // inherited: it follows its parent
            var line = d is System.Windows.Controls.TextBlock or System.Windows.Documents.Block ? (double)d.GetValue(LineDp) : double.NaN;
            b = new TextBase(IconSize(d, cur), line, 1);   // a new size (first layout, or code set it since): the base from now on
        }
        bases.AddOrUpdate(d, b with { Applied = Zoom });
        // icons never take the text's Medium weight (a synthetic bold that bloats glyphs)
        d.SetCurrentValue(FontSizeDp, Scaled(b.Size, Zoom, icon));
        if (icon)
        {
            d.SetCurrentValue(System.Windows.Documents.TextElement.FontWeightProperty, FontWeights.Normal);
            // icons are drawn like shapes, not text: their true outline, smoothed evenly. Text's pixel-snapping (Display)
            // and ClearType's coloured edges bend an unhinted icon glyph and leave it fuzzy.
            TextOptions.SetTextFormattingMode(d, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(d, TextRenderingMode.Grayscale);
        }
        if (!double.IsNaN(b.Line)) d.SetCurrentValue(LineDp, b.Line * Zoom);
    }

    // the size drawn at a zoom: icons land on whole pixels, so their strokes stay crisp
    // On ordinary screens text is also drawn at a whole pixel size (Crisp): WPF draws the letters at the rounded size anyway,
    // and laid out for a fraction smaller, the tails of g, p and y would be cut off.
    static double Scaled(double size, double zoom, bool icon) => icon || PixelText ? Math.Round(size * zoom) : size * zoom;
    public static readonly bool PixelText = GetDpiForSystem() < 144;   // ordinary screens (under 150%), as in Crisp
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForSystem();

    // Text the way Chromium draws it on Windows: on ordinary screens (under 150%) glyphs are hinted onto whole pixels with
    // ClearType, which is what makes small text sharp; on high-DPI screens the true shapes with grayscale edges look best.
    public static void Crisp(Window w)
    {
        var dense = VisualTreeHelper.GetDpi(w).DpiScaleX < 1.5;
        TextOptions.SetTextFormattingMode(w, dense ? TextFormattingMode.Display : TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(w, dense ? TextRenderingMode.ClearType : TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(w, TextHintingMode.Fixed);
        RenderOptions.SetClearTypeHint(w, ClearTypeHint.Enabled);   // keeps ClearType inside layered (transparent) windows too
    }

    static bool IsIcon(DependencyObject d) =>
        d.GetValue(System.Windows.Documents.TextElement.FontFamilyProperty) is System.Windows.Media.FontFamily f && f.Source.Contains("Icons");

    // Icons the way Spotify draws them: two master sizes, 16 px for small ones and 24 px for large ones, so a 2 px stroke stays crisp
    static double IconSize(DependencyObject d, double size)
    {
        if (!IsIcon(d)) return size;
        // snap only icons already near a master size; small glyphs (×, chevrons) and mid sizes keep theirs
        return size is >= 14 and <= 17 ? 16 : size is >= 22 and <= 26 ? 24 : Math.Round(size);
    }

    // a TextBlock's own runs (code spans, footnotes) carry their own sizes
    static void ScaleInlines(System.Windows.Documents.InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            ScaleText(i);
            if (i is System.Windows.Documents.Span s) ScaleInlines(s.Inlines);
        }
    }

    static void ScaleElement(DependencyObject d)
    {
        if (d is System.Windows.Controls.Control or System.Windows.Controls.TextBlock or System.Windows.Controls.Page) ScaleText(d);
        // only a TextBlock built from runs has runs to scale: reading Inlines of a plain-Text one turns it into rich text and
        // lays it out again, which, on every size change of every TextBlock, stalled whole pages for seconds
        if (d is System.Windows.Controls.TextBlock tb && tb.ReadLocalValue(System.Windows.Controls.TextBlock.TextProperty) == DependencyProperty.UnsetValue
            && System.Windows.Data.BindingOperations.GetBindingExpressionBase(tb, System.Windows.Controls.TextBlock.TextProperty) == null)
            ScaleInlines(tb.Inlines);
    }

    // Zoom changed: every open window, all the way down
    public static void Rescale()
    {
        static void Walk(DependencyObject d)
        {
            ScaleElement(d);
            for (int i = 0, n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i < n; i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }
        foreach (Window w in Current.Windows) Walk(w);
    }

    // every line's weight: exactly two screen pixels at the current display scaling and text size (set by MainWindow.ApplyZoom),
    // snapped, so neighbouring lines can't land as 2 px here and 3 px there
    public static double Line = 1.5;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Windows.Controls.Border, object?> styled = new();   // done once each

    static void Round(System.Windows.Controls.Border b)
    {
        if (b.TemplatedParent is System.Windows.Controls.ToolTip) { b.CornerRadius = new CornerRadius(6); return; }   // tooltips: a small rounded rectangle, never a pill
        var r = b.CornerRadius;
        var uniform = r.TopLeft == r.TopRight && r.TopLeft == r.BottomLeft && r.TopLeft == r.BottomRight;
        var pill = r.TopLeft >= Math.Min(b.ActualWidth, b.ActualHeight) / 2 - 1;
        if (uniform && r.TopLeft >= 6 && r.TopLeft <= 20 && !pill) { b.CornerRadius = new CornerRadius(Corner); rounded.Add(new(b)); }
    }
    // Keyboard focus: one violet ring for every control (Windows shows focus visuals only while you use the keyboard, so
    // mouse clicks never draw it)
    static readonly Style FocusRing = MakeFocusRing();
    static Style MakeFocusRing()
    {
        var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.0);
        ring.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, 8.0);
        ring.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, 8.0);
        ring.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
        ring.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var style = new Style();
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, new System.Windows.Controls.ControlTemplate { VisualTree = ring }));
        style.Seal();
        return style;
    }

    public static double Corner = 12;   // every card's and panel's rounding (15 in Soft: RNUI br50)
    static readonly List<WeakReference<System.Windows.Controls.Border>> rounded = [];
    static void Reround()
    {
        rounded.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in rounded) if (w.TryGetTarget(out var b)) b.CornerRadius = new CornerRadius(Corner);
    }

    // Soft: every button gives a little under the finger, iOS-style, and springs back
    static bool softPress;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Windows.Controls.Primitives.ButtonBase, ScaleTransform> pressed = new();
    static void Press(object s, bool down)
    {
        if (!softPress || s is not System.Windows.Controls.Primitives.ButtonBase b) return;
        if (!pressed.TryGetValue(b, out var t))
        {
            if (b.RenderTransform != null && b.RenderTransform != Transform.Identity) return;   // the button has its own
            t = new ScaleTransform(1, 1); b.RenderTransform = t; b.RenderTransformOrigin = new Point(0.5, 0.5); pressed.Add(b, t);
        }
        var a = new System.Windows.Media.Animation.DoubleAnimation(down ? 0.95 : 1, TimeSpan.FromMilliseconds(down ? 90 : 260))
        { EasingFunction = down ? new System.Windows.Media.Animation.QuadraticEase() : new System.Windows.Media.Animation.BackEase { Amplitude = 0.5, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        // back up: when the spring settles, the animation lets go, so the button rests at exactly 1:1 (text stays crisp)
        if (!down) a.Completed += (_, _) => { if (!b.IsPressed) { t.BeginAnimation(ScaleTransform.ScaleXProperty, null); t.BeginAnimation(ScaleTransform.ScaleYProperty, null); t.ScaleX = t.ScaleY = 1; } };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, a); t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // A crash leaves its error in Ivo's folder: crash.log keeps them all, last-crash.txt is shown once at the next start
    public static readonly string LastCrash = System.IO.Path.Combine(Store.Dir, "last-crash.txt");
    static void Crashed(Exception? ex)
    {
        if (ex == null) return;
        Store.Fail("Crash", ex.ToString());
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n\n";
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Store.Dir, "crash.log"), text); System.IO.File.WriteAllText(LastCrash, text); } catch { }
    }

    // Swap the theme brushes in place; everything using DynamicResource updates live.
    public static void ApplyTheme(string theme)
    {
        var soft = theme is "soft" or "softdark" or "oled" or "glow";   // AMOLED (true black) and Ivo (aurora colours) share the soft look
        var light = theme == "soft";
        Current.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;   // Fluent controls (combos, sliders, …)
        var c = theme switch
        {
            // Soft: Wix RNUI's foundations (wix.github.io/react-native-ui-lib): grey70 canvas, white cards, grey60 outlines,
            // grey10 text, grey30 secondary; dark: RNUI "dark" canvas, grey1 / grey5 surfaces, grey80 text
            "soft" => new[] { "#D9DEE6", "#FFFFFF", "#E8ECF2", "#00FFFFFF", "#5F6973", "#1A2530", "#E8ECF2", "#E9EDF2", "#F0F2F6", "#6613191E" },   // canvas, white panels, grey cards / inputs: each layer a clear step apart
            "softdark" => new[] { "#0B0F12", "#151C22", "#26313B", "#00000000", "#A6ACB1", "#F8F9FA", "#222C36", "#2A3540", "#0F1418", "#B3000000" },   // each layer clearly a step apart
            // AMOLED: Soft on true black; cards a step up from black so they still read, no outlines
            "oled" => new[] { "#000000", "#0B0E11", "#14191D", "#00000000", "#9AA0A6", "#F5F6F8", "#14191D", "#181D22", "#0B0E11", "#CC000000" },   // black canvas, two raised greys for panels and cards
            // high contrast: black, white text, yellow secondary text, white outlines (also used while Windows' high contrast is on)
            "contrast" => new[] { "#000000", "#000000", "#000000", "#FFFFFF", "#FFFF00", "#FFFFFF", "#000000", "#000000", "#000000", "#E6000000" },
            // Ivo: the soft look in Aurora's deep violet-black
            _ => new[] { "#06060A", "#0F0F19", "#1E1E2E", "#00000000", "#8B8BA3", "#E9E9F2", "#1A1A29", "#232335", "#0A0A11", "#B3000000" },
        };
        string[] keys = ["Bg", "Panel", "Glass", "GlassStroke", "Dim", "Fg", "InputBg", "PopupBg", "CodeBg", "Backdrop"];
        for (int i = 0; i < keys.Length; i++)
            Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c[i]));
        Current.Resources["Separator"] = Current.Resources["GlassStroke"];   // dividers: the outline colour, except in Soft

        Brush Hex(string h) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        // the edge of floating panels (Inbox, reader, planner, dialogs): on OLED black a soft violet line, so they stand apart
        Current.Resources["EdgeStroke"] = theme == "oled" ? new SolidColorBrush(Color.FromArgb(0xE6, 0xA7, 0x8B, 0xFA)) : Current.Resources["GlassStroke"];
        // outlines that should be seen without shouting: a solid dark grey with a cool tint on the dark themes (white was the
        // brightest thing on screen), a soft grey on Light. The panel you're working in turns purple instead (CodeStroke).
        var quiet = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? "#D0D0D8" : "#3A3A42"));
        Current.Resources["CodeIdleStroke"] = quiet;
        Current.Resources["CardStroke"] = theme == "oled" ? quiet : Current.Resources["GlassStroke"];   // the Inbox's cards
        // Windows high contrast: flat dark blue accents, no decorations, white edges (the other themes set their own below)
        var contrast = theme == "contrast";
        Current.Resources["DecorVis"] = contrast ? Visibility.Collapsed : Visibility.Visible;
        Current.Resources["ShimmerFill"] = Current.Resources["SegmentOn"] = Current.Resources["AccentGrad"] = Hex("#00008B");
        if (contrast) { Current.Resources["EdgeStroke"] = Hex("#FFFFFF"); Current.Resources["CardStroke"] = Hex("#FFFFFF"); }
        if (soft)
        {
            // RNUI primary: violet30 (violet40 → 30 in dark, so it stays bright enough on the dark canvas)
            Current.Resources["AccentGrad"] = Current.Resources["SegmentOn"] = Current.Resources["ShimmerFill"] = light ? Grad("#5A48F5", "#4633E9") : Grad("#8579FF", "#5A48F5");
            Current.Resources["CodeIdleStroke"] = Current.Resources["EdgeStroke"] = Current.Resources["CardStroke"] = Current.Resources["GlassStroke"];   // borderless
            Current.Resources["Separator"] = Hex(light ? "#CDD4DD" : theme == "oled" ? "#262E35" : "#2C3843");   // but list and section dividers stay, as iOS grouped lists
            if (theme == "oled") Current.Resources["EdgeStroke"] = Current.Resources["Separator"];   // floating panels on black need an edge
            if (theme == "glow")   // Ivo keeps Aurora's violet → cyan
            {
                Current.Resources["Separator"] = Current.Resources["EdgeStroke"] = Hex("#24243A");
                Current.Resources["AccentGrad"] = Grad("#7C5CFF", "#4F46E5");
                Current.Resources["SegmentOn"] = Grad("#7C5CFF", "#22D3EE");
                Current.Resources["ShimmerFill"] = Shimmer();
            }
            // checkboxes, toggles, sliders: Ivo's violet, not Windows' accent colour
            var accent = (Color)ColorConverter.ConvertFromString(light ? "#5A48F5" : "#8579FF");
            foreach (var k in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" }) Current.Resources[k] = accent;
            foreach (var k in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush" }) Current.Resources[k] = new SolidColorBrush(accent);
            // drop-downs and flyouts: Ivo's popup colour and dividers, not Fluent's grey acrylic
            foreach (var k in new[] { "AcrylicBackgroundFillColorDefaultBrush", "ComboBoxDropDownBackground", "FlyoutPresenterBackground", "MenuFlyoutPresenterBackground" }) Current.Resources[k] = Current.Resources["PopupBg"];
            foreach (var k in new[] { "ComboBoxDropDownBorderBrush", "SurfaceStrokeColorFlyoutBrush", "SubtleFillColorSecondaryBrush", "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundSelected" }) Current.Resources[k] = Current.Resources["Separator"];
        }
        // the Inbox, To-do, Planner and Starred boxes: one surface that stands off the window, and cards a step inside it
        (Current.Resources["BoxBg"], Current.Resources["BoxCard"]) = theme switch
        {
            "soft" => (Hex("#FFFFFF"), Hex("#E8ECF2")),
            "oled" => (Hex("#12171B"), Hex("#1D242A")),
            "glow" => (Hex("#191927"), Hex("#25253A")),
            "contrast" => (Hex("#000000"), Hex("#000000")),
            _ => (Hex("#1E2730"), Hex("#2B3641")),
        };
        // selected and hovered rows (settings menu, chat list, menus): solid fills that clearly stand off the panel
        (Current.Resources["Selection"], Current.Resources["Hover"]) = theme switch
        {
            "soft" => (Hex("#CDD5E0"), Hex("#E1E6ED")),
            "oled" => (Hex("#262E35"), Hex("#191F24")),
            "glow" => (Hex("#2E2E48"), Hex("#22223A")),
            "contrast" => (Hex("#1A1A1A"), Hex("#1A1A1A")),
            _ => (Hex("#34414E"), Hex("#283340")),
        };
        Current.Resources["SoftVis"] = soft ? Visibility.Visible : Visibility.Collapsed;
        softPress = soft;
        IconFont = soft ? SoftIcons : MixIcons;
        Current.Resources["IconFont"] = IconFont;
        Current.Resources["TitleIconFont"] = IconFont;
        if (Corner != (soft ? 15 : 12)) { Corner = soft ? 15 : 12; Reround(); }   // RNUI br50
        // the system's own UI font (as macOS / iOS apps use theirs); other themes keep the default
        // Spotify's text reads clearly at small sizes because its font is fairly heavy: body text here is Medium (Segoe UI
        // Variable's 500 weight) instead of Regular, so strokes are thick enough to stay sharp; titles keep their own bold
        foreach (Window w in Current.Windows) if (w is not Splash) { w.FontFamily = soft ? new FontFamily("Segoe UI Variable Text, Segoe UI") : SystemFonts.MessageFontFamily; w.FontWeight = FontWeights.Medium; Crisp(w); }
        Current.Resources["SoftShadowFx"] = null;   // flat: no shadows anywhere, so every edge is clean
        // sections (Settings groups, provider cards): outlined cards, or in Soft a divider line under each
        Current.Resources["TrackBg"] = soft ? Hex(light ? "#D3D9E2" : theme == "oled" ? "#1C2227" : theme == "glow" ? "#161622" : "#222C35") : Current.Resources["Glass"];   // behind segmented choices
        Current.Resources["SectionCorner"] = new CornerRadius(soft ? 0 : Corner);
        Current.Resources["SectionEdge"] = soft ? new Thickness(0, 0, 0, Line) : new Thickness(Line);
        Current.Resources["SectionBg"] = soft ? Brushes.Transparent : Current.Resources["Glass"];
        Current.Resources["SectionStroke"] = soft ? Current.Resources["Separator"] : Current.Resources["GlassStroke"];
        Highlight.Apply(light ? "light" : theme);
    }

    // A decorative loop (shimmer, aurora, blink, orb, sound bars) drawn at a calmer frame rate: a slow drift looks the same at
    // 30 or 20 frames a second, and the window isn't redrawn 60 times a second just for it. Motion that follows you stays at 60.
    // Minimised, WPF's animation clock still ticks ~60 times a second for the loops above, waking the app each time though
    // nothing is drawn. Its time manager can pause (WPF has no public switch for it), so it does while Ivo is minimised.
    // If WPF ever changes inside, this quietly does nothing.
    static bool motionPaused;
    public static void PauseMotion(bool pause)
    {
        if (pause == motionPaused) return;
        motionPaused = pause;
        try
        {
            var context = typeof(System.Windows.Media.Visual).Assembly.GetType("System.Windows.Media.MediaContext")!
                .GetMethod("From", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(null, [Current.Dispatcher]);
            var time = context!.GetType().GetField("_timeManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context);
            time!.GetType().GetMethod(pause ? "Pause" : "Resume", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(time, null);
        }
        catch { }
    }

    public static T Loop<T>(T t, int fps = 30) where T : System.Windows.Media.Animation.Timeline
    {
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(t, fps);
        return t;
    }

    static LinearGradientBrush Shimmer()
    {
        var move = new TranslateTransform(-1, 0);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = move };
        foreach (var (c, o) in new[] { ("#5B3DF5", 0.0), ("#5B3DF5", 0.35), ("#B9A8FF", 0.5), ("#5B3DF5", 0.65), ("#5B3DF5", 1.0) })
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), o));
        move.BeginAnimation(TranslateTransform.XProperty, App.Loop(new System.Windows.Media.Animation.DoubleAnimation(-1, 1, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever }));
        return brush;
    }

    static LinearGradientBrush Grad(string a, string b) => new((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), new Point(0, 0), new Point(1, 1));
}
