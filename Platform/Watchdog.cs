using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace Ivo;

// How Ivo is doing, in perf.log (%AppData%\Ivo): how long each launch took, and a line whenever the window stops
// responding for 2 seconds or more. Only lines: nothing is snapshotted (a process dumping itself can deadlock).
static class Watchdog
{
    static readonly string LogFile = Path.Combine(Store.Dir, "perf.log");

    public static void Log(string line)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}"); } catch { }
    }

    public static void Start(Dispatcher ui)
    {
        new Thread(() =>
        {
            while (true)
            {
                var answered = new ManualResetEventSlim();
                var asked = Stopwatch.StartNew();
                ui.BeginInvoke(answered.Set, DispatcherPriority.Send);
                if (answered.Wait(2000)) { Thread.Sleep(500); continue; }
                answered.Wait();
                Log($"not responding for {asked.Elapsed.TotalSeconds:0.0} s");
            }
        }) { IsBackground = true, Name = "Ivo watchdog" }.Start();
    }
}
