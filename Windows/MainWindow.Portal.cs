using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Portal (rail): a live session with one other Ivo. Swap two codes, then chat; either side can ask its own Ivo and the
// answer shows for both (each uses its own model and key; nothing of yours is lent). Listen together keeps both
// Spotify players on the same song at the same moment, each on its own account.
public partial class MainWindow
{
    Portal? portal;
    string portalStage = "";        // "" · inviting · joining · open
    string portalCode = "";
    bool portalDelivered;          // joining: the reply went back through the relay (nothing to send by hand)         // the code this side shows (an invite or a reply)
    readonly List<(string Who, string Text, bool Ivo, bool Mine, string? ReplyFrom, string? ReplyText, long At)> portalLog = [];
    (string Who, string Text, bool Ivo, bool Mine, string? ReplyFrom, string? ReplyText, long At)? portalReplying;
    bool portalListen;              // Listen together, on this side
    StackPanel? portalList;
    ScrollViewer? portalScroll;

    string PortalMe => S.Profile is { Name: { Length: > 0 } n } ? n.Split(' ')[0] : Environment.UserName;

    void OpenPortal_Click(object s, RoutedEventArgs e) { if (panelKey == "portal") ClosePanel(); else OpenPortal(); }

    string portalTab = "chats";   // session · chats · contacts · new

