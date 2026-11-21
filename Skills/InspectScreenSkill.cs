using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

public sealed class InspectScreenSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;
    private readonly ActivityMonitorService _activity;

    public InspectScreenSkill(SkillRuntime skills, ActivityMonitorService activity)
    {
        _skills = skills;
        _activity = activity;
    }

    public string Name => "inspect_screen";
    public string DisplayDescription => "Inspect active window UIA fields, list windows, or capture screen";
    public string StatusText => "Inspecting screen...";
    public string MainAgentSpec => """
            inspect_screen
               - Purpose: Read UIA fields of the last external window (mode=uia, 0 screenshot), list open windows (list), or capture window screenshot (capture, requires user confirmation).
               - Args: {"mode": "uia|list|capture", "window_title": "optional title keyword"}
            
        """;
    public string PlannerSpec => """inspect_screen: Read UIA fields (mode=uia), list windows (list), or capture screen (capture). args: {"mode":"uia|list|capture","window_title":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var mode = ctx.GetArg("mode") ?? "capture";
        var titleKw = ctx.GetArg("window_title");

        if (mode.Equals("uia", StringComparison.OrdinalIgnoreCase))
        {
            var target = _activity.GetLastExternalTarget();
            if (target is null)
            {
                ctx.Observations.Add("inspect_screen: no recent external foreground window recorded.");
                return;
            }
            var refreshed = await _activity.RefreshExternalTargetUiaAsync(target.Hwnd, ctx.Ct) ?? target;
            var fields = refreshed.FormFieldLabels is { Count: > 0 }
                ? string.Join(", ", refreshed.FormFieldLabels)
                : "(no visible input fields)";
            ctx.Observations.Add($"last_external_window: [{refreshed.ProcessName}] {refreshed.Title}\nis_form_like: {refreshed.IsFormLike}\nfields: {fields}");
            ctx.CompletedSteps.Add("inspect_screen:uia");
            return;
        }

        if (string.IsNullOrWhiteSpace(titleKw) && mode.Equals("capture", StringComparison.OrdinalIgnoreCase))
        {
            titleKw = _activity.GetLastExternalTarget()?.Title;
        }

        if (!mode.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var confirmTitle = await ctx.TextAsync("confirm_screen_capture_title", "Screen Capture Confirmation");
            var confirmBody = await ctx.TextAsync("confirm_screen_capture_body", "Allow capturing the active window/screen for AI inspection?");
            var approved = await ctx.ConfirmAsync(confirmTitle, confirmBody);
            if (!approved)
            {
                ctx.MarkDenied();
                ctx.Observations.Add("User denied screen capture.");
                return;
            }
        }
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{ctx.EffectiveName(Name)}", StatusText));
        var (ok, msg, filePath, wins) = _skills.InspectScreen(mode, titleKw);
        var winListText = wins.Count == 0
            ? "(none)"
            : string.Join("\n", wins.Take(20).Select(w => $"- {w.ProcessName}: {w.Title}"));

        if (mode.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Observations.Add($"open_windows:\n{winListText}");
            ctx.CompletedSteps.Add("inspect_screen:list");
        }
        else if (ok && !string.IsNullOrWhiteSpace(filePath))
        {
            ctx.Observations.Add($"{msg}\nopen_windows:\n{winListText}\n[Attachment: {filePath}]");
            ctx.CompletedSteps.Add("inspect_screen:capture");
        }
        else
        {
            ctx.MarkFailed();
            ctx.Observations.Add($"inspect_screen failed: {msg}");
        }
    }
}
