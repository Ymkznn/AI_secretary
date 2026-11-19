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
    public string DisplayDescription => "Dismiss pinned desktop reminder cards";
    public string StatusText => "Dismissing reminder...";
    public string MainAgentSpec => """
            dismiss_reminder
               - Purpose: Dismiss a pinned desktop reminder card by ID or keyword (or "all").
               - Args: {"keyword": "title keyword, id, or all"}
            
        """;
    public string PlannerSpec => """dismiss_reminder: Dismiss pinned reminder card. args: {"keyword":"title, id, or all"}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var kw = ctx.GetArg("keyword") ?? "";
        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var res = await _skills.DismissPinnedReminderAsync(kw);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add($"{skillName}:{kw}");
    }
}
