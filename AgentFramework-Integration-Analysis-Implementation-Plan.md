# Agent Framework (MAF) ADR-0020 Integration: Analysis & Implementation Plan

## Light Path Implementation Tracking Table

| # | Task | Priority | Description | % Done | Reviewed | Notes |
|:-:|------|:--------:|-------------|:------:|:--------:|-------|
| 0 | Pre-flight checks | P0 | Verify packages, MEAI IEvaluator availability, ScoreNormalizer | 100% | :white_check_mark: | Baseline builds clean, MEAI types available via Core |
| 1 | Package reference | P0 | Add `Microsoft.Extensions.AI.Evaluation.Quality` to MAF .csproj | 100% | :white_check_mark: | Added explicitly for clarity |
| 2 | Directory structure | P0 | Create `src/AgentEval.MAF/Evaluators/` | 100% | :white_check_mark: | 6 source files + test dir |
| 3 | ConversationExtractor | P0 | Extract input/output/tools from ChatMessage[] | 100% | :white_check_mark: | 129 lines, 8 tests |
| 4 | AdditionalContextHelper | P0 | Extract RAG context, ground truth, expected tools from additionalContext | 100% | :white_check_mark: | 106 lines, 3 context subtypes, 10 direct tests |
| 5 | ResultConverter | P0 | MetricResult ↔ MEAI EvaluationResult, score normalization | 100% | :white_check_mark: | 66 lines, 10 tests (incl. Theory) |
| 6 | AgentEvalMetricAdapter | P0 | Single IMetric → MEAI IEvaluator adapter | 100% | :white_check_mark: | 69 lines, 8 tests (incl. ExpectedTools) |
| 7 | AgentEvalEvaluator | P0 | Composite N metrics → single MEAI IEvaluator | 100% | :white_check_mark: | 92 lines, 6 tests |
| 8 | AgentEvalEvaluators | P0 | Static factory: Quality(), RAG(), Safety(), Agentic(), Advanced() | 100% | :white_check_mark: | 125 lines, 5 tests |
| 9 | Unit tests | P0 | Tests for all 6 new classes | 100% | :white_check_mark: | 55 unit tests across 6 test files |
| 10 | Integration smoke test | P0 | End-to-end light path test without live LLM | 100% | :white_check_mark: | 4 integration tests, all passing |
| 11 | Full build + test | P0 | `dotnet build` + `dotnet test` — zero errors, zero regressions | 100% | :white_check_mark: | 0 warnings, 0 errors, 2578/2578 tests pass |
| 12 | Final review | P0 | Review all files, verify coverage, check for gaps | 100% | :white_check_mark: | 587 lines source, ~830 lines tests, doc aligned |

---

## Light Path Implementation Summary

### What Was Built

Six new production classes in `src/AgentEval.MAF/Evaluators/` (587 lines total) that enable any AgentEval `IMetric` to be used as an MEAI `IEvaluator` inside MAF's evaluation orchestration:

| Class | Purpose | Key Responsibility |
|-------|---------|-------------------|
| **`ConversationExtractor`** | Data extraction | Extracts input text (last-turn split, matching ADR-0020 default) and tool usage (`FunctionCallContent`/`FunctionResultContent` pairs) from `IEnumerable<ChatMessage>` + `ChatResponse` |
| **`AdditionalContextHelper`** | Context enrichment | Extracts RAG context, ground truth, and expected tool names from MEAI's `additionalContext` parameter via 3 custom subtypes (`AgentEvalRAGContext`, `AgentEvalGroundTruthContext`, `AgentEvalExpectedToolsContext`) |
| **`ResultConverter`** | Score translation | Converts AgentEval `MetricResult` (0–100 scale) → MEAI `NumericMetric` (1–5 scale) with `EvaluationMetricInterpretation` preserving the original score, rating, and pass/fail status |
| **`AgentEvalMetricAdapter`** | Single-metric adapter | Wraps one AgentEval `IMetric` as an MEAI `IEvaluator` — the core bridge class |
| **`AgentEvalEvaluator`** | Composite adapter | Bundles N `IMetric` instances into a single MEAI `IEvaluator` with graceful per-metric error handling |
| **`AgentEvalEvaluators`** | Public API factory | Static factory with preset bundles (`.Quality()`, `.RAG()`, `.Safety()`, `.Agentic()`, `.Advanced()`) and individual metric accessors — the developer-facing entry point |

Seven test files in `tests/AgentEval.Tests/MAF/Evaluators/` (59 tests, ~830 lines) covering every public method, edge case, and the full end-to-end flow.

### What Changed in Existing Code

Only one file was modified: `src/AgentEval.MAF/AgentEval.MAF.csproj` — added an explicit `<PackageReference Include="Microsoft.Extensions.AI.Evaluation.Quality" />` (the package was already transitively available via `AgentEval.Core`, but the explicit reference makes the dependency clear and intentional).

**Zero changes to existing source files.** All 7 existing MAF files (`MAFAgentAdapter`, `MAFEvaluationHarness`, `MAFWorkflowEventBridge`, `MAFWorkflowAdapter`, `MAFGraphExtractor`, `MAFIdentifiableAgentAdapter`, `WorkflowEvaluationHarness`) are untouched.

### MEAI API Adaptations Discovered During Implementation

The actual MEAI v10.3.0 `IEvaluator` interface differed from what was initially assumed in the plan. All adaptations are documented in Section 17, Step 0.2. Key differences:

- Return type is `ValueTask<EvaluationResult>` (not `Task`)
- Requires `IReadOnlyCollection<string> EvaluationMetricNames` property
- Uses `IEnumerable<ChatMessage>` (not `IList`)
- `ChatResponse` has `.Text` (not `.Message.Text`)
- `EvaluationContext` base class requires constructor args — custom subtypes use constructors, not `required init` properties
- `NumericMetric` uses `Interpretation` property with `EvaluationMetricInterpretation` — no `AddDiagnostic` method

### What Is Now Possible in MAF

Once MAF ships the `AgentEvaluationExtensions` from ADR-0020 (the `agent.EvaluateAsync()` orchestration layer), developers will be able to use AgentEval's 19 metrics directly inside MAF's evaluation pipeline with zero friction.

#### Example 1: Quality Evaluation — One Line

```csharp
using AgentEval.MAF.Evaluators;

// Create a MAF agent as usual
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "TravelAgent",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a helpful travel assistant.",
        Tools = [AIFunctionFactory.Create(SearchFlights), AIFunctionFactory.Create(BookHotel)]
    }
});

// Evaluate with AgentEval quality metrics — one line
AgentEvaluationResults results = await agent.EvaluateAsync(
    queries: ["What's the weather in Seattle?", "Plan a weekend trip to Portland"],
    evaluator: AgentEvalEvaluators.Quality(judgeClient));

results.AssertAllPassed();  // throws if any metric fails

// Each item in results contains 4 AgentEval metrics:
// - llm_faithfulness (1-5 MEAI scale, with original 0-100 in interpretation)
// - llm_relevance
// - llm_coherence
// - llm_fluency
```

#### Example 2: Mixed Evaluators — AgentEval + MEAI + Foundry Together

```csharp
using AgentEval.MAF.Evaluators;
using Microsoft.Extensions.AI.Evaluation.Quality;

// Mix evaluators from three different providers in one call
IReadOnlyList<AgentEvaluationResults> results = await agent.EvaluateAsync(
    queries: ["Search for flights to Paris", "What restaurants are near the Eiffel Tower?"],
    evaluators: [
        // MEAI built-in (ships with Microsoft.Extensions.AI.Evaluation)
        new RelevanceEvaluator(),

        // AgentEval — safety bundle (toxicity, bias, misinformation)
        AgentEvalEvaluators.Safety(judgeClient),

        // AgentEval — agentic metrics with expected tools
        AgentEvalEvaluators.Agentic(expectedTools: ["SearchFlights", "SearchRestaurants"]),

        // Azure AI Foundry (cloud-based evaluation with dashboard)
        new FoundryEvals(projectClient, "gpt-4o"),
    ]);

// One result per evaluator — check each
foreach (var r in results)
    r.AssertAllPassed();
```

#### Example 3: RAG Evaluation with Ground Truth

```csharp
using AgentEval.MAF.Evaluators;

// Pass ground truth and RAG context via additionalContext
AgentEvaluationResults results = await agent.EvaluateAsync(
    queries: ["What is the capital of France?"],
    evaluator: AgentEvalEvaluators.RAG(judgeClient),
    expectedOutput: ["Paris"],
    additionalContext: [
        new AgentEvalRAGContext("France is a country in Western Europe. Its capital is Paris."),
        new AgentEvalGroundTruthContext("Paris"),
    ]);

results.AssertAllPassed();

// RAG bundle runs 5 metrics:
// - llm_faithfulness: is the answer grounded in the provided context?
// - llm_relevance: does the answer address the question?
// - llm_context_precision: is the context relevant to the question?
// - llm_context_recall: does the context contain the answer?
// - llm_answer_correctness: does the answer match the ground truth?
```

#### What's Still in the Deep Path Only

For capabilities that require live execution data (not just conversation transcripts), the existing deep path via `MAFEvaluationHarness` and `WorkflowEvaluationHarness` remains the path:

```csharp
// Deep path — streaming evaluation with TTFT, tool timelines, cost tracking
var harness = new MAFEvaluationHarness(evaluatorClient);
var result = await harness.RunEvaluationStreamingAsync(
    new MAFAgentAdapter(agent), testCase,
    new StreamingOptions { OnFirstToken = ttft => Log($"TTFT: {ttft.TotalMs}ms") });

// result.Performance.TimeToFirstToken — not available through IEvaluator
// result.Timeline.ToAsciiDiagram() — not available through IEvaluator
// result.Performance.EstimatedCost — not available through IEvaluator
```

Both paths share the same metric implementations. The light path is for quick, integrated evaluation. The deep path is for comprehensive analysis and debugging.

---

