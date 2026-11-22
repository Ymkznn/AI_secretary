using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Passive mail synchronization service (EWS + Gmail) with cursor tracking, deduplication, and AI triage.
/// Driven solely by scheduled tasks or explicit skill calls—never runs its own background polling loop.
/// Contains ZERO hardcoded OTP/language regexes: MailTriage LLM extracts any verification code format/language
/// and SecretVault locks it immediately at the triage exit before notifying StateMachineAgent or UI.
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
    private readonly SecretVaultService _vault;
    private readonly StateMachineAgent _stateMachine;
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
        SecretVaultService vault,
        StateMachineAgent stateMachine,
        ILogger<MailSyncService> logger)
    {
        _store = store;
        _mailBroker = mailBroker;
        _gmail = gmail;
        _ruleEngine = ruleEngine;
        _skillRegistry = skillRegistry;
        _planner = planner;
        _blackboard = blackboard;
        _vault = vault;
        _stateMachine = stateMachine;
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
                // Omit raw Snippet before background MailTriage finishes so unredacted OTPs never enter Blackboard.
                // Triage writes its redacted summary into its own mail_triage:{id} Blackboard entry once done.
                var lines = newMails
                    .OrderByDescending(m => m.ReceivedAt)
                    .Take(10)
                    .Select(m => $"- id={m.Id} | from={m.SenderName} | subject=\"{_vault.RedactKnownSecrets(m.Subject)}\" | at={m.ReceivedAt.ToLocalTime():MM-dd HH:mm}");
                summary += "\nNew emails:\n" + string.Join("\n", lines);
            }

            summary = _vault.RedactKnownSecrets(summary);
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
                EnqueueBackgroundTriage(conversationId, eventId, mail, onNotifyAsync, "EWS", ct);
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
                EnqueueBackgroundTriage(conversationId, eventId, mail, onNotifyAsync, "Gmail", ct);
        }

        if (newest > cursor.Value)
            await _store.SetSyncValueAsync("gmail_sync_cursor", newest.ToString("O"), ct);

        return processed;
    }

    private void EnqueueBackgroundTriage(
        string conversationId,
        string eventId,
        MailItem mail,
        Func<string, MailItem, string, string, int, List<NotificationButtonOption>?, Task> onNotifyAsync,
        string providerLabel,
        CancellationToken ct)
    {
        var triageTaskId = $"mail_triage:{mail.Id}";
        var triageGoal = $"Analyze new email from {mail.SenderName}";
        _blackboard.StartTask(triageTaskId, "mail_sync", triageGoal);
        _ = Task.Run(async () =>
        {
            bool triageDone = false;
            await _triageGate.WaitAsync(ct);
            try
            {
                var triage = await _planner.TriageMailEventAsync(mail, conversationId, ct);
                var (safeTitle, safeSummary, safeActions, primaryOtpHandle) = VaultAndRedactTriage(_vault, mail, triage);
                var safeMail = RedactMailMetadata(_vault, mail);

                var bbSummary = triage.Urgent
                    ? $"Urgent email [{safeTitle}]: {safeSummary}"
                    : $"Non-urgent email from {safeMail.SenderName} ({safeMail.Subject})";
                _blackboard.CompleteTask(triageTaskId, "mail_sync", triageGoal, bbSummary, succeeded: true, recordInRecent: triage.Urgent);
                triageDone = true;

                // Emit generic ContextEvent into StateMachineAgent if triage produced an actionable vaulted handle.
                // Await synchronously BEFORE pushing any notification card so there is zero race condition or duplicate popup.
                bool handledByStateMachine = false;
                if (!string.IsNullOrWhiteSpace(primaryOtpHandle))
                {
                    var senderDomain = DomainParser.ExtractSenderDomain(mail.SenderEmail);
                    var regDomain = DomainParser.ExtractRegistrableDomain(senderDomain);
                    var ctxEvent = new StateMachineContextEvent(
                        EventId: eventId,
                        ActionClass: AutonomyPolicyService.ActionFillOtp,
                        SourceId: mail.Id,
                        SourceDomain: regDomain,
                        SourceName: mail.SenderName,
                        Title: safeTitle,
                        Summary: safeSummary,
                        ResourceHandle: primaryOtpHandle,
                        OccurredAtUtc: mail.ReceivedAt.ToUniversalTime());

                    handledByStateMachine = await _stateMachine.HandleContextEventAsync(ctxEvent, ct);
                }

                if (triage.Urgent && !handledByStateMachine)
                {
                    await onNotifyAsync(eventId, safeMail, safeTitle, safeSummary, triage.ReRemindMinutes, safeActions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background {Provider} triage error", providerLabel);
            }
            finally
            {
                if (!triageDone)
                    _blackboard.EnsureClosed(triageTaskId);
                _triageGate.Release();
            }
        }, ct);
    }

    internal static MailItem RedactMailMetadata(SecretVaultService vault, MailItem mail)
        => mail with
        {
            Subject = vault.RedactKnownSecrets(mail.Subject),
            Snippet = vault.RedactKnownSecrets(mail.Snippet),
            Body = vault.RedactKnownSecrets(mail.Body)
        };

    /// <summary>
    /// Vaults any verification code extracted by <see cref="PlannerAgent.TriageMailEventAsync"/> (in ANY language or format)
    /// at the triage exit, binds <c>fill_otp</c>/<c>copy_otp</c> buttons to the opaque <c>otp#xxxxxx</c> handle,
    /// and scrubs both plaintext codes and internal handles from human-facing notification Title/Summary.
    /// </summary>
    internal static (string Title, string Summary, List<NotificationButtonOption>? Actions, string? PrimaryOtpHandle) VaultAndRedactTriage(
        SecretVaultService vault,
        MailItem mail,
        TriageResult triage)
    {
        var senderDomain = DomainParser.ExtractSenderDomain(mail.SenderEmail);
        var regDomain = DomainParser.ExtractRegistrableDomain(senderDomain);
        string? primaryHandle = null;
        List<NotificationButtonOption>? safeActions = null;

        if (triage.Actions is { Count: > 0 })
        {
            safeActions = new List<NotificationButtonOption>(triage.Actions.Count);
            foreach (var btn in triage.Actions)
            {
                if (btn.Type is "fill_otp" or "copy_otp")
                {
                    if (string.IsNullOrWhiteSpace(btn.Prompt))
                        continue;

                    string btnHandle;
                    if (vault.TryMatchHandleOrBareSuffix(btn.Prompt, out var matched))
                        btnHandle = matched;
                    else
                        btnHandle = vault.StoreOtp(btn.Prompt, regDomain, mail.SenderName, mail.Id);

                    primaryHandle ??= btnHandle;
                    safeActions.Add(btn with { Prompt = btnHandle });
                }
                else
                {
                    safeActions.Add(btn);
                }
            }
        }

        // Redact any plaintext OTP first, then strip raw opaque vault handles (and any leaked bare 6-hex suffix)
        // from human-facing notification Title/Summary so users & TTS never see raw codes or "otp#xxxxxx".
        var safeTitle = CleanHumanFacingOtpText(vault.RedactKnownSecrets(triage.Title), primaryHandle);
        var safeSummary = CleanHumanFacingOtpText(vault.RedactKnownSecrets(triage.Summary), primaryHandle);
        return (safeTitle, safeSummary, safeActions, primaryHandle);
    }

    internal static string CleanHumanFacingOtpText(string? text, string? knownHandle = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var s = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\s*\botp#[0-9a-f]{6}\b\s*",
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!string.IsNullOrWhiteSpace(knownHandle) && SecretVaultService.IsOtpHandle(knownHandle))
        {
            var suffix = knownHandle.Trim()[4..];
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                $@"\s*\b{System.Text.RegularExpressions.Regex.Escape(suffix)}\b\s*",
                " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Clean stray spaces left before CJK/ASCII punctuation after stripping handle tokens
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+([，。、！？,.;:：）\)])", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]{2,}", " ");
        return s.Trim();
    }
}
