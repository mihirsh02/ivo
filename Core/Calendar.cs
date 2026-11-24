using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Ivo;

// Your calendars, read from their private iCal addresses (Google: Settings → your calendar → "Secret address in iCal
// format"; Outlook: Settings → Shared calendars → Publish → ICS). Read-only; new events are made as an .ics file that
// opens in your calendar app to save. Recurring events are expanded for daily/weekly/monthly/yearly rules.
public static class Calendar
{
    public record Event(DateTime Start, DateTime End, string Title, string Location, bool AllDay);
    static readonly HttpClient http = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<List<Event>> Between(IEnumerable<string> urls, DateTime from, DateTime to)
    {
        var all = new List<Event>();
        foreach (var url in urls.Where(u => u.Trim() != ""))
        {
            try { all.AddRange(Parse(await http.GetStringAsync(url.Trim().Replace("webcal://", "https://")), from, to)); }
            catch (Exception ex) { Store.Fail("Calendar", $"{new Uri(url.Replace("webcal://", "https://")).Host}: {ex.Message}"); }
        }
        return [.. all.OrderBy(e => e.Start)];
    }

    public static string Format(List<Event> events) => events.Count == 0 ? "Nothing on the calendar." :
        string.Join("\n", events.Select(e => $"- {e.Start:ddd d MMM} {(e.AllDay ? "all day" : $"{e.Start:HH:mm}–{e.End:HH:mm}")}  {e.Title}{(e.Location != "" ? $" ({e.Location})" : "")}"));

    // ---------- reading .ics ----------
    public static List<Event> Parse(string ics, DateTime from, DateTime to)
    {
        var text = Regex.Replace(ics.Replace("\r\n", "\n"), @"\n[ \t]", "");   // unfold continued lines
        var events = new List<Event>();
        foreach (Match block in Regex.Matches(text, @"BEGIN:VEVENT\n(.*?)\nEND:VEVENT", RegexOptions.Singleline))
        {
            var props = block.Groups[1].Value.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToList();
            string? Get(string name) => props.FirstOrDefault(p => p[0].Split(';')[0].Equals(name, StringComparison.OrdinalIgnoreCase)) is { } p ? p[1].Trim() : null;
            (DateTime When, bool Date)? Time(string name)
            {
                var p = props.FirstOrDefault(x => x[0].Split(';')[0].Equals(name, StringComparison.OrdinalIgnoreCase));
                return p == null ? null : ParseTime(p[0], p[1].Trim());
            }
            if (Get("STATUS")?.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true) continue;
            if (Time("DTSTART") is not var (start, allDay)) continue;
            var end = Time("DTEND")?.When ?? (allDay ? start.AddDays(1) : start.AddHours(1));
            var length = end - start;
            var title = Unescape(Get("SUMMARY") ?? "(no title)"); var where = Unescape(Get("LOCATION") ?? "");
            var skip = props.Where(p => p[0].Split(';')[0].Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
                            .SelectMany(p => p[1].Split(',').Select(v => ParseTime(p[0], v.Trim()).When)).ToHashSet();
            foreach (var s in Occurrences(start, Get("RRULE"), from - length, to))
                if (!skip.Contains(s) && s + length > from && s < to) events.Add(new(s, s + length, title, where, allDay));
        }
        return events;
    }

    // DTSTART;TZID=Asia/Kolkata:20261102T093000 · DTSTART:20261102T040000Z · DTSTART;VALUE=DATE:20261102 → local time
    static (DateTime When, bool Date) ParseTime(string head, string v)
    {
        if (v.Length == 8) return (DateTime.ParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture), true);
        var t = DateTime.ParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (v.EndsWith('Z')) return (DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(), false);
        if (Regex.Match(head, @"TZID=""?([^;:""]+)") is { Success: true } tz)
            try { return (TimeZoneInfo.ConvertTime(t, TimeZoneInfo.FindSystemTimeZoneById(tz.Groups[1].Value), TimeZoneInfo.Local), false); } catch { }
        return (t, false);   // floating: already local
    }

    // ponytail: FREQ, INTERVAL, COUNT, UNTIL and weekly BYDAY; BYMONTHDAY/BYSETPOS and the rest fall back to FREQ alone
    public static IEnumerable<DateTime> Occurrences(DateTime start, string? rule, DateTime from, DateTime to)
    {
        if (rule == null) { yield return start; yield break; }
        var r = rule.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].ToUpperInvariant(), x => x[1]);
        var freq = r.GetValueOrDefault("FREQ", "DAILY");
        var every = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var n) && n > 0 ? n : 1;
        var count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c) ? c : int.MaxValue;
        var until = r.TryGetValue("UNTIL", out var u) ? ParseTime("", u).When : DateTime.MaxValue;
        var days = r.TryGetValue("BYDAY", out var bd) && freq == "WEEKLY"
            ? bd.Split(',').Select(d => Array.IndexOf(new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }, d.Trim()[^2..])).Where(i => i >= 0).Select(i => (DayOfWeek)i).ToHashSet()
            : null;
        var made = 0;
        for (var k = 0; made < count && k < 5000; k++)
        {
            var baseTime = freq switch
            {
                "WEEKLY" => start.AddDays(7 * every * k), "MONTHLY" => start.AddMonths(every * k), "YEARLY" => start.AddYears(every * k), _ => start.AddDays(every * k),
            };
            if (baseTime > to || baseTime > until) yield break;
            if (days == null) { made++; if (baseTime >= from) yield return baseTime; continue; }
            var weekStart = baseTime.AddDays(-(int)baseTime.DayOfWeek + (int)DayOfWeek.Monday - (baseTime.DayOfWeek == DayOfWeek.Sunday ? 7 : 0));
            foreach (var d in Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).Where(d => days.Contains(d.DayOfWeek)).OrderBy(d => d))
            {
                if (d < start) continue;
                if (d > to || d > until || made >= count) yield break;
                made++;
                if (d >= from) yield return d;
            }
        }
    }

    static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    // ---------- making one: an .ics that opens in the calendar app (Outlook, Calendar…) to save ----------
    public static string Create(string title, DateTime start, DateTime end, string location = "", string notes = "")
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");
        static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        var ics = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Ivo//EN\r\nBEGIN:VEVENT\r\n")
            .Append($"UID:{Guid.NewGuid()}@ivo\r\nDTSTAMP:{Utc(DateTime.Now)}\r\nDTSTART:{Utc(start)}\r\nDTEND:{Utc(end)}\r\nSUMMARY:{Esc(title)}\r\n")
            .Append(location != "" ? $"LOCATION:{Esc(location)}\r\n" : "").Append(notes != "" ? $"DESCRIPTION:{Esc(notes)}\r\n" : "")
            .Append("END:VEVENT\r\nEND:VCALENDAR\r\n").ToString();
        var file = Path.Combine(Path.GetTempPath(), "Ivo " + string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim() + ".ics");
        File.WriteAllText(file, ics);
        return file;
    }
}
