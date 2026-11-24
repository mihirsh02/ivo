using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Settings → Assistant: weather, quiet hours, her mood log (a week of your moods) and her journal of your chats
// (Core/IvoExtras.cs has the logic).
public partial class MainWindow
{
    void ApplyExtras()
    {
        Weather.City = S.WeatherCity;
        (Quiet.Hours, Quiet.From, Quiet.To, Quiet.Fullscreen) = (S.QuietHours, S.QuietFrom, S.QuietTo, S.QuietFullscreen);
        MoodLog.On = S.MoodLog; Journal.On = S.Journal;
    }

    // every minute (RunDue): what waited out the quiet hours, in one card; a gentle check-in when you've been low
    void AfterQuiet()
    {
        if (Quiet.After() is { } held && S.DesktopNotify) NotifyCard.Show("Quiet hours are over", held, "", "#A78BFA", () => OpenInbox());
        if (!Quiet.Now && S.DesktopNotify && MoodLog.CheckIn() is { } ask) NotifyCard.Show("Ivo", ask, "", "#F472B6", () => OpenIvoPanel());
    }

    TextBlock Dim(string text) { var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); return t; }
    // name: what the box is, for a screen reader and for Ivo's chat ("turn on quiet hours"), when its words don't say
    CheckBox Tick(string text, bool on, Action<bool> set, string? name = null)
    {
        var c = new CheckBox { IsChecked = on, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 4) };
        ((TextBlock)c.Content).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        c.Click += (_, _) => { set(c.IsChecked == true); SaveSettings(); ApplyExtras(); };
        System.Windows.Automation.AutomationProperties.SetName(c, name ?? text);
        return c;
    }

    void ExtrasRows(Func<string, UIElement, FrameworkElement> row)
    {
        // weather
        var city = new TextBox { Text = S.WeatherCity, Width = 280, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Your city, for \"will it rain?\" and the morning briefing (Open-Meteo, no account)" };
        var now = Dim(""); now.Margin = new Thickness(0, 8, 0, 0); now.MaxWidth = 560; now.HorizontalAlignment = HorizontalAlignment.Left;
        async void Show() { now.Text = await Weather.Current() is { } w ? $"{w.Place}{(S.WeatherCity == "" ? " (found automatically)" : "")} · {Weather.Briefing(w)}" : S.WeatherCity == "" ? "Couldn't find where you are: type your city" : "Couldn't find that city"; }
        city.LostFocus += (_, _) => { if (S.WeatherCity == city.Text.Trim()) return; S.WeatherCity = city.Text.Trim(); SaveSettings(); ApplyExtras(); Show(); };
        Show();
        AssistantCard.Children.Add(row("Weather", new StackPanel { Children = { Hinted(city, "found automatically, or type a city"), now } }));

        // quiet hours
        var from = new TextBox { Text = S.QuietFrom, Width = 64, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        var to = new TextBox { Text = S.QuietTo, Width = 64, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        void Times() { if (TimeSpan.TryParse(from.Text, out _) && TimeSpan.TryParse(to.Text, out _)) { S.QuietFrom = from.Text.Trim(); S.QuietTo = to.Text.Trim(); SaveSettings(); ApplyExtras(); } }
        from.LostFocus += (_, _) => Times(); to.LostFocus += (_, _) => Times();
        var quiet = new CheckBox { IsChecked = S.QuietHours, Content = "Every day from", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        quiet.SetResourceReference(Control.ForegroundProperty, "Fg");
        System.Windows.Automation.AutomationProperties.SetName(quiet, "Quiet hours");
        quiet.Click += (_, _) => { S.QuietHours = quiet.IsChecked == true; SaveSettings(); ApplyExtras(); };
        var toWord = new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        toWord.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var hours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10), Children = { quiet, from, toWord, to } };
        AssistantCard.Children.Add(row("Quiet hours", new StackPanel { Children = {
            Dim("No pop-ups or chimes; what comes in waits, and one card tells you after."),
            hours,
            Tick("While a full-screen app, game or presentation is up", S.QuietFullscreen, v => S.QuietFullscreen = v, "Quiet during full-screen apps") } }));

        // mood log: the last seven days, a face for each
        // mood graph: the last 30 days, a bar a day like Usage's: tall for a good day, short and pink for a low one, the day on hover
        var week = new Grid { Height = 92, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Width = 520 };
        week.RowDefinitions.Add(new RowDefinition()); week.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var month = Enumerable.Range(0, 30).Select(i => DateTime.Today.AddDays(i - 29)).ToList();
        for (int i = 0; i < month.Count; i++)
        {
            var mood = MoodLog.Of(month[i]);
            week.ColumnDefinitions.Add(new ColumnDefinition());
            var h = mood switch { null => 3, Mood.Happy or Mood.Excited or Mood.Flirty => 64, Mood.Bored => 36, _ => 18 };
            var bar = new System.Windows.Shapes.Rectangle { Height = h, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0), RadiusX = 3, RadiusY = 3,
                                                            ToolTip = $"{month[i]:ddd d MMM}: {(mood is { } m ? MoodLog.Emoji(m) + " " + m : "nothing said")}" };
            if (mood == null) bar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Separator");
            else if (MoodLog.Low(mood)) bar.Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x8B));
            else bar.Fill = (Brush)FindResource("AccentGrad");
            Grid.SetColumn(bar, i); week.Children.Add(bar);
        }
        foreach (var (col, text, align) in new[] { (0, month[0].ToString("d MMM"), HorizontalAlignment.Left), (24, "Today", HorizontalAlignment.Right) })
        {
            var t = new TextBlock { Text = text, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = align };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            Grid.SetRow(t, 1); Grid.SetColumn(t, col); Grid.SetColumnSpan(t, 6); week.Children.Add(t);
        }
        AssistantCard.Children.Add(row("Your mood", new StackPanel { Children = {
            Tick("Ivo keeps a daily record of your mood from what you tell her, and checks in when it's been low", S.MoodLog, v => S.MoodLog = v, "Mood log"), week } }));

        // activity: tracking, and the nudge
        AssistantCard.Children.Add(row("Activity", new StackPanel { Children = {
            Tick("Track what's in front, your typing speed and screen time (the rail at the bottom; on this PC only)", S.ActivityOn, v => { S.ActivityOn = Activity.On = v; StatusRail.Visibility = v ? Visibility.Visible : Visibility.Collapsed; }, "Activity"),
            Tick("Nudge me after an hour straight of videos, games or social", S.ActivityNudge, v => S.ActivityNudge = Activity.Nudges = v, "Activity nudge") } }));

        // journal: today's entry, editable; the days before in a list
        var days = new ComboBox { Width = 180, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var k in Journal.Days.Keys.Reverse())
            days.Items.Add(new ComboBoxItem { Tag = k, Content = DateTime.TryParse(k, out var d) ? (d == DateTime.Today ? "Today" : d == DateTime.Today.AddDays(-1) ? "Yesterday" : d.ToString("ddd d MMM")) : k });
        var entry = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 64, Width = 560, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8, 10, 8) };
        string? Day() => (days.SelectedItem as ComboBoxItem)?.Tag as string;
        days.SelectionChanged += (_, _) => entry.Text = Day() is { } k ? Journal.Days[k].Entry : "";
        entry.LostFocus += (_, _) =>
        {
            if (Day() is not { } k || Journal.Days[k].Entry == entry.Text) return;
            Journal.Days[k].Entry = entry.Text; Journal.Days[k].Edited = true; Journal.Save();
        };
        if (days.Items.Count > 0) days.SelectedIndex = 0;
        AssistantCard.Children.Add(row("Journal", new StackPanel { Children = {
            Tick("Ivo writes a short entry of what you talked about each day, and remembers it when a topic comes back", S.Journal, v => S.Journal = v, "Journal"),
            days.Items.Count > 0 ? days : Dim("Nothing yet: chat with her and today's entry appears here."), entry } }));
        if (days.Items.Count == 0) entry.Visibility = Visibility.Collapsed;
    }
}
