using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Resources;

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
    public string DisplayDescription => "Create and delete custom PowerShell skills (requires approval)";
    public string MainAgentSpec => """
            manage_custom_skill
               - Purpose: Create or delete custom PowerShell/CLI skills (sandboxed by default; creation/execution requires user confirmation).
               - Args: {"action": "create|delete", "name": "snake_case_name", "description": "short summary", "command": "PowerShell script"}
            
        """;
    public string PlannerSpec => """manage_custom_skill: Create or delete custom PowerShell skill (user-confirmed). args: {"action":"create|delete","name":"snake_case","description":"...","command":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "create").Trim().ToLowerInvariant();
        var name = ctx.GetArg("name") ?? "";

        if (action == "create")
        {
            var desc = ctx.GetArg("description") ?? "";
            var cmd = ctx.GetArg("command") ?? "";
            await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Waiting for custom skill confirmation..."));
            var (ok, msg) = await _customSkills.CreateAsync(name, desc, cmd, ctx.ConfirmAsync, ctx.Ct);
            ctx.Observations.Add(msg);
            if (ok) ctx.CompletedSteps.Add($"manage_custom_skill:create:{name}");
            return;
        }

        if (action == "delete")
        {
            await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Waiting for custom skill confirmation..."));
            var (ok, msg) = await _customSkills.DeleteAsync(name, ctx.ConfirmAsync, ctx.Ct);
            ctx.Observations.Add(msg);
            if (ok) ctx.CompletedSteps.Add($"manage_custom_skill:delete:{name}");
            return;
        }

        ctx.Observations.Add($"manage_custom_skill: unsupported action '{action}'.");
    }
}
