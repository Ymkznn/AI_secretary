using System.Collections.Concurrent;
using System.Text;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace AiSecretary.Desktop.Services;

public sealed record AttentionContextResult(
    string Source,         // "browser" | "desktop"
    int AgeSeconds,
    string Confidence,     // "high" | "medium" | "low"
    bool IsStale,
    string MatchedCue,
    string TargetLabel,
    string FormattedBlock,
    IReadOnlyList<string> DisambiguationCandidates);

/// <summary>
/// L1 deictic attention prefetch service:
/// - Triggered ONLY when <see cref="DeicticDetector"/> detects a deterministic screen/selection reference.
/// - Zero-round-trip for window/page cache (title, url, process, headings, excerpt, form labels — NEVER field values or passwords).
/// - Strictly bounded 300ms selection fetch (browser extension / desktop UIA TextPattern.GetSelection); skips cleanly on timeout.
/// - Marks windows untouched &gt; 2 minutes as low-confidence stale and supplies disambiguation candidates instead of guessing.
/// - Logs every trigger to <c>PredictionLogs</c> (<c>source="deixis"</c>) and records 90-second implicit user corrections.
/// </summary>
public sealed class AttentionContextService
{
    public const int StaleThresholdSeconds = 120;
    public const int RecentChatSuppressionMinutes = 3;
    public const int MaxBlockChars = 1150; // ~500 tokens max
    private static readonly TimeSpan SelectionFetchTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ImplicitFeedbackWindow = TimeSpan.FromSeconds(90);

    private sealed record LastDeixisLog(long LogId, DateTimeOffset LoggedAtUtc);

    private readonly ActivityMonitorService _activity;
    private readonly BrowserFormBridgeService _browserBridge;
    private readonly AgentBlackboard _blackboard;
    private readonly SqliteStore _store;
    private readonly ILogger<AttentionContextService> _logger;
    private readonly ConcurrentDictionary<string, LastDeixisLog> _lastLogs = new(StringComparer.Ordinal);

