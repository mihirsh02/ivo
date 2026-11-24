using System.IO;

namespace Ivo;

// Ivo's sounds, each with its own on/off and volume (Settings → Voice → Audio → Sounds), under App sounds:
//   reminder  the chime (Assets/chime.wav, tools/sounds/make_chime.py): two soft rising notes, for reminders and habits
//   message   the same chime, for a new chat message
//   call      the ringtone (Assets/ring.wav, tools/sounds/make_ringtone.py), looped while a call rings (30 s at most),
//             and the chime when a call connects
//   wheel     the wheels' detent tick and settle snap, made here, a few milliseconds of tone each
// Each kind has its own player, so they can overlap: a message chime doesn't cut a ringing call short.
public static class Sounds
{
    public static bool Enabled = true;
    public static Func<string, int> Level = _ => 100;   // a kind's volume, 0 (off) … 100; "master" scales them all
    public static readonly (string Kind, string Name, string Note)[] Kinds =
    [
        ("reminder", "Reminders", "the chime, for reminders and habits"), ("message", "Messages", "the chime, for a new chat message"),
        ("call", "Calls", "the ringtone, and a chime when a call connects"),
        ("wheel", "Wheels", "a soft tick as a wheel turns"),
    ];

    static string Asset(params string[] p) => Path.Combine([AppContext.BaseDirectory, "Assets", .. p]);
    static readonly Dictionary<string, System.Windows.Media.MediaPlayer> players = [];

    // plays a file as one kind of sound (force: play even with it switched off, for Try it: at its volume, or full if that's 0)
    static void Play(string kind, string file, bool loop = false, bool force = false)
    {
        var volume = Level(kind) / 100.0 * Level("master") / 100.0;
        if (force) volume = volume > 0 ? volume : 1; else if (!Enabled) volume = 0;
        if (volume <= 0 || !File.Exists(file)) return;
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null) return;
        ui.BeginInvoke(() =>
        {
            try
            {
                var key = loop ? kind + ":" + Path.GetFileName(file) : kind;   // each looping sound its own player (ringtone, calling tone)
                if (!players.TryGetValue(key, out var p))
                {
                    players[key] = p = new System.Windows.Media.MediaPlayer();
                    p.MediaEnded += (_, _) => { if (looping.Contains(key)) { p.Position = TimeSpan.Zero; p.Play(); } };
                }
                if (loop) looping.Add(key);
                if (p.Source?.LocalPath != file) p.Open(new Uri(file));
                p.Volume = volume;
                p.Position = TimeSpan.Zero;
                p.Play();
            }
            catch { }
        });
    }
    static readonly HashSet<string> looping = [];

    public static void Chime(string kind = "reminder", bool force = false) => Play(kind, Asset("chime.wav"), force: force);

    // a call ringing: the ringtone on a loop until it's answered, declined or 30 seconds pass
    static System.Threading.Timer? ringStop;
    public static void Ring(bool force = false)
    {
        Play("call", Asset("ring.wav"), loop: true, force: force);
        ringStop?.Dispose();
        ringStop = new System.Threading.Timer(_ => StopRing(), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }
    public static void StopRing()
    {
        ringStop?.Dispose(); ringStop = null;
        Stop("call:ring.wav");
    }
    static void Stop(string key) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { looping.Remove(key); if (players.TryGetValue(key, out var p)) p.Stop(); });

    // calling someone: a soft double ring on a loop while it waits for them (at the Calls volume), until they answer or it ends
    static readonly Lazy<string> ringback = new(RingbackFile);
    static System.Threading.Timer? callingStop;
    public static void Calling()
    {
        Play("call", ringback.Value, loop: true);
        callingStop?.Dispose();
        callingStop = new System.Threading.Timer(_ => StopCalling(), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
    }
    public static void StopCalling() { callingStop?.Dispose(); callingStop = null; Stop("call:calling.wav"); }

    // 0.4 s of 400 + 450 Hz, 0.2 s quiet, 0.4 s again, then 2 s quiet: the ring people know from a phone line
    static string RingbackFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "ivo-sounds", "calling.wav");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            const int rate = 44100;
            var n = rate * 3;
            using var w = new BinaryWriter(File.Create(file));
            w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                var t = (double)i / rate;
                var on = t < 0.4 ? t : t is >= 0.6 and < 1.0 ? t - 0.6 : -1;   // where in a ring we are, or -1 between rings
                var edge = on < 0 ? 0 : Math.Min(1, Math.Min(on, 0.4 - on) * 200);   // 5 ms fades: no clicks
                var v = (Math.Sin(2 * Math.PI * 400 * t) + Math.Sin(2 * Math.PI * 450 * t)) * 0.5 * edge;
                w.Write((short)(Math.Clamp(v * 0.28, -1, 1) * short.MaxValue));
            }
        }
        catch { }
        return file;
    }

    // a wheel passing an item: a short, woody click (at most one every 30 ms, so a fast spin is a purr, not a buzz)
    static readonly Lazy<string> tick = new(() => ToneFile("tick", 0.022, 2100, 0.0028, 0.22));
    // a wheel settling on an item: a lower, softer knock
    static readonly Lazy<string> snap = new(() => ToneFile("snap", 0.05, 950, 0.008, 0.2));
    static DateTime lastTick;

    public static void Tick(bool force = false)
    {
        if ((DateTime.Now - lastTick).TotalMilliseconds < 30) return;
        lastTick = DateTime.Now;
        Play("wheel", tick.Value, force: force);
    }

    public static void Snap(bool force = false) => Play("wheel", snap.Value, force: force);

    // a sine (with a touch of its octave) that dies away fast: 44.1 kHz, 16-bit mono, kept as a WAV in the temp folder
    static string ToneFile(string name, double seconds, double hz, double decay, double volume)
    {
        var file = Path.Combine(Path.GetTempPath(), "ivo-sounds", name + ".wav");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            const int rate = 44100;
            var n = (int)(rate * seconds);
            using var w = new BinaryWriter(File.Create(file));
            w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                var t = (double)i / rate;
                var v = (Math.Sin(2 * Math.PI * hz * t) + 0.35 * Math.Sin(4 * Math.PI * hz * t)) * Math.Exp(-t / decay) * Math.Min(1, t * 4000);
                w.Write((short)(Math.Clamp(v * volume, -1, 1) * short.MaxValue));
            }
        }
        catch { }
        return file;
    }
}

// The wheels' magnet: between two items the wheel leans toward the nearer one, so dragging feels like detents
// (it holds, then pulls into the next). Continuous at the half-way point, so nothing jumps.
public static class Magnet
{
    public static double Pull(double pos)
    {
        var m = Math.Round(pos); var f = pos - m;   // -0.5 … 0.5 from the nearest item
        return m + Math.Sign(f) * 0.5 * Math.Pow(Math.Abs(f) * 2, 2.2);
    }

    // a tick each time the wheel's front item changes; a snap when it comes to rest
    public static void Passed(ref int last, double pos)
    {
        var now = (int)Math.Round(pos);
        if (now != last) { last = now; Sounds.Tick(); }
    }
}
