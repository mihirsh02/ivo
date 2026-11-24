using System.Text;
using System.Text.RegularExpressions;

namespace Ivo;

// Keeps a long chat within the model's memory: what's sent is a summary of the older messages plus the recent ones.
// The saved chat keeps everything; only the request shrinks.
public static class Context
{
    // ponytail: one budget for every model (most today take 128k+ tokens); per-model limits if a small model overflows
    public const int Limit = 120_000;

    public static IEnumerable<Msg> Visible(Chat c) => c.Summary == null || c.SummaryUpTo <= 0 ? c.Messages :
        [new("user", "Summary of our conversation so far (older messages were condensed to save space):\n\n" + c.Summary),
         new("assistant", "Understood, I have that context."), .. c.Messages.Skip(c.SummaryUpTo)];

    // ~4 characters a token; pictures count roughly what providers charge for them
    static int Tokens(Msg m) => (m.Text.Length + (m.Thought?.Length ?? 0) / 2 + (m.Calls?.Sum(x => x.Args.Length + 20) ?? 0)) / 4 + (m.Images?.Count ?? 0) * 1500;
    public static double Fill(Chat c) => Visible(c).Sum(Tokens) / (double)Limit;
    public static bool Needed(Chat c) => Fill(c) > 0.7;

    // The error providers give when a request is over the model's limit.
    public static bool TooLong(string error) => Regex.IsMatch(error,
        @"context.{0,20}(length|window|limit)|maximum context|too many tokens|prompt is too long|input is too long|exceeds the (model'?s )?(context|maximum)|reduce the length",
        RegexOptions.IgnoreCase);

    // Summarises the older part with the chat's own model, keeping about a third of the budget of recent messages.
    // The cut is always at a message the user sent, so a tool call and its result are never split.
    public static async Task<bool> Compact(ProviderCfg cfg, string key, Chat c, CancellationToken ct)
    {
        int cut = -1, keep = 0;
        for (int i = c.Messages.Count - 1; i > Math.Max(0, c.SummaryUpTo); i--)
        {
            keep += Tokens(c.Messages[i]);
            if (keep > Limit * 0.3 && cut != -1) break;
            if (c.Messages[i].Role == "user") cut = i;
        }
        if (cut <= Math.Max(0, c.SummaryUpTo)) return false;

        var transcript = new StringBuilder();
        foreach (var m in c.Messages.Skip(Math.Max(0, c.SummaryUpTo)).Take(cut - Math.Max(0, c.SummaryUpTo)))
        {
            if (m.Role == "tool") { transcript.Append($"[result of {m.Name}: {Clip(m.Text, 400)}]\n"); continue; }
            if (m.Text != "") transcript.Append(m.Role == "user" ? "User: " : "Assistant: ").Append(m.Text).Append("\n\n");
            foreach (var call in m.Calls ?? []) transcript.Append($"[used {call.Name}: {Clip(call.Args, 300)}]\n");
        }
        // a very long old part is trimmed from its oldest end; the earlier summary always goes in whole
        var earlier = c.Summary != null ? "Earlier summary:\n" + c.Summary + "\n\n" : "";
        var text = transcript.ToString();
        var room = Math.Max(10_000, Limit * 3 - earlier.Length);
        if (text.Length > room) text = "…" + text[^room..];
        text = earlier + text;

        var ask = new Chat
        {
            Provider = c.Provider, Model = c.Model, Effort = "off", Temp = 0.2,
            Messages = [new("user", "Summarise the conversation below so it can continue without it. Keep every fact, decision, preference, name, " +
                                    "file path, number and open task; drop pleasantries. Write compact notes.\n\n" + text)],
        };
        var r = await Api.Stream(cfg, key, ask, null, _ => { }, null, ct);
        if (r.Text.Trim() == "") return false;
        c.Summary = r.Text.Trim();
        c.SummaryUpTo = cut;
        return true;
    }

    static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
