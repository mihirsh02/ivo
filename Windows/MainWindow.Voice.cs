using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Ivo;

// Settings → Voice: the voice you're using, its speed and volume; Windows' voices in a dropdown (the default); the two natural
// voice packages (Kokoro) to download or remove; and, once one is in, the natural voices, listed when filtered or searched.
public partial class MainWindow
{
    bool voiceSearched;
    string voiceLang = "All languages", voiceAccent = "All accents", voiceGender = "Any gender";

    void ShowVoice()
    {
        loading = true;
        ReadAloudBox.IsChecked = S.ReadAloud; AppSoundsBox.IsChecked = S.AppSounds;
        foreach (var r in DictationChoices.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Tag == S.Dictation;
        VoiceRate.Value = S.VoiceRate;
        VoiceVolume.Value = S.VoiceVolume;
        loading = false;
        DrawVoices();
    }

    void Dictation_Checked(object s, RoutedEventArgs e)
    {
        if (!loading && s is RadioButton { Tag: string choice }) { S.Dictation = choice; SaveSettings(); }
    }

    // ---------- the current voice ----------
    string CurrentVoiceId => S.Voice == "" ? Voice.Default() : S.Voice;

    void DrawCurrentVoice(List<Voice.Entry> all)
    {
        var mine = all.FirstOrDefault(v => v.Id == (S.Voice == "" ? Voice.Default() : S.Voice));
        if (mine == null || !mine.Installed)
        {
            CurrentVoiceName.Text = "No voice yet";
            CurrentVoiceDetail.Text = "Pick or download a voice.";
            VoiceSample.IsEnabled = false;
            return;
        }
        CurrentVoiceName.Text = mine.Name;
        CurrentVoiceDetail.Text = Describe(mine);
        VoiceSample.IsEnabled = true;
    }

    static string Describe(Voice.Entry v, bool kind = true) =>
        string.Join("  ·  ", new[] { !kind ? "" : v.Natural ? "Natural" : "Windows", v.Language, v.Accent, v.Gender == "Unknown" ? "" : v.Gender, v.Detail }.Where(x => x != ""));

    void VoiceVolume_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (S == null || VoiceVolumeText == null) return;
        S.VoiceVolume = (int)VoiceVolume.Value;
        Voice.Volume = S.VoiceVolume / 100.0;
        VoiceVolumeText.Text = $"{S.VoiceVolume}%";
        SaveSettings();
    }

    void VoiceVolumeReset_Click(object s, System.Windows.Input.MouseButtonEventArgs e) => VoiceVolume.Value = 100;

