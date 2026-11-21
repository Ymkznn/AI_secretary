using System.Text.Json;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Agent;

/// <summary>Worker 執行工具後回報給 MainAgent 或 Planner 的純資料結果（不含對使用者的自然語言回覆）。</summary>
public sealed record WorkerReport(
    bool Handled,
    List<string> Observations,
    List<MailSourceRef> Sources,
    List<string> CompletedSteps);

/// <summary>
/// 純工具執行代理（Worker）：只負責呼叫內建技能（<see cref="SkillRegistry"/>）或自訂技能（<see cref="CustomSkillService"/>），
/// 絕不呼叫 LLM，也絕不直接與使用者對話；執行完畢後僅將原始結果回報給呼叫端（<see cref="PlannerAgent"/> 或 <see cref="MainAgent"/>）。
/// </summary>
public sealed class WorkerAgent
{
    private readonly CustomSkillService _customSkills;
    private readonly GuardService _guard;
    private readonly SkillRegistry _skillRegistry;
    private readonly UiLocalizer _localizer;
    private readonly ILogger<WorkerAgent> _logger;

    public WorkerAgent(
        CustomSkillService customSkills,
        GuardService guard,
        SkillRegistry skillRegistry,
        UiLocalizer localizer,
        ILogger<WorkerAgent> logger)
    {
        _customSkills = customSkills;
        _guard = guard;
        _skillRegistry = skillRegistry;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>
    /// 執行單一技能並回傳原始結果給呼叫端（零 LLM 呼叫）；若該技能未被處理則 Handled=false。
    /// </summary>
    public async Task<WorkerReport> ExecuteSingleSkillRawAsync(
        string conversationId,
        string userPrompt,
        string skillName,
        JsonElement args,
        string? targetMailId,
        Func<string, string, Task<bool>> confirmAsync,
        Func<string, Task> onStatusAsync,
        CancellationToken ct = default,
        string? taskId = null)
    {
        var sources = new List<MailSourceRef>();
        var observations = new List<string>();
        var completedSteps = new List<string>();

        await RunStepAsync(
            conversationId, userPrompt, skillName.Trim(), args, targetMailId,
            confirmAsync, onStatusAsync, sources, observations, completedSteps, ct, taskId);

        return new WorkerReport(observations.Count > 0, observations, sources, completedSteps);
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
            _logger.LogDebug(ex, "Worker step {Skill} failed", record.Name);
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
    internal static void CleanupTempScreenshots(IReadOnlyList<string> observations)
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

        var tts = m.Groups[1].Value.Trim();
        var clean = (text[..m.Index] + text[(m.Index + m.Length)..]).Trim();
        return (clean, string.IsNullOrWhiteSpace(tts) ? null : tts);
    }

    /// <summary>從回覆中抽離 `[CHOICES: a | b | c]` 快捷選項標記。</summary>
    internal static (string CleanText, List<string> Choices) ExtractAndStripChoices(string raw)
    {
        var text = (raw ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:^|\r?\n)\s*\[CHOICES:\s*(.+?)\s*\]\s*(?=\r?\n\[(?:TTS_TEXT|TASK_STATUS):|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return (text, new List<string>());

        var choices = m.Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(4)
            .ToList();
        var clean = (text[..m.Index] + text[(m.Index + m.Length)..]).Trim();
        return (clean, choices);
    }
}
