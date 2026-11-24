using System.Diagnostics;
using System.Reflection;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IvoLauncher;

// IvoInstall-<version>.exe (IvoPortalInstall-<version>.exe for Ivo Portal, built with -p:App=Portal): native code, so it
// opens instantly. A small card ("Preparing Ivo setup", a progress bar) shows while it unpacks ivo.tar.br (the app plus the installer window, sharing one .NET runtime, Brotli-compressed) into
// %TEMP%\<app> setup <version>, then starts the installer window from there. A second run finds it unpacked and skips ahead.
//   --pack <folder> <archive>   (the build) tar the folder and compress it with Brotli
public static unsafe partial class Program
{
    static double progress;
    // the app this installs: the assembly's Product, "Ivo" or "Ivo Portal"
    static readonly string AppName = typeof(Program).Assembly.GetCustomAttribute<System.Reflection.AssemblyProductAttribute>()?.Product ?? "Ivo";
    static string status = $"Preparing {AppName} setup…";
    static nint window;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--pack") { Pack(args[1], args[2]); return 0; }
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var dir = Path.Combine(Path.GetTempPath(), $"{AppName} setup {version}");
        var marker = Path.Combine(dir, ".unpacked");
        using var archive = typeof(Program).Assembly.GetManifestResourceStream("ivo.tar.br");
        if (archive == null) { MessageBox(0, $"This installer was built without {AppName} inside it.", $"{AppName} setup", 0x10); return 1; }
        var stamp = $"{version} {archive.Length}";

