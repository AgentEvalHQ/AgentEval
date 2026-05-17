# Benchmarks Guide

> **Running industry-standard AI benchmarks and creating custom benchmark suites with AgentEval**

AgentEval ships **four benchmark families**, each with its own getting-started + plain-English explainer:

| Family | What it measures | CLI | Docs |
|---|---|---|---|
| **Agentic** | Broad agent quality across ~60 evaluators organised into ~12 categories (task completion, tool-call accuracy, RAG, safety, memory, reasoning, …) — 11 named presets | `agenteval bench agentic --preset X` | [getting-started](benchmarks/agentic/getting-started.md) · [how-it-works](benchmarks/agentic/how-it-works.md) · [cost guidance](benchmarks/agentic/cost-guidance.md) · [evaluator cards](benchmarks/agentic/evaluator-cards.md) |
| **GDPR** | Dialog-level conformance across the GDPR articles relevant to agent interaction, in 5 pillars; healthcare/HR/childrens domain packs | `agenteval bench gdpr --preset X` | [getting-started](benchmarks/gdpr/getting-started.md) · [how-it-works](benchmarks/gdpr/how-it-works.md) |
| **EU AI Act** | Dialog-level conformance against Regulation (EU) 2024/1689 controls, in 6 pillars; high-risk employment/credit/education domain packs | `agenteval bench eu-ai-act --preset X` | [getting-started](benchmarks/eu-ai-act/getting-started.md) · [how-it-works](benchmarks/eu-ai-act/how-it-works.md) |
| **Performance** | Latency percentiles, throughput, estimated cost — in-process library API in `AgentEval.Benchmarks.PerformanceBenchmark` | n/a (library API) | This page, below |

The three benchmark suites in the top three rows are **preset-factory based**: a factory method returns a `CompositeEval` configured with canonical evaluator weights, which the CLI executes against a subject + scenario set, persisting audit-chain-validated evidence to `.agenteval/`. The same factory methods are publicly callable from any consumer that links the corresponding assembly.

> **v0.9.0-beta breaking change.** The earlier in-process library-API benchmark surface — `AgentEval.Benchmarks.AgenticBenchmark` with `ToolAccuracyTestCase` / `TaskCompletionTestCase` / `MultiStepTestCase` plus `RunToolAccuracyBenchmarkAsync` / `RunTaskCompletionBenchmarkAsync` / `RunMultiStepReasoningBenchmarkAsync` — has been **removed**. Migrate to the agentic preset-factory API or the CLI; both ship strictly more capable replacements (60 evaluators vs the legacy 3 fixed methods, calibration, audit chain). See [the migration note in `CHANGELOG.md`](../CHANGELOG.md) for details.

---

## Quick Start — agentic via CLI

The fastest path to a working benchmark:

```bash
agenteval init --name MySolution
agenteval bench agentic --preset agentic-execution --subject MyAgent
```

This writes evidence to `.agenteval/compliance/agentic/MyAgent/<timestamp>/` (markdown, PDF, JSON, audit-chain-validated). The [agentic getting-started](benchmarks/agentic/getting-started.md) covers every preset.

## Quick Start — agentic programmatically

Each preset factory returns a `CompositeEval` that you can evaluate directly:

```csharp
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;       // AgenticBenchmark preset factory

IEvaluator judge = /* your IEvaluator — e.g. ChatClientEvaluator wrapping IChatClient */;

CompositeEval preset = AgenticBenchmark.ToolCallAccuracy(judge, judgeModel: "gpt-4o");

var input = new EvalInput(
    Query: "What's the weather in Paris?",
    Response: agentResponseText);   // optionally also pass tool-trace metadata

EvalResult result = await preset.EvaluateAsync(input);

Console.WriteLine($"Score: {result.Score.Value:F2}  Verdict: {result.Score.Label}");
```

For the full pipeline (run against many scenarios, persist evidence, walk the composite tree), see `AgenticBenchmarkRunner` in `AgentEval.Evals.Agentic.Composition` — or use the CLI which does all of that for you.

## Available agentic presets

