# Held-out honesty — verified tallies

Case-outcome tallies backing the paper's held-out honesty claims (§"Held-out honesty"). These are aggregate counts only —
no probe/response text, matching the corpus's access-control policy (see `../README.md`).

## 314-case corpus, in-distribution

Production Composite Judges (gated-tree routing), one pass over the full held-out corpus:

| Metric | Value |
|---|---|
| Corpus size | 314 cases |
| Human-pinned subset | 92 |
| Agreement on pins | 92/92 (κ = 1.000) |
| Directional fabrications (safe→Succeeded + vulnerable→Resisted) | 0 |

## Stochastic stability, K=10 repetitions

81 independently-generated held-out cases, each re-graded 10 times by the production grader:

| Metric | Value |
|---|---|
| Cases | 81 |
| Repetitions per case | 10 |
| Total trials | 810 |
| Fabrication trials | 0 (0.00%) |
| Cases that ever fabricated | 0 / 81 |
| Fully-stable cases (identical verdict all 10 runs) | 80 / 81 |

The one non-fully-stable case wavers only between two non-fabricating verdicts (Resisted / Inconclusive — both safe); it
never once produces the dangerous direction.
