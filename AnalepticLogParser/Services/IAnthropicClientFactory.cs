namespace AnalepticLogParser.Services;

public interface IAnthropicClientFactory
{
    IAnthropicClient Create(string apiKey);
}
