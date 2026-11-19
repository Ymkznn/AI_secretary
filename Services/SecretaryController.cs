using System.Collections.Concurrent;
using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Services;

public sealed class SecretaryController : IDisposable
{
    private readonly SqliteStore _store;
    private readonly IMailBroker _mailBroker;
    private readonly GmailSkillService _gmail;
    private readonly MailSyncService _mailSync;
    private readonly MainAgent _mainAgent;
    private readonly PlannerAgent _planner;
    private readonly WorkerAgent _worker;
    private readonly SkillRuntime _skills;
    private readonly CustomSkillService _customSkills;
    private readonly SandboxService _sandbox;
    private readonly GuardService _guard;
    private readonly StyleService _styles;
    private readonly TtsService _tts;
    private readonly AppSettingsService _settings;
    private readonly InteractionGateway _interaction;
    private readonly ActivityMonitorService _activity;
    private readonly PageIntentPredictorService _pagePredictor;
    private readonly BrowserHostService _browserHost;
    private readonly BrowserFormBridgeService _browserBridge;
    private readonly StandingWatcherService _watchers;
    private readonly WorkflowCacheService _workflowCache;
    private readonly WorkflowOptimizerAgent _optimizer;
    private readonly IConfiguration _config;
    private readonly ILogger<SecretaryController> _logger;

    private readonly CancellationTokenSource _appCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();
    private readonly ConcurrentDictionary<string, ActiveAgentRun> _agentRuns = new();
    private readonly List<PinnedReminderItem> _pinnedReminders = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pinnedNextRemindAt = new();
    private readonly object _pinnedLock = new();
    private readonly SemaphoreSlim _mainTurnGate = new(1, 1);
    private DateTimeOffset? _dndUntil;
    private bool _dndAllowUrgent = true;
    private DateTimeOffset _otpBurstUntilUtc = DateTimeOffset.MinValue;
    private string? _activeOtpPatternKey;
    private sealed class ActivePlannerTaskState
    {
        public required string LogicalTaskId { get; init; }
        public required string Goal { get; set; }
        public int RunningCount;
        public DateTimeOffset LastUpdatedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, ActivePlannerTaskState> _activePlannerTasks = new(StringComparer.Ordinal);
    private DateTimeOffset _lastUserActivityUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastProactiveChatUtc = DateTimeOffset.UtcNow;

    private bool IsDndActive() => _dndUntil.HasValue && DateTimeOffset.UtcNow < _dndUntil.Value;

    public event Action? StateChanged;
    public event Action<string, string>? ProactiveAlert;
    public event Action? PinnedRemindersChanged;
    public event Action? SkillsChanged;
    public event Action<string>? ThemeChanged;
    public event Action? AgentsChanged;
    public event Action<int>? FadeSettingsChanged;
    public event Action<int>? AnimationFpsChanged;

    public Func<string, string, Task<bool>>? ConfirmActionHandler
    {
        get => _interaction.UiConfirmHandler;
        set => _interaction.UiConfirmHandler = value;
    }

    public Func<string, string, string, Task<PathPermissionDecision>>? ConfirmPathPermissionHandler
    {
        get => _interaction.UiPathPermissionHandler;
        set => _interaction.UiPathPermissionHandler = value;
    }

    public Func<string, string, Task<CommandApprovalDecision>>? ConfirmCommandApprovalHandler
    {
        get => _interaction.UiCommandApprovalHandler;
        set => _interaction.UiCommandApprovalHandler = value;
    }

    public IReadOnlyList<PendingInteractionRequest> GetPendingInteractions()
        => _interaction.GetPendingRequests();

    public bool ResolveConfirmation(string requestId, bool approved)
        => _interaction.ResolveConfirmation(requestId, approved);

    public bool ResolvePathPermission(string requestId, PathPermissionDecision decision)
        => _interaction.ResolvePathPermission(requestId, decision);

    public bool ResolveCommandApproval(string requestId, CommandApprovalDecision decision)
        => _interaction.ResolveCommandApproval(requestId, decision);
    public event Action<string>? InteractionResolved
    {
        add => _interaction.PendingRequestResolved += value;
        remove => _interaction.PendingRequestResolved -= value;
    }
    public event Action? TrustedPathsChanged;
    public event Action<ProactiveSuggestionEvent>? ProactiveSuggestionRaised;
    public event Action<StandingWatcherRecord, string>? WatcherTriggered;

    public string ConversationId { get; private set; } = "conv-main";
    public string MonitorStatus { get; private set; } = AppMessages.Localize("MonitorStandby", "Standby");
    public bool IsGmailAuthorized => _gmail.IsAuthorized;
    public bool HasGmailClientCredentials => _gmail.HasClientCredentials;
    public string SandboxRootPath => _sandbox.RootPath;

    public SecretaryController(
        SqliteStore store,
        IMailBroker mailBroker,
        GmailSkillService gmail,
        MailSyncService mailSync,
        MainAgent mainAgent,
        PlannerAgent planner,
        WorkerAgent worker,
        SkillRuntime skills,
        CustomSkillService customSkills,
        SandboxService sandbox,
        GuardService guard,
        StyleService styles,
        TtsService tts,
        AppSettingsService settings,
        InteractionGateway interaction,
        ActivityMonitorService activity,
        PageIntentPredictorService pagePredictor,
        BrowserHostService browserHost,
        BrowserFormBridgeService browserBridge,
        StandingWatcherService watchers,
        WorkflowCacheService workflowCache,
        WorkflowOptimizerAgent optimizer,
        IConfiguration config,
        ILogger<SecretaryController> logger)
    {
        _store = store;
        _mailBroker = mailBroker;
        _gmail = gmail;
        _mailSync = mailSync;
        _mainAgent = mainAgent;
        _planner = planner;
        _worker = worker;
        _skills = skills;
        _customSkills = customSkills;
        _sandbox = sandbox;
        _guard = guard;
        _styles = styles;
        _tts = tts;
        _settings = settings;
        _interaction = interaction;
        _activity = activity;
        _pagePredictor = pagePredictor;
        _browserHost = browserHost;
        _browserBridge = browserBridge;
        _watchers = watchers;
        _workflowCache = workflowCache;
        _optimizer = optimizer;
        _config = config;
        _logger = logger;

        _skills.ConfirmPathPermissionHandler = (title, msg, targetPath) =>
            _interaction.RequestPathPermissionAsync(title, msg, targetPath, _appCts.Token);

        _skills.SyncMailRequested = (convId, source, ct) =>
            _mailSync.SyncOnceAsync(
                convId,
                source,
                (eventId, mail, title, summary, mins, actions) =>
                    PushProactiveNotificationAsync(convId, eventId, mail, title, summary, ct, mins, actions),
                ct);

        _customSkills.SkillsChanged += () => SkillsChanged?.Invoke();
        _skills.SchedulesChanged += () => AgentsChanged?.Invoke();
        _skills.QuietModeChanged += (until, allowUrgent) =>
        {
            _dndUntil = until;
            _dndAllowUrgent = allowUrgent;
        };
        _skills.TrustedPathAdded += path =>
        {
            _ = PersistTrustedPathsAsync();
            TrustedPathsChanged?.Invoke();
        };
        _skills.PinReminderRequested += (title, message, mins) =>
            AddPinnedReminder(new PinnedReminderItem(Guid.NewGuid().ToString("N"), title, message, null, null, mins));
        _skills.DismissPinnedReminderRequested += DismissPinnedByKeywordAsync;
        _skills.PublishNotificationHandler = PersistNotificationAsync;
        _skills.UserPrefChanged += convId =>
            _ = _workflowCache.InvalidateEventWorkflowsAsync(convId, CancellationToken.None);
        _skills.FactModified += (convId, factId) =>
            _ = _workflowCache.InvalidateBySourceFactAsync(convId, factId, CancellationToken.None);
    }

