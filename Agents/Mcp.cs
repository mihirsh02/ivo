using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

// MCP (Model Context Protocol) servers give the agent extra tools. Configured with the same "mcpServers" JSON as
// Claude Desktop: "command" runs a local program speaking JSON-RPC one message per line; "url" (+ "headers")
// talks to a remote server or MCP gateway over Streamable HTTP.
public static class Mcp
{
    public record Tool(McpServer Server, string Name, ToolDef Def);

    public static readonly List<McpServer> Servers = [];
    public static Func<string, JsonNode, Task<bool>>? Approve;   // set by the window: asks the user, remembers the answer
    public static IEnumerable<ToolDef> Tools => Servers.Where(s => s.Ready).SelectMany(s => s.Tools.Select(t => t.Def));
    public static Tool? Find(string exposed) => Servers.Where(s => s.Ready).SelectMany(s => s.Tools).FirstOrDefault(t => t.Def.Name == exposed);

    // Stops whatever runs and starts the configured servers; changed() runs (on the UI thread) as each one settles.
    public static async Task Start(string json, Action changed)
    {
        StopAll();
        var root = JsonNode.Parse(json.Trim() == "" ? "{}" : json) as JsonObject ?? throw new Exception("The config must be a JSON object.");
        var map = root["mcpServers"] as JsonObject ?? root;
        foreach (var (name, cfg) in map) Servers.Add(new McpServer(name));
        changed();
        // a server runs with your rights: asked once, and again whenever its command, address or settings change
        var allowed = new List<McpServer>();
        foreach (var s in Servers.ToList())
        {
            if (Approve == null || await Approve(s.Name, map[s.Name]!)) allowed.Add(s);
            else s.Fail("Not allowed to run. Save the config again to be asked again.");
        }
        changed();
        await Task.WhenAll(allowed.Select(async s =>
        {
            try { await s.Start(map[s.Name]!); }
            catch (Exception ex) { s.Fail(ex.Message); }
            changed();
        }));
    }

    public static void StopAll()
    {
        foreach (var s in Servers) s.Stop();
        Servers.Clear();
    }
}

public class McpServer(string name)
{
    public string Name { get; } = name;
    public string Status { get; private set; } = "starting…";
    public bool Ready { get; private set; }
    public List<Mcp.Tool> Tools { get; } = [];

