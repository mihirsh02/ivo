using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ivo;

// Your activity on this PC (the Activity panel and the rail at the bottom of Ivo): every 5 seconds, what's in front (and,
// with more than one screen, a video or meeting showing on another one), as
// an app or (in a browser) the site its tab title names ("… - YouTube - Google Chrome" is YouTube), sorted into
// Productive, Meetings, Videos, Games, Social, Music, Tools and Other. Away (no input for 3 minutes, unless it's a video or a
// meeting), locked and asleep don't count. Typing speed: keys pressed are counted (never which), five to a word, over
// the time you were typing. Only names and seconds are kept, sealed with the app lock like the rest of Ivo's data, for
// 60 days; no window titles. Private (incognito, InPrivate) windows aren't counted at all. Nothing leaves this PC
// except a site's address when its icon is fetched (once; Google's favicon service).
public static class Activity
{
    public class Day
    {
        public Dictionary<string, int> Apps { get; set; } = [];             // "YouTube" → seconds
        public Dictionary<string, string> Kinds { get; set; } = [];         // "YouTube" → "Videos"
        public Dictionary<string, int[]> Hours { get; set; } = [];          // "Videos" → seconds in each hour
        public int Keys { get; set; }                                       // keys typed
        public int TypingSeconds { get; set; }                              // time spent typing (5-second spells with keys in)
        public int Screen { get; set; }                                     // time at the PC (each 5 s counted once, however many screens)
        public int Total => Screen > 0 ? Screen : Apps.Values.Sum();
        public int In(string kind) => Apps.Where(a => KindOf(a.Key, Kinds.GetValueOrDefault(a.Key, "Other")) == kind).Sum(a => a.Value);
    }

    public static readonly (string Name, string Color)[] Categories =
        [("Productive", "#22C55E"), ("Meetings", "#60A5FA"), ("Videos", "#F43F5E"), ("Games", "#F59E0B"), ("Social", "#A78BFA"), ("Music", "#22D3EE"), ("Tools", "#38BDF8"), ("Other", "#8B95A3")];

    public static bool On = true;
    public static (string Label, string Kind)? Current { get; private set; }
    public static event Action? Ticked;   // from a timer thread: hop to the window's

