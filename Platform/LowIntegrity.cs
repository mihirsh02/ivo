using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ivo;

// Starts a process at Low integrity (like a browser's sandboxed tab): it can read most files but can't write to your
// documents, settings, the registry or other programs, and can't drive higher-integrity windows. Its stdin / stdout /
// stderr are piped back, so an MCP server works as usual. Servers that need to write to their own cache in your profile
// (npm, pip) may fail under it, so it's a per-server choice ("sandbox": true in the MCP config).
public static class LowIntegrity
{
    public sealed record Started(Process Process, StreamWriter Input, StreamReader Output, StreamReader Errors);

    public static Started Start(string file, IEnumerable<string> args, IDictionary<string, string> env, string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);   // must be given: an inherited one can fail with "invalid name"
        if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0008 | 0x0080 | 0x0001, out var token)) throw Fail("OpenProcessToken");   // DUPLICATE | QUERY | ADJUST_DEFAULT | ASSIGN_PRIMARY
        nint low = 0, sid = 0;
        try
        {
            if (!DuplicateTokenEx(token, 0x02000000, 0, 2, 1, out low)) throw Fail("DuplicateTokenEx");   // MAXIMUM_ALLOWED, impersonation, primary
            if (!ConvertStringSidToSid("S-1-16-4096", out sid)) throw Fail("ConvertStringSidToSid");      // Low mandatory level
            var label = new TOKEN_MANDATORY_LABEL { Sid = sid, Attributes = 0x20 };                       // SE_GROUP_INTEGRITY
            var size = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(sid);
            if (!SetTokenInformation(low, 25, ref label, size)) throw Fail("SetTokenInformation");       // TokenIntegrityLevel

            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            Pipe(ref sa, out var inRead, out var inWrite, parentReads: false);
            Pipe(ref sa, out var outRead, out var outWrite, parentReads: true);
            Pipe(ref sa, out var errRead, out var errWrite, parentReads: true);

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = 0x100, hStdInput = inRead, hStdOutput = outWrite, hStdError = errWrite };   // STARTF_USESTDHANDLES
            var cmd = new StringBuilder(Quote(file));
            foreach (var a in args) cmd.Append(' ').Append(Quote(a));
            var block = new StringBuilder();
            var all = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in env) all[k] = v;
            foreach (var (k, v) in all.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) block.Append(k).Append('=').Append(v).Append('\0');
            block.Append('\0');

            var ok = CreateProcessAsUser(low, null, cmd, 0, 0, true, 0x08000000 | 0x00000400, env.Count == 0 ? null : block.ToString(), cwd, ref si, out var pi);   // CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT
            CloseHandle(inRead); CloseHandle(outWrite); CloseHandle(errWrite);   // the child's ends
            if (!ok) { CloseHandle(inWrite); CloseHandle(outRead); CloseHandle(errRead); throw Fail("CreateProcessAsUser"); }
            CloseHandle(pi.hThread);
            var proc = Process.GetProcessById(pi.dwProcessId);
            CloseHandle(pi.hProcess);
            var utf8 = new UTF8Encoding(false);
            return new(proc,
                new StreamWriter(new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write, 1), utf8) { AutoFlush = false },
                new StreamReader(new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 1), utf8),
                new StreamReader(new FileStream(new SafeFileHandle(errRead, true), FileAccess.Read, 1), utf8));
        }
        finally
        {
            if (sid != 0) LocalFree(sid);
            if (low != 0) CloseHandle(low);
            CloseHandle(token);
        }
    }

    static void Pipe(ref SECURITY_ATTRIBUTES sa, out nint read, out nint write, bool parentReads)
    {
        if (!CreatePipe(out read, out write, ref sa, 0)) throw Fail("CreatePipe");
        SetHandleInformation(parentReads ? read : write, 1, 0);   // the parent's end isn't inherited
    }

    // Windows command-line quoting (CommandLineToArgvW rules)
    static string Quote(string a)
    {
        if (a != "" && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new StringBuilder("\"");
        for (int i = 0, slashes = 0; i < a.Length; i++)
        {
            if (a[i] == '\\') { slashes++; continue; }
            sb.Append('\\', a[i] == '"' ? slashes * 2 + 1 : slashes).Append(a[i]);
            slashes = 0;
        }
        var trailing = a.Length - a.TrimEnd('\\').Length;
        sb.Append('\\', trailing * 2).Append('"');
        return sb.ToString();
    }

    static Exception Fail(string what) => new($"Couldn't start the server sandboxed ({what}, error {Marshal.GetLastWin32Error()}).");

    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public nint lpSecurityDescriptor; public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_MANDATORY_LABEL { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(nint token, uint access, nint attrs, int level, int type, out nint newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSid(string sid, out nint psid);
    [DllImport("advapi32.dll")] static extern int GetLengthSid(nint sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(nint token, int cls, ref TOKEN_MANDATORY_LABEL info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out nint read, out nint write, ref SECURITY_ATTRIBUTES sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(nint handle, int mask, int flags);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(nint token, string? app, StringBuilder cmd, nint pa, nint ta, bool inherit, int flags, string? env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint h);
}
