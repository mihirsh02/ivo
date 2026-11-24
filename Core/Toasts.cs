using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Ivo;

// Windows' notification area, for Ivo and Ivo Portal alike: posted quietly beside the app's own card (NotifyCard).
// A person's picture leads it, cropped round; clicking it runs what it's about (while the app is open). Focus / Do not
// disturb and Windows' per-app settings apply. Returns false if Windows wouldn't take it.
public static class Toasts
{
    static readonly Dictionary<string, Action> clicks = [];
    static bool hooked;
    static readonly string Pics = Path.Combine(Path.GetTempPath(), "Ivo", "toast-pictures");

    public static bool Show(string title, string body, Action? click, string? pic = null, string? group = null, bool quiet = false)
    {
        try
        {
            if (!hooked)
            {
                hooked = true;
                // a click on one of ours: back on the app's thread, run its action
                ToastNotificationManagerCompat.OnActivated += e =>
                {
                    var args = ToastArguments.Parse(e.Argument);
                    if (args.TryGetValue("id", out var id) && clicks.TryGetValue(id, out var go))
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(go);
                };
            }
            var key = Guid.NewGuid().ToString("N")[..10];
            if (click != null) { clicks[key] = click; if (clicks.Count > 50) clicks.Remove(clicks.Keys.First()); }
            var t = new ToastContentBuilder().AddArgument("id", key).AddText(title).AddText(body.Length > 200 ? body[..200] + "…" : body);
            if (pic is { Length: > 0 })
            {
                // the picture as a file Windows can read (base64 in, a small file out), cropped round
                Directory.CreateDirectory(Pics);
                var file = Path.Combine(Pics, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Convert.FromBase64String(pic)))[..16] + ".jpg");
                if (!File.Exists(file)) File.WriteAllBytes(file, Convert.FromBase64String(pic));
                t.AddAppLogoOverride(new Uri(file), ToastGenericAppLogoCrop.Circle);
            }
            t.Show(toast => { if (group != null) toast.Group = group; toast.SuppressPopup = quiet; });   // quiet: into the notification area only, no banner
            return true;
        }
        catch { return false; }
    }
}
