using System.Collections.Concurrent;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

public sealed record BrowserOtpFillResult(
    bool Filled,
    string? Title = null,
    string? Url = null,
    string? Mode = null,
    string? Error = null);

public sealed record BrowserDomSnapshot(
    string Url,
    string Title,
    string Html,
    string Text,
    bool Truncated,
    string? Error = null);

public sealed record BrowserTabTitleResult(
    string? Title = null,
    bool Incognito = false);

public sealed record BrowserSelectionResult(
    string? Url = null,
    string? Title = null,
    string? SelectionText = null,
    IReadOnlyList<string>? Headings = null,
    string? Excerpt = null,
    string? FocusedLabel = null,
    bool Incognito = false);

public sealed record BrowserFormFillItemResult(
    string FieldId,
    bool Applied,
    string? Label = null,
    string? ReadBackValue = null,
    string? Error = null);

/// <summary>Result reported back by the extension after applying prefill values in every frame.</summary>
public sealed record BrowserFormFillResult(
    int AppliedCount,
    IReadOnlyList<BrowserFormFillItemResult> Items,
    string? Url = null,
    string? Title = null,
    string? Error = null);

/// <summary>
/// Bridges on-demand form read/fill, full DOM read, and OTP DOM fill commands between C# Agent skills and the Chrome/Edge MV3 Extension over SSE + HTTP,
/// so the user never has to manually click the browser extension icon.
/// </summary>
public sealed class BrowserFormBridgeService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserFormContext?>> _extractWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserDomSnapshot?>> _domWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserOtpFillResult?>> _otpWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserOtpFillResult?>> _clearOtpWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserTabTitleResult?>> _titleWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserFormFillResult?>> _fillWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserSelectionResult?>> _selectionWaiters = new();

    public event Action<string>? ExtractRequested;
    public event Action<string, string?, int>? DomRequested;
    /// <summary>(requestId, fills). The extension must POST the outcome to /api/browser/fill-result/{requestId}.</summary>
    public event Action<string, IReadOnlyList<BrowserFillAction>>? ApplyFillsRequested;
    /// <summary>(requestId, code, expectedDomain, requireOtpField).</summary>
    public event Action<string, string, string?, bool>? FillOtpRequested;
    /// <summary>(requestId, expectedDomain).</summary>
    public event Action<string, string?>? ClearOtpRequested;
    public event Action<string>? TitleRequested;
    public event Action<string>? SelectionRequested;

    public Task<BrowserFormContext?> RequestActiveTabFormAsync(TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_extractWaiters, id => ExtractRequested?.Invoke(id), timeout, ct);

    public Task<BrowserDomSnapshot?> RequestActiveTabDomAsync(string? selector, int maxChars, TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_domWaiters, id => DomRequested?.Invoke(id, selector, maxChars), timeout, ct);

    public Task<BrowserOtpFillResult?> RequestFillOtpAsync(
        string code,
        TimeSpan timeout,
        CancellationToken ct = default,
        string? expectedDomain = null,
        bool requireOtpField = false)
        => WaitWithTimeoutAsync(_otpWaiters, id => FillOtpRequested?.Invoke(id, code, expectedDomain, requireOtpField), timeout, ct);

    public Task<BrowserOtpFillResult?> RequestClearOtpAsync(
        TimeSpan timeout,
        CancellationToken ct = default,
        string? expectedDomain = null)
        => WaitWithTimeoutAsync(_clearOtpWaiters, id => ClearOtpRequested?.Invoke(id, expectedDomain), timeout, ct);

    public Task<BrowserTabTitleResult?> RequestActiveTabTitleAsync(TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_titleWaiters, id => TitleRequested?.Invoke(id), timeout, ct);

    /// <summary>
    /// Fast L1 selection & safe page-excerpt fetch from the active non-incognito browser tab (never reads input values or passwords).
    /// Intended to be called with a tight ~300ms timeout so it never slows down routing.
    /// </summary>
    public Task<BrowserSelectionResult?> RequestActiveTabSelectionAsync(TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_selectionWaiters, id => SelectionRequested?.Invoke(id), timeout, ct);

    private static async Task<T?> WaitWithTimeoutAsync<T>(
        ConcurrentDictionary<string, TaskCompletionSource<T?>> waiters,
        Action<string> dispatch,
        TimeSpan timeout,
        CancellationToken ct) where T : class
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters[requestId] = tcs;

        using var reg = ct.Register(() =>
        {
            if (waiters.TryRemove(requestId, out var w))
                w.TrySetCanceled(ct);
        });

        dispatch(requestId);

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, delayCts.Token));
        if (completed == tcs.Task)
        {
            delayCts.Cancel();
            return await tcs.Task;
        }

        waiters.TryRemove(requestId, out _);
        return null;
    }

    public bool CompleteExtract(string requestId, BrowserFormContext context)
    {
        if (!_extractWaiters.TryRemove(requestId, out var tcs))
            return false;
        tcs.TrySetResult(context);
        return true;
    }

    public bool CompleteDom(string requestId, BrowserDomSnapshot snapshot)
    {
        if (!_domWaiters.TryRemove(requestId, out var tcs))
            return false;
        tcs.TrySetResult(snapshot);
        return true;
    }

    public bool CompleteFillOtp(string requestId, BrowserOtpFillResult result)
    {
        if (!_otpWaiters.TryRemove(requestId, out var tcs))
            return false;
        tcs.TrySetResult(result);
        return true;
    }

    public bool CompleteClearOtp(string requestId, BrowserOtpFillResult result)
    {
        if (!_clearOtpWaiters.TryRemove(requestId, out var tcs))
            return false;
        tcs.TrySetResult(result);
        return true;
    }

    public bool CompleteTabTitle(string requestId, BrowserTabTitleResult result)
    {
        if (!_titleWaiters.TryRemove(requestId, out var tcs))
            return false;
        tcs.TrySetResult(result);
        return true;
    }

    /// <summary>
    /// Applies prefill values and waits for the extension to report which fields were actually found/filled,
    /// so the agent no longer claims success for a blind fire-and-forget dispatch.
    /// Returns null when the extension did not answer within the timeout.
    /// </summary>
    public Task<BrowserFormFillResult?> RequestApplyFillsAsync(IReadOnlyList<BrowserFillAction> fills, TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_fillWaiters, id => ApplyFillsRequested?.Invoke(id, fills), timeout, ct);

    public bool CompleteApplyFills(string requestId, BrowserFormFillResult result)
    {
        if (!_fillWaiters.TryRemove(requestId, out var tcs))
            return false;
        tcs.TrySetResult(result);
        return true;
    }

    public bool CompleteSelection(string requestId, BrowserSelectionResult result)
    {
        if (!_selectionWaiters.TryRemove(requestId, out var tcs))
            return false;
        tcs.TrySetResult(result);
        return true;
    }

    /// <summary>Legacy fire-and-forget dispatch (no result tracking).</summary>
    public void DispatchApplyFills(IReadOnlyList<BrowserFillAction> fills)
        => ApplyFillsRequested?.Invoke(Guid.NewGuid().ToString("N"), fills);
}
