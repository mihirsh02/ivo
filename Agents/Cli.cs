using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

// AI command-line tools as providers: Ivo runs the tool's own one-shot mode in the chat's folder and shows what it
// streams. The tool uses its own sign-in, model choice and tools; Ivo never touches its credentials.
// A provider's BaseUrl is "cli:<command>" (Windows) or "cli:<command>@<distro>" (WSL).
public static class Cli
{
    // Args get the model (null = the tool's default), the session to continue (null = new), the chat (permissions).
    // "{PROMPT}" in the args is replaced by the message; without it the message goes in on stdin.
    // How its exact models are found (Verify): a tool that lists them itself gives ListArgs and a pattern for one model per line;
    // otherwise Catalog names the makers whose models it can run (tried one by one, kept if they answer), spelled with dashes
    // or dots. Neither: the tool picks its own model. Present: an extra check that it's really installed (Gordon lives inside docker).
    public record Adapter(string Command, string Display, string[] Models, string Format, Func<string?, string?, Chat, List<string>> Args, bool Sessions = false,
                          string[]? Catalog = null, bool Dashes = false, string[]? ListArgs = null, string? ListPattern = null, string[]? Present = null,
                          string[]? NamesArgs = null, string? NamesPattern = null, string? NamesAfter = null,
                          int CheckSeconds = 120, int CheckAtOnce = 8);   // how long a check waits for an answer; how many checks run together

    static bool All(Chat c) => c.Permission == "all";

    public static readonly Adapter[] Adapters =
    [
        new("claude", "Claude CLI", ["default", "opus", "sonnet", "haiku"], "claude", (m, s, c) =>
        [
            "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--permission-mode", c.Mode == "plan" ? "plan" : c.Permission switch { "all" => "bypassPermissions", "assisted" => "acceptEdits", _ => "default" },
            .. m != null ? new[] { "--model", m } : [], .. s != null ? new[] { "--resume", s } : [],
            .. c.Title == CheckTitle ? new[] { "--no-session-persistence" } : [],   // a model check leaves nothing in its history
        ], Sessions: true, Catalog: ["anthropic"], Dashes: true),                    // claude-opus-4-8
        new("copilot", "Copilot CLI", ["default"], "copilot", (m, s, c) =>
        [
            "-p", "{PROMPT}", "--output-format", "json",
            .. All(c) ? new[] { "--allow-all-tools", "--allow-all-paths" } : [],
            .. m != null ? new[] { "--model", m } : [], .. s != null ? new[] { "--resume", s } : [],
            .. c.Title == CheckTitle ? new[] { "--disable-builtin-mcps", "--no-custom-instructions" } : [],   // a check loads as little as it can
        ], Sessions: true),
        new("codex", "Codex", ["default"], "text", (m, s, c) => ["exec", "--skip-git-repo-check", .. All(c) ? new[] { "--full-auto" } : [], .. m != null ? new[] { "-m", m } : [], "{PROMPT}"], Catalog: ["openai"]),
        new("gemini", "Gemini CLI", ["default"], "text", (m, s, c) => ["-p", "{PROMPT}", .. All(c) ? new[] { "--yolo" } : [], .. m != null ? new[] { "-m", m } : []],
            Catalog: ["google"], CheckAtOnce: 3),   // heavy to start, so 3 at a time
        new("qwen", "Qwen Code", ["default"], "text", (m, s, c) => ["-p", "{PROMPT}", .. All(c) ? new[] { "--yolo" } : [], .. m != null ? new[] { "-m", m } : []], Catalog: ["qwen"]),
        new("cursor-agent", "Cursor Agent", ["default"], "text", (m, s, c) => ["-p", "{PROMPT}", .. All(c) ? new[] { "--force" } : [], .. m != null ? new[] { "--model", m } : []], Catalog: ["openai", "anthropic", "google", "x-ai"]),
        new("opencode", "opencode", ["default"], "text", (m, s, c) => ["run", .. m != null ? new[] { "-m", m } : [], "{PROMPT}"],
            ListArgs: ["models"], ListPattern: @"^\s*([\w.\-]+/[\w.:\-/]+)\s*$"),        // provider/model, one per line
        new("aider", "Aider", ["default"], "text", (m, s, c) => ["--message", "{PROMPT}", "--no-pretty", .. All(c) ? new[] { "--yes-always" } : [], .. m != null ? new[] { "--model", m } : []],
            ListArgs: ["--list-models", "/"], ListPattern: @"^\s*-\s+([\w.\-/:]+)\s*$"),    // "- openai/gpt-4o"
        new("goose", "Goose", ["default"], "text", (m, s, c) => ["run", "-t", "{PROMPT}"]),
        new("amp", "Amp", ["default"], "text", (m, s, c) => ["-x", "{PROMPT}", .. All(c) ? new[] { "--dangerously-allow-all" } : []]),
        new("crush", "Crush", ["default"], "text", (m, s, c) => ["run", "{PROMPT}"]),
        new("llm", "llm", ["default"], "text", (m, s, c) => [.. m != null ? new[] { "-m", m } : [], "{PROMPT}"],
            ListArgs: ["models"], ListPattern: @"^[^:]+:\s+([\w.\-/:]+)"),                  // "OpenAI Chat: gpt-4o (aliases: 4o)"
        // Docker's AI agent, Gordon: it lives inside docker (so docker alone isn't enough) and Docker picks its model
        new("docker", "Gordon (Docker AI)", ["default"], "text", (m, s, c) => ["ai", "{PROMPT}"], Present: ["ai", "version"]),
    ];

