using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ivo;

// The tour, the usage dashboard, plugins in Settings, and the Ollama model manager.
public partial class MainWindow
{
    // tour: see MainWindow.Tour
    void Tour_Click(object s, RoutedEventArgs e) { SettingsPanel.Visibility = Visibility.Collapsed; StartTour(); }

    // ---------- accessibility ----------
    // F6 moves between the main areas: chat search, the message box, and Settings' search while it's open
    void CycleRegions()
    {
        UIElement[] order = SettingsPanel.IsVisible ? [SettingsSearch, ChatSearch] : [ChatSearch];
        var focused = Keyboard.FocusedElement as DependencyObject;
        var at = Array.FindIndex(order, el => focused != null && (el == focused || el.IsAncestorOf(focused)));
        if (at < order.Length - 1) order[at + 1].Focus();
        else active.FocusInput();
    }

    // ---------- usage dashboard (Settings → Usage) ----------
    void ShowUsage()
    {
        var rows = allChats.SelectMany(c => c.Messages.Where(m => m.Usage != null).Select(m => (At: m.At ?? c.Updated, U: m.Usage!, Prov: m.Usage!.Provider ?? c.Provider ?? "?", Model: c.Model ?? "?"))).ToList();
        var month = rows.Where(r => r.At >= new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1)).ToList();
        string Money(double v) => v == 0 ? "$0" : v < 0.01 ? "<$0.01" : $"${v:0.00}";
        UsageSummary.Text = rows.Count == 0 ? "No usage recorded yet."
            : $"This month: {month.Sum(r => r.U.In + r.U.Out):N0} tokens · ~{Money(month.Sum(r => r.U.Cost ?? 0))}     All time: {rows.Sum(r => r.U.In + r.U.Out):N0} tokens · ~{Money(rows.Sum(r => r.U.Cost ?? 0))}";

