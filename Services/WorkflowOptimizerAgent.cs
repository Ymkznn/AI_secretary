using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Standalone offline optimizer agent completely decoupled from the User/Main/Planner/Worker chat loop.
/// Periodically reviews compact BranchTelemetry + WorkflowCache statistics and quantizes threshold/promotion parameters.
/// Never touches Guard/Policy rules and never runs per-turn.
/// </summary>
public sealed class WorkflowOptimizerAgent
{
    private readonly IModelGateway _model;
    private readonly WorkflowCacheService _cache;
    private readonly SqliteStore _store;
    private readonly ILogger<WorkflowOptimizerAgent> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public WorkflowOptimizerAgent(
        IModelGateway model,
        WorkflowCacheService cache,
        SqliteStore store,
        ILogger<WorkflowOptimizerAgent> logger)
    {
        _model = model;
        _cache = cache;
        _store = store;
        _logger = logger;
    }

    public async Task MaybeRunOfflineOptimizationAsync(string conversationId, CancellationToken ct = default)
    {
        var stats = await _cache.GetTelemetryAsync(ct);
        if (stats.Predictions <= 0) return;

        var stateRaw = await _store.GetSyncValueAsync("workflow_optimizer_last_run", ct);
        long lastPreds = 0;
        DateTimeOffset lastAt = DateTimeOffset.MinValue;
        long nextAfterPreds = 10; // initial bootstrap before AI sets its own cadence
        double nextAfterHours = 24;

        if (!string.IsNullOrWhiteSpace(stateRaw))
        {
            try
            {
                using var doc = JsonDocument.Parse(stateRaw);
                if (doc.RootElement.TryGetProperty("predictions", out var pEl)) lastPreds = pEl.GetInt64();
                if (doc.RootElement.TryGetProperty("at", out var aEl) && DateTimeOffset.TryParse(aEl.GetString(), out var dt)) lastAt = dt;
                if (doc.RootElement.TryGetProperty("next_after_predictions", out var npEl)) nextAfterPreds = Math.Clamp(npEl.GetInt64(), 5, 500);
                if (doc.RootElement.TryGetProperty("next_after_hours", out var nhEl)) nextAfterHours = Math.Clamp(nhEl.GetDouble(), 1, 720);
            }
            catch { }
        }

        if (stats.Predictions <= lastPreds) return;

        bool enoughNewData = (stats.Predictions - lastPreds) >= nextAfterPreds;
        bool timeDue = lastAt != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - lastAt) >= TimeSpan.FromHours(nextAfterHours);
        if (!enoughNewData && !timeDue) return;

        await RunOptimizationOnceAsync(conversationId, ct);
    }

    public async Task<(bool Ok, string Note)> RunOptimizationOnceAsync(string conversationId, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            return (false, "Optimization already running.");

        try
        {
            var stats = await _cache.GetTelemetryAsync(ct);
            var workflows = await _cache.GetAllForTelemetryAsync(conversationId, ct);

            var compactPayload = JsonSerializer.Serialize(new
            {
                current_tuning = new
                {
                    overlap_threshold = Math.Round(_cache.CurrentOverlapThreshold, 2),
                    promote_min_counter = _cache.CurrentPromoteMinCounter
                },
                telemetry = new
                {
                    stats.Predictions,
                    stats.Hits,
                    stats.Mispredictions,
                    stats.HitRatePercent,
                    stats.EstimatedTokensSaved
                },
                top_workflows = workflows.Take(15).Select(w => new
                {
                    w.WorkflowId,
                    w.GoalSignature,
                    w.PredictorCounter,
                    w.HitCount,
                    w.MissCount,
                    w.Status
                })
            });

            var sys = SystemAutomationPrompts.BuildWorkflowOptimizerSystemPrompt();

            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", compactPayload)
            }, ModelTier.Simple, ct);

            var cleanJson = ExtractJsonObject(resp.Content);
            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            double threshold = root.TryGetProperty("overlap_threshold", out var ot) && ot.TryGetDouble(out var d)
                ? d
                : _cache.CurrentOverlapThreshold;
            int promote = root.TryGetProperty("promote_min_counter", out var pc) && pc.TryGetInt32(out var p)
                ? p
                : _cache.CurrentPromoteMinCounter;
            var note = root.TryGetProperty("note", out var nt) ? nt.GetString() ?? "AI tuned" : "AI tuned";

            // Prevent a cold-start / 0%-hit death spiral from locking the threshold too high.
            if (stats.Hits == 0 && stats.Predictions < 20)
            {
                threshold = Math.Min(threshold, 0.72);
                promote = Math.Min(promote, 2);
            }

            var disableIds = new List<string>();
            if (root.TryGetProperty("disable_ids", out var da) && da.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in da.EnumerateArray())
                {
                    var id = el.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) disableIds.Add(id);
                }
            }

            long nextAfterPreds = root.TryGetProperty("next_review_after_predictions", out var np) && np.TryGetInt64(out var npv)
                ? Math.Clamp(npv, 5, 500)
                : 50;
            double nextAfterHours = root.TryGetProperty("next_review_after_hours", out var nh) && nh.TryGetDouble(out var nhv)
                ? Math.Clamp(nhv, 1, 720)
                : 168;

            await _cache.ApplyTuningAsync(threshold, promote, disableIds, conversationId, note, ct);

            // Also prune noisy/disabled AutoStateRules and expired/stale Candidate facts offline.
            var stateRules = await _store.GetAllAutoStateRulesAsync(ct);
            foreach (var r in stateRules.Where(x => x.Status == "Disabled" || (x.MissCount >= 3 && x.MissCount > x.HitCount)))
            {
                await _store.DeleteAutoStateRuleAsync(r.TriggerKey, ct);
            }
            await _store.PruneExpiredAndStaleCandidateFactsAsync(conversationId, DateTimeOffset.UtcNow, ct);

            await _store.SetSyncValueAsync("workflow_optimizer_last_run",
                JsonSerializer.Serialize(new
                {
                    predictions = stats.Predictions,
                    at = DateTimeOffset.UtcNow.ToString("O"),
                    next_after_predictions = nextAfterPreds,
                    next_after_hours = nextAfterHours
                }), ct);

            _logger.LogInformation("WorkflowOptimizerAgent tuned: threshold={Threshold}, promote={Promote}, disabled={Disabled}",
                _cache.CurrentOverlapThreshold, _cache.CurrentPromoteMinCounter, disableIds.Count);
            return (true, _cache.LastOptimizationNote);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WorkflowOptimizerAgent run failed");
            return (false, ex.Message);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private static string ExtractJsonObject(string raw)
    {
        var s = (raw ?? "").Trim();
        int start = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }
}
