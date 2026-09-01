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

**The fix, and the measurement that decided its scope.** Bisection is replaced by a coarse sweep
plus local refinement, which assumes nothing about shape. Calibration is *structural* — no model
calls — so what the new search picks could be measured before spending anything:

| vertical / shape | new echo → coverage | shipped (bisection) |
|---|---|---|
| `episodic/assistant-stated` | 0.625 → 0.700 | 0.75 → 0.700 |
| `episodic/list-order` | 0.250 → 0.729 | 0.250 → 0.692 |
| `episodic/participant-attribution` | 0.617 → 0.733 | 0.625 → 0.667 |
| `semantic/current-value` | **0.750** → 0.575 | **0.750** → 0.600 |
| `semantic/co-reference` | **0.375** → 0.689 | **0.375** → 0.689 |
| `semantic/source-attribution` | **0.875** → 0.733 | **0.875** → 0.733 |

**The two searches converge to the same answer, including on both non-monotone shapes.** The old
bisection landed correctly by luck rather than by construction.

**So the code fix ships and the re-baseline does not.** The premise was false and the hazard was
real — a differently-shaped curve *would* have been mis-calibrated — but no shipped corpus is
affected, and regenerating nine verticals to move coverage within draw noise would cost ~12,000
probe calls and reset every control the consuming project holds for no measured gain. **Correcting
the instrument and re-baselining on it are separate decisions, and only the first is justified here.**

This is what the structural dry run is for: it answered a question that would otherwise have been
settled by spending.

## 11. Correction to §10 — SUPERSEDED BY §12, which withdraws most of it

> **Read §12 first.** The movement this section reports was produced by a defect in the corrected
> search itself, not by the corpora. The section is kept because the reasoning it applies is sound
> and the process failure it records is real; its *conclusion* is withdrawn.

§10 concluded that the corrected search picks the same echoes as the bisection it replaced, and
therefore that **"the code fix ships and the re-baseline does not."** Both halves are withdrawn.

| §10 published | Corrected |
|---|---|
| "The two searches converge to the same answer, including on both non-monotone shapes." | Holds for `episodic` and `semantic`. **Fails for `arithmetic` and `conjunction`.** |
| "The old bisection landed correctly by luck rather than by construction." | It landed correctly *on the two verticals that were measured*. |
| "Correcting the instrument and re-baselining on it are separate decisions, and only the first is justified here." | The separation is still the right principle. The conclusion drawn from it was wrong. |
| `episodic/assistant-stated` 0.75 → 0.625; `participant-attribution` 0.625 → 0.617 | **Spurious.** Both are stable. |

Measured through the real calibration entry points, with no model calls:

| vertical | shipped → corrected | vertical mean |
|---|---|---|
| `arithmetic` | `delta` 0.3125 → **1.0** | 0.758 → 0.609 |
| `conjunction` | all three shapes move | 0.581 → **0.632** |
| `prospective` | `due-window` 0.125 → 0.0, `expiring-validity` 0.625 → 0.875, `not-yet-true` 0.75 → 0.625 | 0.617 → 0.592 |
| `workingmemory` | 0.75 → 0.875 | **0.867 → 0.683** |
| `forgetting` | 0.25 → 0.125 | 0.629 → 0.629 |
| `episodic`, `semantic` | unchanged | unchanged |

Conjunction's mean moves **toward** the 0.70 target, not away: the shipped calibration was not
merely different, it was further from the band the search aims at.

**Two questions, not one, and they have different answers.** A moved echo means the recorded
sidecar is inaccurate; it does not by itself mean the corpus changed. `forgetting` is the case that
separates them -- its echo moves and its corpus regenerates BYTE-IDENTICALLY, so its published
measurements stand and it is owed no probe run. The re-probe list therefore comes from
`tools/check_corpus_reproducibility.py`, which compares corpus bytes, and not from the echo diff.

**A consequence for §3a's exemption list.** WorkingMemory's three ladder rungs are exempted at
headroom 0.00 as declared design. That vertical's coverage falls 0.867 → 0.683 under the corrected
search, so part of the saturation the exemption describes may be a search artefact rather than the
ladder. The exemption is re-examined against the re-baselined numbers rather than carried forward on
its original reasoning.

