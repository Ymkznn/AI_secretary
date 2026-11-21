using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;

namespace AiSecretary.Desktop.Agent;

public enum CommandRiskLevel
{
    Low,
    High
}

public sealed class GuardDecision
{
    public bool Allowed { get; init; }
    public bool RequiresUserConfirmation { get; init; }
    public CommandRiskLevel RiskLevel { get; init; } = CommandRiskLevel.Low;
    public string Reason { get; init; } = "";
}

public sealed record GuardAuditEntry(
    DateTimeOffset Timestamp,
    string SkillName,
    string Target,
    string Action,
    string Reason);

/// <summary>
/// Developer-managed security boundary, REST API permission gate,
/// two-tier command risk gate (High = deny, Low = per-execution ask), and isolated audit log (guard_audit.db).
/// Skill enable/disable state is backed dynamically by SQLite (<see cref="ISkillCatalogLookup"/>);
/// policy rules are loaded from read-only guard_policy.json in AppContext.BaseDirectory.
/// </summary>
public sealed class GuardService
{
    private readonly ConcurrentQueue<GuardAuditEntry> _auditRing = new();
    private readonly GuardAuditStore _auditStore;
    private readonly InteractionGateway _interaction;
    private readonly AppSettingsService _settings;
    private readonly UiLocalizer _localizer;
    private readonly ILogger<GuardService> _logger;
    private readonly IConfiguration _config;
    private readonly string _ewsHost;
    private readonly HashSet<string> _allowedBackgroundServices;
    private ISkillCatalogLookup? _catalog;
    // 僅限單一邏輯任務（taskId）期間有效的低風險指令授權快取（可跨多輪對話與 Session 輪替，切換新任務或關閉即清除）
    private readonly ConcurrentDictionary<string, HashSet<string>> _taskApprovals = new();

    private readonly Dictionary<string, (string Method, string Host, string PathPrefix)> _restPermissions;
    private readonly IReadOnlyList<Regex> _destructivePatterns;
    private readonly IReadOnlyList<Regex> _secretAccessPatterns;
    private readonly IReadOnlyList<Regex> _networkExfilPatterns;

    public int MaxWorkerStepsPerTask { get; }
    public int MaxPlannerRounds { get; }

    public void BindSkillCatalog(ISkillCatalogLookup catalog) => _catalog = catalog;

    public GuardService(
        IConfiguration config,
        GuardAuditStore auditStore,
        InteractionGateway interaction,
        AppSettingsService settings,
        UiLocalizer localizer,
        ILogger<GuardService> logger)
    {
        _auditStore = auditStore;
        _interaction = interaction;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;
        _config = config;

        (_restPermissions, _destructivePatterns, _secretAccessPatterns, _networkExfilPatterns) = LoadDeveloperPolicy();

        var bgServices = config.GetSection("Guard:AllowedBackgroundServices").Get<string[]>() ?? Array.Empty<string>();
        _allowedBackgroundServices = new HashSet<string>(bgServices, StringComparer.OrdinalIgnoreCase);

        MaxWorkerStepsPerTask = config.GetValue<int>("Guard:MaxWorkerStepsPerTask", 15);
        MaxPlannerRounds = Math.Clamp(config.GetValue<int>("Guard:MaxPlannerRounds", 5), 1, 12);

        var ewsUrl = config.GetValue<string>("Ews:Url") ?? "";
        _ewsHost = Uri.TryCreate(ewsUrl, UriKind.Absolute, out var u) ? u.Host : "";
    }

    public bool IsSkillEnabled(string skillName) => _catalog?.IsEnabled(skillName) ?? false;

    public IReadOnlyList<GuardAuditEntry> GetRecentAuditLogs() => _auditRing.ToArray();

    public GuardDecision CheckSkillExecution(string skillName, bool userExplicitlyConfirmed = false)
    {
        var rec = _catalog?.Resolve(skillName);
        if (rec is null)
            return Block(skillName, skillName, $"Skill '{skillName}' is not registered in the skill catalog.");

        if (!rec.IsEnabled)
        {
            return Block(rec.Name, rec.Name, _localizer.TextCached("GuardSkillDisabled", "Skill '{0}' is disabled.", rec.Name));
        }

        RecordAudit(rec.Name, rec.Name, "Allow", "Skill permitted");
        return new GuardDecision { Allowed = true };
    }

