using System.Text.Json;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Agent.Providers;

public sealed class AnthropicProviderAdapter : ILlmProviderAdapter
{
    private readonly AppSettingsService _settings;

    public AnthropicProviderAdapter(AppSettingsService settings)
    {
        _settings = settings;
    }

    public int Priority => 20;

    public bool CanHandle(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase);
    }

    public void ApplyAuthHeaders(HttpRequestMessage request, string apiKey)
    {
        var version = string.IsNullOrWhiteSpace(_settings.Current.Llm.AnthropicVersion)
            ? "2023-06-01"
            : _settings.Current.Llm.AnthropicVersion.Trim();
        request.Headers.Add("api-key", apiKey);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", version);
        request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");
    }

    public string BuildRequestJson(IReadOnlyList<ModelMessage> messages, string model, CachePolicy cachePolicy = CachePolicy.Conversation)
    {
        var systemPrompt = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var normalized = new List<(string Role, string Content)>();
        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            var role = m.Role == "assistant" ? "assistant" : "user";
            if (normalized.Count == 0 && role == "assistant")
                continue;
            if (normalized.Count > 0 && normalized[^1].Role == role)
                normalized[^1] = (role, $"{normalized[^1].Content}\n\n{m.Content}");
            else
                normalized.Add((role, m.Content));
        }

        // Only attach conversation breakpoints when CachePolicy == Conversation (avoids 1.25x cache write waste on one-off calls).
        int lastIdx = cachePolicy == CachePolicy.Conversation ? normalized.Count - 1 : -1;
        int prevIdx = cachePolicy == CachePolicy.Conversation && normalized.Count >= 2 ? normalized.Count - 2 : -1;
        var chatMsgs = normalized
            .Select((m, idx) => new
            {
                role = m.Role,
                content = BuildContent(m.Content, withCacheControl: idx == lastIdx || idx == prevIdx)
            })
            .ToArray();

        bool cacheSystem = cachePolicy is CachePolicy.SystemOnly or CachePolicy.Conversation;
        object[] systemBlocks = string.IsNullOrWhiteSpace(systemPrompt)
            ? Array.Empty<object>()
            : (cacheSystem
                ? new object[]
                {
                    new
                    {
                        type = "text",
                        text = systemPrompt,
                        cache_control = new { type = "ephemeral" }
                    }
                }
                : new object[]
                {
                    new
                    {
                        type = "text",
                        text = systemPrompt
                    }
                });

        var payload = new
        {
            model,
            max_tokens = 8192,
            system = systemBlocks,
            messages = chatMsgs
        };
        return JsonSerializer.Serialize(payload);
    }

    public string ExtractResponseText(JsonElement root)
    {
        if (root.TryGetProperty("content", out var contentArr) &&
            contentArr.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in contentArr.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var s = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                }
            }
            if (parts.Count > 0) return string.Join("\n", parts);
        }
        return "";
    }

    public TokenUsage ExtractUsage(JsonElement root)
    {
        int uncachedInput = 0, cacheWrite = 0, cacheRead = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) uncachedInput = itv;
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cw) && cw.TryGetInt32(out var cwv)) cacheWrite = cwv;
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt32(out var crv)) cacheRead = crv;
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) output = otv;
        }
        return new TokenUsage(uncachedInput, cacheWrite, cacheRead, output);
    }

    public string? ExtractStopReason(JsonElement root)
    {
        if (root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
        {
            var val = sr.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }
        return null;
    }

    private static object BuildContent(string text, bool withCacheControl)
    {
        var enriched = AttachmentExtractor.EnrichTextWithDocumentAttachments(text);
        var images = AttachmentExtractor.ExtractBinaryAttachments(text, includePdf: false);
        if (images.Count == 0 && !withCacheControl) return enriched;

        if (images.Count == 0)
        {
            return new object[]
            {
                new { type = "text", text = enriched, cache_control = new { type = "ephemeral" } }
            };
        }

        var blocks = new List<object> { new { type = "text", text = enriched } };
        for (int i = 0; i < images.Count; i++)
        {
            var (mime, b64) = images[i];
            if (withCacheControl && i == images.Count - 1)
            {
                blocks.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = mime, data = b64 },
                    cache_control = new { type = "ephemeral" }
                });
            }
            else
            {
                blocks.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = mime, data = b64 }
                });
            }
        }
        return blocks;
    }
}
