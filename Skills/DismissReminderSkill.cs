using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Skills;

public sealed class DismissReminderSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public DismissReminderSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "dismiss_reminder";
    public string DisplayDescription => "Dismiss pinned desktop reminder cards";
    public string MainAgentSpec => """
            dismiss_reminder
               - Purpose: Dismiss a pinned desktop reminder card by ID or keyword (or "all").
               - Args: {"keyword": "title keyword, id, or all"}
            
        """;
    public string PlannerSpec => """dismiss_reminder: Dismiss pinned reminder card. args: {"keyword":"title, id, or all"}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var kw = ctx.GetArg("keyword") ?? "";
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Dismissing reminder..."));
        var res = await _skills.DismissPinnedReminderAsync(kw);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add($"dismiss_reminder:{kw}");
    }
}
