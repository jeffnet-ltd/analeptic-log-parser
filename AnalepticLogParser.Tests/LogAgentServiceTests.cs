using AnalepticLogParser.Services;
using Xunit;

namespace AnalepticLogParser.Tests;

// ── Stubs ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Returns responses in order from a pre-loaded queue — no real HTTP calls.
/// </summary>
internal sealed class StubAnthropicClient : IAnthropicClient
{
    private readonly Queue<string> _responses;

    public int CallCount { get; private set; }

    public StubAnthropicClient(params string[] responses) =>
        _responses = new Queue<string>(responses);

    public Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> conversationHistory,
        CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_responses.Dequeue());
    }

    public void Dispose() { }
}

/// <summary>
/// Always returns the same pre-configured stub, ignoring the api key.
/// </summary>
internal sealed class StubAnthropicClientFactory : IAnthropicClientFactory
{
    private readonly IAnthropicClient _client;

    public StubAnthropicClientFactory(IAnthropicClient client) => _client = client;

    public IAnthropicClient Create(string apiKey) => _client;
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class LogAgentServiceTests
{
    private const string SampleLog =
        "2024-01-15 08:00:03 ERROR [Database] Connection pool exhausted\n" +
        "System.TimeoutException: The operation timed out.\n" +
        "   at App.Database.ConnectionPool.AcquireAsync() in ConnectionPool.cs:line 142\n";

    /// <summary>
    /// The stub returns malformed JSON on call 1 and valid JSON on call 2.
    /// Expectations:
    ///   – retryCount is incremented to 1 (proved by CallCount == 2).
    ///   – The method ultimately returns a valid TriageReport.
    ///   – No real API calls are ever made.
    /// </summary>
    [Fact]
    public async Task ExecuteTriageAsync_IncrementsRetryCountOnMalformedJsonThenSucceeds()
    {
        // Arrange
        const string malformed = "DEFINITELY_NOT_JSON <oops />";
        const string valid =
            """{"Error":"System.TimeoutException","Line":142,"Description":"DB connection pool exhausted after 30 s timeout"}""";

        var stub = new StubAnthropicClient(malformed, valid);
        var service = new LogAgentService(new StubAnthropicClientFactory(stub));

        // Act
        var report = await service.ExecuteTriageAsync(
            rawLog: SampleLog,
            providedKey: "sk-test-placeholder-no-real-calls",
            accessCode: "");

        // Assert — stub was called exactly twice:
        //   call 1 → malformed JSON   → retryCount incremented to 1
        //   call 2 → valid JSON       → success (retryCount still 1, < MaxRetries=3)
        Assert.Equal(2, stub.CallCount);

        Assert.Equal("System.TimeoutException", report.Error);
        Assert.Equal(142, report.Line);
        Assert.False(string.IsNullOrWhiteSpace(report.Description));
    }

    /// <summary>
    /// When the first call returns valid JSON, the service should succeed in a single
    /// call with no retries (CallCount == 1, retryCount stays 0).
    /// </summary>
    [Fact]
    public async Task ExecuteTriageAsync_SucceedsImmediatelyWhenFirstResponseIsValid()
    {
        const string valid =
            """{"Error":"NullReferenceException","Line":57,"Description":"Null dereference in user lookup"}""";

        var stub = new StubAnthropicClient(valid);
        var service = new LogAgentService(new StubAnthropicClientFactory(stub));

        var report = await service.ExecuteTriageAsync(
            rawLog: SampleLog,
            providedKey: "sk-test-placeholder-no-real-calls",
            accessCode: "");

        Assert.Equal(1, stub.CallCount);
        Assert.Equal("NullReferenceException", report.Error);
        Assert.Equal(57, report.Line);
    }

    /// <summary>
    /// A Line value of 0 fails the positive-integer validation and must trigger a retry.
    /// Providing a valid response on the second call must still produce a correct result.
    /// </summary>
    [Fact]
    public async Task ExecuteTriageAsync_IncrementsRetryCountOnInvalidLineNumberThenSucceeds()
    {
        const string zeroLine =
            """{"Error":"IOException","Line":0,"Description":"File not found"}""";
        const string validLine =
            """{"Error":"IOException","Line":23,"Description":"Configuration file missing at startup"}""";

        var stub = new StubAnthropicClient(zeroLine, validLine);
        var service = new LogAgentService(new StubAnthropicClientFactory(stub));

        var report = await service.ExecuteTriageAsync(
            rawLog: SampleLog,
            providedKey: "sk-test-placeholder-no-real-calls",
            accessCode: "");

        // Line=0 → validation failure → retryCount→1 → second call succeeds
        Assert.Equal(2, stub.CallCount);
        Assert.Equal(23, report.Line);
        Assert.True(report.Line > 0);
    }

    /// <summary>
    /// Exhausting all retries (3 consecutive malformed responses) must throw
    /// InvalidOperationException rather than return a partial result.
    /// </summary>
    [Fact]
    public async Task ExecuteTriageAsync_ThrowsAfterMaxRetriesExhausted()
    {
        var stub = new StubAnthropicClient("bad1", "bad2", "bad3");
        var service = new LogAgentService(new StubAnthropicClientFactory(stub));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteTriageAsync(
                rawLog: SampleLog,
                providedKey: "sk-test-placeholder-no-real-calls",
                accessCode: ""));

        Assert.Equal(3, stub.CallCount);
    }

    /// <summary>
    /// Missing both a valid access code and a provided key must throw
    /// UnauthorizedAccessException before any API call is made.
    /// </summary>
    [Fact]
    public async Task ExecuteTriageAsync_ThrowsUnauthorizedWhenNoKeyProvided()
    {
        var stub = new StubAnthropicClient();
        var service = new LogAgentService(new StubAnthropicClientFactory(stub));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ExecuteTriageAsync(
                rawLog: SampleLog,
                providedKey: "",
                accessCode: ""));

        // Key gate fires before the factory is ever called
        Assert.Equal(0, stub.CallCount);
    }

    /// <summary>
    /// The passphrase gate is driven entirely by the ACCESS_PASSPHRASE env var —
    /// never hardcoded — so it must be rotatable without a source change. A matching
    /// access code should unlock the server-side ANTHROPIC_API_KEY.
    /// </summary>
    [Fact]
    public void TryResolveApiKey_MatchingAccessCodeUnlocksServerKey()
    {
        Environment.SetEnvironmentVariable("ACCESS_PASSPHRASE", "test-passphrase");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-server-key");
        try
        {
            string? resolved = LogAgentService.TryResolveApiKey(
                accessCode: "test-passphrase", providedKey: "");

            Assert.Equal("sk-test-server-key", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACCESS_PASSPHRASE", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    /// <summary>
    /// A wrong or absent access code must never fall back to the server-side key —
    /// only an explicitly provided key (or nothing) is returned.
    /// </summary>
    [Fact]
    public void TryResolveApiKey_WrongAccessCodeFallsBackToProvidedKeyOnly()
    {
        Environment.SetEnvironmentVariable("ACCESS_PASSPHRASE", "test-passphrase");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-server-key");
        try
        {
            string? resolved = LogAgentService.TryResolveApiKey(
                accessCode: "wrong-code", providedKey: "sk-user-supplied");

            Assert.Equal("sk-user-supplied", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACCESS_PASSPHRASE", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }
}
