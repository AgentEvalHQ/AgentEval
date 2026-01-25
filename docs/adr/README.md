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

---

*Template based on [Michael Nygard's ADR format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)*
