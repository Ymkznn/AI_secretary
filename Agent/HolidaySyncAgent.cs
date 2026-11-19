using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Skills;
using Microsoft.Extensions.Logging;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// Dedicated background agent that uses WebFetchSkill + LLM to discover and verify
/// official national public holidays for a given ISO country code and year.
/// </summary>
public sealed class HolidaySyncAgent
{
    private readonly IModelGateway _model;
    private readonly SqliteStore _store;
    private readonly WebFetchSkill? _webFetch;
    private readonly ILogger<HolidaySyncAgent> _logger;

    public HolidaySyncAgent(
        IModelGateway model,
        SqliteStore store,
        IEnumerable<IBasicSkill> skills,
        ILogger<HolidaySyncAgent> logger)
    {
        _model = model;
        _store = store;
        _webFetch = skills.OfType<WebFetchSkill>().FirstOrDefault();
        _logger = logger;
    }

    public async Task<HashSet<string>> SyncYearAsync(string countryCode, int year, CancellationToken ct = default)
    {
        var messages = new List<ModelMessage>
        {
            new("system", """
                You are HolidaySyncAgent. Your sole job is to collect official national public holidays
                (including observed or substitute holidays) for a given ISO 3166-1 alpha-2 country code and year.

                Available actions (reply with ONLY a single JSON object, no markdown):
                1. Fetch a public URL: {"action": "web_fetch", "url": "https://..."}
                2. Finish with verified dates: {"action": "done", "dates": ["YYYY-MM-DD", ...]}

                Workflow:
                - First call web_fetch on a reliable public holiday source, such as
                  https://date.nager.at/api/v3/PublicHolidays/{year}/{countryCode}
                  or https://www.timeanddate.com/holidays/
                - Inspect the fetched data, fetch another authoritative page if needed, then return action "done"
                  with all verified YYYY-MM-DD dates for the requested year.
                """),
            new("user", $"Find all official national public holidays for country '{countryCode}' in year {year}.")
        };

        var collected = new HashSet<string>(StringComparer.Ordinal);
        const int maxTurns = 4;

        for (int turn = 0; turn < maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await _model.CompleteAsync(messages, ModelTier.Simple, ct);
            var raw = (resp.Content ?? "").Trim();
            messages.Add(new ModelMessage("assistant", raw));

            var (action, url, dates) = ParseAction(raw, year);
            foreach (var d in dates)
                collected.Add(d);

            if (action == "done" && collected.Count > 0)
                break;

            if (action == "web_fetch" && !string.IsNullOrWhiteSpace(url) && _webFetch is not null)
            {
                string fetched;
                try
                {
                    fetched = await _webFetch.WebFetchAsync(url, ct);
                }
                catch (Exception ex)
                {
                    fetched = $"web_fetch error: {ex.Message}";
                }

                if (fetched.Length > 12000)
                    fetched = fetched[..12000] + "\n...[truncated]";

                messages.Add(new ModelMessage("user", $"web_fetch result for {url}:\n{fetched}"));
                continue;
            }

            if (collected.Count > 0)
                break;

            messages.Add(new ModelMessage("user", "Please either call web_fetch with a valid https URL or return action \"done\" with the YYYY-MM-DD dates array."));
        }

        if (collected.Count > 0)
        {
            var sortedJson = JsonSerializer.Serialize(collected.OrderBy(d => d, StringComparer.Ordinal).ToArray());
            await _store.UpsertUiLocaleCacheAsync($"holidays:{countryCode}", year.ToString(CultureInfo.InvariantCulture), sortedJson, ct);
            _logger.LogInformation("[HolidaySyncAgent] Cached {Count} holidays for {Country} {Year}.", collected.Count, countryCode, year);
        }

        return collected;
    }

    private static (string Action, string? Url, List<string> Dates) ParseAction(string raw, int year)
    {
        var json = StripCodeFence(raw);
        var dates = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (root.TryGetProperty("dates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (IsValidYearDate(s, year))
                            dates.Add(s!);
                    }
                }
                return (action, url, dates);
            }
        }
        catch
        {
            foreach (Match m in Regex.Matches(raw, @"\b\d{4}-\d{2}-\d{2}\b"))
            {
                if (IsValidYearDate(m.Value, year))
                    dates.Add(m.Value);
            }
        }
        return ("", null, dates);
    }

    private static bool IsValidYearDate(string? s, int year)
        => !string.IsNullOrWhiteSpace(s)
        && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        && d.Year == year;

    private static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;
        var inner = trimmed[(firstNewline + 1)..];
        var lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        return (lastFence >= 0 ? inner[..lastFence] : inner).Trim();
    }
}
