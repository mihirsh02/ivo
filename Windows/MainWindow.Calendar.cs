using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// The title bar's calendar: a month, the picked day's holidays, calendar events and Ivo items (plus its date in your
// region's own calendar when that's on), and buttons to schedule something on it.
public partial class MainWindow
{
    DateTime calMonth, calDay;
    readonly Dictionary<DateTime, List<Calendar.Event>> calCache = [];   // by month: holidays + your calendars

    // the calendar is a window like the others (the rail's or the title bar's button opens it)
    Border? calHost;

    void Calendar_Click(object s, RoutedEventArgs e)
    {
        if (panelKey == "calendar") { ClosePanel(); return; }
        calDay = DateTime.Today; calMonth = new(calDay.Year, calDay.Month, 1);
        calHost = new Border { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        DrawCal();
        OpenPanel("calendar", "Calendar", "", "#38BDF8", calHost);
    }

    List<Calendar.Event> CalEvents(DateTime month)
    {
        if (calCache.TryGetValue(month, out var got)) return got;
        calCache[month] = [];
        _ = Load();
        return [];
        async Task Load()
        {
            var urls = S.CalendarUrls.Append(S.HolidayCalendar).Where(u => u != "");
            calCache[month] = await Calendar.Between(urls, month, month.AddMonths(1));
            if (panelKey == "calendar" && calMonth == month) DrawCal();
        }
    }

    // the holiday calendar's days alone (CalEvents has them mixed in with yours): titles, per month
    readonly Dictionary<DateTime, HashSet<string>> holidayCache = [];
    HashSet<string> Holidays(DateTime month)
    {
        if (holidayCache.TryGetValue(month, out var got)) return got;
        holidayCache[month] = [];
        if (S.HolidayCalendar != "") _ = Load();
        return holidayCache[month];
        async Task Load() => holidayCache[month] = [.. (await Calendar.Between([S.HolidayCalendar], month, month.AddMonths(1))).Select(e => e.Title)];
    }

    static bool OccursOn(Scheduled x, DateTime d) => x.On && (x.When.Date == d || d > x.When.Date && x.Repeat switch
    {
        "daily" => true,
        "weekdays" => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        "weekly" => d.DayOfWeek == x.When.DayOfWeek,
        _ => false,
    });

    TextBlock CalText(string text, string brush = "Fg", double size = 12.5, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    // Settings → Calendar → holidays: Google's public holiday calendar for a country ("" = none)
    static readonly (string Name, string Code)[] HolidayRegions =
    [
        ("None", ""), ("Australia", "australian"), ("Brazil", "brazilian"), ("Canada", "canadian"), ("China", "china"), ("France", "french"),
        ("Germany", "german"), ("India", "indian"), ("Indonesia", "indonesian"), ("Ireland", "irish"), ("Italy", "italian"), ("Japan", "japanese"),
        ("Malaysia", "malaysia"), ("Mexico", "mexican"), ("Netherlands", "dutch"), ("New Zealand", "new_zealand"), ("Pakistan", "pk"),
        ("Philippines", "philippines"), ("Singapore", "singapore"), ("South Africa", "sa"), ("South Korea", "south_korea"), ("Spain", "spain"),
        ("United Arab Emirates", "ae"), ("United Kingdom", "uk"), ("United States", "usa"),
    ];
    static string HolidayUrl(string code) => code == "" ? "" : $"https://calendar.google.com/calendar/ical/en.{code}%23holiday%40group.v.calendar.google.com/public/basic.ics";

    void ShowCalendarSettings()
    {
        loading = true;
        HolidayRegion.ItemsSource = HolidayRegions.Select(r => r.Name).ToList();
        HolidayRegion.SelectedIndex = Math.Max(0, Array.FindIndex(HolidayRegions, r => HolidayUrl(r.Code) == S.HolidayCalendar));
        RegionalCalendarBox.IsChecked = S.RegionalCalendar;
        loading = false;
    }
    void HolidayRegion_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (loading || HolidayRegion.SelectedIndex < 0) return;
        S.HolidayCalendar = HolidayUrl(HolidayRegions[HolidayRegion.SelectedIndex].Code);
        calCache.Clear();
        SaveSettings();
    }
    void RegionalCalendar_Changed(object s, RoutedEventArgs e) { S.RegionalCalendar = RegionalCalendarBox.IsChecked == true; SaveSettings(); }

    // the holiday region's code, from its calendar's address ("indian", "china", …)
    string Region => System.Text.RegularExpressions.Regex.Match(S.HolidayCalendar, @"ical/en\.([^%]+)%23").Groups[1].Value;

    // An event on the day: written as an .ics file and opened, so your calendar app (Outlook, Windows Calendar, or whatever
    // opens .ics files) adds it to whichever calendar you choose there. "Team lunch at 1pm" sets the time; no time: 9 am, 1 hour.
    async void AddEvent(DateTime day)
    {
        if (await AskText($"New event on {When.Day(day, longForm: true)}", "What, and when (for example \"Team lunch at 1pm\")", "", "Add to calendar") is not { Length: > 0 } text) return;
        var (start, title) = When.Parse(text, day.Date) is { } w && w.At.Date == day.Date ? (w.At, w.Text == "" ? text : w.Text) : (day.Date.AddHours(9), text);
        static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        static string Esc(string t) => t.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");
        var ics = string.Join("\r\n", "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Ivo//EN", "BEGIN:VEVENT",
            $"UID:{Guid.NewGuid():N}@ivo", $"DTSTAMP:{Utc(DateTime.Now)}", $"DTSTART:{Utc(start)}", $"DTEND:{Utc(start.AddHours(1))}", $"SUMMARY:{Esc(title)}", "END:VEVENT", "END:VCALENDAR", "");
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Ivo event {Guid.NewGuid():N}.ics");
        await System.IO.File.WriteAllTextAsync(file, ics);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { _ = Notice("No calendar app opened it", ex.Message); }
    }

    void DrawCal()
    {
        var events = CalEvents(calMonth);
        bool Holiday(Calendar.Event ev) => ev.AllDay;   // ponytail: all-day events count as "significant" (holiday feeds are all-day); a separate list per source if that mixes up

        var root = new StackPanel { Width = 300 };
        // month header
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        Button Nav(string glyph, int step)
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Content = glyph, FontSize = 13 };
            b.Click += (_, _) => { calMonth = calMonth.AddMonths(step); DrawCal(); };
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }
        head.Children.Add(Nav("", 1)); head.Children.Add(Nav("", -1));
        var today = new Button { Style = (Style)FindResource("GhostText"), Content = "Today", FontSize = 13 };
        today.Click += (_, _) => { calDay = DateTime.Today; calMonth = new(calDay.Year, calDay.Month, 1); DrawCal(); };
        DockPanel.SetDock(today, Dock.Right); head.Children.Add(today);
        var month = CalText(calMonth.ToString("MMMM yyyy"), size: 14, weight: FontWeights.SemiBold); month.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(month);
        root.Children.Add(head);

