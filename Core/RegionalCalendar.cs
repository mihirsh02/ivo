using System.Globalization;

namespace Ivo;

// The regional calendar under a day in the Calendar: the one your holiday region keeps beside the Gregorian date.
// India: the national (Saka) calendar. Pakistan, the UAE, Indonesia, Malaysia: the Islamic (Hijri) date. China and
// Singapore: the Chinese lunar date. South Korea: the Korean lunar date. Japan: the year of the era. Anywhere else
// there's nothing to add. All by the calendar's own rules, computed here: nothing is looked up.
public static class RegionalCalendar
{
    // the holiday region's code (from Settings → Calendar) → the calendar's name, or null when it keeps none of its own
    public static string? NameFor(string region) => region switch
    {
        "indian" => "Indian national (Saka)",
        "pk" or "ae" or "indonesian" or "malaysia" => "Islamic (Hijri)",
        "china" or "singapore" => "Chinese lunar",
        "south_korea" => "Korean lunar",
        "japanese" => "Japanese era",
        _ => null,
    };

    public static string? Day(DateTime d, string region) => region switch
    {
        "indian" => Saka(d),
        "pk" or "ae" or "indonesian" or "malaysia" => Hijri(d),
        "china" or "singapore" => Lunar(new ChineseLunisolarCalendar(), d),
        "south_korea" => Lunar(new KoreanLunisolarCalendar(), d),
        "japanese" => Era(d),
        _ => null,
    };

    // The Indian national calendar: the year starts on 22 March (21 March in a leap year), Saka year = Gregorian - 78
    static readonly string[] SakaMonths = ["Chaitra", "Vaisakha", "Jyaishtha", "Ashadha", "Shravana", "Bhadra", "Ashvin", "Kartika", "Agrahayana", "Pausha", "Magha", "Phalguna"];
    static string Saka(DateTime d)
    {
        DateTime Start(int y) => new(y, 3, DateTime.IsLeapYear(y) ? 21 : 22);
        var y = d.Date >= Start(d.Year) ? d.Year : d.Year - 1;
        var day = (d.Date - Start(y)).Days;
        for (int m = 0; m < 12; m++)
        {
            var len = m == 0 ? (DateTime.IsLeapYear(y) ? 31 : 30) : m <= 5 ? 31 : 30;   // Chaitra 30 (31 in a leap year), the next five 31, the rest 30
            if (day < len) return $"{day + 1} {SakaMonths[m]} {y - 78} Saka";
            day -= len;
        }
        return "";
    }

    static readonly string[] HijriMonths = ["Muharram", "Safar", "Rabi' al-Awwal", "Rabi' al-Thani", "Jumada al-Ula", "Jumada al-Akhirah", "Rajab", "Sha'ban", "Ramadan", "Shawwal", "Dhu al-Qi'dah", "Dhu al-Hijjah"];
    static string Hijri(DateTime d)
    {
        var c = new UmAlQuraCalendar();
        return $"{c.GetDayOfMonth(d)} {HijriMonths[c.GetMonth(d) - 1]} {c.GetYear(d)} AH";
    }

    // a lunisolar date: in a year with a leap month, that month repeats the one before it
    static string Lunar(EastAsianLunisolarCalendar c, DateTime d)
    {
        var y = c.GetYear(d); var m = c.GetMonth(d); var leap = c.GetLeapMonth(y);
        var leapMonth = leap > 0 && m == leap;
        var month = leap > 0 && m >= leap ? m - 1 : m;
        return $"Lunar month {month}{(leapMonth ? " (leap)" : "")}, day {c.GetDayOfMonth(d)}";
    }

    static string Era(DateTime d)
    {
        var c = new JapaneseCalendar();
        var era = c.GetEra(d) switch { 5 => "Reiwa", 4 => "Heisei", 3 => "Showa", _ => "" };
        return $"{era} {c.GetYear(d)}, {d:MMMM} {d.Day}".Trim();
    }
}
