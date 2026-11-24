using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Every settings page in Assistant's look: each card leads with an icon tile in its colour, its title and what it's for
// (the help line under its title, or the words here), its settings indented under that, and every on/off a switch.
public partial class MainWindow
{
    static readonly Dictionary<string, (string Icon, string Color, string About)> CardLooks = new()
    {
        ["Startup and background"] = ("🚀", "#34D399", "How Ivo starts, and whether it stays in the tray"),
        ["Web pages"] = ("🌐", "#38BDF8", "Where links open"),
        ["Clock"] = ("🕒", "#FBBF24", "How times are written"),
        ["Model for new chats"] = ("🤖", "#A78BFA", "What a new chat starts with"),
        ["Sending"] = ("📨", "#60A5FA", "The key that sends, and spelling"),
        ["Chat header"] = ("🧾", "#94A3B8", "What shows above a chat"),
        ["Chat layout"] = ("🧱", "#F472B6", "How messages are laid out"),
        ["In the chat"] = ("💬", "#22D3EE", "What the chat shows as it goes"),
        ["Chats"] = ("💬", "#22D3EE", "Your conversations"),
        ["Theme"] = ("🎨", "#F472B6", "Ivo's colours, light or dark"),
        ["Text size"] = ("🔠", "#A78BFA", "Everything bigger or smaller"),
        ["Motion"] = ("✨", "#FBBF24", "Animations and the moving background"),
        ["Title bar"] = ("🪟", "#94A3B8", "What sits along the top"),
        ["Desktop"] = ("🖥️", "#60A5FA", "Ivo on your desktop"),
        ["Modes"] = ("🧭", "#34D399", "Ways to use Ivo"),
        ["Calendar"] = ("📅", "#F87171", "The calendars Ivo reads"),
        ["Holidays and festivals"] = ("🎉", "#FB923C", "Days off and celebrations to show"),
        ["Limits"] = ("🚦", "#F87171", "Stop before you spend too much"),
        ["Budgets"] = ("💰", "#34D399", "What you mean to spend"),
        ["This month, by model"] = ("📊", "#60A5FA", "Where your tokens went"),
        ["Notify me about"] = ("🔔", "#FBBF24", "What's worth a pop-up"),
        ["Sounds"] = ("🔊", "#A78BFA", "Ivo's chimes"),
        ["Read aloud"] = ("🗣️", "#22D3EE", "Replies spoken"),
        ["Current voice"] = ("🎙️", "#F472B6", "Who's speaking"),
        ["Windows voices"] = ("🪟", "#60A5FA", "The voices Windows has"),
        ["Natural voices"] = ("🌿", "#34D399", "Lifelike voices"),
        ["Natural voice packages"] = ("📦", "#FB923C", "Download more lifelike voices"),
        ["Voice typing (the mic button)"] = ("🎤", "#F87171", "Talk instead of typing"),
        ["Morning briefing"] = ("☀️", "#FBBF24", "Your day, ready when you are"),
        ["Daily tasks"] = ("📝", "#34D399", "Models for everyday jobs"),
        ["Dev tasks"] = ("🛠️", "#60A5FA", "Models for code work"),
        ["Storage"] = ("💾", "#60A5FA", "Where Ivo keeps things"),
        ["Knowledge folder"] = ("📚", "#A78BFA", "Files Ivo can search"),
        ["Clipboard history"] = ("📋", "#94A3B8", "What you copied, kept for later"),
        ["Developer"] = ("🧪", "#FB923C", "Extra tools for troubleshooting"),
        ["Activity log"] = ("🗒️", "#94A3B8", "What happened, and when"),
        ["App lock"] = ("🔒", "#F87171", "A PIN or password first"),
        ["What leaves this PC"] = ("📤", "#F87171", "Only what you send to a provider"),
        ["How Ivo handles your data"] = ("🛡️", "#34D399", "Kept here, encrypted"),
        ["Sharing with models"] = ("🤝", "#A78BFA", "What models get to see"),
        ["Device access"] = ("🎛️", "#60A5FA", "Camera, mic and more"),
        ["Web search"] = ("🔎", "#38BDF8", "Searching the web for answers"),
        ["Memory"] = ("💭", "#22D3EE", "What models remember about you"),
        ["Add an API key"] = ("🔑", "#FBBF24", "Connect a provider"),
        ["Auto"] = ("🪄", "#A78BFA", "Ivo picks the model for you"),
        ["Providers Auto uses"] = ("🧩", "#60A5FA", "Who Auto can choose from"),
        ["Key pools"] = ("🔁", "#34D399", "Several keys taking turns"),
        ["Gateway"] = ("🚪", "#FB923C", "Ivo as a gateway for other apps"),
        ["Find on this PC"] = ("🔍", "#38BDF8", "AI tools already here"),
        ["Models on this PC (Ollama)"] = ("🦙", "#94A3B8", "Models running locally"),
        ["Check models"] = ("✅", "#34D399", "Which models answer"),
        ["Model checks"] = ("🩺", "#34D399", "Results of the last check"),
        ["New agent"] = ("🕵️", "#A78BFA", "A specialist your chat hands work to"),
        ["New prompt"] = ("✍️", "#F472B6", "A prompt you insert with /"),
        ["Servers"] = ("🖧", "#60A5FA", "MCP servers and their tools"),
        ["Plugins"] = ("🔌", "#FB923C", "Extra tools for chats"),
        ["Add a connection"] = ("🔗", "#38BDF8", "A service Ivo can work with"),
        // panels' sections (Heading in MainWindow.Panels)
        ["This week"] = ("📈", "#22D3EE", "Minutes focused each day"),
        ["Habits"] = ("🌱", "#34D399", "Small things, done every day"),
        ["Add a webhook"] = ("🪝", "#FB923C", "Tell a chat app when something happens in Ivo"),
    };