        // weekday names + days (Monday first)
        var grid = new UniformGrid { Columns = 7 };
        foreach (var n in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
            grid.Children.Add(new TextBlock { Text = n, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4), Foreground = (Brush)FindResource("Dim") });
        var first = calMonth.AddDays(-(((int)calMonth.DayOfWeek + 6) % 7));
        for (var d = first; d < first.AddDays(42); d = d.AddDays(1))
        {
            var day = d;
            var marked = events.Any(ev => ev.Start.Date <= day && day < (ev.AllDay ? ev.End.Date : ev.End.Date.AddDays(1)) && Holiday(ev)) ;
            var busy = S.Schedules.Any(x => OccursOn(x, day) && x.Repeat == "once") || events.Any(ev => !ev.AllDay && ev.Start.Date == day);
            var num = CalText(day.Day.ToString(), day.Month == calMonth.Month ? "Fg" : "Dim", 12.5, day == DateTime.Today ? FontWeights.Bold : null);
            num.HorizontalAlignment = HorizontalAlignment.Center;
            if (day.Month != calMonth.Month) num.Opacity = 0.5;
            var dot = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
                                                          Fill = marked ? Shell.B("#F2B84B") : busy ? Shell.B("#A78BFA") : Brushes.Transparent };
            var cell = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 5, 0, 3), Margin = new Thickness(1), Cursor = Cursors.Hand,
                                    Background = Brushes.Transparent, Child = new StackPanel { Children = { num, dot } } };
            if (day == calDay) cell.Background = (Brush)FindResource("AccentGrad");
            else if (day == DateTime.Today) cell.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            cell.BorderThickness = new Thickness(day == DateTime.Today && day != calDay ? 1 : 0);
            cell.MouseEnter += (_, _) => { if (day != calDay) cell.SetResourceReference(Border.BackgroundProperty, "Glass"); };
            cell.MouseLeave += (_, _) => { if (day != calDay) cell.Background = Brushes.Transparent; };
            cell.MouseLeftButtonUp += (_, _) => { calDay = day; if (day.Month != calMonth.Month) calMonth = new(day.Year, day.Month, 1); DrawCal(); };
            grid.Children.Add(cell);
        }
        root.Children.Add(grid);

        // the picked day: a heading, then one aligned row per thing (time | what | kind)
        var line = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
        line.SetResourceReference(Border.BackgroundProperty, "Separator");
        root.Children.Add(line);
        root.Children.Add(CalText(calDay.ToString("dddd, d MMMM"), size: 14, weight: FontWeights.SemiBold));
        if (S.RegionalCalendar && RegionalCalendar.Day(calDay, Region) is { } local) { var h = CalText(local, "Dim", 12.5); h.Margin = new Thickness(0, 3, 0, 0); root.Children.Add(h); }
        var dayEvents = (calDay.Month == calMonth.Month ? events : CalEvents(new(calDay.Year, calDay.Month, 1)))
            .Where(ev => ev.Start.Date <= calDay && calDay < (ev.AllDay ? ev.End.Date : ev.End.Date.AddDays(1))).ToList();
        var lines = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Row(string time, string what, string kind, string? tip = null, string timeBrush = "Dim")
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), ToolTip = tip };
            if (kind != "")
            {
                // what it is, as a small pill on the right (never wrapped or cut off)
                var k = CalText(kind, "Dim", 11); k.TextWrapping = TextWrapping.NoWrap;
                var tag = new Border { Child = k, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                tag.SetResourceReference(Border.BackgroundProperty, "InputBg");
                DockPanel.SetDock(tag, Dock.Right); row.Children.Add(tag);
            }
            var t = CalText(time, timeBrush, 13); t.Width = 48; row.Children.Add(t);
            var w = CalText(what, size: 13); w.TextWrapping = TextWrapping.NoWrap; w.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(w);
            lines.Children.Add(row);
        }
        foreach (var ev in dayEvents.Where(Holiday)) Row("Holiday", ev.Title, "");
        foreach (var ev in dayEvents.Where(ev => !Holiday(ev))) Row(ev.Start.ToString("HH:mm"), ev.Title, "event");
        foreach (var x in S.Schedules.Where(x => OccursOn(x, calDay)).OrderBy(x => x.When.TimeOfDay))
        {
            // a readable name: its category, else its first real sentence (prompts often open with "Today's date is {{DATE}}.")
            var name = System.Text.RegularExpressions.Regex.Split(x.Text, @"(?<=[.!?])\s+|\n").Select(l => l.Trim()).FirstOrDefault(l => l != "" && !l.Contains("{{")) ?? x.Text;
            Row(x.When.ToString("HH:mm"), x.Category != "" ? x.Category : name, x.Kind == "task" ? "daily" : x.Kind, x.Text);
        }
        if (lines.Children.Count == 0) lines.Children.Add(CalText("Nothing on this day.", "Dim", 13));
        root.Children.Add(lines);

        // schedule something on it
        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, kind) in new[] { ("Event", "event"), ("Reminder", "reminder"), ("Daily task", "task"), ("Dev task", "dev") })
        {
            var b = new ToggleButton { Style = (Style)FindResource("Chip"), FontSize = 13, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } }, Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) =>
            {
                ClosePanel();
                if (kind == "event") { AddEvent(calDay); return; }
                var at = calDay == DateTime.Today ? DateTime.Now.AddHours(1) : calDay.AddHours(9);
                OpenPlanner(kind);
                SetWhen(new DateTime(at.Year, at.Month, at.Day, at.Hour, 0, 0));
            };
            add.Children.Add(b);
        }
        root.Children.Add(add);
        // your region's own calendar (India: Saka; Pakistan or the UAE: Hijri; China: lunar…), when it keeps one
        var own = RegionalCalendar.NameFor(Region);
        var regional = new CheckBox { IsChecked = S.RegionalCalendar, Margin = new Thickness(0, 10, 0, 0), Content = CalText(own != null ? $"{own} calendar" : "Regional calendar", size: 13),
                                      IsEnabled = own != null, ToolTip = own == null ? "Pick a holiday region with its own calendar in Settings → Calendar" : null };
        regional.Click += (_, _) => { S.RegionalCalendar = regional.IsChecked == true; SaveSettings(); DrawCal(); };
        root.Children.Add(regional);

        // side by side, so it fits without scrolling: the month on the left; on the right what you can add, a line, then the day
        var moved = root.Children.Cast<UIElement>().Skip(2).ToList();   // everything after the month's heading and grid
        foreach (var m in moved) root.Children.Remove(m);
        var side = new StackPanel { MinWidth = 200, Margin = new Thickness(28, 4, 0, 0) };
        side.Children.Add(add); side.Children.Add(regional);
        foreach (var m in moved.Where(m => m != add && m != regional)) side.Children.Add(m);   // the line, the day's heading and what's on it
        add.Margin = new Thickness(0);
        // the month at its own width, the side in exactly what's left (a fixed width ran past the card's edge and cut it off)
        var both = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
        Grid.SetColumn(side, 1);
        both.Children.Add(root); both.Children.Add(side);
        if (calHost != null) calHost.Child = both;
    }
}
