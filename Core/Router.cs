using System.Text.RegularExpressions;

namespace Ivo;

// Auto: three routes that each pick from the models that passed their check, trying the next one when a model fails
// (rate limit, out of credit, down) before it has written anything. Also the key pools: several keys for one provider.
public static class Router
{
    public const string Name = "Auto";
    public static readonly string[] Routes = ["auto/best-coding", "auto/best-chat", "auto/fast"];
    public static readonly string[] Virtual = ["auto", "auto/free"];   // on one provider: its best working model, or its best free one
    static readonly Regex FreeRx = new(@"free", RegexOptions.IgnoreCase);
    public static readonly ProviderCfg Cfg = new() { Name = Name, Kind = "auto", OnlyWorking = false };

    // models that failed recently sit out for a while, so the next message doesn't wait on them again
    static readonly Dictionary<string, DateTime> resting = [];

    // any model that just failed (Auto's picks, a chat's own model, a daily task's) sits out the same way, so the next
    // request goes straight to one that works; it comes back on its own when the rest is over
    public static void Rest(string provider, string model, string error)
    {
        lock (resting) resting[$"{provider}|{model}"] = DateTime.Now.AddMinutes(Exhausted(error) ? Planner.S.AutoRestMinutes : Math.Min(2, Planner.S.AutoRestMinutes));
    }
    public static bool IsResting(string provider, string model) { lock (resting) return resting.TryGetValue($"{provider}|{model}", out var until) && until > DateTime.Now; }
    static readonly Dictionary<string, int> turn = [];

    // A model marked as not taking tools: for good when the model check said so ("1"), for a week when a chat found out (a date),
    // since a transient error can look like "no tools" and models gain tool support.
    public static bool NoTools(string provider, string model) =>
        Cli.Status.TryGetValue($"{provider}|#notools|{model}", out var v) && (v == "1" || DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && at > DateTime.Now.AddDays(-7));

    public static string[] Keys(string key) => key.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static int NextKey(string provider, int count) { lock (turn) return turn[provider] = (turn.GetValueOrDefault(provider, -1) + 1) % count; }

    // worth trying another key or model: limits, quota, credit, auth, the provider being down
    // a short reply that is really the provider saying the account ran dry
    public static bool OutOfCredits(string text) => text.Trim().Length < 600 &&
        Regex.IsMatch(text, @"(doesn't|does not|don't) have enough (credits|balance)|not enough credits|insufficient (credits|balance|funds|quota)|out of credits|quota (exceeded|exhausted)", RegexOptions.IgnoreCase);

    public static bool Exhausted(string error) =>
        Regex.IsMatch(error, @"\b(429|401|402|403|5\d\d)\b|rate.?limit|quota|credit|exhausted|overloaded|unavailable|not available|too many requests|insufficient|unauthori[sz]ed|invalid.*key", RegexOptions.IgnoreCase);

    static readonly Regex NotChat = new(@"embed|whisper|tts|speech|audio|image|flux|stable-diffusion|dall-e|sdxl|moderation|guard|rerank|ocr|transcri", RegexOptions.IgnoreCase);
    static readonly Regex Coder = new(@"coder|codestral|devstral|code|claude|gpt-5|gpt-4\.1|o3|o4|deepseek|kimi|qwen3|glm|grok-4", RegexOptions.IgnoreCase);
    static readonly Regex Strong = new(@"opus|sonnet|gpt-5|gpt-4\.1|gemini-.*pro|grok-4|deepseek|kimi|large|405b|235b|120b|70b|72b|qwen3", RegexOptions.IgnoreCase);
    static readonly Regex Small = new(@"mini|nano|lite|flash|haiku|small|tiny|\b[1-9]b\b|1[0-4]b", RegexOptions.IgnoreCase);

    static double Seconds(string provider, string model) =>
        Cli.Status.TryGetValue($"{provider}|#time|{model}", out var t) && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 30;

