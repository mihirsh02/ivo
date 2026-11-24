using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

public record Starred(string Text, string ChatId, string ChatTitle, DateTime At);
// a habit you tick each day it's due. Days: the ticked ones (yyyy-MM-dd). On: the weekdays it's due (0 Sunday … 6 Saturday;
// none: every day). Remind: a time (HH:mm) Ivo nudges you if it isn't ticked yet, or "".
public class Habit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
    public string Name { get; set; } = "";
    public List<string> Days { get; set; } = [];
    public List<int> On { get; set; } = [];
    public string Remind { get; set; } = "";

    public bool Due(DateTime d) => On.Count == 0 || On.Contains((int)d.DayOfWeek);
    public bool Did(DateTime d) => Days.Contains(d.ToString("yyyy-MM-dd"));
    // days in a row it was done when due, up to today (or yesterday, while today isn't ticked yet); days it isn't due don't break it
    public int Streak(DateTime today)
    {
        var n = 0;
        for (var d = Did(today) || !Due(today) ? today : today.AddDays(-1); d > today.AddYears(-3); d = d.AddDays(-1))
        {
            if (!Due(d)) continue;
            if (!Did(d)) break;
            n++;
        }
        return n;
    }
}
public class Webhook { public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6]; public string Name { get; set; } = ""; public string Kind { get; set; } = "slack"; public string Url { get; set; } = ""; public List<string> Events { get; set; } = ["reminders", "tasks", "briefing"]; }

public class Settings
{
    public string WeatherCity { get; set; } = "";       // for Ivo's weather (Open-Meteo); "" : she asks for it
    public List<string> NewsCategoryOrder { get; set; } = [];
    public List<string> HiddenNewsCategories { get; set; } = [];
    public bool QuietHours { get; set; }                   // no pop-ups or chimes from QuietFrom to QuietTo
    public string QuietFrom { get; set; } = "22:00";
    public string QuietTo { get; set; } = "08:00";
    public bool QuietFullscreen { get; set; } = true;      // ...or while a full-screen app, game or presentation is up
    public bool MoodLog { get; set; } = true;              // Ivo keeps a daily record of your mood and checks in when it's been low
    public bool ActivityOn { get; set; } = true;           // Activity: what's in front, typing speed and screen time
    public bool ActivityNudge { get; set; } = true;        // a nudge after an hour straight of videos, games or social
    public bool Journal { get; set; } = true;              // Ivo keeps a journal of what you talk about each day
    public string Provider { get; set; } = "";
    public List<ProviderCfg> Providers { get; set; } = [];
    public Dictionary<string, string> Models { get; set; } = new();
    public string Effort { get; set; } = "off";
    public string Mode { get; set; } = "interactive";   // interactive | plan | autopilot
    public string? Workspace { get; set; }                                 // folder the agent works in; none = plain chat
    public string Permission { get; set; } = "manual";                    // manual | assisted | all
    public bool Sandbox { get; set; }
    public double Temp { get; set; } = 0.7;
    public string System { get; set; } = "";
    public string Theme { get; set; } = "softdark";     // soft (Default light) | softdark (Default dark) | oled (AMOLED) | glow (Ivo); contrast only while Windows high contrast is on
    public string Layout { get; set; } = "single";  // single | multi
    public Dictionary<string, string> ModelStatus { get; set; } = new();   // "provider|model" -> "ok" or the last error
    public List<string> Pinned { get; set; } = [];                         // "provider|model"
    public bool OnlyWorking { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public Dictionary<string, string> Connections { get; set; } = new();
    public Dictionary<string, List<string>> SpotifyFolders { get; set; } = new();   // Ivo's own folders for Spotify playlists: name → playlist ids   // service id → encrypted token (Settings → Connections)
    public bool CtrlEnterSends { get; set; }           // off: Enter sends, Shift+Enter is a new line
    public bool SpellCheck { get; set; }
    public bool ShowCost { get; set; } = true;         // tokens and cost in the chat header
    public bool ShowTools { get; set; } = true;        // tool cards open (off: one line each, click to open)
    public bool ShowThinking { get; set; } = true;     // the "Thinking" chips
    public bool ShowAgentPrompts { get; set; } = true; // the task text sent to agents, in their cards
    public bool AutoOn { get; set; } = true;           // Auto in the provider picker (see Router)
    public int AutoTries { get; set; } = 6;
    public List<string> AutoSkip { get; set; } = [];   // providers Auto leaves out (new ones are in)
    public int AutoRestMinutes { get; set; } = 10;
    public bool PoolKeys { get; set; } = true;         // several keys on a provider: rotate them (off: the first one only)
    public bool GatewayOn { get; set; }                // Ivo as an OpenAI-compatible server for other apps on this PC (see Gateway)
    public int GatewayPort { get; set; } = 20130;
    public string GatewayKey { get; set; } = "";
    public bool WebInApp { get; set; }                 // provider pages (usage, billing): false opens them in the browser
    public Profile? Profile { get; set; }
    public bool WelcomeDone { get; set; }   // the first-launch welcome was answered or skipped
    public bool Developer { get; set; }     // Settings → Data → Developer: the failure log and other internals
    public string SearchKey { get; set; } = "";   // optional Brave Search API key (encrypted): better web_search results than the free RSS route
    public string KnowledgeFolder { get; set; } = "";
    public string EmbedModel { get; set; } = "";                          // "provider/model" for searching the knowledge folder by meaning; empty = word search
    public List<Persona> Personas { get; set; } = [];
    public List<string> TodoGone { get; set; } = [];                       // remote ids of to-dos deleted here, deleted there on the next sync
    public string TodoService { get; set; } = "";                          // "notion" or "google": where the to-do list syncs ("" = nowhere)
    public string NotionTodoDb { get; set; } = "";                         // the Notion database the to-do list syncs with
    public string GoogleTasksClient { get; set; } = "";                    // encrypted "client id|client secret" of your Google Cloud desktop app
    public string GoogleTasksToken { get; set; } = "";                     // encrypted Google refresh token
    public List<Nag> Nags { get; set; } = [];
    public DateTime LaterNudged { get; set; }                               // the last weekend the read-later queue was brought up
    public int DigestHours { get; set; }
    public List<string> CalendarUrls { get; set; } = [];                  // private iCal addresses (Settings → General → Calendar)
    public bool ClipboardHistory { get; set; }
    public bool TourDone { get; set; }                             // remember recent copied text for the model (off by default; kept in memory only)                                   // 0: notify as things arrive; else task results are gathered into one notification every N hours                              // reminders that come back until marked done                    // the user's own chat templates (Advanced → Template)
    public Dictionary<string, double> Budgets { get; set; } = new();     // provider → monthly limit in $ (Settings → Spending)
    public Dictionary<string, Project> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);   // chat group → its instructions, model, folder
    public double DailyLimit { get; set; }                               // $ per day across all providers; 0 = none
    public double ExpensiveAt { get; set; } = 30;                        // ask before a model costing this much per million output tokens; 0 = never
    public Dictionary<string, double> Spend { get; set; } = new();       // "provider|yyyy-MM" → estimated $ spent
    public Dictionary<string, int> Preferred { get; set; } = new();      // "provider|model" → times picked as the best /compare answer (Auto leans to them)
    public List<string> CompareLog { get; set; } = [];                    // "when · prompt · winner · others"
    public List<string> FoldedGroups { get; set; } = [];   // sidebar sections the user folded away
    public string View { get; set; } = "chat";
    public double Zoom { get; set; } = 0.9;   // interface size: 0.8 small, 0.9 default, 1 large   // chat | code (the light IDE)
    public string McpServers { get; set; } = "{}";
    public List<string> DisabledSkills { get; set; } = [];
    public List<string> SkillSources { get; set; } = [];     // GitHub repos / folders the user added (Skills → Browse)
    public List<string> HiddenSources { get; set; } = [];    // built-in sources the user turned off                         // Claude Desktop-style "mcpServers" JSON
    public List<Scheduled> Schedules { get; set; } = [];
    public string Voice { get; set; } = "";       // kokoro:<id> | piper:<key>; empty = none chosen yet
    public double VoiceRate { get; set; } = 1;
    public int VoiceVolume { get; set; } = 100;   // 0–200 %
    public string LockHash { get; set; } = "";        // app lock: salt:PBKDF2 of the passkey; empty = no lock
    public string LockKind { get; set; } = "pin";     // pin (6 numbers) or password (8-20 characters)
    public List<string> LockCodes { get; set; } = [];  // one-time recovery codes: each wraps the data key (see LockWrap)
    public bool CodesSaved { get; set; }               // the recovery codes were copied, saved or printed
    public string Vault { get; set; } = "";            // the personal settings, sealed while the lock is on (see Store.OpenVault)
    public string LockWrap { get; set; } = "";         // the data key that seals chats, Inbox and API keys, wrapped by the PIN / password
    public bool RedactSecrets { get; set; } = true;   // mask keys, passwords and card numbers before a message goes to a cloud model
    public List<string> Trusted { get; set; } = [];   // MCP servers and skills you allowed: "mcp|name|hash", "skill|name|hash"
    public bool LockWithWindows { get; set; } = true;  // lock when Windows locks
    public bool AutoLock { get; set; }                 // also lock after AutoLockMinutes without input
    public int AutoLockMinutes { get; set; } = 5;
    public bool ReadAloud { get; set; } = true;    // the speaker buttons on replies and in the Inbox
    public bool AppSounds { get; set; } = true;    // Ivo's own sounds, all of them
    public Dictionary<string, int> SoundLevels { get; set; } = [];   // each sound's volume by kind (Sounds.Kinds), 0 = off; missing = 100
    public int SoundVolume { get; set; } = 100;    // all of Ivo's sounds
    public bool QuickBarFolded { get; set; }
    public List<Todo> Todos { get; set; } = [];
    public List<string>? Rail { get; set; }                  // buttons on the left rail (null: Planner, Calendar, Spotify, Settings); the rest are in the title bar
    public List<string> TitleOrder { get; set; } = [];        // the title bar's buttons, left to right (empty: the default order)
    public List<string> HiddenButtons { get; set; } = [];   // title-bar buttons switched off in Appearance: incognito, todo, reminders, daily, dev
    public bool DesktopNotify { get; set; } = true;
    public bool RegionalCalendar { get; set; }          // the Calendar shows each day in your holiday region's own calendar
    public string HolidayCalendar { get; set; } = "https://calendar.google.com/calendar/ical/en.indian%23holiday%40group.v.calendar.google.com/public/basic.ics";   // public holidays and festivals (Google's, India)
    public List<Habit> Habits { get; set; } = [];
    public Dictionary<string, int> FocusMinutes { get; set; } = [];   // yyyy-MM-dd → minutes focused that day
    public int FocusLength { get; set; } = 25;
    public int BreakLength { get; set; } = 5;
    public List<Webhook> Webhooks { get; set; } = [];
    public List<PortalContact> PortalContacts { get; set; } = [];
    public List<PortalContact> PortalContactsBin { get; set; } = [];   // Recently deleted (15 days, then gone for both)
    public string PortalCamera { get; set; } = "";   // Portal's calls: the camera's id, the mic and speakers' numbers ("" / -1: Windows' default)
    public int PortalMic { get; set; } = -1;
    public int PortalSpeaker { get; set; } = -1;
    public bool PortalMessageSound { get; set; } = true;
    public bool PortalCallSound { get; set; } = true;
    public bool PortalReadReceipts { get; set; } = true;
    public List<string> PortalReactions { get; set; } = [];
    public string ChatDownloads { get; set; } = "";  // where Chats saves files ("" : Downloads\Ivo Chats)   // people you can reach in Portal without codes   // where Ivo posts: Slack, Discord, Teams, Google Chat or any URL
    public List<Starred> Stars { get; set; } = [];      // starred replies, copied here so they stay when their chat is deleted
    public List<string> NotifyOff { get; set; } = [];   // kinds of notification switched off: reminder, daily, failed, briefing, dev
    public List<string> Blocked { get; set; } = [];     // devices switched off in Settings → Permissions: microphone, camera, location, …
    public string Dictation { get; set; } = "windows";  // the mic button: "windows" (Windows speech recognition) or "ivo" (own, offline)
    public bool OrbMotion { get; set; }
    public Dictionary<string, string> ActivityKinds { get; set; } = [];   // Activity: an app or site counted as another kind ("Slack" → "Social")
    public List<IvoBehaviourPack> IvoBehaviourPacks { get; set; } = [];   // how Ivo's model behaves, on top of the app's own pack (later ones last)
    public List<IvoDatasetFile> IvoDatasets { get; set; } = [];   // your own datasets for Ivo, copied into the ivo-datasets folder
    public List<YouTubeChannel> YouTube { get; set; } = [];
    public string RedditApp { get; set; } = "";     // a Reddit "installed app" id (reddit.com/prefs/apps), for Reddit in Ivo's chat; no secret, no account   // channels you follow from Ivo's chat (their public feeds, no account)
    public Dictionary<string, WidgetSpot> Widgets { get; set; } = new();   // desktop widgets by id: on, and where you put it
    public bool WidgetsLocked { get; set; }
    public bool IvoWandering { get; set; }
    public double IvoOpacity { get; set; } = 1;
    public string IvoSize { get; set; } = "medium";
    public bool SmarterChat { get; set; }            // Ivo's small talk written by a small model on this PC (Settings → Assistant), off until downloaded and on
    public string SmarterChatFile { get; set; } = "";   // your own model file (a GGUF) instead of the downloaded one
    public string IvoPersonality { get; set; } = "";    // a preset (Sweet, Cheeky, Sassy, Chill, Nerdy, Shy) or your own words
    public int? IvoAgeOffset { get; set; }              // her age is yours plus this (-2…2), picked once     // Ivo says a short reaction now and then (Settings → Assistant), with app sounds on
    public bool CodeOn { get; set; }               // Code mode (files, terminal, Git); off: Ivo is chats only and the Chat | Code switch goes
    public bool StartInTray { get; set; } = true;
    public string Splash { get; set; } = "aurora";     // launch animation: oled, aurora, clear or off     // started with Windows: wait in the tray (off: open the window)       // the empty chat's orb turns and breathes; off: it stands still
    public string DailyModel { get; set; } = "";   // "provider/model" daily tasks run on; empty = the default chat model
    public string DevModel { get; set; } = "";     // what a new dev task starts with; empty = the default chat model
    public List<string> DevPinned { get; set; } = [];   // "provider/model" offered in the Dev tasks window
    public string Clock { get; set; } = "";               // "24" | "12"; empty = like Windows
    public bool Briefing { get; set; }                     // daily tasks arrive together as one morning briefing
    public string BriefingTime { get; set; } = "08:00";
    public DateTime BriefingNext { get; set; }         // Windows notifications for reminders and tasks
    public bool MemoryReview { get; set; } = true;     // a weekly look at what's remembered, in the Inbox
    public DateTime MemoryReviewNext { get; set; }
    public List<string> Memories { get; set; } = [];   // facts the models saved with the remember tool, shared by all chats
    public List<SavedPrompt> Prompts { get; set; } =
    [
        new("review", "Review this for bugs, unclear parts and possible improvements. List the most important first and be specific:\n\n"),
        new("explain", "Explain this simply, as if to a smart 12-year-old:\n\n"),
        new("summarize", "Summarize the key points in a few short bullets:\n\n"),
        new("fix", "Fix the grammar and spelling, keep my tone and meaning:\n\n"),
    ];
}

