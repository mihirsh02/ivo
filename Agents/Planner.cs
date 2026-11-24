using System.Globalization;
using System.Text.Json.Nodes;

namespace Ivo;

public class Subtask { public string Text { get; set; } = ""; public bool Done { get; set; } }

public class Todo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? Due { get; set; }
    public bool DueTime { get; set; }           // Due has a time of day (otherwise just the day)
    public int Priority { get; set; }           // 0 none · 1 low · 2 medium · 3 high
    public string List { get; set; } = "";      // "" = Inbox
    public List<string> Tags { get; set; } = [];
    public string Repeat { get; set; } = "";    // "" · daily · weekdays · weekly · monthly: ticking it moves it to the next time
    public List<Subtask> Steps { get; set; } = [];
    public string? RemoteId { get; set; }      // its Notion page or Google task, once synced (TodoSync)
    public string SyncedText { get; set; } = "";
    public bool SyncedDone { get; set; }
}

// The to-do list, reminders and tasks as tools, so any model can read and change them like the user can.
public static class Planner
{
    public static Settings S = null!;   // set by MainWindow
    public static Action? Changed;      // save and redraw
    public static Action? BriefNow;     // build the morning briefing now, into the Inbox

    const string Time = "Local date and time as yyyy-MM-dd HH:mm";
    public static readonly ToolDef TodoTool = new("todo",
        "The user's to-do list in Ivo. action: list (see items with their ids), add (text), done / undone (id), edit (id, text), remove (id), clear_done. Use it when they ask to note, track, tick off or review things to do.",
        """{"type":"object","properties":{"action":{"type":"string","enum":["list","add","done","undone","edit","remove","clear_done"]},"id":{"type":"string"},"text":{"type":"string"}},"required":["action"]}""", true);

    public static readonly ToolDef ScheduleTool = new("schedule",
        "The user's reminders, daily tasks and dev tasks in Ivo. A reminder shows them a notification with the text at the time; a task (daily task) sends the text as a prompt to a model at the time and delivers the answer; " +
        "a dev task opens a chat in a folder at the time and the coding agent does the work in the text on autopilot (builds, tests, fixes, reports). " +
        "Use a daily task when the user wants regular updates or follow-ups on something; write the text as the full prompt you'll get then, with the context needed. For Alerts, tell yourself to reply exactly 'Nothing new.' when nothing changed, and they won't be bothered. " +
        "action: list (see items with ids), briefing (build the morning briefing now from the daily tasks and put it in the user's Ivo Inbox), add (kind, text, when, repeat, category), edit (id plus any fields to change), remove (id). Times are " + Time + "; now is given in the list output.",
        """{"type":"object","properties":{"action":{"type":"string","enum":["list","briefing","add","edit","remove"]},"id":{"type":"string"},"kind":{"type":"string","enum":["reminder","task","dev"]},"folder":{"type":"string","description":"dev tasks: the folder the coding agent works in"},"text":{"type":"string"},"when":{"type":"string","description":"Local date and time as yyyy-MM-dd HH:mm"},"repeat":{"type":"string","enum":["once","daily","weekdays","weekly"]},"category":{"type":"string","description":"For tasks: Quotes, Alerts, Updates (follow-ups on something), or a short name of your own"}},"required":["action"]}""", true);

    public static string Run(string name, JsonNode a) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var result = name == "todo" ? RunTodo(a) : RunSchedule(a);
            Changed?.Invoke();
            return result;
        });

    static string? Str(JsonNode a, string k) => a[k]?.GetValue<string>()?.Trim();

    static string RunTodo(JsonNode a)
    {
        var list = S.Todos;
        Todo? Find() => list.FirstOrDefault(t => t.Id == Str(a, "id"));
        switch (Str(a, "action"))
        {
            case "briefing":
                BriefNow?.Invoke();
                return "Building the morning briefing; it lands in the Ivo Inbox in a minute or two, with a notification.";
            case "add":
                if (Str(a, "text") is not { Length: > 0 } text) return "Give the text to add.";
                var t = new Todo { Text = text }; list.Add(t);
                return $"Added [{t.Id}] {t.Text}";
            case "done" or "undone":
                if (Find() is not { } d) return "No to-do with that id. Call list first.";
                d.Done = Str(a, "action") == "done"; return $"[{d.Id}] marked {(d.Done ? "done" : "not done")}";
            case "edit":
                if (Find() is not { } e) return "No to-do with that id. Call list first.";
                e.Text = Str(a, "text") ?? e.Text; return $"[{e.Id}] is now: {e.Text}";
            case "remove":
                if (Find() is not { } r) return "No to-do with that id. Call list first.";
                TodoSync.Removed(S, r); list.Remove(r); return $"Removed [{r.Id}]";
            case "clear_done":
                foreach (var gone in list.Where(x => x.Done)) TodoSync.Removed(S, gone);
                return $"Cleared {list.RemoveAll(x => x.Done)} done items";
            default:
                return list.Count == 0 ? "The to-do list is empty." : string.Join("\n", list.Select(x => $"[{x.Id}] {(x.Done ? "✓" : "○")} {x.Text}"));
        }
    }

    static string RunSchedule(JsonNode a)
    {
        var list = S.Schedules;
        Scheduled? Find() => list.FirstOrDefault(x => x.Id == Str(a, "id"));
        string Show(Scheduled x) => $"[{x.Id}] {x.Kind}{(x.Category != "" ? $" ({x.Category})" : "")} · {x.When:yyyy-MM-dd HH:mm} · {x.Repeat}{(x.On ? "" : " · done")} · {x.Text}";
        bool When(out DateTime t) => DateTime.TryParseExact(Str(a, "when"), ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
        switch (Str(a, "action"))
        {
            case "add":
            {
                if (Str(a, "text") is not { Length: > 0 } text) return "Give the text.";
                if (!When(out var t)) return "Give when as yyyy-MM-dd HH:mm (local time). Now is " + DateTime.Now.ToString("yyyy-MM-dd HH:mm dddd");
                if (t <= DateTime.Now) return "That time has passed. Now is " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                var x = new Scheduled { Kind = Str(a, "kind") is "task" or "dev" ? Str(a, "kind")! : "reminder", Text = text, When = t, Repeat = Str(a, "repeat") ?? "once", Folder = Str(a, "folder") };
                // dev tasks start on the dev default (Settings → Tasks); daily tasks always use the daily model from there
                if (x.Kind == "task")
                {
                    x.Category = Str(a, "category") ?? "Updates";
                }
                list.Add(x);
                return "Scheduled " + Show(x);
            }
            case "edit":
            {
                if (Find() is not { } x) return "No item with that id. Call list first.";
                if (Str(a, "text") is { Length: > 0 } text) x.Text = text;
                if (Str(a, "kind") is { } kind) x.Kind = kind is "task" or "dev" ? kind : "reminder";
                if (Str(a, "folder") is { } folder) x.Folder = folder;
                if (Str(a, "repeat") is { } rep) x.Repeat = rep;
                if (Str(a, "category") is { } cat) x.Category = cat;
                if (a["when"] != null) { if (!When(out var t)) return "Give when as yyyy-MM-dd HH:mm."; x.When = t; x.On = t > DateTime.Now; }
                return "Updated " + Show(x);
            }
            case "remove":
                if (Find() is not { } r) return "No item with that id. Call list first.";
                list.Remove(r); return "Removed " + Show(r);
            default:
                return $"Now: {DateTime.Now:yyyy-MM-dd HH:mm dddd}\n" + (list.Count == 0 ? "Nothing scheduled." : string.Join("\n", list.OrderBy(x => x.When).Select(Show)));
        }
    }
}