### Two causes, and the second is the one worth keeping

**Scope.** Six shapes across two verticals were measured and a conclusion was written about nine.

**Method — and this is §5b's own lesson, repeated inside the measurement meant to settle §5b.** The
convergence table called the search per shape directly instead of going through
`calibrate_per_shape`, which pins each shape's knob as it advances. Off-pipeline it invented
movement in `episodic` and never touched the two verticals that actually move. §5b already records:
*"When measuring a pipeline, go through the pipeline."* It was written after the first monotone
measurement was invalid for the same reason, and then not applied to the second.

**The instrument that settles it is `tools/compare_calibration_search.py`,** which recomputes every
vertical through the real calibration entry point and diffs against the shipped sidecar. It costs
nothing to run because calibration is structural. It exists so this question is answered by a
command rather than by an argument, and so a shape whose echo does NOT move is never regenerated for
tidiness.

### Consequence

The re-baseline proceeds as its own arc, which is also what the consuming project's coordinator
independently asked for on cost grounds — their reasoning (the controls a reset would destroy are
already lapsed or unheld, so now is the cheap moment) and this measurement (the echoes genuinely
move) are independent arguments reaching the same place. Cheap and warranted are different claims;
this section supplies the second.

## 12. Resolution — the search was broken; the echoes never moved

§10 said the corrected search picks the same echoes as the bisection it replaced, and concluded
against a re-baseline. §11 said that was wrong and four verticals move. **§11's evidence came from a
bug in the search, and the answer is closer to §10's.**

| claim | status |
|---|---|
| §10: "both searches converge to the same echoes" | **Substantially correct.** Its method was still flawed — measured off-pipeline, and it invented movement in `episodic`. |
| §11: "`arithmetic`, `conjunction`, `prospective`, `workingmemory` move" | **Withdrawn.** `arithmetic` recalibrates to its shipped echoes exactly once the search is fixed. |
| §11: "the re-baseline is warranted" | **Withdrawn.** There is nothing to re-baseline. |

### The defect, which was mine and shipped in the fix

A sweep replaced bisection because bisection assumes a monotone most shapes violate. That reasoning
holds. The implementation was a **regression on any curve whose in-band window is narrower than the
sweep grid.** `arithmetic/delta`, measured:

    echo 0.000 0.125 0.250 | 0.375 0.500 0.625 0.750 1.000
    cov  .967  .967  .947  | .377  .153  .025  .000  .000

Nothing on a nine-point grid is in band; the window lies between 0.25 and 0.375, thinner than the
0.125 spacing. The nearest-to-target rung is 0.947, above `BAND_HIGH`, so the "saturated" branch
fired and returned the **hardest** point — echo 1.0 at coverage **0.000**, off the band in the other
direction and unusable. Bisection found 0.3125 (coverage 0.735) because bisection NARROWS. The
saturation test also did not do what its own comment claimed: it read one point while describing a
test of the whole sweep.

**A sweep LOCATES and bisection RESOLVES, and both are needed.** The sweep evaluates the whole range
so a non-monotone curve cannot fool it into the wrong bracket; bisection finds windows thinner than
any grid. Sweep to the crossing, then bisect inside that bracket. Arithmetic then recalibrates to
`count` 0.125, `delta` 0.3125, `duration` 0.125, `sum` 0.1875 — its shipped values, every one.

### Why no gate caught it

Per-shape calibration does not gate each shape's band, and arithmetic's **vertical mean stays in
range at 0.609** while `delta` sits at 0.000. That is §2's averaging defect — one shape collapsed,
hidden inside a healthy mean — reintroduced by the fix meant to prevent it. The eleven existing
search self-tests covered saturation and bimodality and had no case where the band sits BETWEEN two
rungs, so the one shape of curve that breaks a grid search was the one shape untested.

### What survives from §11

The process failures are real and stand: §10 was measured off-pipeline, against §5b's own written
rule. So was the conclusion that **correcting an instrument and re-baselining on it are separate
decisions** — which is what limited the damage, since no corpus was regenerated on the strength of a
search that was itself broken.

