using System.Collections.Concurrent;
using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Skills;

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
    private readonly SkillRegistry _skillRegistry;
    private readonly CustomSkillService _customSkills;
    private readonly SandboxService _sandbox;
    private readonly GuardService _guard;
    private readonly StyleService _styles;
    private readonly TtsService _tts;
    private readonly UiLocalizer _localizer;
    private readonly AppSettingsService _settings;
    private readonly InteractionGateway _interaction;
    private readonly ActivityMonitorService _activity;
    private readonly StateMachineAgent _stateMachine;
    private readonly BrowserHostService _browserHost;
    private readonly BrowserFormBridgeService _browserBridge;
    private readonly StandingWatcherService _watchers;
    private readonly WorkflowCacheService _workflowCache;
    private readonly WorkflowOptimizerAgent _optimizer;
    private readonly AgentBlackboard _blackboard;
    private readonly IConfiguration _config;
    private readonly ILogger<SecretaryController> _logger;

    private readonly CancellationTokenSource _appCts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();
    private readonly ConcurrentDictionary<string, ActiveAgentRun> _agentRuns = new();
    private readonly List<PinnedReminderItem> _pinnedReminders = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pinnedNextRemindAt = new();
    private readonly object _pinnedLock = new();
    private readonly object _choicesLock = new();
    // Lock hierarchy (STRICT): _userTurnGate -> _presentGate (never reverse!).
    // _userTurnGate serializes user routing turns (Q2 waits for Q1 routing/ack); may span routing LLM, released once background work is dispatched.
    // _presentGate protects millisecond UI/DB presentation state ONLY (messages, choices, TTS enqueue); NEVER spans LLM calls and NEVER acquired by background work alongside _userTurnGate.
    private readonly SemaphoreSlim _userTurnGate = new(1, 1);
    private readonly SemaphoreSlim _presentGate = new(1, 1);
    private int _mainTurnActiveCount;

    private sealed class PresentGateScope : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _disposed;
        public PresentGateScope(SemaphoreSlim gate) => _gate = gate;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _gate.Release();
        }
    }

    private async Task<IDisposable> EnterPresentGateAsync(CancellationToken ct = default)
    {
        if (!await _presentGate.WaitAsync(TimeSpan.FromSeconds(10), ct))
        {
            _logger.LogError("_presentGate wait > 10s — possible re-entrancy or a slow section");
            await _presentGate.WaitAsync(ct);
        }
        return new PresentGateScope(_presentGate);
    }
    private DateTimeOffset? _dndUntil;
    private bool _dndAllowUrgent = true;
    private DateTimeOffset _boostedPollUntilUtc = DateTimeOffset.MinValue;
    private string? _boostedPollSkill;
    private string _boostedPollArgsJson = "{}";
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
    public event Action<PendingInteractionRequest>? InteractionAdded
    {
        add => _interaction.PendingRequestAdded += value;
        remove => _interaction.PendingRequestAdded -= value;
    }
    public event Action<string>? InteractionResolved
    {
        add => _interaction.PendingRequestResolved += value;
        remove => _interaction.PendingRequestResolved -= value;
    }
    public event Action? TrustedPathsChanged;
    public event Action<StandingWatcherRecord, string>? WatcherTriggered;

    public string ConversationId { get; private set; } = "conv-main";
    public string MonitorStatus => _localizer.TextCached("MonitorDefault", "Scheduled sync (configurable)");
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
        SkillRegistry skillRegistry,
        CustomSkillService customSkills,
        SandboxService sandbox,
        GuardService guard,
        StyleService styles,
        TtsService tts,
        UiLocalizer localizer,
        AppSettingsService settings,
        InteractionGateway interaction,
        ActivityMonitorService activity,
        StateMachineAgent stateMachine,
        BrowserHostService browserHost,
        BrowserFormBridgeService browserBridge,
        StandingWatcherService watchers,
        WorkflowCacheService workflowCache,
        WorkflowOptimizerAgent optimizer,
        AgentBlackboard blackboard,
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
        _skillRegistry = skillRegistry;
        _customSkills = customSkills;
        _sandbox = sandbox;
        _guard = guard;
        _styles = styles;
        _tts = tts;
        _localizer = localizer;
        _settings = settings;
        _interaction = interaction;
        _activity = activity;
        _stateMachine = stateMachine;
        _browserHost = browserHost;
        _browserBridge = browserBridge;
        _watchers = watchers;
        _workflowCache = workflowCache;
        _optimizer = optimizer;
        _blackboard = blackboard;
        _config = config;
        _logger = logger;

        _blackboard.JoinedTaskCompleted += OnJoinedSystemTaskCompleted;

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
        _skills.UpdatePinnedReRemindRequested += UpdatePinnedReRemindByKeywordAsync;
        _skills.PublishNotificationHandler = PersistNotificationAsync;
        _skills.UserPrefChanged += convId =>
            _ = _workflowCache.InvalidateEventWorkflowsAsync(convId, CancellationToken.None);
        _skills.FactModified += (convId, factId) =>
            _ = _workflowCache.InvalidateBySourceFactAsync(convId, factId, CancellationToken.None);
    }

    public async Task InitializeAsync()
    {
        ConversationId = await _store.GetActiveConversationIdAsync();

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
                m.Content = await _localizer.TextAsync("TaskInterrupted", "(Previous task was interrupted)", Array.Empty<object?>(), _appCts.Token);
            await _store.UpsertChatMessageAsync(m, _appCts.Token);
        }

        await _workflowCache.LoadTuningAsync(_appCts.Token);
        await _workflowCache.MigrateAndCleanLegacyCachesAsync(ConversationId, _appCts.Token);
        await _store.PruneOldObservabilityLogsAsync(30, _appCts.Token);
        _skills.SkillPollBoostRequested += (skillName, argsJson, secs) =>
        {
            _boostedPollSkill = skillName;
            _boostedPollArgsJson = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson;
            _boostedPollUntilUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(secs, 15, 300));
        };
        _stateMachine.ProposalRequested += OnStateMachineProposal;
        _activity.WindowFocused += (target, url) =>
            _ = _stateMachine.HandleWindowFocusAsync(ConversationId, target, url, _appCts.Token);
        _activity.Start();

        _ = Task.Run(() => RunSchedulerLoopAsync(_appCts.Token));
    }

    private string? _latestChoicesMsgId;
    private List<string> _latestChoices = new();
    private string? _latestChoicesLogicalTaskId;

    public IReadOnlyList<string> GetChoicesForMessage(string messageId)
    {
        lock (_choicesLock)
        {
            return string.Equals(_latestChoicesMsgId, messageId, StringComparison.Ordinal)
                ? _latestChoices.ToList()
                : Array.Empty<string>();
        }
    }

    public string? GetChoicesLogicalTaskIdForMessage(string messageId)
    {
        lock (_choicesLock)
        {
            return string.Equals(_latestChoicesMsgId, messageId, StringComparison.Ordinal)
                ? _latestChoicesLogicalTaskId
                : null;
        }
    }

    private void SetLatestChoices(string messageId, IReadOnlyList<string> choices, string? logicalTaskId)
    {
        lock (_choicesLock)
        {
            _latestChoicesMsgId = messageId;
            _latestChoices = choices.Take(4).ToList();
            _latestChoicesLogicalTaskId = logicalTaskId;
        }
    }

    private bool ClearChoicesIfMatching(string? logicalTaskId, bool force = false, bool removePinnedChatChoice = false)
    {
        bool matched;
        lock (_choicesLock)
        {
            matched = force || string.Equals(_latestChoicesLogicalTaskId, logicalTaskId, StringComparison.Ordinal);
        }
        if (matched)
            ClearChoices(removePinnedChatChoice);
        return matched;
    }

    public void ClearChoices(bool removePinnedChatChoice = true)
    {
        string? prevLogicalId;
        lock (_choicesLock)
        {
            prevLogicalId = _latestChoicesLogicalTaskId;
            _latestChoicesMsgId = null;
            _latestChoices = new List<string>();
            _latestChoicesLogicalTaskId = null;
        }

        if (removePinnedChatChoice &&
            (string.Equals(prevLogicalId, "chat-choice", StringComparison.Ordinal) ||
             HasPinnedConfirmation("chat-choice")))
        {
            RemovePinnedConfirmation("chat-choice");
        }
    }

    public PinnedReminderItem? FindPinnedForNotification(NotificationActionPayload n)
    {
        lock (_pinnedLock)
        {
            return _pinnedReminders.FirstOrDefault(p =>
                p.MailNotification is not null &&
                (string.Equals(p.MailNotification.NotificationId, n.NotificationId, StringComparison.Ordinal) ||
                 (!string.IsNullOrEmpty(n.MailId) && string.Equals(p.MailNotification.MailId, n.MailId, StringComparison.Ordinal))));
        }
    }

    private bool HasPinnedConfirmation(string logicalTaskId)
    {
        var id = $"confirm:{logicalTaskId}";
        lock (_pinnedLock)
            return _pinnedReminders.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal));
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

    public Task<string> LocalizeAsync(string key, string englishTemplate, params object?[] args)
        => _localizer.TextAsync(key, englishTemplate, args, _appCts.Token);

    public async Task<IReadOnlyList<SkillMenuEntry>> GetAllSkillsAsync()
        => await _skillRegistry.GetMenuEntriesAsync(_appCts.Token);

    public async Task SetSkillEnabledAsync(string skillName, bool enabled)
    {
        await _skillRegistry.SetEnabledAsync(skillName, enabled, _appCts.Token);
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
        var ct = _appCts.Token;
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return (false, await _localizer.TextAsync("ZipNotFound", "Selected zip file was not found.", Array.Empty<object?>(), ct));

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
                return (false, await _localizer.TextAsync("ZipMissingCoreFiles", "Zip must contain at least 3 required files: .ckpt (GPT weights), .pth (SoVITS weights), and .wav (reference audio).", Array.Empty<object?>(), ct));
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
                return (false, await _localizer.TextAsync("ZipExtractMissingFiles", "Required files (.ckpt / .pth / .wav) are missing after extraction.", Array.Empty<object?>(), ct));

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
            return (true, await _localizer.TextAsync("ZipImportSuccess", "Imported and switched to voice '{0}'.", new object?[] { rawName }, ct));
        }
        catch (Exception ex)
        {
            return (false, await _localizer.TextAsync("ZipImportFailed", "Failed to import voice package: {0}", new object?[] { ex.Message }, ct));
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
    public bool IsMainTurnActive => Volatile.Read(ref _mainTurnActiveCount) > 0;

    public async Task<(string Model, string Endpoint, long InputTokens, long OutputTokens, long CacheTokens)> GetModelAndUsageInfoAsync()
    {
        var ct = _appCts.Token;
        var simpleModel = await _store.GetSyncValueAsync("llm_simple_model", ct);
        var complexModel = await _store.GetSyncValueAsync("llm_complex_model", ct);
        var notConfigured = await _localizer.TextAsync("NotConfigured", "(Not configured)", Array.Empty<object?>(), ct);
        var defaultModel = _config.GetValue<string>("Foundry:Model")?.Trim() ?? notConfigured;

        var sLabel = string.IsNullOrWhiteSpace(simpleModel) ? defaultModel : simpleModel.Trim();
        var cLabel = string.IsNullOrWhiteSpace(complexModel) ? defaultModel : complexModel.Trim();
        var model = sLabel == cLabel
            ? sLabel
            : await _localizer.TextAsync("ModelTierSummary", "Simple: {0} | Complex: {1}", new object?[] { sLabel, cLabel }, ct);

        var simpleEp = await _store.GetSyncValueAsync("llm_simple_endpoint", ct);
        var endpoint = string.IsNullOrWhiteSpace(simpleEp)
            ? (_config.GetValue<string>("Foundry:Endpoint")?.Trim() ?? notConfigured)
            : simpleEp.Trim();

        long ReadCounter(string? raw) => long.TryParse(raw, out var v) ? v : 0L;
        var inTok = ReadCounter(await _store.GetSyncValueAsync("tokens_input", _appCts.Token));
        var outTok = ReadCounter(await _store.GetSyncValueAsync("tokens_output", _appCts.Token));
        var cacheTok = ReadCounter(await _store.GetSyncValueAsync("tokens_cache", _appCts.Token));
        return (model, endpoint, inTok, outTok, cacheTok);
    }

    public async Task<long> GetCacheWriteTokensAsync()
    {
        var raw = await _store.GetSyncValueAsync("tokens_cache_write", _appCts.Token);
        return long.TryParse(raw, out var v) ? v : 0L;
    }

    public Task<List<AgentUsageSummary>> GetLlmUsageByAgentAsync(CancellationToken ct = default)
        => _store.GetLlmUsageByAgentAsync(ct);

    public Task<List<AutoStateRuleRecord>> GetLearnedStateRulesAsync(CancellationToken ct = default)
        => _stateMachine.GetAllRulesAsync(ct);

    public async Task ForgetStateRuleAsync(string triggerKey, CancellationToken ct = default)
    {
        await _stateMachine.DeleteRuleAsync(triggerKey, ct);
        await _store.InsertLearningEventAsync("StateMachine", triggerKey, "pruned", "UserForgot", "Deleted", "user clicked forget", ct);
        AgentsChanged?.Invoke();
    }

    public Task<List<LearningEventRecord>> GetRecentLearningEventsAsync(int limit = 20, CancellationToken ct = default)
        => _store.GetRecentLearningEventsAsync(limit, ct);

    public async Task<BaselineUsageSnapshot?> GetBaselineUsageInfoAsync()
    {
        var ct = _appCts.Token;
        var recordedRaw = await _store.GetSyncValueAsync("baseline_recorded_at", ct);
        if (string.IsNullOrWhiteSpace(recordedRaw) || !DateTimeOffset.TryParse(recordedRaw, out var recordedAt))
            return null;

        long ReadCounter(string? raw) => long.TryParse(raw, out var v) ? v : 0L;
        return new BaselineUsageSnapshot(
            InputTokens: ReadCounter(await _store.GetSyncValueAsync("baseline_tokens_input", ct)),
            OutputTokens: ReadCounter(await _store.GetSyncValueAsync("baseline_tokens_output", ct)),
            CacheTokens: ReadCounter(await _store.GetSyncValueAsync("baseline_tokens_cache", ct)),
            Predictions: ReadCounter(await _store.GetSyncValueAsync("baseline_branch_predictions", ct)),
            Hits: ReadCounter(await _store.GetSyncValueAsync("baseline_branch_hits", ct)),
            Mispredictions: ReadCounter(await _store.GetSyncValueAsync("baseline_branch_mispredictions", ct)),
            EstimatedTokensSaved: ReadCounter(await _store.GetSyncValueAsync("baseline_branch_saved", ct)),
            RecordedAt: recordedAt);
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
        await _localizer.RefreshLanguageAsync(UiLocalizer.BuildCoreHotEntries(_skillRegistry), _appCts.Token);
        SkillsChanged?.Invoke();
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
        if (ok)
        {
            await _skillRegistry.ReloadFromStoreAsync(_appCts.Token);
            SkillsChanged?.Invoke();
        }
        return ok;
    }

    public void SaveGmailCredentials(string clientId, string? clientSecret = null)
        => _gmail.SaveClientCredentials(clientId, clientSecret);

    public async Task<bool> AuthorizeGmailAsync()
    {
        var ok = await _gmail.AuthorizeInteractiveAsync(_appCts.Token);
        if (ok)
        {
            await PushPetMessageAsync(await _localizer.TextAsync("GmailConnected", "Gmail connected. You can now query your emails.", Array.Empty<object?>(), _appCts.Token));
        }
        return ok;
    }

    public async Task SendUserPromptAsync(string prompt, string? targetMailId = null, string? replyToLogicalTaskId = null)
    {
        if (!string.IsNullOrWhiteSpace(replyToLogicalTaskId) &&
            replyToLogicalTaskId.StartsWith("state-propose:", StringComparison.Ordinal))
        {
            await HandleStateMachineProposalResponseAsync(replyToLogicalTaskId, prompt);
            return;
        }

        _lastUserActivityUtc = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var appCt = _appCts.Token;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _runningTasks[taskId] = linked;
        Interlocked.Increment(ref _mainTurnActiveCount);

        var run = new ActiveAgentRun
        {
            TaskId = taskId,
            Goal = prompt.Length > 40 ? prompt[..40] + "…" : prompt,
            MainState = "Running",
            PlannerState = "Pending",
            WorkerState = "Pending",
            CurrentStep = _localizer.TextCached("StepMainReceived", "MainAgent received prompt")
        };
        _agentRuns[taskId] = run;
        AgentsChanged?.Invoke();

        var processingStatus = await _localizer.TextAsync("ProcessingStatus", "Processing...", Array.Empty<object?>(), appCt);
        ChatMessageRecord userMsg;
        ChatMessageRecord assistantMsg;
        using (await EnterPresentGateAsync(appCt))
        {
            var nowUtc = DateTimeOffset.UtcNow;
            userMsg = new ChatMessageRecord
            {
                ConversationId = ConversationId,
                Role = "user",
                Content = prompt,
                CreatedAt = nowUtc,
                RevealedAt = nowUtc
            };
            assistantMsg = new ChatMessageRecord
            {
                ConversationId = ConversationId,
                Role = "assistant",
                Content = "",
                StatusText = processingStatus,
                IsStreaming = true,
                TaskId = taskId,
                ReplyToMessageId = userMsg.Id,
                CreatedAt = nowUtc.AddMilliseconds(5)
            };
            ClearChoices(removePinnedChatChoice: true);
            if (!string.IsNullOrWhiteSpace(replyToLogicalTaskId))
                RemovePinnedConfirmation(replyToLogicalTaskId);
            await _store.UpsertChatMessageAsync(userMsg);
            await _store.UpsertChatMessageAsync(assistantMsg);
        }
        StateChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            string? directTtsText = null;
            string? pendingText = null;
            List<MailSourceRef>? pendingSources = null;
            int replyRevealed = 0;
            Func<CancellationToken, Func<string, List<MailSourceRef>, Task>, Action<string>?, Action<IReadOnlyList<string>>?, Task>? dispatchedWork = null;

            bool userTurnGateAcquired = false;
            string? activePlannerTaskIdThisTurn = null;
            int plannerFinishReported = 0;
            IReadOnlyList<string>? turnLastChoices = null;
            string? turnLastSummary = null;

            // Presentation gate ONLY: never held across any LLM call or Worker/Planner wait;
            // UI events (StateChanged / ProactiveAlert) are fired AFTER releasing _presentGate.
            async Task RevealReplyAsync()
            {
                if (Interlocked.Exchange(ref replyRevealed, 1) != 0) return;
                bool shouldAlert = false;
                string alertContent = "";
                using (await EnterPresentGateAsync(CancellationToken.None))
                {
                    if (dispatchedWork is null)
                    {
                        if (run.MainState == "Running") run.MainState = "Done";
                        if (run.PlannerState == "Running") run.PlannerState = "Done";
                        if (run.WorkerState == "Running") run.WorkerState = "Done";
                        run.CurrentStep = _localizer.TextCached("StepCompleted", "Completed");

                        if (turnLastChoices is { Count: > 0 })
                        {
                            var choiceList = turnLastChoices.Take(4).ToList();
                            var choiceLogicalId = activePlannerTaskIdThisTurn ?? "chat-choice";
                            SetLatestChoices(assistantMsg.Id, choiceList, choiceLogicalId);
                            if (activePlannerTaskIdThisTurn is null)
                                UpsertPinnedConfirmation("chat-choice", run.Goal, pendingText, choiceList);
                        }
                    }
                    else
                    {
                        run.MainState = "Done";
                    }

                    if (!string.IsNullOrWhiteSpace(pendingText))
                        assistantMsg.Content = pendingText;
                    if (pendingSources is not null)
                        assistantMsg.Sources = pendingSources;

                    assistantMsg.IsStreaming = false;
                    assistantMsg.StatusText = null;
                    assistantMsg.RevealedAt = DateTimeOffset.UtcNow;

                    await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                    shouldAlert = dispatchedWork is null && !string.IsNullOrWhiteSpace(assistantMsg.Content);
                    alertContent = assistantMsg.Content;

                    if (!string.IsNullOrWhiteSpace(directTtsText) && _tts.IsEnabled && _tts.ChatMode != "notify_only")
                    {
                        _tts.EnqueueJapaneseSpeech(directTtsText);
                    }
                    else if (!string.IsNullOrWhiteSpace(pendingText) && string.IsNullOrWhiteSpace(directTtsText))
                    {
                        _tts.EnqueueSummarySpeech(_localizer.TextCached("AlertTitleReply", "AI Secretary Reply"), pendingText, kind: SpeechKind.ChatReply);
                    }
                }
                AgentsChanged?.Invoke();
                StateChanged?.Invoke();
                if (shouldAlert)
                    ProactiveAlert?.Invoke(_localizer.TextCached("AlertTitleReply", "AI Secretary Reply"), alertContent);
            }

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
                // Serialize user turns so Q2 always sees Q1's routing outcome and registered <in_flight> tasks.
                // Released immediately once routing + ack reveal finish (never held by background work).
                await _userTurnGate.WaitAsync(linked.Token);
                userTurnGateAcquired = true;

                CleanupExpiredPlannerTasks();
                // Only expose paused/waiting Planner tasks in <active_planner_tasks>;
                // currently running Planner tasks are exposed in <in_flight> so the router uses join_task instead of spawning duplicate runs.
                var activePlannerSnapshot = _activePlannerTasks.Values
                    .Where(t => Volatile.Read(ref t.RunningCount) <= 0)
                    .OrderByDescending(t => t.LastUpdatedAt)
                    .Select(t => new ActivePlannerTaskInfo(t.LogicalTaskId, t.Goal, false))
                    .ToList();

                await _mainAgent.HandleMessageAsync(
                    ConversationId,
                    taskId,
                    prompt,
                    targetMailId,
                    confirmAsync: (title, msg) => _interaction.RequestConfirmationAsync(title, msg, linked.Token),
                    onStatusAsync: async status =>
                    {
                        if (assistantMsg.IsStreaming)
                        {
                            assistantMsg.StatusText = status;
                            await _store.UpsertChatMessageAsync(assistantMsg, linked.Token);
                            StateChanged?.Invoke();
                        }
                    },
                    onReplyAsync: (text, sources) =>
                    {
                        pendingText = text;
                        pendingSources = sources;
                        turnLastSummary = text;
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
                        if (assistantMsg.IsStreaming)
                        {
                            assistantMsg.StatusText = detail;
                        }
                        AgentsChanged?.Invoke();
                    },
                    linked.Token,
                    onDirectTtsText: ttsText =>
                    {
                        directTtsText = ttsText;
                    },
                    onChoices: choices =>
                    {
                        turnLastChoices = choices.Take(4).ToList();
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
                            RemovePinnedConfirmation(existing.LogicalTaskId);
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
                            else if (!isCompleted && remaining <= 0 && turnLastChoices is { Count: > 0 })
                            {
                                UpsertPinnedConfirmation(logicalId, state.Goal, turnLastSummary, turnLastChoices);
                            }
                            else if (!isCompleted && remaining <= 0)
                            {
                                _activePlannerTasks.TryRemove(logicalId, out _);
                                _guard.ClearTaskApprovals(logicalId);
                                RemovePinnedConfirmation(logicalId);
                            }
                        }
                    },
                    onSingleSkillFinished: singleTaskId => _guard.ClearTaskApprovals(singleTaskId),
                    onClosePlannerTask: ClosePlannerTask,
                    preferredLogicalTaskId: replyToLogicalTaskId,
                    userMessageId: userMsg.Id);

                // Reveal reply atomically via _presentGate (while still inside _userTurnGate so lock order is strictly _userTurnGate -> _presentGate)
                await RevealReplyAsync();
            }
            catch (OperationCanceledException)
            {
                var stoppedText = await _localizer.TextAsync("TurnStopped", "(Stopped)", Array.Empty<object?>(), CancellationToken.None);
                using (await EnterPresentGateAsync(CancellationToken.None))
                {
                    if (run.MainState == "Running") run.MainState = "Stopped";
                    if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                    if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                    run.CurrentStep = _localizer.TextCached("StepStopped", "Stopped");

                    assistantMsg.IsStreaming = false;
                    assistantMsg.StatusText = null;
                    assistantMsg.RevealedAt = DateTimeOffset.UtcNow;
                    assistantMsg.Content = string.IsNullOrWhiteSpace(assistantMsg.Content)
                        ? stoppedText
                        : assistantMsg.Content + "\n\n" + stoppedText;
                    await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                }
                AgentsChanged?.Invoke();
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Task failed");
                var errPrefix = await _localizer.TextAsync("ErrorPrefix", "Error", Array.Empty<object?>(), CancellationToken.None);
                using (await EnterPresentGateAsync(CancellationToken.None))
                {
                    if (run.MainState == "Running") run.MainState = "Stopped";
                    if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                    if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                    run.CurrentStep = _localizer.TextCached("StepError", "Error: {0}", ex.Message);

                    assistantMsg.IsStreaming = false;
                    assistantMsg.StatusText = null;
                    assistantMsg.RevealedAt = DateTimeOffset.UtcNow;
                    assistantMsg.Content = $"❌ {errPrefix}: {ex.Message}";
                    await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                }
                AgentsChanged?.Invoke();
                StateChanged?.Invoke();
            }
            finally
            {
                if (Interlocked.Decrement(ref _mainTurnActiveCount) < 0)
                    Interlocked.Exchange(ref _mainTurnActiveCount, 0);

                if (userTurnGateAcquired)
                    _userTurnGate.Release();

                if (dispatchedWork is null)
                {
                    _blackboard.EnsureClosed(taskId);
                    if (!string.IsNullOrWhiteSpace(activePlannerTaskIdThisTurn))
                        _blackboard.EnsureClosed(activePlannerTaskIdThisTurn);
                    ReleasePlannerRunningIfUnreported();
                    _runningTasks.TryRemove(taskId, out _);
                    linked.Dispose();
                    AgentsChanged?.Invoke();
                    StateChanged?.Invoke();
                }
                else
                {
                    StateChanged?.Invoke();
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

                        // Background presentation ONLY acquires _presentGate (NEVER _userTurnGate).
                        bool hasReport = false;
                        using (await EnterPresentGateAsync(linked.Token))
                        {
                            if (run.PlannerState == "Running") run.PlannerState = "Done";
                            if (run.WorkerState == "Running") run.WorkerState = "Done";
                            run.CurrentStep = _localizer.TextCached("StepCompleted", "Completed");

                            assistantMsg.IsStreaming = false;
                            assistantMsg.StatusText = null;
                            await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);

                            if (!string.IsNullOrWhiteSpace(bgReportText))
                            {
                                hasReport = true;
                                var reportNow = DateTimeOffset.UtcNow;
                                var reportMsg = new ChatMessageRecord
                                {
                                    ConversationId = ConversationId,
                                    Role = "assistant",
                                    Content = bgReportText,
                                    Sources = bgSources ?? new List<MailSourceRef>(),
                                    IsStreaming = false,
                                    TaskId = taskId,
                                    ReplyToMessageId = userMsg.Id,
                                    CreatedAt = reportNow,
                                    RevealedAt = reportNow
                                };

                                if (bgChoices is { Count: > 0 })
                                {
                                    var choiceLogicalId = activePlannerTaskIdThisTurn ?? "chat-choice";
                                    SetLatestChoices(reportMsg.Id, bgChoices, choiceLogicalId);
                                    if (activePlannerTaskIdThisTurn is null)
                                        UpsertPinnedConfirmation("chat-choice", run.Goal, bgReportText, bgChoices);
                                }

                                await _store.UpsertChatMessageAsync(reportMsg, CancellationToken.None);

                                if (!string.IsNullOrWhiteSpace(bgDirectTtsText) && _tts.IsEnabled && _tts.ChatMode != "notify_only")
                                {
                                    _tts.EnqueueJapaneseSpeech(bgDirectTtsText);
                                }
                                else if (string.IsNullOrWhiteSpace(bgDirectTtsText))
                                {
                                    _tts.EnqueueSummarySpeech(_localizer.TextCached("AlertTitleReport", "AI Secretary Report"), bgReportText, kind: SpeechKind.Report);
                                }
                            }
                        }
                        AgentsChanged?.Invoke();
                        StateChanged?.Invoke();
                        if (hasReport && !string.IsNullOrWhiteSpace(bgReportText))
                            ProactiveAlert?.Invoke(_localizer.TextCached("AlertTitleReport", "AI Secretary Report"), bgReportText);
                    }
                    catch (OperationCanceledException)
                    {
                        var stoppedText = await _localizer.TextAsync("TurnStopped", "(Stopped)", Array.Empty<object?>(), CancellationToken.None);
                        using (await EnterPresentGateAsync(CancellationToken.None))
                        {
                            if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                            if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                            run.CurrentStep = _localizer.TextCached("StepStopped", "Stopped");

                            assistantMsg.IsStreaming = false;
                            assistantMsg.StatusText = null;
                            assistantMsg.RevealedAt ??= DateTimeOffset.UtcNow;
                            assistantMsg.Content = string.IsNullOrWhiteSpace(assistantMsg.Content)
                                ? stoppedText
                                : assistantMsg.Content + "\n\n" + stoppedText;
                            await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                        }
                        AgentsChanged?.Invoke();
                        StateChanged?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background planner/worker task failed");
                        var bgFailedText = await _localizer.TextAsync("BgTaskFailed", "Background task failed: {0}", new object?[] { ex.Message }, CancellationToken.None);
                        try
                        {
                            using (await EnterPresentGateAsync(appCt))
                            {
                                if (run.PlannerState == "Running") run.PlannerState = "Stopped";
                                if (run.WorkerState == "Running") run.WorkerState = "Stopped";
                                run.CurrentStep = _localizer.TextCached("StepError", "Error: {0}", ex.Message);

                                assistantMsg.IsStreaming = false;
                                assistantMsg.StatusText = null;
                                await _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);

                                var errNow = DateTimeOffset.UtcNow;
                                var errMsg = new ChatMessageRecord
                                {
                                    ConversationId = ConversationId,
                                    Role = "assistant",
                                    Content = $"❌ {bgFailedText}",
                                    IsStreaming = false,
                                    TaskId = taskId,
                                    ReplyToMessageId = userMsg.Id,
                                    CreatedAt = errNow,
                                    RevealedAt = errNow
                                };
                                await _store.UpsertChatMessageAsync(errMsg, CancellationToken.None);
                            }
                            AgentsChanged?.Invoke();
                            StateChanged?.Invoke();
                        }
                        catch { }
                    }
                    finally
                    {
                        _blackboard.EnsureClosed(taskId);
                        if (!string.IsNullOrWhiteSpace(activePlannerTaskIdThisTurn))
                            _blackboard.EnsureClosed(activePlannerTaskIdThisTurn);
                        if (assistantMsg.IsStreaming)
                        {
                            assistantMsg.IsStreaming = false;
                            assistantMsg.StatusText = null;
                            assistantMsg.RevealedAt ??= DateTimeOffset.UtcNow;
                            _ = _store.UpsertChatMessageAsync(assistantMsg, CancellationToken.None);
                        }
                        ReleasePlannerRunningIfUnreported();
                        _runningTasks.TryRemove(taskId, out _);
                        linked.Dispose();
                        AgentsChanged?.Invoke();
                        StateChanged?.Invoke();
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
        bool clearedChoices = ClearChoicesIfMatching(logicalId, removePinnedChatChoice: false);
        if (_activePlannerTasks.TryGetValue(logicalId, out var state) && Volatile.Read(ref state.RunningCount) <= 0)
        {
            _activePlannerTasks.TryRemove(logicalId, out _);
            _guard.ClearTaskApprovals(logicalId);
            RemovePinnedConfirmation(logicalId);
        }
        if (clearedChoices)
            StateChanged?.Invoke();
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
        _blackboard.ClearInFlight("killed by user");
        foreach (var id in _activePlannerTasks.Keys.ToList())
        {
            _guard.ClearTaskApprovals(id);
            RemovePinnedConfirmation(id);
        }
        _activePlannerTasks.Clear();
        var completedLabel = _localizer.TextCached("StepCompleted", "Completed");
        var killedLabel = _localizer.TextCached("StepKilledByUser", "Cleared by /kill magent");
        foreach (var run in _agentRuns.Values)
        {
            if (run.MainState == "Running") run.MainState = "Stopped";
            if (run.PlannerState == "Running") run.PlannerState = "Stopped";
            if (run.WorkerState == "Running") run.WorkerState = "Stopped";
            if (run.CurrentStep != completedLabel) run.CurrentStep = killedLabel;
        }
        AgentsChanged?.Invoke();

        var msgs = await _store.GetChatMessagesAsync(ConversationId, 20);
        foreach (var m in msgs.Where(x => x.IsStreaming))
        {
            m.IsStreaming = false;
            m.StatusText = null;
            m.RevealedAt ??= DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(m.Content))
                m.Content = await _localizer.TextAsync("KillCommandDone", "(Executed `/kill magent`; cleared stuck agent state)", Array.Empty<object?>(), CancellationToken.None);
            await _store.UpsertChatMessageAsync(m, CancellationToken.None);
        }
        StateChanged?.Invoke();
    }

    public async Task SnoozeNotificationAsync(string notificationId)
    {
        bool removedAny = false;
        lock (_pinnedLock)
        {
            for (int i = _pinnedReminders.Count - 1; i >= 0; i--)
            {
                var p = _pinnedReminders[i];
                if (p.MailNotification is not null &&
                    string.Equals(p.MailNotification.NotificationId, notificationId, StringComparison.Ordinal))
                {
                    _pinnedNextRemindAt.TryRemove(p.Id, out _);
                    _pinnedReminders.RemoveAt(i);
                    removedAny = true;
                }
            }
        }
        if (removedAny)
        {
            await SavePinnedRemindersAsync();
            PinnedRemindersChanged?.Invoke();
        }
        await _store.UpdateNotificationStatusAsync(notificationId, "Snoozed", DateTimeOffset.UtcNow.AddMinutes(15));
        StateChanged?.Invoke();
    }

    public async Task DraftReplyFromNotificationAsync(NotificationActionPayload n)
    {
        await _store.UpdateNotificationStatusAsync(n.NotificationId, "Handled", null);
        StateChanged?.Invoke();
        var promptText = await _localizer.TextAsync("DraftReplyPrompt", "Help me draft a reply to '{0}'", new object?[] { n.Title }, _appCts.Token);
        await SendUserPromptAsync(promptText, targetMailId: n.MailId);
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

    private void OnStateMachineProposal(StateMachineProposal proposal)
    {
        if (IsDndActive() || Volatile.Read(ref _mainTurnActiveCount) > 0)
            return;

        var logicalId = $"state-propose:{proposal.TriggerKey}";
        UpsertPinnedConfirmation(logicalId, proposal.Title, proposal.ProposalMessage, proposal.Choices);
    }

    private async Task HandleStateMachineProposalResponseAsync(string logicalId, string choice)
    {
        var triggerKey = logicalId["state-propose:".Length..];
        RemovePinnedConfirmation(logicalId);
        ClearChoices(removePinnedChatChoice: false);

        var trimmed = (choice ?? "").Trim();
        string replyText;
        if (string.Equals(trimmed, StateMachineAgent.ChoiceSnooze, StringComparison.OrdinalIgnoreCase))
        {
            await _stateMachine.SnoozeProposalAsync(triggerKey, TimeSpan.FromHours(3), _appCts.Token);
            replyText = "好，這次先不打擾你，等幾小時後或你需要的時候再說。";
        }
        else if (string.Equals(trimmed, StateMachineAgent.ChoiceMute, StringComparison.OrdinalIgnoreCase))
        {
            await _stateMachine.MuteProposalAsync(triggerKey, _appCts.Token);
            replyText = "了解，這頁以後不主動問了；如果你改天自己又叫我處理，我再重新記起來。";
        }
        else
        {
            replyText = await _stateMachine.AcceptProposalAsync(ConversationId, triggerKey, _appCts.Token);
        }

        using (await EnterPresentGateAsync(_appCts.Token))
        {
            var now = DateTimeOffset.UtcNow;
            var msg = new ChatMessageRecord
            {
                ConversationId = ConversationId,
                Role = "assistant",
                Content = replyText,
                CreatedAt = now,
                RevealedAt = now
            };
            await _store.UpsertChatMessageAsync(msg, _appCts.Token);
        }
        StateChanged?.Invoke();
        ProactiveAlert?.Invoke(_localizer.TextCached("AlertTitleReply", "AI Secretary Reply"), replyText);
    }

    private void OnJoinedSystemTaskCompleted(TaskResult result, IReadOnlyList<JoinedFollowUp> joinedFollowUps)
    {
        if (joinedFollowUps.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var (reply, ttsText) = await _mainAgent.SynthesizeJoinedFollowUpReplyAsync(ConversationId, result, joinedFollowUps, _appCts.Token);
                if (string.IsNullOrWhiteSpace(reply)) return;

                using (await EnterPresentGateAsync(_appCts.Token))
                {
                    var now = DateTimeOffset.UtcNow;
                    var msg = new ChatMessageRecord
                    {
                        ConversationId = ConversationId,
                        Role = "assistant",
                        Content = reply,
                        TaskId = result.TaskId,
                        ReplyToMessageId = joinedFollowUps[^1].MessageId,
                        CreatedAt = now,
                        RevealedAt = now
                    };
                    await _store.UpsertChatMessageAsync(msg, CancellationToken.None);

                    if (!string.IsNullOrWhiteSpace(ttsText) && _tts.IsEnabled && _tts.ChatMode != "notify_only")
                        _tts.EnqueueJapaneseSpeech(ttsText);
                    else
                        _tts.EnqueueSummarySpeech(_localizer.TextCached("AlertTitleReport", "AI Secretary Report"), reply, kind: SpeechKind.Report);
                }
                StateChanged?.Invoke();
                ProactiveAlert?.Invoke(_localizer.TextCached("AlertTitleReport", "AI Secretary Report"), reply);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Joined system task follow-up synthesis failed for {TaskId}", result.TaskId);
            }
        });
    }

    private void UpsertPinnedConfirmation(string logicalTaskId, string goal, string? replySummary, IReadOnlyList<string>? choices)
    {
        var id = $"confirm:{logicalTaskId}";
        var cleanTitle = (goal ?? "").ReplaceLineEndings(" ").Trim();
        var cleanSummary = (replySummary ?? "").Trim();
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
        else if (removed?.Kind == PinnedItemKind.Confirmation)
        {
            ClearChoicesIfMatching(
                removed.LogicalTaskId,
                force: string.Equals(id, "confirm:chat-choice", StringComparison.Ordinal),
                removePinnedChatChoice: false);
            if (!string.IsNullOrWhiteSpace(removed.LogicalTaskId) &&
                !string.Equals(removed.LogicalTaskId, "chat-choice", StringComparison.Ordinal))
            {
                ClosePlannerTask(removed.LogicalTaskId);
            }
        }

        StateChanged?.Invoke();
    }

    private List<PinnedReminderItem> MatchPinnedRemindersLocked(string rawKeyword)
    {
        var kw = rawKeyword?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(kw) || string.Equals(kw, "all", StringComparison.OrdinalIgnoreCase) || kw == "全部")
            return _pinnedReminders.ToList();

        var parts = kw
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _pinnedReminders.Where(p =>
            parts.Any(part =>
                p.Id.Equals(part, StringComparison.OrdinalIgnoreCase) ||
                p.Title.Contains(part, StringComparison.OrdinalIgnoreCase) ||
                p.Summary.Contains(part, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private async Task<string> DismissPinnedByKeywordAsync(string keyword)
    {
        var kw = keyword?.Trim() ?? "";
        List<PinnedReminderItem> targets;
        lock (_pinnedLock)
        {
            targets = string.IsNullOrWhiteSpace(kw)
                ? new List<PinnedReminderItem>()
                : MatchPinnedRemindersLocked(kw);
        }

        if (targets.Count == 0)
            return string.IsNullOrWhiteSpace(kw)
                ? await _localizer.TextAsync("NoPinnedReminders", "No pinned reminders right now.", Array.Empty<object?>(), _appCts.Token)
                : await _localizer.TextAsync("PinnedNotFound", "No pinned reminder matching '{0}'.", new object?[] { kw }, _appCts.Token);

        foreach (var t in targets)
            await DismissPinnedReminderAsync(t.Id);

        var names = string.Join("、", targets.Select(t => $"「{t.Title}」"));
        return await _localizer.TextAsync("PinnedDismissed", "Dismissed pinned reminder(s): {0}.", new object?[] { names }, _appCts.Token);
    }

    private async Task<string> UpdatePinnedReRemindByKeywordAsync(string keyword, int? reRemindMinutes)
    {
        var kw = keyword?.Trim() ?? "";
        if (reRemindMinutes is null)
        {
            List<PinnedReminderItem> all;
            lock (_pinnedLock)
                all = _pinnedReminders.ToList();
            if (all.Count == 0)
                return "dismiss_reminder: no pinned reminders currently active.";

            var lines = all.Select(p =>
            {
                var nextText = _pinnedNextRemindAt.TryGetValue(p.Id, out var nextAt)
                    ? nextAt.ToLocalTime().ToString("HH:mm")
                    : "none";
                return $"- id={p.Id} | kind={p.Kind} | title=\"{p.Title}\" | re_remind_minutes={p.ReRemindMinutes}m | next_re_remind={nextText}";
            });
            return $"dismiss_reminder: {all.Count} pinned reminder(s):\n" + string.Join("\n", lines);
        }

        int newMins = Math.Clamp(reRemindMinutes.Value, 0, 1440);
        var updatedTitles = new List<string>();
        var now = DateTimeOffset.UtcNow;

        lock (_pinnedLock)
        {
            var targets = MatchPinnedRemindersLocked(kw);
            if (targets.Count == 0)
                return $"dismiss_reminder: no pinned reminder matching '{kw}'.";

            foreach (var t in targets)
            {
                var idx = _pinnedReminders.FindIndex(p => p.Id == t.Id);
                if (idx < 0) continue;
                _pinnedReminders[idx] = t with { ReRemindMinutes = newMins };
                if (newMins > 0)
                    _pinnedNextRemindAt[t.Id] = now.AddMinutes(newMins);
                else
                    _pinnedNextRemindAt.TryRemove(t.Id, out _);
                updatedTitles.Add($"\"{t.Title}\"");
            }
        }

        await SavePinnedRemindersAsync();
        PinnedRemindersChanged?.Invoke();
        return newMins > 0
            ? $"dismiss_reminder: updated re_remind_minutes to {newMins}m (next re-remind at {now.AddMinutes(newMins).ToLocalTime():HH:mm}) for {updatedTitles.Count} pinned reminder(s): {string.Join(", ", updatedTitles)}."
            : $"dismiss_reminder: disabled periodic re-reminding (re_remind_minutes=0, card remains pinned) for {updatedTitles.Count} pinned reminder(s): {string.Join(", ", updatedTitles)}.";
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
        if (!string.IsNullOrEmpty(otpBtn?.Prompt))
        {
            _skills.LastObservedOtpCode = otpBtn.Prompt;
            _skills.LastObservedOtpHint = title;
            _boostedPollUntilUtc = DateTimeOffset.MinValue;
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

        using (await EnterPresentGateAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            var msg = new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "notification",
                Content = summary,
                Notification = payload,
                Sources = mail is null ? new List<MailSourceRef>() : new List<MailSourceRef> { new(mail.Id, mail.Subject, mail.SenderName, mail.Subject, mail.ReceivedAt) },
                CreatedAt = now,
                RevealedAt = now
            };
            await _store.UpsertChatMessageAsync(msg, ct);
            AddPinnedReminder(new PinnedReminderItem(payload.NotificationId, title, summary, mail?.Id, payload, reRemindMinutes));
        }
        StateChanged?.Invoke();
        RaiseProactiveAlert(title, summary);
    }

    private async Task PushPetMessageAsync(string content, CancellationToken ct = default)
    {
        using (await EnterPresentGateAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            var msg = new ChatMessageRecord
            {
                ConversationId = ConversationId,
                Role = "assistant",
                Content = content,
                CreatedAt = now,
                RevealedAt = now
            };
            await _store.UpsertChatMessageAsync(msg, ct);
        }
        StateChanged?.Invoke();
        RaiseProactiveAlert(_localizer.TextCached("AlertTitleDefault", "AI Secretary"), content);
    }

    private void RaiseProactiveAlert(string title, string content)
    {
        _tts.EnqueueSummarySpeech(title, content, () => ProactiveAlert?.Invoke(title, content));
    }

    private async Task RunSchedulerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool inSkillPollBoost = DateTimeOffset.UtcNow < _boostedPollUntilUtc && !string.IsNullOrWhiteSpace(_boostedPollSkill);
            var tickSeconds = inSkillPollBoost ? 6 : Math.Clamp(_settings.Current.Scheduler.TickSeconds, 5, 300);
            try
            {
                CleanupExpiredPlannerTasks();
                await WakeDueSnoozedAsync(ct);
                await WakeDueRemindersAsync(ct);
                await WakeDuePinnedReRemindersAsync(ct);
                if (inSkillPollBoost && _boostedPollSkill is { } boostSkill)
                {
                    var bbId = $"poll_boost:{boostSkill}";
                    var bbGoal = $"Poll boost: {boostSkill}";
                    _blackboard.StartTask(bbId, "scheduled_skill", bbGoal);
                    bool boostDone = false;
                    try
                    {
                        var boostObs = new List<string>();
                        var boostSteps = new List<string>();
                        using var argsDoc = JsonDocument.Parse(_boostedPollArgsJson);
                        var outcome = await _worker.RunStepAsync(
                            ConversationId,
                            $"[poll_boost] {boostSkill}",
                            boostSkill,
                            argsDoc.RootElement.Clone(),
                            targetMailId: null,
                            confirmAsync: (_, _) => Task.FromResult(false),
                            onStatusAsync: _ => Task.CompletedTask,
                            sources: new List<MailSourceRef>(),
                            observations: boostObs,
                            completedSteps: boostSteps,
                            ct: ct);
                        _blackboard.CompleteTask(
                            bbId, "scheduled_skill", bbGoal,
                            AgentBlackboard.BuildCompactSummary(boostSteps, boostObs),
                            succeeded: outcome == StepOutcome.Ok,
                            recordInRecent: false,
                            dedupeKey: $"poll_boost:{boostSkill}");
                        boostDone = true;
                    }
                    finally
                    {
                        if (!boostDone) _blackboard.EnsureClosed(bbId);
                    }
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
            var now = DateTimeOffset.UtcNow;
            var msg = new ChatMessageRecord
            {
                ConversationId = ConversationId,
                Role = "notification",
                Content = item.Summary,
                Notification = item with { Status = "Unread" },
                CreatedAt = now,
                RevealedAt = now
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
            await PushPetMessageAsync(await _localizer.TextAsync("ReRemindSingle", "🔔 Unacknowledged reminder '{0}': {1}", new object?[] { item.Title, item.Summary }, ct), ct);
        }
        else
        {
            var lines = dueItems.Select(i => $"- **{i.Title}**：{i.Summary}");
            await PushPetMessageAsync(await _localizer.TextAsync("ReRemindMulti", "🔔 Unacknowledged reminders ({0}):\n{1}", new object?[] { dueItems.Count, string.Join("\n", lines) }, ct), ct);
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

        var activeWindow = await BuildProactiveWindowLabelAsync(ct);
        var (reply, ttsText) = await _mainAgent.GenerateProactiveStyleLineAsync(ConversationId, pinnedTitles, ct, activeWindow);
        if (string.IsNullOrWhiteSpace(reply) || HasRunningTasks || IsDndActive())
            return;

        var proactiveNow = DateTimeOffset.UtcNow;
        var msg = new ChatMessageRecord
        {
            ConversationId = ConversationId,
            Role = "assistant",
            Content = reply,
            CreatedAt = proactiveNow,
            RevealedAt = proactiveNow
        };
        await _store.UpsertChatMessageAsync(msg, ct);
        var alertTitle = _localizer.TextCached("AlertTitleDefault", "AI Secretary");
        void RevealProactive()
        {
            StateChanged?.Invoke();
            ProactiveAlert?.Invoke(alertTitle, reply);
        }
        if (!string.IsNullOrWhiteSpace(ttsText))
            _tts.EnqueueJapaneseSpeech(ttsText, RevealProactive);
        else
            _tts.EnqueueSummarySpeech(alertTitle, reply, RevealProactive);
    }

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    private async Task<string?> BuildProactiveWindowLabelAsync(CancellationToken ct)
    {
        var lastWin = _activity.GetLastExternalTarget();
        if (lastWin is null)
            return null;

        var uiaTitle = string.IsNullOrWhiteSpace(lastWin.Title) ? lastWin.ProcessName : lastWin.Title.Trim();
        if (!BrowserProcessNames.Contains(lastWin.ProcessName))
            return $"{lastWin.ProcessName}: {uiaTitle}";

        // If foreground is a browser, try asking the Extension for the active tab title (returns null/incognito if private or not installed).
        try
        {
            var ext = await _browserBridge.RequestActiveTabTitleAsync(TimeSpan.FromMilliseconds(750), ct);
            if (ext is not null)
            {
                if (ext.Incognito)
                    return $"{lastWin.ProcessName} (private browsing)";
                if (!string.IsNullOrWhiteSpace(ext.Title))
                    return $"{lastWin.ProcessName}: {ext.Title.Trim()}";
            }
        }
        catch { }

        // Fallback when extension is not installed or timed out: use Windows UIA foreground window title.
        return $"{lastWin.ProcessName}: {uiaTitle}";
    }

    private async Task RunScheduledSkillSilentlyAsync(ReminderRecord r, CancellationToken ct)
    {
        var bbId = $"sched:{r.ReminderId}";
        var bbGoal = $"Scheduled {r.SkillName}: {r.Title}";
        _blackboard.StartTask(bbId, "scheduled_skill", bbGoal);
        bool bbDone = false;

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

            var isMailPoller = _skillRegistry.Resolve(r.SkillName!)?.IsSilentPoller == true;

            var prompt = string.IsNullOrWhiteSpace(r.Message)
                ? $"Scheduled background run: {r.Title}\n(Check <user_preferences> to decide whether to notify the user; if not needed, reply ONLY with NO_NOTIFY)"
                : $"Scheduled background run: {r.Title}\nRule: {r.Message}\n(Check <user_preferences> to decide whether to notify the user; if not needed, reply ONLY with NO_NOTIFY)";

            if (isMailPoller)
            {
                var pollObs = new List<string>();
                var pollSteps = new List<string>();
                var outcome = await _worker.RunStepAsync(
                    ConversationId,
                    prompt,
                    r.SkillName!,
                    args,
                    r.RelatedMailId,
                    confirmAsync: (_, _) => Task.FromResult(false),
                    onStatusAsync: _ => Task.CompletedTask,
                    new List<MailSourceRef>(),
                    pollObs,
                    pollSteps,
                    ct);
                _blackboard.CompleteTask(
                    bbId, "scheduled_skill", bbGoal,
                    AgentBlackboard.BuildCompactSummary(pollSteps, pollObs),
                    succeeded: outcome == StepOutcome.Ok,
                    recordInRecent: false,
                    dedupeKey: $"sched:{r.ReminderId}");
                bbDone = true;
                return;
            }

            var report = await _worker.ExecuteSingleSkillRawAsync(
                ConversationId,
                prompt,
                r.SkillName!,
                args,
                r.RelatedMailId,
                confirmAsync: (_, _) => Task.FromResult(false),
                onStatusAsync: _ => Task.CompletedTask,
                ct);

            var rawSummary = AgentBlackboard.BuildCompactSummary(report.CompletedSteps, report.Observations);
            _blackboard.CompleteTask(
                bbId, "scheduled_skill", bbGoal, rawSummary,
                succeeded: report.Succeeded,
                recordInRecent: report.Handled && report.Observations.Count > 0,
                dedupeKey: $"sched:{r.ReminderId}");
            bbDone = true;

            var reply = report.Handled
                ? await _mainAgent.SummarizeScheduledSkillAsync(ConversationId, prompt, report, ct)
                : "";

            if (report.Handled && !string.IsNullOrWhiteSpace(reply)
                && !reply.Trim().Equals("NO_NOTIFY", StringComparison.OrdinalIgnoreCase))
            {
                _blackboard.RefineResultSummary(bbId, reply.Trim());
                await PushPetMessageAsync($"⏰ {r.Title}：\n{reply.Trim()}", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Silent scheduled skill {Skill} failed", r.SkillName);
        }
        finally
        {
            if (!bbDone)
                _blackboard.EnsureClosed(bbId);
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
            var title = string.IsNullOrWhiteSpace(extRes.Title)
                ? await _localizer.TextAsync("BrowserTabDefault", "Browser tab", Array.Empty<object?>(), ct)
                : extRes.Title.Trim();
            if (title.Length > 24) title = title[..24] + "…";
            var msg = await _localizer.TextAsync("OtpExtensionFilled", "Filled verification code {0} into '{1}' via browser extension ({2}, not auto-submitted).", new object?[] { code, title, extRes.Mode ?? "dom" }, ct);
            return (true, msg);
        }

        return await _activity.TryFillOtpInExternalWindowAsync(code, serviceHint, ct);
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
        var prompt = SystemAutomationPrompts.BuildBrowserFormSuggestPrompt(form, GetReplyLanguage());
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
