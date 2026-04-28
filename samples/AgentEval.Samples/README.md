# AgentEval Samples

> **41 focused, educational samples — browse by group, get started in 5 minutes.**

## Core Principle

**"Evaluation Always Real, Structure Optionally Mock"**

- **Evaluation** (LLM-as-judge scores, metrics) → always real or gracefully skipped
- **Structure** (tool ordering, workflows, conversations) → can be demonstrated with mock data

Group A (samples A1–A5, except A6 Session Lifecycle) and Dataset Loaders / Extensibility in Group F run fully without credentials.
All other samples require Azure OpenAI for meaningful results.

---

## Quick Start

```bash
cd samples/AgentEval.Samples
dotnet run
```

The interactive menu organises samples into **7 groups**. Select a group letter, then a sample number.
You can also run a specific sample directly from the command line (legacy numbering 1–41):

```bash
dotnet run -- 1    # Hello World
dotnet run -- 23   # Red Team Basic
```

---

## Sample Groups

### A — Getting Started  ★ mostly no credentials needed

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Hello World** | Basic test setup, TestCase, TestResult, pass/fail | No | 2 min |
| 2 | **Agent + One Tool** | Tool tracking, fluent assertions (`HaveCalledTool`, `WithoutError`) | No | 5 min |
| 3 | **Agent + Multiple Tools** | Tool ordering (`BeforeTool`/`AfterTool`), visual timeline | No | 7 min |
| 4 | **Performance Metrics** | Latency, cost, TTFT, token budget — basic assertions | No | 5 min |
| 5 | **Light Path (MEAI)** | AgentEval as MEAI `IEvaluator` — plug into MAF's evaluation pipeline | Yes | 5 min |
| 6 | **Session Lifecycle** | MAF `AgentSession`: create → multi-turn → reset → isolation | Yes | 8 min |
| 7 | **Advanced MAF Features** | ChatHistory, middleware, structured output, approval, agent-as-tool | Yes | 10 min |

### B — Metrics & Quality

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Comprehensive RAG** | Build & evaluate a full RAG system — 8 metrics + IR metrics ⭐ | Yes + Embed | 15 min |
| 2 | **Quality & Safety Metrics** | Groundedness, Coherence, Fluency beyond RAG accuracy | Yes | 5 min |
| 3 | **Judge Calibration** | Multi-model consensus voting (Median, Mean, Weighted) | Yes ×3 | 8 min |
| 4 | **Responsible AI** | Toxicity, bias, misinformation with counterfactual testing 🛡️ | Yes | 5 min |
| 5 | **Calibrated Evaluator** | Drop-in `IEvaluator` with per-criterion majority voting | Yes | 5 min |

### C — Workflows & Conversations

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Conversation Evaluation** | Multi-turn testing, `ConversationRunner`, fluent builder API | Yes | 5 min |
| 2 | **Real MAF Workflow** | `WorkflowBuilder` + `InProcessExecution`: 4-agent pipeline ⭐ | Yes | 15 min |
| 3 | **Workflow + Tools** | TripPlanner pipeline: 4 agents with tool call tracking ⭐ | Yes | 15 min |
| 4 | **[MessageHandler] Executors** | Source-gen executor pipeline — deterministic, no LLM, AOT-ready | No | 8 min |

### D — Performance & Statistics

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Performance Profiling** | Real latency: p50 / p90 / p99 percentiles, tool accuracy | Yes | 5 min |
| 2 | **Stochastic Evaluation** | Run N times — assert on pass rate, not single pass/fail | Yes | 5 min |
| 3 | **Model Comparison** | Compare & rank 3 models on quality, speed, cost, reliability | Yes ×3 | 10 min |
| 4 | **Stochastic + Comparison** | Statistical rigor applied to side-by-side model comparison | Yes ×2 | 10 min |
| 5 | **Streaming vs Async** | TTFT vs throughput — compare streaming and non-streaming | Yes | 8 min |

### E — Safety & Security

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Policy & Safety** | Enterprise guardrails — `NeverCallTool`, PII detection, `MustConfirmBefore` 🛡️ | Yes | 8 min |
| 2 | **Red Team Basic** | One-liner security scan — 9 attack types, OWASP probes 🛡️ | Yes | 5 min |
| 3 | **Red Team Advanced** | Custom pipeline, OWASP compliance, PDF export, baseline tracking 🛡️ | Yes | 10 min |

### F — Data & Infrastructure

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Snapshot Testing** | Regression detection — JSON diff, field scrubbing, semantic tolerance | Yes | 5 min |
| 2 | **Datasets & Export** | Batch evaluation: YAML datasets → JUnit / Markdown / JSON / TRX | Yes | 7 min |
| 3 | **Trace Record & Replay** | Capture executions to JSON, replay deterministically | Yes | 10 min |
| 4 | **Benchmark System** | JSONL-loaded tool-accuracy benchmarks (BFCL, GAIA-style) ⭐ | Yes | 5 min |
| 5 | **Dataset Loaders** | Multi-format auto-detection: JSONL, JSON, YAML, CSV | No | 5 min |
| 6 | **Extensibility** | DI registries — custom metrics, exporters, loaders, attacks 🔌 | No* | 3 min |
| 7 | **Cross-Framework** | Universal `IChatClient.AsEvaluableAgent()` for any AI provider | Yes | 3 min |

> *Steps 1–6 run offline; Step 7 (optional live LLM demo) requires Azure credentials.

### G — Memory Evaluation

