using System.Text.Json;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Agent;

/// <summary>郵件急迫性分診結果。</summary>
public sealed record TriageResult(bool Urgent, string Title, string Summary, int ReRemindMinutes = 15, List<NotificationButtonOption>? Actions = null);

/// <summary>Planner 完成多輪規劃與 Worker 執行後回報給 MainAgent 的純資料結果（Planner 本身不直接對使用者發言）。</summary>
public sealed record PlannerReport(
    string Goal,
    List<string> CompletedSteps,
    List<string> Observations,
    List<MailSourceRef> Sources,
    int RoundsExecuted,
    bool ReachedRoundLimit,
    bool WasDeclinedOrDenied);

/// <summary>
/// 內部任務規劃代理（Planner）：
/// - 僅負責將 <see cref="MainAgent"/> 交付的目標拆解為工具步驟並委派 <see cref="WorkerAgent"/> 執行。
/// - 支援可設定上限的多輪 Planner ↔ Worker 迴圈（<see cref="GuardService.MaxPlannerRounds"/>），
///   並在每輪間同步新抵達的背景通知（如驗證碼信件），直到達成目標、無法繼續或觸及輪數上限。
/// - 絕不直接與使用者對話；完成後僅回傳 <see cref="PlannerReport"/> 交由 <see cref="MainAgent"/> 統一對外回覆。
/// </summary>
public sealed class PlannerAgent
{
    private readonly SqliteStore _store;
    private readonly SandboxService _sandbox;
    private readonly GuardService _guard;
    private readonly IModelGateway _model;
    private readonly ContextSessionManager _sessions;
    private readonly SkillRegistry _skillRegistry;
    private readonly WorkerAgent _worker;
    private readonly WorkflowCacheService _workflowCache;
    private readonly UiLocalizer _localizer;
    private readonly AppSettingsService _settings;
    private readonly ActivityMonitorService _activity;
    private readonly AgentBlackboard _blackboard;
    private readonly ILogger<PlannerAgent> _logger;

    public PlannerAgent(
        SqliteStore store,
        SandboxService sandbox,
        GuardService guard,
        IModelGateway model,
        ContextSessionManager sessions,
        SkillRegistry skillRegistry,
        WorkerAgent worker,
        WorkflowCacheService workflowCache,
        UiLocalizer localizer,
        AppSettingsService settings,
        ActivityMonitorService activity,
        AgentBlackboard blackboard,
        ILogger<PlannerAgent> logger)
    {
        _store = store;
        _sandbox = sandbox;
        _guard = guard;
        _model = model;
        _sessions = sessions;
        _skillRegistry = skillRegistry;
        _worker = worker;
        _workflowCache = workflowCache;
        _localizer = localizer;
        _settings = settings;
        _activity = activity;
        _blackboard = blackboard;
        _logger = logger;
    }

    private string GetReplyLanguage()
        => string.IsNullOrWhiteSpace(_settings.Current.Llm.ReplyLanguage)
            ? "繁體中文"
            : _settings.Current.Llm.ReplyLanguage.Trim();

