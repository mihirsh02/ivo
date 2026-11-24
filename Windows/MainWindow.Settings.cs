using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Ivo;

// Settings: its side panel and sections, providers and keys, appearance, data & privacy, about.
public partial class MainWindow
{
    // ---------- appearance ----------

    void Theme_Checked(object s, RoutedEventArgs e)
    {
        if (loading) return;
        var tag = (string)((RadioButton)s).Tag;
        SetTheme(tag == "soft" && S.Theme == "softdark" ? "softdark" : tag);   // Soft keeps the mode it was left in
    }

    // every theme change crossfades: the old look is held as a picture over the new one and fades out
    void SetTheme(string theme)
    {
        if (Root.ActualWidth > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var shot = new RenderTargetBitmap((int)(Root.ActualWidth * dpi.DpiScaleX), (int)(Root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            shot.Render(Root);
            var veil = new Image { Source = shot, IsHitTestVisible = false, Stretch = Stretch.Fill };
            Grid.SetRowSpan(veil, 99); Grid.SetColumnSpan(veil, 99); Panel.SetZIndex(veil, 9999);
            Root.Children.Add(veil);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            fade.Completed += (_, _) => Root.Children.Remove(veil);
            veil.BeginAnimation(OpacityProperty, fade);
        }
        S.Theme = theme;
        App.ApplyTheme(S.Theme);
        UpdateGlow();
        foreach (var p in AllPanes) p.ApplyTheme(S.Theme);
        companion?.Restyle();
        SaveSettings();
    }

    // the title bar's sun / moon: Soft's light and dark modes
    void Mode_Click(object s, RoutedEventArgs e) => SetTheme(S.Theme == "softdark" ? "soft" : "softdark");

    // Aurora + flowing wordmark only run in Glow; Dark/Light stay still (and lighter on the GPU).
    void UpdateGlow()
    {
        var glow = S.Theme == "glow";
        BtnMode.Visibility = S.Theme is "soft" or "softdark" && !S.HiddenButtons.Contains("theme") ? Visibility.Visible : Visibility.Collapsed;
        DrawModeSwitch(animate: IsLoaded);
        GlowLayer.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        BrandVersion.Text = $"Version {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)} · {S.Providers.Count} providers";
        (TranslateTransform t, double x, double y, double sx, double sy)[] blobs = [(B1, 260, 180, 14, 11), (B2, -300, 220, 16, 12), (B3, 340, -260, 13, 15)];
        foreach (var (t, x, y, sx, sy) in blobs)
        {
            t.BeginAnimation(TranslateTransform.XProperty, glow ? Drift(x, sx) : null);
            t.BeginAnimation(TranslateTransform.YProperty, glow ? Drift(y, sy) : null);
        }
        WordFlow.BeginAnimation(TranslateTransform.XProperty, glow ? App.Loop(new DoubleAnimation(0, 2, TimeSpan.FromSeconds(6)) { RepeatBehavior = RepeatBehavior.Forever }) : null);
    }

    // Text size: fonts and icons are drawn at the chosen size (App.ScaleText), not the window shrunk as a picture,
    // so they stay pixel-sharp at every size.
    void ApplyZoom()
    {
        App.Zoom = S.Zoom;
        App.Rescale();
        App.Crisp(this);
        App.Line = 2 / VisualTreeHelper.GetDpi(this).DpiScaleX;   // two whole screen pixels
    }
    // ---------- size: 70% to 130% of the default (Settings → Appearance, or Ctrl + / Ctrl − / Ctrl 0) ----------
    const double DefaultZoom = 0.9;   // what "100%" is
    int ZoomPercent => (int)Math.Round(S.Zoom / DefaultZoom * 10) * 10;
    void SetZoomPercent(int pct)
    {
        S.Zoom = Math.Round(DefaultZoom * Math.Clamp(pct, 70, 130) / 100.0, 3);
        ApplyZoom(); SaveSettings(); DrawZoomScale();
    }

    // Ctrl + / Ctrl − step it by 10%, Ctrl 0 goes back to 100%
    void StepZoom(int step) => SetZoomPercent(step == 0 ? 100 : ZoomPercent + step * 10);

    // Three previews (Dense, Default, Spacious) over seven stops, like Spotify's, and Reset
    void DrawZoomScale()
    {
        var at = ZoomPercent;
        ZoomScale.Children.Clear(); ZoomScale.ColumnDefinitions.Clear(); ZoomScale.RowDefinitions.Clear();
        for (int c = 0; c < 7; c++) ZoomScale.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(34), GridLength.Auto, GridLength.Auto }) ZoomScale.RowDefinitions.Add(new RowDefinition { Height = h });
        var accent = Shell.B("#8B7CFF");
        void Put(UIElement el, int row, int col, int span = 1) { Grid.SetRow(el, row); Grid.SetColumn(el, col); Grid.SetColumnSpan(el, span); ZoomScale.Children.Add(el); }

        // the previews: a small sketch of the layout, its tiles filled in the accent when it's the one in use
        foreach (var (pct, name, rows, cols) in new[] { (70, "Dense", 4, 5), (100, "Default", 3, 4), (130, "Spacious", 2, 3) })
        {
            var on = at <= 80 && pct == 70 || at is >= 90 and <= 110 && pct == 100 || at >= 120 && pct == 130;
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Rows = rows, Columns = cols, Margin = new Thickness(5) };
            for (int i = 0; i < rows * cols; i++)
            {
                var tile = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(1.5) };
                if (on) tile.Background = accent; else tile.SetResourceReference(Border.BackgroundProperty, "Separator");
                tiles.Children.Add(tile);
            }
            var frame = new Border { Width = 76, Height = 56, CornerRadius = new CornerRadius(6, 6, 6, 6.01), BorderThickness = new Thickness(1.5), Child = tiles, Cursor = Cursors.Hand, ToolTip = $"{name} ({pct}%)" };
            if (on) frame.BorderBrush = accent; else frame.SetResourceReference(Border.BorderBrushProperty, "Separator");
            frame.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            frame.MouseLeftButtonUp += (_, _) => SetZoomPercent(pct);
            var label = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, on ? "Fg" : "Dim");
            var col = (pct - 70) / 10;
            frame.HorizontalAlignment = HorizontalAlignment.Center;
            Put(frame, 0, col); Put(label, 1, col);
        }

        // the track and its seven stops; the current one is a ring in the accent
        var line = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40, 0, 40, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Separator");
        Put(line, 2, 0, 7);
        for (int i = 0; i < 7; i++)
        {
            var pct = 70 + i * 10;
            var on = pct == at;
            var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(on ? 3.5 : 2), HorizontalAlignment = HorizontalAlignment.Center,
                                   VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = $"{pct}%" };
            dot.SetResourceReference(Border.BackgroundProperty, "BoxBg");
            if (on) dot.BorderBrush = accent; else dot.SetResourceReference(Border.BorderBrushProperty, "Dim");
            dot.MouseLeftButtonUp += (_, _) => SetZoomPercent(pct);
            var text = new TextBlock { Text = $"{pct}%", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, on ? "Fg" : "Dim");
            Put(dot, 2, i); Put(text, 3, i);
        }
        var reset = new Button { Content = "Reset", Padding = new Thickness(16, 5, 16, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
                                 IsEnabled = at != 100, ToolTip = "Back to 100% (Ctrl 0)" };
        reset.Click += (_, _) => SetZoomPercent(100);
        Put(reset, 4, 4, 3);
    }

    // Glow theme: the dot grid lights up around the cursor (eased per frame, fades when the mouse leaves).
    Point mouse, spotAt = new(-500, -500);
    bool following;
    void Window_MouseMove(object s, System.Windows.Input.MouseEventArgs e)
    {
        if (S.Theme != "glow") return;
        mouse = e.GetPosition(this);
        if (DotSpot.Opacity == 0) { spotAt = mouse; DotSpot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250))); }
        if (!following) { following = true; CompositionTarget.Rendering += FollowCursor; }
    }
    void FollowCursor(object? s, EventArgs e)
    {
        spotAt += (mouse - spotAt) * 0.5;
        SpotMask.Center = SpotMask.GradientOrigin = spotAt;
        if ((mouse - spotAt).Length < 0.5) { following = false; CompositionTarget.Rendering -= FollowCursor; }   // idle: stop ticking
    }
    void Window_MouseLeave(object s, System.Windows.Input.MouseEventArgs e) =>
        DotSpot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
    static DoubleAnimation Drift(double to, double seconds) =>   // the aurora's slow drift: 20 frames a second is plenty
        App.Loop(new DoubleAnimation(0, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }, 20);

    void Layout_Checked(object s, RoutedEventArgs e)
    {
        if (loading) return;
        S.Layout = (string)((RadioButton)s).Tag;
        if (!IsMulti) foreach (var p in panes.Where(p => p != active).ToList()) ClosePane(p);
        LayoutPanes();
        UpdateLayoutHint();
        SaveSettings();
    }

    void UpdateLayoutHint() => LayoutHint.Text = IsMulti
        ? $"Right-click a chat in Recent chats → Open in side pane. Up to {MaxPanes} side by side at this window size. Drag a chat's header to reorder."
        : "One chat at a time.";

    // ---------- settings: side panel of sections ----------
    static readonly Dictionary<string, (string Title, string Hint)> Sections = new()
    {
        ["overview"] = ("Settings", ""),
        ["profile"] = ("Profile & memory", "Who you are, what models remember."),
        ["providers"] = ("API keys", "Keys are encrypted and only sent to their company."),
        ["gateways"] = ("Gateways", "One key, many models. Self-hosted ones need an address."),
        ["agents"] = ("CLI tools", "Claude Code, Copilot CLI, Codex… with their own sign-in."),
        ["subagents"] = ("Agents", "Specialists your chat hands work to."),
        ["routing"] = ("Routing", "Auto, key pools, and Ivo as a gateway."),
        ["discover"] = ("Scan this PC", "Find AI tools here, and check which models work."),
        ["skills"] = ("Skills", "Know-how loaded when a task needs it."),
        ["tools"] = ("Tools (MCP)", "Extra tools. Work in chats with a folder open."),
        ["connections"] = ("Connections", "Changes ask first."),
        ["prompts"] = ("Prompts", "Insert with /."),
        ["general"] = ("General", "Startup, web pages and clock."),
        ["calendar"] = ("Calendar", "Your calendars, holidays and your regional calendar."),
        ["spending"] = ("Spending", "Limits from estimated prices, per day and per provider."),
        ["notifications"] = ("Notifications & sounds", "What pops up, and Ivo's chimes."),
        ["assistant"] = ("Blub", "Blub's personality, packs, your day, introduction and tour."),
        ["widgets"] = ("Widgets", "Cards on your desktop, behind your windows."),
        ["chats"] = ("Chats", "Defaults for every chat."),
        ["appearance"] = ("Appearance", "Look and background."),
        ["tasks"] = ("Tasks", "Models for tasks, and the morning briefing."),
        ["voice"] = ("Voice & dictation", "Read aloud, and the mic button."),
        ["usage"] = ("Usage", "Tokens and estimated spend, from your chats."),
        ["shortcuts"] = ("Keyboard shortcuts", ""),
        ["data"] = ("Data & storage", "Where Ivo keeps things; back up and restore."),
        ["lock"] = ("App lock", "A PIN or password before anything shows."),
        ["privacy"] = ("Permissions", "Everything stays on this PC unless you send it to a provider."),
        ["about"] = ("About", ""),
    };
    string section = "overview";
    string keysCategory = "providers";   // which presets and providers the shared key form shows: providers | gateways

    public void OpenSettings() => OpenSettings(null);
    public void OpenSettings(string? to)
    {
        SettingsPanel.Visibility = Visibility.Visible;
        var target = Nav.Children.OfType<RadioButton>().First(r => (string)r.Tag == (to ?? section));
        if (target.IsChecked == true) ShowSection(); else target.IsChecked = true;   // fresh counts, usage and balances either way
    }
    void OpenSettings_Click(object s, RoutedEventArgs e) => OpenSettings();
    void CloseSettings_Click(object s, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    void Nav_Checked(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { Tag: string tag }) { section = tag; ShowSection(); }
    }

    // each section's panel on the Settings page
    Dictionary<string, FrameworkElement> SectionPanels() => new()
    {
            ["overview"] = OverviewTab, ["profile"] = ProfileTab, ["providers"] = KeysTab, ["gateways"] = KeysTab, ["agents"] = AgentsTab, ["subagents"] = SubagentsTab, ["discover"] = DiscoverTab, ["routing"] = RoutingTab, ["skills"] = SkillsTab, ["tools"] = ToolsTab, ["connections"] = ConnectionsTab,
            ["prompts"] = PromptsTab, ["appearance"] = LookTab, ["general"] = GeneralTab, ["calendar"] = CalendarTab, ["spending"] = SpendingTab, ["lock"] = LockTab, ["notifications"] = NotificationsTab, ["chats"] = ChatsTab, ["assistant"] = AssistantTab, ["widgets"] = WidgetsTab, ["voice"] = VoiceTab, ["tasks"] = TasksTab, ["shortcuts"] = ShortcutsTab, ["usage"] = UsageTab, ["data"] = DataTab, ["privacy"] = PrivacyTab, ["about"] = AboutTab,
    };

    void ShowSection()
    {
        if (KeysTab == null) return;
        var (title, hint) = Sections[section];
        SectionTitle.Text = title; SectionHint.Text = hint; SectionHint.Visibility = hint == "" ? Visibility.Collapsed : Visibility.Visible;
        SectionHint.Visibility = hint == "" ? Visibility.Collapsed : Visibility.Visible;
        var panels = SectionPanels();
        foreach (var pnl in panels.Values.Distinct()) pnl.Visibility = Visibility.Collapsed;
        panels[section].Visibility = Visibility.Visible;
        SectionScroll.ScrollToTop();
        UpdateCounts();
        switch (section)
        {
            case "connections": BuildConnections(); break;
            case "routing": ShowRouting(); break;
            case "spending": ShowSpending(); break;
            case "calendar": ShowCalendarSettings(); break;
            case "chats": ShowChatPrefs(); break;
            case "discover": if (scanStop == null) DrawModelPicks(); break;
            case "providers" or "gateways":
                if (section == "providers") ShowOllama();
                if (keysCategory != section) { keysCategory = section; editing = null; AddButtonText.Text = "Add"; AddKey.Password = ""; FillPresets(); }
                AddLabel.Text = section == "gateways" ? "Add a gateway" : "Add an API key";
                AddedLabel.Text = section == "gateways" ? "ADDED GATEWAYS" : "ADDED PROVIDERS";
                RefreshProviderList();
                break;
            case "agents": RefreshProviderList(); break;
            case "subagents": ShowSubagents(); break;
            case "skills": RefreshSkills(); break;
            case "data": ShowData(); break;
            case "usage": ShowUsage(); break;
            case "tools": ShowPlugins(); break;
            case "about": ShowAbout(); break;
            case "assistant": DrawAssistantCard(); break;
            case "widgets": WidgetsCard.Child = WidgetsList(); break;
            case "voice": ShowVoice(); break;
            case "notifications": ShowNotifyKinds(); break;
            case "privacy": ShowPrivacy(); break;
            case "tasks": ShowTaskModels(); break;
        }
        // every page in Assistant's look: cards with their icon and what they're for, every on/off a switch
        SectionScroll.Resources[typeof(CheckBox)] = FindResource("ToggleSwitch");
        DressCards(panels[section]);
    }

    // Counts beside the side panel's sections.
    void UpdateCounts()
    {
        string N(int n) => n == 0 ? "" : n.ToString();
        Countproviders.Text = N(S.Providers.Count(p => CategoryOf(p) == "providers"));
        Countgateways.Text = N(S.Providers.Count(p => CategoryOf(p) == "gateways"));
        Countagents.Text = N(S.Providers.Count(p => CategoryOf(p) == "agents"));
        Countsubagents.Text = N(Subagents.All(active?.Chat.Workspace).Count);
        Countskills.Text = N(Skills.All().Count);
        Counttools.Text = N(Mcp.Servers.Count(m => m.Ready));
        Countprompts.Text = N(S.Prompts.Count);
        Countconnections.Text = N(Connections.Accounts.Count());
    }

    // Overview: one card per section (icon, name, what it's for); a click opens it.
    void BuildOverview()
    {
        (string Tag, string Icon, string Text)[] cards =
        [
            ("providers", "\uE8D7", "OpenAI, Anthropic, Gemini, local models."),
            ("gateways", "\uE774", "OpenRouter, Vercel, Requesty…"),
            ("agents", "\uE756", "Claude Code, Copilot CLI and more."),
            ("subagents", "\uE99A", "Specialists for your chats."),
            ("skills", "\uE82D", "Reusable know-how."),
            ("tools", "\uE943", "MCP servers: GitHub, databases, browsers."),
            ("connections", "\uE71B", "GitHub, GitLab, Notion."),
            ("prompts", "\uE8F2", "Saved instructions."),
            ("routing", "\uE8AB", "Auto, fallbacks, key pools, gateway."),
            ("spending", "\uE8C7", "Daily limit, budgets, pricey models."),
            ("discover", "\uE773", "Find AI tools already installed."),
            ("general", "\uE713", "Startup, web pages, clock."),
            ("calendar", "\uE787", "Calendars, holidays, regional calendar."),
            ("lock", "\uE72E", "PIN or password, auto-lock."),
            ("assistant", "\uE99A", "Blub's packs, introduction and tour."),
            ("chats", "\uE8BD", "Default model, sending, spelling."),
            ("appearance", "\uE790", "Theme, layout, text size."),
            ("tasks", "\uE706", "Models for tasks, the morning briefing."),
            ("voice", "\uE767", "Read aloud in 40+ languages."),
            ("privacy", "\uEA18", "What leaves this PC, activity log."),
            ("data", "\uE8B7", "Storage, backups, export."),
        ];
        foreach (var (tag, icon, text) in cards)
        {
            var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new System.Windows.Documents.Run(icon) { FontFamily = App.IconFont, FontWeight = FontWeights.Normal });
            title.Inlines.Add(new System.Windows.Documents.Run("   " + Sections[tag].Title));
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var desc = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), LineHeight = 18 };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 14, 16, 16), Margin = new Thickness(0, 0, 10, 10), BorderThickness = new Thickness(1),
                                    Cursor = Cursors.Hand, Child = new StackPanel { Children = { title, desc } } };
            card.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            card.MouseEnter += (_, _) => card.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x7C, 0x5C, 0xFF));
            card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            card.MouseLeftButtonUp += (_, _) => OpenSettings(tag);
            OverviewCards.Children.Add(card);
        }
    }

    // The key form's choices for the current section; Custom fits both.
    void FillPresets()
    {
        AddProvider.ItemsSource = Api.Presets.Where(p => p.Name == Api.CustomName || p.Gateway == (keysCategory == "gateways")).Select(p => p.Name).ToList();
        AddProvider.SelectedIndex = 0;
    }

    // Which section a provider is listed in.
    static string CategoryOf(ProviderCfg p) =>
        p.Kind == "cli" ? "agents" : Api.Presets.FirstOrDefault(x => x.Name == p.Name)?.Gateway == true ? "gateways" : "providers";

    void ShowShortcuts()
    {
        (string Keys, string What)[] list =
        [
            ("Ctrl + Shift + Space", "Show or hide Ivo from anywhere"),
            ("Ctrl + Alt + Space", "Companion: a small floating chat, from anywhere"),
            ("Ctrl + +  /  Ctrl + −  /  Ctrl + 0", "Size: bigger, smaller, back to 100% (70% to 130%)"),
            ("Ctrl + Alt + N", "Quick capture: a to-do, a reminder with a time (\"call mom at 6pm\", \"when I open Teams\"), or Ctrl+Enter to ask Ivo"),
            ("/compare  your question", "Ask up to three models at once, side by side"),
            ("Ctrl + P  (Code mode)", "Go to any file in the folder"), ("Ctrl + H  (Code mode)", "Find and replace in the open file"),
            ("Enter", "Send the message"), ("Shift + Enter", "New line in the message"),
            ("Ctrl + 1 … 6", "Quick actions, in the bar's order: Quick answer, Think deeply, Plan first, Autopilot, Safe review, Research (again to turn off)"),
            ("Ctrl + Alt + S", "Screenshot to chat: clip part of the screen, it lands in the message box"),
            ("F6", "Move between the chat search, the message box and (when open) Settings' search"),
            ("/clip  address", "Save a web page into your knowledge folder"),
            ("Ctrl + Shift + Enter", "Send the message as a task for an agent"), ("Ctrl + Shift + A", "Open the agent map: this chat's agents and what they're doing"),
            ("{{DATE}}  {{TIME}}  {{DAY}}", "In a message, saved prompt or task: today's date, the time or the weekday are filled in when sent"),
            ("↑ in an empty message box", "Edit the last message you sent"),
            ("/", "Saved prompts (at the start of the message box)"), ("↑ ↓  then Enter or Tab", "Pick a saved prompt"),
            ("Esc", "Close Settings, a dialog, a menu or a popup; clear the chat search"),
            ("Enter in Search chats", "Open the first matching chat"),
            ("Double-click a chat's title", "Rename it"), ("Win + H", "Voice typing into the message box"),
            ("Enter / Esc while editing a message", "Resend it / cancel"),
        ];
        foreach (var (keys, what) in list)
        {
            var k = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                                 Child = new TextBlock { Text = keys, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 12 } };
            k.SetResourceReference(Border.BackgroundProperty, "Glass");
            k.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            ((TextBlock)k.Child).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var w = new TextBlock { Text = what, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            w.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(w, 1);
            row.Children.Add(k); row.Children.Add(w);
            ShortcutsTab.Children.Add(row);
        }
    }

    // ---------- Data & storage, About ----------
    void ShowData()
    {
        DataPath.Text = Store.Dir; SkillsPath.Text = Skills.Root;
        DevBox.IsChecked = S.Developer; DevTools.Visibility = S.Developer ? Visibility.Visible : Visibility.Collapsed;
        KnowledgePath.Text = S.KnowledgeFolder == "" ? "None yet: pick a folder of notes or documents" : S.KnowledgeFolder;
        KnowledgeClear.Visibility = S.KnowledgeFolder == "" ? Visibility.Collapsed : Visibility.Visible;
        EmbedModelBox.Text = S.EmbedModel; KnowledgeStatus.Text = Knowledge.Status;
        FailLogInfo.Text = System.IO.File.Exists(Store.FailLog) ? $"{System.IO.File.ReadLines(Store.FailLog).Count()} entries · {new System.IO.FileInfo(Store.FailLog).Length / 1024} KB" : "No failures logged yet";
        ChatCounts.Text = $"{allChats.Count} saved chat{(allChats.Count == 1 ? "" : "s")} · {allChats.Count(c => c.Archived)} archived · {allChats.Sum(c => c.Messages.Count)} messages";
    }
    void OpenData_Click(object s, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{Store.Dir}\"");
    void Dev_Changed(object s, RoutedEventArgs e) { S.Developer = DevBox.IsChecked == true; SaveSettings(); ShowData(); }
    void FailLog_Click(object s, RoutedEventArgs e)
    {
        if (!System.IO.File.Exists(Store.FailLog)) { Store.Fail("Developer", "Opened the log before anything failed"); }
        Process.Start(new ProcessStartInfo(Store.FailLog) { UseShellExecute = true });
    }
    void FailLogClear_Click(object s, RoutedEventArgs e) { try { System.IO.File.Delete(Store.FailLog); } catch { } ShowData(); }

    // One zip to send with a problem: the failure and crash logs, and the settings with every key, token and memory removed.
    async void BugReport_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"Ivo report {DateTime.Now:yyyy-MM-dd HHmm}.zip", Filter = "Zip|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var safe = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(S))!;
            foreach (var p in safe.Providers) p.Key = p.Key == "" ? "" : "(removed)";
            safe.LockHash = safe.LockWrap = safe.Vault = ""; safe.LockCodes.Clear(); safe.Stars.Clear(); safe.Projects.Clear(); safe.Profile = null; safe.Connections.Clear(); safe.SearchKey = ""; safe.GatewayKey = ""; safe.GoogleTasksClient = ""; safe.GoogleTasksToken = ""; safe.Memories.Clear();
            await Task.Run(() =>
            {
                using var zip = System.IO.Compression.ZipFile.Open(dlg.FileName, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var f in new[] { Store.FailLog, System.IO.Path.Combine(Store.Dir, "crash.log") }.Where(System.IO.File.Exists)) System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, f, System.IO.Path.GetFileName(f));
                using (var w = new System.IO.StreamWriter(zip.CreateEntry("settings-without-keys.json").Open()))
                    w.Write(System.Text.Json.JsonSerializer.Serialize(safe, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                using var i = new System.IO.StreamWriter(zip.CreateEntry("system.txt").Open());
                i.Write($"Ivo {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}\nWindows {Environment.OSVersion}\n.NET {Environment.Version}\n{DateTime.Now:yyyy-MM-dd HH:mm}\n");
            });
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _ = Notice("Couldn't export the report", ex.Message); }
    }

    void Knowledge_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with your notes and documents" };
        if (S.KnowledgeFolder != "") dlg.InitialDirectory = S.KnowledgeFolder;
        if (dlg.ShowDialog(this) == true) { S.KnowledgeFolder = dlg.FolderName; SaveSettings(); ShowData(); KnowledgeRebuild_Click(s, e); }
    }
    void KnowledgeClear_Click(object s, RoutedEventArgs e) { S.KnowledgeFolder = ""; SaveSettings(); ShowData(); }
    void EmbedModel_Changed(object s, RoutedEventArgs e)
    {
        var v = EmbedModelBox.Text.Trim();
        if (v == S.EmbedModel) return;
        if (v != "" && !S.Providers.Any(p => v.StartsWith(p.Name + "/"))) { KnowledgeStatus.Text = "Write it as Provider/model, with a provider you've added."; return; }
        S.EmbedModel = v; SaveSettings();
        KnowledgeRebuild_Click(s, e);
    }
    async void KnowledgeRebuild_Click(object s, RoutedEventArgs e)
    {
        if (!System.IO.Directory.Exists(S.KnowledgeFolder)) { KnowledgeStatus.Text = "Choose a folder first."; return; }
        KnowledgeStatus.Text = "Indexing…";
        try { await Knowledge.Update(CancellationToken.None, rebuild: true); KnowledgeStatus.Text = Knowledge.Status; }
        catch (Exception ex) { KnowledgeStatus.Text = "Couldn't index: " + ex.Message; Store.Fail("Knowledge index", ex.Message); }
    }

    // Search every setting: the groups (from every section) whose words match; an empty box goes back to the chosen section.
    static string TextOf(DependencyObject el)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock t) { sb.Append(t.Text).Append(' '); foreach (var r in t.Inlines.OfType<System.Windows.Documents.Run>()) sb.Append(r.Text).Append(' '); }
            if (d is FrameworkElement { ToolTip: string tip }) sb.Append(tip).Append(' ');
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject cd) Walk(cd); else if (child is string str) sb.Append(str).Append(' ');
        }
        Walk(el);
        return sb.ToString();
    }
    void SettingsSearch_Changed(object s, TextChangedEventArgs e)
    {
        if (KeysTab == null) return;
        var q = SettingsSearch.Text.Trim();
        SettingsSearchHint.Visibility = q == "" ? Visibility.Visible : Visibility.Collapsed;
        var panels = ((Panel)KeysTab.Parent).Children.OfType<Panel>().Where(p => p.Name.EndsWith("Tab")).ToList();
        if (q == "") { foreach (var p in panels) foreach (FrameworkElement c in p.Children) c.Visibility = Visibility.Visible; ShowSection(); return; }
        var n = 0;
        foreach (var p in panels)
        {
            var any = false;
            foreach (FrameworkElement c in p.Children)
            {
                var hit = TextOf(c).Contains(q, StringComparison.OrdinalIgnoreCase);
                c.Visibility = hit ? Visibility.Visible : Visibility.Collapsed; any |= hit; if (hit) n++;
            }
            p.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        SectionTitle.Text = n == 0 ? $"Nothing matches “{q}”" : $"{n} match{(n == 1 ? "" : "es")} for “{q}”";
        SectionHint.Visibility = Visibility.Collapsed;
    }
    async void Calendar_Changed(object s, RoutedEventArgs e)
    {
        var urls = CalendarBox.Text.Split('\n').Select(u => u.Trim()).Where(u => u != "").ToList();
        if (urls.SequenceEqual(S.CalendarUrls)) return;
        S.CalendarUrls = urls; SaveSettings();
        if (urls.Count == 0) { CalendarStatus.Text = ""; return; }
        CalendarStatus.Text = "Checking…";
        var week = await Calendar.Between(urls, DateTime.Today, DateTime.Today.AddDays(7));
        CalendarStatus.Text = $"{week.Count} event{(week.Count == 1 ? "" : "s")} in the next 7 days" + (week.Count == 0 ? " (if that's wrong, check the address; failures are in the log)" : "");
    }
    void ClipHistory_Changed(object s, RoutedEventArgs e)
    {
        S.ClipboardHistory = ClipHistoryBox.IsChecked == true; SaveSettings();
        if (!S.ClipboardHistory) lock (Agent.Clips) Agent.Clips.Clear();
    }

    void SearchKey_Changed(object s, RoutedEventArgs e)
    {
        if (SearchKeyBox.Password == Store.Unprotect(S.SearchKey)) return;
        S.SearchKey = Store.Protect(SearchKeyBox.Password); SaveSettings();
    }

    // Everything that's yours in one file; voice packs and the browser cache are left out (they download again).
    // Keys are encrypted to this Windows account, so a backup restores them only for the same account. With the app lock
    // on, the whole backup is sealed with your PIN or password (an .ivobackup), and restoring asks for it.
    static readonly string[] NotBackedUp = ["voices", "web", "hangs"];

    void Backup_Click(object s, RoutedEventArgs e)
    {
        var sealedBackup = S.LockHash != "";
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"Ivo backup {DateTime.Now:yyyy-MM-dd}" + (sealedBackup ? ".ivobackup" : ".zip"), Filter = sealedBackup ? "Encrypted Ivo backup|*.ivobackup" : "Ivo backup|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        SaveSettings();
        async void Write(string? secret)
        {
            try
            {
                var bytes = await Task.Run(() =>
                {
                    using var mem = new System.IO.MemoryStream();
                    using (var zip = new System.IO.Compression.ZipArchive(mem, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                        foreach (var f in System.IO.Directory.EnumerateFiles(Store.Dir, "*", System.IO.SearchOption.AllDirectories))
                        {
                            var rel = System.IO.Path.GetRelativePath(Store.Dir, f);
                            if (NotBackedUp.Any(d => rel.StartsWith(d + "\\")) || rel.EndsWith(".tmp")) continue;
                            try { System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, f, rel.Replace('\\', '/')); } catch (System.IO.IOException) { }   // a file in use: skip it
                        }
                    return secret == null ? mem.ToArray() : Crypto.SealWithPassword(mem.ToArray(), secret);
                });
                await System.IO.File.WriteAllBytesAsync(dlg.FileName, bytes);
                Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
            }
            catch (Exception ex) { _ = Notice("Couldn't back up", ex.Message); }
        }
        if (!sealedBackup) { Write(null); return; }
        PasskeyLayer("Encrypt this backup", "Enter your PIN or password. You'll need it to restore the backup.", false, "Back up",
            key => { if (!KeyMatches(key)) return false; Write(key); return true; }, () => { });
    }

    async void Restore_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Ivo backup|*.zip;*.ivobackup" };
        if (dlg.ShowDialog(this) != true) return;
        if (!await Confirm("Restore this backup?", "It replaces your chats and settings, and Ivo restarts.")) return;
        var bytes = await System.IO.File.ReadAllBytesAsync(dlg.FileName);
        if (!Crypto.IsPasswordSealed(bytes)) { RestoreFrom(bytes); return; }
        PasskeyLayer("Encrypted backup", "Enter the PIN or password this backup was made with.", false, "Restore",
            key => { if (Crypto.OpenWithPassword(bytes, key) is not { } zip) return false; Dispatcher.BeginInvoke(() => RestoreFrom(zip)); return true; }, () => { });
    }

    void RestoreFrom(byte[] zipBytes)
    {
        try
        {
            using (var zip = new System.IO.Compression.ZipArchive(new System.IO.MemoryStream(zipBytes)))
            {
                if (zip.GetEntry("settings.json") == null) { _ = Notice("Not an Ivo backup", "No settings file in it."); return; }
                // unpacked aside first: a bad archive leaves the current chats alone
                var tmp = System.IO.Path.Combine(Store.Dir, "restore.tmp");
                if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
                System.IO.Compression.ZipFileExtensions.ExtractToDirectory(zip, tmp);
                var chats = System.IO.Path.Combine(Store.Dir, "chats");
                if (System.IO.Directory.Exists(chats)) System.IO.Directory.Delete(chats, true);   // the backup's chats, not a mix
                foreach (var f in System.IO.Directory.GetFiles(tmp, "*", System.IO.SearchOption.AllDirectories))
                {
                    var to = System.IO.Path.Combine(Store.Dir, System.IO.Path.GetRelativePath(tmp, f));
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                    System.IO.File.Move(f, to, true);
                }
                System.IO.Directory.Delete(tmp, true);
            }
            // start again without saving the current settings over the restored ones
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{Environment.ProcessPath}\"") { CreateNoWindow = true, UseShellExecute = false });
            Environment.Exit(0);
        }
        catch (Exception ex) { _ = Notice("Couldn't restore", ex.Message); }
    }

    async void ExportAll_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to export every chat into (as Markdown)" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            foreach (var c in allChats)
            {
                var name = Regex.Replace(c.Title, @"[\\/:*?""<>|]", "").Trim().TrimEnd('…');
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dlg.FolderName, $"{(name == "" ? "chat" : name)} ({c.Id[..6]}).md"), Export.Markdown(c));
            }
            Process.Start("explorer.exe", $"\"{dlg.FolderName}\"");
        }
        catch (Exception ex) { await Notice("Couldn't export the chats", ex.Message); }
    }

    async void DeleteArchived_Click(object s, RoutedEventArgs e)
    {
        var archived = allChats.Where(c => c.Archived).ToList();
        if (archived.Count == 0) { await Notice("Nothing to delete", "No archived chats."); return; }
        if (!await Confirm($"Delete {archived.Count} archived chat{(archived.Count == 1 ? "" : "s")}?", "This can't be undone.", "Delete", danger: true)) return;
        foreach (var c in archived) Store.DeleteChat(c);
        RefreshChats(); ShowData();
    }

    void ForgetChecks_Click(object s, RoutedEventArgs e)
    {
        S.ModelStatus.Clear(); SaveSettings();
        RefreshProviderList();
    }

    string aboutText = "";
    bool aboutHooked;
    void ShowAbout()
    {
        var exe = Environment.ProcessPath!;
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0";
        var built = System.IO.File.GetLastWriteTime(exe).ToString("d MMM yyyy");
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        AboutVersion.Text = $"Version {version}  ·  {built}";
        aboutText = $"Ivo {version} ({built})\n{runtime}\n{os}\nProgram: {exe}\nData: {Store.Dir}";
        // the counts: small chips that slide open under the version while it's hovered (they push the rows down, never cover them)
        AboutStats.Children.Clear();
        foreach (var (n, what) in new (int N, string What)[]
        {
            (S.Providers.Count, "providers"), (allChats.Count, "chats"), (Skills.All().Count, "skills"), (Mcp.Servers.Count(m => m.Ready), "MCP servers"),
            (S.Memories.Count, "memories"), (S.Schedules.Count(x => x.On), "scheduled"), (S.Todos.Count(t => !t.Done), "open to-dos"), (S.Prompts.Count, "saved prompts"),
        }.Where(x => x.N > 0))
        {
            var t = new TextBlock { FontSize = 12 };
            t.Inlines.Add(new System.Windows.Documents.Run(n.ToString()) { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(" " + what));
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var chip = new Border { Child = t, Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(3), CornerRadius = new CornerRadius(8, 8, 8, 8.01) };
            chip.SetResourceReference(Border.BackgroundProperty, "TrackBg");
            AboutStats.Children.Add(chip);
        }
        if (!aboutHooked)
        {
            aboutHooked = true;
            void Reveal(bool open)
            {
                AboutStats.Measure(new Size(AboutTab.ActualWidth > 0 ? AboutTab.ActualWidth : 520, double.PositiveInfinity));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var span = TimeSpan.FromMilliseconds(open ? 260 : 200);
                AboutStatsBox.BeginAnimation(HeightProperty, new DoubleAnimation(open ? AboutStats.DesiredSize.Height : 0, span) { EasingFunction = ease });
                AboutStatsBox.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, span) { EasingFunction = ease });
            }
            AboutPill.Cursor = Cursors.Hand;
            AboutPill.MouseEnter += (_, _) => Reveal(true);
            AboutTab.MouseLeave += (_, _) => Reveal(false);   // stays open while the pointer is on the chips
        }

        AboutRows.Children.Clear();
        void Row(string label, string value, string? open = null)
        {
            if (AboutRows.Children.Count > 0) { var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "GlassStroke"); AboutRows.Children.Add(line); }
            var l = new TextBlock { Text = label, FontSize = 12, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var v = new TextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
            v.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var row = new DockPanel { Margin = new Thickness(14, 8, 8, 8), MinHeight = 24 };
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            if (open != null)
            {
                var b = new Button { Content = "\uE838", FontFamily = App.IconFont, FontSize = 14, Style = (Style)FindResource("Ghost"), ToolTip = "Open folder", Margin = new Thickness(8, 0, 0, 0) };
                b.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", open);
                DockPanel.SetDock(b, Dock.Right); row.Children.Add(b);
            }
            row.Children.Add(v);
            AboutRows.Children.Add(row);
        }
        Row("Windows", os.Replace("Microsoft Windows ", ""));
        Row("Your data", Store.Dir, Store.Dir);
        Row("Program", AppContext.BaseDirectory.TrimEnd('\\'), AppContext.BaseDirectory);
    }

    async void AboutCopy_Click(object s, RoutedEventArgs e)
    {
        Clipboard.SetText(aboutText);
        AboutCopyText.Text = "Copied";
        await Task.Delay(1500);
        AboutCopyText.Text = "Copy system info";
    }

    Preset SelectedPreset => Api.Presets.FirstOrDefault(p => p.Name == AddProvider.SelectedItem as string) ?? Api.Presets[0];

    void AddProvider_Changed(object s, SelectionChangedEventArgs e)
    {
        var p = SelectedPreset;
        CustomFields.Visibility = p.Name == Api.CustomName ? Visibility.Visible : Visibility.Collapsed;
        // custom providers and self-hosted gateways need their address
        UrlFields.Visibility = p.Name == Api.CustomName || p.EditUrl ? Visibility.Visible : Visibility.Collapsed;
        UrlLabel.Text = p.EditUrl && !p.SelfHosted ? "ADDRESS  ·  replace ACCOUNT_ID with your account ID" : p.SelfHosted ? "ADDRESS  ·  where your gateway runs (change it if it isn't this PC)" : "BASE URL  ·  OpenAI-compatible, e.g. https://api.example.com/v1";
        if (editing == null) CustomUrlBox.Text = p.EditUrl ? p.BaseUrl : "";
        AddKey.IsEnabled = p.NeedsKey;
        AddKeyLabel.Text = p.NeedsKey ? "API KEY" + (p.KeyHint != "" ? $"  ·  starts with {p.KeyHint}" : "") + "  ·  several: separate with commas" : "API KEY  ·  not needed for local servers";
        AddStatus.Text = "";
    }

    // Build a ProviderCfg from the add form, or explain what's missing.
    ProviderCfg? DraftProvider(out string error)
    {
        var p = SelectedPreset;
        var custom = p.Name == Api.CustomName;
        var cfg = new ProviderCfg
        {
            Name = custom ? CustomNameBox.Text.Trim() : p.Name, Kind = p.Kind,
            BaseUrl = custom || p.EditUrl ? CustomUrlBox.Text.Trim().TrimEnd('/') : p.BaseUrl,
            Key = AddKey.Password.Trim(),
        };
        error = custom && cfg.Name == "" ? "Give the provider a name."
              : cfg.BaseUrl.Contains("ACCOUNT_ID") ? "Put your account ID in the address."
              : (custom || p.EditUrl) && !Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out _) ? "Enter a valid base URL, e.g. https://api.example.com/v1"
              : p.NeedsKey && cfg.Key == "" ? "Paste an API key."
              : "";
        return error == "" ? cfg : null;
    }

    async void TestKey_Click(object s, RoutedEventArgs e)
    {
        if (DraftProvider(out var err) is not { } cfg) { SetAddStatus(err, false); return; }
        await TestInto(cfg, cfg.Key, SetAddStatus);
    }


    // List a provider's models and send each a tiny test message (4 at a time), saving which ones answer.
    public async Task CheckProvider(ProviderCfg cfg, Action<string> report, CancellationToken ct = default)
    {
        if (Cli.Of(cfg) is { } cli)
        {
            if (Cli.CanVerify(cli.Adapter))   // exact versions: only the ones that answer
            {
                report("Finding models…");
                var (good, of) = await Cli.Verify(cfg, report, ct);
                ModelCache[cfg.Name] = Cli.Models(cfg, cli.Adapter);
                SaveSettings();
                report($"✓ {good} of {of} models work.");
                RefreshProviderList();
                foreach (var p in AllPanes) p.RefreshProviders();
                return;
            }
            ModelCache[cfg.Name] = Cli.Models(cfg, cli.Adapter);
            report("Testing…");
            var answered = await Cli.Ping(cfg);
            SaveSettings();
            report(answered switch
            {
                true => "✓ Ready.",
                null => "Out of quota or rate-limited.",
                _ => "No answer. Check it's signed in (Gordon: Docker Desktop).",
            });
            RefreshProviderList();
            return;
        }
        var key = Store.Unprotect(cfg.Key);
        report("Loading models…");
        var ids = await Api.ListModels(cfg, key);
        ModelCache[cfg.Name] = ids;
        int done = 0, ok = 0;
        // OpenRouter: its catalogue says which models take tools; free ones aren't sent a test, since every test would
        // count against the free requests a day (they're listed as working; a chat shows if one is unavailable)
        var catalog = cfg.BaseUrl.Contains("openrouter.ai") ? await OpenRouterCatalog() : null;
        if (catalog != null)
            foreach (var id in ids)
            {
                var info = catalog.GetValueOrDefault(id);
                if (info.Known && !info.Tools) S.ModelStatus[$"{cfg.Name}|#notools|{id}"] = "1"; else S.ModelStatus.Remove($"{cfg.Name}|#notools|{id}");
            }
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync();
            try
            {
                if (ct.IsCancellationRequested) return;   // stopped: the models not reached keep their earlier results
                if (catalog != null && id.EndsWith(":free")) { S.ModelStatus[$"{cfg.Name}|{id}"] = "ok"; ok++; return; }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var r = await Api.Probe(cfg, key, id);
                S.ModelStatus[$"{cfg.Name}|{id}"] = r;
                if (r == "ok") S.ModelStatus[$"{cfg.Name}|#time|{id}"] = Math.Round(clock.Elapsed.TotalSeconds, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);   // fast / slow in the picker
                if (r == "ok") ok++;
            }
            finally { gate.Release(); if (!ct.IsCancellationRequested) report($"Checking models… {++done} of {ids.Count}  ·  {ok} working"); }
        }));
        SaveSettings();
        if (ct.IsCancellationRequested) { RefreshProviderList(); throw new OperationCanceledException(ct); }
        report($"✓ {ok} of {ids.Count} models work.");
        RefreshProviderList();
    }

    // OpenRouter's public list of models: for each, whether it takes tools (the agent's file, command and connection tools)
    static async Task<Dictionary<string, (bool Known, bool Tools)>> OpenRouterCatalog()
    {
        try
        {
            using var web = new System.Net.Http.HttpClient(Net.Handler()) { Timeout = TimeSpan.FromSeconds(60) };
            var json = System.Text.Json.Nodes.JsonNode.Parse(await web.GetStringAsync("https://openrouter.ai/api/v1/models"));
            return (json?["data"] as System.Text.Json.Nodes.JsonArray ?? []).Where(m => m != null).ToDictionary(
                m => (string?)m!["id"] ?? "",
                m => (true, (m!["supported_parameters"] as System.Text.Json.Nodes.JsonArray ?? []).Any(p => (string?)p == "tools")));
        }
        catch { return []; }
    }

    // the gateway switch: other apps on this PC use Ivo's models through an OpenAI-compatible address
    void Gateway_Click(object s, RoutedEventArgs e)
    {
        S.GatewayOn = GatewayBox.IsChecked == true;
        try { Gateway.Apply(S); }
        catch (Exception ex) { S.GatewayOn = false; GatewayBox.IsChecked = false; _ = Notice("Couldn't start the gateway", ex.Message); }
        SaveSettings(); ShowGateway();
    }
    void ShowChatPrefs()
    {
        loading = true;
        var pick = new ModelChooser(this, S.Provider == "" ? "" : $"{S.Provider}/{S.Models.GetValueOrDefault(S.Provider) ?? DefaultModel(S.Provider, ModelCache.GetValueOrDefault(S.Provider))}");
        pick.Changed += v =>
        {
            if (ModelChooser.Split(S, v) is not var (p, m)) return;
            S.Provider = p; S.Models[p] = m; SaveSettings();
        };
        DefaultModelHost.Content = pick;
        (S.CtrlEnterSends ? SendCtrlChoice : SendEnterChoice).IsChecked = true;
        SpellBox.IsChecked = S.SpellCheck; ShowCostBox.IsChecked = S.ShowCost;
        ShowToolsBox.IsChecked = S.ShowTools; ShowThinkingBox.IsChecked = S.ShowThinking; ShowAgentPromptsBox.IsChecked = S.ShowAgentPrompts;
        loading = false;
    }
    void ChatPrefs_Changed(object s, RoutedEventArgs e)
    {
        if (loading || ChatsTab == null) return;
        S.CtrlEnterSends = SendCtrlChoice.IsChecked == true; S.SpellCheck = SpellBox.IsChecked == true; S.ShowCost = ShowCostBox.IsChecked == true;
        S.ShowTools = ShowToolsBox.IsChecked == true; S.ShowThinking = ShowThinkingBox.IsChecked == true; S.ShowAgentPrompts = ShowAgentPromptsBox.IsChecked == true;
        SaveSettings();
        foreach (var p in AllPanes) p.ApplyChatPrefs();
    }

    // Settings → Spending: the daily limit, the expensive-model price, and each provider's monthly budget
    void ShowSpending()
    {
        DailyLimitBox.Text = S.DailyLimit > 0 ? S.DailyLimit.ToString("0.##") : "";
        ExpensiveBox.Text = S.ExpensiveAt > 0 ? S.ExpensiveAt.ToString("0.##") : "";
        SpentToday.Text = $"${S.Spend.GetValueOrDefault($"*|{DateTime.Now:yyyy-MM-dd}"):0.00} spent today";
        DrawBudgets();
    }

    void Spending_Changed(object s, RoutedEventArgs e)
    {
        S.DailyLimit = double.TryParse(DailyLimitBox.Text.Trim().TrimStart('$'), out var d) && d > 0 ? d : 0;
        S.ExpensiveAt = double.TryParse(ExpensiveBox.Text.Trim().TrimStart('$'), out var x) && x > 0 ? x : 0;
        SaveSettings(); ShowSpending();
    }

    void DrawBudgets()
    {
        BudgetList.Children.Clear();
        foreach (var p in S.Providers.Where(p => p.Kind != "cli"))
        {
            var name = p.Name;
            var spent = S.Spend.GetValueOrDefault($"{name}|{DateTime.Now:yyyy-MM}");
            var box = new TextBox { Width = 90, Text = S.Budgets.TryGetValue(name, out var b) ? b.ToString("0.##") : "", ToolTip = "$ per month" };
            box.LostFocus += (_, _) =>
            {
                if (double.TryParse(box.Text.Trim().TrimStart('$'), out var v) && v > 0) S.Budgets[name] = v; else { S.Budgets.Remove(name); box.Text = ""; }
                SaveSettings();
            };
            var label = new TextBlock { Text = $"{name}  ·  ${spent:0.00} this month", VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(box, Dock.Right); row.Children.Add(box); row.Children.Add(label);
            BudgetList.Children.Add(row);
        }
    }

    void ShowRouting()
    {
        loading = true;
        AutoOnBox.IsChecked = S.AutoOn; PoolBox.IsChecked = S.PoolKeys;
        AutoTriesBox.ItemsSource = new[] { 1, 2, 3, 4, 6, 8, 10 }; AutoTriesBox.SelectedItem = S.AutoTries;
        AutoRestBox.ItemsSource = new[] { 1, 5, 10, 30, 60 }; AutoRestBox.SelectedItem = S.AutoRestMinutes;
        GatewayPortBox.Text = S.GatewayPort.ToString();
        AutoProviders.Children.Clear();
        foreach (var p in S.Providers.Where(p => p.Kind != "cli"))
        {
            var name = p.Name;
            var n = S.ModelStatus.Count(x => x.Key.StartsWith(name + "|") && !x.Key.Contains("|#") && x.Value == "ok");
            var box = new CheckBox { Content = $"{name}  ·  {n} working", IsChecked = !S.AutoSkip.Contains(name), Margin = new Thickness(0, 4, 22, 4) };
            box.SetResourceReference(ForegroundProperty, "Fg");
            box.Click += (_, _) => { S.AutoSkip.Remove(name); if (box.IsChecked != true) S.AutoSkip.Add(name); SaveSettings(); };
            AutoProviders.Children.Add(box);
        }
        if (AutoProviders.Children.Count == 0) AutoProviders.Children.Add(DimText("Add an API key or gateway first.", 12));
        ShowGateway();
        loading = false;
    }
    void Routing_Changed(object s, RoutedEventArgs e)
    {
        if (loading || RoutingTab == null) return;
        S.AutoOn = AutoOnBox.IsChecked == true; S.PoolKeys = PoolBox.IsChecked == true;
        if (AutoTriesBox.SelectedItem is int t) S.AutoTries = t;
        if (AutoRestBox.SelectedItem is int r) S.AutoRestMinutes = r;
        SaveSettings(); foreach (var p in AllPanes) p.RefreshProviders();
    }
    void GatewayPort_Changed(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(GatewayPortBox.Text, out var port) || port is < 1024 or > 65535) { GatewayPortBox.Text = S.GatewayPort.ToString(); return; }
        if (port == S.GatewayPort) return;
        S.GatewayPort = port;
        if (S.GatewayOn) Gateway_Click(s, e); else SaveSettings();
        ShowGateway();
    }
    void ShowGateway()
    {
        SearchKeyBox.Password = Store.Unprotect(S.SearchKey);
        CalendarBox.Text = string.Join("\n", S.CalendarUrls); ClipHistoryBox.IsChecked = S.ClipboardHistory;
        GatewayBox.IsChecked = S.GatewayOn;
        GatewayInfo.Visibility = S.GatewayOn ? Visibility.Visible : Visibility.Collapsed;
        GatewayInfo.Text = $"Base URL   {Gateway.Url(S.GatewayPort)}\nAPI key      {S.GatewayKey}\nModels      auto/best-coding, auto/best-chat, auto/fast, or Provider/model";
    }

    string? editing;   // name of the provider being edited, if any
    async void AddKey_Click(object s, RoutedEventArgs e)
    {
        if (DraftProvider(out var err) is not { } cfg) { SetAddStatus(err, false); return; }
        cfg.Key = Store.Protect(cfg.Key);
        S.Providers.RemoveAll(p => p.Name == cfg.Name || p.Name == editing);   // re-adding or editing replaces the old entry
        if (editing != null && editing != cfg.Name && S.Provider == editing) S.Provider = cfg.Name;
        editing = null; AddButtonText.Text = "Add";
        S.Providers.Add(cfg);
        ModelCache.Remove(cfg.Name);
        if (S.Providers.All(p => p.Name != S.Provider)) S.Provider = cfg.Name;
        SaveSettings();
        AddKey.Password = ""; CustomNameBox.Text = ""; CustomUrlBox.Text = "";
        RefreshProviderList();
        try { await CheckProvider(cfg, t => SetAddStatus($"Added {cfg.Name}. {t}", true)); }
        catch (Exception ex) { SetAddStatus($"Added, but check failed: {ex.Message}", false); }
    }

    void SetAddStatus(string text, bool ok)
    {
        AddStatus.Text = text;
        AddStatus.Foreground = ok ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    }

    static async Task TestInto(ProviderCfg cfg, string key, Action<string, bool> report)
    {
        report("Testing…", true);
        try { report($"✓ {(await Api.ListModels(cfg, key)).Count} models.", true); }
        catch (Exception ex) { report("✕ " + ex.Message, false); }
    }

    // Edit, right in the provider's card: the key (empty keeps it), and the name and address where the type has them.
    // Save re-checks the models; Test tries what's typed without saving.
    FrameworkElement InlineEditor(ProviderCfg cfg, Action closed)
    {
        var preset = Api.Presets.FirstOrDefault(p => p.Name == cfg.Name) ?? Api.Presets.First(p => p.Name == Api.CustomName);
        var custom = preset.Name == Api.CustomName;
        TextBlock Label(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) };
        var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 2) };
        var line = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 2) };
        line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        panel.Children.Add(line);

        var nameBox = new TextBox { Text = cfg.Name };
        if (custom) { panel.Children.Add(Label("NAME")); panel.Children.Add(nameBox); }
        var urlBox = new TextBox { Text = cfg.BaseUrl };
        if (custom || preset.EditUrl) { panel.Children.Add(Label(preset.EditUrl ? "ADDRESS" : "BASE URL  ·  OpenAI-compatible")); panel.Children.Add(urlBox); }

        var key = new PasswordBox();
        var shown = new TextBox { Visibility = Visibility.Collapsed };
        var show = new CheckBox { Content = "Show", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        show.SetResourceReference(ForegroundProperty, "Dim");
        show.Checked += (_, _) => { shown.Text = key.Password; key.Visibility = Visibility.Collapsed; shown.Visibility = Visibility.Visible; };
        show.Unchecked += (_, _) => { key.Password = shown.Text; shown.Visibility = Visibility.Collapsed; key.Visibility = Visibility.Visible; };
        string Typed() => show.IsChecked == true ? shown.Text.Trim() : key.Password.Trim();
        if (Api.NeedsKey(cfg) || cfg.Key != "")
        {
            var current = Store.Unprotect(cfg.Key);
            panel.Children.Add(Label(current == "" ? "API KEY" : $"API KEY  ·  now ••••{current[^Math.Min(4, current.Length)..]}, leave empty to keep it"));
            DockPanel.SetDock(show, Dock.Right);
            panel.Children.Add(new DockPanel { Children = { show, new Grid { Children = { key, shown } } } });
        }
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        ProviderCfg Draft() => new() { Name = custom ? nameBox.Text.Trim() : cfg.Name, Kind = cfg.Kind, BaseUrl = custom || preset.EditUrl ? urlBox.Text.Trim().TrimEnd('/') : cfg.BaseUrl,
                                       Key = Typed() != "" ? Typed() : Store.Unprotect(cfg.Key) };
        var save = new Button { Content = "Save", Style = (Style)FindResource("Shimmer"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var test = new Button { Content = "Test", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 5, 12, 5) };
        test.Click += async (_, _) => { test.IsEnabled = false; await TestInto(Draft(), Draft().Key, (t, ok) => { note.Text = t; note.Foreground = ok ? Brushes.MediumSeaGreen : Brushes.IndianRed; }); test.IsEnabled = true; };
        cancel.Click += (_, _) => { panel.Visibility = Visibility.Collapsed; key.Password = ""; shown.Text = ""; note.Text = ""; closed(); };
        save.Click += async (_, _) =>
        {
            var d = Draft();
            if (d.Name == "") { note.Text = "Give it a name."; note.Foreground = Brushes.IndianRed; return; }
            if (d.Name != cfg.Name && S.Providers.Any(p => p.Name == d.Name)) { note.Text = $"There's already a provider called {d.Name}."; note.Foreground = Brushes.IndianRed; return; }
            if ((custom || preset.EditUrl) && !Uri.TryCreate(d.BaseUrl, UriKind.Absolute, out _)) { note.Text = "That address doesn't look right."; note.Foreground = Brushes.IndianRed; return; }
            var renamed = d.Name != cfg.Name;
            if (renamed)
            {
                if (S.Provider == cfg.Name) S.Provider = d.Name;
                foreach (var k in S.ModelStatus.Keys.Where(k => k.StartsWith(cfg.Name + "|")).ToList()) S.ModelStatus.Remove(k);
                if (S.Models.Remove(cfg.Name, out var m)) S.Models[d.Name] = m;
            }
            var keyChanged = Typed() != "";
            cfg.Name = d.Name; cfg.BaseUrl = d.BaseUrl;
            if (keyChanged) cfg.Key = Store.Protect(Typed());
            ModelCache.Remove(cfg.Name);
            SaveSettings();
            RefreshProviderList();
            if (keyChanged || renamed || d.BaseUrl != preset.BaseUrl)
                try { await CheckProvider(cfg, _ => { }); } catch { }
        };
        panel.Children.Add(new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { save, test, cancel } });
        panel.Children.Add(note);
        panel.Tag = custom ? nameBox : (UIElement)key;   // what gets focus when it opens
        return panel;
    }

    // Cards for the providers of the shown section (API keys or Gateways), and the agents list.
    void RefreshProviderList()
    {
        UpdateCounts();
        ProvidersList.Children.Clear(); AgentsList.Children.Clear();
        NoProviders.Text = keysCategory == "gateways" ? "None yet — add one above." : "None yet — add a key above.";
        NoProviders.Visibility = S.Providers.Any(p => CategoryOf(p) == keysCategory) ? Visibility.Collapsed : Visibility.Visible;
        NoAgents.Visibility = S.Providers.Any(p => CategoryOf(p) == "agents") ? Visibility.Collapsed : Visibility.Visible;
        foreach (var cfg in S.Providers)
        {
            var category = CategoryOf(cfg);
            if (category != "agents" && category != keysCategory) continue;
            var key = Store.Unprotect(cfg.Key);
            var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = Cli.Of(cfg) is { } tool ? $"CLI · {tool.Adapter.Command}{(tool.Distro != null ? $" in WSL ({tool.Distro})" : "")}"
                     : (key == "" ? "no key" : "key ••••" + key[^Math.Min(4, key.Length)..]) + "  ·  " + cfg.BaseUrl };
            status.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

            // which models answered the last check
            // (real models only: not the "last checked" time, nor a command-line tool's own shortcuts like "default")
            var shortcuts = Cli.Of(cfg)?.Adapter.Models ?? [];
            var checks = S.ModelStatus.Where(kv => kv.Key.StartsWith(cfg.Name + "|") && !kv.Key.Contains("|#") && !shortcuts.Contains(kv.Key[(cfg.Name.Length + 1)..]))
                                      .Select(kv => kv.Value).ToList();
            var working = checks.Count(v => v == "ok");
            var models = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
                Text = Cli.Of(cfg) is { } own && !Cli.CanVerify(own.Adapter)   // picks its own model: ready or not
                    ? S.ModelStatus.GetValueOrDefault($"{cfg.Name}|#ready") switch { "ok" => "✓ Works", "failed" => "✗ Sign in, then ✓", _ => "Not tested · ✓" }
                    : checks.Count == 0 ? "Not checked · ✓"
                    : working == 0 && Cli.Of(cfg) != null ? $"Auto only · 0 of {checks.Count} named models"
                    : checks.Count == 1 ? (working == 1 ? "✓ Works" : "✗ Doesn't answer")   // one model: no count, just yes or no
                    : checks.Count == 1 ? (working == 1 ? "✓ Works" : "✗ Doesn't answer")   // one model: no count, just yes or no
                    : working == checks.Count ? $"✓ All {working} models work"
                    : $"{working} of {checks.Count} models work",
                Foreground = S.ModelStatus.GetValueOrDefault($"{cfg.Name}|#ready") is { } ready ? (ready == "ok" ? Brushes.MediumSeaGreen : Brushes.IndianRed)
                           : checks.Count == 0 || (working == 0 && Cli.Of(cfg) != null) ? (Brush)FindResource("Dim") : working > 0 ? Brushes.MediumSeaGreen : Brushes.IndianRed };

            // this provider's own choice: list only its models that answered their check (on unless you turn it off)
            var onlyWorking = new CheckBox { IsChecked = cfg.OnlyWorking, Margin = new Thickness(0, 6, 0, 0), FontSize = 12,
                                             Visibility = Cli.Of(cfg) is { } solo && !Cli.CanVerify(solo.Adapter) ? Visibility.Collapsed : Visibility.Visible,   // one model of its own: nothing to filter
                                             Content = new TextBlock { Text = "Only show models that work", FontSize = 12 } };
            ((TextBlock)onlyWorking.Content).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            void OnlyWorkingChanged(object? _, RoutedEventArgs __)
            {
                cfg.OnlyWorking = onlyWorking.IsChecked == true; SaveSettings();
                foreach (var pane in panes) pane.RefreshProviders();
            }
            onlyWorking.Checked += OnlyWorkingChanged; onlyWorking.Unchecked += OnlyWorkingChanged;

            // what Ivo has sent through it, from every saved chat
            var used = allChats.SelectMany(c => c.Messages.Select(m => m.Usage).OfType<Usage>().Where(u => (u.Provider ?? c.Provider) == cfg.Name)).ToList();
            var usage = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = used.Count == 0 ? "No usage in Ivo yet"
                     : "Used in Ivo: " + Pricing.Format(new(used.Sum(u => u.In), used.Sum(u => u.Out), used.All(u => u.Cost != null) ? used.Sum(u => u.Cost) : null)) };
            usage.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

            // money left: live from the API where it tells, otherwise a link to the provider's usage page
            var balance = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            balance.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var billing = Api.Presets.FirstOrDefault(x => x.Name == cfg.Name)?.Billing ?? "";
            if (Api.HasBalance(cfg)) balance.Text = key == "" ? "" : "Loading balance…";
            else if (billing != "")
            {
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Usage & billing")) { NavigateUri = new Uri(billing) };
                link.RequestNavigate += (_, a) => ShowPage($"{cfg.Name} · usage & billing", a.Uri.AbsoluteUri);
                balance.Inlines.Add(link);
            }
            if (key != "") _ = ShowBalance(cfg, key, balance);
            var test = ChatPane.SmallButton("", "Check this key and which models work");
            var remove = ChatPane.SmallButton("", "Remove");
            var edit = ChatPane.SmallButton("\uE70F", "Edit");
            var editor = InlineEditor(cfg, () => edit.Visibility = Visibility.Visible);
            edit.Click += (_, _) =>
            {
                var open = editor.Visibility != Visibility.Visible;
                editor.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                if (open) editor.Dispatcher.BeginInvoke(() => (editor.Tag as UIElement)?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            };
            if (category == "agents") edit.Visibility = Visibility.Collapsed;   // nothing to edit: it runs with its own sign-in
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                try { await CheckProvider(cfg, t => { status.Text = t; status.Foreground = Brushes.MediumSeaGreen; }); }
                catch (Exception ex) { status.Text = "✕ " + ex.Message; status.Foreground = Brushes.IndianRed; }
                test.IsEnabled = true;
            };
            remove.Click += (_, _) =>
            {
                S.Providers.Remove(cfg);
                if (S.Provider == cfg.Name) S.Provider = S.Providers.FirstOrDefault()?.Name ?? "";
                SaveSettings(); RefreshProviderList();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, test, remove } };
            var head = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            var name = new TextBlock { Text = cfg.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            head.Children.Add(name);
            // usage and balance slide open while the card is hovered (height and fade eased, not a jump)
            var reveal = new Border { Child = new StackPanel { Children = { usage, balance } }, ClipToBounds = true, Height = 0, Opacity = 0 };
            var card = new Border
            {
                Child = new StackPanel { Children = { head, status, models, onlyWorking, reveal, editor } },
                Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8),
            };
            card.SetResourceReference(Border.CornerRadiusProperty, "SectionCorner");
            card.SetResourceReference(Border.BackgroundProperty, "SectionBg");
            card.SetResourceReference(Border.BorderBrushProperty, "SectionStroke");
            card.SetResourceReference(Border.BorderThicknessProperty, "SectionEdge");
            void Details(bool open)
            {
                reveal.Child.Measure(new Size(Math.Max(reveal.ActualWidth, 1), double.PositiveInfinity));
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var t = TimeSpan.FromMilliseconds(open ? 280 : 220);
                reveal.BeginAnimation(HeightProperty, new DoubleAnimation(reveal.ActualHeight, open ? reveal.Child.DesiredSize.Height : 0, t) { EasingFunction = ease });
                reveal.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, t) { EasingFunction = ease });
            }
            card.MouseEnter += (_, _) => Details(true); card.MouseLeave += (_, _) => Details(false);
            (category == "agents" ? AgentsList : ProvidersList).Children.Add(card);
        }
        foreach (var p in AllPanes) p.RefreshProviders();
    }

    static async Task ShowBalance(ProviderCfg cfg, string key, TextBlock into)
    {
        try
        {
            if (await Api.Balance(cfg, key) is { } info) into.Text = info;
        }
        catch (Exception ex) { into.Text = "Couldn't read the balance: " + ex.Message; }
    }

    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();
}
