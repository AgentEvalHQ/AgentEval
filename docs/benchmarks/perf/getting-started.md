# Performance Benchmark — Getting Started

> Status: beta. The performance benchmark measures agent runtime characteristics across three telemetry dimensions: P99 latency, throughput (requests-per-second), and per-call cost. It is a runtime-observability tool, not a load-testing or capacity-planning replacement.
>
> Coverage: 3 sub-presets — `latency` (P99 / P90 / P50 latency + mean TTFT), `throughput` (concurrent RPS over a sampling window), `cost` (per-prompt token + USD cost estimate against the pricing table). Not covered: memory pressure, GC pause analysis, cold-start latency, sustained-load endurance, multi-region latency, or any system-level resource accounting outside the agent invocation.

## What this measures

The performance benchmark exercises the agent under test via direct `IEvaluableAgent.InvokeAsync` calls and records timing + token-usage telemetry. The `EvaluateAsync` adapter (Convention 2) runs all three measurements (latency, throughput, cost) and aggregates them into a 3-leaf composite `EvalResult` via `CapByWorst` — a single high-severity leaf caps the composite.

What IS tested: per-call latency (P50 / P90 / P99 + mean, time-to-first-token when the agent implements `IStreamableAgent`), sustained throughput under a configurable concurrent-worker pool, and per-prompt cost based on the `ModelPricing` table. What is NOT tested: process-level memory pressure, GC pause durations, cold-start latency on fresh process spawn, long-tail endurance under sustained load (>15s), network-egress costs, multi-region latency variance, or anything outside the agent invocation boundary (HTTP / Azure SDK / connection pool internals all fall under the per-call latency number but cannot be decomposed by this benchmark).

## Scope and omissions

- Covered (with rationale per item):
  - P50 / P90 / P99 latency — primary tail-latency signal for SLO conformance.
  - Mean time-to-first-token (TTFT) — when the agent implements `IStreamableAgent`, gives perceived-latency signal.
  - Requests-per-second under a concurrent worker pool — sustained-throughput signal over a default 5-second window.
  - Per-prompt token usage + USD cost estimate against the pricing table — cost-per-call signal.
  - Composite `CapByWorst` aggregation — any high/critical-severity leaf caps the composite verdict.
- Out of scope (with rationale):
  - Memory pressure / working-set growth — needs process-level telemetry the agent harness does not expose.
  - GC pause analysis — same.
  - Cold-start latency — the benchmark warms the agent before timed runs; cold-start measurement requires fresh process spawn per measurement.
  - Sustained-load endurance (>15s) — out of scope for a CLI-driven smoke; use a dedicated load tool (k6, NBomber, JMeter) for that.
  - Multi-region latency variance — single-process invocation only.
  - Network egress accounting — the USD-cost estimate covers LLM tokens only; egress to / from your Azure tenant is not tracked.

## Presets

Sourced verbatim from `BenchmarkFamilyRegistry` (see `src/AgentEval.Evals.Performance/PerformanceBenchmarkRegistration.cs:45-50`).

| Preset | Description (verbatim) | Cost tier | Typical iterations | Approx. LLM cost |
|---|---|---|---|---|
| `latency` | P99 latency measurement (3 iterations x N prompts + warmup) | Low | 1 prompt x 3 iterations + 1 warmup = 4 calls | telemetry-only (~$0.001 per call against gpt-4o-mini if using `--azure-from-env`; default EchoAgent stub: free) |
| `throughput` | Concurrent throughput measurement (default 2 workers x 5s) | Low | 2 concurrent workers x ~5s window (typically 5-20 calls depending on agent speed) | telemetry-only |
| `cost` | Per-prompt token + cost estimate (pricing-table-backed) | Low | 1 call per supplied prompt | telemetry-only |

Default thresholds (overridable via `PerformanceBenchmarkEvaluateOptions`):
- P99 latency threshold: 5000 ms → score = 1 - (p99ms / 5000), clamped [0, 1].
- Minimum throughput: 0.5 RPS → score = min(rps / 0.5, 1.0).
- Maximum cost: $0.10 USD → score = 1 - (cost / 0.10), clamped [0, 1]. When pricing data is missing for the model, cost-leaf defaults to pass with score 1.0.
- Composite pass threshold: 0.6.

Note: the `EvaluateAsync` adapter runs ALL THREE leaves (latency, throughput, cost) regardless of which sub-preset name is supplied — the sub-preset name is currently a label rather than a filter (the sub-preset selection is wired via the CLI subcommand structure; tighter per-preset scoping is a roadmap item).

