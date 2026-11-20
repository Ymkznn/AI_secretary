using AiSecretary.Desktop.Agent;

namespace AiSecretary.Desktop.Skills;

public sealed class DismissReminderSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public DismissReminderSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "dismiss_reminder";
    public string DisplayDescription => "List, update re-remind interval, or dismiss pinned desktop reminder cards";
    public string StatusText => "Updating pinned reminders...";
    public string MainAgentSpec => """
            dismiss_reminder
               - Purpose: Dismiss a pinned desktop reminder card, list active pinned reminders, or update their periodic re-remind interval (`re_remind_minutes`, e.g. 60 for 1 hour, 0 to stop re-reminding while keeping it pinned).
               - Args: {"action": "dismiss|update_interval|list", "keyword": "title keyword(s) (comma-separated), id, or all", "re_remind_minutes": 60}
            
        """;
    public string PlannerSpec => """dismiss_reminder: Dismiss, list, or change re-remind interval (`re_remind_minutes`) of pinned reminder cards. args: {"action":"dismiss|update_interval|list","keyword":"title keyword(s) (comma-separated), id, or all","re_remind_minutes":60}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var kw = ctx.GetArg("keyword") ?? ctx.GetArg("id") ?? ctx.GetArg("title") ?? "";
        var action = (ctx.GetArg("action") ?? "").Trim().ToLowerInvariant();
        var minsRaw = ctx.GetArg("re_remind_minutes") ?? ctx.GetArg("interval_minutes");
        bool hasMins = int.TryParse(minsRaw, out var mins);

        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));

        if (action == "list")
        {
            var listRes = await _skills.UpdatePinnedReRemindAsync(kw, null, ctx.Ct);
            ctx.Observations.Add(listRes);
            ctx.CompletedSteps.Add($"{skillName}:list");
            return;
        }

        if (action is "update" or "update_interval" || hasMins)
        {
            int targetMins = hasMins ? mins : 60;
            var updateRes = await _skills.UpdatePinnedReRemindAsync(string.IsNullOrWhiteSpace(kw) ? "all" : kw, targetMins, ctx.Ct);
            ctx.Observations.Add(updateRes);
            ctx.CompletedSteps.Add($"{skillName}:update_interval:{targetMins}m");
            return;
        }

        var res = await _skills.DismissPinnedReminderAsync(kw, ctx.Ct);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add($"{skillName}:{kw}");
    }
}