> **Date:** 2026-03-25 (updated)
> **Context:** PR [microsoft/agent-framework#4731](https://github.com/microsoft/agent-framework/pull/4731) — "ADR-0020: Foundry Evals Integration" (merged 2026-03-20)
> **Author:** bentho (Ben Thompson)
> **AgentEval contact:** joslat

---

## 1. Executive Summary

MAF's ADR-0020 introduces a provider-agnostic evaluation protocol with Azure AI Foundry as the first cloud provider. The .NET side reuses MEAI's `IEvaluator` as the core contract and adds a **thin orchestration layer** (`AgentEvaluationExtensions`) that runs agents, calls `IEvaluator` per item, and aggregates results.

**The critical insight:** AgentEval already has ~2,500 lines of deep MAF integration (`AgentEval.MAF`) that provides capabilities MAF's thin orchestration **fundamentally cannot replicate**: streaming with TTFT, tool call timelines, ChatProtocol/TurnToken handling, per-executor workflow graph analysis, session management, stochastic evaluation, and model comparison. Simply implementing `IEvaluator` and plugging into MAF's orchestration would **abandon our deepest differentiator**.

**The architecture must be dual-path:**
1. **Light path (MAF-native):** AgentEval metrics as MEAI `IEvaluator` — for MAF users who want advanced metrics in their simple pipeline
2. **Deep path (AgentEval-native):** Full MAF integration via existing harnesses — for users who need comprehensive evaluation with rich observability
3. **Shared result layer:** Bidirectional conversion so results from either path are interoperable

---

## 2. What ADR-0020 Actually Builds

### 2.1 Core Abstractions (.NET)

| Concept | Type | Package |
|---------|------|---------|
| Evaluator contract | **MEAI's `IEvaluator`** (no new interface) | `Microsoft.Extensions.AI.Evaluation` |
| Orchestration | `AgentEvaluationExtensions` (extension methods on `AIAgent`, `Run`) | `Microsoft.Agents.AI` |
| Aggregate result | `AgentEvaluationResults` (passed/failed/total + sub-results for workflows) | `Microsoft.Agents.AI` |
| Data carrier | `EvalItem` (conversation + tools + expected output + split strategy) | `Microsoft.Agents.AI` |
| Local checks | `LocalEvaluator` + `FunctionEvaluator.Create()` | `Microsoft.Agents.AI` |
| Cloud provider | `FoundryEvals` (batches to Azure AI Foundry) | `Microsoft.Agents.AI.AzureAI` |

### 2.2 Key Design Decisions

1. **No new evaluator interface in .NET** — reuses MEAI's `IEvaluator` (`EvaluateAsync(messages, chatResponse, chatConfig)`)
2. **Conversation is the single source of truth** — `EvalItem.conversation` contains full `ChatMessage` history; `query`/`response` are derived via split strategies
3. **Multiple evaluators compose naturally** — pass `IEnumerable<IEvaluator>`, get one `AgentEvaluationResults` per evaluator
4. **Conversation split strategies** — last-turn (default), full-conversation, per-turn, or custom `IConversationSplitter`
5. **Workflow evaluation** — `run.EvaluateAsync()` extracts per-agent data from workflow events, provides `SubResults` breakdown

### 2.3 The MAF Orchestration (What It Does)

```
agent.EvaluateAsync(queries, evaluator)
  │
  ├── For each query:
  │     1. Runs agent  →  gets ChatResponse
  │     2. Builds (messages, response) pair
  │     3. Calls evaluator.EvaluateAsync(messages, response, chatConfig)
  │     4. Collects EvaluationResult
  │
  └── Aggregates into AgentEvaluationResults { Passed, Failed, Total, AssertAllPassed() }
```

### 2.4 What MAF's Orchestration Does NOT Do

This is the critical gap that drives our architecture:

| Capability | MAF ADR-0020 | AgentEval.MAF (existing) |
|------------|:------------:|:------------------------:|
| Run agent, collect response | Yes | Yes |
| Call evaluator per item | Yes | Yes |
| **Streaming with TTFT tracking** | No | `MAFEvaluationHarness` + `MAFAgentAdapter` |
| **Tool call timeline (start/end/duration per tool)** | No | `ToolCallTimeline` + streaming chunk tracking |
| **Session management (multi-turn/memory)** | No | `ISessionResettableAgent` + `AgentSession` |
| **ChatProtocol detection + TurnToken** | No | `MAFWorkflowEventBridge` |
| **Per-executor workflow breakdown with tool calls** | Partial (LINQ walk) | `MAFWorkflowAdapter` + `ExecutorStep` |
| **Workflow graph extraction** | No | `MAFGraphExtractor` + `WorkflowGraphSnapshot` |
| **Edge traversal tracking** | No | `EdgeTraversedEvent` + `EdgeExecution` |
| **Parallel branch tracking** | No | `ParallelBranch` records |
| **Routing decision capture** | No | `RoutingDecision` records |
| **Workflow assertion DSL** | No | `WorkflowEvaluationHarness` (7 built-in assertions) |
| **Stochastic evaluation (N runs + P90)** | `num_repetitions` only | `StochasticRunner` with full statistics |
| **Model comparison with rankings** | No | `ModelComparer` + `IModelIdentifiable` |
| **Calibrated multi-judge consensus** | No | `CalibratedJudge` |
| **Cost estimation per evaluation** | No | `ModelPricing` |
| **Failure reports with root cause analysis** | No | `FailureReport` with suggestions |
| **Export to JUnit/SARIF/PDF** | Portal only | Full export pipeline |

**This is ~2,500 lines of production code that represents AgentEval's deepest MAF differentiation.** We cannot afford to flatten this into a simple IEvaluator adapter.

---

## 3. Existing AgentEval.MAF Inventory (What We Protect)

### 3.1 Agent Adapters

**`MAFAgentAdapter`** (130 lines) — Wraps `AIAgent` as `IStreamableAgent` + `ISessionResettableAgent`:
- Session management: creates/resets `AgentSession` per invocation or explicitly
- Streaming: extracts `TextContent`, `FunctionCallContent`, `FunctionResultContent`, `UsageContent` from response chunks
- Token usage: captures from `AgentResponse.Usage` (InputTokenCount, OutputTokenCount)

**`MAFIdentifiableAgentAdapter`** (147 lines) — Adds `IModelIdentifiable` for model comparison:
- `ModelId` (e.g., "gpt-4o-2024-08-06") and `ModelDisplayName` (e.g., "GPT-4o")
- Used by `ModelComparer` for multi-model evaluation

### 3.2 Workflow Integration (The Crown Jewel)

**`MAFWorkflowEventBridge`** (314 lines) — Translates MAF's class-based events to AgentEval records:
- **ChatProtocol detection**: `workflow.DescribeProtocolAsync()` → `ChatProtocolExtensions.IsChatProtocol()`
- **TurnToken handling**: Manually sends `TurnToken(emitEvents: true)` for ChatProtocol-based executors (strings are silently dropped without this)
- **Event translation**: `ExecutorInvokedEvent` → edge tracking, `AgentResponseUpdateEvent` → tool call buffering + text accumulation, `ExecutorCompletedEvent` → output flush, `WorkflowOutputEvent` → workflow completion
- **Tool call buffering**: `FunctionCallContent` buffered until `FunctionResultContent` arrives → complete `ExecutorToolCallEvent` with duration

**`MAFWorkflowAdapter`** (649 lines) — Captures detailed per-executor execution data:
- `ExecutorStep` records: executorId, output, startOffset, duration, stepIndex, toolCalls, incoming/outgoing edges, parallelBranchId
- `EdgeExecution` records: source/target, edgeType, conditionResult, matchedSwitchLabel, routingReason
- `WorkflowGraphSnapshot`: nodes, edges, traversedEdges, parallelBranches, entry/exit node IDs
- `RoutingDecision` records: deciderExecutorId, possibleEdgeIds, selectedEdgeId, evaluatedValue
- Factory methods: `FromMAFWorkflow()`, `FromSteps()`, `WithGraph()`, `FromConditionalSteps()`

**`MAFGraphExtractor`** (173 lines) — Static graph extraction from `Workflow` objects:
- Uses `Workflow.ReflectEdges()` (public API)
- Translates `EdgeKind` (Direct, FanOut, FanIn) → `EdgeType` (Sequential, Conditional, ParallelFanOut, ParallelFanIn)
- Full MAF ID → clean name mapping (`"Planner_abc123"` → `"Planner"`)
- Exit node detection (executors with no outgoing edges)

### 3.3 Evaluation Harnesses

**`MAFEvaluationHarness`** (743 lines) — General agent evaluation:
- Single test, streaming test, batch test, suite execution
- Performance metrics: TTFT, token counts (actual or estimated), cost estimation
- Tool call timeline extraction from `RawMessages`
- AI-powered evaluation via `IEvaluator` (AgentEval's)
- `FailureReport` with categorized reasons, severity levels, and suggestions

**`WorkflowEvaluationHarness`** (435 lines) — Workflow-specific testing:
- 7 built-in assertions: executor order (strict/loose), expected executors, output contains, max duration, no errors, expected tools, per-executor tools
- `WorkflowTestResult` with `AssertionResults` list
- Suite execution with `ContinueOnFailure` option

### 3.4 Key MAF Types Consumed

From `Microsoft.Agents.AI`: `AIAgent`, `AgentSession`, `ChatClientAgent`, `ChatClientAgentOptions`, `AIFunctionFactory`

From `Microsoft.Agents.AI.Workflows`: `Workflow`, `WorkflowBuilder`, `InProcessExecution`, `StreamingRun`, `TurnToken`, `ChatProtocolExtensions`, `EdgeKind`, `ExecutorInvokedEvent`, `ExecutorCompletedEvent`, `ExecutorFailedEvent`, `AgentResponseUpdateEvent`, `WorkflowOutputEvent`, `WorkflowErrorEvent`, `SuperStepStartedEvent`

From `Microsoft.Extensions.AI`: `ChatMessage`, `ChatRole`, `FunctionCallContent`, `FunctionResultContent`, `TextContent`, `UsageContent`, `ChatOptions`

---

## 4. Revised Analysis: Why "Just Implement IEvaluator" Is Insufficient

### 4.1 The Fundamental Problem

MAF's `agent.EvaluateAsync()` runs the agent, captures the `ChatResponse`, and passes `(messages, response)` to `IEvaluator`. That's it. By the time the evaluator receives the data, the execution is over and the rich observability data is lost:

- No streaming chunks → no TTFT
- No per-tool start/end timestamps → no tool timeline
- No MAF event stream → no workflow graph traversal data
- No session management → no multi-turn memory evaluation
- No stochastic repetition → no statistical analysis

**An `IEvaluator` implementation receives a conversation transcript. AgentEval's deep path captures the entire execution lifecycle.**

### 4.2 Analogy

Think of it as the difference between:
- **Post-mortem evaluation** (IEvaluator): "Here's a conversation log. Score it." — This is what MAF's orchestration provides.
- **Live evaluation** (AgentEval harness): "I watched every streaming chunk, every tool call start/end, every workflow edge traversal, every executor transition in real-time. Here's a comprehensive analysis." — This is what AgentEval.MAF provides.

Both are valuable. Different users need different depths. We must support both.

### 4.3 What Your Response to Ben Got Right

> "AgentEval extends this with more advanced scenarios (tool-level evals, RAG grounding, cost, policies, red teaming, upcoming memory evaluation, etc.)"

This is correct — but understates it. The real extension isn't just "more metrics." It's **a fundamentally richer evaluation execution model** that captures data MAF's thin orchestration layer doesn't.

### 4.4 What Needs Refinement

> "Implement `AgentEvalEvaluator : IEvaluator`"

This is necessary but not sufficient. On its own it flattens AgentEval into a metric scorer and abandons the deep integration. The correct framing:

> AgentEval provides two integration surfaces: (1) Advanced metrics as MEAI `IEvaluator` — plug into any MAF evaluation pipeline, and (2) A comprehensive evaluation engine for MAF agents with deep observability that MAF's native orchestration cannot provide.

---

## 5. Recommended Dual-Path Architecture

### 5.1 Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         DEVELOPER CODE                                  │
│                                                                         │
│  ┌─── Light Path (MAF-native) ────────────────────────────────────────┐ │
│  │                                                                     │ │
│  │  // Quick quality check — uses MAF orchestration                    │ │
│  │  agent.EvaluateAsync(queries, AgentEvalEvaluators.Quality(client)); │ │
│  │                                                                     │ │
│  │  // Mix with Foundry — MAF handles everything                       │ │
│  │  agent.EvaluateAsync(queries, [agentEvalMetrics, foundryEvals]);    │ │
│  │                                                                     │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│                                                                         │
│  ┌─── Deep Path (AgentEval-native) ───────────────────────────────────┐ │
│  │                                                                     │ │
│  │  // Full evaluation with streaming, tool timelines, cost tracking   │ │
│  │  var harness = new MAFEvaluationHarness(evaluatorClient);           │ │
│  │  var result = await harness.RunEvaluationStreamingAsync(            │ │
│  │      agent.AsEvaluableAgent(), testCase, streamingOpts);            │ │
│  │                                                                     │ │
│  │  // Workflow evaluation with graph analysis & assertions            │ │
│  │  var wfHarness = new WorkflowEvaluationHarness(evaluator, logger);  │ │
│  │  var wfResult = await wfHarness.RunWorkflowTestAsync(               │ │
│  │      MAFWorkflowAdapter.FromMAFWorkflow(workflow, ...),             │ │
│  │      testCase);                                                     │ │
│  │                                                                     │ │
│  │  // Stochastic evaluation — 10 runs with statistics                 │ │
│  │  var stochastic = await stochasticRunner.RunStochasticTestAsync(    │ │
│  │      agent.AsEvaluableAgent(), testCase,                            │ │
│  │      new StochasticOptions { Repetitions = 10 });                   │ │
│  │                                                                     │ │
│  │  // Model comparison                                                │ │
│  │  var comparison = await modelComparer.CompareModelsAsync(           │ │
│  │      [gpt4oFactory, gpt4MiniFactory], testCase);                    │ │
│  │                                                                     │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│                                                                         │
│  ┌─── Shared Result Layer ────────────────────────────────────────────┐ │
│  │                                                                     │ │
│  │  // Deep path results → MAF format (for .AssertAllPassed())         │ │
│  │  var mafResults = result.ToAgentEvaluationResults();                 │ │
│  │                                                                     │ │
│  │  // Either path → AgentEval export pipeline                         │ │
│  │  await exporter.ExportAsync(result, "junit", stream);               │ │
│  │  await exporter.ExportAsync(result, "sarif", stream);               │ │
│  │  await exporter.ExportAsync(result, "pdf", stream);                 │ │
│  │                                                                     │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
```

### 5.2 What Each Path Provides

| Aspect | Light Path (MAF-native) | Deep Path (AgentEval-native) |
|--------|------------------------|------------------------------|
| **Entry point** | `agent.EvaluateAsync()` | `MAFEvaluationHarness` / `WorkflowEvaluationHarness` |
| **Orchestration** | MAF's `AgentEvaluationExtensions` | AgentEval's harnesses |
| **Agent wrapping** | None (uses `AIAgent` directly) | `MAFAgentAdapter` / `MAFWorkflowAdapter` |
| **Streaming data** | No | Yes (TTFT, per-chunk tool tracking) |
| **Tool timeline** | No | Yes (`ToolCallTimeline` with ASCII diagrams) |
| **Session management** | No | Yes (`ISessionResettableAgent`) |
| **ChatProtocol/TurnToken** | No (MAF handles internally) | Yes (explicit in `MAFWorkflowEventBridge`) |
| **Workflow graph** | No | Yes (`WorkflowGraphSnapshot`, edge traversal) |
| **Workflow assertions** | No | Yes (7 built-in + custom) |
| **Per-executor tools** | Partial | Full (per `ExecutorStep`) |
| **Stochastic runs** | `num_repetitions` (basic) | `StochasticRunner` (P90, std dev, success rate) |
| **Model comparison** | No | Yes (`ModelComparer` + rankings) |
| **Calibrated judges** | No | Yes (`CalibratedJudge`) |
| **Cost estimation** | No | Yes (`ModelPricing`) |
| **Failure diagnostics** | Basic pass/fail | `FailureReport` with reasons + suggestions |
| **Result format** | `AgentEvaluationResults` | `TestResult` / `WorkflowTestResult` (convertible) |
| **Export** | Foundry portal | JUnit, SARIF, Markdown, JSON, PDF |
| **Best for** | Quick checks, CI gates | Deep evaluation, debugging, perf analysis |

### 5.3 The Bridge Between Paths

Both paths share the same metrics (`IMetric` implementations) and the same evaluation intelligence. The difference is what **data** the metrics receive:

- **Light path:** Metrics receive conversation text only (extracted from `ChatMessage[]`)
- **Deep path:** Metrics receive `EvaluationContext` with tool usage, performance metrics, timeline, ground truth, context — the full picture

For metrics that only need text (quality metrics), both paths produce equivalent results. For metrics that need richer data (tool success, cost, latency), the deep path is required.

---

## 6. Detailed Implementation Plan

### Phase 1: AgentEval Metrics as MEAI IEvaluator (Light Path)

**Goal:** Any MAF user can use AgentEval metrics via `agent.EvaluateAsync()`.

#### 6.1.1 `AgentEvalMetricAdapter : MEAI.IEvaluator`

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalMetricAdapter.cs`

Wraps a single `AgentEval.Core.IMetric` as `Microsoft.Extensions.AI.Evaluation.IEvaluator`. Extracts input/output from `ChatMessage[]` + `ChatResponse`, builds an `EvaluationContext`, runs the metric, and converts `MetricResult` → MEAI `EvaluationResult`.

Score mapping: `MetricResult.Score` (0-100) → `NumericMetric` (1-5 scale for MEAI compat, raw 0-100 in interpretation).

#### 6.1.2 `AgentEvalEvaluator : MEAI.IEvaluator`

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalEvaluator.cs`

Composite evaluator bundling multiple AgentEval metrics. Returns one `EvaluationResult` containing all metric scores as named metrics.

#### 6.1.3 `AgentEvalEvaluators` (Static Factory)

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalEvaluators.cs`

```csharp
public static class AgentEvalEvaluators
{
    // Preset bundles
    public static AgentEvalEvaluator Quality(IChatClient judgeClient);
    public static AgentEvalEvaluator RAG(IChatClient judgeClient);
    public static AgentEvalEvaluator Agentic();
    public static AgentEvalEvaluator Safety(IChatClient judgeClient);
    public static AgentEvalEvaluator Memory(IChatClient judgeClient);
    public static AgentEvalEvaluator Advanced(IChatClient judgeClient);  // all metrics

    // Individual metrics (for mixing with MEAI/Foundry evaluators)
    public static IEvaluator Faithfulness(IChatClient judgeClient);
    public static IEvaluator ToolSuccess();
    // ... one per metric

    // Custom
    public static AgentEvalEvaluator Custom(params IMetric[] metrics);
}
```

#### 6.1.4 Supporting Classes

- `ConversationExtractor` — Extracts input/output from `ChatMessage[]` using split strategies (last-turn, full, per-turn)
- `EvalItemToContextConverter` — Converts MEAI evaluation data → AgentEval `EvaluationContext`
- `ResultConverter` — Bidirectional conversion: `MetricResult` ↔ `EvaluationResult`

---

### Phase 2: Deep Path Enhancement & Result Interop

**Goal:** Existing deep integration produces results consumable by both AgentEval and MAF ecosystems.

#### 6.2.1 `AgentEvalResultsAdapter`

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalResultsAdapter.cs`

```csharp
public static class AgentEvalResultsAdapter
{
    // Deep path → MAF format
    public static AgentEvaluationResults ToAgentEvaluationResults(TestResult result);
    public static AgentEvaluationResults ToAgentEvaluationResults(TestSummary summary);
    public static AgentEvaluationResults ToAgentEvaluationResults(WorkflowTestResult result);
    public static AgentEvaluationResults ToAgentEvaluationResults(StochasticResult result);

    // MAF format → Deep path (for feeding MAF results into AgentEval export)
    public static TestSummary ToTestSummary(AgentEvaluationResults results);
}
```

This allows:
```csharp
// Deep evaluation → MAF assertion pattern
var result = await harness.RunEvaluationStreamingAsync(agent, testCase);
result.ToAgentEvaluationResults().AssertAllPassed();

// Deep evaluation → SARIF export
await sarifExporter.ExportAsync(result, stream);
```

#### 6.2.2 `WorkflowTestResult` → `AgentEvaluationResults` with SubResults

When converting workflow results, map per-executor steps to `SubResults`:
```csharp
// WorkflowTestResult with steps [Planner, Researcher, Writer]
// → AgentEvaluationResults with SubResults:
//     "Planner": { Passed: 1, Total: 1 }
//     "Researcher": { Passed: 1, Total: 1 }
//     "Writer": { Passed: 1, Total: 1 }
```

This aligns with ADR-0020's `sub_results` pattern for workflow evaluation.

---

### Phase 3: Deep Path Extensions for ADR-0020 Concepts

**Goal:** Enhance the deep path with ADR-0020 concepts that enrich evaluation.

#### 6.3.1 Conversation Split Strategies in Deep Path

**File:** `src/AgentEval.MAF/Evaluators/ConversationSplitters.cs`

Add split strategy support to `MAFEvaluationHarness` for multi-turn evaluation:
- Last-turn: evaluate only the final exchange (current default behavior)
- Full: evaluate entire conversation trajectory
- Per-turn: evaluate each turn independently → returns multiple `TestResult`s

Aligns with ADR-0020's `ConversationSplit.LAST_TURN / FULL / PER_TURN`.

#### 6.3.2 Expected Tool Call Assertions

**File:** `src/AgentEval.MAF/Evaluators/ToolCallAssertions.cs`

Add ADR-0020-compatible expected tool call validation to the deep path:
```csharp
var testCase = new TestCase {
    ExpectedToolCalls = [
        new ExpectedToolCall("SearchFlights", new { destination = "Paris" }),
        new ExpectedToolCall("BookHotel")
    ]
};
```

Leverages existing `ToolUsageExtractor` + `WorkflowEvaluationHarness` assertions.

#### 6.3.3 Ground Truth / Expected Output in EvaluationContext

Ensure `EvaluationContext` carries `GroundTruth` through both paths:
- Light path: via MEAI's `additionalContext` parameter (custom `GroundTruthContext` subtype)
- Deep path: via `TestCase.ExpectedOutputContains` (existing) + new `TestCase.GroundTruth` property

---

### Phase 4: Advanced Engine Extension Methods on AIAgent

**Goal:** Expose AgentEval's advanced capabilities (stochastic, comparison, red team) as convenient extension methods on MAF types.

#### 6.4.1 `MAFAgentExtensions`

**File:** `src/AgentEval.MAF/MAF/MAFAgentExtensions.cs`

```csharp
public static class MAFAgentExtensions
{
    // Stochastic evaluation with deep path (streaming + tool timelines + statistics)
    public static Task<StochasticResult> EvaluateStochasticAsync(
        this AIAgent agent,
        TestCase testCase,
        StochasticOptions? options = null,
        CancellationToken ct = default);

    // Model comparison via deep path
    public static Task<ModelComparisonResult> CompareAsync(
        this IEnumerable<(AIAgent Agent, string ModelId, string DisplayName)> agents,
        TestCase testCase,
        ModelComparisonOptions? options = null,
        CancellationToken ct = default);

    // Red team via deep path
    public static Task<RedTeamResult> RedTeamAsync(
        this AIAgent agent,
        RedTeamOptions? options = null,
        CancellationToken ct = default);

    // Memory evaluation via deep path
    public static Task<MemoryBenchmarkResult> EvaluateMemoryAsync(
        this AIAgent agent,
        MemoryBenchmarkOptions? options = null,
        CancellationToken ct = default);
}
```

These methods use the deep path internally (wrapping `AIAgent` with `MAFAgentAdapter`) while providing a MAF-native API surface.

---

### Phase 5: DI Integration

**File:** `src/AgentEval.MAF/DependencyInjection/MAFEvaluatorServiceExtensions.cs`

```csharp
public static class MAFEvaluatorServiceExtensions
{
    // Register AgentEval metrics as MEAI IEvaluator (light path)
    public static IServiceCollection AddAgentEvalAsMAFEvaluator(
        this IServiceCollection services,
        Action<AgentEvalMAFOptions>? configure = null);

    // Register full AgentEval pipeline for MAF (deep path)
    public static IServiceCollection AddAgentEvalForMAF(
        this IServiceCollection services,
        Action<AgentEvalMAFOptions>? configure = null);
}
```

---

### Phase 6: MEAI Additional Context Protocol

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalContexts.cs`

Custom MEAI `EvaluationContext` subtypes for passing rich data through the light path:

```csharp
public class GroundTruthContext : Microsoft.Extensions.AI.Evaluation.EvaluationContext
{
    public string GroundTruth { get; init; }
}

public class ToolExpectationContext : Microsoft.Extensions.AI.Evaluation.EvaluationContext
{
    public IReadOnlyList<ExpectedToolCall> ExpectedToolCalls { get; init; }
}

public class RAGContext : Microsoft.Extensions.AI.Evaluation.EvaluationContext
{
    public string RetrievedContext { get; init; }
}
```

---

## 7. File Manifest

### New Files

| File | Purpose | Phase |
|------|---------|-------|
| `src/AgentEval.MAF/Evaluators/AgentEvalMetricAdapter.cs` | Single-metric MEAI IEvaluator adapter | 1 |
| `src/AgentEval.MAF/Evaluators/AgentEvalEvaluator.cs` | Composite multi-metric MEAI IEvaluator | 1 |
| `src/AgentEval.MAF/Evaluators/AgentEvalEvaluators.cs` | Static factory with preset bundles | 1 |
| `src/AgentEval.MAF/Evaluators/ConversationExtractor.cs` | ChatMessage → input/output extraction | 1 |
| `src/AgentEval.MAF/Evaluators/EvalItemToContextConverter.cs` | MEAI data → AgentEval EvaluationContext | 1 |
| `src/AgentEval.MAF/Evaluators/ResultConverter.cs` | Bidirectional result conversion | 1 |
| `src/AgentEval.MAF/Evaluators/AgentEvalResultsAdapter.cs` | TestResult ↔ AgentEvaluationResults | 2 |
| `src/AgentEval.MAF/Evaluators/ConversationSplitters.cs` | Split strategy implementations | 3 |
| `src/AgentEval.MAF/Evaluators/ToolCallAssertions.cs` | Expected tool call validation | 3 |
| `src/AgentEval.MAF/Evaluators/AgentEvalContexts.cs` | Custom MEAI EvaluationContext subtypes | 6 |
| `src/AgentEval.MAF/MAF/MAFAgentExtensions.cs` | Extension methods for advanced capabilities | 4 |
| `src/AgentEval.MAF/DependencyInjection/MAFEvaluatorServiceExtensions.cs` | DI registration | 5 |
| `tests/AgentEval.MAF.Tests/Evaluators/` | Unit tests for all new classes | All |
| `samples/AgentEval.MAFIntegration/` | End-to-end sample showing both paths | All |

### Modified Files

| File | Change |
|------|--------|
| `src/AgentEval.MAF/AgentEval.MAF.csproj` | Add `Microsoft.Extensions.AI.Evaluation` package reference |
| `src/AgentEval.Core/Adapters/MicrosoftEvaluatorAdapter.cs` | Extract shared score conversion to `ScoreNormalizer` |

### Untouched (Preserved As-Is)

| File | Why |
|------|-----|
| `MAFAgentAdapter.cs` | Core deep path — session management, streaming |
| `MAFIdentifiableAgentAdapter.cs` | Core deep path — model comparison |
| `MAFWorkflowEventBridge.cs` | Core deep path — ChatProtocol/TurnToken/event translation |
| `MAFWorkflowAdapter.cs` | Core deep path — per-executor/graph/routing capture |
| `MAFGraphExtractor.cs` | Core deep path — static graph extraction |
| `MAFEvaluationHarness.cs` | Core deep path — general evaluation with rich metrics |
| `WorkflowEvaluationHarness.cs` | Core deep path — workflow assertions |

---

## 8. Usage Examples: Both Paths

### 8.1 Light Path — MAF User Wants Better Metrics

```csharp
// Just swap in AgentEval metrics where you'd use MEAI evaluators
var results = await agent.EvaluateAsync(
    queries: ["What's the weather in Seattle?", "Plan a trip to Portland"],
    evaluator: AgentEvalEvaluators.Quality(judgeClient));

results.AssertAllPassed();

// Mix AgentEval + MEAI + Foundry
var results = await agent.EvaluateAsync(
    queries,
    evaluators: [
        new RelevanceEvaluator(),                      // MEAI native
        AgentEvalEvaluators.Agentic(),                 // AgentEval tools metrics
        AgentEvalEvaluators.Faithfulness(judgeClient), // AgentEval individual
        new FoundryEvals(projectClient, "gpt-4o"),     // Foundry cloud
    ]);
```

### 8.2 Deep Path — Full Evaluation with Observability

```csharp
// Agent evaluation with streaming, tool timelines, cost tracking
var harness = new MAFEvaluationHarness(evaluatorClient);
var agent = new MAFAgentAdapter(myAgent);

var result = await harness.RunEvaluationStreamingAsync(agent, testCase,
    new StreamingOptions
    {
        OnFirstToken = ttft => Console.WriteLine($"TTFT: {ttft.TotalMilliseconds}ms"),
        OnToolStart = tool => Console.WriteLine($"Tool: {tool.Name}"),
    });

// Rich performance data
Console.WriteLine($"Tokens: {result.Performance.TotalTokens}");
Console.WriteLine($"Cost: ${result.Performance.EstimatedCost:F4}");
Console.WriteLine($"Timeline:\n{result.Timeline.ToAsciiDiagram()}");

// Convert to MAF format if needed
result.ToAgentEvaluationResults().AssertAllPassed();

// Export to CI formats
await new JUnitExporter().ExportAsync(result, stream);
await new SarifExporter().ExportAsync(result, stream);
```

### 8.3 Deep Path — Workflow with Graph Analysis

```csharp
var adapter = MAFWorkflowAdapter.FromMAFWorkflow(
    workflow, "TripPlanner",
    executorIds: ["Planner", "Researcher", "Writer"]);

var wfHarness = new WorkflowEvaluationHarness(evaluator, logger);
var result = await wfHarness.RunWorkflowTestAsync(adapter, new WorkflowTestCase
{
    Name = "Trip Planning E2E",
    Input = "Plan a 3-day trip to Paris",
    ExpectedExecutors = ["Planner", "Researcher", "Writer"],
    StrictExecutorOrder = true,
    ExpectedTools = ["SearchFlights", "SearchHotels"],
    PerExecutorExpectedTools = new() {
        ["Researcher"] = ["SearchFlights", "SearchHotels"]
    },
    MaxDuration = TimeSpan.FromSeconds(30)
});

// Graph structure available
var graph = result.ExecutionResult.Graph;
Console.WriteLine($"Nodes: {graph.Nodes.Count}, Edges: {graph.Edges.Count}");
Console.WriteLine($"Traversed: {graph.TraversedEdges.Count} edges");
```

### 8.4 Advanced — Stochastic + Model Comparison

```csharp
// Stochastic: 10 runs with statistical analysis
var stochastic = await agent.EvaluateStochasticAsync(
    testCase,
    new StochasticOptions { Repetitions = 10 });
Console.WriteLine($"Mean: {stochastic.MeanScore:F1}, P90: {stochastic.P90Score:F1}");

// Compare models
var comparison = await new[] {
    (gpt4o, "gpt-4o", "GPT-4o"),
    (gpt4Mini, "gpt-4o-mini", "GPT-4o Mini")
}.CompareAsync(testCase);
Console.WriteLine($"Winner: {comparison.Winner.ModelDisplayName}");
```

---

## 9. Revised Response Framing for Ben

### Before (Your Original)

> "Implement `AgentEvalEvaluator : IEvaluator` → so AgentEval can act as a more advanced evaluation engine behind the interface"

### After (Refined)

> **We're building two integration surfaces that share the same evaluation intelligence:**
>
> 1. **AgentEval metrics as MEAI `IEvaluator`** — so `agent.EvaluateAsync(queries, AgentEvalEvaluators.Quality(client))` just works. Your users get 30+ advanced metrics (faithfulness, tool success, RAG grounding, safety) with zero changes to their evaluation code.
>
> 2. **A comprehensive evaluation engine for MAF agents** — for users who need more than per-item scoring. This includes streaming evaluation with TTFT tracking, per-executor workflow analysis with graph structure and edge traversal, stochastic evaluation with statistical analysis, model comparison, calibrated multi-judge consensus, red teaming, memory evaluation, and rich export (JUnit, SARIF, PDF).
>
> Both paths produce interoperable results — deep path results convert to `AgentEvaluationResults` for `.AssertAllPassed()`, and MAF results feed into AgentEval's export pipeline.
>
> We're not duplicating orchestration. MAF's `AgentEvaluationExtensions` runs agents and calls evaluators. AgentEval provides the evaluation intelligence behind `IEvaluator` for the light path, and provides a richer execution model for users who need it.

---

## 10. Implementation Priority & Sequencing

| Phase | Priority | Effort | Dependency |
|-------|----------|--------|------------|
| 1. Core MEAI Adapter (Light Path) | **P0** | 3-4 days | MAF ADR-0020 merged (done) |
| 2. Result Interop (Bridge) | **P0** | 2-3 days | Phase 1 |
| 3. Deep Path ADR-0020 Enhancements | **P1** | 2-3 days | Phase 1 |
| 4. Advanced Extension Methods | **P1** | 2-3 days | Phase 2 |
| 5. DI Integration | **P1** | 1 day | Phase 2 |
| 6. MEAI Additional Context | **P2** | 1-2 days | Phase 1 |
| **Total** | | **~2-3 weeks** | |

### Verification Plan

1. **Unit tests:** Each adapter/converter with mocked inputs
2. **Light path integration:** `AIAgent.EvaluateAsync()` with `AgentEvalEvaluator` containing 3+ metrics
3. **Deep path integration:** `MAFEvaluationHarness` streaming evaluation → export to JUnit
4. **Interop test:** Convert deep path `TestResult` → `AgentEvaluationResults.AssertAllPassed()`
5. **Mixed evaluators:** `AgentEvalEvaluators.Agentic()` + `RelevanceEvaluator()` + `FoundryEvals` in same call
6. **Workflow test:** `WorkflowEvaluationHarness` with per-executor breakdown → convert to MAF SubResults

---

## 11. Open Questions for Discussion with Ben

1. **`AgentEvaluationResults` — shipped in current RC?** Needed for result adapter. If not yet, we target GA.

2. **`IConversationSplitter` — defined or conceptual?** We should align implementations. If MAF hasn't defined it yet, we can propose it.

3. **`additionalContext` schema** — is there a defined protocol for passing ground truth, expected tool calls through MEAI's `IEnumerable<EvaluationContext>`? We should co-define.

4. **Workflow event access** — MAF's `run.EvaluateAsync()` walks `OutgoingEvents`. Does AgentEval need to consume these events too, or should we continue using `InProcessExecution.RunStreamingAsync()` + `WatchStreamAsync()` which gives us richer data?

5. **Upstream contributions** — Our `ConversationExtractor` and `ToolCallAssertions` could benefit MAF's `LocalEvaluator`. Worth discussing a PR.

---

## 12. Current Integration Flow: How It Works Today

Understanding the current data flow is critical before building the light path.

### 12.1 Direction That Already Works: MEAI → AgentEval

`MicrosoftEvaluatorAdapter` wraps any MEAI `IEvaluator` (e.g., `RelevanceEvaluator`) as an AgentEval `IMetric`, so it can run inside AgentEval's harnesses alongside native metrics.

```
MEAI IEvaluator (e.g., RelevanceEvaluator, CoherenceEvaluator)
    │
    │  MicrosoftEvaluatorAdapter wraps it as AgentEval IMetric
    │  Score: 1-5 → ScoreNormalizer.FromOneToFive() → 0-100
    │
    ▼
AgentEval IMetric.EvaluateAsync(EvaluationContext) → MetricResult
    │
    ▼
MAFEvaluationHarness runs it alongside other AgentEval metrics
```

**Key file:** `src/AgentEval.Core/Adapters/MicrosoftEvaluatorAdapter.cs` (228 lines)

### 12.2 Direction That Does NOT Exist Yet: AgentEval → MEAI

This is the light path — the reverse bridge:

```
AgentEval IMetric (e.g., FaithfulnessMetric, ToolSuccessMetric)
    │
    │  NEW: AgentEvalMetricAdapter wraps it as MEAI IEvaluator
    │  Score: 0-100 → ScoreNormalizer.ToOneToFive() → 1-5
    │
    ▼
MEAI IEvaluator.EvaluateAsync(messages, response, chatConfig) → EvaluationResult
    │
    ▼
MAF's agent.EvaluateAsync() calls it alongside MEAI/Foundry evaluators
```

The reverse conversion `ScoreNormalizer.ToOneToFive()` already exists at `src/AgentEval.Core/Core/ScoreNormalizer.cs:33`. The light path is **mirroring what we already built, in the opposite direction.**

### 12.3 Deep Path Flow: What AgentEval.MAF Does Today

```
AIAgent (MAF)
    │
    │  MAFAgentAdapter wraps it as IStreamableAgent
    │  - Session management (AgentSession)
    │  - Streaming chunks: TextContent, FunctionCallContent, FunctionResultContent, UsageContent
    │  - Token usage from AgentResponse.Usage
    │
    ▼
MAFEvaluationHarness.RunEvaluationStreamingAsync()
    │
    ├── Captures TTFT from first streaming chunk
    ├── Tracks tool calls with start/end timestamps per tool
    ├── Extracts token usage (actual or estimated)
    ├── Calculates cost via ModelPricing
    ├── Builds ToolCallTimeline with ASCII diagrams
    ├── Runs AgentEval IMetrics on rich EvaluationContext
    ├── Generates FailureReport with categorized reasons & suggestions
    │
    ▼
TestResult { Score, Performance, ToolUsage, Timeline, Failure, CriteriaResults }
    │
    ├── Export: JUnit XML, SARIF, Markdown, JSON, PDF
    └── Assertions: Fluent API for tools, performance, response
```

For workflows, the deep path is even richer:

```
Workflow (MAF)
    │
    │  MAFWorkflowAdapter.FromMAFWorkflow()
    │  - Uses Workflow.ReflectEdges() for static graph
    │  - Builds fullId → cleanName mapping
    │
    ▼
MAFWorkflowEventBridge.StreamAsAgentEvalEvents()
    │
    ├── Detects ChatProtocol vs function-based executors
    ├── Sends ChatMessage input (not string — strings are silently dropped!)
    ├── Sends TurnToken(emitEvents: true) for ChatProtocol executors
    ├── Translates MAF events → AgentEval records:
    │     ExecutorInvokedEvent → edge tracking + output flush
    │     AgentResponseUpdateEvent → tool call buffering + text accumulation
    │     ExecutorCompletedEvent → ExecutorOutputEvent
    │     WorkflowOutputEvent → WorkflowCompleteEvent
    │
    ▼
MAFWorkflowAdapter.ExecuteWorkflowAsync()
    │
    ├── Per-executor: ExecutorStep { output, timing, toolCalls, edges, branchId }
    ├── Edge traversal: EdgeExecution { source, target, type, condition, routing }
    ├── Graph: WorkflowGraphSnapshot { nodes, edges, traversedEdges, branches }
    ├── Routing: RoutingDecision { decider, possibleEdges, selected, reason }
    │
    ▼
WorkflowEvaluationHarness.RunWorkflowTestAsync()
    │
    ├── Assertion: Executor order (strict or loose)
    ├── Assertion: Expected executors present
    ├── Assertion: Output contains expected text
    ├── Assertion: Max duration
    ├── Assertion: No errors
    ├── Assertion: Expected tools (across all executors)
    ├── Assertion: Per-executor expected tools
    │
    ▼
WorkflowTestResult { ExecutionResult, AssertionResults, Duration }
```

---

## 13. AgentEval Metric Inventory & Light Path Compatibility

AgentEval has **19 metrics** across 6 categories. Each implements `IMetric.EvaluateAsync(EvaluationContext) → MetricResult`. The light path can surface all of them, but with varying data richness:

| Category | Metric Class | Name | Needs from Context | Light Path Compat |
|----------|-------------|------|-------------------|:-:|
| **RAG/Quality** | `FaithfulnessMetric` | `llm_faithfulness` | Input, Output, **Context** | Partial ¹ |
| | `RelevanceMetric` | `llm_relevance` | Input, Output | **Full** |
| | `ContextPrecisionMetric` | `llm_context_precision` | Input, Output, **Context**, **GroundTruth** | Partial ¹ |
| | `ContextRecallMetric` | `llm_context_recall` | Input, Output, **Context**, **GroundTruth** | Partial ¹ |
| | `AnswerCorrectnessMetric` | `llm_answer_correctness` | Input, Output, **GroundTruth** | Partial ¹ |
| **Quality** | `GroundednessMetric` | `llm_groundedness` | Input, Output, **Context** | Partial ¹ |
| | `CoherenceMetric` | `llm_coherence` | Input, Output | **Full** |
| | `FluencyMetric` | `llm_fluency` | Input, Output | **Full** |
| **Agentic** | `ToolSelectionMetric` | `code_tool_selection` | Input, Output, **ToolUsage** | Partial ² |
| | `ToolArgumentsMetric` | `code_tool_arguments` | Input, Output, **ToolUsage** | Partial ² |
| | `ToolSuccessMetric` | `code_tool_success` | **ToolUsage** | Partial ² |
| | `TaskCompletionMetric` | `llm_task_completion` | Input, Output | **Full** |
| | `ToolEfficiencyMetric` | `code_tool_efficiency` | **ToolUsage** + **Performance** | **No** ³ |
| **Safety** | `ToxicityMetric` | `llm_toxicity` | Input, Output | **Full** |
| | `BiasMetric` | `llm_bias` | Input, Output | **Full** |
| | `MisinformationMetric` | `llm_misinformation` | Input, Output | **Full** |
| **Retrieval** | `RecallAtKMetric` | `code_recall_at_k` | **Context**, **GroundTruth** | Partial ¹ |
| | `MRRMetric` | `code_mrr` | **Context**, **GroundTruth** | Partial ¹ |
| **Conversation** | `ConversationCompletenessMetric` | `llm_conversation_completeness` | Input, Output | **Full** |

**Legend:**
- **Full** — Works perfectly through light path. Only needs Input + Output from `ChatMessage[]`/`ChatResponse`.
- **Partial ¹** — Works IF the caller passes `Context`/`GroundTruth` through MEAI's `additionalContext` parameter. Requires custom `EvaluationContext` subtypes (Phase 6).
- **Partial ²** — Can extract tool call names/arguments from `FunctionCallContent`/`FunctionResultContent` in the conversation messages. But **no timing data** — no per-tool duration, no start/end timestamps. `ToolUsageReport` will have call names but `Duration` will be zero.
- **No ³** — `ToolEfficiencyMetric` requires `PerformanceMetrics` (token counts, cost, total duration) which MAF's orchestration does not provide. Deep path only.

### 13.1 Summary: 9 Full, 9 Partial, 1 Deep-Only

- **9 metrics work fully** through the light path (quality, safety, conversation, task completion)
- **9 metrics work partially** (RAG needs additionalContext; agentic gets tool names but no timing)
- **1 metric requires deep path** (tool efficiency needs performance data)

This is a strong story: "9 of our 19 metrics work out of the box. Another 9 work with data enrichment. And our deep path gives you the full picture."

---

## 14. Light Path Implementation: Concrete Design

### 14.1 `AgentEvalMetricAdapter` — Core Adapter

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalMetricAdapter.cs`

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;
using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Wraps a single AgentEval <see cref="IMetric"/> as an MEAI <see cref="MEAIIEvaluator"/>,
/// enabling AgentEval metrics to be used inside MAF's <c>agent.EvaluateAsync()</c>
/// orchestration alongside MEAI and Foundry evaluators.
/// </summary>
/// <remarks>
/// This is the "light path" — post-mortem evaluation of a conversation transcript.
/// For live evaluation with streaming, tool timelines, and workflow graph analysis,
/// use <see cref="MAFEvaluationHarness"/> or <see cref="WorkflowEvaluationHarness"/> (deep path).
/// </remarks>
public class AgentEvalMetricAdapter : MEAIIEvaluator
{
    private readonly IMetric _metric;

    public AgentEvalMetricAdapter(IMetric metric)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
    }

    public async Task<EvaluationResult> EvaluateAsync(
        IList<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<MEAIEvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Extract input/output from conversation (last-turn split)
        var input = ConversationExtractor.ExtractLastUserMessage(messages);
        var output = response.Message?.Text ?? "";

        // 2. Build AgentEval EvaluationContext with available data
        var context = new AgentEvalEvaluationContext
        {
            Input = input,
            Output = output,
            Context = AdditionalContextHelper.Extract<string>(additionalContext, "RAGContext"),
            GroundTruth = AdditionalContextHelper.Extract<string>(additionalContext, "GroundTruth"),
            ToolUsage = ConversationExtractor.ExtractToolUsage(messages, response),
        };

        // 3. Run the AgentEval metric
        var metricResult = await _metric.EvaluateAsync(context, cancellationToken);

        // 4. Convert to MEAI EvaluationResult
        return ResultConverter.ToMEAI(metricResult);
    }
}
```

### 14.2 `AgentEvalEvaluator` — Composite

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalEvaluator.cs`

```csharp
/// <summary>
/// Composite evaluator that bundles multiple AgentEval metrics into a single
/// MEAI <see cref="MEAIIEvaluator"/>. Each metric produces a named metric
/// in the returned <see cref="EvaluationResult"/>.
/// </summary>
public class AgentEvalEvaluator : MEAIIEvaluator
{
    private readonly IReadOnlyList<IMetric> _metrics;

    public AgentEvalEvaluator(IEnumerable<IMetric> metrics)
    {
        _metrics = (metrics ?? throw new ArgumentNullException(nameof(metrics))).ToList();
    }

    public async Task<EvaluationResult> EvaluateAsync(
        IList<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<MEAIEvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var input = ConversationExtractor.ExtractLastUserMessage(messages);
        var output = response.Message?.Text ?? "";

        var context = new AgentEvalEvaluationContext
        {
            Input = input,
            Output = output,
            Context = AdditionalContextHelper.Extract<string>(additionalContext, "RAGContext"),
            GroundTruth = AdditionalContextHelper.Extract<string>(additionalContext, "GroundTruth"),
            ToolUsage = ConversationExtractor.ExtractToolUsage(messages, response),
        };

        // Run all metrics and collect results
        var result = new EvaluationResult();
        foreach (var metric in _metrics)
        {
            var metricResult = await metric.EvaluateAsync(context, cancellationToken);
            ResultConverter.AddToEvaluationResult(result, metricResult);
        }
        return result;
    }
}
```

### 14.3 `AgentEvalEvaluators` — Static Factory

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalEvaluators.cs`

```csharp
using AgentEval.Metrics.Agentic;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.Safety;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Factory for creating AgentEval evaluators that implement MEAI's IEvaluator.
/// Use these with MAF's <c>agent.EvaluateAsync()</c> extension methods.
/// </summary>
/// <remarks>
/// Mirrors the discoverability pattern of <c>FoundryEvals.RELEVANCE</c>.
/// </remarks>
public static class AgentEvalEvaluators
{
    // ── Preset Bundles ──────────────────────────────────────────────

    /// <summary>Quality metrics: faithfulness, relevance, coherence, fluency.</summary>
    public static AgentEvalEvaluator Quality(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new CoherenceMetric(judgeClient),
        new FluencyMetric(judgeClient)]);

    /// <summary>RAG metrics: faithfulness, relevance, context precision/recall, answer correctness.</summary>
    public static AgentEvalEvaluator RAG(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new ContextPrecisionMetric(judgeClient),
        new ContextRecallMetric(judgeClient),
        new AnswerCorrectnessMetric(judgeClient)]);

    /// <summary>Agentic metrics: tool selection, success, arguments (no timing data in light path).</summary>
    public static AgentEvalEvaluator Agentic() => new([
        new ToolSuccessMetric(),
        new ToolSelectionMetric([])]);  // caller overrides expected tools

    /// <summary>Agentic metrics with expected tools specified.</summary>
    public static AgentEvalEvaluator Agentic(IEnumerable<string> expectedTools) => new([
        new ToolSuccessMetric(),
        new ToolSelectionMetric(expectedTools),
        new ToolArgumentsMetric(expectedTools)]);

    /// <summary>Safety metrics: toxicity, bias, misinformation.</summary>
    public static AgentEvalEvaluator Safety(IChatClient judgeClient) => new([
        new ToxicityMetric(judgeClient),
        new BiasMetric(judgeClient),
        new MisinformationMetric(judgeClient)]);

    /// <summary>All available metrics (quality + RAG + agentic + safety).</summary>
    public static AgentEvalEvaluator Advanced(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new CoherenceMetric(judgeClient),
        new FluencyMetric(judgeClient),
        new GroundednessMetric(judgeClient),
        new ContextPrecisionMetric(judgeClient),
        new ContextRecallMetric(judgeClient),
        new AnswerCorrectnessMetric(judgeClient),
        new ToolSuccessMetric(),
        new TaskCompletionMetric(judgeClient),
        new ToxicityMetric(judgeClient),
        new BiasMetric(judgeClient),
        new MisinformationMetric(judgeClient)]);

    // ── Individual Metrics as MEAI IEvaluator ───────────────────────

    public static MEAIIEvaluator Faithfulness(IChatClient c) => new AgentEvalMetricAdapter(new FaithfulnessMetric(c));
    public static MEAIIEvaluator Relevance(IChatClient c) => new AgentEvalMetricAdapter(new RelevanceMetric(c));
    public static MEAIIEvaluator Coherence(IChatClient c) => new AgentEvalMetricAdapter(new CoherenceMetric(c));
    public static MEAIIEvaluator Fluency(IChatClient c) => new AgentEvalMetricAdapter(new FluencyMetric(c));
    public static MEAIIEvaluator Groundedness(IChatClient c) => new AgentEvalMetricAdapter(new GroundednessMetric(c));
    public static MEAIIEvaluator ToolSuccess() => new AgentEvalMetricAdapter(new ToolSuccessMetric());
    public static MEAIIEvaluator TaskCompletion(IChatClient c) => new AgentEvalMetricAdapter(new TaskCompletionMetric(c));
    public static MEAIIEvaluator Toxicity(IChatClient c) => new AgentEvalMetricAdapter(new ToxicityMetric(c));
    public static MEAIIEvaluator Bias(IChatClient c) => new AgentEvalMetricAdapter(new BiasMetric(c));
    public static MEAIIEvaluator Misinformation(IChatClient c) => new AgentEvalMetricAdapter(new MisinformationMetric(c));

    // ── Custom Composition ──────────────────────────────────────────

    /// <summary>Create a custom evaluator from specific AgentEval metrics.</summary>
    public static AgentEvalEvaluator Custom(params IMetric[] metrics) => new(metrics);
}
```

### 14.4 `ConversationExtractor` — Data Extraction

**File:** `src/AgentEval.MAF/Evaluators/ConversationExtractor.cs`

```csharp
using Microsoft.Extensions.AI;
using AgentEval.Models;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Extracts AgentEval-compatible data from MEAI conversation types.
/// </summary>
public static class ConversationExtractor
{
    /// <summary>Extracts the last user message as input (last-turn split).</summary>
    public static string ExtractLastUserMessage(IList<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
                return messages[i].Text ?? "";
        }
        return messages.FirstOrDefault()?.Text ?? "";
    }

    /// <summary>
    /// Extracts tool usage from conversation messages.
    /// Captures tool names, arguments, and results — but NOT timing (unavailable in light path).
    /// </summary>
    public static ToolUsageReport? ExtractToolUsage(
        IList<ChatMessage> messages, ChatResponse response)
    {
        var allMessages = messages.Concat(response.Messages ?? []);
        var report = new ToolUsageReport();
        var pendingCalls = new Dictionary<string, ToolCallRecord>();
        int order = 0;

        foreach (var message in allMessages)
        {
            foreach (var content in message.Contents ?? [])
            {
                if (content is FunctionCallContent call)
                {
                    var record = new ToolCallRecord
                    {
                        Name = call.Name,
                        CallId = call.CallId ?? Guid.NewGuid().ToString(),
                        Arguments = call.Arguments,
                        Order = ++order,
                    };
                    pendingCalls[record.CallId] = record;
                }
                else if (content is FunctionResultContent result)
                {
                    var callId = result.CallId ?? "";
                    if (pendingCalls.TryGetValue(callId, out var pending))
                    {
                        pending.Result = result.Result;
                        pending.Exception = result.Exception;
                        report.AddCall(pending);
                        pendingCalls.Remove(callId);
                    }
                }
            }
        }

        // Add any unmatched calls
        foreach (var pending in pendingCalls.Values.OrderBy(c => c.Order))
            report.AddCall(pending);

        return report.Count > 0 ? report : null;
    }
}
```

### 14.5 `ResultConverter` — Score & Result Conversion

**File:** `src/AgentEval.MAF/Evaluators/ResultConverter.cs`

```csharp
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Converts between AgentEval and MEAI evaluation result types.
/// </summary>
public static class ResultConverter
{
    /// <summary>Converts an AgentEval MetricResult to an MEAI EvaluationResult.</summary>
    public static EvaluationResult ToMEAI(MetricResult metricResult)
    {
        var result = new EvaluationResult();
        AddToEvaluationResult(result, metricResult);
        return result;
    }

    /// <summary>Adds an AgentEval MetricResult as a named metric in an MEAI EvaluationResult.</summary>
    public static void AddToEvaluationResult(EvaluationResult result, MetricResult metricResult)
    {
        // Convert 0-100 → 1-5 for MEAI NumericMetric
        var meaiScore = ScoreNormalizer.ToOneToFive(metricResult.Score);

        var numericMetric = new NumericMetric(metricResult.MetricName, meaiScore);

        // Add interpretation with the original 0-100 score and explanation
        var interpretation = $"AgentEval score: {metricResult.Score:F0}/100 " +
            $"({ScoreNormalizer.Interpret(metricResult.Score)})";
        if (!string.IsNullOrEmpty(metricResult.Explanation))
            interpretation += $" — {metricResult.Explanation}";

        numericMetric.AddDiagnostic(
            MetricResultDiagnosticSeverity.Informational,
            interpretation);

        result.Metrics[metricResult.MetricName] = numericMetric;
    }
}
```

### 14.6 `AdditionalContextHelper`

**File:** `src/AgentEval.MAF/Evaluators/AdditionalContextHelper.cs`

```csharp
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Extracts typed data from MEAI's additionalContext parameter.
/// </summary>
public static class AdditionalContextHelper
{
    /// <summary>Extracts a value by key from additional context.</summary>
    public static T? Extract<T>(
        IEnumerable<MEAIEvaluationContext>? additionalContext,
        string key) where T : class
    {
        if (additionalContext == null) return null;

        foreach (var ctx in additionalContext)
        {
            // Check custom context subtypes
            if (key == "RAGContext" && ctx is AgentEvalContexts.RAGContext ragCtx)
                return ragCtx.RetrievedContext as T;
            if (key == "GroundTruth" && ctx is AgentEvalContexts.GroundTruthContext gtCtx)
                return gtCtx.GroundTruth as T;
        }
        return null;
    }
}
```

### 14.7 Package Reference Change

**File:** `src/AgentEval.MAF/AgentEval.MAF.csproj`

Add:
```xml
<PackageReference Include="Microsoft.Extensions.AI.Evaluation" />
<PackageReference Include="Microsoft.Extensions.AI.Evaluation.Quality" />
```

---

## 15. Light Path Limitations: What to Discuss with Ben

When AgentEval metrics run through the light path, they only receive what MAF's orchestration provides. This table shows exactly where data is lost:

| Data | Available in Light Path? | How | Deep Path Provides |
|------|:-:|---|---|
| Input text | **Yes** | Last `ChatRole.User` message | Same |
| Output text | **Yes** | `ChatResponse.Message.Text` | Same |
| RAG context | **If provided** | Caller passes `RAGContext` via `additionalContext` | `TestCase.Context` → `EvaluationContext.Context` |
| Ground truth | **If provided** | Caller passes `GroundTruthContext` via `additionalContext` | `TestCase.GroundTruth` → `EvaluationContext.GroundTruth` |
| Tool call names | **Yes** | Extracted from `FunctionCallContent` in messages | Same + ordering |
| Tool call arguments | **Yes** | From `FunctionCallContent.Arguments` | Same |
| Tool call results | **Yes** | From `FunctionResultContent.Result` | Same |
| **Tool call timing** | **No** | MAF's orchestration doesn't timestamp tool calls | Per-tool `StartTime`/`EndTime`/`Duration` |
| **TTFT** | **No** | No streaming in light path | `StreamingOptions.OnFirstToken` |
| **Token counts** | **No** | Not tracked by MAF orchestration | Actual from provider or estimated |
| **Cost estimation** | **No** | No token data → no cost | `ModelPricing.EstimateCost()` |
| **Workflow graph** | **No** | Not applicable | `WorkflowGraphSnapshot` |
| **Per-executor breakdown** | **No** | Not in single-evaluator path | `ExecutorStep` with per-executor data |
| **Failure diagnostics** | **No** | Just pass/fail scores | `FailureReport` with reasons + suggestions |

### Key Conversation Point for Ben

> "9 of our 19 metrics work fully through the light path out of the box — quality, safety, conversation, task completion. Another 9 work with data enrichment via `additionalContext`. Only 1 (tool efficiency) strictly requires our deep path.
>
> But the data gap matters: through `IEvaluator`, we get *what* tools were called but not *how long they took*. We get the response text but not *time-to-first-token*. We get pass/fail but not *why it failed with root cause analysis*.
>
> That's why both paths exist. Light path for CI gates and quick checks. Deep path when you need to actually debug and optimize."

---

## 16. Meeting Playbook: What to Propose to Ben

### 16.1 Open: Show Alignment — Working Code (2 min)

> "We implemented the light path. AgentEval metrics now implement MEAI's `IEvaluator`. Demo:"
>
> ```csharp
> var results = await agent.EvaluateAsync(
>     queries: ["What's the weather in Seattle?"],
>     evaluator: AgentEvalEvaluators.Quality(judgeClient));
> results.AssertAllPassed();
> ```
>
> "19 metrics — faithfulness, tool success, toxicity, bias — alongside MEAI evaluators and Foundry. Zero new concepts."

### 16.2 Show the Data Gap (3 min)

Show the limitations table (Section 15). Emphasize:

- **Agentic metrics are partially blind** — `IEvaluator` gets tool calls from `FunctionCallContent` in messages, but no timing, no duration, no start/end timestamps
- **No streaming data** — TTFT is critical for UX evaluation. Impossible through post-mortem evaluation
- **No workflow graph** — `run.EvaluateAsync()` does per-agent LINQ extraction but doesn't capture edge traversal, routing decisions, parallel branches
- **No stochastic analysis** — `num_repetitions` runs N times but doesn't compute P90, std dev, success rate distributions

### 16.3 Propose the Dual-Path Model (3 min)

> "The right model is two integration surfaces:
>
> **Light path** — AgentEval as `IEvaluator` inside MAF. Already working. 9 metrics fully compatible, 9 more with data enrichment. Good for CI gates, quick quality checks, mixing with Foundry.
>
> **Deep path** — AgentEval's evaluation engine wrapping MAF agents. For comprehensive evaluation: streaming + tool timelines + workflow graph analysis + stochastic + model comparison + red teaming + memory evaluation. Results convert to `AgentEvaluationResults` for interop.
>
> **No duplication** — light path uses MAF's orchestration, deep path uses ours. Same metrics power both."

### 16.4 Ask for Collaboration (2 min)

Concrete asks:

1. **Is `AgentEvaluationResults` public in the current RC?** We need the type for result interop.

2. **Can we co-define the `additionalContext` protocol?** AgentEval needs to pass `GroundTruth`, `RAGContext`, `ExpectedToolCalls` through MEAI's `IEnumerable<EvaluationContext>`. If we align on a schema, any evaluator can use it — not just AgentEval.

3. **`IConversationSplitter`** — is it defined yet? We should align. If MAF hasn't implemented it, we can propose it.

4. **Would you accept a PR adding tool call timing to the orchestration layer?** Even a lightweight `Stopwatch` around each tool call in `AgentEvaluationExtensions` would let evaluators get durations. This makes the light path useful for more agentic metrics without needing AgentEval's deep path.

5. **Upstream contributions** — Our `ConversationExtractor` and expected tool call validation could benefit MAF's `LocalEvaluator` and `EvalChecks`.

### 16.5 Close: The Positioning

> "MAF provides the native evaluation primitives and Foundry integration. AgentEval is the advanced evaluation engine that plugs in. Light path for simplicity, deep path for depth. One ecosystem, no fragmentation.
>
> The light path is done. Next: result interop so deep path results convert to `AgentEvaluationResults`, and then we extend the deep path with ADR-0020 concepts like conversation split strategies and expected tool call assertions."

---
---

## 17. Light Path — Step-by-Step Implementation Plan

> **Objective:** Enable `agent.EvaluateAsync(queries, AgentEvalEvaluators.Quality(judgeClient))` to work — any AgentEval `IMetric` usable as an MEAI `IEvaluator` inside MAF's orchestration layer.
>
> **Estimated effort:** 3–4 hours for a developer familiar with the codebase.
>
> **Prerequisites:** Working AgentEval build; `Microsoft.Extensions.AI.Evaluation.Quality` v10.3.0 already in `Directory.Packages.props`.

---

### Step 0: Pre-Flight Checks

Before writing any code, verify the foundation.

**0.1** Confirm the MEAI evaluation packages are resolvable:
```bash
cd c:\git\AgentEval
dotnet restore src/AgentEval.MAF/AgentEval.MAF.csproj
```

**0.2** Confirm the MEAI `IEvaluator` signature you'll implement. It lives in `Microsoft.Extensions.AI.Evaluation` (transitive dependency of `.Quality`). The **actual** contract (verified during implementation) is:

```csharp
namespace Microsoft.Extensions.AI.Evaluation;

public interface IEvaluator
{
    IReadOnlyCollection<string> EvaluationMetricNames { get; }

    ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default);
}
```

> **Implementation note (discovered during build):** The actual MEAI v10.3.0 `IEvaluator` interface
> differs from what was initially assumed in this plan in several ways:
> - Returns `ValueTask<EvaluationResult>` not `Task<EvaluationResult>`
> - Requires `IReadOnlyCollection<string> EvaluationMetricNames` property
> - Uses `IEnumerable<ChatMessage>` not `IList<ChatMessage>`
> - `ChatResponse` has `.Text` property, not `.Message.Text`
> - `EvaluationContext` base class requires constructor args `(string name, string content)` — cannot use parameterless constructors or `required init` properties
> - `NumericMetric` uses `Interpretation` property with `EvaluationMetricInterpretation(rating, failed, reason)` — no `AddDiagnostic` method
> - `NumericMetric` constructor is `(string name, double? value, string? reason)` (3 args)
>
> All code in Sections 14.1–14.6 below reflects the **originally planned** design. The **actual implemented code** in `src/AgentEval.MAF/Evaluators/` incorporates these adaptations.

**0.3** Confirm the key MEAI result types you'll produce:
- `EvaluationResult` — has `IDictionary<string, EvaluationMetric> Metrics`
- `NumericMetric` — `new NumericMetric(string name, double? value, string? reason)` → value is on 1–5 scale
- `EvaluationMetricInterpretation` — `new(EvaluationRating rating, bool failed, string? reason)`
- `EvaluationRating` — `Exceptional`, `Good`, `Average`, `Poor`, `Inconclusive`

**0.4** Verify `ScoreNormalizer.ToOneToFive()` exists at `src/AgentEval.Core/Core/ScoreNormalizer.cs:33`:
```csharp
public static double ToOneToFive(double score)
{
    var clamped = Math.Clamp(score, 0.0, 100.0);
    return (clamped / 25.0) + 1.0;
}
```
This is the reverse of `FromOneToFive()` used by `MicrosoftEvaluatorAdapter`. You will reuse this.

**0.5** Review the mirror adapter at `src/AgentEval.Core/Adapters/MicrosoftEvaluatorAdapter.cs` — your implementation is the structural reverse. Study lines 59–155 for the pattern of building `ChatMessage` lists, calling the evaluator, extracting metrics, and converting scores.

---

### Step 1: Add Package Reference to `AgentEval.MAF.csproj`

**File:** `src/AgentEval.MAF/AgentEval.MAF.csproj`

**Action:** Add the MEAI Evaluation package reference. The `.Quality` variant is already in `Directory.Packages.props` so its transitive base (`Microsoft.Extensions.AI.Evaluation`) is available. Add an explicit reference to guarantee the types resolve:

```xml
<ItemGroup>
  <ProjectReference Include="../AgentEval.Abstractions/AgentEval.Abstractions.csproj" />
  <ProjectReference Include="../AgentEval.Core/AgentEval.Core.csproj" />
</ItemGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.Agents.AI" />
  <PackageReference Include="Microsoft.Agents.AI.Workflows" />
  <!-- NEW: MEAI Evaluation for IEvaluator implementation -->
  <PackageReference Include="Microsoft.Extensions.AI.Evaluation.Quality" />
</ItemGroup>
```

**Why `.Quality` and not just `.Evaluation`:** The `.Quality` package is already version-pinned in `Directory.Packages.props` at v10.3.0. It transitively includes the base `.Evaluation` package. Using it directly avoids needing a second `<PackageVersion>` entry. It also gives us access to `NumericMetric`, `BooleanMetric`, etc. which live in the base package.

**Verify:** `dotnet restore src/AgentEval.MAF/AgentEval.MAF.csproj` — must succeed.

---

### Step 2: Create Directory Structure

**Action:** Create the `Evaluators` folder inside the MAF project:

```
src/AgentEval.MAF/
├── MAF/                          ← existing (7 files, untouched)
│   ├── MAFAgentAdapter.cs
│   ├── MAFIdentifiableAgentAdapter.cs
│   ├── MAFWorkflowEventBridge.cs
│   ├── MAFWorkflowAdapter.cs
│   ├── MAFGraphExtractor.cs
│   ├── MAFEvaluationHarness.cs
│   └── WorkflowEvaluationHarness.cs
└── Evaluators/                   ← NEW (6 files)
    ├── AgentEvalMetricAdapter.cs
    ├── AgentEvalEvaluator.cs
    ├── AgentEvalEvaluators.cs
    ├── ConversationExtractor.cs
    ├── ResultConverter.cs
    └── AdditionalContextHelper.cs
```

---

### Step 3: Implement `ConversationExtractor.cs`

**File:** `src/AgentEval.MAF/Evaluators/ConversationExtractor.cs`

**Why first:** This has zero internal dependencies — other files depend on it but it depends on nothing in `Evaluators/`. Build from the leaves inward.

**Full implementation:**

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Models;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Extracts AgentEval-compatible data from MEAI <see cref="ChatMessage"/> conversation types.
/// Used by the light path adapters to convert MAF orchestration data into AgentEval's
/// <see cref="AgentEval.Core.EvaluationContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The light path receives a conversation transcript (<c>IList&lt;ChatMessage&gt;</c> + <c>ChatResponse</c>)
/// from MAF's orchestration layer. This class extracts:
/// </para>
/// <list type="bullet">
///   <item>Input text — last user message (last-turn split, matching ADR-0020 default)</item>
///   <item>Tool usage — <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> pairs</item>
/// </list>
/// <para>
/// <b>Limitation:</b> Tool call timing is NOT available through the light path.
/// <see cref="ToolCallRecord.StartTime"/>, <see cref="ToolCallRecord.EndTime"/>, and
/// <see cref="ToolCallRecord.Duration"/> will be null. For tool timing, use the deep path
/// via <see cref="AgentEval.MAF.MAFEvaluationHarness"/>.
/// </para>
/// </remarks>
public static class ConversationExtractor
{
    /// <summary>
    /// Extracts the last user message text from a conversation (last-turn split).
    /// This matches ADR-0020's default split strategy.
    /// </summary>
    /// <param name="messages">The conversation messages.</param>
    /// <returns>
    /// The text of the last <see cref="ChatRole.User"/> message, or the first message's text
    /// as fallback, or empty string if no messages exist.
    /// </returns>
    public static string ExtractLastUserMessage(IList<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0)
            return "";

        // Walk backwards to find the last user message (last-turn split)
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
                return messages[i].Text ?? "";
        }

        // Fallback: return first message text regardless of role
        return messages[0].Text ?? "";
    }

    /// <summary>
    /// Extracts all user messages concatenated (full-conversation split).
    /// Used when a metric needs the complete input trajectory.
    /// </summary>
    /// <param name="messages">The conversation messages.</param>
    /// <returns>All user messages joined with newlines.</returns>
    public static string ExtractAllUserMessages(IList<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0)
            return "";

        return string.Join("\n", messages
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Text ?? "")
            .Where(t => !string.IsNullOrEmpty(t)));
    }

    /// <summary>
    /// Extracts tool usage from conversation messages and response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pairs <see cref="FunctionCallContent"/> (tool invocation) with
    /// <see cref="FunctionResultContent"/> (tool result) by matching on
    /// <see cref="FunctionCallContent.CallId"/>.
    /// </para>
    /// <para>
    /// <b>No timing data:</b> The light path receives a static conversation transcript.
    /// There are no timestamps on when tools started or finished.
    /// <see cref="ToolCallRecord.StartTime"/>, <see cref="ToolCallRecord.EndTime"/>,
    /// and <see cref="ToolCallRecord.Duration"/> will all be null.
    /// </para>
    /// </remarks>
    /// <param name="messages">The conversation messages (input side).</param>
    /// <param name="response">The chat response (output side).</param>
    /// <returns>
    /// A <see cref="ToolUsageReport"/> with tool call records, or null if no tools were called.
    /// </returns>
    public static ToolUsageReport? ExtractToolUsage(
        IList<ChatMessage> messages,
        ChatResponse response)
    {
        var report = new ToolUsageReport();
        var pendingCalls = new Dictionary<string, ToolCallRecord>();
        int order = 0;

        // Iterate all messages: input conversation + response messages
        IEnumerable<ChatMessage> allMessages = messages;
        if (response.Messages != null)
            allMessages = allMessages.Concat(response.Messages);
        // Also include the primary response message if it has content
        if (response.Message != null)
            allMessages = allMessages.Append(response.Message);

        foreach (var message in allMessages)
        {
            if (message.Contents == null)
                continue;

            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    var callId = call.CallId ?? Guid.NewGuid().ToString("N");
                    var record = new ToolCallRecord
                    {
                        Name = call.Name,
                        CallId = callId,
                        Arguments = call.Arguments,
                        Order = ++order,
                        // No timing data in light path — these remain null
                    };
                    pendingCalls[callId] = record;
                }
                else if (content is FunctionResultContent result)
                {
                    var callId = result.CallId ?? "";
                    if (pendingCalls.TryGetValue(callId, out var pending))
                    {
                        pending.Result = result.Result;
                        pending.Exception = result.Exception;
                        report.AddCall(pending);
                        pendingCalls.Remove(callId);
                    }
                    else
                    {
                        // Result without matching call — create a standalone record
                        report.AddCall(new ToolCallRecord
                        {
                            Name = $"unknown_{callId}",
                            CallId = callId,
                            Result = result.Result,
                            Exception = result.Exception,
                            Order = ++order,
                        });
                    }
                }
            }
        }

        // Add any calls that never got a result (still pending)
        foreach (var pending in pendingCalls.Values.OrderBy(c => c.Order))
            report.AddCall(pending);

        return report.Count > 0 ? report : null;
    }
}
```

**Verify:** Ensure it compiles: `dotnet build src/AgentEval.MAF/AgentEval.MAF.csproj`

---

### Step 4: Implement `AdditionalContextHelper.cs`

**File:** `src/AgentEval.MAF/Evaluators/AdditionalContextHelper.cs`

**Why second:** Zero internal dependencies. `AgentEvalMetricAdapter` will use this.

**Full implementation:**

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Extracts typed data from MEAI's <c>additionalContext</c> parameter.
/// </summary>
/// <remarks>
/// <para>
/// MEAI's <see cref="Microsoft.Extensions.AI.Evaluation.IEvaluator.EvaluateAsync"/> accepts
/// an <c>IEnumerable&lt;EvaluationContext&gt;?</c> parameter for passing extra data.
/// AgentEval uses custom subtypes to carry RAG context, ground truth, and expected tool calls.
/// </para>
/// <para>
/// When users call <c>agent.EvaluateAsync()</c> with AgentEval evaluators and need RAG or
/// ground truth evaluation, they pass these custom context objects.
/// </para>
/// </remarks>
public static class AdditionalContextHelper
{
    /// <summary>
    /// Extracts the RAG retrieval context from additional context, if provided.
    /// </summary>
    public static string? ExtractRAGContext(IEnumerable<MEAIEvaluationContext>? additionalContext)
    {
        if (additionalContext == null) return null;

        foreach (var ctx in additionalContext)
        {
            if (ctx is AgentEvalRAGContext ragCtx)
                return ragCtx.RetrievedContext;
        }
        return null;
    }

    /// <summary>
    /// Extracts the ground truth from additional context, if provided.
    /// </summary>
    public static string? ExtractGroundTruth(IEnumerable<MEAIEvaluationContext>? additionalContext)
    {
        if (additionalContext == null) return null;

        foreach (var ctx in additionalContext)
        {
            if (ctx is AgentEvalGroundTruthContext gtCtx)
                return gtCtx.GroundTruth;
        }
        return null;
    }

    /// <summary>
    /// Extracts expected tool names from additional context, if provided.
    /// </summary>
    public static IReadOnlyList<string>? ExtractExpectedTools(IEnumerable<MEAIEvaluationContext>? additionalContext)
    {
        if (additionalContext == null) return null;

        foreach (var ctx in additionalContext)
        {
            if (ctx is AgentEvalExpectedToolsContext toolsCtx)
                return toolsCtx.ExpectedToolNames;
        }
        return null;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// CUSTOM EVALUATION CONTEXT SUBTYPES
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Carries RAG retrieval context through MEAI's <c>additionalContext</c> parameter.
/// </summary>
/// <example>
/// <code>
/// var results = await agent.EvaluateAsync(queries, evaluator,
///     additionalContext: [new AgentEvalRAGContext { RetrievedContext = "..." }]);
/// </code>
/// </example>
public class AgentEvalRAGContext : MEAIEvaluationContext
{
    /// <summary>The retrieved context/documents for RAG evaluation.</summary>
    public required string RetrievedContext { get; init; }
}

/// <summary>
/// Carries ground truth expected output through MEAI's <c>additionalContext</c> parameter.
/// </summary>
/// <example>
/// <code>
/// var results = await agent.EvaluateAsync(queries, evaluator,
///     additionalContext: [new AgentEvalGroundTruthContext { GroundTruth = "Paris is the capital of France" }]);
/// </code>
/// </example>
public class AgentEvalGroundTruthContext : MEAIEvaluationContext
{
    /// <summary>The expected ground truth answer.</summary>
    public required string GroundTruth { get; init; }
}

/// <summary>
/// Carries expected tool call names through MEAI's <c>additionalContext</c> parameter.
/// </summary>
public class AgentEvalExpectedToolsContext : MEAIEvaluationContext
{
    /// <summary>Names of tools expected to be called.</summary>
    public required IReadOnlyList<string> ExpectedToolNames { get; init; }
}
```

