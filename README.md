# AgentEval

**Make agent testing feel like normal .NET testing.**

AgentEval is a .NET-native testing, evaluation, and benchmarking toolkit for AI agents—built first for **Microsoft Agent Framework (MAF)**. It focuses on what agentic systems actually need: **tool-call visibility**, **streaming performance metrics (TTFT)**, **cost awareness**, **benchmarks**, and **run artifacts** you can inspect and “time-travel” during debugging.

> AgentEval turns agent runs into test artifacts—and agent expectations into assertions.

---

## Why AgentEval?

Traditional LLM eval tooling often answers: “Was the final response good?”  
AgentEval answers the questions engineering teams need to ship reliably:

- **What tools were called? In what order? With which arguments? Did they fail?**
- **How long did it take? What was TTFT? Which tool was slow?**
- **Did the agent stay within token/cost budgets?**
- **Did this PR regress behavior compared to baseline?**
- **Can we inspect exactly what happened without rerunning the agent?**

---

## Key Features

### ✅ Fluent assertions for agent behavior
- Tool usage assertions (called/not called, order, arguments, results, errors, duration)
- Response assertions (contains, patterns, length, etc.)
- Performance assertions (latency, TTFT, tokens, estimated cost, tool count)

### ✅ Streaming performance & observability
- Real-time metrics tracking (TTFT, total duration)
- Per-tool timing and execution waterfall data
- Designed to align with OpenTelemetry (OTel) workflows

### ✅ Benchmarks
- Latency / throughput / cost benchmarks
- Agentic benchmarks (tool accuracy, task completion, multi-step quality)
- Percentiles (p50/p90/p95/p99) and summary statistics

### ✅ Evaluation metrics
- RAG metrics (faithfulness, relevance, context precision/recall, correctness)
- Agentic metrics (tool selection, tool arguments, tool success, efficiency, task completion)

### ✅ Run artifacts (“time travel”)
Store run inputs/outputs, tool calls, timings, and scores as artifacts so failures are explainable and inspectable.

---

## Installation

> NuGet packages will be published soon. Until then, clone and reference the project locally.

Planned packages:
- `AgentEval` (core)
- `AgentEval.Maf` (MAF integration)
- `AgentEval.TestKit` (fixtures/builders/helpers)
- `AgentEval.Tracing` (OTel + run artifacts)
- `AgentEval.Studio` (future: workflow visualizer / time-travel UI)

---

## Quick Start (MAF)

```csharp
// Create a test harness (with optional evaluator client)
var harness = new MAFTestHarness(verbose: true);

// Wrap your MAF agent
var adapter = new MAFAgentAdapter(myAgent);

// Define a test case
var testCase = new TestCase
{
    Name = "Feature Planning Test",
    Input = "Plan a user authentication feature",
    EvaluationCriteria = new[] { "Should include security considerations" },
    ExpectedTools = new[] { "FeatureTool", "SecurityTool" },
    PassingScore = 70
};

// Run
var result = await harness.RunTestAsync(adapter, testCase, new TestOptions
{
    TrackTools = true,
    TrackPerformance = true,
    EvaluateResponse = true
});

// Assert tool behavior
result.ToolUsage!
    .Should()
    .HaveCalledTool("FeatureTool")
        .BeforeTool("SecurityTool")
    .And()
    .HaveNoErrors();

// Assert performance budgets
result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(2))
    .HaveEstimatedCostUnder(0.10m);
```
    
