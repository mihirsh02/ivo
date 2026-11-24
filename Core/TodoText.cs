using System.Text.RegularExpressions;

namespace Ivo;

// Quick add, TickTick-style: one line carries the whole to-do.
//   "Call mom tomorrow 6pm !high #family ~Home every week"
//   !high / !med / !low (or !1-!3) priority · #tag · ~list · every day / weekday / week / month · a day or a time
public static partial class TodoText
{
    public static Todo Parse(string line, DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var t = new Todo();
        var text = " " + line.Trim() + " ";

        text = Prio().Replace(text, m => { t.Priority = m.Groups[1].Value.ToLowerInvariant() switch { "high" or "h" or "3" => 3, "med" or "medium" or "m" or "2" => 2, _ => 1 }; return " "; });
        text = Tag().Replace(text, m => { t.Tags.Add(m.Groups[1].Value); return " "; });
        text = ListRx().Replace(text, m => { t.List = m.Groups[1].Value.Replace('_', ' '); return " "; });
        text = Every().Replace(text, m =>
        {
            t.Repeat = m.Groups[1].Value.ToLowerInvariant() switch { "day" or "daily" => "daily", "weekday" => "weekdays", "week" => "weekly", _ => "monthly" };
            return " ";
        });

        // a day without a time: today, tonight's date, a weekday name
        var day = DayRx().Match(text);
        if (day.Success && !Regex.IsMatch(text, @"\b(at\s+)?\d{1,2}(:\d{2})?\s*(am|pm)\b|\bat\s+\d", RegexOptions.IgnoreCase))
        {
            var w = day.Groups[2].Value.ToLowerInvariant();
            t.Due = w switch
            {
                "today" => now.Date,
                "tomorrow" or "tmrw" => now.Date.AddDays(1),
                _ => NextDay(now, w, day.Groups[1].Success),
            };
            text = text.Remove(day.Index, day.Length).Insert(day.Index, " ");
        }
        else if (When.Parse(text.Trim(), now) is { } when)
        {
            t.Due = when.At; t.DueTime = true; text = when.Text;
        }
        if (t.Repeat != "" && t.Due == null) t.Due = now.Date;   // "every day": starting today
        t.Text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        if (t.Text == "") t.Text = line.Trim();
        return t;
    }

    // "friday" or "next friday": the coming one (a week on, when it's Friday today)
    static DateTime NextDay(DateTime now, string name, bool next)
    {
        var target = Enum.GetValues<DayOfWeek>().First(d => d.ToString().StartsWith(name[..3], StringComparison.OrdinalIgnoreCase));
        var add = ((int)target - (int)now.DayOfWeek + 7) % 7;
        return now.Date.AddDays(add == 0 ? 7 : add);
    }

    // a repeating to-do, ticked: its next time
    public static DateTime NextDue(Todo t)
    {
        var d = t.Due ?? DateTime.Today;
        var from = d < DateTime.Today ? DateTime.Today.Add(d.TimeOfDay) : d;   // missed days don't pile up
        return t.Repeat switch
        {
            "daily" => from.AddDays(1),
            "weekdays" => from.AddDays(from.DayOfWeek == DayOfWeek.Friday ? 3 : from.DayOfWeek == DayOfWeek.Saturday ? 2 : 1),
            "weekly" => from.AddDays(7),
            _ => from.AddMonths(1),
        };
    }

    [GeneratedRegex(@"\s!(high|med|medium|low|h|m|l|[123])(?=\s)", RegexOptions.IgnoreCase)] private static partial Regex Prio();
    [GeneratedRegex(@"\s#([\p{L}\p{N}_-]+)(?=\s)")] private static partial Regex Tag();
    [GeneratedRegex(@"\s~([\p{L}\p{N}_-]+)(?=\s)")] private static partial Regex ListRx();
    [GeneratedRegex(@"\bevery\s+(day|daily|weekday|week|month)\b", RegexOptions.IgnoreCase)] private static partial Regex Every();
    [GeneratedRegex(@"\b(next\s+)?(today|tomorrow|tmrw|mon(?:day)?|tue(?:s|sday)?|wed(?:nesday)?|thu(?:rs|rsday)?|fri(?:day)?|sat(?:urday)?|sun(?:day)?)\b", RegexOptions.IgnoreCase)] private static partial Regex DayRx();
}
