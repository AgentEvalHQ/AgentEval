# Run, session, and state reference

This page owns run/session gates and the state-lifecycle contract for Gatekeeper. State scope is part of security:
using process state where run state was intended can leak decisions across users, while losing durable state at a
restart can silently remove containment.

## Lifecycle map

```text
one proposal/batch
       ↓
run ledger ── resets at the end of the root run
       ↓
logical session / rate window ── survives object reload; expires or is explicitly cleared
       ↓
process-owned state ── survives runs, not process restart
       ↓
durable containment / graph / calibration report ── survives reopen; explicit retention or release
```

Choose the narrowest lifetime that still governs the threat. A longer lifetime is not automatically safer: it can
leak decisions across users or make recovery impossible.

Run gates implement `IChatGate` and inspect text at a run-pre or run-post boundary.

| Control | Typical placement | Purpose |
|---|---|---|
| `TokenInjectionGate` | run-pre | Reject configured direct injection markers |
| `RegexPiiGate` | run-pre or run-post | Detect supported PII shapes with bounded regex work |
| `SafetyMetricGate` | run-pre or run-post | Convert a deterministic evaluation into a boundary decision |
| `RenderedOutputExfilGate` | run-post | Reject rendered-output exfiltration such as image beacons |
| `CompositeJudgeGate<TRubric>` | run-pre or run-post after calibration | Apply one bounded semantic axis |
| `InterAgentBoundaryInjectionGate` | A2A run-pre/run-post | Detect outbound goal drift or inbound indirect injection; axis and calibration details live in [Judges, approval, and shadow](judges-approval-and-shadow.md#tribunal-judges) |

A run-pre gate cannot govern tool arguments it has not seen. A run-post gate cannot undo tool effects. Keep
tool-call authorization independent of run-level attack detection.

## Session gates

Session gates evaluate a host-supplied `SessionContext` before a run.

| Gate | Purpose | Honest limit |
|---|---|---|
| `OperatorAuthGate` | Allow only configured operator identities | The host must attest the identity |
| `RateLimitGate` | Bound runs per logical session and window | Default state is in-process; distributed hosts need shared enforcement |
| `SessionIdentityDriftGate` *(experimental)* | Bind the first admitted actor and reject later drift | Cross-process deployments need a shared binding store |
| `QuarantineGate` | Refuse a session armed by shadow judgment | Protects a later run, not the run that produced the finding |
| `QuarantineLeaseGate` | Enforce a bounded signed/authorized lease | Lease authority and clock are part of the trust boundary |
| `ContainedIdentityGate` | Reject identities linked to active/indeterminate containment | Requires authoritative target resolution and store availability |

Configure one stable `SessionIdentity` on `GatekeeperOptions`. Object identity or a model-supplied user name is not
a trustworthy partition key.

## State ownership and lifecycle matrix

| Mechanism | Boundary | Scope | Owner/store | Partition key | Concurrency | Reset/release | Restart | Missing-scope fallback | Fingerprint/evidence |
|---|---|---|---|---|---|---|---|---|---|
| `RunBudgetGate` | tool call | run | `RunLedger` in `AgentRunScope` | gate dimension + tool/argument | atomic ledger operation | end of run | resets | enforcing composition requires run scope | configured limits; bounded reason/evidence |
| `MonetaryLimitGate` | tool call | run | isolated `RunLedger` dimension | gate + argument | atomic | end of run | resets | requires run scope | cap and argument name, not attempted value |
| `PerToolCallBudgetGate` | tool call | run | isolated `RunLedger` dimension | gate + tool | atomic | end of run | resets | requires run scope | per-tool caps |
| `SequenceGate` | tool call | run | proposal history in `RunLedger` | gate + trigger/guarded sets | ledger-serialized | end of run | resets | requires run scope | configured sets; proposal observation |
| `forbiddenIfPrecededBy` contract predicate | tool call | run | contract proposal history | contract fingerprint + tool/predicate | atomic check/record | end of run | resets | contract fails closed when required state is unavailable | contract fingerprint |
| distinct-value contract predicate | tool call | run | bounded hash sets in `RunLedger` | contract/tool/kind/argument | atomic admit/exceed | end of run | resets | requires run scope | hashes/count only |
| `SameBatchOrderingGate` | tool call | current model batch | none; reads latest assistant call batch | current call history | stateless | each batch | n/a | absent history means no sibling proof | trigger/guarded policy |
| `TaintTrackingGate` | tool call | run | weak table owned by gate instance, keyed by `AgentRunScope` | gate instance + run | per-run lock; result-id dedup | end/collection of run scope | resets | stateless recomputation from supplied history | never emits tainted values |
| `ToolResultSizeAnomalyGate` | tool result | run | `RunLedger` per-tool size baseline | run + tool | atomic record-unless-anomalous | end of run | resets | uses current-run fallback ledger; compose with run scope for isolation | count/average/multiplier; no result body |
| `BlockStormSentinelGate` | tool call | root run tree | root `RunLedger` denial count and one-shot latch | root run scope | atomic latch | end of root run | resets | requires real composite ledger for meaningful signal | count/incident reference |
| `RateLimitGate` | session admission | session/window | gate-owned in-process state | stable logical session | race-safe update | window expiry | resets unless host persists externally | missing session fails closed | count/window, content-free |
| `SessionIdentityDriftGate` | session admission | session | session binding state/resolver | stable session independent of actor | bounded synchronized binding | explicit session lifecycle | in-process binding resets unless shared store supplied | missing/untrusted identity fails closed | identity status, not raw identity |
| `QuarantineGate` / `CrescendoTrajectoryJudge` | later run | session lifetime | session `StateBag`; shadow pump serializes judgment | stable session | one shadow consumer; state update serialized | explicit clear/new session | depends on session persistence | missing session never fabricates durable quarantine | content-free turn/threshold state |
| `QuarantineLeaseGate` | session admission | lease | caller-owned lease authority | stable session + lease id | authority-defined | signed/authorized release or expiry rules | durable only if authority persists | missing/invalid lease fails closed | lease status/reference |
| Containment | run/tool admission | durable | one caller-owned `IContainmentStore` | tenant + target kind + target id | store CAS/idempotent mutation | signed, nonce-protected operator release; no silent expiry | persists | unreadable/indeterminate blocks | target digest/reference; no payload |
| Security graph ingestion | after observations | durable | bounded caller-owned pump + tenant-bound store | tenant + content-free observation keys | one serial consumer; linearizable completion | retention and atomic replacement | persists | drops/conflicts create durable coverage gaps | coverage and opaque facts |
| Security graph compute | operations/policy bridge | durable report | `AgenticSecurityGraph` over store snapshot | fixed tenant | immutable computation | retention/new observations | recomputed from durable state | missing/incomplete coverage cannot claim healthy | structured privacy-minimized report |
| Calibration latest report | promotion/operations | durable optional | `ICalibrationReportStore` | judge axis | atomic latest-report replacement | next reviewed calibration | persists when JSON store is used | absent axis is never passing | model/rubric/options report metadata |
| Gate reference index | operations | process/durable projection | `GateReferenceLedger` and aggregator | opaque reference id | bounded ledger/aggregation semantics | retention configured by owner | owner-dependent | missing evidence stays unknown | reference id, severity and counts |

## Scope rules

- **Call/batch state** is derived from the current proposal and cannot remember earlier runs.
- **Run state** must be keyed by `AgentRunScope`. `UseGatekeeper` establishes it by default and refuses known unsafe
  enforcing compositions when it is disabled.
- **Session state** requires a stable host-attested key. A process object reference is not sufficient across reloads.
- **Process state** is acceptable only when the documented deployment is one process and reset semantics are clear.
- **Durable state** needs tenant binding, bounded retention, integrity, atomic mutation, and an explicit release path.
- **Coverage gaps are state.** A dropped observation or unreadable store must remain visible; it must not disappear
  into a healthy score.

## Taint and anomaly corrections

`TaintTrackingGate` is not purely stateless in normal composition. It uses incremental per-run state keyed by the
active run scope, then falls back to stateless history reconstruction only when no scope exists.

`ToolResultSizeAnomalyGate` is per tool **and per run**, not per session. Its baseline starts fresh for a new run.
An anomalous result is excluded from the future baseline so repeated attacks do not normalize themselves.

## Promotion checklist for stateful controls

1. Test two runs and two sessions so isolation is observable.
2. Test concurrent sibling calls against the atomicity claim.
3. Test missing scope/identity/store and verify the documented fail-closed or conservative fallback.
4. Test reset, release, retention, and restart behavior.
5. Keep payloads, secrets, stable identities, and containment keys out of reasons and telemetry.
6. Bind replay/receipts to the frozen configuration fingerprint.
