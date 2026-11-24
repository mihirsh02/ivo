using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Ivo;

// Your accounts on other services, reached with a personal token you paste in Settings → Connections (kept encrypted
// like API keys). Each connected service is one tool the model calls with the service's own REST API, which models
// know well: reading runs freely, anything that changes something asks first (unless the chat allows everything).
public static class Connections
{
    public record Service(string Id, string Name, string Icon, string Base, string TokenUrl, string TokenHelp, string Check, string WhoField, string Hint);

    public static readonly Service[] All =
    [
        new("github", "GitHub", "", "https://api.github.com", "https://github.com/settings/personal-access-tokens/new",
            "A fine-grained access token.",
            "/user", "login",
            "GitHub's REST API (https://api.github.com). Examples: GET /user/repos?sort=updated, GET /repos/{owner}/{repo}/issues, GET /search/issues?q=..., POST /repos/{owner}/{repo}/issues with {\"title\",\"body\"}."),
        new("gitlab", "GitLab", "", "https://gitlab.com/api/v4", "https://gitlab.com/-/user_settings/personal_access_tokens",
            "An access token with read_api (or api to edit).",
            "/user", "username",
            "GitLab's REST API v4 (https://gitlab.com/api/v4). Examples: GET /projects?membership=true&order_by=last_activity_at, GET /projects/{url-encoded path}/issues, GET /merge_requests?scope=assigned_to_me&state=opened."),
        new("notion", "Notion", "", "https://api.notion.com/v1", "https://www.notion.so/profile/integrations",
            "An integration secret. Share pages with it in Notion (••• → Connections).",
            "/users/me", "name",
            "Notion's API (https://api.notion.com/v1, version 2022-06-28). Examples: POST /search with {\"query\":\"...\"}, GET /pages/{id}, GET /blocks/{id}/children, POST /pages to create, PATCH /blocks/{id}/children to append."),
        new("slack", "Slack", "", "https://slack.com/api", "https://api.slack.com/apps",
            "A user token (xoxp-…) from your Slack app's OAuth & Permissions page.",
            "/auth.test", "user",
            "Slack's Web API (https://slack.com/api). Answers carry \"ok\":false on errors. Examples: GET /conversations.list?types=public_channel,private_channel,im, GET /conversations.history?channel=..., GET /search.messages?query=..., POST /chat.postMessage with {\"channel\",\"text\"}."),
        new("figma", "Figma", "", "https://api.figma.com/v1", "https://www.figma.com/settings",
            "A personal access token (Settings → Security).",
            "/me", "handle",
            "Figma's REST API (https://api.figma.com/v1). Examples: GET /files/{key} (key from the file's link), GET /files/{key}/comments, POST /files/{key}/comments with {\"message\"}, GET /images/{key}?ids=..."),
        new("spotify", "Spotify", "", "https://api.spotify.com/v1", "", "Sign in with your Spotify account.",
            "/me", "display_name",
            "Spotify's Web API (https://api.spotify.com/v1). Examples: GET /me/player/currently-playing, PUT /me/player/pause, PUT /me/player/play (body {\"uris\":[...]} or {\"context_uri\":...}), POST /me/player/next, GET /search?q=...&type=track, GET /me/playlists, GET /me/top/tracks, POST /playlists/{id}/tracks. Controlling playback needs Spotify Premium. With nothing playing anywhere, playback calls start the Spotify app on this PC."),
    ];

    // Settings.Connections: account key → protected token. The key is the service id ("notion"), or "id:Name" for a second
    // account of the same service ("notion:Work"); each account is its own tool, named from its key.
    public static Dictionary<string, string> Tokens = [];

    public record Account(Service Svc, string Key)
    {
        public string Label => Key.Contains(':') ? Key[(Key.IndexOf(':') + 1)..] : "";
        public string Name => Label != "" ? $"{Svc.Name} ({Label})" : Svc.Name;
        public string Icon => Svc.Icon;
        public string Tool => System.Text.RegularExpressions.Regex.Replace(Key.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
    }
    public static string Key(Service s, string label) => label.Trim() == "" ? s.Id : $"{s.Id}:{label.Trim()}";
    public static IEnumerable<Account> Accounts => Tokens.Keys.Select(k => All.FirstOrDefault(s => s.Id == k.Split(':')[0]) is { } s ? new Account(s, k) : null).OfType<Account>()
                                                     .OrderBy(a => Array.IndexOf(All, a.Svc)).ThenBy(a => a.Key);

