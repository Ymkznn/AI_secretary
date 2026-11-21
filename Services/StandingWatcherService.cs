using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Deterministic, zero-token background watcher engine for whitelisted URLs, JSON APIs, and isolated Browser DOM checks.
/// Only surfaces notifications when a configured condition (price threshold, text match, or snapshot diff) triggers.
/// </summary>
public sealed class StandingWatcherService
{
    private readonly SqliteStore _store;
    private readonly GuardService _guard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly BrowserHostService _browserHost;
    private readonly UiLocalizer _localizer;
    private readonly AgentBlackboard _blackboard;
    private readonly ILogger<StandingWatcherService> _logger;
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    public StandingWatcherService(
        SqliteStore store,
        GuardService guard,
        IHttpClientFactory httpFactory,
        BrowserHostService browserHost,
        UiLocalizer localizer,
        AgentBlackboard blackboard,
        ILogger<StandingWatcherService> logger)
    {
        _store = store;
        _guard = guard;
        _httpFactory = httpFactory;
        _browserHost = browserHost;
        _localizer = localizer;
        _blackboard = blackboard;
        _logger = logger;
    }

    public async Task TickDueWatchersAsync(
        Func<StandingWatcherRecord, string, Task> onTriggeredAsync,
        CancellationToken ct = default)
    {
        if (!await _tickLock.WaitAsync(0, ct)) return;
        try
        {
            var all = await _store.GetWatchersAsync(null, ct);
            var now = DateTimeOffset.UtcNow;
            var due = all
                .Where(w => w.Status == "Active" && (!w.NextCheckAt.HasValue || w.NextCheckAt.Value <= now))
                .ToList();

            foreach (var w in due)
            {
                if (ct.IsCancellationRequested) break;
                await EvaluateOneWatcherAsync(w, onTriggeredAsync, ct);
            }
        }
        finally
        {
            _tickLock.Release();
        }
    }

