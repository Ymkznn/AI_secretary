using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Resources;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Zero-click browser active-tab form reader & prefill skill via Browser Extension.
/// </summary>
public sealed class BrowserFormSkill : IBasicSkill
{
    private readonly BrowserFormBridgeService _bridge;

    public BrowserFormSkill(BrowserFormBridgeService bridge)
    {
        _bridge = bridge;
    }

    public string Name => "browser_form";
    public string DisplayDescription => "Read and prefill browser tab forms via Extension";
    public string MainAgentSpec => """
            browser_form
               - Purpose: Read form fields from the active Chrome/Edge tab or prefill fields via Browser Extension (never auto-submits).
               - Args: {"action": "read|fill", "fills": [{"field_id": "aisec_0", "value": "..."}]}
            
        """;
    public string PlannerSpec => """browser_form: Read or prefill active browser tab form via Extension (never submits). args: {"action":"read|fill","fills":[{"field_id":"...","value":"..."}]}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "read").Trim().ToLowerInvariant();
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Interacting with browser form..."));

        if (action == "read")
        {
            var form = await _bridge.RequestActiveTabFormAsync(TimeSpan.FromSeconds(5), ctx.Ct);
            if (form is null)
            {
                ctx.Observations.Add("browser_form: extension did not respond within 5s (ensure Chrome/Edge extension is loaded and tab refreshed).");
                return;
            }

            if (form.Fields.Count == 0)
            {
                ctx.Observations.Add($"browser_form: no editable fields found on \"{form.Title}\" ({form.Url}).");
                return;
            }

            var lines = form.Fields.Take(40).Select(f =>
                $"- field_id={f.FieldId} | label={f.Label} | type={f.ControlType} | current={f.CurrentValue ?? ""}" +
                (f.Options is { Count: > 0 } ? $" | options=[{string.Join(", ", f.Options.Take(10))}]" : ""));
            ctx.Observations.Add($"browser_form: \"{form.Title}\" ({form.Url})\nfields:\n" + string.Join("\n", lines));
            ctx.CompletedSteps.Add("browser_form:read");
            return;
        }

        if (action != "fill")
        {
            ctx.Observations.Add($"browser_form: unsupported action '{action}'.");
            return;
        }

        var fills = new List<BrowserFillAction>();
        if (ctx.Args.ValueKind == JsonValueKind.Object &&
            ctx.Args.TryGetProperty("fills", out var fillsEl) &&
            fillsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fillsEl.EnumerateArray())
            {
                var fid = item.TryGetProperty("field_id", out var fEl) ? fEl.GetString() : null;
                var val = item.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(fid))
                    fills.Add(new BrowserFillAction(fid!, val));
            }
        }

        if (fills.Count == 0)
        {
            ctx.Observations.Add("browser_form: fill requires at least one valid entry in fills.");
            return;
        }

        var preview = string.Join("\n", fills.Take(10).Select(f => $"- {f.FieldId} = {f.Value}"));
        var approved = await ctx.ConfirmAsync(
            "Browser Form Prefill",
            $"Prefill {fills.Count} field(s) (will not auto-submit):\n\n{preview}");
        if (!approved)
        {
            ctx.Observations.Add("browser_form: user declined prefill.");
            return;
        }

        _bridge.DispatchApplyFills(fills);
        ctx.Observations.Add($"browser_form: dispatched prefill for {fills.Count} field(s) (not submitted).");
        ctx.CompletedSteps.Add("browser_form:fill");
    }
}