public record SavedPrompt(string Name, string Text);

public record Prepared(DateTime For, string? Text, bool Failed, string Model, string Provider);   // Text null: nothing to show

// a chat group's settings (see MainWindow.Projects)
public class Project
{
    public string Instructions { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Folder { get; set; } = "";
}

public class Scheduled
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
    public string Kind { get; set; } = "reminder";   // reminder | task (Ivo's) | dev | plan (your schedule: shown, never fired)
    // your schedule: on that day? (once: its date; daily; weekdays; weekly: its weekday; from its first date on)
    public bool Falls(DateTime day) => On && day.Date >= When.Date && Repeat switch
    {
        "daily" => true, "weekdays" => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday), "weekly" => day.DayOfWeek == When.DayOfWeek, _ => day.Date == When.Date,
    };
    public string Text { get; set; } = "";
    public DateTime When { get; set; }
    public string Repeat { get; set; } = "once";     // once | daily | weekdays | weekly
    public string App { get; set; } = "";            // reminders: due when this program starts (its process name, e.g. "Teams")
    public bool On { get; set; } = true;
    // reminders
    public bool Sound { get; set; } = true;
    public bool Sticky { get; set; } = true;
    public bool? Brief { get; set; }                    // part of the morning briefing (null: tasks yes, reminders no)
    public bool Nag { get; set; }                       // reminders: again every 10 minutes until marked done in the Inbox
    // tasks
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string Deliver { get; set; } = "notify";
    public string Category { get; set; } = "";
    public string? Folder { get; set; }                 // dev tasks: the folder the agent works in
    public string LastResult { get; set; } = "";        // alerts: what was said last time         // Quotes, Alerts, Updates or the user's own   // notify | open | silent
    public Prepared? Ready { get; set; }                // daily tasks: the answer, got ready early and shown at its time
    public double? Took { get; set; }                   // daily tasks: seconds its last good run took (it starts that much early)
}