    public async Task<(bool Ok, string Message)> EvaluateOneWatcherAsync(
        StandingWatcherRecord w,
        Func<StandingWatcherRecord, string, Task> onTriggeredAsync,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var jitterSec = Random.Shared.Next(-Math.Max(2, w.IntervalSeconds / 15), Math.Max(3, w.IntervalSeconds / 10));
        var nextAt = now.AddSeconds(Math.Max(60, w.IntervalSeconds + jitterSec));
        var bbId = $"watcher:{w.WatcherId}";
        var bbGoal = $"Check watcher: {w.Title}";
        _blackboard.StartTask(bbId, "watcher", bbGoal);
        bool bbCompleted = false;

        try
        {
            var (status, currentValue) = await FetchCurrentValueAsync(w, ct);

            if (status == "needs_auth")
            {
                await _store.UpdateWatcherStateAsync(w.WatcherId, w.LastSnapshot, now, nextAt, w.FailureCount, "NeedsAuth", ct);
                var needsAuthMsg = await _localizer.TextAsync("WatcherNeedsAuth", "Session expired for '{0}'. Please reopen the dedicated browser and sign in to resume.", new object?[] { w.Title }, ct);
                _blackboard.CompleteTask(bbId, "watcher", bbGoal, needsAuthMsg, succeeded: false, recordInRecent: true);
                bbCompleted = true;
                await onTriggeredAsync(w, needsAuthMsg);
                return (false, "needs_auth");
            }

            if (status != "ok")
            {
                var fails = w.FailureCount + 1;
                var newStatus = fails >= 5 ? "Paused" : w.Status;
                await _store.UpdateWatcherStateAsync(w.WatcherId, w.LastSnapshot, now, nextAt, fails, newStatus, ct);
                _blackboard.CompleteTask(bbId, "watcher", bbGoal, $"Watcher '{w.Title}' check error: {currentValue}", succeeded: false, recordInRecent: newStatus == "Paused", dedupeKey: $"watcher:{w.WatcherId}");
                bbCompleted = true;
                if (newStatus == "Paused")
                {
                    var pausedMsg = await _localizer.TextAsync("WatcherPaused", "Watcher '{0}' paused automatically after {1} consecutive failures: {2}", new object?[] { w.Title, fails, currentValue }, ct);
                    await onTriggeredAsync(w, pausedMsg);
                }
                return (false, currentValue);
            }

            bool triggered = ShouldTrigger(w.ConditionType, w.ThresholdValue, w.LastSnapshot, currentValue);
            await _store.UpdateWatcherStateAsync(w.WatcherId, currentValue, now, nextAt, 0, "Active", ct);

            if (triggered)
            {
                var alertMsg = string.IsNullOrWhiteSpace(w.ThresholdValue)
                    ? await _localizer.TextAsync("WatcherChanged", "'{0}' detected a new change:\n{1}", new object?[] { w.Title, currentValue }, ct)
                    : await _localizer.TextAsync("WatcherMatched", "'{0}' matched condition (current: {1}, threshold: {2})\n{3}", new object?[] { w.Title, currentValue, w.ThresholdValue, w.Url }, ct);
                _blackboard.CompleteTask(bbId, "watcher", bbGoal, alertMsg, succeeded: true, recordInRecent: true);
                bbCompleted = true;
                await onTriggeredAsync(w, alertMsg);
            }
            else
            {
                _blackboard.CompleteTask(bbId, "watcher", bbGoal, $"Watcher '{w.Title}' checked (no trigger): {currentValue}", succeeded: true, recordInRecent: false, dedupeKey: $"watcher_idle:{w.WatcherId}");
                bbCompleted = true;
            }

            return (true, currentValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Watcher {Id} evaluation error", w.WatcherId);
            var fails = w.FailureCount + 1;
            var newStatus = fails >= 5 ? "Paused" : w.Status;
            await _store.UpdateWatcherStateAsync(w.WatcherId, w.LastSnapshot, now, nextAt, fails, newStatus, ct);
            _blackboard.CompleteTask(bbId, "watcher", bbGoal, $"Watcher '{w.Title}' error: {ex.Message}", succeeded: false, recordInRecent: false);
            bbCompleted = true;
            return (false, ex.Message);
        }
        finally
        {
            if (!bbCompleted)
                _blackboard.EnsureClosed(bbId);
        }
    }

    private async Task<(string Status, string Value)> FetchCurrentValueAsync(StandingWatcherRecord w, CancellationToken ct)
    {
        if (w.Kind.Equals("browser_dom", StringComparison.OrdinalIgnoreCase))
            return await _browserHost.ReadDomSelectorViaCdpAsync(w.Url, w.SelectorOrPath, ct);

        // SSRF check before any outbound HTTP fetch
        if (!Uri.TryCreate(w.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ("error", "Invalid URL scheme.");

        var addrs = await Dns.GetHostAddressesAsync(uri.Host, ct);
        if (addrs.Any(WebFetchSkill.IsPrivateOrLoopback))
            return ("error", "Private/loopback IP blocked by SSRF policy.");

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "AiSecretary-Watcher/1.0");
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return ("error", $"HTTP {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (w.Kind.Equals("http_json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(body);
            var extracted = ExtractJsonPath(doc.RootElement, w.SelectorOrPath);
            return extracted is null ? ("error", $"JSON path not found: {w.SelectorOrPath}") : ("ok", extracted);
        }

        // http_text: match regex pattern (with capture group 1 if present) on decoded text
        var plain = Regex.Replace(body, @"<(script|style|noscript)\b[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = Regex.Replace(plain, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(w.SelectorOrPath) || w.SelectorOrPath == "*")
            return ("ok", plain.Length > 300 ? plain[..300] : plain);

        var m = Regex.Match(plain, w.SelectorOrPath, RegexOptions.IgnoreCase);
        if (!m.Success)
            return ("error", $"Pattern not matched: {w.SelectorOrPath}");

        var val = m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : m.Value.Trim();
        return ("ok", val);
    }

    public static bool ShouldTrigger(string conditionType, string? thresholdValue, string? previousSnapshot, string currentValue)
    {
        var cond = conditionType.Trim().ToLowerInvariant();
        if (cond == "changed")
            return previousSnapshot is not null && !string.Equals(previousSnapshot, currentValue, StringComparison.Ordinal);

        if (cond == "contains")
        {
            if (string.IsNullOrWhiteSpace(thresholdValue)) return false;
            bool nowMatch = currentValue.Contains(thresholdValue, StringComparison.OrdinalIgnoreCase);
            bool prevMatch = previousSnapshot is not null && previousSnapshot.Contains(thresholdValue, StringComparison.OrdinalIgnoreCase);
            return nowMatch && !prevMatch;
        }

        if (!TryExtractNumber(currentValue, out var curNum) || !TryExtractNumber(thresholdValue, out var threshNum))
            return false;

        bool nowCond = cond switch
        {
            "lt" => curNum < threshNum,
            "lte" => curNum <= threshNum,
            "gt" => curNum > threshNum,
            "gte" => curNum >= threshNum,
            _ => false
        };

        if (!nowCond) return false;
        if (previousSnapshot is not null && TryExtractNumber(previousSnapshot, out var prevNum))
        {
            // Avoid spamming on every poll if already triggered at the exact same numeric value
            if (Math.Abs(prevNum - curNum) < 0.0001)
                return false;
        }
        return true;
    }

    private static bool TryExtractNumber(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var clean = raw.Replace(",", "");
        var m = Regex.Match(clean, @"-?\d+(?:\.\d+)?");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string? ExtractJsonPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next))
            {
                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var idx) && idx >= 0 && idx < current.GetArrayLength())
            {
                current = current[idx];
            }
            else
            {
                return null;
            }
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
            _ => current.GetRawText()
        };
    }
}
