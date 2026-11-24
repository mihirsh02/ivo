using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SIPSorcery.Net;

namespace Ivo;

// Portal: two Ivos talking directly, with no server of Ivo's. WebRTC (SIPSorcery) finds a path between the two PCs and
// encrypts it end to end (DTLS). The only outside help is a public STUN server, which tells each PC its own address;
// nothing of the session passes through it.
//   The one who starts sends an invite, by any app. The other pastes it; their reply comes back on its own, through a
//   public message relays (ntfy.sh and a Nostr relay), into a mailbox whose random name and key are only in the invite: the relay sees a
//   few seconds of scrambled text, never who you are or what you say. Should the relay be unreachable, the reply is shown
//   as a code to send back by hand instead.
// Each code carries that side's connection details and the fingerprint of its encryption key, so only the two of you can join.
public sealed partial class Portal : IDisposable
{
    public const string Prefix = "ivo-portal:";
    readonly RTCPeerConnection pc = new(new RTCConfiguration
    {
        iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }, new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" }],
    });
    RTCDataChannel? channel;
    public string PeerName { get; private set; } = "";
    public bool Open => channel?.readyState == RTCDataChannelState.open;

    // these come from the network's threads: the window hops to its own before touching anything
    public event Action<JsonObject>? Message;
    public (double Screen, double Camera) TheirSplit { get; private set; }
    public string TheirKind { get; private set; } = "";
    public event Action? Opened, Closed;
    public event Action<string>? Failed;   // the reply that came back couldn't be used

    // the mailbox the reply comes back through: a random name and a key, both only in the invite
    internal const string Relay = "https://ntfy.sh/";
    internal static readonly System.Net.Http.HttpClient relay = new(Net.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
    string? box; byte[]? boxKey;
    readonly CancellationTokenSource listening = new();

    public Portal()
    {
        pc.onconnectionstatechange += s => { if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected) FireClosed(); };
        pc.ondatachannel += d => Hook(d);
        AddMedia();   // the audio and video lines, agreed now so calls need no new codes later
    }

    // the one who starts: an invite code to send
    // the internet, really there: a network, and any of several unrelated sites answering (the relays, Windows' own
    // connection test, Cloudflare). Slow or refusing relays aren't "no internet": only nothing answering at all is.
    public static async Task<bool> Online()
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        async Task<bool> Answers(string url)
        {
            try { using var r = await relay.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url), cts.Token); return true; }
            catch { return false; }
        }
        var all = new List<Task<bool>> { Answers("https://ntfy.sh/"), Answers("https://relay.primal.net/"), Answers("https://nos.lol/"), Answers("http://www.msftconnecttest.com/connecttest.txt"), Answers("https://1.1.1.1/") };
        while (all.Count > 0)
        {
            var done = await Task.WhenAny(all);
            if (await done) { cts.Cancel(); return true; }
            all.Remove(done);
        }
        return false;
    }
    public const string OfflineText = "You're offline. Check your internet connection, then try again.";

    public async Task<string> Invite(string me)
    {
        if (!await Online()) throw new InvalidOperationException(OfflineText);
        Hook(await pc.createDataChannel("ivo"));
        await pc.setLocalDescription(pc.createOffer());
        await Gathered();
        box = B64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
        boxKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        _ = Listen(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5);
        return Pack("offer", me, new JsonObject { ["m"] = box, ["k"] = B64(boxKey) });
    }

    // waits in the mailbox for their reply (up to 15 minutes) and connects with it
    async Task Listen(long since)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
        stop.CancelAfter(TimeSpan.FromMinutes(15));
        var taken = 0;   // the reply comes by both relays: the first one connects
        void Got(string sealedReply)
        {
            try { var r = Unseal(sealedReply); if (Interlocked.Exchange(ref taken, 1) == 1) return; Accept(r); listening.Cancel(); }
            catch (Exception ex) { Failed?.Invoke(ex.Message); }
        }
        _ = Nostr.Listen([$"ivo-portal-{box}"], since, (_, body) => Got(body), stop.Token);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var res = await relay.GetAsync($"{Relay}ivo-portal-{box}/json?since={since}", System.Net.Http.HttpCompletionOption.ResponseHeadersRead, stop.Token);
                using var lines = new StreamReader(await res.Content.ReadAsStreamAsync(stop.Token));
                while (await lines.ReadLineAsync(stop.Token) is { } line)
                {
                    if (JsonNode.Parse(line) is not { } m || (string?)m["event"] != "message" || (string?)m["message"] is not { } sealedReply) continue;
                    Got(sealedReply);
                    if (taken == 1) return;
                }
            }
            catch when (!stop.IsCancellationRequested) { try { await Task.Delay(2000, stop.Token); } catch { } }   // the stream dropped: listen again
            catch { return; }
        }
    }

    // the one invited: paste the invite; the reply goes back through the mailbox (Delivered), or is returned to send by hand
    public async Task<(string Reply, bool Delivered)> Join(string invite, string me)
    {
        var (type, sdp, name, o) = Unpack(invite);
        if (!await Online()) throw new InvalidOperationException(OfflineText);
        if (type != "offer") throw new InvalidDataException("That's a reply code. Paste the invite they sent you.");
        PeerName = name;
        if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp }) != SetDescriptionResultEnum.OK)
            throw new InvalidDataException("That invite couldn't be read. Ask for a new one.");
        await pc.setLocalDescription(pc.createAnswer());
        await Gathered();
        var reply = Pack("answer", me);
        if ((string?)o["m"] is not { } m || (string?)o["k"] is not { } k) return (reply, false);
        box = m; boxKey = UnB64(k);
        try
        {
            var body = Seal(reply);
            var second = Nostr.Post($"ivo-portal-{box}", body);   // both relays: either one carries it back
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var res = await relay.PostAsync($"{Relay}ivo-portal-{box}", new System.Net.Http.StringContent(body), cts.Token);
            return (reply, res.IsSuccessStatusCode || await second);
        }
        catch { return (reply, false); }
    }

    // the one who started: paste their reply, and the two connect
    public void Accept(string reply)
    {
        var (type, sdp, name, _) = Unpack(reply);
        if (type != "answer") throw new InvalidDataException("That's an invite. Paste the reply code they sent back.");
        PeerName = name;
        if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp }) != SetDescriptionResultEnum.OK)
            throw new InvalidDataException("That reply couldn't be read. Start again with a new invite.");
    }

    public void Send(JsonObject o) { if (Open) channel!.send(o.ToJsonString()); }

    int opened;
    void FireOpened()   // once, however it's noticed
    {
        if (Interlocked.Exchange(ref opened, 1) != 0) return;
        heard = DateTime.UtcNow;
        heartbeat = new Timer(_ => Beat(), null, 2000, 2000);
        Opened?.Invoke();
    }

    // a heartbeat: a tiny ping every 2 s; nothing at all from them for 6 s and they're gone (a crash, a lost network),
    // without waiting for WebRTC's own, much slower, timeout
    Timer? heartbeat;
    DateTime heard;
    int closed;
    void Beat()
    {
        if (DateTime.UtcNow - heard > TimeSpan.FromSeconds(6)) { FireClosed(); return; }
        try { if (Open) channel!.send("{\"t\":\"ping\"}"); } catch { }
    }

    void FireClosed() { if (Interlocked.Exchange(ref closed, 1) == 0) { heartbeat?.Dispose(); Closed?.Invoke(); } }

    void Hook(RTCDataChannel d)
    {
        channel = d;
        d.onopen += FireOpened;
        if (d.readyState == RTCDataChannelState.open) FireOpened();   // the joining side gets the channel already open: no onopen comes
        d.onclose += FireClosed;
        d.onmessage += (_, _, data) =>
        {
            FireOpened(); heard = DateTime.UtcNow;
            try
            {
                if (JsonNode.Parse(Encoding.UTF8.GetString(data)) is not JsonObject o || (string?)o["t"] == "ping") return;
                if ((string?)o["t"] == "kind") { TheirKind = (string?)o["v"] ?? ""; return; }   // what their picture is: camera or screen
                if ((string?)o["t"] == "split") { TheirSplit = ((double?)o["s"] ?? 0, (double?)o["c"] ?? 0); return; }   // their screen and camera, side by side in one picture
                if ((string?)o["t"] == "mic") { TheirMuted = (bool?)o["on"] == false; TheirMic?.Invoke(); return; }   // their mute, shown on their tile
                Message?.Invoke(o);
            }
            catch { }
        };
    }

    // all the addresses this PC can be reached at, found (or a few seconds, whichever is first) before the code is made
    async Task Gathered()
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
        var done = new TaskCompletionSource();
        pc.onicegatheringstatechange += s => { if (s == RTCIceGatheringState.complete) done.TrySetResult(); };
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    // a code: the connection details as compressed JSON, in letters and digits that survive any chat app
    string Pack(string type, string me, JsonObject? extra = null)
    {
        var o = new JsonObject { ["t"] = type, ["s"] = pc.localDescription.sdp.ToString(), ["n"] = me };
        foreach (var (k, v) in extra ?? []) o[k] = v?.DeepClone();
        var json = o.ToJsonString();
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize)) z.Write(Encoding.UTF8.GetBytes(json));
        var body = Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Prefix + body + "." + Check(body);   // a checksum: an edited or cut-off code is caught at once
    }

    static string Check(string body) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(body)))[..8].ToLowerInvariant();

    static (string Type, string Sdp, string Name, JsonNode All) Unpack(string code)
    {
        code = string.Concat(code.Trim().Where(c => !char.IsWhiteSpace(c)));
        if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("That isn't a Portal code. It starts with \"ivo-portal:\".");
        var parts = code[Prefix.Length..].Split('.');
        if (parts.Length != 2 || Check(parts[0]) != parts[1].ToLowerInvariant()) throw new InvalidDataException("That code was changed or cut off. Copy it again, whole, or ask them to send it again.");
        var b64 = parts[0].Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        using var z = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
        using var r = new StreamReader(z);
        var o = JsonNode.Parse(r.ReadToEnd())!;
        return ((string?)o["t"] ?? "", (string?)o["s"] ?? "", ((string?)o["n"] ?? "Someone").Trim() is { Length: > 0 } n ? n[..Math.Min(40, n.Length)] : "Someone", o);
    }

    // the reply in the mailbox: AES-GCM with the invite's key (nonce, ciphertext, tag)
    string Seal(string text) => Seal(boxKey!, text);
    string Unseal(string sealedText) => Unseal(boxKey!, sealedText);

    internal static string Seal(byte[] key, string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var data = new byte[plain.Length]; var tag = new byte[16];
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16)) aes.Encrypt(nonce, plain, data, tag);
        return B64([.. nonce, .. data, .. tag]);
    }

    internal static string Unseal(byte[] key, string sealedText)
    {
        var all = UnB64(sealedText.Trim());
        var plain = new byte[all.Length - 28];
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16)) aes.Decrypt(all[..12], all[12..^16], all[^16..], plain);
        return Encoding.UTF8.GetString(plain);
    }

    internal static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] UnB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }

    public void Dispose()
    {
        heartbeat?.Dispose(); Interlocked.Exchange(ref closed, 1);   // ended here: no "they left" for our own goodbye
        listening.Cancel();
        _ = EndCall();
        try { channel?.close(); } catch { }
        try { pc.close(); } catch { }
    }
}
