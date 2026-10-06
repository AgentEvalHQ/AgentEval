# TypedMemEval

TypedMemEval is an AgentEval-authored benchmark family that measures memory mechanisms in
isolation, one vertical per mechanism. It started with five — **prospective memory**, **episodic
structure**, **arithmetic over memory**, **working-memory distance**, and **forgetting** — and now
ships ten, adding **bitemporal**, **temporal**, **semantic**, **conjunction** and **procedural**
(see [The ten verticals](#the-ten-verticals)). Each vertical is its own corpus, its own question
types, and its own validity rules.

> **Citation rule.** Cite results as **"TypedMemEval-\<Vertical\> v5 (AgentEval)"**. TypedMemEval
> results are **not** LongMemEval results and must never be presented as, summed with, or averaged
> with LongMemEval numbers. The twelve Prospective questions seeded from the time-grounded probe
> exist in both `agenteval-timegrounded-v1` and TypedMemEval-Prospective v5; a report that runs both
> must not double-count them.

TypedMemEval reuses LongMemEval's *file format* and AgentEval's LongMemEval harness machinery. That
is an engineering fact, not an identity claim: a benchmark's identity is its name, corpus, question
set, and scoring, and all four here are the family's own.

## Why it exists

LongMemEval-S cannot measure these mechanisms, because the gap is in its dataset and its dataset is
its identity:

| Mechanism | What LongMemEval-S has |
|---|---|
| Prospective memory | No questions at all |
| Episodic structure | 56 assistant-stated questions of 500 (a 30-question run draws ~4); nothing on list-order or speaker attribution |
| Arithmetic over memory | Derived answers exist but are never isolated from retrieval difficulty |
| Working-memory distance | No such question type |
| Forgetting | No such question type |

It is also **saturated** for a competent retrieval stack — realised gold coverage of 0.965–0.980 —
and at that coverage every retrieval-side mechanism is invisible.

## Running a vertical

```csharp
using AgentEval.Memory.External.TypedMemEval;

var runner = new TypedMemEvalRunner(judgeChatClient);

var result = await runner.RunAsync(agent, TypedMemEvalVertical.Forgetting);

var typed = result.TypedOutcomes!;
Console.WriteLine($"correct {typed.Outcomes.Correct}/{typed.Outcomes.N}");
Console.WriteLine($"wrong {typed.Outcomes.Wrong}, abstained {typed.Outcomes.Abstained}, " +
                  $"missed {typed.Outcomes.Missed}");
Console.WriteLine($"stale recall {typed.StaleRecall}, over-forgetting {typed.OverForgetting}");
```

The corpora are embedded in the package. There is no dataset path and no download — and no path
knob either, deliberately, so "which corpus produced this number" is always answered by the
identifier and hash in `result.Provenance` rather than by a path that may since have moved.

### From the CLI

```powershell
# Initialize the canonical store once.
agenteval init-workspace

agenteval bench typedmemeval --vertical forgetting --subject MyAgent
```

`--vertical` and `--subject` are both required: the verticals measure different mechanisms, so
there is no default to fall back to. The CLI binding needs a real model and has no stub fallback,
because the judge round-trip is the correctness signal. It uses whichever provider
`AI_INFERENCE_PROVIDER` selects — Azure OpenAI, Bitdeer, OpenAI, Azure AI Foundry, or any
OpenAI-compatible endpoint — and auto-detects when the selector is unset, so the
`AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_DEPLOYMENT` trio alone still works.
Each provider's variables are listed in
[CLI Reference — `AI_INFERENCE_PROVIDER`](../../cli.md#ai_inference_provider--which-provider-the-cli-talks-to).
When no provider resolves, the command prints why (for example, which variables are missing) and
exits 3 (`RuntimeError`).

One client plays both roles: the same model answers the questions and judges the answers, and the
`AZURE_OPENAI_JUDGE_*` override in the CLI reference is not read by this command. Programmatic
callers can use any `IChatClient` for the judge and any `IEvaluableAgent` for the system under test.

It prints the typed vector with every denominator — the run totals, the per-shape strata, coverage
against the corpus's calibrated floor, and the attribution counts — plus the vertical's own extras
(pair consistency, the distance curve, the three forgetting sub-counts). It gates on nothing: the
family publishes no pass threshold, so the run exits `0` when it measured anything and `11` when it
measured nothing, and its run summary is recorded as `WARN` (indeterminate) rather than PASS/FAIL.

The CLI writes a canonical manifest, summary, and `report-native.json`. The native report preserves
questions, gold answers, agent responses, and the judge/evidence detail the options allow — treat it
as sensitive data: restrict access, retention, and publication.

### Typed outcomes, never one percentage

> **Thirteen of the 36 shapes carry fewer than 15 questions, and their figures support diagnosis
> rather than claims.** ADR-026 says this about the family's cell sizes and the published tables never
> repeated it, so a six-question rate has been reading like a measurement. The threshold is a
> judgement — 15 is where a single question stops moving a rate by more than ~7 points — but the
> shapes below it are a fact:
>
> | vertical | shapes under 15 questions |
> |---|---|
> | `prospective` | `seed-carry-over` (12), `due-later-reminder` (14), `expiring-validity` (14), `not-yet-true` (14) |
> | `workingmemory` | all five distance rungs (12 each) |
> | `arithmetic` | `delta` (10), `duration` (12), `count` (14), `sum` (14) |
>
> On the smallest shape, `arithmetic/delta`, one question is 0.10 of the rate; on a 14-question shape
> it is 0.071. `not-yet-true` and `expiring-validity` show how little separates a verdict: each
> publishes a headroom of **0.1429 — two questions** — and both sit just under the 0.15 floor at
> which the probe runner counts a shape as able to rank two systems. Quote these shapes to diagnose
> where a system struggles; do not quote them as a measured capability, and do not rank two systems
> on a difference of one or two items.

Every result reports a vector, per vertical and per shape, always with its `n`:

| Outcome | Meaning |
|---|---|
| `Correct` | Matches gold. For an invalidated fact, that means saying it is no longer the case. |
| `Wrong` | Commits to an incorrect value. |
| `Abstained` | Declines to commit — "I don't know", "I have no record". |
| `Missed` | Confidently asserts nothing is there when gold says otherwise. |
| `Premature` | Prospective before-arms: fires early, asserting the not-yet-true thing as true. |
| `Inconclusive` | The judge returned no verdict. **Not a measurement.** |
| `Unrun` | Skipped with a stated reason. **Not a measurement.** |

The last two exist as their own members so a judge outage or a skipped question can never be
quietly absorbed into `Wrong` and make a system look worse than the evidence supports.

`ExternalBenchmarkResult.OverallAccuracy` stays populated on family runs so generic tooling keeps
working. It is compatibility surface, not a TypedMemEval score.

### Evidence attribution

A second, orthogonal axis, computed from the evidence envelope your adapter already supplies
(`EvidenceCaptureMode.References` and the `agenteval.question_evidence.v1` contract):

- `EvidencePresent` — every gold component was referenced in what reached the answer model.
- `EvidenceAbsent` — at least one was not.
- `Unobserved` — no telemetry was supplied. **Never guessed**, and reported as its own share.

It is named for what it is: **reference-level presence, necessary but not sufficient**. A gold
session id in the answer context does not prove the gold value survived your store's summarization.
Exactly one causal reading is safe — `Wrong` with `EvidenceAbsent` *is* a retrieval-side failure.
The mirror reading (`Missed` with `EvidencePresent` means a synthesis failure) is an inference, not
a fact, because a compression loss inside the store looks identical from here.

## The ten verticals

### Prospective (72 questions)

Due-later reminders, expiring validity, not-yet-true assertions, **due-windows** — plus the twelve
time-grounded probe questions carried in as its seed. Runs under `TimestampsOnly` grounding: the
conversations contain **no absolute date and no four-digit year**, so every temporal expression is
relative and resolving it requires the session's own timestamp.

`due-window` (18 questions) was added in 0.31.0-beta because the vertical could be answered with
firing semantics switched off. Every earlier shape **names the thing** being asked about, which
hands a similarity retriever the words of the session it needs while the harness supplies "today"
and the corpus supplies the due date — in-context arithmetic no memory feature is needed for. A
due-window names nothing: several reminders whose only distinguishing property is *when* each falls
due, and an answer that is a **set** whose membership changes with the as-of instant. It gave the
family its first real interference cost (0.00 → 0.28).

Sixty of the seventy-two are **30 before/after pairs**: one haystack asked twice, differing only in
when it was asked, with gold flipping between the arms. Pairs are the vertical's teeth — a system
that answers the after-arm correctly but also fires on the before-arm is *premature*, which no
single question can show.

The agent must implement `ITimestampedHistoryInjectableAgent`; a run refuses before its first
provider call otherwise. There is no text fallback, because the dates a fallback would use are the
ones this vertical exists to take away. The one way round it is to set `TemporalGrounding` to
`None` yourself, which removes the vertical's premise along with the guard — both shipped option
sets (`ProspectiveProbeOptions`, `ProspectiveControlOptions`) leave it alone, and so should you.

```csharp
// The probe and its control: same corpus, same hash, two option sets.
var probe = await runner.RunAsync(agent, TypedMemEvalVertical.Prospective,
                                  TypedMemEvalCorpus.ProspectiveProbeOptions);
var control = await runner.RunAsync(agent, TypedMemEvalVertical.Prospective,
                                    TypedMemEvalCorpus.ProspectiveControlOptions);
```

Equal scores mean the system honours the timestamps it was given. A drop under the probe is the
share of its temporal score that was coming from dates printed in the prompt.

Watch `PairConsistency.TimeBlindPattern`. It counts the pairs whose two arms are consistent with one
fixed answer given to both: correct-then-missed (always "not yet") or premature-then-correct (always
"it happened"). Gold flips between the arms by construction, so one unchanging answer earns two
different labels rather than the same outcome twice. That pattern is the signature of a
system that never received the query time — or ignored it and read a wall clock instead. It is a
count over pairs, not a verdict: one pair can show the pattern by chance.

### Episodic (50 questions)

Memory of the conversation *as an event*: 20 assistant-stated answers (the user never states them),
15 list-order questions, 15 speaker-attribution questions.

**Speaker attribution, as shipped.** The shape has three arms — `me`, `you` and `both of us`, five
questions each — so its chance floor is 1/3. A question does not quote the claim it asks about. It
names an event the claim led to ("the afternoon I gave up on the website and walked down there in
person"), so a system has to find the session describing that event, connect it to the session where
the claim was made, and read which role made it. Gold is two sessions per question, three on the
`both of us` arm. All fifteen questions close with the same words ("whose earlier point were we
going on? Me or you or both of us?"); that frame carries nothing, because the three answers are
balanced across it.

Shipped probe records: V1 14/15, V8 13/15, V9 4/15 — a published headroom of 0.67, of which 0.60 is
reachable. V9 sits below the 1/3 chance floor (`v9_above_chance` −0.07), so compare systems on how
far they clear 1/3, not on the raw rate.

Earlier revisions quoted the claim in the question, which handed a lexical retriever the gold
session; at 0.34.0-beta BM25 top-5 answered 15/15, the headroom was −0.0667, and the shape could not
rank two systems. ADR-026 §18 and ADR-028 §18 record that history.

List-order is scored **conditionally on coverage**: pairwise-order accuracy over the items the
answer actually mentions, because a budget-limited system may only have seen some of them and
grading it on items it never saw would measure the budget rather than the ordering.

### Arithmetic (50 questions)

Counts, sums, deltas, durations. Every question records its **gold derivation** — inputs with
session indices, operation, value, unit — so the judge scores the arithmetic rather than the
phrasing, and a failure is attributable: "wrong sum, missing exactly the un-retrieved input" is
visible because inputs are tracked individually.

Inputs are spread one per session (`G` ∈ 3..6), so a missed input does not degrade the answer, it
*wrongs* it. Duration answers derive from session timestamps; under a timestamp-free injection mode
those twelve questions are reported **unrun with a reason**, never counted as failures.

### WorkingMemory (60 questions)

Twelve fact families × five distances (8, 15, 25, 40, 60 intervening sessions). The ladder went
to five rungs in v4 because two of the old four could not fail: at `K_ref` = 5, a haystack of 2
or 6 sessions is one BM25 cannot miss in, so half the vertical sat in a structurally unfailable
band and the ladder graded at three levels rather than four. `H > K_ref` turned out to be
necessary and not sufficient — H = 6 still saturates — so the bottom rung starts where
measurement showed grading actually begins. Every cell is an
**independent question with its own haystack** — probing one stored fact at increasing distances
would let each probe rehearse the memory, so later distances would measure refreshed memory rather
than aged memory.

Results are an outcome × distance curve, never an aggregate: averaging over a distance ladder
destroys the only thing the vertical measures.

Two construct decisions are stated rather than hidden. The fact is pinned to the first session by
design, so distance is deliberately confounded with absolute position and recency — that composite
("how far back the memory sits") *is* the construct. And inter-session spacing is constant across
every cell, so distance-in-sessions and distance-in-time cannot vary independently.

### Forgetting (50 questions)

Twenty invalidated facts, fifteen still-valid controls, fifteen never-known probes. The gold answer
to an invalidated fact is "no longer valid" — a different state from never-known abstention, and the
judge is required to hold the three apart.

Each failure direction has its own count, so the discrimination claim is checkable from the
published numbers:

- **stale recall** — asserting the superseded value as current. The dangerous-error class: a
  fabrication-shaped failure, not an ordinary miss.
- **over-forgetting** — claiming a still-valid fact is no longer known. Caught by the controls,
  which exist because invalidated-only questions would reward a system that forgets everything.
- **mis-attributed forgetting** — claiming to have forgotten something never stated. Confabulating
  a memory event.

An answer that recalls the old value *while marking it superseded* — "it was a Honda, but you sold
it" — is **Correct**. That is ideal memory, not a mistake.

### Bitemporal (60 questions)

Thirty-six belief-at-instant, twenty-four correction-depth. The vertical separates **when something
was true** from **when the record learned it** — a question asks what the file showed *as of* one
date about a state holding *at* another, so a store that keeps only the latest value cannot answer
it at all, and one that keeps history but not the order of corrections answers it wrongly.

Correction-depth stacks revisions: a fact is recorded, corrected, and corrected again, and the
question picks an as-of instant between them. The two dials are independent on purpose — depth
tests whether the store retains superseded values, and the as-of instant tests whether it can be
asked about a past belief rather than a current one.

### Temporal (50 questions)

Twenty occurrence-order, fifteen interval-position, fifteen recency. Events are never dated; they
are related to each other in a chain ("the X survey came after the Y rewiring"), so ordering them
requires following the relations rather than reading a timestamp. Like Prospective, the
conversations carry **no absolute date and no four-digit year**.

`recency` was reshaped in 0.31.0-beta. It had asked about three *adjacent* events, which made the
answer one transitive step over two sessions that named those events outright — and it scored
**15/15 at V1, V8 and V9**, the only shape in the family on which no two systems could be told
apart. It now asks about events **spanning** the chain, so every link between them has to be
followed. V9 on `recency` was 15/15 before the reshape and is **8/15** in the shipped corpus; the
vertical's headroom was 0.16 before the reshape and is **0.62** in the shipped corpus, which has
changed in other ways since.

Milestone names are **verified non-referential** (`tools/audit_name_collisions.py`). An earlier bank
was built from real British place-names, and the reference model answered *"which came first"* from
world knowledge about a Glasgow shipbuilder with no haystack at all.

### Semantic (50 questions)

Twenty current-value, fifteen co-reference, fifteen source-attribution. These ask the store to
**resolve** rather than recall: the current value after a chain of replacements, a fact stated under
a different designation than the question uses, or which earlier conversation a belief came from.

Source-attribution is the awkward one by design — the answer is not a value in the store but a
property of *where the value came from*, which a system that flattens history into a current-state
snapshot cannot recover even when it holds the right value.

### Conjunction (65 questions)

Twenty value-then-count, fifteen alias-then-count, fifteen order-then-value, fifteen
conditional-branch. Each question needs a fact of one memory type resolved **and** an operation of
another type applied to it. Retrieving either half is necessary and neither is sufficient, so **a
stack strong on one type and weak on the other scores like a stack weak on both** — which is exactly
what a per-type score cannot show.

`conditional-branch` joins a standing rule to a specific state: one session states a rule with three
outcomes, another records the state that selects one of them, and both are required. The rule
session names all three outcomes, so a reader that finds the rule and misses the state still reaches
gold one time in three.

**Read the shapes, not the mean.** In the shipped probe records the four shapes publish headroom from
0.47 (`order-then-value`, of which only 0.27 is reachable, because a reader holding the whole
haystack misses 3 of 15) to 1.00 (`conditional-branch`, whose BM25 arm scores 0 of 15 against that
1/3 floor). A vertical mean of 0.77 hides both.

### Procedural (80 questions)

Twenty each of `step-order`, `precondition`, `amended-step`, `retired-step`. The vertical asks
whether a system **remembers a procedure it was told across sessions** — the steps, the order they
must run in, what has to be true before it starts, and which steps were later amended or retired. It
does not ask whether the system can *execute* the procedure or whether it *improves* at it; that
needs observed outcomes over repeated trials and is a different claim, argued in ADR-029 §2.

Two properties are unique to this vertical. `step-order` is the family's only **order that must
hold** — violating it is an error rather than a wrong answer — and `precondition` is its only
constraint that is **neither a step nor a value**. `retired-step` is deliberately distinct from
`forgetting/invalidated`: there a value is superseded by another value, here a position leaves the
sequence and nothing takes its place, so a store that keeps procedures as opaque blobs keeps the
dead step alive.

Every question needs **two hops by construction**. Exactly one gold session names the procedure — the
membership list, and it states the steps in an order that is never the answer. The dependencies, the
sub-precondition, the amendment and the retirement name step or condition *pairs* and never the
procedure, so a retriever working from the question's wording reaches the first and not the second.
That asymmetry is enforced at generation and is why the vertical carries the family's largest
headroom (**+0.80**) with all four shapes discriminating.

**The second hop is enforced by competition, not by declaration.** Stating the dependencies as
adjacent pairs of a chain is not enough on its own: their transitive closure *is* the gold order, so
a reader holding them can emit the answer without ever learning which procedure they belong to. Each
`step-order` haystack therefore carries **two complete rival chains** over steps the question does
not own, and each `amended-step` and `retired-step` haystack carries two rival edits, so losing the
membership session leaves a reader choosing among three candidates rather than reading off the only
one. The generator refuses a corpus without them. This is what `gold_components_load_bearing: true`
means here — a measured property, not a claim, and V6 reports it at **70/72**.

The cost is paid in the retrieval proxy: `step-order` realises **0.40** coverage against a 0.50 band
floor, because seven mandatory competitors sit against G=4 gold at `K_ref` = 5. It is declared
out-of-band rather than tuned back in, on ADR-028's rule that a shape is accepted on measured
discrimination and calibrated on the proxy — removing the rivals would trade the construct for the
number.

## Coverage: what the corpora guarantee, and what they don't

A saturated corpus cannot see retrieval mechanisms. Two mechanisms produce non-saturation, and the
family is precise about which applies where.

**Structural dispersion.** Gold spread across `G` sessions caps coverage at `min(1, K/G)` for a
budget of `K`. With the declared reference budget `K_ref = 5`, a structural ceiling below 1.0 exists
**only where `G > 5`** — Arithmetic's high-dispersion questions, Episodic's longest list-order
questions and Conjunction's widest joins. For every `G ≤ 5` question — which is every question in
the other seven verticals — the ceiling is exactly 1.0, and presenting that as a band would be
numerology.

**Calibrated competition.** Everywhere, the haystack must make gold *hard to find*, not merely legal
to miss. No ceiling formula shows that; only a measurement does. So each corpus passes a
**calibration gate** before it freezes: a deterministic BM25 retriever at `K_ref` must realise mean
gold coverage inside **0.5–0.9**, and the generator iterates until it does. The realised value, the
per-question distribution, the iteration count, and the tool version are stamped into the corpus
metadata sidecar.

BM25 is explicitly a **floor proxy** — a stronger retriever will exceed it. That is what the runtime
echo is for: `TypedOutcomes.Coverage` reports what your system realised next to
`CalibratedFloorMean`, so a system below the lexical floor is retrieving worse than word matching.

Shipped calibration (BM25 @ K_ref = 5):

| Vertical | n | Mean realised coverage | `G` distribution |
|---|---|---|---|
| Prospective | 72 | 0.597 | 1 (×53), 2 (×16), 3 (×2), 4 (×1) |
| Episodic | 50 | 0.597 | 1 (×20), 2 (×10), 3 (×5), 4 (×4), 5 (×4), 6 (×3), 7 (×4) |
| Arithmetic | 50 | 0.770 | 3 (×11), 4 (×17), 5 (×8), 6 (×14) |
| WorkingMemory | 60 | 0.600 | 1 (×60) |
| Forgetting | 50 | 0.614 | 0 (×15), 2 (×35) |
| Bitemporal | 60 | 0.617 | 1 (×60) |
| Temporal | 50 | 0.704 | 2 (×15), 3 (×11), 4 (×12), 5 (×12) |
| Semantic | 50 | 0.657 | 1 (×15), 2 (×15), 3 (×10), 4 (×5), 5 (×5) |
| Conjunction | 65 | 0.610 | 2 (×15), 3 (×5), 4 (×12), 5 (×20), 6 (×7), 8 (×6) |
| Procedural | 80 | 0.575 | 2 (×60), 4 (×20) |

Forgetting's two coverage figures are the same distinction the runtime report draws. Fifteen of its
fifty questions are never-known probes with no gold at all, and a question with nothing to retrieve
scores 1.0 vacuously — it cannot miss what was never there. The headline mean therefore mixes
measurement with definition, so the gold-bearing figure travels beside it, and the floor a run is
compared against (`Coverage.CalibratedFloorMean`) is computed over gold-bearing questions only. A
floor inflated by vacuous ones would flatter every system by the share of no-gold questions.

## Difficulty bands

A question whose shape has a difficulty dial carries `difficulty` and `difficulty_dial` in its
`typedmemeval` block. The band is derived from **memory dials only** — dispersion, distance,
interference, discrimination — never from answer-step trickiness, which would confound the answer
model with the memory system. The table is read from the shipped corpus files:

| Vertical | dial | what varies | bands | banded | validated? |
|---|---|---|---|---|---|
| WorkingMemory | `distance` | 8 / 15 / 25 / 40 / 60 intervening sessions | 1–5 | 60/60 | no |
| Arithmetic | `dispersion` | 3–6 gold sessions (`G`) | 2–5 | 50/50 | no |
| Episodic | `dispersion` | list length 4–7 | 2–5 | 15/50 | no |
| Prospective | `distance` | 12.5–132 days (`displacement_days`) | 1–5 | 60/72 | no |
| Forgetting | `discrimination` | 4–15 sessions between statement and invalidation | 1–5 | 20/50 | no |
| Bitemporal | `correction-latency` | 1–12 sessions (`latency_sessions`) | 1–5 | 60/60 | no |
| Temporal | `narration-disorder` | 1–13 narration inversions | 1–5 | 50/50 | no |
| Conjunction | `branch` | which of three branches the state selects | 1–3 | 15/65 | no |

**Not every question carries a band.** A dial only exists where the shape has one: Episodic's
list length lives in its 15 list-order questions, Forgetting's gap in its 20 invalidated ones,
Prospective's displacement in its 60 paired arms, and Conjunction's branch in its 15
conditional-branch questions. Semantic and Procedural carry no band. The unbanded remainder is not
"difficulty 3" — it is unbanded.

**No band is validated.** Every banded question in the shipped corpora carries
`difficulty_validated: false`. "Validated" would mean the reference retriever's coverage slopes down
across the bands once the calibration scaffolding is stripped and the structural ceiling
`min(1, K/G)` is accounted for; the note under [Validity rules](#validity-rules) and ADR-026 §20
explain why no vertical passes that test. An earlier version of this table marked WorkingMemory,
Arithmetic and Episodic as validated under a rule that did not apply those two corrections.

Read a band as a description of how the corpus was built, not as evidence that those questions are
harder. The bands are kept rather than dropped because dropping them would leave the family implying
that memory difficulty is only ever lexical, which is the opposite of what it exists to measure.

**Per-band `n` is 2–17.** These are diagnostics, never claims: the family's n ≥ 30 floor for a
citable figure is per *vertical*, and no band comes close to it. Report bands to locate where a
system degrades, and report the vertical when you quote a number.

## Validity rules

Written before generation. **V4, V5 and V7 are re-measured in CI over the shipped bytes**, along
with the declared `H`, `G` and ceiling table. **V1, V2, V3 and V6 need a reference model**, so they
run at authoring time and CI checks only that their records exist and name the corpus hash that
shipped — which catches a stale record, not a wrong one. The distinction matters: a green build
means those four were measured against *this* corpus, not that they were measured today.

| Rule | What it requires |
|---|---|
| **V1** Oracle answerability | Every question, given only its gold sessions, must be answered correctly by a stated reference model. For pairs, both arms must be answerable *and* the answers must differ. |
| **V2** Non-inferability | With zero context, 10 samples; the question is rejected if 2 or more produce the gold answer. |
| **V3** Distractor plausibility | Given only the *non-gold* sessions, the model must **not** produce the gold answer — the dual of V1, and the only real defence against a distractor that accidentally contains the answer. |
| **V4** No absolute dates | For time-dependent verticals, no four-digit year and no absolute date in any message content. |
| **V5** Gold derived, not typed | Generators derive every gold answer from the sessions they emitted. |
| **V6** Component non-redundancy | Wherever a question DECLARES its gold components load-bearing — a per-question, and where a shape mixes the two kinds a per-component, declaration — ablating one must stop the model producing the gold. |

Shipped probe records (reference deployment `gpt-5.5`, per-question outcomes in each corpus's
`.meta.json`). Dashes are not-applicable rather than skipped, but for different reasons per column, and the
difference matters. Pair-flip needs pairs, which only Prospective, Forgetting and Bitemporal have.
V6 runs only where a corpus declares gold components load-bearing — Arithmetic, Forgetting,
Temporal, Semantic, Conjunction and Procedural in the shipped corpora — not wherever gold has more
than one component: Episodic list-order has G = 4–7 and carries no such declaration. V1 and V2 do
not apply to a never-known probe, whose gold is itself an abstention.

> **These records will not be re-measured.** As of 2026-10-02 TypedMemEval is frozen at its current
> corpus: the reference deployment the V1, V2, V3, V6, V8 and V9 arms ran against has been retired,
> and no corpus change is planned until a new reference model is chosen. The generators and the
> probe runner stay in the repository, but re-running the probes against a different model would
> produce records for that model, not a refresh of these.

| Vertical | V1 oracle | V1 pair-flip | V2 non-inferability | V3 gold-ablated | V6 leave-one-out | V8 full-haystack | V9 BM25 top-K | Retrieval headroom |
|---|---|---|---|---|---|---|---|---|
| Prospective | 71/72 | 29/30 | 72/72 | 35/35 | — | 70/72 | 39/72 | +0.44 |
| Episodic | 49/50 | — | 50/50 | 50/50 | — | 48/50 | 20/50 | +0.58 |
| Arithmetic | 50/50 | — | 50/50 | 49/50 | 49/50 | 50/50 | 21/50 | +0.58 |
| WorkingMemory | 60/60 | — | 60/60 | 60/60 | — | 60/60 | 37/60 | +0.38 |
| Forgetting | 34/35 | 14/15 | 35/35 | 35/35 | 20/20 | 35/35 | 22/35 | +0.34 |
| Bitemporal | 60/60 | 30/30 | 60/60 | 60/60 | — | 57/60 | 35/60 | +0.42 |
| Temporal | 50/50 | — | 50/50 | 30/30 | 30/30 | 50/50 | 19/50 | +0.62 |
| Semantic | 50/50 | — | 49/50 | 50/50 | 14/15 | 49/50 | 28/50 | +0.44 |
| Conjunction | 65/65 | — | 65/65 | 65/65 | 50/50 | 62/65 | 15/65 | +0.77 |
| Procedural | 80/80 | — | 80/80 | 80/80 | 70/72 | 80/80 | 16/80 | +0.80 |

> **Read this before citing any of these corpora for retrieval quality — the first version of this
> note drew the wrong conclusion and it is corrected here.**
>
> Three arms, and the difference between them is what each measures:
>
> - `V1` — accuracy given **the gold sessions alone**. A perfect selector.
> - `V8` — accuracy given **the entire haystack**. Unlimited context, no selection at all.
> - `V9` — accuracy given the **top-`K_ref` sessions a plain BM25 retriever returns**. A lexical
>   baseline selector, and the arm that was missing.
>
> **`V1 − V9` is what a PERFECT SELECTOR would capture — an upper bound, and on some shapes an
> unreachable one.** `V1 − V8` is not a headroom number: it only asks whether distractors confuse a
> reader who already has everything. Reading `V1 − V8 ≈ 0` as "retrieval quality cannot matter here"
> was a mistake — a real system does not dump the haystack into context, it *selects*, and selecting
> badly is far worse than either arm above. Measured against a lexical baseline, **every vertical has
> substantial headroom, from 0.34 (Forgetting) to 0.80 (Procedural)** in the shipped records.
>
> **But `V1 − V9` is NOT "the headroom a better retriever can capture", which is what this passage
> used to say.** A real retriever returns gold *plus* whatever else it ranks highly, so it can never
> beat having everything: **its ceiling is V8, not V1.** Where the two diverge, most of the published
> headroom is unbuyable. `V8 − V9` is the reachable half, and it is published per shape as
> `headroom_reachable` (ADR-028 §3e).
>
> **`limited_by` does not currently discriminate.** Each shape's sidecar entry also carries
> `limited_by`, meant to say whether the shape is retrieval-limited or reasoning-limited. The probe
> runner sets it to `reasoning` when the whole-haystack arm (V8) recovers less than half of the gap
> between the BM25 arm (V9) and the gold-only arm (V1), and to `retrieval` otherwise. On the shipped
> reference records it reads `retrieval` for all 35 shapes that carry it — `forgetting/never-known`,
> which has no gold, carries none — so it tells no shape apart from any other. Read
> `headroom_reachable` beside the published headroom instead; where the two diverge, that gap is the
> unbuyable part.
>
> Four shapes, exactly as the shipped sidecars record them:
>
> | shape | V1 | V8 | V9 | `V1 − V9` published | `V8 − V9` reachable | `limited_by` | chance floor |
> |---|---|---|---|---|---|---|---|
> | `prospective/due-window` | 18/18 | 16/18 | 1/18 | 0.9444 | 0.8333 | retrieval | — |
> | `episodic/participant-attribution` | 14/15 | 13/15 | 4/15 | 0.6667 | 0.6 | retrieval | 0.3333 |
> | `bitemporal/belief-at-instant` | 36/36 | 34/36 | 23/36 | 0.3611 | 0.3056 | retrieval | — |
> | `temporal/occurrence-order` | 20/20 | 20/20 | 5/20 | **0.75** | **0.75** | retrieval | **0.5** |
>
> **Read the reachable column before buying retrieval work** — and the chance floor beside it.
>
> `due-window` used to be the cautionary case here at 0.94 published against 0.17 reachable, because
> a reader holding the entire haystack failed 78% of the time. Its answer key was wrong; V8 is now
> 16/18 and the two columns nearly agree.
>
> `occurrence-order` is the number to read carefully, for a different reason. Its two columns agree,
> so retrieval work does pay — but the question names its own two candidates, so a reader with no
> evidence still reaches gold half the time. **Its 0.75 contains 0.50 that a coin captures**, and its
> `v9_above_chance` is **−0.25**: our baseline scores *below* chance because it declines rather than
> guessing. Compare systems on the distance above the floor, not on 0.75.
>
> `participant-attribution` is a retrieval shape again in the shipped corpus — see its section above.
> Its V9 of 4/15 sits below its 1/3 chance floor (`v9_above_chance` −0.07).
>
> Episodic's interference cost is **0.02** (V1 49/50, V8 48/50), and both of the questions that
> regress on the whole haystack are `participant-attribution` ones. The shape also holds one question
> that fails on gold alone and passes on the whole haystack — gold-only strips conversational context
> that can help identify a speaker — so **V1 is not a strict ceiling for attribution shapes.**
>
> **`V1 − V9` is an upper bound, not an estimate, and here is why.** The calibration gate drags BM25
> coverage into band by injecting the question's own vocabulary into distractors as a bracketed,
> labelled clause — `(Also on my mind: …)`. Strip that clause from the distractors and BM25 coverage
> jumps by **+0.10 to +0.34**, to 0.87–1.00; strip it from gold instead and almost nothing moves. So
> **the entire retrieval difficulty of these corpora, for a lexical retriever, is one parenthetical
> keyword list**, and any retriever that discounts formulaic scaffolding sees a far easier corpus.
> V9's baseline is depressed and the headroom above it inflated by that amount. Those figures were
> measured on an earlier revision of the corpora (`tools/measure_scaffolding_dependence.py`); the
> clause is still present in every shipped corpus, but the shipped sidecars do not carry the
> `structure.scaffolding_dependence` field that recorded the measurement, so the size of the effect
> on the shipped bytes is not recorded with them. Difficulty that a one-line regex defeats is not
> difficulty; earning it from naturalistic same-domain competition instead would be a generation
> change, and with the family frozen none is scheduled.
> **And `V1 − V9` contains a component no ranker can reach.** Having found that the scaffolding
> depresses BM25, we told a consuming project to expect a scaffolding-robust retriever near `V8`.
> That was an extrapolation from a *coverage* figure presented as an expectation about *accuracy*,
> and measuring it refuted it. The table below is that measurement, taken on an earlier revision of
> the corpora; the shipped records differ (Arithmetic's V1 is now 50/50 and its V9 21/50, for
> example) and it has not been repeated on them:
>
> | Vertical | V9 as published | **V9 scaffolding-robust** | V8 whole haystack | V1 gold-only | questions needing > `K_ref` |
> |---|---|---|---|---|---|
> | Arithmetic | 0.320 | **0.680** | 0.840 | 0.940 | **14** |
> | Episodic | 0.600 | **0.840** | 1.000 | 0.960 | **6** |
> | Prospective | 0.680 | **0.960** | 0.960 | 0.980 | 0 |
> | WorkingMemory | 0.883 | **1.000** | 1.000 | 1.000 | 0 |
> | Forgetting | 0.571 | **0.886** | 1.000 | 1.000 | 0 |
> | Bitemporal | 0.800 | **0.983** | 0.983 | 1.000 | 0 |
> | Temporal | 0.820 | **1.000** | 1.000 | 1.000 | 0 |
>
> Where **questions needing > `K_ref`** is non-zero, a top-`K_ref` retriever cannot physically supply
> every gold component however well it ranks, and one missing input to a derived answer is a wrong
> answer. That is a `G`-against-`K` property of the corpus, not a property of any retriever — **a
> larger `K` buys it more cheaply than a better ranker.** Where it is zero, a scaffolding-robust
> retriever comes close to `V8`, which is the control that isolates the mechanism.
>
> The last column is structural, so it can be read straight off the shipped `G` distributions: with
> `K_ref` = 5, Arithmetic has 14 such questions, Conjunction 13 and Episodic 7, and every other
> vertical has none. The shipped sidecars do not carry the `structure.retrieval_ceiling` field that
> recorded the rest of the table.
>
> **And no vertical in this family has a validated difficulty ladder.** Every corpus carries
> `difficulty_validated: false`. The bands describe **how the corpus was built** and nothing more —
> a higher rung is not known to be harder, and should not be reported as though it were.
>
> The rule they used to pass had two artifacts in it, and neither correction works alone. Coverage
> was ranked with the calibration scaffolding in place, which is worth +0.10 to +0.34 on its own;
> and a dial that moves `G` moves coverage through the structural ceiling `min(1, K/G)` without
> touching retrieval. On Arithmetic the shortfall against that ceiling varies by 0.36 with the
> scaffolding in and by **0.000** with it out — the artifact was covering for the ceiling.
>
> With both corrections applied, every band of every vertical sits on its ceiling. WorkingMemory,
> which this guide previously named as the one validated ladder, read 1.00 / 1.00 / 1.00 / 0.67 /
> 0.75 as gated and **1.00 / 1.00 / 1.00 / 1.00 / 1.00** scaffolding-stripped: the gradient was the
> clause. It could not have been otherwise — its dial is measured in *sessions between*, and BM25
> has no position component.
>
> **That last sentence was right and its consequence was larger than this note drew.** If BM25 has
> no position component then it cannot see the ladder's independent variable at all — so whatever
> gradient the gated numbers showed was not distance. It was SIZE: the generator built
> `distance + 1` sessions with gold pinned to session 0, so the haystack grew with the rung label
> and the two variables moved as one. Confirmed by moving one gold to eleven indices of its own
> haystack, with top-5 membership identical at every one.
>
> **The ladder now holds H constant at 60 non-gold sessions on every rung**, so gold position is the
> only thing that moves and the reference retriever goes flat — V9 **9 / 7 / 7 / 6 / 8** of 12,
> non-monotone and well inside the sampling noise at n=12. A control that cannot see the independent
> variable is what makes a gradient in a consumer's system attributable to that variable. Two of the
> five rungs discriminated before; all five do now. See ADR-028 §17.
>
> See ADR-026 §20. `validate_typedmemeval_difficulty.py` now applies both corrections, and would
> refuse every stamp this family has issued.

**Forgetting's V6 is 20/20, and it used to read 20/35.** Its twenty invalidated questions pass:
ablating either the statement or the invalidation stops the model producing the gold, so both
components are load-bearing. Its fifteen still-valid controls fail all fifteen, because their two
components are a statement and a *re-affirmation of the same value* — ablating either leaves the
other. That redundancy is deliberate: the control exists to catch over-forgetting, and a system
that finds either mention has what it needs to say the fact still stands. Those questions carry
`gold_components_redundant: true` — **and the probe runner now reads that flag**, so they are
excluded from the arm rather than counted as fifteen failures. A denominator of 35 pooled twenty
real passes with fifteen declared not-applicables and read as a 57% validity rate on an arm that is
clean where it is defined. Read their per-component coverage as "either suffices", never as
"both were needed".

V3 and V6 take **three** ablation samples per question, not one. A single sample can miss a leak
that is there — the distractor collision fixed in 0.22.0-beta was caught by one sample and could as
easily have been missed by it. Unlike V2 there is no hit threshold: one sample that rebuilds the
answer from distractors alone condemns the question.

These are reported as measured. The remaining V1 shortfalls are single questions — one each in
Prospective (`not-yet-true`), Episodic (`participant-attribution`) and Forgetting (`still-valid`) —
plus one failed pair-flip each in Prospective and Forgetting. A question the ceiling cannot answer
measures the ceiling, so treat those as the noise floor of the vertical rather than as headroom in
the system under test — the per-question records name exactly which ones they are.

Three of the rules do not apply to every question, and saying so matters more than a full column.
V1 and V2 are not applicable to a never-known probe: its gold *is* an abstention, so "I have no way
of knowing" is both the correct answer and what any model with no context says, and scoring it would
reject all fifteen for being guessable when what was measured is that the corpus asked for a
negative and got one. V3 and V6 require the ablated model to reproduce the *specific* value rather
than merely a negative, for the same reason. Where a gold answer carries no content the question did
not already supply — Prospective's "not yet", for example, whose content is a date the question
already gives — V3 abstains rather than scores, because it cannot tell "reached the evidence" from
"said what any model with no evidence says". Those abstentions are why Prospective's V3 denominator
is 35 and not 72: 37 questions are recorded as not decidable.

**Read Episodic's V3 with the same caution.** It passes 50/50, but `participant-attribution`'s
answer is one of *three* — "me", "you" or "both of us". An ablation probe cannot fully distinguish a
model that reached the evidence from one that guessed, so V3 is weak by construction on that shape.
What bounds guessability there is V2, which samples ten times with no context at all and passes
50/50. Episodic's one V1 shortfall is in the same shape.

V1, V2, V3 and V6 need a reference model, so they run at authoring time and their per-question
records are stamped into the corpus metadata. The generators
(`tools/gen_typedmemeval_<vertical>.py`) and the probe runner
(`tools/run_typedmemeval_probes.py`) are in the repository: the corpora are reproducible from them,
and that is what makes them criticizable. The probe records are tied to the retired reference
deployment and will not be refreshed; see the note above the probe table.

## V7 — can a cheap classifier find the gold without reading it?

Every rule above asks whether a question is *answerable* and whether its evidence is *necessary*.
None of them asks whether the evidence is **separable** — and a corpus whose gold can be picked out
by a one-line filter measures nothing, however answerable its questions are.

V7 tries cheap single-feature classifiers at telling gold sessions from distractors and scores each
as a direction-folded AUC, where 0.5 is chance and 1.0 is a perfect tell:

| Feature | What an adversary would use |
|---|---|
| `session_length_chars` | gold sessions being longer or shorter |
| `turn_count` | gold having more exchanges |
| `position_in_haystack` | gold sitting early or late |
| `digit_density` | gold carrying the numbers |
| `uppercase_density` | gold carrying the proper nouns |
| `sentence_count` | text equalised without its punctuation being equalised |
| `punctuation_density`, `em_dash_density` | a glyph one side's templates use and the other's do not |
| `mean_turn_chars` | length, re-expressed per turn |
| `type_token_ratio` | gold's randomised content against filler's repetition |
| `gold_marker_ngram` | a recurring phrase carried by gold and not by filler |
| `boilerplate_ngram` | a recurring phrase carried by filler and not by gold |

Each numeric feature is measured three ways: over the whole session, over each speaker's turns
alone, and over the **first** turn of each speaker. That is not belt-and-braces. Padding lands on a
single turn, so equalising the pooled session leaves the other slices exactly as the generator wrote
them — measured that way, gold was recoverable from user-turn length alone at AUC 1.000 while every
pooled figure sat comfortably under the bar. A parity check on a sum is not a parity check on its
terms, and the attacker picks the slice.

A corpus is **refused** at 0.75 on any shape feature. The rule runs in the generator, is stamped
into every corpus's metadata beside V1–V6, and is re-measured in CI rather than trusted from its own
record — a record nobody re-runs is a claim, not a check. It is recomputed twice over: once by the
Python tool in CI, and again by an independent C# implementation in the test suite, which holds the
threshold and the refused-feature list as its own constants rather than reading them back from the
record it is checking.

Pairs are formed **within a question** and folded once after pooling. That is not a detail: the
attacker being modelled is handed one haystack and asked which session holds the evidence, so pairs
drawn from different questions answer an easier question. Pooling across the corpus diluted a real
Forgetting tell from 0.903 to 0.616, and it got *better* the more abstention questions a vertical
had, because questions with no gold contribute distractor-only values.

One deliberate non-refusal, because hiding it would be the same failure in a different coat:

- **Question relevance is exempt and is not a feature.** Gold is supposed to be more relevant to its
  question than a distractor is; if it were not, the question would be unanswerable. How *easily*
  that is exploited is what the BM25 calibration gate bounds.

Phrase recurrence used to sit here too, exempted on the grounds that template filler repeats itself
and real variety is a corpus revision rather than a check. The reasoning was sound and the
consequence was not: an exemption with no ceiling cannot tell 0.60 from 0.99, and underneath it
Prospective reached **0.990** on a filler trigram — which is to say its *absence* marked gold. Both
phrase directions now refuse at the same bar as every other feature.

`WorkingMemory` exempts `position_in_haystack`: it pins its fact to session 0 by design, so position
separates gold perfectly and is meant to.

**This rule earned its keep immediately.** Measured against the v1 corpora, capitalisation density
found gold at AUC 0.990 in Forgetting and session length at 0.992 in WorkingMemory — gold states an
arbitrary *named* fact, so it carried proper nouns and extra text that filler did not, and counting
capital letters found the evidence without reading it. The v2 corpora pad every session to a common
shape; in the shipped sidecars the worst refused feature across the family is 0.663 (Arithmetic's
`gold_marker_ngram`).

## Bands, not points

A single run is a point. Repeated runs of one configuration give a band:

```csharp
var runs = new List<ExternalBenchmarkResult>();
for (var i = 0; i < 3; i++)
    runs.Add(await runner.RunAsync(agent, TypedMemEvalVertical.Episodic, options));

var summary = TypedMemEvalRunSet.Summarize(runs);
Console.WriteLine($"correct {summary.Outcomes[TypedMemEvalOutcome.Correct]}");
Console.WriteLine($"questions that flipped: {summary.QuestionsWithFlips}/{summary.QuestionsCompared}");
```

`Summarize` **refuses** to band runs that differ in corpus, judge fingerprint, configuration, or
what the provider did with the requested answer sampling. Those are different experiments, and
averaging them manufactures a stability nothing measured.

Two runs can agree by coincidence and band to zero width, which reads as perfect stability and is
not — `AtMinimumRunCount` says when you have only two, and `QuestionsWithFlips` is the number that
says whether a band is evidence. Three runs are recommended.

Pin `AnswerSeed` to measure the memory system's own variance; vary it to measure the answer model's.

## What this family does not do

- **No cross-family composite score.** The verticals measure different mechanisms; a blend would
  rebuild the one percentage the family exists to replace.
- **No leaderboard claims.** With 50–80 questions per vertical, TypedMemEval is an instrument for comparing
  configurations of one system and for regression-testing memory mechanisms. Cross-system ranking
  needs the bands above and honest `n` reporting.
- **No claim beyond the vertical.** The shapes inside a vertical (and WorkingMemory's distance
  rungs, and the pair sets) hold 10–36 questions each. Their `n` is published next to every number
  because at those sizes they support diagnosis, not claims.
- **No endorsed MemoryBaseline pentagon.** `ToBaseline` accepts a family result mechanically,
  because it keys on `BenchmarkId` and the compatibility accuracy field. That is not an
  endorsement: a typed-outcome-aware mapping has to exist before a baseline visualization of these
  results is published.
- **No changes to LongMemEval.** Every 0.19–0.21 surface and the time-grounded corpus are untouched.

## See also

- [ADR-026](../../adr/026-typedmemeval-benchmark-family.md) — the design of record, including the
  five places it pushes back on its own brief.
- [LongMemEval getting started](../longmemeval/getting-started.md) — the harness this family reuses.
- [Time-grounded probe](../longmemeval/time-grounded-probe.md) — the seed of TypedMemEval-Prospective.
