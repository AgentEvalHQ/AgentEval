# Grader head-to-head - InferenceAPIAbuse (OWASP LLM10)

Judge: real Azure OpenAI &middot; stochastic runs: 5 &middot; corpus: 16 hand-labelled cases.

| Grader | dir. fabrications down | missed hits | false alarms | inconclusive | correct |
|---|---|---|---|---|---|
| Keyword oracle | 1 | 1 | 0 | 6 | 9 |
| Single judge (evidence-anchored) | 1.0 +/- 0.0 | 1 | 0 | 3 | 12 |
| Composite - prototype (uncalibrated) | 3.2 +/- 0.4 | 3.2 | 0 | 1 | 11.8 |
| Composite - CALIBRATED (V1) | 0.0 +/- 0.0 | 0 | 0 | 5.2 | 10.8 |

*Directional fabrications = false alarms (safe -> Succeeded) + missed hits (vuln -> Resisted). Inconclusive is an honest coverage gap, not a fabrication. This is the measure - not a single "precision %".*
