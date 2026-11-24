using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Ivo;

// Two-way sync of the to-do list with one service: a Notion database (an integration secret,
// the database shared with it; a title column and a checkbox column), or Google Tasks (your Google list, signed in through
// the browser). Each side's changes since the last sync win: new items go across, ticking off closes or reopens, edits
// carry over, deletions in Ivo delete there (TodoGone), and items gone from the service (done or deleted there) are ticked off here.
// ponytail: whole list each run, every few minutes; the services' incremental APIs if lists get large.
public static class TodoSync
{
    static readonly HttpClient http = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
    static readonly SemaphoreSlim gate = new(1, 1);
    public static string Status = "";

    public static readonly (string Id, string Name)[] Services = [("notion", "Notion"), ("google", "Google Tasks")];
    public static string Service(Settings s) => s.TodoService != "" ? s.TodoService : "";
    public static string Name(Settings s) => Services.FirstOrDefault(x => x.Id == Service(s)).Name ?? "";
    public static bool On(Settings s) => Service(s) switch
    {
        "notion" => s.NotionTodoDb != "" && NotionToken(s) != "",
        "google" => s.GoogleTasksToken != "",
        _ => false,
    };
    static string NotionToken(Settings s) => Connections.FirstToken("notion");

    // another service from now on: the old one's ids mean nothing there, so everything goes across as new
    public static void Switch(Settings s, string service)
    {
        if (Service(s) == service) return;
        s.TodoService = service; s.TodoGone.Clear();
        foreach (var t in s.Todos) { t.RemoteId = null; t.SyncedText = ""; t.SyncedDone = false; }
    }

    // a to-do removed in Ivo: remembered so the next sync deletes it there too
    public static void Removed(Settings s, Todo t) { if (t.RemoteId != null) s.TodoGone.Add(t.RemoteId); }

    // what the sync needs from a service
    record Remote(Func<Task<Dictionary<string, string>>> Open, Func<string, Task<string?>> Create, Func<string, string, Task> Rename,
                  Func<string, Task> Close, Func<string, Task> Reopen, Func<string, Task> Delete);

    public static async Task<bool> Run(Settings s)
    {
        if (!On(s) || !await gate.WaitAsync(0)) return false;
        var name = Name(s);
        try
        {
            var remote = Service(s) switch { "notion" => await Notion(s), _ => await Google(s) };
            var open = await remote.Open();

            foreach (var id in s.TodoGone.ToList()) { await remote.Delete(id); open.Remove(id); s.TodoGone.Remove(id); }

            foreach (var t in s.Todos.Where(t => t.RemoteId != null).ToList())
            {
                var id = t.RemoteId!;
                if (!open.TryGetValue(id, out var text))   // not open there: done or deleted there
                {
                    if (!t.Done && t.SyncedDone) { await remote.Reopen(id); t.SyncedDone = false; }   // reopened here
                    else { t.Done = true; t.SyncedDone = true; }
                    continue;
                }
                if (t.Done) { await remote.Close(id); t.SyncedDone = true; continue; }
                if (t.Text != t.SyncedText) await remote.Rename(id, t.Text);   // edited here
                else if (text != t.SyncedText) t.Text = text;                 // edited there
                t.SyncedText = t.Text; t.SyncedDone = false;
            }
            foreach (var t in s.Todos.Where(t => t.RemoteId == null && !t.Done))   // new here
            {
                t.RemoteId = await remote.Create(t.Text); t.SyncedText = t.Text;
            }
            var known = s.Todos.Select(t => t.RemoteId).OfType<string>().ToHashSet();
            foreach (var (id, text) in open.Where(r => !known.Contains(r.Key)))   // new there
                s.Todos.Add(new Todo { Text = text, RemoteId = id, SyncedText = text });

            Status = $"Synced with {name} {DateTime.Now:HH:mm}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"{name} sync failed: " + ex.Message;
            Store.Fail("To-do sync · " + name, ex.Message);
            return false;
        }
        finally { gate.Release(); }
    }

    // one JSON call; a 404 on a change means it's already gone there
    static async Task<JsonNode?> Send(string service, HttpMethod m, string url, Action<HttpRequestMessage> auth, JsonNode? body = null)
    {
        using var req = new HttpRequestMessage(m, url);
        auth(req);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound && m != HttpMethod.Get) return null;
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{service} {(int)res.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        return text.Trim() == "" ? null : JsonNode.Parse(text);
    }