**Verify:** `dotnet build src/AgentEval.MAF/AgentEval.MAF.csproj`

---

### Step 5: Implement `ResultConverter.cs`

**File:** `src/AgentEval.MAF/Evaluators/ResultConverter.cs`

**Why third:** Depends on `ScoreNormalizer` (in AgentEval.Core, already referenced). No dependency on other Evaluators/ files.

**Full implementation:**

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Converts between AgentEval <see cref="MetricResult"/> and MEAI <see cref="EvaluationResult"/> types.
/// </summary>
/// <remarks>
/// <para>
/// AgentEval uses a 0–100 score scale. MEAI uses a 1–5 scale for <see cref="NumericMetric"/>.
/// Conversion uses <see cref="ScoreNormalizer.ToOneToFive"/> (existing utility):
/// </para>
/// <list type="table">
///   <item><term>0 → 1.0</term><description>Very Poor</description></item>
///   <item><term>25 → 2.0</term><description>Poor</description></item>
///   <item><term>50 → 3.0</term><description>Satisfactory</description></item>
///   <item><term>75 → 4.0</term><description>Good</description></item>
///   <item><term>100 → 5.0</term><description>Excellent</description></item>
/// </list>
/// <para>
/// The original 0–100 score is preserved in the metric's diagnostic message for full fidelity.
/// </para>
/// </remarks>
public static class ResultConverter
{
    /// <summary>
    /// Converts a single AgentEval <see cref="MetricResult"/> to an MEAI <see cref="EvaluationResult"/>
    /// containing one named metric.
    /// </summary>
    public static EvaluationResult ToMEAI(MetricResult metricResult)
    {
        var result = new EvaluationResult();
        AddToEvaluationResult(result, metricResult);
        return result;
    }

