using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Consolidated schedule & reminder CRUD skill (create | list | update | delete).
/// </summary>
public sealed class ManageScheduleSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public ManageScheduleSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "manage_schedule";
    public string DisplayDescription => "Create, list, update, and delete reminders & schedules";
    public string StatusText => "Updating schedules...";
    public string? Capability => "schedule";
    public string MainAgentSpec => """
            manage_schedule
               - Purpose: Create, list, update, or delete timed reminders, recurring schedules, and automated rules.
                 * One-shot reminder: set `remind_at` (ISO-8601).
                 * Recurring schedule: set `interval_minutes` (e.g. 1440 for daily, `workdays_only=true` for Mon-Fri).
                 * Rule-based mail reminder: set `rule_pattern` + `interval_minutes`.
               - Args: {"action": "create|list|update|delete", "id": "schedule id or keyword for update/delete", "title": "...", "message": "...", "remind_at": "ISO-8601", "interval_minutes": 0, "workdays_only": false, "rule_pattern": "sender/subject keyword", "skill_name": "optional background skill", "skill_args": "optional", "status": "Active|Paused"}
            
        """;
    public string PlannerSpec => """manage_schedule: Create/list/update/delete reminders & recurring schedules. args: {"action":"create|list|update|delete","id":"...","title":"...","message":"...","remind_at":"ISO-8601","interval_minutes":0,"workdays_only":false,"rule_pattern":"...","status":"Active|Paused"}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "list").Trim().ToLowerInvariant();
        switch (action)
        {
            case "create":
                await CreateAsync(ctx);
                break;
            case "list":
                await ListAsync(ctx);
                break;
            case "update":
                await UpdateAsync(ctx);
                break;
            case "delete":
                await DeleteAsync(ctx);
                break;
            default:
                ctx.MarkFailed();
                ctx.Observations.Add($"manage_schedule: unsupported action '{action}'.");
                break;
        }
    }

    private async Task CreateAsync(SkillExecutionContext ctx)
    {
        var title = ctx.GetArg("title") ?? "Reminder";
        var message = ctx.GetArg("message") ?? title;
        var remindRaw = ctx.GetArg("remind_at");
        var remindAt = DateTimeOffset.TryParse(remindRaw, out var dt)
            ? dt
            : DateTimeOffset.UtcNow.AddMinutes(5);
        var interval = int.TryParse(ctx.GetArg("interval_minutes"), out var iv) ? iv : 0;
        var workdaysOnly = string.Equals(ctx.GetArg("workdays_only"), "true", StringComparison.OrdinalIgnoreCase);
        var skillName = ctx.GetArg("skill_name");
        var skillArgs = ctx.GetArg("skill_args");
        var recurrenceRule = ctx.GetArg("recurrence_rule") ?? ctx.GetArg("rule_pattern");

        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{ctx.EffectiveName(Name)}", StatusText));
        var rec = await _skills.CreateReminderAsync(
            ctx.ConversationId,
            remindAt,
            title,
            message,
            ctx.TargetMailId,
            interval,
            skillName,
            skillArgs,
            workdaysOnly,
            linkedEventId: null,
            recurrenceRule: recurrenceRule,
            ct: ctx.Ct);

        ctx.Observations.Add($"manage_schedule: created id={rec.ReminderId} title=\"{rec.Title}\" at={rec.RemindAt.ToLocalTime():yyyy-MM-dd HH:mm} interval={rec.IntervalMinutes}m workdaysOnly={rec.WorkdaysOnly}.");
        ctx.CompletedSteps.Add($"manage_schedule:create:{rec.ReminderId}");
    }

    private async Task ListAsync(SkillExecutionContext ctx)
    {
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{ctx.EffectiveName(Name)}", StatusText));
        var items = await _skills.ListSchedulesAsync(ctx.Ct);
        if (items.Count == 0)
        {
            ctx.Observations.Add("manage_schedule: no pending schedules.");
        }
        else
        {
            var lines = items.Select(r =>
                $"- id={r.ReminderId} | title=\"{r.Title}\" | next={r.RemindAt.ToLocalTime():yyyy-MM-dd HH:mm} | interval={r.IntervalMinutes}m | skill={r.SkillName ?? "none"}");
            ctx.Observations.Add($"manage_schedule: {items.Count} schedule(s):\n" + string.Join("\n", lines));
        }
        ctx.CompletedSteps.Add("manage_schedule:list");
    }

    private async Task UpdateAsync(SkillExecutionContext ctx)
    {
        var id = ctx.GetArg("id") ?? ctx.GetArg("keyword") ?? ctx.GetArg("title") ?? "";
        var title = ctx.GetArg("title");
        var message = ctx.GetArg("message");
        var remindRaw = ctx.GetArg("remind_at");
        DateTimeOffset? remindAt = DateTimeOffset.TryParse(remindRaw, out var dt) ? dt : null;
        int? interval = int.TryParse(ctx.GetArg("interval_minutes"), out var iv) ? iv : null;
        var skillName = ctx.GetArg("skill_name");
        var skillArgs = ctx.GetArg("skill_args");

        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{ctx.EffectiveName(Name)}", StatusText));
        var updated = await _skills.UpdateScheduleAsync(id, title, message, remindAt, interval, skillName, skillArgs, ctx.Ct);
        if (updated is null)
        {
            ctx.Observations.Add($"manage_schedule: schedule '{id}' not found.");
            return;
        }
        ctx.Observations.Add($"manage_schedule: updated id={updated.ReminderId} title=\"{updated.Title}\" next={updated.RemindAt.ToLocalTime():yyyy-MM-dd HH:mm}.");
        ctx.CompletedSteps.Add($"manage_schedule:update:{updated.ReminderId}");
    }

    private async Task DeleteAsync(SkillExecutionContext ctx)
    {
        var raw = ctx.GetArg("id") ?? ctx.GetArg("ids") ?? ctx.GetArg("keyword") ?? ctx.GetArg("title") ?? "";
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{ctx.EffectiveName(Name)}", StatusText));

        var targets = raw
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count <= 1)
        {
            var id = targets.FirstOrDefault() ?? raw.Trim();
            var ok = await _skills.DeleteScheduleAsync(id, ctx.Ct);
            ctx.Observations.Add(ok
                ? $"manage_schedule: deleted '{id}'."
                : $"manage_schedule: schedule '{id}' not found.");
            if (ok) ctx.CompletedSteps.Add($"manage_schedule:delete:{id}");
            return;
        }

        var deleted = new List<string>();
        var notFound = new List<string>();
        foreach (var id in targets)
        {
            if (await _skills.DeleteScheduleAsync(id, ctx.Ct))
            {
                deleted.Add(id);
                ctx.CompletedSteps.Add($"manage_schedule:delete:{id}");
            }
            else
            {
                notFound.Add(id);
            }
        }

        var msg = $"manage_schedule: deleted {deleted.Count}/{targets.Count} schedule(s) [{string.Join(", ", deleted)}].";
        if (notFound.Count > 0)
            msg += $" Not found: [{string.Join(", ", notFound)}].";
        ctx.Observations.Add(msg);
    }
}
