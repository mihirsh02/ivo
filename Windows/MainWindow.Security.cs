using System.IO;
using System.Text.Json.Nodes;
using System.Windows;

namespace Ivo;

// MCP servers and skills run with your rights / steer the model, so each is allowed once and asked about again if it changes.
public partial class MainWindow
{
    // once per skill per session (Settings → Skills → Refresh, or a restart, looks again): the model list asks often
    readonly Dictionary<string, string> skillHashes = [];

    string SkillHash(Skill s)
    {
        if (!skillHashes.TryGetValue(s.Dir, out var h)) skillHashes[s.Dir] = h = Skills.Fingerprint(s);
        return h;
    }
    string SkillKey(Skill s) => $"skill|{s.Name}|{SkillHash(s)}";

    void Redact_Changed(object s, RoutedEventArgs e) { S.RedactSecrets = RedactBox.IsChecked == true; SaveSettings(); }
    void MemoryReview_Changed(object s, RoutedEventArgs e) { S.MemoryReview = MemoryReviewBox.IsChecked == true; SaveSettings(); }

    // Settings → Permissions → Activity log: newest first, in a panel like Starred
    void ShowActivity_Click(object s, RoutedEventArgs e)
    {
        var lines = Store.ReadAudit();
        var layer = new System.Windows.Controls.Grid { Margin = new Thickness(0, 44, 0, 0) };
        System.Windows.Controls.Grid.SetRowSpan(layer, 99); System.Windows.Controls.Grid.SetColumnSpan(layer, 99); System.Windows.Controls.Panel.SetZIndex(layer, 950);
        var backdrop = new System.Windows.Controls.Border(); backdrop.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Backdrop");
        backdrop.MouseLeftButtonDown += (_, _) => Root.Children.Remove(layer);
        layer.Children.Add(backdrop);
        var list = new System.Windows.Controls.StackPanel { Margin = new Thickness(22, 0, 22, 22) };
        if (lines.Count == 0) list.Children.Add(Line("Nothing yet: agents haven't changed anything.", 13, "Dim"));
        foreach (var l in lines.Take(500))
        {
            var t = new System.Windows.Controls.TextBlock { Text = l, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas") };
            t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Fg");
            var row = new System.Windows.Controls.Border { Child = t, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8, 8, 8, 8.01) };
            row.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BoxCard");
            list.Children.Add(row);
        }
        var close = new System.Windows.Controls.Button { Content = "", FontFamily = App.IconFont, FontSize = 14, Style = (Style)FindResource("Ghost"), Width = 34, Height = 34, ToolTip = "Close (Esc)" };
        close.Click += (_, _) => Root.Children.Remove(layer);
        var title = Line($"Activity log · {lines.Count} entries", 15, "Fg"); title.TextAlignment = TextAlignment.Left; title.Margin = new Thickness(0);
        var head = new System.Windows.Controls.DockPanel { Margin = new Thickness(22, 14, 12, 12) };
        System.Windows.Controls.DockPanel.SetDock(close, System.Windows.Controls.Dock.Right); head.Children.Add(close); head.Children.Add(title);
        var body = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(head, System.Windows.Controls.Dock.Top); body.Children.Add(head);
        body.Children.Add(new System.Windows.Controls.ScrollViewer { Content = list, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto });
        var card = new System.Windows.Controls.Border { Child = body, Width = Math.Min(860, ActualWidth - 80), MaxHeight = Math.Max(300, ActualHeight - 140), CornerRadius = new CornerRadius(20), ClipToBounds = true };
        card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BoxBg");
        layer.Children.Add(card);
        layer.Focusable = true;
        layer.KeyDown += (_, k) => { if (k.Key == System.Windows.Input.Key.Escape) { Root.Children.Remove(layer); k.Handled = true; } };
        Root.Children.Add(layer); layer.Focus();
    }

    void SetupTrust()
    {
        RedactBox.IsChecked = S.RedactSecrets;
        MemoryReviewBox.IsChecked = S.MemoryReview;
        Skills.IsTrusted = s => S.Trusted.Contains(SkillKey(s));
        Mcp.Approve = async (name, cfg) =>   // called from StartMcp, on the UI thread
        {
            var key = $"mcp|{name}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cfg.ToJsonString())))[..32]}";
            if (S.Trusted.Contains(key)) return true;
            // what it runs, without secret values (env and header values are hidden but still part of the check)
            var what = (string?)cfg["url"] is { } url ? $"Connects to {url}"
                : $"Runs: {(string?)cfg["command"]} {string.Join(" ", cfg["args"]?.AsArray().Select(a => (string?)a) ?? [])}";
            var keys = (cfg["env"] as JsonObject ?? cfg["headers"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? [];
            if (keys.Count > 0) what += "\nWith: " + string.Join(", ", keys);
            var ok = await Confirm($"Allow the MCP server \"{name}\"?",
                what + "\n\nIt runs with your Windows account's rights. Only allow servers you trust. If its settings change, you'll be asked again.", "Allow");
            if (ok) { S.Trusted.RemoveAll(t => t.StartsWith($"mcp|{name}|")); S.Trusted.Add(key); SaveSettings(); }
            return ok;
        };
    }

    // after launch: skills that are new or changed since you last allowed them
    async void CheckSkills()
    {
        var fresh = Skills.All().Where(s => !Skills.Disabled.Contains(s.Name) && !S.Trusted.Contains(SkillKey(s))).ToList();
        if (fresh.Count == 0) return;
        var names = string.Join(", ", fresh.Take(12).Select(s => s.Name)) + (fresh.Count > 12 ? $" and {fresh.Count - 12} more" : "");
        var ok = await Confirm(fresh.Count == 1 ? "Allow a new skill?" : $"Allow {fresh.Count} new or changed skills?",
            $"{names}\n\nSkills are instructions the model follows. Allow only ones you trust; you'll be asked again if one changes.", "Allow", cancel: "Not now");
        if (!ok) return;   // they stay unused until allowed
        foreach (var s in fresh) { S.Trusted.RemoveAll(t => t.StartsWith($"skill|{s.Name}|")); S.Trusted.Add(SkillKey(s)); }
        SaveSettings();
    }
}
