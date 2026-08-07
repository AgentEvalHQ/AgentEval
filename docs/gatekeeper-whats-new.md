# Gatekeeper capability history

> **Current status:** see [Implementation status](gatekeeper/implementation-status.md).
> This page is a compact history, not the API reference or release-readiness record.

Gatekeeper evolved from individual runtime gates into a coordinated protection and assurance layer. New users
should start with the [introduction](gatekeeper/introduction.md), [recipes](gatekeeper/examples.md), and
[gate reference](gatekeeper/gate-reference.md).

## Current baseline — 2026-08-07

### Coordinated composition

- `UseGatekeeper` is the preferred multi-layer entry point and requires an explicit enforcement mode.
- Run, tool, result, approval, shadow, evidence, and memory surfaces share validated composition.
- Unsafe middleware ordering, weakened policy floors, and uncalibrated inline judges refuse promotion.

### Discoverability and CI assurance

- The launcher opens group J on a six-sample 15-minute tour (00/04/10/14/16/23) with ID-prefixed names, named
  learning paths behind **P**, and all 29 menu entries behind **M**.
- Samples print a compact two-line threat/guarantee contract by default; `AGENTEVAL_GATEKEEPER_SHOW_CONTRACTS=true`
  prints the full audited contract.
- All 28 offline-capable samples execute non-interactively in CI on every pull request via
  `--gatekeeper-offline-suite`; sample 10 gained a deterministic replay + trust oracle.

### Runtime and construction protection

- Declarative tool contracts, budgets, sequencing, taint, same-batch ordering, and containment protect local effects.
- Result injection, secret, fixed-size, and behavioral-anomaly gates protect model-context admission.
- HTTP redirect and DNS validation operate inside the tool client at the wire boundary.
- Prompt, MCP, Agent Skills, and coverage checks refuse unsafe or unverifiable construction.

### Stateful operations

- Run, session, rate-window, shadow-quarantine, and durable containment ownership is explicit.
- The security graph correlates privacy-minimized observations and preserves coverage gaps.
- Mission Control exposes a bounded read-only projection rather than a second mutation authority.
- Bulkhead routing separates local normal and isolated HTTP pressure while documenting shared downstream quotas.

### Memory security

- One composite memory configuration protects tool, MCP, `AIContextProvider`, and provider-native seams.
- Host identity remains authoritative; coverage levels never upgrade an unrelated operation.
- Eight offline release fixtures cover scope, lifecycle, hosted limitations, quarantine, and rollback.

### Documentation and samples

- The public reading path separates introduction, selection, focused references, lifecycle, operations, and status.
- A strict manifest synchronizes 30 sample contracts with sources, launcher registration, and the catalog.
- The launcher presents six recommended samples first and reveals the complete 29-entry menu on demand.
- Architecture and specialist samples cover state, concurrency, wire, graph, dynamic-provider, identity, approval,
  provenance, Crescendo, and result-anomaly boundaries.

## Earlier milestones

### Explainability and trust

- `GateProvenance` added structured reasons, thresholds, and contributing evidence.
- `GateReplayer` added deterministic counterfactual policy comparison.
- `TrustScoreCalculator` added availability-aware aggregation that excludes missing and errored signals.

### Semantic judgment

- `CompositeJudgeGate<TRubric>` established narrow Tribunal axes with bounded parsing and fail-closed behavior.
- Confidence propagation and fleet correlation preserved near-miss signals rather than laundering them into clean allows.
- Calibration certificates and reports bound promotion to the exact model, rubric, options, and corpus.

### Skills and cross-agent boundaries

- SkillGate applied construction-time manifest/content integrity to Agent Skills.
- Inbound and outbound A2A judges added separately calibrated delegation and remote-response boundaries.
- Real A2A endpoint promotion remains explicitly external and authorization-gated.

## Maintenance rule

Every new Gatekeeper capability must update one reference owner, executable evidence, and the implementation-status
record in the same change. A historical entry is useful only after those three current sources are accurate.
