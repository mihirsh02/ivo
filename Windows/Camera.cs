using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Ivo;

// Take a photo in Ivo: a small window with the webcam's live preview (Assets/cam/camera.html), returning a JPEG.
public static class Camera
{
    public static Task<Img?> Capture(Window owner)
    {
        var done = new TaskCompletionSource<Img?>();
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 11, 16) };
        var win = new Window { Title = "Take a photo", Width = 760, Height = 560, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                               Content = view, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 11, 16)), ShowInTaskbar = false };
        win.Closed += (_, _) => { done.TrySetResult(null); view.Dispose(); };
        win.Loaded += async (_, _) =>
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Dir, "web"));
            await view.EnsureCoreWebView2Async(env);
            var web = view.CoreWebView2;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.PermissionRequested += (_, a) => { if (a.PermissionKind == CoreWebView2PermissionKind.Camera) a.State = CoreWebView2PermissionState.Allow; };
            web.SetVirtualHostNameToFolderMapping("ivo.cam", Path.Combine(AppContext.BaseDirectory, "Assets", "cam"), CoreWebView2HostResourceAccessKind.Allow);
            web.WebMessageReceived += (_, a) =>
            {
                var url = a.TryGetWebMessageAsString();
                var comma = url.IndexOf(',');
                done.TrySetResult(comma > 0 ? new Img("image/jpeg", url[(comma + 1)..]) : null);
                win.Close();
            };
            web.Navigate("https://ivo.cam/camera.html");
        };
        IvoFrame.Apply(win, web: true);
        win.Show();
        return done.Task;
    }
}
