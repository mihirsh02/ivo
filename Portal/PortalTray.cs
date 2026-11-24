using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Ivo;

// Ivo Portal's tray icon, straight from Windows (Shell_NotifyIcon), so the app needn't ship WinForms (33 MB) for it.
// A hidden message-only window gets its clicks; it comes back by itself if Explorer restarts.
sealed class PortalTray : IDisposable
{
    const int TrayMessage = 0x8001, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    readonly HwndSource window;
    readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    NOTIFYICONDATA data;
    public event Action? Left, Right;

    public PortalTray(string tip)
    {
        window = new HwndSource(new HwndSourceParameters("Ivo Portal tray") { WindowStyle = 0, ParentWindow = new IntPtr(-3) });   // -3: message-only
        window.AddHook(Proc);
        data = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = window.Handle, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                                    uCallbackMessage = TrayMessage, hIcon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath!, 0), szTip = tip };
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    IntPtr Proc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayMessage)
        {
            var what = (int)(lParam.ToInt64() & 0xFFFF);
            if (what == WM_LBUTTONUP) Left?.Invoke(); else if (what == WM_RBUTTONUP) Right?.Invoke();
            handled = true;
        }
        else if (msg == taskbarCreated) Shell_NotifyIcon(NIM_ADD, ref data);   // Explorer restarted: the icon again
        return IntPtr.Zero;
    }

    // where the pointer is, in screen pixels (for the tray's menu)
    public static (int X, int Y) Pointer() { GetCursorPos(out var p); return (p.X, p.Y); }

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (data.hIcon != IntPtr.Zero) DestroyIcon(data.hIcon);
        window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    struct POINT { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
}
