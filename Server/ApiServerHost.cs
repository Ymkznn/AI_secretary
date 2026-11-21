using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiSecretary.Desktop.Server;

public sealed record ChatPromptRequest(string Prompt, string? TargetMailId = null);
public sealed record ResolveConfirmationRequest(bool Approved);
public sealed record ResolvePathRequest(string Decision); // "AllowOnce" | "TrustPermanently" | "Deny"
public sealed record ToggleSkillRequest(bool Enabled);

/// <summary>
/// Embedded local-only HTTP + SSE backend API server (127.0.0.1).
/// Enables full headless operation (--headless) as well as external/frontend API clients.
/// </summary>
public sealed class ApiServerHost : IAsyncDisposable
{
    private readonly SecretaryController _controller;
    private readonly InteractionGateway _interaction;
    private readonly BrowserFormBridgeService _browserBridge;
    private readonly IConfiguration _config;
    private readonly ILogger<ApiServerHost> _logger;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _sseClients = new();
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";

    public ApiServerHost(
        SecretaryController controller,
        InteractionGateway interaction,
        BrowserFormBridgeService browserBridge,
        IConfiguration config,
        ILogger<ApiServerHost> logger)
    {
        _controller = controller;
        _interaction = interaction;
        _browserBridge = browserBridge;
        _config = config;
        _logger = logger;

        _controller.StateChanged += () => BroadcastEvent("state_changed", new { });
        _controller.AgentsChanged += () => BroadcastEvent("agents_changed", _controller.GetAgentRuns());
        _controller.SkillsChanged += () => BroadcastEvent("skills_changed", new { });
        _controller.PinnedRemindersChanged += () => BroadcastEvent("pinned_changed", _controller.GetPinnedReminders());
        _controller.ProactiveAlert += (title, content) => BroadcastEvent("proactive_alert", new { title, content });
        _controller.WatcherTriggered += (w, alert) => BroadcastEvent("watcher_triggered", new
        {
            w.WatcherId,
            w.Title,
            w.Url,
            alert
        });
        _interaction.PendingRequestAdded += req => BroadcastEvent("interaction_requested", req);
        _interaction.PendingRequestResolved += id => BroadcastEvent("interaction_resolved", new { requestId = id });
        _browserBridge.ExtractRequested += requestId => BroadcastEvent("browser_extract_request", new { requestId });
        _browserBridge.DomRequested += (requestId, selector, maxChars) => BroadcastEvent("browser_dom_request", new { requestId, selector, maxChars });
        // Explicit camelCase projection: the default serializer emits "FieldId"/"Value", which content.js never matched,
        // so every prefill silently missed all fields.
        _browserBridge.ApplyFillsRequested += (requestId, fills) => BroadcastEvent("browser_apply_fills", new
        {
            requestId,
            fills = fills.Select(f => new { fieldId = f.FieldId, value = f.Value, label = f.Label, name = f.Name }).ToList()
        });
        _browserBridge.FillOtpRequested += (requestId, code, expectedDomain, requireOtpField) =>
            BroadcastEvent("browser_fill_otp", new { requestId, code, expectedDomain, requireOtpField });
        _browserBridge.ClearOtpRequested += (requestId, expectedDomain) =>
            BroadcastEvent("browser_clear_otp", new { requestId, expectedDomain });
        _browserBridge.TitleRequested += requestId => BroadcastEvent("browser_title_request", new { requestId });
        _browserBridge.SelectionRequested += requestId => BroadcastEvent("browser_selection_request", new { requestId });
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var port = _config.GetValue("Server:Port", 19876);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            conversationId = _controller.ConversationId,
            hasRunningTasks = _controller.HasRunningTasks
        }));

        _app.MapGet("/api/messages", async () => Results.Ok(await _controller.GetMessagesAsync()));

        _app.MapPost("/api/chat", async (ChatPromptRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "Prompt is required." });
            await _controller.SendUserPromptAsync(req.Prompt, req.TargetMailId);
            return Results.Accepted();
        });

        _app.MapGet("/api/agents", () => Results.Ok(_controller.GetAgentRuns()));
        _app.MapPost("/api/agents/stop", () =>
        {
            _controller.StopAllTasks();
            return Results.Ok(new { stopped = true });
        });
        _app.MapPost("/api/agents/kill", async () =>
        {
            await _controller.KillMainAgentAsync();
            return Results.Ok(new { killed = true });
        });

        _app.MapGet("/api/interactions/pending", () => Results.Ok(_controller.GetPendingInteractions()));
        _app.MapPost("/api/interactions/{id}/confirm", (string id, ResolveConfirmationRequest body) =>
            _controller.ResolveConfirmation(id, body.Approved)
                ? Results.Ok(new { resolved = true })
                : Results.NotFound(new { error = "Pending confirmation not found." }));

        _app.MapPost("/api/interactions/{id}/path", (string id, ResolvePathRequest body) =>
        {
            if (!Enum.TryParse<PathPermissionDecision>(body.Decision, ignoreCase: true, out var decision))
                return Results.BadRequest(new { error = "Invalid decision (AllowOnce|TrustPermanently|Deny)." });
            return _controller.ResolvePathPermission(id, decision)
                ? Results.Ok(new { resolved = true })
                : Results.NotFound(new { error = "Pending path request not found." });
        });

        _app.MapPost("/api/interactions/{id}/command", (string id, ResolvePathRequest body) =>
        {
            if (!Enum.TryParse<CommandApprovalDecision>(body.Decision, ignoreCase: true, out var decision))
                return Results.BadRequest(new { error = "Invalid decision (AllowOnce|AllowForTask|Deny)." });
            return _controller.ResolveCommandApproval(id, decision)
                ? Results.Ok(new { resolved = true })
                : Results.NotFound(new { error = "Pending command approval request not found." });
        });

        _app.MapGet("/api/skills", async () => Results.Ok(await _controller.GetAllSkillsAsync()));
        _app.MapPut("/api/skills/{name}", async (string name, ToggleSkillRequest body) =>
        {
            await _controller.SetSkillEnabledAsync(name, body.Enabled);
            return Results.Ok(new { name, enabled = body.Enabled });
        });
        _app.MapDelete("/api/skills/{name}", async (string name) =>
            await _controller.DeleteCustomSkillAsync(name)
                ? Results.Ok(new { deleted = true })
                : Results.NotFound());

        _app.MapGet("/api/schedules", async () => Results.Ok(await _controller.GetPendingSchedulesAsync()));
        _app.MapDelete("/api/schedules/{id}", async (string id) =>
        {
            await _controller.DeleteScheduleAsync(id);
            return Results.Ok(new { deleted = true });
        });

        _app.MapGet("/api/calendar", async () => Results.Ok(await _controller.GetCalendarEventsAsync()));
        _app.MapDelete("/api/calendar/{id}", async (string id) =>
        {
            await _controller.DeleteCalendarEventAsync(id);
            return Results.Ok(new { deleted = true });
        });

        _app.MapGet("/api/pinned", () => Results.Ok(_controller.GetPinnedReminders()));
        _app.MapDelete("/api/pinned/{id}", async (string id) =>
        {
            await _controller.DismissPinnedReminderAsync(id);
            return Results.Ok(new { dismissed = true });
        });

        _app.MapGet("/api/watchers", async () => Results.Ok(await _controller.GetWatchersAsync()));
        _app.MapGet("/api/telemetry", async () =>
        {
            var (model, endpoint, inTok, outTok, cacheTok) = await _controller.GetModelAndUsageInfoAsync();
            var branch = await _controller.GetBranchTelemetryAsync();
            return Results.Ok(new
            {
                model,
                endpoint,
                tokens = new { input = inTok, output = outTok, cached = cacheTok, total = inTok + outTok + cacheTok },
                branchPredictor = branch,
                tuning = _controller.GetWorkflowTuningInfo()
            });
        });
        _app.MapPost("/api/telemetry/optimize", async () =>
        {
            var (ok, note) = await _controller.RunTelemetryOptimizationAsync();
            return Results.Ok(new { ok, note, tuning = _controller.GetWorkflowTuningInfo() });
        });
        _app.MapDelete("/api/watchers/{id}", async (string id) =>
            await _controller.DeleteWatcherAsync(id)
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new { error = "Watcher not found." }));
        _app.MapPost("/api/watchers/{id}/pause", async (string id) =>
            await _controller.SetWatcherStatusAsync(id, "Paused")
                ? Results.Ok(new { status = "Paused" })
                : Results.NotFound(new { error = "Watcher not found." }));
        _app.MapPost("/api/watchers/{id}/resume", async (string id) =>
            await _controller.SetWatcherStatusAsync(id, "Active")
                ? Results.Ok(new { status = "Active" })
                : Results.NotFound(new { error = "Watcher not found." }));

        _app.MapGet("/api/browser/last-window", () => Results.Ok(_controller.GetLastExternalWindow()));
        _app.MapPost("/api/browser/open-login", async (HttpContext ctx) =>
        {
            string? url = ctx.Request.Query["url"].FirstOrDefault();
            var (ok, msg) = await _controller.OpenDedicatedBrowserAsync(url);
            return ok ? Results.Ok(new { ok, message = msg }) : Results.BadRequest(new { ok, message = msg });
        });
        _app.MapPost("/api/browser/form-suggest", async (BrowserFormContext form) =>
        {
            var reply = await _controller.SuggestFormFillAsync(form);
            return Results.Ok(new { reply });
        });
        _app.MapPost("/api/browser/extract-result/{requestId}", (string requestId, BrowserFormContext form) =>
            _browserBridge.CompleteExtract(requestId, form)
                ? Results.Ok(new { received = true })
                : Results.NotFound(new { error = "Extract request not found or expired." }));
        _app.MapPost("/api/browser/dom-result/{requestId}", (string requestId, BrowserDomSnapshot dom) =>
            _browserBridge.CompleteDom(requestId, dom)
                ? Results.Ok(new { received = true })
                : Results.NotFound(new { error = "DOM request not found or expired." }));
        _app.MapPost("/api/browser/otp-result/{requestId}", (string requestId, BrowserOtpFillResult result) =>
            _browserBridge.CompleteFillOtp(requestId, result)
                ? Results.Ok(new { received = true })
                : Results.NotFound(new { error = "OTP fill request not found or expired." }));
        _app.MapPost("/api/browser/clear-otp-result/{requestId}", (string requestId, BrowserOtpFillResult result) =>
            _browserBridge.CompleteClearOtp(requestId, result)
                ? Results.Ok(new { received = true })
                : Results.NotFound(new { error = "OTP clear request not found or expired." }));
        _app.MapPost("/api/browser/title-result/{requestId}", (string requestId, BrowserTabTitleResult result) =>
            _browserBridge.CompleteTabTitle(requestId, result)
                ? Results.Ok(new { received = true })
                : Results.NotFound(new { error = "Title request not found or expired." }));
        _app.MapPost("/api/browser/fill-result/{requestId}", (string requestId, BrowserFormFillResult result) =>
            _browserBridge.CompleteApplyFills(requestId, result)
                ? Results.Ok(new { received = true })
                : Results.NotFound(new { error = "Fill request not found or expired." }));
        _app.MapPost("/api/browser/selection-result/{requestId}", (string requestId, BrowserSelectionResult result) =>
            _browserBridge.CompleteSelection(requestId, result)
                ? Results.Ok(new { received = true })
                : Results.NotFound(new { error = "Selection request not found or expired." }));
        _app.MapPost("/api/browser/page-signal", async (BrowserPageSignal signal, CancellationToken ct) =>
        {
            await _controller.HandleBrowserPageSignalAsync(signal, ct);
            return Results.Accepted();
        });

        _app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            var clientId = Guid.NewGuid();
            var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
            _sseClients[clientId] = ch;
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                    while (await timer.WaitForNextTickAsync(pingCts.Token))
                    {
                        ch.Writer.TryWrite("event: ping\ndata: {}\n\n");
                    }
                }
                catch (OperationCanceledException) { }
            }, pingCts.Token);

            try
            {
                await ctx.Response.WriteAsync("event: ping\ndata: {}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                await foreach (var payload in ch.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                pingCts.Cancel();
                _sseClients.TryRemove(clientId, out _);
            }
        });

        await _app.StartAsync(ct);
        BaseUrl = _app.Urls.FirstOrDefault() ?? $"http://127.0.0.1:{port}";
        _logger.LogInformation("AiSecretary local API server listening on {Url}", BaseUrl);
    }

    private void BroadcastEvent(string eventName, object data)
    {
        if (_sseClients.IsEmpty) return;
        var json = JsonSerializer.Serialize(data);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        foreach (var ch in _sseClients.Values)
        {
            ch.Writer.TryWrite(frame);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
