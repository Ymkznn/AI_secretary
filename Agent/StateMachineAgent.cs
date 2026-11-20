using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;
using Microsoft.Extensions.Logging;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// Standalone zero-hook autonomous state machine agent.
/// Receives raw sensor signals (window/page focus), consults or learns open-ended (StateName, Steps)
/// rules backed by a SQLite 2-bit saturating counter, and dispatches safe read/sync skills via WorkerAgent.
/// Enforces Latest-Wins cancellation and single-concurrency gating so rapid tab switching never jams the app.
/// </summary>
public sealed class StateMachineAgent : IDisposable
{
    private readonly SqliteStore _store;
    private readonly IModelGateway _model;
    private readonly WorkerAgent _worker;
    private readonly SkillRegistry _skillRegistry;
    private readonly GuardService _guard;
    private readonly ILogger<StateMachineAgent> _logger;

    private readonly SemaphoreSlim _evalGate = new(1, 1);
    private readonly object _latestLock = new();
    private CancellationTokenSource? _latestCts;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hitCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _coldCooldowns = new(StringComparer.OrdinalIgnoreCase);

    public StateMachineAgent(
        SqliteStore store,
        IModelGateway model,
        WorkerAgent worker,
        SkillRegistry skillRegistry,
        GuardService guard,
        ILogger<StateMachineAgent> logger)
    {
        _store = store;
        _model = model;
        _worker = worker;
        _skillRegistry = skillRegistry;
        _guard = guard;
        _logger = logger;
    }

    public static string BuildTriggerKey(string? url, string processName, string title)
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

