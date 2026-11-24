using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Settings → Blub: her picture, what's writing her replies, Meet Blub again and the tour, then a card
// per part, each with its icon, what it's for and, where there's a how-to, a round ⓘ: Personality, Smarter chat,
// Memory, Packs (behaviour and datasets), Your day, Connections. On/off is a switch.
public partial class MainWindow
{
    void DrawAssistantCard()
    {
        AssistantCard.Children.Clear();
        AssistantCard.Resources[typeof(CheckBox)] = FindResource("ToggleSwitch");   // every on/off here is a switch
        AssistantCard.Children.Add(IvoHero());
        AssistantCard.Children.Add(SectionCard("🎭", "#F472B6", "Personality", "How she talks: pick a vibe, or describe her in your own words", null, PersonalityPicker()));
        AssistantCard.Children.Add(SectionCard("🧠", "#A78BFA", "Smarter chat", "A language model on this PC writes her replies; her brain still does the actions and knows the app",
            ("How to train your own model", ModelGuide), SmarterChatRow()));
        AssistantCard.Children.Add(SectionCard("💭", "#22D3EE", "Memory", "Words she looked up, what you meant, and what you taught her", null, IvoLearntList()));
        AssistantCard.Children.Add(SectionCard("🧩", "#FB923C", "Packs", "Add to how she talks, what she knows and how she looks", null, PackTiles()));

        // your day: the rows ExtrasRows adds, gathered into one card with a line between each
        var from = AssistantCard.Children.Count;
        ExtrasRows(SettingRow);
        var rows = AssistantCard.Children.Cast<UIElement>().Skip(from).ToList();
        foreach (var r in rows) AssistantCard.Children.Remove(r);
        AssistantCard.Children.Add(SectionCard("☀️", "#FBBF24", "Your day", "Weather, quiet hours, activity, mood and your journal", null, Lined(rows)));

        // connections: Reddit in her chat (a free "installed app" id: no secret, no account)
        var reddit = new TextBox { Text = S.RedditApp, Width = 260, ToolTip = "From reddit.com/prefs/apps: create an app, type \"installed app\", any name, redirect http://localhost; the id is under its name" };
        reddit.LostFocus += (_, _) => { S.RedditApp = reddit.Text.Trim(); SaveSettings(); };
        var get = new Button { Content = "Get one ↗", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        get.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.reddit.com/prefs/apps") { UseShellExecute = true });
        AssistantCard.Children.Add(SectionCard("🔗", "#38BDF8", "Connections", "Services she can read for you", null,
            SettingRow("Reddit", new StackPanel { Children = { Faint("An app id lets her read Reddit in her chat (free, no account needed)."), new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { Hinted(reddit, "paste the app id"), get } } } })));
    }

