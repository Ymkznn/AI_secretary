using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Consolidated long-term memory & user preference CRUD skill (record | search | detail | update | delete).
/// </summary>
public sealed class ManageFactsSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public ManageFactsSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "manage_facts";
    public string DisplayDescription => "Record, search, update, and delete long-term memory facts";
    public string MainAgentSpec => """
        manage_facts
           - Purpose: Record, search, inspect, update, or delete structured long-term memory facts (user_pref, document, project, email, general), AND manage cascade-deleted annotations attached to any of the 6 memory types (fact|workflow|page_intent|schedule|calendar|watcher).
           - Args: {"action": "record|search|detail|update|delete|annotate|list_annotations|update_annotation|delete_annotation", "fact_id": "for detail/update/delete", "summary": "...", "detail": "...", "evidence": "...", "category": "document|user_pref|project|email|general", "confidence": "observed|inferred", "tags": ["..."], "keyword": "for search", "limit": 10, "target_type": "fact|workflow|page_intent|schedule|calendar|watcher", "target_id": "for annotate/list_annotations", "annotation_id": "for update_annotation/delete_annotation", "note": "annotation text"}
        """;
    public string PlannerSpec => """manage_facts: Record/search/detail/update/delete facts, or annotate/list_annotations/update_annotation/delete_annotation on any memory (fact|workflow|page_intent|schedule|calendar|watcher). args: {"action":"record|search|detail|update|delete|annotate|list_annotations|update_annotation|delete_annotation","fact_id":"...","summary":"...","detail":"...","category":"user_pref|document|project|email|general","tags":[],"keyword":"...","target_type":"...","target_id":"...","annotation_id":"...","note":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "search").Trim().ToLowerInvariant();
        switch (action)
        {
            case "record":
                await RecordAsync(ctx);
                break;
            case "search":
                await SearchAsync(ctx);
                break;
            case "detail":
                await DetailAsync(ctx);
                break;
            case "update":
                await UpdateAsync(ctx);
                break;
            case "delete":
                await DeleteAsync(ctx);
                break;
            case "annotate":
                await AnnotateAsync(ctx);
                break;
            case "list_annotations":
                await ListAnnotationsAsync(ctx);
                break;
            case "update_annotation":
                await UpdateAnnotationAsync(ctx);
                break;
            case "delete_annotation":
                await DeleteAnnotationAsync(ctx);
                break;
            default:
                ctx.Observations.Add($"manage_facts: unsupported action '{action}'.");
                break;
        }
    }

    private async Task RecordAsync(SkillExecutionContext ctx)
    {
        var summary = ctx.GetArg("summary") ?? "";
        if (string.IsNullOrWhiteSpace(summary))
        {
            ctx.Observations.Add("manage_facts: missing summary for record.");
            return;
        }
        var detail = ctx.GetArg("detail") ?? summary;
        var evidence = ctx.GetArg("evidence");
        var category = ctx.GetArg("category") ?? "general";
        var confidence = ctx.GetArg("confidence") ?? "observed";
        var tags = ctx.GetStringArrayArg("tags");

        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing memory facts..."));
        var fact = await _skills.RecordFactAsync(ctx.ConversationId, summary, detail, evidence, confidence, category, tags, ctx.Ct);
        ctx.Observations.Add($"manage_facts: recorded id={fact.FactId} category={fact.Category} confidence={fact.Confidence} summary=\"{fact.Summary}\".");
        ctx.CompletedSteps.Add($"manage_facts:record:{fact.FactId}");
    }

    private async Task SearchAsync(SkillExecutionContext ctx)
    {
        var keyword = ctx.GetArg("keyword");
        var category = ctx.GetArg("category");
        var limit = int.TryParse(ctx.GetArg("limit"), out var lim) ? Math.Clamp(lim, 1, 30) : 10;

        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing memory facts..."));
        var items = await _skills.SearchFactsAsync(ctx.ConversationId, keyword, category, limit, ctx.Ct);
        if (items.Count == 0)
        {
            ctx.Observations.Add("manage_facts: no matching facts.");
        }
        else
        {
            var lines = items.Select(f => $"- id={f.FactId} | category={f.Category} | {f.Summary}");
            ctx.Observations.Add($"manage_facts: {items.Count} fact(s):\n" + string.Join("\n", lines));
        }
        ctx.CompletedSteps.Add("manage_facts:search");
    }

    private async Task DetailAsync(SkillExecutionContext ctx)
    {
        var factId = ctx.GetArg("fact_id") ?? "";
        if (string.IsNullOrWhiteSpace(factId))
        {
            ctx.Observations.Add("manage_facts: missing fact_id.");
            return;
        }

        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing memory facts..."));
        var fact = await _skills.GetFactDetailAsync(factId, ctx.Ct);
        if (fact is null)
        {
            ctx.Observations.Add($"manage_facts: fact '{factId}' not found.");
            return;
        }
        ctx.Observations.Add($"manage_facts: id={fact.FactId} category={fact.Category} confidence={fact.Confidence}\nsummary={fact.Summary}\ndetail={fact.Detail}\nevidence={fact.Evidence}");
        ctx.CompletedSteps.Add($"manage_facts:detail:{factId}");
    }

    private async Task UpdateAsync(SkillExecutionContext ctx)
    {
        var factId = ctx.GetArg("fact_id") ?? "";
        if (string.IsNullOrWhiteSpace(factId))
        {
            ctx.Observations.Add("manage_facts: missing fact_id.");
            return;
        }

        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing memory facts..."));
        var ok = await _skills.UpdateFactAsync(
            factId,
            ctx.GetArg("summary"),
            ctx.GetArg("detail"),
            ctx.GetArg("category"),
            ctx.GetArg("evidence"),
            ctx.ConversationId,
            ctx.Ct);
        ctx.Observations.Add(ok
            ? $"manage_facts: updated id={factId}."
            : $"manage_facts: fact '{factId}' not found.");
        if (ok) ctx.CompletedSteps.Add($"manage_facts:update:{factId}");
    }

    private async Task DeleteAsync(SkillExecutionContext ctx)
    {
        var factId = ctx.GetArg("fact_id") ?? "";
        if (string.IsNullOrWhiteSpace(factId))
        {
            ctx.Observations.Add("manage_facts: missing fact_id.");
            return;
        }

        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing memory facts..."));
        var ok = await _skills.DeleteFactAsync(factId, ctx.ConversationId, ctx.Ct);
        ctx.Observations.Add(ok
            ? $"manage_facts: deleted id={factId}."
            : $"manage_facts: fact '{factId}' not found.");
        if (ok) ctx.CompletedSteps.Add($"manage_facts:delete:{factId}");
    }

    private async Task AnnotateAsync(SkillExecutionContext ctx)
    {
        var targetType = ctx.GetArg("target_type") ?? "";
        var targetId = ctx.GetArg("target_id") ?? "";
        var note = ctx.GetArg("note") ?? ctx.GetArg("summary") ?? "";
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(note))
        {
            ctx.Observations.Add("manage_facts: annotate requires target_type, target_id, and note.");
            return;
        }
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing memory facts..."));
        var rec = await _skills.AnnotateMemoryAsync(ctx.ConversationId, targetType, targetId, note, ctx.GetArg("source_fact_id"), ctx.Ct);
        ctx.Observations.Add($"manage_facts: annotated {rec.TargetType}:{rec.TargetId} (annotation_id={rec.AnnotationId}): {rec.Note}");
        ctx.CompletedSteps.Add($"manage_facts:annotate:{rec.AnnotationId}");
    }

    private async Task ListAnnotationsAsync(SkillExecutionContext ctx)
    {
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing memory facts..."));
        var items = await _skills.ListAnnotationsAsync(ctx.GetArg("target_type"), ctx.GetArg("target_id"), ctx.Ct);
        if (items.Count == 0)
        {
            ctx.Observations.Add("manage_facts: no annotations found.");
        }
        else
        {
            var lines = items.Select(a => $"- id={a.AnnotationId} | {a.TargetType}:{a.TargetId} | note={a.Note}");
            ctx.Observations.Add($"manage_facts: {items.Count} annotation(s):\n" + string.Join("\n", lines));
        }
        ctx.CompletedSteps.Add("manage_facts:list_annotations");
    }

    private async Task UpdateAnnotationAsync(SkillExecutionContext ctx)
    {
        var id = ctx.GetArg("annotation_id") ?? "";
        var note = ctx.GetArg("note") ?? ctx.GetArg("summary") ?? "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(note))
        {
            ctx.Observations.Add("manage_facts: update_annotation requires annotation_id and note.");
            return;
        }
        var ok = await _skills.UpdateAnnotationAsync(id, note, ctx.Ct);
        ctx.Observations.Add(ok ? $"manage_facts: updated annotation {id}." : $"manage_facts: annotation '{id}' not found.");
        if (ok) ctx.CompletedSteps.Add($"manage_facts:update_annotation:{id}");
    }

    private async Task DeleteAnnotationAsync(SkillExecutionContext ctx)
    {
        var id = ctx.GetArg("annotation_id") ?? "";
        if (string.IsNullOrWhiteSpace(id))
        {
            ctx.Observations.Add("manage_facts: delete_annotation requires annotation_id.");
            return;
        }
        var ok = await _skills.DeleteAnnotationAsync(id, ctx.Ct);
        ctx.Observations.Add(ok ? $"manage_facts: deleted annotation {id}." : $"manage_facts: annotation '{id}' not found.");
        if (ok) ctx.CompletedSteps.Add($"manage_facts:delete_annotation:{id}");
    }
}
