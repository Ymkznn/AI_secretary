using AiSecretary.Desktop.Agent;

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
    public string StatusText => "Launching application...";
    public string MainAgentSpec => """
            launch_app
               - Purpose: Open a web URL in the user's default browser (pass the https:// URL directly as `app_name` with no browser name), or launch a whitelisted local Windows application (Notepad, Calculator, Edge, Chrome, VS Code, Office, etc.).
               - Args: {"app_name": "https:// URL (uses default browser) OR app name", "arguments": "optional args"}
            
        """;
    public string PlannerSpec => """launch_app: Open https:// URL in default browser (pass URL directly as app_name) or launch local Windows app. args: {"app_name":"https://... or app name","arguments":"optional"}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var appName = ctx.GetArg("app_name") ?? "";
        var appArgs = ctx.GetArg("arguments");
        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var launchRes = _skills.LaunchApp(appName, appArgs);
        ctx.Observations.Add(launchRes.Message);
        if (launchRes.Success)
            ctx.CompletedSteps.Add($"{skillName}:{appName}");
        else if (launchRes.Denied)
            ctx.MarkDenied();
        else
            ctx.MarkFailed();
    }
}
