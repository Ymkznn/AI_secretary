using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Consolidated mail skill covering Outlook/EWS and Gmail (list | detail | sync).
/// </summary>
public sealed class ManageMailSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public ManageMailSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "manage_mail";
    public string DisplayDescription => "Read, search, and sync Outlook & Gmail";
    public string StatusText => "Accessing mail...";
    public string? Capability => "mail";
    public bool IsSilentPoller => true;
    public string MainAgentSpec => """
            manage_mail
               - Purpose: List inbox, read full email detail, or run background sync across Outlook/EWS and Gmail.
               - Args: {"action": "list|detail|sync", "source": "outlook|gmail|all", "query": "optional search query", "max_results": 10, "mail_id": "required when action=detail"}
            
        """;
    public string PlannerSpec => """manage_mail: List, inspect, or sync Outlook/Gmail. args: {"action":"list|detail|sync","source":"outlook|gmail|all","query":"...","max_results":10,"mail_id":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "list").Trim().ToLowerInvariant();
        var source = (ctx.GetArg("source") ?? "outlook").Trim().ToLowerInvariant();

        switch (action)
        {
            case "list":
                await ListAsync(ctx, source);
                break;
            case "detail":
                await DetailAsync(ctx, source);
                break;
            case "sync":
                await SyncAsync(ctx, source);
                break;
            default:
                ctx.Observations.Add($"manage_mail: unsupported action '{action}'.");
                break;
        }
    }

    private async Task ListAsync(SkillExecutionContext ctx, string source)
    {
        var query = ctx.GetArg("query");
        int? maxResults = int.TryParse(ctx.GetArg("max_results"), out var mr) ? Math.Clamp(mr, 1, 50) : null;

        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var collected = new List<MailItem>();
        var notes = new List<string>();

        if (source is "outlook" or "ews" or "all")
        {
            try
            {
                collected.AddRange(await _skills.ReadInboxAsync(query, maxResults, ctx.Ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"outlook/ews skipped: {ex.Message}");
            }
        }

        if (source is "gmail" or "all")
        {
            try
            {
                collected.AddRange(await _skills.ReadGmailAsync(query, maxResults, ctx.Ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"gmail skipped: {ex.Message}");
            }
        }

        if (collected.Count == 0)
        {
            var noteSuffix = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "";
            ctx.Observations.Add($"{skillName}: no emails found (source={source}){noteSuffix}.");
        }
        else
        {
            foreach (var m in collected.Take(15))
                ctx.Sources.Add(new MailSourceRef(m.Id, m.Subject, m.SenderName, m.Subject, m.ReceivedAt));

            var lines = collected.Take(15).Select(m =>
                $"- id={m.Id} | from={m.SenderName} | subject=\"{m.Subject}\" | at={m.ReceivedAt.ToLocalTime():MM-dd HH:mm} | snippet={m.Snippet}");
            ctx.Observations.Add($"{skillName}: {collected.Count} email(s) (source={source}):\n" + string.Join("\n", lines));
        }
        ctx.CompletedSteps.Add($"{skillName}:list:{source}");
    }

    private async Task DetailAsync(SkillExecutionContext ctx, string source)
    {
        var skillName = ctx.EffectiveName(Name);
        var mailId = ctx.GetArg("mail_id") ?? ctx.TargetMailId ?? "";
        if (string.IsNullOrWhiteSpace(mailId))
        {
            ctx.Observations.Add($"{skillName}: missing mail_id for detail.");
            return;
        }

        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        MailItem? mail = null;
        try
        {
            mail = source == "gmail"
                ? await _skills.GetGmailDetailAsync(mailId, ctx.Ct)
                : await _skills.GetMailDetailAsync(mailId, ctx.Ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (source != "all")
            {
                ctx.Observations.Add($"{skillName}: cannot read '{mailId}' from {source}: {ex.Message}");
                return;
            }
        }

        if (mail is null && source == "all")
        {
            try { mail = await _skills.GetGmailDetailAsync(mailId, ctx.Ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        if (mail is null)
        {
            ctx.Observations.Add($"{skillName}: email '{mailId}' not found (source={source}).");
            return;
        }

        ctx.Sources.Add(new MailSourceRef(mail.Id, mail.Subject, mail.SenderName, mail.Subject, mail.ReceivedAt));
        var body = string.IsNullOrWhiteSpace(mail.Body) ? mail.Snippet : mail.Body;
        if (body.Length > 4000) body = body[..4000] + "\n...(truncated)";
        ctx.Observations.Add($"manage_mail: id={mail.Id} from={mail.SenderName} subject=\"{mail.Subject}\" at={mail.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{body}");
        ctx.CompletedSteps.Add($"manage_mail:detail:{mail.Id}");
    }

    private async Task SyncAsync(SkillExecutionContext ctx, string source)
    {
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{ctx.EffectiveName(Name)}", StatusText));
        var res = await _skills.SyncMailAsync(ctx.ConversationId, source, ctx.Ct);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add($"manage_mail:sync:{source}");
    }
}
