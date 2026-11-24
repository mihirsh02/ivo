using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Ivo;

// What Ivo does for you from her chat: reminders, alerts, daily tasks, to-dos and habits (the Planner's own tools),
// Spotify, Portal calls and messages, any setting on the Settings page (by working its control, IvoSettingsIndex),
// and opening a settings section or panel. Calls, messages and weighty settings ask first; the rest happen at once
// with an Undo.
public partial class MainWindow
{
    // every switch, choice and slider on the Settings page, read fresh each time (cheap: one walk of the page)
    List<SettingControl> IvoSettings()
    {
        var panels = SectionPanels();
        string SectionOf(FrameworkElement e)
        {
            for (DependencyObject? p = e; p != null; p = LogicalTreeHelper.GetParent(p))
                foreach (var (tag, panel) in panels) if (panel == p) return tag;
            return "";
        }
        return IvoSettingsIndex.Build(SectionScroll, SectionOf);
    }

    // the places Ivo can send you: every settings section, with the words on its controls, and the panels
    IReadOnlyList<(string Tag, string Title, string Words)> IvoPlaces()
    {
        var all = IvoSettings();
        var places = Sections.Where(s => s.Key != "overview").Select(s => (s.Key, s.Value.Title,
            s.Value.Hint + " " + string.Join(" ", all.Where(c => c.Section == s.Key).Select(c => c.Label + " " + string.Join(" ", c.Choices))))).ToList();
        places.AddRange([
            ("planner", "Planner", "reminders reminder alerts alert daily tasks schedule dev tasks"), ("todo", "To-dos", "to-do todo todos list checklist"),
            ("inbox", "Inbox", "briefing results missed reminders"), ("spotify", "Spotify", "music songs playlists lyrics player"),
            ("portal", "Portal", "contacts call calls chats messages friends"), ("focus", "Focus", "focus timer pomodoro habits streak"),
            ("calendar", "Calendar", "calendar events holidays days"), ("notes", "Notes", "notes scratchpad")]);
        return places;
    }

    // a new song while her chat is open: now and then (about 1 in 10) she says she loves it, with a fact about it
    IvoChatView? ivoView;
    static readonly string[] SongLove = ["Ooh, I love this song 🎶", "Omg this one 😍", "Okay this song is a vibe ✨", "Wait, I love this one 💜", "Turn it up!! 🔊"];
    async Task IvoOnNewSong(JsonNode item)
    {
        if (panelKey != "ivo" || ivoView == null || Random.Shared.Next(10) != 0) return;
        var title = (string?)item["name"] ?? ""; var artist = (string?)item["artists"]?[0]?["name"] ?? "";
        var fact = await IvoMind.AboutAsync(Random.Shared.Next(2) == 0 ? artist : $"{title} {artist} song");
        if (panelKey != "ivo" || ivoView == null) return;
        ivoView.Greet($"{SongLove[Random.Shared.Next(SongLove.Length)]} {title}{(artist != "" ? " by " + artist : "")}!" + (fact != null ? $"\n\nFun fact: {fact}" : ""));
    }

    void IvoOpen(string tag)
    {
        ClosePanel();
        switch (tag)
        {
            case "planner": OpenPlanner("reminder"); break;
            case "todo": OpenTodos(); break;
            case "inbox": OpenInbox(); break;
            case "spotify": OpenSpotify(); break;
            case "portal": OpenPortal(); break;
            case "focus": OpenFocus(); break;
            case "notes": OpenNotes(); break;
            case "calendar": Calendar_Click(this, new RoutedEventArgs()); break;
            case "activity": OpenActivity(); break;
            case "schedule": OpenMySchedule(); break;
            case "tasks": OpenPlanner("task"); break;
            default: if (Sections.ContainsKey(tag)) OpenSettings(tag); break;
        }
    }

    (SettingControl? Hit, string? Value, string? Ask, string[] Choices) IvoSetting(IvoAction a) =>
        IvoSettingsIndex.Resolve(IvoSettings().Where(c => a.Args.GetValueOrDefault("section", "") is var sec && (sec == "" || c.Section == sec)).ToList(),
                                 a.Args.GetValueOrDefault("setting", ""), a.Args.GetValueOrDefault("value"));

    bool IvoNeedsOk(IvoAction a) => a.Kind is "call" or "message" || a.Kind == "set" && IvoSetting(a).Hit is { Weighty: true };

    PortalContact[] IvoContacts(string who) =>
        S.PortalContacts.Where(c => c.Name.Equals(who, StringComparison.OrdinalIgnoreCase)) is var exact && exact.Any() ? [.. exact]
        : [.. S.PortalContacts.Where(c => c.Name.Split(' ').Any(p => p.Equals(who, StringComparison.OrdinalIgnoreCase)) || c.Name.StartsWith(who, StringComparison.OrdinalIgnoreCase))];

