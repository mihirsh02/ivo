using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ivo;

public partial class MainWindow
{
    const string NewsGlyph = "";
    UniformGrid? newsArticles;
    StackPanel? newsCategories;
    TextBlock? newsStatus;
    ScrollViewer? newsCategoryScroll;
    Popup? newsCategoryPopup;
    News.Category? activeNewsCategory;
    int newsRequest;
    (string City, string Country)? newsPlace;
    HwndSource? newsHook;

    static FrameworkElement NewsGlyphView(Brush brush, double size = 20)
    {
        var mark = new Path
        {
            Data = Geometry.Parse("M4.5,2.5 H16.8 L20,5.7 V19 C20,20.1 19.1,21 18,21 H4.5 C3.4,21 2.5,20.1 2.5,19 V4.5 C2.5,3.4 3.4,2.5 4.5,2.5 Z M16.8,2.5 V5.7 H20 M6,7 H17 M6,8.7 H12.5 M6,11 H12 V17 H6 Z M14.3,11.5 H17 M14.3,13.4 H17 M14.3,15.3 H17 M6,19 H17"),
            Stroke = brush,
            StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent
        };
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { mark } } };
    }

    static void ScrollByWheel(ScrollViewer viewer, int delta, bool horizontal)
    {
        var offset = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        var maximum = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
        var next = Math.Clamp(offset - delta, 0, maximum);
        if (horizontal) viewer.ScrollToHorizontalOffset(next); else viewer.ScrollToVerticalOffset(next);
    }

    static void ScrollCategories(ScrollViewer viewer, int delta) => ScrollByWheel(viewer, delta, horizontal: true);

    void OpenNews_Click(object sender, RoutedEventArgs e)
    {
        if (panelKey is "news" or "news-article") { ClosePanel(); return; }
        OpenNews();
    }

    void OpenNews()
    {
        var page = new DockPanel { MaxWidth = 1100 };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = true };
        // two round icon buttons, so the tabs keep the room: edit the tabs, and refresh (just the circling arrow)
        Button? edit = null;
        edit = IvoFrame.Round("", "Edit categories", () => newsCategoryPopup!.IsOpen = !newsCategoryPopup.IsOpen, 34);
        edit.Margin = new Thickness(8, 0, 0, 0); edit.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(edit, Dock.Right);
        top.Children.Add(edit);
        var refreshButton = IvoFrame.Round("", "Refresh: load the latest stories", () => { if (activeNewsCategory != null) _ = LoadNewsCategory(activeNewsCategory, refresh: true); }, 34);
        refreshButton.Margin = new Thickness(10, 0, 0, 0); refreshButton.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(refreshButton, Dock.Right);
        top.Children.Add(refreshButton);
        newsCategories = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        newsCategoryScroll = new ScrollViewer
        {
            Content = newsCategories,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.HorizontalOnly,
            Focusable = true
        };
        // the tabs fade out at the right edge, so one running under Refresh reads as "more this way", not cut off
        newsCategoryScroll.OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Black, 0), new(Colors.Black, 0.86), new(Colors.Transparent, 1) }, 0);
        top.Children.Add(newsCategoryScroll);
        DockPanel.SetDock(top, Dock.Top);
        page.Children.Add(top);

        var status = newsStatus = new TextBlock { Text = "Finding the latest stories…", FontSize = 12, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        DockPanel.SetDock(status, Dock.Top);
        page.Children.Add(status);

        var articles = newsArticles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
        var articleList = new ScrollViewer { Content = articles, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        page.Children.Add(articleList);
        EnableNewsWheel(page, articleList, newsCategoryScroll);
        HookNewsHorizontalWheel();
        newsCategoryPopup = BuildNewsCategoryPopup(edit);
        OpenPanel("news", "News", NewsGlyph, "#38BDF8", page, scroll: false);

        var categories = News.OrderedCategories(S.NewsCategoryOrder, S.HiddenNewsCategories);
        if (activeNewsCategory == null || !categories.Any(c => c.Key == activeNewsCategory.Key))
            activeNewsCategory = categories.FirstOrDefault() ?? News.Categories[0];
        DrawNewsCategories();
        _ = LoadNewsCategory(activeNewsCategory);
    }

    static bool IsOver(FrameworkElement element, MouseEventArgs e) =>
        element.IsVisible && new Rect(element.RenderSize).Contains(e.GetPosition(element));

    void EnableNewsWheel(UIElement surface, ScrollViewer vertical, ScrollViewer? horizontal = null)
    {
        surface.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            if (horizontal != null && (IsOver(horizontal, e) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
                ScrollCategories(horizontal, e.Delta);
            else
                ScrollByWheel(vertical, e.Delta, horizontal: false);
            e.Handled = true;
        }), handledEventsToo: true);
    }

    // Touchpads and tilt wheels send a separate horizontal-wheel message that WPF doesn't turn into a mouse-wheel event.
    void HookNewsHorizontalWheel()
    {
        if (newsHook != null) return;
        newsHook = PresentationSource.FromVisual(this) as HwndSource;
        newsHook?.AddHook(NewsWindowProc);
    }

    IntPtr NewsWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (msg == WM_MOUSEHWHEEL && newsCategoryScroll is { IsVisible: true } scroll)
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            ScrollByWheel(scroll, -delta, horizontal: true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    Popup BuildNewsCategoryPopup(Button target)
    {
        var rows = new StackPanel();
        foreach (var category in News.OrderedCategories(S.NewsCategoryOrder))
        {
            var grip = new TextBlock { Text = "⠿", FontSize = 18, Width = 24, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll };
            grip.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            Point dragStart = default;
            grip.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(grip);
            grip.PreviewMouseMove += (_, e) =>
            {
                var point = e.GetPosition(grip);
                if (e.LeftButton == MouseButtonState.Pressed &&
                    (Math.Abs(point.X - dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                     Math.Abs(point.Y - dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance))
                    DragDrop.DoDragDrop(grip, category, DragDropEffects.Move);
            };
            var label = new TextBlock { Text = NewsLabel(category), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var enabled = !S.HiddenNewsCategories.Contains(category.Key, StringComparer.OrdinalIgnoreCase);
            var check = new CheckBox { IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            check.Checked += (_, _) => SetCategoryVisibility(category, true);
            check.Unchecked += (_, _) => SetCategoryVisibility(category, false);
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(5, 3, 5, 3), AllowDrop = true, Tag = category };
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);
            DockPanel.SetDock(grip, Dock.Left);
            row.Children.Add(grip);
            row.Children.Add(label);
            row.DragOver += (_, e) => { if (e.Data.GetDataPresent(typeof(News.Category))) e.Effects = DragDropEffects.Move; e.Handled = true; };
            row.Drop += (_, e) =>
            {
                if (e.Data.GetData(typeof(News.Category)) is News.Category dragged && row.Tag is News.Category dropped)
                    ReorderNewsCategory(dragged, dropped);
            };
            rows.Children.Add(row);
        }

        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Child = new ScrollViewer { Content = rows, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                Padding = new Thickness(8),
                MinWidth = 240,
                MaxWidth = 300,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1)
            }
        };
        ((Border)popup.Child).SetResourceReference(Border.BackgroundProperty, "BoxCard");
        ((Border)popup.Child).SetResourceReference(Border.BorderBrushProperty, "EdgeStroke");
        return popup;
    }

    void SetCategoryVisibility(News.Category category, bool visible)
    {
        var hidden = new HashSet<string>(S.HiddenNewsCategories, StringComparer.OrdinalIgnoreCase);
        if (visible) hidden.Remove(category.Key); else hidden.Add(category.Key);
        if (News.Categories.All(c => hidden.Contains(c.Key)))
        {
            _ = Notice("Keep a category visible", "News needs at least one visible category.");
            return;
        }
        S.HiddenNewsCategories = [.. hidden];
        SaveSettings();
        var categories = News.OrderedCategories(S.NewsCategoryOrder, S.HiddenNewsCategories);
        if (activeNewsCategory != null && !categories.Any(c => c.Key == activeNewsCategory.Key))
            activeNewsCategory = categories[0];
        RefreshNewsCategoryEditor();
        DrawNewsCategories();
        _ = LoadNewsCategory(activeNewsCategory ?? categories[0]);
    }

    void ReorderNewsCategory(News.Category dragged, News.Category dropped)
    {
        var order = News.OrderedCategories(S.NewsCategoryOrder).Select(c => c.Key).ToList();
        var from = order.IndexOf(dragged.Key);
        var to = order.IndexOf(dropped.Key);
        if (from < 0 || to < 0 || from == to) return;
        order.RemoveAt(from);
        order.Insert(to, dragged.Key);
        S.NewsCategoryOrder = order;
        SaveSettings();
        RefreshNewsCategoryEditor();
        DrawNewsCategories();
    }

    void RefreshNewsCategoryEditor()
    {
        if (newsCategoryPopup?.PlacementTarget is Button target)
        {
            var wasOpen = newsCategoryPopup.IsOpen;
            newsCategoryPopup.IsOpen = false;
            newsCategoryPopup = BuildNewsCategoryPopup(target);
            newsCategoryPopup.IsOpen = wasOpen;
        }
    }

    void DrawNewsCategories()
    {
        if (newsCategories == null) return;
        newsCategories.Children.Clear();
        foreach (var category in News.OrderedCategories(S.NewsCategoryOrder, S.HiddenNewsCategories))
        {
            var selected = category.Key == activeNewsCategory?.Key;
            var button = new ToggleButton
            {
                Content = NewsLabel(category),
                Padding = new Thickness(11, 7, 11, 7),
                Margin = new Thickness(0, 0, 7, 0),
                Cursor = Cursors.Hand,
                IsChecked = selected,
                Style = (Style)FindResource("Chip"),
                ToolTip = category.Topic
            };
            button.Click += (_, _) =>
            {
                activeNewsCategory = category;
                DrawNewsCategories();
                _ = LoadNewsCategory(category);
            };
            newsCategories.Children.Add(button);
        }
    }

    // what a tab is called: your city and country by name, the rest in a word or two
    string NewsLabel(News.Category c) => c.Key switch
    {
        "city-general" => newsPlace?.City is { Length: > 0 } city ? city : "Your city",
        "country-general" => newsPlace?.Country is { Length: > 0 } country ? country : "Your country",
        "country-technology" => "Tech",
        "country-foreign-affairs" => "Foreign affairs",
        "country-business" => "Business",
        "international-general" => "World",
        "international-technology" => "World tech",
        "international-affairs" => "World affairs",
        "international-business" => "World business",
        "international-sports" => "Sports",
        "culture-bollywood" => "Bollywood",
        "culture-hollywood" => "Hollywood",
        "culture-music" => "Music",
        _ => c.Name
    };

    async Task<(string City, string Country)> NewsLocation()
    {
        if (newsPlace is { } known) return known;
        var location = await Weather.Current();
        (string City, string Country) place = (location?.Place ?? S.WeatherCity.Trim(), Weather.Country);
        if (place.City != "") newsPlace = place;
        return place;
    }

    // A category's stories: placeholders at once, then every card together straight from the papers' feeds, then any
    // missing photo or summary fills in on its card (six at a time); the full text waits until a story is opened.
    async Task LoadNewsCategory(News.Category category, bool refresh = false)
    {
        if (newsArticles == null || newsStatus == null) return;
        var request = ++newsRequest;
        var target = newsArticles;
        var status = newsStatus;
        bool Current() => newsArticles == target && request == newsRequest;
        target.Children.Clear();
        for (var i = 0; i < 6; i++) target.Children.Add(NewsPlaceholder());
        status.Text = refresh ? "Checking for newer stories…" : "Finding the latest stories…";
        try
        {
            var (city, country) = await NewsLocation();
            if (!Current()) return;
            DrawNewsCategories();   // the city and country tabs by name, now they're known
            string Header(int n) => $"{NewsLabel(category)} · {n} {(n == 1 ? "story" : "stories")} from the last day · updated {DateTime.Now:t}";
            var key = $"{category.Key}|{city}|{country}";
            if (!refresh && News.Cached(key, TimeSpan.FromMinutes(10)) is { } cached)
            {
                target.Children.Clear();
                foreach (var saved in cached) target.Children.Add(NewsCard(saved));
                status.Text = Header(cached.Count);
                return;
            }
            var stories = await Task.Run(() => News.Latest(category, city, country, 16));   // feeds are read and parsed off the UI thread
            if (!Current()) return;
            target.Children.Clear();
            if (stories.Count == 0)
            {
                status.Text = $"Nothing new in {NewsLabel(category)} in the last day. Try Refresh in a while.";
                return;
            }
            foreach (var story in stories) target.Children.Add(NewsCard(story));
            status.Text = Header(stories.Count);

            using var gate = new SemaphoreSlim(6);
            var filled = await Task.WhenAll(stories.Select(async (story, i) =>
            {
                await gate.WaitAsync();
                try
                {
                    var better = await News.Details(story);
                    if (better != story && Current() && i < target.Children.Count) { target.Children.RemoveAt(i); target.Children.Insert(i, NewsCard(better)); }
                    return better;
                }
                finally { gate.Release(); }
            }));
            if (Current()) News.Remember(key, [.. filled]);
        }
        catch (Exception ex)
        {
            if (Current()) { target.Children.Clear(); status.Text = $"Couldn't load News: {ex.Message}"; }
            Store.Fail("News · Load category", ex.Message);
        }
    }

    // a card's shape while its story is on the way, softly pulsing
    FrameworkElement NewsPlaceholder()
    {
        Border Bar(double h, double w, Thickness m) { var b = new Border { Height = h, Width = w, CornerRadius = new CornerRadius(5), Margin = m, HorizontalAlignment = HorizontalAlignment.Left }; b.SetResourceReference(Border.BackgroundProperty, "InputBg"); return b; }
        var photo = new Border { Height = 132 }; photo.SetResourceReference(Border.BackgroundProperty, "InputBg");
        var card = new Border { Margin = new Thickness(0, 0, 9, 12), CornerRadius = new CornerRadius(13), ClipToBounds = true, BorderThickness = new Thickness(1),
                                Child = new StackPanel { Children = { photo, Bar(14, 210, new Thickness(13, 14, 13, 0)), Bar(14, 150, new Thickness(13, 8, 13, 0)), Bar(10, 230, new Thickness(13, 14, 13, 0)), Bar(10, 180, new Thickness(13, 6, 13, 16)) } } };
        card.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        card.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.45, 0.9, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        return card;
    }

    // how long ago, the way people say it: "just now", "25 min ago", "3 h ago", then the date
    static string NewsAgo(DateTime? at)
    {
        if (at is not { } t) return "";
        var gone = DateTime.Now - t.ToLocalTime();
        return gone.TotalMinutes < 2 ? "just now" : gone.TotalMinutes < 60 ? $"{(int)gone.TotalMinutes} min ago" : gone.TotalHours < 24 ? $"{(int)gone.TotalHours} h ago" : t.ToLocalTime().ToString("d MMM");
    }

    FrameworkElement NewsCard(News.Article article)
    {
        var image = new Image { Height = 170, Stretch = Stretch.UniformToFill };
        var hasPhoto = Uri.TryCreate(article.ImageUrl, UriKind.Absolute, out var photo) && photo.Scheme is "http" or "https";
        if (hasPhoto) image.Source = Thumb(photo!, 640);

        var logo = SourceLogo(article);
        var photoGrid = new Grid { Height = 132, ClipToBounds = true };
        if (!hasPhoto)
        {
            // no photo: the paper's name on a soft tint, not an empty grey box
            photoGrid.Background = new LinearGradientBrush(Color.FromArgb(0x40, 0x38, 0xBD, 0xF8), Color.FromArgb(0x30, 0x8B, 0x7C, 0xFF), 30);
            var paper = new TextBlock { Text = article.Source, FontSize = 18, FontWeight = FontWeights.SemiBold, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 0, 16, 0) };
            paper.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            photoGrid.Children.Add(paper);
        }
        else photoGrid.Children.Add(image);
        var logoHost = new Border
        {
            Child = logo,
            Width = 26,
            Height = 26,
            Margin = new Thickness(8),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
            ToolTip = article.Source,
            Visibility = Visibility.Collapsed   // shown once the paper's icon has loaded: no empty white circle
        };
        if (logo is Image { Source: BitmapImage icon })
        {
            if (!icon.IsDownloading) logoHost.Visibility = Visibility.Visible;
            else icon.DownloadCompleted += (_, _) => { if (icon.PixelWidth > 1) logoHost.Visibility = Visibility.Visible; };
        }
        photoGrid.Children.Add(logoHost);

        var title = new TextBlock { Text = article.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(13, 12, 13, 0), MaxHeight = 56 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var summary = new TextBlock { Text = article.Summary, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(13, 7, 13, 9), MaxHeight = 56 };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var footer = new DockPanel { Margin = new Thickness(13, 0, 13, 11) };
        var source = new TextBlock { Text = article.Source, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = article.Source };
        source.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var published = new TextBlock { Text = NewsAgo(article.Published), ToolTip = article.Published?.ToLocalTime().ToString("d MMM · h:mm tt"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        published.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        DockPanel.SetDock(published, Dock.Right);
        footer.Children.Add(published);
        footer.Children.Add(source);
        var cardContent = new StackPanel { Children = { photoGrid, title, summary, footer } };
        // the photo follows the card's rounded corners (a border's rounding doesn't clip what's inside it)
        cardContent.SizeChanged += (_, e) => cardContent.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
        var card = new Border
        {
            Child = cardContent,
            Margin = new Thickness(0, 0, 9, 12),
            CornerRadius = new CornerRadius(13),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "BoxCard");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        card.MouseEnter += (_, _) => { card.SetResourceReference(Border.BackgroundProperty, "InputBg"); card.SetResourceReference(Border.BorderBrushProperty, "AccentGrad"); };
        card.MouseLeave += (_, _) => { card.SetResourceReference(Border.BackgroundProperty, "BoxCard"); card.SetResourceReference(Border.BorderBrushProperty, "GlassStroke"); };
        card.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenNewsArticle(article); };
        return card;
    }

    FrameworkElement SourceLogo(News.Article article)
    {
        if (Uri.TryCreate(article.Link, UriKind.Absolute, out var link))
        {
            // the paper's icon as a PNG (some sites' own favicon.ico is in a form WPF can't read, which left a blank circle)
            var url = $"https://www.google.com/s2/favicons?domain={link.Host}&sz=64";
            return new Image { Source = Thumb(new Uri(url), 64), Stretch = Stretch.Uniform, ToolTip = article.Source };
        }
        var initial = new TextBlock { Text = article.Source.Length > 0 ? article.Source[..1].ToUpperInvariant() : "N", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        initial.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return initial;
    }

    void OpenNewsArticle(News.Article article)
    {
        var page = new DockPanel { MaxWidth = 700 };
        var back = new Button { Content = "‹  News", Style = (Style)FindResource("GhostText"), Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        back.Click += (_, _) => OpenNews();
        DockPanel.SetDock(back, Dock.Top);
        page.Children.Add(back);

        var link = new Button { Content = "Read original article  ↗", Style = (Style)FindResource("Shimmer"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 0) };
        link.Click += (_, _) => OpenExternal(article.Link);
        DockPanel.SetDock(link, Dock.Bottom);
        page.Children.Add(link);

        var body = new StackPanel();
        if (Uri.TryCreate(article.ImageUrl, UriKind.Absolute, out var photo) && photo.Scheme is "http" or "https")
        {
            // the photo's space only once it has loaded: a photo that won't load leaves no empty block
            var pic = new BitmapImage(photo);
            var shown = new Image { Source = pic, Height = 240, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 0, 14), Visibility = pic.IsDownloading ? Visibility.Collapsed : Visibility.Visible };
            pic.DownloadCompleted += (_, _) => shown.Visibility = Visibility.Visible;
            body.Children.Add(shown);
        }
        var source = new TextBlock
        {
            Text = string.Join(" · ", new[] { article.Source, article.Section, article.Author, article.Published?.ToLocalTime().ToString("d MMM yyyy · h:mm tt"), article.Coverage > 1 ? $"covered by {article.Coverage} sources" : null }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 11)
        };
        source.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var title = new TextBlock { Text = article.Title, FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        body.Children.Add(title);
        body.Children.Add(source);
        // the full story is read when it's opened: the summary meanwhile, and it stays if the page can't be read
        var text = new StackPanel();
        body.Children.Add(text);
        text.Children.Add(ArticleParagraph(article.Summary));
        var loading = new TextBlock { Text = "Loading the full story…", FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        loading.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        text.Children.Add(loading);
        _ = Task.Run(() => News.FullText(article.Link)).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            text.Children.Remove(loading);
            if (t.IsCompletedSuccessfully && t.Result.Count > 0) { text.Children.Clear(); foreach (var paragraph in t.Result) text.Children.Add(ArticleParagraph(paragraph)); }
            else
            {
                var note = new TextBlock { Text = $"{article.Source} didn't let Ivo read the full story. Read it on their site below.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
                note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                text.Children.Add(note);
            }
        }));
        var articleScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        page.Children.Add(articleScroll);
        EnableNewsWheel(page, articleScroll);
        OpenPanel("news-article", "News article", NewsGlyph, "#38BDF8", page, scroll: false);
    }

    TextBlock ArticleParagraph(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 14, LineHeight = 23, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        return block;
    }

    static BitmapImage Thumb(Uri uri, int width)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.DecodePixelWidth = width;
        bitmap.EndInit();
        return bitmap;
    }

    FrameworkElement NewsIconForTitleBar(double size = 18) => NewsGlyphView(Shell.B("#38BDF8"), size);

    static void OpenExternal(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Store.Fail("News · Open article", "The article link isn't a valid web address.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Store.Fail("News · Open article", ex.Message); }
    }
}