        // the last 30 days, one bar a day (estimated $; tokens when nothing was priced)
        UsageChart.Children.Clear(); UsageChart.ColumnDefinitions.Clear();
        var days = Enumerable.Range(0, 30).Select(i => DateTime.Today.AddDays(i - 29)).ToList();
        // ponytail: tokens, not cost — most use is on free models, so a cost chart showed one or two bars and nothing else
        var tokens = days.Select(d => rows.Where(r => r.At.Date == d).Sum(r => (double)(r.U.In + r.U.Out))).ToList();
        var cost = days.Select(d => rows.Where(r => r.At.Date == d).Sum(r => r.U.Cost ?? 0)).ToList();
        var top = Math.Max(tokens.Max(), 1);
        UsageChart.RowDefinitions.Clear();
        UsageChart.RowDefinitions.Add(new RowDefinition());
        UsageChart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < days.Count; i++)
        {
            UsageChart.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = new Rectangle { Height = tokens[i] > 0 ? Math.Max(4, 92 * tokens[i] / top) : 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0),
                                      RadiusX = 3, RadiusY = 3, ToolTip = $"{days[i]:ddd d MMM}: {tokens[i]:N0} tokens{(cost[i] > 0 ? $" · ~{Money(cost[i])}" : "")}" };
            if (tokens[i] > 0) bar.Fill = (Brush)FindResource("AccentGrad"); else bar.SetResourceReference(Shape.FillProperty, "Separator");
            Grid.SetColumn(bar, i); UsageChart.Children.Add(bar);
        }
        foreach (var (col, text, align) in new[] { (0, days[0].ToString("d MMM"), HorizontalAlignment.Left), (15, days[15].ToString("d MMM"), HorizontalAlignment.Center), (29, "Today", HorizontalAlignment.Right) })
        {
            var t = new TextBlock { Text = text, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = align };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            Grid.SetRow(t, 1); Grid.SetColumn(t, col); Grid.SetColumnSpan(t, col == 0 || col == 29 ? 1 : 1);
            if (col == 0) Grid.SetColumnSpan(t, 6); else if (col == 29) { Grid.SetColumn(t, 24); Grid.SetColumnSpan(t, 6); } else { Grid.SetColumn(t, 12); Grid.SetColumnSpan(t, 6); }
            UsageChart.Children.Add(t);
        }
        UsageChartLabel.Text = $"Last 30 days · tokens per day (busiest: {top:N0}) · hover a bar for its cost";

        // this month by provider and model
        UsageTable.Children.Clear();
        foreach (var g in month.GroupBy(r => (r.Prov, r.Model)).OrderByDescending(g => g.Sum(r => r.U.Cost ?? 0)).ThenByDescending(g => g.Sum(r => r.U.In + r.U.Out)).Take(25))
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            TextBlock Cell(string t, int col, bool dim = false)
            {
                var tb = new TextBlock { Text = t, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = col > 0 ? TextAlignment.Right : TextAlignment.Left };
                tb.SetResourceReference(TextBlock.ForegroundProperty, dim ? "Dim" : "Fg"); Grid.SetColumn(tb, col); row.Children.Add(tb); return tb;
            }
            Cell($"{g.Key.Prov} · {g.Key.Model}", 0); Cell($"{g.Sum(r => r.U.In + r.U.Out):N0} tok", 1, true); Cell(Money(g.Sum(r => r.U.Cost ?? 0)), 2);
            UsageTable.Children.Add(row);
        }
        if (UsageTable.Children.Count == 0) UsageTable.Children.Add(new TextBlock { Text = "Nothing this month yet.", Foreground = (Brush)FindResource("Dim") });
        UsageNote.Text = "Costs are estimates from public prices; agents' and daily tasks' use isn't saved in chats, so it isn't included.";
    }

    // ---------- plugins (Settings → Tools) ----------
    void ShowPlugins()
    {
        PluginList.Children.Clear();
        var all = Plugins.All();
        if (all.Count == 0) PluginList.Children.Add(new TextBlock { Text = "None yet. Each plugin is a folder with a plugin.json; see the README in the plugins folder.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Dim"), FontSize = 12 });
        foreach (var p in all)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            if (p.Panel != null)
            {
                var open = new Button { Content = "Open panel", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
                open.Click += (_, _) => OpenPluginPanel(p);
                DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            }
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            t.Inlines.Add(new System.Windows.Documents.Run(p.Name) { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(p.Error != null ? "  ·  " + p.Error : $"  ·  {p.Tools.Count} tool{(p.Tools.Count == 1 ? "" : "s")}{(p.Description != "" ? " · " + p.Description : "")}")
                          { Foreground = p.Error != null ? Brushes.IndianRed : (Brush)FindResource("Dim") });
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            row.Children.Add(t);
            PluginList.Children.Add(row);
        }
    }
    void OpenPlugins_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Plugins.Dir);
        var readme = System.IO.Path.Combine(Plugins.Dir, "README.md");
        if (!File.Exists(readme)) File.WriteAllText(readme, """
            # Ivo plugins

            Each plugin is a folder here with a `plugin.json`:

            ```json
            {
              "name": "Hello",
              "description": "An example",
              "tools": [
                { "name": "hello", "description": "Says hello to someone", "command": "powershell -NoProfile -File hello.ps1",
                  "schema": { "type": "object", "properties": { "who": { "type": "string" } } }, "readOnly": true }
              ],
              "panel": "panel.html"
            }
            ```

            - A tool's `command` runs in the plugin's folder. Its arguments arrive as JSON on standard input; whatever it
              prints on standard output goes back to the model. Exit code other than 0 counts as an error.
            - Tools that aren't `readOnly` ask before running, like Ivo's own.
            - `panel` (optional) is an HTML page, opened from Settings → Tools → Plugins.
            """);
        System.Diagnostics.Process.Start("explorer.exe", $"\"{Plugins.Dir}\"");
    }
    void ReloadPlugins_Click(object s, RoutedEventArgs e) => ShowPlugins();
    void OpenPluginPanel(Plugins.Plugin p)
    {
        var web = new Microsoft.Web.WebView2.Wpf.WebView2 { Source = new Uri(p.Panel!) };
        var w = new Window { Title = p.Name, Width = 720, Height = 560, Owner = this, Content = web, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        w.Closed += (_, _) => web.Dispose();
        IvoFrame.Apply(w, web: true);
        w.Show();
    }

    // ---------- Ollama: the models on this PC (Settings → API keys) ----------
    static readonly HttpClient ollamaHttp = new(Net.Handler()) { Timeout = TimeSpan.FromMinutes(60) };
    string OllamaBase => S.Providers.FirstOrDefault(p => p.BaseUrl.Contains(":11434")) is { } p && Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u)
        ? $"{u.Scheme}://{u.Authority}" : "http://localhost:11434";

    async void ShowOllama()
    {
        OllamaList.Children.Clear();
        try
        {
            var j = JsonNode.Parse(await ollamaHttp.GetStringAsync(OllamaBase + "/api/tags"));
            var models = j?["models"]?.AsArray() ?? [];
            OllamaStatus.Text = models.Count == 0 ? "Ollama is running, with no models yet. Pull one below." : $"{models.Count} model{(models.Count == 1 ? "" : "s")} at {OllamaBase}";
            foreach (var m in models)
            {
                var name = (string?)m!["name"] ?? "";
                var size = (long?)m["size"] ?? 0;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var del = new Button { Content = "Delete", Padding = new Thickness(10, 3, 10, 3), Style = (Style)FindResource("Ghost") };
                del.Click += async (_, _) =>
                {
                    if (!await Confirm($"Delete {name}?", "It's removed from this PC; you can pull it again later.", "Delete", danger: true)) return;
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Delete, OllamaBase + "/api/delete") { Content = new StringContent(new JsonObject { ["model"] = name }.ToJsonString()) };
                        (await ollamaHttp.SendAsync(req)).EnsureSuccessStatusCode();
                    }
                    catch (Exception ex) { _ = Notice("Couldn't delete the model", ex.Message); }
                    ShowOllama();
                };
                DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);
                var t = new TextBlock { Text = $"{name}  ·  {size / 1e9:0.0} GB", VerticalAlignment = VerticalAlignment.Center };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                row.Children.Add(t);
                OllamaList.Children.Add(row);
            }
        }
        catch { OllamaStatus.Text = $"Ollama isn't running at {OllamaBase}. Install it from ollama.com, then come back."; }
    }

    async void OllamaPull_Click(object s, RoutedEventArgs e)
    {
        var name = OllamaPullBox.Text.Trim();
        if (name == "") { OllamaStatus.Text = "Type a model name, e.g. llama3.2 or qwen2.5-coder:7b (see ollama.com/library)."; return; }
        OllamaPullBtn.IsEnabled = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, OllamaBase + "/api/pull") { Content = new StringContent(new JsonObject { ["model"] = name, ["stream"] = true }.ToJsonString()) };
            using var res = await ollamaHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            res.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
            while (await reader.ReadLineAsync() is { } line)
            {
                var j = JsonNode.Parse(line);
                if ((string?)j?["error"] is { } err) throw new InvalidOperationException(err);
                var status = (string?)j?["status"] ?? "";
                OllamaStatus.Text = j?["total"] is { } total && (long)total! > 0 ? $"{name}: {status} {(double)((long?)j["completed"] ?? 0) / (long)total!:P0}" : $"{name}: {status}";
            }
            OllamaPullBox.Clear();
            ShowOllama();
        }
        catch (Exception ex) { OllamaStatus.Text = "Couldn't pull it: " + ex.Message; Store.Fail("Ollama pull", ex.Message); }
        finally { OllamaPullBtn.IsEnabled = true; }
    }
}