    public static Adapter? Find(string command) => Adapters.FirstOrDefault(a => a.Command == command);

    // ---------- which exact models a tool can run, found by asking it, not from a list ----------
    public static Dictionary<string, string> Status = [];   // Settings.ModelStatus: "provider|model" → ok / failed; "provider|#checked" → when

    // the tool's own shortcuts, then every version that answered the last check
    public static List<string> Models(ProviderCfg p, Adapter a)
    {
        var found = Status.Where(x => x.Key.StartsWith(p.Name + "|") && x.Value == "ok" && !x.Key.Contains("|#"))
                          .Select(x => x.Key[(p.Name.Length + 1)..]).Where(m => !a.Models.Contains(m)).OrderByDescending(m => m).ToList();
        return found.Count > 0 ? found : [.. a.Models];   // exact versions once checked; until then the tool's own default
    }

    public static bool CanVerify(Adapter a) => a.Catalog != null || a.ListArgs != null || a.NamesArgs != null;
    const string CheckTitle = "Ivo model check";

    // Finds the tool's models. A tool that lists them: its own list, as it gives it (free). Otherwise one tiny message per
    // candidate on your plan: a name the tool doesn't know is refused before any model runs (free); one it knows answers "OK"
    // (one request). Run weekly, and when you press check.
    public static async Task<(int Ok, int Of)> Verify(ProviderCfg p, Action<string>? report, CancellationToken ct = default)
    {
        var (a, distro) = Of(p) ?? throw new Exception("Not a command-line tool.");
        if (a.ListArgs != null)
        {
            report?.Invoke("Finding models…");
            var listed = (await Capture(a, distro, a.ListArgs, ct)).Split('\n')
                .Select(l => Regex.Match(l.TrimEnd('\r'), a.ListPattern!)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
            foreach (var k in Status.Keys.Where(k => k.StartsWith(p.Name + "|") && !k.EndsWith("|#checked")).ToList()) Status.Remove(k);   // its list replaces the old one
            foreach (var m in listed) Status[$"{p.Name}|{m}"] = "ok";
            Status[$"{p.Name}|#checked"] = DateTime.Now.ToString("O");
            return (listed.Count, listed.Count);
        }
        List<string> candidates;
        if (a.NamesArgs != null)
        {
            var help = await Capture(a, distro, a.NamesArgs, ct);
            var from = a.NamesAfter != null && help.IndexOf(a.NamesAfter, StringComparison.Ordinal) is var at and >= 0 ? help[at..] : help;
            candidates = from.Split('\n').Skip(1).TakeWhile(l => l.Trim() == "" || Regex.IsMatch(l, a.NamesPattern!)).Select(l => Regex.Match(l, a.NamesPattern!))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
        }
        else
        {
            var makers = a.Catalog!;
            // a maker's own list first (your key with that maker in Ivo, e.g. Google's for Gemini CLI); the public catalogue otherwise
            var own = MakerModels != null ? await MakerModels(makers) : [];
            var ids = own.Count > 0 ? own.Select(m => makers[0] + "/" + m).ToList() : await Catalogue(ct);
            candidates = ids
                .Select(id => id.Split(':')[0])
                .Where(id => makers.Any(mk => id.StartsWith(mk + "/")))
                .Select(id => id[(id.IndexOf('/') + 1)..])
                .Where(id => !id.Contains("embed") && !id.Contains("robotics") && !id.Contains("computer-use") && !id.Contains("native-audio") && !id.Contains("nano-banana") && !id.Contains("image") && !id.Contains("audio") && !id.Contains("tts") && !id.Contains("search") && !id.Contains("veo") && !id.Contains("aqa") && !id.Contains("live") && !id.Contains("lyria"))   // chat models only
                .Select(id => a.Dashes ? id.Replace('.', '-') : id)   // anthropic/claude-opus-4.8 → claude-opus-4-8 for Claude CLI
                .Distinct().ToList();
        }
        int done = 0, ok = 0;
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var took = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();   // seconds to answer, for "fast" / "slow"
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);   // the tool ran out of quota: no point asking more
        using var gate = new SemaphoreSlim(a.CheckAtOnce);   // each check mostly waits for the tool to start, so several run at once
        await Task.WhenAll(candidates.Select(async model =>
        {
            try { await gate.WaitAsync(stop.Token); } catch (OperationCanceledException) { return; }
            try
            {
                var clock = Stopwatch.StartNew();
                var (answered, own) = await Answers(p, model, stop.Token, TimeSpan.FromSeconds(a.CheckSeconds));
                switch (answered)
                {
                    case true: found[model] = true; took[model] = own ?? clock.Elapsed.TotalSeconds; Interlocked.Increment(ref ok); break;
                    case false: found[model] = false; break;
                    case null: stop.Cancel(); break;
                }
            }
            finally { gate.Release(); if (!stop.IsCancellationRequested) report?.Invoke($"Checking models… {Interlocked.Increment(ref done)} of {candidates.Count}  ·  {ok} working"); }
        }));
        ct.ThrowIfCancellationRequested();
        // out of quota or rate-limited: the earlier results stand (a model isn't broken because the plan ran dry for now)
        if (stop.IsCancellationRequested)
        {
            Status[$"{p.Name}|#limited"] = DateTime.Now.ToString("O");   // the weekly check waits half a day before trying again
            throw new Exception($"{a.Display} is out of quota or rate-limited right now, so its models couldn't be checked. Earlier results are kept; try again later.");
        }
        foreach (var k in Status.Keys.Where(k => k.StartsWith(p.Name + "|")).ToList()) Status.Remove(k);   // this check replaces the last one
        foreach (var (model, works) in found) Status[$"{p.Name}|{model}"] = works ? "ok" : "failed";
        foreach (var (model, secs) in took) Status[$"{p.Name}|#time|{model}"] = Math.Round(secs, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Status[$"{p.Name}|#checked"] = DateTime.Now.ToString("O");
        return (ok, candidates.Count);
    }

    static readonly Regex Limited = new(@"\b429\b|quota|rate.?limit|RESOURCE_EXHAUSTED|too many requests|usage limit", RegexOptions.IgnoreCase);

    // true: it answered; false: the tool refused that model; null: the tool is out of quota or rate-limited, so no one can tell
    static async Task<(bool? Works, double? Seconds)> Answers(ProviderCfg p, string model, CancellationToken ct, TimeSpan? within = null)
    {
        var chat = new Chat { Model = model, Title = CheckTitle };
        chat.Messages.Add(new("user", "Reply with just the word OK."));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(within ?? TimeSpan.FromMinutes(2));   // too slow to answer counts as not working (Gemini CLI: 10 s)
        try
        {
            var r = await Run(p, chat, _ => { }, _ => { }, limit.Token);
            if (Limited.IsMatch(r.Text)) return (null, null);
            return (r.Text.Trim().Length > 0 && !Regex.IsMatch(r.Text, @"not.?found|invalid model|isn't available|is not available|not exist|not supported|API Error|issue with the selected model|unrecognized_model|have access|code: 404|critical error|sign in", RegexOptions.IgnoreCase), r.ModelSeconds);
        }
        catch (Exception ex) when (Limited.IsMatch(ex.Message)) { return (null, null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, null); }
        catch { return (false, null); }
    }

    // One question with the tool's own default model: does it answer at all? (for tools that pick their own model, like Gordon)
    public static async Task<bool?> Ping(ProviderCfg p)
    {
        var (ok, _) = await Answers(p, "default", default);
        if (ok != null) { Status[$"{p.Name}|#ready"] = ok == true ? "ok" : "failed"; Status[$"{p.Name}|#checked"] = DateTime.Now.ToString("O"); }
        return ok;
    }

    // Set by the app: a maker's models from a key you added for that maker ("google" → your Gemini key's list), or none.
    public static Func<string[], Task<List<string>>>? MakerModels;

    // OpenRouter's public list of models ("maker/model"): fetched once and shared by every tool's check for an hour.
    // It's a big file, so a generous wait and one retry.
    static (DateTime At, List<string> Ids)? catalogue;
    static readonly SemaphoreSlim catalogueGate = new(1, 1);
    static async Task<List<string>> Catalogue(CancellationToken ct)
    {
        await catalogueGate.WaitAsync(ct);
        try
        {
            if (catalogue is { } c && DateTime.Now - c.At < TimeSpan.FromHours(1)) return c.Ids;
            using var web = new System.Net.Http.HttpClient(Net.Handler()) { Timeout = TimeSpan.FromSeconds(90) };
            for (int attempt = 1; ; attempt++)
                try
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(await web.GetStringAsync("https://openrouter.ai/api/v1/models", ct));
                    var ids = (json?["data"] as System.Text.Json.Nodes.JsonArray ?? []).Select(m => (string?)m?["id"] ?? "").Where(x => x != "").ToList();
                    catalogue = (DateTime.Now, ids);
                    return ids;
                }
                catch when (attempt < 2 && !ct.IsCancellationRequested) { }
                catch (Exception ex) { throw new Exception("Couldn't get the list of models to check (no internet, or OpenRouter is slow): " + ex.Message); }
        }
        finally { catalogueGate.Release(); }
    }

