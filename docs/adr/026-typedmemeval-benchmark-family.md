# ADR-026: TypedMemEval — A Mechanism-Isolating Memory Benchmark Family

**Status:** **Accepted** — reviewed jointly with the consuming project on 2026-08-15 and implemented in v0.22.0-beta. All five §9 pushbacks were accepted, including pushback 1 (the reframing of "coverage 0.5–0.9 by construction" into published ceiling tables plus a calibration gate plus runtime echo), and guard 6 (WorkingMemory rehearsal confound → independent per-question haystacks) was ratified as load-bearing.

The §10 open questions were settled as follows, and the design below is to be read with these answers applied:

1. **Name** — keep `TypedMemEval`. The .NET homonym is accepted; docs introduce it as a benchmark name first. FacetMemEval is retired.
2. **`Missed` semantics** — the two-axis proposal (§6) is ratified as written: verbal outcome from the judge, evidence attribution as necessary-but-not-sufficient, `Unobserved` never guessed.
3. **"Participant attributes"** — confirmed as speaker *attribution*. The §5.2 reading is what was meant; attributes-of-participants stays WorkingMemory territory.
4. **`K_ref`** — 5 sessions, uniform across verticals for v1, approximating the consumer's real evidence breadth. Revisited per-vertical only if v1 data demands it. Addition: the **per-question** BM25 realised-coverage distribution is stamped into corpus metadata, not only the mean, so an embedding retriever's realised coverage can be correlated against the lexical floor.
5. **Control arms** — Prospective only, via the tg option-pair mechanism over one corpus file.
6. **MemoryBaseline** — a single-score pentagon is **not** endorsed. `ToBaseline` compatibility is mechanical, not endorsed, and the family docs say so; a typed-outcome-aware mapping must exist before any baseline visualization is published.
7. **Per-cell sizing** — v1 ships as designed (48–50 per vertical). Promoting the WorkingMemory distance curve to n ≥ 30 per rung is a v2 decision, taken only after v1 shows which cells carry signal.
8. **Phasing** — ratified as proposed. All three milestones landed in one release; the family was not documented publicly until all five verticals existed.

**Date:** 2026-08-15 (proposed and accepted)

**Relates to:** ADR-009 (benchmark strategy), ADR-017 (unified benchmarks namespace), the LongMemEval harness (0.19–0.21), the time-grounded probe corpus (`agenteval-timegrounded-v1`)

---

## Context

AgentEval's LongMemEval harness is finished in the sense that matters: the consuming
memory-system project that drove its last three releases reports every prior ask shipped
and consumed — type-filtered sampling, abstention control, the structured judge surface,
answer-model pinning, the public oracle arm, the time-grounded probe. Those surfaces are
frozen. This ADR does not change any of them.

What remains is a gap LongMemEval cannot close, because the gap is in its dataset, and
its dataset is its identity.

### What LongMemEval-S cannot measure

Evidence from the consuming project's runs, and from our own corpus analysis:

- **Prospective memory** — things stated as not yet true, reminders due later, validity
  that expires. The original corpus has no such questions; our time-grounded probe added
  twelve clock-dependent questions, only four of them prospective-memory — and it is a
  probe, not a benchmark (its own documentation says so).
- **Episodic structure** — LongMemEval-S has 56 of its 500 questions (11%) whose gold
  answer was stated by the assistant rather than the user, of which a 30-question
  stratified consumer run draws about 4 — and nothing in the corpus isolates list-order
  or speaker attribution at all.
- **Arithmetic over memory** — in the consumer's sampled runs, 8 questions involve
  derived numbers, and none isolate the derivation: they are never separable from
  retrieval difficulty, so a wrong sum cannot be attributed to a missed input versus
  failed arithmetic.
- **Working-memory distance and forgetting** — no question types exist for either.
- **Saturation** — the consumer measures realised gold coverage of 0.965–0.980 on
  LongMemEval-S with their retrieval stack. At that coverage, every retrieval-side
  mechanism is invisible: the benchmark cannot distinguish a system that retrieves well
  from one that retrieves adequately, because near-everything relevant fits the budget.
  Our own oracle-arm data agrees from the other direction: forcing coverage down
  (`GoldSessionFraction = 0.5`, realising 0.62) moves accuracy violently (the consumer
  measured 95% → 15% on their hand-rolled equivalent), which means the *questions* are
  coverage-sensitive — the corpus just never exercises that sensitivity.

### The identity constraint

LongMemEval is a published benchmark (ICLR 2025). Its identity **is** its fixed dataset
and questions. Run options, metadata enrichment, and judge robustness over the identical
questions are still LongMemEval. New corpora are not, and naming a new corpus
"LongMemEval-anything" would be benchmark-name substitution — the same integrity failure
as quoting recall@k as QA accuracy. The time-grounded probe already lives on the right
side of this line (its API documentation states "not comparable with LongMemEval scores"),
but it lives there as a footnote on the LongMemEval runner. A benchmark family with five
corpora and hundreds of authored questions cannot be a footnote on someone else's name.

## Decision

Build a **separate benchmark family** in `AgentEval.Memory`, named **TypedMemEval**, with
five verticals, its own runner, its own judge fingerprint, and a hard identity separation
from LongMemEval enforced in code, in every report field, and by a stated citation rule.

This is a design decision, not an implementation. Every section below is reviewable and
revisable until this ADR's status moves to Accepted.

---

### 1. Name

**TypedMemEval**, subsets **TypedMemEval-Prospective**, **-Episodic**, **-Arithmetic**,
**-WorkingMemory**, **-Forgetting**. The dataset revision is stamped into every result; the shipped
revision is `v2` (see §13 — `v1` is superseded and should not be cited).

The working name is kept, deliberately. "Typed" does double duty and both duties are the
design's two pillars: **typed verticals** (each corpus isolates one memory mechanism
instead of blending them) and **typed outcomes** (results are a vector of
correct / wrong / abstained / missed — never one percentage). It is descriptive,
field-style (LongMemEval / LoCoMo / DMR), and shares no stem with "Long", so no
abbreviation of it collides with LME. It also brands cleanly under AgentEval — as does
the fallback below — because the branding is carried by the artifact scheme rather than
the name itself: the corpus-id prefix (`agenteval-typedmemeval-*`) and the citation form
("TypedMemEval-\<Vertical\> v3 (AgentEval)") tie every result to the project without
needing "AgentEval" inside the family name.

One honest caveat, flagged for the joint review: in a .NET library, "Typed" has a
type-system homonym (`TypedResults`, strongly-typed clients). A .NET developer skimming an
API list could briefly read `TypedMemEvalRunner` as "the strongly-typed memory evaluator."
The docs mitigate this by always introducing the family as a benchmark name first. If the
review judges the homonym too costly, the prepared fallback is **FacetMemEval** (five
facets of memory, same subset scheme); everything else in this design is name-independent.

Naming scheme, fixed now so nothing drifts later:

| Thing | Pattern | Example |
|---|---|---|
| Family | `TypedMemEval` | — |
| Subset | `TypedMemEval-<Vertical>` | `TypedMemEval-Forgetting` |
| Corpus id | `agenteval-typedmemeval-<vertical>-v<N>` | `agenteval-typedmemeval-prospective-v3` |
| Control-arm `DatasetMode` label | corpus id + `-control` — an options label over the *same* corpus, never a second corpus file (§5.1; the tg pattern) | `agenteval-typedmemeval-prospective-v3-control` |
| Question id | `tme-<abbrev>-<NNN>` | `tme-ari-017` |
| Pair id | `tme-<abbrev>-p<NN>` | `tme-pro-p07` |
| Question id abbrevs | `pro`, `epi`, `ari`, `wm`, `for` | — |
| `BenchmarkId` | `typedmemeval-<vertical>` | `typedmemeval-episodic` |
| `BenchmarkName` | `TypedMemEval-<Vertical> v<N> <n>q` | `TypedMemEval-Episodic v3 50q` |
| EvalResult dimensions | `typedmemeval.*` | `typedmemeval.outcome.missed` |

The `_abs` question-id suffix convention is preserved *only* where a question's gold
behaviour is genuine never-known abstention (`QuestionResult.IsAbstention` infers from
that suffix for legacy results, so misusing it would silently mislabel composition).
Forgetting-vertical questions whose gold is "no longer known" do **not** carry `_abs` —
that distinction is the vertical's entire point (§5.5).

### 2. The unquotability rule, enforced

**Hard rule: a TypedMemEval result must be unquotable as LongMemEval.** Enforcement is
concrete, not aspirational:

1. **Every report field carries the family name.** `ExternalBenchmarkResult.BenchmarkId`
   and `BenchmarkName` are `required` fields that already exist; family runs stamp them
   per the table above. `BenchmarkRunProvenance.DatasetIdentifier` carries the corpus id
   (which embeds the revision), and the family options façade exposes no provenance knob
   at all: the mapper always sets `RunProvenanceMode.Full`. Full, not merely non-None —
   in the shipped capture path, `DatasetIdentifier` and the dataset hash are populated
   only under `Full` (`PromptsOnly` returns before the dataset fields), and for an
   embedded corpus Full capture costs no file I/O. A result that cannot say which corpus
   produced it is exactly the artifact the identity rule exists to prevent.
