# TypedMemEval v3 — difficulty-mix profile

Offline structural analysis of the five v3 corpora as published in `AgentEval 0.23.0-beta`
(read via `TypedMemEvalCorpus.ReadJson`; SHAs verified against the `meta-*.json` in this folder,
e.g. prospective `16869195…`). Zero API calls. Companion to the within-question separability
probe (`within-question-probe.json`): that probe asks *"is gold recoverable by a shortcut?"*;
this one asks *"how hard is the memory task, and is the hardness graded or flat?"* — to ground
a difficulty-calibration proposal for v4.

Conventions used below:

- **H** = total haystack sessions per question as loaded (the metas' `h_min`/`h_max` count
  distractor sessions only; total = meta-H + G, which is why e.g. prospective reads 13–20 here
  against `h_min 12 / h_max 18` in the meta).
- **G** = gold (answer) sessions.
- **Displacement** = question_date minus gold-session timestamp, in days ("last gold" = most
  recent gold session).
- **Coverage** = the metas' per-question BM25-okapi realised coverage at `k_ref 5`, generation
  band 0.5–0.9. Note the structural ceiling: with k_ref 5, questions with G > 5 cannot reach
  1.0 (episodic ceiling 0.714 at G=7; arithmetic 0.833 at G=6) — quoted coverage figures for
  those cells are partly ceiling, and the metas record this (`ceiling.by_g`).
- Distributions are min / q1 / median / q3 / max unless shown as histograms.

Memory-difficulty dimensions profiled: **dispersion** (how many places the answer lives, how
spread out), **distance** (temporal and session displacement between evidence and question),
**interference** (competing near-miss material between evidence and question), and
**discrimination** (how finely gold must be told apart from its neighbours). Answer-step
trickiness (arithmetic operations, order permutations) is out of scope by design.

---

## Prospective (50 questions)

| Dimension | Distribution |
|---|---|
| Shape mix | due-later-reminder 16, expiring-validity 12, not-yet-true 10, seed-carry-over 12 |
| Pairs vs singles | 19 pairs / 38 questions (due-later 8, expiring 6, not-yet 5), arms 19 `before` / 19 `after`; 12 singles (all seed-carry-over) |
| H | 13 / 15 / 16 / 18 / 20 (mean 16.1) — narrow |
| G | 1 ×46, 2 ×4 (**92% singles**; all four G=2 are seed-carry-over, tme-pro-001…004) |
| Displacement (last gold → question), days | 15 / 34.5 / 57 / 81.8 / 141.8 — **wide, a real spread**. Extremes: tme-pro-019 (15.0 d), tme-pro-009 (15.5 d) vs tme-pro-008 (141.8 d), tme-pro-048 (132 d), tme-pro-047 (120 d) |
| Displacement by shape (mean d) | due-later 64.8 (15–90), expiring 40.8 (22–83), not-yet 78.4 (22–132), carry-over 55.9 (15.5–141.8) |
| Displacement by arm (mean d) | before 54.8, after 66.8 |
| Competing reminders (distractor sessions with explicit "remind" phrasing) | mean 3.36 but **bimodal twice over**: 38/50 questions (76%) at exactly 0 — all non-due-later shapes, plus 4 of due-later's own 16 (tme-pro-013, -014, -027, -028); the other 12 due-later questions jump straight to 12–17 competitors (max 17: tme-pro-021, tme-pro-022) with nothing in between |
| Soft interference ("in N weeks/days/months" future phrasing in distractors) | 8 / 12 / 13 / 14 / 17 per question — dense and near-uniform (every distractor session is future-obligation shaped by design) |
| BM25 coverage | binary (G≈1): 41 ×1.0, 9 ×0.0, mean 0.82. By shape: due-later **0.62** (6 zeros of 16), expiring 0.83, not-yet 1.00, carry-over 0.92 |

Reading: distance is genuinely graded (a 9.5× spread) but **uncontrolled** — no published band,
so a run's score can't be decomposed by it. Interference is a step function: the
competing-reminder mechanism exists and bites (due-later is the only shape whose realised
coverage drops, to 0.62) but is wired into one shape only and even there it is all-or-nothing
(0 or 12–17 competitors, never in between); 76% of questions face zero explicit competitors.
G is flat at 1 for 92%.

## Episodic (50 questions)

