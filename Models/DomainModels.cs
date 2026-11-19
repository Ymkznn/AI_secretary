using System.Text.Json.Serialization;

namespace AiSecretary.Desktop.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MailImportance
{
    Low,
    Normal,
    High
}

public sealed record MailItem(
    string Id,
    string Subject,
    string SenderName,
    string SenderEmail,
    DateTimeOffset ReceivedAt,
    MailImportance Importance,
    string Snippet,
    string Body,
    bool IsRead,
    string ThreadId);

public sealed record MailSourceRef(
    string MailId,
    string Label,
    string Sender,
    string Subject,
    DateTimeOffset ReceivedAt);

public sealed record NotificationButtonOption(
    string Type, // view_mail, draft_reply, snooze, ask_ai
    string Label,
    string? Prompt = null,
    bool IsPrimary = false
);

public sealed record NotificationActionPayload(
    string NotificationId,
    string EventId,
    string MailId,
    string Title,
    string Summary,
    string Status, // Unread, Snoozed, Handled
    List<NotificationButtonOption>? Actions = null
);

public sealed class ChatMessageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "conv-main";
    public string Role { get; set; } = "assistant"; // user, assistant, notification
    public string Content { get; set; } = "";
    public string? StatusText { get; set; }
    public bool IsStreaming { get; set; }
    public string? TaskId { get; set; }
    public List<MailSourceRef> Sources { get; set; } = new();
    public NotificationActionPayload? Notification { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum MailRuleDecision
{
    Ignore,
    NeedsAiTriage
}

public sealed record MailEventRecord(
    string EventId,
    string MailId,
    string RuleDecision,
    bool Processed,
    DateTimeOffset DetectedAt);

public sealed class AgentTaskRecord
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "conv-main";
    public string Goal { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? TriggerEventId { get; set; }
    public string? ResultSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentSessionRecord
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "conv-main";
    public string RoleName { get; set; } = "Main";
    public int EstimatedTokens { get; set; }
    public bool IsActive { get; set; } = true;
    public string HandoffJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record SessionHandoff(
    string Goal,
    List<string> CompletedSteps,
    List<string> PendingSteps,
    List<string> UserDecisions,
    List<string> SourceMailIds);

public sealed record ReminderRecord(
    string ReminderId,
    string ConversationId,
    string Title,
    string Message,
    string? RelatedMailId,
    DateTimeOffset RemindAt,
    string Status,
    int IntervalMinutes = 0,
    string? SkillName = null,
    string? SkillArgsJson = null,
    bool WorkdaysOnly = false,
    string ExceptionDatesJson = "[]",
    string? LinkedEventId = null,
    string? RecurrenceRule = null);

public sealed record CustomSkillRecord(
    string Name,
    string Description,
    string Command,
    DateTimeOffset CreatedAt);

public sealed record SkillMenuEntry(
    string Name,
    string Description,
    bool IsCustom,
    bool IsEnabled = true);

public enum PinnedItemKind
{
    Notification,
    Schedule,
    Confirmation
}

public sealed record PinnedReminderItem(
    string Id,
    string Title,
    string Summary,
    string? MailId = null,
    NotificationActionPayload? MailNotification = null,
    int ReRemindMinutes = 15,
    PinnedItemKind Kind = PinnedItemKind.Notification,
    string? LogicalTaskId = null,
    IReadOnlyList<string>? Choices = null);

public sealed class ActiveAgentRun
{
    public string TaskId { get; init; } = "";
    public string Goal { get; set; } = "";
    public string MainState { get; set; } = "Running";   // Running, Done, Stopped
    public string PlannerState { get; set; } = "Pending"; // Pending, Running, Done, Stopped
    public string WorkerState { get; set; } = "Pending";  // Pending, Running, Done, Stopped
    public string CurrentStep { get; set; } = "初始化…";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record KnowledgeFactRecord(
    string FactId,
    string ConversationId,
    string Category,
    string Summary,
    string Detail,
    string? Evidence,
    string Confidence,
    List<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FactSummaryItem(
    string FactId,
    string Category,
    string Summary,
    string Confidence,
    DateTimeOffset CreatedAt);

public sealed record CalendarEventRecord(
    string EventId,
    string ConversationId,
    string Title,
    string Category,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    bool AllDay,
    string Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Recurrence = "none",
    string ExceptionDatesJson = "[]");

public enum PathPermissionDecision
{
    Deny,
    AllowOnce,
    TrustPermanently
}

public enum CommandApprovalDecision
{
    Deny,
    AllowOnce,
    AllowForTask
}

