# Memory Benchmark — Getting Started

> Status: beta. The memory benchmark drives the agent under test through multi-turn, multi-scenario memory stress tests via the `MemoryBenchmarkRunner` and grades responses with the per-scenario `MemoryJudge`. It is a memory-architecture diagnostic, not a published-academic baseline (see the sibling `longmemeval` family for that).
>
> Coverage: 5 presets — `quick` (3 categories, CI-friendly), `standard` (8 categories incl. Abstention + Preference Extraction), `full` (12 categories incl. cross-session, conflict resolution, multi-session reasoning), `diagnostic` (same as Full with maximum context pressure ~50K+ tokens), and `overflow` (Standard categories with 192K target on a 128K window — POWER-USER, deliberately stresses context-window saturation).

## What this measures

The memory benchmark stresses agent memory architecture across retention, temporal-reasoning, noise-resilience, reach-back-depth, fact-update-handling, multi-topic-juggling, abstention (hallucination detection), preference-extraction, cross-session, reducer-fidelity, conflict-resolution, and multi-session-reasoning dimensions. Each preset configures a weighted set of categories; the `MemoryBenchmarkRunner` injects synthetic conversation history, asks targeted recall questions, and grades responses via the LLM `MemoryJudge`.

What IS tested: the agent's end-to-end ability to recall, update, abstain, and reason across multi-turn / multi-session conversation state — under varying levels of noise and context pressure. What is NOT tested: the agent's internal architecture choices in isolation (reducer impl, vector-store backend, embedding model) — those are inferred from end-to-end behaviour, not directly inspected. For paper-comparable single-session memory baselines, see the sibling `longmemeval` family.

## Scope and omissions

- Covered (with rationale per item):
  - 12 distinct memory dimensions across the Full preset — each targets a specific failure mode of memory-equipped agents.
  - Configurable context pressure (`TargetTokensOverride`) — lets diagnostic / overflow presets stress agents past nominal context limits.
  - Configurable overflow filler turns (`OverflowCallsOverride`) — for graduated context-saturation tests beyond raw injection.
  - Per-category weights — composite score reflects the operator's relative concern across dimensions.
  - LLM-graded responses via `MemoryJudge` — semantic grading, not regex/exact-match.
  - Abstention category — explicitly grades the agent's willingness to say "I don't know" rather than hallucinate.
- Out of scope (with rationale):
  - Memory architecture introspection (reducer impl, vector store, embedding model) — black-box evaluation only.
  - Cross-agent comparison without baseline calibration — different agents stress different categories differently; calibrate per-agent before tracking deltas.
  - Code-grader fallback — when the LLM judge fails the category falls through with a judge-failure marker; no syntactic / regex fallback.
  - Multi-language scenarios — English-only.
  - Single-session paper-comparable baselines — use the `longmemeval` family for that.
  - Pure-tool-call memory testing (e.g. "remember to call this tool with this arg") — the scenarios target conversational recall, not tool-state.

## Presets

Sourced verbatim from `BenchmarkFamilyRegistry` (see `src/AgentEval.Memory/MemoryBenchmarkRegistration.cs:43-47`).

| Preset | Description (verbatim) | Cost tier | Typical entry count | Approx. LLM cost |
|---|---|---|---|---|
| `quick` | 3 categories (basic retention, temporal, noise) — CI-friendly | Medium | ~3 categories x ~5 scenarios each = ~15 scenario invocations | ~$0.20 - $0.80 against gpt-4o-mini |
| `standard` | 8 categories including Abstention + Preference Extraction | High | ~8 categories x ~5-10 scenarios = ~40-80 invocations | ~$1.00 - $4.00 |
| `full` | 12 categories including cross-session, conflict resolution, multi-session reasoning | High | ~12 categories x ~5-10 scenarios = ~60-120 invocations | ~$2.00 - $8.00 |
| `diagnostic` | Same categories as Full with maximum context pressure (~50K+ tokens) | High | Full's 12 categories with extended context — POWER-USER preset | ~$5.00 - $20.00 (context-pressure inflates judge prompt sizes) |
| `overflow` | 8 categories with context overflow (192K target on 128K window) | High | Standard's 8 categories with deliberate context-window saturation — POWER-USER preset | ~$5.00 - $20.00 (context overflow + extended interactions per scenario) |