    /// <summary>
    /// Adds an AgentEval <see cref="MetricResult"/> as a named metric inside an existing
    /// MEAI <see cref="EvaluationResult"/>. Call this multiple times for composite evaluators.
    /// </summary>
    /// <param name="result">The MEAI result to add the metric to.</param>
    /// <param name="metricResult">The AgentEval metric result to convert.</param>
    public static void AddToEvaluationResult(EvaluationResult result, MetricResult metricResult)
    {
        // Convert 0-100 AgentEval scale → 1-5 MEAI scale
        var meaiScore = ScoreNormalizer.ToOneToFive(metricResult.Score);

        var numericMetric = new NumericMetric(metricResult.MetricName, meaiScore);

        // Build a diagnostic message preserving the original 0-100 score
        var interpretation = $"AgentEval score: {metricResult.Score:F0}/100 " +
            $"({ScoreNormalizer.Interpret(metricResult.Score)})";

        if (!string.IsNullOrEmpty(metricResult.Explanation))
            interpretation += $" — {metricResult.Explanation}";

        // Add pass/fail status
        interpretation += metricResult.Passed ? " [PASSED]" : " [FAILED]";

        numericMetric.AddDiagnostic(
            metricResult.Passed
                ? MetricResultDiagnosticSeverity.Informational
                : MetricResultDiagnosticSeverity.Warning,
            interpretation);

        // Add detail diagnostics if available
        if (metricResult.Details != null)
        {
            foreach (var (key, value) in metricResult.Details)
            {
                numericMetric.AddDiagnostic(
                    MetricResultDiagnosticSeverity.Informational,
                    $"{key}: {value}");
            }
        }

        result.Metrics[metricResult.MetricName] = numericMetric;
    }
}
```

**Verify:** `dotnet build src/AgentEval.MAF/AgentEval.MAF.csproj`

---

### Step 6: Implement `AgentEvalMetricAdapter.cs`

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalMetricAdapter.cs`