    /// <summary>
    /// Fine-grained REST API / SOAP operation permission gate.
    /// Enforces exact permission ID, HTTP method, host allowlist, and path prefix.
    /// </summary>
    public GuardDecision CheckRestApiAccess(string permissionId, string httpMethod, string requestUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
            return Block("rest_api", requestUrl, $"Invalid API URL: {requestUrl}");

        if (uri.Scheme != Uri.UriSchemeHttps)
            return Block("rest_api", requestUrl, $"Only HTTPS is allowed (rejected {uri.Scheme}).");

        if (permissionId.StartsWith("ews.", StringComparison.OrdinalIgnoreCase))
        {
            if (permissionId is not ("ews.find_item" or "ews.get_item"))
                return Block("rest_api", $"{permissionId} {requestUrl}", $"Unauthorized EWS operation: {permissionId}");

            if (!httpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals(_ewsHost, StringComparison.OrdinalIgnoreCase))
            {
                return Block("rest_api", $"{httpMethod} {requestUrl}", $"EWS host or method blocked ({uri.Host}).");
            }

            RecordAudit(permissionId, $"{httpMethod} {uri.Host}{uri.AbsolutePath}", "Allow", "EWS read-only op");
            return new GuardDecision { Allowed = true };
        }

        if (!_restPermissions.TryGetValue(permissionId, out var rule))
            return Block("rest_api", $"{permissionId} {requestUrl}", $"Unauthorized REST permission ID: {permissionId}");

        if (!httpMethod.Equals(rule.Method, StringComparison.OrdinalIgnoreCase))
            return Block("rest_api", $"{httpMethod} {requestUrl}", $"REST permission {permissionId} allows {rule.Method}, rejected {httpMethod}.");

        if (!uri.Host.Equals(rule.Host, StringComparison.OrdinalIgnoreCase))
            return Block("rest_api", requestUrl, $"REST permission {permissionId} rejected host: {uri.Host}.");

        if (!uri.AbsolutePath.StartsWith(rule.PathPrefix, StringComparison.OrdinalIgnoreCase))
            return Block("rest_api", requestUrl, $"REST permission {permissionId} path mismatch: {uri.AbsolutePath}.");

        if (uri.AbsolutePath.Contains("/send", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/modify", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/trash", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/delete", StringComparison.OrdinalIgnoreCase))
        {
            return Block("rest_api", requestUrl, "Read-only REST permission forbids mutating operations.");
        }

        RecordAudit(permissionId, $"{httpMethod} {uri.Host}{uri.AbsolutePath}", "Allow", "REST API permitted");
        return new GuardDecision { Allowed = true };
    }

    public GuardDecision CheckWatcherRegistration(string skillName, string kind, string url, int intervalSeconds, int currentActiveCount)
    {
        int maxWatchers = Math.Clamp(_config.GetValue<int>("Guard:MaxActiveWatchers", 5), 1, 50);
        int minInterval = Math.Clamp(_config.GetValue<int>("Guard:MinWatcherIntervalSeconds", 60), 15, 3600);

        if (currentActiveCount >= maxWatchers)
            return Block(skillName, url, $"Active watcher limit reached (max {maxWatchers}).");

        if (intervalSeconds < minInterval)
            return Block(skillName, url, $"Watcher interval cannot be below {minInterval} seconds.");

        var normKind = kind.Trim().ToLowerInvariant();
        if (normKind is not ("http_text" or "http_json" or "browser_dom"))
            return Block(skillName, kind, $"Unsupported watcher kind: {kind}");

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Block(skillName, url ?? "", $"Invalid watcher URL: {url}");

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return Block(skillName, url!, "Loopback and local hostnames are blocked for watchers.");

        if (IPAddress.TryParse(uri.Host, out var ip) && WebFetchSkill.IsPrivateOrLoopback(ip))
            return Block(skillName, url!, "Private and loopback IP ranges are blocked for watchers.");

        RecordAudit(skillName, $"{normKind} {uri.Host}", "Allow", "Watcher policy permitted");
        return new GuardDecision { Allowed = true };
    }

    public GuardDecision CheckBackgroundServiceStart(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey) || !_allowedBackgroundServices.Contains(serviceKey.Trim()))
            return Block("background_service", serviceKey, $"Background service '{serviceKey}' is not in Guard:AllowedBackgroundServices.");

        RecordAudit("background_service", serviceKey, "Allow", "Whitelisted background service");
        return new GuardDecision { Allowed = true };
    }

