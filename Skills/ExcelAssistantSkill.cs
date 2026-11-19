using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Restricted Excel collaboration skill (reads active workbook context or writes safe formulas with backup).
/// </summary>
public sealed class ExcelAssistantSkill : IBasicSkill
{
    private readonly OfficeContextService _office;
    private readonly GuardService _guard;
    private readonly SandboxService _sandbox;
    private readonly SkillRuntime _skills;

    public ExcelAssistantSkill(OfficeContextService office, GuardService guard, SandboxService sandbox, SkillRuntime skills)
    {
        _office = office;
        _guard = guard;
        _sandbox = sandbox;
        _skills = skills;
    }

    public string Name => "excel_assistant";
    public string DisplayDescription => "Inspect, read, or write cells/formulas in Excel";
    public string StatusText => "Processing Excel workbook...";
    public string MainAgentSpec => """
            excel_assistant
               - Purpose: Inspect active Excel workbook selection/headers/formulas or safely write cells/formulas (backup + confirmation required).
               - Args: {"action": "inspect_selection|write_cells", "writes": [{"cell": "F2", "value": "..."}]}
            
        """;
    public string PlannerSpec => """excel_assistant: Inspect active Excel selection or safely write cells/formulas. args: {"action":"inspect_selection|write_cells","writes":[{"cell":"F2","value":"..."}]}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        var action = (ctx.GetArg("action") ?? "inspect_selection").Trim().ToLowerInvariant();
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));

        if (action == "inspect_selection")
        {
            var (ok, filePath, summary) = await _office.InspectExcelSelectionAsync(ctx.Ct);
            if (!ok)
            {
                ctx.Observations.Add(summary);
                return;
            }

            if (!await _skills.EnsureExternalPathAccessAsync(filePath, skillName, ctx.ConfirmAsync, ctx.Ct))
            {
                ctx.Observations.Add($"{skillName}: user denied read permission for {filePath}.");
                return;
            }

            ctx.Observations.Add(summary);
            ctx.CompletedSteps.Add($"{skillName}:inspect_selection");
            return;
        }

        if (action != "write_cells")
        {
            ctx.Observations.Add($"{skillName}: unsupported action '{action}'.");
            return;
        }

        var writes = new List<(string Cell, string Value)>();
        if (ctx.Args.ValueKind == JsonValueKind.Object &&
            ctx.Args.TryGetProperty("writes", out var writesEl) &&
            writesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in writesEl.EnumerateArray())
            {
                var cell = item.TryGetProperty("cell", out var cEl) ? cEl.GetString() : null;
                var val = item.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(cell))
                {
                    var fCheck = _guard.CheckExcelFormula(val, skillName);
                    if (!fCheck.Allowed)
                    {
                        ctx.Observations.Add($"excel_assistant: blocked unsafe formula: {fCheck.Reason}");
                        return;
                    }
                    writes.Add((cell!, val));
                }
            }
        }

        if (writes.Count == 0)
        {
            ctx.Observations.Add("excel_assistant: write_cells requires at least one valid writes entry.");
            return;
        }

        var preview = string.Join("\n", writes.Take(10).Select(w => $"- {w.Cell} = {w.Value}"));
        var confirmTitle = await ctx.TextAsync("confirm_excel_write_title", "Excel Write Confirmation");
        var confirmBody = await ctx.TextAsync(
            "confirm_excel_write_body",
            "Write {0} cell(s) to the active Excel sheet (backup created automatically):\n\n{1}",
            writes.Count,
            preview);
        var approved = await ctx.ConfirmAsync(confirmTitle, confirmBody);
        if (!approved)
        {
            ctx.Observations.Add("excel_assistant: user declined write.");
            return;
        }

        var (_, _, msg) = await _office.WriteExcelCellsAsync(writes, ctx.Ct);
        ctx.Observations.Add(msg);
        ctx.CompletedSteps.Add("excel_assistant:write_cells");
    }
}
