using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Atomic skill that publishes a desktop notification card and pinned reminder.
/// Used by both user-initiated workflows and event-driven workflows (e.g., OTP mail arrival).
/// </summary>
public sealed class PushNotificationSkill : IBasicSkill
{
    private readonly SkillRuntime _skills;
    private readonly ActivityMonitorService _activity;

    public PushNotificationSkill(SkillRuntime skills, ActivityMonitorService activity)
    {
        _skills = skills;
        _activity = activity;
    }

    public string Name => "push_notification";
    public string DisplayDescription => "Publish desktop notification cards and pinned reminders";
    public string StatusText => "Publishing notification...";
    public string MainAgentSpec => """
            push_notification
               - Purpose: Publish a desktop notification card and pinned reminder (use whenever user asks to pin a note/reminder/notification).
               - Args: {"title": "notification title", "summary": "notification body", "re_remind_minutes": 0} (default 0 = pin quietly without repeating; set >0 only if user explicitly asks to repeat every N minutes)
            
        """;
    public string PlannerSpec => """push_notification: Publish desktop notification card & pinned reminder. args: {"title":"...","summary":"...","re_remind_minutes":0} (0 = pin quietly without repeating)""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        if (_skills.PublishNotificationHandler is null)
        {
            ctx.MarkFailed();
            ctx.Observations.Add("push_notification: notification handler is not wired.");
            return;
        }

        var ev = _skills.CurrentEventNotification;
        var convId = ev?.ConvId ?? ctx.ConversationId;
        var eventId = ev?.EventId ?? $"ev-{Guid.NewGuid():N}"[..12];
        var mail = ev?.Mail;

        var title = ctx.GetArg("title");
        if (string.IsNullOrWhiteSpace(title) || title.Contains("{{", StringComparison.Ordinal))
            title = ev?.Title ?? "Notification";

        var summary = ctx.GetArg("summary");
        if (string.IsNullOrWhiteSpace(summary) || summary.Contains("{{", StringComparison.Ordinal))
            summary = ev?.Summary ?? ctx.UserPrompt;

        var reRemind = int.TryParse(ctx.GetArg("re_remind_minutes"), out var mins)
            ? Math.Clamp(mins, 0, 1440)
            : (ev?.ReRemindMinutes ?? 0);

        var actions = ev?.Actions is not null
            ? new List<NotificationButtonOption>(ev.Actions)
            : new List<NotificationButtonOption>();

        await _skills.PublishNotificationHandler(convId, eventId, mail, title, summary, reRemind, actions, ctx.Ct);
        if (ev is not null)
            ev.Published = true;

        ctx.Observations.Add($"push_notification: published \"{title}\" (re_remind_minutes={reRemind}m).");
        ctx.CompletedSteps.Add("push_notification");
    }
}
