using System.Collections.Concurrent;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Resources;

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
    private readonly GuardService _guard;
    private readonly PlannerAgent _planner;
    private readonly ILogger<MailSyncService> _logger;
    private readonly SemaphoreSlim _triageGate = new(2, 2);

    public MailSyncService(
        SqliteStore store,
        IMailBroker mailBroker,
        GmailSkillService gmail,
        MailRuleEngine ruleEngine,
        GuardService guard,
        PlannerAgent planner,
        ILogger<MailSyncService> logger)
    {
        _store = store;
        _mailBroker = mailBroker;
        _gmail = gmail;
        _ruleEngine = ruleEngine;
        _guard = guard;
        _planner = planner;
        _logger = logger;
    }

    public async Task<string> SyncOnceAsync(
        string conversationId,
        string source,
        Func<string, MailItem, string, string, int, List<NotificationButtonOption>?, Task> onNotifyAsync,
        CancellationToken ct = default)
    {
        var mode = string.IsNullOrWhiteSpace(source) ? "all" : source.Trim().ToLowerInvariant();
        int ewsCount = 0;
        int gmailCount = 0;
        var notes = new List<string>();

        if (mode is "all" or "ews")
        {
            try
            {
                ewsCount = await PollEwsOnceAsync(conversationId, onNotifyAsync, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "EWS sync skipped");
                notes.Add($"EWS skipped: {ex.Message}");
            }
        }

        if (mode is "all" or "gmail")
        {
            try
            {
                gmailCount = await PollGmailOnceAsync(conversationId, onNotifyAsync, ct);
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
        return summary;
    }

    private async Task<int> PollEwsOnceAsync(
        string conversationId,
        Func<string, MailItem, string, string, int, List<NotificationButtonOption>?, Task> onNotifyAsync,
        CancellationToken ct)
    {
        var cursor = await _store.GetMailSyncCursorAsync(ct);
        var mails = await _mailBroker.FetchRecentMailsAsync(cursor, ct);
        if (mails.Count == 0) return 0;

        var newest = cursor ?? DateTimeOffset.MinValue;
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int processed = 0;

        foreach (var mail in mails.OrderByDescending(m => m.ReceivedAt))
        {
            if (mail.ReceivedAt > newest) newest = mail.ReceivedAt;

            var decision = _ruleEngine.Evaluate(mail);
            var eventId = $"ev-{mail.Id}";
            var isNew = await _store.TryRecordMailEventAsync(
                new MailEventRecord(eventId, mail.Id, decision.ToString(), decision == MailRuleDecision.Ignore, DateTimeOffset.UtcNow), ct);
            if (!isNew) continue;
            if (!seenInBatch.Add($"{mail.SenderEmail}|{mail.Subject}")) continue;

            processed++;
            if (decision == MailRuleDecision.NeedsAiTriage)
            {
                _ = Task.Run(async () =>
                {
                    await _triageGate.WaitAsync(ct);
                    try
                    {
                        var triage = await _planner.TriageMailEventAsync(mail, conversationId, ct);
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
                        _triageGate.Release();
                    }
                }, ct);
            }
        }

        if (newest > (cursor ?? DateTimeOffset.MinValue))
            await _store.SetMailSyncCursorAsync(newest, ct);

        return processed;
    }

    private async Task<int> PollGmailOnceAsync(
        string conversationId,
        Func<string, MailItem, string, string, int, List<NotificationButtonOption>?, Task> onNotifyAsync,
        CancellationToken ct)
    {
        if (!_gmail.IsAuthorized || !_guard.IsSkillEnabled("read_gmail"))
            return 0;

        var cursorRaw = await _store.GetSyncValueAsync("gmail_sync_cursor", ct);
        DateTimeOffset? cursor = DateTimeOffset.TryParse(cursorRaw, out var c) ? c : null;

        var mails = await _gmail.ReadInboxAsync("is:unread newer_than:1d", 5, ct);
        if (mails.Count == 0)
        {
            if (!cursor.HasValue)
                await _store.SetSyncValueAsync("gmail_sync_cursor", DateTimeOffset.UtcNow.ToString("O"), ct);
            return 0;
        }

        if (!cursor.HasValue)
        {
            var baseline = mails.Max(m => m.ReceivedAt);
            await _store.SetSyncValueAsync("gmail_sync_cursor", baseline.ToString("O"), ct);
            return 0;
        }

        var newest = cursor.Value;
        int processed = 0;
        foreach (var mail in mails.OrderByDescending(m => m.ReceivedAt))
        {
            if (mail.ReceivedAt > newest) newest = mail.ReceivedAt;
            if (mail.ReceivedAt <= cursor.Value) continue;

            var decision = _ruleEngine.Evaluate(mail);
            var eventId = $"ev-{mail.Id}";
            var isNew = await _store.TryRecordMailEventAsync(
                new MailEventRecord(eventId, mail.Id, decision.ToString(), decision == MailRuleDecision.Ignore, DateTimeOffset.UtcNow), ct);
            if (!isNew) continue;

            processed++;
            if (decision == MailRuleDecision.NeedsAiTriage)
            {
                _ = Task.Run(async () =>
                {
                    await _triageGate.WaitAsync(ct);
                    try
                    {
                        var triage = await _planner.TriageMailEventAsync(mail, conversationId, ct);
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
