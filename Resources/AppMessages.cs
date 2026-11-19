using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;

namespace AiSecretary.Desktop.Resources;

/// <summary>
/// Pure English base UI templates (messages_en.json) with AI-generated, SQLite-cached localization
/// (UiLocaleCache) for any user-specified ReplyLanguage. Contains zero hardcoded non-English strings.
/// </summary>
public static class AppMessages
{
    private static readonly Lazy<Dictionary<string, string>> EnglishBase = new(() =>
    {
        var asm = typeof(AppMessages).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("messages_en.json", StringComparison.OrdinalIgnoreCase));
        if (resName is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var stream = asm.GetManifestResourceStream(resName);
        if (stream is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    });

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> LocaleCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlightTranslations = new(StringComparer.OrdinalIgnoreCase);
    private static Func<string?>? _languageProvider;
    private static SqliteStore? _store;
    private static IModelGateway? _model;

    public static void SetLanguageProvider(Func<string?> provider) => _languageProvider = provider;

    public static async Task InitializeAsync(
        Func<string?> languageProvider,
        SqliteStore store,
        IModelGateway model,
        CancellationToken ct = default)
    {
        _languageProvider = languageProvider;
        _store = store;
        _model = model;

        var lang = ResolveTargetLanguage();
        if (!IsEnglish(lang))
        {
            var saved = await store.GetUiLocaleCacheAsync(lang, ct);
            var dict = LocaleCache.GetOrAdd(lang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            foreach (var kv in saved)
                dict[kv.Key] = kv.Value;

            // Pre-warm missing EnglishBase templates in the background.
            var missing = EnglishBase.Value.Where(kv => !dict.ContainsKey(kv.Key)).ToList();
            if (missing.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    using var gate = new SemaphoreSlim(3);
                    var tasks = missing.Select(async kv =>
                    {
                        await gate.WaitAsync();
                        try { await TranslateAndCacheAsync(lang, kv.Key, kv.Value, CancellationToken.None); }
                        finally { gate.Release(); }
                    });
                    await Task.WhenAll(tasks);
                });
            }
        }
    }

    private static string ResolveTargetLanguage()
    {
        var raw = _languageProvider?.Invoke()?.Trim();
        return string.IsNullOrWhiteSpace(raw) ? "English" : raw;
    }

    private static bool IsEnglish(string lang)
        => lang.Contains("English", StringComparison.OrdinalIgnoreCase)
           || lang.Equals("en", StringComparison.OrdinalIgnoreCase)
           || lang.Equals("en-US", StringComparison.OrdinalIgnoreCase);

    private static string Get(string key)
    {
        var enTemplate = EnglishBase.Value.TryGetValue(key, out var enVal) ? enVal : key;
        return Localize(key, enTemplate);
    }

