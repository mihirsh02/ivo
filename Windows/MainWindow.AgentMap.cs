using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Ivo;

// The agent map: a chat on the left and every agent it handed work to on the right, joined by lines, each with its state,
// time and tokens (live while they work). Click an agent for what it was asked and what it said.
public partial class MainWindow
{
    Grid? mapLayer;
    Border? mapCard;
    ChatPane? mapPane;
    AgentRun? mapOpen;   // the agent whose details are showing, or null for the map
    DispatcherTimer? mapClock;

    public void ShowAgentMap(ChatPane pane)
    {
        mapPane = pane; mapOpen = null;
        if (mapLayer == null)
        {
            var backdrop = new Border();
            backdrop.SetResourceReference(Border.BackgroundProperty, "Backdrop");
            backdrop.MouseLeftButtonDown += (_, _) => CloseAgentMap();
            mapCard = new Border { CornerRadius = new CornerRadius(App.Corner), BorderThickness = new Thickness(App.Line), Padding = new Thickness(22, 16, 22, 20),
                                   MaxWidth = 760, MaxHeight = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40) };
            mapCard.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            mapCard.SetResourceReference(Border.BorderBrushProperty, "EdgeStroke");
            mapLayer = new Grid { Margin = new Thickness(0, 44, 0, 0), Children = { backdrop, mapCard } };
            Panel.SetZIndex(mapLayer, 25);
            Root.Children.Add(mapLayer);
            Subagents.Changed += () => Dispatcher.BeginInvoke(() => { if (mapLayer.IsVisible) DrawAgentMap(); });
            mapClock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { if (mapLayer.IsVisible) DrawAgentMap(); }, Dispatcher);   // running times tick
        }
        mapLayer.Visibility = Visibility.Visible;
        DrawAgentMap();
        mapClock!.Start();
    }

    void CloseAgentMap()
    {
        if (mapLayer != null) mapLayer.Visibility = Visibility.Collapsed;
        mapClock?.Stop();
    }

    static string Took(AgentRun r) { var t = r.Took; return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0}s"; }
    static string Tokens(int n) => n >= 1000 ? $"{n / 1000.0:0.0}k tokens" : $"{n} tokens";

    TextBlock MapText(string text, double size, string brush, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    // a node: a dot (green while working, grey when done, red if it failed), a title and a line under it
    Border Node(string title, string sub, Brush dot, bool bold, Action? click)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Ellipse { Width = 7, Height = 7, Fill = dot, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center },
                                                                                       MapText(title, 13.5, "Fg", bold ? FontWeights.SemiBold : FontWeights.Normal) } };
        var line = MapText(sub, 11.5, "Dim"); line.Margin = new Thickness(16, 3, 0, 0);
        var node = new Border { CornerRadius = new CornerRadius(App.Corner), BorderThickness = new Thickness(App.Line), Padding = new Thickness(12, 9, 14, 10), Width = 270,
                                Child = new StackPanel { Children = { head, line } }, Background = Brushes.Transparent };
        node.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        if (click != null)
        {
            node.Cursor = Cursors.Hand;
            node.MouseEnter += (_, _) => node.SetResourceReference(Border.BackgroundProperty, "Glass");
            node.MouseLeave += (_, _) => node.Background = Brushes.Transparent;
            node.MouseLeftButtonUp += (_, _) => click();
        }
        return node;
    }

    static Brush Dot(AgentRun r) => new SolidColorBrush(r.Ended == null ? Color.FromRgb(0x3F, 0xB9, 0x50) : r.Failed ? Color.FromRgb(0xF8, 0x51, 0x49) : Color.FromRgb(0x6E, 0x6E, 0x78));

    void DrawAgentMap()
    {
        if (mapPane == null || mapCard == null) return;
        var runs = Subagents.RunsOf(mapPane.Chat.Id);
        var close = new Button { Style = (Style)FindResource("Ghost"), Content = "", FontSize = 13, ToolTip = "Close", VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => CloseAgentMap();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(close, Dock.Right); top.Children.Add(close);
        var body = new StackPanel();

        if (mapOpen is { } open)   // one agent's details
        {
            var back = new Button { Style = (Style)FindResource("Ghost"), Content = "", FontSize = 14, ToolTip = "Back to the map", Margin = new Thickness(-6, 0, 6, 0) };
            back.Click += (_, _) => { mapOpen = null; DrawAgentMap(); };
            DockPanel.SetDock(back, Dock.Left); top.Children.Add(back);
            top.Children.Add(MapText(open.Agent, 18, "Fg", FontWeights.SemiBold));
            body.Children.Add(MapText($"{open.Model} · {Took(open)} · {Tokens(open.Tokens)} · {open.Steps} step{(open.Steps == 1 ? "" : "s")} · {open.Doing}", 12, "Dim"));
            body.Children.Add(MapText("TASK", 11, "Dim", FontWeights.SemiBold)); ((TextBlock)body.Children[^1]).Margin = new Thickness(0, 16, 0, 4);
            var task = MapText(open.Task, 13, "Fg"); task.TextWrapping = TextWrapping.Wrap; task.TextTrimming = TextTrimming.None;
            body.Children.Add(task);
            body.Children.Add(MapText(open.Ended == null ? "WORKING…" : "RESULT", 11, "Dim", FontWeights.SemiBold)); ((TextBlock)body.Children[^1]).Margin = new Thickness(0, 16, 0, 4);
            if (open.Result is { } result) foreach (var el in mapPane.RenderMarkdown(result, false)) body.Children.Add(el);
            mapCard.Child = new DockPanel { Children = { top, new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } };
            DockPanel.SetDock(top, Dock.Top);
            return;
        }

        top.Children.Add(MapText("Agent map", 18, "Fg", FontWeights.SemiBold));
        var busy = runs.Count(r => r.Ended == null);
        var sub = MapText($"{runs.Count} agent{(runs.Count == 1 ? "" : "s")}{(busy > 0 ? $" · {busy} working" : "")}", 12, "Dim"); sub.Margin = new Thickness(0, 0, 0, 16);

        // left: the chat; right: its agents, newest at the bottom; a bracket of lines between them
        var chat = mapPane.Chat;
        var used = chat.Messages.Select(m => m.Usage).OfType<Usage>().ToList();
        var main = Node(chat.Title, $"{chat.Model} · {Tokens(used.Sum(u => u.In + u.Out))}", Brushes.Gray, true, null);
        main.VerticalAlignment = VerticalAlignment.Center;
        var right = new StackPanel();
        foreach (var r in runs)
        {
            var run = r;
            var n = Node(r.Agent, $"{Took(r)} · {Tokens(r.Tokens)}{(r.Ended == null ? " · " + r.Doing : "")}", Dot(r), false, () => { mapOpen = run; DrawAgentMap(); });
            n.ToolTip = r.Task;
            var stub = new Border { Width = 22, Height = App.Line, VerticalAlignment = VerticalAlignment.Center };
            stub.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
            right.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5), Children = { stub, n } });
        }
        // the bracket: a vertical line from the first agent's middle to the last's, and one across to the chat
        var spine = new Border { Width = App.Line, Margin = new Thickness(0, 30, 0, 30) };
        spine.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        var across = new Border { Width = 26, Height = App.Line, VerticalAlignment = VerticalAlignment.Center };
        across.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        var map = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };
        map.Children.Add(main);
        Grid.SetColumn(across, 1); map.Children.Add(across);
        if (runs.Count > 1) { Grid.SetColumn(spine, 2); map.Children.Add(spine); }
        Grid.SetColumn(right, 3); map.Children.Add(right);
        if (runs.Count == 0) body.Children.Add(MapText("No agents yet.", 13, "Dim")); else body.Children.Add(map);

        DockPanel.SetDock(top, Dock.Top);
        var head = new StackPanel { Children = { top, sub } };
        DockPanel.SetDock(head, Dock.Top);
        mapCard.Child = new DockPanel { Children = { head, new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } } };
    }
}
