using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// CPU-style 2-bit saturating counter branch predictor and workflow cache.
/// Tracks repeated multi-step skill patterns, predicts Taken when counter >= 2 to bypass Planner LLM calls,
/// demotes on misprediction, and records telemetry alongside token usage.
/// </summary>
public sealed class WorkflowCacheService
{
    private readonly SqliteStore _store;
    private readonly GuardService _guard;
    private volatile float _overlapThreshold = 0.70f;
    private volatile int _promoteMinCounter = 2;
    private string _lastOptimizationNote = "Initial baseline (threshold=0.70, promote=2)";

    public double CurrentOverlapThreshold => _overlapThreshold;
    public int CurrentPromoteMinCounter => _promoteMinCounter;
    public string LastOptimizationNote => _lastOptimizationNote;

    public WorkflowCacheService(SqliteStore store, GuardService guard)
    {
        _store = store;
        _guard = guard;
    }

    public async Task LoadTuningAsync(CancellationToken ct = default)
    {
        var raw = await _store.GetSyncValueAsync("workflow_predictor_tuning", ct);
        if (string.IsNullOrWhiteSpace(raw)) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("overlap_threshold", out var ot) && ot.TryGetDouble(out var d))
                _overlapThreshold = (float)Math.Clamp(d, 0.55, 0.90);
            if (doc.RootElement.TryGetProperty("promote_min_counter", out var pc) && pc.TryGetInt32(out var p))
                _promoteMinCounter = Math.Clamp(p, 1, 3);
            if (doc.RootElement.TryGetProperty("note", out var nt) && !string.IsNullOrWhiteSpace(nt.GetString()))
                _lastOptimizationNote = nt.GetString()!;
        }
        catch { }
    }

    public async Task ApplyTuningAsync(
        double overlapThreshold,
        int promoteMinCounter,
        IReadOnlyList<string> disableWorkflowIds,
        string conversationId,
        string note,
        CancellationToken ct = default)
    {
        _overlapThreshold = (float)Math.Clamp(overlapThreshold, 0.55, 0.90);
        _promoteMinCounter = Math.Clamp(promoteMinCounter, 1, 3);
        if (!string.IsNullOrWhiteSpace(note))
            _lastOptimizationNote = note.Trim();

        if (disableWorkflowIds.Count > 0)
        {
            var disableSet = new HashSet<string>(disableWorkflowIds, StringComparer.OrdinalIgnoreCase);
            var all = await _store.GetWorkflowCachesAsync(conversationId, ct);
            foreach (var w in all.Where(x => disableSet.Contains(x.WorkflowId) && x.Status != "Disabled"))
            {
                await _store.DeleteWorkflowCacheAsync(w.WorkflowId, ct);
            }
        }

        var json = JsonSerializer.Serialize(new
        {
            overlap_threshold = Math.Round(_overlapThreshold, 2),
            promote_min_counter = _promoteMinCounter,
            note = _lastOptimizationNote
        });
        await _store.SetSyncValueAsync("workflow_predictor_tuning", json, ct);
    }

    public Task<List<WorkflowCacheRecord>> GetAllForTelemetryAsync(string conversationId, CancellationToken ct = default)
        => _store.GetWorkflowCachesAsync(conversationId, ct);

    public async Task InvalidateEventWorkflowsAsync(string conversationId, CancellationToken ct = default)
    {
        var all = await _store.GetWorkflowCachesAsync(conversationId, ct);
        foreach (var w in all.Where(x =>
                     x.GoalSignature.Contains("event:", StringComparison.OrdinalIgnoreCase) &&
                     x.Status != "Disabled"))
        {
            await _store.UpsertWorkflowCacheAsync(w with { PredictorCounter = 0, Status = "Disabled" }, ct);
        }
    }

    public async Task<(WorkflowCacheRecord? Record, List<CachedWorkflowStep> Steps)> TryPredictAsync(
        string conversationId,
        string goal,
        string userPrompt,
        CancellationToken ct = default)
    {
        // Normalize the query exactly like stored signatures so Jaccard compares like with like.
        var queryTokens = Tokenize(BuildSignature($"{goal} {userPrompt}"));
        if (queryTokens.Count == 0) return (null, new List<CachedWorkflowStep>());

        var all = await _store.GetWorkflowCachesAsync(conversationId, ct);
        WorkflowCacheRecord? best = null;
        double bestScore = 0.0;

        var threshold = _overlapThreshold;
        var minCounter = _promoteMinCounter;
        foreach (var w in all)
        {
            if (w.Status == "Disabled" || w.PredictorCounter < minCounter)
                continue;

            var sigTokens = Tokenize(w.GoalSignature);
            var score = ComputeOverlap(queryTokens, sigTokens);
            if (score >= threshold && score > bestScore)
            {
                best = w;
                bestScore = score;
            }
        }

        if (best is null)
            return (null, new List<CachedWorkflowStep>());

        var steps = ParseSteps(best.StepsJson);
        if (steps.Count == 0 || steps.Any(s => !_guard.IsSkillEnabled(s.Skill)) || HasNonReusableMutatingSteps(steps))
            return (null, new List<CachedWorkflowStep>());

        return (best, steps);
    }

    public async Task RecordPredictionOutcomeAsync(
        WorkflowCacheRecord rec,
        bool hit,
        long estimatedTokensSaved,
        CancellationToken ct = default)
    {
        int nextCounter = hit
            ? Math.Min(3, rec.PredictorCounter + 1)
            : Math.Max(0, rec.PredictorCounter - 1);

        var updated = rec with
        {
            PredictorCounter = nextCounter,
            HitCount = hit ? rec.HitCount + 1 : rec.HitCount,
            MissCount = hit ? rec.MissCount : rec.MissCount + 1,
            Status = nextCounter >= 2 ? "Hot" : "Cold",
            LastUsedAt = DateTimeOffset.UtcNow
        };

        await _store.UpsertWorkflowCacheAsync(updated, ct);
        await _store.RecordBranchOutcomeAsync(hit, estimatedTokensSaved, ct);
    }

    public async Task InvalidateBySourceFactAsync(string conversationId, string factId, CancellationToken ct = default)
    {
        var links = await _store.GetMemoryAnnotationsAsync(targetType: "workflow", sourceFactId: factId, ct: ct);
        if (links.Count > 0)
        {
            foreach (var link in links)
            {
                await _store.DeleteWorkflowCacheAsync(link.TargetId, ct);
            }
            return;
        }

        await InvalidateEventWorkflowsAsync(conversationId, ct);
    }

    public async Task RecordObservedPlanAsync(
        string conversationId,
        string goal,
        string userPrompt,
        IReadOnlyList<CachedWorkflowStep> steps,
        CancellationToken ct = default,
        IReadOnlyList<KnowledgeFactRecord>? sourcePrefs = null)
    {
        if (steps.Count < 2) return;
        // Plans that create/delete/update/fill with one-off hardcoded arguments (ids, text, values) cannot be replayed
        // for a different request; caching them only produces mispredictions that cost an extra LLM round.
        if (HasNonReusableMutatingSteps(steps)) return;

        var sig = BuildSignature($"{goal} {userPrompt}");
        if (string.IsNullOrWhiteSpace(sig)) return;

        var stepsJson = JsonSerializer.Serialize(steps);
        var existing = await _store.GetWorkflowCachesAsync(conversationId, ct);
        var queryTokens = Tokenize(sig);
        var match = existing.FirstOrDefault(w => ComputeOverlap(queryTokens, Tokenize(w.GoalSignature)) >= 0.75);

        string workflowId;
        if (match is not null)
        {
            int nextCounter = Math.Min(3, match.PredictorCounter + 1);
            var updated = match with
            {
                StepsJson = stepsJson,
                PredictorCounter = nextCounter,
                HitCount = match.HitCount + 1,
                Status = nextCounter >= 2 ? "Hot" : "Cold",
                LastUsedAt = DateTimeOffset.UtcNow
            };
            await _store.UpsertWorkflowCacheAsync(updated, ct);
            workflowId = match.WorkflowId;
        }
        else
        {
            var created = new WorkflowCacheRecord(
                WorkflowId: Guid.NewGuid().ToString("N")[..12],
                ConversationId: conversationId,
                GoalSignature: sig,
                StepsJson: stepsJson,
                PredictorCounter: 1, // Weakly Not Taken initially
                HitCount: 1,
                MissCount: 0,
                Status: "Cold",
                LastUsedAt: DateTimeOffset.UtcNow,
                CreatedAt: DateTimeOffset.UtcNow);
            await _store.UpsertWorkflowCacheAsync(created, ct);
            workflowId = created.WorkflowId;
        }

        if (sourcePrefs is { Count: > 0 })
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pref in sourcePrefs.Take(2))
            {
                await _store.UpsertMemoryAnnotationAsync(new MemoryAnnotationRecord(
                    AnnotationId: $"ann-{workflowId}-{pref.FactId}",
                    ConversationId: conversationId,
                    TargetType: "workflow",
                    TargetId: workflowId,
                    Note: $"Relies on user_pref: {pref.Summary}",
                    SourceFactId: pref.FactId,
                    CreatedAt: now,
                    UpdatedAt: now), ct);
            }
        }
    }

    public Task<BranchTelemetryStats> GetTelemetryAsync(CancellationToken ct = default)
        => _store.GetBranchTelemetryAsync(ct);

    private static List<CachedWorkflowStep> ParseSteps(string stepsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CachedWorkflowStep>>(stepsJson) ?? new List<CachedWorkflowStep>();
        }
        catch
        {
            return new List<CachedWorkflowStep>();
        }
    }

    private static readonly HashSet<string> MutatingActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "add", "delete", "remove", "update", "edit", "fill", "write", "append", "execute", "run",
        "send", "reply", "forward", "move", "rename", "dismiss", "cancel", "update_interval", "update_slide_text"
    };

    internal static bool HasNonReusableMutatingSteps(IReadOnlyList<CachedWorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Args.ValueKind != JsonValueKind.Object) continue;
            if (step.Args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String &&
                MutatingActions.Contains(a.GetString() ?? ""))
                return true;
        }
        return false;
    }

    private static string BuildSignature(string raw)
        => string.Join(" ", Tokenize(raw).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(48));

    private static bool IsCjk(char c)
        => (c >= '\u3040' && c <= '\u30FF') || (c >= '\u3400' && c <= '\u4DBF') ||
           (c >= '\u4E00' && c <= '\u9FFF') || (c >= '\uAC00' && c <= '\uD7AF') || (c >= '\uF900' && c <= '\uFAFF');

    /// <summary>
    /// Word tokens for Latin text, character bigrams for CJK runs. The old regex treated a whole Chinese
    /// sentence as one token, so two differently-worded requests never overlapped (≈0% predictor hit rate).
    /// </summary>
    private static HashSet<string> Tokenize(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (Match m in Regex.Matches(raw.ToLowerInvariant(), @"[\p{L}\p{N}_]+"))
        {
            var tok = m.Value;
            int i = 0;
            while (i < tok.Length)
            {
                int j = i;
                bool cjk = IsCjk(tok[i]);
                while (j < tok.Length && IsCjk(tok[j]) == cjk) j++;
                var run = tok[i..j];
                if (cjk)
                {
                    if (run.Length == 1) set.Add(run);
                    for (int k = 0; k + 1 < run.Length; k++) set.Add(run.Substring(k, 2));
                }
                else if (run.Length >= 2)
                {
                    set.Add(run);
                }
                i = j;
            }
        }
        return set;
    }

    private static double ComputeOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        int inter = a.Count(t => b.Contains(t));
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0.0 : (double)inter / union;
    }
}
