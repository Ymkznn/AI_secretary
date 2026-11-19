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

/// <summary>
/// Bridges on-demand form read/fill, full DOM read, and OTP DOM fill commands between C# Agent skills and the Chrome/Edge MV3 Extension over SSE + HTTP,
/// so the user never has to manually click the browser extension icon.
/// </summary>
public sealed class BrowserFormBridgeService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserFormContext?>> _extractWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserDomSnapshot?>> _domWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserOtpFillResult?>> _otpWaiters = new();

    public event Action<string>? ExtractRequested;
    public event Action<string, string?, int>? DomRequested;
    public event Action<IReadOnlyList<BrowserFillAction>>? ApplyFillsRequested;
    public event Action<string, string>? FillOtpRequested;

    public Task<BrowserFormContext?> RequestActiveTabFormAsync(TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_extractWaiters, id => ExtractRequested?.Invoke(id), timeout, ct);

    public Task<BrowserDomSnapshot?> RequestActiveTabDomAsync(string? selector, int maxChars, TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_domWaiters, id => DomRequested?.Invoke(id, selector, maxChars), timeout, ct);

    public Task<BrowserOtpFillResult?> RequestFillOtpAsync(string code, TimeSpan timeout, CancellationToken ct = default)
        => WaitWithTimeoutAsync(_otpWaiters, id => FillOtpRequested?.Invoke(id, code), timeout, ct);

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

    public void DispatchApplyFills(IReadOnlyList<BrowserFillAction> fills)
        => ApplyFillsRequested?.Invoke(fills);
}
