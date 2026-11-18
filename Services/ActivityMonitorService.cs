using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

public sealed record ExternalWindowTarget(
    nint Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    DateTimeOffset CapturedAt,
    bool IsFormLike = false,
    IReadOnlyList<string>? FormFieldLabels = null,
    int VisibleInputCount = 0);

public sealed record ProactiveSuggestionEvent(
    string Kind, // "form_stuck" | "switch_back"
    ExternalWindowTarget Target,
    string SuggestedMessage);

/// <summary>
/// Tracks recent external foreground windows (explicitly excluding this app so focus-stealing when typing
/// never targets the secretary itself), performs zero-token local UIA form/focus detection with password redaction,
/// and raises debounced proactive assistance suggestions.
/// </summary>
public sealed class ActivityMonitorService : IDisposable
{
    private readonly ILogger<ActivityMonitorService> _logger;
    private readonly PageIntentPredictorService _pagePredictor;
    private readonly object _lock = new();
    private readonly LinkedList<ExternalWindowTarget> _recentTargets = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _titleCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mutedTitles = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private nint _lastRawHwnd;
    private DateTimeOffset _lastHwndEnteredAt = DateTimeOffset.UtcNow;
    private bool _suggestedForCurrentDwell;
    private string? _lastVisitedTitle;
    private string? _previousVisitedTitle;
    private DateTimeOffset _lastSwitchAt = DateTimeOffset.MinValue;

