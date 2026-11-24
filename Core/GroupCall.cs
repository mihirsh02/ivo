using System.Text.Json.Nodes;

namespace Ivo;

// A group call: everyone in it connected to everyone else, each pair by its own Portal (direct and encrypted, as a
// one-to-one call), with no server in the middle. The group's own sealed mailbox carries the signals:
//   ring   someone started a call: everyone's Ivo rings
//   join   you're in: everyone already in sends you an invite ("offer"), you answer each one, and you're connected
//   offer  an invite, for one person
//   leave  you've gone: everyone closes their line to you
// Your camera and screen are captured once (by a Portal that's never connected, Preview on) and sent on to everyone;
// your mic and speakers run on each line (Windows mixes them). Good for a handful of people: every line carries your
// video again, so past about five it's too much for a home connection.
public sealed class GroupCall : IDisposable
{
    public readonly ChatThread Thread;
    public readonly string Id, Kind;
    readonly IvoChats chats;
    readonly string me;
    public readonly Portal Local = new() { Preview = true };   // your camera and screen, captured once
    public readonly Dictionary<string, Portal> Peers = [];      // by member id: a line to each person in the call
    public readonly HashSet<string> In = [];                    // who's in (joined), connected yet or not
    public bool Muted { get; private set; }
    public DateTime Started { get; } = DateTime.Now;
    public event Action? Changed;                               // from the network's threads: hop to the window's
    public event Action<string, Portal>? PeerAdded;
    public event Action<string>? PeerGone;
    public event Action<string>? Stopped;   // your screen sharing stopped by itself (the window went away): why

    public GroupCall(IvoChats chats, ChatThread thread, string id, string kind, string me)
    {
        this.chats = chats; Thread = thread; Id = id; Kind = kind; this.me = me;
        chats.CallSignal += Signal;
        Local.SharingStopped += why => { if (Local.Sending == "") foreach (var p in Lines()) if (p.Open) p.ForwardOff(); Stopped?.Invoke(why); Changed?.Invoke(); };
        Local.MyFrame += (bgr, w, h) => { foreach (var p in Lines()) if (p.Open) p.Forward(bgr, w, h, Local.Sending, Local.MySplit); };
    }

    Portal[] Lines() { lock (Peers) return [.. Peers.Values]; }
    public string MyId => chats.Book.MyId;
    public string NameOf(string id) => Thread.Members.FirstOrDefault(m => m.Id == id)?.Name ?? "Someone";

    // start one: ring the group, and be in it
    public async Task Start() { await chats.SendCall(Thread, new JsonObject { ["k"] = "ring", ["call"] = Id, ["v"] = Kind }); await Join(); }

    // in: tell everyone; those already in send invites
    public Task Join() => chats.SendCall(Thread, new JsonObject { ["k"] = "join", ["call"] = Id });

    void Signal(ChatThread t, JsonObject o)
    {
        if (t != Thread || (string?)o["call"] != Id) return;
        var from = (string?)o["from"] ?? "";
        switch ((string?)o["k"])
        {
            case "join":   // someone new: we're already in, so we invite them
                lock (In) In.Add(from);
                Changed?.Invoke();
                _ = Offer(from);
                break;
            case "offer" when (string?)o["to"] == chats.Book.MyId:   // an invite for us from someone already in
                lock (In) In.Add(from);
                _ = Answer(from, (string?)o["code"] ?? "");
                break;
            case "leave":
                lock (In) In.Remove(from);
                Drop(from);
                break;
        }
    }

    async Task Offer(string who)
    {
        try
        {
            var p = Line(who);
            var code = await p.Invite(me);
            await chats.SendCall(Thread, new JsonObject { ["k"] = "offer", ["call"] = Id, ["to"] = who, ["code"] = code });
        }
        catch { Drop(who); }
    }

    async Task Answer(string who, string code)
    {
        try { await Line(who).Join(code, me); } catch { Drop(who); }
    }

    // a new line to someone: on open, sound starts, your mic as set, and your video if it's on
    Portal Line(string who)
    {
        Drop(who);
        var p = new Portal();
        lock (Peers) Peers[who] = p;
        p.Opened += async () =>
        {
            try { await p.StartSound(); if (Muted) p.Mute(true); } catch { }
            PeerAdded?.Invoke(who, p); Changed?.Invoke();
        };
        p.Closed += () => { lock (In) In.Remove(who); Drop(who); };
        return p;
    }

    void Drop(string who)
    {
        Portal? p;
        lock (Peers) { if (!Peers.Remove(who, out p)) return; }
        _ = Close(p);
        PeerGone?.Invoke(who); Changed?.Invoke();
    }
    static async Task Close(Portal p) { try { await p.EndCall(); } catch { } p.Dispose(); }

    public void Mute(bool muted) { Muted = muted; foreach (var p in Lines()) p.Mute(muted); Changed?.Invoke(); }

    // camera / screen on or off: captured here, everyone's lines follow
    public async Task Video(string what, ShareTarget? share = null)
    {
        if (what == "camera") await Local.StartCamera();
        else if (what == "camera-off") await Local.StopCamera();
        else if (what == "screen" && share != null) await Local.StartScreen(share);
        else if (what == "screen-off") await Local.StopScreen();
        if (Local.Sending == "") foreach (var p in Lines()) if (p.Open) p.ForwardOff();
        Changed?.Invoke();
    }

    public void Leave()
    {
        _ = chats.SendCall(Thread, new JsonObject { ["k"] = "leave", ["call"] = Id });
        Dispose();
    }

    public void Dispose()
    {
        chats.CallSignal -= Signal;
        foreach (var who in Peers.Keys.ToList()) Drop(who);
        _ = Local.StopVideo(); Local.Dispose();
    }
}
