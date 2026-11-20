using AiSecretary.Desktop.Agent;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Consolidated custom PowerShell/CLI skill manager (create | delete).
/// </summary>
public sealed class ManageCustomSkillSkill : IBasicSkill
{
    private readonly CustomSkillService _customSkills;

    public ManageCustomSkillSkill(CustomSkillService customSkills)
    {
        _customSkills = customSkills;
    }

    public string Name => "manage_custom_skill";
    public string DisplayDescription => "List, create, update, and delete custom PowerShell skills (requires approval)";
    public string StatusText => "Waiting for custom skill confirmation...";
    public IReadOnlyList<string> LegacyAliases => new[] { "create_skill", "delete_skill" };
    public string MainAgentSpec => """
            manage_custom_skill
               - Purpose: List, create, update, or delete custom PowerShell/CLI skills (sandboxed by default; create/update/delete/execution requires user confirmation).
               - Args: {"action": "list|create|update|delete", "name": "snake_case_name", "description": "short summary", "command": "PowerShell script"}
            
        """;
    public string PlannerSpec => """manage_custom_skill: List, create, update, or delete custom PowerShell skills (user-confirmed). args: {"action":"list|create|update|delete","name":"snake_case","description":"...","command":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        var action = (ctx.GetArg("action") ?? "list").Trim().ToLowerInvariant();
        var name = ctx.GetArg("name") ?? "";

        if (action == "list")
        {
            var skills = await _customSkills.ListAsync(ctx.Ct);
            if (skills.Count == 0)
            {
                ctx.Observations.Add("manage_custom_skill: no custom skills defined yet.");
            }
            else
            {
                var lines = skills.Select(s => $"- {s.Name}: {s.Description}\n  command: {s.Command}");
                ctx.Observations.Add($"manage_custom_skill: {skills.Count} custom skill(s):\n" + string.Join("\n", lines));
            }
            ctx.CompletedSteps.Add($"{skillName}:list");
            return;
        }

        if (action is "create" or "update")
        {
            var desc = ctx.GetArg("description") ?? "";
            var cmd = ctx.GetArg("command") ?? "";
            await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
            var (ok, msg) = await _customSkills.CreateAsync(name, desc, cmd, ctx.ConfirmAsync, ctx.Ct);
            ctx.Observations.Add(msg);
            if (ok) ctx.CompletedSteps.Add($"{skillName}:{action}:{name}");
            return;
        }

        if (action == "delete")
        {
            await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
            var (ok, msg) = await _customSkills.DeleteAsync(name, ctx.ConfirmAsync, ctx.Ct);
            ctx.Observations.Add(msg);
            if (ok) ctx.CompletedSteps.Add($"{skillName}:delete:{name}");
            return;
        }

        ctx.Observations.Add($"manage_custom_skill: unsupported action '{action}'.");
    }
}
