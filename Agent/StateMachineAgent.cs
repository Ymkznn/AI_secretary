using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// Zero-LLM behavior-learned autonomous state machine agent.
/// Never spends tokens probing unknown windows/pages on focus. Instead, it passively observes
/// safe read/sync skills that the user actually invokes on a window/page, promotes repeated
/// (TriggerKey -> Steps) patterns via a SQLite 2-bit saturating counter, and replays Hot rules
/// silently via <see cref="WorkerAgent.RunStepAsync"/> (0 LLM tokens on both cold and hot paths).
/// </summary>
public sealed class StateMachineAgent : IDisposable
{
    private readonly SqliteStore _store;
    private readonly WorkerAgent _worker;
    private readonly GuardService _guard;
    private readonly ILogger<StateMachineAgent> _logger;

    private readonly object _latestLock = new();
    private CancellationTokenSource? _latestCts;
    private string? _lastFocusedTriggerKey;
    private DateTimeOffset _lastFocusedAtUtc = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hitCooldowns = new(StringComparer.OrdinalIgnoreCase);

    public StateMachineAgent(
        SqliteStore store,
        WorkerAgent worker,
        GuardService guard,
        ILogger<StateMachineAgent> logger)
    {
        _store = store;
        _worker = worker;
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
        var triggerKey = BuildTriggerKey(url, target.ProcessName, target.Title);

        // Latest-Wins: record latest focused trigger key and cancel any older in-flight replay.
        CancellationTokenSource linked;
        lock (_latestLock)
        {
            _lastFocusedTriggerKey = triggerKey;
            _lastFocusedAtUtc = DateTimeOffset.UtcNow;
            try { _latestCts?.Cancel(); } catch { }
            try { _latestCts?.Dispose(); } catch { }
            _latestCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            linked = _latestCts;
        }
        var ct = linked.Token;

        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is null || existing.Status == "Disabled" || existing.PredictorCounter < 2)
            return;

        var now = DateTimeOffset.UtcNow;
        if (_hitCooldowns.TryGetValue(triggerKey, out var lastHit) && (now - lastHit) < TimeSpan.FromSeconds(30))
            return;
        _hitCooldowns[triggerKey] = now;

        var hotSteps = ParseSteps(existing.StepsJson);
        if (hotSteps.Count == 0)
            return;

        await RecordOutcomeAsync(triggerKey, hit: true, estimatedTokensSaved: 350, ct);
        await ExecuteSafeStepsAsync(conversationId, triggerKey, existing.StateName, hotSteps, ct);
    }

    /// <summary>
    /// Passively observes a user-initiated single-skill execution on the recently focused external window/page.
    /// Zero LLM calls: after 2 identical observations on the same TriggerKey, the rule promotes to Hot.
    /// </summary>
    public async Task ObserveUserSkillExecutionAsync(
        ExternalWindowTarget? fallbackTarget,
        string skillName,
        JsonElement args,
        CancellationToken ct = default)
    {
        var candidate = new CachedWorkflowStep(
            skillName.Trim(),
            args.ValueKind == JsonValueKind.Object ? args.Clone() : JsonDocument.Parse("{}").RootElement.Clone());
        var safeSteps = FilterSafeSteps(new List<CachedWorkflowStep> { candidate });
        if (safeSteps.Count == 0)
            return;

        string? triggerKey;
        lock (_latestLock)
        {
            triggerKey = !string.IsNullOrWhiteSpace(_lastFocusedTriggerKey) &&
                         (DateTimeOffset.UtcNow - _lastFocusedAtUtc) <= TimeSpan.FromMinutes(5)
                ? _lastFocusedTriggerKey
                : null;
        }

        if (string.IsNullOrWhiteSpace(triggerKey) && fallbackTarget is not null)
            triggerKey = BuildTriggerKey(null, fallbackTarget.ProcessName, fallbackTarget.Title);

        if (string.IsNullOrWhiteSpace(triggerKey))
            return;

        var stepsJson = JsonSerializer.Serialize(safeSteps);
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is not null && existing.Status == "Disabled")
            return;

        int nextCounter = 1;
        int hits = existing?.HitCount ?? 0;
        int misses = existing?.MissCount ?? 0;
        if (existing is not null && string.Equals(existing.StepsJson, stepsJson, StringComparison.Ordinal))
            nextCounter = Math.Min(3, existing.PredictorCounter + 1);

        var stateName = $"learned_{skillName.Trim().ToLowerInvariant()}";
        var updated = new AutoStateRuleRecord(
            TriggerKey: triggerKey,
            StateName: stateName,
            StepsJson: stepsJson,
            PredictorCounter: nextCounter,
            HitCount: hits,
            MissCount: misses,
            Status: nextCounter >= 2 ? "Hot" : "Cold",
            LastSeenAt: DateTimeOffset.UtcNow);

        await _store.UpsertAutoStateRuleAsync(updated, ct);
    }

    public async Task RecordOutcomeAsync(string triggerKey, bool hit, long estimatedTokensSaved = 350, CancellationToken ct = default)
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
                await _worker.RunStepAsync(
                    conversationId: conversationId,
                    userPrompt: $"[auto_state:{stateName}] {triggerKey}",
                    skillName: step.Skill,
                    args: step.Args,
                    targetMailId: null,
                    confirmAsync: (_, _) => Task.FromResult(false),
                    onStatusAsync: _ => Task.CompletedTask,
                    sources: new List<MailSourceRef>(),
                    observations: new List<string>(),
                    completedSteps: new List<string>(),
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
    }
}
