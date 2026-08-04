# Containment, coverage, security graph, and operations

This reference owns Gatekeeper's operational assurance surfaces: exact-target containment, cross-session graph
correlation, structural coverage, evidence, telemetry, fingerprints, and replay.

## Containment

Containment is durable enforcement state, not a compromise classifier. A trusted detector or operator creates a
record; `ContainmentOverrideGate` and `ContainedIdentityGate` enforce it.

A `ContainmentTarget` is tenant-bound and names a closed target kind plus bounded identifier. The store exposes
explicit `Active`, `Released`, `NotContained`, and `Indeterminate` outcomes. `MustBlock` is true for active and
indeterminate states: unreadable or unverifiable containment never becomes an allow decision.

`JsonFileContainmentStore` is the strict single-process implementation behind `IContainmentStore`. It owns an
exclusive sidecar lock, validates bounded schema-v1 JSON, performs serialized compare-and-swap mutations, flushes a
temporary file, and atomically replaces the durable state. Deployments needing multiple writers must implement the
interface with equivalent atomicity and tenant isolation.

Release requires `ContainmentReleaseAuthorization` verified by caller-supplied authority. Canonical signing bytes,
bounded expiry, expected version, and durable nonce replay protection make release explicit. Containment has no
silent TTL expiry.

See [Resource isolation and containment](resource-isolation-and-containment.md) for routing contained identities to
separate HTTP resources.

## Security graph

The security graph correlates privacy-minimized observations across sessions without storing prompts, arguments, or
responses.

| Component | Responsibility |
|---|---|
| Graph observation models | Carry bounded tenant/session/agent/MCP/endpoint facts and coverage-gap markers |
| `SecurityGraphIngestionPump` | Caller-owned bounded queue, one serial durable consumer, linearizable completion |
| Graph store | Tenant-bound persistence, retention, atomic replacement, keyed session digests |
| `AgenticSecurityGraph.Compute` | Immutable correlation that never fabricates a score for an unobserved node |
| `SecurityGraphContainmentBridge` | Maps only approved complete report facts into the Phase-3 containment lifecycle |
| Mission Control projection | Read-only totals, truncation, and structured privacy-minimized facts |

Queue drops, capacity failures, and write conflicts become durable coverage gaps. Incomplete coverage cannot produce
a healthy claim or a global containment fact. The bridge is fixed to one tenant and does not introduce a second
store, threshold model, or mutation authority.

## Coverage and evidence

### Coverage analyzer

`GatekeeperCoverageAnalyzer` answers whether reported tools are structurally reachable by local middleware.

- `InterceptedLocalFunction`: a local `AIFunction` that tool gates can observe.
- `ProviderHostedOpaque`: execution owned by the provider and invisible to local tool middleware.

`AnalyzeOrThrow` can refuse an unprotected high-risk static tool. The risk classifier is a bounded heuristic and may
be overridden by the caller. Static analysis cannot prove tools added later by every dynamic `AIContextProvider`,
nor inspect arbitrary operation names hidden inside a generic dispatch schema. Risk acknowledgment records an
accepted gap; it never increases structural coverage.

### Evidence and references

`GateEvidence` records policy, stage, decision, action, severity, correlation, and bounded metadata. Reasons and
metadata must exclude secrets, raw arguments, stable identities, taint values, and containment keys.

`GateReferenceLedger` assigns opaque references for operator follow-up. `GateReferenceIndexAggregator` provides
bounded counts and severity projections; it is not a payload store.

`GateTelemetry` records invocations, verdict counts, and latency. A verdict is what the gate found. The trace action
is what enforcement did. Under observation, a block verdict with action `Warn` is not a prevented effect.

### Configuration fingerprints and receipts

`GateConfigFingerprint` binds receipts and replay evidence to frozen behavior. Stateful gates and declarative
contracts contribute their bounded configuration. A changed policy must not reuse the old configuration identity.

`RunReceipt` summarizes the applied configuration and decisions without copying sensitive payloads. Mutation
capture defaults to redacted. Full argument capture is explicit and suitable only for non-sensitive fixtures.

### Replay

`GateReplayer` evaluates captured calls against baseline and candidate gate lists using the same allow/block/mutate,
throwing-gate, and fixed-point semantics as the live loop. Reports are deterministic and secret-minimizing by
default. Replay validates counterfactual policy behavior; it does not reproduce a tool side effect or prove a
provider-hosted capability was intercepted.

## Construction drift and provenance

The following are construction checks rather than per-request runtime gates:

- `PromptTemplateDriftGate` pins reviewed prompt templates.
- `McpToolDescriptionPoisoningGate` pins MCP tool description/schema material.
- `McpServerProvenanceGate` requires explicit caller-supplied server identity and collision-safe qualified names.
- `SkillGateConstructionCheck` validates Agent Skills manifests/content under the selected mode.

Missing provenance is never inferred from a tool-name prefix or model-visible text. Half-configured baseline/current
pairs fail construction rather than silently skipping validation.

## Operator checklist

- Give every durable store one tenant boundary and one owner.
- Treat active and indeterminate containment as blocked.
- Persist release nonces and require compare-and-swap version checks.
- Bound retention, queues, reports, and read-only projections.
- Preserve coverage gaps through ingestion and computation.
- Compare telemetry verdicts with trace actions.
- Re-run coverage after dynamic composition.
- Replay candidate policy before promotion, then roll through observation before enforcement.
- Keep operations surfaces read-only unless a separately authenticated mutation path is explicitly designed.
