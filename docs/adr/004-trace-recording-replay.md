# ADR-004: Trace Recording and Replay for Time-Travelling Debugging

**Status:** Accepted  
**Date:** January 7, 2026  
**Authors:** AgentEval Team  
**Supersedes:** None  
**Related:** ADR-002 (Result Directory Structure)

---

## Context

AgentEval needs deterministic, fast, and cost-free test execution for CI/CD pipelines. Currently, every test run requires live LLM API calls, which are:

1. **Expensive** — API costs accumulate with every test run
2. **Slow** — Network latency adds 500ms-5s per request
3. **Non-deterministic** — Same prompt can yield different responses
4. **Unreliable** — Depends on external service availability

Additionally, debugging agent failures is difficult because execution context is ephemeral — once a test fails, the exact tool calls, responses, and timing are lost.

### Research Findings

We evaluated existing patterns and standards:

| Pattern | Language | Approach | Suitability |
|---------|----------|----------|-------------|
| **VCR (Ruby)** | Ruby | HTTP cassettes (YAML/JSON) | HTTP-level only, not agent-aware |
| **Azure SDK Test Framework** | .NET | Test Proxy with Record/Playback | Mature but complex, HTTP-focused |
| **scotch / Betamax.NET** | .NET | VCR ports | HTTP-focused, not maintained |
| **OpenTelemetry Traces** | Multi | Spans with attributes | Observability, not replay |
| **Promptfoo caching** | Node | Response cache | Simple cache, not full trace |
| **LangSmith** | Python | Tracing + evaluation | SaaS-focused, Python-only |

**Key Finding:** No existing standard captures agent-specific concerns:
- Tool call semantics (name, arguments, results, ordering)
- Multi-turn conversation state
- Streaming token chunks and TTFT
- Performance metrics (latency, cost)

---

## Decision

### 1. Create AgentEval-Specific Trace Format

**We will define our own JSON-based trace format** rather than adopt an existing standard.

**Rationale:**
- Agent-specific concerns are not covered by HTTP-level patterns
- We can design for our exact use cases
- Enables tool call-aware matching and validation
- Can export to OpenTelemetry for observability if needed
- Positions AgentEval as a potential industry standard

### 2. Trace Recording via Wrapping Pattern

**TraceRecorder wraps `IEvaluableAgent`** and intercepts all calls, recording them to an `AgentTrace` object.

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         RECORDING FLOW                                   │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│   Test Code           TraceRecordingAgent          Actual Agent         │
│       │                      │                          │               │
│       │──InvokeAsync(prompt)─▶│                          │               │
│       │                      │──Record Request──────────▶│               │
│       │                      │                          │               │
│       │                      │◀─────────Response─────────│               │
│       │                      │──Record Response─────────▶│               │
│       │◀─────Response────────│                          │               │
│       │                      │                          │               │
│                                                                         │
│   On disposal: Save AgentTrace to .trace.json                           │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

**Why wrapping, not middleware:**
- Works with any `IEvaluableAgent` implementation
- No changes required to test code
- Transparent to the evaluation harness
- Consistent with existing adapter patterns (ChatClientAgentAdapter)

### 3. Trace Replay via Mock Agent

**TraceReplayingAgent implements `IEvaluableAgent`** and returns recorded responses.

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          REPLAY FLOW                                     │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│   Test Code         TraceReplayingAgent            [No API Calls]       │
│       │                      │                                          │
│       │──InvokeAsync(prompt)─▶│                                          │
│       │                      │──Find matching entry──▶                  │
│       │                      │                                          │
│       │◀─────Recorded Response│                                          │
│       │                      │                                          │
│                                                                         │
│   Execution: Instantaneous, deterministic, zero cost                    │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

**Request matching strategy:** Index-based (simplest, deterministic)
- Entry N in trace matches call N in test
- Future: Optional content-based matching for flexibility

### 4. NOT a New Exporter — Separate Concern

**TraceRecorder is NOT added to `IResultExporter`.**

**Rationale:**
- `IResultExporter` exports **evaluation results** after execution
- `TraceRecorder` captures **execution traces** during execution
- Different lifecycles, different data models
- Mixing concerns would complicate both

