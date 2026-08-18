# ADR-027: TypedMemEval — Semantic, Temporal and Bitemporal verticals (design)

- **Status:** Proposed — design only. **No corpus may be generated under this ADR until §9's gates clear.**
- **Date:** 2026-08-18
- **Supersedes:** nothing. Extends [ADR-026](026-typedmemeval-benchmark-family.md), which owns the five
  shipped verticals (corpus revision v5, released 0.25.0-beta).
- **Requested by:** the consuming project's prompt 09.

> **Naming.** `v5` in this document always means the shipped *corpus revision* of the existing five
> verticals. The verticals proposed here are new and would land as a later revision. The request
> itself carried this warning; it is repeated because the collision is genuinely confusing.

---

## 1. Context, and the one number that reframes the whole request

The consuming engine ships eight memory types; TypedMemEval covers five. This ADR designs three of
the missing four — **Semantic**, **Temporal**, **Bitemporal**. Procedural stays out permanently: it
needs tools, a live agent loop and observed outcomes, so it cannot be a static corpus.

**The reframing signal.** On Arithmetic v5 the consuming stack measures realised coverage **1.000**
against the corpus's calibrated BM25 floor of **0.636** — it retrieves every gold session and still
answers wrong. Three things follow, and they shape every decision below:

1. **The calibrated floor is a property of BM25, not a prediction about a real retriever.** A corpus
   tuned to 0.5–0.9 on BM25 can be fully saturated for a vector+graph stack. The floor is a
   *construction control*, not a difficulty claim, and ADR-026's coverage tables must be read that way.
2. **Dispersion dials cannot discriminate a stack that retrieves everything.** Spreading evidence
   across more sessions changes nothing for a retriever that finds all of it. Every dial proposed
   here therefore changes what must be **reasoned**, not where the evidence sits.
3. **The retriever half of band validation is measuring the wrong thing** (§6).

**The discipline point, which we accept.** Five verticals have consumed five corpus generations and
have not yet produced a citable number. This ADR is design only. Generation waits on §9.

## 2. What we are refusing, and why

A refuted vertical is a better outcome than a weak one, so the refusals come first.

### 2.1 REFUSED: Semantic as "plain facts stated once and asked about later"

This is the LongMemEval construction, and it is saturated for any competent stack — LME's own
realised coverage is 0.965–0.980 with 94–98% of questions at 1.00. Rebuilding it against a 0.5–0.9
BM25 band does not fix that, for the reason in §1.1: the band is a BM25 property. We would spend a
generation producing a corpus that cannot separate a good retriever from an adequate one, which is
the request's own success criterion for this vertical.

**What survives** is a narrower vertical whose difficulty is *resolution*, not retrieval (§3.1). If
that is not what the consuming project wants from "Semantic", the honest answer is that we should not
build it, and LME's saturated semantic questions remain the correct thing to cite for plain recall.

### 2.2 REFUSED: recency-decayed BM25 as the time-aware reference retriever

Prompt 09 §2.3 argues that Bitemporal forces a time-aware reference retriever, and that solving it
"also unblocks the two existing verticals" whose bands are currently unvalidatable — presenting that
dependency as the reason to build Bitemporal first. **We measured it. The dependency does not exist.**

Recency-decayed BM25 (`score × exp(−λ·age)`) on the shipped v5 corpora:

| Vertical | λ = 0 | λ = 0.01 | λ = 0.03 | λ = 0.08 |
|---|---|---|---|---|
| Forgetting — usable bands | 3 | 3 | 3 | 3 |
| Forgetting — trend rho | n/a | n/a | n/a | n/a |
| Prospective — trend rho | **+0.40** | +0.40 | **+0.80** | **+0.80** |

Forgetting is unchanged at every λ, and Prospective gets **worse** — rho moving further from a
gradient, not toward one. The endpoint drop does rise (0.21 → 0.29), which is exactly the
endpoint-versus-trend confusion ADR-026 §19 removed from the validator: the extra drop is carried by a
band with **n = 1**.

The reason is visible without a retriever at all:

