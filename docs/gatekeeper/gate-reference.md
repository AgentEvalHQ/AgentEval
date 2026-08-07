# Gatekeeper gate reference

Use this page to choose the right control family. It is intentionally a compact index; detailed behavior, state,
failure modes, and composition rules live in the focused references linked below.

Start with [the introduction](introduction.md) if Gatekeeper is new to you. Use
[the lifecycle guide](gate-lifecycle-and-coordination.md) when several boundaries cooperate.

## Choose by protected boundary

| Need | Start with | Detailed reference | See it |
|---|---|---|---:|
| Deny or constrain local tool execution | `ToolUsageContractGate`, budgets, sequence, taint, allow/deny gates | [Tool and result gates](tool-and-result-gates.md) | [14](../../samples/AgentEval.Samples/Gatekeeper/14_GatekeeperPoisonedToolKillChain.cs) |
| Keep unsafe tool output away from the model | result injection, secret, fixed-size, or anomaly gates | [Tool and result gates](tool-and-result-gates.md#tool-result-gates) | [17](../../samples/AgentEval.Samples/Gatekeeper/17_GatekeeperToolResultAdmission.cs), [29](../../samples/AgentEval.Samples/Gatekeeper/29_GatekeeperToolResultBehavioralAnomaly.cs) |
| Stop redirect, DNS-rebinding, or private-network egress | `GatekeeperHttpMessageHandler` inside the tool's client | [Tool and result gates](tool-and-result-gates.md#http-wire-boundary) | [23](../../samples/AgentEval.Samples/Gatekeeper/23_GatekeeperHttpWireBoundary.cs) |
| Govern run input or final output | deterministic `IChatGate` or a calibrated judge | [Run, session, and state](run-session-and-state.md) | [16](../../samples/AgentEval.Samples/Gatekeeper/16_GatekeeperJailbreakAndToolAbuse.cs) |
| Bind identity, rate, or quarantine across runs | session gates with a host-attested identity | [Run, session, and state](run-session-and-state.md#session-gates) | [20](../../samples/AgentEval.Samples/Gatekeeper/20_GatekeeperStatefulTimeline.cs), [26](../../samples/AgentEval.Samples/Gatekeeper/26_GatekeeperSessionIdentityTakeover.cs) |
| Ask a human before execution | approval gates over MAF approval | [Judges, approval, and shadow](judges-approval-and-shadow.md#tool-approval) | [28](../../samples/AgentEval.Samples/Gatekeeper/28_GatekeeperApprovalDecisionMatrix.cs) |
| Use expensive judgment without blocking this run | bounded shadow pump and later-run quarantine | [Judges, approval, and shadow](judges-approval-and-shadow.md#shadow-judgment) | [25](../../samples/AgentEval.Samples/Gatekeeper/25_GatekeeperCrescendoTrajectory.cs) |
| Isolate a compromised target | signed containment plus override/admission gates | [Containment, coverage, and operations](containment-coverage-and-operations.md) | [22](../../samples/AgentEval.Samples/Gatekeeper/22_GatekeeperSecurityGraphIncident.cs) |
| Isolate local HTTP resource pressure | normal and isolated client pools | [Resource isolation and containment](resource-isolation-and-containment.md) | [19](../../samples/AgentEval.Samples/Gatekeeper/19_GatekeeperBulkheadIsolation.cs) |
| Correlate attacks across sessions | security-graph ingestion, honest compute, containment bridge | [Containment, coverage, and operations](containment-coverage-and-operations.md#security-graph) | [22](../../samples/AgentEval.Samples/Gatekeeper/22_GatekeeperSecurityGraphIncident.cs) |
| Prove structural reachability and decisions | coverage analyzer, trace, telemetry, replay, fingerprints | [Containment, coverage, and operations](containment-coverage-and-operations.md#coverage-and-evidence) | [18](../../samples/AgentEval.Samples/Gatekeeper/18_GatekeeperHostedToolCoverageBoundary.cs), [24](../../samples/AgentEval.Samples/Gatekeeper/24_GatekeeperDynamicContextProviderBoundary.cs) |
| Protect memory recall/write/promotion/lifecycle | memory protection pipeline and adapters | [Memory security](memory-security.md) | [Memory samples](memory-security-samples.md) |

## Gate families at a glance

The usefulness rank asks how much unique value a control adds over the simplest alternative: not granting the
capability, or validating inside the tool itself.

| Rank | Meaning |
|:---:|---|
| **5 — Essential** | Unique, broadly reusable protection with no simpler equivalent |
| **4 — High** | Distinct protection or evidence beyond simpler controls |
| **3 — Situational** | Valuable for a named threat, with meaningful limits or overlap |
| **2 — Supplementary** | Defense in depth; a simpler control is usually stronger |
| **1 — Marginal** | Rarely justified alone |

Rank is not a correctness score. A lower-ranked gate can be exactly right for one deployment.

| Family | Representative controls | Typical rank | Key limit |
|---|---|:---:|---|
| Declarative tool authorization | `ToolUsageContractGate` and strict JSON contracts | 4–5 | Protects declared argument shapes, not behavior inside the tool |
| Cross-call policy | `SequenceGate`, `SameBatchOrderingGate`, budgets, taint | 4–5 | Scope and proposal/execution semantics must match the threat |
| Tool-result admission | injection, secret, size, anomaly, hidden-instruction gates | 2–4 | Runs after the tool effect |
| Deterministic run policy | token injection, PII, rendered-output exfiltration | 2–4 | Pattern and projection limits are explicit |
| Tribunal judges | task-specific `CompositeJudgeGate<TRubric>` axes | 3–4 | Exact model/rubric/options require calibration |
| Session policy | operator authorization, rate, identity drift, quarantine | 3–4 | Identity is only as trustworthy as the host assertion |
| Containment | exact-target store, override, admission, release | 4–5 | Enforces a decision; does not discover compromise by itself |
| Coverage and evidence | coverage analyzer, trace, telemetry, replay | 4–5 | Reachability and observation are not semantic sufficiency |
| Construction drift | prompt, MCP, skill manifests and provenance | 3–4 | Protects reviewed inputs at construction, not runtime mutations outside the pin |
| Memory security | deterministic recall/write/promotion/lifecycle gates | 4–5 | Every real memory path must use a protected adapter |

## Composing gates safely — `UseGatekeeper`

`UseGatekeeper(GatekeeperEnforcement, configure)` is the preferred composition API. It validates the configuration
before partially mutating the builder and installs one coordinated stack.

Do not chain independent `UseAgentEvalToolGate(...)` registrations. MAF middleware wraps in registration order; an
outer registration can stop forwarding and starve an inner gate list. Put all tool and result gates in one
`UseGatekeeper` configuration unless a sample is deliberately teaching one low-level seam.

Run-scope gates are rejected under enforcing modes when run scope is disabled. A control with a `MinimumPolicy`
cannot be silently weakened below that floor. See [lifecycle and coordination](gate-lifecycle-and-coordination.md).

## Shipped Tribunal judges

The shipped semantic axes include indirect injection, outbound goal drift, inbound inter-agent injection,
exfiltration intent, system-prompt extraction, over-refusal, Crescendo turn shift, and tool-argument/goal
coherence. Each axis has its own rubric and promotion evidence; they are not interchangeable generic safety scores.

See [Judges, approval, and shadow](judges-approval-and-shadow.md#tribunal-judges) for placement and calibration.

## Coverage & telemetry

`GatekeeperCoverageAnalyzer` classifies static local functions separately from provider-hosted opaque tools and can
refuse an unprotected high-risk static inventory (the risk classifier is a bounded heuristic the caller may
override). It cannot prove tools contributed later by every dynamic `AIContextProvider`, and acknowledgment does
not inflate structural coverage.

`GateTelemetry` records gate verdict counts and latency. The trace records the action actually applied. Use both:
a gate finding under observation is not an enforced block.

See [Coverage and evidence](containment-coverage-and-operations.md#coverage-and-evidence).

## Extending the Gatekeeper: LLM-backed detection

Prefer a narrow deterministic policy. When semantics are essential:

1. define one decision axis and representative attack/benign corpus;
2. use a bounded rubric, parser, timeout, and output cap;
3. compare the exact configuration with a deterministic baseline;
4. keep it shadow-only until it clears the promotion bar;
5. preserve deterministic authorization downstream.

A run-pre or run-post judge can block the current boundary after promotion. A shadow judge can only affect a later
run. An LLM-backed `IToolGate` is rejected because tool middleware is the latency-sensitive effect boundary.

## Public surface map

This classifies the Gatekeeper API surface by its documentation owner.

| Surface | Examples | Documentation owner |
|---|---|---|
| Composition and enforcement | `UseGatekeeper`, `GatekeeperOptions`, `GatekeeperEnforcement`, resolved options | [Lifecycle and coordination](gate-lifecycle-and-coordination.md) |
| Tool-call contracts and verdicts | `IToolGate`, `ToolGateVerdict`, `ToolGatePolicy`, contract builder/parser/predicates | [Tool and result gates](tool-and-result-gates.md) |
| Tool-result admission | `IToolResultGate`, `ToolResultVerdict`, result gates | [Tool and result gates](tool-and-result-gates.md#tool-result-gates) |
| Run and session controls | `IChatGate`, run gates, session identity and gates | [Run, session, and state](run-session-and-state.md) |
| Approval | `IToolApprovalGate`, approval gates and MAF approval interop | [Judges, approval, and shadow](judges-approval-and-shadow.md#tool-approval) |
| Semantic and asynchronous judgment | calibration, Tribunal gates, shadow pump, Crescendo | [Judges, approval, and shadow](judges-approval-and-shadow.md) |
| Explainability and trust | `GateProvenance`, `GateReplayer`, `TrustScoreCalculator` | [Explainability and trust](explainability-and-trust.md) |
| Containment and resource isolation | store/contracts, signed release, HTTP pool/routing functions | [Resource isolation and containment](resource-isolation-and-containment.md) |
| Security graph | graph models/store/pump/compute/bridge and read-only projection | [Containment, coverage, and operations](containment-coverage-and-operations.md#security-graph) |
| Evidence and assurance | evidence, severity, reference ledger/index, telemetry, fingerprints, replay, coverage | [Containment, coverage, and operations](containment-coverage-and-operations.md#coverage-and-evidence) |
| Egress | HTTP handler/options/DNS resolver/private-network classifier | [Tool and result gates](tool-and-result-gates.md#http-wire-boundary) |
| Memory | memory contracts, deterministic gates, adapters, DI and reports | [Memory security](memory-security.md) |
| Agent Skills | construction checks and execution/approval gates | [Agent Skills](../agent-skills.md) |
| CLI | list, inspect, calibrate, panel, certificates and exit codes | [Gatekeeper CLI](../gatekeeper-cli.md) |
| Experimental surfaces | APIs carrying the Gatekeeper preview diagnostic | Their focused reference, explicitly labelled experimental |
| Internal implementation | canonicalizers, validators, resolved helpers, store serializers | Intentionally not a public usage contract; XML/API docs only |
