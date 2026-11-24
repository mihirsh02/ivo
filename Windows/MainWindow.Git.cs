using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Code mode's Source Control, laid out like VS Code's: every repository in a folder (the open one, or one you pick) as a
// collapsible section with its branch and change count, its own commit box (Commit, Commit & Push), and its changed files in
// two lists, Staged and Changes, with + / − to move files between them (click a file for its diff).
// Runs the git installed on this PC.
public partial class MainWindow
{
    static async Task<(int Code, string Out)> Git(string folder, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                                                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=false");   // names with non-ASCII letters as they are
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (await output) + (await errors));
        }
        catch (Exception ex) { return (-1, ex.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ? "Git isn't installed on this PC." : ex.Message); }
    }

    string? scanRoot;                                   // the folder scanned for repositories
    bool scannedOpen;                                   // that folder is the open one (follows it), not one picked with the folder button
    List<string> repos = [];
    readonly HashSet<string> collapsedRepos = [];
    readonly Dictionary<string, string> commitMessages = [];   // kept while the view redraws
    int gitDraw;

    // Repositories: the folder itself and any folder below it (4 levels, skipping dependency/build folders) with a .git
    static List<string> FindRepos(string folder)
    {
        var found = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", ".vs", "packages", "vendor", "dist", "build", "target", "publish" };
        void Walk(string d, int depth)
        {
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) found.Add(d);
            if (depth == 4) return;
            try { foreach (var c in Directory.EnumerateDirectories(d)) { var n = Path.GetFileName(c); if (!n.StartsWith('.') && !skip.Contains(n)) Walk(c, depth + 1); } } catch { }
        }
        Walk(folder, 0);
        return found;
    }

    async void ScanRepos(string folder)
    {
        scanRoot = folder;
        GitTitle.Text = "SOURCE CONTROL · " + Path.GetFileName(folder.TrimEnd('\\')).ToUpperInvariant();
        repos = await Task.Run(() => FindRepos(folder));
        RefreshGit();
    }

    void GitScanFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to scan for repositories" };
        if (scanRoot != null) dlg.InitialDirectory = scanRoot;
        if (dlg.ShowDialog(this) == true) { scannedOpen = false; ScanRepos(dlg.FolderName); }
    }

    void GitRefresh_Click(object s, RoutedEventArgs e) { if (scanRoot != null) ScanRepos(scanRoot); else RefreshGit(); }

    async void RefreshGit()
    {
        if (!GitView.IsVisible) return;
        if (root != null && (scanRoot == null || scannedOpen && scanRoot != root)) { scannedOpen = true; ScanRepos(root); return; }   // the open folder (again, if it changed)
        if (scanRoot == null) { ShowGitEmpty("Open or scan a folder."); return; }
        if (GitRepos.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox) return;   // don't redraw under someone typing a message
        var me = ++gitDraw;
        if (repos.Count == 0)
        {
            ShowGitEmpty("No repositories in this folder.");
            var init = new Button { Content = "Initialize repository", Style = (Style)FindResource("Shimmer"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(12, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            var folder = scanRoot;
            init.Click += async (_, _) => { var (code, output) = await Git(folder, "init"); if (code != 0) _ = Notice("Couldn't create a repository", output); else ScanRepos(folder); };
            GitRepos.Children.Add(init);
            GitToggle.Content = "Git";
            return;
        }
        var sections = await Task.WhenAll(repos.Select(RepoSection));
        if (me != gitDraw) return;
        GitRepos.Children.Clear();
        foreach (var (section, _) in sections) GitRepos.Children.Add(section);
        var total = sections.Sum(x => x.Changes);
        GitToggle.Content = "Git";   // the counts show on each repository, not on the switch
    }

    void ShowGitEmpty(string text)
    {
        GitRepos.Children.Clear();
        GitRepos.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(12, 6, 8, 0), TextWrapping = TextWrapping.Wrap });
    }

    static TextBlock DimText(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    // The repository menu (right-click): switch or make a branch, pull / push / fetch, stash, undo the last commit, discard.
    ContextMenu RepoMenu(string repo, string branch)
    {
        var menu = new ContextMenu();
        MenuItem Item(string glyph, string text, Func<Task> run, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled, Icon = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 15 } };
            mi.Click += async (_, _) => await run();
            return mi;
        }
        // run git; on failure show what it said; then redraw the panel
        async Task Run(string what, params string[] args)
        {
            var (code, output) = await Git(repo, args);
            if (code != 0) await Notice(what + " didn't work", output.Trim().Length > 0 ? output.Trim() : "Git gave no reason.");
            RefreshGit(); RefreshTree();
        }

        // Switch branch: the local branches (the current one ticked), then a new one from here
        var switchTo = new MenuItem { Header = "Switch branch", Icon = new TextBlock { Text = "\uE8AB", FontFamily = App.IconFont, FontSize = 15 } };
        switchTo.Items.Add(new MenuItem { Header = "Loading…", IsEnabled = false });
        switchTo.SubmenuOpened += async (_, e) =>
        {
            if (e.OriginalSource != switchTo) return;
            var (_, list) = await Git(repo, "branch", "--format=%(refname:short)");
            switchTo.Items.Clear();
            foreach (var b in list.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                var name = b;
                var mi = new MenuItem { Header = name, IsChecked = name == branch, IsEnabled = name != branch };
                mi.Click += async (_, _) => await Run("Switching to " + name, "switch", name);
                switchTo.Items.Add(mi);
            }
            switchTo.Items.Add(new Separator());
            var make = new MenuItem { Header = "New branch from here…" };
            make.Click += async (_, _) =>
            {
                if (await AskText("New branch", $"Starts from \"{branch}\"; your changes come along.", "", "Create") is { Length: > 0 } name)
                    await Run("Making the branch", "switch", "-c", name.Trim().Replace(' ', '-'));
            };
            switchTo.Items.Add(make);
        };
        menu.Items.Add(switchTo);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("\uE896", "Pull", () => Run("Pull", "pull", "--ff-only")));
        menu.Items.Add(Item("\uE898", "Push", () => Run("Push", "push", "-u", "origin", branch)));
        menu.Items.Add(Item("\uE895", "Fetch", () => Run("Fetch", "fetch", "--all", "--prune")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("\uE7B8", "Stash changes", () => Run("Stash", "stash", "push", "-u")));
        menu.Items.Add(Item("\uE777", "Bring back stashed changes", () => Run("Bringing back the stash", "stash", "pop")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("\uE7A7", "Undo last commit (keep its changes)", async () =>
        {
            if (await Confirm("Undo the last commit?", "Its changes come back as uncommitted changes, so nothing is lost. Don't do this to a commit you've already pushed.", "Undo commit"))
                await Run("Undoing the commit", "reset", "--soft", "HEAD~1");
        }));
        menu.Items.Add(Item("\uE74D", "Discard all changes…", async () =>
        {
            if (await Confirm("Discard every change?", "All uncommitted changes in this repository are thrown away, and new files deleted. This can't be undone.", "Discard all", danger: true))
            {
                await Git(repo, "reset", "--hard");
                await Run("Discarding", "clean", "-fd");
            }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("\uE8C8", "Copy branch name", () => { Clipboard.SetText(branch); return Task.CompletedTask; }));
        menu.Items.Add(Item("\uE838", "Open in File Explorer", () => { System.Diagnostics.Process.Start("explorer.exe", repo); return Task.CompletedTask; }));
        return menu;
    }

    // One repository: header (chevron, name, branch, change count), then the commit box and its changes
    async Task<(FrameworkElement Section, int Changes)> RepoSection(string repo)
    {
        var (_, branchOut) = await Git(repo, "rev-parse", "--abbrev-ref", "HEAD");
        var branch = branchOut.Trim().Split('\n')[0];
        if (branch.StartsWith("fatal")) branch = "no commits yet";
        var (_, status) = await Git(repo, "status", "--porcelain=v1", "-uall");
        var rows = status.Split('\n').Where(l => l.Length > 3).ToList();
        var open = !collapsedRepos.Contains(repo);

        var rel = Path.GetRelativePath(scanRoot!, repo);
        var name = new TextBlock { Text = rel == "." ? Path.GetFileName(repo.TrimEnd('\\')) : rel, FontSize = 14, FontWeight = FontWeights.SemiBold,
                                   VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var chevron = new TextBlock { Text = open ? "" : "", FontFamily = App.IconFont, FontSize = 14, Width = 18, VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(4, 8, 8, 2), ToolTip = repo };
        if (rows.Count > 0)
        {
            var count = new TextBlock { Text = rows.Count.ToString(), FontSize = 12.5 };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                     Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x90)), Child = count };
            DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge);
        }
        DockPanel.SetDock(chevron, Dock.Left); DockPanel.SetDock(name, Dock.Left);
        head.Children.Add(chevron); head.Children.Add(name); head.Children.Add(DimText("  " + branch));
        head.MouseLeftButtonUp += (_, _) => { if (!collapsedRepos.Remove(repo)) collapsedRepos.Add(repo); RefreshGit(); };

        var section = new StackPanel { Background = Brushes.Transparent, Children = { head } };
        // right-click anywhere on a repository: branches, sync, stash and undo
        section.MouseRightButtonUp += (_, e) => { e.Handled = true; RepoMenu(repo, branch).IsOpen = true; };
        if (!open) return (section, rows.Count);

        // commit box: message, then Commit (Ctrl+Enter)
        var message = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 32, MaxHeight = 110, FontSize = 12,
                                    Text = commitMessages.GetValueOrDefault(repo, ""), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var hint = DimText($"Message (Ctrl+Enter to commit on \"{branch}\")", 12);
        hint.IsHitTestVisible = false; hint.Margin = new Thickness(8, 7, 0, 0); hint.VerticalAlignment = VerticalAlignment.Top; hint.Opacity = 0.8;
        hint.Visibility = message.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        message.TextChanged += (_, _) => { commitMessages[repo] = message.Text; hint.Visibility = message.Text == "" ? Visibility.Visible : Visibility.Collapsed; };
        var note = DimText("");
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(2, 4, 0, 0);
        // staged (X, the index) and not yet staged (Y, the working tree, and new files): one file can be in both
        var staged = rows.Where(l => l[0] is not ' ' and not '?').ToList();
        var unstaged = rows.Where(l => l[1] != ' ' || l[0] == '?').ToList();
        var fresh = branch == "no commits yet";   // no HEAD yet: unstaging works differently
        // nothing staged: Commit takes everything (like VS Code's smart commit); otherwise only what's staged
        var what = staged.Count > 0 ? "Commit" : "Commit all";
        Button Action(string glyph, string text, string tip) => new()
        {
            Style = (Style)FindResource("Shimmer"), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = rows.Count > 0, ToolTip = tip,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) }, new TextBlock { Text = text } } },
        };
        var commit = Action("\uE73E", what, staged.Count > 0 ? "Commit the staged changes" : "Nothing is staged: commit every change");
        var commitPush = Action("\uE898", staged.Count > 0 ? "Commit & push" : "Push all",   // short enough for half the width
            staged.Count > 0 ? "Commit the staged changes, then push them to the remote" : "Commit every change, then push it to the remote");
        commitPush.Margin = new Thickness(6, 0, 0, 0);
        var buttons = new Grid { Margin = new Thickness(0, 6, 0, 0), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() } };
        Grid.SetColumn(commitPush, 1);
        buttons.Children.Add(commit); buttons.Children.Add(commitPush);
        async void Commit(bool push)
        {
            if (message.Text.Trim() == "") { note.Text = "Write a commit message first."; message.Focus(); return; }
            commit.IsEnabled = commitPush.IsEnabled = false;
            if (staged.Count == 0) await Git(repo, "add", "-A");
            var (code, output) = await Git(repo, "commit", "-m", message.Text.Trim());
            if (code != 0) { note.Text = output.Trim().Split('\n').LastOrDefault() ?? "Commit failed."; commit.IsEnabled = commitPush.IsEnabled = true; return; }
            commitMessages.Remove(repo); Keyboard.ClearFocus();
            if (push)
            {
                note.Text = "Pushing…";
                var (pc, po) = await Git(repo, "push");
                if (pc != 0 && po.Contains("no upstream", StringComparison.OrdinalIgnoreCase))   // a new branch: push it and track it
                {
                    var (_, cur) = await Git(repo, "rev-parse", "--abbrev-ref", "HEAD");
                    (pc, po) = await Git(repo, "push", "-u", "origin", cur.Trim());
                }
                if (pc != 0) { _ = Notice("Committed, but the push failed", po.Trim()); }
            }
            RefreshGit();
        }
        commit.Click += (_, _) => Commit(false);
        commitPush.Click += (_, _) => Commit(true);
        message.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Commit(false); } };
        section.Children.Add(new StackPanel { Margin = new Thickness(20, 4, 8, 6), Children = { new Grid { Children = { message, hint } }, buttons, note } });

        if (rows.Count == 0) { var none = DimText("No changes"); none.Margin = new Thickness(22, 0, 0, 6); section.Children.Add(none); return (section, 0); }

        async Task Stage(params string[] paths) { await Git(repo, ["add", "--", .. paths]); RefreshGit(); }
        async Task Unstage(params string[] paths)
        {
            if (fresh) await Git(repo, ["rm", "--cached", "-r", "-q", "--", .. paths]);
            else await Git(repo, ["restore", "--staged", "--", .. paths]);
            RefreshGit();
        }
        static string PathOf(string line) { var path = line[3..].Trim().Trim('"'); return path.Contains(" -> ") ? path[(path.IndexOf(" -> ") + 4)..] : path; }   // renamed: the new name

        // a list's heading, with "+" (stage all) or "−" (unstage all) on the right
        void Heading(string title, int count, string glyph, string tip, Func<Task> all)
        {
            var bar = new DockPanel { Margin = new Thickness(22, 4, 6, 2) };
            var button = ChatPane.SmallButton(glyph, tip);
            button.Click += async (_, _) => await all();
            DockPanel.SetDock(button, Dock.Right); bar.Children.Add(button);
            var text = DimText($"{title}  {count}"); text.FontWeight = FontWeights.SemiBold;
            bar.Children.Add(text);
            section.Children.Add(bar);
        }

        // changes: file icon, name, folder, the status letter on the right, and + / − to move it between the lists
        void FileRow(string line, bool inIndex)
        {
            var code = inIndex ? line[0] : line[1] == ' ' ? line[0] : line[1];
            var path = PathOf(line);
            var (letter, color) = line.StartsWith("??") ? ("U", "#3FB950") : code == 'D' ? ("D", "#F85149") : code == 'A' ? ("A", "#3FB950") : code == 'R' ? ("R", "#58A6FF") : ("M", "#E3B341");
            var mark = new TextBlock { Text = letter, Width = 18, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Shell.B(color), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var file = new TextBlock { Text = Path.GetFileName(path), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                       TextDecorations = letter == "D" ? TextDecorations.Strikethrough : null, Foreground = Shell.B(color) };
            var move = ChatPane.SmallButton(inIndex ? "\uE738" : "\uE710", inIndex ? "Unstage" : "Stage");
            move.Visibility = Visibility.Hidden;   // shows on hover, like VS Code
            move.Click += async (_, e) => { e.Handled = true; if (inIndex) await Unstage(path); else await Stage(path); };
            var row = new DockPanel();
            DockPanel.SetDock(mark, Dock.Right); DockPanel.SetDock(move, Dock.Right);
            row.Children.Add(mark); row.Children.Add(move); row.Children.Add(FileIcon(path, false)); row.Children.Add(file); row.Children.Add(DimText("  " + Path.GetDirectoryName(path)));
            var item = new Border { Child = row, Padding = new Thickness(22, 4, 6, 4), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, ToolTip = path, Background = Brushes.Transparent };
            item.MouseEnter += (_, _) => { item.Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x90)); move.Visibility = Visibility.Visible; };
            item.MouseLeave += (_, _) => { item.Background = Brushes.Transparent; move.Visibility = Visibility.Hidden; };
            item.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && IsInside(d, move)) return; OpenDiff(repo, path, letter, inIndex); };
            section.Children.Add(item);
        }

        if (staged.Count > 0)
        {
            Heading("Staged changes", staged.Count, "\uE738", "Unstage all", () => Unstage(staged.Select(PathOf).ToArray()));
            foreach (var line in staged) FileRow(line, true);
        }
        if (unstaged.Count > 0)
        {
            Heading("Changes", unstaged.Count, "\uE710", "Stage all", () => Stage("."));
            foreach (var line in unstaged) FileRow(line, false);
        }
        return (section, rows.Count);
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x)) if (x == parent) return true;   // Runs aren't visuals
        return false;
    }

    // A change as a read-only diff tab (new files show as all-added).
    async void OpenDiff(string repo, string path, string letter, bool staged = false)
    {
        var (_, diff) = letter == "U" ? await Git(repo, "diff", "--no-index", "--", "/dev/null", path)
                      : staged ? await Git(repo, "diff", "--cached", "--", path)   // what's staged
                      : await Git(repo, "diff", "--", path);                       // what isn't staged yet
        var f = new OpenFile(Path.Combine(repo, path) + " (changes)") { Kind = "diff", Text = diff.Trim() == "" ? "(no differences)" : diff,
                                                                         Info = $"changes · {letter switch { "U" => "new file", "D" => "deleted", "A" => "added", "R" => "renamed", _ => "modified" }} · read-only" };
        f.Tab = TabButton(f);
        foreach (var old in files.Where(x => x.Path == f.Path).ToList()) { files.Remove(old); Tabs.Children.Remove(old.Tab); }   // one diff tab per file
        files.Add(f); Tabs.Children.Add(f.Tab);
        Show(f);
    }

    // What a CLI agent is running, in full, as a read-only tab. It keeps updating only while it's the tab in view.
    const string StepTab = "Agent step";
    public void ShowStep(string text, bool open)
    {
        if (!CodeMode) return;
        var f = files.FirstOrDefault(x => x.Path == StepTab);
        if (f == null)
        {
            if (!open) return;
            f = new OpenFile(StepTab) { Kind = "diff", Info = "what the agent is running · read-only" };
            f.Tab = TabButton(f);
            files.Add(f); Tabs.Children.Add(f.Tab);
        }
        else if (!open && current != f) return;
        f.Text = text;
        Show(f);
        HexView.SyntaxHighlighting = null;
        HexView.ScrollToEnd();
    }
}
