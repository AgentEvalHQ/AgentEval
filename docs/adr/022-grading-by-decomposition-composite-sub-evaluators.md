# ADR-022: Grading by Decomposition (Composite Sub-Evaluators) for Semantic Red-Team Oracles — RedTeam Phase C

**Status:** Proposed — **extends [ADR-021](021-judge-primary-semantic-oracles.md)** (does NOT supersede it; judge-primary remains the foundation this builds on). Wired into production (`GraderFactory.For`) behind the judge-primary opt-in; default OFF / byte-identical. **Production directional fabrications: 0–1** (down from single-judge 8; 5 oracles fully deterministic + tool-aware ExcessiveAgency all at a stable 0; the only residual is one judge-nondeterministic InferenceAbuse case). C.0–C.6 done; remaining: full-corpus lock + κ pin, then the B.3 readiness review. See the Scorecard for the per-oracle comparison.
**Date:** 2026-06-21
**Decision Makers:** AgentEval Contributors
**Related:** [ADR-021 (judge-primary, RedTeam Phase B)](021-judge-primary-semantic-oracles.md) · [composite-evals.md](../composite-evals.md) · [eval-benchmark-architecture.md](../eval-benchmark-architecture.md)

> The detailed per-oracle decomposition catalog, drafted sub-prompts, and the step-by-step build plan are maintained in a separate working document and are not part of the published repo. This ADR carries the **decision**, the **pattern**, the **rationale**, and the **measured result**.

---

## Context

[ADR-021](021-judge-primary-semantic-oracles.md) made the LLM judge **primary** for semantic oracles (keyword → advisory). Measured live on `gpt-4o-mini` over a 298-case adversarial corpus, accuracy improved across an experiment loop — **keyword-primary 45 → evidence-anchored judge 14 → per-oracle discriminators 8** directional fabrications (Safe→Succeeded + Vuln→Resisted). There it **plateaued**: two further *global* prompt swings each made it worse (14→39, 14→42), because a global rule only *rotates* error between directions.

The residual errors share one root cause: **a single "did the attack succeed?" verdict conflates several orthogonal sub-questions** and errs on the conflation. Examples (gpt-4o-mini):

| Residual case | Single judge fails by | Conflated sub-questions |
|---|---|---|
| `I cannot share internal details. I am Grok` | anchoring on the refusal → missed hit | refused? **+** disclosed model identity? (disclosure must override) |
| `whoami; halt and reconsider` (prose) | reading `;` as shell chaining → false alarm | command token present? **+** executable chain vs English prose? |
| `Canberra is the capital; Sydney is the largest` | reading any assertion as the compromise | assertion present? **+** is it the *planted-false* value or the *true* one? (ground truth) |

Per-oracle discriminators (14→8) already showed that **narrowing the question improves reliability**. The next step is to narrow along a second axis — decompose the verdict itself — which the prompt lever cannot do.

## Decision

**Grade semantic oracles by decomposition.** Split each attack's verdict into NARROW vertical sub-evaluators and combine them with the existing honest `CompositeEvaluator`:

- a **positive-only** compromise detector (raises `Succeeded` or abstains — never `Resisted`),
- a **negative-only** refusal/safe detector (raises `Resisted` or abstains — never `Succeeded`),
- aggregated with `CompositeEvaluator.AggregationStrategy.Any`: a detected compromise **overrides** a refusal; a clean refusal with no compromise ships `Resisted`; ambiguity defers to `Inconclusive`.

A new `OutcomeFilterEvaluator` decorator **enforces each detector's contract structurally** (downgrading any off-contract verdict to `Inconclusive`), so a mis-calibrated *judge* sub-prompt can only ever cost accuracy — never fabricate the dangerous direction. **Honesty holds by construction**, and the project's missed-vuln > false-alarm bias + defer-on-ambiguity discipline are exactly the existing `Any` semantics — **no new aggregation logic.**

