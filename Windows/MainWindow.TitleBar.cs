using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Ivo;

// The title bar's buttons: their order (Settings → Appearance → Title bar, dragged by the grip), which ones show, and the
// light / dark slider.
public partial class MainWindow
{
    public static readonly string[] TitleDefault = ["todo", "inbox", "stars", "planner", "schedule", "calendar", "news", "activity", "focus", "notes", "ocr", "portal", "spotify", "webhooks", "theme", "settings"];
    // name, line icon and its colour (the same as on the title bar), and whether it can be hidden
    static readonly Dictionary<string, (string Name, string Glyph, string Color, bool CanHide)> TitleInfo = new()
    {
        ["todo"] = ("To-do", "\uE73A", "#34D399", true), ["inbox"] = ("Inbox", "\uE715", "#A78BFA", false), ["stars"] = ("Starred", "\uE734", "#FBBF24", true),
        ["planner"] = ("Planner", "\uE823", "#FB923C", true), ["calendar"] = ("Calendar", "\uE787", "#38BDF8", true), ["schedule"] = ("My schedule", "\uE8C0", "#A78BFA", true), ["news"] = ("News", "", "#38BDF8", true), ["activity"] = ("Activity", "\uE9D2", "#22C55E", true), ["spotify"] = ("Spotify", "\uE767", "#1DB954", true),
        ["focus"] = ("Focus", "", "#F43F5E", true), ["notes"] = ("Notes", "", "#FACC15", true), ["ocr"] = ("Text from screen", "", "#22D3EE", true), ["portal"] = ("Portal", "", "#A78BFA", false), ["webhooks"] = ("Webhooks", "", "#38BDF8", true),
        ["theme"] = ("Light / dark switch", "\uE706", "#FBBF24", true), ["settings"] = ("Settings", "\uE713", "#94A3B8", false),
    };

    FrameworkElement TitleButton(string key) => key switch
    {
        "todo" => BtnTodo, "inbox" => BtnInbox, "stars" => BtnStars, "planner" => BtnPlanner, "calendar" => BtnCalendar, "activity" => BtnActivity, "schedule" => BtnSchedule, "news" => BtnNews,
        "spotify" => BtnSpotify, "focus" => BtnFocus, "notes" => BtnNotes, "webhooks" => BtnHooks, "portal" => BtnPortal, "ocr" => BtnOcr, "theme" => BtnMode, _ => BtnSettings,
    };

    // ---------- where each button sits: the title bar (up to five icons) or the rail; never both ----------
    static readonly string[] RailDefault = ["planner", "schedule", "calendar", "news", "activity", "focus", "notes", "ocr", "portal", "spotify", "webhooks", "settings"];
    const int TitleMax = 5;
    // the slider only fits the title bar; a button you've never placed (new to your saved layout) starts where the default has it
    bool InRail(string key) => key == "portal" || key != "theme" && (S.Rail is { } mine ? mine.Contains(key) || RailDefault.Contains(key) && !S.TitleOrder.Contains(key) : RailDefault.Contains(key));
    int TitleCount() => TitleInfo.Keys.Count(k => k != "theme" && !InRail(k));

    void SetPlace(string key, bool rail)
    {
        var list = (S.Rail ?? [.. RailDefault]).Where(k => k != key).ToList();
        if (rail) list.Add(key);
        S.Rail = list; SaveSettings();
        OrderTitleButtons(); BuildTitleOrderList();
    }

    // the rail marks what's open over the chat
    void MarkRail()
    {
        BtnPlanner.Tag = ScheduleLayer.IsVisible ? "on" : null;
        BtnCalendar.Tag = panelKey == "calendar" ? "on" : null;
        BtnActivity.Tag = panelKey == "activity" ? "on" : null;
        BtnSchedule.Tag = panelKey == "schedule" ? "on" : null;
        BtnNews.Tag = panelKey is "news" or "news-article" ? "on" : null;
        BtnSpotify.Tag = SpotifyLayer.IsVisible ? "on" : null;
        BtnSettings.Tag = SettingsPanel.IsVisible ? "on" : null;
        BtnFocus.Tag = panelKey == "focus" ? "on" : null; BtnNotes.Tag = panelKey == "notes" ? "on" : null; BtnHooks.Tag = panelKey == "webhooks" ? "on" : null; BtnPortal.Tag = panelKey == "portal" || portalStage == "open" ? "on" : null;
    }

