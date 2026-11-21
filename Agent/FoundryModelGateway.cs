using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AiSecretary.Desktop.Agent.Providers;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Agent;

public sealed class FoundryModelGateway : IModelGateway
{
    private static readonly object QueueLock = new();
    private static readonly Queue<PacingWaiter> InteractiveQueue = new();
    private static readonly Queue<PacingWaiter> BackgroundQueue = new();
    private static bool _pacingActive;
    private static DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim UsageLock = new(1, 1);

    private sealed class PacingWaiter
    {
        public TaskCompletionSource<bool> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset EnqueuedUtc { get; } = DateTimeOffset.UtcNow;
        public bool Cancelled;
    }

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly SqliteStore _store;
    private readonly IReadOnlyList<ILlmProviderAdapter> _adapters;
    private readonly ILogger<FoundryModelGateway> _logger;

    public FoundryModelGateway(
        HttpClient http,
        IConfiguration config,
        SqliteStore store,
        IEnumerable<ILlmProviderAdapter> adapters,
        ILogger<FoundryModelGateway> logger)
    {
        _http = http;
        _config = config;
        _store = store;
        _adapters = adapters.OrderBy(a => a.Priority).ToList();
        _logger = logger;
    }

    public Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, CancellationToken ct = default)
        => CompleteAsync(messages, new LlmCallOptions("Unknown", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), ct);

    public Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, ModelTier tier, CancellationToken ct = default)
        => CompleteAsync(messages, new LlmCallOptions("Unknown", tier, CachePolicy.None, LlmPriority.Background), ct);

    public async Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, LlmCallOptions options, CancellationToken ct = default)
    {
        var (endpoint, apiKey, model) = await ResolveProfileAsync(options.Tier, ct);

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("LLM Endpoint or API Key is not configured. Please set it in Settings -> LLM or .env.");
        }

        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(endpoint))
            ?? throw new InvalidOperationException($"No LLM Provider Adapter found for {endpoint}.");

        endpoint = adapter.PrepareEndpoint(endpoint, model);

        var activeMessages = messages;
        var hasImageAttachments = activeMessages.Any(m => m.Role == "user" && AttachmentExtractor.HasImageAttachment(m.Content));
        var imageFallbackUsed = false;
        var bodyJson = adapter.BuildRequestJson(activeMessages, model, options.CachePolicy);

        var sw = Stopwatch.StartNew();
        const int maxRetries = 4;
        for (int attempt = 0; ; attempt++)
        {
            await WaitForPacingAsync(options.Priority, ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            adapter.ApplyAuthHeaders(req, apiKey);
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            HttpResponseMessage? resp = null;
            string respBody;
            try
            {
                resp = await _http.SendAsync(req, ct);
                respBody = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientNetworkError(ex, ct))
            {
                resp?.Dispose();
                var delayMs = Math.Min(1500 * (attempt + 1), 6000);
                _logger.LogWarning(ex, "Transient network error calling LLM API, retrying in {Delay}ms (attempt {Attempt}/{Max})",
                    delayMs, attempt + 1, maxRetries);
                await Task.Delay(delayMs, ct);
                continue;
            }
            catch (HttpRequestException ex) when (ex.InnerException is not null)
            {
                resp?.Dispose();
                await LogCallFailureAsync(options, model, (int)sw.ElapsedMilliseconds, ex.Message);
                throw new HttpRequestException($"{ex.Message} ({ex.InnerException.Message})", ex);
            }
            using var _ = resp;

            if (resp.StatusCode == HttpStatusCode.Forbidden && hasImageAttachments && !imageFallbackUsed)
            {
                imageFallbackUsed = true;
                _logger.LogWarning("LLM API returned 403 on image request (possibly corporate DLP/Forcepoint); stripping image payloads to text placeholders.");
                activeMessages = AttachmentExtractor.StripImagesToPlaceholder(activeMessages);
                bodyJson = adapter.BuildRequestJson(activeMessages, model, options.CachePolicy);
                continue;
            }

            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or (HttpStatusCode)529 && attempt < maxRetries)
            {
                var delay = GetRetryDelay(resp, attempt);
                _logger.LogWarning("LLM API {Status}, retrying in {Delay}ms (attempt {Attempt}/{Max})",
                    (int)resp.StatusCode, (int)delay.TotalMilliseconds, attempt + 1, maxRetries);
                await Task.Delay(delay, ct);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("LLM API error {Status}: {Body}", (int)resp.StatusCode, respBody);
                await LogCallFailureAsync(options, model, (int)sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
                throw new HttpRequestException($"LLM API error ({(int)resp.StatusCode}): {respBody}");
            }

            using var doc = JsonDocument.Parse(respBody);
            var root = doc.RootElement;

            string text = adapter.ExtractResponseText(root);
            string? stopReason = adapter.ExtractStopReason(root);
            int rawTextLength = text.Length;
            var usage = adapter.ExtractUsage(root);
            int uncachedIn = usage.UncachedInput;
            int cacheWrite = usage.CacheWrite;
            int cacheRead = usage.CacheRead;
            int outTok = usage.Output;
            if (uncachedIn == 0 && cacheWrite == 0 && cacheRead == 0)
                uncachedIn = Math.Max(16, messages.Sum(m => m.Content.Length) / 3);
            if (outTok == 0 && rawTextLength > 0)
                outTok = Math.Max(8, rawTextLength / 3);

            await AccumulateAndLogUsageAsync(options, model, uncachedIn, cacheWrite, cacheRead, outTok, (int)sw.ElapsedMilliseconds, stopReason, rawTextLength, ct);

            var tokens = Math.Max(64, uncachedIn + cacheWrite + cacheRead + outTok);
            return new ModelResponse(text, tokens, uncachedIn, outTok, cacheRead, cacheWrite, stopReason);
        }
    }

    private async Task<(string Endpoint, string ApiKey, string Model)> ResolveProfileAsync(ModelTier tier, CancellationToken ct)
    {
        var prefix = tier == ModelTier.Complex ? "llm_complex_" : "llm_simple_";
        var savedEp = await _store.GetSyncValueAsync(prefix + "endpoint", ct);
        var savedKey = SecretProtector.Unprotect(await _store.GetSyncValueAsync(prefix + "key", ct));
        var savedModel = await _store.GetSyncValueAsync(prefix + "model", ct);

        var tierSection = tier == ModelTier.Complex ? "Foundry:Complex" : "Foundry:Simple";
        var defaultEp = FirstNonEmpty(
            _config.GetValue<string>($"{tierSection}:Endpoint"),
            _config.GetValue<string>("Foundry:Endpoint"),
            _config.GetValue<string>("Foundry:Complex:Endpoint")) ?? "";
        var defaultKey = FirstNonEmpty(
            _config.GetValue<string>($"{tierSection}:ApiKey"),
            _config.GetValue<string>("Foundry:ApiKey"),
            _config.GetValue<string>("Foundry:Complex:ApiKey")) ?? "";
        var defaultModel = FirstNonEmpty(
            _config.GetValue<string>($"{tierSection}:Model"),
            _config.GetValue<string>("Foundry:Model"),
            _config.GetValue<string>("Foundry:Complex:Model")) ?? "gpt-4o";

        return (
            string.IsNullOrWhiteSpace(savedEp) ? defaultEp : savedEp.Trim(),
            string.IsNullOrWhiteSpace(savedKey) ? defaultKey : savedKey.Trim(),
            string.IsNullOrWhiteSpace(savedModel) ? defaultModel : savedModel.Trim()
        );
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    /// <summary>
    /// Priority pacing queue: Interactive calls jump ahead of Background calls, with 12s anti-starvation aging
    /// so background tasks (MemoryReflection, Triage, Optimizer) never starve indefinitely.
    /// </summary>
    private async Task WaitForPacingAsync(LlmPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        PacingWaiter? waiter = null;
        bool acquiredImmediately = false;
        lock (QueueLock)
        {
            if (!_pacingActive)
            {
                _pacingActive = true;
                acquiredImmediately = true;
            }
            else
            {
                waiter = new PacingWaiter();
                if (priority == LlmPriority.Interactive)
                    InteractiveQueue.Enqueue(waiter);
                else
                    BackgroundQueue.Enqueue(waiter);
            }
        }

        if (!acquiredImmediately && waiter is not null)
        {
            using var reg = ct.Register(() =>
            {
                lock (QueueLock)
                {
                    waiter.Cancelled = true;
                    waiter.Tcs.TrySetCanceled(ct);
                }
            });
            await waiter.Tcs.Task;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var minIntervalMs = _config.GetValue<int>("Foundry:MinRequestIntervalMs", 1500);
            var elapsed = DateTimeOffset.UtcNow - _lastRequestUtc;
            var wait = TimeSpan.FromMilliseconds(minIntervalMs) - elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequestUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            ReleaseNextWaiter();
        }
    }

    private static void ReleaseNextWaiter()
    {
        lock (QueueLock)
        {
            var now = DateTimeOffset.UtcNow;
            while (true)
            {
                // Anti-starvation: if oldest background waiter has waited >= 12s, let it go next.
                bool bgStarved = BackgroundQueue.Count > 0 &&
                                 (now - BackgroundQueue.Peek().EnqueuedUtc) >= TimeSpan.FromSeconds(12);
                Queue<PacingWaiter>? q =
                    (!bgStarved && InteractiveQueue.Count > 0) ? InteractiveQueue :
                    BackgroundQueue.Count > 0 ? BackgroundQueue :
                    InteractiveQueue.Count > 0 ? InteractiveQueue : null;
                if (q is null)
                {
                    _pacingActive = false;
                    return;
                }

                var cand = q.Dequeue();
                // RunContinuationsAsynchronously is set on Tcs, so calling TrySetResult inside QueueLock is deadlock-free;
                // if it returns false the waiter was already cancelled, so continue handing the turn to the next waiter.
                if (!cand.Cancelled && cand.Tcs.TrySetResult(true))
                    return;
            }
        }
    }

    private static bool IsTransientNetworkError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        if (ex is HttpRequestException hre)
            return hre.StatusCode is null;
        return ex is IOException or System.Net.Sockets.SocketException;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;

        if (resp.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            if (diff > TimeSpan.Zero)
                return diff > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : diff;
        }

        var baseSec = Math.Pow(2, attempt + 1);
        var jitterMs = Random.Shared.Next(150, 600);
        return TimeSpan.FromSeconds(baseSec) + TimeSpan.FromMilliseconds(jitterMs);
    }

    private async Task AccumulateAndLogUsageAsync(
        LlmCallOptions options,
        string model,
        int uncachedIn,
        int cacheWrite,
        int cacheRead,
        int outTok,
        int latencyMs,
        string? stopReason,
        int rawTextLength,
        CancellationToken ct)
    {
        await UsageLock.WaitAsync(ct);
        try
        {
            await AddCounterAsync("tokens_input", uncachedIn, ct);
            await AddCounterAsync("tokens_cache_write", cacheWrite, ct);
            await AddCounterAsync("tokens_cache", cacheRead, ct);
            await AddCounterAsync("tokens_output", outTok, ct);
            await _store.InsertLlmCallLogAsync(new LlmCallLogRecord(
                Id: 0,
                TimestampUtc: DateTimeOffset.UtcNow,
                Agent: options.Agent,
                Tier: options.Tier.ToString(),
                Model: model,
                CachePolicy: options.CachePolicy.ToString(),
                Priority: options.Priority.ToString(),
                UncachedInput: uncachedIn,
                CacheWrite: cacheWrite,
                CacheRead: cacheRead,
                Output: outTok,
                LatencyMs: latencyMs,
                Succeeded: true,
                Error: null,
                StopReason: stopReason,
                RawTextLength: rawTextLength), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist token usage / LlmCallLog");
        }
        finally
        {
            UsageLock.Release();
        }
    }

    private async Task LogCallFailureAsync(LlmCallOptions options, string model, int latencyMs, string error)
    {
        try
        {
            await _store.InsertLlmCallLogAsync(new LlmCallLogRecord(
                Id: 0,
                TimestampUtc: DateTimeOffset.UtcNow,
                Agent: options.Agent,
                Tier: options.Tier.ToString(),
                Model: model,
                CachePolicy: options.CachePolicy.ToString(),
                Priority: options.Priority.ToString(),
                UncachedInput: 0,
                CacheWrite: 0,
                CacheRead: 0,
                Output: 0,
                LatencyMs: latencyMs,
                Succeeded: false,
                Error: error), CancellationToken.None);
        }
        catch { }
    }

    private async Task AddCounterAsync(string key, int delta, CancellationToken ct)
    {
        if (delta <= 0) return;
        var raw = await _store.GetSyncValueAsync(key, ct);
        var current = long.TryParse(raw, out var v) ? v : 0L;
        await _store.SetSyncValueAsync(key, (current + delta).ToString(), ct);
    }
}
