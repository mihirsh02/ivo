using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Ivo;

// Plugins: folders in <data>/plugins, each with a plugin.json. A plugin adds tools (a command Ivo runs, given the
// arguments as JSON on standard input, answering on standard output) and, optionally, a panel (an HTML page Ivo shows
// in its own window). No compiling and no access to Ivo's internals: anything that can read stdin works (a .ps1, .py, .exe).
//
// {
//   "name": "Weather", "description": "Forecasts from my station",
//   "tools": [{ "name": "station_now", "description": "Current readings", "command": "python station.py",
//               "schema": { "type": "object", "properties": {} }, "readOnly": true }],
//   "panel": "panel.html"
// }
public static class Plugins
{
    public record PluginTool(string Plugin, string Dir, string Command, ToolDef Def);
    public record Plugin(string Name, string Description, string Dir, string? Panel, List<PluginTool> Tools, string? Error);

    public static string Dir => Path.Combine(Store.Dir, "plugins");

    public static List<Plugin> All()
    {
        var list = new List<Plugin>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var d in Directory.GetDirectories(Dir))
        {
            var file = Path.Combine(d, "plugin.json");
            if (!File.Exists(file)) continue;
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(file))!;
                var name = (string?)j["name"] ?? Path.GetFileName(d);
                var tools = (j["tools"]?.AsArray() ?? []).Select(t => new PluginTool(name, d, (string?)t!["command"] ?? "",
                    new ToolDef("plugin_" + System.Text.RegularExpressions.Regex.Replace((string?)t["name"] ?? "tool", "[^A-Za-z0-9_]", "_"),
                                $"[{name} plugin] {(string?)t["description"] ?? ""}",
                                t["schema"]?.ToJsonString() ?? """{"type":"object","properties":{}}""",
                                (bool?)t["readOnly"] ?? false))).Where(t => t.Command != "").ToList();
                var panel = (string?)j["panel"] is { } p && File.Exists(Path.Combine(d, p)) ? Path.Combine(d, p) : null;
                list.Add(new(name, (string?)j["description"] ?? "", d, panel, tools, null));
            }
            catch (Exception ex) { list.Add(new(Path.GetFileName(d), "", d, null, [], "plugin.json: " + ex.Message)); }
        }
        return list;
    }

    public static IEnumerable<ToolDef> Tools => All().SelectMany(p => p.Tools).Select(t => t.Def);
    public static PluginTool? Find(string name) => All().SelectMany(p => p.Tools).FirstOrDefault(t => t.Def.Name == name);

    // runs the tool's command in its folder: arguments in on stdin, the answer out on stdout (2 minutes at most)
    public static async Task<string> Call(PluginTool t, JsonNode args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + t.Command)
        {
            WorkingDirectory = t.Dir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + t.Command);
        await p.StandardInput.WriteAsync(args.ToJsonString());
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var errors = p.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } if (ct.IsCancellationRequested) throw; return "Error: the plugin took longer than 2 minutes."; }
        var o = await output; var e = await errors;
        return p.ExitCode == 0 ? (o.Trim() == "" ? "(no output)" : o) : $"Error: exit code {p.ExitCode}\n{e}{o}";
    }
}
