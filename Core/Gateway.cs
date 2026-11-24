using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ivo;

// Ivo as a gateway: an OpenAI-compatible server on this PC, so other apps can use the models (and Auto) set up here.
// Only this PC can reach it, and every request needs the gateway's key. Chats through it are plain: no memory, profile or skills.
public static class Gateway
{
    static HttpListener? listener;
    public static string Url(int port) => $"http://localhost:{port}/v1";

    public static void Apply(Settings s)
    {
        listener?.Close(); listener = null;
        if (!s.GatewayOn) return;
        if (s.GatewayKey == "") s.GatewayKey = "ivo-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{s.GatewayPort}/");
        listener.Start();
        var l = listener;
        _ = Task.Run(async () =>
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await l.GetContextAsync(); } catch { return; }   // stopped
                _ = Task.Run(() => Handle(ctx, s));
            }
        });
    }

    static async Task Handle(HttpListenerContext ctx, Settings s)
    {
        var res = ctx.Response;
        try
        {
            if (ctx.Request.Headers["Authorization"] != "Bearer " + s.GatewayKey) { await Json(res, 401, Error("Wrong or missing API key.")); return; }
            var path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
            if (ctx.Request.HttpMethod == "GET" && path == "/v1/models")
            {
                var ids = Router.Routes.Concat(s.Providers.Where(p => p.Kind != "cli")
                    .SelectMany(p => Cli.Status.Where(x => x.Value == "ok" && x.Key.StartsWith(p.Name + "|") && !x.Key.Contains("|#")).Select(x => x.Key.Replace("|", "/"))));
                await Json(res, 200, new JsonObject { ["object"] = "list", ["data"] = new JsonArray([.. ids.Select(i => (JsonNode)new JsonObject { ["id"] = i, ["object"] = "model", ["owned_by"] = "ivo" })]) });
                return;
            }
            if (ctx.Request.HttpMethod != "POST" || path != "/v1/chat/completions") { await Json(res, 404, Error("Only /v1/models and /v1/chat/completions.")); return; }

            var body = JsonNode.Parse(await new StreamReader(ctx.Request.InputStream).ReadToEndAsync())!;
            var model = (string?)body["model"] ?? Router.Routes[1];
            // "auto/…" → Auto; "Provider/model" → that provider (the model may have slashes of its own)
            ProviderCfg cfg; string id;
            if (model.StartsWith("auto/") || model == "auto") (cfg, id) = (Router.Cfg, model == "auto" ? Router.Routes[1] : model);
            else if (s.Providers.Where(p => p.Kind != "cli").OrderByDescending(p => p.Name.Length).FirstOrDefault(p => model.StartsWith(p.Name + "/")) is { } p) (cfg, id) = (p, model[(p.Name.Length + 1)..]);
            else { await Json(res, 404, Error($"No model {model}. See /v1/models.")); return; }

            var chat = new Chat { Provider = cfg.Name, Model = id, Incognito = true, Raw = true, Temp = (double?)body["temperature"] ?? 0.7 };
            foreach (var m in body["messages"]?.AsArray() ?? [])
            {
                var role = (string?)m?["role"] ?? "user";
                var text = m?["content"] is JsonArray parts ? string.Concat(parts.Select(x => (string?)x?["text"])) : (string?)m?["content"] ?? "";
                if (role is "system" or "developer") chat.System += (chat.System == "" ? "" : "\n\n") + text;
                else if (role is "user" or "assistant") chat.Messages.Add(new Msg(role, text));
            }

            var stream = (bool?)body["stream"] == true;
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var rid = "chatcmpl-" + Guid.NewGuid().ToString("N");
            JsonObject Chunk(JsonObject delta, string? finish) => new() { ["id"] = rid, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }) };
            if (stream)
            {
                res.ContentType = "text/event-stream"; res.SendChunked = true;
                var w = new StreamWriter(res.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
                await Api.Stream(cfg, Store.Unprotect(cfg.Key), chat, null, d => w.Write($"data: {Chunk(new() { ["content"] = d }, null).ToJsonString()}\n\n"), null, CancellationToken.None);
                await w.WriteAsync($"data: {Chunk(new(), "stop").ToJsonString()}\n\ndata: [DONE]\n\n");
                res.Close();
                return;
            }
            var r = await Api.Stream(cfg, Store.Unprotect(cfg.Key), chat, null, _ => { }, null, CancellationToken.None);
            await Json(res, 200, new JsonObject { ["id"] = rid, ["object"] = "chat.completion", ["created"] = created, ["model"] = model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = r.Text }, ["finish_reason"] = "stop" }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = r.Usage?.In ?? 0, ["completion_tokens"] = r.Usage?.Out ?? 0, ["total_tokens"] = (r.Usage?.In ?? 0) + (r.Usage?.Out ?? 0) } });
        }
        catch (Exception ex) { try { await Json(res, 502, Error(ex.Message)); } catch { } }
    }

    static JsonObject Error(string message) => new() { ["error"] = new JsonObject { ["message"] = message, ["type"] = "ivo_gateway" } };

    static async Task Json(HttpListenerResponse res, int status, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        res.StatusCode = status; res.ContentType = "application/json";
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }
}
