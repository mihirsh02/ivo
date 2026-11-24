using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Ivo;

// Code mode: Ctrl+P jumps to any file in the folder by typing part of its name; Ctrl+H finds and replaces in the open file.
public partial class MainWindow
{
    List<string> allFiles = [];

    void ShowQuickOpen()
    {
        if (root == null) return;
        allFiles = [];
        try
        {
            var skip = new[] { "\\.git\\", "\\.vs\\", "\\bin\\", "\\obj\\", "\\node_modules\\", "\\publish\\" };
            foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (skip.Any(x => f.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                allFiles.Add(Path.GetRelativePath(root, f));
                if (allFiles.Count >= 30_000) break;   // ponytail: cap for huge folders; index on a background thread if that bites
            }
        }
        catch { }
        QuickBox.Text = "";
        QuickLayer.Visibility = Visibility.Visible;
        FilterQuick();
        QuickBox.Focus();
    }

    // every typed letter must appear in order; name hits beat folder hits, then shorter paths
    static int Score(string path, string q)
    {
        if (q == "") return 1;
        var name = Path.GetFileName(path);
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1000 - path.Length;
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 800 - path.Length;
        if (path.Contains(q, StringComparison.OrdinalIgnoreCase)) return 600 - path.Length;
        int i = 0;
        foreach (var c in path) if (i < q.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length ? 300 - path.Length : 0;
    }

    void FilterQuick()
    {
        var q = QuickBox.Text.Trim().Replace('/', '\\');
        QuickList.ItemsSource = allFiles.Select(f => (f, s: Score(f, q))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(60)
            .Select(x => new ListBoxItem { Content = Row(x.f), Tag = x.f, Padding = new Thickness(8, 4, 8, 4) }).ToList();
        QuickList.SelectedIndex = 0;

        static FrameworkElement Row(string f)
        {
            var t = new TextBlock { FontSize = 13 };
            t.Inlines.Add(new System.Windows.Documents.Run(Path.GetFileName(f)));
            t.Inlines.Add(new System.Windows.Documents.Run("   " + Path.GetDirectoryName(f)) { FontSize = 12, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Dim") });
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            return t;
        }
    }

    void QuickBox_TextChanged(object s, TextChangedEventArgs e) => FilterQuick();

    void QuickBox_KeyDown(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: QuickList.SelectedIndex = Math.Min(QuickList.SelectedIndex + 1, QuickList.Items.Count - 1); QuickList.ScrollIntoView(QuickList.SelectedItem); e.Handled = true; break;
            case Key.Up: QuickList.SelectedIndex = Math.Max(QuickList.SelectedIndex - 1, 0); QuickList.ScrollIntoView(QuickList.SelectedItem); e.Handled = true; break;
            case Key.Enter: OpenQuick(); e.Handled = true; break;
            case Key.Escape: QuickLayer.Visibility = Visibility.Collapsed; e.Handled = true; break;
        }
    }

    void QuickList_Click(object s, MouseButtonEventArgs e) => OpenQuick();
    void QuickBackdrop_Click(object s, MouseButtonEventArgs e) => QuickLayer.Visibility = Visibility.Collapsed;

    void OpenQuick()
    {
        if (QuickList.SelectedItem is not ListBoxItem { Tag: string f } || root == null) return;
        QuickLayer.Visibility = Visibility.Collapsed;
        OpenFileTab(Path.Combine(root, f));
        Editor.Focus();
    }

    // ---------- find & replace ----------
    void ShowReplace()
    {
        if (Editor.Visibility != Visibility.Visible) return;
        ReplaceBar.Visibility = Visibility.Visible;
        if (Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n')) FindBox.Text = Editor.SelectedText;
        FindBox.Focus(); FindBox.SelectAll();
    }

    StringComparison Cmp => MatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    bool FindNext()
    {
        var q = FindBox.Text;
        if (q == "") return false;
        var text = Editor.Text;
        var at = text.IndexOf(q, Editor.SelectionStart + Editor.SelectionLength, Cmp);
        if (at < 0) at = text.IndexOf(q, 0, Cmp);   // wrap around
        if (at < 0) { ReplaceNote.Text = "No matches"; return false; }
        Editor.Select(at, q.Length);
        Editor.ScrollTo(Editor.Document.GetLineByOffset(at).LineNumber, 0);
        ReplaceNote.Text = "";
        return true;
    }

    void FindNext_Click(object s, RoutedEventArgs e) => FindNext();

    void ReplaceOne_Click(object s, RoutedEventArgs e)
    {
        if (Editor.SelectionLength > 0 && string.Equals(Editor.SelectedText, FindBox.Text, Cmp))
            Editor.Document.Replace(Editor.SelectionStart, Editor.SelectionLength, ReplaceBox.Text);
        FindNext();
    }

    void ReplaceAll_Click(object s, RoutedEventArgs e)
    {
        var q = FindBox.Text;
        if (q == "") return;
        var doc = Editor.Document;
        var n = 0;
        doc.BeginUpdate();   // one undo step
        for (var at = doc.Text.IndexOf(q, 0, Cmp); at >= 0; at = doc.Text.IndexOf(q, at + ReplaceBox.Text.Length, Cmp))
        {
            doc.Replace(at, q.Length, ReplaceBox.Text);
            n++;
        }
        doc.EndUpdate();
        ReplaceNote.Text = n == 0 ? "No matches" : $"Replaced {n}";
    }

    void FindBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; if (s == ReplaceBox) ReplaceOne_Click(s, e); else FindNext(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CloseReplace_Click(s, e); }
    }

    void CloseReplace_Click(object s, RoutedEventArgs e) { ReplaceBar.Visibility = Visibility.Collapsed; Editor.Focus(); }
}
