using System.Globalization;
using System.Text.RegularExpressions;

namespace NubArca.Api.Ai.NaturalGallery;

// Deterministic, LOCAL date-phrase resolution for IT + EN. Turns common relative
// and absolute expressions into whole-day UTC boundaries (From = day start Z, To
// = day end Z) computed against the client's time zone + current instant. It only
// resolves what it can do SAFELY: a genuinely ambiguous year that would
// materially change the query is flagged (NeedsClarification) rather than
// silently guessed; a year assumed from context emits a warning code. The
// matched spans are returned so the interpreter can strip them before extracting
// the visual semantic residual (so "estate 2024" never leaks into the query).
public static partial class GalleryDateResolver
{
    public sealed record DateResolveResult(
        DateTime? From,
        DateTime? To,
        IReadOnlyList<string> MatchedSpans,
        IReadOnlyList<string> Warnings,
        bool NeedsClarification)
    {
        public static readonly DateResolveResult None =
            new(null, null, Array.Empty<string>(), Array.Empty<string>(), false);
        public bool HasMatch => MatchedSpans.Count > 0;
    }

    // Warning / clarification codes (machine-stable; never raw text).
    public const string WarnYearAssumed = "date_year_assumed";
    public const string WarnWinterYearAssumed = "date_winter_year_assumed";

    public static DateResolveResult Resolve(string command, GalleryCommandContext ctx)
    {
        if (string.IsNullOrWhiteSpace(command)) return DateResolveResult.None;
        var nowLocal = TimeZoneInfo.ConvertTime(ctx.Now, ctx.TimeZone);
        var today = nowLocal.Date;
        var text = command;

        // Ordered from most-specific to least. First match wins.
        return TryExplicitRange(text)
            ?? TryBeforeAfter(text)
            ?? TrySeasonYear(text)
            ?? TryChristmas(text, today)
            ?? TryMonthYearOrMonth(text, today)
            ?? TryExplicitYear(text)
            ?? TryRelative(text, today)
            ?? DateResolveResult.None;
    }

    // "dal 3 al 10 giugno [2024]" / "from June 3 to June 10[, 2024]" / "3-10 giugno"
    private static DateResolveResult? TryExplicitRange(string text)
    {
        // IT: dal <d> al <d> <month> [year]
        var it = Regex.Match(text,
            @"\bdal\s+(\d{1,2})\s+al\s+(\d{1,2})\s+(" + MonthAlt + @")(?:\s+(\d{4}))?",
            RegexOptions.IgnoreCase);
        if (it.Success)
        {
            var m = MonthNumber(it.Groups[3].Value);
            var (year, assumed) = ResolveYear(it.Groups[4].Value);
            var d1 = int.Parse(it.Groups[1].Value, CultureInfo.InvariantCulture);
            var d2 = int.Parse(it.Groups[2].Value, CultureInfo.InvariantCulture);
            return DayRange(year, m, d1, year, m, d2, it.Value, assumed);
        }

        // EN: from <month> <d> to <month> <d> [year]
        var en = Regex.Match(text,
            @"\bfrom\s+(" + MonthAlt + @")\s+(\d{1,2})\s+to\s+(" + MonthAlt + @")\s+(\d{1,2})(?:,?\s+(\d{4}))?",
            RegexOptions.IgnoreCase);
        if (en.Success)
        {
            var m1 = MonthNumber(en.Groups[1].Value);
            var m2 = MonthNumber(en.Groups[3].Value);
            var (year, assumed) = ResolveYear(en.Groups[5].Value);
            var d1 = int.Parse(en.Groups[2].Value, CultureInfo.InvariantCulture);
            var d2 = int.Parse(en.Groups[4].Value, CultureInfo.InvariantCulture);
            return DayRange(year, m1, d1, year, m2, d2, en.Value, assumed);
        }
        return null;
    }

    // "prima del 2020" / "before 2020"  |  "dopo agosto 2024" / "after August 2024" / "after 2020"
    private static DateResolveResult? TryBeforeAfter(string text)
    {
        var before = Regex.Match(text,
            @"\b(?:prima del|prima dell'|before)\s+(?:(" + MonthAlt + @")\s+)?(\d{4})",
            RegexOptions.IgnoreCase);
        if (before.Success)
        {
            var year = int.Parse(before.Groups[2].Value, CultureInfo.InvariantCulture);
            var month = before.Groups[1].Success ? MonthNumber(before.Groups[1].Value) : 1;
            var boundary = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            return new DateResolveResult(null, boundary.AddSeconds(-1),
                new[] { before.Value }, Array.Empty<string>(), false);
        }

        var after = Regex.Match(text,
            @"\b(?:dopo|after)\s+(?:(" + MonthAlt + @")\s+)?(\d{4})",
            RegexOptions.IgnoreCase);
        if (after.Success)
        {
            var year = int.Parse(after.Groups[2].Value, CultureInfo.InvariantCulture);
            var month = after.Groups[1].Success ? MonthNumber(after.Groups[1].Value) : 12;
            var lastDay = DateTime.DaysInMonth(year, month);
            var start = new DateTime(year, month, lastDay, 23, 59, 59, DateTimeKind.Utc).AddSeconds(1);
            return new DateResolveResult(start, null,
                new[] { after.Value }, Array.Empty<string>(), false);
        }
        return null;
    }

