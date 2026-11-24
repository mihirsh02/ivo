using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Portal contacts in Ivo: the list on Portal's start page (Connect, Call, Video), incoming Portal requests,
// and Add to contacts during a session. Kept in settings, so they're sealed with the app lock like your chats.
public partial class MainWindow
{
    CancellationTokenSource? contactsListening;
    (PortalContact Who, string Invite, string Kind, DateTime At)? incomingPortal;   // a contact calling, waiting for Accept
    string portalRingOnOpen = "";       // we called a contact: once connected, ring with this (voice · video · screen)
    string portalAnswerKind = "";       // we accepted a contact's call: answer their ring at once
    (string Box, string Key)? contactOffered;    // we asked them to add us; saved when they say yes
    JsonObject? contactAsked;                    // they asked us
    PortalCallView? callView;
    Grid? callLayer;
    bool callFull;

    // listen for Portal requests in every contact's mailbox (again whenever the list changes)
    void ListenForContacts()
    {
        ivoChats?.Sync(S.PortalContacts, PortalMe);   // a thread for every contact
        contactsListening?.Cancel();
        if (S.PortalContacts.Count == 0) return;
        contactsListening = new CancellationTokenSource();
        _ = PortalContacts.Listen(S.PortalContacts.ToList(), (c, invite, kind) => Dispatcher.BeginInvoke(() => IncomingPortal(c, invite, kind)), contactsListening.Token);
    }

