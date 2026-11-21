using AiSecretary.Desktop.Agent;

namespace AiSecretary.Desktop.Skills;

public sealed class ManageCalendarSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;

    public ManageCalendarSkill(SkillRuntime skills)
    {
        _skills = skills;
    }

    public string Name => "manage_calendar";
    public string DisplayDescription => "Read, create, update, and delete calendar events";
    public string StatusText => "Accessing calendar...";
    public string MainAgentSpec => """
            manage_calendar
               - Purpose: List, create, update, delete, check conflicts, skip a date, or update meeting reminders in the calendar.
               - Args: {"action": "list|create|update|delete|check_conflict|skip_date|update_all_reminders", "event_id": "id or keyword", "title": "...", "category": "leave|meeting|focus|event", "start_at": "ISO-8601", "end_at": "ISO-8601", "all_day": false, "recurrence": "none or RRULE", "remind_minutes_before": null, "override_dates": ["YYYY-MM-DD"], "exception_date": "YYYY-MM-DD", "notes": "...", "force": false}
            
        """;
    public string PlannerSpec => """manage_calendar: List/create/update/delete/skip calendar events with conflict check. args: {"action":"list|create|update|delete|check_conflict|skip_date|update_all_reminders","event_id":"...","title":"...","category":"leave|meeting|focus|event","start_at":"ISO-8601","end_at":"ISO-8601","all_day":false,"recurrence":"none|RRULE","remind_minutes_before":null,"override_dates":[],"exception_date":"YYYY-MM-DD","notes":"...","force":false}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = (ctx.GetArg("action") ?? "list").Trim().ToLowerInvariant();
        var idOrKw = ctx.GetArg("event_id") ?? ctx.GetArg("title_keyword") ?? "";
        var title = ctx.GetArg("title") ?? "";
        var category = ctx.GetArg("category") ?? "event";
        var notes = ctx.GetArg("notes") ?? "";
        var recurrence = ctx.GetArg("recurrence") ?? "none";
        var exceptionDate = ctx.GetArg("exception_date") ?? "";
        var allDay = string.Equals(ctx.GetArg("all_day"), "true", StringComparison.OrdinalIgnoreCase);
        var force = string.Equals(ctx.GetArg("force"), "true", StringComparison.OrdinalIgnoreCase);
        DateTimeOffset? startAt = DateTimeOffset.TryParse(ctx.GetArg("start_at"), out var sAt) ? sAt : null;
        DateTimeOffset? endAt = DateTimeOffset.TryParse(ctx.GetArg("end_at"), out var eAt) ? eAt : null;
        var rmbRaw = ctx.GetArg("remind_minutes_before");
        int? remindMinutesBefore = int.TryParse(rmbRaw, out var rmb) ? Math.Max(0, rmb) : null;

        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{ctx.EffectiveName(Name)}", StatusText));
        switch (action)
        {
            case "list":
            {
                var events = await _skills.ListCalendarEventsAsync(ctx.ConversationId, startAt, endAt, ctx.Ct);
                if (events.Count == 0)
                {
                    ctx.Observations.Add("manage_calendar list: empty.");
                }
                else
                {
                    var lines = events.Select(e =>
                    {
                        var recTag = string.IsNullOrWhiteSpace(e.Recurrence) || e.Recurrence == "none" ? "" : $" | recurrence={e.Recurrence}";
                        return $"- event_id={e.EventId} | [{e.Category}] {e.Title} | {e.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {e.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}{recTag}{(string.IsNullOrWhiteSpace(e.Notes) ? "" : $" | notes={e.Notes}")}";
                    });
                    ctx.Observations.Add($"manage_calendar list ({events.Count}):\n{string.Join("\n", lines)}");
                }
                ctx.CompletedSteps.Add("manage_calendar:list");
                break;
            }
            case "check_conflict":
            {
                if (!startAt.HasValue)
                {
                    ctx.MarkFailed();
                    ctx.Observations.Add("manage_calendar check_conflict: missing start_at.");
                    break;
                }
                var effEnd = endAt ?? startAt.Value.AddHours(1);
                var conflicts = await _skills.FindCalendarConflictsAsync(ctx.ConversationId, startAt.Value, effEnd, null, ctx.Ct);
                if (conflicts.Count == 0)
                {
                    ctx.Observations.Add($"No conflict for {startAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} ~ {effEnd.ToLocalTime():yyyy-MM-dd HH:mm}.");
                }
                else
                {
                    var desc = string.Join("; ", conflicts.Select(c =>
                        $"[{c.Category}] {c.Title} ({c.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {c.EndAt.ToLocalTime():yyyy-MM-dd HH:mm})"));
                    ctx.Observations.Add($"Time conflict detected: {desc}");
                }
                ctx.CompletedSteps.Add("manage_calendar:check_conflict");
                break;
            }
            case "create":
            {
                if (string.IsNullOrWhiteSpace(title) || !startAt.HasValue)
                {
                    ctx.MarkFailed();
                    ctx.Observations.Add("manage_calendar create: requires title and start_at.");
                    break;
                }
                var effStart = allDay ? startAt.Value.Date : startAt.Value;
                var effEnd = endAt ?? (allDay ? startAt.Value.Date.AddDays(1).AddSeconds(-1) : startAt.Value.AddHours(1));
                var (created, conflicts, linkedReminder) = await _skills.CreateCalendarEventAsync(
                    ctx.ConversationId, title, category, effStart, effEnd, allDay, notes, force, recurrence, remindMinutesBefore, ctx.Ct);
                if (created is null)
                {
                    var desc = string.Join("; ", conflicts.Select(c =>
                        $"[{c.Category}] {c.Title} ({c.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {c.EndAt.ToLocalTime():yyyy-MM-dd HH:mm})"));
                    ctx.Observations.Add($"Time conflict (not created): '{title}' ({effStart.ToLocalTime():yyyy-MM-dd HH:mm} ~ {effEnd.ToLocalTime():yyyy-MM-dd HH:mm}) overlaps with: {desc}. Ask user before retrying with force=true.");
                }
                else
                {
                    var recNote = created.Recurrence != "none" ? $" (recurrence={created.Recurrence})" : "";
                    var remNote = linkedReminder is not null ? $", linked_reminder={linkedReminder.RemindAt.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
                    ctx.Observations.Add($"Created event: {created.Title}{recNote} ({created.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {created.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}, event_id={created.EventId}{remNote}).");
                    ctx.CompletedSteps.Add($"manage_calendar:create:{created.EventId}");
                }
                break;
            }
            case "update":
            {
                if (string.IsNullOrWhiteSpace(idOrKw))
                {
                    ctx.MarkFailed();
                    ctx.Observations.Add("manage_calendar update: missing event_id.");
                    break;
                }
                var overrideDates = ctx.GetStringArrayArg("override_dates");
                var (updated, conflicts) = await _skills.UpdateCalendarEventAsync(
                    ctx.ConversationId, idOrKw,
                    string.IsNullOrWhiteSpace(title) ? null : title,
                    ctx.GetArg("category"),
                    startAt, endAt,
                    ctx.GetArg("all_day") is null ? null : allDay,
                    ctx.GetArg("notes"),
                    force,
                    ctx.GetArg("recurrence"),
                    remindMinutesBefore,
                    overrideDates,
                    ctx.Ct);
                if (updated is null && conflicts.Count > 0)
                {
                    var desc = string.Join("; ", conflicts.Select(c =>
                        $"[{c.Category}] {c.Title} ({c.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {c.EndAt.ToLocalTime():yyyy-MM-dd HH:mm})"));
                    ctx.Observations.Add($"Time conflict (not updated): overlaps with {desc}. Ask user before forcing.");
                }
                else if (updated is null)
                {
                    ctx.Observations.Add($"Event not found for '{idOrKw}'.");
                }
                else
                {
                    var leadNote = remindMinutesBefore.HasValue
                        ? (overrideDates.Count > 0
                            ? $", override_dates={string.Join(",", overrideDates)} lead={remindMinutesBefore.Value}m"
                            : $", reminder_lead={remindMinutesBefore.Value}m")
                        : "";
                    ctx.Observations.Add($"Updated event: {updated.Title} ({updated.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {updated.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}{leadNote}).");
                    ctx.CompletedSteps.Add($"manage_calendar:update:{updated.EventId}");
                }
                break;
            }
            case "update_all_reminders":
            {
                if (!remindMinutesBefore.HasValue)
                {
                    ctx.MarkFailed();
                    ctx.Observations.Add("manage_calendar update_all_reminders: missing remind_minutes_before.");
                    break;
                }
                var count = await _skills.UpdateAllMeetingRemindLeadAsync(ctx.ConversationId, remindMinutesBefore.Value, ctx.Ct);
                ctx.Observations.Add($"Updated reminder lead to {remindMinutesBefore.Value}m for {count} future/recurring events.");
                ctx.CompletedSteps.Add($"manage_calendar:update_all_reminders:{remindMinutesBefore.Value}");
                break;
            }
            case "skip_date":
            {
                if (string.IsNullOrWhiteSpace(idOrKw))
                {
                    ctx.MarkFailed();
                    ctx.Observations.Add("manage_calendar skip_date: missing event_id.");
                    break;
                }
                var ymd = !string.IsNullOrWhiteSpace(exceptionDate)
                    ? exceptionDate.Trim()
                    : (startAt.HasValue ? startAt.Value.ToLocalTime().ToString("yyyy-MM-dd") : "");
                if (string.IsNullOrWhiteSpace(ymd))
                {
                    ctx.MarkFailed();
                    ctx.Observations.Add("manage_calendar skip_date: missing exception_date (YYYY-MM-DD).");
                    break;
                }
                var skipped = await _skills.SkipCalendarEventDateAsync(ctx.ConversationId, idOrKw, ymd, ctx.Ct);
                ctx.Observations.Add(skipped is null
                    ? $"Event not found for '{idOrKw}'."
                    : $"Skipped date {ymd} for '{skipped.Title}' (linked reminder also skipped).");
                if (skipped is not null) ctx.CompletedSteps.Add($"manage_calendar:skip_date:{skipped.EventId}:{ymd}");
                break;
            }
            case "delete":
            {
                if (string.IsNullOrWhiteSpace(idOrKw))
                {
                    ctx.MarkFailed();
                    ctx.Observations.Add("manage_calendar delete: missing event_id.");
                    break;
                }
                var deleted = await _skills.DeleteCalendarEventAsync(ctx.ConversationId, idOrKw, ctx.Ct);
                ctx.Observations.Add(deleted is null
                    ? $"Event not found for '{idOrKw}'."
                    : $"Deleted event: {deleted.Title} ({deleted.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {deleted.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}).");
                if (deleted is not null) ctx.CompletedSteps.Add($"manage_calendar:delete:{deleted.EventId}");
                break;
            }
            default:
                ctx.MarkFailed();
                ctx.Observations.Add($"Unsupported manage_calendar action: {action}");
                break;
        }
    }
}