| Prospective band | H | mean gold age | competitors |
|---|---|---|---|
| 1 | 14.0 | 22.0 d | 13.0 |
| 3 | 15.5 | 59.5 d | 14.5 |
| 5 | 15.9 | 118.0 d | 14.9 |

The dial moves gold **age** by a factor of five and leaves **competition** flat. Forgetting's moves
neither (competitors 18–21, gold age non-monotone across bands). **These are not retrieval dials**, so
no reference retriever repairs them, time-aware or otherwise. Prospective's displacement changes what
must be *computed*; Forgetting's gap changes a *position*. Both are correctly stamped unvalidated
today, and that stamp is the answer rather than a placeholder for a better retriever.

**Bitemporal still goes first** (§8) — but on its own merits, not on this dependency.

## 3. The three verticals

### 3.1 TypedMemEval-Semantic — resolution, not recall (n = 50)

| Shape | n | The question | Why it cannot saturate |
|---|---|---|---|
| `current-value` | 20 | An attribute is stated, then restated with drift, *k* times. What is it **now**? | Retrieving all *k* statements is necessary and not sufficient; the answer requires ordering them and taking the last. A stack that retrieves everything must still resolve. |
| `co-reference` | 15 | A fact is stated under one designation for an entity and asked under another ("the place on Ferrow Row" / "the new flat"). | Lexical retrieval of the asked term does not reach the session that states the fact. This is the one shape where retrieval quality genuinely differs between stacks. |
| `source-attribution` | 15 | Which session did this belief come from? Scored on correctness of the cited source, not presence. | §2.5's provenance ask, resolved as a shape — see §7. |

**Dial: contradiction depth** `k ∈ {1, 2, 3, 4, 5}` for `current-value`; **designation distance** (how
many alias hops between statement and question) for `co-reference`; **candidate-source count** for
`source-attribution`. All three change reasoning load and none changes dispersion.

**Boundary vs Forgetting.** Forgetting asks *"is it still true, or was it cancelled?"* — the outcome
space includes abstention and the gold is sometimes "nothing recorded". Semantic `current-value` asks
*"what is it now?"* after *k* **replacements**, never a cancellation, and never abstention. Different
question, different typed-outcome vector. If a generated corpus blurs these, the shape is wrong.

### 3.2 TypedMemEval-Temporal — order of occurrence, not arithmetic (n = 50)

| Shape | n | The question |
|---|---|---|
| `occurrence-order` | 20 | Which of these events happened first? |
| `interval-position` | 15 | What was the state at the end of the second week? |
| `recency` | 15 | Which is the most recent X? |

**The construction that makes this non-trivial, and it is the whole design.** If events are narrated
in the order they occurred, every ordering question is answerable from the **session timestamp index
alone**, with no reasoning and no reading — a metadata sort, not a memory measurement. So Temporal's
defining property is that **narration order deliberately disagrees with occurrence order**: events are
mentioned retrospectively ("that was after the thing in March"), so the answer must be assembled from
stated relations rather than read off the index.

**Dial: narration disorder** — the number of events whose narration position differs from their
occurrence position, plus the length of the relation chain needed to order them. A reasoning dial by
construction.

**Boundary vs Arithmetic.** Arithmetic computes a value from numbers — sums, counts, durations.
Temporal orders events, and **no Temporal answer may be a number requiring addition**. That rule is
mechanical enough to enforce in a generator check rather than a review. "How long between" therefore
belongs to Arithmetic and is excluded here, which is the request's own concern about Temporal becoming
"Arithmetic with dates" answered as a generation constraint.

**Boundary vs Episodic `list-order`.** Episodic orders **mentions** — the sequence in which items were
added to a shortlist. Temporal orders **occurrences**, which can contradict mention order. The two are
distinguishable precisely because Temporal requires that contradiction to exist.

### 3.3 TypedMemEval-Bitemporal — two clocks that disagree (n = 60, 30 pairs)

Valid time (when a fact was true) against transaction time (when the system learned it). They diverge
only after a **retroactive correction**, and the divergence is the test:

> Recorded in September: *"Alice moved to Berlin in March."*
> *"Where did Alice live in April?"* → Berlin — **valid time**.
> *"In June, what did you believe about where Alice lived in April?"* → the old answer, because the
> correction had not arrived.

| Shape | pairs | Gold flips on |
|---|---|---|
| `belief-at-instant` | 18 | the **belief** axis; the truth axis holds steady |
| `correction-depth` | 12 | which of ≥ 2 successive corrections was current at the asked instant |

**Why this is the one worth building even if the others slip.** A single-clock store cannot represent
the difference, so its ceiling is **structural**, not a retrieval-quality artefact. Nothing in the
field measures it. And the pairs are designed so that a system silently answering the valid-time
question when asked the transaction-time one is **wrong**, not imprecise — which is what makes the
result a capability statement rather than a score.

**Dials, all temporal and all reasoning-side:** correction **latency** (event-to-record gap),
**corrections per fact**, and **instant separation** (how far apart the two asked instants sit).

**BUILT AND MEASURED, 2026-08-18.** 60 questions, 30 pairs, corpus
`agenteval-typedmemeval-bitemporal-v5` (`f5b384d7f0ff`), BM25 coverage 0.800.

| Probe | Result |
|---|---|
| V1 oracle answerability | **60/60** |
| V1 pair-flip | **30/30** — every pair's two clocks give different answers |
| V2 non-inferability | **60/60** |
| V3 gold-ablated | **60/60** |
| V8 full-haystack | 59/60 — **interference cost 0.0167** |

**A prediction in this ADR's first draft, refuted by its own probe.** §3.3 argued Bitemporal would
carry a large interference cost by construction — a system handed the whole haystack would see the
correction and answer the corrected value on the transaction arm. It does not: V8 is 59/60. The
answer model reads session timestamps and reasons about "recorded before the asked instant"
correctly, unaided.

**That is a better property than the one predicted.** V1 ≈ V8 ≈ 1.0 means the corpus contains
neither reasoning ambiguity nor retrieval difficulty — a reader given the sessions gets it right
either way. So a real memory system failing the transaction arm cannot be explained by an
unanswerable question, an ambiguous frame, or a model that cannot compute "before". It is
attributable to the store having no way to represent *when it learned a thing*. A full-context score
of ~1.0 is precisely what makes the structural-ceiling claim testable instead of confounded, and it
means **Bitemporal is the one vertical in this family whose headline number is about the system under
test rather than about the answer model.**

**Two defects the probes caught during construction**, both recorded because both were mine:

- **The correction quoted the value it superseded** — "…was at Ardenholm from February, **not
  Calderwick**" — and `Calderwick` *is* the transaction arm's answer. Ablating that arm's gold left
  the answer sitting in the correction: V3 failed **28 of 60**, every failure a transaction arm.
  Corrections no longer name what they replace.
- **Band and shape were collinear.** Banding on correction depth put `belief-at-instant` entirely in
  band 2 and gave `correction-depth` bands 3–5 — the Arithmetic confound (ADR-026 §19) rebuilt from
  scratch. The dial is now **correction latency**, which both shapes vary, and the band × shape
  cross-tab §6 requires shows no band owned by one shape.

**Boundary vs Prospective.** Prospective tests **firing** — has the thing come due — on one clock,
with gold flipping as the asked time moves past a due date. Bitemporal tests **belief** on two clocks,
with gold flipping as the asked *belief instant* moves across a correction while the truth is
unchanged. A Prospective pair differs in *when it is asked*; a Bitemporal pair differs in *which clock
is being asked about*.

## 4. The reference retriever for Bitemporal: as-of windowing

§2.2 refused recency decay. What Bitemporal actually needs is not a better ranker but a **correctness
precondition**, and it is deterministic:

> Retrieval for a transaction-time question is restricted to sessions recorded **at or before the
> asked instant**. The window is derived from the question, not tuned.

No λ, no learned component, nothing to calibrate — an auditable filter over the timestamps already in
every corpus record. It matters because a transaction-time question is **ill-posed without it**: if the
retriever can see the September correction while being asked what was believed in June, the question
has no defensible answer, and a system that answers it "correctly" has done so by ignoring the ask.

