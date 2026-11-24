using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ivo;

// What Portal's calls use (Ivo and Ivo Portal set these from their own settings): the camera, the microphone and the
// speakers, and where Chats saves the files people send. And what to share: a whole screen, or one app's window.
public static class PortalDevices
{
    public static string? Camera;     // a camera's id ("" or null: Windows' default)
    public static int Mic = -1, Speaker = -1;   // a device's number (-1: Windows' default)
    public static bool MessageSound = true, CallSound = true;   // the chime for a new message; the ringtone, calling tone and connect chime
    public static bool ReadReceipts = true;   // send "Seen"; off also hides others' (as WhatsApp does)
    public static readonly string[] DefaultReactions = ["❤️", "😂", "😮", "😢", "😡", "👍"];
    public static string[] Reactions = DefaultReactions;   // the six in a message's right-click menu
    public static bool Silenced(string kind) => kind == "message" && !MessageSound || kind == "call" && !CallSound;
    public static string Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Ivo Chats");

    public static async Task<List<(string Id, string Name)>> Cameras()
    {
        try { return (await Windows.Media.Capture.Frames.MediaFrameSourceGroup.FindAllAsync()).SelectMany(g => g.SourceInfos).Where(i => i.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color && i.DeviceInformation != null).Select(i => (i.DeviceInformation.Id, i.DeviceInformation.Name)).DistinctBy(d => d.Id).ToList(); }
        catch { return []; }
    }

    public static List<string> Mics() => Enumerable.Range(0, NAudio.Wave.WaveIn.DeviceCount).Select(i => NAudio.Wave.WaveIn.GetCapabilities(i).ProductName).ToList();
    public static List<string> Speakers() => Enumerable.Range(0, NAudio.Wave.WaveOut.DeviceCount).Select(i => NAudio.Wave.WaveOut.GetCapabilities(i).ProductName).ToList();
}

// what a screen share sends: a whole monitor, or one window (even when it's behind others)
public record ShareTarget(string Name, System.Drawing.Rectangle Screen, nint Window);

// the picker: every screen and every open app, each as a thumbnail; your computer's sound is never sent, only your voice
public class ScreenPicker : Window
{
    public ShareTarget? Picked;
    static Brush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    public ScreenPicker()
    {
        Title = "Share your screen"; Width = 860; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.CanResize;
        Background = B("#1E2730"); Foreground = B("#E6EDF3"); FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var page = new StackPanel { Margin = new Thickness(26, 20, 26, 20) };
        page.Children.Add(new TextBlock { Text = "Share your screen", FontSize = 20, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = "Pick a whole screen, or one app (only that window is sent, even when it's behind others). Your computer's sound isn't shared: they hear only your voice.",
                                          FontSize = 13, Foreground = B("#A0AAB6"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18) });

        page.Children.Add(Heading("Screens"));
        var screens = new WrapPanel();
        var all = Monitors();
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            screens.Children.Add(Tile(s.Primary ? $"Screen {i + 1} (main)" : $"Screen {i + 1}", Grab(s.Bounds, 0), new ShareTarget($"Screen {i + 1}", s.Bounds, 0)));
        }
        page.Children.Add(screens);

        page.Children.Add(Heading("Apps"));
        var apps = new WrapPanel();
        var me = Environment.ProcessId;
        foreach (var (hwnd, title) in Windows())
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == me) continue;
            apps.Children.Add(Tile(title, Grab(Bounds(hwnd), hwnd), new ShareTarget(title, default, hwnd)));
        }
        page.Children.Add(apps);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        IvoFrame.Apply(this);
    }

    TextBlock Heading(string t) => new() { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B("#A0AAB6"), Margin = new Thickness(0, 4, 0, 10) };

    FrameworkElement Tile(string name, BitmapSource? thumb, ShareTarget target)
    {
        var img = new Image { Source = thumb, Stretch = Stretch.Uniform, Height = 120 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var label = new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 8, 2, 0) };
        var card = new Border { Width = 240, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(10), CornerRadius = new CornerRadius(14), Background = B("#2B3641"), Cursor = Cursors.Hand,
                                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                                Child = new StackPanel { Children = { new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = img }, label } } };
        card.MouseEnter += (_, _) => card.BorderBrush = B("#8B7CFF"); card.MouseLeave += (_, _) => card.BorderBrush = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, _) => { Picked = target; Close(); };
        return card;
    }

    // the open apps: visible, titled, not tool windows, not minimised
    static List<(nint Hwnd, string Title)> Windows()
    {
        var found = new List<(nint, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true;   // WS_EX_TOOLWINDOW
            DwmGetWindowAttribute(h, 14, out int cloaked, 4); if (cloaked != 0) return true;   // hidden by Windows (other desktops, suspended apps)
            var len = GetWindowTextLength(h); if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1); GetWindowText(h, sb, sb.Capacity);
            var r = Bounds(h); if (r.Width < 80 || r.Height < 60) return true;
            found.Add((h, sb.ToString()));
            return true;
        }, 0);
        return found;
    }

    public static System.Drawing.Rectangle Bounds(nint h)
    {
        if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(h, out r);   // without the invisible resize border
        return new System.Drawing.Rectangle(r.L, r.T, r.R - r.L, r.B - r.T);
    }

    // a picture of a screen (from the screen) or of a window (from the window itself, so it shows even when covered)
    public static System.Drawing.Bitmap? Capture(System.Drawing.Rectangle screen, nint window)
    {
        try
        {
            if (window != 0)
            {
                var b = Bounds(window);
                if (b.Width <= 0 || b.Height <= 0) return null;
                var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                var hdc = g.GetHdc();
                try { PrintWindow(window, hdc, 2); } finally { g.ReleaseHdc(hdc); }   // PW_RENDERFULLCONTENT
                return bmp;
            }
            var s = new System.Drawing.Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(s)) g.CopyFromScreen(screen.Left, screen.Top, 0, 0, s.Size);
            return s;
        }
        catch { return null; }
    }

    static BitmapSource? Grab(System.Drawing.Rectangle screen, nint window)
    {
        using var bmp = Capture(screen, window);
        if (bmp == null) return null;
        var h = bmp.GetHbitmap();
        try { var src = Imaging.CreateBitmapSourceFromHBitmap(h, 0, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(Math.Min(480, bmp.Width), (int)(Math.Min(480, bmp.Width) * (double)bmp.Height / bmp.Width))); src.Freeze(); return src; }
        finally { DeleteObject(h); }
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(nint h, nint l);
    // every screen: where it is, and whether it's the main one (from Windows, not WinForms)
    static List<(System.Drawing.Rectangle Bounds, bool Primary)> Monitors()
    {
        var found = new List<(System.Drawing.Rectangle, bool)>();
        EnumDisplayMonitors(0, 0, (m, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref info)) found.Add((new System.Drawing.Rectangle(info.rcMonitor.L, info.rcMonitor.T, info.rcMonitor.R - info.rcMonitor.L, info.rcMonitor.B - info.rcMonitor.T), (info.dwFlags & 1) != 0));
            return true;
        }, 0);
        return found;
    }
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorProc(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc f, nint data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, nint l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(nint h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out int pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint hdc, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out int v, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint o);
}
