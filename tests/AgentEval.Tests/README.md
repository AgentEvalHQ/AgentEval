# AgentEval Tests

This project contains the automated tests for AgentEval. It lives at `tests/AgentEval.Tests`, targets **.NET 10**, and uses **xUnit**. The tests validate the core library in `src/AgentEval` and mirror the usage patterns demonstrated in `samples/AgentEval.Samples`.

## How the tests are architected for speed and low cost
- **Pure unit focus:** Tests exercise in-memory models, assertions, and helpers—no network calls or real AI providers are hit.
- **Deterministic fakes:** Metrics that normally depend on LLM output use `FakeChatClient` or inline stub classes (e.g., `TestMetric`, `TestPlugin`) to avoid external latency and cost.
- **Small fixtures:** Each file keeps local helper types close to the tests to minimize setup and cross-file coupling.
- **Fast runners:** Asynchronous surfaces are covered with short-lived tasks; timing-sensitive checks use controlled `DateTimeOffset`/`TimeSpan` values instead of sleeps.

## What’s covered (by area)
- **Core runner & plugins:** `AgentEvalBuilderTests`, `MetricRegistryTests`, `RetryPolicyTests` validate plugin lifecycle, registry behavior, transformers, and retry policies.
- **Logging & reporting:** `AgentEvalLoggerTests` and `FailureReportTests` ensure structured logging and failure summaries stay stable.
- **Models & fluent assertions:** `ToolUsageReportTests`, `ToolUsageAssertionsTests`, `ToolCallRecordTests`, `ToolCallTimelineTests`, and `ToolCallAssertionTests` cover tool tracking and assertion chaining. `PerformanceMetricsTests` (which also houses the `PerformanceAssertions` cases) covers latency/cost assertions.
- **Metrics & scoring:** `FaithfulnessMetricTests`, `ToolSelectionMetricTests`, `ToolSuccessMetricTests`, `EmbeddingSimilarityTests`, `ScoreNormalizerTests`, and related files validate scoring logic for RAG and agentic metrics using fake responses.
- **Cost awareness:** `ModelPricingTests` checks price tables and custom pricing hooks.

## File-by-file map

| File | Focus |
| --- | --- |
| `AgentEvalBuilderTests.cs` | Builder API, plugin lifecycle, transformers, and evaluation flow |
| `MetricRegistryTests.cs` | Metric registration and lookup behaviors |
| `RetryPolicyTests.cs` | Retry/backoff logic for evaluations |
| `AgentEvalLoggerTests.cs` | Logging adapter behavior and sinks |
| `FailureReportTests.cs` | Structured failure reports and summaries |
| `ToolUsageReportTests.cs` | Tool tracking data shape and aggregation |
| `ToolUsageAssertionsTests.cs` | Fluent assertions over tool usage |
| `ToolCallRecordTests.cs` | Tool call record modeling |
| `ToolCallTimelineTests.cs` | Tool call timeline ordering and derivations |
| `ToolCallAssertionTests.cs` | Assertion chaining for individual tool calls |
| `PerformanceMetricsTests.cs` | Performance metrics calculations and assertions (TTFT, tokens, cost) |
| `FaithfulnessMetricTests.cs` | RAG faithfulness scoring with fakes |
| `ToolSelectionMetricTests.cs` | Tool selection accuracy scoring |
| `ToolSuccessMetricTests.cs` | Tool success/failure scoring |
| `EmbeddingSimilarityTests.cs` | Embedding-based similarity helpers |
| `ScoreNormalizerTests.cs` | Score normalization utilities |
| `ModelPricingTests.cs` | Model price tables, case-insensitive lookup, and custom pricing |

## Relationship to samples
- Samples in `samples/AgentEval.Samples` show end-to-end flows (agent harness + assertions).
- Tests focus on the same behaviors at unit level for determinism and speed; when adding new features, consider updating both a sample (to show usage) and a unit test (to lock behavior).

## Running the suite
From the repo root:

```bash
dotnet test
```

The suite runs quickly because everything is local and stubbed. If you add new tests, favor fakes/mocks over live services to keep executions fast and costless.
