# ADR-025 — Gatekeeper: runtime fail-closed enforcement middleware

- **Status:** Accepted (2026-07-05). Implemented in `src/AgentEval.MAF/Gatekeeper/` + `src/AgentEval.RedTeam.Gatekeeper/`.
- **Relates to:** ADR-019/020 (Glass Box dual-boundary trace — Gatekeeper records its evidence there), the shipped chat-gate primitives (`IChatGate` / `EvalGatePolicy`), ADR-021→024 (the red-team oracles the "moat" gates reuse).
- **One-line:** evaluation and red-teaming find problems *after the fact*; Gatekeeper puts the same checks **in the request path** so a forbidden tool call, poisoned argument, or compromised conversation is **blocked before it happens** — and it is **fail-closed by construction**, with the expensive checks moved off the hot path.

## Context

AgentEval could *measure* an agent (Glass Box, red-team) but not *stop* it. Runtime enforcement is a different problem with two failure modes that a naïve middleware gets wrong:

1. **Fail-open.** A gate that throws, times out, or can't read its context, and then lets the action through, is worse than no gate — it gives false assurance. MAF's `FunctionInvokingChatClient` (FICC) makes this easy to get wrong: a thrown callback is swallowed into a tool-error result and the loop proceeds; a null return is fabricated into `"Success: Function completed."`.
2. **Cost on the hot path.** The most useful checks (an LLM-as-judge, a package-reputation lookup) are exactly the ones you cannot afford to run on *every* tool call — they stall the agent and, run inline, invite a fabricated verdict under time pressure.

## Decision

A layered, fail-closed middleware over the MAF agent pipeline, on two load-bearing principles.

**1. Cannot-inspect ⇒ deny.** Every gate is fail-closed by construction: a gate that throws is caught and treated as a **Block** (never swallowed into a proceed); a session gate that can't read its context **Blocks**; a run-post gate that cannot inspect a stream under a blocking policy throws at stream start rather than let output through. A block always returns a non-null refusal, so it can never surface as MEAI's `"Success"` fabrication.

**2. Honest evidence.** Every enforcement decision is recorded into the same [`AgentTrace`](../glass-box.md) the evaluators read, under `gate.*` keys. A gate that only *warns* records `action="Warn"` — it is **never** counted as a block, so `GlassBoxEvidence.GateBlockCount` can never claim it stopped a call that actually ran.

**The layers, split by cost budget:**

| Layer | Seam | Cost budget |
|---|---|---|
| Tool gate | each tool call, pre-execution | pure-code / bounded |
| Run gate | run input (incoming-attack) + output | pure-code / bounded |
| Session gates | before a run (auth / rate / quarantine) | pure-code |
| The moat | a tool call (red-team probes / canaries as gates) | pure-code / bounded |
| Shadow judge | *after* the run, async | **anything — LLM, network** |

Inline gates **reject** `GateCost.Network`/`Llm` at construction (`GateCost` drives inline-vs-shadow). The **shadow judge** is the release valve: it runs after the run returns, on an owned bounded-queue pump, over an immutable snapshot — **never the live trace** (so it can't race the returning run's serialization) — and an adverse verdict **arms quarantine** so a *later* run fails closed, rather than blocking the run it observed.

**The moat inverts the grading rule.** `ProbeEvaluatorGate` runs a deterministic red-team oracle as a runtime gate. Where grading must *never* punish honesty (ADR-021→024: abstention is not a failure), a runtime gate must do the **opposite** — only a clear *Resisted* verdict allows the call; *Succeeded* **and** *Inconclusive* both Block. A gate that cannot prove a call safe must not run it. LLM-backed evaluators are rejected (an `IChatClient` reachable anywhere in the evaluator tree), keeping the moat on the pure-code budget.

**A separate bridge assembly.** The moat gates (`ProbeEvaluatorGate`, `CanaryToolGate`) live in `AgentEval.RedTeam.Gatekeeper`, referencing both `AgentEval.MAF` and `AgentEval.RedTeam` — so the core MAF package never takes a dependency on the (heavier) red-team assembly. No cycle: neither parent references the other.

## Consequences

- Enforcement is composable and independent per layer; adding a gate under the default `WarnOnly` never changes an agent's behavior (opt into `ReplaceResult`/`Terminate` for prod).
- The closed loop is demonstrable end-to-end: the shipped red-team attack suite runs against a Gatekeeper-gated agent (wrapped as an evaluable SUT) and composes with the `--baseline`/`--fail-on regression` gate — a probe that gets *through* a gate becomes a new conclusive failure (attack-the-gate CI).
- Workflow-level enforcement is not covered: MAF exposes no workflow-middleware seam to hook (tracked upstream at `microsoft/agent-framework#3075`).
- Each milestone was reviewed adversarially to convergence and through the Copilot review loop before merge; the fail-closed posture caught real defects (a JSON-escape fail-open, a streaming bypass, a composed-LLM-grader gap) that a contract/test alone would have missed.
