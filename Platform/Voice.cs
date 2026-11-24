using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Windows.Media.SpeechSynthesis;

namespace Ivo;

// Read aloud and dictation. Two kinds of voices, both offline:
//  • Natural: Kokoro (open source, 82M), voices in English (American, British), Hindi,
//    Spanish, French, Italian and Portuguese; run in-process by sherpa-onnx (its .NET package), so any script reads right.
//  • Standard: the voices built into Windows (more from Windows' speech settings, e.g. Indian English Heera and Ravi).
// Voice ids: "kokoro:af_heart", "kokoro:hf_alpha@en" (a Hindi voice reading English), "win:<Windows voice id>".
public static class Voice
{
    public static string Dir => Path.Combine(Store.Dir, "voices");
    // the natural voices come in two packs, same voices: smaller (int8) or full quality. With both, the better one is used.
    public record KokoroPack(string Name, string Note, string Folder, string Model, double Mb);
    public static readonly KokoroPack[] Packs =
    [
        new("Standard quality", "Smaller and quicker to start; a little less crisp.", "kokoro-int8-multi-lang-v1_0", "model.int8.onnx", 126),
        new("High quality", "The clearest, most natural sound; a moment longer per sentence.", "kokoro-multi-lang-v1_0", "model.onnx", 350),
    ];
    public static bool Has(KokoroPack p) => File.Exists(Path.Combine(Dir, p.Folder, p.Model));
    public static KokoroPack? ActivePack => Packs.LastOrDefault(Has);
    static readonly HttpClient http = new(Net.Handler()) { Timeout = TimeSpan.FromMinutes(20) };

    // One voice, whatever its engine. Installed: ready to speak; Size: what downloading it costs (MB, 0 = in a pack).
    public record Entry(string Id, string Name, bool Natural, string Language, string Accent, string Gender, string Detail, bool Installed, double Size);

