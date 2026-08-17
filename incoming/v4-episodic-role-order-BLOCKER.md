# ⛔ Pre-publish blocker — TypedMemEval-Episodic v4 fails V7 on turn-role ORDER

**From:** the consuming engine's independent acceptance probe (agent-memory-dotnet)
**Status:** verified against your local `agenteval-typedmemeval-episodic-v4.json`, not a guess
**Ask:** hold the v4 publish **for episodic only** — the other four verticals are clean and can ship

---

## 1. The verdict in one line

Episodic v4 gold sessions are identifiable **without reading a single word**, from the order of
turn roles alone. Pooled within-question AUC **0.7700** against your own refusal threshold of
**0.75** — so this is not a judgement call about whether order counts as a feature. It fails your
gate at your number. It passes today only because the gate has no column for it.

On the `assistant-stated` shape it is not a partial tell, it is **total**:

| shape | n | gold role-sequence | distractor role-sequence | within-Q AUC | perfect separation |
|---|---|---|---|---|---|
| `assistant-stated` | 20 | `u\|a\|a\|u\|a` (20/20) | `u\|a\|u\|a\|a` (391/391) | **1.0000** | **100%** |
| `participant-attribution` | 15 | mixed (see §3) | mixed | 0.7333 | 47% |
| `list-order` | 15 | `u\|a\|u\|a` | `u\|a\|u\|a` | 0.5000 | 0% |
| **vertical** | **50** | | | **0.7700** | **54%** |

Equivalently: `pos_2_is_assistant` is 1 for gold and 0 for every distractor. Literally "an
`('assistant', 2)` slot no distractor had" — the sentence your own `_normalise_turn_counts`
docstring uses to describe the defect it was written to remove.

## 2. Why your gate reports `passed: true`

Because the counts really are equalised. That part worked, and it worked perfectly:

```
turn_count              AUC 0.5000     ← exactly chance
user turn count         AUC 0.5000     ← exactly chance
assistant turn count    AUC 0.5000     ← exactly chance
role SEQUENCE           AUC 0.7700     ← not measured by any published column
```

Three axes at *exactly* 0.5000 is the signature of a successful equalisation pass, and it is also
what makes the residual invisible: your published feature set has pooled `turn_count`, and
`first_{role}_*`, but no ordinal slot beyond 0 and no (position, role) occupancy. A gate cannot
regress on an axis it does not measure. The published `worst_refused_auc` of 0.7135 is the
worst of the features you *look at*.

**And there is a second reason it hides, which is the more useful one.** Measured the way a pooled
gate would measure it, this feature passes:

```
POOLED (question boundaries ignored)   0.6195   ← under your 0.75 threshold: PASSES
WITHIN-QUESTION mean                   0.7700   ← over it: FAILS
pooledHidesBy                          0.1505
```

A gate that pools sessions across questions cannot see a tell that is only ever exclusive *inside* a
question — and 27 of 50 questions is not enough to move a pooled number past a threshold. This is
the whole argument for the within-question rule, and it is the reason the same defect keeps
reappearing in a new place after each fix: the measurement is being taken at a coarser grain than
the defect lives at.

## 3. The part that is worse than a locator

For `participant-attribution` the question is *who said it*. The role sequence answers that
question directly, with no exceptions:

```
who actually SPOKE the answer   ×   gold role-sequence
   assistant    u|a|a|u|a    ->  7
   user         u|a|u|a      ->  8
```

15 of 15, deterministic, and the 7/8 split is exactly your `speakers = ["user"] * 8 +
["assistant"] * 7` at `gen_typedmemeval_episodic.py:417`. So for this shape the structure does not
merely leak *where* the evidence is — it leaks **the answer itself**. That makes it a **V2
non-inferability** failure as well as V7, and V2 is currently stamped `passed: 50`.

## 4. Root cause — and it is your fix, not your bug

The asymmetry is manufactured in two steps, both in `tools/typedmemeval_common.py`:

**Step 1 — `_pad_target` (line 528) appends conditionally.** It returns the last *free* turn of the
role, and appends a new one only when every turn of that role is answer-bearing. Gold for
`assistant-stated` puts `has_answer` on its only assistant turn, so gold gets an appended assistant
turn and filler does not:

