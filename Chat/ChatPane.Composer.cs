using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace Ivo;

// The message box: pasting code, saved prompts (/name), attachments, camera and voice.
public partial class ChatPane
{
    // ---------- pasting code ----------
    // Code or a command pasted into the box gets fenced with its language, so it is sent (and shown) as code.
    void Input_Pasting(object s, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text || !Code.LooksLikeCode(text)) return;
        e.CancelCommand();
        var lang = Code.Language(text) ?? "";
        var at = Input.SelectionStart;
        var lead = at > 0 && Input.Text[at - 1] != '\n' ? "\n" : "";
        var block = $"{lead}```{lang}\n{text.Trim('\r', '\n')}\n```\n";
        Input.SelectedText = block;
        Input.CaretIndex = at + block.Length;
        PasteHint.Text = lang == "" ? "Pasted as code" : $"Pasted as {LangName(lang)} code";
        PasteHint.Visibility = Visibility.Visible;
    }
    void Input_TextChanged(object s, TextChangedEventArgs e)
    {
        if (!Input.Text.Contains("```")) PasteHint.Visibility = Visibility.Collapsed;
        InputHint.Visibility = Input.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        UpdateSlash();
    }

    // ---------- saved prompts: type /name in the message box ----------
    void UpdateSlash()
    {
        var t = Input.Text;
        var q = t.StartsWith('/') && !t.Any(char.IsWhiteSpace) ? t[1..] : null;
        List<SavedPrompt> hits = q == null ? [] : [.. new[] { new SavedPrompt("compact", "/compact"), new SavedPrompt("clip", "/clip ") }.Concat(main.S.Prompts)
                                                                .Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                                                .OrderBy(p => !p.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Name)];
        SlashList.ItemsSource = hits;
        SlashMenu.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hits.Count > 0) SlashList.SelectedIndex = 0;
    }
    void UseSlash(SavedPrompt p)
    {
        SlashMenu.Visibility = Visibility.Collapsed;
        Input.Text = p.Text;
        Input.Focus(); Input.CaretIndex = Input.Text.Length;
    }
    void SlashList_Click(object s, MouseButtonEventArgs e) { if (SlashList.SelectedItem is SavedPrompt p) UseSlash(p); }

    static string LangName(string lang) => lang switch
    {
        "csharp" => "C#", "cpp" => "C++", "javascript" => "JavaScript", "typescript" => "TypeScript", "powershell" => "PowerShell",
        "json" or "html" or "xml" or "css" or "sql" or "yaml" => lang.ToUpperInvariant(), "shell" or "sh" => "Shell", "output" => "Output", "" => "Code",
        _ => char.ToUpperInvariant(lang[0]) + lang[1..],
    };

    public static Button SmallButton(string glyph, string tip) => new()
    {
        Content = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 14 },
        ToolTip = tip, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
    };

    // A "Copy" button that says "Copied" for a moment; copy puts whatever it is on the clipboard.
    static Button CopyButton(string tip, Action copy, string glyph = "\uE8C8", string text = "Copy", string done = "Copied")
    {
        var icon = new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var word = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var b = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, word } },
            ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Cursor = Cursors.Hand,
        };
        b.Click += async (_, _) =>
        {
            try { copy(); } catch { return; }   // the clipboard can be busy, or a dialog cancelled
            icon.Text = "\uE73E"; word.Text = done;
            await Task.Delay(1500);
            icon.Text = glyph; word.Text = text;
        };
        return b;
    }

    UIElement CodeBlock(string code, string lang = "")
    {
        var label = new TextBlock { Text = LangName(lang), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var copy = CopyButton("Copy code", () => Clipboard.SetText(code));
        DockPanel.SetDock(copy, Dock.Right);
        var bar = new DockPanel { Margin = new Thickness(14, 6, 6, 6), Children = { copy, label } };
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        var box = Highlight.View(code, lang);
        var border = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 280, Child = new StackPanel { Children = { bar, line, box } } };
        border.SetResourceReference(Border.BackgroundProperty, "CodeBg");
        border.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        return border;
    }

    // A file change as coloured lines: green added, red removed, grey context.
    UIElement DiffBlock(List<(char Kind, string Line)> diff)
    {
        int add = diff.Count(d => d.Kind == '+'), del = diff.Count(d => d.Kind == '-');
        var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinHeight = 22, Padding = new Thickness(0, 3, 0, 0) };
        label.Inlines.Add(new Run($"+{add}") { Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) });
        label.Inlines.Add(new Run($"  −{del}") { Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)) });
        label.Inlines.Add(new Run("  lines · changes to apply"));
        label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var rows = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        var added = new SolidColorBrush(Color.FromArgb(0x30, 0x22, 0xC5, 0x5E)); var removed = new SolidColorBrush(Color.FromArgb(0x30, 0xEF, 0x44, 0x44));
        foreach (var (kind, line) in diff.Take(400))
        {
            var t = new TextBlock { Text = (kind == '…' ? "⋯  " : $"{kind}  ") + line, FontFamily = Mono, FontSize = 12, Padding = new Thickness(14, 1, 14, 1) };
            t.SetResourceReference(TextBlock.ForegroundProperty, kind is ' ' or '…' ? "Dim" : "Fg");
            if (kind == '+') t.Background = added; else if (kind == '-') t.Background = removed;
            if (kind == '…') t.FontStyle = FontStyles.Italic;
            rows.Children.Add(t);
        }
        if (diff.Count > 400) rows.Children.Add(new TextBlock { Text = $"   … {diff.Count - 400} more lines", FontFamily = Mono, FontSize = 12, Opacity = 0.6, Padding = new Thickness(14, 1, 14, 1) });
        var scroll = new ScrollViewer { Content = rows, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var bar = new DockPanel { Margin = new Thickness(14, 6, 6, 6), Children = { label } };
        var line2 = new Border { Height = 1 };
        line2.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        var border = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 6), Child = new StackPanel { Children = { bar, line2, scroll } } };
        border.SetResourceReference(Border.BackgroundProperty, "CodeBg");
        border.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        return border;
    }

    // slide + fade in, then scroll to bottom
    void AddAnimated(FrameworkElement el)
    {
        var move = new TranslateTransform(0, 20);
        el.RenderTransform = move;
        Log.Children.Add(el);
        var d = TimeSpan.FromMilliseconds(320);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
        Scroller.ScrollToEnd();
    }

    // the 3D crystal leans toward the cursor (not while it's being dragged)
    void Empty_MouseMove(object s, MouseEventArgs e)
    {
        if (crystalDrag || !main.S.OrbMotion) return;   // motion off: it doesn't follow the cursor either
        var p = e.GetPosition(Empty);
        var ease = new QuadraticEase();
        TiltX.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation((p.Y / Empty.ActualHeight - 0.5) * 50, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
        TiltZ.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(-(p.X / Empty.ActualWidth - 0.5) * 50, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
    }

    // ---------- drag the crystal: it follows the mouse, keeps the throw's speed when let go, then slows back to its idle spin.
    // A click without crystalDrag gives it a quick full turn. ----------
    bool crystalDrag, coasting;
    Point crystalLast, crystalStart;
    double spinVelocity;   // degrees per frame
    DateTime crystalTime;

    // stop an animation at the value it's showing, so the drag carries on from exactly there
    static double Hold(AxisAngleRotation3D r)
    {
        var now = r.Angle;
        r.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        r.Angle = now;
        return now;
    }

    void Crystal_Down(object s, MouseButtonEventArgs e)
    {
        StopCoast();
        Hold(Spin); Hold(TiltX); Hold(TiltZ);
        crystalDrag = true; spinVelocity = 0;
        crystalLast = crystalStart = e.GetPosition(CrystalHost); crystalTime = DateTime.Now;
        CrystalHost.CaptureMouse();
        e.Handled = true;
    }

    void Crystal_Move(object s, MouseEventArgs e)
    {
        if (!crystalDrag) return;
        var p = e.GetPosition(CrystalHost);
        var dx = p.X - crystalLast.X; var dy = p.Y - crystalLast.Y;
        Spin.Angle += dx * 1.2;
        TiltX.Angle = Math.Clamp(TiltX.Angle + dy * 0.6, -60, 60);
        var ms = Math.Max(1, (DateTime.Now - crystalTime).TotalMilliseconds);
        spinVelocity = dx * 1.2 * (16.0 / ms);   // per ~frame, from the latest movement
        crystalLast = p; crystalTime = DateTime.Now;
    }

    void Crystal_Up(object s, MouseButtonEventArgs e)
    {
        if (!crystalDrag) return;
        var moved = (e.GetPosition(CrystalHost) - crystalStart).Length;
        CrystalHost.ReleaseMouseCapture();   // also raises LostMouseCapture, which ends the drag
        if (moved < 4)   // a click: one quick full turn, then idle
        {
            var from = Spin.Angle;
            var turn = new DoubleAnimation(from, from + 360, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            turn.Completed += (_, _) => ResumeIdle();
            Spin.BeginAnimation(AxisAngleRotation3D.AngleProperty, turn);
            Bump();
        }
        else StartCoast();
    }

    void Crystal_Lost(object s, MouseEventArgs e) { crystalDrag = false; }

    void StartCoast()
    {
        if (Math.Abs(spinVelocity) < 0.5) { ResumeIdle(); return; }
        coasting = true;
        CompositionTarget.Rendering += Coast;
    }

    void Coast(object? s, EventArgs e)
    {
        Spin.Angle += spinVelocity;
        spinVelocity *= 0.96;
        TiltX.Angle *= 0.94;   // settle upright as it slows
        if (Math.Abs(spinVelocity) < 0.6) ResumeIdle();
    }

    void StopCoast()
    {
        if (!coasting) return;
        coasting = false;
        CompositionTarget.Rendering -= Coast;
    }

    // back to the slow idle spin, from wherever it stopped
    void ResumeIdle()
    {
        StopCoast();
        var from = Hold(Spin) % 360;
        Spin.BeginAnimation(AxisAngleRotation3D.AngleProperty, main.S.OrbMotion && Empty.IsVisible   // motion off: it stays where it stopped
            ? App.Loop(new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(10)) { RepeatBehavior = RepeatBehavior.Forever }) : null);
        if (!main.S.OrbMotion) Spin.Angle = from;
        TiltX.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase() });
    }

    // the halo swells for a moment on a click
    void Bump()
    {
        var swell = new DoubleAnimation(1.35, TimeSpan.FromMilliseconds(220)) { AutoReverse = true, EasingFunction = new QuadraticEase() };
        swell.Completed += (_, _) => StartAnimations();   // back to the steady pulse
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, swell);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, swell.Clone());
    }

    // ---------- attachments, camera, voice ----------
    void Attach_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Images, documents and text|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.docx;*.pdf;" + string.Join(";", TextExt.Select(x => "*" + x)) + "|All files|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        AttachFiles(dlg.FileNames);
    }

    // images go as images; anything else as text (the explorer's "Add to chat" uses this too)
    public void AttachFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp") AddPending(new(Path.GetFileName(path), Img: LoadImage(path)));
                else
                {
                    var text = Docs.Read(path);   // .docx and .pdf as their text; anything else as it is
                    if (text.Length > 100_000) text = text[..100_000] + "\n…(truncated)";
                    AddPending(new(Path.GetFileName(path), Text: text));
                }
            }
            catch (Exception ex) { _ = main.Notice("Couldn't attach", $"{Path.GetFileName(path)}: {ex.Message}"); }
        }
    }

    // Re-encode to JPEG, max 1600px on the long side, so requests stay small.
    static Img LoadImage(string path)
    {
        var src = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        double scale = Math.Min(1.0, 1600.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource bmp = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return new("image/jpeg", Convert.ToBase64String(ms.ToArray()));
    }

    // a picture from the screen clip (Ctrl+Alt+S) or anywhere else: into the message box like an attached image
    public void AttachImage(BitmapSource src)
    {
        double scale = Math.Min(1.0, 1600.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource bmp = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        AddPending(new($"Screen clip {DateTime.Now:HH.mm}", Img: new("image/jpeg", Convert.ToBase64String(ms.ToArray()))));
        FocusInput();
    }

    static BitmapImage ToBitmap(Img img)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.StreamSource = new MemoryStream(Convert.FromBase64String(img.B64));
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.EndInit();
        b.Freeze();
        return b;
    }

    void AddPending(Pending p)
    {
        pending.Add(p);
        var remove = SmallButton("", "Remove");
        remove.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (p.Img != null) row.Children.Add(new Image { Source = ToBitmap(p.Img), Height = 28, Margin = new Thickness(0, 0, 8, 0) });
        else row.Children.Add(new TextBlock { Text = "", FontFamily = App.IconFont, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = p.Name, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        row.Children.Add(remove);
        var chip = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
        chip.SetResourceReference(Border.BackgroundProperty, "Glass");
        chip.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        remove.Click += (_, _) => { pending.Remove(p); Attachments.Children.Remove(chip); };
        Attachments.Children.Add(chip);
    }

    void Skills_Click(object s, RoutedEventArgs e) => main.ShowSkillsMenu(SkillsButton);

    // Picking a skill from the menu starts the message with it, so the model loads it.
    public void UseSkill(string name)
    {
        var lead = $"Use the {name} skill: ";
        if (!Input.Text.StartsWith(lead)) Input.Text = lead + Input.Text;
        Input.Focus(); Input.CaretIndex = Input.Text.Length;
    }

    async void Camera_Click(object s, RoutedEventArgs e)
    {
        if (main.Allowed("camera") && await Camera.Capture(main) is { } photo)
        {
            AddPending(new($"photo {DateTime.Now:HH.mm.ss}.jpg", Img: photo));
            Input.Focus();
        }
    }


    // Talk instead of typing: words appear live as you speak; click again (or stay quiet a while) to stop.
    // Settings → Voice picks the listener: Windows speech recognition (falls back to Win+H when Windows can't listen),
    // or Ivo's own, offline (the first time, it offers to download the model).
    static bool fetchingDictation;
    async void Dictate_Click(object s, RoutedEventArgs e)
    {
        if (Dictation.Listening) { Dictation.Stop(); return; }
        if (!main.Allowed("microphone") || fetchingDictation) return;
        if (main.S.Dictation == "windows") { await DictateWithWindows(); return; }
        if (!Dictation.Installed)
        {
            if (!await main.Confirm("Set up voice typing?", "Ivo listens and writes on this PC; nothing is sent anywhere. It needs a one-time download of an English speech model (310 MB, about 70 MB kept).", "Download")) return;
            fetchingDictation = true;
            MicIcon.Opacity = 0.4;
            try { await Dictation.Download(new Progress<double>(p => ((Button)s).ToolTip = $"Downloading voice typing… {p:P0}")); }
            catch (Exception ex) { _ = main.Notice("Couldn't set up voice typing", ex.Message); return; }
            finally { fetchingDictation = false; MicIcon.Opacity = 1; ((Button)s).ToolTip = "Dictate"; }
        }
        var start = Input.Text.Length > 0 && !Input.Text.EndsWith(' ') ? Input.Text + " " : Input.Text;
        void Show(string words) => Dispatcher.Invoke(() => { Input.Text = start + words; Input.CaretIndex = Input.Text.Length; });
        Dictation.Start(partial: Show, final: w => { Show(w); Dispatcher.Invoke(() => start = Input.Text + " "); },
                        ended: () => Dispatcher.Invoke(() => { MicIcon.Text = ""; MicIcon.ClearValue(TextBlock.ForegroundProperty); }));
        MicIcon.Text = ""; MicIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        Input.Focus();
    }

    async Task DictateWithWindows()
    {
        var start = Input.Text.Length > 0 && !Input.Text.EndsWith(' ') ? Input.Text + " " : Input.Text;
        void Show(string words) => Dispatcher.Invoke(() => { Input.Text = start + words; Input.CaretIndex = Input.Text.Length; });
        try
        {
            await Dictation.StartWindows(partial: Show, final: w => { Show(w); start = Input.Text + " "; },
                                         ended: () => Dispatcher.Invoke(() => { MicIcon.Text = ""; MicIcon.ClearValue(TextBlock.ForegroundProperty); }));
            MicIcon.Text = ""; MicIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
            Input.Focus();
        }
        catch
        {
            Input.Focus();
            const byte Win = 0x5B, H = 0x48; const uint Up = 2;
            keybd_event(Win, 0, 0, 0); keybd_event(H, 0, 0, 0);
            keybd_event(H, 0, Up, 0); keybd_event(Win, 0, Up, 0);
        }
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
}
