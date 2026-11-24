using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ivo;

// A dataset is a small, topic-specific set of example inputs and useful replies. Ivo checks it alongside her built-in
// knowledge, so keep entries focused and write replies in the voice and format you want the model to learn.
// Ivo-compatible JSON:
//   { "name": "Everyday science", "entries": [
//     { "match": ["why is the sky blue", "what makes the sky look blue"], "reply": ["Air scatters blue light from the sun more than most other colours."] },
//     { "words": "photosynthesis:3 plant:1", "reply": ["Photosynthesis is how plants use light to turn water and carbon dioxide into stored energy."], "more": "Chlorophyll captures the light." }
//   ] }
// Each entry needs at least one match phrase or weighted keyword and one reply. "reply" can be a string or a list of
// alternatives; "more" is an optional follow-up. "{name}" can stand for the user's name. Ivo also accepts optional
public sealed class IvoDataset
{
    public sealed record Entry(List<HashSet<string>> Phrases, Dictionary<string, int> Words, string[] Replies, string More);

    public string Name { get; init; } = "";
    public List<Entry> Entries { get; } = [];
    public int Skipped { get; private set; }

    // a dataset from its JSON; throws when it isn't one at all, skips the entries it can't use
    public static IvoDataset Parse(string json, string fallbackName)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                   ?? throw new InvalidDataException("A dataset is a JSON object with \"entries\".");
        if (root["entries"] is not JsonArray entries) throw new InvalidDataException("This file has no \"entries\" list.");
        var d = new IvoDataset { Name = (string?)root["name"] is { Length: > 0 } n ? n : fallbackName };
        foreach (var e in entries)
        {
            var replies = e?["reply"] switch { JsonArray a => a.Select(x => (string?)x).OfType<string>().Where(x => x.Trim() != "").ToArray(), JsonValue v when (string?)v is { } one => [one], _ => [] };
            var phrases = (e?["match"] as JsonArray)?.Select(x => (string?)x).OfType<string>().Select(IvoMind.MeantAsWritten).Where(p => p.Count > 0).ToList() ?? [];
            var words = new Dictionary<string, int>();
            foreach (var w in ((string?)e?["words"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = w.Split(':');
                foreach (var m in IvoMind.MeantAsWritten(p[0])) words[m] = p.Length > 1 && int.TryParse(p[1], out var weight) ? weight : 1;
            }
            if (replies.Length == 0 || phrases.Count == 0 && words.Count == 0) { d.Skipped++; continue; }
            d.Entries.Add(new(phrases, words, replies, (string?)e?["more"] ?? ""));
        }
        return d;
    }

    // how well a message fits an entry: a phrase whose words are (nearly) all in it, or keywords adding up to 3
    // loose: mid-conversation in a dataset, so a looser fit still counts
    public static double Fit(Entry e, HashSet<string> meant, bool loose = false)
    {
        var best = 0.0;
        foreach (var p in e.Phrases)
        {
            var hit = p.Count(meant.Contains);
            var cover = (double)hit / p.Count;                       // how much of the phrase is there
            var tight = (double)hit / Math.Max(meant.Count, 1);      // and how little else was said
            if (p.Count == 1 && tight < .5 && !loose) continue;      // one word only counts when it's most of what was said
            if (cover >= (loose ? .4 : .6)) best = Math.Max(best, 2 + cover + tight);
        }
        var score = e.Words.Sum(w => meant.Contains(w.Key) ? w.Value : 0);
        if (score >= (loose ? 2 : 3)) best = Math.Max(best, 1 + score / 10.0);
        return best;
    }
}
