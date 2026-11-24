using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// The to-do list: its own panel (title bar -> To-do). Models change it through the todo tool (Planner.cs).
public partial class MainWindow
{
    // Reminders, Daily tasks and Dev tasks: one panel, opened as three separate windows from the title bar
    // task = daily task (answers in the background); dev = the coding agent works in a folder
    string PlannerKind => PlannerTaskTab.IsChecked == true ? "task" : PlannerDevTab.IsChecked == true ? "dev" : PlannerPlanTab.IsChecked == true ? "plan" : "reminder";

    string? shownKind;
    readonly Dictionary<string, string> drafts = [];

    void PlannerTab_Checked(object s, RoutedEventArgs e)
    {
        if (SchedView == null) return;
        var kind = PlannerKind;
        (PlannerTitle.Text, PlannerIcon.Text) = kind switch { "task" => ("Ivo's daily tasks", "\uE706"), "dev" => ("Dev tasks", "\uE943"), "plan" => ("My schedule", "\uE8C0"), _ => ("Reminders", "\uEA8F") };
        if (kind == "plan") PlannerIcon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        else PlannerIcon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        PlannerCounts();
        PlannerIcon.Foreground = Shell.B(kind switch { "task" => "#FB923C", "dev" => "#58A6FF", "plan" => "#A78BFA", _ => "#F2B84B" });
        // each window keeps its own unfinished text; nothing (edit, category, note) carries over to another
        if (shownKind != null) drafts[shownKind] = editingSched == null ? SchedText.Text : "";
        if (shownKind != kind)
        {
            if (editingSched != null) EndEdit();
            SchedText.Text = drafts.GetValueOrDefault(kind, "");
            SchedNote.Text = ""; schedCat = ""; SchedCatNew.Clear();
            foreach (System.Windows.Controls.Primitives.ToggleButton c in SchedQuick.Children) c.IsChecked = false;
        }
        shownKind = kind;
        RefreshPlannerEditor();
        var dev = kind == "dev";
        ((FrameworkElement)SchedCatLabel.Parent).Visibility = dev ? Visibility.Collapsed : Visibility.Visible;   // the whole Category row
        // daily tasks stay basic: a notification with the answer, a time and how often
        var daily = kind == "task";
        DeliverRow.Visibility = ModelRow.Visibility = daily ? Visibility.Collapsed : Visibility.Visible;   // daily tasks: the model is set in Settings → Tasks
        SchedQuick.Visibility = SchedDate.Visibility = daily ? Visibility.Collapsed : Visibility.Visible;
        if (daily) SchedDeliver.SelectedIndex = 0;
        if (dev) SchedCatNew.Visibility = Visibility.Collapsed;
        DevFolderRow.Visibility = dev ? Visibility.Visible : Visibility.Collapsed;
        // your schedule: what, when and how often; nothing to deliver, no sound, no model
        var plan = kind == "plan";
        foreach (var row in new FrameworkElement[] { (FrameworkElement)SchedCatLabel.Parent, (FrameworkElement)SchedBrief.Parent, (FrameworkElement)SchedSound.Parent })
            if (plan) row.Visibility = Visibility.Collapsed; else if (row != SchedCatLabel.Parent || !dev) row.Visibility = Visibility.Visible;
        if (plan) { DeliverRow.Visibility = ModelRow.Visibility = Visibility.Collapsed; SchedPlaceholder.Text = "What's on, e.g. Gym, Class, Lunch with Sam…"; }
        if (dev && editingSched == null && devFolder == null) SetDevFolder(root);
        SchedPlaceholder.Text = dev ? "Work for the agent, e.g. Run the tests, fix what fails and summarise the changes… (start with @agent-name to give it to one of your agents)" : SchedPlaceholder.Text;
        SchedRepeat.SelectedIndex = kind == "task" ? 1 : 0;   // daily tasks are usually daily
        if (kind == "task") SetWhen(DateTime.Now.Hour < 8 ? DateTime.Today.AddHours(8) : DateTime.Today.AddDays(1).AddHours(8));
        DrawSchedule();
        SchedText.Focus();
    }

    public void OpenPlanner(string kind)
    {
        if (kind == "todo") { OpenTodos(); return; }
        OpenSchedule_Click(this, new RoutedEventArgs());
        var tab = kind switch { "task" => PlannerTaskTab, "dev" => PlannerDevTab, "plan" => PlannerPlanTab, _ => PlannerRemTab };
        if (tab.IsChecked == true) PlannerTab_Checked(tab, new RoutedEventArgs()); else tab.IsChecked = true;
    }