    /// <summary>
    /// 執行多輪 Planner ↔ Worker 任務迴圈，完成或觸及上限後將結構化 <see cref="PlannerReport"/> 回報給 MainAgent。
    /// </summary>
    public async Task<PlannerReport> ExecuteGoalAsync(
        string conversationId,
        string taskId,
        string goal,
        string userPrompt,
        string? targetMailId,
        Func<string, string, Task<bool>> confirmAsync,
        Func<string, Task> onStatusAsync,
        Action<string, string>? onStageChanged = null,
        ModelTier tier = ModelTier.Complex,
        CancellationToken ct = default)
    {
        var plannerSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Planner", ct: ct);
        var lastNotifCheckUtc = DateTimeOffset.UtcNow;
        var seenNotifIds = new HashSet<string>(StringComparer.Ordinal);

        var customList = _skillRegistry.GetCustom().Where(s => s.IsEnabled).ToList();
        var customSkillsPrompt = PlannerPrompts.FormatCustomSkillsPrompt(customList);

        var recent = await _store.GetChatMessagesAsync(conversationId, 80, ct);
        var historyMessages = MainAgent.BuildHistoryModelMessages(recent, userPrompt, maxMessages: 20, perMessageMaxChars: 2500, totalMaxChars: 18000);

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
        var bbSnap = _blackboard.GetSnapshot(maxRecentResults: 6, maxAge: TimeSpan.FromMinutes(10));
        long lastBbVersion = bbSnap.Version;
        var blackboardContext = AgentBlackboard.FormatForPrompt(bbSnap);
        var plannerPrompt = PlannerPrompts.BuildPlannerUserPrompt(
            nowLocal,
            _sandbox.RootPath,
            targetMailId,
            prefContext,
            factsContext,
            customSkillsPrompt,
            goal,
            userPrompt,
            activeWindowContext,
            _sandbox.GetTrustedPaths(),
            blackboardContext);

        var sources = new List<MailSourceRef>();
        var observations = new List<string>();
        var completedSteps = new List<string>();
        bool usedPredictedBranch = false;
        int roundsExecuted = 0;
        bool reachedRoundLimit = false;
        bool wasDeclinedOrDenied = false;

        // CPU-style 2-bit Branch Predictor: try matching a Hot workflow before burning a Planner LLM call
        var (predictedRec, predictedSteps) = await _workflowCache.TryPredictAsync(conversationId, goal, userPrompt, ct);
        if (predictedRec is not null && predictedSteps.Count > 0)
        {
            var predSteps = predictedSteps.Select(s => new PlannedStep(s.Skill, s.Args)).ToList();
            onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerPreparing", "Worker preparing {0} step(s)...", predSteps.Count));
            var workerSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Worker", forceNewSession: true, ct: ct);
            var stepLimit = Math.Min(predSteps.Count, _guard.MaxWorkerStepsPerTask);

            bool allPredOk = true;
            for (int i = 0; i < stepLimit; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = predSteps[i];
                var skillName = step.Skill.Trim();
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerStep", "Worker running {0} ({1}/{2})...", skillName, i + 1, stepLimit));
                var outcome = await _worker.RunStepAsync(
                    conversationId, userPrompt, skillName, step.Args, targetMailId,
                    confirmAsync, onStatusAsync, sources, observations, completedSteps, ct, taskId);
                if (outcome != StepOutcome.Ok)
                {
                    allPredOk = false;
                    break;
                }
            }

            bool predSucceeded = allPredOk && stepLimit > 0;

            long estSaved = predSucceeded ? Math.Max(400, (plannerSysPrompt.Length + plannerPrompt.Length) / 3) : 0;
            await _workflowCache.RecordPredictionOutcomeAsync(predictedRec, predSucceeded, estSaved, ct);
            await _store.InsertPredictionLogAsync("multi_step", predictedRec.WorkflowId, userPrompt, string.Join("->", predSteps.Select(s => s.Skill)), predSucceeded, estSaved, ct);

            if (predSucceeded)
            {
                usedPredictedBranch = true;
                roundsExecuted = 1;
                await _sessions.CompleteTaskSessionAsync(
                    workerSession,
                    new SessionHandoff(goal, completedSteps, new List<string>(), new List<string>(), sources.Select(s => s.MailId).ToList()),
                    ct);
            }
            else
            {
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

            var maxRounds = _guard.MaxPlannerRounds;
            var workerSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Worker", forceNewSession: true, ct: ct);
            List<PlannedStep>? firstRoundSteps = null;
            bool firstRoundAllOk = true;

            for (int round = 1; round <= maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                roundsExecuted = round;

                var planResp = await _model.CompleteAsync(
                    planMessages,
                    new LlmCallOptions("PlannerAgent.Plan", tier, CachePolicy.Conversation, LlmPriority.Interactive),
                    ct);
                await _sessions.RecordTokensAsync(plannerSession, planResp.EstimatedTokens, ct: ct);

                var (plannedSteps, statusNote, done) = ParsePlanWithStatus(planResp.Content);
                if (plannedSteps.Count == 0 || done)
                    break;

                if (!string.IsNullOrWhiteSpace(statusNote))
                    await onStatusAsync(statusNote);

                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerPreparing", "Worker preparing {0} step(s)...", plannedSteps.Count));
                _blackboard.UpdateProgress(taskId, $"round {round}/{maxRounds}: {string.Join(", ", plannedSteps.Take(3).Select(s => s.Skill))}");
                var stepLimit = Math.Min(plannedSteps.Count, _guard.MaxWorkerStepsPerTask);
                int obsBefore = observations.Count;
                bool roundHadDenied = false;
                bool roundHadFailure = false;

                for (int i = 0; i < stepLimit; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = plannedSteps[i];
                    var skillName = step.Skill.Trim();
                    onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerStep", "Worker running {0} ({1}/{2})...", skillName, i + 1, stepLimit));

                    var outcome = await _worker.RunStepAsync(
                        conversationId, userPrompt, skillName, step.Args, targetMailId,
                        confirmAsync, onStatusAsync, sources, observations, completedSteps, ct, taskId);
                    if (outcome == StepOutcome.Denied) roundHadDenied = true;
                    else if (outcome == StepOutcome.Failed) roundHadFailure = true;
                }

                if (round == 1)
                {
                    firstRoundSteps = plannedSteps;
                    firstRoundAllOk = !roundHadDenied && !roundHadFailure;
                }

                var roundObs = observations.Skip(obsBefore).ToList();

                // Sync any concurrent notifications (e.g. arriving OTP mail / reminder) & blackboard results between Planner <-> Worker rounds
                var latestMsgs = await _store.GetChatMessagesAsync(conversationId, 10, ct);
                foreach (var n in latestMsgs.Where(m =>
                             m.Role == "notification" &&
                             m.CreatedAt >= lastNotifCheckUtc &&
                             !string.IsNullOrWhiteSpace(m.Content) &&
                             seenNotifIds.Add(m.Id)))
                {
                    var note = $"[Concurrent notification arrived during round {round} ({n.CreatedAt.ToLocalTime():HH:mm:ss})]: {n.Content}";
                    observations.Add(note);
                    roundObs.Add(note);
                }
                lastNotifCheckUtc = DateTimeOffset.UtcNow;

                var concurrentBb = _blackboard.GetCompletedSince(lastBbVersion, excludeTaskId: taskId);
                foreach (var r in concurrentBb)
                {
                    if (r.Version > lastBbVersion) lastBbVersion = r.Version;
                    if (!r.IsUserInitiated && !r.IsSignificant) continue;
                    var note = $"[Concurrent background result ({r.Kind}) at {r.CompletedAt.ToLocalTime():HH:mm:ss}]: {r.Goal} -> {r.Summary}";
                    if (!observations.Any(o => o.Contains(r.Summary, StringComparison.Ordinal)))
                    {
                        observations.Add(note);
                        roundObs.Add(note);
                    }
                }

                wasDeclinedOrDenied = roundHadDenied;
                if (wasDeclinedOrDenied)
                    break;

                if (round >= maxRounds)
                {
                    reachedRoundLimit = true;
                    break;
                }

                // Feed Worker's round report back to Planner for next-round decision (self-repair, follow-up, or done)
                planMessages.Add(new("assistant", planResp.Content));
                planMessages.Add(new("user", PlannerPrompts.BuildPlannerRoundFeedbackPrompt(goal, round, maxRounds, roundObs)));
            }

            await _sessions.CompleteTaskSessionAsync(
                workerSession,
                new SessionHandoff(goal, completedSteps, new List<string>(), new List<string>(), sources.Select(s => s.MailId).ToList()),
                ct);

            // Cache clean single-round multi-step workflows for 2-bit branch prediction (only when structured outcome == Ok)
            if (roundsExecuted <= 2 && firstRoundSteps is { Count: >= 2 } && firstRoundAllOk && !wasDeclinedOrDenied)
            {
                await _workflowCache.RecordObservedPlanAsync(
                    conversationId,
                    goal,
                    userPrompt,
                    firstRoundSteps.Select(s => new CachedWorkflowStep(s.Skill, s.Args)).ToList(),
                    ct,
                    prefFacts);
            }
        }

        await _sessions.CompleteTaskSessionAsync(
            plannerSession,
            new SessionHandoff(goal, completedSteps, new List<string>(), new List<string>(), sources.Select(s => s.MailId).ToList()),
            ct);

        return new PlannerReport(
            Goal: goal,
            CompletedSteps: completedSteps,
            Observations: observations,
            Sources: sources,
            RoundsExecuted: roundsExecuted,
            ReachedRoundLimit: reachedRoundLimit,
            WasDeclinedOrDenied: wasDeclinedOrDenied);
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
        }, new LlmCallOptions("MailTriage", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), ct);

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

    private sealed record PlannedStep(string Skill, JsonElement Args);

    /// <summary>解析 Planner 回傳的步驟清單、狀態說明與是否完成旗標。</summary>
    private (List<PlannedStep> Steps, string? StatusNote, bool Done) ParsePlanWithStatus(string raw)
    {
        var list = new List<PlannedStep>();
        string? statusNote = null;
        bool done = false;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            if (doc.RootElement.TryGetProperty("status_note", out var sn))
                statusNote = sn.GetString()?.Trim();
            if (doc.RootElement.TryGetProperty("done", out var dn) && dn.ValueKind == JsonValueKind.True)
                done = true;
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
        return (list, statusNote, done);
    }

    private static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
