using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Skills;

public sealed class LaunchAppSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public LaunchAppSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "launch_app";
    public string DisplayDescription => "Launch local Windows applications";
    public string MainAgentSpec => """
            launch_app
               - Purpose: Launch a whitelisted local Windows application (Notepad, Calculator, Chrome, Edge, VS Code, Terminal, Office, etc.).
               - Args: {"app_name": "app name", "arguments": "optional args"}
            
        """;
    public string PlannerSpec => """launch_app: Launch local Windows app. args: {"app_name":"...","arguments":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var appName = ctx.GetArg("app_name") ?? "";
        var appArgs = ctx.GetArg("arguments");
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Launching application..."));
        var launchRes = _skills.LaunchApp(appName, appArgs);
        ctx.Observations.Add(launchRes.Message);
        if (launchRes.Success)
            ctx.CompletedSteps.Add($"launch_app:{appName}");
    }
}
