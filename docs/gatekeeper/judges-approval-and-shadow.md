# Judges, approval, and shadow processing

Semantic judgment belongs on a boundary where its latency and uncertainty are explicit. Gatekeeper uses three
different authorities: a calibrated run gate may block the current run boundary, approval may pause a tool for a
human, and a shadow judge may influence only a later run.

## Tribunal judges

`CompositeJudgeGate<TRubric>` wraps one `IJudgeRubric` axis with a prefilter, bounded prompt, parser, timeout, and
fail-closed handling. Keep axes narrow: indirect injection and output exfiltration are different tasks and require
different corpora.

| Axis | Typical boundary | Purpose |
|---|---|---|
| Indirect injection | run-pre; the same axis also guards inbound A2A run-post via `InterAgentBoundaryInjectionGate.CreateInbound` | Separate agent-control instructions from ordinary untrusted content |
| Outbound goal drift | A2A run-pre (`InterAgentBoundaryInjectionGate.CreateOutbound`) | Compare a delegated instruction with a trusted parent goal |
| Exfiltration intent | run-post | Detect an answer attempting to move protected data outward |
| System-prompt extraction | run-post | Detect disclosure or reconstruction of protected instructions |
| Over-refusal | run-post, advisory | Measure utility damage; do not use as a safety block |
| Intent-action mismatch | run-post | Detect a response whose action diverges from the user's stated intent |
| Goal-hijack drift | run-post | Detect the working goal drifting toward an injected objective |
| Ungrounded claim | run-post | Detect assertions not supported by the supplied context |
| Hallucinated citation | run-post | Deterministic citation-existence check plus a judge support check; a bespoke hybrid gate, not on the CLI judge registry |
| Crescendo turn shift | shadow | Score one turn's escalation against bounded trajectory context |
| Tool argument/goal coherence | approval | Decide whether arguments are confidently aligned with a fixed goal |

Inbound inter-agent injection is deliberately not a separate axis: `InterAgentBoundaryInjectionGate.InboundAxis`
reuses `IndirectInjectionJudge.Axis` because it is the same semantic detector applied at the inter-agent response
boundary.

### Calibration is part of the configuration

Calibrate the exact model deployment, rubric, prompt, parser, timeout, temperature posture, and output cap that will
run in production. `GateCalibrationHarness` compares decisive outcomes with reviewed labels and a deterministic
baseline. `IsInlineReady` is necessary, not a generalization claim.

A calibration-set result is not a held-out estimate. For broad promotion, preserve a separate validation split or a
stochastic sweep and record false positives as well as misses. Provider errors and unparseable responses fail closed
under an enforced judge; a matrix of all blocks can therefore look like 50% on a balanced set. Inspect provider
errors before interpreting accuracy.

`CalibrationReport.IsStale` is informational. Callers decide whether staleness blocks promotion.
`ICalibrationReportStore` stores the latest report per axis; a missing axis is never represented as healthy.
`GatekeeperFleetHealthIndex` computes only from observed axes and lists missing/stale axes separately.

### Read the calibration report as a release decision

| Signal | Release interpretation |
|---|---|
| Decisive coverage | How often the judge produced a parseable allow/block instead of an error or inconclusive result |
| Attack recall | Fraction of reviewed attacks blocked; misses are shown separately |
| Benign allow rate | Utility retained; its complement is the false-positive rate |
| Provider/parser errors | Availability failures, not model mistakes; enforced use still follows the configured fail-safe action |
| Baseline delta | Whether the semantic judge adds value over the named deterministic baseline |
| Configuration fingerprint | Proof that the report belongs to the exact rubric, model deployment, and bounded options being promoted |

A report is promotion evidence only when all required signals are present and the deployment's explicit thresholds
pass. Never compress “all provider calls failed and therefore blocked” into a reassuring accuracy number.

## Tool approval

Approval is not sanitization. It decides whether a proposed tool call may proceed to a human or auto-approval path;
tool contracts and validation inside the tool remain authoritative.

| Gate | Auto-approval rule | Safe failure |
|---|---|---|
| `ArgumentPatternApprovalGate` | Arguments are present and positively shown routine | Missing, malformed, matching, or inconclusive arguments escalate |
| `ToolNameApprovalGate` | Tool is not on the sensitive-name list | A sensitive tool escalates even with no arguments |
| `SkillScriptApprovalGate` | Read-only skill operations or an explicitly trusted script | Untrusted script execution escalates |
| `ToolArgumentGoalCoherenceApprovalGate` | Judge is confidently coherent with the fixed goal | Incoherent, timeout, provider error, or parse failure escalates |

Only tools participating in MAF's approval flow reach these gates. Choose one clear posture for Agent Skills:
auto-approval plus deterministic execution policy, or per-script trust plus human escalation. Do not interpret a
native approval pause as evidence that a Gatekeeper execution gate ran.

### Read approval as a decision matrix

| Proposal | Expected disposition | Why |
|---|---|---|
| Routine name and bounded routine arguments | Auto-approve | Positive routine policy matched |
| Sensitive tool name | Escalate | Name policy is authoritative even when arguments look harmless |
| Risky, missing, or malformed arguments | Escalate | Approval requires positive evidence, not absence of a deny match |
| Semantic judge timeout, provider error, or parse failure | Escalate | Uncertainty cannot mint authority |
| Human rejects continuation | Do not execute | The fake-effect counter must remain zero |
| Human approves an escalated continuation | Execute once | The effect must occur only after the resumed approval path |

Sample [28](../../samples/AgentEval.Samples/Gatekeeper/28_GatekeeperApprovalDecisionMatrix.cs) exercises this matrix
offline with measured fake effects.

## Shadow judgment

`ShadowJudgePump` is caller-owned and asynchronously disposable. It uses a bounded queue and one serial consumer so
the returning run does not wait for LLM/network judgment. Queue drops and failures are observable.

A shadow verdict cannot protect the run it analyzes. Its supported pattern is:

1. capture a bounded snapshot after the run;
2. enqueue without blocking the hot path;
3. judge serially with a bounded timeout;
4. arm quarantine or another explicit state transition;
5. let a session gate enforce that state on a later run.

`CrescendoTrajectoryJudge` maintains bounded turn context and a lifetime escalation count in session state. The
rolling summary is bounded, while the arm count intentionally survives turns scrolling out of that window. Provider
errors do not increment the escalation count; an inconclusive model result must not irreversibly quarantine an
innocent session.

## Promotion checklist

- The axis and boundary are named precisely.
- Attack, benign, and hard-negative examples are representative of that boundary.
- Model options and output caps are bounded and recorded.
- Calibration beats the deterministic baseline and satisfies the deployment's miss/false-positive bars.
- The same corpus is not described as independent validation.
- Inconclusive behavior is explicit: block, escalate, or observe—never silent allow.
- Shadow queue drops and later-run semantics are observable.
- Deterministic tool authorization stays active downstream.