    // ponytail: name-based ranking, no benchmark data; swap in scores per model if picks feel wrong
    public static List<(ProviderCfg P, string Model)> Candidates(string route, bool tools, ProviderCfg? only = null, bool free = false)
    {
        var list = new List<(ProviderCfg P, string Model, double Score)>();
        foreach (var p in Planner.S.Providers.Where(p => only != null ? p.Name == only.Name : p.Kind is not ("cli" or "auto") && !Planner.S.AutoSkip.Contains(p.Name) && !Cli.RecentlyLimited(p)))
            foreach (var (k, v) in Cli.Status.Where(x => x.Value == "ok" && x.Key.StartsWith(p.Name + "|") && !x.Key.Contains("|#")).ToList())
            {
                var m = k[(p.Name.Length + 1)..];
                if (NotChat.IsMatch(m) || Virtual.Contains(m) || (tools && NoTools(p.Name, m))) continue;
                lock (resting) if (resting.TryGetValue($"{p.Name}|{m}", out var until) && until > DateTime.Now) continue;
                var secs = Seconds(p.Name, m);
                var score = route switch
                {
                    "auto/fast" => -secs + (Small.IsMatch(m) ? 2 : 0),
                    "auto/best-coding" => (Coder.IsMatch(m) ? 20 : 0) + (Strong.IsMatch(m) ? 10 : 0) - (Small.IsMatch(m) ? 8 : 0) - secs / 10,
                    _ => (Strong.IsMatch(m) ? 20 : 0) - (Small.IsMatch(m) ? 6 : 0) - secs / 5,
                };
                score += Math.Min(Planner.S.Preferred.GetValueOrDefault($"{p.Name}|{m}"), 10) * 1.5;   // picked as best in /compare: a nudge, capped
                list.Add((p, m, score));
            }
        // ponytail: "free" means the id says so (OpenRouter's :free); a provider with no such ids is taken as free-tier, all of it
        if (free && list.Any(x => FreeRx.IsMatch(x.Model))) list = list.Where(x => FreeRx.IsMatch(x.Model)).ToList();
        return list.OrderByDescending(x => x.Score).Select(x => (x.P, x.Model)).ToList();
    }

    public static async Task<Api.Reply> Stream(Chat chat, List<ToolDef>? tools, Action<string> onDelta, Action<string>? onThought, CancellationToken ct, ProviderCfg? only = null, bool free = false)
    {
        var chosen = chat.Model;   // "auto" / "auto/free" on a provider, or a route: put back after
        var route = Routes.Contains(chat.Model) ? chat.Model! : "auto/best-chat";
        var picks = Candidates(route, tools?.Count > 0, only, free).Take(Math.Max(1, Planner.S.AutoTries)).ToList();
        if (picks.Count == 0) throw new Exception(only == null ? "No working models yet. Run Settings → Scan this PC → Check models." : $"No working{(free ? " free" : "")} models known on {only.Name} yet. Check its models in Settings → Models.");
        var errors = new List<string>();
        foreach (var (p, m) in picks)
        {
            var sent = false;
            chat.Model = m;   // the request is built from the chat; put the route back after
            try
            {
                onThought?.Invoke($"▸ {p.Name} · {m}\n");
                // the start is held back: some providers (Pollinations) send "not enough credits" as an ordinary answer,
                // which must fail over like an error instead of reaching the chat or the briefing
                var held = new System.Text.StringBuilder();
                var r = await Api.Stream(p, Store.Unprotect(p.Key), chat, tools, d =>
                {
                    if (sent) { onDelta(d); return; }
                    held.Append(d);
                    if (held.Length > 600) { sent = true; onDelta(held.ToString()); }
                }, onThought, ct);
                if (!sent && r.Calls.Count == 0 && OutOfCredits(r.Text)) throw new Exception(r.Text.Trim().Split('\n')[0]);
                if (!sent && held.Length > 0) onDelta(held.ToString());
                return r;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !sent)
            {
                Rest(p.Name, m, ex.Message);
                errors.Add($"{p.Name} · {m}: {ex.Message.Split('\n')[0]}");
            }
            finally { chat.Model = chosen; }
        }
        throw new Exception("Every model Auto tried failed:\n" + string.Join("\n", errors));
    }
}