// one of your datasets for Ivo: its copy in the ivo-datasets folder, whether it's on, and what it held when added
public class IvoDatasetFile { public string File { get; set; } = ""; public string Name { get; set; } = ""; public bool On { get; set; } = true; public int Entries { get; set; } public int Skipped { get; set; } }

// one of your behaviour packs for Ivo's model (Core/IvoBehaviour.cs): its file in ivo-behaviour, whether it's on, its name
public class IvoBehaviourPack { public string File { get; set; } = ""; public string Name { get; set; } = ""; public bool On { get; set; } = true; }

// The person using Ivo (Settings → Profile). Share: whether the models get a short note about them.
public class Profile
{
    public string Name { get; set; } = "";
    public DateTime? Born { get; set; }   // date of birth: age is worked out from it, so it's always right
    [System.Text.Json.Serialization.JsonIgnore] public int? Age => Born is { } b ? DateTime.Today.Year - b.Year - (b.Date > DateTime.Today.AddYears(-(DateTime.Today.Year - b.Year)) ? 1 : 0) : null;
    public string Gender { get; set; } = "";   // "Male", "Female" or not given
    public string Work { get; set; } = "";
    public List<string> Uses { get; set; } = [];
    public bool Share { get; set; } = true;
}   // typed as /name in the message box

public record Img(string Mime, string B64);
// Agent turns: an assistant Msg carries Calls; each result is a "tool" Msg with CallId and Name.
// Blocks: Anthropic thinking blocks (JSON) sent back on tool turns. Undo: a file tool's "before", on its tool Msg.
public record Msg(string Role, string Text, List<Img>? Images = null, List<ToolCall>? Calls = null, string? CallId = null, string? Name = null,
                  string? Blocks = null, string? Thought = null, Usage? Usage = null, UndoInfo? Undo = null, double? Seconds = null, DateTime? At = null,
                  string? Detail = null, bool Starred = false);   // Seconds: how long the reply took; At: when it was sent; Detail: shown to the user only, never sent (an agent's transcript)
public record Usage(int In, int Out, double? Cost = null, string? Provider = null);   // Provider: null means the chat's own
public record Nag(string InboxId, string Text, DateTime Next);
public record Branch(int At, List<Msg> Tail, List<Branch>? Nested = null);   // a version of a chat from message At on (edit and resend keeps the old one); Nested: versions further down inside it
public record Persona(string Name, string Prompt);
// Before: the file's bytes (base64) before the change, null if the tool created it. AfterHash: to spot later edits.
public record UndoInfo(string Path, string? Before, string AfterHash);

public class Chat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New chat";
    public bool Pinned { get; set; }
    public bool Archived { get; set; }
    public string? Group { get; set; }   // a sidebar group the user put this chat in
    public string Space { get; set; } = "chat";
    [System.Text.Json.Serialization.JsonIgnore] public bool Raw { get; set; }   // from the gateway: only the caller's own system prompt
    [System.Text.Json.Serialization.JsonIgnore] public bool Incognito { get; set; }   // never written to disk, no memory   // chat | code: the mode it belongs to (each mode lists its own chats)
    [System.Text.Json.Serialization.JsonIgnore] public string Area => Pinned || Group != null ? "Groups" : "Recent";   // sidebar: top heading
    [System.Text.Json.Serialization.JsonIgnore] public string? SubGroup => Pinned ? "Pinned" : Group;                     // sidebar: the group inside Groups
    [System.Text.Json.Serialization.JsonIgnore] public string Section => Pinned ? "Pinned" : Archived ? "Archived" : Group ?? "Recents";
    // sidebar order: pinned, then groups (by name), then the rest
    [System.Text.Json.Serialization.JsonIgnore] public (int, string) Rank => Pinned ? (0, "") : Group != null && !Archived ? (1, Group.ToLowerInvariant()) : (2, "");
    [System.Text.Json.Serialization.JsonIgnore] public string PinGlyph => Pinned ? "\uE840" : "\uE718";
    [System.Text.Json.Serialization.JsonIgnore] public string PinTip => Pinned ? "Unpin" : "Pin to top";
    public DateTime Updated { get; set; } = DateTime.Now;
    public bool LocalOnly { get; set; }   // only models on this PC / your network may answer (Api.Stream refuses the rest)
    public List<Msg> Messages { get; set; } = [];
    // per-chat options (null on chats saved before these existed -> use current defaults)
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string Effort { get; set; } = "off";
    public string Mode { get; set; } = "interactive";   // interactive | plan | autopilot
    public string? Workspace { get; set; }                                 // folder the agent works in; none = plain chat
    public string Permission { get; set; } = "manual";                    // manual | assisted | all
    public bool Sandbox { get; set; }
    public string? Quick { get; set; }   // the quick action picked, if any (the defaults can equal one)
    public bool Elevated { get; set; }   // run agent commands as administrator (Allow all only); never carried to new chats
    public string? CliSession { get; set; }
    public string? Summary { get; set; }   // the older messages, condensed (see Context); sent in their place
    public int SummaryUpTo { get; set; }   // how many messages the summary covers   // "<provider url>|<id>": the command-line tool's session this chat continues
    public double Temp { get; set; } = 0.7;
    public string System { get; set; } = "";
    public string Context { get; set; } = "";               // pinned to this chat: notes and file text sent with every message
    public List<Branch> Branches { get; set; } = [];       // other versions of the conversation after an edited message
    public override string ToString() => Title;
}

// Kind decides the wire format: "openai" (chat/completions — most providers), "anthropic", "gemini".
public record Preset(string Name, string Kind, string BaseUrl, string[] Models, string KeyHint = "", bool NeedsKey = true, string Billing = "", bool SelfHosted = false)
{
    // gateways (one key, many companies' models) get their own Settings section
    public bool EditUrl => SelfHosted || BaseUrl.Contains("ACCOUNT_ID");   // the address is typed in (or completed) when adding it
    public bool Gateway => SelfHosted || Name is "OpenRouter" or "Vercel AI Gateway" or "Requesty" or "AI/ML API";
}   // Billing: the provider's usage page

public class ProviderCfg
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string Key { get; set; } = "";   // DPAPI-protected, base64; "" for keyless local servers
    public bool OnlyWorking { get; set; } = true;   // its model lists show only models that passed their check
    public override string ToString() => Name;
}