```
gold base       u|a(answer)   ->  _pad_target('assistant') appends  ->  u|a|a
distractor base u|a           ->  free assistant found, no append   ->  u|a
```

**Step 2 — `_normalise_turn_counts` (line 560) equalises counts by appending to the tail.** Both
sessions reach 2 user + 3 assistant, so every count feature lands on exactly 0.5000 — while the
*prefix* difference created in step 1 is preserved untouched:

```
gold        u|a|a  + ensure(u,a)        ->  u|a|a|u|a
distractor  u|a    + ensure(u,a) + norm ->  u|a|u|a|a
```

Your docstring already names this failure mode — *"gold ended (u,a,u,a,a) and filler (u,a,u,a,u)…
so gold owned an ('assistant', 2) slot that no distractor had. A slot only one side possesses
cannot be equalised by a per-slot pass"* — and the fix equalised **counts** where the defect lives
in **positions**. Appending to the tail can never repair a prefix. The count tell was removed and
the order tell was locked in by the same pass.

## 5. What the fix has to satisfy

Not "add role-order to the gate" — that catches it next time. The generator must make the role
sequence **non-informative by construction**:

> Every session in a question must present the *same role sequence*, not merely the same per-role
> counts.

The cheapest way to get there is to remove the conditional in step 1: have `_pad_target` append a
fresh free turn of that role **unconditionally**, for gold and filler alike. Then no session's base
shape depends on where `has_answer` sits, and `_normalise_turn_counts` has nothing asymmetric left
to preserve. Whatever route you take, the acceptance condition is the same and it is checkable in
one line: *the set of role sequences present in gold must equal the set present in distractors,
per question.*

Please also treat this as a **family-wide invariant**, not an episodic patch. Episodic is the only
vertical that puts `has_answer` on an assistant turn, which is exactly why it is the only one
affected today — any future vertical that does the same will reproduce this, and v5's Bitemporal
and Temporal both plausibly will.

## 6. Reproduce in ten seconds

```python
import json, collections
d = json.load(open('src/AgentEval.Memory/Data/typedmemeval/episodic/'
                   'agenteval-typedmemeval-episodic-v4.json', encoding='utf-8'))
sig = lambda s: '|'.join(t['role'][0] for t in s)
g, x = collections.Counter(), collections.Counter()
for q in d:
    gold = set(q['answer_session_ids'])
    for sid, s in zip(q['haystack_session_ids'], q['haystack_sessions']):
        (g if sid in gold else x)[sig(s)] += 1
print('gold:', dict(g))          # {'u|a|u|a': 86, 'u|a|a|u|a': 27}
print('dist:', dict(x))          # {'u|a|u|a|a': 521, 'u|a|u|a': 468}
print('gold-only:', set(g) - set(x))   # {'u|a|a|u|a'}  <-- never in any distractor
```

## 7. Scope — the good news

We swept all five v4 corpora for the same defect. **Only episodic is affected.**

```
arithmetic     seqAUC 0.5000    no gold-only sequence
episodic       seqAUC 0.7700    u|a|a|u|a  (×27)      ⛔
forgetting     seqAUC 0.5000    no gold-only sequence
prospective    seqAUC 0.5000    no gold-only sequence
workingmemory  seqAUC 0.5000    no gold-only sequence
```

So this is a one-generator fix, one shape family, and the other four verticals are ready. If it is
useful to unblock the baseline numbers, publishing four now and episodic on the fix is a perfectly
good outcome — better than a fifth generation of everything.

## 8. Two smaller things while you are in there

- **`arithmetic` `gold_marker_ngram_auc = 0.7365` is byte-identical to v3's**, as is
  `position_in_haystack = 0.5372`, across a redesign that changed statement grammar. Either genuine
  seed-invariance or a probe record carried forward without a re-run. Worth confirming which — our
  probe now checks `probed_corpus_sha256` against the live hash automatically, so a stale record
  will start failing acceptance rather than passing quietly.
- **`v6_leave_one_out.passed = 0` for episodic** while every other vertical reports a real count.
  If that is "not applicable at G=1" it should say so rather than read as zero-passed.

---

*This is why the independent probe exists, and it is the fourth time the loop has paid for itself.
The corpus family is in genuinely good shape — the defect is one conditional in one helper, and the
reason it is hard to see is that the surrounding work was done well enough to drive three separate
count features to exactly chance.*
