# RUN_PROTOCOL — verify small and live before every full run

**Standing rule, set 2026-09-06. Applies to every wave of the plan, and to anyone picking this up later.**

## The rule

> **Verify the wiring live, in the smallest possible unit, BEFORE any full run.**
> Real credentials are approved for this. A full run is never the first live thing you do.

This is stricter than "dry-run first". A dry run proves the plumbing accepts a stub; it does **not**
prove the thing works against a real model. Both are required, in this order.

## The stages

| # | Stage | Spends | Proves |
|---|---|---|---|
| 1 | **Dry run — every case** | nothing | the code path executes; arguments survive the round trip; nothing throws. ⚠ A dry run that passes proves the *stub* was accepted, nothing more. |
| 2 | **One real unit, live** | one turn | the wiring reaches a real model and comes back usable: the tool channel was used rather than prose, usage is reported, the result is not empty or degenerate. **If this is wrong, STOP — do not proceed to stage 3.** |
| 3 | **The full run** | the rest | the measurement itself. |

`--only <id>` exists on Evals 02, 02b and 02c precisely to make stage 2 possible; it addresses a probe
snapshot key, never the cohort key, and is ignored under `--ci`.

## Why stage 2 exists — the evidence from this repository

Every one of these passed a dry run and was still broken:

| What passed dry-run | What the live run found |
|---|---|
| Eval 01, all plumbing green | `wahl` — German for both *election* and *choice* — tripped a zero-tolerance GDPR detector on de-language personas. 3 of 6 failures. **Structurally invisible offline**: the deterministic arm composes its reasons in English. |
| Demo 2, exit 0 | 6 of 7 model calls timed out at 60 s. The printed verdict "the loop bought nothing" came from **timeouts, not the reviewer**. |
| The ranker, offline | Rule 6 tells the model to cite `grounding_attribute_key` from a list whose tokens the resolver **rejects** — the agent punished for obeying its instructions. Visible only in a live trace. |
| Eval 02, `--dry-run` exit 0 | **Crashes on the paid path.** The dry run cannot see the branch that crashes, so two gates and the cost panel never printed. ✅ *Fixed `cef95b6c` — the dry run now runs that branch AND varies the stub's `k`, and says which.* |
| Eval 05, judged cells | The judge returned **criteria nobody declared**, on 3 of 10 cells. 🔴 *Corrected `a78d05e5`: it did not. It echoed OUR rubric with the ordinal `ChatClientEvaluator.cs:46` prints itself, and our matcher failed to recognise our own text. Same lesson, opposite subject — the live model behaved more faithfully than the stub, which stripped the ordinal.* |

The pattern: a dry run exercises the code the stub reaches. The defects live in the code only a real model
reaches — different text, different language, different failure modes, different timing.

**And one refinement the 2026-09-06 wave added, which is the harder half.** Two of those rows were reachable
from stage 1 all along; what stopped them was that the **stub behaved better than any real model would**. The
Eval 05 stub echoed each criterion with the ordinal *stripped*; the Eval 02 stub presented a constant `k` and
ran one repetition. A stub that is more convenient than reality is not a conservative test, it is a blind one.
Both stubs now exercise the awkward form — Eval 05 alternates ordinal / bare and asserts both were seen, Eval
02 alternates 2 / 3 products across two reps — and where a dry run genuinely **cannot** see its subject it now
prints a check that says so rather than a green tick beside it.

## Persistence

Every eval run that produces a verdict must persist a snapshot to
`.agenteval/samples/Galaxus.RecommendationAgent.Evals/snapshots/` via `EvalResultStore`.
**Verify the file landed** — list it with its timestamp. A run whose result was not written did not happen,
as far as the next person is concerned.

**A dry run of a MODEL-BACKED eval must not write a snapshot** — it has no result to record. A dry run of a
**model-free** eval writes normally, because there is nothing to stub: it is the same measurement either way.

> ✅ **CORRECTED 2026-09-06 (Wave 2, plan item 8.19).** The rule above used to read *"a dry run must NOT
> write a snapshot"* full stop, and `--ci --dry-run` falsified it on every run: Evals 03 and 04 call no
> model, so the CI chain hands them no `dryRun` argument, so they run for real and persist —
> `eval03_controls.json` and `eval04_injection.json` moved at 01:26:14 inside a dry run that ran
> 01:26:12–01:26:19 (`MEASUREMENT_STATUS` §24.7 item 1), and again inside the run that verified the write-up.
> **The writes were always correct; the claim was the defect.**
>
> **Decided, not deferred:** 03 and 04 do **not** gain a `dryRun` parameter. Replacing a real, model-free
> measurement with a stubbed copy of itself inside a dry run would make the cheapest honest measurement in
> the suite worse in order to make a sentence true.
>
> **What changed instead:** `EvalResultStore` keeps a write ledger (`KeysWrittenThisRun` /
> `SnapshotsWrittenThisRun`), fed by both write chokepoints, and the closing banner **names every snapshot
> the run actually wrote** instead of asserting there were none. It reports only keys whose file is still on
> disk, so a reader who is told a snapshot was written can go and look at it. Pinned by Eval 03's gating
> control `WriteLedgerMatchesTheStore`, proven red by removing either chokepoint's `RecordWrite`.

**Which evals persist at all.** Do not read this list off a document again — read it off the code. Every
eval file carries a `// SNAPSHOT-POLICY:` line on line 4, and Eval 03's gating control
`EveryEvalDeclaresItsSnapshotPolicy` checks each declaration **against that file's actual store calls, in
both directions**, so a stale declaration fails the build rather than misleading a reader:

```
grep -n "SNAPSHOT-POLICY" samples/Galaxus.RecommendationAgent.Evals/Evals/*.cs
```

Measured 2026-09-06 at `046f5425`: **11 files, 10 `writes`, 1 `deliberately-none`.** The one is **Eval 08**,
and its reason is stated in code (`Eval08:316-319`) — nothing consumes a stability snapshot, and a number in
a shared store that no gate reads is a hazard a later reader can mistake for one that is.

> ✅ **CORRECTED 2026-09-06 (Wave 2, plan item 8.20).** This paragraph used to read *"01, 02, 02b, 02c, 03,
> 04, 07, 09 do; **05, 06 and 08 do not**"*, and item **8.20** closed exactly that: `eval05_quality` and
> `eval06_trajectory` are new typed records, written on the **live** branch only — so a dry run still writes
> neither, and a paid run now leaves both. **The silence was the defect, not the missing file**: three
> identical-looking silences, one deliberate and two accidental, with no way to tell them apart without
> reading three files. That is what the declaration and its control remove.

## Cost discipline

Report cost from the provider's own usage blocks, never from an estimate. If a run reports
`token-estimated > 0` or `unaccounted > 0`, say so — a currency figure derived from a guess is not a
measurement. Prompt tokens dominate in a tool loop (measured: 96% of the bill), so a per-turn cost is
driven by context re-sending, not generation.

## What this protocol does not do

It does not make a run *correct*. Stage 2 proves the wiring is live; it says nothing about whether the
metric is meaningful, whether the arms are comparable, or whether the gate can fail. Those are the
instrument's job — chance floors, negative controls, equal-k pairing, attainable p — and they are
documented in `MEASUREMENT_STATUS.md` and the ADRs, not here.
