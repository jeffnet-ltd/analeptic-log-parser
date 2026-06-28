namespace AnalepticLogParser.Services;

public interface IAnthropicClient : IDisposable
{
    Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> conversationHistory,
        CancellationToken ct = default);
}
