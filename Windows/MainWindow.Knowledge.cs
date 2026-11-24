using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

namespace Ivo;

// The knowledge folder from the app's side: saving replies and web pages into it as Markdown notes.
public partial class MainWindow
{
    // a reply (☆ row → Save to notes) or a page (/clip url): <knowledge folder>/Clips/<date> <title>.md
    public void ClipToNotes(string title, string markdown, string? source = null)
    {
        if (!Directory.Exists(S.KnowledgeFolder))
        {
            _ = Notice("No knowledge folder yet", "Choose one in Settings → Data → Knowledge folder; saved replies and pages go into it.");
            OpenSettings("data");
            return;
        }
        try
        {
            var dir = Path.Combine(S.KnowledgeFolder, "Clips");
            Directory.CreateDirectory(dir);
            var name = Regex.Replace(title, @"[\\/:*?""<>|\r\n]", "").Trim();
            if (name.Length > 60) name = name[..60].Trim();
            var file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd HHmm} {(name == "" ? "note" : name)}.md");
            File.WriteAllText(file, $"# {title}\n\n_Saved {DateTime.Now:yyyy-MM-dd HH:mm}{(source != null ? $" from {source}" : "")}_\n\n{markdown}\n");
            Knowledge.Changed();
            NotifyCard.Show("Saved to notes", Path.GetFileName(file), "", "#3FB950", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""));
        }
        catch (Exception ex) { _ = Notice("Couldn't save the note", ex.Message); }
    }
}
