namespace AiSecretary.Desktop.Models;

/// <summary>
/// Persistent declarative background monitor (price check, X/Twitter notification, public JSON/HTML diff).
/// Executed deterministically by <see cref="Services.StandingWatcherService"/> without burning LLM tokens on unchanged polls.
/// </summary>
public sealed record StandingWatcherRecord(
    string WatcherId,
    string ConversationId,
    string Title,
    string Kind, // "http_text" | "http_json" | "browser_dom"
    string Url,
    string SelectorOrPath,
    string ConditionType, // "changed" | "lt" | "lte" | "gt" | "gte" | "contains"
    string? ThresholdValue,
    int IntervalSeconds,
    string? LastSnapshot,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? NextCheckAt,
    int FailureCount,
    string Status, // "Active" | "Paused" | "NeedsAuth" | "Completed"
    DateTimeOffset CreatedAt);

public sealed record BrowserFormField(
    string FieldId,
    string Name,
    string Label,
    string ControlType,
    string? CurrentValue,
    IReadOnlyList<string>? Options,
    bool IsFocused = false);

public sealed record BrowserFormContext(
    string Url,
    string Title,
    IReadOnlyList<BrowserFormField> Fields);

public sealed record BrowserFillAction(
    string FieldId,
    string Value,
    string? Label = null,
    string? Name = null);

public sealed record CachedWorkflowStep(
    string Skill,
    System.Text.Json.JsonElement Args);

public sealed record WorkflowCacheRecord(
    string WorkflowId,
    string ConversationId,
    string GoalSignature,
    string StepsJson,
    int PredictorCounter, // 0=StronglyNotTaken, 1=WeaklyNotTaken, 2=WeaklyTaken, 3=StronglyTaken
    int HitCount,
    int MissCount,
    string Status, // "Hot" | "Cold" | "Disabled"
    DateTimeOffset LastUsedAt,
    DateTimeOffset CreatedAt);

public sealed record BrowserPageSignal(
    string Url,
    string Title,
    int InputCount,
    IReadOnlyList<string>? Labels = null);

public sealed record AutoStateRuleRecord(
    string TriggerKey,
    string StateName,
    string StepsJson,
    int PredictorCounter, // 0..3 (2-bit saturating counter)
    int HitCount,
    int MissCount,
    string Status, // "Cold" | "Hot" | "Ignored" | "Muted" | "Disabled"
    DateTimeOffset LastSeenAt,
    DateTimeOffset? CooldownUntil = null,
    string? ProposalText = null);

public sealed record BaselineUsageSnapshot(
    long InputTokens,
    long OutputTokens,
    long CacheTokens,
    long Predictions,
    long Hits,
    long Mispredictions,
    long EstimatedTokensSaved,
    DateTimeOffset RecordedAt)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheTokens;
    public double HitRatePercent => Predictions == 0 ? 0.0 : Math.Round(Hits * 100.0 / Predictions, 1);
}

public sealed record BranchTelemetryStats(
    int TotalWorkflows,
    int HotWorkflows,
    int ColdWorkflows,
    long Predictions,
    long Hits,
    long Mispredictions,
    long EstimatedTokensSaved)
{
    public double HitRatePercent => Predictions == 0 ? 0.0 : Math.Round(Hits * 100.0 / Predictions, 1);
}

public sealed record MemoryAnnotationRecord(
    string AnnotationId,
    string ConversationId,
    string TargetType, // "fact" | "workflow" | "page_intent" | "schedule" | "calendar" | "watcher"
    string TargetId,
    string Note,
    string? SourceFactId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
