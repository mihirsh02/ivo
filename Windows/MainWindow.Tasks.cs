using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Settings → Tasks: the model daily tasks run on, the one a new dev task starts with, and the models pinned for the
// Dev tasks window (its Model dropdown shows only these). Every model of every provider, gateway and CLI tool is offered.
public partial class MainWindow
{
    // Every usable model, by the chat model picker's rules: failed models never; once a provider has a model that's known
    // to work (and "Show only working models" is on), only its working ones, plus pinned and its default; `keep` stays.
    List<(string Provider, string Model, bool Pinned)> AllModels(string keep = "")
    {
        var list = new List<(string, string, bool)>();
        foreach (var p in S.Providers)
        {
            var ids = (ModelCache.GetValueOrDefault(p.Name) ?? (IEnumerable<string>)Api.ModelsFor(p)).Distinct().ToList();
            string? St(string m) => S.ModelStatus.GetValueOrDefault($"{p.Name}|{m}");
            var def = S.Models.GetValueOrDefault(p.Name);
            var anyOk = ids.Any(m => St(m) == "ok");
            foreach (var m in ids)
            {
                var pinned = S.Pinned.Contains($"{p.Name}|{m}");
                var mine = keep == $"{p.Name}/{m}" || pinned || m == def;
                if (St(m) is { } st && st != "ok" && !mine) continue;                       // checked and failed
                if (p.OnlyWorking && anyOk && St(m) != "ok" && !mine) continue;            // not known to work (that provider's switch)
                list.Add((p.Name, m, pinned));
            }
        }
        return list;
    }

