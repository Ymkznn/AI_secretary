using System.Text.Json;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// 郵件急迫性分診結果。
/// </summary>
/// <param name="Urgent">是否屬於需主動通知並置頂的急迫郵件。</param>
/// <param name="Title">通知卡片標題。</param>
/// <param name="Summary">一句話郵件摘要。</param>
/// <param name="ReRemindMinutes">置頂重複提醒間隔（分鐘），0 表示不重複。</param>
/// <param name="Actions">通知卡片上的快捷動作按鈕清單。</param>
public sealed record TriageResult(bool Urgent, string Title, string Summary, int ReRemindMinutes = 15, List<NotificationButtonOption>? Actions = null);

/// <summary>
/// 自主規劃代理（Planner）：負責多步驟任務拆解、委派 <see cref="WorkerAgent"/> 執行、
/// 彙整最終回覆並判定任務是否結案（[TASK_STATUS]），以及新進郵件的急迫性分診。
/// </summary>
public sealed class PlannerAgent
{
    private readonly SqliteStore _store;
    private readonly SandboxService _sandbox;
    private readonly GuardService _guard;
    private readonly IModelGateway _model;
    private readonly ContextSessionManager _sessions;
    private readonly StyleService _styles;
    private readonly TtsService _tts;
    private readonly SkillRegistry _skillRegistry;
    private readonly WorkerAgent _worker;
    private readonly WorkflowCacheService _workflowCache;
    private readonly MemoryReflectionAgent _memoryReflection;
    private readonly UiLocalizer _localizer;
    private readonly AppSettingsService _settings;
    private readonly ActivityMonitorService _activity;
    private readonly ILogger<PlannerAgent> _logger;

    public PlannerAgent(
        SqliteStore store,
        SandboxService sandbox,
        GuardService guard,
        IModelGateway model,
        ContextSessionManager sessions,
        StyleService styles,
        TtsService tts,
        SkillRegistry skillRegistry,
        WorkerAgent worker,
        WorkflowCacheService workflowCache,
        MemoryReflectionAgent memoryReflection,
        UiLocalizer localizer,
        AppSettingsService settings,
        ActivityMonitorService activity,
        ILogger<PlannerAgent> logger)
    {
        _store = store;
        _sandbox = sandbox;
        _guard = guard;
        _model = model;
        _sessions = sessions;
        _styles = styles;
        _tts = tts;
        _skillRegistry = skillRegistry;
        _worker = worker;
        _workflowCache = workflowCache;
        _memoryReflection = memoryReflection;
        _localizer = localizer;
        _settings = settings;
        _activity = activity;
        _logger = logger;
    }

    private string GetReplyLanguage()
        => string.IsNullOrWhiteSpace(_settings.Current.Llm.ReplyLanguage)
            ? "繁體中文"
            : _settings.Current.Llm.ReplyLanguage.Trim();

