using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Ivo;

// "Scan this PC": AI things already on this computer (Windows and WSL) that Ivo can use. Read-only; nothing leaves the PC
// except the local port checks. Other apps' sign-in tokens are deliberately not touched.
public static class Detect
{
    public record Server(string Name, string Url, int Models, string Where, string? Preset);
    public record EnvKey(string Var, string Preset, string Key, string Where);
    public record McpFound(string App, string Where, JsonObject Servers);
    public record SkillFound(string Name, string Dir, string Where);
    public record Result(List<Server> Servers, List<EnvKey> Keys, List<(string Name, string Where)> Clis, List<McpFound> Mcp, List<SkillFound> Skills, string? WslNote, string? WslDistro);

    // Usual ports of local model servers; all speak the OpenAI API at /v1.
    static readonly (int Port, string Name, string? Preset)[] Ports =
    [
        (11434, "Ollama", "Ollama (local)"), (1234, "LM Studio", "LM Studio (local)"), (4000, "LiteLLM", "LiteLLM (self-hosted)"),
        (20128, "OmniRoute", "OmniRoute (self-hosted)"), (8000, "vLLM", null), (8080, "llama.cpp / LocalAI", null), (1337, "Jan", null), (5000, "text-generation-webui", null),
    ];

    static readonly (string Var, string Preset)[] KeyVars =
    [
        ("OPENAI_API_KEY", "OpenAI"), ("ANTHROPIC_API_KEY", "Anthropic"), ("GEMINI_API_KEY", "Gemini"), ("GOOGLE_API_KEY", "Gemini"),
        ("OPENROUTER_API_KEY", "OpenRouter"), ("GROQ_API_KEY", "Groq"), ("MISTRAL_API_KEY", "Mistral"), ("DEEPSEEK_API_KEY", "DeepSeek"),
        ("XAI_API_KEY", "xAI (Grok)"), ("TOGETHER_API_KEY", "Together AI"), ("FIREWORKS_API_KEY", "Fireworks"), ("PERPLEXITY_API_KEY", "Perplexity"),
        ("PPLX_API_KEY", "Perplexity"), ("CEREBRAS_API_KEY", "Cerebras"), ("AI_GATEWAY_API_KEY", "Vercel AI Gateway"), ("REQUESTY_API_KEY", "Requesty"), ("AIML_API_KEY", "AI/ML API"),
    ];

    // Keep Scan this PC in sync with every CLI Ivo can run; the others are local-server tools checked on WSL.
    static readonly string[] Clis = Cli.Adapters.Select(a => a.Command)
        .Concat(["ollama", "lms", "litellm", "vllm"])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // MCP configs of other apps, relative to the home folder (Windows) — WSL uses the same ones under its own home.
    static readonly (string App, string Path, string Key)[] McpConfigs =
    [
        ("Claude Code", ".claude.json", "mcpServers"), ("Cursor", ".cursor/mcp.json", "mcpServers"), ("Windsurf", ".codeium/windsurf/mcp_config.json", "mcpServers"),
    ];

    static readonly HttpClient Probe = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<Result> Scan()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var wsl = await Wsl();

        // local servers: this PC's ports (which also reach WSL by default), then WSL's own address when it has one
        var hosts = new List<(string Host, string Where)> { ("localhost", "Windows") };
        if (wsl?.Ip is { } ip) hosts.Add((ip, "WSL"));
        var servers = (await Task.WhenAll(hosts.SelectMany(h => Ports.Select(async p =>
        {
            var url = $"http://{h.Host}:{p.Port}/v1";
            try
            {
                var j = JsonNode.Parse(await Probe.GetStringAsync(url + "/models"));
                return j?["data"] is JsonArray d ? new Server(p.Name, url, d.Count, h.Where, p.Preset) : null;
            }
            catch { return null; }
        })))).OfType<Server>().GroupBy(s => s.Name).Select(g => g.First()).ToList();   // the same server seen both ways counts once

