using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ivo;

// Live cards in Ivo's chat: ask her about something of yours and under her answer comes a rich card of it, like the
// Spotify one: artwork or icons, the headline, rows with pictures and bars, kept up to date while it's on screen, and
// a tap opens where it lives. Your music (up next, top songs and artists, recently played), the weather, your screen
// time, your schedule for today, to-dos, habits, calendar and who's online.
public partial class MainWindow
{
    static readonly (Regex Asked, string Topic)[] CardTopics =
    [
        (new(@"(?i)\blyrics?\b"), "lyrics"),
        (new(@"(?i)\b(how many|number of|count of|total)\b.*\b(models?|apis?|api keys?|providers?|gateways?|connections?|connected accounts?|service accounts?)\b|\b(models?|apis?|api keys?|providers?|gateways?|connections?|connected accounts?|service accounts?)\b.*\b(how many|number of|count|total)\b"), "setupcounts"),
        (new(@"(?i)\b(up next|play(s|ing)? next|what'?s next|in (the|my) queue|queue)\b|\bwhat (?:are|is) (?:the )?(?:next|upcoming|coming) (?:songs?|tracks?)\b|\b(?:which|what) (?:songs?|tracks?) (?:are|come|play) next\b"), "queue"),
        (new(@"(?i)\b(top|most played|favou?rite|best) (songs?|tracks?|music)\b|\bmy top songs?\b"), "toptracks"),
        (new(@"(?i)\b(top|most played|favou?rite|best) (artists?|singers?|bands?)\b"), "topartists"),
        (new(@"(?i)\b(recent(ly)? played|last (few )?(songs?|played)|what did i (listen|play))\b"), "recent"),
        (new(@"(?i)\b(weather|forecast|rain|umbrella|temperature|sunny|snow)\b"), "weather"),
        (new(@"(?i)\b(screen ?time|activity|typing speed|wpm|how (long|much).*\b(on|using|spent)\b)"), "activity"),
        (new(@"(?i)\b(remind(er|ers)?|plans?|schedules?|timetables?|what'?s (on|up) (today|tomorrow)|my day|agenda)\b"), "today"),
        (new(@"(?i)\b(to-?dos?|to do list|what do i (have|need) to do)\b"), "todos"),
        (new(@"(?i)\b(tasks?|daily tasks?|jobs?)\b"), "tasks"),
        (new(@"(?i)\b(habits?|streaks?)\b"), "habits"),
        (new(@"(?i)\b(meetings?|busy|free)\b"), "meetings"),
        (new(@"(?i)\b(calendars?|events?|holidays?)\b"), "calendar"),
        (new(@"(?i)\b(who'?s online|contacts?|friends? online)\b"), "people"),
    ];

    internal static string? IvoCardTopic(string said) => CardTopics.FirstOrDefault(t => t.Asked.IsMatch(said)) is { Asked: not null } match ? match.Topic : null;

    // the card for what you asked, if it's about something of yours
    Task<InfoCard?> IvoCard(string said) => IvoCardTopic(said) switch
    {
        "setupcounts" => Done(SetupCountsCard(said)),
        "lyrics" => LyricsCard(), "queue" => QueueCard(), "toptracks" => TopTracksCard(), "topartists" => TopArtistsCard(), "recent" => RecentCard(),
        "weather" => WeatherCard(), "activity" => Done(ActivityCard()), "today" => Done(ScheduleCard()), "todos" => Done(TodosCard()), "tasks" => Done(TasksCard()),
        "habits" => Done(HabitsCard()), "calendar" => Done(CalendarCard()), "meetings" => Done(MeetingsCard(DayIn(said), said)), "people" => Done(PeopleCard()),
        _ => Task.FromResult<InfoCard?>(null),
    };
    static Task<InfoCard?> Done(InfoCard? c) => Task.FromResult(c);

