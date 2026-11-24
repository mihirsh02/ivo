using System.Windows;
using System.Windows.Controls;

namespace Ivo;

// Settings → Voice → Audio → Sounds: one volume for all of Ivo's sounds, then one for each kind (0 = off), each with a
// Try it that plays it at that volume (Sounds.Kinds lists them).
public partial class MainWindow
{
    void DrawSounds()
    {
        Sounds.Level = k => k == "master" ? S.SoundVolume : PortalDevices.Silenced(k) ? 0 : S.SoundLevels.TryGetValue(k, out var v) ? v : 100;
        SoundRows.Children.Clear();
        SoundRows.Children.Add(SoundRow("All sounds", "", S.SoundVolume, v => S.SoundVolume = v, () => Sounds.Chime(force: true)));
        foreach (var (kind, name, note) in Sounds.Kinds)
            SoundRows.Children.Add(SoundRow(name, note, Sounds.Level(kind), v => S.SoundLevels[kind] = v, () => TrySound(kind)));
    }

    static void TrySound(string kind)
    {
        switch (kind)
        {
            case "call": Sounds.Ring(force: true); _ = Task.Delay(3000).ContinueWith(_ => Sounds.StopRing()); break;
            case "wheel": Sounds.Snap(force: true); break;
            default: Sounds.Chime(kind, force: true); break;
        }
    }

    FrameworkElement SoundRow(string name, string note, int value, Action<int> set, Action tryIt)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        label.Inlines.Add(name);
        if (note != "") { var dim = new System.Windows.Documents.Run(" · " + note); dim.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Dim"); label.Inlines.Add(dim); }
        var level = new TextBlock { Width = 36, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Text = value == 0 ? "Off" : $"{value}%" };
        level.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = value, Width = 120, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 5,
                                  ToolTip = name + " volume · all the way left: off" };
        slider.ValueChanged += (_, e) => { var v = (int)e.NewValue; set(v); level.Text = v == 0 ? "Off" : $"{v}%"; SaveSettings(); };
        var play = new Button { Content = "\uE767", Style = (Style)FindResource("Ghost"), FontSize = 15, ToolTip = "Try it", VerticalAlignment = VerticalAlignment.Center };
        play.Click += (_, _) => tryIt();
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var right in new FrameworkElement[] { play, level, slider }) { DockPanel.SetDock(right, Dock.Right); row.Children.Add(right); }
        row.Children.Add(label);
        return row;
    }
}