    static string Token(string key) { try { return Tokens.TryGetValue(key, out var t) ? Store.Unprotect(t) : ""; } catch { return ""; } }   // Spotify: its refresh token
    public static string FirstToken(string serviceId) => Accounts.FirstOrDefault(a => a.Svc.Id == serviceId) is { } a ? Token(a.Key) : "";
    public static Action? Changed;   // a token was renewed: save settings
    public static Task<string> SpotifyAccess() => Spotify.Access(Token("spotify"));
    public static bool Connected(Service s) => Accounts.Any(a => a.Svc == s);

    public static Account? Find(string toolName) => Accounts.FirstOrDefault(a => a.Tool == toolName);

    const string Schema = """{"type":"object","properties":{"method":{"type":"string","enum":["GET","POST","PATCH","PUT","DELETE"]},"path":{"type":"string","description":"Path after the API base, with any query string, e.g. /user"},"body":{"type":"object","description":"JSON body for POST/PATCH/PUT"}},"required":["method","path"]}""";

    public static IEnumerable<ToolDef> Tools => Accounts.Select(a => new ToolDef(a.Tool, $"Call the user's {a.Name} account. {a.Svc.Hint}", Schema, false));

    // Reading is free; so is pausing or skipping a song (Spotify's player), which changes nothing that lasts
    public static bool Reads(string argsJson)
    {
        try
        {
            var a = JsonNode.Parse(argsJson);
            return ((string?)a?["method"] ?? "GET").ToUpperInvariant() == "GET" || ((string?)a?["path"] ?? "").StartsWith("/me/player");
        }
        catch { return false; }
    }

    static readonly HttpClient Web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(30) };