    // Like the chat's model picker: one heading per provider / gateway / CLI tool, then Pinned, then the rest grouped by
    // vendor (google/…, meta-llama/…) A–Z with short names. A long list scrolls smoothly; type to jump.
    void FillModels(ComboBox pick, string? first, string selected)
    {
        var items = new List<ComboBoxItem>();
        if (first != null) items.Add(new() { Content = first, Tag = "" });
        ComboBoxItem Head(string text, double size, double top, double left) =>
            new() { IsEnabled = false, Content = text, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0), Padding = new Thickness(left, 2, 6, 2) };
        static string Vendor(string id) => id.Contains('/') ? id[..id.IndexOf('/')].TrimStart('~') : "";
        static string Short(string id) => id[(id.LastIndexOf('/') + 1)..];
        foreach (var g in AllModels(selected).GroupBy(x => x.Provider))
        {
            var kind = S.Providers.FirstOrDefault(p => p.Name == g.Key) is { } cfg ? CategoryOf(cfg) switch { "agents" => "CLI tool", "gateways" => "gateway", _ => "provider" } : "";
            items.Add(Head($"{g.Key}  ·  {kind}", 12, items.Count == 0 ? 0 : 8, 6));
            var sections = g.OrderByDescending(x => x.Pinned).ThenBy(x => Vendor(x.Model), StringComparer.OrdinalIgnoreCase).ThenBy(x => Short(x.Model), StringComparer.OrdinalIgnoreCase)
                            .GroupBy(x => x.Pinned ? "PINNED" : Vendor(x.Model) == "" ? "" : Vendor(x.Model).ToUpperInvariant()).ToList();
            foreach (var sec in sections)
            {
                if (sec.Key != "" && sections.Count > 1) items.Add(Head(sec.Key, 10, 4, 18));
                foreach (var (prov, model, _) in sec)
                    items.Add(new() { Content = Short(model), Tag = $"{prov}/{model}", Padding = new Thickness(sec.Key != "" && sections.Count > 1 ? 28 : 18, 3, 6, 3), ToolTip = $"{model} · {prov}" });
            }
        }
        pick.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        pick.ItemsSource = items;
        pick.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == selected) ?? (first != null ? items[0] : null);
    }

    bool fetchingModels, briefingHooked;
    void ShowTaskModels()
    {
        if (!briefingHooked) { BriefingTimeBox.Changed += (_, _) => Briefing_Changed(BriefingTimeBox, new RoutedEventArgs()); briefingHooked = true; }
        loading = true;
        // the same picker as in a chat; empty = whatever new chats use
        var daily = new ModelChooser(this, S.DailyModel, "Same as new chats");
        daily.Changed += v => { S.DailyModel = v; SaveSettings(); };
        DailyModelHost.Content = daily;
        var dev = new ModelChooser(this, S.DevModel, "Same as new chats");
        dev.Changed += v => { S.DevModel = v; SaveSettings(); SchedModel.ItemsSource = null; };   // the Dev tasks window rebuilds its list with the new default
        DevModelHost.Content = dev;
        FillModels(DevPinPick, null, "");
        if (DevPinPick.Items.Count > 1) DevPinPick.SelectedIndex = 1;
        BriefingBox.IsChecked = S.Briefing; BriefingTimeBox.Value = TimeSpan.TryParse(S.BriefingTime, out var bt) ? bt : new TimeSpan(8, 0, 0);
        loading = false;
        ShowBriefingNext();
        DrawDevPins();
        _ = FetchMissingModels();
    }

    // providers whose live model list hasn't been fetched this session (gateways list hundreds): fetch, then redraw
    async Task FetchMissingModels()
    {
        if (fetchingModels) return;
        var missing = S.Providers.Where(p => !ModelCache.ContainsKey(p.Name)).ToList();
        if (missing.Count == 0) return;
        fetchingModels = true;
        TaskModelsNote.Text = "Loading every provider's model list…";
        await Task.WhenAll(missing.Select(async p =>
        {
            try { ModelCache[p.Name] = await Api.ListModels(p, Store.Unprotect(p.Key)); } catch { }
        }));
        fetchingModels = false;
        TaskModelsNote.Text = "";
        if (TasksTab.IsVisible) ShowTaskModels();
    }


    void Briefing_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        S.BriefingTime = $"{BriefingTimeBox.Value:hh\\:mm}";
        S.Briefing = BriefingBox.IsChecked == true;
        S.BriefingNext = NextBriefing();   // from now on, at the chosen time
        SaveSettings();
        ShowBriefingNext();
    }

    void ShowBriefingNext()
    {
        BriefingNextText.Text = S.Briefing ? $"next: {When.Day(S.BriefingNext).ToLower()} at {When.Time(S.BriefingNext)}" : "off: each daily task arrives on its own";
        DrawBriefingPreview();
    }

    // The next briefing's sections: each daily task in it, the model that will answer (and whether that model is resting
    // after failing, in which case another one stands in), and Run to get just that section now.
    void DrawBriefingPreview()
    {
        BriefingPreview.Children.Clear();
        var tasks = S.Schedules.Where(x => x.On && x.Kind == "task" && (x.Brief ?? true)).OrderBy(x => Categories.Select(k => k.Name).ToList().IndexOf(x.Category)).ToList();
        if (tasks.Count == 0) { BriefingPreview.Children.Add(new TextBlock { Text = "No daily tasks yet. Add one in the Planner.", FontSize = 12, Foreground = (Brush)FindResource("Dim") }); return; }
        var (prov, model) = TaskModel(S.DailyModel);
        var resting = prov != null && model != null && Router.IsResting(prov, model);
        foreach (var x in tasks)
        {
            var name = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = x.Text };
            name.Inlines.Add(new System.Windows.Documents.Run(x.Category != "" ? x.Category : x.Text.Split('\n')[0]));
            name.Inlines.Add(new System.Windows.Documents.Run($"  ·  {model ?? "no model"}{(resting ? " (resting after errors: another model stands in)" : "")}") { Foreground = (Brush)FindResource("Dim") });
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var run = new Button { Content = "Run", Padding = new Thickness(12, 3, 12, 4), ToolTip = "Run just this section now; it arrives in the Inbox" };
            var task = x;
            run.Click += (_, _) => RunNow(task);
            DockPanel.SetDock(run, Dock.Right);
            var row = new Border { Padding = new Thickness(12, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8, 8, 8, 8.01), Child = new DockPanel { Children = { run, name } } };
            row.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            BriefingPreview.Children.Add(row);
        }
    }

    void DevPinAdd_Click(object s, RoutedEventArgs e)
    {
        if (DevPinPick.SelectedItem is not ComboBoxItem { Tag: string id } || id == "" || S.DevPinned.Contains(id)) return;
        S.DevPinned.Add(id); SaveSettings(); DrawDevPins();
        SchedModel.ItemsSource = null;   // the Dev tasks window rebuilds its list
    }

    void DrawDevPins()
    {
        DevPinList.Children.Clear();
        if (S.DevPinned.Count == 0)
            DevPinList.Children.Add(new TextBlock { Text = "Nothing pinned.", FontSize = 12, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(0, 4, 0, 0) });
        foreach (var id in S.DevPinned.ToList())
        {
            var (prov, model) = id.Split('/', 2) is [var p, var m] ? (p, m) : (id, "");
            var text = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run(model));
            text.Inlines.Add(new System.Windows.Documents.Run($"  ·  {prov}") { Foreground = (Brush)FindResource("Dim") });
            text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var del = new Button { Content = "", FontFamily = App.IconFont, FontSize = 12, Style = (Style)FindResource("Ghost"), ToolTip = "Unpin" };
            del.Click += (_, _) => { S.DevPinned.Remove(id); SaveSettings(); DrawDevPins(); SchedModel.ItemsSource = null; };
            DockPanel.SetDock(del, Dock.Right);
            var row = new Border { Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                                   Child = new DockPanel { Children = { del, text } } };
            row.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            DevPinList.Children.Add(row);
        }
    }

    // the Dev tasks window's Model dropdown: the pinned models, plus the dev default so there's always one
    List<ComboBoxItem> DevChoices()
    {
        var (dp, dm) = TaskModel(S.DevModel);
        var ids = S.DevPinned.Where(id => id.Split('/', 2) is [var p, _] && S.Providers.Any(x => x.Name == p)).ToList();
        if (dp != null && dm != null && !ids.Contains($"{dp}/{dm}")) ids.Insert(0, $"{dp}/{dm}");
        return ids.Select(id => id.Split('/', 2)).Select(a => new ComboBoxItem { Content = $"{a[1]}  ·  {a[0]}", Tag = (a[0], a[1]) }).ToList();
    }

    // "provider/model" from Settings → Tasks, or the default chat model when empty or no longer set up
    (string? Provider, string? Model) TaskModel(string setting) =>
        ModelChooser.Split(S, setting) is var (p, m) && (p == Router.Name ? S.AutoOn : S.Providers.Any(x => x.Name == p)) ? (p, m) : (S.Provider, DefaultModel(S.Provider, ModelCache.GetValueOrDefault(S.Provider)));
}