    Process? p;
    StreamWriter? input;                          // the process's stdin (its own pipe when sandboxed)
    string? url;                                  // remote server (Streamable HTTP) instead of a process
    Dictionary<string, string> headers = [];
    string? session;                              // Mcp-Session-Id the server handed out
    static readonly HttpClient Http = new(Net.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
    int nextId;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> waiting = new();
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly Queue<string> stderr = new();

    public async Task Start(JsonNode cfg)
    {
        if ((string?)cfg["url"] is { } u) await StartRemote(cfg, u); else StartLocal(cfg);

        await Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Ivo", ["version"] = "1.0" },
        }, TimeSpan.FromSeconds(60), CancellationToken.None);
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, CancellationToken.None);
        await ListTools();
    }

    Task StartRemote(JsonNode cfg, string address)
    {
        if ((string?)cfg["type"] == "sse") throw new Exception("Old-style SSE servers aren't supported. Use the server's Streamable HTTP address (it often ends in /mcp).");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) throw new Exception("\"url\" must be an http(s) address.");
        url = address;
        headers = (cfg["headers"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => (string?)kv.Value ?? "");
        return Task.CompletedTask;
    }

    void StartLocal(JsonNode cfg)
    {
        var command = (string?)cfg["command"] ?? throw new Exception("Give a \"command\" to run, or a \"url\" for a remote server.");
        var args = cfg["args"]?.AsArray().Select(a => (string?)a ?? "").ToList() ?? [];
        var (file, prefix) = Resolve(command);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        var env = (cfg["env"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => (string?)kv.Value ?? "");
        StreamReader output, errors;
        if ((bool?)cfg["sandbox"] == true)   // Low integrity: can't write to your files, settings or registry (see LowIntegrity)
        {
            var started = LowIntegrity.Start(file, prefix.Concat(args), env, null);
            (p, input, output, errors) = (started.Process, started.Input, started.Output, started.Errors);
        }
        else
        {
            foreach (var a in prefix.Concat(args)) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env) psi.Environment[k] = v;
            p = Process.Start(psi) ?? throw new Exception("Couldn't start " + command);
            (input, output, errors) = (p.StandardInput, p.StandardOutput, p.StandardError);
        }
        _ = ReadLoop(output);
        _ = Task.Run(async () =>
        {
            while (await errors.ReadLineAsync() is { } l) lock (stderr) { stderr.Enqueue(l); if (stderr.Count > 5) stderr.Dequeue(); }
        });
    }

    async Task ListTools()
    {
        string? cursor = null;
        do
        {
            var page = await Request("tools/list", cursor == null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, TimeSpan.FromSeconds(30), CancellationToken.None);
            foreach (var t in page["tools"]?.AsArray() ?? [])
            {
                var tool = (string?)t?["name"] ?? "";
                if (tool == "") continue;
                // Provider tool names allow letters, digits, _ and - (64 max), and must be unique across servers.
                var exposed = Regex.Replace($"{Name}__{tool}", "[^A-Za-z0-9_-]", "_");
                if (exposed.Length > 64) exposed = exposed[..64];
                var schema = Clean(t?["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
                var desc = ((string?)t?["description"] ?? tool) + $" (from the {Name} MCP server)";
                Tools.Add(new(this, tool, new ToolDef(exposed, desc, schema.ToJsonString(), (bool?)t?["annotations"]?["readOnlyHint"] == true)));
            }
            cursor = (string?)page["nextCursor"];
        } while (cursor != null);
        Ready = true;
        Status = $"{Tools.Count} tool{(Tools.Count == 1 ? "" : "s")}";
    }

    public async Task<string> Call(string tool, JsonNode args, CancellationToken ct)
    {
        var r = await Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args.DeepClone() }, TimeSpan.FromMinutes(2), ct);
        var text = string.Join("\n", (r["content"]?.AsArray() ?? []).Select(c => (string?)c?["type"] switch
        {
            "text" => (string?)c?["text"],
            "resource" => (string?)c?["resource"]?["text"] ?? $"[resource {(string?)c?["resource"]?["uri"]}]",
            "image" => "[image]",
            var other => $"[{other}]",
        }));
        if (text == "" && r["structuredContent"] is { } sc) text = sc.ToJsonString();
        return (bool?)r["isError"] == true ? "Error: " + text : text == "" ? "(no output)" : text;
    }

    public void Fail(string why)
    {
        Ready = false;
        string tail; lock (stderr) tail = string.Join(" ", stderr).Trim();
        Status = "✕ " + why + (tail != "" ? " · " + (tail.Length > 200 ? tail[..200] + "…" : tail) : "");
        Stop();
    }

    public void Stop()
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
        p = null;
        url = null;
    }

    // Sends a request and waits for its answer. Over HTTP the answer comes back on the same call (as JSON or a
    // short event stream), so the send itself can finish the wait.
    async Task<JsonNode> Request(string method, JsonObject prms, TimeSpan limit, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = tcs;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            var sending = Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms }, timeout.Token);
            if (await Task.WhenAny(tcs.Task, sending).WaitAsync(timeout.Token) == sending) await sending;   // a failed send throws here
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new Exception($"{Name} didn't answer {method} within {limit.TotalSeconds:0} seconds."); }
        finally { waiting.TryRemove(id, out _); }
    }

    Task Send(JsonObject msg) => Send(msg, CancellationToken.None);

    async Task Send(JsonObject msg, CancellationToken ct)
    {
        if (url != null) { await Post(msg, ct); return; }
        await writeGate.WaitAsync(ct);
        try
        {
            if (p is not { HasExited: false }) throw new Exception($"The {Name} server isn't running.");
            await input!.WriteLineAsync(msg.ToJsonString());
            await input.FlushAsync();
        }
        finally { writeGate.Release(); }
    }

    async Task Post(JsonObject msg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        if (session != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.Headers.TryGetValues("Mcp-Session-Id", out var sid)) session = sid.First();
        if (!res.IsSuccessStatusCode)
            throw new Exception($"{Name} answered HTTP {(int)res.StatusCode}" + ((int)res.StatusCode is 401 or 403 ? " (check the key or token in \"headers\")" : ""));
        if (res.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
            var id = (int?)msg["id"];
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                try { if (JsonNode.Parse(line[5..]) is { } m) Handle(m); } catch (System.Text.Json.JsonException) { }
                if (id == null || !waiting.TryGetValue(id.Value, out var t) || t.Task.IsCompleted) break;   // our answer is in
            }
            return;
        }
        var body = await res.Content.ReadAsStringAsync(ct);
        if (body.Trim() == "") return;   // 202 for notifications
        var node = JsonNode.Parse(body);
        if (node is JsonArray batch) foreach (var m in batch) { if (m != null) Handle(m); }
        else if (node != null) Handle(node);
    }

    async Task ReadLoop(StreamReader output)
    {
        try
        {
            while (await output.ReadLineAsync() is { } line)
            {
                JsonNode? m;
                try { m = JsonNode.Parse(line); } catch { continue; }   // servers sometimes log to stdout
                if (m != null) Handle(m);
            }
        }
        catch { }
        foreach (var t in waiting.Values) t.TrySetException(new Exception($"The {Name} server stopped."));
        if (Ready) Fail("stopped");
    }

    // An answer to one of our requests, or a request from the server (ping, roots, sampling…) that we answer
    // so it never waits on us.
    void Handle(JsonNode m)
    {
        if (m["method"] is { } method)
        {
            if (m["id"] is { } rid)
                _ = Send((string?)method == "ping"
                    ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = rid.DeepClone(), ["result"] = new JsonObject() }
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = rid.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported by Ivo" } });
            return;
        }
        if ((int?)m["id"] is not { } id || !waiting.TryGetValue(id, out var tcs)) return;
        if (m["error"] is { } err) tcs.TrySetException(new Exception((string?)err["message"] ?? err.ToJsonString()));
        else tcs.TrySetResult(m["result"] ?? new JsonObject());
    }

    // npx, uvx and friends are .cmd scripts on Windows, which only start through cmd.exe.
    static (string File, string[] Prefix) Resolve(string command)
    {
        if (Path.IsPathRooted(command) || Path.HasExtension(command))
            return Path.GetExtension(command).ToLowerInvariant() is ".cmd" or ".bat" ? ("cmd.exe", ["/c", command]) : (command, []);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                var f = Path.Combine(dir.Trim(), command + ext);
                if (File.Exists(f)) return ext == ".exe" ? (f, []) : ("cmd.exe", ["/c", f]);
            }
        return (command, []);
    }

    // Gemini rejects some JSON-schema keywords that MCP servers commonly include.
    static JsonObject Clean(JsonObject schema)
    {
        foreach (var k in new[] { "$schema", "additionalProperties", "default", "examples", "$id" }) schema.Remove(k);
        foreach (var (k, v) in schema.ToList())
            if (k is "properties" or "$defs" or "definitions" && v is JsonObject named) { foreach (var (_, x) in named) if (x is JsonObject xo) Clean(xo); }   // keys here are names, not keywords
            else if (v is JsonObject o) Clean(o);
            else if (v is JsonArray arr) foreach (var x in arr) if (x is JsonObject xo) Clean(xo);
        return schema;
    }
}
