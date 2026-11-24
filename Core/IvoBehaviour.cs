using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ivo;

// A behaviour pack is a JSON description of a model's role, response style, examples, and context-aware guidance.
// Ivo combines its built-in defaults with optional user packs (Settings → Assistant → Behaviour packs).
// Notes can use app-provided signals such as mood, question, message length, time of day, and meaning. Prefix a signal
// with "!" to match when it is absent. Notes can also match message text and use {her} or {meaning}.
//   mood:Sad …       the mood read in the message (Happy, Excited, Sad, Stressed, Angry, Rude, Flirty, Bored, Confused)
//   question         it asks something
//   short            three words or fewer, not a question
//   rude             language in the message matches Ivo's existing language signal
//   meaning          slang or short forms were interpreted ({meaning} says what they meant)
//   morning · afternoon · evening · night   the time of day now
//   (and "!signal" for its opposite)
// A note can also need its "match" (a pattern on the message, lower case) and fill in {her} (her last line) and {meaning}.
//
//   { "name": "…",
//     "persona": "You are Ivo, a conversational companion in the Ivo app. Be clear, thoughtful and natural.",
//     "personality": "the personality when none is picked in Settings",
//     "clean": "Be respectful and appropriate for a general audience.",
//     "examples": [["why is the sky blue", "Air scatters blue light more than most other colours."]],
//     "notes": [{ "id": "sad", "when": ["mood:Sad"], "match": null, "say": "They feel sad…" }, …],
//     "avoid": [{ "when": [], "match": "as an ai", "cost": 6 }, …], "never": [{ "when": ["question"], "match": "…" }],
//     "strip": [{ "when": ["mood:Sad"], "match": "😂|🤣" }], "maxEmoji": 1,
//     "quality": { "echo": 6, "quote": 5, "repeatHer": 6, "questionOnly": 2, "onTopic": 2, "stuck": 4, "length": [15, 220], "retryBelow": 1 },
//     "model": { "temperature": 0.55, "retryTemperature": 0.7, "maxTokens": 80, "tries": 2, "history": 6, "retryNote": "…" } }
// (quality and model: a pack on top replaces them whole)
//
// Packs overlay defaults: persona/style replace matching fields; examples replace or append with "addExamples"; notes
// append or replace by id; avoid, never, and strip rules append; quality and model settings replace as a whole.
public class IvoBehaviour
{
    public class Note { public string? Id { get; set; } public string[] When { get; set; } = []; public string? Match { get; set; } public string Say { get; set; } = ""; }
    // Optional fixed replies for matching text or Ivo intents.
    public class Reply { public string? Id { get; set; } public string? Match { get; set; } public string[] Say { get; set; } = []; }
    public class Phrase { public string[] When { get; set; } = []; public string Match { get; set; } = ""; public bool Missing { get; set; } public int Cost { get; set; } = 2; }

    public string Name { get; set; } = "";
    public string? Persona { get; set; }
    public string? Personality { get; set; }
    public string? Clean { get; set; }
    public List<string[]>? Examples { get; set; }
    public bool AddExamples { get; set; }
    public List<Note> Notes { get; set; } = [];
    public List<Reply> Replies { get; set; } = [];
    public List<Phrase> Avoid { get; set; } = [];   // a reply with it (or, "missing": true, without it) costs that much; a costly one is tried again
    public List<Phrase> Never { get; set; } = [];   // a reply with it isn't said (her own line instead)
    public List<Phrase> Strip { get; set; } = [];   // taken out of a reply (e.g. laughing emoji when they're sad)
    public int? MaxEmoji { get; set; }
    // how a reply is judged (points; 0 turns a check off) and when it's tried again
    public class Judge
    {
        public int Start { get; set; } = 4;           // every reply starts here
        public int Echo { get; set; } = 6;            // it opens with their words
        public int Quote { get; set; } = 5;           // it quotes their whole message back
        public int RepeatHer { get; set; } = 6;       // it says a line of hers from just before again
        public int QuestionOnly { get; set; } = 2;    // a question answered only with a question
        public int OnTopic { get; set; } = 2;         // (a plus) it's about what they talked about
        public int Stuck { get; set; } = 4;           // a short reply saying the same word twice
        public int[] Length { get; set; } = [15, 220]; // a reply this long (characters) gets a point, otherwise loses one
        public int RetryBelow { get; set; } = 1;      // below this, it's tried again
    }
    public Judge? Quality { get; set; }
    // the model's settings for her replies
    public class Settings
    {
        public float Temperature { get; set; } = 0.55f;   // how adventurous the first try is
        public float RetryTemperature { get; set; } = 0.7f;
        public int MaxTokens { get; set; } = 80;          // the longest reply, in tokens
        public int Tries { get; set; } = 2;
        public int History { get; set; } = 12;            // the most recent chat lines the model sees
        public string RetryNote { get; set; } = "Say it differently from before; do NOT repeat their words; reply to what they mean.";
    }
    public Settings? Model { get; set; }
    public Judge Judging => Quality ?? new();
    public Settings Using => Model ?? new();

