using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Restricted PowerPoint collaboration skill (reads slides/outline or updates slide text/notes with backup).
/// </summary>
public sealed class PowerPointAssistantSkill : IBasicSkill
{
    private readonly OfficeContextService _office;
    private readonly SandboxService _sandbox;
    private readonly SkillRuntime _skills;

    public PowerPointAssistantSkill(OfficeContextService office, SandboxService sandbox, SkillRuntime skills)
    {
        _office = office;
        _sandbox = sandbox;
        _skills = skills;
    }

    public string Name => "powerpoint_assistant";
    public string DisplayDescription => "Inspect, read, or update slides/notes in PowerPoint";
    public string StatusText => "Processing PowerPoint deck...";
    public string MainAgentSpec => """
            powerpoint_assistant
               - Purpose: Inspect PowerPoint slide/outline/notes/tables (for active window or a specific `file_path` .pptx) or safely update slide text/notes (backup + confirmation required).
               - Args: {"action": "inspect_slide|inspect_outline|update_slide_text", "file_path": "optional .pptx path", "slide_index": 1, "shape_index": 2, "text": "...", "notes": "..."}
            
        """;
    public string PlannerSpec => """powerpoint_assistant: Inspect or safely update PowerPoint slide/outline/notes/tables (active window or `file_path`). args: {"action":"inspect_slide|inspect_outline|update_slide_text","file_path":"optional .pptx path","slide_index":1,"shape_index":2,"text":"...","notes":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        var action = (ctx.GetArg("action") ?? "inspect_slide").Trim().ToLowerInvariant();
        var targetPath = ctx.GetArg("file_path") ?? ctx.GetArg("path");
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));

        if (action is "inspect_slide" or "inspect_outline")
        {
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                if (!await _skills.EnsureExternalPathAccessAsync(targetPath, skillName, ctx.ConfirmAsync, ctx.Ct))
                {
                    ctx.MarkDenied();
                    ctx.Observations.Add($"powerpoint_assistant: user denied read permission for {targetPath}.");
                    return;
                }
            }

            var (ok, filePath, summary) = await _office.InspectPowerPointAsync(action == "inspect_outline" || !string.IsNullOrWhiteSpace(targetPath), ctx.Ct, targetPath);
            if (!ok)
            {
                ctx.MarkFailed();
                ctx.Observations.Add(summary);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetPath) && !await _skills.EnsureExternalPathAccessAsync(filePath, skillName, ctx.ConfirmAsync, ctx.Ct))
            {
                ctx.MarkDenied();
                ctx.Observations.Add($"powerpoint_assistant: user denied read permission for {filePath}.");
                return;
            }

            ctx.Observations.Add(summary);
            ctx.CompletedSteps.Add($"powerpoint_assistant:{action}");
            return;
        }

        if (action != "update_slide_text")
        {
            ctx.MarkFailed();
            ctx.Observations.Add($"powerpoint_assistant: unsupported action '{action}'.");
            return;
        }

        int slideIndex = int.TryParse(ctx.GetArg("slide_index"), out var si) ? si : 1;
        int? shapeIndex = int.TryParse(ctx.GetArg("shape_index"), out var shi) ? shi : null;
        var text = ctx.GetArg("text");
        var notes = ctx.GetArg("notes");

        if (!shapeIndex.HasValue && notes is null)
        {
            ctx.Observations.Add("powerpoint_assistant: update_slide_text requires shape_index+text or notes.");
            return;
        }

        var details = (shapeIndex.HasValue ? $"- Shape #{shapeIndex}: {text}\n" : "")
                    + (notes is not null ? $"- Notes: {notes}" : "");
        var confirmTitle = await ctx.TextAsync("confirm_ppt_update_title", "PowerPoint Update Confirmation");
        var confirmBody = await ctx.TextAsync(
            "confirm_ppt_update_body",
            "Update slide #{0} (backup created automatically):\n{1}",
            slideIndex,
            details);
        var approved = await ctx.ConfirmAsync(confirmTitle, confirmBody);
        if (!approved)
        {
            ctx.MarkDenied();
            ctx.Observations.Add("powerpoint_assistant: user declined update.");
            return;
        }

        var (_, _, msg) = await _office.UpdatePowerPointSlideAsync(slideIndex, shapeIndex, text, notes, ctx.Ct);
        ctx.Observations.Add(msg);
        ctx.CompletedSteps.Add("powerpoint_assistant:update_slide_text");
    }
}
