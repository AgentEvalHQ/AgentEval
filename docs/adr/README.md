# Architecture Decision Records (ADRs)

This folder contains Architecture Decision Records documenting significant technical decisions made in the AgentEval project.

## What is an ADR?

An Architecture Decision Record (ADR) captures an important architectural decision along with its context and consequences.

## ADR Template

Each ADR follows this structure:

1. **Title** - Short descriptive title
2. **Status** - Proposed, Accepted, Deprecated, Superseded
3. **Context** - The situation and forces that led to this decision
4. **Decision** - What we decided to do
5. **Consequences** - The results of the decision (positive and negative)
6. **Alternatives Considered** - Other options we evaluated

## Index

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [001](001-metric-naming-prefixes.md) | Metric Naming Prefixes | Proposed | 2026-01-07 |
| [002](002-result-directory-structure.md) | Result Directory Structure | Proposed | 2026-01-07 |
| [003](003-cli-review-commands.md) | CLI Review Commands | Proposed | 2026-01-07 |
| [004](004-trace-recording-replay.md) | Trace Recording and Replay | Accepted | 2026-01-07 |
| [005](005-model-comparison-stochastic.md) | Model Comparison and stochastic evaluation Architecture | Accepted | 2026-01-08 |
| [006](006-service-based-architecture-di.md) | Service-Based Architecture & DI | Accepted | 2026-01-09 |
| [007](007-metrics-taxonomy.md) | Metrics Taxonomy | Accepted | 2026-01-10 |
| [008](008-calibrated-judge-multi-model.md) | Calibrated Judge for Multi-Model LLM Evaluation | Accepted | 2026-01-12 |
| [009](009-benchmark-strategy.md) | Benchmark Strategy | Accepted | 2026-01-13 |
| [010](010-maf-workflow-integration-architecture.md) | MAF Workflow Integration Architecture | Accepted | 2026-02-14 |
| [011](011-workflow-event-processing-timeout-handling.md) | Workflow Event Processing and Timeout Handling | Accepted | 2026-02-14 |
| [012](012-workflow-assertion-design.md) | Workflow Assertion Design | Accepted | 2026-02-14 |
| [013](013-maf-rc1-upgrade.md) | Microsoft Agent Framework RC1 Upgrade | Accepted | 2026-02 |
| [014](014-dataset-pipeline-two-model-architecture.md) | Dataset Pipeline — Two-Model Architecture | Accepted | 2026-02-24 |
| [015](015-extension-registration-manual-vs-auto-discovery.md) | Extension Registration — Manual vs Auto-Discovery | Accepted | 2026-02-25 |
| [016](016-monolith-modularization.md) | Monolith Modularization | Accepted | 2026-02-26 |
| [017](017-unified-benchmarks-namespace.md) | Unified Benchmarks Namespace (Convention 1-4) | Implemented (v0.10.0-beta) | 2026-05-17 |
| [018](018-compliance-core-and-shared-extractions.md) | Compliance.Core and Cross-Cutting Shared Extractions | Accepted | 2026-05-31 |
| [019](019-chat-boundary-two-layer-recording.md) | Chat-Boundary Tracing and the Two-Layer Recording Model (Glass Box) | Accepted | 2026-05-31 |
| [020](020-agenttrace-v1_1-schema.md) | AgentTrace v1.1 Schema (Glass Box additive fields) | Accepted | 2026-05-31 |
| [021](021-judge-primary-semantic-oracles.md) | Judge-Primary Grading for Semantic Red-Team Oracles (RedTeam Phase B) | Accepted (extended by 022) | 2026-06-18 |
| [022](022-grading-by-decomposition-composite-sub-evaluators.md) | Grading by Decomposition (Composite Sub-Evaluators) — RedTeam Phase C | Accepted (extends 021; B.3 executed) | 2026-06-21 |
| [023](023-decompose-misinformation-confabulation-vs-denial.md) | Decompose the Misinformation Oracle (Confabulation ⊕ Existence-Denial) | Accepted | 2026-06-22 |
| [024](024-split-then-gate-decomposition-and-its-bounds.md) | Split-then-Gate Decomposition (Gated Trees) and Its Bounds | Accepted | 2026-06-23 |
| [025](025-gatekeeper-runtime-fail-closed-enforcement.md) | Gatekeeper — Runtime Fail-Closed Enforcement Middleware | Accepted | 2026-07-05 |
| [026](026-typedmemeval-benchmark-family.md) | TypedMemEval — A Mechanism-Isolating Memory Benchmark Family | Accepted (implemented v0.22.0-beta) | 2026-08-15 |
| [027](027-typedmemeval-semantic-temporal-bitemporal.md) | TypedMemEval — Semantic, Temporal and Bitemporal Verticals (design) | Proposed (design only; generation gated) | 2026-08-18 |

---

*Template based on [Michael Nygard's ADR format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)*
