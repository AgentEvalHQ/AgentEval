# AgentEval Tests

This project contains the automated tests for AgentEval. It lives at `tests/AgentEval.Tests`, targets **.NET 10**, and uses **xUnit**. The tests validate the core library in `src/AgentEval` and mirror the usage patterns demonstrated in `samples/AgentEval.Samples`.

## How the tests are architected for speed and low cost
- **Pure unit focus:** Tests exercise in-memory models, assertions, and helpers—no network calls or real AI providers are hit.
- **Deterministic fakes:** Metrics that normally depend on LLM output use `FakeChatClient` or inline stub classes (e.g., `TestMetric`, `TestPlugin`) to avoid external latency and cost.
- **Custom chat fake over auto-mocking:** `FakeChatClient` is hand-rolled (vs. an auto-mock generator) to keep full control of responses and streaming behavior. We accept the maintenance overhead if the underlying chat client surface changes.
- **Small fixtures:** Each file keeps local helper types close to the tests to minimize setup and cross-file coupling.
- **Fast runners:** Asynchronous surfaces are covered with short-lived tasks; timing-sensitive checks use controlled `DateTimeOffset`/`TimeSpan` values instead of sleeps.

## What’s covered (by area)
- **Core runner & plugins:** `AgentEvalBuilderTests`, `MetricRegistryTests`, `RetryPolicyTests` validate plugin lifecycle, registry behavior, transformers, and retry policies.
- **Logging & reporting:** `AgentEvalLoggerTests` and `FailureReportTests` ensure structured logging and failure summaries stay stable.
- **Models & fluent assertions:** `ToolUsageReportTests`, `ToolUsageAssertionsTests`, `ToolCallRecordTests`, `ToolCallTimelineTests`, and `ToolCallAssertionTests` cover tool tracking and assertion chaining. `PerformanceMetricsTests` (which also houses the `PerformanceAssertions` cases) covers latency/cost assertions. `ResponseAssertionsTests` covers response content assertions.
- **Metrics & scoring:** `FaithfulnessMetricTests`, `ToolSelectionMetricTests`, `ToolSuccessMetricTests`, `EmbeddingSimilarityTests`, `ScoreNormalizerTests`, and related files validate scoring logic for RAG and agentic metrics using fake responses.
- **Cost awareness:** `ModelPricingTests` checks price tables and custom pricing hooks.
- **Multi-turn conversations:** `ConversationalTestCaseTests` and `ConversationRunnerTests` cover the fluent conversation builder, turn handling, and conversation execution.
- **Snapshot testing:** `SnapshotComparerTests` validates JSON comparison, field ignoring, pattern scrubbing, and semantic similarity.
- **CLI components:** `DataLoaderTests` and `ExporterTests` cover dataset loading (JSON, JSONL, CSV, YAML) and result exporting (JSON, JUnit, Markdown).
- **Tracing & replay:** `TraceRecordingAndReplayTests`, `ChatExecutionResultTests`, and `WorkflowTraceTests` cover trace recording, serialization, and deterministic replay for single-agent, multi-turn chat, and workflow executions including streaming support.

## File-by-file map

| File | Focus |
| --- | --- |
| `AgentEvalBuilderTests.cs` | Builder API, plugin lifecycle, transformers, and evaluation flow |
| `AgenticBenchmarkTests.cs` | Agentic benchmark execution and metrics |
| `ConcurrencyTests.cs` | Concurrent evaluation safety |
| `ConversationalTestCaseTests.cs` | Multi-turn conversation builder and metric |
| `ConversationRunnerTests.cs` | Conversation execution against IChatClient |
| `DataLoaderTests.cs` | JSON, JSONL, CSV, YAML dataset loading |
| `EmbeddingSimilarityTests.cs` | Embedding-based similarity helpers |
| `ExporterTests.cs` | JSON, JUnit XML, Markdown export |
| `FailureReportTests.cs` | Structured failure reports and summaries |
| `FaithfulnessMetricTests.cs` | RAG faithfulness scoring with fakes |
| `FakeChatClientTests.cs` | Fake chat client behavior |
| `MAFTestHarnessTests.cs` | MAF integration and evaluation harness |
| `MetricRegistryTests.cs` | Metric registration and lookup behaviors |
| `ModelPricingTests.cs` | Model price tables, case-insensitive lookup, and custom pricing |
| `PerformanceMetricsTests.cs` | Performance metrics calculations and assertions (TTFT, tokens, cost) |
| `ResponseAssertionsTests.cs` | Fluent response content assertions |
| `RetryPolicyTests.cs` | Retry/backoff logic for evaluations |
| `ScoreNormalizerTests.cs` | Score normalization utilities |
| `SerializationTests.cs` | JSON serialization of models |
| `SnapshotComparerTests.cs` | Snapshot comparison with scrubbing and semantic similarity |
| `ToolCallAssertionTests.cs` | Assertion chaining for individual tool calls |
| `ToolCallRecordTests.cs` | Tool call record modeling |
| `ToolCallTimelineTests.cs` | Tool call timeline ordering and derivations |
| `ToolSelectionMetricTests.cs` | Tool selection accuracy scoring |
| `ToolSuccessMetricTests.cs` | Tool success/failure scoring |
| `ToolUsageAssertionsTests.cs` | Fluent assertions over tool usage |
| `ToolUsageReportTests.cs` | Tool tracking data shape and aggregation |
| `Tracing/TraceRecordingAndReplayTests.cs` | Single-agent trace recording and replay, streaming support |
| `Tracing/ChatExecutionResultTests.cs` | Multi-turn chat trace recording |
| `Tracing/WorkflowTraceTests.cs` | Workflow trace recording and replay |

## Running the suite
From the repo root:

```bash
dotnet test
```

The suite runs quickly because everything is local and stubbed. If you add new tests, favor fakes/mocks over live services to keep executions fast and costless.