    // does it; what comes back is what to say, how to take it back (null: nothing to undo), and when she has to ask
    // which one, the answers to tap (each the same thing, with that one picked)
    async Task<(string Said, Action? Undo, (string Label, IvoAction Then)[] Pick, SongCard? Song)> IvoDo(IvoAction a)
    {
        ivoSong = null;
        var (said, undo) = await IvoDoOnce(a);
        if (a.Kind is "call" or "message" && said.StartsWith("Which one"))
            return (said, null, [.. IvoContacts(a.Args["who"]).Select(p => (p.Name, a with { Args = new(a.Args) { ["who"] = p.Name } }))], null);
        if (ivoLinks.Count > 0) { var links = ivoLinks.ToArray(); ivoLinks.Clear(); return (said, undo, [.. links.Select(l => (l.Label, new IvoAction("url", new() { ["target"] = l.Url })))], null); }
        if (a.Kind == "set" && IvoSetting(a) is { Ask: not null, Choices.Length: > 0 } q)
            return (said, null, [.. q.Choices.Select(ch => (ch, q.Ask.StartsWith("Which one")
                ? a with { Args = new(a.Args) { ["setting"] = Regex.Replace(ch, @" \((\w+)\)$", ""), ["section"] = Regex.Match(ch, @" \((\w+)\)$").Groups[1].Value } }
                : a with { Args = new(a.Args) { ["value"] = ch } }))], null);
        return (said, undo, [], ivoSong);
    }

