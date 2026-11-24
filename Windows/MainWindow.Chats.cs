using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Ivo;

// Ivo Chats in Ivo: the rail's Chats button (just above Settings) opens the list of your contacts and groups. History is
// kept in <data>/chats.json, sealed with the app lock like your other chats. Ask Ivo answers with your own model.
public partial class MainWindow
{
    IvoChats? ivoChats;
    IvoChatsView? chatsView;   // the Chats window's view, while it's open
    static string ChatsPath => Path.Combine(Store.Dir, "chats.json");

    // Portal's devices and Chats' downloads folder, from settings
    void ApplyPortalSettings()
    {
        PortalDevices.Camera = S.PortalCamera == "" ? null : S.PortalCamera;
        PortalDevices.Mic = S.PortalMic; PortalDevices.Speaker = S.PortalSpeaker;
        if (S.ChatDownloads != "") PortalDevices.Downloads = S.ChatDownloads;
        PortalDevices.MessageSound = S.PortalMessageSound; PortalDevices.CallSound = S.PortalCallSound; PortalDevices.ReadReceipts = S.PortalReadReceipts;
        if (S.PortalReactions.Count == 6) PortalDevices.Reactions = [.. S.PortalReactions];
    }

    void SavePortalSettings()
    {
        S.PortalCamera = PortalDevices.Camera ?? ""; S.PortalMic = PortalDevices.Mic; S.PortalSpeaker = PortalDevices.Speaker; S.ChatDownloads = PortalDevices.Downloads;
        S.PortalMessageSound = PortalDevices.MessageSound; S.PortalCallSound = PortalDevices.CallSound; S.PortalReadReceipts = PortalDevices.ReadReceipts; S.PortalReactions = [.. PortalDevices.Reactions];
        SaveSettings();
    }

    // ---------- Recently deleted contacts ----------
    // removed: into the bin (their calls stop, their chat hides); restored: back as before; after 15 days: gone for both
    void RemoveContact(PortalContact c)
    {
        S.PortalContacts.Remove(c); c.Removed = DateTime.Now; S.PortalContactsBin.Add(c); SaveSettings();
        ivoChats?.Bin(c.Box, true); ListenForContacts(); OpenPortal();
    }
    void RestoreContact(PortalContact c)
    {
        S.PortalContactsBin.Remove(c); c.Removed = null; S.PortalContacts.Add(c); SaveSettings();
        ivoChats?.Bin(c.Box, false); ListenForContacts();
    }
    System.Windows.Threading.DispatcherTimer? binTimer;
    async Task EmptyContactBin()
    {
        foreach (var c in S.PortalContactsBin.Where(c => c.Expired).ToList())
        {
            if (ivoChats != null) await ivoChats.Unfriend(c.Box);
            S.PortalContactsBin.Remove(c);
        }
        SaveSettings();
    }
    // they deleted us for good: the contact goes here too
    void Unfriended(string box)
    {
        S.PortalContacts.RemoveAll(c => c.Box == box); S.PortalContactsBin.RemoveAll(c => c.Box == box); SaveSettings();
        ListenForContacts(); if (panelKey == "portal") OpenPortal();
    }

    void OpenPortalSettings()
    {
        var look = new ChatLook((Brush)FindResource("Fg"), (Brush)FindResource("Dim"), (Brush)FindResource("BoxCard"), (Brush)FindResource("InputBg"), (Brush)FindResource("AccentGrad"), (Brush)FindResource("BoxCard"));
        OpenPanel("portal", "Portal", "\uED26", "#A78BFA", new PortalSettingsView(look, SavePortalSettings, OpenPortal, (() => S.PortalContactsBin, RestoreContact)));
    }