2. **No LongMemEval-named type is introduced by the family, and none reaches its
   serialized results.** The runner is `TypedMemEvalRunner`, not a partial of
   `LongMemEvalBenchmarkRunner` (a deliberate deviation from the time-grounded probe's
   placement — see §9, pushback 2). Shared *implementation* machinery is fine and
   intended (§3); shared *names* in the serialized surface are not. One shipped type is
   a deliberate, documented exception in the API signature itself:
   `LongMemEvalOracleOptions` on `RunOracleAsync` (§3), kept by name because the
   consumer's ask is that their oracle ceiling and ours be *the same number* from *the
   same knobs* — and it never appears in result JSON (only `OracleProjectionReport`'s
   numeric fields do).
3. **A serialization guard test.** CI asserts that the JSON serialization of a
   TypedMemEval run result contains no case-insensitive `longmemeval` token. This makes
   rule 1 a regression test instead of a review comment.
4. **Question ids are family-scoped** (`tme-*`), so even a stripped-context spreadsheet
   of per-question rows identifies its origin.
5. **A stated citation rule**, in the family's documentation and repeated in the runner's
   XML docs:

   > Cite results as "TypedMemEval-\<Vertical\> v3 (AgentEval)". TypedMemEval results are
   > not LongMemEval results and must never be presented as, summed with, or averaged
   > with LongMemEval numbers. The twelve questions seeded from the time-grounded probe
   > (§5.1) exist in both `agenteval-timegrounded-v1` and TypedMemEval-Prospective v3;
   > a report that runs both must not double-count them.

The relationship is honest in both directions: TypedMemEval **may** state that it uses
the LongMemEval *file format* and shares harness machinery — format reuse is an
engineering fact, not an identity claim. Identity is (name, corpus, question set,
scoring), and all four are the family's own.

### 3. Family architecture

**Namespace:** `AgentEval.Memory.External.TypedMemEval`. Corpora under
`src/AgentEval.Memory/Data/typedmemeval/<vertical>/`, embedded resources, `eol=lf`
pinned in `.gitattributes` — the `agenteval-timegrounded-v1` pattern exactly: no dataset
path, versioned identifier, newline-normalized SHA-256 over the shipped text.

**File format:** LongMemEval-compatible session shape (`question_id`, `question_type`,
`question`, `answer`, `question_date` in `yyyy/MM/dd (ddd) HH:mm`, `haystack_sessions`
with `has_answer` labels), extended with one family-owned object per question:

```jsonc
"typedmemeval": {
  "vertical": "arithmetic",              // always present; must match the corpus
  "seeded_from": "tg-prospective-001",   // lineage, present only on carried questions
  "pair_id": "tme-pro-p07",              // links before/after and Forgetting control pairs
  "arm": "after",                        // "before" | "after" | "control" (Forgetting's
                                         // still-valid pair member, §5.5 — unrelated to
                                         // the "-control" DatasetMode label) | null
  "distance_sessions": 15,               // working-memory ladder position
  "derivation": {                        // arithmetic only — see §5.3
    "operation": "sum",
    "inputs": [ { "session_index": 2, "value": 42.50 }, ... ],
    "value": 128.75,
    "unit": "USD"
  },
  "gold_components": [                   // forgetting only — see §5.5
    { "kind": "statement",    "session_index": 1 },
    { "kind": "invalidation", "session_index": 6 }
  ]
}
```

Why format reuse: the tested plumbing — history formatter, timestamped injection,
temporal-grounding stripping, the oracle projector, the data loader's selection rules —
all operate on this shape. Reusing it means the family inherits ~zero-defect machinery
instead of re-proving it. The family loader parses the extension block;
`LongMemEvalDataLoader` ignores unknown fields, and nothing routes family corpora into
the LongMemEval runner in the first place.

The extension block is an answer key — `derivation.value` is the gold answer verbatim,
`gold_components` and `arm` say which sessions matter and which side of the pivot a
question sits on — so it gets the same guard-test treatment as the identity rule (§2.3):
a CI assertion that no assembled prompt (formatted history or question) for any family
question contains a `typedmemeval` block, plus — for derivation values whose literal
presence is actually diagnostic (sums, deltas, durations with non-trivial values) — that
the derivation value never appears as a literal (derivable from the sessions by V5 but
never stated, so its presence is proof of a leak). Count golds are small integers whose
literal appearance in a 15–25-session haystack is easily coincidental, so counts rely on
the block assertion alone rather than a check that would false-positive.

**Corpus constants class per vertical** (`TypedMemEvalProspectiveCorpus`, …), each
following `LongMemEvalTimeGroundedCorpus`: `CorpusId`, `QuestionCount`, `ReadJson()`,
`Sha256()`, `Load()`, and — where a control arm exists — `ProbeOptions`/`ControlOptions`
pairs.

**One public runner for the family:**

```csharp
namespace AgentEval.Memory.External.TypedMemEval;

public sealed class TypedMemEvalRunner
{
    public TypedMemEvalRunner(IChatClient judgeClient, ILogger<TypedMemEvalRunner>? logger = null);

    /// <summary>Runs one vertical against the agent under test.</summary>
    public Task<ExternalBenchmarkResult> RunAsync(
        IEvaluableAgent agent,
        TypedMemEvalVertical vertical,
        TypedMemEvalOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Runs one vertical through the oracle arm — the same projector the LongMemEval
    /// oracle arm uses, so the consumer's ceiling and ours are the same number.
    /// </summary>
    public Task<ExternalBenchmarkResult> RunOracleAsync(
        IChatClient answerClient,
        TypedMemEvalVertical vertical,
        TypedMemEvalOptions? options = null,
        LongMemEvalOracleOptions? oracleOptions = null,
        CancellationToken ct = default);
}

public enum TypedMemEvalVertical { Prospective, Episodic, Arithmetic, WorkingMemory, Forgetting }
```

`RunOracleAsync` reuses `LongMemEvalOracleProjector`, `LongMemEvalOracleReader`, and
`LongMemEvalOracleOptions` (`DistractorSessions`, `GoldSessionFraction`, realised
reporting) verbatim. Those types keep their names — they are shipped, frozen API, and the
consumer's explicit ask across two prompts has been "your projector reachable, so my
ceiling and yours are the same number." Sharing the *mechanism* is the point; the
*report* still says TypedMemEval (rule §2.1 — `BenchmarkId` et al. come from the family
runner, and the serialization guard covers oracle runs too — oracle options never
appear in the result JSON at all; the result embeds only `OracleProjectionReport`'s
requested/realised values, and the embedded `Options` object carries a family
`DatasetMode` with `DatasetPath` forced null by the façade).

**`TypedMemEvalOptions`** is a thin façade, not `ExternalBenchmarkOptions` re-exposed.
It carries only knobs that are meaningful for an embedded-corpus family run —
`AnswerTemperature`, `AnswerSeed`, `RandomSeed`, `MaxQuestions`, `HistoryInjectionMode`,
judge configuration, evidence capture — and maps them onto an internal
`ExternalBenchmarkOptions` with the family's invariants applied:

- `DatasetPath` does not exist on the façade. The family is embedded-only; a path knob
  would reintroduce the "which corpus was this" ambiguity the family exists to end.
- `RunProvenanceMode` is not exposed; the mapper always sets `Full` (§2.1).
- `JudgeVerdictProtocol` is forced to `StructuredJson`. The free-text protocol cannot
  carry a five-way outcome (§6), and the family has no historical free-text scores to
  stay comparable with — it starts structured.
- For grounding-required verticals (§5.1), the mapper forces `HistoryInjectionMode` to
  `StructuredChatHistory` unless the caller already chose a compatible mode — mirroring
  `LongMemEvalTimeGroundedCorpus.ProbeOptions`, and necessary because the shipped
  `ExternalBenchmarkOptions.Validate()` (correctly) rejects temporal grounding combined
  with the `TextBlob` default. Without this invariant the M1 flagship vertical would
  throw on default options.
- Vertical-specific grounding requirements (§5) are validated before the first provider
  call, with the same fail-before-measuring behaviour as the LongMemEval
  temporal-grounding guard. This includes the Arithmetic duration rule (§5.3): under a
  timestamp-free injection mode, duration questions are reported as *unrun*, never as
  failed.

### 4. The distributed-coverage property

A saturated corpus cannot see retrieval mechanisms. The family engineers non-saturation,
and is precise about what can and cannot be promised — this is the one ask this design
reframes rather than accepts verbatim (§9, pushback 1).

