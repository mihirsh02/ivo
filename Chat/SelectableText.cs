using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Ivo;

// A TextBlock you can select text in (drag, double-click a word, Ctrl+C), like a web page. WPF's own text editor, the one
// TextBox uses, is attached read-only to the block, so it keeps all its rich text and costs no more than a TextBlock.
// If WPF ever changes these internals, it quietly stays an ordinary TextBlock.
public class SelectableTextBlock : TextBlock
{
    static readonly Type? EditorType = Type.GetType("System.Windows.Documents.TextEditor, PresentationFramework");
    static readonly Type? ContainerType = Type.GetType("System.Windows.Documents.ITextContainer, PresentationFramework");
    static readonly PropertyInfo? ContainerProp = typeof(TextBlock).GetProperty("TextContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly bool works;
    readonly object? editor;

    static SelectableTextBlock()
    {
        try
        {
            FocusableProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(true));
            FocusVisualStyleProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(null));
            EditorType!.GetMethod("RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(Type), typeof(bool), typeof(bool), typeof(bool)], null)!
                       .Invoke(null, [typeof(SelectableTextBlock), true, true, true]);
            works = ContainerType != null && ContainerProp != null;
        }
        catch { works = false; }
    }

    public SelectableTextBlock()
    {
        Wheel.PassUp(this);   // the attached editor would keep the mouse wheel for itself
        if (!works) return;
        try
        {
            var container = ContainerProp!.GetValue(this);
            editor = Activator.CreateInstance(EditorType!, BindingFlags.Instance | BindingFlags.NonPublic, null, [container, this, false], null);
            EditorType!.GetProperty("IsReadOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, true);
            EditorType.GetProperty("TextView", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, ContainerType!.GetProperty("TextView")!.GetValue(container));
        }
        catch { editor = null; }
    }

    // what's selected right now ("" for nothing)
    public string SelectedText =>
        editor != null && EditorType!.GetProperty("Selection", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor) is TextRange r ? r.Text.Trim() : "";
}

// Things inside a reply that would swallow the mouse wheel (selectable text, a table's sideways scroller, a code view)
// hand it to the nearest scroller above them that can scroll up and down: the chat, so it scrolls under the pointer.
public static class Wheel
{
    public static T PassUp<T>(T el) where T : UIElement
    {
        el.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            for (var x = System.Windows.Media.VisualTreeHelper.GetParent(el); x != null; x = System.Windows.Media.VisualTreeHelper.GetParent(x))
                if (x is ScrollViewer { ScrollableHeight: > 0 } sv)
                {
                    e.Handled = true;
                    sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 120.0 * 48);   // Windows' usual three lines a notch
                    return;
                }
        };
        return el;
    }
}
