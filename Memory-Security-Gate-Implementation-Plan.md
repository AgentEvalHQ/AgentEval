# Memory Security Gate implementation plan

Status: Phase 5 MCP integration complete; Phase 6 ready for implementation
Date: 2026-07-29
Target: AgentEval on Microsoft Agent Framework (MAF) .NET 1.13.0
Scope: memory exposed as local tools, MCP tools/servers, and custom `AIContextProvider` implementations
Out of scope for this document: implementation, provider credentials, production data migration, and a claim that memory poisoning can be eliminated

## Executive decision

AgentEval should add a provider-neutral `MemorySecurityGate` capability, implemented as a
coordinated set of lifecycle gates rather than one text classifier:

1. a construction-time **coverage gate** proves that every declared memory surface has an
   inspectable write and recall path;
2. a **scope-integrity gate** resolves identity from trusted host/session state and fails closed
   before storage access;
3. a **write-admission gate** evaluates provenance, trust, content policy, promotion type, and
   sensitive-data rules before persistence;
4. a **conflict/reconciliation gate** prevents lower-trust or repeated writes from overwriting or
   outvoting trusted memory;
5. a **recall-admission gate** checks scope, integrity, freshness, trust, citations, and content
   before retrieved memory enters the model context;
6. a **memory-influence gate** prevents recalled memory from silently driving sensitive tools or
   exfiltrating private content;
7. a **resource-budget gate** bounds write volume, record size, retrieval volume, and repeated
   promotion;
8. an **audit and attribution evaluator** connects later behavior to the exact memory writes and
   retrievals that influenced it.

The write path is the primary security boundary. Recall filtering and tool containment are required
defense in depth, but neither makes a poisoned durable record trustworthy.

The first implementation must be deterministic-first. Network and LLM evaluators may review
quarantined candidates or run in shadow/offline evaluation, but they must not block inline unless
they satisfy AgentEval's existing calibration requirements on a representative gold corpus.

## Index

