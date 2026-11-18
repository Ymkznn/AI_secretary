using System.Text.Json;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// 執行代理（Worker）：負責派發執行內建技能（<see cref="SkillRegistry"/>）或自訂技能（<see cref="CustomSkillService"/>），
/// 並支援由 <see cref="MainAgent"/> 直接派發的單步快速執行與結果彙整。
/// </summary>
public sealed class WorkerAgent
{
    private readonly SqliteStore _store;
    private readonly CustomSkillService _customSkills;
    private readonly GuardService _guard;
    private readonly IModelGateway _model;
    private readonly ContextSessionManager _sessions;
    private readonly StyleService _styles;
    private readonly TtsService _tts;
    private readonly SkillRegistry _skillRegistry;
    private readonly AppSettingsService _settings;
    private readonly ILogger<WorkerAgent> _logger;

    public WorkerAgent(
        SqliteStore store,
        CustomSkillService customSkills,
        GuardService guard,
        IModelGateway model,
        ContextSessionManager sessions,
        StyleService styles,
        TtsService tts,
        SkillRegistry skillRegistry,
        AppSettingsService settings,
        ILogger<WorkerAgent> logger)
    {
        _store = store;
        _customSkills = customSkills;
        _guard = guard;
        _model = model;
        _sessions = sessions;
        _styles = styles;
        _tts = tts;
        _skillRegistry = skillRegistry;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 執行單一技能並彙整成繁體中文回覆；若該技能未被處理則回傳 Handled=false 以便退回 Planner。
    /// </summary>
    public async Task<(bool Handled, string Summary, List<MailSourceRef> Sources)> ExecuteSingleSkillAsync(
        string conversationId,
        string userPrompt,
        string skillName,
        JsonElement args,
        string? targetMailId,
        Func<string, string, Task<bool>> confirmAsync,
        Func<string, Task> onStatusAsync,
        ModelTier tier = ModelTier.Simple,
        CancellationToken ct = default,
        Action<string>? onDirectTtsText = null,
        Action<IReadOnlyList<string>>? onChoices = null)
    {
        var sources = new List<MailSourceRef>();
        var observations = new List<string>();
        var completedSteps = new List<string>();

        await RunStepAsync(
            conversationId, userPrompt, skillName.Trim(), args, targetMailId,
            confirmAsync, onStatusAsync, sources, observations, completedSteps, ct);

        if (observations.Count == 0)
            return (false, "", sources);

        var activeStylePrompt = _tts.GetActiveStylePrompt() ?? _styles.GetActiveStylePrompt();

        // 若未啟用角色語氣，確定性操作直接回傳觀察結果以節省一次 LLM 呼叫。
        var lower = skillName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(activeStylePrompt) &&
            lower is "launch_app" or "manage_schedule" or "manage_facts" or "manage_custom_skill" or "create_schedule" or "update_schedule" or "delete_schedule" or "create_reminder" or "dismiss_reminder" or "update_fact" or "delete_fact" or "create_skill" or "delete_skill" or "set_quiet_mode" or "sync_mail" or "fill_otp" or "push_notification")
        {
            return (true, string.Join("\n", observations), sources);
        }

        var mainSession = await _sessions.GetOrRotateSessionAsync(conversationId, "Main", ct: ct);
        var prefFacts = await _store.RecallRelevantUserPrefFactsAsync(conversationId, userPrompt, 5, ct);
        var prefContext = MainAgentPrompts.FormatPreferencesContext(prefFacts);

        var replyLang = string.IsNullOrWhiteSpace(_settings.Current.Llm.ReplyLanguage)
            ? "繁體中文"
            : _settings.Current.Llm.ReplyLanguage.Trim();
        var singleSysPrompt = PlannerPrompts.BuildFinalReplySystemPrompt(
            activeStylePrompt,
            _tts.IsEnabled,
            _tts.ChatMode,
            includeTaskStatus: false,
            _tts.TargetLang,
            replyLang);

        var finalResp = await _model.CompleteAsync(new[]
        {
            new ModelMessage("system", singleSysPrompt),
            new ModelMessage("user", PlannerPrompts.BuildSingleSkillUserPrompt(prefContext, userPrompt, observations))
        }, tier, ct);

        CleanupTempScreenshots(observations);
        await _sessions.RecordTokensAsync(mainSession, finalResp.EstimatedTokens, ct: ct);
        var (cleanSingle0, singleChoices) = ExtractAndStripChoices(finalResp.Content);
        var (cleanSingle, singleTtsText) = ExtractAndStripTtsText(cleanSingle0);
        if (singleChoices.Count > 0)
            onChoices?.Invoke(singleChoices);
        if (!string.IsNullOrWhiteSpace(singleTtsText))
            onDirectTtsText?.Invoke(singleTtsText);
        return (true, cleanSingle, sources);
    }

