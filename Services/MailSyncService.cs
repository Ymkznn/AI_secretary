using System.Collections.Concurrent;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Passive mail synchronization service (EWS + Gmail) with cursor tracking, deduplication, and AI triage.
/// Driven solely by scheduled tasks or explicit skill calls—never runs its own background polling loop.
/// </summary>
public sealed class MailSyncService
{
    private readonly SqliteStore _store;
    private readonly IMailBroker _mailBroker;
    private readonly GmailSkillService _gmail;
    private readonly MailRuleEngine _ruleEngine;
    private readonly SkillRegistry _skillRegistry;
    private readonly PlannerAgent _planner;
    private readonly AgentBlackboard _blackboard;
    private readonly ILogger<MailSyncService> _logger;
    private readonly SemaphoreSlim _triageGate = new(2, 2);

    public MailSyncService(
        SqliteStore store,
        IMailBroker mailBroker,
        GmailSkillService gmail,
        MailRuleEngine ruleEngine,
        SkillRegistry skillRegistry,
        PlannerAgent planner,
        AgentBlackboard blackboard,
        ILogger<MailSyncService> logger)
    {
        _store = store;
        _mailBroker = mailBroker;
        _gmail = gmail;
        _ruleEngine = ruleEngine;
        _skillRegistry = skillRegistry;
        _planner = planner;
        _blackboard = blackboard;
        _logger = logger;
    }