    // the counts in the planner's first column: what's still on, per part
    void PlannerCounts()
    {
        string N(string kind) { var n = S.Schedules.Count(x => x.Kind == kind && x.On); return n > 0 ? n.ToString() : ""; }
        RemCount.Text = N("reminder"); DailyCount.Text = N("task"); DevCount.Text = N("dev"); PlanCount.Text = N("plan");
    }

    // the title bar's Planner button: opens where you were last
    void OpenPlanner_Click(object s, RoutedEventArgs e) => OpenPlanner(shownKind ?? "reminder");
    void OpenTodos_Click(object s, RoutedEventArgs e) => OpenTodos();
    public void OpenTodos() { DrawTodos(); TodoLayer.Visibility = Visibility.Visible; TodoBox.Focus(); }
    void CloseTodo_Click(object s, RoutedEventArgs e) => TodoLayer.Visibility = Visibility.Collapsed;

    string? devFolder;
    void DevFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder the agent works in" };
        if (devFolder != null) dlg.InitialDirectory = devFolder;
        if (dlg.ShowDialog(this) == true) SetDevFolder(dlg.FolderName);
    }
    void SetDevFolder(string? f) { devFolder = f; DevFolderText.Text = f ?? "No folder: works as a plain chat"; DevFolderText.ToolTip = f; }

    void TodoBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || TodoBox.Text.Trim() == "") return;
        e.Handled = true;
        // one line, the whole to-do: "Call mom tomorrow 6pm !high #family ~Home every week"
        var todo = TodoText.Parse(TodoBox.Text);
        if (todo.List == "" && todoView.StartsWith('~')) todo.List = todoView[1..];   // added while a list is open: into that list
        if (todo.Due == null && todoView == "today") todo.Due = DateTime.Today;
        S.Todos.Add(todo);
        TodoBox.Clear();
        SaveSettings(); DrawTodos(); SyncTodosSoon();
    }

    void TodoClear_Click(object s, RoutedEventArgs e) { foreach (var t in S.Todos.Where(t => t.Done)) TodoSync.Removed(S, t); S.Todos.RemoveAll(t => t.Done); SaveSettings(); DrawTodos(); SyncTodosSoon(); }

    // Sync: a menu to pick the service (Notion, Google Tasks) and sync now; it also syncs every 5 minutes and after changes
    void TodoSync_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)s, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        if (TodoSync.On(S))
        {
            var now = new MenuItem { Header = $"Sync now with {TodoSync.Name(S)}" };
            now.Click += async (_, _) => await SyncTodos();
            menu.Items.Add(now); menu.Items.Add(new Separator());
        }
        foreach (var (id, name) in TodoSync.Services)
        {
            var linked = id == "notion" && Connections.FirstToken("notion") != "";   // Notion already in Connections: only the database is left to choose
            var item = new MenuItem { Header = TodoSync.On(S) && TodoSync.Service(S) == id ? name + " (syncing)" : linked ? "Sync with " + name : "Connect " + name };
            item.Click += async (_, _) => { if (await ConnectTodos(id)) await SyncTodos(); };
            menu.Items.Add(item);
        }
        if (TodoSync.On(S))
        {
            var off = new MenuItem { Header = "Stop syncing" };
            off.Click += (_, _) => { TodoSync.Switch(S, "off"); S.GoogleTasksToken = ""; GoogleTasks.Forget(); SaveSettings(); TodoSyncText.Text = ""; };
            menu.Items.Add(off);
        }
        menu.IsOpen = true;
    }

    // asks for what the service needs; the list then syncs with it alone
    async Task<bool> ConnectTodos(string service)
    {
        switch (service)
        {
            case "notion":
                if (Connections.FirstToken("notion") == "")
                {
                    var secret = await AskText("Sync with Notion", "Paste a Notion integration secret (notion.so/profile/integrations → New integration). It's also added to Settings → Connections.", "", "Next");
                    if (string.IsNullOrWhiteSpace(secret)) return false;
                    try { await Connections.Check(Connections.All.First(c => c.Id == "notion"), secret.Trim()); }
                    catch (Exception ex) { _ = Notice("Notion didn't accept that secret", ex.Message); return false; }
                    S.Connections["notion"] = Store.Protect(secret.Trim());
                }
                // the databases shared with the integration: one with a checkbox column is used as is; otherwise ask which
                var dbs = await TodoSync.NotionDatabases(S);
                var db = dbs.Count(d => d.HasCheckbox) == 1 ? dbs.First(d => d.HasCheckbox).Id : null;
                if (db == null)
                {
                    var known = string.Join(", ", dbs.Select(d => d.Title + (d.HasCheckbox ? "" : " (no checkbox column)")));
                    var link = await AskText("Sync with Notion", "Paste the link to the Notion database for your to-dos. It needs a checkbox column (ticked = done), and your integration added to it (••• → Connections)." +
                                             (known != "" ? $"\n\nShared with Ivo now: {known}." : "\n\nNo database is shared with Ivo yet."), "", "Connect");
                    if (link == null) return false;
                    if (TodoSync.NotionDbId(link) is not { } id) { _ = Notice("That isn't a Notion database link", "Open the database in Notion, then Share → Copy link."); return false; }
                    db = id;
                }
                TodoSync.Switch(S, "notion"); S.NotionTodoDb = db;
                break;
            case "google":
                var client = await AskText("Sync with Google Tasks",
                    "Google needs your own app for this, once: console.cloud.google.com → enable the Google Tasks API → Credentials → Create OAuth client ID → Desktop app. " +
                    "Paste its client ID and client secret here, separated by a space. Then sign in in the browser.", "", "Sign in");
                if (client?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is not [var cid, var csecret]) return false;
                S.GoogleTasksClient = Store.Protect(cid + "|" + csecret);
                try { await GoogleTasks.SignIn(S); }
                catch (Exception ex) { _ = Notice("Google sign-in didn't finish", ex.Message); return false; }
                TodoSync.Switch(S, "google");
                break;
        }
        SaveSettings();
        return true;
    }
    async Task SyncTodos()
    {
        if (!TodoSync.On(S)) return;
        TodoSyncText.Text = "Syncing…";
        if (await TodoSync.Run(S)) { SaveSettings(); DrawTodos(); }
        TodoSyncText.Text = TodoSync.Status;
    }
    DateTime nextTodoSync;
    void SyncTodosSoon() => nextTodoSync = DateTime.Now.AddSeconds(5);
    public void SyncTodosIfDue() { if (TodoSync.On(S) && DateTime.Now >= nextTodoSync) { nextTodoSync = DateTime.Now.AddMinutes(5); _ = SyncTodos(); } }

    void DrawTodos()
    {
        TodoPlaceholder.Visibility = TodoBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        TodoBox.TextChanged -= TodoBox_Changed; TodoBox.TextChanged += TodoBox_Changed;
        var open = S.Todos.Count(t => !t.Done);
        TodoSummary.Text = S.Todos.Count == 0 ? "Nothing to do" : $"{open} open · {S.Todos.Count - open} done";
        TodoClear.Visibility = S.Todos.Any(t => t.Done) ? Visibility.Visible : Visibility.Collapsed;

        // the views: Today, Next 7 days, All, then one per list
        var lists = S.Todos.Select(t => t.List).Where(l => l != "").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l).ToList();
        if (todoView.StartsWith('~') && !lists.Contains(todoView[1..], StringComparer.OrdinalIgnoreCase)) todoView = "all";
        TodoViews.Children.Clear();
        foreach (var (id, label) in new[] { ("today", "Today"), ("week", "Next 7 days"), ("all", "All") }.Concat(lists.Select(l => ("~" + l, l))))
        {
            var n = S.Todos.Count(t => !t.Done && InView(t, id));
            var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = n > 0 ? $"{label}  {n}" : label, Style = (Style)FindResource("Chip"), IsChecked = todoView == id, Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) => { todoView = id; DrawTodos(); };
            TodoViews.Children.Add(chip);
        }

        TodoList.Children.Clear();
        var shown = S.Todos.Where(t => InView(t, todoView) || t.Done && todoView == "all").ToList();
        if (shown.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 10) };
            empty.Children.Add(new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 34, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("Dim") });
            empty.Children.Add(new TextBlock { Text = todoView == "today" ? "Nothing due today" : "All clear", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2), HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("Fg") });
            TodoList.Children.Add(empty);
            return;
        }
        // sections, in time order; inside each, the most important first
        var today = DateTime.Today;
        string Section(Todo t) => t.Done ? "Done" : t.Due is not { } d ? "No date" : d.Date < today ? "Overdue" : d.Date == today ? "Today" : d.Date == today.AddDays(1) ? "Tomorrow" : d.Date < today.AddDays(7) ? "Next 7 days" : "Later";
        string[] order = ["Overdue", "Today", "Tomorrow", "Next 7 days", "Later", "No date", "Done"];
        foreach (var g in shown.GroupBy(Section).OrderBy(g => Array.IndexOf(order, g.Key)))
        {
            var head = new TextBlock { Text = g.Key.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, TodoList.Children.Count == 0 ? 2 : 14, 0, 6) };
            if (g.Key == "Overdue") head.Foreground = Shell.B("#F87171"); else head.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            TodoList.Children.Add(head);
            foreach (var t in g.OrderByDescending(t => t.Priority).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => t.Created)) TodoList.Children.Add(TodoRow(t));
        }
    }

    string todoView = "all";
    readonly HashSet<string> todoOpen = [];   // to-dos showing their details and steps

    static bool InView(Todo t, string view) => view switch
    {
        "today" => !t.Done && t.Due is { } d && d.Date <= DateTime.Today,
        "week" => !t.Done && t.Due is { } d && d.Date < DateTime.Today.AddDays(7),
        "all" => !t.Done,
        _ => !t.Done && t.List.Equals(view[1..], StringComparison.OrdinalIgnoreCase),
    };

    static readonly string[] PrioColor = ["", "#58A6FF", "#F59E0B", "#F43F5E"];

    void TodoBox_Changed(object s, TextChangedEventArgs e) => TodoPlaceholder.Visibility = TodoBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;

    // the line under a to-do: when, repeat, its list, tags, steps
    static string TodoMeta(Todo t)
    {
        var bits = new List<string>();
        if (t.Due is { } d) bits.Add((d.Date == DateTime.Today ? "Today" : d.Date == DateTime.Today.AddDays(1) ? "Tomorrow" : When.Day(d)) + (t.DueTime ? " " + When.Time(d) : ""));
        if (t.Repeat != "") bits.Add("↻ " + t.Repeat);
        if (t.List != "") bits.Add(t.List);
        bits.AddRange(t.Tags.Select(x => "#" + x));
        if (t.Steps.Count > 0) bits.Add($"{t.Steps.Count(x => x.Done)}/{t.Steps.Count} steps");
        return string.Join("  ·  ", bits);
    }

    void Tick(Todo t)
    {
        if (!t.Done && t.Repeat != "") { t.Due = TodoText.NextDue(t); foreach (var x in t.Steps) x.Done = false; }   // a repeating one moves on instead
        else t.Done = !t.Done;
        Sounds.Snap();
        SaveSettings(); DrawTodos(); SyncTodosSoon();
    }

    FrameworkElement TodoRow(Todo t)
    {
        var tint = t.Done ? "#3FB950" : t.Priority > 0 ? PrioColor[t.Priority] : null;
        var check = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.6), Margin = new Thickness(0, 1, 12, 0),
                                 VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand, ToolTip = t.Done ? "Mark as not done" : t.Repeat != "" ? $"Done for now (it comes back, {t.Repeat})" : "Mark as done",
                                 Background = t.Done ? Shell.B("#3FB950") : Brushes.Transparent,
                                 Child = new TextBlock { Text = t.Done ? "" : "", FontFamily = App.IconFont, FontSize = 12, Foreground = Brushes.Black,
                                                         HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (tint != null) check.BorderBrush = Shell.B(tint); else check.SetResourceReference(Border.BorderBrushProperty, "Dim");
        check.MouseLeftButtonUp += (_, e) => { e.Handled = true; Tick(t); };

        var text = new TextBlock { Text = t.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextDecorations = t.Done ? TextDecorations.Strikethrough : null, Opacity = t.Done ? 0.5 : 1 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var info = TodoMeta(t);
        var meta = new TextBlock { Text = info, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = info == "" ? Visibility.Collapsed : Visibility.Visible };
        if (!t.Done && t.Due is { } due && (t.DueTime ? due < DateTime.Now : due.Date < DateTime.Today)) meta.Foreground = Shell.B("#F87171");
        else meta.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var edit = new TextBox { Text = t.Text, FontSize = 14, Visibility = Visibility.Collapsed };
        var words = new StackPanel { Children = { text, edit, meta } };

        var del = new Button { Content = "", Style = (Style)FindResource("Ghost"), FontFamily = App.IconFont, FontSize = 14, ToolTip = "Delete", Opacity = 0, VerticalAlignment = VerticalAlignment.Top };
        del.Click += (_, _) => { TodoSync.Removed(S, t); S.Todos.Remove(t); SaveSettings(); DrawTodos(); SyncTodosSoon(); };
        var row = new DockPanel { Background = Brushes.Transparent };
        DockPanel.SetDock(check, Dock.Left); DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(check); row.Children.Add(del); row.Children.Add(words);

        var body = new StackPanel { Children = { row } };
        if (todoOpen.Contains(t.Id)) body.Children.Add(TodoDetails(t));
        var card = new Border { Padding = new Thickness(12, 10, 6, 10), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 6), Child = body, Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        card.MouseEnter += (_, _) => del.Opacity = 1;
        card.MouseLeave += (_, _) => del.Opacity = 0;
        // a click opens its details and steps; a double-click on the words edits them
        row.MouseLeftButtonUp += (_, e) => { if (edit.IsVisible) return; if (!todoOpen.Remove(t.Id)) todoOpen.Add(t.Id); DrawTodos(); };
        void Save(bool keep)
        {
            if (keep && edit.Text.Trim() != "") t.Text = edit.Text.Trim();
            SaveSettings(); DrawTodos();
        }
        text.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            e.Handled = true;
            text.Visibility = Visibility.Collapsed; edit.Visibility = Visibility.Visible;
            edit.Focus(); edit.SelectAll();
        };
        edit.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Save(true); } else if (e.Key == Key.Escape) { e.Handled = true; Save(false); } };
        edit.LostFocus += (_, _) => { if (edit.IsVisible) Save(true); };
        return card;
    }

    // under an open to-do: when (quick picks), priority, repeat, and its steps
    FrameworkElement TodoDetails(Todo t)
    {
        var box = new StackPanel { Margin = new Thickness(30, 10, 6, 2) };
        void Changed() { SaveSettings(); DrawTodos(); }
        System.Windows.Controls.Primitives.ToggleButton Chip(string label, bool on, Action click)
        {
            var c = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Style = (Style)FindResource("Chip"), IsChecked = on, Margin = new Thickness(0, 0, 6, 6) };
            c.Click += (_, e) => { e.Handled = true; click(); Changed(); };
            c.MouseLeftButtonUp += (_, e) => e.Handled = true;
            return c;
        }
        var when = new WrapPanel();
        foreach (var (label, day) in new (string, DateTime?)[] { ("Today", DateTime.Today), ("Tomorrow", DateTime.Today.AddDays(1)), ("Next week", DateTime.Today.AddDays(7)), ("No date", null) })
            when.Children.Add(Chip(label, day == null ? t.Due == null : t.Due?.Date == day.Value, () => { t.Due = day; t.DueTime = false; }));
        var prio = new WrapPanel();
        foreach (var (label, p) in new[] { ("No priority", 0), ("Low", 1), ("Medium", 2), ("High", 3) })
            prio.Children.Add(Chip(label, t.Priority == p, () => t.Priority = p));
        var rep = new WrapPanel();
        foreach (var (label, r) in new[] { ("Once", ""), ("Daily", "daily"), ("Weekdays", "weekdays"), ("Weekly", "weekly"), ("Monthly", "monthly") })
            rep.Children.Add(Chip(label, t.Repeat == r, () => { t.Repeat = r; if (r != "" && t.Due == null) t.Due = DateTime.Today; }));
        box.Children.Add(when); box.Children.Add(prio); box.Children.Add(rep);

        // steps: tick, and add with Enter
        foreach (var st in t.Steps.ToList())
        {
            var label = new TextBlock { Text = st.Text, FontSize = 13, TextDecorations = st.Done ? TextDecorations.Strikethrough : null };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var c = new CheckBox { IsChecked = st.Done, Margin = new Thickness(0, 4, 0, 0), Content = label };
            c.Click += (_, e) => { e.Handled = true; st.Done = c.IsChecked == true; Changed(); };
            c.MouseLeftButtonUp += (_, e) => e.Handled = true;
            box.Children.Add(c);
        }
        var add = new TextBox { FontSize = 13 };
        add.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || add.Text.Trim() == "") return;
            e.Handled = true; t.Steps.Add(new Subtask { Text = add.Text.Trim() }); Changed();
        };
        var hinted = Hinted(add, "Add a step, then Enter"); hinted.Margin = new Thickness(0, 8, 0, 0);
        box.Children.Add(hinted);
        box.MouseLeftButtonUp += (_, e) => e.Handled = true;
        return box;
    }
}
