using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Group calls (see Core/GroupCall.cs): start one from a group's call buttons in Chats; when someone starts one in a group
// you're in, Ivo rings with Accept, Decline and Ignore. The call fills Ivo's window like a one-to-one call, with People
// and the group's chat beside it.
public partial class MainWindow
{
    GroupCall? groupCall;
    GroupCallView? groupView;
    Grid? groupLayer;

    // the group's signals reach here; a ring for a call we're not in rings
    void GroupSignal(ChatThread t, JsonObject o)
    {
        if ((string?)o["k"] != "ring" || groupCall != null) return;
        var id = (string?)o["call"] ?? ""; var kind = (string?)o["v"] ?? "voice";
        if (merging.Contains(id)) return;   // a merged call we're already joining
        var who = t.Members.FirstOrDefault(m => m.Id == (string?)o["from"])?.Name ?? "Someone";
        Sounds.Ring();
        if (CallAway) NotifyCard.Show($"{who} started a call in {t.Name}", kind == "video" ? "Group video call" : "Group call", "", "#A78BFA", () => { ShowWindow(); });
        ShowIncoming($"{who} · {t.Name}", kind, () => _ = EnterGroupCall(t, id, kind, start: false), () => Sounds.StopRing());
    }

    async Task EnterGroupCall(ChatThread t, string id, string kind, bool start)
    {
        Sounds.StopRing(); HideIncoming();
        if (groupCall != null || portalCall == "on") { _ = Notice("You're already in a call", "Leave it first, then join this one."); return; }
        if (ivoChats == null) return;
        groupCall = new GroupCall(ivoChats, t, id, kind, PortalMe);
        groupCall.Stopped += why => Dispatcher.BeginInvoke(() => _ = Notice("Screen sharing stopped", why + ". Share it again once it's open."));
        ShowGroupCall();
        try
        {
            if (start) await groupCall.Start(); else await groupCall.Join();
            if (kind == "video") await GroupVideo("camera");
        }
        catch (Exception ex) { _ = Notice("The call couldn't start", ex.Message); LeaveGroupCall(); }
    }

    void ShowGroupCall()
    {
        if (groupCall == null) return;
        if (groupLayer != null) { groupLayer.Visibility = Visibility.Visible; UpdatePortalPill(); return; }
        ClosePanel();
        var look = new ChatLook((Brush)FindResource("Fg"), (Brush)FindResource("Dim"), (Brush)FindResource("BoxCard"), (Brush)FindResource("InputBg"),
                                (Brush)FindResource("AccentGrad"), new SolidColorBrush(Color.FromArgb(0x55, 0x7C, 0x5C, 0xFF)));
        var chat = new IvoChatsView(ivoChats!, look, ChatAsk, null);
        chat.OpenThread(groupCall.Thread);
        var fullOn = false;
        groupView = new GroupCallView(groupCall, PortalMe, GroupVideo, LeaveGroupCall,
            () => { fullOn = !fullOn; FullScreen(fullOn); groupLayer!.Margin = new Thickness(fullOn ? 0 : 14, fullOn ? 0 : 56, fullOn ? 0 : 14, fullOn ? 0 : 14); groupView?.FullScreenShown(fullOn); },
            () => { groupLayer!.Visibility = Visibility.Collapsed; UpdatePortalPill(); }, chat);
        groupView.AddPeopleButton(PickPeople);
        groupView.Ticked += UpdatePortalPill;
        groupLayer = new Grid { Margin = new Thickness(14, 56, 14, 14), Children = { new Border { CornerRadius = new CornerRadius(18), ClipToBounds = true, Child = groupView } } };
        Grid.SetRowSpan(groupLayer, 99); Grid.SetColumnSpan(groupLayer, 99); Panel.SetZIndex(groupLayer, 950);
        Root.Children.Add(groupLayer);
    }

    // the camera, or a screen picked here, for everyone in the call
    async Task GroupVideo(string what)
    {
        if (groupCall == null) return;
        try
        {
            if (what == "screen")
            {
                var pick = new ScreenPicker { Owner = this }; pick.ShowDialog();
                if (pick.Picked is { } chosen) await groupCall.Video("screen", chosen);
            }
            else await groupCall.Video(what);
        }
        catch (Exception ex) { _ = Notice(what == "camera" ? "The camera couldn't start" : "Couldn't share the screen", ex.Message); }
    }

    void LeaveGroupCall()
    {
        groupView?.Detach(); groupView = null;
        if (groupLayer != null) { Root.Children.Remove(groupLayer); groupLayer = null; }
        groupCall?.Leave(); groupCall = null;
        FullScreen(false); UpdatePortalPill();
    }
}
