using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ivo;

// Reminders, daily tasks (a prompt answered in the background) and dev tasks (the coding agent in a folder), once or repeating.
// Checked every 20 seconds while Ivo runs, in the tray too; anything missed while it was closed fires on the next start.
public partial class MainWindow
{
    DispatcherTimer? scheduleTimer;
    static readonly Brush ReminderColor = Shell.B("#F2B84B");

    void StartSchedule()
    {
        Planner.S = S; Planner.BriefNow = () => Dispatcher.BeginInvoke(BriefingNow);
        try { Gateway.Apply(S); } catch { S.GatewayOn = false; }   // the port is taken: stays off
        Planner.Changed = () =>
        {
            SaveSettings();
            if (ScheduleLayer.IsVisible) { if (PlannerKind == "todo") DrawTodos(); else DrawSchedule(); }
        };
        scheduleTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => RunDue(), Dispatcher);
        scheduleTimer.Start();
        Dispatcher.BeginInvoke(RunDue, DispatcherPriority.ApplicationIdle);
    }

    // The morning briefing takes a while (searching the web, the model), so it starts this much early, then holds
    // the result and delivers it on the dot. Daily tasks get ready as early as they can (PrepareNext); reminders and dev
    // tasks start at their time.
    static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);

    // Due before noon on the coming briefing's day and opted in: it arrives in the briefing, not as its own card.
    bool InBriefing(Scheduled x) => S.Briefing && x.On && !Triggered(x) && x.Kind is "task" or "reminder" && (x.Brief ?? x.Kind == "task")
                                    && x.When.Date == S.BriefingNext.Date && x.When.TimeOfDay < TimeSpan.FromHours(12);

    void RunDue()
    {
        if (Store.Sealed) return;   // locked since launch: keys and the Inbox open once you unlock, then late items run
        RunTriggers(); RunNags(); FlushDigest(); AfterQuiet(); SyncTodosIfDue(); RunAppReminders(); ReviewMemories();
        var due = S.Schedules.Where(x => x.On && x.Kind is not ("task" or "plan") && x.When <= DateTime.Now && !InBriefing(x)).ToList();
        foreach (var x in due)
        {
            if (x.Kind == "dev") RunDev(x); else Remind(x);
            x.When = Next(x.When, x.Repeat) ?? x.When;
            if (x.Repeat == "once") x.On = false;
        }
        // tasks outside the briefing arrive one by one, at their own time
        foreach (var x in S.Schedules.Where(x => x.On && x.Kind == "task" && !Triggered(x) && x.Ready?.For == x.When && x.When <= DateTime.Now && !InBriefing(x)).ToList()) Deliver(x);
        PrepareNext();
        if (S.Briefing && DateTime.Now >= S.BriefingNext - Lead)
        {
            var covered = S.Schedules.Where(InBriefing).ToList();
            if (S.BriefingNext != default) RunBriefing(S.BriefingNext, covered);   // (the first time it's turned on, it only schedules)
            foreach (var x in covered)   // the briefing covered them: on to their next time
            {
                x.Ready = null;
                x.When = Next(x.When, x.Repeat) ?? x.When;
                if (x.Repeat == "once") x.On = false;
            }
            S.BriefingNext = NextBriefing();
            SaveSettings();
        }
        if (due.Count > 0) SaveSettings();
        if (ScheduleLayer.IsVisible && PlannerKind != "todo") DrawSchedule();   // also keeps the countdowns fresh
    }

    DateTime NextBriefing()
    {
        var t = TimeSpan.TryParse(S.BriefingTime, out var at) ? at : new TimeSpan(8, 0, 0);
        var next = DateTime.Today + t;
        return next - Lead > DateTime.Now ? next : next.AddDays(1);
    }

    // held until the time it was set for (now, if that's passed)
    static Task Until(DateTime at) => at > DateTime.Now ? Task.Delay(at - DateTime.Now) : Task.CompletedTask;

    // A desktop notification (Ivo's own card, bottom right); clicking it opens the Inbox. Switch: the Inbox's bell, or Appearance.
    // kind: reminder, daily, failed, briefing, dev; each can be switched off in Settings → Notifications
    void Notify(string kind, string title, string body, Action open, string icon = "\uE715", string color = "#A78BFA")
    {
        if (!S.DesktopNotify || S.NotifyOff.Contains(kind)) return;
        if (Quiet.Now) { Quiet.Hold(title); return; }   // quiet hours: told in one card after
        if (S.DigestHours > 0 && kind is "daily" or "briefing" or "failed") { lock (digest) digest.Add(title); return; }   // gathered for the digest
        if (locked) (title, body) = ("Ivo", "Something new. Unlock Ivo to see it.");   // nothing private on screen while locked
        NotifyCard.Show(title, body.Length > 200 ? body[..200] + "…" : body, icon, color, open);
    }

    // digest mode: task results wait in the Inbox and one notification every few hours says what came in
    readonly List<string> digest = [];
    DateTime nextDigest = DateTime.Now.AddHours(1);
    void FlushDigest()
    {
        if (S.DigestHours <= 0 || DateTime.Now < nextDigest) return;
        nextDigest = DateTime.Now.AddHours(S.DigestHours);
        List<string> items; lock (digest) { items = [.. digest]; digest.Clear(); }
        if (items.Count == 0) return;
        NotifyCard.Show($"{items.Count} update{(items.Count == 1 ? "" : "s")} in your Inbox", string.Join(" · ", items.GroupBy(t => t).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)), "\uE715", "#A78BFA", () => OpenInbox());
    }
    void Digest_Changed(object s, SelectionChangedEventArgs e)
    {
        if (loading || DigestPick.SelectedItem is not ComboBoxItem { Tag: string h }) return;
        S.DigestHours = int.Parse(h); nextDigest = DateTime.Now.AddHours(Math.Max(1, S.DigestHours)); SaveSettings();
    }

    void ShowNotifyKinds()
    {
        loading = true;
        foreach (var c in NotifyKinds.Children.OfType<CheckBox>()) c.IsChecked = !S.NotifyOff.Contains((string)c.Tag);
        NotifyKinds.IsEnabled = S.DesktopNotify;
        AppSoundsBox.IsChecked = S.AppSounds;
        DigestPick.SelectedItem = DigestPick.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.DigestHours.ToString()) ?? DigestPick.Items[0];
        loading = false;
    }

    void NotifyKind_Changed(object s, RoutedEventArgs e)
    {
        if (loading || s is not CheckBox { Tag: string kind } c) return;
        S.NotifyOff.Remove(kind);
        if (c.IsChecked != true) S.NotifyOff.Add(kind);
        SaveSettings();
    }

    // A reminder: waits under "Now" in the Inbox (Snooze, Done) and a Windows notification; nothing is sent to any model.
    void Remind(Scheduled x)
    {
        if (x.Sound && !Quiet.Now) Sounds.Chime();
        var item = new InboxItem { Kind = "reminder", TaskId = x.Id, Title = x.Text };
        Inbox.Add(item);
        Notify("reminder", "Reminder", x.Text, () => OpenInbox(), "\uEA8F", "#F2B84B");
        if (x.Nag) { S.Nags.Add(new(item.Id, x.Text, DateTime.Now.AddMinutes(10))); SaveSettings(); }
    }

    // ---------- triggers: daily tasks that run when files change, not at a time ----------
    static bool Triggered(Scheduled x) => x.Repeat == "files";
    readonly Dictionary<string, System.IO.FileSystemWatcher> watchers = [];
    readonly Dictionary<string, HashSet<string>> changedFiles = [];
    readonly Dictionary<string, DateTime> quietUntil = [];

    void RunTriggers()
    {
        var mine = S.Schedules.Where(x => x.On && x.Kind == "task" && Triggered(x)).ToList();
        // folders: a watcher each; a change waits for 30 quiet seconds, then the task runs with the list of changed files
        foreach (var id in watchers.Keys.Where(id => !mine.Any(x => x.Id == id && x.Repeat == "files")).ToList()) { watchers[id].Dispose(); watchers.Remove(id); }
        foreach (var x in mine.Where(x => x.Repeat == "files" && System.IO.Directory.Exists(x.Folder) && !watchers.ContainsKey(x.Id)))
        {
            var w = new System.IO.FileSystemWatcher(x.Folder!) { IncludeSubdirectories = true, EnableRaisingEvents = true,
                                                                NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.DirectoryName };
            var id = x.Id;
            void Seen(string path) { if (System.Text.RegularExpressions.Regex.IsMatch(path, @"[\\/](\.git|bin|obj|node_modules)[\\/]")) return;
                                     lock (changedFiles) { (changedFiles.TryGetValue(id, out var set) ? set : changedFiles[id] = []).Add(path); quietUntil[id] = DateTime.Now.AddSeconds(30); } }
            w.Changed += (_, e) => Seen(e.FullPath); w.Created += (_, e) => Seen(e.FullPath); w.Deleted += (_, e) => Seen(e.FullPath); w.Renamed += (_, e) => Seen(e.FullPath);
            watchers[x.Id] = w;
        }
        foreach (var x in mine.Where(x => x.Repeat == "files" && !running.Contains(x.Id)))
        {
            List<string> files;
            lock (changedFiles)
            {
                if (!changedFiles.TryGetValue(x.Id, out var set) || set.Count == 0 || quietUntil.GetValueOrDefault(x.Id) > DateTime.Now) continue;
                files = [.. set.Take(50)]; set.Clear();
            }
            FireTrigger(x, "Files that changed in " + x.Folder + ":\n" + string.Join("\n", files.Select(f => "- " + System.IO.Path.GetRelativePath(x.Folder!, f))));
        }
    }

    async void FireTrigger(Scheduled x, string? extra)
    {
        running.Add(x.Id);
        try
        {
            var a = await Answer(x, extra);
            SaveSettings();
            if (a is not { } r) return;   // nothing new to say
            Inbox.Add(new InboxItem { Kind = "daily", TaskId = x.Id, Category = x.Category, Title = x.Text, Text = r.Text, Model = r.Model, Provider = r.Provider, Failed = r.Failed });
            Notify(r.Failed ? "failed" : "daily", r.Failed ? "Triggered task failed" : x.Category != "" ? x.Category : "Triggered task", Voice.Plain(r.Text), () => OpenInbox());
        }
        catch (Exception ex) { Store.Fail($"Trigger · {x.Text.Split('\n')[0]}", ex.Message); }
        finally { running.Remove(x.Id); }
    }

    // reminders set to come back: again every 10 minutes until their Inbox card is marked done (or removed)
    void RunNags()
    {
        if (DateTime.Now.DayOfWeek is DayOfWeek.Saturday && DateTime.Now.Hour >= 10 && S.LaterNudged.Date != DateTime.Today
            && Inbox.All.Count(i => i.Kind == "later" && !i.Done) is > 0 and var waiting)
        {
            S.LaterNudged = DateTime.Today; SaveSettings();
            NotifyCard.Show("Your read-later queue", $"{waiting} link{(waiting == 1 ? "" : "s")} saved for the weekend", "\uE7BC", "#3FB950", () => { OpenInbox(); wheelFilter = "Read later"; FillWheel(); });
        }
        if (S.Nags.Count == 0) return;
        var changed = false;
        foreach (var n in S.Nags.ToList())
        {
            if (Inbox.All.FirstOrDefault(i => i.Id == n.InboxId) is not { Done: false }) { S.Nags.Remove(n); changed = true; continue; }
            if (n.Next > DateTime.Now) continue;
            Sounds.Chime();
            Notify("reminder", "Still to do", n.Text, () => OpenInbox(), "\uEA8F", "#F2B84B");
            S.Nags[S.Nags.IndexOf(n)] = n with { Next = DateTime.Now.AddMinutes(10) }; changed = true;
        }
        if (changed) SaveSettings();
    }

    // ready-made daily tasks (Templates in the Daily tasks window): fill the form, then adjust and add
    record TaskTemplate(string Name, string Category, string Prompt, string Repeat = "daily");
    static readonly TaskTemplate[] Templates =
    [
        new("GitHub release watch", "Updates", "List new releases of these GitHub projects since yesterday: … For each: version, date, and the 3 most important changes, with a link to the release notes."),
        new("Weather and commute", "Updates", "Today's weather for … (city): temperature range, rain chance, and what to wear. Then anything affecting the commute there today (strikes, closures, heavy traffic) with links."),
        new("Learn something", "Quotes", "Teach me one useful idea in 5 minutes on … : the idea, a concrete example, and one way to use it today."),
        new("Weekly review prompt", "Updates", "It's my weekly review. Ask me 5 short questions about my week (wins, blockers, what to drop, next week's top 3), then suggest one improvement.", "weekly"),
        new("When my notes change", "Updates", "Summarise what changed in these files and list any new to-dos or decisions in them.", "files"),
    ];
    void SchedTemplates_Click(object s, RoutedEventArgs e)
    {
        var el = (FrameworkElement)s;
        ShowMenu(el.PointToScreen(new Point(0, el.ActualHeight)) is var p ? PointFromScreen(p) : default,
            [.. Templates.Select(t => ((string Icon, string Text, Action Run, bool Danger)?)(Category(t.Category).Icon, t.Name, () => UseTemplate(t), false))]);
    }
    void UseTemplate(TaskTemplate t)
    {
        if (PlannerKind != "task") OpenPlanner("task");
        SchedText.Text = t.Prompt;
        schedCat = t.Category; BuildCategories();
        SchedRepeat.SelectedItem = SchedRepeat.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == t.Repeat) ?? SchedRepeat.SelectedItem;
        SchedNote.Text = "Fill in the … and adjust, then add it.";
        SchedText.Focus();
    }
    void SchedRepeat_Changed(object s, SelectionChangedEventArgs e)
    {
        if (DevFolderRow == null || SchedNote == null || TaskOpts == null || SchedRepeat.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var files = tag == "files";
        DevFolderRow.Visibility = files || PlannerKind == "dev" ? Visibility.Visible : Visibility.Collapsed;
        if (files && devFolder == null) SetDevFolder(S.KnowledgeFolder != "" ? S.KnowledgeFolder : null);
        SchedNote.Text = tag switch
        {
            "files" => "Runs when files in the folder change (after 30 quiet seconds), with the list of changed files; the time is ignored.",
            _ => SchedNote.Text.StartsWith("Runs when") ? "" : SchedNote.Text,
        };
    }

    // Once a week, what Ivo remembers about you lands in the Inbox to look over (Settings → Profile & memory to change it).
    void ReviewMemories()
    {
        if (!S.MemoryReview || S.Memories.Count == 0 || DateTime.Now < S.MemoryReviewNext) return;
        var first = S.MemoryReviewNext == default;
        S.MemoryReviewNext = DateTime.Today.AddDays(7).AddHours(10);
        SaveSettings();
        if (first) return;   // the first week starts counting now
        Inbox.Add(new InboxItem
        {
            Kind = "daily", Category = "Memory", Title = "What Ivo remembers about you",
            Text = $"Models in your chats saved these {S.Memories.Count} facts. Anything wrong or out of date? Remove it in Settings → Profile & memory.\n\n"
                   + string.Join("\n", S.Memories.Select(m => "- " + m)),
        });
        Notify("daily", "Your weekly memory review", $"{S.Memories.Count} things Ivo remembers about you", () => OpenInbox(), "", "#A78BFA");
    }

    // Reminders for when an app opens ("…when I open Teams"): they wait far in the future and become due the moment that
    // app starts (it wasn't running at the last look, it is now).
    readonly HashSet<string> appsSeen = new(StringComparer.OrdinalIgnoreCase), appsChecked = new(StringComparer.OrdinalIgnoreCase);
    void RunAppReminders()
    {
        foreach (var x in S.Schedules.Where(x => x.On && x.App != "").ToList())
        {
            var running = System.Diagnostics.Process.GetProcessesByName(x.App).Length > 0;
            // the first look only notes what's already open: it fires the next time the app opens, not now
            if (running && appsChecked.Contains(x.App) && !appsSeen.Contains(x.App) && x.When > DateTime.Now.AddYears(1)) x.When = DateTime.Now;
            appsChecked.Add(x.App);
            if (running) appsSeen.Add(x.App); else appsSeen.Remove(x.App);
        }
    }
    public static DateTime WhenAppOpens => new(2999, 1, 1);

    // "after lunch", "this evening", "tomorrow morning": the next such time (When.Parse)
    void SnoozeUntil(string text, string when)
    {
        S.Schedules.Add(new Scheduled { Kind = "reminder", Text = text, When = When.Parse(when)!.Value.At });
        SaveSettings();
        if (ScheduleLayer.IsVisible) DrawSchedule();
    }

    void Snooze(string text, int minutes)
    {
        S.Schedules.Add(new Scheduled { Kind = "reminder", Text = text, When = DateTime.Now.AddMinutes(minutes) });
        SaveSettings();
        if (ScheduleLayer.IsVisible) DrawSchedule();
    }

    // A daily task's answer, from the daily tasks model (which can search the web and read pages). An alert reports only
    // what changed since its last run, and says "Nothing new." otherwise.
    async Task<(string Text, bool Failed, string Model, string Provider)?> Answer(Scheduled x, string? extra = null)
    {
        var c = NewChat(); c.Space = "chat"; c.Workspace = null; c.Mode = "autopilot"; c.Incognito = true;   // a working chat only, never saved
        (c.Provider, c.Model) = TaskModel(S.DailyModel);   // Settings → Tasks
        var alert = x.Category.Equals("Alerts", StringComparison.OrdinalIgnoreCase);
        var prompt = Agent.Dates(x.Text) + (extra != null ? "\n\n" + extra : "");
        c.System = $"Today is {DateTime.Now:dddd d MMMM yyyy}. Your answer is read in a feed of daily updates, rendered as Markdown: short sections, clear headings, no preamble. " +
                   "You can search the web (web_search) and read pages (fetch_url) for anything the task needs: current facts, prices, releases, weather. Link what you cite: [Title](https://…).";
        if (News.IsRequest(x.Text))
        {
            var sources = News.SourcesFrom(x.Text);
            var topic = News.QueryFrom(x.Text, sources);
            var articles = await News.Fetch(topic, sources, max: 10);
            if (articles.Count > 0)
            {
                articles = await News.Enrich(articles);
                prompt += "\n\nCurrent news articles found for this task. Summarize only the most relevant items, preserve their links and distinguish reported facts from uncertain details:\n" + News.ForPrompt(articles);
            }
        }
        if (alert && x.LastResult != "")
            prompt += $"\n\nWhat you reported last time:\n{x.LastResult}\n\nReport only what's new or changed. If nothing meaningful changed, reply exactly \"Nothing new.\"";
        c.Messages.Add(new("user", prompt, At: DateTime.Now));
        var first = "";
        if (c.Provider != null && c.Model != null && Router.IsResting(c.Provider, c.Model)) Fallback(c);   // failed a moment ago: straight to one that works
        for (var attempt = 0; ; attempt++)
        {
        if (attempt > 0) c.Messages.RemoveRange(1, c.Messages.Count - 1);   // the retry starts from the prompt
        var cfg = c.Provider == Router.Name && S.AutoOn ? Router.Cfg : S.Providers.FirstOrDefault(p => p.Name == c.Provider);
        try
        {
            if (cfg == null) throw new InvalidOperationException("The daily tasks model isn't set up any more. Pick one in Settings → Tasks.");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var r = cfg.Kind == "cli" ? await Cli.Run(cfg, c, _ => { }, _ => { }, cts.Token) : await WithWeb(cfg, c, cts.Token);
            var text = r.Text.Trim();
            if (Router.OutOfCredits(text)) throw new Exception(text.Split('\n')[0]);   // a "no credits" reply: try another model
            if (text.StartsWith("Nothing new", StringComparison.OrdinalIgnoreCase)) return null;
            if (alert) x.LastResult = text.Length > 4000 ? text[..4000] : text;
            return (text, false, c.Model ?? "", c.Provider ?? "");
        }
        catch (Exception ex) when (attempt == 0 && Rested(c, ex) && Fallback(c))
        {
            first = ex.Message.Split('\n')[0];
            Store.Fail($"Daily task · {x.Text.Split('\n')[0]} · trying {c.Provider} · {c.Model}", ex.Message);
        }
        catch (Exception ex)
        {
            Store.Fail($"Daily task · {x.Text.Split('\n')[0]}", ex.Message);
            return ("The task failed: " + ex.Message + (first != "" ? $" (the first model also failed: {first})" : ""), true, c.Model ?? "", c.Provider ?? "");
        }
        }
    }

    // the model that just failed sits out a while (always true, so it can lead a catch filter)
    static bool Rested(Chat c, Exception ex) { if (c.Provider != null && c.Model != null) Router.Rest(c.Provider, c.Model, ex.Message); return true; }

    // Another model to retry a failed daily task on: Auto's best chat model (it fails over between models itself), else the main provider's default.
    bool Fallback(Chat c)
    {
        if (S.AutoOn && !(c.Provider == Router.Name && c.Model == "auto/best-chat")) { (c.Provider, c.Model) = (Router.Name, "auto/best-chat"); return true; }
        if (S.Provider is { } p && p != c.Provider && S.Providers.Any(x => x.Name == p)) { (c.Provider, c.Model) = (p, DefaultModel(p, ModelCache.GetValueOrDefault(p))); return true; }
        return false;
    }

    // The task's model with the web tools (search, read a page), up to a few steps; a model that can't take tools answers without them.
    static async Task<Api.Reply> WithWeb(ProviderCfg cfg, Chat c, CancellationToken ct)
    {
        var key = Store.Unprotect(cfg.Key);
        var tools = Agent.Tools.Where(t => t.Name is "web_search" or "fetch_url").ToList();
        var trail = new List<string>();   // what it looked up, shown under the answer
        for (var step = 0; ; step++)
        {
            Api.Reply r;
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct); stepCts.CancelAfter(TimeSpan.FromSeconds(90));   // one slow step can't eat the whole task
            try { r = await Api.Stream(cfg, key, c, step < 8 && tools != null ? tools : null, _ => { }, null, stepCts.Token); }
            catch (Exception ex) when (tools != null && System.Text.RegularExpressions.Regex.IsMatch(ex.Message, @"tool|function", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { tools = null; continue; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"The model didn't answer within 90 seconds (step {step + 1})."); }
            if (r.Calls.Count == 0) return trail.Count == 0 ? r : r with { Text = r.Text + "\n\n---\n_Looked up: " + string.Join(" · ", trail) + "_" };
            c.Messages.Add(new("assistant", r.Text, Calls: r.Calls, Blocks: r.Blocks));
            foreach (var call in r.Calls)
            {
                trail.Add(Agent.Describe(call).Label);
                c.Messages.Add(new("tool", await Agent.Run(c, call.Name, call.Args, ct), CallId: call.Id, Name: call.Name));
            }
        }
    }

    // Daily tasks start as late as they can and still be ready on the dot, so what they report is as fresh as possible: each run is
    // timed, and the next starts that long before its time (×1.5, plus 30 s to spare; 1-15 minutes; 5 the first time).
    // The answer is saved with the task, so closing Ivo or restarting the PC doesn't lose it. One that's late runs at once.
    readonly HashSet<string> running = [];

    static TimeSpan HeadStart(Scheduled x) => x.Took is { } s
        ? TimeSpan.FromSeconds(Math.Clamp(s * 1.5 + 30, 60, 15 * 60))
        : TimeSpan.FromMinutes(5);

    void PrepareNext()
    {
        if (Store.Sealed) return;
        foreach (var x in S.Schedules.Where(x => x.On && x.Kind == "task" && !Triggered(x) && x.Ready?.For != x.When && x.When - HeadStart(x) <= DateTime.Now && !running.Contains(x.Id) && !InBriefing(x)).ToList())
            Prepare(x);
    }

    async void Prepare(Scheduled x)
    {
        var (at, text) = (x.When, x.Text);
        running.Add(x.Id);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (string Text, bool Failed, string Model, string Provider)? answer;
        try { answer = await Answer(x); }
        catch (Exception ex) { answer = ("The task failed: " + ex.Message, true, "", ""); }   // e.g. the model unreachable
        finally { running.Remove(x.Id); }
        if (answer is not { Failed: true }) x.Took = clock.Elapsed.TotalSeconds;   // how long a good run takes, for next time's start
        if (x.When != at || x.Text != text) return;   // edited meanwhile: it gets ready again
        x.Ready = answer is { } a ? new(at, a.Text, a.Failed, a.Model, a.Provider) : new(at, null, false, "", "");   // null: nothing to say
        SaveSettings();   // the answer, and Seen / LastResult
        await Until(at);
        RunDue();   // shows it on the dot
    }

    void Deliver(Scheduled x)
    {
        if (x.Ready is { Text: { } t } r)
        {
            Inbox.Add(new InboxItem { Kind = "daily", TaskId = x.Id, Category = x.Category, Title = x.Text, Text = t, Model = r.Model, Provider = r.Provider, Failed = r.Failed });
            Notify(r.Failed ? "failed" : "daily", r.Failed ? "Daily task failed" : x.Category != "" ? x.Category : "Daily task", Voice.Plain(t), () => OpenInbox(), r.Failed ? "\uE783" : Category(x.Category).Icon, r.Failed ? "#F85149" : Category(x.Category).Color);
        }
        x.Ready = null;
        x.When = Next(x.When, x.Repeat) ?? x.When;
        if (x.Repeat == "once") x.On = false;
        SaveSettings();
    }

    // the morning briefing: every active daily task, together, as one sectioned digest
    // "Send now": today's briefing from every active daily task, without moving their schedules
    void BriefingNow()
    {
        var covered = S.Schedules.Where(x => x.On && (x.Kind == "task" && (x.Brief ?? true) || x.Kind == "reminder" && x.Brief == true && x.When.Date == DateTime.Today)).ToList();
        if (covered.Count == 0) { _ = Notice("Nothing to brief", "Add a daily task first; the briefing gathers their answers."); return; }
        RunBriefing(DateTime.Now, covered);
    }
    void BriefNow_Click(object s, RoutedEventArgs e) => BriefingNow();

    async void RunBriefing(DateTime at, List<Scheduled> covered)
    {
        var tasks = covered.Where(x => x.Kind == "task").ToList();
        var reminders = covered.Where(x => x.Kind == "reminder").OrderBy(x => x.When).Select(x => $"- **{When.Time(x.When)}** · {x.Text}").ToList();
        if (tasks.Count == 0 && reminders.Count == 0) return;
        var answers = await Task.WhenAll(tasks.Select(async x => (x, a: await Answer(x))));
        SaveSettings();
        var parts = answers.Where(t => t.a != null).ToList();
        if (parts.Count == 0 && reminders.Count == 0) return;
        var order = Categories.Select(k => k.Name).ToList();
        var md = new System.Text.StringBuilder();
        if (S.CalendarUrls.Count > 0)   // the day's meetings head the briefing
            md.Append($"## Today's calendar\n\n{Calendar.Format(await Calendar.Between(S.CalendarUrls, at.Date, at.Date.AddDays(1)))}\n\n");
        if (await Weather.Current() is { } weather) md.Append($"## Weather\n\n{Weather.Briefing(weather)}\n\n");
        if (reminders.Count > 0) md.Append("## This morning\n\n" + string.Join("\n", reminders) + "\n\n");
        foreach (var (x, a) in parts.OrderBy(t => order.IndexOf(t.x.Category) is var i && i >= 0 ? i : 99))
            md.Append($"## {(x.Category != "" ? x.Category : "Task")}\n\n{a!.Value.Text}\n\n");
        var models = parts.Select(t => t.a!.Value.Model).Distinct();
        await Until(at);
        Inbox.Add(new InboxItem { Kind = "briefing", Title = $"Morning briefing · {DateTime.Now:dddd d MMMM}", Text = md.ToString().Trim(),
                                  Model = string.Join(", ", models), Failed = parts.Count > 0 && parts.All(t => t.a!.Value.Failed) });
        var what = parts.Select(t => t.x.Category).Concat(reminders.Count > 0 ? ["Reminders"] : []).Distinct();
        var n = parts.Count + reminders.Count;
        Notify("briefing", "Your morning briefing is ready", $"{n} update{(n == 1 ? "" : "s")}: " + string.Join(", ", what), () => OpenInbox(), "\uE706", "#FB923C");
    }

    // A dev task: a chat in its folder with the coding agent on Autopilot (low-risk actions run without asking), opened
    // so you can watch and step in; the Inbox keeps a link to it.
    void RunDev(Scheduled x)
    {
        var c = NewChat(); c.Workspace = x.Folder != null && System.IO.Directory.Exists(x.Folder) ? x.Folder : null;
        c.Space = c.Workspace != null ? "code" : "chat"; c.Mode = "autopilot"; c.Permission = "assisted";
        if (x.Provider != null && S.Providers.Any(p => p.Name == x.Provider)) { c.Provider = x.Provider; c.Model = x.Model ?? DefaultModel(x.Provider, null); }
        else (c.Provider, c.Model) = TaskModel(S.DevModel);
        c.Title = "Dev · " + (x.Text.Length > 40 ? x.Text[..40] + "…" : x.Text);
        if (CodeMode != (c.Space == "code")) { S.View = c.Space; foreach (RadioButton r in ViewChoices.Children) r.IsChecked = (string)r.Tag == S.View; ApplyView(); }
        active.ShowChat(c);
        // "@reviewer check the open pull requests": that agent does it (the chat hands it over and reports back)
        var at = System.Text.RegularExpressions.Regex.Match(x.Text, @"^@([\w\-]+)\s+(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (at.Success && Subagents.All(c.Workspace).Append(Subagents.General).Any(a => a.Name.Equals(at.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
            active.Send($"Hand this task to the \"{at.Groups[1].Value}\" agent with delegate_to_agent, giving it everything it needs, then report what it did:\n\n{at.Groups[2].Value}");
        else active.Send(x.Text);
        RefreshChats();
        Inbox.Add(new InboxItem { Kind = "dev", TaskId = x.Id, Title = x.Text, ChatId = c.Id, Model = c.Model ?? "", Provider = c.Provider ?? "",
                                  Text = $"Started in **{(c.Workspace != null ? System.IO.Path.GetFileName(c.Workspace) : "a plain chat")}** on autopilot." });
        Notify("dev", "Dev task started", x.Text, () => OpenInbox(), "\uE943", "#58A6FF");
    }

    // the next time after now, or null for one-offs
    internal static DateTime? Next(DateTime t, string repeat)
    {
        if (repeat == "once") return null;
        do
        {
            t = repeat == "weekly" ? t.AddDays(7) : t.AddDays(1);
            while (repeat == "weekdays" && t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) t = t.AddDays(1);
        } while (t <= DateTime.Now);
        return t;
    }

    void OpenSchedule_Click(object s, RoutedEventArgs e)
    {
        SetWhen(DateTime.Now.AddHours(1).Date.AddHours(DateTime.Now.AddHours(1).Hour));
        SchedNote.Text = "";
        BuildQuickTimes();
        DrawSchedule();
        ScheduleLayer.Visibility = Visibility.Visible;
        SchedText.Focus();
    }

    void SetWhen(DateTime t) { SchedDate.SelectedDate = t.Date; SchedTime.Value = t.TimeOfDay; }

    // one click sets the date and time
    void BuildQuickTimes()
    {
        SchedQuick.Children.Clear();
        var now = DateTime.Now;
        var tonight = now.Date.AddHours(20);
        (string, DateTime)[] picks =
        [
            ("In 10 min", now.AddMinutes(10)), ("In 1 hour", now.AddHours(1)),
            (tonight > now ? $"Tonight {When.Time(tonight)}" : $"Tomorrow {When.Time(tonight)}", tonight > now ? tonight : tonight.AddDays(1)),
            ($"Tomorrow {When.Time(now.Date.AddHours(9))}", now.Date.AddDays(1).AddHours(9)),
            ($"Monday {When.Time(now.Date.AddHours(9))}", now.Date.AddDays(((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7 is 0 ? 7 : ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7).AddHours(9)),
        ];
        foreach (var (label, t) in picks)
        {
            var chip = new ToggleButton { Content = label, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6), Tag = t };
            chip.Click += (_, _) =>
            {
                foreach (ToggleButton c in SchedQuick.Children) c.IsChecked = c == chip;
                SetWhen((DateTime)chip.Tag);
            };
            SchedQuick.Children.Add(chip);
        }
    }

    // ---------- daily task categories: each has a look and a starting prompt; "+" makes your own ----------
    record TaskCategory(string Name, string Icon, string Color, string Prompt);
    static readonly TaskCategory[] Categories =
    [
        new("Quotes", "", "#F2B84B", "Give me 3 short, inspiring quotes for today with their authors, and one line on how I could apply one of them."),
        new("Alerts", "", "#F85149", "Check for important developments on: … If nothing significant changed since yesterday, reply exactly \"Nothing new.\" Otherwise give me a short alert."),
        new("Updates", "", "#3FB950", "Give me an update and follow-up on: … What's new since yesterday, and what should I do next?"),
    ];
    static TaskCategory Category(string name) => Categories.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? new(name == "" ? "Task" : name, "", "#9C7CFF", "");
    string schedCat = "";

    void BuildCategories()
    {
        SchedCats.Children.Clear();
        var own = S.Schedules.Select(x => x.Category).Where(c => c != "" && !Categories.Any(k => k.Name.Equals(c, StringComparison.OrdinalIgnoreCase))).Distinct();
        foreach (var cat in Categories.Concat(own.Select(Category)))
        {
            var label = new TextBlock();
            label.Inlines.Add(new System.Windows.Documents.Run(cat.Icon) { FontFamily = App.IconFont, Foreground = Shell.B(cat.Color) });
            label.Inlines.Add(new System.Windows.Documents.Run("  " + cat.Name));
            var chip = new ToggleButton { Content = label, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6), IsChecked = schedCat == cat.Name };
            chip.Click += (_, _) =>
            {
                schedCat = cat.Name; SchedCatNew.Visibility = Visibility.Collapsed;
                // swap in this category's starting prompt unless you've written your own
                if (cat.Prompt != "" && (SchedText.Text == "" || Categories.Any(k => k.Prompt == SchedText.Text))) { SchedText.Text = cat.Prompt; SchedText.CaretIndex = SchedText.Text.Length; }
                BuildCategories(); SchedText.Focus();
            };
            SchedCats.Children.Add(chip);
        }
        var add = new ToggleButton { Content = "+  New", Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6), ToolTip = "Your own category" };
        add.Click += (_, _) => { add.IsChecked = false; SchedCatNew.Visibility = Visibility.Visible; SchedCatNew.Focus(); };
        SchedCats.Children.Add(add);
    }

    void RefreshPlannerEditor()
    {
        if (SchedPlaceholder == null) return;
        var task = PlannerKind == "task";
        SchedPlaceholder.Text = task ? "Prompt for the AI, e.g. Give me 3 motivating quotes for the day…" : "Remind me to…";
        ReminderOpts.Visibility = task ? Visibility.Collapsed : Visibility.Visible;
        TaskOpts.Visibility = task ? Visibility.Visible : Visibility.Collapsed;
        if (editingSched == null) SchedBrief.IsChecked = task;   // new tasks join the briefing, new reminders pop up on their own
        BriefRow.Visibility = PlannerKind == "dev" ? Visibility.Collapsed : Visibility.Visible;
        if (task) BuildCategories();
        if (task && SchedModel.Items.Count == 0)
        {
            SchedModel.ItemsSource = DevChoices();   // pinned in Settings → Tasks, plus the dev default
            var (dp, dm) = TaskModel(S.DevModel);
            SchedModel.SelectedItem = SchedModel.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is (string p, string m) && p == dp && m == dm) ?? SchedModel.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
    }

    void CloseSchedule_Click(object s, RoutedEventArgs e) => ScheduleLayer.Visibility = Visibility.Collapsed;

    void SchedAdd_Click(object s, RoutedEventArgs e)
    {
        var text = SchedText.Text.Trim();
        if (text == "") { SchedNote.Text = "Write the reminder or prompt."; return; }
        var time = DateTime.Today + SchedTime.Value;   // the picker always holds a valid time
        var when = (PlannerKind == "task" ? DateTime.Today : SchedDate.SelectedDate ?? DateTime.Today).Date + time.TimeOfDay;
        if (PlannerKind == "task" && when <= DateTime.Now) when = when.AddDays(1);
        var repeat = (string)((ComboBoxItem)SchedRepeat.SelectedItem).Tag;
        if (when <= DateTime.Now && PlannerKind != "plan") when = Next(when, repeat) ?? when;   // your schedule keeps its first day
        if (when <= DateTime.Now && PlannerKind != "plan") { SchedNote.Text = "That time has already passed."; return; }   // (your schedule can include earlier today)
        var item = editingSched ?? new Scheduled();
        item.Kind = PlannerKind;
        item.Folder = item.Kind == "dev" || repeat == "files" ? devFolder : null; item.Text = text; item.When = when; item.Repeat = repeat; item.On = true; item.Ready = null;   // changed: get ready again
        if (repeat == "files" && PlannerKind != "task") { SchedNote.Text = "Triggers are for daily tasks."; return; }
        if (repeat == "files" && !System.IO.Directory.Exists(devFolder)) { SchedNote.Text = "Pick the folder to watch."; return; }
        item.Nag = SchedNag.IsChecked == true;
        item.Brief = item.Kind is "task" or "reminder" ? SchedBrief.IsChecked == true : null;
        item.Sound = SchedSound.IsChecked == true; item.Sticky = SchedSticky.IsChecked == true;
        item.Deliver = (string)((ComboBoxItem)SchedDeliver.SelectedItem).Tag;
        if (item.Kind is "task" or "dev")
        {
            if (item.Kind == "task")
            {
            }
            if (item.Kind == "task") item.Category = SchedCatNew.IsVisible && SchedCatNew.Text.Trim() != "" ? SchedCatNew.Text.Trim() : schedCat != "" ? schedCat : "Updates";
            if (S.Providers.Count == 0) { SchedNote.Text = "Add a provider in Settings first."; return; }
            if (item.Kind == "dev" && SchedModel.SelectedItem is ComboBoxItem { Tag: (string prov, string model) }) { item.Provider = prov; item.Model = model; }
            else { item.Provider = null; item.Model = null; }   // daily tasks: Settings → Tasks decides
        }
        if (editingSched == null) S.Schedules.Add(item);
        EndEdit();
        SaveSettings();
        SchedText.Clear(); SchedNote.Text = ""; SchedCatNew.Clear(); SchedCatNew.Visibility = Visibility.Collapsed;
        foreach (ToggleButton c in SchedQuick.Children) c.IsChecked = false;
        if (item.Kind == "task") BuildCategories();
        DrawSchedule();
    }

    // click a card to change it in the form above
    Scheduled? editingSched;
    void BeginEdit(Scheduled x)
    {
        if (x.Kind != PlannerKind) OpenPlanner(x.Kind);
        editingSched = x;
        if (x.Kind == "dev") SetDevFolder(x.Folder);
        RefreshPlannerEditor();
        if (x.Kind == "task") { schedCat = x.Category; BuildCategories(); }
        SchedText.Text = x.Text;
        SetWhen(x.When);
        SchedRepeat.SelectedItem = SchedRepeat.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == x.Repeat) ?? SchedRepeat.Items[0];
        SchedSound.IsChecked = x.Sound; SchedSticky.IsChecked = x.Sticky; SchedNag.IsChecked = x.Nag;
        SchedBrief.IsChecked = x.Brief ?? x.Kind == "task";
        if (x.Repeat == "files") SetDevFolder(x.Folder);
        SchedDeliver.SelectedItem = SchedDeliver.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == x.Deliver) ?? SchedDeliver.Items[0];
        if (x.Kind == "task") SchedModel.SelectedItem = SchedModel.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is (string p, string m) && p == x.Provider && m == x.Model) ?? SchedModel.SelectedItem;
        SchedAddIcon.Text = ""; SchedAddText.Text = "  Save changes"; SchedEditorTitle.Text = "Editing";
        SchedCancelEdit.Visibility = Visibility.Visible;
        SchedNote.Text = "";
        DrawSchedule();
        SchedText.Focus(); SchedText.CaretIndex = SchedText.Text.Length;
    }

    void EndEdit()
    {
        editingSched = null;
        SchedAddIcon.Text = ""; SchedAddText.Text = "  Schedule"; SchedEditorTitle.Text = "New";
        SchedCancelEdit.Visibility = Visibility.Collapsed;
    }

    void SchedCancelEdit_Click(object s, RoutedEventArgs e) { EndEdit(); SchedText.Clear(); DrawSchedule(); }
    void SchedNew_Click(object s, RoutedEventArgs e) { SchedCancelEdit_Click(s, e); SchedText.Focus(); }

    bool weekView;
    void SchedWeek_Click(object s, RoutedEventArgs e) { weekView = !weekView; SchedWeekBtn.Content = weekView ? "List" : "Week"; DrawSchedule(); }

    // The next seven days as columns, every kind together; a repeat shows on each day it falls on. Click one to edit it.
    void DrawWeek()
    {
        var grid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        for (int d = 0; d < 7; d++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        static bool Falls(Scheduled x, DateTime day) => x.On && !Triggered(x) && x.Repeat switch
        {
            "daily" => day >= x.When.Date,
            "weekdays" => day >= x.When.Date && day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            "weekly" => day >= x.When.Date && day.DayOfWeek == x.When.DayOfWeek,
            _ => x.When.Date == day,
        };
        for (int d = 0; d < 7; d++)
        {
            var day = DateTime.Today.AddDays(d);
            var col = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
            var head = new TextBlock { Text = d == 0 ? "Today" : d == 1 ? "Tomorrow" : day.ToString("ddd d"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 6) };
            head.SetResourceReference(TextBlock.ForegroundProperty, d == 0 ? "Fg" : "Dim");
            col.Children.Add(head);
            foreach (var x in S.Schedules.Where(x => Falls(x, day)).OrderBy(x => x.When.TimeOfDay))
            {
                var color = x.Kind == "dev" ? Shell.B("#58A6FF") : x.Kind == "task" ? Shell.B(Category(x.Category).Color) : ReminderColor;
                var t = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis };
                t.Inlines.Add(new System.Windows.Documents.Run(x.When.ToString("HH:mm") + " ") { Foreground = color, FontWeight = FontWeights.SemiBold });
                t.Inlines.Add(new System.Windows.Documents.Run(System.Text.RegularExpressions.Regex.Replace(x.Text, @"\s+", " ").Trim()));
                t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                var chip = new Border { Child = t, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = When.Full(x.When) + " · click to edit" };
                chip.SetResourceReference(Border.BackgroundProperty, "Glass");
                chip.MouseLeftButtonUp += (_, _) => { weekView = false; SchedWeekBtn.Content = "Week"; OpenPlanner(x.Kind); BeginEdit(x); };
                col.Children.Add(chip);
            }
            if (col.Children.Count == 1) { var none = new TextBlock { Text = "—", FontSize = 12, Margin = new Thickness(4, 0, 0, 0) }; none.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); col.Children.Add(none); }
            Grid.SetColumn(col, d); grid.Children.Add(col);
        }
        SchedList.Children.Add(grid);
    }

    // Run a daily task now: its answer goes to the Inbox like a scheduled run, but the schedule itself doesn't move.
    async void RunNow(Scheduled x)
    {
        Notify("daily", "Running now", x.Text, () => OpenInbox(), "\uE768", "#58A6FF");
        var a = await Answer(x);
        SaveSettings();
        if (a is not { } r) { _ = Confirm("Nothing new", "The task found nothing new to report this time.", cancel: null); return; }
        Inbox.Add(new InboxItem { Kind = "daily", TaskId = x.Id, Category = x.Category, Title = x.Text, Text = r.Text, Model = r.Model, Provider = r.Provider, Failed = r.Failed });
        Notify(r.Failed ? "failed" : "daily", r.Failed ? "Daily task failed" : x.Category != "" ? x.Category : "Daily task", Voice.Plain(r.Text), () => OpenInbox());
    }

    // A task's earlier answers (from the Inbox), newest first: the latest answer line by line against the one before it
    // (green: new, red: gone), then each earlier run with how much it changed from the run before it.
    Task ShowHistory(Scheduled x)
    {
        var runs = Inbox.All.Where(i => i.TaskId == x.Id && !i.Failed).OrderByDescending(i => i.At).Take(10).ToList();
        var failed = Inbox.All.Count(i => i.TaskId == x.Id && i.Failed);
        if (runs.Count == 0) return Confirm("No history yet", failed > 0 ? $"Every run so far failed ({failed}). Use Run now to try again." : "This task hasn't run yet. Use Run now to try it.", cancel: null);
        var shown = Confirm($"History · {runs.Count} run{(runs.Count == 1 ? "" : "s")}{(failed > 0 ? $" · {failed} failed" : "")}", "", cancel: null);
        DialogBox.Width = 680;
        var t = DialogText; t.Inlines.Clear();
        var green = Shell.B("#3FB950"); var red = Shell.B("#F87171");
        void Line(string s, Brush? color = null, bool bold = false)
        {
            var r = new System.Windows.Documents.Run(s + "\n") { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            if (color != null) r.Foreground = color;
            t.Inlines.Add(r);
        }
        if (runs.Count == 1)
        {
            Line($"{runs[0].At:ddd d MMM HH:mm} · the only run so far", bold: true);
            foreach (var l in runs[0].Text.Replace("\r", "").Split('\n').Where(l => l.Trim() != "").Take(60)) Line("   " + l.Trim());
            return shown;
        }
        var diff = LineDiff.Compare(runs[1].Text, runs[0].Text);
        var changed = diff.Where(d => d.Kind != LineDiff.Kind.Same).ToList();
        Line($"{runs[0].At:ddd d MMM HH:mm} compared with {runs[1].At:ddd d MMM HH:mm}", bold: true);
        if (changed.Count == 0) Line("   The same answer as the time before.");
        foreach (var (kind, l) in changed.Take(80))
            Line((kind == LineDiff.Kind.Added ? "+  " : "−  ") + l, kind == LineDiff.Kind.Added ? green : red);
        if (changed.Count > 80) Line($"   …and {changed.Count - 80} more changed lines");
        Line("");
        Line("Earlier runs", bold: true);
        for (int i = 1; i < runs.Count; i++)
        {
            var d = i + 1 < runs.Count ? LineDiff.Compare(runs[i + 1].Text, runs[i].Text) : null;
            var note = d == null ? "the oldest kept" : d.All(z => z.Kind == LineDiff.Kind.Same) ? "same as the time before"
                     : $"+{d.Count(z => z.Kind == LineDiff.Kind.Added)}  −{d.Count(z => z.Kind == LineDiff.Kind.Removed)} lines from the time before";
            Line($"   {runs[i].At:ddd d MMM HH:mm}  ·  {note}");
        }
        return shown;
    }

    static string Countdown(DateTime t)
    {
        var d = t - DateTime.Now;
        return d.TotalMinutes < 1 ? "now" : d.TotalHours < 1 ? $"in {(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"in {(int)d.TotalHours}h {d.Minutes}m" : $"in {(int)d.TotalDays}d {d.Hours}h";
    }

    void DrawSchedule()
    {
        PlannerCounts();
        SchedPlaceholder.Visibility = SchedText.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        SchedText.TextChanged -= SchedText_Changed; SchedText.TextChanged += SchedText_Changed;
        SchedList.Children.Clear();
        var kind = PlannerKind;
        var mine = S.Schedules.Where(x => x.Kind == kind).ToList();
        var on = mine.Where(x => x.On).OrderBy(x => x.When).ToList();
        SchedSummary.Text = on.Count == 0 ? "Nothing coming up" : $"{on.Count} active · next {Countdown(on[0].When)}";
        if (weekView) { DrawWeek(); return; }
        if (mine.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 10) };
            var (icon, title, hint) = kind switch
            {
                "task" => ("\uE706", "No daily tasks yet", "Pick a category above for quotes, alerts or follow-ups.\nOr ask in any chat: \"give me updates on this every morning\"."),
                "dev" => ("\uE943", "No dev tasks yet", "Schedule work for the coding agent in a folder.\nOr ask in any chat: \"run the tests here every night\"."),
                _ => ("\uEA8F", "No reminders", "Or ask in any chat: \"remind me to call mom at 6\"."),
            };
            empty.Children.Add(new TextBlock { Text = icon, FontFamily = App.IconFont, FontSize = 30, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("Dim") });
            empty.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2), HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("Fg") });
            empty.Children.Add(new TextBlock { Text = hint, FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("Dim") });
            SchedList.Children.Add(empty);
            return;
        }
        var today = DateTime.Today;
        // reminders and dev tasks by when; daily tasks by category (finished one-offs last)
        foreach (var group in mine.OrderByDescending(x => x.On).ThenBy(x => x.When).GroupBy(x =>
                     !x.On ? "Done" : kind == "task" ? Category(x.Category).Name : x.When.Date == today ? "Today" : x.When.Date == today.AddDays(1) ? "Tomorrow" : "Later"))
        {
            var head = new TextBlock { Text = group.Key, FontSize = 12, Margin = new Thickness(8, 14, 0, 2), Opacity = 0.8 };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            SchedList.Children.Add(head);
            foreach (var x in group) SchedList.Children.Add(ScheduleCard(x));
        }
    }

    void SchedText_Changed(object s, TextChangedEventArgs e) => SchedPlaceholder.Visibility = SchedText.Text == "" ? Visibility.Visible : Visibility.Collapsed;

    FrameworkElement ScheduleCard(Scheduled x)
    {
        var task = x.Kind != "reminder";
        var dev = x.Kind == "dev";
        var color = dev ? Shell.B("#58A6FF") : task ? Shell.B(Category(x.Category).Color) : ReminderColor;
        var badge = new TextBlock { Text = dev ? "\uE943" : task ? Category(x.Category).Icon : "\uEA8F", FontFamily = App.IconFont, FontSize = 15, Foreground = color,
                                   Width = 26, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        // a one-line title (prompts can span lines), under a top line with the time, the countdown and the details
        var title = new TextBlock { Text = System.Text.RegularExpressions.Regex.Replace(x.Text, @"\s+", " ").Trim(), FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = x.Text, Margin = new Thickness(0, 3, 0, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var repeat = x.Repeat switch { "daily" => "every day", "weekdays" => "weekdays", "weekly" => $"every {x.When:dddd}",
                                       "files" => $"when files change in {System.IO.Path.GetFileName(x.Folder ?? "")}", _ => "" };
        var details = new List<string>();
        if (repeat != "") details.Add(repeat);
        if (dev) { details.Add(x.Folder != null ? System.IO.Path.GetFileName(x.Folder) : "no folder"); details.Add(x.Model ?? "default model"); }
        else if (task) details.Add(x.Deliver switch { "open" => "opens the answer", "silent" => "saves quietly", _ => "notifies" });
        else { details.Add(x.Sound ? "sound" : "silent"); if (x.Sticky) details.Add("stays until dismissed"); }
        var top = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = When.Full(x.When) };
        var time = new System.Windows.Documents.Run(!x.On ? "Done" : x.App != "" ? $"When {x.App} opens" : Triggered(x) ? "Watching" : When.Stamp(x.When)) { FontWeight = FontWeights.SemiBold };
        time.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Fg");
        top.Inlines.Add(time);
        if (x.On && !Triggered(x) && x.App == "") top.Inlines.Add(new System.Windows.Documents.Run("  " + Countdown(x.When)) { Foreground = color });
        var rest = new System.Windows.Documents.Run("  ·  " + string.Join(" · ", details));
        rest.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Dim");
        top.Inlines.Add(rest);
        var del = new Button { Content = "", Style = (Style)FindResource("Ghost"), FontFamily = App.IconFont, FontSize = 14, ToolTip = "Delete", VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        del.Click += (_, _) => { if (editingSched == x) EndEdit(); S.Schedules.Remove(x); SaveSettings(); DrawSchedule(); };
        Button? run = null, hist = null;   // daily tasks: run it now, and what it reported before
        if (x.Kind == "task")
        {
            run = new Button { Content = "\uE768", Style = (Style)FindResource("Ghost"), FontFamily = App.IconFont, FontSize = 14, ToolTip = "Run now: the answer goes to the Inbox; the schedule stays as it is", VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            run.Click += (_, e) => { e.Handled = true; RunNow(x); };
            hist = new Button { Content = "\uE81C", Style = (Style)FindResource("Ghost"), FontFamily = App.IconFont, FontSize = 14, ToolTip = "History: its earlier answers", VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            hist.Click += (_, e) => { e.Handled = true; _ = ShowHistory(x); };
        }
        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left); DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(badge); row.Children.Add(del);
        if (run != null && hist != null) { DockPanel.SetDock(run, Dock.Right); DockPanel.SetDock(hist, Dock.Right); row.Children.Add(run); row.Children.Add(hist); }
        row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { top, title } });
        var card = new Border { Padding = new Thickness(10, 8, 4, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8), Child = row, Opacity = x.On ? 1 : 0.5 };
        card.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        card.Cursor = System.Windows.Input.Cursors.Hand; card.ToolTip = "Click to edit";
        var hover = new SolidColorBrush(Color.FromArgb(0x12, 0x80, 0x80, 0x90));
        if (editingSched == x) card.Background = hover;
        card.MouseEnter += (_, _) => { del.Opacity = 1; if (run != null) run.Opacity = 1; if (hist != null) hist.Opacity = 1; card.Background = hover; };
        card.MouseLeave += (_, _) => { del.Opacity = 0; if (run != null) run.Opacity = 0; if (hist != null) hist.Opacity = 0; if (editingSched != x) card.SetResourceReference(Border.BackgroundProperty, "BoxCard"); };
        card.MouseLeftButtonUp += (_, e) => { if (!del.IsMouseOver && run?.IsMouseOver != true && hist?.IsMouseOver != true) BeginEdit(x); };
        return card;
    }
}