    static async Task<string> Capture(Adapter a, string? distro, string[] args, CancellationToken ct = default)
    {
        ProcessStartInfo psi;
        if (distro == null)
        {
            var (file, prefix) = Locate(a.Command) ?? throw new Exception($"{a.Display} isn't on this PC.");
            psi = new(file);
            foreach (var x in prefix.Concat(args)) psi.ArgumentList.Add(x);
        }
        else
        {
            psi = new(Path.Combine(Environment.SystemDirectory, "wsl.exe"));
            foreach (var x in new[] { "-d", distro, "-e", "bash", "-lc", "exec \"$@\"", "bash", a.Command }.Concat(args)) psi.ArgumentList.Add(x);
        }
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false); psi.Environment["NO_COLOR"] = "1";
        using var proc = Process.Start(psi) ?? throw new Exception($"Couldn't start {a.Display}.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(60));
        using var kill = limit.Token.Register(() => { try { proc.Kill(true); } catch { } });
        var errors = proc.StandardError.ReadToEndAsync(CancellationToken.None);   // drained too, or a full stderr pipe stalls it
        var output = await proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await errors;
        await proc.WaitForExitAsync(CancellationToken.None);
        return proc.ExitCode == 0 ? output : throw new Exception($"{a.Display} said: {output.Trim()}");
    }

