# ADR-029: Build the conditional, not procedural memory

- **Status:** **Accepted**, scoped to ONE shape — `conjunction/conditional-branch`. Procedural memory
  as a memory type stays out and ADR-026 §8 is untouched.
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
