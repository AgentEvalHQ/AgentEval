# ADR-034: Exporters receive the flat report; the result model needs its own export path

- **Status:** **Proposed (2026-10-04).** Option A is recommended now; B is the path when a producer asks for it.
- **Raised by:** [#203](https://github.com/AgentEvalHQ/AgentEval/issues/203) — a third-party exporter for a portable
  results format, built on `IResultExporter` as our docs suggest, could not export what our results actually say.

## 1. Context

`IResultExporter.ExportAsync(EvaluationReport, Stream, CancellationToken)` is the documented extension point for new
output formats. An `EvaluationReport` is flat: per test a name, a 0–100 `Score`, `Passed`, `Skipped`, an error
string, an output string, `MetricScores` (0–100, no per-metric pass flag) and assertions. It carries none of the
result model:

| The result model (`EvalResult`) | In `EvaluationReport`? |
|---|---|
| `MeasurementState` (measured / not applicable / not measured) | no |
| Labels beyond pass/fail: `warn`, `error`, `skipped`, `inapplicable` | no |
| Composite trees: aggregation, sub-results, coverage notes | no |
| Provenance: judge model, prompt id/hash, tokens, cost, cache hit | no |

So an exporter reached through the registry cannot honour "not measured is not failed": a judge outage and a
graded failure both arrive as `Passed = false`.

**The two pipelines.** The report is not a lossy copy of `EvalResult` trees. It comes from a different pipeline:

- **Test pipeline:** `agenteval eval` runs the test harness and `IMetric` metrics, builds a `TestSummary`, converts
  it with `TestSummaryExtensions.ToEvaluationReport`, and hands it to `ExportHandler`, which calls the exporter.
  The memory benchmark's `ToEvaluationReport` is the other producer. **Neither pipeline ever has an `EvalResult`
  tree.**
- **Eval pipeline:** `IEval` / `CompositeEval` / `AtomicLlmEval` / `DecisionEval` and the bench commands produce
  `EvalResult` trees, persisted through `IOutputStore` and described by `eval-result.schema.json` v1 (embedded in
  `AgentEval.DataLoaders`). **No exporter is ever called here.**

**Why not just add a field.** An optional `IReadOnlyList<EvalResult>? EvalResults` on `TestResultSummary` would be
filled by no producer: the only pipeline that calls exporters has no trees to put in it. It would advertise faithful
export that no run delivers — a declared-but-never-run surface, the shape this project has shipped before and had to
retract.

## 2. Options

**A. Document the split (no code).** `IResultExporter` is for the test pipeline's report. Integrators that need the
result model take `EvalResult` trees directly — in process from `IEval.EvaluateAsync`, or from a stored run — and
serialise them against `eval-result.schema.json`. A third-party converter takes that shape as its input (the #203
package does: its `EvalPortDocuments.Build` reads `EvalResult` trees). Cost: docs. Risk: none.

**B. An eval-run exporter with a producer.** A second interface, e.g. `IEvalRunExporter.ExportAsync(EvalRun run,
Stream output, CancellationToken ct)`, where `EvalRun` is run metadata plus, per case, the dataset case, the agent's
output and its top-level `EvalResult`s; a registry like `IExporterRegistry`; and **wired into the bench commands'
output** so the interface has a real caller from day one. Cost: about 1–2 days with tests. Benefit: any format,
including JUnit/TRX for bench runs in CI, gets the full model.

**C. Converge the pipelines.** Make `agenteval eval` run `IEval` and put the trees into the report. Largest; it
overlaps with the eval-interface unification and would change what the existing exporters receive.

## 3. Recommendation

**A now.** It is true today, costs nothing, and removes the trap #203 fell into: the docs no longer imply that a
registered exporter sees the result model.

**B when it has a producer that asks for it** — a user who wants bench output in a CI format, or a second exporter
that needs the model. The `EvalRun` shape is already proven by the #203 package's `EvalPortRun`, which is a reason to
copy its shape, not to depend on it.

**Not C** until the eval-interface unification says what `agenteval eval` should run.

## 4. Acceptance (for B, if chosen)

1. A bench command writes a registered format end to end through the new registry (no test-only caller).
2. The exporter receives `MeasurementState`, every label, the composite tree and provenance; a test fails if any of
   the four is dropped on the way.
3. `docs/export.md` says which interface to implement for which pipeline.
