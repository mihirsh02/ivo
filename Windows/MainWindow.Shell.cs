using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ivo;

// The window itself: in-window dialogs, the web page popup, tray, global shortcut and maximising.
public partial class MainWindow
{
    // ---------- in-window dialogs (instead of Windows message boxes) ----------
    TaskCompletionSource<bool>? dialog;

    public Task<bool> Confirm(string title, string message, string ok = "OK", bool danger = false, string? cancel = "Cancel")
    {
        dialog?.TrySetResult(false);
        dialog = new();
        DialogTitle.Text = title; DialogText.Text = message; DialogBox.Width = 420;
        DialogOk.Content = ok; DialogOk.Style = (Style)FindResource(danger ? "Danger" : "Shimmer");
        DialogCancel.Content = cancel; DialogCancel.Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;
        DialogIcon.Text = danger ? "\uE7BA" : "\uE946";
        DialogIcon.Foreground = new SolidColorBrush(danger ? Color.FromRgb(0xF8, 0x71, 0x71) : Color.FromRgb(0xA7, 0x8B, 0xFA));
        DialogIconBg.Background = new SolidColorBrush(danger ? Color.FromArgb(0x2E, 0xEF, 0x44, 0x44) : Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF));
        CloseMenu();
        DialogLayer.Visibility = Visibility.Visible;
        DialogOk.Focus();
        return dialog.Task;
    }
    public Task Notice(string title, string message) { Store.Fail(title, message); return Confirm(title, message, cancel: null); }

    // The dialog with a text box; the typed text, or null when cancelled or empty.
    public async Task<string?> AskText(string title, string message, string initial, string ok)
    {
        DialogInput.Text = initial; DialogInput.Visibility = Visibility.Visible;
        var answer = Confirm(title, message, ok);
        DialogInput.Focus(); DialogInput.SelectAll();
        var yes = await answer;
        DialogInput.Visibility = Visibility.Collapsed;
        return yes && DialogInput.Text.Trim() != "" ? DialogInput.Text.Trim() : null;
    }

    void CloseDialog(bool result)
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        var d = dialog; dialog = null;
        d?.TrySetResult(result);
    }
    void DialogOk_Click(object s, RoutedEventArgs e) => CloseDialog(true);
    void DialogCancel_Click(object s, RoutedEventArgs e) => CloseDialog(false);
    void DialogBackdrop_Click(object s, MouseButtonEventArgs e) => CloseDialog(false);

    // ---------- web page popup ----------
    Microsoft.Web.WebView2.Wpf.WebView2? web;
    string webUrl = "";

    void WebWhere_Checked(object s, RoutedEventArgs e)
    {
        if (loading) return;
        S.WebInApp = WebInAppChoice.IsChecked == true; SaveSettings();
    }

    async void ShowPage(string title, string url)
    {
        if (!S.WebInApp) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } return; }   // the default: your browser
        WebTitle.Text = title; webUrl = url;
        WebLayer.Visibility = Visibility.Visible;
        try
        {
            if (web == null)
            {
                web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
                WebHost.Child = web;
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, System.IO.Path.Combine(Store.Dir, "web"));
                await web.EnsureCoreWebView2Async(env);
                Guard(web.CoreWebView2);
                web.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; web.CoreWebView2.Navigate(a.Uri); };   // sign-in pop-ups stay in the view
            }
            web.CoreWebView2.Navigate(url);
        }
        catch
        {
            // no WebView2 runtime: fall back to the browser
            WebLayer.Visibility = Visibility.Collapsed;
            WebBrowser_Click(this, new RoutedEventArgs());
        }
    }
    void WebClose_Click(object s, RoutedEventArgs e) => CloseWeb();
    void WebClose_Click(object s, MouseButtonEventArgs e) => CloseWeb();

    // closed: its Edge processes (~300 MB) go too; the next page starts a fresh one in about a second (sign-ins are kept on disk)
    void CloseWeb()
    {
        WebLayer.Visibility = Visibility.Collapsed;
        WebHost.Child = null;
        web?.Dispose(); web = null;
    }
    void WebReload_Click(object s, RoutedEventArgs e) => web?.CoreWebView2?.Reload();
    void WebBrowser_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(web?.Source?.AbsoluteUri is { Length: > 0 } u && u != "about:blank" ? u : webUrl) { UseShellExecute = true });

    void Window_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.F6 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; CycleRegions(); return; }   // move between the main areas
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; if (callFull) CallFullScreen(); else FullScreen(!fullScreen); return; }   // full screen
        if (e.Key == Key.Escape && fullScreen) { e.Handled = true; if (callFull) CallFullScreen(); else FullScreen(false); return; }   // and out of it
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0)   // text size
        {
            e.Handled = true; StepZoom(e.Key is Key.OemPlus or Key.Add ? 1 : e.Key is Key.OemMinus or Key.Subtract ? -1 : 0); return;
        }
        if (CodeMode && e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SaveCurrent(); return; }
        if (CodeMode && e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowQuickOpen(); return; }
        if (CodeMode && e.Key == Key.V && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && IsMarkdown(current)) { e.Handled = true; mdPreview = !mdPreview; UpdateMdPreview(); return; }
        if (CodeMode && e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowReplace(); return; }
        if (ScheduleLayer.Visibility == Visibility.Visible && e.Key == Key.Escape) { e.Handled = true; ScheduleLayer.Visibility = Visibility.Collapsed; return; }
        if (starsLayer != null && e.Key == Key.Escape) { e.Handled = true; CloseStars(); return; }
        if (ReaderLayer.Visibility == Visibility.Visible && e.Key == Key.Escape) { e.Handled = true; ReaderLayer.Visibility = Visibility.Collapsed; return; }
        if (e.Key == Key.Escape && islandOpen) { e.Handled = true; FoldIsland(); return; }
        if (e.Key == Key.Escape && mapLayer?.IsVisible == true) { e.Handled = true; if (mapOpen != null) { mapOpen = null; DrawAgentMap(); } else CloseAgentMap(); return; }
        if (SpotifyLayer.Visibility == Visibility.Visible && e.Key == Key.Escape) { e.Handled = true; CloseSpotify(); return; }
        if (WheelLayer.Visibility == Visibility.Visible && e.Key == Key.Escape) { e.Handled = true; CloseWheel_Click(this, e); return; }
        if (WebLayer.Visibility == Visibility.Visible && e.Key == Key.Escape) { e.Handled = true; WebLayer.Visibility = Visibility.Collapsed; return; }
        if (DialogLayer.Visibility != Visibility.Visible && SettingsPanel.Visibility == Visibility.Visible && e.Key == Key.Escape) { e.Handled = true; SettingsPanel.Visibility = Visibility.Collapsed; return; }
        if (DialogLayer.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Escape) { e.Handled = true; CloseDialog(false); }
        else if (e.Key == Key.Enter) { e.Handled = true; CloseDialog(true); }
    }

    // ---------- tray, global shortcut, maximise ----------
    // Idle, Ivo lets the runtime reclaim unused memory when it's minimised or has lost focus for a while.
    bool Answering => AllPanes.Any(p => p.Answering);
    int trimId;
    static int trimming;
    void TrimSoon(TimeSpan after)
    {
        var me = ++trimId;
        _ = Task.Delay(after).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            // not while you're in another Ivo window (companion, a floating panel) or a reply is coming in
            if (me != trimId || Application.Current.Windows.Cast<Window>().Any(w => w.IsActive) && WindowState != WindowState.Minimized && IsVisible || Voice.Busy || Answering || portalCall == "on" || groupCall != null) return;
            GiveBack();
        }));
    }

    // Keep idle collection off the dispatcher and allow the runtime to perform it concurrently.
    static void GiveBack()
    {
        if (Interlocked.Exchange(ref trimming, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false); }
            finally { Volatile.Write(ref trimming, 0); }
        });
    }

    // and every 45 seconds while nothing of Ivo is in use (started in the background, it never loses focus to trigger it)
    System.Windows.Threading.DispatcherTimer? idleTrim;
    bool trimmedIdle;
    void StartIdleTrim()
    {
        idleTrim = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        idleTrim.Tick += (_, _) =>
        {
            var inUse = Application.Current.Windows.Cast<Window>().Any(w => w.IsActive) && WindowState != WindowState.Minimized && IsVisible;
            if (inUse || Voice.Busy || Answering || portalCall == "on" || IvoModelBusy)
            {
                if (trimmedIdle) App.PauseMotion(false);
                trimmedIdle = false;
                return;
            }
            if (trimmedIdle) return;   // once per quiet spell: after that there's nothing new to give back
            trimmedIdle = true;
            if (IsVisible && WindowState != WindowState.Minimized) App.PauseMotion(true);
            GiveBack();
        };
        idleTrim.Start();
        Activated += (_, _) => { trimmedIdle = false; App.PauseMotion(false); };
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) App.PauseMotion(false); };
    }

    static bool IvoModelBusy => IvoModel.Loaded;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint DefWindowProc(nint hwnd, int msg, nint w, nint l);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    const int HotkeyId = 0xA61, CompanionKeyId = 0xA62, SnipKeyId = 0xA63, CaptureKeyId = 0xA64;
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(nint hwnd);
    DateTime snipUntil;   // a screen clip was started: the next picture on the clipboard (until then) goes into the chat

    // Text from screen: Ivo's own picker (no other app), then OCR on this PC
    // the rail's button: Ivo's window freezes and blurs, with Select an area and Add a photo floating over it
    public void TextFromScreen_Click(object s, RoutedEventArgs e)
    {
        if (locked) { ShowWindow(); return; }
        new TextSnip(this, shot => _ = ReadClip(shot), TextFromPhoto).Show();
    }

    void TextFromArea() => TextFromScreen_Click(this, new RoutedEventArgs());

    void TextFromPhoto()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "A photo or picture with text", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.UriSource = new Uri(dlg.FileName); img.EndInit(); img.Freeze();
            _ = ReadClip(img);
        }
        catch (Exception ex) { _ = Notice("Couldn't open that picture", ex.Message); }
    }

    void StartSnip()
    {
        if (locked) { ShowWindow(); return; }
        snipUntil = DateTime.Now.AddMinutes(1);
        try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch (Exception ex) { snipUntil = default; _ = Notice("Couldn't start the screen clip", ex.Message); }
    }

    void ClipboardChanged()
    {
        try
        {
            if (DateTime.Now < snipUntil && Clipboard.ContainsImage() && Clipboard.GetImage() is { } shot)
            {
                snipUntil = default;
                ShowWindow();
                active.AttachImage(shot);
                return;
            }
            // password managers mark secrets so Windows' own clipboard history skips them: so does this
            var secret = Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || Clipboard.ContainsData("Clipboard Viewer Ignore")
                         || Clipboard.GetData("CanIncludeInClipboardHistory") is System.IO.MemoryStream { Length: 4 } m && m.ToArray().All(b => b == 0);
            if (S.ClipboardHistory && !secret && Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 and < 20_000 } t)
                lock (Agent.Clips) { Agent.Clips.Remove(t); Agent.Clips.Insert(0, t); if (Agent.Clips.Count > 50) Agent.Clips.RemoveAt(50); }
        }
        catch { }   // the clipboard is busy with another app: skip this one
    }
    // the clipped area's text, read on this PC, onto the clipboard
    async Task ReadClip(System.Windows.Media.Imaging.BitmapSource shot)
    {
        try
        {
            var text = await Ocr.Read(shot);
            if (text.Trim() == "") { NotifyCard.Show("No text found", "Try a larger area or a clearer picture.", "", "#22D3EE", TextFromArea); return; }
            Clipboard.SetText(text);
            NotifyCard.Show("Text copied", text.Length > 90 ? text[..90] + "…" : text, "", "#22D3EE", () => { ShowWindow(); active.InsertText(text); });
        }
        catch (Exception ex) { _ = Notice("Couldn't read the text", ex.Message); }
    }

    public bool Quitting => quitting;
    public void QuitIvo() { quitting = true; Close(); }
    Companion? companion;
    nint hwnd;
    bool companionKeyOk;
    bool hotkeyOk, quitting;
    System.Windows.Forms.NotifyIcon? tray;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        Deactivated += (_, _) => TrimSoon(TimeSpan.FromSeconds(30));
        Activated += (_, _) => trimId++;   // back in use: no trim
        // minimised: nothing in the window is drawn (looping animations included), and memory is trimmed; restored, it's only redrawn
        StateChanged += (_, _) =>
        {
            var down = WindowState == WindowState.Minimized;
            if (Content is UIElement page) page.Visibility = down ? Visibility.Hidden : Visibility.Visible;
            // motion pauses only while nothing of Ivo's is on screen (the companion or a notification keeps it going)
            App.PauseMotion(down && !Application.Current.Windows.OfType<Window>().Any(w => w != this && w.IsVisible));
            if (down) TrimSoon(TimeSpan.FromSeconds(2));
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) TrimSoon(TimeSpan.FromSeconds(2)); };
        TrimSoon(TimeSpan.FromSeconds(15));   // started minimised or in the background: trim once start-up settles
        using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))   // the app moved (new folder, an update): point startup at this copy
            if (run?.GetValue("Ivo") is string v && !StartsWithWindows() && !Environment.ProcessPath!.Contains(@"\bin\")) SetStartup(true);
        loading = true; StartupBox.IsChecked = StartsWithWindows(); StartTrayBox.IsChecked = S.StartInTray; StartTrayBox.IsEnabled = StartupBox.IsChecked == true;
        (S.WebInApp ? WebInAppChoice : WebBrowserChoice).IsChecked = true; loading = false;
        ApplyTitleButtons();
        UpdateInboxBadge();
        Sounds.Enabled = S.AppSounds; DrawSounds();
        if (System.IO.File.Exists(App.LastCrash))   // Ivo crashed last time: say what happened, once
        {
            var crash = System.IO.File.ReadAllText(App.LastCrash); System.IO.File.Delete(App.LastCrash);
            var lines = crash.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Dispatcher.BeginInvoke(() => Notice("Ivo closed unexpectedly last time", string.Join("\n", lines.Skip(1).Take(3)) + "\n\nFull report: crash.log in Ivo's folder."), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        Voice.Volume = S.VoiceVolume / 100.0;
        // the voice loads when the mouse reaches a speaker button (not at start-up), so it's ready by the click
        EventManager.RegisterClassHandler(typeof(Button), MouseEnterEvent, new MouseEventHandler((s, _) =>
        {
            if (s is Button { Content: "\uE767" or "Try" } || s == VoiceSample || s == TryReadAloud) Voice.Warm(S.Voice);
        }));
        InboxReadBtn.Visibility = S.ReadAloud ? Visibility.Visible : Visibility.Collapsed;
        Inbox.Changed += () => Dispatcher.BeginInvoke(DrawInbox);
        hotkeyOk = RegisterHotKey(hwnd, HotkeyId, 0x2 | 0x4 | 0x4000, 0x20);   // Ctrl+Shift+Space, no auto-repeat
        companionKeyOk = RegisterHotKey(hwnd, CompanionKeyId, 0x1 | 0x2 | 0x4000, 0x20);   // Ctrl+Alt+Space
        RegisterHotKey(hwnd, SnipKeyId, 0x1 | 0x2 | 0x4000, 0x53);   // Ctrl+Alt+S: screenshot to chat
        RegisterHotKey(hwnd, CaptureKeyId, 0x1 | 0x2 | 0x4000, 0x4E);   // Ctrl+Alt+N: quick capture
        AddClipboardFormatListener(hwnd);
        HotkeyHint.Text = hotkeyOk ? "Press Ctrl+Shift+Space anywhere to show or hide Ivo."
                                   : "Ctrl+Shift+Space is already used by another app, so Ivo's shortcut is off.";
        tray = new() { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "Ivo", Visible = true };
        tray.MouseClick += (_, a) => { if (a.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow(); else ShowTrayMenu(); };
        StartSchedule();
        if (Environment.GetCommandLineArgs().Contains("--tray")) Dispatcher.BeginInvoke(Hide);   // started with Windows: wait in the tray
    }

    // ---------- the tray menu (TrayMenu.cs draws it) ----------
    TrayMenu? trayMenu;

    // the chat moves to the companion; this pane gets a fresh one so the same chat isn't open twice
    public void OpenInCompanion(Chat c)
    {
        AllPanes.FirstOrDefault(p => p.Chat == c)?.ShowChat(NewChat());
        Companion().Open(c);
    }

    // one companion; while it's on screen the "open in companion" options are hidden everywhere else
    Companion Companion()
    {
        if (companion != null) return companion;
        companion = new Companion(this);
        companion.IsVisibleChanged += (_, _) => { foreach (var p in AllPanes) p.ShowPopOut(!companion.IsVisible); };
        return companion;
    }
    void ShowTrayMenu()
    {
        var m = trayMenu ??= new TrayMenu();
        m.Clear();
        var next = S.Schedules.Where(x => x.On).OrderBy(x => x.When).FirstOrDefault();
        // when, never the task's prompt text
        m.Header("Ivo", next == null ? "Nothing scheduled" : $"Next {(next.Kind == "task" ? "daily task" : next.Kind)} · {Countdown(next.When)}");
        m.Item("\uE8A7", "Open Ivo", "Ctrl+Shift+Space", ShowWindow);
        m.Item("\uEA3A", "Companion", "Ctrl+Alt+Space", () => Companion().Toggle());
        m.Item("\uE710", "Quick capture", "Ctrl+Alt+N", QuickCapture);
        m.Line();
        m.Item("\uE710", "New chat", "", () => { ShowWindow(); active.ShowChat(NewChat()); active.FocusInput(); });
        m.Item("\uED1A", "Incognito chat", "", () => { ShowWindow(); if (!active.Chat.Incognito) Incognito_Click(this, new RoutedEventArgs()); });
        m.Item("\uE706", "Send morning briefing now", "", BriefingNow);
        m.Item("\uE715", "Inbox", Inbox.Unread > 0 ? $"{Inbox.Unread} new" : "", () => OpenInbox());
        m.Item("\uEA8F", "Reminders", "", () => OpenPlannerAnywhere("reminder"));
        m.Item("\uE706", "Daily tasks", "", () => OpenPlannerAnywhere("task"));
        m.Item("\uE943", "Dev tasks", "", () => OpenPlannerAnywhere("dev"));
        m.Item("\uE73A", "To-do list", S.Todos.Count(t => !t.Done) is > 0 and var n ? $"{n} open" : "", () => OpenPlannerAnywhere("todo"));
        m.Line();
        m.Switch("\uE7E8", "Run on startup", StartsWithWindows(), SetStartup);
        m.Switch("\uE8A5", "Keep running in tray", S.CloseToTray, on => TrayBox.IsChecked = on);
        m.Item("\uE713", "Settings", "", () => { ShowWindow(); OpenSettings(); });
        m.Line();
        m.Item("\uE8BB", "Quit Ivo", "", () => { quitting = true; Close(); }, Shell.B("#FF8C82"));
        m.ShowAtCursor();
    }

    // ---------- run on startup (the per-user Run key; starts in the tray) ----------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool StartsWithWindows()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Ivo") is string v && v.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
    }

    void SetStartup(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Ivo", $"\"{Environment.ProcessPath}\"" + (S.StartInTray ? " --tray" : "")); else k.DeleteValue("Ivo", false);
        if (StartupBox != null) { loading = true; StartupBox.IsChecked = on; loading = false; }
        if (StartTrayBox != null) { loading = true; StartTrayBox.IsChecked = S.StartInTray; StartTrayBox.IsEnabled = on; loading = false; }
    }

    void Clock_Checked(object s, RoutedEventArgs e)
    {
        if (loading || s is not RadioButton { Tag: string c }) return;
        S.Clock = c; When.TwelveHour = c == "12"; SaveSettings();
        SchedTime.Build(); BriefingTimeBox.Build();
        foreach (var p in AllPanes) p.ShowChat(p.Chat);   // redraw the chats' times
    }

    void RestOrbs(object? s, EventArgs e) { foreach (var p in AllPanes) p.Orb(this, default); }   // minimised: nothing turns

    void Splash_Checked(object s, RoutedEventArgs e)
    {
        if (loading || s is not RadioButton { Tag: string style }) return;
        S.Splash = style; SaveSettings();
        if (style != "off") Splash.Play(style);   // see it as you pick it
    }

    void SplashPreview_Click(object s, RoutedEventArgs e) => Splash.Play(S.Splash == "off" ? "aurora" : S.Splash);

    public const string DeveloperSite = "https://mihirsh02.github.io/";
    void Developer_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(DeveloperSite) { UseShellExecute = true });

    void CodeOn_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        S.CodeOn = CodeOnBox.IsChecked == true; SaveSettings();
        ApplyCodeOn();
    }

    // Code mode off: no switch, and Ivo stays in Chat
    void ApplyCodeOn()
    {
        ViewSwitch.Visibility = S.CodeOn ? Visibility.Visible : Visibility.Collapsed;
        if (S.CodeOn || !CodeMode) return;
        S.View = "chat"; SaveSettings();
        loading = true; foreach (RadioButton r in ViewChoices.Children) r.IsChecked = (string)r.Tag == "chat"; loading = false;
        ApplyView();
    }

    void OrbMotion_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        S.OrbMotion = OrbMotionBox.IsChecked == true; SaveSettings();
        foreach (var p in AllPanes) p.Orb(this, default);
    }

    void Notify_Changed(object s, RoutedEventArgs e) { if (!loading) { S.DesktopNotify = NotifyBox.IsChecked == true; SaveSettings(); DrawBell(); NotifyKinds.IsEnabled = S.DesktopNotify; } }


    void ApplyTitleButtons()
    {
        IncognitoButton.Visibility = Visibility.Collapsed;   // incognito lives in each chat's header
        BtnSettings.Visibility = Visibility.Visible;
        BtnPlanner.Visibility = S.HiddenButtons.Contains("planner") ? Visibility.Collapsed : Visibility.Visible;
        BtnTodo.Visibility = S.HiddenButtons.Contains("todo") ? Visibility.Collapsed : Visibility.Visible;
        BtnSpotify.Visibility = SpotifyOn && !S.HiddenButtons.Contains("spotify") ? Visibility.Visible : Visibility.Collapsed;   // only once Spotify is connected
        BtnInbox.Visibility = Visibility.Visible;
        BtnStars.Visibility = S.HiddenButtons.Contains("stars") ? Visibility.Collapsed : Visibility.Visible;
        BtnCalendar.Visibility = S.HiddenButtons.Contains("calendar") ? Visibility.Collapsed : Visibility.Visible;
        BtnActivity.Visibility = S.HiddenButtons.Contains("activity") ? Visibility.Collapsed : Visibility.Visible;
        BtnSchedule.Visibility = S.HiddenButtons.Contains("schedule") ? Visibility.Collapsed : Visibility.Visible;
        BtnNews.Visibility = S.HiddenButtons.Contains("news") ? Visibility.Collapsed : Visibility.Visible;
        BtnMode.Visibility = S.Theme is "soft" or "softdark" && !S.HiddenButtons.Contains("theme") ? Visibility.Visible : Visibility.Collapsed;
        OrderTitleButtons();
        BuildTitleOrderList();
        loading = true;
        NotifyBox.IsChecked = S.DesktopNotify;
        OrbMotionBox.IsChecked = S.OrbMotion;
        CodeOnBox.IsChecked = S.CodeOn;
        foreach (RadioButton rb in SplashChoices.Children) rb.IsChecked = (string)rb.Tag == S.Splash;
        StateChanged -= RestOrbs; StateChanged += RestOrbs;
        if (S.Clock != "") When.TwelveHour = S.Clock == "12";
        foreach (RadioButton c in ClockChoices.Children) c.IsChecked = (string)c.Tag == (When.TwelveHour ? "12" : "24");
        loading = false;
        ApplyCodeOn();
    }

    void Startup_Changed(object s, RoutedEventArgs e) { if (!loading) SetStartup(StartupBox.IsChecked == true); }

    // how it starts with Windows: in the tray, or with its window open (rewrites the Run entry)
    void StartTray_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        S.StartInTray = StartTrayBox.IsChecked == true; SaveSettings();
        if (StartsWithWindows()) SetStartup(true);
    }

    nint WndProc(nint h, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x0024) { MaxSize(l); handled = true; }   // WM_GETMINMAXINFO
        // the whole window is client area (WindowChrome), so Windows must never paint its own frame: on some builds it
        // draws the classic caption buttons and a title-bar edge over ours when the window activates or repaints
        if (msg == 0x0085) { handled = true; return 0; }   // WM_NCPAINT
        if (msg == 0x0086) { handled = true; return 1; }   // WM_NCACTIVATE: allow the change, skip the frame paint
        if (msg is 0x00AE or 0x00AF) { handled = true; return 0; }   // WM_NCUAHDRAWCAPTION / WM_NCUAHDRAWFRAME: themed caption paint
        // WM_SETTEXT / WM_SETICON: DefWindowProc paints the caption straight away, skipping WM_NCPAINT; hide WS_VISIBLE
        // while it runs so it has nothing to draw on (the trick Chromium uses)
        if (msg is 0x000C or 0x0080)
        {
            var style = GetWindowLongPtr(h, -16);
            SetWindowLongPtr(h, -16, style & ~0x10000000);
            var r = DefWindowProc(h, msg, w, l);
            SetWindowLongPtr(h, -16, style);
            handled = true;
            return r;
        }
        if (msg == 0x031D) ClipboardChanged();                 // WM_CLIPBOARDUPDATE
        if (msg == 0x312 && (int)w == SnipKeyId) { handled = true; StartSnip(); }
        if (msg == 0x312 && (int)w == CaptureKeyId) { handled = true; QuickCapture(); }
        if (msg == 0x312 && (int)w == CompanionKeyId)
        {
            handled = true;
            if (locked) ShowWindow(); else Companion().Toggle();   // locked: the lock screen, not a chat
        }
        if (msg == 0x312 && (int)w == HotkeyId)   // WM_HOTKEY
        {
            handled = true;
            if (IsVisible && IsActive && WindowState != WindowState.Minimized) Hide(); else ShowWindow();
        }
        return 0;
    }

    public void ShowWindow()
    {
        if (closed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        active.FocusInput();
    }

    // Closing hides to the tray (when that's on); Quit in the tray menu really exits.
    void Window_Closing(object s, CancelEventArgs e)
    {
        if (S.CloseToTray && !quitting)
        {
            e.Cancel = true;
            CloseDropdowns();
            Hide();   // quietly: the tray icon says it's still there
            return;
        }
        foreach (var p in AllPanes) p.Stop();
    }

    bool closed;
    void Window_Closed(object s, EventArgs e)
    {
        closed = true;
        if (hotkeyOk) UnregisterHotKey(hwnd, HotkeyId);
        if (companionKeyOk) UnregisterHotKey(hwnd, CompanionKeyId);
        UnregisterHotKey(hwnd, CaptureKeyId);
        companion?.Close();
        tray?.Dispose();
        Mcp.StopAll();
        CloseAllTerms();
        // really gone: end the process too. Hidden windows (the tray menu, a notification) would otherwise keep it alive
        // with no window, holding the one-Ivo lock, so the next launch would only poke a closed window.
        EndPortal();   // the other side hears that you left
        Application.Current.Shutdown();
    }

    // Full screen (F11, Esc to leave, and a call's full screen): the whole monitor, taskbar included, with no minimise,
    // maximise or close buttons; back to how the window was after
    bool fullScreen;
    WindowState fullWas;
    public void FullScreen(bool on)
    {
        if (on == fullScreen) return;
        if (on) fullWas = WindowState;
        fullScreen = on;
        foreach (var b in new[] { MinBtn, MaxBtn, CloseBtn }) b.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusRail.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        WindowState = WindowState.Normal;   // maximising again asks the size anew (MaxSize, below)
        WindowState = on ? WindowState.Maximized : fullWas;
    }

    // Maximised, the window fills the screen's work area exactly (WM_GETMINMAXINFO below), so no edge correction is needed.
    void Window_StateChanged(object s, EventArgs e)
    {
        CloseDropdowns();
        var max = WindowState == WindowState.Maximized;
        MaxBtn.Content = max ? "" : ""; MaxBtn.ToolTip = max ? "Restore down" : "Maximize";
    }

    // With a custom title bar, Windows would place a maximised window past every screen edge and over the whole screen,
    // which also covers the strip an auto-hiding taskbar needs to pop up. Size it to the monitor's work area instead, and
    // when the taskbar auto-hides (the work area is then the whole screen) leave it one pixel on its edge.
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public uint Size; public nint Hwnd; public uint Callback, Edge; public RECT Rc; public int LParam; }
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("shell32.dll")] static extern nuint SHAppBarMessage(uint msg, ref APPBARDATA data);
    void MaxSize(nint l)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;   // MONITOR_DEFAULTTONEAREST
        var w = fullScreen ? info.Monitor : info.Work;   // full screen (F11): the whole monitor, over the taskbar
        var bar = new APPBARDATA { Size = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (!fullScreen && (SHAppBarMessage(4, ref bar) & 1) != 0 && SHAppBarMessage(5, ref bar) != 0)   // ABM_GETSTATE has ABS_AUTOHIDE; ABM_GETTASKBARPOS for its edge
        {
            var onThis = bar.Rc.Left < info.Monitor.Right && bar.Rc.Right > info.Monitor.Left && bar.Rc.Top < info.Monitor.Bottom && bar.Rc.Bottom > info.Monitor.Top;
            if (onThis) switch (bar.Edge) { case 0: w.Left++; break; case 1: w.Top++; break; case 2: w.Right--; break; default: w.Bottom--; break; }   // left, top, right, bottom
        }
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(l);
        mmi.MaxPosition = new POINT { X = w.Left - info.Monitor.Left, Y = w.Top - info.Monitor.Top };
        mmi.MaxSize = new POINT { X = w.Right - w.Left, Y = w.Bottom - w.Top };
        Marshal.StructureToPtr(mmi, l, false);
    }

    void Tray_Changed(object s, RoutedEventArgs e) { if (!loading) { S.CloseToTray = TrayBox.IsChecked == true; SaveSettings(); } }
}