    /// <summary>
    /// 針對複雜或多步驟任務進行規劃與執行，並在完成後透過 <paramref name="onTaskCompleted"/> 回報是否已結案。
    /// </summary>
    public async Task ExecuteGoalAsync(
        string conversationId,
        string taskId,
        string goal,
        string userPrompt,
        string? targetMailId,
        Func<string, string, Task<bool>> confirmAsync,
        Func<string, Task> onStatusAsync,
        Func<string, List<MailSourceRef>, Task> onReplyAsync,
        Action<string, string>? onStageChanged = null,
        ModelTier tier = ModelTier.Complex,
        CancellationToken ct = default,
        Action<string>? onDirectTtsText = null,
        Action<IReadOnlyList<string>>? onChoices = null,
        Action<bool>? onTaskCompleted = null)
    {
        var plannerSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Planner", ct: ct);

        var customList = _skillRegistry.GetCustom().Where(s => s.IsEnabled).ToList();
        var customSkillsPrompt = PlannerPrompts.FormatCustomSkillsPrompt(customList);

        var recent = await _store.GetChatMessagesAsync(conversationId, 80, ct);
        var historyMessages = MainAgent.BuildHistoryModelMessages(recent, userPrompt, maxMessages: 10, perMessageMaxChars: 2500, totalMaxChars: 12000);

        var recalledFacts = await _store.RecallRelevantFactsAsync(conversationId, userPrompt, 5, ct);
        var factsContext = MainAgentPrompts.FormatFactsContext(recalledFacts);

        var prefFacts = await _store.RecallRelevantUserPrefFactsAsync(conversationId, $"{goal} {userPrompt}", 5, ct);
        var prefContext = MainAgentPrompts.FormatPreferencesContext(prefFacts);

        var enabledBuiltInBlock = string.Join("\n", _skillRegistry.GetEnabledPlannerSpecs()
            .Select((s, idx) => $"{idx + 1}. {s.Spec}"));

        var replyLang = GetReplyLanguage();
        var plannerSysPrompt = PlannerPrompts.BuildPlannerSystemPrompt(enabledBuiltInBlock, replyLang);

        var nowLocal = DateTimeOffset.Now;
        var activeWindowContext = ActivityMonitorService.FormatActiveWindowState(_activity.GetLastExternalTarget());
        var plannerPrompt = PlannerPrompts.BuildPlannerUserPrompt(
            nowLocal,
            _sandbox.RootPath,
            targetMailId,
            prefContext,
            factsContext,
            customSkillsPrompt,
            goal,
            userPrompt,
            activeWindowContext);

        var sources = new List<MailSourceRef>();
        var observations = new List<string>();
        var completedSteps = new List<string>();
        var plannedSteps = new List<PlannedStep>();
        string? statusNote = null;
        bool usedPredictedBranch = false;

        // CPU-style 2-bit Branch Predictor: try matching a Hot workflow before burning a Planner LLM call
        var (predictedRec, predictedSteps) = await _workflowCache.TryPredictAsync(conversationId, goal, userPrompt, ct);
        if (predictedRec is not null && predictedSteps.Count > 0)
        {
            var predSteps = predictedSteps.Select(s => new PlannedStep(s.Skill, s.Args)).ToList();
            onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerPreparing", "Worker preparing {0} step(s)...", predSteps.Count));
            var workerSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Worker", forceNewSession: true, ct: ct);
            var stepLimit = Math.Min(predSteps.Count, _guard.MaxWorkerStepsPerTask);

            for (int i = 0; i < stepLimit; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = predSteps[i];
                var skillName = step.Skill.Trim();
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerStep", "Worker running {0} ({1}/{2})...", skillName, i + 1, stepLimit));
                await _worker.RunStepAsync(
                    conversationId, userPrompt, skillName, step.Args, targetMailId,
                    confirmAsync, onStatusAsync, sources, observations, completedSteps, ct, taskId);
            }

            bool predSucceeded = completedSteps.Count == stepLimit &&
                !observations.Any(o => o.Contains("failed", StringComparison.OrdinalIgnoreCase) || o.Contains("denied", StringComparison.OrdinalIgnoreCase));

            long estSaved = Math.Max(400, (plannerSysPrompt.Length + plannerPrompt.Length) / 3);
            await _workflowCache.RecordPredictionOutcomeAsync(predictedRec, predSucceeded, estSaved, ct);

            if (predSucceeded)
            {
                usedPredictedBranch = true;
                plannedSteps = predSteps;
                await _sessions.CompleteTaskSessionAsync(
                    workerSession,
                    new SessionHandoff(goal, completedSteps, new List<string>(), new List<string>(), sources.Select(s => s.MailId).ToList()),
                    ct);
            }
            else
            {
                // Misprediction rollback: clear failed observations and fall back to full Planner
                sources.Clear();
                observations.Clear();
                completedSteps.Clear();
            }
        }

        if (!usedPredictedBranch)
        {
            var planMessages = new List<ModelMessage>(2 + historyMessages.Count)
            {
                new("system", plannerSysPrompt)
            };
            planMessages.AddRange(historyMessages);
            planMessages.Add(new("user", plannerPrompt));

            var planResp = await _model.CompleteAsync(planMessages, tier, ct);

            await _sessions.RecordTokensAsync(plannerSession, planResp.EstimatedTokens, ct: ct);

            (plannedSteps, statusNote) = ParsePlanWithStatus(planResp.Content);

            if (plannedSteps.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(statusNote))
                    await onStatusAsync(statusNote);
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerPreparing", "Worker preparing {0} step(s)...", plannedSteps.Count));
                var workerSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Worker", forceNewSession: true, ct: ct);
                var stepLimit = Math.Min(plannedSteps.Count, _guard.MaxWorkerStepsPerTask);

                for (int i = 0; i < stepLimit; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = plannedSteps[i];
                    var skillName = step.Skill.Trim();
                    onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerStep", "Worker running {0} ({1}/{2})...", skillName, i + 1, stepLimit));

                    await _worker.RunStepAsync(
                        conversationId, userPrompt, skillName, step.Args, targetMailId,
                        confirmAsync, onStatusAsync, sources, observations, completedSteps, ct, taskId);
                }

                // Automatic 1-round self-repair if a tool/script execution failed (and wasn't declined/denied by user or guard)
                bool hasExecError = observations.Any(o =>
                    (o.Contains("Execution failed", StringComparison.OrdinalIgnoreCase) ||
                     o.Contains(" failed:", StringComparison.OrdinalIgnoreCase)) &&
                    !o.Contains("declined", StringComparison.OrdinalIgnoreCase) &&
                    !o.Contains("denied", StringComparison.OrdinalIgnoreCase));

                if (hasExecError)
                {
                    planMessages.Add(new("assistant", planResp.Content));
                    planMessages.Add(new("user", $"One or more steps failed with the following error(s):\n{string.Join("\n\n", observations)}\n\nOutput a corrected JSON plan (`{{\"status_note\":\"...\",\"steps\":[...]}}`) to fix the error and complete the goal (do NOT repeat steps that already succeeded unless rewriting a failed script)."));

                    var retryPlanResp = await _model.CompleteAsync(planMessages, tier, ct);
                    await _sessions.RecordTokensAsync(plannerSession, retryPlanResp.EstimatedTokens, ct: ct);
                    var (retrySteps, retryNote) = ParsePlanWithStatus(retryPlanResp.Content);
                    if (retrySteps.Count > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(retryNote))
                            await onStatusAsync(retryNote);
                        var retryLimit = Math.Min(retrySteps.Count, _guard.MaxWorkerStepsPerTask);
                        for (int i = 0; i < retryLimit; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var step = retrySteps[i];
                            var skillName = step.Skill.Trim();
                            onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerStep", "Worker running {0} ({1}/{2})...", skillName, i + 1, retryLimit));
                            await _worker.RunStepAsync(
                                conversationId, userPrompt, skillName, step.Args, targetMailId,
                                confirmAsync, onStatusAsync, sources, observations, completedSteps, ct, taskId);
                        }
                    }
                }

                await _sessions.CompleteTaskSessionAsync(
                    workerSession,
                    new SessionHandoff(goal, completedSteps, new List<string>(), new List<string>(), sources.Select(s => s.MailId).ToList()),
                    ct);

                // Autonomous 1-round continuation: if round 1 only performed read/list discovery (or hit stepLimit)
                // and did not fail/decline, let Planner inspect observations and output follow-up action steps (or steps: [] if done).
                bool wasDeclinedOrDenied = observations.Any(o =>
                    o.Contains("declined", StringComparison.OrdinalIgnoreCase) ||
                    o.Contains("denied", StringComparison.OrdinalIgnoreCase));
                bool hadReadOrHitLimit = plannedSteps.Count > stepLimit ||
                    completedSteps.Any(s =>
                        s.EndsWith(":list", StringComparison.OrdinalIgnoreCase) ||
                        s.EndsWith(":read", StringComparison.OrdinalIgnoreCase) ||
                        s.EndsWith(":read_dom", StringComparison.OrdinalIgnoreCase) ||
                        s.EndsWith(":scan", StringComparison.OrdinalIgnoreCase));

                if (!wasDeclinedOrDenied && hadReadOrHitLimit && completedSteps.Count > 0)
                {
                    planMessages.Add(new("assistant", planResp.Content));
                    planMessages.Add(new("user", $"Round 1 observations:\n{string.Join("\n\n", observations)}\n\nIf the goal (`{goal}`) requires follow-up execution steps based on the observations above (e.g. deleting/updating the discovered items — note: `manage_schedule` delete supports comma-separated IDs in `id`), output the remaining JSON steps (`{{\"status_note\":\"...\",\"steps\":[...]}}`). If the goal is already satisfied or requires user confirmation first, output `{{\"status_note\":\"\",\"steps\":[]}}`."));

                    var followResp = await _model.CompleteAsync(planMessages, tier, ct);
                    await _sessions.RecordTokensAsync(plannerSession, followResp.EstimatedTokens, ct: ct);
                    var (followSteps, followNote) = ParsePlanWithStatus(followResp.Content);
                    if (followSteps.Count > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(followNote))
                            await onStatusAsync(followNote);
                        var followLimit = Math.Min(followSteps.Count, _guard.MaxWorkerStepsPerTask);
                        for (int i = 0; i < followLimit; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var step = followSteps[i];
                            var skillName = step.Skill.Trim();
                            onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerStep", "Worker running {0} ({1}/{2})...", skillName, i + 1, followLimit));
                            await _worker.RunStepAsync(
                                conversationId, userPrompt, skillName, step.Args, targetMailId,
                                confirmAsync, onStatusAsync, sources, observations, completedSteps, ct, taskId);
                        }
                    }
                }

