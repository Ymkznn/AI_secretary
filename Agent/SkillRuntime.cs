using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Resources;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// 跨技能共用的底層執行引擎（SkillRuntime）：封裝郵件存取、應用程式啟動、螢幕擷取、
/// 路徑授權請求、知識庫與行事曆／排程資料存取等共用能力。
/// </summary>
public sealed record PendingMailNotificationContext(
    string ConvId,
    string EventId,
    MailItem? Mail,
    string Title,
    string Summary,
    int ReRemindMinutes,
    List<NotificationButtonOption>? Actions,
    string? Otp)
{
    public bool Published { get; set; }
}

public sealed class SkillRuntime
{
    private readonly IMailBroker _mailBroker;
    private readonly GmailSkillService _gmail;
    private readonly AppLauncherService _appLauncher;
    private readonly ScreenCaptureService _screenCapture;
    private readonly SandboxService _sandbox;
    private readonly GuardService _guard;
    private readonly SqliteStore _store;
    private readonly IServiceProvider _services;
    private readonly ILogger<SkillRuntime> _logger;

    public event Action<string, string, int>? PinReminderRequested;
    public event Func<string, Task<string>>? DismissPinnedReminderRequested;
    public event Action? SchedulesChanged;
    public event Action<DateTimeOffset?, bool>? QuietModeChanged;
    public event Action<string>? TrustedPathAdded;
    public event Action<string>? UserPrefChanged;
    public event Action<string, string>? FactModified;
    public Func<string, string, string, Task<PathPermissionDecision>>? ConfirmPathPermissionHandler { get; set; }
    public Func<string, string, CancellationToken, Task<string>>? SyncMailRequested { get; set; }
    public Func<string, string, MailItem?, string, string, int, List<NotificationButtonOption>?, CancellationToken, Task>? PublishNotificationHandler { get; set; }

    private readonly AsyncLocal<PendingMailNotificationContext?> _activeEventNotification = new();
    public PendingMailNotificationContext? CurrentEventNotification
    {
        get => _activeEventNotification.Value;
        set => _activeEventNotification.Value = value;
    }
    public string? LastObservedOtpCode { get; set; }
    public string? LastObservedOtpHint { get; set; }

    public SkillRuntime(
        IMailBroker mailBroker,
        GmailSkillService gmail,
        AppLauncherService appLauncher,
        ScreenCaptureService screenCapture,
        SandboxService sandbox,
        GuardService guard,
        SqliteStore store,
        IServiceProvider services,
        ILogger<SkillRuntime> logger)
    {
        _mailBroker = mailBroker;
        _gmail = gmail;
        _appLauncher = appLauncher;
        _screenCapture = screenCapture;
        _sandbox = sandbox;
        _guard = guard;
        _store = store;
        _services = services;
        _logger = logger;
    }

    public void ValidateAtStartup()
    {
        var registry = _services.GetRequiredService<SkillRegistry>();
        foreach (var skill in registry.All)
        {
            var check = _guard.CheckSkillExecution(skill.Name);
            _logger.LogInformation("Startup Skill Validation [{Skill}]: Allowed={Allowed}", skill.Name, check.Allowed);
        }
    }

    public IReadOnlyList<SkillMenuEntry> GetBuiltInSkills()
        => _services.GetRequiredService<SkillRegistry>().GetMenuEntries(_guard.IsSkillEnabled);

    public async Task<IReadOnlyList<MailItem>> ReadInboxAsync(string? query = null, int? maxResults = null, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_mail");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _mailBroker.FetchRecentMailsAsync(null, ct, query, maxResults);
    }

    public async Task<MailItem?> GetMailDetailAsync(string mailId, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_mail");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _mailBroker.GetMailByIdAsync(mailId, ct);
    }

    public async Task<IReadOnlyList<MailItem>> ReadGmailAsync(string? query = null, int? maxResults = null, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_mail");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _gmail.ReadInboxAsync(query, maxResults, ct);
    }

