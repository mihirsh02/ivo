using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ivo;

// Song lyrics from LRCLIB (lrclib.net: free, open, no account). Only the song's name, artist, album and length are sent.
// Synced lyrics come with a time per line; plain ones (no times) still show, without following the song.
public static partial class Lyrics
{
    public record Line(double At, string Text);   // At: seconds into the song, -1 when the lyrics have no times

    internal static (int Start, int Current, List<Line> Lines) Around(IReadOnlyList<Line> lines, double seconds, int eachSide = 4)
    {
        if (lines.Count == 0) return (0, -1, []);
        var current = lines[0].At < 0 ? -1 : lines.ToList().FindLastIndex(l => l.At <= seconds + 0.25);
        var center = Math.Max(0, current);
        var start = Math.Max(0, center - Math.Max(0, eachSide));
        var end = Math.Min(lines.Count, center + Math.Max(0, eachSide) + 1);
        return (start, current < 0 ? -1 : current - start, lines.Skip(start).Take(end - start).ToList());
    }

    static readonly HttpClient http = new(Net.Handler()) { Timeout = TimeSpan.FromSeconds(10) };
    static readonly Dictionary<string, List<Line>?> cache = [];

    static Lyrics() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Ivo");

    public static async Task<List<Line>?> For(JsonNode track)
    {
        var name = (string?)track["name"] ?? ""; var artist = (string?)track["artists"]?[0]?["name"] ?? "";
        var key = $"{artist}\n{name}";
        if (cache.TryGetValue(key, out var known)) return known;
        List<Line>? lines = null;
        try
        {
            var q = $"track_name={Uri.EscapeDataString(name)}&artist_name={Uri.EscapeDataString(artist)}" +
                    $"&album_name={Uri.EscapeDataString((string?)track["album"]?["name"] ?? "")}&duration={(int)(((double?)track["duration_ms"] ?? 0) / 1000)}";
            var res = await http.GetAsync("https://lrclib.net/api/get?" + q);
            if (!res.IsSuccessStatusCode)   // no exact match (length or album differ): the closest search result
            {
                var found = JsonNode.Parse(await http.GetStringAsync($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(name)}&artist_name={Uri.EscapeDataString(artist)}")) as JsonArray;
                lines = Parse(found?.FirstOrDefault(x => x?["syncedLyrics"] != null) ?? found?.FirstOrDefault());
            }
            else lines = Parse(JsonNode.Parse(await res.Content.ReadAsStringAsync()));
        }
        catch { return null; }   // offline: try again next time
        cache[key] = lines;
        return lines;
    }

    static List<Line>? Parse(JsonNode? j)
    {
        if (j == null || j["instrumental"]?.GetValue<bool>() == true) return null;
        if ((string?)j["syncedLyrics"] is { Length: > 0 } synced)
        {
            var lines = new List<Line>();
            foreach (var raw in synced.Split('\n'))
                if (Stamp().Match(raw) is { Success: true } m)
                    lines.Add(new(int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[3].Value.Trim()));
            return lines.Count > 0 ? lines : null;
        }
        return (string?)j["plainLyrics"] is { Length: > 0 } plain ? plain.Split('\n').Select(l => new Line(-1, l.Trim())).ToList() : null;
    }

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$")]
    private static partial Regex Stamp();
}

// Lyrics on a vertical wheel, like the Inbox's drum turned upright: the line being sung in the middle, full size and bright,
// the ones above and below smaller and fainter. It eases from line to line with the same spring as the cover wheel.
// Click a line to jump the song there; scroll to look around (it comes back to the song after a few seconds).
public class LyricsWheel : Grid
{
    readonly Canvas canvas = new() { ClipToBounds = true };
    readonly TextBlock note = new() { TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 20, 0) };
    readonly double size;
    List<Lyrics.Line> lines = [];
    readonly List<TextBlock> rows = [];
    double pos, target, speed;
    bool springing;
    DateTime frame, looked;
    public Action<double>? SeekTo;   // seconds
    public bool OneLine;   // only the line being sung, centred, and no scrolling (Home's Now playing)