        // API keys in environment variables
        var keys = new List<EnvKey>();
        foreach (var (v, preset) in KeyVars)
        {
            var win = Environment.GetEnvironmentVariable(v) ?? Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(win)) keys.Add(new(v, preset, win.Trim(), "Windows"));
            if (wsl?.Env.GetValueOrDefault(v) is { Length: > 0 } lx && lx != win?.Trim()) keys.Add(new(v, preset, lx, "WSL"));
        }

        // command-line tools
        var here = await Task.WhenAll(Clis.Select(c => Task.Run(async () => Cli.Find(c) is { } a ? await Cli.Installed(a) : OnPath(c))));   // checked at once, off the UI thread
        var clis = Clis.Where((_, i) => here[i]).Select(c => (c, "Windows")).Concat((wsl?.Clis ?? []).Select(c => (c, "WSL"))).ToList();

        // MCP servers other apps already use
        var mcp = new List<McpFound>();
        void AddMcp(string app, string where, string? json, string key, bool inWsl)
        {
            if (json == null) return;
            JsonNode? root; try { root = JsonNode.Parse(json); } catch { return; }
            var found = new JsonObject();
            foreach (var (name, cfg) in root?[key] as JsonObject ?? []) if (cfg is JsonObject o) found[name] = inWsl ? ToWsl(o) : o.DeepClone();
            if (app == "Claude Code")   // project-level servers too
                foreach (var (_, proj) in root?["projects"] as JsonObject ?? [])
                    foreach (var (name, cfg) in proj?["mcpServers"] as JsonObject ?? [])
                        if (cfg is JsonObject o && !found.ContainsKey(name)) found[name] = inWsl ? ToWsl(o) : o.DeepClone();
            if (found.Count > 0) mcp.Add(new(app, where, found));
        }
        AddMcp("Claude Desktop", "Windows", ReadOrNull(Path.Combine(appData, "Claude", "claude_desktop_config.json")), "mcpServers", false);
        AddMcp("VS Code", "Windows", ReadOrNull(Path.Combine(appData, "Code", "User", "mcp.json")), "servers", false);
        foreach (var (app, rel, key) in McpConfigs)
        {
            AddMcp(app, "Windows", ReadOrNull(Path.Combine(home, rel)), key, false);
            AddMcp(app, "WSL", wsl?.Files.GetValueOrDefault(rel), key, true);
        }

        // Claude skills
        var skills = new List<SkillFound>();
        void AddSkills(string dir, string where)
        {
            try { foreach (var d in Directory.GetDirectories(dir)) if (File.Exists(Path.Combine(d, "SKILL.md"))) skills.Add(new(Path.GetFileName(d), d, where)); } catch { }
        }
        AddSkills(Path.Combine(home, ".claude", "skills"), "Windows");
        if (wsl?.HomeUnc is { } wh) AddSkills(Path.Combine(wh, ".claude", "skills"), "WSL");

        return new(servers, keys, clis, mcp, skills, wsl?.Note, wsl?.Distro);
    }

    static string? ReadOrNull(string path) { try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; } }

    static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(d => new[] { ".exe", ".cmd", ".bat", ".ps1" }.Any(e => File.Exists(Path.Combine(d.Trim(), name + e))));

    // A WSL program started from Windows: wsl.exe -e [env K=V …] command args…
    static JsonObject ToWsl(JsonObject o)
    {
        if (o["url"] != null) return (JsonObject)o.DeepClone();
        var args = new JsonArray("-e");
        if (o["env"] is JsonObject env && env.Count > 0) { args.Add("env"); foreach (var (k, v) in env) args.Add($"{k}={v}"); }
        args.Add((string?)o["command"] ?? "");
        foreach (var a in o["args"] as JsonArray ?? []) args.Add((string?)a ?? "");
        return new JsonObject { ["command"] = "wsl.exe", ["args"] = args };
    }

    // ---------- WSL: one shell run collects everything ----------
    record WslInfo(string? Ip, Dictionary<string, string> Env, List<string> Clis, Dictionary<string, string> Files, string? HomeUnc, string? Note, string? Distro = null);

    static async Task<WslInfo?> Wsl()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (!File.Exists(exe)) return null;
        var script = new StringBuilder();
        script.Append("echo @@distro; echo \"$WSL_DISTRO_NAME\"; echo @@home; echo \"$HOME\"; echo @@ip; hostname -I 2>/dev/null; ");
        script.Append("echo @@env; env | grep -E '^(" + string.Join("|", KeyVars.Select(k => k.Var)) + ")=' ; ");
        // tools installed in Linux only: WSL also sees Windows' programs through /mnt/c (directly or via a link), which
        // are the same program and already counted on Windows
        script.Append("echo @@cli; for c in " + string.Join(" ", Clis.Where(c => c != "docker")) + "; do p=$(command -v $c 2>/dev/null) && r=$(readlink -f \"$p\") && case \"$r\" in /mnt/*|*.exe) ;; *) echo $c;; esac; done; ");
        foreach (var (_, rel, _) in McpConfigs) script.Append($"echo '@@file {rel}'; cat ~/{rel} 2>/dev/null; echo; ");
        if (await Distro(exe) is not { } linux) return new(null, [], [], [], null, "WSL is installed but has no Linux distribution set up.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-d", linux, "-e", "bash", "-lic", script.ToString() }) psi.ArgumentList.Add(a);   // -i: keys are usually exported in .bashrc
        string output;
        try
        {
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await p.WaitForExitAsync(limit.Token); } catch (OperationCanceledException) { p.Kill(true); return new(null, [], [], [], null, "WSL took too long to answer, so it was skipped."); }
            output = await read;
        }
        catch { return null; }
        if (!output.Contains("@@distro")) return new(null, [], [], [], null, "WSL is installed but has no Linux distribution set up.");

        var parts = new Dictionary<string, List<string>>(); var current = "";
        foreach (var line in output.Replace("\r", "").Split('\n'))
            if (line.StartsWith("@@")) parts[current = line[2..].Trim()] = [];
            else if (current != "") parts[current].Add(line);
        string First(string k) => parts.GetValueOrDefault(k)?.FirstOrDefault(l => l.Trim() != "")?.Trim() ?? "";
        var distro = First("distro"); var home = First("home");
        var ip = First("ip").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var env = (parts.GetValueOrDefault("env") ?? []).Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..].Trim().Trim('"', '\''));
        var files = parts.Where(kv => kv.Key.StartsWith("file ")).ToDictionary(kv => kv.Key[5..], kv => string.Join("\n", kv.Value));
        var unc = distro != "" && home.StartsWith('/') ? $@"\\wsl.localhost\{distro}{home.Replace('/', '\\')}" : null;
        return new(ip, env, (parts.GetValueOrDefault("cli") ?? []).Select(c => c.Trim()).Where(c => c != "").Distinct().ToList(), files, unc, null, linux);
    }

    // The Linux distro to look in: the default one, unless that's Docker Desktop's internal distro (no shell, no tools).
    static async Task<string?> Distro(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.Unicode };
        foreach (var a in new[] { "-l", "-v" }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var rows = (await p.StandardOutput.ReadToEndAsync()).Replace("\0", "").Split('\n').Skip(1)
                .Select(l => (Default: l.TrimStart().StartsWith('*'), Name: l.Trim().TrimStart('*').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""))
                .Where(x => x.Name != "" && !x.Name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)).ToList();
            return rows.FirstOrDefault(x => x.Default).Name is { Length: > 0 } d ? d : rows.FirstOrDefault().Name is { Length: > 0 } f ? f : null;
        }
        catch { return null; }
    }
}