public static class Store
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("IVO_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ivo");   // IVO_DATA_DIR: a separate data folder (checks, side builds)
    static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
    static readonly string ChatsDir = Path.Combine(Dir, "chats");

    public static Settings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new();
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new();
            vaultOpen = s.LockWrap == "" || s.Vault == "";   // nothing sealed: the private fields in the file are the real ones
            return s;
        }
        catch { File.Copy(SettingsPath, SettingsPath + ".bad", true); vaultOpen = true; return new(); }   // unreadable: keep a copy before it's overwritten
    }

    // The personal parts of settings (to-dos, memories, profile, schedules, saved prompts, gateway key, Portal contacts,
    // webhook addresses) live in Settings.Vault,
    // sealed with the data key while the lock is on. The rest (the lock itself, theme, window) must be readable before unlock.
    static readonly string[] Private = ["Todos", "Memories", "Profile", "Schedules", "Prompts", "GatewayKey", "SearchKey", "Stars", "Projects", "PortalContacts", "Webhooks"];
    static bool vaultOpen;   // the private fields in memory are the real ones (not the empty stand-ins of a sealed launch)

    // after the first unlock: fill the private fields from the vault
    public static void OpenVault(Settings s)
    {
        if (s.Vault != "" && DataKey != null)
        {
            var from = JsonSerializer.Deserialize<Settings>(Encoding.UTF8.GetString(Crypto.Open(DataKey, s.Vault)))!;
            foreach (var name in Private) { var p = typeof(Settings).GetProperty(name)!; p.SetValue(s, p.GetValue(from)); }
        }
        vaultOpen = true;
    }
    // Every failure Ivo shows or swallows, one line each, for Settings → Data → Developer. Kept to the last ~2000 lines.
    public static readonly string FailLog = Path.Combine(Dir, "failures.log");
    static readonly object failLock = new();
    public static void Fail(string where, string message)
    {
        try
        {
            lock (failLock)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FailLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {where}: {message.ReplaceLineEndings(" ⏎ ").Trim()}{Environment.NewLine}");
                if (new FileInfo(FailLog).Length > 2_000_000) File.WriteAllLines(FailLog, File.ReadAllLines(FailLog).TakeLast(2000));
            }
        }
        catch { }
    }

    public static void SaveSettings(Settings s)
    {
        Directory.CreateDirectory(Dir);
        // model statuses of providers that are gone: out, so the file doesn't grow for ever
        var names = s.Providers.Select(p => p.Name).Append(Router.Name).ToHashSet();
        foreach (var k in s.ModelStatus.Keys.Where(k => !names.Contains(k.Split('|')[0])).ToList()) s.ModelStatus.Remove(k);
        var json = JsonSerializer.SerializeToNode(s)!.AsObject();
        if (EncryptOn)
        {
            if (vaultOpen && DataKey != null)   // seal the real private fields
            {
                var vault = new JsonObject();
                foreach (var name in Private) vault[name] = json[name]?.DeepClone();
                json["Vault"] = Crypto.Seal(DataKey, Encoding.UTF8.GetBytes(vault.ToJsonString()));
            }
            foreach (var name in Private) json.Remove(name);   // sealed launch: the vault stays as it was
        }
        else json["Vault"] = "";
        // a copy a day, the last 7 kept: settings lost or reset by mistake can be brought back
        try
        {
            var backups = Path.Combine(Dir, "backups");
            var today = Path.Combine(backups, $"settings-{DateTime.Now:yyyy-MM-dd}.json");
            if (File.Exists(SettingsPath) && !File.Exists(today))
            {
                Directory.CreateDirectory(backups);
                File.Copy(SettingsPath, today);
                foreach (var old in Directory.GetFiles(backups, "settings-*.json").Order().SkipLast(7)) File.Delete(old);
            }
        }
        catch { }
        File.WriteAllText(SettingsPath + ".tmp", json.ToJsonString());   // written whole, then swapped in: a crash mid-save can't truncate it
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }

    // The sidebar reloads after every reply; only files that changed since last time are read again.
    static readonly Dictionary<string, (DateTime Written, Chat? Chat)> loaded = [];

    public static List<Chat> LoadChats()
    {
        Directory.CreateDirectory(ChatsDir);
        var files = Directory.GetFiles(ChatsDir, "*.json");
        foreach (var gone in loaded.Keys.Except(files).ToList()) loaded.Remove(gone);
        return files.Select(f =>
        {
            var written = File.GetLastWriteTimeUtc(f);
            if (loaded.TryGetValue(f, out var hit) && hit.Written == written) return hit.Chat;
            Chat? c; string? text = null;
            try { text = Store.ReadData(f); c = text == null ? null : JsonSerializer.Deserialize<Chat>(text); } catch { c = null; }
            if (text != null) loaded[f] = (written, c);   // a sealed one is read again once unlocked
            return c;
        }).OfType<Chat>().OrderByDescending(c => c.Updated).ToList();
    }
    public static void SaveChat(Chat c)
    {
        if (c.Messages.Count == 0 || c.Incognito) return;
        Directory.CreateDirectory(ChatsDir);
        WriteData(Path.Combine(ChatsDir, c.Id + ".json"), JsonSerializer.Serialize(c));   // swapped in whole, sealed while the lock is on
    }
    public static void DeleteChat(Chat c) => File.Delete(Path.Combine(ChatsDir, c.Id + ".json"));

    // ---------- encryption at rest (on while the app lock is set) ----------
    // Chats, the Inbox and API keys are sealed with a random data key; the data key itself is stored only wrapped by your
    // PIN / password and by each recovery code (see MainWindow.Lock). Until Ivo is unlocked once, DataKey is null and
    // sealed files read as nothing and are never overwritten.
    public static bool EncryptOn;          // the lock is set
    public static byte[]? DataKey;          // known after the first unlock
    public static bool Sealed => EncryptOn && DataKey == null;
    const string Tag = "IVOENC1:";


    // a data file's text: null when it's sealed and Ivo hasn't been unlocked yet
    public static string? ReadData(string path)
    {
        var text = File.ReadAllText(path);
        if (!text.StartsWith(Tag)) return text;
        if (DataKey == null) return null;
        return Encoding.UTF8.GetString(Crypto.Open(DataKey, text[Tag.Length..]));
    }
    // false: sealed and locked, so nothing was written
    public static bool WriteData(string path, string text)
    {
        if (Sealed) return false;
        var body = EncryptOn ? Tag + Crypto.Seal(DataKey!, Encoding.UTF8.GetBytes(text)) : text;
        File.WriteAllText(path + ".tmp", body);   // written whole, then swapped in
        File.Move(path + ".tmp", path, true);
        return true;
    }

    // ---------- the activity log: every agent action that changed something, one line each ----------
    // Lines are sealed one by one while the lock is on; the newest 2000 are kept.
    public static readonly string AuditLog = Path.Combine(Dir, "activity.log");
    static readonly object auditLock = new();
    public static void Audit(string line)
    {
        if (Sealed) return;
        try
        {
            lock (auditLock)
            {
                Directory.CreateDirectory(Dir);
                var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line.ReplaceLineEndings(" ⏎ ")}";
                File.AppendAllText(AuditLog, (EncryptOn ? Tag + Crypto.Seal(DataKey!, Encoding.UTF8.GetBytes(entry)) : entry) + Environment.NewLine);
                if (new FileInfo(AuditLog).Length > 4_000_000) File.WriteAllLines(AuditLog, File.ReadAllLines(AuditLog).TakeLast(2000));
            }
        }
        catch { }
    }
    public static List<string> ReadAudit()
    {
        if (!File.Exists(AuditLog)) return [];
        lock (auditLock)
            return [.. File.ReadAllLines(AuditLog).Select(l => !l.StartsWith(Tag) ? l : DataKey == null ? null : TryOpen(l[Tag.Length..])).OfType<string>().Reverse()];
        static string? TryOpen(string s) { try { return Encoding.UTF8.GetString(Crypto.Open(DataKey!, s)); } catch { return null; } }
    }

    // re-save every data file in the current mode (after the lock is set or removed)
    public static void Reseal(Settings s)
    {
        loaded.Clear();
        foreach (var c in LoadChats()) SaveChat(c);
        _ = Inbox.All; Inbox.Save();
        var activity = ReadAudit(); activity.Reverse();
        if (File.Exists(AuditLog)) lock (auditLock) File.WriteAllLines(AuditLog, activity.Select(l => EncryptOn ? Tag + Crypto.Seal(DataKey!, Encoding.UTF8.GetBytes(l)) : l));
        foreach (var p in s.Providers) p.Key = Protect(UnprotectAny(p.Key));
        SaveSettings(s);
    }

    public static string Protect(string key)
    {
        if (key == "") return "";
        var blob = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        return EncryptOn && DataKey != null ? "enc:" + Crypto.Seal(DataKey, Encoding.UTF8.GetBytes(blob)) : blob;   // locked: a second layer only the passkey opens
    }
    // A key saved by another Windows account (or garbled, or sealed while locked) reads as no key.
    public static string Unprotect(string blob) => UnprotectAny(blob);
    static string UnprotectAny(string blob)
    {
        try
        {
            if (blob == "") return "";
            if (blob.StartsWith("enc:")) { if (DataKey == null) return ""; blob = Encoding.UTF8.GetString(Crypto.Open(DataKey, blob[4..])); }
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }
    }
}

