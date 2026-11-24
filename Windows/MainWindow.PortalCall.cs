using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ivo;

// Portal calls in Ivo: ring, accept or decline, then a call with mute, camera, screen share and hang up.
// The other side can be Ivo or the standalone Portal app: they speak the same messages.
public partial class MainWindow
{
    string portalCall = "";          // "" · calling (we rang) · ringing (they rang) · on
    string portalCallKind = "voice"; // voice · video · screen: what the one who rang wanted to start with
    WriteableBitmap? theirVideo, myVideo;
    bool theirPending, myPending;

    // the row under "Connected with …": call buttons, an incoming call, or the call's own controls
    FrameworkElement CallBar()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button B(string text, string style, Action click, string? tip = null)
        {
            var b = new Button { Content = text, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 8, 0), ToolTip = tip, MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => click();
            row.Children.Add(b);
            return b;
        }
        switch (portalCall)
        {
            case "":
                B("Call", "Ghost", () => Ring("voice"), "A voice call");
                B("Video call", "Ghost", () => Ring("video"), "With your camera");
                B("Share screen", "Ghost", () => Ring("screen"), "They see your main screen");
                break;
            case "calling":
                row.Children.Add(new StackPanel { Width = 9999, Children = { Say($"Ringing {portal!.PeerName}… waiting for them to pick up", 14, "Fg") } });
                B("Cancel", "Danger", () => HangUp(tell: true)).Margin = new Thickness(0, 10, 8, 0);
                break;
            case "ringing":
                row.Children.Add(new StackPanel { Width = 9999, Children = { Say($"{portal!.PeerName} is calling{(portalCallKind == "video" ? " (video)" : portalCallKind == "screen" ? " to share their screen" : "")}", 14, "Fg") } });
                B("Accept", "Shimmer", () => ShowCallWindow(true)).Margin = new Thickness(0, 10, 8, 0);
                B("Decline", "Danger", () => HangUp(tell: true));
                break;
            case "on":
                B("Show call", "Shimmer", () => ShowCallWindow(), "The call's own window");
                B("Hang up", "Danger", () => HangUp(tell: true));
                break;
        }
        return row;
    }

    void Ring(string kind)
    {
        portalCall = "calling"; portalCallKind = kind;
        portal?.Send(new JsonObject { ["t"] = "ring", ["v"] = kind });
        Sounds.Calling();
        OpenPortal();
    }

    async Task AcceptCall()
    {
        Sounds.StopRing(); HideIncoming();
        portal?.Send(new JsonObject { ["t"] = "answer" });
        await CallOn();
    }

    async Task CallOn()
    {
        Sounds.StopCalling();
        if (portal == null) return;
        portalCall = "on";
        try
        {
            await portal.StartSound();
            // the one who rang for video or a screen starts sending it; the other answers with sound and can add their camera
            if (portalCallKind != "voice" && portalCallWeRang) { if (portalCallKind is "camera" or "video") await portal.StartCamera(); else await Video("screen"); }
        }
        catch (Exception ex) { _ = Notice("The call couldn't start", ex.Message); }
        OpenPortal();
        ShowCallWindow();
    }
    bool portalCallWeRang;

    async Task Video(string what)
    {
        if (portal == null) return;
        try
        {
            if (what == "camera") await portal.StartCamera();
            else if (what == "screen") { var pick = new ScreenPicker { Owner = this }; pick.ShowDialog(); if (pick.Picked is not { } chosen) return; await portal.StartScreen(chosen); }   // a whole screen or one app
            else if (what == "camera-off") await portal.StopCamera();
            else if (what == "screen-off") await portal.StopScreen();
            else { await portal.StopVideo(); myVideo = null; }
            if (portal.Sending == "") myVideo = null;
        }
        catch (Exception ex) { _ = Notice(what == "camera" ? "The camera couldn't start" : "Couldn't share the screen", ex.Message); }
        portal.Send(new JsonObject { ["t"] = "video", ["on"] = portal.Sending != "" });
        OpenPortal();
    }

    void HangUp(bool tell)
    {
        Sounds.StopRing(); Sounds.StopCalling(); HideIncoming();
        if (tell) portal?.Send(new JsonObject { ["t"] = "hangup" });
        portalCall = ""; portalCallWeRang = false; theirVideo = myVideo = null;
        CloseCall();
        _ = Finish();
        // a call with a contact was the session's only reason: it ends with the call (an invite's portal stays open)
        async Task Finish()
        {
            if (portal != null) await portal.EndCall();
            // a call with a contact was the session's only reason; hung up while it still rang them, it's called off
            if (portalWith != null && portalStage is "open" or "calling" or "inviting") EndPortal(quiet: portalStage != "open");
            if (panelKey == "portal") OpenPortal();
        }
    }

    // the call's messages, from PortalReceived
    void CallMessage(JsonObject o)
    {
        switch ((string?)o["t"])
        {
            case "ring":
                portalCall = "ringing"; portalCallKind = (string?)o["v"] ?? "voice"; portalCallWeRang = false;
                // We accepted the Portal request: no second ring; accept the call at once.
                if (portalAnswerKind != "") { portalAnswerKind = ""; _ = AcceptCall(); break; }
                Sounds.Ring();
                ShowIncoming(portal!.PeerName, portalCallKind, () => _ = AcceptCall(), () => HangUp(tell: true));
                if (CallAway) NotifyCard.Show($"{portal!.PeerName} is calling", portalCallKind == "voice" ? "Portal · voice call" : portalCallKind == "video" ? "Portal · video call" : "Portal · wants to share their screen", "", "#A78BFA", OpenPortal);
                if (panelKey == "portal") OpenPortal();
                break;
            case "answer" when portalCall == "calling":
                portalCallWeRang = true;
                _ = CallOn();
                break;
            case "hangup":
                if (portalCall == "ringing") NotifyCard.Show("Missed call", $"{portal?.PeerName} called", "", "#A78BFA", OpenPortal);
                HangUp(tell: false);
                break;
            case "video":
                if ((bool?)o["on"] != true) { theirVideo = null; callView?.TheirVideoStopped(); callFloat?.TheirVideoStopped(); if (panelKey == "portal") OpenPortal(); }
                break;
        }
    }

    // frames arrive on the network's threads; each one is drawn on the window's, skipping any that come while one is pending
    void HookVideo(Portal k)
    {
        k.TheirFrame += (bgr, w, h) =>
        {
            if (theirPending) return; theirPending = true;
            Dispatcher.BeginInvoke(() => { theirPending = false; var fresh = theirVideo == null; theirVideo = Paint(theirVideo, bgr, w, h); if (fresh && panelKey == "portal") OpenPortal(); });
        };
        k.MyFrame += (bgr, w, h) =>
        {
            if (myPending) return; myPending = true;
            Dispatcher.BeginInvoke(() => { myPending = false; var fresh = myVideo == null; myVideo = Paint(myVideo, bgr, w, h); if (fresh && panelKey == "portal") OpenPortal(); });
        };
    }

    static WriteableBitmap Paint(WriteableBitmap? bmp, byte[] bgr, int w, int h)
    {
        if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h) bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), bgr, w * 3, 0);
        return bmp;
    }
}