**Why fourth:** This is the core adapter. Depends on `ConversationExtractor`, `AdditionalContextHelper`, `ResultConverter` (all built in steps 3–5).

**Full implementation:**

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;

// Alias to avoid conflict with AgentEval.Core.EvaluationContext
using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Wraps a single AgentEval <see cref="IMetric"/> as an MEAI <see cref="MEAIIEvaluator"/>,
/// enabling AgentEval metrics to be used inside MAF's <c>agent.EvaluateAsync()</c>
/// orchestration alongside MEAI and Foundry evaluators.
/// </summary>
/// <remarks>
/// <para>
/// This is the "light path" adapter. It performs post-mortem evaluation of a conversation
/// transcript. The adapter:
/// </para>
/// <list type="number">
///   <item>Extracts input/output from <see cref="ChatMessage"/> conversation (last-turn split)</item>
///   <item>Extracts tool usage from <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> pairs</item>
///   <item>Extracts optional RAG context / ground truth from <c>additionalContext</c></item>
///   <item>Builds an AgentEval <see cref="AgentEvalEvaluationContext"/></item>
///   <item>Runs the wrapped <see cref="IMetric"/></item>
///   <item>Converts <see cref="MetricResult"/> → MEAI <see cref="EvaluationResult"/></item>
/// </list>
/// <para>
/// <b>Limitations vs. deep path:</b> No streaming data (TTFT), no tool call timing,
/// no performance metrics (token counts, cost), no workflow graph.
/// For those capabilities, use <see cref="AgentEval.MAF.MAFEvaluationHarness"/>.
/// </para>
/// </remarks>
public class AgentEvalMetricAdapter : MEAIIEvaluator
{
    private readonly IMetric _metric;