    public async Task<string> SyncOnceAsync(
        string conversationId,
        string source,
        Func<string, MailItem, string, string, int, List<NotificationButtonOption>?, Task> onNotifyAsync,
        CancellationToken ct = default)
    {
        var mode = string.IsNullOrWhiteSpace(source) ? "all" : source.Trim().ToLowerInvariant();
        var bbId = $"mail_sync:{Guid.NewGuid():N}"[..18];
        var bbGoal = $"Sync emails ({mode})";
        _blackboard.StartTask(bbId, "mail_sync", bbGoal);

        int ewsCount = 0;
        int gmailCount = 0;
        var newMails = new List<MailItem>();
        var notes = new List<string>();
        bool completed = false;

        try
        {
            if ((mode is "all" or "ews" or "outlook") && (_mailBroker.IsConfigured || mode != "all"))
            {
                try
                {
                    var ewsNew = await PollEwsOnceAsync(conversationId, onNotifyAsync, ct);
                    ewsCount = ewsNew.Count;
                    newMails.AddRange(ewsNew);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "EWS sync skipped");
                    notes.Add($"EWS skipped: {ex.Message}");
                }
            }

            if ((mode is "all" or "gmail") && _gmail.IsAuthorized)
            {
                try
                {
                    var gmailNew = await PollGmailOnceAsync(conversationId, onNotifyAsync, ct);
                    gmailCount = gmailNew.Count;
                    newMails.AddRange(gmailNew);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Gmail sync skipped");
                    notes.Add($"Gmail skipped: {ex.Message}");
                }
            }

            var summary = $"Mail sync completed: EWS={ewsCount}, Gmail={gmailCount}.";
            if (notes.Count > 0)
                summary += " (" + string.Join("; ", notes) + ")";
            if (newMails.Count > 0)
            {
                var lines = newMails
                    .OrderByDescending(m => m.ReceivedAt)
                    .Take(10)
                    .Select(m => $"- id={m.Id} | from={m.SenderName} | subject=\"{m.Subject}\" | at={m.ReceivedAt.ToLocalTime():MM-dd HH:mm} | snippet={m.Snippet}");
                summary += "\nNew emails:\n" + string.Join("\n", lines);
            }

            bool hasNotableChanges = newMails.Count > 0 || notes.Count > 0;
            var dedupeKey = hasNotableChanges ? null : $"mail_sync_empty:{mode}";
            _blackboard.CompleteTask(
                bbId, "mail_sync", bbGoal, summary,
                succeeded: notes.Count == 0 || newMails.Count > 0,
                recordInRecent: hasNotableChanges,
                dedupeKey: dedupeKey);
            completed = true;
            return summary;
        }
        finally
        {
            if (!completed)
                _blackboard.EnsureClosed(bbId, "Mail sync stopped or failed.");
        }
    }

    private async Task<List<MailItem>> PollEwsOnceAsync(
        string conversationId,
        Func<string, MailItem, string, string, int, List<NotificationButtonOption>?, Task> onNotifyAsync,
        CancellationToken ct)
    {
        var cursor = await _store.GetMailSyncCursorAsync(ct);
        var mails = await _mailBroker.FetchRecentMailsAsync(cursor, ct);
        var processed = new List<MailItem>();
        if (mails.Count == 0) return processed;

        var newest = cursor ?? DateTimeOffset.MinValue;
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mail in mails.OrderByDescending(m => m.ReceivedAt))
        {
            if (mail.ReceivedAt > newest) newest = mail.ReceivedAt;

            var decision = _ruleEngine.Evaluate(mail);
            var eventId = $"ev-{mail.Id}";
            var isNew = await _store.TryRecordMailEventAsync(
                new MailEventRecord(eventId, mail.Id, decision.ToString(), decision == MailRuleDecision.Ignore, DateTimeOffset.UtcNow), ct);
            if (!isNew) continue;
            if (!seenInBatch.Add($"{mail.SenderEmail}|{mail.Subject}")) continue;

            processed.Add(mail);
            if (decision == MailRuleDecision.NeedsAiTriage)
            {
                var triageTaskId = $"mail_triage:{mail.Id}";
                var triageGoal = $"Analyze new email: {mail.Subject}";
                _blackboard.StartTask(triageTaskId, "mail_sync", triageGoal);
                _ = Task.Run(async () =>
                {
                    bool triageDone = false;
                    await _triageGate.WaitAsync(ct);
                    try
                    {
                        var triage = await _planner.TriageMailEventAsync(mail, conversationId, ct);
                        var bbSummary = triage.Urgent
                            ? $"Urgent email [{triage.Title}]: {triage.Summary}"
                            : $"Non-urgent email from {mail.SenderName} ({mail.Subject})";
                        _blackboard.CompleteTask(triageTaskId, "mail_sync", triageGoal, bbSummary, succeeded: true, recordInRecent: triage.Urgent);
                        triageDone = true;
                        if (triage.Urgent)
                        {
                            await onNotifyAsync(eventId, mail, triage.Title, triage.Summary, triage.ReRemindMinutes, triage.Actions);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Background EWS triage error");
                    }
                    finally
                    {
                        if (!triageDone)
                            _blackboard.EnsureClosed(triageTaskId);
                        _triageGate.Release();
                    }
                }, ct);
            }
        }

        if (newest > (cursor ?? DateTimeOffset.MinValue))
            await _store.SetMailSyncCursorAsync(newest, ct);

        return processed;
    }

    private async Task<List<MailItem>> PollGmailOnceAsync(
        string conversationId,
        Func<string, MailItem, string, string, int, List<NotificationButtonOption>?, Task> onNotifyAsync,
        CancellationToken ct)
    {
        var processed = new List<MailItem>();
        var mailSkill = _skillRegistry.FindByCapability("mail");
        if (!_gmail.IsAuthorized || (mailSkill is not null && !mailSkill.IsEnabled))
            return processed;

        var cursorRaw = await _store.GetSyncValueAsync("gmail_sync_cursor", ct);
        DateTimeOffset? cursor = DateTimeOffset.TryParse(cursorRaw, out var c) ? c : null;

        var mails = await _gmail.ReadInboxAsync("is:unread newer_than:1d", 5, ct);
        if (mails.Count == 0)
        {
            if (!cursor.HasValue)
                await _store.SetSyncValueAsync("gmail_sync_cursor", DateTimeOffset.UtcNow.ToString("O"), ct);
            return processed;
        }

        if (!cursor.HasValue)
        {
            var baseline = mails.Max(m => m.ReceivedAt);
            await _store.SetSyncValueAsync("gmail_sync_cursor", baseline.ToString("O"), ct);
            return processed;
        }

        var newest = cursor.Value;
        foreach (var mail in mails.OrderByDescending(m => m.ReceivedAt))
        {
            if (mail.ReceivedAt > newest) newest = mail.ReceivedAt;
            if (mail.ReceivedAt <= cursor.Value) continue;

            var decision = _ruleEngine.Evaluate(mail);
            var eventId = $"ev-{mail.Id}";
            var isNew = await _store.TryRecordMailEventAsync(
                new MailEventRecord(eventId, mail.Id, decision.ToString(), decision == MailRuleDecision.Ignore, DateTimeOffset.UtcNow), ct);
            if (!isNew) continue;

            processed.Add(mail);
            if (decision == MailRuleDecision.NeedsAiTriage)
            {
                var triageTaskId = $"mail_triage:{mail.Id}";
                var triageGoal = $"Analyze new email: {mail.Subject}";
                _blackboard.StartTask(triageTaskId, "mail_sync", triageGoal);
                _ = Task.Run(async () =>
                {
                    bool triageDone = false;
                    await _triageGate.WaitAsync(ct);
                    try
                    {
                        var triage = await _planner.TriageMailEventAsync(mail, conversationId, ct);
                        var bbSummary = triage.Urgent
                            ? $"Urgent email [{triage.Title}]: {triage.Summary}"
                            : $"Non-urgent email from {mail.SenderName} ({mail.Subject})";
                        _blackboard.CompleteTask(triageTaskId, "mail_sync", triageGoal, bbSummary, succeeded: true, recordInRecent: triage.Urgent);
                        triageDone = true;
                        if (triage.Urgent)
                        {
                            await onNotifyAsync(eventId, mail, triage.Title, triage.Summary, triage.ReRemindMinutes, triage.Actions);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Background Gmail triage error");
                    }
                    finally
                    {
                        if (!triageDone)
                            _blackboard.EnsureClosed(triageTaskId);
                        _triageGate.Release();
                    }
                }, ct);
            }
        }

        if (newest > cursor.Value)
            await _store.SetSyncValueAsync("gmail_sync_cursor", newest.ToString("O"), ct);

        return processed;
    }
}
