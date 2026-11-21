using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Runtime UI & status localization service backed by SQLite (UiLocaleCache) + Simple LLM translation.
/// Translates on first use (awaiting before displaying) and caches persistently in SQLite, eliminating static Resources/.
/// </summary>
public sealed class UiLocalizer
{
    private readonly AppSettingsService _settings;
    private readonly SqliteStore _store;
    private readonly IModelGateway _model;
    private readonly ILogger<UiLocalizer> _logger;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Lang, string Key), Lazy<Task<string>>> _inFlight = new();

    public UiLocalizer(
        AppSettingsService settings,
        SqliteStore store,
        IModelGateway model,
        ILogger<UiLocalizer> logger)
    {
        _settings = settings;
        _store = store;
        _model = model;
        _logger = logger;
    }

    public string CurrentLanguage
    {
        get
        {
            var lang = _settings.Current.Llm.ReplyLanguage;
            return string.IsNullOrWhiteSpace(lang) ? "繁體中文" : lang.Trim();
        }
    }

    public static bool IsEnglish(string? lang)
    {
        var n = (lang ?? "").Trim().ToLowerInvariant();
        return n is "en" or "english" or "en-us" or "en-gb" or "en-au" or "en-ca";
    }

    /// <summary>
    /// Builds the startup hot localization entry set covering synchronous UI/status labels and all registered basic skills.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildCoreHotEntries(SkillRegistry registry)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProcessingStatus"] = "Processing...",
            ["MonitorDefault"] = "Scheduled sync (configurable)",
            ["AlertTitleDefault"] = "AI Secretary",
            ["AlertTitleReply"] = "AI Secretary Reply",
            ["AlertTitleReport"] = "AI Secretary Report",
            ["AlertTitleStatus"] = "AI Secretary Status",
            ["StepMainReceived"] = "MainAgent received prompt",
            ["StepCompleted"] = "Completed",
            ["StepStopped"] = "Stopped",
            ["StepError"] = "Error: {0}",
            ["StepKilledByUser"] = "Cleared by /kill magent",
            ["StageMainRouting"] = "MainAgent analyzing intent and routing...",
            ["StageMainDone"] = "MainAgent replied directly",
            ["StageMainJoined"] = "Joined running background task",
            ["JoinTaskDefaultReply"] = "Still checking on that—I'll answer everything together as soon as it finishes.",
            ["StageWorkerDispatch"] = "MainAgent dispatched Worker ({0}, {1})",
            ["StageWorkerDone"] = "Worker finished {0}",
            ["StageWorkerPreparing"] = "Worker preparing {0} step(s)...",
            ["StageWorkerStep"] = "Worker running {0} ({1}/{2})...",
            ["StagePlannerPlanning"] = "MainAgent -> Planner ({0})...",
            ["TurnStopped"] = "(Stopped)",
            ["ErrorPrefix"] = "Error",
            ["GuardSkillDisabled"] = "Skill '{0}' is disabled.",
            ["TtsDefaultVoice"] = "Default Voice",
            ["TtsTestPhrase"] = "Voice test completed. Ready whenever you are.",
            ["TtsKeywordMail"] = "email",
            ["TtsKeywordSchedule"] = "reminder",
            ["TtsFallbackMail"] = "You have a new email notification.",
            ["TtsFallbackSchedule"] = "You have an upcoming schedule reminder.",
            ["TtsFallbackDone"] = "Done. Please check the screen.",
            ["path_perm_title"] = "External File Access",
            ["OtpExtensionFilledSafe"] = "Filled {0} into '{1}' via browser extension ({2}, not auto-submitted).",
            ["OtpFilledSafe"] = "Filled {0} into '{1}' (not auto-submitted).",
            ["OtpTypedSafe"] = "Entered {0} into '{1}' (not auto-submitted).",
            ["OtpCopiedSafeStatus"] = "Copied {0} to clipboard!"
        };

        foreach (var (key, val) in registry.GetHotLocalizationEntries())
            map[key] = val;

        return map;
    }

    public async Task InitializeAsync(IReadOnlyDictionary<string, string> hotEntries, CancellationToken ct = default)
    {
        var lang = CurrentLanguage;
        var langCache = _cache.GetOrAdd(lang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        try
        {
            var saved = await _store.GetUiLocaleCacheAsync(lang, ct);
            foreach (var kv in saved)
                langCache[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load UiLocaleCache for {Lang}", lang);
        }

        if (IsEnglish(lang) || hotEntries.Count == 0)
            return;

        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in hotEntries)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value) && !langCache.ContainsKey(kv.Key))
                missing[kv.Key] = kv.Value;
        }

        if (missing.Count == 0)
            return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            await PreWarmBatchAsync(lang, langCache, missing, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Startup UI localization pre-warm skipped/partial for {Lang}", lang);
        }
    }

    public Task RefreshLanguageAsync(IReadOnlyDictionary<string, string> hotEntries, CancellationToken ct = default)
        => InitializeAsync(hotEntries, ct);

    public Task<string> TextAsync(string key, string englishTemplate, params object?[] args)
        => TextAsync(key, englishTemplate, args, CancellationToken.None);

    public async Task<string> TextAsync(string key, string englishTemplate, object?[] args, CancellationToken ct)
    {
        var lang = CurrentLanguage;
        if (IsEnglish(lang) || string.IsNullOrWhiteSpace(englishTemplate))
            return SafeFormat(englishTemplate, args);

        var langCache = _cache.GetOrAdd(lang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (langCache.TryGetValue(key, out var cachedTpl))
            return SafeFormat(cachedTpl, args);

        var translatedTpl = await TranslateAndCacheDedupAsync(lang, key, englishTemplate, ct);
        return SafeFormat(translatedTpl, args);
    }

    /// <summary>
    /// Synchronous lookup for startup-prewarmed UI strings; falls back to triggering background translation on unexpected miss.
    /// </summary>
    public string TextCached(string key, string englishTemplate, params object?[] args)
    {
        var lang = CurrentLanguage;
        if (IsEnglish(lang) || string.IsNullOrWhiteSpace(englishTemplate))
            return SafeFormat(englishTemplate, args);

        var langCache = _cache.GetOrAdd(lang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (langCache.TryGetValue(key, out var cachedTpl))
            return SafeFormat(cachedTpl, args);

        _ = Task.Run(() => TranslateAndCacheDedupAsync(lang, key, englishTemplate, CancellationToken.None));
        return SafeFormat(englishTemplate, args);
    }

    /// <summary>
    /// Strictly zero-LLM synchronous lookup: returns cached localized template if already pre-cached,
    /// otherwise formats <paramref name="fallbackTemplate"/> directly without spawning any background translation LLM call.
    /// </summary>
    public string TextCachedOnly(string key, string fallbackTemplate, params object?[] args)
    {
        if (string.IsNullOrWhiteSpace(fallbackTemplate))
            return "";

        var lang = CurrentLanguage;
        if (_cache.TryGetValue(lang, out var langCache) && langCache.TryGetValue(key, out var cachedTpl))
            return SafeFormat(cachedTpl, args);

        return SafeFormat(fallbackTemplate, args);
    }

    private async Task<string> TranslateAndCacheDedupAsync(string lang, string key, string englishTemplate, CancellationToken ct)
    {
        var lazy = _inFlight.GetOrAdd(
            (lang, key),
            _ => new Lazy<Task<string>>(() => TranslateAndCacheCoreAsync(lang, key, englishTemplate), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        catch
        {
            return englishTemplate;
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                _inFlight.TryRemove((lang, key), out _);
        }
    }

    private async Task<string> TranslateAndCacheCoreAsync(string lang, string key, string englishTemplate)
    {
        var langCache = _cache.GetOrAdd(lang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        try
        {
            // Double-check DB before hitting LLM
            var existing = await _store.GetUiLocaleCacheAsync(lang);
            foreach (var kv in existing)
                langCache[kv.Key] = kv.Value;
            if (langCache.TryGetValue(key, out var found))
                return found;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", SystemAutomationPrompts.BuildUiTranslateSystemPrompt(lang)),
                new ModelMessage("user", englishTemplate)
            }, new LlmCallOptions("UiLocalizer", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), timeoutCts.Token);

            var translated = (resp.Content ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(translated) && HasSamePlaceholders(englishTemplate, translated))
            {
                langCache[key] = translated;
                await _store.UpsertUiLocaleCacheAsync(lang, key, translated);
                return translated;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UI string translation skipped for {Lang}:{Key}", lang, key);
        }

        return englishTemplate;
    }

    private async Task PreWarmBatchAsync(
        string lang,
        ConcurrentDictionary<string, string> langCache,
        Dictionary<string, string> missing,
        CancellationToken ct)
    {
        const int batchSize = 80;
        var items = missing.ToList();
        for (int i = 0; i < items.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = items.Skip(i).Take(batchSize).ToDictionary(k => k.Key, v => v.Value);
            var jsonIn = JsonSerializer.Serialize(chunk);

            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", SystemAutomationPrompts.BuildUiBatchTranslateSystemPrompt(lang)),
                new ModelMessage("user", jsonIn)
            }, new LlmCallOptions("UiLocalizer.Batch", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), ct);

            var raw = StripJsonFences((resp.Content ?? "").Trim());
            if (string.IsNullOrWhiteSpace(raw)) continue;

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!chunk.TryGetValue(prop.Name, out var srcTpl)) continue;
                var val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(val) || !HasSamePlaceholders(srcTpl, val)) continue;

                langCache[prop.Name] = val;
                await _store.UpsertUiLocaleCacheAsync(lang, prop.Name, val, ct);
            }
        }
    }

    private static bool HasSamePlaceholders(string source, string translated)
    {
        var expected = Regex.Matches(source, @"\{\d+\}").Select(m => m.Value).Distinct();
        return expected.All(ph => translated.Contains(ph, StringComparison.Ordinal));
    }

    private static string StripJsonFences(string raw)
    {
        if (!raw.StartsWith("```", StringComparison.Ordinal))
        {
            var first = raw.IndexOf('{');
            var last = raw.LastIndexOf('}');
            return first >= 0 && last > first ? raw[first..(last + 1)] : raw;
        }
        var nl = raw.IndexOf('\n');
        if (nl < 0) return raw;
        var inner = raw[(nl + 1)..];
        var end = inner.LastIndexOf("```", StringComparison.Ordinal);
        return (end >= 0 ? inner[..end] : inner).Trim();
    }

    public static string SafeFormat(string template, params object?[] args)
    {
        if (args is null || args.Length == 0)
            return template;
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return $"{template} ({string.Join(", ", args)})";
        }
    }
}
