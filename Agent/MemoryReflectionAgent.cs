using System.Text.Json;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using Microsoft.Extensions.Logging;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// Dedicated background reflection agent that inspects completed AI decisions (executed skills + observations)
/// against existing long-term memory, and autonomously records new reusable environment discoveries or
/// updates/deletes outdated or contradicted facts.
/// </summary>
public sealed class MemoryReflectionAgent : IDisposable
{
    private readonly SqliteStore _store;
    private readonly IModelGateway _model;
    private readonly SkillRuntime _skills;
    private readonly GuardService _guard;
    private readonly ILogger<MemoryReflectionAgent> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MemoryReflectionAgent(
        SqliteStore store,
        IModelGateway model,
        SkillRuntime skills,
        GuardService guard,
        ILogger<MemoryReflectionAgent> logger)
    {
        _store = store;
        _model = model;
        _skills = skills;
        _guard = guard;
        _logger = logger;
    }

    public void EnqueueChatReflection(string conversationId, string userPrompt, string directReply)
    {
        if (string.IsNullOrWhiteSpace(userPrompt) || !_guard.IsSkillEnabled("manage_facts"))
            return;

        var replySnippet = directReply.Length > 600 ? directReply[..600] + "…" : directReply;
        _ = Task.Run(() => ReflectAsync(
            conversationId,
            userPrompt,
            new[] { "chat_reply" },
            new[] { $"User stated: {userPrompt}" },
            replySnippet,
            defaultState: "Candidate",
            CancellationToken.None));
    }

    public void EnqueueDecisionReflection(
        string conversationId,
        string userPrompt,
        IReadOnlyList<string> completedSteps,
        IReadOnlyList<string> observations,
        string finalReply)
    {
        if (completedSteps.Count == 0 || !_guard.IsSkillEnabled("manage_facts"))
            return;

        // Avoid recursive reflection on pure manage_facts-only turns.
        if (completedSteps.All(s => s.StartsWith("manage_facts", StringComparison.OrdinalIgnoreCase)))
            return;

        var stepsCopy = completedSteps.Take(10).ToList();
        var obsCopy = observations.Take(8)
            .Select(o => o.Length > 800 ? o[..800] + "…" : o)
            .ToList();
        var replySnippet = finalReply.Length > 600 ? finalReply[..600] + "…" : finalReply;

        _ = Task.Run(() => ReflectAsync(conversationId, userPrompt, stepsCopy, obsCopy, replySnippet, defaultState: "Confirmed", CancellationToken.None));
    }

    private async Task ReflectAsync(
        string conversationId,
        string userPrompt,
        IReadOnlyList<string> completedSteps,
        IReadOnlyList<string> observations,
        string finalReply,
        string defaultState,
        CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            await _store.InsertLearningEventAsync("MemoryReflection", conversationId, "skipped", null, null, "busy_gate", CancellationToken.None);
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Allow enough budget for low-priority pacing queue wait + LLM completion
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            var recalled = await _store.RecallRelevantFactsAsync(conversationId, userPrompt, 6, timeoutCts.Token);
            var existingBlock = recalled.Count == 0
                ? "(none)"
                : string.Join("\n", recalled.Select(f => $"- fact_id={f.FactId} | key={f.MemoryKey} | state={f.State}({f.ReinforceCount}) | category={f.Category} | summary={f.Summary}"));

            var sys = SystemAutomationPrompts.BuildMemoryReflectionSystemPrompt();
            var user = SystemAutomationPrompts.BuildMemoryReflectionUserPrompt(
                userPrompt, completedSteps, observations, finalReply, existingBlock);

            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", user)
            }, new LlmCallOptions("MemoryReflection", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), timeoutCts.Token);

            var applied = await ApplyReflectionOpsAsync(conversationId, resp.Content, defaultState, timeoutCts.Token);
            if (applied > 0)
                await _store.InsertLearningEventAsync("MemoryReflection", conversationId, "observed", null, $"applied:{applied}", userPrompt.Length > 80 ? userPrompt[..80] : userPrompt, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _store.InsertLearningEventAsync("MemoryReflection", conversationId, "skipped", null, null, "timeout_or_cancelled", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MemoryReflectionAgent] reflection skipped");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ApplyReflectionOpsAsync(string conversationId, string? raw, string defaultState, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;

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

        using var doc = JsonDocument.Parse(cleaned);
        if (!doc.RootElement.TryGetProperty("ops", out var opsEl) || opsEl.ValueKind != JsonValueKind.Array)
            return 0;

        int applied = 0;
        foreach (var item in opsEl.EnumerateArray())
        {
            if (applied >= 2 || item.ValueKind != JsonValueKind.Object) break;
            var op = item.TryGetProperty("op", out var oEl) ? oEl.GetString()?.Trim().ToLowerInvariant() : null;
            var factId = item.TryGetProperty("fact_id", out var fEl) ? fEl.GetString()?.Trim() : null;
            var memKey = item.TryGetProperty("memory_key", out var mkEl) ? mkEl.GetString()?.Trim() : null;
            var category = item.TryGetProperty("category", out var cEl) ? cEl.GetString()?.Trim() : null;
            var state = item.TryGetProperty("state", out var stEl) ? stEl.GetString()?.Trim() : defaultState;
            var summary = item.TryGetProperty("summary", out var sEl) ? sEl.GetString()?.Trim() : null;
            var detail = item.TryGetProperty("detail", out var dEl) ? dEl.GetString()?.Trim() : null;
            var evidence = item.TryGetProperty("evidence", out var eEl) ? eEl.GetString()?.Trim() : null;
            List<string>? parsedTags = null;
            if (item.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                parsedTags = tagsEl.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetString()?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            DateTimeOffset? expiresAt = null;
            if (item.TryGetProperty("ttl_hours", out var ttlEl) && ttlEl.ValueKind == JsonValueKind.Number && ttlEl.TryGetInt32(out var ttlHours) && ttlHours > 0)
                expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(ttlHours, 1, 24 * 90));

            switch (op)
            {
                case "record":
                    if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(detail)) continue;
                    await _skills.RecordFactAsync(
                        conversationId: conversationId,
                        summary: summary,
                        detail: detail,
                        category: string.IsNullOrWhiteSpace(category) ? "general" : category,
                        evidence: evidence,
                        confidence: string.Equals(state, "Candidate", StringComparison.OrdinalIgnoreCase) ? "inferred" : "observed",
                        tags: parsedTags,
                        ct: ct,
                        memoryKey: memKey,
                        state: string.IsNullOrWhiteSpace(state) ? defaultState : state,
                        expiresAt: expiresAt);
                    applied++;
                    break;
                case "update":
                    if (string.IsNullOrWhiteSpace(factId)) continue;
                    await _skills.UpdateFactAsync(
                        factId: factId,
                        summary: summary,
                        detail: detail,
                        category: category,
                        evidence: evidence,
                        conversationId: conversationId,
                        ct: ct);
                    applied++;
                    break;
                case "delete":
                    if (string.IsNullOrWhiteSpace(factId)) continue;
                    await _skills.DeleteFactAsync(factId, conversationId, ct);
                    applied++;
                    break;
            }
        }

        if (applied > 0)
            _logger.LogInformation("[MemoryReflectionAgent] Applied {Count} memory update(s).", applied);
        return applied;
    }

    public void Dispose() => _gate.Dispose();
}
