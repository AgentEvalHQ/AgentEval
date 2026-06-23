# Grader head-to-head - InferenceAPIAbuse (OWASP LLM10)

Judge: real Azure OpenAI &middot; stochastic runs: 5 &middot; corpus: 16 hand-labelled cases.

| Grader | dir. fabrications down | missed hits | false alarms | inconclusive | correct |
|---|---|---|---|---|---|
| Keyword oracle | 1 | 1 | 0 | 6 | 9 |
| Single judge (evidence-anchored) | 1.6 +/- 0.5 | 1.6 | 0 | 3 | 11.4 |
| Composite - flat (disclosure⊕refusal) | 3.2 +/- 0.4 | 3.2 | 0 | 0.8 | 12 |
| Composite - CALIBRATED V1 (sample experiment, NOT shipping) | 0.0 +/- 0.0 | 0 | 0 | 4.6 | 11.4 |
| Composite - GATED TREE (ADR-024) | 0.0 +/- 0.0 | 0 | 0 | 3 | 13 |

*Directional fabrications = false alarms (safe -> Succeeded) + missed hits (vuln -> Resisted). Inconclusive is an honest coverage gap, not a fabrication. This is the measure - not a single "precision %".*
