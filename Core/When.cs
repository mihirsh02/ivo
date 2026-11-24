using System.Globalization;

namespace Ivo;

// Every date and time shown to you, written one way: "14:30" or "2:30 pm" (Settings → Appearance → Clock), with
// friendly days ("Today", "Yesterday", "Monday", "27 Oct", "27 Oct 2025"). What models read keeps a fixed machine format.
public static class When
{
    public static bool TwelveHour = !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    // A time written the way people say it, for quick capture and snoozes: "in 20 min", "in 2 hours", "at 5pm", "at 17:30",
    // "tomorrow 9am", "tonight", "this evening", "after lunch", "tomorrow morning", "next week". Returns the time and the
    // text with the time words removed, or null when there's no time in it.
    public static (DateTime At, string Text)? Parse(string text, DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        DateTime Next(DateTime t) => t > now ? t : t.AddDays(1);
        var rules = new (string Pattern, Func<System.Text.RegularExpressions.Match, DateTime> At)[]
        {
            (@"\bin\s+(\d+)\s*(m|min|mins|minutes?)\b", m => now.AddMinutes(int.Parse(m.Groups[1].Value))),
            (@"\bin\s+(\d+)\s*(h|hr|hrs|hours?)\b", m => now.AddHours(int.Parse(m.Groups[1].Value))),
            (@"\bin\s+(\d+)\s*(d|days?)\b", m => now.Date.AddDays(int.Parse(m.Groups[1].Value)).AddHours(9)),
            (@"\b(tomorrow|tmrw)\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", m => Clock(now.Date.AddDays(1), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value)),
            (@"\btomorrow morning\b", _ => now.Date.AddDays(1).AddHours(9)),
            (@"\btomorrow\b", _ => now.Date.AddDays(1).AddHours(9)),
            (@"\bnext week\b", _ => now.Date.AddDays(7 - ((int)now.DayOfWeek + 6) % 7).AddHours(9)),
            (@"\bafter lunch\b", _ => Next(now.Date.AddHours(13).AddMinutes(30))),
            (@"\b(tonight|this evening)\b", _ => Next(now.Date.AddHours(19))),
            (@"\bat\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", m => Next(Clock(now.Date, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))),
        };
        foreach (var (pattern, at) in rules)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            try
            {
                var rest = System.Text.RegularExpressions.Regex.Replace(text.Remove(m.Index, m.Length), @"\s{2,}", " ").Trim(' ', ',', '-');
                return (at(m), rest);
            }
            catch (ArgumentOutOfRangeException) { return null; }   // "at 27"
        }
        return null;
    }
    static DateTime Clock(DateTime day, string hour, string minute, string ampm)
    {
        var h = int.Parse(hour); var mi = minute == "" ? 0 : int.Parse(minute);
        if (ampm.Equals("pm", StringComparison.OrdinalIgnoreCase) && h < 12) h += 12;
        if (ampm.Equals("am", StringComparison.OrdinalIgnoreCase) && h == 12) h = 0;
        return day.AddHours(h).AddMinutes(mi);
    }

    public static string Time(DateTime t) => TwelveHour ? t.ToString("h:mm", En) + (t.Hour < 12 ? " am" : " pm") : t.ToString("HH:mm", En);

    // Today / Yesterday / Tomorrow / a weekday within the week / 27 Oct / 27 Oct 2025
    public static string Day(DateTime t, bool longForm = false)
    {
        var d = (t.Date - DateTime.Today).Days;
        return d switch
        {
            0 => "Today",
            -1 => "Yesterday",
            1 => "Tomorrow",
            > -7 and < 7 => t.ToString(longForm ? "dddd, d MMMM" : "dddd", En),
            _ when t.Year == DateTime.Today.Year => t.ToString(longForm ? "dddd, d MMMM" : "d MMM", En),
            _ => t.ToString(longForm ? "d MMMM yyyy" : "d MMM yyyy", En),
        };
    }

    // "14:30" today; otherwise "Yesterday, 14:30", "Monday, 14:30", "27 Oct, 14:30", "27 Oct 2025"
    public static string Stamp(DateTime t)
    {
        var day = Day(t);
        return day == "Today" ? Time(t) : t.Year != DateTime.Today.Year && (t.Date - DateTime.Today).Days is <= -7 or >= 7 ? day : $"{day}, {Time(t)}";
    }

    // tooltips: "Monday, 27 October 2026 at 14:30"
    public static string Full(DateTime t) => $"{t.ToString("dddd, d MMMM yyyy", En)} at {Time(t)}";

    // a date heading in a list: "Today", "Yesterday", "Monday, 27 October", "27 October 2025"
    public static string Heading(DateTime t) => Day(t, longForm: true);
}