    void VoiceRate_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (S == null || VoiceRateText == null) return;
        S.VoiceRate = Math.Round(VoiceRate.Value, 1);
        VoiceRateText.Text = $"{S.VoiceRate:0.0}×";
        SaveSettings();
    }

    void VoiceTest_Click(object s, RoutedEventArgs e) => ToggleRead(VoiceSample, SampleText(CurrentVoiceId));

    void WindowsVoices_Click(object s, RoutedEventArgs e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });

    string SampleText(string voice)
    {
        var name = S.Profile?.Name is { Length: > 0 } n ? n.Split(' ')[0] : "there";
        if (voice.StartsWith("win:")) voice = Voice.All().FirstOrDefault(v => v.Id == voice) is { Language: "Hindi" } ? "x:h" : "";
        return voice.EndsWith("@en") ? $"Hi {name}! This is how I'll read your replies and briefings to you."
             : voice.Contains(":h") ? $"नमस्ते {name}! मैं आपकी आवाज़ हूँ। ऐसे मैं आपके जवाब पढ़कर सुनाऊँगी।"
             : voice.Contains(":e") ? $"¡Hola {name}! Así es como leeré tus respuestas."
             : voice.Contains(":f") ? $"Bonjour {name} ! Voici comment je lirai tes réponses."
             : voice.Contains(":i") ? $"Ciao {name}! Ecco come leggerò le tue risposte."
             : voice.Contains(":p") ? $"Olá {name}! É assim que vou ler as suas respostas."
             : $"Hi {name}! This is how I'll read your replies and briefings to you.";
    }

    // ---------- Windows' voices ----------
    void DrawWindowsVoices(List<Voice.Entry> all)
    {
        loading = true;
        WinVoicePick.ItemsSource = all.Where(v => !v.Natural)
            .Select(v => new ComboBoxItem { Content = string.Join("  ·  ", new[] { v.Name, v.Accent == "" ? v.Language : $"{v.Language} ({v.Accent})", v.Gender }), Tag = v.Id }).ToList();
        var id = CurrentVoiceId.StartsWith("win:") ? CurrentVoiceId : Voice.Default();
        WinVoicePick.SelectedItem = WinVoicePick.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == id);
        loading = false;
        WinVoicePick_Changed(this, null!);
    }

    string? PickedWindowsVoice => (WinVoicePick.SelectedItem as ComboBoxItem)?.Tag as string;

    void WinVoicePick_Changed(object s, SelectionChangedEventArgs e)
    {
        var inUse = PickedWindowsVoice == CurrentVoiceId;
        WinVoiceUse.IsEnabled = PickedWindowsVoice != null && !inUse;
        WinVoiceUse.Content = inUse ? "In use" : "Use";
    }

    void WinVoiceUse_Click(object s, RoutedEventArgs e)
    {
        if (PickedWindowsVoice is not { } id) return;
        S.Voice = id; SaveSettings(); DrawVoices();
    }

    void WinVoicePlay_Click(object s, RoutedEventArgs e) { if (PickedWindowsVoice is { } id) ToggleRead(WinVoicePlay, SampleText(id), id); }

    // ---------- the natural voice packages ----------
    bool packBusy;

    void DrawPacks()
    {
        PackList.Children.Clear();
        foreach (var pack in Voice.Packs)
        {
            var have = Voice.Has(pack);
            var title = new TextBlock { FontSize = 13.5 };
            title.Inlines.Add(new System.Windows.Documents.Run(pack.Name) { FontWeight = FontWeights.SemiBold });
            title.Inlines.Add(new System.Windows.Documents.Run($"   {pack.Mb:0} MB") { FontSize = 12.5, Foreground = (Brush)FindResource("Dim") });
            if (have) title.Inlines.Add(new System.Windows.Documents.Run("   ✓ downloaded") { FontSize = 12.5, Foreground = Brushes.MediumSeaGreen });
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var note = new TextBlock { Text = pack.Note, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var bar = new ProgressBar { Height = 3, Maximum = 1, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            var btn = new Button { Content = have ? "Remove" : "Download", Padding = new Thickness(14, 5, 14, 5), MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !packBusy };
            if (!have) btn.Style = (Style)FindResource("Shimmer");
            btn.Click += async (_, _) =>
            {
                if (have) { await RemovePack(pack); return; }
                packBusy = true; foreach (var b in PackList.Children.OfType<Border>().Select(x => ((DockPanel)x.Child).Children[0])) ((Button)b).IsEnabled = false;
                bar.Visibility = Visibility.Visible;
                try
                {
                    await Voice.DownloadKokoro(pack, new Progress<double>(x => { bar.Value = x; btn.Content = x < 0.94 ? $"{x:0%}" : "Unpacking…"; }));
                    if (!S.Voice.StartsWith("kokoro:")) { S.Voice = "kokoro:af_heart"; SaveSettings(); }
                    packBusy = false; DrawVoices();
                    ToggleRead(VoiceSample, SampleText(S.Voice));
                }
                catch (Exception ex) { packBusy = false; DrawVoices(); _ = Notice("Couldn't get the natural voices", ex.Message); }
            };
            DockPanel.SetDock(btn, Dock.Right);
            var row = new Border { Padding = new Thickness(12, 10, 10, 10), Margin = new Thickness(0, 6, 0, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                                   Child = new DockPanel { Children = { btn, new StackPanel { Margin = new Thickness(0, 0, 10, 0), Children = { title, note, bar } } } } };
            row.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
            row.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            PackList.Children.Add(row);
        }
    }

    async Task RemovePack(Voice.KokoroPack pack)
    {
        if (!await Confirm($"Remove {pack.Name.ToLower()}?", $"Frees about {pack.Mb:0} MB.", "Remove", danger: true)) return;
        Voice.Stop();
        try { Voice.RemoveKokoro(pack); } catch (Exception ex) { _ = Notice("Couldn't remove it", ex.Message); }
        if (!Voice.KokoroInstalled && S.Voice.StartsWith("kokoro:")) { S.Voice = ""; SaveSettings(); }
        DrawVoices();
    }

    // ---------- the natural voices: filters, then the list (only once filtered or searched) ----------
    void DrawVoices()
    {
        var all = Voice.All();
        DrawCurrentVoice(all);
        DrawWindowsVoices(all);
        DrawPacks();
        NaturalGroup.Visibility = Voice.KokoroInstalled ? Visibility.Visible : Visibility.Collapsed;
        if (!Voice.KokoroInstalled) return;

        var natural = all.Where(v => v.Natural).ToList();
        // each list offers what the others leave, so nothing leads nowhere; "All …" = no filter
        bool L(Voice.Entry v) => voiceLang == "All languages" || v.Language == voiceLang;
        bool A(Voice.Entry v) => voiceAccent == "All accents" || v.Accent == voiceAccent;
        bool G(Voice.Entry v) => voiceGender == "Any gender" || v.Gender == voiceGender;
        loading = true;
        Fill(VoiceLangPick, "All languages", natural.Select(v => v.Language).Distinct().OrderBy(l => l == "English" ? "" : l == "Hindi" ? " " : l), voiceLang);
        Fill(VoiceAccentPick, "All accents", natural.Where(L).Select(v => v.Accent).Distinct().OrderBy(a => a), voiceAccent);
        Fill(VoiceGenderPick, "Any gender", ["Female", "Male"], voiceGender);
        VoiceSearchHint.Visibility = VoiceSearch.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        loading = false;
        voiceLang = (string)VoiceLangPick.SelectedItem; voiceAccent = (string)VoiceAccentPick.SelectedItem; voiceGender = (string)VoiceGenderPick.SelectedItem;
        var q = VoiceSearch.Text.Trim();
        var filtered = voiceLang != "All languages" || voiceAccent != "All accents" || voiceGender != "Any gender";
        VoiceClear.Visibility = filtered || voiceSearched ? Visibility.Visible : Visibility.Collapsed;
        VoiceList.Children.Clear();
        if (!filtered && !voiceSearched)
        {
            VoiceCount.Text = $"{natural.Count} voices";
            VoiceList.Children.Add(new TextBlock { Text = "Filter or search to see voices.", FontSize = 12, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(2, 0, 0, 0) });
            return;
        }
        var shown = natural.Where(v => L(v) && A(v) && G(v) && (q == "" || $"{v.Name} {v.Language} {v.Accent} {v.Gender} {v.Detail}".Contains(q, StringComparison.OrdinalIgnoreCase)))
                           .OrderByDescending(v => v.Id == CurrentVoiceId).ThenBy(v => v.Language == "English" ? "" : v.Language == "Hindi" ? " " : v.Language)
                           .ThenBy(v => v.Accent).ThenBy(v => v.Name).ToList();
        VoiceCount.Text = $"{shown.Count} of {natural.Count} voices";
        if (shown.Count == 0)
            VoiceList.Children.Add(new TextBlock { Text = "No voices match.", FontSize = 12, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(2, 0, 0, 0) });
        foreach (var v in shown) VoiceList.Children.Add(VoiceRow(v));
    }

    static void Fill(ComboBox box, string all, IEnumerable<string> values, string selected)
    {
        var list = new[] { all }.Concat(values).ToList();
        box.ItemsSource = list;
        box.SelectedItem = list.Contains(selected) ? selected : all;
    }

    void VoiceFilter_Changed(object s, SelectionChangedEventArgs e)
    {
        if (loading || s is not ComboBox { SelectedItem: string v } box) return;
        if (box == VoiceLangPick) voiceLang = v; else if (box == VoiceAccentPick) voiceAccent = v; else voiceGender = v;
        DrawVoices();
    }

    // the list follows the search once it's been run
    void VoiceSearch_Changed(object s, TextChangedEventArgs e) { if (!loading && VoiceList != null) { if (VoiceSearch.Text == "") voiceSearched = false; DrawVoices(); } }
    void VoiceSearch_KeyDown(object s, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) VoiceSearchGo_Click(s, e); }
    void VoiceSearchGo_Click(object s, RoutedEventArgs e) { voiceSearched = true; DrawVoices(); }

    void VoiceClear_Click(object s, RoutedEventArgs e)
    {
        (voiceLang, voiceAccent, voiceGender, voiceSearched) = ("All languages", "All accents", "Any gender", false);
        loading = true; VoiceSearch.Text = ""; loading = false;
        DrawVoices();
    }

    FrameworkElement VoiceRow(Voice.Entry v)
    {
        var current = v.Id == CurrentVoiceId;
        var name = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        name.Inlines.Add(new System.Windows.Documents.Run(v.Name) { FontWeight = FontWeights.SemiBold });
        if (current) name.Inlines.Add(new System.Windows.Documents.Run("   in use") { Foreground = Shell.B("#A78BFA"), FontSize = 12 });
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var detail = new TextBlock { Text = Describe(v, kind: false) + (!v.Installed && v.Size > 0 ? $"  ·  {v.Size:0} MB" : ""), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var bar = new ProgressBar { Height = 3, Maximum = 1, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Btn(string text, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 64 };
            if (primary) b.Style = (Style)FindResource("Shimmer");
            actions.Children.Add(b);
            return b;
        }
        if (v.Installed)
        {
            var play = new Button { Content = "", FontFamily = App.IconFont, FontSize = 15, Style = (Style)FindResource("Ghost"), ToolTip = "Preview" };
            play.Click += (_, _) => ToggleRead(play, SampleText(v.Id), v.Id);
            actions.Children.Add(play);
            if (!current) Btn("Use", primary: true).Click += (_, _) => { S.Voice = v.Id; SaveSettings(); Voice.Warm(v.Id); DrawVoices(); };
        }
        DockPanel.SetDock(actions, Dock.Right);
        var row = new Border { Padding = new Thickness(12, 9, 8, 9), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                               Child = new DockPanel { Children = { actions, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, detail, bar } } } } };
        if (current) row.BorderBrush = Shell.B("#A78BFA"); else row.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        row.SetResourceReference(Border.BackgroundProperty, current ? "Glass" : "PopupBg");
        return row;
    }
}
