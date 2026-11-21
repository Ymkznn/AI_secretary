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

/// <summary>
/// Human-friendly adaptive autonomous state machine agent.
/// - Hot rules (Counter >= 2): replay silently via WorkerAgent.RunStepAsync (0 LLM tokens).
/// - Cold rules (Counter == 1) & same-domain experience transfer: after a short dwell, gently raise hand
///   with ["好，幫我做", "這次不用", "以後別問"] (0 LLM tokens).
/// - First-visit unknown work windows/forms: zero-token noise filter + 5s dwell + work-signal check,
///   then at most ONE Simple-tier probe cached as Cold or Ignored(7d) so it never re-spends tokens.
/// - "這次不用" snoozes for 3h without killing the rule; "以後別問" mutes it; manually using the skill
///   again on that window/page auto-revives Muted/Disabled/Ignored rules back to Cold.
/// </summary>
public sealed class StateMachineAgent : IDisposable
{
    public const string ChoiceAccept = "好，幫我做";
    public const string ChoiceAcceptTransfer = "好，照上次做";
    public const string ChoiceSnooze = "這次不用";
    public const string ChoiceMute = "以後別問";

    private static readonly TimeSpan ColdDwellDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FirstVisitDwellDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GlobalFirstProbeCooldown = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> NoiseProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "aisecretary.desktop", "explorer", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
        "applicationframehost", "taskmgr", "lockapp", "textinputhost",
        "discord", "line", "telegram", "slack", "spotify", "steam", "steamwebhelper",
        "epicgameslauncher", "riotclientux", "battle.net", "vlc", "potplayermini64", "mpv",
        "code", "devenv", "rider64", "windowsterminal", "powershell", "pwsh", "cmd", "conhost", "wt"
    };

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    private static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "excel", "powerpnt", "winword", "outlook"
    };

    private static readonly string[] EntertainmentHosts =
    {
        "youtube.com", "youtu.be", "twitch.tv", "netflix.com", "bilibili.com", "bahamut.com.tw",
        "gamer.com.tw", "discord.com", "twitter.com", "x.com", "instagram.com", "facebook.com",
        "threads.net", "reddit.com", "tiktok.com", "spotify.com", "nicovideo.jp", "pixiv.net",
        "google.com", "bing.com", "duckduckgo.com", "github.com", "chatgpt.com", "claude.ai"
    };

    private static readonly Regex WorkTitleHintRegex = new(
        @"(登入|登錄|驗證碼|驗證|兩步驟|表單|申請|報銷|請假|問卷|預約|報名|login|sign[\s-]?in|verify|verification|otp|2fa|mfa|auth|form|apply|submit|checkout|register)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SqliteStore _store;
    private readonly WorkerAgent _worker;
    private readonly GuardService _guard;
    private readonly IModelGateway _model;
    private readonly BrowserFormBridgeService _browserBridge;
    private readonly ILogger<StateMachineAgent> _logger;

    private readonly object _latestLock = new();
    private CancellationTokenSource? _latestCts;
    private string? _lastFocusedTriggerKey;
    private DateTimeOffset _lastFocusedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFirstProbeAtUtc = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hitCooldowns = new(StringComparer.OrdinalIgnoreCase);

    public event Action<StateMachineProposal>? ProposalRequested;

    public StateMachineAgent(
        SqliteStore store,
        WorkerAgent worker,
        GuardService guard,
        IModelGateway model,
        BrowserFormBridgeService browserBridge,
        ILogger<StateMachineAgent> logger)
    {
        _store = store;
        _worker = worker;
        _guard = guard;
        _model = model;
        _browserBridge = browserBridge;
        _logger = logger;
    }

    public static string BuildTriggerKey(string? url, string processName, string title)
    {
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Regex.IsMatch(s, @"^(?:cht|chs|[a-z]{2}(?:-[a-z]{2,4})?|\d+)$", RegexOptions.IgnoreCase)
                    ? "*"
                    : s)
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

    public async Task HandleWindowFocusAsync(
        string conversationId,
        ExternalWindowTarget target,
        string? url,
        CancellationToken appCt = default)
    {
        var triggerKey = BuildTriggerKey(url, target.ProcessName, target.Title);

        // Latest-Wins: record latest focused trigger key and cancel any older in-flight dwell/replay.
        CancellationTokenSource linked;
        lock (_latestLock)
        {
            _lastFocusedTriggerKey = triggerKey;
            _lastFocusedAtUtc = DateTimeOffset.UtcNow;
            try { _latestCts?.Cancel(); } catch { }
            try { _latestCts?.Dispose(); } catch { }
            _latestCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            linked = _latestCts;
        }
        var ct = linked.Token;

        try
        {
            var now = DateTimeOffset.UtcNow;
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

                // Hot rule (Counter >= 2): run silently with 30s hit cooldown (0 LLM tokens)
                if (existing.PredictorCounter >= 2)
                {
                    if (_hitCooldowns.TryGetValue(triggerKey, out var lastHit) && (now - lastHit) < TimeSpan.FromSeconds(30))
                        return;
                    _hitCooldowns[triggerKey] = now;

                    await RecordOutcomeAsync(triggerKey, hit: true, estimatedTokensSaved: 350, ct);
                    await ExecuteSafeStepsAsync(conversationId, triggerKey, existing.StateName, steps, ct);
                    return;
                }

                // Cold rule (Counter == 1): wait dwell time, then gently raise hand (0 LLM tokens)
                if (existing.PredictorCounter == 1)
                {
                    await Task.Delay(ColdDwellDelay, ct);
                    if (ct.IsCancellationRequested) return;

                    // Prevent re-popping if user ignores the pill and Alt+Tabs right back.
                    await _store.UpsertAutoStateRuleAsync(existing with
                    {
                        CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(30),
                        LastSeenAt = DateTimeOffset.UtcNow
                    }, ct);

                    var msg = !string.IsNullOrWhiteSpace(existing.ProposalText)
                        ? existing.ProposalText!
                        : BuildDefaultProposalMessage(steps[0].Skill, target.Title);

                    RaiseProposal(triggerKey, target.Title, msg, new[] { ChoiceAccept, ChoiceSnooze, ChoiceMute });
                    return;
                }
            }

            // No exact rule: check same-domain coarse transfer (0 LLM tokens)
            var coarsePrefix = BuildCoarseTriggerKey(url);
            if (!string.IsNullOrWhiteSpace(coarsePrefix))
            {
                var allRules = await _store.GetAllAutoStateRulesAsync(ct);
                var donor = allRules.FirstOrDefault(r =>
                    !string.Equals(r.TriggerKey, triggerKey, StringComparison.OrdinalIgnoreCase) &&
                    r.TriggerKey.StartsWith(coarsePrefix, StringComparison.OrdinalIgnoreCase) &&
                    r.Status is "Hot" or "Cold" &&
                    r.PredictorCounter >= 1 &&
                    ParseSteps(r.StepsJson).Count > 0);

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
                        LastSeenAt: DateTimeOffset.UtcNow,
                        CooldownUntil: DateTimeOffset.UtcNow.AddMinutes(30),
                        ProposalText: $"這頁跟之前在同個網站用過的很像，要照上次那樣幫你處理（{DescribeSkill(donorSteps[0].Skill)}）嗎？");

                    await _store.UpsertAutoStateRuleAsync(transferred, ct);
                    RaiseProposal(triggerKey, target.Title, transferred.ProposalText!, new[] { ChoiceAcceptTransfer, ChoiceSnooze, ChoiceMute });
                    return;
                }
            }

            // Brand-new window/page: zero-token noise filter -> dwell -> work-signal check -> at most 1 Simple LLM probe
            await TryFirstVisitProbeAsync(triggerKey, target, url, ct);
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

    private async Task TryFirstVisitProbeAsync(
        string triggerKey,
        ExternalWindowTarget target,
        string? url,
        CancellationToken ct)
    {
        var proc = (target.ProcessName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(proc) || NoiseProcesses.Contains(proc))
            return;

        bool isBrowser = BrowserProcesses.Contains(proc);
        bool isOffice = OfficeProcesses.Contains(proc);
        if (!isBrowser && !isOffice)
            return;

        if (isBrowser)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;
            if (uri.Scheme is not ("http" or "https"))
                return;
            var host = uri.Host.ToLowerInvariant();
            if (EntertainmentHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
                return;
        }

        // Wait dwell time so rapid tab switching costs 0 tokens and never nags.
        await Task.Delay(FirstVisitDwellDelay, ct);
        if (ct.IsCancellationRequested) return;

        // Global first-visit probe rate limit.
        lock (_latestLock)
        {
            if ((DateTimeOffset.UtcNow - _lastFirstProbeAtUtc) < GlobalFirstProbeCooldown)
                return;
        }

        string? formHints = null;
        if (isBrowser)
        {
            BrowserFormContext? formCtx = null;
            try
            {
                formCtx = await _browserBridge.RequestActiveTabFormAsync(TimeSpan.FromMilliseconds(1500), ct);
            }
            catch { }

            int fieldCount = formCtx?.Fields?.Count ?? 0;
            bool titleMatchesWork = WorkTitleHintRegex.IsMatch(target.Title ?? "");

            // Only spend 1 Simple LLM call when there is a real work signal (form fields or auth/form title).
            if (fieldCount == 0 && !titleMatchesWork)
                return;

            if (fieldCount > 0)
            {
                var labels = formCtx!.Fields
                    .Select(f => string.IsNullOrWhiteSpace(f.Label) ? f.ControlType : f.Label)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(8);
                formHints = $"Detected {fieldCount} form field(s): {string.Join(", ", labels)}";
            }
            else
            {
                formHints = "Title suggests login/verification/form page (no extension form snapshot).";
            }
        }
        else if (isOffice)
        {
            formHints = $"Active desktop Office document ({proc}).";
        }

        lock (_latestLock)
        {
            _lastFirstProbeAtUtc = DateTimeOffset.UtcNow;
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(8));

        var sys = SystemAutomationPrompts.BuildFirstVisitProbeSystemPrompt();
        var user = SystemAutomationPrompts.BuildFirstVisitProbeUserPrompt(proc, target.Title ?? "", url, formHints);
        var resp = await _model.CompleteAsync(new[]
        {
            new ModelMessage("system", sys),
            new ModelMessage("user", user)
        }, ModelTier.Simple, probeCts.Token);

        var (shouldPropose, proposal, step) = ParseFirstVisitDecision(resp.Content);
        if (!shouldPropose || step is null || FilterSafeSteps(new List<CachedWorkflowStep> { step }).Count == 0)
        {
            // Remember "nothing to offer" for 7 days so revisiting costs 0 tokens.
            await _store.UpsertAutoStateRuleAsync(new AutoStateRuleRecord(
                TriggerKey: triggerKey,
                StateName: "ignored_first_visit",
                StepsJson: "[]",
                PredictorCounter: 0,
                HitCount: 0,
                MissCount: 0,
                Status: "Ignored",
                LastSeenAt: DateTimeOffset.UtcNow,
                CooldownUntil: DateTimeOffset.UtcNow.AddDays(7)), ct);
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
            LastSeenAt: DateTimeOffset.UtcNow,
            CooldownUntil: DateTimeOffset.UtcNow.AddMinutes(30),
            ProposalText: proposalText);

        await _store.UpsertAutoStateRuleAsync(rule, ct);
        RaiseProposal(triggerKey, target.Title, proposalText, new[] { ChoiceAccept, ChoiceSnooze, ChoiceMute });
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
            LastSeenAt = DateTimeOffset.UtcNow
        }, ct);
        await _store.RecordBranchOutcomeAsync(hit: true, estimatedTokensSaved: 350, ct);

        var observations = new List<string>();
        foreach (var step in steps.Take(2))
        {
            if (!_guard.IsSkillEnabled(step.Skill)) continue;
            await _worker.RunStepAsync(
                conversationId: conversationId,
                userPrompt: $"[auto_state_accepted:{existing.StateName}] {triggerKey}",
                skillName: step.Skill,
                args: step.Args,
                targetMailId: null,
                confirmAsync: (_, _) => Task.FromResult(false),
                onStatusAsync: _ => Task.CompletedTask,
                sources: new List<MailSourceRef>(),
                observations: observations,
                completedSteps: new List<string>(),
                ct: ct);
        }

        if (observations.Count == 0)
            return $"好，已經幫你處理好了（{DescribeSkill(steps[0].Skill)}），之後遇到這頁也會直接幫你留意。";

        var snippet = string.Join("\n", observations.Take(2));
        if (snippet.Length > 350) snippet = snippet[..350] + "…";
        return $"好，幫你處理完了，以後到這頁我也會主動留意：\n{snippet}";
    }

    /// <summary>
    /// User answered "這次不用": snooze temporarily without killing the rule.
    /// </summary>
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
            CooldownUntil = DateTimeOffset.UtcNow.Add(snooze),
            LastSeenAt = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// User answered "以後別問": mute proactive prompts, but allow auto-revive if user manually uses the skill later.
    /// </summary>
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
            LastSeenAt = DateTimeOffset.UtcNow
        }, ct);
        await _store.RecordBranchOutcomeAsync(hit: false, estimatedTokensSaved: 0, ct);
    }

    /// <summary>
    /// Passively observes a user-initiated single-skill execution on the recently focused external window/page.
    /// Zero LLM calls: revives Muted/Disabled/Ignored rules if the user changed their mind, and promotes repeated
    /// observations to Hot.
    /// </summary>
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
                         (DateTimeOffset.UtcNow - _lastFocusedAtUtc) <= TimeSpan.FromMinutes(5)
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
                // Auto-revive: user explicitly invoked the skill again on this page/window!
                nextCounter = 1;
                misses = 0;
            }
            else if (string.Equals(existing.StepsJson, stepsJson, StringComparison.Ordinal))
            {
                nextCounter = Math.Min(3, existing.PredictorCounter + 1);
            }
        }

        var stateName = $"learned_{skillName.Trim().ToLowerInvariant()}";
        var updated = new AutoStateRuleRecord(
            TriggerKey: triggerKey,
            StateName: stateName,
            StepsJson: stepsJson,
            PredictorCounter: nextCounter,
            HitCount: hits,
            MissCount: misses,
            Status: nextCounter >= 2 ? "Hot" : "Cold",
            LastSeenAt: DateTimeOffset.UtcNow,
            CooldownUntil: null,
            ProposalText: existing?.ProposalText);

        await _store.UpsertAutoStateRuleAsync(updated, ct);
    }

    public async Task RecordOutcomeAsync(string triggerKey, bool hit, long estimatedTokensSaved = 350, CancellationToken ct = default)
    {
        var existing = await _store.GetAutoStateRuleAsync(triggerKey, ct);
        if (existing is null) return;

        int nextCnt = hit
            ? Math.Min(3, existing.PredictorCounter + 1)
            : Math.Max(1, existing.PredictorCounter - 1);

        var nextStatus = nextCnt >= 2 ? "Hot" : "Cold";
        DateTimeOffset? cooldown = hit ? null : DateTimeOffset.UtcNow.AddHours(1);

        await _store.UpsertAutoStateRuleAsync(existing with
        {
            PredictorCounter = nextCnt,
            HitCount = hit ? existing.HitCount + 1 : existing.HitCount,
            MissCount = hit ? existing.MissCount : existing.MissCount + 1,
            Status = nextStatus,
            CooldownUntil = cooldown,
            LastSeenAt = DateTimeOffset.UtcNow
        }, ct);

        await _store.RecordBranchOutcomeAsync(hit, hit ? estimatedTokensSaved : 0, ct);
    }

    public Task<List<AutoStateRuleRecord>> GetAllRulesAsync(CancellationToken ct = default)
        => _store.GetAllAutoStateRulesAsync(ct);

    public Task<bool> DeleteRuleAsync(string triggerKey, CancellationToken ct = default)
        => _store.DeleteAutoStateRuleAsync(triggerKey, ct);

    private async Task ExecuteSafeStepsAsync(
        string conversationId,
        string triggerKey,
        string stateName,
        List<CachedWorkflowStep> steps,
        CancellationToken ct)
    {
        foreach (var step in steps.Take(2))
        {
            if (!_guard.IsSkillEnabled(step.Skill)) continue;
            try
            {
                await _worker.RunStepAsync(
                    conversationId: conversationId,
                    userPrompt: $"[auto_state:{stateName}] {triggerKey}",
                    skillName: step.Skill,
                    args: step.Args,
                    targetMailId: null,
                    confirmAsync: (_, _) => Task.FromResult(false),
                    onStatusAsync: _ => Task.CompletedTask,
                    sources: new List<MailSourceRef>(),
                    observations: new List<string>(),
                    completedSteps: new List<string>(),
                    ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[StateMachineAgent] step {Skill} failed for {Key}", step.Skill, triggerKey);
                await RecordOutcomeAsync(triggerKey, hit: false, estimatedTokensSaved: 0, CancellationToken.None);
            }
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

    private static (bool ShouldPropose, string? Proposal, CachedWorkflowStep? Step) ParseFirstVisitDecision(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, null, null);
        try
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

            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            bool should = root.TryGetProperty("should_propose", out var sp) && sp.ValueKind == JsonValueKind.True;
            if (!should) return (false, null, null);

            var skill = root.TryGetProperty("skill", out var sk) ? sk.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(skill)) return (false, null, null);

            var proposal = root.TryGetProperty("proposal", out var pr) ? pr.GetString()?.Trim() : null;
            var args = root.TryGetProperty("args", out var ag) && ag.ValueKind == JsonValueKind.Object
                ? ag.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            return (true, proposal, new CachedWorkflowStep(skill, args));
        }
        catch
        {
            return (false, null, null);
        }
    }

    private static string DescribeSkill(string skill) => skill.ToLowerInvariant() switch
    {
        "browser_form" => "讀取表單並協助預填",
        "manage_mail" => "同步收信確認驗證碼或郵件",
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
