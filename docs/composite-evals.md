# Composite Evaluations

## Overview

A composite evaluation aggregates multiple sub-evals into one scored result with a recursive tree of sub-results. This is the right primitive when a single pass/fail verdict must draw on several independent checks — for example, a GDPR Article 17 rollup across acknowledgment, backup propagation, legal obligation, and over-erasure checks; Foundry's tool-call-accuracy formula (`0.25 × selection + 0.25 × input + ...`); or a multi-judge consensus where each judge is one component. Phase 1 ships `WeightedSumAggregation` as the only aggregation strategy. Additional strategies (`Min`, `WeightedMedian`, `Strict`) are deferred until a concrete consumer asks for them.

---

## Two kinds of evals

Both types implement `IEval` and produce the same `EvalResult` shape. Callers never need to branch on type.

| Kind | Class | How it scores |
|------|-------|---------------|
| Atomic (LLM judge) | `AtomicLlmEval` | Delegates to an `AgentEval.Core.IEvaluator`; normalises the 0–100 score to 0–1. |
| Atomic (deterministic) | `AtomicCodeEval` (abstract) | Subclass implements `Evaluate(input)` synchronously; no LLM call. |
| Composite | `CompositeEval` | Runs all sub-evals in parallel via `Task.WhenAll`; aggregates via `IAggregationStrategy`. |

All three live in the `AgentEval.Evals` namespace.

---

## Quick start — atomic LLM-judge eval

`AtomicLlmEval` wraps an existing `AgentEval.Core.IEvaluator` instance (obtain one from your evaluator factory or DI container).

```csharp
var ack = new AtomicLlmEval(
    evaluator: myEvaluator,                   // AgentEval.Core.IEvaluator
    key:       "art17_acknowledgment",
    name:      "Article 17 - Acknowledgment",
    category:  "compliance.gdpr",
    version:   "1.0.0",
    criteria:  new[] { "Acknowledgment is explicit and timely" },
    passThreshold: 0.70);                     // default; omit to keep 0.70

var result = await ack.EvaluateAsync(new EvalInput(
    Query:    "I want my data deleted.",
    Response: "We confirm receipt of your request..."));

Console.WriteLine(result.Score.Value);   // 0..1
Console.WriteLine(result.Score.Label);  // "pass" or "fail"
```

---

## Quick start — composite eval

The example below mirrors the GDPR Article 17 worked example from the internal design notes.

```csharp
// 1. Build four atomic sub-evals (using stub implementations here for brevity).
var acknowledgment   = new Art17AcknowledgmentEval(myEvaluator);   // AtomicLlmEval subclass
var backupPropagation = new Art17BackupPropagationEval(myEvaluator);
var legalObligation  = new Art17LegalObligationEval(myEvaluator);
var noOvererasure    = new Art17NoOvererasureEval(myEvaluator);

// 2. Wrap them in a CompositeEval with weights that sum to 1.0 and a pass threshold.
var components = new EvalComponent[]
{
    new(acknowledgment,    Weight: 0.30),
    new(backupPropagation, Weight: 0.30),
    new(legalObligation,   Weight: 0.20),
    new(noOvererasure,     Weight: 0.20),
};

var article17 = new CompositeEval(
    key:        "gdpr_article_17",
    name:       "Article 17 - Right to erasure",
    category:   "compliance.gdpr",
    version:    "1.0.0",
    components: components,
    aggregation: WeightedSumAggregation.Instance,
    threshold:   0.80);

// 3. Evaluate.
var input  = new EvalInput(Query: "Please delete all my personal data.");
var result = await article17.EvaluateAsync(input);

// 4. Inspect the result.
Console.WriteLine(result.Score.Value);            // weighted sum, e.g. 0.675
Console.WriteLine(result.Score.Severity);         // max severity of sub-evals, e.g. "high"
Console.WriteLine(result.Score.Label);            // "pass" or "fail" (threshold-driven)

foreach (var sub in result.Details.SubResults!)
{
    Console.WriteLine($"  {sub.Metric.Key}: {sub.Score.Value:F2} ({sub.Score.Label})");
}
```

Composites can nest. Because `CompositeEval` implements `IEval`, it can itself appear as a component inside another `CompositeEval`. The recursive tree of sub-results is preserved all the way down.

---

## Verdict matrix

The composite verdict is determined after aggregation. `warn` is a soft fail: `Passed = false` but `Label = "warn"` distinguishes it from a hard fail.

| Threshold set? | Condition | `Label` | `Passed` |
|----------------|-----------|---------|----------|
| Yes | `score >= threshold` | `"pass"` | `true` |
| Yes | `score < threshold` | `"fail"` | `false` |
| No | severity is `critical` or `high` | `"fail"` | `false` |
| No | severity is `medium` | `"warn"` | `false` |
| No | severity is `none` or `low` | `"pass"` | `true` |

Composite severity is the maximum severity across all sub-results (`none < low < medium < high < critical`), computed by `SeverityRollup.Max`.

---

## Persistence to the canonical store

Composite results go through `EvalResultPersistence` to reach the canonical `IOutputStore`.

```csharp
// Serialise the recursive tree into a ScenarioResult.
var sr = EvalResultPersistence.ToScenarioResult(
    result,
    scenarioId:   "art17",
    scenarioName: "Article 17 - Right to erasure");

// Write to the store.
await store.WriteScenarioResultAsync(runId, sr);
```

`ToScenarioResult` lifts `Score.Value`, `Score.Passed`, `Details.Dimensions`, and `Provenance.EstimatedCost` to the top-level `ScenarioResult` fields for queryability. The full recursive tree — including every level of sub-results — is serialised as JSON inside `ScenarioResult.Output`.

To restore the tree:

```csharp
var restored = EvalResultPersistence.FromScenarioResult(sr);
```

The existing `ContentHasher.HashRunAsync` covers the embedded JSON, so the audit chain (`agenteval doctor`) extends to composite results without any schema or store changes.

---

## DI registration

```csharp
services.AddCompositeEvals();
```

Registers `WeightedSumAggregation` as the default `IAggregationStrategy` using `TryAdd` semantics. If your container already has an `IAggregationStrategy` registration, it is preserved.

---

## Schema validation

The `eval-result.schema.json` v1 schema is embedded as a resource in the `AgentEval.DataLoaders` assembly and covers the recursive `EvalResult` tree (including nested sub-results). Use the existing `SchemaValidator` helper (internal) when building tools that need to validate persisted results.

---

## What is not in Phase 1

The following are deferred until a concrete consumer asks for them:

- `MinAggregation` — useful for audit-grade compliance where every component must pass.
- `WeightedMedianAggregation` — outlier-resistant alternative to weighted sum.
- `StrictAggregation` — any failure fails the composite.
- `CapByWorstAggregation` — caps the composite score at the lowest sub-score.
- `MedianAggregation` — unweighted median for multi-judge consensus.
- `Predicate` field on `EvalComponent` — conditional component skipping with automatic weight renormalization.
- YAML composite authoring — declare composites in config rather than code.
- Sub-eval result caching — reuse a sub-eval result across multiple composites.
- Streaming events on sub-eval completion — notify callers as each sub-eval finishes.
- Hierarchical calibration — per-level score adjustment for nested composites.

---

## See also

- [Evaluation Guide](evaluation-guide.md) — choosing the right metrics for your use case
- [Migrating to the `.agenteval/` workspace layout](migration/output-folder.md) — canonical output store, audit chain, and `agenteval doctor`
