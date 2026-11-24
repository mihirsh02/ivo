using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ivo;

// A program running in a Windows pseudo-console (ConPTY): it sees a real terminal, so prompts, colours, Ctrl+C,
// full-screen apps (vim, Claude Code, Copilot…) all work. Input and Output carry the terminal's byte streams.
public sealed class Pty : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFO
    {
        public int cb; public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public nint lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out nint hPC);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(nint hPC, COORD size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(nint hPC);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attrs, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attr, nint value, nint size, nint prev, nint retSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, nint pa, nint ta, bool inherit, uint flags, nint env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(nint h, uint code);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint h, uint ms);

    nint pc, process, attrs;
    public Stream Input { get; }
    public Stream Output { get; }
    public Task Exited { get; }

    Pty(nint pc, nint process, nint attrs, Stream input, Stream output)
    {
        this.pc = pc; this.process = process; this.attrs = attrs; Input = input; Output = output;
        Exited = Task.Run(() => WaitForSingleObject(process, 0xFFFFFFFF));
    }

    public static Pty Start(string file, IEnumerable<string> args, string cwd, int cols, int rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, 0, 0) || !CreatePipe(out var outRead, out var outWrite, 0, 0))
            throw new IOException("Couldn't create the terminal's pipes.");
        if (CreatePseudoConsole(Size(cols, rows), inRead, outWrite, 0, out var pc) != 0)
            throw new IOException("This version of Windows has no pseudo-console (needs Windows 10 1809 or later).");

        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var attrs = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size) ||
            !UpdateProcThreadAttribute(attrs, 0, 0x00020016 /* PSEUDOCONSOLE */, pc, IntPtr.Size, 0, 0))
            throw new IOException("Couldn't attach the pseudo-console.");
        var si = new STARTUPINFOEX { lpAttributeList = attrs };
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        var cmd = new StringBuilder(string.Join(" ", new[] { file }.Concat(args).Select(Quote)));
        if (!CreateProcessW(null, cmd, 0, 0, false, 0x00080000 /* EXTENDED_STARTUPINFO_PRESENT */, 0, cwd, ref si, out var pi))
        {
            var err = Marshal.GetLastWin32Error();
            ClosePseudoConsole(pc); DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs);
            throw new IOException($"Couldn't start {Path.GetFileName(file)} (error {err}).");
        }
        CloseHandle(pi.hThread);
        inRead.Dispose(); outWrite.Dispose();   // the console holds its own copies
        return new Pty(pc, pi.hProcess, attrs, new FileStream(inWrite, FileAccess.Write, 1), new FileStream(outRead, FileAccess.Read, 1));
    }

    public void Resize(int cols, int rows) { if (pc != 0) ResizePseudoConsole(pc, Size(cols, rows)); }

    public void Write(string text)
    {
        try { var b = Encoding.UTF8.GetBytes(text); Input.Write(b); Input.Flush(); } catch { }
    }

    public void Dispose()
    {
        if (pc == 0) return;
        try { Input.Dispose(); } catch { }
        ClosePseudoConsole(pc); pc = 0;            // tells the programs in it the window closed
        TerminateProcess(process, 1); CloseHandle(process);
        DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs);
    }

    static COORD Size(int cols, int rows) => new() { X = (short)Math.Clamp(cols, 2, 999), Y = (short)Math.Clamp(rows, 1, 999) };

    // Windows command-line quoting, as programs' argv parsers expect it.
    static string Quote(string a)
    {
        if (a != "" && !a.Any(c => c is ' ' or '\t' or '"')) return a;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < a.Length; i++)
        {
            int slashes = 0;
            while (i < a.Length && a[i] == '\\') { slashes++; i++; }
            if (i == a.Length) { sb.Append('\\', slashes * 2); break; }
            if (a[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(a[i]);
        }
        return sb.Append('"').ToString();
    }
}