    // Blub: her picture, name, what's writing her replies; meet her again, or take the tour
    FrameworkElement IvoHero()
    {
        var face = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), BorderThickness = new Thickness(2), BorderBrush = Shell.B("#80A78BFA"), Margin = new Thickness(0, 0, 18, 0) };
        try { face.Background = new ImageBrush(new System.Windows.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "blub.png")))) { Stretch = Stretch.UniformToFill }; } catch { }
        var brain = !S.SmarterChat || !IvoModel.Ready ? "her own brain (Smarter chat is off)"
                  : IvoModel.File == IvoModel.Default ? "the standard model" : "your own model";
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Strong("Blub", 20), Faint($"Your assistant · replies by {brain}") } };
        var meet = new Button { Content = "Meet Blub again", Style = (Style)FindResource("Shimmer"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Blub introduces herself, like on first launch" };
        meet.Click += (_, _) => IvoIntro_Click(meet, new RoutedEventArgs());
        var tour = new Button { Content = "Take the tour", Padding = new Thickness(14, 7, 14, 7) };
        tour.Click += (_, _) => Tour_Click(tour, new RoutedEventArgs());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { meet, tour } };
        var top = new DockPanel();
        DockPanel.SetDock(face, Dock.Left); DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(face); top.Children.Add(buttons); top.Children.Add(words);
        var card = new Border { Style = (Style)FindResource("Group"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 0), Child = top };
        card.Background = new LinearGradientBrush(Color.FromArgb(0x33, 0x8B, 0x7C, 0xFF), Color.FromArgb(0x10, 0x22, 0xD3, 0xEE), 0);
        return card;
    }

    // a card: an icon tile in its colour, the title, what it's for, a round ⓘ when there's a how-to, then its content
    FrameworkElement SectionCard(string icon, string color, string title, string about, (string Tip, Action Open)? help, FrameworkElement content)
    {
        var tint = (Color)ColorConverter.ConvertFromString(color);
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                                Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B)),
                                Child = new TextBlock { Text = icon, FontSize = 19, FontFamily = ColorEmoji.EmojiFontFamily, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var head = new DockPanel();
        DockPanel.SetDock(tile, Dock.Left); head.Children.Add(tile);
        if (help is { } h) { var i = InfoButton(h.Tip, h.Open); DockPanel.SetDock(i, Dock.Right); head.Children.Add(i); }
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Strong(title, 16), Faint(about) } });
        content.Margin = new Thickness(54, 16, 0, 0);
        return new Border { Style = (Style)FindResource("Group"), Padding = new Thickness(22, 18, 22, 20), Child = new StackPanel { Children = { head, content } } };
    }

    // the round ⓘ: a how-to, a tap away
    Button InfoButton(string tip, Action open)
    {
        var face = new FrameworkElementFactory(typeof(Border), "Bd");
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        face.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        face.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        face.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = face };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x30, 0x8B, 0x7C, 0xFF)), "Bd"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Shell.B("#8B7CFF"), "Bd"));
        t.Triggers.Add(hover);
        var b = new Button { Width = 28, Height = 28, Template = t, Cursor = Cursors.Hand, ToolTip = tip, VerticalAlignment = VerticalAlignment.Top, Focusable = false,
                             Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 } };
        ((TextBlock)b.Content).SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => open();
        return b;
    }

    // packs as tiles: each kind its own, with its icon, how many are on, its ⓘ, its list and Add
    FrameworkElement PackTiles()
    {
        var grid = new UniformGrid { Columns = 2 };
        FrameworkElement Tile(string icon, string title, int on, int all, (string, Action)? help, FrameworkElement list)
        {
            var count = Faint(all == 0 ? "none yet" : $"{on} of {all} on");
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            if (help is { } h) { var i = InfoButton(h.Item1, h.Item2); DockPanel.SetDock(i, Dock.Right); head.Children.Add(i); }
            var glyph = new TextBlock { Text = icon, FontSize = 18, FontFamily = ColorEmoji.EmojiFontFamily, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(glyph, Dock.Left); head.Children.Add(glyph);
            head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Strong(title, 14), count } });
            var tile = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 12, 12), BorderThickness = new Thickness(1),
                                    Child = new StackPanel { Children = { head, list } } };
            tile.SetResourceReference(Border.BackgroundProperty, "InputBg"); tile.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            return tile;
        }
        grid.Children.Add(Tile("🎨", "Behaviour", S.IvoBehaviourPacks.Count(p => p.On), S.IvoBehaviourPacks.Count, ("How to make a behaviour pack", BehaviourGuide), IvoBehaviourList()));
        grid.Children.Add(Tile("📚", "Datasets", S.IvoDatasets.Count(d => d.On), S.IvoDatasets.Count, ("How to make a dataset", DatasetGuide), IvoDatasetsList()));
        return grid;
    }

    // a setting inside a card: its name, then its controls under it
    FrameworkElement SettingRow(string title, UIElement content)
    {
        if (content is FrameworkElement f) f.Margin = new Thickness(0, 6, 0, 0);
        return new StackPanel { Children = { Strong(title, 13.5), content } };
    }

    // settings one under another, a hairline between each
    static FrameworkElement Lined(IEnumerable<UIElement> rows)
    {
        var all = new StackPanel();
        foreach (var r in rows)
        {
            if (all.Children.Count > 0) { var line = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 16) }; line.SetResourceReference(Border.BackgroundProperty, "Separator"); all.Children.Add(line); }
            all.Children.Add(r);
        }
        return all;
    }

    static TextBlock Strong(string t, double size) { var x = new TextBlock { Text = t, FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }; x.SetResourceReference(TextBlock.ForegroundProperty, "Fg"); return x; }
    static TextBlock Faint(string t) { var x = new TextBlock { Text = t, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }; x.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); return x; }
}
