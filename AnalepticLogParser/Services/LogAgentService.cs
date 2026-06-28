using System.Text;
using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using AnalepticLogParser.Models;

namespace AnalepticLogParser.Services;

public sealed class LogAgentService : ILogAgentService
{
    private const string AccessPassphrase = "AnalepticMongoose";
    private const int LogSizeThresholdBytes = 50 * 1024;
    private const int ContextLines = 20;
    private const int MaxRetries = 3;

    private static readonly string SystemPrompt =
        """
        You are a Site Reliability Engineer (SRE) log analysis agent.
        Analyze the supplied log and respond with ONLY a raw JSON object — no markdown, no code fences, no explanation.
        The JSON must exactly match this schema:
        { "Error": "<string>", "Line": <positive integer>, "Description": "<string>" }
        - Error: the primary error identifier or exception class name.
        - Line: the 1-based line number in the log where the critical error occurs (must be > 0).
        - Description: a concise root-cause summary in one sentence.
        Output nothing except the JSON object.
        """;

    public async Task<TriageReport> ExecuteTriageAsync(
        string rawLog,
        string providedKey,
        string accessCode)
    {
        string apiKey = ResolveApiKey(accessCode, providedKey);
        string processedLog = TruncateIfNeeded(rawLog);

        using var client = new AnthropicClient(new APIAuthentication(apiKey));

        var messages = new List<Message>
        {
            new Message(RoleType.User, $"<log>\n{processedLog}\n</log>")
        };

        var parameters = new MessageParameters
        {
            Model = AnthropicModels.Claude45Sonnet,
            MaxTokens = 512,
            Stream = false,
            System = [new SystemMessage(SystemPrompt)],
            Messages = messages
        };

        int retryCount = 0;
        var telemetry = new StringBuilder();

        while (retryCount < MaxRetries)
        {
            try
            {
                var response = await client.Messages.GetClaudeMessageAsync(parameters);
                string rawJson = response.Message.Content is List<ContentBase> blocks
                    ? string.Join("", blocks.OfType<TextContent>().Select(t => t.Text ?? ""))
                    : response.Message.ToString() ?? string.Empty;

                messages.Add(response.Message);

                TriageReport? report;
                try
                {
                    report = JsonSerializer.Deserialize<TriageReport>(
                        rawJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException je)
                {
                    string validationError = $"JSON parse error: {je.Message}";
                    telemetry.AppendLine($"[WARN] attempt={retryCount + 1} {validationError}");
                    messages.Add(new Message(RoleType.User,
                        $"Your previous response was not valid JSON.\n" +
                        $"Malformed response: {rawJson}\n" +
                        $"Error: {validationError}\n" +
                        $"Please respond with only the raw JSON object."));
                    retryCount++;
                    continue;
                }

                if (report is null || report.Line <= 0)
                {
                    string validationError = report is null
                        ? "Deserialization returned null."
                        : $"Line must be a positive integer, got {report.Line}.";

                    telemetry.AppendLine($"[WARN] attempt={retryCount + 1} validation failed: {validationError}");
                    messages.Add(new Message(RoleType.User,
                        $"Validation failed.\n" +
                        $"Malformed response: {rawJson}\n" +
                        $"Error: {validationError}\n" +
                        $"Please correct the JSON and ensure Line is a positive integer."));
                    retryCount++;
                    continue;
                }

                return report;
            }
            catch (RateLimitsExceeded rle)
            {
                TimeSpan delay = rle.RateLimits?.RetryAfter ?? TimeSpan.FromSeconds(60);
                telemetry.AppendLine($"[INFO] Rate limited (HTTP 429). Waiting {delay.TotalSeconds:F0}s before retry.");
                await Task.Delay(delay);
                // Do NOT increment retryCount — external rate limit is not a logic failure.
            }
            catch (Exception ex)
            {
                string safeMessage = ScrubKey(ex.Message, apiKey);
                throw new InvalidOperationException(
                    $"Triage agent encountered an unexpected error: {safeMessage}\n" +
                    $"Telemetry:\n{ScrubKey(telemetry.ToString(), apiKey)}");
            }
        }

        throw new InvalidOperationException(
            $"Triage failed after {MaxRetries} retries.\n" +
            $"Telemetry:\n{ScrubKey(telemetry.ToString(), apiKey)}");
    }

    private static string ResolveApiKey(string accessCode, string providedKey)
    {
        if (accessCode == AccessPassphrase)
        {
            string? envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
                return envKey;
        }

        if (!string.IsNullOrWhiteSpace(providedKey))
            return providedKey;

        throw new UnauthorizedAccessException("Access Denied");
    }

    private static string TruncateIfNeeded(string log)
    {
        if (Encoding.UTF8.GetByteCount(log) <= LogSizeThresholdBytes)
            return log;

        string[] lines = log.Split('\n');
        var includedIndices = new HashSet<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (ContainsCriticalKeyword(lines[i]))
            {
                int start = Math.Max(0, i - ContextLines);
                int end = Math.Min(lines.Length - 1, i + ContextLines);
                for (int j = start; j <= end; j++)
                    includedIndices.Add(j);
            }
        }

        if (includedIndices.Count == 0)
        {
            // No critical lines found — return the first 50KB slice directly.
            int byteLimit = LogSizeThresholdBytes;
            int charCount = 0;
            int byteCount = 0;
            foreach (char c in log)
            {
                byteCount += Encoding.UTF8.GetByteCount([c]);
                if (byteCount > byteLimit) break;
                charCount++;
            }
            return log[..charCount];
        }

        var sb = new StringBuilder();
        foreach (int idx in includedIndices.OrderBy(x => x))
            sb.AppendLine(lines[idx]);

        return sb.ToString();
    }

    private static bool ContainsCriticalKeyword(string line)
    {
        return line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || line.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase)
            || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase);
    }

    private static string ScrubKey(string text, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(text))
            return text;
        return text.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
    }
}