public static class Api
{
    static readonly HttpClient Http = new(Net.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
    // Effort -> thinking token budget (Anthropic / Gemini). OpenAI-style APIs take the word directly.
    static readonly Dictionary<string, int> Budget = new() { ["low"] = 2048, ["medium"] = 8192, ["high"] = 24576 };

    public const string CustomName = "Custom (OpenAI-compatible)";

    public static readonly Preset[] Presets =
    [
        new("OpenAI", "openai", "https://api.openai.com/v1", ["gpt-5", "gpt-5-mini", "gpt-4.1", "o4-mini"], "sk-…", Billing: "https://platform.openai.com/usage"),
        new("Anthropic", "anthropic", "https://api.anthropic.com/v1", ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"], "sk-ant-…", Billing: "https://console.anthropic.com/settings/billing"),
        new("Gemini", "gemini", "https://generativelanguage.googleapis.com/v1beta", ["gemini-3.5-flash-lite"], "AIza… or AQ.…", Billing: "https://aistudio.google.com/usage"),
        new("OpenRouter", "openai", "https://openrouter.ai/api/v1", ["openrouter/auto", "anthropic/claude-sonnet-5-5", "openai/gpt-5"], "sk-or-…", Billing: "https://openrouter.ai/settings/credits"),
        new("Groq", "openai", "https://api.groq.com/openai/v1", ["llama-3.3-70b-versatile", "openai/gpt-oss-120b"], "gsk_…", Billing: "https://console.groq.com/settings/billing"),
        new("Mistral", "openai", "https://api.mistral.ai/v1", ["mistral-large-latest", "mistral-small-latest", "codestral-latest"], Billing: "https://console.mistral.ai/usage"),
        new("DeepSeek", "openai", "https://api.deepseek.com/v1", ["deepseek-chat", "deepseek-reasoner"], "sk-…", Billing: "https://platform.deepseek.com/usage"),
        new("xAI (Grok)", "openai", "https://api.x.ai/v1", ["grok-4", "grok-3-mini"], "xai-…", Billing: "https://console.x.ai"),
        new("Together AI", "openai", "https://api.together.xyz/v1", ["meta-llama/Llama-3.3-70B-Instruct-Turbo"], Billing: "https://api.together.ai/settings/billing"),
        new("Fireworks", "openai", "https://api.fireworks.ai/inference/v1", ["accounts/fireworks/models/llama-v3p3-70b-instruct"], "fw_…", Billing: "https://fireworks.ai/account/billing"),
        new("Perplexity", "openai", "https://api.perplexity.ai", ["sonar", "sonar-pro", "sonar-reasoning"], "pplx-…", Billing: "https://www.perplexity.ai/account/api/billing"),
        new("Cloudflare Workers AI", "openai", "https://api.cloudflare.com/client/v4/accounts/ACCOUNT_ID/ai/v1", ["@cf/meta/llama-3.3-70b-instruct-fp8-fast", "@cf/openai/gpt-oss-120b"], Billing: "https://dash.cloudflare.com/?to=/:account/ai/workers-ai"),
        new("Cerebras", "openai", "https://api.cerebras.ai/v1", ["llama-3.3-70b", "qwen-3-32b"], "csk-…", Billing: "https://cloud.cerebras.ai"),
        // Gateways: one key, many providers' models
        new("Vercel AI Gateway", "openai", "https://ai-gateway.vercel.sh/v1", ["openai/gpt-5", "anthropic/claude-sonnet-5.5", "google/gemini-3.5-flash"], Billing: "https://vercel.com/dashboard"),
        new("Requesty", "openai", "https://router.requesty.ai/v1", ["openai/gpt-5-mini", "anthropic/claude-sonnet-5-5"], Billing: "https://app.requesty.ai"),
        new("AI/ML API", "openai", "https://api.aimlapi.com/v1", ["openai/gpt-5-mini", "anthropic/claude-sonnet-5-5"], Billing: "https://aimlapi.com/app"),
        // Self-hosted gateways: the address is editable, since it depends on where they run
        new("OmniRoute (self-hosted)", "openai", "http://localhost:20128/v1", [], SelfHosted: true),
        new("LiteLLM (self-hosted)", "openai", "http://localhost:4000/v1", [], SelfHosted: true),
        new("Ollama (local)", "openai", "http://localhost:11434/v1", ["llama3.2", "qwen2.5"], NeedsKey: false),
        new("LM Studio (local)", "openai", "http://localhost:1234/v1", [], NeedsKey: false),
        new(CustomName, "openai", "", []),
    ];

    // Providers whose API key can read its own spend / balance; the rest only show it on their website.
    public static bool HasBalance(ProviderCfg p) => p.BaseUrl.Contains("openrouter.ai") || p.BaseUrl.Contains("api.deepseek.com");

    public static async Task<string?> Balance(ProviderCfg p, string key)
    {
        static string D(double? v) => $"${v ?? 0:0.00}";
        if (p.BaseUrl.Contains("openrouter.ai"))
        {
            var d = (await Call(Req(HttpMethod.Get, "https://openrouter.ai/api/v1/key", Bearer(key))))["data"];
            var lines = new List<string>
            {
                $"Spent with this key: {D((double?)d?["usage"])}  ·  today {D((double?)d?["usage_daily"])}  ·  this week {D((double?)d?["usage_weekly"])}  ·  this month {D((double?)d?["usage_monthly"])}",
            };
            if ((double?)d?["limit"] is { } limit) lines.Add($"Key limit: {D(limit)}  ·  {D((double?)d?["limit_remaining"])} left");
            try   // account credits; some keys aren't allowed to read them
            {
                var c = (await Call(Req(HttpMethod.Get, "https://openrouter.ai/api/v1/credits", Bearer(key))))["data"];
                var total = (double?)c?["total_credits"]; var used = (double?)c?["total_usage"];
                lines.Add($"Account credits: {D(total - used)} left of {D(total)}");
            }
            catch { }
            if ((bool?)d?["is_free_tier"] == true) lines.Add("Free tier (no credits bought yet)");
            return string.Join("\n", lines);
        }
        if (p.BaseUrl.Contains("api.deepseek.com"))
        {
            var b = (await Call(Req(HttpMethod.Get, "https://api.deepseek.com/user/balance", Bearer(key))))["balance_infos"]?[0];
            return $"Balance: {(string?)b?["total_balance"]} {(string?)b?["currency"]}  ·  topped up {(string?)b?["topped_up_balance"]}  ·  granted {(string?)b?["granted_balance"]}";
        }
        return null;
    }

    public static string[] ModelsFor(ProviderCfg? p) => p?.Kind switch
    {
        "auto" => Router.Routes,
        "llama" when IvoModel.Ready => [IvoModel.CodingModelId],
        "llama" => [],
        _ => Presets.FirstOrDefault(x => x.Name == p?.Name)?.Models ?? [],
    };
    public static bool NeedsKey(ProviderCfg p) => Presets.FirstOrDefault(x => x.Name == p.Name)?.NeedsKey ?? p.Key != "";

    public static Dictionary<string, string> Bearer(string key) => new() { ["Authorization"] = "Bearer " + key };
    public static Dictionary<string, string> AnthropicHeaders(string key) => new() { ["x-api-key"] = key, ["anthropic-version"] = "2023-06-01" };

    public static HttpRequestMessage Req(HttpMethod m, string url, Dictionary<string, string> headers, JsonNode? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        if (body != null) r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return r;
    }

    static string ErrorText(string txt, int status)
    {
        try
        {
            var err = JsonNode.Parse(txt)?["error"];
            if (err != null) return (err as JsonObject)?["message"]?.ToString() ?? err.ToString();
        }
        catch { }
        return $"HTTP {status}: {txt}";
    }

    public static async Task<JsonNode> Call(HttpRequestMessage req)
    {
        using var res = await Http.SendAsync(req);
        var txt = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception(ErrorText(txt, (int)res.StatusCode));
        return Parse(txt, req.RequestUri);
    }

    static JsonNode Parse(string txt, Uri? from)
    {
        try { return JsonNode.Parse(txt) ?? throw new Exception("Empty response"); }
        catch (System.Text.Json.JsonException) { throw new Exception($"{from?.Host} sent back \"{(txt.Length > 80 ? txt[..80] + "…" : txt).Trim()}\" instead of data. The service may be unavailable from this network."); }
    }

    public const string GitHubRetired = "GitHub Models was shut down by GitHub on July 30, 2026. Remove this provider and add another (OpenRouter, OpenAI, Anthropic, Gemini…).";

    // The "list models" call doubles as the key check.
    public static async Task<List<string>> ListModels(ProviderCfg p, string key)
    {
        if (Cli.Of(p) is { } cli) return Cli.Models(p, cli.Adapter);
        if (p.Kind == "auto") return [.. Router.Routes];
        key = Router.Keys(key).FirstOrDefault() ?? "";   // a pool: any of its keys can list the models
        if (p.BaseUrl.Contains("models.github.ai")) throw new Exception(GitHubRetired);
        if (p.BaseUrl.Contains("api.cloudflare.com") && p.BaseUrl.TrimEnd('/').EndsWith("/ai/v1"))   // Workers AI has no /models: its catalogue, text models only
        {
            var cf = await Call(Req(HttpMethod.Get, p.BaseUrl.TrimEnd('/')[..^3] + "/models/search?task=Text%20Generation&per_page=500", Bearer(key)));
            return cf["result"]?.AsArray().Select(m => (string?)m?["name"] ?? "").Where(x => x != "").OrderBy(x => x).ToList() ?? [];
        }
        var (req, field) = p.Kind switch
        {
            "anthropic" => (Req(HttpMethod.Get, p.BaseUrl + "/models?limit=1000", AnthropicHeaders(key)), "data"),
            "gemini" => (Req(HttpMethod.Get, p.BaseUrl + "/models?pageSize=1000", new() { ["x-goog-api-key"] = key }), "models"),
            _ => (Req(HttpMethod.Get, p.BaseUrl.TrimEnd('/') + "/models", key == "" ? new() : Bearer(key)), "data"),
        };
        var j = await Call(req);
        var list = j[field]?.AsArray()
                   // Gemini also lists embedding/imagen models; keep only ones that can chat
                   .Where(m => p.Kind != "gemini" || m?["supportedGenerationMethods"]?.AsArray().Any(x => (string?)x == "generateContent") == true)
                   .Select(m => ((string?)(m?["id"] ?? m?["name"]) ?? "").Replace("models/", ""))
                   .Where(x => x != "").OrderBy(x => x).ToList() ?? [];
        return [.. Router.Virtual, .. list];   // "auto" and "auto/free" on every provider: Ivo picks a model there
    }

    // One request for the chat in the provider's wire format; tools are added on agent turns.
    static HttpRequestMessage Build(ProviderCfg cfg, string key, Chat chat, List<ToolDef>? tools, int? maxTokens = null)
    {
        if (cfg.BaseUrl.Contains("models.github.ai")) throw new Exception(GitHubRetired);
        var effort = chat.Effort; var model = chat.Model ?? "";
        var sys = Redact.Mask(Agent.SystemPrompt(chat));
        switch (cfg.Kind)
        {
            case "anthropic":
            {
                var body = new JsonObject { ["model"] = model, ["max_tokens"] = 8192, ["messages"] = AnthropicMsgs(chat), ["stream"] = true };
                if (sys != "") body["system"] = sys;
                // Mid tool loop, thinking needs the last step's thinking blocks; skip it if they weren't kept.
                var midLoop = chat.Messages.Count > 0 && chat.Messages[^1].Role == "tool" && chat.Messages.LastOrDefault(m => m.Calls != null)?.Blocks == null;
                if (effort != "off" && !midLoop) { body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = Budget[effort] }; body["max_tokens"] = 8192 + Budget[effort]; }
                else body["temperature"] = Math.Min(chat.Temp, 1);
                if (tools != null) body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Desc, ["input_schema"] = t.Schema() }).ToArray());
                return Req(HttpMethod.Post, cfg.BaseUrl + "/messages", AnthropicHeaders(key), body);
            }
            case "gemini":
            {
                var gen = new JsonObject { ["temperature"] = chat.Temp };
                // Effort off: send no thinking setting at all (many models reject one).
                // Gemini 3+ takes a thinking level; 2.x takes a token budget.
                if (effort != "off")
                    gen["thinkingConfig"] = Regex.IsMatch(model, @"^gemini-[3-9]")
                        ? new JsonObject { ["thinkingLevel"] = effort == "high" ? "high" : "low", ["includeThoughts"] = true }
                        : new JsonObject { ["thinkingBudget"] = Budget[effort], ["includeThoughts"] = true };
                var body = new JsonObject { ["contents"] = GeminiContents(chat), ["generationConfig"] = gen };
                if (sys != "") body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = sys }) };
                if (tools != null)
                    body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Desc, ["parameters"] = t.Schema() }).ToArray()) });
                var url = $"{cfg.BaseUrl}/models/{model}:streamGenerateContent?alt=sse";
                return Req(HttpMethod.Post, url, new() { ["x-goog-api-key"] = key }, body);
            }
            default: // OpenAI-compatible
            {
                var msgs = OpenAiMsgs(chat);
                if (sys != "") msgs.Insert(0, new JsonObject { ["role"] = "system", ["content"] = sys });
                var body = new JsonObject { ["model"] = model, ["messages"] = msgs, ["stream"] = true };
                if (maxTokens != null) body["max_tokens"] = maxTokens;
                // token counts in the last chunk; only where the option is known to be accepted (others reject unknown fields)
                if (Regex.IsMatch(cfg.BaseUrl, @"api\.openai\.com|openrouter\.ai|api\.deepseek\.com")) body["stream_options"] = new JsonObject { ["include_usage"] = true };
                // OpenRouter only sends the reasoning back when asked in its own format
                if (effort != "off" && cfg.BaseUrl.Contains("openrouter.ai")) body["reasoning"] = new JsonObject { ["effort"] = effort };
                else if (effort != "off") body["reasoning_effort"] = effort;
                else if (!Regex.IsMatch(model, @"(^|/)(gpt-5|o\d)")) body["temperature"] = chat.Temp;   // reasoning models only take the default
                if (tools != null)
                    body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["type"] = "function", ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Desc, ["parameters"] = t.Schema() },
                    }).ToArray());
                return Req(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/chat/completions", key == "" ? new() : Bearer(key), body);
            }
        }
    }

    public record Reply(string Text, List<ToolCall> Calls, string Thought, string? Blocks, Usage? Usage, double? ModelSeconds = null);   // ModelSeconds: the model's own time, without a tool's start-up

    static JsonNode? First(JsonNode? list) => list is JsonArray { Count: > 0 } a ? a[0] : null;   // [0] on an empty array throws

    // Streams one reply: onDelta gets text as it arrives, onThought reasoning. Tool calls (agent turns) are put
    // together from their streamed pieces and returned with the full text, thinking blocks and token counts.
    public static async Task<Reply> Stream(ProviderCfg cfg, string key, Chat chat, List<ToolDef>? tools, Action<string> onDelta, Action<string>? onThought, CancellationToken ct)
    {
        // a local-only chat never reaches a cloud: Auto then only gets its local picks through (the others fail over)
        if (chat.LocalOnly && cfg.Kind != "auto" && (cfg.Kind == "cli" || cfg.Kind != "llama" && !Redact.IsLocal(cfg.BaseUrl)))
            throw new Exception($"This chat is set to local only, and {cfg.Name} is a cloud service. Pick a model running on this PC (Ollama, LM Studio), or turn local only off in the chat's menu.");
        if (cfg.Kind == "auto") return await Router.Stream(chat, tools, onDelta, onThought, ct);
        if (cfg.Kind == "llama")
        {
            if (!IvoModel.Ready) throw new Exception("The configured local model isn't available. Choose it in Settings → Assistant → Smarter chat, or pick another model.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var text = await IvoModel.CodingReply(chat, ct);
            if (string.IsNullOrWhiteSpace(text)) throw new Exception("The local model couldn't answer" + (IvoModel.LastError is { Length: > 0 } why ? ": " + why : "."));
            onDelta(text);
            return new(text, [], "", null, null, clock.Elapsed.TotalSeconds);
        }
        if (cfg.Kind != "cli" && Router.Virtual.Contains(chat.Model)) return await Router.Stream(chat, tools, onDelta, onThought, ct, cfg, chat.Model == "auto/free");
        // a pool of keys (comma-separated): start with the next one each time, move on when one is rate-limited or out of credit
        var keys = Router.Keys(key);
        if (keys.Length <= 1 || !Planner.S.PoolKeys) return await StreamOnce(cfg, keys.FirstOrDefault() ?? "", chat, tools, onDelta, onThought, ct);
        var start = Router.NextKey(cfg.Name, keys.Length);
        for (var i = 0; ; i++)
        {
            var sent = false;
            try { return await StreamOnce(cfg, keys[(start + i) % keys.Length], chat, tools, d => { sent = true; onDelta(d); }, onThought, ct); }
            catch (Exception ex) when (!sent && i < keys.Length - 1 && Router.Exhausted(ex.Message)) { }
        }
    }

    // Secrets (keys, tokens, passwords, card numbers) are swapped for placeholders before the request leaves the PC and put
    // back in the reply, so the provider never sees them. Local models (this PC / your network) get the real text.
    static async Task<Reply> StreamOnce(ProviderCfg cfg, string key, Chat chat, List<ToolDef>? tools, Action<string> onDelta, Action<string>? onThought, CancellationToken ct)
    {
        if (!Planner.S.RedactSecrets || Redact.IsLocal(cfg.BaseUrl)) return await StreamRaw(cfg, key, chat, tools, onDelta, onThought, ct);
        using var scope = Redact.Begin();
        var r = await StreamRaw(cfg, key, chat, tools, d => onDelta(Redact.Unmask(d)), onThought, ct);
        return r with { Text = Redact.Unmask(r.Text), Calls = r.Calls.Select(c => c with { Args = Redact.Unmask(c.Args) }).ToList() };
    }

    static async Task<Reply> StreamRaw(ProviderCfg cfg, string key, Chat chat, List<ToolDef>? tools, Action<string> onDelta, Action<string>? onThought, CancellationToken ct)
    {
        var req = Build(cfg, key, chat, tools);
        var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = ErrorText(await res.Content.ReadAsStringAsync(ct), (int)res.StatusCode);
            // OpenRouter with little credit: it reserves the model's longest reply; ask for one the credit covers instead
            if (Regex.Match(error, @"can only afford (\d+)") is { Success: true } afford && int.Parse(afford.Groups[1].Value) > 200)
            {
                res.Dispose();
                var retry = Build(cfg, key, chat, tools, maxTokens: int.Parse(afford.Groups[1].Value) * 9 / 10);
                res = await Http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode) { var e2 = ErrorText(await res.Content.ReadAsStringAsync(ct), (int)res.StatusCode); res.Dispose(); throw new Exception(e2); }
            }
            else { res.Dispose(); throw new Exception(error); }
        }
        using var _res = res;
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        var text = new StringBuilder(); var th = new StringBuilder(); var calls = new List<ToolCall>();
        Usage? usage = null; var sawData = false; var other = new StringBuilder();
        var oaCalls = new SortedDictionary<int, (string? Id, string Name, StringBuilder Args)>();   // OpenAI: tool calls arrive in pieces
        var blocks = new SortedDictionary<int, JsonObject>(); var partial = new Dictionary<int, StringBuilder>();   // Anthropic content blocks
        void Text(string? d) { if (string.IsNullOrEmpty(d)) return; text.Append(d); onDelta(d); }
        void Thought(string? d) { if (string.IsNullOrEmpty(d)) return; th.Append(d); onThought?.Invoke(d); }

        // Server-sent events: we only care about "data: {...}" lines.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) { if (!sawData && other.Length < 300) other.Append(line); continue; }
            sawData = true;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? j;
            try { j = JsonNode.Parse(data); } catch { continue; }
            if (j == null) continue;
            if (j["error"] is JsonObject err) throw new Exception((string?)err["message"] ?? err.ToJsonString());
            switch (cfg.Kind)
            {
                case "anthropic":
                {
                    var i = (int?)j["index"] ?? 0;
                    switch ((string?)j["type"])
                    {
                        case "message_start":
                            var u = j["message"]?["usage"];
                            usage = new(((int?)u?["input_tokens"] ?? 0) + ((int?)u?["cache_read_input_tokens"] ?? 0) + ((int?)u?["cache_creation_input_tokens"] ?? 0), 0);
                            break;
                        case "message_delta":
                            if ((int?)j["usage"]?["output_tokens"] is { } o) usage = (usage ?? new(0, 0)) with { Out = o };
                            break;
                        case "content_block_start":
                            blocks[i] = j["content_block"]!.DeepClone().AsObject();
                            break;
                        case "content_block_delta":
                            var d = j["delta"]; var b = blocks.GetValueOrDefault(i);
                            switch ((string?)d?["type"])
                            {
                                case "text_delta": Text((string?)d["text"]); break;
                                case "thinking_delta": var t = (string?)d["thinking"]; Thought(t); if (b != null) b["thinking"] = (string?)b["thinking"] + t; break;
                                case "signature_delta": if (b != null) b["signature"] = (string?)d["signature"]; break;
                                case "input_json_delta": (partial.TryGetValue(i, out var sb) ? sb : partial[i] = new()).Append((string?)d["partial_json"]); break;
                            }
                            break;
                        case "content_block_stop":
                            if (blocks.GetValueOrDefault(i) is { } tb && (string?)tb["type"] == "tool_use")
                                calls.Add(new((string)tb["id"]!, (string)tb["name"]!, partial.GetValueOrDefault(i)?.ToString() is { Length: > 0 } args ? args : "{}"));
                            break;
                    }
                    break;
                }
                case "gemini":
                {
                    foreach (var p in First(j["candidates"])?["content"]?["parts"]?.AsArray() ?? [])
                        if (p?["functionCall"] is { } f) calls.Add(new("call_" + Guid.NewGuid().ToString("N")[..12], (string)f["name"]!, f["args"]?.ToJsonString() ?? "{}", (string?)p["thoughtSignature"]));
                        else if ((bool?)p?["thought"] == true) Thought((string?)p["text"]);
                        else Text((string?)p?["text"]);
                    if (j["usageMetadata"] is { } um)
                        usage = new((int?)um["promptTokenCount"] ?? 0, ((int?)um["candidatesTokenCount"] ?? 0) + ((int?)um["thoughtsTokenCount"] ?? 0));
                    break;
                }
                default:
                {
                    var d = First(j["choices"])?["delta"];
                    Text((string?)d?["content"]);
                    Thought((string?)(d?["reasoning_content"] ?? d?["reasoning"]));
                    foreach (var c in d?["tool_calls"]?.AsArray() ?? [])
                    {
                        var i = (int?)c?["index"] ?? oaCalls.Count;
                        if (!oaCalls.TryGetValue(i, out var call)) call = ((string?)c?["id"], "", new());
                        if ((string?)c?["function"]?["name"] is { Length: > 0 } n) call.Name += n;
                        call.Args.Append((string?)c?["function"]?["arguments"]);
                        oaCalls[i] = call with { Id = call.Id ?? (string?)c?["id"] };
                    }
                    if ((j["usage"] ?? j["x_groq"]?["usage"]) is JsonObject u)
                        usage = new((int?)u["prompt_tokens"] ?? 0, (int?)u["completion_tokens"] ?? 0);
                    break;
                }
            }
        }
        // A 200 reply that isn't a stream at all (a proxy page, a retired service answering "OK")
        if (!sawData && other.Length > 0) throw new Exception($"{res.RequestMessage?.RequestUri?.Host} sent back \"{other.ToString().Trim()}\" instead of a reply.");
        foreach (var (_, c) in oaCalls)
            calls.Add(new(c.Id ?? "call_" + Guid.NewGuid().ToString("N")[..12], c.Name, c.Args.Length > 0 ? c.Args.ToString() : "{}"));
        var thinking = blocks.Values.Where(b => (string?)b["type"] is "thinking" or "redacted_thinking").Select(b => (JsonNode)b).ToArray();
        return new(text.ToString().Trim(), calls, th.ToString().Trim(), thinking.Length > 0 ? new JsonArray(thinking).ToJsonString() : null, usage);
    }

    // Tiny "does this model answer?" request: stops as soon as the first text arrives.
    public static async Task<string> Probe(ProviderCfg cfg, string key, string model)
    {
        var chat = new Chat { Model = model, Effort = "off", Temp = 0, Messages = [new("user", "Hi")] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var answered = false;
        try { await Stream(cfg, key, chat, null, _ => { answered = true; cts.Cancel(); }, null, cts.Token); }
        catch (OperationCanceledException) { if (!answered) return "slow: No reply within 60 seconds."; }
        catch (Exception ex) { return ex.Message; }
        return "ok";
    }

    // Provider-specific message shapes; images ride along with the text they were sent with.
    // Tool turns: an assistant message carries its calls, then one "tool" message per result.
    static JsonObject Plain(Msg m, bool anthropic)
    {
        if (m.Images is not { Count: > 0 }) return new JsonObject { ["role"] = m.Role, ["content"] = m.Text };
        var parts = new JsonArray();
        foreach (var img in m.Images)
            parts.Add(anthropic
                ? new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = img.Mime, ["data"] = img.B64 } }
                : new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{img.Mime};base64,{img.B64}" } });
        if (m.Text != "") parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
        return new JsonObject { ["role"] = m.Role, ["content"] = parts };
    }

    // An empty reply (no text, no tool calls) is left out: providers reject empty assistant content.
    static IEnumerable<Msg> Sendable(Chat chat) => Context.Visible(chat).Where(m => m.Role != "assistant" || m.Text != "" || m.Calls is { Count: > 0 })
        .Select(m => Redact.Active ? m with { Text = Redact.Mask(m.Text), Calls = m.Calls?.Select(c => c with { Args = Redact.Mask(c.Args) }).ToList() } : m);

    static JsonArray OpenAiMsgs(Chat chat) => new(Sendable(chat).Select(m => (JsonNode)(
        m.Role == "tool" ? new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.CallId, ["content"] = m.Text }
        : m.Calls is { Count: > 0 } ? new JsonObject
        {
            ["role"] = "assistant", ["content"] = m.Text == "" ? null : m.Text,
            ["tool_calls"] = new JsonArray(m.Calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args },
            }).ToArray()),
        }
        : Plain(m, false))).ToArray());

    // Anthropic wants each turn's tool results together in one user message.
    static JsonArray AnthropicMsgs(Chat chat)
    {
        var a = new JsonArray();
        foreach (var m in Sendable(chat))
        {
            if (m.Role == "tool")
            {
                var r = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.CallId, ["content"] = m.Text };
                if (a.Count > 0 && a[a.Count - 1]!["content"] is JsonArray prev && prev.Count > 0 && (string?)prev[0]!["type"] == "tool_result") prev.Add(r);
                else a.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(r) });
            }
            else if (m.Calls is { Count: > 0 })
            {
                var c = m.Blocks != null ? JsonNode.Parse(m.Blocks)!.AsArray() : new JsonArray();
                if (m.Text != "") c.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                foreach (var t in m.Calls) c.Add(new JsonObject { ["type"] = "tool_use", ["id"] = t.Id, ["name"] = t.Name, ["input"] = JsonNode.Parse(t.Args) ?? new JsonObject() });
                a.Add(new JsonObject { ["role"] = "assistant", ["content"] = c });
            }
            else a.Add(Plain(m, true));
        }
        return a;
    }

    static JsonArray GeminiContents(Chat chat)
    {
        var a = new JsonArray();
        foreach (var m in Sendable(chat))
        {
            if (m.Role == "tool")
            {
                var r = new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = m.Name, ["response"] = new JsonObject { ["result"] = m.Text } } };
                if (a.Count > 0 && a[a.Count - 1]!["parts"] is JsonArray prev && prev.Count > 0 && prev[0]!["functionResponse"] != null) prev.Add(r);
                else a.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(r) });
            }
            else if (m.Calls is { Count: > 0 })
            {
                var parts = new JsonArray();
                if (m.Text != "") parts.Add(new JsonObject { ["text"] = m.Text });
                foreach (var c in m.Calls)
                {
                    var p = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = JsonNode.Parse(c.Args) ?? new JsonObject() } };
                    if (c.Sig != null) p["thoughtSignature"] = c.Sig;
                    parts.Add(p);
                }
                a.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
            else a.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "model" : "user", ["parts"] = GeminiParts(m) });
        }
        return a;
    }

    static JsonArray GeminiParts(Msg m)
    {
        var parts = new JsonArray();
        foreach (var img in m.Images ?? []) parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = img.Mime, ["data"] = img.B64 } });
        if (m.Text != "" || parts.Count == 0) parts.Add(new JsonObject { ["text"] = m.Text });
        return parts;
    }
}

