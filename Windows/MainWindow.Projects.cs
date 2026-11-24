using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Ivo;

// Projects: a chat group with its own instructions (sent with every chat in it), default model and folder for new chats
// started there. Right-click a group's heading in the sidebar.
public partial class MainWindow
{
    static readonly string[] NotProjects = ["Groups", "Recent", "Pinned"];

    void GroupHead_RightClick(object s, MouseButtonEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string name } || NotProjects.Contains(name)) return;
        e.Handled = true;
        var p = S.Projects.GetValueOrDefault(name) ?? new Project();
        void Save(Action<Project> change) { var q = S.Projects.GetValueOrDefault(name) ?? new Project(); change(q); S.Projects[name] = q; SaveSettings(); }
        ShowMenu(e.GetPosition(this),
        [
            ("", "New chat here", () =>
            {
                var c = NewChat(); c.Group = name;
                if (p.Provider != "" && S.Providers.Any(x => x.Name == p.Provider)) (c.Provider, c.Model) = (p.Provider, p.Model);
                if (p.Folder != "" && Directory.Exists(p.Folder)) c.Workspace = p.Folder;
                active.ShowChat(c); active.FocusInput();
            }, false),
            null,
            ("", p.Instructions == "" ? "Project instructions…" : "Edit project instructions…", async () =>
            {
                if (await AskText($"{name}: instructions", "Sent with every chat in this project (for example: who it's for, tone, what to avoid).", p.Instructions, "Save") is { } text)
                    Save(q => q.Instructions = text.Trim());
            }, false),
            ("", active.Chat.Provider is { } prov ? $"Use {active.Chat.Model} for new chats here" : "Default model", () =>
            {
                if (active.Chat.Provider is { } pv) Save(q => (q.Provider, q.Model) = (pv, active.Chat.Model ?? ""));
            }, false),
            ("", p.Folder == "" ? "Default folder…" : $"Folder: {Path.GetFileName(p.Folder.TrimEnd('\\'))}…", () =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = $"{name}: the folder new chats here work in" };
                if (d.ShowDialog(this) == true) Save(q => q.Folder = d.FolderName);
            }, false),
            .. (p.Instructions != "" || p.Provider != "" || p.Folder != "" ? [("", "Clear project settings", () => { S.Projects.Remove(name); SaveSettings(); }, true)] : new (string, string, Action, bool)?[0]),
        ]);
    }
}
