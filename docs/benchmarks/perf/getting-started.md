# Performance Benchmark — Getting Started

> Status: beta. The performance benchmark measures agent runtime characteristics across three telemetry dimensions: P99 latency, throughput (requests-per-second), and per-call cost. It is a runtime-observability tool, not a load-testing or capacity-planning replacement.
>
> Coverage: 3 sub-presets — `latency` (P99 / P90 / P50 latency + mean TTFT), `throughput` (concurrent RPS over a sampling window), `cost` (per-prompt token + USD cost estimate against the pricing table). Not covered: memory pressure, GC pause analysis, cold-start latency, sustained-load endurance, multi-region latency, or any system-level resource accounting outside the agent invocation.

## What this measures

The performance benchmark exercises the agent under test via direct `IEvaluableAgent.InvokeAsync` calls and records timing + token-usage telemetry. The `EvaluateAsync` adapter (Convention 2) runs all three measurements (latency, throughput, cost) and aggregates them into a 3-leaf composite `EvalResult` via `CapByWorst` — a single high-severity leaf caps the composite.

What IS tested: per-call latency (P50 / P90 / P99 + mean, time-to-first-token when the agent implements `IStreamableAgent`), sustained throughput under a configurable concurrent-worker pool, and per-prompt cost based on the `ModelPricing` table. What is NOT tested: process-level memory pressure, GC pause durations, cold-start latency on fresh process spawn, long-tail endurance under sustained load (>15s), network-egress costs, multi-region latency variance, or anything outside the agent invocation boundary (HTTP / provider SDK / connection pool internals all fall under the per-call latency number but cannot be decomposed by this benchmark).

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
  - Network egress accounting — the USD-cost estimate covers LLM tokens only; egress to / from your model provider is not tracked.

## Presets

Sourced verbatim from `BenchmarkFamilyRegistry` (see `src/AgentEval.Evals.Performance/PerformanceBenchmarkRegistration.cs:45-50`).

| Preset | Description (verbatim) | Cost tier | Typical iterations | Approx. LLM cost |
|---|---|---|---|---|
| `latency` | P99 latency measurement (3 iterations x N prompts + warmup) | Low | 1 prompt x 3 iterations + 1 warmup = 4 calls | telemetry-only (~$0.001 per call against gpt-4o-mini if using `--azure-from-env`; `--sut mock`: free, and measures nothing) |
| `throughput` | Concurrent throughput measurement (default 2 workers x 5s) | Low | 2 concurrent workers x ~5s window (typically 5-20 calls depending on agent speed) | telemetry-only |
| `cost` | Per-prompt token + cost estimate (pricing-table-backed) | Low | 1 call per supplied prompt | telemetry-only |

Default thresholds (overridable via `PerformanceBenchmarkEvaluateOptions`):
- P99 latency threshold: 5000 ms → score = 1 - (p99ms / 5000), clamped [0, 1].
- Minimum throughput: 0.5 RPS → score = min(rps / 0.5, 1.0).
- Maximum cost: $0.10 USD → score = 1 - (cost / 0.10), clamped [0, 1]. When pricing data is missing for the model, cost-leaf defaults to pass with score 1.0.
- Composite pass threshold: 0.6.

Note: the `EvaluateAsync` adapter runs ALL THREE leaves (latency, throughput, cost) regardless of which sub-preset name is supplied — the sub-preset name is currently a label rather than a filter (the sub-preset selection is wired via the CLI subcommand structure, and no subcommand restricts which measurements run).

## CLI usage

The `bench perf` family exposes three subcommands (one per preset):

```bash
# Real model from the configured inference provider
agenteval bench perf latency --subject MyAgent --azure-from-env
agenteval bench perf throughput --subject MyAgent --azure-from-env --prompt "Summarise the last quarter's earnings."
agenteval bench perf cost --subject MyAgent --azure-from-env --prompt "Hello!"

# Any OpenAI-compatible endpoint
agenteval bench perf latency --subject MyAgent --endpoint http://localhost:11434/v1 --model llama3.1
```

The `--prompt` flag overrides the default `"Hello!"` prompt. The benchmark uses the same prompt for latency + throughput + cost measurements within a single run.

`--azure-from-env` builds the agent from whichever provider `AI_INFERENCE_PROVIDER` selects — Azure OpenAI, Bitdeer, OpenAI, Azure AI Foundry or any OpenAI-compatible host; see the [provider table](../../cli.md#ai_inference_provider--which-provider-the-cli-talks-to) for the variables each one needs. Despite its name, the flag is not Azure-only: with the selector unset, the first fully configured provider in that table's order is used, so an environment with only the `AZURE_OPENAI_*` trio still gets Azure OpenAI. If no provider is configured, the command fails and names what is missing. Without a target the command refuses (exit 2). `--sut mock` measures a built-in stand-in (a 50 ms echo) instead: the run says MOCK, exits 11 whatever it scores, and nothing is written to `.agenteval/`, because it measures no agent.

The cost leaf prices the model the agent used: the model the provider resolved for `--azure-from-env` (the Azure deployment, the Bitdeer model id, and so on), or `--model` for an `--endpoint` target. A `--sut` target names no model, so the leaf falls back to the `--subject` name and then to the model id the provider reported in its response. If no name is in the pricing table, the cost is not measured: the cost leaf is skipped, no cost is reported, and a warning names the model.

## Output

Each run writes to the canonical run dir under `.agenteval/subjects/agents/{subject}/runs/{runId}/`:

- `report.json` — canonical eval-result shape (3-leaf composite, one leaf per metric).
- `report.html` — HTML report, rendered by `GenericReportRenderer`.
- `report.pdf` — PDF report, generated via `AgentEval.Rendering.Pdf` / QuestPDF.
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

## Limitations

Known limitations:
- The sub-preset names (`latency`, `throughput`, `cost`) currently label the run but do not filter the measurements — the `EvaluateAsync` adapter always runs all three; there is no latency-only, throughput-only or cost-only execution path.
- The throughput measurement window is fixed at 5 seconds by default — not suitable for endurance / soak testing.
- Cold-start latency is explicitly excluded (the warmup iteration runs first).
- Cost estimation requires the agent's model name to appear in `ModelPricing.GetPricing`. For any other model the cost is not measured and the cost leaf is skipped.
- Per-prompt input is single-string only; multi-prompt CSV / metadata override (via `EvalInput.Metadata["prompts"]`) is supported programmatically but not exposed on the CLI subcommands.
- The CLI measures a plain chat model: built with `--azure-from-env` from any configured provider, an OpenAI-compatible `--endpoint`, or a built-in `--sut` target. There is no option that loads an agent from a manifest file. An agent with its own tools, memory or a non-chat interface is measured from a small program that wraps it as an `IEvaluableAgent` — see `samples/AgentEval.Samples/Benchmarks/02_PerformanceBenchmark.cs` and [Programmatic use](#programmatic-use).

See also:
- [OWASP getting-started](../owasp/getting-started.md) — security red-team family.
- [MITRE ATLAS getting-started](../mitre/getting-started.md) — security red-team family tagged against ATLAS.
- [Memory getting-started](../memory/getting-started.md) — agent-state-stressing benchmarks (different scope: memory recall vs runtime perf).
- `src/AgentEval.Evals.Performance/PerformanceBenchmark.cs` — benchmark source + adapter.
- `src/AgentEval.Cli/Commands/BenchPerfCommand.cs` — CLI subcommand source.
