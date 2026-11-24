using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Ivo;

// Someone calling: the whole window blurs behind them (Core/IncomingCall.cs), with Accept, Decline and Ignore.
// Ignore just quiets it here; the call still waits in Portal until they give up.
public partial class MainWindow
{
    // the call card on the desktop only when Ivo isn't in front (closed to the tray, minimised or behind another app): in front, its own call screen says it
    bool CallAway => !IsActive || WindowState == WindowState.Minimized || !IsVisible;
    // Ignored: no ring, no screen; the call sits in the title bar's pill until it's answered, declined or gone
    bool callIgnored;
    Action? showAgain;

    void ShowIncoming(string who, string kind, Action accept, Action decline, string? pic = null)
    {
        HideIncoming();
        showAgain = () => { callIgnored = false; ShowIncoming(who, kind, accept, decline, pic); UpdatePortalPill(); };
        if (!IsVisible || WindowState == WindowState.Minimized) return;   // the card and the ring say it; the window isn't there to cover
        removeIncoming = IncomingCall.Show(Root, Root, who, kind, pic, accept, decline, () => { Sounds.StopRing(); callIgnored = true; UpdatePortalPill(); });
    }
    Action? removeIncoming;

    // the call is over one way or another: the screen and the pill go
    void HideIncoming()
    {
        RemoveIncoming();
        if (callIgnored) { callIgnored = false; showAgain = null; UpdatePortalPill(); }
    }
    void RemoveIncoming() { removeIncoming?.Invoke(); removeIncoming = null; }
}
