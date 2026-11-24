using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

// The weather where you are, from Open-Meteo (free, no key): your city (Settings → Assistant → Weather), or where your IP
// says you are (ipwho.is) when none is set, is looked up once;
// the forecast is kept 15 minutes. Her chat, the morning briefing and the desktop widgets all read it from here.
public static class Weather
{
    public record Now(string Place, double Temp, string Sky, double High, double Low, int Rain, string TomorrowSky, double TomorrowHigh, int TomorrowRain);
    public static string City = "";
    static readonly HttpClient web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(8) };
    static (string City, double Lat, double Lon, string Name, string Country)? place;
    static (DateTime At, string City, Now Now)? cached;
    public static string Country => place?.Country ?? "";

    public static async Task<Now?> Current(string? city = null)
    {
        city = (city ?? City).Trim();   // "": where you are, by your IP (ipwho.is)
        if (cached is { } c && c.City.Equals(city, StringComparison.OrdinalIgnoreCase) && DateTime.Now - c.At < TimeSpan.FromMinutes(15)) return c.Now;
        try
        {
            if (place?.City.Equals(city, StringComparison.OrdinalIgnoreCase) != true)
            {
                var g = city == "" ? JsonNode.Parse(await web.GetStringAsync("https://ipwho.is/")) is { } me && (double?)me["latitude"] != null ? me : null
                                   : JsonNode.Parse(await web.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}"))?["results"]?[0];
                if (g == null) return null;
                place = (city, (double)g["latitude"]!, (double)g["longitude"]!, (string?)g["name"] ?? (string?)g["city"] ?? city,
                         (string?)g["country"] ?? (string?)g["country_name"] ?? "");
            }
            var p = place!.Value;
            var f = JsonNode.Parse(await web.GetStringAsync(FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={p.Lat}&longitude={p.Lon}&current=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=2")))!;
            var d = f["daily"]!;
            double At(string k, int i) => (double?)d[k]?[i] ?? 0;
            var now = new Now(p.Name, (double)f["current"]!["temperature_2m"]!, Sky((int)f["current"]!["weather_code"]!), At("temperature_2m_max", 0), At("temperature_2m_min", 0),
                              (int)At("precipitation_probability_max", 0), Sky((int)At("weather_code", 1)), At("temperature_2m_max", 1), (int)At("precipitation_probability_max", 1));
            cached = (DateTime.Now, city, now);
            return now;
        }
        catch (Exception ex) { Store.Fail("Weather", ex.Message); return null; }
    }

    // WMO weather codes, in words
    public static string Sky(int code) => code switch
    {
        0 => "clear", 1 or 2 => "partly cloudy", 3 => "cloudy", 45 or 48 => "foggy", >= 51 and <= 57 => "drizzly", >= 61 and <= 67 or >= 80 and <= 82 => "rainy",
        >= 71 and <= 77 or 85 or 86 => "snowy", >= 95 => "stormy", _ => "mixed",
    };
    public static string Icon(string sky) => sky switch
    {
        "clear" => "☀️", "partly cloudy" => "⛅", "cloudy" => "☁️", "foggy" => "🌫️", "drizzly" or "rainy" => "🌧️", "snowy" => "❄️", "stormy" => "⛈️", _ => "🌤️",
    };

    // "will it rain?", "how's the weather in paris", "is it cold tomorrow"
    static readonly Regex Ask = new(@"(?i)\b(weather|forecast|rain(ing|y)?|umbrella|snow(ing)?|temperature|how (hot|cold|warm) is it|is it (hot|cold|warm|sunny)|sunny)\b");
    public static bool Asked(string said) => Ask.IsMatch(said) && !Regex.IsMatch(said, @"(?i)\b(song|play|lyrics|purple rain)\b");
    public static string? CityIn(string said) => Regex.Match(said, @"(?i)\b(?:in|at|for)\s+([a-z][a-z .'-]{1,40}?)(?:\s+(?:today|tomorrow|now|tonight))?\s*\??$") is { Success: true } m ? m.Groups[1].Value : null;

    // her answer, with a remark of her own
    public static async Task<string> Answer(string said)
    {
        var city = CityIn(said);
        if (await Current(city) is not { } w) return "I couldn't reach the weather right now 🥺 try again in a bit? (Your city goes in Settings → Assistant → Weather.)";
        var tomorrow = Regex.IsMatch(said, @"(?i)\btomorrow\b");
        var (sky, high, rain) = tomorrow ? (w.TomorrowSky, w.TomorrowHigh, w.TomorrowRain) : (w.Sky, w.High, w.Rain);
        var head = tomorrow ? $"Tomorrow in {w.Place}: {sky} {Icon(sky)}, up to {high:0}°, {rain}% chance of rain."
                            : $"In {w.Place} it's {w.Temp:0}° and {sky} {Icon(sky)} (high {w.High:0}°, low {w.Low:0}°), {w.Rain}% chance of rain.";
        return head + " " + Remark(sky, high, rain);
    }

    public static string Remark(string sky, double high, int rain) =>
        rain >= 60 ? "Take an umbrella, I'm not drying you off 😤☔" :
        rain >= 30 ? "Maybe pack an umbrella, just in case 👀" :
        high >= 33 ? "It's giving oven. Stay hydrated, please 🥵💧" :
        high <= 5 ? "Wear a jacket, I mean it 🥶" :
        sky == "clear" ? "Perfect excuse to go outside for once 😌" : "Pretty chill day, honestly ✨";

    public static string Briefing(Now w) => $"{Icon(w.Sky)} {w.Temp:0}° and {w.Sky} in {w.Place}, high {w.High:0}°, low {w.Low:0}°, {w.Rain}% chance of rain. {Remark(w.Sky, w.High, w.Rain)}";
}

// A little file of hers in the data folder: read once, written on every change.
static class Kept<T> where T : new()
{
    public static T Load(string name) { try { var f = Path.Combine(Store.Dir, name); return File.Exists(f) ? JsonSerializer.Deserialize<T>(File.ReadAllText(f)) ?? new() : new(); } catch { return new(); } }
    public static void Save(string name, T value) { try { Directory.CreateDirectory(Store.Dir); File.WriteAllText(Path.Combine(Store.Dir, name), JsonSerializer.Serialize(value)); } catch (Exception ex) { Store.Fail("Ivo · " + name, ex.Message); } }
}

// How you've felt, a day at a time, from the mood she reads in what you say to her (the last 60 days, in mood.json).
public static class MoodLog
{
    public static bool On = true;
    static Dictionary<string, Dictionary<string, int>>? days;
    static Dictionary<string, Dictionary<string, int>> Days => days ??= Kept<Dictionary<string, Dictionary<string, int>>>.Load("mood.json");
    static string Key(DateTime d) => d.ToString("yyyy-MM-dd");

    public static void Note(Mood mood)
    {
        if (!On || mood is Mood.Neutral or Mood.Confused) return;
        var day = Days.TryGetValue(Key(DateTime.Today), out var x) ? x : Days[Key(DateTime.Today)] = [];
        day[mood.ToString()] = day.GetValueOrDefault(mood.ToString()) + 1;
        foreach (var old in Days.Keys.Where(k => string.CompareOrdinal(k, Key(DateTime.Today.AddDays(-60))) < 0).ToList()) Days.Remove(old);
        Kept<Dictionary<string, Dictionary<string, int>>>.Save("mood.json", Days);
    }

    // the day's mood: the one felt most (null: nothing said that day)
    public static Mood? Of(DateTime d) => Days.TryGetValue(Key(d), out var x) && x.Count > 0 && Enum.TryParse<Mood>(x.MaxBy(k => k.Value).Key, out var m) ? m : null;
    public static List<(DateTime Day, Mood? Mood)> Week() => [.. Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).Select(d => (d, Of(d)))];
    public static bool Low(Mood? m) => m is Mood.Sad or Mood.Stressed or Mood.Angry;
    public static string Emoji(Mood? m) => m switch
    {
        Mood.Happy => "😊", Mood.Excited => "🤩", Mood.Flirty => "😏", Mood.Sad => "😢", Mood.Stressed => "😩", Mood.Angry => "😠", Mood.Rude => "😤", Mood.Bored => "🥱", _ => "·",
    };

    static DateTime asked;
    // a gentle check-in: two or more of the last three days were low, and she hasn't asked today
    public static string? CheckIn()
    {
        if (!On || asked == DateTime.Today) return null;
        if (Enumerable.Range(0, 3).Count(i => Low(Of(DateTime.Today.AddDays(-i)))) < 2) return null;
        asked = DateTime.Today;
        return "Hey… you've seemed a bit down the last few days 🥺 Want to talk about it? I'm here.";
    }
}