    void StartChats()
    {
        ApplyPortalSettings();
        string? json = null;
        try { json = File.Exists(ChatsPath) ? Store.ReadData(ChatsPath) : ""; } catch { json = ""; }
        if (json == null) return;   // sealed until Ivo is unlocked
        ivoChats = new IvoChats(json, text => { Directory.CreateDirectory(Store.Dir); Store.WriteData(ChatsPath, text); });
        ivoChats.CallSignal += (t, o) => Dispatcher.BeginInvoke(() => GroupSignal(t, o));
        ivoChats.TheyreBusy += t => Dispatcher.BeginInvoke(() => PortalTheyreBusy(t));
        ivoChats.Merged += (dm, g, id, kind) => Dispatcher.BeginInvoke(() => MergedInto(dm, g, id, kind));
        ivoChats.Unfriended += box => Dispatcher.BeginInvoke(() => Unfriended(box));
        _ = EmptyContactBin();
        binTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromHours(6), System.Windows.Threading.DispatcherPriority.Background, (_, _) => _ = EmptyContactBin(), Dispatcher);   // Ivo may run for days in the tray
        ivoChats.Changed += (t, m) => Dispatcher.BeginInvoke(() =>
        {
            MarkChats();
            // a notification unless you're looking at this very conversation (Ivo in front, Chats open, that chat open)
            var looking = IsActive && WindowState != WindowState.Minimized && panelKey == "portal" && portalTab == "chats" && chatsView?.Showing == t || IsActive && portalChat?.Showing == t;
            if (m != null && !looking)
            {
                Sounds.Chime("message");
                NotifyCard.Show(t.Kind == "group" ? $"{m.FromName} · {t.Name}" : m.FromName, m.Kind switch { "file" => "📎 " + m.File, _ => m.Text.Length > 90 ? m.Text[..90] + "…" : m.Text },
                                "", "#A78BFA", () => OpenChats(t), m.FromName, t.Members.FirstOrDefault(x => x.Id == m.From)?.Pic);   // their face leads the card
            }
        });
        ivoChats.Sync(S.PortalContacts, PortalMe);
        MarkChats();
    }

    // a chat: Portal's window on its Chats tab (that conversation, if one's given)
    void OpenChats(ChatThread? thread)
    {
        portalTab = "chats"; chatsThread = thread;
        OpenPortal();
    }
    ChatThread? chatsThread;

    // the Chats tab: the list of conversations and groups (or the one asked for)
    FrameworkElement ChatsPane()
    {
        if (ivoChats == null) StartChats();
        if (ivoChats == null) return Say("Unlock Ivo to see your chats.");
        var look = new ChatLook((Brush)FindResource("Fg"), (Brush)FindResource("Dim"), (Brush)FindResource("BoxCard"), (Brush)FindResource("InputBg"),
                                (Brush)FindResource("AccentGrad"), new SolidColorBrush(Color.FromArgb(0x55, 0x7C, 0x5C, 0xFF)));
        var view = chatsView = new IvoChatsView(ivoChats, look, ChatAsk, (t, kind) =>
        {
            if (t.Kind == "group") _ = EnterGroupCall(t, Guid.NewGuid().ToString("N")[..10], kind, start: true);
            else if (S.PortalContacts.FirstOrDefault(c => c.Box == t.ContactBox) is { } c) _ = CallContact(c, kind);
        })
        {
            Listen = SpotifyOn ? t => _ = InviteToListen(t) : null,
            JoinListen = SpotifyOn ? t => { if (S.PortalContacts.FirstOrDefault(c => c.Box == t.ContactBox) is { } c) _ = CallContact(c, "listenjoin"); } : null,
        };
        if (chatsThread is { } th) { view.OpenThread(th); chatsThread = null; }
        return view;
    }


    // the rail's Portal button: how many chats have something new
    void MarkChats()
    {
        var n = ivoChats?.UnreadChats ?? 0;
        BtnPortal.ToolTip = n > 0 ? $"Portal · {n} chat{(n == 1 ? "" : "s")} with something new" : "Portal · chats, contacts and calls";
    }

    // Listen along, as Discord does it: an invite card in the chat with what you're playing; whoever clicks Join connects
    // to you and their Spotify follows yours. The invite holds for two hours; a join within it needs no "accept" here.
    // While it holds, the card follows your music: each new song updates it on both sides.
    readonly Dictionary<string, DateTime> listenInvites = [];   // contact box → until when their join is let in
    readonly Dictionary<string, (ChatThread Thread, ChatMsg Card)> listenCards = [];   // contact box → the invite card showing what you play
    async Task InviteToListen(ChatThread t)
    {
        var item = nowPlaying?["item"];
        if (item == null) { _ = Notice("Nothing playing", "Play something on Spotify, then invite them to listen along."); return; }
        listenInvites[t.ContactBox ?? ""] = DateTime.Now.AddHours(2);
        var card = new ChatMsg { Kind = "listen", Text = (string?)item["name"] ?? "", Artist = SpotifyView.Artists(item), Url = (string?)item["album"]?["images"]?[0]?["url"] };
        listenCards[t.ContactBox ?? ""] = (t, card);
        await ivoChats!.Send(t, card);
    }

    void NewSongForInvites(System.Text.Json.Nodes.JsonNode item)
    {
        if (ivoChats == null) return;
        foreach (var (box, (t, card)) in listenCards.ToList())
        {
            if (!listenInvites.TryGetValue(box, out var until) || DateTime.Now >= until) { listenCards.Remove(box); continue; }
            _ = ivoChats.UpdateListen(t, card, (string?)item["name"] ?? "", SpotifyView.Artists(item), (string?)item["album"]?["images"]?[0]?["url"]);
        }
    }

    // Ask Ivo in a chat: your own model, with the thread's last messages, and the answer goes to everyone there
    async Task<string> ChatAsk(ChatThread t, string question)
    {
        var c = NewChat(); c.Space = "chat"; c.Workspace = null; c.Incognito = true;
        c.System = $"You're Ivo, answering in a chat called \"{t.Name}\" ({(t.Kind == "group" ? "a group" : "between two people")}). Everyone there reads your answer. Be short and clear.";
        var context = string.Join("\n", t.Messages.TakeLast(15).Where(m => m.Kind is "text" or "ivo").Select(m => $"{(m.Kind == "ivo" ? "Ivo" : m.FromName)}: {m.Text}"));
        c.Messages.Add(new("user", $"The conversation so far:\n{context}\n\n{PortalMe} asks you: {question}"));
        var cfg = c.Provider == Router.Name && S.AutoOn ? Router.Cfg : S.Providers.FirstOrDefault(p => p.Name == c.Provider) ?? throw new InvalidOperationException("Pick a model in Ivo first.");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var r = cfg.Kind == "cli" ? await Cli.Run(cfg, c, _ => { }, _ => { }, cts.Token) : await WithWeb(cfg, c, cts.Token);
        return r.Text.Trim();
    }
}
