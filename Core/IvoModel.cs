using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Ivo;

// "Smarter chat" (Settings → Assistant, optional): a small language model on this PC (Qwen2.5 0.5B Instruct, 4-bit, ~400 MB)
// that writes Ivo's replies in her voice. The same configured file can also be selected for code conversations; that
// separate path doesn't change her companion model semantics. Her own brain still does every Ivo action and keeps learning.
// Light on purpose: loaded on first use (or when her chat opens), let go after 2 minutes idle, a small context.
public static class IvoModel
{
    public const string Url = "https://huggingface.co/bartowski/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf";
    public const double SizeMb = 398;
    public static string Dir => Path.Combine(Store.Dir, "models");
    public static string Default => Path.Combine(Dir, "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf");
    // The selected GGUF can also be chosen for coding conversations. It is a virtual provider: it never changes the
    // companion's Smarter chat toggle, personality, or model selection.
    public const string CodingProviderName = "Ivo local";
    public static string CodingModelId => Path.GetFileNameWithoutExtension(File);
    public static ProviderCfg CodingProvider => new() { Name = CodingProviderName, Kind = "llama", BaseUrl = "local://ivo-model" };
    public static string CodingModelName => Catalog.FirstOrDefault(o => o.File == Path.GetFileName(File))?.Name ?? Path.GetFileNameWithoutExtension(File);

    public static bool On { get; set; }              // Settings → Assistant → Smarter chat
    public static string Custom { get; set; } = "";  // the model picked in Settings (one you added; "" for the standard one)
    public static string File => Custom != "" && System.IO.File.Exists(Custom) ? Custom : Default;
    public static bool Ready => System.IO.File.Exists(File);
    public static bool Using => On && Ready;

    static LLamaWeights? weights;
    static ModelParams? settings;
    static string? loaded;
    static LLamaContext? context;                 // kept with the model, with what it has read
    static readonly List<LLamaToken> seen = [];   // the tokens the context holds, in order
    static readonly SemaphoreSlim gate = new(1, 1);
    static Timer? idle;

    // half the cores (2 to 4): replies come several times faster than on two, and the PC stays responsive
    static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    // loaded ahead (her chat opened), so the first reply doesn't wait for it
    public static async Task Warm() { if (Using && weights == null) await Reply("", maxTokens: 0); }