    public LyricsWheel(double fontSize = 20)
    {
        size = fontSize;
        Background = Brushes.Transparent; ClipToBounds = true;
        note.FontSize = size * 0.75; note.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        Children.Add(canvas); Children.Add(note);
        SizeChanged += (_, _) => Layout();
        PreviewMouseWheel += (_, e) => { if (OneLine) return; e.Handled = true; looked = DateTime.Now; To(target + (e.Delta < 0 ? 1 : -1)); };
    }

    public void Show(List<Lyrics.Line>? found, string? empty = null)
    {
        canvas.Children.Clear(); rows.Clear(); heights.Clear();
        lines = found ?? []; wordLine = -1;
        note.Text = found == null ? empty ?? "No lyrics for this song." : "";
        foreach (var l in lines)
        {
            var row = new TextBlock { Text = Label(l), FontSize = size, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Left,
                                      TextWrapping = TextWrapping.Wrap, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), Cursor = Cursors.Hand };
            row.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var at = l.At;
            row.MouseLeftButtonUp += (_, _) => { if (at >= 0) SeekTo?.Invoke(at); };
            canvas.Children.Add(row); rows.Add(row);
        }
        pos = target = 0;
        Layout();
    }

    // the song is at this many seconds: bring the line being sung to the middle
    public void At(double seconds)
    {
        songAt = seconds; songStamp = DateTime.Now;
        if (!words && lines.Count > 0 && lines[0].At >= 0)
        {
            words = true;   // every frame while shown, so the fill sweeps smoothly
            void On() { CompositionTarget.Rendering -= Frame; if (IsVisible) CompositionTarget.Rendering += Frame; }
            IsVisibleChanged += (_, _) => On();
            Unloaded += (_, _) => CompositionTarget.Rendering -= Frame;
            On();
        }
        if (lines.Count == 0 || lines[0].At < 0 || (DateTime.Now - looked).TotalSeconds < 4) return;
        var i = lines.FindLastIndex(l => l.At <= seconds + 0.25);
        To(Math.Max(i, 0));
    }

    // word by word, as Apple Music does: LRCLIB times lines, not words, so each word's moment is spread across the line
    // by its length (it takes the line's whole time, up to ~120 ms a letter, never past the next line). Sung words are bright, the rest faint.
    bool words;
    void Frame(object? s, EventArgs e) => Words();
    double songAt; DateTime songStamp;
    int wordLine = -1;

    void Words()
    {
        if (lines.Count == 0 || lines[0].At < 0) return;
        var s = songAt + Math.Min(2, (DateTime.Now - songStamp).TotalSeconds);
        var i = lines.FindLastIndex(l => l.At <= s + 0.25);
        if (i != wordLine)
        {
            if (wordLine >= 0 && wordLine < rows.Count) { var old = rows[wordLine]; old.Text = Label(lines[wordLine]); }
            wordLine = i; Layout();   // the line lights up as it starts
            if (i < 0 || lines[i].Text.Length == 0) return;
            rows[i].Inlines.Clear();
            var f = (TryFindResource("Fg") as SolidColorBrush)?.Color ?? Colors.White; var dim = Color.FromArgb((byte)(f.A * 0.32), f.R, f.G, f.B);
            // each word gets its own left-to-right fill: bright up to the stop, faint after it
            foreach (var w in lines[i].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                rows[i].Inlines.Add(new System.Windows.Documents.Run(w + " ") { Foreground = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                    GradientStops = { new(dim, 0), new(dim, 0), new(dim, 0), new(dim, 1) } } });
        }
        if (i < 0 || lines[i].Text.Length == 0 || TryFindResource("Fg") is not SolidColorBrush fg) return;
        var start = lines[i].At; var next = i + 1 < lines.Count ? lines[i + 1].At : start + 6;
        var total = lines[i].Text.Replace(" ", "").Length;
        var dur = Math.Clamp(Math.Min(next - start - 0.3, total * 0.12), 0.5, Math.Max(0.5, next - start));
        var lit = Math.Clamp((s - start) / dur, 0, 1) * total;
        double before = 0;
        foreach (System.Windows.Documents.Run r in rows[i].Inlines)
        {
            var len = r.Text.Length - 1;
            var a = Math.Clamp((lit - before) / Math.Max(1, len), 0, 1);
            before += len;
            if (r.Foreground is not LinearGradientBrush g) continue;
            var on = fg.Color; var off = Color.FromArgb((byte)(on.A * 0.32), on.R, on.G, on.B);
            // the fill covers the letters (the run's last part is its space); a sung word is bright to its end, an unsung one faint
            var x = a >= 1 ? 1.0 : a * len / (len + 1.0);
            var soft = a is > 0 and < 1 ? 0.06 : 0;
            g.GradientStops[0].Color = on; g.GradientStops[1].Color = on; g.GradientStops[1].Offset = Math.Clamp(x - soft, 0, 1);
            g.GradientStops[2].Color = off; g.GradientStops[2].Offset = Math.Clamp(a >= 1 ? 1 : x + soft, 0, 1); g.GradientStops[3].Color = a >= 1 ? on : off;
        }
    }

    static string Label(Lyrics.Line l) => l.Text.Length == 0 ? "♪" : l.Text;

    void To(double t)
    {
        target = Math.Clamp(Math.Round(t), 0, Math.Max(0, lines.Count - 1));
        if (springing) return;
        springing = true; frame = DateTime.Now;
        CompositionTarget.Rendering += Spring;
    }

    void Spring(object? s, EventArgs e)
    {
        var now = DateTime.Now; var dt = Math.Min(0.05, (now - frame).TotalSeconds); frame = now;
        const double k = 120; var c = 1.8 * Math.Sqrt(k);
        for (int i = 0; i < 4; i++) { var h = dt / 4; speed += (k * (target - pos) - c * speed) * h; pos += speed * h; }
        if (Math.Abs(target - pos) < 0.002 && Math.Abs(speed) < 0.02) { pos = target; speed = 0; springing = false; CompositionTarget.Rendering -= Spring; }
        Layout();
    }

    // Apple Music's way: every line the same size, left-aligned, stacked by their real heights; the sung line sits a third of the
    // way down, bright (its words light up as they're sung), the ones after it fading the further they are, the ones before faint
    readonly List<double> heights = [];
    double measuredFor;

    void Layout()
    {
        var w = ActualWidth; var h = ActualHeight; if (w == 0 || h == 0 || rows.Count == 0) return;
        var rowW = w - 40; var gap = size * 0.9;
        if (rowW != measuredFor || heights.Count != rows.Count)
        {
            measuredFor = rowW; heights.Clear();
            foreach (var r in rows) { r.Width = rowW; r.Measure(new Size(rowW, double.PositiveInfinity)); heights.Add(r.DesiredSize.Height); }
        }
        var tops = new double[rows.Count + 1];
        for (int i = 0; i < rows.Count; i++) tops[i + 1] = tops[i] + heights[i] + gap;
        var at = Math.Clamp(pos, 0, rows.Count - 1); var lo = (int)Math.Floor(at);
        var cur = tops[lo] + (at - lo) * (tops[Math.Min(lo + 1, rows.Count)] - tops[lo]);
        var anchor = OneLine ? (h - heights[Math.Clamp((int)Math.Round(pos), 0, rows.Count - 1)]) / 2 : h * 0.3;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var y = anchor + tops[i] - cur;
            if (y > h || y + heights[i] < 0) { row.Visibility = Visibility.Hidden; continue; }
            row.Visibility = Visibility.Visible;
            var d = i - pos;
            var started = lines.Count > 0 && (lines[0].At < 0 || wordLine >= 0);
            row.Opacity = OneLine ? (Math.Abs(d) < 0.5 ? (started ? 1 : 0.42) : 0) : Math.Abs(d) < 0.5 ? (started ? 1 : 0.42) : d < 0 ? 0.28 : Math.Max(0.1, 0.42 - 0.07 * d);
            Canvas.SetLeft(row, 20); Canvas.SetTop(row, y);
        }
    }
}
