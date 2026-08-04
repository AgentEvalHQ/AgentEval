# Gatekeeper implementation status

> **Status:** implementation complete for the currently approved scope; one live promotion
> validation remains deferred.
>
> **Updated:** 2026-08-04
>
> **Merged baseline:** [PR #147](https://github.com/AgentEvalHQ/AgentEval/pull/147)

This page is the tracked, publication-safe status record for Gatekeeper. Detailed threat scenarios,
calibration corpora, and internal design records remain outside the repository. A green check means
the applicable implementation and focused review are complete. A pause means the item is
deliberately demand-gated or waiting for an external prerequisite, rather than partially
implemented.

## Phase status

| Phase | Scope | Status | Notes |
|---:|---|:---:|---|
| 0.5 | Confirmed defect corrections | ✅ | Fail-closed enforcement, hosted-tool policy, calibration safeguards, stable session identity, and enforcement semantics are merged |
| 0 | Validation infrastructure | ✅ | Bounded replay corpus, deterministic reporting, and promotion thresholds are merged |
| 1 | Boundary and reuse spikes | ✅ | Result injection, intent coherence, MCP provenance, retry detection, and opaque-tool seam decisions are merged |
| 2 | Tool-usage contract engine | ✅ | Fluent and JSON contracts, seven deterministic predicates, stateful limits, hidden-instruction prefilter, and aggregate review are merged |
| 3 | Containment core | ✅ | Resolved options, signed containment storage, containment gates, escalation wiring, precise correlation, and camouflaged refusal are merged |
| 4 | Cross-agent boundary | ⏸️ | A2A composition plus inbound/outbound gates and reviewed calibration are complete; real remote-endpoint validation remains the only promotion item |
| 5 | Resource isolation | ✅ | HTTP resource isolation is implemented and promoted; additional resource types remain demand-gated until a concrete exhaustion mode exists |
| 6 | Security graph and escalation | ✅ | Durable graph storage/computation, ingestion, global containment, and the read-only operations surface are merged |
| 7 | Applicable long-tail work | ✅ | Mock dangerous-tool fixtures and session-identity drift coverage are complete; deployment-specific predicates and judges remain demand-gated |
| 8 | Documentation truth and information architecture | ✅ | High-level introductions, focused references, state and isolation operations, and a validated 19-entry sample manifest are complete and reviewed locally |
| 9 | Sample reliability foundation | ✅ | Samples 00–09 are deterministic offline-first hybrids, contracts are standardized, and supported composition is explicit |
| 10 | Architecture showcase | ✅ | Five offline samples expose measured resource isolation, state lifecycle, same-batch ordering, graph escalation, and HTTP wire enforcement |
| 11 | Specialized showcase | ✅ | Six offline samples cover dynamic providers, Crescendo trajectories, identity takeover, manifest drift, approval edges, and result anomalies |

## Sample and documentation showcase follow-up

This follow-up does not change the core phase percentages. It turns the implemented controls into deterministic,
discoverable demonstrations and records review evidence separately.

| Phase | Task | Description | Done | Reviewed | Implementation notes |
|---|---|---|---:|:---:|---|
| Showcase | S.1 | Inventory samples and map gates/features/boundaries | 100% | ✅ | Added complexity/execution catalog plus gate-boundary and feature coverage matrices |
| Showcase | S.2 | Register mocked SQL/browser/cloud/package fixture | 100% | ✅ | Offline menu sample; 4 allowed mock bodies and 5 hostile calls blocked |
| Showcase | S.3 | Add poisoned-tool kill-chain demonstration | 100% | ✅ | Fake MCP poison withheld and isolated; bulk read, email, POST, delete and propagation effects remain zero |
| Showcase | S.4 | Protect a Harness-owned capability | 100% | ✅ | Discovers todos_add at runtime, blocks weird-request misuse, preserves benign control |
| Showcase | S.5 | Add jailbreak-to-tool-abuse demonstration | 100% | ✅ | Obvious input stops pre-model; paraphrase remains bounded by shell, deletion and recipient contracts |
| Showcase | S.6 | Document gate lifecycle and coordination | 100% | ✅ | Added lifecycle/order/shared-state guide, sample index, TOC and cross-links |
| Showcase | S.7 | Demonstrate tool-result admission | 100% | ✅ | Fake credential is masked and oversized diagnostics are truncated before model context; clean bounded control is unchanged |
| Showcase | S.8 | Demonstrate the provider-hosted coverage boundary | 100% | ✅ | Unacknowledged hosted code execution refuses promotion; acknowledgment admits risk without fabricating interception or inflating 50% coverage |
| Showcase | S.R | Focused review and validation | 100% | ✅ | Release sample build 0 warnings; 1,383 Gatekeeper tests; 6/6 offline samples; formatter clean; DocFX has 32 existing and 0 new warnings; scoped MAF Doctor has 0 errors |

## Documentation and discovery improvement

| Phase | Task | Description | Done | Reviewed | Implementation notes |
|---|---|---|---:|:---:|---|
| 8 | 8.0 | Audit and freeze the improvement backlog | 100% | ✅ | Scored 19 core samples and prioritized state, Bulkhead, reliability, and architecture-showcase gaps |
| 8 | 8.1 | Correct documentation truth and simplify introductions | 100% | ✅ | Reduced the introduction to protected seams, one quick start, operating principles, architecture tiers, limits, and navigation; corrected stale links and semantics |
| 8 | 8.2 | Restructure and complete the gate reference | 100% | ✅ | Replaced the encyclopedia entry point with a selection index and four focused references organized by protected boundary and operator concern |
| 8 | 8.3 | Add state ownership and lifecycle matrix | 100% | ✅ | Documents scope, owner, partitioning, concurrency, reset/release, restart, missing-scope behavior, fingerprinting, and evidence for every stateful mechanism |
| 8 | 8.4 | Document resource isolation and containment operations | 100% | ✅ | Covers separate HTTP pools, routing authority, permit ownership, Active/Indeterminate behavior, metrics, disposal, and the downstream shared-quota ceiling |
| 8 | 8.5 | Add validated sample manifest and cross-suite discovery | 100% | ✅ | Added a strict 19-entry manifest, stable 11A/11B identifiers, launcher/source/catalog synchronization test, and memory-security/Agent Skills links |
| 8 | 8.R | Documentation promotion review | 100% | ✅ | Formatter clean; manifest test passes on net8/net9/net10; 1,384 Gatekeeper tests pass on net10; Release samples build; DocFX has 0 errors and no Gatekeeper-owned warnings |

## Sample reliability foundation

| Phase | Task | Description | Done | Reviewed | Implementation notes |
|---|---|---|---:|:---:|---|
| 9 | 9.1 | Bound live sample output and review MAF findings | 100% | ✅ | Local call caps reduced cost findings from 29 to one remote-A2A limitation; live pipelines gained non-sensitive OpenTelemetry; the remaining three telemetry warnings are offline scripted fixtures |
| 9 | 9.2 | Standardize threat, guarantee, and pass-oracle output | 100% | ✅ | All 19 sample entry points render one embedded manifest contract; tests enforce fields, packaging, source declarations, launcher registration, and catalog ids |
| 9 | 9.3 | Add deterministic offline-first paths to live samples 00–09 | 100% | ✅ | All ten hybrid samples execute scripted attack + benign controls, throw on invariant failure, use fake/local effects, and retain optional bounded Azure overlays |
| 9 | 9.4 | Normalize supported multi-layer composition | 100% | ✅ | Samples 02 and 04–09 use `UseGatekeeper`; 00, 01, 03 and non-runtime fixtures declare why their specialist low-level surface is intentional |
| 9 | 9.R | Sample reliability promotion review | 100% | ✅ | Ten offline oracles pass; Release samples build clean; manifest passes net8/net9/net10; 1,384 net10 Gatekeeper tests pass; formatter clean; scoped MAF Doctor B/0 errors |

## Architecture showcase

| Phase | Task | Description | Done | Reviewed | Implementation notes |
|---|---|---|---:|:---:|---|
| 10 | 10.1 | Demonstrate Bulkhead and containment isolation | 100% | ✅ | Measured independent peaks of 3 normal and 1 isolated permit while contained saturation could not starve normal work |
| 10 | 10.2 | Demonstrate state ownership and lifecycle | 100% | ✅ | Proves run reset, stable-session reload, rate-window reset, and signed durable containment after store reopen |
| 10 | 10.3 | Demonstrate the same-batch exfiltration race | 100% | ✅ | Contrasts the honest `SequenceGate` limitation with enforced `SameBatchOrderingGate`; five controls and a zero-effect oracle pass |
| 10 | 10.4 | Demonstrate security-graph incident response | 100% | ✅ | Bounded ingestion → durable compute → real read-only Mission Control projection → containment → future refusal; incomplete coverage cannot mint a decision |
| 10 | 10.5 | Demonstrate the HTTP wire boundary | 100% | ✅ | Fake DNS/transport prove redirect, private-address, limit, cancellation, and non-disclosure behavior without network access |
| 10 | 10.R | Architecture-showcase promotion review | 100% | ✅ | Five launcher oracles pass; Release build 0 warnings; manifest net8/net9/net10; 1,384 Gatekeeper tests; formatter; DocFX 0 errors; scoped MAF Doctor B/0 errors |

## Specialized showcase

| Phase | Task | Description | Done | Reviewed | Implementation notes |
|---|---|---|---:|:---:|---|
| 11 | 11.1 | Dynamic context-provider coverage boundary | 100% | ✅ | Automatic inventory-gap detection refuses promotion; a real gated provider filters unsupported runtime tools and emits content-free evidence |
| 11 | 11.2 | Crescendo multi-turn trajectory | 100% | ✅ | Deterministic slow escalation emits one shadow compromise, allows the observed run, quarantines the next, and preserves frustrated-safe/direct-danger controls |
| 11 | 11.3 | Session-identity takeover and reload | 100% | ✅ | Contrasts weak object identity with stable logical identity across reload; proves non-poisoning, repeated use, and atomic concurrent binding |
| 11 | 11.4 | Prompt/MCP manifest provenance drift | 100% | ✅ | Allows canonical schema reformatting while refusing prompt/description/server drift, missing provenance, and duplicate qualified identities |
| 11 | 11.5 | Approval decision matrix | 100% | ✅ | Routine arguments auto-run; sensitive, risky, mismatched, and judge-failure paths pause; real reject/approve continuations measure effects |
| 11 | 11.6 | Tool-result behavioral anomaly | 100% | ✅ | Contrasts a fixed cap with per-tool run baselines, repeated non-poisoning anomaly handling, and run reset |
| 11 | 11.R | Specialized-showcase promotion review | 100% | ✅ | Six launcher oracles pass; synchronized manifest/catalog has 30 entries; Release build 0 warnings; regressions, formatter, DocFX, and scoped MAF review are green |

The Crescendo sample intentionally stays deterministic and offline. Live semantic promotion remains owned by the
calibrated-corpus workflow, avoiding a second uncalibrated model path that could be mistaken for production evidence.

## Deferred and demand-gated work

| Item | State | Reactivation condition |
|---|:---:|---|
| Phase 4 promotion review | ⏸️ | An explicitly authorized real remote A2A endpoint is available for the final live validation |
| External MAF Workflows blockers | ⏸️ | The upstream framework exposes a stable supported enforcement seam |
| Non-HTTP resource isolation | ⏸️ | A named deployment demonstrates a measurable non-HTTP exhaustion mode |
| SQL/browser/cloud/package predicates | ⏸️ | A production caller and concrete policy contract are identified |
| Calendar/physical-action gates | ⏸️ | A production calendar or actuator integration needs enforcement |
| Additional semantic judges | ⏸️ | A specific judge has a justified use case and a representative calibration corpus |

Deferred items are not included in completion percentages and are not represented as enforced
coverage.

## Validation snapshot

- The latest merged repository validation recorded 28,745 passing tests, four intentional skips,
  and zero failures across the supported target frameworks.
- Gatekeeper phase reviews include focused adversarial tests, full-suite regressions, and scoped
  MAF Doctor checks on changed MAF code.
- Phase 8 review adds a mechanically validated 19-entry sample manifest, a clean formatter check,
  manifest contract passes on net8/net9/net10, 1,384 passing net10 Gatekeeper tests, and a successful
  Release sample build. DocFX completes with zero errors and 28 existing warnings, none owned by the
  changed Gatekeeper documentation.
- Phase 10 adds five self-validating offline architecture samples and expands the synchronized manifest/catalog to 24 entries. All five launcher runs pass; the Release sample build has 0 warnings; the manifest passes net8/net9/net10; 1,384 Gatekeeper tests pass; formatter is clean; DocFX completes with 0 errors and 28 existing non-Gatekeeper warnings; scoped MAF Doctor remains B/0 errors.
- Phase 11 adds six self-validating offline specialized samples and expands the synchronized manifest/catalog to 30 entries. All six launcher runs pass; the Release sample build has 0 warnings; the manifest passes net8/net9/net10; 1,384 Gatekeeper tests pass; formatter is clean; DocFX completes with 0 errors and only existing non-Gatekeeper warnings; scoped MAF Doctor remains B/0 errors.
- The final scoped MAF Doctor review reports grade B and zero errors. Its eight observability warnings are
  deterministic offline fixtures. The three cost heuristics are the pre-existing remote-A2A/server and scripted-helper
  limitations plus one false positive whose Crescendo call has both agent-level and explicit per-run token caps.
- The repository build baseline at the merged LongMemEval commit
  (`bb2cf6a2f5ae75e16dea196b246d5003eebc8df4`) is zero errors and 175 existing warnings. New work
  must introduce no additional warnings.
- Live semantic calibration is never treated as evidence of production generalization when it uses
  the calibration set itself. Inline promotion still requires representative data and the existing
  calibration safeguards.

## Release posture

The core Gatekeeper phases are merged into `main` at the baseline above. The showcase follow-up and Phases 8–11
are complete and reviewed on the current local branch, but remain intentionally unpublished under the local-only
instruction.
Phase 4's deferred endpoint check limits only the promotion claim for a real remote A2A boundary; it does not erase
the completed local composition, gate implementation, or reviewed calibration. No unsupported or demand-gated
surface may be reported as fully enforced.
