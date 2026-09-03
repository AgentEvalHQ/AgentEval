# ADR-029: Build the conditional, not procedural memory

- **Status:** **Accepted and BUILT** (2026-09-02, §9) — **REOPENED 2026-09-03 (§10)** and **BUILT the same day (§11)**: the Procedural vertical this ADR declined ships as 80 questions at headroom +0.81, committed to a branch and deliberately unreleased while the counterparty's co-design reply is outstanding. §9 stands; §6a does not, and
  neither does the title — it is kept because retitling an ADR hides the reversal.
- **How it got here, which is the useful part:** an earlier draft proposed narrowing ADR-027's
  exclusion and was put to an adversarial review that refuted three of its four decisions (§8). The
  maintainer then set aside the one political objection, leaving the two technical ones — and
  re-examining those produced a smaller, better design that dissolves all of them (§7b).
- **Date:** 2026-08-31, rewritten 2026-09-01 after review.
- **Relates to:** [ADR-026](026-typedmemeval-benchmark-family.md) §8, which is where the exclusion
  actually lives; [ADR-027](027-typedmemeval-semantic-temporal-bitemporal.md) §1;
  [ADR-028](028-typedmemeval-acceptance-on-discrimination.md), whose acceptance criterion any new
  vertical must clear.

---

## 1. THE EXCLUSION IS BILATERAL. THIS PROJECT CANNOT NARROW IT.

ADR-026 records it twice, and both times as an agreement rather than a preference:

> **No Procedural vertical** — consumer-side by agreement (needs tools; agentic).
> — ADR-026 §8, line 792

> Procedural memory stays consumer-side (it is agentic and needs tools); it is out of scope for
> AgentEval and this family.
> — ADR-026, line 373

**Procedural memory has an owner: the consuming project, by agreement.** The first draft of this ADR
asserted the opposite — that if the exclusion stood, procedural memory would have "no owner anywhere
in the product" — and used that as the cost that made narrowing look like the only responsible
answer. That claim was false, and it was the load-bearing one.

The correct move is therefore **not** to amend ADR-027 §1 on one party's authority. It is to put the
finding in §2 to the counterparty and ask whether the agreement should be reopened, because the
premise it was struck on is the part that no longer holds. **That question has been sent** (see the
2026-09-01 correspondence); their answer is an input this repository cannot contain, which is
precisely the condition under which a decision stays open rather than being guessed.

## 2. THE FINDING THAT IS WORTH SENDING

ADR-027 §1 excluded procedural memory because it "needs tools, a live agent loop and observed
outcomes, so it cannot be a static corpus." **The mechanism half of that reason does not hold**, and
it is worth the counterparty knowing:

- `IToolCapableAgent` already hands an agent tool schemas and returns a trace of emitted calls.
- `CanaryTool.Execute` is **optional**, and `EvidenceFidelity.IntentToAct` already distinguishes a
  call the model *emitted* from one that *executed*.

So an agent can be handed tools, asked to carry out a procedure it learned sessions earlier, and
scored on the **emitted call sequence** — no sandbox, no side effects, no state threading. Observed
outcomes are needed to test whether a system *improved*; they are not needed to test whether it
*remembered*.

**One structural gap is also real and unclaimed: nothing in the family has a conditional.** Checked
across all nine generators. Whether that deserves a shape, and where, is §5.

## 3. WHAT THE FAMILY ALREADY COVERS, WHICH THE FIRST DRAFT MISSED

The first draft listed five properties as evidence for a new vertical. Four of them have homes:

| property | existing home |
|---|---|
| ordered steps | `episodic/list-order`, `temporal/occurrence-order` — and pairwise-order credit already ships (`PairwiseOrderAccuracy`, `TypedMemEvalRunner.cs`) |
| retired steps | `forgetting` |
| partial revision | `semantic/current-value`, `bitemporal/correction-depth` |
| preconditions | unhomed |
| **conditional branch** | **unhomed — the only one nothing covers** |

