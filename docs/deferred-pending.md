# Deferred & Pending — items not yet implemented across plans 00–08

This document is the canonical inventory of work that has been
**explicitly considered and deliberately deferred** across the AgentEval
development plans. It covers:

- Items that the original plans (00–08) called out as out-of-scope or
  deferred to future releases.
- Items surfaced by the post-ship 10-agent Opus review (2026-05-10) that
  the team has decided to handle in a follow-up PR rather than fold
  into the current branch.
- Architectural cleanup tasks that are mechanical refactors safer to
  ship as their own slice.

Each entry follows the same shape:

| Field | Meaning |
|---|---|
| **Source** | Where the item was originally captured (plan + section, or review). |
| **Scope** | What the implementation would entail. |
| **Why deferred** | The reason it didn't ship in the current PR. |
| **Trigger to revisit** | What signal should unblock the work. |
| **Effort estimate** | Rough size in person-hours of focused work. |

The intent is that anyone reading this doc gets enough context to make
an informed "should we ship this next?" decision without re-reading the
underlying plan.

---

## Plan 01 — `.agenteval/` Output Store

### Pit / tournament implementation
- **Source**: `strategy/FutureFeatures/todo/01-AgentEval-OutputFolder-ImplementationPlan.md` §"Out-of-scope" (Strategy r2 §23.1).
- **Scope**: Pit-style round-robin tournament across N agent variants on a shared scenario set, producing a ranked leaderboard with statistical significance bands.
- **Why deferred**: Design-only in plan-01; no production consumer yet.
- **Trigger to revisit**: A team wants to A/B test 5+ prompt or model variants and needs a built-in scoreboard rather than rolling their own.
- **Effort estimate**: 1–2 weeks for the harness + reporter + CLI.

### Evolution cycle implementation
- **Source**: plan-01 §"Out-of-scope" (Strategy r2 §23.2).
- **Scope**: Genetic-algorithm-style prompt/agent evolution loop that combines mutation, selection, and pit-based fitness evaluation.
- **Why deferred**: Design-only; depends on the pit harness; no production consumer.
- **Trigger to revisit**: After pit/tournament ships and a team wants automated prompt optimization.
- **Effort estimate**: 2–3 weeks on top of pit.

### Genome / behavioral fingerprint
- **Source**: plan-01 §"Out-of-scope" (Strategy r2 §23.3).
- **Scope**: Compact descriptor of an agent's behavior (tool-call distribution, response style, refusal rate, etc.) usable for similarity search + drift detection.
- **Why deferred**: Design-only.
- **Trigger to revisit**: When a team needs to detect "is this variant materially different from baseline" automatically.
- **Effort estimate**: 1 week for the descriptor; 2 weeks total with the drift-detection harness.

### Roslyn analyzer for path-string detection
- **Source**: plan-01 §"Out-of-scope".
- **Scope**: A Roslyn-based analyzer that flags hand-rolled `.agenteval/...` string literals in user code, suggesting the layout helpers instead.
- **Why deferred**: Low ROI for current codebase size; the layout APIs are easy to discover via IntelliSense.
- **Trigger to revisit**: When 5+ external consumers have shipped against the workspace API and reported path-mistake bugs.
- **Effort estimate**: 1 week for the analyzer + integration tests.

---

## Plan 02 — Composite Evaluations

### `StrictAggregation`
- **Source**: `docs/composite-evals.md` §"Deferred features"; plan-02 phase-1 explicit deferral.
- **Scope**: An aggregation strategy that fails the composite if any sub-eval fails (regardless of weight). Equivalent to `Min` when severity-driven; needed when a single failing component must hard-block the verdict.
- **Why deferred**: `MinAggregation` covers most use cases today.
- **Trigger to revisit**: A consumer asks for it (e.g., a security composite where any sub-failure must hard-fail without severity ambiguity).
- **Effort estimate**: 2–4 hours including tests.

### Unweighted `MedianAggregation`
- **Source**: `docs/composite-evals.md` §"Deferred features".
- **Scope**: Median aggregation that ignores weights (vs `WeightedMedianAggregation` which respects them).
- **Why deferred**: `WeightedMedianAggregation` with uniform weights is equivalent.
- **Trigger to revisit**: A consumer prefers the unweighted shape for clarity.
- **Effort estimate**: 1–2 hours.

### `EvalComponent.Predicate` — conditional skipping with renormalization
- **Source**: `docs/composite-evals.md` §"Deferred features"; plan-02 phase-1 explicit deferral.
- **Scope**: A predicate function on `EvalComponent` that decides at runtime whether the component should be evaluated, with automatic weight renormalization across the surviving components.
- **Why deferred**: Phase-1 ships `Required: bool` as the simpler shape; predicate adds dynamism but no consumer has asked.
- **Trigger to revisit**: A consumer needs context-aware sub-eval composition (e.g., "only evaluate the multi-turn judge when the run had ≥3 turns").
- **Effort estimate**: 4–6 hours including the renormalization tests.

### YAML composite authoring
- **Source**: `docs/composite-evals.md` §"Deferred features".
- **Scope**: Declare composites in YAML (mirroring article-spec format) so consumers can compose new audit suites without writing C#.
- **Why deferred**: Phase-1 ships only code-authored composites; YAML pathway needs a stable schema + loader + sample-ready golden tree tests.
- **Trigger to revisit**: A third party wants to ship a composite suite without touching the codebase.
- **Effort estimate**: 1 week for schema + loader + 4–6 representative goldens.

