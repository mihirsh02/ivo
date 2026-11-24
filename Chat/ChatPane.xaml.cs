using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace Ivo;

public record Pending(string Name, Img? Img = null, string Text = "");

public record ModelItem(string Id, bool Pinned, string? Status, bool IsDefault = false, string? Speed = null, bool InUse = false, bool NoTools = false)
{
    // the labels on the right: in use (this chat's model), no tools (in a chat that offers them), fast / slow (its last check)
    public string SpeedText => string.Join(" · ", new[] { InUse ? "in use" : null, NoTools ? "no tools" : null, Speed }.Where(x => x != null));
    public string StarGlyph => IsDefault ? "\uE735" : "\uE734";
    public string PinTip => Pinned ? "Unpin" : "Pin to top";
    public string StarTip => IsDefault ? "Default for new chats (click to clear)" : "Make this the default for new chats";
    public string PinGlyph => Pinned ? "\uE840" : "\uE718";
    // "google/gemma-4-31b-it": listed as gemma-4-31b-it under GOOGLE (ids without a vendor go under MODELS)
    public string Short => Id[(Id.LastIndexOf('/') + 1)..];
    public string Vendor => Id.Contains('/') ? Id[..Id.IndexOf('/')].TrimStart('~') : "";
    public string Group => Pinned ? "PINNED" : Vendor == "" ? "MODELS" : Vendor.ToUpperInvariant();
    public string Tip => $"{Id}\n{StatusText}";
    // Quota/rate-limit errors mean the model exists but your plan can't use it right now.
    bool Limited => Status != null && Regex.IsMatch(Status, "quota|rate limit|exceeded|billing|slow:", RegexOptions.IgnoreCase);
    public Brush Dot => Status == null ? new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80))
                      : Status == "ok" ? Brushes.MediumSeaGreen : Limited ? Brushes.Goldenrod : Brushes.IndianRed;
    public string StatusText => Status == null ? "Not tried yet — use ✓ Check models"
                              : Status == "ok" ? "Working"
                              : Limited ? "Not available on your plan right now (or too slow): " + Status.Replace("slow: ", "")
                              : "Doesn't work: " + Status;
    public override string ToString() => Id;
}

// One chat: header, messages, composer. Single mode shows one; multi-chat shows up to three side by side.
public partial class ChatPane : UserControl
{
    static readonly FontFamily Mono = new("Cascadia Code, Consolas");
    static readonly string[] TextExt = [".txt", ".md", ".cs", ".js", ".ts", ".py", ".json", ".xml", ".html", ".css", ".csv", ".log", ".yaml", ".yml", ".java", ".cpp", ".c", ".h", ".go", ".rs", ".sql", ".ps1", ".sh"];

    readonly MainWindow main;
    public Chat Chat { get; private set; } = new();
    CancellationTokenSource? cts;
    public bool Busy => cts != null;   // replying (or running tools) right now
    readonly List<Pending> pending = [];
    bool loading;

    // in the companion window: no pane buttons, no dragging between panes
    public bool Companion { get => companion; init { companion = value; if (value) { ClosePane.Visibility = SplitPane.Visibility = PopOut.Visibility = Visibility.Collapsed; Header.Cursor = null; Header.ToolTip = null; } } }
    readonly bool companion;

    void Incog_Click(object s, RoutedEventArgs e) { main.ActivatePane(this); main.Incognito_Click(s, e); }
    void PopOut_Click(object s, RoutedEventArgs e) => main.OpenInCompanion(Chat);
    public void ShowPopOut(bool show) => PopOut.Visibility = show && !Companion ? Visibility.Visible : Visibility.Collapsed;

    // text (from the screen, say) added to what's being written
    public void InsertText(string t) { Input.Text += (Input.Text.Length > 0 ? Environment.NewLine : "") + t; FocusInput(); }

    public void FocusInput() => Dispatcher.BeginInvoke(() => { Input.Focus(); Input.CaretIndex = Input.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);

    public ChatPane(MainWindow main)
    {
        this.main = main;
        InitializeComponent();
        Loaded += (_, _) => Input.SpellCheck.IsEnabled = main.S.SpellCheck;
        Subagents.Changed += () => Dispatcher.BeginInvoke(() => { ShowAgents(); UpdateUsage(); });
        Log.SizeChanged += (_, _) => { foreach (var el in Log.Children.OfType<FrameworkElement>()) if (el.Tag as string == FullReply) Widen(el); };
        Beam.Padding = new Thickness(App.Line);   // the chat bar's ring: the same weight as every line
        Greet(code: false);
        Loaded += (_, _) =>
        {
            StartAnimations();
            if (Window.GetWindow(this) is { } w) { w.PreviewMouseDown -= Window_PreviewMouseDown; w.PreviewMouseDown += Window_PreviewMouseDown; }
        };
        Log.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(Log_MouseUp), true);   // after a selection: Reply
        SizeChanged += (_, _) => OptionsPanel.Width = Math.Max(240, Math.Min(380, ActualWidth - 28));
    }

    // Started from code: XAML event-trigger storyboards inside templates proved crash-prone.
    void StartAnimations()
    {
        // the orb on the empty chat moves only while it's on screen (hidden behind a conversation, minimised or in the tray, it rests)
        Empty.IsVisibleChanged -= Orb; Empty.IsVisibleChanged += Orb;
        Orb(this, default);
        ApplyTheme(main.S.Theme);
    }