    // installed for real: docker alone doesn't mean Gordon is there
    public static async Task<bool> Installed(Adapter a)
    {
        if (Locate(a.Command) == null) return false;
        if (a.Present == null) return true;
        try { await Capture(a, null, a.Present); return true; } catch { return false; }
    }

    // How quickly a model answered its last check: "fast" within 10 seconds, "slow" beyond, null if never timed.
    public static string? Speed(string provider, string model) =>
        Status.TryGetValue($"{provider}|#time|{model}", out var t) && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            ? secs <= 10 ? "fast" : "slow" : null;

    // out of quota or rate-limited in the last 12 hours: don't ask again yet
    public static bool RecentlyLimited(ProviderCfg p) =>
        Status.TryGetValue($"{p.Name}|#limited", out var lim) && DateTime.TryParse(lim, out var l) && DateTime.Now - l < TimeSpan.FromHours(12);

    // a check older than a week (or none yet) is due again
    public static bool Due(ProviderCfg p) =>
        (!Status.TryGetValue($"{p.Name}|#checked", out var at) || !DateTime.TryParse(at, out var t) || DateTime.Now - t > TimeSpan.FromDays(7))
        && !(Status.TryGetValue($"{p.Name}|#limited", out var lim) && DateTime.TryParse(lim, out var l) && DateTime.Now - l < TimeSpan.FromHours(12));