// Prices from OpenRouter's public model list (no key needed), loaded once at startup. Model names are matched loosely
// (vendor prefix, dots vs dashes, date suffix), so most providers' models are found; unknown ones just show tokens.
public static class Pricing
{
    static Dictionary<string, (double In, double Out)>? table;

    public static async void Warm()
    {
        try
        {
            var j = await Api.Call(Api.Req(HttpMethod.Get, "https://openrouter.ai/api/v1/models", new()));
            var t = new Dictionary<string, (double, double)>();
            foreach (var m in j["data"]?.AsArray() ?? [])
            {
                if (!double.TryParse((string?)m?["pricing"]?["prompt"], System.Globalization.CultureInfo.InvariantCulture, out var pi) ||
                    !double.TryParse((string?)m?["pricing"]?["completion"], System.Globalization.CultureInfo.InvariantCulture, out var po) || pi < 0 || po < 0) continue;
                var id = (string?)m?["id"] ?? "";
                t[Norm(id)] = (pi, po);
                t.TryAdd(Norm(id[(id.IndexOf('/') + 1)..]), (pi, po));
            }
            table = t;
        }
        catch { /* offline: costs stay unknown */ }
    }

    static string Norm(string id) => Regex.Replace(id.ToLowerInvariant().Replace('.', '-'), @"-\d{8}$|-latest$|:free$", "");

