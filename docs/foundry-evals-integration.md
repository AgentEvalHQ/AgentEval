# Foundry evals + AgentEval

AgentEval runs **Azure AI Foundry evals alongside — and inside — its own evals**: one agent run, one
source-tagged report. Foundry's cloud evaluators and AgentEval's local Composite Evals become peers, so you
can mix cloud-graded and local-graded signals in a single benchmark **without running the agent twice**.

Both integration points live in **`AgentEval.MAF`** and work with **any** MAF `IAgentEvaluator` — Foundry is
the motivating example, but nothing in the shipping libraries references Foundry (see [Dependencies](#dependencies)).

## Two ways to combine them

| Direction | Entry point | Shape | When |
|-----------|-------------|-------|------|
| **Alongside** | `CompositeAgentEvaluator` | Foundry and AgentEval run as sibling *sources* over one agent run — concurrent, per-source isolated + timed-out — merged into one report. | The default. Efficient: Foundry batches all items in one call. |
| **Inside** | `IAgentEvaluator.AsEvalLeaf()` | A Foundry eval becomes a *weighted leaf* inside an AgentEval `CompositeEval`, next to AgentEval leaves, under the same weighting / threshold / aggregation. | A single hierarchical benchmark whose leaves mix providers. Foundry runs once per input — best for small item counts. |

### Alongside — `CompositeAgentEvaluator`

```csharp
using AgentEval.MAF.Evaluators;

var local   = AgenticBenchmark.AgenticExecution(judge).AsMeaiEvaluator().AsAgentEvaluator(chatConfig);
var foundry = new FoundryEvals(projectClient, model, /* … */ FoundryEvals.TaskAdherence, FoundryEvals.Relevance);

var hybrid = new CompositeAgentEvaluator(
[
    ("agenteval-local", local,   null),                        // fast; no ceiling
    ("foundry",         foundry, TimeSpan.FromMinutes(2)),     // bound the cloud call
]);

var merged = await agent.EvaluateAsync(queries, hybrid);       // the agent runs ONCE
EvalResult report = UnifiedEvalReport.Build(hybrid.CapturedPerSource);   // one branch per source
```

- A Foundry failure/timeout becomes a **visible "skipped" branch** — the local results survive.
- Each source's metrics are merged under a `"{source}:"` prefix, so identically-named metrics never collide.

### Inside — `AsEvalLeaf`

```csharp
IEval foundryRelevance = new FoundryEvals(projectClient, model, /* … */ FoundryEvals.Relevance)
    .AsEvalLeaf("foundry.relevance", "Foundry Relevance");

var benchmark = new CompositeEval(
    "hybrid.benchmark", "Hybrid Benchmark", "hybrid", "1.0.0",
    components:
    [
        new(AgenticBenchmark.ToolCallAccuracy(judge), 0.5),    // AgentEval sub-composite (multi-dimension)
        new(foundryRelevance, 0.5),                            // Foundry eval as a weighted leaf
    ],
    WeightedSumAggregation.Instance, threshold: 0.75);

EvalResult tree = await benchmark.EvaluateAsync(new EvalInput(query, response));
```

## Surface (`AgentEval.MAF.Evaluators`)

| Type | Role |
|------|------|
| `CompositeAgentEvaluator` | run several `IAgentEvaluator`s over one run — concurrent, isolated, per-source timeout, optional `CircuitBreaker` |
| `UnifiedEvalReport` | merge per-source results into one source-tagged `EvalResult` tree (a branch per source) |
| `AsEvalLeaf` / `AgentEvaluatorEvalLeaf` | wrap a MAF `IAgentEvaluator` as an AgentEval `IEval` leaf for a `CompositeEval` (inverse of `AsAgentEvaluator`) |
| `CircuitBreaker` | skip a persistently-failing source fast |

## Samples

| Sample | Direction |
|--------|-----------|
| `AgentEval.Samples` → Benchmarks → **Foundry Hybrid** | Alongside (batched) |
| `AgentEval.Samples` → Benchmarks → **Foundry Hierarchy** | Inside (weighted leaves) |
| [`samples/AgentEval.MafEvalFoundryAlongsideLocal`](../samples/AgentEval.MafEvalFoundryAlongsideLocal) | Alongside, standalone end-to-end |

Set `FOUNDRY_PROJECT_ENDPOINT` (+ Azure credentials) to include the Foundry branch; without it the samples run
AgentEval-local-only and say so.

## Dependencies

The Foundry evals package is currently published **preview-only**, so it is referenced **only by the samples** —
never by the shipping libraries. `AgentEval`, `AgentEval.MAF`, and the CLI pull **nothing** Foundry (no transitive
`Microsoft.Agents.AI.Foundry` or `Azure.AI.Projects`). The core stays provider-agnostic: every type above works
with any `IAgentEvaluator`, so a stable Foundry package — or any other provider's evaluator — drops in unchanged.

## See also

- [Using AgentEval with MAF evals](using-agenteval-with-maf-evals.md) — §3d (alongside) and §3e (inside), in depth.
