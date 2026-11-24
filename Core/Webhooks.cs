using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Ivo;

// Posting to chat apps through their incoming webhooks: each kind wants its own JSON. Only what an event delivers is sent
// (a reminder's words, a task's result, the briefing, a finished focus session), to the addresses you added.
public static class Webhooks
{
    static readonly HttpClient http = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(15) };

    public static readonly (string Id, string Name, string Glyph, string Color)[] Kinds =
    [
        ("slack", "Slack", "", "#E01E5A"), ("discord", "Discord", "", "#5865F2"), ("teams", "Microsoft Teams", "", "#6264A7"),
        ("gchat", "Google Chat", "", "#34A853"), ("custom", "Any URL (JSON)", "", "#94A3B8"),
    ];

    public static readonly (string Id, string Name)[] Events =
        [("reminders", "Reminders"), ("tasks", "Task results"), ("briefing", "Morning briefing"), ("focus", "Focus sessions")];

    public static string Event(InboxItem i) => i.Kind switch { "reminder" => "reminders", "briefing" => "briefing", "daily" or "dev" => "tasks", _ => "" };

    // the message as each service expects it (Discord caps a message at 2000 characters)
    static string Body(Webhook h, string ev, string title, string text)
    {
        text = text.Length > 1800 ? text[..1800] + "…" : text;
        JsonObject o = h.Kind switch
        {
            "slack" or "gchat" => new() { ["text"] = $"*{title}*\n{text}" },
            "discord" => new() { ["content"] = $"**{title}**\n{text}" },
            "teams" => new() { ["text"] = $"**{title}**\n\n{text}" },
            _ => new() { ["event"] = ev, ["title"] = title, ["text"] = text, ["at"] = DateTime.Now.ToString("o"), ["from"] = "Ivo" },
        };
        return o.ToJsonString();
    }

    // null when it went through, otherwise why not
    public static async Task<string?> Send(Webhook h, string ev, string title, string text)
    {
        if (!Uri.TryCreate(h.Url, UriKind.Absolute, out var u) || u.Scheme != "https") return "The address must start with https://";
        try
        {
            var res = await http.PostAsync(u, new StringContent(Body(h, ev, title, text), Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode ? null : $"{(int)res.StatusCode} {res.ReasonPhrase}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static void Post(Settings s, string ev, string title, string text)
    {
        foreach (var h in s.Webhooks.Where(h => h.Events.Contains(ev))) _ = Send(h, ev, title, text);
    }
}
