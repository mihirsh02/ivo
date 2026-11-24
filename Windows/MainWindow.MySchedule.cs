using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Your schedule, to look at (you add and change it in the Planner → My schedule): the next seven days as tabs, and the
// chosen day as a timeline: your schedule, reminders and calendar events, in time order, with what's on now lit.
// Ivo's own daily tasks aren't here: they're hers to do, not your plans.
public partial class MainWindow
{
    int scheduleDay;   // 0 today … 6

    void OpenMySchedule_Click(object s, RoutedEventArgs e) { if (panelKey == "schedule") ClosePanel(); else OpenMySchedule(); }

    // a day's things: your schedule, reminders and calendar events, in time order
    List<(TimeSpan At, string Text, string Kind)> DayPlan(DateTime day) =>
        S.Schedules.Where(x => x.Kind == "plan" && x.Falls(day)).Select(x => (x.When.TimeOfDay, x.Text, "plan"))
        .Concat(S.Schedules.Where(x => x.Kind == "reminder" && x.Falls(day)).Select(x => (x.When.TimeOfDay, x.Text, "reminder")))
        .Concat(CalEvents(new DateTime(day.Year, day.Month, 1)).Where(e => e.Start.Date <= day.Date && e.End.Date >= day.Date).Select(e => (e.AllDay ? TimeSpan.Zero : e.Start.TimeOfDay, e.Title, "calendar")))
        .OrderBy(x => x.Item1).ToList();

    void OpenMySchedule()
    {
        var page = new StackPanel { MaxWidth = 760 };
        TextBlock Text(string t, double size, bool bold = false, bool dim = false)
        {
            var x = new TextBlock { Text = t, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            x.SetResourceReference(TextBlock.ForegroundProperty, dim ? "Dim" : "Fg");
            return x;
        }
        void Draw()
        {
            page.Children.Clear();
            // the week: a tab a day, today first
            var tabs = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 18) };
            for (var i = 0; i < 7; i++)
            {
                var d = DateTime.Today.AddDays(i);
                var n = DayPlan(d).Count;
                var on = i == scheduleDay;
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { Text(i == 0 ? "Today" : d.ToString("ddd"), 12, dim: !on), Text(d.Day.ToString(), 18, bold: true) } };
                foreach (TextBlock t in cell.Children) t.HorizontalAlignment = HorizontalAlignment.Center;
                cell.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0), Background = n > 0 ? Shell.B("#A78BFA") : Brushes.Transparent });
                var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(0, 8, 0, 8), Margin = new Thickness(3), Cursor = Cursors.Hand, Child = cell };
                if (on) b.SetResourceReference(Border.BackgroundProperty, "InputBg"); else b.Background = Brushes.Transparent;
                var day = i; b.MouseLeftButtonUp += (_, _) => { scheduleDay = day; Draw(); };
                tabs.Children.Add(b);
            }
            page.Children.Add(tabs);

            var date = DateTime.Today.AddDays(scheduleDay);
            var items = DayPlan(date);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var edit = new Button { Content = "Edit in Planner", Padding = new Thickness(12, 5, 12, 5) };
            edit.Click += (_, _) => { ClosePanel(); OpenPlanner("plan"); };
            DockPanel.SetDock(edit, Dock.Right); head.Children.Add(edit);
            head.Children.Add(new StackPanel { Children = { Text(date.ToString("dddd, d MMMM"), 17, bold: true), Text(items.Count == 0 ? "Nothing planned" : $"{items.Count} thing{(items.Count == 1 ? "" : "s")}", 12.5, dim: true) } });
            page.Children.Add(head);

            if (items.Count == 0)
            {
                page.Children.Add(EmptyState("🗓️", "Nothing planned this day", "Add your classes, gym, meetings and the rest in the Planner → My schedule, and they show here."));
                return;
            }
            // the timeline: a time, a dot on a line, and the thing; what's on now (today, the latest one started) lit
            var now = DateTime.Now.TimeOfDay;
            var current = scheduleDay == 0 ? items.FindLastIndex(x => x.At != TimeSpan.Zero && x.At <= now) : -1;
            for (var i = 0; i < items.Count; i++)
            {
                var (at, text, kind) = items[i];
                var color = kind switch { "plan" => "#A78BFA", "reminder" => "#F2B84B", _ => "#38BDF8" };
                var past = scheduleDay == 0 && at != TimeSpan.Zero && at < now && i != current;
                var time = Text(at == TimeSpan.Zero ? "All day" : DateTime.Today.Add(at).ToString(When.TwelveHour ? "h:mm tt" : "HH:mm"), 13, bold: i == current, dim: i != current);
                time.Width = 76;
                var rail = new Grid { Width = 26 };
                if (i < items.Count - 1) rail.Children.Add(new Border { Width = 2, Margin = new Thickness(0, 14, 0, -14), HorizontalAlignment = HorizontalAlignment.Center, Background = Shell.B("#33A78BFA") });
                rail.Children.Add(new Border { Width = i == current ? 14 : 10, Height = i == current ? 14 : 10, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Background = Shell.B(color) });
                var what = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(6, 0, 0, 10),
                                        Child = new StackPanel { Children = { Text(text, 14, bold: i == current), Text(kind switch { "plan" => "Your schedule", "reminder" => "Reminder", _ => "Calendar" } + (i == current ? " · on now" : ""), 11.5, dim: true) } } };
                what.SetResourceReference(Border.BackgroundProperty, i == current ? "InputBg" : "BoxCard");
                if (i == current) { what.BorderThickness = new Thickness(1); what.BorderBrush = Shell.B(color); }
                var row = new DockPanel { Opacity = past ? .55 : 1 };
                DockPanel.SetDock(time, Dock.Left); DockPanel.SetDock(rail, Dock.Left);
                time.VerticalAlignment = VerticalAlignment.Top; time.Margin = new Thickness(0, 10, 0, 0); rail.Margin = new Thickness(0, 8, 0, 0);
                row.Children.Add(time); row.Children.Add(rail); row.Children.Add(what);
                page.Children.Add(row);
            }
        }
        Draw();
        OpenPanel("schedule", "My schedule", "", "#A78BFA", page);
    }

    // the chat card: today's (or the asked day's) schedule
    InfoCard ScheduleCard()
    {
        var items = DayPlan(DateTime.Today);
        var now = DateTime.Now.TimeOfDay;
        return new("🗓️", "#A78BFA", items.Count == 0 ? "Nothing planned today" : $"{items.Count} thing{(items.Count == 1 ? "" : "s")} today", DateTime.Now.ToString("dddd, d MMM"),
            [.. items.Take(7).Select(x => new InfoRow(x.Text, x.Kind switch { "plan" => "your schedule", "reminder" => "reminder", _ => "calendar" },
                                                      x.At == TimeSpan.Zero ? "all day" : DateTime.Today.Add(x.At).ToString(When.TwelveHour ? "h:mm tt" : "HH:mm"),
                                                      Glyph: x.Kind switch { "plan" => "🗓️", "reminder" => "⏰", _ => "📅" }, Faded: x.At != TimeSpan.Zero && x.At < now))],
            "schedule", () => Task.FromResult<InfoCard?>(ScheduleCard()));
    }
}