    // the rest of Spotify, from her chat: shuffle, repeat, volume, seek, liking, playlists, albums, artists, podcasts,
    // your queue, recent and top songs, following, devices
    async Task<(string Said, Action? Undo)> IvoSpotify(string op, string value)
    {
        if (!SpotifyOn) return ("Connect Spotify first: Settings → Connections 🎧", null);
        string No(int code, JsonNode? json, string what)
        {
            Store.Fail("Ivo · Spotify", $"{what}: {code}");
            return code switch { 401 => "Spotify signed you out 🥺 reconnect it in Settings → Connections", 403 => "Spotify needs Premium for that 🎧", 404 => "Start playing something on Spotify first 🎧", _ => $"Spotify said no ({code}) 🥺" };
        }
        bool Ok(int code) => code is >= 200 and < 300;
        string Lines(IEnumerable<string> items, int max = 10) => string.Join("\n", items.Take(max).Select((x, i) => $"{i + 1}. {x}"));
        async Task<(string, Action?)> PlayContext(string uri, string name)
        {
            var (code, json) = await SpotifyApi.Send("PUT", "/me/player/play", new JsonObject { ["context_uri"] = uri }, control: true);
            if (!Ok(code)) return (No(code, json, "play " + uri), null);
            await Task.Delay(800); ivoSong = Card(await SpotifyApi.Get("/me/player"));
            return ($"Playing {name} 🎶", null);
        }
        var player = await SpotifyApi.Get("/me/player");
        var item = player?["item"];
        switch (op)
        {
            case "shuffle":
            {
                var was = (bool?)player?["shuffle_state"] == true;
                var (code, json) = await SpotifyApi.Send("PUT", $"/me/player/shuffle?state={(value == "on" ? "true" : "false")}", control: true);
                return Ok(code) ? (value == "on" ? "Shuffle's on 🔀" : "Shuffle's off ➡️", () => _ = SpotifyApi.Send("PUT", $"/me/player/shuffle?state={(was ? "true" : "false")}", control: true)) : (No(code, json, "shuffle"), null);
            }
            case "repeat":
            {
                var was = (string?)player?["repeat_state"] ?? "off";
                var (code, json) = await SpotifyApi.Send("PUT", $"/me/player/repeat?state={value}", control: true);
                return Ok(code) ? (value switch { "track" => "On repeat 🔂 this one again and again", "context" => "Repeating the whole thing 🔁", _ => "Repeat's off ➡️" }, () => _ = SpotifyApi.Send("PUT", $"/me/player/repeat?state={was}", control: true)) : (No(code, json, "repeat"), null);
            }
            case "volume":
            {
                var was = (int?)player?["device"]?["volume_percent"] ?? 50;
                var to = value switch { "up" => Math.Min(100, was + 15), "down" => Math.Max(0, was - 15), _ => int.TryParse(value, out var n) ? Math.Clamp(n, 0, 100) : was };
                var (code, json) = await SpotifyApi.Send("PUT", $"/me/player/volume?volume_percent={to}", control: true);
                return Ok(code) ? (to == 0 ? "Muted 🔇" : $"Volume {to}% 🔊", () => _ = SpotifyApi.Send("PUT", $"/me/player/volume?volume_percent={was}", control: true))
                                : (code == 403 ? "This device doesn't let me change its volume 🥺" : No(code, json, "volume"), null);
            }
            case "seek":
            {
                var at = (int?)player?["progress_ms"] ?? 0;
                var ms = value.StartsWith('+') || value.StartsWith('-') ? at + int.Parse(value) * 1000 : int.Parse(value) * 1000;
                var (code, json) = await SpotifyApi.Send("PUT", $"/me/player/seek?position_ms={Math.Max(0, ms)}", control: true);
                return Ok(code) ? (ms <= 0 ? "From the top ⏪" : $"Jumped to {ms / 60000}:{ms / 1000 % 60:00} ⏩", null) : (No(code, json, "seek"), null);
            }
            case "like" or "unlike":
            {
                if (item == null) return ("Nothing's playing right now 🎧", null);
                var id = (string?)item["id"];
                var (code, json) = await SpotifyApi.Send(op == "like" ? "PUT" : "DELETE", $"/me/tracks?ids={id}");
                var back = op == "like" ? "DELETE" : "PUT";
                return Ok(code) ? (op == "like" ? $"Saved {(string?)item["name"]} to your Liked Songs 💚" : $"Removed {(string?)item["name"]} from your Liked Songs 💔", () => _ = SpotifyApi.Send(back, $"/me/tracks?ids={id}"))
                                : (No(code, json, op), null);
            }
            case "addto":
            {
                if (item == null) return ("Nothing's playing right now 🎧", null);
                var lists = (await SpotifyApi.Get("/me/playlists?limit=50"))?["items"]?.AsArray().Where(p => p != null).ToList() ?? [];
                var list = lists.FirstOrDefault(p => ((string?)p!["name"] ?? "").Equals(value, StringComparison.OrdinalIgnoreCase)) ?? lists.FirstOrDefault(p => ((string?)p!["name"] ?? "").Contains(value, StringComparison.OrdinalIgnoreCase));
                if (list == null) return ($"I couldn't find a playlist called \"{value}\" 🥺", null);
                var (code, json) = await SpotifyApi.Send("POST", $"/playlists/{(string?)list["id"]}/tracks", new JsonObject { ["uris"] = new JsonArray((JsonNode?)(string?)item["uri"]) });
                return Ok(code) ? ($"Added {(string?)item["name"]} to {(string?)list["name"]} ➕", null) : (No(code, json, "add to playlist"), null);
            }
            case "liked":
            {
                var saved = (await SpotifyApi.Get("/me/tracks?limit=50"))?["items"]?.AsArray().Select(x => (string?)x?["track"]?["uri"]).OfType<string>().ToList() ?? [];
                if (saved.Count == 0) return ("Your Liked Songs are empty 🥺", null);
                var (code, json) = await SpotifyApi.Send("PUT", "/me/player/play", new JsonObject { ["uris"] = new JsonArray([.. saved.Select(u => (JsonNode?)u)]) }, control: true);
                if (!Ok(code)) return (No(code, json, "liked"), null);
                await Task.Delay(800); ivoSong = Card(await SpotifyApi.Get("/me/player"));
                return ("Playing your Liked Songs 💚", null);
            }
            case "myplaylist":
            {
                var lists = (await SpotifyApi.Get("/me/playlists?limit=50"))?["items"]?.AsArray().Where(p => p != null).ToList() ?? [];
                var list = lists.FirstOrDefault(p => ((string?)p!["name"] ?? "").Equals(value, StringComparison.OrdinalIgnoreCase)) ?? lists.FirstOrDefault(p => ((string?)p!["name"] ?? "").Contains(value, StringComparison.OrdinalIgnoreCase));
                return list == null ? ($"I couldn't find a playlist called \"{value}\" 🥺 say \"show my playlists\" to see them", null) : await PlayContext((string?)list["uri"] ?? "", (string?)list["name"] ?? value);
            }
            case "playlists":
            {
                var lists = (await SpotifyApi.Get("/me/playlists?limit=20"))?["items"]?.AsArray().Where(p => p != null).Select(p => $"{(string?)p!["name"]} · {(int?)p["tracks"]?["total"] ?? 0} songs").ToList() ?? [];
                return (lists.Count == 0 ? "You don't have any playlists yet 🎶" : $"Your playlists 🎶\n{Lines(lists, 20)}\n\nSay \"play my … playlist\" and I'll put it on", null);
            }
            case "queue":
            {
                var q = await SpotifyApi.Get("/me/player/queue");
                var next = q?["queue"]?.AsArray().Where(x => x != null).Select(x => $"{(string?)x!["name"]} · {SpotifyView.Artists(x)}").ToList() ?? [];
                return (next.Count == 0 ? "Nothing's queued up next 📋 say \"queue\" and a song" : $"Up next 📋\n{Lines(next)}", null);
            }
            case "recent":
            {
                var recent = (await SpotifyApi.Get("/me/player/recently-played?limit=10"))?["items"]?.AsArray().Select(x => x?["track"]).Where(x => x != null).Select(x => $"{(string?)x!["name"]} · {SpotifyView.Artists(x)}").Distinct().ToList() ?? [];
                return (recent.Count == 0 ? "I can't see anything you played lately 🎧" : $"You've been listening to 🕘\n{Lines(recent)}", null);
            }
            case "top":
            {
                var top = (await SpotifyApi.Get($"/me/top/{value}?limit=10&time_range=short_term"))?["items"]?.AsArray().Where(x => x != null)
                    .Select(x => value == "artists" ? (string?)x!["name"] ?? "" : $"{(string?)x!["name"]} · {SpotifyView.Artists(x)}").ToList() ?? [];
                return (top.Count == 0 ? "Spotify hasn't worked out your favourites yet 🎧" : $"Your top {(value == "artists" ? "artists" : "songs")} lately 🏆\n{Lines(top)}", null);
            }
            case "album" or "artist" or "podcast":
            {
                var type = op == "podcast" ? "show" : op;
                var hit = (await SpotifyApi.Get($"/search?q={Uri.EscapeDataString(value)}&type={type}&limit=3"))?[type + "s"]?["items"]?.AsArray().FirstOrDefault(x => x != null);
                if (hit == null) return ($"Couldn't find {(op == "podcast" ? "the podcast" : op == "album" ? "the album" : "")} \"{value}\" on Spotify 🥺".Replace("  ", " "), null);
                return await PlayContext((string?)hit["uri"] ?? "", (string?)hit["name"] ?? value);
            }
            case "follow":
            {
                if (item?["artists"]?[0] is not { } artist) return ("Nothing's playing right now 🎧", null);
                var id = (string?)artist["id"];
                var (code, json) = await SpotifyApi.Send("PUT", $"/me/following?type=artist&ids={id}");
                return Ok(code) ? ($"You're following {(string?)artist["name"]} now ➕", () => _ = SpotifyApi.Send("DELETE", $"/me/following?type=artist&ids={id}")) : (No(code, json, "follow"), null);
            }
            case "devices" or "device":
            {
                var devices = (await SpotifyApi.Get("/me/player/devices"))?["devices"]?.AsArray().Where(d => d != null).ToList() ?? [];
                if (op == "devices")
                    return (devices.Count == 0 ? "No Spotify devices are on right now 📱 open Spotify somewhere" : $"Your devices 📱\n{Lines(devices.Select(d => $"{(string?)d!["name"]} · {(string?)d["type"]}{((bool?)d["is_active"] == true ? " · playing" : "")}"))}", null);
                var to = devices.FirstOrDefault(d => ((string?)d!["name"] ?? "").Contains(value, StringComparison.OrdinalIgnoreCase) || ((string?)d!["type"] ?? "").Contains(value, StringComparison.OrdinalIgnoreCase)
                                                     || value is "phone" or "mobile" && (string?)d!["type"] == "Smartphone" || value is "pc" or "computer" or "laptop" && (string?)d!["type"] == "Computer");
                if (to == null) return ($"I can't see \"{value}\" in your Spotify devices 🥺 open Spotify on it first", null);
                var (code, json) = await SpotifyApi.Send("PUT", "/me/player", new JsonObject { ["device_ids"] = new JsonArray((JsonNode?)(string?)to["id"]), ["play"] = true });
                return Ok(code) ? ($"Moved the music to {(string?)to["name"]} 📱", null) : (No(code, json, "device"), null);
            }
        }
        return ("I'm not sure how to do that on Spotify yet 🥺", null);
    }

