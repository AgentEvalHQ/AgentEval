# Auto-Audit — benchmark endpoints on honesty, safety & cost (Glass Box)

**"Benchmark local vs. hosted endpoints for honesty, safety, and cost — end-to-end, in .NET, in one command."** The auto-audit runs the same scenario through the full Glass Box stack on each endpoint and produces a single ranked, cross-endpoint comparison.

## What it measures (per endpoint)

| Axis | Source |
|---|---|
| **Honesty** | [Trace Fidelity](../benchmarks/trace-fidelity.md) — reconciles the agent-boundary account against the chat-boundary truth (hidden retries, suppressed finish reasons, …) |
| **Safety** | gate Block count — PII / injection / safety verdicts from the runtime [policy gate](../guardrails.md) |
| **Cost** | total prompt + completion tokens, total latency |
| **Reliability** | whether the scenario completed |

Endpoints are ranked best-first: completed → highest fidelity → fewest gate blocks → lowest token cost.

## CLI (offline demo)

```bash
agenteval bench autoaudit --out ./autoaudit.md
```

The offline demo (no credentials) compares three scripted endpoints with deliberately different behaviour:

| Rank | Endpoint | Fidelity | Gate blocks | Notes |
|---|---|---|---|---|
| 1 | Azure-GPT-4o-mini | 100% | 0 | clean |
| 2 | Ollama-Llama3.1 | 90% | 0 | one silent retry the framework hid |
| 3 | DeepSeek-V3 | 85% | 1 | leaked an SSN (redacted by the post-gate) under a suppressed `content_filter` |

(Also available interactively: samples → **I. Observability → Auto-Audit**.)

## Wiring real endpoints

The comparison engine (`AutoAuditRunner.Evaluate` / `Compare`) is endpoint-agnostic: it consumes a per-endpoint agent-boundary trace + chat-boundary trace and emits a ranked report. To audit real endpoints, build each `IChatClient` with `EndpointFactory.CreateOpenAICompatible(endpoint, model, apiKey)` (Ollama, LM Studio, vLLM, Groq, Together, DeepSeek, …) or `EndpointFactory.CreateAzure(...)`, run your scenario through a Glass-Box-instrumented pipeline (recording + gates, per [the workflow pre-wiring pattern](../workflows.md)), and pass the resulting trace pair to `AutoAuditRunner.Evaluate` per endpoint.

> Compliance (GDPR / EU AI Act) and Red Team resistance compose on top via their existing `agenteval bench {gdpr,eu-ai-act}` and `agenteval redteam` commands per endpoint — the auto-audit focuses on the Glass Box differentiators (honesty + inline safety + cost) that no other .NET toolkit measures.

## Related

- [Trace Fidelity](../benchmarks/trace-fidelity.md) · [Runtime policy gate](../guardrails.md) · [Tracing](../tracing.md) · [Workflows](../workflows.md)
