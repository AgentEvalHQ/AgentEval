# Memory Evaluation

> **AgentEval.Memory** — the comprehensive .NET toolkit for evaluating an AI agent's memory: retention, recall depth, temporal reasoning, fact updates, cross-session persistence, and resistance to noise.

What [LongMemEval](https://github.com/xiaowu0162/LongMemEval) (ICLR 2025) does for Python research, AgentEval.Memory does for production .NET — plus a curated benchmark suite, an HTML reporting engine, baseline tracking, and a fluent assertion API.

> **Status:** ✅ Available in the current release. Ships in the `AgentEval` umbrella package as the `AgentEval.Memory` module.

---

## Why Memory Evaluation Matters

Modern AI agents promise to **remember** — across turns, across sessions, across days. But conversation history grows, context windows fill, reducers/compactors compress, and `AIContextProvider` implementations vary. The hard questions are:

- Does the agent still recall a fact mentioned 30 turns ago?
- Does it remember what was said *yesterday* after the session was reset?
- Does it abstain when it doesn't know — or hallucinate?
- Does it correctly resolve conflicting facts told minutes apart?
- Does it survive context-window compaction without losing critical state?

**AgentEval.Memory answers these — quantitatively, repeatedly, in CI.**

---

## What Ships

### 🧠 Five Memory Metrics

| Metric | What it measures |
|---|---|
| `MemoryRetentionMetric` | Can the agent recall a planted fact after intervening turns? |
| `MemoryReachBackMetric` | How far back can it reach as conversation depth grows? |
| `MemoryTemporalMetric` | Does it reason about *when* events happened, not just *what*? |
| `MemoryNoiseResilienceMetric` | Does it stay accurate when surrounded by distractor / red-herring turns? |
| `MemoryReducerFidelityMetric` | Does it survive context compaction / summarization without losing facts? |

All metrics use an **LLM judge** (`MemoryJudge`) with type-specific prompts (synthesis, counterfactual, correction-chain, specificity-attack, …) calibrated to match the LongMemEval methodology.

### 🏆 Curated Benchmark Suite

Run a one-line benchmark and get a **weighted score across multiple categories**:

```csharp
var runner = MemoryBenchmarkRunner.Create(chatClient);
var agent = chatClient.AsEvaluableAgent(name: "MemoryAgent", includeHistory: true);

var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard);
Console.WriteLine($"Memory score: {result.OverallScore:F1}% ({result.Grade})");
```

Three tiers + diagnostics:

| Preset | Categories | Use For |
|---|---|---|
| `MemoryBenchmark.Quick` | 3 (retention, temporal, noise) | Fast CI feedback |
| `MemoryBenchmark.Standard` | 8 (adds reach-back, fact-update, multi-topic, abstention, preference) | Daily quality gate |
| `MemoryBenchmark.Full` | 12 (adds cross-session, reducer fidelity, conflict resolution, multi-session reasoning) | Pre-release validation |
| `MemoryBenchmark.Diagnostic` | 12 + ~50K-token context pressure | Deep limits analysis |
| `MemoryBenchmark.Overflow` | 8 + 192K-token haystacks | Long-context stress |

### 📊 HTML Reporting Engine — Pentagon Comparison

The most spectacular piece. Run your benchmark, save a baseline, then **visually compare configurations and models** with overlaid pentagon charts:

```csharp
var store = new JsonFileBaselineStore();
await store.SaveAsync(result.ToBaseline(label: "GPT-4o-mini"));

// Generate an interactive HTML report comparing baselines
await result.ExportHtmlReportAsync("memory-report.html", new MemoryReportingOptions
{
    OverlayBaselines = await store.LoadAllAsync()
});
```

The report includes:
- **Pentagon overlay charts** — see strengths and gaps across categories
- **Per-category scores** with grades (A+ → F)
- **Baseline diffs** — what changed since the last run
- **Model comparison** — overlay GPT-4o-mini, GPT-4o, GPT-4.1 on the same chart
- **Drill-down** — failing scenarios, judge explanations, response excerpts

### 🌍 LongMemEval — First-Class .NET Re-implementation

The biggest research-grade memory benchmark, **fully re-implemented in .NET** with the official methodology preserved:

```csharp
var runner = LongMemEvalBenchmarkRunner.Create(chatClient, datasetPath);
var config = new AgentBenchmarkConfig { ConfigurationId = "my-agent", ModelId = "gpt-4o" };

var result = await runner.RunAsync(agent, config, new ExternalBenchmarkOptions
{
    MaxQuestions = 50,
    StratifiedSampling = true,         // proportional across all 6 question types
    PreserveSessionBoundaries = true,  // session markers in history
});

Console.WriteLine($"Score: {result.OverallAccuracy:F1}% (paper: GPT-4o = 57.7%)");
```

What's preserved from the official benchmark:
- **Stratified sampling** across all 6 question types (single-session, multi-session, temporal, knowledge-update, abstention, preference)
- **Type-specific judge prompts** matching the official evaluation
- **Session boundary + timestamp preservation** in history injection
- **Binary scoring** (0/1) comparable to published results
- **2 LLM calls per question** (query + judge) via history injection

Sample [G8: LongMemEvalBenchmark](../samples/AgentEval.Samples/MemoryEvaluation/07_LongMemEvalBenchmark.cs) and [G10: LongMemEvalBaselineRepro](../samples/AgentEval.Samples/MemoryEvaluation/10_LongMemEvalBaselineRepro.cs) reproduce the GPT-4o paper baseline.

### ✍️ Fluent Memory Assertions

```csharp
result.Should()
    .HaveRetentionAbove(80, because: "agent must recall planted facts")
    .HaveTemporalReasoningAbove(70)
    .HaveNoHallucinations()
    .HavePassedCategory("Cross-Session", because: "long-term memory required");

await agent.CanRememberAsync("My favorite language is C#")
    .Should().BeTrue();
```

### 🔌 Production DI Wiring

```csharp
services.AddAgentEvalAll();          // includes Memory
// or selectively:
services.AddAgentEvalMemory();       // metrics + scenarios + reporting + temporal + evaluators

public class MemoryHealthCheck(IMemoryBenchmarkRunner runner) { /* … */ }
```

### 🔗 MAF Pipeline Compatibility

AgentEval.Memory works **without modification** with MAF 1.3.0's pipeline (`ChatHistoryProvider`, `AIContextProvider`, `CompactionStrategy`). It evaluates *behavior*, not *mechanism*. See [MAF Memory Integration](maf-memory-integration.md) for the concept-mapping table.

---

## Honest Caveats

We hold ourselves to the same evaluation rigor we ship. A few candid notes:

### ⚠️ The Native Benchmark Currently Scores High

Our curated `Standard` benchmark scores roughly **88–93% on GPT-4.1** in our reference runs. Strong models clear it comfortably — which is **less useful as a discriminator** than we want it to be.

**Why:** the native scenarios were initially designed to test *retrieval* (find a fact, return it) more than *reasoning* (synthesise fragments across sessions, resolve conflicts, infer unstated conclusions). Strong base models retrieve very well.

**What we recommend today:**
- Use the native benchmark as a **regression gate** — track your own delta over time, not the absolute number.
- Use **LongMemEval** (Sample G8 / G10) for **cross-platform comparable** numbers — it's calibrated to the published GPT-4o = 57.7% baseline.
- Use the **Diagnostic** and **Overflow** presets to apply real context pressure.

**Current limitation:** the native benchmark is better suited to regression tracking than fine-grained model differentiation, especially when stronger models can solve many scenarios through retrieval alone.

### ⚠️ Memory Evaluation Always Calls a Real LLM

The MemoryJudge **cannot run in mock mode** — there is no shortcut to "did the agent really remember." Sample G samples gracefully skip when credentials are missing.

### ⚠️ LongMemEval Dataset Is External

The dataset is **not redistributed** with AgentEval (license / size). Download it from [HuggingFace](https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned) and place it in `src/AgentEval.Memory/Data/longmemeval/`. Sample G8 prints the link if missing.

---

## Crafting Your Own Memory Evaluation

The benchmark presets are the curated fast path. For domain-specific memory (medical histories, financial preferences, support-ticket continuity, …) **build your own scenarios**:

1. **Define facts** — `MemoryFact.Create("user prefers metric units")`
2. **Define queries** — `MemoryQuery.Create("what units should I use?", expectedFact)`
3. **Drive a runner** — `MemoryTestRunner` interleaves facts with optional noise turns and asks the queries
4. **Add a judge variant** if your domain needs a custom rubric — `MemoryJudge` is extensible

See [Sample G3 — Memory Scenarios](../samples/AgentEval.Samples/MemoryEvaluation/03_MemoryScenariosDemo.cs) for `ReachBackEvaluator` + `ReducerEvaluator`, and [Sample G6 — AIContextProvider Memory](../samples/AgentEval.Samples/MemoryEvaluation/09_MemoryAIContextProvider.cs) for MAF-pipeline-native memory.

---

## Samples

| # | Sample | What you'll learn |
|---|---|---|
| G1 | [Memory Basics](../samples/AgentEval.Samples/MemoryEvaluation/01_MemoryBasics.cs) | `MemoryJudge`, `MemoryTestRunner`, fluent assertions |
| G2 | [Memory Benchmark Demo](../samples/AgentEval.Samples/MemoryEvaluation/02_MemoryBenchmarkDemo.cs) | Quick / Standard / Full presets with grades |
| G3 | [Memory Scenarios](../samples/AgentEval.Samples/MemoryEvaluation/03_MemoryScenariosDemo.cs) | `ReachBackEvaluator`, `ReducerEvaluator` |
| G4 | [Memory DI](../samples/AgentEval.Samples/MemoryEvaluation/04_MemoryDI.cs) | `AddAgentEvalMemory()`, `CanRememberAsync()` |
| G5 | [Cross-Session Memory](../samples/AgentEval.Samples/MemoryEvaluation/05_MemoryCrossSession.cs) | Fact persistence across session resets |
| G6 | [Benchmark Reporting](../samples/AgentEval.Samples/MemoryEvaluation/06_MemoryBenchmarkReporting.cs) | Multi-model HTML pentagon report |
| G7 | [LongMemEval Benchmark](../samples/AgentEval.Samples/MemoryEvaluation/07_LongMemEvalBenchmark.cs) | Cross-platform research-grade evaluation |
| G8 | [Run Single Benchmark](../samples/AgentEval.Samples/MemoryEvaluation/08_RunSingleBenchmark.cs) | Pick a preset, save a baseline, view report |
| G9 | [AIContextProvider Memory](../samples/AgentEval.Samples/MemoryEvaluation/09_MemoryAIContextProvider.cs) | MAF-native pipeline memory |
| G10 | [LongMemEval Baseline Repro](../samples/AgentEval.Samples/MemoryEvaluation/10_LongMemEvalBaselineRepro.cs) | Reproduce the GPT-4o paper baseline |

---

## See Also

- [MAF Memory Integration](maf-memory-integration.md) — how AgentEval.Memory maps to MAF 1.3.0's pipeline (`AIContextProvider`, `CompactionStrategy`, `ChatHistoryProvider`)
- [Upgrading MAF](maf-1.3.0-migration-guide.md) — the complete MAF 1.3.0 migration guide
- [Architecture](architecture.md) — where the Memory module fits
- [Naming Conventions](naming-conventions.md) — `llm_*` / `code_*` / `embed_*` metric prefixes

---

<p align="center"><em>Don't ship memory you can't measure.</em></p>