## CLI usage

The `bench perf` family exposes three subcommands (one per preset):

```bash
# Basic — uses built-in EchoAgent stub (prints a stub-mode warning banner)
agenteval bench perf latency --subject MyAgent
agenteval bench perf throughput --subject MyAgent
agenteval bench perf cost --subject MyAgent

# Real agent via Azure OpenAI env vars
agenteval bench perf latency --subject MyAgent --azure-from-env
agenteval bench perf throughput --subject MyAgent --azure-from-env --prompt "Summarise the last quarter's earnings."
agenteval bench perf cost --subject MyAgent --azure-from-env --prompt "Hello!"
```

The `--prompt` flag overrides the default `"Hello!"` prompt. The benchmark uses the same prompt for latency + throughput + cost measurements within a single run.

`--azure-from-env` requires all three of `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_DEPLOYMENT`. Without it, the CLI falls back to the built-in `EchoAgent` stub (50 ms synthetic delay + prompt echo) with a prominent banner warning that the measurements do not reflect a real agent.

## Output

Each run writes to the canonical run dir under `.agenteval/subjects/agents/{subject}/runs/{runId}/`:

- `report.json` — canonical eval-result shape (3-leaf composite, one leaf per metric).
- `report.html` — HTML report (T0.5 v1.1, shipped 2026-05-24 via `GenericReportRenderer`).
- `report.pdf` — PDF report (T0.5 v1.1, generated via `AgentEval.Rendering.Pdf` / QuestPDF).
- The canonical `summary.json` / `manifest.json` carry the run-level audit-chain metadata (run ID, content hash, timestamp).

The perf family does NOT emit a separate `report.md` markdown sidecar (unlike OWASP / MITRE / GDPR) — the canonical store entry + the HTML/PDF render covers the documented operator scenarios. HTML and PDF emission is best-effort with warning-fallback — failures do not abort the run.

## Interpreting results

The composite `EvalResult` aggregates 3 leaves (latency / throughput / cost) via `CapByWorst` — any high/critical-severity leaf caps the composite score (critical → max 0.40, high → max 0.69). Per-leaf score interpretation:

| Score band | Label | Severity | Meaning |
|---|---|---|---|
| `>= 0.8` | `pass` | none | Comfortably within budget |
| `>= 0.5` | `warn` | low | Approaching budget; investigate trend |
| `>= 0.3` | `warn` | medium | Past target but not breaking SLO |
| `>= 0.1` | `fail` | high | SLO breach |
| `< 0.1` | `fail` | critical | Order-of-magnitude breach |