Cost estimates assume `gpt-4o-mini` judge pricing and depend heavily on the agent's response length + the chosen context-pressure target. Diagnostic and overflow presets are POWER-USER — they stress the agent's reducer / summarisation / vector-store path past nominal limits and are designed to surface failure modes that the standard preset masks; expect notably higher cost.

> **Why is `quick` `CostTier.Medium` if it's CI-friendly?** `quick` makes ~15 LLM round-trips (~$0.20 - $0.80 at gpt-4o-mini pricing) — small in absolute terms but well above the `CostTier.Low` budget used by `bench owasp smoke` (zero LLM cost) or `bench perf latency` (telemetry-only). It IS CI-tractable when the CI budget allows ~$1/run; consider running `quick` on the main branch + nightly rather than on every commit if the budget is tighter. The other 4 presets are `High` and not intended for any commit-time CI.

## CLI usage

```bash
# Quick (CI-friendly, ~$0.20-$0.80)
agenteval bench memory --preset quick --subject MyAgent

# Standard
agenteval bench memory --preset standard --subject MyAgent

# Full (12 categories)
agenteval bench memory --preset full --subject MyAgent

# Diagnostic (POWER-USER — max context pressure)
agenteval bench memory --preset diagnostic --subject MyAgent

# Overflow (POWER-USER — context-window saturation on 128K models)
agenteval bench memory --preset overflow --subject MyAgent
```

REQUIRES Azure OpenAI — no stub fallback. All three of `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_DEPLOYMENT` must be set; the benchmark needs a real LLM-backed agent under test plus a real LLM judge for grading.

The CLI runs the agent-under-test via `chatClient.AsEvaluableAgent(name: subject, includeHistory: true)` against the resolved Azure deployment. The system prompt is fixed to `"You are a helpful assistant. Use what you remember from our conversation to answer."` Programmatic callers can compose a different `IEvaluableAgent` and pass it to `MemoryBenchmarkRunner.Create(chatClient).RunBenchmarkAsync(agent, preset)` directly.

The `overflow` preset is designed for ~128K-context models (such as gpt-4o-mini) where setting `TargetTokensOverride = 128_000` + `OverflowCallsOverride = 20` deliberately fills 75% of the window via injection, then pushes past the limit via filler calls. On larger-context models the overflow effect attenuates; treat the result as model-specific rather than absolute.

## Output

Each run writes to the canonical run dir under `.agenteval/subjects/agents/{subject}/runs/{runId}/`:

- `report-native.json` — the native `MemoryBenchmarkResult` (Shape B, ADR-017 Convention 3): per-category scores, overall score, grade, total duration.
- The canonical `manifest.json` / `summary.json` carry the run-level audit-chain metadata (run ID, content hash, timestamp, verdict, metrics).

Memory is a Shape B family (per ADR-017): its multi-scenario, multi-turn, agent-stateful semantics do not map onto the single-shot Convention-2 `(EvalInput) → EvalResult` shape that OWASP / MITRE / Perf use, so no `report.json` / `report.md` / `report.html` / `report.pdf` sidecars are emitted. Mission Control renders the native shape directly.

CLI verdict mapping (aligned with `MemoryBenchmarkResult.Passed` canonical semantics at `src/AgentEval.Memory/Models/MemoryBenchmarkResult.cs:75`):
- `overall_score >= 70` → `PASS`
- `50 <= overall_score < 70` → `WARN`
- `overall_score < 50` → `FAIL`

## Interpreting results

The native `MemoryBenchmarkResult` carries:

- `OverallScore` — weighted aggregate across the preset's category set (0-100 scale).
- `Grade` — letter grade derived from `OverallScore`.
- `CategoryResults[]` — per-category score, name, scenario-level breakdown.
- Per-scenario judge verdicts + raw transcripts (programmatically accessible via the runner).

