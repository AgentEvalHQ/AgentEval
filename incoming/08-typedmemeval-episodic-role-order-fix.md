# Prompt 08 — AgentEval: fix the episodic role-order leak, then re-verify v4

For the agent in `C:\git\joslat\AgentEval`. **This runs before prompt 09 and prompt 10, and before
any baseline spend.** It is a correction to the work prompt 07 produced, not new scope.

**Read first:** `incoming/v4-episodic-role-order-BLOCKER.md` — the full finding, with the
ten-second reproduction, the root cause traced to two helpers, and the measured numbers. This
prompt assumes you have read it and is only the work order.

---

## 1. Why this is a blocker rather than a note

TypedMemEval-Episodic v4 gold sessions are identifiable from **turn-role order alone**, with no
words read. Within-question AUC **0.7700** against your own V7 refusal threshold of **0.75**, and
**1.0000 with perfect separation on all 20 `assistant-stated` questions**. For
`participant-attribution` the role sequence encodes *who spoke the answer* — the answer itself —
15 of 15 deterministically, which makes it a **V2 non-inferability** failure on top of V7, where
V2 is currently stamped `passed: 50`.

A corpus that leaks its gold structurally cannot measure retrieval, so no number computed on
episodic v4 is worth having. That is the whole reason to hold rather than publish and patch later.

## 2. The three things to fix

### 2.1 The generator (the actual defect)

Root cause, both in `tools/typedmemeval_common.py`:

- **`_pad_target` (line 528)** appends a padding turn of a role only when *every* turn of that role
  already carries the answer. That is true for episodic gold and false for filler, so gold alone
  gains an extra assistant turn **in the prefix**.
- **`_normalise_turn_counts` (line 560)** then equalises per-role *counts* by appending to the
  **tail**. Counts land on exactly 0.5000 — genuinely correct, and it is what makes the residual
  invisible — while the prefix asymmetry from the previous step survives untouched.

```
gold        u|a(answer)  -> _pad_target appends -> u|a|a  -> +ensure(u,a)        -> u|a|a|u|a
distractor  u|a          -> free turn, no append -> u|a   -> +ensure(u,a) +norm  -> u|a|u|a|a
```

Appending to a tail cannot repair a prefix. Your own docstring on `_normalise_turn_counts` names
this exact defect class and was written to remove it — the fix removed the *count* tell and locked
in the *order* tell in the same pass. That is worth sitting with for a moment, because it is the
third consecutive fix that relocated the tell instead of removing it, and the reason is consistent:
each fix equalised the statistic that had been measured, not the property that made gold special.

**The property to make true**, stated as the acceptance condition:

> Per question, the set of role sequences present in gold sessions must equal the set present in
> distractor sessions.

The cheapest route is to remove the conditional in `_pad_target` — append a fresh free turn of that
role **unconditionally**, for gold and filler alike — so that no session's base shape depends on
where `has_answer` sits, and `_normalise_turn_counts` has nothing asymmetric left to preserve.
Take a different route if you see a better one; the acceptance condition is what is binding, not
the implementation.

**Treat it as a family-wide invariant, not an episodic patch.** Episodic is the only vertical
affected today for exactly one reason: it is the only one that puts `has_answer` on an assistant
turn. Any future vertical that does the same reproduces this, and v5's Temporal and Bitemporal
plausibly will — a retroactive correction is naturally something the assistant states.

### 2.2 The gate (so it cannot regress)

Fixing the generator without extending the gate means the next relocation is found by us again
rather than by you. Add to the published probe:

- **Per-(position, role) occupancy** — the axis that carries this defect. `first_{role}_*` and
  pooled `turn_count` cannot express it.
- **Role-sequence signature** as a categorical presence feature, screened against a chance
  baseline.
- **Within-question, not pooled.** This is the load-bearing one. Measured pooled across questions
  the feature scores **0.6195 and passes your 0.75 threshold**; measured within-question it scores
  **0.7700 and fails**. A tell that is exclusive only *inside* a question is invisible to a pooled
  statistic, and 27 of 50 questions is not enough to move a pooled number past a threshold. If you
  adopt one thing from this prompt beyond the generator fix, adopt this: **the gate must be
  computed at the grain the defect lives at.** We believe this is why each fix has relocated the
  tell rather than ended it.

### 2.3 The two smaller items

- **`arithmetic` `gold_marker_ngram_auc = 0.7365` is byte-identical to v3's**, as is
  `position_in_haystack = 0.5372`, across a redesign that changed statement grammar. Determine
  which it is: genuine seed-invariance, or a probe record carried forward without a re-run. If the
  latter, every arithmetic v4 probe number needs regenerating. Stamping `probed_corpus_sha256` and
  checking it against the live corpus hash at gate time makes this self-detecting — our probe
  already does this, so a stale record will start failing acceptance rather than passing quietly.
- **`v6_leave_one_out.passed = 0` for episodic** while every other vertical reports a real count.
  If that means "not applicable at G=1", say so explicitly in the metadata rather than emitting a
  zero that reads as zero-passed.

## 3. What to do after the fix

1. Regenerate episodic. Re-run the full probe battery — **V1, V2, V3, V6, V7**, not only V7. V2 is
   directly implicated by §1 and its current stamp is wrong.
2. Re-stamp probe records, and confirm the corpus SHA in metadata matches the regenerated bytes.
3. Re-run the reproduction in the blocker's §6 and confirm `set(gold_sigs) - set(distractor_sigs)`
   is **empty** for every question.
4. Re-check the difficulty bands — regeneration changes sessions, so the stamped coverage and the
   band validation both need to be recomputed rather than carried over.

## 4. Scope, and what is *not* blocked

We swept all five v4 corpora. **Only episodic is affected** — arithmetic, forgetting, prospective
and workingmemory are at exactly 0.5000 with no gold-only sequence. So this is one generator and
one shape family.

**Publishing the other four now is a perfectly good outcome**, and probably the right one: it
unblocks four of the five baselines while episodic is corrected, and it avoids a sixth full
generation of everything. Your call — but do not hold four clean verticals hostage to one.

## 5. Deliverable

A short report: what changed in the generator, the before/after on the acceptance condition in §2.1,
the re-run probe table for episodic (all of V1/V2/V3/V6/V7), the answer on the arithmetic record in
§2.3, and whether you published the other four separately. Push back if you disagree with the
diagnosis — but note that the finding is measured against your committed corpus and reproduces in
ten seconds, so the burden is on a specific counter-measurement rather than on reasoning.

---

*Said plainly because it is worth saying: the corpus family is in good shape, and this defect is
hard to see precisely because the surrounding work was done well enough to drive three separate
count features to exactly chance. The instrument is close. It needs its gate measured at the right
grain, and then it is done.*