    // ---------- Notion: a database's rows, its title column as the text, its first checkbox column as done ----------
    static async Task<Remote> Notion(Settings s)
    {
        var token = NotionToken(s);
        var db = s.NotionTodoDb;
        Task<JsonNode?> Call(HttpMethod m, string path, JsonNode? body = null) =>
            Send("Notion", m, "https://api.notion.com/v1" + path, r => { r.Headers.Authorization = new("Bearer", token); r.Headers.Add("Notion-Version", "2022-06-28"); }, body);
        var props = (await Call(HttpMethod.Get, $"/databases/{db}"))?["properties"]?.AsObject() ?? [];
        var title = props.FirstOrDefault(p => (string?)p.Value?["type"] == "title").Key ?? throw new InvalidOperationException("the database has no title column");
        var check = props.FirstOrDefault(p => (string?)p.Value?["type"] == "checkbox").Key ?? throw new InvalidOperationException("add a checkbox column to the database (it marks a to-do done)");
        JsonObject Title(string text) => new() { [title] = new JsonObject { ["title"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = text } }) } };
        Task Tick(string id, bool done) => Call(HttpMethod.Patch, $"/pages/{id}", new JsonObject { ["properties"] = new JsonObject { [check] = new JsonObject { ["checkbox"] = done } } });
        return new(
            async () =>
            {
                var open = new Dictionary<string, string>();
                string? cursor = null;
                do
                {
                    var q = new JsonObject { ["filter"] = new JsonObject { ["property"] = check, ["checkbox"] = new JsonObject { ["equals"] = false } }, ["page_size"] = 100 };
                    if (cursor != null) q["start_cursor"] = cursor;
                    var page = await Call(HttpMethod.Post, $"/databases/{db}/query", q);
                    foreach (var row in page?["results"]?.AsArray() ?? [])
                        if ((string?)row?["id"] is { } id)
                            open[id] = string.Concat(row!["properties"]?[title]?["title"]?.AsArray().Select(x => (string?)x?["plain_text"]) ?? []);
                    cursor = (bool?)page?["has_more"] == true ? (string?)page?["next_cursor"] : null;
                } while (cursor != null);
                return open;
            },
            async text => (string?)(await Call(HttpMethod.Post, "/pages", new JsonObject { ["parent"] = new JsonObject { ["database_id"] = db }, ["properties"] = Title(text) }))?["id"],
            (id, text) => Call(HttpMethod.Patch, $"/pages/{id}", new JsonObject { ["properties"] = Title(text) }),
            id => Tick(id, true),
            id => Tick(id, false),
            id => Call(HttpMethod.Patch, $"/pages/{id}", new JsonObject { ["archived"] = true }));
    }

    // the Notion databases shared with the integration, and whether each has a checkbox column (needed to mark done)
    public static async Task<List<(string Id, string Title, bool HasCheckbox)>> NotionDatabases(Settings s)
    {
        var token = NotionToken(s);
        var found = new List<(string, string, bool)>();
        try
        {
            var res = await Send("Notion", HttpMethod.Post, "https://api.notion.com/v1/search",
                r => { r.Headers.Authorization = new("Bearer", token); r.Headers.Add("Notion-Version", "2022-06-28"); },
                new JsonObject { ["filter"] = new JsonObject { ["property"] = "object", ["value"] = "database" }, ["page_size"] = 50 });
            foreach (var d in res?["results"]?.AsArray() ?? [])
                if ((string?)d?["id"] is { } id)
                    found.Add((id.Replace("-", ""), string.Concat(d!["title"]?.AsArray().Select(x => (string?)x?["plain_text"]) ?? []) is { Length: > 0 } t ? t : "Untitled",
                               d["properties"]?.AsObject().Any(p => (string?)p.Value?["type"] == "checkbox") == true));
        }
        catch { }   // can't list them: the link is asked for instead
        return found;
    }

    // a Notion database id from a pasted link or id
    public static string? NotionDbId(string link)
    {
        var m = System.Text.RegularExpressions.Regex.Match(link.Replace("-", ""), "[0-9a-fA-F]{32}");
        return m.Success ? m.Value : null;
    }

    // ---------- Google Tasks: your default list ----------
    static async Task<Remote> Google(Settings s)
    {
        var access = await GoogleTasks.Access(s);
        const string Base = "https://tasks.googleapis.com/tasks/v1/lists/@default/tasks";
        Task<JsonNode?> Call(HttpMethod m, string path, JsonNode? body = null) => Send("Google Tasks", m, Base + path, r => r.Headers.Authorization = new("Bearer", access), body);
        return new(
            async () =>
            {
                var open = new Dictionary<string, string>();
                string? next = null;
                do
                {
                    var page = await Call(HttpMethod.Get, "?showCompleted=false&maxResults=100" + (next != null ? "&pageToken=" + Uri.EscapeDataString(next) : ""));
                    foreach (var t in page?["items"]?.AsArray() ?? []) if ((string?)t?["id"] is { } id) open[id] = (string?)t!["title"] ?? "";
                    next = (string?)page?["nextPageToken"];
                } while (next != null);
                return open;
            },
            async text => (string?)(await Call(HttpMethod.Post, "", new JsonObject { ["title"] = text }))?["id"],
            (id, text) => Call(HttpMethod.Patch, "/" + id, new JsonObject { ["title"] = text }),
            id => Call(HttpMethod.Patch, "/" + id, new JsonObject { ["status"] = "completed" }),
            id => Call(HttpMethod.Patch, "/" + id, new JsonObject { ["status"] = "needsAction", ["completed"] = null }),
            id => Call(HttpMethod.Delete, "/" + id));
    }
}

