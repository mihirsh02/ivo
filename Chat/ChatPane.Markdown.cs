using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfMath.Controls;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using Table = Markdig.Extensions.Tables.Table;
using TableRow = Markdig.Extensions.Tables.TableRow;
using TableCell = Markdig.Extensions.Tables.TableCell;

namespace Ivo;

// Replies (and prompts) as Markdown: GitHub-style tables, task lists, nested lists, quotes, links, images,
// math, footnotes, strikethrough, highlights, sub/superscript, code with language labels.
public partial class ChatPane
{
    static readonly MarkdownPipeline Md = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseEmojiAndSmiley().Build();

    internal IEnumerable<UIElement> RenderMarkdown(string md, bool user)
    {
        MarkdownDocument doc;
        try { doc = Markdown.Parse(md, Md); }
        catch { return [Para(user, new Run(md))]; }
        return doc.Select(b => RenderBlock(b, user)).OfType<UIElement>().ToList();
    }

    UIElement? RenderBlock(MdBlock block, bool user)
    {
        switch (block)
        {
            case HeadingBlock h:
            {
                var tb = Para(user, h.Inline);
                tb.FontSize = h.Level switch { 1 => 22, 2 => 19, 3 => 16.5, _ => 15 };
                tb.FontWeight = h.Level <= 3 ? FontWeights.SemiBold : FontWeights.Bold;
                tb.Margin = new Thickness(0, h.Level <= 2 ? 10 : 6, 0, 4);
                return tb;
            }
            case MathBlock m:
                return Formula(string.Join("\n", m.Lines.Lines.Take(m.Lines.Count).Select(l => l.ToString())), user, display: true);
            case FencedCodeBlock f:
                return CodeBlock(Lines(f), f.Info?.Trim() ?? "");
            case CodeBlock c:
                var code = Lines(c);
                return CodeBlock(code, Code.Language(code) ?? "");
            case ParagraphBlock p:
                return Para(user, p.Inline);
            case ListBlock list:
                return List(list, user);
            case QuoteBlock q:
            {
                var inner = new StackPanel();
                foreach (var b in q) if (RenderBlock(b, user) is { } el) inner.Children.Add(el);
                var bar = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 4, 0, 4), Child = inner, Opacity = 0.9 };
                bar.BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x7C, 0x5C, 0xFF));
                return bar;
            }
            case ThematicBreakBlock:
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
                line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
                return line;
            }
            case Table t:
                return TableGrid(t, user);
            case FootnoteGroup fg:
            {
                var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 6), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
                line.SetResourceReference(Border.BackgroundProperty, "GlassStroke");
                panel.Children.Add(line);
                foreach (var fn in fg.OfType<Footnote>())
                {
                    var row = new DockPanel();
                    var n = new TextBlock { Text = fn.Order + ".", FontSize = 12, Margin = new Thickness(0, 1, 6, 0) };
                    n.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                    row.Children.Add(n);
                    var body = new StackPanel();
                    foreach (var b in fn) if (RenderBlock(b, user) is FrameworkElement el) { if (el is TextBlock tb) tb.FontSize = 12; body.Children.Add(el); }
                    row.Children.Add(body);
                    panel.Children.Add(row);
                }
                return panel;
            }
            case HtmlBlock html:
                return Para(user, new Run(Lines(html)));
            case LinkReferenceDefinitionGroup:
                return null;
            case ContainerBlock other:   // anything else that holds blocks (e.g. custom containers)
            {
                var panel = new StackPanel();
                foreach (var b in other) if (RenderBlock(b, user) is { } el) panel.Children.Add(el);
                return panel;
            }
            case LeafBlock leaf:
                return leaf.Inline != null ? Para(user, leaf.Inline) : Para(user, new Run(Lines(leaf)));
            default:
                return null;
        }
    }

    static string Lines(LeafBlock b) => string.Join("\n", b.Lines.Lines.Take(b.Lines.Count).Select(l => l.ToString())).TrimEnd('\n');

    // Bullets, numbers and checkboxes in a column; items can hold anything, including nested lists.
    UIElement List(ListBlock list, bool user)
    {
        var grid = new Grid { Margin = new Thickness(4, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int.TryParse(list.OrderedStart, out var n); if (n == 0) n = 1;
        var row = 0;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var task = (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild as TaskList;
            var marker = new TextBlock
            {
                Text = task != null ? (task.Checked ? "" : "") : list.IsOrdered ? $"{n++}." : (list.Parent is ListItemBlock ? "◦" : "•"),
                FontFamily = task != null ? App.IconFont : FontFamily, FontSize = task != null ? 13 : 14,
                Margin = new Thickness(0, task != null ? 3 : 1, 8, 0), MinWidth = list.IsOrdered ? 18 : 10, TextAlignment = TextAlignment.Right,
            };
            if (task?.Checked == true) marker.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
            else marker.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var body = new StackPanel();
            foreach (var b in item) if (RenderBlock(b, user) is FrameworkElement el) { el.Margin = new Thickness(el.Margin.Left, 1, el.Margin.Right, 1); body.Children.Add(el); }
            Grid.SetRow(marker, row); Grid.SetRow(body, row); Grid.SetColumn(body, 1);
            grid.Children.Add(marker); grid.Children.Add(body);
            row++;
        }
        return grid;
    }

    UIElement TableGrid(Table t, bool user)
    {
        var grid = new Grid();
        var rows = t.OfType<TableRow>().Count();
        var cols = t.ColumnDefinitions.Count;
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var r = 0;
        foreach (var tr in t.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = 0;
            foreach (var cell in tr.OfType<TableCell>())
            {
                var content = new StackPanel();
                foreach (var b in cell) if (RenderBlock(b, user) is { } el) content.Children.Add(el);
                foreach (var tb in content.Children.OfType<TextBlock>())
                {
                    tb.Margin = new Thickness(0);
                    if (tr.IsHeader) tb.FontWeight = FontWeights.SemiBold;
                    var align = c < t.ColumnDefinitions.Count ? t.ColumnDefinitions[c].Alignment : null;
                    tb.TextAlignment = align == TableColumnAlign.Center ? TextAlignment.Center : align == TableColumnAlign.Right ? TextAlignment.Right : TextAlignment.Left;
                }
                var border = new Border { Child = content, Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < rows - 1 ? 1 : 0) };   // inner lines only: the frame is the outside
                border.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
                if (tr.IsHeader) border.SetResourceReference(Border.BackgroundProperty, "Glass");
                Grid.SetRow(border, r); Grid.SetColumn(border, Math.Min(c, cols - 1)); Grid.SetColumnSpan(border, Math.Max(1, cell.ColumnSpan));
                grid.Children.Add(border);
                c += Math.Max(1, cell.ColumnSpan);
            }
            r++;
        }
        var frame = new Border { Child = grid, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };   // as wide as the table, not the reply
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        // wide tables scroll sideways instead of squashing
        var scroll = Wheel.PassUp(new ScrollViewer { Content = frame, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 6, 0, 4) });
        // tab-separated: pastes into Excel or Sheets as cells, and reads fine as text
        var copy = CopyButton("Copy table (pastes into Excel as cells)", () => Clipboard.SetText(string.Join("\r\n",
            t.OfType<TableRow>().Select(tr => string.Join("\t", tr.OfType<TableCell>().Select(CellText))))), text: "Copy table");
        copy.HorizontalAlignment = HorizontalAlignment.Left; copy.Margin = new Thickness(0, 0, 0, 6);
        return new StackPanel { Children = { scroll, copy } };
    }

    static string CellText(TableCell cell) => System.Text.RegularExpressions.Regex.Replace(string.Join("", cell.Descendants<MdInline>().Select(i => i switch
    {
        LiteralInline l => l.Content.ToString(), CodeInline c => c.Content, MathInline m => m.Content.ToString(), LineBreakInline => " ", _ => "",
    })), @"\s+", " ").Trim();

    TextBlock Para(bool user, ContainerInline? inlines)
    {
        var tb = NewText(user);
        if (inlines != null) foreach (var i in inlines) AddInline(tb.Inlines, i, user);
        return tb;
    }
    TextBlock Para(bool user, System.Windows.Documents.Inline run) { var tb = NewText(user); tb.Inlines.Add(run); return tb; }

    static TextBlock NewText(bool user)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Margin = new Thickness(0, 3, 0, 3), Cursor = System.Windows.Input.Cursors.IBeam };
        if (user) tb.Foreground = Brushes.White; else tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        return tb;
    }

    void AddInline(InlineCollection into, MdInline inline, bool user)
    {
        switch (inline)
        {
            case TaskList:
                return;   // drawn as the list marker
            case LiteralInline lit:
                into.Add(new Run(lit.Content.ToString()));
                return;
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = Mono, FontSize = 13 };
                into.Add(new InlineUIContainer(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x5C, 0xFF)), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1),
                    Child = new TextBlock(run) { Foreground = user ? Brushes.White : (Brush)FindResource("Fg") },
                }) { BaselineAlignment = BaselineAlignment.Center });
                return;
            }
            case MathInline math:
                into.Add(new InlineUIContainer(Formula(math.Content.ToString(), user, display: false)) { BaselineAlignment = BaselineAlignment.Center });
                return;
            case LineBreakInline br:
                into.Add(br.IsHard ? new LineBreak() : new Run(" "));
                return;
            case AutolinkInline auto:
                into.Add(Link(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, new Run(auto.Url), user));
                return;
            case LinkInline { IsImage: true } img:
                into.Add(new InlineUIContainer(WebImage(img.Url ?? "", img.Title)));
                return;
            case LinkInline link:
            {
                var span = new Span();
                foreach (var c in link) AddInline(span.Inlines, c, user);
                into.Add(Link(link.Url ?? "", span, user));
                return;
            }
            case FootnoteLink fl:
                into.Add(new Run($"[{fl.Footnote.Order}]") { BaselineAlignment = BaselineAlignment.Superscript, FontSize = 11 });
                return;
            case EmphasisInline em:
            {
                Span span = (em.DelimiterChar, em.DelimiterCount) switch
                {
                    ('~', 2) => new Span { TextDecorations = TextDecorations.Strikethrough },
                    ('~', 1) => new Span { BaselineAlignment = BaselineAlignment.Subscript, FontSize = 12 },
                    ('^', _) => new Span { BaselineAlignment = BaselineAlignment.Superscript, FontSize = 12 },
                    ('+', 2) => new Span { TextDecorations = TextDecorations.Underline },
                    ('=', 2) => new Span { Background = new SolidColorBrush(Color.FromArgb(0x55, 0xF4, 0x72, 0xB6)) },
                    (_, >= 3) => new Bold(new Italic()),
                    (_, 2) => new Bold(),
                    _ => new Italic(),
                };
                var target = span is Bold { Inlines.FirstInline: Italic it } ? it.Inlines : span.Inlines;
                foreach (var c in em) AddInline(target, c, user);
                into.Add(span);
                return;
            }
            case HtmlInline html:
                if (System.Text.RegularExpressions.Regex.IsMatch(html.Tag, @"^<br\s*/?>$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) into.Add(new LineBreak());
                else if (!System.Text.RegularExpressions.Regex.IsMatch(html.Tag, @"^</?\w+[^>]*>$")) into.Add(new Run(html.Tag));   // drop plain tags like <b>, keep anything odd
                return;
            case HtmlEntityInline ent:
                into.Add(new Run(ent.Transcoded.ToString()));
                return;
            case ContainerInline container:
                foreach (var c in container) AddInline(into, c, user);
                return;
            case LeafInline leaf:
                into.Add(new Run(leaf.ToString()));
                return;
        }
    }

    Hyperlink Link(string url, System.Windows.Documents.Inline content, bool user)
    {
        var h = new Hyperlink(content) { ToolTip = url, Cursor = System.Windows.Input.Cursors.Hand };
        h.Foreground = user ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            // opened on mouse-up ourselves: the press is kept from the selectable text's editor, which would capture it and swallow the click
            Point? down = null;
            h.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(null); e.Handled = true; };
            h.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (down is not { } d || (e.GetPosition(null) - d).Length > 4) { down = null; return; }
                down = null; e.Handled = true;
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            };
        }
        return h;
    }

    static FrameworkElement WebImage(string url, string? title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new TextBlock { Text = $"[image: {title ?? url}]", FontStyle = FontStyles.Italic };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.UriSource = uri; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
            var name = Path.GetFileNameWithoutExtension(uri.LocalPath);
            return WithImageActions(new Image { Source = bmp, MaxWidth = 420, MaxHeight = 320, Stretch = Stretch.Uniform, ToolTip = title ?? url }, bmp, name);
        }
        catch { return new TextBlock { Text = $"[image: {title ?? url}]", FontStyle = FontStyles.Italic }; }
    }

    // An image with Copy and Save under it.
    static FrameworkElement WithImageActions(Image img, BitmapSource src, string name)
    {
        var copy = CopyButton("Copy image", () => Clipboard.SetImage(src));
        var save = CopyButton("Save image as…", () =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = (name is { Length: > 0 } ? name : $"image {DateTime.Now:yyyy-MM-dd HH.mm.ss}") + ".png", Filter = "PNG image|*.png" };
            if (dlg.ShowDialog() != true) throw new OperationCanceledException();
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(src));
            using var f = File.Create(dlg.FileName); png.Save(f);
        }, glyph: "", text: "Save", done: "Saved");
        save.Margin = new Thickness(6, 0, 0, 0);
        img.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Margin = new Thickness(0, 4, 0, 4), Children = { img, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { copy, save } } } };
    }

    // LaTeX through WpfMath; anything it can't parse shows as the raw source.
    FrameworkElement Formula(string tex, bool user, bool display)
    {
        try
        {
            var f = new FormulaControl { Formula = tex.Trim(), Scale = display ? 18 : 15, SelectionBrush = null };
            f.Foreground = user ? Brushes.White : (Brush)FindResource("Fg");
            if (display) { f.HorizontalAlignment = HorizontalAlignment.Center; f.Margin = new Thickness(0, 6, 0, 6); }
            return f;
        }
        catch
        {
            var tb = new TextBlock { Text = display ? tex : $"${tex}$", FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            if (user) tb.Foreground = Brushes.White; else tb.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            return tb;
        }
    }
}