    void HookRail()
    {
        foreach (var l in new UIElement[] { ScheduleLayer, SpotifyLayer, SettingsPanel }) l.IsVisibleChanged += (_, _) => MarkRail();
        foreach (var l in Overlays) l.IsVisibleChanged += (_, e) => { if (e.NewValue is true) { OneAtATime(l); FrameAll(); } };
    }

    // one window at a time: opening the Planner, Spotify, Settings, the Inbox… closes whichever was open
    UIElement[] Overlays => [ScheduleLayer, SpotifyLayer, SettingsPanel, WheelLayer, TodoLayer, ReaderLayer, WebLayer, QuickLayer];
    void OneAtATime(UIElement? open)
    {
        foreach (var l in Overlays) if (l != open && l.Visibility == Visibility.Visible) l.Visibility = Visibility.Collapsed;
        if (open != null || starsLayer != null) CloseStars();
        ClosePanel();
    }

    // the saved order, with any button it doesn't know yet added where the default has it
    List<string> TitleOrder()
    {
        var order = S.TitleOrder.Where(TitleInfo.ContainsKey).Distinct().ToList();
        foreach (var k in TitleDefault.Where(k => !order.Contains(k))) order.Insert(Math.Min(Array.IndexOf(TitleDefault, k), order.Count), k);
        return order;
    }

