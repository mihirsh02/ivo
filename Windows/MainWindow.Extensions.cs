using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ivo;

// What extends the models: saved prompts, Scan this PC, skills (and their marketplace), MCP servers.
public partial class MainWindow
{
    // ---------- saved prompts (Settings → Prompts) ----------
    string? editingPrompt;

    void RefreshPrompts()
    {
        UpdateCounts();
        PromptsList.Children.Clear();
        foreach (var sp in S.Prompts.OrderBy(p => p.Name))
        {
            var edit = ChatPane.SmallButton("\uE70F", "Edit");
            var remove = ChatPane.SmallButton("\uE74D", "Delete");
            edit.Click += (_, _) =>
            {
                PromptName.Text = sp.Name; PromptText.Text = sp.Text; editingPrompt = sp.Name;
                PromptSaveText.Text = "Save"; PromptCancel.Visibility = Visibility.Visible; PromptStatus.Text = "";
                PromptName.Focus();
            };
            remove.Click += (_, _) => { S.Prompts.Remove(sp); SaveSettings(); RefreshPrompts(); };
            var name = new TextBlock { Text = "/" + sp.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var head = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, remove } };
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons); head.Children.Add(name);
            var preview = new TextBlock { Text = sp.Text.Trim(), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            PromptsList.Children.Add(Card(new StackPanel { Children = { head, preview } }));
        }
    }

    void PromptSave_Click(object s, RoutedEventArgs e)
    {
        var name = PromptName.Text.Trim().TrimStart('/');
        if (!Regex.IsMatch(name, @"^[\w-]{1,30}$")) { PromptStatus.Text = "Short, no spaces, e.g. fix-tone."; return; }
        if (PromptText.Text.Trim() == "") { PromptStatus.Text = "Write the prompt text."; return; }
        if (S.Prompts.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Name != editingPrompt)) { PromptStatus.Text = $"There's already a /{name} prompt."; return; }
        S.Prompts.RemoveAll(p => p.Name == editingPrompt);
        S.Prompts.Add(new(name, PromptText.Text));
        SaveSettings();
        PromptCancel_Click(s, e);
        RefreshPrompts();
    }

    void PromptCancel_Click(object s, RoutedEventArgs e)
    {
        editingPrompt = null; PromptName.Text = ""; PromptText.Text = ""; PromptStatus.Text = "";
        PromptSaveText.Text = "Add"; PromptCancel.Visibility = Visibility.Collapsed;
    }

    // ---------- Scan this PC (Settings → API keys) ----------
    // Find on this PC: what's set up here (read-only, quick)
    async void Scan_Click(object s, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false; ScanText.Text = "Scanning…";
        ScanResults.Children.Clear();
        try { ShowScan(await Detect.Scan(), ScanResults, agentsOnly: false); }
        catch (Exception ex) { ScanResults.Children.Add(ScanLine("Couldn't finish: " + ex.Message)); }
        ScanButton.IsEnabled = true; ScanText.Text = "Scan again";
        DrawModelPicks();   // anything just added can be checked straight away
    }

    // Check models: the providers to check, each with when it was last checked; all ticked to start with
    readonly HashSet<string> skipChecks = [];
    void DrawModelPicks()
    {
        ModelPicks.Children.Clear();
        if (S.Providers.Count == 0) { ModelPicks.Children.Add(DimText("Add a provider first.", 12)); CheckModelsButton.IsEnabled = false; return; }
        CheckModelsButton.IsEnabled = true;
        foreach (var cfg in S.Providers)
        {
            var at = S.ModelStatus.GetValueOrDefault($"{cfg.Name}|#checked") is { } t && DateTime.TryParse(t, out var when) ? "checked " + Ago(when) : "not checked yet";
            var limited = Cli.RecentlyLimited(cfg) ? " · out of quota, skipped for now" : "";
            var label = new TextBlock { FontSize = 12.5 };
            label.Inlines.Add(new System.Windows.Documents.Run(cfg.Name));
            label.Inlines.Add(new System.Windows.Documents.Run($"   {at}{limited}") { FontSize = 12.5, Foreground = (Brush)FindResource("Dim") });
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var name = cfg.Name;
            var box = new CheckBox { IsChecked = !skipChecks.Contains(name), Content = label, Margin = new Thickness(0, 3, 0, 3), MinWidth = 0 };
            box.Click += (_, _) => { if (box.IsChecked == true) skipChecks.Remove(name); else skipChecks.Add(name); };
            ModelPicks.Children.Add(box);
        }
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours}h ago" : $"{(int)d.TotalDays}d ago";
    }

    // while it runs the button is Stop: the provider being checked stops where it is, the rest aren't checked
    CancellationTokenSource? scanStop;
    async void CheckModels_Click(object s, RoutedEventArgs e)
    {
        if (scanStop != null) { scanStop.Cancel(); CheckModelsButton.IsEnabled = false; CheckModelsText.Text = "Stopping…"; return; }
        var chosen = S.Providers.Where(p => !skipChecks.Contains(p.Name)).ToList();
        if (chosen.Count == 0) return;
        scanStop = new CancellationTokenSource();
        CheckModelsText.Text = "Stop";
        ModelResults.Children.Clear();
        await CheckAllModels(chosen, scanStop.Token);
        scanStop = null;
        CheckModelsButton.IsEnabled = true; CheckModelsText.Text = "Check models";
        DrawModelPicks();
    }

    async Task CheckAllModels(List<ProviderCfg> providers, CancellationToken ct)
    {
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var now = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap };
        now.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var lines = new StackPanel();
        var card = new Border { Style = (Style)FindResource("Group"), Child = new StackPanel { Children = { title, now, lines } } };
        ModelResults.Children.Insert(0, card);

        var all = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < providers.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var cfg = providers[i];
            title.Text = $"Checking models… {i + 1} of {providers.Count} providers";
            now.Text = cfg.Name;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string result;
            if (Cli.RecentlyLimited(cfg)) result = "skipped · out of quota recently";
            else
            {
                try { await CheckProvider(cfg, t => now.Text = $"{cfg.Name} · {t}", ct); result = Summary(cfg); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = "stopped part-way · models not reached keep their earlier results"; }
                catch (Exception ex) { result = "couldn't check · " + ex.Message; }
            }
            lines.Children.Add(ScanLine($"{cfg.Name}  ·  {result}  ·  {Took(clock.Elapsed)}"));
        }
        if (ct.IsCancellationRequested)
        {
            title.Text = $"Stopped after {Took(all.Elapsed)}";
            now.Text = $"Stopped at {lines.Children.Count} of {providers.Count} providers.";
            return;
        }
        title.Text = $"✓ All models updated in {Took(all.Elapsed)}";
        title.Foreground = Brushes.MediumSeaGreen;
        now.Text = $"{providers.Count} providers checked at {DateTime.Now:t}.";
    }

    // "12 of 40 work · 9 fast" for a provider, from its saved results
    string Summary(ProviderCfg cfg)
    {
        if (Cli.Of(cfg) is { } c && !Cli.CanVerify(c.Adapter))
            return S.ModelStatus.GetValueOrDefault($"{cfg.Name}|#ready") == "ok" ? "answers" : "not answering";
        var mine = S.ModelStatus.Where(kv => kv.Key.StartsWith(cfg.Name + "|") && !kv.Key.Contains("|#")).ToList();
        var ok = mine.Count(kv => kv.Value == "ok");
        var fast = mine.Count(kv => kv.Value == "ok" && Cli.Speed(cfg.Name, kv.Key[(cfg.Name.Length + 1)..]) == "fast");
        return $"{ok} of {mine.Count} work · {fast} fast";
    }

    static string Took(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0}s";


    void ShowScan(Detect.Result r, StackPanel into, bool agentsOnly)
    {
        into.Children.Clear();
        var known = S.Providers.Select(p => Store.Unprotect(p.Key)).Where(k => k != "").ToHashSet();
        var keys = r.Keys.Where(k => !known.Contains(k.Key)).ToList();
        var servers = r.Servers.Where(sv => S.Providers.All(p => !p.BaseUrl.Equals(sv.Url, StringComparison.OrdinalIgnoreCase))).ToList();
        var have = (JsonNode.Parse(S.McpServers) as JsonObject)?["mcpServers"] as JsonObject ?? JsonNode.Parse(S.McpServers) as JsonObject ?? [];
        var mine = Skills.All().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skills = r.Skills.Where(x => !mine.Contains(x.Name)).ToList();

        void Group(string title) => into.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Label") });
        void Row(string text, string detail, string action, Func<Task<string>> run)
        {
            var b = new Button { Content = action, Padding = new Thickness(12, 4, 12, 4), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Shimmer") };
            var info = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = detail, Margin = new Thickness(0, 2, 0, 0) };
            info.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                try { info.Text = await run(); info.Foreground = Brushes.MediumSeaGreen; b.Visibility = Visibility.Collapsed; }
                catch (Exception ex) { info.Text = "✕ " + ex.Message; info.Foreground = Brushes.IndianRed; b.IsEnabled = true; }
            };
            var name = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var row = new DockPanel();
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
            row.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 10, 0), Children = { name, info } });
            into.Children.Add(Card(row));
        }

        if (agentsOnly) { servers.Clear(); keys.Clear(); skills.Clear(); }
        if (servers.Count > 0)
        {
            Group("AI SERVERS RUNNING ON THIS PC");
            foreach (var sv in servers)
                Row($"{sv.Name} · {sv.Where}", $"{sv.Url} · {sv.Models} model{(sv.Models == 1 ? "" : "s")}", "Add", async () =>
                {
                    var name = sv.Preset ?? $"{sv.Name} (local)";
                    if (sv.Where == "WSL" && S.Providers.Any(p => p.Name == name)) name += " · WSL";
                    return await AddFound(new ProviderCfg { Name = name, Kind = "openai", BaseUrl = sv.Url, Key = "" });
                });
        }
        if (keys.Count > 0)
        {
            Group("API KEYS IN ENVIRONMENT VARIABLES");
            foreach (var k in keys)
            {
                var preset = Api.Presets.First(x => x.Name == k.Preset);
                Row($"{k.Preset} · {k.Where}", $"{k.Var} = ••••{k.Key[^Math.Min(4, k.Key.Length)..]}", "Add", () =>
                {
                    // a second, different key for the same provider is added beside the first, not over it
                    var name = S.Providers.Any(p => p.Name == k.Preset) ? $"{k.Preset} · {k.Where}" : k.Preset;
                    return AddFound(new ProviderCfg { Name = name, Kind = preset.Kind, BaseUrl = preset.BaseUrl, Key = Store.Protect(k.Key) });
                });
            }
        }
        foreach (var m in agentsOnly ? [] : r.Mcp)
        {
            var fresh = m.Servers.Where(kv => !have.ContainsKey(kv.Key)).ToList();
            if (fresh.Count == 0) continue;
            if (into.Children.OfType<TextBlock>().All(t => t.Text != "MCP SERVERS IN OTHER APPS")) Group("MCP SERVERS IN OTHER APPS");
            Row($"{m.App} · {m.Where}", string.Join(", ", fresh.Select(kv => kv.Key)), $"Import {fresh.Count}", () =>
            {
                var root = JsonNode.Parse(S.McpServers.Trim() == "" ? "{}" : S.McpServers) as JsonObject ?? [];
                var map = root["mcpServers"] as JsonObject ?? root;
                foreach (var (name, cfg) in fresh) if (!map.ContainsKey(name)) map[name] = cfg!.DeepClone();
                S.McpServers = root.ToJsonString(new() { WriteIndented = true });
                McpBox.Text = S.McpServers; SaveSettings(); StartMcp();
                return Task.FromResult($"Imported into Tools: {string.Join(", ", fresh.Select(kv => kv.Key))}. Connecting…");
            });
        }
        if (skills.Count > 0)
        {
            Group("CLAUDE SKILLS ON THIS PC");
            Row($"{skills.Count} skill{(skills.Count == 1 ? "" : "s")}", string.Join(", ", skills.Select(x => x.Name + (x.Where == "WSL" ? " (WSL)" : ""))), "Import all", () =>
            {
                foreach (var x in skills) Skills.Import(x.Dir);
                RefreshSkills();
                return Task.FromResult($"Added {skills.Count} skill{(skills.Count == 1 ? "" : "s")} to Settings → Skills.");
            });
        }
        // command-line tools: the ones Ivo can drive become providers; servers (ollama…) come in as AI servers above
        var usable = r.Clis.Where(c => Cli.Find(c.Name) != null).Select(c => (c.Name, Distro: c.Where == "WSL" ? r.WslDistro : null))
                           .Where(c => S.Providers.All(p => p.BaseUrl != Cli.Provider(c.Name, c.Distro).BaseUrl)).ToList();
        if (usable.Count > 0)
        {
            Group("AI COMMAND-LINE TOOLS");
            foreach (var (name, distro) in usable)
            {
                var cfg = Cli.Provider(name, distro);
                Row(cfg.Name, $"{name} · {(distro != null ? $"WSL ({distro})" : "Windows")} · chats with it through its own sign-in and plan", "Add", () => AddFound(cfg));
            }
        }
        if (r.WslNote != null && !agentsOnly) into.Children.Add(ScanLine(r.WslNote));
        if (into.Children.Count == 0) into.Children.Add(ScanLine(agentsOnly ? "No new agents found: the ones installed are already added, or none are installed." : "Nothing new found: what's on this PC is already in Ivo, or nothing AI-related is set up yet."));
    }

    TextBlock ScanLine(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    // Adds a found provider and checks which of its models work, like Add does.
    async Task<string> AddFound(ProviderCfg cfg)
    {
        S.Providers.RemoveAll(p => p.Name == cfg.Name);
        S.Providers.Add(cfg);
        if (S.Providers.Count == 1 || S.Providers.All(p => p.Name != S.Provider)) S.Provider = cfg.Name;
        ModelCache.Remove(cfg.Name);
        SaveSettings();
        var last = "";
        try { await CheckProvider(cfg, t => last = t); } catch (Exception ex) { return $"Added {cfg.Name}, but its models couldn't be checked: {ex.Message}"; }
        return $"Added {cfg.Name}. {last}";
    }

    // ---------- skills (Settings → Skills) ----------
    readonly HashSet<string> pickedSkills = new(StringComparer.OrdinalIgnoreCase);   // selected for a bulk action

    void RefreshSkills()
    {
        skillHashes.Clear();   // look at the skill files again (an edited skill is asked about)
        UpdateCounts();
        SkillsList.Children.Clear();
        var all = Skills.All();
        NoSkills.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        pickedSkills.IntersectWith(all.Select(x => x.Name));   // deleted ones drop out
        var q = SkillSearch.Text.Trim();   // the search box: name or description
        var shown = all.Where(x => q == "" || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        // the bulk bar: Select all (what's shown), and with a selection, Enable / Disable / Delete
        if (shown.Count > 0) SkillsList.Children.Add(SkillsBulkBar(shown));

        foreach (var sk in shown)
        {
            var pick = new CheckBox { IsChecked = pickedSkills.Contains(sk.Name), VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new Thickness(0, 0, 2, 0), ToolTip = "Select" };
            pick.Click += (_, _) => { if (pick.IsChecked == true) pickedSkills.Add(sk.Name); else pickedSkills.Remove(sk.Name); RefreshSkills(); };
            var on = SkillSwitch(!Skills.Disabled.Contains(sk.Name), now => SetSkills([sk.Name], now));
            var open = ChatPane.SmallButton("\uE8DA", "Open folder");
            open.Click += (_, _) => Process.Start("explorer.exe", $"\"{sk.Dir}\"");
            var remove = ChatPane.SmallButton("\uE74D", "Delete");
            remove.Click += async (_, _) =>
            {
                if (!await Confirm("Delete this skill?", $"“{sk.Name}” will be deleted from Ivo.", "Delete", danger: true)) return;
                DeleteSkills([sk]);
            };
            var name = new TextBlock { Text = sk.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var head = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { on, open, remove } };
            DockPanel.SetDock(buttons, Dock.Right); DockPanel.SetDock(pick, Dock.Left);
            head.Children.Add(buttons); head.Children.Add(pick); head.Children.Add(name);

            var desc = new TextBlock { Text = sk.Description == "" ? "No description" : sk.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var card = Card(new StackPanel { Children = { head, desc } });
            if (Skills.Disabled.Contains(sk.Name)) card.Opacity = 0.55;   // off: dimmed
            SkillsList.Children.Add(card);
        }
    }

    FrameworkElement SkillsBulkBar(List<Skill> shown)
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var everyone = shown.All(x => pickedSkills.Contains(x.Name));
        var all = new CheckBox { IsChecked = everyone, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new Thickness(0, 0, 8, 0),
                                 Content = new TextBlock { Text = "Select all", FontSize = 12 } };
        ((TextBlock)all.Content).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        all.Click += (_, _) => { if (everyone) pickedSkills.ExceptWith(shown.Select(x => x.Name)); else pickedSkills.UnionWith(shown.Select(x => x.Name)); RefreshSkills(); };
        DockPanel.SetDock(all, Dock.Left); bar.Children.Add(all);
        if (pickedSkills.Count > 0)
        {
            var picked = Skills.All().Where(x => pickedSkills.Contains(x.Name)).ToList();
            Button Act(string text, Action run, bool danger = false)
            {
                var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), FontSize = 12 };
                if (danger) b.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                b.Click += (_, _) => run();
                return b;
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children =
            {
                Act("Enable", () => SetSkills(picked.Select(x => x.Name), true)),
                Act("Disable", () => SetSkills(picked.Select(x => x.Name), false)),
                Act("Delete", async () =>
                {
                    if (!await Confirm($"Delete {picked.Count} skill{(picked.Count == 1 ? "" : "s")}?", $"{(picked.Count == 1 ? "It" : "They")} will be deleted from Ivo.", "Delete", danger: true)) return;
                    DeleteSkills(picked);
                }, danger: true),
                Act("Clear", () => { pickedSkills.Clear(); RefreshSkills(); }),
            } };
            DockPanel.SetDock(actions, Dock.Right); bar.Children.Add(actions);
            bar.Children.Add(DimText($"{picked.Count} selected", 12));
        }
        return bar;
    }

    void SetSkills(IEnumerable<string> names, bool on)
    {
        foreach (var n in names) if (on) Skills.Disabled.Remove(n); else Skills.Disabled.Add(n);
        S.DisabledSkills = [.. Skills.Disabled]; SaveSettings();
        RefreshSkills();
    }

    void DeleteSkills(IEnumerable<Skill> skills)
    {
        var failed = new List<string>();
        foreach (var sk in skills)
        {
            try { System.IO.Directory.Delete(sk.Dir, recursive: true); pickedSkills.Remove(sk.Name); }
            catch { failed.Add(sk.Name); }
        }
        if (failed.Count > 0) _ = Notice("Couldn't delete some", string.Join(", ", failed));
        RefreshSkills();
    }

    // a small On / Off switch: green when the skill is used
    Button SkillSwitch(bool on, Action<bool> changed)
    {
        var b = new Button { Content = on ? "On" : "Off", FontSize = 12, Padding = new Thickness(10, 1, 10, 2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                             Cursor = Cursors.Hand, ToolTip = on ? "In use · click to turn off" : "Off · click to turn on" };
        b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<Border CornerRadius='9' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' /></Border></ControlTemplate>");
        if (on) { b.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x3F, 0xB9, 0x50)); b.Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)); }
        else { b.SetResourceReference(BackgroundProperty, "Glass"); b.SetResourceReference(ForegroundProperty, "Dim"); }
        b.Click += (_, _) => changed(!on);
        return b;
    }

    // The composer's Skills button: installed skills to use, then the way to all of them.
    public void ShowSkillsMenu(FrameworkElement from)
    {
        var mine = Skills.Enabled();
        MenuItems.Children.Clear();
        MenuCard.MinWidth = 300;

        FrameworkElement Item(string icon, string text, string? sub, Action run)
        {
            var glyph = new TextBlock { Text = icon, FontFamily = App.IconFont, FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Fg"); name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 14, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand, MaxWidth = 380,
                                   Child = new DockPanel { Children = { glyph, new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { name } } } } };
            if (sub != "" && sub != null) { var d = DimText(sub, 11); ((StackPanel)((DockPanel)row.Child).Children[1]).Children.Add(d); row.ToolTip = sub; }
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Glass");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { CloseMenu(); run(); };
            return row;
        }

        // search: filters by name and description as you type
        var search = new TextBox { FontSize = 13, Padding = new Thickness(28, 6, 8, 6), Margin = new Thickness(4, 2, 4, 6) };
        var hint = DimText("Search skills", 13); hint.IsHitTestVisible = false; hint.Margin = new Thickness(33, 0, 0, 4);
        var lens = new TextBlock { Text = "\uE721", FontFamily = App.IconFont, FontSize = 14, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(13, 0, 0, 4) };
        lens.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Fill()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            var shown = mine.Where(sk => q == "" || sk.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || sk.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var sk in shown) { var n = sk.Name; list.Children.Add(Item("\uE82D", n, sk.Description, () => active.UseSkill(n))); }
            if (mine.Count == 0) list.Children.Add(Item("\uE946", "No skills installed yet", null, () => OpenSkills(browse: true)));
            else if (shown.Count == 0) { var none = DimText("No skill matches.", 12); none.Margin = new Thickness(12, 6, 0, 6); list.Children.Add(none); }
        }
        search.TextChanged += (_, _) => { hint.Visibility = search.Text == "" ? Visibility.Visible : Visibility.Collapsed; Fill(); };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseMenu(); e.Handled = true; }
            else if (e.Key == Key.Enter && list.Children.OfType<Border>().FirstOrDefault() is { } first)   // Enter: the first match
            { e.Handled = true; first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); }
        };
        Fill();
        MenuItems.Children.Add(new Grid { Children = { search, lens, hint } });
        MenuItems.Children.Add(scroll);
        var line = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5) };
        line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        MenuItems.Children.Add(line);
        MenuItems.Children.Add(Item("\uE719", "Browse all skills…", null, () => OpenSkills(browse: true)));
        MenuItems.Children.Add(Item("\uE713", "Manage installed skills…", null, () => OpenSkills(browse: false)));

        PlaceMenu(from.TranslatePoint(new Point(0, 0), this), above: true);   // opens upward from the button
        Dispatcher.BeginInvoke(() => search.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void OpenSkills(bool browse)
    {
        OpenSettings("skills");
        (browse ? SkillsMarket : SkillsMine).IsChecked = true;
        SkillView_Checked(this, new RoutedEventArgs());
    }

    // ---------- skills from GitHub (Skills → Browse) ----------
    List<RemoteSkill>? catalog;
    List<string> catalogErrors = [];

    void SkillView_Checked(object s, RoutedEventArgs e)
    {
        if (SkillsBrowse == null) return;
        var browse = SkillsMarket.IsChecked == true;
        SkillsBrowse.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        SkillsInstalled.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
        if (browse) { ShowSources(); LoadCatalog(); } else RefreshSkills();
    }

    // Built-in sources first (in priority order), then the user's own.
    List<string> Sources => [.. Skills.BuiltInSources.Where(x => !S.HiddenSources.Contains(x)), .. S.SkillSources];

    const string AllSources = "All sources";

    // The source dropdown: all sources together, or one of them (keeps the current pick when it still exists).
    void ShowSources()
    {
        var was = SourcePick.SelectedItem as string;
        SourcePick.ItemsSource = new[] { AllSources }.Concat(Sources).ToList();
        SourcePick.SelectedItem = was != null && Sources.Contains(was) ? was : AllSources;
        var hidden = S.HiddenSources.Count;
        SourceRestore.Content = $"Restore {hidden} hidden built-in source{(hidden == 1 ? "" : "s")}";
        SourceRestore.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    string? PickedSource => SourcePick.SelectedItem is string x && x != AllSources ? x : null;

    void SourcePick_Changed(object s, SelectionChangedEventArgs e)
    {
        var src = PickedSource;
        SourceRemove.IsEnabled = src != null;
        SourceRemove.ToolTip = src == null ? "Pick a source first" : Skills.BuiltInSources.Contains(src) ? "Hide source" : "Remove source";
        if (catalog != null) ShowCatalog();
    }

    void SourceRemove_Click(object s, RoutedEventArgs e)
    {
        if (PickedSource is not { } src) return;
        if (Skills.BuiltInSources.Contains(src)) S.HiddenSources.Add(src); else S.SkillSources.Remove(src);
        SaveSettings(); ShowSources(); LoadCatalog(force: true);
    }

    void SourceRestore_Click(object s, RoutedEventArgs e) { S.HiddenSources.Clear(); SaveSettings(); ShowSources(); LoadCatalog(force: true); }

    // Does this skill come from that source? (A source can be a whole repo or one folder of it.)
    static bool From(RemoteSkill r, string source)
    {
        var (repo, _, sub) = Skills.ParseSource(source);
        return r.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) && (sub == "" || r.Path == sub || r.Path.StartsWith(sub + "/"));
    }

    async void SourceAdd_Click(object s, RoutedEventArgs e)
    {
        var text = SourceBox.Text.Trim();
        if (text == "") return;
        try { Skills.ParseSource(text); }
        catch (Exception ex) { await Notice("That isn't a GitHub source", ex.Message); return; }
        if (Skills.BuiltInSources.Contains(text, StringComparer.OrdinalIgnoreCase)) S.HiddenSources.RemoveAll(x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
        else if (!S.SkillSources.Contains(text, StringComparer.OrdinalIgnoreCase)) S.SkillSources.Add(text);
        SaveSettings(); SourceBox.Text = "";
        ShowSources(); LoadCatalog(force: true);
    }
    void SourceBox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; SourceAdd_Click(s, e); } }
    bool refreshing;   // only the Reload button asks GitHub again before a day has passed
    void MarketReload_Click(object s, RoutedEventArgs e) { refreshing = true; LoadCatalog(force: true); refreshing = false; }
    void MarketSearch_Changed(object s, TextChangedEventArgs e) { if (catalog != null) ShowCatalog(); }

    // One GitHub call per source (GitHub allows 60 an hour without signing in), kept for the session; each source's
    // skills appear as soon as it's loaded. A skill name shows once, from the highest-priority source that has it.
    int catalogPending;
    List<RemoteSkill> catalogAll = [];
    async void LoadCatalog(bool force = false)
    {
        if (catalog != null && !force) { ShowCatalog(); return; }
        var sources = Sources;
        var lists = new List<RemoteSkill>?[sources.Count];
        var errors = new List<string>();
        catalog = []; catalogAll = []; catalogErrors = errors; catalogPending = sources.Count;
        MarketList.Children.Clear();
        ShowCatalog();
        await Task.WhenAll(sources.Select(async (src, i) =>
        {
            try { lists[i] = await Skills.Browse(src, refresh: force && refreshing); }
            catch (Exception ex) { errors.Add($"{src}: {ex.Message}"); }
            catalogPending--;
            var all = lists.OfType<List<RemoteSkill>>().SelectMany(l => l).ToList();   // still in priority order
            catalogAll = all;
            catalog = all.GroupBy(r => r.Name.Trim().ToLowerInvariant()).Select(g => g.First()).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ShowCatalog();
        }));
    }

    void ShowCatalog()
    {
        MarketList.Children.Clear();
        var q = MarketSearch.Text.Trim();
        var src = PickedSource;
        var pool = src == null ? catalog! : catalogAll.Where(r => From(r, src)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();   // one source: all of its skills
        var shown = pool.Where(r => q == "" || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var installed = Skills.All();
        // only what needs attention: nothing to browse, still loading, or nothing could be loaded
        MarketStatus.Text = Sources.Count == 0 ? "Add a source to browse its skills."
                          : catalogPending > 0 ? "Loading skills…"
                          : catalog!.Count == 0 && catalogErrors.Count > 0 ? "Couldn't reach GitHub right now. Try again in a few minutes." : "";
        MarketStatus.ToolTip = catalogErrors.Count > 0 ? string.Join("\n", catalogErrors) : null;
        foreach (var rs in shown.Take(200))
        {
            var have = installed.FirstOrDefault(i => i.Source == rs.Id) ?? installed.FirstOrDefault(i => i.Name.Equals(rs.Name, StringComparison.OrdinalIgnoreCase));
            var button = new Button { Padding = new Thickness(12, 5, 12, 5), FontSize = 12, VerticalAlignment = VerticalAlignment.Top,
                                      Content = have == null ? "Install" : "Update", ToolTip = have == null ? "Install" : "Update" };
            if (have == null) button.Style = (Style)FindResource("Shimmer");
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false; button.Content = "Installing…";
                try { var n = await Skills.Install(rs); RefreshSkills(); MarketStatus.Text = $"✓ {(have == null ? "Installed" : "Updated")} “{n}”."; }
                catch (Exception ex) { await Notice("Couldn't install the skill", ex.Message); }
                ShowCatalog();
            };
            var name = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            name.Inlines.Add(new System.Windows.Documents.Run(rs.Name));
            if (have != null) name.Inlines.Add(new System.Windows.Documents.Run("  ✓ installed") { FontWeight = FontWeights.Normal, FontSize = 12, Foreground = Brushes.MediumSeaGreen });
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var from = new TextBlock { Text = $"{rs.Repo} · {rs.Files.Count} file{(rs.Files.Count == 1 ? "" : "s")}", FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            from.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var desc = new TextBlock { Text = rs.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 54, TextTrimming = TextTrimming.WordEllipsis, ToolTip = rs.Description };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var head = new DockPanel();
            DockPanel.SetDock(button, Dock.Right);
            head.Children.Add(button);
            head.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 10, 0), Children = { name, from } });
            MarketList.Children.Add(Card(new StackPanel { Children = { head, desc } }));
        }
    }

    async void SkillImport(string path)
    {
        try { var n = Skills.Import(path); RefreshSkills(); SkillStatus.Text = $"Added “{n}”."; }
        catch (Exception ex) { await Notice("Couldn't add that skill", ex.Message); }
    }
    void SkillFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a skill folder (it has a SKILL.md)" };
        if (dlg.ShowDialog(this) == true) SkillImport(dlg.FolderName);
    }
    void SkillZip_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick a skill archive", Filter = "Skill archives (*.zip;*.skill)|*.zip;*.skill" };
        if (dlg.ShowDialog(this) == true) SkillImport(dlg.FileName);
    }
    void SkillNew_Click(object s, RoutedEventArgs e)
    {
        var dir = Skills.Create();
        RefreshSkills();
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(dir, "SKILL.md")) { UseShellExecute = true });
    }
    // ---------- Import from this PC: one card per provider you've added, with the skills that tool has here ----------
    bool importOpen = true;                         // the Import from this PC section, folded or open
    readonly HashSet<string> openImports = [];      // the provider cards opened in it

    void SkillsImportPc_Click(object s, RoutedEventArgs e)
    {
        if (ImportPcPanel.Children.Count > 0) { ImportPcPanel.Children.Clear(); return; }   // a second click hides it again
        importOpen = true;
        DrawImportPc();
    }

    void DrawImportPc()
    {
        ImportPcPanel.Children.Clear();
        var have = Skills.All().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // the heading folds the whole section away and opens it again
        var chevron = new TextBlock { Text = importOpen ? "" : "", FontFamily = App.IconFont, FontSize = 12, Width = 16, VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 14, 0, 8),
                                       Children = { chevron, new TextBlock { Text = "IMPORT FROM THIS PC", Style = (Style)FindResource("Label"), Margin = new Thickness(0) } } };
        heading.MouseLeftButtonUp += (_, _) => { importOpen = !importOpen; DrawImportPc(); };
        ImportPcPanel.Children.Add(heading);
        if (!importOpen) return;
        if (S.Providers.Count == 0) { ImportPcPanel.Children.Add(DimText("Add a provider first.", 12)); return; }
        foreach (var cfg in S.Providers)
        {
            // which tool's skills: the command-line tools that keep skills on this PC
            var tool = Cli.Of(cfg)?.Adapter.Command switch { "claude" => "claude", "gemini" => "gemini", "copilot" => "copilot", _ => null };
            var found = tool != null && Cli.Of(cfg)!.Value.Distro == null ? Skills.OnPc(tool) : [];
            var body = new StackPanel();
            var title = new TextBlock { Text = cfg.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var fresh = found.Where(x => !have.Contains(x.Name)).ToList();
            if (fresh.Count > 1)
            {
                var all = new Button { Content = $"Import all {fresh.Count}", Padding = new Thickness(10, 4, 10, 4) };
                all.Click += (_, _) => { foreach (var x in fresh) Import(x, quiet: true); SkillStatus.Text = $"Imported {fresh.Count} skills from {cfg.Name}."; RefreshSkills(); DrawImportPc(); };
                DockPanel.SetDock(all, Dock.Right); head.Children.Add(all);
            }
            // a card folds to one line: its name and how many skills it has (click to open the list)
            var open = openImports.Contains(cfg.Name);
            var fold = new TextBlock { Text = open ? "" : "", FontFamily = App.IconFont, FontSize = 12, Width = 16, VerticalAlignment = VerticalAlignment.Center };
            fold.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var count = DimText(found.Count == 0 ? "" : $"  ·  {found.Count} skill{(found.Count == 1 ? "" : "s")}" + (fresh.Count > 0 ? $" · {fresh.Count} not imported" : " · all imported"), 12);
            DockPanel.SetDock(fold, Dock.Left); DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(fold); head.Children.Add(title); head.Children.Add(count);
            head.Background = Brushes.Transparent; head.Cursor = Cursors.Hand;
            head.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;   // Import all
                if (!openImports.Remove(cfg.Name)) openImports.Add(cfg.Name);
                DrawImportPc();
            };
            body.Children.Add(head);
            if (!open) { head.Margin = new Thickness(0); ImportPcPanel.Children.Add(Card(body)); continue; }
            if (found.Count == 0)
                body.Children.Add(DimText(tool == null ? (Cli.Of(cfg) != null ? "This tool keeps no skills Ivo can import." : "API keys and gateways have no skills of their own.")
                                                       : "No skills of its own on this PC.", 12));
            foreach (var sk in found)
            {
                var name = new TextBlock { Text = sk.Name, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                FrameworkElement action;
                if (have.Contains(sk.Name)) action = DimText("Imported ✓", 11.5);
                else
                {
                    var import = new Button { Content = "Import", Padding = new Thickness(10, 3, 10, 3) };
                    var one = sk;
                    import.Click += (_, _) => { Import(one, quiet: false); RefreshSkills(); DrawImportPc(); };
                    action = import;
                }
                DockPanel.SetDock(action, Dock.Right); line.Children.Add(action);
                var text = new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { name, DimText("  ·  " + sk.Origin, 11) } } } };
                if (sk.Description != "") { var d = DimText(sk.Description, 11); d.TextWrapping = TextWrapping.Wrap; d.MaxHeight = 32; d.Margin = new Thickness(0, 2, 12, 0); text.Children.Add(d); }
                line.Children.Add(text);
                body.Children.Add(line);
            }
            ImportPcPanel.Children.Add(Card(body));
        }
    }

    // copies another tool's skill into Ivo's own skills (that tool's copy stays as it was)
    void Import(Skill sk, bool quiet)
    {
        try { var name = Skills.Import(sk.Dir); if (!quiet) SkillStatus.Text = $"Imported “{name}”."; }
        catch (Exception ex) { _ = Notice("Couldn't import it", ex.Message); }
    }

    void SkillSearch_Changed(object s, TextChangedEventArgs e)
    {
        SkillSearchHint.Visibility = SkillSearch.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        RefreshSkills();
    }

    void SkillsOpen_Click(object s, RoutedEventArgs e) { System.IO.Directory.CreateDirectory(Skills.Root); Process.Start("explorer.exe", $"\"{Skills.Root}\""); }
    void SkillsRefresh_Click(object s, RoutedEventArgs e) { RefreshSkills(); SkillStatus.Text = ""; }

    // ---------- MCP servers (Settings → Tools) ----------
    async void StartMcp()
    {
        McpError.Text = "";
        try { await Mcp.Start(S.McpServers, RefreshMcp); }
        catch (Exception ex) { McpError.Text = "Couldn't read the JSON: " + ex.Message; }
        RefreshMcp();
    }

    void McpSave_Click(object s, RoutedEventArgs e)
    {
        try { if (JsonNode.Parse(McpBox.Text.Trim() == "" ? "{}" : McpBox.Text) is not JsonObject) throw new Exception("it must be a JSON object { … }."); }
        catch (Exception ex) { McpError.Text = "That isn't valid JSON: " + ex.Message; return; }
        S.McpServers = McpBox.Text;
        SaveSettings();
        StartMcp();
    }

    void McpExample_Click(object s, RoutedEventArgs e)
    {
        var docs = System.Text.Json.JsonSerializer.Serialize(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        McpBox.Text = $$"""
            {
              "mcpServers": {
                "filesystem": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-filesystem", {{docs}}]
                }
              }
            }
            """;
    }

    void RefreshMcp()
    {
        UpdateCounts();
        McpList.Children.Clear();
        NoMcp.Visibility = Mcp.Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var srv in Mcp.Servers)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                                    Fill = srv.Ready ? Brushes.MediumSeaGreen : srv.Status.StartsWith("✕") ? Brushes.IndianRed : Brushes.Gray };
            var name = new TextBlock { Text = srv.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var status = new TextBlock { Text = srv.Status, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 4, 0, 0) };
            status.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            if (srv.Tools.Count > 0) status.ToolTip = string.Join("\n", srv.Tools.Select(t => t.Name));
            McpList.Children.Add(Card(new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, name } }, status } }));
        }
    }

    Border Card(UIElement child)
    {
        var card = new Border { Child = child, Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Glass");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        return card;
    }
}