// Her journal of your chats: what you talked about each day, as a short entry you can read and change
// (Settings → Assistant → Journal). When a topic comes back, she remembers when you last talked about it.
public static class Journal
{
    public class Day { public List<string> Said { get; set; } = []; public string Entry { get; set; } = ""; public bool Edited { get; set; } }
    public static bool On = true;
    static SortedDictionary<string, Day>? days;
    public static SortedDictionary<string, Day> Days => days ??= Kept<SortedDictionary<string, Day>>.Load("journal.json");
    static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    public static void Save() => Kept<SortedDictionary<string, Day>>.Save("journal.json", Days);

    static readonly HashSet<string> Plain = new("about after again also always another because been before being could didnt does doing dont from going gonna good have just know like make maybe more much need really right said should some something still sure tell than thats that them then there they thing think this time today tomorrow want wanna what when where which while will with would yeah your youre okay haha lmao".Split(' '));
    public static IEnumerable<string> Topics(string said) => Regex.Matches(said.ToLowerInvariant(), @"[a-z]{4,}").Select(m => m.Value).Where(w => !Plain.Contains(w)).Distinct();

    public static void Note(string said)
    {
        if (!On || said.StartsWith('/') || said.Length < 8) return;
        var day = Days.TryGetValue(Key(DateTime.Today), out var x) ? x : Days[Key(DateTime.Today)] = new();
        if (day.Said.Count < 200) day.Said.Add(said.Length > 160 ? said[..160] : said);
        if (!day.Edited) day.Entry = Summary(day.Said);
        Save();
    }

