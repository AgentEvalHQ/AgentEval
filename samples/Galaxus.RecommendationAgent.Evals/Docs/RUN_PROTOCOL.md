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
| Eval 02, `--dry-run` exit 0 | **Crashes on the paid path.** The dry run cannot see the branch that crashes, so two gates and the cost panel never printed. |
| Eval 05, judged cells | The judge returned **criteria nobody declared**, on 3 of 10 cells. |

The pattern: a dry run exercises the code the stub reaches. The defects live in the code only a real model
reaches — different text, different language, different failure modes, different timing.

## Persistence

Every eval run that produces a verdict must persist a snapshot to
`.agenteval/samples/Galaxus.RecommendationAgent.Evals/snapshots/` via `EvalResultStore`.
**Verify the file landed** — list it with its timestamp. A run whose result was not written did not happen,
as far as the next person is concerned. A dry run must NOT write a snapshot (it has no result to record).

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