    static HttpRequestMessage Request(Service s, string token, HttpMethod method, string path, JsonNode? body)
    {
        var req = new HttpRequestMessage(method, s.Base + (path.StartsWith('/') ? path : "/" + path));
        req.Headers.UserAgent.ParseAdd("Ivo");
        switch (s.Id)
        {
            case "github": req.Headers.Add("Authorization", "Bearer " + token); req.Headers.Add("Accept", "application/vnd.github+json"); req.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); break;
            case "gitlab": req.Headers.Add("PRIVATE-TOKEN", token); break;
            case "notion": req.Headers.Add("Authorization", "Bearer " + token); req.Headers.Add("Notion-Version", "2022-06-28"); break;
            case "figma": req.Headers.Add("X-Figma-Token", token); break;
            default: req.Headers.Add("Authorization", "Bearer " + token); break;   // Spotify, Slack
        }
        if (body != null && method != HttpMethod.Get) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    public static async Task<string> Call(Account a, JsonNode args, bool planMode, CancellationToken ct)
    {
        var s = a.Svc;
        var method = new HttpMethod(((string?)args["method"] ?? "GET").ToUpperInvariant());
        var path = (string?)args["path"] ?? "";
        if (planMode && method != HttpMethod.Get) return "Blocked: Plan mode can only read.";
        if (path.Contains("://")) return "Error: give a path after the API base, not a full address.";
        if (s.Id == "spotify")
        {
            var (code, json) = await SpotifyApi.Send(method.Method, path, args["body"], control: method != HttpMethod.Get);
            return $"HTTP {code}\n\n{json?.ToJsonString() ?? ""}";
        }
        var token = Token(a.Key);
        using var res = await Web.SendAsync(Request(s, token, method, path, args["body"]), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (text.Length > 20_000) text = text[..20_000] + "\n…(truncated)";
        return $"HTTP {(int)res.StatusCode}\n\n{text}";
    }

    // Settings: is this token good? Returns who it signs in as, or throws with the service's reason.
    public static async Task<string> Check(Service s, string token)
    {
        using var res = await Web.SendAsync(Request(s, token, HttpMethod.Get, s.Check, null));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{s.Name} said {(int)res.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        var json = JsonNode.Parse(text);
        if ((bool?)json?["ok"] == false) throw new InvalidOperationException($"{s.Name} said: {(string?)json!["error"]}");   // Slack answers 200 even for a bad token
        try { return (string?)s.WhoField.Split('.').Aggregate(json, (n, k) => n?[k]) ?? "connected"; } catch { return "connected"; }   // "data.name": a nested field
    }
}

// Spotify signs in through the browser (OAuth with PKCE: no secret in the app). Ivo keeps the refresh token and trades it
// for an hour-long access token when needed.
public static class Spotify
{
    const string ClientId = "807ff7d885eb472faec06058441d90d5";   // Ivo's app on developer.spotify.com (a public id, not a secret)
    const string Redirect = "http://127.0.0.1:43821/callback";
    const string Scopes = "user-follow-read user-follow-modify user-read-playback-state user-modify-playback-state user-read-currently-playing user-read-recently-played user-top-read " +
                          "playlist-read-private playlist-modify-private playlist-modify-public user-library-read user-library-modify user-read-private";
    static readonly HttpClient Web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
    static string access = "";
    static DateTime expires;
    static readonly SemaphoreSlim Renewing = new(1, 1);   // one refresh at a time: Spotify may rotate the refresh token

    // Opens Spotify's sign-in page and waits (up to 3 minutes) for it to come back; returns the refresh token.
    public static async Task<string> SignIn()
    {
        var verifier = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Guid.NewGuid().ToString("N");
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:43821/callback/");
        listener.Start();
        var url = "https://accounts.spotify.com/authorize?response_type=code&client_id=" + ClientId + "&scope=" + Uri.EscapeDataString(Scopes) +
                  "&redirect_uri=" + Uri.EscapeDataString(Redirect) + "&state=" + state + "&code_challenge_method=S256&code_challenge=" + challenge;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        var got = listener.GetContextAsync();
        if (await Task.WhenAny(got, Task.Delay(TimeSpan.FromMinutes(3))) != got) throw new TimeoutException("No answer from Spotify. Try again.");
        var ctx = await got;
        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
        var ok = q["state"] == state && !string.IsNullOrEmpty(q["code"]);
        var message = ok ? "Spotify is connected to Ivo. You can close this tab." : "Sign-in did not finish. You can close this tab.";
        var page = Encoding.UTF8.GetBytes("<html><body style=\"background:#000;color:#eee;font:16px Segoe UI;display:grid;place-items:center;height:90vh\">" + message + "</body></html>");
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(page);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException(q["error"] is { } e ? "Spotify said: " + e : "Sign-in did not finish.");
        var t = await Token(new() { ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier });
        return (string?)t["refresh_token"] ?? "";
    }

    // a fresh access token (renewed a minute before it runs out)
    public static async Task<string> Access(string refresh)
    {
        await Renewing.WaitAsync();
        try
        {
            if (access != "" && DateTime.UtcNow < expires) return access;
            var t = await Token(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh });
            if ((string?)t["refresh_token"] is { Length: > 0 } next && next != refresh)   // Spotify may hand out a new one: keep it
            {
                Connections.Tokens["spotify"] = Store.Protect(next);
                Connections.Changed?.Invoke();
            }
            return access;
        }
        finally { Renewing.Release(); }
    }

    public static void Forget() => access = "";

    static async Task<JsonNode> Token(Dictionary<string, string> form)
    {
        form["client_id"] = ClientId;
        using var res = await Web.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(text.TrimStart().StartsWith('<')   // a web page, not Spotify's JSON: something in between answered
                ? text.Contains("Blocked", StringComparison.OrdinalIgnoreCase) || text.Contains("policy", StringComparison.OrdinalIgnoreCase)
                    ? "Your network blocks Spotify (a web filter answered instead of Spotify). Try another network, or ask your IT team to allow accounts.spotify.com and api.spotify.com."
                    : $"Spotify sign-in failed: Spotify's server answered with an error page ({(int)res.StatusCode}). Try again later."
                : "Spotify sign-in failed: " + text);
        var t = JsonNode.Parse(text)!;
        access = (string?)t["access_token"] ?? "";
        expires = DateTime.UtcNow.AddSeconds(((int?)t["expires_in"] ?? 3600) - 60);
        return t;
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// Ivo's Spotify remote: one Web API call at a time. Playing needs a Spotify app somewhere (phone, speaker, this PC);
// when nothing is open anywhere, a play/pause/skip starts the Spotify app on this PC, minimised, and plays there.
public static class SpotifyApi
{
    static readonly HttpClient Web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(20) };
    public static bool Success(int status) => status is >= 200 and < 300;  // 204 No Content is Spotify's normal player success

    public static async Task<(int Status, JsonNode? Body)> Send(string method, string path, JsonNode? body = null, bool control = false)
    {
        var (status, json) = await Once(method, path, body);
        if (control && status == 404 && await WakeApp() is { } device)   // "no active device": the app on this PC takes it
            (status, json) = await Once(method, path + (path.Contains('?') ? "&" : "?") + "device_id=" + device, body);
        return (status, json);
    }

    static async Task<(int, JsonNode?)> Once(string method, string path, JsonNode? body)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), "https://api.spotify.com/v1" + path);
        req.Headers.Add("Authorization", "Bearer " + await Connections.SpotifyAccess());
        if (body != null || method is "PUT" or "POST") req.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json");
        using var res = await Web.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { if (text.Length > 0) json = JsonNode.Parse(text); } catch { }
        return ((int)res.StatusCode, json);
    }

    public static async Task<JsonNode?> Get(string path) { var (s, b) = await Send("GET", path); return s is >= 200 and < 300 ? b : null; }

    // A device to play on: an open Spotify app (this PC's first), or the app here, started minimised. Null if there's no app.
    public static async Task<string?> WakeApp()
    {
        string? Pick(JsonNode? d) =>
            d?["devices"]?.AsArray().OrderByDescending(x => (string?)x?["type"] == "Computer").Select(x => (string?)x?["id"]).FirstOrDefault(x => x != null);
        if (Pick(await Get("/me/player/devices")) is { } open) return open;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
        try
        {
            if (File.Exists(exe)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--minimized") { UseShellExecute = false });
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("spotify:") { UseShellExecute = true });   // the Microsoft Store version
        }
        catch { return null; }
        for (int i = 0; i < 20; i++)   // it takes a few seconds to sign in and show up
        {
            await Task.Delay(1000);
            if (Pick(await Get("/me/player/devices")) is { } d) return d;
        }
        return null;
    }
}

