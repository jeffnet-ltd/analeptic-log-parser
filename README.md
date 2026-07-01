# AnalepticLogParser

> **Live Demo:** [huggingface.co/spaces/JeffNet/analeptic-log-parser](https://huggingface.co/spaces/JeffNet/analeptic-log-parser) &nbsp;|&nbsp; **Source:** [github.com/jeffnet-ltd/analeptic-log-parser](https://github.com/jeffnet-ltd/analeptic-log-parser)

A production-ready ASP.NET Core 9 web application engineered to automate enterprise server log triage for SRE teams. This platform leverages an **Agentic AI Loop with Autonomous Self-Correction** using the Anthropic Claude SDK to isolate root-cause errors and output deterministic JSON payloads alongside Markdown remediation playbooks.

---

## The Tech Stack
* **Framework:** ASP.NET Core 9.0 (Hardened `aspnet:9.0` runtime container)
* **Frontend UI:** Gradio.Net
* **AI Orchestration:** Anthropic Claude SDK (Claude 3.5 Sonnet)
* **Testing Suite:** xUnit (100% deterministic via mocked client factories)
* **Deployment:** Docker / Hugging Face Spaces

---

## Features

* **Intelligent Log Truncation** — Logs over 50 KB are scanned for `ERROR`, `EXCEPTION`, and `FATAL` lines. Only those lines plus ±20 lines of context are forwarded to Claude, controlling token costs on large production log files.
* **Agentic Retry Loop** — A 3-attempt retry budget maintains full conversation history. If Claude returns malformed JSON or an invalid line number, the exact error is fed back into the prompt and Claude self-corrects in-process.
* **Dual-Authentication Gatekeeping** — Recruiter access code unlocks the server-side hosted key; users can also supply their own `sk-ant-...` key. All other requests are hard-denied before any API call is made.
* **In-Process Rate Limiting** — 2 requests per minute per IP enforced inside the Gradio click handler, returning a user-friendly message rather than a raw HTTP 429.
* **Docker-Ready** — Multi-stage build (`sdk:9.0` → `aspnet:9.0`), binds to port `7860` for zero-config Hugging Face Spaces deployment.

---

## AI-Accelerated Engineering (Built with Claude Code)
This project was planned over 2 days and executed in 1 day using **Claude Code** as an AI pair-programmer. Rather than treating the AI as a blind code generator, it was utilized as an architectural accelerator:
1. **Scaffolding & Boilerplate:** Claude Code rapidly generated the baseline ASP.NET infrastructure, allowing me to focus immediately on business logic and defensive programming boundaries.
2. **The Agentic Loop Challenge:** When Claude initially generated standard single-turn API calls, I systematically refactored the pipeline in C# to enforce a strict **3-tier retry budget**. If the LLM returns malformed JSON, the application catches the `JsonException`, wraps the compiler error, preserves the conversation history, and forces Claude to self-correct in-process.

---

## Enterprise Safeguards
* **Gatekeeping:** Features dual-authentication logic. It securely accepts user-provided API keys or checks a server-side token gatekeeper using the specific recruiter verification code.
* **Rate Limiting:** Enforces an in-process IP-based throttle (2 requests/min) to prevent upstream API token starvation and billing exploits.

---

## Running Locally

```bash
# 1. Clone the repo
git clone https://github.com/jeffnet-ltd/analeptic-log-parser.git
cd analeptic-log-parser

# 2. Set your Anthropic API key (required for the access code path)
$env:ANTHROPIC_API_KEY = "sk-ant-..."   # PowerShell
# export ANTHROPIC_API_KEY="sk-ant-..."  # bash

# 3. Run
dotnet run --project AnalepticLogParser/AnalepticLogParser.csproj
# → http://localhost:5273
```

---

## Running with Docker

```bash
docker build -t analeptic-log-parser .

docker run -p 7860:7860 \
  -e ANTHROPIC_API_KEY="sk-ant-..." \
  analeptic-log-parser
# → http://localhost:7860
```

---

## Environment Variables

| Variable | Required | Description |
|---|---|---|
| `ANTHROPIC_API_KEY` | When using access code | Server-side Anthropic API key used by the hosted demo. Not required if users supply their own key in the UI. |

---

## Running Tests

```bash
dotnet test AnalepticLogParser.Tests/AnalepticLogParser.Tests.csproj
# 5 tests, 0 real API calls — fully deterministic via StubAnthropicClientFactory
```