**What a corpus can fix by construction is the coverage *ceiling*, not the realised
value.** Realised gold coverage is a property of system × corpus: it depends on the
system's retrieval budget, which AgentEval does not control for an opaque agent. What the
corpus controls is:

- **Evidence dispersion `G`** — gold evidence spread across `G` sessions per question, so
  a retrieval budget of `K` sessions caps coverage at `min(1, K/G)`.
- **Haystack size `H`** — enough same-domain non-gold sessions that retrieving
  everything is not an option (`H` well above the budget in every vertical except
  WorkingMemory's short-distance rungs, where `H` *is* the independent variable), and
  plausible enough that ignoring them requires actual retrieval quality (a distractor
  from another topic is trivially ignorable and would measure a strawman — the oracle
  arm's `DistractorSessions` documentation already states this rule; the family's
  haystacks are built to it).

Here `H` counts **non-gold** sessions per question (gold sessions are counted by `G`,
never double-counted into `H`).

Each corpus **declares a reference budget `K_ref`** (proposed: 5 sessions — a review
input, since it should approximate the consumer's real budget). Being honest about the
arithmetic: with `ceiling = min(1, K_ref/G)`, a structural ceiling below 1.0 exists
**only where `G > K_ref`** — in this family, Arithmetic's high-dispersion questions and
Episodic list-order (ceilings 0.71–0.83 at the declared `G` ranges). For every `G ≤ 5`
question — all of Prospective, Forgetting, and WorkingMemory, where the
*mechanism under test* fixes `G` at 1 or 2 — the structural ceiling is exactly 1.0, and
pretending otherwise would be numerology. So the family's anti-saturation property rests
on **two mechanisms, stated per corpus**:

- **Structural dispersion**, where dispersion is intrinsic to the vertical (Arithmetic
  inputs, list items): the ceiling table is published and recomputed in CI.
- **Calibrated competition**, everywhere: the haystack must make gold *hard to find*,
  not just legal to miss — which no ceiling formula can show and only a measurement can.

That second mechanism is a **gate, not a stamp**: each vertical declares a target band
for reference-retriever realised coverage (proposed: **0.5–0.9 mean, per-question
distribution published**), and a corpus does not freeze until a deterministic lexical
retriever (BM25, no LLM, fully reproducible) run at `K_ref` lands inside its band — the
generator iterates (harder distractors, wider placement, higher lexical overlap with
gold) until it does. The final values, the iteration count, and the tool version are
stamped into the corpus metadata. BM25 is explicitly a *floor proxy*: a stronger
retriever will exceed its coverage, which is what the runtime echo is for. A corpus that
cannot be tuned into its band is redesigned, not shipped with a footnote.

**Verification, three layers:**

1. **Structural, in CI** — model-free assertions **re-derived from the shipped corpus
   bytes**, not read back from the sidecar that the generator stamped: per-question `H`
   against the declared floor and ceiling (scoped per vertical — WorkingMemory
   deliberately *varies* `H` as its independent variable, §5.4), `G` against the declared
   distribution, the ceiling table under `K_ref` recomputed as `min(G, K_ref) / G`, the
   V7 separability AUCs recomputed by a second implementation against a threshold and
   feature list held as C# constants, session order against timestamp order, and gold
   sessions are position-shuffled — with stated per-vertical carve-outs where
   position is pinned by design (WorkingMemory pins the fact to the first session,
   §5.4; Forgetting constrains statement-before-invalidation order, §5.5). The oracle
   projector already emits selected sessions in dataset order for the same
   position-artefact reason.
2. **Construction-time calibration gate, recorded in corpus metadata** — the BM25 gate
   above. This is the honest form of "verified by construction": a real measurement by a
   stated method with an acceptance criterion, not a claim.
3. **Runtime echo, per question** — §7. A level that degraded nothing must be
   distinguishable from a level whose degradation did not matter, which requires the
   realised number next to the requested one — the oracle arm's reporting style, applied
   family-wide.

### 5. The five verticals

Common floor: **n ≥ 30 per vertical, target 50 — and the floor is a per-vertical claim
floor, nothing finer.** The shapes inside each vertical (and WorkingMemory's distance
rungs, and the pair sets) are **diagnostic strata**, sized at n = 5–20 per cell in the
tables below with every cell's `n` published next to its numbers: they explain *where*
a vertical's outcome rates come from, and at those sizes they support diagnosis, not
claims — a 30-question corpus of 15 pairs is 15 analysable pairs, and this ADR does not
pretend otherwise. Sizing every cell to a claim floor of its own would put each vertical
at 90–120 questions; whether any cell deserves that in v1 (the WorkingMemory distance
curve is the strongest candidate) is a costed open question for the joint review (§10),
not something these tables quietly promise. Procedural memory stays consumer-side (it is
agentic and needs tools); it is out of scope for AgentEval and this family.

Common validity rules, written before any generation (per-vertical rules add to these):

- **V1 — Oracle answerability.** Every question, run gold-only through the oracle arm
  with a stated reference model, must be answered correctly. For paired questions the
  check is stronger: the reference model must answer both arms correctly *and its answer
  must flip between them* — a pair whose oracle answers do not flip has no signal. The
  check runs before corpus freeze; model, date, and per-question outcome are recorded in
  corpus metadata. A question the ceiling cannot answer measures nothing.
- **V2 — Non-inferability, operationalized.** Every question, run with *zero* context:
  `k = 10` samples from the reference model at a stated temperature; the question is
  rejected if **2 or more** samples produce the gold answer (within the question's
  tolerance). Model, seed, temperature, and per-question sample outcomes are stamped
  into corpus metadata, so the check is re-runnable. Answers are arbitrary where they
  could be guessable: names, numbers, dates, and orderings are drawn randomly by the
  generator, never chosen for narrative plausibility. This is the "trick must be
  arbitrary rather than inferable" rule with a reproducible measurement attached.
- **V3 — Distractor plausibility, with a gold-ablated probe.** Non-gold sessions are
  same-domain and same-register as gold ones (other reminders with other dates, other
  people's pets, other purchases); generators enforce domain overlap, and the BM25
  calibration gate (§4) is what demonstrates the distractors actually *compete*. The
  answer-leak failure mode — a distractor that accidentally contains or paraphrases the
  gold — is caught by the dual of V1: every question is run through the reference model
  with **non-gold sessions only** as context, and the gold answer must *not* be
  produced. (The zero-context probe cannot catch this — it never sees the distractors.)
- **V4 — No absolute dates in message content** for any time-dependent vertical
  (Prospective, and the duration questions of Arithmetic): the
  `agenteval-timegrounded-v1` rule, inherited verbatim, enforced by the same generator
  checks and corpus tests (no four-digit years, no absolute dates; all temporal
  expressions relative to session timestamps).
- **V5 — Gold derivable from the corpus alone.** Generators derive every gold answer
  from the sessions they emitted (the tg generator's "arithmetic in the answers cannot
  drift from the arithmetic in the conversations" property), never accept a typed one.
- **V6 — Component non-redundancy.** For multi-component questions (Arithmetic inputs,
  Forgetting's statement + invalidation), a leave-one-out probe: with any single gold
  component ablated, the reference model at otherwise-full context must not produce the
  gold within tolerance. Without this, per-component coverage echo (§7) reports
  components as load-bearing that are not, and partial-retrieval outcomes become
  uninterpretable.

Generation methodology is part of the deliverable: one generator script per vertical
(`tools/gen_typedmemeval_<vertical>.py`, following `gen_timegrounded_corpus.py`), each
with built-in verification of its vertical's rules, each documented well enough that the
corpus is reproducible and criticizable. The generators and their verification output are
how this benchmark earns the right to be argued with.

#### 5.1 TypedMemEval-Prospective (50 questions)

What it measures: whether a system places remembered facts *in time* — things due later,
validity that expires, assertions not yet true — under the timestamps-only rule, so
nothing can be read out of printed dates.

The existing `agenteval-timegrounded-v1` corpus is the seed: its 12 questions are carried
into this corpus with new `tme-pro-*` ids and `seeded_from` lineage fields. The tg corpus
itself is shipped, frozen API and does not change; the citation rule (§2.5) covers the
overlap. The carried as-of/current questions belong here because the vertical is
clock-dependent recall, of which strict prospection is the sharpest case.

| Shape | Questions | Structure |
|---|---|---|
| Seed carry-over (as-of 4, current 4, prospective 4) | 12 | as in tg v1 |
| Due-later reminders | 16 | 8 before/after pairs |
| Expiring validity | 12 | 6 before/after pairs |
| Not-yet-true assertions | 10 | 5 before/after pairs |

A **before/after pair** is two questions with the same haystack and linked `pair_id`,
differing only in `question_date`: one queried before the pivotal instant, one after.
Gold *flips* between arms by construction (before: "not yet / nothing due"; after: the
thing). Pairs are the vertical's teeth: a system that answers the after-arm correctly but
also "fires" on the before-arm is **premature**, and only a pair can see that.

Pairs only work if two mechanics hold, so the design states them rather than assuming
them:

- **Query time must actually reach the agent.** Under `TimestampsOnly` grounding the
  arms' observable input differs *only* in the query instant, so the harness's existing
  typed channel is load-bearing: `TimestampedConversationHistory.QueryTime`, which the
  shipped adapter emits as a query-time system message. An agent that ignores that
  channel (e.g. reads the wall clock) will answer both arms identically — which is a
  *finding about that agent*, and the pair-consistency report (§6) makes it visible as a
  systematic both-arms-same pattern rather than letting it masquerade as random error.
  The corpus-side precondition — that the arms are genuinely distinguishable through the
  channel — is what V1's pair-flip oracle check certifies before freeze.
- **Arms are independent runs.** Each arm is an ordinary question with its own haystack
  copy, executed under the runner's standard per-question session reset — the same
  isolation rule §5.4 imposes for rehearsal, applied here because a before-arm query
  could otherwise write retrieval traces that contaminate the after-arm.

Vertical validity rules: every pivotal instant lies strictly between the two arms'
query times; every gold in a before-arm is an explicit not-yet; V1's pair-flip check and
V4 apply corpus-wide. Dispersion: reminders/validity stated in 1–2 sessions
(`G` ∈ {1,2}), so the structural ceiling here is 1.0 and non-saturation comes entirely
from the §4 calibration gate; haystack `H` = 12–20 same-domain sessions per question
including 3+ distractor reminders with other due dates. Grounding:
`TemporalGroundingMode.TimestampsOnly` required (probe), with a control arm over the
**same corpus** via an options pair (`ProbeOptions`/`ControlOptions`,
`TimestampsAndText`, the `-control` `DatasetMode` label) — exactly the tg mechanism: one
corpus file, one hash, two option sets, never a second corpus.

#### 5.2 TypedMemEval-Episodic (50 questions)

What it measures: memory of the conversation *as an event* — who said what, in what
order — rather than facts extractable from any single message.

| Shape | Questions | Gold lives in |
|---|---|---|
| Assistant-stated answers | 20 | an assistant turn (the user never states it) |
| List-order | 15 | the *sequence* of sessions, each item in its own session |
| Participant attribution | 15 | which speaker said it |

An interpretation is being made here, visibly: the ask's phrase is "participant
*attributes*", and this design reads it as speaker **attribution** — who said it —
because that is the episodic construct ("memory of the conversation as an event"),
whereas attributes *of* participants (someone's role, allergy, employer stated
in-session) are content recall, already covered by WorkingMemory's stable-profile facts.
If the consumer meant the attributes reading, the shape changes; §10 carries the
question, and the review settles it before generation.

Vertical validity rules: for assistant-stated, the user's turns must not restate the
answer anywhere in the corpus — checked by a **stated leak screen**, not a hand-wave:
a lexical n-gram screen, then an LLM paraphrase screen (model, prompt, and per-question
verdicts stamped into corpus metadata), then a human audit of a documented sample.
Paraphrase detection is semantic, so residual leakage is listed as a known limitation
rather than claimed impossible. For list-order, the correct order is arbitrary (V2) and
derivable only from session sequence, never from narrative logic (no "starter before
dessert" orderings), with each item mentioned in exactly one session; for attribution,
the statement must be plausible from either speaker (a fact only an assistant would say
is inferable, violating V2 in spirit) — the generator emits matched statement templates
usable by both roles and assigns the speaker randomly.

Dispersion: list-order questions have `G` = list length (4–7), the family's highest
dispersion — and at `K_ref` = 5 the longest lists are *structurally* un-completable
(ceiling 5/7), so binary correctness there would measure the budget, not ordering.
List-order is therefore scored **conditionally on coverage**: pairwise-order accuracy
over the items whose sessions were actually surfaced (§6), reported next to the
question's realised coverage, so a budget-5 system is measured on the ordering of what
it saw. Assistant-stated and attribution have `G` = 1 with `H` = 15–25 — precision
questions, non-saturation via the §4 gate. Grounding: any injection mode; timestamps
recommended (order signal), and the corpus guarantees session order matches timestamp
order so neither channel contradicts the other.

#### 5.3 TypedMemEval-Arithmetic (50 questions)

What it measures: derived answers — the memory system must surface *all* inputs and the
answer model must combine them. Isolated for the first time: every question records its
**gold derivation** (inputs with session indices, operation, value, unit) in the
`typedmemeval.derivation` block, so the judge scores the *arithmetic*, not the phrasing,
and failure is attributable (§7).

| Shape | Questions | Operation |
|---|---|---|
| Counts | 14 | count of matching events across sessions |
| Sums | 14 | sum of stated values |
| Deltas | 10 | difference between two stated values |
| Durations | 12 | time between events, from session timestamps (V4 applies) |

Vertical validity rules, per operation because they differ in kind:

- **Counts — predicate decidability, not subset-coincidence.** (A subset-coincidence
  rule is vacuous for counts: every same-size subset of matching events "sums" to the
  same value.) The counting predicate is stated in the question ("how many times did I
  *order from* Meridian", not "how many times did I mention it"), and the generator
  emits, for **every candidate event in the haystack** — gold and distractor alike — a
  labeled matches/does-not-match record, so the predicate's extension is decidable and
  auditable, and the V1 oracle run confirms the reference model agrees with it. No
  borderline events: an event either matches the predicate on its face or is clearly
  outside it.
- **Sums, deltas, durations — no coincident combination.** No other subset of same-unit
  values in the question's haystack — drawn from gold and distractor sessions **mixed**,
  because a system that substitutes one distractor value and still lands on gold is the
  case that corrupts evidence attribution — combines under the question's operation to
  the gold value or within its tolerance. Asserted by the generator over all subsets up
  to size `G + 1` (one larger than gold, to kill the nearest add-one/drop-one
  coincidences; larger coincidences are noted as a residual risk, not claimed away).
- **Tolerances are typed** — counts and sums of exact inputs are exact, durations carry
  a stated unit granularity — and the *numeric normalization* the judge applies to
  conversational answers ("$128.75" vs "roughly 129 dollars") is a stated §6 rule
  ratified in review, never template discretion.
- Every input value is stated exactly once; V6's leave-one-out probe certifies each
  input is genuinely load-bearing.

Dispersion: this is the vertical where coverage bites hardest by design — inputs are
spread one per session, `G` ∈ {3..6}, so a missed input does not degrade the answer, it
*wrongs* it. `H` = 15–25 with same-unit distractor values (V3). Structural ceiling at
`K_ref` = 5: 0.83–1.0 across the `G` distribution; the sub-1.0 mass of the family's
ceiling table lives here and in list-order. Grounding: duration questions derive gold
from session timestamps, so the vertical **requires a timestamp-bearing injection
mode**; under a timestamp-free mode the runner reports the 12 duration questions as
*unrun* (skipped-with-notice, per the §3 validation list) rather than letting harness
artifacts count as Wrong.

#### 5.4 TypedMemEval-WorkingMemory (48 questions)

What it measures: recall of stable profile facts as a function of *distance* — how many
sessions of same-domain interference sit between statement and query.

Structure: **12 fact families × 4 distances** (`d` ∈ {1, 5, 15, 40} intervening
sessions) = 48 questions. Each (family, distance) cell is an **independent question with
its own haystack** — fact stated once in session 1, `d` interference sessions, query.
Distance is encoded per question in `typedmemeval.distance_sessions`, and results report
the outcome × distance curve, not an aggregate (an aggregate over a distance ladder is
exactly the "one percentage" this family exists to refuse).

The independent-haystack rule is a deliberate design guard (§9, item 6): probing the
*same* stored fact at increasing distances would let each probe rehearse the memory —
retrieval-augmented systems typically re-write what they retrieve — so later distances
would measure refreshed memory, not aged memory. Independence costs corpus size and buys
a clean independent variable.

Vertical validity rules: the fact is stated exactly once and never restated or
paraphrased in any interference session — checked by the same stated leak screen as
Episodic (§5.2: lexical n-gram + recorded LLM paraphrase screen + audited sample),
because a paraphrased restatement in an interference session silently converts an aging
measurement into a rehearsal measurement; interference is same-domain (other people's
employers, other pets — V3) and never contradicts the fact (contradiction is the
Forgetting vertical's variable, and blending them would confound both); facts are
arbitrary (V2).

Two construct decisions, stated because a careful reader will otherwise find them as
bugs. First, **gold position is pinned, not shuffled**: the fact sits in the first
session by design, so this vertical carves itself out of the §4 shuffle check — and
that means distance is deliberately confounded with absolute position and recency; that
composite ("how far back the memory sits") *is* the construct, and the ADR names it
rather than pretending session-count distance was isolated from recency bias. Second,
**timestamp spacing is fixed**: intervening sessions are spaced at a constant
inter-session interval, identical across all cells of the grid, so distance-in-sessions
and distance-in-time cannot vary independently — without this, two `d` = 15 cells with
different timestamp spreads would be incomparable for any time-decay-aware memory
system.

Dispersion: `G` = 1 always, so the structural ceiling is 1.0 and per-question coverage
is binary — this vertical leans entirely on the §4 calibration gate and the ladder
itself. The independent variable is `H` = `d` (interference sessions; the gold session
is counted by `G`, per §4's definition). At `d` = 40 the haystack alone exceeds any
small budget, making this the family's cleanest retrieval-precision ladder — with
`n` = 12 per rung published as the diagnostic stratum it is (§5 floor rule).

#### 5.5 TypedMemEval-Forgetting (50 questions)

What it measures: whether a system knows what it *no longer* knows. The gold answer to an
invalidated fact is "no longer valid/known (and here is why)" — distinct from never-known
abstention, and the judge is required to hold the three states apart.

| Shape | Questions | Gold behaviour |
|---|---|---|
| Invalidated facts | 20 | "no longer …" citing the invalidation |
| Still-valid controls | 15 | the value (paired with invalidated shapes via `pair_id`) |
| Never-known probes | 15 | never-known abstention (`_abs` ids — the one place the suffix is used) |

The three shapes form a discrimination test, and *each* failure direction gets its own
named sub-count so the catch is verifiable from the published numbers. Invalidated-only
would reward a system that answers "no longer known" to everything; the still-valid
controls catch that (**over-forgetting**), and the never-known probes catch a system
that cannot tell "I lost it" from "I never had it" — a system that claims a forgetting
history it never had (**mis-attributed forgetting**: "no longer known" on a never-known
probe) is confabulating a memory event, a fabrication-shaped error, and it is counted as
such rather than dissolving into generic Wrong (§6).

Each invalidated question carries **two gold components** in
`typedmemeval.gold_components`: the statement session and the invalidation session. This
matters because the vertical's signature failure is asymmetric retrieval — surfacing the
statement but not the invalidation yields a *confident stale answer*, the most dangerous
outcome the family can produce (it is a fabrication-shaped error, and the project's
standing grading rule is that fabrications are complete failures, weighted accordingly in
reporting). Coverage echo is therefore per-component here (§7): "retrieved statement but
not invalidation" is a first-class diagnostic, not a footnote.

Vertical validity rules: exactly one invalidation event per invalidated fact, no
re-validation later in the haystack; statement strictly precedes invalidation in both
session order and timestamps; the invalidation is explicit (a stated event — "sold the
car", "we cancelled" — never an implication); V1 applies with the three-state judge (§6):
the oracle at perfect context must produce "no longer", which also validates the judge
template before freeze.

Dispersion: `G` = 2 (the two components), placed 4–15 sessions apart, `H` = 15–25 —
structural ceiling 1.0, non-saturation via the §4 gate; the §4 shuffle check applies
here *subject to* the component-order constraint (statement before invalidation is
pinned; everything else shuffles). V6's leave-one-out probe applies to both components.
Grounding: timestamps recommended; order is additionally guaranteed by session sequence
so the vertical works under any injection mode.

### 6. Typed outcomes

Never one percentage. Every family result reports, per vertical and per shape, with `n`:

```
correct / wrong / abstained / missed        (+ premature, Prospective only)
```

**Proposed semantics — a review decision point** (§10, Q2), because "missed" is the one
term in the ask with two defensible readings. The proposal is two orthogonal axes, so
neither reading is lost:

**Axis 1 — verbal outcome** (from the judge; always observable):

| Outcome | Meaning |
|---|---|
| `Correct` | matches gold (for Forgetting-invalidated: says "no longer", acknowledging invalidation) |
| `Wrong` | commits to an incorrect value (incl. Forgetting stale recall — reported also as its own sub-count, because stale recall is the dangerous-error class) |
| `Abstained` | explicit uncertainty — "I don't know / I have no record" |
| `Missed` | confident false negative — asserts *nothing is there* ("nothing is due", "you never told me") when gold says otherwise |
| `Premature` | Prospective before-arms only: asserts the not-yet-true thing as already true / fires the reminder early |

`Abstained` vs `Missed` is the uncertainty/denial line; both differ from `Wrong`, which
asserts a value. On never-known probes and correct before-arms, the "negative" answer *is*
gold and is scored `Correct` — outcomes describe deviation from gold, not surface form.

**Precedence for mixed answers, fixed here so the judge templates implement semantics
rather than invent them** (the 0.19 judge-hardening lesson: undefined edge cases
re-import free-text discretion into a structured protocol):

1. **A stated value outranks hedging.** "I'm not sure, but probably X" is scored on X —
   `Correct` if X matches gold, `Wrong` if not — never `Abstained`. `Abstained` requires
   declining to commit to any value.
2. **Forgetting: the invalidation acknowledgment governs.** "It was a Honda, but you
   sold it" is `Correct` — recalling the stale value *while marking it invalid* is ideal
   memory, not stale recall. Stale recall (`Wrong`, dangerous-error sub-count) is
   asserting the stale value *as current*. Saying "no longer known" on a never-known
   probe is the **mis-attributed forgetting** sub-count (§5.5), reported under `Wrong`.
3. **List-order is scored conditionally on coverage** (§5.2): pairwise-order accuracy
   over the items whose sessions were surfaced, with a stated correctness threshold
   (proposed: all observed pairs ordered correctly → `Correct`; any inversion →
   `Wrong`), reported next to realised coverage. A partial list with correct relative
   order is not garbage and is not scored as garbage.
4. **Arithmetic numeric normalization is a stated rule, not template discretion:** the
   judge extracts a numeric value under declared parsing rules (currency symbols,
   magnitude words, decimal separators); an exact extracted value scores against the
   typed tolerance; an answer offering only a rounded value is correct iff gold rounds
   to the offered precision. The parsing rules ship in the ADR-ratified template, pinned
   by the family judge fingerprint.

**Axis 2 — evidence attribution** (only when observable): `EvidencePresent`,
`EvidenceAbsent`, or `Unobserved`, computed from the per-question evidence envelope (§7)
against the question's gold components — and named for what it *is*: reference-level
presence, **necessary but not sufficient**. A gold session id in the answer context does
not prove the gold value survived the system's summarization or truncation, so the
causal readings are stated as inferences with one certain direction: `Wrong` with
`EvidenceAbsent` **is** a retrieval-side failure; `Missed` with `EvidencePresent` is
*consistent with* a synthesis failure but could be a compression loss inside the memory
store. Where the envelope carries content (`EvidenceCaptureMode.Full`), an optional
content-level check (gold value present in the surfaced text) upgrades the inference,
and the report says which level was used — the same "which list was used" honesty §7
applies to coverage. For Arithmetic, presence is per-input, so "wrong sum, missing
exactly the un-retrieved input" becomes visible. When no telemetry is supplied,
attribution is `Unobserved` and reported as such — never guessed.

**Judge:** a family-owned `TypedMemEvalJudge` with per-vertical structured templates
(structured-JSON protocol mandatory, §3), its own pinned prompt fingerprint, disjoint
from the frozen LongMemEval judge fingerprint. The verdict schema returns the axis-1
outcome directly (`correct | wrong | abstained | missed | premature` as
vertical-appropriate), replacing binary yes/no. Arithmetic templates receive the recorded
derivation and judge the number under the typed tolerance; Forgetting templates receive
the three-state instruction and the invalidation reference. Judge-robustness surfaces
from 0.19 (structured protocol, retry accounting, raw-response retention) apply
unchanged.

**Result surfaces** (additive, nullable — the `AnswerSampling` precedent):

- `ExternalBenchmarkResult.TypedOutcomes : TypedMemEvalReport?` — vertical, corpus id +
  revision, `K_ref`, per-shape outcome counts with `n` (published as diagnostic strata,
  §5), stale-recall / over-forgetting / mis-attributed-forgetting sub-counts
  (Forgetting), outcome × distance table (WorkingMemory), pair-consistency counts
  (Prospective: both-arms-correct / premature / missed-after / both-arms-same),
  evidence-attribution counts with the observed share and the attribution level used,
  and realised-coverage distribution summary. (Property named `TypedOutcomes`, not
  `TypedMemEval`, following the existing surface's content-named convention —
  `AnswerSampling`, `OracleProjection` — and avoiding a member that shadows the
  `AgentEval.Memory.External.TypedMemEval` namespace in qualified references.)
- `QuestionResult.TypedOutcome : TypedMemEvalQuestionDetail?` — outcome, attribution,
  realised gold coverage (per component where components exist), `pair_id`/`arm`,
  `distance_sessions`, `seeded_from`.
- A `TypedMemEvalEvalResultAdapter` projecting `typedmemeval.*` dimensions into the
  standard EvalResult pipeline, sibling to the LongMemEval adapter.

Existing binary fields (`CorrectQuestions`, `OverallAccuracy`, per-type accuracy) remain
populated — `Correct` maps to correct, everything else to not-correct — so generic
tooling keeps working. This is a deliberate, registered deviation from "never one
percentage" (§9, deviation 5): the number exists in the JSON for compatibility, and the
citation rule plus the family documentation state that the typed vector is the only
citable form of a TypedMemEval result.

**Bands, not points:** family documentation reports outcome rates as bands over
**≥ 2 constant-configuration runs — recommending 3** (§9, pushback 3), with per-question
flip counts. A `TypedMemEvalRunSet.Summarize(results)` aggregator computes bands and
**refuses** to band across runs whose corpus SHA-256, options fingerprint, or answer-
sampling dispositions differ — banding non-comparable runs would manufacture false
stability, the exact failure the answer-pinning work exists to prevent. It composes with
`AnswerSeed`: pin the seed to measure the memory system's own variance; vary it to
measure the answer model's.

### 7. Realised coverage, echoed per question

The oracle arm's reporting style — requested next to realised — applied family-wide:

- **Oracle runs:** exact by construction; `OracleProjectionReport` (gold kept of total,
  distractors added of requested) is already the shipped surface and is reused unchanged.
- **Opaque-agent runs:** computed from the **existing evidence channel** — 
  `EvidenceCaptureMode.References` and the adapter-supplied `QuestionEvidenceEnvelope`
  (the `agenteval.question_evidence.v1` additional-properties contract). No new
  capability interface: the channel, its bounds, and its validation already exist and are
  consumed. The family adds derived diagnostics: **realised gold coverage** = gold
  sessions present in `AnswerContext` ÷ `G` (falling back to `Retrieved` when the adapter
  supplies no answer-context list, and saying which list was used), per gold *component*
  for Forgetting, per *input* for Arithmetic. These extend `QuestionEvidenceDiagnostics`
  additively (`GoldSessionPresent` and `FirstGoldRank` stay untouched).
- **No telemetry supplied:** coverage is `null`, attribution `Unobserved`, and the
  run-level report states the observed share — a run that cannot see coverage must say
  so rather than print blanks that read as zeros.

`AnswerSeed`/`AnswerTemperature` are honored by the family runner exactly as in the
LongMemEval runner — same coordinator, same seven-value disposition, same
provider-rejection = question-failure rule, echoed into the same `AnswerSampling` report.

### 8. What this family does not do

- **Does not touch LongMemEval.** No shipped LongMemEval surface changes. The tg corpus
  stays where it is, frozen.
- **No Procedural vertical** — consumer-side by agreement (needs tools; agentic).
- **No cross-family composite score.** There is deliberately no "TypedMemEval overall
  number": the verticals measure different mechanisms and a blend would rebuild the one
  percentage the family exists to replace.
- **No leaderboard claims.** v1 is an instrument for comparing configurations of one
  system and for regression-testing memory mechanisms — with 30–50 questions per
  vertical, cross-system ranking claims need the bands of §6 and honest `n` reporting,
  and the documentation will say exactly that.

### 9. Where this design pushes back

Per the ask's own rule — "if any ask is wrong for AgentEval's shape, say so in the design
rather than building a variant" — five asks are reframed or deviated from, with reasons:

1. **"Corpora sized so realised gold coverage lands ~0.5–0.9 by construction."**
   Realised coverage is a property of system × corpus; no corpus can place an arbitrary
   system in a band, and — being precise about the arithmetic — structural `K/G`
   ceilings below 1.0 exist only where the vertical's mechanism disperses evidence
   (Arithmetic, list-order); for the `G` ∈ {1,2} verticals the ceiling is exactly 1.0
   and cannot be engineered otherwise without changing what those verticals measure.
   What §4 builds instead: published ceiling tables where dispersion exists, and a
   **calibration gate** everywhere — a corpus does not freeze until a deterministic
   reference retriever at the declared budget realises coverage inside the declared
   ~0.5–0.9 band, with per-question values stamped and the realised number echoed at
   run time. That is the buildable form of the ask; the literal guarantee is not
   honestly buildable, and this ADR says so rather than shipping a number that pretends
   otherwise.
2. **"One public runner following the tg runner pattern."** Followed in every respect
   except placement: the tg runner is a partial of `LongMemEvalBenchmarkRunner`, and
   putting the family there would hang five corpora off the LongMemEval name —
   contradicting ask 1, which is the controlling ask. `TypedMemEvalRunner` is a separate
   public class reusing the same internal pipeline (§3).
3. **"Bands over ≥ 2 runs."** Accepted as the floor, with a recommendation of 3: two
   runs can agree by coincidence and band to zero width, overclaiming stability — the
   consumer's own 13-of-14-flips evidence shows run-pair agreement is not run-set
   stability. The aggregator reports flip counts alongside bands so a zero-width band
   over a small run set is legible as weak evidence, not strong.
4. **"n ≥ 30 each."** Accepted as a per-vertical claim floor, met by all five corpora
   (48–50). Not silently extended to the strata inside a vertical: shapes, pair sets,
   and distance rungs sit at n = 5–20, every cell's `n` is published, and the design
   labels those numbers diagnostics, not claims (§5) — because 30 questions of 15 pairs
   is n = 15 for every pairwise claim, and pretending per-cell rigor the sizes cannot
   deliver would be the quiet version of the one-percentage failure. Sizing any cell to
   its own claim floor is a costed §10 option, not a v1 promise.
5. **"Typed outcomes, never one percentage" — one registered exception.** Family
   results keep the pipeline's existing `OverallAccuracy`/`CorrectQuestions` fields
   populated so generic tooling does not break (§6). The number is compatibility
   surface, not a citable result, and the citation rule says so explicitly. The
   alternative — nulling the legacy fields — was rejected because a result that crashes
   half the consumer's tooling gets re-derived by hand anyway, without the citation
   rule attached.

And one design guard the ask did not mention but the review should ratify:

6. **WorkingMemory rehearsal confound.** Querying one stored fact at increasing
   distances lets each query refresh the memory. §5.4 uses independent per-question
   haystacks so distance measures aging, not rehearsal.

### 10. Open questions for the joint review

1. **Name:** keep TypedMemEval, or switch to FacetMemEval over the .NET homonym (§1)?
   Everything else is name-independent; the rename cost is zero before implementation
   and never zero again after.
2. **`Missed` semantics:** ratify the two-axis proposal (§6), or collapse to a single
   judge-side or evidence-side definition? The corpus format is unaffected either way;
   the judge templates and report shapes are not.
3. **"Participant attributes":** §5.2 reads the ask's phrase as speaker *attribution*
   (who said it) on episodic grounds, since attributes-of-participants recall is
   WorkingMemory's territory. Confirm the reading, or the 15-question shape changes.
4. **`K_ref`:** is 5 sessions the right reference budget, and should it be per-vertical?
   It should approximate the consumer's real retrieval budget to make the calibration
   gate meaningful (§4).
5. **Control arms:** ship probe/control *option pairs* (tg-style: same corpus, same
   hash, `-control` `DatasetMode` label) for all verticals or only Prospective
   (currently: only Prospective — the others have no in-text date scaffolding to
   control for)?
6. **MemoryBaseline conversion:** the existing `ToBaseline` extension is
   benchmark-neutral and would already accept a family result today (it keys on
   `BenchmarkId` and the `OverallAccuracy` §6 keeps populated) — so the mechanical
   question is settled, and the real one is whether the family should *endorse* a
   single-score baseline pentagon or require a typed-outcome-aware mapping before one
   is published.
7. **Per-cell sizing:** should any diagnostic stratum be promoted to a claim cell and
   sized to n ≥ 30 in v1 — the WorkingMemory distance curve being the strongest
   candidate (≈ 120 questions for that vertical alone) — or does that wait for a v2
   revision once v1 shows which cells carry signal?
8. **Phasing for Prompt 06** (proposal): **M1** family skeleton — runner, options
   façade, judge, typed outcomes, report surfaces, both guard tests, and the
   **calibration-gate retriever** (it comes first because no corpus in any milestone
   can freeze without it, §4), plus TypedMemEval-Prospective (tg-seeded, the only
   vertical with a seed corpus to validate the pipeline against). **M2** Episodic +
   WorkingMemory (no new grounding machinery). **M3** Arithmetic + Forgetting
   (derivation-aware and three-state judge templates — the two new judge behaviours) +
   `TypedMemEvalRunSet`. Each
   milestone independently shippable; the family is not announced until all five
   verticals exist, so no release ships a two-vertical "family."

### 11. Implementation inventory (for Prompt 06 sizing — nothing here is built)

| Piece | New / extended | Est. shape |
|---|---|---|
| `TypedMemEvalRunner`, `TypedMemEvalVertical`, `TypedMemEvalOptions` | new | runner façade over existing pipeline |
| `TypedMemEvalJudge` + 5 vertical template sets + pinned fingerprint | new | judge, structured-only |
| `TypedMemEvalReport`, `TypedMemEvalQuestionDetail`, outcome enums | new | additive result models |
| `TypedMemEvalRunSet` band aggregator | new | pure function over results |
| `TypedMemEvalEvalResultAdapter` | new | sibling of LongMemEval adapter |
| Corpus constants classes ×5 (Prospective carries `ProbeOptions`/`ControlOptions`) | new | tg-corpus pattern each |
| Corpora ×5, embedded, `eol=lf` (control arms are option pairs, not extra corpora) | new | ~250 authored questions |
| `tools/gen_typedmemeval_<vertical>.py` ×5 + calibration-gate retriever | new | tg-generator pattern |
| `QuestionEvidenceDiagnostics` realised-coverage fields | extended (additive) | nullable additions |
| `ExternalBenchmarkResult.TypedOutcomes`, `QuestionResult.TypedOutcome` | extended (additive) | nullable, `WhenWritingNull` |
| Serialization guard test, prompt-leak guard test (no `typedmemeval` block in any assembled prompt; derivation-literal check scoped per §3), corpus structural tests, V1–V3/V6 probe records | new | CI |
| Docs: `docs/benchmarks/typedmemeval/` getting-started + citation rule | new | after implementation |

### 12. Implementation record (v0.22.0-beta)

Where the built thing differs from the design above, with reasons. Nothing here changes what the
benchmark measures; each is a placement or scoping decision the design did not settle.

1. **Result models live in `AgentEval.Memory.External.Models`**, not the family namespace. Every
   peer report type on `ExternalBenchmarkResult` (`AnswerSamplingReport`, `OracleProjectionReport`,
   `TemporalGroundingReport`) lives there, and it removes the member/namespace shadowing §6 flagged.
   The family's *machinery* is in `AgentEval.Memory.External.TypedMemEval` as designed.
2. **One `TypedMemEvalCorpus` class keyed by vertical**, rather than five near-identical constants
   classes. Same capability, one place to change; `ProspectiveProbeOptions`/`ProspectiveControlOptions`
   sit on it.
3. **Realised coverage lives on `TypedMemEvalQuestionDetail`, not on `QuestionEvidenceDiagnostics`.**
   §7 proposed extending the shared diagnostics; §6 put it on the family detail. The family surface
   won because coverage is computed against *family* gold components that only the family knows, and
   a field on the shared type would imply LongMemEval runs populate it. Carrying it in both places
   would invite the two to drift.
4. **The extension block carries more than §3's sketch.** `shape` is required — §6 mandates
   per-shape reporting and nothing else could key it. Verticals added what their own scoring needs:
   `list_order` (items, presented order, session indices), `count_predicate` + `candidates`,
   `fact_family`, `stated_by`. All of it is answer-key material and all of it is covered by the
   prompt-leak guard.
5. **Arithmetic `delta` is the difference of two side-totals**, and duration inputs carry a
   `from_session_index`. §5.3 pins `G` ∈ 3..6 with one input per gold session, which a two-value
   delta cannot reach; a side-total delta keeps both the dispersion and the operation.
6. **V6 is scoped to Arithmetic and Forgetting**, the two verticals §5 defines it for. Applied
   corpus-wide it fired on Prospective's incidental two-gold questions, where the design never
   claimed both components were individually load-bearing.
7. **`TypedMemEvalOptions.IncludeTimestamps` was added** so §5.3's unrun path is reachable and
   testable rather than dead code.
8. **`TypedMemEvalEvalResultAdapter` takes an explicit `passThresholdPercent`.** `EvalScore.Passed`
   is not nullable and the family defines no pass mark, so the threshold is stated by the caller
   rather than hidden in the adapter.

**What a pre-merge review caught, after the probes had passed.** A four-lens adversarial review of
the finished implementation returned 22 findings, and the most serious invalidated all five corpora:
the calibration clause the gate appends to distractors was never appended to gold. Gold carried it
0 times in 501 sessions against ~99% for distractors, so `clause absent` isolated every piece of
gold evidence in every corpus with a one-line string filter. Nothing in the design forbade it,
every structural check passed, the BM25 gate was satisfied, and all four validity probes were green
— because none of them ask whether the evidence is *separable*, only whether it is *necessary*. The
fix had to give gold the same clause built from other questions' vocabulary: the obvious version,
echoing the question's own words, removed the tell but handed gold the query's keywords and pushed
every corpus through the calibration ceiling. Parity is now a generator rule and a CI test.

The same review found the whole not-yet-true shape asserting an event had happened from evidence
that only stated a plan, a malformed template in all twelve expiring-validity questions, carried
gold pinned to the tail of its haystack while metadata claimed shuffled, a pair-consistency metric
that measured the opposite of what it documented (outcomes are gold-relative and gold flips, so a
time-blind system produces *different* labels — the test asserting it scored 19/19 on a run with no
time-blindness at all), a run that measured nothing projecting as a hard 0.0, a coverage mean 30%
composed of vacuous 1.0s, and a documented CI check that did not exist. The lesson worth keeping is
that the probes and the review catch disjoint classes: the probes ask whether a question is
answerable and its evidence load-bearing, and no amount of that notices that the evidence is
labelled.

**What the validity probes actually caught.** The first Prospective generator computed every due
date from an anchor timestamp that was then overwritten when the haystack was shuffled and
re-stamped, so all 38 generated pair questions named dates their own conversations could not
produce. Every structural check passed — none of them re-did the arithmetic — and the V1 oracle
probe failed 38 of 50 while all 12 hand-authored seed questions passed. The generator now derives
the pivot from the session's final timestamp and re-checks the arithmetic as a hard rule. This is
the clearest available argument for the probes existing at all: the corpus looked correct, its own
generator believed it was correct, and it was wrong.

### 13. V7 — adversarial separability, and the Episodic attribution decision (post-0.22.0-beta)

**V7 is now a standing rule.** The clause-parity check added during the 0.22.0-beta review was
specific to one marker string, and the consuming project's verification round asked the right
question: is there a probe that would catch the *next* tell, which will not be a clause? There was
not. There is now.

V7 tries cheap single-feature classifiers at separating gold sessions from distractors — session
length, turn count, position, digit density, capitalisation density, and recurring boilerplate
phrases — and scores each as a direction-folded AUC. It refuses a corpus at 0.75 on any shape
feature, runs in the generator, is stamped into every corpus's metadata beside V1–V6, and is
re-measured in CI rather than trusted from the record.

It found real separability in **all five shipped corpora on its first run**: capitalisation density
reached 0.990 in Forgetting and 0.925 in Arithmetic, session length 0.955 in Episodic and 0.992 in
WorkingMemory. Phrase recurrence is reported at 0.583–0.850 and does not refuse (see below). The cause was structural rather than accidental — gold states an arbitrary *named*
fact because V2 requires the answer to be unguessable, so gold carries proper nouns and extra text
that filler does not. A central shape-parity pass now pads every session to a common length and
capitalisation density using invented names built from syllables that appear in no question; the
shape converges, the content cannot. Worst refused feature across the family is now 0.713.

Two things V7 does **not** refuse, both stated rather than quietly excluded:

- **Question relevance is exempt and is not a feature.** Gold is supposed to be more relevant to its
  question than a distractor is — if it were not, the question would be unanswerable. How *easily*
  that is exploited is bounded by the BM25 calibration gate, a different instrument for a different
  question.
- **Phrase recurrence is measured but does not refuse** (0.552–0.850 across the family). Filler is
  template-generated, so every filler phrase recurs across questions and no gold phrase does, and a
  phrase-match classifier separates them at an AUC that says "this filler came from templates"
  rather than "this corpus hides a tell". Driving it to chance needs filler with the variety of real
  conversation, which is a corpus revision and not a check. Recorded at full value so it can be
  argued with.

**WorkingMemory exempts `position_in_haystack`.** It pins its fact to session 0 by design (§5.4), so
position separates gold perfectly and is meant to — the construct is how far back the memory sits.
Declared as an exemption with a reason rather than accommodated by raising a threshold.

**Episodic attribution: a varied-template regeneration is planned for corpus revision v3**, not
permanent by design. The shape currently emits its statements from a fixed frame, so a system
storing no speaker label can recover the answer from the framing rather than from memory, and its
numbers are a floor rather than speaker-attribution accuracy. That is a corpus fix of the same kind
as the shape-parity pass above, and it ships with the next corpus revision (v3) — the same one that
addresses phrase recurrence, since both need richer generated language and neither is worth a corpus
regeneration on its own. The v2 revision in this release is what the separability finding forced;
v3 is what the two remaining known limitations are queued behind.

### 14. Corpus v3 — the separability check was measuring the wrong thing (round-3 review)

V7 was added in §13 because a corpus-invalidating tell had survived every structural check
and all four validity probes. The v2 corpora it certified carried the same class of defect,
and V7 passed them. It was wrong in two independent ways, and each on its own was enough.

**The statistic paired the wrong sessions.** V7 pooled every (gold, distractor) pair in the
corpus, including pairs drawn from different questions. The attacker being modelled is handed
one question's haystack and asked which session holds the evidence, so the only comparisons
that mean anything are between sessions competing with each other. Pooling diluted the number
two ways. Session lengths drift across questions, so a tell pointing the same way inside every
question averages out: Forgetting's gold was consistently at the short end of its own haystack,
which reads as 0.616 pooled and **0.903 within**.
And questions with no gold at all — Forgetting's 15 never-known probes — contribute
distractor-only values that pair against every other question's gold, so a pooled metric gets
*better* the more abstention questions a vertical adds. That one is true by construction rather
than by measurement: those sessions can only ever appear on the distractor side of a pair.

**The refused-feature list was a list of the tells we had already thought of.** It covered
length, turn count, position, digit density and capitalisation density — and nothing else. It
did not cover punctuation, sentence structure, turn shape, vocabulary variety, or phrase
markers. Measured over the shipped v2 corpora:

| Classifier | Vertical | AUC |
|---|---|---|
| `"Noted" in session` | Forgetting | **1.000** |
| `"—" in session` | Forgetting / Prospective | 0.95 |
| count of full stops | WorkingMemory | **0.990** |
| session length, within question | Forgetting | 0.903 |

`ECHO_LEAD not in session` was the v1 defect. `"Noted" in session` is the same defect wearing
a different string, in the corpus V7 existed to certify. The gold acknowledgement templates
spelled `"Noted — I have that down."` while no filler sentence used the word or the glyph.

**What changed.** The metric now pairs within a question and folds once after pooling. The
refused set gains sentence count, punctuation density, em-dash density, mean turn characters
and type-token ratio. Phrase recurrence is split by *direction* and both halves refuse: a
phrase carried by gold marks the evidence, and one carried by filler marks it by absence,
which is neither better nor different. The "reported, not refused" carve-out is gone — its
reasoning was sound (template filler repeats itself, and real variety is a corpus revision
rather than a check) but an exemption with no ceiling cannot tell 0.60 from 0.99, and
Prospective sat at 0.990 underneath it.

**The generators changed more.** Equalising one axis at a time relocated the tell rather than
removing it: matching characters left capitals-per-character at 0.89, matching capitals left
sentences-per-character at 1.000. Padding is now chosen by a greedy search over all six raw
counts the refused features are built from — characters, capitals, sentences, punctuation,
tokens, distinct tokens — scoring overshoot as harshly as shortfall, because a folded metric
does not care which side of the target a session lands on. Three levers make the axes
separable: lower-case tails add characters without a sentence or a capital, short sentences
add a sentence without characters, and name-bearing tails add a capital without a sentence.
Gold acknowledgements are drawn from one bank shared with filler, the calibration clause sits
on the same turn role for both (its *position* was itself a marker — Prospective's
`weeks also mind` trigram reached 0.990), and gold's share of that clause is now counted
rather than drawn per session, because a question with one gold session and a 0.92 rate left
it bare 8% of the time.

**Result.** Worst refused feature per vertical is 0.631–0.737 against the 0.75 bar. The four
classifiers in the table above — the ones that found v2 — now score 0.500–0.701 across all five
corpora, against 0.903–1.000 before, and so does every per-role and per-turn slice tried. Measured
with this same final metric, the v1 corpora shipped in 0.22.0-beta score **0.973–1.000**, four of
them perfectly separable by a single count.
Retrieval difficulty moved again, so
**no v1 or v2 number is comparable with a v3 number, and neither should be cited.** v2 was
never released; it existed only on an unmerged branch.

**Balancing an aggregate does not balance its parts, and that took three tries to learn.** The
shape-parity pass appends its padding to one turn, so equalising the *pooled* session text left
every other slice exactly as the generator wrote it. Each narrowing was forced by measuring the one
before it:

| What was equalised | What was still separable | AUC |
|---|---|---|
| the pooled session | user-turn length (WorkingMemory) | **1.000** |
| | user-turn capitals and sentence count (Forgetting) | **1.000** |
| each role's total | the FIRST user turn's length (WorkingMemory) | **1.000** |
| each turn slot | — worst over every slice tried | 0.701 |

Every pooled figure sat comfortably under the bar the whole time. The lesson generalises past this
corpus: a parity check on a sum is not a parity check on its terms, and the attacker picks the
slice. Padding is now applied per **turn slot** — each (role, position-within-role) pair gets its
own target — and the refused set carries the per-role and first-turn variants of every numeric
feature, so the gate can see what the aggregate hides. A uniform empty user/assistant exchange is
appended to *every* session to absorb that padding; the first attempt added it only where a free
turn was missing, which meant only to gold, and turn count promptly became a 1.000 tell of its own.

**This is patching, and it has limits worth stating.** Gold and filler are built by different code
paths and reconciled afterwards, so each fix removes a residue and the next measurement looks for
the next one. Nothing here proves a finer slice — a first sentence, a first clause — is balanced;
it proves that the slices we thought to measure are. The durable fix is to generate gold and filler
from one template machinery so they are identical in shape by construction and differ only in the
fact asserted. That is a redesign of all five generators, and it is what a v4 should be.

**The C# half now measures rather than reads.** `NoCheapFeatureSeparatesGoldFromDistractors`
took its threshold *and its list of features to check* from the record it was testing, so a
trimmed `refused_features` array or a `threshold_auc` of 0.99 would have passed. Both are C#
constants now, and the AUCs are recomputed from the corpus by a second implementation — which
is the point, given the first one certified a corpus a substring filter could pick apart. The
declared `H`, `G` and ceiling table are likewise re-derived from the shipped bytes rather than
read back from the sidecar the generator stamped.

**Two probe defects, both of which manufactured leaks that did not exist.** The negative-gold
guard in `run_typedmemeval_probes.py` contained a literal backspace byte (0x08) where a word
boundary was intended, so its leading-negative alternative could never match any real text and half
the guard was dead code from the day it was written. And the number pattern was greedy enough to capture `2026,` with its
trailing comma, which then failed to match the bare `2026` the prompt itself supplied — so a
year the model had been *handed* counted as evidence it had reached the corpus. Together they
reported a Prospective leak on an answer that said, in as many words, that the conversations
did not contain the information. Month and weekday names no longer count as distinctive
evidence either; in a corpus family made of dates they are world knowledge. The screen is now
covered by a credential-free `--self-test` that runs in CI, because an instrument that has
been wrong needs a calibration check of its own.

**V3 and V6 now sample.** Both ran a single ablation, and a single sample can miss a leak that
is there — the gutter/inspection leak in §12 was caught by one sample and could as easily have
been missed by it. Three samples each, and unlike V2 there is no hit threshold: one sample
that reconstructs the answer from distractors alone condemns the question.

## Consequences

**Positive.** The five mechanisms the consumer cannot measure become measurable, each in
isolation, each with typed outcomes and echoed coverage; the identity rule protects
LongMemEval's name and AgentEval's credibility symmetrically; the family reuses the
hardened 0.19–0.21 pipeline (judge robustness, answer pinning, oracle projection,
evidence capture) rather than re-proving any of it; the generators-plus-probes
methodology makes the benchmark criticizable, which is what makes it citable.

**Negative / costs.** ~250 authored questions with per-vertical validity tooling is the
largest corpus-authoring effort in the repo to date; two shared result models gain
nullable family fields (additive, but the models grow); a second judge fingerprint and
five template sets to maintain; the family name adds a naming surface that must be
policed (the serialization guard automates the worst failure, not all of them); n≈50 per
vertical bounds the claims v1 can carry — bands and honest `n` reporting are load-bearing,
not decorative.

**Explicitly frozen.** All 0.19–0.21 LongMemEval surfaces; the tg corpus and its ids;
the LongMemEval judge-prompt fingerprint; `LongMemEvalOracleOptions` semantics.

## Alternatives Considered

1. **Extend LongMemEval with new corpora under its runner and name** — rejected: it is
   benchmark-name substitution, the integrity failure this ADR exists to prevent, and
   the consumer's ask 1 rules it out explicitly.
2. **Five independent benchmarks instead of one family** — rejected: they share the
   corpus format, runner, judge protocol, outcome typing, and coverage machinery;
   independence would quintuple surface area and invite five divergent reporting styles.
3. **A single mixed corpus with vertical tags** — rejected: blending mechanisms in one
   haystack re-creates LongMemEval's attribution problem (a wrong answer with three
   candidate causes); isolation per corpus is the family's reason to exist.
4. **Community datasets instead of authored corpora** — rejected for v1: no existing
   dataset has the gold-derivation, pairing, and component-labelling the typed outcomes
   need, and licensing-clean session data with `has_answer` labels effectively means
   authoring anyway. Revisit if the family earns external contributors.
5. **Names considered and passed over:** LME-* anything (forbidden on identity),
   MemMechanics (opaque), IsoMemEval (cryptic), FacetMemEval (held as the fallback, §1).
