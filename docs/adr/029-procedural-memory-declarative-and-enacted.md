# ADR-029: Procedural memory — split the excluded category in two, and reopen only half of it

- **Status:** **Proposed — a decision is REQUESTED, not recorded.** ADR-027 excluded procedural
  memory *permanently*; nothing below overturns that on its own authority. §7 is the decision this
  asks for and §6 the three things that cannot be designed until it is answered.
- **Date:** 2026-08-31
- **Amends:** [ADR-027](027-typedmemeval-semantic-temporal-bitemporal.md) §1, which reads:
  *"Procedural stays out permanently: it needs tools, a live agent loop and observed outcomes, so it
  cannot be a static corpus."*

---

## 1. What this amendment does and does not claim

ADR-027's reasoning is correct **for half of what "procedural memory" names, and the half it is
correct about is not the half a memory benchmark most needs.**

- **Enacted procedural memory** — whether a system gets *better* at doing something, expressed
  through performance. ADR-027 is right that this needs tools and observed outcomes.
- **Declarative memory of a procedure** — whether a system retains and correctly reproduces an
  ordered, conditional, revisable procedure it was told about. This is a static corpus, and nothing
  in the family tests it.

**The naming is the load-bearing part of this ADR, not a footnote.** Strict procedural memory is
knowing *how*, largely non-verbalizable. Asking "what are the steps?" tests knowing *that*, which is
closer to semantic memory with procedural content. If a vertical is built it must be named so that
it does not claim the category ADR-027 excluded. Quietly redefining an excluded category in order to
report coverage of it would be precisely the over-claim this family exists to refuse, and it would
be the most damaging kind, because it would be true of the label and false of the thing.

## 2. The gap, stated as structure rather than as a category

A procedure has properties no current vertical measures, and each is testable in a static corpus:

| property | why no existing vertical covers it |
|---|---|
| **Ordered steps with dependencies** | Temporal orders *occurrences*; a wrong order there is a wrong answer. In a procedure, violating order is an **error** — the steps are causally required, not merely sequenced. |
| **Conditional branches** | *"If the check fails, do X instead."* **Nothing in the family has a conditional.** |
| **Partial revision** | *"Step 3 changed, the rest stands."* Bitemporal corrects a whole fact; this corrects **one element of a structure** and must leave the rest intact. |
| **Preconditions** | *"Never on a Friday."* A constraint that is not a step and not a value. |
| **Retired steps** | *"We stopped doing the manual backup."* Forgetting invalidates facts, not positions in a sequence. |

That is a distinct construct. Whether it is distinct enough to be a tenth vertical is §6.3.

## 3. The dominant risk, and why it is survivable

**Procedures are the most guessable content this family would ever hold.** Asked *"how do we deploy
the service?"*, a reference model writes a plausible runbook from priors with no context at all —
the exact failure V2 exists to refuse. **V2 currently passes 100% across all nine verticals**
(470/470), so that is the bar, and a naive procedural corpus would fail it badly.

**The mitigation is proven in this repository.** Temporal faces the same problem — event orderings
that world knowledge constrains — and solves it with invented referents whose ordering is *stated
rather than inferable*. `MILESTONES` are verified non-referential, and the relation between them is
carried by the corpus, not by plausibility. A procedural corpus would do the same: invented steps
whose required order is arbitrary and stated.

**The second cost is the judge.** An ordered multi-step answer needs per-step and per-order credit
rather than a yes/no verdict. That is real work, but it is precedented: the structured judge
protocol built for LongMemEval already replaced free-text parsing for the same reason.

## 4. Enacted procedural is cheaper than ADR-027 assumed

ADR-027 excluded the enacted half because it "needs tools, a live agent loop and observed outcomes."
Reading the RedTeam harness, the expensive part of that is not required:

- `IToolCapableAgent` already hands an agent tool schemas and returns a trace of emitted calls.
- `CanaryTool.Execute` is **optional**, and `EvidenceFidelity.IntentToAct` already distinguishes a
  call the model *emitted* from one that *executed*.

So an agent can be handed the tools, asked to carry out a procedure it learned sessions earlier, and
scored on the **emitted call sequence** — with nothing running. No sandbox, no side effects, no state
threading. **Observed outcomes are not needed to test whether the procedure was remembered
correctly**; they are only needed to test whether the system got *better*, which is a different
claim and stays out.

What is genuinely missing is semantics, not mechanism, and §6.2 records it.

## 5. What this ADR proposes

**5a.** ADR-027's exclusion is **narrowed, not lifted**: it continues to apply in full to enacted
procedural memory as skill acquisition — whether a system improves with practice — which remains out
of scope for a static corpus family.

**5b.** The declarative half is **admissible in principle**, under a name that does not claim the
excluded category, subject to §6.

**5c.** Nothing is scheduled. Both halves sit after the existing nine verticals are healthy, for a
reason beyond effort: adding a tenth corpus while three of nine carry open defects spreads the work
thin, and the family-wide padding fix would then touch ten corpora rather than nine.

## 6. What cannot be designed until §7 is answered

**6.1 — The name, and therefore the claim.** Until it is settled whether this is "Procedural
(declarative)", "Protocol", "Runbook", or a shape inside an existing vertical, every downstream
choice is unanchored. This is the gate.

**6.2 — There is no mechanical boundary rule, and this family does not accept prose ones.** Temporal
holds its line against Arithmetic in code: `check_temporal` refuses any answer containing a digit.
Bitemporal refuses a pair whose two clocks give the same answer. A procedural vertical needs an
equivalent against **Conjunction** (which already does multi-hop joins) and **Episodic** (which
already orders mentions), and none has been designed. A candidate — an answer must be an ordered set
of at least three elements containing at least one conditional — is a starting point, not a decision.

**6.3 — Whether it is a tenth vertical at all.** That changes the declared family size, the
consuming project's expectations, and every table that says "nine".

**6.4 — For the enacted half only:** `ForbiddenCategory` is a *required* field on `CanaryTool`,
because every tool in that harness is attacker-desirable by construction. Procedural asks the
inverse — the right tools in the right order — so it needs an `ExpectedStep` notion and a sequence
evaluator (`ToolInvocationEvaluator` detects invocation, not order). It would also be **the first
thing to marry the RedTeam and Memory assemblies**, which is an architectural decision rather than a
feature.

## 7. The decision requested

**Does ADR-027's permanent exclusion stand as written, or is it narrowed to the enacted half?**

- If it **stands**, procedural memory has no owner anywhere in the product, and that should be
  recorded as an accepted gap rather than left implicit.
- If it is **narrowed**, §6.1 through §6.3 become the design work, and §6.4 becomes a separate
  question about where enacted procedural evaluation lives — which is a product decision, not a
  TypedMemEval one.

Either answer is defensible. What is not defensible is the current state, where the exclusion reads
as permanent, the enacted half is unowned, and the declarative half was never separately considered.

## 8. Provenance

This came out of a maintainer's question — whether procedural memory could be tested as *"how would
you do this"* rather than *"do this"* — which is exactly the split §1 draws, and which ADR-027 did
not consider because it evaluated "procedural memory" as one thing. The claim in §4 was made once
without inspecting the harness and corrected after reading it; the correction is what moved the
enacted half from prohibitive to merely unscheduled.
