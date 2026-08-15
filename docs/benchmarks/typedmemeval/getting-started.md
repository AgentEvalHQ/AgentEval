# TypedMemEval

TypedMemEval is an AgentEval-authored benchmark family that measures five memory mechanisms in
isolation: **prospective memory**, **episodic structure**, **arithmetic over memory**,
**working-memory distance**, and **forgetting**. Each vertical is its own corpus, its own
question types, and its own validity rules.

> **Citation rule.** Cite results as **"TypedMemEval-\<Vertical\> v2 (AgentEval)"**. TypedMemEval
> results are **not** LongMemEval results and must never be presented as, summed with, or averaged
> with LongMemEval numbers. The twelve Prospective questions seeded from the time-grounded probe
> exist in both `agenteval-timegrounded-v1` and TypedMemEval-Prospective v2; a report that runs both
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
there is no default to fall back to. The CLI binding requires `AZURE_OPENAI_ENDPOINT`,
`AZURE_OPENAI_API_KEY`, and `AZURE_OPENAI_DEPLOYMENT` and has no stub fallback, because the judge
round-trip is the correctness signal. Programmatic callers can use any `IChatClient` and any
`IEvaluableAgent`.

It prints the typed vector with every denominator — the run totals, the per-shape strata, coverage
against the corpus's calibrated floor, and the attribution counts — plus the vertical's own extras
(pair consistency, the distance curve, the three forgetting sub-counts). It gates on nothing: the
family publishes no pass threshold, so the run exits `0` when it measured anything and `11` when it
measured nothing, and its run summary is recorded as `WARN` (indeterminate) rather than PASS/FAIL.

The CLI writes a canonical manifest, summary, and `report-native.json`. The native report preserves
questions, gold answers, agent responses, and the judge/evidence detail the options allow — treat it
as sensitive data: restrict access, retention, and publication.

### Typed outcomes, never one percentage

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

## The five verticals

### Prospective (50 questions)

Due-later reminders, expiring validity, not-yet-true assertions — plus the twelve time-grounded
probe questions carried in as its seed. Runs under `TimestampsOnly` grounding: the conversations
contain **no absolute date and no four-digit year**, so every temporal expression is relative and
resolving it requires the session's own timestamp.

Thirty-eight of the fifty are **19 before/after pairs**: one haystack asked twice, differing only in
when it was asked, with gold flipping between the arms. Pairs are the vertical's teeth — a system
that answers the after-arm correctly but also fires on the before-arm is *premature*, which no
single question can show.

The agent must implement `ITimestampedHistoryInjectableAgent`; a run refuses before its first
provider call otherwise. There is no text fallback, because the dates a fallback would use are the
ones this vertical exists to take away.

```csharp
// The probe and its control: same corpus, same hash, two option sets.
var probe = await runner.RunAsync(agent, TypedMemEvalVertical.Prospective,
                                  TypedMemEvalCorpus.ProspectiveProbeOptions);
var control = await runner.RunAsync(agent, TypedMemEvalVertical.Prospective,
                                    TypedMemEvalCorpus.ProspectiveControlOptions);
```

Equal scores mean the system honours the timestamps it was given. A drop under the probe is the
share of its temporal score that was coming from dates printed in the prompt.

Watch `PairConsistency.BothArmsSameOutcome`: gold flips between arms by construction, so identical
outcomes on both arms is the signature of a system that never received the query time — or ignored
it and read a wall clock instead.

### Episodic (50 questions)

Memory of the conversation *as an event*: 20 assistant-stated answers (the user never states them),
15 list-order questions, 15 speaker-attribution questions.

**Known limitation, v1.** The attribution shape's statements are emitted from matched templates so
that either speaker could plausibly have said them (that is what stops the answer being inferable
from content). A consequence is that the surrounding wording is fixed, so a system that stores no
speaker label at all can still recover the answer from the template rather than from memory. The
shape therefore measures less than its name promises until v2 varies the framing; read its numbers
as a floor, not as speaker-attribution accuracy.

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

### WorkingMemory (48 questions)

Twelve fact families × four distances (1, 5, 15, 40 intervening sessions). Every cell is an
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

## Coverage: what the corpora guarantee, and what they don't

A saturated corpus cannot see retrieval mechanisms. Two mechanisms produce non-saturation, and the
family is precise about which applies where.

**Structural dispersion.** Gold spread across `G` sessions caps coverage at `min(1, K/G)` for a
budget of `K`. With the declared reference budget `K_ref = 5`, a structural ceiling below 1.0 exists
**only where `G > 5`** — Arithmetic's high-dispersion questions and Episodic's longest list-order
questions. For every `G ≤ 5` question — all of Prospective, Forgetting, and WorkingMemory, where the
mechanism under test fixes `G` at 1 or 2 — the ceiling is exactly 1.0, and presenting that as a band
would be numerology.

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
| Prospective | 50 | 0.820 | 1 (×46), 2 (×4) |
| Episodic | 50 | 0.880 | 1 (×35), 4–7 (×15) |
| Arithmetic | 50 | 0.635 | 3–6 |
| WorkingMemory | 48 | 0.896 | 1 |
| Forgetting | 50 | 0.830 — **0.757 over the 35 gold-bearing questions** | 0 (×15), 1 (×15), 2 (×20) |

