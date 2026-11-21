using System.Text;
using System.Text.RegularExpressions;

namespace AiSecretary.Desktop.Services;

public sealed record JoinedFollowUp(
    string MessageId,
    string UserPrompt,
    DateTimeOffset JoinedAt);

public sealed record InFlightTaskSnapshot(
    string TaskId,
    string Kind,
    string Goal,
    DateTimeOffset StartedAt,
    string Status,
    string? PartialSummary,
    string? OriginUserMessageId,
    IReadOnlyList<JoinedFollowUp> JoinedFollowUps);

public sealed record TaskResult(
    string TaskId,
    string Kind,
    string Goal,
    string Summary,
    bool Succeeded,
    DateTimeOffset CompletedAt,
    long Version);

public sealed record BlackboardSnapshot(
    long Version,
    IReadOnlyList<InFlightTaskSnapshot> InFlight,
    IReadOnlyList<TaskResult> RecentResults);

/// <summary>
/// Thread-safe, in-memory shared real-time blackboard for coordinating MainAgent, PlannerAgent,
/// WorkerAgent, MailSyncService, StandingWatcherService, scheduled skills, and StateMachineAgent.
/// Never persisted to SQLite; every lifecycle mutation bumps a monotonic <see cref="Version"/>.
/// Background producers MUST commit results here immediately upon finishing work, BEFORE acquiring
/// any UI presentation gate or running LLM synthesis.
/// </summary>
public sealed class AgentBlackboard
{
    private const int MaxStoredResults = 24;
    private static readonly TimeSpan ResultRetention = TimeSpan.FromMinutes(15);

    private sealed class MutableInFlightTask
    {
        public required string TaskId { get; init; }
        public required string Kind { get; set; }
        public required string Goal { get; set; }
        public required DateTimeOffset StartedAt { get; init; }
        public string Status { get; set; } = "running";
        public string? PartialSummary { get; set; }
        public string? OriginUserMessageId { get; set; }
        public List<JoinedFollowUp> JoinedFollowUps { get; } = new();

        public InFlightTaskSnapshot ToSnapshot() => new(
            TaskId,
            Kind,
            Goal,
            StartedAt,
            Status,
            PartialSummary,
            OriginUserMessageId,
            JoinedFollowUps.ToList());
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, MutableInFlightTask> _inFlight = new(StringComparer.Ordinal);
    private readonly List<TaskResult> _recentResults = new();
    private readonly Dictionary<string, string> _dedupeKeyToTaskId = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    /// <summary>
    /// Raised when a non-interactive background task (mail_sync, watcher, scheduled_skill, state_machine)
    /// completes while one or more user follow-up questions were joined to it via <c>join_task</c>.
    /// </summary>
    public event Action<TaskResult, IReadOnlyList<JoinedFollowUp>>? JoinedTaskCompleted;

    public long Version => Volatile.Read(ref _version);

    public long StartTask(
        string taskId,
        string kind,
        string goal,
        string? originUserMessageId = null,
        string status = "running")
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Version;

        var compactGoal = CompactText(goal, 120);
        lock (_lock)
        {
            if (_inFlight.TryGetValue(taskId, out var existing))
            {
                existing.Kind = kind;
                existing.Goal = compactGoal;
                existing.Status = status;
                if (!string.IsNullOrWhiteSpace(originUserMessageId))
                    existing.OriginUserMessageId = originUserMessageId;
            }
            else
            {
                _inFlight[taskId] = new MutableInFlightTask
                {
                    TaskId = taskId,
                    Kind = kind,
                    Goal = compactGoal,
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = status,
                    OriginUserMessageId = originUserMessageId
                };
            }
            return Interlocked.Increment(ref _version);
        }
    }

