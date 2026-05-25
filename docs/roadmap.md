# AgentEval Roadmap — Plans 00–08 Overview

> **What you're looking at**: a one-page index of every implementation plan AgentEval has executed against, with status, scope, and what ships next. Useful both as a reader's quick-tour and as boilerplate for release PR descriptions.

**Last updated**: 2026-05-13 · **Current release**: v0.8.1-beta (PR #26, awaiting CI green-merge) · **Next**: v1.1

---

## At-a-glance status

| Plan | Title | Status | What it delivers |
|---|---|---|---|
| **00** | Plans 01–06 Recap & Architecture | ✅ design-doc | Cross-plan architecture map. Reference only. |
| **01** | `.agenteval/` Output Store | ✅ shipped | Workspace contract, audit-chain hashing, three store impls, `agenteval init/doctor/migrate` CLI |
| **02** | Composite Evaluations | ✅ shipped | Recursive `IEval` model, atomic + composite primitives, 7 aggregations, depth cap, DI |
| **03** | GDPR Compliance Benchmark | ✅ shipped (5/5 pillars PASS calibration) — Pillar 6 governance probes deferred to v1.1 | 21 article YAMLs, 5 pillars, evidence + PDF reporter, calibration suite |
| **04** | EU AI Act Compliance Benchmark | ✅ shipped (6/6 pillars PASS calibration with 2 documented threshold overrides) — Art 9/10 awareness probes deferred to v1.1 | EU AI Act articles + pillars; honest scope for GPAI / admin obligations |
| **05** | Foundry-equivalent Agentic Suite | ✅ shipped — calibration coverage is partial today (headline system-and-process + RAG-quality categories meet the strict gate; remaining categories await calibration evidence in v1.1) | Broad agentic evaluator suite across 10 categories; relationship to upstream Foundry is prompt-provenance-only (cited per-file) |
| **06** | Memory / Multi-turn / Reasoning / UX | ✅ shipped | Memory recall, multi-turn coherence, reasoning, UX evaluators wired into the 60-suite |
| **07** | Mission Control — Design | ✅ design-doc | Phase-1 viewer + Phase-2 server architecture; identity, audit, ingestion contracts |
| **08** | Mission Control — Phase 1 implementation | ✅ shipped | ASP.NET host, Hot Chocolate GraphQL, binary REST endpoints, React 19 + Vite 6 SPA, single-binary deployment, Docker image, `agenteval mc serve / doctor`, first-run landing |
| **v1.1 plan** | Honest-scope completion + adoption-leverage + audit hardening | ⬜ draft (`11-v1.1-implementation-plan.md`) | Closes the v0.8.1-beta disclaimers (Pillar 6, Art 9/10, agentic calibration coverage), publishes `dotnet tool`, hardens audit chain |

---

## Per-plan summary

### Plan 00 — Recap & Architecture
**Type**: Design-only.
**Scope**: One-page map of plans 01–06 — naming conventions, cross-cutting concerns, abstraction boundaries (`IEval`, `IOutputStore`, judge resolution). Never produces code; serves as the orientation doc for anyone joining the project mid-flight.

### Plan 01 — `.agenteval/` Output Store
**Status**: Shipped in v0.8.1-beta.
**Scope**: The on-disk contract for everything AgentEval writes — manifests, summaries, scenario results, compliance evidence, red-team campaigns, baselines.
**Highlights**:
- Schemas v1 for `manifest.json`, `summary.json`, `evidence.json`, `scenarios/*.json` — all embedded as resources + schema-validated at write.
- **Audit chain**: `ContentHasher` hashes the run body; `evidence.SourceRun.ManifestHash` must equal the source manifest's stored `ContentHash`. `agenteval doctor` validates the chain end-to-end.
- Three store implementations: `FileSystemOutputStore` (production), `InMemoryOutputStore` (tests), `NullOutputStore` (sink).
- CLI: `agenteval init` bootstraps a workspace, `agenteval doctor` validates it, `agenteval migrate` upgrades legacy layouts.
- **Deferred (won't be added unless asked)**: Pit/tournament, evolution-cycle, behavioral-fingerprint, Roslyn path analyzer. All design-only with no consumer signal.

### Plan 02 — Composite Evaluations
**Status**: Shipped in v0.8.1-beta.
**Scope**: The evaluation primitive. Every benchmark in v1 is built on it.
**Highlights**:
- `IEval` is recursive — composites contain composites contain atomics. `eval-result.schema.json` `$ref`s itself.
- Three atomic shapes: `AtomicEval` (custom), `AtomicLlmEval` (judge-based), `AtomicCodeEval` (deterministic). Composites compose them with `EvalComponent(Eval, Weight, Required)`.
- 7 aggregation strategies: `WeightedSum`, `Min`, `CapByWorst`, `MajorityVote`, `WeightedMedian` (+ severity / cost rollups). Verdict matrix: if `Threshold` set → score≥t pass else fail; if null → severity {high|critical→fail, medium→warn, none|low→pass}.
- **Producer-side depth cap**: `MaxNestingDepth=32` via `AsyncLocal<int>` so a malicious eval tree can't blow the stack.
- DI: `AddCompositeEvals(IServiceCollection)`.
- **Deferred**: `StrictAggregation` (Min covers it), unweighted `MedianAggregation` (uniform-weight `WeightedMedian` is equivalent), `EvalComponent.Predicate` (no consumer ask), YAML composite authoring (premature).

### Plan 03 — GDPR Compliance Benchmark
**Status**: Shipped in v0.8.1-beta. **5 of 5 calibration pillars PASS** at default thresholds against Azure OpenAI gpt-5-chat.
**Scope**: A regulator-facing GDPR compliance benchmark for AI agents — does the agent correctly refuse / cite / advise under GDPR obligations?
**Highlights**:
- **21 article YAMLs** across **5 pillars**: lawful basis (Art 6/7/8), data subject rights (Art 12–22), security (Art 32), special categories (Art 9/10), transparency (Art 13/14/15).
- Three presets: `Smoke` (5 articles, <$0.10), `Standard` (16 articles, ~$0.50), `AuditGrade` (16 articles × multi-judge + Mode-B per-criterion + stochastic `--runs`, ~$5–10).
- Three optional domain packs: Healthcare, HR, Childrens-service.
- **Evidence + PDF reporter**: `GDPRComplianceReporter` emits schema-valid `gdpr-evidence.json` (extends the plan-01 audit-chain contract) + a styled PDF cover page with the v1 disclaimer.
- **Calibration suite**: hand-labelled goldens + Cohen's kappa + accuracy gate. Per-pillar threshold overrides documented in code.
- CLI: `agenteval bench gdpr --preset … --subject … --input …` runs the bench; `agenteval bench gdpr calibrate` measures judge agreement.
- **Deferred to v1.1**: **Pillar 6 — Governance awareness probes** (Art 28 processor contracts, Art 30 records, Art 33+34 breach notification 72h DPA clock, Art 35 DPIA, Art 37–39 DPO, Art 44–49 international transfers, Art 5(2) accountability). Awareness layer only — does NOT substitute organizational compliance evidence.
- **Deferred indefinitely**: multi-language scenarios (English-only honest scope), Art 88 Member-State variants, external compliance-platform integrations.

### Plan 04 — EU AI Act Compliance Benchmark
**Status**: Shipped in v0.8.1-beta. **6 of 6 calibration pillars PASS** — 4 at default thresholds + 2 with documented per-pillar overrides (`pillar1-prohibited-25` at 0.75/0.50 absorbs Art 5 prohibited-practice stochasticity; `pillar6-gpai-12` at 0.60/0.25 reflects Art 51–55 GPAI scope ambiguity).
**Scope**: Same shape as GDPR, EU AI Act articles + pillars.
**Highlights**:
- Pillars cover prohibited practices (Art 5), high-risk-system obligations (Art 13/14/15/17/26/27), deployer-side awareness (Art 50), GPAI awareness (Art 51–55).
- Three domain packs for Annex III high-risk areas: Employment, Credit, Education. Composable: `standard+high-risk-employment+high-risk-credit` works.
- Same evidence + PDF + CLI shape as Plan 03.
- **Honest scope disclaimer** (in `docs/benchmarks/eu-ai-act/getting-started.md`): Art 9 / 10 / 11 / 71 / 72 / 73 + GPAI provider-side obligations are out of v1 scope.
- **Deferred to v1.1**: Art 9 (risk-management lifecycle) + Art 10 (data governance / training-data quality) awareness probes — same caveat as GDPR Pillar 6.
- **Deferred indefinitely**: Art 11 TD probe (lower value), Art 71/72/73 (process attestation, needs v2 evidence pipeline), GPAI Art 51–55 (different audience — provider, not deployer), Annex III law-enforcement / migration / justice / critical-infra packs (community-contribution scope).

### Plan 05 — Foundry-equivalent Agentic Suite
**Status**: Shipped in v0.8.1-beta. The full evaluator suite runs today via `agenteval bench agentic`. Calibration coverage is partial — the headline system-and-process and RAG-quality categories meet the strict gate; the remaining categories run and produce verdicts but await fuller calibration evidence. The dispatch wiring for `bench agentic calibrate` is being extended to those categories in v1.1.
**Scope**: Production-grade agentic evaluators forked from Microsoft Foundry's public MIT-licensed `.prompty` files. Each evaluator emits `evidence[]` instead of chain-of-thought, runs at `temperature: 0`, and carries date-stamped fork provenance (SHA-pinning tracked for v1.1).
**Highlights**:
- **10 categories**: Process (tool-call accuracy, tool selection, intent resolution), System (task completion, task adherence), RAG quality (groundedness, relevance, coherence, fluency, similarity, response-completeness, F1), Safety (violence, sexual content, protected material, hate, ungrounded attributes, indirect attack, code vulnerability, unsafe tool use), Reasoning, UX, Adversarial, Memory, Multi-turn, Calibration (epistemic).
- **EvaluatorCard registry**: one card per evaluator carries display metadata, cost tier, calibration status — drives the Mission Control SPA's Evaluators page.
- **Prompt provenance**: each forked judge prompt cites its public MIT-licensed Foundry source in the file header with a date-stamped fork reference (e.g., `main/2026-05`) and the modifications enumerated. Tightening the date stamps to real pinned commit SHAs per file is tracked as a v1.1 polish item. A Pearson-correlation cross-validation report generator (A5.4) is deferred to v1.1.
- **Deferred to v1.1**: calibration-coverage extension to the remaining categories (task **1.3** in `11-v1.1-implementation-plan.md`).
- **Deferred indefinitely**: A5.3 workflow-specific evaluators (stays in `AgentEval.MAF`), A5.4 Foundry Pearson-correlation report generator (data path ships; report is value-additive without ask), `AdjudicatedMultiJudgeWrapper` kappa-of-1 real fix (data-dependent), card-category drift audit (cosmetic).

### Plan 06 — Memory / Multi-turn / Reasoning / UX
**Status**: Shipped in v0.8.1-beta.
**Scope**: The Phase-6 follow-up to Plan 05 — evaluators that probe behaviour patterns the Foundry baseline doesn't cover.
**Highlights**:
- Memory recall accuracy + cross-session continuity probes.
- Multi-turn coherence + topic-drift detection.
- Reasoning quality (multi-step inference correctness).
- UX (helpfulness, response completeness, confidence calibration).
- Confidence Calibration eval + Calibration Accuracy meta-eval.
- **Deferred indefinitely**: `ContextCompactionQualityEval` (needs session-store + reference compactor), `CrossSessionContinuityEval` (needs session-store abstraction), `IncrementalManipulationEval` (no red-team consumer ask), `HelpfulnessHarmlessnessBalanceEval` (needs both-ends calibration dataset).

### Plan 07 — Mission Control: Design
**Type**: Design-only.
**Scope**: The architectural blueprint for AgentEval's portal — Mode A (local viewer), Mode B (multi-workspace aggregator, deferred to Phase 2), Mode C (self-hosted server, deferred to Phase 2, target v1.5+).
**Highlights**:
- Identity model: `Workspace` → `Solution` → `Subject` → `Run` → `Scenario` → `EvalResult`.
- Data path: `IOutputStoreReader` reads `.agenteval/` directly, no daemon required for Mode A.
- GraphQL contract: `solutions / subjects / runs / scenarios / scenarioTree / compliance / complianceMatrix / complianceEvidence / evaluators / evaluatorTimeline / runCostBreakdown`.
- Binary REST endpoints: trace download, report MD/HTML/PDF, schema lookup, history.
- Security model: read-only by default; workspace path is the trust boundary in Mode A.

### Plan 08 — Mission Control Phase 1 implementation
**Status**: Shipped in v0.8.1-beta. **Mode A (local viewer) is feature-complete.**
**Highlights**:
- `AgentEval.MissionControl` ASP.NET host + Hot Chocolate 16 GraphQL schema (every Plan-07 resolver wired).
- SPA: React 19 + Vite 6 + Tailwind 4 + TanStack Query + recharts. Pages: Solutions, Subjects, Runs, Run Detail (with cost-tier breakdown chart), Evaluators registry, Evaluator Detail (card-driven), Compliance List, **Compliance Matrix** (the killer feature — drill-through cell→evidence detail → recursive scenario tree → adjudication flow), Red-team campaign list (resolvers shipped — page deferred to v1.1 stretch).
- **Single-binary deployment**: `dotnet run --project src/AgentEval.MissionControl` serves the SPA + GraphQL + REST on one port.
- **Docker image**: multi-stage build, ~80 MB final.
- **CLI**: `agenteval mc serve [--workspace <path>]`, `agenteval mc doctor` (health check). First-run landing for uninitialised workspaces.
- **A11y**: every interactive surface (matrix cells, drill-down tree, adjudication flow) carries `aria-label`s + keyboard navigation.
- **Deferred to v1.1 stretch**: MC1.4.5 red-team campaign SPA page (~80 LoC).
- **Deferred indefinitely**: MC1.3.1 `/api/v1/version` enrichment (Phase-2 ingest trigger), MC1.4.4 GraphQL inline-fragment dispatch (SPA-renderer trigger), MC1.4.7 GreenDonut DataLoader batching (telemetry trigger), MC1.5.4/5/6.11 SSE live updates (multi-tab telemetry trigger), MC1.7.2 static-bundle export (no consumer ask), MC1.7.3 multi-workspace Mode B (Phase 2), MC1.9 Notes (Phase 2), MC1.10.2 `--legacy-import` (one-shot fixture), MC1.11.1 visual regression (no current gap), entire Phase 2 Mode C server (16-week scope, target v1.5+).

---

## v1.1 plan (next release)

**Source**: `strategy/FutureFeatures/todo/11-v1.1-implementation-plan.md` (local-only / gitignored).

**Filter**: From the ~52-item v1.1 backlog spanning two source docs, the plan KEEPs 14 + 3 stretch items; DROPs 27; DEFERs-further 11. The KEEP criteria are strict: (a) closes a v0.8.1-beta honest-scope disclaimer, (b) the user explicitly named the item, (c) it removes measurable adoption friction, or (d) it's a mechanical refactor whose blast radius grows with delay.

**Phases**:
- **Phase 1 — Honest-scope completion (~8 working days)**: GDPR Pillar 6 governance probes, EU AI Act Art 9 + 10 awareness probes, agentic calibration coverage extension to the remaining categories, recalibrate all three benchmarks after content additions, structured `Recommendations[]` shape, evaluator-card category-drift audit.
- **Phase 2 — Adoption leverage (~2 working days)**: ✅ `dotnet tool install --global AgentEval.Cli --prerelease` (landed 2026-05-24 — CLI is now packed as a `dotnet tool` with multi-TFM net8 LTS / net10 targets; install picks the highest compatible runtime); ECS 2026 showcase smoke.
- **Phase 3 — Architecture cleanup + audit hardening (~7 working days)**: Promote `samples/AgentEval.GdprBenchmark` + `samples/AgentEval.EuAiActBenchmark` → `src/AgentEval.Compliance.*` (CLI-references-samples antipattern fix), relocate `EvaluatorCostMap` out of Abstractions, thread `judgeModel` through agentic factories, `agenteval doctor` schema validation, manifest+evidence body re-hashing with canonical-JSON projection (RFC 8785). (The "disambiguate two `AgenticBenchmark` types" task was resolved by the v0.9.0-beta removal of the legacy class.)
- **Phase 4 — Stretch (~3 working days)**: `AdjudicatedMultiJudgeWrapper` kappa-rename cosmetic fix, `runCostBreakdown` unknown-bucket semantics split, MC1.4.5 red-team campaign SPA page.

**Total Phases 1–3**: ~17 working days (~3–4-week sprint with normal review overhead).

---

## Out of scope (won't ship without fresh consumer signal)

These items live in the backlogs as inventory; they will NOT enter a v1.x plan without explicit external demand:

- **Pit / tournament / evolution / behavioral fingerprint** — Plan 01 design-only items; no consumer asked.
- **Multi-language scenarios** (DE / FR / ES / IT) — English-only honest scope; trigger is a non-English regulator engagement.
- **GDPR Art 88 Member-State variants** — Per-jurisdiction legal review per pack.
- **External compliance platform integrations** (OneTrust / TrustArc) — Per-target integration cost.
- **EU AI Act Art 11 TD probe / Art 71/72/73 admin obligations / GPAI Art 51–55 / Annex III law-enforcement+migration+justice+critical-infra packs** — Disclaimer-acknowledged out-of-scope.
- **Agentic memory / context-compaction / cross-session / incremental-manipulation / helpfulness-harmlessness-balance** — Need session-store abstraction or both-ends calibration dataset; no consumer ask.
- **Mission Control export bundle / visual regression / multi-workspace aggregator / Notes / legacy-import** — Trigger-specific (e.g., "platform engineer with 5+ repos asks") or premature.
- **Mission Control Phase 2 (Mode C self-hosted server)** — 16-week scope; target v1.5+.

---

## Where things live

| Concern | Path |
|---|---|
| Implementation plans 00–08 | `strategy/FutureFeatures/todo/0{0..8}-*.md` (local-only / gitignored) |
| Closed master tracking (review-03 8-phase remediation) | `strategy/FutureFeatures/todo/10-review03-findings-fix-implementation-plan.md` |
| v1.1 plan (filtered + sequenced) | `strategy/FutureFeatures/todo/11-v1.1-implementation-plan.md` |
| Plan-first backlog (full inventory) | `strategy/FutureFeatures/todo/13-pending-issues-tasks.md` (v1.1 consolidation supersedes earlier `deferred-pending.md`, archived under `done/`) |
| Feature-first backlog (same items, regrouped) | `strategy/FutureFeatures/todo/pending-tasks-by-feature.md` |
| Calibration baselines (3 benchmarks) | `strategy/FutureFeatures/calibration-baselines/{gdpr,eu-ai-act,agentic}-calibration-report.md` |
| Honest-scope disclaimers (public-facing) | `docs/benchmarks/{gdpr,eu-ai-act,agentic}/getting-started.md` |
| This roadmap | `docs/roadmap.md` (you are here) |

---

## How to use this doc

- **For a PR description**: copy the "At-a-glance status" table + the relevant plan summaries. Adjust the "Status" column per release.
- **For onboarding a new contributor**: read the per-plan summary; pick an "Out of scope" item with a consumer trigger and propose it as a follow-up PR.
- **For consumers evaluating v0.8.1-beta**: read Plans 03 / 04 / 05 / 08 summaries — those describe what you can run today, with honest scope and calibration state.
