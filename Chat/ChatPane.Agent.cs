using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace Ivo;

// Agent turns: the tool loop, tool cards with their diffs and undo, and the options that govern them.
public partial class ChatPane
{
    // ---------- agent (chat has a workspace folder) ----------
    readonly HashSet<string> alwaysAllow = [];   // tools the user said "always allow" for, until the chat is switched

    // Steps until the model answers without calling a tool, or the mode's step limit. Each step streams in live.
    static bool NoToolSupport(string error) =>
        System.Text.RegularExpressions.Regex.IsMatch(error, @"support tool use|tools? (is |are )?not supported|does not support tools|tool.?calling is not supported|function calling is not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    async Task RunAgent(ProviderCfg cfg, string key)
    {
        var noTools = Router.NoTools(Chat.Provider ?? "", Chat.Model ?? "");   // known not to take tools: don't offer them
        cts = new CancellationTokenSource();
        SetSending(true);
        var failed = false;
        var chat = Chat;   // switching chats cancels the run: nothing more goes into the chat now on screen
        CloseRun(); runLive = true;
        var sources = new List<string>(); var searched = new List<string>();   // what the reply drew on, shown under it
        try
        {
            for (int step = 1; ; step++)
            {
                await Fit(cfg, key);
                Api.Reply r; bool stopped;
                var tools = noTools ? null : Agent.ToolsFor(Chat);
                try { (r, stopped) = await StreamReply(cfg, key, tools, "Working…"); }
                catch (Exception ex) when (Context.TooLong(ex.Message))
                {
                    if (!await Fit(cfg, key, force: true)) throw;
                    (r, stopped) = await StreamReply(cfg, key, tools, "Working…");
                }
                catch (Exception ex) when (!noTools && NoToolSupport(ex.Message))   // this model can't use tools: answer without them
                {
                    noTools = true;
                    main.S.ModelStatus[$"{Chat.Provider}|#notools|{Chat.Model}"] = DateTime.Now.ToString("o"); main.SaveSettings();   // not tried again for a week (Router.NoTools)
                    Bubble(false, $"{Chat.Model} can't use tools, so it answered without them.", error: false);
                    (r, stopped) = await StreamReply(cfg, key, null, "Thinking…");
                }
                if (Chat != chat) { Store.SaveChat(chat); return; }
                runSeconds += lastSeconds;
                List<ToolCall> calls = stopped ? [] : r.Calls;
                Chat.Messages.Add(new("assistant", r.Text, Calls: calls.Count > 0 ? calls : null, Blocks: calls.Count > 0 ? r.Blocks : null,
                                      Thought: r.Thought != "" ? r.Thought : null, Usage: Priced(r.Usage), Seconds: lastSeconds, At: DateTime.Now));
                if (r.Text != "") Bubble(false, r.Text, index: Chat.Messages.Count - 1);
                else if (r.Thought != "") StandaloneThinking(r.Thought, animate: true);
                UpdateUsage();
                if (calls.Count == 0) { ShowSources(sources, searched); break; }
                // agents run at once (they never need approval); everything else one after another, so approvals come in order
                toolGroup = ToolGroup(calls, animate: true);
                var started = calls.Select(c => c.Name == "delegate_to_agent" ? RunTool(c) : null).ToList();
                for (int i = 0; i < calls.Count; i++)
                {
                    (string Text, UndoInfo? Undo, string? Detail) result;
                    try { result = await (started[i] ?? RunTool(calls[i])); }
                    catch (OperationCanceledException) when (Chat != chat)
                    {
                        foreach (var c in calls.Skip(i)) chat.Messages.Add(new("tool", "Cancelled by the user.", CallId: c.Id, Name: c.Name));
                        Store.SaveChat(chat); return;
                    }
                    catch (OperationCanceledException)
                    {
                        // every call needs a result, or the next request is rejected
                        foreach (var c in calls.Skip(i)) Chat.Messages.Add(new("tool", "Cancelled by the user.", CallId: c.Id, Name: c.Name));
                        throw;
                    }
                    Chat.Messages.Add(new("tool", result.Text, CallId: calls[i].Id, Name: calls[i].Name, Undo: result.Undo, Detail: result.Detail));
                    try
                    {
                        var a = JsonNode.Parse(calls[i].Args);
                        if (calls[i].Name == "fetch_url" && (string?)a?["url"] is { Length: > 0 } u && !result.Text.StartsWith("Error")) sources.Add(u);
                        if (calls[i].Name == "web_search" && (string?)a?["query"] is { Length: > 0 } q) searched.Add(q);
                    }
                    catch { }
                }
                toolGroup = null;
                Store.SaveChat(Chat);
                if (step >= Agent.MaxSteps(Chat))
                {
                    Bubble(false, $"Stopped after {step} steps. Say \"continue\" to keep going.", error: true);
                    break;
                }
            }
        }
        catch (Exception) when (Chat != chat) { Store.SaveChat(chat); return; }
        catch (OperationCanceledException) { Bubble(false, "_(stopped)_"); }
        catch (Exception ex)
        {
            failed = true; Store.Fail($"Reply · {Chat.Provider} · {Chat.Model}", ex.Message); SetStatus(Chat.Model ?? "", ex.Message);
            if (!OfflineFallback(ex, () => Dispatcher.BeginInvoke(async () => { if (Cfg is { } c2) await RunAgent(c2, Store.Unprotect(c2.Key)); })))
            {
                Bubble(false, ex.Message, error: true);
                OfferAuto(() => _ = RunAgent(Router.Cfg, ""));
            }
        }
        if (!failed) SetStatus(Chat.Model ?? "", "ok");
        if (!failed && Chat.Mode == "plan" && Agent.HasWorkspace(Chat)) StartImplementationButton();
        Chat.Updated = DateTime.Now;
        Store.SaveChat(Chat);
        main.ChatsChanged();
        toolGroup = null; CloseRun(); cts = null; SetSending(false); Input.Focus();
    }

    // After a failed reply: one click runs the same thing through Auto (any working model), when Auto is on and isn't already in use.
    void OfferAuto(Action retry, UIElement? alsoRemove = null)
    {
        if (!main.S.AutoOn || Chat.Provider == Router.Name) return;
        var b = new Button { Content = "Try with Auto", Style = (Style)FindResource("Plain"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Switch this chat to Auto and send again" };
        b.Click += (_, _) =>
        {
            Log.Children.Remove(b); if (alsoRemove != null) Log.Children.Remove(alsoRemove);
            Chat.Provider = Router.Name; Chat.Model = "auto/best-chat"; LoadModels(); Capture();
            retry();
        };
        AddAnimated(b);
    }

    // Under a reply that read the web: the pages it fetched, as links, and what it searched for.
    void ShowSources(List<string> sources, List<string> searched)
    {
        var steps = runSteps ?? lastSteps;
        if (sources.Count == 0 || steps == null) return;   // the searches are already rows in the run
        var t = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Fade(t);
        {
            t.Inlines.Add(new System.Windows.Documents.Run("Sources  "));
            foreach (var (u, i) in sources.Distinct().Select((u, i) => (u, i)))
            {
                if (i > 0) t.Inlines.Add(new System.Windows.Documents.Run(" · "));
                var host = Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : u;
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(host)) { ToolTip = u };
                link.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
                t.Inlines.Add(link);
            }
        }
        steps.Children.Add(t);
    }

    // Streams one reply into a live bubble (plain text while it arrives); the caller adds the finished message.
    // Stop keeps what arrived so far. Other errors are thrown, with the live bubble already gone.
    double lastSeconds;   // how long the last reply took to arrive

    async Task<(Api.Reply Reply, bool Stopped)> StreamReply(ProviderCfg cfg, string key, List<ToolDef>? tools, string waiting)
    {
        var live = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Text = waiting, Opacity = 0.6 };
        live.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var bubble = Bubble(false, "", liveBody: live);
        var sb = new StringBuilder(); var th = new StringBuilder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // tokens arrive far faster than the screen redraws: one redraw per idle moment, not one per token
            // (redrawing the whole reply per token made long replies slow the window down)
            var queued = false;
            void Redraw()
            {
                if (queued) return; queued = true;
                Dispatcher.BeginInvoke(() =>
                {
                    queued = false;
                    if (sb.Length > 0) { live.Opacity = 1; live.Text = sb.ToString(); Scroller.ScrollToEnd(); }
                    // a command-line tool's steps ("▸ Read …") show while nothing has been written yet
                    else if (th.ToString().TrimEnd().Split('\n').LastOrDefault(l => l.StartsWith("▸ ")) is { } step) live.Text = waiting + "  " + step;
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            void Delta(string d) { sb.Append(d); Redraw(); }
            void Thought(string t) { th.Append(t); if (sb.Length == 0) Redraw(); }
            // Code mode: click the bubble to see the step's command and output in full in the editor area
            string? step = null;
            live.MouseLeftButtonUp += (_, _) => { if (step != null) main.ShowStep(step, open: true); };
            void Step(string s) { step = s; live.Cursor = Cursors.Hand; live.ToolTip = "Show the command and its output"; main.ShowStep(s, open: false); }
            Api.Reply r;
            for (var attempt = 1; ; attempt++)   // a limit or outage before anything arrived: wait and try again, up to three times
            {
                try
                {
                    r = cfg.Kind == "cli" ? await Cli.Run(cfg, Chat, Delta, Thought, cts!.Token, Step)
                                          : await Api.Stream(cfg, key, Chat, tools, Delta, Thought, cts!.Token);
                    break;
                }
                catch (Exception ex) when (attempt <= 3 && sb.Length == 0 && cfg.Kind != "cli" && ex is not OperationCanceledException && Router.Exhausted(ex.Message))
                {
                    var wait = attempt * attempt * 3;   // 3, 12, 27 s
                    live.Text = $"{waiting}  {cfg.Name} is busy or limited · retrying in {wait} s ({attempt} of 3)";
                    await Task.Delay(TimeSpan.FromSeconds(wait), cts!.Token);
                }
            }
            return (r, false);
        }
        catch (OperationCanceledException) { return (new(sb.Length == 0 ? "_(stopped)_" : sb.ToString(), [], th.ToString().Trim(), null, null), true); }
        finally { Log.Children.Remove(bubble); lastSeconds = clock.Elapsed.TotalSeconds; }
    }

    // Token counts with a cost estimate; models running on this PC (Ollama, LM Studio) cost nothing.
    // (A self-hosted gateway on this PC still forwards to paid providers, so it's priced normally.)
    Usage? Priced(Usage? u)
    {
        if (u == null) return null;
        var p = u with { Provider = Chat.Provider, Cost = Chat.Provider?.EndsWith("(local)") == true ? 0 : Pricing.Cost(Chat.Model ?? "", u) };
        if (p.Cost is > 0 and var c && Chat.Provider != null)   // the month's spend per provider, and the day's in total (Settings → Spending)
        {
            var k = $"{Chat.Provider}|{DateTime.Now:yyyy-MM}";
            main.S.Spend[k] = main.S.Spend.GetValueOrDefault(k) + c;
            var d = $"*|{DateTime.Now:yyyy-MM-dd}";
            main.S.Spend[d] = main.S.Spend.GetValueOrDefault(d) + c;
        }
        return p;
    }

    // Before a request: past the day's limit or the month's budget for this provider, ask first; past 80%, a quiet note once
    // a day. A model above the "expensive" price asks once per chat.
    async Task<bool> WithinBudget()
    {
        var S = main.S;
        if (S.DailyLimit > 0 && S.Spend.GetValueOrDefault($"*|{DateTime.Now:yyyy-MM-dd}") is var today && today >= S.DailyLimit
            && !await main.Confirm("Today's spending limit is reached", $"About ${today:0.00} of your ${S.DailyLimit:0.##} daily limit is spent. Send anyway?", "Send anyway"))
            return false;
        if (S.ExpensiveAt > 0 && expensiveOk != Chat.Id && Pricing.OutPerMillion(Chat.Model ?? "") is { } price && price >= S.ExpensiveAt)
        {
            if (!await main.Confirm("This is an expensive model", $"{Chat.Model} costs about ${price:0.##} per million tokens it writes. Use it for this chat?", "Use it")) return false;
            expensiveOk = Chat.Id;
        }
        var prov = Chat.Provider ?? "";
        var limit = main.S.Budgets.GetValueOrDefault(prov);
        if (limit <= 0) return true;
        var spent = main.S.Spend.GetValueOrDefault($"{prov}|{DateTime.Now:yyyy-MM}");
        if (spent >= limit) return await main.Confirm($"{prov} is over budget", $"About ${spent:0.00} of your ${limit:0.##} monthly budget is spent. Send anyway?", "Send anyway");
        if (spent >= limit * 0.8 && budgetNoted != DateTime.Today) { budgetNoted = DateTime.Today; Bubble(false, $"Heads up: {spent / limit:P0} of this month's {prov} budget (${limit:0.##}) is used.", error: false); }
        return true;
    }
    static DateTime budgetNoted;
    string? expensiveOk;   // the chat whose expensive model was already allowed

    // No internet (or the provider unreachable) and a model on this PC that works: carry on there, once, and say so.
    bool OfflineFallback(Exception ex, Action resend)
    {
        if (ex is OperationCanceledException || Cfg is not { } cfg || Local(cfg)) return false;
        var offline = ex is System.Net.Http.HttpRequestException || System.Text.RegularExpressions.Regex.IsMatch(ex.Message,
            @"no such host|name or service not known|network is unreachable|No connection could be made|actively refused|host is known|nodename nor servname|The SSL connection could not be established", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!offline || System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() && !System.Text.RegularExpressions.Regex.IsMatch(ex.Message, "host|connect", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        var local = main.S.Providers.Where(Local)
            .SelectMany(p => main.S.ModelStatus.Where(x => x.Value == "ok" && x.Key.StartsWith(p.Name + "|") && !x.Key.Contains("|#")).Select(x => (p, m: x.Key[(p.Name.Length + 1)..])))
            .FirstOrDefault();
        if (local.p == null) return false;
        Bubble(false, $"Couldn't reach {cfg.Name} (offline?), so this went to {local.p.Name} · {local.m} on your PC.", error: false);
        Chat.Provider = local.p.Name; Chat.Model = local.m; LoadModels(); Capture();
        resend();
        return true;
    }
    static bool Local(ProviderCfg p) => p.Kind == "llama" || System.Text.RegularExpressions.Regex.IsMatch(p.BaseUrl, @"//(localhost|127\.0\.0\.1|\[::1\])[:/]");

    // Chat total in the header (input is billed again on every step, so it adds up).
    void UpdateUsage()
    {
        var all = Chat.Messages.Select(m => m.Usage).OfType<Usage>().ToList();
        var fill = Context.Fill(Chat);
        UsageText.Visibility = all.Count > 0 && main.S.ShowCost ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count > 0) UsageText.Text = Pricing.Format(new(all.Sum(u => u.In), all.Sum(u => u.Out), all.All(u => u.Cost != null) ? all.Sum(u => u.Cost) : null));
        var runs = Subagents.RunsOf(Chat.Id);   // what this chat's agents used, on top
        if (runs.Count > 0 && main.S.ShowCost)
        {
            UsageText.Visibility = Visibility.Visible;
            UsageText.Text = (all.Count > 0 ? UsageText.Text + " · " : "") + $"agents {runs.Sum(r => r.Tokens):N0} tok · {TimeSpan.FromSeconds(runs.Sum(r => r.Took.TotalSeconds)).ToString(@"m\:ss")}";
        }
        DrawContextRing(fill);
    }

    // The ring fills with the chat's share of the model's memory: green, amber past half, orange past 80%.
    void DrawContextRing(double fill)
    {
        ContextRing.Visibility = Chat.Messages.Count > 0 && Cfg?.Kind != "cli" ? Visibility.Visible : Visibility.Collapsed;
        var f = Math.Clamp(fill, 0, 1);
        const double c = 7.5, rad = 6.4;
        var angle = (f * 360 - 90) * Math.PI / 180;
        var end = new Point(c + rad * Math.Cos(angle), c + rad * Math.Sin(angle));
        ContextArc.Data = f >= 0.999 ? new EllipseGeometry(new Point(c, c), rad, rad)
            : f < 0.01 ? null
            : new PathGeometry([new PathFigure(new Point(c, c - rad), [new ArcSegment(end, new Size(rad, rad), 0, f > 0.5, SweepDirection.Clockwise, true)], false)]);
        ContextArc.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(f < 0.5 ? "#3FB950" : f < 0.8 ? "#E3B341" : "#F0883E"));
        ContextRing.ToolTip = $"Context ~{f:P0}{(Chat.Summary != null ? " (summarised)" : "")}\nClick to compact";
    }

    // Compact now (the ring or /compact): summarise the older messages without waiting until it's nearly full.
    async void Compact_Click(object s, RoutedEventArgs e)
    {
        if (cts != null || Cfg is not { } cfg) return;
        if (cfg.Kind == "cli") { _ = main.Notice("Nothing to compact", $"{cfg.Name} manages its own memory."); return; }
        if (Chat.Summary != null)   // already summarised once: show what stands in for the older messages first
        {
            var sum = Chat.Summary.Length > 3000 ? Chat.Summary[..3000] + "…" : Chat.Summary;
            if (!await main.Confirm($"Summary standing in for the first {Chat.SummaryUpTo} messages", sum, "Compact again", cancel: "Close")) return;
        }
        cts = new CancellationTokenSource(); SetSending(true);
        try
        {
            if (!await Fit(cfg, Store.Unprotect(cfg.Key), force: true))
                _ = main.Notice("Nothing to compact yet", "Nothing older to summarise.");
        }
        catch (OperationCanceledException) { }
        cts = null; SetSending(false);
    }

    // Long chat: summarise the older messages (when nearly full, or forced after a "too long" error). Agents keep their own memory.
    async Task<bool> Fit(ProviderCfg cfg, string key, bool force = false)
    {
        if (cfg.Kind == "cli" || !(force || Context.Needed(Chat))) return false;
        var note = new TextBlock { Text = "Summarising…", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        AddAnimated(note);
        var ok = false;
        try { ok = await Context.Compact(cfg, key, Chat, cts!.Token); } catch (OperationCanceledException) { throw; } catch { }
        note.Text = ok ? "Earlier messages were summarised so the model can keep going (the chat itself keeps everything)." : "Couldn't summarise the earlier messages.";
        if (ok) Store.SaveChat(Chat);
        UpdateUsage();
        return ok;
    }

    // "› Thinking" header that shows/hides the model's reasoning, collapsed by default
    // an icon chevron (› / ⌄) sized and centred with the label, then "Thinking"
    (FrameworkElement Toggle, FrameworkElement Thoughts) ThinkingToggle(string text = "")
    {
        var thoughts = ThoughtSteps(text);
        // titled by the model's first "**Title**" (e.g. "Exploring favicon implementation"), Copilot-style
        var head = System.Text.RegularExpressions.Regex.Match(text, @"\*\*(.+?)\*\*") is { Success: true } hm ? hm.Groups[1].Value : "Thinking";
        var icon = new TextBlock { Text = "\uE99A", FontFamily = App.IconFont, FontSize = 14, Width = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };
        var label = new TextBlock { Text = head, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var chevron = new TextBlock { Text = "\uE76C", FontFamily = App.IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 0, 0) };
        var toggle = new Border { Padding = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "Reasoning",
                                  HorizontalAlignment = HorizontalAlignment.Left,
                                  Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label, chevron } } };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        Fade(toggle);
        if (!main.S.ShowThinking) toggle.Visibility = Visibility.Collapsed;
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        toggle.MouseLeftButtonUp += (_, _) =>
        {
            var open = thoughts.Visibility != Visibility.Visible;
            thoughts.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "\uE70D" : "\uE76C";
        };
        return (toggle, thoughts);
    }

    // The reasoning as a timeline: a dot per step, its title in bold, the text under it, a line down to the next step.
    // A step starts at a "**Title**" line (Gemini's style) or a "▸ tool" line; otherwise each paragraph is its own untitled step.
    FrameworkElement ThoughtSteps(string text)
    {
        var steps = new List<(string? Title, string Body)>();
        foreach (var para in System.Text.RegularExpressions.Regex.Split(text.Trim(), @"\n\s*\n"))
        {
            var p = para.Trim();
            if (p == "") continue;
            var m = System.Text.RegularExpressions.Regex.Match(p, @"^(?:\*\*(.+?)\*\*|▸ (.+?))\s*(?:\n|$)");
            if (m.Success) steps.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, p[m.Length..].Trim()));
            else if (steps.Count > 0 && steps[^1].Title != null && steps[^1].Body == "") steps[^1] = (steps[^1].Title, p);   // title, blank line, then its text
            else steps.Add((null, p));
        }
        var list = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 8), MaxWidth = ReplyWidth, HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < steps.Count; i++)
        {
            var (title, body) = steps[i];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 5, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Dim");
            row.Children.Add(dot);
            if (i < steps.Count - 1)   // the line down to the next step
            {
                var line = new System.Windows.Shapes.Rectangle { Width = 1, Margin = new Thickness(0, 17, 0, -4), HorizontalAlignment = HorizontalAlignment.Center };
                line.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "GlassStroke");
                row.Children.Add(line);
            }
            var text_ = new StackPanel { Margin = new Thickness(6, 0, 0, 14) };
            Grid.SetColumn(text_, 1);
            if (title != null)
            {
                var t = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                text_.Children.Add(t);
            }
            if (body != "")
            {
                var b = new TextBlock { Text = body, FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap };
                b.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                text_.Children.Add(b);
            }
            row.Children.Add(text_);
            list.Children.Add(row);
        }
        return list;
    }

    // A step that only called tools: its thinking, without a reply bubble.
    void StandaloneThinking(string thought, bool animate)
    {
        var (t, th) = ThinkingToggle(thought);
        t.Margin = new Thickness(0, 2, 0, 2);
        Steps(animate).Children.Add(new StackPanel { Children = { t, th } });
        stepCount++; UpdateRun();
    }

    // Plan mode ends with a way to act on the plan.
    void StartImplementationButton()
    {
        var go = new Button
        {
            Style = (Style)FindResource("Shimmer"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 2, 0, 8),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Glyph(""), new TextBlock { Text = "Start implementation", Margin = new Thickness(8, 0, 0, 0) } } },
        };
        go.Click += (_, _) =>
        {
            Log.Children.Remove(go);
            Chat.Mode = "interactive";
            loading = true;
            foreach (RadioButton b in ModePick.Children) b.IsChecked = (string)b.Tag == Chat.Mode;
            loading = false;
            Capture();
            Send("Go ahead and implement the plan.");
        };
        AddAnimated(go);
    }

    async Task<(string Text, UndoInfo? Undo, string? Detail)> RunTool(ToolCall call)
    {
        var tool = Agent.ToolsFor(Chat).FirstOrDefault(t => t.Name == call.Name);
        var snap = tool == null ? null : Agent.Snapshot(Chat, call.Name, call.Args);
        var (body, status, done) = ToolCard(call, null, animate: true, before: snap?.Before is { } b ? Agent.Text(b) : null);
        if (tool == null) { var e = $"Error: unknown tool {call.Name}."; done(e, null); return (e, null, null); }
        JsonNode args;
        try { args = JsonNode.Parse(call.Args) ?? new JsonObject(); } catch { args = new JsonObject(); }
        if (!alwaysAllow.Contains(call.Name) && Agent.NeedsApproval(Chat, tool, args))
        {
            status.Text = "· waiting for you";
            var choice = await Ask(body);
            if (choice == "deny") { done("The user denied this action.", null); return ("The user denied this action.", null, null); }
            if (choice == "always") alwaysAllow.Add(call.Name);
        }
        status.Text = "· running…";
        var result = await Agent.Run(Chat, call.Name, call.Args, cts!.Token);
        UndoInfo? undo = snap is { } sn && !result.StartsWith("Error") && File.Exists(sn.Path)
            ? new(sn.Path, sn.Before is { } before ? Convert.ToBase64String(before) : null, Agent.Hash(File.ReadAllBytes(sn.Path))) : null;
        done(result, undo);
        string? detail = null;
        if (call.Name == "delegate_to_agent")   // the agent's own steps: on the card, kept with the chat, but not sent back to the model
        {
            var task = (string?)args["task"] ?? "";
            detail = Subagents.RunsOf(Chat.Id).LastOrDefault(r => r.Task == task)?.Transcript is { Length: > 0 } t ? t : null;
            if (detail != null) AddTranscript(body, detail);
        }
        return (result, undo, detail);
    }

    // "› Agent's steps" under an agent card: folded, opens to every step and what it got back
    void AddTranscript(StackPanel card, string transcript)
    {
        var details = card.Children.Count > 1 && card.Children[1] is StackPanel d ? d : card;
        var steps = transcript.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        var text = new TextBlock { Text = transcript.TrimEnd(), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85, Visibility = Visibility.Collapsed, Margin = new Thickness(26, 4, 0, 4) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var chevron = new System.Windows.Documents.Run("\uE76C") { FontFamily = App.IconFont, FontSize = 12 };
        var toggle = new TextBlock { FontSize = 12, Cursor = Cursors.Hand, Margin = new Thickness(26, 6, 0, 0), ToolTip = "Every step the agent took and what it got back" };
        toggle.Inlines.Add(chevron); toggle.Inlines.Add(new System.Windows.Documents.Run($"  Agent's steps ({steps})"));
        toggle.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        toggle.MouseLeftButtonUp += (_, _) =>
        {
            var open = text.Visibility != Visibility.Visible;
            text.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "\uE70D" : "\uE76C";
        };
        details.Children.Add(toggle); details.Children.Add(text);
    }

    // Puts a file back as it was before an agent step, and tells the model through that step's result.
    async void UndoChange(string callId, UndoInfo u, TextBlock status, Button button)
    {
        if (Agent.ChangedSince(u) && !await main.Confirm("Undo this change?",
                $"{Path.GetFileName(u.Path)} changed since. Later edits will be lost.", "Undo anyway", danger: true))
            return;
        try
        {
            var what = Agent.Undo(u);
            status.Text = $"· undone ({what})";
            button.Visibility = Visibility.Collapsed;
            var i = Chat.Messages.FindIndex(m => m.Role == "tool" && m.CallId == callId);
            if (i >= 0) Chat.Messages[i] = Chat.Messages[i] with { Undo = null, Text = Chat.Messages[i].Text + "\n(The user then undid this change; the file is back as it was before.)" };
            Store.SaveChat(Chat);
        }
        catch (Exception ex) { await main.Notice("Couldn't undo", ex.Message); }
    }

    // An agent working for this chat wants to do something that needs approval: a card in the chat, one at a time.
    static readonly SemaphoreSlim approvals = new(1, 1);
    public async Task<bool> ApproveForAgent(string agent, ToolCall call)
    {
        if (cts == null) return false;   // nothing running here any more: nobody to ask
        await approvals.WaitAsync();
        try
        {
            var (body, status, _) = ToolCard(call, null, animate: true);
            status.Text = $"· the {agent} agent asks";
            string choice;
            try { choice = await Ask(body); } catch (OperationCanceledException) { status.Text = "· stopped"; return false; }
            status.Text = choice == "deny" ? "· you said no" : $"· allowed · the {agent} agent runs it";
            return choice != "deny";
        }
        finally { approvals.Release(); }
    }

    async Task<string> Ask(Panel into)
    {
        var tcs = new TaskCompletionSource<string>();
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, value) in new[] { ("Allow", "allow"), ("Always allow in this chat", "always"), ("Deny", "deny") })
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0), FontSize = 12, Cursor = Cursors.Hand };
            if (value == "allow") b.Style = (Style)FindResource("Shimmer");
            b.Click += (_, _) => tcs.TrySetResult(value);
            row.Children.Add(b);
        }
        into.Children.Add(row);
        Scroller.ScrollToEnd();
        using var reg = cts!.Token.Register(() => tcs.TrySetCanceled());
        try { return await tcs.Task; }
        finally { into.Children.Remove(row); }
    }

    // One tool call in the log: icon + what it does, the command/content, then its output (collapsed).
    (StackPanel Body, TextBlock Status, Action<string, UndoInfo?> Done) ToolCard(ToolCall call, string? result, bool animate, string? before = null, UndoInfo? undo = null)
    {
        var (icon, label, detail) = Agent.Describe(call);
        var status = new TextBlock { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var title = new TextBlock { Text = label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var head = new DockPanel();
        var show = SmallButton("", "Show output");
        show.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(show, Dock.Right);
        head.Children.Add(show);
        var chevron = new TextBlock { FontFamily = App.IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 0, 0) };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Glyph(icon), new Border { Width = 8 }, title, chevron, status } });
        var details = new StackPanel();   // everything but the header line
        var rail = new Border { Child = details, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(14, 2, 0, 4), Margin = new Thickness(7, 4, 0, 2), Visibility = Visibility.Collapsed };
        rail.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        void Fold()
        {
            var any = details.Children.Count > 0;
            chevron.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            head.Cursor = any ? Cursors.Hand : null;
            if (!any) rail.Visibility = Visibility.Collapsed;
            chevron.Text = rail.Visibility == Visibility.Visible ? "\uE70D" : "\uE76C";
        }
        var body = new StackPanel { Children = { head, rail } };
        head.Background = Brushes.Transparent;   // click the line to fold or unfold
        Fade(head);
        head.MouseLeftButtonUp += (_, _) => { if (details.Children.Count == 0) return; rail.Visibility = rail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; Fold(); };
        if (Agent.Preview(call, before) is { } diff) details.Children.Add(DiffBlock(diff));   // a change to an existing file: show what changes
        else if (call.Name == "delegate_to_agent" && !string.IsNullOrEmpty(detail) && main.S.ShowAgentPrompts)
        {
            var task = new TextBlock { Text = detail.Length > 4000 ? detail[..4000] + "…" : detail, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Opacity = 0.85, Margin = new Thickness(26, 6, 0, 4) };
            task.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            details.Children.Add(task);
        }
        else if (!string.IsNullOrEmpty(detail)) details.Children.Add(CodeBlock(detail.Length > 4000 ? detail[..4000] + "\n…" : detail, call.Name == "run_command" ? "powershell" : Code.Language(detail) ?? ""));
        if (main.S.ShowTools && details.Children.Count > 0) rail.Visibility = Visibility.Visible;   // a diff or command: open, as before
        Fold();
        var card = new Border { Child = body, Padding = new Thickness(0, 3, 0, 3) };
        (toolGroup ?? Steps(animate)).Children.Add(card);
        stepCount++; UpdateRun();

        void Done(string r, UndoInfo? u)
        {
            if (u != null)
            {
                var undoButton = SmallButton("\uE7A7", "Undo this change: put the file back as it was before this step");
                DockPanel.SetDock(undoButton, Dock.Right);
                undoButton.Click += (_, _) => UndoChange(call.Id, u, status, undoButton);
                head.Children.Insert(1, undoButton);
            }
            var failed = r.StartsWith("Error") || r.StartsWith("Blocked") || r.StartsWith("The agent failed");
            if (failed) Store.Fail($"Tool · {call.Name}", r);
            var why = failed ? System.Text.RegularExpressions.Regex.Replace(r.Split('\n')[0], @"^(Error|Blocked by the terminal sandbox|The agent failed):\s*", "") : "";
            status.Text = failed ? "· failed" + (why != "" ? " · " + (why.Length > 90 ? why[..90] + "…" : why) : "") : r.StartsWith("The user denied") || r.StartsWith("Cancelled") ? "· denied" : "· done";
            status.ToolTip = failed ? r.Length > 2000 ? r[..2000] : r : null;
            if (status.Text == "· done" && FileOf(call) is { } path) details.Children.Add(FileActions(path));
            if (r.Trim() != "") details.Children.Add(CodeBlock(r.Length > 6000 ? r[..6000] + "\n…" : r, "output"));   // what it got back, in the fold
            Fold();
        }
        if (result != null) Done(result, undo);
        return (body, status, Done);
    }

    StackPanel? toolGroup;   // the open step's group: tool rows go in here instead of straight into the log

    // One step's tool calls under a Copilot-style summary: "Ran 2 commands, read 2 files, searched for 1 phrase".
    StackPanel? ToolGroup(List<ToolCall> calls, bool animate)
    {
        if (calls.Count < 2) return null;   // a lone tool is its own row
        static string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        var parts = calls.GroupBy(c => c.Name switch
        {
            "run_command" => "ran", "read_file" or "list_dir" or "use_skill" => "read", "search" or "search_notes" => "searched",
            "web_search" or "fetch_url" => "web", "write_file" or "edit_file" => "edited", "delegate_to_agent" or "run_chain" => "agents", _ => "other",
        }).Select(g => (g.Key, g.Count()) switch
        {
            ("ran", var n) => "ran " + N(n, "command", "commands"),
            ("read", var n) => "read " + N(n, "file", "files"),
            ("searched", var n) => "searched for " + N(n, "phrase", "phrases"),
            ("web", var n) => "looked on the web " + N(n, "time", "times"),
            ("edited", var n) => "edited " + N(n, "file", "files"),
            ("agents", var n) => "asked " + N(n, "agent", "agents"),
            (_, var n) => N(n, "other step", "other steps"),
        });
        var text = string.Join(", ", parts);
        text = char.ToUpper(text[0]) + text[1..];
        var icon = new TextBlock { Text = "\uE90F", FontFamily = App.IconFont, FontSize = 14, Width = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };
        var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var chevron = new TextBlock { Text = "\uE76C", FontFamily = App.IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 0, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 3, 0, 3), Children = { icon, label, chevron } };
        var rows = new StackPanel();
        var rail = new Border { Child = rows, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(14, 0, 0, 0), Margin = new Thickness(7, 2, 0, 4), Visibility = Visibility.Collapsed };
        Fade(head);
        rail.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        head.MouseLeftButtonUp += (_, _) =>
        {
            rail.Visibility = rail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = rail.Visibility == Visibility.Visible ? "\uE70D" : "\uE76C";
        };
        Steps(animate).Children.Add(new StackPanel { Children = { head, rail } });
        return rows;
    }

    // Faded until hovered, then full white.
    static void Fade(FrameworkElement row)
    {
        row.Opacity = 0.55;
        row.MouseEnter += (_, _) => row.Opacity = 1;
        row.MouseLeave += (_, _) => row.Opacity = 0.55;
    }

    // The run's steps under one folding header: "Working… 3 steps" → "Completed 20 steps in 2m 40s".
    // A reply bubble mid-run closes it; the next step opens a fresh one below.
    StackPanel? runSteps; TextBlock? runLabel; int stepCount; double runSeconds; bool runLive;
    StackPanel Steps(bool animate)
    {
        if (runSteps != null) return runSteps;
        stepCount = 0; runSeconds = 0;
        var chevron = new TextBlock { Text = "\uE70D", FontFamily = App.IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 0, 0) };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        runLabel = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        runLabel.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 4), Children = { runLabel, chevron } };
        Fade(head);
        runSteps = new StackPanel();
        var rail = new Border { Child = runSteps, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(14, 0, 0, 0), Margin = new Thickness(7, 0, 0, 6) };
        rail.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        head.MouseLeftButtonUp += (_, _) =>
        {
            rail.Visibility = rail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = rail.Visibility == Visibility.Visible ? "\uE70D" : "\uE76C";
        };
        var run = new StackPanel { Margin = new Thickness(4, 2, 60, 2), Children = { head, rail } };
        FullWidth(run);
        if (animate) AddAnimated(run); else Log.Children.Add(run);
        return runSteps;
    }

    void UpdateRun()
    {
        if (runLabel == null) return;
        var n = $"{stepCount} step{(stepCount == 1 ? "" : "s")}";
        var t = runSeconds >= 60 ? $"{(int)runSeconds / 60}m {(int)runSeconds % 60}s" : $"{Math.Max(1, (int)runSeconds)}s";
        runLabel.Text = runLive ? $"Working… {n}" : runSeconds > 0 ? $"Completed {n} in {t}" : $"Completed {n}";
    }

    StackPanel? lastSteps;   // the run just closed: sources land there after the reply
    void CloseRun() { runLive = false; UpdateRun(); if (runSteps != null) lastSteps = runSteps; runSteps = null; runLabel = null; }

    // the file a write or edit made, if it's still there
    string? FileOf(ToolCall call)
    {
        if (call.Name is not ("write_file" or "edit_file")) return null;
        try { return System.Text.Json.Nodes.JsonNode.Parse(call.Args)?["path"]?.GetValue<string>() is { } p && Agent.Resolve(Chat, p) is var full && File.Exists(full) ? full : null; }
        catch { return null; }
    }

    // Open, copy (pastes as the file in Explorer, mail, chat apps) or save a copy elsewhere.
    static UIElement FileActions(string path)
    {
        var open = CopyButton("Open in its app", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }), glyph: "", text: "Open", done: "Opened");
        var copy = CopyButton("Copy the file", () => Clipboard.SetFileDropList([path]));
        var save = CopyButton("Save a copy as…", () =>
        {
            var dlg = new SaveFileDialog { FileName = Path.GetFileName(path), Filter = $"{Path.GetExtension(path)} file|*{Path.GetExtension(path)}|All files|*.*" };
            if (dlg.ShowDialog() != true) throw new OperationCanceledException();
            File.Copy(path, dlg.FileName, overwrite: true);
        }, glyph: "", text: "Save as", done: "Saved");
        copy.Margin = save.Margin = new Thickness(6, 0, 0, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { open, copy, save } };
    }

    static TextBlock Glyph(string g) => new() { Text = g, FontFamily = App.IconFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };

    void PickWorkspace_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder the agent works in" };
        if (Agent.HasWorkspace(Chat)) dlg.InitialDirectory = Chat.Workspace;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        UseWorkspace(dlg.FolderName);
    }
    void ClearWorkspace_Click(object s, RoutedEventArgs e) { Chat.Workspace = null; UpdateAgentUi(); Capture(); main.WorkspaceChanged(this); }

    public void UseWorkspace(string folder)
    {
        Chat.Workspace = folder; alwaysAllow.Clear();
        UpdateAgentUi(); Capture();
        main.WorkspaceChanged(this);
    }
    void Perm_Checked(object s, RoutedEventArgs e)
    {
        if (loading || s is not RadioButton { Tag: string v }) return;
        Chat.Permission = v; UpdateAgentUi(); Capture();
    }
    // Sandbox and administrator mode exclude each other: turning one on turns the other off.
    void Sandbox_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        Chat.Sandbox = SandboxBox.IsChecked == true;
        if (Chat.Sandbox) ElevatedBox.IsChecked = false;
        UpdateAgentUi(); Capture();
    }

    // Turning it on asks UAC right away, so the prompt comes while you're looking, not mid-task.
    async void Elevated_Changed(object s, RoutedEventArgs e)
    {
        if (loading) return;
        var on = ElevatedBox.IsChecked == true;
        if (on)
        {
            if (!await main.Confirm("Run commands as administrator?", "With Allow all, the model can change anything on this PC without asking.", "Turn on", danger: true))
            {
                loading = true; ElevatedBox.IsChecked = false; loading = false;
                return;
            }
            SandboxBox.IsChecked = false;
            bool ok;
            try { ok = await Elevation.Start(); }
            catch (Exception ex) { ok = false; Bubble(false, "Couldn't start administrator mode: " + ex.Message, error: true); }
            if (!ok) { loading = true; ElevatedBox.IsChecked = false; loading = false; on = false; }
        }
        Chat.Elevated = on;
        if (!on && !main.AnyElevated()) Elevation.Stop();
        UpdateAgentUi(); Capture();
    }

    internal void UpdateAgentUi()
    {
        var on = Agent.HasWorkspace(Chat);
        WorkspaceText.Inlines.Clear();
        if (on)
        {
            var path = Chat.Workspace!.TrimEnd('\\');
            WorkspaceText.Inlines.Add(new Run(Path.GetFileName(path)));
            WorkspaceText.Inlines.Add(new Run("   " + Path.GetDirectoryName(path)) { Foreground = (Brush)FindResource("Dim"), FontSize = 12 });
        }
        else WorkspaceText.Inlines.Add(new Run("No folder · plain chat") { Foreground = (Brush)FindResource("Dim") });
        WorkspaceField.ToolTip = on ? $"{Chat.Workspace}\nClick to change" : "Work in a folder";
        ClearWorkspace.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AgentOptions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var all = Chat.Permission == "all";
        ElevatedBox.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
        ElevatedHint.Visibility = Agent.RunsElevated(Chat) ? Visibility.Visible : Visibility.Collapsed;
        PermHint.Text = Chat.Permission switch
        {
            "assisted" => "Runs low-risk actions (new files, read-only commands) and asks for the rest.",
            "all" => "Runs every file change and command without asking.",
            _ => "Asks before every file change and command. Reading files never asks.",
        };
        ModeHint.Text = (on ? AgentHints : Modes.ToDictionary(x => x.Key, x => x.Value.Hint)).GetValueOrDefault(Chat.Mode ?? "") ?? "";
        UpdateSummary();
    }

    static readonly Dictionary<string, string> AgentHints = new()
    {
        ["interactive"] = "Works step by step in the folder and checks in with you.",
        ["plan"] = "Reads the folder and writes a plan; changes nothing until you start it.",
        ["autopilot"] = "Keeps working in the folder until the task is done (within permissions).",
    };
}