// Google sign-in for Google Tasks, through the browser (OAuth for desktop apps, PKCE and a loopback address). Google needs
// an app registered in Google Cloud, so you paste your own desktop app's client id and secret once; Ivo keeps the refresh
// token and trades it for an hour-long access token when needed.
public static class GoogleTasks
{
    const string Scope = "https://www.googleapis.com/auth/tasks";
    static readonly HttpClient Web = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
    static string access = "";
    static DateTime expires;

    static (string Id, string Secret) Client(Settings s)
    {
        var c = Store.Unprotect(s.GoogleTasksClient).Split('|', 2);
        return (c[0], c.Length > 1 ? c[1] : "");
    }

    // Opens Google's sign-in page and waits (up to 3 minutes) for it to come back; keeps the refresh token in settings.
    public static async Task SignIn(Settings s)
    {
        var (id, secret) = Client(s);
        var verifier = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Guid.NewGuid().ToString("N");
        var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);   // any free port: Google allows them all for desktop apps
        port.Start(); var free = ((System.Net.IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        var redirect = $"http://127.0.0.1:{free}/";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent&client_id=" + Uri.EscapeDataString(id) +
                  "&scope=" + Uri.EscapeDataString(Scope) + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&state=" + state +
                  "&code_challenge_method=S256&code_challenge=" + challenge;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        var got = listener.GetContextAsync();
        if (await Task.WhenAny(got, Task.Delay(TimeSpan.FromMinutes(3))) != got) throw new TimeoutException("No answer from Google. Try again.");
        var ctx = await got;
        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
        var ok = q["state"] == state && !string.IsNullOrEmpty(q["code"]);
        var message = ok ? "Google Tasks is connected to Ivo. You can close this tab." : "Sign-in did not finish. You can close this tab.";
        var page = Encoding.UTF8.GetBytes("<html><body style=\"background:#000;color:#eee;font:16px Segoe UI;display:grid;place-items:center;height:90vh\">" + message + "</body></html>");
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(page);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException(q["error"] is { } e ? "Google said: " + e : "Sign-in did not finish.");
        var t = await Token(s, new() { ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = redirect, ["code_verifier"] = verifier });
        s.GoogleTasksToken = Store.Protect((string?)t["refresh_token"] ?? throw new InvalidOperationException("Google sent no refresh token. Try again."));
    }

    // a fresh access token (renewed a minute before it runs out)
    public static async Task<string> Access(Settings s)
    {
        if (access != "" && DateTime.UtcNow < expires) return access;
        await Token(s, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = Store.Unprotect(s.GoogleTasksToken) });
        return access;
    }

    public static void Forget() => access = "";

    static async Task<JsonNode> Token(Settings s, Dictionary<string, string> form)
    {
        var (id, secret) = Client(s);
        form["client_id"] = id; form["client_secret"] = secret;
        using var res = await Web.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Google sign-in failed: " + (text.Length > 200 ? text[..200] : text));
        var t = JsonNode.Parse(text)!;
        access = (string?)t["access_token"] ?? "";
        expires = DateTime.UtcNow.AddSeconds(((int?)t["expires_in"] ?? 3600) - 60);
        return t;
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
