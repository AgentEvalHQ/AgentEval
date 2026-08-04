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
- The repository build baseline at the merged LongMemEval commit
  (`bb2cf6a2f5ae75e16dea196b246d5003eebc8df4`) is zero errors and 175 existing warnings. New work
  must introduce no additional warnings.
- Live semantic calibration is never treated as evidence of production generalization when it uses
  the calibration set itself. Inline promotion still requires representative data and the existing
  calibration safeguards.

## Release posture

The core Gatekeeper phases are merged into `main` at the baseline above. The showcase follow-up is complete
and reviewed on the current local branch, but remains intentionally unpublished under the local-only instruction.
Phase 4's deferred endpoint check limits only the promotion claim for a real remote A2A boundary; it does not erase
the completed local composition, gate implementation, or reviewed calibration. No unsupported or demand-gated
surface may be reported as fully enforced.
