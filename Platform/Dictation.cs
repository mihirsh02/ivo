using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Ivo;

// Ivo's own voice typing: the microphone is read here and the words worked out on this PC by sherpa-onnx (the same
// library as the natural voices), with a streaming English model, so words appear while you talk. Nothing is sent
// anywhere. The model downloads once (~310 MB archive; only its compact int8 files, ~70 MB, are kept).
public static class Dictation
{
    const string Folder = "sherpa-onnx-streaming-zipformer-en-2023-06-26";
    static string ModelDir => Path.Combine(Voice.Dir, Folder);
    static string? Find(string prefix) => Directory.Exists(ModelDir) ? Directory.GetFiles(ModelDir, prefix + "*.int8.onnx").FirstOrDefault() : null;
    public static bool Installed => Find("encoder") != null && Find("decoder") != null && Find("joiner") != null && File.Exists(Path.Combine(ModelDir, "tokens.txt"));

    public static async Task Download(IProgress<double> progress)
    {
        Directory.CreateDirectory(Voice.Dir);
        var archive = Path.Combine(Voice.Dir, "dictation.tar.bz2");
        await Voice.Fetch($"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{Folder}.tar.bz2", archive, new Progress<double>(x => progress.Report(x * 0.9)));
        // Windows' tar; only the int8 models and the tokens, not the full-size copies
        var tar = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-xjf", archive, "-C", Voice.Dir, $"{Folder}/*.int8.onnx", $"{Folder}/tokens.txt" }) tar.ArgumentList.Add(a);
        using (var p = Process.Start(tar)!)
        {
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            File.Delete(archive);
            if (p.ExitCode != 0 || !Installed) throw new InvalidOperationException("Couldn't unpack the voice typing model. " + err.Trim());
        }
        progress.Report(1);
    }

    static SherpaOnnx.OnlineRecognizer? recognizer;
    static CancellationTokenSource? listening;
    public static bool Listening => listening != null || windows != null;

    // ---------- the other choice: Windows speech recognition (Settings → Voice) ----------
    static Windows.Media.SpeechRecognition.SpeechRecognizer? windows;

    // Throws when Windows can't listen (no mic, or online speech recognition off in Settings → Privacy → Speech),
    // so the caller can fall back to Win+H.
    public static async Task StartWindows(Action<string> partial, Action<string> final, Action ended)
    {
        var r = new Windows.Media.SpeechRecognition.SpeechRecognizer();
        r.Constraints.Add(new Windows.Media.SpeechRecognition.SpeechRecognitionTopicConstraint(Windows.Media.SpeechRecognition.SpeechRecognitionScenario.Dictation, "dictation"));
        var compiled = await r.CompileConstraintsAsync();
        if (compiled.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success) { r.Dispose(); throw new InvalidOperationException(compiled.Status.ToString()); }
        r.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
        r.HypothesisGenerated += (_, a) => partial(a.Hypothesis.Text);
        r.ContinuousRecognitionSession.ResultGenerated += (_, a) => { if (a.Result.Text != "") final(a.Result.Text); };
        r.ContinuousRecognitionSession.Completed += (_, _) => { if (windows == r) { windows = null; r.Dispose(); } ended(); };
        windows = r;
        await r.ContinuousRecognitionSession.StartAsync();
    }

    // partial: the phrase so far (it keeps changing); final: a finished phrase; ended: the mic is off again.
    // Stops on Stop(), or by itself after 20 seconds without new words.
    public static void Start(Action<string> partial, Action<string> final, Action ended)
    {
        var stop = listening = new CancellationTokenSource();
        new Thread(() =>
        {
            try { Run(partial, final, stop.Token); }
            catch { }
            finally { if (listening == stop) listening = null; ended(); }
        }) { IsBackground = true, Name = "dictation" }.Start();
    }

    public static async void Stop()
    {
        listening?.Cancel();
        if (windows is { } r) try { await r.ContinuousRecognitionSession.StopAsync(); } catch { windows = null; r.Dispose(); }
    }

    static void Run(Action<string> partial, Action<string> final, CancellationToken ct)
    {
        if (recognizer == null)
        {
            var c = new SherpaOnnx.OnlineRecognizerConfig();
            c.FeatConfig.SampleRate = Rate; c.FeatConfig.FeatureDim = 80;
            c.ModelConfig.Transducer.Encoder = Find("encoder")!;
            c.ModelConfig.Transducer.Decoder = Find("decoder")!;
            c.ModelConfig.Transducer.Joiner = Find("joiner")!;
            c.ModelConfig.Tokens = Path.Combine(ModelDir, "tokens.txt");
            c.ModelConfig.NumThreads = 2; c.ModelConfig.Provider = "cpu";
            c.DecodingMethod = "greedy_search";
            c.EnableEndpoint = 1;   // a pause ends a phrase
            c.Rule1MinTrailingSilence = 2.4f; c.Rule2MinTrailingSilence = 0.8f; c.Rule3MinUtteranceLength = 20;
            recognizer = new SherpaOnnx.OnlineRecognizer(c);
        }
        var r = recognizer;
        var s = r.CreateStream();
        var last = ""; var heard = Stopwatch.StartNew();
        using var mic = new Mic();
        while (!ct.IsCancellationRequested && heard.Elapsed < TimeSpan.FromSeconds(20))
        {
            foreach (var chunk in mic.Read()) s.AcceptWaveform(Rate, chunk);
            while (r.IsReady(s)) r.Decode(s);
            var text = r.GetResult(s).Text.Trim();
            if (text != last) { last = text; heard.Restart(); if (text != "") partial(Tidy(text)); }
            if (r.IsEndpoint(s))
            {
                if (text != "") final(Tidy(text));
                r.Reset(s); last = "";
            }
        }
        if (last != "") final(Tidy(last));
    }

    // the model writes CAPITALS without punctuation: sentence case, "I" kept
    static string Tidy(string t)
    {
        t = System.Text.RegularExpressions.Regex.Replace(t.ToLowerInvariant(), @"\bi\b", "I");
        return char.ToUpperInvariant(t[0]) + t[1..];
    }

    const int Rate = 16000;

    // The default microphone through waveIn: 16 kHz mono 16-bit, in 100 ms buffers, signalled by an event.
    sealed class Mic : IDisposable
    {
        const int Buffers = 4, Bytes = Rate / 10 * 2, HeaderSize = 48;   // WAVEHDR on x64
        readonly nint handle;
        readonly AutoResetEvent ready = new(false);
        readonly nint[] headers = new nint[Buffers];

        public Mic()
        {
            var fmt = new WaveFormat { Tag = 1, Channels = 1, Rate = Rate, BytesPerSecond = Rate * 2, BlockAlign = 2, Bits = 16 };
            var err = waveInOpen(out handle, -1, ref fmt, ready.SafeWaitHandle.DangerousGetHandle(), 0, 0x50000);   // WAVE_MAPPER, CALLBACK_EVENT
            if (err != 0) throw new InvalidOperationException($"No microphone (waveIn error {err}).");
            for (int i = 0; i < Buffers; i++)
            {
                var h = headers[i] = Marshal.AllocHGlobal(HeaderSize);
                for (int b = 0; b < HeaderSize; b++) Marshal.WriteByte(h, b, 0);
                Marshal.WriteIntPtr(h, 0, Marshal.AllocHGlobal(Bytes));   // lpData
                Marshal.WriteInt32(h, 8, Bytes);                          // dwBufferLength
                waveInPrepareHeader(handle, h, HeaderSize);
                waveInAddBuffer(handle, h, HeaderSize);
            }
            waveInStart(handle);
        }

        // waits up to 200 ms, then returns every buffer that has filled, as samples (-1..1), and hands it back to fill again
        public IEnumerable<float[]> Read()
        {
            ready.WaitOne(200);
            foreach (var h in headers)
            {
                if ((Marshal.ReadInt32(h, 24) & 1) == 0) continue;   // dwFlags: WHDR_DONE
                var n = Marshal.ReadInt32(h, 12) / 2;                // dwBytesRecorded
                var raw = new short[n];
                Marshal.Copy(Marshal.ReadIntPtr(h, 0), raw, 0, n);
                Marshal.WriteInt32(h, 24, Marshal.ReadInt32(h, 24) & ~1);
                waveInAddBuffer(handle, h, HeaderSize);
                yield return Array.ConvertAll(raw, x => x / 32768f);
            }
        }

        public void Dispose()
        {
            waveInStop(handle); waveInReset(handle);
            foreach (var h in headers)
            {
                waveInUnprepareHeader(handle, h, HeaderSize);
                Marshal.FreeHGlobal(Marshal.ReadIntPtr(h, 0)); Marshal.FreeHGlobal(h);
            }
            waveInClose(handle);
            ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WaveFormat { public short Tag, Channels; public int Rate, BytesPerSecond; public short BlockAlign, Bits, Extra; }

        [DllImport("winmm.dll")] static extern int waveInOpen(out nint h, int device, ref WaveFormat fmt, nint callback, nint instance, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(nint h, nint header, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(nint h, nint header, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(nint h, nint header, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(nint h);
        [DllImport("winmm.dll")] static extern int waveInStop(nint h);
        [DllImport("winmm.dll")] static extern int waveInReset(nint h);
        [DllImport("winmm.dll")] static extern int waveInClose(nint h);
    }
}
