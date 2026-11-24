using System.Text.RegularExpressions;

namespace Ivo;

// Secrets in what's sent to a cloud model are swapped for placeholders like [secret-1] and swapped back in the reply, so
// the provider never sees them but answers and tool calls still use the real values. One scope per request (AsyncLocal).
// ponytail: pattern-based; it catches common key formats, "password: …" style values and card numbers, not every secret.
public static class Redact
{
    sealed class Map { public readonly Dictionary<string, string> ToHolder = []; public readonly Dictionary<string, string> ToSecret = []; }
    static readonly AsyncLocal<Map?> current = new();
    public static bool Active => current.Value != null;

    public static IDisposable Begin() { current.Value = new Map(); return new End(); }
    sealed class End : IDisposable { public void Dispose() => current.Value = null; }

    static readonly Regex Whole = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----" +
        @"|\bsk-(?:ant-|or-|proj-)?[A-Za-z0-9_\-]{20,}" +
        @"|\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bglpat-[A-Za-z0-9_\-]{20,}" +
        @"|\bxox[abprs]-[A-Za-z0-9\-]{10,}|\bAKIA[0-9A-Z]{16}\b|\bAIza[0-9A-Za-z_\-]{35}" +
        @"|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled);
    // only the value after the label
    static readonly Regex Labelled = new(@"(?i)\b(?:(?:password|passwd|pwd|passphrase|secret|api[_\- ]?key|access[_\- ]?token|auth[_\- ]?token)\s*[:=]\s*|bearer\s+)([""']?)([^\s""']{6,})\1", RegexOptions.Compiled);
    static readonly Regex Card = new(@"\b(?:\d[ \-]?){12,18}\d\b", RegexOptions.Compiled);

    public static string Mask(string text)
    {
        if (current.Value is not { } map || string.IsNullOrEmpty(text)) return text;
        string Hold(string secret)
        {
            if (!map.ToHolder.TryGetValue(secret, out var h)) { h = $"[secret-{map.ToHolder.Count + 1}]"; map.ToHolder[secret] = h; map.ToSecret[h] = secret; }
            return h;
        }
        text = Whole.Replace(text, m => Hold(m.Value));
        text = Labelled.Replace(text, m => m.Groups[2].Value.StartsWith("[secret-") ? m.Value : m.Value[..(m.Groups[2].Index - m.Index)] + Hold(m.Groups[2].Value) + m.Groups[1].Value);
        text = Card.Replace(text, m => Luhn(m.Value) ? Hold(m.Value) : m.Value);
        return text;
    }

    public static string Unmask(string text)
    {
        if (current.Value is not { } map || map.ToSecret.Count == 0 || string.IsNullOrEmpty(text)) return text;
        foreach (var (h, secret) in map.ToSecret) text = text.Replace(h, secret);
        return text;
    }

    static bool Luhn(string s)
    {
        var digits = s.Where(char.IsDigit).Select(c => c - '0').Reverse().ToList();
        if (digits.Count is < 13 or > 19) return false;
        var sum = digits.Select((d, i) => i % 2 == 1 ? (d * 2 > 9 ? d * 2 - 9 : d * 2) : d).Sum();
        return sum % 10 == 0;
    }

    // this PC or a private network: nothing leaves, so nothing to hide
    public static bool IsLocal(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) && (u.IsLoopback || Regex.IsMatch(u.Host, @"^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)"));
}
