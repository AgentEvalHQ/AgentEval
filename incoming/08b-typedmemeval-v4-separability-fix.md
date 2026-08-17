# Prompt 08b — AgentEval: fix v4's remaining separability, then re-release

For the agent in `C:\git\joslat\AgentEval`. Successor to prompt 08, which you closed correctly —
episodic is clean. **Runs before prompts 09, 10 and 11, and before any baseline spend.**

**Decision from the maintainer, up front: do NOT unlist 0.24.0-beta.** Nobody outside this project
consumes it, the release mechanics were correct, and unlisting buys ceremony rather than safety.
**Fix forward and cut 0.25.0-beta.** The one real risk of leaving it listed is that someone
baselines against it by mistake; we have marked it *do-not-baseline* on our side and will wait for
the fixed release.

**Full evidence:** `incoming/v4-probe-result-FAIL.md`. Verified against the **published package**,
not the working tree — all five corpora extracted from `AgentEval.Memory.dll` in
`agenteval/0.24.0-beta` are byte-identical to the release-tag sources, and your
`probed_corpus_sha256` matches too.

---

## 1. Credit first, because it is also the template

**Episodic is fixed.** Role-order AUC 0.5000, no gold-only sequences, 0% perfect separation — down
from 0.7700 / 54% / 100%-on-assistant-stated. You also adopted the check itself
(`role_sequence_auc` is now in the metadata). That is exactly the right response, and it is why
episodic is the one vertical whose recurring n-grams are *shared* with distractors instead of
exclusive to gold. The other four need the same treatment.

## 2. The highest-value fix: change the gate's grain FIRST

Your published method:

> *"single-feature AUC over (gold, distractor) pairs formed WITHIN a question, **pooled across
> questions** and folded once to [0.5, 1.0]"*

Pairs are formed per question, then collapsed into **one global AUC**. When gold's marker differs
from question to question, each individual phrase looks rare across the whole corpus and the pooled
average washes it out — while inside its own question it is decisive.

Your own published number is the proof, and it needs no re-run to check:

| Feature | Vertical | Your pooled | Per-question |
|---|---|---:|---:|
| `'today'` — your `worst_gold_marker_ngram` | arithmetic | **0.7365 → pass** | **0.9498 → fail** |

**Do this before regenerating anything.** A corpus regenerated and then graded by a pooled gate will
pass again while still being separable, and you will have spent a generation to learn nothing. The
change is small: compute AUC per question, average the per-question values, keep the same 0.75
threshold. Report both numbers during the transition if it helps you trust it — but gate on the
per-question one.

## 3. Then the corpus fix — a fix you have already executed successfully

Every item below recurs in ≥20% of questions (your own boilerplate rule) and was **inspected by
hand**, not taken from a number. None is content; all are constructions that only gold receives.

### prospective — `'while it lasts'` · 12 gold questions · **0 distractor hits**
> *"Good to know. Enjoy it while it lasts. (Also on my mind: …)"*

An assistant **acknowledgement**, carrying no information about the answer. This is precisely the
`REPLIES` defect you already fixed — where `"noted"` found Forgetting's gold at AUC 1.000 — with a
different string. Same fix: one shared bank, drawn for gold and filler alike.

### workingmemory — `'since the'` · 20 gold · **0 distractors** — `'the winter'` · 15 gold · **0**
> *"I have moved onto Ferrow Row, a street of terraces, **since the** flat fell through in **the
> winter**."*

**This is the `'i have'` statement-grammar defect relocated to the temporal clause.** v4's central
fix made filler state first-person facts in gold's construction — but only for the verb phrase. The
`since the <event>` time clause stayed gold-only. Extend the shared frames to cover it.

### forgetting — `'for the record'` / `'still the same'` · 15 gold each · **0 distractors**
> *"**Still the same** car, **for the record**: Kelbrick Solace."*

A retention marker exclusive to gold. Same shape, same fix.

### arithmetic — `'today'` 0.9498 · `'today for'` / `'put an order'` 0.9424
> *"I put an order in with Stelling Adhesives **today for** two rolls of jointing tape."*

You already name `'today'` as the worst gold marker. It only passes because of §2.

### Structural, on axes your feature set has no column for

| Feature | Vertical | Per-question AUC |
|---|---|---:|
| `user[1]_chars` — per-role, **ordinal slot 1** | prospective | 0.7997 |
| `assistant[0]_chars` | prospective | 0.7883 |
| `user[1]_chars` | workingmemory | 0.7744 |
| `sentences` | workingmemory | 0.7701 |

You have `assistant_length_chars` and `assistant_mean_turn_chars` — per-role, but aggregate. The
length equalisation worked on the total and left the **second turn** separable. Add per-(role,
ordinal slot) length to the refused set.

## 4. Acceptance, and what to re-run

1. **Gate grain changed** (§2) and the published metadata says so in `method`.
2. Regenerate **prospective, workingmemory, forgetting, arithmetic**. Episodic needs no corpus
   change — but re-run its probes, because the gate that judges it changed.
3. Re-run the **full battery: V1, V2, V3, V6, V7** — not only V7. A construction shared with filler
   changes what the oracle sees.
4. **Difficulty bands must be recomputed**, not carried over: regeneration changes sessions, so both
   stamped coverage and band validation are stale by construction.
5. **Our probe as the acceptance gate**, at per-question grain, before release. Send us the corpora
   (or a prerelease we can restore) and we will run it and report *before* you tag — that is the
   sequence prompt 08's handoff asked for and it works.
6. Cut **0.25.0-beta**. Leave 0.24.0-beta listed.

## 5. One thing worth saying

Your gate has been right where ours was wrong four times, and we have said so each time. This
disagreement is different in kind: it is not judgement, it is **grain**, and grain is checkable by
either of us in isolation. Once both instruments compute at the same grain, most of these
disagreements should simply stop happening — which is worth more than either of us being right about
this particular release.

Episodic already proves the loop works end to end. Four verticals to go.
