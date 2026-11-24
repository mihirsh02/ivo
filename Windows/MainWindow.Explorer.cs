using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Ivo;

// Code mode's explorer: right-click a file or folder (or the empty space below) for New file / New folder, Rename (F2),
// Delete (Del, to the Recycle Bin), Copy path, Copy relative path, Reveal in File Explorer and Add to chat.
public partial class MainWindow
{
    void ExplorerMenu(string path, Point at)
    {
        var folder = Directory.Exists(path);
        var dir = folder ? path : Path.GetDirectoryName(path)!;
        var isRoot = string.Equals(path.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        ShowMenu(at,
        [
            ("", "New file…", () => NewEntry(dir, folder: false), false),
            ("", "New folder…", () => NewEntry(dir, folder: true), false),
            null,
            .. folder ? new (string, string, Action, bool)?[0] : [("", "Add to chat", () => { active.AttachFiles([path]); active.FocusInput(); }, false)],
            ("", "Reveal in File Explorer", () => System.Diagnostics.Process.Start("explorer.exe", folder ? $"\"{path}\"" : $"/select,\"{path}\""), false),
            ("", "Copy path", () => Clipboard.SetText(path), false),
            .. root != null && !isRoot ? [("", "Copy relative path", () => Clipboard.SetText(Path.GetRelativePath(root, path)), false)] : new (string, string, Action, bool)?[0],
            .. isRoot ? new (string, string, Action, bool)?[0] : [null, ("", "Rename…", () => RenameEntry(path), false), ("", "Delete", () => DeleteEntries(picked.Count > 1 && picked.Contains(path) ? [.. picked] : [path]), true)],
        ]);
    }

    void FileTree_RightClick(object s, MouseButtonEventArgs e)
    {
        if (e.Handled || root == null) return;   // an item handled it; this is the empty space: the folder itself
        e.Handled = true;
        ExplorerMenu(root, e.GetPosition(this));
    }

    void FileTree_KeyDown(object s, KeyEventArgs e)
    {
        if (FileTree.SelectedItem is not TreeViewItem { Tag: string path }) return;
        if (e.Key == Key.F2) { e.Handled = true; RenameEntry(path); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteEntries(picked.Count > 1 ? [.. picked] : [path]); }
    }

    // ---------- several at once, and drag and drop ----------
    // Click picks one (and opens a file); Ctrl+click adds or removes; Shift+click picks the run from the last click.
    // Drag the picked items onto a folder to move them (hold Ctrl to copy), or out to File Explorer, mail or a chat app.
    // Files dropped in from outside are copied (hold Shift to move). Name clashes get Windows' own replace/skip dialog.
    readonly List<string> picked = [];
    string? anchor, dragFrom, dropHint;
    Point dragStart;

    static TreeViewItem? ItemAt(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton) return null;   // the expand arrow
            if (d is TreeViewItem i) return i;
        }
        return null;
    }

    IEnumerable<TreeViewItem> Shown(ItemsControl parent)
    {
        foreach (var i in parent.Items.OfType<TreeViewItem>())
        {
            yield return i;
            if (i.IsExpanded) foreach (var c in Shown(i)) yield return c;
        }
    }

    void PaintPicked()
    {
        var pick = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x7C, 0x5C, 0xFF));
        var drop = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x70, 0x7C, 0x5C, 0xFF));
        foreach (var i in Shown(FileTree))
            if (i.Header is Panel h)
                h.Background = (string)i.Tag == dropHint ? drop : picked.Contains((string)i.Tag) ? pick : System.Windows.Media.Brushes.Transparent;
    }

    void FileTree_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is not { Tag: string path }) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) { if (!picked.Remove(path)) picked.Add(path); anchor = path; }
        else if (mods.HasFlag(ModifierKeys.Shift) && anchor != null)
        {
            var order = Shown(FileTree).Select(i => (string)i.Tag).ToList();
            int a = order.IndexOf(anchor), b = order.IndexOf(path);
            if (a >= 0 && b >= 0) { picked.Clear(); picked.AddRange(order.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1)); }
        }
        else
        {
            if (!picked.Contains(path)) { picked.Clear(); picked.Add(path); }   // a picked one stays with the others, ready to drag
            anchor = path;
            dragFrom = path; dragStart = e.GetPosition(FileTree);
        }
        PaintPicked();
    }

    void FileTree_PreviewMouseUp(object s, MouseButtonEventArgs e)
    {
        // a plain click (no drag) on one of several picked ones: just that one
        if (dragFrom != null && Keyboard.Modifiers == ModifierKeys.None && picked.Count > 1) { picked.Clear(); picked.Add(dragFrom); PaintPicked(); }
        dragFrom = null;
    }

    void FileTree_PreviewMouseMove(object s, MouseEventArgs e)
    {
        if (dragFrom == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(FileTree) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = picked.Contains(dragFrom) ? picked.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray() : [dragFrom];
        dragFrom = null;
        if (paths.Length == 0 || paths.Any(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase))) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetData("ivo-tree", true);
        DragDrop.DoDragDrop(FileTree, data, DragDropEffects.Move | DragDropEffects.Copy);
        dropHint = null; PaintPicked();
        RefreshSoon();   // dragged out and moved by File Explorer: the tree catches up
    }

    // where a drop lands: the folder under the mouse, a file's folder, or the open folder itself
    string? DropDir(object source) =>
        ItemAt(source) is { Tag: string p } ? (Directory.Exists(p) ? p : Path.GetDirectoryName(p)) : root;

    DragDropEffects DropEffect(DragEventArgs e, string? dir)
    {
        if (dir == null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return DragDropEffects.None;
        // not into itself or a folder inside it
        if (paths.Any(p => Directory.Exists(p) && (dir + "\\").StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return DragDropEffects.None;
        var ours = e.Data.GetDataPresent("ivo-tree");
        var copy = ours ? e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) : !e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        return copy ? DragDropEffects.Copy : DragDropEffects.Move;
    }

    void FileTree_DragOver(object s, DragEventArgs e)
    {
        var dir = DropDir(e.OriginalSource);
        e.Effects = DropEffect(e, dir);
        e.Handled = true;
        var hint = e.Effects == DragDropEffects.None ? null : ItemAt(e.OriginalSource) is { Tag: string p } && Directory.Exists(p) ? p : dir;
        if (hint != dropHint) { dropHint = hint; PaintPicked(); }
    }

    void FileTree_DragLeave(object s, DragEventArgs e) { dropHint = null; PaintPicked(); }

    async void FileTree_Drop(object s, DragEventArgs e)
    {
        e.Handled = true;
        dropHint = null;
        var dir = DropDir(e.OriginalSource);
        var effect = DropEffect(e, dir);
        if (effect == DragDropEffects.None || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) { PaintPicked(); return; }
        e.Effects = effect;
        var move = effect == DragDropEffects.Move;
        var ui = Microsoft.VisualBasic.FileIO.UIOption.AllDialogs;   // Windows' progress and replace/skip dialogs
        try
        {
            foreach (var p in paths)
            {
                var target = Path.Combine(dir!, Path.GetFileName(p.TrimEnd('\\')));
                if (string.Equals(target, p, StringComparison.OrdinalIgnoreCase)) continue;   // already there
                if (Directory.Exists(p))
                {
                    if (move) Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(p, target, ui); else Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(p, target, ui);
                }
                else if (move) Microsoft.VisualBasic.FileIO.FileSystem.MoveFile(p, target, ui);
                else Microsoft.VisualBasic.FileIO.FileSystem.CopyFile(p, target, ui);
                if (move) Retarget(p, target);
            }
        }
        catch (OperationCanceledException) { }   // cancelled in Windows' dialog
        catch (Exception ex) { await Notice(move ? "Couldn't move it" : "Couldn't copy it", ex.Message); }
        expanded.Add(dir!);
        picked.Clear();
        RefreshTree();
    }

    // open tabs of a moved file (or anything inside a moved folder) follow it
    void Retarget(string from, string to)
    {
        from = from.TrimEnd('\\');
        foreach (var f in files.Where(f => f.Path.Equals(from, StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            if (!f.Dirty) { var moved = to + f.Path[from.Length..]; CloseTab(f); OpenFileTab(moved); }
    }

    async void NewEntry(string dir, bool folder)
    {
        if (await AskText(folder ? "New folder" : "New file", $"In {Path.GetFileName(dir.TrimEnd('\\'))}", "", "Create") is not { Length: > 0 } name) return;
        var path = Path.Combine(dir, name.Trim());
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) { await Notice("Already exists", $"{name} is already there."); return; }
            if (folder) Directory.CreateDirectory(path);
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); }
            expanded.Add(dir);
            RefreshTree();
            if (!folder) OpenFileTab(path);
        }
        catch (Exception ex) { await Notice(folder ? "Couldn't create the folder" : "Couldn't create the file", ex.Message); }
    }

    async void RenameEntry(string path)
    {
        var old = Path.GetFileName(path.TrimEnd('\\'));
        if (await AskText("Rename", old, old, "Rename") is not { Length: > 0 } name || name.Trim() == old) return;
        var target = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\'))!, name.Trim());
        try
        {
            var (from, to) = IsDeviceName(path) || IsDeviceName(target) ? (Raw(path), Raw(target)) : (path, target);
            if (Directory.Exists(path)) Directory.Move(from, to); else File.Move(from, to);
            Retarget(path, target);   // tabs of the old path (or anything inside a renamed folder) reopen at the new one
            RefreshTree();
        }
        catch (Exception ex) { await Notice("Couldn't rename it", ex.Message); }
    }

    // Names Windows keeps for devices (nul, con, aux, com1…): ordinary file calls and the Recycle Bin refuse them, so they're
    // handled through the \\?\ form of the path. A stray "nul" file is often left by a command-line redirect.
    static readonly string[] DeviceNames = ["con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];
    static bool IsDeviceName(string path) => DeviceNames.Contains(Path.GetFileNameWithoutExtension(path.TrimEnd('\\')).ToLowerInvariant());
    static string Raw(string path) => @"\\?\" + Path.GetFullPath(path);

    async void DeleteEntries(List<string> paths)
    {
        paths = paths.Where(p => !string.Equals(p.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && (File.Exists(p) || Directory.Exists(p))).ToList();
        if (paths.Count == 0) return;
        var one = paths.Count == 1 ? paths[0] : null;
        var what = paths.Any(IsDeviceName) ? "Reserved names are deleted for good, not to the Recycle Bin; the rest go to the Recycle Bin."
                 : one != null && Directory.Exists(one) ? "It and its contents go to the Recycle Bin." : one != null ? "It goes to the Recycle Bin." : "They go to the Recycle Bin.";
        if (!await Confirm(one != null ? $"Delete {Path.GetFileName(one.TrimEnd('\\'))}?" : $"Delete {paths.Count} items?", what, "Delete", danger: true)) return;
        var bin = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin; var ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        foreach (var path in paths)
            try
            {
                var folder = Directory.Exists(path);
                if (IsDeviceName(path)) { if (folder) Directory.Delete(Raw(path), true); else File.Delete(Raw(path)); }
                else if (folder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, bin); else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, bin);
                foreach (var f in files.Where(f => f.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList()) { f.Dirty = false; CloseTab(f); }
            }
            catch (Exception ex) { await Notice($"Couldn't delete {Path.GetFileName(path.TrimEnd('\\'))}", ex.Message); }
        picked.Clear();
        RefreshTree();
    }
}
