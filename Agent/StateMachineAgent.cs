using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace AiSecretary.Desktop.Agent;

public sealed record StateMachineProposal(
    string TriggerKey,
    string Title,
    string ProposalMessage,
    IReadOnlyList<string> Choices);

public sealed record ActivePageContext(
    string Url,
    string Host,
    string RegistrableDomain,
    string Title,
    IReadOnlyList<string> Headings,
    IReadOnlyList<string> Labels,
    int InputCount,
    bool HasOneTimeCodeInput,
    bool HasFocusedInput,
    DateTimeOffset ObservedAtUtc);

public sealed record StateMachineContextEvent(
    string EventId,
    string ActionClass,
    string SourceId,
    string SourceDomain,
    string SourceName,
    string Title,
    string Summary,
    string ResourceHandle,
    DateTimeOffset OccurredAtUtc);

public enum ContextEventMatchKind
{
    None,
    SingleMatch,
    MultipleMatches
}

public sealed record MatchedContextEvent(
    StateMachineContextEvent Event,
    double Confidence,
    string Reason);

public sealed record ContextEventMatchDecision(
    ContextEventMatchKind Kind,
    ActivePageContext Page,
    IReadOnlyList<MatchedContextEvent> Matches);

/// <summary>
/// Unified adaptive autonomous state machine agent (Window/Page Context x Incoming Events -> Learned Action).
/// Contains ZERO hardcoded language keywords, OTP regexes, or domain blacklists:
/// - Hot rules (Counter >= 2): replay silently via WorkerAgent.RunStepAsync (0 LLM tokens).
/// - Cold rules (Counter == 1) &amp; same-domain experience transfer: gently raise hand after dwell (0 LLM tokens).
/// - First-visit unknown input pages/Office docs: structural input check + brief dwell -> 1 Simple-tier LLM probe
///   choosing <c>silent_prepare</c> (read-only background sync, zero popup), <c>propose</c>, or <c>ignore</c> (cached 7d).
/// - Incoming actionable context events (e.g., triaged mail actions): correlated bidirectionally (page-first or event-first)
///   within a ±3 minute window via exact RFC registrable-domain match (0 LLM) or 1 Simple-tier semantic match for cross-domain services.
/// </summary>
public sealed class StateMachineAgent : IDisposable
{
    public const string ChoiceAccept = "好，幫我做";
    public const string ChoiceAcceptTransfer = "好，照上次做";
    public const string ChoiceSnooze = "這次不用";
    public const string ChoiceMute = "以後別問";

    public static readonly TimeSpan EventCorrelationWindow = TimeSpan.FromMinutes(3);
    public const double MinSemanticMatchConfidence = 0.50;

    private static readonly TimeSpan ColdDwellDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FirstVisitDwellDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GlobalFirstProbeCooldown = TimeSpan.FromSeconds(20);