    private static readonly HashSet<string> SensitiveProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1password", "bitwarden", "keepass", "keepassxc", "lastpass", "dashlane", "explorer"
    };

    private static readonly HashSet<string> IgnoredWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "TaskListThumbnailWnd"
    };

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi"
    };

    public event Action<ProactiveSuggestionEvent>? ProactiveSuggestionRaised;
    public event Action<ExternalWindowTarget, string>? VerificationPageEntered;

    /// <summary>
    /// Language-agnostic AI semantic classifier: ("process", "title", fieldLabels) -> "otp_wait" | "form_fill" | "media_or_idle".
    /// </summary>
    public Func<string, string, IReadOnlyList<string>, CancellationToken, Task<string>>? SemanticClassifyAsync { get; set; }

    private readonly ConcurrentDictionary<string, string> _intentCache = new(StringComparer.OrdinalIgnoreCase);

    public bool ProactiveMonitoringEnabled { get; set; } = true;

    public ActivityMonitorService(PageIntentPredictorService pagePredictor, ILogger<ActivityMonitorService> logger)
    {
        _pagePredictor = pagePredictor;
        _logger = logger;
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        _loopTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    public ExternalWindowTarget? GetLastExternalTarget()
    {
        lock (_lock)
            return _recentTargets.First?.Value;
    }

    public bool HasMatchingVerificationWindow(string? titleHint, string? senderHint)
    {
        var targets = GetRecentExternalTargets();
        foreach (var t in targets)
        {
            if (_intentCache.TryGetValue(t.Title, out var cachedIntent) && cachedIntent == "otp_wait")
                return true;
            if (!string.IsNullOrWhiteSpace(titleHint) && SharesKeyword(t.Title, titleHint))
                return true;
            if (!string.IsNullOrWhiteSpace(senderHint) && SharesKeyword(t.Title, senderHint))
                return true;
        }
        return false;
    }

    public (bool Ok, string Message) TryFillOtpInExternalWindow(string code, string? serviceHint = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (false, "驗證碼為空。");

        var targets = GetRecentExternalTargets();
        var target = targets.FirstOrDefault(t =>
                         (!string.IsNullOrWhiteSpace(serviceHint) && SharesKeyword(t.Title, serviceHint))
                         || (_intentCache.TryGetValue(t.Title, out var ci) && ci == "otp_wait"))
                     ?? targets.FirstOrDefault();

        if (target is null)
            return (false, "找不到對應的外部瀏覽器或登入視窗。");

        try
        {
            var root = AutomationElement.FromHandle(target.Hwnd);
            if (root is null)
                return (false, "無法存取目標視窗介面。");

            var doc = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)) ?? root;

            var edits = doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            AutomationElement? chosen = null;

            for (int i = 0; i < edits.Count; i++)
            {
                var el = edits[i];
                if (!el.Current.IsEnabled || el.Current.IsPassword) continue;
                chosen = el;
                break;
            }

            if (chosen is null)
                return (false, $"在「{Truncate(target.Title, 24)}」找不到可填入的輸入框，已改複製到剪貼簿。");

            SetForegroundWindow(target.Hwnd);
            if (chosen.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp && !vp.Current.IsReadOnly)
            {
                vp.SetValue(code);
                return (true, $"已將驗證碼 {code} 填入「{Truncate(target.Title, 24)}」（未自動送出）。");
            }

            chosen.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("^a");
            System.Windows.Forms.SendKeys.SendWait(code);
            return (true, $"已將驗證碼 {code} 輸入「{Truncate(target.Title, 24)}」（未自動送出）。");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryFillOtpInExternalWindow failed");
            return (false, $"自動填入失敗：{ex.Message}");
        }
    }

    public async Task HandleBrowserPageSignalAsync(BrowserPageSignal signal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signal.Url) || signal.InputCount <= 0)
            return;

        var title = string.IsNullOrWhiteSpace(signal.Title) ? signal.Url : signal.Title.Trim();
        var current = GetLastExternalTarget();
        var target = new ExternalWindowTarget(
            Hwnd: current?.Hwnd ?? IntPtr.Zero,
            ProcessId: current?.ProcessId ?? 0,
            ProcessName: current?.ProcessName ?? "browser",
            Title: title,
            CapturedAt: DateTimeOffset.UtcNow,
            IsFormLike: signal.InputCount >= 4,
            FormFieldLabels: signal.Labels ?? Array.Empty<string>(),
            VisibleInputCount: signal.InputCount);

        var (patternKey, intent) = await ClassifyTargetIntentAsync(signal.Url, target, ct);
        if (intent == "otp_wait")
        {
            VerificationPageEntered?.Invoke(target, patternKey);
        }
    }

    private async Task<(string PatternKey, string Intent)> ClassifyTargetIntentAsync(
        string? url,
        ExternalWindowTarget target,
        CancellationToken ct)
    {
        var (patternKey, predictedIntent, isHighConf) = await _pagePredictor.TryPredictAsync(url, target.ProcessName, target.Title, ct);
        if (isHighConf && predictedIntent is not null)
        {
            _intentCache[target.Title] = predictedIntent;
            return (patternKey, predictedIntent);
        }

        if (_intentCache.TryGetValue(target.Title, out var cached))
            return (patternKey, cached);

        if (SemanticClassifyAsync is null)
            return (patternKey, "media_or_idle");

        var labels = target.FormFieldLabels ?? Array.Empty<string>();
        if (labels.Count == 0 && target.VisibleInputCount <= 0)
            return (patternKey, "media_or_idle");

        var intent = await SemanticClassifyAsync(target.ProcessName, target.Title, labels, ct);
        _intentCache[target.Title] = intent;
        await _pagePredictor.RecordAiClassificationAsync(patternKey, intent, ct);
        return (patternKey, intent);
    }

    private static bool SharesKeyword(string windowTitle, string hint)
    {
        foreach (var token in hint.Split(new[] { ' ', '-', '_', '|', '(', ')', '（', '）', '/', ':' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 3 && windowTitle.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public IReadOnlyList<ExternalWindowTarget> GetRecentExternalTargets()
    {
        lock (_lock)
            return _recentTargets.ToList();
    }

    public void MuteWindowTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        lock (_lock)
            _mutedTitles.Add(title.Trim());
    }

    /// <summary>
    /// Reads lightweight, password-redacted UIA form labels from the specified external window handle.
    /// Safe to run on demand without taking screenshots.
    /// </summary>
    public async Task<ExternalWindowTarget?> RefreshExternalTargetUiaAsync(nint hwnd, CancellationToken ct = default)
    {
        if (hwnd == IntPtr.Zero) return null;
        ExternalWindowTarget? existing;
        lock (_lock)
            existing = _recentTargets.FirstOrDefault(t => t.Hwnd == hwnd);

        if (existing is null) return null;
        if (SensitiveProcesses.Contains(existing.ProcessName)) return existing;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1500);
            var (isForm, labels, inputCount) = await Task.Run(() => InspectWindowUia(hwnd), timeoutCts.Token);
            var updated = existing with
            {
                IsFormLike = isForm,
                FormFieldLabels = labels,
                VisibleInputCount = inputCount,
                CapturedAt = DateTimeOffset.UtcNow
            };
            RecordTarget(updated);
            return updated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "UIA inspection skipped for hwnd={Hwnd}", hwnd);
            return existing;
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var selfPid = Environment.ProcessId;
        var lastUiaCheckAt = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(450, ct);

                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) continue;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == selfPid)
                    continue; // Critical: never overwrite LastExternalTarget with our own UI window!

                var cls = GetWindowClass(hwnd);
                if (IgnoredWindowClasses.Contains(cls))
                    continue;

                var title = GetWindowTextSafe(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var procName = GetProcessNameSafe((int)pid);
                if (string.IsNullOrWhiteSpace(procName))
                    continue;

                var now = DateTimeOffset.UtcNow;
                bool windowChanged = hwnd != _lastRawHwnd || !string.Equals(title, _lastVisitedTitle, StringComparison.Ordinal);

                if (windowChanged)
                {
                    bool isSwitchBack = _previousVisitedTitle is not null
                        && string.Equals(title, _previousVisitedTitle, StringComparison.Ordinal)
                        && (now - _lastSwitchAt) < TimeSpan.FromSeconds(75);

                    _previousVisitedTitle = _lastVisitedTitle;
                    _lastVisitedTitle = title;
                    _lastRawHwnd = hwnd;
                    _lastHwndEnteredAt = now;
                    _lastSwitchAt = now;
                    _suggestedForCurrentDwell = false;

                    var newTarget = new ExternalWindowTarget(hwnd, (int)pid, procName, title, now);
                    RecordTarget(newTarget);

                    if (BrowserProcesses.Contains(procName) && !SensitiveProcesses.Contains(procName))
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(1200, ct);
                            if (ct.IsCancellationRequested || _lastRawHwnd != hwnd) return;
                            var enriched = await RefreshExternalTargetUiaAsync(hwnd, ct) ?? newTarget;
                            var (patternKey, intent) = await ClassifyTargetIntentAsync(url: null, enriched, ct);
                            if (intent == "otp_wait")
                            {
                                VerificationPageEntered?.Invoke(enriched, patternKey);
                            }
                            else if (intent == "form_fill" && isSwitchBack && CanSuggest(title, DateTimeOffset.UtcNow) && !_suggestedForCurrentDwell)
                            {
                                _suggestedForCurrentDwell = true;
                                _titleCooldowns[title] = DateTimeOffset.UtcNow;
                                ProactiveSuggestionRaised?.Invoke(new ProactiveSuggestionEvent(
                                    "switch_back",
                                    enriched,
                                    $"剛才是在找「{Truncate(title, 30)}」要填的資料嗎？需要我幫您整理或預填嗎？"));
                            }
                        }, ct);
                    }
                    continue;
                }

                if (!ProactiveMonitoringEnabled || _suggestedForCurrentDwell || SensitiveProcesses.Contains(procName))
                    continue;

                var dwell = now - _lastHwndEnteredAt;
                if (BrowserProcesses.Contains(procName) && dwell >= TimeSpan.FromSeconds(15) && (now - lastUiaCheckAt) >= TimeSpan.FromSeconds(5))
                {
                    lastUiaCheckAt = now;
                    var target = await RefreshExternalTargetUiaAsync(hwnd, ct);
                    if (target is not null && target.IsFormLike && CanSuggest(title, now))
                    {
                        var (_, intent) = await ClassifyTargetIntentAsync(url: null, target, ct);
                        if (intent == "form_fill")
                        {
                            _suggestedForCurrentDwell = true;
                            _titleCooldowns[title] = now;
                            ProactiveSuggestionRaised?.Invoke(new ProactiveSuggestionEvent(
                                "form_stuck",
                                target,
                                $"看到您在「{Truncate(title, 30)}」停留了一會兒，需要我根據記憶幫您整理填寫建議嗎？"));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ActivityMonitorService tick error");
            }
        }
    }

    private bool CanSuggest(string title, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_mutedTitles.Contains(title.Trim()))
                return false;
        }
        return !_titleCooldowns.TryGetValue(title, out var last) || (now - last) >= TimeSpan.FromMinutes(6);
    }

    private void RecordTarget(ExternalWindowTarget target)
    {
        lock (_lock)
        {
            var existingNode = _recentTargets.FirstOrDefault(t => t.Hwnd == target.Hwnd);
            if (existingNode is not null)
                _recentTargets.Remove(existingNode);
            _recentTargets.AddFirst(target);
            while (_recentTargets.Count > 5)
                _recentTargets.RemoveLast();
        }
    }

    private static (bool IsForm, IReadOnlyList<string> Labels, int VisibleInputCount) InspectWindowUia(nint hwnd)
    {
        var labels = new List<string>();
        int visibleInputCount = 0;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return (false, labels, 0);

            // For browsers, scope strictly to the web Document subtree so the address bar,
            // tab search, and toolbar controls are never miscounted as form fields.
            var searchRoot = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)) ?? root;

            var cond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));

            var matches = searchRoot.FindAll(TreeScope.Descendants, cond);
            for (int i = 0; i < matches.Count && visibleInputCount < 25; i++)
            {
                var el = matches[i];
                if (!el.Current.IsEnabled || el.Current.IsPassword)
                    continue; // Redact password inputs completely

                visibleInputCount++;
                var name = el.Current.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !labels.Contains(name) && labels.Count < 15)
                    labels.Add(name);
            }
        }
        catch
        {
            // UIA access may fail for elevated or closed windows; fail safely.
        }
        return (visibleInputCount >= 4, labels, visibleInputCount);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string GetWindowTextSafe(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static string GetWindowClass(nint hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessNameSafe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
}