    // ponytail: word-count summary, a model-written one if it ever reads too flat
    public static string Summary(List<string> said)
    {
        var top = said.SelectMany(Topics).GroupBy(w => w).Where(g => g.Count() >= 1).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToList();
        if (top.Count == 0) return "We just hung out and chatted ✨";
        return $"We talked about {(top.Count == 1 ? top[0] : string.Join(", ", top.SkipLast(1)) + " and " + top[^1])}. ({said.Count} message{(said.Count == 1 ? "" : "s")})";
    }

    // the last day before today you talked about something in this message, and what about
    public static (DateTime Day, string Topic)? Before(string said)
    {
        if (!On) return null;
        var now = Topics(said).Where(w => w.Length >= 5).ToHashSet();
        if (now.Count == 0) return null;
        foreach (var (k, d) in Days.Reverse())
        {
            if (k == Key(DateTime.Today)) continue;
            if (d.Said.SelectMany(Topics).FirstOrDefault(now.Contains) is { } t) return (DateTime.Parse(k), t);
        }
        return null;
    }

    public static string Recall(DateTime day, string topic)
    {
        var ago = (DateTime.Today - day.Date).Days;
        var when = ago == 1 ? "yesterday" : ago < 7 ? "on " + day.DayOfWeek : $"back on {day:d MMM}";
        return $"(You mentioned {topic} {when} too 👀)";
    }
}

// Quiet hours: no pop-ups or chimes during the hours you set, or while a full-screen app, game or presentation is up.
// What came in waits and she tells you in one card after.
public static class Quiet
{
    public static bool Hours, Fullscreen = true;
    public static string From = "22:00", To = "08:00";

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    // QUNS_BUSY (2), RUNNING_D3D_FULL_SCREEN (3), PRESENTATION_MODE (4)
    static bool FullscreenUp() { try { return SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4; } catch { return false; } }

    public static bool InHours(TimeSpan now)
    {
        if (!TimeSpan.TryParse(From, out var a) || !TimeSpan.TryParse(To, out var b) || a == b) return false;
        return a < b ? now >= a && now < b : now >= a || now < b;
    }
    public static bool Now => Hours && InHours(DateTime.Now.TimeOfDay) || Fullscreen && FullscreenUp();

    static readonly List<string> held = [];
    public static void Hold(string title) { lock (held) held.Add(title); }
    // after the quiet: what waited, in one line (null: nothing did, or it's still quiet)
    public static string? After()
    {
        if (Now) return null;
        lock (held)
        {
            if (held.Count == 0) return null;
            var s = $"{held.Count} while you were busy: " + string.Join(" · ", held.GroupBy(t => t).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
            held.Clear();
            return s;
        }
    }
}
