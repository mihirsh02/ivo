using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

// The knowledge folder as something to search by meaning. Each readable file (Docs) is cut into passages of about
// 800 characters; with an embedding model chosen (Settings → Data), each passage gets a vector and a question finds the
// closest passages. Without one, passages are ranked by how many of the question's words they contain.
// The index lives in <data>/knowledge-index.json and only files that changed since are read again.
public static class Knowledge
{
    public record Passage(string File, int Line, string Text, float[]? Vec);
    record Index(string Folder, string Model, Dictionary<string, DateTime> Files, List<Passage> Passages);

    static string IndexPath => Path.Combine(Store.Dir, "knowledge-index.json");
    static Index? index;
    static readonly SemaphoreSlim gate = new(1, 1);
    public static string Status = "";   // for Settings: "120 passages from 14 files"

    public static void Changed() { }   // nothing to do now: the next search notices changed files by their dates

    // ---------- search ----------
    public static async Task<List<Passage>> Search(string query, int max, CancellationToken ct)
    {
        await Update(ct);
        var all = index?.Passages ?? [];
        if (all.Count == 0) return [];
        if (all.Any(p => p.Vec != null) && await Embed([query], ct) is [var q])
            return [.. all.Where(p => p.Vec != null).OrderByDescending(p => Cosine(q, p.Vec!)).Take(max)];
        var words = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Distinct().ToList();
        return [.. all.Select(p => (p, n: words.Count(w => p.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))).Where(x => x.n > 0)
                      .OrderByDescending(x => x.n).ThenBy(x => x.p.Text.Length).Take(max).Select(x => x.p)];
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : (float)(dot / Math.Sqrt(na * nb));
    }

    // ---------- the index ----------
    public static async Task Update(CancellationToken ct, bool rebuild = false)
    {
        var folder = Planner.S.KnowledgeFolder;
        if (!Directory.Exists(folder)) { index = null; Status = ""; return; }
        await gate.WaitAsync(ct);
        try
        {
            var model = await EmbedModel(ct);
            index ??= Load();
            if (rebuild || index == null || index.Folder != folder || index.Model != model) index = new(folder, model, [], []);
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                                 .Where(f => Docs.CanRead(f) && !Regex.IsMatch(f, @"[\\/](\.git|node_modules|\.obsidian)[\\/]") && new FileInfo(f).Length < 20_000_000)
                                 .ToDictionary(f => Path.GetRelativePath(folder, f), f => File.GetLastWriteTimeUtc(f));
            var gone = index.Files.Keys.Where(k => !files.ContainsKey(k)).ToList();
            var changed = files.Where(f => index.Files.GetValueOrDefault(f.Key) != f.Value).Select(f => f.Key).ToList();
            if (gone.Count == 0 && changed.Count == 0) return;
            index.Passages.RemoveAll(p => gone.Contains(p.File) || changed.Contains(p.File));
            foreach (var g in gone) index.Files.Remove(g);
            var fresh = new List<Passage>();
            foreach (var rel in changed)
            {
                string text; try { text = Docs.Read(Path.Combine(folder, rel)); } catch { continue; }
                fresh.AddRange(Cut(rel, text));
            }
            if (model != "")   // vectors in batches; a failure leaves those passages to word search
                for (int i = 0; i < fresh.Count; i += 32)
                {
                    var batch = fresh.GetRange(i, Math.Min(32, fresh.Count - i));
                    var vecs = await Embed([.. batch.Select(p => p.Text)], ct);
                    if (vecs == null) break;
                    for (int j = 0; j < batch.Count && j < vecs.Count; j++) fresh[i + j] = batch[j] with { Vec = vecs[j] };
                }
            index.Passages.AddRange(fresh);
            foreach (var rel in changed) index.Files[rel] = files[rel];
            Save();
        }
        finally
        {
            Status = index == null ? "" : $"{index.Passages.Count} passages from {index.Files.Count} files{(index.Passages.Any(p => p.Vec != null) ? " · search by meaning" : " · word search")}";
            gate.Release();
        }
    }

