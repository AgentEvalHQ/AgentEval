# TypedMemEval aggregate diagnostics in AEF

TypedMemEval writes per-question outcomes into `results.ndjson` and aggregate diagnostics into
`summary.json.ext["agenteval.typedmemeval.diagnostics"]`. Both files are covered by the run seal.
The extension's `schemaVersion` is `1.0`. It contains counts, not percentages or gate decisions.

| Field | Meaning |
|---|---|
| `vertical` | Lowercase benchmark vertical, e.g. `workingmemory` or `forgetting` |
| `scope` | Always `observed-questions`; planned cases remain in the suite definition |
| `complete` | True only after normal completion with a typed report; false for interrupted exports |
| `gating` | Always false; these diagnostics do not change scores, verdicts or gates |
| `observedQuestions` | Number of question results retained in this export |
| `missingTypedQuestions` | Observed questions without typed details; excluded from the outcome vector |
| `outcomes` | Typed outcome counts and their denominator `n` |
| `byDistanceSessions` | WorkingMemory only: a map of session distances to outcome-count vectors |
| `missingDistanceQuestions` | WorkingMemory only: typed questions without an observed distance |
| `forgetting` | Forgetting only: `n`, `staleRecall`, `overForgetting`, `misattributedForgetting` |

Each outcome vector contains `n`, `correct`, `wrong`, `abstained`, `missed`, `premature`,
`inconclusive` and `unrun`. Its seven counts sum to `n`. Inconclusive and unrun are explicit
non-measurements; they are never silently counted as wrong. These denominators therefore differ
from the denominator of a score over measured questions.

WorkingMemory distances are decimal session-count keys, sorted numerically. A missing distance
does not become distance zero. The curve is retained as separate buckets; the exporter does not
replace it with an overall distance score. An empty interrupted run has an empty map and `n: 0`.

Forgetting sub-counts count flags on observed typed questions. Their denominator is all observed
typed questions, including explicit non-measurements. It is not the size of an eligible control
stratum, so these counts must not be interpreted as control-specific rates. Flags are counted
independently; no disjointness is inferred. Other verticals omit the forgetting object entirely.
Zero counts with `n: 0` indicate no observations, not evidence of error-free behavior.

Aggregates are derived from the same question results that populate per-question evidence,
including on interruption. Missing results are not filled in from the planned roster. No question,
answer, gold text, shape, pair identifier or retrieved content is copied into this extension,
regardless of the content-capture setting. Existing benchmark scores and labels remain unchanged.

The native composite adapter accepts detached `SummaryExt` diagnostics from producers and reserves
`agenteval.bench` for its own benchmark summary. A producer cannot override that reserved field.
