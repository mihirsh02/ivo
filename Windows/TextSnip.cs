using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ivo;

// Text from screen: the screens freeze and blur, with two floating choices: Select an area, or Add a photo.
// Select an area: the blur clears to a dim veil; drag a box over the text and it lights up. Let go and the box (from the
// frozen picture, so nothing moves under you) goes to OCR. Add a photo: a file picker instead. Esc or a right-click cancels.
public class TextSnip : Window
{
    readonly System.Drawing.Rectangle screens;   // Ivo's window, in device pixels: the picker works only inside the app
    readonly BitmapSource shot;
    readonly Action<BitmapSource> done;
    readonly Action photo;
    bool picking;   // choosing (blurred, two buttons) until Select an area
    readonly RectangleGeometry hole = new();
    readonly Rectangle edge = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), StrokeThickness = 2, RadiusX = 6, RadiusY = 6, Visibility = Visibility.Collapsed };
    Point start; bool dragging;

    public TextSnip(Window owner, Action<BitmapSource> done, Action photo)
    {
        this.done = done; this.photo = photo;
        GetWindowRect(new WindowInteropHelper(owner).Handle, out var w); screens = new(w.L, w.T, w.R - w.L, w.B - w.T);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false; Cursor = Cursors.Arrow;
        shot = Grab();

        // the picture, a dim veil with the box cut out of it, the box's outline, and a hint at the top
        var veil = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var all = new RectangleGeometry();
        veil.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { all, hole } };
        var hint = new Border
        {
            CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 9, 18, 10), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1B, 0x22)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Child = new TextBlock { Text = "Drag over the text to copy it   ·   Esc to cancel", Foreground = Brushes.White, FontSize = 14 },
        };
        var canvas = new Canvas { Children = { veil, edge } };
        var picture = new Image { Source = shot, Stretch = Stretch.Fill, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 22, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance } };
        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), IsHitTestVisible = false };

        // the two floating choices, in the middle of the main screen
        Button Choice(string glyph, string label, Action click)
        {
            var face = new StackPanel { Children = {
                new TextBlock { Text = glyph, FontFamily = App.IconFont, FontSize = 34, Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = label, FontSize = 15, Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center } } };
            var card = new FrameworkElementFactory(typeof(Border), "Bd");
            card.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
            card.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1B, 0x22)));
            card.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
            card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            card.SetValue(Border.PaddingProperty, new Thickness(26, 24, 26, 22));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); card.AppendChild(cp);
            var b = new Button { Content = face, Width = 190, Margin = new Thickness(10), Cursor = Cursors.Hand, Template = new ControlTemplate(typeof(Button)) { VisualTree = card },
                                 RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform() };
            b.MouseEnter += (_, _) => { ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 1.04; };
            b.MouseLeave += (_, _) => { ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 1; };
            b.Click += (_, _) => click();
            return b;
        }
        var choices = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var cancel = new TextBlock { Text = "Esc to cancel", Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var chooser = new StackPanel { Children = { choices, cancel }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        choices.Children.Add(Choice("", "Select an area", () =>
        {
            picking = true; Cursor = Cursors.Cross; chooser.Visibility = Visibility.Collapsed; picture.Effect = null; shade.Visibility = Visibility.Collapsed;
            veil.Visibility = hint.Visibility = Visibility.Visible;
        }));
        choices.Children.Add(Choice("", "Add a photo", () => { Close(); photo(); }));
        Content = new Grid { Children = { picture, shade, canvas, hint, chooser } };
        SizeChanged += (_, _) => { all.Rect = new Rect(0, 0, ActualWidth, ActualHeight); veil.Width = ActualWidth; veil.Height = ActualHeight; };

        MouseLeftButtonDown += (_, e) => { if (!picking) return; start = e.GetPosition(this); dragging = true; CaptureMouse(); Draw(start); };
        MouseMove += (_, e) => { if (dragging) Draw(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) => { if (!dragging) return; dragging = false; ReleaseMouseCapture(); Finish(); };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => { Activate(); Focus(); };
    }

    // exactly over Ivo's window, in device pixels (WPF's own units would be off by the DPI on mixed-scale setups)
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetWindowPos(new WindowInteropHelper(this).Handle, -1, screens.Left, screens.Top, screens.Width, screens.Height, 0x0040);
    }

    void Draw(Point to)
    {
        var r = new Rect(start, to);
        hole.Rect = r;
        edge.Visibility = r.Width > 2 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(edge, r.X); Canvas.SetTop(edge, r.Y); edge.Width = r.Width; edge.Height = r.Height;
    }

    void Finish()
    {
        var r = hole.Rect;
        Close();
        if (r.Width < 6 || r.Height < 6) return;   // a click, not a box
        var kx = shot.PixelWidth / ActualWidth; var ky = shot.PixelHeight / ActualHeight;
        var px = new Int32Rect((int)(r.X * kx), (int)(r.Y * ky), (int)Math.Max(1, r.Width * kx), (int)Math.Max(1, r.Height * ky));
        px.Width = Math.Min(px.Width, shot.PixelWidth - px.X); px.Height = Math.Min(px.Height, shot.PixelHeight - px.Y);
        var crop = new CroppedBitmap(shot, px); crop.Freeze();
        done(crop);
    }

    BitmapSource Grab()
    {
        using var bmp = new System.Drawing.Bitmap(screens.Width, screens.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(screens.Left, screens.Top, 0, 0, bmp.Size);
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    struct Box { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out Box r);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
}
