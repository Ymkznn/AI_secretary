using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

public static class HolidayHelper
{
    private static SqliteStore? _store;
    private static IModelGateway? _model;
    private static readonly ConcurrentDictionary<(string CountryCode, int Year), HashSet<string>> HolidaysByYear = new();
    private static readonly ConcurrentDictionary<(string CountryCode, int Year), byte> InFlight = new();

    public static string GetCurrentCountryCode()
    {
        try
        {
            var code = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (!string.IsNullOrWhiteSpace(code) && code.Length == 2)
                return code.ToUpperInvariant();
        }
        catch
        {
        }
        return "US";
    }

    public static async Task InitializeAsync(SqliteStore store, IModelGateway model, CancellationToken ct = default)
    {
        _store = store;
        _model = model;

        var country = GetCurrentCountryCode();
        await LoadCachedYearsAsync(store, country, ct);

        var now = DateTime.Now;
        _ = Task.Run(() => EnsureYearLoadedAsync(store, now.Year, CancellationToken.None), CancellationToken.None);
        if (now.Month == 12)
            _ = Task.Run(() => EnsureYearLoadedAsync(store, now.Year + 1, CancellationToken.None), CancellationToken.None);
    }

    private static async Task LoadCachedYearsAsync(SqliteStore store, string countryCode, CancellationToken ct)
    {
        try
        {
            var cached = await store.GetUiLocaleCacheAsync($"holidays:{countryCode}", ct);
            foreach (var (yearStr, json) in cached)
            {
                if (!int.TryParse(yearStr, out var year)) continue;
                var dates = ParseDates(json);
                if (dates.Count > 0)
                    HolidaysByYear[(countryCode, year)] = dates;
            }
        }
        catch
        {
        }
    }

    public static async Task EnsureYearLoadedAsync(SqliteStore store, int year, CancellationToken ct = default)
    {
        var country = GetCurrentCountryCode();
        var key = (country, year);
        if (HolidaysByYear.ContainsKey(key)) return;

        await LoadCachedYearsAsync(store, country, ct);
        if (HolidaysByYear.ContainsKey(key) || _model is null) return;

        if (!InFlight.TryAdd(key, 0)) return;
        try
        {
            var prompt =
                $"List all official national public holidays (including observed or substitute holidays if applicable) " +
                $"for ISO 3166-1 alpha-2 country/region '{country}' in year {year}. " +
                $"Return ONLY a JSON array of YYYY-MM-DD strings, e.g. [\"{year}-01-01\"]. No markdown or explanation.";

            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", "You are a precise calendar data assistant. Reply only with a JSON array of YYYY-MM-DD date strings."),
                new ModelMessage("user", prompt)
            }, ModelTier.Simple, ct);

            var dates = ParseDates(resp.Content);
            if (dates.Count > 0)
            {
                HolidaysByYear[key] = dates;
                var sortedJson = JsonSerializer.Serialize(dates.OrderBy(d => d, StringComparer.Ordinal).ToArray());
                await store.UpsertUiLocaleCacheAsync($"holidays:{country}", year.ToString(CultureInfo.InvariantCulture), sortedJson, ct);
            }
        }
        catch
        {
            // Offline or unconfigured LLM: fall back gracefully without crashing.
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }

    private static HashSet<string> ParseDates(string? raw)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return set;

        foreach (Match m in Regex.Matches(raw, @"\b\d{4}-\d{2}-\d{2}\b"))
        {
            if (DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                set.Add(m.Value);
        }
        return set;
    }

    public static bool IsHoliday(DateTime localDate)
    {
        var country = GetCurrentCountryCode();
        var key = (country, localDate.Year);
        if (HolidaysByYear.TryGetValue(key, out var dates))
            return dates.Contains(localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (_store is not null)
            _ = Task.Run(() => EnsureYearLoadedAsync(_store, localDate.Year, CancellationToken.None), CancellationToken.None);

        return false;
    }

    public static bool IsWeekend(DateTime localDate)
        => localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static async Task<bool> IsNonWorkingDayAsync(
        SqliteStore store,
        string conversationId,
        DateTimeOffset localNow,
        CancellationToken ct = default)
    {
        var d = localNow.LocalDateTime.Date;
        if (IsWeekend(d))
            return true;

        await EnsureYearLoadedAsync(store, d.Year, ct);
        if (IsHoliday(d))
            return true;

        var dayStart = new DateTimeOffset(d, localNow.Offset);
        var dayEnd = dayStart.AddDays(1).AddSeconds(-1);
        var events = await store.GetCalendarEventsAsync(conversationId, dayStart, dayEnd, ct);
        return events.Any(e => e.Category.Equals("leave", StringComparison.OrdinalIgnoreCase)
                            || e.Category.Equals("holiday", StringComparison.OrdinalIgnoreCase));
    }
}
