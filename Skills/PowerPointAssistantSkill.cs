using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Resources;
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
    public string MainAgentSpec => """
            powerpoint_assistant
               - Purpose: Inspect active PowerPoint slide/outline/notes or safely update slide text/notes (backup + confirmation required).
               - Args: {"action": "inspect_slide|inspect_outline|update_slide_text", "slide_index": 1, "shape_index": 2, "text": "...", "notes": "..."}
            
        """;
    public string PlannerSpec => """powerpoint_assistant: Inspect or safely update active PowerPoint slide/outline/notes. args: {"action":"inspect_slide|inspect_outline|update_slide_text","slide_index":1,"shape_index":2,"text":"...","notes":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "inspect_slide").Trim().ToLowerInvariant();
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Processing PowerPoint deck..."));

        if (action is "inspect_slide" or "inspect_outline")
        {
            var (ok, filePath, summary) = await _office.InspectPowerPointAsync(action == "inspect_outline", ctx.Ct);
            if (!ok)
            {
                ctx.Observations.Add(summary);
                return;
            }

            if (!await _skills.EnsureExternalPathAccessAsync(filePath, Name, ctx.ConfirmAsync))
            {
                ctx.Observations.Add($"powerpoint_assistant: user denied read permission for {filePath}.");
                return;
            }

            ctx.Observations.Add(summary);
            ctx.CompletedSteps.Add($"powerpoint_assistant:{action}");
            return;
        }

        if (action != "update_slide_text")
        {
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

        var approved = await ctx.ConfirmAsync(
            "PowerPoint Update Confirmation",
            $"Update slide #{slideIndex} (backup created automatically):\n" +
            (shapeIndex.HasValue ? $"- Shape #{shapeIndex}: {text}\n" : "") +
            (notes is not null ? $"- Notes: {notes}" : ""));
        if (!approved)
        {
            ctx.Observations.Add("powerpoint_assistant: user declined update.");
            return;
        }

        var (_, _, msg) = await _office.UpdatePowerPointSlideAsync(slideIndex, shapeIndex, text, notes, ctx.Ct);
        ctx.Observations.Add(msg);
        ctx.CompletedSteps.Add("powerpoint_assistant:update_slide_text");
    }
}