    // passages of ~800 characters, cut at blank lines where possible, each with the line it starts on
    public static IEnumerable<Passage> Cut(string file, string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var buf = new System.Text.StringBuilder(); var start = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (buf.Length == 0) start = i + 1;
            buf.AppendLine(lines[i]);
            if (buf.Length >= 800 || (buf.Length >= 400 && lines[i].Trim() == "") || i == lines.Length - 1)
            {
                var t = buf.ToString().Trim();
                if (t.Length > 0) yield return new(file, start, t.Length > 1600 ? t[..1600] : t, null);
                buf.Clear();
            }
        }
    }

    // sealed like chats while the app lock is on (it holds passages of your notes)
    static Index? Load()
    {
        try { return File.Exists(IndexPath) && Store.ReadData(IndexPath) is { } t ? JsonSerializer.Deserialize<Index>(t) : null; }
        catch { return null; }
    }
    static void Save()
    {
        try { Store.WriteData(IndexPath, JsonSerializer.Serialize(index)); }
        catch (Exception ex) { Store.Fail("Knowledge index", ex.Message); }
    }

    // ---------- vectors ----------
    // OpenAI-compatible providers (OpenAI, Ollama, LM Studio, Mistral, Together…) take POST /embeddings; Gemini has its own call.
    // The embedding model: the one set in Settings, or, when none is, an embedding model Ollama already has on this PC
    // (search by meaning without a cloud key). "" means word search.
    static string? localEmbed;
    static async Task<string> EmbedModel(CancellationToken ct)
    {
        var s = Planner.S;
        if (s.EmbedModel != "") return s.EmbedModel;
        if (localEmbed != null) return localEmbed;
        localEmbed = "";
        if (s.Providers.FirstOrDefault(p => p.BaseUrl.Contains("localhost:11434")) is { } ollama)
            try
            {
                using var http = new HttpClient(Net.Handler()) { Timeout = TimeSpan.FromSeconds(3) };
                var tags = JsonNode.Parse(await http.GetStringAsync("http://localhost:11434/api/tags", ct));
                if (tags?["models"]?.AsArray().Select(m => (string?)m?["name"] ?? "").FirstOrDefault(n => n.Contains("embed", StringComparison.OrdinalIgnoreCase)) is { Length: > 0 } name)
                    localEmbed = $"{ollama.Name}/{name}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }   // Ollama isn't running: word search
        return localEmbed;
    }

    static async Task<List<float[]>?> Embed(List<string> texts, CancellationToken ct)
    {
        var s = Planner.S;
        if (await EmbedModel(ct) is not { Length: > 0 } chosen || chosen.Split('/', 2) is not [var prov, var model]) return null;
        var p = s.Providers.FirstOrDefault(x => x.Name == prov);
        if (p == null) return null;
        var key = Router.Keys(Store.Unprotect(p.Key)).FirstOrDefault() ?? "";
        try
        {
            if (p.Kind == "gemini")
            {
                var body = new JsonObject { ["requests"] = new JsonArray([.. texts.Select(t => (JsonNode)new JsonObject
                    { ["model"] = "models/" + model, ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = t }) } })]) };
                var j = await Api.Call(Api.Req(HttpMethod.Post, $"{p.BaseUrl}/models/{model}:batchEmbedContents", new() { ["x-goog-api-key"] = key }, body));
                return [.. j["embeddings"]!.AsArray().Select(e => e!["values"]!.AsArray().Select(v => (float)v!).ToArray())];
            }
            if (p.Kind == "anthropic") throw new InvalidOperationException("Anthropic has no embedding models; pick one from another provider.");
            var b = new JsonObject { ["model"] = model, ["input"] = new JsonArray([.. texts.Select(t => (JsonNode)t)]) };
            var r = await Api.Call(Api.Req(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/embeddings", key == "" ? new() : Api.Bearer(key), b));
            return [.. r["data"]!.AsArray().OrderBy(d => (int?)d!["index"] ?? 0).Select(d => d!["embedding"]!.AsArray().Select(v => (float)v!).ToArray())];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Store.Fail($"Embeddings · {prov} · {model}", ex.Message);
            return null;
        }
    }
}
