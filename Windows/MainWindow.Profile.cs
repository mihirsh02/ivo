using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ivo;

// Who's using Ivo (asked once on first launch, editable in Settings → Profile). It's used for real: the greeting,
// the empty chat's suggestions, the starting mode, and — unless switched off — a short note the models get about you.
public partial class MainWindow
{
    static readonly (string Name, string Icon)[] Uses = [("Coding", "💻"), ("Writing", "✍️"), ("Studying", "📚"), ("Research", "🔬"), ("Work & business", "💼"), ("Everyday questions", "💬")];
    static readonly string[] Jobs = ["Student", "Developer", "Designer", "Writer", "Teacher", "Researcher", "Founder", "Manager"];
    int step;

    // ---------- your own datasets for Ivo (the format: see Core/IvoDataset.cs) ----------
    static string DatasetsDir => System.IO.Path.Combine(Store.Dir, "ivo-datasets");

    FrameworkElement IvoDatasetsList()
    {
        var list = new StackPanel();
        foreach (var d in S.IvoDatasets.ToList())
        {
            list.Children.Add(PackRow($"{d.Name} · {d.Entries} {(d.Entries == 1 ? "entry" : "entries")}{(d.Skipped > 0 ? $" ({d.Skipped} skipped)" : "")}", d.On,
                on => { d.On = on; SaveSettings(); DrawAssistantCard(); },
                () => { try { System.IO.File.Delete(System.IO.Path.Combine(DatasetsDir, d.File)); } catch { } S.IvoDatasets.Remove(d); SaveSettings(); DrawAssistantCard(); }));
        }
        var add = AddButton("Add a dataset…", "A JSON file of things people might say and how Ivo replies");
        add.Click += (_, _) => AddIvoDataset();
        list.Children.Add(add);
        return list;
    }

