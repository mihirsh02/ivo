using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ivo;

// Browse models (Settings → Assistant → Smarter chat), one of Ivo's panels, in the look of the model picker: a search
// box, the models grouped by family, each a row (a dot for how it suits this PC, its name, its size and speed, "in use"
// or "downloaded"). A click opens the row: what it's good at, what it needs against what this PC has (heavy ones say so),
// and Download or Use it.
public partial class MainWindow
{
    string? storeOpen;   // the row opened

    void ModelStore()
    {
        var search = new TextBox { Padding = new Thickness(10, 7, 10, 7), FontSize = 13, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        search.SetResourceReference(Control.ForegroundProperty, "Fg"); search.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        var hint = new TextBlock { Text = "Search models", FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var glass = new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) };
        glass.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var searchRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(glass, Dock.Left); searchRow.Children.Add(glass); searchRow.Children.Add(new Grid { Children = { search, hint } });
        var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        var list = new StackPanel { Margin = new Thickness(6, 4, 6, 10) };
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12, 4, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        TextBlock Dim(string t, double size = 12) { var x = new TextBlock { Text = t, FontSize = size, VerticalAlignment = VerticalAlignment.Center }; x.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); return x; }
        static string Family(IvoModel.Offer o) => o.Name.StartsWith("Qwen2.5 Coder") ? "QWEN CODER" : o.Name.StartsWith("Qwen") ? "QWEN" : o.Name.StartsWith("SmolLM") ? "SMOLLM" : o.Name.Contains("Hermes") ? "HERMES" : "MORE";
        static string Size(double mb) => mb >= 1000 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

