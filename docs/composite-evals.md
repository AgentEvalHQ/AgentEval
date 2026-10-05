# Composite Evaluations

## Overview

A composite evaluation aggregates multiple sub-evals into one scored result with a recursive tree of sub-results. This is the right primitive when a single pass/fail verdict must draw on several independent checks — for example, a GDPR Article 17 rollup across acknowledgment, backup propagation, legal obligation, and over-erasure checks; Foundry's tool-call-accuracy formula (`0.25 × selection + 0.25 × input + ...`); or a multi-judge consensus where each judge is one component.

Five aggregation strategies ship under `AgentEval.Evals.Aggregations`:

| Strategy | When to use |
|---|---|
| `WeightedSumAggregation` | Default — score is `Σ(weight_i × score_i)` over components. Used by most agentic and GDPR Standard presets. |
| `MinAggregation` | Score is the minimum across components — any weak component caps the verdict. Used inside EU AI Act Pillar 1 (Prohibited Practices). |
| `CapByWorstAggregation` | Score is capped at the worst severity-weighted sub-score; surfaces critical failures even when other components score well. Used by GDPR / EU AI Act `audit` presets. |
| `WeightedMedianAggregation` | Outlier-resistant alternative to weighted sum — useful when one judge in a multi-judge panel is known to skew scores. |
| `MajorityVoteAggregation` | Pass/fail verdict by majority vote across components — used for stochastic-runs aggregation in `agenteval bench gdpr --runs N`. |

---

## Four kinds of evals

All four implement `IEval` and produce the same `EvalResult` shape. Callers never need to branch on type.

| Kind | Class | How it scores | `provenance.type` |
|------|-------|---------------|-------------------|
| Atomic (LLM judge) | `AtomicLlmEval` | Delegates to an `AgentEval.Core.IEvaluator`; normalises the 0–100 score to 0–1. | `atomic-llm` |
| Atomic (deterministic) | `AtomicCodeEval` (abstract) | Subclass implements `Evaluate(input)` synchronously; no LLM call. | `atomic-code` |
| Atomic (decision model) | `DecisionEval` | Asks an `AgentEval.Decisions.IDecisionClient` (TypeSafe Jev via `SystemOneDecisionClient`) ONE yes/no question; `Score.Value` is the returned `P(yes)`, also kept under `Details.Dimensions["decision.probability_yes"]`. Uncalibrated; use beside a judge, not in front of one. See [ADR-033](adr/033-decision-evals-third-evaluator-kind.md). | `atomic-decision` |
| Composite | `CompositeEval` | Runs all sub-evals in parallel via `Task.WhenAll`; aggregates via `IAggregationStrategy`. | `composite` |

All four live in the `AgentEval.Evals` namespace.

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

The composite verdict is determined after aggregation. `warn` is a soft fail: `Passed = false` but `Label = "warn"` distinguishes it from a hard fail. The rows are checked in order:

| # | Condition | `Label` | `Passed` |
|---|-----------|---------|----------|
| 1 | a `Required` component errored | `"error"` — unless the measured parts already decide `"fail"` under the severity rule (no threshold, or `SeverityCapsThreshold`): a high or critical failure, here or inside an errored nested composite, is the verdict whatever the missing part would have scored | `false` |
| 2 | no component produced a measurement | `"error"` if a component the verdict rests on errored (a `Required` one is row 1; any one when none is required); `"warn"` (recorded `notMeasured`) if one of them is a nested composite that withheld its own pass — its parts were measured, but a required part inside did not run; else `"skipped"`. An optional component that errored or withheld is not the verdict, so it reads like an optional skip, here and in a parent | `false` |
| 3 | threshold set, `score >= threshold` | `"pass"` (see rows 6–7); with `SeverityCapsThreshold = true`, required-component severity still decides as in row 5 (`critical`/`high` → `"fail"`, `medium` → `"warn"`) | `true` only on pass |
| 4 | threshold set, `score < threshold` | `"fail"` | `false` |
| 5 | no threshold: severity `critical` or `high` → `"fail"`; `medium` → `"warn"`; `none` or `low` → `"pass"` (see rows 6–7) | as stated | |
| 6 | the label would be `"pass"`, but a `Required` component did not run (`skipped`, or any not-measured result) or is a nested composite that withheld its own pass for that reason (it records `measurement: notMeasured`) | `"warn"` | `false` |
| 7 | the label would be `"pass"`, but fewer than `MinimumMeasuredShare` (default **0.5**) of the components produced a measurement | `"warn"` | `false` |

After the matrix, each component's `EvalComponent.OnFailure` applies to a component that ran and failed: `Fail`
makes the composite `"fail"`, `Warn` turns a `"pass"` into `"warn"` and the summary names the component, and
`Averaged` (the default) leaves it to the score. The effect only escalates, and a component that only warned
passes a warn up, never a fail. The agentic presets set it on every component (see the agentic getting-started
guide, "What a preset's verdict means").

Skipped, inapplicable and errored components are left out of the score, so they never count as 0. Rows 6 and 7 stop
that from turning into a pass on whatever is left. Row 6: a pass cannot rest on a required component that never ran
— components are `Required` by default, so mark a component `Required: false` if the composite may pass without it.
A nested composite tells its parent through its **measurement state**, not its label: one that withheld its pass
this way records `notMeasured`, and one whose required components are all inapplicable records `notApplicable`. A
nested `warn` from a measured medium-severity failure, or from a nested composite's own coverage bar, is a measurement
like any other (the coverage bar is per level). An `inapplicable` component (the case cannot test the thing) never
triggers row 6. Row 7: a composite whose components mostly report "not applicable" cannot pass on
the few that remain; `MinimumMeasuredShare = 0` drops this bar, not row 6. Only a pass is withheld; a measured
failure stays a failure. Whenever components were left out, the result's `Details.Summary` says how many were
measured and why the others were not, and names the required components that did not run — behind a row-6 `warn`,
and under a `warn` or `fail` the measured part decided. Through the CLI,
`warn` exits with code 10 (`GateWarning`), which CI can treat as blocking or not.

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

## Deferred features

The following are deferred until a concrete consumer asks for them:

- `StrictAggregation` — any failure fails the composite (`MinAggregation` covers most use cases today).
- `MedianAggregation` — unweighted median for multi-judge consensus (`WeightedMedianAggregation` covers the weighted case).
- `Predicate` field on `EvalComponent` — conditional component skipping with automatic weight renormalization.
- YAML composite authoring — declare composites in config rather than code.
- Sub-eval result caching — reuse a sub-eval result across multiple composites.
- Streaming events on sub-eval completion — notify callers as each sub-eval finishes.
- Hierarchical calibration — per-level score adjustment for nested composites.

---

## See also

- [Evaluation Guide](evaluation-guide.md) — choosing the right metrics for your use case
- [The `.agenteval/` Workspace](agenteval-workspace.md) — canonical output store, schema versions, audit chain, and `agenteval doctor`
