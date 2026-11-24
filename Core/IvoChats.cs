using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ivo;

// Ivo Chats: a messenger between contacts, in Ivo and Ivo Portal alike, with no server of Ivo's.
//   Every thread is a mailbox on the same public relays Portal uses (ntfy.sh, and Nostr: see Nostr.cs): a random name and a key. Messages are sealed
//   with that key (AES-GCM) before they leave this PC, so the relay holds only scrambled text, for up to 12 hours; files are
//   sealed the same way and uploaded as ntfy.sh's attachments (up to 15 MB, kept about 3 hours), or to a public Blossom server when ntfy.sh refuses.
//   One-to-one: a contact's thread uses a mailbox derived from that contact's own.
//   Groups: the one who makes it is the admin. Everyone has an identity (a P-256 key pair); the group's key is handed to each
//   member sealed for them alone, so when the admin removes someone, the group moves to a new mailbox and key that the
//   removed person can't open. Members can invite their own contacts; the admin approves, then the inviter hands them in.
public class ChatMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Pub { get; set; } = "";   // their public key (identity), base64url
    public string Pic { get; set; } = "";   // their picture: a small JPEG, base64
    public string Status { get; set; } = "";   // active · busy · away · invisible (shown as offline)
    public string App { get; set; } = "";      // what they use: "Ivo" or "Ivo Portal"
    public long Seen { get; set; }              // when they were last here (unix seconds), from their presence beat

    // what others see: their status while they're here (a beat in the last 75 s: they beat every 30 s), offline otherwise
    [System.Text.Json.Serialization.JsonIgnore]
    public string Live => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Seen > 75 || Status == "invisible" ? "offline" : Status;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Presence => Live switch
    {
        "active" => "Active", "busy" => "Busy", "away" => "Away",
        _ => Seen > 0 ? "Last seen " + Ago(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Seen) : "Offline",
    };
    static string Ago(long s) => s < 3600 ? $"{Math.Max(1, s / 60)} min ago" : s < 86400 ? $"{s / 3600} h ago"
        : DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s).LocalDateTime.ToString("d MMM");
}

public class ChatMsg
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string From { get; set; } = "";
    public string FromName { get; set; } = "";
    public long At { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public string Kind { get; set; } = "text";   // text · file · ivo (Ivo's answer) · listen (a listen-along invite) · sys (joined, left…)
    public string Text { get; set; } = "";
    public string? File { get; set; }            // a file: its name, size, where it is and its key
    public long Size { get; set; }
    public string? Url { get; set; }
    public string? Artist { get; set; }          // a listen-along invite: the song's artists (Text: its title, Url: its cover)
    public string? FileKey { get; set; }
    public string? Saved { get; set; }           // where it was saved on this PC
    public bool Pending { get; set; }            // ours, not delivered yet: the outbox sends it again until it goes
    public string? Cached { get; set; }
    public string? ReplyTo { get; set; }         // a reply: the message it answers (its id, who wrote it, a few words of it)
    public string? ReplyFrom { get; set; }
    public string? ReplyText { get; set; }
    public Dictionary<string, string> Reactions { get; set; } = [];   // who reacted (their id) → with which emoji
    public bool Delivered { get; set; }   // ours, one-to-one: it reached their PC
    public bool Read { get; set; }        // ours, one-to-one: they've seen it (only shown with read receipts on)
}

public class ChatThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Kind { get; set; } = "dm";     // dm · group
    public string Name { get; set; } = "";
    public string Box { get; set; } = "";
    public string Key { get; set; } = "";
    public string? ContactBox { get; set; }      // a one-to-one thread: the contact it's with
    public long SeenSent { get; set; }           // the newest of their messages we've told them we saw
    public bool Binned { get; set; }             // its contact is in Recently deleted: hidden (kept, so it comes back on Restore)
    public string Admin { get; set; } = "";      // a group: the admin's id
    public List<ChatMember> Members { get; set; } = [];
    public List<ChatMember> Asked { get; set; } = [];   // a group: invites waiting for the admin (with who asked, in Text)
    public Dictionary<string, string> AskedBy { get; set; } = [];
    public List<ChatMsg> Messages { get; set; } = [];
    public int Unread { get; set; }
    public bool Gone { get; set; }               // removed from the group: read-only
    public bool Deleted { get; set; }            // deleted on this PC; a later message restores it
}

public class ChatBook
{
    public string MyId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string MyName { get; set; } = "";
    public string Priv { get; set; } = "";       // this PC's identity (P-256 private key, PKCS#8, base64url)
    public string Pub { get; set; } = "";
    public long Since { get; set; }              // the relay's time of the newest message seen
    public string Pic { get; set; } = "";        // your picture (a small JPEG, base64) and status, shared with everyone you chat with
    public string Status { get; set; } = "active";
    public bool ProfileDue { get; set; }         // your picture or status changed and hasn't reached everyone yet
    public List<ChatThread> Threads { get; set; } = [];
    public List<string> Blocked { get; set; } = [];   // people (their ids) whose messages and calls are dropped; they aren't told
}