        void Draw()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            var shown = IvoModel.Catalog.Where(IvoModel.Fits).Where(o => q == "" || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || o.About.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var g in shown.GroupBy(Family))
            {
                var head = Dim(g.Key, 11); head.FontWeight = FontWeights.SemiBold; head.Margin = new Thickness(10, 10, 0, 4);
                list.Children.Add(head);
                foreach (var o in g) list.Children.Add(Row(o));
            }
            if (shown.Count == 0) { var none = Dim(q == "" ? "No models fit this PC." : "No match."); none.Margin = new Thickness(10, 10, 0, 0); list.Children.Add(none); }
            var hidden = IvoModel.Catalog.Count(o => !IvoModel.Fits(o));
            note.Text = (hidden > 0 ? $"{hidden} bigger model{(hidden == 1 ? " isn't" : "s aren't")} listed: more than this PC's memory with 2 GB to spare. " : "")
                      + "From Hugging Face; they run on this PC. Trained your own? Add your own model… in Smarter chat.";
        }

        FrameworkElement Row(IvoModel.Offer o)
        {
            var file = System.IO.Path.Combine(IvoModel.Dir, o.File);
            var have = System.IO.File.Exists(file);
            var inUse = have && IvoModel.Using && IvoModel.File == file;
            var tight = IvoModel.Tight(o);
            // the dot: green suits this PC, amber is heavy or short of free memory right now
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
                                    Fill = Shell.B(o.Heavy || tight ? "#F59E0B" : "#22C55E"), ToolTip = o.Heavy ? "Heavy for a laptop" : tight ? "Short of free memory right now" : "Suits this PC" };
            var name = new TextBlock { Text = o.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var tag = Dim(inUse ? "in use" : have ? "downloaded" : $"{Size(o.SizeMb)} · {o.Speed.Split('·')[0].Trim()}"); tag.Margin = new Thickness(8, 0, 6, 0);
            if (inUse) tag.Foreground = Shell.B("#22C55E");
            var star = new TextBlock { Text = inUse ? "" : "", FontFamily = App.IconFont, FontSize = 12, Foreground = Shell.B("#C4B5FD"), VerticalAlignment = VerticalAlignment.Center, Width = 18 };
            var head = new DockPanel { Background = Brushes.Transparent };
            DockPanel.SetDock(dot, Dock.Left); DockPanel.SetDock(star, Dock.Right); DockPanel.SetDock(tag, Dock.Right);
            head.Children.Add(dot); head.Children.Add(star); head.Children.Add(tag); head.Children.Add(name);

            var open = storeOpen == o.File;
            var body = new StackPanel { Children = { head } };
            if (open)
            {
                // the details: what it's for, what it needs against this PC, and the button
                var about = Dim(o.About, 12.5); about.TextWrapping = TextWrapping.Wrap; about.Margin = new Thickness(15, 8, 0, 0); about.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                var needs = Dim($"{Size(o.SizeMb)} download · needs about {o.MemoryGb:0.#} GB of memory and {o.Cores}+ CPU cores · {o.Speed} on a laptop", 11.5);
                needs.TextWrapping = TextWrapping.Wrap; needs.Margin = new Thickness(15, 4, 0, 0);
                body.Children.Add(about); body.Children.Add(needs);
                if (o.Heavy || tight)
                {
                    var warn = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(15, 6, 0, 0), Foreground = Shell.B("#F59E0B"),
                                               Text = (o.Heavy ? $"⚠ Heavy: while she replies it takes about {o.MemoryGb:0.#} GB and keeps {o.Cores}+ cores busy (this PC: {IvoModel.TotalGb():0} GB, {Environment.ProcessorCount} cores). " : "")
                                                    + (tight ? $"Only {IvoModel.FreeGb():0.#} GB is free right now: close some apps first, so 2 GB stays free." : "") };
                    body.Children.Add(warn);
                }
                var bar = new ProgressBar { Height = 4, Margin = new Thickness(15, 8, 0, 0), Visibility = Visibility.Collapsed };
                var act = new Button { Content = inUse ? "In use" : have ? "Use it" : $"Download ({Size(o.SizeMb)})", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(15, 10, 0, 4),
                                       HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !inUse };
                if (!inUse) act.Style = (Style)FindResource("Shimmer");
                act.Click += async (_, _) =>
                {
                    if (!have)
                    {
                        act.IsEnabled = false; bar.Visibility = Visibility.Visible;
                        try { await IvoModel.Download(o, new Progress<double>(p => { bar.Value = p * 100; act.Content = $"Downloading… {p:P0}"; }), CancellationToken.None); }
                        catch (Exception ex) { _ = Notice("Couldn't download it", ex.Message); Draw(); return; }
                    }
                    IvoModel.Unload();
                    S.SmarterChatFile = IvoModel.Custom = file == IvoModel.Default ? "" : file; S.SmarterChat = IvoModel.On = true;
                    SaveSettings(); DrawAssistantCard(); Draw();
                };
                body.Children.Add(bar); body.Children.Add(act);
            }
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 4, 6), Cursor = Cursors.Hand, Child = body, Background = Brushes.Transparent };
            if (open) row.SetResourceReference(Border.BackgroundProperty, "InputBg");
            row.MouseEnter += (_, _) => { if (!open) row.SetResourceReference(Border.BackgroundProperty, "Glass"); };
            row.MouseLeave += (_, _) => { if (!open) row.Background = Brushes.Transparent; };
            head.MouseLeftButtonUp += (_, _) => { storeOpen = open ? null : o.File; Draw(); };
            return row;
        }

        search.TextChanged += (_, _) => { hint.Visibility = search.Text == "" ? Visibility.Visible : Visibility.Collapsed; Draw(); };
        Draw();
        // the picker's look: one rounded box, the search on top, a line, the list
        var inner = new DockPanel();
        DockPanel.SetDock(searchRow, Dock.Top); DockPanel.SetDock(line, Dock.Top);
        inner.Children.Add(searchRow); inner.Children.Add(line); inner.Children.Add(list);
        var box = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = inner };
        box.SetResourceReference(Border.BackgroundProperty, "PopupBg"); box.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        OpenFromSettings("models", "Models for Ivo", "", "#22D3EE", new StackPanel { MaxWidth = 760, Children = { box, note } });
        search.Focus();
    }
}