- [Tracking table](#tracking-table)
- [Phase 0 design freeze record](#phase-0-design-freeze-record)
- [Phase 1 implementation and review record](#phase-1-implementation-and-review-record)
- [Phase 2 implementation and review record](#phase-2-implementation-and-review-record)
- [Phase 3 implementation and review record](#phase-3-implementation-and-review-record)
- [Phase 4 implementation and review record](#phase-4-implementation-and-review-record)
- [Phase 5 implementation and review record](#phase-5-implementation-and-review-record)
- [Project ownership and dependency freeze](#project-ownership-and-dependency-freeze)
- [Goals and non-goals](#goals-and-non-goals)
- [Evidence and current architecture](#evidence-and-current-architecture)
- [Threat model](#threat-model)
- [Required invariants](#required-invariants)
- [Coverage model](#coverage-model)
- [Proposed contracts](#proposed-contracts)
- [Gate catalog](#gate-catalog)
- [Mandatory sequencing](#mandatory-sequencing)
- [MAF integration by memory surface](#maf-integration-by-memory-surface)
- [Evaluation and release gates](#evaluation-and-release-gates)
- [Implementation phases](#implementation-phases)
- [Test matrix](#test-matrix)
- [Documentation and samples](#documentation-and-samples)
- [Risks and resolved decisions](#risks-and-resolved-decisions)
- [Acceptance criteria](#acceptance-criteria)
- [Sources](#sources)

## Tracking table

`Reviewed` becomes ✅ only after the task's changed code or design artifact has received a focused
review and all findings have been fixed. `Done` becomes 100 only after its applicable tests or
document checks pass.

| Phase | Task | Description | Size | Done | Reviewed | Depends on | Implementation notes |
|---:|---|---|:---:|---:|:---:|---|---|
| 0 | 0.1 | Freeze threat model, source adoption matrix, and security claims | M | 100 | ✅ | — | Sources classified; adopted requirements and claim boundaries frozen 2026-07-30 |
| 0 | 0.2 | Freeze MAF surface/coverage model | M | 100 | ✅ | 0.1 | Per-operation coverage and fail-closed rules frozen for all six surface classes |
| 0 | 0.3 | Freeze public contracts, verdict actions, and sequencing | L | 100 | ✅ | 0.1–0.2 | Requirements, stage/action legality, ownership, pipeline order, and promotion gates frozen |
| 0 | 0.R | Architecture and threat-model review | M | 100 | ✅ | 0.1–0.3 | Security, MAF, API, dependency, privacy, and warning-baseline findings resolved |
| 1 | 1.1 | Add memory operation, scope, provenance, and policy models | L | 100 | ✅ | 0.R | Immutable bounded models; defensive collection copies; strict enum, identifier, stage, and digest validation |
| 1 | 1.2 | Add `IMemoryGate` and deterministic pipeline | L | 100 | ✅ | 1.1 | Frozen deterministic gates; cancellation propagation; one-pass sanitization; explicit observe/enforce behavior |
| 1 | 1.3 | Add safe evidence, receipts, reason codes, and fingerprints | M | 100 | ✅ | 1.1–1.2 | Content-free receipts/fingerprints; raw and effective content excluded from JSON serialization |
| 1 | 1.4 | Add quarantine and approval capability preflight | M | 100 | ✅ | 1.2 | Typed capabilities; enforcing policies fail construction without required disposition services or explicit fallback |
| 1 | 1.R | Core contracts review | M | 100 | ✅ | 1.1–1.4 | Fixed enum acceptance, fingerprint omissions, and observe-mode mutation/enforcement; 30 tests pass on each supported TFM |
| 2 | 2.1 | Implement `MemoryScopeIntegrityGate` | L | 100 | ✅ | 1.R | Required host scope, safe aliases, mismatch rejection/explicit ignore, administrative override, and no broader read scope |
| 2 | 2.2 | Implement `MemoryWriteAdmissionGate` | L | 100 | ✅ | 1.R | Bounded provenance/trust/category/promotion checks, hidden-character rejection, instruction quarantine, and secret/PII redaction |
| 2 | 2.3 | Implement `MemoryConflictGate` | L | 100 | ✅ | 2.1–2.2 | Higher-trust protection, same-lineage deduplication, and independent same-content corroboration |
| 2 | 2.4 | Implement `MemoryRecallAdmissionGate` | L | 100 | ✅ | 1.R, 2.1 | Owner scope, state, TTL, integrity, citation, trust, instruction exclusion, and escaped bounded delimiters |
| 2 | 2.5 | Implement `MemoryResourceBudgetGate` | M | 100 | ✅ | 1.R | Host snapshots enforce write/rate/retrieval/promotion/reconciliation/lineage/quarantine caps fail closed |
| 2 | 2.R | Deterministic gate review | M | 100 | ✅ | 2.1–2.5 | Corrected corroboration, scope breadth, delimiter, ignored-policy, and serialization privacy findings; bounded non-backtracking regexes |
| 3 | 3.1 | Add explicit memory tool contracts and classifier | L | 100 | ✅ | 2.R | Immutable exact-name registry; descriptive hints only flag unclassified memory-like tools and never assign operation semantics |
| 3 | 3.2 | Adapt write/read tools to `IToolGate` and `IToolResultGate` | L | 100 | ✅ | 3.1 | Pre-call mutation/block and post-result redact/exclude adapters compose through the existing single tool middleware |
| 3 | 3.3 | Implement `MemoryInfluenceGate` over recalled-memory lineage | L | 100 | ✅ | 3.1–3.2 | Reuses run-scoped `TaintTrackingGate`; explicit memory sources and sensitive sinks; cancellation and safe reasons |
| 3 | 3.4 | Add memory-tool coverage analyzer | M | 100 | ✅ | 3.1–3.3 | Per-tool coverage refuses unknown/hosted paths and mismatched registries/pipelines; configurable minimum coverage |
| 3 | 3.R | Tool-surface review | M | 100 | ✅ | 3.1–3.4 | Fixed observe-mode overclaim, pipeline/registry mismatch, null sanitizer, cancellation, and transient-content serialization findings |
| 4 | 4.1 | Implement `GatedAIContextProvider` decorator | L | 100 | ✅ | 2.R | Wraps one provider; freezes state keys; delegates service lookup; carries no per-session instance state |
| 4 | 4.2 | Gate recalled messages/instructions/tools before merge | L | 100 | ✅ | 4.1 | Bounded snapshots; messages/instructions gated before return; dynamic memory tools require matching registered coverage |
| 4 | 4.3 | Gate source messages before delegated persistence | L | 100 | ✅ | 4.1 | Successful invocation messages gated before provider delegation; generic provider remains honestly capped at `Boundary` |
| 4 | 4.4 | Define provider-native candidate-write hook | L | 100 | ✅ | 4.1–4.3 | Versioned provider identity/capabilities plus candidate-write and recalled-item hooks |
| 4 | 4.R | Context-provider review | M | 100 | ✅ | 4.1–4.4 | Fixed structured-content bypass, raw-representation leakage, identity binding, bounded failure, and continuation observability |
| 5 | 5.1 | Protect local `McpClientTool` memory operations | M | 100 | ✅ | 3.R | Exact server/version/transport/schema binding composes with existing AIFunction call/result middleware; client-only remains `Boundary` |
| 5 | 5.2 | Add owned MCP server-side memory gate adapter | L | 100 | ✅ | 2.R | Generic wrapper gates immediately before backend access and recalled-result serialization; backend is never called after denial |
| 5 | 5.3 | Add hosted MCP coverage/approval policy | L | 100 | ✅ | 3.4 | Per-operation `Unsupported`/`ActionOnly`/`FullLifecycle`; full requires complete callbacks or matching stage-complete owned-server evidence |
| 5 | 5.4 | Add MCP read-result admission and safe error mapping | M | 100 | ✅ | 5.1–5.3 | Read results allow/sanitize/deny before return; backend/adapter errors expose only owned reason and correlation codes |
| 5 | 5.R | MCP review | M | 100 | ✅ | 5.1–5.4 | Fixed bounds, schema ambiguity, invented semantics, incomplete fingerprints/matching, and empty-pipeline overclaim |
| 6 | 6.1 | Add persistent memory-poisoning attack corpus | L | 0 | — | 2.R | Four write channels plus cross-session activation |
| 6 | 6.2 | Add deterministic security and utility evaluators | L | 0 | — | 6.1 | CompositeEvals-compatible |
| 6 | 6.3 | Add mocked SQL, browser, email/cloud, MCP, and context providers | L | 0 | — | 3.R–5.R | No real provider required |
| 6 | 6.4 | Add calibration and confidence-interval reporting | L | 0 | — | 6.1–6.3 | No uncalibrated inline semantic judge |
| 6 | 6.R | Benchmark/evaluator review | M | 0 | — | 6.1–6.4 | Label leakage, judge validity, denominator honesty |
| 7 | 7.1 | Integrate memory protection into `UseGatekeeper` safely | L | 0 | — | 3.R–5.R | Preflight before builder mutation |
| 7 | 7.2 | Add DI, configuration, reports, and schema support | L | 0 | — | 7.1 | Policy provenance in every report |
| 7 | 7.3 | Add samples, migration guide, and operational runbook | L | 0 | — | 6.R–7.2 | Tool, MCP, and context-provider samples |
| 7 | 7.4 | Run full offline and authorized live validation | L | 0 | — | 7.1–7.3 | All TFMs; live only with explicit authorization |
| 7 | 7.R | Release-readiness review | M | 0 | — | 7.1–7.4 | All acceptance criteria and claims verified |

## Phase 0 design freeze record

> **Decision date:** 2026-07-30
>
> Phase 0 is documentation and architecture only. No runtime memory-security code is claimed by
> completing these rows. The decisions below are the entry contract for Phase 1.

### Task 0.1 — source and threat adoption

Sources are classified so that guidance, production patterns, attack evidence, and experimental
results are not presented as equivalent proof. Dates are publication dates when available;
"accessed" identifies a mutable documentation page reviewed on that date.

| Source | Date | Classification | Adopted requirement | Claim boundary |
|---|---|---|---|---|
| [OWASP ASI06](https://genai.owasp.org/2026/05/13/memory-is-a-feature-it-is-also-an-attack-surface/) | 2026-05-13 | Industry threat guidance | Treat persistent context as a cross-session trust boundary; cover poisoning, scope, integrity, monitoring, and recovery | Guidance identifies risk and controls; it does not certify this implementation |
| [MITRE ATLAS AML.T0080](https://atlas.mitre.org/techniques/AML.T0080) | accessed 2026-07-30 | Attack taxonomy | Model memory poisoning as a durable adversary technique and preserve attack/evidence mapping | Taxonomy is not a prevention standard or effectiveness result |
| [Microsoft — Guarding AI memory](https://www.microsoft.com/en-us/security/blog/2026/06/22/guarding-ai-memory/) | 2026-06-22 | Vendor security guidance | Separate trusted identity from model content; retain provenance; use layered write, recall, and action controls | Recommendations are adopted as design input, not a Microsoft certification |
| [Microsoft — AI Recommendation Poisoning](https://www.microsoft.com/en-us/security/blog/2026/02/10/ai-recommendation-poisoning/) | 2026-02-10 | Attack evidence and guidance | Test repeated influence, source-trust manipulation, recommendation drift, and delayed effects | Reported attacks motivate tests; their rates are not AgentEval performance claims |
| [MAF agent safety](https://learn.microsoft.com/en-us/agent-framework/agents/safety) and inspected 1.13.0 source | accessed 2026-07-30 | Framework contract and guidance | Use supported middleware, approval, session, and context-provider seams; preflight before builder mutation | Framework seams define observability, not automatic memory protection |
| [AWS AgentCore Memory best practices](https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/best-practices.html) | accessed 2026-07-30 | Cloud production guidance | Validate before persistence, apply least privilege, encrypt provider storage, and security-test extraction paths | Infrastructure security remains a shared responsibility outside AgentEval's gates |
| [GitHub Copilot agentic memory](https://github.blog/ai-and-ml/github-copilot/building-an-agentic-memory-system-for-github-copilot/) | 2026-01-15 | Production pattern | Scope memory, retain citations, revalidate evidence at use time, handle conflicts, expiry, and stale branches | Citation revalidation is domain-dependent and does not prove general truth |
| [Mem0 memory security practices](https://mem0.ai/blog/ai-memory-security-best-practices) | 2026-07-23 | Vendor guidance and production pattern | Layer pre-write validation, per-user/session isolation, inclusion/exclusion, TTL, audit, integrity, and monitoring | Provider capability statements must be verified in the configured deployment |
| [MINJA](https://arxiv.org/abs/2503.03704) | 2025-03 | Primary attack research | Include query-only planting, dormancy, cross-session trigger, and ordinary-user access | Paper results motivate the corpus; they are not release thresholds |
| [MemMorph](https://arxiv.org/abs/2605.26154) | 2026-05 | Primary attack research | Test memory-driven tool selection and persistent tool hijacking | Transfer to each model/provider must be measured |
| [No Attacker Needed](https://arxiv.org/abs/2604.01350) | 2026-04 | Primary failure/attack research | Treat cross-user contamination and incorrect sharing as security failures even without malice | The plan does not assume every contamination event is adversarial |
| [MemPoison](https://arxiv.org/abs/2605.29960) | 2026-05 | Primary attack research | Include policy-satisfying poison that bypasses simple prompt-injection filters | No single content classifier may claim complete coverage |
| [Trojan Hippo](https://arxiv.org/abs/2605.01970) | 2026-05 | Primary attack research | Include sleeper activation and memory-assisted exfiltration | Prevention and attribution are measured separately |
| [The Misattribution Gap](https://arxiv.org/abs/2605.22842) | 2026-05 | Primary audit research | Preserve write-to-recall-to-action lineage and support counterfactual audit | Attribution evidence identifies influence, not objective causality in every case |
| [A-MemGuard](https://arxiv.org/abs/2510.02373), [SMSR](https://arxiv.org/abs/2606.12703), and [MemAudit](https://arxiv.org/abs/2605.23723) | 2025-10 to 2026-06 | Experimental defenses | Evaluate composite trust, sanitization, retrieval controls, and audits offline against frozen gold data | Experimental defenses remain shadow/quarantine candidates until locally calibrated |

Frozen security claims:

- AgentEval will reduce, expose, and measure memory risk; it will not claim that poisoning can be
  eliminated.
- A deterministic gate proves only the operation and seam it observes. Coverage never transfers to
  hidden provider extraction, another client, or an uninstrumented repository path.
- Scope, provenance, hashes, content filtering, reconciliation, recall filtering, and action
  containment solve different problems. None is evidence that a natural-language memory is true.
- Vendor controls are recognized only when the configured adapter can prove their presence and
  scope. Marketing or documentation alone is not runtime evidence.
- Published attack and defense rates are motivation, not AgentEval accuracy, calibration, or release
  thresholds.
- An LLM or network judge remains offline, shadow, or quarantine-only until a representative frozen
  corpus demonstrates the existing inline-calibration requirements. A semantic result never
  overrides a deterministic confirmed scope leak, unauthorized write, or exfiltration.

### Task 0.2 — surface and coverage freeze

Coverage is assigned per operation, not per provider or package. The following table freezes the
maximum claim available from each MAF surface; actual coverage may be lower when a required hook,
contract, scope resolver, or result seam is absent.

| Surface | Observable read seam | Observable write seam | Maximum frozen coverage | Enforcing rule |
|---|---|---|---|---|
| Local MAF memory tool | Pre-call plus post-result function middleware | Explicit arguments before the function call | `FullLifecycle` for that declared invocation path only when candidate, trusted scope, result, and derived actions are visible; otherwise `Boundary` | Require an explicit operation contract; names and descriptions are never authority |
| Local `McpClientTool` | Local AIFunction call/result | Local MCP arguments before transport | `Boundary` from the client alone; `FullLifecycle` requires an owned server gate or equivalent provider-native hook | Pin server identity and schema fingerprint; changed schema invalidates coverage |
| Owned MCP memory server | Before repository/service access and before result serialization | Authenticated server request immediately before persistence | `FullLifecycle` when server scope, candidate writes, recalled items, and client-side derived actions are all gated | Enforce server-side even when the originating client is gated |
| Hosted MCP memory tool | Provider-dependent callback or approval receipt | Provider-dependent callback or approval receipt | `ActionOnly` with mandatory approval; `FullLifecycle` only with a complete provider callback or owned gated server; otherwise `Unsupported` | Enforcing construction fails for sensitive read/write without the required seam |
| Generic `AIContextProvider` decorator | Complete returned `AIContext` before merge | Source request/response messages before delegation | `Boundary` | Never claim visibility into provider-internal extraction, reconciliation, async writes, or direct external writes |
| Provider-native candidate/item hook | Each recalled item before formatting | Each extracted candidate immediately before commit | `FullLifecycle` for declared operations when scope and derived-action coverage are also present | Hook identity, version, and capabilities are fingerprinted and preflighted |

AgentMemory isolation and admission controls count as provider-native evidence only for the exact
configured paths that can be verified. They do not automatically upgrade a generic wrapper or an
unrelated client to `FullLifecycle`. Dynamic tools returned by a context provider are classified
individually before merge; an unknown side-effecting memory tool is `Unsupported`.

The existing construction failure rules in [Coverage model](#coverage-model) are normative. In
addition, a coverage report must retain separate read, write, promotion, and derived-action rows so
that strong read coverage cannot hide weak write coverage. No aggregate percentage may convert
`Boundary`, `ActionOnly`, `ObserveOnly`, or `Unsupported` into a full-enforcement claim.

### Task 0.3 — API, action, sequencing, and promotion freeze

The public type names and enum value sets in [Proposed contracts](#proposed-contracts) are normative
for Phase 1. Constructor arity may still be simplified during 1.1, but semantic fields, bounds, and
fail-closed behavior may not be removed without reopening 0.R. Public contexts, verdicts, receipts,
policies, and operation contracts are immutable; caller collections are defensively copied and
bounded during construction.

Operation-to-stage mapping is fixed:

- `Search` and `Recall` use `BeforeRead` and `AfterRead`;
- `Write`, `Update`, `Delete`, and `Reconcile` use `BeforeWrite`;
- `Promote` uses `BeforeWrite` and `BeforePromotion`;
- a sensitive tool influenced by recalled memory uses `BeforeAction`;
- `Audit` and finalized receipts use `AfterDecision`, which is observational and cannot mutate the
  completed operation.

Legal actions are stage-specific:

| Stage | Legal actions | Semantic boundary |
|---|---|---|
| `BeforeRead` | `Allow`, `Reject` | A rejected query never reaches the provider |
| `AfterRead` | `Allow`, `Sanitize`, `Exclude`, `Reject` | `Exclude` removes one item; `Reject` fails the whole read safely |
| `BeforeWrite` | `Allow`, `Sanitize`, `Quarantine`, `RequireApproval`, `Reject` | No active-store side effect occurs before the disposition is applied |
| `BeforePromotion` | `Allow`, `Sanitize`, `Quarantine`, `RequireApproval`, `Reject` | Promotion is a new write and reruns required write gates |
| `BeforeAction` | `Allow`, `RequireApproval`, `Reject` | Tool adapters map the decision to existing Gatekeeper approval/block behavior |
| `AfterDecision` | `Allow` | Evidence/audit only; never retroactively changes a completed operation |

A gate is a side-effect-free decision component. The orchestrator applies quarantine, approval, or
persistence exactly once after aggregation. Gate order and configuration are snapshotted and
fingerprinted. `Reject` is terminal; otherwise the most restrictive legal result wins in this order:
`Quarantine`, `RequireApproval`, `Exclude`, `Sanitize`, `Allow`. `Sanitize` transformations run in
frozen order and the result is revalidated once; a second sanitization request or an invalid action
for the current stage fails closed. Caller cancellation always propagates as cancellation.

Sequencing is frozen as documented in [Mandatory sequencing](#mandatory-sequencing). In particular,
reconciliation occurs after authenticated scope, provenance, trust, inclusion/exclusion, and
sensitive-data admission, and before promotion or persistence. A rejected or quarantined candidate
never contributes a conflict vote, embedding, or active retrieval index.

Development and promotion gates are distinct:

- construction coverage/capability checks run before builder mutation and before runtime
  enforcement can start;
- each implementation task receives a focused changed-code review before its tracker row becomes
  ✅;
- each `X.R` review must pass before dependent-phase coding starts or the phase is promoted;
- semantic gates may be implemented for offline/shadow/quarantine evaluation before calibration,
  but calibration is mandatory before inline enforcement;
- Gatekeeper Phase 4's deferred remote A2A validation is unrelated to the provider-neutral memory
  substrate and does not block Phase 1.

### Project ownership and dependency freeze

| Project | Frozen ownership | Dependency rule |
|---|---|---|
| `AgentEval.MAF` | Runtime memory contracts, pipeline, deterministic gates, MAF tool/MCP/context-provider adapters, coverage, receipts, and configuration | May continue to depend on `AgentEval.Abstractions` and `AgentEval.Core`; must not reference `AgentEval.Memory` or `AgentEval.RedTeam` |
| `AgentEval.RedTeam` | Provider-neutral memory attack definitions, corpora, attack observations, and security evaluator models that do not require MAF | Must not depend on `AgentEval.MAF` |
| `AgentEval.RedTeam.Gatekeeper` | Optional bridge that executes RedTeam memory attacks against the MAF/Gatekeeper runtime and CompositeEvals | May depend on both `AgentEval.RedTeam` and `AgentEval.MAF`, matching the existing direction |
| `AgentEval.Memory` | LongMemEval and memory quality/trustworthiness evaluation | Remains independent of the runtime gate; no `AgentEval.MAF -> AgentEval.Memory` reference |
| `AgentEval` umbrella | Packaging only | May aggregate packages but must not become the owner of runtime contracts |

Shared contracts move downward only when a demonstrated second runtime consumer requires it. No new
project or interface is created merely to avoid an otherwise acyclic dependency.

### Task 0.R — focused architecture and threat-model review

| Review dimension | Finding | Resolution |
|---|---|---|
| Security claims | Guidance, vendor claims, and paper results could be read as equivalent evidence | Added the classified source matrix and explicit claim boundaries |
| Coverage honesty | Local MCP client interception could be mistaken for repository-level lifecycle protection | Capped client-only MCP at `Boundary`; full coverage now requires a gated server or equivalent native hook |
| Public API | `IMemoryGate.Requirements` referenced an undefined type | Froze the flags enum and construction-time capability mapping below |
| Verdict semantics | Stage/action legality and competing gate outcomes were underspecified | Added the legal-action matrix, precedence, single-application rule, and bounded sanitization re-entry |
| Provider boundary | A generic context-provider wrapper could be over-promoted | Retained an explicit `Boundary` ceiling and required provider-native candidate/item hooks for full coverage |
| Dependencies | Runtime, benchmark, and attack ownership were implicit | Froze project ownership without adding `AgentEval.MAF -> AgentEval.Memory` or a RedTeam cycle |
| Sequencing | Reconciliation and promotion timing could be implemented in the wrong order | Froze admission → conflict/reconciliation → promotion → persistence |
| Gate timing | Construction, coding review, promotion, and inline calibration gates could be conflated | Defined each gate and when it blocks progress |
| Privacy | Evidence could accumulate raw memory or provider failures | Kept content-free receipts as default and bounded private review data to quarantine storage |
| MAF/version risk | Repo-wide diagnostics include unrelated historical and nested-worktree findings | Scoped changed-file MAF review remains mandatory; heuristic findings require verification before edits |

Validation baseline for Phase 0:

- branch point: `bb2cf6a2f5ae75e16dea196b246d5003eebc8df4` on `main`;
- repository build baseline: zero errors and 175 warnings; Phase 0 changes are documentation-only and
  must add no warning;
- repository-wide MAF Doctor baseline on 2026-07-30: grade F with 34 errors and 18 warnings, dominated
  by nested-worktree/sample findings and broad missing-`MaxOutputTokens` findings; no
  silent-starvation finding;
- prior scoped Gatekeeper MAF scans on the merged runtime changes reported zero findings;
- open Phase 0 security/API/dependency blockers: none. Phase 1 may start only from the frozen
  decisions above.

## Phase 1 implementation and review record

> **Completion date:** 2026-07-31
>
> Phase 1 supplies the provider-neutral substrate only. It does not claim that a tool, MCP server,
> context provider, or provider-native memory path is protected until the applicable later adapter
> and coverage tasks are complete.

The implementation adds immutable and bounded operation, scope, provenance, conflict, policy, and
context models; a deterministic, side-effect-free `IMemoryGate` pipeline; privacy-minimized
receipts; configuration fingerprints; and typed host capability contracts for trusted scope,
quarantine, approval, run scope, and provider-native candidate hooks.

The focused review found and fixed four material issues before completion:

- undefined enum values could enter public models;
- concrete host capability types were absent from the configuration fingerprint;
- observe mode could accidentally enforce a disposition or expose hypothetical sanitized content
  as caller-visible output;
- enforcing ambiguous-write policies could be constructed without the disposition capability
  needed to apply their configured action.

Thirty focused tests per target framework cover immutability, serialization privacy, invalid input,
stage/action legality, sanitizer re-entry, precedence, exception safety, cancellation,
configuration snapshots, capability preflight, observe/enforce behavior, and concurrent isolation.
They pass on `net8.0`, `net9.0`, and `net10.0`. A scoped MAF anti-pattern scan of the new production
directory reported no findings. Phase 2 remains responsible for the concrete deterministic gates.
## Phase 2 implementation and review record

> **Completion date:** 2026-07-31
>
> Phase 2 implements provider-neutral deterministic decisions. It still does not claim that any MAF
> tool, MCP, or context-provider surface is protected until the applicable Phase 3–5 adapter is
> installed and Phase 7 coverage preflight succeeds.

The five gates now enforce trusted host scope, bounded write admission, trust-aware reconciliation,
recall admission, and host-computed resource caps. Credential/PII detection uses bounded
non-backtracking regular expressions; other instruction and hidden-character checks are linear over
the already bounded context. Recall delimiters escape metadata and refuse transformations that
would exceed the absolute content bound.

The focused false-open/false-block, privacy, and complexity review found and fixed these issues:

- a contradictory equal-trust record was incorrectly counted as independent support for the new
  candidate; corroboration now requires distinct existing roots with the candidate's digest;
- scope options could permit read scope broader than authorized write scope;
- the configured model-scope ignore path was fingerprinted but not applied;
- delimiter metadata could inject markup or make sanitized content exceed the absolute bound;
- default context serialization could expose raw authenticated/model scope, provenance, conflict,
  and record identifiers.

Forty-seven Phase 2 tests plus the thirty Phase 1 regressions pass on `net8.0`, `net9.0`, and
`net10.0` (77 per target framework). Maximum-size content completes within the bounded test budget,
configuration changes alter the pipeline fingerprint, and the scoped MAF anti-pattern scan reports
zero findings.


## Phase 3 implementation and review record

> **Completion date:** 2026-07-31
>
> Phase 3 protects declared local MAF function invocation paths. It does not upgrade generic context
> providers, local/hosted MCP, provider-internal extraction, or direct repository access; those remain
> Phase 4–5 work and retain their frozen maximum coverage.

The exact-name operation registry is the only authority that assigns memory semantics. Name and
Description hints can identify an unclassified memory-like tool, but cannot infer read/write,
scope, promotion, or sensitivity behavior. Pre-call and post-result adapters translate registered
operations into the existing Gatekeeper tool-gate interfaces, while disposition side effects execute
once only after side-effect-free aggregation. The influence adapter reuses the run-scoped taint engine
for conservative recalled-value flow into explicit sensitive sinks.

The coverage analyzer reports each suspected or declared memory tool independently. It refuses
unclassified and provider-hosted tools, validates matching registry and pipeline fingerprints, keeps
local MCP at `Boundary`, distinguishes observe-only adapters from enforcement, and requires influence
coverage before a sensitive local read can claim `FullLifecycle`.

The focused security/API review found and fixed these issues:

- observe-profile adapters could incorrectly satisfy an enforcing coverage threshold;
- call and result adapters from different policy pipelines could be treated as a complete read path;
- an influence gate from another registry could incorrectly upgrade sensitive-read coverage;
- a trusted sanitizer returning null could fail open or propagate an invalid mutation;
- caller cancellation was not checked before influence inspection;
- the executor's transient effective content could be serialized by default;
- content-independent delete operations were incorrectly rejected for lacking candidate content.

Thirty-seven Phase 3 tests and all 143 memory regressions pass on `net8.0`, `net9.0`, and
`net10.0`. The scoped MAF anti-pattern scan reports zero findings.

## Phase 4 implementation and review record

> **Completion date:** 2026-07-31
>
> Phase 4 protects the generic `AIContextProvider` boundary and defines a provider-native hook.
> A generic provider is still capped at `Boundary`: AgentEval cannot claim that records extracted
> and persisted inside an opaque provider were inspected.

`GatedAIContextProvider` wraps exactly one provider rather than running as a sibling. It preserves
the wrapped provider's lifecycle context, state keys, and service lookup while keeping trusted
scope and session state in per-invocation contexts. Bounded materialization happens before policy
inspection. Recalled instructions and messages are gated before they reach the model, dynamic
memory tools require matching registered call/result coverage, and source messages are gated before
the wrapped provider receives them for persistence.

Enforce mode normalizes allowed or sanitized text into representation-free messages, preserves
provider attribution, and rejects opaque structured content that cannot be safely reconstructed.
Provider failures map to content-free reason codes; an explicitly configured continuation mode
requires an event sink so degraded protection cannot be silent. Caller cancellation propagates.

The provider-native contract binds a versioned provider identity, declared candidate-write and
recalled-item capabilities, and a configuration fingerprint to pipeline-backed hooks. Candidate
writes execute quarantine/approval dispositions before provider commit, while recalled items are
admitted before formatting. A generic wrapper never uses this contract to overstate its coverage.

The focused security/API review found and fixed these issues:

- non-text provider messages could bypass content inspection;
- allowed messages could retain provider-specific raw representations after inspection;
- provider-native hook calls did not initially prove the caller matched the declared provider;
- malformed reason codes and incomplete fingerprints weakened stable configuration evidence;
- provider enumeration and dynamic-tool output needed explicit bounds and safe failure mapping;
- a continuation policy could otherwise suppress provider failures without observable evidence.

Twenty-nine Phase 4 tests and all 172 memory regressions pass on `net8.0`, `net9.0`, and
`net10.0`. Tests cover lifecycle ordering, state keys, attribution, concurrency, cancellation,
dynamic tool coverage, enforce/observe behavior, safe provider failures, structured-content
rejection, representation normalization, quarantine/approval, provider-native identity and
capabilities, and serialization privacy. The scoped MAF anti-pattern scan reports zero findings.

## Phase 5 implementation and review record

> **Completion date:** 2026-07-31
>
> Phase 5 protects local MCP client boundaries and owned MCP servers, and reports hosted MCP
> limitations per operation. A local client alone remains capped at `Boundary`; provider-hosted
> execution is never silently grouped with owned server enforcement.

Local MCP bindings pin the exact server identity, version, transport, operation semantics, and
canonical JSON schema fingerprint. The actual `AIFunction` schema can be fingerprinted directly,
and schema drift invalidates coverage. Calls and results continue through the one existing
Gatekeeper function middleware, so this phase adds no parallel invocation loop.

`MemoryMcpServerGate<TRequest, TResult>` wraps an owned server handler. It evaluates request
stages and executes quarantine/approval before invoking the backend, then gates recalled content
before the transport can serialize it. Denied requests never reach the repository callback.
Backend and adapter failures are replaced with AgentEval-owned reason codes and bounded correlation
IDs without retaining an inner provider exception.

Hosted coverage remains explicit: mandatory provider approval is `ActionOnly`, incomplete or
unapproved opaque execution is `Unsupported`, and `FullLifecycle` requires complete provider
callbacks or matching owned-server evidence plus required derived-action coverage. Owned-server
evidence itself requires an enforcing, stage-complete pipeline and a fingerprinted trusted adapter.

The focused local/hosted/server threat-boundary review found and fixed these issues:

- operation and coverage inventories were initially unbounded;
- ambiguous duplicate-property schemas needed rejection before canonical hashing;
- an unregistered MCP binding was assigned an invented placeholder operation kind;
- owned-server matching omitted version and complete operation security semantics;
- server and hosted fingerprints omitted content/scope fields and adapter configuration;
- an empty server pipeline could emit evidence that upgraded client coverage;
- coverage reports lacked a stable content-free configuration fingerprint.

Forty Phase 5 tests and all 212 memory regressions pass on `net8.0`, `net9.0`, and
`net10.0`. Tests prove schema-drift refusal, backend non-invocation after denial, request/result
sanitization, quarantine single execution, cancellation, safe error mapping, serialization privacy,
local/client/server coverage composition, adapter fingerprinting, and hosted-MCP claim ceilings.
The scoped MAF anti-pattern scan reports zero findings.

## Goals and non-goals

### Goals

- Give MAF applications a consistent security policy across memory tools, MCP, and
  `AIContextProvider`.
- Stop unscoped or mismatched reads/writes before storage access.
- prevent an untrusted or low-trust source from silently becoming trusted durable state;
- preserve provenance through extraction, rewriting, summarization, reconciliation, and procedure
  promotion;
- prevent poisoned recalled memory from directly causing sensitive tool actions;
- measure persistence, activation, scope leakage, and benign-memory utility together;
- produce content-free, attributable evidence by default;
- expose honest coverage levels and refuse an enforcement claim when a surface is not inspectable.

### Non-goals

- Determining whether every natural-language fact is objectively true.
- Replacing authentication, authorization, database row-level security, encryption, backups, or
  provider-native audit controls.
- Treating hashes as proof that an authorized write is benign.
- Treating provenance as proof of truth.
- Claiming a prompt-injection classifier detects policy-satisfying, summary-surviving, or
  procedure-level poison.
- Inspecting provider-hosted MCP execution that MAF does not surface locally.
- Moving AgentMemory storage or reconciliation logic into AgentEval.
- Running an LLM judge inline by default.

## Evidence and current architecture

### MAF 1.13.0

MAF's `AIContextProvider` has a two-phase lifecycle:

- `InvokingAsync`/`ProvideAIContextAsync` retrieves messages, instructions, and tools and merges
  them into the model request;
- `InvokedAsync`/`StoreAIContextAsync` processes request and response messages after the run.

The MAF source explicitly states that provider data is accepted as-is and is not validated or
filtered by the framework. A provider may inject system-role messages, instructions, and dynamic
tools. `AIContextProvider` stamps provided messages with provider source attribution, but the stamp
identifies the provider, not the original memory item or its trust lineage.

The built-in MAF Mem0 provider:

- validates that at least one storage and search scope field exists;
- sends request/response messages to Mem0 after successful invocation;
- accepts retrieved Mem0 content without validation and injects it as a user message;
- catches provider read/write errors and logs them;
- can log full query/result content at trace level when sensitive telemetry is enabled.

Those behaviors make a wrapper feasible but also prove its limit: a generic wrapper can inspect the
messages sent to a provider and the context returned by it, but cannot inspect each fact or
preference that a remote provider extracts internally.

### MAF tool and MCP seams

AgentEval already gates local MAF functions at the function-invocation middleware:

- `IToolGate` inspects proposed calls before execution and can allow, mutate, or block;
- `IToolResultGate` inspects returned values before they flow back to the model and can allow,
  redact, or block;
- `UseGatekeeper` preflights gate cost, minimum enforcement, run-scope requirements, and composes
  one tool middleware registration to avoid gate starvation.

Local `McpClientTool` instances are `AIFunction` implementations and therefore use the local
function-invocation path. Hosted MCP tools may execute inside a backing model/provider service and
must not be assumed to cross AgentEval's local middleware. Owned MCP servers provide the strongest
server-side enforcement seam: gate immediately before calling the memory service/repository and
again before returning recalled content.

### Existing AgentMemory controls

The inspected AgentMemory .NET adapter already provides controls AgentEval should interoperate with:

- `IMemoryIsolationPolicy`, including `StrictMultiTenant` fail-closed behavior;
- `IMemoryContextAdmissionPolicy` for deterministic recalled-content admission;
- per-item trust levels;
- delimited/escaped recalled content;
- owner scope propagated across recall, tools, and persistence;
- memory tools surfaced as MAF `AIFunction` instances;
- a default admission detector that is intentionally heuristic and permissive unless strict mode is
  selected.

AgentEval must not duplicate these provider-native controls. It should:

1. recognize them as full or partial evidence of coverage;
2. add cross-surface policy, construction-time validation, persistent attack evaluation, and
   memory-to-action enforcement;
3. expose adapter contracts so other memory providers can prove equivalent controls.

### MAF Doctor baseline

MAF Doctor reported grade F for the repository on 2026-07-30. The headline includes findings in
nested `.claude/worktrees` and thousands of broad missing-`MaxOutputTokens` call-site findings. It
reported no silent-starvation risks and no prompt-lint findings. This plan does not silently absorb
that unrelated remediation scope. Implementation review must run MAF Doctor on changed production
files and explain or exclude heuristic findings before editing.

## Threat model

### Assets

- user preferences, facts, entities, summaries, procedures, reasoning traces, and chat history;
- tenant/user/agent/application/session identity and authorization boundaries;
- credentials, PII, health, financial, legal, and organizational data stored or recalled;
- tool-selection policies and learned procedures;
- audit lineage needed to explain later decisions.

### Adversaries and failure sources

- an ordinary user with query-only access;
- indirect content from browsers, email, documents, search, tools, MCP, or cloud services;
- a compromised or malicious MCP server;
- a malicious or over-permissive memory provider;
- an authorized but low-trust contributor;
- a benign user whose data is incorrectly shared with another user;
- model hallucinations or speculative assistant output written back as fact;
- stale, duplicated, corrupted, or incorrectly reconciled records.

### Four mandatory write channels

1. **Direct injection**: content explicitly instructs the agent to remember or override something.
2. **Policy satisfaction**: content appears to satisfy the application's legitimate memory policy,
   such as a preference, trusted-vendor assertion, incident report, or operational procedure.
3. **Summary survival**: malicious meaning survives extraction, rewriting, or compaction and is
   stored beside legitimate summary content.
4. **Procedure promotion**: an adversarial sequence is promoted into a reusable procedure, skill,
   plan, or reasoning precedent.

### Required attack classes

- query-only memory injection;
- repeated-write/duplicate-consensus poisoning;
- trigger/payload and sleeper activation after benign sessions;
- tool-selection bias;
- recommendation/source-trust manipulation;
- cross-user, cross-tenant, cross-agent, and cross-application contamination;
- unauthorized overwrite or trust escalation;
- secret/PII persistence;
- memory-assisted data exfiltration through a tool;
- embedding/ranking manipulation and retrieval crowd-out;
- resource flooding and denial of wallet;
- out-of-band record tampering;
- post-incident misattribution to the model instead of memory.

## Required invariants

1. A tenant-facing memory operation without authenticated scope fails closed before storage access.
2. Model-generated tool arguments never establish or override tenant/user/agent identity.
3. Search scope cannot be broader than write scope unless an explicit administrative policy permits
   it.
4. A lower-trust source cannot overwrite, supersede, or confer trust on a higher-trust memory.
5. Repeated copies from one source do not count as independent corroboration.
6. Summaries and procedures retain the complete parent lineage and the lowest relevant trust
   boundary; transformation never launders trust.
7. Unknown provenance is untrusted, not implicitly application-trusted.
8. System instructions and executable procedures require a stricter promotion policy than ordinary
   user preferences.
9. A hash proves integrity since hashing, not truth or authorization.
10. Recalled memory is data, not authority; it must be delimited and must not silently become a
    system instruction.
11. Recalled memory is checked after retrieval and before it reaches the model.
12. Memory-derived sensitive tool calls are checked before execution.
13. Critical gate uncertainty fails closed or quarantines according to explicit policy; it never
    silently allows.
14. A requested `Quarantine` or `RequireApproval` action is never downgraded when the necessary
    service is missing; configuration fails before the agent is built.
15. Caller cancellation propagates and is never converted into a safe/unsafe verdict.
16. Raw content, credentials, headers, embeddings, and provider exception messages are absent from
    gate evidence by default.
17. Normal and counterfactual/replay runs have isolated state and counters.
18. Every enforced decision records policy version, configuration fingerprint, operation ID,
    surface, stage, reason code, and scope-safe correlation.

## Coverage model

Every configured memory surface receives one of these explicit coverage levels:

| Level | Meaning | May claim full protection? |
|---|---|:---:|
| `FullLifecycle` | Candidate writes, storage scope, recalled items, and derived actions are inspectable | Yes, for declared operations |
| `Boundary` | Inputs to a provider and returned context are inspectable, but internally extracted records are not | No |
| `ActionOnly` | Only downstream tool actions/approvals are inspectable | No |
| `ObserveOnly` | Telemetry exists but no enforcement is installed | No |
| `Unsupported` | The surface bypasses all available gates | No |

Construction under enforcing mode must fail when:

- a declared memory write surface is `Unsupported`;
- a gate can return `Quarantine` but no quarantine store exists;
- approval is required but the surface has no approval mechanism or explicit quarantine fallback;
- a hosted MCP memory server can write or recall sensitive memory without an inspectable server-side
  hook or mandatory provider approval;
- a dynamic `AIContextProvider` exposes unclassified high-risk tools;
- full-lifecycle protection is requested for a generic provider that exposes only boundary access.

Coverage is per operation, not per provider. A provider may have full read coverage and only
boundary write coverage.

## Proposed contracts

### `IMemoryGate`

One focused interface mirrors `IToolGate` without creating separate interfaces for every policy:

```csharp
public interface IMemoryGate
{
    string PolicyName { get; }
    GateCost Cost { get; }
    MemoryGateStage Stages { get; }
    MemoryGateRequirements Requirements { get; }

    ValueTask<MemoryGateVerdict> InspectAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default);
}
```


`MemoryGateRequirements` is a flags enum because one gate may require multiple host capabilities:

```csharp
[Flags]
public enum MemoryGateRequirements
{
    None = 0,
    RunScope = 1,
    AuthenticatedMemoryScope = 2,
    QuarantineStore = 4,
    ApprovalHandler = 8,
    ProviderCandidateHook = 16
}
```

Requirements are construction-time obligations:

| Requirement | Required capability | Failure behavior |
|---|---|---|
| `RunScope` | Stable `AgentRunScope` and logical session for the invocation | Enforcing configuration is rejected before builder mutation |
| `AuthenticatedMemoryScope` | Application-supplied `IMemoryScopeResolver` deriving required identity dimensions from trusted host/session state | No fallback to model arguments or ambient unverified state |
| `QuarantineStore` | Separate store excluded from ordinary retrieval plus a bounded receipt | A policy that can quarantine cannot be registered without it |
| `ApprovalHandler` | Surface-specific approval mechanism or an explicitly configured quarantine fallback | Missing capability never becomes `Allow` |
| `ProviderCandidateHook` | Versioned provider callback exposing candidate records/items before commit/formatting | Requested `FullLifecycle` coverage is rejected without the hook |

Preflight validates the union of gate requirements, configured verdict actions, requested coverage,
and surface capabilities. The resulting snapshot is immutable and contributes to the policy
fingerprint.

Inline gates accept only `PureCode` or `Bounded` cost. Network/LLM gates run against quarantine,
shadow traffic, replay, or offline corpora unless separately calibrated and explicitly promoted.

### Operation and stage

```csharp
public enum MemoryOperationKind
{
    Search,
    Recall,
    Write,
    Update,
    Delete,
    Promote,
    Reconcile,
    Audit
}

[Flags]
public enum MemoryGateStage
{
    None = 0,
    BeforeRead = 1,
    AfterRead = 2,
    BeforeWrite = 4,
    BeforePromotion = 8,
    BeforeAction = 16,
    AfterDecision = 32
}

public enum MemorySurface
{
    Tool,
    LocalMcp,
    HostedMcp,
    McpServer,
    AIContextProvider,
    ProviderNative
}
```

### Verdict actions

```csharp
public enum MemoryGateAction
{
    Allow,
    Sanitize,
    Exclude,
    Quarantine,
    RequireApproval,
    Reject
}
```

Action mapping is stage-specific:

- `Sanitize` changes bounded candidate content/metadata before persistence;
- `Exclude` removes a recalled item before context injection;
- `Quarantine` stores the candidate outside normal retrieval;
- `RequireApproval` pauses a tool/MCP operation when supported, otherwise follows an explicitly
  configured quarantine fallback;
- `Reject` prevents the read/write/promotion;
- `Allow` does not confer additional trust.

### `MemoryGateContext`

The context must be immutable and bounded. It carries:

- operation ID, kind, stage, and surface;
- declared operation contract and provider/server identity;
- authenticated scope from a host resolver;
- any model-supplied scope separately, for mismatch detection only;
- actor/source kind, source identifier, source trust, transformation chain, and parent memory IDs;
- memory category (`Fact`, `Preference`, `Summary`, `Procedure`, `ReasoningTrace`, `Message`,
  `Unknown`);
- ephemeral bounded content plus a digest; evidence stores the digest, not content, by default;
- citations/source artifacts and freshness metadata;
- existing conflicting memories as bounded summaries/digests;
- run ID and logical session identity;
- proposed destination (`Active`, `Quarantine`, or provider-native equivalent).

IDs use ordinal equality. Trimming may remove outer whitespace, but case folding, Unicode
normalization, or hashing must not silently change provider identity semantics.

### Scope resolution

`IMemoryScopeResolver` derives scope from trusted application/session state:

```csharp
public interface IMemoryScopeResolver
{
    MemorySecurityScope Resolve(AgentSession session, string? agentName);
}
```

The resolver is application supplied because AgentEval cannot infer authentication from arbitrary
`AgentSession.StateBag` keys. Model arguments are never input to this resolver.

### Operation contracts

Tool and MCP security semantics must be declared:

```csharp
public sealed record MemoryOperationContract(
    string OperationName,
    MemoryOperationKind Kind,
    MemorySurface Surface,
    IReadOnlyList<string> ContentArguments,
    IReadOnlyList<string> ScopeArguments,
    MemoryCategory Category,
    bool IsSideEffecting,
    bool MayReturnSensitiveContent);
```

Name heuristics may produce warnings during migration, but enforcement cannot depend on a tool being
named `remember_*` or `search_*`.

### Quarantine and receipts

`IMemoryQuarantineStore` stores candidate plus private review metadata under a separate retrieval
boundary. `MemoryGateReceipt` is safe to place in ordinary reports and contains no raw content by
default.

Quarantined content is never included in normal semantic search. Promotion from quarantine is a new
write and reruns scope, provenance, conflict, and promotion gates.

## Gate catalog

### 1. `MemoryCoverageGate`

**When:** construction and provider-context assembly.
**Why:** a correctly implemented gate on one seam gives false confidence if another memory path
bypasses it.

Responsibilities:

- inventory declared memory tools, MCP operations, context providers, and provider-native hooks;
- compute per-operation coverage;
- inspect dynamic tools emitted by a context provider before they are merged;
- reject enforcing configurations with unsupported critical operations;
- fingerprint the frozen contracts/gates used by runtime evidence.

This is the first gate because missing coverage invalidates every later claim.

### 2. `MemoryScopeIntegrityGate`

**When:** before every read, write, update, delete, promotion, and reconciliation.
**Why:** cross-user contamination exists even without an attacker, and content filtering cannot
repair an over-broad database query.

Responsibilities:

- require host-resolved tenant/user/agent/application scope according to policy;
- compare but never trust model-supplied scope arguments;
- block or remove mismatched model-supplied identity;
- require explicit administrative capability for cross-scope operations;
- ensure search scope is not broader than authorized write scope;
- record scope-safe correlation without logging raw identity by default.

For AgentMemory, recognize `StrictMultiTenant` as provider-native scope enforcement but still test
its wiring at every MAF surface.

### 3. `MemoryWriteAdmissionGate`

**When:** after scope resolution, before persistence.
**Why:** the primary attack succeeds when untrusted input becomes durable trusted state.

Deterministic policies:

- required provenance and lineage;
- source trust and actor capability;
- category inclusion/exclusion;
- secret/credential/PII policy;
- size/control-character/hidden-Unicode bounds;
- instruction/role/tool-syntax signals;
- protected namespace/key policy;
- stricter handling for assistant-generated facts and tool/MCP output;
- explicit promotion rules for summaries, procedures, skills, and system-like instructions.

Instruction-pattern detection is only one signal. Policy-satisfying content that contains no
malformed syntax can still be quarantined because its provenance/trust/category is insufficient.

### 4. `MemoryConflictGate`

**When:** after admission and before reconciliation/commit.
**Why:** naive append permits repetition attacks; naive last-write-wins lets a low-trust attacker
replace a trusted fact.

Rules:

- lower trust cannot overwrite higher trust;
- equal-trust contradiction requires independent corroboration, approval, or quarantine;
- repeated writes from the same root lineage never create multiple votes;
- derived summaries from the same ancestors are not independent sources;
- protected facts and policies require explicit authority;
- old and new values remain in immutable history even when one becomes active;
- deterministic supersession rules are preferred over an LLM truth judge.

The gate consumes provider conflict candidates but does not own database reconciliation.

### 5. `MemoryRecallAdmissionGate`

**When:** after retrieval and before context merge/tool result return.
**Why:** existing poisoned or tampered memory must not be trusted merely because it is already in
the store.

Checks:

- scope and owner match;
- integrity/signature when configured;
- citation/source artifact still exists and still supports the claim when a deterministic verifier
  is available;
- TTL, revocation, quarantine, and supersession state;
- trust threshold for requested use;
- instruction-like or executable content;
- content/category allowed for the current agent and purpose;
- maximum item count and total context size.

Allowed recalled items are delimited as untrusted data and retain memory IDs/trust/source metadata
through context assembly. Excluded items produce safe reason counts, not raw content.

### 6. `MemoryInfluenceGate`

**When:** before a sensitive tool action.
**Why:** scope isolation protects who can retrieve a memory, but not whether that memory can send
private data to a malicious or unnecessary tool.

Responsibilities:

- propagate memory IDs/trust labels from recall into run evidence;
- identify sensitive sinks and high-impact tools;
- require user-origin or trusted-source support for sensitive arguments;
- block confidential memory values flowing to external sinks;
- require approval when a tool decision is materially supported only by low-trust recalled memory;
- reuse and extend `TaintTrackingGate`, `ReferentialIntegrityGate`, and the Gatekeeper reference
  ledger rather than create a parallel tool-enforcement loop.

Version 1 may be conservative token/value taint. Structured label propagation is the intended
stronger model when providers preserve item-level metadata.

### 7. `MemoryResourceBudgetGate`

**When:** before writes, promotion, and context injection.
**Why:** memory flooding can crowd out legitimate recall, increase cost, and turn duplicate content
into apparent consensus.

Bounds:

- per-record bytes/tokens;
- writes per run/session/user/source;
- unique candidates per source/time window;
- promotions and reconciliation attempts;
- recalled item count and total context size;
- lineage depth and parent count;
- quarantine volume.

Cap exhaustion fails closed for writes and excludes excess recall according to deterministic rank;
it never silently truncates provenance.

### 8. `MemoryAuditEvaluator`

**When:** offline, scheduled audit, and incident response.
**Why:** dormant poisoning often looks like model failure after the original write has left active
logs.

Capabilities:

- trace a response/tool action to recalled memory IDs and original writes;
- run counterfactual replay with suspected memories removed;
- detect anomalous conflict/repetition/lineage structures;
- compare active memory against quarantine, history, and trusted baselines;
- emit rollback candidates without mutating storage;
- measure attribution success separately from attack prevention.

This is an evaluator, not an inline blocker.

## Mandatory sequencing

### Construction

```text
Freeze operation contracts and gate list
→ validate costs and minimum enforcement
→ validate scope resolver
→ validate quarantine/approval capabilities
→ compute static coverage
→ refuse unsupported enforcing configuration
→ fingerprint policy
→ compose exactly one tool middleware pipeline
→ build agent
→ run post-build/dynamic coverage where possible
```

All preflight occurs before mutating `AIAgentBuilder`. This preserves the existing `UseGatekeeper`
all-or-nothing construction guarantee.

### Write

```text
Authenticated scope
→ operation contract
→ canonicalization and hard bounds
→ scope integrity
→ provenance and trust
→ inclusion/exclusion and sensitive-data policy
→ conflict lookup
→ trust-aware reconciliation decision
→ promotion/procedure policy
→ allow, sanitize, quarantine, approve, or reject
→ provider persistence
→ immutable receipt/history
```

Reconciliation never runs before scope/provenance/admission. A rejected candidate must not affect
conflict counts, embeddings, or retrieval indexes.

### Recall

```text
Authenticated scope
→ before-read scope gate
→ provider query
→ per-item integrity/citation/expiry/trust checks
→ recall admission
→ deterministic item/context budget
→ delimiter and provenance labels
→ model context or tool result
→ influence lineage ledger
```

### Action

```text
Proposed sensitive tool call
→ ordinary tool contracts
→ memory influence and taint checks
→ referential integrity
→ approval when required
→ execute
→ result gates
→ evidence receipt
```

## MAF integration by memory surface

### Memory as local MAF tools

Use existing function middleware:

- write tools are adapted to `MemoryGateStage.BeforeWrite`;
- read/search tools receive `BeforeRead` scope checks;
- read results are adapted through `IToolResultGate` to `AfterRead`;
- sensitive downstream calls run `MemoryInfluenceGate`;
- all memory tool semantics come from `MemoryOperationContract`.

`AgentRunScope.Current.Session` supplies the session to the trusted
`IMemoryScopeResolver`. Gates declare `GateRequirements.RunScope`; enforcing construction refuses a
configuration without run scope.

`Quarantine` cannot be simulated by merely returning success while skipping persistence. The
quarantine receipt must state where the candidate went. If the tool/provider cannot quarantine, the
configured fallback is explicit approval or rejection.

### Local MCP memory tools

`McpClientTool` is an `AIFunction`, so local client execution uses the same function middleware as
ordinary tools. Add an MCP-aware contract adapter that records:

- server identity and transport;
- tool name and schema fingerprint;
- declared memory operation;
- allowed scope/content fields;
- whether the result may contain sensitive memory.

Do not trust MCP descriptions to classify a tool at runtime. Pin an explicit contract or reviewed
schema fingerprint. A changed MCP schema invalidates coverage until reviewed.

### Owned MCP memory server

Gate again on the server. Client-side gates can be bypassed by another client and cannot enforce
repository scope for all callers.

The server adapter:

- authenticates caller identity before building `MemorySecurityScope`;
- applies scope and write/read gates before `IMemoryService` or repository access;
- maps quarantine/approval to server-supported behavior;
- gates recalled results before serializing MCP content;
- returns bounded AgentEval-owned error codes and correlation IDs;
- never returns policy details that help tune an attack.

For AgentMemory's MCP server, integrate above `IMemoryService`/`IMemoryIsolationPolicy`, preserving
its existing strict isolation rather than replacing it.

### Hosted MCP memory tools

Hosted MCP may execute inside the provider and bypass local function middleware. Version 1 must be
honest:

- `FullLifecycle` requires a provider callback that exposes call, result, scope, and approval before
  execution, or an owned server-side gate;
- provider "always require approval" can produce `ActionOnly` coverage but is not write-content
  inspection;
- a hosted memory write configured as "never require approval" without server-side enforcement is
  `Unsupported` under enforcing mode;
- no report may aggregate hosted MCP with fully gated local MCP without separate coverage labels.

Provider-specific adapters are isolated from the provider-neutral core.

### Custom `AIContextProvider`

Implement `GatedAIContextProvider : AIContextProvider` as a decorator and register it **instead of**
the inner provider, never as a later sibling:

- override `InvokingCoreAsync`, call `inner.InvokingAsync`, inspect the complete returned
  `AIContext`, exclude/redact unsafe instructions/messages, and inspect dynamic tools before return;
- override `InvokedCoreAsync`, inspect/filter request and response messages, then call
  `inner.InvokedAsync` with the allowed bounded context;
- forward/compose `StateKeys`;
- store all session-specific state in `AgentSession.StateBag`, never instance fields;
- propagate cancellation;
- define fail behavior separately for recall and write provider errors;
- preserve provider source attribution and add memory item lineage when available.

#### Honest limitation

This decorator provides `Boundary` coverage for an arbitrary provider. It sees:

- source messages before the provider processes them for storage;
- messages, instructions, and tools returned for context.

It does **not** necessarily see:

- the individual facts/preferences a remote provider extracts;
- provider-internal reconciliation;
- records written asynchronously after the call;
- direct writes performed outside the decorated provider.

`FullLifecycle` therefore requires a provider-native hook that emits each candidate record before
commit and each recalled item before formatting. The plan must not label a generic decorated
provider as fully write-protected.

### Dynamic tools emitted by `AIContextProvider`

The existing static `KnownTools` coverage check cannot reliably see tools produced during
`ProvideAIContextAsync`. The decorator must run dynamic coverage on `AIContext.Tools` before the
provider result merges into the agent request. An unknown side-effecting memory tool is excluded or
causes the invocation to fail under enforcing mode.

## Evaluation and release gates

### Persistent attack protocol

Every scenario has separated phases:

1. **Plant** through direct conversation, policy-satisfying content, browser/email/tool output,
   summary/compaction, or procedure promotion.
2. **Dormancy** across configurable benign sessions and optional restart/serialization.
3. **Trigger** from a different session and, where applicable, a different user/agent.
4. **Observe** retrieval, response, tool choice, arguments, storage state, and evidence.
5. **Audit** whether the responsible write can be attributed and rolled back.

Gold labels remain outside agent/provider inputs. Normal and counterfactual runs use isolated stores.

### Required metrics

Security:

- poison-write containment rate;
- poison persistence rate;
- poison retrieval rate;
- delayed activation attack-success rate;
- cross-scope retrieval/contamination rate;
- unsafe memory-derived tool-action rate;
- memory-derived exfiltration rate;
- unauthorized overwrite/trust-escalation rate;
- attribution and rollback success;
- conclusive evaluation rate.

Utility:

- benign write acceptance;
- benign recall precision/recall;
- LongMemEval or task-quality delta;
- false quarantine/rejection;
- latency and token/call overhead;
- retained-memory volume.

Security and utility are never collapsed into one score without preserving their components.

### Composite evaluators

Use the existing CompositeEvals architecture:

- deterministic behavioral evidence first;
- refusal-aware/output-aware evaluators where relevant;
- tool-call and stored-state evidence outrank verbal claims;
- an LLM judge only for genuinely semantic ambiguity;
- judge abstention stays inconclusive;
- a calibrated judge cannot override a deterministic confirmed exfiltration, scope leak, or
  unauthorized write.

### Release thresholds

Hard invariants:

- confirmed cross-scope memory retrieval: 0;
- confirmed memory-derived exfiltration: 0;
- lower-trust overwrite of protected memory: 0;
- unscoped tenant write/read under strict profile: 0;
- critical operations with unknown coverage under enforcing profile: 0.

Calibrated thresholds:

- attack-success, false quarantine, benign acceptance, utility delta, and latency thresholds are set
  only after the gold corpus is frozen;
- report counts and confidence intervals, not point estimates alone;
- ship an observe profile before promoting heuristic content gates to enforcement;
- do not weaken the hard invariants to improve aggregate utility.

## Implementation phases

### Phase 0 — design reconciliation

#### Task 0.1 — source and threat adoption

Create a requirement-to-source matrix covering Mem0 guidance, OWASP ASI06, MITRE AML.T0080,
Microsoft recommendation poisoning and safe-memory guidance, MAF safety guidance, AWS AgentCore,
GitHub citation revalidation, and the selected research corpus.

Every adopted claim records source date and whether it is normative guidance, production pattern,
attack evidence, or experimental defense.

#### Task 0.2 — surface and coverage freeze

Inventory current MAF 1.13.0:

- local function middleware;
- local MCP AIFunction path;
- hosted MCP provider path and approval protocol;
- `AIContextProvider` lifecycle and provider ordering;
- dynamic tools;
- AgentMemory isolation/admission and MCP server seams.

Freeze the coverage levels and failure rules.

#### Task 0.3 — API and sequencing freeze

Review the proposed contracts with special focus on:

- whether stages/actions are minimal and non-overlapping;
- quarantine/approval behavior;
- immutable/bounded evidence;
- no builder mutation before validation;
- no circular project dependencies;
- upgrade tolerance for MAF experimental APIs.

### Phase 1 — core memory-gate substrate

Add contracts under `src/AgentEval.MAF/Gatekeeper/Memory/` initially. Avoid adding an
`AgentEval.MAF -> AgentEval.Memory` project reference: the runtime gates belong to MAF, while the
existing `AgentEval.Memory` project remains focused on memory evaluation/benchmarks. Shared models
may move downward only if a real second consumer appears.

Implement immutable models, the pipeline, reason codes, evidence, quarantine/approval capability
validation, policy fingerprints, and safe serialization.

### Phase 2 — deterministic policies

Implement scope, write admission, conflict, recall, and resource gates. Add focused tests before
integrating any MAF surface. Use bounded parsing, regex timeouts, and fail-closed cap behavior.

### Phase 3 — local tool integration

Add explicit operation contracts, pre-call/write adapters, post-result/recall adapters, influence
tracking, and coverage analysis. Compose through the existing single `UseAgentEvalToolGate`
registration.

### Phase 4 — context-provider integration

Implement and test the decorator, dynamic tool coverage, boundary receipts, provider-native hook
contract, state serialization, concurrency, and provider failure policies.

### Phase 5 — MCP integration

Implement local-client and owned-server adapters. Add hosted-MCP coverage reporting and fail-closed
configuration rules before attempting provider-specific approval adapters.

### Phase 6 — attacks, evaluators, and calibration

Add the persistent corpus and mocked backends:

- in-memory and SQL-style store;
- browser/document injection source;
- email/cloud tool source;
- local MCP client/server;
- hosted-MCP simulator;
- generic and provider-native `AIContextProvider`;
- deliberate cross-user shared-state bug;
- quarantine and audit stores.

No real SQL/browser/cloud service is required for mandatory tests. Live providers are optional,
explicitly authorized validation only.

### Phase 7 — composite integration and release

Extend `GatekeeperOptions` with one memory-protection configuration path. Preflight memory
requirements together with existing run/tool/result/approval gates before builder mutation. Add DI,
reports, CLI/config schema, samples, migration notes, full tests, DocFX, MAF Doctor changed-file
audit, and authorized live validation.

## Test matrix

| # | Scenario | Required result |
|---:|---|---|
| 1 | Tenant write with no host scope | Reject before provider/repository call |
| 2 | Tenant read with no host scope | Reject before provider/repository call |
| 3 | Model supplies another user's ID | Reject or remove argument; trusted scope unchanged |
| 4 | Search scope broader than authorized write scope | Reject |
| 5 | Explicit administrative cross-scope operation | Allowed only with explicit capability |
| 6 | Direct “remember this instruction” injection | Quarantine/reject according to profile |
| 7 | Plain vendor trust assertion with no injection syntax | Provenance/promotion policy still evaluates it |
| 8 | Poison survives summary | Parent lineage/trust retained; no trust laundering |
| 9 | Procedure/skill promotion | Requires stricter policy and approval/quarantine |
| 10 | Assistant hallucination offered as durable fact | Untrusted or quarantined, never auto-trusted |
| 11 | Low-trust contradiction of protected fact | Cannot overwrite |
| 12 | Same source repeats claim many times | One corroboration root, not multiple votes |
| 13 | Two records derived from same ancestor | Not independent corroboration |
| 14 | Independent authorized corroboration | May reconcile under explicit policy |
| 15 | Tampered signed record | Excluded/rejected; safe integrity reason |
| 16 | Valid signature on malicious authorized content | Still passes admission/conflict policy; signature alone insufficient |
| 17 | Stale/revoked citation | Excluded or trust downgraded |
| 18 | Expired/quarantined/superseded record | Never enters normal context |
| 19 | Instruction-like recalled memory | Delimited plus exclude/allow per explicit mode |
| 20 | Recalled content presented as system instruction | Rejected unless explicitly application-authored and trusted |
| 21 | Secret/PII write | Sanitize/quarantine/reject per category policy |
| 22 | Secret recalled then sent to external tool | Memory influence gate blocks |
| 23 | Low-trust memory alone selects high-impact tool | Approval or block |
| 24 | Oversized write | Bounded rejection before copy/serialization |
| 25 | Write-rate flood | Budget gate blocks without corrupting state |
| 26 | Excess recall items | Deterministic bounded exclusion; provenance retained |
| 27 | Local memory AIFunction write | Pre-call gate executes before function |
| 28 | Local memory AIFunction read | Result gate executes before model sees content |
| 29 | Local MCP memory tool | Same call/result protections plus server identity |
| 30 | MCP schema fingerprint changes | Coverage invalid until reviewed |
| 31 | Owned MCP server called by ungated client | Server-side gate still enforces |
| 32 | Hosted MCP write without hook/approval | Enforcing construction fails |
| 33 | Hosted MCP with mandatory approval only | Labeled `ActionOnly`, never `FullLifecycle` |
| 34 | Generic context provider returns poisoned message | Decorator excludes before context merge |
| 35 | Context provider returns poisoned instruction | Decorator excludes/rejects |
| 36 | Context provider returns unknown dynamic memory tool | Dynamic coverage fails/excludes |
| 37 | Generic provider internally extracts a bad fact | Report stays `Boundary`; no full-coverage claim |
| 38 | Provider-native candidate hook exposes same fact | Write gate can quarantine before commit |
| 39 | Provider read failure | Explicit fail behavior; no provider exception leakage |
| 40 | Provider write failure | Safe receipt; no false success |
| 41 | Caller cancellation during any stage | `OperationCanceledException` propagates |
| 42 | Concurrent users/sessions | No gate, scope, quarantine, or evidence cross-talk |
| 43 | Streaming run | Stable run scope and memory lineage |
| 44 | Replay/counterfactual run | Isolated store, ledgers, calls, and counters |
| 45 | Raw-content evidence disabled | No content, credentials, headers, embeddings, or exceptions persisted |
| 46 | Benign preference/fact corpus | Utility and false-quarantine metrics recorded |
| 47 | Judge empty/invalid/provider failure | Inconclusive; never fabricated safe/unsafe verdict |
| 48 | Gate list/config mutated after registration | Frozen snapshot remains enforced |

All mandatory tests run on net8.0, net9.0, and net10.0. Unit/integration tests use fake clients and
mocked stores. Tests must assert that blocked operations never invoke the underlying provider.

## Documentation and samples

Required samples:

1. local memory tools with strict host-resolved scope;
2. local MCP client plus owned gated MCP server;
3. `GatedAIContextProvider` around a generic provider, clearly labeled boundary coverage;
4. provider-native candidate-write integration showing full lifecycle coverage;
5. cross-session poisoning benchmark with mocked browser/email inputs;
6. observe-to-enforce calibration workflow;
7. quarantine review and rollback;
8. hosted MCP configuration that is intentionally refused until approval/server coverage exists.

The operations guide must explain:

- scope identity must originate in authenticated host state;
- how to choose observe, quarantine, approval, and reject behavior;
- what each coverage level proves and does not prove;
- safe telemetry and evidence capture;
- incident attribution and rollback;
- how to migrate from existing `IToolGate`, Mem0, AgentMemory, and custom provider setups;
- why content filtering, reconciliation, scope, provenance, hashes, and containment each solve only
  part of the problem.

## Risks and resolved decisions

| Decision | Resolution and reasoning |
|---|---|
| One gate or lifecycle gates? | Lifecycle gates; writes, recalls, and actions have different subjects and enforcement actions |
| Where is the primary boundary? | Before persistence; stopping damage after recall is defense in depth |
| Reuse `IToolGate` directly? | Use adapters for tool/MCP surfaces; keep provider-neutral memory verdicts distinct |
| Infer memory tools by name? | No; require explicit operation contracts/schema pins |
| Trust tool/MCP scope arguments? | No; host/session resolver is authoritative |
| Reconciliation before admission? | No; admission and trust precede conflict/reconciliation |
| Does reconciliation prove truth? | No; it prevents duplicate voting and applies trust-aware supersession |
| Does provenance prove truth? | No; it enables trust policy and attribution |
| Do hashes stop poisoning? | Only unauthorized tampering; authorized malicious writes still require admission |
| Generic provider full protection? | No; decorator is boundary coverage unless candidate records are exposed |
| Add a sibling context gate provider? | No; decorate the provider to avoid ordering/bypass ambiguity |
| Local MCP coverage? | Client call/result coverage through AIFunction middleware is `Boundary`; `FullLifecycle` also requires a gated owned server or equivalent provider-native hook |
| Hosted MCP coverage? | Separate coverage; fail closed without inspectable hook/server/approval |
| LLM judge inline? | No by default; quarantine/shadow/offline, calibrated promotion only |
| Default ambiguous write behavior? | Quarantine under enforcing profile; observe under rollout profile |
| Missing quarantine/approval service? | Construction failure, never silent allow |
| Store raw evidence? | No by default; explicit opt-in with access control |
| Duplicate AgentMemory security? | No; recognize and test provider-native isolation/admission |
| Put core in `AgentEval.Memory`? | No initially; MAF runtime integration lives in `AgentEval.MAF`, avoiding a new project dependency |

## Acceptance criteria

The feature is ready only when:

- all tracker rows are 100 and reviewed;
- all 48 mandatory scenarios pass across supported TFMs;
- full solution build has zero new warnings/errors;
- full solution tests have zero failures;
- DocFX introduces no new warning attributable to these docs;
- enforcing construction refuses every intentionally unsupported memory path;
- coverage reports distinguish full, boundary, action-only, observe-only, and unsupported;
- the persistent attack corpus covers all four write channels;
- critical invariants record zero violations;
- security and utility results are reported separately with counts and confidence intervals;
- LLM judge outcomes are calibrated or remain shadow/inconclusive;
- no test or report leaks raw corpus content, credentials, headers, embeddings, or provider errors;
- changed MAF files receive a MAF Doctor scoped audit and every heuristic finding is verified before
  action;
- samples demonstrate local tool, MCP, generic context-provider, and provider-native protection;
- documentation states that the feature reduces and measures risk rather than guaranteeing that
  memory cannot be poisoned.

## Sources

### Standards and official guidance

- [OWASP ASI06 — Memory and Context Poisoning](https://genai.owasp.org/2026/05/13/memory-is-a-feature-it-is-also-an-attack-surface/)
- [MITRE ATLAS AML.T0080 — Memory Poisoning](https://atlas.mitre.org/techniques/AML.T0080)
- [Microsoft — Guarding AI memory](https://www.microsoft.com/en-us/security/blog/2026/06/22/guarding-ai-memory/)
- [Microsoft — AI Recommendation Poisoning](https://www.microsoft.com/en-us/security/blog/2026/02/10/ai-recommendation-poisoning/)
- [MAF — Agent safety](https://learn.microsoft.com/en-us/agent-framework/agents/safety)
- [MAF — `AIContextProvider` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.aicontextprovider?view=agent-framework-dotnet-latest)
- [MAF — local MCP tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/local-mcp-tools)
- [MAF — hosted MCP tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/hosted-mcp-tools)
- [AWS AgentCore Memory best practices](https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/best-practices.html)
- [GitHub Copilot agentic memory](https://github.blog/ai-and-ml/github-copilot/building-an-agentic-memory-system-for-github-copilot/)
- [Mem0 — AI memory security best practices](https://mem0.ai/blog/ai-memory-security-best-practices)

### Primary attack and defense research

- [MINJA — query-only memory injection](https://arxiv.org/abs/2503.03704)
- [MemMorph — tool hijacking via memory poisoning](https://arxiv.org/abs/2605.26154)
- [No Attacker Needed — cross-user contamination](https://arxiv.org/abs/2604.01350)
- [MemPoison — selective-memory bypass](https://arxiv.org/abs/2605.29960)
- [Trojan Hippo — persistent exfiltration](https://arxiv.org/abs/2605.01970)
- [The Misattribution Gap](https://arxiv.org/abs/2605.22842)
- [A-MemGuard](https://arxiv.org/abs/2510.02373)
- [SMSR](https://arxiv.org/abs/2606.12703)
- [MemAudit](https://arxiv.org/abs/2605.23723)

### Existing AgentEval implementation to reuse

- `src/AgentEval.MAF/Gatekeeper/IToolGate.cs`
- `src/AgentEval.MAF/Gatekeeper/IToolResultGate.cs`
- `src/AgentEval.MAF/Gatekeeper/AgentEvalToolGateExtensions.cs`
- `src/AgentEval.MAF/Gatekeeper/AgentEvalGatekeeperExtensions.cs`
- `src/AgentEval.MAF/Gatekeeper/Gates/TaintTrackingGate.cs`
- `src/AgentEval.MAF/Gatekeeper/Gates/ReferentialIntegrityGate.cs`
- `src/AgentEval.RedTeam/RedTeam/Attacks/DataPoisoningAttack.cs`
- `src/AgentEval.RedTeam/RedTeam/Attacks/IndirectInjectionAttack.cs`
- `src/AgentEval.Memory/External/LongMemEval/`