    static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    public static IvoBehaviour Parse(string json) => JsonSerializer.Deserialize<IvoBehaviour>(json, Read) ?? new();

    // the pack in use: the app's own (shipped beside it), with yours on top
    public static string Shipped => Path.Combine(AppContext.BaseDirectory, "Assets", "ivo", "behaviour.json");
    static IvoBehaviour? current;
    public static IvoBehaviour Current => current ??= Build([]);
    public static void Use(IEnumerable<string> mine) => current = Build(mine);
    static IvoBehaviour Build(IEnumerable<string> mine)
    {
        IvoBehaviour b;
        try { b = Parse(File.ReadAllText(Shipped)); } catch { b = new(); }
        foreach (var json in mine)
            try { b = b.With(Parse(json)); } catch { }
        return b;
    }

    IvoBehaviour With(IvoBehaviour top) => new()
    {
        Name = top.Name != "" ? top.Name : Name,
        Persona = top.Persona ?? Persona, Personality = top.Personality ?? Personality, Clean = top.Clean ?? Clean,
        Examples = top.Examples == null ? Examples : top.AddExamples ? [.. Examples ?? [], .. top.Examples] : top.Examples,
        Notes = [.. Notes.Where(n => n.Id == null || !top.Notes.Any(t => t.Id == n.Id)), .. top.Notes], Replies = [.. Replies, .. top.Replies], Avoid = [.. Avoid, .. top.Avoid], Never = [.. Never, .. top.Never], Strip = [.. Strip, .. top.Strip], Quality = top.Quality ?? Quality, Model = top.Model ?? Model, MaxEmoji = top.MaxEmoji ?? MaxEmoji,
    };

    static bool Fit(string[] when, ISet<string> signals) => when.All(w => w.StartsWith('!') ? !signals.Contains(w[1..]) : signals.Contains(w));

    // the notes for this message, from its signals
    public IEnumerable<string> NotesFor(ISet<string> signals, string low, string her, string meaning) =>
        Notes.Where(n => Fit(n.When, signals)
                         && (n.Match is not { Length: > 0 } m || Matches(m, low)))
             .Select(n => n.Say.Replace("{her}", her).Replace("{meaning}", meaning));

    // how much a reply costs for what the pack says to avoid (for this message's signals)
    public int Cost(string reply, ISet<string> signals)
    {
        var low = reply.ToLowerInvariant();
        var cost = Avoid.Where(a => Fit(a.When, signals) && Matches(a.Match, low) != a.Missing).Sum(a => a.Cost);
        if (MaxEmoji is { } max && Regex.Matches(reply, @"\p{So}").Count > max) cost += 2;
        return cost;
    }

    // a reply the pack says is never said here
    public bool Refuses(string reply, ISet<string> signals) => Never.Any(n => Fit(n.When, signals) && Matches(n.Match, reply.ToLowerInvariant()));
    // the reply with what the pack takes out here taken out
    public string Tidy(string reply, ISet<string> signals)
    {
        foreach (var x in Strip.Where(x => Fit(x.When, signals)))
            try { reply = Regex.Replace(reply, x.Match, "", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); } catch { }
        return Regex.Replace(reply, "  +", " ").Trim();
    }

    public static bool Matches(string pattern, string text) { try { return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); } catch { return false; } }
}
