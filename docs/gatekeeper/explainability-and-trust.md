# Explainability & Trust

Three primitives that make a Gatekeeper decision reconstructable — WHY a gate decided what it decided, WHAT a
different configuration would have decided against the same traffic, and HOW to combine several signals into
one honest composite score. Shipped 2026-07-19 as library APIs, each independently tested; none has a CLI
command yet (see each section's "Status" note) — the mechanism is real and testable today from code, and a
CLI wrapper for each is a bounded, low-risk follow-on.

> **Honest scope note.** These are additive to the existing gate primitives documented in the
> [gate reference](gate-reference.md) and [examples](examples.md) — nothing here changes how an existing gate
> behaves. `GateVerdictDto`, the JSON contract `gatekeeper inspect` emits, is a **frozen v1 schema** (see its
> own remarks) that does not yet surface `Confidence` or `Provenance` — both exist in the C# `GateVerdict`
> today, but reaching them from the CLI's JSON output needs a deliberate, versioned schema bump, not done here.

## Gate provenance chains

`AgentEval.Guardrails.GateProvenance` is a reconstructable "why" behind a `GateVerdict` — richer than the
existing free-text `Reason` string: which rule fired (`RuleName`), what evidence it saw (`Evidence`), and,
for a threshold-based gate, the threshold it was compared against versus the actual value observed
(`Threshold`/`ActualValue`). A `Contributing` list lets a gate that aggregates other gates' findings attach
their provenance chains too — not populated by anything yet (no aggregating gate is wired), but the shape is
there for one.

Attached via a new, optional `GateVerdict.Provenance` field — additive, same precedent as the existing
`Confidence` field: a gate that doesn't populate it behaves exactly as before.

**Wired into:** `CompositeJudgeGate<TRubric>` (the Tribunal primitive — see [gate reference](gate-reference.md)),
for both the Block path and the near-miss-Allow-with-Confidence path Fleet Correlation already reads.

```csharp
var verdict = await judgeGate.InspectAsync(text);
if (verdict.Provenance is { } why)
{
    Console.WriteLine($"Rule: {why.RuleName}");
    Console.WriteLine($"Threshold {why.Threshold} vs actual {why.ActualValue}");
    Console.WriteLine($"Evidence: {string.Join(", ", why.Evidence)}");
}
```

**Status:** wired into one gate (`CompositeJudgeGate`). Deterministic (regex/keyword) gates and
`FleetCorrelator`-level chain aggregation across a whole session are natural follow-ons, not done yet.

## Counterfactual gate replay

"What would a DIFFERENT tool-gate configuration have done to this SAME captured traffic?"
`AgentEval.MAF.Gatekeeper.GateReplayer.CompareAsync` runs a baseline and a candidate list of the REAL
`IToolGate` objects — not a simulation — against the same captured `GatedToolCall`s, using the identical
sequential, first-Block/Mutate-wins semantics the live `UseAgentEvalToolGate` pipeline applies. A divergence
found here is exactly what would have happened had the candidate configuration been live at capture time.

```csharp
var comparison = await GateReplayer.CompareAsync(capturedCalls, baseline: currentGates, candidate: proposedGates);
foreach (var row in comparison.Diverged)
{
    Console.WriteLine($"{row.Call.FunctionName}: {row.Baseline.Action} -> {row.Candidate.Action}");
}
```

Tool gates are pure/bounded by construction (`GateCost.PureCode`/`GateCost.Bounded` — `UseAgentEvalToolGate`
itself refuses `Network`/`Llm` gates inline), so replaying them against already-captured calls needs no
network call and no live agent.

**Status:** library API only. Getting `GatedToolCall`s to replay against currently means capturing them
yourself (e.g. from a `AgentTrace`, or reconstructing them from a `--capture-fixture` JSONL capture — see
[CLI Reference](../cli.md#agenteval-log-file)). A `agenteval log-file gate-replay` command wiring this
directly to a capture file is the natural, mechanical next step — not built yet.

## Unified Trust Score

A single honest composite across gate verdicts and eval scores. The naive approach — average everything,
including gaps — is exactly the trap `WeightedSumAggregation`'s own comment warns against: *"including
\[skipped/error] at 0.0 would incorrectly drag the composite below threshold."* `AgentEval.Trust.
TrustScoreCalculator.Compute` applies the same exclusion discipline already used across this repo's
aggregation strategies (`WeightedSumAggregation`/`WeightedMedianAggregation`/`MinAggregation`/
`MajorityVoteAggregation`) to a cross-cutting mix of signal SOURCES, not just sub-evals of one eval tree.

```csharp
var signals = new[]
{
    new TrustSignal("gate:injection", Score: verdict.Action == GateAction.Allow ? 1.0 : 0.0, Weight: 2),
    new TrustSignal("eval:groundedness", Score: evalResult.Score.Value, Weight: 1),
    new TrustSignal("eval:timed-out", Score: 0.0, Weight: 5, Label: "error"),   // excluded, not zero-scored
};
var trust = TrustScoreCalculator.Compute(signals);
Console.WriteLine($"{trust.Explanation}");   // "Composite trust score 87/100 from 2/3 signal(s) measured; excluded: eval:timed-out (error) (never scored as distrust)."
```

A signal's `Label` uses the same `"measured"`/`"skipped"`/`"error"` vocabulary `EvalScore.Label` already
uses — a `"skipped"` or `"error"` signal is excluded from the weighted math entirely, never scored at 0.0. A
missing/excluded signal is never silently treated as fully trusted either — `SignalsMeasured`/`SignalsTotal`
report exactly how much of the intended signal set actually contributed, and `Score` is `null` (not `0`)
when nothing could be scored at all.

**Status:** library API only, no CLI/report wiring. There is no built-in helper yet to turn a `GateVerdict` or
an `EvalResult` into a `TrustSignal` automatically — the caller constructs the list today.

## Related

- [Gate reference](gate-reference.md) — every built-in gate, including `CompositeJudgeGate`'s Tribunal role.
- [Examples](examples.md) — the general `UseGatekeeper(enforcement, configure)` wiring pattern.
- [CLI Reference — Exit codes](../cli.md#exit-codes) — the BUG-22 exit-code split (`9`/`10`/`11` for
  benchmark gate outcomes) this same session also shipped, a related but separate honesty fix.
