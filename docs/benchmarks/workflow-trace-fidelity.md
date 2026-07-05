# Workflow Trace Fidelity (Glass Box)

The workflow analogue of [Trace Fidelity](trace-fidelity.md). It answers, **per executor** in a multi-agent
workflow: *does the framework's reported per-executor ledger (tokens + finish reason) match chat-boundary
truth?* Pure code, no LLM tokens (`CostTier.Free`), registered as the `workflow-trace-fidelity` benchmark family.

## What it reconciles

For each executor in a `WorkflowExecutionResult`, the reconciler compares the framework-reported ledger —
built from MAF's per-executor `AgentResponseEvent` (summed tokens, finish reason) — against chat-boundary
truth, when a per-executor chat trace is supplied. It groups steps by executor and **sums** framework tokens
(so a looped executor is not falsely flagged), and reports one verdict per executor:

| Verdict | Meaning | Score |
|---|---|---|
| `Agree` | Framework tokens (within tolerance) and finish reason match chat truth | 1.0 |
| `TokenMismatch` | Framework meaningfully under-reports tokens vs. chat truth | 0.5 |
| `FinishMismatch` | Finish reason differs (incl. a suppressed/`null` framework reason vs. a real one) | 0.5 |
| `Both` | Both diverge | 0.0 |
| `NoTruth` | No chat trace supplied for that executor (ledger-only) | 1.0 (excluded from the aggregate) |

The overall score is the mean over executors that **have** chat truth; `NoTruth` executors are excluded so an
untraced majority cannot dilute a real divergence past the gate.

## Presets

`smoke` / `standard` / `audit-grade` — all `CostTier.Free`; the preset is informational (it does not change
scoring), mirroring the single-agent family.

## Live-workflow caveat

Inside a live MAF `InProcessExecution` workflow, executor responses are **not** routed back through the
instrumented chat client today, so per-executor chat traces come back **without** `ChatTurn` Response entries
(the Glass Box Path-2 upstream-hook gap). Until that hook lands, every executor in a live run is `NoTruth`
(score 1.0); real reconciliation occurs only for **direct-agent / pre-wired / hand-built** traces.

## CLI

```
agenteval bench workflow-trace-fidelity --workflow-trace <file> --subject <name> [--preset standard]
```

Exit codes: `0` clean, `2` discrepancies, `1` setup/IO error.
