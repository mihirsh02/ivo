using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Settings → Agents: create and edit the specialists the chat model can hand work to (Agents/Subagents.cs runs them).
public partial class MainWindow
{
    AgentDef? editingAgent;

    void ShowSubagents()
    {
        AgentModel.ItemsSource = ModelItems("Same as the chat");
        if (AgentModel.SelectedIndex < 0) AgentModel.SelectedIndex = 0;
        DrawAgents();
    }

    // "first" (Tag ""), then each provider's default model and the pinned ones (Tag "provider/model"); CLI tools too if asked
    List<ComboBoxItem> ModelItems(string first, bool cli = false)
    {
        var items = new List<ComboBoxItem> { new() { Content = first, Tag = "" } };
        foreach (var (prov, model) in S.Providers.Where(p => cli || p.Kind != "cli").Select(p => (p.Name, DefaultModel(p.Name, ModelCache.GetValueOrDefault(p.Name))))
                     .Concat(S.Pinned.Select(x => x.Split('|')).Where(a => a.Length == 2).Select(a => (a[0], a[1]))).Where(x => x.Item2 != "").Distinct())
            items.Add(new() { Content = $"{model}  ·  {prov}", Tag = $"{prov}/{model}" });
        return items;
    }


    void DrawAgents()
    {
        AgentList.Children.Clear();
        var all = Subagents.All(active?.Chat.Workspace);
        NoSubagents.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var a in all)
        {
            var title = new TextBlock { FontSize = 13 };
            title.Inlines.Add(new System.Windows.Documents.Run(a.Name) { FontWeight = FontWeights.SemiBold });
            title.Inlines.Add(new System.Windows.Documents.Run($"   {(a.ReadOnly ? "read-only" : "can make changes")} · {(a.Model ?? "same model as the chat")}{(a.Project ? " · this project" : "")}") { FontSize = 12.5, Foreground = (Brush)FindResource("Dim") });
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var desc = new TextBlock { Text = a.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var edit = new Button { Content = "Edit", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            edit.Click += (_, _) => BeginAgentEdit(a);
            var del = new Button { Content = "", FontFamily = App.IconFont, Style = (Style)FindResource("Ghost"), FontSize = 14, ToolTip = "Delete", VerticalAlignment = VerticalAlignment.Center };
            del.Click += async (_, _) =>
            {
                if (!await Confirm($"Delete {a.Name}?", "It will be deleted from Ivo.", "Delete", danger: true)) return;
                try { System.IO.File.Delete(a.File); } catch (Exception ex) { _ = Notice("Couldn't delete it", ex.Message); }
                DrawAgents();
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { del, edit } };
            DockPanel.SetDock(actions, Dock.Right);
            var card = new Border { Style = (Style)FindResource("Group"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 12, 12, 12),
                                    Child = new DockPanel { Children = { actions, new StackPanel { Children = { title, desc } } } } };
            AgentList.Children.Add(card);
        }
    }

    void BeginAgentEdit(AgentDef a)
    {
        editingAgent = a;
        AgentName.Text = a.Name; AgentDesc.Text = a.Description; AgentInstructions.Text = a.Instructions;
        (a.ReadOnly ? AgentReadOnly : AgentFull).IsChecked = true;
        AgentModel.SelectedItem = AgentModel.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (a.Model ?? "")) ?? AgentModel.Items[0];
        AgentFormTitle.Text = $"Edit {a.Name}"; AgentSaveText.Text = "Save"; AgentCancel.Visibility = Visibility.Visible; AgentStatus.Text = "";
        SectionScroll.ScrollToTop();
    }

    void AgentCancel_Click(object s, RoutedEventArgs e) => ResetAgentForm();

    void ResetAgentForm()
    {
        editingAgent = null;
        AgentName.Clear(); AgentDesc.Clear(); AgentInstructions.Clear(); AgentReadOnly.IsChecked = true; AgentModel.SelectedIndex = 0;
        AgentFormTitle.Text = "New agent"; AgentSaveText.Text = "Create"; AgentCancel.Visibility = Visibility.Collapsed;
    }

    void AgentSave_Click(object s, RoutedEventArgs e)
    {
        var name = AgentName.Text.Trim();
        if (name == "" || AgentDesc.Text.Trim() == "" || AgentInstructions.Text.Trim() == "") { AgentStatus.Text = "Needs a name, description and instructions."; return; }
        var clash = Subagents.All(active?.Chat.Workspace).FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (clash != null && clash.File != editingAgent?.File) { AgentStatus.Text = $"There's already an agent called {clash.Name}."; return; }
        var model = AgentModel.SelectedItem is ComboBoxItem { Tag: string t } && t != "" ? t : null;
        try { Subagents.Save(new AgentDef(name, AgentDesc.Text.Trim(), AgentInstructions.Text, model, AgentReadOnly.IsChecked == true, "", false), editingAgent is { Project: false } old ? old.File : null); }
        catch (Exception ex) { AgentStatus.Text = ex.Message; return; }
        AgentStatus.Text = "";
        ResetAgentForm();
        DrawAgents();
    }

    void AgentsFolder_Click(object s, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Subagents.Dir);
        System.Diagnostics.Process.Start("explorer.exe", Subagents.Dir);
    }
}
