using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Ivo;

// "Run as administrator" for agent commands: one UAC prompt starts Ivo.exe --elevated-host as admin,
// which then runs each command it's sent over a private named pipe, so later commands don't prompt again.
// Ivo owns the pipe; each side checks the other's process ID, so no other program can use the admin helper
// or pose as Ivo to it. The helper exits when Ivo closes, when elevation is turned off, or on Stop.
public static class Elevation
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(nint pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(nint pipe, out uint pid);

    static NamedPipeServerStream? pipe;
    static StreamReader? reader;
    static StreamWriter? writer;
    static Process? host;
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool Active => pipe is { IsConnected: true } && host is { HasExited: false };

    // Shows the UAC prompt (only when no helper is running). False = the user said no.
    public static async Task<bool> Start()
    {
        if (Active) return true;
        Stop();
        var name = "ivo-elevated-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--elevated-host {name} {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { server.Dispose(); return false; }   // UAC declined
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await server.WaitForConnectionAsync(wait.Token); }
        catch (OperationCanceledException) { server.Dispose(); throw new Exception("The administrator prompt wasn't accepted in time."); }   // not a user Stop
        if (host == null || !GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid) || pid != host.Id)
        {
            server.Dispose();
            throw new Exception("Something other than Ivo's helper connected to the elevation pipe, so it was refused.");
        }
        pipe = server;
        reader = new StreamReader(server);
        writer = new StreamWriter(server) { AutoFlush = true };
        return true;
    }

    // ponytail: stopping a running elevated command drops the helper (the next one asks UAC again) rather than
    // signalling it; the helper's own 2-minute limit ends the command.
    public static async Task<string> Run(string cmd, string cwd, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (!Active && !await Start()) return "Error: administrator access was declined, so the command didn't run.";
            await writer!.WriteLineAsync(new JsonObject { ["cmd"] = cmd, ["cwd"] = cwd }.ToJsonString().AsMemory(), ct);
            var line = await reader!.ReadLineAsync(ct) ?? throw new Exception("The administrator helper closed unexpectedly.");
            return (string?)JsonNode.Parse(line)?["out"] ?? "";
        }
        catch (OperationCanceledException) { Stop(); throw; }
        catch (IOException) { Stop(); return "Error: lost the administrator helper. Run the command again to re-approve."; }
        finally { Gate.Release(); }
    }

    public static void Stop()
    {
        pipe?.Dispose();   // the helper sees the pipe close and exits
        pipe = null; reader = null; writer = null; host = null;
    }

    // ---- the elevated side ----
    public static async Task Host(string name, int parentPid)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(15_000);
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid) || pid != parentPid) return;   // not our Ivo
        using var r = new StreamReader(client);
        using var w = new StreamWriter(client) { AutoFlush = true };
        while (await r.ReadLineAsync() is { } line)
        {
            var req = JsonNode.Parse(line);
            var output = await Agent.Shell((string?)req?["cmd"] ?? "", (string?)req?["cwd"] ?? Environment.CurrentDirectory, CancellationToken.None);
            await w.WriteLineAsync(new JsonObject { ["out"] = output }.ToJsonString());
        }
    }
}