    public async Task InitializeAsync()
    {
        ConversationId = await _store.GetActiveConversationIdAsync();
        var disabledJson = await _store.GetSyncValueAsync("disabled_skills", _appCts.Token);
        if (!string.IsNullOrWhiteSpace(disabledJson))
        {
            try
            {
                var disabled = JsonSerializer.Deserialize<List<string>>(disabledJson);
                if (disabled is not null) _guard.LoadDisabledSkills(disabled);
            }
            catch { }
        }

        var pinnedJson = await _store.GetSyncValueAsync("pinned_reminders", _appCts.Token);
        if (!string.IsNullOrWhiteSpace(pinnedJson))
        {
            try
            {
                var savedPinned = JsonSerializer.Deserialize<List<PinnedReminderItem>>(pinnedJson);
                if (savedPinned is { Count: > 0 })
                {
                    lock (_pinnedLock)
                    {
                        _pinnedReminders.Clear();
                        _pinnedReminders.AddRange(savedPinned);
                    }
                    Dictionary<string, DateTimeOffset>? savedNextAt = null;
                    var nextAtJson = await _store.GetSyncValueAsync("pinned_next_remind_at", _appCts.Token);
                    if (!string.IsNullOrWhiteSpace(nextAtJson))
                    {
                        try
                        {
                            savedNextAt = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(nextAtJson);
                        }
                        catch { }
                    }
                    foreach (var p in savedPinned.Where(x => x.ReRemindMinutes > 0))
                    {
                        _pinnedNextRemindAt[p.Id] = savedNextAt is not null && savedNextAt.TryGetValue(p.Id, out var existing)
                            ? existing
                            : DateTimeOffset.UtcNow.AddMinutes(p.ReRemindMinutes);
                    }
                    PinnedRemindersChanged?.Invoke();
                }
            }
            catch { }
        }

        var savedStylePath = await _store.GetSyncValueAsync("active_style_path", _appCts.Token);
        _styles.SetActiveStylePath(savedStylePath);

        var trustedPathsJson = await _store.GetSyncValueAsync("trusted_paths", _appCts.Token);
        if (!string.IsNullOrWhiteSpace(trustedPathsJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(trustedPathsJson);
                if (list is not null)
                    _sandbox.SetTrustedPaths(list);
            }
            catch { }
        }

        var ttsEnabledStr = await _store.GetSyncValueAsync("tts_enabled", _appCts.Token);
        var ttsVoice = await _store.GetSyncValueAsync("tts_voice_profile", _appCts.Token);
        var ttsMode = await _store.GetSyncValueAsync("tts_chat_mode", _appCts.Token);
        bool ttsEnabled = string.IsNullOrWhiteSpace(ttsEnabledStr) ? _tts.IsEnabled : !string.Equals(ttsEnabledStr, "false", StringComparison.OrdinalIgnoreCase);
        _tts.Configure(ttsEnabled, ttsVoice ?? _tts.VoiceProfile, ttsMode ?? _tts.ChatMode);
        _tts.EnsureServerStartedInBackground();

        var dndJson = await _store.GetSyncValueAsync("dnd_state", _appCts.Token);
        if (!string.IsNullOrWhiteSpace(dndJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(dndJson);
                if (doc.RootElement.TryGetProperty("until", out var u) && DateTimeOffset.TryParse(u.GetString(), out var until))
                    _dndUntil = until;
                if (doc.RootElement.TryGetProperty("allow_urgent", out var au))
                    _dndAllowUrgent = au.GetBoolean();
            }
            catch { }
        }

        // Clear any orphaned IsStreaming messages left over from a previous crash/forced exit
        var recentMsgs = await _store.GetChatMessagesAsync(ConversationId, 30);
        foreach (var m in recentMsgs.Where(x => x.IsStreaming))
        {
            m.IsStreaming = false;
            m.StatusText = null;
            if (string.IsNullOrWhiteSpace(m.Content))
                m.Content = AppMessages.Localize("TaskInterrupted", "(Previous task was interrupted)");
            await _store.UpsertChatMessageAsync(m, _appCts.Token);
        }

        // Bootstrap default mail-sync schedule once so Agent & user can inspect/update/delete it via schedules
        var bootstrapped = await _store.GetSyncValueAsync("mail_monitor_bootstrapped", _appCts.Token);
        if (bootstrapped != "1")
        {
            var existing = await _store.GetPendingRemindersAsync(_appCts.Token);
            if (!existing.Any(r =>
                    string.Equals(r.SkillName, "manage_mail", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.SkillName, "sync_mail", StringComparison.OrdinalIgnoreCase)))
            {
                var intervalMins = Math.Max(1, _config.GetValue<int>("MailMonitor:PollIntervalSeconds", 60) / 60);
                await _store.CreateReminderAsync(new ReminderRecord(
                    ReminderId: Guid.NewGuid().ToString("N"),
                    ConversationId: ConversationId,
                    Title: "Mail Sync",
                    Message: "Periodically sync Outlook EWS and Gmail and triage urgent mail.",
                    RelatedMailId: null,
                    RemindAt: DateTimeOffset.UtcNow.AddSeconds(10),
                    Status: "Pending",
                    IntervalMinutes: intervalMins,
                    SkillName: "manage_mail",
                    SkillArgsJson: """{"action":"sync","source":"all"}"""), _appCts.Token);
            }
            await _store.SetSyncValueAsync("mail_monitor_bootstrapped", "1", _appCts.Token);
        }

        await _workflowCache.LoadTuningAsync(_appCts.Token);
        _activity.SemanticClassifyAsync = (proc, title, fields, token) =>
            _planner.ClassifyWindowIntentAsync(proc, title, fields, token);
        _activity.VerificationPageEntered += (_, patternKey) =>
        {
            _activeOtpPatternKey = patternKey;
            _otpBurstUntilUtc = DateTimeOffset.UtcNow.AddMinutes(3);
        };
        _activity.ProactiveSuggestionRaised += ev =>
        {
            ProactiveSuggestionRaised?.Invoke(ev);
            _ = PushPetMessageAsync($"💡 {ev.SuggestedMessage}", _appCts.Token);
        };
        _activity.Start();

        _ = Task.Run(() => RunSchedulerLoopAsync(_appCts.Token));
    }

    private string? _latestChoicesMsgId;
    private List<string> _latestChoices = new();
    private string? _latestChoicesLogicalTaskId;

    public IReadOnlyList<string> GetChoicesForMessage(string messageId)
        => string.Equals(_latestChoicesMsgId, messageId, StringComparison.Ordinal)
            ? _latestChoices
            : Array.Empty<string>();

    public string? GetChoicesLogicalTaskIdForMessage(string messageId)
        => string.Equals(_latestChoicesMsgId, messageId, StringComparison.Ordinal)
            ? _latestChoicesLogicalTaskId
            : null;

    public void ClearChoices()
    {
        _latestChoicesMsgId = null;
        _latestChoices = new List<string>();
        _latestChoicesLogicalTaskId = null;
    }

    public IReadOnlyList<string> GetTrustedPaths() => _sandbox.GetTrustedPaths();

    public async Task SaveTrustedPathsAsync(IEnumerable<string> paths)
    {
        _sandbox.SetTrustedPaths(paths);
        await PersistTrustedPathsAsync();
        TrustedPathsChanged?.Invoke();
    }

    private Task PersistTrustedPathsAsync()
    {
        var json = JsonSerializer.Serialize(_sandbox.GetTrustedPaths());
        return _store.SetSyncValueAsync("trusted_paths", json, _appCts.Token);
    }

    public Task<List<ChatMessageRecord>> GetMessagesAsync()
        => _store.GetChatMessagesAsync(ConversationId, 100);

    public Task<MailItem?> GetMailDetailAsync(string mailId)
        => mailId.StartsWith("gmail:", StringComparison.OrdinalIgnoreCase)
            ? _gmail.GetMailDetailAsync(mailId)
            : _mailBroker.GetMailByIdAsync(mailId);

    public async Task<IReadOnlyList<SkillMenuEntry>> GetAllSkillsAsync()
    {
        var list = _skills.GetBuiltInSkills().ToList();
        var custom = await _customSkills.ListAsync(_appCts.Token);
        foreach (var c in custom)
        {
            list.Add(new SkillMenuEntry(c.Name, c.Description, true, _guard.IsSkillEnabled(c.Name)));
        }
        return list;
    }

    public async Task SetSkillEnabledAsync(string skillName, bool enabled)
    {
        _guard.SetSkillEnabled(skillName, enabled);
        var json = JsonSerializer.Serialize(_guard.GetDisabledSkills());
        await _store.SetSyncValueAsync("disabled_skills", json, _appCts.Token);
        SkillsChanged?.Invoke();
    }

    public async Task<string> GetThemeModeAsync()
    {
        var mode = await _store.GetSyncValueAsync("theme_mode", _appCts.Token);
        return mode is "Light" or "Dark" or "System" ? mode : "System";
    }

    public async Task SetThemeModeAsync(string mode)
    {
        var normalized = mode is "Light" or "Dark" or "System" ? mode : "System";
        await _store.SetSyncValueAsync("theme_mode", normalized, _appCts.Token);
        ThemeChanged?.Invoke(normalized);
    }

    public async Task<int> GetOutputFadeSecondsAsync()
    {
        var raw = await _store.GetSyncValueAsync("output_fade_seconds", _appCts.Token);
        return int.TryParse(raw, out var sec) && sec >= 0 ? sec : 5;
    }

