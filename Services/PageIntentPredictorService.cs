using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Persistent 2-bit branch predictor for browser URLs and desktop windows.
/// Eliminates repeat LLM window classification calls and validates otp_wait/form_fill guesses via real feedback.
/// </summary>
public sealed class PageIntentPredictorService
{
    private readonly SqliteStore _store;

    public PageIntentPredictorService(SqliteStore store)
    {
        _store = store;
    }

    public static string BuildPatternKey(string? url, string processName, string title)
    {
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Regex.IsMatch(s, @"^(?:cht|chs|[a-z]{2}(?:-[a-z]{2,4})?|\d+)$", RegexOptions.IgnoreCase)
                    ? "*"
                    : s)
                .Take(4);
            return $"url:{uri.Host.ToLowerInvariant()}/{string.Join("/", segs)}";
        }

        var cleanTitle = Regex.Replace(title.Trim(), @"\d+", "#");
        if (cleanTitle.Length > 80) cleanTitle = cleanTitle[..80];
        return $"win:{processName.ToLowerInvariant()}|{cleanTitle}";
    }

    public async Task<(string PatternKey, string? PredictedIntent, bool IsHighConfidence)> TryPredictAsync(
        string? url,
        string processName,
        string title,
        CancellationToken ct = default)
    {
        var key = BuildPatternKey(url, processName, title);
        var rec = await _store.GetPageIntentAsync(key, ct);
        if (rec is null)
            return (key, null, false);

        if (rec.PredictorCounter >= 2)
            return (key, rec.Intent, true);

        return (key, rec.Intent, false);
    }

    public async Task RecordAiClassificationAsync(string patternKey, string intent, CancellationToken ct = default)
    {
        var existing = await _store.GetPageIntentAsync(patternKey, ct);
        if (existing is not null && string.Equals(existing.Intent, intent, StringComparison.OrdinalIgnoreCase))
        {
            var nextCnt = Math.Min(3, existing.PredictorCounter + 1);
            await _store.UpsertPageIntentAsync(existing with
            {
                PredictorCounter = nextCnt,
                LastSeenAt = DateTimeOffset.UtcNow
            }, ct);
            return;
        }

        await _store.UpsertPageIntentAsync(new PageIntentRecord(
            PatternKey: patternKey,
            Intent: intent,
            PredictorCounter: 1,
            HitCount: 0,
            MissCount: 0,
            LastSeenAt: DateTimeOffset.UtcNow), ct);
    }

    public async Task RecordOutcomeAsync(string patternKey, bool hit, long estimatedTokensSaved = 90, CancellationToken ct = default)
    {
        var existing = await _store.GetPageIntentAsync(patternKey, ct);
        if (existing is null) return;

        int nextCnt = hit
            ? Math.Min(3, existing.PredictorCounter + 1)
            : Math.Max(0, existing.PredictorCounter - 1);

        var nextIntent = (!hit && nextCnt == 0) ? "media_or_idle" : existing.Intent;

        await _store.UpsertPageIntentAsync(existing with
        {
            Intent = nextIntent,
            PredictorCounter = nextCnt,
            HitCount = hit ? existing.HitCount + 1 : existing.HitCount,
            MissCount = hit ? existing.MissCount : existing.MissCount + 1,
            LastSeenAt = DateTimeOffset.UtcNow
        }, ct);

        await _store.RecordBranchOutcomeAsync(hit, hit ? estimatedTokensSaved : 0, ct);
    }
}
