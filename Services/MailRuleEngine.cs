using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Pure C# deterministic rule engine. Runs without calling any AI model.
/// </summary>
public sealed class MailRuleEngine
{
    private readonly HashSet<string> _vipSenders;

    public MailRuleEngine(IConfiguration config)
    {
        var vips = config.GetSection("MailBroker:VipSenders").Get<string[]>() ?? Array.Empty<string>();
        _vipSenders = new HashSet<string>(vips, StringComparer.OrdinalIgnoreCase);
    }

    public MailRuleDecision Evaluate(MailItem mail)
    {
        // Already read historical mails don't trigger proactive notifications
        if (mail.IsRead)
            return MailRuleDecision.Ignore;

        // Low importance newsletters / bulk notices -> ignore without AI
        if (mail.Importance == MailImportance.Low ||
            mail.Subject.Contains("電子報", StringComparison.OrdinalIgnoreCase) ||
            mail.Subject.Contains("訂閱", StringComparison.OrdinalIgnoreCase))
        {
            return MailRuleDecision.Ignore;
        }

        // Deterministic rule: VIP sender + High Importance -> Direct Notify (no AI needed)
        if (_vipSenders.Contains(mail.SenderEmail) && mail.Importance == MailImportance.High)
            return MailRuleDecision.DirectNotify;

        // High importance or action/urgency cues -> send to AI semantic triage
        if (mail.Importance == MailImportance.High ||
            ContainsAny(mail.Subject, "急", "確認", "處理", "簽核", "通知", "表單", "單號", "urgent", "action") ||
            ContainsAny(mail.Body, "今天", "盡快", "儘快", "盡速", "儘速", "請您處理", "請處理", "回覆", "截止", "期限"))
        {
            return MailRuleDecision.NeedsAiTriage;
        }

        return MailRuleDecision.Ignore;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