    // every Group card under `root` with a GroupTitle first: dressed once (later calls skip it)
    void DressCards(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToList())
        {
            if (child is Border { Child: StackPanel body } card && card.Style == FindResource("Group") && card.Tag as string != "dressed"
                && body.Children.Count > 0 && body.Children[0] is TextBlock { Text: { Length: > 0 } title } head && head.Style == FindResource("GroupTitle"))
                Dress(card, body, title, head);
            DressCards(child);
        }
    }

    void Dress(Border card, StackPanel body, string title, TextBlock head)
    {
        card.Tag = "dressed";
        var (icon, color, about) = CardLooks.TryGetValue(title, out var look) ? look : ("⚙️", "#94A3B8", "");
        body.Children.Remove(head);
        // a help line right under the title says what the card is for better than a stock line: it moves up beside the
        // title (the same element, so code that changes it still does)
        TextBlock? help = null;
        if (body.Children.Count > 0 && body.Children[0] is TextBlock first && first.Style == FindResource("Help"))
        { help = first; about = ""; body.Children.Remove(help); help.Margin = new Thickness(0, 2, 0, 0); }
        var rest = body.Children.Cast<UIElement>().ToList();
        body.Children.Clear();

        var tint = (Color)ColorConverter.ConvertFromString(color);
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                                Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B)),
                                Child = new TextBlock { Text = icon, FontSize = 19, FontFamily = ColorEmoji.EmojiFontFamily, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Strong(title, 16) } };
        if (help != null) words.Children.Add(help);
        else if (about != "") words.Children.Add(Faint(about));
        var top = new DockPanel();
        DockPanel.SetDock(tile, Dock.Left); top.Children.Add(tile); top.Children.Add(words);
        var content = new StackPanel { Margin = new Thickness(54, 16, 0, 0) };
        foreach (var r in rest) content.Children.Add(r);
        body.Children.Add(top);
        if (rest.Count > 0) body.Children.Add(content);
        card.Padding = new Thickness(22, 18, 22, 20);
    }
}
