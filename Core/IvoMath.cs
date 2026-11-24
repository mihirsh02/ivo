using System.Globalization;
using System.Text.RegularExpressions;

namespace Ivo;

// Ivo's maths and logic, all on this PC: sums with brackets and powers, "15% of 240", "which is bigger, 2^10 or 1000",
// "is 17 prime", "how many days until christmas", "what day is 3 March 2027". Null when it isn't maths.
public static class IvoMath
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static string N(double x) => x.ToString("0.######", En);

    public static string? Answer(string said, DateTime today)
    {
        var t = Regex.Replace(said.ToLowerInvariant().Replace("’", "'"), @"[?!]+$", "").Trim();

        var pct = Regex.Match(t, @"(\d+(?:\.\d+)?)\s*%\s*of\s*(\d+(?:\.\d+)?)");
        if (pct.Success) return $"{N(double.Parse(pct.Groups[1].Value, En) * double.Parse(pct.Groups[2].Value, En) / 100)} ✨";

        var cmp = Regex.Match(t, @"^(?:which|what)(?: one)? is (bigger|larger|greater|more|smaller|less|lower)[,:]?\s*(.+?)\s+or\s+(.+)$");
        if (cmp.Success && Eval(cmp.Groups[2].Value) is { } a && Eval(cmp.Groups[3].Value) is { } b)
        {
            var (ea, eb) = (cmp.Groups[2].Value.Trim(), cmp.Groups[3].Value.Trim());
            string Show(string e, double v) => Regex.IsMatch(e, @"^[\d.]+$") ? e : $"{e} = {N(v)}";
            if (a == b) return $"They're equal: {Show(ea, a)} and {Show(eb, b)} 🤝";
            var big = cmp.Groups[1].Value is "bigger" or "larger" or "greater" or "more";
            var (win, wv, lose, lv) = (a > b) == big ? (ea, a, eb, b) : (eb, b, ea, a);
            return $"{Show(win, wv)}, so it's {(big ? "bigger" : "smaller")} than {Show(lose, lv)} ✨";
        }

        var test = Regex.Match(t, @"^is (-?\d+) (?:a )?(prime|even|odd)(?: number)?$");
        if (test.Success)
        {
            var n = long.Parse(test.Groups[1].Value, En);
            var yes = test.Groups[2].Value switch { "prime" => Prime(n), "even" => n % 2 == 0, _ => n % 2 != 0 };
            return $"{(yes ? "Yes" : "No")}, {n} is{(yes ? "" : " not")} {(test.Groups[2].Value == "prime" ? "a prime" : test.Groups[2].Value)} {(yes ? "✨" : "🙂")}";
        }

        var until = Regex.Match(t, @"(?:how many )?days (?:until|till|til|to|left (?:until|till|to)) (.+)$");
        if (until.Success && Day(until.Groups[1].Value, today, future: true) is { } target)
        {
            var days = (target - today.Date).Days;
            return days == 0 ? "That's today! 🎉" : $"{days} day{(days == 1 ? "" : "s")} until {target:d MMMM yyyy} 📅";
        }

        var whatDay = Regex.Match(t, @"^what day (?:is|was|will be|will) (.+?)(?: be)?$");
        if (whatDay.Success && !Regex.IsMatch(whatDay.Groups[1].Value, @"^(it|today)$") && Day(whatDay.Groups[1].Value, today, future: false) is { } d)
            return $"{d:d MMMM yyyy} {(d < today ? "was" : d == today ? "is" : "is")} a {d:dddd} 📅";

        var e = Regex.Replace(t, @"^(what is|whats|what's|calculate|calc|solve|how much is|compute)\s*", "")
                     .Replace(" x ", "*").Replace("plus", "+").Replace("minus", "-").Replace("times", "*").Replace("multiplied by", "*")
                     .Replace("divided by", "/").Replace("over", "/").Replace("to the power of", "^").Replace("squared", "^2").Replace("cubed", "^3").Trim(' ', '=');
        if (Regex.IsMatch(e, @"^[\d\s+\-*/^().%]+$") && Regex.IsMatch(e, @"\d\s*[+\-*/^%]\s*\(?\s*-?\d") && Eval(e) is { } r) return $"{N(r)} ✨";
        return null;
    }

    static bool Prime(long n)
    {
        if (n < 2) return false;
        for (long i = 2; i * i <= n; i++) if (n % i == 0) return false;
        return true;
    }

    // a date in words: christmas, new year, "3 march 2027", "march 3", "25/12/2026"; future: a past date this year means next year's
    static DateTime? Day(string s, DateTime today, bool future)
    {
        s = s.Trim().TrimEnd('.', '?');
        DateTime? d = s switch
        {
            "christmas" or "xmas" or "christmas day" => new DateTime(today.Year, 12, 25),
            "new year" or "new years" or "new year's" or "new years day" => new DateTime(today.Year + 1, 1, 1),
            "halloween" => new DateTime(today.Year, 10, 31),
            "valentines" or "valentines day" or "valentine's day" => new DateTime(today.Year, 2, 14),
            "tomorrow" => today.AddDays(1),
            _ => null,
        };
        var hasYear = Regex.IsMatch(s, @"\b\d{4}\b");
        if (d == null && DateTime.TryParseExact(Regex.Replace(s, @"(\d)(st|nd|rd|th)\b", "$1"), ["d MMMM yyyy", "d MMM yyyy", "MMMM d yyyy", "MMMM d, yyyy", "MMM d yyyy", "d/M/yyyy", "yyyy-MM-dd", "d MMMM", "d MMM", "MMMM d", "MMM d"], En, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            d = hasYear ? parsed : new DateTime(today.Year, parsed.Month, parsed.Day);
        if (d is { } x && future && x.Date < today.Date && !hasYear) d = x.AddYears(1);
        return d?.Date;
    }

    // + - * / ^ % and brackets, with the usual order; null if it doesn't read
    static double? Eval(string e)
    {
        var s = Regex.Replace(e, @"\s+", ""); var i = 0;
        try
        {
            var v = Sum();
            return i == s.Length && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;
        }
        catch { return null; }

        double Sum() { var v = Product(); while (i < s.Length && s[i] is '+' or '-') v = s[i++] == '+' ? v + Product() : v - Product(); return v; }
        double Product() { var v = Power(); while (i < s.Length && s[i] is '*' or '/' or '%') { var op = s[i++]; var r = Power(); v = op == '*' ? v * r : op == '/' ? v / r : v % r; } return v; }
        double Power() { var v = Unary(); return i < s.Length && s[i] == '^' ? Math.Pow(v, (++i > 0 ? Power() : 0)) : v; }
        double Unary() => i < s.Length && s[i] == '-' ? (++i > 0 ? -Unary() : 0) : Atom();
        double Atom()
        {
            if (s[i] == '(') { i++; var v = Sum(); if (s[i++] != ')') throw new FormatException(); return v; }
            var start = i; while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            return double.Parse(s[start..i], En);
        }
    }
}
