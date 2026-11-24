using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ivo;

// Portal's settings, the same in Ivo and Ivo Portal: where Chats saves files, and the camera, microphone and speakers calls use.
// Changes apply at once (the next call, the next download); `saved` keeps them.
public class PortalSettingsView : StackPanel
{
    readonly ChatLook look;
    readonly Action saved;

    // bin: the contacts in Recently deleted and how to restore one (left out where there are no contacts)
    public PortalSettingsView(ChatLook look, Action saved, Action? back, (Func<IReadOnlyList<PortalContact>> List, Action<PortalContact> Restore)? bin = null)
    {
        this.look = look; this.saved = saved;
        if (back != null)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var b = IvoFrame.Round("", "Back", back, 36);   // a round bubble in the app's colours
            DockPanel.SetDock(b, Dock.Left); head.Children.Add(b);
            head.Children.Add(new TextBlock { Text = "Settings", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = look.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            Children.Add(head);
        }

        // files
        Children.Add(Group("Files people send you", Downloads()));
        // sounds
        Children.Add(Group("Sounds", new StackPanel { Children = {
            Switch("Message sound", "The chime when a message arrives", PortalDevices.MessageSound, v => PortalDevices.MessageSound = v),
            Switch("Call sound", "The ringtone, the calling tone and the chime when a call connects", PortalDevices.CallSound, v => PortalDevices.CallSound = v) } }));
        // privacy
        Children.Add(Group("Privacy", Switch("Read receipts", "Let people see when you've read their messages. Off, you won't see when they've read yours either.", PortalDevices.ReadReceipts, v => PortalDevices.ReadReceipts = v)));
        // reactions
        Children.Add(Group("Reactions", Reactions()));
        // contacts removed in the last 15 days, to bring back
        if (bin is { } deleted) { var host = new StackPanel(); Children.Add(Group("Recently deleted contacts", host)); FillBin(host, deleted); }
        // calls
        var calls = new StackPanel();
        Children.Add(Group("Calls", calls));
        _ = FillCalls(calls);
    }

    TextBlock T(string text, double size = 13, Brush? brush = null, bool bold = false) => new() { Text = text, FontSize = size, Foreground = brush ?? look.Fg, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    Border Group(string title, UIElement body) => new()
    {
        Background = look.Card, CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 16, 18, 18), Margin = new Thickness(0, 0, 0, 12),
        Child = new StackPanel { Children = { T(title, 14, bold: true), new Border { Height = 10 }, body } },
    };

    void FillBin(StackPanel into, (Func<IReadOnlyList<PortalContact>> List, Action<PortalContact> Restore) bin)
    {
        into.Children.Clear();
        into.Children.Add(T("A contact you remove waits here for 15 days. After that it's deleted for both of you, with your chat, and you can't reach each other again.", 12, look.Dim));
        into.Children.Add(new Border { Height = 10 });
        var all = bin.List();
        if (all.Count == 0) { into.Children.Add(T("Nothing here.", 13)); return; }
        foreach (var c in all.OrderBy(c => c.DaysLeft))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var restore = Small("Restore", () => { bin.Restore(c); FillBin(into, bin); });
            restore.Margin = new Thickness(10, 0, 0, 0); restore.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(restore, Dock.Right); row.Children.Add(restore);
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                T(c.Name, 13.5), T(c.DaysLeft <= 1 ? "Deleted for good tomorrow" : $"Deleted for good in {c.DaysLeft} days", 12, c.DaysLeft <= 2 ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)) : look.Dim) } });
            into.Children.Add(row);
        }
    }

    // a setting that's on or off: its name and what it does, a switch on the right
    FrameworkElement Switch(string title, string note, bool on, Action<bool> set)
    {
        var knob = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0), RenderTransform = new TranslateTransform(on ? 18 : 0, 0) };
        var track = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Background = on ? look.Accent : look.Field, Child = knob, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        DockPanel.SetDock(track, Dock.Right); row.Children.Add(track);
        row.Children.Add(new StackPanel { Children = { T(title, 13.5), T(note, 12, look.Dim) } });
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        row.MouseLeftButtonUp += (_, _) =>
        {
            on = !on; set(on); saved();
            track.Background = on ? look.Accent : look.Field;
            ((TranslateTransform)knob.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(140)));
        };
        return row;
    }

    // your six reactions: tap one to pick another emoji for it; Reset puts back the usual six
    FrameworkElement Reactions()
    {
        var slots = new StackPanel { Orientation = Orientation.Horizontal };
        void Fill()
        {
            slots.Children.Clear();
            for (var i = 0; i < 6; i++)
            {
                var at = i;
                var slot = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Margin = new Thickness(0, 0, 8, 0), Background = look.Field, Cursor = System.Windows.Input.Cursors.Hand,
                                        Child = IvoChatsView.Emoji(PortalDevices.Reactions[at], 28), ToolTip = "Change this reaction" };
                slot.MouseEnter += (_, _) => slot.Background = look.Accent; slot.MouseLeave += (_, _) => slot.Background = look.Field;
                slot.MouseLeftButtonUp += (_, _) => IvoChatsView.PickEmoji(slot, look, e =>
                {
                    var now = PortalDevices.Reactions.ToArray(); now[at] = e; PortalDevices.Reactions = now; saved(); Fill();
                });
                slots.Children.Add(slot);
            }
        }
        Fill();
        var reset = Small("Reset", () => { PortalDevices.Reactions = PortalDevices.DefaultReactions; saved(); Fill(); }, quiet: true);
        reset.Margin = new Thickness(0, 12, 0, 0); reset.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Children = { T("The emoji in a message's right-click menu. Tap one to change it.", 12, look.Dim), new Border { Height = 10 }, slots, reset } };
    }

    // a pill button (the accent, or quiet)
    Button Small(string text, Action click, bool quiet = false)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        face.SetValue(Border.BackgroundProperty, quiet ? look.Field : look.Accent);
        face.SetValue(Border.PaddingProperty, new Thickness(16, 7, 16, 8));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        face.AppendChild(cp);
        var b = new Button { Content = text, Foreground = quiet ? look.Fg : Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand,
                             Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        b.MouseEnter += (_, _) => b.Opacity = 0.85; b.MouseLeave += (_, _) => b.Opacity = 1;
        b.Click += (_, _) => click();
        return b;
    }

    FrameworkElement Downloads()
    {
        var path = T(PortalDevices.Downloads, 12.5, look.Dim); path.TextTrimming = TextTrimming.CharacterEllipsis; path.TextWrapping = TextWrapping.NoWrap; path.ToolTip = PortalDevices.Downloads; path.Margin = new Thickness(0, 0, 0, 12);
        var change = Small("Change", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where files people send you are saved", InitialDirectory = System.IO.Directory.Exists(PortalDevices.Downloads) ? PortalDevices.Downloads : null };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            PortalDevices.Downloads = dlg.FolderName; path.Text = dlg.FolderName; path.ToolTip = dlg.FolderName; saved();
        });
        var open = Small("Open", () => { System.IO.Directory.CreateDirectory(PortalDevices.Downloads); System.Diagnostics.Process.Start("explorer.exe", $"\"{PortalDevices.Downloads}\""); }, quiet: true);
        // the folder on its own line, the buttons under it
        return new StackPanel { Children = { path, new StackPanel { Orientation = Orientation.Horizontal, Children = { change, open } } } };
    }

    async Task FillCalls(StackPanel into)
    {
        var cams = await PortalDevices.Cameras();
        List<string> mics, speakers;
        try { mics = PortalDevices.Mics(); speakers = PortalDevices.Speakers(); } catch { mics = []; speakers = []; }
        into.Children.Add(Pick("Camera", ["Windows' default", .. cams.Select(c => c.Name)], cams.FindIndex(c => c.Id == PortalDevices.Camera) + 1,
                               i => PortalDevices.Camera = i == 0 ? null : cams[i - 1].Id));
        into.Children.Add(Pick("Microphone", ["Windows' default", .. mics], PortalDevices.Mic + 1, i => PortalDevices.Mic = i - 1));
        into.Children.Add(Pick("Speakers", ["Windows' default", .. speakers], PortalDevices.Speaker + 1, i => PortalDevices.Speaker = i - 1));
        into.Children.Add(T("Used from the next call.", 12, look.Dim));
    }

    // a device: its name above, then a dark rounded picker (the current one and a chevron) that opens a dark list
    FrameworkElement Pick(string label, List<string> items, int selected, Action<int> set)
    {
        var current = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        var shown = T(items.Count > 0 ? items[current] : "None found", 13.5); shown.TextTrimming = TextTrimming.CharacterEllipsis; shown.TextWrapping = TextWrapping.NoWrap; shown.VerticalAlignment = VerticalAlignment.Center;
        var chevron = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = look.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var inner = new DockPanel(); DockPanel.SetDock(chevron, Dock.Right); inner.Children.Add(chevron); inner.Children.Add(shown);
        var field = new Border { Background = look.Field, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Cursor = System.Windows.Input.Cursors.Hand, Child = inner };
        var list = new StackPanel { Margin = new Thickness(6) };
        var pop = new System.Windows.Controls.Primitives.Popup { PlacementTarget = field, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4,
                                                               Child = new Border { Background = look.Card, CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Child = list } };
        for (int i = 0; i < items.Count; i++)
        {
            var at = i;
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Cursor = System.Windows.Input.Cursors.Hand, Background = at == current ? look.Field : Brushes.Transparent,
                                   Child = T(items[at], 13.5) };
            row.MouseEnter += (_, _) => row.Background = look.Field; row.MouseLeave += (_, _) => row.Background = at == current ? look.Field : Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => { current = at; shown.Text = items[at]; pop.IsOpen = false; set(at); saved(); };
            list.Children.Add(row);
        }
        field.MouseLeftButtonUp += (_, _) => { if (items.Count == 0) return; pop.Width = field.ActualWidth; pop.IsOpen = true; };
        return new StackPanel { Margin = new Thickness(0, 0, 0, 12), Children = { T(label, 12.5, look.Dim), new Border { Height = 6 }, field } };
    }
}
