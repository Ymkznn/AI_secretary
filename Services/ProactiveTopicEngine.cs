using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

public enum ProactiveAwarenessLevel
{
    Low,
    Normal,
    High
}

public enum ProactiveTopicKind
{
    TimeAndLife,
    OngoingThread,
    RemindersAndSchedules,
    PersonaSmallTalk,
    CurrentActivity,
    CustomUserTopic
}

public sealed record ProactiveActivitySnapshot(
    string? ProcessName,
    string? Title,
    string? Url,
    bool IsIncognito = false);

public sealed record ProactiveTopicContext(
    DateTimeOffset Now,
    IReadOnlyList<string> RecentConversationTurns24h,
    IReadOnlyList<string> PendingConfirmations,
    IReadOnlyList<string> RecentBlackboardResults,
    IReadOnlyList<string> PinnedReminders,
    IReadOnlyList<string> UpcomingSchedules,
    ProactiveActivitySnapshot? Activity,
    IReadOnlyList<KnowledgeFactRecord> CustomTopics,
    IReadOnlyList<string> RecentSharedCustomSummaries,
    IReadOnlyList<ProactiveTopicKind> RecentTopics,
    ProactiveAwarenessLevel AwarenessLevel);

public sealed record SelectedProactiveTopic(
    ProactiveTopicKind Topic,
    string? ActivityPayload,
    KnowledgeFactRecord? SelectedCustomTopic,
    IReadOnlyList<string> RecentSharedSummaries,
    IReadOnlyList<string> OngoingThreadItems,
    IReadOnlyList<string> ReminderItems);

public sealed record ProactiveGenerationResult(
    string? Reply,
    string? TtsText,
    bool Skipped,
    ProactiveTopicKind SelectedTopic,
    string? SharedSummary,
    ProactiveAwarenessLevel AwarenessLevel,
    string AwarenessSource,
    string StyleScopeKey);

/// <summary>
/// Language-agnostic proactive idle check-in engine:
/// - Programmatically picks ONE topic with weighted randomness and soft recency decay (lowers weight after being picked instead of hard-banning).
/// - Passes window/tab title directly ONLY when CurrentActivity is picked (incognito stays redacted).
/// - Resolves effective awareness strictly per style file: verbal override for this style file > cached LLM classification by SHA-256 > default Normal.
/// </summary>
public static class ProactiveTopicEngine
{
    public const string AwarenessTag = "proactive_awareness";
    public const string CustomTopicTag = "proactive_topic";

    /// <summary>Soft cooldown decay applied when a topic was just used (last 1 turn / last 2 turns).</summary>
    public const double LastOneDecayMultiplier = 0.15;
    public const double LastTwoDecayMultiplier = 0.45;