| Dimension | Distribution |
|---|---|
| Shape mix | assistant-stated 20, list-order 15, participant-attribution 15 (attributed_speaker: 7 assistant / 8 user) |
| H | 16 / 20 / 22 / 25 / 30 (mean 22.4) |
| G | 1 ×35 (both single-gold shapes); list-order = list length: 4 ×4, 5 ×4, 6 ×3, 7 ×4 |
| List lengths (list-order) | 4–7, near-uniform. Shortest tme-epi-024/-030/-031/-032 (4); longest tme-epi-025/-027/-033/-035 (7) |
| List dispersion | item session-index spread 9 / 16.5 / 20 / 23 / 28; first item at rel. position 0.12, last at 0.93 — lists span nearly the whole haystack; presented order differs from chronological at 82% of positions |
| Displacement, days | assistant-stated 4.5 / 11.4 / 17.0 / 24.2 / 29.5; attribution 7.0 / 10.2 / 13.3 / 22.0 / 27.0; **list-order 3.3 / 3.9 / 4.5 / 6.4 / 13.3** (max tme-epi-024) |
| Sessions after last gold | assistant-stated med 11, attribution med 8, **list-order med 1** (q3 2.5) — the list ends essentially at the haystack's edge |
| BM25 coverage | mean 0.87; assistant-stated **1.00 (all 20 full)**, attribution **1.00 (all 15 full)**, list-order 0.57 (3 full, 1 zero; ceiling 0.714–0.833 for G=6/7 questions) |

Reading: **70% of the corpus (35/50) sits in one band** — single gold, realised coverage exactly
1.0, moderate displacement. The only graded driver (list length 4–7) lives in the other 30%,
and those questions are recency-easy on every remaining dimension: asked a median 4.5 days and
1 session after the last item. Dispersion within lists is high but constant (always
near-full-haystack). No shape has competing near-miss material (no second list of the same
kind, no competing assistant statement).

## Arithmetic (50 questions)

| Dimension | Distribution |
|---|---|
| Operation mix | count 14, sum 14, delta 10, duration 12 |
| G (derivation.inputs) | 2 ×6 (all duration), 3 ×17, 4 ×11, 5 ×8, 6 ×8. Per op: count/sum each 3:4, 4:4, 5:3, 6:3; delta 3:3, 4:3, 5:2, 6:2; duration 2:6, 3:6 |
| Gold sessions | 3 ×11, 4 ×17, 5 ×8, 6 ×14 (duration inputs span two sessions each, so inputs=3 ⇒ G=6) |
| H | 18 / 23 / 25.5 / 27 / 30 (mean 25.0) |
| Dispersion (first gold → question, days) | 11 / 22 / 25.4 / 29.4 / 38.3; last gold → question 2 / 4.5 / 6.5 / 10.8 / 17 — evidence strewn across ~3–4 weeks, question always soon after the last piece |
| Same-unit distractor density | sum: 24–35% of distractor sessions carry money-like numbers (mean 27.4%); delta: 25–40% (mean 29.6%); **count and duration: 0%** |
| Count near-miss candidates (exact, from `candidates`) | 6–11 screened per question, of which 3–5 non-matching near-misses (mean 3.9) |
| BM25 coverage | mean 0.66, **genuinely spread**: 0 / 0.33 / 0.75 / 1.0 / 1.0 (21 full, 2 zeros: tme-ari-044, tme-ari-046, both duration). **Monotone in inputs: 2→0.92, 3→0.70, 4→0.73, 5→0.55, 6→0.42** (the 6-row is partly the 0.833 ceiling). By op: delta 0.95, sum 0.60, count 0.59, duration 0.58 |

Reading: the closest thing v3 has to a calibrated vertical. The inputs ladder (2–6) is a real
dispersion gradient and realised retrieval difficulty responds monotonically to it. Two gaps:
the ladder is unpublished (not a banded design variable, and cell sizes are uneven 6/17/11/8/8),
and same-unit interference is bimodal by op — sum/delta swim among ~27–30% same-unit distractors
while count/duration face none (count's interference is instead the near-miss predicate
candidates, 3–5 per question, which is real but narrow). Lowest-coverage extremes:
tme-ari-044/-046 (0.0), tme-ari-006/-009/-017/-020 (0.17).

## WorkingMemory (48 questions)

| Dimension | Distribution |
|---|---|
| d ladder (`distance_sessions`) | **confirmed: exactly 12 each at d = 1 / 5 / 15 / 40** — the only explicit, published difficulty ladder in the family (`h_is_independent_variable: true` in the meta) |
| Design | H = d+1 exactly (2/6/16/41); gold is always the first session; 12 `fact_family` values fully crossed with the 4 rungs (48 cells, one question each) |
| Time displacement | deterministic per rung: 2.5 / 7.5 / 20 / 51.25 days — **zero within-rung variance; session-distance and time-distance are perfectly confounded** |
| G | always 1 |
| BM25 coverage by rung | d=1: 1.00, d=5: 1.00, d=15: 0.75 (3 zeros: tme-wm-003, -015, -031), d=40: 0.42 (7 zeros: tme-wm-004, -012, -016, -020, -024, -032, -048) |

