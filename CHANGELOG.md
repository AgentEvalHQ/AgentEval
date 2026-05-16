# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — Pre-merge polish from last-review parallel Opus sweep (2026-05-16)

Eight merge-critical items (M1-M8) plus four pulled-forward v1.1 items (1.5 / 1.6 / 1.7 / 3.2) landed in the pre-merge bundle. See `strategy/FutureFeatures/todo/lastreview/00-summary.md` for the full audit trail.

- **`AtomicLlmEval` now populates `EstimatedCost` from real judge token usage** (closes F-002). The `IEvaluator` interface gained an `EvaluationResult.InputTokenCount` / `OutputTokenCount` pair; `ChatClientEvaluator` lifts those from the underlying `ChatResponse.Usage`. `AtomicLlmEval` looks them up against a new `AgentEval.Abstractions.Evals.JudgeCostMap` (per-1K input/output rates by model id, with substring fallback for dated suffixes like `gpt-4o-mini-2024-07-18`) and writes the dollar figure into `EvalResult.Provenance.EstimatedCost`. Composite cost rollups via `CostRollup.Aggregate` now sum to real dollars rather than $0. Consumers that filtered on `EstimatedCost == 0` to detect "no LLM call happened" must switch to checking the trace's evaluator-kind field instead.
- **`Recommendation` is now a structured record across both compliance benchmarks** (closes the `v0.8.1-beta` `getting-started.md:172` disclaimer for GDPR + the parallel EU AI Act disclaimer). The new record is `Recommendation(string ControlId, string Severity, string Text, IReadOnlyDictionary<string,string>? Metadata = null)`, replacing the legacy `string[]` shape in `GdprComplianceEvidence` / `EuAiActComplianceEvidence`. Both `gdpr-evidence.schema.json` and `eu-ai-act-evidence.schema.json` use an `anyOf` union at the `items` level so legacy `string[]` evidence files written by 0.8.0-beta still validate against the v0.8.1-beta schema. The optional `metadata: { string: string }` field is reserved for v1.2+ extensions (evidence references, correlation ids) without requiring a breaking schema change. Markdown renderer output changes from `<text>` to `` `<controlId>` [<severity>]: <text>`` per entry. **PDF reports do NOT include recommendations** — by design, the PDF is the boardroom-signed artefact and the Markdown report + evidence JSON carry actionable remediation copy; rendering recommendations in the PDF is tracked as a v1.1 markdown-reporter-parity item.
- **EvaluatorCard categories reconciled with the runtime** (closes F-006). A new `CardRuntimeMetadataParityTest` enumerates every embedded card JSON and asserts the card's `category` matches the runtime class's `Category` property (or the static `CategoryValue` constant on evaluators with complex constructors). Found and fixed drift on 37 of 60 cards: `safety`→`safety-security` (12 cards), `process`→`agentic-process` (6), `system`→`system-outcome` (5), `quality`→`rag` (7), `telemetry`→`operational` (6), `stochastic-stability`→`operational` (1). Downstream consumers (Mission Control SPA filter chips, `--budget-tier` filter) need to read the new category values; the GraphQL `evaluator(key)` resolver returns them verbatim. A single `GraphQLSmokeTests` assertion was updated to match the renamed `system-outcome` value.
- **`AgentEval.Core.Benchmarks.AgenticBenchmark` is now `[Obsolete]` with a v1.2 removal target.** The deprecation message points consumers at the canonical `AgentEval.Evals.Agentic.Composition.AgenticBenchmark` preset factory. 20 existing call sites (19 in `AgenticBenchmarkTests.cs`, 1 in `04_BenchmarkSystem.cs`) raise CS0618 warnings — they're intentionally not migrated in this PR; migration is tracked for v1.2 alongside the type's removal.
- **Eight merge-critical items closed across the `last-review` parallel Opus sweep** — see `lastreview/00-summary.md` for the M1-M8 audit trail. Highlights:
  - **EU AI Act Pillar 1 thresholds corrected.** Four `art-5-1-*.yaml` files had inverted thresholds (`pass:0.70, warn:0.85` — WARN above PASS, the inverse of every other YAML); now `pass:0.85, warn:0.70` matching the rest of the benchmark. Affects every consumer that read `WarnThreshold` directly (the field was previously parsed-but-unused, so no runtime behaviour change today — but the data is now correct).
  - **HR Art 22 severity drop fixed.** `art-22-hiring-decisions.yaml` previously declared `severity: "high"` while the base `art-22-automated.yaml` declared `critical`. Result: HR-domain Art 22 failures (an ATS auto-rejecting candidates without human review, for example) registered as `high` and slipped past both the `CriticalFindingExtractor` and `CapByWorstAggregation` in AuditGrade. Now aligned to `critical / 0.85 / 0.75`.
  - **`WorkspaceRootValidator` threaded through `MigrateCommand`, `DoctorCommand`, and `McServeCommand`.** A malformed or non-existent `--root` / `--workspace` argument now returns exit code 1 from the validator before any path operations run, matching the contract of every other workspace-aware command. Four new bad-root tests added.
  - **Umbrella `AgentEval` NuGet package now ships the agentic evaluator suite.** `<PackageReference Include="AgentEval" />` consumers gain access to `AgentEval.Evals.Agentic.*` types and a new `services.AddAgentEvalAgentic()` stable DI hook (no-op today; future per-evaluator services land behind the same signature).
  - **Plain-English `how-it-works.md` explainer pages** added per benchmark; existing `getting-started.md` docs swept for fragile counts and replaced with qualitative bands where the numbers churn between releases.
  - Doc-drift items in the GDPR + EU AI Act + agentic explainers fixed (4 factual errors I'd authored in the first pass, including missing the `Safety` category in the agentic doc).

### Security — LR7 hardening extras (2026-05-16)

Three small additive hardening items closed after the LR7 audit, on top of the M1-M8 + Option C bundle.

- **`Permissions-Policy` header added to Mission Control.** Locks down geolocation, microphone, camera, payment, USB, MIDI, magnetometer, gyroscope, and accelerometer — none of which the portal ever uses. Defense-in-depth against a future XSS bug or an operator who follows the Dockerfile LAN-expose example.
- **`additionalProperties: false` added to top-level evidence wrappers.** `gdpr-evidence.schema.json` and `eu-ai-act-evidence.schema.json` now reject unknown top-level keys. Closes a real attack-surface gap where tampered tooling could inject arbitrary wrapper-level keys past schema validation.
- **`category` field on `evaluator-card.schema.json` is now enum-constrained** to the 14 canonical category strings. The M1.6 / LR3-008 class of card↔runtime drift bugs (37 cards corrected in this PR) is now caught at schema-validation time, before `CardRuntimeMetadataParityTest` even runs. Add new values to the enum when a new runtime category lands.

### Security — Mission Control portal audit findings (Phase-0 close, 2026-05-13)

Three findings from the in-depth Mission Control portal security audit (2026-05-13). 0 P0 blockers were found; the three items below are P1 hardening that the audit recommended landing before merge to `main`.

- **`Query.complianceEvidence` now enforces the per-doc audit chain (plan-07 §7).** The resolver previously returned the evidence document blind to whether `evidence.SourceRun.ManifestHash` still matched the actual `RunManifest.ContentHash`. The aggregated `ComplianceMatrix` already enforced the check; this resolver now mirrors it. New return shape `ComplianceEvidenceWithChain { evidence, chainValid, chainBreakReason }` — `chainBreakReason` is `null` (valid), `"source-run-not-found"` (orphaned evidence), or `"hash-mismatch"` (tamper signal). The SPA's evidence-detail page now renders a red "Audit chain broken" banner + a `valid` / `broken` shield badge in the Audit-chain section. (Breaking schema change to the `complianceEvidence` GraphQL field; SPA query updated in this PR.)
- **`FileSystemOutputStore` constructor no longer sweeps stale sentinels.** The 24h+ sweep of `*.invalid.json` / `*.lock` / `*.tmp` files moved out of the constructor into a new explicit `SweepStaleSentinelsAsync(TimeSpan olderThan)` method. CLI writer entry points (`bench gdpr` / `bench eu-ai-act` / `bench agentic`) call sweep after constructing the store; Mission Control (read-only viewer per plan-07 §1) does not. Closes the previous contract violation where MC startup silently deleted files outside Docker.
- **`Dockerfile` `docker run` example bound to `127.0.0.1`.** The comment example at `Dockerfile:13` previously showed `-p 5000:5000`, which publishes the unauthenticated portal on all host interfaces. Now shows `-p 127.0.0.1:5000:5000` (matching `docker-compose.yml`) with an explicit `# SECURITY:` note explaining when LAN exposure is acceptable.

### Changed (BREAKING) — audit-chain hash format

The `ContentHasher` now binds the **canonical-serialised manifest** (with `contentHash` zeroed) into the hash domain, in addition to summary + scenarios + traces. Three consequences:

1. **Workspaces written by 0.8.0-beta will fail `VerifyAsync` under 0.8.1-beta.** The hash format is intentionally different: pre-0.8.1-beta `contentHash` covered only summary + scenarios + agent-trace.json, so a `manifest.run.verdict` tamper went undetected. The new domain binds operator / host / git provenance to the run. No migration tooling ships in 0.8.1-beta — re-run `agenteval bench …` to regenerate evidence.

2. **Every `traces/*.json` file** is now hashed (previously only `agent-trace.json`). Per-test trace artefacts written by `TraceArtifactManager` were silently excluded from the audit chain pre-0.8.1-beta; they're now covered.

3. **Manifest property order in the canonical-hash bytes is pinned alphabetically** via a hand-written converter (`CanonicalRunManifestConverter`). Adding a new field to `RunManifest` requires updating the converter — a deliberate hash-format change, not an accidental one.

### Changed — `manifest.run.kind` enum extended

The `manifest.schema.json#/properties/run/properties/kind` enum gained `"benchmark"` (alongside existing `eval`/`memory-benchmark`/`stochastic-eval`/`compliance`). Producers using `Kind: "benchmark"` (the agentic benchmark runner; some test fixtures) now validate cleanly against the schema. This is an additive, non-breaking change for any existing producer using the original values.

### Changed — JSONL appenders are now cross-process safe

`recent.jsonl` and `history.jsonl` appends serialise via a named `Mutex` (keyed on SHA-256 of the canonicalised absolute path) plus an in-process `SemaphoreSlim` short-circuit. Two parallel `agenteval bench` runs writing to the same workspace no longer interleave bytes mid-line.

### Changed — `EnsureSubjectAsync` concurrency-gated

`FileSystemOutputStore.EnsureSubjectAsync` now takes an exclusive `.lock` sentinel for the read-check-write triple. Concurrent same-name calls (e.g. parallel test fixtures sharing a workspace) serialise via the file lock; corrupt `subject.json` throws `InvalidOperationException` with manual-inspect guidance instead of a raw `JsonException`; partial-init collisions (subject directory present without subject.json on case-insensitive filesystems) are detected.

### Changed — `EvalResultPersistence` lifted-metrics keys namespaced

`ToScenarioResult` now lifts `_lifted.severity_ordinal` and `_lifted.confidence` into `ScenarioResult.Metrics` (previously `severity_ordinal` / `confidence`, which silently overwrote consumer `Dimensions` using those names as criterion keys). Readers that queried the lifted values must update to the `_lifted.*` form. Consumer dimensions named `confidence` / `severity_ordinal` are now preserved untouched.

### Changed — schema validation at every write

`FileSystemOutputStore` now calls `SchemaValidator.ValidateOrThrow` before writing `subject.json` / `manifest.json` (initial + final) / `summary.json` / red-team manifest / `solution.json`. On validation failure, the offending DTO is dumped to a sibling `.invalid.json` sidecar for debugging; the store ctor sweeps stale `.invalid.json` / `.lock` / `.tmp` sentinels older than 24 hours.

### Changed — `MultiJudgeOptions` record marked `[Obsolete]` (no removal in v1)

The `MultiJudgeOptions` record in `AgentEval.GdprBenchmark` and `AgentEval.EuAiActBenchmark` is now annotated `[Obsolete]` because Mode-B per-criterion multi-judge fan-out has moved into `ScenarioToAtomicEval` ctor flags. The `AuditGrade(articles, multiJudge)` factory signature is retained for v1 source compatibility (passing `null` continues to select single-judge behaviour); removal is scheduled for v1.1. Consumers will get a compile-time CS0618 warning when constructing `new MultiJudgeOptions(...)` — switch to the `ScenarioToAtomicEval` Mode-B configuration instead, or pass `null` to keep single-judge behaviour.

### Changed (BREAKING) — `agenteval bench <regulation> --subject` is now required

`agenteval bench gdpr`, `bench eu-ai-act`, and `bench agentic` previously defaulted `--subject` to the literal string `"default-agent"` when omitted. Phase-7 Task 7.21 removed that default: the commands now exit with code 1 and an explicit error message when `--subject` is missing. CI pipelines / scripts that depended on the default must pass `--subject <agent-name>` explicitly.

### Changed (BREAKING) — `agenteval bench eu-ai-act --input` is now required

`agenteval bench eu-ai-act` previously substituted a hard-coded built-in fixture ("I'm building an AI assistant. What should it disclose…") when `--input` was omitted. Phase-7 Task 7.22 removed the fixture: the command now exits with code 1 unless `--input <prompt>` is supplied. The other two bench commands (`bench gdpr`, `bench agentic`) still accept their own built-in fixtures — only EU AI Act required the breaking change.

### Changed — calibration commands gate on evaluation failures + new `INFRA-FAIL` status

`agenteval bench gdpr calibrate`, `bench eu-ai-act calibrate`, and `bench agentic calibrate` now treat any `EvaluationFailures > 0` as a gate failure (exit code 2) and surface the failure count alongside accuracy / kappa in both the console output and the Markdown report. A new status — `[INFRA-FAIL]` — replaces `[FAIL]` when every entry threw (Azure unreachable, transient infra error, etc.), making it possible to distinguish infrastructure breakage from a real model regression. Operators tooling on the prior `[PASS|FAIL]` only output may need a 3-way switch.

### Changed — GDPR / EU AI Act bench commands now load embedded judge system prompts

`agenteval bench gdpr` and `bench eu-ai-act` now load `gdpr-judge-system.v1.md` / `eu-ai-act-judge-system.v1.md` from the corresponding benchmark assembly's manifest resources and wire them into `ChatClientEvaluator` via the new `JudgeFactory.Resolve(..., systemPrompt: ...)` parameter. Previously the prompt files were validated by tests, embedded in the assembly, recorded in provenance — and never reached the LLM. The "Cite articles / Be conservative / Flag evasive responses" rules now actually steer the judge. Operators relying on the prior un-steered behaviour will see judgements shift; the calibration baseline should be re-run after this change.

### Added — `ComplianceMatrixCell.timestamp` GraphQL field

Mission Control's GraphQL `ComplianceMatrixCell` type now carries a `timestamp: String!` field containing the raw on-disk evidence directory name (`yyyy-MM-dd_HH-mm-ss`). The SPA reads this verbatim when building drill-through URLs. Previously the SPA round-tripped `lastEvidenceAt` through JavaScript's `Date.toISOString()`, which silently shifted to UTC — non-UTC workspaces (CET, PST, JST, …) generated URL timestamps that 404'd against the local-clock-named directory on disk. Existing clients that don't select `timestamp` are unaffected.

### Changed — `SubjectIdentity.QualifiedId` no longer in GraphQL surface

The `QualifiedId` computed property on `AgentEval.Output.SubjectIdentity` is now `internal` so Hot Chocolate's default public-property convention does not auto-bind it as a GraphQL field. It was never serialised to JSON (`[JsonIgnore]`) and there are no external consumers, but a future `{ subjects { identity { qualifiedId } } }` query would have locked it into the v1 GraphQL contract. The change is non-breaking for consumers of the `SubjectIdentity` record (the property had no external callers).

### Added — `AgentEval.Memory` shipped in the umbrella NuGet package

The Memory evaluation subsystem (memory benchmarks, LongMemEval, retention/temporal/reach-back metrics, HTML pentagon reporting) is now bundled into the `AgentEval` umbrella package. `AddAgentEvalAll()` registers `AddAgentEvalMemory()` — consumers reach all Memory APIs via `using AgentEval.Memory;` without a separate `<ProjectReference>`. `AgentEval.Memory.dll` ships in `lib/net{8,9,10}.0/` of the umbrella nupkg.

### Changed (BREAKING) — compliance evidence schemas + Attestation.EvaluatorModel

**Schema change** — Both `gdpr-evidence.schema.json` and `eu-ai-act-evidence.schema.json` split the `preset` enum into a base preset + a new required `domainPacks: string[]` field. Previously a composite preset (e.g. `"standard+healthcare"`) wrote the concatenated string into `preset`; the enum only listed 6 base names, so every composite-preset invocation crashed at SaveReportAsync. Now:
- `preset` enum is restricted to the 3 base names (`"smoke"`, `"standard"`, `"audit"`).
- `domainPacks` carries the ordered pack list (`["healthcare"]` / `["high-risk-employment", "high-risk-credit"]` / etc.).
- `GdprReportOptions` and `EuAiActReportOptions` records gained `DomainPacks: IReadOnlyList<string>?` and `JudgeModel: string?` parameters.
- **Existing on-disk `*-evidence.json` files written by 0.8.0-beta against the old schema will fail re-validation under the new schema.** No migration tooling ships in 0.8.1-beta — re-run `agenteval bench …` to regenerate.

**Attestation change** — `Attestation.EvaluatorModel` previously hard-coded the literal string `"internal"` regardless of which judge actually ran. It now records the resolved judge identifier:
- `"<deployment-name>"` when `JudgeFactory.Resolve` resolved a real Azure OpenAI judge (e.g. `"gpt-4o-deployment-01"`).
- `"stub"` when the operator opted into the stub via `AGENTEVAL_ALLOW_STUB_JUDGE=1`.
- `"override"` when a test passed `evaluatorOverride`.

Tooling that filtered on `evaluatorModel == "internal"` to identify benchmark output is now broken; switch to `evaluator == "AgentEval.GdprBenchmark"` / `"AgentEval.EuAiActBenchmark"` for the benchmark-identifier check, and use `evaluatorModel` as the judge-identifier check.

### Changed (BREAKING) — bench / calibrate CLI judge resolution

`agenteval bench gdpr` / `bench eu-ai-act` / `bench agentic` and their `calibrate` siblings now resolve their LLM judge via the new `JudgeFactory` and **refuse to run silently against the stub**. Resolution order:

1. Test override (programmatic; not user-visible).
2. All three of `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT` set → real Azure OpenAI judge via `AzureOpenAIClient` → `IChatClient` → `ChatClientEvaluator`.
3. Any of the three set but not all → exit code **2** with a diagnostic listing missing variables. **Previously**: silent fall-through to stub.
4. None set + `AGENTEVAL_ALLOW_STUB_JUDGE=1` (case-insensitive) → stub judge (deterministic 75/100) with a stderr warning. Opt-in only — **never use in CI**.
5. None set + no opt-in → exit code **2** with a help message pointing at the two recovery paths.

**Migration**: CI jobs that previously ran `agenteval bench … calibrate` without `AZURE_OPENAI_*` env vars now exit 2 instead of silently producing a stub-graded calibration report. Either set the Azure secrets OR add `AGENTEVAL_ALLOW_STUB_JUDGE=1` to the CI env (the latter only if you understand that calibration against a stub gates nothing). See [CLI Reference — Environment variables](docs/cli.md#environment-variables).

**Note** — earlier `[Unreleased]` entries below reference an `agenteval eval` command. That command was proposed in ADR-003 but never shipped; the entry should be read as "the cross-framework dataset-runner CLI surface, eventually superseded by `agenteval bench` and the in-tree `samples/AgentEval.Samples` runner."

### Added — AgentEval Mission Control Phase 1 — local viewer + GraphQL backend (plan-08)

Mission Control is the visualisation, aggregation, and governance layer on top of `.agenteval/`. Phase 1 ships the dotnet backend with the full read surface; the React + Vite SPA, CLI subcommand wiring, and Mode C self-hosted server land in subsequent phases (per plan-08).

- **`AgentEval.MissionControl`** — new .NET 10 project (`src/AgentEval.MissionControl/`) hosting the GraphQL server + REST binary endpoints. Boot via `dotnet run --project src/AgentEval.MissionControl`.
- **`IOutputStoreReader` interface** extracted from `IOutputStore` — pure additive refactor; existing implementations satisfy it for free. Mission Control consumes only the reader, verified by `ReaderOnlyArchitectureTests`. Mode A's local viewer cannot accidentally write to `.agenteval/`.
- **`RunPointer` extended** with optional `Kind`, `Score`, `DurationMs`, `EstimatedCost` fields — backwards-compatible (4-arg positional ctor still works; legacy JSON deserialises with new fields as null).
- **15 GraphQL resolvers** at `POST /graphql`:
  - `Query.solution`, `Query.subjects(kind?)`, `Query.subject(kind, name)`.
  - `Query.recentRuns(count)`, `Query.run(runId)`, `Query.runSummary(runId)`.
  - `Query.scenarios(runId)`, `Query.scenario(runId, scenarioId)`.
  - `Query.scenarioTree(runId, scenarioId)` — **recursive `EvalResult` walked in one round-trip** (the central architectural justification for choosing GraphQL over REST on the read path).
  - `Query.compliance`, `Query.complianceMatrix(regulation)` — the killer-feature compliance dashboard backend, with audit-chain validation per cell. `Query.complianceEvidence(...)`.
  - `Query.evaluators(category?, costTier?)`, `Query.evaluator(key)` — driven by 60 hand-authored + generated `EvaluatorCard` JSON files (full coverage of every shipped evaluator).
- **`EvaluatorCard` primitive** — schema-driven UI metadata per evaluator. Drop a JSON file at `src/AgentEval.Evals.Agentic/EvaluatorCards/<key>.json` and it appears in `Query.evaluators` immediately, no code change. `evaluator-card.schema.json` v1.0 in `AgentEval.DataLoaders`. Lock-down tests verify schema validation, tier-match against `EvaluatorCostMap`, source-path resolution, no duplicate keys.
- **5 REST binary endpoints**: `GET /api/v1/runs/{runId}/trace`, `/reports/{format}`, `GET /api/v1/compliance/{reg}/{subject}/{ts}/report.pdf`, `GET /api/v1/compliance/{regulation}/schema`, `GET /api/v1/subjects/{kind}/{name}/history` (NDJSON stream).
- **`GET /api/v1/version`** — server metadata for diagnostics.
- **Hot Chocolate 16 (ChilliCream OSS — *not* Microsoft)** — GraphQL server with `MaxAllowedExecutionDepth = 8` guarding the recursive `EvalResult` tree, embedded Nitro UI at `/graphql` for ad-hoc query exploration in dev.
- **`FileSystemLayout` promoted to public** in `AgentEval.DataLoaders` so Mission Control's binary endpoints can resolve canonical paths without re-implementing the layout.
- **Hybrid REST + GraphQL design** — see `docs/missioncontrol/api-design.md` for rationale. GitHub / Stripe / Shopify all do this; we're following established practice.
- **Documentation**: `docs/missioncontrol/{getting-started,portal-ready-evaluators,charting,api-design}.md`.
- **Tooling**: `tools/gen_evaluator_cards.py` — idempotent generator for boilerplate cards. Hand-authored cards take precedence; the generator only writes keys not already present.
- **Test coverage**: 35 MC integration tests (8 GraphQL smoke + 16 read-resolver/compliance + 7 binary endpoint + 1 reader-only architecture + 3 recursive-tree) on net10.0; 14 EvaluatorCard schema tests. Multi-TFM build clean (net8.0 / net9.0 / net10.0); MC tests are net10.0-gated since `Microsoft.AspNetCore.Mvc.Testing 10.0.0` is net10-only.



### Added

- **Composite evaluations primitive** (`AgentEval.Evals` namespace) — a composite eval aggregates N sub-evals into one scored result with a recursive tree of sub-results. `IEval` unifies atomic and composite evals; `CompositeEval` runs sub-evals in parallel via `Task.WhenAll` and aggregates via a pluggable `IAggregationStrategy`. Phase 1 ships `WeightedSumAggregation` (the only strategy needed for GDPR per-article rollups, Foundry's tool-call-accuracy formula, and 80% of other use cases).
- **`AtomicLlmEval` / `AtomicCodeEval`** — atomic evals wrap either an existing `AgentEval.Core.IEvaluator` (LLM-judge case) or a deterministic computation (code case). Both produce the same `EvalResult` shape so callers don't branch on type.
- **`SeverityRollup` / `CostRollup` helpers** — composite severity = max of sub-severities (`none < low < medium < high < critical`); composite cost = sum of sub-costs; cache-hit only when all subs hit cache.
- **`eval-result.schema.json` v1** — JSON Schema (draft 2020-12) for the recursive `EvalResult` tree, embedded as a resource in `AgentEval.DataLoaders`. Used for runtime validation.
- **`EvalResultPersistence`** — bridges composite results to the existing `IOutputStore`. `ToScenarioResult(result, id, name)` serialises the recursive tree as JSON inside `ScenarioResult.Output` while lifting score / pass-state / dimensions / cost to top-level fields. `FromScenarioResult(sr)` restores the tree. The existing `ContentHasher.HashRunAsync` covers the embedded JSON, so the audit chain extends to composite results with no schema or store changes.
- **`AddCompositeEvals` DI extension** — registers `WeightedSumAggregation` as the default `IAggregationStrategy`. `TryAdd` semantics preserve consumer overrides.

Verdict matrix: when no threshold is set on a composite, its label is severity-driven — `critical|high → fail`, `medium → warn`, `none|low → pass`. With a threshold, label is purely score-driven (`score >= threshold ? pass : fail`).

Tests added on this branch — Article 17 golden tree (executable spec), 24+ unit tests across atomics and composite, schema validation, persistence round-trips, DI wiring. Total suite delta: +60 tests; 2738 passing on net10.0.

- **Canonical `.agenteval/` workspace layout** — `subjects/{kind}/{name}/runs/{runId}/...` is now the single source of truth for all evaluation output. Seven v1 JSON Schemas (manifest, summary, subject, solution, history-line, evidence, red-team-manifest) are embedded as resources in `AgentEval.DataLoaders` and validated at runtime.
- **`IOutputStore` interface and three implementations** — `FileSystemOutputStore` persists to the canonical folder tree; `NullOutputStore` silently discards all writes (no-op, no filesystem side effects); `InMemoryOutputStore` accumulates data in memory for testing. All three live in the `AgentEval.Output` namespace.
- **`AgentEval.Cli` executable with `init`, `doctor`, and `migrate` subcommands** — `doctor` validates `solution.json` structure, subject-name-vs-folder consistency, per-run manifest content hashes, the compliance-evidence audit chain, and legacy paths via `LegacyPathScanner`. Exit code `2` means validation errors were found; `0` means clean.
- **`agenteval init`** — Writes three files into `.agenteval/`: `solution.json` (schema v1, random UUID, solution display name), `README.md`, and `.gitignore`. All three are sourced from embedded templates. Safe to re-run; exits cleanly if already initialized.
- **`agenteval migrate`** — Dry-run by default; pass `--apply` to commit changes. Handles three migration paths: (1) renames uppercase `.AgentEval/` → `.agenteval/` using a temp-name intermediate on Windows; (2) moves `TestResults/traces/{name}_{ts}_{*}.json` into per-subject run folders under `.agenteval/subjects/agents/{name}/runs/{ts}/traces/`; (3) moves `.agenteval/benchmarks/{Agent}/baselines/{*}.json` into `.agenteval/subjects/agents/{Agent}/baselines/v{n}.json`. Accepts `--root` to override the auto-detected workspace root.
- **Compliance evidence audit chain** — `SaveComplianceEvidenceAsync` validates each evidence document against `evidence.schema.json` and refuses to persist it when `sourceRun.manifestHash` does not match the source run's stored `ContentHash`. `agenteval doctor` re-validates the full chain on demand.
- **`ContentHasher.HashRunAsync` / `ContentHasher.VerifyAsync`** (internal) — Compute a deterministic SHA-256 hash over a run's summary, sorted scenario results, and optional trace. Used by both `CompleteRunAsync` and `agenteval doctor`.
- **`AddAgentEvalOutputStore` DI extension method** — Registered on `IServiceCollection` in `AgentEval.Output`; accepts `Action<OutputStoreOptions>` for configuring `OutputStoreMode` (`Auto`, `FileSystem`, `Null`) and an optional explicit workspace path. `InMemoryOutputStore` is available for tests but is not selectable via `OutputStoreMode` — wire it directly in DI when needed.

### Changed

- **`JsonFileBaselineStore`** gains a constructor overload `(MemoryReportingOptions, IOutputStore, SubjectIdentity?)` that dual-writes baselines to both the legacy path (source-of-truth) and the canonical store path. Existing callers using the original constructor are unaffected.
- **Four red-team compliance reporters** (`OWASPComplianceReporter`, `ISO27001ComplianceReporter`, `SOC2ComplianceReporter`, `MITREATLASReporter`) gain a `SaveReportAsync(IOutputStore, SubjectIdentity, runId, ...)` overload that maps their report types into `ComplianceEvidence` and routes through the audit chain.
- **`EvalResultStore` in the travel demo** now writes snapshots to `.agenteval/samples/AgentEval.TravelDemo.Evals/snapshots/` instead of `.AgentEval/ECS2026MAF_Evals/`.
- **`Program.cs` in the travel demo** now accepts an optional positional `1`..`5` argument to invoke a single eval directly; the interactive menu remains the default when no argument is supplied.
- **Renamed `samples/ECS2026MAF*` → `samples/AgentEval.TravelDemo*`** — Drops the conference-specific name in favour of an evergreen one. Folder, csproj, root namespace (`AgentEval.TravelDemo` / `AgentEval.TravelDemo.Evals`), `using` statements, and the sample's `EvalResultStore` snapshot path were all updated. Existing snapshots at `.agenteval/samples/ECS2026MAF.Evals/snapshots/` were moved to the new path during the rename so Eval03's hypothesis comparison continues to work without re-running.

### Fixed

- **`LegacyPathScanner`** no longer reports a false-positive `.AgentEval/` finding on Windows when the workspace already uses the lowercase `.agenteval/` folder. The previous case-insensitive lookup matched the same on-disk directory under both names.

---

### Added — Agentic Evaluator Suite Phase 6: Memory, Multi-turn, Reasoning, Calibration, Adversarial, UX (plan 06)

- **19 new evaluators** across 7 new categories — all AgentEval-original (no upstream prompty equivalents):
  - _Memory (2)_: `MemoryRecallAccuracyEval` (HIGH), `LongConversationCoherenceEval` (HIGH) — in `Memory/`.
  - _Multi-turn (3)_: `TurnCoherenceEval` (MEDIUM), `GoalTrackingEval` (HIGH), `ClarificationAppropriatenessEval` (LOW) — in `MultiTurn/`.
  - _Reasoning (4)_: `ReasoningCorrectnessEval` (MEDIUM), `GoalDecompositionQualityEval` (MEDIUM), `PlanFormulationQualityEval` (MEDIUM), `IntermediateStepHallucinationEval` (MEDIUM) — in `Reasoning/`.
  - _Calibration (3)_: `ConfidenceCalibrationEval` (LOW), `UncertaintyAcknowledgmentEval` (LOW), `SelfCorrectionQualityEval` (MEDIUM) — in `Calibration/`.
  - _Adversarial (3)_: `DirectInjectionEval` (LOW — hybrid deterministic-first), `PersonaAttackEval` (LOW — hybrid deterministic-first), `JailbreakResistanceEval` (MEDIUM — combined pattern library) — in `Adversarial/`.
  - _UX (3)_: `VerbosityAppropriatenessEval` (LOW), `ToneAppropriatenessEval` (LOW), `RefusalQualityEval` (LOW) — in `UX/`.
  - _Efficiency (1)_: `CostQualityEfficiencyEval` (TRIVIAL — pure code) — in `Efficiency/`.
- **`EvaluatorCostTier` enum + `EvaluatorCostMap` static dictionary** in `AgentEval.Abstractions/Evals/` — 46 entries spanning all plan-05 + plan-06 evaluators. Unknown keys default to `Medium` (conservative).
- **`--budget-tier {trivial|low|medium|high|all}` CLI flag** for `agenteval bench agentic` — filters out above-budget evaluators and renormalizes weights. Use `low` for dev iteration, `medium` for PR builds, omit for release gates.
- **4 new preset factories** in `AgenticBenchmark`:
  - `Conversational()` — 5 evaluators (MemoryRecall 0.25, LongConvCoherence 0.25, TurnCoherence 0.20, GoalTracking 0.20, ClarificationAppropriateness 0.10); threshold 0.80.
  - `Reasoning()` — 4 evaluators (ReasoningCorrectness 0.30, IntermediateStepHallucination 0.25, PlanFormulationQuality 0.25, GoalDecompositionQuality 0.20); threshold 0.80.
  - `UserExperience()` — 5 evaluators (ToneAppropriateness 0.30, VerbosityAppropriateness 0.25, RefusalQuality 0.20, ConfidenceCalibration 0.15, UncertaintyAcknowledgment 0.10); threshold 0.80.
  - `AdversarialDirect()` — 3 evaluators (DirectInjection 0.40, PersonaAttack 0.30, JailbreakResistance 0.30); threshold 0.95.
  - **Total agentic preset count: 11** (up from 7).
- **4 new CLI presets** — `conversational`, `reasoning`, `user-experience`, `adversarial-direct` added to `BenchAgenticCommand.ResolvePreset`.
- **`ConversationTurn` record** — `sealed record ConversationTurn(Role, Content, Timestamp?)` in `Conversation/`; carries the `EvalInput.Metadata["conversation_history"]` contract for all memory, multi-turn, and calibration evaluators.
- **`ConversationHistoryHelper`** in `Conversation/` — public helper that centralises `TryGetHistory`, `TryGetCorrectionTurn`, `FormatTranscript`, and `FormatPreviousTurn`. New conversation-history-consuming evaluators must use this helper rather than re-implementing private copies.
- **`AdversarialPatternLibrary`** in `Adversarial/` — internal helper that loads + compiles regex patterns from embedded JSON resources. Used by `DirectInjectionEval`, `PersonaAttackEval`, and `JailbreakResistanceEval`.
- **`CostFilteredCompositeBuilder.FilterByBudget`** — filters composite components by cost tier and renormalizes weights.
- **19 new per-evaluator test files** across `tests/AgentEval.Tests/Agentic/{Memory,MultiTurn,Reasoning,Calibration,Adversarial,UX,Efficiency}/`.
- **4 new E2E preset tests** — `AgenticConversationalE2ETest`, `AgenticReasoningE2ETest`, `AgenticUserExperienceE2ETest`, `AgenticAdversarialDirectE2ETest`.
- **3 new `CostFilteredCompositeBuilder` tests** — filter low, no-op all, and throw-on-empty. Plus a zero-weight-component edge-case test added in the R1-R7 polish pass.
- **R6 boundary tests** for `JailbreakResistanceEval.patternsToRun` — `Theory` covering 0/-1/-100 throw paths, plus single-pattern cap and `int.MaxValue` overflow guard.
- **5 new golden datasets** under `tests/AgentEval.Tests/Agentic/Calibration/Golden/` — ~77 hand-labeled scenarios:
  - `golden-memory-multiturn.jsonl` — 25 entries across 5 memory/multi-turn evaluators.
  - `golden-reasoning.jsonl` — 16 entries across 4 reasoning evaluators.
  - `golden-confidence-calibration.jsonl` — 12 entries across 3 calibration evaluators.
  - `golden-adversarial-direct.jsonl` — 12 entries across 3 adversarial evaluators.
  - `golden-ux.jsonl` — 12 entries across 3 UX evaluators.
- **Documentation updates** — `docs/benchmarks/agentic/getting-started.md` extended with 4 new preset rows, 7 new category sections, and a "Cost-Aware Execution" section. New `docs/benchmarks/agentic/cost-guidance.md` with per-evaluator cost-tier table, recommended budget tiers per use case, and estimated costs per preset.

---

### Added — Agentic Evaluator Suite (plan 05 Phase 1)

- New `src/AgentEval.Evals.Agentic/` project: 11 named `IEval` implementations for agent-level evaluation (Task Completion, Task Adherence with 5 sub-dimensions, Intent Identification, Intent Resolution, Task Navigation Efficiency, Tool Selection, Tool Input Accuracy, Tool Output Utilization, Tool Call Success — deterministic-first, Tool Efficiency, Tool Call Accuracy aggregate).
- Evaluator prompts under `Resources/Prompts/{system,process}/` are forked from public MIT-licensed sources (`azure-sdk-for-python` `_evaluators/*.prompty` files) and improved per the AgentEval envelope: `temperature: 0`, structured `evidence[]` output, severity rubric, sub-dimensions where applicable. Each prompt file's header carries the source URL, pinned commit SHA at fork time, and the list of modifications.
- `AgenticBenchmark.AgenticExecution()` and `.ToolCallAccuracy()` factory methods.
- New CLI verbs: `agenteval bench agentic [--preset agentic-execution|tool-call-accuracy]`, `agenteval bench agentic calibrate`, `agenteval render --benchmark agentic`.
- New CI workflow `.github/workflows/agentic-calibration.yml`.
- `AgenticBenchmarkResult` wrapper + `agentic-result.schema.json` (separate from compliance evidence).

### Notes

- Multi-judge × Mode-B mutual exclusivity continues to apply (inherited from plan-03 G7.6).
- PDF rendering is deferred to a follow-up batch; Markdown report ships in Phase 1.
- The previous Foundry-equivalent compatibility layer (`FoundryUriRegistry`, `ExternalReference`, `FoundryEquivalent()` preset) was removed; the project's relationship to upstream is **prompt provenance only** — each forked prompt cites its public MIT-licensed source in the file header, and the `findings-and-suggestions.md` document captures the upstream feedback story.

---

### Added — Agentic Evaluator Suite Phases 4 + 5: Safety + Telemetry + Stochastic Stability (plan 05 Phase 4 + 5)

- **13 safety evaluators** in `src/AgentEval.Evals.Agentic/Safety/`:
  - _Hybrid deterministic-first (3)_: `ProhibitedActionsEval` (policy-as-code, forbidden tools + patterns + approval checks → LLM fallback), `SensitiveDataLeakageEval` (regex scan for PII/secrets → LLM fallback), `SystemPromptLeakageEval` (high-signal phrase patterns → LLM fallback).
  - _Content-safety hybrid (4)_: `HateUnfairnessEval`, `SexualEval`, `ViolenceEval`, `SelfHarmEval` — each delegates to `IContentSafetyClient` when available, falls back to LLM judge. All four carry `severity: critical` and threshold 0.95.
  - _LLM judge (4)_: `IndirectAttackEval` (XPIA / cross-prompt injection), `ProtectedMaterialEval` (copyright), `CodeVulnerabilityEval` (insecure generated code), `UngroundedAttributesEval` (hallucinated facts).
  - _LLM judge with skip short-circuit (1)_: `UnsafeToolUseEval` — returns `Skipped` when no tool calls are present.
- **Policy-as-code framework** — `ProhibitedActionPolicy` (immutable record), `IPolicyResolver` interface, `StaticPolicyResolver` (single global policy), `ToolPattern` (regex-based call prohibition). Located in `Safety/Policy/`.
- **`IContentSafetyClient` / `NullContentSafetyClient`** — pluggable interface for Azure AI Content Safety integration. `NullContentSafetyClient.Instance` is the default (all zero severity → LLM fallback).
- **6 telemetry evaluators** in `src/AgentEval.Evals.Agentic/Telemetry/` — pure-code, zero LLM calls: `LatencyEval` (P99 vs. threshold), `TokenUsageEval` (token budget), `CostEval` (USD budget), `ErrorRateEval` (call error rate), `RetryRateEval` (retry rate), `ToolLatencyEval` (worst per-tool mean latency). All read telemetry from `EvalInput.Metadata["agentic_telemetry"]` (`AgenticTelemetry` record) or constructor fallback. Return `Skipped` when no telemetry data is present.
- **`StochasticStabilityEval`** in `src/AgentEval.Evals.Agentic/StochasticStability/` — pure-code meta-evaluator measuring run-to-run score consistency across N prior runs. Composite of success-rate (0.50), score-variance-inverse (0.30), and failure-mode-consistency (0.20). Reads `EvalInput.Metadata["run_results"]`. Requires ≥2 runs; returns `Skipped` for fewer.
- **`AgenticBenchmark.Safety()` factory** — 12-evaluator composite (threshold 0.90); weights 0.20/0.10/0.08/0.08/0.08/0.08/0.10/0.06/0.06/0.04/0.06/0.06 for critical-to-high dimension priority.
- **3 new CLI presets** — `agenteval bench agentic --preset safety | telemetry | stochastic-stability` added to `BenchAgenticCommand.ResolvePreset`. Safety preset uses an empty `StaticPolicyResolver`; custom policies must be supplied programmatically.
- 9 of the 13 safety-evaluator prompts (hate-unfairness, sexual, violence, self-harm, protected-material, code-vulnerability, indirect-attack, ungrounded-attributes, sensitive-data-leakage) carry MIT-attribution headers citing their public-source `.prompty` upstreams; the remaining 4 (prohibited-actions, system-prompt-leakage, unsafe-tool-use, content within the policy framework) are AgentEval-original. Telemetry and stochastic-stability are pure-code AgentEval-original operational metrics with no upstream prompts.
- **78 new tests** across `Safety/` (13 files × ~4 tests), `Telemetry/` (6 files × 3 tests), `StochasticStability/` (4 tests), `EndToEnd/AgenticSafetyE2ETest.cs` (2 tests). Total suite: 3242 passing.

### Fixed — Agentic Evaluator Suite Phases 4 + 5 (Opus review fixes)

- **Credit-card regex false-positive** (`SensitiveDataLeakageEval`): old pattern `\b(?:\d[ \-]?){13,16}\b` matched 14-digit timestamps (e.g., `20250509120000`) as credit card numbers. Replaced with an issuer-prefix–anchored pattern requiring a known Visa / MasterCard / Amex / Discover / Diners prefix. Regression test: `"order id 20250509120000"` no longer triggers; `"4532 1234 5678 9012"` (Visa) does.
- **Severity label `"none"` on failure** (`HateUnfairnessEval`, `SexualEval`, `ViolenceEval`, `SelfHarmEval`): when a strict threshold (e.g., 0.95) caused a fail on a small absolute severity (e.g., severity=0.06 → score=0.94), the severity label was erroneously `"none"` rather than `"low"`. Fixed via a `(passed, severity)` switch expression that guarantees at least `"low"` on all failure paths. Test coverage added for all four evaluators.

---

### Added — Agentic Evaluator Suite Phase 3: Multi-Judge Adjudication + Meta-Evaluators (plan 05 Phase 3)

- **`AdjudicatedMultiJudgeWrapper`** in `src/AgentEval.Evals.Agentic/Adjudication/` — wraps a panel of judges, computes inter-rater agreement (Cohen's kappa for ≥3 judges, pairwise agreement rate for 2), and conditionally invokes an adjudicator judge when agreement falls below a configurable threshold (default 0.70). Adjudication state surfaced in `Details.Dimensions` (`agreement`, `disputed`, `adjudicated`). SubResults include panel + adjudicator result when triggered.
- **`JudgeAgreementEval`** in `src/AgentEval.Evals.Agentic/JudgeQuality/` — pure-code meta-evaluator computing Cohen's kappa across a judge panel. Reads results from `EvalInput.Metadata["judge_results"]` (accepts `IEnumerable<EvalResult>`, `IEnumerable<string>` of labels, or a JSON array string). Pass threshold: 0.60.
- **`CalibrationAccuracyEval`** in `src/AgentEval.Evals.Agentic/JudgeQuality/` — pure-code meta-evaluator computing fraction of judge verdicts matching expected verdicts. Reads from `EvalInput.Metadata["calibration_pairs"]`. Pass threshold: 0.85.
- **`JudgeDriftEval`** in `src/AgentEval.Evals.Agentic/JudgeQuality/` — pure-code meta-evaluator comparing two run snapshots (`snapshot_a` / `snapshot_b` in metadata) and computing `score = 1.0 - max_delta`. Passes when max_delta < 0.05 (5%). Severity: low (meta-metric).
- **`AgenticBenchmark.JudgeQuality()`** factory — 3-evaluator meta-benchmark: `JudgeAgreementEval` (0.40), `CalibrationAccuracyEval` (0.40), `JudgeDriftEval` (0.20); aggregation `WeightedSumAggregation`; threshold 0.75. No LLM judge required.
- **New CLI preset** `agenteval bench agentic --preset judge-quality` — resolves to `AgenticBenchmark.JudgeQuality()` in `BenchAgenticCommand.ResolvePreset`.
- 3 new meta-evaluators (`judge_agreement`, `calibration_accuracy`, `judge_drift`) — all AgentEval-original; pure code, no LLM dependency.
- **13 new tests** across `Adjudication/AdjudicatedMultiJudgeWrapperTests.cs` (3), `JudgeQuality/JudgeAgreementEvalTests.cs` (3), `JudgeQuality/CalibrationAccuracyEvalTests.cs` (3), `JudgeQuality/JudgeDriftEvalTests.cs` (3), and `EndToEnd/AgenticJudgeQualityE2ETest.cs` (2).

---

### Added — Agentic Evaluator Suite Phase 2: RAG/Quality (plan 05 Phase 2)

- **8 RAG/quality evaluators** in `src/AgentEval.Evals.Agentic/Quality/`: `GroundednessEval` (4-sub-dimension composite: claim support, claim contradicted, citation accuracy, evidence coverage), `RelevanceEval`, `CoherenceEval`, `FluencyEval`, `SimilarityEval`, `ResponseCompletenessEval`, `QaCompositeEval` (weighted roll-up of all 7 quality dimensions).
- **`F1ScoreEval`** ships in `src/AgentEval.Core/Evals/` — pure-code deterministic evaluator, zero LLM dependency; useful standalone without pulling the agentic package.
- **`AgenticBenchmark.RagQuality()`** factory — 7-evaluator flat composite (groundedness 0.30, response_completeness 0.20, relevance 0.15, similarity 0.15, f1_score 0.10, coherence 0.05, fluency 0.05); threshold 0.70. Tree is intentionally flat for diagnosis; `QaCompositeEval` is the single-number roll-up for users who don't need per-dimension breakdown.
- **New CLI preset** `agenteval bench agentic --preset rag-quality`.
- **Golden dataset** `tests/AgentEval.Tests/Agentic/Calibration/Golden/golden-20-quality.jsonl` — 20 hand-labeled scenarios across 7 quality evaluators (~70% pass / 30% fail).
- 7 of the 8 RAG-evaluator prompts (groundedness, relevance, coherence, fluency, similarity, response-completeness — plus the 4 groundedness sub-dimensions sharing the parent prompt) carry MIT-attribution headers citing their public-source `.prompty` upstreams. `f1_score` is pure code (no prompt). `qa_composite` is AgentEval-original (composite of the other 7).
- **24 new tests** across `Golden/` (Groundedness, Relevance, Coherence, Fluency, Similarity, ResponseCompleteness, F1Score, QaComposite — 3 tests each) and `EndToEnd/AgenticRagQualityE2ETest.cs` (2 tests).

---

### Added — EU AI Act Compliance Benchmark (plan 04)

- New `samples/AgentEval.EuAiActBenchmark/` sample implementing an EU AI Act behavioral compliance benchmark covering 13 controls across 6 pillars (Art 5 prohibitions, Art 13/14, Art 15, Art 50, Annex III, Art 51-55 GPAI probe).
- New CLI verb `agenteval bench eu-ai-act` with presets `smoke` / `standard` / `audit` (+ `standard+high-risk-{employment,credit,education}` domain packs if E8.1-3 shipped).
- New CLI verb `agenteval bench eu-ai-act calibrate` for hand-labeled judge calibration.
- Extended `agenteval compliance render --regulation eu-ai-act` to re-render PDFs without LLM cost.
- New CI workflow `.github/workflows/eu-ai-act-calibration.yml` gating release branches on judge calibration accuracy.
- New cross-regulation linking: `CrossRegulationLinker` surfaces overlap between GDPR and EU AI Act findings.
- All Composite-Eval Phase-2 strategies reused from GDPR (CapByWorst, Min, MajorityVote, WeightedMedian, MultiJudgeWrapper) — zero new strategies added in Core, validating the expand-on-demand-then-reuse pattern.

### Notes

- This benchmark is a first-line **dialog-behavior screening tool**. It does not establish EU AI Act compliance — full compliance requires risk classification, conformity assessment, technical documentation, post-market monitoring, and (where applicable) EU database registration, none of which are in scope.
- Multi-judge x Mode-B mutual exclusivity is a known v1 limitation inherited from the GDPR plan-03 implementation.

---

### GDPR Benchmark (Plan 03)

#### Added

- **`samples/AgentEval.GdprBenchmark/`** — sample project with 21 article scenario YAMLs covering 5 GDPR pillars (Art 5, 6, 7, 8, 9, 13, 14, 15, 16, 17, 18, 20, 21, 22, 25, 32). Scenario YAMLs live under `Articles/Yaml/`; domain packs live under `DomainPacks/`.
- **Three benchmark presets** — `Smoke` (5 articles, ~$0.05/run), `Standard` (16 articles, ~$0.50/run), and `AuditGrade` (Standard + `CapByWorstAggregation` severity-aware cap, optional multi-judge consensus, Mode-B per-criterion evaluation for Critical articles Art 9 and Art 22).
- **Three domain packs** — `Healthcare` (8 scenarios targeting Art 9(2)(h) and special-category data), `HR` (7 scenarios targeting Art 6(1)(b)/(c), Art 15, and Art 17 in employment context), and `ChildrensService` (8 scenarios targeting Art 8 age-of-consent and parental consent). Composable via `--preset standard+healthcare` etc.; weights are renormalized automatically.
- **`GDPRComplianceReporter`** integrated with `IOutputStore`: writes `evidence.json` (audit-chain-validated) plus a sibling `gdpr-evidence.json` containing the recursive composite tree, per-pillar and per-article rollups, critical findings, recommendations, the verbatim disclaimer, and a GDPR attestation block. Validated against `gdpr-evidence.schema.json` before writing.
- **Markdown and PDF reporters** with PII redaction for scenarios marked `sensitive: true`. PDF reporter uses QuestPDF and includes a cover page, executive summary, per-pillar section, per-article section, audit-chain appendix, methodology note, and disclaimer.
- **Calibration suite** — 120 hand-labeled golden entries distributed across 5 GDPR pillars (30/20/40/15/15). `agenteval bench gdpr calibrate` runs the golden dataset against the configured judge and computes per-pillar accuracy and Cohen's kappa. GitHub Actions release gate requires accuracy >= 0.85 and Cohen's kappa >= 0.70 per pillar, with zero evaluation failures.
- **Five new aggregation strategies** in `AgentEval.Core/Evals/Aggregations/`: `MinAggregation`, `CapByWorstAggregation`, `MajorityVoteAggregation`, `WeightedMedianAggregation` (reusable by Foundry plan 04 and any other consumer).
- **`MultiJudgeWrapper`** primitive in `AgentEval.Core/Evals/` for N-judge parallel evaluation with majority-vote aggregation.
- **`WithExtraScenarios` extension method** on `CompositeEval` for layered domain packs. Returns a new composite with the additional `EvalComponent` entries appended; weights are renormalized across all components.
- **New CLI subcommands**: `agenteval bench gdpr [--preset] [--subject] [--root] [--runs]`, `agenteval bench gdpr calibrate`, and `agenteval compliance render --regulation gdpr [--subject] [--ts]`.

#### Changed

- **`AtomicLlmEval`** gained an optional `failureSeverity` parameter so atomic results can inherit metadata-driven severity (escalated only, via `SeverityRollup.Max`). Backward-compatible; existing callers see no behavior change.
- **`ScenarioToAtomicEval`** gained an optional `useModeB` flag and an optional list of judges; when both Critical-article flag and judge count > 1 are set, scenarios become per-criterion composites wrapped in `MultiJudgeWrapper`.

#### Fixed

- An earlier draft of `gdpr-evidence.json` could be persisted without schema validation; the reporter now validates against `gdpr-evidence.schema.json` before writing and refuses to proceed if validation fails.
- `CalibrationRunner` previously used the parent article's first-scenario criteria for every golden entry, making calibration meaningless for entries targeting other scenarios; it now looks up the matching scenario by id.
- `CalibrationRunner` previously swallowed all evaluation exceptions silently; failures are now logged to stderr and counted in the `Eval failures` column of the calibration report.

Total LoC delta: approximately +4200 production / +1124 test. Test count delta: +124 tests; suite is ~3462 passing on net10.0 across both test projects (was ~3338 before plan 03).

---

## [0.8.0-beta] - 2026-04-28

**MAF 1.3.0 + MEAI 10.5.0 Compatibility** ✅

### Changed
- **MAF upgraded from 1.1.0 to 1.3.0** — All four MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Workflows.Generators`) bumped to `1.3.0`. Verified via `dotnet-inspect` API diff: zero breaking changes in `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, and `Microsoft.Agents.AI.OpenAI`. Two attribute types (`StreamsMessageAttribute`, `YieldsMessageAttribute`) were removed from `Microsoft.Agents.AI.Workflows` — AgentEval does not reference either, confirmed via repo-wide grep. New additive APIs (not consumed by AgentEval): `AgentEvaluationExtensions`, `WorkflowEvaluationExtensions`, `IAgentEvaluator`, `AgentSkill*`, A2A SDK v1 surfaces, server-side Foundry Toolbox.
- **MEAI upgraded from 10.4.0 to 10.5.0** — Cascading bump for `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.AI.Evaluation.Quality`. Transitive dependency `System.Numerics.Tensors` bumped from `10.0.4` to `10.0.6` to satisfy the new MEAI minimum.
- **NuGetConsumer sample** — Explicit version pins updated (CPM-disabled project).
- **NuGet metadata** — `<PackageReleaseNotes>` reflects MAF 1.3.0 + MEAI 10.5.0.
- **README.md** — MAF compatibility badge updated to 1.3.0.
- **docs/installation.md, docs/maf-memory-integration.md** — Version references refreshed.
- **THIRD-PARTY-NOTICES.md** — Package version table updated (7 MAF/MEAI rows + Tensors).

### Verified
- Full test suite passes across all three target frameworks (`net8.0`, `net9.0`, `net10.0`).
- All 27 samples build.
- Zero source-code changes required for the version bump itself.

### Verification Tool
This migration was verified end-to-end via the `dotnet-inspect` skill (installed at `.github/skills/dotnet-inspect/SKILL.md`, CLI `dnx dotnet-inspect@0.7.6`) rather than by reading source from `MAF/` or `MAFVnext/` folders. See [migration-to-MAF-1.3-plan.md](migration-to-MAF-1.3-plan.md).

---

## [0.7.0-beta] - 2026-04-12

**MAF 1.1.0 GA + Memory Integration + Workflow Enhancements** 🚀

### Changed
- **MAF upgraded from 1.0.0-rc3 to 1.1.0** — All three MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) updated to 1.1.0 (first post-GA minor release). Zero source code changes required for the version bump alone — all changes in 1.1.0 are additive (new `FinishReason` property on `AgentResponse`, internal `ChatClientAgent` refactoring for per-service-call persistence, new Skills/Compaction APIs). Cascading dependency bumps: `Microsoft.Extensions.AI` 10.3.0 → 10.4.0, `Microsoft.Extensions.AI.OpenAI` 10.3.0 → 10.4.0, `Microsoft.Extensions.AI.Evaluation.Quality` 10.3.0 → 10.4.0, `System.Numerics.Tensors` 10.0.3 → 10.0.4. Full test suite (9,129 tests × 3 TFMs) passes with zero failures. Full diff analysis was completed as part of the upgrade review.
- **NuGetConsumer sample** — Updated explicit version pins to MAF 1.1.0 and MEAI 10.4.0 (CPM disabled project).
- **NuGet metadata** — Updated `PackageReleaseNotes` to reference MAF 1.1.0 + MEAI 10.4.0.
- **README.md** — Updated MAF compatibility badge and compatibility table to 1.1.0.
- **docs/installation.md** — Updated compatibility and dependency tables to MAF 1.1.0 + MEAI 10.4.0.
- **THIRD-PARTY-NOTICES.md** — Synced all MAF/MEAI/Tensors package versions to match `Directory.Packages.props`.

### Fixed
- **AgentResponseEvent handling in MAFWorkflowEventBridge** — `AgentResponseEvent` (which inherits `WorkflowOutputEvent`) was falling through to the generic `WorkflowOutputEvent` handler, triggering false `WorkflowCompleteEvent` emissions and losing `Usage`/`FinishReason`/`ExecutorId` data. Added an explicit `case AgentResponseEvent` handler before the `WorkflowOutputEvent` case. Emits new `ExecutorAgentResponseEvent` record with per-executor text, token usage, and finish reason.

### Added
- **`ExecutorAgentResponseEvent` record** — New workflow event type that extends `ExecutorOutputEvent` with `Usage` (TokenUsage?) and `FinishReason` (string?) properties. Backward-compatible via Liskov substitution.
- **`IHistoryInjectableAgent` on MAFAgentAdapter** — `MAFAgentAdapter` now implements `IHistoryInjectableAgent`, enabling synthetic conversation history injection for evaluation. Injected history is prepended to messages on next `InvokeAsync`/`InvokeStreamingAsync`, then cleared after first use.
- **Getting Started samples updated to `.AsAIAgent()` pattern** — Samples 01-05 now use `chatClient.AsAIAgent(name:, instructions:, tools:)` instead of `new ChatClientAgent(client, new ChatClientAgentOptions { ... })`. Follows MAF 1.1.0 recommended idiomatic pattern.
- **Sample: [MessageHandler] Source-Generated Executors** — New sample (C4) showing MAF's `[MessageHandler]` partial class executor pattern: deterministic text pipeline (Sanitizer → Classifier → Formatter) evaluated with standard AgentEval assertions. No LLM needed, runs offline. Added `Microsoft.Agents.AI.Workflows.Generators` 1.1.0 dependency for source generation.
- **Sample: AIContextProvider-Based Persistent Memory** — New sample (G6) demonstrating MAF's native `AIContextProvider` for persistent memory. `PersistentMemoryProvider` subclass injects stored facts via `ProvideAIContextAsync()` and extracts facts via `StoreAIContextAsync()`. Evaluated with `CrossSessionEvaluator` — zero evaluator changes required.
- **Sample: AgentSession Lifecycle** — New sample (A6) showing MAF session management: `CreateSessionAsync` → multi-turn conversation → `ResetSessionAsync` → session isolation verification. Demonstrates how `MAFAgentAdapter.ResetSessionAsync()` maps to `agent.CreateSessionAsync()`.
- **docs/maf-memory-integration.md** — New documentation mapping AgentEval.Memory concepts to MAF 1.1.0 equivalents (session lifecycle, AIContextProvider, CompactionStrategy). Includes architecture diagrams and adapter selection guide.
- **4 new MAFWorkflowEventBridge tests** — Agent-based workflow tests: `YieldsExecutorAgentResponseEvent`, `PreservesExecutorId`, `IsNotMistakenForWorkflowOutput`, `IsSubtypeOfExecutorOutputEvent`.
- **5 new MAFAgentAdapter tests** — History injection tests: `ImplementsIHistoryInjectableAgent`, `MessagesIncludedInNextInvocation`, `ClearedAfterFirstInvocation`, `WithNoHistory_OnlyPromptSent`, `ResetSessionAsync_ClearsInjectedHistory`.

---

## [0.6.0-beta] - 2026-03-05

**MAF RC3 Compatibility** ⬆️

### Changed
- **MAF upgraded from 1.0.0-rc2 to 1.0.0-rc3** — All three MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) updated to 1.0.0-rc3. Zero AgentEval source code changes required — all RC3 breaking changes (`StateKey` → `StateKeys`, provider constructor renames) are in provider base classes that AgentEval does not subclass. RC3 introduces a new REST-based agent-to-agent protocol (CopilotStudio, A2A), OpenAPI-described agent endpoints, `IAgentApplication` hosting model, and `AgentWorkerClient` transport layer. Transitive `Microsoft.Agents.ObjectModel` bumped to latest. Full test suite (2519 tests × 3 TFMs) passes with zero failures. See [MAF-Upgrade-Plan.md](MAF/MAF-Upgrade-Plan.md) for full diff analysis.
- **THIRD-PARTY-NOTICES.md** — Synced all package versions to match `Directory.Packages.props` (MAF rc1→rc3 and 7 other stale versions corrected).
- **README.md** — Added MAF compatibility badge, .NET TFM badge, and compatibility table in Installation section. Repositioned preview warning below value proposition.
- **NuGet metadata** — Added `PackageReleaseNotes` property to umbrella package.
- **docs/installation.md** — Added Compatibility section with MAF and .NET version requirements.

---

## [0.5.2-beta] - 2026-02-28

**MAF RC2 Dependency Upgrade** ⬆️

### Changed
- **MAF upgraded from 1.0.0-rc1 to 1.0.0-rc2** — All three MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) updated to 1.0.0-rc2. Zero public API breaking changes — every AgentEval dependency is byte-identical between RC1 and RC2. RC2 contains only internal telemetry restructuring (session-level OTel spans in Workflows), two internal resource leak fixes, and three new additive `[Experimental]` APIs (Agent Skills, builder-level context providers, stored-output-disabled client). Transitive `Microsoft.Agents.ObjectModel` bumped `2026.2.3.1 → 2026.2.4.1`. No AgentEval source code changes required. Full test suite passes across all 3 TFMs. See [MAF-Upgrade-Plan.md](MAF/MAF-Upgrade-Plan.md) for full diff analysis.

---

## [0.5.1-beta] - 2026-02-28

**Modularization, Cross-Framework, CLI, DI & Extensibility** 🏗️🔌

Major architectural release: monolith split into 6 sub-projects (ADR-016), universal IChatClient adapter, CLI tool, dependency injection architecture, rich evaluation output, extensibility framework, and runnable samples. Comprehensive test suite passing across all 3 TFMs.

### Added
- **Monolith Modularization (ADR-016)** — Split single `src/AgentEval` project (~203 files, ~35K lines) into 6 internal sub-projects while shipping a single NuGet package. Resolves dependency coupling: non-MAF users no longer pull `Microsoft.Agents.AI`, non-RedTeam users no longer pull `PdfSharp-MigraDoc`. Compiler-enforced dependency direction: Abstractions → Core → DataLoaders/MAF/RedTeam → Umbrella.
  - `AgentEval.Abstractions` (~48 files) — Public contracts: `IMetric`, `IEvaluableAgent`, `IStreamableAgent`, models
  - `AgentEval.Core` (~63 files) — Implementations: metrics, assertions, tracing, comparison, DI registration
  - `AgentEval.DataLoaders` (~23 files) — Dataset loaders (JSON/JSONL/CSV/YAML), exporters, output formatting
  - `AgentEval.MAF` (7 files) — Microsoft Agent Framework integration (`MAFAgentAdapter`, `MAFEvaluationHarness`)
  - `AgentEval.RedTeam` (61 files) — Security scanning, attack types, compliance reporting, PDF export
  - `AgentEval` (umbrella) — Single NuGet package containing all 6 DLLs per TFM via `TargetsForTfmSpecificBuildOutput`
  - All sub-projects use `RootNamespace=AgentEval` — zero namespace changes, zero API surface changes
  - `PrivateAssets="all"` on umbrella ProjectReferences with explicit NuGet dependency declarations
  - `InternalsVisibleTo` on all sub-projects → `AgentEval.Tests`
  - Phase 0: Fixed 11 cross-cutting coupling anomalies before split
  - See [ADR-016](docs/adr/016-monolith-modularization.md) for full rationale and alternatives considered
- **Cross-Framework IChatClient Support** — Universal adapter pattern for evaluating any `IChatClient`-based AI agent regardless of underlying framework (Azure OpenAI, Ollama, Groq, LM Studio, Semantic Kernel, etc.):
  - `IChatClient.AsEvaluableAgent()` extension method — One-liner wrapping any `IChatClient` as `IStreamableAgent` for evaluation. Located in `AgentEval.Core.ChatClientExtensions`. Parallels `.AsIChatClient()` from Microsoft.Extensions.AI.
  - `TestSummary.ToEvaluationReport()` extension method — Bridges evaluation pipeline (`TestSummary`) to export pipeline (`EvaluationReport` for `IResultExporter`). Derives time boundaries from `PerformanceMetrics`, maps `MetricResults` to `MetricScores`, supports `agentName`/`modelName`/`endpoint` provenance, sets `Category` for JUnit XML grouping.
  - **NuGetConsumer Semantic Kernel demo** — Real SK with `[KernelFunction]` plugins (`FlightPlugin.cs`) evaluated by AgentEval via the `AIFunctionFactory.Create()` bridge pattern. 8-step demo: Kernel build → plugin registration → SK↔M.E.AI bridge → tool assertions → code metrics → LLM-as-judge → performance summary. Isolated project with `Microsoft.SemanticKernel 1.72.0` and `Azure.AI.OpenAI 2.7.0-beta.2`. Located in `samples/AgentEval.NuGetConsumer/`.
  - **Sample 27: Cross-Framework Evaluation** — Universal IChatClient adapter pattern: `IChatClient` → `AsEvaluableAgent()` → evaluate → `ToEvaluationReport()` → export to Markdown.
  - **Documentation** — `docs/cross-framework.md` with capability table, SK bridge code example, NuGetConsumer link.
- **AgentEval CLI (`agenteval eval`)** — Evaluate any OpenAI-compatible AI agent from the command line without writing C#. Supports all providers (OpenAI, Ollama, Groq, vLLM, LM Studio, Azure OpenAI, etc.) via the Chat Completions API standard. Features: 15 CLI options, 7 export formats (json, junit, xml, markdown, md, trx, csv), LLM-as-judge via `--judge`, system prompt from file, stderr progress reporting for Unix piping, and CI/CD exit codes (0=pass, 1=fail, 2=usage error, 3=runtime error). Packaged as a .NET tool (`dotnet tool install AgentEval.Cli`). Located in `src/AgentEval.Cli/`.
- **Dependency Injection architecture (ADR-006)** — All core services registered via `services.AddAgentEval()`, `services.AddAgentEvalDataLoaders()`, `services.AddAgentEvalRedTeam()`, or `services.AddAgentEvalAll()`. Interface-first design: `IStochasticRunner`, `IModelComparer`, `IStatisticsCalculator`, `IToolUsageExtractor`, `ISnapshotComparer`, `ISnapshotStore`, and all exporters/loaders registered with appropriate lifetimes. Configurable via `AgentEvalServiceOptions` (lifetime, harness factory, logger factory). See `AgentEvalServiceCollectionExtensions`.
- **Rich Evaluation Output subsystem** — Structured output formatting moved to `AgentEval.DataLoaders/Output/` during modularization, contracts split to `AgentEval.Abstractions/Output/`:
  - `TableFormatter` — `PrintTable()`, `PrintComparisonTable()`, `PrintPerformanceSummary()`, `PrintToolSummary()` with dynamic column selection and ANSI variance color-coding.
  - `StochasticResultExtensions` — Fluent `result.PrintTable("Metrics")`, `result.PrintSummary()`, `result.PrintPerformanceSummary()`, `result.PrintToolSummary()`, `result.ToTableString()`.
  - `ComparisonResultExtensions` — `modelResults.PrintComparisonTable()`, `modelResults.ToComparisonTableString()`.
  - `OutputOptions` — 15+ toggle properties (`ShowScore`, `ShowPassRate`, `ShowDuration`, `ShowTTFT`, `ShowTokens`, `ShowCost`, `ShowToolCalls`, `ShowConfidenceInterval`, etc.) with `Default`, `Minimal`, `Full` static presets and fluent `With()` copy method.
  - `VerbosityLevel` enum (`None`/`Summary`/`Detailed`/`Full`), `VerbositySettings`, `VerbosityConfiguration` with environment variable support (`AGENTEVAL_VERBOSITY`, `AGENTEVAL_SAVE_TRACES`, `AGENTEVAL_TRACE_DIR`).
  - `EvaluationOutputWriter` — 4-mode writer (Summary/Detailed/Full/None) producing tool timelines, performance sections, metric sections, and full JSON trace to any `TextWriter`.
  - `AgentEvalTestBase` — xUnit test base class with automatic tracing, `RecordResult()`, `SaveTrace()`, `CreateResult()` fluent builder pattern (`TestResultBuilder`).
  - `TimeTravelTrace` — 22+ model classes for time-travel debugging (`ExecutionStep`, 13 `StepType` values, `ToolCallStepData`, `AgentHandoffStepData`, etc.).
  - `TraceArtifactManager` — `SaveTestResult()`, `SaveTrace()`, `LoadTrace()`, `ListTraceFiles()`, `GetMostRecentTrace()`, `CleanupOldTraces()`.
- **Exporter registry and DI auto-discovery** — Extensible exporter system with runtime registration:
  - `IExporterRegistry` interface (in Abstractions) — `Register()`, `Get()`, `GetRequired()`, `GetAll()`, `GetRegisteredFormats()`, `Contains()`, `Remove()`, `Clear()`.
  - `ExporterRegistry` implementation — Thread-safe `ConcurrentDictionary`, pre-populated with 5 built-in exporters (JSON, JUnit XML, Markdown, TRX, CSV) via DI.
  - DI auto-discovery: custom `IResultExporter` services registered in DI are automatically picked up by the registry.
  - `FormatName` default interface member on `IResultExporter` for string-based lookup.
  - `ResultExporterFactory` — Static factory with `Create(ExportFormat)` and `CreateFromExtension(string)`.
- **DataLoader factory and DI architecture** — Extensible dataset loading with runtime registration:
  - `IDatasetLoaderFactory` interface (in Abstractions) — `CreateFromExtension()`, `Create()`, `Register()`.
  - `DefaultDatasetLoaderFactory` implementation — Dictionary-based registry for `.jsonl`, `.ndjson`, `.json`, `.csv`, `.tsv`, `.yaml`, `.yml`. Constructor accepts `IEnumerable<IDatasetLoader>` for DI auto-discovery of custom loaders.
  - `DatasetLoaderFactory` refactored to static convenience façade delegating to `DefaultDatasetLoaderFactory`.
  - `IsTrulyStreaming` property on `IDatasetLoader` — distinguishes JSONL/CSV true streaming from JSON/YAML buffered loading.
  - `.ndjson` and `.tsv` file extension support added.
  - `DatasetTestCaseBenchmarkExtensions` — `ToToolAccuracyTestCase()` and `ToTaskCompletionTestCase()` bridging dataset test cases to benchmark types with `required_params` metadata mapping.
- **Benchmarking improvements** — DI integration and multi-prompt support:
  - `AgenticBenchmark` now accepts `IToolUsageExtractor?` via DI (defaults to `DefaultToolUsageExtractor.Instance` for non-DI usage).
  - `PerformanceBenchmark.RunLatencyBenchmarkAsync()` gained multi-prompt overload (`IEnumerable<string> prompts`) to avoid server-side caching and produce more representative latency measurements.
  - `AgenticBenchmarkOptions.AddDefaultCompletionCriteria` — boolean controlling auto-appended standard criteria.
  - Throughput benchmark `Task.Yield()` fixes for both success and error paths preventing deadlocks with synchronous agents.
- **Extensibility framework** — Plugin system and registry pattern for custom extensions:
  - `IMetricRegistry` — now DI-registered as singleton with auto-population from `IMetric` services.
  - `IAgentEvalPlugin` lifecycle interface — `InitializeAsync()`, `OnBeforeEvaluationAsync()`, `OnAfterEvaluationAsync()`, `ShutdownAsync()`, with `PluginId`, `Name`, `Version`, `Dependencies`.
  - `IPluginContext` — provides `Metrics` (IMetricRegistry), `Logger`, `Configuration`, `GetConfig<T>()`.
  - `IResultTransformer` — Post-processing with `Priority` ordering for composable result pipelines.
  - See Sample 26 for custom metrics, exporters, loaders, and attack registration via DI.
- **Sample 22: Responsible AI** — Toxicity, bias, misinformation metrics with counterfactual testing.
- **Sample 23: Benchmark System** — JSONL-loaded benchmarks: tool accuracy, latency, cost analysis with `DatasetTestCaseBenchmarkExtensions`.
- **Sample 24: Calibrated Evaluator** — Multi-model consensus evaluation with calibrated scoring.
- **Sample 25: Dataset Loaders** — Multi-format dataset pipeline: JSONL, JSON, YAML, CSV with `IDatasetLoaderFactory`.
- **Sample 26: Extensibility** — DI registries, custom metrics/exporters/loaders/attacks demonstrating all extension points.

### Changed
- **Snapshot Evaluation comprehensive review (28+ fixes)** — Major audit and hardening of the snapshot comparison and storage system:
  - *Interfaces & DI:* Added `ISnapshotComparer` and `ISnapshotStore` interfaces with DI registration (ADR-006 compliance). Added `InternalsVisibleTo` for test project access to internal helpers.
  - *Security:* Sanitized suffix parameter in `GetSnapshotPath` to prevent path traversal (CODE-22). Added `basePath` validation in `SnapshotStore` constructor (CODE-21). Fixed `SanitizeFileName` collision resistance with SHA256 hash suffix (CODE-17).
  - *Correctness:* Fixed `JsonValueKind.Null` handling in element comparison (CODE-12). Fixed boolean type guard treating `True`/`False` as compatible types (CODE-30). Fixed `SemanticComparisonResult` to store scrubbed values (CODE-33). Fixed `ComputeSimpleSimilarity` to split on all whitespace (CODE-32). Fixed `CompareArrays` to continue comparing after length mismatch (CODE-23). Fixed `LoadAsync` TOCTOU with try/catch pattern (CODE-26/35). Fixed GUID regex word boundaries (CODE-16). Fixed duration regex word boundaries to prevent false positives (CODE-15). Fixed field name passed as parameter through recursion (CODE-20/34).
  - *Validation:* Added `SemanticThreshold` [0.0, 1.0] range validation (CODE-31). Added null guards on `Compare` method (TEST-12).
  - *New features:* Added `AllowExtraProperties` option (CODE-6). Added `Delete`, `ListSnapshots`, and `Count` to `SnapshotStore` (CODE-9/18). Added epsilon-based floating-point comparison (CODE-10). Added `CancellationToken` support on all async methods (CODE-7).
  - *Performance:* Added `RegexOptions.Compiled` on all default patterns (CODE-13). Made `JsonSerializerOptions` static in `SnapshotStore` (CODE-14).
  - *Testing:* Expanded test coverage from 23 to 51+ tests. Moved tests from `Benchmarks/` to `Snapshots/` directory (TEST-1/7). Added thread safety documentation (CODE-19). Documentation aligned with code defaults and APIs.
- **Sample 27 simplified** — Removed redundant MAF flight agent (Part B, ~350 lines) already demonstrated in Samples 2-3, 9-10, and NuGetConsumer. Now focused solely on the unique Universal IChatClient Adapter pattern.
- **Cross-framework documentation fixed** — Fixed broken Semantic Kernel code example in `docs/cross-framework.md` (replaced non-existent `AsChatClient()` method with working `AIFunctionFactory.Create()` bridge pattern). Added NuGetConsumer SK demo link. Fixed capability table footnote.
- **README updated** — Sample count corrected from 26 to 27 with Sample 27 row added. Test counts now use qualitative descriptions instead of hard-coded numbers. Added CLI, DI, and cross-framework to Key Features. Expanded documentation table.
- **Roadmap updated** — Marked Red Team and CLI as shipped; added CLI Phase 2, MCP Server, Benchmark runner, and Verify.Xunit to "What's Next". Updated version history table through 0.6.0-beta.
- **System.CommandLine upgraded from 2.0.0-beta4 to 2.0.3 stable** — Breaking API change: `SetHandler` → `SetAction`, `IsRequired` → `Required`, `AddOption()` → `Options.Add()`, `AddAlias()` → constructor aliases, `root.InvokeAsync(args)` → `root.Parse(args)` then `parseResult.InvokeAsync()`. Only affects the new CLI project; no existing code referenced System.CommandLine.
- **Expanded test coverage** — New tests for DI service registration, snapshot evaluation improvements, CLI commands, cross-framework adapter, and export pipeline bridging across all 3 TFMs.

### Fixed
- **Streaming tool extraction for ChatClientAgentAdapter** — `InvokeStreamingAsync` now yields `ToolCallStarted` and `ToolCallCompleted` chunks when the underlying `IChatClient` streams `FunctionCallContent`/`FunctionResultContent`. Previously, streaming evaluations via `RunEvaluationStreamingAsync` produced empty `ToolUsageReport` for all `IChatClient`-based agents. Non-streaming path was unaffected.

---

## [0.4.0-beta] - 2026-02-22

**Security, Responsible AI & MAF RC1** 🛡️🤖

Major feature release: Red Team security scanning, Responsible AI metrics, Calibrated multi-model evaluation, MAF RC1 upgrade, and comprehensive tracing improvements. Comprehensive test suite passing across all 3 TFMs.

### ⚠️ BREAKING CHANGES

- **MAF RC1 Upgrade** - Upgraded from `Microsoft.Agents.AI 1.0.0-preview.251110.2` to `1.0.0-rc1`
  - `Microsoft.Extensions.AI` upgraded from `10.0.0` to `10.3.0`
  - `Microsoft.Extensions.AI.OpenAI` upgraded from `10.0.0-preview.1.25559.3` to `10.3.0` (preview → stable)
  - `Microsoft.Extensions.AI.Evaluation.Quality` upgraded from `9.5.0` to `10.3.0`
  - `System.Numerics.Tensors` bumped from `10.0.0` to `10.0.3` (transitive compatibility)
  - Event hierarchy fix: `AgentResponseUpdateEvent` now inherits `WorkflowOutputEvent` (critical switch restructuring in `MAFWorkflowEventBridge`)
  - Type renames: `AgentThread` → `AgentSession`, `GetNewThread()` → `CreateSessionAsync()` (sync → async)
  - Method renames: `StreamAsync` → `RunStreamingAsync`, `AddFanInEdge` → `AddFanInBarrierEdge`
  - Naming conflict resolved: `using AgentResponse = AgentEval.Core.AgentResponse;` alias in adapter files
  - `ChatClientAgentOptions.Instructions` → `ChatOptions.Instructions` across all samples (26 occurrences in 14 files)
  - **Breaking change (MAF adapters only):** Helper methods on `MAFAgentAdapter` and `MAFIdentifiableAgentAdapter` were renamed and made async: `ResetThread()` → `ResetSessionAsync()`, `GetNewThread()` → `CreateSessionAsync()`, and constructor parameter type `AgentThread?` → `AgentSession?`. Core evaluation interfaces (`IEvaluableAgent`, `IStreamableAgent`) are unchanged; only code that calls these helper methods directly must be updated.

### Added
- **Red Team Security Testing Module** - Comprehensive AI agent security evaluation
  - **9 attack types**: PromptInjection, Jailbreak, PIILeakage (LLM02), SystemPromptExtraction (LLM07), IndirectInjection, ExcessiveAgency (LLM06), InsecureOutput (LLM05), InferenceAPIAbuse (LLM10), EncodingEvasion
  - **192 total probes** across all attack categories (expanded InsecureOutput from 18→33)
  - **60% OWASP LLM Top 10 2025 coverage** (6/10): LLM01, LLM02, LLM05, LLM06, LLM07, LLM10
  - **6 MITRE ATLAS techniques**: AML.T0024, AML.T0037, AML.T0043, AML.T0045, AML.T0051, AML.T0054
  - **6 export formats**: JSON, JUnit XML, SARIF (GitHub Security), Markdown, PDF, CSV
  - **4 compliance reports**: OWASP, MITRE, SOC2, ISO27001
  - Fluent assertions: `result.Should().HaveOverallScoreAbove(85)`
  - Attack pipeline API: `AttackPipeline.Create().WithAllAttacks().ScanAsync(agent)`
  - Baseline comparison for CI/CD regression tracking
  - Real-time progress reporting with `ScanProgress` callback
  - Rich console output with emoji, colors, and detailed breakdowns
- **Responsible AI Metrics** (`AgentEval.Metrics.ResponsibleAI` namespace)
  - `ToxicityMetric` - Pattern + LLM hybrid toxicity detection
  - `BiasMetric` - LLM-based bias detection with counterfactual testing
  - `MisinformationMetric` - Claim verification and calibration assessment
- **Calibrated Evaluator** - Multi-model criteria-based evaluation with `CalibratedEvaluator` for consensus-driven scoring
- **CSV Export Format** - New `CsvExporter` for Excel and business intelligence tools
- **Sample 23: Responsible AI** - Toxicity, bias, misinformation metrics with counterfactual testing
- **Sample 24: Benchmark System** - Performance, agentic, standard, and cost benchmarks with comparative analysis
- **SPDX License Identifiers** - Added to all source and test files for compliance

### Changed
- **Trace Record & Replay Improvements** (9 improvements from comprehensive audit)
  - Added `IsComplete` property to `TraceReplayingAgent` for cleaner replay loops
  - Implemented `RecordStreamingChunks` conditional check — streaming chunks now only recorded when option is enabled
  - Wired up `SanitizeToolResult` in streaming recording — tool results are sanitized consistently
  - Implemented `MaxTurns` enforcement in `ChatTraceRecorder` — throws `InvalidOperationException` when limit reached
  - Fixed documentation API names across `docs/tracing.md`, `docs/conversations.md`, `docs/workflows.md`, and `docs/adr/004-trace-recording-replay.md`
  - Added cross-reference sections in `docs/conversations.md` and `docs/workflows.md` linking to tracing guide
  - Updated ADR-004 phase status to reflect current implementation state
  - Sample 13 Demos 3 & 4 rewritten from mocked to fully operational real AI workflows
  - Added 12 new tracing tests (Contains matching, Warn/Ignore mismatch, sanitization, MaxTurns)
- **Sample 13 Audit Fixes** — fixed prompt display mismatch, added `DelayMultiplier = 0.1` for fast workflow replay, removed unused `System.Text.Json` import, corrected Key Takeaways API names
- **docs/tracing.md** Performance Baseline example fixed: `Entries[0].Duration` → `Entries.First(e => e.Type == TraceEntryType.Response).DurationMs`
- Added `ConfigureAwait(false)` to MAF adapter async calls for reliability
- Replaced `Assert.True` with `Assert.Contains` for improved test readability
- Removed hardcoded version strings from documentation

---

## [0.3.0-beta] - 2026-01-25

**Brand Alignment: Evaluation-First Naming** 🎯

This release implements comprehensive renamed APIs to better reflect AgentEval's primary identity as an **AI Agent Evaluation Toolkit**. All "Test" terminology in public APIs has been renamed to "Evaluation" to align with the framework's positioning.

### ⚠️ BREAKING CHANGES

#### Interface Renames
| Old Name | New Name |
|----------|----------|
| `ITestHarness` | `IEvaluationHarness` |
| `IStreamingTestHarness` | `IStreamingEvaluationHarness` |
| `ITestableAgent` | `IEvaluableAgent` |
| `IWorkflowTestableAgent` | `IWorkflowEvaluableAgent` |

#### Class Renames
| Old Name | New Name |
|----------|----------|
| `MAFTestHarness` | `MAFEvaluationHarness` |
| `WorkflowTestHarness` | `WorkflowEvaluationHarness` |
| `TestOptions` | `EvaluationOptions` |
| `TestOutputWriter` | `EvaluationOutputWriter` |
| `TestMetadata` | `EvaluationMetadata` |

#### Method Renames
| Old Name | New Name |
|----------|----------|
| `RunTestAsync()` | `RunEvaluationAsync()` |
| `RunTestStreamingAsync()` | `RunEvaluationStreamingAsync()` |
| `RunTestSuiteAsync()` | `RunEvaluationSuiteAsync()` |
| `TestHarnessFactory` property | `EvaluationHarnessFactory` property |

#### File Renames
| Old Name | New Name |
|----------|----------|
| `ITestHarness.cs` | `IEvaluationHarness.cs` |
| `ITestableAgent.cs` | `IEvaluableAgent.cs` |
| `MAFTestHarness.cs` | `MAFEvaluationHarness.cs` |
| `WorkflowTestHarness.cs` | `WorkflowEvaluationHarness.cs` |
| `TestModels.cs` | `EvaluationModels.cs` |
| `TestOutputWriter.cs` | `EvaluationOutputWriter.cs` |
| `stochastic-testing.md` | `stochastic-evaluation.md` |
| `Sample14_StochasticTesting.cs` | `Sample14_StochasticEvaluation.cs` |

### Unchanged (Universal Terminology)
The following names are **intentionally kept** as they represent universal industry terminology:
- `TestCase` - Standard testing terminology used across all frameworks
- `TestResult` - Conflict resolution with existing `Core.EvaluationResult` type
- `TestSummary` - Consistent with TestResult
- `AgentEvalTestBase` - xUnit integration base class
- `StochasticRunner` - Neutral name, not test-specific
- `*Tests.cs` files - xUnit naming convention

### Changed
- **Terminology:** "stochastic testing" → "stochastic evaluation" throughout codebase and documentation
- **Terminology:** "test harness" → "evaluation harness" throughout codebase and documentation
- **XML Documentation:** Updated all public API comments with evaluation-first language
- **C# Naming Conventions:** Fixed parameter names to use camelCase (`evaluationOptions` instead of `EvaluationOptions`)
- **Documentation:** Title case capitalization fixes in markdown headers
- **Documentation:** Fixed all broken links to `stochastic-testing.md` (now `stochastic-evaluation.md`)
- **TOC:** API Reference section now renders consistently with other menu items

### Migration Guide

Update your code to use the new names:

```csharp
// Before (0.2.x)
var harness = new MAFTestHarness(evaluatorClient);
var result = await harness.RunTestAsync(agent, testCase, options);

// After (0.3.0)
var harness = new MAFEvaluationHarness(evaluatorClient);
var result = await harness.RunEvaluationAsync(agent, testCase, options);
```

```csharp
// Before (0.2.x)
public class MyAgent : ITestableAgent { }

// After (0.3.0)
public class MyAgent : IEvaluableAgent { }
```

### Documentation
- Brand Positioning Guidelines created at `strategy/plans/Implementation-Plan-Brand-Positioning-Guidelines.md`
- All documentation files updated with evaluation-first messaging
- Code examples in documentation updated to use new API names

---

## [0.2.1-beta] - 2026-01-24

**Features + Documentation & Messaging Refresh** 🚀📝

This release adds new features (enhanced token tracking, Sample 19) and updates AgentEval's positioning to better reflect its core value as an **evaluation toolkit** for AI agents.

### Added (Features)
- **Enhanced Token Usage Tracking** - Improved token usage extraction and cost estimation in `MAFTestHarness` and `PerformanceMetrics`
  - More accurate cost calculation across streaming and async scenarios
  - Better handling of model pricing for cost estimation
- **Sample 19: Streaming vs Async Performance Comparison** - New sample demonstrating:
  - Side-by-side streaming vs async performance measurement
  - Time-to-first-token (TTFT) tracking for streaming scenarios
  - Token usage comparison between execution modes
- **Interactive Demo Menu** - Enhanced samples with interactive selection and demo inputs
- **NuGetConsumer Sample Project Enhancements** - Additional demos and offline testing patterns

### Added (Documentation)
- **"Who Is AgentEval For?"** section to README.md and docs/index.md
  - .NET Teams Building AI Agents
  - Microsoft Agent Framework (MAF) Developers
  - ML Engineers Evaluating LLM Quality
- **".NET Advantage"** comparison table to README.md showing AgentEval vs Python alternatives
- **CLI Tool & Samples** section to docs/index.md
- License badge to docs/index.md

### Changed
- **New Positioning:** "The .NET Evaluation Toolkit for AI Agents" (previously "testing framework")
  - Evaluation leads (50% of codebase), followed by testing (25%) and benchmarking (25%)
  - Clearer differentiation vs Python alternatives (RAGAS, DeepEval)
- Updated test count badge across 3 TFMs
- Fixed version references from 1.0.0-alpha to 0.2.0-beta in all documentation
- Updated NuGet tags: added `rag` and `agentic` keywords
- Simplified `docs/roadmap.md` - removed internal planning details, shows only shipped features and general direction

### Removed
- `src/AgentEval/AgentEval-Design.md` - Internal design document with outdated information
- `docs/why-agenteval.md` - Content merged into docs/index.md for unified landing page

### Fixed
- Removed inaccurate "Native xUnit/NUnit/MSTest support" claim (AgentEval works WITH test frameworks, doesn't provide native integration)
- Removed fabricated testimonials from documentation
- Fixed trace replay description accuracy
- Documentation site toc.yml updated for removed files

### Documentation
- All 18+ documentation files updated with consistent messaging
- NuGet README now shows correct positioning tagline
- Strategy documents aligned with new positioning

---

## [0.2.0-beta] - 2026-01-24

**AgentEval Public Beta Release** 🎉

This release marks the transition from alpha to beta. The framework is now feature-complete for core scenarios and ready for community feedback.

### Added
- **Codecov Badge** - Coverage visibility in README.md
- **NuGet Consumer Sample** (`samples/AgentEval.NuGetConsumer/`) - Standalone project showcasing all major features
  - Tool chain assertions (HaveCalledTool, WithArgument, BeforeTool, AfterTool)
  - Performance assertions (Duration, TTFT, Cost, Token limits)
  - Behavioral policies (NeverCallTool, MustConfirmBefore, NeverPassArgumentMatching)
  - Response assertions (Contain, NotContain, length validation)
  - Mock testing with FakeChatClient
  - Stochastic testing examples
  - Model comparison patterns
  - Agentic metrics overview
  - Works offline with mock data - no Azure OpenAI required
- **Custom Domain** - AgentEval.dev documentation site with GitHub Pages
- **Comprehensive Documentation** - 25+ documentation pages with zero DocFX warnings
- **Security Scanning** - Enhanced pipeline with secret detection and dependency scanning

### Changed
- Updated README test count badge to 3000+ (reflecting 1000+ tests × 3 TFMs)
- Documentation navigation reorganized with improved feature grouping
- Security scanning patterns refined to reduce false positives
- Version bumped from 0.1.3-alpha to 0.2.0-beta signaling production readiness

### Documentation
- Getting Started, Assertions, Metrics Reference, Model Comparison guides
- Trace Record & Replay, Stochastic Testing, Benchmarks documentation
- CI/CD Integration guide with GitHub Actions examples
- Migration guide for Python/Node.js developers

---

## [0.1.3-alpha] - 2026-01-18

### Added
- **Security Scanning Pipeline** - Comprehensive automated security analysis
  - DevSkim static analysis integrated into CI/CD
  - NuGet dependency vulnerability scanning
  - Secret detection to prevent credential leaks
  - SARIF output to GitHub Security tab
  - Weekly scheduled scans plus on push/PR triggers
- **CLI Baseline Comparison** - Compare against golden files
  - `--baseline` option for snapshot testing workflow
  - Human-readable diff output with color coding
  - Exit code 2 for baseline mismatches (distinct from test failures)
- **Security Documentation** - Comprehensive security guidance
  - [SECURITY.md](SECURITY.md) - Vulnerability reporting process
  - [docs/security-scanning.md](docs/security-scanning.md) - Tech stack and architecture
  - [strategy/Implementation-Plan-Security-Hardening.md](strategy/Implementation-Plan-Security-Hardening.md) - Security roadmap
- **Input Validation Hardening** - Defense against path traversal attacks
  - CLI file path validation with directory allowlist
  - Path normalization and canonicalization
  - Extension validation for dataset files
- **Security Workflow** (`.github/workflows/security.yml`)
  - Runs on all pushes to main/develop branches
  - Runs on all pull requests
  - Scheduled weekly Monday scans for dependency updates

### Changed
- Project version bumped to 0.1.3-alpha across all packages
- Enhanced CI/CD with security gate requirements

### Security
- Implemented OWASP Top 10 mitigations for web-adjacent attack vectors
- Added anti-glassworm protections in development workflow
- PII detection in `NeverPassArgumentMatching` uses redaction by default

---

## [0.1.2-alpha] - 2026-01-04

### Added
- **Behavioral Policy Assertions** - Safety-critical assertions for enterprise compliance
  - `NeverCallTool(toolName, because)` - Assert forbidden tools were never called
  - `NeverPassArgumentMatching(pattern, because, options)` - Detect PII/secrets via regex with automatic redaction
  - `MustConfirmBefore(toolName, because, confirmationToolName)` - Require confirmation before risky actions
  - `BehavioralPolicyViolationException` with structured properties (PolicyName, ViolationType, ViolatingAction, RedactedValue)
  - 16 unit tests for behavioral policy assertions
  - Updated Sample12 with new behavioral policy examples
  - See [ADR-008](docs/adr/008-calibrated-judge-multi-model.md) for design decisions
- **Judge Calibration** - Multi-model consensus for reliable LLM-as-judge evaluations
  - `CalibratedJudge` - Wrapper for running evaluations with multiple LLM judges
  - `VotingStrategy` enum: Median, Mean, Unanimous, Weighted
  - `CalibratedResult` with Agreement %, Confidence Intervals, per-judge scores
  - `ICalibratedJudge` interface for testability
  - `CalibratedJudgeOptions` with configurable timeouts, parallelism, consensus tolerance
  - Factory pattern: `metricFactory(judgeName)` for per-judge metric instantiation
  - Parallel judge execution with graceful degradation
  - 17 unit tests for calibrated judge
  - Sample18_JudgeCalibration demonstration
  - See [ADR-008](docs/adr/008-calibrated-judge-multi-model.md) for design decisions
- **Model Comparison Markdown Export** - Shareable comparison reports
  - `ToMarkdown()` extension for `ModelComparisonResult` - Full report with all sections
  - `ToRankingsTable()` - Compact table with medal emojis (🥇🥈🥉)
  - `ToDetailedMetricsTable()` - Pass rate, latency, cost metrics
  - `ToStatisticsTable()` - Mean, median, percentiles, confidence intervals
  - `ToGitHubComment()` - Collapsible PR comment format
  - `SaveToMarkdownAsync()` - File export
  - `MarkdownExportOptions` with Default and Minimal presets
  - Batch comparison support for multiple test cases
  - 20 unit tests for markdown export
  - Updated Sample15 with markdown export demonstration
- **Trace Record & Replay (Phase 8)** - Deterministic testing and time-travel debugging
  - `TraceRecordingAgent` - Wraps any agent to capture all executions with full fidelity
  - `TraceReplayingAgent` - Replays recorded traces deterministically without LLM calls
  - `ChatTraceRecorder` - Records multi-turn conversations with turn tracking
  - `ChatExecutionResult` - Complete conversation result with aggregate performance
  - `WorkflowTraceRecorder` - Records multi-agent workflow orchestrations
  - `WorkflowTraceReplayingAgent` - Replays workflow traces step-by-step
  - `TraceSerializer` / `WorkflowTraceSerializer` - JSON serialization for traces
  - `AgentTrace`, `WorkflowTrace` - Rich trace models with metadata and performance
  - `TraceEntry`, `WorkflowTraceStep` - Detailed per-invocation/step records
  - `TraceTokenUsage`, `TraceToolCall`, `TraceError` - Supporting models
  - Streaming support for recording/replaying chunked responses
  - 168 new tests covering all tracing functionality
  - Comprehensive [tracing documentation](docs/tracing.md)
  - Sample 13: Trace Record & Replay demonstration
- **Enhanced Fluent Assertions** - Improved xUnit assertion failure experience inspired by FluentAssertions/Shouldly
  - **`because` parameter** on all assertions for documenting test intent (e.g., `HaveCalledTool("SearchTool", because: "user query requires search")`)
  - **`AgentEvalScope`** for collecting multiple assertion failures into a single exception with all failures listed
  - **Rich structured error messages** with Expected/Actual values, context, tool timeline, and actionable suggestions
  - **`[StackTraceHidden]`** attribute on assertion methods for cleaner failure stack traces
  - **`CallerArgumentExpression`** for automatic subject name capture in ResponseAssertions
  - New `AgentEvalScopeException` for batch failure reporting
  - Comprehensive [assertions documentation](docs/assertions.md) with examples
- **CLI eval command** with real dataset validation
  - Loads datasets from YAML, JSON, JSONL, and CSV files
  - Validates test case completeness, ground truth, expected tools, and context
  - Outputs results in JSON, JUnit XML, Markdown, or TRX formats
  - Cross-platform color support with NO_COLOR environment variable respect
- **Sample datasets** for quick start
  - `samples/datasets/travel-agent.yaml` - agentic evaluation with tool usage
  - `samples/datasets/rag-qa.yaml` - RAG evaluation with context documents
  - `samples/datasets/README.md` - comprehensive dataset format documentation
- **YAML dataset loader** with flexible field aliasing
  - Supports both `expected_output` and `expectedOutput` naming conventions
  - Supports `ground_truth`, `expected_tools`, and `context` fields
  - Full YAML 1.2 compliance via YamlDotNet
- **Workflow Testing Support (Phase 6B)** - Per-executor visibility for multi-agent workflows
  - `WorkflowExecutionResult` - Captures per-executor output, timing, and tool calls
  - `ExecutorStep` and `WorkflowError` models for detailed workflow analysis
  - `IWorkflowEvaluableAgent` - Extended interface for workflow-aware agents
  - `MAFWorkflowAdapter` - Adapter for MAF Workflows with streaming event capture
  - `WorkflowEvaluationHarness` - evaluation harness for workflow testing with assertions
  - `WorkflowAssertions` - Fluent assertion API for workflow execution results
  - Supports executor order validation, step timing, tool call tracking
  - 71 new tests for workflow components
- **Workflow Edge/Graph Support (Phase 6B+)** - Full DAG structure for complex workflows
  - `EdgeType` enum - Sequential, Conditional, Switch, ParallelFanOut, ParallelFanIn, Loop, Error, Terminal
  - `WorkflowEdge` - Static edge definitions with conditions and switch labels
  - `EdgeExecution` - Runtime edge traversal with routing decisions and data transfer
  - `ParallelBranch` - Tracks parallel execution branches
  - `WorkflowNode` - Node definitions with entry/exit point markers
  - `WorkflowGraphSnapshot` - Complete DAG topology with nodes, edges, and execution path
  - `RoutingDecision` - Captures conditional/switch routing decisions
  - New workflow events: `EdgeTraversedEvent`, `RoutingDecisionEvent`, `ParallelBranchStartEvent`, `ParallelBranchEndEvent`
  - Edge assertions: `HaveTraversedEdge()`, `HaveConditionalRouting()`, `HaveParallelExecution()`, `ForEdge().BeOfType()`
  - Step edge assertions: `HaveIncomingEdge()`, `HaveBeenConditionallyRouted()`, `BeInParallelBranch()`
  - `MAFWorkflowAdapter.WithGraph()` and `FromConditionalSteps()` factory methods
  - 66 new tests for edge models and assertions

### Changed
- **Test project reorganization** into logical folder structure:
  - `Core/` - AgentEvalBuilder, Logger, MetricRegistry, Retry, Normalizer, Concurrency tests
  - `Metrics/RAG/` - Faithfulness, Relevance, Context Precision/Recall, Answer Correctness
  - `Metrics/Agentic/` - Tool Selection, Arguments, Success, Efficiency, Task Completion
  - `DataLoaders/` - Dataset loader and serialization tests
  - `Exporters/` - Result exporter tests
  - `Testing/` - FakeChatClient, ConversationRunner, ConversationalTestCase tests
  - `Assertions/` - Tool usage and response assertion tests
  - `Models/` - Domain model tests
  - `Benchmarks/` - Performance and agentic benchmark tests
  - `MAF/` - Microsoft Agent Framework integration tests
- **CLI ConsoleHelper** for improved cross-platform terminal support
  - Detects NO_COLOR environment variable
  - Detects TERM=dumb terminals
  - Gracefully handles output redirection (piping to files)

### Fixed
- YAML loader tests now use correct 4-space indentation matching YAML standards
- Removed invalid `include-prerelease` input from CI workflow (actions/setup-dotnet@v4 compatibility)

---

## [0.1.2-alpha] - 2026-01-04

### Added
- Additional test coverage for core components
- XML documentation generation enabled in project configuration
- DocFX build scripts (PowerShell and Batch) for automated API documentation generation
- Comprehensive documentation guides (GENERATE-DOCS.md, DOCUMENTATION-SUMMARY.md)

### Changed
- Project now generates XML documentation files for all target frameworks (net8.0, net9.0, net10.0)
- Suppressed CS1591 warnings for undocumented members

---

## [0.1.1-alpha] - 2026-01-03

### Added
- SourceLink support for debugging into source code
- Symbol packages (.snupkg) published to NuGet.org
- NuGet package icon (AgentEvalNugetLogoAE.png)
- Azure OpenAI environment variables in CI/CD workflows

### Changed
- Repository restructured to standard .NET layout (src/, samples/, tests/, docs/)
- Central package management with `Directory.Packages.props`
- Shared build configuration with `Directory.Build.props`
- GitHub Actions CI now tests on .NET 8, 9, and 10 across Ubuntu and Windows
- CI workflow optimized with NuGet caching and fail-fast disabled

### Infrastructure
- GitHub Actions CI workflow for automated build and test
- GitHub Actions release workflow for NuGet publishing
- DocFX documentation scaffolding
- EditorConfig for consistent code style

---

## [0.1.0-alpha] - 2026-01-02

### Added

#### Core Framework
- First .NET-native AI agent testing, evaluation, and benchmarking framework
- Full Microsoft Agent Framework (MAF) integration via `MAFAgentAdapter` and `MAFTestHarness`
- Extensible adapter pattern supporting `IChatClient` and other frameworks
- Plugin system with `IAgentEvalPlugin` interface

#### Tool Usage Tracking & Assertions
- `ToolCallRecord` for capturing tool invocations with timing, arguments, results, and errors
- `ToolCallTimeline` for visualizing parallel tool execution
- Fluent assertions: `HaveCalledTool()`, `BeforeTool()`, `WithArgument()`, `HaveNoErrors()`
- Tool usage reports with success/failure metrics

#### Performance Metrics
- Real-time performance tracking with TTFT (Time To First Token)
- Per-tool timing and execution waterfall data
- Token counting (prompt/completion/total)
- Cost estimation for 8+ models (GPT-4o, GPT-4o-mini, Claude 3.5, Claude 3 Opus, GPT-4 Turbo, GPT-3.5 Turbo, o1-preview, o1-mini)
- Performance assertions: `HaveTotalDurationUnder()`, `HaveTimeToFirstTokenUnder()`, `HaveEstimatedCostUnder()`

#### RAG Metrics
- Faithfulness metric (grounded in context)
- Relevance metric (response addresses query)
- Context Precision metric
- Context Recall metric
- Answer Correctness metric

#### Agentic Metrics
- Tool Selection metric (chose appropriate tools)
- Tool Arguments metric (correct arguments passed)
- Tool Success metric (tools executed successfully)
- Task Completion metric (agent completed the task)
- Efficiency metric (minimal steps, tokens, time)

#### Benchmarks
- `PerformanceBenchmark` for latency/throughput/cost analysis
- `AgenticBenchmark` for multi-step agentic task evaluation
- Percentile statistics (p50, p90, p95, p99)
- Summary statistics (mean, min, max, standard deviation)

#### Testing Infrastructure
- `FakeChatClient` for zero-dependency unit testing
- `TestCase` model with inputs, expected outputs, evaluation criteria
- `TestResult` with comprehensive run data
- Trace-first failure reporting with structured diagnostics

#### Observability
- `IAgentEvalLogger` abstraction with console and Microsoft.Extensions.Logging adapters
- Run artifacts for debugging and "time travel" inspection
- Designed for OpenTelemetry (OTel) integration

### Technical Details
- Comprehensive unit test coverage across all target frameworks
- Multi-target framework support: .NET 8.0, 9.0, 10.0
- Zero-dependency core (optional integrations for MAF, Azure OpenAI)

---

## Future Releases

### Planned Packages
- `AgentEval` (core) ✅ This release
- `AgentEval.Maf` (MAF integration) - planned
- `AgentEval.TestKit` (fixtures/builders/helpers) - planned
- `AgentEval.Tracing` (OTel + run artifacts) - planned
- `AgentEval.Studio` (workflow visualizer / time-travel UI) - future

[Unreleased]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.8.0-beta...HEAD
[0.8.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.7.0-beta...v0.8.0-beta
[0.7.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.6.0-beta...v0.7.0-beta
[0.6.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.5.4-beta...v0.6.0-beta
[0.5.2-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.5.1-beta...v0.5.2-beta
[0.5.1-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.4.0-beta...v0.5.1-beta
[0.4.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.3.0-beta...v0.4.0-beta
[0.3.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.2.1-beta...v0.3.0-beta
[0.2.1-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.2.0-beta...v0.2.1-beta
[0.2.0-beta]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.3-alpha...v0.2.0-beta
[0.1.3-alpha]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.2-alpha...v0.1.3-alpha
[0.1.2-alpha]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.1-alpha...v0.1.2-alpha
[0.1.1-alpha]: https://github.com/AgentEvalHQ/AgentEval/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/AgentEvalHQ/AgentEval/releases/tag/v0.1.0-alpha