    // Portal: making or joining one, its steps; otherwise tabs: Chats | Contacts | New portal, and in a session (or a call)
    // the session as its own first tab, so your other chats and contacts stay a click away
    void OpenPortal()
    {
        var open = portalStage == "open";
        if (!open && portalStage != "") { var steps = new StackPanel(); PortalStart(steps); OpenPanel("portal", "Portal", "\uED26", "#A78BFA", steps); return; }
        if (!open && portalTab == "session") portalTab = "chats";
        if (open && portalTab == "new") portalTab = "session";
        var page = new DockPanel();
        var calling = new StackPanel(); ContactsSection(calling);   // someone calling: above the tabs, whichever is open
        DockPanel.SetDock(calling, Dock.Top); page.Children.Add(calling);
        var tabs = PortalTabs(); DockPanel.SetDock(tabs, Dock.Top); page.Children.Add(tabs);
        if (portalTab == "session") { page.Children.Add(PortalSession()); OpenPanel("portal", "Portal", "", "#A78BFA", page, scroll: false); portalScroll?.ScrollToEnd(); return; }
        if (portalTab == "chats") page.Children.Add(ChatsPane());
        else if (portalTab == "contacts") page.Children.Add(new ScrollViewer { Content = ContactsPane(), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        else { var start = new StackPanel(); PortalStart(start); page.Children.Add(new ScrollViewer { Content = start, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }); }
        OpenPanel("portal", "Portal", "\uED26", "#A78BFA", page, scroll: false);
    }

    // Chats | Contacts | New portal: one segmented switch across the width
    FrameworkElement PortalTabs()
    {
        var unread = ivoChats?.UnreadChats ?? 0;
        var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        var chatsTab = ("chats", unread > 0 ? $"Chats  {unread}" : "Chats");
        var all = portalStage == "open" && portal != null
            ? new[] { ("session", portalCall == "on" ? $"{portal.PeerName} · in call" : portal.PeerName), chatsTab, ("contacts", "Contacts") }
            : new[] { chatsTab, ("contacts", "Contacts"), ("new", "New portal") };
        foreach (var (id, label) in all)
        {
            var b = new RadioButton { GroupName = "PortalTabs", Content = label, Style = (Style)FindResource("Segment"), IsChecked = portalTab == id, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => { portalTab = id; OpenPortal(); };
            row.Children.Add(b);
        }
        var track = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(3), Margin = new Thickness(0, 0, 0, 14), Child = row };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBg");
        return track;
    }

    // your contacts: each with Chat, Call, Video and Remove
    FrameworkElement ContactsPane()
    {
        var list = new StackPanel();
        if (S.PortalContacts.Count == 0)
        {
            list.Children.Add(Say("No contacts yet", 15, "Fg", FontWeights.SemiBold));
            list.Children.Add(Say("Connect with someone in New portal, then Add to contacts: after that you can chat and call any time, with no codes."));
            return list;
        }
        foreach (var c in S.PortalContacts.OrderBy(c => c.Name))
        {
            var who = ivoChats?.Book.Threads.FirstOrDefault(t => t.ContactBox == c.Box);
            var person = who?.Members.FirstOrDefault();
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (who != null) tools.Children.Add(IvoChatsView.Round("\uE8BD", $"Chat with {c.Name}", "#A78BFA", () => OpenChats(who), App.IconFont));
            tools.Children.Add(IvoChatsView.Round("\uE717", $"Call {c.Name}", "#34D399", () => _ = CallContact(c, "voice"), App.IconFont));
            tools.Children.Add(IvoChatsView.Round("\uE714", $"Video call {c.Name}", "#60A5FA", () => _ = CallContact(c, "video"), App.IconFont));
            tools.Children.Add(IvoChatsView.Round("\uE74D", "Remove from contacts (restorable for 15 days)", "#F87171", () => RemoveContact(c), App.IconFont));
            DockPanel.SetDock(tools, Dock.Right); row.Children.Add(tools);
            var face = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                                    Child = new TextBlock { Text = c.Name[..1].ToUpper(), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            face.SetResourceReference(Border.BackgroundProperty, "AccentGrad");
            if (person?.Pic is { Length: > 0 } pic)
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage(); img.BeginInit(); img.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(pic)); img.DecodePixelWidth = 96; img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.EndInit();
                    face.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; face.Child = null; RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.HighQuality);
                }
                catch { }
            DockPanel.SetDock(face, Dock.Left); row.Children.Add(face);
            var status = person?.Presence ?? "Offline";
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Say(c.Name, 14, "Fg", FontWeights.SemiBold), Say(status, 12) } });
            list.Children.Add(row);
        }
        return list;
    }

    static TextBlock Say(string text, double size = 13, string brush = "Dim", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    // a code to send: shown in full, with Copy
    FrameworkElement CodeBox(string code, string label)
    {
        // shown, not editable: no cursor, nothing to type into; Copy takes it whole
        var text = new TextBlock { Text = code, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = new ScrollViewer { Content = text, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        box.SetResourceReference(Border.BackgroundProperty, "InputBg");
        var copy = new Button { Content = "Copy", Style = (Style)FindResource("Shimmer"), Width = 110, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) => { Clipboard.SetText(code); copy.Content = "Copied"; };
        return new StackPanel { Children = { Say(label, 13, "Fg", FontWeights.SemiBold), new Border { Height = 8 }, box, copy } };
    }

    // ---------- before you're connected: start one, or join one ----------
    void PortalStart(StackPanel page)
    {
        var note = Say(""); note.Foreground = Shell.B("#F87171"); note.Margin = new Thickness(0, 8, 0, 0);

        if (portalStage == "inviting")
        {
            var reply = new TextBox { TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
            var go = new Button { Content = "Connect", Style = (Style)FindResource("Shimmer"), Width = 110, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            go.Click += (_, _) =>
            {
                try { portal!.Accept(reply.Text); note.Text = ""; go.Content = "Connecting…"; go.IsEnabled = false; }
                catch (Exception ex) { note.Text = ex.Message; }
            };
            // their reply comes back by itself; pasting one by hand is only for when it can't
            var byHand = new StackPanel { Visibility = Visibility.Collapsed, Children = { new Border { Height = 14 }, Say("Paste the reply code they sent", 13, "Fg", FontWeights.SemiBold), new Border { Height = 8 }, Hinted(reply, "ivo-portal:…"), go } };
            var show = new Button { Content = "They sent a reply code?", Style = (Style)FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 8, 0, 0) };
            show.Click += (_, _) => { byHand.Visibility = Visibility.Visible; show.Visibility = Visibility.Collapsed; reply.Focus(); };
            page.Children.Add(Box(new StackPanel { Children = { CodeBox(portalCode, "Send this invite"), new Border { Height = 14 },
                Say("Waiting for them to join… you'll be connected as soon as they paste it.", 13), show, byHand, note } }, 20));
            page.Children.Add(PortalCancel());
            return;
        }
        if (portalStage == "calling")
        {
            page.Children.Add(Box(new StackPanel { Children = { Say($"Calling {portalCalling}…", 15, "Fg", FontWeights.SemiBold), new Border { Height = 6 },
                Say("Their Ivo or Ivo Portal rings if it's open. You'll be connected as soon as they accept.") } }, 20));
            page.Children.Add(PortalCancel());
            return;
        }
        if (portalStage == "joining")
        {
            // the reply went back on its own; the code is only shown when it couldn't
            page.Children.Add(Box(portalDelivered
                ? new StackPanel { Children = { Say($"Connecting to {portal?.PeerName}…", 15, "Fg", FontWeights.SemiBold), new Border { Height = 6 }, Say("This takes a few seconds.") } }
                : new StackPanel { Children = { CodeBox(portalCode, $"Send this reply to {portal?.PeerName}"), new Border { Height = 10 },
                    Say("Their Ivo couldn't be reached directly, so send this back to them. You'll be connected as soon as they paste it.") } }, 20));
            page.Children.Add(PortalCancel());
            return;
        }

        var start = new Button { Content = "Create an invite", Style = (Style)FindResource("Shimmer"), Height = 40, Margin = new Thickness(0, 12, 0, 0) };
        start.Click += async (_, _) =>
        {
            start.IsEnabled = false; start.Content = "Finding the way to you…";
            try { NewPortal(); portalCode = await portal!.Invite(PortalMe); portalStage = "inviting"; OpenPortal(); }
            catch (Exception ex) { start.IsEnabled = true; start.Content = "Create an invite"; note.Text = ex.Message; }
        };
        var invite = new TextBox { TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        var join = new Button { Content = "Join", Style = (Style)FindResource("Shimmer"), Height = 40, Margin = new Thickness(0, 12, 0, 0) };
        join.Click += async (_, _) =>
        {
            join.IsEnabled = false; join.Content = "Joining…";
            try { NewPortal(); (portalCode, portalDelivered) = await portal!.Join(invite.Text, PortalMe); portalStage = "joining"; OpenPortal(); }
            catch (Exception ex) { join.IsEnabled = true; join.Content = "Join"; note.Text = ex.Message; EndPortal(quiet: true); }
        };
        var grid = new StackPanel();   // one above the other: the window is a narrow column
        var a = Box(new StackPanel { Children = { Say("Open a portal", 15, "Fg", FontWeights.SemiBold), new Border { Height = 6 }, Say("Make an invite and send it. You're connected as soon as they join."), start } }, 20);
        var b = Box(new StackPanel { Children = { Say("Join a portal", 15, "Fg", FontWeights.SemiBold), new Border { Height = 6 }, Hinted(invite, "Paste their invite (ivo-portal:…)"), join } }, 20);
        grid.Children.Add(a); grid.Children.Add(b);
        page.Children.Add(grid);
        page.Children.Add(note);
    }

    FrameworkElement PortalCancel()
    {
        var c = new Button { Content = "Cancel", Style = (Style)FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
        c.Click += (_, _) => { EndPortal(quiet: true); OpenPortal(); };
        return c;
    }

    void NewPortal()
    {
        EndPortal(quiet: true);
        var k = portal = new Portal();
        HookVideo(k);
        // the window you shared was minimised or closed: sharing stops, they're told, and you see why
        k.SharingStopped += why => Dispatcher.BeginInvoke(() =>
        {
            if (portal != k) return;
            k.Send(new JsonObject { ["t"] = "video", ["on"] = k.Sending != "" });
            _ = Notice("Screen sharing stopped", why + ". Share it again once it's open.");
            if (callLayer?.Visibility == Visibility.Visible) ShowCallWindow(); else OpenPortal();
        });
        k.Opened += () => Dispatcher.BeginInvoke(() =>
        {
            if (portal != k) return;
            portalStage = "open"; portalTab = "session"; portalLog.Clear();
            k.Send(new JsonObject { ["t"] = "hello", ["n"] = PortalMe, ["a"] = "Ivo" });   // and which app this is
            // we called a contact: ring them now (or, for Listen together, start playing along); they asked us to listen along: follow
            if (portalRingOnOpen == "listenjoin") { portalRingOnOpen = ""; portalListen = true; }   // we joined their listen-along: follow their Spotify
            else if (portalRingOnOpen != "") { var kind = portalRingOnOpen; portalRingOnOpen = ""; Ring(kind); }
            if (portalAnswerKind == "listenjoin") { portalAnswerKind = ""; portalListen = true; PortalMusic(nowPlaying, force: true); }   // they joined ours: send what's playing
            UpdatePortalPill();
            Sounds.Chime("call");
            NotifyCard.Show("Portal", $"Connected with {k.PeerName}", "", "#A78BFA", OpenPortal);
            if (panelKey == "portal") OpenPortal();
            BtnPortal.ToolTip = $"Portal · with {k.PeerName}";
        });
        // it dropped: they left, or your own internet went (said so, not blamed on them)
        k.Closed += () => Dispatcher.BeginInvoke(async () =>
        {
            if (portal != k || portalStage != "open") return;
            var left = k.PeerName;
            EndPortal(quiet: true); if (panelKey == "portal") OpenPortal();
            if (await Portal.Online()) NotifyCard.Show("Portal ended", $"{left} left", "", "#A78BFA", OpenPortal);
            else NotifyCard.Show("Your internet dropped", "The portal closed. It can open again once you're back online.", "", "#A78BFA", OpenPortal);
        });
        k.Message += o => Dispatcher.BeginInvoke(() => { if (portal == k) PortalReceived(o); });
    }

    void EndPortal(bool quiet = false)
    {
        HideIncoming(); Sounds.StopCalling();
        // still ringing them (they haven't joined): call it off, so their phone stops
        if (portalStage == "calling" && portalWith is { } ringing && portalCode != "") _ = PortalContacts.CancelRequest(ringing, portalCode);
        if (portal != null && !quiet) portal.Send(new JsonObject { ["t"] = "bye" });
        portal?.Dispose(); portal = null;
        portalStage = ""; portalCode = ""; portalListen = false; portalList = null; portalLists.Clear(); portalScroll = null;
        portalCall = ""; portalCallWeRang = false; theirVideo = myVideo = null;
        portalRingOnOpen = portalAnswerKind = ""; contactOffered = null; contactAsked = null; portalWith = null; portalChat = null; portalPeerApp = "";
        CloseCall();
        if (BtnPortal != null) BtnPortal.ToolTip = "Portal · talk to another Ivo";
    }

    // ---------- connected ----------
    // one style for every action in Portal: a chip (a toggle for things that stay on, a plain press for the rest)
    System.Windows.Controls.Primitives.ToggleButton PortalChip(string text, Action click, bool? on = null, string? tip = null)
    {
        var c = new System.Windows.Controls.Primitives.ToggleButton { Content = text, Style = (Style)FindResource("Chip"), IsChecked = on == true, Margin = new Thickness(0, 0, 8, 8), ToolTip = tip };
        c.Click += (_, _) => { if (on == null) c.IsChecked = false; click(); };
        return c;
    }

    FrameworkElement PortalSession()
    {
        var page = new DockPanel();
        var who = Say($"Connected with {portal!.PeerName}{(portalPeerApp != "" ? $" · on {portalPeerApp}" : "")}", 15, "Fg", FontWeights.SemiBold); who.VerticalAlignment = VerticalAlignment.Center;
        var end = new Button { Content = "End", Style = (Style)FindResource("Danger"), Margin = new Thickness(0) };
        end.Click += (_, _) => { EndPortal(); OpenPortal(); };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(end, Dock.Right); top.Children.Add(end); top.Children.Add(who);
        DockPanel.SetDock(top, Dock.Top); page.Children.Add(top);

        // the actions, one row: calls (while none is on), Listen together, Add to contacts
        var actions = new WrapPanel { Margin = new Thickness(-6, 0, 0, 10) };
        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        if (portalCall == "")
        {
            actions.Children.Add(IvoChatsView.Round("", "Call", "#34D399", () => Ring("voice"), icons));
            actions.Children.Add(IvoChatsView.Round("", "Video call", "#60A5FA", () => Ring("video"), icons));
            actions.Children.Add(IvoChatsView.Round("", "Share your screen", "#22D3EE", () => Ring("screen"), icons));
        }
        else if (portalCall == "on")   // the call goes on: back to it, or hang up
        {
            actions.Children.Add(IvoChatsView.Round("", "Show the call", "#A78BFA", () => ShowCallWindow(), icons));
            actions.Children.Add(IvoChatsView.Round("", "Hang up", "#F43F5E", () => HangUp(tell: true), icons));
        }
        // Listen together: green while it's on (not with Ivo Portal, which has no Spotify)
        if (portalPeerApp != "Ivo Portal") actions.Children.Add(IvoChatsView.Round("", portalListen ? "Listening together · click to stop" : "Listen together: both Spotify players play the same song at the same moment (each needs Premium)",
            portalListen ? "#1DB954" : "#94A3B8", () => { portalListen = !portalListen; if (portalListen) PortalMusic(nowPlaying, force: true); OpenPortal(); }, icons));
        if (ContactOffer() is { } offer) actions.Children.Add(offer);
        DockPanel.SetDock(actions, Dock.Top); page.Children.Add(actions);
        foreach (var extra in new[] { portalCall is "calling" or "ringing" ? CallBar() : null, ContactAsked() })
            if (extra != null) { DockPanel.SetDock(extra, Dock.Top); page.Children.Add(extra); }

        page.Children.Add(PortalChatPane());
        return page;
    }

    // the chat with them: the messages (only they scroll) and the message bar right under them. In Portal's window, and
    // beside the call while one is on.
    string portalPeerApp = "";   // what they use, from their hello: "Ivo" or "Ivo Portal"
    PortalContact? portalWith;   // the contact this session is with (none: someone who isn't saved)
    IvoChatsView? portalChat;    // that contact's own conversation, while the session shows it

    // the session's chat: with a contact, your usual conversation with them (the same one as in Chats, kept);
    // with someone who isn't saved, a fresh one that lasts as long as the session
    FrameworkElement PortalChatPane()
    {
        if (portalWith is { } with)
        {
            if (ivoChats == null) StartChats();
            if (ivoChats?.Book.Threads.FirstOrDefault(t => t.ContactBox == with.Box) is { } thread)
            {
                var look = new ChatLook((Brush)FindResource("Fg"), (Brush)FindResource("Dim"), (Brush)FindResource("BoxCard"), (Brush)FindResource("InputBg"),
                                        (Brush)FindResource("AccentGrad"), new SolidColorBrush(Color.FromArgb(0x55, 0x7C, 0x5C, 0xFF)));
                var view = portalChat = new IvoChatsView(ivoChats, look, ChatAsk, null);   // that conversation first; back goes to all your chats, call or not
                view.OpenThread(thread);
                return view;
            }
        }
        var page = new DockPanel();
        var input = new TextBox { FontSize = 14 };
        var send = new Button { Content = "Send", Style = (Style)FindResource("Shimmer"), Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        var ask = PortalChip("Ask Ivo", () => { }, tip: "Your Ivo answers, and you both see it"); ask.Margin = new Thickness(8, 0, 0, 0); ask.VerticalAlignment = VerticalAlignment.Center;
        void Post(bool toIvo)
        {
            var text = input.Text.Trim(); if (text == "") return;
            input.Clear();
            var reply = portalReplying; portalReplying = null;
            PortalSay(PortalMe, text, ivo: false, mine: true, replyFrom: reply?.ReplyFrom, replyText: reply?.ReplyText);
            if (reply != null) OpenPortal();
            if (toIvo) _ = PortalAsk(text);
        }
        send.Click += (_, _) => Post(false);
        ask.Click += (_, _) => Post(true);
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Post(Keyboard.Modifiers == ModifierKeys.Control); } };
        var row = new DockPanel();
        DockPanel.SetDock(ask, Dock.Right); DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(ask); row.Children.Add(send); row.Children.Add(Hinted(input, $"Message {portal!.PeerName}"));
        DockPanel.SetDock(row, Dock.Bottom); page.Children.Add(row);
        if (portalReplying is { } to)
        {
            var cancel = IvoChatsView.Round("", "Cancel reply", "#A78BFA", () => { portalReplying = null; OpenPortal(); }, App.IconFont);
            DockPanel.SetDock(cancel, Dock.Right);
            var quote = new StackPanel { Children = { Say($"Replying to {to.ReplyFrom}", 11.5, "Fg", FontWeights.SemiBold), Say(IvoChatsView.WrapLongWords(to.ReplyText ?? ""), 12, "Dim") } };
            var replyBar = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), Child = new DockPanel { Children = { cancel, quote } } };
            DockPanel.SetDock(replyBar, Dock.Bottom); page.Children.Add(replyBar);
        }

        var list = portalList = new StackPanel();
        portalLists.Add(list); list.Unloaded += (_, _) => portalLists.Remove(list);   // the call view and the panel each keep their own: every one gets each line
        if (portalLog.Count == 0) { var hi = Say($"Say hi to {portal.PeerName}. Ctrl+Enter asks Ivo, and you both see the answer.", 13); hi.Margin = new Thickness(0, 4, 0, 4); portalList.Children.Add(hi); }
        foreach (var m in portalLog) portalList.Children.Add(PortalBubble(m));
        portalScroll = new ScrollViewer { Content = portalList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 10, 0, 12) };
        page.Children.Add(portalScroll);
        Dispatcher.BeginInvoke(() => { input.Focus(); portalScroll?.ScrollToEnd(); }, System.Windows.Threading.DispatcherPriority.Input);
        return page;
    }

    FrameworkElement PortalBubble((string Who, string Text, bool Ivo, bool Mine, string? ReplyFrom, string? ReplyText, long At) m)
    {
        var name = Say(m.Ivo ? $"Ivo · for {m.Who}" : m.Who, 11.5, m.Ivo ? "Fg" : "Dim", FontWeights.SemiBold);
        if (m.Ivo) name.Foreground = Shell.B("#A78BFA");
        var content = new StackPanel { Children = { name } };
        var sent = DateTimeOffset.FromUnixTimeSeconds(m.At).LocalDateTime;
        var stamp = Say(sent.Date == DateTime.Today ? sent.ToString("t") : sent.ToString("d MMM, t"), 11, "Dim");
        stamp.Margin = new Thickness(0, 0, 0, 4); stamp.Visibility = Visibility.Collapsed;
        content.Children.Add(stamp);
        if (m.ReplyText is { Length: > 0 } replyText)
        {
            var quote = new StackPanel { Children = { Say($"Reply to {m.ReplyFrom}", 11.5, "Fg", FontWeights.SemiBold), Say(IvoChatsView.WrapLongWords(replyText), 12, "Dim") } };
            content.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 0, 0, 6),
                                              BorderBrush = Shell.B("#A78BFA"), BorderThickness = new Thickness(3, 0, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Child = quote });
        }
        content.Children.Add(Say(IvoChatsView.WrapLongWords(m.Text), 14, "Fg"));
        var body = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 9, 14, 10), Margin = new Thickness(0, 0, 0, 8), MaxWidth = 440, Child = content };
        if (m.Mine && !m.Ivo) body.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x7C, 0x5C, 0xFF)); else body.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        actions.Children.Add(IvoChatsView.Round("", "Reply", "#A78BFA", () =>
        {
            portalReplying = m with { ReplyFrom = m.Ivo ? $"Ivo · for {m.Who}" : m.Who, ReplyText = m.Text.Length > 90 ? m.Text[..90] + "…" : m.Text };
            OpenPortal();
        }, App.IconFont));
        actions.Children.Add(IvoChatsView.Round("", "Copy", "#A78BFA", () => Clipboard.SetText(m.Text), App.IconFont));
        var row = new Grid { Margin = new Thickness(m.Mine ? 40 : 0, 0, m.Mine ? 0 : 40, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = m.Mine ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = m.Mine ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Grid.SetColumn(actions, m.Mine ? 0 : 1);
        Grid.SetColumn(body, m.Mine ? 1 : 0);
        actions.HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        body.HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        row.Children.Add(actions);
        row.Children.Add(body);
        row.MouseEnter += (_, _) => { actions.Opacity = 1; stamp.Visibility = Visibility.Visible; };
        row.MouseLeave += (_, _) => { actions.Opacity = 0; stamp.Visibility = Visibility.Collapsed; };
        return row;
    }

    readonly List<StackPanel> portalLists = [];
    void ShowLine((string Who, string Text, bool Ivo, bool Mine, string? ReplyFrom, string? ReplyText, long At) m)
    {
        foreach (var l in portalLists.ToList())
        {
            if (portalLog.Count == 1) l.Children.Clear();   // the "say hi" goes
            l.Children.Add(PortalBubble(m));
            (l.Parent as ScrollViewer)?.ScrollToEnd();
        }
    }

    // a line in the session: shown here, sent there
    void PortalSay(string who, string text, bool ivo, bool mine, string? replyFrom = null, string? replyText = null, long? at = null)
    {
        var sentAt = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        portalLog.Add((who, text, ivo, mine, replyFrom, replyText, sentAt));
        ShowLine(portalLog[^1]);
        if (mine) portal?.Send(new JsonObject { ["t"] = "chat", ["n"] = who, ["x"] = text, ["ivo"] = ivo, ["rf"] = replyFrom, ["rt"] = replyText, ["at"] = sentAt });
    }

    // your own Ivo answers (your model, your key), and the answer is shared
    async Task PortalAsk(string question)
    {
        var c = NewChat(); c.Space = "chat"; c.Workspace = null; c.Incognito = true;
        c.System = $"You're Ivo, answering in a shared chat between {PortalMe} and {portal?.PeerName}. Both read your answer. Be short and clear; Markdown is shown as plain text.";
        var context = string.Join("\n", portalLog.TakeLast(12).Select(m => $"{(m.Ivo ? "Ivo" : m.Who)}: {m.Text}"));
        c.Messages.Add(new("user", $"The conversation so far:\n{context}\n\n{PortalMe} asks you: {question}"));
        var cfg = c.Provider == Router.Name && S.AutoOn ? Router.Cfg : S.Providers.FirstOrDefault(p => p.Name == c.Provider);
        string answer;
        try
        {
            if (cfg == null) throw new InvalidOperationException("Pick a model in Ivo first.");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var r = cfg.Kind == "cli" ? await Cli.Run(cfg, c, _ => { }, _ => { }, cts.Token) : await WithWeb(cfg, c, cts.Token);
            answer = r.Text.Trim();
        }
        catch (Exception ex) { answer = "I couldn't answer: " + ex.Message.Split('\n')[0]; }
        if (portal?.Open == true) PortalSay(PortalMe, answer, ivo: true, mine: true);
    }

    void PortalReceived(JsonObject o)
    {
        switch ((string?)o["t"])
        {
            case "merge":   // the person we're with is moving the call into a group call
                _ = JoinMerged((string?)o["g"] ?? "", (string?)o["call"] ?? "", (string?)o["v"] ?? "voice");
                break;
            case "hello":   // they said which app they use
                portalPeerApp = (string?)o["a"] ?? "";
                break;
            case "chat":
                var text = (string?)o["x"] ?? ""; var ivo = (bool?)o["ivo"] == true;
                portalLog.Add((portal!.PeerName, text.Length > 20000 ? text[..20000] : text, ivo, false, (string?)o["rf"], (string?)o["rt"], MessageTimestamp(o)));
                ShowLine(portalLog[^1]);
                if (panelKey != "portal") NotifyCard.Show(ivo ? $"Ivo · for {portal.PeerName}" : portal.PeerName, text.Length > 90 ? text[..90] + "…" : text, "", "#A78BFA", OpenPortal);
                break;
            case "play" when portalListen:
                _ = PortalPlay((string?)o["uri"], (long?)o["ms"] ?? 0, (bool?)o["on"] == true);
                break;
            case "contact" or "contact-ok" or "contact-no":
                ContactMessage(o);
                break;
            case "ring" or "answer" or "hangup" or "video":
                CallMessage(o);
                break;
            case "bye":
                NotifyCard.Show("Portal ended", $"{portal?.PeerName} left", "", "#A78BFA", OpenPortal);
                EndPortal(quiet: true);
                if (panelKey == "portal") OpenPortal();
                break;
        }
    }

    static long MessageTimestamp(JsonObject o)
    {
        return o["at"] is JsonValue value && value.TryGetValue<long>(out var at) && at is >= -62135596800 and <= 253402300799
            ? at : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    // ---------- Listen together ----------
    string? portalSentUri; bool portalSentOn; DateTime portalSentAt;

    // what this side is playing, sent when the song changes, it's paused or played, or now and then to stay in step
    void PortalMusic(JsonNode? p, bool force = false)
    {
        if (!portalListen || portal?.Open != true || p?["item"]?["uri"] is not { } u) return;
        var uri = (string?)u; var on = (bool?)p["is_playing"] == true;
        if (!force && uri == portalSentUri && on == portalSentOn && (DateTime.Now - portalSentAt).TotalSeconds < 30) return;
        portalSentUri = uri; portalSentOn = on; portalSentAt = DateTime.Now;
        portal.Send(new JsonObject { ["t"] = "play", ["uri"] = uri, ["ms"] = (long?)p["progress_ms"] ?? 0, ["on"] = on });
    }

    async Task PortalPlay(string? uri, long ms, bool on)
    {
        if (uri == null || !SpotifyOn) return;
        var mine = nowPlaying?["item"]?["uri"] is { } m ? (string?)m : null;
        var at = (long?)nowPlaying?["progress_ms"] ?? 0;
        if (!on) { if ((bool?)nowPlaying?["is_playing"] == true) await SpotifyApi.Send("PUT", "/me/player/pause", control: true); return; }
        if (mine == uri && Math.Abs(at - ms) < 4000 && (bool?)nowPlaying?["is_playing"] == true) return;   // already together
        portalSentUri = uri; portalSentOn = true; portalSentAt = DateTime.Now;   // following, not leading: don't echo it back
        await SpotifyApi.Send("PUT", "/me/player/play", new JsonObject { ["uris"] = new JsonArray(uri), ["position_ms"] = ms + 600 }, control: true);
    }
}