    void IncomingPortal(PortalContact c, string invite, string kind)
    {
        // they hung up while it rang: the ringing stops, and it's a missed call
        if (kind == PortalContacts.Cancelled)
        {
            if (incomingPortal is { } k && k.Invite == invite)
            {
                incomingPortal = null; Sounds.StopRing(); HideIncoming();
                NotifyCard.Show($"Missed call from {c.Name}", "Portal", "\uE717", "#A78BFA", OpenPortal);
                if (panelKey == "portal") OpenPortal();
            }
            return;
        }
        if (ivoChats?.IsBlockedContact(c.Box) == true) return;   // blocked: their call never rings
        if (portalStage == "open" || groupCall != null)   // already on a call: call waiting (they hear you're busy; you can merge them in)
        {
            if (kind is not ("voice" or "video" or "screen")) return;
            var theirs = ivoChats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box);
            if (theirs != null) _ = ivoChats!.Busy(theirs);
            incomingPortal = (c, invite, kind, DateTime.Now);
            Sounds.Chime("call");
            if (CallAway) NotifyCard.Show($"{c.Name} is calling", "You're on another call: open Ivo to merge them in", "\uE717", "#A78BFA", OpenPortal);
            RemoveIncoming();
            if (IsVisible && WindowState != WindowState.Minimized)
                removeIncoming = IncomingCall.Show(Root, Root, c.Name, kind, theirs?.Members.FirstOrDefault()?.Pic,
                    () => { incomingPortal = null; removeIncoming = null; if (theirs != null) _ = MergeCall([theirs]); },
                    () => { incomingPortal = null; removeIncoming = null; }, () => removeIncoming = null, "Merge", "Calling you · you're on another call");
            return;
        }
        incomingPortal = (c, invite, kind, DateTime.Now);
        // joining our listen-along invite: let them in straight away, as Discord does
        if (kind == "listenjoin" && listenInvites.TryGetValue(c.Box, out var until) && DateTime.Now < until) { _ = AcceptPortal(); return; }
        Sounds.Ring();
        ShowIncoming(c.Name, kind, () => _ = AcceptPortal(), () => { Sounds.StopRing(); incomingPortal = null; if (panelKey == "portal") OpenPortal(); },
                     ivoChats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box)?.Members.FirstOrDefault()?.Pic);
        if (CallAway) NotifyCard.Show($"{c.Name} is calling", kind == "video" ? "Portal · video call" : kind == "screen" ? "Portal · wants to share their screen" : kind == "voice" ? "Portal · voice call" : "Portal · wants to connect", "", "#A78BFA", OpenPortal);
        if (panelKey == "portal") OpenPortal();
    }

    async Task AcceptPortal()
    {
        Sounds.StopRing(); HideIncoming();
        if (incomingPortal is not { } k) return;
        incomingPortal = null;
        try
        {
            NewPortal(); portalAnswerKind = k.Kind; portalWith = k.Who;
            (portalCode, portalDelivered) = await portal!.Join(k.Invite, PortalMe);
            portalStage = "joining";
        }
        catch (Exception ex) { EndPortal(quiet: true); _ = Notice("Couldn't connect", ex.Message); }
        OpenPortal();
    }

    async Task CallContact(PortalContact c, string kind)
    {
        try
        {
            NewPortal(); portalRingOnOpen = kind; portalWith = c;
            portalCode = await portal!.Invite(PortalMe);
            portalStage = "calling"; portalCalling = c.Name;
            OpenPortal(); Sounds.Calling();
            if (!await PortalContacts.RequestCall(c, portalCode, kind)) { portalStage = "inviting"; Sounds.StopCalling(); }   // the relay couldn't be reached: send the invite by hand
        }
        catch (Exception ex) { EndPortal(quiet: true); _ = Notice("Couldn't call", ex.Message); }
        OpenPortal();
    }
    string portalCalling = "";

    // ---------- merging calls and adding people ----------
    // A one-to-one call becomes a group call: a new group with them and whoever's added (handed over in each chat),
    // the person you're with moves over at once (told on the call itself), the others are told in their chat, and
    // you start the group call. Already in a group call: they're added to that group and pulled into its call.
    readonly HashSet<string> merging = [];   // calls we're moving into (their ring isn't shown again)

    ChatThread? PeerThread() => portalWith is { } w ? ivoChats?.Book.Threads.FirstOrDefault(t => t.Kind == "dm" && t.ContactBox == w.Box) : null;

    void PickPeople(FrameworkElement at)
    {
        if (ivoChats == null) return;
        var inCall = groupCall?.Thread.Members.Select(m => m.Name).ToHashSet() ?? [];
        var others = ivoChats.Book.Threads.Where(t => t.Kind == "dm" && t.ContactBox != null && t.ContactBox != portalWith?.Box && !inCall.Contains(t.Name)).OrderBy(t => t.Name).ToList();
        IvoFrame.PickFrom(at, "Add to this call", others.Select(t => t.Name).ToList(), name => { if (others.FirstOrDefault(t => t.Name == name) is { } d) _ = MergeCall([d]); });
    }

    async Task MergeCall(List<ChatThread> add)
    {
        if (ivoChats == null || add.Count == 0) return;
        try
        {
            if (groupCall != null)
            {
                foreach (var d in add) { await ivoChats.Invite(groupCall.Thread, d); await ivoChats.Merge(d, groupCall.Thread, groupCall.Id, groupCall.Kind); }
                return;
            }
            if (PeerThread() is not { } peer) { _ = Notice("This call can't be merged", "Calls merge with people in your contacts. Add them to your contacts first."); return; }
            var kind = portal?.CameraOn == true || portalCallKind == "video" ? "video" : "voice";
            var g = await ivoChats.NewGroup(string.Join(", ", new[] { peer.Name }.Concat(add.Select(a => a.Name))), [peer, .. add]);
            var id = Guid.NewGuid().ToString("N")[..12];
            merging.Add(id);
            portal?.Send(new JsonObject { ["t"] = "merge", ["g"] = g.Id, ["call"] = id, ["v"] = kind });
            await ivoChats.Merge(peer, g, id, kind);   // in case the call's own line is slow
            foreach (var d in add) await ivoChats.Merge(d, g, id, kind);
            await Task.Delay(400);   // the message on its way before the one-to-one call closes
            HangUp(false); EndPortal(quiet: true);
            await EnterGroupCall(g, id, kind, start: true);
        }
        catch (Exception ex) { _ = Notice("Couldn't merge the call", ex.Message); }
    }

    // told in a chat: only from the person we're calling or with (no one else can pull you into a call)
    void MergedInto(ChatThread dm, string groupId, string id, string kind)
    {
        if (portalStage is "calling" or "open" or "joining" && portalWith?.Box == dm.ContactBox) _ = JoinMerged(groupId, id, kind);
    }

    async Task JoinMerged(string groupId, string id, string kind)
    {
        if (ivoChats == null || groupId == "" || !merging.Add(id)) return;
        if (portalStage != "") { HangUp(false); EndPortal(quiet: true); }
        var g = await ivoChats.WaitForGroup(groupId);
        if (g == null) { _ = Notice("Couldn't join the merged call", "The group didn't arrive. Ask them to add you again."); return; }
        await EnterGroupCall(g, id, kind, start: false);
    }

    // the contact we're calling answered "busy": they're on another call
    void PortalTheyreBusy(ChatThread t)
    {
        if (portalStage != "calling" || portalWith is not { } calling || calling.Box != t.ContactBox) return;
        _ = Notice($"{calling.Name} is on another call", "It's still ringing: they can add you to their call.");
    }

    // the start page's top: someone calling, then your contacts
    void ContactsSection(StackPanel page)
    {
        if (incomingPortal is { } k && DateTime.Now - k.At < TimeSpan.FromMinutes(2))
        {
            var accept = new Button { Content = "Accept", Style = (Style)FindResource("Shimmer"), Height = 40, Margin = new Thickness(0, 12, 8, 0), Width = 130 };
            var decline = new Button { Content = "Decline", Style = (Style)FindResource("Danger"), Height = 40, Margin = new Thickness(0, 12, 0, 0) };
            accept.Click += (_, _) => _ = AcceptPortal();
            decline.Click += (_, _) => { Sounds.StopRing(); HideIncoming(); incomingPortal = null; OpenPortal(); };
            page.Children.Add(Box(new StackPanel { Children = { Say($"{k.Who.Name} is calling", 15, "Fg", FontWeights.SemiBold), new Border { Height = 4 },
                Say(k.Kind == "listenjoin" ? "They want to listen along" : k.Kind == "video" ? "A video call" : k.Kind == "screen" ? "They want to share their screen" : k.Kind == "voice" ? "A voice call" : "They want to connect"),
                new StackPanel { Orientation = Orientation.Horizontal, Children = { accept, decline } } } }, 20));
        }
    }

    // during a session: their request to add you, as a card
    FrameworkElement? ContactAsked()
    {
        if (portal == null || contactAsked is not { } asked) return null;
        var add = new Button { Content = "Add", Style = (Style)FindResource("Shimmer"), Margin = new Thickness(0, 10, 8, 0), Width = 90 };
        var no = new Button { Content = "Not now", Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) =>
        {
            SaveContact(portal.PeerName, (string?)asked["box"] ?? "", (string?)asked["key"] ?? "");
            portal.Send(new JsonObject { ["t"] = "contact-ok" });
            contactAsked = null; OpenPortal();
        };
        no.Click += (_, _) => { portal.Send(new JsonObject { ["t"] = "contact-no" }); contactAsked = null; OpenPortal(); };
        return Box(new StackPanel { Children = { Say($"{portal.PeerName} wants to add you to their contacts", 13, "Fg"), new StackPanel { Orientation = Orientation.Horizontal, Children = { add, no } } } }, 14);
    }

    // Add to contacts: a chip with the other actions (gone once they're a contact, or while asked)
    FrameworkElement? ContactOffer()
    {
        if (portal == null || contactAsked != null) return null;
        if (contactOffered != null) { var waiting = PortalChip("Asked to add…", () => { }); waiting.IsEnabled = false; return waiting; }
        if (S.PortalContacts.Any(c => c.Name == portal.PeerName)) return null;
        return PortalChip("Add to contacts", () =>
        {
            var c = PortalContacts.Make(portal.PeerName);
            contactOffered = (c.Box, c.Key);
            SaveContact(c.Name, c.Box, c.Key, quiet: true);   // kept now: a session that drops before their answer doesn't lose it
            portal.Send(new JsonObject { ["t"] = "contact", ["box"] = c.Box, ["key"] = c.Key });
            OpenPortal();
        });
    }

    void SaveContact(string name, string box, string key, bool quiet = false)
    {
        if (box == "" || key == "") return;
        S.PortalContacts.RemoveAll(c => c.Box == box);
        S.PortalContacts.Add(new PortalContact { Name = name, Box = box, Key = key });
        SaveSettings(); ListenForContacts();
        if (!quiet) NotifyCard.Show("Contact added", $"{name} · call them from Portal any time", "", "#A78BFA", OpenPortal);
    }

    void ContactMessage(JsonObject o)
    {
        switch ((string?)o["t"])
        {
            case "contact": contactAsked = o; break;
            case "contact-ok" when contactOffered is { } mine: SaveContact(portal!.PeerName, mine.Box, mine.Key); contactOffered = null; break;
            case "contact-no" when contactOffered is { } mine:   // they said not now: take it back
                S.PortalContacts.RemoveAll(c => c.Box == mine.Box); SaveSettings(); ListenForContacts(); contactOffered = null; break;
        }
        if (panelKey == "portal") OpenPortal();
    }

    // the picture of who we're in a call with, from their chat (null until they've shared one)
    string? PeerPic() => portal == null ? null : S.PortalContacts.FirstOrDefault(c => c.Name == portal.PeerName) is { } c
        ? ivoChats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box)?.Members.FirstOrDefault()?.Pic : null;

    // the call, inside Ivo: over everything under the title bar, the app's own corners; full screen fills the whole window.
    // Back to Ivo hides it (the call goes on, and the title bar's pill brings it back).
    void ShowCallWindow(bool lobby = false)
    {
        if (portal == null) return;
        if (callLayer != null) { callLayer.Visibility = Visibility.Visible; UpdatePortalPill(); FloatCall(); return; }
        ClosePanel();
        callView = new PortalCallView(portal, portal.PeerName, () => HangUp(tell: true), Video, CallFullScreen, () => { if (callFull) CallFullScreen(); callLayer!.Visibility = Visibility.Collapsed; UpdatePortalPill(); FloatCall(); }, PortalChatPane(), PortalMe, lobby ? (_, _) => AcceptCall() : null, PeerPic(), ivoChats?.Book.Pic);
        callView.AddPeopleButton(PickPeople);
        callView.Ticked += UpdatePortalPill;
        callLayer = new Grid { Margin = new Thickness(14, 56, 14, 14), Children = { new Border { CornerRadius = new CornerRadius(18), ClipToBounds = true, Child = callView } } };
        Grid.SetRowSpan(callLayer, 99); Grid.SetColumnSpan(callLayer, 99); Panel.SetZIndex(callLayer, 950);
        Root.Children.Add(callLayer);
        callLayer.Opacity = 0;
        callLayer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        UpdatePortalPill();
        FloatCall();
    }

    void CallFullScreen()
    {
        if (callLayer == null) return;
        callFull = !callFull;
        if (callFull) { FullScreen(true); callLayer.Margin = new Thickness(0); Panel.SetZIndex(callLayer, 2000); }
        else { FullScreen(false); callLayer.Margin = new Thickness(14, 56, 14, 14); Panel.SetZIndex(callLayer, 950); }
        ((Border)callLayer.Children[0]).CornerRadius = new CornerRadius(callFull ? 0 : 18);
        callView?.FullScreenShown(callFull);
    }

    void CloseCall()
    {
        if (callFull) CallFullScreen();
        callView?.Detach(); callView = null;
        if (callLayer != null) Root.Children.Remove(callLayer);
        callLayer = null;
        UpdatePortalPill();
        FloatCall();
    }

    // the call floats in a corner of the screen whenever it isn't on show: Ivo minimised, in the tray, behind another
    // app, or the call tucked away inside Ivo. The taskbar button carries a red dot all call long.
    CallFloat? callFloat;
    bool floatHooked, floatClosed;
    void FloatCall()
    {
        if (!floatHooked)
        {
            floatHooked = true;
            StateChanged += (_, _) => FloatCall(); IsVisibleChanged += (_, _) => FloatCall();
        }
        var inCall = portalCall == "on" && callView != null && portal != null;
        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.Overlay = inCall ? LiveDot.Value : null;
        TaskbarItemInfo.Description = inCall ? $"On a call with {portal!.PeerName}" : "";
        var onShow = IsVisible && WindowState != WindowState.Minimized && callLayer is { Visibility: Visibility.Visible };
        if (onShow || !inCall) floatClosed = false;   // closed with ✕: back the next time the call goes out of sight
        if (inCall && !onShow && !floatClosed)
        {
            if (callFloat != null) return;
            void Open() { ShowWindow(); ShowCallWindow(); }
            var f = callFloat = new CallFloat(portal!, portal!.PeerName, () => callView?.Elapsed ?? "", Open, () => { Open(); if (!callFull) CallFullScreen(); }, Video, () => HangUp(tell: true), PeerPic());
            f.Closed += (_, _) => { if (f.Dismissed) floatClosed = true; if (callFloat == f) callFloat = null; };
            f.Show();
        }
        else callFloat?.Close();
    }
    // the taskbar's "on a call" mark: a small red dot
    static readonly Lazy<ImageSource> LiveDot = new(() =>
    {
        var d = new DrawingImage(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)), new Pen(Brushes.White, 1.5), new EllipseGeometry(new Point(8, 8), 6.5, 6.5)));
        d.Freeze(); return d;
    });

    // the title bar's pill: shown while connected; with the call's time while in one
    void UpdatePortalPill()
    {
        // an ignored call waiting: the pill says who, a click brings the call screen back
        if (callIgnored && (incomingPortal is { } k && DateTime.Now - k.At < TimeSpan.FromMinutes(2) || portalCall == "ringing"))
        {
            PortalPill.Visibility = Visibility.Visible;
            PortalPillText.Text = $"{(incomingPortal is { } kk ? kk.Who.Name : portal?.PeerName)} is calling";
            PortalDot.Fill = Shell.B("#22C55E");
            PortalPill.ToolTip = "Answer or decline";
            return;
        }
        // a group call tucked away: the pill brings it back
        if (groupCall != null && groupLayer is { Visibility: not Visibility.Visible })
        {
            PortalPill.Visibility = Visibility.Visible;
            PortalPillText.Text = $"{groupCall.Thread.Name} · {groupView?.Elapsed}";
            PortalDot.Fill = Shell.B("#F43F5E");
            PortalPill.ToolTip = "Back to the group call";
            return;
        }
        var on = portal != null && portalStage == "open";
        PortalPill.Visibility = on && callLayer is not { Visibility: Visibility.Visible } ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        var inCall = portalCall == "on" && callView != null;
        PortalPillText.Text = inCall ? $"{portal!.PeerName} · {callView!.Elapsed}" : portal!.PeerName;
        PortalDot.Fill = Shell.B(inCall ? "#F43F5E" : "#3FB950");
        PortalPill.ToolTip = inCall ? "Back to the call" : "Portal · connected";
    }

    void PortalPill_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (callIgnored && showAgain != null) showAgain();
        else if (groupCall != null && groupLayer is { Visibility: not Visibility.Visible }) { ShowGroupCall(); UpdatePortalPill(); }
        else if (portalCall == "on" && callLayer != null) ShowCallWindow(); else OpenPortal();
    }
}
