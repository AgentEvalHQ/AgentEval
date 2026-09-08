# Findings — measurement records kept after their sample was removed

These are **records**, not documentation. They say what was measured, when, on which tree, and what
turned out to be wrong. They are indexed here rather than in `docs/toc.yml`, the same hub-page
pattern `docs/adr/` uses, because they are evidence for decisions rather than pages a reader of the
site navigates to.

## Index

| Document | What it is | Size |
|---|---|---|
| [MEASUREMENT_STATUS.md](MEASUREMENT_STATUS.md) | §§1–87. What the eval suite can and cannot support, every figure with the command that produced it, and every claim that did not survive re-measurement. Cited by ADR-030, ADR-031 and ADR-032. | ~16 000 lines |
| [SUITE_SUMMARY.md](SUITE_SUMMARY.md) | Every eval, every case, what happened, and whether it was the agent's fault. | ~1 000 lines |
| [RUN_PROTOCOL.md](RUN_PROTOCOL.md) | The standing three-stage rule before any paid run: dry-run every case, then one real item, then the full run. | short |

## ⚠ Why these still say "Galaxus"

The sample they measure — `samples/Galaxus.RecommendationAgent{,.Evals}`, ~71 000 lines — was
**deleted on 2026-09-08**. It had no dependents, and every pattern it demonstrated is taught by a
smaller sample: `AgentEval.Samples/EvalJoin` for the deterministic-code-eval join (including
`MinimumAttainableP` and the paired control arm), `AgentEval.PartnerDeskDemo.Evals` for ceiling
floors, `AgentEval.TravelDemo.Evals` for `AtLeastOneHit`. The realistic end-to-end case moved to the
standalone VITRINE repository.

The name stays **inside** these documents deliberately. Every figure in them was measured on a sample
called Galaxus, on a branch called `joslat/digitec-galaxus`. Renaming the subject of a measurement
record does not tidy it up — it makes the record claim something that never happened. The brand is
gone from all live code, from `AgentEval.sln` and from `.gitignore`; it survives here, and in the
three ADRs, only where it is still true.

## How to read a record like this

Two habits these documents were written to enforce, and which outlive the sample:

- **A figure without its command is a claim, not a measurement.** Each section names the command that
  produced its numbers so a reader can re-take them rather than trust them.
- **Corrections are kept, not overwritten.** Where a later section refutes an earlier one, both stay
  and the newer one says which claim it supersedes and in which direction the error ran. A record
  that only ever agreed with itself is not evidence of anything.
