using System.Net.Http.Headers;
using System.Text.Json;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Agent.Providers;

/// <summary>
/// Default adapter for OpenAI / Azure AI Foundry / Ollama / vLLM / Groq chat-completions endpoints.
/// </summary>
public sealed class OpenAiCompatibleProviderAdapter : ILlmProviderAdapter
{
    public int Priority => 100;

    public bool CanHandle(string endpoint) => true;

    public void ApplyAuthHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("api-key", apiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string BuildRequestJson(IReadOnlyList<ModelMessage> messages, string model)
    {
        var payload = new
        {
            model,
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Role == "user" ? BuildContent(m.Content) : (object)m.Content
            }).ToArray(),
            temperature = 0.2
        };
        return JsonSerializer.Serialize(payload);
    }

    public string ExtractResponseText(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var contentEl) &&
            contentEl.ValueKind == JsonValueKind.String)
        {
            return contentEl.GetString() ?? "";
        }

        if (root.TryGetProperty("output_text", out var outText) && outText.ValueKind == JsonValueKind.String)
            return outText.GetString() ?? "";

        return "";
    }

    public (int Input, int Output, int Cache) ExtractUsage(JsonElement root)
    {
        int input = 0, output = 0, cache = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv)) input = ptv;
            if (usage.TryGetProperty("completion_tokens", out var ctEl) && ctEl.TryGetInt32(out var ctv)) output = ctv;
            if (usage.TryGetProperty("prompt_tokens_details", out var ptd) &&
                ptd.ValueKind == JsonValueKind.Object &&
                ptd.TryGetProperty("cached_tokens", out var cachedEl) &&
                cachedEl.TryGetInt32(out var cv))
            {
                cache = cv;
            }
        }
        return (input, output, cache);
    }

    private static object BuildContent(string text)
    {
        var enriched = AttachmentExtractor.EnrichTextWithDocumentAttachments(text);
        var images = AttachmentExtractor.ExtractBinaryAttachments(text, includePdf: false);
        if (images.Count == 0) return enriched;

        var blocks = new List<object> { new { type = "text", text = enriched } };
        foreach (var (mime, b64) in images)
        {
            blocks.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{mime};base64,{b64}" }
            });
        }
        return blocks;
    }
}