    public static double? Cost(string model, Usage u)
    {
        if (table == null || model == "") return null;
        if (!table.TryGetValue(Norm(model), out var p) && !table.TryGetValue(Norm(model[(model.IndexOf('/') + 1)..]), out p)) return null;
        return u.In * p.In + u.Out * p.Out;
    }

    // $ per million output tokens, when known (for the expensive-model warning)
    public static double? OutPerMillion(string model)
    {
        if (table == null || model == "") return null;
        if (!table.TryGetValue(Norm(model), out var p) && !table.TryGetValue(Norm(model[(model.IndexOf('/') + 1)..]), out p)) return null;
        return p.Out * 1_000_000;
    }

    public static string Format(Usage u)
    {
        static string K(int n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();
        var cost = u.Cost is not { } c ? "" : c == 0 ? " · free" : c < 0.01 ? $" · ~${c:0.0000}" : $" · ~${c:0.00}";
        return $"{K(u.In)} in · {K(u.Out)} out{cost}";
    }
}

// Guesses whether pasted text is code (or a shell command) and which language, for fencing and labels.
// ponytail: keyword heuristics; good for the common languages, a real classifier only if guesses annoy.
public static class Code
{
    static readonly (string Lang, string Pattern)[] Signs =
    [
        ("json", @"^\s*[\[{]\s*""[^""]+""\s*:"),
        ("html", @"^\s*<(!doctype|html|div|head|body|span|p|a|ul|section|script)\b"),
        ("xml", @"^\s*<(\?xml|[\w:]+[^>]*xmlns)"),
        ("csharp", @"\b(using System|namespace \w|public (static |sealed |partial )*(class|record|void|async)|Console\.Write|var \w+ = new)\b"),
        ("typescript", @"\b(interface \w+ \{|: (string|number|boolean)\b|import .* from '|export (type|interface))"),
        ("javascript", @"\b(const|let) \w+ = |function \w*\(|=> \{|console\.log|require\(|module\.exports"),
        ("python", @"^\s*(def \w+\(.*\):|import \w+$|from \w+ import|class \w+(\(.*\))?:|if __name__|print\()"),
        ("java", @"\b(public static void main|System\.out\.println|import java\.)"),
        ("cpp", @"#include\s*<|std::|int main\("),
        ("go", @"^\s*(package \w+|func \w+\(|import \()"),
        ("rust", @"\b(fn \w+\(|let mut |impl \w+|use std::)"),
        ("sql", @"^\s*(SELECT .+ FROM|INSERT INTO|UPDATE \w+ SET|CREATE TABLE|DELETE FROM)\b"),
        ("css", @"^\s*[.#]?[\w-]+(\s*[,>]\s*[.#]?[\w-]+)*\s*\{\s*$|^\s*[\w-]+\s*:\s*[^;]+;\s*$"),
        ("yaml", @"^[\w-]+:\s*$\n^\s+[\w-]+:"),
        ("powershell", @"\b(Get|Set|New|Remove|Invoke|Start|Stop|Write)-[A-Z]\w+|\$env:|\$PSScriptRoot"),
        ("bash", @"^(#!/bin/(ba)?sh|\s*(sudo|apt|apt-get|brew|chmod|export \w+=|echo \$))"),
    ];

