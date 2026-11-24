using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Quick capture (Ctrl+Alt+N, from anywhere): one line, then
//   Enter        a to-do, or a reminder when it says when ("call mom at 6pm", "in 20 min", "tomorrow 9am")
//   Ctrl+Enter   ask Ivo: a new chat with it
// Esc or a click elsewhere closes it. While Ivo is locked it opens the lock screen instead.
public partial class MainWindow
{
    Window? captureWin;

    // a new chat that starts with this message (Spotify's "Ask Ivo for music", quick capture's Ctrl+Enter)
    public void AskIvo(string text)
    {
        if (locked) { ShowWindow(); return; }
        ShowWindow(); active.ShowChat(NewChat()); active.Send(text);
    }

    void QuickCapture()
    {
        if (locked) { ShowWindow(); return; }
        if (captureWin != null) { captureWin.Activate(); return; }
        var box = new TextBox { FontSize = 16, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
        box.SetResourceReference(TextBox.ForegroundProperty, "Fg");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        var hint = new TextBlock { FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        void Hint()
        {
            var t = box.Text.Trim();
            hint.Text = t == "" ? "A to-do, or a reminder with a time (\"call mom at 6pm\").   Enter saves  ·  Ctrl+Enter asks Ivo  ·  Esc closes"
                : When.Parse(t) is { } w ? $"Enter: reminder \"{w.Text}\", {When.Day(w.At).ToLower()} at {When.Time(w.At)}   ·   Ctrl+Enter: ask Ivo"
                : "Enter: add to your to-do list   ·   Ctrl+Enter: ask Ivo";
        }
        Hint();
        box.TextChanged += (_, _) => Hint();
        var icon = new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 18, Foreground = Shell.B("#A78BFA"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var line = new DockPanel { Children = { icon, box } };
        DockPanel.SetDock(icon, Dock.Left);
        var card = new Border { Padding = new Thickness(20, 16, 20, 16), CornerRadius = new CornerRadius(16), Child = new StackPanel { Children = { line, hint } } };
        card.SetResourceReference(Border.BackgroundProperty, "BoxBg");
        var win = captureWin = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Width = 600, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.Manual, Content = card, Title = "Ivo quick capture",
        };
        var area = SystemParameters.WorkArea;
        win.Left = area.Left + (area.Width - win.Width) / 2; win.Top = area.Top + area.Height * 0.28;
        var closed = false;
        void Close() { if (closed) return; closed = true; if (captureWin == win) captureWin = null; win.Close(); }   // once (losing focus while closing would close it again)
        win.Deactivated += (_, _) => Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var text = box.Text.Trim();
            if (text == "") { Close(); return; }
            Close();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { AskIvo(text); return; }
            if (System.Text.RegularExpressions.Regex.Match(text, @"\bwhen I (?:open|start|launch)\s+([\w.\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } app)
            {
                var what = text.Remove(app.Index, app.Length).Trim(' ', ',');
                S.Schedules.Add(new Scheduled { Kind = "reminder", Text = what, When = WhenAppOpens, App = app.Groups[1].Value.Replace(".exe", "") });
                Notify("reminder", "Reminder set", $"{what} · when {app.Groups[1].Value} opens", () => OpenPlannerAnywhere("reminder"), "", "#F2B84B");
            }
            else if (When.Parse(text) is { } w)
            {
                S.Schedules.Add(new Scheduled { Kind = "reminder", Text = w.Text == "" ? text : w.Text, When = w.At });
                Notify("reminder", "Reminder set", $"{(w.Text == "" ? text : w.Text)} · {When.Day(w.At).ToLower()} at {When.Time(w.At)}", () => OpenPlannerAnywhere("reminder"), "", "#F2B84B");
            }
            else
            {
                S.Todos.Add(new Todo { Text = text });
                Notify("reminder", "Added to your to-do list", text, () => OpenPlannerAnywhere("todo"), "", "#34D399");
            }
            SaveSettings();
            ApplyTitleButtons();
        };
        win.Show(); win.Activate(); box.Focus();
    }
}
