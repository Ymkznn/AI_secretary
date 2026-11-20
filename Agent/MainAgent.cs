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
        var historyMessages = BuildHistoryModelMessages(recent, userPrompt, maxMessages: 16, perMessageMaxChars: 3000, totalMaxChars: 20000);

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
            activeWindowContext);

        // Fast path: 2-bit branch prediction for repeated read-only single-step intents (skips MainAgent routing LLM).
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

                if (onDispatchBackground is not null)
                {
                    onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", predSkill, "Predicted"));
                    await onReplyAsync(predAck, new List<MailSourceRef>());

                    onDispatchBackground(async (bgCt, bgReplyAsync, bgDirectTtsText, bgChoices) =>
                    {
                        var (handled, summary, sources) = await _worker.ExecuteSingleSkillAsync(
                            conversationId, userPrompt, predSkill, predArgs, targetMailId, confirmAsync,
                            async s => { onStageChanged?.Invoke("worker", s); await onStatusAsync(s); },
                            ModelTier.Simple, bgCt, bgDirectTtsText, bgChoices);
                        if (handled)
                        {
                            await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: true, estimatedTokensSaved: 350, bgCt);
                            onSingleSkillFinished?.Invoke(taskId);
                            onStageChanged?.Invoke("worker_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", predSkill));
                            await bgReplyAsync(summary, sources);
                            return;
                        }

                        await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: false, estimatedTokensSaved: 0, bgCt);
                        onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", "Simple"));
                        var plannerTaskId = resolveLogicalTaskId is not null
                            ? resolveLogicalTaskId("call_planner", null, userPrompt)
                            : taskId;
                        bool isDone = true;
                        await _planner.ExecuteGoalAsync(
                            conversationId, plannerTaskId, userPrompt, userPrompt, targetMailId, confirmAsync,
                            async s => { onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                            bgReplyAsync, onStageChanged, ModelTier.Simple, bgCt, bgDirectTtsText, bgChoices, done => isDone = done);
                        onPlannerTaskFinished?.Invoke(plannerTaskId, isDone);
                    });
                    return;
                }

                await onStatusAsync(predAck);
                onStageChanged?.Invoke("worker", _localizer.TextCached("StageWorkerDispatch", "MainAgent dispatched Worker ({0}, {1})", predSkill, "Predicted"));
                var (handledSyncPred, summarySyncPred, sourcesSyncPred) = await _worker.ExecuteSingleSkillAsync(
                    conversationId, userPrompt, predSkill, predArgs, targetMailId, confirmAsync,
                    s => onStatusAsync(s), ModelTier.Simple, ct, onDirectTtsText, onChoices);
                if (handledSyncPred)
                {
                    await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: true, estimatedTokensSaved: 350, ct);
                    onSingleSkillFinished?.Invoke(taskId);
                    onStageChanged?.Invoke("main_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", predSkill));
                    await onReplyAsync(summarySyncPred, sourcesSyncPred);
                    return;
                }

                await _workflowCache.RecordPredictionOutcomeAsync(predWf, hit: false, estimatedTokensSaved: 0, ct);
            }
        }

        var routeMessages = new List<ModelMessage>(2 + historyMessages.Count)
        {
            new("system", effectiveSystemPrompt)
        };
        routeMessages.AddRange(historyMessages);
        routeMessages.Add(new("user", dynamicUserPrompt));

        var routeResp = await _model.CompleteAsync(routeMessages, ModelTier.Simple, ct);

        await _sessions.RecordTokensAsync(mainSession, routeResp.EstimatedTokens, ct: ct);

        var (action, targetTaskId, closeTaskId, tier, directReply, ack, ttsText, choices, skill, args, goal) = ParseRoute(routeResp.Content, userPrompt);
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
                    var (handled, summary, sources) = await _worker.ExecuteSingleSkillAsync(
                        conversationId, userPrompt, skill, args, targetMailId, confirmAsync,
                        async s => { onStageChanged?.Invoke("worker", s); await onStatusAsync(s); },
                        tier, bgCt, bgDirectTtsText, bgChoices);
                    if (handled)
                    {
                        await _workflowCache.RecordObservedSingleSkillAsync(conversationId, userPrompt, skill, args, bgCt);
                        onSingleSkillFinished?.Invoke(taskId);
                        onStageChanged?.Invoke("worker_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", skill));
                        await bgReplyAsync(summary, sources);
                        return;
                    }

                    onStageChanged?.Invoke("planner", _localizer.TextCached("StagePlannerPlanning", "MainAgent -> Planner ({0})...", tier.ToString()));
                    var plannerTaskId = resolveLogicalTaskId is not null
                        ? resolveLogicalTaskId("call_planner", targetTaskId, effectiveGoal)
                        : taskId;
                    bool isDone = true;
                    await _planner.ExecuteGoalAsync(
                        conversationId,
                        plannerTaskId,
                        effectiveGoal,
                        userPrompt,
                        targetMailId,
                        confirmAsync,
                        async s => { onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                        bgReplyAsync,
                        onStageChanged,
                        tier,
                        bgCt,
                        bgDirectTtsText,
                        bgChoices,
                        done => isDone = done);
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
            var (handledSync, summarySync, sourcesSync) = await _worker.ExecuteSingleSkillAsync(
                conversationId, userPrompt, skill, args, targetMailId, confirmAsync, workerStatusAsync, tier, ct, onDirectTtsText, onChoices);
            if (handledSync)
            {
                await _workflowCache.RecordObservedSingleSkillAsync(conversationId, userPrompt, skill, args, ct);
                onSingleSkillFinished?.Invoke(taskId);
                onStageChanged?.Invoke("main_done", _localizer.TextCached("StageWorkerDone", "Worker finished {0}", skill));
                await onReplyAsync(summarySync, sourcesSync);
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
                bool isDone = true;
                await _planner.ExecuteGoalAsync(
                    conversationId,
                    logicalTaskId,
                    effectiveGoal,
                    userPrompt,
                    targetMailId,
                    confirmAsync,
                    async s => { onStageChanged?.Invoke("planner", s); await onStatusAsync(s); },
                    bgReplyAsync,
                    onStageChanged,
                    tier,
                    bgCt,
                    bgDirectTtsText,
                    bgChoices,
                    done => isDone = done);
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
        bool syncDone = true;
        await _planner.ExecuteGoalAsync(
            conversationId,
            logicalTaskId,
            effectiveGoal,
            userPrompt,
            targetMailId,
            confirmAsync,
            plannerStatusAsync,
            onReplyAsync,
            onStageChanged,
            tier,
            ct,
            onDirectTtsText,
            onChoices,
            done => syncDone = done);
        onPlannerTaskFinished?.Invoke(logicalTaskId, syncDone);
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
    private (string Action, string? TargetTaskId, string? CloseTaskId, ModelTier Tier, string Reply, string Ack, string TtsText, List<string> Choices, string Skill, JsonElement Args, string Goal) ParseRoute(string raw, string fallbackGoal)
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
            return (action.ToLowerInvariant(), targetTaskId, closeTaskId, tier, reply, ack, ttsText, choices, skill, args, goal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MainAgent route parse fallback to planner");
            return ("call_planner", null, null, ModelTier.Complex, "", "", "", new List<string>(), "", default, fallbackGoal);
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
            var keepAttachments = m.Role == "user" && i >= eligible.Count - 6 && IsFollowedByError(eligible, i);
            var content = AttachmentExtractor.AttachmentPattern.Replace(
                rawContent,
                match =>
                {
                    var name = Path.GetFileName(match.Groups[1].Value.Trim());
                    if (name.StartsWith("screen_", StringComparison.OrdinalIgnoreCase))
                        return $"[History screenshot: {name}]";
                    return keepAttachments ? match.Value : $"[History attachment: {name}]";
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
            // Re-anchor with headroom (~60% of both budgets) so the same prefix can be reused for several turns.
            var targetMessages = Math.Max(1, (int)Math.Ceiling(maxMessages * 0.6));
            var targetChars = (int)(totalMaxChars * 0.6);
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