    public void Orb(object s, DependencyPropertyChangedEventArgs e)
    {
        var on = Empty.IsVisible && main.S.OrbMotion && main.WindowState != WindowState.Minimized;   // Settings → Appearance → Motion
        Spin.BeginAnimation(AxisAngleRotation3D.AngleProperty, on ? App.Loop(new DoubleAnimation(0, 360, TimeSpan.FromSeconds(10)) { RepeatBehavior = RepeatBehavior.Forever }) : null);
        var sine = new SineEase();
        Bob.BeginAnimation(TranslateTransform3D.OffsetYProperty, on ? App.Loop(new DoubleAnimation(-0.08, 0.08, TimeSpan.FromSeconds(2.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine }) : null);
        var pulse = on ? App.Loop(new DoubleAnimation(0.85, 1.15, TimeSpan.FromSeconds(2.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine }) : null;
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        if (!on) { TiltX.BeginAnimation(AxisAngleRotation3D.AngleProperty, null); TiltZ.BeginAnimation(AxisAngleRotation3D.AngleProperty, null); }
    }

    // Ivo (glow): a light runs around the composer. The others: a plain border.
    public void ApplyTheme(string theme)
    {
        Frame.BorderThickness = new Thickness(Companion ? 0 : App.Line);   // the same weight as every line; the companion's window is its frame
        if (Companion) Frame.CornerRadius = new CornerRadius(0);
        PlaceComposer();
        if (theme != "glow") { Beam.SetResourceReference(Border.BackgroundProperty, "GlassStroke"); return; }
        var rot = new RotateTransform { CenterX = .5, CenterY = .5 };
        Beam.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = rot,
            GradientStops = { new(Color.FromArgb(0x1F, 255, 255, 255), 0), new(Color.FromArgb(0x1F, 255, 255, 255), 0.4), new(Color.FromRgb(0x22, 0xD3, 0xEE), 0.48),
                              new(Color.FromRgb(0x7C, 0x5C, 0xFF), 0.54), new(Color.FromArgb(0x1F, 255, 255, 255), 0.62), new(Color.FromArgb(0x1F, 255, 255, 255), 1) },
        };
        rot.BeginAnimation(RotateTransform.AngleProperty, App.Loop(new DoubleAnimation(0, 360, TimeSpan.FromSeconds(4)) { RepeatBehavior = RepeatBehavior.Forever }));
    }

    void PlaceComposer() => Beam.Margin = new Thickness(14, 6, 14, 14);

    public void SetActive(bool on, bool multi)
    {
        if (on && multi) Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x7C, 0x5C, 0xFF));
        else Frame.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        ClosePane.Visibility = SplitPane.Visibility = multi && !Companion ? Visibility.Visible : Visibility.Collapsed;
        Header.Cursor = multi ? Cursors.SizeAll : Cursors.Arrow;
        Header.ToolTip = multi ? "Drag to move" : null;
    }

    // The empty chat's greeting and suggestions: everyday ones in Chat mode, coding ones in Code mode.
    static readonly Dictionary<string, string[]> Ideas = new()
    {
        ["Coding"] = ["Explain how this project is put together", "Find a bug and fix it", "Add tests for the main parts"],
        ["Writing"] = ["Make this paragraph clearer and shorter", "Draft a post about something I worked on", "Give me three title ideas for my piece"],
        ["Studying"] = ["Quiz me on a topic I'm learning", "Explain a hard concept simply, with an example", "Make a one-week study plan"],
        ["Research"] = ["Compare two options with pros and cons", "Summarise what's known about a topic", "Find the weak points in an argument"],
        ["Work & business"] = ["Help me write a polite follow-up email", "Turn my meeting notes into action items", "Draft an agenda for a 30-minute meeting"],
        ["Everyday questions"] = ["Plan a 3-day trip to Jaipur on a budget", "Explain quantum computing like I'm 12", "Suggest a week of simple dinners"],
    };

    // The empty chat greets you by name (when Settings → Profile has one) and suggests things from what you use Ivo
    // for; Code mode always offers coding ideas.
    public void Greet(bool code)
    {
        var me = main.S.Profile;
        var hour = DateTime.Now.Hour;
        var hello = hour < 5 ? "Good night" : hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
        EmptyTitle.Text = me is { Name: not "" } ? $"{hello}, {me.Name.Split(' ')[0]}" : code ? "What are we building today?" : "What can I help with?";
        Suggestions.Children.Clear();
        Suggestions.Visibility = code ? Visibility.Collapsed : Visibility.Visible;   // Code mode shows just the greeting
        var uses = code ? ["Coding"] : me?.Uses is { Count: > 0 } u ? u : ["Everyday questions", "Work & business", "Coding"];
        // a few from each use, taking turns, up to three
        var ideas = Enumerable.Range(0, 3).SelectMany(i => uses.Select(x => Ideas.GetValueOrDefault(x)?.ElementAtOrDefault(i))).OfType<string>().Take(3).ToList();
        if (!code && me is { Work: not "" } && ideas.Count == 3) ideas[2] = $"Help me with a typical task in my work as {me.Work}";
        foreach (var s in ideas)
        {
            var b = new Button { Style = (Style)FindResource("Card"), Content = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap } };   // s is a suggestion
            b.Click += (_, _) => Send(s);
            Suggestions.Children.Add(b);
        }
    }

    // ---------- chat & options ----------
    ProviderCfg? Cfg => Chat.Provider == Router.Name && main.S.AutoOn ? Router.Cfg
        : Chat.Provider == IvoModel.CodingProviderName ? IvoModel.CodingProvider
        : main.S.Providers.FirstOrDefault(p => p.Name == Chat.Provider);
    string CurrentModel => Chat.Model ?? "";

    public bool Answering => cts != null;

    // ---------- the agent map's door: a chat-bar pill with this chat's helper-agent count ----------
    void ShowAgents()
    {
        var runs = Subagents.RunsOf(Chat.Id);
        var busy = runs.Count(r => r.Ended == null);
        AgentsText.Text = $"{runs.Count} agent{(runs.Count == 1 ? "" : "s")}";
        if (busy > 0) AgentsDot.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)); else AgentsDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Dim");
        AgentsButton.ToolTip = $"{runs.Count} agent{(runs.Count == 1 ? "" : "s")}{(busy > 0 ? " · working" : "")} · open the agent map";
    }
    void AgentsButton_Click(object s, RoutedEventArgs e) => main.ShowAgentMap(this);

    // key pool switch: shows when this chat's provider has several keys; on, they take turns and a spent one is skipped
    // Settings → Chats: spelling in the message box, and the token / cost count in the header
    public void ApplyChatPrefs() { Input.SpellCheck.IsEnabled = main.S.SpellCheck; UpdateUsage(); if (!Busy) ShowChat(Chat); }   // redraw: folded cards, thinking, agent prompts

    void ShowPool()
    {
        var keys = Cfg is { Kind: not ("auto" or "cli") } c ? Router.Keys(Store.Unprotect(c.Key)).Length : 0;
        PoolButton.Visibility = keys > 1 ? Visibility.Visible : Visibility.Collapsed;
        PoolButton.Opacity = main.S.PoolKeys ? 1 : 0.45;
        PoolButton.ToolTip = main.S.PoolKeys ? $"Key pool on · {keys} keys" : "Key pool off · first key only";
    }
    void PoolButton_Click(object s, RoutedEventArgs e) { main.S.PoolKeys = !main.S.PoolKeys; main.SaveSettings(); ShowPool(); }
    // How far a reply reaches across the chat: text, tool cards, code and thinking all stop here, however wide the window.
    public const double ReplyWidth = 1050;
    const string FullReply = "full-reply";   // tool cards: as wide as a reply can be (other reply parts just never exceed it)

    void Widen(FrameworkElement el) => el.Width = Math.Max(0, Math.Min(ReplyWidth, Log.ActualWidth - el.Margin.Left - el.Margin.Right));

    void FullWidth(FrameworkElement el)
    {
        el.Tag = FullReply; el.HorizontalAlignment = HorizontalAlignment.Left;
        Widen(el);
    }

    public void ShowChat(Chat c) => ShowChat(c, all: false);
    public void ShowChat(Chat c, bool all)
    {
        cts?.Cancel(); cts = null; SetSending(false); Quote(null);
        Dispatcher.BeginInvoke(ShowAgents);
        Chat = c;
        ShowAgents();
        if (Cfg == null) Chat.Provider = main.S.Providers.FirstOrDefault(p => p.Name == main.S.Provider)?.Name ?? main.S.Providers.FirstOrDefault()?.Name;
        TitleText.Text = c.Title;
        if (!Companion) main.UpdateIncognitoButton(); else IncogBtn.Visibility = Visibility.Collapsed;
        Frame.BorderBrush = c.Incognito ? new SolidColorBrush(Color.FromArgb(0x88, 0x9C, 0x7C, 0xFF)) : null;
        if (!c.Incognito) Frame.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        main.CodeChat_FocusChanged(this, default);
        Log.Children.Clear(); sentBubbles.Clear(); shown.Clear();
        var empty = c.Messages.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PlaceComposer();
        alwaysAllow.Clear();
        var results = c.Messages.Where(m => m.Role == "tool" && m.CallId != null).GroupBy(m => m.CallId!).ToDictionary(g => g.Key, g => g.Last());
        // Long chats open with their latest messages (from a message you sent); the rest appear on request.
        var from = all || c.Messages.Count <= 20 ? 0 : Math.Max(0, c.Messages.FindLastIndex(c.Messages.Count - 20, m => m.Role == "user"));   // a screen or two: switching chats and modes stays quick
        if (from > 0)
        {
            var more = new Button { Content = $"Show {from} earlier messages", Style = (Style)FindResource("Plain"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 8) };
            more.Click += (_, _) => ShowChat(c, all: true);
            Log.Children.Add(more);
        }
        for (int i = from; i < c.Messages.Count; i++)
        {
            var m = c.Messages[i];
            if (m.Role == "tool") continue;
            if (m.Calls == null || m.Text != "") Bubble(m.Role == "user", m.Text, animate: false, images: m.Images, index: i);
            else if (m.Thought != null) StandaloneThinking(m.Thought, animate: false);
            if (m.Calls is { Count: > 0 } && m.Seconds is { } sec) { Steps(false); runSeconds += sec; }
            if (m.Calls is { Count: > 0 } cs) toolGroup = ToolGroup(cs, animate: false);
            foreach (var call in m.Calls ?? [])
            {
                var res = results.GetValueOrDefault(call.Id);
                var (cardBody, _, _) = ToolCard(call, res?.Text ?? "", animate: false, before: res?.Undo?.Before is { } b ? Agent.Text(Convert.FromBase64String(b)) : null, undo: res?.Undo);
                if (res?.Detail is { Length: > 0 } transcript) AddTranscript(cardBody, transcript);
            }
            toolGroup = null;
        }
        CloseRun();
        UpdateUsage();
        RefreshProviders();
        main.WorkspaceChanged(this);
        Scroller.ScrollToEnd();
    }

    // Called when providers are added/removed in Settings.
    public void RefreshProviders()
    {
        loading = true;
        if (Cfg == null) Chat.Provider = main.S.Providers.FirstOrDefault()?.Name;
        ProviderPick.ItemsSource = ProviderNames();
        ProviderPick.SelectedItem = Chat.Provider;
        Temp.Value = Math.Min(Chat.Temp, 1);
        SystemPrompt.Text = Chat.System; PinnedContext.Text = Chat.Context;
        TemplatePick.ItemsSource = Personas.Concat(main.S.Personas).Select(p => p.Name).Prepend("None").ToList();
        TemplatePick.SelectedItem = Personas.Concat(main.S.Personas).FirstOrDefault(p => p.Prompt == Chat.System)?.Name ?? (Chat.System == "" ? "None" : null);
        foreach (RadioButton b in EffortPick.Children) b.IsChecked = (string)b.Tag == Chat.Effort;
        foreach (RadioButton b in ModePick.Children) b.IsChecked = (string)b.Tag == (Chat.Mode ?? "interactive");
        foreach (RadioButton b in PermPick.Children) b.IsChecked = (string)b.Tag == Chat.Permission;
        SandboxBox.IsChecked = Chat.Sandbox;
        ElevatedBox.IsChecked = Chat.Elevated;
        loading = false;
        EmptyHint.Text = Cfg == null ? "Add a provider in Settings to start." : "Pick a model below and ask anything.";
        LoadModels();
        UpdateTempLabel();
        UpdateAgentUi();
        DrawQuickBar();
    }

    // ---------- quick actions: common combinations of mode, effort and permissions in one click ----------
    record QuickAction(string Icon, string Name, string Tip, string Effort, string Mode, string Perm, bool Sandbox, string Lead = "");
    static readonly QuickAction[] QuickActions =
    [
        new("", "Quick answer", "Fast and direct: no extra thinking, step by step", "off", "interactive", "manual", false),
        new("", "Think deeply", "Maximum reasoning for hard problems", "high", "interactive", "manual", false),
        new("", "Plan first", "Lays out a plan and waits for your go-ahead", "medium", "plan", "manual", false),
        new("", "Autopilot", "Works on its own until done; low-risk actions run without asking", "medium", "autopilot", "assisted", false),
        new("", "Safe review", "Look but don't touch: plan mode, every action asks, terminal sandboxed", "medium", "plan", "manual", true,
            "Review this carefully. Point out bugs, risks and improvements, most important first:\n\n"),
        new("", "Research", "Thinks it through and cites sources (reads web pages when a folder is open)", "medium", "interactive", "assisted", false,
            "Research this thoroughly. Compare viewpoints, give the key facts and cite your sources:\n\n"),
    ];

    void DrawQuickBar()
    {
        QuickBar.Children.Clear();
        QuickBar.Visibility = Companion ? Visibility.Collapsed : Visibility.Visible;
        // fold toggle: folded shows only this chip (with the active preset's name, if any)
        var folded = main.S.QuickBarFolded;
        var current = QuickActions.FirstOrDefault(q => Chat.Quick == q.Name && Chat.Effort == q.Effort && (Chat.Mode ?? "interactive") == q.Mode && Chat.Permission == q.Perm && Chat.Sandbox == q.Sandbox);
        var toggleText = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        toggleText.Inlines.Add(new System.Windows.Documents.Run(folded ? "" : "") { FontFamily = App.IconFont, FontSize = 14, BaselineAlignment = BaselineAlignment.Center });
        toggleText.Inlines.Add(new System.Windows.Documents.Run(folded ? "  Quick actions" + (current != null ? $" · {current.Name}" : "") : ""));
        toggleText.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var toggle = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(folded ? 10 : 8, 4, folded ? 11 : 8, 5), Margin = new Thickness(0, 0, 6, 6),
                                  Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = toggleText, ToolTip = folded ? "Show quick actions" : "Hide quick actions" };
        toggle.MouseEnter += (_, _) => toggle.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x9C, 0x7C, 0xFF));
        toggle.MouseLeave += (_, _) => toggle.Background = Brushes.Transparent;
        toggle.MouseLeftButtonUp += (_, _) => { main.S.QuickBarFolded = !main.S.QuickBarFolded; main.SaveSettings(); DrawQuickBar(); };
        QuickBar.Children.Add(toggle);
        if (folded) return;
        foreach (var q in QuickActions)
        {
            var on = Chat.Quick == q.Name && Chat.Effort == q.Effort && (Chat.Mode ?? "interactive") == q.Mode && Chat.Permission == q.Perm && Chat.Sandbox == q.Sandbox;
            var label = new TextBlock { FontSize = 12.5 };
            label.Inlines.Add(new System.Windows.Documents.Run(q.Icon) { FontFamily = App.IconFont, FontSize = 13 });
            label.Inlines.Add(new System.Windows.Documents.Run("  " + q.Name));
            // a squircle chip (8.01: keeps App.Round from turning it into a card corner)
            var chip = new Border { CornerRadius = new CornerRadius(8, 8, 8, 8.01), Padding = new Thickness(10, 5, 11, 6), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1),
                                    Cursor = Cursors.Hand, ToolTip = q.Tip, Child = label };
            if (on)
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x9C, 0x7C, 0xFF));
                chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x9C, 0x7C, 0xFF));
                label.Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xC9, 0xFF));
            }
            else
            {
                chip.SetResourceReference(Border.BackgroundProperty, "Glass");
                chip.SetResourceReference(Border.BorderBrushProperty, "Separator");
                label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                chip.MouseEnter += (_, _) => label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                chip.MouseLeave += (_, _) => label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            }
            chip.MouseLeftButtonUp += (_, _) => ApplyQuick(q);
            QuickBar.Children.Add(chip);
        }
    }

    void ApplyQuick(QuickAction q)
    {
        var on = Chat.Quick == q.Name && Chat.Effort == q.Effort && (Chat.Mode ?? "interactive") == q.Mode && Chat.Permission == q.Perm && Chat.Sandbox == q.Sandbox;
        if (on)   // clicked again: back to the settings' defaults, and the lead-in out of the box
        {
            Chat.Quick = null; Chat.Effort = main.S.Effort; Chat.Mode = main.S.Mode; Chat.Permission = main.S.Permission; Chat.Sandbox = main.S.Sandbox;
            if (q.Lead != "" && Input.Text.StartsWith(q.Lead)) Input.Text = Input.Text[q.Lead.Length..];
        }
        else
        {
            Chat.Quick = q.Name; Chat.Effort = q.Effort; Chat.Mode = q.Mode; Chat.Permission = q.Perm; Chat.Sandbox = q.Sandbox;
            if (q.Lead != "" && !Input.Text.StartsWith(q.Lead)) Input.Text = q.Lead + Input.Text;
        }
        RefreshProviders();   // syncs the options panel and redraws the bar
        Capture();
        FocusInput();
    }

    static readonly Dictionary<string, (string Icon, string Name, string Hint)> Modes = new()
    {
        ["interactive"] = ("", "Interactive", "Step-by-step collaboration."),
        ["plan"] = ("", "Plan", "Plans first, does the work once you say go."),
        ["autopilot"] = ("", "Autopilot", "Works on its own, no clarifying questions."),
    };

    // The pill beside the input: mode icon · provider · model · effort.
    void UpdateSummary()
    {
        var m = Modes.GetValueOrDefault(Chat.Mode ?? "");
        if (m.Name == null) m = Modes["interactive"];
        ModeIcon.Text = m.Icon;
        // short on the button (the model, or the agent's name when it picks its own model); everything in the tooltip
        var model = Chat.Provider == IvoModel.CodingProviderName ? IvoModel.CodingModelName
            : CurrentModel is "" or "default" ? Chat.Provider ?? "" : CurrentModel[(CurrentModel.LastIndexOf('/') + 1)..];
        ModelFieldText.Text = CurrentModel is "" ? "Choose a model" : model;
        ProviderFieldText.Text = Chat.Provider ?? "Add a provider in Settings";
        Summary.Text = model + (Chat.Effort == "off" ? "" : $" · {Chat.Effort}");
        var folder = Agent.HasWorkspace(Chat) ? $" · folder {Path.GetFileName(Chat.Workspace!.TrimEnd('\\'))}" : "";
        var cfg = main.S.Providers.FirstOrDefault(p => p.Name == Chat.Provider);
        var local = cfg != null && (cfg.Kind == "llama" || cfg.Kind is not ("cli" or "auto") && Redact.IsLocal(cfg.BaseUrl));
        WhereIcon.Text = local ? "" : "";   // a PC, or a cloud
        if (Chat.LocalOnly) WhereIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        if (!Chat.LocalOnly) WhereIcon.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var where = Chat.LocalOnly ? "Local only: nothing leaves this PC or your network" : local ? "Stays on this PC / your network" : $"Sent to {Chat.Provider}'s servers";
        OptionsToggle.ToolTip = $"{where}\n{m.Name} mode · {Chat.Provider} · {CurrentModel}" + (Chat.Effort == "off" ? "" : $" · effort {Chat.Effort}") + folder;
    }

    void LoadModels()
    {
        loading = true;
        var cached = Cfg != null ? main.ModelCache.GetValueOrDefault(Cfg.Name) : null;
        IEnumerable<string> available = cached is { } current ? current : Api.ModelsFor(Cfg);
        if (string.IsNullOrEmpty(Chat.Model) || !available.Contains(Chat.Model))
            Chat.Model = main.DefaultModel(Chat.Provider ?? "", available);
        ModelSearch.Text = "";
        ShowModels(ModelItems(available));
        loading = false;
        ShowPool();
        if (Cfg is { Kind: not "llama" } && cached == null) _ = LoadLiveModels(Cfg);
    }

    // Pinned models first, then the rest; each carries its last known status.
    List<ModelItem> ModelItems(IEnumerable<string> ids)
    {
        var s = main.S; var prov = Chat.Provider ?? "";
        var def = s.Models.GetValueOrDefault(prov);
        var tooled = Agent.ToolsFor(Chat).Count > 0;   // a folder, skills or connections: tools are offered, so say which models can't take them
        var items = ids.Distinct().Select(id => new ModelItem(id, s.Pinned.Contains($"{prov}|{id}"),
                                                              prov == IvoModel.CodingProviderName || Router.Virtual.Contains(id) ? "ok" : s.ModelStatus.GetValueOrDefault($"{prov}|{id}"), id == def, Cli.Speed(prov, id),
                                                              InUse: id == Chat.Model, NoTools: tooled && Router.NoTools(prov, id))).ToList();
        // "Only working": keep checked-OK models (plus pinned and the one in use), once at least one is known to work.
        if (Cfg?.OnlyWorking != false && items.Any(m => m.Status == "ok"))
            items = items.Where(m => m.Status == "ok" || m.Pinned || m.IsDefault || m.Id == Chat.Model).ToList();
        return items.OrderByDescending(m => m.Pinned).ThenBy(m => m.Vendor, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Short, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Pinned and the rest under their own headers; the search box filters; the chat's model is selected.
    void ShowModels(List<ModelItem> items)
    {
        var v = new ListCollectionView(items);
        if (items.Select(m => m.Group).Distinct().Count() > 1) v.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelItem.Group)));
        var q = ModelSearch.Text.Trim();
        if (q != "") v.Filter = o => ((ModelItem)o).Id.Contains(q, StringComparison.OrdinalIgnoreCase);
        var was = loading; loading = true;
        ModelList.ItemsSource = v;
        ModelList.SelectedItem = items.FirstOrDefault(m => m.Id == Chat.Model);
        loading = was;
        if (ModelList.SelectedItem != null) ModelList.ScrollIntoView(ModelList.SelectedItem);
        var none = q != "" && v.IsEmpty;
        CustomModelHint.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        CustomModelHint.Text = $"No match. Enter uses “{q}”.";
        ModelSearchHint.Visibility = q == "" ? Visibility.Visible : Visibility.Collapsed;
    }

    List<ModelItem> ListedModels => ModelList.ItemsSource is ListCollectionView v ? v.SourceCollection.Cast<ModelItem>().ToList() : [];

    void ModelSearch_Changed(object s, TextChangedEventArgs e) => ShowModels(ModelItems(ListedModels.Select(m => m.Id)));
    void ModelSearch_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Down && !ModelList.Items.IsEmpty) { e.Handled = true; ModelList.SelectedIndex = Math.Max(0, ModelList.SelectedIndex); (ModelList.ItemContainerGenerator.ContainerFromIndex(ModelList.SelectedIndex) as UIElement)?.Focus(); }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var q = ModelSearch.Text.Trim();
        if (q == "") return;
        var hit = ModelList.Items.Cast<ModelItem>().FirstOrDefault();
        UseModel(hit?.Id ?? q);
        ModelSearch.Text = "";
    }
    void ModelList_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!loading && ModelList.SelectedItem is ModelItem m && m.Id != Chat.Model) UseModel(m.Id);
    }
    void UseModel(string id)
    {
        Chat.Model = id;
        ShowModels(ModelItems(ListedModels.Select(m => m.Id).Append(id)));
        Capture();
        OpenModels(false);
    }

    // The model field opens and closes the search and list under it.
    void ModelField_Click(object s, RoutedEventArgs e) => OpenModels(ModelBox.Visibility != Visibility.Visible);
    void OpenModels(bool open)
    {
        ModelBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ModelChevron.Text = open ? "\uE70E" : "\uE70D";
        if (open) OpenProviders(false);
        if (open) { ModelSearch.Text = ""; ModelSearch.Focus(); if (ModelList.SelectedItem != null) ModelList.ScrollIntoView(ModelList.SelectedItem); }
    }
    void OptionsPanel_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; OptionsToggle.IsChecked = false; OptionsToggle.Focus(); }
    }
    // Clicks anywhere outside the panel (or its button) close it.
    void Window_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (OptionsToggle.IsChecked != true || e.OriginalSource is not DependencyObject d) return;
        // a dropdown's list opens in its own pop-up window: clicks there belong to the panel
        if (d is Visual v && PresentationSource.FromVisual(v) != PresentationSource.FromVisual(this)) return;
        if (IsInside(d, OptionsPanel) || IsInside(d, OptionsToggle)) return;
        OptionsToggle.IsChecked = false;
    }
    static bool IsInside(DependencyObject? d, DependencyObject root)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == root) return true;
        return false;
    }

    void SetStatus(string model, string status)
    {
        main.S.ModelStatus[$"{Chat.Provider}|{model}"] = status;
        main.SaveSettings();
    }

    // Pin and star toggle on mouse-down (handled), so they don't also select the row or close the list.
    void Pin_MouseDown(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((s as FrameworkElement)?.DataContext is not ModelItem m) return;
        var key = $"{Chat.Provider}|{m.Id}";
        if (!main.S.Pinned.Remove(key)) main.S.Pinned.Add(key);
        main.SaveSettings();
        RefreshModelList();
    }
    void Star_MouseDown(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((s as FrameworkElement)?.DataContext is not ModelItem m || Chat.Provider is not { } prov) return;
        if (m.IsDefault) main.S.Models.Remove(prov); else main.S.Models[prov] = m.Id;
        main.SaveSettings();
        RefreshModelList();
    }
    void RefreshModelList() => ShowModels(ModelItems(ListedModels.Select(m => m.Id)));

    // Built-in model names go stale; fetch the provider's live list once per session.
    async Task LoadLiveModels(ProviderCfg p)
    {
        try
        {
            var live = await Api.ListModels(p, Store.Unprotect(p.Key));
            if (live.Count == 0) return;
            main.ModelCache[p.Name] = live;
            if (Cfg?.Name == p.Name) LoadModels();
        }
        catch { /* offline or bad key: keep the built-in list */ }
    }

    // Toolbar changed: remember on the chat, and as the default for new chats.
    void Capture()
    {
        if (loading) return;
        DrawQuickBar();
        var s = main.S;
        if (Chat.Provider != IvoModel.CodingProviderName) s.Provider = Chat.Provider ?? "";   // virtual model: per-chat only, not a missing Settings provider
        s.Effort = Chat.Effort; s.Mode = Chat.Mode; s.Workspace = Chat.Workspace; s.Permission = Chat.Permission; s.Sandbox = Chat.Sandbox; s.Temp = Chat.Temp; s.System = Chat.System;
        main.SaveSettings();
        Store.SaveChat(Chat);
        UpdateSummary();
    }

    void ProviderField_Click(object s, RoutedEventArgs e) => OpenProviders(ProviderBox.Visibility != Visibility.Visible);
    List<string> ProviderNames()
    {
        var names = main.S.Providers.Select(p => p.Name).ToList();
        if (IvoModel.Ready) names.Add(IvoModel.CodingProviderName);
        if (main.S.AutoOn) names.Insert(0, Router.Name);
        return names;
    }
    void OpenProviders(bool open)
    {
        ProviderBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ProviderChevron.Text = open ? "\uE70E" : "\uE70D";
        if (open) { loading = true; ProviderPick.ItemsSource = ProviderNames(); ProviderPick.SelectedItem = Chat.Provider; loading = false; }
        if (open) OpenModels(false);
    }

    void ProviderPick_Up(object s, MouseButtonEventArgs e) => OpenProviders(false);   // also when the current one is clicked again

    void Provider_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!loading && ProviderBox.IsVisible) OpenProviders(false);
        if (loading || ProviderPick.SelectedItem is not string p || p == Chat.Provider) return;
        Chat.Provider = p; Chat.Model = null; LoadModels(); Capture();
    }
    void Effort_Checked(object s, RoutedEventArgs e)
    {
        if (loading || s is not RadioButton { Tag: string v }) return;
        Chat.Effort = v; UpdateTempLabel(); Capture();
    }
    void Mode_Checked(object s, RoutedEventArgs e)
    {
        if (loading || s is not RadioButton { Tag: string v }) return;
        Chat.Mode = v; UpdateAgentUi(); Capture();
    }
    void Temp_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        Chat.Temp = Math.Round(Temp.Value, 1); UpdateTempLabel(); Capture();
    }
    void System_Changed(object s, TextChangedEventArgs e) { if (!loading) { Chat.System = SystemPrompt.Text; Capture(); } }
    void Pinned_Changed(object s, TextChangedEventArgs e) { if (!loading) { Chat.Context = PinnedContext.Text; Store.SaveChat(Chat); } }

    // Ready-made system prompts; the user's own ones are added with "Save as template".
    static readonly Persona[] Personas =
    [
        new("Code reviewer", "You are a senior code reviewer. Point out bugs, risks and unclear code first, most important first, with the fix for each. Be specific and brief."),
        new("Translator", "Translate what I send into English (or into the language I name), keeping meaning, tone and formatting. Reply with the translation only."),
        new("Tutor", "Teach step by step. Check what I already know, explain with small examples, and ask me a short question before moving on."),
        new("Editor", "Edit my writing for clarity and flow. Keep my voice. Show the edited text, then list the main changes in a few bullets."),
        new("Concise", "Answer in as few words as possible. No preamble, no summary at the end."),
        new("Brainstorm partner", "Generate many varied ideas quickly, then pick the three strongest and say why."),
    ];
    void Template_Changed(object s, SelectionChangedEventArgs e)
    {
        if (loading || TemplatePick.SelectedItem is not string name) return;
        SystemPrompt.Text = name == "None" ? "" : Personas.Concat(main.S.Personas).FirstOrDefault(p => p.Name == name)?.Prompt ?? SystemPrompt.Text;
    }
    async void SaveTemplate_Click(object s, RoutedEventArgs e)
    {
        if (SystemPrompt.Text.Trim() == "") { _ = main.Notice("Nothing to save", "Write a system prompt first."); return; }
        var name = await main.AskText("Save as template", "A short name for this system prompt:", "", "Save");
        if (string.IsNullOrWhiteSpace(name)) return;
        main.S.Personas.RemoveAll(p => p.Name == name.Trim());
        main.S.Personas.Add(new(name.Trim(), SystemPrompt.Text.Trim())); main.SaveSettings();
        RefreshProviders();
    }
    void PinFile_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pin a file to this chat", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            try { PinnedContext.Text += (PinnedContext.Text == "" ? "" : "\n\n") + $"**{System.IO.Path.GetFileName(f)}**\n" + Docs.Read(f); }
            catch (Exception ex) { _ = main.Notice("Couldn't read the file", ex.Message); }
    }

    // Reasoning (effort on) requests don't accept temperature, so it isn't sent then.
    void UpdateTempLabel()
    {
        Temp.IsEnabled = Chat.Effort == "off";
        TempLabel.Text = Chat.Effort == "off" ? $"TEMPERATURE  {Chat.Temp:0.0}" : "TEMPERATURE  (not used while effort is on)";
    }

    // ---------- sending ----------
    void Send_Click(object s, RoutedEventArgs e)
    {
        if (cts != null) cts.Cancel(); else Send(Input.Text);
    }

    readonly Dictionary<int, StackPanel> sentBubbles = [];   // your messages on screen, by their place in the chat
    readonly Dictionary<int, Border> shown = [];             // every message bubble on screen, by its place in the chat

    // Search found this chat: scroll to the first message with the words (earlier ones are loaded if needed) and outline it
    // for a moment, so it's easy to spot.
    public void FindInChat(string q)
    {
        var at = Chat.Messages.FindIndex(m => m.Role != "tool" && m.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;
        if (!shown.ContainsKey(at)) ShowChat(Chat, all: true);
        if (!shown.TryGetValue(at, out var b)) return;
        Dispatcher.BeginInvoke(() =>
        {
            b.BringIntoView();
            var (brush, thick) = (b.BorderBrush, b.BorderThickness);
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)); b.BorderThickness = new Thickness(2);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t.Tick += (_, _) => { t.Stop(); b.BorderBrush = brush; b.BorderThickness = thick; };
            t.Start();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void Input_KeyDown(object s, KeyEventArgs e)
    {
        // Ctrl+1 … Ctrl+6: the quick actions, in the order of the bar (again on the active one turns it off)
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D6 && !Companion) { e.Handled = true; ApplyQuick(QuickActions[e.Key - Key.D1]); return; }
        // Ctrl+Shift+A: the agent map. Ctrl+Shift+Enter: send the message as a task for an agent.
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.A && !Companion) { e.Handled = true; main.ShowAgentMap(this); return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Enter && cts == null && Input.Text.Trim() != "")
        {
            e.Handled = true;
            Send("Hand this to an agent with delegate_to_agent (the general one unless one of mine fits better), give it everything it needs, then tell me what it found:\n\n" + Input.Text);
            return;
        }
        if (SlashMenu.Visibility == Visibility.Visible)
        {
            var n = SlashList.Items.Count;
            if (e.Key is Key.Down or Key.Up) { e.Handled = true; SlashList.SelectedIndex = (SlashList.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + n) % n; SlashList.ScrollIntoView(SlashList.SelectedItem); return; }
            if (e.Key is Key.Enter or Key.Tab && SlashList.SelectedItem is SavedPrompt sp) { e.Handled = true; UseSlash(sp); return; }
            if (e.Key == Key.Escape) { e.Handled = true; SlashMenu.Visibility = Visibility.Collapsed; return; }
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == (main.S.CtrlEnterSends ? ModifierKeys.Control : ModifierKeys.None)) { e.Handled = true; if (cts == null) Send(Input.Text); }
        // ↑ in an empty message box: edit the last message you sent
        if (e.Key == Key.Up && Input.Text == "" && cts == null)
        {
            var last = Chat.Messages.FindLastIndex(m => m.Role == "user");
            if (last >= 0 && sentBubbles.TryGetValue(last, out var bubble)) { e.Handled = true; bubble.BringIntoView(); EditPrompt(bubble, last); }
        }
    }

    internal async void Send(string text)
    {
        text = Agent.Dates(text.Trim());   // {{DATE}} and friends become today's values
        if ((text == "" && pending.Count == 0) || cts != null) return;
        if (text.Equals("/compact", StringComparison.OrdinalIgnoreCase)) { Input.Clear(); Compact_Click(this, new RoutedEventArgs()); return; }
        if (text.StartsWith("/compare ", StringComparison.OrdinalIgnoreCase) && !Companion) { Input.Clear(); main.Compare(text[9..].Trim()); return; }
        if (text.StartsWith("/clip ", StringComparison.OrdinalIgnoreCase))   // save a web page into the knowledge folder
        {
            var url = text[6..].Trim(); Input.Clear();
            try
            {
                var page = await Agent.FetchText(url, CancellationToken.None);
                if (page.StartsWith("Error")) throw new Exception(page);
                var body = page.Contains("\n\n") ? page[(page.IndexOf("\n\n") + 2)..] : page;
                var head = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3) ?? url;
                main.ClipToNotes(head.Length > 80 ? head[..80] : head, body, url);
            }
            catch (Exception ex) { _ = main.Notice("Couldn't clip the page", ex.Message); }
            return;
        }
        Empty.Visibility = Visibility.Collapsed; Scroller.Visibility = Visibility.Visible;
        PlaceComposer();
        var cfg = Cfg;
        var key = cfg != null ? Store.Unprotect(cfg.Key) : "";
        if (cfg == null || (Api.NeedsKey(cfg) && key == ""))
        {
            Bubble(false, cfg == null ? "Add a provider and its API key in Settings first." : $"Add your {cfg.Name} API key in Settings first.", error: true);
            main.OpenSettings(); return;
        }
        if (!await WithinBudget()) return;
        Input.Clear();
        if (quote != null)   // replying to something: it goes first, so just that gets addressed
        {
            text = string.Join("\n", quote.Replace("\r", "").Split('\n').Select(l => "> " + l)) + "\n\n" + text;
            Quote(null);
        }
        // text files are inlined into the message; images go as image parts
        foreach (var f in pending.Where(p => p.Img == null)) text += $"\n\n**{f.Name}**\n```\n{f.Text}\n```";
        var images = pending.Where(p => p.Img != null).Select(p => p.Img!).ToList();
        pending.Clear(); Attachments.Children.Clear();
        var title = text != "" ? text : "Image";
        if (Chat.Messages.Count == 0) TitleText.Text = Chat.Title = title.Length > 40 ? title[..40] + "…" : title;
        var msg = new Msg("user", text.Trim(), images.Count > 0 ? images : null, At: DateTime.Now);
        Chat.Messages.Add(msg);
        Bubble(true, msg.Text, images: msg.Images, index: Chat.Messages.Count - 1);   // added first, so the bubble can show its time
        if (cfg.Kind != "cli" && Agent.ToolsFor(Chat).Count > 0) { await RunAgent(cfg, key); return; }   // a workspace, or skills

        cts = new CancellationTokenSource();
        SetSending(true);
        var chat = Chat;   // switching chats mid-reply cancels it: what came back belongs to this chat, not the new one
        Api.Reply r;
        try
        {
            await Fit(cfg, key);
            try { (r, _) = await StreamReply(cfg, key, null, cfg.Kind == "cli" ? "Working…" : "Thinking…"); }
            catch (Exception ex) when (Context.TooLong(ex.Message) && cfg.Kind != "cli")
            {
                if (!await Fit(cfg, key, force: true)) throw;
                (r, _) = await StreamReply(cfg, key, null, "Thinking…");
            }
        }
        catch (Exception ex)
        {
            if (Chat != chat) { Store.SaveChat(chat); return; }   // the pane has moved on to another chat
            if (ex is OperationCanceledException) { Store.SaveChat(chat); Finish(); return; }   // stopped: keep the question
            var userMsg = Chat.Messages[^1];
            Chat.Messages.RemoveAt(Chat.Messages.Count - 1);
            // Retired model whose error names a replacement ("…please use models/X…"): switch, remember, retry once.
            if (Regex.Match(ex.Message, @"no longer available.*?use models/([\w.\-]+)", RegexOptions.Singleline) is { Success: true } m && m.Groups[1].Value != Chat.Model)
            {
                var old = Chat.Model;
                Chat.Model = m.Groups[1].Value; LoadModels();
                if (main.S.Models.GetValueOrDefault(Chat.Provider ?? "") == old) main.S.Models[Chat.Provider!] = Chat.Model!;   // the retired default moves too
                Capture();
                Log.Children.RemoveAt(Log.Children.Count - 1);   // the user bubble; Send adds it again

                Bubble(false, $"{old} is retired — switched to {Chat.Model} and retried.", error: true);
                Finish();
                pending.AddRange(userMsg.Images?.Select(i => new Pending("image", i)) ?? []);
                Send(userMsg.Text);
                return;
            }
            SetStatus(Chat.Model ?? "", ex.Message);
            var userBubble = Log.Children.Count > 0 ? Log.Children[Log.Children.Count - 1] : null;   // the question, re-added when sent again
            if (OfflineFallback(ex, () => { if (userBubble != null) Log.Children.Remove(userBubble); Send(userMsg.Text); })) { Finish(); return; }
            Bubble(false, ex.Message, error: true);
            Finish();
            OfferAuto(() => { pending.AddRange(userMsg.Images?.Select(i => new Pending("image", i)) ?? []); Send(userMsg.Text); }, userBubble);
            return;
        }
        if (Chat != chat)
        {
            chat.Messages.Add(new("assistant", r.Text, Thought: r.Thought != "" ? r.Thought : null, Usage: Priced(r.Usage), Seconds: lastSeconds, At: DateTime.Now));
            chat.Updated = DateTime.Now; Store.SaveChat(chat); return;
        }
        if (r.Text.Length > 0) SetStatus(Chat.Model ?? "", "ok");
        Chat.Messages.Add(new("assistant", r.Text, Thought: r.Thought != "" ? r.Thought : null, Usage: Priced(r.Usage), Seconds: lastSeconds, At: DateTime.Now));
        Bubble(false, r.Text, animate: false, index: Chat.Messages.Count - 1);
        if (Comparing != null) OfferPreference(Comparing);
        UpdateUsage();
        Chat.Updated = DateTime.Now;
        Store.SaveChat(Chat);
        main.ChatsChanged();
        Finish();

        void Finish() { cts = null; SetSending(false); Input.Focus(); }
    }

    // /compare: under each answer, "Best answer" records the pick (Auto leans towards models you prefer) and ends the round.
    public string? Comparing;   // the prompt being compared in this pane, until a winner is picked
    void OfferPreference(string prompt)
    {
        var b = new Button { Content = "\uE8E1  Best answer", Style = (Style)FindResource("Plain"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), ToolTip = "This one answered best: Auto will lean towards this model" };
        b.Click += (_, _) => main.PickCompareWinner(this, prompt);
        AddAnimated(b);
        compareButton = b;
    }
    Button? compareButton;
    public void EndCompare() { Comparing = null; if (compareButton != null) Log.Children.Remove(compareButton); compareButton = null; }

    void SetSending(bool on)
    {
        SendIcon.Text = on ? "" : "";   // stop / send
        SendBtn.ToolTip = on ? "Stop" : "Send (Enter)";
    }

    public void Stop() => cts?.Cancel();

    public void CloseDropdowns()
    {
        OptionsToggle.IsChecked = false;
    }

    // ---------- replying to a message, or to the part of it you selected ----------
    string? quote;

    public void Quote(string? text)
    {
        quote = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        QuoteText.Text = quote == null ? "" : Voice.Plain(quote).Replace("\n", "  ");
        QuoteBar.Visibility = quote == null ? Visibility.Collapsed : Visibility.Visible;
        if (quote != null) Input.Focus();
    }

    void QuoteClear_Click(object s, RoutedEventArgs e) => Quote(null);

    // select text in a message: a small Reply button comes up by the mouse
    System.Windows.Controls.Primitives.Popup? replyPop;

    void Log_MouseUp(object s, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is SelectableTextBlock tb)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var picked = tb.SelectedText;
                    if (picked.Length < 2) return;
                    if (replyPop == null)
                    {
                        var go = new Button { Style = (Style)FindResource("Shimmer"), Padding = new Thickness(12, 5, 12, 5), FontSize = 12 };
                        go.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "\uE97A", FontFamily = App.IconFont, FontSize = 13, Margin = new Thickness(0, 1, 7, 0) }, new TextBlock { Text = "Reply" } } };
                        go.Click += (_, _) => { replyPop!.IsOpen = false; Quote((string)replyPop.Tag); };
                        replyPop = new System.Windows.Controls.Primitives.Popup { Child = go, StaysOpen = false, AllowsTransparency = true, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, VerticalOffset = 10 };
                    }
                    replyPop.Tag = picked;
                    replyPop.IsOpen = false; replyPop.IsOpen = true;
                }, System.Windows.Threading.DispatcherPriority.Input);
                return;
            }
    }

    // ---------- message rendering ----------
    Border Bubble(bool user, string text, bool error = false, bool animate = true, UIElement? liveBody = null, List<Img>? images = null, int index = -1)
    {
        if (runSteps != null && liveBody == null) { var live = runLive; CloseRun(); runLive = live; }
        // a reply (Send quotes it as "> " lines first): the quoted message shows as its own faded bubble above, like a chat app's reply
        string? quoted = null;
        var flat = text.Replace("\r", "");
        if (user && flat.StartsWith("> ") && flat.IndexOf("\n\n") is var cut and > 0)
        {
            var lines = flat[..cut].Split('\n');
            if (lines.All(l => l.StartsWith('>'))) { quoted = string.Join("\n", lines.Select(l => l.TrimStart('>').TrimStart())); text = flat[(cut + 2)..]; }
        }
        var stack = new StackPanel();
        if (!user)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var who = new TextBlock { Text = $"{Chat.Provider} · {Chat.Model}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            who.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            if (index >= 0 && index < Chat.Messages.Count && Chat.Messages[index].At is { } at)   // when it arrived, right side of the header
            {
                var time = new TextBlock { Text = When.Stamp(at), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), ToolTip = $"Arrived {When.Full(at)}" };
                time.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                DockPanel.SetDock(time, Dock.Right);
                head.Children.Add(time);
            }
            head.Children.Add(who);
            stack.Children.Add(head);
        }
        foreach (var img in images ?? [])
        {
            var bmp = ToBitmap(img);
            stack.Children.Add(WithImageActions(new Image { Source = bmp, MaxHeight = 220, MaxWidth = 320 }, bmp, ""));
        }
        if (liveBody != null) stack.Children.Add(liveBody);
        else if (error)
            stack.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), Style = null, FontSize = 14, Foreground = Brushes.IndianRed,
            });
        else if (text != "") foreach (var el in RenderMarkdown(text, user)) stack.Children.Add(el);

        var b = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(user ? 60 : 0, 6, user ? 0 : 60, 6),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BorderThickness = new Thickness(user ? 0 : 1),
            MaxWidth = user ? double.PositiveInfinity : ReplyWidth,
        };
        if (user) b.Background = (Brush)FindResource("AccentGrad"); else b.SetResourceReference(Border.BackgroundProperty, "Glass");
        if (error) b.BorderBrush = Brushes.IndianRed; else b.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        if (quoted != null)
        {
            var q = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 8, 14, 14), Margin = new Thickness(0, 0, 0, -8), Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Right, BorderThickness = new Thickness(1), MaxWidth = 420,
                Child = new TextBlock { Text = Voice.Plain(quoted), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 60, FontSize = 12.5 },
            };
            q.SetResourceReference(Border.BackgroundProperty, "Glass"); q.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            ((TextBlock)q.Child).SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var label = new TextBlock { Text = "You replied", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            b.Child = null;   // the message moves into the inner bubble: it can't have two parents
            var main = new Border { Child = stack, CornerRadius = b.CornerRadius, Padding = b.Padding, Background = b.Background, HorizontalAlignment = HorizontalAlignment.Right };
            b.Child = new StackPanel { Children = { label, q, main } };
            b.Background = null; b.Padding = new Thickness(0);
        }
        if (index >= 0) shown[index] = b;
        if (liveBody != null || index < 0) { if (animate) AddAnimated(b); else Log.Children.Add(b); return b; }

        // Actions sit under the bubble as bare icons and only show while the message is hovered (or focused).
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, user ? 4 : 64, 0) };
        if (!error)
        {
            var copy = Action("", user ? "Copy message" : "Copy reply");
            copy.Click += async (_, _) =>
            {
                Clipboard.SetText(text);
                copy.Content = "";
                await Task.Delay(1500);
                copy.Content = "";
            };
            actions.Children.Add(copy);
        }
        if (!error)
        {
            var reply = Action("", "Reply to this (or select part of it to reply to just that)");
            reply.Click += (_, _) => Quote(text);
            actions.Children.Add(reply);
        }
        if (user)
        {
            var edit = Action("", "Edit and resend");
            edit.Click += (_, _) => EditPrompt(stack, index);
            sentBubbles[index] = stack;
            actions.Children.Add(edit);
            var versions = Chat.Branches.Count(br => br.At == index);
            if (versions > 0)   // earlier versions from here on: step through them
            {
                var prev = Action("\uE76B", "Previous version of the conversation from here");
                var label = new TextBlock { Text = $"{versions + 1} versions", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                var next = Action("\uE76C", "Next version of the conversation from here");
                prev.Click += (_, _) => SwitchBranch(index, back: true);
                next.Click += (_, _) => SwitchBranch(index, back: false);
                actions.Children.Add(prev); actions.Children.Add(label); actions.Children.Add(next);
            }
        }
        else
        {
            var speak = Action("\uE767", "Read aloud");
            speak.Click += (_, _) => main.ToggleRead(speak, text);   // click to read, click again to stop
            main.Speaker(speak, text);
            if (!main.S.ReadAloud) speak.Visibility = Visibility.Collapsed;   // Settings → Voice → Audio
            actions.Children.Add(speak);
            var retry = Action("", "Retry: ask again for a new reply");
            retry.Click += (_, _) => Retry(index);
            actions.Children.Add(retry);
            if (index < Chat.Messages.Count && !error)
            {
                var starred = Chat.Messages[index].Starred;
                var star = Action(starred ? "\uE735" : "\uE734", starred ? "Unstar" : "Star this reply (find it again from the ☆ in the title bar)");
                star.Click += (_, _) =>
                {
                    if (index >= Chat.Messages.Count) return;
                    var on = !Chat.Messages[index].Starred;
                    Chat.Messages[index] = Chat.Messages[index] with { Starred = on };
                    star.Content = on ? "\uE735" : "\uE734"; star.ToolTip = on ? "Unstar" : "Star this reply";
                    main.S.Stars.RemoveAll(x => x.ChatId == Chat.Id && x.Text == text);
                    if (on) main.S.Stars.Insert(0, new(text, Chat.Id, Chat.Title, DateTime.Now));
                    main.SaveSettings();
                    Store.SaveChat(Chat); main.ChatsChanged();
                };
                actions.Children.Add(star);
                var clip = Action("\uE70B", "Save to notes: a Markdown file in your knowledge folder");
                clip.Click += (_, _) => main.ClipToNotes(Chat.Title, text);
                actions.Children.Add(clip);
            }
        }
        var info = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
        var bar = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions); bar.Children.Add(info);
        var under = new StackPanel { HorizontalAlignment = b.HorizontalAlignment, Children = { b, bar } };
        var msg = !user && index < Chat.Messages.Count ? Chat.Messages[index] : null;
        // the reply's reasoning: grey "› Thinking" on the left of the action row, expands below it
        FrameworkElement? thinking = null, reasoning = null;
        if (msg?.Thought is { Length: > 0 } thought)
        {
            (thinking, reasoning) = ThinkingToggle(thought);
            thinking.Margin = new Thickness(0, 0, 14, 0); thinking.VerticalAlignment = VerticalAlignment.Center; thinking.Opacity = 0;
            info.Children.Add(thinking);
            under.Children.Add(reasoning);
        }
        FrameworkElement? details = null;
        if (msg != null && (msg.Usage != null || msg.Seconds != null || msg.At != null))
        {
            var stats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            void Stat(string? glyph, string text, string tip)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), ToolTip = tip, Background = Brushes.Transparent };
                if (glyph != null) item.Children.Add(new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) });
                item.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                foreach (TextBlock t in item.Children) t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                stats.Children.Add(item);
            }
            static string K(int n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();
            if (msg.Seconds is { } sec) Stat("\uE916", sec < 60 ? $"{sec:0.0}s" : $"{(int)sec / 60}m {(int)sec % 60}s", "How long the reply took");
            if (msg.Usage is { } usage)
            {
                Stat(null, $"↑{K(usage.In)}  ↓{K(usage.Out)}", $"Tokens: {usage.In:N0} sent, {usage.Out:N0} received");
                if (usage.Cost is { } cost) Stat(null, cost == 0 ? "free" : cost < 0.01 ? $"~${cost:0.0000}" : $"~${cost:0.00}", "Estimated cost, from public prices");
            }
            details = stats;
            info.Children.Add(stats);
        }
        if (user && index >= 0 && index < Chat.Messages.Count && Chat.Messages[index].At is { } sent)
        {
            details = new TextBlock { Text = When.Stamp(sent), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Margin = new Thickness(0, 0, 6, 0), ToolTip = When.Full(sent) };
            details.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            actions.Children.Insert(0, details);
        }
        var row = new Border { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 4), Child = under };
        void Show()
        {
            var on = row.IsMouseOver || row.IsKeyboardFocusWithin ? 1 : 0;
            foreach (var c in actions.Children.OfType<Button>()) c.Opacity = on;
            if (details != null) details.Opacity = on;
            if (thinking != null) thinking.Opacity = on == 1 || reasoning!.IsVisible ? 1 : 0;   // stays while the reasoning is open
        }
        Show();
        row.MouseEnter += (_, _) => Show(); row.MouseLeave += (_, _) => Show(); row.IsKeyboardFocusWithinChanged += (_, _) => Show();
        if (animate) AddAnimated(row); else Log.Children.Add(row);
        return row;
    }


    Button Action(string glyph, string tip) => new() { Content = glyph, ToolTip = tip, Style = (Style)FindResource("Ghost") };

    // ‹ / ›: the conversation from this message on becomes another saved version; the current one is kept in its place.
    void SwitchBranch(int at, bool back)
    {
        if (cts != null) return;
        var mine = Chat.Branches.Where(b => b.At == at).ToList();
        if (mine.Count == 0) return;
        var current = new Branch(at, Chat.Messages.GetRange(at, Chat.Messages.Count - at), Chat.Branches.Where(b => b.At > at).ToList());   // with its own deeper versions
        Chat.Branches.RemoveAll(b => b.At > at);
        var take = back ? mine[^1] : mine[0];
        Chat.Branches.Remove(take);
        Chat.Branches.AddRange(take.Nested ?? []);
        if (back) Chat.Branches.Insert(Chat.Branches.FindIndex(b => b.At == at) is var i and >= 0 ? i : Chat.Branches.Count, current);
        else Chat.Branches.Add(current);
        Chat.Messages.RemoveRange(at, Chat.Messages.Count - at);
        Chat.Messages.AddRange(take.Tail);
        if (at < Chat.SummaryUpTo) { Chat.Summary = null; Chat.SummaryUpTo = 0; }
        Chat.CliSession = null;
        Store.SaveChat(Chat);
        ShowChat(Chat);
    }

    // Retry: drop the reply (and anything after it) and send the prompt before it again.
    void Retry(int index)
    {
        if (cts != null) return;
        var u = Chat.Messages.FindLastIndex(Math.Min(index, Chat.Messages.Count - 1), m => m.Role == "user");
        if (u >= 0) Resend(u, Chat.Messages[u].Text);
    }

    void Resend(int userIndex, string text)
    {
        var old = Chat.Messages[userIndex];
        // the version being replaced isn't lost: it stays in the chat as a branch (‹ › on the message switches between them)
        var deeper = Chat.Branches.Where(b => b.At > userIndex).ToList();   // versions further down belong to the conversation being replaced
        Chat.Branches.RemoveAll(b => b.At > userIndex);
        if (Chat.Messages.Count > userIndex + 1) Chat.Branches.Add(new(userIndex, Chat.Messages.GetRange(userIndex, Chat.Messages.Count - userIndex), deeper));
        Chat.Messages.RemoveRange(userIndex, Chat.Messages.Count - userIndex);
        if (userIndex < Chat.SummaryUpTo) { Chat.Summary = null; Chat.SummaryUpTo = 0; }
        Chat.CliSession = null;   // the tool's session still holds the removed turns; start fresh with the chat sent along
        Store.SaveChat(Chat);
        ShowChat(Chat);
        pending.AddRange(old.Images?.Select(i => new Pending("image", i)) ?? []);
        Send(text);
    }

    // Edit a sent prompt in place; sending it replaces that prompt and everything after it.
    void EditPrompt(StackPanel stack, int index)
    {
        if (cts != null || index >= Chat.Messages.Count || stack.Children.OfType<TextBox>().Any()) return;
        var before = stack.Children.Cast<UIElement>().ToList();
        var box = new TextBox
        {
            Text = Chat.Messages[index].Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 340, MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14,
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0), FontSize = 12, Cursor = Cursors.Hand };
        var send = new Button { Content = "Send", Padding = new Thickness(14, 5, 14, 5), FontSize = 12, Cursor = Cursors.Hand };
        void Restore() { stack.Children.Clear(); foreach (var el in before) stack.Children.Add(el); }
        void Go() { if (box.Text.Trim() != "") Resend(index, box.Text); }
        cancel.Click += (_, _) => Restore();
        send.Click += (_, _) => Go();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Restore(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) { e.Handled = true; Go(); }
        };
        stack.Children.Clear();
        stack.Children.Add(box);
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, send } });
        box.Focus(); box.CaretIndex = box.Text.Length;
    }

    // ---------- rename ----------
    void Title_MouseDown(object s, MouseButtonEventArgs e) { if (e.ClickCount == 2) { e.Handled = true; BeginRename(); } }
    public void BeginRename()
    {
        TitleEdit.Text = Chat.Title;
        TitleText.Visibility = Visibility.Collapsed; TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus(); TitleEdit.SelectAll();
    }
    void TitleEdit_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; EndRename(true); }
        else if (e.Key == Key.Escape) { e.Handled = true; EndRename(false); }
    }
    void TitleEdit_LostFocus(object s, RoutedEventArgs e) => EndRename(true);
    void EndRename(bool keep)
    {
        if (TitleEdit.Visibility != Visibility.Visible) return;
        TitleEdit.Visibility = Visibility.Collapsed; TitleText.Visibility = Visibility.Visible;
        var t = TitleEdit.Text.Trim();
        if (!keep || t == "" || t == Chat.Title) return;
        TitleText.Text = Chat.Title = t;
        Store.SaveChat(Chat);
        main.ChatsChanged();
    }

    // ---------- pane focus, close, drag to reorder ----------
    void Pane_Activate(object s, MouseButtonEventArgs e) => main.ActivatePane(this);
    void ClosePane_Click(object s, RoutedEventArgs e) => main.ClosePane(this);
    void SplitPane_Click(object s, RoutedEventArgs e) => main.NewSidePane();

    // Drag only after a real move, and never from the header's buttons — otherwise a slightly
    // shaky click on split/close turned into a drag and the click was lost.
    Point? dragStart;
    void Header_MouseDown(object s, MouseButtonEventArgs e)
    {
        var onButton = e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null;
        dragStart = (main.IsMulti || Companion) && !onButton ? e.GetPosition(this) : null;   // the companion's chat can be swapped too
    }
    void Header_MouseMove(object s, MouseEventArgs e)
    {
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) { dragStart = null; return; }
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 3 && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 3) return;
        dragStart = null;
        DragDrop.DoDragDrop(Header, new DataObject(typeof(ChatPane), this), DragDropEffects.Move);
    }
    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
    void Pane_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ChatPane)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    void Pane_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ChatPane)) is not ChatPane src || src == this) return;
        if (src.Companion || Companion)
        {
            // between the companion and a pane in Ivo: the two chats trade places (each window keeps its own pane)
            if (src.cts != null || cts != null) return;   // not while either is answering
            var (mine, theirs) = (Chat, src.Chat);
            src.ShowChat(mine); ShowChat(theirs);
            main.ChatsChanged();
            return;
        }
        main.MovePane(src, this);
    }
}