    public async Task<MailItem?> GetGmailDetailAsync(string messageId, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_mail");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _gmail.GetMailDetailAsync(messageId, ct);
    }

    public async Task<string> SyncMailAsync(string conversationId, string? source = null, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_mail");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        if (SyncMailRequested is null)
            return "sync_mail: service not initialized.";
        return await SyncMailRequested(conversationId, source ?? "all", ct);
    }

    /// <summary>啟動指定的本機應用程式。</summary>
    public AppLaunchResult LaunchApp(string appName, string? arguments = null)
    {
        var check = _guard.CheckSkillExecution("launch_app");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return _appLauncher.Launch(appName, arguments);
    }

    /// <summary>列出目前可視視窗，或擷取指定視窗／螢幕畫面。</summary>
    public (bool Ok, string Message, string? FilePath, IReadOnlyList<(string ProcessName, string Title)> Windows) InspectScreen(string mode = "capture", string? windowTitle = null)
    {
        var check = _guard.CheckSkillExecution("inspect_screen");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var wins = _screenCapture.ListOpenWindows();
        if (mode.Equals("list", StringComparison.OrdinalIgnoreCase))
            return (true, "Listed open windows.", null, wins);

        var (ok, msg, path) = _screenCapture.Capture(windowTitle);
        return (ok, msg, path, wins);
    }

