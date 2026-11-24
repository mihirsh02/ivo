using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Ivo;

// App lock: a 6-digit PIN or an 8-20 character password, asked on every launch (and after a chosen idle time). While locked
// an opaque layer covers the whole window and notifications don't say what they're about. Eight one-time recovery codes are
// shown when the passkey is set. Only salted PBKDF2 hashes are stored, never the passkey or the codes.
// ponytail: this hides the UI; the data files on disk aren't encrypted by it (Windows' own account protects those).
public partial class MainWindow
{
    static readonly int[] LockTimes = [1, 2, 3, 5, 10, 15, 20, 25, 30, 45, 60, 120, 180];
    bool locked;
    DateTime lastInput = DateTime.Now;

    bool KeyMatches(string key) => Crypto.Matches(S.LockHash, key);

    // unlocked with the PIN / password: open the data key
    void Unsealed(string key)
    {
        if (Store.DataKey != null) return;   // already open this session (auto-lock, Windows lock)
        Store.DataKey = Crypto.Unwrap(S.LockWrap, key);
        if (Store.DataKey == null) Store.Fail("App lock", "The passkey matched but didn't open the data key.");   // never re-key existing sealed data
        Store.OpenVault(S);
        Inbox.Reload(); RefreshChats();
        if (!S.CodesSaved && S.LockCodes.Count > 0)   // never copied, saved or printed: offer fresh ones
            Dispatcher.BeginInvoke(async () =>
            {
                if (await Confirm("Your recovery codes aren't saved", "If you forget your PIN or password, recovery codes are the only way back in. Make a new set and save them now?", "Make new codes", cancel: "Later"))
                    ShowCodes();
            });
    }