---

## Plan 03 — GDPR Compliance Benchmark

### Pillar 6 — Governance (Art 28, 30, 33, 34, 35, 37–39, 44–49, 5(2))
- **Source**: 10-agent Opus review 2026-05-10 (Agent 4 BLOCKER 1).
- **Scope**: A new pillar covering eight governance articles via dialog-AWARENESS probes (NOT a substitute for actual organizational compliance evidence — see "honest read" below):
  - **Art 28** (processor contracts) — agent describes data-processing-agreement, joint-controller, and sub-processor consent obligations.
  - **Art 30** (records of processing) — agent identifies what ROPA contains, retention norms.
  - **Art 33** (breach notification — 72h DPA clock) — agent cites the 72-hour rule + criteria for "personal data breach".
  - **Art 34** (breach communication to data subjects) — agent identifies when high-risk notification triggers.
  - **Art 35** (DPIA) — agent recognises Art 35(3) trigger conditions.
  - **Art 37–39** (DPO) — agent describes the role, contact obligations, and Art 37(1) appointment triggers.
  - **Art 44–49** (international transfers) — Schrems II awareness, SCCs, BCRs, adequacy decisions.
  - **Art 5(2)** (accountability principle) — meta-control surfacing demonstrable-compliance obligations.
- **Honest read**: Dialog probes test whether the agent CAN DESCRIBE the obligation when asked. They do NOT verify that the organisation actually has ROPA, has a DPO, has SCCs, or notifies the DPA within 72h. Those are process attestations outside any dialog benchmark's reach. Treat Pillar 6 as a "the agent is aware enough not to give bad legal advice" gate, not as audit-grade evidence.
- **Why deferred**: ~10 article YAMLs at the same quality bar as the existing 16 articles is 4–6 hours of focused regulatory cross-check authoring. Opus already caught one citation error in the existing EU AI Act content (Art 10 vs Annex III in art-15-003); rushing 10 more articles without similar care would import similar errors. The disclaimer in `docs/benchmarks/gdpr/getting-started.md` is honest about v1 scope.
- **Trigger to revisit**: A DPO or auditor-side consumer asks for dialog-awareness probes on these articles, OR a v2 evidence pipeline (document-review + process-attestation) is wired up that could carry the real-process-compliance side of the obligation.
- **Effort estimate**: 4–6 hours for 8 article YAMLs + 1 new pillar + preset rebalancing + a calibration update.

### Multi-language scenario authoring
- **Source**: plan-03 §"Out-of-scope".
- **Scope**: Per-language scenario YAML variants beyond English (DE, FR, ES, IT at minimum for EU regulator alignment).
- **Why deferred**: English-only in v1.
- **Trigger to revisit**: A non-English-speaking regulator scope.
- **Effort estimate**: ~30 minutes per article per language to translate scenarios, given a known-good English baseline.

### Human-in-the-loop verdict overrides
- **Source**: plan-03 §"Out-of-scope" (Phase 11+ feature).
- **Scope**: Mission Control UI flow letting a reviewer override a judge verdict on a per-control basis, with audit-chained justification.
- **Why deferred**: Out of GDPR core scope; needs Mission Control's notes infrastructure first (MC1.9 ⬜).
- **Trigger to revisit**: When MC1.9 Notes is shipped, this becomes a natural extension.
- **Effort estimate**: 2–3 days for the override UI + audit-chained justification persistence.

### GDPR Article 88 (employee data) full coverage
- **Source**: plan-03 §"Out-of-scope".
- **Scope**: Full Art 88 sub-controls, including Member-State-specific implementations (Germany BDSG, France Loi Informatique, etc.).
- **Why deferred**: Partially covered by HR domain pack via Art 6 scenarios; full Art 88 is Member-State-specific and out of scope for a single benchmark.
- **Trigger to revisit**: A specific Member-State enterprise needs employee-data-specific evaluation.
- **Effort estimate**: 1 week per Member-State jurisdiction including legal review.

### Real-time portal trace streaming during run
- **Source**: plan-03 §"Out-of-scope".
- **Scope**: Live "the bench is running, here's the current scenario being evaluated" trace stream into Mission Control.
- **Why deferred**: Mission Control Phase 2 territory; needs SSE + FileSystemWatcher first (see MC1.5.4/.5/.6.11).
- **Trigger to revisit**: After Mission Control Phase 1 ships and SSE lands.
- **Effort estimate**: Folds into the SSE work — minimal incremental.

### Integration with external compliance management platforms
- **Source**: plan-03 §"Out-of-scope".
- **Scope**: Push evidence to OneTrust, TrustArc, or similar; pull subject lists from them.
- **Why deferred**: Third-party integrations; out of scope until a customer requires it.
- **Trigger to revisit**: A specific platform integration is requested.
- **Effort estimate**: 2–3 weeks per integration target including auth + idempotency + schema mapping.

---

## Plan 04 — EU AI Act Compliance Benchmark

### Art 9 (Risk Management) + Art 10 (Data Governance) probes
- **Source**: 10-agent Opus review (Agent 5 IMPORTANTs I1+I2).
- **Scope**: Two awareness probes:
  - **Art 9** — agent describes the iterative risk-management lifecycle (identification, analysis, estimation, mitigation, ongoing review) and acknowledges Art 9(2)(a–g) requirements.
  - **Art 10** — agent describes training-data quality, representativeness, and bias-mitigation obligations.