        var cleanTitle = Regex.Replace((title ?? "").Trim(), @"\d+", "#");
        if (cleanTitle.Length > 80) cleanTitle = cleanTitle[..80];
        return $"win:{processName.ToLowerInvariant()}|{cleanTitle}";
    }

    public async Task HandleWindowFocusAsync(
        string conversationId,
        ExternalWindowTarget target,
        string? url,
        CancellationToken appCt = default)
    {
        var labels = target.FormFieldLabels ?? Array.Empty<string>();
        if (labels.Count == 0 && target.VisibleInputCount <= 0)
            return;

        var triggerKey = BuildTriggerKey(url, target.ProcessName, target.Title);

        // Latest-Wins: cancel any in-flight evaluation for an older tab/window.
        CancellationTokenSource linked;
        lock (_latestLock)
        {
            try { _latestCts?.Cancel(); } catch { }
            try { _latestCts?.Dispose(); } catch { }
            _latestCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            linked = _latestCts;
        }
        var ct = linked.Token;

        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is not null && existing.Status != "Disabled" && existing.PredictorCounter >= 2)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_hitCooldowns.TryGetValue(triggerKey, out var lastHit) || (now - lastHit) >= TimeSpan.FromSeconds(30))
            {
                _hitCooldowns[triggerKey] = now;
                await RecordOutcomeAsync(triggerKey, hit: true, estimatedTokensSaved: 90, ct);
            }

            var hotSteps = ParseSteps(existing.StepsJson);
            if (hotSteps.Count > 0)
                await ExecuteSafeStepsAsync(conversationId, triggerKey, existing.StateName, hotSteps, ct);
            return;
        }

        // Prevent repeated cold LLM calls on the exact same page within 60s.
        var nowUtc = DateTimeOffset.UtcNow;
        if (_coldCooldowns.TryGetValue(triggerKey, out var lastCold) && (nowUtc - lastCold) < TimeSpan.FromSeconds(60))
            return;
        _coldCooldowns[triggerKey] = nowUtc;

        if (!await _evalGate.WaitAsync(0, ct))
            return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            var skillsSummary = string.Join("\n", _skillRegistry.All
                .Where(s => _guard.IsSkillEnabled(s.Name))
                .Select(s => $"- {s.PlannerSpec}"));

            var sys = SystemAutomationPrompts.BuildAutoStateTransitionSystemPrompt(skillsSummary);
            var user = SystemAutomationPrompts.BuildAutoStateTransitionUserPrompt(
                triggerKey, target.ProcessName, target.Title, url, target.VisibleInputCount, labels);

            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", user)
            }, ModelTier.Simple, timeoutCts.Token);

            var (stateName, steps) = ParseTransition(resp.Content);
            var safeSteps = FilterSafeSteps(steps);
            var stepsJson = JsonSerializer.Serialize(safeSteps);

            int nextCounter = 1;
            int hits = existing?.HitCount ?? 0;
            int misses = existing?.MissCount ?? 0;
            if (existing is not null &&
                string.Equals(existing.StateName, stateName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StepsJson, stepsJson, StringComparison.Ordinal))
            {
                nextCounter = Math.Min(3, existing.PredictorCounter + 1);
            }

            var updated = new AutoStateRuleRecord(
                TriggerKey: triggerKey,
                StateName: stateName,
                StepsJson: stepsJson,
                PredictorCounter: nextCounter,
                HitCount: hits,
                MissCount: misses,
                Status: nextCounter >= 2 ? "Hot" : "Cold",
                LastSeenAt: DateTimeOffset.UtcNow);
            await _store.UpsertAutoStateRuleAsync(updated, CancellationToken.None);

            if (safeSteps.Count > 0)
                await ExecuteSafeStepsAsync(conversationId, triggerKey, stateName, safeSteps, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StateMachineAgent] transition evaluation failed for {Key}", triggerKey);
        }
        finally
        {
            _evalGate.Release();
        }
    }

    public async Task RecordOutcomeAsync(string triggerKey, bool hit, long estimatedTokensSaved = 90, CancellationToken ct = default)
    {
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is null) return;

        int nextCnt = hit
            ? Math.Min(3, existing.PredictorCounter + 1)
            : Math.Max(0, existing.PredictorCounter - 1);

        var nextStatus = (!hit && nextCnt == 0)
            ? "Disabled"
            : (nextCnt >= 2 ? "Hot" : "Cold");

        await _store.UpsertAutoStateRuleAsync(existing with
        {
            PredictorCounter = nextCnt,
            HitCount = hit ? existing.HitCount + 1 : existing.HitCount,
            MissCount = hit ? existing.MissCount : existing.MissCount + 1,
            Status = nextStatus,
            LastSeenAt = DateTimeOffset.UtcNow
        }, ct);

        await _store.RecordBranchOutcomeAsync(hit, hit ? estimatedTokensSaved : 0, ct);
    }

    public Task<List<AutoStateRuleRecord>> GetAllRulesAsync(CancellationToken ct = default)
        => _store.GetAllAutoStateRulesAsync(ct);

    public Task<bool> DeleteRuleAsync(string triggerKey, CancellationToken ct = default)
        => _store.DeleteAutoStateRuleAsync(triggerKey, ct);

    private async Task ExecuteSafeStepsAsync(
        string conversationId,
        string triggerKey,
        string stateName,
        List<CachedWorkflowStep> steps,
        CancellationToken ct)
    {
        foreach (var step in steps.Take(2))
        {
            if (!_guard.IsSkillEnabled(step.Skill)) continue;
            try
            {
                await _worker.ExecuteSingleSkillAsync(
                    conversationId: conversationId,
                    userPrompt: $"[auto_state:{stateName}] {triggerKey}",
                    skillName: step.Skill,
                    args: step.Args,
                    targetMailId: null,
                    confirmAsync: (_, _) => Task.FromResult(false),
                    onStatusAsync: _ => Task.CompletedTask,
                    tier: ModelTier.Simple,
                    ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[StateMachineAgent] step {Skill} failed for {Key}", step.Skill, triggerKey);
                await RecordOutcomeAsync(triggerKey, hit: false, estimatedTokensSaved: 0, CancellationToken.None);
            }
        }
    }

    private List<CachedWorkflowStep> FilterSafeSteps(List<CachedWorkflowStep> steps)
    {
        var safe = new List<CachedWorkflowStep>();
        foreach (var s in steps.Take(2))
        {
            if (string.IsNullOrWhiteSpace(s.Skill) || !_guard.IsSkillEnabled(s.Skill)) continue;
            if (WorkflowCacheService.HasNonReusableMutatingSteps(new[] { s })) continue;
            safe.Add(s);
        }
        return safe;
    }

    private static (string StateName, List<CachedWorkflowStep> Steps) ParseTransition(string? raw)
    {
        var steps = new List<CachedWorkflowStep>();
        if (string.IsNullOrWhiteSpace(raw))
            return ("idle", steps);

        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = cleaned.IndexOf('\n');
            if (nl >= 0) cleaned = cleaned[(nl + 1)..];
            var end = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) cleaned = cleaned[..end];
            cleaned = cleaned.Trim();
        }
        var objStart = cleaned.IndexOf('{');
        var objEnd = cleaned.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
            cleaned = cleaned[objStart..(objEnd + 1)];

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var stateName = root.TryGetProperty("state_name", out var sn) && sn.ValueKind == JsonValueKind.String
                ? sn.GetString()?.Trim().ToLowerInvariant()
                : null;
            if (string.IsNullOrWhiteSpace(stateName))
                stateName = "idle";

            if (root.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var skill = el.TryGetProperty("skill", out var sk) ? sk.GetString()?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(skill)) continue;
                    var args = el.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    steps.Add(new CachedWorkflowStep(skill, args));
                }
            }
            return (stateName, steps);
        }
        catch
        {
            return ("idle", steps);
        }
    }

    private static List<CachedWorkflowStep> ParseSteps(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CachedWorkflowStep>>(json) ?? new List<CachedWorkflowStep>();
        }
        catch
        {
            return new List<CachedWorkflowStep>();
        }
    }

    public void Dispose()
    {
        lock (_latestLock)
        {
            try { _latestCts?.Cancel(); } catch { }
            try { _latestCts?.Dispose(); } catch { }
            _latestCts = null;
        }
        _evalGate.Dispose();
    }
}
