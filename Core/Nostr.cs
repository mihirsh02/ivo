using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace Ivo;

// The second path for Portal and Chats: Nostr relays (relay.primal.net, nos.lol, relay.damus.io), beside ntfy.sh. Each
// message goes to all three and is heard from all three (once), so one relay being down changes nothing. Everything goes both ways and
// the first copy to arrive wins (the second is ignored), so either relay alone still delivers, and ntfy.sh's daily cap
// stops mattering. Bodies are already sealed; the relay sees only a hash of the mailbox's name, never the name itself.
// A Nostr event must be signed: each run signs with its own throwaway key, so nothing ties your messages together.
public static class Nostr
{
    public static readonly string[] Relays = ["wss://relay.primal.net", "wss://nos.lol", "wss://relay.damus.io"];
    public const int Kind = 4333;    // messages: a regular (stored) kind of our own, so nothing shows in anyone's Nostr feed
    public const int Seen = 30078;   // presence: Nostr's app-data kind, replaceable, so the relay keeps only each person's latest "here, and how"
    static readonly ECPrivKey key = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
    static readonly string pub = Hex(key.CreateXOnlyPubKey().ToBytes());

    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
    public static string Tag(string topic) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes("ivo:" + topic)));

    static JsonObject Event(string topic, string body, int kind)
    {
        var tags = new JsonArray(new JsonArray("t", Tag(topic)));
        if (kind == Seen) tags.Add(new JsonArray("d", Tag(topic)));   // one per person: each new one replaces the last
        return Signed(tags, body, kind);
    }

    static JsonObject Signed(JsonArray tags, string body, int kind)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = SHA256.HashData(Encoding.UTF8.GetBytes(new JsonArray(0, pub, at, kind, tags.DeepClone(), body).ToJsonString(Plain)));
        key.TrySignBIP340(id, null, out var sig);
        var s = new byte[64]; sig!.WriteToSpan(s);
        return new JsonObject { ["id"] = Hex(id), ["pubkey"] = pub, ["created_at"] = at, ["kind"] = kind, ["tags"] = tags, ["content"] = body, ["sig"] = Hex(s) };
    }

    // Files, when ntfy.sh won't take one: public Blossom servers (Nostr's file storage), tried in turn. The upload is signed
    // with this run's key (kind 24242, naming the bytes' hash); the bytes are already sealed, so a server holds only noise.
    static readonly string[] Blossom = ["https://nostr.download", "https://blossom.oxtr.dev"];
    public static async Task<string?> Upload(byte[] data, CancellationToken ct)
    {
        var hash = Hex(SHA256.HashData(data));
        var auth = Signed(new JsonArray(new JsonArray("t", "upload"), new JsonArray("x", hash),
            new JsonArray("expiration", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString())), "Upload", 24242);
        var header = "Nostr " + Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.ToJsonString(Plain)));
        foreach (var server in Blossom)
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, server + "/upload") { Content = new ByteArrayContent(data) };
                req.Content.Headers.ContentType = new("application/octet-stream");
                req.Headers.TryAddWithoutValidation("Authorization", header);
                using var res = await Portal.relay.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) continue;
                if ((string?)JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["url"] is { } url) return url;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        return null;
    }

    static readonly HttpMessageInvoker sockets = new(Net.Handler());   // connects as browsers do: IPv6 and IPv4 raced
    static async Task<ClientWebSocket> Open(string relay, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        // a ping every 20 s, and no answer in 15 s means the line is dead (sleep, a network change, a relay that went quiet
        // without closing): the listen loop connects again instead of waiting forever on a silent socket
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        using var c = CancellationTokenSource.CreateLinkedTokenSource(ct); c.CancelAfter(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri(relay), sockets, c.Token);
        return ws;
    }

    // the event id is a hash of the event written as Nostr writes JSON: quotes as \", other text as itself (.NET would write ")
    static readonly System.Text.Json.JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static Task Say(ClientWebSocket ws, JsonNode n, CancellationToken ct) => ws.SendAsync(Encoding.UTF8.GetBytes(n.ToJsonString(Plain)), WebSocketMessageType.Text, true, ct);

    static async Task<string?> Hear(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16384]; using var all = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            all.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(all.GetBuffer(), 0, (int)all.Length);
        }
    }

    // leave a sealed body in a mailbox, on every relay at once; true once any of them has stored it
    public static async Task<bool> Post(string topic, string body, int kind = Kind)
    {
        var ev = Event(topic, body, kind);
        async Task<bool> To(string relay)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var ws = await Open(relay, cts.Token);
                await Say(ws, new JsonArray("EVENT", ev.DeepClone()), cts.Token);
                while (await Hear(ws, cts.Token) is { } m)
                    if (JsonNode.Parse(m) is JsonArray { Count: >= 3 } a && (string?)a[0] == "OK" && (string?)a[1] == (string?)ev["id"])
                        return a[2]!.GetValue<bool>();
            }
            catch { }
            return false;
        }
        var all = Relays.Select(To).ToList();
        while (all.Count > 0)
        {
            var done = await Task.WhenAny(all);
            if (await done) return true;
            all.Remove(done);
        }
        return false;
    }

    // listen in these mailboxes until stopped: what's stored since `since` (unix seconds), then what arrives
    // (every relay at once; a message heard from more than one comes through once)
    public static Task Listen(IReadOnlyCollection<string> topics, long since, Action<string, string> got, CancellationToken stop, int kind = Kind)
    {
        if (topics.Count == 0) return Task.CompletedTask;
        var seen = new HashSet<string>();
        var order = new Queue<string>();
        void Once(string id, string topic, string body)
        {
            lock (seen)
            {
                if (!seen.Add(id)) return;
                order.Enqueue(id); while (order.Count > 2000) seen.Remove(order.Dequeue());
            }
            got(topic, body);
        }
        return Task.WhenAll(Relays.Select(r => ListenOn(r, topics, since, Once, stop, kind)));
    }

    static async Task ListenOn(string relay, IReadOnlyCollection<string> topics, long since, Action<string, string, string> got, CancellationToken stop, int kind)
    {
        var byTag = topics.Distinct().ToDictionary(Tag);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var ws = await Open(relay, stop);
                await Say(ws, new JsonArray("REQ", "ivo", new JsonObject { ["kinds"] = new JsonArray(kind), ["#t"] = new JsonArray(byTag.Keys.Select(k => (JsonNode)k).ToArray()), ["since"] = since }), stop);
                while (await Hear(ws, stop) is { } m)
                {
                    if (JsonNode.Parse(m) is not JsonArray { Count: >= 3 } a || (string?)a[0] != "EVENT" || a[2] is not JsonObject ev) continue;
                    since = Math.Max(since, ((long?)ev["created_at"] ?? 0) - 60);
                    var tag = (ev["tags"] as JsonArray)?.OfType<JsonArray>().FirstOrDefault(t => (string?)t[0] == "t")?[1]?.ToString();
                    if (tag != null && byTag.TryGetValue(tag, out var topic) && (string?)ev["content"] is { } body) got((string?)ev["id"] ?? body, topic, body);
                }
            }
            catch when (!stop.IsCancellationRequested) { }
            catch { return; }
            try { await Task.Delay(5000, stop); } catch { return; }   // dropped: connect again
        }
    }
}
