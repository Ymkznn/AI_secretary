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
    private readonly UiLocalizer _localizer;
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
        UiLocalizer localizer,
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
        _localizer = localizer;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 執行單一技能並以使用者指定語系彙整回覆；若該技能未被處理則回傳 Handled=false 以便退回 Planner。
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
        var basic = _skillRegistry.FindBasic(skillName);
        if (basic is null)
        {
            await ExecuteCustomStepAsync(skillName, args, observations, completedSteps, taskId, onStatusAsync, ct);
            return;
        }

        var (record, handler) = basic.Value;
        var check = _guard.CheckSkillExecution(record.Name);
        if (!check.Allowed)
        {
            observations.Add($"Skill {record.Name} denied by Guard: {check.Reason}");
            return;
        }

        try
        {
            await handler.ExecuteAsync(new SkillExecutionContext
            {
                SkillName = record.Name,
                ConversationId = conversationId,
                UserPrompt = userPrompt,
                Args = args,
                TargetMailId = targetMailId,
                ConfirmAsync = confirmAsync,
                OnStatusAsync = onStatusAsync,
                LocalizeAsync = (k, en, a) => _localizer.TextAsync(k, en, a, ct),
                Sources = sources,
                Observations = observations,
                CompletedSteps = completedSteps,
                Ct = ct
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            observations.Add($"{record.Name} failed: {ex.Message}");
        }
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
        var check = _guard.CheckSkillExecution(skill.Name);
        if (!check.Allowed)
        {
            observations.Add($"Custom skill disabled: {skill.Name}");
            return;
        }

        var statusMsg = await _localizer.TextAsync("CustomSkillRunning", "Running {0}...", new object?[] { skill.Name }, ct);
        await onStatusAsync(statusMsg);
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
            foreach (System.Text.RegularExpressions.Match m in AttachmentExtractor.AttachmentPattern.Matches(obs))
            {
                var path = m.Groups[1].Value.Trim();
                if (Path.GetFileName(path).StartsWith("screen_", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    try { File.Delete(path); } catch { }
                }
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