    private static readonly Regex DangerousExcelFormulaRegex = new(
        @"^[\s\+\-=@]*(?:cmd|msexcel|exec)\s*\||=\s*(?:WEBSERVICE|REGISTER\.ID|CALL)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GuardDecision CheckExcelFormula(string? value, string? skillName = null)
    {
        if (string.IsNullOrEmpty(value)) return new GuardDecision { Allowed = true };
        var auditKey = string.IsNullOrWhiteSpace(skillName) ? "office_formula" : skillName;
        if (DangerousExcelFormulaRegex.IsMatch(value))
            return BlockHighRisk(auditKey, value.Length > 80 ? value[..80] : value, "Blocked high-risk DDE or external-fetch Excel formula.");

        return new GuardDecision { Allowed = true };
    }

    /// <summary>
    /// Pre-execution intercept rule for custom skills / PowerShell commands (developer-managed only).
    /// High risk: immediately denied without prompting.
    /// Low risk: requires explicit per-execution user confirmation (never "always allow").
    /// </summary>
    public GuardDecision CheckCommandExecution(string skillName, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return BlockHighRisk(skillName, "", "Command cannot be empty.");

        if (_destructivePatterns.Any(pat => pat.IsMatch(command)))
            return BlockHighRisk(skillName, command, "Blocked by destructive command rule.");

        bool accessesSecret = _secretAccessPatterns.Any(pat => pat.IsMatch(command));
        bool hasNetExfil = _networkExfilPatterns.Any(pat => pat.IsMatch(command));

        if (accessesSecret && hasNetExfil)
            return BlockHighRisk(skillName, command, "Blocked by secret exfiltration rule.");

        if (accessesSecret)
            return BlockHighRisk(skillName, command, "Custom skills are forbidden from accessing local credentials, keys, or databases.");

        var preview = command.Length > 120 ? command[..120] + "…" : command;
        RecordAudit(skillName, preview, "RequireConfirm", "Low-risk command requires per-execution confirmation");
        return new GuardDecision
        {
            Allowed = false,
            RequiresUserConfirmation = true,
            RiskLevel = CommandRiskLevel.Low,
            Reason = "Low-risk commands require per-execution user confirmation."
        };
    }

    /// <summary>清除指定邏輯任務（taskId）暫存的授權紀錄。</summary>
    public void ClearTaskApprovals(string? taskId)
    {
        if (!string.IsNullOrWhiteSpace(taskId))
            _taskApprovals.TryRemove(taskId, out _);
    }

    /// <summary>檢查自訂技能參數長度與殼層控制字元。</summary>
    public void ValidateCustomSkillArgs(string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs))
            return;

