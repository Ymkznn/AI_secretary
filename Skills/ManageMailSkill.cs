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
    private string AvailableSourcesHint
    {
        get
        {
            var sources = new List<string>();
            if (_skills.IsOutlookConfigured) sources.Add("outlook");
            if (_skills.IsGmailAuthorized) sources.Add("gmail");
            if (sources.Count == 0) return "outlook|gmail|all";
            if (sources.Count > 1) sources.Add("all");
            return string.Join("|", sources);
        }
    }

    private string DefaultSource => _skills.IsOutlookConfigured ? "outlook" : (_skills.IsGmailAuthorized ? "gmail" : "outlook");

    public string MainAgentSpec => $$"""
            manage_mail
               - Purpose: List folders, list/search emails in any folder, read full email detail, or run background sync across connected mail providers ({{AvailableSourcesHint}}).
               - Args: {"action": "list|list_folders|detail|sync", "source": "{{AvailableSourcesHint}}", "folder": "optional folder name or inbox/sentitems/drafts", "query": "optional search query", "max_results": 10, "mail_id": "required when action=detail", "boost_seconds": "optional 15-300s temporary fast poll window when action=sync"}
            
        """;
    public string PlannerSpec => $$"""manage_mail: List folders, search/read emails in any folder, or sync connected mail ({{AvailableSourcesHint}}). args: {"action":"list|list_folders|detail|sync","source":"{{AvailableSourcesHint}}","folder":"optional","query":"...","max_results":10,"mail_id":"...","boost_seconds":180}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "list").Trim().ToLowerInvariant();
        var source = (ctx.GetArg("source") ?? DefaultSource).Trim().ToLowerInvariant();

        switch (action)
        {
            case "list":
                await ListAsync(ctx, source);
                break;
            case "list_folders":
                await ListFoldersAsync(ctx);
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

    private async Task ListFoldersAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        if (!_skills.IsOutlookConfigured)
        {
            ctx.Observations.Add($"{skillName}: Outlook/EWS is not configured; folder discovery is unavailable.");
            return;
        }
        var folders = await _skills.ListMailFoldersAsync(ctx.Ct);
        ctx.Observations.Add(folders.Count == 0
            ? $"{skillName}: no folders found."
            : $"{skillName}: folders ({folders.Count}):\n- " + string.Join("\n- ", folders));
        ctx.CompletedSteps.Add($"{skillName}:list_folders");
    }

    private async Task ListAsync(SkillExecutionContext ctx, string source)
    {
        var query = ctx.GetArg("query");
        var folder = ctx.GetArg("folder");
        int? maxResults = int.TryParse(ctx.GetArg("max_results"), out var mr) ? Math.Clamp(mr, 1, 50) : null;

        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var collected = new List<MailItem>();
        var notes = new List<string>();

        bool wantOutlook = source is "outlook" or "ews" or "all";
        bool wantGmail = source is "gmail" or "all";

        if (wantOutlook && (_skills.IsOutlookConfigured || source != "all"))
        {
            try
            {
                collected.AddRange(await _skills.ReadInboxAsync(query, maxResults, ctx.Ct, folder));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"outlook/ews skipped: {ex.Message}");
            }
        }

        if (wantGmail && _skills.IsGmailAuthorized)
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
        else if (source == "gmail" && !_skills.IsGmailAuthorized)
        {
            notes.Add("gmail is not connected (only Outlook/EWS is configured)");
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
        if (int.TryParse(ctx.GetArg("boost_seconds"), out var boostSecs) && boostSecs > 0)
            _skills.RequestSkillPollBoost(Name, """{"action":"sync","source":"all"}""", boostSecs);
        var res = await _skills.SyncMailAsync(ctx.ConversationId, source, ctx.Ct);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add($"manage_mail:sync:{source}");
    }
}
