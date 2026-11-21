using System.Text.Json;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// 精簡的人機介面協調層。
/// 負責接收使用者訊息並進行路由決策：直接文字回覆、單步技能派發給 <see cref="WorkerAgent"/>，
/// 或將複雜／多步驟目標交給 <see cref="PlannerAgent"/> 執行，並維持活躍任務的接續與關閉。
/// </summary>
public sealed class MainAgent
{
    private readonly SqliteStore _store;
    private readonly IModelGateway _model;
    private readonly ContextSessionManager _sessions;
    private readonly PlannerAgent _planner;
    private readonly WorkerAgent _worker;
    private readonly StyleService _styles;
    private readonly TtsService _tts;
    private readonly SkillRegistry _skillRegistry;
    private readonly UiLocalizer _localizer;
    private readonly AppSettingsService _settings;
    private readonly ActivityMonitorService _activity;
    private readonly WorkflowCacheService _workflowCache;
    private readonly MemoryReflectionAgent _memoryReflection;
    private readonly StateMachineAgent _stateMachine;
    private readonly SandboxService _sandbox;
    private readonly ILogger<MainAgent> _logger;

    public MainAgent(
        SqliteStore store,
        IModelGateway model,
        ContextSessionManager sessions,
        PlannerAgent planner,
        WorkerAgent worker,
        StyleService styles,
        TtsService tts,
        SkillRegistry skillRegistry,
        UiLocalizer localizer,
        AppSettingsService settings,
        ActivityMonitorService activity,
        WorkflowCacheService workflowCache,
        MemoryReflectionAgent memoryReflection,
        StateMachineAgent stateMachine,
        SandboxService sandbox,
        ILogger<MainAgent> logger)
    {
        _store = store;
        _model = model;
        _sessions = sessions;
        _planner = planner;
        _worker = worker;
        _styles = styles;
        _tts = tts;
        _skillRegistry = skillRegistry;
        _localizer = localizer;
        _settings = settings;
        _activity = activity;
        _workflowCache = workflowCache;
        _memoryReflection = memoryReflection;
        _stateMachine = stateMachine;
        _sandbox = sandbox;
        _logger = logger;
    }

    private string GetReplyLanguage()
        => string.IsNullOrWhiteSpace(_settings.Current.Llm.ReplyLanguage)
            ? "繁體中文"
            : _settings.Current.Llm.ReplyLanguage.Trim();

