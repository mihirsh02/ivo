using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// The Inbox: a floating window ¾ the size of Ivo's where the cards stand on a drum. The front card is
// full size; the ones above and below tilt back, shrink and fade as they roll round. Scroll, drag or ↑ ↓ to turn it;
// it coasts and settles on a card. Click a side card to bring it to the front, the front card to read it.
public partial class MainWindow
{
    List<InboxItem> wheelItems = [];
    readonly Dictionary<string, FrameworkElement> wheelCards = [];
    double wheelPos, wheelVelocity;
    bool wheelDragging;
    Point wheelLast; DateTime wheelLastTime; double wheelMoved;
    const double WheelStep = 26;   // degrees between cards on the drum

    void OpenWheel()
    {
        WheelLayer.Visibility = Visibility.Visible;
        SizeWheel();
        DrawBell();
        wheelPos = 0;
        FillWheel();
        WheelCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        ((ScaleTransform)WheelCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase() });
        ((ScaleTransform)WheelCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase() });
        WheelCanvas.Focus();
    }

    void CloseWheel_Click(object s, RoutedEventArgs e)
    {
        StopWheelCoast(); WheelLayer.Visibility = Visibility.Collapsed;
        if (Inbox.All.Any(i => !i.Read)) { foreach (var i in Inbox.All) i.Read = true; Inbox.Save(); }   // seen now
        UpdateInboxBadge();
    }
    void WheelPrev_Click(object s, RoutedEventArgs e) => WheelSnap(wheelTarget - 1);
    void WheelNext_Click(object s, RoutedEventArgs e) => WheelSnap(wheelTarget + 1);
    void WheelCanvas_SizeChanged(object s, SizeChangedEventArgs e) => LayoutWheel();

    // ¾ of the window, centred
    // Every window over the chat (Settings, Planner, Spotify, Inbox, To-do, reader, web, Starred) is one frame: the app's own
    // shape scaled down, centred under the title bar, with the same corners and edge.
    // four sizes:
    //   large: the app's shape at 80% (Settings, Planner, Spotify, Inbox, the model store)
    //   medium: the app's shape at 65% (News)
    //   small: the app's shape at half (Calendar, Starred, Webhooks, the how-to guides)
    //   tall:  a narrow column, 80% high, a third as wide (Focus, Notes, To-do, Portal)
    public enum Fit { Large, Medium, Small, Tall }
    static Fit FitFor(string? key) => key switch { "focus" or "notes" or "todo" or "portal" or "ivo" or "schedule" or "news-article" => Fit.Tall, "news" => Fit.Medium, "calendar" or "stars" or "webhooks" => Fit.Small, { } k when k.StartsWith("guide-") => Fit.Small, _ => Fit.Large };

    Size FrameSize(Fit fit = Fit.Large)
    {
        var room = (ActualHeight - 44) - 32;   // under the title bar, with a margin
        return fit switch
        {
            Fit.Small => new(Math.Max(440, ActualWidth * 0.5), Math.Max(420, Math.Min(room, ActualHeight * 0.5))),
            Fit.Medium => new(Math.Max(560, ActualWidth * 0.65), Math.Max(480, Math.Min(room, ActualHeight * 0.7))),
            Fit.Tall => new(Math.Clamp(ActualWidth * 0.33, 420, 520), Math.Max(420, Math.Min(room, ActualHeight * 0.8))),
            _ => new(Math.Max(480, ActualWidth * 0.8), Math.Max(380, Math.Min(room, ActualHeight * 0.8))),
        };
    }

    void Frame(FrameworkElement card, Fit fit = Fit.Large, bool flexible = false)
    {
        var f = FrameSize(fit);
        card.Width = f.Width; card.MaxWidth = double.PositiveInfinity;
        // flexible: as tall as what's in it, up to the frame's height (then its own list scrolls)
        if (flexible) { card.Height = double.NaN; card.MaxHeight = f.Height; } else { card.Height = f.Height; card.MaxHeight = double.PositiveInfinity; }
        card.Margin = new Thickness(0); card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
        if (card is Border b)
        {
            b.CornerRadius = new CornerRadius(18); b.BorderThickness = new Thickness(1); b.ClipToBounds = true;
            b.SetResourceReference(Border.BackgroundProperty, "BoxBg"); b.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");   // the cards inside (BoxCard) sit a step above it
        }
    }

    void FrameAll()
    {
        foreach (var c in new FrameworkElement[] { SettingsCard, PlannerCard, ReaderCard, WebCard }) Frame(c);
        Frame(TodoCard, Fit.Tall, flexible: true);
        Frame(SpotifyHost); SpotifyHost.ClearValue(Border.BackgroundProperty); SpotifyHost.BorderThickness = new Thickness(0);   // Spotify draws its own surface
        if (starsLayer?.Children.OfType<Border>().LastOrDefault() is { } star) Frame(star, Fit.Small, flexible: true);
        if (panelLayer?.Children.OfType<Border>().LastOrDefault() is { } panel) Frame(panel, FitFor(panelKey), flexible: true);
    }

    void SizeWheel()
    {
        FrameAll();
        if (floating == WheelLayer && popout != null) { WheelCard.Width = popout.Width; WheelCard.Height = popout.Height; return; }
        if (!WheelLayer.IsVisible) return;
        Frame(WheelCard);   // the Inbox: the large window
    }

    void WheelSearch_Changed(object s, TextChangedEventArgs e) { if (WheelLayer.IsVisible) { wheelPos = 0; FillWheel(); } }
    void WheelPick_Changed(object s, SelectionChangedEventArgs e) { if (!loading && WheelLayer.IsVisible) { wheelPos = 0; FillWheel(); } }

    string wheelFilter = "Everything";
    static readonly string[] WheelSorts = ["Needs you first", "Newest first", "Oldest first", "Unread first", "Starred first"];

    // the header's four icons: show that kind (To-do has no results in the inbox, so it opens the list)
    void InboxKind_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button { Tag: string kind }) return;
        if (kind == "To-do") { CloseWheel_Click(s, e); OpenPlanner("todo"); return; }
        wheelFilter = kind; wheelPos = 0; FillWheel();
    }

    void FillWheel()
    {
        WheelSearchHint.Visibility = WheelSearch.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        var q = WheelSearch.Text.Trim();
        var all = Inbox.All;
        // chips in three groups: status, kind, then the topics of daily-task results; time is its own picker
        var groups = new List<List<(string Name, string Icon, string Color)>>
        {
            new() { ("Everything", "", "#A78BFA"), ("Unread", "•", "#A78BFA"), ("Starred", "", "#F2B84B") },
            new() { ("Reminders", "", "#F2B84B"), ("Daily tasks", "", "#FB923C"), ("Dev tasks", "", "#58A6FF") },
            all.Where(i => i.Kind == "daily").Select(i => Category(i.Category)).DistinctBy(c => c.Name).Select(c => (c.Name, c.Icon, c.Color)).ToList(),
        };
        if (all.Any(i => i.Kind == "briefing")) groups[1].Add(("Briefings", "", "#FB923C"));
        if (all.Any(i => i.Kind == "later" && !i.Done)) groups[1].Add(("Read later", "", "#3FB950"));
        if (!groups.Any(g => g.Any(c => c.Name == wheelFilter))) wheelFilter = "Everything";
        WheelFilters.Children.Clear();
        foreach (var g in groups.Where(g => g.Count > 0))
        {
            if (WheelFilters.Children.Count > 0)
                WheelFilters.Children.Add(new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 12, 6), VerticalAlignment = VerticalAlignment.Center, Background = (Brush)FindResource("Dim"), Opacity = 0.4 });
            foreach (var (name, icon, color) in g)
            {
                var label = new TextBlock();
                label.Inlines.Add(new System.Windows.Documents.Run(icon) { FontFamily = icon == "•" ? new FontFamily("Segoe UI") : App.IconFont, Foreground = Shell.B(color) });
                label.Inlines.Add(new System.Windows.Documents.Run("  " + name));
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6), IsChecked = wheelFilter == name };
                chip.Click += (_, _) => { wheelFilter = name; wheelPos = 0; FillWheel(); };
                WheelFilters.Children.Add(chip);
            }
        }
        loading = true;
        if (WheelSort.ItemsSource == null) { WheelSort.ItemsSource = WheelSorts; WheelSort.SelectedIndex = 0; }
        if (WheelWhen.ItemsSource == null) { WheelWhen.ItemsSource = new[] { "Any time", "Today", "This week" }; WheelWhen.SelectedIndex = 0; }
        loading = false;
        var filter = wheelFilter;
        bool Match(InboxItem i) => filter switch
        {
            "Everything" => i.Kind != "later" || !i.Done, "Starred" => i.Starred, "Unread" => !i.Read,
            "Read later" => i.Kind == "later" && !i.Done, "Reminders" => i.Kind == "reminder", "Daily tasks" => i.Kind is "daily" or "briefing", "Briefings" => i.Kind == "briefing", "Dev tasks" => i.Kind == "dev",
            var c => i.Kind == "daily" && Category(i.Category).Name == c,
        } && ((WheelWhen.SelectedItem as string) switch { "Today" => i.At.Date == DateTime.Today, "This week" => i.At > DateTime.Today.AddDays(-6), _ => true })
          && (q == "" || $"{i.Title} {i.Text} {i.Category}".Contains(q, StringComparison.OrdinalIgnoreCase));
        var shown = all.Where(Match);
        wheelItems = ((WheelSort.SelectedItem as string) switch
        {
            "Newest first" => shown.OrderByDescending(i => i.At),
            "Oldest first" => shown.OrderBy(i => i.At),
            "Unread first" => shown.OrderBy(i => i.Read).ThenByDescending(i => i.At),
            "Starred first" => shown.OrderByDescending(i => i.Starred).ThenByDescending(i => i.At),
            _ => shown.OrderByDescending(i => i.Kind == "reminder" && !i.Done).ThenByDescending(i => i.At),   // reminders waiting, then newest
        }).ToList();
        InboxSummary.Text = all.Count == 0 ? "Nothing yet" : Inbox.Unread is > 0 and var n ? $"{n} new" : "All caught up";
        wheelCards.Clear(); wheelHeights.Clear(); WheelCanvas.Children.Clear();
        wheelPos = Math.Clamp(wheelPos, 0, Math.Max(0, wheelItems.Count - 1));
        wheelTarget = Math.Round(wheelPos); wheelSpeed = 0;
        WheelEmpty.Text = all.Count == 0 ? "Nothing here yet. Reminders, your morning briefing and task results land here." : "Nothing matches";
        WheelEmptyBox.Visibility = wheelItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutWheel();
    }

    // a card for the wheel: the Inbox card, on its own surface, folded to the wheel's height
    FrameworkElement WheelCardFor(InboxItem i)
    {
        if (wheelCards.TryGetValue(i.Id, out var el)) return el;
        var content = InboxCard(i);
        var surface = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(20, 16, 20, 14), BorderThickness = new Thickness(1), Child = content,
                                   MaxHeight = 320, ClipToBounds = true, RenderTransformOrigin = new Point(0.5, 0.5), Cursor = Cursors.Hand, IsHitTestVisible = false };
        // (no drop shadow: an Effect would render the card's text blurry)
        surface.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        surface.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
        surface.RenderTransform = new ScaleTransform();
        surface.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 0, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        wheelCards[i.Id] = surface;
        WheelCanvas.Children.Add(surface);
        return surface;
    }

    // every visible card on the drum: its angle from the front sets height, size, tilt (squash), fade, blur and depth order.
    // Runs every frame while turning, so it only moves things: each card is measured once (wheelHeights) and keeps one transform.
    readonly Dictionary<string, double> wheelHeights = [];
    double wheelCardWidth;

    int wheelTicked;
    void LayoutWheel()
    {
        Magnet.Passed(ref wheelTicked, wheelPos);
        WheelCount.Text = wheelItems.Count == 0 ? "0" : $"{Math.Clamp((int)Math.Round(wheelPos) + 1, 1, wheelItems.Count)} of {wheelItems.Count}";
        var w = WheelCanvas.ActualWidth; var h = WheelCanvas.ActualHeight;
        if (w == 0 || wheelItems.Count == 0) return;
        var cardWidth = Math.Min(720, w - 80);
        if (cardWidth != wheelCardWidth) { wheelCardWidth = cardWidth; wheelHeights.Clear(); }
        var radius = h / 2 - 40;
        var front = (int)Math.Round(wheelPos);
        var lo = Math.Max(0, front - 4); var hi = Math.Min(wheelItems.Count - 1, front + 4);
        foreach (var (id, card) in wheelCards)
            if (wheelItems.FindIndex(x => x.Id == id) is var n && (n < lo || n > hi)) card.Visibility = Visibility.Hidden;
        for (int i = lo; i <= hi; i++)
        {
            var angle = (i - wheelPos) * WheelStep;
            var card = WheelCardFor(wheelItems[i]);
            if (Math.Abs(angle) >= 88) { card.Visibility = Visibility.Hidden; continue; }
            if (!wheelHeights.TryGetValue(wheelItems[i].Id, out var ch))
            {
                card.Width = cardWidth;
                card.Measure(new Size(cardWidth, double.PositiveInfinity));
                wheelHeights[wheelItems[i].Id] = ch = Math.Min(card.DesiredSize.Height, 320);
            }
            var rad = angle * Math.PI / 180;
            var depth = Math.Cos(rad);                       // 1 at the front, 0 at the side of the drum
            var scale = 0.62 + 0.38 * depth;
            card.Visibility = Visibility.Visible;
            // the front card, once settled, takes clicks itself: its buttons work (copy, read aloud, star, delete, Done/Snooze)
            card.IsHitTestVisible = i == (int)Math.Round(wheelTarget) && Math.Abs(wheelPos - wheelTarget) < 0.05 && !wheelDragging;
            Canvas.SetLeft(card, (w - cardWidth) / 2);
            Canvas.SetTop(card, h / 2 + Math.Sin(rad) * radius - ch / 2);
            var t = (ScaleTransform)card.RenderTransform;
            t.ScaleX = scale; t.ScaleY = scale * (0.55 + 0.45 * depth);   // squashed vertically as it tilts away
            // the card itself stays solid (so the ones behind never show through), its content fades with depth,
            // and the very edge of the drum fades away entirely
            card.Opacity = Math.Min(1, depth * 3);
            if (card is Border { Child: UIElement inside }) inside.Opacity = Math.Pow(depth, 1.4);
            // the ones behind go soft; the front one stays sharp
            var blur = Math.Min(Math.Abs(i - wheelPos), 3) * 2.2;
            ((System.Windows.Media.Effects.BlurEffect)card.Effect).Radius = blur < 0.3 ? 0 : blur;
            Panel.SetZIndex(card, (int)(depth * 1000));
        }
    }

    // ---------- turning it: one spring ----------
    // The wheel shows wheelPos and aims at wheelTarget. Every frame a critically damped spring pulls the one toward the
    // other (time-based, so the same at any frame rate). Scroll notches, keys, clicks and flicks only move the target, so
    // the motion never restarts or stutters; a drag moves the wheel directly and a flick lands as far as its speed carries.
    double wheelTarget, wheelSpeed, wheelScroll;   // target (cards), speed (cards/s), part-notches from touchpads
    bool wheelSpringing;
    DateTime wheelFrame;
    const double WheelStiffness = 260;              // higher = snappier; damping is critical (2·√k), so no wobble

    void WheelSnap(double target)
    {
        wheelTarget = Math.Clamp(Math.Round(target), 0, Math.Max(0, wheelItems.Count - 1));
        StartSpring();
    }

    void StartSpring()
    {
        if (wheelSpringing) return;
        wheelSpringing = true; wheelFrame = DateTime.Now;
        CompositionTarget.Rendering += WheelSpring;
    }

    void StopWheelCoast() { if (!wheelSpringing) return; wheelSpringing = false; wheelSpeed = 0; CompositionTarget.Rendering -= WheelSpring; }

    void WheelSpring(object? s, EventArgs e)
    {
        var now = DateTime.Now; var dt = Math.Min(0.05, (now - wheelFrame).TotalSeconds); wheelFrame = now;
        if (!wheelDragging)
        {
            var k = WheelStiffness; var c = 1.7 * Math.Sqrt(k);   // a touch under critical: it clicks into place
            for (int i = 0; i < 4; i++)   // a few small steps per frame keep it stable
            {
                var h = dt / 4;
                wheelSpeed += (k * (wheelTarget - wheelPos) - c * wheelSpeed) * h;
                wheelPos += wheelSpeed * h;
            }
            if (Math.Abs(wheelTarget - wheelPos) < 0.0015 && Math.Abs(wheelSpeed) < 0.02)
            {
                wheelPos = wheelTarget; LayoutWheel(); StopWheelCoast(); Sounds.Snap(); return;
            }
        }
        LayoutWheel();
    }

    void Wheel_MouseWheel(object s, MouseWheelEventArgs e)
    {
        e.Handled = true;
        wheelScroll -= e.Delta / 120.0;   // a mouse notch is one card; a touchpad's small steps add up
        if (Math.Abs(wheelScroll) < 1) return;
        var step = Math.Truncate(wheelScroll); wheelScroll -= step;
        WheelSnap(wheelTarget + step);
    }

    void Wheel_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.PageDown) { e.Handled = true; WheelSnap(wheelTarget + 1); }
        else if (e.Key is Key.Up or Key.PageUp) { e.Handled = true; WheelSnap(wheelTarget - 1); }
        else if (e.Key == Key.Home) { e.Handled = true; WheelSnap(0); }
        else if (e.Key == Key.End) { e.Handled = true; WheelSnap(wheelItems.Count - 1); }
        else if (e.Key == Key.Enter && wheelItems.Count > 0) { e.Handled = true; OpenReader(wheelItems[(int)wheelTarget]); }
    }

    void Wheel_Down(object s, MouseButtonEventArgs e)
    {
        wheelDragging = true; wheelMoved = 0; wheelLast = e.GetPosition(WheelCanvas); wheelLastTime = DateTime.Now; wheelVelocity = 0;
        WheelCanvas.CaptureMouse(); WheelCanvas.Focus();
        WheelCanvas.LostMouseCapture -= WheelLost; WheelCanvas.LostMouseCapture += WheelLost;
        StartSpring();
    }

    void Wheel_Move(object s, MouseEventArgs e)
    {
        if (!wheelDragging) return;
        var p = e.GetPosition(WheelCanvas);
        var dy = p.Y - wheelLast.Y; wheelMoved += Math.Abs(dy);
        var per = WheelCanvas.ActualHeight / 5;   // pixels per card
        var secs = Math.Max(0.004, (DateTime.Now - wheelLastTime).TotalSeconds);
        var max = Math.Max(0, wheelItems.Count - 1);
        var pos = wheelPos - dy / per;
        // past either end it resists (rubber band) instead of stopping dead
        if (pos < 0 || pos > max) pos = wheelPos - dy / per * 0.35;
        wheelPos = Math.Clamp(Magnet.Pull(pos), -0.6, max + 0.6);   // detents under the finger
        wheelVelocity = wheelVelocity * 0.3 + (-dy / per / secs) * 0.7;   // a rolling average, in cards per second
        wheelLast = p; wheelLastTime = DateTime.Now;
    }

    // the card under the point, by where each card is actually drawn (topmost first); -1 for none
    int CardAt(Point p)
    {
        int best = -1, bestZ = -1;
        for (int i = 0; i < wheelItems.Count; i++)
        {
            if (!wheelCards.TryGetValue(wheelItems[i].Id, out var card) || card.Visibility != Visibility.Visible || !wheelHeights.TryGetValue(wheelItems[i].Id, out var h)) continue;
            var t = (ScaleTransform)card.RenderTransform;
            var cx = Canvas.GetLeft(card) + card.Width / 2; var cy = Canvas.GetTop(card) + h / 2;   // scaled about its centre
            if (Math.Abs(p.X - cx) <= card.Width / 2 * t.ScaleX && Math.Abs(p.Y - cy) <= h / 2 * t.ScaleY && Panel.GetZIndex(card) > bestZ)
                (best, bestZ) = (i, Panel.GetZIndex(card));
        }
        return best;
    }

    // capture taken away mid-drag (Alt+Tab, a window closing): settle, or the spring runs forever
    void WheelLost(object s, MouseEventArgs e)
    {
        if (wheelDragging) { wheelDragging = false; WheelSnap(Math.Round(wheelPos)); }
    }

    void Wheel_Up(object s, MouseButtonEventArgs e)
    {
        if (!wheelDragging) return;
        wheelDragging = false; WheelCanvas.ReleaseMouseCapture();
        if (wheelMoved < 10)   // a click (a little wobble allowed): the front card opens; another card comes to the front
        {
            var hit = CardAt(e.GetPosition(WheelCanvas));
            if (hit < 0) { WheelSnap(Math.Round(wheelPos)); return; }
            if (hit == (int)wheelTarget) { WheelSnap(hit); OpenReader(wheelItems[hit]); }
            else WheelSnap(hit);
            return;
        }
        if ((DateTime.Now - wheelLastTime).TotalMilliseconds > 90) wheelVelocity = 0;   // held still before letting go: no flick
        // the flick: it keeps its speed and lands where that would carry it (hard flicks skip several cards)
        wheelSpeed = wheelVelocity;
        WheelSnap(wheelPos + wheelVelocity * 0.22);
    }
}