    /// <summary>
    /// Dynamically translates and caches any self-described English string (e.g. from plug-and-play skills)
    /// without requiring pre-registration in messages_en.json.
    /// </summary>
    public static string Localize(string cacheKey, string englishText)
    {
        var targetLang = ResolveTargetLanguage();
        if (IsEnglish(targetLang))
            return englishText;

        var dict = LocaleCache.GetOrAdd(targetLang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (dict.TryGetValue(cacheKey, out var cached))
            return cached;

        TriggerBackgroundTranslation(targetLang, cacheKey, englishText);
        return englishText;
    }

    /// <summary>動態翻譯並格式化英文模板字串（免預先登錄於 messages_en.json）。</summary>
    public static string LocalizeFormat(string cacheKey, string englishTemplate, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, Localize(cacheKey, englishTemplate), args);

    /// <summary>非同步取得翻譯字串（首次未快取時等待翻譯完成再回傳，避免第一次顯示英文）。</summary>
    public static async Task<string> LocalizeAsync(string cacheKey, string englishText, CancellationToken ct = default)
    {
        var targetLang = ResolveTargetLanguage();
        if (IsEnglish(targetLang))
            return englishText;

        var dict = LocaleCache.GetOrAdd(targetLang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (dict.TryGetValue(cacheKey, out var cached))
            return cached;

        return await TranslateAndCacheAsync(targetLang, cacheKey, englishText, ct) ?? englishText;
    }

    public static async Task<string> LocalizeFormatAsync(string cacheKey, string englishTemplate, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, await LocalizeAsync(cacheKey, englishTemplate), args);

    private static void TriggerBackgroundTranslation(string targetLang, string key, string enTemplate)
    {
        if (_model is null || _store is null) return;
        var inflightKey = $"{targetLang}|{key}";
        if (!InFlightTranslations.TryAdd(inflightKey, 0)) return;

        _ = Task.Run(() => TranslateAndCacheAsync(targetLang, key, enTemplate, CancellationToken.None));
    }

    private static async Task<string?> TranslateAndCacheAsync(string targetLang, string key, string enTemplate, CancellationToken ct)
    {
        if (_model is null || _store is null) return null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            var sys = SystemAutomationPrompts.BuildUiTranslateSystemPrompt(targetLang);
            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", enTemplate)
            }, ModelTier.Simple, timeoutCts.Token);

            var translated = resp.Content.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(translated) || !HasSamePlaceholders(enTemplate, translated))
                return null;