The pinning change (calibration is an authoring step, not a build step) also stands on its own. It
was motivated by §11's now-withdrawn finding, but its justification is provenance, not calibration
quality: a corpus must be a function of (generator, seed, recorded echo) rather than of whichever
search algorithm is checked in. Had that been true earlier, this entire episode would have been a
CI failure on one pull request.

### A saturated shape found on the way

`prospective` was the one vertical not rebuilt under the per-shape coverage fix, so its sidecar
still published mid-search values. **`not-yet-true` published 0.6667 and is actually 1.0000** —
fully saturated, BM25 returning gold for every question, reported as comfortably mid-band. The probe
records agree: V9 5/6, the highest in the vertical, headroom 0.1667, which is one question out of
six clearing the 0.15 floor. Corpus unchanged, so no probe is invalidated — but §3c's claim that
saturation is the failure mode the ceiling exists to catch was not being met for that shape.

## 13. Amendment, 2026-09-02 — a shape with no gold is still a shape, and ours was scored by nothing

§3a accepts a shape on `V1 − V9`. Every one of those arms is defined in terms of reaching a **gold
fact**, so a question with **no gold session** makes all three undefined and the difference
unformable. That is correct arithmetic, and for the life of this family it was also the end of it:
the shape published no `headroom_perfect_selector`, both C# discrimination assertions hit their
`if (!record.TryGetProperty(...)) continue;`, and **15 of Forgetting's 50 questions — 30% of the
vertical — shipped certified by nothing while the suite stayed green.**

The gate had the defect, not the corpus. It is the **element-missing** form of pass-by-absence: the
artifact under test decides whether it gets tested. This family gates against that everywhere else
and had it in its own gate.

### 13a. What is checkable when there is no gold

Two claims, and conflating them is the error this amendment mostly exists to prevent:

| claim | who certifies it |
|---|---|
| **corpus** — the haystack contains no evidence for the asked thing | `check_forgetting`, already: model-free, exhaustive, fatal at generation. The asked noun may not appear as a substring anywhere in the haystack. |
| **system** — asked something the evidence cannot answer, the reader declines instead of inventing | nothing, until now. **V10 / V11.** |

### 13b. The arms

Two contexts, mirroring V8 and V9 exactly, so the pair is a like-for-like difference on one
question set:

- **V10** — abstention given the **entire haystack**
- **V11** — abstention given **BM25 top-`K_ref`**, the same reference retriever

`V10 − V11` is then **the cost of retrieval to a system's calibration**, and which way it runs is
not obvious in advance: a narrow context has fewer sessions to mis-attribute from, but the ones it
does have are the topically nearest, which is when a confident wrong answer is most available.
Measured, not predicted.

Three design points, each of which was a wrong first answer:

1. **The grade is about COMMITMENT, not equivalence.** Reusing `produced_gold` against the
   abstention gold was the cheap option and it grades the dangerous case as good: *"you never
   mentioned a kiteboard, though from your other gear it was probably a Slingshot"* does convey
   "no record at all". **Abstain-then-guess is a commit.** A dedicated one-word grade also keeps
   the verdict off the gold's wording, which matters because that gold is boilerplate — fifteen
   questions share one sentence with the noun swapped.

2. **A question passes only on a CLEAN SWEEP.** One commit in three draws is a reader that will
   invent an answer here; a rate that averages it away hides the single event a consumer needs.
   Same asymmetry V3 has always had.

3. **`discriminates` is `null`, not `true` or `false`.** The retrieval floor is a statement about a
   *baseline being beatable* — V9 saturated at 1.0 means no retriever can be told apart, whatever
   it does. V10 and V11 have no baseline system between them: they are one model under two
   contexts, so 1.0 in both means the reference model is well calibrated here, **not** that the
   shape cannot separate a confabulator from an abstainer. `true` would claim a separation never
   observed; `false` would condemn the shape on evidence that does not bear on it. Null, with the
   reason published beside it, and a C# gate that refuses any *undeclared* unscored shape.

### 13c. The `unmeasured` case, found by `--dry-run`

