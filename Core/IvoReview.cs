using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ivo;

// The replies her model got wrong (still poor after its tries), kept with everything the model saw, so you can write
// the right one and train on it (Settings → Assistant → Smarter chat → Export for training). Each export line is one
// training example in the notebook's format, with "reply" left for you and "bad" what the model said; the newest 500
// are kept, sealed with the app lock like her chat.
public static class Review
{
    static string File => Path.Combine(Store.Dir, "ivo-review.json");
    static List<JsonObject>? rows;
    static List<JsonObject> Rows
    {
        get
        {
            if (rows != null) return rows;
            try { rows = Store.ReadData(File) is { Length: > 0 } j ? JsonSerializer.Deserialize<List<JsonObject>>(j) ?? [] : []; } catch { rows = []; }
            return rows;
        }
    }
    public static int Count { get { lock (Rows) return Rows.Count; } }

    public static void Add(IvoMind mind, string said, string? notes, string bad, int score)
    {
        var row = mind.TrainingRow(said, notes);
        row["bad"] = bad; row["score"] = score; row["at"] = DateTime.Now.ToString("s");
        lock (Rows)
        {
            Rows.Add(row);
            if (Rows.Count > 500) Rows.RemoveRange(0, Rows.Count - 500);
            try { Directory.CreateDirectory(Store.Dir); Store.WriteData(File, JsonSerializer.Serialize(Rows)); } catch { }
        }
    }

    // the training file: one example a line, "reply" for you to fill in (the model's "bad" one beside it)
    public static string Export()
    {
        lock (Rows) return string.Join("\n", Rows.Select(r => { var o = (JsonObject)r.DeepClone(); o.Remove("score"); o.Remove("at"); o["reply"] = ""; return o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }));
    }

    public static void Clear() { lock (Rows) { Rows.Clear(); try { Store.WriteData(File, "[]"); } catch { } } }
}