- **Honest read**: As with GDPR Pillar 6, these are AWARENESS probes — they cannot substantiate that the organisation actually maintains a risk-management system or curates training data. The benchmark's existing scope disclaimer already explicitly lists these as out of scope; adding awareness probes would narrow the disclaimer but not eliminate it.
- **Why deferred**: ~2 article YAMLs requires careful regulatory cross-check. Same risk as GDPR Pillar 6 — careless authoring imports regulatory errors.
- **Trigger to revisit**: Same as GDPR Pillar 6 — a deployer-side consumer asks, or v2 evidence pipeline ships.
- **Effort estimate**: 2–4 hours for 2 article YAMLs + pillar wiring + a calibration update.

### Art 11 (technical documentation) probe
- **Source**: plan-04 §"Out-of-scope"; 10-agent review (Agent 5 NICE).
- **Scope**: Awareness probe for what Art 11 + Annex IV require an AI system's technical file to contain.
- **Why deferred**: Documentation is a deliverable artifact, not a dialog behaviour. Awareness probe possible but lower value than Art 9/10.
- **Trigger to revisit**: When a deployer wants a "the agent knows what TD requires" check.
- **Effort estimate**: 1–2 hours for 1 article YAML.

### Art 71 / Art 72 / Art 73 (database registration / post-market monitoring / incident reporting)
- **Source**: plan-04 §"Out-of-scope"; documented in `docs/benchmarks/eu-ai-act/getting-started.md`.
- **Scope**: Three administrative obligations not testable from dialog alone.
- **Why deferred**: Process attestation, not dialog behaviour.
- **Trigger to revisit**: When v2 evidence pipeline supports process-attestation artefacts.
- **Effort estimate**: Out of scope for awareness probes; full coverage requires v2 evidence pipeline.

### GPAI provider obligations (Art 51–55)
- **Source**: plan-04 §"Out-of-scope"; `docs/benchmarks/eu-ai-act/getting-started.md` explicitly out of scope.
- **Scope**: Obligations on the **model provider** (OpenAI, Anthropic) rather than the deployer.
- **Why deferred**: Different audience. Pillar 6 already ships a "self-awareness probe" for downstream deployers to verify the agent correctly represents its own provenance.
- **Trigger to revisit**: A model-provider customer (not deployer) wants to evaluate their own foundation model.
- **Effort estimate**: 1 week to design the dual-audience benchmark.

### Annex III high-risk areas not packaged: law enforcement, migration/asylum/border control, justice administration, critical infrastructure
- **Source**: plan-04 §"Out-of-scope" — explicitly community-contribution-welcome.
- **Scope**: One domain pack per Annex III high-risk category. Current packs cover employment, credit, education (the most common deployment domains).
- **Why deferred**: Each pack requires careful regulatory + domain expert review.
- **Trigger to revisit**: A deployer in the specific domain wants coverage.
- **Effort estimate**: 1 week per domain pack including legal + subject-matter review.

### Multi-language scenario authoring
- **Source**: plan-04 §"Out-of-scope".
- **Scope**: Same as GDPR — per-EU-language scenario variants.
- **Why deferred**: English-only in v1.
- **Trigger to revisit**: A non-English-language regulator scope.
- **Effort estimate**: ~30 minutes per article per language.

### Mappings to harmonized standards (under Art 40)
- **Source**: plan-04 §"Out-of-scope".
- **Scope**: A separate doc mapping each benchmark scenario to the corresponding harmonized-standard clause (when standards are finalised by CEN-CENELEC / ISO).
- **Why deferred**: Harmonized standards under EU AI Act Art 40 are still being drafted (as of 2026-05).
- **Trigger to revisit**: When the first set of harmonized standards is published.
- **Effort estimate**: 2–3 weeks for a comprehensive mapping doc.

---

## Plan 05 — Agentic Benchmark (Foundry-equivalent)

### A5.3 — Workflow-specific evaluators
- **Source**: plan-05 tracking table; explicitly deferred.
- **Scope**: Evaluators that target multi-agent workflows specifically (handoff fidelity, executor-order verification, edge-traversal completeness). Live in `AgentEval.MAF` today or a future `AgentEval.Evals.Workflow` package.
- **Why deferred**: Stays in `AgentEval.MAF` per plan-05 §5 challenge 6. Not blocking the Phase-5 ship.
- **Trigger to revisit**: A workflow-heavy consumer asks for the dedicated evaluator surface.
- **Effort estimate**: 1 week for the package + 6–10 representative workflow evaluators.

### A5.4 — Foundry SDK Pearson-correlation cross-calibration runner
- **Source**: plan-05 tracking table; partially shipped.
- **Scope**: A report generator that produces Pearson-correlation between AgentEval evaluator scores and the corresponding Foundry SDK evaluator scores on a shared dataset.
- **Why deferred**: The `FoundryEquivalent` preset already exposes the cross-validation entry point; the report generator is value-additive but not blocking.
- **Trigger to revisit**: When a customer wants the "we behave the same as Foundry within ±X correlation" attestation.
- **Effort estimate**: 1 week for the report generator + golden datasets + CI gate.

