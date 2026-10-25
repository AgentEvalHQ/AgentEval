# Upgrading from 0.43 to 1.0.0-rc.1

This candidate makes unavailable measurements explicit and uses AEF as the CLI's default run record.
It includes breaking changes before the stable 1.0 promise begins. See the
[maturity policy](../README.md#feature-maturity) and [full changelog](../CHANGELOG.md#100-rc1).

Once the candidate is published, update the library and CLI together:

```powershell
dotnet add package AgentEval --version 1.0.0-rc.1
dotnet tool update --global AgentEval.Cli --version 1.0.0-rc.1
```

## Measurements and comparisons

| Area | What changes | Update your consumer |
|---|---|---|
| Scores, rates, costs and timings | Many public aggregates are nullable, including comparison cost/speed scores, average latency, evaluator overall scores, benchmark summaries and stored scenario values. | Handle absence explicitly in DTOs, arithmetic, sorting and displays. A recorded zero remains a value; an unavailable measurement stays absent. |
| Individual metrics | `MetricResult.Measured` and `NotMeasuredCause` distinguish missing input, failed/unreadable judge calls and other unavailable checks. Some compatibility score fields still contain a placeholder zero. | Check availability before using `Score` or `Passed`; retain the reason. An actually observed empty answer can still be a measured quality failure. |
| Stochastic evaluation | Quality statistics include measured runs only; errored/judge-failed runs remain in execution/reliability accounting. | Use `Statistics`, `MeasuredCount`, `NotMeasuredCount` and `Verdict`. Do not average every `IndividualResults.Score`, including placeholders. |
| Memory baselines | Saved observations retain `IsComplete`, `HasMeasuredScore` and category gaps. Partial snapshots stay saveable; automatic canonical promotion and winner selection require recorded complete evidence. Older snapshots have unrecorded completeness. | Qualify scores before displaying a grade or ranking. Handle a null `BestBaselineId`; refresh an older `report.html` as described in [Memory reporting](memory-evaluation.md). |
| Memory fact diagnostics | A usable numeric score does not establish that every expected fact was found or every forbidden fact was assessed. Fact assertions and derived noise diagnostics disclose unavailable assessments. | Custom `IMemoryJudge` implementations set `MemoryJudgmentResult.ForbiddenFactsAssessed` only when they actually assessed forbidden facts; the runner carries it into `MemoryQueryResult`. Older results default to unrecorded assessment. Numeric score thresholds and averaging are unchanged. |
| Comparison | An unpriced model cannot win as cheapest, and absent latency cannot win as fastest. If a required comparison dimension is unavailable, its relative ranking is withheld. | Inspect `CostNotMeasuredReason`, `SpeedNotMeasuredReason` and `UnrankedReason`. Missing baseline dimensions may have no dictionary entry: use `TryGetValue`. |
| Tool/security evidence | Recorded no calls, missing capture, an attempted blocked call, execution outcome and timing are distinct facts. Reports and qualifying successful confirmations require recorded execution without error. | Keep measured-empty separate from unknown. Set `WasExecuted` on genuine executed-call fixtures. Manually constructed `ToolUsageSummary` instances supply `ExecutedCalls` when known; null cannot certify success. Missing evidence cannot justify a requested security pass; known failures remain failures. |

For example, preserve the metric's availability:

```csharp
var result = await metric.EvaluateAsync(context);
Console.WriteLine(result.Measured
    ? $"Score: {result.Score}; passed: {result.Passed}"
    : $"Not measured: {result.NotMeasuredCause}; {result.Explanation}");

var costLabel = ranking.CostScore is { } costScore
    ? $"{costScore:F1}"
    : "not measured";
```

Stochastic missing runs do not always force an incomplete verdict. A test passes when known passes
already meet the threshold over all planned runs, fails when even every missing run passing could
not meet it, and is otherwise undecided. No measured run yields `NotMeasured`. For example, four
known passes out of five planned runs meet an 80% threshold even if the fifth is unavailable.
The library and shipped demos use `StochasticResult.DecideVerdict` for this rule.

## Workflow recording and timing — breaking

Step counts describe recorded executor activations/agent turns rather than every MAF delivery event.
Update assertions and baselines that counted repeated message/turn-token deliveries as separate steps.
Nested steps retain qualified paths and observed traversal/source identities. Old traces remain readable;
unrecorded outcomes and timings remain unavailable on replay.

Build the root and each declared child workflow with the same instrumentation owner:

```csharp
using AgentEval.MAF;

using var timing = new MAFWorkflowInstrumentation();
// Build declared children with timing.Build(childBuilder) before composing the root.
var workflow = timing.Build(rootBuilder);
var agent = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "Workflow", executorIds);
// Keep timing alive until all evaluations of these workflows finish.
```

Build once and retain normal workflow state across evaluations. `Build` replaces the builder's telemetry
configuration with its dedicated timing source and keeps sensitive payload capture disabled. Producer
spans provide executor timing; unavailable/unattributed spans prevent a complete timing claim.
Time-limit assertions reject unmeasured timing, and incomplete executor-duration totals stay unavailable.
Tool outcome and timing have separate evidence; `ToolStatistics.SuccessRate` is nullable when no outcome
was recorded. See [workflow contracts](workflows.md#maf-event-processing) and
[nested workflows](workflows.md#nested-workflows-and-external-answers).

A tool exception remains an executed failure even when the agent catches it and completes normally.
`TraceRecordingChatClient` keeps partial streaming text and chunks after an early consumer break, but
records complete token usage only after natural stream completion. Accounting consumers treat absent
usage as unavailable, including after trace save/reload.

## Model identity, pricing and reasoning

An explicit `EvaluationOptions.ModelName` selects the pricing model. Otherwise the harness can use
`IModelIdentifiable`; the chat-client adapter takes an explicit request model before client-default
metadata. A display name supplies no missing model identity. Unknown identity/rates or incomplete
usage leave cost unavailable. Cost reports use a dated configured pricing baseline, not a provider invoice.
An observed priced subtotal that already exceeds a performance budget still proves failure, even if
other responses omitted usage. It is labelled a lower bound and does not become the workload's total.

The CLI and shipped sample/demo factories wrap their constructed chat clients with
`MinimalReasoningChatClient`. Without an explicit request level, it tries `none`, then `low` after a
provider HTTP 400/422 rejection, then the provider default if both are rejected. Explicit
`ChatOptions.Reasoning` is preserved. The bounded AEF dataset runner (`agenteval run --plan`) negotiates
the same sequence during readiness, accounting for each attempt against its role's call/cost bounds.
Cases reuse the accepted setting without renegotiating it; each transport call makes one attempt.
Application-supplied library clients are not automatically wrapped; `WithMinimalReasoning()` is
available for opt-in use. See [provider and reasoning behavior](cli.md#environment-variables).

Older baselines may have used a different reasoning policy or judge prompt fingerprint. Check instrument
and evaluation-policy comparability before interpreting a score difference as a regression.
Embedding scores also depend on the selected model: see [embedding configuration and threshold guidance](embedding-metrics.md).

## CLI exits, output and existing results

- Treat exit **11** as incomplete/not measured. Requested named metrics that are unavailable make an
  otherwise successful `eval` exit 11; a known test failure still takes its failure exit. Follow the
  [per-command exit contract](cli.md#exit-codes).
- Model-demo `--mock` runs in Getting Started A1–A5 and Auto-Audit I2 now exit 11 when no real model was
  measured; an actual failed check still exits 1. Code-only offline checks can still complete successfully.
  Benchmark `--sut mock` results also remain stand-in evidence, with exit 11.
- CLI run recording defaults to AEF. `eval --output-dir` now selects an AEF destination. Single-file
  `--output` exports and standalone benchmark reports remain available. Existing library output-store
  APIs are unchanged.
- Use `--aef <directory>` to explicitly require a run destination. Without a workspace or explicit
  destination a command can report that no run was written; `--no-aef` disables recording. Content
  capture defaults off, and `--aef-unsealed` is an explicit exception to default sealing. Standalone
  reports and console output follow their own content behavior.
- Existing legacy folders remain readable through conversion throughout 1.x. Converted custody says
  the original run was not sealed when it executed; a new conversion seal does not establish past custody.
  Missing recorded facts stay unknown, and ambiguous/malformed input may be refused.

```powershell
agenteval eval --dataset cases.json --endpoint $env:MY_ENDPOINT --model $env:MY_MODEL --api-key $env:MY_API_KEY --aef ./evidence
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
agenteval aef verify "./evidence/runs/YYYY/MM/RUN_ID"
```

Replace the quoted run path with the produced run folder. Verification checks integrity/custody requirements;
it does not establish that a judge's conclusion was correct. See [AEF recording and conversion](aef.md#runs-from-eval-redteam-and-bench).
Custom Mission Control/history consumers must also handle nullable scalars and explicit source/container
identities; the viewer remains experimental under the maturity policy.