    /// <summary>
    /// Creates an adapter wrapping an AgentEval metric as an MEAI evaluator.
    /// </summary>
    /// <param name="metric">The AgentEval metric to wrap.</param>
    public AgentEvalMetricAdapter(IMetric metric)
    {
        _metric = metric ?? throw new ArgumentNullException(nameof(metric));
    }

    /// <summary>Gets the name of the wrapped AgentEval metric.</summary>
    public string MetricName => _metric.Name;

    /// <inheritdoc/>
    public async Task<EvaluationResult> EvaluateAsync(
        IList<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<MEAIEvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Extract input/output from conversation (last-turn split — ADR-0020 default)
        var input = ConversationExtractor.ExtractLastUserMessage(messages);
        var output = response.Message?.Text ?? "";

        // 2. Build AgentEval EvaluationContext with all available data
        var context = new AgentEvalEvaluationContext
        {
            Input = input,
            Output = output,
            Context = AdditionalContextHelper.ExtractRAGContext(additionalContext),
            GroundTruth = AdditionalContextHelper.ExtractGroundTruth(additionalContext),
            ToolUsage = ConversationExtractor.ExtractToolUsage(messages, response),
            ExpectedTools = AdditionalContextHelper.ExtractExpectedTools(additionalContext),
        };

        // 3. Run the AgentEval metric
        var metricResult = await _metric.EvaluateAsync(context, cancellationToken);

        // 4. Convert MetricResult → MEAI EvaluationResult
        return ResultConverter.ToMEAI(metricResult);
    }
}
```

**Verify:** `dotnet build src/AgentEval.MAF/AgentEval.MAF.csproj`

---

### Step 7: Implement `AgentEvalEvaluator.cs`

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalEvaluator.cs`

**Why fifth:** Composite — depends on the same helpers as `AgentEvalMetricAdapter`, bundles N metrics.

**Full implementation:**

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Core;

using AgentEvalEvaluationContext = AgentEval.Core.EvaluationContext;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Composite evaluator that bundles multiple AgentEval <see cref="IMetric"/> instances
/// into a single MEAI <see cref="MEAIIEvaluator"/>. Each metric produces a named metric
/// in the returned <see cref="EvaluationResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use this when you want to run multiple AgentEval metrics in a single evaluator call.
/// Construct via <see cref="AgentEvalEvaluators"/> factory for preset bundles.
/// </para>
/// <para>
/// All metrics share the same <see cref="AgentEvalEvaluationContext"/> built from
/// the conversation transcript. Metrics are run sequentially to avoid contention
/// on the judge LLM (LLM-evaluated metrics make API calls).
/// </para>
/// </remarks>
public class AgentEvalEvaluator : MEAIIEvaluator
{
    private readonly IReadOnlyList<IMetric> _metrics;

    /// <summary>
    /// Creates a composite evaluator from a collection of AgentEval metrics.
    /// </summary>
    /// <param name="metrics">The metrics to include in this evaluator.</param>
    public AgentEvalEvaluator(IEnumerable<IMetric> metrics)
    {
        _metrics = (metrics ?? throw new ArgumentNullException(nameof(metrics))).ToList();
        if (_metrics.Count == 0)
            throw new ArgumentException("At least one metric must be provided.", nameof(metrics));
    }

    /// <summary>Gets the number of metrics in this evaluator.</summary>
    public int MetricCount => _metrics.Count;

    /// <summary>Gets the names of all included metrics.</summary>
    public IEnumerable<string> MetricNames => _metrics.Select(m => m.Name);

    /// <inheritdoc/>
    public async Task<EvaluationResult> EvaluateAsync(
        IList<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<MEAIEvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Extract input/output once (shared across all metrics)
        var input = ConversationExtractor.ExtractLastUserMessage(messages);
        var output = response.Message?.Text ?? "";

        // 2. Build EvaluationContext once
        var context = new AgentEvalEvaluationContext
        {
            Input = input,
            Output = output,
            Context = AdditionalContextHelper.ExtractRAGContext(additionalContext),
            GroundTruth = AdditionalContextHelper.ExtractGroundTruth(additionalContext),
            ToolUsage = ConversationExtractor.ExtractToolUsage(messages, response),
            ExpectedTools = AdditionalContextHelper.ExtractExpectedTools(additionalContext),
        };

        // 3. Run all metrics sequentially, collecting results
        var result = new EvaluationResult();
        foreach (var metric in _metrics)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var metricResult = await metric.EvaluateAsync(context, cancellationToken);
                ResultConverter.AddToEvaluationResult(result, metricResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // If a single metric fails, add a failed entry and continue
                var failedResult = MetricResult.Fail(
                    metric.Name,
                    $"Metric execution failed: {ex.Message}");
                ResultConverter.AddToEvaluationResult(result, failedResult);
            }
        }

        return result;
    }
}
```

**Verify:** `dotnet build src/AgentEval.MAF/AgentEval.MAF.csproj`

---

### Step 8: Implement `AgentEvalEvaluators.cs`

**File:** `src/AgentEval.MAF/Evaluators/AgentEvalEvaluators.cs`

**Why last:** This is the public API surface. Depends on `AgentEvalEvaluator`, `AgentEvalMetricAdapter`, and all metric classes.

**Full implementation:**

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.Safety;
using AgentEval.Metrics;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Factory for creating AgentEval evaluators that implement MEAI's
/// <see cref="MEAIIEvaluator"/> interface.
/// </summary>
/// <remarks>
/// <para>
/// Use these with MAF's <c>agent.EvaluateAsync()</c> extension methods:
/// </para>
/// <code>
/// var results = await agent.EvaluateAsync(
///     queries: ["What's the weather in Seattle?"],
///     evaluator: AgentEvalEvaluators.Quality(judgeClient));
/// results.AssertAllPassed();
/// </code>
/// <para>
/// Mirrors the discoverability pattern of MAF's <c>FoundryEvals.RELEVANCE</c>.
/// </para>
/// </remarks>
public static class AgentEvalEvaluators
{
    // ═══════════════════════════════════════════════════════════════════
    // PRESET BUNDLES — return AgentEvalEvaluator (composite)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Quality metrics bundle: faithfulness, relevance, coherence, fluency.
    /// All work fully through the light path (text-only evaluation).
    /// </summary>
    /// <param name="judgeClient">The LLM chat client used as the evaluation judge.</param>
    public static AgentEvalEvaluator Quality(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new CoherenceMetric(judgeClient),
        new FluencyMetric(judgeClient)]);

    /// <summary>
    /// RAG metrics bundle: faithfulness, relevance, context precision, context recall, answer correctness.
    /// Requires <see cref="AgentEvalRAGContext"/> and/or <see cref="AgentEvalGroundTruthContext"/>
    /// passed via <c>additionalContext</c> for full effectiveness.
    /// </summary>
    /// <param name="judgeClient">The LLM chat client used as the evaluation judge.</param>
    public static AgentEvalEvaluator RAG(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new ContextPrecisionMetric(judgeClient),
        new ContextRecallMetric(judgeClient),
        new AnswerCorrectnessMetric(judgeClient)]);

    /// <summary>
    /// Agentic metrics bundle: tool success rate.
    /// Tool names and arguments are extracted from <see cref="FunctionCallContent"/>
    /// in conversation messages. Tool timing is NOT available (light path limitation).
    /// </summary>
    public static AgentEvalEvaluator Agentic() => new([
        new ToolSuccessMetric()]);

    /// <summary>
    /// Agentic metrics bundle with expected tools: tool success, tool selection, tool arguments.
    /// </summary>
    /// <param name="expectedTools">The tool names expected to be called by the agent.</param>
    public static AgentEvalEvaluator Agentic(IEnumerable<string> expectedTools) => new([
        new ToolSuccessMetric(),
        new ToolSelectionMetric(expectedTools),
        new ToolArgumentsMetric(expectedTools)]);

    /// <summary>
    /// Safety metrics bundle: toxicity, bias, misinformation.
    /// All work fully through the light path (text-only evaluation).
    /// </summary>
    /// <param name="judgeClient">The LLM chat client used as the evaluation judge.</param>
    public static AgentEvalEvaluator Safety(IChatClient judgeClient) => new([
        new ToxicityMetric(judgeClient),
        new BiasMetric(judgeClient),
        new MisinformationMetric(judgeClient)]);

    /// <summary>
    /// All available metrics combined (quality + agentic + safety + task completion).
    /// The most comprehensive single-call evaluation.
    /// </summary>
    /// <param name="judgeClient">The LLM chat client used as the evaluation judge.</param>
    public static AgentEvalEvaluator Advanced(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new CoherenceMetric(judgeClient),
        new FluencyMetric(judgeClient),
        new GroundednessMetric(judgeClient),
        new ToolSuccessMetric(),
        new TaskCompletionMetric(judgeClient),
        new ToxicityMetric(judgeClient),
        new BiasMetric(judgeClient),
        new MisinformationMetric(judgeClient)]);

    // ═══════════════════════════════════════════════════════════════════
    // INDIVIDUAL METRICS — return MEAIIEvaluator (single metric)
    // Use these when mixing AgentEval metrics with MEAI/Foundry evaluators.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Faithfulness: is the response grounded in the provided context?</summary>
    public static MEAIIEvaluator Faithfulness(IChatClient c) => Adapt(new FaithfulnessMetric(c));
    /// <summary>Relevance: does the response address the user's question?</summary>
    public static MEAIIEvaluator Relevance(IChatClient c) => Adapt(new RelevanceMetric(c));
    /// <summary>Coherence: is the response logically consistent?</summary>
    public static MEAIIEvaluator Coherence(IChatClient c) => Adapt(new CoherenceMetric(c));
    /// <summary>Fluency: is the response grammatically correct and natural?</summary>
    public static MEAIIEvaluator Fluency(IChatClient c) => Adapt(new FluencyMetric(c));
    /// <summary>Groundedness: does the response avoid unsubstantiated claims?</summary>
    public static MEAIIEvaluator Groundedness(IChatClient c) => Adapt(new GroundednessMetric(c));
    /// <summary>Tool success: did all invoked tools execute without errors?</summary>
    public static MEAIIEvaluator ToolSuccess() => Adapt(new ToolSuccessMetric());
    /// <summary>Task completion: did the agent accomplish the requested task?</summary>
    public static MEAIIEvaluator TaskCompletion(IChatClient c) => Adapt(new TaskCompletionMetric(c));
    /// <summary>Toxicity: does the response contain harmful content?</summary>
    public static MEAIIEvaluator Toxicity(IChatClient c) => Adapt(new ToxicityMetric(c));
    /// <summary>Bias: does the response contain biased content?</summary>
    public static MEAIIEvaluator Bias(IChatClient c) => Adapt(new BiasMetric(c));
    /// <summary>Misinformation: does the response contain false claims?</summary>
    public static MEAIIEvaluator Misinformation(IChatClient c) => Adapt(new MisinformationMetric(c));

    // ═══════════════════════════════════════════════════════════════════
    // CUSTOM COMPOSITION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a custom composite evaluator from specific AgentEval metrics.
    /// </summary>
    public static AgentEvalEvaluator Custom(params IMetric[] metrics) => new(metrics);

    /// <summary>
    /// Wraps any AgentEval <see cref="IMetric"/> as an individual MEAI evaluator.
    /// </summary>
    public static MEAIIEvaluator Adapt(IMetric metric) => new AgentEvalMetricAdapter(metric);
}
```