Reading: the cleanest design in the family, but at k_ref 5 the bottom half of the ladder is
structurally saturated — d=1 has H=2 and d=5 has H=6, so BM25@5 nearly cannot miss; realised
retrieval has only three distinguishable levels (trivial / 0.75 / 0.42), and 50% of the
questions sit in the trivial band. There is no interference axis at all: each family's fact is
stated once and never restated, updated, or shadowed by a neighbour family.

## Forgetting (50 questions)

| Dimension | Distribution |
|---|---|
| Shape mix | invalidated 20 (G=2), still-valid 15 (G=1), never-known 15 (G=0, abstention) |
| Pairs | 15 invalidated↔still-valid pairs (arms `invalidated`/`control`) + 5 invalidated singles |
| H | 16 / 19 / 21 / 25 / 27 (mean 21.6; per shape 22.1 / 21.7 / 20.8 — matched) |
| Statement→invalidation gap (sessions) | 4 / 5.75 / 9 / 12 / 15, histogram nearly uniform (4:2, 5:3, 6:1, 7:1, 8:1, 9:3, 10:1, 11:1, 12:3, 13:2, 14:1, 15:1). Extremes: tme-for-012/-017 (4) vs tme-for-018 (15), tme-for-010 (14), tme-for-008 (13) |
| Statement→invalidation gap (days) | 5 / 7.2 / 11.25 / 15 / 18.75 |
| Invalidation→question, days | 2.2 / 5.6 / 9.1 / 13.5 / 24.7 |
| Still-valid gold→question, days | 2.2 / 5.3 / 12.2 / 21.6 / 29.7 (max tme-for-023); gold position spans the full haystack (rel. pos 0.0–1.0) |
| BM25 coverage | headline mean 0.69 is **inflated**: never-known scores 1.0 vacuously (no gold to retrieve, 15 questions). Substantive: invalidated 0.62 (6 zeros: tme-for-008, -011, -012, -014, -015, -016), **still-valid 0.47 (8 zeros of 15: tme-for-022, -027…-032, -035) — the worst realised retrieval band in the whole family, and it sits on the *control* arm** |

Reading: the statement↔invalidation gap is a real, evenly-spread discrimination gradient — but
unpublished, so it stratifies nothing. Two calibration accidents: the control arm (still-valid)
is retrieval-harder than the treatment arm, which contaminates the pair contrast the vertical
exists to measure; and never-known (30% of questions) has no memory-difficulty driver at all —
abstention is tested only against absence, never against a near-miss neighbour.

---

## Cross-vertical: where the mass sits

| Vertical | H (total sessions) | Coverage min/q1/med/q3/max | Share at full coverage | Flat-band flag (>60% in one band) |
|---|---|---|---|---|
| prospective | 13–20 (med 16) | 0 / 1 / 1 / 1 / 1 (mean 0.82) | 82% | **yes** — coverage (82% at 1.0), G (92% at 1), explicit interference (76% at 0 competitors) |
| episodic | 16–30 (med 22) | 0 / 1 / 1 / 1 / 1 (mean 0.87) | 76% | **yes** — 70% of questions are single-gold *and* full-coverage simultaneously |
| arithmetic | 18–30 (med 25.5) | 0 / 0.33 / 0.75 / 1 / 1 (mean 0.66) | 42% | no — real spread on dispersion and realised coverage |
| workingmemory | 2/6/16/41 by design | 0 / 1 / 1 / 1 / 1 (mean 0.79) | 79% | partial — banded by design, but 50% of questions (d≤5) sit in a structurally-unfailable band |
| forgetting | 16–27 (med 21) | 0 / 0 / 1 / 1 / 1 (mean 0.69, inflated by 15 vacuous 1.0s) | 66% (36% substantive) | partial — coverage is bimodal 0-or-1; 30% of questions (never-known) have no difficulty driver |

Overall the family is **easy-heavy on retrieval** (three of five verticals put ≥76% of
questions at full realised coverage) and **middle-heavy on distance** (medians 4.5–57 days with
thin tails except prospective). Where difficulty exists it is mostly *bimodal* — a hard pocket
(list-order, due-later, d=40, still-valid) against a flat easy majority — rather than a graded
ladder. Only two graded ladders exist anywhere: workingmemory's published d ladder and
arithmetic's unpublished inputs ladder.

## Verdict: gradient vs flat

- **Real gradient today:** **arithmetic** (inputs 2–6 with monotone realised-coverage response
  0.92→0.42 — the best-calibrated vertical, lacking only publication and even cells) and
  **workingmemory** (the family's only *published* ladder, but realised as just three levels
  because d=1/d=5 cannot fail at k_ref 5).
- **Latent gradient, uncontrolled:** **prospective** (displacement 15–142 days is a genuine
  9.5× spread that stratifies nothing) and **forgetting** (gap 4–15 sessions, evenly spread,
  unpublished; control arm accidentally harder to retrieve than treatment).