        string? error = null;
        Process? started = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (!(File.Exists(marker) && File.ReadAllText(marker) == stamp))   // not unpacked yet (or another build's): unpack
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                    var full = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                    using var brotli = new BrotliStream(archive, CompressionMode.Decompress);
                    using var tar = new TarReader(brotli);
                    while (tar.GetNextEntry() is { } entry)
                    {
                        var to = Path.GetFullPath(Path.Combine(dir, entry.Name));
                        if (!to.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;   // never outside the folder
                        if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(to); continue; }
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        entry.ExtractToFile(to, true);
                        progress = (double)archive.Position / archive.Length;
                        InvalidateRect(window, null, 0);
                    }
                    File.WriteAllText(marker, stamp);
                }
                progress = 1; status = "Starting setup…"; InvalidateRect(window, null, 0);
                AllowSetForegroundWindow(-1);   // the installer's window may come to the front (this card has it now)
                started = Process.Start(new ProcessStartInfo(Path.Combine(dir, "IvoInstaller.exe"), string.Join(" ", args)) { UseShellExecute = false, WorkingDirectory = dir });
                // the card stays until the installer's own window is up, so there's no gap with nothing on screen
                for (int i = 0; i < 150 && started != null && !started.HasExited && started.MainWindowHandle == 0; i++) { Thread.Sleep(100); started.Refresh(); }
            }
            catch (Exception ex) { error = ex.Message; }
            PostMessage(window, 0x0010, 0, 0);   // WM_CLOSE
        }) { IsBackground = true };

        ShowCard();
        worker.Start();
        MSG msg;
        while (GetMessage(&msg, 0, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessage(&msg); }
        if (error != null) { MessageBox(0, $"{AppName} setup couldn't start:\n\n{error}", $"{AppName} setup", 0x10); return 1; }
        return 0;
    }

    static void Pack(string folder, string output)
    {
        var tar = new MemoryStream();
        TarFile.CreateFromDirectory(folder, tar, includeBaseDirectory: false);
        var source = tar.GetBuffer().AsSpan(0, (int)tar.Length);
        var dest = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        // quality 9 with the largest window: within a few percent of 11, and many times faster on Ivo's ~255 MB
        if (!BrotliEncoder.TryCompress(source, dest, out var written, quality: 9, window: 24)) throw new InvalidOperationException("Brotli failed");
        File.WriteAllBytes(output, dest.AsSpan(0, written).ToArray());
        Console.WriteLine($"{tar.Length / 1048576.0:0.0} MB → {written / 1048576.0:0.0} MB");
    }

    // ---------- the card: Ivo's dark canvas, the crystal, a line of text and a progress bar ----------
    static int dpi = 96;
    static int S(int px) => px * dpi / 96;

    static void ShowCard()
    {
        var name = (char*)Marshal.StringToHGlobalUni("IvoLauncherCard");
        var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = GetModuleHandleW(null), lpszClassName = name, hCursor = LoadCursorW(0, 32512) };
        RegisterClassExW(&wc);
        dpi = (int)GetDpiForSystem();
        int w = S(380), h = S(150);
        var sw = GetSystemMetrics(0); var sh = GetSystemMetrics(1);
        fixed (char* title = $"{AppName} setup")
            window = CreateWindowExW(0x00000008 /* topmost */, name, title, 0x80000000 /* popup */, (sw - w) / 2, (sh - h) / 2, w, h, 0, 0, wc.hInstance, null);
        int round = 2; DwmSetWindowAttribute(window, 33, &round, 4);   // rounded corners on Windows 11
        ShowWindow(window, 5); UpdateWindow(window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case 0x000F: Paint(hwnd); return 0;              // WM_PAINT
            case 0x0014: return 1;                           // WM_ERASEBKGND: painted in one go below
            case 0x0002: PostQuitMessage(0); return 0;       // WM_DESTROY
            case 0x0084: return 2;                           // WM_NCHITTEST: drag it by anywhere
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    static void Paint(nint hwnd)
    {
        PAINTSTRUCT ps;
        var dc = BeginPaint(hwnd, &ps);
        RECT r; GetClientRect(hwnd, &r);
        // draw off-screen, then copy, so it never flickers
        var mem = CreateCompatibleDC(dc); var bmp = CreateCompatibleBitmap(dc, r.right, r.bottom); var old = SelectObject(mem, bmp);
        Fill(mem, r, 0x12_0F_0B);                                                       // #0B0F12
        // the app's mark: Ivo's crystal (violet and cyan halves, a light facet), or Ivo Portal's logo (the icon this carries)
        int cx = S(48), cy = S(75), hw = S(17), hh = S(20);
        nint logo = 0, id = 0;
        if (AppName == "Ivo Portal" && PrivateExtractIcons(Environment.ProcessPath!, 0, S(44), S(44), &logo, &id, 1, 0) > 0 && logo != 0)
        {
            DrawIconEx(mem, cx - S(22), cy - S(22), logo, S(44), S(44), 0, 0, 3);
            DestroyIcon(logo);
        }
        else
        {
            Poly(mem, 0xFF_5C_7C, [(cx, cy - hh), (cx, cy + hh), (cx - hw, cy)]);          // #7C5CFF
            Poly(mem, 0xEE_D3_22, [(cx, cy - hh), (cx + hw, cy), (cx, cy + hh)]);          // #22D3EE
            Poly(mem, 0xFF_FF_FF, [(cx, cy - hh), (cx + hw, cy), (cx, cy)], 56);           // the light facet, blended over the cyan
        }
        SetBkMode(mem, 1);
        Text(mem, AppName, S(86), S(36), S(24), 600, 0xFA_F9_F8);
        Text(mem, status, S(86), S(70), S(14), 400, 0xB1_AC_A6);
        // progress: a track and its fill, rounded
        var track = new RECT { left = S(86), top = S(104), right = r.right - S(28), bottom = S(110) };
        Round(mem, track, 0x36_2C_22);
        var fill = track; fill.right = track.left + Math.Max(S(6), (int)((track.right - track.left) * Math.Clamp(progress, 0, 1)));
        Round(mem, fill, 0xFF_79_85);                                                   // #8579FF
        BitBlt(dc, 0, 0, r.right, r.bottom, mem, 0, 0, 0x00CC0020);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem);
        EndPaint(hwnd, &ps);
    }

    static void Fill(nint dc, RECT r, uint bgr) { var b = CreateSolidBrush(bgr); FillRect(dc, &r, b); DeleteObject(b); }
    // shapes go through GDI+ with anti-aliasing (plain GDI draws them with jagged edges)
    static nint gdiplus;
    static uint Argb(uint bgr, byte a = 255) => (uint)a << 24 | (bgr & 0xFF) << 16 | (bgr & 0xFF00) | (bgr >> 16 & 0xFF);
    static void Smooth(nint dc, Action<nint> draw)
    {
        if (gdiplus == 0) { var input = new GdiplusStartupInput { GdiplusVersion = 1 }; GdiplusStartup(out gdiplus, &input, 0); }
        GdipCreateFromHDC(dc, out var g);
        GdipSetSmoothingMode(g, 4);         // anti-alias
        GdipSetPixelOffsetMode(g, 4);       // half-pixel offset: crisper edges
        draw(g);
        GdipDeleteGraphics(g);
    }
    static void Round(nint dc, RECT r, uint bgr) => Smooth(dc, g =>
    {
        GdipCreateSolidFill(Argb(bgr), out var b);
        float h = r.bottom - r.top;
        GdipFillEllipse(g, b, r.left, r.top, h, h);
        GdipFillEllipse(g, b, r.right - h, r.top, h, h);
        GdipFillRectangle(g, b, r.left + h / 2, r.top, Math.Max(0, r.right - r.left - h), h);
        GdipDeleteBrush(b);
    });
    static void Poly(nint dc, uint bgr, (int X, int Y)[] pts, byte alpha = 255) => Smooth(dc, g =>
    {
        GdipCreateSolidFill(Argb(bgr, alpha), out var b);
        var arr = stackalloc PointF[pts.Length];
        for (int i = 0; i < pts.Length; i++) arr[i] = new PointF { X = pts[i].X, Y = pts[i].Y };
        GdipFillPolygon2(g, b, arr, pts.Length);
        GdipDeleteBrush(b);
    });
    [StructLayout(LayoutKind.Sequential)] struct PointF { public float X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct GdiplusStartupInput { public uint GdiplusVersion; public nint DebugEventCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }
    [LibraryImport("user32.dll")] private static partial int AllowSetForegroundWindow(int pid);
    [LibraryImport("gdiplus.dll")] private static partial int GdiplusStartup(out nint token, GdiplusStartupInput* input, nint output);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateFromHDC(nint dc, out nint graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteGraphics(nint graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetSmoothingMode(nint graphics, int mode);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetPixelOffsetMode(nint graphics, int mode);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateSolidFill(uint argb, out nint brush);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteBrush(nint brush);
    [LibraryImport("gdiplus.dll")] private static partial int GdipFillPolygon2(nint graphics, nint brush, PointF* points, int count);
    [LibraryImport("gdiplus.dll")] private static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float w, float h);
    [LibraryImport("gdiplus.dll")] private static partial int GdipFillRectangle(nint graphics, nint brush, float x, float y, float w, float h);
    static void Text(nint dc, string s, int x, int y, int size, int weight, uint bgr)
    {
        nint font;
        fixed (char* face = "Segoe UI Variable Display") font = CreateFontW(-size, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5 /* ClearType */, 0, face);
        var of = SelectObject(dc, font);
        SetTextColor(dc, bgr);
        fixed (char* t = s) TextOutW(dc, x, y, t, s.Length);
        SelectObject(dc, of); DeleteObject(font);
    }

    // ---------- Win32 ----------
    [StructLayout(LayoutKind.Sequential)] struct WNDCLASSEXW { public uint cbSize, style; public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public char* lpszMenuName, lpszClassName; public nint hIconSm; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint exStyle, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, void* param);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] private static partial int UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);
    static int GetMessage(MSG* m, nint h, uint a, uint b) => GetMessageW(m, h, a, b);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(MSG* msg);
    static nint DispatchMessage(MSG* m) => DispatchMessageW(m);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [LibraryImport("user32.dll")] private static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] private static partial int PostMessageW(nint hwnd, uint msg, nint w, nint l);
    static void PostMessage(nint h, uint m, nint w, nint l) => PostMessageW(h, m, w, l);
    [LibraryImport("user32.dll")] private static partial int InvalidateRect(nint hwnd, RECT* r, int erase);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] private static partial int GetClientRect(nint hwnd, RECT* r);
    [LibraryImport("user32.dll")] private static partial int FillRect(nint dc, RECT* r, nint brush);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int i);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] private static partial nint LoadCursorW(nint inst, nint id);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);
    static void MessageBox(nint h, string t, string c, uint ty) => MessageBoxW(h, t, c, ty);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(nint hwnd, int attr, void* value, int size);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] private static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial int RoundRect(nint dc, int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial int Polygon(nint dc, POINT* pts, int count);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint PrivateExtractIcons(string file, int index, int cx, int cy, nint* icons, nint* ids, uint count, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, int step, nint brush, int flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyIcon(nint icon);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] private static partial int TextOutW(nint dc, int x, int y, char* text, int length);
    [LibraryImport("gdi32.dll")] private static partial nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, char* face);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial int BitBlt(nint dc, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
}
