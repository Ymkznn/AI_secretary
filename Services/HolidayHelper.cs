using AiSecretary.Desktop.Data;

namespace AiSecretary.Desktop.Services;

public static class HolidayHelper
{
    private static readonly Dictionary<string, string> TaiwanHolidays = new(StringComparer.Ordinal)
    {
        // 2026 Taiwan Public Holidays
        ["2026-01-01"] = "中華民國開國紀念日",
        ["2026-02-16"] = "農曆除夕",
        ["2026-02-17"] = "春節初一",
        ["2026-02-18"] = "春節初二",
        ["2026-02-19"] = "春節初三",
        ["2026-02-20"] = "春節補假",
        ["2026-02-27"] = "和平紀念日補假",
        ["2026-02-28"] = "和平紀念日",
        ["2026-04-03"] = "兒童節補假",
        ["2026-04-04"] = "兒童節",
        ["2026-04-05"] = "清明節",
        ["2026-04-06"] = "清明節補假",
        ["2026-05-01"] = "勞動節",
        ["2026-06-19"] = "端午節",
        ["2026-09-25"] = "中秋節",
        ["2026-10-09"] = "國慶日補假",
        ["2026-10-10"] = "國慶日",

        // 2027 Taiwan Public Holidays
        ["2027-01-01"] = "中華民國開國紀念日",
        ["2027-02-05"] = "農曆除夕",
        ["2027-02-06"] = "春節初一",
        ["2027-02-07"] = "春節初二",
        ["2027-02-08"] = "春節初三",
        ["2027-02-09"] = "春節補假",
        ["2027-02-10"] = "春節補假",
        ["2027-02-28"] = "和平紀念日",
        ["2027-03-01"] = "和平紀念日補假",
        ["2027-04-04"] = "兒童節",
        ["2027-04-05"] = "清明節",
        ["2027-05-01"] = "勞動節",
        ["2027-06-09"] = "端午節",
        ["2027-09-15"] = "中秋節",
        ["2027-10-10"] = "國慶日",
        ["2027-10-11"] = "國慶日補假"
    };

    public static bool TryGetTaiwanHolidayName(DateTime localDate, out string name)
        => TaiwanHolidays.TryGetValue(localDate.ToString("yyyy-MM-dd"), out name!);

    public static bool IsWeekend(DateTime localDate)
        => localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static async Task<bool> IsNonWorkingDayAsync(
        SqliteStore store,
        string conversationId,
        DateTimeOffset localNow,
        CancellationToken ct = default)
    {
        var d = localNow.LocalDateTime.Date;
        if (IsWeekend(d) || TryGetTaiwanHolidayName(d, out _))
            return true;

        var dayStart = new DateTimeOffset(d, localNow.Offset);
        var dayEnd = dayStart.AddDays(1).AddSeconds(-1);
        var events = await store.GetCalendarEventsAsync(conversationId, dayStart, dayEnd, ct);
        return events.Any(e => e.Category.Equals("leave", StringComparison.OrdinalIgnoreCase)
                            || e.Category.Equals("holiday", StringComparison.OrdinalIgnoreCase));
    }
}
