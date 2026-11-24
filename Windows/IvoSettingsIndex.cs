using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace Ivo;

// One setting on the Settings page, as Ivo sees it: its section, its name, what kind it is and its choices.
// Kind: toggle (a CheckBox or ToggleButton) | choice (a group of RadioButtons, or a ComboBox) | number (a Slider).
// Row: the name beside the control on its row ("Her voice"), also matched. Options: the RadioButtons of a choice. Weighty: needs a restart, resets or removes something, or changes the lock.
public sealed record SettingControl(string Section, string Label, string Kind, string[] Choices, FrameworkElement Control, FrameworkElement[] Options, bool Weighty, string Row = "");

// Ivo changes a setting by working the Settings page's own control, exactly as a click would, so every switch, choice
// and slider there works from her chat, the app updates at once and saves the way it always does. Text and password
// boxes are never touched: keys, PINs and sign-ins stay where only you type them.
public static class IvoSettingsIndex
{
    static readonly Regex Heavy = new(@"restart|reset|clear|remove|delete|erase|wipe|lock|uninstall|sign out|disconnect", RegexOptions.IgnoreCase);

    // a control's words: its text, or the TextBlock / Runs inside it, up to " · " (the hint after it)
    static string Text(object? o) => o switch
    {
        string s => s,
        TextBlock t => t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } runs ? runs : t.Text : t.Text,
        ContentControl c => Text(c.Content),
        Panel p => string.Join(" ", p.Children.OfType<object>().Select(Text).Where(x => x != "")),
        Decorator d => Text(d.Child),
        _ => "",
    };
    static string Label(object? o) => Text(o).Split(" · ")[0].Trim();

    // a list or slider's name: its tooltip, or the words on its own row (often after it: it docks right), else the last words above
    static string Beside(FrameworkElement c, string above)
    {
        if (c.ToolTip is string tip && tip != "") return Label(tip);
        if (c.Parent is Panel row)
            foreach (var o in row.Children.OfType<FrameworkElement>())
                if (o != c && o is TextBlock or ToggleButton && Label(o is ToggleButton b ? b.Content : o) is { Length: > 0 } t) return t;
        return above;
    }

    public static List<SettingControl> Build(FrameworkElement settingsRoot, Func<FrameworkElement, string> sectionOf)
    {
        var found = new List<SettingControl>();
        var groups = new Dictionary<string, List<RadioButton>>();
        string lastText = "";
        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case PasswordBox or TextBox: return;
                case TextBlock t when t.Parent is not ButtonBase: lastText = Label(t); return;
                case RadioButton r:
                {
                    var key = r.GroupName is { Length: > 0 } g ? g : r.Parent?.GetHashCode().ToString() ?? "";
                    if (!groups.TryGetValue(key, out var list))
                    {
                        groups[key] = list = [];
                        var name = r.GroupName is { Length: > 0 } gn && !Regex.IsMatch(gn, @"^\d") ? Regex.Replace(gn, "(?<=[a-z])(?=[A-Z])", " ") : lastText;
                        found.Add(new(sectionOf(r), name, "choice", [], r, [], false));
                    }
                    list.Add(r);
                    return;
                }
                case ToggleButton b when b is CheckBox || b.Content != null:
                {
                    var own = Label(b.Content);   // a bare "On" / "Enabled" box is named by its row
                    if (System.Windows.Automation.AutomationProperties.GetName(b) is { Length: > 0 } spoken) own = spoken;   // its accessible name says what it is best
                    found.Add(new(sectionOf(b), own.ToLowerInvariant() is "on" or "enabled" or "enable" or "" ? lastText : own, "toggle", ["on", "off"], b, [], false, lastText));
                    return;
                }
                case ComboBox c:
                    found.Add(new(sectionOf(c), Beside(c, lastText), "choice", c.Items.OfType<object>().Select(i => Label(i)).ToArray(), c, [], false));
                    return;
                case Slider s:
                    found.Add(new(sectionOf(s), Beside(s, lastText), "number", [], s, [], false));
                    return;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child);
        }
        Walk(settingsRoot);
        return found.Where(c => c.Label != "").Select(c =>
        {
            if (c.Kind == "choice" && c.Control is RadioButton first)
            {
                var opts = groups.First(g => g.Value.Contains(first)).Value;
                c = c with { Options = [.. opts], Choices = opts.Select(o => Label(o.Content)).ToArray() };
            }
            return c with { Weighty = Heavy.IsMatch(c.Label) || Heavy.IsMatch(c.Section) };
        }).ToList();
    }

    // what you call a choice, as the Settings page names it
    static readonly Dictionary<string, string[]> Also = new()
    {
        ["oled"] = ["amoled", "black", "true black", "oled"], ["softdark"] = ["dark", "dark mode", "night"], ["soft"] = ["light", "light mode", "default", "white"],
        ["glow"] = ["ivo", "violet", "aurora", "purple"],
    };

    static string[] Tokens(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w is not ("the" or "my" or "a" or "an" or "of" or "to" or "in" or "on" or "for" or "and")).ToArray();

    static double Fit(string label, string setting)
    {
        var (l, s) = (Tokens(label), Tokens(setting));
        if (l.Length == 0 || s.Length == 0) return 0;
        var hits = s.Count(w => l.Any(x => x == w || x.TrimEnd('s') == w.TrimEnd('s')));
        return (double)hits / Math.Max(l.Length, s.Length);
    }

    // the choice a word means: its name, its tag, or a word people use for it
    static FrameworkElement? Option(SettingControl c, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        foreach (var o in c.Options)
        {
            var tag = (o.Tag as string ?? "").ToLowerInvariant();
            var name = Label((o as ContentControl)?.Content).ToLowerInvariant();
            if (v == tag || v == name || Also.GetValueOrDefault(tag, []).Contains(v) || name != "" && Fit(name, v) >= 1) return o;
        }
        return null;
    }

    // the setting you mean, and the value as the control knows it; or a question (Ask) with what to pick from (Choices:
    // the settings' names, or a setting's values). A name that only half fits isn't a match: she won't flip a near miss.
    public static (SettingControl? Hit, string? Value, string? Ask, string[] Choices) Resolve(IReadOnlyList<SettingControl> all, string setting, string? value)
    {
        double Named(SettingControl c) => Math.Max(Fit(c.Label, setting), c.Row == "" || c.Row == c.Label ? 0 : Fit(c.Row, setting) * .9);
        var scored = all.Select(c => (c, fit: Named(c), score: Named(c) + (c.Kind == "choice" && value != null && Option(c, value) != null ? .5 : 0)))
                        .Where(x => x.fit >= .5).OrderByDescending(x => x.score).ToList();
        if (scored.Count == 0) return (null, null, null, []);
        var top = scored[0].score;
        var tied = scored.Where(x => Math.Abs(x.score - top) < 1e-9).Select(x => x.c).DistinctBy(x => (x.Section, x.Label)).ToList();
        if (tied.Count > 1)
        {
            var sameName = tied.DistinctBy(x => x.Label).Count() < tied.Count;
            var names = tied.Select(x => sameName ? $"{x.Label} ({x.Section})" : x.Label).ToArray();
            return (null, null, $"Which one: {string.Join(", ", names[..^1])} or {names[^1]}?", names);
        }
        var hit = scored[0].c;
        if (value == null) return (hit, null, null, []);
        var v = value.Trim().ToLowerInvariant();
        switch (hit.Kind)
        {
            case "toggle":
                return v is "on" or "off" ? (hit, v, null, []) : (null, null, $"On or off, for {hit.Label}?", ["on", "off"]);
            case "choice" when hit.Options.Length > 0:
                return Option(hit, v) is { } o ? (hit, (o.Tag as string) is { Length: > 0 } tag ? tag : Label((o as ContentControl)?.Content), null, [])
                                               : (null, null, $"{hit.Label} can be {string.Join(", ", hit.Choices)}. Which one?", hit.Choices);
            case "choice":
                return hit.Choices.FirstOrDefault(x => x.Equals(v, StringComparison.OrdinalIgnoreCase) || Fit(x, v) >= 1) is { } pick ? (hit, pick, null, [])
                    : (null, null, $"{hit.Label} can be {string.Join(", ", hit.Choices.Take(8))}. Which one?", [.. hit.Choices.Take(8)]);
            case "number" when !double.TryParse(v, out _) && v is not ("bigger" or "larger" or "louder" or "brighter" or "faster" or "more" or "up" or "smaller" or "quieter" or "darker" or "slower" or "less" or "down"):
                return (null, null, $"What number, for {hit.Label}? Or say bigger or smaller.", ["bigger", "smaller"]);
            default:
                return (hit, v, null, []);
        }
    }

    public static string Read(SettingControl c) => c.Control switch
    {
        ToggleButton b when c.Kind == "toggle" => b.IsChecked == true ? "on" : "off",
        _ when c.Options.Length > 0 => c.Options.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true) is { } r ? Label(r.Content) : "not set",
        ComboBox cb => Label(cb.SelectedItem),
        Slider s => s.Value.ToString("0.##"),
        _ => "",
    };

    // works the control like a click; what comes back puts it back as it was
    public static Action Apply(SettingControl c, string value)
    {
        switch (c.Control)
        {
            case ToggleButton b when c.Kind == "toggle":
            {
                var was = b.IsChecked;
                void Set(bool? on) { b.IsChecked = on; b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
                Set(value == "on");
                return () => Set(was);
            }
            case not null when c.Options.Length > 0:
            {
                var was = c.Options.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
                var to = c.Options.OfType<RadioButton>().First(r => (r.Tag as string) == value || Label(r.Content) == value);
                to.IsChecked = true;
                return () => { if (was != null) was.IsChecked = true; };
            }
            case ComboBox cb:
            {
                var was = cb.SelectedItem;
                cb.SelectedItem = cb.Items.OfType<object>().First(i => Label(i).Equals(value, StringComparison.OrdinalIgnoreCase));
                return () => cb.SelectedItem = was;
            }
            case Slider s:
            {
                var was = s.Value;
                var step = Math.Max(s.SmallChange, (s.Maximum - s.Minimum) / 10);
                s.Value = value switch
                {
                    "bigger" or "larger" or "louder" or "brighter" or "faster" or "more" or "up" => Math.Min(s.Maximum, s.Value + step),
                    "smaller" or "quieter" or "darker" or "slower" or "less" or "down" => Math.Max(s.Minimum, s.Value - step),
                    _ => double.TryParse(value, out var n) ? Math.Clamp(n, s.Minimum, s.Maximum) : s.Value,
                };
                return () => s.Value = was;
            }
            default: return () => { };
        }
    }
}
