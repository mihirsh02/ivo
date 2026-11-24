using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ivo;

// The chat's model picker for places outside a chat (Tasks, chat defaults): a provider field and a model field, each opening a
// list under it; the model list searches, puts pinned models first, groups by maker, and shows status, fast/slow and pins.
// Value is "provider/model"; "" means the empty choice (e.g. "same as chats"), when one is offered.
public class ModelChooser : StackPanel
{
    readonly MainWindow main;
    readonly string? emptyText;
    string? provider, model;
    readonly Button providerField = Field(), modelField = Field();
    readonly Border providerBox = Box(), modelBox = Box();
    readonly ListBox providers = List(220), models = List(240);
    readonly TextBox search = new() { Padding = new Thickness(10, 7, 10, 7), FontSize = 13, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    public event Action<string>? Changed;

    public string Value => provider == null ? "" : $"{provider}/{model}";

    public ModelChooser(MainWindow main, string value, string? emptyText = null)
    {
        this.main = main; this.emptyText = emptyText;
        if (Split(main.S, value) is var (p, m)) { provider = p; model = m; }
        search.SetResourceReference(Control.ForegroundProperty, "Fg"); search.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        search.TextChanged += (_, _) => DrawModels();
        main.SizeChanged += (_, _) => FitListsToWindow();
        search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && search.Text.Trim() is { Length: > 0 } q) { e.Handled = true; Use(models.Items.OfType<ListBoxItem>().FirstOrDefault()?.Tag as string ?? q); } };
        var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
        providerBox.Child = providers;
        modelBox.Child = new DockPanel { Children = { Top(search), Top(line), models } };
        providerField.Click += (_, _) => Open(providerBox, providerBox.Visibility != Visibility.Visible);
        modelField.Click += (_, _) => Open(modelBox, modelBox.Visibility != Visibility.Visible);
        modelField.Margin = new Thickness(0, 8, 0, 0);
        providers.SelectionChanged += (_, _) =>
        {
            if (providers.SelectedItem is not ListBoxItem { Tag: string p } || p == (provider ?? "")) return;
            provider = p == "" ? null : p; model = provider == null ? null : main.DefaultModel(provider, main.ModelCache.GetValueOrDefault(provider));
            if (provider == Router.Name) model = Router.Routes[1];
            Open(providerBox, false); Draw(); Changed?.Invoke(Value);
        };
        models.SelectionChanged += (_, _) => { if (models.SelectedItem is ListBoxItem { Tag: string id } && id != model) Use(id); };
        Children.Add(providerField); Children.Add(providerBox); Children.Add(modelField); Children.Add(modelBox);
        Draw();
    }

    // "AI/ML API/openai/gpt-5" → the longest provider name that fits, then the model
    public static (string, string)? Split(Settings s, string value)
    {
        if (value == "") return null;
        var names = s.Providers.Select(p => p.Name).Append(Router.Name).OrderByDescending(n => n.Length);
        return names.FirstOrDefault(n => value.StartsWith(n + "/")) is { } n ? (n, value[(n.Length + 1)..]) : null;
    }

    void Use(string id) { model = id; search.Text = ""; Open(modelBox, false); Draw(); Changed?.Invoke(Value); }

    void Open(Border box, bool open)
    {
        providerBox.Visibility = modelBox.Visibility = Visibility.Collapsed;
        box.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            FitListsToWindow();
            if (box == modelBox) { search.Text = ""; search.Focus(); DrawModels(); }
        }
    }

    void FitListsToWindow()
    {
        if (!IsLoaded || main.ActualHeight <= 0) return;
        var field = modelBox.Visibility == Visibility.Visible ? modelField : providerField;
        var list = modelBox.Visibility == Visibility.Visible ? models : providers;
        var bottom = field.TranslatePoint(new Point(0, field.ActualHeight), main).Y;
        list.MaxHeight = Math.Clamp(main.ActualHeight - bottom - 72, 80, 240);
    }

    void Draw()
    {
        var cfg = provider == Router.Name ? Router.Cfg : main.S.Providers.FirstOrDefault(p => p.Name == provider);
        if (provider != null && cfg == null) provider = model = null;   // removed since
        ((TextBlock)((DockPanel)providerField.Content).Children[1]).Text = provider ?? emptyText ?? "Choose a provider";
        ((TextBlock)((DockPanel)modelField.Content).Children[1]).Text = model == null ? "" : model[(model.LastIndexOf('/') + 1)..];
        modelField.Visibility = provider == null ? Visibility.Collapsed : Visibility.Visible;
        providers.Items.Clear();
        if (emptyText != null) providers.Items.Add(Row(emptyText, ""));
        if (main.S.AutoOn) providers.Items.Add(Row(Router.Name, Router.Name));
        foreach (var p in main.S.Providers) providers.Items.Add(Row(p.Name, p.Name));
        providers.SelectedItem = providers.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == (provider ?? ""));
        if (cfg != null && !main.ModelCache.ContainsKey(cfg.Name) && cfg.Kind != "auto") _ = Load(cfg);
    }

    async Task Load(ProviderCfg cfg)
    {
        try { var live = await Api.ListModels(cfg, Store.Unprotect(cfg.Key)); if (live.Count > 0) main.ModelCache[cfg.Name] = live; } catch { }
        if (provider == cfg.Name) DrawModels();
    }

    // the same rules as a chat's list: only working ones (once any are known), pinned first, grouped by maker
    void DrawModels()
    {
        if (provider == null) return;
        var s = main.S;
        var cfg = provider == Router.Name ? Router.Cfg : s.Providers.FirstOrDefault(p => p.Name == provider);
        var ids = (main.ModelCache.GetValueOrDefault(provider) ?? (IEnumerable<string>)Api.ModelsFor(cfg)).Append(model).OfType<string>().Distinct();
        var items = ids.Select(id => new ModelItem(id, s.Pinned.Contains($"{provider}|{id}"), cfg?.Kind == "auto" || Router.Virtual.Contains(id) ? "ok" : s.ModelStatus.GetValueOrDefault($"{provider}|{id}"), false, Cli.Speed(provider, id), InUse: id == model)).ToList();
        if (cfg?.OnlyWorking != false && items.Any(m => m.Status == "ok")) items = items.Where(m => m.Status == "ok" || m.Pinned || m.Id == model).ToList();
        var q = search.Text.Trim();
        if (q != "") items = items.Where(m => m.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        models.Items.Clear();
        var groups = items.OrderByDescending(m => m.Pinned).ThenBy(m => m.Vendor, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Short, StringComparer.OrdinalIgnoreCase).GroupBy(m => m.Group).ToList();
        foreach (var g in groups)
        {
            if (groups.Count > 1)
            {
                var head = new TextBlock { Text = g.Key, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 8, 0, 4) };
                head.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                models.Items.Add(new ListBoxItem { Content = head, IsEnabled = false, Focusable = false });
            }
            foreach (var m in g) models.Items.Add(ModelRow(m));
        }
        if (models.Items.Count == 0) models.Items.Add(new ListBoxItem { Content = Dim(q == "" ? "No models." : $"No match. Enter uses “{q}”."), IsEnabled = false });
        models.SelectedItem = models.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == model);
    }

    ListBoxItem ModelRow(ModelItem m)
    {
        var pin = new TextBlock { Text = m.PinGlyph, FontFamily = App.IconFont, FontSize = 14, Opacity = m.Pinned ? 1 : 0, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = m.PinTip };
        if (m.Pinned) pin.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD)); else pin.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        pin.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!main.S.Pinned.Remove($"{provider}|{m.Id}")) main.S.Pinned.Add($"{provider}|{m.Id}");
            main.SaveSettings(); DrawModels();
        };
        var speed = Dim(m.SpeedText); speed.Margin = new Thickness(6, 0, 2, 0);
        var name = new TextBlock { Text = m.Short, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = m.Tip };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 9, 0), Fill = m.Dot, ToolTip = m.StatusText, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { Children = { pin, speed, dot, name } };
        DockPanel.SetDock(pin, Dock.Right); DockPanel.SetDock(speed, Dock.Right); DockPanel.SetDock(dot, Dock.Left);
        var item = new ListBoxItem { Content = row, Tag = m.Id, Padding = new Thickness(10, 5, 4, 5) };
        item.MouseEnter += (_, _) => { if (!m.Pinned) pin.Opacity = 0.8; };
        item.MouseLeave += (_, _) => { if (!m.Pinned) pin.Opacity = 0; };
        return item;
    }

    ListBoxItem Row(string text, string tag)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        return new ListBoxItem { Content = t, Tag = tag, Padding = new Thickness(10, 5, 10, 5) };
    }

    static TextBlock Dim(string text) { var t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; t.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); return t; }
    static T Top<T>(T e) where T : UIElement { DockPanel.SetDock(e, Dock.Top); return e; }

    static Button Field()
    {
        var chevron = new TextBlock { Text = "", FontFamily = App.IconFont, FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(chevron, Dock.Right);
        return new Button { Height = 32, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 10, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            Content = new DockPanel { Children = { chevron, new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } } } };
    }
    // the chat list's rows: rounded, a soft hover, a stronger fill when chosen
    static readonly Style RowStyle = (Style)System.Windows.Markup.XamlReader.Parse(
        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'><Setter Property='Cursor' Value='Hand' /><Setter Property='Template'><Setter.Value>" +
        "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='Bd' CornerRadius='6' Padding='{TemplateBinding Padding}' Background='Transparent'><ContentPresenter /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource Glass}' /></Trigger>" +
        "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource GlassStroke}' /></Trigger></ControlTemplate.Triggers></ControlTemplate>" +
        "</Setter.Value></Setter></Style>");

    static Border Box()
    {
        var b = new Border { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0) };
        b.SetResourceReference(Border.BorderBrushProperty, "GlassStroke"); b.SetResourceReference(Border.BackgroundProperty, "CodeBg");
        return b;
    }
    static ListBox List(double max)
    {
        var l = new ListBox { MaxHeight = max, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4) };
        ScrollViewer.SetHorizontalScrollBarVisibility(l, ScrollBarVisibility.Disabled);
        l.ItemContainerStyle = RowStyle;
        return l;
    }
}
