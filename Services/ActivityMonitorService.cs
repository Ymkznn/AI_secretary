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

public sealed record CachedBrowserPageSignal(
    string Url,
    string Title,
    int InputCount,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Headings,
    string Excerpt,
    DateTimeOffset CapturedAt,
    bool HasOneTimeCodeInput = false,
    bool HasFocusedInput = false);

public sealed record DesktopSelectionSnapshot(
    string? SelectionText,
    string? FocusedElementLabel,
    string? FocusedControlType);

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
    private CachedBrowserPageSignal? _lastBrowserPageSignal;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private nint _lastRawHwnd;
    private string? _lastVisitedTitle;
    private bool _wasSelfForeground;
    private int _uiaSelectionInFlight;

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
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"
    };

    public event Action<ExternalWindowTarget, string?>? WindowFocused;
    public event Action<CachedBrowserPageSignal>? BrowserPageSignalReceived;

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

    public CachedBrowserPageSignal? GetLastBrowserPageSignal()
    {
        lock (_lock)
            return _lastBrowserPageSignal;
    }

    /// <summary>
    /// Reads ONLY explicitly highlighted selection text (via UIA TextPattern.GetSelection) and safe accessible
    /// focus label from a non-sensitive desktop window, strictly bounded by <paramref name="timeout"/> (default 300ms).
    /// NEVER reads password fields and NEVER reads unselected input field values (ValuePattern).
    /// </summary>
    public async Task<DesktopSelectionSnapshot?> TryReadDesktopSelectionAsync(
        ExternalWindowTarget target,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (target.Hwnd == IntPtr.Zero || SensitiveProcesses.Contains(target.ProcessName))
            return null;

        // Single-flight guard: if a previous UIA inspection on a large document is still unwinding after timeout,
        // skip this attempt instead of piling up background thread-pool work.
        if (Interlocked.CompareExchange(ref _uiaSelectionInFlight, 1, 0) != 0)
            return null;

        try
        {
            var work = Task.Run(() =>
            {
                try
                {
                    return ReadDesktopSelectionSync(target.Hwnd, target.ProcessId);
                }
                finally
                {
                    Interlocked.Exchange(ref _uiaSelectionInFlight, 0);
                }
            });
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var completed = await Task.WhenAny(work, Task.Delay(timeout, delayCts.Token));
            if (completed == work)
            {
                delayCts.Cancel();
                return await work;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static DesktopSelectionSnapshot? ReadDesktopSelectionSync(nint hwnd, int targetPid)
    {
        try
        {
            string? selection = null;
            string? focusedLabel = null;
            string? focusedType = null;

            // 1) Inspect target window's Document / Edit controls via TextPattern.GetSelection() ONLY
            //    (works for Word, Notepad, VS Code, PDF readers even after user focused AI Secretary).
            var root = AutomationElement.FromHandle(hwnd);
            if (root is not null)
            {
                var doc = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                if (doc is not null)
                    selection = TryExtractSafeSelection(doc);

                if (string.IsNullOrWhiteSpace(selection))
                {
                    var edit = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    if (edit is not null)
                        selection = TryExtractSafeSelection(edit);
                }
            }

            // 2) Inspect current AutomationElement.FocusedElement only if it still belongs to targetPid & is NOT password.
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is not null && focused.Current.ProcessId == targetPid && !focused.Current.IsPassword)
                {
                    if (string.IsNullOrWhiteSpace(selection))
                        selection = TryExtractSafeSelection(focused);

                    var name = focused.Current.Name?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                        focusedLabel = name.Length <= 80 ? name : name[..80];
                    focusedType = focused.Current.ControlType?.ProgrammaticName;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(selection) && string.IsNullOrWhiteSpace(focusedLabel))
                return null;

            return new DesktopSelectionSnapshot(selection, focusedLabel, focusedType);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractSafeSelection(AutomationElement el)
    {
        try
        {
            if (el.Current.IsPassword)
                return null;

            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj) && tpObj is TextPattern tp)
            {
                var ranges = tp.GetSelection();
                if (ranges is { Length: > 0 })
                {
                    var sb = new StringBuilder();
                    foreach (var r in ranges)
                    {
                        var txt = r.GetText(900);
                        if (!string.IsNullOrWhiteSpace(txt))
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(txt.Trim());
                            if (sb.Length >= 800) break;
                        }
                    }
                    var combined = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
                    if (!string.IsNullOrWhiteSpace(combined))
                        return combined.Length <= 800 ? combined : combined[..800] + "…";
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Tightened target selector for L1 / manual UIA OTP fallback (L2 NEVER uses UIA).
    /// NEVER blindly falls back to an unrelated non-browser window (such as LINE, Discord, or Notepad).
    /// </summary>
    internal static ExternalWindowTarget? SelectOtpUiaTarget(
        IReadOnlyList<ExternalWindowTarget> targets,
        string? serviceHint,
        string? expectedDomain,
        string? senderName,
        bool allowBrowserFallback = true)
    {
        if (targets is null || targets.Count == 0)
            return null;

        var eligible = targets.Where(t => !SensitiveProcesses.Contains(t.ProcessName)).ToList();
        if (eligible.Count == 0)
            return null;

        var domain = string.IsNullOrWhiteSpace(expectedDomain) || string.Equals(expectedDomain.Trim(), "unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : expectedDomain.Trim().ToLowerInvariant();
        var domainStem = domain is not null ? DomainParser.ExtractDomainStem(domain) : "";

        bool MatchesDomainOrSender(ExternalWindowTarget t)
        {
            if (domain is not null)
            {
                if (t.Title.Contains(domain, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (domainStem.Length >= 3 && t.Title.Contains(domainStem, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(senderName) && SharesKeyword(t.Title, senderName))
                return true;
            return false;
        }

        // 1) Strongest match: browser window whose title matches expectedDomain or senderName
        var matchedBrowser = eligible.FirstOrDefault(t => IsBrowserProcess(t.ProcessName) && MatchesDomainOrSender(t));
        if (matchedBrowser is not null)
            return matchedBrowser;

        // 2) Native desktop app (non-browser) whose title explicitly matches expectedDomain or senderName
        var matchedNative = eligible.FirstOrDefault(t => !IsBrowserProcess(t.ProcessName) && MatchesDomainOrSender(t));
        if (matchedNative is not null)
            return matchedNative;

        // 3) When neither domain nor sender metadata is available, allow explicit serviceHint title match
        if (domain is null && string.IsNullOrWhiteSpace(senderName) && !string.IsNullOrWhiteSpace(serviceHint))
        {
            var hintMatch = eligible.FirstOrDefault(t => IsBrowserProcess(t.ProcessName) && SharesKeyword(t.Title, serviceHint))
                         ?? eligible.FirstOrDefault(t => SharesKeyword(t.Title, serviceHint));
            if (hintMatch is not null)
                return hintMatch;
        }

        // 4) Browser-only fallback: ONLY when allowed AND the most recent external window itself is a browser
        //    (e.g. Firefox without extension showing a generic "Sign in" title). Never targets LINE/Discord/Notepad.
        if (allowBrowserFallback && IsBrowserProcess(eligible[0].ProcessName))
            return eligible[0];

        return null;
    }

    public Task<(bool Ok, string Message)> TryFillOtpInExternalWindowAsync(
        string code,
        string? serviceHint = null,
        CancellationToken ct = default,
        string? displayHandle = null,
        string? expectedDomain = null,
        string? senderName = null,
        bool allowBrowserFallback = true)
    {
        // Zero-LLM path: uses TextCached only.
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult((false, _localizer.TextCached("OtpEmpty", "Verification code is empty.")));

        var handleTag = string.IsNullOrWhiteSpace(displayHandle) ? "otp" : displayHandle;
        var targets = GetRecentExternalTargets();
        var target = SelectOtpUiaTarget(targets, serviceHint, expectedDomain, senderName, allowBrowserFallback);

        if (target is null)
            return Task.FromResult((false, _localizer.TextCached("OtpTargetMissing", "No matching external browser or login window found.")));

        try
        {
            var root = AutomationElement.FromHandle(target.Hwnd);
            if (root is null)
                return Task.FromResult((false, _localizer.TextCached("OtpWindowInaccessible", "Cannot access target window UI.")));

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
                return Task.FromResult((false, _localizer.TextCached("OtpFieldNotFound", "No fillable input found in '{0}'.", Truncate(target.Title, 24))));

            SetForegroundWindow(target.Hwnd);
            if (chosen.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp && !vp.Current.IsReadOnly)
            {
                vp.SetValue(code);
                return Task.FromResult((true, _localizer.TextCached("OtpFilledSafe", "Filled {0} into '{1}' (not auto-submitted).", handleTag, Truncate(target.Title, 24))));
            }

            chosen.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("^a");
            System.Windows.Forms.SendKeys.SendWait(code);
            return Task.FromResult((true, _localizer.TextCached("OtpTypedSafe", "Entered {0} into '{1}' (not auto-submitted).", handleTag, Truncate(target.Title, 24))));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryFillOtpInExternalWindowAsync failed");
            return Task.FromResult((false, _localizer.TextCached("OtpAutoFillFailed", "Auto-fill failed: {0}", ex.Message)));
        }
    }

    public Task HandleBrowserPageSignalAsync(BrowserPageSignal signal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signal.Url))
            return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(signal.Title) ? signal.Url : signal.Title.Trim();
        var labels = signal.Labels ?? Array.Empty<string>();
        var headings = signal.Headings ?? Array.Empty<string>();
        var excerpt = (signal.Excerpt ?? "").Trim();
        if (excerpt.Length > 700) excerpt = excerpt[..700] + "…";

        var cached = new CachedBrowserPageSignal(
            Url: signal.Url.Trim(),
            Title: title,
            InputCount: Math.Max(0, signal.InputCount),
            Labels: labels,
            Headings: headings,
            Excerpt: excerpt,
            CapturedAt: now,
            HasOneTimeCodeInput: signal.HasOneTimeCodeInput,
            HasFocusedInput: signal.HasFocusedInput);

        // Always cache L1 browser page metadata (even when InputCount == 0, e.g., Wiki/doc pages) for zero-round-trip deictic prefetch.
        lock (_lock)
        {
            _lastBrowserPageSignal = cached;
        }

        // Single unified browser-page signal channel consumed by StateMachineAgent (eliminates dual WindowFocused + PageSignal triggers).
        if (ProactiveMonitoringEnabled)
            BrowserPageSignalReceived?.Invoke(cached);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Suppresses redundant UIA <c>WindowFocused(enriched, null)</c> events for browsers whenever the browser extension
    /// already reported a fresh page signal (preventing duplicate <c>win:msedge|...</c> + <c>url:...</c> rules).
    /// </summary>
    internal static bool ShouldSuppressBrowserUiaWindowFocus(
        CachedBrowserPageSignal? lastSignal,
        string uiaTitle,
        DateTimeOffset nowUtc)
    {
        if (lastSignal is null)
            return false;

        var age = nowUtc - lastSignal.CapturedAt;
        if (age > TimeSpan.FromSeconds(15))
            return false;

        if (age <= TimeSpan.FromSeconds(5))
            return true;

        if (!string.IsNullOrWhiteSpace(lastSignal.Title) &&
            !string.IsNullOrWhiteSpace(uiaTitle) &&
            uiaTitle.Contains(lastSignal.Title, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
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
                {
                    if (pid == selfPid)
                        _wasSelfForeground = true;
                    continue; // Critical: never overwrite LastExternalTarget with our own UI window!
                }

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
                bool returnedFromSelf = _wasSelfForeground;
                _wasSelfForeground = false;
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
                            if (ShouldSuppressBrowserUiaWindowFocus(GetLastBrowserPageSignal(), enriched.Title, DateTimeOffset.UtcNow))
                                return;
                            if ((enriched.FormFieldLabels is { Count: > 0 }) || enriched.VisibleInputCount > 0)
                                WindowFocused?.Invoke(enriched, null);
                        }, ct);
                    }
                }
                else if (returnedFromSelf)
                {
                    // User switched away from AI Secretary back to the same external window (e.g. to highlight text in Word/Chrome):
                    // refresh CapturedAt so L1 staleness accurately reflects when the user last actively focused that external window.
                    var existing = GetLastExternalTarget();
                    if (existing is not null && existing.Hwnd == hwnd)
                        RecordTarget(existing with { CapturedAt = now });
                    else
                        RecordTarget(new ExternalWindowTarget(hwnd, (int)pid, procName, title, now));
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