    private static readonly Regex AwarenessLevelRegex = new(
        @"\b(low|normal|high)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ComputeStyleHash(string stylePrompt)
    {
        var normalized = (stylePrompt ?? "").Trim().Replace("\r\n", "\n");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Derives a stable per-style-file identifier (e.g. "sana_style_prompt") so verbal awareness overrides
    /// bind strictly to that specific persona file and never leak across different personas.
    /// </summary>
    public static string ResolveStyleScopeKey(string? styleFilePath, string? stylePromptFallback = null)
    {
        if (!string.IsNullOrWhiteSpace(styleFilePath))
        {
            var stem = Path.GetFileNameWithoutExtension(styleFilePath.Trim());
            if (!string.IsNullOrWhiteSpace(stem))
            {
                var clean = Regex.Replace(stem.ToLowerInvariant(), @"[^a-z0-9_\-\u4e00-\u9fff\u3040-\u30ff]+", "_").Trim('_');
                if (!string.IsNullOrWhiteSpace(clean))
                    return clean;
            }
        }

        return string.IsNullOrWhiteSpace(stylePromptFallback)
            ? "default"
            : ComputeStyleHash(stylePromptFallback)[..12];
    }

    public static string BuildAwarenessMemoryKey(string styleScopeKey)
        => $"{AwarenessTag}:{(string.IsNullOrWhiteSpace(styleScopeKey) ? "default" : styleScopeKey.Trim().ToLowerInvariant())}";

    public static string BuildStyleScopeTag(string styleScopeKey)
        => $"style:{(string.IsNullOrWhiteSpace(styleScopeKey) ? "default" : styleScopeKey.Trim().ToLowerInvariant())}";

    public static ProactiveAwarenessLevel? TryParseAwarenessLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var m = AwarenessLevelRegex.Match(text);
        if (!m.Success)
            return null;

        return m.Groups[1].Value.ToLowerInvariant() switch
        {
            "low" => ProactiveAwarenessLevel.Low,
            "normal" => ProactiveAwarenessLevel.Normal,
            "high" => ProactiveAwarenessLevel.High,
            _ => null
        };
    }

    public static string ToCanonicalString(ProactiveAwarenessLevel level) => level switch
    {
        ProactiveAwarenessLevel.Low => "low",
        ProactiveAwarenessLevel.High => "high",
        _ => "normal"
    };

    public static string ToCanonicalTopicKey(ProactiveTopicKind topic) => topic switch
    {
        ProactiveTopicKind.TimeAndLife => "time_and_life",
        ProactiveTopicKind.OngoingThread => "ongoing_thread",
        ProactiveTopicKind.RemindersAndSchedules => "reminders_and_schedules",
        ProactiveTopicKind.PersonaSmallTalk => "persona_small_talk",
        ProactiveTopicKind.CurrentActivity => "current_activity",
        ProactiveTopicKind.CustomUserTopic => "custom_user_topic",
        _ => "time_and_life"
    };

    /// <summary>
    /// Strict per-style-file precedence:
    /// Verbal override bound to <paramref name="styleScopeKey"/> > cached LLM classification > default Normal.
    /// </summary>
    public static (ProactiveAwarenessLevel Level, string Source) ResolveEffectiveAwareness(
        IReadOnlyList<KnowledgeFactRecord> confirmedFacts,
        string styleScopeKey,
        ProactiveAwarenessLevel? llmClassifiedLevel)
    {
        var expectedMemKey = BuildAwarenessMemoryKey(styleScopeKey);
        var expectedStyleTag = BuildStyleScopeTag(styleScopeKey);

        foreach (var fact in confirmedFacts)
        {
            if (!string.Equals(fact.State, "Confirmed", StringComparison.OrdinalIgnoreCase))
                continue;

            bool matchesKey = string.Equals(fact.MemoryKey, expectedMemKey, StringComparison.OrdinalIgnoreCase);
            bool matchesTaggedPair =
                fact.Tags.Any(t => string.Equals(t, AwarenessTag, StringComparison.OrdinalIgnoreCase)) &&
                fact.Tags.Any(t => string.Equals(t, expectedStyleTag, StringComparison.OrdinalIgnoreCase));

            if (!matchesKey && !matchesTaggedPair)
                continue;

            var parsed = TryParseAwarenessLevel(fact.Detail) ?? TryParseAwarenessLevel(fact.Summary);
            if (parsed.HasValue)
                return (parsed.Value, "override");
        }

        if (llmClassifiedLevel.HasValue)
            return (llmClassifiedLevel.Value, "llm");

        return (ProactiveAwarenessLevel.Normal, "default");
    }

    public static List<string> FilterUserUpcomingSchedules(
        IReadOnlyList<ReminderRecord> pendingReminders,
        DateTimeOffset horizon)
    {
        return pendingReminders
            .Where(r => string.IsNullOrWhiteSpace(r.SkillName))
            .Where(r => r.RemindAt <= horizon)
            .Take(4)
            .Select(r => $"{r.RemindAt.ToLocalTime():MM/dd HH:mm} {r.Title}")
            .ToList();
    }

    public static List<KnowledgeFactRecord> ExtractConfirmedCustomTopics(IReadOnlyList<KnowledgeFactRecord> facts)
    {
        return facts
            .Where(f => string.Equals(f.State, "Confirmed", StringComparison.OrdinalIgnoreCase))
            .Where(f =>
                string.Equals(f.Category, CustomTopicTag, StringComparison.OrdinalIgnoreCase) ||
                f.Tags.Any(t => string.Equals(t, CustomTopicTag, StringComparison.OrdinalIgnoreCase)) ||
                f.MemoryKey.StartsWith(CustomTopicTag + ":", StringComparison.OrdinalIgnoreCase))
            .Where(f => !string.IsNullOrWhiteSpace(f.Summary) || !string.IsNullOrWhiteSpace(f.Detail))
            .ToList();
    }

    /// <summary>
    /// Formats current window/tab title directly when CurrentActivity is chosen (incognito stays redacted).
    /// </summary>
    public static string? FormatActivityForPrompt(ProactiveActivitySnapshot? snap, ProactiveAwarenessLevel _ = ProactiveAwarenessLevel.Normal)
    {
        if (snap is null)
            return null;

        var proc = (snap.ProcessName ?? "").Trim();
        if (snap.IsIncognito)
            return string.IsNullOrWhiteSpace(proc) ? "(private browsing)" : $"{proc} (private browsing)";

        var rawTitle = (snap.Title ?? "").Trim();
        if (rawTitle.Length > 140)
            rawTitle = rawTitle[..140] + "…";

        if (string.IsNullOrWhiteSpace(rawTitle))
            return string.IsNullOrWhiteSpace(proc) ? null : proc;

        return string.IsNullOrWhiteSpace(proc) ? rawTitle : $"{proc}: {rawTitle}";
    }

    public static double GetActivityTargetWeight(ProactiveAwarenessLevel level) => level switch
    {
        ProactiveAwarenessLevel.Low => 0.10,
        ProactiveAwarenessLevel.High => 0.60,
        _ => 0.25
    };

    /// <summary>
    /// Computes normalized selection probabilities for all eligible topics, applying soft recency decay
    /// (never hard-banning recently picked topics).
    /// </summary>
    public static Dictionary<ProactiveTopicKind, double> ComputeTopicWeights(ProactiveTopicContext ctx)
    {
        var ongoingItems = BuildOngoingThreadItems(ctx);
        var reminderItems = BuildReminderItems(ctx);
        var customTopics = ExtractConfirmedCustomTopics(ctx.CustomTopics);
        var activityPayload = FormatActivityForPrompt(ctx.Activity, ctx.AwarenessLevel);

        var eligible = new List<(ProactiveTopicKind Topic, double BaseWeight)>
        {
            (ProactiveTopicKind.TimeAndLife, 25.0),
            (ProactiveTopicKind.PersonaSmallTalk, 25.0)
        };

        if (ongoingItems.Count > 0)
            eligible.Add((ProactiveTopicKind.OngoingThread, 25.0));
        if (reminderItems.Count > 0)
            eligible.Add((ProactiveTopicKind.RemindersAndSchedules, 20.0));
        if (customTopics.Count > 0)
            eligible.Add((ProactiveTopicKind.CustomUserTopic, 25.0));
        if (!string.IsNullOrWhiteSpace(activityPayload))
            eligible.Add((ProactiveTopicKind.CurrentActivity, 0.0)); // scaled dynamically by AwarenessLevel

        var hasActivity = eligible.Any(x => x.Topic == ProactiveTopicKind.CurrentActivity);
        var others = eligible.Where(x => x.Topic != ProactiveTopicKind.CurrentActivity).ToList();
        var rawWeights = new Dictionary<ProactiveTopicKind, double>(eligible.Count);

        if (hasActivity && others.Count > 0)
        {
            double actShare = GetActivityTargetWeight(ctx.AwarenessLevel);
            double otherShare = 1.0 - actShare;
            double otherBaseSum = others.Sum(o => o.BaseWeight);

            rawWeights[ProactiveTopicKind.CurrentActivity] = actShare;
            foreach (var o in others)
                rawWeights[o.Topic] = otherBaseSum > 0 ? otherShare * (o.BaseWeight / otherBaseSum) : otherShare / others.Count;
        }
        else if (hasActivity)
        {
            rawWeights[ProactiveTopicKind.CurrentActivity] = 1.0;
        }
        else
        {
            double baseSum = others.Sum(o => o.BaseWeight);
            foreach (var o in others)
                rawWeights[o.Topic] = baseSum > 0 ? o.BaseWeight / baseSum : 1.0 / others.Count;
        }

        // Soft recency decay: reduce probability after being picked instead of hard-banning.
        ProactiveTopicKind? last1 = ctx.RecentTopics.Count >= 1 ? ctx.RecentTopics[^1] : null;
        ProactiveTopicKind? last2 = ctx.RecentTopics.Count >= 2 ? ctx.RecentTopics[^2] : null;

        var decayed = new Dictionary<ProactiveTopicKind, double>(rawWeights.Count);
        double total = 0.0;
        foreach (var (topic, w) in rawWeights)
        {
            double mult = 1.0;
            if (last1 == topic)
                mult = LastOneDecayMultiplier;
            else if (last2 == topic)
                mult = LastTwoDecayMultiplier;

            double adjusted = w * mult;
            decayed[topic] = adjusted;
            total += adjusted;
        }

        if (total > 0)
        {
            foreach (var key in decayed.Keys.ToList())
                decayed[key] /= total;
        }
        return decayed;
    }

    public static SelectedProactiveTopic PickTopic(ProactiveTopicContext ctx, Random? rng = null)
    {
        rng ??= Random.Shared;

        var ongoingItems = BuildOngoingThreadItems(ctx);
        var reminderItems = BuildReminderItems(ctx);
        var customTopics = ExtractConfirmedCustomTopics(ctx.CustomTopics);
        var activityPayload = FormatActivityForPrompt(ctx.Activity, ctx.AwarenessLevel);

        var weights = ComputeTopicWeights(ctx);

        double roll = rng.NextDouble();
        double acc = 0.0;
        var chosen = weights.Keys.Last();
        foreach (var (topic, weight) in weights)
        {
            acc += weight;
            if (roll <= acc)
            {
                chosen = topic;
                break;
            }
        }

        KnowledgeFactRecord? chosenCustom = null;
        if (chosen == ProactiveTopicKind.CustomUserTopic && customTopics.Count > 0)
            chosenCustom = customTopics[rng.Next(customTopics.Count)];

        return new SelectedProactiveTopic(
            Topic: chosen,
            ActivityPayload: chosen == ProactiveTopicKind.CurrentActivity ? activityPayload : null,
            SelectedCustomTopic: chosenCustom,
            RecentSharedSummaries: chosen == ProactiveTopicKind.CustomUserTopic
                ? ctx.RecentSharedCustomSummaries.TakeLast(5).ToList()
                : Array.Empty<string>(),
            OngoingThreadItems: chosen == ProactiveTopicKind.OngoingThread
                ? ongoingItems
                : Array.Empty<string>(),
            ReminderItems: chosen == ProactiveTopicKind.RemindersAndSchedules
                ? reminderItems
                : Array.Empty<string>());
    }

    private static List<string> BuildOngoingThreadItems(ProactiveTopicContext ctx)
    {
        var items = new List<string>();
        foreach (var turn in ctx.RecentConversationTurns24h.Where(s => !string.IsNullOrWhiteSpace(s)).Take(6))
            items.Add(Compact(turn, 200));
        foreach (var c in ctx.PendingConfirmations.Where(s => !string.IsNullOrWhiteSpace(s)).Take(3))
            items.Add($"[awaiting_confirmation] {Compact(c, 160)}");
        foreach (var b in ctx.RecentBlackboardResults.Where(s => !string.IsNullOrWhiteSpace(s)).Take(3))
            items.Add($"[recent_task_result] {Compact(b, 180)}");
        return items;
    }

    private static List<string> BuildReminderItems(ProactiveTopicContext ctx)
    {
        var items = new List<string>();
        foreach (var p in ctx.PinnedReminders.Where(s => !string.IsNullOrWhiteSpace(s)).Take(4))
            items.Add($"[pinned] {Compact(p, 140)}");
        foreach (var s in ctx.UpcomingSchedules.Where(s => !string.IsNullOrWhiteSpace(s)).Take(4))
            items.Add($"[schedule] {Compact(s, 140)}");
        return items;
    }

    private static string Compact(string text, int maxLen)
    {
        var single = Regex.Replace(text.Trim(), @"\s+", " ");
        return single.Length <= maxLen ? single : single[..maxLen] + "…";
    }
}
