using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Ivo;

// Plain text out of the documents people keep notes in: Word (.docx), PDF, and anything that's already text.
// Used by pinned context, the knowledge folder (search and the semantic index) and attachments.
public static class Docs
{
    public static readonly string[] Readable = [".md", ".markdown", ".txt", ".org", ".csv", ".json", ".yaml", ".yml", ".rst", ".tex", ".html", ".htm", ".log", ".docx", ".pdf"];
    public static bool CanRead(string path) => Readable.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => Docx(File.ReadAllBytes(path)),
        ".pdf" => Pdf(File.ReadAllBytes(path)),
        _ => File.ReadAllText(path),
    };

    // A .docx is a zip; the text is in word/document.xml as <w:t> runs inside <w:p> paragraphs.
    public static string Docx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a Word document.");
        using var r = new StreamReader(entry.Open());
        var xml = r.ReadToEnd();
        var sb = new StringBuilder();
        foreach (Match p in Regex.Matches(xml, @"<w:p[ >].*?</w:p>", RegexOptions.Singleline))
        {
            foreach (Match t in Regex.Matches(p.Value, @"<w:t(?: [^>]*)?>(.*?)</w:t>|<w:tab/>|<w:br/>", RegexOptions.Singleline))
                sb.Append(t.Value.StartsWith("<w:tab") ? "\t" : t.Value.StartsWith("<w:br") ? "\n" : System.Net.WebUtility.HtmlDecode(t.Groups[1].Value));
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    // ponytail: reads text drawn with Tj/TJ/'/" from plain or Flate-compressed content streams. PDFs whose fonts need a
    // ToUnicode map (many CJK and some designed PDFs) come out empty or garbled; a real PDF library if that matters.
    public static string Pdf(byte[] bytes)
    {
        var raw = Encoding.Latin1.GetString(bytes);
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(raw, @"<<(?<dict>(?:(?!>>).)*?)>>\s*stream\r?\n", RegexOptions.Singleline))
        {
            var start = m.Index + m.Length;
            var end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var data = bytes.AsSpan(start, end - start).ToArray();
            var dict = m.Groups["dict"].Value;
            if (Regex.IsMatch(dict, @"/Subtype\s*/Image|/Length[123]\b|/Type\s*/(XRef|ObjStm|Metadata)")) continue;   // pictures, embedded fonts, indexes
            string content;
            if (dict.Contains("/FlateDecode"))
            {
                try
                {
                    using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                    using var o = new MemoryStream(); z.CopyTo(o);
                    content = Encoding.Latin1.GetString(o.ToArray());
                }
                catch { continue; }
            }
            else if (Regex.IsMatch(dict, @"/Filter")) continue;   // other encodings (images, DCT…): not text
            else content = Encoding.Latin1.GetString(data);
            if (!content.Contains("BT")) continue;
            foreach (Match bt in Regex.Matches(content, @"BT(.*?)ET", RegexOptions.Singleline)) sb.Append(TextOps(bt.Groups[1].Value)).Append('\n');
        }
        return Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n").Trim();
    }

    // the strings shown by one BT … ET block, with line breaks where the text moves to a new line
    static string TextOps(string ops)
    {
        var sb = new StringBuilder();
        string? last = null;
        int i = 0;
        while (i < ops.Length)
        {
            var ch = ops[i];
            if (ch == '(') { last = ReadString(ops, ref i); continue; }
            if (ch == '[')   // [(a) -250 (b)] TJ: strings, with a space where the gap is wide
            {
                var parts = new StringBuilder(); i++;
                while (i < ops.Length && ops[i] != ']')
                {
                    if (ops[i] == '(') { parts.Append(ReadString(ops, ref i)); continue; }
                    var m = Regex.Match(ops[i..], @"^-?\d+(?:\.\d+)?");
                    if (m.Success) { if (double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) < -200) parts.Append(' '); i += m.Length; continue; }
                    i++;
                }
                i++; last = parts.ToString(); continue;
            }
            if (char.IsLetter(ch) || ch is '\'' or '"' or '*')
            {
                var start = i;
                while (i < ops.Length && (char.IsLetter(ops[i]) || ops[i] is '*' or '\'' or '"')) i++;
                var op = ops[start..i];
                if (op is "Tj" or "TJ") { if (last != null) sb.Append(last); last = null; }
                else if (op is "'" or "\"") { sb.Append('\n'); if (last != null) sb.Append(last); last = null; }
                else if (op is "T*" or "TD" or "Tm") { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); }
                else if (op == "Td") { if (sb.Length > 0 && sb[^1] is not ('\n' or ' ')) sb.Append(' '); }
                continue;
            }
            i++;
        }
        return sb.ToString();
    }

    // a (literal string) starting at i: balanced parentheses nest, backslash escapes; i ends after the closing ')'
    static string ReadString(string s, ref int i)
    {
        var sb = new StringBuilder(); var depth = 0; i++;
        while (i < s.Length)
        {
            var c = s[i++];
            if (c == '\\' && i < s.Length)
            {
                var e = s[i++];
                if (e is >= '0' and <= '7')
                {
                    var oct = e.ToString();
                    while (oct.Length < 3 && i < s.Length && s[i] is >= '0' and <= '7') oct += s[i++];
                    sb.Append((char)Convert.ToInt32(oct, 8));
                }
                else sb.Append(e switch { 'n' => "\n", 't' => "\t", 'r' or 'b' or 'f' or '\n' or '\r' => "", _ => e.ToString() });
            }
            else if (c == '(') { depth++; sb.Append(c); }
            else if (c == ')') { if (depth == 0) break; depth--; sb.Append(c); }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
