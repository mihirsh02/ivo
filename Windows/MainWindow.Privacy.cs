using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Ivo;

// Settings → Permissions → Device access: what Ivo, and websites opened inside it, may use. Switched off, Ivo won't
// open it (dictation, the camera) and web pages are refused without asking. On, web pages still ask first.
// Windows has the final say over the hardware itself, so each row also links to its page in Windows' privacy settings.
public partial class MainWindow
{
    static readonly (string Key, string Name, string Note, string? Windows, CoreWebView2PermissionKind[] Web)[] Devices =
    [
        ("microphone", "Microphone", "dictation, and websites", "ms-settings:privacy-microphone", [CoreWebView2PermissionKind.Microphone]),
        ("camera", "Camera", "photos for a chat, and websites", "ms-settings:privacy-webcam", [CoreWebView2PermissionKind.Camera]),
        ("location", "Location", "websites", "ms-settings:privacy-location", [CoreWebView2PermissionKind.Geolocation]),
        ("clipboard", "Reading the clipboard", "websites", null, [CoreWebView2PermissionKind.ClipboardRead]),
        ("notifications", "Notifications from websites", "websites", null, [CoreWebView2PermissionKind.Notifications]),
        ("files", "Files and folders", "websites opening your files", null, [CoreWebView2PermissionKind.FileReadWrite]),
        ("devices", "Sensors, MIDI and other hardware", "websites", null, [CoreWebView2PermissionKind.OtherSensors, CoreWebView2PermissionKind.MidiSystemExclusiveMessages]),
    ];

    // Before Ivo uses a device: false (with a note saying where to turn it on) when it's switched off.
    public bool Allowed(string device)
    {
        if (!S.Blocked.Contains(device)) return true;
        Offer();
        return false;
        async void Offer()
        {
            if (await Confirm($"{Devices.First(d => d.Key == device).Name} is off for Ivo", "It's switched off in Settings → Permissions.", "Open Permissions")) OpenSettings("privacy");
        }
    }

    // Every web view Ivo shows: switched-off devices are refused; the rest go to the page's usual prompt.
    public void Guard(CoreWebView2 web) => web.PermissionRequested += (_, a) =>
    {
        if (Devices.FirstOrDefault(d => d.Web.Contains(a.PermissionKind)) is { Key: { } key } && S.Blocked.Contains(key)) a.State = CoreWebView2PermissionState.Deny;
    };

    void ShowPrivacy()
    {
        PrivacyList.Children.Clear();
        foreach (var d in Devices)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            label.Inlines.Add(d.Name + " ");
            var note = new System.Windows.Documents.Run("· " + d.Note); note.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Dim");
            label.Inlines.Add(note);
            var box = new CheckBox { Content = label, IsChecked = !S.Blocked.Contains(d.Key), VerticalAlignment = VerticalAlignment.Center };
            box.Click += (_, _) => { S.Blocked.Remove(d.Key); if (box.IsChecked != true) S.Blocked.Add(d.Key); SaveSettings(); };
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            if (d.Windows is { } uri)
            {
                var link = new Button { Content = "Windows settings", Style = (Style)FindResource("Ghost"), FontSize = 14, ToolTip = "Whether any app may use it, in Windows' own privacy settings" };
                link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { } };
                DockPanel.SetDock(link, Dock.Right);
                row.Children.Add(link);
            }
            row.Children.Add(box);
            PrivacyList.Children.Add(row);
        }
    }
}
