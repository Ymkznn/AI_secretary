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

    public static string BuildUiBatchTranslateSystemPrompt(string targetLanguage)
        => $"Translate every JSON string value in the input object into '{targetLanguage}'. " +
           "Keep ALL JSON keys unchanged, preserve ALL format placeholders like {0}, {1}, {2} and escape sequences verbatim. " +
           "Output ONLY a valid JSON object with the exact same keys (no markdown fences).";

    public static string BuildAutoStateTransitionSystemPrompt(string skillsSummary)
        => $$$"""
            You are StateMachineAgent, an autonomous zero-hook background state machine.
            Given a raw window/page sensor signal, decide:
            1. A short snake_case `state_name` describing the user's current context (open-ended, invented by you).
            2. `steps`: an array of 0-2 safe, non-destructive atomic skill calls to execute immediately on entering this state.

            Strict rules:
            - For video/music/streaming, AI chat/search tools (e.g., YouTube, Gemini, ChatGPT, Google Search), social feeds, general reading, or ordinary browsing: ALWAYS return `"steps": []` (stay completely silent).
            - Only emit non-empty `steps` when background preparation is clearly useful and non-intrusive (for example, on a login/2FA/verification code entry page, call `{"skill":"manage_mail","args":{"action":"sync","source":"all","boost_seconds":180}}`).
            - NEVER emit mutating/destructive/form-submitting/mail-sending actions.
            - Reply with ONLY raw JSON (no markdown fences):
            {"state_name": "short_snake_case", "steps": [{"skill": "skill_name", "args": {}}]}

            Available read/sync skills:
            {{{skillsSummary}}}
            """;

    public static string BuildAutoStateTransitionUserPrompt(
        string triggerKey,
        string processName,
        string windowTitle,
        string? url,
        int inputCount,
        IReadOnlyList<string> fieldLabels)
    {
        var fieldsText = fieldLabels.Count == 0 ? "(none)" : string.Join(", ", fieldLabels.Take(12));
        var urlLine = string.IsNullOrWhiteSpace(url) ? "" : $"\nURL: {url}";
        return $"TriggerKey: {triggerKey}\nProcess: {processName}\nTitle: {windowTitle}{urlLine}\nVisibleInputs: {inputCount}\nFieldLabels: {fieldsText}";
    }

    public static string BuildWorkflowOptimizerSystemPrompt()
        => "You are an offline CPU-style workflow branch-predictor optimizer. " +
           "Analyze compact telemetry and output ONLY raw JSON: " +
           "{\"overlap_threshold\": 0.55..0.90, \"promote_min_counter\": 1..3, \"disable_ids\": [\"...\"], \"next_review_after_predictions\": 5..500, \"next_review_after_hours\": 1..720, \"note\": \"concise 1-sentence reason\"}. " +
           "Raise overlap_threshold if mispredictions are high; lower it if hit rate is high and workflows are under-triggered. " +
           "Set shorter next_review_after_* when tuning is unstable or samples are low, and longer intervals when hit rate is already stable to save tokens. " +
           "Only disable workflows with repeated misses (MissCount >= 3 and MissCount > HitCount).";

    public static string BuildBrowserFormSuggestPrompt(BrowserFormContext form, string replyLang = "繁體中文")
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
            {"summary": "short explanation strictly in '{{replyLang}}'", "fills": [{"field_id": "...", "value": "..."}]}
            """;
    }

    public static string BuildHolidaySyncSystemPrompt()
        => """
            You are HolidaySyncAgent. Your sole job is to collect official national public holidays
            (including observed or substitute holidays) for a given ISO 3166-1 alpha-2 country code and year.

            Available actions (reply with ONLY a single JSON object, no markdown):
            1. Fetch a public URL: {"action": "web_fetch", "url": "https://..."}
            2. Finish with verified dates: {"action": "done", "dates": ["YYYY-MM-DD", ...]}

            Workflow:
            - First call web_fetch on a reliable public holiday source, such as
              https://date.nager.at/api/v3/PublicHolidays/{year}/{countryCode}
              or https://www.timeanddate.com/holidays/
            - Inspect the fetched data, fetch another authoritative page if needed, then return action "done"
              with all verified YYYY-MM-DD dates for the requested year.
            """;

    public static string BuildHolidaySyncUserPrompt(string countryCode, int year)
        => $"Find all official national public holidays for country '{countryCode}' in year {year}.";

    public static string BuildHolidaySyncWebFetchObservation(string url, string fetched)
        => $"web_fetch result for {url}:\n{fetched}";

    public const string HolidaySyncRetryPrompt =
        "Please either call web_fetch with a valid https URL or return action \"done\" with the YYYY-MM-DD dates array.";

    public static string BuildMemoryReflectionSystemPrompt()
        => """
            You are MemoryReflectionAgent. After an AI decision or memory-worthy chat finishes, inspect the trace
            and existing recalled facts to see if any reusable environment knowledge, user habit/preference, or
            correction to an outdated/contradicted fact was discovered.

            Memory lifecycle rules:
            - Always provide a short snake_case `memory_key` (e.g., "sleep_schedule", "mail_folder:expense", "reply_style") so repeated observations reinforce the same key instead of duplicating.
            - `state`: use "Confirmed" for directly observed tool results or explicit user rules; use "Candidate" for tentative/inferred chat observations (2nd occurrence auto-promotes to Confirmed).
            - `ttl_hours`: optional integer for temporary conditions (e.g., "these few days", "today only"); omit or null for long-term memory.
            - Do NOT record one-off transient values (OTP codes, single email body, greetings, jokes).
            - Prefer `update` or `delete` when an existing fact is outdated or contradicted.
            - If nothing reusable or corrective was learned, return {"ops": []}.
            - Output ONLY raw JSON (max 2 ops, no markdown fences):
            {"ops": [{"op": "record|update|delete", "fact_id": "for update/delete", "memory_key": "snake_case_key", "category": "open_category_tag", "state": "Confirmed|Candidate", "ttl_hours": null, "summary": "concise summary", "detail": "clear reusable detail", "evidence": "short basis"}]}
            """;

    public static string BuildMemoryReflectionUserPrompt(
        string userPrompt,
        IReadOnlyList<string> completedSteps,
        IReadOnlyList<string> observations,
        string finalReply,
        string existingFactsBlock)
        => $"""
            User request: {userPrompt}
            Executed steps: {string.Join(" -> ", completedSteps)}
            Observations:
            {string.Join("\n", observations)}
            Final reply: {finalReply}

            Existing recalled facts:
            {existingFactsBlock}
            """;
}