    // Pure Windows OS shell & self-exclusion (non-business infrastructure only).
    private static readonly HashSet<string> SystemShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "aisecretary.desktop", "explorer", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
        "applicationframehost", "taskmgr", "lockapp", "textinputhost"
    };

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    private static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "excel", "powerpnt", "winword", "outlook"
    };

    private readonly SqliteStore _store;
    private readonly WorkerAgent _worker;
    private readonly GuardService _guard;
    private readonly IModelGateway _model;
    private readonly BrowserFormBridgeService _browserBridge;
    private readonly AgentBlackboard _blackboard;
    private readonly UserPresenceService _presence;
    private readonly SecretVaultService _vault;
    private readonly ILogger<StateMachineAgent> _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    private readonly object _latestLock = new();
    private CancellationTokenSource? _latestCts;
    private string? _lastFocusedTriggerKey;
    private DateTimeOffset _lastFocusedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFirstProbeAtUtc = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hitCooldowns = new(StringComparer.OrdinalIgnoreCase);

    // Unified Context x Event correlation state (replaces dedicated OTP detector).
    private readonly object _eventLock = new();
    private ActivePageContext? _activePage;
    private readonly List<StateMachineContextEvent> _recentEvents = new();
    private readonly HashSet<string> _dispatchedEventKeys = new(StringComparer.OrdinalIgnoreCase);

    public event Action<StateMachineProposal>? ProposalRequested;

    /// <summary>
    /// Async handler invoked whenever an active page/window matches 1+ recent actionable context events.
    /// Returns <c>true</c> if the handler executed or surfaced a UI card so upstream producers skip duplicate alerts.
    /// </summary>
    public Func<ContextEventMatchDecision, CancellationToken, Task<bool>>? ContextEventMatchHandler { get; set; }

    public StateMachineAgent(
        SqliteStore store,
        WorkerAgent worker,
        GuardService guard,
        IModelGateway model,
        BrowserFormBridgeService browserBridge,
        AgentBlackboard blackboard,
        UserPresenceService presence,
        SecretVaultService vault,
        ILogger<StateMachineAgent> logger)
        : this(store, worker, guard, model, browserBridge, blackboard, presence, vault, logger, () => DateTimeOffset.UtcNow)
    {
    }

    internal StateMachineAgent(
        SqliteStore store,
        WorkerAgent worker,
        GuardService guard,
        IModelGateway model,
        BrowserFormBridgeService browserBridge,
        AgentBlackboard blackboard,
        UserPresenceService presence,
        SecretVaultService vault,
        ILogger<StateMachineAgent> logger,
        Func<DateTimeOffset> utcNow)
    {
        _store = store;
        _worker = worker;
        _guard = guard;
        _model = model;
        _browserBridge = browserBridge;
        _blackboard = blackboard;
        _presence = presence;
        _vault = vault;
        _logger = logger;
        _utcNow = utcNow;
    }

    /// <summary>
    /// One-time cleanup and upgrade of legacy rules:
    /// - Removes legacy browser-UIA (<c>win:msedge|...</c>) rules.
    /// - Removes any Cold <c>manage_mail</c> rules (e.g., bogus same-host transfers onto password login pages).
    /// - Upgrades legacy Hot <c>manage_mail</c> rules to <c>silent_prepare_manage_mail</c> with <c>boost_seconds=90</c>.
    /// - Prunes old un-normalized opaque-ID <c>Ignored</c> entries (e.g., per-video YouTube Shorts rows).
    /// </summary>
    public async Task MigrateLegacyRulesAsync(CancellationToken ct = default)
    {
        var done = await _store.GetSyncValueAsync("state_machine_v3_migrated", ct);
        if (done == "1") return;

        var rules = await _store.GetAllAutoStateRulesAsync(ct);
        foreach (var r in rules)
        {
            bool isLegacyBrowserWinRule = BrowserProcesses.Any(b =>
                r.TriggerKey.StartsWith($"win:{b}|", StringComparison.OrdinalIgnoreCase));

            var steps = ParseSteps(r.StepsJson);
            bool isMailSyncRule = steps.Any(IsSafeSilentPrepareStep) ||
                string.Equals(r.StateName, "first_visit_manage_mail", StringComparison.OrdinalIgnoreCase);

            if (isLegacyBrowserWinRule)
            {
                await _store.DeleteAutoStateRuleAsync(r.TriggerKey, ct);
                continue;
            }

            if (isMailSyncRule)
            {
                if (string.Equals(r.Status, "Hot", StringComparison.OrdinalIgnoreCase) && r.PredictorCounter >= 2)
                {
                    var upgradedSteps = new[] { EnsureSilentPrepareBoostArgs(steps.FirstOrDefault()) };
                    await _store.UpsertAutoStateRuleAsync(r with
                    {
                        StateName = "silent_prepare_manage_mail",
                        StepsJson = JsonSerializer.Serialize(upgradedSteps),
                        ProposalText = null
                    }, ct);
                }
                else
                {
                    await _store.DeleteAutoStateRuleAsync(r.TriggerKey, ct);
                }
                continue;
            }

            // Re-normalize legacy Ignored URL keys that contained opaque video/item IDs
            if (string.Equals(r.Status, "Ignored", StringComparison.OrdinalIgnoreCase) &&
                r.TriggerKey.StartsWith("url:", StringComparison.OrdinalIgnoreCase) &&
                !r.TriggerKey.EndsWith("/*", StringComparison.Ordinal))
            {
                var syntheticUrl = "https://" + r.TriggerKey["url:".Length..];
                var normalized = BuildTriggerKey(syntheticUrl, "browser", "");
                if (!string.Equals(normalized, r.TriggerKey, StringComparison.OrdinalIgnoreCase))
                    await _store.DeleteAutoStateRuleAsync(r.TriggerKey, ct);
            }
        }

        await _store.SetSyncValueAsync("state_machine_v3_migrated", "1", ct);
    }

    internal static bool IsOpaqueUrlSegment(string seg)
    {
        if (string.IsNullOrEmpty(seg)) return false;
        if (seg.StartsWith('@')) return true;
        if (Regex.IsMatch(seg, @"^(?:cht|chs|[a-z]{2}(?:-[a-z]{2,4})?|\d+)$", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(seg, @"^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase))
            return true;
        if (seg.Length >= 16 && Regex.IsMatch(seg, @"^[A-Za-z0-9_\-]+$") && seg.Any(char.IsDigit))
            return true;
        if (seg.Length >= 8 && Regex.IsMatch(seg, @"^[A-Za-z0-9_\-]+$") &&
            seg.Any(char.IsDigit) && seg.Any(char.IsUpper) && seg.Any(char.IsLower))
            return true;
        return false;
    }

    public static string BuildTriggerKey(string? url, string processName, string title)
    {
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => IsOpaqueUrlSegment(s) ? "*" : s)
                .Take(4);
            return $"url:{uri.Host.ToLowerInvariant()}/{string.Join("/", segs)}";
        }

        var cleanTitle = Regex.Replace((title ?? "").Trim(), @"\d+", "#");
        if (cleanTitle.Length > 80) cleanTitle = cleanTitle[..80];
        return $"win:{processName.ToLowerInvariant()}|{cleanTitle}";
    }

    public static string? BuildCoarseTriggerKey(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return $"url:{uri.Host.ToLowerInvariant()}/";
        return null;
    }

    internal static string? BuildHostIgnoreKey(string? url)
    {
        var coarse = BuildCoarseTriggerKey(url);
        return coarse is null ? null : coarse + "*";
    }

    /// <summary>
    /// Unified entry point for browser extension page signals:
    /// 1) Updates active page context and immediately evaluates any pending incoming context events (event-first -> page-second).
    /// 2) When the page has editable non-search inputs, drives standard StateMachine rule replay / first-visit probe.
    /// </summary>
    public async Task HandleBrowserPageSignalAsync(
        string conversationId,
        CachedBrowserPageSignal signal,
        ExternalWindowTarget? currentExternalTarget = null,
        CancellationToken appCt = default)
    {
        if (signal is null || string.IsNullOrWhiteSpace(signal.Url))
            return;

        var host = DomainParser.ExtractHostFromUrl(signal.Url);
        var regDomain = DomainParser.ExtractRegistrableDomain(host);
        var page = new ActivePageContext(
            Url: signal.Url.Trim(),
            Host: host,
            RegistrableDomain: regDomain,
            Title: (signal.Title ?? "").Trim(),
            Headings: signal.Headings ?? Array.Empty<string>(),
            Labels: signal.Labels ?? Array.Empty<string>(),
            InputCount: Math.Max(0, signal.InputCount),
            HasOneTimeCodeInput: signal.HasOneTimeCodeInput,
            HasFocusedInput: signal.HasFocusedInput,
            ObservedAtUtc: signal.CapturedAt == default ? _utcNow() : signal.CapturedAt);

        lock (_eventLock)
        {
            _activePage = page;
        }

        // Step 1: Event-first -> Page-second correlation (zero dwell so already-arrived events act immediately).
        if (page.InputCount > 0 || page.HasOneTimeCodeInput)
        {
            try
            {
                var decision = await TryCorrelateActivePageAsync(appCt);
                if (decision is not null && ContextEventMatchHandler is not null)
                    await ContextEventMatchHandler(decision, appCt);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StateMachineAgent] Page-arrival event correlation failed");
            }
        }

        // Step 2: Only engage page automation rules when the page structurally has editable form/input controls.
        if (page.InputCount <= 0 && !page.HasOneTimeCodeInput)
            return;

        var target = new ExternalWindowTarget(
            Hwnd: currentExternalTarget?.Hwnd ?? IntPtr.Zero,
            ProcessId: currentExternalTarget?.ProcessId ?? 0,
            ProcessName: currentExternalTarget is not null && BrowserProcesses.Contains(currentExternalTarget.ProcessName)
                ? currentExternalTarget.ProcessName
                : "chrome",
            Title: page.Title,
            CapturedAt: page.ObservedAtUtc,
            IsFormLike: page.InputCount >= 4,
            FormFieldLabels: page.Labels,
            VisibleInputCount: page.InputCount);

        await HandleWindowFocusInternalAsync(conversationId, target, page.Url, signal, appCt);
    }

    /// <summary>
    /// Records an incoming actionable context event (e.g., an OTP/action extracted by MailTriage at exit)
    /// and attempts immediate correlation against the currently active page (page-first -> event-second).
    /// Returns <c>true</c> if correlated and handled so the caller skips emitting a duplicate notification card.
    /// </summary>
    public async Task<bool> HandleContextEventAsync(StateMachineContextEvent contextEvent, CancellationToken ct = default)
    {
        if (contextEvent is null || string.IsNullOrWhiteSpace(contextEvent.EventId))
            return false;

        var now = _utcNow();
        lock (_eventLock)
        {
            PruneEventsLocked(now);
            _recentEvents.RemoveAll(e =>
                string.Equals(e.EventId, contextEvent.EventId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(contextEvent.ResourceHandle) &&
                 string.Equals(e.ResourceHandle, contextEvent.ResourceHandle, StringComparison.OrdinalIgnoreCase)));
            _recentEvents.Add(contextEvent);
        }

        var decision = await TryCorrelateActivePageAsync(ct);
        if (decision is null)
            return false;

        if (ContextEventMatchHandler is not null)
            return await ContextEventMatchHandler(decision, ct);

        return true;
    }

    internal async Task<ContextEventMatchDecision?> TryCorrelateActivePageAsync(CancellationToken ct = default)
    {
        var now = _utcNow();
        ActivePageContext? page;
        List<StateMachineContextEvent> eligible;

        lock (_eventLock)
        {
            PruneEventsLocked(now);
            page = _activePage;
            if (page is null || (page.InputCount <= 0 && !page.HasOneTimeCodeInput) || _recentEvents.Count == 0)
                return null;

            if ((now - page.ObservedAtUtc) > EventCorrelationWindow)
                return null;

            eligible = _recentEvents
                .Where(ev =>
                    Math.Abs((page.ObservedAtUtc - ev.OccurredAtUtc).TotalSeconds) <= EventCorrelationWindow.TotalSeconds &&
                    (now - ev.OccurredAtUtc) <= EventCorrelationWindow &&
                    IsEventResourceActive(ev) &&
                    !_dispatchedEventKeys.Contains(BuildDispatchKey(page.Url, ev.EventId)))
                .ToList();
        }

        if (eligible.Count == 0)
            return null;

        var decision = await EvaluatePageEventMatchAsync(page, eligible, ct);
        if (decision is null || decision.Kind == ContextEventMatchKind.None || decision.Matches.Count == 0)
            return null;

        lock (_eventLock)
        {
            // Guard against concurrent double-dispatch while async semantic match was in flight.
            var freshMatches = decision.Matches
                .Where(m => !_dispatchedEventKeys.Contains(BuildDispatchKey(page.Url, m.Event.EventId)))
                .ToList();
            if (freshMatches.Count == 0)
                return null;

            foreach (var m in freshMatches)
                _dispatchedEventKeys.Add(BuildDispatchKey(page.Url, m.Event.EventId));

            var kind = freshMatches.Count == 1 ? ContextEventMatchKind.SingleMatch : ContextEventMatchKind.MultipleMatches;
            return new ContextEventMatchDecision(kind, page, freshMatches);
        }
    }

    internal async Task<ContextEventMatchDecision?> EvaluatePageEventMatchAsync(
        ActivePageContext page,
        IReadOnlyList<StateMachineContextEvent> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return null;

        // Path A: Exact RFC registrable-domain match (e.g., tenso.com == tenso.com, pchome.com.tw == pchome.com.tw).
        // Pure DNS identity boundary (same trust basis as browser Same-Origin) -> zero LLM calls, confidence 1.0.
        if (!string.IsNullOrWhiteSpace(page.RegistrableDomain))
        {
            var exact = candidates
                .Where(ev => !string.IsNullOrWhiteSpace(ev.SourceDomain) &&
                             string.Equals(page.RegistrableDomain, ev.SourceDomain, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ev => ev.OccurredAtUtc)
                .Select(ev => new MatchedContextEvent(ev, 1.0, $"exact_domain:{page.RegistrableDomain}"))
                .ToList();

            if (exact.Count > 0)
            {
                var kind = exact.Count == 1 ? ContextEventMatchKind.SingleMatch : ContextEventMatchKind.MultipleMatches;
                return new ContextEventMatchDecision(kind, page, exact);
            }
        }

        // Path B: Cross-domain / localized-brand / third-party-ESP semantic match via 1 Simple-tier LLM call.
        // Never sends any plaintext secret (only redacted metadata + page labels/title).
        if (_presence.IsSilentForTokens)
            return null;

        try
        {
            using var matchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            matchCts.CancelAfter(TimeSpan.FromSeconds(8));

            var eventsBlock = string.Join("\n", candidates.Take(5).Select(ev =>
                $"- event_id={ev.EventId} | action={ev.ActionClass} | source_domain={ev.SourceDomain} | source_name=\"{ev.SourceName}\" | title=\"{ev.Title}\" | summary=\"{ev.Summary}\""));

            var sys = SystemAutomationPrompts.BuildContextEventMatchSystemPrompt();
            var user = SystemAutomationPrompts.BuildContextEventMatchUserPrompt(
                page.Url, page.Title, page.Headings, page.Labels, page.HasOneTimeCodeInput, eventsBlock);

            var resp = await _model.CompleteAsync(new[]
            {
                new ModelMessage("system", sys),
                new ModelMessage("user", user)
            }, new LlmCallOptions("StateMachine.EventMatch", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), matchCts.Token);

            var (matchedIds, confidence, reason) = ParseEventMatchDecision(resp.Content);
            if (matchedIds.Count == 0 || confidence < MinSemanticMatchConfidence)
                return null;

            var idSet = new HashSet<string>(matchedIds, StringComparer.OrdinalIgnoreCase);
            var matched = candidates
                .Where(ev => idSet.Contains(ev.EventId))
                .OrderByDescending(ev => ev.OccurredAtUtc)
                .Select(ev => new MatchedContextEvent(ev, confidence, string.IsNullOrWhiteSpace(reason) ? "semantic_match" : reason))
                .ToList();

            if (matched.Count == 0)
                return null;

            var kind = matched.Count == 1 ? ContextEventMatchKind.SingleMatch : ContextEventMatchKind.MultipleMatches;
            return new ContextEventMatchDecision(kind, page, matched);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StateMachineAgent] Cross-domain EventMatch failed");
            return null;
        }
    }

    private bool IsEventResourceActive(StateMachineContextEvent ev)
    {
        if (string.IsNullOrWhiteSpace(ev.ResourceHandle))
            return true;
        if (SecretVaultService.IsOtpHandle(ev.ResourceHandle))
            return _vault.TryPeekMetadata(ev.ResourceHandle, out _);
        return true;
    }

    private void PruneEventsLocked(DateTimeOffset now)
    {
        _recentEvents.RemoveAll(ev =>
            (now - ev.OccurredAtUtc) > EventCorrelationWindow || !IsEventResourceActive(ev));

        if (_dispatchedEventKeys.Count > 200)
            _dispatchedEventKeys.Clear();
    }

    private static string BuildDispatchKey(string pageUrl, string eventId) => $"{pageUrl}|{eventId}";

    public Task HandleWindowFocusAsync(
        string conversationId,
        ExternalWindowTarget target,
        string? url,
        CancellationToken appCt = default)
        => HandleWindowFocusInternalAsync(conversationId, target, url, browserSignal: null, appCt);

    private async Task HandleWindowFocusInternalAsync(
        string conversationId,
        ExternalWindowTarget target,
        string? url,
        CachedBrowserPageSignal? browserSignal,
        CancellationToken appCt)
    {
        var triggerKey = BuildTriggerKey(url, target.ProcessName, target.Title);

        // Latest-Wins: record latest focused trigger key and cancel any older in-flight dwell/replay.
        CancellationTokenSource linked;
        lock (_latestLock)
        {
            _lastFocusedTriggerKey = triggerKey;
            _lastFocusedAtUtc = _utcNow();
            try { _latestCts?.Cancel(); } catch { }
            try { _latestCts?.Dispose(); } catch { }
            _latestCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            linked = _latestCts;
        }
        var ct = linked.Token;

        try
        {
            var now = _utcNow();
            var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
            if (existing is not null)
            {
                if (existing.Status is "Muted" or "Disabled")
                    return;

                if (existing.CooldownUntil.HasValue && existing.CooldownUntil.Value > now)
                    return;

                if (existing.Status == "Ignored")
                    return;

                var steps = ParseSteps(existing.StepsJson);
                if (steps.Count == 0)
                    return;

                // Hot rule (Counter >= 2): run silently with 30s hit cooldown (0 LLM tokens); record outcome AFTER execution.
                if (existing.PredictorCounter >= 2)
                {
                    if (_hitCooldowns.TryGetValue(triggerKey, out var lastHit) && (now - lastHit) < TimeSpan.FromSeconds(30))
                        return;
                    _hitCooldowns[triggerKey] = now;

                    var ok = await ExecuteSafeStepsAsync(conversationId, triggerKey, existing.StateName, steps, ct);
                    await RecordOutcomeAsync(triggerKey, hit: ok, estimatedTokensSaved: ok ? 350 : 0, ct);
                    await _store.InsertPredictionLogAsync("state_machine", triggerKey, target.Title ?? triggerKey, steps[0].Skill, ok, ok ? 350 : 0, ct);
                    await _store.InsertLearningEventAsync("StateMachine", triggerKey, ok ? "replay_ok" : "replay_fail", existing.Status, ok ? "Hot" : "Cold", steps[0].Skill, ct);
                    return;
                }

                // Cold rule (Counter == 1): wait dwell time, then gently raise hand (0 LLM tokens)
                if (existing.PredictorCounter == 1)
                {
                    await Task.Delay(ColdDwellDelay, ct);
                    if (ct.IsCancellationRequested) return;

                    await _store.UpsertAutoStateRuleAsync(existing with
                    {
                        CooldownUntil = _utcNow().AddMinutes(30),
                        LastSeenAt = _utcNow()
                    }, ct);

                    var msg = !string.IsNullOrWhiteSpace(existing.ProposalText)
                        ? existing.ProposalText!
                        : BuildDefaultProposalMessage(steps[0].Skill, target.Title);

                    RaiseProposal(triggerKey, target.Title, msg, new[] { ChoiceAccept, ChoiceSnooze, ChoiceMute });
                    return;
                }
            }

            // Adaptive host-level cooling (zero hardcoded site blacklists): if this host already accumulated >=3 Ignored paths
            // with zero useful rules, skip probing new subpaths until cooldown expires or user manually uses a skill on this host.
            var hostIgnoreKey = BuildHostIgnoreKey(url);
            if (!string.IsNullOrWhiteSpace(hostIgnoreKey))
            {
                var hostIgnored = await _store.GetAutoStateRuleAsync(hostIgnoreKey, ct);
                if (hostIgnored is not null &&
                    string.Equals(hostIgnored.Status, "Ignored", StringComparison.OrdinalIgnoreCase) &&
                    (!hostIgnored.CooldownUntil.HasValue || hostIgnored.CooldownUntil.Value > now))
                {
                    return;
                }
            }

            // No exact rule: check same-domain coarse transfer for interactive form/doc rules ONLY (0 LLM tokens).
            // NEVER coarse-transfer passive background rules (such as manage_mail sync) onto unrelated pages like /login or /mypage.
            var coarsePrefix = BuildCoarseTriggerKey(url);
            if (!string.IsNullOrWhiteSpace(coarsePrefix))
            {
                var allRules = await _store.GetAllAutoStateRulesAsync(ct);
                var donor = allRules.FirstOrDefault(r =>
                {
                    if (string.Equals(r.TriggerKey, triggerKey, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!r.TriggerKey.StartsWith(coarsePrefix, StringComparison.OrdinalIgnoreCase)) return false;
                    if (r.Status is not ("Hot" or "Cold") || r.PredictorCounter < 1) return false;
                    if (r.StateName.StartsWith("silent_prepare_", StringComparison.OrdinalIgnoreCase)) return false;
                    var donorSteps = ParseSteps(r.StepsJson);
                    return donorSteps.Count > 0 && !donorSteps.Any(IsSafeSilentPrepareStep);
                });

                if (donor is not null)
                {
                    await Task.Delay(ColdDwellDelay, ct);
                    if (ct.IsCancellationRequested) return;

                    var donorSteps = ParseSteps(donor.StepsJson);
                    var transferred = new AutoStateRuleRecord(
                        TriggerKey: triggerKey,
                        StateName: donor.StateName,
                        StepsJson: donor.StepsJson,
                        PredictorCounter: 1,
                        HitCount: 0,
                        MissCount: 0,
                        Status: "Cold",
                        LastSeenAt: _utcNow(),
                        CooldownUntil: _utcNow().AddMinutes(30),
                        ProposalText: $"這頁跟之前在同個網站用過的很像，要照上次那樣幫你處理（{DescribeSkill(donorSteps[0].Skill)}）嗎？");

                    await _store.UpsertAutoStateRuleAsync(transferred, ct);
                    RaiseProposal(triggerKey, target.Title, transferred.ProposalText!, new[] { ChoiceAcceptTransfer, ChoiceSnooze, ChoiceMute });
                    return;
                }
            }

            // Brand-new window/page: pure structural check -> dwell -> at most 1 Simple LLM probe
            await TryFirstVisitProbeAsync(conversationId, triggerKey, target, url, browserSignal, ct);
        }
        catch (OperationCanceledException)
        {
            // User switched away before dwell completed — silent no-op.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StateMachineAgent] HandleWindowFocusAsync failed for {Key}", triggerKey);
        }
    }

    internal async Task TryFirstVisitProbeAsync(
        string conversationId,
        string triggerKey,
        ExternalWindowTarget target,
        string? url,
        CachedBrowserPageSignal? browserSignal,
        CancellationToken ct,
        bool skipDwellForTest = false)
    {
        if (_presence.IsSilentForTokens)
            return;

        var proc = (target.ProcessName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(proc) || SystemShellProcesses.Contains(proc))
            return;

        bool isBrowser = browserSignal is not null || BrowserProcesses.Contains(proc);
        bool isOffice = OfficeProcesses.Contains(proc);
        if (!isBrowser && !isOffice)
            return;

        if (isBrowser)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;
            if (uri.Scheme is not ("http" or "https"))
                return;
        }

        if (!skipDwellForTest)
        {
            // Wait brief dwell time so rapid tab switching costs 0 tokens and never nags.
            await Task.Delay(FirstVisitDwellDelay, ct);
            if (ct.IsCancellationRequested) return;

            // Global first-visit probe rate limit.
            lock (_latestLock)
            {
                if ((_utcNow() - _lastFirstProbeAtUtc) < GlobalFirstProbeCooldown)
                    return;
            }
        }

        string? formHints = null;
        if (isBrowser)
        {
            int fieldCount = browserSignal?.InputCount ?? 0;
            var labels = browserSignal?.Labels is { Count: > 0 } ? browserSignal.Labels.Take(8).ToList() : new List<string>();
            var headings = browserSignal?.Headings is { Count: > 0 } ? browserSignal.Headings.Take(4).ToList() : new List<string>();
            var excerpt = browserSignal?.Excerpt ?? "";

            if (fieldCount == 0 && browserSignal is null)
            {
                BrowserFormContext? formCtx = null;
                try
                {
                    formCtx = await _browserBridge.RequestActiveTabFormAsync(TimeSpan.FromMilliseconds(1500), ct);
                }
                catch { }

                var nonSearchFields = formCtx?.Fields?
                    .Where(f => !string.Equals(f.ControlType, "search", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                fieldCount = nonSearchFields?.Count ?? 0;
                if (fieldCount > 0)
                {
                    labels = nonSearchFields!
                        .Select(f => string.IsNullOrWhiteSpace(f.Label) ? f.ControlType : f.Label)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Take(8)
                        .ToList();
                }
            }

            // Pure structural gate (ZERO language keywords): only probe pages that actually contain editable non-search inputs.
            if (fieldCount == 0 && browserSignal?.HasOneTimeCodeInput != true)
                return;

            var parts = new List<string> { $"Detected {fieldCount} editable form field(s)" };
            if (labels.Count > 0)
                parts.Add($"labels=[{string.Join(", ", labels)}]");
            if (headings.Count > 0)
                parts.Add($"headings=[{string.Join(" | ", headings)}]");
            if (browserSignal?.HasOneTimeCodeInput == true)
                parts.Add("structural_hint=w3c_one_time_code_or_multibox");
            if (!string.IsNullOrWhiteSpace(excerpt))
                parts.Add($"excerpt=\"{(excerpt.Length <= 220 ? excerpt : excerpt[..220] + "…")}\"");
            formHints = string.Join("; ", parts);
        }
        else if (isOffice)
        {
            formHints = $"Active desktop Office document ({proc}).";
        }

        lock (_latestLock)
        {
            _lastFirstProbeAtUtc = _utcNow();
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(8));

        var sys = SystemAutomationPrompts.BuildFirstVisitProbeSystemPrompt();
        var user = SystemAutomationPrompts.BuildFirstVisitProbeUserPrompt(proc, target.Title ?? "", url, formHints);
        var resp = await _model.CompleteAsync(new[]
        {
            new ModelMessage("system", sys),
            new ModelMessage("user", user)
        }, new LlmCallOptions("StateMachine.FirstProbe", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), probeCts.Token);

        var (mode, proposal, step) = ParseFirstVisitDecision(resp.Content);
        if (mode == "ignore" || step is null || FilterSafeSteps(new List<CachedWorkflowStep> { step }).Count == 0)
        {
            var nowUtc = _utcNow();
            // Remember "nothing to offer" for 7 days so revisiting costs 0 tokens.
            await _store.UpsertAutoStateRuleAsync(new AutoStateRuleRecord(
                TriggerKey: triggerKey,
                StateName: "ignored_first_visit",
                StepsJson: "[]",
                PredictorCounter: 0,
                HitCount: 0,
                MissCount: 0,
                Status: "Ignored",
                LastSeenAt: nowUtc,
                CooldownUntil: nowUtc.AddDays(7)), ct);

            // Adaptive host-level cooling (zero hardcoded site blacklists): if >=3 paths on the same host
            // are Ignored and 0 are Hot/Cold, record a 7-day host-level Ignored rule so browsing subpaths costs 0 tokens.
            var coarse = BuildCoarseTriggerKey(url);
            var hostIgnore = BuildHostIgnoreKey(url);
            if (!string.IsNullOrWhiteSpace(coarse) && !string.IsNullOrWhiteSpace(hostIgnore))
            {
                var allRules = await _store.GetAllAutoStateRulesAsync(ct);
                var hostRules = allRules
                    .Where(r => r.TriggerKey.StartsWith(coarse, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(r.TriggerKey, hostIgnore, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                bool hasUseful = hostRules.Any(r => r.Status is "Hot" or "Cold");
                int ignoredCount = hostRules.Count(r =>
                    string.Equals(r.Status, "Ignored", StringComparison.OrdinalIgnoreCase) &&
                    (!r.CooldownUntil.HasValue || r.CooldownUntil.Value > nowUtc));
                if (!hasUseful && ignoredCount >= 3)
                {
                    await _store.UpsertAutoStateRuleAsync(new AutoStateRuleRecord(
                        TriggerKey: hostIgnore,
                        StateName: "ignored_host_adaptive",
                        StepsJson: "[]",
                        PredictorCounter: 0,
                        HitCount: 0,
                        MissCount: 0,
                        Status: "Ignored",
                        LastSeenAt: nowUtc,
                        CooldownUntil: nowUtc.AddDays(7)), ct);
                }
            }
            return;
        }

        // Strictly read-only background preparation (manage_mail sync):
        // ALWAYS save directly as Hot and execute silently with ZERO popup card (even if LLM wrote mode=propose).
        if (IsSafeSilentPrepareStep(step))
        {
            var silentStep = EnsureSilentPrepareBoostArgs(step);
            var silentSteps = new List<CachedWorkflowStep> { silentStep };
            var silentRule = new AutoStateRuleRecord(
                TriggerKey: triggerKey,
                StateName: $"silent_prepare_{silentStep.Skill.ToLowerInvariant()}",
                StepsJson: JsonSerializer.Serialize(silentSteps),
                PredictorCounter: 2,
                HitCount: 0,
                MissCount: 0,
                Status: "Hot",
                LastSeenAt: _utcNow(),
                CooldownUntil: null,
                ProposalText: null);

            await _store.UpsertAutoStateRuleAsync(silentRule, ct);
            _hitCooldowns[triggerKey] = _utcNow();
            var ok = await ExecuteSafeStepsAsync(conversationId, triggerKey, silentRule.StateName, silentSteps, ct);
            await RecordOutcomeAsync(triggerKey, hit: ok, estimatedTokensSaved: 0, ct);
            return;
        }

        var proposalText = string.IsNullOrWhiteSpace(proposal)
            ? BuildDefaultProposalMessage(step.Skill, target.Title)
            : proposal.Trim();

        var rule = new AutoStateRuleRecord(
            TriggerKey: triggerKey,
            StateName: $"first_visit_{step.Skill.ToLowerInvariant()}",
            StepsJson: JsonSerializer.Serialize(new[] { step }),
            PredictorCounter: 1,
            HitCount: 0,
            MissCount: 0,
            Status: "Cold",
            LastSeenAt: _utcNow(),
            CooldownUntil: _utcNow().AddMinutes(30),
            ProposalText: proposalText);

        await _store.UpsertAutoStateRuleAsync(rule, ct);
        RaiseProposal(triggerKey, target.Title, proposalText, new[] { ChoiceAccept, ChoiceSnooze, ChoiceMute });
    }

    private static bool IsSafeSilentPrepareStep(CachedWorkflowStep step)
    {
        if (!string.Equals(step.Skill, "manage_mail", StringComparison.OrdinalIgnoreCase))
            return false;
        if (step.Args.ValueKind != JsonValueKind.Object)
            return false;
        if (!step.Args.TryGetProperty("action", out var act))
            return false;
        return string.Equals(act.GetString()?.Trim(), "sync", StringComparison.OrdinalIgnoreCase);
    }

    private static CachedWorkflowStep EnsureSilentPrepareBoostArgs(CachedWorkflowStep? step)
    {
        if (step is not null && step.Args.ValueKind == JsonValueKind.Object && step.Args.TryGetProperty("boost_seconds", out _))
            return step;
        using var doc = JsonDocument.Parse("""{"action":"sync","source":"all","boost_seconds":90}""");
        return new CachedWorkflowStep("manage_mail", doc.RootElement.Clone());
    }

    private void RaiseProposal(string triggerKey, string? title, string message, IReadOnlyList<string> choices)
    {
        ProposalRequested?.Invoke(new StateMachineProposal(triggerKey, title ?? "", message, choices));
    }

    /// <summary>
    /// User accepted a gentle proposal: promote to Hot, clear cooldown, and execute the step immediately.
    /// </summary>
    public async Task<string> AcceptProposalAsync(string conversationId, string triggerKey, CancellationToken ct = default)
    {
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is null)
            return "找不到對應的提議規則。";

        var steps = ParseSteps(existing.StepsJson);
        if (steps.Count == 0)
            return "這個提議目前沒有可執行的步驟。";

        int nextCounter = Math.Min(3, Math.Max(2, existing.PredictorCounter + 1));
        await _store.UpsertAutoStateRuleAsync(existing with
        {
            PredictorCounter = nextCounter,
            HitCount = existing.HitCount + 1,
            Status = "Hot",
            CooldownUntil = null,
            LastSeenAt = _utcNow()
        }, ct);
        await _store.RecordBranchOutcomeAsync(hit: true, estimatedTokensSaved: 350, ct);

        var observations = new List<string>();
        var completedSteps = new List<string>();
        var bbId = $"sm_accept:{triggerKey}";
        var bbGoal = $"StateMachine accepted {steps[0].Skill} ({triggerKey})";
        _blackboard.StartTask(bbId, "state_machine", bbGoal);
        bool bbDone = false;
        try
        {
            bool allOk = true;
            foreach (var step in steps.Take(2))
            {
                if (!_guard.IsSkillEnabled(step.Skill)) continue;
                var outcome = await _worker.RunStepAsync(
                    conversationId: conversationId,
                    userPrompt: $"[auto_state_accepted:{existing.StateName}] {triggerKey}",
                    skillName: step.Skill,
                    args: step.Args,
                    targetMailId: null,
                    confirmAsync: (_, _) => Task.FromResult(false),
                    onStatusAsync: _ => Task.CompletedTask,
                    sources: new List<MailSourceRef>(),
                    observations: observations,
                    completedSteps: completedSteps,
                    ct: ct);
                if (outcome != Skills.StepOutcome.Ok)
                    allOk = false;
            }
            _blackboard.CompleteTask(bbId, "state_machine", bbGoal, AgentBlackboard.BuildCompactSummary(completedSteps, observations), succeeded: allOk, recordInRecent: true);
            bbDone = true;
        }
        finally
        {
            if (!bbDone) _blackboard.EnsureClosed(bbId);
        }

        if (observations.Count == 0)
            return $"好，已經幫你處理好了（{DescribeSkill(steps[0].Skill)}），之後遇到這頁也會直接幫你留意。";

        var snippet = string.Join("\n", observations.Take(2));
        if (snippet.Length > 350) snippet = snippet[..350] + "…";
        return $"好，幫你處理完了，以後到這頁我也會主動留意：\n{snippet}";
    }

    public async Task SnoozeProposalAsync(string triggerKey, TimeSpan? duration = null, CancellationToken ct = default)
    {
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is null) return;

        var snooze = duration ?? TimeSpan.FromHours(3);
        int nextCnt = Math.Max(1, existing.PredictorCounter - 1);
        await _store.UpsertAutoStateRuleAsync(existing with
        {
            PredictorCounter = nextCnt,
            Status = "Cold",
            CooldownUntil = _utcNow().Add(snooze),
            LastSeenAt = _utcNow()
        }, ct);
    }

    public async Task MuteProposalAsync(string triggerKey, CancellationToken ct = default)
    {
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is null) return;

        await _store.UpsertAutoStateRuleAsync(existing with
        {
            PredictorCounter = 0,
            MissCount = existing.MissCount + 1,
            Status = "Muted",
            CooldownUntil = null,
            LastSeenAt = _utcNow()
        }, ct);
        await _store.RecordBranchOutcomeAsync(hit: false, estimatedTokensSaved: 0, ct);
    }

    public async Task ObserveUserSkillExecutionAsync(
        ExternalWindowTarget? fallbackTarget,
        string skillName,
        JsonElement args,
        CancellationToken ct = default)
    {
        var candidate = new CachedWorkflowStep(
            skillName.Trim(),
            args.ValueKind == JsonValueKind.Object ? args.Clone() : JsonDocument.Parse("{}").RootElement.Clone());
        var safeSteps = FilterSafeSteps(new List<CachedWorkflowStep> { candidate });
        if (safeSteps.Count == 0)
            return;

        string? triggerKey;
        lock (_latestLock)
        {
            triggerKey = !string.IsNullOrWhiteSpace(_lastFocusedTriggerKey) &&
                         (_utcNow() - _lastFocusedAtUtc) <= TimeSpan.FromMinutes(5)
                ? _lastFocusedTriggerKey
                : null;
        }

        if (string.IsNullOrWhiteSpace(triggerKey) && fallbackTarget is not null)
            triggerKey = BuildTriggerKey(null, fallbackTarget.ProcessName, fallbackTarget.Title);

        if (string.IsNullOrWhiteSpace(triggerKey))
            return;

        var stepsJson = JsonSerializer.Serialize(safeSteps);
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);

        int nextCounter = 1;
        int hits = existing?.HitCount ?? 0;
        int misses = existing?.MissCount ?? 0;

        if (existing is not null)
        {
            if (existing.Status is "Muted" or "Disabled" or "Ignored")
            {
                nextCounter = 1;
                misses = 0;
            }
            else if (string.Equals(existing.StepsJson, stepsJson, StringComparison.Ordinal))
            {
                nextCounter = Math.Min(3, existing.PredictorCounter + 1);
            }
        }

        var stateName = $"learned_{skillName.Trim().ToLowerInvariant()}";
        var nextStatus = nextCounter >= 2 ? "Hot" : "Cold";
        var updated = new AutoStateRuleRecord(
            TriggerKey: triggerKey,
            StateName: stateName,
            StepsJson: stepsJson,
            PredictorCounter: nextCounter,
            HitCount: hits,
            MissCount: misses,
            Status: nextStatus,
            LastSeenAt: _utcNow(),
            CooldownUntil: null,
            ProposalText: existing?.ProposalText);

        await _store.UpsertAutoStateRuleAsync(updated, ct);
        // If this host had an adaptive host-level Ignored rule, clear it since the user explicitly acted on this host.
        if (triggerKey.StartsWith("url:", StringComparison.OrdinalIgnoreCase))
        {
            var slash = triggerKey.IndexOf('/', 4);
            if (slash > 4)
            {
                var hostIgnore = triggerKey[..(slash + 1)] + "*";
                await _store.DeleteAutoStateRuleAsync(hostIgnore, ct);
            }
        }
        var evtType = existing is not null && existing.Status is "Muted" or "Disabled" or "Ignored"
            ? "revived"
            : (existing?.Status != "Hot" && nextStatus == "Hot" ? "promoted" : "observed");
        await _store.InsertLearningEventAsync("StateMachine", triggerKey, evtType, existing?.Status ?? "None", nextStatus, skillName, ct);
    }

    public async Task RecordOutcomeAsync(string triggerKey, bool hit, long estimatedTokensSaved = 350, CancellationToken ct = default)
    {
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is null) return;

        int nextCnt = hit
            ? Math.Min(3, existing.PredictorCounter + 1)
            : Math.Max(1, existing.PredictorCounter - 1);

        var nextStatus = nextCnt >= 2 ? "Hot" : "Cold";
        DateTimeOffset? cooldown = hit ? null : _utcNow().AddHours(1);

        await _store.UpsertAutoStateRuleAsync(existing with
        {
            PredictorCounter = nextCnt,
            HitCount = hit ? existing.HitCount + 1 : existing.HitCount,
            MissCount = hit ? existing.MissCount : existing.MissCount + 1,
            Status = nextStatus,
            CooldownUntil = cooldown,
            LastSeenAt = _utcNow()
        }, ct);

        await _store.RecordBranchOutcomeAsync(hit, hit ? estimatedTokensSaved : 0, ct);
    }

    public Task<List<AutoStateRuleRecord>> GetAllRulesAsync(CancellationToken ct = default)
        => _store.GetAllAutoStateRulesAsync(ct);

    public Task<bool> DeleteRuleAsync(string triggerKey, CancellationToken ct = default)
        => _store.DeleteAutoStateRuleAsync(triggerKey, ct);

    private async Task<bool> ExecuteSafeStepsAsync(
        string conversationId,
        string triggerKey,
        string stateName,
        List<CachedWorkflowStep> steps,
        CancellationToken ct)
    {
        var bbId = $"sm_replay:{triggerKey}";
        var bbGoal = $"Auto-replay {steps[0].Skill} ({stateName})";
        _blackboard.StartTask(bbId, "state_machine", bbGoal);
        var observations = new List<string>();
        var completedSteps = new List<string>();
        bool anyExecuted = false;
        bool succeeded = true;
        bool bbDone = false;

        try
        {
            foreach (var step in steps.Take(2))
            {
                if (!_guard.IsSkillEnabled(step.Skill)) continue;
                try
                {
                    var outcome = await _worker.RunStepAsync(
                        conversationId: conversationId,
                        userPrompt: $"[auto_state:{stateName}] {triggerKey}",
                        skillName: step.Skill,
                        args: step.Args,
                        targetMailId: null,
                        confirmAsync: (_, _) => Task.FromResult(false),
                        onStatusAsync: _ => Task.CompletedTask,
                        sources: new List<MailSourceRef>(),
                        observations: observations,
                        completedSteps: completedSteps,
                        ct: ct);
                    anyExecuted = true;
                    if (outcome != Skills.StepOutcome.Ok)
                    {
                        succeeded = false;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "[StateMachineAgent] step {Skill} failed for {Key}", step.Skill, triggerKey);
                    observations.Add($"{step.Skill} failed: {ex.Message}");
                    succeeded = false;
                    break;
                }
            }

            bool ok = anyExecuted && succeeded;
            _blackboard.CompleteTask(
                bbId,
                "state_machine",
                bbGoal,
                AgentBlackboard.BuildCompactSummary(completedSteps, observations),
                succeeded: ok,
                recordInRecent: observations.Count > 0,
                dedupeKey: $"sm_replay:{triggerKey}");
            bbDone = true;
            return ok;
        }
        finally
        {
            if (!bbDone)
                _blackboard.EnsureClosed(bbId);
        }
    }

    private List<CachedWorkflowStep> FilterSafeSteps(List<CachedWorkflowStep> steps)
    {
        var safe = new List<CachedWorkflowStep>();
        foreach (var s in steps.Take(2))
        {
            if (string.IsNullOrWhiteSpace(s.Skill) || !_guard.IsSkillEnabled(s.Skill)) continue;
            if (WorkflowCacheService.HasNonReusableMutatingSteps(new[] { s })) continue;
            safe.Add(s);
        }
        return safe;
    }

    internal static (string Mode, string? Proposal, CachedWorkflowStep? Step) ParseFirstVisitDecision(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("ignore", null, null);
        try
        {
            var cleaned = ExtractJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            string mode = "ignore";
            if (root.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.String)
            {
                mode = (md.GetString() ?? "ignore").Trim().ToLowerInvariant();
            }
            else if (root.TryGetProperty("should_propose", out var sp) && sp.ValueKind == JsonValueKind.True)
            {
                mode = "propose";
            }

            if (mode is not ("silent_prepare" or "propose"))
                return ("ignore", null, null);

            var skill = root.TryGetProperty("skill", out var sk) ? sk.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(skill)) return ("ignore", null, null);

            var proposal = root.TryGetProperty("proposal", out var pr) ? pr.GetString()?.Trim() : null;
            var args = root.TryGetProperty("args", out var ag) && ag.ValueKind == JsonValueKind.Object
                ? ag.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            return (mode, proposal, new CachedWorkflowStep(skill, args));
        }
        catch
        {
            return ("ignore", null, null);
        }
    }

    internal static (List<string> MatchedEventIds, double Confidence, string Reason) ParseEventMatchDecision(string? raw)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return (ids, 0.0, "");
        try
        {
            var cleaned = ExtractJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            if (root.TryGetProperty("matched_event_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var s = el.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        ids.Add(s);
                }
            }

            double conf = 0.0;
            if (root.TryGetProperty("confidence", out var cf) && cf.TryGetDouble(out var d))
                conf = Math.Clamp(d, 0.0, 1.0);

            var reason = root.TryGetProperty("reason", out var rs) ? (rs.GetString() ?? "").Trim() : "";
            return (ids, conf, reason);
        }
        catch
        {
            return (ids, 0.0, "");
        }
    }

    private static string ExtractJsonObject(string raw)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = cleaned.IndexOf('\n');
            if (nl >= 0) cleaned = cleaned[(nl + 1)..];
            var end = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) cleaned = cleaned[..end];
            cleaned = cleaned.Trim();
        }
        var sIdx = cleaned.IndexOf('{');
        var eIdx = cleaned.LastIndexOf('}');
        if (sIdx >= 0 && eIdx > sIdx)
            cleaned = cleaned[sIdx..(eIdx + 1)];
        return cleaned;
    }

    private static string DescribeSkill(string skill) => skill.ToLowerInvariant() switch
    {
        "browser_form" => "讀取表單並協助預填",
        "manage_mail" => "同步收信確認郵件",
        "office_excel" => "讀取這份 Excel 內容",
        "office_ppt" => "讀取這份簡報內容",
        _ => skill
    };

    private static string BuildDefaultProposalMessage(string skill, string? title)
    {
        var shortTitle = string.IsNullOrWhiteSpace(title) ? "這個畫面" : (title.Length > 26 ? title[..26] + "…" : title);
        return $"看到你在「{shortTitle}」，要順手幫你{DescribeSkill(skill)}嗎？";
    }

    private static List<CachedWorkflowStep> ParseSteps(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CachedWorkflowStep>>(json) ?? new List<CachedWorkflowStep>();
        }
        catch
        {
            return new List<CachedWorkflowStep>();
        }
    }

    public void Dispose()
    {
        lock (_latestLock)
        {
            try { _latestCts?.Cancel(); } catch { }
            try { _latestCts?.Dispose(); } catch { }
            _latestCts = null;
        }
    }
}