    public static (Adapter Adapter, string? Distro)? Of(ProviderCfg p)
    {
        if (p.Kind != "cli" || !p.BaseUrl.StartsWith("cli:")) return null;
        var t = p.BaseUrl[4..].Split('@', 2);
        return Find(t[0]) is { } a ? (a, t.Length > 1 ? t[1] : null) : null;
    }

    public static ProviderCfg Provider(string command, string? distro) => new()
    {
        Name = Find(command)!.Display + (distro != null ? " · WSL" : ""), Kind = "cli",
        BaseUrl = "cli:" + command + (distro != null ? "@" + distro : ""), Key = "",
    };

    // ---------- finding the program on Windows ----------
    // npm installs a .cmd wrapper; running that through cmd.exe would let a message's & | " characters break out, so
    // the wrapper's node script is run directly instead. VS Code's "copilot-shim" placeholder is skipped.
    public static (string File, string[] Prefix)? Locate(string command)
    {
        foreach (var dir in SearchDirs())
        {
            if (dir.Contains("copilot-shim", StringComparison.OrdinalIgnoreCase)) continue;
            var exe = Path.Combine(dir, command + ".exe");
            if (File.Exists(exe)) return (exe, []);
            var cmd = Path.Combine(dir, command + ".cmd");
            if (File.Exists(cmd) && Regex.Match(File.ReadAllText(cmd), @"""%dp0%\\([^""]+?\.exe)""") is { Success: true } bin && File.Exists(Path.Combine(dir, bin.Groups[1].Value)))
                return (Path.Combine(dir, bin.Groups[1].Value), []);   // npm shims that start a bundled program (opencode)
            if (File.Exists(cmd) && Regex.Match(File.ReadAllText(cmd), @"""%dp0%\\([^""]+?\.(?:js|mjs|cjs))""") is { Success: true } m)
            {
                var node = Path.Combine(dir, "node.exe");
                return (File.Exists(node) ? node : "node", [Path.Combine(dir, m.Groups[1].Value)]);
            }
        }
        return null;
    }

