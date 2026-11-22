namespace AiSecretary.Desktop.Agent;

public enum ModelTier
{
    Simple,
    Complex
}

public enum CachePolicy
{
    /// <summary>Do not attach prompt-cache breakpoints (one-off background calls; avoids wasted 1.25x cache write fees).</summary>
    None,
    /// <summary>Cache only the static system prompt prefix.</summary>
    SystemOnly,
    /// <summary>Cache the system prompt plus conversation history prefix breakpoints (including trailing message).</summary>
    Conversation,
    /// <summary>
    /// Reuse existing system + preceding conversation prefix cache (breakpoint at <c>n - 3</c>) WITHOUT caching
    /// the trailing assistant/user pair (avoids 1.25x cache write surcharge on one-off final-reply observations).
    /// </summary>
    PrefixReadOnly
}

public enum LlmPriority
{
    /// <summary>User-facing turn (routing, planner, final reply) — prioritized ahead of background queues.</summary>
    Interactive,
    /// <summary>Background automation (reflection, triage, first-visit probe, optimizer, proactive, holiday sync).</summary>
    Background
}

public sealed record LlmCallOptions(
    string Agent,
    ModelTier Tier = ModelTier.Simple,
    CachePolicy CachePolicy = CachePolicy.None,
    LlmPriority Priority = LlmPriority.Background);

public sealed record ModelMessage(string Role, string Content);

public sealed record TokenUsage(
    int UncachedInput,
    int CacheWrite,
    int CacheRead,
    int Output);

public sealed record ModelResponse(
    string Content,
    int EstimatedTokens,
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheTokens = 0,
    int CacheWriteTokens = 0,
    string? StopReason = null);

public interface IModelGateway
{
    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        CancellationToken ct = default)
        => CompleteAsync(messages, new LlmCallOptions("Unknown", ModelTier.Simple, CachePolicy.None, LlmPriority.Background), ct);

    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        ModelTier tier,
        CancellationToken ct = default)
        => CompleteAsync(messages, new LlmCallOptions("Unknown", tier, CachePolicy.None, LlmPriority.Background), ct);

    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        LlmCallOptions options,
        CancellationToken ct = default);
}
