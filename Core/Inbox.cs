using System.IO;
using System.Text.Json;

namespace Ivo;

// Everything reminders and tasks deliver, read in the Inbox panel: fired reminders (until marked done), the morning
// briefing, single daily-task answers, dev task runs. Kept in <data>/inbox.json, newest first, the last 400 (starred ones stay).
public class InboxItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; set; } = "daily";          // reminder | briefing | daily | dev | later (a link saved to read)
    public string TaskId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";              // what was asked / the reminder's words / "Morning briefing"
    public string Text { get; set; } = "";               // Markdown
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ChatId { get; set; }                   // dev tasks: the chat the agent works in
    public DateTime At { get; set; } = DateTime.Now;
    public bool Failed { get; set; }
    public bool Read { get; set; }
    public bool Done { get; set; }                        // reminders: dealt with (leaves "Now")
    public bool Starred { get; set; }
}

public static class Inbox
{
    static string FilePath => Path.Combine(Store.Dir, "inbox.json");
    static List<InboxItem>? items;
    public static event Action? Changed;
    public static event Action<InboxItem>? Added;   // a new item (webhooks pass it on)

    public static List<InboxItem> All
    {
        get
        {
            if (items != null) return items;
            string? text = null;
            try { text = File.Exists(FilePath) ? Store.ReadData(FilePath) : "[]"; } catch { text = "[]"; }
            if (text == null) return [];   // sealed until Ivo is unlocked: not cached, so it's read then
            try { items = JsonSerializer.Deserialize<List<InboxItem>>(text) ?? []; }
            catch { items = []; }
            return items;
        }
    }

    public static int Unread => All.Count(i => !i.Read || i.Kind == "reminder" && !i.Done);

    public static void Add(InboxItem i)
    {
        All.Insert(0, i);
        // keep the newest 400, but never drop starred ones or reminders still waiting
        var keep = All.Take(400).Concat(All.Skip(400).Where(x => x.Starred || x.Kind == "reminder" && !x.Done)).ToList();
        All.Clear(); All.AddRange(keep);
        Save();
        Added?.Invoke(i);
    }

    public static void Save()
    {
        Directory.CreateDirectory(Store.Dir);
        if (items == null) return;   // never loaded (sealed): nothing to write, and the file must not be replaced
        Store.WriteData(FilePath, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }

    public static void Reload() { items = null; Changed?.Invoke(); }

}