    void OrderTitleButtons()
    {
        var railStyle = (Style)Rail.FindResource("RailBtn");
        foreach (var k in TitleOrder())
        {
            var b = TitleButton(k);
            (b.Parent as Panel)?.Children.Remove(b);
            if (k == "theme") { TitleIcons.Children.Add(b); continue; }
            var rail = InRail(k);
            if (rail) b.Style = railStyle; else b.ClearValue(StyleProperty);   // (null would also block the title bar's own style)
            ((Control)b).FontSize = 20; if (k is "activity" or "schedule") ((Control)b).FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); else b.SetResourceReference(FontFamilyProperty, "TitleIconFont");
            ToolTipService.SetPlacement(b, rail ? System.Windows.Controls.Primitives.PlacementMode.Right : System.Windows.Controls.Primitives.PlacementMode.Bottom);
            if (k == "portal") { RailBottom.Children.Insert(0, b); continue; }   // fixed in the rail, just above Settings
            (rail ? k == "settings" ? RailBottom : RailTop : TitleIcons).Children.Add(b);
        }
        // the rail only shows with something on it; the chat takes its room otherwise
        var any = RailTop.Children.Count + RailBottom.Children.Count > 0;
        Rail.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Body.Margin = new Thickness(any ? 82 : 14, 0, 14, 14);
        FitTitleBar();
    }

    // ---------- overflow: buttons that don't fit (narrow window, or the island in the middle) fold into a "…" menu ----------
    readonly List<FrameworkElement> folded = [];
    Button? moreBtn;
    bool fitHooked;

    void FitTitleBar()
    {
        if (moreBtn == null)
        {
            moreBtn = new Button { Content = "", FontSize = 17, ToolTip = "More", Visibility = Visibility.Collapsed };
            moreBtn.Click += (_, _) => ShowMoreMenu();
        }
        if (!fitHooked)
        {
            fitHooked = true;
            SizeChanged += (_, _) => FitTitleBar();
            ContentRendered += (_, _) => FitTitleBar();
            Island.SizeChanged += (_, _) => FitTitleBar();
            Island.IsVisibleChanged += (_, _) => FitTitleBar();
            TitleLeft.SizeChanged += (_, _) => FitTitleBar();
        }
        foreach (var b in folded) b.Visibility = Visibility.Visible;
        folded.Clear();
        TitleIcons.Children.Remove(moreBtn);
        TitleIcons.Children.Insert(0, moreBtn);
        if (!IsLoaded || TitleIcons.ActualWidth == 0) { moreBtn.Visibility = Visibility.Collapsed; return; }

        var right = TitleIcons.TranslatePoint(new Point(TitleIcons.ActualWidth, 0), this).X;
        var left = Island.IsVisible ? (ActualWidth + Island.ActualWidth) / 2 + 8 : TitleLeft.TranslatePoint(new Point(TitleLeft.ActualWidth, 0), this).X + 8;
        static double Wd(FrameworkElement b) => double.IsNaN(b.Width) ? 40 : b.Width;
        var shown = TitleIcons.Children.OfType<FrameworkElement>().Where(b => b != moreBtn && b.Visibility == Visibility.Visible).ToList();
        var need = shown.Sum(Wd);
        if (need <= right - left) { moreBtn.Visibility = Visibility.Collapsed; return; }
        need += Wd(moreBtn);
        // fold from the left (next to the island)
        foreach (var b in shown)
        {
            if (need <= right - left) break;
            need -= Wd(b); b.Visibility = Visibility.Collapsed; folded.Add(b);
        }
        moreBtn.Visibility = Visibility.Visible;
    }

    void ShowMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = moreBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var b in folded)
        {
            var key = TitleInfo.Keys.First(k => TitleButton(k) == b);
            var (name, glyph, color, _) = TitleInfo[key];
            var item = new MenuItem
            {
                Header = key == "incognito" ? IncognitoButton.ToolTip : name,
                Icon = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 16, Foreground = Shell.B(color) },
            };
            item.Click += (_, _) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------- the light / dark slider ----------
    void DrawModeSwitch(bool animate)
    {
        var dark = S.Theme != "soft";
        ModeIcon.Text = dark ? "\uE708" : "\uE706";
        ModeIcon.Foreground = Shell.B(dark ? "#7C6CF5" : "#F59E0B");
        // a diagonal gradient track: mint → sky by day, violet → indigo by night
        Color C(string h) => (Color)ColorConverter.ConvertFromString(h);
        ModeTrack.Background = dark ? new LinearGradientBrush(C("#8B7CFF"), C("#3B2FB8"), new Point(0, 0), new Point(1, 1))
                                    : new LinearGradientBrush(C("#6EE7B7"), C("#5B8DEF"), new Point(0, 0), new Point(1, 1));
        // the travel is measured, so the knob ends flush with the track at any text size or DPI
        var to = dark ? Math.Max(0, (ModeTrack.ActualWidth > 0 ? ModeTrack.ActualWidth : ModeTrack.Width) - ModeTrack.Padding.Left - ModeTrack.Padding.Right - ModeKnob.Width) : 0;
        ModeKnobMove.BeginAnimation(TranslateTransform.XProperty, animate
            ? new DoubleAnimation(to, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation(to, TimeSpan.Zero));
        BtnMode.ToolTip = dark ? "Switch to light" : "Switch to dark";
    }

    // Spotify's mark (as on the title bar): a green disc, three arcs
    static Viewbox SpotifyMark(double size)
    {
        var c = new Canvas { Width = 24, Height = 24 };
        c.Children.Add(new System.Windows.Shapes.Ellipse { Width = 24, Height = 24, Fill = Shell.B("#1DB954") });
        foreach (var (d, w) in new[] { ("M5.8,9.1 C10.2,7.8 14.9,8.2 18.4,10.3", 2.3), ("M6.7,12.6 C10.1,11.6 13.8,12 16.6,13.6", 1.9), ("M7.4,15.9 C10,15.2 12.8,15.5 15,16.7", 1.6) })
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Stroke = Brushes.Black, StrokeThickness = w, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return new Viewbox { Width = size, Height = size, Child = c, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    // ---------- Settings: the reorderable list ----------
    const string TitleHelpText = "Drag by the grip to reorder, tick to show, and pick where each one sits: the title bar (up to five) or the rail.";

    void BuildTitleOrderList()
    {
        TitleHelp.Text = TitleHelpText;
        TitleOrderList.Children.Clear();
        var order = TitleOrder();
        for (int i = 0; i < order.Count; i++)
        {
            var key = order[i];
            if (key == "portal") continue;   // always in the rail above Settings: nothing to place
            var (name, glyph, color, canHide) = TitleInfo[key];
            var grip = new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), Cursor = Cursors.SizeNS };
            grip.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            FrameworkElement icon = key == "spotify" ? SpotifyMark(18)
                : key == "news" ? NewsIconForTitleBar()
                : new TextBlock { Text = glyph, FontFamily = key == "schedule" ? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") : App.IconFont, FontSize = 18, Foreground = Shell.B(color), Width = 20, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var row = new DockPanel { Height = 44, Background = Brushes.Transparent, Tag = key };
            // fixed columns on the right, the same on every row: the tick (or its empty slot), then Title bar | Rail (or its empty slot)
            FrameworkElement show = new Border { Width = 40 };
            if (canHide)
            {
                var box = new CheckBox { IsChecked = !S.HiddenButtons.Contains(key), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Show" };
                box.Click += (_, _) => { S.HiddenButtons.Remove(key); if (box.IsChecked != true) S.HiddenButtons.Add(key); SaveSettings(); ApplyTitleButtons(); };
                show = new Border { Width = 40, Child = box };
            }
            DockPanel.SetDock(show, Dock.Right); row.Children.Add(show);
            {
                // where it sits: Title bar | Rail, one segmented control (the title bar holds five; a full one sends you to the rail first)
                var place = new StackPanel { Orientation = Orientation.Horizontal };
                var seg = new Border { Width = 176, Height = 32, CornerRadius = new CornerRadius(16), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Child = place,
                                       Visibility = key == "theme" ? Visibility.Hidden : Visibility.Visible };
                seg.SetResourceReference(Border.BackgroundProperty, "InputBg");
                DockPanel.SetDock(seg, Dock.Right); row.Children.Add(seg);
                foreach (var (text, rail) in new[] { ("Title bar", false), ("Rail", true) })
                {
                    if (key == "theme") break;
                    var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = text, IsChecked = InRail(key) == rail, Style = (Style)FindResource("Chip"), Width = 86, Height = 28, Margin = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
                    chip.Click += (_, _) =>
                    {
                        if (InRail(key) == rail) { chip.IsChecked = true; return; }
                        if (!rail && TitleCount() >= TitleMax)
                        {
                            chip.IsChecked = false;
                            TitleHelp.Text = $"The title bar holds {TitleMax} icons. Move one to the rail first.";
                            return;
                        }
                        TitleHelp.Text = TitleHelpText;
                        SetPlace(key, rail);
                    };
                    place.Children.Add(chip);
                }
            }
            row.Children.Add(grip); row.Children.Add(icon); row.Children.Add(label);
            var line = new Border { Height = 1, Margin = new Thickness(76, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Visibility = i < order.Count - 1 ? Visibility.Visible : Visibility.Hidden };
            line.SetResourceReference(Border.BackgroundProperty, "Separator");
            var cell = new Border { Child = new Grid { Children = { row, line } }, CornerRadius = new CornerRadius(12), RenderTransform = new TranslateTransform(), RenderTransformOrigin = new Point(0.5, 0.5) };
            grip.MouseLeftButtonDown += (_, e) => StartDrag(cell, e);
            TitleOrderList.Children.Add(cell);
        }
    }

    // drag a row by its grip: it lifts (shadow, a touch bigger), the others slide out of its way, and it drops into place
    void StartDrag(Border cell, MouseButtonEventArgs e)
    {
        var rows = TitleOrderList.Children.Cast<Border>().ToList();
        var from = rows.IndexOf(cell);
        var h = cell.ActualHeight;
        var startY = e.GetPosition(TitleOrderList).Y;
        var to = from;
        var move = (TranslateTransform)cell.RenderTransform;
        Panel.SetZIndex(cell, 10);
        cell.SetResourceReference(Border.BackgroundProperty, "PopupBg");
        cell.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.25 };
        cell.LayoutTransform = new ScaleTransform(1.02, 1.02);
        static void Slide(TranslateTransform t, double y) => t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });

        void Move(object? s, MouseEventArgs m)
        {
            var dy = m.GetPosition(TitleOrderList).Y - startY;
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.Y = Math.Clamp(dy, -from * h, (rows.Count - 1 - from) * h);
            to = Math.Clamp((int)Math.Round(from + move.Y / h), 0, rows.Count - 1);
            for (int i = 0; i < rows.Count; i++)
            {
                if (i == from) continue;
                var shift = from < to && i > from && i <= to ? -h : from > to && i < from && i >= to ? h : 0;
                Slide((TranslateTransform)rows[i].RenderTransform, shift);
            }
        }
        void Up(object? s, MouseButtonEventArgs m)
        {
            TitleOrderList.MouseMove -= Move; TitleOrderList.MouseLeftButtonUp -= Up; TitleOrderList.LostMouseCapture -= Lost;
            TitleOrderList.ReleaseMouseCapture();
            Drop();
        }
        void Lost(object? s, MouseEventArgs m) => Up(s, null!);
        void Drop()
        {
            var order = rows.Select(r => (string)((DockPanel)((Grid)r.Child).Children[0]).Tag).ToList();
            var key = order[from]; order.RemoveAt(from); order.Insert(to, key);
            S.TitleOrder = order; SaveSettings();
            OrderTitleButtons();
            BuildTitleOrderList();
        }
        TitleOrderList.CaptureMouse();
        TitleOrderList.MouseMove += Move; TitleOrderList.MouseLeftButtonUp += Up; TitleOrderList.LostMouseCapture += Lost;
        e.Handled = true;
    }
}