    void AddIvoDataset()
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = "A dataset for Ivo", Filter = "Ivo dataset (*.json)|*.json" };
        if (pick.ShowDialog(this) != true) return;
        try
        {
            var json = System.IO.File.ReadAllText(pick.FileName);
            var d = IvoDataset.Parse(json, System.IO.Path.GetFileNameWithoutExtension(pick.FileName));
            if (d.Entries.Count == 0) throw new System.IO.InvalidDataException($"None of its {d.Skipped} entries could be used: each needs a \"reply\", and \"match\" or \"words\".");
            System.IO.Directory.CreateDirectory(DatasetsDir);
            var file = Guid.NewGuid().ToString("N")[..10] + ".json";
            Store.WriteData(System.IO.Path.Combine(DatasetsDir, file), json);   // sealed with the app lock, like her chat
            S.IvoDatasets.Add(new IvoDatasetFile { File = file, Name = d.Name, Entries = d.Entries.Count, Skipped = d.Skipped });
            SaveSettings(); DrawAssistantCard();
        }
        catch (Exception ex) { _ = Notice("Couldn't add that dataset", ex.Message); }
    }

    // ---------- behaviour packs for Ivo's model (the format: see Core/IvoBehaviour.cs) ----------
    static string BehaviourDir => System.IO.Path.Combine(Store.Dir, "ivo-behaviour");

    // the app's pack with yours on top, in the order you added them
    void ApplyBehaviour() =>
        IvoBehaviour.Use(S.IvoBehaviourPacks.Where(p => p.On).Select(p => { try { return Store.ReadData(System.IO.Path.Combine(BehaviourDir, p.File)) ?? ""; } catch { return ""; } }).Where(j => j != ""));

    FrameworkElement IvoBehaviourList()
    {
        var list = new StackPanel();
        var shipped = new TextBlock { Text = "Ivo's own (clean, kind and caring) is always under yours", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        shipped.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        list.Children.Add(shipped);
        foreach (var p in S.IvoBehaviourPacks.ToList())
            list.Children.Add(PackRow(p.Name, p.On, on => { p.On = on; SaveSettings(); ApplyBehaviour(); },
                () => { try { System.IO.File.Delete(System.IO.Path.Combine(BehaviourDir, p.File)); } catch { } S.IvoBehaviourPacks.Remove(p); SaveSettings(); ApplyBehaviour(); DrawAssistantCard(); }));
        var add = AddButton("Add a behaviour pack…", "A JSON guide for Ivo's model: role, response style, examples, contextual notes, and phrases to avoid");
        add.Click += (_, _) =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "A behaviour pack for Ivo", Filter = "Behaviour pack (*.json)|*.json" };
            if (pick.ShowDialog(this) != true) return;
            try
            {
                var json = System.IO.File.ReadAllText(pick.FileName);
                var b = IvoBehaviour.Parse(json);
                System.IO.Directory.CreateDirectory(BehaviourDir);
                var file = Guid.NewGuid().ToString("N")[..10] + ".json";
                Store.WriteData(System.IO.Path.Combine(BehaviourDir, file), json);   // sealed with the app lock
                S.IvoBehaviourPacks.Add(new IvoBehaviourPack { File = file, Name = b.Name != "" ? b.Name : System.IO.Path.GetFileNameWithoutExtension(pick.FileName) });
                SaveSettings(); ApplyBehaviour(); DrawAssistantCard();
            }
            catch (Exception ex) { _ = Notice("Couldn't add that behaviour pack", ex.Message); }
        };
        list.Children.Add(add);
        return list;
    }

    // the small ✕ beside a row, to remove it
    Button RemoveButton(string tip, Action remove)
    {
        var x = new Button { Content = "\uE711", FontFamily = App.IconFont, FontSize = 10, Width = 26, Height = 26, Padding = new Thickness(0), Style = (Style)FindResource("Ghost"),
                             ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        x.Click += (_, _) => remove();
        return x;
    }

    // a dataset or pack: its switch and name, and its remove button, side by side
    FrameworkElement PackRow(string text, bool on, Action<bool> toggle, Action remove)
    {
        var box = new CheckBox
        {
            IsChecked = on,
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            ToolTip = text,
        };
        box.SetResourceReference(Control.ForegroundProperty, "Fg");
        box.Click += (_, _) => toggle(box.IsChecked == true);
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        var column = 1;
        var removeButton = RemoveButton("Remove", remove);
        Grid.SetColumn(removeButton, column);
        row.Children.Add(removeButton);
        return row;
    }

    // "Add a …" under a list
    Button AddButton(string text, string tip) =>
        new() { Content = text, Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0), ToolTip = tip };

    // the datasets that are on, for Ivo's chat
    List<IvoDataset> IvoDatasetsOn() =>
        S.IvoDatasets.Where(d => d.On).Select(d =>
        {
            try { return Store.ReadData(System.IO.Path.Combine(DatasetsDir, d.File)) is { Length: > 0 } json ? IvoDataset.Parse(json, d.Name) : null; }
            catch { return null; }
        }).OfType<IvoDataset>().ToList();

    void ShowWelcome(bool first)
    {
        var p = S.Profile ?? new Profile();
        WelcomeTitle.Text = first ? "Welcome to Ivo" : "Your profile";
        WelcomeSkip.Content = first ? "Maybe later" : "Cancel";
        ProfName.Text = p.Name; ProfBorn.SelectedDate = p.Born; ProfBorn.DisplayDateEnd = DateTime.Today; ProfWork.Text = p.Work;
        ProfMale.IsChecked = p.Gender == "Male"; ProfFemale.IsChecked = p.Gender == "Female";
        UseChips.Children.Clear();
        foreach (var (u, icon) in Uses)
            UseChips.Children.Add(new ToggleButton { Content = $"{icon}  {u}", Tag = u, Style = (Style)FindResource("Chip"), IsChecked = p.Uses.Contains(u), Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 8, 14, 8), FontSize = 13 });
        WorkChips.Children.Clear();
        foreach (var j in Jobs)
        {
            var chip = new ToggleButton { Content = j, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) => { chip.IsChecked = false; ProfWork.Text = j; ProfWork.CaretIndex = j.Length; ProfWork.Focus(); };
            WorkChips.Children.Add(chip);
        }
        ProfShare.IsChecked = p.Share;
        WelcomeLayer.Visibility = Visibility.Visible;
        GoStep(0);
    }

    // one question at a time: slide in, dots for progress, Enter moves on
    void GoStep(int to)
    {
        var dir = to >= step ? 1 : -1;
        step = to;
        for (var i = 0; i < WelcomeSteps.Children.Count; i++) WelcomeSteps.Children[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        WelcomeDots.Children.Clear();
        for (var i = 0; i < WelcomeSteps.Children.Count; i++)
        {
            var dot = new Border { Height = 6, Width = i == step ? 22 : 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 0, 0), Opacity = i <= step ? 1 : 0.3 };
            dot.SetResourceReference(Border.BackgroundProperty, i <= step ? "AccentGrad" : "Dim");
            WelcomeDots.Children.Add(dot);
        }
        WelcomeBack.Visibility = step > 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeNext.Content = step == WelcomeSteps.Children.Count - 1 ? "Get started  →" : "Continue";
        var name = ProfName.Text.Trim();
        WelcomeHello.Text = name == "" ? "Nice to meet you!" : $"Nice to meet you, {name.Split(' ')[0]}! 💜";
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        WelcomeSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * dir, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        WelcomeSteps.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        Dispatcher.BeginInvoke(() => (step == 0 ? ProfName : step == 1 ? ProfWork : (IInputElement?)null)?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void WelcomeNext_Click(object s, RoutedEventArgs e)
    {
        if (step < WelcomeSteps.Children.Count - 1) GoStep(step + 1); else WelcomeSave_Click(s, e);
    }

    // one or the other (or neither: tap again to clear)
    void ProfGender_Click(object s, RoutedEventArgs e) { if (s == ProfMale) ProfFemale.IsChecked = false; else ProfMale.IsChecked = false; }

    void WelcomeBack_Click(object s, RoutedEventArgs e) { if (step > 0) GoStep(step - 1); }

    void WelcomeSave_Click(object s, RoutedEventArgs e)
    {
        var uses = UseChips.Children.OfType<ToggleButton>().Where(t => t.IsChecked == true).Select(t => (string)t.Tag).ToList();
        var firstTime = S.Profile == null;
        S.Profile = new Profile { Name = ProfName.Text.Trim(), Born = ProfBorn.SelectedDate is { } born && born <= DateTime.Today ? born.Date : null, Gender = ProfMale.IsChecked == true ? "Male" : ProfFemale.IsChecked == true ? "Female" : "", Work = ProfWork.Text.Trim(), Uses = uses, Share = ProfShare.IsChecked == true };
        S.WelcomeDone = true;
        // the starting mode follows what it's mainly for: coding opens the IDE, everything else the chat
        if (firstTime && uses.Count > 0 && S.View != (uses[0] == "Coding" ? "code" : "chat"))
        {
            S.View = uses[0] == "Coding" ? "code" : "chat";
            foreach (RadioButton r in ViewChoices.Children) r.IsChecked = (string)r.Tag == S.View;
            ApplyView();
        }
        SaveSettings();
        ApplyProfile();
        WelcomeLayer.Visibility = Visibility.Collapsed;
        if (!S.TourDone) StartTour();
    }

    void WelcomeSkip_Click(object s, RoutedEventArgs e)
    {
        S.WelcomeDone = true; SaveSettings();
        WelcomeLayer.Visibility = Visibility.Collapsed;
        if (!S.TourDone) StartTour();
    }

    void DrawMemories()
    {
        MemoryList.Children.Clear();
        MemoryTitle.Text = S.Memories.Count == 0 ? "Memory" : $"Memory  ·  {S.Memories.Count}";
        MemoryForgetAll.Visibility = S.Memories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (S.Memories.Count == 0) MemoryList.Children.Add(new TextBlock { Text = "Nothing remembered yet.", FontSize = 12, Foreground = (Brush)FindResource("Dim") });
        foreach (var m in S.Memories.ToList())
        {
            var dot = new TextBlock { Text = "\uE8F1", FontFamily = App.IconFont, FontSize = 14, Foreground = Shell.B("#A78BFA"), Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            var del = new Button { Content = "\uE74D", Style = (Style)FindResource("Ghost"), FontFamily = App.IconFont, FontSize = 13, ToolTip = "Forget this", Opacity = 0, VerticalAlignment = VerticalAlignment.Top };
            del.Click += (_, _) => { S.Memories.Remove(m); SaveSettings(); DrawMemories(); };
            var text = new TextBlock { Text = m, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, LineHeight = 19 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            DockPanel.SetDock(dot, Dock.Left); DockPanel.SetDock(del, Dock.Right);
            var row = new Border { Padding = new Thickness(6, 6, 2, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Child = new DockPanel { Children = { dot, del, text } } };
            row.MouseEnter += (_, _) => { del.Opacity = 1; row.Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x90)); };
            row.MouseLeave += (_, _) => { del.Opacity = 0; row.Background = Brushes.Transparent; };
            MemoryList.Children.Add(row);
        }
    }

    async void MemoryForgetAll_Click(object s, RoutedEventArgs e)
    {
        if (!await Confirm("Forget everything?", $"All {S.Memories.Count} memories will be deleted.", "Forget all", danger: true)) return;
        S.Memories.Clear(); SaveSettings(); DrawMemories();
    }

    void ProfileShare_Changed(object s, RoutedEventArgs e)
    {
        if (loading || S.Profile == null) return;
        S.Profile.Share = ProfileShareBox.IsChecked == true;
        SaveSettings(); ApplyProfile();
    }

    void EditProfile_Click(object s, RoutedEventArgs e) { SettingsPanel.Visibility = Visibility.Collapsed; ShowWelcome(first: false); }

    async void ClearProfile_Click(object s, RoutedEventArgs e)
    {
        if (!await Confirm("Clear your profile?", "Memories stay.", "Clear", danger: true)) return;
        S.Profile = null; SaveSettings(); ApplyProfile();
    }

    // Everywhere the profile shows up: the models' note, the greeting and suggestions, the Settings summary.
    void ApplyProfile()
    {
        Agent.Me = S.Profile;
        IvoMind.YourBirth = S.Profile?.Born; IvoMind.Gender = S.Profile?.Gender ?? "";   // Ivo has them as soon as they change
        Agent.Memories = S.Memories;
        Agent.MemoriesChanged = () => { SaveSettings(); DrawMemories(); };
        DrawMemories();
        foreach (var p in AllPanes) p.Greet(CodeMode && p == active);
        DrawProfileCard();
    }

    // avatar with initials, name and work; then age, what it's used for and where it opens; the models' note below
    void DrawProfileCard()
    {
        ProfileCard.Children.Clear();
        var me = S.Profile;
        ProfileEdit.Content = me == null ? "Set up profile" : "Edit profile";
        ProfileClear.Visibility = me == null ? Visibility.Collapsed : Visibility.Visible;
        loading = true; ProfileShareBox.IsChecked = me?.Share == true; ProfileShareBox.IsEnabled = me != null; loading = false;
        ProfilePreview.Text = me == null ? "Nothing: there's no profile yet." : Agent.AboutMe() is { Length: > 0 } note ? note : "Nothing: sharing is off.";
        var initials = me == null || me.Name == "" ? "?" : string.Concat(me.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0])));
        var avatar = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Margin = new Thickness(0, 0, 14, 0), Cursor = System.Windows.Input.Cursors.Hand,
                                  ToolTip = "Your picture (your contacts see it in Chats)",
                                  Child = new TextBlock { Text = initials, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        avatar.SetResourceReference(Border.BackgroundProperty, "AccentGrad");
        // your picture: the one your Chats contacts see; click to pick one
        if (ivoChats?.Book.Pic is { Length: > 0 } pic)
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage(); img.BeginInit(); img.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(pic)); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.EndInit();
                avatar.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; avatar.Child = null;
            }
            catch { }
        avatar.MouseLeftButtonUp += (_, _) =>
        {
            if (ivoChats == null) StartChats();
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Your picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
            if (ivoChats == null || dlg.ShowDialog(this) != true) return;
            try { if (PictureCropper.Crop(dlg.FileName, this) is { } cropped) { _ = ivoChats.SetProfile(cropped, null); DrawProfileCard(); } }   // you crop it
            catch (Exception ex) { _ = Notice("Couldn't use that picture", ex.Message); }
        };
        var name = new TextBlock { Text = me is { Name.Length: > 0 } ? me.Name : "No profile yet", FontSize = 18, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var sub = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                                  Text = me == null ? "Tell Ivo a little about you so it can greet you and suggest things that fit." : me.Work != "" ? me.Work : "No profession given" };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        DockPanel.SetDock(avatar, Dock.Left);
        ProfileCard.Children.Add(new DockPanel { Children = { avatar, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } } } });
        // your status in Chats: Active, Busy, Away or Invisible (shown to others as offline)
        if (ivoChats != null)
        {
            var statuses = new WrapPanel { Margin = new Thickness(66, 10, 0, 0) };
            foreach (var (id, label, color) in IvoChatsView.StatusList)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = Shell.B(color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } },
                                                                                Style = (Style)FindResource("Chip"), IsChecked = ivoChats.Book.Status == id, Margin = new Thickness(0, 0, 6, 6) };
                chip.Click += (_, _) => { _ = ivoChats.SetProfile(null, id); DrawProfileCard(); };
                statuses.Children.Add(chip);
            }
            if (ivoChats.Book.Pic != "")
            {
                var rm = new Button { Content = "Remove picture", Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 6, 6) };
                rm.Click += (_, _) => { _ = ivoChats.SetProfile("", null); DrawProfileCard(); };
                statuses.Children.Add(rm);
            }
            ProfileCard.Children.Add(statuses);
        }
        if (me == null) return;

        TextBlock Value(string t) { var v = new TextBlock { Text = t, FontSize = 13, TextWrapping = TextWrapping.Wrap }; v.SetResourceReference(TextBlock.ForegroundProperty, "Fg"); return v; }
        FrameworkElement Row(string label, UIElement value)
        {
            var l = new TextBlock { Text = label, FontSize = 12, Width = 120, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            DockPanel.SetDock(l, Dock.Left);
            return new DockPanel { Margin = new Thickness(0, 10, 0, 0), Children = { l, value } };
        }
        var line = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        ProfileCard.Children.Add(line);
        ProfileCard.Children.Add(Row("Born", Value(me.Born is { } born ? $"{born:d MMMM yyyy} · {me.Age} years" : "Not given")));
        ProfileCard.Children.Add(Row("Gender", Value(me.Gender != "" ? me.Gender : "Not given")));
        var uses = new WrapPanel();
        foreach (var u in me.Uses)
        {
            var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1),
                                    Child = Value($"{Uses.FirstOrDefault(x => x.Name == u).Icon} {u}".Trim()) };
            chip.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            uses.Children.Add(chip);
        }
        if (me.Uses.Count == 0) uses.Children.Add(Value("Not given"));
        ProfileCard.Children.Add(Row("Uses Ivo for", uses));
    }

    // Settings → Assistant: Ivo's datasets
    // her personality: a preset to tap, or your own words (the model follows it; her own replies lean that way)
    FrameworkElement PersonalityPicker()
    {
        var box = new TextBox { Text = S.IvoPersonality, TextWrapping = TextWrapping.Wrap, Width = 420, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "In your words, e.g. \"a sarcastic bestie who loves anime and roasts me lovingly\"" };
        void Set(string p) { S.IvoPersonality = IvoMind.Personality = p; SaveSettings(); }
        box.LostFocus += (_, _) => Set(box.Text.Trim());
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (name, says) in new[] { ("Sweet", "sweet, caring and gentle, lots of hearts"), ("Cheeky", "cheeky and playful, teases a lot"), ("Sassy", "sassy and confident, quick comebacks"),
                                             ("Chill", "chill and laid-back, low-key funny"), ("Nerdy", "nerdy and curious, loves facts and games"), ("Shy", "a bit shy, soft-spoken and sweet") })
        {
            var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = name, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 8, 8), IsChecked = S.IvoPersonality.StartsWith(says[..5], StringComparison.OrdinalIgnoreCase) };
            chip.SetResourceReference(BackgroundProperty, "InputBg");
            chip.Click += (_, _) => { box.Text = says; Set(says); DrawAssistantCard(); };
            presets.Children.Add(chip);
        }
        return new StackPanel { Children = { presets, Hinted(box, "or describe her in your own words") } };
    }

    // Smarter chat: the model that writes her replies. Your models live in Ivo's models folder: the standard one (a
    // download) and any you add (copied in, so cleaning out Downloads can't take them away). Pick the one she uses; adding
    // your own replaces the standard download.
    FrameworkElement SmarterChatRow()
    {
        var panel = new StackPanel();
        var models = System.IO.Directory.Exists(IvoModel.Dir) ? System.IO.Directory.GetFiles(IvoModel.Dir, "*.gguf").OrderBy(f => f != IvoModel.Default).ThenBy(f => f).ToList() : [];
        var row = new WrapPanel();
        Button Btn(string text, Action click, bool main = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6) };
            if (main) b.Style = (Style)FindResource("Shimmer");
            b.Click += (_, _) => click();
            return b;
        }
        if (models.Count > 0)
        {
            var on = new CheckBox { IsChecked = S.SmarterChat, Content = "Use a model for her replies", Margin = new Thickness(0, 0, 0, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
            on.SetResourceReference(ForegroundProperty, "Fg");
            on.Click += (_, _) => { S.SmarterChat = IvoModel.On = on.IsChecked == true; SaveSettings(); };
            panel.Children.Add(on);
            // the models: pick one, or remove one
            foreach (var f in models)
            {
                var standard = f == IvoModel.Default;
                var label = standard ? "Standard model" : System.Text.RegularExpressions.Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(f), "[-_]+", " ");
                var info = new System.IO.FileInfo(f);
                var pick = new RadioButton { GroupName = "IvoModel", IsChecked = IvoModel.File == f, VerticalAlignment = VerticalAlignment.Center,
                                             Content = new TextBlock { Text = $"{label}  ·  {info.Length / 1048576.0:0} MB · {(standard ? "downloaded" : "added")} {info.LastWriteTime:d MMM}", TextTrimming = TextTrimming.CharacterEllipsis } };
                ((TextBlock)pick.Content).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                var path = f;
                pick.Checked += (_, _) => { S.SmarterChatFile = IvoModel.Custom = standard ? "" : path; IvoModel.Unload(); SaveSettings(); };
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
                var x = RemoveButton("Remove this model", () =>
                {
                    if (IvoModel.File == path) { IvoModel.Unload(); S.SmarterChatFile = IvoModel.Custom = ""; }
                    try { System.IO.File.Delete(path); } catch (Exception ex) { _ = Notice("Couldn't remove it", ex.Message); }
                    if (!IvoModel.Ready && System.IO.Directory.GetFiles(IvoModel.Dir, "*.gguf").FirstOrDefault() is { } next) S.SmarterChatFile = IvoModel.Custom = next;
                    if (!IvoModel.Ready) S.SmarterChat = IvoModel.On = false;
                    SaveSettings(); DrawAssistantCard();
                });
                DockPanel.SetDock(x, Dock.Right); line.Children.Add(x); line.Children.Add(pick);
                panel.Children.Add(line);
            }
            panel.Children.Add(new Border { Height = 8 });
        }
        else
        {
            var bar = new ProgressBar { Width = 160, Height = 6, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
            Button? get = null;
            get = Btn($"Download the standard model ({IvoModel.SizeMb:0} MB)", async () =>
            {
                get!.IsEnabled = false; bar.Visibility = Visibility.Visible;
                try
                {
                    await IvoModel.Download(new Progress<double>(p => { bar.Value = p * 100; get.Content = $"Downloading… {p:P0}"; }), CancellationToken.None);
                    S.SmarterChat = IvoModel.On = true; SaveSettings();
                }
                catch (Exception ex) { _ = Notice("Couldn't download the model", ex.Message); }
                DrawAssistantCard();
            }, main: true);
            row.Children.Add(get); row.Children.Add(bar);
        }
        Button? add = null;
        add = Btn("Add your own model…", async () =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Model (GGUF)|*.gguf", Title = "Your model file (e.g. your trained Ivo)" };
            if (pick.ShowDialog(this) != true) return;
            if (System.IO.File.Exists(IvoModel.Default) && !await Confirm("Use your own model?",
                    "It's copied into Ivo and becomes the model she uses. The standard model is removed to give you its space back (about 400 MB).\n\nYou can download the standard one, or a better one, again any time from Browse models.", "Add it"))
                return;
            add!.IsEnabled = false; add.Content = "Adding…";
            try
            {
                System.IO.Directory.CreateDirectory(IvoModel.Dir);
                var to = System.IO.Path.Combine(IvoModel.Dir, System.IO.Path.GetFileName(pick.FileName));
                if (System.IO.File.Exists(to)) to = System.IO.Path.Combine(IvoModel.Dir, System.IO.Path.GetFileNameWithoutExtension(pick.FileName) + $"-{DateTime.Now:yyMMddHHmm}.gguf");
                if (!string.Equals(System.IO.Path.GetFullPath(pick.FileName), System.IO.Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
                    await Task.Run(() => System.IO.File.Copy(pick.FileName, to));   // a copy in Ivo's own folder: safe from a cleaned-out Downloads
                IvoModel.Unload();
                S.SmarterChatFile = IvoModel.Custom = to; S.SmarterChat = IvoModel.On = true;
                // your own model in, the standard download out (400 MB back)
                if (System.IO.File.Exists(IvoModel.Default)) try { System.IO.File.Delete(IvoModel.Default); } catch { }
                SaveSettings();
            }
            catch (Exception ex) { _ = Notice("Couldn't add that model", ex.Message); }
            DrawAssistantCard();
        });
        row.Children.Add(add);
        row.Children.Add(Btn("Browse models…", ModelStore));
        // the replies it got wrong, as training examples for you to finish: write the right "reply", train, and it learns them
        if (Review.Count > 0)
            row.Children.Add(Btn($"Export {Review.Count} poor repl{(Review.Count == 1 ? "y" : "ies")} for training…", () =>
            {
                var pick = new Microsoft.Win32.SaveFileDialog { FileName = $"ivo-corrections-{DateTime.Now:yyyy-MM-dd}.jsonl", Filter = "Training data (*.jsonl)|*.jsonl" };
                if (pick.ShowDialog(this) != true) return;
                try
                {
                    System.IO.File.WriteAllText(pick.FileName, Review.Export());
                    _ = Notice("Saved", "Fill in each \"reply\" with what she should have said (her \"bad\" one is beside it), then add the file to your training notebook.");
                    Review.Clear(); DrawAssistantCard();
                }
                catch (Exception ex) { _ = Notice("Couldn't save it", ex.Message); }
            }));
        panel.Children.Add(row);
        return panel;
    }

    // what she has learnt (IvoMemory): words, what messages meant, facts and your taught replies, each removable. A tidy
    // list: a kind chip, the line (cut short, all of it on hover), and a ✕; past a few rows it scrolls, without a bar.
    bool learntOpen;
    FrameworkElement IvoLearntList()
    {
        IvoMind.Memory = IvoMemory.Load(IvoMemory.Path);
        var mem = IvoMind.Memory;
        var list = new StackPanel();
        void Changed() { mem.Save(IvoMemory.Path); DrawAssistantCard(); }
        void Item(string kind, string text, Action remove)
        {
            var t = new TextBlock { Text = text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"{kind}: {text}" };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var row = new Grid { Margin = new Thickness(0, 0, 8, 0), MinHeight = 30, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var x = RemoveButton("Forget it", () => { remove(); Changed(); });
            Grid.SetColumn(x, 1);
            row.Children.Add(t); row.Children.Add(x);
            list.Children.Add(row);
        }
        foreach (var (w, m) in mem.You.ToList()) Item("About you", $"your {w}: {m.What}{(m.On is { } on ? $" ({on:d MMM})" : "")}", () => mem.You.Remove(w));
        foreach (var (w, m) in mem.Taught.ToList()) Item("Taught", $"\"{w}\" → \"{m}\"", () => mem.Taught.Remove(w));
        foreach (var (w, m) in mem.Phrases.ToList()) Item("Means", $"\"{w}\" → {m.Replace('_', ' ')}", () => mem.Phrases.Remove(w));
        foreach (var (w, m) in mem.Words.ToList()) Item("Word", $"{w}: {m}", () => mem.Words.Remove(w));
        foreach (var (w, m) in mem.Facts.ToList()) Item("Fact", $"{w}: {m}", () => mem.Facts.Remove(w));
        if (mem.Count == 0)
        {
            var none = new TextBlock { Text = "Nothing yet. She learns words she looks up and what you teach her (\"when I say gm, say good morning\").", FontSize = 13, TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            return none;
        }
        var junk = new Button { Content = "Clear junk", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0),
                                ToolTip = "Forget everyday words she looked up for nothing, odd or crude meanings, and one-word reactions she read too much into" };
        junk.Click += async (_, _) =>
        {
            junk.IsEnabled = false; junk.Content = "Checking…";
            var gone = await mem.ClearJunk();
            mem.Save(IvoMemory.Path);
            _ = Notice("Cleared", gone == 0 ? "Nothing to clear." : $"She forgot {gone} thing{(gone == 1 ? "" : "s")} she'd got wrong.");
            DrawAssistantCard();
        };
        var all = new Button { Content = "Forget all", Style = (Style)FindResource("Ghost"), Padding = new Thickness(12, 5, 12, 5) };
        all.Click += (_, _) => { mem.Words.Clear(); mem.Phrases.Clear(); mem.Facts.Clear(); mem.Taught.Clear(); mem.You.Clear(); Changed(); };
        var scroll = new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // folded: a count and Show; open, the list and its buttons
        var more = new StackPanel { Visibility = learntOpen ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0),
                                    Children = { scroll, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { junk, all } } } };
        var count = new TextBlock { Text = $"{mem.Count} thing{(mem.Count == 1 ? "" : "s")} she's learnt", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var show = new Button { Content = learntOpen ? "Hide" : "Show", Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        show.Click += (_, _) => { learntOpen = !learntOpen; more.Visibility = learntOpen ? Visibility.Visible : Visibility.Collapsed; show.Content = learntOpen ? "Hide" : "Show"; };
        return new StackPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { count, show } }, more } };
    }
    void IvoIntro_Click(object s, RoutedEventArgs e) { SettingsPanel.Visibility = Visibility.Collapsed; ShowIntro(thenWelcome: false); }
}
