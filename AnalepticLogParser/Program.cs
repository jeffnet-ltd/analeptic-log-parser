using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using AnalepticLogParser.Models;
using AnalepticLogParser.Services;
using Gradio.Net;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Inbound rate limiting ────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("StrictUiLimit", opt =>
    {
        opt.PermitLimit = 2;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Application services ─────────────────────────────────────────────────────
builder.Services.AddGradio();
builder.Services.AddSingleton<IAnthropicClientFactory, DefaultAnthropicClientFactory>();
builder.Services.AddSingleton<ILogAgentService, LogAgentService>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

app.UseRateLimiter();
app.UseGradio(await CreateUi());
app.Run();

// ── UI factory ────────────────────────────────────────────────────────────────
async Task<Blocks> CreateUi()
{
    var agentService = app.Services.GetRequiredService<ILogAgentService>();
    var httpContextAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();

    // Mirror of StrictUiLimit (2 req / min / IP) enforced inside the handler
    // so we can surface a friendly message instead of a raw 429.
    var rateLimitStore = new ConcurrentDictionary<string, (int Count, DateTime WindowStart)>();

    const string MockLog =
        "2024-01-15 08:00:01 INFO  [AppStartup] Application initializing...\n" +
        "2024-01-15 08:00:02 INFO  [Config] Loading configuration from appsettings.json\n" +
        "2024-01-15 08:00:02 DEBUG [Pool] Checking connection pool — target=db-primary:5432\n" +
        "2024-01-15 08:00:03 WARN  [Pool] Pool saturation at 95% (190/200 connections in use)\n" +
        "2024-01-15 08:00:03 ERROR [Database] Connection pool exhausted after 30 s timeout\n" +
        "System.TimeoutException: The operation has timed out.\n" +
        "   at App.Database.ConnectionPool.AcquireAsync() in ConnectionPool.cs:line 142\n" +
        "   at App.Repositories.UserRepository.GetByIdAsync(Int32 id) in UserRepository.cs:line 58\n" +
        "   at App.Services.AuthService.ValidateSessionAsync() in AuthService.cs:line 31\n" +
        "2024-01-15 08:00:03 FATAL [AppStartup] Critical dependency unavailable — aborting startup\n" +
        "System.AggregateException: One or more errors occurred. (The operation has timed out.)\n" +
        "   at App.Services.StartupOrchestrator.RunAsync() in StartupOrchestrator.cs:line 77\n" +
        "2024-01-15 08:00:04 ERROR [HealthCheck] Database health check FAILED: connection timeout\n" +
        "2024-01-15 08:00:04 ERROR [HealthCheck] Readiness probe returning HTTP 503\n" +
        "2024-01-15 08:00:04 INFO  [Metrics] Emitting startup failure telemetry to DataDog\n";

    using (var blocks = gr.Blocks())
    {
        gr.Markdown(
            "# Analeptic Log Parser\n" +
            "Paste a raw application log below and click **Run Triage** to receive a " +
            "structured SRE analysis powered by Claude.");

        gr.Markdown("---");

        // ── Inputs ─────────────────────────────────────────────────────────
        Textbox logInput, apiKeyInput, accessCodeInput;
        Button loadMockBtn, runBtn;

        using (gr.Row())
        {
            using (gr.Column())
            {
                logInput = gr.Textbox(
                    label: "Raw Log Input",
                    lines: 14,
                    placeholder: "Paste your raw application log here...");
                loadMockBtn = gr.Button("Load Mock Log Sample");
            }

            using (gr.Column())
            {
                apiKeyInput = gr.Textbox(
                    label: "Personal Anthropic API Key (optional)",
                    placeholder: "sk-ant-...");

                accessCodeInput = gr.Textbox(
                    label: "Recruiter Access Code",
                    placeholder: "Enter access code...");

                runBtn = gr.Button("Run Triage");
            }
        }

        gr.Markdown("---");

        // ── Outputs ────────────────────────────────────────────────────────
        Textbox telemetryOutput, jsonOutput;
        Component playbookOutput;

        using (gr.Row())
        {
            telemetryOutput = gr.Textbox(
                label: "Live Console Telemetry",
                lines: 8,
                interactive: false);

            jsonOutput = gr.Textbox(
                label: "Validated JSON Report Payload",
                lines: 8,
                interactive: false);
        }

        playbookOutput = gr.Markdown("*Run Triage to generate the SRE Playbook...*");

        // ── Mock log loader ────────────────────────────────────────────────
        await loadMockBtn.Click(
            fn: async (inp) => await Task.FromResult(gr.Output(MockLog)),
            inputs: new Component[0],
            outputs: new Component[] { logInput }
        );

        // ── Main triage action (bound to StrictUiLimit policy) ─────────────
        await runBtn.Click(
            fn: async (inp) =>
            {
                // Enforce StrictUiLimit: 2 requests per minute per IP
                string clientIp = httpContextAccessor.HttpContext
                    ?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var now = DateTime.UtcNow;
                var entry = rateLimitStore.GetOrAdd(clientIp, _ => (0, now));

                // ============================================================================
                // DESIGN NOTE (AI Collaboration): ASP.NET's UseRateLimiter() middleware
                // returns a raw HTTP 429 that Gradio.Net swallows — the user never sees it.
                // I directed a second enforcement layer here inside the click handler using a
                // ConcurrentDictionary to mirror the 2 req/min/IP policy and surface a
                // friendly message instead of a silent failure.
                // ============================================================================
                if (now - entry.WindowStart > TimeSpan.FromMinutes(1))
                {
                    rateLimitStore[clientIp] = (1, now);
                }
                else if (entry.Count >= 2)
                {
                    const string limitMsg = "Demo limited to 2 runs per minute to control costs.";
                    return gr.Output(limitMsg, "", $"### Rate Limited\n\n{limitMsg}");
                }
                else
                {
                    rateLimitStore[clientIp] = (entry.Count + 1, entry.WindowStart);
                }

                string rawLog = Textbox.Payload(inp.Data[0]) ?? "";
                string apiKey = Textbox.Payload(inp.Data[1]) ?? "";
                string accessCode = Textbox.Payload(inp.Data[2]) ?? "";

                var telemetry = new StringBuilder();
                telemetry.AppendLine("[INFO] Triage started.");

                try
                {
                    telemetry.AppendLine("[INFO] Invoking AI agent loop...");
                    var report = await agentService.ExecuteTriageAsync(rawLog, apiKey, accessCode);
                    string reportJson = JsonSerializer.Serialize(
                        report, new JsonSerializerOptions { WriteIndented = true });
                    telemetry.AppendLine("[INFO] Triage analysis complete.");

                    telemetry.AppendLine("[INFO] Generating SRE Playbook...");
                    string playbook = await GeneratePlaybookAsync(report, apiKey, accessCode);
                    telemetry.AppendLine("[INFO] Playbook ready.");

                    return gr.Output(telemetry.ToString(), reportJson, playbook);
                }
                catch (UnauthorizedAccessException)
                {
                    telemetry.AppendLine("[ERROR] Access denied — invalid access code and no API key provided.");
                    return gr.Output(telemetry.ToString(), "", "");
                }
                catch (Exception ex)
                {
                    telemetry.AppendLine($"[ERROR] {ex.Message}");
                    return gr.Output(telemetry.ToString(), "", "");
                }
            },
            inputs: new Component[] { logInput, apiKeyInput, accessCodeInput },
            outputs: new Component[] { telemetryOutput, jsonOutput, playbookOutput }
        );

        return blocks;
    }
}

// ── SRE Playbook generator ────────────────────────────────────────────────────
async Task<string> GeneratePlaybookAsync(
    TriageReport report,
    string providedKey,
    string accessCode)
{
    try
    {
        string? apiKey = accessCode == "AnalepticMongoose"
            ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? providedKey
            : providedKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            return "*(No API key available — SRE Playbook generation skipped.)*";

        using var client = new AnthropicClient(new APIAuthentication(apiKey));

        var messages = new List<Message>
        {
            new Message(RoleType.User,
                "Based on this SRE triage report, write a concise SRE Playbook with " +
                "exactly 3-4 numbered, actionable remediation steps. Include shell commands " +
                "or config snippets where relevant. Format as clean Markdown with headers.\n\n" +
                $"**Error:** {report.Error}\n" +
                $"**Line:** {report.Line}\n" +
                $"**Root Cause:** {report.Description}")
        };

        var parameters = new MessageParameters
        {
            Model = AnthropicModels.Claude45Sonnet,
            MaxTokens = 1024,
            Stream = false,
            Messages = messages
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);
        return response.Message.Content is List<ContentBase> contentBlocks
            ? string.Join("", contentBlocks.OfType<TextContent>().Select(t => t.Text ?? ""))
            : response.Message.ToString() ?? "";
    }
    catch
    {
        return "*(SRE Playbook generation failed — check telemetry for details.)*";
    }
}