    const string Command = @"^\s*(git|npm|npx|yarn|pnpm|dotnet|pip|pip3|python|py|node|cd|ls|dir|mkdir|docker|kubectl|curl|wget|winget|choco|scoop|cargo|go|java|javac|mvn|gradle|az|aws|gcloud|ssh|code|sudo|apt|brew|[A-Z][a-z]+-[A-Z]\w+)\s+\S";

    public static string? Language(string text)
    {
        foreach (var (lang, pattern) in Signs)
            if (Regex.IsMatch(text, pattern, RegexOptions.Multiline | (lang == "sql" ? RegexOptions.IgnoreCase : 0))) return lang;
        return Regex.IsMatch(text, Command) ? (Regex.IsMatch(text, @"[A-Z][a-z]+-[A-Z]\w+") ? "powershell" : "shell") : null;
    }

    // A one-line command, or several lines with code structure. Plain prose stays plain.
    public static bool LooksLikeCode(string text)
    {
        text = text.Trim();
        if (text == "" || text.Contains("```")) return false;
        var lines = text.Split('\n');
        if (lines.Length == 1) return (Regex.IsMatch(text, Command) || Language(text) != null) && !Regex.IsMatch(text, @"[.!?]$") && text.Split(' ').Length < 25;
        if (Language(text) is not null and not "shell") return true;
        var codey = lines.Count(l => Regex.IsMatch(l, @"[;{}]\s*$|^\s{2,}\S|^\s*(//|#|--)\s|\w\(.*\)|=>|==|\+=|^\s*<\w"));
        return codey >= Math.Max(2, lines.Length / 2);
    }
}
