using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Prompts;

/// <summary>
/// 集中管理背景自動化、UI 動態語系翻譯、離線最佳化與瀏覽器表單建議之系統提示詞。
/// </summary>
public static class SystemAutomationPrompts
{
    public static string BuildUiTranslateSystemPrompt(string targetLanguage)
        => $"Translate the UI template into '{targetLanguage}'. " +
           "Preserve ALL format placeholders like {0}, {1}, {2} and newlines verbatim. Output ONLY the translated string.";

    public static string BuildWindowClassifierSystemPrompt()
        => "Classify the user's current window into exact JSON: {\"intent\": \"otp_wait\" | \"form_fill\" | \"media_or_idle\"}. " +
           "Use \"otp_wait\" for login/2FA/verification code entry pages in ANY language. " +
           "Use \"form_fill\" only for multi-field applications, surveys, registration, or expense forms. " +
           "Use \"media_or_idle\" for videos, streams, music, reading, chat, or general browsing.";

    public static string BuildWindowClassifierUserPrompt(string processName, string windowTitle, IReadOnlyList<string> fieldLabels)
    {
        var fieldsText = fieldLabels.Count == 0 ? "(none)" : string.Join(", ", fieldLabels.Take(10));
        return $"Process: {processName}\nTitle: {windowTitle}\nVisible inputs: {fieldsText}";
    }

    public static string BuildWorkflowOptimizerSystemPrompt()
        => "You are an offline CPU-style workflow branch-predictor optimizer. " +
           "Analyze compact telemetry and output ONLY raw JSON: " +
           "{\"overlap_threshold\": 0.55..0.90, \"promote_min_counter\": 1..3, \"disable_ids\": [\"...\"], \"next_review_after_predictions\": 5..500, \"next_review_after_hours\": 1..720, \"note\": \"concise 1-sentence reason\"}. " +
           "Raise overlap_threshold if mispredictions are high; lower it if hit rate is high and workflows are under-triggered. " +
           "Set shorter next_review_after_* when tuning is unstable or samples are low, and longer intervals when hit rate is already stable to save tokens. " +
           "Only disable workflows with repeated misses (MissCount >= 3 and MissCount > HitCount).";

    public static string BuildBrowserFormSuggestPrompt(BrowserFormContext form)
    {
        var fieldsBlock = string.Join("\n", form.Fields.Take(30).Select(f =>
            $"- field_id={f.FieldId} | label={f.Label} | type={f.ControlType} | current={f.CurrentValue ?? ""}" +
            (f.Options is { Count: > 0 } ? $" | options=[{string.Join(", ", f.Options.Take(10))}]" : "")));

        return $$"""
            The user is filling out a web form and asked for field suggestions (output suggestions JSON only; NEVER submit):
            Page Title: {{form.Title}}
            Page URL: {{form.Url}}
            Fields:
            {{fieldsBlock}}

            Use <persistent_memory> and <user_preferences> to propose values and return raw JSON only (no Markdown fence):
            {"summary": "short explanation in user reply language", "fills": [{"field_id": "...", "value": "..."}]}
            """;
    }
}