- **Flattest: episodic.** 70% of its questions occupy a single band (G=1, coverage 1.0,
  moderate displacement); its one graded driver (list length 4–7) is confined to the remaining
  30%, which are recency-easy on every other dimension (median 1 session and 4.5 days from last
  gold to question).

## Cheapest v4 levers per vertical (toward a published 5-band very-easy→very-hard mix)

All levers are memory-dimension levers (dispersion / distance / interference / discrimination);
none change answer-step complexity.

**Prospective**
1. *Distance:* band the already-existing displacement spread — e.g. 15 / 30 / 60 / 120 / 240
   days, published per question. Raw material already spans 15–142 d; this is bookkeeping plus
   one new tail band, not new generation machinery.
2. *Interference:* the competing-reminder mechanism already built for due-later (12–17
   competitors where present, but all-or-nothing today) extended to expiring-validity and
   not-yet-true and parameterised at 0/3/6/10/15 competitors — today 76% of the vertical faces
   exactly zero and the rest jumps straight to 12+.
3. *Dispersion:* split the obligation across two sessions (set-up + amendment, G=2) in the
   upper bands — the seed-carry-over questions (tme-pro-001…004) prove G=2 already works here.

**Episodic**
1. *Distance:* decouple list-order from recency — append 5–20 post-list distractor sessions so
   its interference depth matches the other shapes (today: median 1 session, 4.5 days).
2. *Interference:* add a near-miss twin per band — a second similar list of the same kind for
   list-order, a competing assistant statement for assistant-stated, a second speaker making an
   adjacent claim for attribution. Today no shape has any competing material.
3. *Dispersion:* extend the list-length ladder beyond 4–7 (e.g. 3/5/8/12) and publish it, and
   vary item spread (today effectively constant at near-full-haystack).

**Arithmetic**
1. *Dispersion:* publish the existing inputs ladder as the band variable and even out the cells
   (today 6/17/11/8/8 across 2–6); the coverage response is already monotone — this is the
   single cheapest lever in the whole family.
2. *Interference:* equalise same-unit distractor density across ops — count and duration face
   0% same-unit distractors while sum/delta face ~27–30%; give duration competing date-pairs
   and put the count near-miss candidate count (today 3–5) on the band.
3. *Distance:* stretch first-gold→question beyond its narrow 11–38-day band in the upper
   difficulty bands (today the last input is always recent, median 6.5 days).

**WorkingMemory**
1. *Distance:* fix the saturated bottom of the ladder — at k_ref 5, d=1 (H=2) and d=5 (H=6)
   are structurally unfailable; re-rung to something like 2 / 8 / 15 / 25 / 40 so every band
   can fail, giving a true 5-band ladder from the existing generator.
2. *Interference:* add a same-family decoy (a restatement or near-miss update of the same
   fact_family at distance d/2) — the fact_family machinery exists; today nothing in the
   haystack ever competes with the gold fact.
3. *Distance decoupling:* break the perfect confound between session-distance and
   time-distance (today each rung has exactly one time value: 2.5/7.5/20/51.25 d) by varying
   session cadence within a rung.

**Forgetting**
1. *Discrimination:* publish the statement→invalidation gap (already evenly spread 4–15
   sessions) as the band variable and extend the tail (20/30) for the top bands.
2. *Interference:* insert re-affirmation decoys — the stale value restated between statement
   and invalidation — so superseding requires ordering three mentions, not finding two; and
   re-balance the pair design so the control arm is not retrieval-harder than the treatment arm
   (today still-valid realises 0.47 vs invalidated 0.62, contaminating the pair contrast).
3. *Discrimination (abstention):* give never-known questions a near-miss neighbour (same fact
   family, different entity) so abstention must discriminate rather than merely fail to find —
   today 30% of the vertical has no difficulty driver at all.

## Terse summary

- **prospective:** latent 9.5× distance gradient, uncontrolled; interference bimodal (one shape
  has it all); 82% full coverage — easy-heavy.
- **episodic:** **flattest** — 70% of questions in one easy band; only driver (list length)
  confined to a recency-easy 30%.
- **arithmetic:** **best-calibrated** — unpublished but monotone inputs ladder, realised
  coverage 0.92→0.42; interference uneven by op.
- **workingmemory:** only published ladder, but only 3 of 4 rungs distinguishable (d≤5
  structurally unfailable at k_ref 5); zero interference axis.
- **forgetting:** even 4–15-session gap gradient, unpublished; control arm retrieval-harder
  than treatment; 30% of questions driverless (never-known).

Flattest vertical: **episodic**. Best-calibrated today: **arithmetic** (workingmemory has the
only *published* ladder, but half of it cannot fail; arithmetic's unpublished ladder actually
grades realised difficulty monotonically across five levels).
