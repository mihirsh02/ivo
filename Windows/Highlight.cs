using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Ivo;

// Syntax colouring (AvalonEdit) for the Code-mode editor and for code blocks in replies. A file is matched by its
// extension; anything else goes through Code.Language, the same detector used for pasted code.
public static class Highlight
{
    // Names from code fences and Code.Language → AvalonEdit's built-in definitions.
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = "C#", ["cs"] = "C#", ["c#"] = "C#",
        ["javascript"] = "JavaScript", ["js"] = "JavaScript", ["jsx"] = "JavaScript", ["json"] = "JavaScript", ["jsonc"] = "JavaScript",
        ["typescript"] = "JavaScript", ["ts"] = "JavaScript", ["tsx"] = "JavaScript", ["mjs"] = "JavaScript",
        ["html"] = "HTML", ["htm"] = "HTML", ["xml"] = "XML", ["xaml"] = "XML", ["svg"] = "XML",
        ["python"] = "Python", ["py"] = "Python", ["java"] = "Java", ["cpp"] = "C++", ["c++"] = "C++", ["c"] = "C++", ["h"] = "C++", ["hpp"] = "C++",
        ["css"] = "CSS", ["scss"] = "CSS", ["sql"] = "TSQL", ["powershell"] = "PowerShell", ["ps1"] = "PowerShell", ["pwsh"] = "PowerShell",
        ["markdown"] = "MarkDown", ["md"] = "MarkDown", ["php"] = "PHP", ["vb"] = "VB",
    };

    public static IHighlightingDefinition? ForLanguage(string? lang) =>
        lang != null && Names.TryGetValue(lang, out var n) ? HighlightingManager.Instance.GetDefinition(n) : null;

    public static IHighlightingDefinition? ForFile(string path, string text)
    {
        var ext = Path.GetExtension(path);
        return HighlightingManager.Instance.GetDefinitionByExtension(ext) ?? ForLanguage(ext.TrimStart('.')) ?? ForLanguage(Code.Language(text));
    }

    // The built-in colours are made for white backgrounds; dark themes get a VS Code-like palette instead.
    static readonly Dictionary<HighlightingColor, HighlightingBrush?> original = new(ReferenceEqualityComparer.Instance);   // by identity: a colour's hash changes when its font is cleared

    public static void Apply(string theme)
    {
        var dark = theme != "light";
        foreach (var def in HighlightingManager.Instance.HighlightingDefinitions)
            foreach (var c in def.NamedHighlightingColors)
            {
                if (!original.ContainsKey(c)) original[c] = c.Foreground;
                c.FontFamily = null; c.FontSize = null;   // colour only: Markdown's big serif headings and code spans would break the even monospace grid
                c.Foreground = dark ? new SimpleHighlightingBrush(DarkColor(c.Name, original[c])) : original[c];
            }
    }

    static Color DarkColor(string name, HighlightingBrush? was)
    {
        var n = name.ToLowerInvariant();
        string hex =
            n.Contains("comment") ? "#6A9955" :
            n.Contains("string") || n.Contains("char") ? "#CE9178" :
            n.Contains("number") || n.Contains("digit") ? "#B5CEA8" :
            n.Contains("preprocessor") || n.Contains("directive") || n.Contains("region") ? "#C586C0" :
            n.Contains("method") || n.Contains("function") ? "#DCDCAA" :
            n.Contains("attribute") ? "#9CDCFE" :
            n.Contains("type") || n.Contains("class") || n.Contains("struct") || n.Contains("namespace") || n.Contains("reference") ? "#4EC9B0" :
            n.Contains("tag") || n.Contains("element") || n.Contains("keyword") || n.Contains("modifier") || n.Contains("visibility") ||
            n.Contains("this") || n.Contains("null") || n.Contains("true") || n.Contains("bool") || n.Contains("value") ? "#569CD6" :
            n.Contains("entit") ? "#D7BA7D" : "";
        if (hex != "") return (Color)ColorConverter.ConvertFromString(hex);
        // anything else: the original hue, lifted so it reads on a dark background
        var c = was?.GetColor(null) ?? Colors.Silver;
        return Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2));
    }

    // A read-only, coloured code view for replies. Scrolling the reply still works over it.
    public static TextEditor View(string code, string lang)
    {
        var e = new TextEditor
        {
            Text = code, IsReadOnly = true, SyntaxHighlighting = ForLanguage(lang), ShowLineNumbers = false, WordWrap = true,
            FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 13, Background = Brushes.Transparent, Padding = new Thickness(14, 10, 14, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        e.SetResourceReference(Control.ForegroundProperty, "Fg");
        e.TextArea.Caret.CaretBrush = Brushes.Transparent;
        Wheel.PassUp(e);   // hand the wheel to the chat around it
        return e;
    }
}