ADR-027 §11.1 sets the standard this must be judged against: *"A three-shape vertical whose shapes
each have a plausible existing home is a weak vertical."* By that standard the honest scope is **at
most two candidate shapes inside existing verticals**, not a tenth vertical.

## 4. THE CONTRADICTION AT THE HEART OF THE FIRST DRAFT

It argued distinctness and buildability with premises that negate each other:

- **Distinctness (§2):** a procedure is different because its order is **causally required** —
  violating it is an error, not merely a wrong answer.
- **Buildability (§3):** it survives V2 by borrowing Temporal's technique — invented steps whose
  order is **arbitrary and stated**.

Arbitrary-and-stated is the negation of causally-required. ADR-026 §5.2 already settles which one
the family permits: ordered gold must be *"arbitrary (V2) and derivable only from session sequence,
never from narrative logic (no 'starter before dessert' orderings)."* **Causal necessity is narrative
logic.** So the property that would make a procedural vertical distinct is the property the family's
own construction rule forbids — and once removed, what remains is Episodic list-order.

**This contradiction must be resolved in writing before any design proceeds.** Either the order is
arbitrary (and the vertical is not distinct) or it is causal (and it cannot pass V2). No third
option has been articulated.

## 5. TWO MORE REASONS THE EVIDENCE PREDICTS REJECTION

**V2 is documented blind in exactly this question class.** `tools/name-collision-audit.json`,
measured 2026-08-30 — the day before the first draft — records: *"V2 scores only a leak that AGREES
with gold… Harm therefore concentrates in ORDERING questions."* A procedural corpus is ordering
questions end to end. And inventing names does not reach the leak: in a procedure the prior is
carried by the **verbs** (back up before migrating, test before deploying), not by the entity names,
so a non-referential noun bank buys nothing.

**No discrimination argument was offered, and ADR-028 predicts a poor one.** Acceptance now rests on
`V1 − V9 ≥ 0.15`. ADR-028 §7.4 names the exact structural cap a procedural question inherits —
near-closed-choice forms *"where the question names the entity it asks about"*
(`temporal/occurrence-order` 0.05, `bitemporal/belief-at-instant` 0.11,
`episodic/participant-attribution` 0.20). *"What are the steps of the Verrin changeover?"* names its
own procedure and is lexically self-retrieving, which drives V9 up and headroom toward zero. The
family's nearest existing conditional-shaped join, `conjunction/order-then-value`, shipped saturated
at V9 15/15, headroom 0.00, for its whole life.

## 6. WHAT IS ACTUALLY DECIDED HERE

**6a.** ADR-026 §8's exclusion **stands unchanged**. This project does not narrow it.

**6b.** The mechanism premise in ADR-027 §1 is **recorded as refuted** (§2). That is a finding about
the reason, not a change to the decision.

**6c.** The **conditional-branch gap is recorded as real and unhomed** (§3). If anything proceeds, it
proceeds as at most one or two shapes inside existing verticals, judged by ADR-028's discrimination
floor like everything else.

**6d.** The §4 contradiction is **open and blocking**. No design work should start before it is
resolved in writing.

## 7. What is asked of the counterparty

Whether the agreement recorded in ADR-026 §8 should be reopened, given that the mechanism it was
struck on (§2) does not hold. Nothing else in this ADR needs their answer; nothing in this ADR
proceeds without it.

## 7b. RESOLUTION — build the CONDITIONAL, not "procedural memory"

