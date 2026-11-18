using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Skills;

public sealed class SetQuietModeSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public SetQuietModeSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "set_quiet_mode";
    public string DisplayDescription => "Enable or disable Do-Not-Disturb quiet mode";
    public string MainAgentSpec => """
            set_quiet_mode
               - Purpose: Enable or disable Do-Not-Disturb quiet mode.
               - Args: {"until": "ISO-8601 end time (null/empty to disable)", "allow_urgent": true, "reason": "optional"}
            
        """;
    public string PlannerSpec => """set_quiet_mode: Enable/disable Do-Not-Disturb quiet mode. args: {"until":"ISO-8601 or null","allow_urgent":true,"reason":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var untilRaw = ctx.GetArg("until");
        DateTimeOffset? until = DateTimeOffset.TryParse(untilRaw, out var parsedUntil) ? parsedUntil : null;
        var allowUrgent = !string.Equals(ctx.GetArg("allow_urgent"), "false", StringComparison.OrdinalIgnoreCase);
        var reason = ctx.GetArg("reason");
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Updating quiet mode..."));
        var (msg, upcoming) = await _skills.SetQuietModeAsync(until, allowUrgent, reason, ctx.Ct);
        if (upcoming.Count > 0)
        {
            var listText = string.Join(", ", upcoming.Select(r => $"{r.RemindAt.ToLocalTime():HH:mm} \"{r.Title}\""));
            ctx.Observations.Add($"{msg}\nNote: upcoming one-shot reminders scheduled in quiet window: {listText}.");
        }
        else
        {
            ctx.Observations.Add(msg);
        }
        ctx.CompletedSteps.Add("set_quiet_mode");
    }
}