    InfoCard SetupCountsCard(string asked)
    {
        var providers = S.Providers.Where(p => CategoryOf(p) is "providers" or "gateways").ToList();
        var models = providers.SelectMany(p => (ModelCache.GetValueOrDefault(p.Name) ?? Api.ModelsFor(p).ToList())
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(model => (Provider: p.Name, Model: model))).ToList();
        var working = models.Count(m => S.ModelStatus.GetValueOrDefault($"{m.Provider}|{m.Model}") == "ok");
        var accounts = Connections.Accounts.Count();
        var connectionsOnly = Regex.IsMatch(asked, @"(?i)\b(connections?|connected accounts?|service accounts?)\b")
            && !Regex.IsMatch(asked, @"(?i)\b(models?|apis?|api keys?|providers?|gateways?)\b");
        return new("🔌", "#8B6CFF", "Your Ivo setup", "models, APIs and connected services",
            [
                new("Model choices", "listed across configured providers", models.Count.ToString(), Glyph: "✨"),
                new("API providers", "API keys and gateways", providers.Count.ToString(), Glyph: "🔌"),
                new("Working models", "passed a model check", working.ToString(), Glyph: "✓"),
                new("Connections", "saved service accounts", accounts.ToString(), Glyph: "🔗"),
            ],
            connectionsOnly ? "connections" : "providers", null);
    }

    // a picture from the web (album art, an artist), decoded small
    static ImageSource? Pic(string? url, int px = 160)
    {
        if (url is not { Length: > 0 }) return null;
        try { var b = new BitmapImage(); b.BeginInit(); b.UriSource = new Uri(url); b.DecodePixelWidth = px; b.EndInit(); return b; } catch { return null; }
    }
    static string? Cover(JsonNode? track) => (string?)(track?["album"]?["images"] as JsonArray)?.FirstOrDefault()?["url"];

    // ---------- your music ----------
    async Task<InfoCard?> LyricsCard()
    {
        if (!SpotifyOn || await SpotifyApi.Get("/me/player") is not { } player || player["item"] is not { } track) return null;
        var all = await Lyrics.For(track);
        if (all is not { Count: > 0 }) return null;

        var title = (string?)track["name"] ?? "Lyrics";
        var artist = SpotifyView.Artists(track);
        var synced = all[0].At >= 0;
        InfoRow[] rows;
        string? sub;
        if (!synced)
        {
            sub = $"{artist} · not synced to playback";
            rows = [new("These lyrics can't follow the song yet.", null, null, Faded: true)];
        }
        else
        {
            var window = Lyrics.Around(all, ((double?)player["progress_ms"] ?? 0) / 1000);
            var playing = (bool?)player["is_playing"] == true;
            sub = $"{artist} · {(playing ? "playing now" : "paused")}";
            rows = [.. window.Lines.Select((line, i) => new InfoRow(line.Text, null,
                i == window.Current ? "♪" : null, Faded: window.Current >= 0 && i != window.Current, Highlight: i == window.Current))];
        }

        return new("🎤", "#1DB954", title, sub, rows, "spotify", LyricsCard, Pic(Cover(track), 300),
            TimeSpan.FromSeconds(2), CompactRows: true);
    }

    async Task<InfoCard?> QueueCard()
    {
        if (!SpotifyOn) return null;
        var q = await SpotifyApi.Get("/me/player/queue");
        var now = q?["currently_playing"];
        var next = (q?["queue"] as JsonArray ?? []).Where(x => x != null).Take(5).ToList();
        return new("🎶", "#1DB954", next.Count == 0 ? "Nothing up next" : "Up next", now != null ? $"after {(string?)now["name"]}" : null,
            [.. next.Select((t, i) => new InfoRow((string?)t!["name"] ?? "", SpotifyView.Artists(t), $"{i + 1}", Pic(Cover(t))))],
            "spotify", QueueCard, Pic(Cover(now) ?? Cover(next.FirstOrDefault()), 300));
    }