**Architecture:**
```
┌─────────────────────────────────────────────────────────────────────────┐
│                        SEPARATION OF CONCERNS                            │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  DURING EXECUTION                    AFTER EXECUTION                    │
│  ─────────────────                   ─────────────────                  │
│  TraceRecorder                       IResultExporter                    │
│  ├─ Wraps IEvaluableAgent             ├─ JsonExporter                    │
│  ├─ Records requests/responses       ├─ JUnitXmlExporter                │
│  └─ Outputs: .trace.json             ├─ MarkdownExporter                │
│                                      └─ TrxExporter                     │
│                                                                         │
│  Data: AgentTrace                    Data: EvaluationReport             │
│  Purpose: Replay & Debug             Purpose: CI/CD & Reporting         │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 5. Trace File Format

**JSON with versioned schema:**

```json
{
  "version": "1.0",
  "traceName": "weather_test",
  "capturedAt": "2026-01-07T10:30:00Z",
  "agentName": "WeatherAgent",
  "entries": [
    {
      "type": "Request",
      "index": 0,
      "timestamp": "2026-01-07T10:30:00.000Z",
      "prompt": "What's the weather in Paris?"
    },
    {
      "type": "Response",
      "index": 0,
      "timestamp": "2026-01-07T10:30:00.500Z",
      "text": "The weather in Paris is 22°C and sunny.",
      "durationMs": 500,
      "toolCalls": [...],
      "tokenUsage": { "prompt": 50, "completion": 30 }
    }
  ],
  "performance": {
    "totalDurationMs": 500,
    "totalPromptTokens": 50,
    "totalCompletionTokens": 30
  }
}
```

**Schema hosted at:** `https://agenteval.dev/schemas/trace-v1.json` (future)

### 6. CLI Integration

```bash
# Record mode
agenteval test --record --output ./traces

# Replay mode
agenteval test --replay ./traces

# View a trace
agenteval trace show ./traces/weather_test.trace.json
```

---

## Consequences

### Positive

1. **100x faster tests** — Replay is instantaneous (no network)
2. **Zero API cost** — Recorded traces can run indefinitely
3. **Deterministic** — Same trace = same result every time
4. **Debuggable** — Traces can be inspected, edited, shared
5. **CI/CD friendly** — Record nightly, replay on every PR
6. **Future-proof** — Versioned format allows evolution

### Negative

1. **Storage overhead** — Traces are stored in repo or separate location
2. **Drift risk** — Agent changes may invalidate old traces
3. **Maintenance** — Traces must be re-recorded when prompts change
4. **Complexity** — Two modes to understand and manage

### Mitigations

- **Storage:** Compress traces; store in separate assets repo (Azure SDK pattern)
- **Drift:** Provide `--force-record` and automated drift detection
- **Maintenance:** CI job to detect trace staleness
- **Complexity:** Good defaults; record-once semantics

---

## Implementation

### Phase 1: Core Classes ✅ IMPLEMENTED
- `AgentTrace` — Data model for recorded traces ([AgentTrace.cs](https://github.com/joslat/AgentEval/blob/main/src/AgentEval/Tracing/AgentTrace.cs))
- `TraceEntry` — Individual request/response entry
- `TraceRecordingAgent` — Wrapper that records ([TraceRecordingAgent.cs](https://github.com/joslat/AgentEval/blob/main/src/AgentEval/Tracing/TraceRecordingAgent.cs))
- `TraceReplayingAgent` — Mock that replays ([TraceReplayingAgent.cs](https://github.com/joslat/AgentEval/blob/main/src/AgentEval/Tracing/TraceReplayingAgent.cs))
- `TraceSerializer` — JSON serialization ([TraceSerializer.cs](https://github.com/joslat/AgentEval/blob/main/src/AgentEval/Tracing/TraceSerializer.cs))
- Tests: 17 tests in [TraceRecordingAndReplayTests.cs](https://github.com/joslat/AgentEval/blob/main/tests/AgentEval.Tests/Tracing/TraceRecordingAndReplayTests.cs)

### Phase 2: Integration (Pending)
- evaluation harness integration (`EvaluationOptions.TraceRecorder`)
- CLI commands (`agenteval test --record/--replay`)

### Phase 3: Advanced Features (Pending)
- Streaming chunk recording/replay
- Content-based matching
- Trace diff for debugging
- VS Code extension integration

---

## Alternatives Considered

### A. Use Azure SDK Test Proxy

**Rejected:** Too heavy, HTTP-focused, requires external process.

### B. Use OpenTelemetry Format

**Rejected:** Designed for observability, not replay. Could add as parallel export later.

### C. Add to IResultExporter

**Rejected:** Wrong abstraction — exporters run after tests, traces capture during.

### D. HTTP-Level Recording (VCR Pattern)

**Rejected:** Doesn't capture tool call semantics, multi-turn state, or agent-level concerns.

---

## References

- [VCR (Ruby)](https://github.com/vcr/vcr) — Original cassette pattern
- [Azure SDK Test Framework](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/core/Azure.Core.TestFramework) — .NET gold standard
- [OpenTelemetry Traces](https://opentelemetry.io/docs/concepts/signals/traces/) — Observability standard
- ADR-002: Result Directory Structure — Related file organization decisions

---

*Accepted: January 7, 2026*
