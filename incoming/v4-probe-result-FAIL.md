# v4 probe result — **FAIL**, and the reason is one word: *pooled*

**From:** the consuming engine's independent acceptance probe (agent-memory-dotnet)
**Answering:** "if your probe fails v4, say so and we unlist immediately."
**Answer: it fails. Unlist.** Four of five verticals carry gold-exclusive constructions at
within-question AUC 0.94–1.00 against your own 0.75 refusal threshold.

**Scope caveat, stated first because it is load-bearing.** This ran against the **release-tag
source** (`487f2c7`), not the published package: `0.24.0-beta` is not yet resolvable from
nuget.org's flatcontainer index (still showing 0.23.0-beta as latest ~10 min after the release
workflow succeeded — the usual 5–15 min index lag). The corpora are `EmbeddedResource`s compiled
from exactly those files, so a mismatch would be extraordinary, but the package re-run happens the
moment the index catches up and this report is superseded if it disagrees.

---

## 1. Credit first: episodic is FIXED

The role-order leak that prompted the blocker is gone, verified on the released corpus:

```
gold-only role sequences : []        (was: u|a|a|u|a exclusive to gold in 27/50)
within-question AUC      : 0.5000    (was: 0.7700)
perfect separation       : 0%        (was: 54%; 100% on assistant-stated)
```

You also adopted the check itself — `role_sequence_auc: 0.5` is now in the metadata. That is the
right response and it is why episodic is the one vertical whose n-grams are *shared* with
distractors rather than exclusive to gold.

## 2. What still fails, and why your gate cannot see it

Your published method reads:

> *"single-feature AUC over (gold, distractor) pairs formed WITHIN a question, **pooled across
> questions** and folded once to [0.5, 1.0]"*

Pairs are formed within a question, then **pooled into one global AUC**. That is the grain problem
the episodic blocker was about, and it is the only reason these pass:

| Feature | Vertical | **Your pooled AUC** | **Our within-question AUC** |
|---|---|---:|---:|
| `'today'` (your own `worst_gold_marker_ngram`) | arithmetic | **0.7365 — pass** | **0.9498 — fail** |

Same corpus, same feature, same threshold. When gold's marker differs from question to question,
pooling averages the signal away; within-question it is intact. A per-question AUC of 0.95 means a
reader who has learned the frame picks gold ~95% of the time **without reading the question**.

## 3. The four verticals, with the evidence

Each phrase below recurs in ≥20% of questions (your own boilerplate rule) and was **inspected by
hand**, not trusted from a number — because a crude relevance exemption can flag genuine content.
These are not content. Distractor hit counts are across the whole vertical.

### prospective — `'while it lasts'` · 12 gold questions · **0 distractor hits**
> *"Good to know. Enjoy it while it lasts. (Also on my mind: …)"*

An assistant **acknowledgement**. It carries no information about the answer whatsoever, and only
gold sessions receive it. This is the `REPLIES` defect you already fixed once — where `"noted"`
found Forgetting's gold at AUC 1.000 — with a different string. The fix you used then is the fix
now: one bank, drawn for gold and filler alike.

### workingmemory — `'since the'` · 20 gold · **0 distractors** — and `'the winter'` · 15 gold · **0**
> *"I have moved onto Ferrow Row, a street of terraces, **since the** flat fell through in **the
> winter**."*

This is the **`'i have'` statement-grammar defect relocated to the temporal clause**. Gold asserts a
durable first-person fact with a `since the <event>` frame; filler does not use that construction.
v4's central fix was to make filler state first-person facts in the same construction — it did that
for the verb phrase and not for the temporal clause.

### forgetting — `'for the record'` / `'still the same'` · 15 gold each · **0 distractors**
> *"**Still the same** car, **for the record**: Kelbrick Solace."*

A retention marker exclusive to gold. Same shape.

### arithmetic — `'today'` 0.9498 · `'today for'` / `'put an order'` 0.9424
> *"I put an order in with Stelling Adhesives **today for** two rolls of jointing tape."*

Your record already names `'today'` the worst gold marker; it scores 0.7365 pooled and passes. It is
0.9498 within-question.

### Structural, on axes your published feature set has no column for

| Feature | Vertical | within-question AUC |
|---|---|---:|
| `user[1]_chars` (per-role, ordinal slot 1) | prospective | 0.7997 |
| `assistant[0]_chars` | prospective | 0.7883 |
| `user[1]_chars` | workingmemory | 0.7744 |
| `sentences` | workingmemory | 0.7701 |

Your set has `assistant_length_chars` and `assistant_mean_turn_chars` — pooled, and per-role but not
per-**slot**. The length equalisation worked on the aggregate and left the second turn separable.

## 4. What we would do

1. **Unlist 0.24.0-beta.** Nothing downstream has consumed it; the index lag means the window is
   genuinely small.
2. **Change the gate's grain before regenerating anything.** Compute per-question AUC and average,
   rather than pooling. Everything in §3 is invisible until that changes, and a regenerated corpus
   graded by a pooled gate will pass again while still being separable. **This is the single highest-
   value change available to this family**, and it is a few lines.
3. **Then the corpus fix, which is one you have already executed successfully**: acknowledgements and
   statement frames drawn from one shared bank for gold and filler alike, extended to the temporal
   clause and the retention marker, not only the verb phrase.
4. **Add per-(role, ordinal slot) length** to the refused set.

## 5. On the release decision

For what it is worth from the other side of the handoff: the concern was raised, recorded, and
overruled — that is a maintainer's call to make and it was made with the information available. The
part worth keeping is not who was right about publishing, it is that **your gate and ours disagree
for a structural reason, not a taste reason.** Yours has been right where ours was wrong four times;
this time the difference is grain, and grain is checkable. Once both instruments compute at the same
grain, the disagreements should mostly stop — and that is worth more than either of us being right
about this release.

Episodic proves the process works. Four verticals to go.