    async Task<InfoCard?> TopTracksCard()
    {
        if (!SpotifyOn) return null;
        var list = ((await SpotifyApi.Get("/me/top/tracks?limit=5&time_range=short_term"))?["items"] as JsonArray ?? []).Where(x => x != null).ToList();
        return new("🏆", "#1DB954", "Your top songs", "the last 4 weeks",
            [.. list.Select((t, i) => new InfoRow((string?)t!["name"] ?? "", SpotifyView.Artists(t), $"#{i + 1}", Pic(Cover(t))))],
            "spotify", null, Pic(Cover(list.FirstOrDefault()), 300));
    }

    async Task<InfoCard?> TopArtistsCard()
    {
        if (!SpotifyOn) return null;
        var list = ((await SpotifyApi.Get("/me/top/artists?limit=5&time_range=short_term"))?["items"] as JsonArray ?? []).Where(x => x != null).ToList();
        static string? Img(JsonNode? a) => (string?)(a?["images"] as JsonArray)?.FirstOrDefault()?["url"];
        return new("🎤", "#1DB954", "Your top artists", "the last 4 weeks",
            [.. list.Select((a, i) => new InfoRow((string?)a!["name"] ?? "", string.Join(", ", (a["genres"] as JsonArray ?? []).Take(2).Select(g => (string?)g)), $"#{i + 1}", Pic(Img(a)), Round: true))],
            "spotify", null, Pic(Img(list.FirstOrDefault()), 300));
    }

    async Task<InfoCard?> RecentCard()
    {
        if (!SpotifyOn) return null;
        var items = ((await SpotifyApi.Get("/me/player/recently-played?limit=5"))?["items"] as JsonArray ?? []).Where(x => x != null).ToList();
        static string Ago(JsonNode p) { var at = DateTime.TryParse((string?)p["played_at"], out var d) ? DateTime.Now - d : TimeSpan.Zero; return at.TotalMinutes < 60 ? $"{at.TotalMinutes:0}m ago" : at.TotalHours < 24 ? $"{at.TotalHours:0}h ago" : $"{at.TotalDays:0}d ago"; }
        return new("🕘", "#1DB954", "Recently played", null,
            [.. items.Select(p => new InfoRow((string?)p!["track"]?["name"] ?? "", SpotifyView.Artists(p["track"]!), Ago(p), Pic(Cover(p["track"]))))],
            "spotify", RecentCard, Pic(Cover(items.FirstOrDefault()?["track"]), 300));
    }

    // ---------- your day ----------
    async Task<InfoCard?> WeatherCard()
    {
        if (await Weather.Current() is not { } w) return null;
        return new(Weather.Icon(w.Sky), "#38BDF8", $"{w.Temp:0}°  {w.Sky}", w.Place,
            [new("Today", $"high {w.High:0}° · low {w.Low:0}°", $"{w.Rain}% rain", Glyph: Weather.Icon(w.Sky)),
             new("Tomorrow", $"{w.TomorrowSky}, up to {w.TomorrowHigh:0}°", $"{w.TomorrowRain}% rain", Glyph: Weather.Icon(w.TomorrowSky))],
            null, WeatherCard);
    }

    InfoCard ActivityCard()
    {
        var d = Activity.DayOf(DateTime.Today);
        var total = Math.Max(1, d.Total);
        string Kind(string app) => Activity.KindOf(app, d.Kinds.GetValueOrDefault(app, "Other"));
        var rows = d.Apps.OrderByDescending(a => a.Value).Take(5)
            .Select(a => new InfoRow(a.Key, Kind(a.Key), Activity.Span(a.Value), AppIcon(a.Key), (double)a.Value / total,
                                     Bar: Activity.Categories.FirstOrDefault(c => c.Name == Kind(a.Key)).Color)).ToArray();
        return new("⏱️", "#22C55E", Activity.Span(d.Total), $"screen time today{(Activity.Wpm() is { } w ? $" · typing {w} wpm" : "")}", rows, "activity", () => Done(ActivityCard()));
    }