    public async Task<bool> EnsureExternalPathAccessAsync(
        string? filePath,
        string skillName,
        Func<string, string, Task<bool>> confirmAsync)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath) || _sandbox.IsPathInsideSandbox(filePath))
            return true;

        var title = Resources.AppMessages.Localize("path_perm_title", "External File Access");
        var body = Resources.AppMessages.Localize($"path_perm_body:{skillName}", $"Allow {skillName} to access this file outside sandbox:");
        var perm = await RequestPathPermissionAsync(title, body, filePath, confirmAsync);
        return perm != PathPermissionDecision.Deny;
    }

    internal async Task<PathPermissionDecision> RequestPathPermissionAsync(
        string title,
        string message,
        string targetPath,
        Func<string, string, Task<bool>> fallbackConfirmAsync)
    {
        if (_sandbox.IsPathInsideSandbox(targetPath))
            return PathPermissionDecision.AllowOnce;

        if (ConfirmPathPermissionHandler is not null)
        {
            var decision = await ConfirmPathPermissionHandler(title, message, targetPath);
            if (decision == PathPermissionDecision.TrustPermanently)
            {
                var folder = SandboxService.NormalizeFolderForTrust(targetPath);
                _sandbox.AddTrustedPath(folder);
                TrustedPathAdded?.Invoke(folder);
            }
            return decision;
        }

        var approved = await fallbackConfirmAsync(title, AppMessages.FallbackConfirmSuffix(message, targetPath));
        return approved ? PathPermissionDecision.AllowOnce : PathPermissionDecision.Deny;
    }

    /// <summary>立即將指定提醒釘選至對話框置頂欄。</summary>
    public void PinReminderNow(string title, string message, int reRemindMinutes = 15)
    {
        var check = _guard.CheckSkillExecution("manage_schedule");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        PinReminderRequested?.Invoke(title, message, reRemindMinutes);
    }

    /// <summary>依關鍵字解除置頂提醒。</summary>
    public async Task<string> DismissPinnedReminderAsync(string keyword)
    {
        var check = _guard.CheckSkillExecution("dismiss_reminder");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        if (DismissPinnedReminderRequested is null)
            return AppMessages.CannotDismissPinnedReminder;
        return await DismissPinnedReminderRequested.Invoke(keyword ?? "");
    }

    public async Task<ReminderRecord> CreateReminderAsync(
        string conversationId,
        DateTimeOffset remindAt,
        string title,
        string message,
        string? relatedMailId = null,
        int intervalMinutes = 0,
        string? skillName = null,
        string? skillArgsJson = null,
        bool workdaysOnly = false,
        string? linkedEventId = null,
        string? recurrenceRule = null,
        CancellationToken ct = default)
    {
        var hasRule = RecurrenceHelper.IsRecurring(recurrenceRule);
        var normRule = hasRule ? RecurrenceHelper.Normalize(recurrenceRule) : null;
        var skillKey = intervalMinutes > 0 || hasRule || !string.IsNullOrWhiteSpace(skillName) ? "create_schedule" : "create_reminder";
        var check = _guard.CheckSkillExecution(skillKey);
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var rec = new ReminderRecord(
            ReminderId: Guid.NewGuid().ToString("N"),
            ConversationId: conversationId,
            Title: title,
            Message: message,
            RelatedMailId: relatedMailId,
            RemindAt: remindAt,
            Status: "Pending",
            IntervalMinutes: intervalMinutes,
            SkillName: skillName,
            SkillArgsJson: skillArgsJson,
            WorkdaysOnly: workdaysOnly || RecurrenceHelper.IsWorkdaysRule(normRule),
            ExceptionDatesJson: "[]",
            LinkedEventId: linkedEventId,
            RecurrenceRule: normRule);
        await _store.CreateReminderAsync(rec, ct);
        SchedulesChanged?.Invoke();
        return rec;
    }

    public async Task<IReadOnlyList<ReminderRecord>> ListSchedulesAsync(CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_schedule");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.GetPendingRemindersAsync(ct);
    }

    public async Task<ReminderRecord?> UpdateScheduleAsync(
        string idOrKeyword,
        string? title = null,
        string? message = null,
        DateTimeOffset? remindAt = null,
        int? intervalMinutes = null,
        string? skillName = null,
        string? skillArgsJson = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_schedule");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var pending = await _store.GetPendingRemindersAsync(ct);
        var key = idOrKeyword?.Trim() ?? "";
        var target = pending.FirstOrDefault(r => r.ReminderId.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? (string.IsNullOrWhiteSpace(key) ? null : pending.FirstOrDefault(r => r.Title.Contains(key, StringComparison.OrdinalIgnoreCase)));
        if (target is null) return null;

        var ok = await _store.UpdateReminderAsync(
            target.ReminderId, title, message, remindAt, intervalMinutes, skillName, skillArgsJson, ct);
        if (!ok) return null;

        SchedulesChanged?.Invoke();
        return (await _store.GetPendingRemindersAsync(ct)).FirstOrDefault(r => r.ReminderId == target.ReminderId);
    }

    public async Task<KnowledgeFactRecord> RecordFactAsync(
        string conversationId,
        string summary,
        string detail,
        string? evidence = null,
        string confidence = "observed",
        string category = "general",
        List<string>? tags = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var normalizedConfidence = string.Equals(confidence, "inferred", StringComparison.OrdinalIgnoreCase)
            ? "inferred"
            : "observed";
        var normalizedCategory = string.IsNullOrWhiteSpace(category)
            ? "general"
            : category.Trim().ToLowerInvariant();

        var now = DateTimeOffset.UtcNow;
        var fact = new KnowledgeFactRecord(
            FactId: "fact-" + Guid.NewGuid().ToString("N")[..8],
            ConversationId: conversationId,
            Category: normalizedCategory,
            Summary: summary.Trim(),
            Detail: string.IsNullOrWhiteSpace(detail) ? summary.Trim() : detail.Trim(),
            Evidence: string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim(),
            Confidence: normalizedConfidence,
            Tags: tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
            CreatedAt: now,
            UpdatedAt: now);

        await _store.UpsertFactAsync(fact, ct);
        if (normalizedCategory == "user_pref")
            UserPrefChanged?.Invoke(conversationId);
        return fact;
    }

    public async Task<List<FactSummaryItem>> SearchFactsAsync(
        string conversationId,
        string? keyword = null,
        string? category = null,
        int limit = 10,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.ListFactsAsync(conversationId, keyword, category, limit, ct);
    }

    public async Task<KnowledgeFactRecord?> GetFactDetailAsync(string factId, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.GetFactDetailAsync(factId, ct);
    }

    public async Task<bool> DeleteScheduleAsync(string idOrKeyword, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_schedule");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var pending = await _store.GetPendingRemindersAsync(ct);
        var key = idOrKeyword?.Trim() ?? "";
        var target = pending.FirstOrDefault(r => r.ReminderId.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? (string.IsNullOrWhiteSpace(key) ? null : pending.FirstOrDefault(r => r.Title.Contains(key, StringComparison.OrdinalIgnoreCase)));
        if (target is null) return false;

        await _store.DeleteReminderAsync(target.ReminderId, ct);
        SchedulesChanged?.Invoke();
        return true;
    }

    public async Task<bool> UpdateFactAsync(
        string factId,
        string? summary = null,
        string? detail = null,
        string? category = null,
        string? evidence = null,
        string conversationId = "",
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        var ok = await _store.UpdateFactAsync(factId, summary, detail, category, evidence, ct);
        if (ok && !string.IsNullOrWhiteSpace(conversationId))
            FactModified?.Invoke(conversationId, factId);
        return ok;
    }

    public async Task<bool> DeleteFactAsync(string factId, string conversationId = "", CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        var ok = await _store.DeleteFactAsync(factId, ct);
        if (ok && !string.IsNullOrWhiteSpace(conversationId))
            FactModified?.Invoke(conversationId, factId);
        return ok;
    }

    public async Task<MemoryAnnotationRecord> AnnotateMemoryAsync(
        string conversationId,
        string targetType,
        string targetId,
        string note,
        string? sourceFactId = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var now = DateTimeOffset.UtcNow;
        var rec = new MemoryAnnotationRecord(
            AnnotationId: "ann-" + Guid.NewGuid().ToString("N")[..8],
            ConversationId: conversationId,
            TargetType: targetType.Trim().ToLowerInvariant(),
            TargetId: targetId.Trim(),
            Note: note.Trim(),
            SourceFactId: string.IsNullOrWhiteSpace(sourceFactId) ? null : sourceFactId.Trim(),
            CreatedAt: now,
            UpdatedAt: now);
        await _store.UpsertMemoryAnnotationAsync(rec, ct);
        return rec;
    }

    public async Task<List<MemoryAnnotationRecord>> ListAnnotationsAsync(
        string? targetType = null,
        string? targetId = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.GetMemoryAnnotationsAsync(targetType, targetId, sourceFactId: null, ct);
    }

    public async Task<bool> UpdateAnnotationAsync(string annotationId, string note, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.UpdateMemoryAnnotationAsync(annotationId, note, ct);
    }

    public async Task<bool> DeleteAnnotationAsync(string annotationId, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_facts");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.DeleteMemoryAnnotationAsync(annotationId, ct);
    }

    public async Task<List<CalendarEventRecord>> ListCalendarEventsAsync(
        string conversationId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_calendar");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.GetCalendarEventsAsync(conversationId, from, to, ct);
    }

    public async Task<List<CalendarEventRecord>> FindCalendarConflictsAsync(
        string conversationId,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        string? excludeEventId = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_calendar");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        return await _store.FindConflictingEventsAsync(conversationId, startAt, endAt, excludeEventId, ct);
    }

    private async Task<int> ResolveDefaultMeetingRemindMinutesAsync(string conversationId, CancellationToken ct)
    {
        var prefs = await _store.GetUserPrefFactsAsync(conversationId, 15, ct);
        foreach (var p in prefs)
        {
            var text = $"{p.Summary} {p.Detail}";
            if (!text.Contains("提醒", StringComparison.Ordinal) && !text.Contains("會議", StringComparison.Ordinal) && !text.Contains("提前", StringComparison.Ordinal))
                continue;
            var m = Regex.Match(text, @"(?:提前|會前|前)\s*(\d{1,3})\s*分鐘|(\d{1,3})\s*分鐘前");
            if (m.Success)
            {
                var raw = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
                if (int.TryParse(raw, out var mins) && mins >= 0)
                    return mins;
            }
        }
        return 10;
    }

    public async Task<(CalendarEventRecord? Created, List<CalendarEventRecord> Conflicts, ReminderRecord? LinkedReminder)> CreateCalendarEventAsync(
        string conversationId,
        string title,
        string category,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        bool allDay,
        string notes,
        bool force = false,
        string recurrence = "none",
        int? remindMinutesBefore = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_calendar");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var conflicts = await _store.FindConflictingEventsAsync(conversationId, startAt, endAt, null, ct);
        if (!force && conflicts.Count > 0)
            return (null, conflicts, null);

        var normCat = string.IsNullOrWhiteSpace(category) ? "event" : category.Trim().ToLowerInvariant();
        int effRemindMins = remindMinutesBefore
            ?? (allDay || normCat is "leave" or "holiday" ? 0 : await ResolveDefaultMeetingRemindMinutesAsync(conversationId, ct));

        var now = DateTimeOffset.UtcNow;
        var normRec = RecurrenceHelper.Normalize(recurrence);
        var ev = new CalendarEventRecord(
            EventId: "evt-" + Guid.NewGuid().ToString("N")[..8],
            ConversationId: conversationId,
            Title: title.Trim(),
            Category: normCat,
            StartAt: startAt,
            EndAt: endAt,
            AllDay: allDay,
            Notes: notes?.Trim() ?? "",
            CreatedAt: now,
            UpdatedAt: now,
            Recurrence: normRec,
            ExceptionDatesJson: "[]");
        await _store.CreateCalendarEventAsync(ev, ct);

        ReminderRecord? linkedReminder = null;
        if (effRemindMins > 0 && !allDay)
        {
            var isRec = RecurrenceHelper.IsRecurring(normRec);
            var remindAt = startAt.AddMinutes(-effRemindMins);
            if (remindAt > DateTimeOffset.UtcNow || isRec)
            {
                linkedReminder = new ReminderRecord(
                    ReminderId: Guid.NewGuid().ToString("N"),
                    ConversationId: conversationId,
                    Title: AppMessages.CalendarLeadTitle(ev.Title),
                    Message: AppMessages.CalendarLeadMessage(ev.StartAt, ev.Title),
                    RelatedMailId: null,
                    RemindAt: remindAt,
                    Status: "Pending",
                    IntervalMinutes: 0,
                    SkillName: null,
                    SkillArgsJson: null,
                    WorkdaysOnly: RecurrenceHelper.IsWorkdaysRule(normRec),
                    ExceptionDatesJson: "[]",
                    LinkedEventId: ev.EventId,
                    RecurrenceRule: isRec ? normRec : null);
                await _store.CreateReminderAsync(linkedReminder, ct);
            }
        }

        SchedulesChanged?.Invoke();
        return (ev, conflicts, linkedReminder);
    }

    public async Task<(CalendarEventRecord? Updated, List<CalendarEventRecord> Conflicts)> UpdateCalendarEventAsync(
        string conversationId,
        string idOrKeyword,
        string? title = null,
        string? category = null,
        DateTimeOffset? startAt = null,
        DateTimeOffset? endAt = null,
        bool? allDay = null,
        string? notes = null,
        bool force = false,
        string? recurrence = null,
        int? remindMinutesBefore = null,
        IReadOnlyList<string>? overrideDates = null,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_calendar");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var all = await _store.GetCalendarEventsAsync(conversationId, null, null, ct);
        var key = idOrKeyword?.Trim() ?? "";
        var target = all.FirstOrDefault(e => e.EventId.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? (string.IsNullOrWhiteSpace(key) ? null : all.FirstOrDefault(e => e.Title.Contains(key, StringComparison.OrdinalIgnoreCase)));
        if (target is null) return (null, new List<CalendarEventRecord>());

        var effectiveStart = startAt ?? target.StartAt;
        var effectiveEnd = endAt ?? target.EndAt;
        var conflicts = await _store.FindConflictingEventsAsync(conversationId, effectiveStart, effectiveEnd, target.EventId, ct);
        if (!force && (startAt.HasValue || endAt.HasValue) && conflicts.Count > 0)
            return (null, conflicts);

        var hasEventFieldUpdate = title is not null || category is not null || startAt.HasValue || endAt.HasValue || allDay.HasValue || notes is not null || recurrence is not null;
        if (hasEventFieldUpdate)
        {
            var ok = await _store.UpdateCalendarEventAsync(target.EventId, title, category, startAt, endAt, allDay, notes, recurrence, ct);
            if (!ok && !remindMinutesBefore.HasValue) return (null, conflicts);
        }
        else if (!remindMinutesBefore.HasValue)
        {
            return (null, conflicts);
        }

        var updated = (await _store.GetCalendarEventsAsync(conversationId, null, null, ct))
            .FirstOrDefault(e => e.EventId == target.EventId) ?? target;

        if (remindMinutesBefore.HasValue)
        {
            if (overrideDates is { Count: > 0 })
                await _store.SetLinkedReminderLeadForDatesAsync(updated, Math.Max(0, remindMinutesBefore.Value), overrideDates, ct);
            else
                await _store.SetLinkedReminderLeadAsync(updated, Math.Max(0, remindMinutesBefore.Value), ct);
        }

        SchedulesChanged?.Invoke();
        return (updated, conflicts);
    }

    public async Task<int> UpdateAllMeetingRemindLeadAsync(string conversationId, int remindMinutesBefore, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_calendar");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);
        var count = await _store.UpdateAllLinkedRemindersLeadAsync(conversationId, Math.Max(0, remindMinutesBefore), ct);
        SchedulesChanged?.Invoke();
        return count;
    }

    public async Task<CalendarEventRecord?> SkipCalendarEventDateAsync(
        string conversationId,
        string idOrKeyword,
        string dateYmd,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_calendar");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var all = await _store.GetCalendarEventsAsync(conversationId, null, null, ct);
        var key = idOrKeyword?.Trim() ?? "";
        var target = all.FirstOrDefault(e => e.EventId.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? (string.IsNullOrWhiteSpace(key) ? null : all.FirstOrDefault(e => e.Title.Contains(key, StringComparison.OrdinalIgnoreCase)));
        if (target is null) return null;

        await _store.SkipCalendarEventDateAsync(target.EventId, dateYmd, ct);
        SchedulesChanged?.Invoke();
        return target;
    }

    public async Task<CalendarEventRecord?> DeleteCalendarEventAsync(string conversationId, string idOrKeyword, CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("manage_calendar");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var all = await _store.GetCalendarEventsAsync(conversationId, null, null, ct);
        var key = idOrKeyword?.Trim() ?? "";
        var target = all.FirstOrDefault(e => e.EventId.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? (string.IsNullOrWhiteSpace(key) ? null : all.FirstOrDefault(e => e.Title.Contains(key, StringComparison.OrdinalIgnoreCase)));
        if (target is null) return null;

        await _store.DeleteCalendarEventAsync(target.EventId, ct);
        SchedulesChanged?.Invoke();
        return target;
    }

    public async Task<(string Message, List<ReminderRecord> UpcomingInWindow)> SetQuietModeAsync(
        DateTimeOffset? until,
        bool allowUrgent,
        string? reason,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("set_quiet_mode");
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var payload = until.HasValue
            ? JsonSerializer.Serialize(new { until = until.Value.ToString("O"), allow_urgent = allowUrgent, reason = reason ?? "" })
            : "";
        await _store.SetSyncValueAsync("dnd_state", payload, ct);
        QuietModeChanged?.Invoke(until, allowUrgent);

        if (!until.HasValue)
            return (AppMessages.QuietModeDisabledReply, new List<ReminderRecord>());

        var pending = await _store.GetPendingRemindersAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var inWindow = pending.Where(r => r.IntervalMinutes == 0 && r.RemindAt >= now && r.RemindAt <= until.Value).ToList();
        return (AppMessages.QuietModeEnabledReply(until.Value, allowUrgent), inWindow);
    }
}