**Added 2026-09-01 after the maintainer set aside the ownership objection** ("we can decide this
alone; the other party is simply not ready to use it, but if it works, it will"). That removes §1 as
a blocker. It does not remove §4 or §8's boundary refutation, which are technical and decide whether
the thing would WORK — so they were re-examined rather than waved through, and one of them turns out
to point at a much better design.

### The gap, verified rather than asserted

Of the five properties §3 claimed were distinct, four have existing homes. **One does not, and it is
now measured:** across all 470 shipped questions, **not one requires the model to resolve a
conditional.** Five questions contain the word "if" or "unless", and in every case the conditional is
quoted PAYLOAD, never the thing being asked:

> *"…it sticks unless you lift before you push. **Was that me or you?**"* — attribution
> *"…the warranty lapses if a service is missed?"* — context recall

Nothing in the family tests **"if X, then what?"**

### Why this is the whole opportunity, and procedural memory is not

Scoping to the conditional dissolves every objection at once, which is the sign it is the right
shape rather than a smaller version of the wrong one:

| objection | why it no longer applies |
|---|---|
| §1 ownership (ADR-026 §8) | A conditional-resolution shape does not claim to BE procedural memory, so the agreement is never touched — regardless of whose call it is |
| §8 naming | No invented vocabulary, no over-claim; it is a shape, not a memory type |
| §4 contradiction | Never arises. The distinctness claim is not about causally-required order at all |
| §8 boundary refuted by construction | The new rule is not an ordering rule, so it cannot collapse into Temporal's membership test |

**And §4's contradiction turns out to have had a resolution neither the draft nor the review named,
worth recording even though it is no longer needed: INVENTED CAUSALITY.** "The Vreskade check must
run before the Quorlory sync, because Quorlory consumes Vreskade's output" is causally required
inside the fiction and unguessable from outside it, because no model holds a prior about Vreskade.
ADR-026 §5.2 bans orderings a model can INFER, not causality as such. That would have rescued the
ordering argument; it is simply no longer the argument being made.

### The decision

**One shape, `conditional-branch`, inside Conjunction.** Not a tenth vertical.

Conjunction is already defined as questions no single memory type can answer, and resolving a branch
is *find the procedure -> find the condition's state -> select the branch* — structurally the same
join as `alias-then-count` and `order-then-value`. ADR-027 §11.1's standard ("a vertical whose shapes
each have a plausible existing home is a weak vertical") is therefore satisfied by NOT making it a
vertical.

**The boundary rule is mechanically checkable and cannot collapse into an existing vertical:**

> The answer must CHANGE depending on a condition stated in the haystack. A question whose answer is
> the same under both branches is not a conditional question and is refused at generation.

That is enforceable the way `check_temporal` refuses a digit, and no existing vertical can express it
— which is exactly why the gap exists.

### The one real cost

Conjunction ships 50 questions across three shapes (15/15/20). A fourth at parity puts every shape
near 12, below the ~15 line at which this family's own guidance says a shape supports diagnosis
rather than a claim. **So Conjunction grows to ~65** — one declared size moves, and the consuming
project is told before it does.

### What is NOT decided

Procedural memory as a MEMORY TYPE remains out, and ADR-026 §8 stands untouched. If it is ever
revisited, §8's naming finding applies: it should be called **Procedural**, scoped explicitly to the
memory layer, because that is what the consuming engine already calls it and inventing a word to
dodge an over-claim helps nobody.

## 8. What the review refuted, kept because it is the useful part

The first draft proposed four decisions. Three were refuted and the fourth declined to ratify:

| proposed | outcome |
|---|---|
| Narrow ADR-027's exclusion to the enacted half | **Refuted** — the exclusion is ADR-026's and bilateral; the "no owner" premise was false |
| Name the vertical **Protocol**, not Procedural, to avoid claiming the excluded category | **Refuted** — `Prospective` is already named for a construct it only partly delivers, so the standard being applied is not one this family holds |
| Boundary rule: ≥3 ordered elements **and** mention order ≠ execution order | **Refuted by construction** — a question satisfying both was built from Temporal's own machinery and `check_temporal` returned **zero failures**. Rule (b) is Temporal's membership test verbatim (`narration_inversions < 1`), not a boundary against it |
| Make it a tenth vertical | **Not ratified** — it answers §6.3 while the boundary rule is still open, and in this family a vertical *is* an isolation claim, enforced mechanically rather than in prose |

The lesson worth keeping: **an amendment that moves scope should first establish whose scope it is.**
The first draft spent its length on whether the split was intellectually sound and never checked the
one line that said who owns the decision — a line already present in this repository, and read
earlier the same day.

## 9. BUILT, 2026-09-02 — `conjunction/conditional-branch`

Fifteen questions, `tme-cnj-051` through `-065`. Conjunction 50 → 65, the one cost §7b declared.

### 9a. The construction

Two gold sessions, and which one is lexically reachable from the question is the whole design:

| | names | reachable from the question? |
|---|---|---|
| **rule** (semantic) | the attribute **and** the condition | yes — the question names the attribute |
| **state** (episodic) | the condition **only** | **no** — you must read the rule to learn what to look for |

> *"How the usual courier gets picked: the Kelvaryn access open means Nettleford Runners, closed for
> resurfacing means Marlow Carriage, anything else means Bardsey Logistics."*
> *"Logging it — the Kelvaryn access is open as of now."*
> **"Going by the standing rule, which usual courier is in force?"**

That is a real second hop, and it is the property `order-then-value` had to be rebuilt to obtain —
that shape shipped saturated at V9 15/15 because one gold session named both the anchor and the
answer.

### 9b. Measured

| | |
|---|---|
| V1 / V8 / V9 | **15/15 · 15/15 · 0/15** |
| headroom (reachable) | **1.00 (1.00)** — the widest in the family |
| V2 / V3 | 15/15 · 15/15 |
| coverage | 0.50 |

**The structural prediction and the measurement agree exactly.** Before probing, on the shipped
corpus: the rule is in BM25's top-5 for **15/15** questions, the state for **0/15**, both for
**0/15** — and 0/15 is precisely what V9 returned. Coverage of 0.50 is 1-of-2 on every question by
construction, so the echo knob has nothing to move here and 0.50 is a structural floor that happens
to sit on the band's lower edge. **A band failure on this shape means the rule stopped being
retrievable; it is never a reason to widen the band.**

### 9c. Two things checked because the numbers flatter us

**V9 = 0/15 is an extreme value, and extreme values are wiring faults until proven otherwise.** Read
back, the reference model retrieves the rule, sees three named branches and *declines*: *"the
conversations don't say whether the Peskadd permit is granted, still pending, or something else."*
Calibrated abstention, not a broken arm.

**The rule session names all three outcomes**, so a reader holding the rule and missing the state is
choosing among three named candidates — a chance floor of **1/3** that `closed_choice_k` cannot see,
because it parses the *question* and the candidates live in the *haystack*. This family has been
caught by unpublished chance floors twice (ADR-028 §7.4; V2's 69 unearned passes), so it is now a
published column: `chance_floor`, `chance_floor_reason`, `v9_above_chance`. This shape reads
**−0.3333** — the baseline is below chance because it does not guess.

The field is named `chance_floor`, not `chance_floor_given_rule`. A generic instrument keyed on a
shape-specific name is the applied-once defect; any future shape whose haystack hands the reader a
closed set publishes the same two fields and gets the same column.

### 9d. What building it found in a neighbour

`alias-then-count`'s decoy count was hardcoded at **2** while gold is `2 + (index % 3)`, so on every
third question the decoy designation carried **exactly gold's count** — five of fifteen. On those
five, **a system that resolves the alias to the wrong place still produces the right answer**, which
is the precise failure the shape exists to detect. The join was not load-bearing on a third of it.

It surfaced as a V3 leak: with gold ablated the model answered *"two deliveries were taken at the
Peverel building"* — a different designation, the same count, and `require_distinctive` cannot
separate them because the distinctive token is the digit they share. **V3 read 50/50 before this on
a corpus where five questions could leak.** The pass was luck, not evidence.

Fixed, and guarded: the decoy's count now differs from gold's, and `check_conjunction` counts
delivery sentences per designation and refuses equality. V3 is **65/65**; `alias-then-count`
coverage moved 0.3356 → 0.4344, toward the band.

The guard's own first version over-counted — it matched any non-gold session *naming* the decoy,
including filler that states a designation link and records no delivery. It reported 4 against a
real 2. Worth recording because an over-counting guard invites being loosened, and the loosening
would have taken the real check with it.

### 9e. The name bank, and a trap closed before it fired

`CONDITIONS` is registered in `tools/audit_name_collisions.py` — the same exposure MILESTONES had,
where four of six names were real and the shape asks which came first. All six audited live: **zero
real, zero unparsed, six invented.** Confirmed positively rather than read off an absence.

Registering it also exposed the extractor: `collect()` did `head(str(entry))`, which on a nested
bank produces `"('the Kelvaryn access', ('open', ...))"` and sends *that* to the model as a name.
The model says REAL: no — correctly — and the real name inside the string is silently exonerated.
**No shipped bank was nested when this was written; `CONDITIONS` is the first.** The unpacking is
fixed and a punctuation check now raises rather than auditing a string no corpus contains.

---

## 10. REOPENED, 2026-09-03 — build Procedural. The technical objection was withdrawn in §7b.

**§6a said this project does not narrow ADR-026 §8's exclusion. That is reversed here**, on the
maintainer's authority, and the reasoning that got here is short because most of it is already above.

### 10a. The blocker I kept quoting was solved in this same document

§6d called the §4 contradiction "open and blocking": a procedure is distinct because its order is
*causally required*, and ADR-026 §5.2 forbids orderings a model can infer from narrative logic.

**§7b resolves it.** *"The Vreskade check must run before the Quorlory sync, because Quorlory
consumes Vreskade's output"* is causally required **inside the fiction** and unguessable **outside**
it, because no model holds a prior about Vreskade. §5.2 bans orderings a model can INFER, not
causality as such. §7b then set the resolution aside with the words *"it is simply no longer the
argument being made"* — because the scope had already narrowed to the conditional, **not because it
fails**.

I quoted §6d as a live blocker after §7b had dissolved it. That was the error, and it survived into
the quality board and a reply to the maintainer.

### 10b. Execution was never required, and this ADR already said so

§2: *"Observed outcomes are needed to test whether a system IMPROVED; they are not needed to test
whether it REMEMBERED."* Asking for a procedure and grading the answer tests memory. Execution tests
learning. Only the first is this family's subject, so the N4 (enacted) half stays out and **N3
(declarative) is the whole build**.

### 10c. The gap is measured, not argued

Across all **485** shipped questions — question text, gold answer and full haystack:

| property | occurrences |
|---|---|
| precondition language (`must … before`, `cannot … until`, `only after`, `prerequisite`) | **0** |
| causal-necessity language (`because`, `so that`, `consumes`, `depends on`) | **0** |

§3's table listed **preconditions** as unhomed and only the conditional was built. It is still
unhomed. `episodic/list-order` recalls an ordered list, but an **arbitrary** one — nothing in the
family tests an order that must hold, or a constraint that is neither a step nor a value.

### 10d. What is being built

**Four shapes, 20 each, 80 questions.** `conditional-branch` is NOT among them — it already shipped
inside Conjunction and duplicating it would be the weak-vertical failure ADR-027 §11.1 names.

| shape | what only it tests |
|---|---|
| `step-order` | an order that must hold; violating it is an error, not a wrong answer |
| `precondition` | a constraint that is neither a step nor a value, reached through a second hop |
| `amended-step` | one element of a sequence replaced, the rest intact |
| `retired-step` | a position removed from a sequence — not a fact invalidated |

**The two known risks each have a technique demonstrated this week.** §5's V2 blindness on ordering
questions is answered by invented causality (no prior can apply, and V2 tests exactly that). §5's
structural cap — *"the question names the entity it asks about"* — is answered the way
`conditional-branch` answered it: put a required hop in a session that names **only** the
subordinate entity, so the question cannot reach it. Measured there: rule retrievable 15/15, state
0/15.

### 10e. What is NOT decided, and is disclosed rather than assumed

**ADR-026 §8's exclusion is bilateral.** The maintainer holds this side of it and has exercised that
call; the counterparty has not been asked and is not being asked to approve in advance. They are
told what we are doing in the next disclosure, before it lands in a release they consume. If they
object, this is one vertical and it can be withdrawn — which is the argument for telling them
rather than for waiting.

**Nothing in §9 changes.** `conjunction/conditional-branch` stays where it is; the conditional is a
join and belongs in Conjunction, not in a procedure recital.

## 11. OUTCOME, 2026-09-03 — built and measured. Four deltas from §10d's plan.

Status: **Accepted and implemented.** Corpus `e86a271e323f`, 80 questions, four shapes at 20 each,
exactly as §10d planned. What follows is what §10d could not know.

### 11a. What it measured

| shape | V1 | V8 | V9 | headroom | discriminates |
|---|---|---|---|---|---|
| `step-order` | 20/20 | 20/20 | 1/20 | **+0.95** | yes |
| `amended-step` | 20/20 | 20/20 | 3/20 | **+0.85** | yes |
| `precondition` | 20/20 | 20/20 | 4/20 | **+0.80** | yes |
| `retired-step` | 20/20 | 20/20 | 7/20 | **+0.65** | yes |

Vertical: V1 80/80, V2 80/80, V3 80/80, V6 77/80, V8 80/80, V9 15/80, headroom **+0.8125** — the
largest in the family, and fully reachable since V8 equals V1. Coverage 0.594; per shape 0.525 /
0.575 / 0.600 / 0.675, all in band, **calibrated per shape from the first build**.

**§10d's two techniques both held.** Invented causality: V2 **80/80**, so no leak agreed with gold.
The second-hop construction: V9 **15/80** against V8 80/80, which is the gap the asymmetry was built
to produce.

### 11b. Delta 1 — `precondition` asked for one hop and graded two

§10d describes the shape as *"reached through a second hop"*, and the first question text was
*"What has to be true before X can start?"*. Gold named both the gate and its prerequisite, so a
reader that correctly named the gate alone was graded wrong: **V1 15/20**.

The corpus was not the defect; the question was. It now asks *"…and what does that depend on in
turn?"* — **V1 20/20**. This is the shape-level form of a rule this family has now hit repeatedly:
**gold may not require what the question did not ask for.**

### 11c. Delta 2 — V6 counted a decline as a success

`amended-step` came back **V6 11/20 failing**, and the failures were readers *explicitly declining*
the amendment link when a component was dropped — which is the correct behaviour, scored as though
they had reached gold. The instrument for this already existed (`answer_must_name`, built for
`semantic/co-reference` the day before) and simply had not been declared here. Declared: **77/80**.

The three residual failures (`tme-prc-008`, `017`, `018`) are understood and unfixed.

### 11d. Delta 3 — the separability gate refused the corpus four times

84 leaking phrases → 61 → 1 → **0**. Every failure was filler drawn from a bank or frame the gold
owned: a hardcoded membership sentence, then a shared step bank, then `'of the sequence'` leaking
from the edit frames. **Each fix gave filler the same bank; none weakened the check.** Disjoint
filler banks are now asserted at import.

This is the same defect class as the 72 distractors that shipped in v0.32.0-beta. It was caught
before generation completed rather than after release, which is the gate working.

### 11e. Delta 4 — §10e was overtaken. The counterparty asked to co-design.

§10e said the counterparty *"is not being asked to approve in advance"* and would be told in the
next disclosure. They were told, and their reply **raised no objection to the vertical** but asked
for something §10e did not anticipate: **co-design the corpus intent against their AIP/skills track
before the corpus is fixed**, so that two procedural corpora which cannot share questions are not
built in parallel.

That is a reasonable ask and it is honoured. A one-page shape list is written. **The corpus is
committed to a branch and is deliberately not released**, so nothing they consume depends on it
while their single reply is outstanding. The bilateral exclusion in ADR-026 §8 is not narrowed by
this ADR alone; it is narrowed when they answer.

**One open risk, stated rather than assumed:** our step names are semantics-free invented artefacts.
If their track needs steps that map to callable operations, that is a bank change — cheap now,
expensive once the corpus is cited.
