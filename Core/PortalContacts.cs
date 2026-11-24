using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ivo;

// Portal contacts: after one connection, the two of you can keep each other. Saving a contact shares a private mailbox
// (a random name) and a key between your two Portals, and nothing else. To reach a contact later, your Portal makes an
// invite and leaves it, sealed with that key, in that mailbox on the relay; their Ivo or Ivo Portal, while it's running,
// receives a Portal call request and on Accept joins: no codes to swap. The relay only ever sees scrambled
// text in a mailbox whose name tells it nothing.
public class PortalContact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Box { get; set; } = "";   // the shared mailbox's name
    public string Key { get; set; } = "";   // the shared key (base64url)
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? Removed { get; set; }   // in Recently deleted since then (restorable); gone for both of you after 15 days

    // Recently deleted: a removed contact waits here 15 days, then is deleted for good, for both of you
    public static readonly TimeSpan Keep = TimeSpan.FromDays(15);
    public int DaysLeft => Removed is { } r ? Math.Max(0, (int)Math.Ceiling((r + Keep - DateTime.Now).TotalDays)) : 0;
    public bool Expired => Removed is { } r && DateTime.Now - r > Keep;
}

public static class PortalContacts
{
    static string Topic(PortalContact c) => "ivo-portal-c-" + c.Box;
    // Both sides listen in the shared mailbox, so your own request comes back to you too.
    static readonly string Self = Guid.NewGuid().ToString("N")[..12];

    // a new pair of mailbox and key, offered to the person you're connected with
    public static PortalContact Make(string theirName) => new()
    {
        Name = theirName,
        Box = Portal.B64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18)),
        Key = Portal.B64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
    };

    // Cancel a pending Portal call request: their ringing stops and they see a missed call.
    public const string Cancelled = "cancel";
    public static Task<bool> CancelRequest(PortalContact c, string invite) => Post(c, "unknock", invite, "");

    // Request a Portal call: kind is empty to connect, or voice / video / screen.
    public static Task<bool> RequestCall(PortalContact c, string invite, string kind) => Post(c, "knock", invite, kind);

    static async Task<bool> Post(PortalContact c, string what, string invite, string kind)
    {
        try
        {
            var note = new JsonObject { ["t"] = what, ["i"] = invite, ["v"] = kind, ["f"] = Self, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }.ToJsonString();
            var body = Portal.Seal(Portal.UnB64(c.Key), note);
            var second = Nostr.Post(Topic(c), body);   // both relays: either one is enough
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var res = await Portal.relay.PostAsync(Portal.Relay + Topic(c), new System.Net.Http.StringContent(body), cts.Token);
                if (res.IsSuccessStatusCode) return true;
            }
            catch { }
            return await second;
        }
        catch { return false; }
    }

    // Listen in every contact's mailbox until stopped; a fresh call request reaches onRequest, and a cancelled request
    // reaches it as kind Cancelled. The relay's legacy "knock"/"unknock" event names are kept for older app versions.
    public static async Task Listen(IReadOnlyList<PortalContact> contacts, Action<PortalContact, string, string> onRequest, CancellationToken stop)
    {
        if (contacts.Count == 0) return;
        var byTopic = contacts.GroupBy(Topic).ToDictionary(g => g.Key, g => g.First());
        var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;   // only what arrives from now on: no old rings replayed at start
        var taken = new HashSet<string>();   // the same request comes by both relays: once is enough
        void Got(PortalContact c, string sealedNote)
        {
            lock (taken) if (!taken.Add(sealedNote)) return;
            try
            {
                var note = JsonNode.Parse(Portal.Unseal(Portal.UnB64(c.Key), sealedNote))!;
                if ((string?)note["f"] == Self) return;   // our own request, back from the shared mailbox
                if ((string?)note["t"] is not ("knock" or "unknock") || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ((long?)note["at"] ?? 0) > 45) return;
                onRequest(c, (string?)note["i"] ?? "", (string?)note["t"] == "unknock" ? Cancelled : (string?)note["v"] ?? "");
            }
            catch { }   // not sealed with this contact's key: not theirs
        }
        _ = Nostr.Listen(byTopic.Keys, since, (topic, body) => { if (byTopic.TryGetValue(topic, out var c)) Got(c, body); }, stop);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var res = await Portal.relay.GetAsync($"{Portal.Relay}{string.Join(',', byTopic.Keys)}/json?since={since}", System.Net.Http.HttpCompletionOption.ResponseHeadersRead, stop);
                using var lines = new StreamReader(await res.Content.ReadAsStreamAsync(stop));
                while (await lines.ReadLineAsync(stop) is { } line)
                {
                    if (JsonNode.Parse(line) is not { } m || (string?)m["event"] != "message") continue;
                    since = Math.Max(since, (long?)m["time"] ?? since);
                    if ((string?)m["topic"] is not { } topic || !byTopic.TryGetValue(topic, out var c) || (string?)m["message"] is not { } sealedNote) continue;
                    Got(c, sealedNote);
                }
            }
            catch when (!stop.IsCancellationRequested) { try { await Task.Delay(5000, stop); } catch { } }
            catch { return; }
        }
    }

    // the standalone app keeps its contacts in a file; Ivo keeps them in its settings (sealed with the app lock)
    public static List<PortalContact> Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<PortalContact>>(File.ReadAllText(file)) ?? [] : []; } catch { return []; }
    }

    public static void Save(string file, List<PortalContact> list)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}
