using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
                var displayValue = string.IsNullOrWhiteSpace(w.ThresholdValue)
                    ? BuildReadableDiffSummary(w.LastSnapshot, currentValue)
                    : FormatSnapshotPreview(currentValue, 300);
                var alertMsg = string.IsNullOrWhiteSpace(w.ThresholdValue)
                    ? await _localizer.TextAsync("WatcherChanged", "'{0}' detected a new change:\n{1}", new object?[] { w.Title, displayValue }, ct)
                    : await _localizer.TextAsync("WatcherMatched", "'{0}' matched condition (current: {1}, threshold: {2})\n{3}", new object?[] { w.Title, displayValue, w.ThresholdValue, w.Url }, ct);
                _blackboard.CompleteTask(bbId, "watcher", bbGoal, alertMsg, succeeded: true, recordInRecent: true);
                bbCompleted = true;
                await onTriggeredAsync(w, alertMsg);
            }
            else
            {
                var preview = FormatSnapshotPreview(currentValue, 140);
                _blackboard.CompleteTask(bbId, "watcher", bbGoal, $"Watcher '{w.Title}' checked (no trigger): {preview}", succeeded: true, recordInRecent: false, dedupeKey: $"watcher_idle:{w.WatcherId}");
                bbCompleted = true;
            }

            return (true, FormatSnapshotPreview(currentValue, 200));
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

        // Wildcard "*": clean full-page structured text + SHA-256 hash so changes anywhere on the page
        // (e.g. parcel tracking tables deep below navigation menus) are reliably detected and diffed.
        if (string.IsNullOrWhiteSpace(w.SelectorOrPath) || w.SelectorOrPath == "*")
            return ("ok", ExtractWildcardPageSnapshot(body));

        // http_text with explicit regex selector: match pattern (with capture group 1 if present) on decoded text
        var plain = Regex.Replace(body, @"<(script|style|noscript)\b[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        plain = Regex.Replace(plain, @"\s+", " ").Trim();

        var m = Regex.Match(plain, w.SelectorOrPath, RegexOptions.IgnoreCase);
        if (!m.Success)
            return ("error", $"Pattern not matched: {w.SelectorOrPath}");

        var val = m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : m.Value.Trim();
        return ("ok", val);
    }

    internal static string ExtractWildcardPageSnapshot(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "sha256:0000000000000000\n";

        // 1) Strip comments, head, scripts, styles, noscript, svg, and hidden inputs
        var cleaned = Regex.Replace(html, @"<!--[\s\S]*?-->", " ");
        cleaned = Regex.Replace(cleaned, @"<(head|script|style|noscript|svg)\b[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<input\b[^>]*>", " ", RegexOptions.IgnoreCase);

        // 2) Try stripping chrome boilerplate (header/nav/footer/aside); keep stripped version if substantive text remains
        var withoutChrome = Regex.Replace(cleaned, @"<(header|nav|footer|aside)\b[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        var candidate = StripHtmlToStructuredLines(withoutChrome);
        var text = candidate.Length >= 80 ? candidate : StripHtmlToStructuredLines(cleaned);

        // 3) Compute SHA-256 over the ENTIRE normalized text so even changes past 12k chars trigger reliably
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var hashPrefix = Convert.ToHexString(hashBytes[..8]).ToLowerInvariant();
        var bodySlice = text.Length > 12000 ? text[..12000] : text;
        return $"sha256:{hashPrefix}\n{bodySlice}";
    }

    private static string StripHtmlToStructuredLines(string html)
    {
        // Preserve table cells as " | " and row/block boundaries as newlines so diffs read cleanly
        var s = Regex.Replace(html, @"</(td|th)\s*>", " | ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(br|/tr|/p|/div|/li|/h[1-6]|/section|/article|/table)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);

        var lines = s.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = Regex.Replace(raw, @"[ \t\r]+", " ").Trim();
            line = Regex.Replace(line, @"(\s*\|\s*){2,}", " | ").Trim(' ', '|');
            if (!string.IsNullOrWhiteSpace(line))
                kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    internal static string BuildReadableDiffSummary(string? previousSnapshot, string currentValue, int maxChars = 600)
    {
        if (!currentValue.StartsWith("sha256:", StringComparison.Ordinal))
            return FormatSnapshotPreview(currentValue, maxChars);

        var curBody = StripHashHeader(currentValue);
        var prevBody = previousSnapshot is not null && previousSnapshot.StartsWith("sha256:", StringComparison.Ordinal)
            ? StripHashHeader(previousSnapshot)
            : "";

        var prevLines = new HashSet<string>(
            prevBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        var added = new List<string>();
        foreach (var line in curBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!prevLines.Contains(line))
            {
                added.Add($"• {line}");
                if (added.Count >= 12) break;
            }
        }

        var summary = added.Count > 0
            ? string.Join("\n", added)
            : "網頁內容有變更（部分區塊已移除或更新）。";

        return summary.Length > maxChars ? summary[..maxChars] + "…" : summary;
    }

    public static string FormatSnapshotPreview(string? snapshot, int maxChars = 160)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return "(尚未取得快照)";
        var body = snapshot.StartsWith("sha256:", StringComparison.Ordinal)
            ? StripHashHeader(snapshot)
            : snapshot;
        var flat = Regex.Replace(body, @"\s*\n+\s*", " · ").Trim();
        if (string.IsNullOrWhiteSpace(flat)) return "(空白內容)";
        return flat.Length > maxChars ? flat[..maxChars] + "…" : flat;
    }

    private static string StripHashHeader(string snapshot)
    {
        var nl = snapshot.IndexOf('\n');
        return nl >= 0 && nl + 1 < snapshot.Length ? snapshot[(nl + 1)..] : "";
    }

    private static string ExtractHashLine(string snapshot)
    {
        var nl = snapshot.IndexOf('\n');
        return nl >= 0 ? snapshot[..nl] : snapshot;
    }

    public static bool ShouldTrigger(string conditionType, string? thresholdValue, string? previousSnapshot, string currentValue)
    {
        var cond = conditionType.Trim().ToLowerInvariant();
        if (cond == "changed")
        {
            if (previousSnapshot is null)
                return false;

            // Silent one-time baseline upgrade when migrating legacy 300-char snapshots to full-page sha256 snapshots.
            bool curHashed = currentValue.StartsWith("sha256:", StringComparison.Ordinal);
            bool prevHashed = previousSnapshot.StartsWith("sha256:", StringComparison.Ordinal);
            if (curHashed && !prevHashed)
                return false;
            if (curHashed && prevHashed)
                return !string.Equals(ExtractHashLine(previousSnapshot), ExtractHashLine(currentValue), StringComparison.Ordinal);

            return !string.Equals(previousSnapshot, currentValue, StringComparison.Ordinal);
        }

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