| # | Sample | What You'll Learn | Azure? | Time |
|---|--------|-------------------|--------|------|
| 1 | **Memory Basics** | Test if agents remember facts — `MemoryJudge`, fluent assertions | Yes | 5 min |
| 2 | **Memory Benchmark** | Comprehensive memory scoring — Quick / Standard / Full tiers with grades | Yes | 8 min |
| 3 | **Memory Scenarios** | `ReachBackEvaluator` (recall depth), `ReducerEvaluator` (compression) | Yes | 8 min |
| 4 | **Memory DI** | Production DI wiring — `AddAgentEvalMemory()`, `CanRememberAsync()` | Yes | 5 min |
| 5 | **Cross-Session Memory** | Fact persistence across session resets — compare with / without memory | Yes | 8 min |
| 6 | **AIContextProvider Memory** | MAF-native memory pipeline — `AIContextProvider` + cross-session evaluation | Yes | 8 min |
| 7 | **Benchmark Reporting** | Multi-model comparison + interactive HTML pentagon report | Yes ×3 | 15 min |
| 8 | **LongMemEval Benchmark** | Cross-platform research-grade eval — ICLR 2025, MIT-licensed dataset | Yes | 15 min |
| 9 | **Run Single Benchmark** | Pick Quick/Standard/Full, run, save baseline, view report | Yes | 8 min |
| 10 | **LongMemEval Baseline Repro** | Reproduce the GPT-4o paper baseline (TextBlob mode) | Yes | 20 min |

---

## Prerequisites

### With Azure OpenAI (full experience)

```powershell
# PowerShell
$env:AZURE_OPENAI_ENDPOINT   = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY    = "your-api-key"
$env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"

# Optional: embedding-based metrics (B1 — Comprehensive RAG)
$env:AZURE_OPENAI_EMBEDDING_DEPLOYMENT = "text-embedding-ada-002"

# Optional: multi-model samples (B3 Judge Calibration, D3 Model Comparison, D4 Stochastic+Comparison)
$env:AZURE_OPENAI_DEPLOYMENT_2 = "gpt-4o-mini"
$env:AZURE_OPENAI_DEPLOYMENT_3 = "gpt-4.1"
```

```bash
# Bash / Linux / macOS
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-api-key"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"
```

### Without Azure (mock mode — Group A)

Samples in Group A work fully without credentials. You'll see:

```
╔══════════════════════════════════════════════════════════════╗
║  ⚠️  Azure OpenAI credentials not configured                  ║
║  All samples will run in MOCK MODE without real AI.          ║
╚══════════════════════════════════════════════════════════════╝
```

Samples requiring credentials show a skip banner and return gracefully.

---

## Selected Code Highlights

### Tool chain assertion (A2 / A3)
```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights", because: "must search before booking")
        .WithArgument("destination", "Paris")
    .And()
    .HaveCalledTool("BookFlight")
        .AfterTool("SearchFlights")
    .HaveNoErrors();
```

### Performance SLA (A4 / D1)
```csharp
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500))
    .HaveEstimatedCostUnder(0.05m)
    .HaveTokenCountUnder(2000);
```

### RAG evaluation — 8 metrics (B1)
```csharp
var llmMetrics = new IMetric[]
{
    new FaithfulnessMetric(client),      // no hallucinations
    new RelevanceMetric(client),          // addresses the question
    new ContextPrecisionMetric(client),   // retrieved context is useful
    new ContextRecallMetric(client),      // all needed context retrieved
    new AnswerCorrectnessMetric(client),  // matches ground truth
};
// + 3 embedding metrics (10–100× cheaper) + 2 IR metrics (free)
```

### Stochastic evaluation (D2)
```csharp
var result = await runner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.85));
result.Statistics.Mean.Should().BeGreaterThan(80);          // avg quality
result.Statistics.StandardDeviation.Should().BeLessThan(10); // consistency
```

### Policy guardrails (E1)
```csharp
result.ToolUsage!.Should()
    .NeverCallTool("DeleteAccount")
    .NeverPassArgumentMatching("ssn", @"\d{3}-\d{2}-\d{4}")
    .MustConfirmBefore("TransferFunds");
```

### Red Team (E2 / E3)
```csharp
var result = await agent.RedTeamAsync(new ScanOptions { Intensity = Intensity.Quick });
result.Should()
    .HavePassed()
    .And().HaveMinimumScore(80)
    .And().HaveASRBelow(0.05);
```

### Memory evaluation (G1 / G2)
```csharp
var result = await runner.RunMemoryBenchmarkAsync(agent, MemoryBenchmark.Standard);
result.Should()
    .HaveOverallScoreAtLeast(70)
    .HaveAllQueriesPassed()
    .NotHaveRecalledForbiddenFacts();
```

---

## Cost Optimisation Reference

| Metric type | Cost / eval | Latency | Best for |
|-------------|-------------|---------|----------|
| LLM-based   | ~$0.01      | 2–5 s   | Quality gates, pre-prod |
| Embedding   | ~$0.0001    | ~0.1 s  | Dev / CI, scale testing |
| Code-based  | FREE        | ~1 ms   | Retrieval tuning |

---

## Key Concepts

**TestCase** — defines what to test: `Name`, `Input`, `ExpectedOutputContains`, `EvaluationCriteria`, `ExpectedTools`.

**TestResult** — what you get back: `Passed`, `Score`, `ToolUsage`, `Performance`, `Failure`.

**Fluent Assertions** — natural-language API:
```csharp
result.ToolUsage.Should().HaveCalledTool("X").BeforeTool("Y").WithoutError();
result.Performance.Should().HaveTotalDurationUnder(TimeSpan.FromSeconds(5));
```

---

## Next Steps

1. Run Group A (no credentials) to understand the core API
2. Copy patterns into your own test project
3. See [docs/](../../docs/) for the full API reference
4. See [AgentEval.Tests](../../tests/AgentEval.Tests/) for more examples

---

**Happy Evaluating!** 🎉