Two consequences to state plainly:

- **This is new corpus surface, not just retriever surface.** Every Bitemporal question must name its
  asked instant explicitly, and the metadata must record it, or the window cannot be derived.
- **It does not help the existing five.** In all five shipped corpora every session precedes the
  question, so an as-of window at question time is the identity function. §2.2's measurement stands.

## 5. Constraints inherited, and one that is already satisfied

Everything ADR-026 enforces: V1 oracle answerability per shape, V2 non-inferability, V3 gold-ablated,
V6 leave-one-out where components exist, V7 separability under the post-v4 statement-grammar rules
including the per-role and per-slot slicing, difficulty bands stamped with their validation state,
typed outcomes never one percentage, corpus SHAs and probe records in metadata, and the consuming
project's probe as a pre-tag acceptance gate.

**Prompt 09 §3 asks us to carry a new invariant into the next revision:**

> Per question, the set of role sequences present in gold must equal the set present in distractors.

**It is already implemented and enforced family-wide.** ADR-026 §18/§19: sessions are aligned onto a
shortest-common-supersequence of their question's role sequences, the condition holds with **zero**
gold-only sequences in all five shipped verticals, `role_sequence` and every `position_N_is_*` read
exactly 0.5000, and `stamp_typedmemeval_separability.py --self-test` reconstructs the defect in CI and
asserts refusal. Their concern that Temporal and Bitemporal will both put `has_answer` on an assistant
turn is correct — a retroactive correction is naturally something the assistant states — and it is
handled by construction rather than needing per-vertical care.

## 6. Band validation must change, and the reason is their measurement

ADR-026 validates a difficulty band in two halves: the reference retriever's coverage must slope down
across the bands, and the answer model on gold alone must stay flat. **The first half is a BM25
property**, and §1.1 shows it does not predict a real stack: coverage 1.000 against a 0.636 floor.

**Proposal — V8, interference cost.** Measure accuracy given the **full haystack**, not gold alone.

- `V1` = accuracy given gold sessions only — *is the question answerable at all?*
- `V8` = accuracy given the entire haystack — *what does interference cost?*
- **`V1 − V8` is the room retrieval quality has to matter.** A corpus where V8 ≈ V1 cannot
  discriminate any two retrievers, because a perfect retriever and no retriever produce the same
  answer. That is precisely LME's saturation, stated as a measurement rather than a complaint.

**Band validation becomes: V8 slopes across the bands, V1 stays flat.** Both halves are then
answer-side, neither depends on the reference retriever, and the rule tests the thing the bands claim.

### 6.1 V8 measured on the shipped five — and it answers the question that prompted it

**Implemented and run** (2026-08-18). V8 is identical to V1 in question, judge, screen and
applicability; the only difference is the context. Every number below is on the shipped v5 bytes.

| Vertical | V1 (gold only) | V8 (full haystack) | **interference cost** |
|---|---|---|---|
| Prospective | 49/50 | 48/50 | **+0.02** |
| Episodic | 48/50 | 50/50 | **−0.04** |
| Arithmetic | 47/50 | 42/50 | **+0.10** |
| WorkingMemory | 60/60 | 60/60 | **0.00** |
| Forgetting | 35/35 | 35/35 | **0.00** |
| **Family** | **239/245** | **235/245** | **+0.016** |

**Four of five verticals have an interference cost of essentially zero, and the family figure is
1.6%.** On WorkingMemory, Forgetting and Episodic a perfect retriever and *no retriever at all*
produce the same answers. Two consequences, and neither is comfortable:

1. **The shipped family cannot measure retrieval quality**, except on Arithmetic. This is the exact
   explanation for the consuming project's observation that they read realised coverage 1.000 against
   a 0.636 calibrated floor: the floor is a BM25 construction control, and above it there is no
   answering-side headroom for a better retriever to win. Their adaptive-router decision now has a
   measurement rather than a proxy behind it.
2. **Episodic's cost is NEGATIVE.** Two questions that fail with gold alone succeed with the whole
   haystack, so V1 is not a strict ceiling — surrounding sessions supply context the gold alone does
   not. Worth stating because ADR-026 describes V1 as "the ceiling", and on at least one vertical it
   is not one.