Forgetting's two coverage figures are the same distinction the runtime report draws. Fifteen of its
fifty questions are never-known probes with no gold at all, and a question with nothing to retrieve
scores 1.0 vacuously — it cannot miss what was never there. The headline mean therefore mixes
measurement with definition, so the gold-bearing figure travels beside it, and the floor a run is
compared against (`Coverage.CalibratedFloorMean`) is computed over gold-bearing questions only. A
floor inflated by vacuous ones would flatter every system by the share of no-gold questions.

## Validity rules

Written before generation, and re-checked in CI over what actually ships:

| Rule | What it requires |
|---|---|
| **V1** Oracle answerability | Every question, given only its gold sessions, must be answered correctly by a stated reference model. For pairs, both arms must be answerable *and* the answers must differ. |
| **V2** Non-inferability | With zero context, 10 samples; the question is rejected if 2 or more produce the gold answer. |
| **V3** Distractor plausibility | Given only the *non-gold* sessions, the model must **not** produce the gold answer — the dual of V1, and the only real defence against a distractor that accidentally contains the answer. |
| **V4** No absolute dates | For time-dependent verticals, no four-digit year and no absolute date in any message content. |
| **V5** Gold derived, not typed | Generators derive every gold answer from the sessions they emitted. |
| **V6** Component non-redundancy | For Arithmetic and Forgetting, ablating any single gold component must stop the model producing the gold. |

Shipped probe records (reference deployment `gpt-5.5`, per-question outcomes in each corpus's
`.meta.json`). Dashes are not-applicable rather than skipped, but for different reasons per column, and the
difference matters. Pair-flip needs pairs, which only Prospective and Forgetting have. V6 is
scoped by design to Arithmetic and Forgetting (ADR §12) — not because the other verticals lack
multi-component gold, since Episodic list-order has G = 4–7 and some Prospective questions have
G = 2, but because those are the two verticals whose per-component coverage echo depends on every
component being individually load-bearing. V1 and V2 do not apply to a never-known probe, whose
gold is itself an abstention.

| Vertical | V1 oracle | V1 pair-flip | V2 non-inferability | V3 gold-ablated | V6 leave-one-out |
|---|---|---|---|---|---|
| Prospective | 47/50 | 17/19 | 50/50 | 49/50 | — |
| Episodic | 50/50 | — | 50/50 | 50/50 | — |
| Arithmetic | 48/50 | — | 50/50 | 50/50 | 50/50 |
| WorkingMemory | 48/48 | — | 48/48 | 48/48 | — |
| Forgetting | 34/35 | 14/15 | 35/35 | 35/35 | 20/20 |

These are reported as measured. The remaining V1 shortfalls sit where the *answer model*, not the
memory system, is the limit: the Arithmetic misses are duration questions whose gold requires
summing several timestamp-derived intervals, and whose arithmetic was verified correct independently
of the model. Four Prospective questions and three of its pairs sit in the same place.
A question the ceiling cannot answer measures the ceiling, so treat those as the noise floor of the
vertical rather than as headroom in the system under test — the per-question records name exactly
which ones they are.

Three of the rules do not apply to every question, and saying so matters more than a full column.
V1 and V2 are not applicable to a never-known probe: its gold *is* an abstention, so "I have no way
of knowing" is both the correct answer and what any model with no context says, and scoring it would
reject all fifteen for being guessable when what was measured is that the corpus asked for a
negative and got one. V3 and V6 require the ablated model to reproduce the *specific* value rather
than merely a negative, for the same reason.

V1, V2, V3 and V6 need a reference model, so they run at authoring time and their per-question
records are stamped into the corpus metadata. The generators
(`tools/gen_typedmemeval_<vertical>.py`) and the probe runner
(`tools/run_typedmemeval_probes.py`) are in the repository: the corpora are reproducible, and that
is what makes them criticizable.

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
| `boilerplate_ngram` | a recurring phrase present in one side only |

A corpus is **refused** at 0.75 on any shape feature. The rule runs in the generator, is stamped
into every corpus's metadata beside V1–V6, and is re-measured in CI rather than trusted from its own
record — a record nobody re-runs is a claim, not a check.

Two deliberate non-refusals, because hiding them would be the same failure in a different coat:

- **Question relevance is exempt and is not a feature.** Gold is supposed to be more relevant to its
  question than a distractor is; if it were not, the question would be unanswerable. How *easily*
  that is exploited is what the BM25 calibration gate bounds.
- **Phrase recurrence is measured but does not refuse** (0.552–0.850 across the family). Filler is
  template-generated, so every filler phrase recurs across questions and no gold phrase does. That
  number says "this filler came from templates", not "this corpus hides a tell", and driving it to
  chance needs filler with the variety of real conversation — a corpus revision, not a check.

`WorkingMemory` exempts `position_in_haystack`: it pins its fact to session 0 by design, so position
separates gold perfectly and is meant to.

**This rule earned its keep immediately.** Measured against the v1 corpora, capitalisation density
found gold at AUC 0.990 in Forgetting and session length at 0.992 in WorkingMemory — gold states an
arbitrary *named* fact, so it carried proper nouns and extra text that filler did not, and counting
capital letters found the evidence without reading it. The v2 corpora pad every session to a common
shape; the worst refused feature across the family is now 0.713.

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
- **No leaderboard claims.** With 48–50 questions per vertical, v1 is an instrument for comparing
  configurations of one system and for regression-testing memory mechanisms. Cross-system ranking
  needs the bands above and honest `n` reporting.
- **No claim beyond the vertical.** The shapes inside a vertical (and WorkingMemory's distance
  rungs, and the pair sets) hold 5–20 questions each. Their `n` is published next to every number
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
