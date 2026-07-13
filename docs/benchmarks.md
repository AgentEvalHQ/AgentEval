# Benchmarks Guide

> **Running industry-standard AI benchmarks and creating custom benchmark suites with AgentEval**

AgentEval ships **11 benchmark families**, each registered in `BenchmarkFamilyRegistry` (`agenteval bench --list` enumerates them) with its own getting-started + plain-English explainer:

| Family | What it measures | CLI | Docs |
|---|---|---|---|
| **Agentic** | Broad agent quality across ~60 evaluators organised into ~12 categories (task completion, tool-call accuracy, RAG, safety, memory, reasoning, …) — 11 named presets | `agenteval bench agentic --preset X` | [getting-started](benchmarks/agentic/getting-started.md) · [how-it-works](benchmarks/agentic/how-it-works.md) · [cost guidance](benchmarks/agentic/cost-guidance.md) · [evaluator cards](benchmarks/agentic/evaluator-cards.md) |
| **GDPR** | Dialog-level conformance across the GDPR articles relevant to agent interaction, in 5 pillars; healthcare/HR/childrens domain packs | `agenteval bench gdpr --preset X` | [getting-started](benchmarks/gdpr/getting-started.md) · [how-it-works](benchmarks/gdpr/how-it-works.md) |
| **EU AI Act** | Dialog-level conformance against Regulation (EU) 2024/1689 controls, in 6 pillars; high-risk employment/credit/education domain packs | `agenteval bench eu-ai-act --preset X` | [getting-started](benchmarks/eu-ai-act/getting-started.md) · [how-it-works](benchmarks/eu-ai-act/how-it-works.md) |
| **OWASP LLM Top 10** | Red-team scan against all 10 OWASP LLM Top 10 v2.0 categories via 13 built-in attack types; stub agent by default, or your real agent via `--azure-from-env` | `agenteval bench owasp --preset X` | [getting-started](benchmarks/owasp/getting-started.md) |
| **MITRE ATLAS** | The same 13 attacks mapped via `IAttackType.MitreAtlasIds` to 8 applicable ATLAS techniques | `agenteval bench mitre --preset X` | [getting-started](benchmarks/mitre/getting-started.md) |
| **NIST AI RMF** | The same 13 attacks mapped to NIST AI RMF (AI 100-1) MEASURE security/privacy/validity sub-actions (GOVERN/MAP/MANAGE not applicable) | `agenteval bench nist --preset X` | [redteam guide](redteam.md) · [CLI reference](cli.md#agenteval-bench) |
| **LongMemEval** | Cross-platform long-horizon memory benchmark (ICLR 2025) — paper-published GPT-4o baseline ≈ 57.7% | `agenteval bench longmemeval --preset X` | [getting-started](benchmarks/longmemeval/getting-started.md) |
| **Memory** | Native AgentEval memory benchmark across 3/8/12 categories with weighted grading | `agenteval bench memory --preset X` | [getting-started](benchmarks/memory/getting-started.md) |
| **Performance** | Latency percentiles, throughput, estimated cost — CLI benchmark, or in-process via the `AgentEval.Benchmarks.PerformanceBenchmark` library API | `agenteval bench perf {latency,throughput,cost} --subject X` | [getting-started](benchmarks/perf/getting-started.md) · this page, below (library API) |
| **Trace Fidelity** | Agent-boundary vs chat-boundary trace reconciliation — missing/phantom tool calls, hidden retries, argument drift, token under-reporting, suppressed finish reasons; pure code, no LLM tokens | `agenteval bench trace-fidelity --agent-trace X --chat-trace Y` | [trace-fidelity.md](benchmarks/trace-fidelity.md) |
| **Workflow Trace Fidelity** | Per-executor workflow ledger (tokens + finish reason) vs chat-boundary truth; pure code, no LLM tokens | `agenteval bench workflow-trace-fidelity --workflow-trace X` | [workflow-trace-fidelity.md](benchmarks/workflow-trace-fidelity.md) |

Agentic, GDPR, and EU AI Act are **preset-factory based**: a factory method returns a `CompositeEval` configured with canonical evaluator weights, which the CLI executes against a subject + scenario set, persisting audit-chain-validated evidence to `.agenteval/`. The same factory methods are publicly callable from any consumer that links the corresponding assembly. OWASP, MITRE, and NIST AI RMF each run the same 13 built-in red-team attacks through a family-specific compliance crosswalk; LongMemEval, Memory, Trace Fidelity, and Workflow Trace Fidelity register as Shape-B custom runners (see [`docs/architecture.md` — Benchmark family registration](architecture.md#benchmark-family-registration)) because their natural result types don't fit the single-shot `EvalInput → EvalResult` envelope.

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
- [CLI Reference](cli.md) — `agenteval bench {agentic,gdpr,eu-ai-act}` and their `calibrate` subcommands, plus `{owasp,mitre,nist,longmemeval,memory,perf,trace-fidelity,workflow-trace-fidelity}`.
- [The `.agenteval/` Workspace](agenteval-workspace.md) — canonical layout, schema versions, audit chain.
- [Evaluation Guide](evaluation-guide.md) — overall framework concepts.