public sealed class IvoChats
{
    public ChatBook Book { get; private set; }
    readonly Action<string> save;
    CancellationTokenSource? listening;
    public event Action<ChatThread, ChatMsg?>? Changed;   // from the network's threads: hop to the window's before drawing
    // a group call's signals (ring, join, offer, leave), carried over the group's own sealed mailbox
    public event Action<ChatThread, JsonObject>? CallSignal;
    public Task<bool> SendCall(ChatThread t, JsonObject o) { o["t"] = "gcall"; return Post(t, o); }
    const long MaxFile = 15 * 1024 * 1024;

    static string DmTopic(string contactBox) => "ivo-chat-d-" + contactBox;
    static string GroupTopic(string box) => "ivo-chat-g-" + box;
    static string Topic(ChatThread t) => t.Kind == "dm" ? DmTopic(t.Box) : GroupTopic(t.Box);

    public IvoChats(string? json, Action<string> save)
    {
        this.save = save;
        try { Book = json is { Length: > 0 } ? JsonSerializer.Deserialize<ChatBook>(json) ?? new() : new(); } catch { Book = new(); }
        if (Book.Priv == "")
        {
            using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            Book.Priv = Portal.B64(ec.ExportPkcs8PrivateKey());
            Book.Pub = Portal.B64(ec.ExportSubjectPublicKeyInfo());
        }
        if (Book.Since == 0) Book.Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 12 * 3600;
    }

    public void Save() { lock (Book) save(JsonSerializer.Serialize(Book)); }
    public static string App = "Ivo";   // this app (Ivo Portal sets its own name): it travels with everything you send
    ChatMember Me => new() { Id = Book.MyId, Name = Book.MyName, Pub = Book.Pub, Status = Book.Status, App = App };   // (the picture travels on its own: it's the big part)