        var maxArgLen = Math.Clamp(_settings.Current.CustomSkill.MaxArgLength, 64, 4000);
        var trimmed = extraArgs.Trim();
        if (trimmed.Length > maxArgLen || Regex.IsMatch(trimmed, @"[;|&`<>]|(\$\()"))
            throw new UnauthorizedAccessException($"Custom skill args cannot exceed {maxArgLen} chars or contain shell control characters.");
    }

    /// <summary>
    /// 執行前統一授權檢查：高風險直接拒絕，低風險則檢查本邏輯任務是否已授權，否則經 InteractionGateway 詢問使用者。
    /// </summary>
    public async Task<(bool Approved, string? DenialReason)> AuthorizeCommandAsync(
        string skillName,
        string command,
        string? extraArgs,
        string sandboxRoot,
        string? taskId,
        CancellationToken ct = default)
    {
        var guardCheck = CheckCommandExecution($"custom:{skillName}", command);
        if (!guardCheck.Allowed && !guardCheck.RequiresUserConfirmation)
            throw new UnauthorizedAccessException(guardCheck.Reason);

        ValidateCustomSkillArgs(extraArgs);

        if (guardCheck.RequiresUserConfirmation && !IsApprovedForTask(taskId, skillName))
        {
            var execTitle = await _localizer.TextAsync("ExecuteSkillTitle", "Confirm Low-Risk Command", Array.Empty<object?>(), ct);
            var argsLine = string.IsNullOrWhiteSpace(extraArgs)
                ? ""
                : await _localizer.TextAsync("ExecuteSkillArgsLine", "\nArgs: {0}", new object?[] { extraArgs }, ct);
            var execPrompt = await _localizer.TextAsync(
                "ExecuteSkillPrompt",
                "Assistant requests to run a custom command skill (per-execution confirmation):\n\nSkill: {0}\nCommand: {1}{2}\nWorking directory: {3}\n\nAllow running once?",
                new object?[] { skillName, command, argsLine, sandboxRoot },
                ct);
            var decision = await _interaction.RequestCommandApprovalAsync(execTitle, execPrompt, ct);
            if (decision == CommandApprovalDecision.Deny)
                return (false, $"User declined running skill '{skillName}'.");
            if (decision == CommandApprovalDecision.AllowForTask)
                ApproveForTask(taskId, skillName);
        }

        return (true, null);
    }

    private bool IsApprovedForTask(string? taskId, string skillName)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        if (!_taskApprovals.TryGetValue(taskId, out var set)) return false;
        lock (set) return set.Contains(skillName);
    }

    private void ApproveForTask(string? taskId, string skillName)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        var set = _taskApprovals.GetOrAdd(taskId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (set) set.Add(skillName);
    }

    private GuardDecision BlockHighRisk(string skillName, string target, string reason)
    {
        RecordAudit(skillName, target, "DenyHighRisk", reason);
        _logger.LogWarning("Guard DENIED high-risk [{Skill}] target={Target}: {Reason}", skillName, target, reason);
        return new GuardDecision
        {
            Allowed = false,
            RequiresUserConfirmation = false,
            RiskLevel = CommandRiskLevel.High,
            Reason = reason
        };
    }

    private GuardDecision Block(string skillName, string target, string reason)
    {
        RecordAudit(skillName, target, "Block", reason);
        _logger.LogWarning("Guard BLOCKED [{Skill}] target={Target}: {Reason}", skillName, target, reason);
        return new GuardDecision { Allowed = false, Reason = reason };
    }

    private void RecordAudit(string skillName, string target, string action, string reason)
    {
        var entry = new GuardAuditEntry(DateTimeOffset.Now, skillName, target, action, reason);
        _auditRing.Enqueue(entry);
        while (_auditRing.Count > 100) _auditRing.TryDequeue(out _);

        _ = Task.Run(async () =>
        {
            try { await _auditStore.InsertAuditAsync(skillName, target, action, reason); }
            catch { }
        });
    }

    private static (
        Dictionary<string, (string Method, string Host, string PathPrefix)> RestRules,
        IReadOnlyList<Regex> Destructive,
        IReadOnlyList<Regex> Secret,
        IReadOnlyList<Regex> Exfil) LoadDeveloperPolicy()
    {
        var asm = typeof(GuardService).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("guard_policy.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded guard_policy.json resource not found.");

        using var stream = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException("Unable to open embedded guard_policy.json stream.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var rest = new Dictionary<string, (string Method, string Host, string PathPrefix)>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("RestPermissions", out var rpEl) && rpEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in rpEl.EnumerateObject())
            {
                var method = prop.Value.TryGetProperty("Method", out var m) ? m.GetString() ?? "GET" : "GET";
                var host = prop.Value.TryGetProperty("Host", out var h) ? h.GetString() ?? "" : "";
                var path = prop.Value.TryGetProperty("PathPrefix", out var p) ? p.GetString() ?? "/" : "/";
                if (!string.IsNullOrWhiteSpace(host))
                    rest[prop.Name] = (method, host, path);
            }
        }

        TryReadRegexArray(root, "DestructiveCommandPatterns", out var destructiveList);
        TryReadRegexArray(root, "SecretAccessPatterns", out var secretList);
        TryReadRegexArray(root, "NetworkExfilPatterns", out var exfilList);

        static IReadOnlyList<Regex> CompileAll(IEnumerable<string> patterns)
            => patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToList();

        return (rest, CompileAll(destructiveList), CompileAll(secretList), CompileAll(exfilList));
    }

    private static bool TryReadRegexArray(JsonElement root, string propName, out List<string> patterns)
    {
        patterns = new List<string>();
        if (!root.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                patterns.Add(s);
        }
        return patterns.Count > 0;
    }
}