    // her reply to a chat prompt (ChatML, as Qwen2.5 was trained), or null if it couldn't run
    // grammar (GBNF): what she may write at all, e.g. only "yes" or "no"
    public static async Task<string?> Reply(string prompt, CancellationToken ct = default, float temperature = 0.6f, int maxTokens = 40, string? grammar = null)
    {
        if (!Ready) return null;
        await gate.WaitAsync(ct);
        try
        {
            idle?.Dispose();
            if (weights == null || loaded != File)
            {
                Unload();
                settings = new ModelParams(File) { ContextSize = 2048, GpuLayerCount = 0, Threads = Threads, BatchSize = 512 };
                weights = await LLamaWeights.LoadFromFileAsync(settings, ct);
                loaded = File;
            }
            context ??= weights.CreateContext(settings!);
            if (maxTokens == 0) return "";
            // her prompts start the same way each time (who she is, her examples, the chat so far): what the context
            // already holds of it is kept, and only what's new is read, so a reply starts in a fraction of the time
            var tokens = context.Tokenize(prompt, addBos: true, special: true).ToList();
            var room = (int)context.ContextSize - maxTokens - 1;
            if (tokens.Count > room) tokens = tokens[^room..];
            var keep = 0;
            while (keep < seen.Count && keep < tokens.Count - 1 && seen[keep].Equals(tokens[keep])) keep++;
            context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, keep, -1);
            seen.RemoveRange(keep, seen.Count - keep);
            var batch = new LLamaBatch();
            for (var i = keep; i < tokens.Count; i += 512)
            {
                batch.Clear();
                for (var j = i; j < Math.Min(i + 512, tokens.Count); j++) batch.Add(tokens[j], j, LLamaSeqId.Zero, j == tokens.Count - 1);
                if (await context.DecodeAsync(batch, ct) != DecodeResult.Ok) { seen.Clear(); context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, 0, -1); throw new InvalidOperationException("couldn't read the prompt"); }
            }
            seen.AddRange(tokens[keep..]);
            using var sampler = new DefaultSamplingPipeline { Temperature = temperature, TopP = 0.9f, TopK = 40, RepeatPenalty = 1.1f, Grammar = grammar == null ? null : new LLama.Sampling.Grammar(grammar, "root") };
            var decoder = new StreamingTokenDecoder(context) { DecodeSpecialTokens = true };
            var sb = new StringBuilder();
            var at = batch.TokenCount - 1;
            for (var n = 0; n < maxTokens; n++)
            {
                var token = sampler.Sample(context.NativeHandle, at);
                if (token.Equals(context.Vocab.EOS) || token.Equals(context.Vocab.EOT)) break;
                decoder.Add(token); sb.Append(decoder.Read());
                if (sb.ToString() is var s && (s.Contains("<|im_end|>") || s.Contains("<|im_start|>") || s.Contains("\nUser:") || s.Contains("\nuser:"))) break;
                batch.Clear(); batch.Add(token, tokens.Count + n, LLamaSeqId.Zero, true);
                if (await context.DecodeAsync(batch, ct) != DecodeResult.Ok) break;
                seen.Add(token); at = 0;
            }
            LastError = null;
            return sb.ToString();
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return null; }
        finally
        {
            idle = new Timer(_ => { if (gate.Wait(0)) { try { Unload(); } finally { gate.Release(); } } }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            gate.Release();
        }
    }

    public static bool Loaded => weights != null;
    public static string? LastError { get; private set; }   // why the last reply couldn't run (for the failures log)

    // A standalone completion for ordinary/code chats. This deliberately does not toggle Smarter chat or invoke its
    // persona prompt; it only reuses the already-configured local GGUF and inference engine.
    public static async Task<string?> CodingReply(Chat chat, CancellationToken ct = default)
    {
        var prompt = new StringBuilder()
            .Append("<|im_start|>system\n")
            .Append("You are a helpful coding assistant running locally. Answer programming questions with accurate, usable code. ")
            .Append("You cannot inspect or edit the user's files or run commands; provide code or a patch in your answer when asked. ")
            .Append(chat.System.Length > 700 ? chat.System[..700] : chat.System).Append('\n');
        if (chat.Context != "") prompt.Append("Project context:\n").Append(chat.Context.Length > 1200 ? chat.Context[..1200] : chat.Context).Append('\n');
        foreach (var m in chat.Messages.Where(m => (m.Role is "user" or "assistant") && m.Text != "").TakeLast(6))
        {
            var role = m.Role == "assistant" ? "assistant" : "user";
            var text = m.Text.Length > 600 ? m.Text[^600..] : m.Text;
            prompt.Append("<|im_start|>").Append(role).Append('\n').Append(text).Append("<|im_end|>\n");
        }
        prompt.Append("<|im_start|>assistant\n");
        return await Reply(prompt.ToString(), ct, temperature: 0.2f, maxTokens: 768);
    }

    public static void Unload()
    {
        context?.Dispose(); context = null; seen.Clear();
        weights?.Dispose(); weights = null; loaded = null;
        GC.Collect();
    }

    // the models you can download in Ivo (Settings → Smarter chat → Browse models): popular open models that talk in the
    // chat format Ivo writes (ChatML), 4-bit, from Hugging Face. Each says the memory it needs while she replies, the CPU
    // cores it wants to be quick, and how fast it is on a laptop; bigger understands more and takes longer.
    public record Offer(string Name, string File, string Url, double SizeMb, double MemoryGb, int Cores, string Speed, string About)
    {
        public bool Heavy => MemoryGb >= 4 || Cores >= 6;   // a big model: a lot of memory and CPU while she replies
    }
    public static readonly Offer[] Catalog =
    [
        new("SmolLM2 135M", "SmolLM2-135M-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/SmolLM2-135M-Instruct-GGUF/resolve/main/SmolLM2-135M-Instruct-Q4_K_M.gguf", 101, 0.3, 2, "instant · under 1 s",
            "Tiny and instant, but simple: short replies, often off the mark."),
        new("SmolLM2 360M", "SmolLM2-360M-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/SmolLM2-360M-Instruct-GGUF/resolve/main/SmolLM2-360M-Instruct-Q4_K_M.gguf", 258, 0.5, 2, "very fast · about 1 s",
            "Very light; fine for quick small talk on a slow PC."),
        new("Qwen2.5 0.5B", "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf", Url, 398, 0.7, 2, "fastest of the useful ones · 1–3 s",
            "The standard: small and quick. Good for short chat; misses meaning in vague or longer messages."),
        new("Qwen2.5 1.5B", "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf", 986, 1.4, 4, "fast · 3–6 s",
            "The best balance on a laptop: understands what you mean far better, still quick."),
        new("Qwen2.5 Coder 1.5B", "Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf", 986, 1.4, 4, "fast · 3–6 s",
            "Qwen tuned for code: better at programming questions, a little drier in chat."),
        new("SmolLM2 1.7B", "SmolLM2-1.7B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/SmolLM2-1.7B-Instruct-GGUF/resolve/main/SmolLM2-1.7B-Instruct-Q4_K_M.gguf", 1007, 1.5, 4, "fast · 3–6 s",
            "Hugging Face's small model: friendly, natural small talk; a little less knowledgeable than Qwen."),
        new("Qwen2.5 3B", "Qwen2.5-3B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf", 1841, 2.6, 4, "steady · 6–12 s",
            "Noticeably smarter and more natural."),
        new("Qwen2.5 Coder 3B", "Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-Coder-3B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf", 1841, 2.6, 4, "steady · 6–12 s",
            "A stronger coder; good when you mostly ask about programming."),
        new("Hermes 3 (Llama 3.2) 3B", "Hermes-3-Llama-3.2-3B-Q4_K_M.gguf", "https://huggingface.co/bartowski/Hermes-3-Llama-3.2-3B-GGUF/resolve/main/Hermes-3-Llama-3.2-3B-Q4_K_M.gguf", 1926, 2.7, 4, "steady · 6–12 s",
            "Nous Research's chat model: lively role-play and personality, follows her persona closely."),
        new("OpenHermes 2.5 (Mistral) 7B", "openhermes-2.5-mistral-7b.Q4_K_M.gguf", "https://huggingface.co/TheBloke/OpenHermes-2.5-Mistral-7B-GGUF/resolve/main/openhermes-2.5-mistral-7b.Q4_K_M.gguf", 4166, 5.2, 6, "slow · 15–30 s",
            "A well-loved 7B chat model: warm, articulate conversation."),
        new("Qwen2.5 7B", "Qwen2.5-7B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf", 4466, 5.5, 6, "slow · 15–30 s",
            "Close to a cloud assistant for chat: clever and well-spoken."),
        new("Qwen2.5 Coder 7B", "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf", 4466, 5.5, 6, "slow · 15–30 s",
            "A strong local coding model."),
        new("Hermes 3 (Llama 3.1) 8B", "Hermes-3-Llama-3.1-8B-Q4_K_M.gguf", "https://huggingface.co/bartowski/Hermes-3-Llama-3.1-8B-GGUF/resolve/main/Hermes-3-Llama-3.1-8B-Q4_K_M.gguf", 4693, 5.8, 6, "slow · 15–35 s",
            "Hermes at 8B: rich personality and long, thoughtful replies."),
        new("Qwen2.5 14B", "Qwen2.5-14B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-14B-Instruct-GGUF/resolve/main/Qwen2.5-14B-Instruct-Q4_K_M.gguf", 8572, 10.2, 8, "very slow on a laptop · 40–90 s",
            "The cleverest you can run on a strong desktop."),
        new("Qwen2.5 32B", "Qwen2.5-32B-Instruct-Q4_K_M.gguf", "https://huggingface.co/bartowski/Qwen2.5-32B-Instruct-GGUF/resolve/main/Qwen2.5-32B-Instruct-Q4_K_M.gguf", 18932, 21.5, 12, "workstation only · minutes",
            "Near the best open chat models; needs a workstation's memory."),
    ];
    public static Offer Standard => Catalog.First(o => o.Url == Url);

    // this PC's memory (GB): all of it, and what's free right now
    public static double TotalGb() { var m = Status(); return m.ullTotalPhys / 1073741824.0; }
    public static double FreeGb() { var m = Status(); return m.ullAvailPhys / 1073741824.0; }
    static MEMORYSTATUSEX Status() { var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() }; GlobalMemoryStatusEx(ref m); return m; }
    // one this PC can run at all: it fits in its memory with 2 GB still left for everything else
    public static bool Fits(Offer o) => o.MemoryGb <= TotalGb() - 2 || System.IO.File.Exists(Path.Combine(Dir, o.File));
    // one that would squeeze the memory right now: free memory, less 2 GB, is less than it needs
    public static bool Tight(Offer o) => o.MemoryGb > FreeGb() - 2;
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    // a model, downloaded once into the models folder (a .part file until it's whole)
    public static Task Download(IProgress<double> progress, CancellationToken ct) => Download(Standard, progress, ct);
    public static async Task Download(Offer o, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, o.File);
        var part = file + ".part";
        using var http = new HttpClient(Net.Handler()) { Timeout = TimeSpan.FromHours(2) };
        using var res = await http.GetAsync(o.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? (long)(o.SizeMb * 1024 * 1024);
        await using (var from = await res.Content.ReadAsStreamAsync(ct))
        await using (var to = System.IO.File.Create(part))
        {
            var buf = new byte[1 << 16]; long got = 0; int n;
            while ((n = await from.ReadAsync(buf, ct)) > 0) { await to.WriteAsync(buf.AsMemory(0, n), ct); got += n; progress.Report((double)got / total); }
        }
        System.IO.File.Move(part, file, overwrite: true);
    }


    // what the model wrote, made into one of her chat bubbles: up to the end of its turn, no role tags, no "as an AI",
    // two or three short sentences at most
    public static string Clean(string raw)
    {
        var t = raw;
        foreach (var stop in new[] { "<|im_end|>", "<|im_start|>", "<|endoftext|>", "\nUser:", "\nuser:", "\nIvo:", "\nAssistant:" })
            if (t.IndexOf(stop, StringComparison.Ordinal) is var at and >= 0) t = t[..at];
        t = Regex.Replace(t, @"^\s*(assistant|ivo)\s*[:\n]", "", RegexOptions.IgnoreCase).Trim();
        t = Regex.Replace(t, @"(?i)[^.!?]*\b(as an ai|language model|i'?m an ai|i am an ai|artificial intelligence|chat ?bot|i don'?t have (feelings|emotions|personal preferences))\b[^.!?]*[.!?]?", "").Trim();
        t = Regex.Replace(t, @"(?i)[^.!?]*\b(for this reply|you looked up|they used:|in your own words)[^.!?]*[.!?]?|""[^""]+"" means [^.!?]*([.!?]|$)", "").Trim();   // its instructions, said back
        var sentences = Regex.Split(t, @"(?<=[.!?…])\s+").Where(s => s.Trim() != "").Take(3).ToList();
        t = string.Join(" ", sentences).Trim();
        if (t.Length > 320) t = t[..320].TrimEnd() + "…";
        if (t.Length > 0 && char.IsLower(t[0])) t = char.ToUpper(t[0]) + t[1..];   // a chat bubble starts with a capital
        return t;
    }
}