**Verify:** `dotnet build src/AgentEval.MAF/AgentEval.MAF.csproj` — this is the final build check. All 6 files should compile together.

---

### Step 9: Write Unit Tests

**Directory:** `tests/AgentEval.Tests/MAF/Evaluators/` (create if needed)

**9.1 — `ConversationExtractorTests.cs`**

Test cases:
- `ExtractLastUserMessage_WithMultipleMessages_ReturnsLastUserMessage`
- `ExtractLastUserMessage_WithNoUserMessages_ReturnsFirstMessage`
- `ExtractLastUserMessage_WithEmptyList_ReturnsEmptyString`
- `ExtractToolUsage_WithToolCalls_ReturnsPairedRecords`
- `ExtractToolUsage_WithNoToolCalls_ReturnsNull`
- `ExtractToolUsage_WithUnmatchedResult_CreatesStandaloneRecord`
- `ExtractToolUsage_WithUnmatchedCall_IncludesAsPending`
- `ExtractToolUsage_PreservesCallOrder`

**9.2 — `ResultConverterTests.cs`**

Test cases:
- `ToMEAI_WithPassingResult_ReturnsNumericMetricWithCorrectScore`
- `ToMEAI_Score0_MapsTo1` (verify `ScoreNormalizer.ToOneToFive(0) == 1.0`)
- `ToMEAI_Score50_MapsTo3` (verify midpoint)
- `ToMEAI_Score100_MapsTo5` (verify max)
- `ToMEAI_WithExplanation_IncludesDiagnostic`
- `ToMEAI_WithFailingResult_HasWarningDiagnostic`
- `AddToEvaluationResult_MultipleCalls_AddsMultipleMetrics`

**9.3 — `AgentEvalMetricAdapterTests.cs`**

Test cases:
- `EvaluateAsync_WithTextOnlyMetric_ExtractsInputOutputCorrectly`
- `EvaluateAsync_WithToolUsageMetric_ExtractsToolCalls`
- `EvaluateAsync_WithRAGContext_PassesContextToMetric`
- `EvaluateAsync_WithGroundTruth_PassesGroundTruthToMetric`
- `EvaluateAsync_WithNoAdditionalContext_NullContextAndGroundTruth`

Use a mock `IMetric` that records the `EvaluationContext` it receives, then assert the context fields.

**9.4 — `AgentEvalEvaluatorTests.cs`**

Test cases:
- `EvaluateAsync_WithMultipleMetrics_ReturnsAllMetricResults`
- `EvaluateAsync_WithFailingMetric_ContinuesAndIncludesFailure`
- `EvaluateAsync_WithCancellation_ThrowsOperationCanceled`
- `MetricCount_ReturnsCorrectCount`
- `Constructor_WithEmptyMetrics_ThrowsArgumentException`

**9.5 — `AgentEvalEvaluatorsTests.cs`**

Test cases:
- `Quality_ReturnsEvaluatorWithFourMetrics`
- `RAG_ReturnsEvaluatorWithFiveMetrics`
- `Safety_ReturnsEvaluatorWithThreeMetrics`
- `Advanced_ReturnsEvaluatorWithTenMetrics`
- `Agentic_WithExpectedTools_ReturnsEvaluatorWithThreeMetrics`
- `Individual_Faithfulness_ReturnsAdapterWrappingFaithfulnessMetric`
- `Custom_WithArbitraryMetrics_CreatesEvaluator`
- `Adapt_WrapsAnyIMetricAsIEvaluator`

**Verify:** `dotnet test tests/AgentEval.Tests/ --filter "FullyQualifiedName~MAF.Evaluators"`

---

### Step 10: End-to-End Smoke Test

Create a minimal integration test that verifies the full flow without a live LLM.

**File:** `tests/AgentEval.Tests/MAF/Evaluators/LightPathIntegrationTests.cs`

```csharp
[Fact]
public async Task LightPath_ToolSuccessMetric_ExtractsToolCallsFromConversation()
{
    // Arrange — simulate a conversation with tool calls (no LLM needed)
    var messages = new List<ChatMessage>
    {
        new(ChatRole.User, "What's the weather in Seattle?"),
        new(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_weather",
            new Dictionary<string, object?> { ["city"] = "Seattle" })]),
        new(ChatRole.Tool, [new FunctionResultContent("call-1", "get_weather",
            result: "72°F and sunny")]),
    };
    var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
        "The weather in Seattle is 72°F and sunny."));

    // Act — run ToolSuccessMetric through the light path
    var evaluator = AgentEvalEvaluators.ToolSuccess();
    var result = await evaluator.EvaluateAsync(messages, response);

    // Assert — metric should have produced a result
    Assert.True(result.Metrics.ContainsKey("code_tool_success"));
    var metric = result.Metrics["code_tool_success"];
    Assert.IsType<NumericMetric>(metric);
    var numericMetric = (NumericMetric)metric;
    // Score should be 5.0 (100/100 → 5.0 on MEAI scale) — all tools succeeded
    Assert.Equal(5.0, numericMetric.Value, precision: 1);
}
```

**Verify:** `dotnet test tests/AgentEval.Tests/ --filter "LightPathIntegrationTests"`

---

### Step 11: Final Verification Checklist

Run through each verification in order:

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 1 | Full solution builds | `dotnet build AgentEval.sln` | 0 errors |
| 2 | All existing tests pass | `dotnet test tests/AgentEval.Tests/` | No regressions |
| 3 | New unit tests pass | `dotnet test --filter "MAF.Evaluators"` | All green |
| 4 | Integration test passes | `dotnet test --filter "LightPathIntegrationTests"` | Green |
| 5 | No new warnings | Check build output | Clean or pre-existing only |
| 6 | Code style | Consistent with existing `MicrosoftEvaluatorAdapter.cs` | Same patterns |

---

### Step 12: Summary of Files Created

| # | File | Lines (approx) | Depends On |
|---|------|:-:|---|
| 1 | `src/AgentEval.MAF/Evaluators/ConversationExtractor.cs` | ~120 | MEAI types, `ToolUsageReport`, `ToolCallRecord` |
| 2 | `src/AgentEval.MAF/Evaluators/AdditionalContextHelper.cs` | ~100 | MEAI `EvaluationContext` |
| 3 | `src/AgentEval.MAF/Evaluators/ResultConverter.cs` | ~80 | `ScoreNormalizer`, MEAI `NumericMetric` |
| 4 | `src/AgentEval.MAF/Evaluators/AgentEvalMetricAdapter.cs` | ~90 | Steps 1-3 |
| 5 | `src/AgentEval.MAF/Evaluators/AgentEvalEvaluator.cs` | ~100 | Steps 1-3 |
| 6 | `src/AgentEval.MAF/Evaluators/AgentEvalEvaluators.cs` | ~130 | Steps 4-5, all metric classes |
| | **Total new production code** | **~620** | |
| 7 | `AgentEval.MAF.csproj` (modified) | +1 line | Package reference |
| 8 | Unit + integration tests | ~300 | All above |

---

## 18. Presentation to Ben — Full Script & Slides

> **Meeting context:** In-person or Teams call with Ben Thompson (MAF team lead for evaluation).
> **Duration:** 12 minutes presentation + 8 minutes discussion = 20 minutes total.
> **Goal:** Show working alignment, demonstrate value, secure collaboration commitment.

---

### Slide 1: Title (show while settling in — 0 min)

**Visual:** AgentEval logo + MAF logo side by side. Title: "AgentEval + Agent Framework: Better Together"

**Text on slide:**
- AgentEval: The .NET Evaluation Toolkit for AI Agents
- Integration with Agent Framework Evaluation (ADR-0020)
- [Your name] — [Date]

*No script — just visible while you set up.*

---

### Slide 2: We Built It — Live Demo (0:00–2:30)

**Visual:** Code snippet centered on slide, large font:

```csharp
// Before: MEAI evaluators only
var results = await agent.EvaluateAsync(queries,
    new RelevanceEvaluator());

// Now: AgentEval metrics plug in directly
var results = await agent.EvaluateAsync(queries,
    AgentEvalEvaluators.Quality(judgeClient));

results.AssertAllPassed();
```

**Script (read aloud, conversational):**

> "Ben, first — thank you for sharing the ADR. I want to start by showing you what we've already built.
>
> After reading your ADR, we implemented what we're calling the 'light path.' AgentEval's metrics now implement MEAI's `IEvaluator` directly. Here's what it looks like.
>
> *[Point to code]* Same `agent.EvaluateAsync()` you designed. Same `AssertAllPassed()` pattern. But behind that evaluator, there are four AgentEval metrics running — faithfulness, relevance, coherence, fluency — all powered by LLM-as-judge.
>
> We have 19 metrics total across quality, RAG, agentic, safety, and responsible AI. Nine of them work fully through this path out of the box. Another nine work with data enrichment. We also have preset bundles — `.Quality()`, `.RAG()`, `.Safety()`, `.Agentic()` — that mirror your `FoundryEvals.RELEVANCE` discoverability pattern.
>
> And it mixes seamlessly:"

**Show second snippet (same slide or transition):**

```csharp
var results = await agent.EvaluateAsync(queries,
    evaluators: [
        new RelevanceEvaluator(),                      // MEAI native
        AgentEvalEvaluators.Safety(judgeClient),       // AgentEval bundle
        new FoundryEvals(projectClient, "gpt-4o"),     // Foundry cloud
    ]);
```

> "MEAI, AgentEval, and Foundry — all in one call. One ecosystem. This is live, it compiles, it runs."

---

### Slide 3: What the Light Path Can't Do (2:30–5:00)

**Visual:** Two-column comparison table, highlighted cells:

| | Light Path (IEvaluator) | Deep Path (AgentEval Engine) |
|---|:-:|:-:|
| Quality metrics (text) | **Full** | Full |
| Safety metrics (text) | **Full** | Full |
| Tool call names/args | **Yes** | Yes |
| **Tool call timing** | -- | **Per-tool start/end/duration** |
| **Time-to-first-token** | -- | **Via streaming** |
| **Token counts & cost** | -- | **Actual or estimated** |
| **Workflow graph** | -- | **Nodes, edges, traversal** |
| **Per-executor breakdown** | -- | **Steps, tools per executor** |
| **Stochastic (N runs + P90)** | -- | **Full statistics** |
| **Model comparison** | -- | **Rankings + winner** |
| **Red teaming** | -- | **192 probes, OWASP/MITRE** |
| **Failure root cause** | -- | **FailureReport + suggestions** |
| **CI export** | Portal | **JUnit, SARIF, PDF** |

**Script:**

> "Now, here's the honest picture. The light path — plugging into `IEvaluator` — gives you the conversation transcript after the fact. That's enough for quality and safety metrics. But it's post-mortem evaluation.
>
> *[Point to right column]* AgentEval also has what we call the 'deep path.' This wraps your MAF agents and workflows, and captures data that the `IEvaluator` contract physically cannot carry.
>
> Time-to-first-token from streaming. Per-tool start and end timestamps with durations. Full workflow graph extraction — we use your `Workflow.ReflectEdges()` API to build graph snapshots with edge traversal tracking. Per-executor breakdowns with tool calls per agent. Stochastic evaluation with P90 and standard deviation. Model comparison with rankings.
>
> We already have 2,500 lines of deep MAF integration code doing this — including handling your ChatProtocol detection and TurnToken sending, which is non-trivial.
>
> Both paths share the same metrics. The difference is what data the metrics receive."

---

### Slide 4: Architecture — Dual Path (5:00–7:00)

**Visual:** Clean architecture diagram:

```
┌─────────────────────────────────────────────────────┐
│              MAF User Code                           │
│                                                      │
│  Light:  agent.EvaluateAsync(q, AgentEvalEvals...)  │
│  Deep:   harness.RunEvaluationStreamingAsync(...)    │
│  Deep:   wfHarness.RunWorkflowTestAsync(...)         │
└────────┬──────────────────────────┬──────────────────┘
         │                          │
    MAF Orchestration        AgentEval Engine
    (runs agent,             (streaming, tool
     calls IEvaluator)        timelines, graphs)
         │                          │
         └──────────┬───────────────┘
                    │
           AgentEval IMetric
        (19 metrics, same code)
                    │
         ┌──────────┴──────────┐
         │   Result Interop    │
         │ TestResult ↔ MEAI   │
         │ JUnit/SARIF/PDF     │
         └─────────────────────┘
```

**Script:**

> "Here's the architecture. Two paths, one metric layer.
>
> *[Point to left]* The light path uses your orchestration. MAF runs the agent, calls our evaluator, gets results. Zero new concepts for your users.
>
> *[Point to right]* The deep path uses our engine. Same metrics, but fed with richer data — streaming chunks, tool timelines, workflow events.
>
> *[Point to bottom]* Both paths produce interoperable results. Deep path results convert to `AgentEvaluationResults` so users can call `.AssertAllPassed()`. Either path feeds into our export pipeline — JUnit XML for CI, SARIF for GitHub security tab, PDF for executive reporting.
>
> No duplication. Your orchestration handles agent invocation. Our engine handles evaluation intelligence. Clean separation."

---

### Slide 5: What MAF Gets — Win-Win (7:00–9:30)

**Visual:** Two columns: "What AgentEval Gets" / "What MAF Gets"

| AgentEval Gets | MAF Gets |
|----------------|----------|
| First-class integration with MAF ecosystem | **19 additional metrics** beyond MEAI's 6 quality evaluators |
| Visibility to MAF users | **LLM-as-judge evaluation** (faithfulness, task completion) with no extra work |
| Alignment with Foundry pattern | **Agentic metrics** (tool selection, tool success, tool arguments) |
| Deep path validated by real MAF workflows | **Safety metrics** (toxicity, bias, misinformation) not in Foundry |
| | **RAG metrics** (context precision/recall, answer correctness) |
| | **Red teaming** — 192 probes, OWASP LLM Top 10 |
| | **Memory evaluation** — benchmark suite for agents with memory |
| | **Stochastic evaluation** — statistical analysis your `num_repetitions` doesn't compute |
| | **Model comparison** — side-by-side with rankings |
| | **CI/CD export** — JUnit, SARIF, PDF beyond Foundry portal |

**Script:**

