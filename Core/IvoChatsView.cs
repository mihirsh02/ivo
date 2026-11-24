using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Ivo Chats' screen, the same in Ivo and Ivo Portal: the list (contacts and groups, newest first), then a thread
// (messages, files; Ask Ivo where there's an Ivo), and for a group its members (the admin adds and removes,
// approves invites; members invite their own contacts).
public record ChatLook(Brush Fg, Brush Dim, Brush Card, Brush Field, Brush Accent, Brush Mine);

public class IvoChatsView : Grid
{
    readonly IvoChats chats;
    readonly ChatLook look;
    readonly Func<ChatThread, string, Task<string>>? askIvo;   // Ivo only: an answer from your own model
    readonly Action<ChatThread, string>? call;                 // a one-to-one thread: voice / video
    public Action<string>? Rename;                             // Ivo Portal: your name is set here (Ivo's comes from its profile)
    ChatThread? open;
    string screen = "list";
    string query = "";   // the list's search   // list · thread · members · newgroup
    StackPanel? messages;
    ScrollViewer? scroll;
    readonly FontFamily icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public IvoChatsView(IvoChats chats, ChatLook look, Func<ChatThread, string, Task<string>>? askIvo, Action<ChatThread, string>? call)
    {
        this.chats = chats; this.look = look; this.askIvo = askIvo; this.call = call;
        chats.Changed += Arrived;
        // listening only while on screen; a page redrawn around it takes it off and puts it back, so it listens again then
        Loaded += (_, _) => { chats.Changed -= Arrived; chats.Changed += Arrived; if (screen == "thread" && open != null && messages != null) FillMessages(open); };
        Unloaded += (_, _) => chats.Changed -= Arrived;
        Draw();
    }

    public void OpenThread(ChatThread t) { if (t.Deleted) { t.Deleted = false; chats.Save(); } open = t; screen = "thread"; chats.Seen(t); Draw(); }
    public Action<ChatThread>? Listen { get; init; }   // invite this chat to listen along (Ivo sets it when Spotify is connected)
    public Action<ChatThread>? JoinListen { get; init; }   // join someone's listen-along invite (Ivo with Spotify)
    public ChatThread? Showing => screen == "thread" && IsVisible ? open : null;   // the conversation on screen now (no notification for it)
    public void ShowMe() { screen = "me"; Draw(); }   // your picture and status (Ivo Portal's corner opens this)
    public static (string Id, string Name, string Color)[] StatusList => Statuses;

    void Arrived(ChatThread t, ChatMsg? m) => Dispatcher.BeginInvoke(() =>
    {
        if (screen == "thread" && open == t && m != null) chats.Seen(t);
        if (screen == "thread" && open == t && messages != null)   // a new one, or ours changed (sent at last): the bubbles redrawn in place
        {
            FillMessages(t);
            scroll?.ScrollToEnd();
            return;
        }
        if (screen != "newgroup") Draw();
    });