Per-category interpretation:

| Score band | Meaning |
|---|---|
| `>= 90` | Excellent — strong memory across the dimension |
| `70 - 89` | Pass — production-acceptable for most use cases |
| `50 - 69` | Warning — degradation visible; investigate before shipping memory-critical features |
| `< 50` | Fail — memory architecture is unable to handle the dimension at the preset's pressure level |

CLI exit codes: `PASS` → exit 0, `WARN` → exit 10 (`GateWarning`), `FAIL` → exit 9 (`GateFailed`) — see
[CLI Reference — Exit codes](../../cli.md#exit-codes). (This previously said WARN maps to exit 0 alongside
PASS — that was never accurate; WARN has always been non-zero, distinct from a clean PASS.)

## How to act on findings

- Low Basic Retention — the agent isn't holding even single-session state; verify the chat-client adapter has `includeHistory: true` and the history isn't being aggressively truncated.
- Low Temporal Reasoning — the agent isn't tracking relative time / ordering; review whether timestamps reach the model and whether the system prompt acknowledges temporal awareness.
- Low Noise Resilience — irrelevant turns are confusing the agent; the reducer / summarisation strategy may be over-dropping relevant facts, or the model may be over-attending to recency.
- Low Reach-Back Depth — the agent can't reach back far enough; if you have a reducer, its summarisation may be compressing too aggressively.
- Low Fact Update Handling — the agent sticks with the original fact when it should pick up the update; classic stale-cache symptom in summarisation pipelines.
- Low Abstention — the agent hallucinates rather than saying "I don't know"; tighten the system prompt's abstention policy and consider an upstream uncertainty-detection gate.
- Low Cross-Session — multi-session state isn't crossing session boundaries; verify the agent implements `ISessionResettableAgent` and the session-boundary semantics match your production model.
- Low Reducer Fidelity — the reducer is losing information during compression; this is the canonical reducer-tuning signal.
- Low Conflict Resolution — when facts conflict, the agent picks arbitrarily rather than reasoning about precedence; review whether the agent has explicit guidance on conflict-resolution policy.
- Low Multi-Session Reasoning — the agent can recall across sessions but can't reason across the recalled facts; this typically points at a reducer that's too lossy for downstream synthesis.
- Low Preference Extraction — implicit user preferences expressed across the conversation aren't being internalised; preference extraction is often a separate sub-system; check whether it's wired in.
- Low Multi-Topic — context-switching is causing forgetting of earlier-topic facts; review summarisation policy across topic boundaries.

## When to use this benchmark

