using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
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
    public string DisplayDescription => "Read form/DOM and prefill browser tab forms via Extension";
    public string StatusText => "Interacting with browser form...";
    public string MainAgentSpec => """
            browser_form
               - Purpose: Read form fields, inspect up to the full page DOM (on-demand), or prefill fields in the active Chrome/Edge tab via Browser Extension (never auto-submits).
               - Args: {"action": "read|read_dom|fill", "selector": "optional CSS selector for read_dom", "max_chars": 20000, "fills": [{"field_id": "aisec_top_0", "value": "...", "label": "optional field label"}]}
               - fill reports exactly which fields were applied/missed (with read-back values); only claim success for fields listed as applied.
            
        """;
    public string PlannerSpec => """browser_form: Read form fields, full page DOM (read_dom), or prefill active browser tab via Extension (never submits). args: {"action":"read|read_dom|fill","selector":"optional","max_chars":20000,"fills":[{"field_id":"...","value":"...","label":"optional"}]}; fill returns applied/missed fields - never claim unlisted fields were filled.""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "read").Trim().ToLowerInvariant();
        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));

        if (action == "read")
        {
            var form = await _bridge.RequestActiveTabFormAsync(TimeSpan.FromSeconds(5), ctx.Ct);
            if (form is null)
            {
                ctx.MarkFailed();
                ctx.Observations.Add("browser_form: extension did not respond within 5s (ensure Chrome/Edge extension is loaded and tab refreshed).");
                return;
            }

            if (form.Fields.Count == 0)
            {
                ctx.Observations.Add($"browser_form: no editable fields found on \"{form.Title}\" ({form.Url}).");
                return;
            }

            var focused = form.Fields.FirstOrDefault(f => f.IsFocused);
            var shown = form.Fields.Take(40).ToList();
            if (focused is not null && !shown.Any(f => f.FieldId == focused.FieldId))
                shown.Add(focused);

            var lines = shown.Select(f =>
                $"- field_id={f.FieldId} | label={f.Label} | type={f.ControlType} | current={f.CurrentValue ?? ""}" +
                (f.Options is { Count: > 0 } ? $" | options=[{string.Join(", ", f.Options.Take(10))}]" : "") +
                (f.IsFocused ? " | [FOCUSED]" : ""));
            var focusHeader = focused is not null
                ? $"\nfocused_field: {focused.FieldId} | label={focused.Label}"
                : "";
            ctx.Observations.Add($"browser_form: \"{form.Title}\" ({form.Url}){focusHeader}\nfields:\n" + string.Join("\n", lines));
            ctx.CompletedSteps.Add("browser_form:read");
            return;
        }

        if (action == "read_dom")
        {
            var selector = ctx.GetArg("selector");
            // Default lowered from 80k: every read_dom result is re-sent on each subsequent LLM call of the turn.
            var maxChars = int.TryParse(ctx.GetArg("max_chars"), out var mc) ? Math.Clamp(mc, 1000, 120000) : 20000;
            var dom = await _bridge.RequestActiveTabDomAsync(selector, maxChars, TimeSpan.FromSeconds(8), ctx.Ct);
            if (dom is null)
            {
                ctx.MarkFailed();
                ctx.Observations.Add("browser_form: extension did not respond within 8s for read_dom (ensure Chrome/Edge extension is loaded and tab refreshed).");
                return;
            }
            if (!string.IsNullOrWhiteSpace(dom.Error))
            {
                ctx.MarkFailed();
                ctx.Observations.Add($"browser_form: read_dom failed on \"{dom.Title}\" ({dom.Url}): {dom.Error}");
                return;
            }

            var html = CompactHtmlForContext(dom.Html ?? "");
            var text = dom.Text ?? "";
            var truncated = dom.Truncated;
            if (text.Length > maxChars) { text = text[..maxChars]; truncated = true; }
            // Text is usually what the model needs; HTML gets whatever budget is left (at least a quarter).
            var htmlBudget = Math.Max(maxChars / 4, maxChars - text.Length);
            if (html.Length > htmlBudget) { html = html[..htmlBudget]; truncated = true; }
            var note = truncated ? " [truncated]" : "";
            ctx.Observations.Add($"browser_form: DOM snapshot of \"{dom.Title}\" ({dom.Url}){note}\n--- TEXT ---\n{text}\n--- HTML (compacted) ---\n{html}");
            ctx.CompletedSteps.Add("browser_form:read_dom");
            return;
        }

        if (action != "fill")
        {
            ctx.MarkFailed();
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
                if (item.ValueKind != JsonValueKind.Object) continue;
                var fid = item.TryGetProperty("field_id", out var fEl) ? fEl.ToString() : null;
                var val = item.TryGetProperty("value", out var vEl)
                    ? (vEl.ValueKind == JsonValueKind.String ? vEl.GetString() ?? "" : vEl.ToString())
                    : "";
                var lbl = item.TryGetProperty("label", out var lEl) ? lEl.ToString() : null;
                if (!string.IsNullOrWhiteSpace(fid) || !string.IsNullOrWhiteSpace(lbl))
                    fills.Add(new BrowserFillAction(fid ?? "", val, lbl));
            }
        }

        if (fills.Count == 0)
        {
            ctx.Observations.Add("browser_form: fill requires at least one valid entry in fills.");
            return;
        }

        // Re-read the live form first: re-tags the DOM (data-aisec-id may be lost after a re-render) and lets us
        // attach label/name so the extension can still locate the field if the id no longer matches.
        var live = await _bridge.RequestActiveTabFormAsync(TimeSpan.FromSeconds(5), ctx.Ct);
        if (live is not null && live.Fields.Count > 0)
        {
            fills = fills.Select(f =>
            {
                var field = live.Fields.FirstOrDefault(x => string.Equals(x.FieldId, f.FieldId, StringComparison.OrdinalIgnoreCase))
                    ?? (string.IsNullOrWhiteSpace(f.Label) ? null : live.Fields.FirstOrDefault(x =>
                        string.Equals(x.Label?.Trim(), f.Label.Trim(), StringComparison.OrdinalIgnoreCase)));
                return field is null
                    ? f
                    : f with { FieldId = field.FieldId, Label = f.Label ?? field.Label, Name = field.Name };
            }).ToList();
        }

        var preview = string.Join("\n", fills.Take(10).Select(f => $"- {(string.IsNullOrWhiteSpace(f.Label) ? f.FieldId : f.Label)} = {f.Value}"));
        var confirmTitle = await ctx.TextAsync("confirm_browser_fill_title", "Browser Form Prefill");
        var confirmBody = await ctx.TextAsync(
            "confirm_browser_fill_body",
            "Prefill {0} field(s) (will not auto-submit):\n\n{1}",
            fills.Count,
            preview);
        var approved = await ctx.ConfirmAsync(confirmTitle, confirmBody);
        if (!approved)
        {
            ctx.MarkDenied();
            ctx.Observations.Add("browser_form: user declined prefill.");
            return;
        }

        var result = await _bridge.RequestApplyFillsAsync(fills, TimeSpan.FromSeconds(10), ctx.Ct);
        if (result is null)
        {
            ctx.MarkFailed();
            ctx.Observations.Add("browser_form: FILL NOT VERIFIED - the browser extension did not report back within 10s. " +
                                 "Do NOT tell the user the fields were filled; ask them to check the tab / reload the extension.");
            return;
        }

        var appliedItems = result.Items.Where(i => i.Applied).ToList();
        var missedItems = result.Items.Where(i => !i.Applied).ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append($"browser_form: fill result on \"{result.Title}\" ({result.Url}): applied {appliedItems.Count}/{fills.Count} field(s) (not submitted).");
        if (!string.IsNullOrWhiteSpace(result.Error))
            sb.Append($"\nerror: {result.Error}");
        if (appliedItems.Count > 0)
            sb.Append("\napplied (read back from page):\n" + string.Join("\n", appliedItems.Take(20).Select(i =>
                $"- {i.FieldId}{(string.IsNullOrWhiteSpace(i.Label) ? "" : $" ({i.Label})")} = {i.ReadBackValue}")));
        if (missedItems.Count > 0)
            sb.Append("\nNOT filled (field not found):\n" + string.Join("\n", missedItems.Take(20).Select(i =>
                $"- {i.FieldId}{(string.IsNullOrWhiteSpace(i.Label) ? "" : $" ({i.Label})")}: {i.Error}")));
        if (appliedItems.Count == 0)
            sb.Append("\nNothing was filled. Do NOT claim success; re-run browser_form read to get current field_ids, or tell the user.");
        ctx.Observations.Add(sb.ToString());
        if (appliedItems.Count > 0)
            ctx.CompletedSteps.Add("browser_form:fill");
    }

    private static readonly Regex NoiseAttrRegex = new(
        @"\s(?:style|class|data-(?!aisec-id)[\w-]+|on\w+|jsaction|jscontroller|jsname|jsmodel|aria-describedby|aria-labelledby|tabindex|role)=(?:""[^""]*""|'[^']*'|[^\s>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommentRegex = new(@"<!--(?!\s*\[Frame:).*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex EmptyTagRegex = new(@"<(div|span)>\s*</\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Strips styling/framework noise from raw page HTML so a DOM snapshot costs far fewer tokens.</summary>
    internal static string CompactHtmlForContext(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var s = CommentRegex.Replace(html, "");
        s = NoiseAttrRegex.Replace(s, "");
        s = WhitespaceRegex.Replace(s, " ");
        for (var i = 0; i < 3; i++)
        {
            var next = EmptyTagRegex.Replace(s, "");
            if (next.Length == s.Length) break;
            s = next;
        }
        return s.Trim();
    }
}
