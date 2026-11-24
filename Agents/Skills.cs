using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

public record Skill(string Name, string Description, string Dir, string Origin = "Ivo")   // Origin: whose skill it is ("Ivo", "Claude CLI", …)
{
    public bool Mine => Origin == "Ivo";   // only Ivo's own can be deleted from Ivo
    // "owner/repo/path" for skills installed from GitHub, so Browse can show them as installed and update them
    public string? Source => File.Exists(System.IO.Path.Combine(Dir, Skills.SourceFile)) ? File.ReadAllText(System.IO.Path.Combine(Dir, Skills.SourceFile)).Trim() : null;
}

// A skill found in a GitHub source: Path is its folder in the repo, Files every file under it.
public record RemoteSkill(string Repo, string Ref, string Path, string Name, string Description, List<string> Files)
{
    public string Id => $"{Repo}/{Path}";
}

// Skills: folders with a SKILL.md (same format as Claude's). The model sees each one's name and description and
// loads the full instructions with use_skill only when a task calls for it, so unused skills cost almost nothing.
public static class Skills
{
    public static readonly string Root = Path.Combine(Store.Dir, "skills");
    public static HashSet<string> Disabled { get; set; } = [];

    public static List<Skill> All()
    {
        Directory.CreateDirectory(Root);
        return Directory.GetDirectories(Root).Select(d => Parse(d)).OfType<Skill>().OrderBy(s => s.Name).ToList();
    }

