using System.Globalization;

namespace AiSecretary.Desktop.Services;

public static class RecurrenceHelper
{
    public sealed record ParsedRRule(
        string Freq,
        int Interval,
        List<(int? Ordinal, DayOfWeek Day)> ByDay,
        List<int> ByMonthDay,
        List<int> ByMonth,
        DateTimeOffset? Until,
        bool IsWorkdaysShortcut);

    public static bool IsRecurring(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return false;
        var t = rule.Trim();
        return !t.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWorkdaysRule(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return false;
        var parsed = Parse(rule);
        return parsed is not null && parsed.IsWorkdaysShortcut;
    }

    public static string Normalize(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return "none";
        var t = rule.Trim();
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
        return t.ToLowerInvariant() switch
        {
            "daily" => "FREQ=DAILY",
            "workdays" => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
            "weekly" => "FREQ=WEEKLY",
            "monthly" => "FREQ=MONTHLY",
            "yearly" => "FREQ=YEARLY",
            _ => t.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? t[6..].Trim().ToUpperInvariant() : t.ToUpperInvariant()
        };
    }

    public static ParsedRRule? Parse(string? rule)
    {
        var norm = Normalize(rule);
        if (norm == "none") return null;

        string freq = "";
        int interval = 1;
        var byDay = new List<(int? Ordinal, DayOfWeek Day)>();
        var byMonthDay = new List<int>();
        var byMonth = new List<int>();
        DateTimeOffset? until = null;

        foreach (var part in norm.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim().ToUpperInvariant();
            var val = part[(eq + 1)..].Trim();

            switch (key)
            {
                case "FREQ":
                    freq = val.ToUpperInvariant();
                    break;
                case "INTERVAL":
                    if (int.TryParse(val, out var iv) && iv >= 1) interval = iv;
                    break;
                case "BYDAY":
                    foreach (var token in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (TryParseByDayToken(token, out var ord, out var dow))
                            byDay.Add((ord, dow));
                    }
                    break;
                case "BYMONTHDAY":
                    foreach (var token in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (int.TryParse(token, out var md) && md != 0 && md >= -31 && md <= 31)
                            byMonthDay.Add(md);
                    }
                    break;
                case "BYMONTH":
                    foreach (var token in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (int.TryParse(token, out var m) && m >= 1 && m <= 12)
                            byMonth.Add(m);
                    }
                    break;
                case "UNTIL":
                    until = ParseUntil(val);
                    break;
            }
        }

        if (string.IsNullOrEmpty(freq)) return null;

        bool isWorkdays = freq == "WEEKLY" && interval == 1 && byDay.Count == 5 &&
            byDay.All(x => !x.Ordinal.HasValue && x.Day is >= DayOfWeek.Monday and <= DayOfWeek.Friday);

        return new ParsedRRule(freq, interval, byDay, byMonthDay, byMonth, until, isWorkdays);
    }

    private static bool TryParseByDayToken(string token, out int? ordinal, out DayOfWeek day)
    {
        ordinal = null;
        day = DayOfWeek.Monday;
        var t = token.Trim().ToUpperInvariant();
        if (t.Length < 2) return false;
        var dayCode = t[^2..];
        var prefix = t[..^2];
        if (!string.IsNullOrEmpty(prefix))
        {
            if (!int.TryParse(prefix, out var ord) || ord == 0 || ord < -5 || ord > 5)
                return false;
            ordinal = ord;
        }
        day = dayCode switch
        {
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            "SU" => DayOfWeek.Sunday,
            _ => (DayOfWeek)99
        };
        return (int)day <= 6;
    }

    private static DateTimeOffset? ParseUntil(string val)
    {
        if (DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            // If date-only (length <= 10), include the entire end of that local day
            if (val.Length <= 10)
                return new DateTimeOffset(dto.Year, dto.Month, dto.Day, 23, 59, 59, dto.Offset);
            return dto;
        }
        if (DateTime.TryParseExact(val, new[] { "yyyyMMdd", "yyyyMMddTHHmmss", "yyyyMMddTHHmmssZ" },
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            if (val.Length == 8)
                return new DateTimeOffset(dt.Year, dt.Month, dt.Day, 23, 59, 59, DateTimeOffset.Now.Offset);
            return new DateTimeOffset(dt, val.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ? TimeSpan.Zero : DateTimeOffset.Now.Offset);
        }
        return null;
    }

    public static IEnumerable<DateTimeOffset> ExpandOccurrences(
        DateTimeOffset anchorStart,
        string? rule,
        TimeSpan duration,
        DateTimeOffset winStart,
        DateTimeOffset winEnd,
        HashSet<string>? exceptionDates = null)
    {
        var parsed = Parse(rule);
        if (parsed is null)
        {
            var end = anchorStart + duration;
            if (end >= winStart && anchorStart <= winEnd)
                yield return anchorStart;
            yield break;
        }

        var localAnchor = anchorStart.ToLocalTime();
        var effectiveWinEnd = parsed.Until.HasValue && parsed.Until.Value < winEnd ? parsed.Until.Value : winEnd;
        if (effectiveWinEnd < anchorStart) yield break;

        var anchorDate = localAnchor.Date;
        var startDate = winStart.ToLocalTime().Date > anchorDate ? winStart.ToLocalTime().Date.AddDays(-1) : anchorDate;
        var endDate = effectiveWinEnd.ToLocalTime().Date;

        int emitted = 0;
        for (var d = startDate; d <= endDate && emitted < 1000; d = d.AddDays(1))
        {
            if (!MatchesRuleOnDate(d, anchorDate, parsed)) continue;

            var occStart = new DateTimeOffset(d.Year, d.Month, d.Day, localAnchor.Hour, localAnchor.Minute, localAnchor.Second, localAnchor.Offset);
            if (occStart < anchorStart) continue;
            if (parsed.Until.HasValue && occStart > parsed.Until.Value) yield break;

            var ymd = occStart.ToString("yyyy-MM-dd");
            if (exceptionDates is not null && exceptionDates.Contains(ymd)) continue;

            if (parsed.IsWorkdaysShortcut &&
                (HolidayHelper.IsWeekend(d) || HolidayHelper.TryGetTaiwanHolidayName(d, out _)))
            {
                continue;
            }

            var occEnd = occStart + duration;
            if (occEnd >= winStart && occStart <= effectiveWinEnd)
            {
                emitted++;
                yield return occStart;
            }
        }
    }

    public static DateTimeOffset? GetNextOccurrenceAfter(DateTimeOffset currentOccurrence, string? rule)
    {
        var parsed = Parse(rule);
        if (parsed is null) return null;

        var localCur = currentOccurrence.ToLocalTime();
        var anchorDate = localCur.Date;
        var searchEnd = parsed.Freq == "YEARLY"
            ? anchorDate.AddYears(parsed.Interval + 2)
            : anchorDate.AddDays(400);

        for (var d = anchorDate.AddDays(1); d <= searchEnd; d = d.AddDays(1))
        {
            if (!MatchesRuleOnDate(d, anchorDate, parsed)) continue;

            var next = new DateTimeOffset(d.Year, d.Month, d.Day, localCur.Hour, localCur.Minute, localCur.Second, localCur.Offset);
            if (parsed.Until.HasValue && next > parsed.Until.Value) return null;
            return next;
        }
        return null;
    }

    private static bool MatchesRuleOnDate(DateTime date, DateTime anchorDate, ParsedRRule rule)
    {
        if (date < anchorDate) return false;
        if (rule.ByMonth.Count > 0 && !rule.ByMonth.Contains(date.Month)) return false;

        switch (rule.Freq)
        {
            case "DAILY":
                return ((date - anchorDate).Days % rule.Interval) == 0;

            case "WEEKLY":
            {
                var anchorWeekStart = StartOfWeekMonday(anchorDate);
                var curWeekStart = StartOfWeekMonday(date);
                var weeksDiff = (curWeekStart - anchorWeekStart).Days / 7;
                if (weeksDiff < 0 || (weeksDiff % rule.Interval) != 0) return false;

                if (rule.ByDay.Count > 0)
                    return rule.ByDay.Any(b => b.Day == date.DayOfWeek);
                return date.DayOfWeek == anchorDate.DayOfWeek;
            }

            case "MONTHLY":
            {
                var monthsDiff = ((date.Year - anchorDate.Year) * 12) + (date.Month - anchorDate.Month);
                if (monthsDiff < 0 || (monthsDiff % rule.Interval) != 0) return false;

                int daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
                if (rule.ByMonthDay.Count > 0)
                {
                    return rule.ByMonthDay.Any(md => md > 0 ? date.Day == md : date.Day == daysInMonth + 1 + md);
                }
                if (rule.ByDay.Count > 0)
                {
                    return rule.ByDay.Any(b => MatchesMonthlyByDay(date, b.Ordinal, b.Day, daysInMonth));
                }
                var targetDay = Math.Min(anchorDate.Day, daysInMonth);
                return date.Day == targetDay;
            }

            case "YEARLY":
            {
                var yearsDiff = date.Year - anchorDate.Year;
                if (yearsDiff < 0 || (yearsDiff % rule.Interval) != 0) return false;
                if (rule.ByMonth.Count == 0 && date.Month != anchorDate.Month) return false;

                int daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
                if (rule.ByMonthDay.Count > 0)
                    return rule.ByMonthDay.Any(md => md > 0 ? date.Day == md : date.Day == daysInMonth + 1 + md);
                if (rule.ByDay.Count > 0)
                    return rule.ByDay.Any(b => MatchesMonthlyByDay(date, b.Ordinal, b.Day, daysInMonth));

                var targetDay = Math.Min(anchorDate.Day, daysInMonth);
                return date.Day == targetDay;
            }

            default:
                return false;
        }
    }

    private static bool MatchesMonthlyByDay(DateTime date, int? ordinal, DayOfWeek dow, int daysInMonth)
    {
        if (date.DayOfWeek != dow) return false;
        if (!ordinal.HasValue) return true;
        if (ordinal.Value > 0)
        {
            int occIndex = ((date.Day - 1) / 7) + 1;
            return occIndex == ordinal.Value;
        }
        else
        {
            int revIndex = ((daysInMonth - date.Day) / 7) + 1;
            return revIndex == -ordinal.Value;
        }
    }

    private static DateTime StartOfWeekMonday(DateTime dt)
    {
        int diff = ((int)dt.DayOfWeek + 6) % 7;
        return dt.AddDays(-diff).Date;
    }
}