The composite verdict is `pass` when composite score >= 0.6 AND no leaf is labelled `fail`. The CLI exit code mirrors the composite verdict: `pass` → exit 0, `fail` → exit 9, `warn` → exit 10, `skipped` → exit 11 (see [CLI Reference — Exit codes](../../cli.md#exit-codes)).

## How to act on findings

- Latency `fail` — start with the recommendation embedded in the leaf (e.g. "P99 latency 8200ms exceeds threshold 5000ms. Consider caching, reducing prompt length, or upgrading the model tier."). Common root causes: bloated system prompt, oversized retrieval context, unnecessary tool round-trips, judge / safety filter overhead, network round-trip on cold connection.
- Throughput `fail` — review the agent's concurrency story (connection pool, rate-limit budget, queue backpressure). The default 2-worker / 5s window catches obvious serialisation bugs; sustained-load testing requires a dedicated tool.
- Cost `fail` — prompt-bloat is the dominant cause; check whether the system prompt has grown, whether retrieved context is being over-included, whether tool descriptions are verbose. Caching repeated prompts can substantially help for hot paths.
- Cost leaf `pass` with "Cost unknown — no pricing data for model" — the model name isn't in `ModelPricing.GetPricing`. Either add a pricing entry or treat the cost result as advisory.

## When to use this benchmark

- You need a quick smoke on P99 latency regressions for an agent in CI (use `latency` against a real agent via `--azure-from-env`).
- You want a baseline cost-per-call estimate against the pricing table to detect prompt-bloat regressions (use `cost`).
- You want a sanity-check on sustained throughput under modest concurrency (use `throughput`, default 2 workers x 5s).
- You need a single command that runs all three telemetry dimensions and produces a unified `EvalResult` for downstream Mission Control rendering.

When NOT to use:
- For load testing at production scale — use k6, NBomber, JMeter, or a dedicated load-test platform.
- For cold-start latency analysis — the benchmark warms the agent first; measuring cold-start requires a different harness.
- For memory / GC profiling — use `dotnet-trace`, `dotnet-counters`, or PerfView for that.
- For cost auditing of pricing models the `ModelPricing` table does not yet cover — the cost-leaf returns pass with score 1.0 + "Cost unknown — no pricing data for model" instead of a real number. Verify your model is in the pricing table before relying on the cost leaf.

## Programmatic use

The CLI exposes the family-level adapter, but the `PerformanceBenchmark` class is public and usable from C# directly for tighter integration. Minimal example:

```csharp
using AgentEval.Benchmarks;
using AgentEval.Core;

var bench = new PerformanceBenchmark(myAgent, new PerformanceBenchmarkOptions
{
    Verbose = false,
    EvaluateOptions = new PerformanceBenchmarkEvaluateOptions
    {
        P99LatencyThresholdMs = 3_000,        // tighter than default
        MinThroughputRps = 1.0,               // stricter
        MaxCostUSD = 0.05,                    // tighter budget
        LatencyIterationsPerPrompt = 5,
        ThroughputDuration = TimeSpan.FromSeconds(10),
    },
});

// Single-prompt path
var latency = await bench.RunLatencyBenchmarkAsync("Summarise the last quarter.");

// Multi-prompt latency aggregation (avoids server-side caching)
var prompts = new[] { "prompt A", "prompt B", "prompt C" };
var multiLatency = await bench.RunLatencyBenchmarkAsync(prompts, iterationsPerPrompt: 3);

// Composite via the Convention-2 adapter
var input = new EvalInput(
    Query: "Hello!",
    Metadata: new Dictionary<string, object> { ["prompts"] = prompts });
var composite = await bench.EvaluateAsync(input);
Console.WriteLine($"verdict={composite.Score.Label} score={composite.Score.Value:F3}");
```

The individual `RunLatencyBenchmarkAsync` / `RunThroughputBenchmarkAsync` / `RunCostBenchmarkAsync` methods return strongly-typed result records (`LatencyBenchmarkResult`, `ThroughputBenchmarkResult`, `CostBenchmarkResult`) with full per-measurement detail.

## Comparing across runs / baselines

Perf runs are stored canonically under `.agenteval/subjects/agents/{subject}/runs/{runId}/`. Compare runs via:

- `git diff` on `report.json` between runs — surfaces per-leaf score changes plus the raw `p99_ms` / `rps` / `cost_usd` dimensions.
- Mission Control — renders runs with per-leaf detail; visual diff across runs by selecting two runs.
- Programmatic post-processing of the canonical `EvalResult.Details.Dimensions` dictionary (`p99_ms`, `rps`, `cost_usd`) for time-series tracking outside AgentEval.

## Limitations and roadmap

Known limitations:
- The sub-preset names (`latency`, `throughput`, `cost`) currently label the run but do not filter the measurements — the `EvaluateAsync` adapter always runs all three. Tighter per-preset scoping is a roadmap item.
- The throughput measurement window is fixed at 5 seconds by default — not suitable for endurance / soak testing.
- Cold-start latency is explicitly excluded (the warmup iteration runs first).
- Cost estimation requires the agent's model name to appear in `ModelPricing.GetPricing` — unknown models default the cost leaf to pass with score 1.0.
- Per-prompt input is single-string only; multi-prompt CSV / metadata override (via `EvalInput.Metadata["prompts"]`) is supported programmatically but not exposed on the CLI subcommands.

Tracking backlog (see `strategy/FutureFeatures/todo/13-pending-issues-tasks.md`):
- T0.2 — `--azure-from-env` flag on `bench perf` (shipped 2026-05-24).
- T0.5 — `report.html` + `report.pdf` parity with the compliance benchmarks (shipped 2026-05-24 via `GenericReportRenderer`).
- T3.11 — Multi-provider agent-manifest schema (would let `--agent-config <path>` resolve non-Azure agents).
- Per-preset measurement scoping (latency-only / throughput-only / cost-only execution paths) remains roadmap.

See also:
- [OWASP getting-started](../owasp/getting-started.md) — security red-team family.
- [MITRE ATLAS getting-started](../mitre/getting-started.md) — security red-team family tagged against ATLAS.
- [Memory getting-started](../memory/getting-started.md) — agent-state-stressing benchmarks (different scope: memory recall vs runtime perf).
- `src/AgentEval.Evals.Performance/PerformanceBenchmark.cs` — benchmark source + adapter.
- `src/AgentEval.Cli/Commands/BenchPerfCommand.cs` — CLI subcommand source.