| Preset | CLI name | Use case |
|---|---|---|
| `AgenticBenchmark.AgenticExecution(judge)` | `agentic-execution` | Standard 6-evaluator agent quality gate |
| `AgenticBenchmark.ToolCallAccuracy(judge)` | `tool-call-accuracy` | Focused 5-sub-dimension tool-call diagnostic |
| `AgenticBenchmark.RagQuality(judge)` | `rag-quality` | 7-evaluator RAG pipeline quality |
| `AgenticBenchmark.JudgeQuality()` | `judge-quality` | Meta-evaluation of judge health (no LLM needed) |
| `AgenticBenchmark.Safety(judge, policyResolver, …)` | `safety` | 12-evaluator safety/security gate |
| `AgenticBenchmark.Telemetry()` | `telemetry` | 6 pure-code operational evaluators |
| `AgenticBenchmark.StochasticStability()` | `stochastic-stability` | Run-to-run consistency |
| `AgenticBenchmark.Conversational(judge)` | `conversational` | Memory + multi-turn quality |
| `AgenticBenchmark.Reasoning(judge)` | `reasoning` | Reasoning chain quality |
| `AgenticBenchmark.UserExperience(judge)` | `user-experience` | UX/communication quality |
| `AgenticBenchmark.AdversarialDirect(judge)` | `adversarial-direct` | Direct adversarial resistance gate |

Full preset reference + cost tiers: [agentic getting-started.md](benchmarks/agentic/getting-started.md#preset-reference).

---

## Performance Benchmark (in-process)

`AgentEval.Benchmarks.PerformanceBenchmark` (in `AgentEval.Core`) measures latency percentiles, throughput, and estimated cost without LLM judging. Useful for capacity planning and regression detection on hot paths.

```csharp
using AgentEval.Benchmarks;

var bench = new PerformanceBenchmark(adapter);
var result = await bench.MeasureLatencyAsync(prompts, runsPerPrompt: 5);

Console.WriteLine($"P50: {result.P50Ms} ms");
Console.WriteLine($"P99: {result.P99Ms} ms");
Console.WriteLine($"Throughput: {result.RequestsPerSecond:F1} req/s");
```

PerformanceBenchmark is a pure-code measurement layer — no LLM judge involved. It pairs naturally with the agentic suite's `telemetry` preset, which provides the equivalent measurements wrapped in the standard `EvalResult` envelope with budget thresholds.

---

## JSONL dataset loading

Both the agentic suite and PerformanceBenchmark accept prompts loaded from JSONL files via `DatasetLoaderFactory`. JSONL is the industry-standard format for AI benchmark datasets (used by BFCL, GAIA, MMLU, GSM8K, ToolBench, etc.).

```csharp
using AgentEval.DataLoaders;

var dataset = await DatasetLoaderFactory.LoadAsync("samples/datasets/benchmark-tool-accuracy.jsonl");
var prompts = dataset.Select(dc => dc.Input).ToList();
```

A working end-to-end sample lives at [`samples/AgentEval.Samples/DataAndInfrastructure/04_BenchmarkSystem.cs`](../samples/AgentEval.Samples/DataAndInfrastructure/04_BenchmarkSystem.cs) — it loads prompts from JSONL, runs an agent against each, and evaluates the responses with `AgenticBenchmark.ToolCallAccuracy(judge)`.

---

## Custom presets

To build your own preset composite:

```csharp
using AgentEval.Core;
using AgentEval.Evals;

var custom = new CompositeEval(
    key: "my.custom.preset",
    name: "My Custom Preset",
    category: "custom",
    version: "1.0.0",
    components:
    [
        new(AgenticBenchmark.ToolCallAccuracy(judge), 0.6),
        new(AgenticBenchmark.RagQuality(judge), 0.4),
    ],
    aggregation: WeightedSumAggregation.Instance,
    threshold: 0.75);
```

This combines two presets with custom weights. The result is a standard `CompositeEval` that runs through the same runner / reporter / audit-chain pipeline as the built-in presets.

---

## See Also

- [Composite Evaluations](composite-evals.md) — the underlying `CompositeEval` / `AtomicLlmEval` / `AtomicCodeEval` primitives.
- [CLI Reference](cli.md) — `agenteval bench {agentic,gdpr,eu-ai-act}` and their `calibrate` subcommands.
- [The `.agenteval/` Workspace](agenteval-workspace.md) — canonical layout, schema versions, audit chain.
- [Evaluation Guide](evaluation-guide.md) — overall framework concepts.
