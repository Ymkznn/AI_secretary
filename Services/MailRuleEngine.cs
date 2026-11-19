using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Filters already-read historical mails; all unread incoming mails are triaged by AI (no hardcoded VIP or keyword weights).
/// </summary>
public sealed class MailRuleEngine
{
    public MailRuleDecision Evaluate(MailItem mail)
        => mail.IsRead ? MailRuleDecision.Ignore : MailRuleDecision.NeedsAiTriage;
}