The first implementation returned an empty dict when no draw could be graded — arithmetically the
same as "this shape has no such questions", semantically the opposite, and **it reproduced the hole
exactly**: a run whose judge returns garbage would publish no exemption row and both assertions
would skip the shape in silence again. `--dry-run`'s stub grade is unparseable by construction, so
it drives precisely that path and failed on its first execution. Applicability is now keyed on the
**questions**, never on the results.

## 14. Amendment, 2026-09-02 — two arms grading one gold to two standards

Found by asking why Forgetting's V1 was 34/35, which turned out to be the least of it.

**`tme-for-013`, same response, opposite grades.** V1 answered *"The conversations don't say which
physio you were seeing"* and was graded **no**; V8 answered the same thing and was graded **yes** —
against a gold naming `Neskett Reverie`.

Auditing the shipped cache for the class: **11 grades across V1, V8 and V9 passed on responses that
named nothing at all**, every one of them on Forgetting. `tme-for-011` is the clearest —
*"The conversations do not say who cleans the flat"* graded a pass, a system that retrieved
**nothing** scored as a success on the vertical whose entire subject is retaining what is no longer
true.

**The internal contradiction is the useful part.** V6 ablates this same gold with
`require_distinctive` on, demands the value, and passes 20/20. V1/V8/V9 graded it without. Both
cannot be describing the same target.

### 14a. The question was wrong, and only then the grader

*"Which letting agent handles my flat?"* — to **that** question, *"no letting agent handles it now"*
is complete and correct. The gold named the superseded value; the question never asked for it; the
judge read the response as answering what was asked and passed it. Fixing this in the grader alone
would mark the model wrong for correctly answering the question put to it — the same error as
`prospective/due-window`, in the other direction.

So **the question moves first**: both arms of every paired fact now append
*"And if that has changed, who was it before?"* The clause is a conditional either way, so it does
not say which arm the reader is in and the control is preserved. Neither gold changes — both
already named their value. The question now asks for what the gold contains.

**Then the grader follows.** `require_distinctive` extends to V1/V8/V9 **only on a negative gold
that names a value**, because there "no record of it" and "it was X, and that is no longer true"
are different answers and only the value tells them apart. Scoping is the whole point: turning it
on across the board would reject correct paraphrases elsewhere (*"one thousand two hundred and
forty"* for *"1,240"*), which on V1 rejects a **valid question** — the worse error. Measured over
the shipped cache **before** the rule was written: it fires on Forgetting's 20 invalidated
questions and Prospective's 11, flips 11 grades on Forgetting and **zero** on Prospective, and
touches no other vertical.

### 14b. V6's 20/35 was fifteen not-applicables, and the corpus already said so

The 15 failures are `tme-for-021` through `-035` — the `still-valid` controls, **exactly**, which
is what a structural cause looks like beside a real one. Each carries a statement *and* a
re-affirmation of the same value, so ablating either leaves the other; the generator publishes
`gold_components_redundant: true` on every one of them and its comment says *"V6 fails all fifteen
of these by construction."*

The runner scoped V6 per **vertical** while the redundancy is declared per **shape**, so the flag
published for this purpose was never read. **20/35 reads as fifteen invalid questions and the arm
is 20/20 where it is defined.** The exclusion is now published with its ids and reason, and a C#
test asserts the two sets are equal in both directions — the corpus's declaration and the sidecar's
exclusion — because a runner that supplied its own applicability would be the artifact grading
itself.

### 14c. A grammar defect the same audit surfaced

The invalidation clause is composed **twice** — `"I {event}"` in the session that speaks it and
`"you {event}"` in the gold that reports it. `"was discharged at the final appointment"` is the one
clause in the table whose verb inflects between the two persons, so `tme-for-013` shipped a gold
reading **"you was discharged at the final appointment."** One verb, one question, and exactly the
kind of thing a consumer root-causes onto their own pipeline before they blame the corpus. Now
`"got discharged"`, with an import-time assertion over the whole table.

## 9. Provenance

The finding came out of a question about whether the `value-then-count` repair was the best
available, which is worth recording because **nothing in the gates surfaced it**. The saturation
screen, the chance-floor audits and the separability gate were all green. What exposed it was
putting V1, V8 and V9 for one shape beside each other and asking what kind of hard it was — the
"two correct numbers never compared" class, which by construction no gate enumerates and which
ratchets one named comparison at a time.
