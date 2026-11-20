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

/// <summary>
/// Pure sensor tracking recent external foreground windows (excluding this app),
/// reading local password-redacted UIA fields, and emitting debounced WindowFocused signals
/// with zero hardcoded business hooks.
/// </summary>
public sealed class ActivityMonitorService : IDisposable
{
    private readonly ILogger<ActivityMonitorService> _logger;
    private readonly UiLocalizer _localizer;
    private readonly object _lock = new();
    private readonly LinkedList<ExternalWindowTarget> _recentTargets = new();

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private nint _lastRawHwnd;
    private string? _lastVisitedTitle;

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

    public event Action<ExternalWindowTarget, string?>? WindowFocused;

    public bool ProactiveMonitoringEnabled { get; set; } = true;

    public ActivityMonitorService(UiLocalizer localizer, ILogger<ActivityMonitorService> logger)
    {
        _localizer = localizer;
        _logger = logger;
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        _loopTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    public static bool IsBrowserProcess(string? processName)
        => !string.IsNullOrWhiteSpace(processName) && BrowserProcesses.Contains(processName.Trim());

    public static string FormatActiveWindowState(ExternalWindowTarget? target)
    {
        if (target is null)
            return "(none)";
        var isBrowser = IsBrowserProcess(target.ProcessName);
        var labels = target.FormFieldLabels is { Count: > 0 }
            ? string.Join(", ", target.FormFieldLabels.Take(10))
            : "(not scanned)";
        return $"process: {target.ProcessName} | title: {target.Title} | is_browser: {isBrowser} | input_count: {target.VisibleInputCount} | uia_labels: {labels}";
    }

    public ExternalWindowTarget? GetLastExternalTarget()
    {
        lock (_lock)
            return _recentTargets.First?.Value;
    }

    public async Task<(bool Ok, string Message)> TryFillOtpInExternalWindowAsync(string code, string? serviceHint = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (false, await _localizer.TextAsync("OtpEmpty", "Verification code is empty.", Array.Empty<object?>(), ct));

        var targets = GetRecentExternalTargets();
        var target = targets.FirstOrDefault(t => !string.IsNullOrWhiteSpace(serviceHint) && SharesKeyword(t.Title, serviceHint))
                     ?? targets.FirstOrDefault();

        if (target is null)
            return (false, await _localizer.TextAsync("OtpTargetMissing", "No matching external browser or login window found.", Array.Empty<object?>(), ct));

        try
        {
            var root = AutomationElement.FromHandle(target.Hwnd);
            if (root is null)
                return (false, await _localizer.TextAsync("OtpWindowInaccessible", "Cannot access target window UI.", Array.Empty<object?>(), ct));

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
                return (false, await _localizer.TextAsync("OtpFieldNotFound", "No fillable input found in '{0}'; copied to clipboard instead.", new object?[] { Truncate(target.Title, 24) }, ct));

            SetForegroundWindow(target.Hwnd);
            if (chosen.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp && !vp.Current.IsReadOnly)
            {
                vp.SetValue(code);
                return (true, await _localizer.TextAsync("OtpFilled", "Filled verification code {0} into '{1}' (not auto-submitted).", new object?[] { code, Truncate(target.Title, 24) }, ct));
            }

            chosen.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("^a");
            System.Windows.Forms.SendKeys.SendWait(code);
            return (true, await _localizer.TextAsync("OtpTyped", "Entered verification code {0} into '{1}' (not auto-submitted).", new object?[] { code, Truncate(target.Title, 24) }, ct));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryFillOtpInExternalWindowAsync failed");
            return (false, await _localizer.TextAsync("OtpAutoFillFailed", "Auto-fill failed: {0}", new object?[] { ex.Message }, ct));
        }
    }

    public Task HandleBrowserPageSignalAsync(BrowserPageSignal signal, CancellationToken ct = default)
    {
        if (!ProactiveMonitoringEnabled || string.IsNullOrWhiteSpace(signal.Url) || signal.InputCount <= 0)
            return Task.CompletedTask;

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

        WindowFocused?.Invoke(target, signal.Url);
        return Task.CompletedTask;
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
                    _lastVisitedTitle = title;
                    _lastRawHwnd = hwnd;

                    var newTarget = new ExternalWindowTarget(hwnd, (int)pid, procName, title, now);
                    RecordTarget(newTarget);

                    if (ProactiveMonitoringEnabled && BrowserProcesses.Contains(procName) && !SensitiveProcesses.Contains(procName))
                    {
                        var capturedTitle = title;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(1200, ct);
                            if (ct.IsCancellationRequested ||
                                _lastRawHwnd != hwnd ||
                                !string.Equals(_lastVisitedTitle, capturedTitle, StringComparison.Ordinal))
                                return;

                            var enriched = await RefreshExternalTargetUiaAsync(hwnd, ct) ?? newTarget;
                            if ((enriched.FormFieldLabels is { Count: > 0 }) || enriched.VisibleInputCount > 0)
                                WindowFocused?.Invoke(enriched, null);
                        }, ct);
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
