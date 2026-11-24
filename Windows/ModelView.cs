using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Ivo;

// Ivo in 3D: the viewer page (Assets/viewer/viewer.html, three.js bundled) in a web view, with a folder of models served
// to it. Load a model (GLB/glTF or FBX), set her moving (hop, bounce, spin, sway), play a model's own animations, and take
// a picture from any angle (for thumbnails).
public sealed class ModelView : Grid
{
    readonly WebView2 web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    readonly string folder;
    readonly bool dark;
    readonly System.Windows.Media.Color? edge;
    TaskCompletionSource ready = new();
    TaskCompletionSource<JsonObject?>? loading;

    public event Action<JsonObject>? Loaded2;   // what the viewer saw: name, meshes, triangles, textures, animations…

    // edge: the colour around the stage, for its rounded corners (a web view can't be clipped round)
    public ModelView(string modelsFolder, bool darkStage = true, System.Windows.Media.Color? edge = null)
    {
        folder = modelsFolder; dark = darkStage; this.edge = edge;
        Children.Add(web);
        Loaded += async (_, _) => await Start();
    }

    async Task Start()
    {
        if (web.CoreWebView2 != null) return;
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Dir, "web"));
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 did not initialize.");
        core.SetVirtualHostNameToFolderMapping("ivo.viewer", Path.Combine(AppContext.BaseDirectory, "Assets", "viewer"), CoreWebView2HostResourceAccessKind.Allow);
        Directory.CreateDirectory(folder);
        core.SetVirtualHostNameToFolderMapping("ivo.models", folder, CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
        core.WebMessageReceived += (_, e) =>
        {
            if (JsonNode.Parse(e.WebMessageAsJson) is not JsonObject m) return;
            switch ((string?)m["t"])
            {
                case "ready": ready.TrySetResult(); break;
                case "loaded": loading?.TrySetResult(m); Loaded2?.Invoke(m); break;
                case "error": loading?.TrySetResult(m); break;
                case "snap": if ((int?)m["id"] is { } id && snaps.TryGetValue(id, out var s)) s.TrySetResult((string?)m["url"]); break;
            }
        };
        var q = (dark ? "" : "&bg=clear") + (edge is { } e ? $"&edge=%23{e.R:x2}{e.G:x2}{e.B:x2}" : "");
        web.Source = new Uri("https://ivo.viewer/viewer.html" + (q == "" ? "" : "?" + q[1..]));
    }

    // a model by its path inside the folder ("pack/ivo-casual.glb"); what the viewer saw, or an error
    public async Task<JsonObject?> Load(string relative, string name)
    {
        await ready.Task;
        var current = loading = new();
        var url = "https://ivo.models/" + string.Join('/', relative.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "load", url, name }));
        var done = await Task.WhenAny(current.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        return done == current.Task ? current.Task.Result : null;
    }

    // done with it: the browser behind it goes, with all its memory
    public void Free() { Children.Clear(); web.Dispose(); }

    public void Motion(string kind) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "motion", kind }));
    public void Emote(string name) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "emote", name }));
    public void Play(string clip) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "play", name = clip }));

    // a square PNG of her from an angle (0 = front), sent back by the page as a message
    readonly Dictionary<int, TaskCompletionSource<string?>> snaps = [];
    int snapId;
    public async Task<byte[]?> Snapshot(int angle, int size = 512, double? at = null, string? emote = null)
    {
        await ready.Task;
        var id = ++snapId; var wait = snaps[id] = new();
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "snap", id, angle, size, at, emote }));
        var done = await Task.WhenAny(wait.Task, Task.Delay(10000));
        snaps.Remove(id);
        var url = done == wait.Task ? wait.Task.Result : null;
        return url != null && url.StartsWith("data:image/png;base64,") ? Convert.FromBase64String(url["data:image/png;base64,".Length..]) : null;
    }

    // Ivo.exe --render-models <folder> <out>: every model in the folder as pictures from four angles, plus what the
    // viewer saw of each (stats.json): for sorting models by pose, outfit and look
    public static async Task RenderAll(string models, string output)
    {
        Directory.CreateDirectory(output);
        var view = new ModelView(models);
        var win = new Window { Width = 640, Height = 640, Content = view, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow, Left = -2000, Top = -2000, ShowActivated = false };
        win.Show();
        var all = new JsonObject();
        foreach (var file in Directory.EnumerateFiles(models, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var rel = Path.GetRelativePath(models, file);
            var seen = await view.Load(rel, Path.GetFileName(file));
            all[rel] = seen?.DeepClone() ?? new JsonObject { ["t"] = "timeout" };
            if ((string?)seen?["t"] != "loaded") continue;
            await Task.Delay(400);
            var stem = Path.GetFileNameWithoutExtension(file) + (Path.GetExtension(file).ToLowerInvariant() == ".fbx" ? "-fbx" : "");
            foreach (var angle in new[] { 0, 45, 90, 180 })
                if (await view.Snapshot(angle) is { } png) await File.WriteAllBytesAsync(Path.Combine(output, $"{stem}-{angle}.png"), png);
            // a rigged model: each emote at three moments, from the front and from 45°
            if (seen?["emotes"] is JsonArray { Count: > 0 } emotes)
                foreach (var e in emotes.Select(x => (string?)x).OfType<string>())
                    foreach (var at in new[] { 0.6, 1.2 })
                        foreach (var angle in new[] { 0, 45 })
                            if (await view.Snapshot(angle, 384, at, e) is { } shot) await File.WriteAllBytesAsync(Path.Combine(output, $"{stem}-emote-{e}-{at:0.0}-{angle}.png"), shot);
            // an animated model: 12 moments through its first clip, from the front and the side
            if (seen?["durations"] is JsonArray { Count: > 0 } durations && (double?)durations[0] is > 0 and var length)
                for (var i = 0; i < 12; i++)
                    foreach (var angle in new[] { 0, 90 })
                        if (await view.Snapshot(angle, 384, length * i / 12) is { } frame) await File.WriteAllBytesAsync(Path.Combine(output, $"{stem}-anim{i:00}-{angle}.png"), frame);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "stats.json"), all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        win.Close();
    }
}
