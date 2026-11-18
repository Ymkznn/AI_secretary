namespace AiSecretary.Desktop.Agent;

public enum ModelTier
{
    Simple,
    Complex
}

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelResponse(
    string Content,
    int EstimatedTokens,
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheTokens = 0);

public interface IModelGateway
{
    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        CancellationToken ct = default)
        => CompleteAsync(messages, ModelTier.Simple, ct);

    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        ModelTier tier,
        CancellationToken ct = default);
}
