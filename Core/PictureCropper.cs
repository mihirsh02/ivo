using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ivo;

// Crop your picture: the photo behind a round frame; drag to move it, the slider or the mouse wheel to zoom.
// Use picture keeps what's in the circle, at 384 px, high quality (a JPEG, base64). Ivo and Ivo Portal alike.
public class PictureCropper : Window
{
    const double Frame = 340;          // the frame on screen
    const int Out = 384;               // the picture made
    readonly BitmapSource photo;
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Slider zoom = new() { Minimum = 1, Maximum = 4, Value = 1, Width = 220, VerticalAlignment = VerticalAlignment.Center };
    readonly double cover;             // the scale at which the photo just fills the frame
    double x, y;                       // the photo's top-left, in the frame
    Point dragFrom; bool dragging;
    public string? Picture;            // the result (null: cancelled)

    static Brush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    public static string? Crop(string path, Window? owner)
    {
        var c = new PictureCropper(path) { Owner = owner };
        c.ShowDialog();
        return c.Picture;
    }

    PictureCropper(string path)
    {
        Title = "Crop your picture"; Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B("#1E2730"); Foreground = B("#E6EDF3"); FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var src = new BitmapImage(); src.BeginInit(); src.UriSource = new Uri(path); src.CacheOption = BitmapCacheOption.OnLoad; src.DecodePixelWidth = 2048; src.EndInit(); src.Freeze();
        photo = src;
        image.Source = photo;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        cover = Math.Max(Frame / photo.PixelWidth, Frame / photo.PixelHeight);

        // the frame: the photo, darkened outside a circle, the circle outlined
        var canvas = new Canvas { Width = Frame, Height = Frame, ClipToBounds = true, Background = Brushes.Black, Cursor = Cursors.SizeAll };
        canvas.Children.Add(image);
        var veil = new Path { Fill = B("#B0000000"), IsHitTestVisible = false,
                              Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { new RectangleGeometry(new Rect(0, 0, Frame, Frame)), new EllipseGeometry(new Rect(0, 0, Frame, Frame)) } } };
        canvas.Children.Add(veil);
        canvas.Children.Add(new Ellipse { Width = Frame, Height = Frame, Stroke = B("#CCFFFFFF"), StrokeThickness = 2, IsHitTestVisible = false });

        canvas.MouseLeftButtonDown += (_, e) => { dragging = true; dragFrom = e.GetPosition(canvas); canvas.CaptureMouse(); };
        canvas.MouseMove += (_, e) => { if (!dragging) return; var p = e.GetPosition(canvas); x += p.X - dragFrom.X; y += p.Y - dragFrom.Y; dragFrom = p; Place(); };
        canvas.MouseLeftButtonUp += (_, _) => { dragging = false; canvas.ReleaseMouseCapture(); };
        canvas.MouseWheel += (_, e) => zoom.Value = Math.Clamp(zoom.Value + (e.Delta > 0 ? 0.15 : -0.15), zoom.Minimum, zoom.Maximum);
        var lastScale = cover;
        zoom.ValueChanged += (_, _) =>
        {
            // zoom about the frame's centre
            var s = cover * zoom.Value;
            x = Frame / 2 - (Frame / 2 - x) * s / lastScale; y = Frame / 2 - (Frame / 2 - y) * s / lastScale;
            lastScale = s; Place();
        };
        // start centred
        x = (Frame - photo.PixelWidth * cover) / 2; y = (Frame - photo.PixelHeight * cover) / 2;
        Place();

        var minus = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = B("#A0AAB6"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var plus = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = B("#A0AAB6"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Children = { minus, zoom, plus } };

        var use = Btn("Use picture", true, () => { Picture = Render(); Close(); });
        var cancel = Btn("Cancel", false, Close);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Children = { cancel, use } };

        Content = new StackPanel { Margin = new Thickness(24, 18, 24, 22), Children = {
            new TextBlock { Text = "Drag to move it; zoom with the slider or the mouse wheel.", FontSize = 13, Foreground = B("#A0AAB6"), Margin = new Thickness(0, 0, 0, 16) },
            new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, Child = canvas },
            zoomRow, buttons } };
        IvoFrame.Apply(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); else if (e.Key == Key.Enter) { Picture = Render(); Close(); } };
    }

    // keep the photo covering the whole frame (no empty edges in the circle)
    void Place()
    {
        var s = cover * zoom.Value;
        double w = photo.PixelWidth * s, h = photo.PixelHeight * s;
        x = Math.Min(0, Math.Max(Frame - w, x)); y = Math.Min(0, Math.Max(Frame - h, y));
        image.Width = w; image.Height = h;
        Canvas.SetLeft(image, x); Canvas.SetTop(image, y);
    }

    // what's in the frame, drawn at full quality to a 384 px square
    string Render()
    {
        var k = Out / Frame;
        var s = cover * zoom.Value;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var g = visual.RenderOpen()) g.DrawImage(photo, new Rect(x * k, y * k, photo.PixelWidth * s * k, photo.PixelHeight * s * k));
        var bmp = new RenderTargetBitmap(Out, Out, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream(); enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    Button Btn(string text, bool primary, Action click)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
        face.SetValue(Border.BackgroundProperty, primary ? new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#8B7CFF"), (Color)ColorConverter.ConvertFromString("#4F46E5"), 0) : B("#2B3641"));
        face.SetValue(Border.PaddingProperty, new Thickness(20, 9, 20, 10));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        face.AppendChild(cp);
        var b = new Button { Content = text, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Template = new ControlTemplate(typeof(Button)) { VisualTree = face } };
        b.Click += (_, _) => click();
        return b;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