    /// <summary>
    /// 處理一輪使用者對話：載入上下文與記憶、執行路由模型決策，並分派至直接回覆、單步 Worker 或多步 Planner。
    /// </summary>
    public async Task HandleMessageAsync(
        string conversationId,
        string taskId,
        string userPrompt,
        string? targetMailId,
        Func<string, string, Task<bool>> confirmAsync,
        Func<string, Task> onStatusAsync,
        Func<string, List<MailSourceRef>, Task> onReplyAsync,
        Action<string, string>? onStageChanged = null,
        CancellationToken ct = default,
        Action<string>? onDirectTtsText = null,
        Action<IReadOnlyList<string>>? onChoices = null,
        Action<Func<CancellationToken, Func<string, List<MailSourceRef>, Task>, Action<string>?, Action<IReadOnlyList<string>>?, Task>>? onDispatchBackground = null,
        IReadOnlyList<ActivePlannerTaskInfo>? activePlannerTasks = null,
        Func<string, string?, string, string>? resolveLogicalTaskId = null,
        Action<string, bool>? onPlannerTaskFinished = null,
        Action<string>? onSingleSkillFinished = null,
        Action<string>? onClosePlannerTask = null,
        string? preferredLogicalTaskId = null)
    {
        onStageChanged?.Invoke("main", _localizer.TextCached("StageMainRouting", "MainAgent analyzing intent and routing..."));
        var mainSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Main", ct: ct);

        var recent = await _store.GetChatMessagesAsync(conversationId, 80, ct);
        var historyMessages = BuildHistoryModelMessages(recent, userPrompt, maxMessages: 32, perMessageMaxChars: 3000, totalMaxChars: 36000);

        var recalledFacts = await _store.RecallRelevantFactsAsync(conversationId, userPrompt, 5, ct);
        var factsContext = MainAgentPrompts.FormatFactsContext(recalledFacts);

        var prefFacts = await _store.RecallRelevantUserPrefFactsAsync(conversationId, userPrompt, 5, ct);
        var prefContext = MainAgentPrompts.FormatPreferencesContext(prefFacts);

        var handoffContext = string.IsNullOrWhiteSpace(mainSession.HandoffJson) || mainSession.HandoffJson == "{}"
            ? ""
            : $"\nPrevious stage handoff: {mainSession.HandoffJson}";

        var enabledSpecs = _skillRegistry.GetEnabledMainAgentSpecs()
            .Select((s, idx) => $"{idx + 1}. {s.Spec.TrimEnd()}");
        var skillsBlock = enabledSpecs.Any()
            ? string.Join("\n\n", enabledSpecs)
            : "(No built-in skills available)";

        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();
        var ttsEnabled = _tts.IsEnabled && _tts.ChatMode != "notify_only";
        var effectiveSystemPrompt = MainAgentPrompts.BuildSystemPrompt(
            skillsBlock,
            _tts.ChatMode == "full",
            activeStylePrompt,
            _tts.TargetLang,
            GetReplyLanguage(),
            ttsEnabled);

        var activeTasksContext = MainAgentPrompts.FormatActiveTasksContext(activePlannerTasks);
        var activeWindowContext = ActivityMonitorService.FormatActiveWindowState(_activity.GetLastExternalTarget());

        var dynamicUserPrompt = MainAgentPrompts.BuildUserContextPrompt(
            DateTimeOffset.Now,
            targetMailId,
            handoffContext,
            prefContext,
            factsContext,
            userPrompt,
            activeTasksContext,
            activeWindowContext,
            _sandbox.RootPath,
            _sandbox.GetTrustedPaths());

        // Fast path: 2-bit branch prediction for repeated read-only single-step intents (skips MainAgent routing LLM).
        var turnStartedUtc = DateTimeOffset.UtcNow;
        var basePromptMessages = new List<ModelMessage>(2 + historyMessages.Count)
        {
            new("system", effectiveSystemPrompt)
        };
        basePromptMessages.AddRange(historyMessages);
        basePromptMessages.Add(new("user", dynamicUserPrompt));

        if (string.IsNullOrWhiteSpace(targetMailId) &&
            string.IsNullOrWhiteSpace(preferredLogicalTaskId) &&
            (activePlannerTasks is null || activePlannerTasks.Count == 0) &&
            !AttachmentExtractor.AttachmentPattern.IsMatch(userPrompt))
        {
            var (predWf, predStep) = await _workflowCache.TryPredictSingleStepAsync(conversationId, userPrompt, ct);
            if (predWf is not null && predStep is not null)
            {
                var predSkill = predStep.Skill;
                var predArgs = predStep.Args;
                var predAck = await _localizer.TextAsync("DefaultDispatchAck", "Working on it...", Array.Empty<object?>(), ct);
                var syntheticRouteJson = JsonSerializer.Serialize(new { action = "run_skill", skill = predSkill, ack = predAck });

                if (onDispatchBackground is not null)
                {
                    onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", predSkill, "Predicted"));
                    await onReplyAsync(predAck, new List<MailSourceRef>());

                    onDispatchBackground(async (bgCt, bgReplyAsync, bgDirectTtsText, bgChoices) =>
                    {
                        var report = await _worker.ExecuteSingleSkillRawAsync(
                            conversationId, userPrompt, predSkill, predArgs, targetMailId, confirmAsync,
                            async s => { onStageChanged?.Invoke("worker", s); await onStatusAsync(s); },
                            bgCt, taskId);
                        if (report.Handled)
                        {
                            await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: true, estimatedTokensSaved: 350, bgCt);
                            onSingleSkillFinished?.Invoke(taskId);
                            onStageChanged?.Invoke("worker_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", predSkill));
                            await SynthesizeFinalReplyAsync(
                                conversationId, mainSession, basePromptMessages, syntheticRouteJson,
                                "Worker", userPrompt, report.CompletedSteps, report.Observations, report.Sources,
                                turnStartedUtc, includeTaskStatus: false, ModelTier.Simple,
                                bgReplyAsync, bgDirectTtsText, bgChoices, onTaskCompleted: null, extraNote: null, bgCt);
                            return;
                        }

                        await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: false, estimatedTokensSaved: 0, bgCt);
                        onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", "Simple"));
                        var plannerTaskId = resolveLogicalTaskId is not null
                            ? resolveLogicalTaskId("call_planner", null, userPrompt)
                            : taskId;
                        var planReport = await _planner.ExecuteGoalAsync(
                            conversationId, plannerTaskId, userPrompt, userPrompt, targetMailId, confirmAsync,
                            async s => { onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                            onStageChanged, ModelTier.Simple, bgCt);
                        bool isDone = true;
                        await SynthesizeFinalReplyAsync(
                            conversationId, mainSession, basePromptMessages, syntheticRouteJson,
                            "Planner", userPrompt, planReport.CompletedSteps, planReport.Observations, planReport.Sources,
                            turnStartedUtc, includeTaskStatus: true, ModelTier.Simple,
                            bgReplyAsync, bgDirectTtsText, bgChoices, done => isDone = done,
                            planReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({planReport.RoundsExecuted})." : null,
                            bgCt);
                        onPlannerTaskFinished?.Invoke(plannerTaskId, isDone);
                    });
                    return;
                }

                await onStatusAsync(predAck);
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", predSkill, "Predicted"));
                var syncReport = await _worker.ExecuteSingleSkillRawAsync(
                    conversationId, userPrompt, predSkill, predArgs, targetMailId, confirmAsync,
                    s => onStatusAsync(s), ct, taskId);
                if (syncReport.Handled)
                {
                    await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: true, estimatedTokensSaved: 350, ct);
                    onSingleSkillFinished?.Invoke(taskId);
                    onStageChanged?.Invoke("main_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", predSkill));
                    await SynthesizeFinalReplyAsync(
                        conversationId, mainSession, basePromptMessages, syntheticRouteJson,
                        "Worker", userPrompt, syncReport.CompletedSteps, syncReport.Observations, syncReport.Sources,
                        turnStartedUtc, includeTaskStatus: false, ModelTier.Simple,
                        onReplyAsync, onDirectTtsText, onChoices, onTaskCompleted: null, extraNote: null, ct);
                    return;
                }

                await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: false, estimatedTokensSaved: 0, ct);
            }
        }

        var routeMessages = basePromptMessages;
        var routeResp = await _model.CompleteAsync(routeMessages, ModelTier.Simple, ct);

        await _sessions.RecordTokensAsync(mainSession, routeResp.EstimatedTokens, ct: ct);

        var (action, targetTaskId, closeTaskId, tier, memoryWorthy, directReply, ack, ttsText, choices, skill, args, goal) = ParseRoute(routeResp.Content, userPrompt);
        var effectiveGoal = string.IsNullOrWhiteSpace(goal) ? userPrompt : goal;

        if (!string.IsNullOrWhiteSpace(preferredLogicalTaskId))
        {
            if (action == "reply" && string.IsNullOrWhiteSpace(closeTaskId))
                closeTaskId = preferredLogicalTaskId;
            else if (action != "reply" && string.IsNullOrWhiteSpace(targetTaskId))
                targetTaskId = preferredLogicalTaskId;
        }

        // Guard: if the model matched an active Planner task, force Planner continuation so task approvals persist.
        if (action == "run_skill" && !string.IsNullOrWhiteSpace(targetTaskId))
            action = "call_planner";

        if (action == "reply" && !string.IsNullOrWhiteSpace(directReply) && string.IsNullOrWhiteSpace(targetMailId))
        {
            if (!string.IsNullOrWhiteSpace(closeTaskId))
                onClosePlannerTask?.Invoke(closeTaskId);

            onStageChanged?.Invoke("main_done", _localizer.TextCached("StageMainDone", "MainAgent replied directly"));
            if (choices.Count > 0)
                onChoices?.Invoke(choices);
            if (!string.IsNullOrWhiteSpace(ttsText))
                onDirectTtsText?.Invoke(ttsText);
            await onReplyAsync(directReply, new List<MailSourceRef>());
            if (memoryWorthy)
                _memoryReflection.EnqueueChatReflection(conversationId, userPrompt, directReply);
            return;
        }

        if (action == "run_skill" && !string.IsNullOrWhiteSpace(skill))
        {
            var dispatchNote = !string.IsNullOrWhiteSpace(ack)
                ? ack
                : await _localizer.TextAsync("DefaultDispatchAck", "Working on it...", Array.Empty<object?>(), ct);
            if (onDispatchBackground is not null)
            {
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", skill, tier.ToString()));
                if (!string.IsNullOrWhiteSpace(ttsText))
                    onDirectTtsText?.Invoke(ttsText);
                await onReplyAsync(dispatchNote, new List<MailSourceRef>());

                onDispatchBackground(async (bgCt, bgReplyAsync, bgDirectTtsText, bgChoices) =>
                {
                    var report = await _worker.ExecuteSingleSkillRawAsync(
                        conversationId, userPrompt, skill, args, targetMailId, confirmAsync,
                        async s => { onStageChanged?.Invoke("worker", s); await onStatusAsync(s); },
                        bgCt, taskId);
                    if (report.Handled)
                    {
                        await _workflowCache.RecordObservedSingleSkillAsync(conversationId, userPrompt, skill, args, bgCt);
                        await _stateMachine.ObserveUserSkillExecutionAsync(_activity.GetLastExternalTarget(), skill, args, bgCt);
                        onSingleSkillFinished?.Invoke(taskId);
                        onStageChanged?.Invoke("worker_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", skill));
                        await SynthesizeFinalReplyAsync(
                            conversationId, mainSession, routeMessages, routeResp.Content,
                            "Worker", userPrompt, report.CompletedSteps, report.Observations, report.Sources,
                            turnStartedUtc, includeTaskStatus: false, tier,
                            bgReplyAsync, bgDirectTtsText, bgChoices, onTaskCompleted: null, extraNote: null, bgCt);
                        return;
                    }

                    onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", tier.ToString()));
                    var plannerTaskId = resolveLogicalTaskId is not null
                        ? resolveLogicalTaskId("call_planner", targetTaskId, effectiveGoal)
                        : taskId;
                    var planReport = await _planner.ExecuteGoalAsync(
                        conversationId, plannerTaskId, effectiveGoal, userPrompt, targetMailId, confirmAsync,
                        async s => { onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                        onStageChanged, tier, bgCt);
                    bool isDone = true;
                    await SynthesizeFinalReplyAsync(
                        conversationId, mainSession, routeMessages, routeResp.Content,
                        "Planner", userPrompt, planReport.CompletedSteps, planReport.Observations, planReport.Sources,
                        turnStartedUtc, includeTaskStatus: true, tier,
                        bgReplyAsync, bgDirectTtsText, bgChoices, done => isDone = done,
                        planReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({planReport.RoundsExecuted})." : null,
                        bgCt);
                    onPlannerTaskFinished?.Invoke(plannerTaskId, isDone);
                });
                return;
            }

            await onStatusAsync(dispatchNote);
            SpeakDispatchAck(dispatchNote, ttsText);
            Func<string, Task> workerStatusAsync = s =>
                string.Equals(s, dispatchNote, StringComparison.Ordinal)
                    ? onStatusAsync(s)
                    : onStatusAsync($"{dispatchNote} ({s})");
            onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", skill, tier.ToString()));
            var syncReport = await _worker.ExecuteSingleSkillRawAsync(
                conversationId, userPrompt, skill, args, targetMailId, confirmAsync, workerStatusAsync, ct, taskId);
            if (syncReport.Handled)
            {
                await _workflowCache.RecordObservedSingleSkillAsync(conversationId, userPrompt, skill, args, ct);
                await _stateMachine.ObserveUserSkillExecutionAsync(_activity.GetLastExternalTarget(), skill, args, ct);
                onSingleSkillFinished?.Invoke(taskId);
                onStageChanged?.Invoke("main_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", skill));
                await SynthesizeFinalReplyAsync(
                    conversationId, mainSession, routeMessages, routeResp.Content,
                    "Worker", userPrompt, syncReport.CompletedSteps, syncReport.Observations, syncReport.Sources,
                    turnStartedUtc, includeTaskStatus: false, tier,
                    onReplyAsync, onDirectTtsText, onChoices, onTaskCompleted: null, extraNote: null, ct);
                return;
            }
        }

        var logicalTaskId = resolveLogicalTaskId is not null
            ? resolveLogicalTaskId("call_planner", targetTaskId, effectiveGoal)
            : taskId;
        var plannerNote = !string.IsNullOrWhiteSpace(ack)
            ? ack
            : await _localizer.TextAsync("DefaultPlannerAck", "Planning the steps...", Array.Empty<object?>(), ct);
        if (onDispatchBackground is not null)
        {
            onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", tier.ToString()));
            if (!string.IsNullOrWhiteSpace(ttsText))
                onDirectTtsText?.Invoke(ttsText);
            await onReplyAsync(plannerNote, new List<MailSourceRef>());

            onDispatchBackground(async (bgCt, bgReplyAsync, bgDirectTtsText, bgChoices) =>
            {
                var planReport = await _planner.ExecuteGoalAsync(
                    conversationId, logicalTaskId, effectiveGoal, userPrompt, targetMailId, confirmAsync,
                    async s => { onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                    onStageChanged, tier, bgCt);
                bool isDone = true;
                await SynthesizeFinalReplyAsync(
                    conversationId, mainSession, routeMessages, routeResp.Content,
                    "Planner", userPrompt, planReport.CompletedSteps, planReport.Observations, planReport.Sources,
                    turnStartedUtc, includeTaskStatus: true, tier,
                    bgReplyAsync, bgDirectTtsText, bgChoices, done => isDone = done,
                    planReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({planReport.RoundsExecuted})." : null,
                    bgCt);
                onPlannerTaskFinished?.Invoke(logicalTaskId, isDone);
            });
            return;
        }

        onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", tier.ToString()));
        await onStatusAsync(plannerNote);
        SpeakDispatchAck(plannerNote, ttsText);
        Func<string, Task> plannerStatusAsync = s =>
            string.Equals(s, plannerNote, StringComparison.Ordinal)
                ? onStatusAsync(s)
                : onStatusAsync($"{plannerNote} ({s})");
        var syncPlanReport = await _planner.ExecuteGoalAsync(
            conversationId, logicalTaskId, effectiveGoal, userPrompt, targetMailId, confirmAsync,
            plannerStatusAsync, onStageChanged, tier, ct);
        bool syncDone = true;
        await SynthesizeFinalReplyAsync(
            conversationId, mainSession, routeMessages, routeResp.Content,
            "Planner", userPrompt, syncPlanReport.CompletedSteps, syncPlanReport.Observations, syncPlanReport.Sources,
            turnStartedUtc, includeTaskStatus: true, tier,
            onReplyAsync, onDirectTtsText, onChoices, done => syncDone = done,
            syncPlanReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({syncPlanReport.RoundsExecuted})." : null,
            ct);
        onPlannerTaskFinished?.Invoke(logicalTaskId, syncDone);
    }

    /// <summary>
    /// 作為唯一對外窗口，由 MainAgent 統一消化 Worker / Planner 回報與中途背景通知，
    /// 並直接接續已熱快取的 MainAgent 對話前綴產生最終給使用者的自然語言回覆（重用 Prompt Cache）。
    /// </summary>
    private async Task SynthesizeFinalReplyAsync(
        string conversationId,
        AgentSessionRecord mainSession,
        IReadOnlyList<ModelMessage> cachedPrefixMessages,
        string routingAssistantJson,
        string sourceLabel,
        string userPrompt,
        IReadOnlyList<string> completedSteps,
        List<string> observations,
        List<MailSourceRef> sources,
        DateTimeOffset turnStartedUtc,
        bool includeTaskStatus,
        ModelTier tier,
        Func<string, List<MailSourceRef>, Task> onReplyAsync,
        Action<string>? onDirectTtsText,
        Action<IReadOnlyList<string>>? onChoices,
        Action<bool>? onTaskCompleted,
        string? extraNote,
        CancellationToken ct)
    {
        // Sync any background notifications (mail triage, reminders, watchers) delivered while Worker/Planner ran
        var latestMsgs = await _store.GetChatMessagesAsync(conversationId, 10, ct);
        foreach (var n in latestMsgs.Where(m =>
                     m.Role == "notification" &&
                     m.CreatedAt >= turnStartedUtc &&
                     !string.IsNullOrWhiteSpace(m.Content)))
        {
            var note = $"[Concurrent notification already shown to user during this turn ({n.CreatedAt.ToLocalTime():HH:mm:ss})]: {n.Content}";
            if (!observations.Any(o => o.Contains(n.Content, StringComparison.Ordinal)))
                observations.Add(note);
        }

        var synthesisPrompt = PlannerPrompts.BuildMainAgentReportSynthesisPrompt(
            sourceLabel,
            completedSteps,
            observations,
            includeTaskStatus,
            _tts.IsEnabled,
            _tts.ChatMode,
            _tts.TargetLang,
            GetReplyLanguage(),
            extraNote);

        var finalMessages = new List<ModelMessage>(cachedPrefixMessages.Count + 2);
        finalMessages.AddRange(cachedPrefixMessages);
        finalMessages.Add(new("assistant", routingAssistantJson));
        finalMessages.Add(new("user", synthesisPrompt));

        var finalResp = await _model.CompleteAsync(finalMessages, tier, ct);
        WorkerAgent.CleanupTempScreenshots(observations);
        await _sessions.RecordTokensAsync(mainSession, finalResp.EstimatedTokens, ct: ct);

        var (clean0, finalChoices) = WorkerAgent.ExtractAndStripChoices(finalResp.Content);
        var (clean1, finalTtsText) = WorkerAgent.ExtractAndStripTtsText(clean0);
        var (cleanFinal, isTaskDone) = ExtractAndStripTaskStatus(clean1, finalChoices.Count > 0);

        if (finalChoices.Count > 0)
            onChoices?.Invoke(finalChoices);
        if (!string.IsNullOrWhiteSpace(finalTtsText))
            onDirectTtsText?.Invoke(finalTtsText);
        await onReplyAsync(cleanFinal, sources);
        onTaskCompleted?.Invoke(isTaskDone);

        if (completedSteps.Count > 0)
            _memoryReflection.EnqueueDecisionReflection(conversationId, userPrompt, completedSteps, observations, cleanFinal);
    }

    /// <summary>供排程非靜默技能執行完畢後，統一透過 MainAgent 以角色語氣摘要後推播給使用者。</summary>
    public async Task<string> SummarizeScheduledSkillAsync(
        string conversationId,
        string prompt,
        WorkerReport report,
        CancellationToken ct = default)
    {
        if (!report.Handled || report.Observations.Count == 0)
            return "";

        var mainSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Main", ct: ct);
        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();
        var prefFacts = await _store.RecallRelevantUserPrefFactsAsync(conversationId, prompt, 5, ct);
        var prefContext = MainAgentPrompts.FormatPreferencesContext(prefFacts);
        var sys = PlannerPrompts.BuildFinalReplySystemPrompt(
            activeStylePrompt,
            ttsEnabled: false,
            _tts.ChatMode,
            includeTaskStatus: false,
            _tts.TargetLang,
            GetReplyLanguage());

        var resp = await _model.CompleteAsync(new[]
        {
            new ModelMessage("system", sys),
            new ModelMessage("user", PlannerPrompts.BuildSingleSkillUserPrompt(prefContext, prompt, report.Observations))
        }, ModelTier.Simple, ct);

        WorkerAgent.CleanupTempScreenshots(report.Observations);
        await _sessions.RecordTokensAsync(mainSession, resp.EstimatedTokens, ct: ct);
        var (clean0, _) = WorkerAgent.ExtractAndStripChoices(resp.Content);
        var (clean1, _) = WorkerAgent.ExtractAndStripTtsText(clean0);
        return clean1;
    }

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

    /// <summary>
    /// 依目前啟用的角色語氣設定，產生一句主動關心使用者的搭話與對應語系的語音台詞。
    /// </summary>
    public async Task<(string? Reply, string? TtsText)> GenerateProactiveStyleLineAsync(
        string conversationId,
        IReadOnlyList<string> pinnedTitles,
        CancellationToken ct = default,
        string? activeWindow = null)
    {
        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();
        if (string.IsNullOrWhiteSpace(activeStylePrompt))
            return (null, null);

        var recent = await _store.GetChatMessagesAsync(conversationId, 20, ct);
        var history = BuildRecentHistory(recent, maxMessages: 15, perMessageMaxChars: 400, totalMaxChars: 4500);
        var pinnedText = pinnedTitles.Count == 0 ? "(None)" : string.Join(", ", pinnedTitles);
        var ttsEnabled = _tts.IsEnabled && _tts.ChatMode != "notify_only";
        var sys = MainAgentPrompts.BuildProactiveSystemPrompt(activeStylePrompt, _tts.ChatMode == "full", _tts.TargetLang, GetReplyLanguage(), ttsEnabled);
        var user = MainAgentPrompts.BuildProactiveUserPrompt(
            DateTimeOffset.Now,
            pinnedText,
            string.IsNullOrWhiteSpace(history) ? "(None)" : history,
            activeWindow);

        try
        {
            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", user)
            }, ModelTier.Simple, ct);

            var s = resp.Content.Trim();
            var first = s.IndexOf('{');
            var last = s.LastIndexOf('}');
            if (first >= 0 && last > first) s = s[first..(last + 1)];

            using var doc = JsonDocument.Parse(s);
            var reply = doc.RootElement.TryGetProperty("reply", out var r) ? r.GetString()?.Trim() : null;
            var ttsText = doc.RootElement.TryGetProperty("tts_text", out var tt) ? tt.GetString()?.Trim() : null;
            return (string.IsNullOrWhiteSpace(reply) ? null : reply, string.IsNullOrWhiteSpace(ttsText) ? null : ttsText);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Proactive style line generation skipped");
            return (null, null);
        }
    }

    /// <summary>依語音模式播報派發確認語（直接播報指定語系台詞或摘要播報）。</summary>
    private void SpeakDispatchAck(string ackText, string? ttsText)
    {
        if (!_tts.IsEnabled || _tts.ChatMode == "notify_only")
            return;

        if (!string.IsNullOrWhiteSpace(ttsText))
            _tts.EnqueueJapaneseSpeech(ttsText);
        else if (!string.IsNullOrWhiteSpace(ackText))
            _tts.EnqueueSummarySpeech(_localizer.TextCached("AlertTitleStatus", "AI Secretary Status"), ackText, kind: SpeechKind.Status);
    }

    /// <summary>解析路由模型輸出的 JSON 指令；若解析失敗則安全退回複雜 Planner 模式。</summary>
    private (string Action, string? TargetTaskId, string? CloseTaskId, ModelTier Tier, bool MemoryWorthy, string Reply, string Ack, string TtsText, List<string> Choices, string Skill, JsonElement Args, string Goal) ParseRoute(string raw, string fallbackGoal)
    {
        try
        {
            var s = raw.Trim();
            var first = s.IndexOf('{');
            var last = s.LastIndexOf('}');
            if (first >= 0 && last > first) s = s[first..(last + 1)];

            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "call_planner" : "call_planner";
            var targetTaskId = ParseNullableTaskId(root, "target_task_id");
            var closeTaskId = ParseNullableTaskId(root, "close_task_id");
            var tierStr = root.TryGetProperty("tier", out var t) ? t.GetString() ?? "" : "";
            var tier = tierStr.Equals("complex", StringComparison.OrdinalIgnoreCase)
                ? ModelTier.Complex
                : (action.Equals("call_planner", StringComparison.OrdinalIgnoreCase) ? ModelTier.Complex : ModelTier.Simple);
            var memoryWorthy = root.TryGetProperty("memory_worthy", out var mw) && mw.ValueKind == JsonValueKind.True;
            var reply = root.TryGetProperty("reply", out var r) ? r.GetString() ?? "" : "";
            var ack = root.TryGetProperty("ack", out var ak) ? ak.GetString()?.Trim() ?? "" : "";
            var ttsText = root.TryGetProperty("tts_text", out var tt) ? tt.GetString() ?? "" : "";
            var choices = new List<string>();
            if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ch.EnumerateArray())
                {
                    var label = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(label) && choices.Count < 4)
                        choices.Add(label);
                }
            }
            var skill = root.TryGetProperty("skill", out var sk) ? sk.GetString() ?? "" : "";
            var args = root.TryGetProperty("args", out var ar) ? ar.Clone() : default;
            var goal = root.TryGetProperty("goal", out var g) ? g.GetString() ?? fallbackGoal : fallbackGoal;
            return (action.ToLowerInvariant(), targetTaskId, closeTaskId, tier, memoryWorthy, reply, ack, ttsText, choices, skill, args, goal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MainAgent route parse fallback to planner");
            return ("call_planner", null, null, ModelTier.Complex, false, "", "", "", new List<string>(), "", default, fallbackGoal);
        }
    }

    /// <summary>從 JSON 元素讀取可空的任務 ID，將 `"null"` 字串正規化為 null。</summary>
    private static string? ParseNullableTaskId(JsonElement root, string propName)
    {
        if (root.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var rawId = el.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(rawId) && !rawId.Equals("null", StringComparison.OrdinalIgnoreCase))
                return rawId;
        }
        return null;
    }

    /// <summary>截斷過長訊息內容，同時保留 `[附件：...]` 標籤以免遺失檔案參照。</summary>
    internal static string TruncatePreservingAttachments(string content, int maxChars)
    {
        if (content.Length <= maxChars) return content;

        var attachmentMatches = AttachmentExtractor.AttachmentPattern.Matches(content);
        var head = content[..maxChars] + "...(truncated)";
        if (attachmentMatches.Count == 0) return head;

        var missingTags = attachmentMatches
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(tag => !head.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return missingTags.Count == 0 ? head : $"{head}\n{string.Join(" ", missingTags)}";
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_historyAnchors = new();

    /// <summary>
    /// 將近期對話紀錄轉換為受字數與訊息數限制的結構化模型訊息序列（供快取與上下文重用）。
    /// - 歷史訊息中的 [Attachment: ...] 一律換成 [History attachment: 檔名]，避免每輪、每次 LLM 呼叫重新展開整份文件/圖片。
    ///   （僅保留最近失敗輪次的附件，讓使用者重試時仍可讀取。）
    /// - 視窗起點採「黏性錨點」：只要仍在訊息數/字數上限內就沿用同一個起點，讓 history 前綴每輪完全相同，
    ///   provider 的 prompt cache 才能命中；超出上限時才一次性往後跳到約 60% 預算處，而不是每輪滑動一格（滑動會讓快取全失效）。
    /// </summary>
    internal static List<ModelMessage> BuildHistoryModelMessages(
        IReadOnlyList<ChatMessageRecord> recent,
        string? excludeTrailingUserPrompt = null,
        int maxMessages = 16,
        int perMessageMaxChars = 3000,
        int totalMaxChars = 20000)
    {
        var eligible = recent
            .Where(m => m.Role is "user" or "assistant" or "notification")
            .Where(m => !m.IsStreaming && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(Math.Max(maxMessages * 3, 30))
            .ToList();

        if (!string.IsNullOrWhiteSpace(excludeTrailingUserPrompt) && eligible.Count > 0)
        {
            var trimmedTarget = excludeTrailingUserPrompt.Trim();
            for (int i = eligible.Count - 1; i >= Math.Max(0, eligible.Count - 6); i--)
            {
                if (eligible[i].Role == "user" &&
                    string.Equals(eligible[i].Content.Trim(), trimmedTarget, StringComparison.Ordinal))
                {
                    eligible.RemoveAt(i);
                    break;
                }
            }
        }

        if (eligible.Count == 0)
            return new List<ModelMessage>();

        var contents = new List<(string Role, string Content)>(eligible.Count);
        for (int i = 0; i < eligible.Count; i++)
        {
            var m = eligible[i];
            var role = m.Role == "user" ? "user" : "assistant";
            var rawContent = m.Role == "notification"
                ? $"[System Notification] {m.Content}"
                : m.Content;
            // Keep historical messages immutable so prefix cache never invalidates as messages age.
            var content = AttachmentExtractor.AttachmentPattern.Replace(
                rawContent,
                match =>
                {
                    var name = Path.GetFileName(match.Groups[1].Value.Trim());
                    return name.StartsWith("screen_", StringComparison.OrdinalIgnoreCase)
                        ? $"[History screenshot: {name}]"
                        : $"[History attachment: {name}]";
                });
            contents.Add((role, TruncatePreservingAttachments(content, perMessageMaxChars)));
        }

        int SumFrom(int start)
        {
            int sum = 0;
            for (int i = start; i < contents.Count; i++) sum += contents[i].Content.Length;
            return sum;
        }

        var anchorKey = $"{eligible[0].ConversationId}|{maxMessages}|{totalMaxChars}";
        int startIdx = -1;
        if (s_historyAnchors.TryGetValue(anchorKey, out var anchorId))
        {
            var idx = eligible.FindIndex(m => m.Id == anchorId);
            if (idx >= 0 && eligible.Count - idx <= maxMessages && SumFrom(idx) <= totalMaxChars)
                startIdx = idx;
        }

        if (startIdx < 0)
        {
            // Re-anchor with generous headroom (~50% of both budgets) so the exact same prefix
            // stays cache-hot for ~8+ turns before advancing in one batch.
            var targetMessages = Math.Max(1, (int)Math.Ceiling(maxMessages * 0.5));
            var targetChars = (int)(totalMaxChars * 0.5);
            startIdx = eligible.Count - 1;
            int sum = contents[startIdx].Content.Length;
            while (startIdx > 0
                   && eligible.Count - (startIdx - 1) <= targetMessages
                   && sum + contents[startIdx - 1].Content.Length <= targetChars)
            {
                startIdx--;
                sum += contents[startIdx].Content.Length;
            }
            s_historyAnchors[anchorKey] = eligible[startIdx].Id;
        }

        var normalized = new List<ModelMessage>(contents.Count - startIdx);
        for (int i = startIdx; i < contents.Count; i++)
        {
            var (role, content) = contents[i];
            if (normalized.Count == 0 && role == "assistant")
                continue;
            if (normalized.Count > 0 && normalized[^1].Role == role)
                normalized[^1] = new ModelMessage(role, $"{normalized[^1].Content}\n\n{content}");
            else
                normalized.Add(new ModelMessage(role, content));
        }
        return normalized;
    }

    internal static string BuildRecentHistory(
        IReadOnlyList<ChatMessageRecord> recent,
        int maxMessages = 50,
        int perMessageMaxChars = 12000,
        int totalMaxChars = 80000)
    {
        var eligible = recent
            .Where(m => m.Role is "user" or "assistant" or "notification")
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(maxMessages)
            .ToList();

        var selected = new List<string>(eligible.Count);
        int totalChars = 0;
        for (int i = eligible.Count - 1; i >= 0; i--)
        {
            var m = eligible[i];
            var isVeryRecentUser = m.Role == "user" && i >= eligible.Count - 3;
            var isRecentFailedAttachment = m.Role == "user" && i >= eligible.Count - 6 && IsFollowedByError(eligible, i);
            var content = AttachmentExtractor.AttachmentPattern.Replace(
                m.Content,
                match =>
                {
                    var path = match.Groups[1].Value.Trim();
                    var name = Path.GetFileName(path);
                    var isScreenCap = name.StartsWith("screen_", StringComparison.OrdinalIgnoreCase);
                    if ((isVeryRecentUser || isRecentFailedAttachment) && !isScreenCap)
                        return match.Value;
                    return $"[History attachment: {name}]";
                });
            var line = $"[{m.CreatedAt.ToLocalTime():HH:mm}] [{m.Role}] {TruncatePreservingAttachments(content, perMessageMaxChars)}";
            if (selected.Count > 0 && totalChars + line.Length + 1 > totalMaxChars)
                break;
            selected.Add(line);
            totalChars += line.Length + 1;
        }
        selected.Reverse();
        return string.Join("\n", selected);
    }

    /// <summary>檢查該則使用者訊息之後是否緊接著錯誤回覆（用於過濾失敗輪次）。</summary>
    private static bool IsFollowedByError(List<ChatMessageRecord> eligible, int userIndex)
    {
        for (int j = userIndex + 1; j < eligible.Count; j++)
        {
            if (eligible[j].Role == "assistant")
                return eligible[j].Content.TrimStart().StartsWith("❌", StringComparison.Ordinal);
            if (eligible[j].Role == "user")
                return false;
        }
        return false;
    }
}

/// <summary>
/// 提供給 <see cref="MainAgent"/> 路由參考的未結案 Planner 任務摘要。
/// </summary>
public sealed record ActivePlannerTaskInfo(string TaskId, string Goal, bool IsRunning);