### 6.2 V8 cannot carry band validation, and on Arithmetic it inverts the labels

§6's proposal was that V8 replace the retriever half of band validation. **Measured, it cannot** —
there is no headroom to slope on four of five verticals:

| Vertical | V8 by band | spread |
|---|---|---|
| WorkingMemory | 1.00 / 1.00 / 1.00 / 1.00 / 1.00 | 0.00 |
| Forgetting | 1.00 / 1.00 / 1.00 / 1.00 / 1.00 | 0.00 |
| Episodic | 1.00 / 1.00 / 1.00 / 1.00 | 0.00 |
| Prospective | 1.00 / 1.00 / 1.00 / 1.00 / 0.86 | 0.14 |
| **Arithmetic** | **0.33** / 0.76 / 1.00 / 1.00 / 1.00 | **0.67** |

Arithmetic is the only vertical with real spread, and **it runs the wrong way**: the band labelled
*easiest* is where the answer model fails two questions in three. ADR-026 §19 recorded this as a
confound from the oracle half (spread 0.17 → 0.33 across revisions); V8 shows it at **0.67** and
shows what it actually is — **Arithmetic's difficulty labels are anti-correlated with difficulty for
any system with good retrieval.** Band 1 is the hardest band in the vertical. `duration` living at two
and three inputs is not a caveat on an otherwise-good ladder; it is the ladder pointing backwards.

**So V8 ships as a diagnostic and an acceptance gate, not as a band validator.** The gate is: a
vertical whose interference cost is ~0 cannot claim to measure retrieval, and must say so in its
metadata. Band validation stays as ADR-026 defines it, with the BM25 half demoted to a construction
control per §6, and the honest position is that **the shipped family validates bands on a retriever
proxy while having no answering-side headroom to check them against.**

### 6.3 Feasibility

**Measured rather than assumed**, because a probe that cannot fit its own prompt is not a proposal.
Full-haystack sizes on the shipped v5 corpora:

| Vertical | max H | max ≈tokens | mean ≈tokens |
|---|---|---|---|
| WorkingMemory | 61 | 11,049 | 5,412 |
| Episodic | 31 | 6,121 | 4,339 |
| Arithmetic | 31 | 5,421 | 4,188 |
| Prospective | 19 | 5,682 | 3,409 |
| Forgetting | 27 | 4,751 | 3,700 |

One call per question, ~4.5k input tokens on average and ~11k at worst — the same order as V1 and
cheaper than V3, which samples ablations three times per question. There is no context-window
objection, and WorkingMemory's H = 61 rung is the only place that would ever approach one.

The BM25 coverage gate is **kept** — but demoted in what it claims. It stays a construction control
(is the corpus neither noise nor trivially indexed?) and stops being read as difficulty.

**And the Arithmetic lesson applies at design time.** ADR-026 §19: Arithmetic's dispersion ladder had
`duration` living at the low input counts, which is where the answer model is weakest, so part of a
clean gradient was the oracle failing — and the confound *widened* in v5 (oracle spread 0.17 → 0.33).
For each vertical here, **cross-tabulate band against shape before generating, and refuse a design
where one shape dominates a band.** The §3 tables are deliberately built so every shape spans the full
band range; the cross-tab is a §9 gate, not a post-hoc check.

## 7. Provenance: a shape, not a vertical

Prompt 09 §2.5 asks us to assess *"where did this belief come from?"* and decide with reasons.
**Decision: a shape inside Semantic (`source-attribution`), not its own vertical.**

- It needs no new corpus mechanics — a haystack, one fact, and several plausible candidate sources.
  A vertical would duplicate Semantic's construction to host one shape.
- It is **not** Episodic's `participant-attribution`, which asks *which speaker* said something inside
  a session. Provenance asks *which session* a belief came from. Different mechanism, same family of
  concern, and worth keeping distinct in the ADR so the two are not read as duplicates.
- Scoring on **correctness of the cited source** rather than presence is the part that makes it worth
  having, and it is also the part that makes it cheap: the gold source id is already in every record.