    // a right-click menu in Ivo's own look, at the mouse; each item is a glyph and a label
    (System.Windows.Controls.Primitives.Popup Menu, StackPanel Items) PopMenu(FrameworkElement at)
    {
        var menu = new System.Windows.Controls.Primitives.Popup { PlacementTarget = at, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade };
        var items = new StackPanel();
        var box = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), MinWidth = 190, Child = items, BorderThickness = new Thickness(1), Margin = new Thickness(6),
                               Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .45, Color = Colors.Black } };
        box.Background = TryFindResource("PopupBg") as Brush ?? look.Field;
        box.BorderBrush = TryFindResource("GlassStroke") as Brush ?? look.Card;
        menu.Child = box;
        return (menu, items);
    }
    Border MenuItem(string glyph, string text, Brush fg, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 13, Foreground = fg, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 14, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                             Child = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                                 new TextBlock { Text = glyph, FontFamily = icons, FontSize = 13, Foreground = fg, Width = 24, VerticalAlignment = VerticalAlignment.Center }, label } } };
        b.MouseEnter += (_, _) => b.Background = look.Card; b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    // right-click a chat: mark it read or unread, or delete it (tap Delete twice)
    void ChatMenu(ChatThread t, FrameworkElement at)
    {
        var (menu, items) = PopMenu(at);
        Border Item(string glyph, string text, Brush fg, Action click) => MenuItem(glyph, text, fg, click);
        var unread = t.Unread > 0;
        items.Children.Add(Item(unread ? "\uE8C3" : "\uE715", unread ? "Mark as read" : "Mark as unread", look.Fg, () => { menu.IsOpen = false; chats.Read(t, unread); Draw(); }));
        Border? del = null;
        var asked = false;
        var red = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E));
        del = Item("\uE74D", t.Kind == "group" ? "Leave and delete" : "Delete chat", red, () =>
        {
            if (!asked) { asked = true; ((TextBlock)((StackPanel)del!.Child).Children[1]).Text = "Tap again to delete"; return; }
            menu.IsOpen = false; chats.DeleteChat(t); Draw();
        });
        items.Children.Add(del);
        if (IvoChats.PersonOf(t) != null)
        {
            var blocked = chats.IsBlocked(t);
            items.Children.Add(Item(blocked ? "\uE785" : "\uE8F8", blocked ? $"Unblock {t.Name}" : $"Block {t.Name}", red, () => { menu.IsOpen = false; chats.Block(t, !blocked); Draw(); }));
        }
        menu.IsOpen = true;
    }

    // ---------- pieces ----------
    TextBlock T(string text, double size = 14, Brush? brush = null, bool bold = false) =>
        new() { Text = text, FontSize = size, Foreground = brush ?? look.Fg, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    // a round icon button: tinted glyph on a soft tint of the same colour, brighter on hover (contacts and chat rows)
    public static Button Round(string glyph, string tip, string hex, Action click, FontFamily font)
    {
        var tint = (Color)ColorConverter.ConvertFromString(hex);
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        Brush soft = new SolidColorBrush(Color.FromArgb(0x26, tint.R, tint.G, tint.B)), strong = new SolidColorBrush(Color.FromArgb(0x48, tint.R, tint.G, tint.B));
        var b = new Button { Content = glyph, FontFamily = font, FontSize = 15, Width = 34, Height = 34, Margin = new Thickness(6, 0, 0, 0), Foreground = new SolidColorBrush(tint), Background = soft,
                             Cursor = Cursors.Hand, ToolTip = tip, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        b.MouseEnter += (_, _) => b.Background = strong; b.MouseLeave += (_, _) => b.Background = soft;
        b.Click += (_, _) => click();
        return b;
    }

    Button Btn(string text, Brush? fill, Action click, string? tip = null, bool glyph = false)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        face.SetValue(Border.BackgroundProperty, fill ?? Brushes.Transparent);
        face.SetValue(Border.PaddingProperty, glyph ? new Thickness(9) : new Thickness(14, 8, 14, 9));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        face.AppendChild(cp);
        var b = new Button { Content = text, Foreground = fill == null ? look.Dim : Brushes.White, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, ToolTip = tip, Focusable = false,
                             VerticalAlignment = VerticalAlignment.Center, Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        if (glyph) { b.FontFamily = icons; b.FontSize = 15; b.FontWeight = FontWeights.Normal; }
        b.MouseEnter += (_, _) => b.Opacity = 0.8; b.MouseLeave += (_, _) => b.Opacity = 1;
        b.Click += (_, _) => click();
        return b;
    }

    static readonly (string Id, string Name, string Color)[] Statuses = [("active", "Active", "#3FB950"), ("busy", "Busy", "#F43F5E"), ("away", "Away", "#F59E0B"), ("invisible", "Invisible", "#6B7280")];

    public FrameworkElement Face(string name, bool group, double size, ChatMember? who)
    {
        FrameworkElement face = Face(name, group, size);
        if (who?.Pic is { Length: > 0 } pic)
        {
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit(); img.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(pic)); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.EndInit(); img.Freeze();
                face = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.HighQuality);   // smooth, not blocky, at any size
                var full = img; var whose = name;
                face.Cursor = Cursors.Hand; face.ToolTip = "See the picture";
                face.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowPicture(full, whose); };
            }
            catch { }
        }
        if (who == null || who.Live == "") return face;
        var color = Statuses.FirstOrDefault(s => s.Id == who.Live).Color ?? "#6B7280";
        var dot = new Border { Width = size * 0.3, Height = size * 0.3, CornerRadius = new CornerRadius(size * 0.15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                               BorderBrush = look.Card, BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                               ToolTip = who.Presence };   // invisible or gone quiet: offline, with when they were last here
        return new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Children = { face, dot } };
    }

    // a picture in the chat: its preview (keeping its shape, up to 260 × 260); a click shows it full size
    async Task Fill(Border into, ChatMsg m)
    {
        var path = await chats.Picture(m);
        if (path == null) { ((TextBlock)into.Child).Text = "The picture has expired"; return; }
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit(); img.UriSource = new Uri(path); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.EndInit(); img.Freeze();
            var k = Math.Min(260.0 / img.PixelWidth, 260.0 / img.PixelHeight);
            into.Width = Math.Max(80, img.PixelWidth * Math.Min(1, k)); into.Height = Math.Max(60, img.PixelHeight * Math.Min(1, k));
            into.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; into.Child = null;
            RenderOptions.SetBitmapScalingMode(into, BitmapScalingMode.HighQuality);
            into.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowPicture(img, m.File ?? ""); };
        }
        catch { if (into.Child is TextBlock tb) tb.Text = "Couldn't show this picture"; }
    }

    // someone's picture, large, over the whole app (not squeezed into the chat's window); a click or Esc closes it
    void ShowPicture(System.Windows.Media.Imaging.BitmapSource img, string name)
    {
        var owner = Window.GetWindow(this);
        var area = owner != null && owner.WindowState != WindowState.Maximized ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight) : SystemParameters.WorkArea;
        // never larger than the picture's own pixels (at this screen's scale), so it stays sharp; smaller only if the window is
        var dpi = owner != null ? VisualTreeHelper.GetDpi(owner).DpiScaleX : 1;
        var k = Math.Min(1, Math.Min(area.Width * 0.86 / (img.PixelWidth / dpi), area.Height * 0.8 / (img.PixelHeight / dpi)));
        var big = new Border { Width = img.PixelWidth / dpi * k, Height = img.PixelHeight / dpi * k, CornerRadius = new CornerRadius(18), Background = new ImageBrush(img) { Stretch = Stretch.Uniform } };
        RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.HighQuality);
        var label = T(name, 18, Brushes.White, true); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 16, 0, 0);
        var view = new Window
        {
            Owner = owner, WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Left = area.Left, Top = area.Top, Width = area.Width, Height = area.Height, Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x05, 0x07, 0x0A)), Cursor = Cursors.Hand,
            Content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { big, label } },
        };
        view.MouseLeftButtonUp += (_, _) => view.Close();
        view.KeyDown += (_, e) => { if (e.Key == Key.Escape) view.Close(); };
        view.Show(); view.Activate();
    }

    Border Face(string name, bool group, double size = 40) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = look.Accent, VerticalAlignment = VerticalAlignment.Center,
        Child = group ? new TextBlock { Text = "", FontFamily = icons, FontSize = size * 0.42, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                      : new TextBlock { Text = name.Length > 0 ? name[..1].ToUpper() : "?", FontSize = size * 0.42, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    TextBox Field(string? hint = null)
    {
        var b = new TextBox { Background = look.Field, Foreground = look.Fg, CaretBrush = look.Fg, BorderThickness = new Thickness(0), Padding = new Thickness(12, 9, 12, 9), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        return b;
    }

    static string When(long at)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime;
        return d.Date == DateTime.Today ? d.ToString("t") : d.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : d.ToString("d MMM");
    }

    // a sticker: its picture (Fluent Emoji 3D, in Assets\stickers), or the emoji itself if the picture is missing
    // emoji use the installed Windows emoji font, including joined and skin-tone sequences
    // a message that's nothing but one to three emoji (spaces aside): those emoji, else null
    static List<string>? OnlyEmoji(string text)
    {
        var faces = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text.Trim());
        while (e.MoveNext())
        {
            var el = e.GetTextElement();
            if (string.IsNullOrWhiteSpace(el)) continue;
            // emoji pieces only: pictographs and symbols, joiners, variation selectors and skin tones (no letters or digits)
            if (!el.EnumerateRunes().All(r => r.Value >= 0x1F000 || r.Value is 0x200D or 0xFE0F or 0x20E3 || System.Text.Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.OtherSymbol)) return null;
            faces.Add(el);
        }
        return faces.Count is >= 1 and <= 3 ? faces : null;
    }

    public static FrameworkElement Emoji(string emoji, double size)
    {
        if (ColorEmoji.Image(emoji) is { } img) return new Image { Source = img, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        return new TextBlock { Text = emoji, FontSize = size * 0.8, FontFamily = ColorEmoji.EmojiFontFamily, VerticalAlignment = VerticalAlignment.Center };
    }

    // a file's size as people read it: 512 B, 38 KB, 4.2 MB
    static string SizeText(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.#} KB" : $"{b / 1024.0 / 1024.0:0.#} MB";

    static string Preview(ChatMsg m) => m.Kind switch { "listen" => "🎵 Listen along: " + m.Text, "file" => "📎 " + m.File, "ivo" => "Ivo: " + m.Text, _ => m.Text };

    public static string WrapLongWords(string text)
    {
        const int maxRunLength = 32;
        var result = new System.Text.StringBuilder(text.Length + text.Length / maxRunLength);
        var runLength = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsWhiteSpace(rune)) runLength = 0;
            else if (runLength == maxRunLength) { result.Append('\u200B'); runLength = 0; }
            result.Append(rune.ToString());
            if (!System.Text.Rune.IsWhiteSpace(rune)) runLength++;
        }
        return result.ToString();
    }

    // ---------- screens ----------
    void Draw()
    {
        Children.Clear();
        var page = screen switch { "thread" when open != null => Thread(open), "members" when open != null => Members(open), "newgroup" => NewGroup(), "me" => MeScreen(), _ => List() };
        Children.Add(page);
    }

    FrameworkElement List()
    {
        var page = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var group = Btn("New group", look.Accent, () => { screen = "newgroup"; Draw(); });
        DockPanel.SetDock(group, Dock.Right); head.Children.Add(group);
        var chatCount = chats.Book.Threads.Count(t => !t.Deleted && !t.Binned);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T(chatCount == 0 ? "No chats yet" : $"{chatCount} chat{(chatCount == 1 ? "" : "s")}", 13, look.Dim) } });
        DockPanel.SetDock(head, Dock.Top); page.Children.Add(head);

        var find = Field();
        var findHint = T("Search chats and messages", 14, look.Dim); findHint.IsHitTestVisible = false; findHint.Margin = new Thickness(36, 0, 0, 0); findHint.VerticalAlignment = VerticalAlignment.Center;
        var glass = new TextBlock { Text = "", FontFamily = icons, FontSize = 13, Foreground = look.Dim, IsHitTestVisible = false, Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        find.Padding = new Thickness(34, 9, 12, 9); find.Text = query;
        findHint.Visibility = query == "" ? Visibility.Visible : Visibility.Collapsed;
        var findBox = new Grid { Margin = new Thickness(0, 0, 0, 10), Children = { find, glass, findHint } };
        DockPanel.SetDock(findBox, Dock.Top); page.Children.Add(findBox);
        if (chatCount == 0 && query == "") findBox.Visibility = Visibility.Collapsed;   // nothing to search yet
        var list = new StackPanel();
        find.TextChanged += (_, _) => { query = find.Text; findHint.Visibility = query == "" ? Visibility.Visible : Visibility.Collapsed; Fill(); };
        page.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        void Fill()
        {
        list.Children.Clear();
        var q = query.Trim();
        var threads = chats.Book.Threads.Where(t => !t.Deleted && !t.Binned && (q == "" || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Messages.Any(m => m.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || (m.File ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(t => t.Messages.LastOrDefault()?.At ?? 0).ToList();
        if (q != "" && threads.Count == 0) list.Children.Add(T("Nothing matches.", 13, look.Dim));
        else if (threads.Count == 0)
        {
            // nothing yet: a soft icon, a title and how to start, centred
            var mark = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = look.Field, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12),
                                    Child = new TextBlock { Text = "💬", FontSize = 24, FontFamily = ColorEmoji.EmojiFontFamily, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var title = T("No chats yet", 15, look.Fg, true); title.HorizontalAlignment = HorizontalAlignment.Center;
            var hint = T("Your contacts show here, to chat with any time. Connect with someone in New portal, then Add to contacts.", 12.5, look.Dim);
            hint.TextAlignment = TextAlignment.Center; hint.MaxWidth = 320; hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 4, 0, 0);
            list.Children.Add(new StackPanel { Margin = new Thickness(0, 40, 0, 0), Children = { mark, title, hint } });
        }
        foreach (var t in threads)
        {
            var last = (q != "" ? t.Messages.LastOrDefault(m => m.Text.Contains(q, StringComparison.OrdinalIgnoreCase)) : null) ?? t.Messages.LastOrDefault(m => m.Kind != "sys") ?? t.Messages.LastOrDefault();
            var row = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // the time, then the unread count, side by side on one line
            var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (last != null) meta.Children.Add(new TextBlock { Text = When(last.At), FontSize = 11.5, Foreground = t.Unread > 0 ? look.Accent : look.Dim, VerticalAlignment = VerticalAlignment.Center });
            if (t.Unread > 0) meta.Children.Add(new Border { MinWidth = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = look.Accent, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                                             Child = new TextBlock { Text = t.Unread.ToString(), FontSize = 11, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            right.Children.Add(meta);
            if (call != null && !t.Gone && !chats.IsBlocked(t))   // one-to-one: call them; a group: start a group call
            {
                var v = Round("", $"Call {t.Name}", "#34D399", () => call(t, "voice"), icons); v.Margin = new Thickness(14, 0, 0, 0);
                right.Children.Add(v);
                var vid = Round("", $"Video call {t.Name}", "#60A5FA", () => call(t, "video"), icons); vid.Margin = new Thickness(8, 0, 0, 0); right.Children.Add(vid);   // 8 between the icons
            }
            DockPanel.SetDock(right, Dock.Right); row.Children.Add(right);
            var face = Face(t.Name, t.Kind == "group", 40, chats.Person(t)); face.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(face, Dock.Left); row.Children.Add(face);
            var name = T(t.Name, 14.5, bold: t.Unread > 0); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            var line = T(last == null ? (t.Kind == "group" ? $"{t.Members.Count} people" : "Say hi") : (t.Kind == "group" && last.FromName != "" && last.Kind != "sys" ? last.FromName + ": " : "") + Preview(last), 12.5, look.Dim);
            line.TextWrapping = TextWrapping.NoWrap; line.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Children = { name, line } });
            var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Child = row };   // the same inset on both sides, in line with the search bar
            card.MouseEnter += (_, _) => card.Background = look.Field; card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;   // the hover bubble: the field colour stands out on any panel
            card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && Up<Button>(d) != null) return; OpenThread(t); };
            card.MouseRightButtonUp += (_, e) => { ChatMenu(t, card); e.Handled = true; };
            list.Children.Add(card);
        }
        }
        Fill();
        Dispatcher.BeginInvoke(() => { if (query != "") { find.Focus(); find.CaretIndex = query.Length; } }, System.Windows.Threading.DispatcherPriority.Input);
        return page;
    }

    static T? Up<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x)) if (x is T t) return t;
        return null;
    }

    FrameworkElement Thread(ChatThread t)
    {
        var page = new DockPanel();
        // the head: back, who, and calls (one-to-one) or members (group)
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var back = Btn("", null, () => { screen = "list"; open = null; Draw(); }, "All chats", glyph: true);
        DockPanel.SetDock(back, Dock.Left); head.Children.Add(back);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        if (call != null && !t.Gone && !chats.IsBlocked(t))
        {
            tools.Children.Add(Btn("", null, () => call(t, "voice"), "Call", glyph: true));
            tools.Children.Add(Btn("", null, () => call(t, "video"), "Video call", glyph: true));
        }
        tools.Children.Add(Btn("", null, () =>
        {
            var what = t.Kind == "group" ? $"Leave and delete \"{t.Name}\" on this PC?" : $"Delete your chat with {t.Name} on this PC? They keep their copy.";
            if (!IvoFrame.Message(Window.GetWindow(this), what, "Delete chat", "Delete", "Cancel")) return;
            chats.DeleteChat(t); open = null;
            screen = "list"; Draw();
        }, "Delete chat", glyph: true));
        if (t.Kind == "group") tools.Children.Add(Btn(t.Asked.Count > 0 && t.Admin == chats.Book.MyId ? $"Members · {t.Asked.Count} asking" : "Members", look.Card, () => { screen = "members"; Draw(); }));
        DockPanel.SetDock(tools, Dock.Right); head.Children.Add(tools);
        var face = Face(t.Name, t.Kind == "group", 34, chats.Person(t)); face.Margin = new Thickness(6, 0, 10, 0);
        DockPanel.SetDock(face, Dock.Left); head.Children.Add(face);
        var title = T(t.Name, 15, bold: true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var sub = T(t.Kind == "group" ? string.Join(", ", t.Members.Select(m => m.Id == chats.Book.MyId ? "You" : m.Name))
                    : (chats.Person(t) is { } who ? who.Presence + (who.App != "" ? $" · on {who.App}" : "") : ""), 12, look.Dim);
        sub.TextWrapping = TextWrapping.NoWrap; sub.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, sub } });
        DockPanel.SetDock(head, Dock.Top); page.Children.Add(head);

        if (chats.IsBlocked(t))
        {
            var unblock = Btn("Unblock", look.Accent, () => { chats.Block(t, false); Draw(); }); unblock.Margin = new Thickness(0, 10, 0, 0); unblock.HorizontalAlignment = HorizontalAlignment.Center;
            var said = T($"You blocked {t.Name}. Their messages and calls don't reach you, and they aren't told.", 13, look.Dim); said.TextAlignment = TextAlignment.Center;
            var bar = new StackPanel { Margin = new Thickness(0, 8, 0, 4), Children = { said, unblock } };
            DockPanel.SetDock(bar, Dock.Bottom); page.Children.Add(bar);
        }
        else if (!t.Gone) { var bar = Composer(t); DockPanel.SetDock(bar, Dock.Bottom); page.Children.Add(bar); }
        else { var gone = T("You're no longer in this group.", 13, look.Dim); gone.Margin = new Thickness(0, 8, 0, 0); DockPanel.SetDock(gone, Dock.Bottom); page.Children.Add(gone); }

        messages = new StackPanel();
        FillMessages(t);
        scroll = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 4, 0, 10) };
        page.Children.Add(scroll);
        Dispatcher.BeginInvoke(() => scroll?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        return t.Gone ? page : DropToSend(t, page);
    }

    // files dragged onto a chat: a "Drop to send" veil while they're over it, then each is sent, as Attach does
    FrameworkElement DropToSend(ChatThread t, FrameworkElement page)
    {
        var veil = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2), BorderBrush = look.Accent, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
                                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x17, 0x1C)),
                                Child = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = {
                                    new TextBlock { Text = "", FontFamily = icons, FontSize = 34, Foreground = look.Accent, HorizontalAlignment = HorizontalAlignment.Center },
                                    T($"Drop to send to {t.Name}", 15, look.Fg, true), T("Files up to 15 MB each", 12.5, look.Dim) } } };
        foreach (var x in ((StackPanel)veil.Child).Children.OfType<TextBlock>()) { x.HorizontalAlignment = HorizontalAlignment.Center; x.Margin = new Thickness(0, 4, 0, 0); }
        var holder = new Grid { AllowDrop = true, Background = Brushes.Transparent, Children = { page, veil } };
        static string[]? Files(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) ? (e.Data.GetData(DataFormats.FileDrop) as string[])?.Where(System.IO.File.Exists).ToArray() : null;
        holder.DragEnter += (_, e) => { if (Files(e) is { Length: > 0 }) veil.Visibility = Visibility.Visible; };
        holder.DragOver += (_, e) => { e.Effects = Files(e) is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        holder.DragLeave += (_, e) => { var p = e.GetPosition(holder); if (p.X <= 0 || p.Y <= 0 || p.X >= holder.ActualWidth || p.Y >= holder.ActualHeight) veil.Visibility = Visibility.Collapsed; };
        holder.Drop += async (_, e) =>
        {
            veil.Visibility = Visibility.Collapsed;
            if (Files(e) is not { Length: > 0 } files) return;
            e.Handled = true;
            var to = replying; replying = null;
            foreach (var f in files)
            {
                var err = await chats.SendFile(t, f, to); to = null;   // a reply goes with the first file only
                if (err != null) IvoFrame.Message(Window.GetWindow(this), $"{System.IO.Path.GetFileName(f)}: {err}", "Couldn't send the file");
            }
            Draw();
        };
        return holder;
    }

    // a chat's messages: each day's under its date, and one person's messages a moment apart closer together
    void FillMessages(ChatThread t)
    {
        messages!.Children.Clear();
        ChatMsg? last = null;
        var lastMine = t.Kind == "dm" ? t.Messages.LastOrDefault(x => x.From == chats.Book.MyId && x.Kind != "sys") : null;
        foreach (var m in t.Messages)
        {
            var day = DateTimeOffset.FromUnixTimeSeconds(m.At).LocalDateTime.Date;
            if (last == null || DateTimeOffset.FromUnixTimeSeconds(last.At).LocalDateTime.Date != day)
            {
                var d = T(day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString(day.Year == DateTime.Today.Year ? "ddd, d MMM" : "d MMM yyyy"), 11.5, look.Dim, true);
                d.HorizontalAlignment = HorizontalAlignment.Center; d.Margin = new Thickness(0, last == null ? 2 : 10, 0, 10);
                messages.Children.Add(d);
            }
            else if (last.From == m.From && last.Kind != "sys" && m.Kind != "sys" && m.At - last.At < 120 && messages.Children[^1] is FrameworkElement prev)
                prev.Margin = new Thickness(prev.Margin.Left, prev.Margin.Top, prev.Margin.Right, prev.Margin.Bottom - 5);
            messages.Children.Add(Bubble(t, m));
            if (m == lastMine && !m.Pending && Status(m) is { } status) messages.Children.Add(status);
            last = m;
        }
        if (t.Messages.Count == 0)
        {
            var hello = T(t.Kind == "group" ? "Say hello to the group." : $"Say hi to {t.Name}. Only the two of you can read this.", 13, look.Dim);
            hello.HorizontalAlignment = HorizontalAlignment.Center; hello.TextAlignment = TextAlignment.Center; hello.Margin = new Thickness(20, 40, 20, 0);
            messages.Children.Add(hello);
        }
    }

    // under your newest message in a one-to-one chat: Sent, Delivered, or Seen (Seen only with read receipts on)
    TextBlock? Status(ChatMsg m)
    {
        var (text, brush) = m.Read && PortalDevices.ReadReceipts ? ("Seen", look.Accent) : m.Delivered ? ("Delivered", look.Dim) : ("Sent", look.Dim);
        var t = T(text, 11, brush); t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, -4, 4, 6);
        if (text == "Seen") t.Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
        return t;
    }

    ChatMsg? replying;   // the message the next one answers (Reply in a message's menu)

    // the message bar: a reply strip (when answering one), attach, the words, send, and Ask Ivo
    FrameworkElement Composer(ChatThread t)
    {
        var input = Field(); input.AcceptsReturn = false;
        var to = replying;
        var hint = T("Message", 14, look.Dim); hint.ToolTip = askIvo != null ? "Ctrl+Enter asks Ivo" : null; hint.IsHitTestVisible = false; hint.Margin = new Thickness(15, 0, 0, 0); hint.VerticalAlignment = VerticalAlignment.Center;
        input.TextChanged += (_, _) => hint.Visibility = input.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        void Post(bool ivo)
        {
            var text = input.Text.Trim(); if (text == "") return;
            input.Clear();
            _ = chats.Text(t, text, replyTo: to);
            if (to != null) { replying = null; Draw(); }
            if (ivo && askIvo != null) _ = Ask(t, text);
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Post(Keyboard.Modifiers == ModifierKeys.Control && askIvo != null); } };
        var attach = Btn("", null, () => _ = Attach(t), "Send a file (up to 15 MB)", glyph: true);
        var send = Btn("", look.Accent, () => Post(false), "Send", glyph: true); send.Margin = new Thickness(8, 0, 0, 0);   // the paper-plane arrow, as Instagram has it
        var row = new DockPanel();
        DockPanel.SetDock(attach, Dock.Left); DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(attach); row.Children.Add(send);
        if (Listen != null && t.Kind == "dm" && chats.Person(t)?.App != "Ivo Portal")   // Spotify together (Ivo with Spotify, and they use Ivo too: Ivo Portal has no Spotify)
        {
            var listen = Btn("", null, () => Listen(t), "Listen to Spotify together", glyph: true);
            listen.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            DockPanel.SetDock(listen, Dock.Left); row.Children.Insert(2, listen);
        }
        if (askIvo != null) { var ask = Btn("Ask Ivo", look.Card, () => Post(true), "Ivo answers, and everyone here sees it"); ask.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(ask, Dock.Right); row.Children.Insert(2, ask); }
        row.Children.Add(new Grid { Margin = new Thickness(6, 0, 0, 0), Children = { input, hint } });
        Dispatcher.BeginInvoke(() => input.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        if (to == null) return row;
        var x = Btn("\uE711", null, () => { replying = null; Draw(); }, "Don't reply", glyph: true);
        var quote = new DockPanel(); DockPanel.SetDock(x, Dock.Right); quote.Children.Add(x);
        var words = T(IvoChats.Gist(to), 12.5, look.Dim); words.TextTrimming = TextTrimming.CharacterEllipsis; words.TextWrapping = TextWrapping.NoWrap;
        quote.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T($"Replying to {(to.From == chats.Book.MyId ? "yourself" : to.FromName)}", 12, look.Accent is SolidColorBrush ? look.Accent : new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), true), words } });
        var strip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6, 4, 6), Margin = new Thickness(0, 0, 0, 8), Background = look.Card,
                                 BorderBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)), BorderThickness = new Thickness(3, 0, 0, 0), Child = quote };
        return new StackPanel { Children = { strip, row } };
    }

    async Task Ask(ChatThread t, string question)
    {
        string answer;
        try { answer = await askIvo!(t, question); } catch (Exception ex) { answer = "I couldn't answer: " + ex.Message.Split('\n')[0]; }
        await chats.Text(t, answer, fromIvo: true);
    }

    async Task Attach(ChatThread t)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "A file to send (up to 15 MB)" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var to = replying; replying = null;
        var err = await chats.SendFile(t, dlg.FileName, to);
        if (to != null) Draw();
        if (err != null) IvoFrame.Message(Window.GetWindow(this), err, "Couldn't send the file");
    }


    // a scrollbar that looks like one: a rounded thumb on a faint track, a little wider under the mouse
    public static Style SlimScrollBar(ChatLook look) => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Width" Value="6" /><Setter Property="MinWidth" Value="6" />
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="ScrollBar">
              <Border CornerRadius="3" Background="#14FFFFFF" Margin="0,4">
                <Track x:Name="PART_Track" IsDirectionReversed="True">
                  <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb">
                    <Border x:Name="Face" CornerRadius="3" Background="#66FFFFFF" />
                    <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Background" Value="#AAFFFFFF" /></Trigger></ControlTemplate.Triggers>
                  </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                </Track>
              </Border>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """);

    // every emoji supported by the installed Windows emoji font, eight to a row
    public static void PickEmoji(FrameworkElement at, ChatLook look, Action<string> picked)
    {
        const int across = 8;
        var all = ColorEmoji.All;
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Width = across * 44 + 14, Height = 340, Padding = new Thickness(0),
                                 FocusVisualStyle = null };
        list.Resources[typeof(ScrollBar)] = SlimScrollBar(look);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);   // smooth, not a row at a time
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Standard);
        var card = new Border { Background = look.Card, CornerRadius = new CornerRadius(16), BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Padding = new Thickness(6, 6, 2, 6), Child = list,
                                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black } };
        var close = Overlay(at, card);
        // a plain row: no selection highlight from the list itself
        var plain = new Style(typeof(ListBoxItem));
        plain.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        plain.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        list.ItemContainerStyle = plain;
        for (var i = 0; i < all.Count; i += across)
        {
            var start = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 44 };
            row.Loaded += (_, _) =>
            {
                if (row.Children.Count > 0) return;
                foreach (var s in all.Skip(start).Take(across))
                {
                    FrameworkElement face = ColorEmoji.Picture(s) is { } picture ? new Image { Source = picture, Width = 30, Height = 30 } : Emoji(s, 30);
                    var b = new Button { Content = face, Width = 44, Height = 44, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(0), ToolTip = null, Focusable = false };
                    b.Click += (_, _) => { close(); picked(s); };
                    row.Children.Add(b);
                }
            };
            list.Items.Add(row);
        }
        if (all.Count == 0) list.Items.Add(new TextBlock { Text = "Windows' emoji font isn't on this PC.", FontSize = 13, Foreground = look.Dim, Margin = new Thickness(10) });
    }

    // something shown inside the app, in the middle, over a blurred picture of the window (a click outside or Esc closes
    // it); returns what closes it. Without a window to cover, it floats as a popup by `at` instead.
    static Action Overlay(FrameworkElement at, FrameworkElement card)
    {
        Grid? host = null;
        for (DependencyObject? d = at; d != null; d = VisualTreeHelper.GetParent(d)) if (d is Grid g) host = g;   // the outermost grid: the whole window
        if (host == null || host.ActualWidth <= 0)
        {
            var pop = new Popup { PlacementTarget = at, Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Child = card };
            pop.IsOpen = true;
            return () => pop.IsOpen = false;
        }
        var dpi = VisualTreeHelper.GetDpi(host);
        var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)(host.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(host.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        shot.Render(host);
        var picture = new Image { Source = shot, Stretch = Stretch.Fill, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 18, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance } };
        var veil = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0x06, 0x08, 0x0C)) };
        card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
        card.RenderTransformOrigin = new Point(0.5, 0.5); card.RenderTransform = new ScaleTransform(0.94, 0.94);
        var layer = new Grid { Children = { picture, veil, card }, Focusable = true, Opacity = 0 };
        Grid.SetRowSpan(layer, 99); Grid.SetColumnSpan(layer, 99); Panel.SetZIndex(layer, 2500);
        var closed = false;
        void Close()
        {
            if (closed) return;
            closed = true;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) => host.Children.Remove(layer);
            layer.BeginAnimation(OpacityProperty, fade);
        }
        veil.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        picture.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        layer.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        host.Children.Add(layer);
        layer.Focus();
        layer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        var pop2 = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.3, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop2);
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop2);
        return Close;
    }

    TextBlock Quote(string text) { var q = T(text, 12.5, look.Dim); q.MaxHeight = 36; q.TextTrimming = TextTrimming.CharacterEllipsis; return q; }   // two lines at most

    FrameworkElement Bubble(ChatThread t, ChatMsg m)
    {
        if (m.Kind == "sys") { var s = T(m.Text, 12, look.Dim); s.HorizontalAlignment = HorizontalAlignment.Center; s.Margin = new Thickness(0, 6, 0, 10); return s; }
        var mine = m.From == chats.Book.MyId;
        var stack = new StackPanel();
        if (m.ReplyTo != null)
        {
            // the quote of what this answers: who, and a few words
            var q = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 6), Margin = new Thickness(0, 0, 0, 6), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                                 BorderBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)), BorderThickness = new Thickness(3, 0, 0, 0),
                                 Child = new StackPanel { Children = { T(m.ReplyFrom == chats.Book.MyName ? "You" : m.ReplyFrom ?? "", 11.5, new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), true), Quote(m.ReplyText ?? "") } } };
            stack.Children.Add(q);
        }
        if (m.Kind == "ivo") stack.Children.Add(T($"Ivo · asked by {(mine ? "you" : m.FromName)}", 11.5, new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), true));
        else if (!mine && t.Kind == "group") stack.Children.Add(T(m.FromName, 11.5, look.Dim, true));
        Border body;
        var faces = m.Kind == "sticker" ? [m.Text] : m.Kind == "text" ? OnlyEmoji(m.Text) : null;
        if (faces != null)
        {
            // an emoji on its own, from the picker or typed (Win + .): big, with no bubble, up to three side by side
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var f in faces) { var e = Emoji(f, 64); e.Margin = new Thickness(0, 0, 4, 0); row.Children.Add(e); }
            stack.Children.Add(row);
            body = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 8) };
        }
        else if (IvoChats.IsPicture(m))
        {
            // a picture: one already on this PC (yours, or downloaded) shows at once; one sent to you waits for Download,
            // so nothing is fetched until you ask. A click shows it full size; Save keeps the original
            var pic = new Border { Width = 260, Height = 200, CornerRadius = new CornerRadius(12), Background = look.Card, Cursor = Cursors.Hand,
                                   Child = T("Loading the picture…", 12, look.Dim) };
            ((TextBlock)pic.Child).HorizontalAlignment = HorizontalAlignment.Center; ((TextBlock)pic.Child).VerticalAlignment = VerticalAlignment.Center;
            var here = new[] { m.Saved, m.Cached }.Any(f => f != null && System.IO.File.Exists(f));
            if (here) _ = Fill(pic, m);
            else
            {
                var get = Btn("Download", look.Accent, () => { });
                get.HorizontalAlignment = HorizontalAlignment.Center; get.Margin = new Thickness(0, 10, 0, 0);
                var icon = new TextBlock { Text = "", FontFamily = icons, FontSize = 28, Foreground = look.Dim, HorizontalAlignment = HorizontalAlignment.Center };
                var size = T($"Photo · {SizeText(m.Size)}", 12.5, look.Dim); size.HorizontalAlignment = HorizontalAlignment.Center; size.Margin = new Thickness(0, 6, 0, 0);
                pic.Height = 150; pic.Cursor = Cursors.Arrow;
                pic.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, size, get } };
                get.Click += (_, _) =>
                {
                    var wait = T("Downloading…", 12, look.Dim); wait.HorizontalAlignment = HorizontalAlignment.Center; wait.VerticalAlignment = VerticalAlignment.Center;
                    pic.Child = wait; pic.Cursor = Cursors.Hand;
                    _ = Fill(pic, m);
                };
            }
            // just the photo: no frame, no name or size (Save is in its right-click menu)
            stack.Children.Add(pic);
            body = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 8) };
        }
        else if (m.Kind == "listen")
        {
            // a listen-along invite, as Discord shows one: the cover, what's playing, and Join
            var cover = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Background = look.Card, Margin = new Thickness(0, 0, 12, 0) };
            if (m.Url is { Length: > 0 } art) try { cover.Background = new ImageBrush(new System.Windows.Media.Imaging.BitmapImage(new Uri(art))) { Stretch = Stretch.UniformToFill }; } catch { }
            var green = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                T(mine ? "You invited them to listen along" : $"{m.FromName} invites you to listen along", 11.5, green, true),
                T(m.Text, 14.5, bold: true), T(m.Artist ?? "", 12.5, look.Dim) } };
            var card = new DockPanel(); DockPanel.SetDock(cover, Dock.Left); card.Children.Add(cover); card.Children.Add(words);
            stack.Children.Add(card);
            if (!mine)
            {
                if (JoinListen != null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - m.At < 7200)
                {
                    var join = Btn("Join", green, () => JoinListen(t)); join.HorizontalAlignment = HorizontalAlignment.Left; join.Margin = new Thickness(0, 10, 0, 0);
                    stack.Children.Add(join);
                }
                else stack.Children.Add(T(JoinListen == null ? "Join from Ivo, with Spotify connected" : "This invite has ended", 12, look.Dim));
            }
            body = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12), Background = look.Field, Child = stack, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 360 };
        }
        else if (m.Kind == "file")
        {
            // saved to Downloads\Ivo Chats and shown in File Explorer: a file someone sent never runs by itself
            var open = Btn(m.Saved != null && System.IO.File.Exists(m.Saved) ? "Show" : "Save", look.Accent, async () =>
            {
                try { var path = await chats.Download(m); System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); Draw(); }
                catch (Exception ex) { IvoFrame.Message(Window.GetWindow(this), "It may have expired (files are kept about 3 hours). " + ex.Message, "Couldn't get the file"); }
            });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0), Children = { T(m.File ?? "file", 14, bold: true), T(SizeText(m.Size), 12, look.Dim) } };
            var row = new DockPanel();
            var ic = new TextBlock { Text = "", FontFamily = icons, FontSize = 24, Foreground = look.Fg, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(ic, Dock.Left); DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(ic); row.Children.Add(open); row.Children.Add(info);
            stack.Children.Add(row);
            body = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 10, 10), Background = mine ? look.Mine : look.Field, Child = stack, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 340 };
        }
        else
        {
            var said = T("", 14); ColorEmoji.Append(said.Inlines, WrapLongWords(m.Text), 14); stack.Children.Add(said);
            body = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 9, 14, 10), Background = mine && m.Kind != "ivo" ? look.Mine : look.Field, Child = stack, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 440 };   // wraps at a readable width
        }
        if (mine && m.Pending) { var wait = T("Waiting to send…", 11, look.Dim); wait.Margin = new Thickness(0, 4, 0, 0); stack.Children.Add(wait); }   // the outbox sends it as soon as it can
        stack.Children.Add(new Border { Height = 2 });
        body.Margin = new Thickness(0, 0, 0, 8);
        return WithActions(t, m, body, mine);
    }

    // a message, with its reactions under it; right-click it to react, reply, copy, save, unsend / delete
    FrameworkElement WithActions(ChatThread t, ChatMsg m, FrameworkElement body, bool mine)
    {
        var holder = new StackPanel { HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(mine ? 40 : 0, 0, mine ? 0 : 40, 0) };
        holder.Children.Add(body);
        if (m.Reactions.Count > 0)
        {
            // the reactions, as a little pill tucked under the bubble's corner
            var faces = m.Reactions.Values.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key);
            var words = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var f in faces) { var e = Emoji(f, 15); e.Margin = new Thickness(1, 0, 1, 0); words.Children.Add(e); }
            if (m.Reactions.Count > 1) { var n = T($"{m.Reactions.Count}", 11.5, look.Dim); n.Margin = new Thickness(3, 0, 0, 0); n.VerticalAlignment = VerticalAlignment.Center; words.Children.Add(n); }
            var pill = new Border { Child = words, CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 1, 6, 2), Background = look.Card, BorderBrush = TryFindResource("GlassStroke") as Brush ?? look.Field, BorderThickness = new Thickness(1),
                                    HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(10, -14, 10, 8), Cursor = Cursors.Hand,
                                    ToolTip = string.Join("\n", m.Reactions.Select(x => $"{x.Value}  {(x.Key == chats.Book.MyId ? "You" : t.Members.FirstOrDefault(p => p.Id == x.Key)?.Name ?? "Someone")}")) };
            body.Margin = new Thickness(0, 0, 0, 0);
            holder.Children.Add(pill);
        }
        body.MouseRightButtonUp += (_, e) => { e.Handled = true; Lift(t, m, body, mine); };
        return holder;
    }

    // right-click a message: the chat blurs, the message lifts out a little, its reactions above and what you can do below
    // (with when it was sent); a click anywhere else, Esc, or a choice puts it all back
    void Lift(ChatThread t, ChatMsg m, FrameworkElement body, bool mine)
    {
        var items = new List<MessageLift.Item>();
        if (!t.Gone) items.Add(new("\uE97A", "Reply", () => { replying = m; Draw(); }));
        if (m.Kind is "text" or "ivo") items.Add(new("\uE8C8", "Copy", () => { try { Clipboard.SetText(m.Text); } catch { } }));
        if (m.Kind == "file") items.Add(new("\uE896", IvoChats.IsPicture(m) ? "Save photo" : "Save file", async () =>
        {
            try { var path = await chats.Download(m); System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch (Exception ex) { IvoFrame.Message(Window.GetWindow(this), "It may have expired (files are kept about 3 hours). " + ex.Message, "Couldn't get it"); }
        }));
        if (mine && !t.Gone) items.Add(new("\uE7A7", "Unsend for everyone", () => _ = chats.Unsend(t, m), Danger: true));
        else items.Add(new("\uE74D", "Delete for me", () => chats.DeleteForMe(t, m), Danger: true));
        MessageLift.Show(body, mine, DateTimeOffset.FromUnixTimeSeconds(m.At).LocalDateTime, items,
                         IvoChats.ReactionSet, m.Reactions.GetValueOrDefault(chats.Book.MyId), e => _ = chats.React(t, m, e));
    }

    // a group's people: the admin adds (from contacts), removes, and lets in those others invited; anyone invites a contact or leaves
    FrameworkElement Members(ChatThread g)
    {
        var admin = g.Admin == chats.Book.MyId;
        var page = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var back = Btn("", null, () => { screen = "thread"; Draw(); }, "Back to the chat", glyph: true);
        DockPanel.SetDock(back, Dock.Left); head.Children.Add(back);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Children = { T(g.Name, 15, bold: true), T(admin ? "You're the admin" : "Members", 12, look.Dim) } });
        page.Children.Add(head);

        if (admin && g.Asked.Count > 0)
        {
            page.Children.Add(T("Asking to join", 13, look.Dim, true));
            foreach (var a in g.Asked.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { Btn("Let in", look.Accent, () => _ = chats.Approve(g, a)), Btn("Decline", null, () => _ = chats.Decline(g, a)) } };
                DockPanel.SetDock(tools, Dock.Right); row.Children.Add(tools);
                var by = g.Members.FirstOrDefault(m => m.Id == g.AskedBy.GetValueOrDefault(a.Name))?.Name;
                row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Face(a.Name, false, 32), new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { T(a.Name, 14), T(by != null ? $"invited by {by}" : "invited", 12, look.Dim) } } } });
                page.Children.Add(row);
            }
            page.Children.Add(new Border { Height = 16 });
        }

        page.Children.Add(T($"{g.Members.Count} people", 13, look.Dim, true));
        foreach (var m in g.Members)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (admin && m.Id != chats.Book.MyId) { var rm = Btn("Remove", null, () => _ = chats.Remove(g, m)); DockPanel.SetDock(rm, Dock.Right); row.Children.Add(rm); }
            var shown = m.Id == chats.Book.MyId ? new ChatMember { Pic = chats.Book.Pic, Status = chats.Book.Status } : m;
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Face(m.Name, false, 32, shown), new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Children = { T(m.Id == chats.Book.MyId ? $"{m.Name} (you)" : m.Name, 14), T(m.Id == g.Admin ? "Admin" : "Member", 12, look.Dim) } } } });
            page.Children.Add(row);
        }

        // invite: your contacts who aren't in it yet (the admin adds them; anyone else asks the admin)
        var outside = chats.Book.Threads.Where(t => t.Kind == "dm" && !g.Members.Any(m => m.Name == t.Name)).ToList();
        if (outside.Count > 0)
        {
            page.Children.Add(new Border { Height = 16 });
            page.Children.Add(T(admin ? "Add from your contacts" : "Invite a contact (the admin decides)", 13, look.Dim, true));
            foreach (var d in outside)
            {
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var go = Btn(admin ? "Add" : "Invite", look.Card, () => { _ = chats.Invite(g, d); screen = "thread"; Draw(); });
                DockPanel.SetDock(go, Dock.Right); row.Children.Add(go);
                row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Face(d.Name, false, 32), new Border { Width = 10 }, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T(d.Name, 14) } } } });
                page.Children.Add(row);
            }
        }
        page.Children.Add(new Border { Height = 20 });
        var leave = Btn("Leave the group", new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)), () => { _ = chats.Leave(g); screen = "list"; open = null; Draw(); });
        leave.HorizontalAlignment = HorizontalAlignment.Left;
        if (!g.Gone) page.Children.Add(leave);
        return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    }

    // you: your picture (any photo, made small and round) and your status; everyone you chat with sees them
    FrameworkElement MeScreen()
    {
        var page = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var back = Btn("\uE72B", null, () => { screen = "list"; Draw(); }, "All chats", glyph: true);
        DockPanel.SetDock(back, Dock.Left); head.Children.Add(back);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Children = { T("You", 15, bold: true), T("Your contacts and groups see these", 12, look.Dim) } });
        page.Children.Add(head);

        var face = Face(chats.Book.MyName, false, 96, new ChatMember { Pic = chats.Book.Pic, Status = chats.Book.Status });
        face.HorizontalAlignment = HorizontalAlignment.Center;
        page.Children.Add(face);
        if (Rename == null)
        {
            var name = T(chats.Book.MyName, 18, bold: true); name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 12, 0, 14);
            page.Children.Add(name);
        }
        else
        {
            // your name: change it and press Enter (or click away); your contacts see the new one
            var box = Field(); box.Text = chats.Book.MyName; box.FontSize = 16; box.HorizontalContentAlignment = HorizontalAlignment.Center; box.Margin = new Thickness(40, 14, 40, 14);
            void Keep() { var n = box.Text.Trim(); if (n != "" && n != chats.Book.MyName) Rename(n); }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Keep(); } };
            box.LostFocus += (_, _) => Keep();
            page.Children.Add(box);
        }
        var pics = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 22) };
        pics.Children.Add(Btn(chats.Book.Pic == "" ? "Add a picture" : "Change picture", look.Accent, () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Your picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try { if (PictureCropper.Crop(dlg.FileName, Window.GetWindow(this)) is { } pic) { _ = chats.SetProfile(pic, null); Draw(); } }   // you crop it
            catch (Exception ex) { IvoFrame.Message(Window.GetWindow(this), ex.Message, "Couldn't use that picture"); }
        }));
        if (chats.Book.Pic != "") { var rm = Btn("Remove", null, () => { _ = chats.SetProfile("", null); Draw(); }); rm.Margin = new Thickness(8, 0, 0, 0); pics.Children.Add(rm); }
        page.Children.Add(pics);

        page.Children.Add(T("Status", 13, look.Dim, true));
        foreach (var (id, label, color) in Statuses)
        {
            var on = chats.Book.Status == id;
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var tick = T(on ? "\uE73E" : "", 14, look.Fg); tick.FontFamily = icons; tick.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { Background = Brushes.Transparent };
            DockPanel.SetDock(tick, Dock.Right); row.Children.Add(tick);
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, new StackPanel { Children = { T(label, 14), T(id switch { "busy" => "They'll know you may not answer", "away" => "Not at your desk", "invisible" => "You show as offline", _ => "Around and reachable" }, 12, look.Dim) } } } });
            var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 0), Background = on ? look.Card : Brushes.Transparent, Cursor = Cursors.Hand, Child = row };
            card.MouseLeftButtonUp += (_, _) => { _ = chats.SetProfile(null, id); Draw(); };
            page.Children.Add(card);
        }
        return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    }

    // a new group: a name and who's in it (from your contacts); you're its admin
    FrameworkElement NewGroup()
    {
        var page = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var back = Btn("", null, () => { screen = "list"; Draw(); }, "All chats", glyph: true);
        DockPanel.SetDock(back, Dock.Left); head.Children.Add(back);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Children = { T("New group", 15, bold: true), T("You'll be its admin", 12, look.Dim) } });
        DockPanel.SetDock(head, Dock.Top); page.Children.Add(head);

        var name = Field();
        var nameHint = T("Group name", 14, look.Dim); nameHint.IsHitTestVisible = false; nameHint.Margin = new Thickness(15, 0, 0, 0); nameHint.VerticalAlignment = VerticalAlignment.Center;
        name.TextChanged += (_, _) => nameHint.Visibility = name.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        var nameBox = new Grid { Children = { name, nameHint }, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(nameBox, Dock.Top); page.Children.Add(nameBox);

        var picks = new List<(CheckBox Box, ChatThread Dm)>();
        var list = new StackPanel();
        foreach (var d in chats.Book.Threads.Where(t => t.Kind == "dm").OrderBy(t => t.Name))
        {
            var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            picks.Add((box, d));
            list.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { box, Face(d.Name, false, 30), new Border { Width = 10 }, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T(d.Name, 14) } } } });
        }
        if (picks.Count == 0) list.Children.Add(T("Add contacts in Portal first.", 13, look.Dim));
        var note = T("", 13, new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
        var make = Btn("Create group", look.Accent, async () =>
        {
            var who = picks.Where(p => p.Box.IsChecked == true).Select(p => p.Dm).ToList();
            if (name.Text.Trim() == "") { note.Text = "Give the group a name."; return; }
            if (who.Count == 0) { note.Text = "Pick at least one person."; return; }
            var g = await chats.NewGroup(name.Text.Trim(), who);
            OpenThread(g);
        });
        var foot = new StackPanel { Children = { note, make }, Margin = new Thickness(0, 12, 0, 0) };
        make.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(foot, Dock.Bottom); page.Children.Add(foot);
        page.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        return page;
    }
}
