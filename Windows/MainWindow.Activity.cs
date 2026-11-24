using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ivo;

// Activity (Core/Activity.cs): the thin rail along the bottom of Ivo (what you're on, your typing speed over the last 10
// minutes, today's screen time; click it for the panel), and the Activity panel: a day or the week, time by kind, by
// hour, and your top apps and sites, each one movable to another kind.
public partial class MainWindow
{
    readonly TextBlock railNow = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock railWpm = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock railTime = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    readonly Ellipse railDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };

    void StartActivity()
    {
        Activity.Overrides = S.ActivityKinds; Activity.On = S.ActivityOn; Activity.Nudges = S.ActivityNudge;
        Activity.Start();
        Activity.Listen();
        foreach (var t in new[] { railNow, railWpm, railTime }) t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        FrameworkElement Item(string glyph, TextBlock text, string tip, Action? click = null)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var b = new Border { Padding = new Thickness(10, 0, 10, 0), Background = Brushes.Transparent, ToolTip = tip, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text } } };
            if (click != null)
            {
                b.Cursor = Cursors.Hand;
                b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Glass");
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
                b.MouseLeftButtonUp += (_, _) => click();
            }
            return b;
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Item("", railWpm, "Your typing speed: words a minute over the last 10 minutes, counting only the time you were typing"));
        right.Children.Add(Item("", railTime, "Screen time today (locked and asleep don't count). Click for Activity", OpenActivity));
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), Children = { railDot, railNow } };
        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right); dock.Children.Add(left);
        StatusRail.Child = dock;
        Activity.Ticked += () => Dispatcher.BeginInvoke(() =>
        {
            DrawRail();
            if (Activity.Nudge() is { } app && !Quiet.Now && portalCall != "on" && groupCall == null)
                NotifyCard.Show("Ivo", $"That's an hour of {app} 👀 back to it?", "", "#22C55E", OpenActivity);
        });
        StatusRail.Visibility = S.ActivityOn ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) => Activity.Save();
        DrawRail();
    }

    void DrawRail()
    {
        var now = Activity.Current;
        railNow.Text = now is { } n ? $"{n.Label} · {n.Kind}" : "Away";
        railDot.Fill = Shell.B(now is { } c ? Activity.Categories.First(x => x.Name == c.Kind).Color : "#5A6472");
        railWpm.Text = Activity.Wpm() is { } w ? $"{w} wpm" : "— wpm";
        railTime.Text = Activity.Span(Activity.DayOf(DateTime.Today).Total);
        if (panelKey == "activity" && activityRedraw?.Invoke() == true) { }
    }

    Func<bool>? activityRedraw;
    int activityBack;
    string activityShow = "All";   // the apps list: everything, or one kind   // the day on show: 0 today, 1 yesterday, … (up to 59)

    void OpenActivity_Click(object s, RoutedEventArgs e) { if (panelKey == "activity") ClosePanel(); else OpenActivity(); }

    // Digital Wellbeing's look: the day's time as a ring (by kind) with the total in its middle, the kinds beside it;
    // ‹ the day › to step through days; the week as bars (tap one for that day); by hour; then the apps and sites, each
    // a round letter, its name and kind (tap the kind to change it) and its time
    void OpenActivity()
    {
        var page = new StackPanel { MaxWidth = 880 };
        Border Card(UIElement child, double bottom = 14)
        {
            var c = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(24, 20, 24, 20), Margin = new Thickness(0, 0, 0, bottom), Child = child };
            c.SetResourceReference(Border.BackgroundProperty, "BoxCard");
            return c;
        }
        TextBlock Text(string t, double size, bool bold = false, bool dim = false)
        {
            var x = new TextBlock { Text = t, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            x.SetResourceReference(TextBlock.ForegroundProperty, dim ? "Dim" : "Fg");
            return x;
        }
        string Color(string kind) => Activity.Categories.FirstOrDefault(c => c.Name == kind).Color ?? "#8B95A3";
        string DayName(int back) => back == 0 ? "Today" : back == 1 ? "Yesterday" : DateTime.Today.AddDays(-back).ToString("dddd, d MMM");

        void Draw()
        {
            page.Children.Clear();
            var day = Activity.DayOf(DateTime.Today.AddDays(-activityBack));
            string KindOf(string app) => Activity.KindOf(app, day.Kinds.GetValueOrDefault(app, "Other"));
            var total = day.Total;

            // ‹ Today ›
            Button Step(string glyph, int to, bool can)
            {
                var b = new Button { Content = glyph, FontFamily = App.IconFont, FontSize = 14, Width = 36, Height = 36, Style = (Style)FindResource("Ghost"), IsEnabled = can, Opacity = can ? 1 : 0.3 };
                b.Click += (_, _) => { activityBack = to; Draw(); };
                return b;
            }
            var title = Text(DayName(activityBack), 16, bold: true); title.Width = 220; title.TextAlignment = TextAlignment.Center;
            page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
                Children = { Step("", activityBack + 1, activityBack < 59), title, Step("", activityBack - 1, activityBack > 0) } });

            // the ring, and the kinds beside it
            var kinds = Activity.Categories.Select(c => (c.Name, c.Color, Sec: day.Apps.Where(a => KindOf(a.Key) == c.Name).Sum(a => a.Value))).Where(k => k.Sec > 0).OrderByDescending(k => k.Sec).ToList();
            var ring = Ring(kinds.Select(k => (k.Sec, k.Color)).ToList(), 210, 22);
            var middle = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var big = Text(Activity.Span(total), 30, bold: true); big.HorizontalAlignment = HorizontalAlignment.Center;
            var cap = Text(activityBack == 0 ? "today" : "that day", 12.5, dim: true); cap.HorizontalAlignment = HorizontalAlignment.Center;
            middle.Children.Add(big); middle.Children.Add(cap);
            var donut = new Grid { Width = 210, Height = 210, Children = { ring, middle } };
            var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 0, 0), MinWidth = 220 };
            foreach (var (name, color, sec) in kinds)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
                var dot = new Ellipse { Width = 12, Height = 12, Fill = Shell.B(color), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                var time = Text(Activity.Span(sec), 13.5, bold: true); var pct = Text($"{100 * sec / Math.Max(1, total)}%", 12, dim: true); pct.Width = 44; pct.TextAlignment = TextAlignment.Right;
                DockPanel.SetDock(dot, Dock.Left); DockPanel.SetDock(pct, Dock.Right); DockPanel.SetDock(time, Dock.Right);
                row.Children.Add(dot); row.Children.Add(pct); row.Children.Add(time); row.Children.Add(Text(name, 13.5));
                legend.Children.Add(row);
            }
            if (kinds.Count == 0) legend.Children.Add(Text("Nothing yet. Ivo counts what's in front while you use your PC.", 13, dim: true));
            // typing, under the kinds
            var wpm = Activity.WpmOn(day);
            var typing = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 8, 0, 0),
                                      Child = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                                          new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 16, Foreground = Shell.B("#A78BFA"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                                          new StackPanel { Children = { Text(wpm is { } v ? $"{v} wpm" : "— wpm", 15, bold: true), Text($"typing speed · {day.Keys:N0} keys", 11.5, dim: true) } } } } };
            typing.SetResourceReference(Border.BackgroundProperty, "InputBg");
            legend.Children.Add(typing);
            page.Children.Add(Card(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { donut, legend } }));

            // the week: a bar a day, the one on show lit; tap one to see it
            var week = new Grid { Height = 150 };
            var days = Enumerable.Range(0, 7).Select(i => 6 - i).ToList();   // six days ago … today
            var most = Math.Max(60, days.Max(b => Activity.DayOf(DateTime.Today.AddDays(-b)).Total));
            foreach (var back in days)
            {
                var d = Activity.DayOf(DateTime.Today.AddDays(-back));
                week.ColumnDefinitions.Add(new ColumnDefinition());
                var on = back == activityBack;
                var bar = new Border { Width = 26, Height = Math.Max(4, 110.0 * d.Total / most), CornerRadius = new CornerRadius(8, 8, 3, 3), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
                if (on) bar.SetResourceReference(Border.BackgroundProperty, "AccentGrad"); else bar.Background = Shell.B("#3A4250");
                var label = Text(DateTime.Today.AddDays(-back).ToString("ddd")[..1], 12, bold: on, dim: !on); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 8, 0, 0);
                var col = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = $"{DayName(back)}: {Activity.Span(d.Total)}" };
                DockPanel.SetDock(label, Dock.Bottom); col.Children.Add(label); col.Children.Add(bar);
                var b2 = back; col.MouseLeftButtonUp += (_, _) => { activityBack = b2; Draw(); };
                Grid.SetColumn(col, week.ColumnDefinitions.Count - 1); week.Children.Add(col);
            }
            var weekTotal = days.Sum(b => Activity.DayOf(DateTime.Today.AddDays(-b)).Total);
            var weekHead = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var avg = Text($"{Activity.Span(weekTotal / 7)} a day on average", 12.5, dim: true); DockPanel.SetDock(avg, Dock.Right);
            weekHead.Children.Add(avg); weekHead.Children.Add(Text("This week", 15, bold: true));
            page.Children.Add(Card(new StackPanel { Children = { weekHead, week } }));

            // by hour: slim rounded bars, stacked by kind
            var hours = new Grid { Height = 90 };
            var peak = Math.Max(60, Enumerable.Range(0, 24).Max(i => day.Hours.Values.Sum(h => h[i])));
            for (int i = 0; i < 24; i++)
            {
                hours.ColumnDefinitions.Add(new ColumnDefinition());
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Width = 10 };
                var tip = new List<string>();
                foreach (var (k, color) in Activity.Categories.Reverse())
                    if (day.Hours.TryGetValue(k, out var h) && h[i] > 0) { stack.Children.Add(new Border { Height = Math.Max(2, 80.0 * h[i] / peak), Background = Shell.B(color) }); tip.Insert(0, $"{k} {Activity.Span(h[i])}"); }
                var holder = new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Width = 10, Child = stack,
                                          ToolTip = $"{i:00}:00 – {(i + 1) % 24:00}:00" + (tip.Count > 0 ? "\n" + string.Join("\n", tip) : "") };
                if (stack.Children.Count == 0) { holder.Height = 4; holder.Background = Shell.B("#2B3341"); }
                holder.SizeChanged += (_, _) => holder.Clip = new RectangleGeometry(new Rect(0, 0, holder.ActualWidth, holder.ActualHeight), 5, 5);
                Grid.SetColumn(holder, i); hours.Children.Add(holder);
            }
            var marks = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            for (var q = 0; q < 4; q++) marks.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (var (t, col) in new[] { ("12 am", 0), ("6 am", 1), ("12 pm", 2), ("6 pm", 3) }) { var x = Text(t, 11.5, dim: true); x.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetColumn(x, col); marks.Children.Add(x); }
            page.Children.Add(Card(new StackPanel { Children = { Text("By hour", 15, bold: true), new Border { Height = 14 }, hours, marks } }));

            // the apps and sites
            var list = new StackPanel();
            // a kind to show (All by default), and that kind's time
            var pick = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
            foreach (var k in new[] { "All" }.Concat(Activity.Categories.Select(c => c.Name))) pick.Items.Add(k);
            pick.SelectedItem = activityShow;
            pick.SelectionChanged += (_, _) => { activityShow = (string)pick.SelectedItem; Draw(); };
            var shown = day.Apps.Where(a => activityShow == "All" || KindOf(a.Key) == activityShow).OrderByDescending(a => a.Value).ToList();
            var sum = Text(activityShow == "All" ? "" : Activity.Span(shown.Sum(a => a.Value)), 13.5, bold: true); sum.Margin = new Thickness(0, 0, 12, 0);
            if (activityShow != "All") sum.Foreground = Shell.B(Color(activityShow));
            var listHead = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { sum, pick } };
            DockPanel.SetDock(pickRow, Dock.Right); listHead.Children.Add(pickRow); listHead.Children.Add(Text("Apps and sites", 15, bold: true));
            list.Children.Add(listHead);
            if (shown.Count == 0) list.Children.Add(Text($"Nothing in {activityShow} {(activityBack == 0 ? "today" : "that day")}.", 13, dim: true));
            foreach (var (app, sec) in shown.Take(30))
            {
                var kind = KindOf(app); var color = Color(kind);
                var face = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Shell.B(color), Margin = new Thickness(0, 0, 14, 0), Opacity = 0.9,
                                        Child = new TextBlock { Text = app.Length > 0 ? app[..1].ToUpper() : "?", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                if (AppIcon(app) is { } icon)   // its own icon on a soft tile (the letter when there's none)
                {
                    face.Opacity = 1; face.CornerRadius = new CornerRadius(12); face.Background = Shell.B("#14FFFFFF");
                    face.Child = new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    RenderOptions.SetBitmapScalingMode((Image)face.Child, BitmapScalingMode.HighQuality);
                }
                var kindLink = new TextBlock { FontSize = 12, Foreground = Shell.B(color), Cursor = Cursors.Hand, ToolTip = "Count it as something else" };
                kindLink.Inlines.Clear(); kindLink.Inlines.Add(new System.Windows.Documents.Run(kind)); kindLink.Inlines.Add(new System.Windows.Documents.Run("  ") { FontFamily = App.IconFont, FontSize = 9 });
                var a = app;
                kindLink.MouseLeftButtonUp += (_, _) =>
                {
                    var menu = new ContextMenu { PlacementTarget = kindLink };
                    foreach (var (k, _) in Activity.Categories)
                    {
                        var item = new MenuItem { Header = k, IsCheckable = true, IsChecked = k == kind };
                        item.Click += (_, _) => { S.ActivityKinds[a] = k; Activity.Overrides = S.ActivityKinds; SaveSettings(); Draw(); };
                        menu.Items.Add(item);
                    }
                    menu.IsOpen = true;
                };
                var time = Text(Activity.Span(sec), 13.5, bold: true);
                var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
                DockPanel.SetDock(face, Dock.Left); DockPanel.SetDock(time, Dock.Right);
                row.Children.Add(face); row.Children.Add(time);
                row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Text(app, 14), kindLink } });
                list.Children.Add(row);
                var line = new Border { Height = 1, Margin = new Thickness(54, 0, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "Separator");
                list.Children.Add(line);
            }
            if (shown.Count > 0) list.Children.RemoveAt(list.Children.Count - 1);
            if (day.Apps.Count > 0) page.Children.Add(Card(list));

            var note = Text("Only app and site names and their times are kept, on this PC, sealed with your app lock. Locked, asleep and away time don't count (videos and meetings do).", 12, dim: true);
            note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None; note.TextAlignment = TextAlignment.Center; note.Margin = new Thickness(20, 4, 20, 10);
            page.Children.Add(note);
        }
        Draw();
        var lastDraw = DateTime.Now;
        activityRedraw = () => { if (activityBack != 0 || DateTime.Now - lastDraw < TimeSpan.FromSeconds(30)) return false; lastDraw = DateTime.Now; Draw(); return true; };   // today's numbers, fresh every half minute while open
        OpenPanel("activity", "Activity", "", "#22C55E", new Grid { Children = { page } });
    }

    // an app's icon from its program file, or a site's favicon (fetched once, then kept on this PC); null: the letter
    readonly Dictionary<string, ImageSource?> icons = [];
    ImageSource? AppIcon(string app)
    {
        if (icons.TryGetValue(app, out var known)) return known;
        ImageSource? got = null;
        try
        {
            if (Activity.SiteOf.TryGetValue(app, out var site))
            {
                var file = System.IO.Path.Combine(Store.Dir, "icons", site + ".png");
                if (System.IO.File.Exists(file)) got = Load(System.IO.File.ReadAllBytes(file));
                else _ = Fetch(app, site, file);
            }
            else if (Activity.IconOf.GetValueOrDefault(app) is { } exe && System.IO.File.Exists(exe) && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } ico)
            {
                using (ico) got = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                got.Freeze();
            }
        }
        catch { }
        return icons[app] = got;

        static ImageSource Load(byte[] png) { var b = new System.Windows.Media.Imaging.BitmapImage(); b.BeginInit(); b.StreamSource = new System.IO.MemoryStream(png); b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze(); return b; }
        async Task Fetch(string name, string domain, string file)
        {
            try
            {
                using var web = new System.Net.Http.HttpClient(Net.Handler()) { Timeout = TimeSpan.FromSeconds(8) };
                var png = await web.GetByteArrayAsync($"https://www.google.com/s2/favicons?domain={domain}&sz=64");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                await System.IO.File.WriteAllBytesAsync(file, png);
                icons.Remove(name);
                if (panelKey == "activity") OpenActivity();   // drawn again with it
            }
            catch { }
        }
    }

    // a ring of coloured arcs, one per part, each in proportion, with a small gap between (an empty ring when there's nothing)
    static Grid Ring(List<(int Sec, string Color)> parts, double size, double thick)
    {
        var g = new Grid { Width = size, Height = size };
        var r = (size - thick) / 2; var c = new Point(size / 2, size / 2);
        var track = new Ellipse { Width = size - thick, Height = size - thick, StrokeThickness = thick, Stroke = Shell.B("#2B3341"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(track);
        var total = parts.Sum(p => p.Sec);
        if (total == 0) return g;
        var gap = parts.Count > 1 ? 3.0 : 0; var at = -90.0;
        foreach (var (sec, color) in parts)
        {
            var sweep = 360.0 * sec / total;
            var draw = Math.Max(0.5, sweep - gap);
            if (draw >= 359.9) { g.Children.Add(new Ellipse { Width = size - thick, Height = size - thick, StrokeThickness = thick, Stroke = Shell.B(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }); break; }
            Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
            var fig = new PathFigure { StartPoint = P(at + gap / 2), IsClosed = false };
            fig.Segments.Add(new ArcSegment(P(at + gap / 2 + draw), new Size(r, r), 0, draw > 180, SweepDirection.Clockwise, true));
            g.Children.Add(new Path { Data = new PathGeometry([fig]), Stroke = Shell.B(color), StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat });
            at += sweep;
        }
        return g;
    }
}
