using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

namespace AnalepticLogParser.Services;

public sealed class DefaultAnthropicClientFactory : IAnthropicClientFactory
{
    public IAnthropicClient Create(string apiKey) =>
        new AnthropicClientAdapter(new AnthropicClient(new APIAuthentication(apiKey)));
}

internal sealed class AnthropicClientAdapter : IAnthropicClient
{
    private readonly AnthropicClient _inner;

    public AnthropicClientAdapter(AnthropicClient inner) => _inner = inner;

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> conversationHistory,
        CancellationToken ct = default)
    {
        var sdkMessages = conversationHistory
            .Select(m => new Message(
                m.Role == "user" ? RoleType.User : RoleType.Assistant,
                m.Content))
            .ToList();

        var parameters = new MessageParameters
        {
            Model = AnthropicModels.Claude45Sonnet,
            MaxTokens = 512,
            Stream = false,
            System = [new SystemMessage(systemPrompt)],
            Messages = sdkMessages
        };

        var response = await _inner.Messages.GetClaudeMessageAsync(parameters, ct);

        return response.Message.Content is List<ContentBase> blocks
            ? string.Join("", blocks.OfType<TextContent>().Select(t => t.Text ?? ""))
            : response.Message.ToString() ?? string.Empty;
    }

    public void Dispose() => _inner.Dispose();
}
