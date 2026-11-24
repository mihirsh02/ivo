using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

// What a message means, as one of Ivo's intents, each with example messages, weighted words (for when a meaning she
// looked up is all she has: "a casual way to say hello" → hello:3 → greeting) and replies by her mood: any, cute,
// cheeky, flirty, naughty, rude, mad, sulky, sorry, caring, hyped. Built in: Core/IvoIntents.json.
public sealed record Intent(string Id, string[] Examples, Dictionary<string, int> Words, Dictionary<string, string[]> Replies);

public static class IvoIntents
{
    public const double Sure = 0.6;   // the score an intent needs before she answers with it

    static readonly Lazy<List<Intent>> builtIn = new(() =>
    {
        using var file = typeof(IvoIntents).Assembly.GetManifestResourceStream("Ivo.IvoIntents.json");
        if (file == null || JsonNode.Parse(file)?["intents"] is not JsonArray all) return [];
        return all.OfType<JsonObject>().Select(o => new Intent(
            (string)o["id"]!,
            o["examples"]!.AsArray().Select(x => (string)x!).ToArray(),
            Weights((string?)o["words"] ?? ""),
            o["replies"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.AsArray().Select(x => (string)x!).ToArray())
        )).ToList();
    });
    public static IReadOnlyList<Intent> BuiltIn => builtIn.Value;
    public static Intent? Get(string id) => BuiltIn.FirstOrDefault(i => i.Id == id);

    public static Dictionary<string, int> Weights(string words) =>
        words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Split(':')).ToDictionary(p => p[0], p => p.Length > 1 ? int.Parse(p[1]) : 1);

    // the intent that fits best, and how well (0..1): the closest example message (words in common, a typo apart
    // still counts), or its weighted words adding up to 3
    public static (Intent? Best, double Score) Best(IEnumerable<Intent> all, IReadOnlyList<string> words)
    {
        var said = words.Select(w => Stem(w.Trim('?', '!', '.', ','))).Where(w => w != "").ToList();
        if (said.Count == 0) return (null, 0);
        Intent? best = null; double top = 0;
        foreach (var intent in all)
        {
            var score = Math.Min(1, intent.Words.Sum(w => said.Contains(Stem(w.Key)) ? w.Value : 0) / 3.0);
            var heard = said.Where(w => !Small.Contains(w)).ToList();
            foreach (var ex in intent.Examples)
            {
                // the words that carry the meaning: "you are mean" is about "mean", so "you are indeed" isn't it
                var whole = IvoMind.Words(ex).Select(Stem).ToList();
                var e = whole.Where(w => !Small.Contains(w)).ToList() is { Count: > 0 } meant ? meant : whole;
                var against = e == whole ? said : heard;
                if (against.Count == 0) continue;
                var hits = e.Count(x => against.Any(s => Near(s, x)));
                score = Math.Max(score, (double)hits / Math.Max(e.Count, against.Count));
                // every word of a longer example is there, with a few more around it ("what will you do today")
                if (e.Count >= 2 && hits >= e.Count - (e.Count >= 5 ? 1 : 0)) score = Math.Max(score, .85 * hits / e.Count);
            }
            if (score > top) { top = score; best = intent; }
        }
        return (best, top);
    }

    // little words that don't carry what a message is about
    static readonly HashSet<string> Small = ["i", "you", "me", "my", "your", "it", "is", "are", "am", "the", "a", "an", "to", "of", "and", "or", "that", "this", "be", "so", "very", "really", "just", "we", "us"];

    // a word without its ending, so "doing", "does" and "do", "plans" and "plan" meet
    static string Stem(string w) => w.Length > 5 && w.EndsWith("ing") ? w[..^3] : w.Length > 4 && w.EndsWith("es") ? w[..^2] : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w;

    static bool Near(string a, string b) => a == b || a.Length >= 4 && b.Length >= 4 && Math.Abs(a.Length - b.Length) <= 1 && Distance(a, b) <= 1;
    static int Distance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0]; d[0] = i;
            for (var j = 1; j <= b.Length; j++) { var t = d[j]; d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1)); prev = t; }
        }
        return d[b.Length];
    }

    // replies for her mood, falling back to "any" (a behaviour pack has any flirty, naughty or rude ones: cheeky and mad here)
    public static string[] RepliesFor(Intent intent, string mood)
    {
        if (mood is "flirty" or "naughty" or "rude") mood = mood == "rude" ? "mad" : "cheeky";
        return intent.Replies.TryGetValue(mood, out var r) && r.Length > 0 ? r : intent.Replies.GetValueOrDefault("any", ["💜"]);
    }
}