    InfoCard TodosCard()
    {
        var open = S.Todos.Where(t => !t.Done).OrderBy(t => t.Due ?? DateTime.MaxValue).ThenByDescending(t => t.Priority).ToList();
        var rows = open.Take(6).Select(t => new InfoRow(t.Text, t.List != "" ? t.List : null,
            t.Due is { } due ? (due.Date == DateTime.Today ? "today" : due.Date < DateTime.Today ? "overdue" : due.ToString("d MMM")) : "",
            Glyph: t.Priority switch { 3 => "🔴", 2 => "🟠", 1 => "🔵", _ => "⚪" })).ToArray();
        return new("✅", "#34D399", open.Count == 0 ? "All done 🎉" : $"{open.Count} to do", open.Count > 6 ? $"the next 6 of {open.Count}" : null, rows, "todo", () => Done(TodosCard()));
    }

    // Ivo's tasks: the prompts she runs for you, each with when and how often
    InfoCard TasksCard()
    {
        var tasks = S.Schedules.Where(x => x.Kind == "task").OrderByDescending(x => x.On).ThenBy(x => x.When.TimeOfDay).ToList();
        var on = tasks.Count(t => t.On);
        var rows = tasks.Take(7).Select(x => new InfoRow(x.Text.Split('\n')[0], x.Category != "" ? x.Category : null,
            x.Repeat == "files" ? "on changes" : (x.Repeat switch { "daily" => "daily", "weekdays" => "weekdays", "weekly" => x.When.ToString("ddd"), _ => x.When.ToString("d MMM") }) + " · " + x.When.ToString(When.TwelveHour ? "h:mm tt" : "HH:mm"),
            Glyph: "✨", Faded: !x.On)).ToArray();
        return new("✨", "#FB923C", tasks.Count == 0 ? "No tasks for Ivo" : $"{on} task{(on == 1 ? "" : "s")} Ivo does for you", null, rows, "tasks", () => Done(TasksCard()));
    }

    InfoCard HabitsCard()
    {
        var due = S.Habits.Where(h => h.Due(DateTime.Today)).ToList();
        var done = due.Count(h => h.Did(DateTime.Today));
        // days in a row, up to today (today still open doesn't break it)
        static int Streak(Habit h) { var d = h.Did(DateTime.Today) ? DateTime.Today : DateTime.Today.AddDays(-1); var n = 0; while (h.Did(d)) { n++; d = d.AddDays(-1); } return n; }
        return new("🎯", "#F43F5E", due.Count == 0 ? "No habits today" : $"{done} of {due.Count} done", "today",
            [.. due.Select(h => new InfoRow(h.Name, Streak(h) > 1 ? $"{Streak(h)}-day streak 🔥" : null, h.Did(DateTime.Today) ? "done" : "", Glyph: h.Did(DateTime.Today) ? "✅" : "⭕"))],
            "focus", () => Done(HabitsCard()));
    }

    // the day a message is about: today, tomorrow, or a weekday ("on friday": the next one)
    static DateTime DayIn(string said)
    {
        var low = said.ToLowerInvariant();
        if (low.Contains("tomorrow")) return DateTime.Today.AddDays(1);
        for (var i = 1; i <= 7; i++) { var d = DateTime.Today.AddDays(i); if (low.Contains(d.DayOfWeek.ToString().ToLowerInvariant())) return d; }
        return DateTime.Today;
    }