    public AttentionContextService(
        ActivityMonitorService activity,
        BrowserFormBridgeService browserBridge,
        AgentBlackboard blackboard,
        SqliteStore store,
        ILogger<AttentionContextService> logger)
    {
        _activity = activity;
        _browserBridge = browserBridge;
        _blackboard = blackboard;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates whether the current user prompt is an implicit correction of a deixis prefetch made within the last 90 seconds,
    /// updating <c>PredictionLogs.ImplicitFeedback</c> accordingly.
    /// </summary>
    public async Task CheckImplicitFeedbackAsync(string conversationId, string userPrompt, CancellationToken ct = default)
    {
        if (!_lastLogs.TryGetValue(conversationId, out var last))
            return;

        var elapsed = DateTimeOffset.UtcNow - last.LoggedAtUtc;
        if (elapsed > ImplicitFeedbackWindow)
        {
            _lastLogs.TryRemove(conversationId, out _);
            return;
        }

        if (DeicticDetector.IsUserCorrectionSignal(userPrompt))
        {
            _lastLogs.TryRemove(conversationId, out _);
            await _store.UpdatePredictionLogFeedbackAsync(last.LogId, "user_corrected_within_90s", ct);
        }
        else if (elapsed >= TimeSpan.FromSeconds(3))
        {
            _lastLogs.TryRemove(conversationId, out _);
            await _store.UpdatePredictionLogFeedbackAsync(last.LogId, "no_correction", ct);
        }
    }

    public async Task<AttentionContextResult?> TryBuildL1ContextAsync(
        string conversationId,
        string userPrompt,
        string? targetMailId,
        IReadOnlyList<ChatMessageRecord> recentMessages,
        CancellationToken ct = default)
    {
        var detection = DeicticDetector.Detect(userPrompt, targetMailId);
        if (!detection.IsDeictic)
            return null;

        var target = _activity.GetLastExternalTarget();
        if (target is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        bool isBrowser = ActivityMonitorService.IsBrowserProcess(target.ProcessName);
        var cachedPage = isBrowser ? _activity.GetLastBrowserPageSignal() : null;

        // Only reuse cached page-signal if it matches the currently focused browser tab/title (prevents leaking Tab A into Tab B/incognito).
        bool cachedMatchesTab = cachedPage is not null &&
                                (!string.IsNullOrWhiteSpace(cachedPage.Title) &&
                                 target.Title.Contains(cachedPage.Title, StringComparison.OrdinalIgnoreCase));

        var effectiveCapturedAt = (cachedMatchesTab && cachedPage!.CapturedAt > target.CapturedAt)
            ? cachedPage.CapturedAt
            : target.CapturedAt;

        // Behavioral signal: if the assistant just replied within the last 3 minutes and the user has NOT
        // switched to an external window since that reply, "這個/上面" almost certainly refers to the chat reply, not the screen.
        if (ShouldSkipForRecentChat(effectiveCapturedAt, recentMessages, now))
            return null;

        int ageSec = Math.Max(0, (int)(now - effectiveCapturedAt).TotalSeconds);
        bool isStale = ageSec > StaleThresholdSeconds;
        var source = isBrowser ? "browser" : "desktop";
        var appDisplay = FormatAppDisplayName(target.ProcessName);
        var effectiveTitle = cachedMatchesTab && !string.IsNullOrWhiteSpace(cachedPage!.Title)
            ? cachedPage.Title
            : target.Title;

        if (isStale)
        {
            var candidates = BuildDisambiguationCandidates(appDisplay, effectiveTitle, recentMessages);
            var staleBlock = FormatStaleBlock(source, ageSec, target.ProcessName, effectiveTitle, candidates);
            return new AttentionContextResult(
                Source: source,
                AgeSeconds: ageSec,
                Confidence: "low",
                IsStale: true,
                MatchedCue: detection.MatchedCue ?? detection.Reason,
                TargetLabel: $"{appDisplay}:{Compact(effectiveTitle, 40)}",
                FormattedBlock: staleBlock,
                DisambiguationCandidates: candidates);
        }

        string? url = cachedMatchesTab ? cachedPage!.Url : null;
        IReadOnlyList<string> headings = cachedMatchesTab ? cachedPage!.Headings : Array.Empty<string>();
        string? excerpt = cachedMatchesTab ? cachedPage!.Excerpt : null;
        IReadOnlyList<string> formLabels = cachedMatchesTab && cachedPage!.Labels.Count > 0
            ? cachedPage.Labels
            : (target.FormFieldLabels ?? Array.Empty<string>());
        string? selectionText = null;
        string? focusedLabel = null;

        // 300ms-bounded selection fetch (skips cleanly on timeout; never reads passwords or input field values).
        if (isBrowser)
        {
            try
            {
                var liveSel = await _browserBridge.RequestActiveTabSelectionAsync(SelectionFetchTimeout, ct);
                if (liveSel is { Incognito: true })
                {
                    // Incognito tab: never leak cached or live page content.
                    url = null;
                    selectionText = null;
                    focusedLabel = null;
                    headings = Array.Empty<string>();
                    excerpt = null;
                    formLabels = Array.Empty<string>();
                }
                else if (liveSel is not null)
                {
                    if (!string.IsNullOrWhiteSpace(liveSel.Url)) url = liveSel.Url;
                    if (!string.IsNullOrWhiteSpace(liveSel.Title)) effectiveTitle = liveSel.Title;
                    if (!string.IsNullOrWhiteSpace(liveSel.SelectionText)) selectionText = liveSel.SelectionText;
                    if (!string.IsNullOrWhiteSpace(liveSel.FocusedLabel)) focusedLabel = liveSel.FocusedLabel;
                    if (liveSel.Headings is { Count: > 0 }) headings = liveSel.Headings;
                    if (!string.IsNullOrWhiteSpace(liveSel.Excerpt)) excerpt = liveSel.Excerpt;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Browser L1 selection fetch skipped");
            }
        }
        else
        {
            var deskSel = await _activity.TryReadDesktopSelectionAsync(target, SelectionFetchTimeout, ct);
            if (deskSel is not null)
            {
                selectionText = deskSel.SelectionText;
                focusedLabel = deskSel.FocusedElementLabel;
            }
        }

        string confidence = (!string.IsNullOrWhiteSpace(selectionText) || ageSec <= 60) ? "high" : "medium";
        var formatted = FormatFreshBlock(
            source,
            ageSec,
            confidence,
            target.ProcessName,
            effectiveTitle,
            url,
            selectionText,
            focusedLabel,
            headings,
            formLabels,
            excerpt);

        return new AttentionContextResult(
            Source: source,
            AgeSeconds: ageSec,
            Confidence: confidence,
            IsStale: false,
            MatchedCue: detection.MatchedCue ?? detection.Reason,
            TargetLabel: $"{appDisplay}:{Compact(effectiveTitle, 40)}",
            FormattedBlock: formatted,
            DisambiguationCandidates: Array.Empty<string>());
    }

    public async Task RecordDeixisOutcomeAsync(
        string conversationId,
        AttentionContextResult ctx,
        string userPrompt,
        string routeAction,
        string? skillName,
        CancellationToken ct = default)
    {
        string predictedAction;
        bool succeeded;
        long tokensSaved = 0;

        if (ctx.IsStale)
        {
            predictedAction = routeAction == "reply" ? "stale_disambiguate" : $"stale_{routeAction}";
            succeeded = routeAction == "reply";
        }
        else if (routeAction == "reply")
        {
            predictedAction = "l1_direct_reply";
            succeeded = true;
            tokensSaved = 450;
        }
        else if (routeAction == "run_skill" &&
                 (string.Equals(skillName, "browser_form", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(skillName, "inspect_screen", StringComparison.OrdinalIgnoreCase)))
        {
            predictedAction = $"escalated_l2:{skillName}";
            succeeded = true;
        }
        else if (routeAction == "call_planner")
        {
            predictedAction = "escalated_l2:planner";
            succeeded = true;
        }
        else
        {
            predictedAction = $"unused:{routeAction}" + (string.IsNullOrWhiteSpace(skillName) ? "" : $":{skillName}");
            succeeded = false;
        }

        var subjectKey = $"{ctx.Source}:{ctx.Confidence}:{Compact(ctx.MatchedCue, 24)}";
        var logId = await _store.InsertPredictionLogReturningIdAsync(
            source: "deixis",
            subjectKey: subjectKey,
            queryText: userPrompt,
            predictedAction: predictedAction,
            execSucceeded: succeeded,
            tokensSaved: tokensSaved,
            ct: ct);

        if (logId > 0)
            _lastLogs[conversationId] = new LastDeixisLog(logId, DateTimeOffset.UtcNow);
    }

    internal static bool ShouldSkipForRecentChat(
        DateTimeOffset externalCapturedAt,
        IReadOnlyList<ChatMessageRecord> recentMessages,
        DateTimeOffset now)
    {
        if (recentMessages.Count == 0)
            return false;

        var lastAssistant = recentMessages
            .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                                && !m.IsStreaming
                                && !string.IsNullOrWhiteSpace(m.Content));
        if (lastAssistant is null)
            return false;

        var lastAssistantAt = lastAssistant.RevealedAt ?? lastAssistant.CreatedAt;
        var sinceReply = now - lastAssistantAt;
        return sinceReply >= TimeSpan.Zero
               && sinceReply <= TimeSpan.FromMinutes(RecentChatSuppressionMinutes)
               && externalCapturedAt < lastAssistantAt;
    }

    internal static string FormatFreshBlock(
        string source,
        int ageSec,
        string confidence,
        string processName,
        string title,
        string? url,
        string? selectionText,
        string? focusedLabel,
        IReadOnlyList<string> headings,
        IReadOnlyList<string> formLabels,
        string? excerpt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<attention_context source=\"{source}\" age_sec=\"{ageSec}\" confidence=\"{confidence}\">");
        sb.AppendLine($"process: {processName}");
        sb.AppendLine($"title: {Compact(title, 120)}");
        if (!string.IsNullOrWhiteSpace(url))
            sb.AppendLine($"url: {Compact(url, 180)}");

        if (!string.IsNullOrWhiteSpace(selectionText))
            sb.AppendLine($"selected_text: {Compact(selectionText, 550)}");

        if (!string.IsNullOrWhiteSpace(focusedLabel))
            sb.AppendLine($"focused_label: {Compact(focusedLabel, 80)}");

        if (headings.Count > 0)
            sb.AppendLine($"headings: {Compact(string.Join(" | ", headings.Take(5)), 220)}");

        if (formLabels.Count > 0)
            sb.AppendLine($"form_field_labels: {Compact(string.Join(", ", formLabels.Take(10)), 150)}");

        if (!string.IsNullOrWhiteSpace(excerpt))
        {
            var remaining = MaxBlockChars - sb.Length - 40;
            if (remaining >= 80)
                sb.AppendLine($"page_excerpt: {Compact(excerpt, Math.Min(remaining, 550))}");
        }

        sb.Append("</attention_context>");
        var result = sb.ToString();
        if (result.Length > MaxBlockChars)
        {
            var cut = Math.Max(0, MaxBlockChars - 22);
            result = result[..cut].TrimEnd() + "\n</attention_context>";
        }
        return result;
    }

    internal static string FormatStaleBlock(
        string source,
        int ageSec,
        string processName,
        string title,
        IReadOnlyList<string> candidates)
    {
        var mins = Math.Max(2, ageSec / 60);
        var sb = new StringBuilder();
        sb.AppendLine($"<attention_context source=\"{source}\" age_sec=\"{ageSec}\" confidence=\"low\" stale=\"true\">");
        sb.AppendLine($"note: Last external window focus was {mins} min ago ({processName}: {Compact(title, 80)}). Do NOT guess blindly; ask the user which target they mean and output `choices` buttons.");
        if (candidates.Count > 0)
        {
            sb.AppendLine("candidates:");
            for (int i = 0; i < candidates.Count; i++)
                sb.AppendLine($"- {i + 1}. {candidates[i]}");
        }
        sb.Append("</attention_context>");
        return sb.ToString();
    }

    private List<string> BuildDisambiguationCandidates(
        string appDisplay,
        string windowTitle,
        IReadOnlyList<ChatMessageRecord> recentMessages)
    {
        var list = new List<string>();
        var shortTitle = Compact(windowTitle, 24);
        list.Add($"{appDisplay} 上的〈{shortTitle}〉");

        // Check recent notification / mail in chat history
        var recentNotif = recentMessages
            .LastOrDefault(m => m.Role == "notification" && m.Notification is not null && (DateTimeOffset.UtcNow - m.CreatedAt) <= TimeSpan.FromHours(2));
        if (recentNotif?.Notification is { } n)
        {
            list.Add($"剛才的〈{Compact(n.Title, 20)}〉");
        }
        else
        {
            var bbSnap = _blackboard.GetSnapshot(maxRecentResults: 3, maxAge: TimeSpan.FromMinutes(15));
            var lastRes = bbSnap.RecentResults.LastOrDefault(r => r.IsUserInitiated || r.IsSignificant);
            if (lastRes is not null)
                list.Add($"剛才查的〈{Compact(lastRes.Goal, 20)}〉");
        }

        if (list.Count == 1)
            list.Add("剛才聊的內容");

        list.Add("其他（我直接說）");
        return list.Take(3).ToList();
    }

    private static string FormatAppDisplayName(string processName) => (processName ?? "").Trim().ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "winword" => "Word",
        "excel" => "Excel",
        "powerpnt" => "PowerPoint",
        "code" => "VS Code",
        "notepad" => "記事本",
        "acrobat" or "acrord32" => "PDF",
        var p => string.IsNullOrWhiteSpace(p) ? "外部視窗" : p
    };

    private static string Compact(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        return flat.Length <= maxChars ? flat : flat[..maxChars] + "…";
    }
}
