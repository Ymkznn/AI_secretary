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
    IReadOnlyList<string> RecentUserMessages24h,
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
    string AwarenessSource);

/// <summary>
/// Language-agnostic proactive idle check-in engine:
/// - Programmatically picks ONE topic with weighted randomness and last-2 exclusion.
/// - Maps active process/URL into coarse neutral activity categories for Low/Normal awareness (never leaking raw titles/filenames).
/// - Resolves effective awareness strictly by precedence: verbal override > cached LLM classification > default Normal.
/// - Computes exponential backoff intervals on consecutive unanswered check-ins.
/// </summary>
public static class ProactiveTopicEngine
{
    public const string AwarenessTag = "proactive_awareness";
    public const string CustomTopicTag = "proactive_topic";

    private static readonly Regex AwarenessLevelRegex = new(
        @"\b(low|normal|high)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    private static readonly HashSet<string> CodingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "code - insiders", "devenv", "rider64", "idea64", "pycharm64", "webstorm64",
        "clion64", "goland64", "cursor", "windsurf", "antigravity", "sublime_text", "notepad++",
        "windowsterminal", "wt", "powershell", "pwsh", "cmd", "mintty", "wezterm-gui", "alacritty"
    };

    private static readonly HashSet<string> RhythmGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "osu!", "osu", "kalpa", "djmax", "djmax respect v", "muse_dash", "musedash",
        "arcaea", "vividstasis", "etterna", "stepmania", "quaver", "malody", "adofai",
        "a dance of fire and ice", "project diva", "ez2on"
    };

    private static readonly HashSet<string> GamingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "epicgameslauncher", "playnite.desktopapp", "genshinimpact", "starrail",
        "zenlesszonezero", "valorant", "valorant-win64-shipping", "leagueclient", "league of legends",
        "minecraft", "robloxplayerbeta", "bluearchive", "nikke", "umamusume", "wuthering waves"
    };

    private static readonly HashSet<string> VideoPlayerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "mpv", "potplayermini64", "potplayer", "mpc-hc64", "mpc-be64", "wmplayer"
    };

    private static readonly HashSet<string> MusicPlayerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "itunes", "foobar2000", "musicbee", "applemusic"
    };

    private static readonly HashSet<string> ChatProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "line", "slack", "teams", "ms-teams", "telegram", "whatsapp", "messenger"
    };

    private static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword", "excel", "powerpnt", "onenote", "outlook", "olk", "notion", "obsidian",
        "acrobat", "acrord32", "foxitpdfreader", "wps"
    };

    private static readonly string[] VideoHosts =
    {
        "youtube.com", "youtu.be", "twitch.tv", "bilibili.com", "nicovideo.jp",
        "ani.gamer.com.tw", "netflix.com", "disneyplus.com", "primevideo.com",
        "crunchyroll.com", "abema.tv", "holodex.net"
    };

    private static readonly string[] MusicHosts =
    {
        "music.youtube.com", "open.spotify.com", "soundcloud.com", "music.apple.com"
    };

    private static readonly string[] CodingHosts =
    {
        "github.com", "gitlab.com", "stackoverflow.com", "stackexchange.com",
        "learn.microsoft.com", "developer.mozilla.org", "docs.python.org",
        "hackthebox.com", "app.hackthebox.com", "tryhackme.com", "portswigger.net", "leetcode.com"
    };

    private static readonly string[] ChatHosts =
    {
        "discord.com", "web.whatsapp.com", "web.telegram.org", "mail.google.com", "outlook.live.com"
    };

    public static string ComputeStyleHash(string stylePrompt)
    {
        var normalized = (stylePrompt ?? "").Trim().Replace("\r\n", "\n");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

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
    /// Strict precedence: Verbal override (Confirmed user_pref tagged proactive_awareness) > cached LLM classification > default Normal.
    /// </summary>
    public static (ProactiveAwarenessLevel Level, string Source) ResolveEffectiveAwareness(
        IReadOnlyList<KnowledgeFactRecord> confirmedFacts,
        ProactiveAwarenessLevel? llmClassifiedLevel)
    {
        foreach (var fact in confirmedFacts)
        {
            if (!string.Equals(fact.State, "Confirmed", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isAwarenessOverride =
                fact.Tags.Any(t => string.Equals(t, AwarenessTag, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(fact.MemoryKey, AwarenessTag, StringComparison.OrdinalIgnoreCase) ||
                fact.MemoryKey.StartsWith(AwarenessTag + ":", StringComparison.OrdinalIgnoreCase);

            if (!isAwarenessOverride)
                continue;

            var parsed = TryParseAwarenessLevel(fact.Detail) ?? TryParseAwarenessLevel(fact.Summary);
            if (parsed.HasValue)
                return (parsed.Value, "override");
        }

        if (llmClassifiedLevel.HasValue)
            return (llmClassifiedLevel.Value, "llm");

        return (ProactiveAwarenessLevel.Normal, "default");
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

    public static string? ClassifyActivityCategory(ProactiveActivitySnapshot? snap)
    {
        if (snap is null)
            return null;

        var proc = (snap.ProcessName ?? "").Trim();
        if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            proc = proc[..^4];

        if (RhythmGameProcesses.Contains(proc))
            return "rhythm_game";
        if (CodingProcesses.Contains(proc))
            return "coding";
        if (GamingProcesses.Contains(proc))
            return "gaming";
        if (VideoPlayerProcesses.Contains(proc))
            return "watching_video";
        if (MusicPlayerProcesses.Contains(proc))
            return "listening_music";
        if (ChatProcesses.Contains(proc))
            return "chatting";
        if (OfficeProcesses.Contains(proc))
            return "office_work";

        if (BrowserProcesses.Contains(proc))
        {
            if (snap.IsIncognito)
                return "browsing";

            if (!string.IsNullOrWhiteSpace(snap.Url) && Uri.TryCreate(snap.Url, UriKind.Absolute, out var uri))
            {
                var host = uri.Host.ToLowerInvariant();
                if (MusicHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
                    return "listening_music";
                if (VideoHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
                    return "watching_video";
                if (CodingHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
                    return "coding";
                if (ChatHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
                    return "chatting";
            }

            // Brand-name fallback from title when extension URL is unavailable (language-independent site names only)
            var title = snap.Title ?? "";
            if (title.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                return "listening_music";
            if (title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Twitch", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("bilibili", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("niconico", StringComparison.OrdinalIgnoreCase))
                return "watching_video";
            if (title.Contains("GitHub", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Stack Overflow", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Hack The Box", StringComparison.OrdinalIgnoreCase))
                return "coding";

            return "browsing";
        }

        return string.IsNullOrWhiteSpace(proc) ? null : "desktop_app";
    }

    /// <summary>
    /// Formats current activity for the proactive prompt.
    /// Low/Normal: coarse category ONLY (NEVER leaks filenames, song names, video titles, or URLs).
    /// High: category + full window/tab title (private browsing remains redacted).
    /// </summary>
    public static string? FormatActivityForPrompt(ProactiveActivitySnapshot? snap, ProactiveAwarenessLevel level)
    {
        var category = ClassifyActivityCategory(snap);
        if (string.IsNullOrWhiteSpace(category) || snap is null)
            return null;

        if (level != ProactiveAwarenessLevel.High)
            return $"category={category}";

        if (snap.IsIncognito)
            return "category=browsing (private browsing)";

        var rawTitle = (snap.Title ?? "").Trim();
        if (rawTitle.Length > 120)
            rawTitle = rawTitle[..120] + "…";

        var proc = (snap.ProcessName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawTitle))
            return $"category={category}" + (string.IsNullOrWhiteSpace(proc) ? "" : $" ({proc})");

        return string.IsNullOrWhiteSpace(proc)
            ? $"category={category} | title={rawTitle}"
            : $"category={category} | app={proc} | title={rawTitle}";
    }

    public static double GetActivityTargetWeight(ProactiveAwarenessLevel level) => level switch
    {
        ProactiveAwarenessLevel.Low => 0.10,
        ProactiveAwarenessLevel.High => 0.60,
        _ => 0.25
    };

    public static SelectedProactiveTopic PickTopic(ProactiveTopicContext ctx, Random? rng = null)
    {
        rng ??= Random.Shared;

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
            eligible.Add((ProactiveTopicKind.CurrentActivity, 0.0)); // weight set dynamically by AwarenessLevel

        // Exclude last 2 topics to prevent repetition; fall back to excluding only last 1 if needed.
        var lastTwo = ctx.RecentTopics.TakeLast(2).ToHashSet();
        var filtered = eligible.Where(e => !lastTwo.Contains(e.Topic)).ToList();
        if (filtered.Count == 0 && ctx.RecentTopics.Count > 0)
        {
            var lastOne = ctx.RecentTopics[^1];
            filtered = eligible.Where(e => e.Topic != lastOne).ToList();
        }
        if (filtered.Count == 0)
            filtered = eligible;

        // Compute normalized weights respecting AwarenessLevel share for CurrentActivity.
        var hasActivity = filtered.Any(x => x.Topic == ProactiveTopicKind.CurrentActivity);
        var others = filtered.Where(x => x.Topic != ProactiveTopicKind.CurrentActivity).ToList();
        var weighted = new List<(ProactiveTopicKind Topic, double Weight)>(filtered.Count);

        if (hasActivity && others.Count > 0)
        {
            double actShare = GetActivityTargetWeight(ctx.AwarenessLevel);
            double otherShare = 1.0 - actShare;
            double otherBaseSum = others.Sum(o => o.BaseWeight);

            weighted.Add((ProactiveTopicKind.CurrentActivity, actShare));
            foreach (var o in others)
                weighted.Add((o.Topic, otherBaseSum > 0 ? otherShare * (o.BaseWeight / otherBaseSum) : otherShare / others.Count));
        }
        else if (hasActivity)
        {
            weighted.Add((ProactiveTopicKind.CurrentActivity, 1.0));
        }
        else
        {
            double baseSum = others.Sum(o => o.BaseWeight);
            foreach (var o in others)
                weighted.Add((o.Topic, baseSum > 0 ? o.BaseWeight / baseSum : 1.0 / others.Count));
        }

        double roll = rng.NextDouble();
        double acc = 0.0;
        var chosen = weighted[^1].Topic;
        foreach (var (topic, weight) in weighted)
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
        foreach (var msg in ctx.RecentUserMessages24h.Where(s => !string.IsNullOrWhiteSpace(s)).Take(3))
            items.Add($"[user_recent] {Compact(msg, 180)}");
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

/// <summary>
/// Exponential backoff interval calculator for unanswered proactive check-ins (e.g. 15 -> 30 -> 60 min).
/// Skips do NOT advance unanswered count; any user interaction resets unanswered count to 0.
/// </summary>
public static class ProactiveBackoffCalculator
{
    public static int ComputeNextIntervalMinutes(
        int baseMinMinutes,
        int baseMaxMinutes,
        int unansweredCount,
        int backoffCapMinutes = 60,
        Random? rng = null)
    {
        int min = Math.Clamp(baseMinMinutes, 1, 180);
        int max = Math.Clamp(baseMaxMinutes, min, 180);
        int cap = Math.Max(max, Math.Clamp(backoffCapMinutes, 15, 1440));
        int baseRoll = min == max ? min : (rng ?? Random.Shared).Next(min, max + 1);
        int shift = Math.Clamp(unansweredCount, 0, 10);
        long scaled = (long)baseRoll << shift;
        return (int)Math.Min(scaled, cap);
    }
}