- You ship a memory-capable agent (reducer, vector store, summarisation) and need diagnostic coverage across the failure modes that production memory architectures hit.
- You are tuning reducer / context-window / summarisation strategy and want to detect regressions across 12 dimensions.
- You need to detect hallucination regressions (the Abstention category explicitly grades the agent's willingness to say "I don't know").
- You want a CI-friendly fast feedback loop on basic retention + temporal + noise dimensions (use `quick`).
- You need POWER-USER context-overflow stress testing on a 128K-window model (use `overflow`).
- You need maximum-pressure diagnostic coverage to surface failure modes the standard preset masks (use `diagnostic`).

When NOT to use:
- For paper-comparable single-session memory baselines — use the sibling `longmemeval` family (academic ICLR 2025 benchmark).
- For pure-tool-call memory (tool-arg persistence) — the scenarios target conversational recall, not tool-state.
- For non-English memory tests — English-only.
- When you need a stub / dry-run path — there is none. The LLM round-trip IS the signal.
- For cross-agent comparison without per-agent baseline calibration — different agents stress different categories differently.

## Programmatic use

The CLI is the supported path for canonical run persistence, but the underlying `MemoryBenchmark` presets + `MemoryBenchmarkRunner` are public and usable from C# directly. Minimal example:

```csharp
using AgentEval.Benchmarks;
using AgentEval.Memory;
using AgentEval.Memory.Evaluators;
using Microsoft.Extensions.AI;

// Provide your own IChatClient (Azure OpenAI, OpenAI direct, etc).
IChatClient chatClient = /* your IChatClient */;

// Pick a preset: Quick / Standard / Full / Diagnostic / Overflow.
var preset = MemoryBenchmark.Standard;

// Build the runner (one runner instance per chat client is fine).
var runner = MemoryBenchmarkRunner.Create(chatClient);

// Build the agent under test.
var agent = chatClient.AsEvaluableAgent(
    name: "MyAgent",
    systemPrompt: "You are a helpful assistant. Use what you remember from our conversation to answer.",
    includeHistory: true);

var result = await runner.RunBenchmarkAsync(agent, preset);
Console.WriteLine($"Overall score: {result.OverallScore:F1}% — Grade: {result.Grade}");
foreach (var cat in result.CategoryResults)
    Console.WriteLine($"  {cat.CategoryName,-30} {cat.Score:F1}%");
```

The runner accepts any `IEvaluableAgent` — the `chatClient.AsEvaluableAgent(...)` extension is one convenience binding; for an agent with a custom reducer / vector store, implement `IEvaluableAgent` (and optionally `ISessionResettableAgent` for the cross-session categories) yourself.

## Comparing across runs / baselines

Memory runs are stored canonically under `.agenteval/subjects/agents/{subject}/runs/{runId}/`. Compare runs via:

- `git diff` on `report-native.json` — surfaces per-category score changes plus overall score + grade deltas.
- Mission Control — renders the native shape; visual diff across runs.
- Programmatic post-processing of `MemoryBenchmarkResult.CategoryResults` for per-category tracking outside AgentEval.

The `AgentEval.Memory` assembly also ships a `BaselineComparer` + `JsonFileBaselineStore` for per-agent baseline persistence and regression detection — see `src/AgentEval.Memory/Reporting/` for the surface (not yet exposed via CLI for the memory family specifically).

## Limitations and roadmap

Known limitations:
- LLM-judge cost dominates; diagnostic + overflow presets are notably more expensive than Standard. Budget accordingly.
- English-only scenarios.
- Memory architecture introspection (reducer impl, vector store, embedding model) is inferred from end-to-end behaviour, not directly inspected.
- The `overflow` preset's saturation effect attenuates on larger-context models; treat results as model-specific.
- No code-grader fallback — judge-failure entries fall through with a judge-failure marker.
- CLI verdict thresholds (70 PASS / 50 WARN) are aligned with the canonical `MemoryBenchmarkResult.Passed` boundary at `MemoryBenchmarkResult.cs:75`; pre-v1.1 CLI used 80/50 which made canonical Passed=true scores of 75 render as WARN.

Tracking backlog (see `strategy/FutureFeatures/todo/13-pending-issues-tasks.md`):
- T0.6 — `agenteval bench memory` CLI command (shipped 2026-05-24).
- T3.13 — Multi-turn calibration entry schema extension (open; would re-enable carved-out memory evaluators in the agentic calibration sweep).
- T3.11 — Multi-provider agent-manifest schema (would let the memory benchmark target non-Azure agents directly).
- Per-category drill-down rendering in Mission Control remains roadmap.

See also:
- [LongMemEval getting-started](../longmemeval/getting-started.md) — sister memory family targeting paper-comparable academic baselines.
- [Agentic getting-started](../agentic/getting-started.md) — broader agentic-quality benchmarks (including 5 memory-multiturn evaluators in the agentic dispatch table).
- `src/AgentEval.Memory/Models/MemoryBenchmark.cs` — preset factory source (`Quick` / `Standard` / `Full` / `Diagnostic` / `Overflow`).
- `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs` — runner source.
- `src/AgentEval.Memory/Engine/MemoryJudge.cs` — LLM judge source.
- `src/AgentEval.Cli/Commands/BenchMemoryCommand.cs` — CLI subcommand source.
