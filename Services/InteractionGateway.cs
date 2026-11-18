using System.Collections.Concurrent;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

public sealed record PendingInteractionRequest(
    string RequestId,
    string Kind, // "confirm" | "path_permission" | "command_approval"
    string Title,
    string Message,
    string? TargetPath,
    DateTimeOffset CreatedAt);

/// <summary>
/// Headless-capable programmatic API for all permission requests and user confirmations.
/// Exposes pending requests over the HTTP API/SSE event stream even when a WPF UI handler is attached,
/// so either the desktop bubble or an external frontend/API client can resolve the interaction first.
/// </summary>
public sealed class InteractionGateway
{
    private readonly ConcurrentDictionary<string, PendingInteractionRequest> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _confirmWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PathPermissionDecision>> _pathWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandApprovalDecision>> _commandWaiters = new();

    public Func<string, string, Task<bool>>? UiConfirmHandler { get; set; }
    public Func<string, string, string, Task<PathPermissionDecision>>? UiPathPermissionHandler { get; set; }
    public Func<string, string, Task<CommandApprovalDecision>>? UiCommandApprovalHandler { get; set; }

    public event Action<PendingInteractionRequest>? PendingRequestAdded;
    public event Action<string>? PendingRequestResolved;

    public IReadOnlyList<PendingInteractionRequest> GetPendingRequests()
        => _pending.Values.OrderBy(r => r.CreatedAt).ToList();

    public Task<bool> RequestConfirmationAsync(string title, string message, CancellationToken ct = default)
        => RequestInteractionAsync(
            "confirm",
            title,
            message,
            targetPath: null,
            _confirmWaiters,
            cancelFallback: false,
            uiCall: UiConfirmHandler is not null ? () => UiConfirmHandler(title, message) : null,
            ct);

    public Task<PathPermissionDecision> RequestPathPermissionAsync(
        string title,
        string message,
        string targetPath,
        CancellationToken ct = default)
        => RequestInteractionAsync(
            "path_permission",
            title,
            message,
            targetPath,
            _pathWaiters,
            cancelFallback: PathPermissionDecision.Deny,
            uiCall: UiPathPermissionHandler is not null
                ? () => UiPathPermissionHandler(title, message, targetPath)
                : null,
            ct);

    public bool ResolveConfirmation(string requestId, bool approved)
        => ResolveInteraction(requestId, _confirmWaiters, approved);

    public bool ResolvePathPermission(string requestId, PathPermissionDecision decision)
        => ResolveInteraction(requestId, _pathWaiters, decision);

    public Task<CommandApprovalDecision> RequestCommandApprovalAsync(
        string title,
        string message,
        CancellationToken ct = default)
    {
        Func<Task<CommandApprovalDecision>>? uiCall = UiCommandApprovalHandler is not null
            ? () => UiCommandApprovalHandler(title, message)
            : UiConfirmHandler is not null
                ? async () => await UiConfirmHandler(title, message)
                    ? CommandApprovalDecision.AllowOnce
                    : CommandApprovalDecision.Deny
                : null;

        return RequestInteractionAsync(
            "command_approval",
            title,
            message,
            targetPath: null,
            _commandWaiters,
            cancelFallback: CommandApprovalDecision.Deny,
            uiCall,
            ct);
    }

    public bool ResolveCommandApproval(string requestId, CommandApprovalDecision decision)
        => ResolveInteraction(requestId, _commandWaiters, decision);

    private async Task<TResult> RequestInteractionAsync<TResult>(
        string kind,
        string title,
        string message,
        string? targetPath,
        ConcurrentDictionary<string, TaskCompletionSource<TResult>> waiters,
        TResult cancelFallback,
        Func<Task<TResult>>? uiCall,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var req = new PendingInteractionRequest(id, kind, title, message, targetPath, DateTimeOffset.UtcNow);
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id] = req;
        waiters[id] = tcs;
        PendingRequestAdded?.Invoke(req);

        using var reg = ct.Register(() => ResolveInteraction(id, waiters, cancelFallback));

        if (uiCall is not null)
        {
            _ = uiCall().ContinueWith(t =>
            {
                var result = t.IsCompletedSuccessfully ? t.Result : cancelFallback;
                ResolveInteraction(id, waiters, result);
            }, TaskScheduler.Default);
        }

        return await tcs.Task;
    }

    private bool ResolveInteraction<TResult>(
        string requestId,
        ConcurrentDictionary<string, TaskCompletionSource<TResult>> waiters,
        TResult result)
    {
        if (!waiters.TryRemove(requestId, out var tcs))
            return false;
        _pending.TryRemove(requestId, out _);
        tcs.TrySetResult(result);
        PendingRequestResolved?.Invoke(requestId);
        return true;
    }
}
