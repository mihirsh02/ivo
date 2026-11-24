using System.Text.Json;

namespace Ivo;

// What Ivo has learnt, kept on this PC (ivo-memory.json, sealed with the app lock like her chat) and used offline:
// words and what they mean, things people say and what they mean by them (the intent she answers), facts about the
// wider world, and replies you taught her. Settings → Assistant lists it all, each with a remove button.
public sealed class IvoMemory
{
    public Dictionary<string, string> Words { get; set; } = [];     // word -> its short meaning
    public Dictionary<string, string> Phrases { get; set; } = [];   // a message, as she reads it -> the intent it means
    public Dictionary<string, string> Facts { get; set; } = [];     // subject -> what she'd say about it
    public Dictionary<string, string> Taught { get; set; } = [];    // a message, as she reads it -> the reply you taught her
    public Dictionary<string, YouSaid> You { get; set; } = [];      // things about you, from what you told her ("favourite colour" -> blue)

    // something you told her about yourself: what, when you said it, and for plans, the day it's on (to ask how it went)
    public sealed record YouSaid(string What, DateTime Said, DateTime? On = null, bool Asked = false);

    public static string Path => System.IO.Path.Combine(Store.Dir, "ivo-memory.json");

    // a message the way she reads it: slang spelled out, typos fixed, stretched words unstretched, punctuation gone
    public static string Key(string said) => string.Join(' ', IvoMind.Words(said).Select(w => w.Trim('?', '!', '.', ',', '(', ')')).Where(w => w != ""));

    public int Count => Words.Count + Phrases.Count + Facts.Count + Taught.Count + You.Count;

    public static IvoMemory Load(string path)
    {
        IvoMemory m;
        try { m = Store.ReadData(path) is { Length: > 0 } json ? JsonSerializer.Deserialize<IvoMemory>(json) ?? new() : new(); }
        catch { return new(); }
        foreach (var w in m.Words.Where(x => IvoMind.Keepable(x.Value) == null).Select(x => x.Key).ToList()) m.Words.Remove(w);   // jokes, crude lines, album titles
        return m;
    }

    // Settings → Assistant → Clear junk: also everyday English words (their dictionary sense is rarely the one meant) and
    // one-word reactions kept as "this means that" ("awwh" → sad). How many went.
    public async Task<int> ClearJunk()
    {
        var before = Count;
        foreach (var w in Words.Where(x => IvoMind.Keepable(x.Value) == null).Select(x => x.Key).ToList()) Words.Remove(w);
        var everyday = await Task.WhenAll(Words.Keys.ToList().Select(async w => (w, Plain: await IvoMind.Ordinary(w))));
        foreach (var (w, plain) in everyday) if (plain) Words.Remove(w);
        foreach (var p in Phrases.Keys.Where(k => !k.Contains(' ')).ToList()) Phrases.Remove(p);
        return before - Count;
    }

    public void Save(string path)
    {
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); Store.WriteData(path, JsonSerializer.Serialize(this)); } catch { }
    }
}