    public async Task<(double Left, double Top)?> GetWindowPositionAsync()
    {
        var raw = await _store.GetSyncValueAsync("pet_window_pos", _appCts.Token);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var left) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var top) &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            return (left, top);
        }
        return null;
    }

    public Task SaveWindowPositionAsync(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return Task.CompletedTask;
        var val = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1},{1:F1}", left, top);
        return _store.SetSyncValueAsync("pet_window_pos", val, _appCts.Token);
    }

    public IReadOnlyList<StyleProfileEntry> GetAvailableStyles() => _styles.GetAvailableStyles();

    public string? GetActiveStylePath() => _styles.ActiveStylePath;

    public async Task SetActiveStylePathAsync(string? path)
    {
        _styles.SetActiveStylePath(path);
        await _store.SetSyncValueAsync("active_style_path", path ?? "", _appCts.Token);
    }

    public async Task ImportAndActivateStyleAsync(string sourcePath)
    {
        var copied = _styles.ImportStyleFile(sourcePath);
        await _store.SetSyncValueAsync("active_style_path", copied, _appCts.Token);
    }

    public (bool Enabled, string VoiceProfile, string ChatMode) GetTtsSettings()
        => (_tts.IsEnabled, _tts.VoiceProfile, _tts.ChatMode);

    public async Task SaveTtsSettingsAsync(bool enabled, string voiceProfile, string chatMode)
    {
        _tts.Configure(enabled, voiceProfile, chatMode);
        await _store.SetSyncValueAsync("tts_enabled", enabled ? "true" : "false", _appCts.Token);
        await _store.SetSyncValueAsync("tts_voice_profile", _tts.VoiceProfile, _appCts.Token);
        await _store.SetSyncValueAsync("tts_chat_mode", _tts.ChatMode, _appCts.Token);
    }

    public IReadOnlyList<(string Key, string DisplayName)> GetAvailableVoiceProfiles()
        => _tts.GetAvailableVoiceProfiles();

    public void TestTtsVoice()
        => _tts.EnqueueJapaneseSpeech(_tts.GetTestPhrase());

    public async Task<(bool Ok, string Message)> ImportVoicePackageAsync(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return (false, AppMessages.Localize("ZipNotFound", "Selected zip file was not found."));

        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var validEntries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) &&
                            !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var ckpt = validEntries.FirstOrDefault(e => e.Name.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase));
            var pth = validEntries.FirstOrDefault(e => e.Name.EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
            var wavs = validEntries.Where(e => e.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToList();

            if (ckpt is null || pth is null || wavs.Count == 0)
            {
                return (false, AppMessages.Localize("ZipMissingCoreFiles", "Zip must contain at least 3 required files: .ckpt (GPT weights), .pth (SoVITS weights), and .wav (reference audio)."));
            }

            var rawName = Path.GetFileNameWithoutExtension(zipPath).Trim();
            var profileKey = System.Text.RegularExpressions.Regex.Replace(rawName.ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(profileKey))
                profileKey = "voice_" + Guid.NewGuid().ToString("N")[..6];

            var destDir = Path.Combine(AppPaths.UserDataRoot, "tts", "voices", profileKey);
            Directory.CreateDirectory(destDir);
            var fullDestDir = Path.GetFullPath(destDir);

            string? extractedCkpt = null;
            string? extractedPth = null;
            string? mainWav = null;
            var auxWavs = new List<string>();
            string? promptTxtPath = null;
            string? styleMdPath = null;

            foreach (var entry in validEntries)
            {
                var safeName = Path.GetFileName(entry.Name);
                if (string.IsNullOrWhiteSpace(safeName)) continue;

                var targetPath = Path.GetFullPath(Path.Combine(fullDestDir, safeName));
                if (!targetPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                using (var src = entry.Open())
                using (var dst = File.Create(targetPath))
                    await src.CopyToAsync(dst);

                var ext = Path.GetExtension(safeName).ToLowerInvariant();
                if (ext == ".ckpt" && extractedCkpt is null) extractedCkpt = targetPath;
                else if (ext == ".pth" && extractedPth is null) extractedPth = targetPath;
                else if (ext == ".wav")
                {
                    if (safeName.Contains("aux", StringComparison.OrdinalIgnoreCase))
                        auxWavs.Add(targetPath);
                    else if (mainWav is null)
                        mainWav = targetPath;
                    else
                        auxWavs.Add(targetPath);
                }
                else if (ext == ".txt" && promptTxtPath is null) promptTxtPath = targetPath;
                else if (ext == ".md" && styleMdPath is null) styleMdPath = targetPath;
            }

            if (mainWav is null && auxWavs.Count > 0)
            {
                mainWav = auxWavs[0];
                auxWavs.RemoveAt(0);
            }

            if (extractedCkpt is null || extractedPth is null || mainWav is null)
                return (false, AppMessages.Localize("ZipExtractMissingFiles", "Required files (.ckpt / .pth / .wav) are missing after extraction."));

            var promptText = promptTxtPath is not null && File.Exists(promptTxtPath)
                ? (await File.ReadAllTextAsync(promptTxtPath)).Trim()
                : "";
            var detectedLang = (!string.IsNullOrWhiteSpace(styleMdPath) ? StyleService.ParseTtsLanguageFromMd(styleMdPath) : null) ?? "ja";

            var profile = new TtsVoiceProfileSettings
            {
                DisplayName = rawName,
                GptWeightsPath = extractedCkpt,
                SovitsWeightsPath = extractedPth,
                RefAudioPath = mainWav,
                AuxRefAudioPaths = auxWavs.Count > 0 ? auxWavs.ToArray() : null,
                PromptText = promptText,
                StylePromptPath = styleMdPath,
                PromptLang = detectedLang,
                TextLang = detectedLang
            };

            await _settings.SaveOverridesAsync(s => s.Tts.Profiles[profileKey] = profile);
            var (_, _, curMode) = GetTtsSettings();
            await SaveTtsSettingsAsync(true, profileKey, curMode);
            return (true, AppMessages.LocalizeFormat("ZipImportSuccess", "Imported and switched to voice '{0}'.", rawName));
        }
        catch (Exception ex)
        {
            return (false, AppMessages.LocalizeFormat("ZipImportFailed", "Failed to import voice package: {0}", ex.Message));
        }
    }

    public async Task SetOutputFadeSecondsAsync(int seconds)
    {
        var clamped = Math.Clamp(seconds, 0, 120);
        await _store.SetSyncValueAsync("output_fade_seconds", clamped.ToString(), _appCts.Token);
        FadeSettingsChanged?.Invoke(clamped);
    }

    public async Task<int> GetAnimationFpsAsync()
    {
        var raw = await _store.GetSyncValueAsync("animation_fps", _appCts.Token);
        if (int.TryParse(raw, out var fps) && fps is 0 or 15 or 24 or 30 or 60)
            return fps;
        return 24;
    }

    public async Task SetAnimationFpsAsync(int fps)
    {
        var normalized = fps is 0 or 15 or 24 or 30 or 60 ? fps : 24;
        await _store.SetSyncValueAsync("animation_fps", normalized.ToString(), _appCts.Token);
        AnimationFpsChanged?.Invoke(normalized);
    }

    public IReadOnlyList<ActiveAgentRun> GetAgentRuns()
        => _agentRuns.Values.OrderByDescending(r => r.StartedAt).Take(10).ToList();

    public bool HasRunningTasks => !_runningTasks.IsEmpty;

    public async Task<(string Model, string Endpoint, long InputTokens, long OutputTokens, long CacheTokens)> GetModelAndUsageInfoAsync()
    {
        var simpleModel = await _store.GetSyncValueAsync("llm_simple_model", _appCts.Token);
        var complexModel = await _store.GetSyncValueAsync("llm_complex_model", _appCts.Token);
        var defaultModel = _config.GetValue<string>("Foundry:Model")?.Trim() ?? AppMessages.Localize("NotConfigured", "(Not configured)");

        var sLabel = string.IsNullOrWhiteSpace(simpleModel) ? defaultModel : simpleModel.Trim();
        var cLabel = string.IsNullOrWhiteSpace(complexModel) ? defaultModel : complexModel.Trim();
        var model = sLabel == cLabel ? sLabel : AppMessages.LocalizeFormat("ModelTierSummary", "Simple: {0} | Complex: {1}", sLabel, cLabel);

        var simpleEp = await _store.GetSyncValueAsync("llm_simple_endpoint", _appCts.Token);
        var endpoint = string.IsNullOrWhiteSpace(simpleEp)
            ? (_config.GetValue<string>("Foundry:Endpoint")?.Trim() ?? AppMessages.Localize("NotConfigured", "(Not configured)"))
            : simpleEp.Trim();

        long ReadCounter(string? raw) => long.TryParse(raw, out var v) ? v : 0L;
        var inTok = ReadCounter(await _store.GetSyncValueAsync("tokens_input", _appCts.Token));
        var outTok = ReadCounter(await _store.GetSyncValueAsync("tokens_output", _appCts.Token));
        var cacheTok = ReadCounter(await _store.GetSyncValueAsync("tokens_cache", _appCts.Token));
        return (model, endpoint, inTok, outTok, cacheTok);
    }

    public async Task<(string SimpleEp, string SimpleKey, string SimpleModel, string ComplexEp, string ComplexKey, string ComplexModel)> GetLlmProfilesAsync()
    {
        return (
            await _store.GetSyncValueAsync("llm_simple_endpoint", _appCts.Token) ?? "",
            SecretProtector.Unprotect(await _store.GetSyncValueAsync("llm_simple_key", _appCts.Token)),
            await _store.GetSyncValueAsync("llm_simple_model", _appCts.Token) ?? "",
            await _store.GetSyncValueAsync("llm_complex_endpoint", _appCts.Token) ?? "",
            SecretProtector.Unprotect(await _store.GetSyncValueAsync("llm_complex_key", _appCts.Token)),
            await _store.GetSyncValueAsync("llm_complex_model", _appCts.Token) ?? ""
        );
    }

    public async Task SaveLlmProfilesAsync(
        string simpleEp, string simpleKey, string simpleModel,
        string complexEp, string complexKey, string complexModel)
    {
        await _store.SetSyncValueAsync("llm_simple_endpoint", simpleEp.Trim(), _appCts.Token);
        await _store.SetSyncValueAsync("llm_simple_key", SecretProtector.Protect(simpleKey.Trim()), _appCts.Token);
        await _store.SetSyncValueAsync("llm_simple_model", simpleModel.Trim(), _appCts.Token);
        await _store.SetSyncValueAsync("llm_complex_endpoint", complexEp.Trim(), _appCts.Token);
        await _store.SetSyncValueAsync("llm_complex_key", SecretProtector.Protect(complexKey.Trim()), _appCts.Token);
        await _store.SetSyncValueAsync("llm_complex_model", complexModel.Trim(), _appCts.Token);
    }

    public string GetReplyLanguage()
        => string.IsNullOrWhiteSpace(_settings.Current.Llm.ReplyLanguage)
            ? "繁體中文"
            : _settings.Current.Llm.ReplyLanguage.Trim();

    public async Task SaveReplyLanguageAsync(string replyLanguage)
    {
        var val = string.IsNullOrWhiteSpace(replyLanguage) ? "繁體中文" : replyLanguage.Trim();
        await _settings.SaveOverridesAsync(s => s.Llm.ReplyLanguage = val, _appCts.Token);
    }

    public async Task<string?> GetPetImagePathAsync()
    {
        var saved = await _store.GetSyncValueAsync("pet_image_path", _appCts.Token);
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return saved;

        var configured = _config.GetValue<string>("Pet:ImagePath");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
            if (File.Exists(resolved)) return resolved;
        }

        foreach (var candidate in new[] { "pet.png", "pet.jpg", "pet.jpeg", "pet.gif" })
        {
            var full = Path.Combine(AppContext.BaseDirectory, candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public Task SetPetImagePathAsync(string path)
        => _store.SetSyncValueAsync("pet_image_path", path, _appCts.Token);

    public async Task<bool> DeleteCustomSkillAsync(string name)
    {
        var ok = await _store.DeleteCustomSkillAsync(name, _appCts.Token);
        if (ok) SkillsChanged?.Invoke();
        return ok;
    }

    public void SaveGmailCredentials(string clientId, string? clientSecret = null)
        => _gmail.SaveClientCredentials(clientId, clientSecret);

    public async Task<bool> AuthorizeGmailAsync()
    {
        var ok = await _gmail.AuthorizeInteractiveAsync(_appCts.Token);
        if (ok)
        {
            await PushPetMessageAsync(await AppMessages.LocalizeAsync("GmailConnected", "Gmail connected. You can now query your emails."));
        }
        return ok;
    }

    public async Task SendUserPromptAsync(string prompt, string? targetMailId = null, string? replyToLogicalTaskId = null)
    {
        _lastUserActivityUtc = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var userMsg = new ChatMessageRecord
        {
            ConversationId = ConversationId,
            Role = "user",
            Content = prompt,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _store.UpsertChatMessageAsync(userMsg);

        var assistantMsg = new ChatMessageRecord
        {
            ConversationId = ConversationId,
            Role = "assistant",
            Content = "",
            StatusText = AppMessages.Localize("ProcessingStatus", "Processing..."),
            IsStreaming = true,
            TaskId = taskId,
            CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(5)
        };
        ClearChoices();
        await _store.UpsertChatMessageAsync(assistantMsg);
        StateChanged?.Invoke();

        var run = new ActiveAgentRun
        {
            TaskId = taskId,
            Goal = prompt.Length > 40 ? prompt[..40] + "…" : prompt,
            MainState = "Running",
            PlannerState = "Pending",
            WorkerState = "Pending",
            CurrentStep = AppMessages.Localize("StepMainReceived", "MainAgent received prompt")
        };
        _agentRuns[taskId] = run;
        AgentsChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            var appCt = _appCts.Token;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            _runningTasks[taskId] = linked;
            string? directTtsText = null;
            string? pendingText = null;
            List<MailSourceRef>? pendingSources = null;
            var replyReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int replyRevealed = 0;
            Func<CancellationToken, Func<string, List<MailSourceRef>, Task>, Action<string>?, Action<IReadOnlyList<string>>?, Task>? dispatchedWork = null;

            void RevealReply()
            {
                if (Interlocked.Exchange(ref replyRevealed, 1) != 0) return;
                if (!string.IsNullOrWhiteSpace(pendingText))
                    assistantMsg.Content = pendingText;
                if (pendingSources is not null)
                    assistantMsg.Sources = pendingSources;
                assistantMsg.IsStreaming = false;
                assistantMsg.StatusText = null;
                _ = _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                StateChanged?.Invoke();
                if (!string.IsNullOrWhiteSpace(assistantMsg.Content))
                    ProactiveAlert?.Invoke(AppMessages.Localize("AlertTitleReply", "AI Secretary Reply"), assistantMsg.Content);
            }

            bool turnGateAcquired = false;
            string? activePlannerTaskIdThisTurn = null;
            int plannerFinishReported = 0;
            IReadOnlyList<string>? turnLastChoices = null;
            string? turnLastSummary = null;

            void ReleasePlannerRunningIfUnreported()
            {
                if (activePlannerTaskIdThisTurn is null || Interlocked.Exchange(ref plannerFinishReported, 1) != 0)
                    return;
                if (_activePlannerTasks.TryGetValue(activePlannerTaskIdThisTurn, out var st))
                {
                    if (Interlocked.Decrement(ref st.RunningCount) < 0)
                        Interlocked.Exchange(ref st.RunningCount, 0);
                    st.LastUpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            try
            {
                await _mainTurnGate.WaitAsync(linked.Token);
                turnGateAcquired = true;

                CleanupExpiredPlannerTasks();
                var activePlannerSnapshot = _activePlannerTasks.Values
                    .OrderByDescending(t => t.LastUpdatedAt)
                    .Select(t => new ActivePlannerTaskInfo(t.LogicalTaskId, t.Goal, Volatile.Read(ref t.RunningCount) > 0))
                    .ToList();

                await _mainAgent.HandleMessageAsync(
                    ConversationId,
                    taskId,
                    prompt,
                    targetMailId,
                    confirmAsync: (title, msg) => _interaction.RequestConfirmationAsync(title, msg, linked.Token),
                    onStatusAsync: async status =>
                    {
                        assistantMsg.StatusText = status;
                        await _store.UpsertChatMessageAsync(assistantMsg, linked.Token);
                        StateChanged?.Invoke();
                    },
                    onReplyAsync: (text, sources) =>
                    {
                        pendingText = text;
                        pendingSources = sources;
                        turnLastSummary = text;
                        replyReady.TrySetResult(true);
                        return Task.CompletedTask;
                    },
                    onStageChanged: (stage, detail) =>
                    {
                        run.CurrentStep = detail;
                        switch (stage)
                        {
                            case "main":
                                run.MainState = "Running";
                                break;
                            case "main_done":
                                run.MainState = "Done";
                                if (run.WorkerState == "Running") run.WorkerState = "Done";
                                break;
                            case "worker_done":
                                run.MainState = "Done";
                                run.WorkerState = "Done";
                                break;
                            case "planner":
                                run.MainState = "Done";
                                run.PlannerState = "Running";
                                break;
                            case "worker":
                                run.MainState = "Done";
                                if (run.PlannerState == "Running") run.PlannerState = "Done";
                                run.WorkerState = "Running";
                                break;
                        }
                        AgentsChanged?.Invoke();
                    },
                    linked.Token,
                    onDirectTtsText: ttsText =>
                    {
                        directTtsText = ttsText;
                        if (_tts.IsEnabled && _tts.ChatMode != "notify_only" && !string.IsNullOrWhiteSpace(ttsText))
                        {
                            _tts.EnqueueJapaneseSpeech(ttsText, onBeforePlay: () =>
                            {
                                replyReady.Task.GetAwaiter().GetResult();
                                RevealReply();
                            });
                        }
                    },
                    onChoices: choices =>
                    {
                        _latestChoicesMsgId = assistantMsg.Id;
                        _latestChoices = choices.Take(4).ToList();
                        _latestChoicesLogicalTaskId = activePlannerTaskIdThisTurn;
                        turnLastChoices = _latestChoices;
                    },
                    onDispatchBackground: work => dispatchedWork = work,
                    activePlannerTasks: activePlannerSnapshot,
                    resolveLogicalTaskId: (action, targetTaskId, effectiveGoal) =>
                    {
                        if (!string.Equals(action, "call_planner", StringComparison.OrdinalIgnoreCase))
                            return taskId;

                        var compactGoal = effectiveGoal.Length > 80 ? effectiveGoal[..80] + "…" : effectiveGoal;
                        if (!string.IsNullOrWhiteSpace(targetTaskId) &&
                            _activePlannerTasks.TryGetValue(targetTaskId, out var existing))
                        {
                            Interlocked.Increment(ref existing.RunningCount);
                            existing.LastUpdatedAt = DateTimeOffset.UtcNow;
                            activePlannerTaskIdThisTurn = existing.LogicalTaskId;
                            return existing.LogicalTaskId;
                        }

                        var created = new ActivePlannerTaskState
                        {
                            LogicalTaskId = taskId,
                            Goal = compactGoal,
                            RunningCount = 1,
                            LastUpdatedAt = DateTimeOffset.UtcNow
                        };
                        _activePlannerTasks[taskId] = created;
                        activePlannerTaskIdThisTurn = taskId;
                        return taskId;
                    },
                    onPlannerTaskFinished: (logicalId, isCompleted) =>
                    {
                        Interlocked.Exchange(ref plannerFinishReported, 1);
                        if (_activePlannerTasks.TryGetValue(logicalId, out var state))
                        {
                            var remaining = Interlocked.Decrement(ref state.RunningCount);
                            if (remaining < 0)
                            {
                                Interlocked.Exchange(ref state.RunningCount, 0);
                                remaining = 0;
                            }
                            state.LastUpdatedAt = DateTimeOffset.UtcNow;
                            if (isCompleted && remaining <= 0)
                            {
                                _activePlannerTasks.TryRemove(logicalId, out _);
                                _guard.ClearTaskApprovals(logicalId);
                                RemovePinnedConfirmation(logicalId);
                            }
                            else if (!isCompleted && remaining <= 0)
                            {
                                UpsertPinnedConfirmation(logicalId, state.Goal, turnLastSummary, turnLastChoices);
                            }
                        }
                    },
                    onSingleSkillFinished: singleTaskId => _guard.ClearTaskApprovals(singleTaskId),
                    onClosePlannerTask: ClosePlannerTask,
                    preferredLogicalTaskId: replyToLogicalTaskId);

                if (dispatchedWork is null)
                {
                    if (run.MainState == "Running") run.MainState = "Done";
                    if (run.PlannerState == "Running") run.PlannerState = "Done";
                    if (run.WorkerState == "Running") run.WorkerState = "Done";
                    run.CurrentStep = AppMessages.Localize("StepCompleted", "Completed");
                    AgentsChanged?.Invoke();
                }
                else
                {
                    run.MainState = "Done";
                    AgentsChanged?.Invoke();
                }

                replyReady.TrySetResult(true);

                if (string.IsNullOrWhiteSpace(pendingText))
                {
                    RevealReply();
                }
                else if (string.IsNullOrWhiteSpace(directTtsText))
                {
                    _tts.EnqueueSummarySpeech(AppMessages.Localize("AlertTitleReply", "AI Secretary Reply"), pendingText, RevealReply);
                }
                else if (!_tts.IsEnabled || _tts.ChatMode == "notify_only")
                {
                    RevealReply();
                }
            }
            catch (OperationCanceledException)
            {
                if (run.MainState == "Running") run.MainState = "Stopped";
                if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                run.CurrentStep = AppMessages.Localize("StepStopped", "Stopped");
                AgentsChanged?.Invoke();

                assistantMsg.IsStreaming = false;
                assistantMsg.StatusText = null;
                assistantMsg.Content = string.IsNullOrWhiteSpace(assistantMsg.Content)
                    ? AppMessages.Localize("TurnStopped", "(Stopped)")
                    : assistantMsg.Content + "\n\n" + AppMessages.Localize("TurnStoppedSuffix", "(Stopped)");
                await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Task failed");
                if (run.MainState == "Running") run.MainState = "Stopped";
                if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                run.CurrentStep = AppMessages.LocalizeFormat("StepError", "Error: {0}", ex.Message);
                AgentsChanged?.Invoke();

                assistantMsg.IsStreaming = false;
                assistantMsg.StatusText = null;
                assistantMsg.Content = $"{AppMessages.ErrorPrefix}: {ex.Message}";
                await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                StateChanged?.Invoke();
            }
            finally
            {
                if (turnGateAcquired)
                    _mainTurnGate.Release();

                if (dispatchedWork is null)
                {
                    ReleasePlannerRunningIfUnreported();
                    _runningTasks.TryRemove(taskId, out _);
                    linked.Dispose();
                    AgentsChanged?.Invoke();
                }
            }

            if (dispatchedWork is not null)
            {
                _ = Task.Run(async () =>
                {
                    string? bgDirectTtsText = null;
                    string? bgReportText = null;
                    List<MailSourceRef>? bgSources = null;
                    IReadOnlyList<string>? bgChoices = null;

                    try
                    {
                        await dispatchedWork(
                            linked.Token,
                            (text, sources) =>
                            {
                                bgReportText = text;
                                bgSources = sources;
                                turnLastSummary = text;
                                return Task.CompletedTask;
                            },
                            ttsText => bgDirectTtsText = ttsText,
                            choices =>
                            {
                                bgChoices = choices.Take(4).ToList();
                                turnLastChoices = bgChoices;
                            });

                        await _mainTurnGate.WaitAsync(linked.Token);
                        try
                        {
                            if (run.PlannerState == "Running") run.PlannerState = "Done";
                            if (run.WorkerState == "Running") run.WorkerState = "Done";
                            run.CurrentStep = AppMessages.Localize("StepCompleted", "Completed");
                            AgentsChanged?.Invoke();

                            if (!string.IsNullOrWhiteSpace(bgReportText))
                            {
                                var reportMsg = new ChatMessageRecord
                                {
                                    ConversationId = ConversationId,
                                    Role = "assistant",
                                    Content = bgReportText,
                                    Sources = bgSources ?? new List<MailSourceRef>(),
                                    IsStreaming = false,
                                    TaskId = taskId,
                                    CreatedAt = DateTimeOffset.UtcNow
                                };

                                if (bgChoices is { Count: > 0 })
                                {
                                    _latestChoicesMsgId = reportMsg.Id;
                                    _latestChoices = bgChoices.ToList();
                                    _latestChoicesLogicalTaskId = activePlannerTaskIdThisTurn;
                                }

                                var reportReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                int reportRevealed = 0;
                                void RevealReport()
                                {
                                    if (Interlocked.Exchange(ref reportRevealed, 1) != 0) return;
                                    _ = _store.UpsertChatMessageAsync(reportMsg, CancellationToken.None);
                                    StateChanged?.Invoke();
                                    ProactiveAlert?.Invoke(AppMessages.Localize("AlertTitleReport", "AI Secretary Report"), bgReportText);
                                }

                                if (!string.IsNullOrWhiteSpace(bgDirectTtsText) && _tts.IsEnabled && _tts.ChatMode != "notify_only")
                                {
                                    _tts.EnqueueJapaneseSpeech(bgDirectTtsText, onBeforePlay: () =>
                                    {
                                        reportReady.Task.GetAwaiter().GetResult();
                                        RevealReport();
                                    });
                                    reportReady.TrySetResult(true);
                                }
                                else
                                {
                                    reportReady.TrySetResult(true);
                                    if (string.IsNullOrWhiteSpace(bgDirectTtsText))
                                        _tts.EnqueueSummarySpeech(AppMessages.Localize("AlertTitleReport", "AI Secretary Report"), bgReportText, RevealReport);
                                    else
                                        RevealReport();
                                }
                            }
                        }
                        finally
                        {
                            _mainTurnGate.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                        if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                        run.CurrentStep = AppMessages.Localize("StepStopped", "Stopped");
                        AgentsChanged?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background planner/worker task failed");
                        bool acquired = false;
                        try
                        {
                            await _mainTurnGate.WaitAsync(appCt);
                            acquired = true;
                            if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                            if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                            run.CurrentStep = AppMessages.LocalizeFormat("StepError", "Error: {0}", ex.Message);
                            AgentsChanged?.Invoke();

                            var errMsg = new ChatMessageRecord
                            {
                                ConversationId = ConversationId,
                                Role = "assistant",
                                Content = AppMessages.LocalizeFormat("BgTaskFailed", "Background task failed: {0}", ex.Message),
                                IsStreaming = false,
                                TaskId = taskId,
                                CreatedAt = DateTimeOffset.UtcNow
                            };
                            await _store.UpsertChatMessageAsync(errMsg, CancellationToken.None);
                            StateChanged?.Invoke();
                        }
                        catch { }
                        finally
                        {
                            if (acquired) _mainTurnGate.Release();
                        }
                    }
                    finally
                    {
                        ReleasePlannerRunningIfUnreported();
                        _runningTasks.TryRemove(taskId, out _);
                        linked.Dispose();
                        AgentsChanged?.Invoke();
                    }
                });
            }
        });
    }

    public void StopTask(string taskId)
    {
        if (_runningTasks.TryGetValue(taskId, out var cts))
            cts.Cancel();
    }

    public void StopAllTasks()
    {
        foreach (var cts in _runningTasks.Values)
            cts.Cancel();
    }

    public void ClosePlannerTask(string logicalId)
    {
        if (string.IsNullOrWhiteSpace(logicalId)) return;
        if (_activePlannerTasks.TryGetValue(logicalId, out var state) && Volatile.Read(ref state.RunningCount) <= 0)
        {
            _activePlannerTasks.TryRemove(logicalId, out _);
            _guard.ClearTaskApprovals(logicalId);
            RemovePinnedConfirmation(logicalId);
        }
    }

    private void CleanupExpiredPlannerTasks()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var kv in _activePlannerTasks)
        {
            if (Volatile.Read(ref kv.Value.RunningCount) <= 0 && kv.Value.LastUpdatedAt < cutoff)
            {
                if (_activePlannerTasks.TryRemove(kv.Key, out _))
                {
                    _guard.ClearTaskApprovals(kv.Key);
                    RemovePinnedConfirmation(kv.Key);
                }
            }
        }
    }

    public async Task KillMainAgentAsync()
    {
        StopAllTasks();
        foreach (var id in _activePlannerTasks.Keys.ToList())
        {
            _guard.ClearTaskApprovals(id);
            RemovePinnedConfirmation(id);
        }
        _activePlannerTasks.Clear();
        foreach (var run in _agentRuns.Values)
        {
            if (run.MainState == "Running") run.MainState = "Stopped";
            if (run.PlannerState == "Running") run.PlannerState = "Stopped";
            if (run.WorkerState == "Running") run.WorkerState = "Stopped";
            if (run.CurrentStep != AppMessages.Localize("StepCompleted", "Completed")) run.CurrentStep = AppMessages.Localize("StepKilledByUser", "Cleared by /kill magent");
        }
        AgentsChanged?.Invoke();

        var msgs = await _store.GetChatMessagesAsync(ConversationId, 20);
        foreach (var m in msgs.Where(x => x.IsStreaming))
        {
            m.IsStreaming = false;
            m.StatusText = null;
            if (string.IsNullOrWhiteSpace(m.Content))
                m.Content = AppMessages.Localize("KillCommandDone", "(Executed `/kill magent`; cleared stuck agent state)");
            await _store.UpsertChatMessageAsync(m, CancellationToken.None);
        }
        StateChanged?.Invoke();
    }

    public async Task SnoozeNotificationAsync(string notificationId)
    {
        await _store.UpdateNotificationStatusAsync(notificationId, "Snoozed", DateTimeOffset.UtcNow.AddMinutes(15));
        StateChanged?.Invoke();
    }

    public async Task DraftReplyFromNotificationAsync(NotificationActionPayload n)
    {
        await _store.UpdateNotificationStatusAsync(n.NotificationId, "Handled", null);
        StateChanged?.Invoke();
        await SendUserPromptAsync(AppMessages.LocalizeFormat("DraftReplyPrompt", "Help me draft a reply to '{0}'", n.Title), targetMailId: n.MailId);
    }

    public IReadOnlyList<PinnedReminderItem> GetPinnedReminders()
    {
        lock (_pinnedLock)
            return _pinnedReminders.ToList();
    }

    public async Task<IReadOnlyList<ReminderRecord>> GetPendingSchedulesAsync()
        => await _store.GetPendingRemindersAsync(_appCts.Token);

    public async Task DeleteScheduleAsync(string reminderId)
    {
        await _store.DeleteReminderAsync(reminderId, _appCts.Token);
        AgentsChanged?.Invoke();
    }

    public async Task<IReadOnlyList<CalendarEventRecord>> GetCalendarEventsAsync()
        => await _store.GetCalendarEventsAsync(ConversationId, null, null, _appCts.Token);

    public async Task DeleteCalendarEventAsync(string eventId)
    {
        await _store.DeleteCalendarEventAsync(eventId, _appCts.Token);
        AgentsChanged?.Invoke();
    }

    private void UpsertPinnedConfirmation(string logicalTaskId, string goal, string? replySummary, IReadOnlyList<string>? choices)
    {
        var id = $"confirm:{logicalTaskId}";
        var cleanTitle = (goal ?? "").ReplaceLineEndings(" ").Trim();
        if (cleanTitle.Length > 28)
            cleanTitle = cleanTitle[..28] + "…";
        var cleanSummary = (replySummary ?? "").Trim();
        if (cleanSummary.Length > 120)
            cleanSummary = cleanSummary[..120] + "…";
        var item = new PinnedReminderItem(
            id,
            cleanTitle,
            cleanSummary,
            null,
            null,
            0,
            PinnedItemKind.Confirmation,
            logicalTaskId,
            choices is { Count: > 0 } ? choices.ToList() : null);

        lock (_pinnedLock)
        {
            var idx = _pinnedReminders.FindIndex(p => p.Id == id);
            if (idx >= 0)
                _pinnedReminders[idx] = item;
            else
                _pinnedReminders.Add(item);
        }
        PinnedRemindersChanged?.Invoke();
    }

    private void RemovePinnedConfirmation(string logicalTaskId)
    {
        var id = $"confirm:{logicalTaskId}";
        bool removed = false;
        lock (_pinnedLock)
        {
            var idx = _pinnedReminders.FindIndex(p => p.Id == id);
            if (idx >= 0)
            {
                _pinnedReminders.RemoveAt(idx);
                removed = true;
            }
        }
        if (removed)
            PinnedRemindersChanged?.Invoke();
    }

    private void AddPinnedReminder(PinnedReminderItem item)
    {
        lock (_pinnedLock)
        {
            if (_pinnedReminders.Any(p => p.Id == item.Id))
                return;
            _pinnedReminders.Add(item);
        }
        if (item.ReRemindMinutes > 0)
            _pinnedNextRemindAt[item.Id] = DateTimeOffset.UtcNow.AddMinutes(item.ReRemindMinutes);
        _ = SavePinnedRemindersAsync();
        PinnedRemindersChanged?.Invoke();
    }

    public async Task DismissPinnedReminderAsync(string id)
    {
        PinnedReminderItem? removed = null;
        lock (_pinnedLock)
        {
            var idx = _pinnedReminders.FindIndex(p => p.Id == id);
            if (idx >= 0)
            {
                removed = _pinnedReminders[idx];
                _pinnedReminders.RemoveAt(idx);
            }
        }
        _pinnedNextRemindAt.TryRemove(id, out _);
        await SavePinnedRemindersAsync();
        PinnedRemindersChanged?.Invoke();

        if (removed?.MailNotification is not null)
        {
            await _store.UpdateNotificationStatusAsync(removed.MailNotification.NotificationId, "Handled", null);
        }
        else if (removed?.Kind == PinnedItemKind.Confirmation && !string.IsNullOrWhiteSpace(removed.LogicalTaskId))
        {
            ClosePlannerTask(removed.LogicalTaskId);
        }
    }

    private async Task<string> DismissPinnedByKeywordAsync(string keyword)
    {
        var kw = keyword?.Trim() ?? "";
        List<PinnedReminderItem> targets;
        lock (_pinnedLock)
        {
            if (string.Equals(kw, "all", StringComparison.OrdinalIgnoreCase) || kw == "全部")
                targets = _pinnedReminders.ToList();
            else
                targets = _pinnedReminders.Where(p =>
                    p.Id.Equals(kw, StringComparison.OrdinalIgnoreCase) ||
                    p.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    p.Summary.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (targets.Count == 0)
            return string.IsNullOrWhiteSpace(kw) ? AppMessages.Localize("NoPinnedReminders", "No pinned reminders right now.") : AppMessages.LocalizeFormat("PinnedNotFound", "No pinned reminder matching '{0}'.", kw);

        foreach (var t in targets)
            await DismissPinnedReminderAsync(t.Id);

        var names = string.Join("、", targets.Select(t => $"「{t.Title}」"));
        return AppMessages.LocalizeFormat("PinnedDismissed", "Dismissed pinned reminder(s): {0}.", names);
    }

    private async Task SavePinnedRemindersAsync()
    {
        try
        {
            List<PinnedReminderItem> snapshot;
            lock (_pinnedLock)
                snapshot = _pinnedReminders.Where(p => p.Kind != PinnedItemKind.Confirmation).ToList();
            var json = JsonSerializer.Serialize(snapshot);
            await _store.SetSyncValueAsync("pinned_reminders", json, CancellationToken.None);

            var nextAtSnapshot = _pinnedNextRemindAt.ToDictionary(kv => kv.Key, kv => kv.Value);
            await _store.SetSyncValueAsync("pinned_next_remind_at", JsonSerializer.Serialize(nextAtSnapshot), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist pinned reminders");
        }
    }

    public async Task PushProactiveNotificationAsync(string convId, string eventId, MailItem mail, string title, string summary, CancellationToken ct, int reRemindMinutes = 15, List<NotificationButtonOption>? actions = null)
    {
        var finalActions = actions is not null ? new List<NotificationButtonOption>(actions) : new List<NotificationButtonOption>();
        var otpBtn = finalActions.FirstOrDefault(a => a.Type is "fill_otp" or "copy_otp");
        var otp = otpBtn?.Prompt;
        if (!string.IsNullOrEmpty(otp))
        {
            _skills.LastObservedOtpCode = otp;
            _skills.LastObservedOtpHint = title;
            _otpBurstUntilUtc = DateTimeOffset.MinValue;
            if (_activeOtpPatternKey is { } hitKey)
            {
                _activeOtpPatternKey = null;
                _ = _pagePredictor.RecordOutcomeAsync(hitKey, hit: true, estimatedTokensSaved: 90, CancellationToken.None);
            }

            if (_activity.HasMatchingVerificationWindow(title, mail.SenderName))
            {
                var evCtx = new PendingMailNotificationContext(convId, eventId, mail, title, summary, reRemindMinutes, finalActions, otp);
                _skills.CurrentEventNotification = evCtx;
                try
                {
                    await _planner.ExecuteGoalAsync(
                        convId,
                        taskId: $"ev-{eventId}",
                        goal: "event:mail_otp_received",
                        userPrompt: "event:mail_otp_received matching_verification_window=true. Check user_pref: if user prefers auto-filling OTP without asking, run fill_otp then push_notification(include_fill_button=false); otherwise run push_notification(include_fill_button=true).",
                        targetMailId: mail.Id,
                        confirmAsync: (_, _) => Task.FromResult(true),
                        onStatusAsync: _ => Task.CompletedTask,
                        onReplyAsync: (_, _) => Task.CompletedTask,
                        tier: ModelTier.Simple,
                        ct: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Event workflow failed for {EventId}, falling back to notification", eventId);
                }
                finally
                {
                    _skills.CurrentEventNotification = null;
                }

                if (!evCtx.Published)
                {
                    await PersistNotificationAsync(convId, eventId, mail, title, summary, reRemindMinutes, finalActions, ct);
                }
                return;
            }
        }

        await PersistNotificationAsync(convId, eventId, mail, title, summary, reRemindMinutes, finalActions, ct);
    }

    private async Task PersistNotificationAsync(
        string convId,
        string eventId,
        MailItem? mail,
        string title,
        string summary,
        int reRemindMinutes,
        List<NotificationButtonOption>? actions,
        CancellationToken ct)
    {
        var finalActions = actions ?? new List<NotificationButtonOption>();
        var payload = new NotificationActionPayload(Guid.NewGuid().ToString("N"), eventId, mail?.Id ?? "", title, summary, "Unread", finalActions.Count > 0 ? finalActions : null);
        var created = await _store.TryCreateNotificationAsync(convId, payload, ct);
        if (!created) return;

        var msg = new ChatMessageRecord
        {
            ConversationId = convId,
            Role = "notification",
            Content = summary,
            Notification = payload,
            Sources = mail is null ? new List<MailSourceRef>() : new List<MailSourceRef> { new(mail.Id, mail.Subject, mail.SenderName, mail.Subject, mail.ReceivedAt) }
        };
        await _store.UpsertChatMessageAsync(msg, ct);
        AddPinnedReminder(new PinnedReminderItem(payload.NotificationId, title, summary, mail?.Id, payload, reRemindMinutes));
        StateChanged?.Invoke();
        RaiseProactiveAlert(title, summary);
    }

    private async Task PushPetMessageAsync(string content, CancellationToken ct = default)
    {
        var msg = new ChatMessageRecord
        {
            ConversationId = ConversationId,
            Role = "assistant",
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _store.UpsertChatMessageAsync(msg, ct);
        StateChanged?.Invoke();
        RaiseProactiveAlert(AppMessages.Localize("AlertTitleDefault", "AI Secretary"), content);
    }

    private void RaiseProactiveAlert(string title, string content)
    {
        _tts.EnqueueSummarySpeech(title, content, () => ProactiveAlert?.Invoke(title, content));
    }

    private async Task RunSchedulerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool inOtpBurst = DateTimeOffset.UtcNow < _otpBurstUntilUtc;
            var tickSeconds = inOtpBurst ? 6 : Math.Clamp(_settings.Current.Scheduler.TickSeconds, 5, 300);
            try
            {
                CleanupExpiredPlannerTasks();
                await WakeDueSnoozedAsync(ct);
                await WakeDueRemindersAsync(ct);
                await WakeDuePinnedReRemindersAsync(ct);
                if (inOtpBurst)
                {
                    await _mailSync.SyncOnceAsync(
                        ConversationId,
                        "all",
                        (eventId, mail, title, summary, mins, actions) =>
                            PushProactiveNotificationAsync(ConversationId, eventId, mail, title, summary, ct, mins, actions),
                        ct);
                }
                else if (_activeOtpPatternKey is { } expiredKey)
                {
                    _activeOtpPatternKey = null;
                    await _pagePredictor.RecordOutcomeAsync(expiredKey, hit: false, estimatedTokensSaved: 0, ct);
                }
                await _watchers.TickDueWatchersAsync(async (w, alertMsg) =>
                {
                    WatcherTriggered?.Invoke(w, alertMsg);
                    await PushPetMessageAsync($"🔔 {alertMsg}", ct);
                }, ct);
                await TryProactiveStyleChatAsync(ct);
                await _optimizer.MaybeRunOfflineOptimizationAsync(ConversationId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduler tick error");
            }

            await Task.Delay(TimeSpan.FromSeconds(tickSeconds), ct);
        }
    }

    private async Task WakeDueSnoozedAsync(CancellationToken ct)
    {
        var due = await _store.GetDueSnoozedNotificationsAsync(ct);
        foreach (var item in due)
        {
            await _store.UpdateNotificationStatusAsync(item.NotificationId, "Unread", null, ct);
            var msg = new ChatMessageRecord
            {
                ConversationId = ConversationId,
                Role = "notification",
                Content = item.Summary,
                Notification = item with { Status = "Unread" }
            };
            await _store.UpsertChatMessageAsync(msg, ct);
            AddPinnedReminder(new PinnedReminderItem(item.NotificationId, item.Title, item.Summary, item.MailId, item with { Status = "Unread" }));
            StateChanged?.Invoke();
            RaiseProactiveAlert(item.Title, item.Summary);
        }
    }

    private async Task WakeDueRemindersAsync(CancellationToken ct)
    {
        var due = await _store.GetDueRemindersAsync(ct);
        if (due.Count == 0) return;

        var nowLocal = DateTimeOffset.Now;
        var todayYmd = nowLocal.ToString("yyyy-MM-dd");
        bool? isNonWorkingDay = null;
        bool dnd = IsDndActive();

        foreach (var r in due)
        {
            bool hasRRule = RecurrenceHelper.IsRecurring(r.RecurrenceRule);
            bool isRecurring = hasRRule || r.IntervalMinutes > 0;
            bool workdaysOnly = r.WorkdaysOnly || RecurrenceHelper.IsWorkdaysRule(r.RecurrenceRule);

            async Task AdvanceOrFinishAsync()
            {
                if (hasRRule)
                {
                    var nextAt = RecurrenceHelper.GetNextOccurrenceAfter(r.RemindAt, r.RecurrenceRule);
                    if (nextAt.HasValue)
                        await _store.RescheduleReminderAsync(r.ReminderId, nextAt.Value, ct);
                    else
                        await _store.MarkReminderTriggeredAsync(r.ReminderId, ct);
                }
                else if (r.IntervalMinutes > 0)
                {
                    await _store.RescheduleReminderAsync(r.ReminderId, DateTimeOffset.UtcNow.AddMinutes(r.IntervalMinutes), ct);
                }
                else
                {
                    await _store.MarkReminderTriggeredAsync(r.ReminderId, ct);
                }
            }

            bool isSkippedDate = false;
            if (!string.IsNullOrWhiteSpace(r.ExceptionDatesJson) && r.ExceptionDatesJson != "[]")
            {
                try
                {
                    var exDates = JsonSerializer.Deserialize<List<string>>(r.ExceptionDatesJson);
                    if (exDates is not null && exDates.Contains(todayYmd, StringComparer.Ordinal))
                        isSkippedDate = true;
                }
                catch { }
            }

            if (isSkippedDate)
            {
                await AdvanceOrFinishAsync();
                continue;
            }

            if (workdaysOnly)
            {
                isNonWorkingDay ??= await HolidayHelper.IsNonWorkingDayAsync(_store, ConversationId, nowLocal, ct);
                if (isNonWorkingDay.Value)
                {
                    await AdvanceOrFinishAsync();
                    continue;
                }
            }

            if (dnd)
            {
                if (isRecurring)
                {
                    await AdvanceOrFinishAsync();
                    continue;
                }
                if (!_dndAllowUrgent)
                {
                    // Defer one-off reminder until DND ends
                    continue;
                }
            }

            await AdvanceOrFinishAsync();

            if (!string.IsNullOrWhiteSpace(r.SkillName))
            {
                _ = Task.Run(() => RunScheduledSkillSilentlyAsync(r, _appCts.Token), _appCts.Token);
            }
            else
            {
                var reRemindMins = r.IntervalMinutes > 0 ? Math.Clamp(r.IntervalMinutes, 3, 60) : 10;
                AddPinnedReminder(new PinnedReminderItem(r.ReminderId, r.Title, r.Message, r.RelatedMailId, null, reRemindMins, PinnedItemKind.Schedule));
                await PushPetMessageAsync($"⏰ {r.Title}：{r.Message}", ct);
            }
        }
    }

    private async Task WakeDuePinnedReRemindersAsync(CancellationToken ct)
    {
        if (IsDndActive()) return;
        var now = DateTimeOffset.UtcNow;
        List<PinnedReminderItem> snapshot;
        lock (_pinnedLock)
            snapshot = _pinnedReminders.ToList();

        var dueItems = new List<PinnedReminderItem>();
        foreach (var item in snapshot)
        {
            if (item.ReRemindMinutes <= 0) continue;
            if (!_pinnedNextRemindAt.TryGetValue(item.Id, out var nextAt) || nextAt > now)
                continue;

            _pinnedNextRemindAt[item.Id] = now.AddMinutes(item.ReRemindMinutes);
            dueItems.Add(item);
        }

        if (dueItems.Count == 0) return;

        await SavePinnedRemindersAsync();
        if (HasRunningTasks) return;

        if (dueItems.Count == 1)
        {
            var item = dueItems[0];
            await PushPetMessageAsync(await AppMessages.LocalizeFormatAsync("ReRemindSingle", "🔔 Unacknowledged reminder '{0}': {1}", item.Title, item.Summary), ct);
        }
        else
        {
            var lines = dueItems.Select(i => $"- **{i.Title}**：{i.Summary}");
            await PushPetMessageAsync(await AppMessages.LocalizeFormatAsync("ReRemindMulti", "🔔 Unacknowledged reminders ({0}):\n{1}", dueItems.Count, string.Join("\n", lines)), ct);
        }
    }

    private int _nextProactiveIdleMinutes = -1;

    private int RollNextProactiveMinutes()
    {
        var sched = _settings.Current.Scheduler;
        int min = Math.Clamp(sched.ProactiveIdleMinMinutes > 0 ? sched.ProactiveIdleMinMinutes : sched.ProactiveIdleMinutes, 5, 180);
        int max = Math.Clamp(sched.ProactiveIdleMaxMinutes > 0 ? sched.ProactiveIdleMaxMinutes : sched.ProactiveIdleMinutes, min, 180);
        _nextProactiveIdleMinutes = min == max ? min : Random.Shared.Next(min, max + 1);
        return _nextProactiveIdleMinutes;
    }

    public (bool Enabled, int MinMinutes, int MaxMinutes) GetProactiveChatSettings()
    {
        var sched = _settings.Current.Scheduler;
        int min = Math.Clamp(sched.ProactiveIdleMinMinutes > 0 ? sched.ProactiveIdleMinMinutes : sched.ProactiveIdleMinutes, 5, 180);
        int max = Math.Clamp(sched.ProactiveIdleMaxMinutes > 0 ? sched.ProactiveIdleMaxMinutes : sched.ProactiveIdleMinutes, min, 180);
        return (sched.ProactiveEnabledWithStyle, min, max);
    }

    public async Task SaveProactiveChatSettingsAsync(bool enabled, int minMinutes, int maxMinutes)
    {
        int min = Math.Clamp(minMinutes, 5, 180);
        int max = Math.Clamp(maxMinutes, min, 180);
        await _settings.SaveOverridesAsync(s =>
        {
            s.Scheduler.ProactiveEnabledWithStyle = enabled;
            s.Scheduler.ProactiveIdleMinMinutes = min;
            s.Scheduler.ProactiveIdleMaxMinutes = max;
            s.Scheduler.ProactiveIdleMinutes = (min + max) / 2;
        });
        RollNextProactiveMinutes();
    }

    private async Task TryProactiveStyleChatAsync(CancellationToken ct)
    {
        var sched = _settings.Current.Scheduler;
        if (IsDndActive() || HasRunningTasks || !sched.ProactiveEnabledWithStyle)
            return;

        if (_nextProactiveIdleMinutes <= 0)
            RollNextProactiveMinutes();

        var lastTouch = _lastUserActivityUtc > _lastProactiveChatUtc ? _lastUserActivityUtc : _lastProactiveChatUtc;
        if (DateTimeOffset.UtcNow - lastTouch < TimeSpan.FromMinutes(_nextProactiveIdleMinutes))
            return;

        _lastProactiveChatUtc = DateTimeOffset.UtcNow;
        RollNextProactiveMinutes();

        List<string> pinnedTitles;
        lock (_pinnedLock)
            pinnedTitles = _pinnedReminders.Select(p => p.Title).ToList();

        var (reply, ttsText) = await _mainAgent.GenerateProactiveStyleLineAsync(ConversationId, pinnedTitles, ct);
        if (string.IsNullOrWhiteSpace(reply))
            return;

        var msg = new ChatMessageRecord
        {
            ConversationId = ConversationId,
            Role = "assistant",
            Content = reply,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _store.UpsertChatMessageAsync(msg, ct);
        void RevealProactive()
        {
            StateChanged?.Invoke();
            ProactiveAlert?.Invoke(AppMessages.Localize("AlertTitleDefault", "AI Secretary"), reply);
        }
        if (!string.IsNullOrWhiteSpace(ttsText))
            _tts.EnqueueJapaneseSpeech(ttsText, RevealProactive);
        else
            _tts.EnqueueSummarySpeech(AppMessages.Localize("AlertTitleDefault", "AI Secretary"), reply, RevealProactive);
    }

    private async Task RunScheduledSkillSilentlyAsync(ReminderRecord r, CancellationToken ct)
    {
        try
        {
            JsonElement args = default;
            if (!string.IsNullOrWhiteSpace(r.SkillArgsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(r.SkillArgsJson);
                    args = doc.RootElement.Clone();
                }
                catch
                {
                    args = default;
                }
            }

            var isMailPoller = r.SkillName!.Equals("manage_mail", StringComparison.OrdinalIgnoreCase)
                || r.SkillName!.Equals("read_inbox", StringComparison.OrdinalIgnoreCase)
                || r.SkillName!.Equals("read_gmail", StringComparison.OrdinalIgnoreCase)
                || r.SkillName!.Equals("sync_mail", StringComparison.OrdinalIgnoreCase);

            var prompt = string.IsNullOrWhiteSpace(r.Message)
                ? $"Scheduled background run: {r.Title}\n(Check <user_preferences> to decide whether to notify the user; if not needed, reply ONLY with NO_NOTIFY)"
                : $"Scheduled background run: {r.Title}\nRule: {r.Message}\n(Check <user_preferences> to decide whether to notify the user; if not needed, reply ONLY with NO_NOTIFY)";

            if (isMailPoller)
            {
                await _worker.RunStepAsync(
                    ConversationId,
                    prompt,
                    r.SkillName!,
                    args,
                    r.RelatedMailId,
                    confirmAsync: (_, _) => Task.FromResult(false),
                    onStatusAsync: _ => Task.CompletedTask,
                    new List<MailSourceRef>(),
                    new List<string>(),
                    new List<string>(),
                    ct);
                return;
            }

            var (ok, reply, _) = await _worker.ExecuteSingleSkillAsync(
                ConversationId,
                prompt,
                r.SkillName!,
                args,
                r.RelatedMailId,
                confirmAsync: (_, _) => Task.FromResult(false),
                onStatusAsync: _ => Task.CompletedTask,
                ModelTier.Simple,
                ct);

            if (ok && !string.IsNullOrWhiteSpace(reply)
                && !reply.Trim().Equals("NO_NOTIFY", StringComparison.OrdinalIgnoreCase))
            {
                await PushPetMessageAsync($"⏰ {r.Title}：\n{reply.Trim()}", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Silent scheduled skill {Skill} failed", r.SkillName);
        }
    }

    public Task<int> ClearImageAttachmentsInHistoryAsync(CancellationToken ct = default)
        => _store.ClearImageAttachmentsInHistoryAsync(ct);

    public Task<int> ClearChatMessagesAsync(CancellationToken ct = default)
        => _store.ClearChatMessagesAsync(ct);

    public Task<(bool Ok, string Message)> OpenDedicatedBrowserAsync(string? startUrl = null, CancellationToken ct = default)
        => _browserHost.OpenLoginBrowserAsync(startUrl, ct);

    public ExternalWindowTarget? GetLastExternalWindow() => _activity.GetLastExternalTarget();

    public Task HandleBrowserPageSignalAsync(BrowserPageSignal signal, CancellationToken ct = default)
        => _activity.HandleBrowserPageSignalAsync(signal, ct);

    public async Task<(bool Ok, string Message)> FillOtpInExternalWindowAsync(
        string code,
        string? serviceHint = null,
        CancellationToken ct = default)
    {
        var extRes = await _browserBridge.RequestFillOtpAsync(code, TimeSpan.FromMilliseconds(1500), ct);
        if (extRes is { Filled: true })
        {
            var title = string.IsNullOrWhiteSpace(extRes.Title) ? AppMessages.Localize("BrowserTabDefault", "Browser tab") : extRes.Title.Trim();
            if (title.Length > 24) title = title[..24] + "…";
            return (true, AppMessages.LocalizeFormat("OtpExtensionFilled", "Filled verification code {0} into '{1}' via browser extension ({2}, not auto-submitted).", code, title, extRes.Mode ?? "dom"));
        }

        return _activity.TryFillOtpInExternalWindow(code, serviceHint);
    }

    public Task<BranchTelemetryStats> GetBranchTelemetryAsync(CancellationToken ct = default)
        => _store.GetBranchTelemetryAsync(ct);

    public object GetWorkflowTuningInfo() => new
    {
        overlapThreshold = Math.Round(_cacheThreshold(), 2),
        promoteMinCounter = _workflowCache.CurrentPromoteMinCounter,
        lastNote = _workflowCache.LastOptimizationNote
    };

    private double _cacheThreshold() => _workflowCache.CurrentOverlapThreshold;

    public Task<(bool Ok, string Note)> RunTelemetryOptimizationAsync(CancellationToken ct = default)
        => _optimizer.RunOptimizationOnceAsync(ConversationId, ct);

    public Task<List<StandingWatcherRecord>> GetWatchersAsync(CancellationToken ct = default)
        => _store.GetWatchersAsync(null, ct);

    public Task<bool> DeleteWatcherAsync(string watcherId, CancellationToken ct = default)
        => _store.DeleteWatcherAsync(watcherId, ct);

    public async Task<bool> SetWatcherStatusAsync(string watcherId, string status, CancellationToken ct = default)
    {
        var all = await _store.GetWatchersAsync(null, ct);
        var w = all.FirstOrDefault(x => x.WatcherId == watcherId);
        if (w is null) return false;
        await _store.UpdateWatcherStateAsync(watcherId, w.LastSnapshot, w.LastCheckedAt, DateTimeOffset.UtcNow, 0, status, ct);
        return true;
    }

    public async Task<string> SuggestFormFillAsync(BrowserFormContext form, CancellationToken ct = default)
    {
        var prompt = SystemAutomationPrompts.BuildBrowserFormSuggestPrompt(form);
        await SendUserPromptAsync(prompt);
        var msgs = await _store.GetChatMessagesAsync(ConversationId, 5, ct);
        return msgs.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
    }

    public void Dispose()
    {
        _appCts.Cancel();
        _activity.Dispose();
        _browserHost.Dispose();
    }
}
