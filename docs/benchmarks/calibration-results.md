# Calibration results

These are the judge-agreement figures AgentEval measured for its own judges with `calibrate`: how often each judge's
verdict matched the hand-labelled golden cases, and Cohen's κ (agreement beyond chance). They describe **one judge model
on one day**. A different judge model, deployment or prompt can move them, so calibrate against the judge you use before
relying on a verdict.

| | |
|---|---|
| **Date** | 2026-10-05 (GDPR, EU AI Act); 2026-10-06 (agentic) |
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

Measured on 2026-10-06 (the GDPR and EU AI Act rows on 2026-10-05), after similarity and response completeness began
to receive the reference answer: 328 judge calls, no evaluation failure. The suite's gate **fails**: one category fails
its gate and two are INCOMPLETE.

| Category | Result | Accuracy | κ | Cases | Gate |
|---|---|---|---|---|---|
| Adversarial | PASS | 95.7% | 0.911 | 23 | default |
| Calibration (epistemic) | PASS | 100.0% | 1.000 | 8 | 75% / κ 0.55 |
| Process | FAIL | 61.5% | 0.323 | 13 | 85% / κ 0.65 |
| Quality | INCOMPLETE | 81.8% | 0.651 | 22, `goal_decomposition_quality` left out | 65% / κ 0.40 |
| Reasoning | INCOMPLETE | — | — | 0, `reasoning_correctness` left out | 70% / κ 0.40 |
| Safety | PASS | 95.1% | 0.894 | 203 | 80% / κ 0.60 |
| System | PASS | 85.0% | 0.727 | 20 | 70% / κ 0.45 |
| UX | PASS | 100.0% | 1.000 | 8 | default |
| Memory | SKIP | — | — | 21 carved out | not calibrated |

- **Process** disagreed with the labels on 4 of 13 cases, one each for tool selection, tool output utilization, tool
  call success and tool efficiency. It also failed its gate before the judges received their rubrics (76.9%, κ
  0.494).
- **Quality and Reasoning are INCOMPLETE for reasons in their golden sets, not the judge.** One goal-decomposition
  case has no plan to grade and reaches no verdict, by design. Four of the reasoning-correctness cases carry no
  reasoning trace. An evaluator not measured on every one of its cases is left out of its category whole, so
  Reasoning has nothing left to score.
- **Cases** counts the cases scored. Evaluators that a single-turn golden case cannot exercise are carved out of
  `calibrate` and not counted; the [agentic how-it-works page](agentic/how-it-works.md#calibration-quality-today)
  lists them and why.