                if (plannedSteps.Count >= 2 && completedSteps.Count == stepLimit)
                {
                    await _workflowCache.RecordObservedPlanAsync(
                        conversationId,
                        goal,
                        userPrompt,
                        plannedSteps.Select(s => new CachedWorkflowStep(s.Skill, s.Args)).ToList(),
                        ct,
                        prefFacts);
                }
            }
        }

        var mainSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Main", ct: ct);

        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();
        var finalSysPrompt = PlannerPrompts.BuildFinalReplySystemPrompt(
            activeStylePrompt,
            _tts.IsEnabled,
            _tts.ChatMode,
            includeTaskStatus: true,
            _tts.TargetLang,
            replyLang);

        var finalMessages = new List<ModelMessage>(2 + historyMessages.Count)
        {
            new("system", finalSysPrompt)
        };
        finalMessages.AddRange(historyMessages);
        finalMessages.Add(new("user", PlannerPrompts.BuildFinalReplyUserPrompt(userPrompt, observations)));

        var finalResp = await _model.CompleteAsync(finalMessages, tier, ct);

        WorkerAgent.CleanupTempScreenshots(observations);
        await _sessions.RecordTokensAsync(mainSession, finalResp.EstimatedTokens, ct: ct);
        var (cleanFinal0, finalChoices) = WorkerAgent.ExtractAndStripChoices(finalResp.Content);
        var (cleanFinal1, finalTtsText) = WorkerAgent.ExtractAndStripTtsText(cleanFinal0);
        var (cleanFinal, isTaskDone) = ExtractAndStripTaskStatus(cleanFinal1, finalChoices.Count > 0);
        if (finalChoices.Count > 0)
            onChoices?.Invoke(finalChoices);
        if (!string.IsNullOrWhiteSpace(finalTtsText))
            onDirectTtsText?.Invoke(finalTtsText);
        await onReplyAsync(cleanFinal, sources);
        onTaskCompleted?.Invoke(isTaskDone);
        _memoryReflection.EnqueueDecisionReflection(conversationId, userPrompt, completedSteps, observations, cleanFinal);

        await _sessions.CompleteTaskSessionAsync(
            plannerSession,
            new SessionHandoff(goal, completedSteps, new List<string>(), new List<string>(), sources.Select(s => s.MailId).ToList()),
            ct);
    }

    public async Task<TriageResult> TriageMailEventAsync(MailItem mail, string conversationId, CancellationToken ct)
    {
        var triageSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Triage", forceNewSession: true, ct: ct);

        var prefFacts = await _store.RecallRelevantUserPrefFactsAsync(conversationId, $"{mail.SenderName} {mail.Subject} {mail.Snippet}", 5, ct);
        var prefBlock = PlannerPrompts.FormatMailPreferencesContext(prefFacts);

        var nowLocal = DateTimeOffset.Now;
        var receivedLocal = mail.ReceivedAt.ToLocalTime();
        var resp = await _model.CompleteAsync(new[]
        {
            new ModelMessage("system", PlannerPrompts.BuildMailTriageSystemPrompt(GetReplyLanguage())),
            new ModelMessage("user", PlannerPrompts.BuildMailTriageUserPrompt(
                nowLocal,
                receivedLocal,
                prefBlock,
                mail.Subject,
                mail.SenderName,
                mail.SenderEmail,
                mail.Body))
        }, ct);

        await _sessions.RecordTokensAsync(triageSession, resp.EstimatedTokens, ct: ct);

        var title = await _localizer.TextAsync("DefaultTriageTitle", "Email Requiring Attention", Array.Empty<object?>(), ct);
        var summary = await _localizer.TextAsync(
            "DefaultTriageSummary",
            "From {0} \"{1}\": {2}",
            new object?[] { mail.SenderName, mail.Subject, mail.Snippet },
            ct);
        var urgent = true;
        var reRemindMinutes = 15;
        List<NotificationButtonOption>? actions = null;

        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(resp.Content));
            if (doc.RootElement.TryGetProperty("urgent", out var u)) urgent = u.GetBoolean();
            if (doc.RootElement.TryGetProperty("title", out var t) && !string.IsNullOrWhiteSpace(t.GetString())) title = t.GetString()!;
            if (doc.RootElement.TryGetProperty("summary", out var s) && !string.IsNullOrWhiteSpace(s.GetString())) summary = s.GetString()!;
            if (doc.RootElement.TryGetProperty("re_remind_minutes", out var rm) && rm.TryGetInt32(out var mins))
                reRemindMinutes = mins <= 0 ? 0 : Math.Clamp(mins, 3, 120);
            if (doc.RootElement.TryGetProperty("actions", out var actsEl) && actsEl.ValueKind == JsonValueKind.Array)
            {
                var parsed = new List<NotificationButtonOption>();
                foreach (var a in actsEl.EnumerateArray())
                {
                    var type = a.TryGetProperty("type", out var ty) ? (ty.GetString() ?? "").Trim().ToLowerInvariant() : "";
                    var label = a.TryGetProperty("label", out var lb) ? (lb.GetString() ?? "").Trim() : "";
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    if (type is not ("view_mail" or "draft_reply" or "snooze" or "ask_ai" or "fill_otp" or "copy_otp")) continue;
                    var prompt = a.TryGetProperty("prompt", out var pr) ? pr.GetString()?.Trim() : null;
                    if (type is ("ask_ai" or "fill_otp" or "copy_otp") && string.IsNullOrWhiteSpace(prompt)) continue;
                    var isPrimary = a.TryGetProperty("is_primary", out var ip) && ip.ValueKind == JsonValueKind.True;
                    parsed.Add(new NotificationButtonOption(type, label, prompt, isPrimary));
                    if (parsed.Count >= 4) break;
                }
                if (parsed.Count > 0) actions = parsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse triage JSON: {Raw}", resp.Content);
        }

        await _sessions.CompleteTaskSessionAsync(
            triageSession,
            new SessionHandoff($"Triage {mail.Id}", new List<string> { "AI Triage" }, new List<string>(), new List<string>(), new List<string> { mail.Id }),
            ct);

        return new TriageResult(urgent, title, summary, reRemindMinutes, actions);
    }

    /// <summary>從回覆中抽離 `[TASK_STATUS: done|waiting]` 標記；若模型未輸出，則有選項按鈕時視為等待中。</summary>
    private static (string CleanText, bool IsDone) ExtractAndStripTaskStatus(string raw, bool hasChoices)
    {
        var text = (raw ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:^|\r?\n)\s*\[TASK_STATUS:\s*(done|waiting)\s*\]\s*(?=$|\r?\n)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return (text, !hasChoices);

        var isDone = m.Groups[1].Value.Equals("done", StringComparison.OrdinalIgnoreCase);
        var clean = (text[..m.Index] + text[(m.Index + m.Length)..]).Trim();
        return (clean, isDone);
    }


    private sealed record PlannedStep(string Skill, JsonElement Args);

    /// <summary>解析 Planner 回傳的步驟清單與狀態說明。</summary>
    private (List<PlannedStep> Steps, string? StatusNote) ParsePlanWithStatus(string raw)
    {
        var list = new List<PlannedStep>();
        string? statusNote = null;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            if (doc.RootElement.TryGetProperty("status_note", out var sn))
                statusNote = sn.GetString()?.Trim();
            if (doc.RootElement.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in stepsEl.EnumerateArray())
                {
                    var skill = s.TryGetProperty("skill", out var sk) ? sk.GetString() : null;
                    if (string.IsNullOrWhiteSpace(skill)) continue;
                    var args = s.TryGetProperty("args", out var a) ? a.Clone() : default;
                    list.Add(new PlannedStep(skill, args));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Planner JSON: {Raw}", raw);
        }
        return (list, string.IsNullOrWhiteSpace(statusNote) ? null : statusNote);
    }

    /// <summary>擷取文字中最外層 `{...}` JSON 區段，容錯模型附加的說明文字。</summary>
    private static string ExtractJson(string raw)
    {
        var s = raw.Trim();
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        return (first >= 0 && last > first) ? s[first..(last + 1)] : s;
    }
}