### Calibration coverage extension (BenchAgenticCalibrateCommand)
- **Source**: 10-agent Opus review (Agent 3 BLOCKER 2).
- **Scope**: `bench agentic calibrate` currently registers ~11 of the 60 evaluators in `evalRegistry`. The remaining ~49 (Plan-06 categories: memory, multi-turn, reasoning, calibration, adversarial, UX, RAG quality, telemetry, judge-quality, safety) are silently `null`-resolved with a "unknown evaluator key" log — they never participate in the calibration gate even when datasets exist.
- **Why deferred**: Extending the registry requires per-category dataset format design + golden-set authoring; rushing it would produce false calibration confidence.
- **Trigger to revisit**: Before the next public release where the "60 evaluators are calibrated" claim is made.
- **Effort estimate**: 1–2 weeks for full coverage including dataset authoring.

### AdjudicatedMultiJudgeWrapper kappa-of-1 semantics
- **Source**: 10-agent Opus review (Agent 3 IMPORTANT I5).
- **Scope**: When 3+ judges evaluate a single observation, the kappa calculation always returns 1.0 (unanimous) or is clamped to 0 (any disagreement) — it isn't a real kappa, it's a unanimity-or-not vote.
- **Why deferred**: Renaming `agreement_method` to `"unanimity"` for the 3+ branch is a one-line cosmetic fix, but the underlying improvement (compute kappa across many disputed scenarios before clamping) is a design change that benefits from real consumer data first.
- **Trigger to revisit**: When the first multi-judge panel is calibrated against a real dataset and the metric semantics matter.
- **Effort estimate**: 30 min for the cosmetic fix; 1–2 days for the real cross-scenario kappa aggregation.

### 60-card category drift audit
- **Source**: 10-agent Opus review (Agent 3 IMPORTANT I6).
- **Scope**: Some `EvaluatorCard.category` values disagree with their corresponding evaluator's `Category` property. Affects dashboard grouping but doesn't change scoring.
- **Why deferred**: 60 cards to audit; mostly cosmetic.
- **Trigger to revisit**: Before the next public release.
- **Effort estimate**: 1–2 hours for the full audit.

---

## Plan 06 — Memory, Multi-turn, Reasoning, UX

### `ContextCompactionQualityEval`
- **Source**: plan-06 §5 (deferred from scope).
- **Scope**: Evaluator measuring how well an agent's context-compaction algorithm preserves semantic content vs naive truncation.
- **Why deferred**: Needs session-store integration + a reference compaction algorithm to compare against.
- **Trigger to revisit**: A consumer ships context compaction and wants to measure quality regression.
- **Effort estimate**: 1 week including reference algorithm + tests.

### `CrossSessionContinuityEval`
- **Source**: plan-06 §5 (deferred — needs session-store integration).
- **Scope**: Evaluator measuring whether an agent correctly recalls information across session boundaries (genuine long-term memory rather than in-conversation context).
- **Why deferred**: Requires a session-store abstraction that doesn't ship in v1.
- **Trigger to revisit**: When a session-store contract is defined (likely Phase 2 of Mission Control).
- **Effort estimate**: 1 week for the eval + 1 week for the session-store abstraction (if not already done).

### `IncrementalManipulationEval`
- **Source**: plan-06 §5 (deferred from adversarial scope).
- **Scope**: Evaluator detecting whether an agent gradually drifts toward a target behaviour over many small prompts (incremental jailbreak via boiling-frog patterns).
- **Why deferred**: Adversarial coverage is non-exhaustive; one consumer ask shifts priority.
- **Trigger to revisit**: A red-team consumer asks for it specifically.
- **Effort estimate**: 3–5 days including representative scenarios.

### `HelpfulnessHarmlessnessBalanceEval`
- **Source**: plan-06 §5 (deferred from UX scope).
- **Scope**: Evaluator measuring whether an agent's refusal posture is well-calibrated (refuses harmful asks AND is helpful for benign ones — penalising both over-refusal and under-refusal).
- **Why deferred**: Requires a calibration dataset spanning both ends; out of scope for plan-06 ship.
- **Trigger to revisit**: A consumer ships a refusal-heavy agent and needs balance verification.
- **Effort estimate**: 1 week including the calibration dataset.

---

## Plans 07 + 08 — Mission Control

### MC1.3.1 — `/api/v1/version` enrichment
- **Source**: plan-08 tracking table 🟡.
- **Scope**: Basic shape ships (`{ mode, agentEvalVersion, graphqlEndpoint }`). Deferred: `schemaVersions` map (per-schema versions for manifest/summary/evidence/etc.) + `features` array.
- **Why deferred**: Waiting until Phase 2 ingest endpoints exist so the version response can advertise them honestly.
- **Trigger to revisit**: Mission Control Phase 2 (Mode C server).
- **Effort estimate**: 2 hours.

