using System.Text.Json;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Agent.Providers;

public sealed class GeminiProviderAdapter : ILlmProviderAdapter
{
    public int Priority => 10;

    public bool CanHandle(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".aiplatform.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("aiplatform.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase);
    }

    public string PrepareEndpoint(string endpoint, string model)
    {
        if (!endpoint.Contains(":generateContent", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = endpoint.TrimEnd('/');
            return $"{trimmed}/models/{Uri.EscapeDataString(model)}:generateContent";
        }
        return endpoint;
    }

    public void ApplyAuthHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("x-goog-api-key", apiKey);
    }

    public string BuildRequestJson(IReadOnlyList<ModelMessage> messages, string model, CachePolicy cachePolicy = CachePolicy.Conversation)
    {
        var systemPrompt = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var contents = messages
            .Where(m => m.Role != "system")
            .Select(m => new
            {
                role = m.Role == "assistant" ? "model" : "user",
                parts = BuildParts(m.Content)
            })
            .ToArray();

        var payload = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = new { temperature = 0.2 }
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["systemInstruction"] = new { parts = new[] { new { text = systemPrompt } } };
        }
        return JsonSerializer.Serialize(payload);
    }

    public string ExtractResponseText(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var gContent) &&
            gContent.TryGetProperty("parts", out var gParts) &&
            gParts.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var p in gParts.EnumerateArray())
            {
                if (p.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                {
                    var s = tEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                }
            }
            if (parts.Count > 0) return string.Join("\n", parts);
        }
        return "";
    }

    public TokenUsage ExtractUsage(JsonElement root)
    {
        int input = 0, output = 0, cache = 0;
        if (root.TryGetProperty("usageMetadata", out var gmUsage) && gmUsage.ValueKind == JsonValueKind.Object)
        {
            if (gmUsage.TryGetProperty("promptTokenCount", out var pt) && pt.TryGetInt32(out var ptv)) input = ptv;
            if (gmUsage.TryGetProperty("candidatesTokenCount", out var ctEl) && ctEl.TryGetInt32(out var ctv)) output = ctv;
            if (gmUsage.TryGetProperty("cachedContentTokenCount", out var cc) && cc.TryGetInt32(out var ccv)) cache = ccv;
        }
        var nonCachedInput = Math.Max(0, input - cache);
        return new TokenUsage(nonCachedInput, 0, cache, output);
    }

    private static object[] BuildParts(string text)
    {
        var enriched = AttachmentExtractor.EnrichTextWithDocumentAttachments(text);
        var parts = new List<object> { new { text = enriched } };
        foreach (var (mime, b64) in AttachmentExtractor.ExtractBinaryAttachments(text, includePdf: true))
        {
            parts.Add(new { inlineData = new { mimeType = mime, data = b64 } });
        }
        return parts.ToArray();
    }
}