One caveat we will not hide: a system that cites no source cannot be scored wrong, only abstaining. The
shape therefore needs the abstention arm that Forgetting already has, or it will reward silence.

## 8. Sequencing, and what to build if we can only build two

**Build Bitemporal first.** Not for §2.2's refuted dependency, but because it is the only one of the
three that measures something nothing else measures, its ceiling for a single-clock store is
structural rather than a matter of tuning, and the as-of machinery and instant-naming metadata it
introduces are what Temporal reuses.

**Then Temporal**, which shares Bitemporal's timestamp discipline and adds the narration-disorder
construction.

**Semantic last, and narrowed** per §2.1 — `current-value`, `co-reference`, `source-attribution`, with
plain single-statement recall explicitly out of scope.

**If only two: Bitemporal and Temporal.** They share machinery, they are the two the field does not
cover, and Semantic is simultaneously the most at risk of saturation (§2.1) and the one whose
boundary against a shipped vertical (Forgetting) is narrowest. Two certified verticals beat three soft
ones, and this is where the third would go soft.

## 9. Gates before any generation

1. **Gate (c): the consuming project's first baselines on v5 report.** In flight. Extending an
   instrument that has never produced a citable number is how instruments outrun their validation.
2. ~~V8 implemented and run on the five shipped verticals~~ — **CLEARED 2026-08-18, and it was a
   finding about them.** Interference cost is ~0 on four of five (§6.1) and Arithmetic's bands are
   inverted (§6.2). The three new verticals must therefore be built for answering-side headroom from
   the first draft: a dispersion ladder cannot be repaired into one later.
3. **Band × shape cross-tab published for each proposed vertical**, with no shape dominating a band.
4. **The two open v6 items from ADR-026 §19 closed or explicitly deferred with reasons:** Arithmetic's
   widened oracle confound (spread 0.33) and WorkingMemory's coverage drift toward the ceiling
   (0.767 → 0.867 on the family's only validated ladder).

## 10. Composability commitments (prompt 09 §2.4)

Prompt 10 will build a cross-type Conjunction vertical by *composing* certified verticals rather than
authoring fresh questions. Not in scope here; not foreclosed, via two commitments that are cheap now:

1. **Per-item type labels on gold.** Every gold item in every vertical — the three proposed here and
   the five shipped — carries the memory type it belongs to. Composition needs to know what it is
   joining and the consuming decision rule needs a per-type denominator. For the shipped five this is
   a metadata addition, not a regeneration.
2. **One structural convention across verticals.** Shared session and turn shape, shared filler
   conventions, shared padding and echo machinery. This is already true — all five shipped verticals
   go through the same `equalise_echo` / `equalise_reply` / `equalise_shape` pipeline in
   `tools/typedmemeval_common.py` — and it must stay true, because merging two corpora built to
   different conventions is an efficient way to manufacture exactly the structural tell that took
   ADR-026 §18/§19 two revisions to remove. **Treat cross-vertical merge as requiring its own V7 run
   on the merged corpus**, not as inheriting the certifications of its parts.

## 11. Open questions

1. **Is narrowed Semantic worth building at all**, or does `co-reference` belong in Episodic and
   `current-value` in Forgetting as new shapes? A three-shape vertical whose shapes each have a
   plausible existing home is a weak vertical.
2. **What is the reference retriever for Temporal's calibration gate?** BM25 over session text works
   for construction control, but occurrence order is not lexically expressed, so the coverage number
   will be even less predictive than usual. §6's V8 may have to carry band validation alone here.
3. **Does Bitemporal need a single-clock control arm** — the same haystack run against a store told to
   ignore transaction time — to make the structural ceiling a measurement rather than an argument?
4. **n per shape versus the citable floor.** Every shape above is 12–20, against ADR-026's n ≥ 30 floor
   for a citable figure. These are per-*vertical* citable and per-*shape* diagnostic, exactly as the
   shipped five are; if the consuming project needs per-shape citable numbers, the n must roughly
   double and that is a generation-cost decision, not a design one.