// A Spotify search hit and the exact play request it represents. Keeping selection and verification together prevents a
// successful-but-empty API response from being mistaken for starting the requested song.
public sealed record SpotifyPlayTarget(string Name, string Uri, string[] Artists, JsonObject Body, bool Track);

public static class SpotifyPlayback
{
    public static bool TrySelect(JsonNode? search, string query, out SpotifyPlayTarget target)
    {
        target = null!;
        var kind = System.Text.RegularExpressions.Regex.IsMatch(query, @"\bplaylist\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? "playlists"
            : System.Text.RegularExpressions.Regex.IsMatch(query, @"\b(?:artist|songs? by|music by)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                ? "artists" : "tracks";
        if (search?[kind]?["items"] is not JsonArray items) return false;
        var hit = items.FirstOrDefault(x => x != null && ((string?)x["uri"])?.StartsWith("spotify:", StringComparison.Ordinal) == true);
        if (hit == null || (string?)hit["uri"] is not { Length: > 0 } uri || (string?)hit["name"] is not { Length: > 0 } name) return false;

        var isTrack = kind == "tracks";
        var artists = isTrack
            ? (hit["artists"] as JsonArray ?? []).Select(x => (string?)x?["name"]).OfType<string>().ToArray()
            : [];
        var body = isTrack
            ? new JsonObject { ["uris"] = new JsonArray(JsonValue.Create(uri)) }
            : new JsonObject { ["context_uri"] = uri };
        target = new(name, uri, artists, body, isTrack);
        return true;
    }

    public static bool IsPlaying(SpotifyPlayTarget target, JsonNode? player)
    {
        if ((bool?)player?["is_playing"] != true) return false;
        if (!target.Track) return (string?)player?["context"]?["uri"] == target.Uri;
        var item = player?["item"];
        if ((string?)item?["uri"] == target.Uri || (string?)item?["linked_from"]?["uri"] == target.Uri) return true;
        // Spotify can relink an unavailable regional track. In that case the URI changes, but the title and artist
        // still identify the selected search result (unlike title-only matching, which can accept the wrong song).
        var titleMatches = string.Equals((string?)item?["name"], target.Name, StringComparison.OrdinalIgnoreCase);
        var artists = (item?["artists"] as JsonArray ?? []).Select(x => (string?)x?["name"]).OfType<string>();
        var leadArtist = target.Artists.FirstOrDefault();
        return titleMatches && leadArtist != null && artists.Any(actual =>
            string.Equals(actual, leadArtist, StringComparison.OrdinalIgnoreCase));
    }
}