    public void UpdateProgress(string taskId, string status, string? partialSummary = null)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        lock (_lock)
        {
            if (_inFlight.TryGetValue(taskId, out var task))
            {
                if (!string.IsNullOrWhiteSpace(status))
                    task.Status = CompactText(status, 80);
                if (!string.IsNullOrWhiteSpace(partialSummary))
                    task.PartialSummary = CompactText(partialSummary, 200);
            }
        }
    }

    /// <summary>
    /// Atomically migrates an in-flight task (including any already-joined user follow-ups and origin message ID)
    /// when e.g. a single-skill background task escalates to Planner under a new logical task ID.
    /// </summary>
    public long UpgradeTask(string oldTaskId, string newTaskId, string newKind, string newGoal)
    {
        if (string.IsNullOrWhiteSpace(newTaskId)) return Version;
        var compactGoal = CompactText(newGoal, 120);

        lock (_lock)
        {
            MutableInFlightTask? prev = null;
            if (!string.IsNullOrWhiteSpace(oldTaskId) && !string.Equals(oldTaskId, newTaskId, StringComparison.Ordinal))
            {
                _inFlight.Remove(oldTaskId, out prev);
            }

            if (_inFlight.TryGetValue(newTaskId, out var target))
            {
                target.Kind = newKind;
                target.Goal = compactGoal;
                target.Status = "running";
                if (prev is not null)
                {
                    target.OriginUserMessageId ??= prev.OriginUserMessageId;
                    foreach (var fu in prev.JoinedFollowUps)
                    {
                        if (!target.JoinedFollowUps.Any(x => x.MessageId == fu.MessageId))
                            target.JoinedFollowUps.Add(fu);
                    }
                }
            }
            else
            {
                var created = new MutableInFlightTask
                {
                    TaskId = newTaskId,
                    Kind = newKind,
                    Goal = compactGoal,
                    StartedAt = prev?.StartedAt ?? DateTimeOffset.UtcNow,
                    Status = "running",
                    OriginUserMessageId = prev?.OriginUserMessageId
                };
                if (prev is not null)
                    created.JoinedFollowUps.AddRange(prev.JoinedFollowUps);
                _inFlight[newTaskId] = created;
            }
            return Interlocked.Increment(ref _version);
        }
    }

    /// <summary>
    /// Atomically attaches a user follow-up question to an in-flight task.
    /// Returns <c>true</c> if the task is still running and the follow-up was attached.
    /// Returns <c>false</c> if the task is no longer in-flight; if it already completed,
    /// <paramref name="alreadyCompleted"/> contains its committed <see cref="TaskResult"/>.
    /// </summary>
    public bool TryJoinTask(
        string taskId,
        string followUpMessageId,
        string followUpPrompt,
        out TaskResult? alreadyCompleted)
    {
        alreadyCompleted = null;
        if (string.IsNullOrWhiteSpace(taskId))
            return false;

        lock (_lock)
        {
            if (_inFlight.TryGetValue(taskId, out var task))
            {
                if (!task.JoinedFollowUps.Any(x => string.Equals(x.MessageId, followUpMessageId, StringComparison.Ordinal)))
                {
                    task.JoinedFollowUps.Add(new JoinedFollowUp(
                        followUpMessageId,
                        CompactText(followUpPrompt, 300),
                        DateTimeOffset.UtcNow));
                    Interlocked.Increment(ref _version);
                }
                return true;
            }

            alreadyCompleted = _recentResults.LastOrDefault(r => string.Equals(r.TaskId, taskId, StringComparison.Ordinal));
            return false;
        }
    }

    /// <summary>
    /// Commits a task's result immediately to the blackboard, removes it from in-flight, bumps <see cref="Version"/>,
    /// and atomically returns any user follow-ups that joined while the task was running.
    /// </summary>
    public (long Version, IReadOnlyList<JoinedFollowUp> JoinedFollowUps, string? OriginUserMessageId) CompleteTask(
        string taskId,
        string kind,
        string goal,
        string summary,
        bool succeeded,
        bool suppressJoinedEvent = false,
        bool recordInRecent = true,
        string? dedupeKey = null)
    {
        TaskResult? committedResult = null;
        List<JoinedFollowUp> joined;
        string? originUserMsgId = null;
        long ver;

        lock (_lock)
        {
            if (_inFlight.Remove(taskId, out var existing))
            {
                joined = existing.JoinedFollowUps.ToList();
                originUserMsgId = existing.OriginUserMessageId;
                if (string.IsNullOrWhiteSpace(goal)) goal = existing.Goal;
                if (string.IsNullOrWhiteSpace(kind)) kind = existing.Kind;
            }
            else
            {
                joined = new List<JoinedFollowUp>();
            }

            ver = Interlocked.Increment(ref _version);

            bool shouldStore = recordInRecent || joined.Count > 0;
            if (shouldStore)
            {
                var now = DateTimeOffset.UtcNow;
                PruneOldResultsLocked(now);

                if (!string.IsNullOrWhiteSpace(dedupeKey) && _dedupeKeyToTaskId.TryGetValue(dedupeKey, out var oldId))
                {
                    _recentResults.RemoveAll(r => string.Equals(r.TaskId, oldId, StringComparison.Ordinal));
                }
                _recentResults.RemoveAll(r => string.Equals(r.TaskId, taskId, StringComparison.Ordinal));

                committedResult = new TaskResult(
                    TaskId: taskId,
                    Kind: kind,
                    Goal: CompactText(goal, 120),
                    Summary: CompactText(summary, 600),
                    Succeeded: succeeded,
                    CompletedAt: now,
                    Version: ver);
                _recentResults.Add(committedResult);

                if (!string.IsNullOrWhiteSpace(dedupeKey))
                    _dedupeKeyToTaskId[dedupeKey] = taskId;

                while (_recentResults.Count > MaxStoredResults)
                    _recentResults.RemoveAt(0);
            }
        }

        if (!suppressJoinedEvent && committedResult is not null && joined.Count > 0)
        {
            JoinedTaskCompleted?.Invoke(committedResult, joined);
        }

        return (ver, joined, originUserMsgId);
    }

    /// <summary>
    /// Updates the summary text of an already-committed result (e.g. replacing raw worker observations
    /// with MainAgent's clean user-facing summary) without bumping Version or re-triggering freshness checks.
    /// </summary>
    public void RefineResultSummary(string taskId, string refinedSummary)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(refinedSummary))
            return;

        lock (_lock)
        {
            var idx = _recentResults.FindLastIndex(r => string.Equals(r.TaskId, taskId, StringComparison.Ordinal));
            if (idx >= 0)
            {
                _recentResults[idx] = _recentResults[idx] with { Summary = CompactText(refinedSummary, 600) };
            }
        }
    }

    /// <summary>
    /// Removes a task from in-flight if it has not yet completed (e.g. cancelled, errored, or early exit).
    /// </summary>
    public void EnsureClosed(string taskId, string? failureReason = null, bool recordFailure = false)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        TaskResult? failureResult = null;
        List<JoinedFollowUp>? joined = null;

        lock (_lock)
        {
            if (!_inFlight.Remove(taskId, out var existing))
                return;

            var ver = Interlocked.Increment(ref _version);
            joined = existing.JoinedFollowUps.ToList();

            if (recordFailure || joined.Count > 0)
            {
                var now = DateTimeOffset.UtcNow;
                PruneOldResultsLocked(now);
                var msg = string.IsNullOrWhiteSpace(failureReason) ? "Task stopped before completion." : failureReason;
                failureResult = new TaskResult(
                    TaskId: existing.TaskId,
                    Kind: existing.Kind,
                    Goal: existing.Goal,
                    Summary: CompactText(msg, 300),
                    Succeeded: false,
                    CompletedAt: now,
                    Version: ver);
                _recentResults.Add(failureResult);
                while (_recentResults.Count > MaxStoredResults)
                    _recentResults.RemoveAt(0);
            }
        }

        if (failureResult is not null && joined is { Count: > 0 } &&
            failureResult.Kind is not ("planner" or "single_skill"))
        {
            JoinedTaskCompleted?.Invoke(failureResult, joined);
        }
    }

    public void ClearInFlight(string reason = "cleared")
    {
        lock (_lock)
        {
            if (_inFlight.Count == 0) return;
            _inFlight.Clear();
            Interlocked.Increment(ref _version);
        }
    }

    public BlackboardSnapshot GetSnapshot(int maxRecentResults = 6, TimeSpan? maxAge = null)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - (maxAge ?? TimeSpan.FromMinutes(10));

        lock (_lock)
        {
            PruneOldResultsLocked(now);
            var inFlight = _inFlight.Values
                .OrderBy(t => t.StartedAt)
                .Select(t => t.ToSnapshot())
                .ToList();
            var recent = _recentResults
                .Where(r => r.CompletedAt >= cutoff)
                .TakeLast(maxRecentResults)
                .ToList();
            return new BlackboardSnapshot(Volatile.Read(ref _version), inFlight, recent);
        }
    }

    public List<TaskResult> GetCompletedSince(
        long sinceVersion,
        IReadOnlySet<string>? onlyTaskIds = null,
        string? excludeTaskId = null)
    {
        lock (_lock)
        {
            return _recentResults
                .Where(r => r.Version > sinceVersion)
                .Where(r => onlyTaskIds is null || onlyTaskIds.Contains(r.TaskId))
                .Where(r => string.IsNullOrWhiteSpace(excludeTaskId) || !string.Equals(r.TaskId, excludeTaskId, StringComparison.Ordinal))
                .ToList();
        }
    }

    /// <summary>
    /// Formats <c>&lt;in_flight&gt;</c> and <c>&lt;recent_results&gt;</c> for placement at the END of dynamic user prompts
    /// (never inside static system prompts, preserving prompt cache prefixes).
    /// Returns empty string when both sections are empty.
    /// </summary>
    public static string FormatForPrompt(BlackboardSnapshot snapshot)
    {
        if (snapshot.InFlight.Count == 0 && snapshot.RecentResults.Count == 0)
            return "";

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();

        if (snapshot.InFlight.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<in_flight>");
            foreach (var t in snapshot.InFlight)
            {
                var elapsedSec = Math.Max(0, (int)(now - t.StartedAt).TotalSeconds);
                var partial = string.IsNullOrWhiteSpace(t.PartialSummary) ? "" : $" | progress={t.PartialSummary}";
                var joinedTag = t.JoinedFollowUps.Count > 0 ? $" | joined_followups={t.JoinedFollowUps.Count}" : "";
                sb.AppendLine($"- task_id={t.TaskId} | kind={t.Kind} | elapsed={elapsedSec}s | status={t.Status} | goal={t.Goal}{partial}{joinedTag}");
            }
            sb.AppendLine("</in_flight>");
        }

        if (snapshot.RecentResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<recent_results>");
            foreach (var r in snapshot.RecentResults)
            {
                var ageSec = Math.Max(0, (int)(now - r.CompletedAt).TotalSeconds);
                var ageStr = ageSec < 60 ? $"{ageSec}s" : $"{ageSec / 60}m{ageSec % 60}s";
                sb.AppendLine($"- task_id={r.TaskId} | kind={r.Kind} | age={ageStr} | ok={r.Succeeded.ToString().ToLowerInvariant()} | goal={r.Goal} | summary={r.Summary}");
            }
            sb.AppendLine("</recent_results>");
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildCompactSummary(
        IReadOnlyList<string> completedSteps,
        IReadOnlyList<string> observations,
        int maxChars = 550)
    {
        var sb = new StringBuilder();
        if (completedSteps.Count > 0)
            sb.Append("steps=[").Append(string.Join(", ", completedSteps.Take(5))).Append("] ");
        if (observations.Count > 0)
            sb.Append(string.Join(" | ", observations.Take(4)));
        else if (completedSteps.Count == 0)
            sb.Append("(no observations)");
        return CompactText(sb.ToString(), maxChars);
    }

    public static string CompactText(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = Regex.Replace(text.Trim(), @"\s+", " ");
        return flat.Length <= maxChars ? flat : flat[..maxChars] + "…";
    }

    private void PruneOldResultsLocked(DateTimeOffset now)
    {
        var cutoff = now - ResultRetention;
        _recentResults.RemoveAll(r => r.CompletedAt < cutoff);
    }
}