    // links to tap under what she found (videos, posts), read once by IvoDo
    readonly List<(string Label, string Url)> ivoLinks = [];
    static string Short(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
    static string Since(DateTime t)
    {
        var d = DateTime.Now - t.ToLocalTime();
        return d.TotalHours < 1 ? $"{Math.Max(1, (int)d.TotalMinutes)}m ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours}h ago" : d.TotalDays < 30 ? $"{(int)d.TotalDays}d ago" : t.ToString("d MMM");
    }

    async Task<(string Said, Action? Undo)> IvoYouTube(string op, string value)
    {
        YouTubeChannel? Followed(string who) => S.YouTube.FirstOrDefault(c => c.Name.Equals(who, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(who, StringComparison.OrdinalIgnoreCase) || c.Id == who);
        switch (op)
        {
            case "follow":
            {
                if (Followed(value) is { } already) return ($"You already follow {already.Name} 📺", null);
                if (await Social.FindChannel(value) is not { } c) return ($"I couldn't find a YouTube channel called \"{value}\" 🥺 try its @handle", null);
                S.YouTube.Add(c); SaveSettings();
                return ($"Following {c.Name} on YouTube 📺 ask me \"any new videos?\" any time", () => { S.YouTube.Remove(c); SaveSettings(); });
            }
            case "unfollow":
            {
                if (Followed(value) is not { } c) return ($"You don't follow \"{value}\" here 🤔", null);
                S.YouTube.Remove(c); SaveSettings();
                return ($"Unfollowed {c.Name}", () => { S.YouTube.Add(c); SaveSettings(); });
            }
            case "list":
                return (S.YouTube.Count == 0 ? "You don't follow any channels yet 📺 say \"follow mkbhd on youtube\"" : $"Your channels 📺\n{string.Join("\n", S.YouTube.Select(c => "• " + c.Name))}", null);
            default:
            {
                List<Social.Video> videos;
                string from;
                if (value == "")
                {
                    if (S.YouTube.Count == 0) return ("You don't follow any channels yet 📺 say \"follow mkbhd on youtube\", or ask \"new videos from mkbhd\"", null);
                    var all = await Task.WhenAll(S.YouTube.Select(Social.Videos));
                    videos = all.SelectMany(v => v.Take(5)).Where(v => v.Published > DateTime.Now.AddDays(-7)).OrderByDescending(v => v.Published).Take(8).ToList();
                    from = "your channels";
                    if (videos.Count == 0) return ("Nothing new from your channels this week 📺", null);
                }
                else
                {
                    var c = Followed(value) ?? await Social.FindChannel(value);
                    if (c == null) return ($"I couldn't find a YouTube channel called \"{value}\" 🥺", null);
                    videos = (await Social.Videos(c)).Take(5).ToList();
                    from = videos.FirstOrDefault()?.Channel ?? c.Name;
                    if (videos.Count == 0) return ($"I couldn't get {c.Name}'s videos right now 🥺", null);
                }
                foreach (var v in videos.Take(4)) ivoLinks.Add(("▶ " + Short(v.Title, 34), v.Url));
                return ($"New from {from} 📺\n" + string.Join("\n", videos.Select(v => $"• {v.Title}{(value == "" ? " · " + v.Channel : "")} · {Since(v.Published)}")), null);
            }
        }
    }

    async Task<(string Said, Action? Undo)> IvoReddit(string sub, string sort, string range)
    {
        if (S.RedditApp == "") { ivoLinks.Add(("Get a free app id ↗", "https://www.reddit.com/prefs/apps")); return ("Reddit only lets apps read it with a free app id 🟠 Make one at reddit.com/prefs/apps (type: installed app, any name, redirect http://localhost), then paste its id in Settings → Assistant → Reddit", null); }
        Social.RedditApp = S.RedditApp;
        var posts = await Social.Posts(sub, sort, range);
        if (posts == null) return ($"I couldn't reach r/{sub} right now 🥺 (it may be private, or Reddit is busy)", null);
        if (posts.Count == 0) return ($"Nothing in r/{sub} right now 🤔", null);
        foreach (var p in posts.Take(4)) ivoLinks.Add(("↗ " + Short(p.Title, 34), p.Url));
        static string K(int n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();
        var head = sub == "popular" ? "Hot on Reddit 🟠" : $"r/{posts[0].Sub} · {(sort == "top" ? "top " + (range == "day" ? "today" : range == "all" ? "of all time" : "this " + range) : sort)} 🟠";
        return (head + "\n" + string.Join("\n", posts.Take(6).Select(p => $"• {p.Title} · ⬆ {K(p.Score)} · 💬 {K(p.Comments)}{(sub == "popular" ? " · r/" + p.Sub : "")}")), null);
    }

    // what's playing, for her music card (set by the music actions, read once by IvoDo)
    SongCard? ivoSong;
    string lastAsk = "";
    static SongCard? Card(JsonNode? player) => player?["item"] is { } item
        ? new((string?)item["name"] ?? "", SpotifyView.Artists(item), (string?)item["album"]?["images"]?[0]?["url"], (bool?)player["is_playing"] == true,
              ((double?)player["progress_ms"] ?? 0) / 1000, ((double?)item["duration_ms"] ?? 0) / 1000)
        : null;

    async Task<(string Said, Action? Undo)> IvoDoOnce(IvoAction a)
    {
        string Arg(string k) => a.Args.GetValueOrDefault(k, "");
        try
        {
            switch (a.Kind)
            {
                case "reminder" or "alert" or "task":
                {
                    var add = new JsonObject { ["action"] = "add", ["kind"] = a.Kind == "reminder" ? "reminder" : "task", ["text"] = Arg("text"), ["when"] = Arg("when"), ["repeat"] = Arg("repeat") is "" ? "once" : Arg("repeat") };
                    if (a.Kind == "alert") add["category"] = "Alerts";
                    var said = Planner.Run("schedule", add);
                    if (Regex.Match(said, @"^Scheduled \[(\w+)\]") is not { Success: true } id) return (said, null);
                    return ("Done ✨", () => Planner.Run("schedule", new JsonObject { ["action"] = "remove", ["id"] = id.Groups[1].Value }));
                }
                case "todo":
                {
                    var said = Planner.Run("todo", new JsonObject { ["action"] = "add", ["text"] = Arg("text") });
                    SyncTodosSoon();
                    if (Regex.Match(said, @"^Added \[(\w+)\]") is not { Success: true } id) return (said, null);
                    return ("Added to your to-dos ✅", () => { Planner.Run("todo", new JsonObject { ["action"] = "remove", ["id"] = id.Groups[1].Value }); SyncTodosSoon(); });
                }
                case "habitdone":
                {
                    if (IvoMind.HabitNamed(Arg("text")) is not { } h) return ($"I can't find a habit called \"{Arg("text")}\" 🥺 add it with /habit", null);
                    var key = DateTime.Today.ToString("yyyy-MM-dd");
                    if (h.Days.Contains(key)) return ($"{h.Name} is already ticked for today ✅ {h.Streak(DateTime.Today)}-day streak 🔥", null);
                    h.Days.Add(key); SaveSettings(); Cheer(h);
                    if (panelKey == "focus") OpenFocus();
                    var st = h.Streak(DateTime.Today);
                    return ($"Ticked {h.Name} ✅{(st > 1 ? $" {st} days in a row 🔥" : "")}", () => { h.Days.Remove(key); SaveSettings(); });
                }
                case "habit":
                {
                    var h = new Habit { Name = Arg("text") };
                    S.Habits.Add(h); SaveSettings();
                    return ("New habit 🔥 tick it in Focus each day", () => { S.Habits.Remove(h); SaveSettings(); });
                }
                case "lyrics" or "song" or "artist":
                {
                    if (!SpotifyOn) return ("Connect Spotify first: Settings → Connections 🎧", null);
                    if ((await SpotifyApi.Get("/me/player"))?["item"] is not { } song) return ("Nothing's playing right now 🎧 play something and ask me again", null);
                    var title = (string?)song["name"] ?? ""; var by = SpotifyView.Artists(song); var first = (string?)song["artists"]?[0]?["name"] ?? by;
                    if (a.Kind == "lyrics")
                    {
                        var words = (await Lyrics.For(song))?.Select(l => l.Text.Trim()).Where(t => t != "").Take(14).ToList();
                        if (words is not { Count: > 0 }) return ($"I couldn't find the lyrics to {title} 🥺", null);
                        return ($"🎤 {title} · {by}\n\n{string.Join("\n", words)}\n\n…the rest is in the Spotify panel, following along as it plays", null);
                    }
                    var about = await IvoMind.AboutAsync(a.Kind == "artist" ? first : $"{title} {first} song") ?? (a.Kind == "song" ? await IvoMind.AboutAsync(first) : null);
                    return (about ?? $"I couldn't find much about {(a.Kind == "artist" ? first : title)} right now 🥺 it's {title} by {by} though, and it slaps 🎶", null);
                }
                case "music":
                    return await IvoSpotify(Arg("op"), Arg("value"));
                case "ask":   // a new chat with your AI model, asked for you; her panel steps aside so you see it
                {
                    // "now in python": the last thing asked, again, that way
                    var text = Arg("text");
                    if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^(now |and |same )?(in|using) \S+[\s?.!]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && lastAsk != "") text = $"{lastAsk} — {text}";
                    lastAsk = System.Text.RegularExpressions.Regex.IsMatch(text, @" — ") ? lastAsk : text;
                    var chat = NewChat(); active.ShowChat(chat); RefreshChats();
                    ClosePanel();
                    active.Send(text);
                    return ("Asked your AI in a new chat 💻 the answer's coming in there", null);
                }
                case "youtube":
                    return await IvoYouTube(Arg("op"), Arg("value"));
                case "reddit":
                    return await IvoReddit(Arg("sub"), Arg("sort") is "" ? "top" : Arg("sort"), Arg("range") is "" ? "day" : Arg("range"));
                case "url":
                    if (Uri.TryCreate(Arg("target"), UriKind.Absolute, out var link) && link.Scheme is "https" or "http")
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
                    return ("Opened ↗️", null);
                case "queue":
                {
                    if (!SpotifyOn) return ("Connect Spotify first: Settings → Connections 🎧", null);
                    var (found, results) = await SpotifyApi.Send("GET", $"/search?q={Uri.EscapeDataString(Arg("query"))}&type=track&limit=5");
                    if (results?["tracks"]?["items"]?.AsArray().FirstOrDefault(x => x != null) is not { } song)
                        return (found is >= 200 and < 300 ? $"Couldn't find \"{Arg("query")}\" on Spotify 🥺" : $"Spotify said no ({found}) 🥺", null);
                    var (queued, why) = await SpotifyApi.Send("POST", $"/me/player/queue?uri={Uri.EscapeDataString((string?)song["uri"] ?? "")}", control: true);
                    if (queued is >= 200 and < 300)
                        return ($"Added {(string?)song["name"]} by {SpotifyView.Artists(song)} to your queue 📋 it plays after this one", null);
                    Store.Fail("Ivo · Spotify", $"queue: {queued}");
                    return (queued switch { 403 => "Spotify needs Premium for the queue 🎧", 404 => "Start playing something first, then I can queue songs after it 🎧", _ => $"Spotify said no ({queued}) 🥺" }, null);
                }
                case "playing":
                {
                    if (!SpotifyOn) return ("Connect Spotify first: Settings → Connections 🎧", null);
                    var now = await SpotifyApi.Get("/me/player");
                    if (now?["item"] is not { } song) return ("Nothing's playing right now 🎧 say \"play\" and a song", null);
                    var by = SpotifyView.Artists(song);
                    var where = a.Args.ContainsKey("device") ? $" on {(string?)now["device"]?["name"] ?? "Spotify"}" : "";
                    if (where == "") ivoSong = Card(now);
                    return ((bool?)now["is_playing"] == true ? $"You're listening to {(string?)song["name"]}{(by != "" ? " by " + by : "")}{where} 🎶" : $"{(string?)song["name"]}{(by != "" ? " by " + by : "")} is paused{where} ⏸️", null);
                }
                case "play" or "pause" or "next" or "previous":
                {
                    if (!SpotifyOn) return ("Connect Spotify first: Settings → Connections 🎧", null);
                    // what Spotify said when it said no, in its own words, kept in the failures log too
                    string Why(string what, int code, JsonNode? json)
                    {
                        var why = json?["error"] is JsonObject e ? (string?)e["message"] ?? "" : json?["error"]?.ToString() ?? "";
                        Store.Fail("Ivo · Spotify", $"{what}: {code} {why}".Trim());
                        return code switch
                        {
                            401 => "Spotify signed you out 🥺 reconnect it in Settings → Connections",
                            403 => "Spotify needs Premium to play from here 🎧",
                            404 => "Open Spotify on a device first (the app on this PC is fine), then ask me again 🎧",
                            429 => "Spotify says too many requests, give it a minute 😮‍💨",
                            _ => $"Spotify said no ({code}{(why != "" ? ": " + why : "")}) 🥺",
                        };
                    }
                    if (a.Kind == "play" && Arg("query") != "")
                    {
                        var query = Arg("query").Trim();
                        var type = Regex.IsMatch(query, @"\bplaylist\b", RegexOptions.IgnoreCase) ? "playlist"
                            : Regex.IsMatch(query, @"\b(?:artist|songs? by|music by)\b", RegexOptions.IgnoreCase) ? "artist" : "track";
                        var (found, results) = await SpotifyApi.Send("GET", $"/search?q={Uri.EscapeDataString(query)}&type={type}&limit=5");
                        if (!SpotifyApi.Success(found)) return (Why("search", found, results), null);
                        if (!SpotifyPlayback.TrySelect(results, query, out var target))
                            return ($"Couldn't find \"{query}\" on Spotify 🥺", null);
                        // Use the active device when possible. Otherwise wake Spotify and explicitly transfer playback
                        // before sending the selected URI; a 204 from /play alone doesn't prove the song started.
                        var devices = await SpotifyApi.Get("/me/player/devices");
                        var active = devices?["devices"]?.AsArray().FirstOrDefault(d => (bool?)d?["is_active"] == true);
                        var device = (string?)active?["id"] ?? await SpotifyApi.WakeApp();
                        if (device == null) return ("I couldn't find Spotify on any device 🥺 open the Spotify app, then ask me again", null);
                        if (active == null)
                        {
                            var (moved, moveError) = await SpotifyApi.Send("PUT", "/me/player",
                                new JsonObject { ["device_ids"] = new JsonArray(JsonValue.Create(device)), ["play"] = false });
                            if (!SpotifyApi.Success(moved)) return (Why("transfer playback", moved, moveError), null);
                            await Task.Delay(700);
                        }
                        var playedOnce = false;
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            // Do not let SpotifyApi's generic no-active-device retry append another device_id to this
                            // already-targeted request. Every 2xx (including Spotify's expected 204) is accepted.
                            var (played, playError) = await SpotifyApi.Send("PUT",
                                $"/me/player/play?device_id={Uri.EscapeDataString(device)}", target.Body.DeepClone());
                            if (!SpotifyApi.Success(played)) return (Why("play", played, playError), null);
                            playedOnce = true;
                            for (var look = 0; look < 8; look++)
                            {
                                await Task.Delay(600);
                                var now = await SpotifyApi.Get("/me/player");
                                if (SpotifyPlayback.IsPlaying(target, now)) { _ = PollMusic(); ivoSong = Card(now); return ($"Playing {target.Name} 🎵", null); }
                            }
                        }
                        // why: a Free account (Spotify takes the request but won't play it), or what the player shows
                        var me = await SpotifyApi.Get("/me");
                        var state = await SpotifyApi.Get("/me/player");
                        Store.Fail("Ivo · Spotify", $"play: {(playedOnce ? "accepted" : "not accepted")} but requested item {target.Uri} did not start on device {device} · account {(string?)me?["product"] ?? "?"} · player {(state == null ? "none" : $"{(string?)state["device"]?["name"]} playing={(bool?)state["is_playing"]} item={(string?)state["item"]?["name"]}")}");
                        if ((string?)me?["product"] is { } plan && plan != "premium")
                            return ("Spotify only lets apps start music on Premium 🎧 your account is " + plan + ", so I can show what's playing but can't press play", null);
                        return ("Spotify said yes, but nothing started 🥺 open the Spotify app, play anything once, then ask me again", null);
                    }
                    var (method, path) = a.Kind switch { "pause" => ("PUT", "/me/player/pause"), "next" => ("POST", "/me/player/next"), "previous" => ("POST", "/me/player/previous"), _ => ("PUT", "/me/player/play") };
                    var (status, said) = await SpotifyApi.Send(method, path, control: true);
                    if (status is >= 200 and < 300) { _ = PollMusic(); return (a.Kind switch { "pause" => "Paused ⏸️", "next" => "Skipped ⏭️", "previous" => "Back one ⏮️", _ => "Playing ▶️" }, null); }
                    return (Why(a.Kind, status, said), null);
                }
                case "call":
                {
                    var people = IvoContacts(Arg("who"));
                    if (people.Length != 1) return (people.Length == 0 ? $"I can't find {Arg("who")} in your Portal contacts 🥺" : $"Which one: {string.Join(" or ", people.Select(p => p.Name))}?", null);
                    await CallContact(people[0], Arg("video") == "1" ? "video" : "voice");
                    return ($"Calling {people[0].Name} 📞", null);
                }
                case "message":
                {
                    var people = IvoContacts(Arg("who"));
                    if (people.Length != 1) return (people.Length == 0 ? $"I can't find {Arg("who")} in your Portal contacts 🥺" : $"Which one: {string.Join(" or ", people.Select(p => p.Name))}?", null);
                    if (ivoChats == null) { OpenPortal(); return ("Open Portal once, then I can message for you 💬", null); }
                    ivoChats.Sync(S.PortalContacts, PortalMe);
                    if (ivoChats.Book.Threads.FirstOrDefault(t => t.Kind == "dm" && t.ContactBox == people[0].Box) is not { } thread) return ($"I couldn't open your chat with {people[0].Name} 🥺", null);
                    var sent = await ivoChats.Text(thread, Arg("text"));
                    return (sent ? $"Sent to {people[0].Name} 💬" : $"It's in your outbox; it goes to {people[0].Name} as soon as it can 💬", null);
                }
                case "set":
                {
                    var (hit, value, ask, _) = IvoSetting(a);
                    if (ask != null) return (ask, null);
                    if (hit == null) return ($"I couldn't find a setting called \"{Arg("setting")}\" 🥺", null);
                    var undo = IvoSettingsIndex.Apply(hit, value ?? "");
                    return ($"Done: {hit.Label} is now {IvoSettingsIndex.Read(hit)} ✨", undo);
                }
                case "open":
                {
                    if (IvoMind.PlaceFor(Arg("target")) is not { } target) return ($"I'm not sure where \"{Arg("target")}\" is 🥺", null);
                    IvoOpen(target.Tag);
                    return ($"Opened {target.Title} ↗️", null);
                }
            }
        }
        catch (Exception ex) { return ($"That didn't work: {ex.Message} 🥺", null); }
        return ("I'm not sure how to do that yet 🥺", null);
    }
}