    // your picture and status: kept, and sent to every chat you're in
    public async Task SetProfile(string? pic, string? status)
    {
        if (pic != null) Book.Pic = pic;
        if (status != null) Book.Status = status;
        Book.ProfileDue = true;
        if (status != null) _ = Nostr.Post("seen:" + Book.MyId, new JsonObject { ["a"] = App, ["s"] = Book.Status == "invisible" ? "offline" : Book.Status, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Book.Status == "invisible" ? 200 : 0) }.ToJsonString(), Nostr.Seen);   // straight away
        Save(); Changed?.Invoke(Book.Threads.FirstOrDefault() ?? new ChatThread(), null);
        await Flush();
    }

    // someone in a thread: their latest picture and status, wherever they appear
    public ChatMember? Person(ChatThread t) => t.Kind == "dm" ? t.Members.FirstOrDefault() : null;

    // a one-to-one thread for every contact (made when first needed), then listen in all of them
    public void Sync(IEnumerable<PortalContact> contacts, string myName)
    {
        Book.MyName = myName;
        foreach (var c in contacts)
            if (!Book.Threads.Any(t => t.ContactBox == c.Box))
                Book.Threads.Add(new ChatThread { Kind = "dm", Name = c.Name, Box = c.Box, Key = c.Key, ContactBox = c.Box });
        foreach (var t in Book.Threads.Where(t => t.Kind == "dm")) t.Name = contacts.FirstOrDefault(c => c.Box == t.ContactBox)?.Name ?? t.Name;
        Save();
        Listen();
    }

    public void Listen()
    {
        listening?.Cancel();
        var topics = Book.Threads.Where(t => !t.Gone).Select(Topic).Distinct().ToList();
        if (topics.Count == 0) return;
        listening = new CancellationTokenSource();
        _ = Run(topics, listening.Token);
        _ = Nostr.Listen(topics, Book.Since - 60, Received, listening.Token);   // the second path: whichever copy comes first
        _ = Outbox(listening.Token);
        _ = Beat(listening.Token);
        // everyone you chat with: their latest beat (stored by the relay), then each new one live
        var people = Book.Threads.SelectMany(t => t.Members).Select(m => m.Id).Where(id => id != "" && id != Book.MyId).Distinct().ToDictionary(id => "seen:" + id);
        _ = Nostr.Listen(people.Keys, 0, (topic, body) =>
        {
            try
            {
                var o = JsonNode.Parse(body)!; var at = (long?)o["at"] ?? 0; var st = (string?)o["s"] ?? ""; var app = (string?)o["a"] ?? "";
                var changed = false;
                foreach (var th in Book.Threads)
                    foreach (var x in th.Members.Where(x => x.Id == people[topic] && at >= x.Seen)) { changed |= x.Status != st || x.Live != st; x.Seen = at; x.Status = st; if (app != "") { changed |= x.App != app; x.App = app; } }
                if (changed) Changed?.Invoke(Book.Threads.First(), null);
            }
            catch { }
        }, listening.Token, Nostr.Seen);
    }

    // the outbox: every 20 s (and at once on start) send what didn't go: messages, and your profile
    async Task Outbox(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Flush(); } catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch { return; }
        }
    }

    readonly SemaphoreSlim flushing = new(1, 1);
    async Task Flush()
    {
        if (!await flushing.WaitAsync(0)) return;   // one at a time
        try
        {
            foreach (var t in Book.Threads.Where(t => !t.Gone).ToList())
                foreach (var m in t.Messages.Where(m => m.Pending).ToList())
                    if (await PostMsg(t, m)) { m.Pending = false; Save(); Changed?.Invoke(t, null); }
            if (Book.ProfileDue)
            {
                var all = true;
                foreach (var t in Book.Threads.Where(t => !t.Gone).ToList())
                    all &= await Post(t, new JsonObject { ["t"] = "profile", ["pic"] = Book.Pic, ["status"] = Book.Status });
                if (all) { Book.ProfileDue = false; Save(); }
            }
        }
        finally { flushing.Release(); }
    }

    public void Stop() => listening?.Cancel();

    // your presence, every 30 s while the app runs: your status and the time (invisible sends nothing, so you look away).
    // Others are checked every 15 s, so someone whose app closed without a goodbye shows offline within about 1½ minutes.
    string shown = "";
    int beat;
    async Task Beat(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var now = string.Join(",", Book.Threads.SelectMany(t => t.Members).Select(m => m.Live));   // someone went quiet: show them offline
            if (now != shown) { shown = now; if (Book.Threads.Count > 0) Changed?.Invoke(Book.Threads[0], null); }
            if (Book.Status != "invisible" && beat++ % 2 == 0) await Nostr.Post("seen:" + Book.MyId, new JsonObject { ["a"] = App, ["s"] = Book.Status, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }.ToJsonString(), Nostr.Seen);
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch { return; }
        }
    }

    async Task Run(List<string> topics, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var res = await Portal.relay.GetAsync($"{Portal.Relay}{string.Join(',', topics)}/json?since={Book.Since}", HttpCompletionOption.ResponseHeadersRead, stop);
                using var lines = new StreamReader(await res.Content.ReadAsStreamAsync(stop));
                while (await lines.ReadLineAsync(stop) is { } line)
                {
                    if (JsonNode.Parse(line) is not { } m || (string?)m["event"] != "message") continue;
                    var topic = (string?)m["topic"] ?? "";
                    var body = (string?)m["message"];
                    if (m["attachment"]?["url"] is { } big && body != null && !body.StartsWith('A') && body.Length < 60)   // a long message the relay turned into a file
                        body = await Portal.relay.GetStringAsync((string)big!, stop);
                    if (body != null) Received(topic, body);
                    Book.Since = Math.Max(Book.Since, (long?)m["time"] ?? 0);
                }
            }
            catch when (!stop.IsCancellationRequested) { try { await Task.Delay(5000, stop); } catch { } }
            catch { return; }
        }
    }

    // ---------- arriving ----------
    readonly HashSet<string> seen = [];   // bodies already taken (each comes by both relays)
    void Received(string topic, string sealedBody)
    {
        lock (seen) { if (!seen.Add(topic + sealedBody.GetHashCode())) return; if (seen.Count > 5000) seen.Clear(); }
        var t = Book.Threads.FirstOrDefault(t => Topic(t) == topic);
        if (t == null) return;
        JsonNode o;
        try { o = JsonNode.Parse(Portal.Unseal(Portal.UnB64(t.Key), sealedBody))!; } catch { return; }   // not sealed with this thread's key
        var from = (string?)o["from"] ?? "";
        if (from != "" && Book.Blocked.Contains(from)) return;   // blocked: nothing of theirs gets through (messages, reactions, receipts, calls)
        var kind = (string?)o["t"] ?? "";
        // who they are: their id, name and public key, learnt from anything they send
        if (o["me"] is JsonObject who)
        {
            var m = who.Deserialize<ChatMember>()!;
            if (m.Id != Book.MyId)
            {
                if (kind == "profile") m.Pic = (string?)o["pic"] ?? "";
                // the same person in every thread: keep their picture (unless this brings a new one) and take their status
                foreach (var th in Book.Threads)
                    foreach (var x in th.Members.Where(x => x.Id == m.Id)) { x.Status = m.Status; x.Name = m.Name; if (m.App != "") x.App = m.App; if (kind == "profile") x.Pic = m.Pic; if (x.Pub == "") x.Pub = m.Pub; }
                if (t.Kind == "dm" && !t.Members.Any(x => x.Id == m.Id))
                {
                    m.Pic = kind == "profile" ? m.Pic : Book.Threads.SelectMany(x => x.Members).FirstOrDefault(x => x.Id == m.Id && x.Pic != "")?.Pic ?? "";
                    t.Members.Clear(); t.Members.Add(m);
                    _ = Post(t, new JsonObject { ["t"] = "profile", ["pic"] = Book.Pic, ["status"] = Book.Status });   // first meeting: show them ours too
                }
                if (kind == "profile") { Save(); Changed?.Invoke(t, null); }
            }
        }
        switch (kind)
        {
            case "msg":
                var msg = o["m"]!.Deserialize<ChatMsg>()!;
                if (t.Messages.Any(x => x.Id == msg.Id)) return;   // seen (it was ours, or the relay sent it twice)
                lock (Book)   // in the order they were written, wherever a late one lands (the same on both sides)
                {
                    if (msg.From != Book.MyId) t.Deleted = false;
                    var at = t.Messages.FindLastIndex(x => x.At <= msg.At);
                    t.Messages.Insert(at + 1, msg);
                }
                if (msg.From != Book.MyId) t.Unread++;
                Save(); Changed?.Invoke(t, msg.From != Book.MyId ? msg : null);
                if (msg.From != Book.MyId && t.Kind == "dm") _ = Post(t, new JsonObject { ["t"] = "got", ["id"] = msg.Id });   // delivered
                break;
            case "song":   // the host's listen-along card: now playing something else
                if (t.Messages.FirstOrDefault(x => x.Id == (string?)o["id"] && x.From == from && x.Kind == "listen") is { } card)
                { card.Text = (string?)o["title"] ?? card.Text; card.Artist = (string?)o["artist"]; card.Url = (string?)o["cover"]; Save(); Changed?.Invoke(t, null); }
                break;
            case "react":   // someone reacted to a message, or took their reaction back ("" ); only the known set is kept
                if (from != "" && t.Messages.FirstOrDefault(x => x.Id == (string?)o["id"]) is { } liked && (string?)o["e"] is { } e && (e == "" || IsReaction(e)))
                {
                    lock (Book) { if (e == "") liked.Reactions.Remove(from); else liked.Reactions[from] = e; }
                    Save(); Changed?.Invoke(t, null);
                }
                break;
            case "unfriend" when t.Kind == "dm" && from != Book.MyId:   // they deleted you for good: the chat and the contact go here too
                var theirBox = t.ContactBox;
                lock (Book) Book.Threads.Remove(t);
                Save(); Listen(); Changed?.Invoke(t, null);
                if (theirBox != null) Unfriended?.Invoke(theirBox);
                return;
            case "merge" when t.Kind == "dm" && from != Book.MyId:   // our call is moving into a group call
                Merged?.Invoke(t, (string?)o["g"] ?? "", (string?)o["call"] ?? "", (string?)o["v"] ?? "voice");
                break;
            case "busy" when t.Kind == "dm" && from != Book.MyId:   // we called; they're on another call
                TheyreBusy?.Invoke(t);
                break;
            case "got" when t.Kind == "dm" && from != Book.MyId:   // our message reached them
                if (t.Messages.FirstOrDefault(x => x.Id == (string?)o["id"] && x.From == Book.MyId) is { Delivered: false } got) { got.Delivered = true; Save(); Changed?.Invoke(t, null); }
                break;
            case "seen" when t.Kind == "dm" && from != Book.MyId:   // they've read up to then; with ours off, we don't see theirs either
                if (!PortalDevices.ReadReceipts) break;
                var upTo = (long?)o["at"] ?? 0; var any = false;
                foreach (var x in t.Messages.Where(x => x.From == Book.MyId && x.At <= upTo && !x.Read)) { x.Read = x.Delivered = true; any = true; }
                if (any) { Save(); Changed?.Invoke(t, null); }
                break;
            case "delete":   // its writer unsent it
                if (t.Messages.FirstOrDefault(x => x.Id == (string?)o["id"] && x.From == from) is { } gone) { lock (Book) t.Messages.Remove(gone); Save(); Changed?.Invoke(t, null); }
                break;
            case "gcall" when t.Kind == "group" && from != Book.MyId:
                CallSignal?.Invoke(t, (JsonObject)o);
                break;
            case "group" when t.Kind == "dm":   // a group's key, handed to us over a one-to-one thread
                if (o["g"] is JsonObject g) JoinGroup(g);
                break;
            case "roster" when t.Kind == "group" && from == t.Admin:
                t.Members = o["members"]!.Deserialize<List<ChatMember>>()!;
                t.Asked.RemoveAll(a => t.Members.Any(m => m.Name == a.Name));
                if (!t.Members.Any(m => m.Id == Book.MyId)) { t.Gone = true; Sys(t, "You were removed from this group"); }
                Save(); Changed?.Invoke(t, null);
                break;
            case "ask" when t.Kind == "group":   // a member asks the admin to let one of their contacts in
                var invitee = o["who"]!.Deserialize<ChatMember>()!;
                if (!t.Asked.Any(a => a.Name == invitee.Name)) { t.Asked.Add(invitee); t.AskedBy[invitee.Name] = from; }
                Save(); Changed?.Invoke(t, null);
                break;
            case "approved" when t.Kind == "group" && from == t.Admin:   // the admin said yes: if it was our invite, we hand them the group
                var name = (string?)o["name"] ?? "";
                if ((string?)o["by"] == Book.MyId && Book.Threads.FirstOrDefault(d => d.Kind == "dm" && d.Name == name) is { } dm) _ = HandOver(t, dm);
                t.Asked.RemoveAll(a => a.Name == name);
                Save(); Changed?.Invoke(t, null);
                break;
            case "rekey" when t.Kind == "group" && from == t.Admin:   // someone was removed: the group moves on, sealed for those who stay
                if (o["keys"]?[Book.MyId] is { } mine && t.Members.FirstOrDefault(m => m.Id == t.Admin) is { } admin)
                {
                    try
                    {
                        var key = Open(mine.ToString(), admin.Pub);
                        t.Box = (string)o["box"]!; t.Key = key;
                        Save(); Listen(); Changed?.Invoke(t, null);
                    }
                    catch { }
                }
                break;
        }
    }

    void JoinGroup(JsonObject g)
    {
        var id = (string?)g["id"] ?? "";
        var t = Book.Threads.FirstOrDefault(x => x.Id == id);
        if (t == null) { t = new ChatThread { Id = id, Kind = "group" }; Book.Threads.Add(t); Sys(t, "You were added to this group"); }
        t.Name = (string?)g["name"] ?? "Group"; t.Box = (string?)g["box"] ?? ""; t.Key = (string?)g["key"] ?? "";
        t.Admin = (string?)g["admin"] ?? ""; t.Members = g["members"]!.Deserialize<List<ChatMember>>()!; t.Gone = false;
        if (!t.Members.Any(m => m.Id == Book.MyId)) t.Members.Add(Me);
        Save(); Listen(); Changed?.Invoke(t, null);
        _ = Post(t, new JsonObject { ["t"] = "hello" });   // so the admin learns our identity (the roster follows)
    }

    void Sys(ChatThread t, string text) { lock (Book) t.Messages.Add(new ChatMsg { Kind = "sys", Text = text, From = "" }); }

    // ---------- sending ----------
    async Task<bool> Post(ChatThread t, JsonObject o)
    {
        o["from"] = Book.MyId; o["me"] = JsonSerializer.SerializeToNode(Me);
        var body = Portal.Seal(Portal.UnB64(t.Key), o.ToJsonString());
        var second = Nostr.Post(Topic(t), body);   // both relays at once: either one delivering is enough
        for (int tries = 0; tries < 4; tries++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var res = await Portal.relay.PostAsync(Portal.Relay + Topic(t), new StringContent(body), cts.Token);
                if (res.IsSuccessStatusCode) return true;
                if ((int)res.StatusCode != 429) break;
            }
            catch { }
            if (await second) return true;   // the other relay has it: no need to wait on this one
            await Task.Delay(TimeSpan.FromSeconds(5 * (tries + 1)));   // too many at once (the relay's limit), or a moment offline: wait and send again
        }
        return await second;
    }

    public async Task<bool> Send(ChatThread t, ChatMsg m)
    {
        t.Deleted = false;
        m.From = Book.MyId; m.FromName = Book.MyName; m.Pending = true;
        lock (Book) t.Messages.Add(m);
        Save(); Changed?.Invoke(t, null);
        var ok = await PostMsg(t, m);
        if (ok) { m.Pending = false; Save(); Changed?.Invoke(t, null); }
        return ok;   // not delivered: it stays in the outbox and goes when it can
    }

    // a listen-along card follows the host's music: the new song, on both sides
    public Task<bool> UpdateListen(ChatThread t, ChatMsg card, string title, string artist, string? cover)
    {
        card.Text = title; card.Artist = artist; card.Url = cover;
        Save(); Changed?.Invoke(t, null);
        return Post(t, new JsonObject { ["t"] = "song", ["id"] = card.Id, ["title"] = title, ["artist"] = artist, ["cover"] = cover });
    }

    Task<bool> PostMsg(ChatThread t, ChatMsg m)
    {
        var sent = JsonSerializer.SerializeToNode(m)!.AsObject();
        sent.Remove("Saved"); sent.Remove("Pending"); sent.Remove("Cached");   // where the file is on this PC, and our own outbox, stay here
        return Post(t, new JsonObject { ["t"] = "msg", ["m"] = sent });
    }

    public Task<bool> Text(ChatThread t, string text, bool fromIvo = false, ChatMsg? replyTo = null) => Send(t, Replying(new ChatMsg { Kind = fromIvo ? "ivo" : "text", Text = text.Length > 2500 ? text[..2500] + "…" : text }, replyTo));

    // a reply carries a few words of what it answers, so the quote shows even where the original was deleted
    static ChatMsg Replying(ChatMsg m, ChatMsg? to)
    {
        if (to == null) return m;
        m.ReplyTo = to.Id; m.ReplyFrom = to.FromName;
        m.ReplyText = Gist(to);
        return m;
    }

    public static string Gist(ChatMsg m) => m.Kind switch { "file" => (IsPicture(m) ? "Photo · " : "File · ") + m.File, _ => m.Text.Length > 90 ? m.Text[..90] + "…" : m.Text };

    // delete: just here, or (your own) for everyone in the thread
    public void DeleteForMe(ChatThread t, ChatMsg m)
    {
        lock (Book) t.Messages.Remove(m);
        Save(); Changed?.Invoke(t, null);
    }

    // delete a chat here only: a one-to-one conversation is hidden and cleared, but stays connected to its contact;
    // a later message restores it. A group is left on this PC and stops being listened to.
    public void DeleteChat(ChatThread t)
    {
        lock (Book)
        {
            if (t.Kind == "group") Book.Threads.Remove(t);
            else { t.Messages.Clear(); t.Unread = 0; t.Deleted = true; }
        }
        Save(); Changed?.Invoke(t, null);
        if (t.Kind == "group") Listen();
    }

    // a contact moved to Recently deleted (or restored): their chat hides (or comes back) with them
    public void Bin(string box, bool binned)
    {
        if (Book.Threads.FirstOrDefault(t => t.ContactBox == box) is not { } t) return;
        t.Binned = binned; Save(); Changed?.Invoke(t, null);
    }

    // 15 days in Recently deleted: the chat ends for both of you; they're told, so their app removes you and it too
    public event Action<string>? Unfriended;   // their contact's mailbox, so the app can drop the contact
    public async Task Unfriend(string box)
    {
        if (Book.Threads.FirstOrDefault(t => t.ContactBox == box) is not { } t) return;
        try { await Post(t, new JsonObject { ["t"] = "unfriend" }); } catch { }
        lock (Book) Book.Threads.Remove(t);
        Save(); Listen(); Changed?.Invoke(t, null);
    }

    // blocking someone (a one-to-one chat's person): their messages and calls stop reaching you, and they aren't told
    public static string? PersonOf(ChatThread t) => t.Kind == "dm" ? t.Members.FirstOrDefault()?.Id is { Length: > 0 } id ? id : null : null;
    public bool IsBlocked(ChatThread t) => PersonOf(t) is { } id && Book.Blocked.Contains(id);
    public bool IsBlockedContact(string box) => Book.Threads.FirstOrDefault(t => t.ContactBox == box) is { } t && IsBlocked(t);
    public void Block(ChatThread t, bool block)
    {
        if (PersonOf(t) is not { } id) return;
        lock (Book) { Book.Blocked.Remove(id); if (block) Book.Blocked.Add(id); }
        Sys(t, block ? "You blocked them" : "You unblocked them");
        Save(); Changed?.Invoke(t, null);
    }

    // the reactions anyone can leave on a message (one each; the same again takes it back)
    public static string[] ReactionSet => PortalDevices.Reactions;   // your six (Settings); any single emoji is accepted from others
    public static bool IsReaction(string e) => e.Length is > 0 and <= 16 && e.EnumerateRunes().All(r => r.Value >= 0x1F000 || r.Value is 0x200D or 0xFE0F or 0x20E3 || r.Value is >= 0x2000 and < 0x3300 || r.Value is >= 0x1F3FB and <= 0x1F3FF);
    public async Task React(ChatThread t, ChatMsg m, string emoji)
    {
        lock (Book) { if (m.Reactions.GetValueOrDefault(Book.MyId) == emoji) { m.Reactions.Remove(Book.MyId); emoji = ""; } else m.Reactions[Book.MyId] = emoji; }
        Save(); Changed?.Invoke(t, null);
        await Post(t, new JsonObject { ["t"] = "react", ["id"] = m.Id, ["e"] = emoji });
    }

    // merging calls: the one-to-one call (or a caller waiting) moves into a group call. The group arrives over the same
    // chat (HandOver), maybe after this message, so WaitForGroup gives it a little while.
    public event Action<ChatThread, string, string, string>? Merged;   // the chat it came on, the group's id, the call's id, voice or video
    public Task Merge(ChatThread dm, ChatThread g, string call, string kind) => Post(dm, new JsonObject { ["t"] = "merge", ["g"] = g.Id, ["call"] = call, ["v"] = kind });
    public async Task<ChatThread?> WaitForGroup(string id, int ms = 20000)
    {
        for (var waited = 0; waited < ms; waited += 300)
        {
            if (Book.Threads.FirstOrDefault(x => x.Id == id && x.Kind == "group" && !x.Gone) is { } g) return g;
            await Task.Delay(300);
        }
        return null;
    }

    // calling someone who's already on a call: their Portal answers "busy" in your chat with them
    public event Action<ChatThread>? TheyreBusy;
    public Task Busy(ChatThread t) => Post(t, new JsonObject { ["t"] = "busy" });

    // unsend (yours): gone from the thread for everyone, without a trace
    public async Task Unsend(ChatThread t, ChatMsg m)
    {
        lock (Book) t.Messages.Remove(m);
        Save(); Changed?.Invoke(t, null);
        await Post(t, new JsonObject { ["t"] = "delete", ["id"] = m.Id });
    }


    // a file: sealed with its own key, uploaded as ntfy.sh's attachment (or to Blossom when ntfy.sh refuses), then announced in the thread
    public async Task<string?> SendFile(ChatThread t, string path, ChatMsg? replyTo = null)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFile) return "Files can be up to 15 MB.";
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = await File.ReadAllBytesAsync(path);
        var nonce = RandomNumberGenerator.GetBytes(12); var data = new byte[plain.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, data, tag);
        try
        {
            byte[] sealedBytes = [.. nonce, .. data, .. tag];
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var url = await Ntfy(sealedBytes, cts.Token) ?? await Nostr.Upload(sealedBytes, cts.Token);
            if (url == null) return "Neither relay took the file. Try again in a while.";
            await Send(t, Replying(new ChatMsg { Kind = "file", File = info.Name, Size = info.Length, Url = url, FileKey = Portal.B64(key), Saved = path }, replyTo));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ntfy.sh first; null when it refuses (its daily limit, or down), and the file goes to Blossom instead
    static async Task<string?> Ntfy(byte[] sealedBytes, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{Portal.Relay}ivo-chat-f-{Portal.B64(RandomNumberGenerator.GetBytes(12))}") { Content = new ByteArrayContent(sealedBytes) };
            req.Headers.Add("Filename", "ivo.bin");
            using var res = await Portal.relay.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return (string?)JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["attachment"]?["url"];
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    // a file someone sent: fetched, opened with its key, saved to Downloads\Ivo Chats
    public static bool IsPicture(ChatMsg m) => m.Kind == "file" && Path.GetExtension(m.File ?? "").ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ivo", "chat-pictures");

    // the file's bytes: from where it already is on this PC (ours, a saved copy, the cache), else fetched and opened with its key
    async Task<byte[]> Bytes(ChatMsg m)
    {
        foreach (var have in new[] { m.Saved, m.Cached }) if (have != null && File.Exists(have)) return await File.ReadAllBytesAsync(have);
        var all = await Portal.relay.GetByteArrayAsync(m.Url!);
        var plain = new byte[all.Length - 28];
        using (var aes = new AesGcm(Portal.UnB64(m.FileKey!), 16)) aes.Decrypt(all[..12], all[12..^16], all[^16..], plain);
        return plain;
    }

    // a picture, for showing in the chat: kept in Ivo's cache (fetched once, while the relay still has it)
    public async Task<string?> Picture(ChatMsg m)
    {
        if (m.Saved is { } mine && File.Exists(mine)) return mine;
        if (m.Cached is { } c && File.Exists(c)) return c;
        try
        {
            var bytes = await Bytes(m);
            Directory.CreateDirectory(CacheDir);
            var to = Path.Combine(CacheDir, m.Id + Path.GetExtension(m.File ?? ".png"));
            await File.WriteAllBytesAsync(to, bytes);
            m.Cached = to; Save();
            return to;
        }
        catch { return null; }
    }

    public async Task<string> Download(ChatMsg m)
    {
        if (m.Saved is { } done && File.Exists(done)) return done;
        var plain = await Bytes(m);
        var dir = PortalDevices.Downloads;   // where you chose (Downloads\Ivo Chats unless changed)
        Directory.CreateDirectory(dir);
        var name = string.Concat((m.File ?? "file").Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var to = Path.Combine(dir, name);
        for (int i = 2; File.Exists(to); i++) to = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        await File.WriteAllBytesAsync(to, plain);
        m.Saved = to; Save();
        return to;
    }

    // ---------- groups ----------
    public async Task<ChatThread> NewGroup(string name, List<ChatThread> with)
    {
        var g = new ChatThread
        {
            Kind = "group", Name = name, Admin = Book.MyId,
            Box = Portal.B64(RandomNumberGenerator.GetBytes(18)), Key = Portal.B64(RandomNumberGenerator.GetBytes(32)),
            Members = [Me, .. with.Select(d => d.Members.FirstOrDefault() ?? new ChatMember { Name = d.Name, Id = "" })],
        };
        Book.Threads.Add(g); Sys(g, $"You made this group");
        Save(); Listen(); Changed?.Invoke(g, null);
        foreach (var d in with) await HandOver(g, d);
        return g;
    }

    // hand a group to someone over our one-to-one thread with them
    async Task HandOver(ChatThread g, ChatThread dm)
    {
        var spec = new JsonObject { ["id"] = g.Id, ["name"] = g.Name, ["box"] = g.Box, ["key"] = g.Key, ["admin"] = g.Admin, ["members"] = JsonSerializer.SerializeToNode(g.Members) };
        await Post(dm, new JsonObject { ["t"] = "group", ["g"] = spec });
    }

    // the admin adds a contact; a member asks the admin to
    public async Task Invite(ChatThread g, ChatThread dm)
    {
        var who = dm.Members.FirstOrDefault() ?? new ChatMember { Name = dm.Name };
        if (g.Admin == Book.MyId)
        {
            g.Members.RemoveAll(m => m.Name == who.Name && m.Id == ""); g.Members.Add(who);
            Sys(g, $"You added {who.Name}");
            await HandOver(g, dm); await Roster(g);
        }
        else
        {
            await Post(g, new JsonObject { ["t"] = "ask", ["who"] = JsonSerializer.SerializeToNode(who) });
            Sys(g, $"You asked to add {who.Name}; the admin decides");
        }
        Save(); Changed?.Invoke(g, null);
    }

    // the admin says yes to an invite: the inviter hands them the group
    public async Task Approve(ChatThread g, ChatMember who)
    {
        g.Asked.RemoveAll(a => a.Name == who.Name);
        g.Members.Add(who);
        var by = g.AskedBy.GetValueOrDefault(who.Name, "");
        await Post(g, new JsonObject { ["t"] = "approved", ["name"] = who.Name, ["by"] = by });
        await Roster(g);
        Sys(g, $"You let {who.Name} in");
        Save(); Changed?.Invoke(g, null);
    }

    public async Task Decline(ChatThread g, ChatMember who)
    {
        g.Asked.RemoveAll(a => a.Name == who.Name);
        Save(); Changed?.Invoke(g, null);
        await Task.CompletedTask;
    }

    // the admin removes someone: a new mailbox and key, sealed for each one who stays, posted in the old mailbox
    public async Task Remove(ChatThread g, ChatMember who)
    {
        g.Members.RemoveAll(m => m.Id == who.Id && m.Name == who.Name);
        await Roster(g);
        var box = Portal.B64(RandomNumberGenerator.GetBytes(18)); var key = Portal.B64(RandomNumberGenerator.GetBytes(32));
        var keys = new JsonObject();
        foreach (var m in g.Members.Where(m => m.Id != Book.MyId && m.Pub != "")) keys[m.Id] = SealFor(key, m.Pub);
        await Post(g, new JsonObject { ["t"] = "rekey", ["box"] = box, ["keys"] = keys });
        g.Box = box; g.Key = key;
        Sys(g, $"You removed {who.Name}");
        Save(); Listen(); Changed?.Invoke(g, null);
    }

    public async Task Leave(ChatThread g)
    {
        await Text(g, $"{Book.MyName} left the group");
        g.Gone = true;
        Save(); Listen(); Changed?.Invoke(g, null);
    }

    async Task Roster(ChatThread g) => await Post(g, new JsonObject { ["t"] = "roster", ["members"] = JsonSerializer.SerializeToNode(g.Members) });

    // a new group key, for one member alone: ECDH between our identity and theirs, then AES-GCM
    string SealFor(string text, string theirPub) => Portal.Seal(Shared(theirPub), text);
    string Open(string sealedText, string theirPub) => Portal.Unseal(Shared(theirPub), sealedText);

    byte[] Shared(string theirPub)
    {
        using var me = ECDiffieHellman.Create();
        me.ImportPkcs8PrivateKey(Portal.UnB64(Book.Priv), out _);
        using var them = ECDiffieHellman.Create();
        them.ImportSubjectPublicKeyInfo(Portal.UnB64(theirPub), out _);
        return me.DeriveKeyFromHash(them.PublicKey, HashAlgorithmName.SHA256);
    }

    public void Seen(ChatThread t)
    {
        // with read receipts on, a one-to-one chat tells them how far you've read
        if (PortalDevices.ReadReceipts && t.Kind == "dm" && t.Messages.LastOrDefault(x => x.From != Book.MyId && x.Kind != "sys") is { } newest && newest.At > t.SeenSent)
        { t.SeenSent = newest.At; _ = Post(t, new JsonObject { ["t"] = "seen", ["at"] = newest.At }); Save(); }
        if (t.Unread == 0) return;
        t.Unread = 0; Save();
    }
    public void Read(ChatThread t, bool read) { t.Unread = read ? 0 : Math.Max(1, t.Unread); Save(); Changed?.Invoke(t, null); }
    public int Unread => Book.Threads.Sum(t => t.Unread);
    public int UnreadChats => Book.Threads.Count(t => t.Unread > 0);   // chats with something new (the count on a tab or button)
}