> "I want to be clear about why this is a win for MAF, not just for AgentEval.
>
> Right now, MAF ships with MEAI's quality evaluators — relevance, coherence, fluency, groundedness, equivalence, completeness. That's six metrics. Foundry adds cloud-based evaluators but requires Azure setup.
>
> *[Point to right column]* With AgentEval plugging in via `IEvaluator`, your users instantly get 19 more metrics. Faithfulness that catches hallucinations. Tool selection and success metrics for agentic workflows. Toxicity, bias, and misinformation detection. RAG evaluation with context precision and recall.
>
> And these aren't toys. They're battle-tested with calibrated multi-judge consensus, stochastic evaluation, and the deep path gives workflow-level analysis that no other .NET evaluation tool provides.
>
> Your `LocalEvaluator` provides lambda-based checks — keyword checks, tool called checks. AgentEval provides the LLM-as-judge intelligence layer above that.
>
> This is not AgentEval competing with MAF evaluation. It's AgentEval extending what MAF can offer. Your users write `agent.EvaluateAsync()` — they get a much richer evaluation story."

---

### Slide 6: Collaboration Asks (9:30–11:00)

**Visual:** Numbered list with icons

**Script:**

> "Here's what I'd like to collaborate on. Five concrete items.
>
> **One — `AgentEvaluationResults` type availability.** We need it to convert deep path results into MAF-compatible format. Is it public in the current RC? If not, when can we expect it?
>
> **Two — Co-define the `additionalContext` protocol.** MEAI's `IEvaluator` has an `additionalContext` parameter. Right now there's no standard schema for passing ground truth, RAG context, or expected tool calls through it. We've defined `AgentEvalGroundTruthContext`, `AgentEvalRAGContext`, and `AgentEvalExpectedToolsContext`. If we align on this, any evaluator — not just AgentEval — can use it. This benefits the whole ecosystem.
>
> **Three — `IConversationSplitter` alignment.** Your ADR defines last-turn, full, and per-turn split strategies. We should align on the interface so our implementations are interchangeable.
>
> **Four — this one benefits MAF directly: tool call timing in the orchestration layer.** Right now, `AgentEvaluationExtensions` runs the agent and collects the response, but doesn't timestamp individual tool calls. Even a lightweight `Stopwatch` around each tool invocation would let evaluators — ours and others — get tool durations. This makes agentic evaluation much more useful through the light path. We're happy to contribute this PR.
>
> **Five — upstream contributions.** Our `ConversationExtractor` and expected tool call validation logic could benefit MAF's `LocalEvaluator` and `EvalChecks`. We'd like to discuss contributing some of this."

---

### Slide 7: Next Steps & Timeline (11:00–12:00)

**Visual:** Simple timeline

```
  ✅ Done        Now          Next 2 weeks       After
  ─────────────┬─────────────┬─────────────────┬──────────
  Light path   │ This        │ Result interop  │ Upstream
  implemented  │ meeting     │ (TestResult ↔   │ PRs to
               │             │ AgentEvalResults)│ MAF
               │             │                 │
               │             │ Conversation    │ Deep path
               │             │ split alignment │ extensions
               │             │                 │ with ADR-
               │             │ additionalCtx   │ 0020
               │             │ schema co-def   │ concepts
```

**Script:**

> "To close — here's where we are and where we're going.
>
> The light path is implemented. AgentEval metrics work as MEAI `IEvaluator` today.
>
> Next two weeks: result interoperability so deep path results convert to `AgentEvaluationResults`, conversation split alignment, and co-defining the `additionalContext` schema.
>
> After that: upstream PRs to MAF — tool call timing, conversation extraction utilities — and extending our deep path with ADR-0020 concepts.
>
> I think we're converging on something really strong here. AgentEval as the advanced evaluation engine behind MAF's native evaluation primitives. One ecosystem for .NET agent evaluation."

---

### Discussion Phase (12:00–20:00)

**Prepared responses for likely questions:**

**Q: "Why not just contribute your metrics directly to MEAI?"**
> "Two reasons. First, many of our metrics are LLM-as-judge — they require an `IChatClient` for the judge model. MEAI's built-in evaluators are self-contained. Second, our value isn't just individual metrics — it's the evaluation engine: stochastic evaluation, calibrated multi-judge, model comparison, red teaming, memory benchmarks. Those don't fit into a single `IEvaluator` implementation. The light path gives you our metrics. The deep path gives you our engine."

**Q: "Is there overlap with Foundry Evals?"**
> "Complementary, not overlapping. Foundry gives you cloud-scale evaluation with dashboard views — great for production monitoring. AgentEval gives you developer-time evaluation — fast local feedback, CI integration, detailed debugging. Your `LocalEvaluator` pattern is exactly right for this. We extend it with LLM-as-judge intelligence."

**Q: "What about the `IAgentEvaluator` interface mentioned in the ADR for .NET?"**
> "The ADR mentions `IAgentEvaluator` in the Python ↔ .NET mapping table as the .NET equivalent of Python's `Evaluator` protocol — for batch-level evaluation across items. If you ship this interface, we'd implement that too. Our `AgentEvalEvaluator` already bundles metrics — adding batch semantics is straightforward."

**Q: "Can you show the deep path working with our workflows?"**
> "Yes. We have `MAFWorkflowAdapter.FromMAFWorkflow()` that takes a built `Workflow`, streams events via `InProcessExecution.RunStreamingAsync()`, and captures per-executor steps, edge traversals, tool calls, and graph structure. We handle the ChatProtocol detection and TurnToken sending. Happy to do a deeper demo."

**Q: "What about performance overhead of running 19 metrics?"**
> "The code-based metrics (tool selection, tool success, tool arguments, tool efficiency) are essentially free — pure in-memory computation. The LLM-as-judge metrics make API calls, so the cost is proportional to the number of LLM-evaluated metrics you select. That's why we have preset bundles — `.Quality()` is 4 metrics, `.Safety()` is 3. Users pick what they need. We also have `CalibratedEvaluator` that optimizes multi-judge costs."

---

## 19. Slide Image Prompts & Content Specifications

Use these prompts with an image generation tool (DALL-E, Midjourney, or a design tool like Figma/Canva). Each prompt is designed for a clean, professional, tech-presentation style.

---

### Slide 1 — Title: "MAF & AgentEval Integration"

**Image prompt:**

> A clean, modern tech presentation title slide. Two abstract geometric logos sit side by side in the center — one representing "Agent Framework" (a stylized interconnected node graph in Microsoft blue #0078D4) and one representing "AgentEval" (a stylized checkmark inside a hexagonal gauge in teal #00B294). Between them, a glowing bridge or connection line links the two logos, symbolizing integration. Background is dark navy (#1B1B2F) with subtle circuit-board-like traces fading into the edges. No text in the image — text will be overlaid.

**Text to overlay:**
```
AgentEval + Agent Framework
      Better Together

Advanced Evaluation for the .NET Agent Ecosystem

[Your name] — 2026-03-26
```

**Design notes:** Minimal, high-contrast, no clutter. The bridge visual between logos is the anchor — it communicates "integration" instantly.

---

### Slide 2 — Live Demo (code slide)

No image prompt needed — this is a code-on-dark-background slide. Use your IDE or a syntax-highlighted code block on dark theme. The code IS the visual.

---

### Slide 3 — Light Path vs. Deep Path Comparison

**Image prompt:**

> A professional infographic-style comparison visual for a tech presentation. Split vertically into two columns. LEFT column labeled "Light Path" shows a simple, clean pipeline: a document icon flowing through a single magnifying glass into a checkmark — representing post-mortem transcript evaluation. It should feel light, simple, fast. RIGHT column labeled "Deep Path" shows a rich, layered pipeline: a streaming data flow with multiple parallel tracks — a stopwatch (TTFT), a wrench with clock overlay (tool timing), a network graph (workflow), bar charts (statistics), and a shield (red teaming). The right side should feel deep, comprehensive, powerful. Use a consistent color scheme: left in light blue/silver tones, right in deep teal/gold tones. Dark background (#1B1B2F). No text labels in the image — text will be overlaid as a table.

**Table content to overlay on or below the image:**

| Capability | Light Path | Deep Path |
|:--|:-:|:-:|
| Quality metrics (text) | **Full** | **Full** |
| Safety metrics (text) | **Full** | **Full** |
| Tool call names & args | **Yes** | **Yes** |
| Tool call timing | -- | Per-tool start/end/duration |
| Time-to-first-token | -- | Via streaming |
| Token counts & cost | -- | Actual or estimated |
| Workflow graph | -- | Nodes, edges, traversal |
| Per-executor breakdown | -- | Steps, tools per executor |
| Stochastic (N runs + P90) | -- | Full statistics |
| Model comparison | -- | Rankings + winner |
| Red teaming | -- | 192 probes, OWASP/MITRE |
| Failure root cause | -- | FailureReport + suggestions |
| CI export | Portal only | JUnit, SARIF, PDF |

**Design notes:** The table should use green checkmarks for "Full"/"Yes", gray dashes for "--", and teal text for the deep path descriptions. Row striping for readability.

---

### Slide 4 — Architecture: Dual Path Diagram

**Image prompt:**

> A clean, modern software architecture diagram for a tech presentation on dark background (#1B1B2F). At the top, a wide rounded rectangle labeled "Developer Code" in white. Two paths descend from it: LEFT path goes through a box labeled "MAF Orchestration" (in Microsoft blue #0078D4) — this box is thin and simple, representing lightweight orchestration. RIGHT path goes through a box labeled "AgentEval Engine" (in teal #00B294) — this box is thicker and richer, with subtle icons inside representing streaming, graphs, and statistics. Both paths converge into a central diamond shape labeled "AgentEval IMetric — 19 metrics" in gold (#FFB900). Below the diamond, a final box labeled "Result Interop" shows two output arrows: one going left to "AgentEvaluationResults" and one going right to "JUnit / SARIF / PDF". Use clean connecting lines with arrowheads. Modern flat design, no 3D effects. Subtle glow on the convergence point to emphasize the shared metric layer.

**Text labels for the diagram (overlay or embedded):**

```
                    Developer Code
                   /              \
         Light Path                Deep Path
              |                        |
     MAF Orchestration          AgentEval Engine
     (runs agent,               (streaming, tool
      calls IEvaluator)          timelines, graphs)
              \                      /
               \                    /
            AgentEval IMetric Layer
             (19 metrics, shared)
                      |
              Result Interop
             /              \
  AgentEvaluationResults   JUnit/SARIF/PDF
  .AssertAllPassed()       CI/CD Export
```

**Design notes:** The key visual moment is the convergence — two paths, one metric layer. The left path should look simple/thin, the right path should look rich/layered. Both feed into the same core.

---

### Slide 5 — Win-Win Analysis

**Image prompt:**

> A professional presentation slide showing a win-win value exchange between two entities. On the LEFT side, a teal hexagonal badge labeled "AgentEval" with 3–4 small benefit icons flowing INTO it (integration, visibility, alignment, validation). On the RIGHT side, a blue hexagonal badge labeled "MAF" with 8–10 benefit icons flowing INTO it (metrics, safety, RAG, red team, memory, stochastic, comparison, export). The RIGHT side intentionally has MORE items flowing in — the visual weight should clearly show that MAF gets significant value. A golden handshake or bridge icon connects the two badges in the center. Dark background (#1B1B2F). Clean, infographic style. No text inside the image — text will be overlaid.

**Content to overlay (two-column layout):**

**AgentEval Gets:**
- First-class integration with .NET agent ecosystem
- Visibility to MAF's user base
- Alignment with Foundry evaluation pattern
- Deep path validated against real MAF workflows

**MAF Gets:**
- **+19 metrics** beyond MEAI's 6 (faithfulness, relevance, coherence, fluency, groundedness, task completion, tool selection, tool success, tool arguments, toxicity, bias, misinformation, context precision, context recall, answer correctness, and more)
- **LLM-as-judge evaluation** — no extra infrastructure
- **Agentic metrics** — tool selection, success, arguments
- **Safety metrics** — toxicity, bias, misinformation (not in Foundry)
- **RAG evaluation** — context precision/recall, answer correctness
- **Red teaming** — 192 probes, OWASP LLM Top 10, MITRE ATLAS
- **Memory evaluation** — benchmark suite for agents with memory
- **Stochastic analysis** — P90, std dev, success rate beyond `num_repetitions`
- **Model comparison** — side-by-side rankings with cost/latency
- **CI/CD export** — JUnit XML, SARIF (GitHub), PDF reports

**Design notes:** The asymmetry is intentional and powerful — AgentEval gets 4 things, MAF gets 10+. This visually communicates "this is not a balanced trade — MAF gets the bigger win." That's the right message.

---

### Slide 6 — Collaboration Asks

**Image prompt:**

> A professional presentation slide showing 5 numbered collaboration items as a visual roadmap. Style: clean cards or tiles arranged in a slight arc or staircase pattern, numbered 1–5. Each card has a small icon and a one-line label. Card 1: a box/package icon — "AgentEvaluationResults availability". Card 2: a handshake icon — "additionalContext protocol". Card 3: a scissors/split icon — "IConversationSplitter alignment". Card 4: a stopwatch icon — "Tool call timing in orchestration". Card 5: a git-merge/PR icon — "Upstream contributions". Each card has two small badges below it: one teal badge labeled "AgentEval" and one blue badge labeled "MAF", indicating who benefits. Cards 2, 3, 4, and 5 should show BOTH badges (mutual benefit). Card 1 shows only the teal badge (primarily AgentEval). Dark background (#1B1B2F). Modern flat design.

**Content for each card (overlay):**

| # | Ask | Benefits AgentEval | Benefits MAF |
|:-:|-----|:-:|:-:|
| 1 | `AgentEvaluationResults` type — is it public in RC? | Yes — needed for result interop | -- |
| 2 | Co-define `additionalContext` schema (ground truth, RAG context, expected tools) | Yes — enables RAG/agentic metrics through light path | **Yes — any evaluator can use it, not just AgentEval** |
| 3 | `IConversationSplitter` alignment (last-turn, full, per-turn) | Yes — interchangeable implementations | **Yes — consistent split behavior across all evaluators** |
| 4 | Tool call timing in MAF orchestration layer (Stopwatch around tool invocations) | Yes — makes agentic metrics richer through light path | **Yes — ALL evaluators get tool duration data, not just AgentEval** |
| 5 | Upstream PRs: ConversationExtractor, expected tool validation → MAF LocalEvaluator | Yes — code contribution visibility | **Yes — MAF's LocalEvaluator gets richer built-in utilities** |

**Key message:** 4 out of 5 asks benefit MAF directly. This is not a one-sided request.

---

### Slide 7 — Next Steps & Timeline

**Image prompt:**

> A clean horizontal timeline for a tech presentation. Three phases, left to right. PHASE 1 (leftmost, marked with a green checkmark): "Done" — a completed milestone marker. PHASE 2 (center, marked with a blue "now" dot): "Next 2 Weeks" — an active work zone with 3 small task cards below it. PHASE 3 (rightmost, marked with a teal future icon): "After" — a forward-looking zone with 2 task cards below it. The timeline line is a gradient from green (done) through blue (now) to teal (future). Each phase card has a subtle glow matching its phase color. Dark background (#1B1B2F). Modern, minimal, no clutter.

**Content to overlay:**

```
Phase 1: DONE ✅                Phase 2: NEXT 2 WEEKS              Phase 3: AFTER
─────────────────────────────────────────────────────────────────────────────────

Light path implemented          Result interop                      Upstream PRs to MAF
  AgentEval metrics as            TestResult ↔                        Tool call timing
  MEAI IEvaluator                 AgentEvaluationResults               ConversationExtractor
                                                                       EvalChecks utilities
                                Conversation split
                                  alignment with                    Deep path extensions
                                  ADR-0020 strategies                 with ADR-0020 concepts

                                additionalContext
                                  schema co-definition
                                  (with MAF team)
```

**Design notes:** The green/done phase should feel solid and stable. The blue/now phase should feel active and in-motion. The teal/future phase should feel open and aspirational. The visual tells the story: "we've already started, we have a plan, and the future is collaborative."

---

### General Slide Design Guidelines

- **Background:** Dark navy #1B1B2F across all slides for consistency
- **Primary text:** White #FFFFFF
- **AgentEval accent:** Teal #00B294
- **MAF/Microsoft accent:** Blue #0078D4
- **Highlight/emphasis:** Gold #FFB900
- **Font:** Segoe UI or Inter — clean, modern, Microsoft-ecosystem-native
- **Code blocks:** Use dark theme syntax highlighting (VS Code Dark+ or similar)
- **No decorative elements** — every visual element should communicate information
- **Slide count:** 7 slides for 12 minutes = ~1.7 min per slide average. Comfortable pace.
