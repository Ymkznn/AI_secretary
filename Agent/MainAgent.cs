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
    private readonly AgentBlackboard _blackboard;
    private readonly AttentionContextService _attention;
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
        AgentBlackboard blackboard,
        AttentionContextService attention,
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
        _blackboard = blackboard;
        _attention = attention;
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
        string? preferredLogicalTaskId = null,
        string? userMessageId = null)
    {
        onStageChanged?.Invoke("main", _localizer.TextCached("StageMainRouting", "MainAgent analyzing intent and routing..."));
        var mainSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Main", ct: ct);

        var recent = await _store.GetChatMessagesAsync(conversationId, 80, ct);
        var historyMessages = BuildHistoryModelMessages(recent, userPrompt, maxMessages: 20, perMessageMaxChars: 2500, totalMaxChars: 18000);

        // Record 90-second implicit correction feedback for any preceding deixis L1 prefetch turn.
        await _attention.CheckImplicitFeedbackAsync(conversationId, userPrompt, ct);

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
        var bbSnapshot = _blackboard.GetSnapshot(maxRecentResults: 6, maxAge: TimeSpan.FromMinutes(10));
        long routeStartVersion = bbSnapshot.Version;
        var inFlightIdsAtRouteStart = bbSnapshot
            .GetPromptVisibleInFlight(DateTimeOffset.UtcNow)
            .Select(t => t.TaskId)
            .ToHashSet(StringComparer.Ordinal);
        var blackboardContext = AgentBlackboard.FormatForPrompt(bbSnapshot);

        // L1 deictic attention prefetch (only triggered on deterministic deictic cues; zero cost on normal turns).
        var attentionCtx = await _attention.TryBuildL1ContextAsync(conversationId, userPrompt, targetMailId, recent, ct);

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
            _sandbox.GetTrustedPaths(),
            blackboardContext,
            attentionCtx?.FormattedBlock ?? "");

        // Fast path: 2-bit branch prediction for repeated read-only single-step intents (skips MainAgent routing LLM).
        // Skip fast path when a USER-INITIATED task is in-flight OR when L1 deictic context was prefetched.
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
            !bbSnapshot.HasUserInitiatedInFlight &&
            attentionCtx is null &&
            !AttachmentExtractor.AttachmentPattern.IsMatch(userPrompt))
        {
            var (predWf, predStep) = await _workflowCache.TryPredictSingleStepAsync(conversationId, userPrompt, ct);
            if (predWf is not null && predStep is not null)
            {
                var predSkill = predStep.Skill;
                var predArgs = predStep.Args;
                var predAck = await _localizer.TextAsync("DefaultDispatchAck", "Working on it...", Array.Empty<object?>(), ct);
                var syntheticRouteJson = JsonSerializer.Serialize(new { action = "run_skill", skill = predSkill, ack = predAck });
                var predGoal = $"{predSkill}: {userPrompt}";

                if (onDispatchBackground is not null)
                {
                    _blackboard.StartTask(taskId, "single_skill", predGoal, userMessageId);
                    onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", predSkill, "Predicted"));
                    await onReplyAsync(predAck, new List<MailSourceRef>());

                    onDispatchBackground(async (bgCt, bgReplyAsync, bgDirectTtsText, bgChoices) =>
                    {
                        var activeBbId = taskId;
                        bool bbCommitted = false;
                        try
                        {
                            var report = await _worker.ExecuteSingleSkillRawAsync(
                                conversationId, userPrompt, predSkill, predArgs, targetMailId, confirmAsync,
                                async s => { _blackboard.UpdateProgress(activeBbId, s); onStageChanged?.Invoke("worker", s); await onStatusAsync(s); },
                                bgCt, taskId);
                            if (report.Handled)
                            {
                                var rawSummary = AgentBlackboard.BuildCompactSummary(report.CompletedSteps, report.Observations);
                                var (_, joinedFollowUps, _) = _blackboard.CompleteTask(activeBbId, "single_skill", predGoal, rawSummary, report.Succeeded, suppressJoinedEvent: true);
                                bbCommitted = true;

                                await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: report.Succeeded, estimatedTokensSaved: report.Succeeded ? 350 : 0, bgCt);
                                await _store.InsertPredictionLogAsync("single_step", predWf.WorkflowId, userPrompt, predSkill, report.Succeeded, report.Succeeded ? 350 : 0, bgCt);
                                onSingleSkillFinished?.Invoke(taskId);
                                onStageChanged?.Invoke("worker_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", predSkill));
                                await SynthesizeFinalReplyAsync(
                                    conversationId, mainSession, basePromptMessages, syntheticRouteJson,
                                    "Worker", userPrompt, report.CompletedSteps, report.Observations, report.Sources,
                                    turnStartedUtc, includeTaskStatus: false, ModelTier.Simple,
                                    bgReplyAsync, bgDirectTtsText, bgChoices, onTaskCompleted: null, extraNote: null, bgCt,
                                    joinedFollowUps: joinedFollowUps, turnStartBbVersion: routeStartVersion, bbTaskId: activeBbId);
                                return;
                            }

                            await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: false, estimatedTokensSaved: 0, bgCt);
                            await _store.InsertPredictionLogAsync("single_step", predWf.WorkflowId, userPrompt, predSkill, false, 0, bgCt);
                            onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", "Simple"));
                            var plannerTaskId = resolveLogicalTaskId is not null
                                ? resolveLogicalTaskId("call_planner", null, userPrompt)
                                : taskId;
                            _blackboard.UpgradeTask(activeBbId, plannerTaskId, "planner", userPrompt);
                            activeBbId = plannerTaskId;

                            var planReport = await _planner.ExecuteGoalAsync(
                                conversationId, plannerTaskId, userPrompt, userPrompt, targetMailId, confirmAsync,
                                async s => { _blackboard.UpdateProgress(activeBbId, s); onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                                onStageChanged, ModelTier.Simple, bgCt);
                            var planSummary = AgentBlackboard.BuildCompactSummary(planReport.CompletedSteps, planReport.Observations);
                            var (_, planJoined, _) = _blackboard.CompleteTask(
                                activeBbId, "planner", userPrompt, planSummary,
                                succeeded: !planReport.WasDeclinedOrDenied && planReport.Observations.Count > 0,
                                suppressJoinedEvent: true);
                            bbCommitted = true;

                            bool isDone = true;
                            await SynthesizeFinalReplyAsync(
                                conversationId, mainSession, basePromptMessages, syntheticRouteJson,
                                "Planner", userPrompt, planReport.CompletedSteps, planReport.Observations, planReport.Sources,
                                turnStartedUtc, includeTaskStatus: true, ModelTier.Simple,
                                bgReplyAsync, bgDirectTtsText, bgChoices, done => isDone = done,
                                planReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({planReport.RoundsExecuted})." : null,
                                bgCt,
                                joinedFollowUps: planJoined, turnStartBbVersion: routeStartVersion, bbTaskId: activeBbId);
                            onPlannerTaskFinished?.Invoke(plannerTaskId, isDone);
                        }
                        finally
                        {
                            if (!bbCommitted)
                                _blackboard.EnsureClosed(activeBbId);
                        }
                    });
                    return;
                }

                await onStatusAsync(predAck);
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", predSkill, "Predicted"));
                _blackboard.StartTask(taskId, "single_skill", predGoal, userMessageId);
                bool syncPredCommitted = false;
                try
                {
                    var syncReport = await _worker.ExecuteSingleSkillRawAsync(
                        conversationId, userPrompt, predSkill, predArgs, targetMailId, confirmAsync,
                        s => onStatusAsync(s), ct, taskId);
                    if (syncReport.Handled)
                    {
                        var (_, syncJoined, _) = _blackboard.CompleteTask(taskId, "single_skill", predGoal, AgentBlackboard.BuildCompactSummary(syncReport.CompletedSteps, syncReport.Observations), syncReport.Succeeded, suppressJoinedEvent: true);
                        syncPredCommitted = true;
                        await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: syncReport.Succeeded, estimatedTokensSaved: syncReport.Succeeded ? 350 : 0, ct);
                        await _store.InsertPredictionLogAsync("single_step", predWf.WorkflowId, userPrompt, predSkill, syncReport.Succeeded, syncReport.Succeeded ? 350 : 0, ct);
                        onSingleSkillFinished?.Invoke(taskId);
                        onStageChanged?.Invoke("main_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", predSkill));
                        await SynthesizeFinalReplyAsync(
                            conversationId, mainSession, basePromptMessages, syntheticRouteJson,
                            "Worker", userPrompt, syncReport.CompletedSteps, syncReport.Observations, syncReport.Sources,
                            turnStartedUtc, includeTaskStatus: false, ModelTier.Simple,
                            onReplyAsync, onDirectTtsText, onChoices, onTaskCompleted: null, extraNote: null, ct,
                            joinedFollowUps: syncJoined, turnStartBbVersion: routeStartVersion, bbTaskId: taskId);
                        return;
                    }
                }
                finally
                {
                    if (!syncPredCommitted) _blackboard.EnsureClosed(taskId);
                }

                await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: false, estimatedTokensSaved: 0, ct);
                await _store.InsertPredictionLogAsync("single_step", predWf.WorkflowId, userPrompt, predSkill, false, 0, ct);
            }
        }

        var routeMessages = basePromptMessages;
        var routeResp = await _model.CompleteAsync(
            routeMessages,
            new LlmCallOptions("MainAgent.Route", ModelTier.Simple, CachePolicy.Conversation, LlmPriority.Interactive),
            ct);

        await _sessions.RecordTokensAsync(mainSession, routeResp.EstimatedTokens, ct: ct);

        if (string.IsNullOrWhiteSpace(routeResp.Content))
        {
            onStageChanged?.Invoke("main_done", _localizer.TextCached("StageMainDone", "MainAgent replied directly"));
            await onReplyAsync(BuildEmptyReplyFallback(routeResp.StopReason, _localizer), new List<MailSourceRef>());
            return;
        }

        var (action, targetTaskId, closeTaskId, tier, memoryWorthy, directReply, ack, ttsText, choices, skill, args, goal) = ParseRoute(routeResp.Content, userPrompt);
        var effectiveGoal = string.IsNullOrWhiteSpace(goal) ? userPrompt : goal;

        if (!string.IsNullOrWhiteSpace(preferredLogicalTaskId))
        {
            if (action == "reply" && string.IsNullOrWhiteSpace(closeTaskId))
                closeTaskId = preferredLogicalTaskId;
            else if (action is not ("reply" or "join_task") && string.IsNullOrWhiteSpace(targetTaskId))
                targetTaskId = preferredLogicalTaskId;
        }

        // Guard: if the model matched an active Planner task, force Planner continuation so task approvals persist.
        if (action == "run_skill" && !string.IsNullOrWhiteSpace(targetTaskId))
            action = "call_planner";

        if (attentionCtx is not null)
            await _attention.RecordDeixisOutcomeAsync(conversationId, attentionCtx, userPrompt, action, skill, ct);

        if (action == "join_task")
        {
            var joinTargetId = targetTaskId;
            if (string.IsNullOrWhiteSpace(joinTargetId))
            {
                // Auto-select ONLY among user-initiated in-flight tasks (planner / single_skill),
                // never accidentally binding an ambiguous join to background mail_sync/watcher.
                var userInFlight = bbSnapshot.InFlight.Where(t => t.IsUserInitiated).ToList();
                if (userInFlight.Count == 1)
                    joinTargetId = userInFlight[0].TaskId;
            }

            if (!string.IsNullOrWhiteSpace(joinTargetId))
            {
                if (_blackboard.TryJoinTask(joinTargetId, userMessageId ?? taskId, userPrompt, out var alreadyCompleted))
                {
                    var joinReply = !string.IsNullOrWhiteSpace(directReply)
                        ? directReply
                        : (!string.IsNullOrWhiteSpace(ack)
                            ? ack
                            : await _localizer.TextAsync("JoinTaskDefaultReply", "Still checking on that—I'll answer everything together as soon as it finishes.", Array.Empty<object?>(), ct));
                    onStageChanged?.Invoke("main_done", _localizer.TextCached("StageMainJoined", "Joined running background task"));
                    if (!string.IsNullOrWhiteSpace(ttsText))
                        onDirectTtsText?.Invoke(ttsText);
                    await onReplyAsync(joinReply, new List<MailSourceRef>());
                    return;
                }

                if (alreadyCompleted is not null)
                {
                    // Target task finished right while MainAgent was routing: answer immediately with fresh result!
                    onStageChanged?.Invoke("main_done", _localizer.TextCached("StageMainDone", "MainAgent replied with fresh result"));
                    await RewriteWithFreshResultsAsync(
                        conversationId, mainSession, routeMessages, routeResp.Content,
                        new[] { alreadyCompleted }, onReplyAsync, onDirectTtsText, onChoices, ct);
                    return;
                }
            }

            // Fallback if join target disappeared during routing: check if any in-flight task from start just completed
            if (inFlightIdsAtRouteStart.Count > 0 && _blackboard.Version > routeStartVersion)
            {
                var finishedAny = _blackboard.GetCompletedSince(routeStartVersion, onlyTaskIds: inFlightIdsAtRouteStart);
                if (finishedAny.Count > 0)
                {
                    onStageChanged?.Invoke("main_done", _localizer.TextCached("StageMainDone", "MainAgent replied with fresh result"));
                    await RewriteWithFreshResultsAsync(
                        conversationId, mainSession, routeMessages, routeResp.Content,
                        finishedAny, onReplyAsync, onDirectTtsText, onChoices, ct);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(directReply))
            {
                onStageChanged?.Invoke("main_done", _localizer.TextCached("StageMainDone", "MainAgent replied directly"));
                if (!string.IsNullOrWhiteSpace(ttsText))
                    onDirectTtsText?.Invoke(ttsText);
                await onReplyAsync(directReply, new List<MailSourceRef>());
                return;
            }
        }

        if (action == "reply" && !string.IsNullOrWhiteSpace(directReply) && string.IsNullOrWhiteSpace(targetMailId))
        {
            if (!string.IsNullOrWhiteSpace(closeTaskId))
                onClosePlannerTask?.Invoke(closeTaskId);

            // Narrow freshness check (direct reply only, max 1 regeneration):
            // Only trigger if a task that was ALREADY in <in_flight> at routing start completed during routing
            // AND is relevant to this turn (so unrelated questions are never slowed down or regenerated).
            if (inFlightIdsAtRouteStart.Count > 0 && _blackboard.Version > routeStartVersion)
            {
                var newlyCompleted = _blackboard.GetCompletedSince(routeStartVersion, onlyTaskIds: inFlightIdsAtRouteStart);
                var relevantCompleted = newlyCompleted
                    .Where(r => IsCompletedResultRelevantToTurn(userPrompt, directReply, r))
                    .ToList();
                if (relevantCompleted.Count > 0)
                {
                    onStageChanged?.Invoke("main_done", _localizer.TextCached("StageMainDone", "MainAgent replied with fresh result"));
                    var rewritten = await RewriteWithFreshResultsAsync(
                        conversationId, mainSession, routeMessages, routeResp.Content,
                        relevantCompleted, onReplyAsync, onDirectTtsText, onChoices, ct);
                    if (memoryWorthy)
                        _memoryReflection.EnqueueChatReflection(conversationId, userPrompt, rewritten);
                    return;
                }
            }

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
            var skillGoal = $"{skill}: {effectiveGoal}";
            if (onDispatchBackground is not null)
            {
                _blackboard.StartTask(taskId, "single_skill", skillGoal, userMessageId);
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", skill, tier.ToString()));
                if (!string.IsNullOrWhiteSpace(ttsText))
                    onDirectTtsText?.Invoke(ttsText);
                await onReplyAsync(dispatchNote, new List<MailSourceRef>());

                onDispatchBackground(async (bgCt, bgReplyAsync, bgDirectTtsText, bgChoices) =>
                {
                    var activeBbId = taskId;
                    bool bbCommitted = false;
                    try
                    {
                        var report = await _worker.ExecuteSingleSkillRawAsync(
                            conversationId, userPrompt, skill, args, targetMailId, confirmAsync,
                            async s => { _blackboard.UpdateProgress(activeBbId, s); onStageChanged?.Invoke("worker", s); await onStatusAsync(s); },
                            bgCt, taskId);
                        if (report.Handled)
                        {
                            var rawSummary = AgentBlackboard.BuildCompactSummary(report.CompletedSteps, report.Observations);
                            var (_, joinedFollowUps, _) = _blackboard.CompleteTask(activeBbId, "single_skill", skillGoal, rawSummary, report.Succeeded, suppressJoinedEvent: true);
                            bbCommitted = true;

                            if (report.Succeeded)
                            {
                                await _workflowCache.RecordObservedSingleSkillAsync(conversationId, userPrompt, skill, args, bgCt);
                                await _stateMachine.ObserveUserSkillExecutionAsync(_activity.GetLastExternalTarget(), skill, args, bgCt);
                            }
                            onSingleSkillFinished?.Invoke(taskId);
                            onStageChanged?.Invoke("worker_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", skill));
                            await SynthesizeFinalReplyAsync(
                                conversationId, mainSession, routeMessages, routeResp.Content,
                                "Worker", userPrompt, report.CompletedSteps, report.Observations, report.Sources,
                                turnStartedUtc, includeTaskStatus: false, tier,
                                bgReplyAsync, bgDirectTtsText, bgChoices, onTaskCompleted: null, extraNote: null, bgCt,
                                joinedFollowUps: joinedFollowUps, turnStartBbVersion: routeStartVersion, bbTaskId: activeBbId);
                            return;
                        }

                        onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", tier.ToString()));
                        var plannerTaskId = resolveLogicalTaskId is not null
                            ? resolveLogicalTaskId("call_planner", targetTaskId, effectiveGoal)
                            : taskId;
                        _blackboard.UpgradeTask(activeBbId, plannerTaskId, "planner", effectiveGoal);
                        activeBbId = plannerTaskId;

                        var planReport = await _planner.ExecuteGoalAsync(
                            conversationId, plannerTaskId, effectiveGoal, userPrompt, targetMailId, confirmAsync,
                            async s => { _blackboard.UpdateProgress(activeBbId, s); onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                            onStageChanged, tier, bgCt);
                        var planSummary = AgentBlackboard.BuildCompactSummary(planReport.CompletedSteps, planReport.Observations);
                        var (_, planJoined, _) = _blackboard.CompleteTask(
                            activeBbId, "planner", effectiveGoal, planSummary,
                            succeeded: !planReport.WasDeclinedOrDenied && planReport.Observations.Count > 0,
                            suppressJoinedEvent: true);
                        bbCommitted = true;

                        bool isDone = true;
                        await SynthesizeFinalReplyAsync(
                            conversationId, mainSession, routeMessages, routeResp.Content,
                            "Planner", userPrompt, planReport.CompletedSteps, planReport.Observations, planReport.Sources,
                            turnStartedUtc, includeTaskStatus: true, tier,
                            bgReplyAsync, bgDirectTtsText, bgChoices, done => isDone = done,
                            planReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({planReport.RoundsExecuted})." : null,
                            bgCt,
                            joinedFollowUps: planJoined, turnStartBbVersion: routeStartVersion, bbTaskId: activeBbId);
                        onPlannerTaskFinished?.Invoke(plannerTaskId, isDone);
                    }
                    finally
                    {
                        if (!bbCommitted)
                            _blackboard.EnsureClosed(activeBbId);
                    }
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
            _blackboard.StartTask(taskId, "single_skill", skillGoal, userMessageId);
            bool syncSkillCommitted = false;
            try
            {
                var syncReport = await _worker.ExecuteSingleSkillRawAsync(
                    conversationId, userPrompt, skill, args, targetMailId, confirmAsync, workerStatusAsync, ct, taskId);
                if (syncReport.Handled)
                {
                    var (_, syncJoined, _) = _blackboard.CompleteTask(taskId, "single_skill", skillGoal, AgentBlackboard.BuildCompactSummary(syncReport.CompletedSteps, syncReport.Observations), syncReport.Succeeded, suppressJoinedEvent: true);
                    syncSkillCommitted = true;
                    if (syncReport.Succeeded)
                    {
                        await _workflowCache.RecordObservedSingleSkillAsync(conversationId, userPrompt, skill, args, ct);
                        await _stateMachine.ObserveUserSkillExecutionAsync(_activity.GetLastExternalTarget(), skill, args, ct);
                    }
                    onSingleSkillFinished?.Invoke(taskId);
                    onStageChanged?.Invoke("main_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", skill));
                    await SynthesizeFinalReplyAsync(
                        conversationId, mainSession, routeMessages, routeResp.Content,
                        "Worker", userPrompt, syncReport.CompletedSteps, syncReport.Observations, syncReport.Sources,
                        turnStartedUtc, includeTaskStatus: false, tier,
                        onReplyAsync, onDirectTtsText, onChoices, onTaskCompleted: null, extraNote: null, ct,
                        joinedFollowUps: syncJoined, turnStartBbVersion: routeStartVersion, bbTaskId: taskId);
                    return;
                }
            }
            finally
            {
                if (!syncSkillCommitted) _blackboard.EnsureClosed(taskId);
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
            _blackboard.StartTask(logicalTaskId, "planner", effectiveGoal, userMessageId);
            onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", tier.ToString()));
            if (!string.IsNullOrWhiteSpace(ttsText))
                onDirectTtsText?.Invoke(ttsText);
            await onReplyAsync(plannerNote, new List<MailSourceRef>());

            onDispatchBackground(async (bgCt, bgReplyAsync, bgDirectTtsText, bgChoices) =>
            {
                bool bbCommitted = false;
                try
                {
                    var planReport = await _planner.ExecuteGoalAsync(
                        conversationId, logicalTaskId, effectiveGoal, userPrompt, targetMailId, confirmAsync,
                        async s => { _blackboard.UpdateProgress(logicalTaskId, s); onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                        onStageChanged, tier, bgCt);
                    var planSummary = AgentBlackboard.BuildCompactSummary(planReport.CompletedSteps, planReport.Observations);
                    var (_, planJoined, _) = _blackboard.CompleteTask(
                        logicalTaskId, "planner", effectiveGoal, planSummary,
                        succeeded: !planReport.WasDeclinedOrDenied && planReport.Observations.Count > 0,
                        suppressJoinedEvent: true);
                    bbCommitted = true;

                    bool isDone = true;
                    await SynthesizeFinalReplyAsync(
                        conversationId, mainSession, routeMessages, routeResp.Content,
                        "Planner", userPrompt, planReport.CompletedSteps, planReport.Observations, planReport.Sources,
                        turnStartedUtc, includeTaskStatus: true, tier,
                        bgReplyAsync, bgDirectTtsText, bgChoices, done => isDone = done,
                        planReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({planReport.RoundsExecuted})." : null,
                        bgCt,
                        joinedFollowUps: planJoined, turnStartBbVersion: routeStartVersion, bbTaskId: logicalTaskId);
                    onPlannerTaskFinished?.Invoke(logicalTaskId, isDone);
                }
                finally
                {
                    if (!bbCommitted)
                        _blackboard.EnsureClosed(logicalTaskId);
                }
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
        _blackboard.StartTask(logicalTaskId, "planner", effectiveGoal, userMessageId);
        bool syncPlanCommitted = false;
        try
        {
            var syncPlanReport = await _planner.ExecuteGoalAsync(
                conversationId, logicalTaskId, effectiveGoal, userPrompt, targetMailId, confirmAsync,
                plannerStatusAsync, onStageChanged, tier, ct);
            var (_, syncPlanJoined, _) = _blackboard.CompleteTask(
                logicalTaskId, "planner", effectiveGoal,
                AgentBlackboard.BuildCompactSummary(syncPlanReport.CompletedSteps, syncPlanReport.Observations),
                succeeded: !syncPlanReport.WasDeclinedOrDenied && syncPlanReport.Observations.Count > 0,
                suppressJoinedEvent: true);
            syncPlanCommitted = true;

            bool syncDone = true;
            await SynthesizeFinalReplyAsync(
                conversationId, mainSession, routeMessages, routeResp.Content,
                "Planner", userPrompt, syncPlanReport.CompletedSteps, syncPlanReport.Observations, syncPlanReport.Sources,
                turnStartedUtc, includeTaskStatus: true, tier,
                onReplyAsync, onDirectTtsText, onChoices, done => syncDone = done,
                syncPlanReport.ReachedRoundLimit ? $"Planner reached the configured max rounds ({syncPlanReport.RoundsExecuted})." : null,
                ct,
                joinedFollowUps: syncPlanJoined, turnStartBbVersion: routeStartVersion, bbTaskId: logicalTaskId);
            onPlannerTaskFinished?.Invoke(logicalTaskId, syncDone);
        }
        finally
        {
            if (!syncPlanCommitted) _blackboard.EnsureClosed(logicalTaskId);
        }
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
        CancellationToken ct,
        IReadOnlyList<JoinedFollowUp>? joinedFollowUps = null,
        long turnStartBbVersion = 0,
        string? bbTaskId = null)
    {
        // Sync concurrent background alerts separately as side notifications (never mixed into main task observations),
        // while keeping concurrent user-initiated task completions visible if relevant.
        var sideNotifications = new List<string>();
        var latestMsgs = await _store.GetChatMessagesAsync(conversationId, 10, ct);
        foreach (var n in latestMsgs.Where(m =>
                     m.Role == "notification" &&
                     m.CreatedAt >= turnStartedUtc &&
                     !string.IsNullOrWhiteSpace(m.Content)))
        {
            var note = $"({n.CreatedAt.ToLocalTime():HH:mm:ss}) [notification] {n.Content}";
            if (!observations.Any(o => o.Contains(n.Content, StringComparison.Ordinal)) &&
                !sideNotifications.Any(s => s.Contains(n.Content, StringComparison.Ordinal)))
            {
                sideNotifications.Add(note);
            }
        }

        if (turnStartBbVersion > 0)
        {
            foreach (var r in _blackboard.GetCompletedSince(turnStartBbVersion, excludeTaskId: bbTaskId))
            {
                if (r.IsUserInitiated)
                {
                    var note = $"[Concurrent user task result ({r.Kind}) at {r.CompletedAt.ToLocalTime():HH:mm:ss}]: {r.Goal} -> {r.Summary}";
                    if (!observations.Any(o => o.Contains(r.Summary, StringComparison.Ordinal)))
                        observations.Add(note);
                }
                else if (r.IsSignificant)
                {
                    var note = $"({r.CompletedAt.ToLocalTime():HH:mm:ss}) [{r.Kind}] {r.Goal}: {r.Summary}";
                    if (!observations.Any(o => o.Contains(r.Summary, StringComparison.Ordinal)) &&
                        !sideNotifications.Any(s => s.Contains(r.Summary, StringComparison.Ordinal)))
                    {
                        sideNotifications.Add(note);
                    }
                }
            }
        }

        // Strip raw HTML sections from read_dom observations before FinalReply & MemoryReflection
        // so human-facing reply synthesis never re-ingests 10k-20k tokens of raw HTML.
        var finalObservations = observations
            .Select(BrowserFormSkill.StripHtmlSectionForFinalReply)
            .ToList();

        var synthesisPrompt = PlannerPrompts.BuildMainAgentReportSynthesisPrompt(
            sourceLabel,
            completedSteps,
            finalObservations,
            includeTaskStatus,
            _tts.IsEnabled,
            _tts.ChatMode,
            _tts.TargetLang,
            GetReplyLanguage(),
            extraNote,
            joinedFollowUps,
            sideNotifications);

        var finalMessages = new List<ModelMessage>(cachedPrefixMessages.Count + 2);
        finalMessages.AddRange(cachedPrefixMessages);
        finalMessages.Add(new("assistant", routingAssistantJson));
        finalMessages.Add(new("user", synthesisPrompt));

        var finalResp = await _model.CompleteAsync(
            finalMessages,
            new LlmCallOptions("MainAgent.FinalReply", tier, CachePolicy.PrefixReadOnly, LlmPriority.Interactive),
            ct);
        WorkerAgent.CleanupTempScreenshots(observations);
        await _sessions.RecordTokensAsync(mainSession, finalResp.EstimatedTokens, ct: ct);

        var (cleanFinal, finalChoices, finalTtsText, isTaskDone, usedFallback) =
            ResolveFinalReplyOrFallback(finalResp, _localizer);

        if (!string.IsNullOrWhiteSpace(bbTaskId) && !string.IsNullOrWhiteSpace(cleanFinal))
            _blackboard.RefineResultSummary(bbTaskId, cleanFinal);

        if (finalChoices.Count > 0)
            onChoices?.Invoke(finalChoices);
        if (!string.IsNullOrWhiteSpace(finalTtsText))
            onDirectTtsText?.Invoke(finalTtsText);
        await onReplyAsync(cleanFinal, sources);
        onTaskCompleted?.Invoke(isTaskDone);

        if (completedSteps.Count > 0 && !usedFallback)
            _memoryReflection.EnqueueDecisionReflection(conversationId, userPrompt, completedSteps, finalObservations, cleanFinal);
    }

    internal static bool IsRefusalStopReason(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason)) return false;
        var norm = stopReason.Trim().ToLowerInvariant();
        return norm is "refusal" or "content_filter" or "safety" or "recitation" or "blocklist" or "prohibited_content" or "spii"
               || norm.Contains("refus", StringComparison.Ordinal)
               || norm.Contains("content_filter", StringComparison.Ordinal);
    }

    internal static bool IsLengthTruncatedStopReason(string? stopReason)
    {
        if (string.IsNullOrWhiteSpace(stopReason)) return false;
        var norm = stopReason.Trim().ToLowerInvariant();
        return norm is "max_tokens" or "length" or "max_output_tokens";
    }

    /// <summary>
    /// Zero-LLM deterministic explanation when a model returns empty text after tag stripping
    /// (e.g. API refusal, content_filter, max_tokens truncation, or tag-only output).
    /// Uses <see cref="UiLocalizer.TextCachedOnly"/> so we NEVER issue another LLM call (even background translation) for error text.
    /// </summary>
    internal static string BuildEmptyReplyFallback(string? stopReason, UiLocalizer localizer)
    {
        if (IsRefusalStopReason(stopReason))
            return localizer.TextCachedOnly("EmptyReplyRefusal", "這題被模型的安全機制擋下了，可以換個方式描述，例如只問觀念。");
        if (IsLengthTruncatedStopReason(stopReason))
            return localizer.TextCachedOnly("EmptyReplyMaxTokens", "回覆太長被截斷了。");
        return localizer.TextCachedOnly("EmptyReplyGeneric", "這次沒有產生回覆，請再試一次。");
    }

    internal static (string CleanReply, IReadOnlyList<string> Choices, string? TtsText, bool IsTaskDone, bool UsedFallback)
        ResolveFinalReplyOrFallback(ModelResponse resp, UiLocalizer localizer)
    {
        var (clean0, choices) = WorkerAgent.ExtractAndStripChoices(resp.Content ?? "");
        var (clean1, ttsText) = WorkerAgent.ExtractAndStripTtsText(clean0);
        var (cleanFinal, isTaskDone) = ExtractAndStripTaskStatus(clean1, choices.Count > 0);

        if (!string.IsNullOrWhiteSpace(cleanFinal))
            return (cleanFinal, choices, string.IsNullOrWhiteSpace(ttsText) ? null : ttsText, isTaskDone, false);

        var fallback = BuildEmptyReplyFallback(resp.StopReason, localizer);
        return (fallback, Array.Empty<string>(), null, true, true);
    }

    /// <summary>
    /// 一次性以剛完成的背景任務結果改寫回覆（重用路由前綴快取，絕不遞迴重試）。
    /// </summary>
    private async Task<string> RewriteWithFreshResultsAsync(
        string conversationId,
        AgentSessionRecord mainSession,
        IReadOnlyList<ModelMessage> cachedRouteMessages,
        string routingAssistantJson,
        IReadOnlyList<TaskResult> completedResults,
        Func<string, List<MailSourceRef>, Task> onReplyAsync,
        Action<string>? onDirectTtsText,
        Action<IReadOnlyList<string>>? onChoices,
        CancellationToken ct)
    {
        var rewritePrompt = PlannerPrompts.BuildFreshnessRewritePrompt(
            completedResults,
            _tts.IsEnabled,
            _tts.ChatMode,
            _tts.TargetLang,
            GetReplyLanguage());

        var msgs = new List<ModelMessage>(cachedRouteMessages.Count + 2);
        msgs.AddRange(cachedRouteMessages);
        msgs.Add(new("assistant", routingAssistantJson));
        msgs.Add(new("user", rewritePrompt));

        var resp = await _model.CompleteAsync(
            msgs,
            new LlmCallOptions("MainAgent.FreshnessRegen", ModelTier.Simple, CachePolicy.PrefixReadOnly, LlmPriority.Interactive),
            ct);
        await _sessions.RecordTokensAsync(mainSession, resp.EstimatedTokens, ct: ct);

        var (cleanFinal, choices, ttsText, _, _) = ResolveFinalReplyOrFallback(resp, _localizer);

        if (choices.Count > 0)
            onChoices?.Invoke(choices);
        if (!string.IsNullOrWhiteSpace(ttsText))
            onDirectTtsText?.Invoke(ttsText);
        await onReplyAsync(cleanFinal, new List<MailSourceRef>());
        return cleanFinal;
    }

    /// <summary>供使用者透過 join_task 掛載於非互動背景任務（mail_sync / watcher / scheduled / state_machine）完成後產生回覆。</summary>
    public async Task<(string Reply, string? TtsText)> SynthesizeJoinedFollowUpReplyAsync(
        string conversationId,
        TaskResult result,
        IReadOnlyList<JoinedFollowUp> joinedFollowUps,
        CancellationToken ct = default)
    {
        var mainSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Main", ct: ct);
        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();
        var prefFacts = await _store.RecallRelevantUserPrefFactsAsync(conversationId, result.Goal, 5, ct);
        var prefContext = MainAgentPrompts.FormatPreferencesContext(prefFacts);
        var sys = PlannerPrompts.BuildFinalReplySystemPrompt(
            activeStylePrompt,
            _tts.IsEnabled,
            _tts.ChatMode,
            includeTaskStatus: false,
            _tts.TargetLang,
            GetReplyLanguage());

        var resp = await _model.CompleteAsync(new[]
        {
            new ModelMessage("system", sys),
            new ModelMessage("user", PlannerPrompts.BuildJoinedSystemTaskPrompt(prefContext, result, joinedFollowUps))
        }, new LlmCallOptions("MainAgent.JoinedSystemReply", ModelTier.Simple, CachePolicy.None, LlmPriority.Interactive), ct);

        await _sessions.RecordTokensAsync(mainSession, resp.EstimatedTokens, ct: ct);
        var (cleanFinal, _, ttsText, _, _) = ResolveFinalReplyOrFallback(resp, _localizer);
        return (cleanFinal, ttsText);
    }

    private static readonly string[] WaitingOrStatusSignals =
    {
        "還在查", "還在跑", "還在找", "還沒收到", "沒有收到", "沒收到", "尚未收到",
        "沒看到驗證碼", "還沒出來", "查不到", "好了嗎", "好了沒", "太慢", "確認中",
        "not yet", "still checking", "still running", "still working"
    };

    private static readonly string[] ShortStatusNudges =
    {
        "結果呢", "好了沒", "好了嗎", "進度", "怎麼樣", "有嗎", "收到了嗎", "來了嗎", "還沒好", "太慢"
    };

    /// <summary>
    /// Checks whether a background task that completed during routing is relevant to this direct reply turn,
    /// preventing unrelated questions (e.g. "what day is today?", "how to write C# code?") from being slowed down or regenerated.
    /// </summary>
    internal static bool IsCompletedResultRelevantToTurn(string userPrompt, string directReply, TaskResult completed)
    {
        var trimmedPrompt = (userPrompt ?? "").Trim();
        var turnText = $"{trimmedPrompt} {directReply}";

        if (WaitingOrStatusSignals.Any(sig => turnText.Contains(sig, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (trimmedPrompt.Length <= 14 &&
            ShortStatusNudges.Any(n => trimmedPrompt.Contains(n, StringComparison.OrdinalIgnoreCase)))
            return true;

        var turnTokens = ExtractSalientTokens(turnText);
        if (turnTokens.Count == 0)
            return false;

        var taskTokens = ExtractSalientTokens($"{completed.Goal} {completed.Summary}");
        foreach (var tok in turnTokens)
        {
            if (taskTokens.Contains(tok))
                return true;
        }
        return false;
    }

    private static HashSet<string> ExtractSalientTokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9_]{3,}"))
        {
            var w = m.Value;
            if (w is "the" or "and" or "for" or "with" or "from" or "that" or "this" or "are" or "was" or "have" or "not" or "all" or "true" or "false" or "none" or "steps")
                continue;
            set.Add(w);
        }

        // Extract CJK 2-grams for Chinese/Japanese matching without false positives on single filler chars
        var cjkRun = new System.Text.StringBuilder();
        void FlushRun()
        {
            if (cjkRun.Length >= 2)
            {
                var s = cjkRun.ToString();
                for (int i = 0; i <= s.Length - 2; i++)
                {
                    var bg = s.Substring(i, 2);
                    if (bg is "一下" or "什麼" or "怎麼" or "可以" or "幫我" or "這個" or "那個" or "目前" or "現在" or "今天" or "已經" or "沒有" or "不是")
                        continue;
                    set.Add(bg);
                }
            }
            cjkRun.Clear();
        }

        foreach (var ch in text)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF')
                cjkRun.Append(ch);
            else
                FlushRun();
        }
        FlushRun();
        return set;
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
        }, new LlmCallOptions("MainAgent.ScheduledSummary", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), ct);

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
    /// Resolves effective proactive screen-awareness level per style file with strict precedence:
    /// Verbal override bound to <paramref name="styleScopeKey"/> > cached LLM classification by SHA-256 > default Normal.
    /// </summary>
    internal static async Task<(ProactiveAwarenessLevel Level, string Source)> ResolveProactiveAwarenessAsync(
        SqliteStore store,
        IModelGateway model,
        string conversationId,
        string styleScopeKey,
        string activeStylePrompt,
        IReadOnlyList<KnowledgeFactRecord> confirmedProactivePrefs,
        ILogger? logger,
        CancellationToken ct)
    {
        // 1) Per-style-file verbal override ALWAYS wins (zero LLM calls needed when overridden).
        var (overrideLevel, overrideSrc) = ProactiveTopicEngine.ResolveEffectiveAwareness(
            confirmedProactivePrefs, styleScopeKey, llmClassifiedLevel: null);
        if (overrideSrc == "override")
            return (overrideLevel, "override");

        // 2) Lookup cached 1-time LLM classification by style prompt SHA-256.
        var hash = ProactiveTopicEngine.ComputeStyleHash(activeStylePrompt);
        var cacheKey = $"style_awareness:{hash}";
        var cachedRaw = await store.GetSyncValueAsync(cacheKey, ct);
        var cachedLevel = ProactiveTopicEngine.TryParseAwarenessLevel(cachedRaw);
        if (cachedLevel.HasValue)
            return ProactiveTopicEngine.ResolveEffectiveAwareness(confirmedProactivePrefs, styleScopeKey, cachedLevel);

        // 3) First time seeing this style hash: classify once with Simple LLM (language-agnostic); default Normal on failure.
        try
        {
            using var classifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            classifyCts.CancelAfter(TimeSpan.FromSeconds(15));

            var resp = await model.CompleteAsync(new[]
            {
                new ModelMessage("system", MainAgentPrompts.BuildStyleAwarenessClassifySystemPrompt()),
                new ModelMessage("user", MainAgentPrompts.BuildStyleAwarenessClassifyUserPrompt(activeStylePrompt))
            }, new LlmCallOptions("MainAgent.StyleClassify", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), classifyCts.Token);

            var raw = (resp.Content ?? "").Trim();
            var first = raw.IndexOf('{');
            var last = raw.LastIndexOf('}');
            if (first >= 0 && last > first) raw = raw[first..(last + 1)];

            ProactiveAwarenessLevel? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("level", out var lvlEl))
                    parsed = ProactiveTopicEngine.TryParseAwarenessLevel(lvlEl.GetString());
            }
            catch
            {
                parsed = ProactiveTopicEngine.TryParseAwarenessLevel(raw);
            }

            if (parsed.HasValue)
            {
                await store.SetSyncValueAsync(cacheKey, ProactiveTopicEngine.ToCanonicalString(parsed.Value), ct);
                return ProactiveTopicEngine.ResolveEffectiveAwareness(confirmedProactivePrefs, styleScopeKey, parsed.Value);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Style awareness classification failed; defaulting to normal.");
        }

        return (ProactiveAwarenessLevel.Normal, "default");
    }

    /// <summary>
    /// 依目前啟用的角色語氣設定與程式抽選出的單一話題，產生一句主動關心搭話或允許跳過（{"skip": true}）。
    /// 僅濾除先前的自家主動搭話（TaskId = 'proactive'），保留真正完成的對話／任務紀錄供 OngoingThread 延續。
    /// </summary>
    public async Task<ProactiveGenerationResult?> GenerateProactiveStyleLineAsync(
        string conversationId,
        IReadOnlyList<string> pinnedReminders,
        IReadOnlyList<string> pendingConfirmations,
        IReadOnlyList<string> upcomingSchedules,
        ProactiveActivitySnapshot? activity,
        IReadOnlyList<ProactiveTopicKind> recentTopics,
        IReadOnlyList<string> recentSharedCustomSummaries,
        CancellationToken ct = default)
    {
        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();
        if (string.IsNullOrWhiteSpace(activeStylePrompt))
            return null;

        var styleScopeKey = ProactiveTopicEngine.ResolveStyleScopeKey(
            _tts.GetActiveStyleFilePath() ?? _styles.ActiveStylePath,
            activeStylePrompt);

        var nowLocal = DateTimeOffset.Now;
        var confirmedPrefs = await _store.GetConfirmedProactivePrefsAsync(conversationId, ct);
        var (awarenessLevel, awarenessSource) = await ResolveProactiveAwarenessAsync(
            _store, _model, conversationId, styleScopeKey, activeStylePrompt, confirmedPrefs, _logger, ct);

        // OngoingThread keeps real completed conversation turns (user + non-proactive assistant replies)
        // while strictly excluding prior proactive idle check-ins (TaskId = 'proactive').
        var recentTurns24h = await _store.GetRecentNonProactiveChatTurnsAsync(conversationId, DateTimeOffset.UtcNow.AddHours(-24), 6, ct);
        var bbSnap = _blackboard.GetSnapshot(maxRecentResults: 4, maxAge: TimeSpan.FromHours(6));
        var bbHighlights = bbSnap.RecentResults
            .Where(r => r.IsSignificant && !string.IsNullOrWhiteSpace(r.Summary))
            .TakeLast(3)
            .Select(r => $"{r.Goal}: {r.Summary}")
            .ToList();

        var ctx = new ProactiveTopicContext(
            Now: nowLocal,
            RecentConversationTurns24h: recentTurns24h,
            PendingConfirmations: pendingConfirmations,
            RecentBlackboardResults: bbHighlights,
            PinnedReminders: pinnedReminders,
            UpcomingSchedules: upcomingSchedules,
            Activity: activity,
            CustomTopics: confirmedPrefs,
            RecentSharedCustomSummaries: recentSharedCustomSummaries,
            RecentTopics: recentTopics,
            AwarenessLevel: awarenessLevel);

        var selection = ProactiveTopicEngine.PickTopic(ctx);
        var recentProactiveSnippets = await _store.GetRecentProactiveSnippetsAsync(
            conversationId, DateTimeOffset.UtcNow.AddHours(-12), 6, ct);
        var ttsEnabled = _tts.IsEnabled && _tts.ChatMode != "notify_only";
        var sys = MainAgentPrompts.BuildProactiveSystemPrompt(
            activeStylePrompt,
            _tts.ChatMode == "full",
            _tts.TargetLang,
            GetReplyLanguage(),
            ttsEnabled);
        var user = MainAgentPrompts.BuildProactiveUserPrompt(nowLocal, selection, recentTopics, recentProactiveSnippets);

        try
        {
            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", user)
            }, new LlmCallOptions("MainAgent.Proactive", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), ct);

            var s = (resp.Content ?? "").Trim();
            var first = s.IndexOf('{');
            var last = s.LastIndexOf('}');
            if (first >= 0 && last > first) s = s[first..(last + 1)];

            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.TryGetProperty("skip", out var skipEl) &&
                skipEl.ValueKind == JsonValueKind.True)
            {
                return new ProactiveGenerationResult(
                    Reply: null,
                    TtsText: null,
                    Skipped: true,
                    SelectedTopic: selection.Topic,
                    SharedSummary: null,
                    AwarenessLevel: awarenessLevel,
                    AwarenessSource: awarenessSource,
                    StyleScopeKey: styleScopeKey);
            }

            var reply = doc.RootElement.TryGetProperty("reply", out var r) ? r.GetString()?.Trim() : null;
            var ttsText = doc.RootElement.TryGetProperty("tts_text", out var tt) && tt.ValueKind == JsonValueKind.String
                ? tt.GetString()?.Trim()
                : null;
            var sharedSummary = doc.RootElement.TryGetProperty("shared_summary", out var ss) && ss.ValueKind == JsonValueKind.String
                ? ss.GetString()?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(reply))
            {
                return new ProactiveGenerationResult(
                    Reply: null,
                    TtsText: null,
                    Skipped: true,
                    SelectedTopic: selection.Topic,
                    SharedSummary: null,
                    AwarenessLevel: awarenessLevel,
                    AwarenessSource: awarenessSource,
                    StyleScopeKey: styleScopeKey);
            }

            if (selection.Topic == ProactiveTopicKind.CustomUserTopic && string.IsNullOrWhiteSpace(sharedSummary))
                sharedSummary = reply.Length > 60 ? reply[..60] + "…" : reply;

            return new ProactiveGenerationResult(
                Reply: reply,
                TtsText: string.IsNullOrWhiteSpace(ttsText) ? null : ttsText,
                Skipped: false,
                SelectedTopic: selection.Topic,
                SharedSummary: sharedSummary,
                AwarenessLevel: awarenessLevel,
                AwarenessSource: awarenessSource,
                StyleScopeKey: styleScopeKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Proactive style line generation skipped");
            return null;
        }
    }

    /// <summary>
    /// 偵測到使用者長時間中離後回到電腦前時，產生一句自然的自主關心與離開期間重點彙報。
    /// </summary>
    public async Task<(string? Reply, string? TtsText)> GenerateWelcomeBackLineAsync(
        string conversationId,
        WelcomeBackEvent ev,
        IReadOnlyList<string> pinnedTitles,
        IReadOnlyList<string> awayUpdates,
        CancellationToken ct = default,
        string? activeWindow = null)
    {
        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();
        if (string.IsNullOrWhiteSpace(activeStylePrompt))
            return (null, null);

        var recent = await _store.GetChatMessagesAsync(conversationId, 20, ct);
        var history = BuildRecentHistory(recent, maxMessages: 10, perMessageMaxChars: 350, totalMaxChars: 3500);
        var pinnedText = pinnedTitles.Count == 0 ? "(None)" : string.Join(", ", pinnedTitles);
        var updatesText = awayUpdates.Count == 0 ? "(None)" : string.Join("; ", awayUpdates);
        var awayMins = Math.Max(1, (int)Math.Round(ev.AwayDuration.TotalMinutes));
        var awayLabel = awayMins >= 60 ? $"{awayMins / 60} 小時 {awayMins % 60} 分鐘" : $"{awayMins} 分鐘";

        var ttsEnabled = _tts.IsEnabled && _tts.ChatMode != "notify_only";
        var sys = MainAgentPrompts.BuildWelcomeBackSystemPrompt(
            activeStylePrompt,
            _tts.ChatMode == "full",
            ev.AudioWasPlaying,
            _tts.TargetLang,
            GetReplyLanguage(),
            ttsEnabled);
        var user = MainAgentPrompts.BuildWelcomeBackUserPrompt(
            DateTimeOffset.Now,
            awayLabel,
            ev.Reason.ToString(),
            updatesText,
            pinnedText,
            string.IsNullOrWhiteSpace(history) ? "(None)" : history,
            activeWindow);

        try
        {
            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", user)
            }, new LlmCallOptions("MainAgent.WelcomeBack", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), ct);

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
            _logger.LogDebug(ex, "Welcome-back line generation skipped");
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
            // Map notification to "user" role with [System Notification] prefix so arriving background
            // notifications never mutate and invalidate the previously cached assistant message block.
            var role = m.Role == "assistant" ? "assistant" : "user";
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
            // Never anchor solely on assistant messages (e.g. after consecutive proactive check-ins):
            // walk back to include the nearest preceding user message if within budget.
            if (contents[startIdx].Role == "assistant")
            {
                int back = startIdx - 1;
                int extraSum = sum;
                while (back >= 0
                       && eligible.Count - back <= maxMessages
                       && extraSum + contents[back].Content.Length <= totalMaxChars)
                {
                    extraSum += contents[back].Content.Length;
                    if (contents[back].Role == "user")
                    {
                        startIdx = back;
                        break;
                    }
                    back--;
                }
            }
            s_historyAnchors[anchorKey] = eligible[startIdx].Id;
        }

        var normalized = new List<ModelMessage>(contents.Count - startIdx + 1);
        for (int i = startIdx; i < contents.Count; i++)
        {
            var (role, content) = contents[i];
            if (normalized.Count == 0 && role == "assistant")
            {
                // Preserve leading assistant/proactive turns instead of silently dropping all history:
                // LLM chat APIs require the first non-system message to have role="user".
                normalized.Add(new ModelMessage("user", "[Session resumed / proactive check-in]"));
            }
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