    // The skills another AI tool has on this PC, for Settings → Skills → Import from this PC. "claude": its own, the ones
    // synced from your Claude account, and its installed plugins' (at their installed version); "gemini" and "copilot": the
    // shared ~/.agents/skills plus their own folders (and Gemini's extensions). Each name once.
    public static List<Skill> OnPc(string tool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<Skill> In(string dir, string origin, int depth = 1)
        {
            if (!Directory.Exists(dir)) return [];
            try
            {
                return Directory.EnumerateFiles(dir, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, MatchCasing = MatchCasing.CaseInsensitive })
                    .Select(f => Parse(Path.GetDirectoryName(f)!, origin)).OfType<Skill>().ToList();
            }
            catch { return []; }
        }
        var found = new List<Skill>();
        if (tool == "gemini" || tool == "copilot") found.AddRange(In(Path.Combine(home, ".agents", "skills"), "shared with Gemini CLI and Copilot CLI"));
        if (tool == "gemini") found.AddRange(In(Path.Combine(home, ".gemini", "skills"), "Gemini CLI"));
        if (tool == "copilot") found.AddRange(In(Path.Combine(home, ".copilot", "skills"), "Copilot CLI"));
        if (tool == "claude") found.AddRange(In(Path.Combine(home, ".claude", "skills"), "Claude CLI", depth: 3));   // synced ones sit two folders down
        // Claude's plugins: their cache keeps old versions too, so only the installed one of each (its own record says which)
        if (tool == "claude") try
        {
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ".claude", "plugins", "installed_plugins.json")));
            foreach (var (id, installs) in record?["plugins"]?.AsObject() ?? [])
                foreach (var install in installs?.AsArray() ?? [])
                    if ((string?)install?["installPath"] is { } at)
                        found.AddRange(In(Path.Combine(at, "skills"), $"Claude plugin · {id.Split('@')[0]}"));
        }
        catch { }
        // Gemini CLI's extensions
        var ext = Path.Combine(home, ".gemini", "extensions");
        if (tool == "gemini" && Directory.Exists(ext))
            foreach (var d in Directory.GetDirectories(ext)) found.AddRange(In(Path.Combine(d, "skills"), $"Gemini extension · {Path.GetFileName(d)}"));
        return found.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(s => s.Name).ToList();
    }
    public static List<Skill> Enabled() => All().Where(s => !Disabled.Contains(s.Name) && (IsTrusted?.Invoke(s) ?? true)).ToList();
    public static Func<Skill, bool>? IsTrusted;   // set by the window: new or changed skills stay out until you allow them

    // What a skill is, as one hash, so a changed skill is asked about again: the full text of SKILL.md (what steers the
    // model) plus every other file's name, size and date. Reading only metadata for the rest keeps it instant at startup.
    static readonly Dictionary<string, (DateTime At, string Hash)> prints = [];
    // ponytail: hashed at most every 30 s per skill (Enabled() runs on every model list); a watcher if that ever lags
    public static string Fingerprint(Skill s)
    {
        lock (prints)
            if (prints.TryGetValue(s.Dir, out var p) && DateTime.UtcNow - p.At < TimeSpan.FromSeconds(30)) return p.Hash;
        var hash = Hash(s);
        lock (prints) prints[s.Dir] = (DateTime.UtcNow, hash);
        return hash;
    }

    static string Hash(Skill s)
    {
        var sb = new System.Text.StringBuilder();
        if (SkillFile(s.Dir) is { } md) sb.Append(File.ReadAllText(md)).Append('\0');
        foreach (var f in new DirectoryInfo(s.Dir).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            sb.Append(Path.GetRelativePath(s.Dir, f.FullName)).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    static string? SkillFile(string dir) => Directory.GetFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));

    public const string SourceFile = ".ivo-source";

    static Skill? Parse(string dir, string origin = "Ivo")
    {
        try { return SkillFile(dir) is { } file ? Parse(File.ReadAllText(file), Path.GetFileName(dir), dir) with { Origin = origin } : null; }
        catch { return null; }
    }

    // Frontmatter between --- lines: name and description (a one-liner, quoted, or a > / | block).
    static Skill Parse(string text, string fallbackName, string dir)
    {
        var fm = Regex.Match(text, @"\A\s*---\s*\n(.*?)\n---", RegexOptions.Singleline).Groups[1].Value.Replace("\r", "");
        string Field(string key)
        {
            var m = Regex.Match(fm, $@"^{key}:[ \t]*(.*)$((?:\n[ \t]+.*)*)", RegexOptions.Multiline);
            if (!m.Success) return "";
            var first = m.Groups[1].Value.Trim();
            var rest = string.Join(" ", m.Groups[2].Value.Split('\n').Select(l => l.Trim()).Where(l => l != ""));
            return (first is ">" or "|" or ">-" or "|-" ? rest : (first + " " + rest).Trim()).Trim('"', '\'');
        }
        var name = Field("name");
        return new(name != "" ? name : fallbackName, Field("description"), dir);
    }

    // For the system prompt: what exists and when to load it.
    public static string Prompt(List<Skill> skills) => skills.Count == 0 ? "" :
        "You have skills: instructions for particular kinds of tasks. Before starting a task that one covers, call use_skill with its name and follow what it says.\n" +
        string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));

    // SKILL.md (or another file of the skill) for the model, plus a list of the skill's files.
    public static string Read(string name, string? file)
    {
        var s = Enabled().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception($"There's no skill called {name}. Available: {string.Join(", ", Enabled().Select(x => x.Name))}.");
        var root = Path.GetFullPath(s.Dir).TrimEnd('\\') + "\\";
        var path = string.IsNullOrWhiteSpace(file) ? SkillFile(s.Dir)! : Path.GetFullPath(Path.Combine(root, file));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception($"{file} is outside the skill's folder.");
        var text = File.ReadAllText(path);
        if (!string.IsNullOrWhiteSpace(file)) return text;
        var others = Directory.EnumerateFiles(s.Dir, "*", SearchOption.AllDirectories).Where(f => f != path && Path.GetFileName(f) != SourceFile).Select(f => Path.GetRelativePath(s.Dir, f)).Take(100).ToList();
        return text + (others.Count == 0 ? "" :
            $"\n\n---\nOther files in this skill (folder {s.Dir}); read one with use_skill and its file path:\n" + string.Join("\n", others.Select(f => "- " + f)));
    }

    // Copies a skill folder, or a .zip / .skill archive, into Ivo's skills folder. Returns the skill's name.
    public static string Import(string source)
    {
        var tmp = Directory.CreateTempSubdirectory("ivo-skill").FullName;
        try
        {
            var from = source;
            if (File.Exists(source))
            {
                ZipFile.ExtractToDirectory(source, tmp);
                from = SkillFile(tmp) != null ? tmp : Directory.GetDirectories(tmp).FirstOrDefault(d => SkillFile(d) != null)
                       ?? throw new Exception("That archive has no SKILL.md in it.");
            }
            if (Parse(from) is not { } skill) throw new Exception("That folder has no SKILL.md in it.");
            var folder = Regex.Replace(skill.Name, @"[^\w\- .]", "_").Trim(' ', '.');   // never "", "." or ".."
            if (folder == "") folder = "skill";
            var dest = Path.GetFullPath(Path.Combine(Root, folder));
            if (!string.Equals(Path.GetDirectoryName(dest), Path.GetFullPath(Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new Exception("That skill's name isn't a usable folder name.");
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);   // re-importing updates it
            Copy(from, dest);
            return skill.Name;
        }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    // ---------- GitHub sources (Settings → Skills → Browse) ----------
    // Built in, highest priority first: when two sources have a skill with the same name, the earlier one is shown.
    public static readonly string[] BuiltInSources =
    [
        "anthropics/skills", "obra/superpowers", "getsentry/skills",
        "beshkenadze/claude-skills-marketplace", "alirezarezvani/claude-skills", "ComposioHQ/awesome-claude-skills",
    ];

    // Big repos have hundreds of SKILL.md files; they're only fetched again when the repo changes (its tree hash).
    record CacheEntry(string Sha, List<RemoteSkill> Skills, DateTime Fetched = default);
    static readonly string CachePath = Path.Combine(Store.Dir, "skill-catalog.json");
    static readonly object CacheLock = new();
    static Dictionary<string, CacheEntry> LoadCache()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)) ?? []; } catch { return []; }
    }
    static readonly HttpClient Gh = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "User-Agent", "Ivo" } } };

    // "owner/repo", "owner/repo/some/folder", or a github.com link (…/tree/<branch>/<folder> keeps the branch).
    public static (string Repo, string Ref, string Sub) ParseSource(string source)
    {
        var t = Regex.Replace(source.Trim(), @"^(https?://)?(www\.)?github\.com/", "", RegexOptions.IgnoreCase).Trim('/');
        if (t.EndsWith(".git")) t = t[..^4];
        var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Take(2).Any(x => !Regex.IsMatch(x, @"^[\w.-]+$"))) throw new Exception("Use owner/repo, or a link to a GitHub repo or folder.");
        var repo = parts[0] + "/" + parts[1];
        if (parts.Length >= 4 && parts[2] is "tree" or "blob") return (repo, parts[3], string.Join("/", parts.Skip(4)));
        return (repo, "HEAD", string.Join("/", parts.Skip(2)));
    }

    // Every SKILL.md in the source's repo (one API call), then each one's name and description (raw files, no rate limit).
    // GitHub allows 60 API calls an hour without signing in, so the saved list is used for a day (or until Reload),
    // and kept whenever GitHub can't be asked.
    public static async Task<List<RemoteSkill>> Browse(string source, bool refresh = false)
    {
        var (repo, @ref, sub) = ParseSource(source);
        CacheEntry? saved;
        lock (CacheLock) saved = LoadCache().GetValueOrDefault(source);
        if (saved != null && !refresh && DateTime.Now - saved.Fetched < TimeSpan.FromDays(1)) return saved.Skills;
        JsonNode? tree;
        try
        {
            using var res = await Gh.GetAsync($"https://api.github.com/repos/{repo}/git/trees/{@ref}?recursive=1");
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new Exception(res.StatusCode == System.Net.HttpStatusCode.NotFound ? "not found on GitHub (or it's private)"
                                  : (int)res.StatusCode is 403 or 429 ? "GitHub is busy, try again in a while" : $"GitHub said {(int)res.StatusCode}");
            tree = JsonNode.Parse(body);
        }
        catch when (saved != null) { return saved.Skills; }
        var sha = (string?)tree?["sha"] ?? "";
        if (saved != null && saved.Sha == sha) { Save(source, saved with { Fetched = DateTime.Now }); return saved.Skills; }
        var files = (tree?["tree"]?.AsArray() ?? []).Where(n => (string?)n?["type"] == "blob").Select(n => (string)n!["path"]!).ToList();
        var dirs = files.Where(f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                        .Select(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
                        .Where(d => sub == "" || d == sub || d.StartsWith(sub + "/")).ToList();
        using var gate = new SemaphoreSlim(16);
        var found = await Task.WhenAll(dirs.Select(async d =>
        {
            await gate.WaitAsync();
            try
            {
                var mine = files.Where(f => d == "" || f.StartsWith(d + "/")).ToList();
                var md = mine.First(f => (f.Contains('/') ? f[..f.LastIndexOf('/')] : "") == d && Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
                var text = await Gh.GetStringAsync(Raw(repo, @ref, md));
                var sk = Parse(text, d == "" ? repo.Split('/')[1] : d.Split('/')[^1], "");
                return new RemoteSkill(repo, @ref, d, sk.Name, sk.Description, mine);
            }
            catch { return null; }
            finally { gate.Release(); }
        }));
        var list = found.OfType<RemoteSkill>().ToList();
        Save(source, new(sha, list, DateTime.Now));
        return list;
    }

    static void Save(string source, CacheEntry e)
    {
        lock (CacheLock)
        {
            var cache = LoadCache();
            cache[source] = e;
            try { File.WriteAllText(CachePath, System.Text.Json.JsonSerializer.Serialize(cache)); } catch { }
        }
    }

    static string Raw(string repo, string @ref, string path) => $"https://raw.githubusercontent.com/{repo}/{@ref}/{string.Join("/", path.Split('/').Select(Uri.EscapeDataString))}";

    // Downloads the skill's files and adds it like a local folder (replacing an older copy). Returns its name.
    public static async Task<string> Install(RemoteSkill r)
    {
        var tmp = Directory.CreateTempSubdirectory("ivo-get").FullName;
        try
        {
            var dir = Path.Combine(tmp, r.Path == "" ? r.Repo.Split('/')[1] : r.Path.Split('/')[^1]);
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(r.Files.Select(async f =>
            {
                await gate.WaitAsync();
                try
                {
                    var to = Path.Combine(dir, (r.Path == "" ? f : f[(r.Path.Length + 1)..]).Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    await File.WriteAllBytesAsync(to, await Gh.GetByteArrayAsync(Raw(r.Repo, r.Ref, f)));
                }
                finally { gate.Release(); }
            }));
            await File.WriteAllTextAsync(Path.Combine(dir, SourceFile), r.Id);
            return Import(dir);
        }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // A starter SKILL.md to fill in.
    public static string Create()
    {
        var name = "new-skill"; var n = 1;
        while (Directory.Exists(Path.Combine(Root, name))) name = $"new-skill-{++n}";
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {name}
            description: What this skill does and when to use it (the model decides from this line).
            ---

            # {name}

            Step-by-step instructions the model follows when it uses this skill.
            """);
        return dir;
    }
}