    // Ivo's own PATH, the current one from Windows' settings (tools installed after Ivo started), and the
    // usual install folders that aren't always on PATH.
    static IEnumerable<string> SearchDirs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(t => (Environment.GetEnvironmentVariable("PATH", t) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Concat([
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(profile, ".local", "bin"), Path.Combine(profile, ".bun", "bin"),
                Path.Combine(profile, ".cargo", "bin"), Path.Combine(profile, ".deno", "bin"),
                Path.Combine(profile, ".cursor", "bin"), Path.Combine(profile, ".codex", "bin"),
                Path.Combine(profile, ".opencode", "bin"), Path.Combine(profile, "go", "bin"),
                Path.Combine(profile, "scoop", "shims"),
                Path.Combine(local, "GitHubCopilotCLI"), Path.Combine(local, "Programs"),
                Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"),
                Path.Combine(local, "Programs", "Cursor", "resources", "app", "bin"),
                Path.Combine(local, "Microsoft", "WinGet", "Links"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin"),
            ]);
        return paths.Select(d => Environment.ExpandEnvironmentVariables(d.Trim()).TrimEnd('\\')).Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // ---------- one turn ----------
    // secrets in the prompt are masked for the tool (and its cloud model), like Api.StreamOnce; what it reads itself isn't
    public static async Task<Api.Reply> Run(ProviderCfg cfg, Chat chat, Action<string> onDelta, Action<string> onThought, CancellationToken ct, Action<string>? onStep = null)
    {
        if (!Planner.S.RedactSecrets) return await RunRaw(cfg, chat, onDelta, onThought, ct, onStep);
        using var scope = Redact.Begin();
        var r = await RunRaw(cfg, chat, d => onDelta(Redact.Unmask(d)), onThought, ct, onStep);
        return r with { Text = Redact.Unmask(r.Text) };
    }

    static async Task<Api.Reply> RunRaw(ProviderCfg cfg, Chat chat, Action<string> onDelta, Action<string> onThought, CancellationToken ct, Action<string>? onStep)
    {
        var (a, distro) = Of(cfg) ?? throw new Exception("This provider isn't a command-line tool Ivo knows.");
        var session = a.Sessions && chat.CliSession is { } cs && cs.StartsWith(cfg.BaseUrl + "|") ? cs[(cfg.BaseUrl.Length + 1)..] : null;
        var prompt = Redact.Mask(Prompt(chat, session != null));
        var model = chat.Model is null or "" or "default" ? null : chat.Model;
        var args = a.Args(model, session, chat);
        var stdin = !args.Contains("{PROMPT}");
        var cwd = Agent.HasWorkspace(chat) ? chat.Workspace! : Directory.CreateDirectory(Path.Combine(Store.Dir, "cli")).FullName;

        ProcessStartInfo psi;
        if (distro == null)
        {
            var (file, prefix) = Locate(a.Command) ?? throw new Exception($"{a.Display} ({a.Command}) wasn't found on this PC any more.");
            psi = new(file) { WorkingDirectory = cwd };
            foreach (var x in prefix.Concat(args)) psi.ArgumentList.Add(x == "{PROMPT}" ? prompt : x);
        }
        else
        {
            // In WSL the message always goes in on stdin; a tiny bash wrapper puts it where "{PROMPT}" is, so no
            // quoting ever passes through wsl.exe.
            const string wrap = """if printf '%s\n' "$@" | grep -qx '{PROMPT}'; then P=$(cat); a=(); for x in "$@"; do if [ "$x" = '{PROMPT}' ]; then a+=("$P"); else a+=("$x"); fi; done; set -- "${a[@]}"; fi; exec "$@" """;
            psi = new(Path.Combine(Environment.SystemDirectory, "wsl.exe"));
            foreach (var x in new[] { "-d", distro, "--cd", cwd, "-e", "bash", "-lc", wrap, "bash", a.Command }.Concat(args)) psi.ArgumentList.Add(x);
            stdin = true;
        }
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.Environment["NO_COLOR"] = "1";

        using var p = Process.Start(psi) ?? throw new Exception($"Couldn't start {a.Display}.");
        using var kill = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
        if (stdin) await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();
        var errors = p.StandardError.ReadToEndAsync(CancellationToken.None);

        var text = new StringBuilder(); var th = new StringBuilder(); string? final = null; Usage? usage = null; string? newSession = null; string? failure = null;
        void Text(string? d) { if (string.IsNullOrEmpty(d)) return; text.Append(d); onDelta(d); }
        void Thought(string? d) { if (string.IsNullOrEmpty(d)) return; th.Append(d); onThought(d); }
        void Step(string s) => Thought((th.Length > 0 && !th.ToString().EndsWith('\n') ? "\n" : "") + "▸ " + s + "\n");
        // the latest step in full (the command, then its output once it's back), for a tab in Code mode
        string? stepId = null, stepHead = null;
        void Full(string? id, string name, JsonNode? input) { stepId = id; stepHead = Detail(name, input); onStep?.Invoke(id != null ? stepHead + "\n\n(running…)" : stepHead); }   // only Claude's results can be matched back

        // the model's own time: Claude CLI reports it; for other tools, from their first line of output (start-up done) to the end
        double? apiMs = null; var started = Stopwatch.StartNew(); TimeSpan? firstLine = null;
        while (await p.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            firstLine ??= started.Elapsed;
            if (a.Format == "text") { Text(line + "\n"); continue; }
            JsonNode? j; try { j = JsonNode.Parse(line); } catch { continue; }
            if (j == null) continue;
            if (a.Format == "claude")
                switch ((string?)j["type"])
                {
                    case "stream_event":
                        var d = j["event"]?["delta"];
                        if ((string?)d?["type"] == "text_delta") Text((string?)d["text"]);
                        else if ((string?)d?["type"] == "thinking_delta") Thought((string?)d["thinking"]);
                        break;
                    case "assistant":
                        if (text.Length > 0 && !text.ToString().EndsWith('\n')) Text("\n\n");   // text from several turns doesn't run together
                        foreach (var b in j["message"]?["content"]?.AsArray() ?? [])
                            if ((string?)b?["type"] == "tool_use") { Step($"{b["name"]} {Brief(b["input"])}"); Full((string?)b["id"], (string?)b["name"] ?? "", b["input"]); }
                        break;
                    case "user":   // a tool's result comes back
                        foreach (var b in j["message"]?["content"]?.AsArray() ?? [])
                            if ((string?)b?["type"] == "tool_result" && (string?)b["tool_use_id"] == stepId && stepHead != null)
                                onStep?.Invoke(stepHead + "\n\n" + (b["content"] is JsonArray parts ? string.Join("\n", parts.Select(x => (string?)x?["text"])) : (string?)b["content"]));
                        break;
                    case "result":
                        newSession = (string?)j["session_id"];
                        final = (string?)j["result"];
                        apiMs = (double?)j["duration_api_ms"];
                        var u = j["usage"];
                        usage = new(((int?)u?["input_tokens"] ?? 0) + ((int?)u?["cache_read_input_tokens"] ?? 0) + ((int?)u?["cache_creation_input_tokens"] ?? 0), (int?)u?["output_tokens"] ?? 0);
                        if ((bool?)j["is_error"] == true) failure = final ?? "Claude Code reported an error.";
                        break;
                }
            else   // copilot
                switch ((string?)j["type"])
                {
                    case "assistant.message_delta": Text((string?)j["data"]?["deltaContent"]); break;
                    case "assistant.reasoning_delta": Thought((string?)j["data"]?["deltaContent"]); break;
                    case "assistant.message":
                        if ((string?)j["data"]?["phase"] == "final_answer") final = (string?)j["data"]?["content"];
                        foreach (var t in j["data"]?["toolRequests"]?.AsArray() ?? []) { Step($"{t?["name"] ?? t?["toolName"]} {Brief(t?["arguments"])}"); Full(null, (string?)(t?["name"] ?? t?["toolName"]) ?? "", t?["arguments"]); }
                        if (text.Length > 0 && !text.ToString().EndsWith('\n')) Text("\n\n");
                        break;
                    case "result":
                        newSession = (string?)j["sessionId"];
                        if ((int?)j["exitCode"] is { } code && code != 0) failure = $"Copilot stopped with exit code {code}.";
                        break;
                    case "session.error": failure = (string?)j["data"]?["message"] ?? "Copilot reported an error."; break;
                }
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var err = (await errors).Trim();
        if (failure != null) throw new Exception(failure);
        if (text.Length == 0 && final == null && p.ExitCode != 0)
            throw new Exception($"{a.Display} stopped (exit code {p.ExitCode})" + (err != "" ? ": " + Tail(err) : "."));
        if (newSession != null) chat.CliSession = $"{cfg.BaseUrl}|{newSession}";
        // (a tool whose first line already is the answer gives no start-up mark: then the whole time is used)
        var own = apiMs is { } ms ? ms / 1000 : firstLine is { } f && (started.Elapsed - f).TotalSeconds > 0.5 ? (started.Elapsed - f).TotalSeconds : (double?)null;
        return new((final ?? text.ToString()).Trim(), [], th.ToString().Trim(), null, usage, own);
    }

    // With a session the tool remembers the chat; otherwise the earlier messages are sent along with the new one.
    static string Prompt(Chat c, bool remembered)
    {
        var last = c.Messages.Last(m => m.Role == "user").Text;
        if (remembered) return last;
        var earlier = string.Join("\n\n", c.Messages.Take(c.Messages.Count - 1).Where(m => m.Role is "user" or "assistant" && m.Text != "")
                                                 .Select(m => (m.Role == "user" ? "User: " : "Assistant: ") + m.Text));
        if (earlier.Length > 20_000) earlier = "…" + earlier[^20_000..];
        var sys = (Agent.AboutMe() + "\n" + c.System).Trim();
        return (sys != "" ? $"Instructions: {sys}\n\n" : "") + (earlier != "" ? $"Our conversation so far:\n{earlier}\n\nNow reply to this:\n" : "") + last;
    }

    static string Brief(JsonNode? input)
    {
        if (input == null) return "";
        var s = input is JsonValue v ? v.ToString() : string.Join(", ", (input as JsonObject ?? []).Take(2).Select(kv => kv.Value?.ToString() ?? ""));
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 90 ? s[..90] + "…" : s;
    }

    // a step written out whole: a shell command as "$ command", anything else as its arguments
    static string Detail(string name, JsonNode? input)
    {
        if (input is JsonValue v && v.TryGetValue(out string? raw)) try { input = JsonNode.Parse(raw); } catch { }   // Copilot sends arguments as a JSON string
        var o = input as JsonObject;
        var cmd = (string?)o?["command"];
        var about = (string?)o?["description"];
        return "▸ " + name + (about != null ? " · " + about : "") + "\n\n"
             + (cmd != null ? "$ " + cmd : input?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "");
    }

    static string Tail(string s) { var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l != "").TakeLast(3); var t = string.Join(" ", lines); return t.Length > 300 ? t[^300..] : t; }
}