    // meetings on a day: your calendar's events (not holidays) and your schedule, with a yes or no up top
    InfoCard MeetingsCard(DateTime day, string asked = "")
    {
        var free = System.Text.RegularExpressions.Regex.IsMatch(asked, @"(?i)\bfree\b");
        var busy = System.Text.RegularExpressions.Regex.IsMatch(asked, @"(?i)\bbusy\b");
        var month = new DateTime(day.Year, day.Month, 1);
        var holidays = Holidays(month);
        var items = CalEvents(month).Where(e => e.Start.Date <= day && e.End.Date >= day && !holidays.Contains(e.Title))
            .Select(e => (At: e.AllDay ? TimeSpan.Zero : e.Start.TimeOfDay, e.Title, Kind: "calendar"))
            .Concat(S.Schedules.Where(x => x.Kind == "plan" && x.Falls(day)).Select(x => (At: x.When.TimeOfDay, Title: x.Text, Kind: "plan")))
            .OrderBy(x => x.At).ToList();
        var when = day == DateTime.Today ? "today" : day == DateTime.Today.AddDays(1) ? "tomorrow" : "on " + day.ToString("dddd");
        var now = DateTime.Now.TimeOfDay;
        var n = $"{items.Count} {(items.Count == 1 ? "meeting" : "meetings")}";
        var title = (free, busy, items.Count) switch
        {
            (true, _, 0) => $"Yes, you're free {when}", (true, _, _) => $"Not quite: {n} {when}",
            (_, true, 0) => $"No, you're free {when}", (_, true, _) => $"Yes: {n} {when}",
            (_, _, 0) => $"No meetings {when}", _ => $"{n} {when}",
        };
        return new("📅", "#38BDF8", title, day.ToString("dddd, d MMM"),
            [.. items.Select(x => new InfoRow(x.Title, x.Kind == "plan" ? "your schedule" : null, x.At == TimeSpan.Zero ? "all day" : DateTime.Today.Add(x.At).ToString(When.TwelveHour ? "h:mm tt" : "HH:mm"),
                                              Glyph: x.Kind == "plan" ? "🗓️" : "📅", Faded: day == DateTime.Today && x.At != TimeSpan.Zero && x.At < now))],
            "schedule", () => Done(MeetingsCard(day, asked)));
    }

    InfoCard CalendarCard()
    {
        var now = DateTime.Now;
        var next = Enumerable.Range(0, 2).SelectMany(m => CalEvents(new DateTime(now.Year, now.Month, 1).AddMonths(m)))
            .Where(e => e.End >= now).OrderBy(e => e.Start).DistinctBy(e => (e.Start, e.Title)).Take(5)
            .Select(e => new InfoRow(e.Title, e.AllDay ? "all day" : e.Start.ToString(When.TwelveHour ? "h:mm tt" : "HH:mm"),
                                     e.Start.Date == now.Date ? "today" : e.Start.Date == now.Date.AddDays(1) ? "tomorrow" : e.Start.ToString("ddd d MMM"), Glyph: "📅")).ToArray();
        return new("🗓️", "#38BDF8", next.Length == 0 ? "Nothing coming up" : "Coming up", null, next, "calendar", () => Done(CalendarCard()));
    }

    InfoCard PeopleCard()
    {
        var people = ivoChats?.Book.Threads.Where(t => t.Kind == "dm").Select(t => t.Members.FirstOrDefault()).OfType<ChatMember>().DistinctBy(m => m.Id).ToList() ?? [];
        static bool On(ChatMember p) => p.Live is not ("offline" or "");
        return new("👥", "#A78BFA", $"{people.Count(On)} of {people.Count} online", null,
            [.. people.OrderBy(p => On(p) ? 0 : 1).Take(6).Select(p => new InfoRow(p.Name, On(p) ? $"{p.Live}{(p.App != "" ? $" · on {p.App}" : "")}" : "offline", null,
                                                                                  ChatPic(p.Pic), Round: true, Glyph: On(p) ? "🟢" : "⚪", Faded: !On(p)))],
            "portal", () => Done(PeopleCard()));
    }
    static ImageSource? ChatPic(string? b64)
    {
        if (b64 is not { Length: > 0 }) return null;
        try { var b = new BitmapImage(); b.BeginInit(); b.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(b64)); b.DecodePixelWidth = 80; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze(); return b; } catch { return null; }
    }
}
