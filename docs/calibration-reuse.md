# Reusing runtime judge calibration

A calibration report measures a judge against labelled examples. Its rubric and acceptance policy can be reused across models and languages; its measured accuracy remains evidence about the original judge and cases.

`CalibrationReusePolicy` makes that trade-off explicit:

| Policy | Behavior |
|---|---|
| `RequireMatchingInstrument` (default) | Configured instrument and reported runtime model must match independent calibration. Legacy report/legacy judge pairs retain axis-only compatibility. |
| `AllowSameAxis` | Explicitly accepts a ready independent report for another judge, model or language on the same axis. Provenance labels the evidence `Transferred`; it does not claim newly measured target accuracy. |

Both require a ready source report for the correct axis. A passing configured panel cannot certify an individual member. Missing measurements, failed source calibration and unknown policy values do not become valid approvals through reuse.

## Configure once

Use asynchronous registration to load evidence and install runtime checks before building the agent:

```csharp
using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Gatekeeper;

var builder = await agent.AsBuilder().UseGatekeeperAsync(
    GatekeeperEnforcement.Terminate,
    options =>
    {
        options.CalibrationReportStore = reportStore;
        options.CalibrationReusePolicy = CalibrationReusePolicy.AllowSameAxis;
        options.PostGates.Add(judge);
        // ApprovalGates also support the stock ToolArgumentGoalCoherenceApprovalGate.
    }, cancellationToken);
var protectedAgent = builder.Build();
```

The application owns the decision that the acceptance policy remains appropriate. Matching the axis name does not prove equivalent rubric meaning, translation quality, thresholds or model capability. Reuse never widens deterministic tool permissions.

For independent calibration of a configured `ToolArgumentGoalCoherenceApprovalGate`, pass `approval.CreateCalibrationGate()` to `GateCalibrationHarness`. Each gold-case text is JSON such as `{"toolName":"SearchFlights","arguments":{"destination":"Stockholm"}}`. The adapter uses the approval gate's actual fixed goal, evidence limits and judge path. Malformed or oversized cases remain unmeasured. This avoids calibrating an approximate prompt through a different code path.

Registration performs report-store I/O and **no model calls**. It does not automatically recalibrate or ask for interactive approval. Existing synchronous `UseGatekeeper` remains available without automatic calibration checks. For manual composition, call `ValidateInlineJudgesAsync` before building the pipeline and use its resulting `PreGates`, `PostGates` and `ApprovalGates` registrations. Calling the original unwrapped judge bypasses those checks.

## Read the evidence

`GateVerdict.Provenance.Calibration` records the policy, source calibration time, source/target instrument identities, and known source/runtime model IDs and locales. It distinguishes `Matched`, `LegacyAxisOnly`, `Transferred`, `Unmeasured` and `Rejected`. Reports and their accuracy statistics are not rewritten. Unknown identities remain unknown.

Strict runtime model mismatches suppress the judge decision; tool approval escalates to a human. With explicit reuse, changed or unreported runtime model identities are recorded as transferred evidence. An inconclusive or budget-exhausted judge retains its existing chat failure policy and is never auto-approved. Implementation-owned structural decisions may use matching evidence without claiming a model call.

Per-registration wrappers leave shared judges and reports unchanged. Cached verdicts retain the model evidence from their original call; no new provider observation is claimed for a cache hit. A later cache miss is checked against the actual returned model. Use normal cache invalidation/TTL controls when fresh judgments are required.

Run-gate traces retain content-free calibration records, including for sensitive axes. Stock approval gates expose them through `OnJudgment` and, when configured, `GatekeeperOptions.Trace`. Custom legacy approval gates retain axis-only provenance; configured custom approval gates that hide their observations cannot use this automatic runtime check.

The CLI's certificate workflow remains strict; `AllowSameAxis` is a library registration option. The CLI and library share the observed-model matching rule. `--allow-uncalibrated` remains an advisory bypass, not a claim of transferred calibration.

## Keep verification proportional

Reuse existing labels and unchanged calibration runs. For a new language or judge, a small, budget-capped sample of passes, failures and boundary cases can detect obvious incompatibilities; expand when it finds disagreements or the acceptance policy changes materially. Such a sample is not a full statistical calibration. Explicit transfer does not require running that sample automatically.

Changing the agent being evaluated does not necessarily change the judge. Within the relevant domain, one judge calibration can support evaluation of several agent models. The Swedish samples remain AI-authored fixtures, not human-reviewed customer calibration.
