# Calibration results

These are the judge-agreement figures AgentEval measured for its own judges with `calibrate`: how often each judge's
verdict matched the hand-labelled golden cases, and Cohen's κ (agreement beyond chance). They describe **one judge model
on one day**. A different judge model, deployment or prompt can move them, so calibrate against the judge you use before
relying on a verdict.

| | |
|---|---|
| **Date** | 2026-10-05 |
| **Judge** | Bitdeer AI Model Studio, `zai-org/GLM-5.3-Flash` (`AI_INFERENCE_PROVIDER=bitdeer`) |
| **Version** | the code released as 0.44.0-beta |
| **Golden sets** | the ones shipped with the release (agentic: `tests/AgentEval.Tests/Agentic/Calibration/Golden/`) |
| **Gate** | accuracy ≥ 85% and κ ≥ 0.70 per pillar or category, with zero evaluation failures, unless a row names its own gate |

Reproduce them with your own judge:

```bash
agenteval bench agentic calibrate --out agentic.md --records agentic.records.jsonl
agenteval bench gdpr calibrate --out gdpr.md
agenteval bench eu-ai-act calibrate --out eu-ai-act.md
```

## How to read them

- **One run, not an average.** The GDPR and EU AI Act judges send no fixed temperature, so the same request can be
  scored differently on another run: re-running the 145 GDPR judge requests unchanged changed 13 of their scores, in
  both directions. On a 15-case pillar one case moves accuracy by 6.7 points and can move κ across its gate.
- **INFRA-FAIL is not a verdict.** A judge call that failed (a provider timeout) is reported as an infrastructure
  failure, never as a disagreement; the row's figures cover the cases that were measured.
- **INCOMPLETE** means an evaluator was not measured on every one of its cases; it is left out of the row whole.

## GDPR

| Pillar | Result | Accuracy | κ | Cases | Gate |
|---|---|---|---|---|---|
| 1 — Foundations | PASS | 100.0% | 1.000 | 30 | default |
| 2 — Lawful basis | PASS | 100.0% | 1.000 | 20 | default |
| 3 — Rights | PASS | 100.0% | 1.000 | 40 | default |
| 4 — Transparency | FAIL | 86.7% | 0.667 | 15 | default (κ below 0.70 by one case) |
| 5 — Design | PASS | 100.0% | 1.000 | 15 | default |
| 6 — Governance | PASS | 100.0% | 1.000 | 25 | 85% / κ 0.60 |

## EU AI Act

| Pillar | Result | Accuracy | κ | Cases | Gate |
|---|---|---|---|---|---|
| 1 — Prohibited practices | INFRA-FAIL | 83.3% | 0.610 | 24 of 25 (one judge timeout) | 65% / κ 0.35 |
| 2 — Transparency | PASS | 93.3% | 0.842 | 15 | default |
| 3 — Human oversight | PASS | 92.9% | 0.837 | 28 | default |
| 4 — Risk tier | PASS | 87.0% | 0.736 | 23 | default |
| 5 — Robustness | PASS | 100.0% | 1.000 | 15 | default |
| 6 — General-purpose AI | PASS | 100.0% | 1.000 | 12 | 60% / κ 0.25 |

Pillar 1's measured cases clear its gate; the row is INFRA-FAIL because one of its 25 judge calls timed out at the
provider four times.

## Agentic

Pending: the agentic suite is being re-measured on the release's final code (two of its evaluators changed after the
first after-round: similarity and response completeness now receive the reference answer). Its figures will be
published here.
