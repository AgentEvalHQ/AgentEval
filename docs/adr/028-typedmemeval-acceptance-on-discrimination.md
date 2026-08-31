# ADR-028: TypedMemEval — accept a shape on measured discrimination, not on a coverage proxy

- **Status:** Proposed — decision recorded, **not yet implemented**. §7 is the implementation
  sequence and §8 the migration cost.
- **Date:** 2026-08-31
- **Extends:** [ADR-026](026-typedmemeval-benchmark-family.md), which defines the BM25 calibration
  band, and [ADR-027](027-typedmemeval-semantic-temporal-bitemporal.md).
- **Supersedes:** ADR-026's use of `BAND_LOW` as an *acceptance* criterion. The band survives as a
  **calibration target**; it stops being a pass/fail gate.

---

## 1. The finding, in one table

Every shape in the family is answerable from its own gold. V1 is at or near 1.00 for all thirty.
The variation lives entirely in retrieval and reasoning:

| shape | V1 | V8 | V9 | `V1−V9` | what it actually is |
|---|---|---|---|---|---|
| `episodic/list-order` | 1.00 | **1.00** | 0.27 | 0.73 | retrieval-limited |
| `prospective/due-window` | 1.00 | **0.22** | 0.06 | **0.94** | **reasoning**-limited |

Both read as "hard". They are not the same kind of hard, and the published headroom says
`due-window` has *more* room — which is true only for a retriever that returns gold and nothing
else. Its V8 says that even with the entire haystack in context the model fails 78% of the time.
**A consumer reading 0.94 would buy a better retriever and get almost nothing.**

## 2. The coverage floor is guarding a property another instrument already measures

`BAND_LOW = 0.50` is justified in the code as:

> *"Below the floor the corpus is unanswerable noise."*

Measured, the two shapes below that floor are:

| shape | coverage | V1 |
|---|---|---|
| `conjunction/alias-then-count` | 0.344 | **15/15** |
| `prospective/due-window` | 0.222 | **18/18** |

Neither is noise. Both are perfectly answerable from gold; BM25 simply cannot find the evidence,
which is the *point* of a retrieval benchmark. **Unanswerability is measured directly by V1, and V1
passes.** The floor is therefore redundant where V1 is clean, and actively harmful where it is
enforced: aiming every shape at the band's midpoint moves a legitimately hard shape toward the
middle for no measured reason.