    // ---------- the catalogue ----------
    // Kokoro v1.0: speaker id = position (0..52 alphabetical, em_santa added at 53). Japanese and Chinese voices are
    // left out: this pack reads them only with extra dictionaries.
    static readonly string[] KokoroIds =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
        "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
        "ef_dora", "em_alex", "ff_siwis", "hf_alpha", "hf_beta", "hm_omega", "hm_psi", "if_sara", "im_nicola",
        "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro", "jm_kumo", "pf_dora", "pm_alex", "pm_santa",
        "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi", "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang", "em_santa",
    ];
    static (string Language, string Accent, string Lang, string Lexicon)? KokoroLang(char c) => c switch
    {
        'a' => ("English", "American", "en-us", "lexicon-us-en.txt"), 'b' => ("English", "British", "en", "lexicon-gb-en.txt"),   // "en" is espeak's British; "en-gb" crashes sherpa-onnx 1.12
        'h' => ("Hindi", "Indian", "hi", ""), 'e' => ("Spanish", "Spanish", "es", ""), 'f' => ("French", "French", "fr", ""),
        'i' => ("Italian", "Italian", "it", ""), 'p' => ("Portuguese", "Brazilian", "pt-br", ""), _ => null,
    };
    // a few words on each voice's character (the Kokoro team's grades and descriptions)
    static readonly Dictionary<string, string> KokoroNotes = new()
    {
        ["af_heart"] = "warm, the most natural", ["af_bella"] = "expressive", ["af_nicole"] = "soft, close", ["af_sarah"] = "clear", ["af_sky"] = "light",
        ["am_michael"] = "steady", ["am_fenrir"] = "deep", ["am_puck"] = "lively", ["bf_emma"] = "polished", ["bf_isabella"] = "bright",
        ["bm_george"] = "classic", ["bm_fable"] = "storyteller", ["hf_alpha"] = "clear", ["hf_beta"] = "gentle", ["hm_omega"] = "calm", ["hm_psi"] = "steady",
    };

    public static bool KokoroInstalled => ActivePack != null;

    static IEnumerable<Entry> KokoroEntries()
    {
        var list = KokoroIds.Select(id => (id, l: KokoroLang(id[0]))).Where(x => x.l != null).Select(x =>
            new Entry("kokoro:" + x.id, char.ToUpper(x.id[3]) + x.id[4..], true, x.l!.Value.Language, x.l.Value.Accent, x.id[1] == 'f' ? "Female" : "Male",
                      KokoroNotes.GetValueOrDefault(x.id, ""), KokoroInstalled, 0)).ToList();
        return list;
    }

    // one word for an accent, whichever library describes it ("United States" → American)
    static string AccentOf(string country) => country switch
    {
        "United States" => "American", "Great Britain" or "United Kingdom" => "British", "India" => "Indian", "Australia" => "Australian",
        "Canada" => "Canadian", "Ireland" => "Irish", "Spain" => "Spanish", "Mexico" => "Mexican", "Argentina" => "Argentinian", "France" => "French",
        "Germany" => "German", "Austria" => "Austrian", "Switzerland" => "Swiss", "Italy" => "Italian", "Brazil" => "Brazilian", "Portugal" => "Portuguese",
        "Netherlands" => "Dutch", "Belgium" => "Belgian", "China" => "Chinese", "Russia" => "Russian", "Ukraine" => "Ukrainian", "Poland" => "Polish",
        "Turkey" => "Turkish", "Greece" => "Greek", "Japan" => "Japanese", "Nepal" => "Nepali", "Vietnam" => "Vietnamese", "Iran" => "Iranian",
        _ => country,
    };

    // Windows' voices: language and accent from their language tag (en-IN → English, Indian)
    static IEnumerable<Entry> WindowsEntries() => SpeechSynthesizer.AllVoices.Select(v =>
    {
        string language = v.Language, accent = "";
        try
        {
            var c = new System.Globalization.CultureInfo(v.Language);
            language = (c.IsNeutralCulture ? c : c.Parent).EnglishName;
            var region = c.EnglishName.Contains('(') ? c.EnglishName[(c.EnglishName.IndexOf('(') + 1)..].TrimEnd(')') : "";
            accent = region == "" ? "" : AccentOf(region);
        }
        catch { }
        var name = v.DisplayName.Replace("Microsoft ", "");
        if (name.Contains(" - ")) name = name[..name.IndexOf(" - ")];
        return new Entry("win:" + v.Id, name, false, language, accent, v.Gender == VoiceGender.Female ? "Female" : "Male", "Windows voice", true, 0);
    });

    public static List<Entry> All() => [.. KokoroEntries(), .. WindowsEntries()];

    // the voice used when none is chosen: an Indian English Windows voice if there is one, else Windows' default
    public static string Default() =>
        "win:" + (SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.Equals("en-IN", StringComparison.OrdinalIgnoreCase)) ?? SpeechSynthesizer.DefaultVoice).Id;

    public static bool Ready(string voice) =>
        voice.StartsWith("kokoro:") ? KokoroInstalled : voice.StartsWith("win:") && SpeechSynthesizer.AllVoices.Any(v => "win:" + v.Id == voice);

    public class NoVoiceException() : Exception("Pick a voice first: Settings → Voice.");

    // ---------- speaking: one reading at a time ----------
    static System.Windows.Media.MediaPlayer? player;
    static TaskCompletionSource? finished;
    static int playing;          // bumps on every Speak/Stop, so a reading that was replaced stops quietly
    static readonly List<string> files = [];
    public static bool Paused { get; private set; }
    public static double Volume { get; set; } = 1;   // 1 = as made; up to 2 boosts it
    public static bool Busy => finished != null;

    // Reads the text; done() runs exactly once when this reading ends for any reason (finished, stopped, replaced, failed).
    public static async Task Speak(string text, string voice, double rate, Action done)
    {
        Stop();
        var me = ++playing;
        var end = finished = new TaskCompletionSource();
        try
        {
            if (voice == "") voice = Default();   // nothing chosen yet: a Windows voice, so it just works
            if (!Ready(voice)) throw new NoVoiceException();
            // sentence by sentence: the first plays as soon as it's made, and each next one is made while the last plays
            var parts = Chunks(Plain(text));
            async Task<System.Windows.Media.MediaPlayer> Load(string t)
            {
                var wav = Louder(voice.StartsWith("kokoro:") ? await Kokoro(t, voice[7..], rate) : await Windows(t, voice, rate), Volume);
                idle.Stop(); idle.Start();   // the countdown to letting the engine go restarts with each use
                var file = Path.Combine(Path.GetTempPath(), $"ivo-read-{Guid.NewGuid():N}.wav");
                await File.WriteAllBytesAsync(file, wav);
                files.Add(file);
                var p = new System.Windows.Media.MediaPlayer();
                p.Open(new Uri(file));
                return p;
            }
            var next = Load(parts[0]);
            for (int i = 0; i < parts.Count; i++)
            {
                var p = await next;
                if (me != playing) { p.Close(); return; }   // stopped while the audio was being made
                next = i + 1 < parts.Count ? Load(parts[i + 1]) : null!;
                var part = new TaskCompletionSource();
                p.MediaEnded += (_, _) => part.TrySetResult();
                p.MediaFailed += (_, _) => part.TrySetResult();
                player?.Close(); player = p;
                if (!Paused) p.Play();
                await Task.WhenAny(part.Task, end.Task);
                if (me != playing) { if (next != null) _ = next.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispatcher.Invoke(t.Result.Close); }); return; }
            }
        }
        finally
        {
            if (me == playing) { player?.Close(); player = null; Paused = false; finished = null; Cleanup(); }
            done();
        }
    }

    // one sentence per piece: each is made while the one before plays, and ends with the same clear pause
    static List<string> Chunks(string text)
    {
        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?।])\s+|\n+").Select(x => x.Trim()).Where(x => x != "").ToList();
        // a long opening sentence starts at its first natural pause (a comma after a few words), so sound comes sooner
        if (sentences.Count > 0 && sentences[0].Length > 70 && System.Text.RegularExpressions.Regex.Match(sentences[0], @"^(\S+\s+){3,}?\S+?[,;:—–](?=\s)") is { Success: true } m && m.Length < sentences[0].Length - 15)
        {
            sentences.Insert(1, sentences[0][m.Length..].Trim());
            sentences[0] = m.Value;
        }
        var parts = new List<string>();
        foreach (var s in sentences)
            if (parts.Count > 0 && parts[^1].Length < 20) parts[^1] += " " + s;   // only very short bits ("Hi Amit!") join the next
            else parts.Add(s);
        return parts.Count == 0 ? [" "] : parts;
    }

    // The natural voice's engine (~300 MB) is loaded when you reach for a speaker button, so it's ready by the click, and let go
    // after a minute unused.
    // made on the UI thread whichever thread touches Voice first, or it would never tick; still reading: check again later
    static readonly System.Windows.Threading.DispatcherTimer idle = new(TimeSpan.FromMinutes(1), System.Windows.Threading.DispatcherPriority.Background,
        (_, _) => { if (!Busy) { idle!.Stop(); Unload(); } }, System.Windows.Application.Current.Dispatcher) { IsEnabled = false };

    [System.Runtime.InteropServices.DllImport("psapi.dll")] static extern bool EmptyWorkingSet(nint process);

    static void Unload()
    {
        if (!kokoroGate.Wait(0)) { idle.Start(); return; }   // making audio right now: later
        try { foreach (var e in engines.Values) e.Dispose(); engines.Clear(); }
        finally { kokoroGate.Release(); }
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        EmptyWorkingSet(Process.GetCurrentProcess().Handle);   // and the pages go back to Windows
    }

    public static void Warm(string voice)
    {
        if (voice == "") voice = Default();
        if (!voice.StartsWith("kokoro:") || !KokoroInstalled) return;
        idle.Stop(); idle.Start();
        if (engines.Count == 0) _ = Task.Run(async () => { try { await Kokoro("Hi.", voice[7..], 1); } catch { } });
    }

    public static void Pause() { if (player == null || Paused) return; player.Pause(); Paused = true; }
    public static void Resume() { if (player == null || !Paused) return; player.Play(); Paused = false; }

    public static void Stop()
    {
        playing++;
        player?.Stop(); player?.Close(); player = null; Paused = false;
        finished?.TrySetResult(); finished = null;   // lets the reading's Speak finish, so its button resets
        Cleanup();
    }

    static void Cleanup() { foreach (var f in files) try { File.Delete(f); } catch { } files.Clear(); }

    // ---------- Kokoro (sherpa-onnx): one engine per language, made when first needed ----------
    static readonly Dictionary<string, SherpaOnnx.OfflineTts> engines = [];
    static readonly SemaphoreSlim kokoroGate = new(1, 1);

    internal static async Task<byte[]> Kokoro(string text, string id, double rate)
    {
        var asEnglish = id.EndsWith("@en");
        if (asEnglish) id = id[..^3];
        var sid = Array.IndexOf(KokoroIds, id);
        if (sid < 0 || (asEnglish ? KokoroLang('a') : KokoroLang(id[0])) is not { } l) throw new InvalidOperationException($"Unknown natural voice {id}.");
        await kokoroGate.WaitAsync();   // one at a time: each engine uses several cores
        try
        {
            return await Task.Run(() =>
            {
                var key = l.Lang + l.Lexicon;
                if (!engines.TryGetValue(key, out var tts))
                {
                    var c = new SherpaOnnx.OfflineTtsConfig();
                    var pack = ActivePack ?? throw new NoVoiceException();
                    var dir = Path.Combine(Dir, pack.Folder);
                    c.Model.Kokoro.Model = Path.Combine(dir, pack.Model);
                    c.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
                    c.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
                    c.Model.Kokoro.Lang = l.Lang;
                    c.Model.Kokoro.Lexicon = l.Lexicon == "" ? "" : Path.Combine(dir, l.Lexicon);
                    c.Model.Kokoro.DataDir = Path.Combine(dir, "espeak-ng-data");   // words not in the lexicon (WWDC, launches) are sounded out by espeak;
                                                                                   // sherpa-onnx 2.x dropped this and skips them, so Ivo stays on 1.12
                    c.Model.Kokoro.LengthScale = 1;
                    c.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);   // one per real core: hyper-threads only slow it (measured)
                    c.Model.Provider = "cpu";
                    c.MaxNumSentences = 2;
                    engines[key] = tts = new SherpaOnnx.OfflineTts(c);
                }
                var audio = tts.Generate(text, (float)rate, sid);
                try { return Wav([.. audio.Samples, .. new float[audio.SampleRate * 35 / 100]], audio.SampleRate); }
                finally { audio.Dispose(); }   // native memory (it has Dispose but isn't IDisposable)
            });
        }
        finally { kokoroGate.Release(); }
    }

    // scales a 16-bit PCM WAV's samples (clipping at full scale)
    static byte[] Louder(byte[] wav, double gain)
    {
        if (gain == 1) return wav;
        var at = wav.AsSpan().IndexOf("data"u8);
        if (at < 0) return wav;
        for (int i = at + 8; i + 1 < wav.Length; i += 2)
            BitConverter.TryWriteBytes(wav.AsSpan(i), (short)Math.Clamp(BitConverter.ToInt16(wav, i) * gain, short.MinValue, short.MaxValue));
        return wav;
    }

    // float samples (-1..1) to a 16-bit mono WAV
    static byte[] Wav(float[] samples, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write((short)(Math.Clamp(s, -1f, 1f) * 32767));
        return ms.ToArray();
    }

    public static async Task DownloadKokoro(KokoroPack pack, IProgress<double> progress)
    {
        Directory.CreateDirectory(Dir);
        var archive = Path.Combine(Dir, "kokoro.tar.bz2");
        await Fetch($"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{pack.Folder}.tar.bz2", archive, new Progress<double>(x => progress.Report(x * 0.92)));
        progress.Report(0.95);
        // Windows' own tar (in System32 since Windows 10 1803) unpacks .tar.bz2
        var tar = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-xjf", archive, "-C", Dir }) tar.ArgumentList.Add(a);
        using (var p = Process.Start(tar)!)
        {
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 || !Has(pack)) throw new InvalidOperationException("Couldn't unpack the voice pack. " + err.Trim());
        }
        File.Delete(archive);
        if (pack == Packs[^1]) RemoveKokoro(Packs[0]);   // the better pack makes the smaller one pointless
        else DropEngines();
        progress.Report(1);
    }

    // not while one is making audio: freeing it under Generate crashes
    static void DropEngines()
    {
        Stop(); kokoroGate.Wait();
        try { foreach (var e in engines.Values) e.Dispose(); engines.Clear(); }
        finally { kokoroGate.Release(); }
    }

    public static void RemoveKokoro(KokoroPack pack)
    {
        DropEngines();
        if (Directory.Exists(Path.Combine(Dir, pack.Folder))) Directory.Delete(Path.Combine(Dir, pack.Folder), true);
    }

    // ---------- Windows' voices ----------
    static async Task<byte[]> Windows(string text, string voice, double rate)
    {
        using var synth = new SpeechSynthesizer();
        if (SpeechSynthesizer.AllVoices.FirstOrDefault(v => "win:" + v.Id == voice) is { } v) synth.Voice = v;
        synth.Options.SpeakingRate = rate;
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        using var ms = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(ms);
        return ms.ToArray();
    }

    // markdown to something worth hearing: no code, symbols or link targets
    public static string Plain(string md)
    {
        var s = Regex.Replace(md, @"```.*?```", " (code omitted) ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"https?://\S+", "a link");
        s = Regex.Replace(s, @"^\s*(#+|>|[-*+]|\d+\.)\s+", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"[*_`~|#]", "");
        s = Regex.Replace(s, @"(?<=[\p{L}\p{N})""'])[ \t]*$", ".", RegexOptions.Multiline);
        return Regex.Replace(s, @"\n{2,}", "\n").Trim();
    }

    internal static async Task Fetch(string url, string file, IProgress<double>? progress)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync();
        await using var dst = File.Create(file);
        var buf = new byte[81920]; long got = 0; int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            got += n;
            if (total > 0) progress?.Report((double)got / total);
        }
    }
}
