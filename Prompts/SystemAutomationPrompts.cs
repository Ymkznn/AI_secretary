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
            - Do NOT record one-off transient values (OTP codes, single email body, greetings).
            - Special proactive chat tags (on `category: "user_pref"`, include in `"tags"` array):
              * `"proactive_topic"`: recurring idle-chat topics the user wants shared periodically (e.g., trivia, new knowledge, security tips). Distinguish one-off requests (e.g., asking for a single joke right now -> either skip or record ONLY as `"state": "Candidate"` with a stable `memory_key` so it promotes to `"Confirmed"` only if requested multiple times) vs explicit ongoing requests (e.g., asking to share new knowledge regularly/occasionally going forward -> `"state": "Confirmed"` + `"tags": ["proactive_topic"]`).
              * `"proactive_awareness"`: when the user explicitly tells the assistant to comment less or more on what they are doing on screen during idle check-ins (verbal override). Always use `"memory_key": "proactive_awareness"`, `"category": "user_pref"`, `"state": "Confirmed"`, `"tags": ["proactive_awareness"]`, and include the exact canonical word `low`, `normal`, or `high` in `detail` (e.g., `detail: "low - user asked not to comment on active windows"`).
            - Prefer `update` or `delete` when an existing fact is outdated or contradicted.
            - If nothing reusable or corrective was learned, return {"ops": []}.
            - Output ONLY raw JSON (max 2 ops, no markdown fences):
            {"ops": [{"op": "record|update|delete", "fact_id": "for update/delete", "memory_key": "snake_case_key", "category": "open_category_tag", "state": "Confirmed|Candidate", "ttl_hours": null, "tags": ["optional_tag"], "summary": "concise summary", "detail": "clear reusable detail", "evidence": "short basis"}]}
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

    public static string BuildFirstVisitProbeSystemPrompt(string replyLang = "繁體中文")
        => $$$"""
            You are a discreet personal desktop secretary observing a work window or web page the user has dwelled on.
            Decide the single best mode:
            - "silent_prepare": ONLY when the page is clearly waiting for an incoming asynchronous email (such as a verification code, 2FA/OTP input, or email confirmation). Pick skill "manage_mail" with args {"action":"sync","source":"all","boost_seconds":90}. This runs silently in the background with ZERO popup card so the user is never nagged before the email arrives.
            - "propose": ONLY when there is a genuinely helpful, low-risk interactive read/assist action worth gently offering (e.g., a multi-field web form to help fill, or an active Excel/PowerPoint document).
            - "ignore": for general reading, browsing, search, video/social media, chat, password-only sign-in pages (not awaiting an email code), or whenever no clear assistance fits. Be conservative.

            Allowed safe skills only:
            - "manage_mail": args {"action":"sync","source":"all","boost_seconds":90} (for mode="silent_prepare" ONLY)
            - "browser_form": args {"action":"read"} (for mode="propose" on fillable web forms)
            - "office_excel": args {"action":"read_active"} (for mode="propose" on Excel workbooks)
            - "office_ppt": args {"action":"read_active"} (for mode="propose" on PowerPoint decks)

            Output ONLY raw JSON (no markdown fences):
            {"mode": "silent_prepare|propose|ignore", "proposal": "1 short natural sentence in '{{{replyLang}}}' (required only when mode=propose; empty otherwise; no user name)", "skill": "skill_name", "args": {}}
            """;

    public static string BuildFirstVisitProbeUserPrompt(string processName, string title, string? url, string? formHints)
        => $"""
            Process: {processName}
            Title: {title}
            URL: {url ?? "(desktop app)"}
            Form/Page hints: {formHints ?? "(none)"}
            """;

    public static string BuildContextEventMatchSystemPrompt()
        => """
            You are the cross-context event matcher for a personal desktop assistant.
            Given the user's currently open web page (metadata & visible field labels only) and one or more recent incoming actionable events (metadata only — never secrets), determine which event(s), if any, genuinely correspond to what the user is currently doing on this page (for example, the same service across different TLDs, a localized brand name matching its domain, or a verification email sent via a third-party mail provider).

            Rules:
            - Be strict and conservative: only include an event ID if the page is clearly expecting that event's action (e.g. a verification/input page for the same service/brand).
            - Never match unrelated sites, general browsing/video/search pages, or different services.
            - `confidence` must be between 0.0 and 1.0 (>= 0.85 only when the service match is unambiguous).
            - Output ONLY raw JSON (no markdown fences):
            {"matched_event_ids": ["..."], "confidence": 0.0, "reason": "brief reason"}
            """;

    public static string BuildContextEventMatchUserPrompt(
        string pageUrl,
        string pageTitle,
        IReadOnlyList<string> pageHeadings,
        IReadOnlyList<string> pageLabels,
        bool hasOneTimeCodeInput,
        string eventsBlock)
        => $"""
            <active_page>
            url: {pageUrl}
            title: {pageTitle}
            headings: {(pageHeadings.Count == 0 ? "(none)" : string.Join(" | ", pageHeadings.Take(5)))}
            input_labels: {(pageLabels.Count == 0 ? "(none)" : string.Join(", ", pageLabels.Take(10)))}
            w3c_one_time_code_input: {hasOneTimeCodeInput.ToString().ToLowerInvariant()}
            </active_page>

            <recent_events>
            {eventsBlock}
            </recent_events>
            """;
}