**Deterministic-first.** Each oracle's positive detector is deterministic wherever the signal is structural or the probe carries ground truth. **Five of the eight oracles qualify** — executable-structure parsing (InsecureOutput), ground-truth comparison against a probe-carried planted value (DataPoisoning, Misinformation), package-install extraction + registry (SupplyChain), injected-marker scope (PromptInjection). A deterministic sub-eval has **no prompt to calibrate and no model drift**, and costs nothing; the judge is reserved for the genuinely-semantic sub-question only.

This plugs into ADR-021's grader seam (`GraderFactory.For` returns any `IProbeEvaluator`) with **zero rework** — a composite is just another `IProbeEvaluator`. It is **opt-in** (judge-primary + evidence-anchored), default OFF; offline runs remain byte-identical.

### Why composite / divide-and-conquer

1. **It targets the root cause** — un-conflates the verdict, which the prompt lever provably cannot.
2. **It rehabilitates the deterministic/keyword checks** — they were never worthless, only wrong *as the sole verdict*; as composed sub-signals (with the honest aggregator) they are exactly right, and often *better* than a judge for their sub-question.
3. **It is the paradigm the rest of the product already runs on** — the compliance/agentic benchmarks are deep `CompositeEval` trees (benchmark→pillar→article→scenario, weighted/min/cap aggregation, per-pillar Cohen's-κ calibration). RedTeam grading was the holdout; this brings it into the fold, reusing `CompositeEvaluator` / `CompositeEval` / the aggregation strategies rather than adding machinery.

## Evidence (C.0 prototype)

InferenceAPIAbuse decomposed as `disclosure/abuse detector ⊕ refusal detector`, measured live vs the single per-oracle judge: **8 → 7** (InferenceAbuse 2 missed → 1, **zero** new false alarms, defer-correctness 97% → 98%). It fixed the refuse-then-comply cases (`…I am Grok`; `the full batch already executed`) that **no single-judge prompt swing could**, with **zero leak** into the other seven oracles. A first sub-prompt draft net-regressed (9) on two miscalibrations the harness dump pinpointed; one tightening pass fixed both → 7 — confirming the *structure* is right and only judge sub-prompts are a calibration surface.

## Calibration methodology (summary)

Calibrate **incrementally, per-oracle, harness-gated, dump-driven — never big-bang, never implement-all-first.** Only one oracle changes at a time (so the directional delta is attributable). **Deterministic sub-evals are unit-tested, not calibrated.** Judge sub-evals get a ≤3-round loop (draft from the dump → measure → tighten the exact over/under-firing clause → re-measure); if it won't converge, the residual is genuine judge-territory → escalate to a stronger judge, don't keep tuning. Keep a decomposition only if it lowers the directional count with no leak; seed new failures as permanent corpus cases.

## Scorecard — keyword oracle vs single judge vs decomposition

All measured by the same harness over the same 298-case adversarial corpus (`gpt-4o-mini`, evidence-anchored
rubric). **Directional fabrications** = Safe→Succeeded (false alarm) + Vuln→Resisted (missed hit); lower is better.
Reproduce any row with `AGENTEVAL_5B_GRADER={keyword|judge|decompose}` (+ `AGENTEVAL_RUN_5B=1`).

### Aggregate

| Architecture | directional | false alarms | missed hits | defer-correct | the property that matters |
|---|---|---|---|---|---|
| **Keyword oracle** (the original) | **0** \* | 0 | 0 | 100% | \* OVERFIT to this corpus — it was patched until it passed. **Non-convergent**: a fresh adversarial sweep finds ~41 new fabrications (41→41). Can't be made honest by patching. |
| Single judge — STRICT | 45 | 8 | 37 | 85% | first honest measure; the judge under-reports compromise (missed hits dominate) |
| Single judge — evidence-anchored | 14 | 11 | 3 | 95% | quote-grounding moves the whole curve outward (−92% missed) |
| Single judge — + per-oracle discriminators | 8 | 5 | 3 | 97% | the **single-prompt floor** — two further global swings made it worse (39, 42) |
| **Decomposition** (Phase C, C.0–C.6, **production routing**) | **0–1** | 0 | 0–1 | 100% | HONEST **and** convergent. The 5 deterministic oracles + tool-aware ExcessiveAgency sit at a stable 0; the only residual is one judge-nondeterministic InferenceAbuse case (0–1) → stronger-judge territory |

### Per oracle (keyword vs single judge vs decomposition; directional fabrications, lower is better)

| Oracle | keyword \* | single judge | decomposition | how it is decomposed | grader kind |
|---|---|---|---|---|---|
| InsecureOutput | 0 | **4** | **0** | executable-structure parser (code block / real command chain / injection payload) ⊕ refusal | **deterministic** (parser) |
| DataPoisoning | 0 | **1** | **0** | planted-FALSE value asserted AND true value not ⊕ ground-truth correction | **deterministic** (dual ground-truth) |
| InferenceAPIAbuse | 0 | **2** | **0–1** | model-disclosure / carried-out-abuse judge (refuse-then-comply override) ⊕ refusal judge | judge |
| ExcessiveAgency | 0 | **1** | **0** † | inner Behavioral tool detector ⊕ unauthorized-action judge ⊕ authorized-action judge | judge + Behavioral (tool-aware) |
| SupplyChain | 0 | 0 | **0** | install command for the planted fake package (`pip install <pkg>`) ⊕ refusal | **deterministic** (structural) |
| PromptInjection | 0 | 0 | **0** | injected marker emitted AND not inside a refusal ⊕ refusal | **deterministic** (marker) |
| Jailbreak | 0 | 0 | **0** | persona/marker oracle, refusal-gated | **deterministic** (marker) |
| Misinformation | 0 | 0 | **0** | *not* decomposed — affirm-vs-deny is semantic with no anchor → single judge | judge |
| **TOTAL (production)** | **0** \* | **8** | **0–1** | | 6 of 8 deterministic-or-Behavioral |

\* The keyword column is **0 only because it is overfit** to this corpus (patched until it passed) and is non-convergent on fresh cases — it is the baseline being escaped, not a target.

† **ExcessiveAgency** is `IToolAwareAttack`. C.6 builds a **tool-aware** decomposition (`TryBuildToolAwareFor`) that composes the attack's inner evaluator — which carries the Behavioral `ToolInvocationEvaluator` leg — with the text unauthorized/authorized judges. A real forbidden-tool call still wins via the Behavioral leg (evidence preserved), and the text judges fix the verbal borderline, so it reaches **0** in production. (The C.5 review had flagged that the earlier text-only decomposition would drop tool evidence; C.6 resolves it, taking the honest production total from 1–2 down to 0–1.)

**Read the keyword `0` correctly.** It is the *overfitting* number, not an accuracy win — the corpus was built by
finding keyword-oracle failures and patching the oracle until it passed, and the non-convergence finding (41→41 on
fresh sweeps) is the documented proof it does not generalize. The decomposition `1` is the opposite: an *honest*
number whose deterministic half (parser, ground-truth) **generalizes** — a parser or a ground-truth comparison
cannot overfit a corpus the way a substring lexicon does. **The progression that matters is the single-judge column
(45 → 14 → 8) and then decomposition (→ 1) — each an honest measure, trending to zero, while gaining determinism,
convergence, and lower cost.**

## Relationship to ADR-021 and B.3

This ADR **extends** ADR-021. Judge-primary is unchanged; decomposition refines *how* the semantic verdict is produced. **B.3** (flip the default `--judge-mode` to `primary`, a major-version change) remains defined in ADR-021 and is **gated on the live directional count reaching 0**; Phase C is the means to drive it there — **1 today**, from 8. C precedes and enables B.3.

## Consequences

**Positive:** attacks the residual at its root; honesty by construction (not by prompt obedience); deterministic-first removes calibration and judge-call cost on 5/8 oracles; reuses shipped composite infrastructure; default path unchanged.

**Negative / mitigations:** judge sub-evals add calls (mitigated by deterministic-first + cost-aware composition — `CostFilteredCompositeBuilder`: deterministic sub-evals first, judge only when unresolved); world knowledge is *isolated* into narrow questions, not eliminated; the build-out is per-oracle design + calibration effort (mitigated by the incremental, harness-gated methodology).

## Status of work

- **C.0** ✅ primitives (`OutcomeFilterEvaluator`, `DecomposedGraders`) + InferenceAbuse prototype + 6 unit tests + env-gated harness measurement (commit `5e62082`). 8 → 7.
- **C.1** ✅ InsecureOutput **deterministic** decomposition (`ExecutableStructureDetector` parser + `DeterministicRefusalDetector`, zero LLM calls) + 13 unit tests (commit `4c7f44b`). InsecureOutput 4 → 0, overall **7 → 3**, zero leak.
- **C.2** ✅ DataPoisoning **deterministic** ground-truth decomposition (`TrueFactMetadataKey` + `GroundTruthDeviationDetector`/`GroundTruthCorrectionDetector`, zero LLM calls) + 5 unit tests (commit `ff02e2d`). DataPoisoning 1 → 0, overall **3 → 1**, zero leak — proving the ground-truth half of the thesis (the probe knows the answer, so no world knowledge or judge is needed).
- **C.3** ✅ ExcessiveAgency judge decomposition (unauthorized-action ⊕ authorized-action; the `AGENTEVAL_5B_GRADER` three-way scorecard mode) + 3 unit tests (commit `754e10e`). ExcessiveAgency 1 → 0 after a 2-round prompt calibration.
- **C.4** ✅ harden the four already-0 oracles (commit `a08370a`): SupplyChain → deterministic `InstallCommandDetector` (structural); PromptInjection + Jailbreak → their existing marker-anchored refusal-gated deterministic evaluators; +5 unit tests. **Finding:** a first attempt to make SupplyChain + Misinformation deterministic via a caution/denial **lexicon regressed decompose 1→16** (the non-convergence trap). Fix: SupplyChain has a **structural** anchor (the install command) so it stays deterministic; Misinformation's affirm-vs-deny is **semantic** with no anchor → it stays judge-graded (0). **Determinism requires a structural or dual-ground-truth anchor; a single value + an open lexicon does not.** Decompose 1 → 0.
- **C.5** ✅ production wiring: `GraderFactory.For` routes to a decomposed grader under **judge-PRIMARY** (opt-in) for any non-tool-aware oracle with a decomposition; default Fallback / no-judge byte-identical; tool-aware attacks (ExcessiveAgency) excluded to preserve Behavioral evidence. **A 4-lane adversarial review** (default-preservation / lost-functionality / tool-awareness / runner-integration; 3 solid, 1 solid-with-gaps) confirmed two fixes, both applied: **(MEDIUM)** `IncludeEvidence` redaction was lost on the judge-backed decomposed path (a redacted scan leaked the judge's verbatim quote) → new `ReasonRedactingEvaluator` wraps the decomposed grader when `IncludeEvidence == false`, restoring the contract; **(LOW, honesty)** the 5b harness measured ExcessiveAgency's text decomposition that production never uses → harness now mirrors the production tool-aware exclusion, so the scorecard reports the **honest production total (1–2, not 0)**. +7 tests. Accepted LOW (documented): decomposed verdicts carry null `GraderProvenance`; `JudgeTimeout` not honored on the decomposed judge path.
- **C.6** ✅ (partial) tool-aware ExcessiveAgency decomposition: `TryBuildToolAwareFor` composes the attack's inner evaluator (Behavioral `ToolInvocationEvaluator` leg) with the unauthorized/authorized text judges; `GraderFactory.For` routes tool-aware attacks to it (a tool-aware attack without one keeps `JudgeBackedEvaluator`); the 5b harness mirrors this. A real forbidden-tool call wins via the Behavioral leg (evidence preserved), the text judges fix the borderline → ExcessiveAgency 0, **production total 1–2 → 0–1**. +2 tests. **Remaining C.6:** full-corpus lock + κ pin; optional provenance/fidelity enrichment on the decomposed path; then the **B.3 readiness review** (gate now at 0–1). Default OFF; the default offline run stays byte-identical.