### MC1.4.4 (partial) — Compliance interface + cross-regulation overlap
- **Source**: plan-08 tracking table 🟡.
- **Scope**: Base `compliance`, `complianceMatrix`, `complianceEvidence` resolvers ship. Deferred: regulation-specific GraphQL interface (`... on GdprComplianceEvidence` inline fragments per plan-07 §8.3); `crossRegulationOverlap` resolver.
- **Why deferred**: Needs cross-project type discovery from sample assemblies (Hot Chocolate doesn't auto-discover types from referenced sample DLLs).
- **Trigger to revisit**: When the SPA needs regulation-specific cell renderers (e.g., GDPR-specific control metadata that doesn't fit the base `EvidenceControl` shape).
- **Effort estimate**: 1 week including cross-assembly type registration + SPA inline-fragment queries.

### MC1.4.5 — Red-team campaign resolvers (`redTeamCampaigns`, `redTeamCampaign`)
- **Source**: plan-08 tracking table ⬜.
- **Scope**: Two GraphQL resolvers + a new SPA page rendering red-team campaign manifests + findings. The fixture already seeds a campaign on disk (verified by `RedTeamCampaign_FilesPersisted` test) so the data path exists.
- **Why deferred**: Needs an `IOutputStoreReader` extension (`ListRedTeamCampaignsAsync` + `GetRedTeamCampaignAsync`) which is an abstraction change; plus a SPA page (~80 LoC).
- **Trigger to revisit**: A red-team consumer wants to view campaign results in the portal.
- **Effort estimate**: 3–4 hours for resolver + SPA page + tests.

### MC1.4.7 — GreenDonut DataLoader batching
- **Source**: plan-08 tracking table ⬜; 10-agent review (Agent 6 IMPORTANT B3).
- **Scope**: Per-request DataLoader batching for `Subject`, `Run`, `EvaluatorCard` lookups. The current N+1 hot spot is `Query.evaluatorTimeline` (one `GetRunManifestAsync` + one `GetScenarioResultsAsync` per scanned run).
- **Why deferred**: Bounded today by the resolver's own `maxScan: 200` cap. At v1 scale (≤ 100 recent runs, ≤ 100 scenarios per run) the overhead is invisible.
- **Trigger to revisit**: When telemetry shows the SPA opening 4+ evaluator-detail pages fans out to ~800 file reads.
- **Effort estimate**: 1 week for DataLoader registration + 3–4 resolvers refactored.

### MC1.5.4 + MC1.5.5 + MC1.6.11 — SSE live updates
- **Source**: plan-08 tracking table ⬜ (all three rows).
- **Scope**: Backend `FileSystemWatcher` wiring + `GET /api/v1/events?topics=runs,compliance,redteam` SSE endpoint + SPA `useSseSubscription(topics)` client hook with TanStack Query invalidation.
- **Why deferred**: Plan-07 acceptance #3 ("live updates < 500 ms"). The SPA's `refetchOnWindowFocus: true` (added in MC1.10.1 fixes) covers the common "tab back to MC" flow without a backend file-watcher.
- **Trigger to revisit**: A CI dashboard / multi-tab consumer signal where polling vs push has observable latency impact.
- **Effort estimate**: 1 week for the full backend → SPA chain.

### MC1.7.2 — `agenteval mc export`
- **Source**: plan-08 tracking table ⬜; plan-07 acceptance #4.
- **Scope**: Static HTML bundle export of the dashboard so users can share a snapshot without running the portal.
- **Why deferred**: Pre-rendering the dynamic SPA requires a "static mode" that captures GraphQL responses inline.
- **Trigger to revisit**: A consumer asks for "share-this-dashboard-offline" specifically.
- **Effort estimate**: 1–2 weeks for the bundler + SPA static-mode toggle + integration tests.

### MC1.7.3 — `agenteval mc serve --workspace <dir>` (Mode B)
- **Source**: plan-08 tracking table ⬜.
- **Scope**: Multi-folder aggregator. CLI accepts a parent directory, discovers all `.agenteval/` workspaces under it, and Mission Control renders an aggregated dashboard across them.
- **Why deferred**: Phase-2 (Mode C) territory. Multi-folder aggregation presupposes some kind of workspace-identity story.
- **Trigger to revisit**: A platform engineer / AI lead has 5+ repos on one machine and wants one dashboard.
- **Effort estimate**: 1 week for the aggregator + tests.

### MC1.9.1 + MC1.9.2 — Notes UI/storage
- **Source**: plan-08 tracking table ⬜ (both rows).
- **Scope**: File-based notes at `.agenteval/notes/<kind>/<id>.md` + `<NoteCard/>` + `<NoteEditor/>` (react-hook-form + zod).
- **Why deferred**: Plan-07 §14 explicitly classes notes as a server-only collaborative feature (NOT synced from local). Single-user local notes are low-value (Markdown files in a folder duplicate what an issue tracker does).
- **Trigger to revisit**: Mission Control Phase 2 (Mode C server) ships and multi-user collaboration becomes the actual use case.
- **Effort estimate**: 1 week for the file-based shape; rewrite for Phase 2.

### MC1.10.2 — `--legacy-import` flag
- **Source**: plan-08 tracking table ⬜.
- **Scope**: Read-only synthesis of `benchmarks/<name>/baselines/*.json` files into in-memory `solution + subject + run` views so users with pre-`.agenteval/` data can browse it.
- **Why deferred**: One-shot migration tool for a specific legacy shape (longmemeval). MC1.10.1 first-run landing already gives users a clear path forward.
- **Trigger to revisit**: A user with the exact longmemeval shape asks for it. Fixture already exists for them to anchor against.
- **Effort estimate**: 3–4 days.

### MC1.11.1 — Visual regression tests
- **Source**: plan-08 tracking table ⬜.
- **Scope**: Playwright + screenshot diffs against `samples/AgentEval.TravelDemo.Evals/snapshots/*.json` golden fixtures.
- **Why deferred**: Playwright + screenshot diffing is its own infrastructure investment. The current 152 MC-scope backend tests + manual smoke cover the critical paths.
- **Trigger to revisit**: CI flakiness emerges, or visual regressions ship that backend tests didn't catch.
- **Effort estimate**: 1 week for the harness + 5–8 representative goldens.

### Phase 2 — Mode C self-hosted server (entire phase)
- **Source**: plan-08 §"Phase 2 — Aggregator server with sync"; ~16 weeks scope.
- **Scope**: Full multi-tenant ingestion server with EF Core + SQLite/PostgreSQL, outbox sync protocol, PAT/OIDC/RBAC, FTS5 search, server-side notes, ingest API, Docker Compose + Helm chart.
- **Why deferred**: Phase 1 must merge first. Phase 2 is target v1.5+.
- **Trigger to revisit**: After Phase 1 has been adopted by a few teams and the "multiple repos / multiple users / shared dashboard" gap becomes loud.
- **Effort estimate**: 16 weeks per plan-08 estimate.

---

## Architecture cleanup (deferred to v1.1 PR)

The 10-agent Opus review (Agent A — Architecture) surfaced four
architectural smells that are mechanical refactors. They are documented
together here because they ship cleanest as their own PR after the
current branch merges.

### CLI references sample projects
- **Source**: 10-agent review (Agent A BLOCKER).
- **Scope**: `src/AgentEval.Cli/AgentEval.Cli.csproj` adds `<ProjectReference>` to `samples/AgentEval.GdprBenchmark` and `samples/AgentEval.EuAiActBenchmark`. CLI command files hard-bind to those sample types — either samples don't deserve the `samples/` folder, or CLI is leaking sample knowledge.
- **Why deferred**: Mechanical refactor touching ~6 CLI command files + 2 csproj files + namespace renames. Risk of breaking the GDPR / EU AI Act CLI surface during the move. Cleaner as a separate slice.
- **Trigger to revisit**: When Phase 1 merges to main.
- **Effort estimate**: 1–2 days for the proper refactor (promote `AgentEval.GdprBenchmark` / `AgentEval.EuAiActBenchmark` → `src/AgentEval.Compliance.{Gdpr,EuAiAct}` and keep thin runnable `samples/*` wrappers).

### Sample → sample reference
- **Source**: 10-agent review (Agent A BLOCKER).
- **Scope**: `samples/AgentEval.EuAiActBenchmark/AgentEval.EuAiActBenchmark.csproj` references `samples/AgentEval.GdprBenchmark`. Samples should be peers; this leak exists because the cross-regulation linker (`CrossRegulationLinker.cs`) lives in the GDPR sample.
- **Why deferred**: Folds into the CLI/samples refactor above.
- **Trigger to revisit**: With the CLI/samples refactor.
- **Effort estimate**: Folded into the 1–2-day refactor above.

### Two public `AgenticBenchmark` types
- **Source**: 10-agent review (Agent A BLOCKER).
- **Scope**: `src/AgentEval.Core/Benchmarks/AgenticBenchmark.cs` (namespace `AgentEval.Benchmarks`) and `src/AgentEval.Evals.Agentic/Composition/AgenticBenchmark.cs` (namespace `AgentEval.Evals.Agentic.Composition`). Same name, different APIs — IDE collisions, ambiguous which is canonical.
- **Why deferred**: The legacy `Core/Benchmarks/AgenticBenchmark.cs` is referenced by 3 test files; renaming requires migrating those callers OR introducing `[Obsolete]` aliases. Mechanical but not zero-risk.
- **Trigger to revisit**: When Phase 1 merges. The legacy one should be renamed `LegacyAgenticBenchmark` + `[Obsolete]`, with a 1-version deprecation window.
- **Effort estimate**: 2–4 hours.

### `EvaluatorCostMap` location (Abstractions)
- **Source**: 10-agent review (Agent A IMPORTANT).
- **Scope**: `EvaluatorCostMap` lives in `AgentEval.Abstractions` but hard-codes 60 plan-05/06 evaluator keys. Abstractions shouldn't know concrete evaluator data. Belongs alongside the cards in `AgentEval.Evals.Agentic`.
- **Why deferred**: Moving the type is a one-file move + namespace rename, but every caller needs an updated `using`. ~10–15 files affected.
- **Trigger to revisit**: When Phase 1 merges to main.
- **Effort estimate**: 2–3 hours.

### Thread `judgeModel` through `AgenticBenchmark.<Preset>(judge)` factories
- **Source**: Phase-4 review-03 remediation (Task 4.4 — judgeModelName threading).
- **Scope**: `src/AgentEval.Evals.Agentic/Composition/AgenticBenchmark.cs` — every static factory (`AgenticExecution`, `ToolCallAccuracy`, `RagQuality`, `Safety`, `Conversational`, `Reasoning`, `UserExperience`, `AdversarialDirect`, etc.) accepts an `IEvaluator judge` but no `string? judgeModel` parameter. The two CLI sites that consume these factories (`BenchAgenticCommand.cs`, `BenchAgenticCalibrateCommand.cs`) currently discard the resolved `judgeModelName` because there is nowhere to thread it. The GDPR / EU AI Act bench paths thread it correctly because their builders (`ScenarioToAtomicEval`) expose `judgeModel`.
- **Why deferred**: Each factory builds 4–12 evaluators internally and passes a single `judge` into them. Threading `judgeModel` end-to-end means a parameter on every factory + every internal eval ctor that exposes `judgeModel`. Net: ~15 factory signatures + ~50 internal ctor sites. Mechanical but high diff churn and best handled when the agentic Attestation block lands (which itself is a v1.1 item — agentic bench writes `agentic-result.json`, not a compliance Attestation today).
- **Trigger to revisit**: When agentic bench grows a compliance Attestation block (when the user requests it, or when ECS2026MAF or downstream consumers need to record which judge deployment graded the agentic run).
- **Effort estimate**: 4–6 hours including tests.

---

## Cross-cutting from the 10-agent Opus review

These items are deferred for one of three reasons:

1. **Lower marginal impact** than the items addressed in the current PR.
2. **Semantic / design questions** that need consumer-driven data to answer correctly.
3. **Process changes** (test harness, CI gates) that deserve their own PR.

### Manifest body re-hashing (v2 audit chain)
- **Source**: 10-agent review (Agent 1 BLOCKER B3).
- **Scope**: Recompute the manifest's `contentHash` against its body on every read, not just trust the stored value. Combined with canonical-JSON projection (RFC 8785 / JCS) so field-order or whitespace changes don't break the audit chain.
- **Why deferred**: Significant change to `ContentHasher` + `agenteval doctor` + the schema. v1 chain enforcement (stored-hash equality) catches the most common tampering vector; the gap is documented in `docs/agenteval-workspace.md` "What the chain guarantees — and what it doesn't".
- **Trigger to revisit**: First regulator engagement, or first observed real-world tampering attack.
- **Effort estimate**: 1 week for the canonical-JSON projection + recompute-on-read path + extensive tampering test matrix.

### Evidence document body hashing
- **Source**: 10-agent review (Agent 1 BLOCKER B3).
- **Scope**: Hash the evidence JSON canonically and verify on read. Today the chain only verifies the stored `manifestHash` field; an attacker editing `controls[i].status` or `attestation.evaluator` bypasses the chain.
- **Why deferred**: Same family as the manifest re-hash; ships together.
- **Trigger to revisit**: Same as manifest re-hash.
- **Effort estimate**: Folded into the manifest re-hash work.

### Cross-evidence chain (previous-evidence pointer)
- **Source**: 10-agent review (Agent 1 BLOCKER B4 / N).
- **Scope**: Each evidence document carries a `previousEvidenceHash` pointer + this evidence's own content hash, so the timeline of attestations for a subject forms a tamper-evident chain.
- **Why deferred**: Same v2 audit-chain hardening; design choice on chain reset semantics (per-subject? per-regulation?).
- **Trigger to revisit**: When a regulator asks "show me the entire chain of attestations for subject X".
- **Effort estimate**: 1 week for the chain + verification logic + UI badge.

### `agenteval doctor` schema validation
- **Source**: 10-agent review (Agent 1 IMPORTANT I4).
- **Scope**: Doctor currently does manual property checks + hash verification but never validates files against their embedded schemas (`manifest.schema.json`, `summary.schema.json`, etc.). A malformed manifest with wrong enums passes doctor silently.
- **Why deferred**: Medium-effort + risks new test failures for any existing-but-slightly-off file. Deserves its own slice with focused review.
- **Trigger to revisit**: Before the next public release.
- **Effort estimate**: 1 day for the validator integration + tests.

### Summary schema `PENDING` enum vs manifest enum alignment
- **Source**: 10-agent review (Agent 1 IMPORTANT I5).
- **Scope**: `summary.schema.json:10` allows `[PASS, FAIL, WARN]`; `manifest.schema.json:39` allows `PENDING`. A run whose summary is written while still pending fails schema validation.
- **Why deferred**: Either align the two enums (additive) or document that summary is only written post-finalization. Needs a decision on whether pending-summary writes should exist at all.
- **Trigger to revisit**: When `agenteval doctor` schema validation lands (above).
- **Effort estimate**: 30 minutes including the test.

### Subprocess working-directory injection hardening
- **Source**: 10-agent review (Agent B IMPORTANT I1).
- **Scope**: `mc serve --workspace <path>` flows the value straight into the spawned MC subprocess's `AgentEval__Root` env var. An attacker who can influence CLI args can point MC at any directory. Acceptable for local CLI but should `Path.GetFullPath`-normalise and document the trust boundary.
- **Why deferred**: Low blast radius (single-user local), small fix.
- **Trigger to revisit**: When Mission Control runs in any non-local context (Phase 2).
- **Effort estimate**: 1 hour including documentation.

### `runCostBreakdown` unknown-bucket semantics split
- **Source**: 10-agent review (Agent 6 IMPORTANT I4).
- **Scope**: Today the `unknown` bucket mixes two distinct semantics: (a) evaluator keys not registered in `EvaluatorCostMap`, and (b) flat scenarios where `EvalResultPersistence.FromScenarioResult` returns null (so the tree can't be walked).
- **Why deferred**: Splitting into two fields (`unknownKeyCost` + `legacyFlatCost`) requires a SPA query + render change. The current copy in `RunDetailPage.tsx` is honest about what `unknown` includes.
- **Trigger to revisit**: When users complain they can't tell whether their costs are unregistered evaluators or legacy flat outputs.
- **Effort estimate**: 2 hours for the resolver + SPA update.

### GraphQL introspection gating in production
- **Source**: 10-agent review (Agent 6 cross-cut MC review).
- **Scope**: Introspection is on unconditionally in `McHost.cs`. Plan-07 §14 calls for gating behind `workspace:admin` in production. For Mode A's single-user trust boundary this is acceptable; Mode B/C will need gating.
- **Why deferred**: Phase 2 multi-tenant work.
- **Trigger to revisit**: Mode B (multi-workspace) or Mode C (multi-tenant) ships.
- **Effort estimate**: 2 hours including environment-gated config.

### `CapByWorstAggregation` test delegation tautology
- **Source**: 10-agent review (Agent C IMPORTANT 2).
- **Scope**: `CapByWorstAggregationTests` computes the expected value by calling `WeightedSumAggregation.Instance.Aggregate(...)` on the same inputs. Defensible as a "no-cap-applied" contract pin, but if `WeightedSum` regresses, this test silently regresses with it.
- **Why deferred**: Low impact; test still catches the most common regressions.
- **Trigger to revisit**: Before the next public release.
- **Effort estimate**: 1 hour to add hard-coded numeric expectations.

---

## Review-03 remediation deferrals (Phase 8, 2026-05-11)

The eight-phase review-03 plan closed every P0 / P1 finding. The items below
were deliberately deferred for v1.1 (Phase 8 tasks 8.1 – 8.10).

### Composite-preset bench output — historical note (closed in 2.1 / 2.2)
- **Source**: Phase-8 Task 8.1; closed by Phase-2 Tasks 2.1 (GDPR) + 2.2 (EU AI Act).
- **Scope**: Bench composite presets like `standard+healthcare` previously crashed at `SaveReportAsync` because the on-disk schema enum only listed the 6 base presets. The fix split the preset into a `preset` enum + `domainPacks: string[]` array.
- **Why deferred (historical only)**: This entry is preserved so a future reader of `deferred-pending.md` understands why the schema carries the seemingly-redundant pair.
- **Trigger to revisit**: N/A — already closed. Remove this entry when the next deferred-pending refresh runs.
- **Effort estimate**: 0 hours (closed).

### Memory + Evals.Agentic NuGet packaging
- **Source**: Phase-8 Task 8.2; Phase-2 Task 2.4 chose Path A (bundle into the umbrella `AgentEval` nupkg).
- **Scope**: A future v1.1 may split `AgentEval.Memory` and/or `AgentEval.Evals.Agentic` into standalone NuGet packages so consumers can pull only the subsystem they need (smaller dep graph, faster restore, independent versioning).
- **Why deferred**: Path A unblocks v1 consumers without paying the packaging overhead. The standalone-package route adds 2 csproj migrations + nuspec authoring + cross-package compatibility tests; not worth the v1 churn when umbrella works.
- **Trigger to revisit**: When a downstream consumer reports `AgentEval` (the umbrella) is too large to depend on (typical signal: a Function App / AOT-compiled console that wants only the agentic pieces).
- **Effort estimate**: 4–6 hours (carve out csproj + nuspec; rewire AddAgentEvalAll DI extension to be additive).

### Evaluator card-vs-code category drift (scope correction)
- **Source**: Phase-8 Task 8.3; 10-agent Opus review.
- **Scope**: 38 of the 60 plan-05 / plan-06 evaluator cards have category metadata that disagrees with the runtime registration. The earlier `deferred-pending.md` entry said "some cards drift"; the actual scope is 38/60 — a much wider surface that warrants a dedicated card-vs-code reconciliation pass.
- **Why deferred**: Mechanical scrub touching every card file + matching `EvaluatorCostMap` registration. Out of scope for v1 because the GraphQL `evaluators.category` field can already be served from either source (the runtime registration is authoritative; cards are documentation).
- **Trigger to revisit**: When the Mission Control evaluator-registry UI ships richer per-card metadata that auditors will read end-to-end.
- **Effort estimate**: 6–8 hours (38 card edits + reconciliation tests).

### BenchAgenticCalibrate dispatch coverage breakdown
- **Source**: Phase-8 Task 8.4.
- **Scope**: `BenchAgenticCalibrateCommand` dispatches against 11 of the 60 agentic evaluators today (Plan-05 phases 1-3 evaluators + a slice of Plan-06). The remaining 49 evaluators (Plan-05 phase 4-5 safety/telemetry/stochastic-stability composites and Plan-06 reasoning/UX/adversarial composites) are not yet wired into the calibrate dispatch table.
- **Why deferred**: Adding them requires authoring 49 new calibration goldens (one per evaluator) — a content lift, not a code lift. Premature without consumer demand for per-evaluator calibration.
- **Trigger to revisit**: When a consumer asks "how well-calibrated is the X evaluator?" for an X that isn't in the 11.
- **Effort estimate**: ~30 minutes per new evaluator × 49 = ~24 hours of golden-authoring + dispatch-table wiring.

---

## How to use this document

- **For PR authors**: when reviewing this branch's merge PR, scan this doc to confirm none of the deferrals affect your area. If they do, flag in the PR comments.
- **For new contributors**: this is the canonical "what's NOT in v1" list. Pick an item with a clear trigger condition + manageable effort estimate and propose it as a follow-up PR.
- **For consumers**: this doc explains the honest scope of v1. The disclaimers in `docs/benchmarks/{gdpr,eu-ai-act}/getting-started.md` and `docs/agenteval-workspace.md` mirror what's captured here.

This doc is meant to be **living** — when a deferred item ships, remove
its entry. When new deferrals emerge from a review, add them with the
same shape.

---

*Last updated: 2026-05-11 after the review-03 8-phase remediation pass.
Captures items deferred from plans 00–08 + the post-review remediation
batches (commits `129a98b` / `8b4d691` / `a157f7a`) + the review-03
Phase-8 entries above.*
