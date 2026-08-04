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
| Indirect injection | run-pre or inbound A2A run-post | Separate agent-control instructions from ordinary untrusted content |
| Outbound goal drift | A2A run-pre | Compare a delegated instruction with a trusted parent goal |
| Exfiltration intent | run-post | Detect an answer attempting to move protected data outward |
| System-prompt extraction | run-post | Detect disclosure or reconstruction of protected instructions |
| Over-refusal | run-post, advisory | Measure utility damage; do not use as a safety block |
| Crescendo turn shift | shadow | Score one turn's escalation against bounded trajectory context |
| Tool argument/goal coherence | approval | Decide whether arguments are confidently aligned with a fixed goal |

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
