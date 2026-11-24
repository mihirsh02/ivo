using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// The first start after an install, in Ivo and Ivo Portal: when an account from before is on this PC, the app asks
// whether to carry on with it or start a new one. A new one moves the old data aside (kept whole, not deleted), and the
// usual first-run onboarding follows. Nothing found, or not just installed: nothing is asked. The installers leave the
// "just installed" flag (HKCU\Software\Ivo\<app>\JustInstalled); it's cleared once read.
public static class AccountChoice
{
    public static void AskIfNeeded(string app, string dataDir, string name)
    {
        using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Ivo\{app}", writable: true))
        {
            if (key?.GetValue("JustInstalled") == null) return;
            key.DeleteValue("JustInstalled", false);
        }
        if (!Directory.Exists(dataDir) || !Directory.EnumerateFileSystemEntries(dataDir).Any()) return;
        if (!Ask(app, name)) return;
        try { Directory.Move(dataDir, $"{dataDir} (old account {DateTime.Now:yyyy-MM-dd HHmm})"); }
        catch (Exception ex) { IvoFrame.Message(null, $"The old account couldn't be moved aside ({ex.Message}), so {app} carries on with it.", "Couldn't start a new account"); }
    }

    // true: a new account. Closing it without choosing keeps the account (never a surprise fresh start)
    static bool Ask(string app, string name)
    {
        var fresh = false;
        var current = Application.Current;
        var mode = current?.ShutdownMode ?? ShutdownMode.OnExplicitShutdown;
        if (current != null) current.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // this is the only window yet: closing it mustn't end the app
        var w = new Window { Title = "Welcome back", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                             FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), Topmost = true };
        Button Pill(string label, bool main)
        {
            var face = new FrameworkElementFactory(typeof(Border));
            face.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
            face.SetValue(Border.PaddingProperty, new Thickness(18, 9, 18, 10));
            face.SetValue(Border.BackgroundProperty, main ? new LinearGradientBrush(Color.FromRgb(0x85, 0x79, 0xFF), Color.FromRgb(0x5A, 0x48, 0xF5), 0) : new SolidColorBrush(Color.FromRgb(0x26, 0x30, 0x3A)));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            face.AppendChild(cp);
            var b = new Button { Content = label, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                                 IsDefault = main, Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
            b.MouseEnter += (_, _) => b.Opacity = 0.85; b.MouseLeave += (_, _) => b.Opacity = 1;
            return b;
        }
        var keep = Pill(name != "" ? $"Continue as {name}" : "Continue with my account", true);
        var start = Pill("Start a new account", false);
        keep.Click += (_, _) => w.Close();
        start.Click += (_, _) => { fresh = true; w.Close(); };
        var fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)); var dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xAF));
        w.Content = new StackPanel { Margin = new Thickness(22, 2, 22, 20), Children = {
            new TextBlock { Text = $"{app} found your account on this PC{(name != "" ? $" ({name})" : "")}, with your chats, contacts and settings.", FontSize = 14, Foreground = fg, TextWrapping = TextWrapping.Wrap, LineHeight = 21 },
            new TextBlock { Text = "Carry on with it, or start fresh. Starting fresh keeps the old account in a folder beside the new one; nothing is deleted.", FontSize = 12.5, Foreground = dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) },
            keep, start } };
        IvoFrame.Apply(w);
        w.ShowDialog();
        if (current != null) current.ShutdownMode = mode;
        return fresh;
    }
}