    static Dictionary<string, Day>? days;
    static string File => Path.Combine(Store.Dir, "activity.json");
    static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    static Dictionary<string, Day> Days
    {
        get
        {
            if (days != null) return days;
            try { days = Store.ReadData(File) is { Length: > 0 } json ? JsonSerializer.Deserialize<Dictionary<string, Day>>(json) ?? [] : []; } catch { days = []; }
            foreach (var d in days.Values) Tidy(d);
            return days;
        }
    }
    // an app counted under its file's name ("SnippingTool.exe") or another name of its own ("Windows Explorer") goes under
    // the one name it has now
    static readonly Dictionary<string, string> SameApp = new() { ["Windows Explorer"] = "File Explorer" };
    static void Tidy(Day d)
    {
        foreach (var (was, now) in SameApp)
            if (d.Apps.Remove(was, out var sec)) { d.Apps[now] = d.Apps.GetValueOrDefault(now) + sec; if (d.Kinds.Remove(was, out var k)) d.Kinds.TryAdd(now, k); }
        foreach (var file in d.Apps.Keys.Where(k => k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var exe = file[..^4].ToLowerInvariant();
            var name = Names.GetValueOrDefault(exe) ?? System.Text.RegularExpressions.Regex.Replace(file[..^4], "(?<=[a-z])(?=[A-Z])", " ");
            d.Apps[name] = d.Apps.GetValueOrDefault(name) + d.Apps[file]; d.Apps.Remove(file);
            if (d.Kinds.Remove(file, out var k)) d.Kinds.TryAdd(name, k);
        }
    }

    public static Day DayOf(DateTime d) { lock (Days) return Days.TryGetValue(Key(d), out var x) ? x : Days[Key(d)] = new Day(); }

    static System.Threading.Timer? timer;
    static bool locked, asleep;
    static int ticks;

    public static void Start()
    {
        if (timer != null) return;
        LoadIcons();
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect) locked = true;
            else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect) locked = false;
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Suspend) asleep = true; else if (e.Mode == Microsoft.Win32.PowerModes.Resume) asleep = false; };
        timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    static string IconFile => Path.Combine(Store.Dir, "activity-icons.json");
    public static void LoadIcons() { try { if (Store.ReadData(IconFile) is { Length: > 0 } j && JsonSerializer.Deserialize<Dictionary<string, string>>(j) is { } m) lock (IconOf) foreach (var (k, v) in m) IconOf[k] = v; } catch { } }
    public static void Save() { lock (IconOf) Store.WriteData(IconFile, JsonSerializer.Serialize(IconOf)); lock (Days) { foreach (var old in Days.Keys.Where(k => string.CompareOrdinal(k, Key(DateTime.Today.AddDays(-60))) < 0).ToList()) Days.Remove(old); Store.WriteData(File, JsonSerializer.Serialize(Days)); } }

    static void Tick()
    {
        try
        {
            if (!On || locked || asleep) { Current = null; Ticked?.Invoke(); return; }
            var spell = Interlocked.Exchange(ref keysNow, 0);
            lock (recent) { recent.Enqueue(spell); while (recent.Count > 120) recent.Dequeue(); }   // the last 10 minutes, 5 seconds a step
            var fg = GetForegroundWindow();
            var (label, kind) = What(fg);
            var idle = Idle();
            var front = label != "" && (idle <= TimeSpan.FromMinutes(3) || kind is "Videos" or "Meetings");
            // on another screen: the window on top there counts too when it's watched or listened to (a video, a meeting)
            var others = OtherScreens(fg).Where(o => o.Kind is "Videos" or "Meetings" && o.Label != label).DistinctBy(o => o.Label).ToList();
            if (!front && others.Count == 0) { Current = null; Ticked?.Invoke(); return; }
            Current = front ? (label, kind) : others[0];
            var day = DayOf(DateTime.Today);
            lock (Days)
            {
                void Count(string l, string k)
                {
                    day.Apps[l] = day.Apps.GetValueOrDefault(l) + 5;
                    day.Kinds[l] = k;
                    if (!day.Hours.TryGetValue(k, out var h)) day.Hours[k] = h = new int[24];
                    h[DateTime.Now.Hour] += 5;
                }
                if (front) Count(label, kind);
                foreach (var o in others) Count(o.Label, o.Kind);
                if (day.Screen == 0) day.Screen = day.Apps.Values.Sum() - 5 * (front ? 1 : 0) - 5 * others.Count;   // a day counted before Screen: its time so far
                day.Screen += 5;
                if (spell > 0) { day.Keys += spell; day.TypingSeconds += 5; }
            }
            if (++ticks % 12 == 0) Save();   // once a minute
            Ticked?.Invoke();
        }
        catch (Exception ex) { Store.Fail("Activity", ex.Message); }
    }

    // ---------- typing speed ----------
    static int keysNow;
    static readonly Queue<int> recent = new();
    // words a minute over the last 10 minutes, counting only the time you were typing (null: you haven't typed)
    public static int? Wpm()
    {
        int keys, spells;
        lock (recent) { keys = recent.Sum(); spells = recent.Count(k => k > 0); }
        return spells == 0 || keys < 10 ? null : (int)Math.Round(keys / 5.0 / (spells * 5 / 60.0));
    }
    public static int? WpmOn(Day d) => d.TypingSeconds < 30 ? null : (int)Math.Round(d.Keys / 5.0 / (d.TypingSeconds / 60.0));

    // the keyboard, counted: a low-level hook (on the window's thread, which pumps messages) that adds one per key typed
    // (letters, digits, space, punctuation; not shortcuts, arrows or the like). Which key it was is never kept.
    static nint hook;
    static readonly HookProc proc = KeyDown;
    public static void Listen()
    {
        if (hook != 0) return;
        hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);   // WH_KEYBOARD_LL
    }
    static nint KeyDown(int code, nint w, nint l)
    {
        if (code >= 0 && w is 0x100 or 0x104)   // WM_KEYDOWN, WM_SYSKEYDOWN
        {
            var vk = Marshal.ReadInt32(l);
            if (vk is >= 0x30 and <= 0x5A or 0x20 or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE or 0x0D or 0x08)
                if ((GetKeyState(0x11) & 0x8000) == 0 && (GetKeyState(0x12) & 0x8000) == 0) Interlocked.Increment(ref keysNow);   // not Ctrl or Alt shortcuts
        }
        return CallNextHookEx(hook, code, w, l);
    }

    // ---------- what's in front ----------
    static readonly Dictionary<int, string> names = [];
    static (string Label, string Kind) What(nint h)
    {
        if (h == 0) return ("", "");
        GetWindowThreadProcessId(h, out var pid);
        string exe;
        try { using var p = Process.GetProcessById((int)pid); exe = p.ProcessName.ToLowerInvariant(); } catch { return ("", ""); }
        if (exe is "lockapp" or "logonui" or "screensaver" or "searchhost" or "startmenuexperiencehost" or "shellexperiencehost" or "screenclippinghost") return ("", "");   // Windows itself (Start, search, the snip overlay): not an activity
        // the desktop, the taskbar, the tray and Windows' own pop-ups are explorer.exe too, but not something you're using
        var cls = new System.Text.StringBuilder(128);
        GetClassName(h, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
            or "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow" or "MultitaskingViewFrame" or "ForegroundStaging") return ("", "");
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        var title = sb.ToString();
        if (Browsers.Contains(exe) && Private.IsMatch(title)) return ("", "");   // incognito / InPrivate / private browsing: not counted, not kept
        var what = Classify(exe, title, (int)pid, FullScreen(h));
        // where its icon comes from: the program's file, or (a site) its web address
        if (!Browsers.Contains(exe)) { lock (IconOf) if (!IconOf.ContainsKey(what.Label)) try { using var p = Process.GetProcessById((int)pid); if (p.MainModule?.FileName is { } f) IconOf[what.Label] = f; } catch { } }
        return what;
    }

    static readonly System.Text.RegularExpressions.Regex Private = new(@"(?i)\b(incognito|inprivate|private browsing|private window)\b");

    // each app's program file (its icon), learnt as it's used; a site's icon is its address's
    public static readonly Dictionary<string, string> IconOf = [];
    public static readonly Dictionary<string, string> SiteOf = new()
    {
        ["YouTube"] = "youtube.com", ["Netflix"] = "netflix.com", ["Prime Video"] = "primevideo.com", ["Hotstar"] = "hotstar.com", ["JioCinema"] = "jiocinema.com", ["Twitch"] = "twitch.tv",
        ["Disney+"] = "disneyplus.com", ["Hulu"] = "hulu.com", ["Crunchyroll"] = "crunchyroll.com", ["Google Meet"] = "meet.google.com", ["Zoom"] = "zoom.us", ["Teams"] = "teams.microsoft.com",
        ["GitHub"] = "github.com", ["Google Docs"] = "docs.google.com", ["Google Sheets"] = "sheets.google.com", ["Google Slides"] = "slides.google.com", ["Notion"] = "notion.so", ["Figma"] = "figma.com",
        ["Stack Overflow"] = "stackoverflow.com", ["ChatGPT"] = "chatgpt.com", ["Claude"] = "claude.ai", ["Gmail"] = "mail.google.com", ["Outlook"] = "outlook.com", ["Jira"] = "atlassian.com",
        ["Confluence"] = "atlassian.com", ["Linear"] = "linear.app", ["Google Calendar"] = "calendar.google.com", ["Google Drive"] = "drive.google.com", ["LeetCode"] = "leetcode.com",
        ["WhatsApp"] = "whatsapp.com", ["Instagram"] = "instagram.com", ["Facebook"] = "facebook.com", ["Reddit"] = "reddit.com", ["LinkedIn"] = "linkedin.com", ["X"] = "x.com",
        ["Discord"] = "discord.com", ["Telegram"] = "telegram.org", ["Spotify"] = "spotify.com", ["YouTube Music"] = "music.youtube.com", ["SoundCloud"] = "soundcloud.com",
    };

    // the window on top of each screen other than the one in front: what you'd see there (not minimised, not hidden)
    static List<(string Label, string Kind)> OtherScreens(nint front)
    {
        var mine = MonitorFromWindow(front, 2);
        var seen = new HashSet<nint> { mine };
        var found = new List<(string, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, 4) != 0) return true;   // hidden, minimised, or owned by another window
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;   // on another desktop, or suspended
            var mon = MonitorFromWindow(h, 0);
            if (mon == 0 || !seen.Add(mon)) return true;   // a screen already taken by a window above
            var what = What(h);
            if (what.Label != "") found.Add(what);
            return true;
        }, 0);
        return found;
    }

    static readonly string[] Browsers = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "iexplore"];
    static readonly (string Match, string Label, string Kind)[] Sites =
    [
        ("youtube", "YouTube", "Videos"), ("netflix", "Netflix", "Videos"), ("prime video", "Prime Video", "Videos"), ("hotstar", "Hotstar", "Videos"), ("jiocinema", "JioCinema", "Videos"),
        ("twitch", "Twitch", "Videos"), ("disney+", "Disney+", "Videos"), ("hulu", "Hulu", "Videos"), ("crunchyroll", "Crunchyroll", "Videos"),
        ("meet - ", "Google Meet", "Meetings"), ("google meet", "Google Meet", "Meetings"), ("zoom", "Zoom", "Meetings"), ("microsoft teams", "Teams", "Meetings"),
        ("github", "GitHub", "Productive"), ("google docs", "Google Docs", "Productive"), ("google sheets", "Google Sheets", "Productive"), ("google slides", "Google Slides", "Productive"),
        ("notion", "Notion", "Productive"), ("figma", "Figma", "Productive"), ("stack overflow", "Stack Overflow", "Productive"), ("chatgpt", "ChatGPT", "Productive"),
        ("claude", "Claude", "Productive"), ("gmail", "Gmail", "Productive"), ("outlook", "Outlook", "Productive"), ("jira", "Jira", "Productive"), ("confluence", "Confluence", "Productive"),
        ("linear", "Linear", "Productive"), ("google calendar", "Google Calendar", "Productive"), ("google drive", "Google Drive", "Productive"), ("leetcode", "LeetCode", "Productive"),
        ("whatsapp", "WhatsApp", "Social"), ("instagram", "Instagram", "Social"), ("facebook", "Facebook", "Social"), ("reddit", "Reddit", "Social"), ("linkedin", "LinkedIn", "Social"),
        (" / x", "X", "Social"), ("twitter", "X", "Social"), ("discord", "Discord", "Social"), ("telegram", "Telegram", "Social"),
        ("spotify", "Spotify", "Music"), ("youtube music", "YouTube Music", "Music"), ("soundcloud", "SoundCloud", "Music"),
    ];
    static readonly Dictionary<string, string> AppKinds = new()
    {
        ["devenv"] = "Productive", ["code"] = "Productive", ["cursor"] = "Productive", ["rider64"] = "Productive", ["idea64"] = "Productive", ["pycharm64"] = "Productive", ["webstorm64"] = "Productive",
        ["androidstudio64"] = "Productive", ["winword"] = "Productive", ["excel"] = "Productive", ["powerpnt"] = "Productive", ["onenote"] = "Productive", ["notion"] = "Productive", ["obsidian"] = "Productive",
        ["figma"] = "Productive", ["windowsterminal"] = "Productive", ["powershell"] = "Productive", ["pwsh"] = "Productive", ["cmd"] = "Productive", ["wt"] = "Productive", ["slack"] = "Productive",
        ["outlook"] = "Productive", ["olk"] = "Productive", ["photoshop"] = "Productive", ["illustrator"] = "Productive", ["blender"] = "Productive", ["unity"] = "Productive", ["notepad"] = "Productive", ["notepad++"] = "Productive",
        ["ms-teams"] = "Meetings", ["teams"] = "Meetings", ["zoom"] = "Meetings", ["webex"] = "Meetings",
        ["vlc"] = "Videos", ["mpc-hc64"] = "Videos", ["wmplayer"] = "Videos", ["video.ui"] = "Videos", ["potplayermini64"] = "Videos", ["netflix"] = "Videos",
        ["steam"] = "Games", ["epicgameslauncher"] = "Games", ["riotclientservices"] = "Games", ["battle.net"] = "Games",
        ["whatsapp"] = "Social", ["telegram"] = "Social", ["discord"] = "Social",
        ["spotify"] = "Music",
        // Tools: Windows' own utilities and the like
        ["snippingtool"] = "Tools", ["explorer"] = "Tools", ["systemsettings"] = "Tools", ["taskmgr"] = "Tools", ["mspaint"] = "Tools", ["calculatorapp"] = "Tools", ["calc"] = "Tools",
        ["regedit"] = "Tools", ["mmc"] = "Tools", ["control"] = "Tools", ["7zfm"] = "Tools", ["winrar"] = "Tools", ["everything"] = "Tools", ["powertoys"] = "Tools", ["applicationframehost"] = "Other",
    };

    // the names we give apps (others: their file's description)
    static readonly Dictionary<string, string> Names = new()
    {
        ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox", ["brave"] = "Brave", ["code"] = "VS Code", ["devenv"] = "Visual Studio", ["windowsterminal"] = "Terminal", ["wt"] = "Terminal",
        ["snippingtool"] = "Snipping Tool", ["explorer"] = "File Explorer", ["systemsettings"] = "Settings", ["taskmgr"] = "Task Manager", ["mspaint"] = "Paint", ["calculatorapp"] = "Calculator",
    };

    // the kind an app or site counts as now: yours if you picked one, else ours (so a change to ours shows on days already
    // counted), else what it was counted as
    static readonly Lazy<Dictionary<string, string>> OurKinds = new(() =>
    {
        var k = new Dictionary<string, string>();
        foreach (var (exe, kind) in AppKinds) k.TryAdd(Names.GetValueOrDefault(exe, exe), kind);
        foreach (var (_, label, kind) in Sites) k.TryAdd(label, kind);
        return k;
    });
    public static string KindOf(string label, string counted) => Overrides.GetValueOrDefault(label) ?? OurKinds.Value.GetValueOrDefault(label) ?? counted;

    // an app or a site, and its kind (yours first: a correction made in the Activity panel)
    public static Dictionary<string, string> Overrides { get; set; } = [];
    public static (string Label, string Kind) Classify(string exe, string title, int pid = 0, bool full = false)
    {
        var low = title.ToLowerInvariant();
        (string, string) Mine((string L, string K) x) => (x.L, Overrides.GetValueOrDefault(x.L, x.K));
        if (Browsers.Contains(exe))
        {
            foreach (var (m, l, k) in Sites) if (low.Contains(m)) return Mine((l, k));
            return Mine(($"{Pretty(exe, pid)} · other", "Other"));
        }
        if (exe is "ivo" or "ivo portal") return Mine((exe == "ivo" ? "Ivo" : "Ivo Portal", low.Contains("call") ? "Meetings" : "Productive"));
        if (exe == "discord" && (low.Contains("voice") || low.Contains("call"))) return Mine(("Discord", "Meetings"));
        if (AppKinds.TryGetValue(exe, out var kind)) return Mine((Pretty(exe, pid), kind));
        return Mine((Pretty(exe, pid), full ? "Games" : "Other"));   // an app we don't know, full screen: most likely a game (a Windows tool we know is named above)
    }

    // the app's own name ("Visual Studio Code"), from its file, kept per program
    static string Pretty(string exe, int pid)
    {
        lock (names) if (names.TryGetValue(pid, out var n)) return n;
        var name = Names.GetValueOrDefault(exe, "");
        if (name == "")
            try { using var p = Process.GetProcessById(pid); name = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d && !d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? d : ""; } catch { }
        if (name == "") name = exe.Length > 0 ? char.ToUpper(exe[0]) + exe[1..] : "App";
        lock (names) { if (names.Count > 500) names.Clear(); names[pid] = name; }
        return name;
    }

    static bool FullScreen(nint h)
    {
        if (!GetWindowRect(h, out var r)) return false;
        var mon = MonitorFromWindow(h, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(mon, ref info) && r.L <= info.rcMonitor.L && r.T <= info.rcMonitor.T && r.R >= info.rcMonitor.R && r.B >= info.rcMonitor.B;
    }

    static TimeSpan Idle()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime)) : TimeSpan.Zero;
    }

    // ---------- asked in her chat ----------
    // "how long was I on youtube today", "my screen time yesterday", "how much did I game this week", "my typing speed"
    static readonly System.Text.RegularExpressions.Regex Ask = new(@"(?i)\b(screen ?time|how (long|much)( time)? (was|have|did|am) i\b.*\b(on|in|using|use|spend|spent|watch|play|game)|time (on|in) \w+ (today|yesterday|this week)|typing speed|how fast (do|am) i typ|wpm|my activity)\b");
    public static bool Asked(string said) => Ask.IsMatch(said);
    public static string Answer(string said)
    {
        var low = said.ToLowerInvariant();
        if ((low.Contains("typing") || low.Contains("wpm")) && low.Contains("average"))
            return WpmOn(DayOf(DateTime.Today)) is { } avg ? $"About {avg} words a minute on average today{(Wpm() is { } n ? $" (right now: {n})" : "")} ⌨️" : "I haven't seen you type enough today to tell yet ⌨️";
        if (low.Contains("typing") || low.Contains("wpm") || low.Contains("how fast"))
            return Wpm() is { } w ? $"You're typing at about {w} words a minute right now (the last 10 minutes){(WpmOn(DayOf(DateTime.Today)) is { } t ? $", {t} on average today" : "")} ⌨️"
                                  : WpmOn(DayOf(DateTime.Today)) is { } today ? $"About {today} words a minute on average today ⌨️" : "I haven't seen you type enough today to tell yet ⌨️";
        var week = low.Contains("week");
        var when = week ? "this week" : low.Contains("yesterday") ? "yesterday" : "today";
        var dates = week ? Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(-i)) : [DateTime.Today.AddDays(when == "yesterday" ? -1 : 0)];
        var apps = new Dictionary<string, int>(); var kinds = new Dictionary<string, string>();
        foreach (var d in dates.Select(DayOf)) { foreach (var (a, s) in d.Apps) apps[a] = apps.GetValueOrDefault(a) + s; foreach (var (a, k) in d.Kinds) kinds[a] = KindOf(a, k); }
        // one app or site by name ("youtube"), or one kind ("gaming", "meetings", "videos")
        if (apps.Keys.FirstOrDefault(a => low.Contains(a.ToLowerInvariant().Split(' ')[0]) && a.Length >= 2) is { } app)
            return $"{app}: {Span(apps[app])} {when}.";
        var kind = low switch { var x when x.Contains("game") || x.Contains("gaming") || x.Contains("play") => "Games", var x when x.Contains("meeting") || x.Contains("call") => "Meetings",
                                var x when x.Contains("video") || x.Contains("watch") => "Videos", var x when x.Contains("social") => "Social", var x when x.Contains("work") || x.Contains("productive") => "Productive", _ => null };
        if (kind != null) return $"{kind}: {Span(apps.Where(a => kinds.GetValueOrDefault(a.Key) == kind).Sum(a => a.Value))} {when}.";
        var total = apps.Values.Sum();
        if (total == 0) return $"No screen time recorded {when} yet.";
        var top = string.Join(", ", apps.OrderByDescending(a => a.Value).Take(3).Select(a => $"{a.Key} {Span(a.Value)}"));
        return $"Screen time {when}: {Span(total)}. Most of it: {top}.";
    }

    // ---------- a nudge: an hour straight of videos, games or social ----------
    public static bool Nudges = true;
    static int streak; static string streakOn = "";
    // called each tick: the app to nudge about once an hour of fun goes unbroken (null: nothing to say)
    public static string? Nudge()
    {
        if (!Nudges || Current is not { } now || now.Kind is not ("Videos" or "Games" or "Social")) { streak = 0; return null; }
        streak += 5; streakOn = now.Label;
        if (streak != 3600) return null;   // once, at the hour (it starts over after a break)
        return streakOn;
    }

    public static string Span(int seconds) { var t = TimeSpan.FromSeconds(seconds); return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m" : $"{t.Seconds}s"; }

    delegate nint HookProc(int code, nint w, nint l);
    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int id, HookProc fn, nint mod, uint thread);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("kernel32.dll")] static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, nint l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] static extern nint GetWindow(nint h, uint cmd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint m, ref MONITORINFO i);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    struct RECT { public int L, T, R, B; }
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
}
