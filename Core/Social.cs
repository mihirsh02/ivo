using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ivo;

// YouTube and Reddit. YouTube: each channel's public feed of new videos (the one feed readers use), no account.
// Reddit: its official API, read-only, as an "installed app" (an app id you register free at reddit.com/prefs/apps; no
// secret, no sign-in, no Reddit account linked). Only the channel or subreddit asked about is sent; nothing about you.
public static class Social
{
    static readonly HttpClient web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(12), DefaultRequestHeaders = { { "User-Agent", "Ivo/1.0 (desktop app; reads public feeds)" } } };

    public record Video(string Title, string Url, DateTime Published, string Channel);
    public record Post(string Title, string Url, int Score, int Comments, string Sub);

    // ---------- YouTube ----------
    // a channel by its @handle, its link, or its name (YouTube's own search, first channel found)
    public static async Task<YouTubeChannel?> FindChannel(string who)
    {
        who = who.Trim().TrimEnd('?', '.', '!');
        try
        {
            if (Regex.Match(who, @"(UC[\w-]{22})") is { Success: true } id) return new() { Id = id.Value, Name = who };
            var page = who.StartsWith('@') || !who.Contains(' ') && Regex.IsMatch(who, @"^[\w.-]+$")
                ? await web.GetStringAsync("https://www.youtube.com/@" + Uri.EscapeDataString(who.TrimStart('@')))
                : null;
            if (page == null || !Regex.IsMatch(page, @"""externalId"":""UC"))
                page = await web.GetStringAsync("https://www.youtube.com/results?sp=EgIQAg%253D%253D&search_query=" + Uri.EscapeDataString(who));   // channels only
            var channel = Regex.Match(page, @"""(?:externalId|channelId)"":""(UC[\w-]{22})""");
            if (!channel.Success) return null;
            var name = Regex.Match(page, @"<meta property=""og:title"" content=""([^""]+)""") is { Success: true } og && page.Contains("\"externalId\"")
                ? System.Net.WebUtility.HtmlDecode(og.Groups[1].Value)
                : Regex.Match(page, @"""title"":\{""simpleText"":""([^""]+)""") is { Success: true } t ? t.Groups[1].Value : who;
            return new() { Id = channel.Groups[1].Value, Name = name };
        }
        catch { return null; }
    }

    public static async Task<List<Video>> Videos(YouTubeChannel c)
    {
        try { return ParseFeed(await web.GetStringAsync("https://www.youtube.com/feeds/videos.xml?channel_id=" + c.Id), c.Name); }
        catch { return []; }
    }

    // a channel's Atom feed: its newest videos, newest first
    public static List<Video> ParseFeed(string xml, string channel)
    {
        XNamespace a = "http://www.w3.org/2005/Atom";
        var doc = XDocument.Parse(xml);
        var name = (string?)doc.Root?.Element(a + "title") ?? channel;
        return doc.Root?.Elements(a + "entry").Select(e => new Video(
            (string?)e.Element(a + "title") ?? "",
            (string?)e.Element(a + "link")?.Attribute("href") ?? "",
            DateTime.TryParse((string?)e.Element(a + "published"), out var p) ? p : DateTime.MinValue,
            name)).Where(v => v.Url != "").OrderByDescending(v => v.Published).ToList() ?? [];
    }

    // ---------- Reddit ----------
    public static string RedditApp = "";   // the app id (Settings → Assistant)
    static string? redditToken; static DateTime redditUntil;

    // an app-only token for an installed app: read-only, nothing of yours
    static async Task<string?> RedditToken()
    {
        if (redditToken != null && DateTime.UtcNow < redditUntil) return redditToken;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.reddit.com/api/v1/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "https://oauth.reddit.com/grants/installed_client", ["device_id"] = "DO_NOT_TRACK_THIS_DEVICE" }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(RedditApp + ":")));
        using var res = await web.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        redditToken = (string?)j?["access_token"];
        redditUntil = DateTime.UtcNow.AddSeconds(((int?)j?["expires_in"] ?? 3600) - 60);
        return redditToken;
    }

    // range: hour, day, week, month, year, all (top) · sort: top, hot, new. Null: no app id, or Reddit didn't answer.
    public static async Task<List<Post>?> Posts(string sub, string sort, string range)
    {
        if (RedditApp == "") return null;
        try
        {
            if (await RedditToken() is not { } token) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://oauth.reddit.com/r/{Uri.EscapeDataString(sub)}/{sort}?limit=8&raw_json=1" + (sort == "top" ? "&t=" + range : ""));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var res = await web.SendAsync(req);
            return res.IsSuccessStatusCode ? ParseListing(await res.Content.ReadAsStringAsync()) : null;
        }
        catch { return null; }
    }

    public static List<Post> ParseListing(string json) =>
        (JsonNode.Parse(json)?["data"]?["children"] as JsonArray ?? []).Select(c => c?["data"]).Where(d => d != null && (bool?)d["stickied"] != true && (bool?)d["over_18"] != true)
            .Select(d => new Post((string?)d!["title"] ?? "", "https://www.reddit.com" + (string?)d["permalink"], (int?)d["score"] ?? 0, (int?)d["num_comments"] ?? 0, (string?)d["subreddit"] ?? ""))
            .ToList();
}

public class YouTubeChannel { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
