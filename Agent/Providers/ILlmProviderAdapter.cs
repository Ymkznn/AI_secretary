using System.Text.Json;

namespace AiSecretary.Desktop.Agent.Providers;

/// <summary>
/// Strategy interface for LLM API protocol adapters (OpenAI-compatible, Anthropic, Gemini, etc.).
/// New providers can be added by implementing this interface without modifying the gateway.
/// </summary>
public interface ILlmProviderAdapter
{
    int Priority => 50;
    bool CanHandle(string endpoint);
    string PrepareEndpoint(string endpoint, string model) => endpoint;
    void ApplyAuthHeaders(HttpRequestMessage request, string apiKey);
    string BuildRequestJson(IReadOnlyList<ModelMessage> messages, string model, CachePolicy cachePolicy = CachePolicy.Conversation);
    string ExtractResponseText(JsonElement root);
    TokenUsage ExtractUsage(JsonElement root);
    string? ExtractStopReason(JsonElement root) => null;
}