    /// <summary>
    /// 執行單一規劃步驟（由 <see cref="SkillRegistry"/> 派發內建技能，或轉交 <see cref="CustomSkillService"/> 執行自訂技能）。
    /// </summary>
    public async Task RunStepAsync(
        string conversationId,
        string userPrompt,
        string skillName,
        JsonElement args,
        string? targetMailId,
        Func<string, string, Task<bool>> confirmAsync,
        Func<string, Task> onStatusAsync,
        List<MailSourceRef> sources,
        List<string> observations,
        List<string> completedSteps,
        CancellationToken ct,
        string? taskId = null)
    {
        var builtIn = _skillRegistry.Find(skillName);
        if (builtIn is null)
        {
            await ExecuteCustomStepAsync(skillName, args, observations, completedSteps, taskId, onStatusAsync, ct);
            return;
        }

        var check = _guard.CheckSkillExecution(builtIn.Name);
        if (!check.Allowed)
        {
            observations.Add($"Skill {skillName} denied by Guard: {check.Reason}");
            return;
        }

        await builtIn.ExecuteAsync(new SkillExecutionContext
        {
            ConversationId = conversationId,
            UserPrompt = userPrompt,
            Args = args,
            TargetMailId = targetMailId,
            ConfirmAsync = confirmAsync,
            OnStatusAsync = onStatusAsync,
            Sources = sources,
            Observations = observations,
            CompletedSteps = completedSteps,
            Ct = ct
        });
    }

    private async Task ExecuteCustomStepAsync(
        string skillName,
        JsonElement args,
        List<string> observations,
        List<string> completedSteps,
        string? taskId,
        Func<string, Task> onStatusAsync,
        CancellationToken ct)
    {
        var skill = await _customSkills.FindAsync(skillName, ct);
        if (skill is null)
        {
            observations.Add($"Custom skill not found: {skillName}");
            return;
        }
        if (!_guard.IsSkillEnabled(skill.Name))
        {
            observations.Add($"Custom skill disabled: {skill.Name}");
            return;
        }

        await onStatusAsync($"執行 {skill.Name}…");
        try
        {
            var extraArgs = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
            var output = await _customSkills.ExecuteAsync(skill, extraArgs, taskId, ct);
            observations.Add($"custom:{skill.Name} output:\n{output}");
            completedSteps.Add($"custom:{skill.Name}");
        }
        catch (Exception ex)
        {
            observations.Add($"custom:{skill.Name} failed: {ex.Message}");
        }
    }

    /// <summary>清理執行期間產生的暫存螢幕截圖（`screen_*`）。</summary>
    internal static void CleanupTempScreenshots(List<string> observations)
    {
        foreach (var obs in observations)
        {
            var idx = obs.IndexOf("[附件：", StringComparison.Ordinal);
            if (idx < 0) continue;
            var end = obs.IndexOf(']', idx);
            if (end <= idx) continue;
            var path = obs[(idx + 4)..end].Trim();
            if (Path.GetFileName(path).StartsWith("screen_", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    /// <summary>從回覆中抽離 `[TTS_TEXT: ...]` 語音台詞標記。</summary>
    internal static (string CleanText, string? TtsText) ExtractAndStripTtsText(string raw)
    {
        var text = (raw ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:^|\r?\n)\s*\[TTS_TEXT:\s*(.+?)\]\s*(?=$|\r?\n)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return (text, null);

        var ttsText = m.Groups[1].Value.Trim();
        var clean = (text[..m.Index] + text[(m.Index + m.Length)..]).Trim();
        return (clean, string.IsNullOrWhiteSpace(ttsText) ? null : ttsText);
    }

    /// <summary>從回覆中抽離 `[CHOICES: ...]` 氣泡選項清單（最多 4 項）。</summary>
    internal static (string CleanText, List<string> Choices) ExtractAndStripChoices(string raw)
    {
        var text = (raw ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:^|\r?\n)\s*\[CHOICES:\s*(.+?)\]\s*(?=$|\r?\n)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return (text, new List<string>());

        var choices = m.Groups[1].Value
            .Split(new[] { '|', '｜' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(4)
            .ToList();
        var clean = (text[..m.Index] + text[(m.Index + m.Length)..]).Trim();
        return (clean, choices);
    }
}
