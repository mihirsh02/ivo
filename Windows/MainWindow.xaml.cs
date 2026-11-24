using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// Shell: sidebar + chat panes + settings. Chat logic lives in ChatPane.
public partial class MainWindow : Window
{
    public Settings S { get; }
    public Dictionary<string, List<string>> ModelCache { get; } = new();   // live model lists, per provider, per session
    readonly List<ChatPane> panes = [];
    ChatPane active = null!;
    // Code mode's chat: a pane of its own, kept alive beside Chat mode's, so switching modes rebuilds nothing
    ChatPane? codePane;
    ChatPane? chatActive;   // the Chat mode pane that was active before Code mode
    IEnumerable<ChatPane> AllPanes => codePane == null ? panes : panes.Append(codePane);
    bool loading = true;

    public bool IsMulti => S.Layout == "multi";
    const double MinPaneWidth = 420;   // 3 panes fit a ~1500px-wide window; fewer on smaller screens
    int MaxPanes => IsMulti ? Math.Clamp((int)(Math.Max(Panes.ActualWidth, ActualWidth - 300) / MinPaneWidth), 1, 3) : 1;

    public MainWindow()
    {
        InitializeComponent();
        BtnNews.Content = NewsGlyphView(Shell.B("#38BDF8"), 22);
        // never bigger than the screen's work area (a small or scaled screen), so centring it can't push an edge off-screen
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 16); Height = Math.Min(Height, area.Height - 16);
        S = Store.LoadSettings();
        Store.EncryptOn = S.LockWrap != "";   // chats, Inbox and keys stay sealed until the first unlock
        Cli.Status = S.ModelStatus;
        // tools Ivo doesn't check model by model (Copilot CLI, Gemini CLI, Gordon…): no leftover per-model results, only "does it answer"
        foreach (var cfg in S.Providers.Where(x => Cli.Of(x) is { } c && !Cli.CanVerify(c.Adapter)))
            foreach (var k in S.ModelStatus.Keys.Where(k => k.StartsWith(cfg.Name + "|") && !k.EndsWith("|#ready")).ToList()) S.ModelStatus.Remove(k);
        Cli.MakerModels = async makers =>
        {
            var kind = makers[0] switch { "google" => "gemini", "anthropic" => "anthropic", _ => "" };
            if (kind == "" || S.Providers.FirstOrDefault(x => x.Kind == kind && x.Key != "") is not { } p) return [];
            try { return (await Api.ListModels(p, Store.Unprotect(p.Key))).Select(m => m.StartsWith("models/") ? m[7..] : m).ToList(); } catch { return []; }
        };
        if (S.Clock != "") When.TwelveHour = S.Clock == "12";   // before anything shows a time
        if (S.Theme is not ("soft" or "softdark" or "oled" or "glow")) S.Theme = "softdark";   // an unknown theme falls back to Default
        Loaded += (_, _) => StartLock();   // before anything renders: locked from the first frame
        SetupTrust();                      // before MCP servers start or skills load
        ContentRendered += (_, _) => CheckSkills();
        if (S.Theme is "soft" or "softdark") S.Theme = SystemDark() ? "softdark" : "soft";   // Default follows Windows' mode
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && S.Theme is "soft" or "softdark")
                Dispatcher.BeginInvoke(() => { var t = SystemDark() ? "softdark" : "soft"; if (t != S.Theme) SetTheme(t); });
        };
        static bool SystemDark() => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        App.ApplyTheme(SystemParameters.HighContrast ? "contrast" : S.Theme);   // Windows' high contrast wins while it's on
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(() => App.ApplyTheme(SystemParameters.HighContrast ? "contrast" : S.Theme)); };
        FillPresets();
        HideTermsUnderOverlays();
        ICSharpCode.AvalonEdit.Search.SearchPanel.Install(Editor);   // Ctrl+F / F3 in the code editor
        ShowShortcuts();
        BuildOverview();
        foreach (RadioButton r in ThemeChoices.Children) r.IsChecked = (string)r.Tag == (S.Theme == "softdark" ? "soft" : S.Theme);
        foreach (RadioButton r in LayoutChoices.Children) r.IsChecked = (string)r.Tag == S.Layout;
        foreach (RadioButton r in ViewChoices.Children) r.IsChecked = (string)r.Tag == S.View;
        S.Zoom = Math.Clamp(Math.Round(S.Zoom, 1), 0.7, 1.5);
        DrawZoomScale();   // Settings → Appearance → Text size, at the saved size
        ApplyZoom();
        TrayBox.IsChecked = S.CloseToTray;
        McpBox.Text = S.McpServers;
        loading = false;
        Skills.Disabled = [.. S.DisabledSkills];
        Agent.Me = S.Profile;
        Planner.S = S;   // tools read it as soon as the first chat lists models
        WireAgentApprovals();
        Agent.EmailDraft = (to, subject, body) => DraftEmail(subject, body, to);
        Connections.Tokens = S.Connections; StartSpotify(); StartHabitNudges(); StartWidgets(); StartActivity(); ApplyBehaviour(); IvoModel.On = S.SmarterChat; IvoModel.Custom = S.SmarterChatFile; StartIdleTrim(); HookRail(); HookWebhooks(); NotifyCard.Look = App.Crisp; NotifyCard.IconFont = App.IconFont; StartChats(); ListenForContacts(); Connections.Changed = () => Dispatcher.BeginInvoke(SaveSettings);
        // Code mode built ahead, a few seconds after the window is on screen, so even the first switch to it is instant
        ContentRendered += (_, _) => { if (codePane == null && S.CodeOn) new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(4), System.Windows.Threading.DispatcherPriority.Background, (t, _) => { ((System.Windows.Threading.DispatcherTimer)t!).Stop(); PrepareCode(); }, Dispatcher); };
        Pricing.Warm();
        StartMcp();
        RefreshPrompts();
        Application.Current.SessionEnding += (_, _) => quitting = true;   // never block a Windows sign-out

        active = AddPane(NewChat());
        ApplyProfile();
        if (!S.WelcomeDone) ShowIntro();
        if (CodeMode) ApplyView();
        // Dropdowns/popups are separate top-level windows; close them when Ivo loses focus or minimises,
        // otherwise one can be left floating over other apps.
        Deactivated += (_, _) => CloseDropdowns();
        RefreshProviderList();
        RefreshChats();
        UpdateGlow();
        UpdateLayoutHint();
    }

    public void SaveSettings() { if (!loading) Store.SaveSettings(S); }

    public Chat NewCompanionChat() { var c = NewChat(); c.Space = "chat"; c.Workspace = null; return c; }

    public void OpenChat(Chat c)
    {
        if (CodeMode) { S.View = "chat"; foreach (RadioButton r in ViewChoices.Children) r.IsChecked = (string)r.Tag == S.View; ApplyView(); }
        RefreshChats();
        OpenChat(c, side: false);
        ShowWindow();
    }

    // a fresh chat that isn't saved and has no memory
    // a second click leaves it: back to the chat you were in before (the incognito one is gone)
    Chat? beforeIncognito;
    public void Incognito_Click(object s, RoutedEventArgs e)
    {
        if (active.Chat.Incognito)
            active.ShowChat(allChats.FirstOrDefault(c => c.Id == beforeIncognito?.Id) ?? NewChat());
        else
        {
            beforeIncognito = active.Chat;
            var c = NewChat(); c.Incognito = true; c.Title = "Incognito chat";
            active.ShowChat(c);
        }
        active.FocusInput();
    }

    // each chat's header has its incognito eye: an open eye that blinks now and then while that chat is incognito
    public void UpdateIncognitoButton()
    {
        foreach (var p in AllPanes)
        {
            var b = p.IncogBtn; var on = p.Chat?.Incognito == true;
            if (on) b.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x9C, 0x7C, 0xFF)); else b.ClearValue(BackgroundProperty);
            b.ToolTip = on ? "Leave incognito" : "Incognito chat";
            b.Content = on ? "" : "";
            if (b.RenderTransform is not ScaleTransform blink) b.RenderTransform = blink = new ScaleTransform();
            if (on && blink.HasAnimatedProperties) continue;
            if (on)
            {
                var anim = App.Loop(new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.2), RepeatBehavior = RepeatBehavior.Forever });
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.9))));
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.0)), new QuadraticEase()));
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.15)), new QuadraticEase()));
                blink.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            else blink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    Chat NewChat() => new()
    {
        Provider = S.Provider, Model = DefaultModel(S.Provider, ModelCache.GetValueOrDefault(S.Provider)),
        Effort = S.Effort, Mode = S.Mode, Workspace = S.Workspace, Permission = S.Permission, Sandbox = S.Sandbox, Temp = Math.Min(S.Temp, 1), System = S.System,
        Space = CodeMode ? "code" : "chat",
    };

    // The starred default if it isn't known to fail; otherwise the first working model (pinned first),
    // then any model that hasn't failed. A failed model is never picked while another option exists.
    public string DefaultModel(string provider, IEnumerable<string>? available)
    {
        var ids = (available ?? Api.ModelsFor(S.Providers.FirstOrDefault(p => p.Name == provider))).ToList();
        string? St(string m) => S.ModelStatus.GetValueOrDefault($"{provider}|{m}");
        var star = S.Models.GetValueOrDefault(provider);
        if (star != null && (St(star) ?? "ok") == "ok" && (available == null || ids.Contains(star))) return star;
        var byPin = ids.OrderByDescending(m => S.Pinned.Contains($"{provider}|{m}")).ToList();
        return byPin.FirstOrDefault(m => St(m) == "ok") ?? byPin.FirstOrDefault(m => St(m) == null) ?? star ?? ids.FirstOrDefault() ?? "";
    }

    public bool AnyElevated() => AllPanes.Any(p => Agent.RunsElevated(p.Chat));

    // ---------- panes ----------
    ChatPane AddPane(Chat c)
    {
        var p = new ChatPane(this) { Margin = new Thickness(0, 0, 7, 0) };
        panes.Add(p);
        p.ShowPopOut(companion is not { IsVisible: true });
        LayoutPanes();
        p.ShowChat(c);
        ActivatePane(p);
        return p;
    }

    void LayoutPanes()
    {
        Panes.Children.Clear();
        Panes.Columns = panes.Count;
        foreach (var p in panes) { Detach(p); Panes.Children.Add(p); }
        foreach (var p in panes) p.SetActive(p == active, IsMulti);
    }

    // a pane can only have one parent: take it out of wherever it is (code view's dock, a drag in progress) before placing it
    static void Detach(FrameworkElement el)
    {
        switch (el.Parent)
        {
            case Panel panel: panel.Children.Remove(el); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
            case ContentPresenter cp: cp.Content = null; break;
        }
    }

    void CloseDropdowns() { foreach (var p in AllPanes) p.CloseDropdowns(); }

    public void ActivatePane(ChatPane p)
    {
        if (!panes.Contains(p) && p != codePane) return;   // the companion's pane
        active = p;
        foreach (var x in panes) x.SetActive(x == p, IsMulti);
        if (CodeMode) CodeChat_FocusChanged(CodeChat, default);   // docked: its outline follows focus, like the editor's
        loading = true;
        Chats.SelectedItem = ((Chats.ItemsSource as System.Windows.Data.ListCollectionView)?.SourceCollection as List<Chat>)?.FirstOrDefault(c => c.Id == p.Chat.Id);
        loading = false;
    }

    public void ClosePane(ChatPane p)
    {
        if (p == codePane) { p.Stop(); p.ShowChat(NewChat()); RefreshChats(); return; }   // Code mode's one chat: a fresh one takes its place
        if (panes.Count == 1) { p.Stop(); p.ShowChat(NewChat()); RefreshChats(); return; }   // the last one: a fresh chat takes its place
        p.Stop();
        panes.Remove(p);
        if (active == p) active = panes[^1];
        LayoutPanes();
        ActivatePane(active);
    }

    public void MovePane(ChatPane src, ChatPane target)
    {
        if (!panes.Contains(src) || !panes.Contains(target)) return;   // only panes in this window reorder
        int to = panes.IndexOf(target);
        panes.Remove(src);
        panes.Insert(to, src);
        LayoutPanes();
    }

    // Window got narrower: drop panes that no longer fit (their chats are saved).
    void Window_SizeChanged(object s, SizeChangedEventArgs e)
    {
        while (panes.Count > MaxPanes) ClosePane(panes.Last(p => p != active));
        UpdateLayoutHint();
        SizeWheel();   // every window over the chat keeps the app's shape, scaled down
    }

    void OpenChat(Chat c, bool side)
    {
        var open = AllPanes.FirstOrDefault(p => p.Chat.Id == c.Id);
        if (open != null) { ActivatePane(open); return; }
        // a pane still showing an empty new chat takes it (the one you're in first), rather than opening another beside it
        if (side && (active.Chat.Messages.Count == 0 && !active.Answering ? active : AllPanes.FirstOrDefault(p => p.Chat.Messages.Count == 0 && !p.Answering)) is { } blank)
        {
            blank.ShowChat(c); ActivatePane(blank); return;
        }
        if (side && panes.Count < MaxPanes) { AddPane(c); return; }
        var target = side ? panes.LastOrDefault(p => p != active) ?? active : active;
        target.ShowChat(c);
        ActivatePane(target);
    }

    // ---------- sidebar ----------
    public void ChatsChanged() => RefreshChats();

    bool showArchived;

    List<Chat> allChats = [];
    void RefreshChats()
    {
        allChats = Store.LoadChats(); ShowChats();
        if (CodeMode && CodeChatsView.IsVisible) RefreshCodeChats();
    }

    // Groups (Pinned and your own groups, each foldable, chats indented) then Recent (newest first); archived chats live in
    // their own view.
    // A search looks through every chat's title and messages, archived ones too.
    void ShowChats()
    {
        loading = true;
        var q = ChatSearch.Text.Trim();
        var list = q != ""
            ? allChats.Where(c => c.Space != "code" && (c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Messages.Any(m => m.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))).ToList()
            : allChats.Where(c => c.Space != "code" && c.Archived == showArchived)
                      .OrderBy(c => c.Area == "Recent").ThenByDescending(c => c.Pinned).ThenBy(c => c.Group).ThenBy(c => c.Rank).ThenByDescending(c => c.Updated).ToList();
        var view = new System.Windows.Data.ListCollectionView(list);
        if (q == "" && !showArchived)
        {
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Chat.Area)));
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Chat.SubGroup)));
        }
        Chats.ItemsSource = view;
        Chats.SelectedItem = list.FirstOrDefault(c => c.Id == active?.Chat.Id);
        var archived = allChats.Count(c => c.Archived);
        ChatsLabel.Text = q != "" ? $"{list.Count} matching chat{(list.Count == 1 ? "" : "s")}" : showArchived ? "Archived chats" : "";
        ChatsLabel.Visibility = ChatsLabel.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        ArchiveToggle.Content = showArchived ? "\uE72B" : "\uE7B8";
        ArchiveToggle.ToolTip = showArchived ? "Back to chats" : $"Archived chats ({archived})";
        ArchiveToggle.Visibility = q == "" && (showArchived || archived > 0) ? Visibility.Visible : Visibility.Collapsed;
        ChatSearchHint.Visibility = ChatSearch.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        loading = false;
    }

    void ChatSearch_Changed(object s, TextChangedEventArgs e) => ShowChats();
    void ChatSearch_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; ChatSearch.Text = ""; }
        else if (e.Key == Key.Enter && Chats.Items.Count > 0 && Chats.Items[0] is Chat c) { e.Handled = true; OpenChat(c, side: false); }
    }

    // A sidebar heading: open unless the user folded it; clicking folds or opens it.
    void GroupHead_Loaded(object s, RoutedEventArgs e)
    {
        if (s is System.Windows.Controls.Primitives.ToggleButton { Tag: string name } t) t.IsChecked = !S.FoldedGroups.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
    void GroupHead_Click(object s, RoutedEventArgs e)
    {
        if (s is not System.Windows.Controls.Primitives.ToggleButton { Tag: string name } t) return;
        S.FoldedGroups.Remove(name);
        if (t.IsChecked != true) S.FoldedGroups.Add(name);
        SaveSettings();
    }

    void ArchiveToggle_Click(object s, RoutedEventArgs e) { showArchived = !showArchived; RefreshChats(); }

    void NewChat_Click(object s, RoutedEventArgs e) { active.ShowChat(NewChat()); RefreshChats(); }
    // Ivo's own chat: the mascot, offline, in a window of her own (not a model chat)
    void TalkToIvo_Click(object s, RoutedEventArgs e) => OpenIvo();
    public IvoChatView OpenIvo() { PrepIvo(); return OpenIvoPanel(); }

    // Blub's chat on its own, over the desktop, when Ivo is minimised or in the tray, like a chat bubble on a phone: it
    // grows out of her desktop widget (from: the widget, in screen units), tapping her again tucks it back in, and it
    // goes away when Ivo itself comes back. The same chat and look as her panel, a little bigger in the same shape.
    Window? ivoWindow;
    Action? tuckIvo;
    public bool IvoWindowOpen => tuckIvo != null;
    public void TuckIvoWindow() => tuckIvo?.Invoke();
    public void ToggleIvoWindow(Rect from, Action<bool> raise)
    {
        if (tuckIvo != null) { tuckIvo(); return; }
        PrepIvo();
        var f = FrameSize(Fit.Tall);
        if (f.Width < 100 || f.Height < 100) f = new Size(500, 760);
        var area = SystemParameters.WorkArea;
        var scale = Math.Min(1.15, Math.Min((area.Width - 40) / f.Width, (area.Height - 40) / f.Height));
        var v = new IvoChatView(S.Profile?.Name ?? "", new IvoHands(IvoNeedsOk, IvoDo, IvoOpen, () => { tuckIvo?.Invoke(); Show(); WindowState = WindowState.Normal; Activate(); StartTour(); }, SignInSpotifyFromChat));
        var w = new Window { Title = "Blub", Width = f.Width * scale, Height = f.Height * scale, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                             ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, FontFamily = FontFamily, FontWeight = FontWeight, UseLayoutRounding = true, Opacity = 0 };
        // right beside the widget, on the side with room, level with her as far as the screen allows; a tail points at her
        const double tail = 14;
        w.Width += tail;
        var right = from.Left + from.Width / 2 > area.Left + area.Width / 2;   // she's on the right: the chat opens to her left
        w.Left = Math.Clamp(right ? from.Left - w.Width + 6 : from.Right - 6, area.Left + 4, area.Right - w.Width - 4);
        w.Top = Math.Clamp(from.Top + from.Height / 2 - 60, area.Top + 8, area.Bottom - w.Height - 8);
        var tailY = Math.Clamp(from.Top + from.Height / 2 - w.Top, 30, w.Height - 30);
        var body = new DockPanel();
        var head = PanelHead("Blub", "", "#A78BFA", () => tuckIvo?.Invoke(), v.HeaderActions);
        DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        body.Children.Add(new Border { Child = v, Padding = new Thickness(24, 4, 24, 24) });
        var card = new Border { Child = body, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), ClipToBounds = true, Margin = right ? new Thickness(0, 0, tail, 0) : new Thickness(tail, 0, 0, 0) };
        var point = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(right ? $"M0,{tailY - 11} L{tail + 1},{tailY} L0,{tailY + 11} Z" : $"M{tail + 1},{tailY - 11} L0,{tailY} L{tail + 1},{tailY + 11} Z"),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = right ? new Thickness(0, 0, 0, 0) : new Thickness(0)
        };
        point.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BoxBg");
        var whole = new Grid { Children = { card, point } };
        card.SetResourceReference(Border.BackgroundProperty, "BoxBg"); card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        // it pops out of her widget like a bubble and shrinks back into it. The live chat is too heavy to scale smoothly,
        // so a still picture of it does the moving and the chat itself shows once it's in place.
        var still = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var pop = new ScaleTransform(1, 1);
        still.RenderTransform = pop;
        still.RenderTransformOrigin = new Point(right ? 1 : 0, tailY / w.Height);   // from the tail's tip, at her
        w.Content = new Grid { Children = { whole, still } };
        Action tuck = null!;   // tucks this chat away (set below)
        void Bubble(bool open, Action? done = null)
        {
            if (whole.IsVisible)   // tapped again while it's still opening: the picture already moving is the one to shrink
            {
                var dpi = VisualTreeHelper.GetDpi(whole);
                var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)(whole.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(whole.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                shot.Render(whole); shot.Freeze();
                still.Source = shot;
            }
            still.Visibility = Visibility.Visible; whole.Visibility = Visibility.Hidden; w.Opacity = 1;
            var time = TimeSpan.FromMilliseconds(open ? 240 : 170);
            var size = new System.Windows.Media.Animation.DoubleAnimation(open ? 0.15 : 1, open ? 1 : 0.1, time)
            { EasingFunction = open ? new System.Windows.Media.Animation.BackEase { Amplitude = 0.35, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                                    : new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
            size.Completed += (_, _) =>
            {
                if (open && tuckIvo == tuck) { whole.Visibility = Visibility.Visible; still.Visibility = Visibility.Collapsed; still.Source = null; v.Focus(); }
                done?.Invoke();
            };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, size);
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, size.Clone());
            still.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(open ? 0.3 : 1, open ? 1 : 0, time));
        }
        void Back(object? _, EventArgs __) { if (IsVisible && WindowState != WindowState.Minimized) tuckIvo?.Invoke(); }   // Ivo is back: the bubble goes
        void BackV(object s, DependencyPropertyChangedEventArgs e) => Back(s, EventArgs.Empty);
        tuck = () => { if (tuckIvo == tuck) tuckIvo = null; Bubble(false, w.Close); };
        tuckIvo = tuck;
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; tuckIvo?.Invoke(); } };
        w.Closed += (_, _) =>
        {
            StateChanged -= Back; IsVisibleChanged -= BackV;
            if (ivoView == v) ivoView = null;
            if (ivoWindow == w) { ivoWindow = null; raise(false); }   // not when a new one already took its place (she was moved)
            if (tuckIvo == tuck) tuckIvo = null;
        };
        StateChanged += Back; IsVisibleChanged += BackV;
        App.Crisp(w);
        App.PauseMotion(false);   // Ivo is minimised, with its animations paused
        ivoView = v; ivoWindow = w;
        raise(true);
        w.Show(); w.Activate();
        w.Dispatcher.BeginInvoke(() => Bubble(true), System.Windows.Threading.DispatcherPriority.Loaded);   // once it's laid out, so the picture has the chat in it
    }

    void PrepIvo()
    {
        IvoMind.Datasets = IvoDatasetsOn();
        IvoMind.Places = IvoPlaces();
        IvoChatView.CardFor = IvoCard;
        Holidays(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)); Holidays(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1));   // ready before you ask about meetings
        IvoModel.On = S.SmarterChat; IvoModel.Custom = S.SmarterChatFile;
        IvoMind.Personality = S.IvoPersonality;
        ApplyExtras();
        S.IvoAgeOffset ??= Random.Shared.Next(-2, 3);   // a year or two either side of yours, once
        IvoMind.YourBirth = S.Profile?.Born; IvoMind.Gender = S.Profile?.Gender ?? ""; IvoMind.AgeOffset = S.IvoAgeOffset.Value;
        IvoMind.Contacts = [.. S.PortalContacts.Select(c => c.Name).Where(n => n != "")];
        IvoMind.CalendarOn = day => CalEvents(new DateTime(day.Year, day.Month, 1)).Where(e => e.Start.Date <= day.Date && e.End.Date >= day.Date).Select(e => (e.AllDay ? day.Date : e.Start, e.Title));
    }
    IvoChatView OpenIvoPanel()
    {
        var v = new IvoChatView(S.Profile?.Name ?? "", new IvoHands(IvoNeedsOk, IvoDo, IvoOpen, () => { ClosePanel(); StartTour(); }, SignInSpotifyFromChat));
        OpenPanel("ivo", "Blub", "", "#A78BFA", v, scroll: false);
        ivoView = v;
        return v;
    }

    // Open on left-click only. (Opening on selection meant right-click, or the list auto-selecting
    // on load, replaced the chat in the current pane.)
    void Chats_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || IsInButton(d)) return;
        if (ItemsControl.ContainerFromElement(Chats, d) is ListBoxItem { DataContext: Chat c })
        {
            OpenChat(c, side: IsMulti && !CodeMode);
            if (ChatSearch.Text.Trim() is { Length: > 0 } q) AllPanes.FirstOrDefault(p => p.Chat.Id == c.Id)?.FindInChat(q);   // searching: jump to the match
        }
    }
    static bool IsInButton(DependencyObject? d)
    {
        for (; d != null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    void Chats_RightClick(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Chats, d) is ListBoxItem { DataContext: Chat c })
            ShowChatMenu(c, e.GetPosition(this));
    }
    void ChatMore_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { DataContext: Chat c } b) return;
        ShowChatMenu(c, b.TranslatePoint(new Point(b.ActualWidth, b.ActualHeight + 4), this));
    }
    void ChatPin_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { DataContext: Chat c }) Change(c, x => x.Pinned = !x.Pinned);
    }

    // Edits a saved chat and any open copy of it, so a pane saving later doesn't undo the change.
    void Change(Chat c, Action<Chat> edit)
    {
        edit(c);
        foreach (var p in AllPanes.Where(p => p.Chat.Id == c.Id)) edit(p.Chat);
        Store.SaveChat(c);
        RefreshChats();
    }

    void ShowChatMenu(Chat c, Point at)
    {
        ShowMenu(at,
        [
            ("\uE70F", "Rename", () => Rename(c), false),
            ("\uEDE1", "Export…", () => ExportChat(c), false),
            (c.Pinned ? "\uE77A" : "\uE718", c.Pinned ? "Unpin" : "Pin", () => Change(c, x => x.Pinned = !x.Pinned), false),
            ("\uE8B7", "Move to group…", () => GroupMenu(c, at), false),
            .. c.Group != null ? [("\uE8B7", $"Remove from {c.Group}", () => Change(c, x => x.Group = null), false)] : new (string, string, Action, bool)[0],
            .. IsMulti ? [("\uE8A0", "Open in side pane", () => OpenChat(c, side: true), false)] : new (string, string, Action, bool)[0],
            .. companion is { IsVisible: true } ? new (string, string, Action, bool)[0] : [("\uE8A7", "Open in companion", () => OpenInCompanion(c), false)],
            null,
            ("\uE7F4", c.LocalOnly ? "Allow cloud models" : "Local only", (Action)(() => { Change(c, x => x.LocalOnly = !x.LocalOnly); foreach (var p in AllPanes) if (p.Chat.Id == c.Id) { p.Chat.LocalOnly = c.LocalOnly; p.UpdateAgentUi(); } }), false),
            (c.Archived ? "\uE7A7" : "\uE7B8", c.Archived ? "Unarchive" : "Archive", () => Change(c, x => { x.Archived = !x.Archived; x.Pinned = false; }), false),
            ("\uE74D", "Delete", () => Delete(c), true),
        ]);
    }

    // Existing groups to move the chat into, or a new one.
    void GroupMenu(Chat c, Point at)
    {
        var groups = allChats.Select(x => x.Group).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(g => g != c.Group).OrderBy(g => g).ToList();
        ShowMenu(at,
        [
            .. groups.Select(g => ((string, string, Action, bool)?)("\uE8B7", g, () => Change(c, x => x.Group = g), false)),
            .. groups.Count > 0 ? [null] : new (string, string, Action, bool)?[0],
            ("\uE8F4", "New group…", async () =>
            {
                if (await AskText("New group", "Group name", "", "Create") is { } name) Change(c, x => x.Group = name);
            }, false),
        ]);
    }

    // A small menu drawn on top of the window (not a popup), moved so it always fits inside it.
    void ShowMenu(Point at, (string Icon, string Text, Action Run, bool Danger)?[] items, Dictionary<string, Func<FrameworkElement>>? marks = null, bool above = false)
    {
        MenuItems.Children.Clear();
        foreach (var it in items)
        {
            if (it is not { } i)
            {
                var line = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5) };
                line.SetResourceReference(Border.BackgroundProperty, "Separator");
                MenuItems.Children.Add(line);
                continue;
            }
            var color = i.Danger ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)) : null;
            var icon = new TextBlock { Text = i.Icon, FontFamily = App.IconFont, FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = i.Text, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var t in new[] { icon, text }) if (color != null) t.Foreground = color; else t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            FrameworkElement badge = icon;
            if (marks?.TryGetValue(i.Text, out var mk) == true) { badge = mk(); badge.Width = 24; }
            var compact = marks != null;   // the terminal picker: tighter rows
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = compact ? new Thickness(8, 6, 14, 6) : new Thickness(10, 8, 18, 8), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                                   Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { badge, text } } };
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Hover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var run = i.Run;
            row.MouseLeftButtonUp += (_, _) => { CloseMenu(); run(); };
            MenuItems.Children.Add(row);
        }
        MenuCard.MinWidth = marks != null ? 0 : 210;
        PlaceMenu(at, above);
    }

    // shows the menu card at a point (window coordinates), kept inside the window and below the title bar
    void PlaceMenu(Point at, bool above)
    {
        MenuLayer.Visibility = Visibility.Visible;
        at = TranslatePoint(at, MenuLayer);   // window → menu layer (it's scaled with the text size)
        var (w, h) = (MenuLayer.ActualWidth > 0 ? MenuLayer.ActualWidth : ActualWidth, MenuLayer.ActualHeight > 0 ? MenuLayer.ActualHeight : ActualHeight);
        MenuCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = MenuCard.DesiredSize;
        const double titleBar = 52;
        Canvas.SetLeft(MenuCard, Math.Max(8, Math.Min(at.X, w - size.Width - 8)));
        Canvas.SetTop(MenuCard, Math.Max(titleBar, Math.Min(above ? at.Y - size.Height - 6 : at.Y, h - size.Height - 8)));
    }
    void CloseMenu() => MenuLayer.Visibility = Visibility.Collapsed;
    void MenuLayer_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e) => CloseMenu();
    void MenuCard_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    // Header "split" button: a fresh chat beside the current ones.
    // /compare <prompt>: the same prompt to up to three models side by side. Panes already open keep their model;
    // new ones get other working models (pinned first), so the answers can be read next to each other.
    public void Compare(string prompt)
    {
        if (prompt == "") return;
        if (CodeMode) { S.View = "chat"; foreach (RadioButton r in ViewChoices.Children) r.IsChecked = (string)r.Tag == S.View; ApplyView(); }
        if (!IsMulti) { S.Layout = "multi"; SaveSettings(); }
        var used = panes.Select(p => $"{p.Chat.Provider}|{p.Chat.Model}").ToHashSet();
        var others = S.Providers.Where(p => p.Kind != "cli")
            .SelectMany(p => (ModelCache.GetValueOrDefault(p.Name) ?? Api.ModelsFor(p).ToList()).Select(m => (p: p.Name, m)))
            .Where(x => S.ModelStatus.GetValueOrDefault($"{x.p}|{x.m}") != null && S.ModelStatus[$"{x.p}|{x.m}"] == "ok" && !used.Contains($"{x.p}|{x.m}"))
            .OrderByDescending(x => S.Pinned.Contains($"{x.p}|{x.m}")).ToList();
        var target = Math.Min(3, Math.Max(2, MaxPanes));
        while (panes.Count < target && others.Count > 0)
        {
            var (prov, model) = others[0]; others.RemoveAt(0);
            var c = NewChat(); c.Provider = prov; c.Model = model;
            AddPane(c);
        }
        foreach (var p in panes.ToList())
        {
            if (p.Chat.Messages.Count > 0) { var c = NewChat(); c.Provider = p.Chat.Provider; c.Model = p.Chat.Model; p.ShowChat(c); }
            p.Comparing = prompt;
            p.Send(prompt);
        }
        if (panes.Count < 2) _ = Notice("Only one model to compare", "Add another provider in Settings.");
    }

    // agents' approvals go to whichever pane shows the chat they work for
    void WireAgentApprovals() => Subagents.Approve = (chat, agent, call) =>
        Dispatcher.Invoke(() => AllPanes.FirstOrDefault(p => p.Chat.Id == chat.Id) is { } pane ? pane.ApproveForAgent(agent, call) : Task.FromResult(false));

    public void PickCompareWinner(ChatPane winner, string prompt)
    {
        var key = $"{winner.Chat.Provider}|{winner.Chat.Model}";
        S.Preferred[key] = S.Preferred.GetValueOrDefault(key) + 1;
        var others = panes.Where(p => p != winner && p.Comparing == prompt).Select(p => $"{p.Chat.Provider}|{p.Chat.Model}");
        S.CompareLog.Insert(0, $"{DateTime.Now:yyyy-MM-dd HH:mm} · {(prompt.Length > 80 ? prompt[..80] + "…" : prompt)} · {key} · {string.Join(", ", others)}");
        if (S.CompareLog.Count > 200) S.CompareLog.RemoveRange(200, S.CompareLog.Count - 200);
        SaveSettings();
        foreach (var p in panes.Where(p => p.Comparing == prompt)) p.EndCompare();
    }

    // Starred replies across every chat (the ☆ by Search chats): the newest 20, click one to open its chat.
    void Starred_Click(object s, RoutedEventArgs e) => ShowStars();

    public void NewSidePane()
    {
        if (panes.Count < MaxPanes) AddPane(NewChat());
        else _ = Notice("No room for another chat", $"{MaxPanes} chats max at this width.");
    }

    // Opens the chat (if it isn't already) and edits its title in the pane header.
    void Rename(Chat c)
    {
        OpenChat(c, side: false);
        AllPanes.FirstOrDefault(p => p.Chat.Id == c.Id)?.BeginRename();
    }

    async void Delete(Chat c)
    {
        if (!await Confirm("Delete this chat?", $"“{c.Title}” will be deleted.", "Delete", danger: true)) return;
        Store.DeleteChat(c);
        foreach (var p in AllPanes.Where(p => p.Chat.Id == c.Id)) p.ShowChat(NewChat());
        RefreshChats();
    }

    // Uses the open copy when there is one, so the newest messages are included.
    async void ExportChat(Chat c)
    {
        c = AllPanes.FirstOrDefault(p => p.Chat.Id == c.Id)?.Chat ?? c;
        var name = Regex.Replace(c.Title, @"[\\/:*?""<>|]", "").Trim().TrimEnd('…');
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export chat", FileName = name == "" ? "chat" : name,
            Filter = "PDF (*.pdf)|*.pdf|Markdown (*.md)|*.md|Web page (*.html)|*.html",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await Export.Save(c, dlg.FileName);
            Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex) { await Notice("Couldn't export the chat", ex.Message); }
    }

}
