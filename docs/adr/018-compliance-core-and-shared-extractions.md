# ADR-018: Compliance.Core and Cross-Cutting Shared Extractions

## Status

**Accepted** — delivered on branch `fix/thorough-review-findings` as part of the thorough-review hardening wave (no release version cut yet). Extends [ADR-016](016-monolith-modularization.md) (the original monolith→multi-project split).

## Date

2026-05-31

## Context

A repository-wide thorough review flagged a cluster of **DRY / SOLID architecture** issues (the `ARC-*` findings) where the same logic was reimplemented across assemblies and could silently drift. The most acute:

- **ARC-01** — the GDPR and EU AI Act compliance packs are near-verbatim parallel hierarchies (loader, validator, builders, reporters, PDF renderers, model records). A fix to shared mechanics had to be made twice and had already drifted.
- **ARC-02/03/04/05/07/08/11, MNT-02/03/05** — duplicated tree-walk depth caps, PDF helpers, calibration statistics, tool-call assertion checks, model-pricing key matching, OWASP/MITRE leaf scoring, workspace-root discovery, and the memory god-class.
- **ARC-10** — the umbrella re-declares each embedded sub-project's external `PackageReference`s by hand (because `PrivateAssets="all"` suppresses transitive propagation), with **no compile/CI signal** when a sub-project adds a dependency the umbrella forgets to mirror (SEC-02 was a concrete instance).

The hard constraint: the compliance gates were carefully calibrated over a prior arc (GDPR 5/5; EU AI Act with documented carve-outs — Pillar 1 / Art 5 at 0.65/0.35, GPAI at 0.60/0.25). **No refactor may change any scoring weight, pass threshold, pillar/article definition, aggregation rule, or judge constant.**

## Decision

1. **Create `AgentEval.Compliance.Core`** — a new embedded sub-project holding the regulation-neutral building blocks shared by both compliance packs. The wave seeds it with the provably-identical, self-contained, calibration-neutral types (`CompositeExtensions`, `Recommendation`, `CriticalFindingExtractor`); the remaining entangled files (models, loaders, validators, builders, runners, renderers, calibration) migrate into it incrementally as test-gated follow-ups.

2. **Consolidate cross-cutting logic into single owners**, each close to its domain and validated by the existing test suites (behaviour preserved, not changed):
   - `EvalTreeLimits` (Abstractions) — one tree-walk depth cap referenced by the producer and every consumer.
   - `EvalReportHelpers` (Abstractions) — shared QuestPDF-free report helpers.
   - `ModelKeyMatcher` (Abstractions) — one longest-first model-pricing key matcher.
   - `CalibrationMath` (Core) — shared judge/evaluator statistics.
   - `WorkflowToolCallChecks` (Core) — shared order-independent tool-call assertion checks.
   - `AgenticCategoryResolver` (Evals.Agentic), `RedTeamComplianceLeaf` (RedTeam), `MemoryScenarioContextBuilder` (Memory), `WorkspaceRootDiscovery.CanonicaliseExistingDirectory` (DataLoaders).

3. **Add `UmbrellaDependencyClosureTests`** — a build-time guard that parses the umbrella + every embedded sub-project `.csproj` and fails when a sub-project runtime `PackageReference` is not re-declared on the umbrella. This is the missing compile/CI signal for ARC-10.

4. **"Safe extraction + documented residual" for the large ARCs.** Where a complete merge would be high-risk on calibrated/tested code (ARC-01 full pack merge, ARC-02 layout skeletons, ARC-05 fluent-builder unification), extract only the provably-safe, behaviour-preserving slice now and record the deeper structural merge as a tracked vNext residual rather than forcing a risky change.

## Consequences

**Positive**
- Shared logic has one owner; a fix lands once. The most drift-prone duplications (depth cap, calibration math, leaf scoring, pricing match) can no longer diverge.
- The umbrella's NuGet dependency closure is now guarded — a forgotten transitive dependency fails a test instead of shipping a broken/silently-vulnerable package.
- Zero behaviour/calibration change: the GDPR/EU-AI-Act compliance tests and the full suite pass identically; an independent adversarial re-review confirmed the wave is behaviour-preserving and calibration-safe.

**Negative / costs**
- One more embedded sub-project (`AgentEval.Compliance.Core`) in the umbrella's `PrivateAssets="all"` list (and in the closure guard).
- `Compliance.Core` is intentionally a *partial* extraction; the compliance packs still hold near-identical model/builder/runner files pending the incremental migration documented per finding.