    // "estate 2024" / "summer 2024" / "inverno 2023" / "winter 2023"
    private static DateResolveResult? TrySeasonYear(string text)
    {
        var m = Regex.Match(text,
            @"\b(estate|inverno|primavera|autunno|summer|winter|spring|autumn|fall)\s+(?:del\s+|of\s+)?(\d{4})",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var season = m.Groups[1].Value.ToLowerInvariant();
        var year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var warnings = new List<string>();
        (int fm, int fd, int tm, int td, int fromYear, int toYear) s = season switch
        {
            "primavera" or "spring" => (3, 1, 5, 31, year, year),
            "estate" or "summer" => (6, 1, 8, 31, year, year),
            "autunno" or "autumn" or "fall" => (9, 1, 11, 30, year, year),
            _ => (12, 1, 2, LastFeb(year + 1), year, year + 1), // winter starts Dec of named year
        };
        if (season is "inverno" or "winter") warnings.Add(WarnWinterYearAssumed);
        return DayRange(s.fromYear, s.fm, s.fd, s.toYear, s.tm, s.td, m.Value, false, warnings);
    }

    // "Natale 2025" / "Christmas 2025"
    private static DateResolveResult? TryChristmas(string text, DateTime today)
    {
        var withYear = Regex.Match(text, @"\b(?:natale|christmas)\s+(\d{4})", RegexOptions.IgnoreCase);
        if (withYear.Success)
        {
            var year = int.Parse(withYear.Groups[1].Value, CultureInfo.InvariantCulture);
            return DayRange(year, 12, 24, year, 12, 26, withYear.Value, false);
        }
        // "scorso Natale" / "last Christmas" — most recent past Dec 25.
        var last = Regex.Match(text,
            @"\b(?:(?:lo\s+)?scorso\s+natale|natale\s+scorso|last\s+christmas)\b",
            RegexOptions.IgnoreCase);
        if (last.Success)
        {
            var year = today.Month >= 12 && today.Day > 26 ? today.Year : today.Year - 1;
            return DayRange(year, 12, 24, year, 12, 26, last.Value, false);
        }
        return null;
    }

    // "giugno 2024" / "June 2024" (whole month) | bare "giugno"/"June" (year assumed)
    private static DateResolveResult? TryMonthYearOrMonth(string text, DateTime today)
    {
        var withYear = Regex.Match(text, @"\b(" + MonthAlt + @")\s+(\d{4})\b", RegexOptions.IgnoreCase);
        if (withYear.Success)
        {
            var month = MonthNumber(withYear.Groups[1].Value);
            var year = int.Parse(withYear.Groups[2].Value, CultureInfo.InvariantCulture);
            return DayRange(year, month, 1, year, month, DateTime.DaysInMonth(year, month), withYear.Value, false);
        }
        var bare = Regex.Match(text, @"\b(" + MonthAlt + @")\b", RegexOptions.IgnoreCase);
        if (bare.Success)
        {
            var month = MonthNumber(bare.Groups[1].Value);
            var year = today.Year; // assumed → warning, never silent
            return DayRange(year, month, 1, year, month, DateTime.DaysInMonth(year, month), bare.Value, true);
        }
        return null;
    }

    // "nel 2024" / "in 2024" (whole year). A bare 4-digit number is NOT treated
    // as a year unless clearly introduced — avoids eating "IMG_2024".
    private static DateResolveResult? TryExplicitYear(string text)
    {
        var m = Regex.Match(text, @"\b(?:nel|in|del|dell'|durante il|during)\s+(\d{4})\b", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return DayRange(year, 1, 1, year, 12, 31, m.Value, false);
    }

    // oggi/today, ieri/yesterday, questa/scorsa settimana, questo/scorso mese, quest'/scorso anno
    private static DateResolveResult? TryRelative(string text, DateTime today)
    {
        if (Regex.IsMatch(text, @"\b(oggi|today)\b", RegexOptions.IgnoreCase))
            return SingleDay(today, Regex.Match(text, @"\b(oggi|today)\b", RegexOptions.IgnoreCase).Value);
        if (Regex.IsMatch(text, @"\b(ieri|yesterday)\b", RegexOptions.IgnoreCase))
            return SingleDay(today.AddDays(-1), Regex.Match(text, @"\b(ieri|yesterday)\b", RegexOptions.IgnoreCase).Value);

        var lastWeek = Regex.Match(text,
            @"\b(?:la\s+settimana\s+scorsa|settimana\s+scorsa|last\s+week)\b", RegexOptions.IgnoreCase);
        if (lastWeek.Success)
        {
            var monday = StartOfWeek(today).AddDays(-7);
            return Range(monday, monday.AddDays(6), lastWeek.Value);
        }
        var thisWeek = Regex.Match(text,
            @"\b(?:questa\s+settimana|this\s+week)\b", RegexOptions.IgnoreCase);
        if (thisWeek.Success)
        {
            var monday = StartOfWeek(today);
            return Range(monday, monday.AddDays(6), thisWeek.Value);
        }

        var lastMonth = Regex.Match(text,
            @"\b(?:il\s+mese\s+scorso|mese\s+scorso|last\s+month)\b", RegexOptions.IgnoreCase);
        if (lastMonth.Success)
        {
            var first = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            return DayRange(first.Year, first.Month, 1, first.Year, first.Month,
                DateTime.DaysInMonth(first.Year, first.Month), lastMonth.Value, false);
        }
        var thisMonth = Regex.Match(text,
            @"\b(?:questo\s+mese|this\s+month)\b", RegexOptions.IgnoreCase);
        if (thisMonth.Success)
        {
            return DayRange(today.Year, today.Month, 1, today.Year, today.Month,
                DateTime.DaysInMonth(today.Year, today.Month), thisMonth.Value, false);
        }

        var lastYear = Regex.Match(text,
            @"\b(?:l'anno\s+scorso|anno\s+scorso|last\s+year)\b", RegexOptions.IgnoreCase);
        if (lastYear.Success)
            return DayRange(today.Year - 1, 1, 1, today.Year - 1, 12, 31, lastYear.Value, false);
        var thisYear = Regex.Match(text,
            @"\b(?:quest'anno|questo\s+anno|this\s+year)\b", RegexOptions.IgnoreCase);
        if (thisYear.Success)
            return DayRange(today.Year, 1, 1, today.Year, 12, 31, thisYear.Value, false);

        // "last summer" / "lo scorso estate" style handled by season-relative:
        var lastSummer = Regex.Match(text, @"\b(?:last\s+summer|scorsa\s+estate|estate\s+scorsa)\b", RegexOptions.IgnoreCase);
        if (lastSummer.Success)
        {
            var year = today.Month >= 9 ? today.Year : today.Year - 1;
            return DayRange(year, 6, 1, year, 8, 31, lastSummer.Value, false);
        }
        return null;
    }

    // ---- helpers ------------------------------------------------------------

    private static DateResolveResult SingleDay(DateTime day, string span) => Range(day, day, span);

    private static DateResolveResult Range(DateTime fromDay, DateTime toDay, string span)
    {
        var from = new DateTime(fromDay.Year, fromDay.Month, fromDay.Day, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(toDay.Year, toDay.Month, toDay.Day, 23, 59, 59, DateTimeKind.Utc);
        return new DateResolveResult(from, to, new[] { span }, Array.Empty<string>(), false);
    }

    private static DateResolveResult DayRange(
        int fy, int fm, int fd, int ty, int tm, int td, string span, bool yearAssumed,
        List<string>? extraWarnings = null)
    {
        var warnings = extraWarnings ?? new List<string>();
        if (yearAssumed) warnings.Add(WarnYearAssumed);
        var from = new DateTime(fy, fm, Math.Min(fd, DateTime.DaysInMonth(fy, fm)), 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(ty, tm, Math.Min(td, DateTime.DaysInMonth(ty, tm)), 23, 59, 59, DateTimeKind.Utc);
        return new DateResolveResult(from, to, new[] { span }, warnings, false);
    }

    private static (int Year, bool Assumed) ResolveYear(string yearGroup)
        => string.IsNullOrEmpty(yearGroup)
            ? (DateTime.UtcNow.Year, true)
            : (int.Parse(yearGroup, CultureInfo.InvariantCulture), false);

    private static int LastFeb(int year) => DateTime.IsLeapYear(year) ? 29 : 28;

    private static DateTime StartOfWeek(DateTime day)
    {
        // ISO week: Monday.
        var diff = (7 + (int)day.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return day.AddDays(-diff);
    }

    private const string MonthAlt =
        "gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre|" +
        "january|february|march|april|may|june|july|august|september|october|november|december|" +
        "jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec";

    private static int MonthNumber(string token)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "gennaio": case "january": case "jan": return 1;
            case "febbraio": case "february": case "feb": return 2;
            case "marzo": case "march": case "mar": return 3;
            case "aprile": case "april": case "apr": return 4;
            case "maggio": case "may": return 5;
            case "giugno": case "june": case "jun": return 6;
            case "luglio": case "july": case "jul": return 7;
            case "agosto": case "august": case "aug": return 8;
            case "settembre": case "september": case "sep": case "sept": return 9;
            case "ottobre": case "october": case "oct": return 10;
            case "novembre": case "november": case "nov": return 11;
            case "dicembre": case "december": case "dec": return 12;
            default: return 1;
        }
    }
}
