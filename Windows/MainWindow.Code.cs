using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ivo;

// Code mode: a light IDE around the active chat — the workspace's files on the left, an editor with tabs and a
// terminal in the middle, the chat on the right. Files the agent changes refresh in the tree and in open tabs.
public partial class MainWindow
{
    record OpenFile(string Path)
    {
        public string Kind { get; set; } = "text";   // text | image | binary
        public string Info { get; set; } = "";       // for images and binaries: size, dimensions
        public string Text { get; set; } = "";
        public bool Dirty { get; set; }
        public Button Tab { get; set; } = null!;
    }

    readonly List<OpenFile> files = [];
    OpenFile? current;
    bool loadingEditor;
    string? root;                                  // the workspace shown in the explorer
    FileSystemWatcher? watcher;
    DispatcherTimer? refreshSoon;
    readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);

    bool CodeMode => S.View == "code";

    // the docked chat outlines like the explorer and editor: purple while you're in it, white otherwise
    internal void CodeChat_FocusChanged(object s, DependencyPropertyChangedEventArgs e)
    {
        if (CodeChat.Child is ChatPane p) p.Frame.BorderBrush = (Brush)FindResource(CodeChat.IsKeyboardFocusWithin ? "CodeStroke" : "CodeIdleStroke");
    }

    // no tabs open: no divider either (it would sit right under the panel's edge, a double line)
    void Tabs_SizeChanged(object s, SizeChangedEventArgs e) => TabLine.Visibility = Tabs.ActualHeight > 1 ? Visibility.Visible : Visibility.Collapsed;

    void ViewMode_Checked(object s, RoutedEventArgs e)
    {
        if (loading || s is not RadioButton { Tag: string v }) return;
        S.View = v; SaveSettings();
        ApplyView();
        // the mode arriving: a short fade and rise, like Windows 11's page changes (drawn on the GPU, so it stays smooth)
        foreach (var el in CodeMode ? new FrameworkElement[] { CodeView } : [Sidebar, Panes])
        {
            var rise = new System.Windows.Media.TranslateTransform(0, 10);
            el.RenderTransform = rise;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }
    }

    // Chat mode: sidebar + chat panes. Code mode: the IDE, with its own chat docked on the right. Each mode keeps its pane and
    // its chat; the one not in use is only hidden (still laid out), so switching back and forth rebuilds and re-measures nothing.
    // Code mode's pane, file tree and editor, built once: a few seconds after Ivo starts (while idle), or on the first switch
    void PrepareCode()
    {
        if (codePane != null) return;
        if (CodeView.Visibility == Visibility.Collapsed) CodeView.Visibility = Visibility.Hidden;   // laid out now, unseen, so showing it is only a redraw
        var code = allChats.FirstOrDefault(c => c.Space == "code" && !c.Archived);
        codePane = new ChatPane(this);
        codePane.ShowPopOut(companion is not { IsVisible: true });
        if (code == null) { var was = S.View; S.View = "code"; code = NewChat(); S.View = was; }   // a new chat belongs to Code mode
        codePane.ShowChat(code);
        CodeChat.Child = codePane;
        LoadWorkspace(code.Workspace);
        _ = Task.Run(Shells);   // find shells and CLIs before + is clicked
    }

    void ApplyView()
    {
        Sidebar.Visibility = Panes.Visibility = CodeMode ? Visibility.Hidden : Visibility.Visible;
        CodeView.Visibility = CodeMode ? Visibility.Visible : Visibility.Hidden;
        // hidden terminals keep running, but their page gives back what memory it can until Code mode is back
        if (termView?.CoreWebView2 is { } tw) tw.MemoryUsageTargetLevel = CodeMode ? Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Normal : Microsoft.Web.WebView2.Core.CoreWebView2MemoryUsageTargetLevel.Low;
        if (CodeMode)
        {
            PrepareCode();
            // the terminal (an embedded browser) starts once Code mode is on screen, so it never holds up the switch
            if (terms.Count == 0) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { if (CodeMode && terms.Count == 0) ShowTerm(null); });
            if (active != codePane) chatActive = active;
            ActivatePane(codePane!);
            codePane!.Greet(code: true);
        }
        else
        {
            ActivatePane(chatActive != null && panes.Contains(chatActive) ? chatActive : panes[0]);
            foreach (var p in panes) p.Greet(code: false);
        }
        ShowChats();
    }

    // A chat's folder changed (picked, cleared or another chat opened).
    public void WorkspaceChanged(ChatPane p)
    {
        if (CodeMode && p == active && !string.Equals(root, p.Chat.Workspace, StringComparison.OrdinalIgnoreCase)) LoadWorkspace(p.Chat.Workspace);
    }

    // ---------- explorer: Files | Chats ----------
    void ExplorerView_Checked(object s, RoutedEventArgs e)
    {
        if (FilesView == null) return;
        var chats = ChatsToggle.IsChecked == true; var git = GitToggle.IsChecked == true;
        FilesView.Visibility = chats || git ? Visibility.Collapsed : Visibility.Visible;
        CodeChatsView.Visibility = chats ? Visibility.Visible : Visibility.Collapsed;
        GitView.Visibility = git ? Visibility.Visible : Visibility.Collapsed;
        if (chats) RefreshCodeChats();
        if (git) RefreshGit();
    }

    void RefreshCodeChats()
    {
        CodeChatList.ItemsSource = allChats.Where(c => c.Space == "code" && !c.Archived).OrderByDescending(c => c.Pinned).ThenByDescending(c => c.Updated).ToList();
        CodeChatList.SelectedItem = (CodeChatList.ItemsSource as List<Chat>)?.FirstOrDefault(c => c.Id == active.Chat.Id);
    }

    void CodeChatList_Click(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(CodeChatList, d) is ListBoxItem { DataContext: Chat c } && c.Id != active.Chat.Id)
            active.ShowChat(c);
    }

    void CodeNewChat_Click(object s, RoutedEventArgs e) { active.ShowChat(NewChat()); RefreshChats(); RefreshCodeChats(); }

    // ---------- explorer ----------
    void LoadWorkspace(string? folder)
    {
        watcher?.Dispose(); watcher = null;
        root = folder != null && Directory.Exists(folder) ? folder : null;
        ExplorerTitle.Text = root == null ? "EXPLORER" : Path.GetFileName(root.TrimEnd('\\')).ToUpperInvariant();
        ExplorerTitle.ToolTip = root;
        OpenFolderHint.Visibility = root == null ? Visibility.Visible : Visibility.Collapsed;
        FileTree.Visibility = root == null ? Visibility.Collapsed : Visibility.Visible;
        expanded.Clear();
        RefreshTree();
        if (root == null) return;
        Watch();
    }

    // Changes made outside Ivo (File Explorer, a build, git) refresh the tree. Windows drops a watcher whose buffer
    // overflows (lots of changes at once in a big folder) without saying so in events, so on an error it starts again;
    // and coming back to the window refreshes anyway (see Window_Activated).
    void Watch()
    {
        watcher?.Dispose(); watcher = null;
        if (root == null || !Directory.Exists(root)) return;
        watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                                                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        FileSystemEventHandler changed = (_, a) => { if (!Ignored(a.FullPath) || a.FullPath.EndsWith("HEAD") || a.FullPath.EndsWith("index")) Dispatcher.BeginInvoke(RefreshSoon); };
        watcher.Created += changed; watcher.Deleted += changed; watcher.Changed += changed;
        watcher.Renamed += (_, a) => Dispatcher.BeginInvoke(RefreshSoon);
        watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { Watch(); RefreshSoon(); });
        watcher.EnableRaisingEvents = true;
    }

    void Window_Activated(object? s, EventArgs e)
    {
        if (CodeMode && root != null) RefreshSoon();
    }

    static bool Ignored(string path) => System.Text.RegularExpressions.Regex.IsMatch(path, @"[\\/](\.git|\.vs)([\\/]|$)");

    // Many changes at once (a build, an agent edit) refresh once.
    void RefreshSoon()
    {
        refreshSoon ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { refreshSoon!.Stop(); RefreshTree(); ReloadCleanTabs(); RefreshGit(); }, Dispatcher);
        refreshSoon.Stop(); refreshSoon.Start();
    }

    void RefreshTree()
    {
        FileTree.Items.Clear();
        if (root == null) return;
        foreach (var item in Children(root)) FileTree.Items.Add(item);
        PaintPicked();   // the picked ones stay picked through a refresh
    }

    // Folders first, then files; folders fill in when opened (and stay open across refreshes).
    List<TreeViewItem> Children(string dir)
    {
        var list = new List<TreeViewItem>();
        try
        {
            foreach (var d in Directory.GetDirectories(dir).Where(d => !Ignored(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var item = Item(d, "");
                if (expanded.Contains(d)) { foreach (var c in Children(d)) item.Items.Add(c); item.IsExpanded = true; }
                else item.Items.Add("…");
                item.Expanded += (s, e) =>
                {
                    if (e.OriginalSource != item) return;
                    expanded.Add(d);
                    if (item.Items.Count == 1 && item.Items[0] is string) { item.Items.Clear(); foreach (var c in Children(d)) item.Items.Add(c); }
                };
                item.Collapsed += (s, e) => { if (e.OriginalSource == item) expanded.Remove(d); };
                item.MouseRightButtonUp += (_, e) => { if (e.Handled) return; e.Handled = true; item.IsSelected = true; ExplorerMenu(d, e.GetPosition(this)); };
                list.Add(item);
            }
            foreach (var f in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var item = Item(f, "");
                item.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (Keyboard.Modifiers == ModifierKeys.None) OpenFileTab(f); };   // Ctrl/Shift+click only picks
                item.MouseRightButtonUp += (_, e) => { e.Handled = true; item.IsSelected = true; ExplorerMenu(f, e.GetPosition(this)); };
                list.Add(item);
            }
        }
        catch { }
        return list;
    }

    // File-type marks, coloured like a code editor's explorer: (text, is it an icon-font glyph, colour).
    static readonly Dictionary<string, (string Mark, bool Glyph, string Color)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = ("C#", false, "#68A0FF"), [".csx"] = ("C#", false, "#68A0FF"),
        [".json"] = ("{}", false, "#E6C04A"), [".jsonc"] = ("{}", false, "#E6C04A"),
        [".xaml"] = ("<>", false, "#F07D3A"), [".xml"] = ("<>", false, "#F07D3A"), [".csproj"] = ("<>", false, "#F07D3A"),
        [".props"] = ("<>", false, "#F07D3A"), [".targets"] = ("<>", false, "#F07D3A"), [".config"] = ("<>", false, "#F07D3A"),
        [".sln"] = ("VS", false, "#A47BE8"), [".slnx"] = ("VS", false, "#A47BE8"),
        [".md"] = ("M↓", false, "#5FA8F5"), [".js"] = ("JS", false, "#E8D44D"), [".mjs"] = ("JS", false, "#E8D44D"), [".jsx"] = ("JS", false, "#61DAFB"),
        [".ts"] = ("TS", false, "#3B82F6"), [".tsx"] = ("TS", false, "#61DAFB"), [".py"] = ("py", false, "#4B8BBE"),
        [".html"] = ("<>", false, "#E34C26"), [".htm"] = ("<>", false, "#E34C26"), [".css"] = ("#", false, "#2965F1"), [".scss"] = ("#", false, "#CD6799"),
        [".ps1"] = (">_", false, "#4EC9B0"), [".sh"] = (">_", false, "#4EC9B0"), [".bat"] = (">_", false, "#4EC9B0"), [".cmd"] = (">_", false, "#4EC9B0"),
        [".yml"] = ("!", false, "#CB4B4B"), [".yaml"] = ("!", false, "#CB4B4B"), [".sql"] = ("db", false, "#E0A64B"),
        [".go"] = ("go", false, "#00ADD8"), [".rs"] = ("rs", false, "#DEA584"), [".java"] = ("J", false, "#E76F00"), [".cpp"] = ("C+", false, "#659AD2"), [".c"] = ("C", false, "#659AD2"), [".h"] = ("h", false, "#A074C4"),
        [".png"] = ("", true, "#B07CD8"), [".jpg"] = ("", true, "#B07CD8"), [".jpeg"] = ("", true, "#B07CD8"), [".gif"] = ("", true, "#B07CD8"),
        [".ico"] = ("", true, "#B07CD8"), [".svg"] = ("", true, "#F2A33A"), [".webp"] = ("", true, "#B07CD8"),
        [".exe"] = ("", true, "#8E8E93"), [".dll"] = ("", true, "#8E8E93"), [".pdb"] = ("", true, "#8E8E93"),
        [".zip"] = ("", true, "#C9A44A"), [".pdf"] = ("", true, "#E5534B"),
    };

    static FrameworkElement FileIcon(string path, bool folder)
    {
        var t = new TextBlock { Width = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        if (folder || !Types.TryGetValue(Path.GetExtension(path), out var k))
        {
            // folders in a soft blue, like an editor's explorer; plain files quieter
            t.Text = folder ? "" : ""; t.FontFamily = App.IconFont; t.FontSize = 16;
            if (folder) t.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0xA8, 0xE6)); else t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            return t;
        }
        t.Text = k.Mark;
        t.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(k.Color));
        if (k.Glyph) { t.FontFamily = App.IconFont; t.FontSize = 16; }
        else { t.FontFamily = new FontFamily("Cascadia Code, Consolas"); t.FontSize = k.Mark.Length > 1 ? 11 : 13; t.FontWeight = FontWeights.Bold; }
        return t;
    }

    static TreeViewItem Item(string path, string glyph)
    {
        var icon = FileIcon(path, glyph == "");
        var name = new TextBlock { Text = Path.GetFileName(path), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Children = { icon, name } };
        var item = new TreeViewItem { Header = header, Tag = path, ToolTip = path, FocusVisualStyle = null };
        // folders: a click anywhere on the row opens or closes it, and never selects it (the arrow does the same itself)
        if (Directory.Exists(path))
            item.PreviewMouseLeftButtonDown += (_, e) =>   // before the row selects itself
            {
                if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject d) return;
                for (var x = d; x != null && x != item; x = VisualTreeHelper.GetParent(x))
                    if (x is System.Windows.Controls.Primitives.ToggleButton || x is TreeViewItem) return;   // the arrow, or a row inside it
                item.IsExpanded = !item.IsExpanded;
                e.Handled = true;
            };
        // and a folder never stays selected (the keyboard or a click on its arrow can select it): no fill, no box
        if (Directory.Exists(path))
            item.Selected += (_, e) => { if (e.OriginalSource == item) item.Dispatcher.BeginInvoke(() => item.IsSelected = false); };
        return item;
    }

    void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to work in" };
        if (root != null) dlg.InitialDirectory = root;
        if (dlg.ShowDialog(this) == true) active.UseWorkspace(dlg.FolderName);
    }
    void RefreshTree_Click(object s, RoutedEventArgs e) { RefreshTree(); ReloadCleanTabs(); }

    // ---------- editor ----------
    void OpenFileTab(string path)
    {
        var open = files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (open == null)
        {
            try { open = Load(path); }
            catch (Exception ex) { _ = Notice("Couldn't open it", ex.Message); return; }
            open.Tab = TabButton(open);
            // the file in the current tab is saved: show the new one in its place instead of adding a tab
            var at = current is { Dirty: false } ? files.IndexOf(current) : -1;
            if (at >= 0)
            {
                files[at] = open;
                Tabs.Children.RemoveAt(at); Tabs.Children.Insert(at, open.Tab);
            }
            else { files.Add(open); Tabs.Children.Add(open.Tab); }
        }
        Show(open);
    }

    Button TabButton(OpenFile f)
    {
        var name = new TextBlock { Text = Path.GetFileName(f.Path), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "", Style = (Style)FindResource("Ghost"), FontSize = 12, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Close" };
        close.Click += (_, e) => { e.Handled = true; CloseTab(f); };
        var row = new DockPanel { Children = { close, FileIcon(f.Path, false), name } };
        DockPanel.SetDock(close, Dock.Right);
        var tab = new Button { Style = (Style)FindResource("Plain"), Height = 34, Padding = new Thickness(10, 0, 4, 0), ToolTip = f.Path, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = row };
        tab.Click += (_, _) => Show(f);
        HoverOnly(tab, close);
        return tab;
    }

    static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff"];

    // Text files open for editing; images as a picture; anything else binary as a hex view (first 1 MB).
    static OpenFile Load(string path)
    {
        var size = new FileInfo(path).Length;
        string Size() => size >= 1 << 20 ? $"{size / 1048576.0:0.#} MB" : size >= 1024 ? $"{size / 1024.0:0.#} KB" : $"{size} bytes";
        if (ImageTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return new OpenFile(path) { Kind = "image", Info = $"image · {Size()}" };
        var head = new byte[(int)Math.Min(size, 1 << 20)];
        using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
        if (!head.Take(8000).Contains((byte)0))   // text: editable, or read-only (first 1 MB) when it's huge
            return size <= 2_000_000 ? new OpenFile(path) { Text = File.ReadAllText(path) }
                 : new OpenFile(path) { Kind = "binary", Text = Encoding.UTF8.GetString(head), Info = $"large text · {Size()} · read-only · first 1 MB shown" };
        return new OpenFile(path) { Kind = "binary", Text = Hex(head), Info = $"binary · {Size()} · read-only" + (size > head.Length ? " · first 1 MB shown" : "") };
    }

    // offset · 16 bytes in hex · the same bytes as text
    static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 4 + 64);
        for (int i = 0; i < bytes.Length; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
            {
                sb.Append(i + j < bytes.Length ? bytes[i + j].ToString("X2") + " " : "   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < bytes.Length; j++) sb.Append(bytes[i + j] is >= 32 and < 127 ? (char)bytes[i + j] : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    void OpenExternally_Click(object s, RoutedEventArgs e)
    {
        if (current != null) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(current.Path) { UseShellExecute = true }); } catch (Exception ex) { _ = Notice("Couldn't open it", ex.Message); }
    }

    // ---------- Markdown preview ----------
    bool mdPreview;
    System.Windows.Threading.DispatcherTimer? mdTimer;
    static bool IsMarkdown(OpenFile? f) => f is { Kind: "text" } && Path.GetExtension(f.Path).ToLowerInvariant() is ".md" or ".markdown" or ".mdx";

    void MdToggle_Click(object s, RoutedEventArgs e) { mdPreview = !mdPreview; UpdateMdPreview(); }

    void UpdateMdPreview()
    {
        var md = IsMarkdown(current);
        MdToggle.Visibility = md ? Visibility.Visible : Visibility.Collapsed;
        var on = md && mdPreview;
        MdPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MdCol.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        MdToggle.Content = on ? "" : "";
        if (on) RenderMdPreview();
        else MdPreview.Children.Clear();
    }

    void RenderMdPreview()
    {
        MdPreview.Children.Clear();
        if (current == null) return;
        foreach (var el in active.RenderMarkdown(current.Text, false)) MdPreview.Children.Add(el);   // the same renderer as replies
    }

    void MdTextChanged()   // re-rendered a moment after typing stops
    {
        if (!mdPreview || !IsMarkdown(current)) return;
        mdTimer ??= new(TimeSpan.FromMilliseconds(300), System.Windows.Threading.DispatcherPriority.Background, (_, _) => { mdTimer!.Stop(); RenderMdPreview(); }, Dispatcher);
        mdTimer.Stop(); mdTimer.Start();
    }

    void Show(OpenFile f)
    {
        current = f;
        UpdateMdPreview();
        BinaryView.Visibility = f.Kind == "text" ? Visibility.Collapsed : Visibility.Visible;
        OpenExternallyButton.Visibility = f.Kind == "diff" ? Visibility.Collapsed : Visibility.Visible;
        if (f.Kind != "text")
        {
            Editor.Visibility = Visibility.Collapsed; EditorEmpty.Visibility = Visibility.Collapsed;
            ImageView.Visibility = f.Kind == "image" ? Visibility.Visible : Visibility.Collapsed;
            HexView.Visibility = f.Kind is "binary" or "diff" ? Visibility.Visible : Visibility.Collapsed;
            HexView.SyntaxHighlighting = f.Kind == "diff" ? ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("Patch") : null;
            if (f.Kind == "image")
                try
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage();
                    img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.UriSource = new Uri(f.Path); img.EndInit();
                    ImagePreview.Source = img;
                    if (!f.Info.Contains('×')) f.Info = f.Info.Replace("image ·", $"image · {img.PixelWidth}×{img.PixelHeight} ·");
                }
                catch { ImagePreview.Source = null; f.Info = "image · couldn't be shown (use Open in its app)"; }
            else { HexView.Text = f.Text; HexView.ScrollToHome(); }
            Tabs.Reveal(f.Tab);
            foreach (var o in files)
            {
                if (o == f) o.Tab.SetResourceReference(ForegroundProperty, "Fg"); else o.Tab.ClearValue(ForegroundProperty);
                o.Tab.FontWeight = o == f ? FontWeights.SemiBold : FontWeights.Normal;
            }
            UpdateEditorStatus();
            return;
        }
        loadingEditor = true;
        Editor.SyntaxHighlighting = Highlight.ForFile(f.Path, f.Text);
        Editor.Text = f.Text;
        Editor.CaretOffset = 0; Editor.ScrollToHome();
        loadingEditor = false;
        Editor.Visibility = Visibility.Visible; EditorEmpty.Visibility = Visibility.Collapsed;
        Tabs.Reveal(f.Tab);
        foreach (var o in files)   // the open tab reads brighter and bolder
        {
            if (o == f) o.Tab.SetResourceReference(ForegroundProperty, "Fg"); else o.Tab.ClearValue(ForegroundProperty);
            o.Tab.FontWeight = o == f ? FontWeights.SemiBold : FontWeights.Normal;
        }
        UpdateEditorStatus();
        Editor.Focus();
    }

    void Editor_TextChanged(object? s, EventArgs e)
    {
        if (loadingEditor || current == null) return;
        current.Text = Editor.Text;
        MdTextChanged();
        if (!current.Dirty) { current.Dirty = true; UpdateEditorStatus(); }
    }

    void UpdateEditorStatus()
    {
        foreach (var f in files) ((TextBlock)((DockPanel)f.Tab.Content).Children[2]).Text = (f.Dirty ? "● " : "") + Path.GetFileName(f.Path);
        if (current == null) { EditorStatus.Text = ""; return; }
        var rel = root != null && current.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, current.Path) : current.Path;
        EditorStatus.Text = rel + (current.Kind != "text" ? "  ·  " + current.Info : current.Dirty ? "  ·  unsaved — Ctrl+S to save" : "  ·  saved");
    }

    void SaveCurrent()
    {
        if (current is not { Kind: "text" }) return;
        try { File.WriteAllText(current.Path, current.Text); current.Dirty = false; UpdateEditorStatus(); }
        catch (Exception ex) { _ = Notice("Couldn't save", ex.Message); }
    }

    async void CloseTab(OpenFile f)
    {
        if (f.Dirty && !await Confirm("Close without saving?", $"{Path.GetFileName(f.Path)} has unsaved changes.", "Close anyway", danger: true)) return;
        var i = files.IndexOf(f);
        files.Remove(f); Tabs.Children.Remove(f.Tab);
        if (current != f) return;
        current = null;
        if (files.Count > 0) Show(files[Math.Min(i, files.Count - 1)]);
        else { Editor.Visibility = BinaryView.Visibility = Visibility.Collapsed; EditorEmpty.Visibility = Visibility.Visible; UpdateEditorStatus(); UpdateMdPreview(); }
    }

    // A file changed on disk (the agent, a build): open tabs without unsaved edits show the new text.
    void ReloadCleanTabs()
    {
        foreach (var f in files.Where(f => !f.Dirty && f.Kind == "text").ToList())
        {
            try
            {
                if (!File.Exists(f.Path)) continue;
                var text = File.ReadAllText(f.Path);
                if (text == f.Text) continue;
                f.Text = text;
                if (f == current) { var caret = Editor.CaretOffset; loadingEditor = true; Editor.Text = text; Editor.CaretOffset = Math.Min(caret, text.Length); loadingEditor = false; }
            }
            catch { }
        }
    }

    // ---------- terminals: real terminals in tabs ----------
    // Each tab is a program in a Windows pseudo-console, drawn by xterm.js (the terminal VS Code uses) in a WebView2:
    // prompts, colours, Ctrl+C, tab completion and full-screen apps (vim, Claude Code, Copilot…) all work.
    // Mark, Color: the badge in the + menu and on the tab. Bg, Fg: the terminal's colours. Agent: an AI CLI.
    record Shell(string Name, string File, string[] Args, string Mark = ">_", string Color = "#8E8E93", string Bg = "#141414", string Fg = "#E6E6E6", bool Agent = false, string Scheme = "onehalf")
    {
        public static Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    class Term
    {
        public required Shell Shell;
        public required string Id, Cwd;   // Id: its place in the shared terminal page
        public Pty? Pty;
        public Button Tab = null!;
    }

    // One browser view draws every terminal tab (each is a separate terminal inside the page), not one per tab.
    Microsoft.Web.WebView2.Wpf.WebView2? termView;
    TaskCompletionSource? termReady;

    readonly List<Term> terms = [];
    Term? term;
    List<Shell>? shells;
    Microsoft.Web.WebView2.Core.CoreWebView2Environment? webEnv;

    // Badges for AI command-line tools, in each one's own colours.
    static readonly Dictionary<string, (string Mark, string Color, string Bg, string Fg)> AgentLooks = new()
    {
        ["claude"] = ("claude", "#D97757", "#262624", "#F5F4EE"), ["copilot"] = ("copilot", "#A371F7", "#0D1117", "#E6EDF3"),
        ["codex"] = ("◎", "#10A37F", "#0B0F0E", "#E6E6E6"), ["gemini"] = ("gemini", "#8AB4F8", "#131314", "#E3E3E3"),
        ["qwen"] = ("Q", "#7C6CF2", "#121218", "#E6E6E6"), ["cursor-agent"] = ("▲", "#EDEDED", "#0A0A0A", "#EDEDED"),
        ["opencode"] = ("◇", "#F5A742", "#121212", "#E6E6E6"), ["aider"] = ("a", "#14B014", "#101410", "#E6E6E6"),
        ["goose"] = ("g", "#F2C94C", "#151515", "#E6E6E6"), ["amp"] = ("⚡", "#F34E3F", "#141414", "#E6E6E6"),
        ["crush"] = ("♥", "#F7768E", "#16161E", "#E6E6E6"), ["llm"] = ("›", "#9ECE6A", "#141414", "#E6E6E6"),
    };

    // Every shell on this PC (Windows PowerShell, PowerShell 7, Command Prompt, Git Bash, each WSL distro), then the
    // AI CLIs installed on Windows or in WSL. Looked up once; started in the background when Code mode opens.
    List<Shell> Shells()
    {
        if (shells != null) return shells;
        var list = new List<Shell> { new("powershell", "powershell.exe", ["-NoLogo"], ">_", "#4FA3FF", "#012456", "#EEEDF0", Scheme: "campbell") };
        var pwsh = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "PowerShell", "7", "pwsh.exe")).FirstOrDefault(File.Exists);
        if (pwsh != null) list.Add(new("pwsh 7", pwsh, ["-NoLogo"], ">_", "#7FD0FF", "#0B1A2C", "#E8F1FA", Scheme: "campbell"));
        list.Add(new("cmd", "cmd.exe", [], @"C:\", "#CCCCCC", "#0C0C0C", "#CCCCCC", Scheme: "campbell"));
        var git = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(git)) list.Add(new("git bash", git, ["--login", "-i"], "$", "#F05033", "#1B1A19", "#E8E6E3", Scheme: "mintty"));
        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        distros = [];
        if (File.Exists(wsl))
        {
            distros = [.. Run(wsl, ["-l", "-q"], Encoding.Unicode).Split('\n').Select(n => n.Replace("\0", "").Trim())
                                .Where(n => n != "" && !n.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))];
            foreach (var d in distros)   // Ubuntu in its own orange on aubergine; other distros in terminal green
                list.Add(d.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase)
                    ? new(Short(d), wsl, ["-d", d, "--cd", "{cwd}"], "~$", "#E95420", "#300A24", "#F2EEF0", Scheme: "ubuntu")
                    : new(Short(d), wsl, ["-d", d, "--cd", "{cwd}"], "~$", "#8AE234", "#1D1F21", "#E6E6E6"));
        }
        // AI CLIs: Windows ones run directly; Linux-only ones (not links to the Windows copy) run in their distro
        foreach (var a in Cli.Adapters)
            if (Cli.Locate(a.Command) is { } loc) list.Add(AgentShell(a, a.Command, loc.File, loc.Prefix));
        foreach (var d in distros)
        {
            var found = Run(wsl, ["-d", d, "-e", "bash", "-lc", "for c in " + string.Join(" ", Cli.Adapters.Select(x => x.Command)) +
                                  "; do p=$(command -v $c 2>/dev/null) && r=$(readlink -f \"$p\") && case \"$r\" in /mnt/*|*.exe) ;; *) echo $c;; esac; done"], Encoding.UTF8);
            foreach (var c in found.Split('\n').Select(x => x.Trim()).Where(x => x != ""))
                if (Cli.Find(c) is { } a) list.Add(AgentShell(a, $"{c} · {Short(d)}", wsl, ["-d", d, "--cd", "{cwd}", "-e", "bash", "-lic", "exec " + c]));
        }
        // AI CLIs in a set order (claude, gemini, copilot first), shells as found
        string[] order = ["claude", "gemini", "copilot"];
        int Rank(Shell x) => Array.IndexOf(order, x.Name.Split(' ')[0]) is var i and >= 0 ? i : order.Length;
        return shells = [.. list.Where(x => !x.Agent), .. list.Where(x => x.Agent).OrderBy(Rank)];

        static Shell AgentShell(Cli.Adapter a, string name, string file, string[] args)
        {
            (string Mark, string Color, string Bg, string Fg) look = AgentLooks.TryGetValue(a.Command, out var l) ? l : ("›", "#9ECE6A", "#141414", "#E6E6E6");
            var scheme = a.Command switch { "claude" => "claude", "copilot" => "github", "gemini" => "google", _ => "onehalf" };
            return new(name, file, args, look.Mark, look.Color, look.Bg, look.Fg, Agent: true, Scheme: scheme);
        }
    }

    // "Ubuntu-26.04" → "ubuntu"
    // ("Ubuntu-22.04" and "Ubuntu-26.04" side by side keep their versions, so names stay unique)
    string Short(string distro) =>
        distros.Count(d => d.Split('-')[0].Equals(distro.Split('-')[0], StringComparison.OrdinalIgnoreCase)) > 1 ? distro.ToLowerInvariant() : distro.Split('-')[0].ToLowerInvariant();
    List<string> distros = [];

    static string Run(string file, string[] args, Encoding enc)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = enc };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            return p.WaitForExit(10_000) ? output.Result : "";
        }
        catch { return ""; }
    }

    async void NewTerm_Click(object s, RoutedEventArgs e)
    {
        NewTermButton.IsEnabled = false;
        var all = await Task.Run(Shells);
        NewTermButton.IsEnabled = true;
        // shells, a line, then AI CLIs; the menu opens upward so it isn't hidden behind the terminal
        var items = new List<(string, string, Action, bool)?>();
        foreach (var sh in all.Where(x => !x.Agent)) items.Add(("", sh.Name, () => OpenTerm(sh), false));
        if (all.Any(x => x.Agent)) items.Add(null);
        foreach (var sh in all.Where(x => x.Agent)) items.Add(("", sh.Name, () => OpenTerm(sh), false));
        ShowMenu(NewTermButton.TranslatePoint(new Point(0, 0), this), [.. items], all.ToDictionary(x => x.Name, x => (Func<FrameworkElement>)(() => Badge(x))), above: true);
    }

    async void OpenTerm(Shell sh)
    {
        var t = new Term { Shell = sh, Id = Guid.NewGuid().ToString("N"), Cwd = root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        t.Tab = TermTab(t);
        terms.Add(t); TermTabs.Children.Add(t.Tab);
        ShowTerm(t);
        try
        {
            await TermView();
            ToPage(new() { ["type"] = "create", ["id"] = t.Id, ["bg"] = sh.Bg, ["fg"] = sh.Fg, ["cursor"] = sh.Color, ["scheme"] = sh.Scheme });
        }
        catch (Exception ex)
        {
            if (termReady is { Task.IsCompletedSuccessfully: false }) { termReady = null; termView = null; }   // let the next terminal try again
            CloseTerm(t); _ = Notice("Couldn't open a terminal", ex.Message);
        }
    }

    // The shared terminal page, made the first time a terminal opens.
    async Task TermView()
    {
        if (termReady != null) { await termReady.Task; return; }
        termReady = new TaskCompletionSource();
        termView = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        if (term != null) TermHost.Child = termView;
        webEnv ??= await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Dir, "web"));
        await termView.EnsureCoreWebView2Async(webEnv);
        termView.ZoomFactor = S.Zoom;
        termView.CoreWebView2.SetVirtualHostNameToFolderMapping("ivo.term", Path.Combine(AppContext.BaseDirectory, "Assets", "term"),
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        termView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        termView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        termView.CoreWebView2.WebMessageReceived += (_, m) => FromTerminal(System.Text.Json.Nodes.JsonNode.Parse(m.WebMessageAsJson));
        termView.Source = new Uri("https://ivo.term/term.html");
        await termReady.Task;
    }

    void ToPage(System.Text.Json.Nodes.JsonObject m)
    {
        try { termView?.CoreWebView2?.PostWebMessageAsJson(m.ToJsonString()); } catch { }
    }

    // The page is ready; a terminal was created (start its program at that size); keystrokes; a resize.
    void FromTerminal(System.Text.Json.Nodes.JsonNode? m)
    {
        if ((string?)m?["type"] == "ready") { termReady?.TrySetResult(); return; }
        if (terms.FirstOrDefault(x => x.Id == (string?)m?["id"]) is not { } t) return;
        switch ((string?)m!["type"])
        {
            case "created" when t.Pty == null:
                try
                {
                    t.Pty = Pty.Start(t.Shell.File, t.Shell.Args.Select(a => a == "{cwd}" ? t.Cwd : a), t.Cwd, (int?)m["cols"] ?? 100, (int?)m["rows"] ?? 24);
                    _ = Pump(t, t.Pty);
                }
                catch (Exception ex) { Post(t, $"\u001b[31m{ex.Message}\u001b[0m\r\n"); }
                if (term != t) ToPage(new() { ["type"] = "show", ["id"] = term?.Id });   // another tab was picked meanwhile
                break;
            case "input": t.Pty?.Write((string?)m["data"] ?? ""); break;
            case "resize": t.Pty?.Resize((int?)m["cols"] ?? 100, (int?)m["rows"] ?? 24); break;
        }
    }

    // Program output → its terminal, decoded as UTF-8 across chunk boundaries.
    async Task Pump(Term t, Pty pty)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16384]; var chars = new char[16384 + 4];
        try
        {
            int n;
            while ((n = await Task.Run(() => pty.Output.Read(bytes, 0, bytes.Length))) > 0)
            {
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                Post(t, new string(chars, 0, c));
            }
        }
        catch { }
        if (terms.Contains(t)) Post(t, "\r\n\u001b[90m(ended · Stop starts it again)\u001b[0m\r\n");
    }

    void Post(Term t, string text) => ToPage(new() { ["type"] = "out", ["id"] = t.Id, ["data"] = text });

    // A shell's badge: its text mark in its colour, or a drawn icon (Copilot's goggled head, in Copilot purple).
    // Every badge sits in the same 22×16 box: text marks at one size, drawn icons at a matching 13 px.
    static FrameworkElement Badge(Shell sh)
    {
        FrameworkElement inner = sh.Mark switch
        {
            "copilot" => new Image { Source = CopilotIcon(sh.Color), Width = 13, Height = 13 },
            "claude" => new Image { Source = Burst(sh.Color), Width = 13, Height = 13 },
            "gemini" => new Image { Source = Sparkle(sh.Color), Width = 13, Height = 13 },
            _ => new TextBlock { Text = sh.Mark, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Shell.B(sh.Color) },
        };
        inner.HorizontalAlignment = HorizontalAlignment.Left; inner.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Width = 22, Height = 16, VerticalAlignment = VerticalAlignment.Center, Children = { inner } };
    }

    static ImageSource Drawn(params GeometryDrawing[] parts)
    {
        var g = new DrawingGroup();
        foreach (var d in parts) g.Children.Add(d);
        var img = new DrawingImage(g); img.Freeze();
        return img;
    }

    // Claude's starburst: rounded rays around a centre.
    static ImageSource Burst(string color)
    {
        var rays = string.Join(" ", Enumerable.Range(0, 10).Select(i =>
        {
            var a = i * Math.PI / 5; double c = Math.Cos(a), s = Math.Sin(a), len = i % 2 == 0 ? 10.5 : 8.5;
            return FormattableString.Invariant($"M{12 + c * 2:0.##},{12 + s * 2:0.##} L{12 + c * len:0.##},{12 + s * len:0.##}");
        }));
        return Drawn(new GeometryDrawing(null, new Pen(Shell.B(color), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geometry.Parse("M0,0 M24,24 " + rays)));
    }

    // Gemini's four-point sparkle.
    static ImageSource Sparkle(string color) =>
        Drawn(new GeometryDrawing(Shell.B(color), null, Geometry.Parse("M12,1 C12.8,7.5 16.5,11.2 23,12 C16.5,12.8 12.8,16.5 12,23 C11.2,16.5 7.5,12.8 1,12 C7.5,11.2 11.2,7.5 12,1 Z")));

    static ImageSource CopilotIcon(string color)
    {
        var fill = Shell.B(color); var dark = Shell.B("#1A1330");
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        void Add(Brush? b, Pen? p, string geometry) => g.Children.Add(new GeometryDrawing(b, p, Geometry.Parse(geometry)));
        Add(fill, null, "M2.5,10.5 a1.6,1.6 0 0 1 1.6,-1.6 h0.4 v7.4 h-0.4 a1.6,1.6 0 0 1 -1.6,-1.6 Z");                 // headphones
        Add(fill, null, "M21.5,10.5 a1.6,1.6 0 0 0 -1.6,-1.6 h-0.4 v7.4 h0.4 a1.6,1.6 0 0 0 1.6,-1.6 Z");
        Add(fill, null, "M5,11 C5,5.5 8,3.5 12,3.5 C16,3.5 19,5.5 19,11 V16.5 C19,19 17.5,20.5 15,20.5 H9 C6.5,20.5 5,19 5,16.5 Z");   // head
        var pen = new Pen(dark, 1.3);
        Add(null, pen, "M7.2,8.2 a2.3,2.3 0 1 0 4.6,0 a2.3,2.3 0 1 0 -4.6,0 Z M12.2,8.2 a2.3,2.3 0 1 0 4.6,0 a2.3,2.3 0 1 0 -4.6,0 Z M11.8,8.2 H12.2");   // goggles
        Add(dark, null, "M7.6,13.2 C7.6,12.2 8.3,11.6 9.3,11.6 H14.7 C15.7,11.6 16.4,12.2 16.4,13.2 V16.6 C16.4,17.8 15.7,18.5 14.5,18.5 H9.5 C8.3,18.5 7.6,17.8 7.6,16.6 Z");   // face
        Add(fill, null, "M9.9,13.6 h1.2 v2.6 h-1.2 Z M12.9,13.6 h1.2 v2.6 h-1.2 Z");   // eyes
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    Button TermTab(Term t)
    {
        // one snug row: icon · name (trimmed with "…" when short of room) · a small × that shows on hover
        var name = new TextBlock { Text = t.Shell.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "", Style = (Style)FindResource("Plain"), FontFamily = App.IconFont, FontSize = 12,
                                 Height = 20, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(1, 0, 0, 0), ToolTip = "Close this terminal", Visibility = Visibility.Collapsed };
        close.Click += (_, e) => { e.Handled = true; CloseTerm(t); };
        var icon = Badge(t.Shell);
        icon.Width = 18; icon.Margin = new Thickness(0, 0, 4, 0);
        // the × follows right after the name; the name only trims when it and the × don't both fit
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name, close } };
        var tab = new Button { Style = (Style)FindResource("Plain"), Height = 30, Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(6, 0, 2, 0),
                               ToolTip = t.Shell.Name, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = row };
        void Fit() => name.MaxWidth = Math.Max(0, tab.ActualWidth - 8 - icon.Width - icon.Margin.Right - (close.Visibility == Visibility.Visible ? 22 : 0));
        tab.SizeChanged += (_, _) => Fit();
        tab.Click += (_, _) => ShowTerm(t);
        tab.MouseEnter += (_, _) => { close.Visibility = Visibility.Visible; Fit(); };
        tab.MouseLeave += (_, _) => { close.Visibility = Visibility.Collapsed; Fit(); };
        return tab;
    }

    void ShowTerm(Term? t)
    {
        term = t;
        TermHost.Child = t == null ? null : termView;
        foreach (var o in terms)
        {
            if (o == t) o.Tab.SetResourceReference(ForegroundProperty, "Fg"); else o.Tab.ClearValue(ForegroundProperty);
            o.Tab.FontWeight = o == t ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (t == null)
        {
            TermPanel.SetResourceReference(Border.BackgroundProperty, "CodeBg");
            TermHost.Child = new TextBlock { Text = "No terminal open · + to start one", Foreground = (Brush)FindResource("Dim"), FontSize = 12, Margin = new Thickness(14, 8, 0, 0) };
            return;
        }
        TermPanel.Background = Shell.B(t.Shell.Bg);   // the open terminal's own colours
        t.Tab.BringIntoView();
        termView?.Focus();
        ToPage(new() { ["type"] = "show", ["id"] = t.Id });
    }

    void CloseTerm(Term t)
    {
        t.Pty?.Dispose();
        ToPage(new() { ["type"] = "close", ["id"] = t.Id });
        var i = terms.IndexOf(t);
        terms.Remove(t); TermTabs.Children.Remove(t.Tab);
        if (term == t) ShowTerm(terms.Count > 0 ? terms[Math.Min(i, terms.Count - 1)] : null);
        if (terms.Count == 0) ReleaseTermView();
    }

    // No terminals left: the page that draws them and its Edge processes (~300 MB) go; + starts them again.
    void ReleaseTermView()
    {
        if (TermHost.Child == termView) TermHost.Child = null;
        termView?.Dispose(); termView = null; termReady = null;
    }

    public void CloseAllTerms() { foreach (var t in terms.ToList()) t.Pty?.Dispose(); }

    // Stop: Ctrl+C to the program; if it has ended, start it again in the same tab.
    void TermStop_Click(object s, RoutedEventArgs e)
    {
        if (term is not { } t) return;
        if (t.Pty != null && !t.Pty.Exited.IsCompleted) { t.Pty.Write("\u0003"); return; }
        var at = terms.IndexOf(t);
        CloseTerm(t);
        OpenTerm(t.Shell);
        var fresh = terms[^1];
        terms.Remove(fresh); terms.Insert(at, fresh);
        TermTabs.Children.Remove(fresh.Tab); TermTabs.Children.Insert(at, fresh.Tab);
    }

    // A focused terminal is its own window and takes the mouse wheel; while the pointer is over a tab list, the list
    // holds focus so the wheel scrolls it, and the terminal gets focus back when the pointer leaves.
    UIElement? gaveFocus;
    void TabList_MouseEnter(object s, MouseEventArgs e)
    {
        if (Keyboard.FocusedElement is not (Microsoft.Web.WebView2.Wpf.WebView2 or null)) return;   // only when a terminal (or nothing) has it
        gaveFocus = Keyboard.FocusedElement as UIElement ?? termView;
        ((UIElement)s).Focus();
    }
    void TabList_MouseLeave(object s, MouseEventArgs e)
    {
        if (gaveFocus == null) return;
        if (((UIElement)s).IsKeyboardFocused) gaveFocus.Focus();
        gaveFocus = null;
    }

    // A tab's × shows only while the pointer is over the tab; otherwise the name has the room.
    static void HoverOnly(Button tab, Button close)
    {
        close.Visibility = Visibility.Collapsed;
        tab.MouseEnter += (_, _) => close.Visibility = Visibility.Visible;
        tab.MouseLeave += (_, _) => close.Visibility = Visibility.Collapsed;
    }

    void TermClear_Click(object s, RoutedEventArgs e) { if (term != null) ToPage(new() { ["type"] = "clear", ["id"] = term.Id }); }

    // A WebView is its own window and would sit on top of Settings, dialogs and popups; hide it while one is open.
    void HideTermsUnderOverlays()
    {
        // every overlay: Settings, dialogs, the Inbox and its reader, the planner, quick-open, menus, the welcome
        UIElement[] layers = [SettingsPanel, DialogLayer, WebLayer, WelcomeLayer, WheelLayer, ReaderLayer, ScheduleLayer, QuickLayer, MenuLayer, SpotifyLayer];
        void Update() => TermHost.Visibility = layers.Any(l => l.IsVisible) ? Visibility.Hidden : Visibility.Visible;
        foreach (var l in layers) l.IsVisibleChanged += (_, _) => Update();
    }

}