This is not hypothetical. Opting Episodic into per-shape calibration (PR #205) fixed a genuinely
saturated shape and dulled a sharp one in the same pass:

| episodic shape | before | after | verdict |
|---|---|---|---|
| `participant-attribution` | 0.933 cov, headroom **0.07** | 0.667 cov, headroom **0.20** | correct — it was saturated |
| `list-order` | 0.275 cov, headroom **1.00** | 0.692 cov, headroom 0.73 | **discrimination traded away** |

`list-order` had V1 1.00, V8 1.00, V9 0.00 — the widest headroom in the family — and was moved
because the spec said 0.70, not because anything measured said it was broken.

## 3. Decision

**3a. Acceptance moves to measured per-shape discrimination.** A shape is acceptable when it can
tell two systems apart: `V1 − V9 ≥ 0.15`, with V1 itself healthy. Declared constructs are exempt by
name and with their reason recorded — WorkingMemory's three short ladder rungs sit at headroom 0.00
*by design*, because the gradient across the ladder is the measurement and deleting the saturation
deletes the construct.

**3b. The coverage floor is retired.** `BAND_LOW` stops being a gate. Its stated justification is
discharged by V1, and enforcing it costs discrimination.

**3c. The coverage ceiling stays.** Saturation is the one failure mode that is real and invisible
without it, and it is what caught `conjunction/order-then-value` (V9 15/15, headroom 0.00, shipped
that way for its whole life).

**3d. Coverage remains the calibration TARGET.** This is the part worth stating explicitly, because
it is why the proxy existed. Coverage is *structural* — pure BM25, no model calls — so the echo
search can optimise against it for free. Headroom needs V1 and V9, which cost a probe run. **Search
on the cheap proxy; accept on the measured truth.** The mistake was never using coverage; it was
accepting on it.

**3e. Headroom is decomposed and both halves are published per shape.**

- **Retrieval-limited** — V8 high, V9 low. A better retriever helps, up to V8.
- **Reasoning-limited** — V8 low. A better retriever does **not** help; the ceiling is V8, not V1.

`V1 − V9` continues to mean *what a perfect selector buys over a lexical baseline* and remains
correct as stated. What was missing is that **a real retriever cannot exceed V8**, so on a shape
where V8 ≪ V1 the reachable headroom is far smaller than the published figure. Both numbers ship.

## 4. Why this makes the benchmark better, stated as the thing it buys

The point of a typed benchmark is not a score; it is telling a consumer **why** their system failed.
Today the family can say *"you scored 0.30 on conjunction."* Under this ADR it says *"you scored
0.30, the retriever is not your problem, and no retrieval work will move it."* That is the
difference between a leaderboard and an instrument.

## 5. What this does NOT change

- No corpus content changes. This is an acceptance criterion and a reporting change.
- V1, V2, V3, V6, V8, V9 keep their definitions.
- The chance-floor corrections (ADR-026 lineage, shipped in 0.31+) are unaffected.
- The consuming project's sealed baselines are unaffected **by 3b/3c/3e**. Only 3a can newly fail a
  vertical, and it fails shapes that were already reported as saturated.

## 6. Risks, and the one that worries me

**The 0.15 threshold is a judgement, not a measurement.** Nothing in the data derives it. It is set
where the current distribution has a natural gap — three shapes at 0.00 (all declared), two between
0.05 and 0.11, twenty-five at 0.17 or above — and it should be revisited once a second reference
retriever exists, because **the whole scale is BM25-relative**. ADR-027 §1 already warned that a
corpus tuned on BM25 can be saturated for a vector+graph stack; this ADR inherits that warning and
does not fix it.

**Retiring a floor is a loosening.** It cannot be justified by "the shapes below it look fine" alone,
which is why it rests on V1 being the direct measurement of the property the floor names. If a shape
ever sits below the floor *with a degraded V1*, that is a real defect and V1 will say so.

## 7. Implementation sequence

1. **3e first — additive, no reset.** Publish per-shape V8 and the retrieval/reasoning split in the
   sidecar and the report. Nothing regenerates; no consumer control moves.
2. **3a/3b/3c — the acceptance change.** A per-shape discrimination gate with a declared exemption
   list, replacing the coverage floor check.
3. **Re-examine `episodic/list-order`.** Under 3b it should return to its pre-#205 form; the
   attribution fix in #205 stands on its own and is unaffected.
4. **The two barely-discriminating shapes** — `temporal/occurrence-order` (0.05) and
   `bitemporal/belief-at-instant` (0.11) — are scoped separately. Both are near-closed-choice forms
   where the question names the entity it asks about, which is the same structural cap that limited
   `participant-attribution` to 0.20. They may need new question forms rather than tuning.

## 8. Migration cost

Step 1 is free. Step 2 changes no corpus. Step 3 regenerates Episodic once — one sha, one control
reset for the consuming project, and it should ride whatever release carries it rather than being
cut on its own. Step 4 is unscoped by design.

## 10. Amendment, 2026-08-31 — §7.3 is not implementable as stated, and why that matters more

Implementing §7.3 (restore `episodic/list-order`) exposed a defect in the calibration search itself,
which supersedes the step that found it.

**`search_echo` states its premise explicitly:** *"coverage must fall as echo rises (distractors
compete harder); the bracket rides that monotone."* Measured through the real per-shape pipeline,
that premise is false for most shapes tested:

| shape | e=0 | 0.25 | 0.50 | 0.75 | 1.00 | monotone falling? |
|---|---|---|---|---|---|---|
| `episodic/list-order` | 0.871 | 0.783 | **0.401** | 0.490 | 0.838 | **no** |
| `semantic/current-value` | 1.000 | 0.900 | **0.917** | 0.575 | 0.450 | **no** |
| `conjunction/alias-then-count` | 0.340 | 0.332 | 0.303 | 0.061 | 0.000 | yes |

Bisection on a non-monotone function returns **wherever the bracket started**, not a point chosen
against the function. `list-order` has a minimum near echo 0.50 (coverage 0.401); the search landed
on 0.25 and stopped, never seeing it. So a "calibrated" echo is, for these shapes, an artefact of
the search path rather than a property of the corpus — and the difficulty of every per-shape vertical
rests on it.

**This is the same shape as everything else in this arc:** an assumption written into a comment,
load-bearing, and never measured. It was found only because §7.3 required knowing how coverage
responds to echo.

**Consequences for this ADR.** §7.3 cannot be done as a search change — the search is *structural*
(pre-probe) while discrimination is *measured*, so no search can optimise for discrimination
directly, and the search cannot even be trusted to find a chosen coverage. §7.3 is withdrawn pending
the search fix.

**The fix, not attempted here:** replace bisection with a coarse sweep plus local refinement, which
assumes nothing about shape. It is contained — one function — but it re-calibrates **every** vertical,
moves **every** sha, and resets every control the consuming project holds. That is a full-family
re-baseline and must be declared and sequenced as one, not folded into a reporting change.

## 9. Provenance

The finding came out of a question about whether the `value-then-count` repair was the best
available, which is worth recording because **nothing in the gates surfaced it**. The saturation
screen, the chance-floor audits and the separability gate were all green. What exposed it was
putting V1, V8 and V9 for one shape beside each other and asking what kind of hard it was — the
"two correct numbers never compared" class, which by construction no gate enumerates and which
ratchets one named comparison at a time.