            var dict = LocaleCache.GetOrAdd(targetLang, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            dict[key] = translated;
            await _store.UpsertUiLocaleCacheAsync(targetLang, key, translated, CancellationToken.None);
            return translated;
        }
        catch
        {
            // Offline / unconfigured LLM: safely keep English base fallback.
            return null;
        }
    }

    private static bool HasSamePlaceholders(string source, string translated)
    {
        var expected = Regex.Matches(source, @"\{\d+\}").Select(m => m.Value).Distinct().ToList();
        return expected.All(ph => translated.Contains(ph, StringComparison.Ordinal));
    }

    private static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, Get(key), args);

    // User-facing configuration & status messages
    public static string MissingLlmConfig => Get(nameof(MissingLlmConfig));
    public static string ErrorPrefix => Get(nameof(ErrorPrefix));
    public static string StatusSpeechTitle => Get(nameof(StatusSpeechTitle));

    // User confirmation dialogs
    public static string ExecuteSkillTitle => Get(nameof(ExecuteSkillTitle));
    public static string ExecuteSkillPrompt(string name, string command, string? extraArgs, string sandboxRoot)
    {
        var argsLine = string.IsNullOrWhiteSpace(extraArgs) ? "" : Format("ExecuteSkillArgsLine", extraArgs);
        return Format(nameof(ExecuteSkillPrompt), name, command, argsLine, sandboxRoot);
    }
    public static string CreateSkillTitle => Get(nameof(CreateSkillTitle));
    public static string CreateSkillPrompt(string name, string description, string command, string sandboxRoot)
        => Format(nameof(CreateSkillPrompt), name, description, command, sandboxRoot);
    public static string DeleteSkillTitle => Get(nameof(DeleteSkillTitle));
    public static string DeleteSkillPrompt(string name, string description)
        => Format(nameof(DeleteSkillPrompt), name, description);

    // User-facing mail/notification cards
    public static string VipEwsTitle => Get(nameof(VipEwsTitle));
    public static string VipGmailTitle => Get(nameof(VipGmailTitle));
    public static string DefaultTriageTitle => Get(nameof(DefaultTriageTitle));
    public static string DefaultTriageSummary(string senderName, string subject, string snippet)
        => Format(nameof(DefaultTriageSummary), senderName, subject, snippet);

    // MainAgent / Planner UI progress statuses
    public static string StageMainRouting => Get(nameof(StageMainRouting));
    public static string StageMainDone => Get(nameof(StageMainDone));
    public static string StageWorkerDispatch(string skill, string tier) => Format(nameof(StageWorkerDispatch), skill, tier);
    public static string StageWorkerDone(string skill) => Format(nameof(StageWorkerDone), skill);
    public static string StagePlannerPlanning(string tier) => Format(nameof(StagePlannerPlanning), tier);
    public static string DispatchSkillAck(string skill) => Format(nameof(DispatchSkillAck), skill);
    public static string DispatchPlannerAck(string goal) => Format(nameof(DispatchPlannerAck), goal);
    public static string ProgressStatus(string ack, string progress) => Format(nameof(ProgressStatus), ack, progress);
    public static string StageWorkerPreparing(int count) => Format(nameof(StageWorkerPreparing), count);
    public static string StageWorkerStep(string skillName, int index, int total) => Format(nameof(StageWorkerStep), skillName, index, total);

    // SkillRuntime user-facing dialogs, direct replies & calendar reminders
    public static string SandboxOutsideTitle => Get(nameof(SandboxOutsideTitle));
    public static string SandboxOutsideMessage(string act) => Format(nameof(SandboxOutsideMessage), act);
    public static string RootLabelDrive => Get(nameof(RootLabelDrive));
    public static string RootLabelUserProfile => Get(nameof(RootLabelUserProfile));
    public static string OrganizeRootTitle(string label) => Format(nameof(OrganizeRootTitle), label);
    public static string OrganizeRootMessage(string label, string rootFull) => Format(nameof(OrganizeRootMessage), label, rootFull);
    public static string CrossFolderTitle => Get(nameof(CrossFolderTitle));
    public static string CrossFolderMessage(string opLabel, string rootFull) => Format(nameof(CrossFolderMessage), opLabel, rootFull);
    public static string OpLabelSource => Get(nameof(OpLabelSource));
    public static string OpLabelDest => Get(nameof(OpLabelDest));
    public static string OpLabelSourceDir => Get(nameof(OpLabelSourceDir));
    public static string OpLabelDestDir => Get(nameof(OpLabelDestDir));
    public static string OpLabelDeleteSource => Get(nameof(OpLabelDeleteSource));
    public static string Stage2DeleteTitle => Get(nameof(Stage2DeleteTitle));
    public static string Stage2DeleteMessage(string quarantineDir, int count, string preview, string moreNote)
        => Format(nameof(Stage2DeleteMessage), quarantineDir, count, preview, moreNote);
    public static string Stage2MoreNote(int extra) => Format(nameof(Stage2MoreNote), extra);
    public static string FallbackConfirmSuffix(string message, string targetPath) => Format(nameof(FallbackConfirmSuffix), message, targetPath);
    public static string CalendarLeadTitle(string title) => Format(nameof(CalendarLeadTitle), title);
    public static string CalendarLeadMessage(DateTimeOffset startAt, string title)
        => Format(nameof(CalendarLeadMessage), startAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), title);
    public static string QuietModeDisabledReply => Get(nameof(QuietModeDisabledReply));
    public static string QuietModeEnabledReply(DateTimeOffset until, bool allowUrgent)
    {
        var urgentNote = allowUrgent ? Get("QuietModeUrgentNote") : Get("QuietModeSilentNote");
        return Format(nameof(QuietModeEnabledReply), until.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), urgentNote);
    }
    public static string CannotDismissPinnedReminder => Get(nameof(CannotDismissPinnedReminder));
    public static string OtpCopyLabel(string code) => Format(nameof(OtpCopyLabel), code);
    public static string OtpFillLabel(string code) => Format(nameof(OtpFillLabel), code);
    public static string OtpCopiedStatus(string code) => Format(nameof(OtpCopiedStatus), code);
    public static string ViewMailLabel => Get(nameof(ViewMailLabel));
    public static string DraftReplyLabel => Get(nameof(DraftReplyLabel));
    public static string SnoozeLabel => Get(nameof(SnoozeLabel));
}