    void StartLock()
    {
        foreach (var m in LockTimes) AutoLockTime.Items.Add(new ComboBoxItem { Content = (m < 60 ? $"{m} minute{(m == 1 ? "" : "s")}" : $"{m / 60} hour{(m == 60 ? "" : "s")}") + (m == 5 ? " (Recommended)" : ""), Tag = m });
        RefreshLockUi();
        InputManager.Current.PreProcessInput += (_, e) => { if (e.StagingItem.Input is KeyboardEventArgs or MouseEventArgs) lastInput = DateTime.Now; };
        var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        idle.Tick += (_, _) => { if (!locked && S.LockHash != "" && S.AutoLock && DateTime.Now - lastInput > TimeSpan.FromMinutes(S.AutoLockMinutes)) Lock(); };
        idle.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>   // Windows locked (Win+L, sleep, lid): Ivo locks too
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock && S.LockWithWindows) Dispatcher.BeginInvoke(Lock);
        };
        if (S.LockHash != "") Lock();
    }

    void RefreshLockUi()
    {
        var on = S.LockHash != "";
        LockSetBtn.Content = on ? "Change" : "Set";
        LockRemoveBtn.Visibility = LockCodesBtn.Visibility = AutoLockRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        (S.LockKind == "password" ? LockPassword : LockPin).IsChecked = true;
        AutoLockBox.IsChecked = S.AutoLock;
        LockWithWindowsBox.IsChecked = S.LockWithWindows;
        AutoLockTime.SelectedItem = AutoLockTime.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == S.AutoLockMinutes) ?? AutoLockTime.Items[3];
        AutoLockTime.IsEnabled = S.AutoLock;
    }

    Grid Layer(int z = 100000)
    {
        var layer = new Grid { Background = (Brush)FindResource("Bg") };
        Grid.SetRowSpan(layer, 99); Grid.SetColumnSpan(layer, 99); Panel.SetZIndex(layer, z);
        return layer;
    }
    static TextBlock Line(string s, double size, string brush, string? font = null)
    {
        var t = new TextBlock { Text = s, FontSize = size, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (font != null) t.FontFamily = new FontFamily(font);
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    // A full-window card: a title, one or two boxes, a button and maybe a link. kind: pin / password (checked as typed),
    // code (a recovery code) or any. done gets the input and returns false to say "wrong" and stay open.
    void PasskeyLayer(string title, string sub, bool confirm, string ok, Func<string, bool> done, Action? cancel, string kind = "any", (string Text, Action Run)? link = null)
    {
        var layer = Layer();
        var col = new StackPanel { Width = 300, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/ivo.ico")), Width = 56, Height = 56, Margin = new Thickness(0, 0, 0, 14) });
        col.Children.Add(Line(title, 18, "Fg"));
        col.Children.Add(Line(sub, 12.5, "Dim"));
        PasswordBox Box(string hint)
        {
            var b = new PasswordBox { Margin = new Thickness(0, 10, 0, 0), FontSize = 14, ToolTip = hint, MaxLength = kind == "pin" ? 6 : kind == "code" ? 9 : 20 };
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            col.Children.Add(b);
            return b;
        }
        var first = Box(kind == "pin" ? "PIN" : kind == "code" ? "Recovery code" : "Passkey");
        var second = confirm ? Box("Type it again") : null;
        var error = Line("", 12, "Dim"); error.Foreground = Shell.B("#F87171"); error.Margin = new Thickness(0, 8, 0, 0);
        col.Children.Add(error);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var go = new Button { Content = ok, Style = (Style)FindResource("Shimmer"), Padding = new Thickness(22, 7, 22, 7) };
        row.Children.Add(go);
        if (cancel != null)
        {
            var no = new Button { Content = "Cancel", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
            no.Click += (_, _) => { Root.Children.Remove(layer); cancel(); };
            row.Children.Add(no);
        }
        col.Children.Add(row);
        if (link is { } l)
        {
            var a = new TextBlock { Text = l.Text, FontSize = 12.5, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Foreground = Shell.B("#A78BFA") };
            WindowChrome.SetIsHitTestVisibleInChrome(a, true);
            a.MouseLeftButtonUp += (_, _) => { Root.Children.Remove(layer); l.Run(); };
            col.Children.Add(a);
        }
        void Submit()
        {
            var k = first.Password;
            if (k == "") return;
            if (kind == "pin" && !Regex.IsMatch(k, @"^\d{6}$")) { error.Text = "The PIN is exactly 6 numbers."; return; }
            if (kind == "password" && k.Length is < 8 or > 20) { error.Text = "Use 8 to 20 characters."; return; }
            if (second != null && second.Password != k) { error.Text = "The two don't match."; return; }
            if (done(k)) Root.Children.Remove(layer);
            else { error.Text = kind == "code" ? "That code isn't valid, or was already used." : "That's not right."; first.Clear(); first.Focus(); }
        }
        go.Click += (_, _) => Submit();
        layer.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };
        layer.Children.Add(col);
        // still closable while locked
        var close = new Button { Content = "", FontFamily = App.IconFont, FontSize = 14, Style = (Style)FindResource("Ghost"), Width = 40, Height = 36, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0), ToolTip = "Close" };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close();
        layer.Children.Add(close);
        Root.Children.Add(layer);
        Dispatcher.BeginInvoke(() => first.Focus(), DispatcherPriority.Input);
    }

    void Lock()
    {
        if (locked || S.LockHash == "") return;
        locked = true;
        LockScreen();
    }
    void LockScreen() => PasskeyLayer("Ivo is locked", S.LockKind == "pin" ? "Enter your 6-digit PIN." : "Enter your password.", false, "Unlock", key =>
    {
        if (!KeyMatches(key)) return false;
        locked = false; lastInput = DateTime.Now;
        Unsealed(key);
        return true;
    }, null, "any", S.LockCodes.Count > 0 ? ("Forgot it? Use a recovery code", Recover) : null);

    // a recovery code works once, then a new passkey has to be chosen
    void Recover() => PasskeyLayer("Recovery code", $"Enter one of the codes you saved, like ABCD-EFGH. {S.LockCodes.Count} left.", false, "Unlock", code =>
    {
        var c = code.Trim().ToUpperInvariant();
        var hit = S.LockCodes.FirstOrDefault(w => Crypto.Unwrap(w, c) != null);
        if (hit == null) return false;
        Store.DataKey ??= Crypto.Unwrap(hit, c);   // the code opens the data, then a new PIN / password re-wraps it
        Store.OpenVault(S);
        S.LockCodes.Remove(hit); SaveSettings();
        Inbox.Reload(); RefreshChats();
        Dispatcher.BeginInvoke(() => SetPasskey(S.LockKind, mustSet: true));
        return true;
    }, null, "code", ("Back", LockScreen));

    void SetPasskey(string kind, bool mustSet = false) => PasskeyLayer(kind == "pin" ? "Choose a 6-digit PIN" : "Choose a password",
        (kind == "pin" ? "Exactly 6 numbers." : "8 to 20 characters.") + " Asked every time Ivo opens.", true, "Save", key =>
        {
            S.LockHash = Crypto.Hash(key); S.LockKind = kind;
            if (mustSet) { locked = false; lastInput = DateTime.Now; }
            var first = Store.DataKey == null;   // turning the lock on: make the data key and seal what's on disk
            Store.DataKey ??= RandomNumberGenerator.GetBytes(32);
            S.LockWrap = Crypto.Wrap(Store.DataKey, key);
            Store.EncryptOn = true;
            if (first) Store.Reseal(S);
            SaveSettings(); RefreshLockUi();
            Dispatcher.BeginInvoke(ShowCodes);   // fresh recovery codes with every new passkey
            return true;
        }, mustSet ? null : () => RefreshLockUi(), kind);

    // eight one-time recovery codes, shown once; only their salted hashes are kept
    void ShowCodes()
    {
        const string abc = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O or 1/I to confuse
        var codes = Enumerable.Range(0, 8).Select(_ => RandomNumberGenerator.GetString(abc, 8)).Select(r => r[..4] + "-" + r[4..]).ToList();
        S.LockCodes = codes.Select(c => Crypto.Wrap(Store.DataKey!, c)).ToList();   // each one can open the data key
        S.CodesSaved = false;   // until they're copied, saved or printed
        SaveSettings();
        void Kept() { S.CodesSaved = true; SaveSettings(); }
        var text = "Ivo recovery codes (each works once)\r\n\r\n" + string.Join("\r\n", codes);
        var layer = Layer(100001);
        var col = new StackPanel { Width = 360, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(Line("Save your recovery codes", 18, "Fg"));
        col.Children.Add(Line("If you forget your PIN or password, each code unlocks Ivo once. They won't be shown again.", 12.5, "Dim"));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 14) };
        foreach (var c in codes) grid.Children.Add(Line(c, 15, "Fg", "Cascadia Code, Consolas"));
        col.Children.Add(grid);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var copy = new Button { Content = "Copy", Padding = new Thickness(16, 7, 16, 7) };
        copy.Click += (_, _) => { Clipboard.SetText(text); copy.Content = "Copied"; Kept(); };
        var save = new Button { Content = "Save as file…", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { FileName = "Ivo recovery codes.txt", Filter = "Text|*.txt" };
            if (d.ShowDialog(this) == true) { System.IO.File.WriteAllText(d.FileName, text); Kept(); }
        };
        // print, or "Microsoft Print to PDF" for a PDF: a plain page with the codes in a large fixed-width font
        var print = new Button { Content = "Print / PDF…", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 8, 0) };
        print.Click += (_, _) =>
        {
            var pd = new PrintDialog();
            if (pd.ShowDialog() != true) return;
            var doc = new System.Windows.Documents.FlowDocument { PagePadding = new Thickness(72), FontFamily = new FontFamily("Segoe UI"), ColumnWidth = double.PositiveInfinity };
            doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("Ivo recovery codes")) { FontSize = 22, FontWeight = FontWeights.SemiBold });
            doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run($"Made {DateTime.Now:d MMMM yyyy}. Each code unlocks Ivo once if you forget your PIN or password. Keep this page somewhere safe.")) { FontSize = 12 });
            foreach (var c in codes) doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(c)) { FontSize = 20, FontFamily = new FontFamily("Cascadia Code, Consolas"), Margin = new Thickness(0, 4, 0, 4) });
            pd.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)doc).DocumentPaginator, "Ivo recovery codes");
            Kept();
        };
        var done = new Button { Content = "I've saved them", Style = (Style)FindResource("Shimmer"), Padding = new Thickness(18, 7, 18, 7) };
        done.Click += (_, _) => Root.Children.Remove(layer);
        row.Children.Add(copy); row.Children.Add(save); row.Children.Add(print); row.Children.Add(done);
        col.Children.Add(row);
        layer.Children.Add(col);
        Root.Children.Add(layer);
    }

    void LockSet_Click(object s, RoutedEventArgs e)
    {
        var kind = LockPassword.IsChecked == true ? "password" : "pin";
        if (S.LockHash == "") SetPasskey(kind);
        else PasskeyLayer("Current " + (S.LockKind == "pin" ? "PIN" : "password"), "Enter it to change it.", false, "Next",
            key => { if (!KeyMatches(key)) return false; Dispatcher.BeginInvoke(() => SetPasskey(kind)); return true; }, () => { });
    }

    void LockCodes_Click(object s, RoutedEventArgs e) =>
        PasskeyLayer("New recovery codes", "Enter your PIN or password. The old codes stop working.", false, "Next",
            key => { if (!KeyMatches(key)) return false; Dispatcher.BeginInvoke(ShowCodes); return true; }, () => { });

    void LockRemove_Click(object s, RoutedEventArgs e) =>
        PasskeyLayer("Remove the lock", "Enter your PIN or password to turn the lock off.", false, "Remove", key =>
        {
            if (!KeyMatches(key)) return false;
            S.LockHash = ""; S.LockCodes = []; S.AutoLock = false;
            Store.EncryptOn = false; Store.Reseal(S);   // everything back to the plain (Windows-protected) form
            Store.DataKey = null; S.LockWrap = "";
            SaveSettings(); RefreshLockUi();
            return true;
        }, () => { });

    void LockWithWindows_Changed(object s, RoutedEventArgs e) { S.LockWithWindows = LockWithWindowsBox.IsChecked == true; SaveSettings(); }

    void AutoLock_Changed(object s, RoutedEventArgs e)
    {
        if (AutoLockTime.Items.Count == 0) return;   // still filling the list
        S.AutoLock = AutoLockBox.IsChecked == true;
        if (AutoLockTime.SelectedItem is ComboBoxItem { Tag: int m }) S.AutoLockMinutes = m;
        AutoLockTime.IsEnabled = S.AutoLock;
        SaveSettings();
    }
}
