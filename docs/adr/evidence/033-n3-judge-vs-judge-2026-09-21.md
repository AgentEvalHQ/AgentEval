# ADR-033 evidence — sample N3, Judge vs Judge, 2026-09-21

The evaluator evaluates the evaluators. Before a decision model decides anything in AgentEval, it is
measured the way every agent is: against labels, beside the judge it would replace. This file records
the first such measurement. Sample N3 (`dotnet run -- 102`) produced it; the sample reuses the agentic
`CalibrationRunner` and the shared `EvalRegistry`, so both judges were scored on the **same rubrics** by
the **same harness** the CLI's `bench agentic calibrate` uses. Nothing here is a second harness.

Released code: `v0.40.0-beta` plus the N3 sample. Golden set: `tests/AgentEval.Tests/Agentic/Calibration/Golden/`,
22 files, **378 cases** (238 labelled `pass`, 140 `fail`), 49 evaluator keys.

## What ran

| | Arm A — generative judge | Arm B — decision model |
|---|---|---|
| model | `zai-org/GLM-5.3-Flash@bitdeer` (echoed by the provider) | `jev-1.13.0@typesafe` (echoed; `jev-latest` requested) |
| path | `ChatClientEvaluator` → the evaluator's own criteria → JSON verdict | `DecisionJudge` → one binary question per criterion, one request per judge call → P(met) |
| run 1 (both arms) | 1 repeat, 4 files in parallel, 1,119 s | same run, 26 s of it |
| run 2 (arm B only) | — | 3 repeats, 80 s, 1,041 requests |

Of the 378 cases, **40 carry keys the registry does not dispatch** — the nine multi-turn and trace-dependent
keys that `BenchAgenticCalibrateCommand`'s carve-out list omits **deliberately**, with its reason recorded
there: their grading semantics do not fit a single-turn calibration entry, so an LLM judge would measure the
judge rather than the evaluator. Counted here, not hidden and **40 decide in code** without a judge leaf (`unsafe_tool_use`, the
regex path of `prompt_leak`, and the like); those cannot tell the judges apart and are excluded. **298 cases
compare the judges.** Zero transport errors on either arm. Dry-run first: the 40 dispatched keys resolved (the
nine unknown ones listed), every dispatched case run against a judge that recorded its criteria and sent nothing, the first Jev request rendered through the
real serializer (1,710 bytes, four questions).

## The comparison — run 1, 298 cases

| file | n | acc A | acc B | κ A | κ B | false-pass A | false-pass B | band A | band B | Brier A | Brier B | p50 A | p50 B | tokens A | tokens B |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 20-process | 20 | 0.850 | 0.700 | 0.681 | 0.400 | 0.250 | 0.250 | 0.700 | 0.450 | 0.119 | 0.153 | 12,062 | 309 | 68,070 | 22,056 |
| 20-quality | 18 | 0.833 | 0.889 | 0.675 | 0.778 | 0.000 | 0.000 | 0.722 | 0.722 | 0.070 | 0.052 | 11,148 | 312 | 55,446 | 16,711 |
| 20-system | 20 | 0.750 | 0.750 | 0.500 | 0.500 | 0.300 | 0.300 | 0.450 | 0.500 | 0.195 | 0.185 | 13,689 | 307 | 78,969 | 23,184 |
| adversarial-direct | 10 | 0.500 | 0.400 | 0.194 | 0.091 | 0.000 | 0.000 | 0.900 | 0.200 | 0.006 | 0.054 | 9,990 | 313 | 13,367 | 5,711 |
| code-vulnerability | 20 | 1.000 | 0.850 | 1.000 | 0.700 | 0.000 | 0.200 | 0.500 | 0.450 | 0.134 | 0.265 | 12,082 | 301 | 45,844 | 17,265 |
| confidence-calibration | 8 | 1.000 | 1.000 | 1.000 | 1.000 | 0.000 | 0.000 | 1.000 | 0.750 | 0.020 | 0.015 | 7,706 | 293 | 11,809 | 4,798 |
| escalation-resistance | 5 | 1.000 | 1.000 | 1.000 | 1.000 | 0.000 | 0.000 | 1.000 | 0.600 | 0.002 | 0.011 | 10,489 | 338 | 0 | 0 |
| hate-unfairness | 22 | 1.000 | 0.955 | 1.000 | 0.899 | 0.000 | 0.000 | 0.909 | 0.818 | 0.006 | 0.016 | 6,269 | 297 | 36,081 | 15,157 |
| indirect-attack | 20 | 1.000 | 0.850 | 1.000 | 0.688 | 0.000 | 0.000 | 0.700 | 0.650 | 0.033 | 0.065 | 9,035 | 301 | 36,630 | 14,170 |
| prompt-leak | 3 | 0.667 | 0.333 | 0.400 | 0.000 | 0.000 | 0.000 | 0.667 | 0.000 | 0.017 | 0.086 | 29,242 | 280 | 11,213 | 2,173 |
| protected-material | 5 | 1.000 | 0.800 | 1.000 | 0.545 | 0.000 | 0.000 | 0.400 | 0.200 | 0.121 | 0.215 | 11,023 | 281 | 10,073 | 3,687 |
| reasoning | 4 | 1.000 | 0.500 | 1.000 | 0.200 | 0.000 | 0.000 | 0.500 | 0.250 | 0.035 | 0.099 | 14,662 | 268 | 9,173 | 2,874 |
| reasoning-correctness | 5 | 1.000 | 0.800 | 1.000 | 0.545 | 0.000 | 0.000 | 0.800 | 0.600 | 0.071 | 0.031 | 12,556 | 312 | 12,009 | 3,843 |
| self-harm | 25 | 0.880 | 0.840 | 0.749 | 0.675 | 0.000 | 0.000 | 0.960 | 0.800 | 0.013 | 0.034 | 11,764 | 278 | 51,188 | 18,337 |
| sensitive-data-leakage | 15 | 0.867 | 0.600 | 0.444 | 0.151 | 0.000 | 0.000 | 0.867 | 0.600 | 0.027 | 0.057 | 7,624 | 290 | 21,286 | 9,006 |
| sexual | 26 | 1.000 | 0.769 | 1.000 | 0.552 | 0.000 | 0.000 | 0.769 | 0.615 | 0.027 | 0.057 | 7,608 | 297 | 52,736 | 18,111 |
| system-prompt-leakage | 15 | 1.000 | 0.867 | 1.000 | 0.706 | 0.000 | 0.000 | 0.867 | 0.600 | 0.013 | 0.075 | 10,066 | 284 | 29,189 | 10,411 |
| ungrounded-attributes | 20 | 0.950 | 1.000 | 0.898 | 1.000 | 0.111 | 0.000 | 0.600 | 0.550 | 0.129 | 0.250 | 10,662 | 295 | 39,518 | 16,159 |
| ux | 12 | 1.000 | 0.750 | 1.000 | 0.526 | 0.000 | 0.000 | 0.667 | 0.250 | 0.064 | 0.118 | 8,143 | 292 | 18,593 | 7,312 |
| violence | 25 | 1.000 | 0.640 | 1.000 | 0.363 | 0.000 | 0.000 | 0.880 | 0.600 | 0.010 | 0.027 | 7,868 | 290 | 49,078 | 17,767 |
| **ALL** | **298** | **0.923** | **0.792** | **0.837** | **0.589** | **0.056** | **0.065** | 0.752 | 0.574 | 0.059 | 0.096 | **10,591** | **299** | 650,272 | 228,732 |

`acc` = agreement with `expectedVerdict`; `κ` = Cohen's kappa against it; `false-pass` = share of
`fail`-labelled cases the judge passed; `band` = share of scores inside the golden `[min, max]`; `Brier` =
mean squared distance between the score and the label; `p50` = per-case wall-clock (all of a case's judge
calls); tokens are the judge's, from leaf provenance (`escalation-resistance` reports none on either arm,
a provenance gap in that evaluator, not a free call). Bitdeer is unpriced, so arm A shows tokens only; arm
B cost **$0.0090 for the run at list price, $0.000030 per case**.

**Judge-vs-judge agreement (same label on the same case): 84.9%.**

### The direction of the errors, which the accuracy hides

| | wrong | of which false **fail** (labelled pass, judged fail) | of which false **pass** (labelled fail, judged pass) |
|---|---:|---:|---:|
| A — GLM-5.3 Flash | 23 | 17 | 6 |
| B — Jev | 62 | **55** | **7** |
| both wrong | 20 | | |

Jev is thirteen points less accurate, and **fifty-five of its sixty-two errors are false fails**. On the
cases that matter for a gate — the `fail`-labelled ones — the two judges are within one case of each other
(7 vs 6 false passes on 108 such cases). Jev errs closed. Its false fails concentrate on the safety sets
where a pass-labelled response *discusses* the topic safely: `violence` 9, `adversarial-direct` 6,
`sensitive-data-leakage` 6, `sexual` 6, `20-process` 4, `self-harm` 4. On every safety file (`violence`,
`sexual`, `self-harm`, `hate-unfairness`) Jev's false-pass rate was **0.000**.

The one exception runs the other way: **`code-vulnerability`**, where Jev passed 20% of the fail-labelled
cases and GLM none. Code review is where the documented literal reading costs most.

## Repeat stability — run 2, arm B, three repeats

| | value |
|---|---|
| cases × repeats | 298 × 3 (1,041 requests, 0 errors) |
| accuracy / κ / false-pass / false-fail | 0.790 / 0.584 / 0.074 / 0.288 (run 1: 0.792 / 0.589 / 0.065 / 0.289) |
| **verdict flips across the three repeats** | **0.3%** of cases |
| per request | p50 288 ms, p90 336 ms |
| tokens / cost | 639,297 in / 69,852 out; $0.0269 at list price |

The ≈0.01 noise floor measured on identical bytes on 2026-09-20 holds at 298 cases: one verdict in three
hundred moved between repeats.

## The seven pre-registered hypotheses (factsheet §7, written before the run)

| | hypothesis | verdict | what the run says |
|---|---|---|---|
| H1 | ≥ 85% on process/quality/system/memory-multiturn and within 5 pts of the generative judge | **refuted** | Jev 77.6% vs GLM 81.0% on 58 cases; the gap (3.4 pts) is inside the bound, the level is not |
| H2 | higher false-pass than the generative judge on the adversarial sets | **open** | 9 fail-labelled adversarial cases reached a judge; the sample's floor is 10. On those 9, both judges passed none |
| H3 | sharp but not calibrated: Brier ≤ 0.10 when right, probability still ≥ 0.9 / ≤ 0.1 when wrong | **refuted** | Brier when right 0.080 / 0.077; but only 27–30% of the wrong cases carry an extreme probability. Jev is **not** confidently wrong; its mistakes sit near the middle |
| H4 | the largest gap on counting / arithmetic / dates (confidence-calibration, code-vulnerability) | **refuted** | gap 10.7 pts on those files vs 13.3 elsewhere; the gap concentrates on the safety sets, not the numeric ones |
| H5 | p50 < 400 ms, p90 < 800 ms, < $0.0002 per case | **confirmed** | p50 296 / 288 ms, p90 350 / 336 ms, $0.000030 per case |
| H6 | < 2% of verdicts flip across three repeats | **confirmed** | 0.3% |
| H7 | negated criteria answered with lower agreement than positive ones | **open** | P(met) ≥ 0.5 on pass-labelled cases: negated 93.9% (1,626 criteria) vs positive 94.9% (777). A one-point difference; the pre-registered bound was five |

Three hypotheses refuted, two confirmed, two open. The refutations are the useful part: the expected
weaknesses (numeric criteria, negation, confident errors) did not show; the actual weakness (strictness
on safety topics discussed safely) was not on the list.

## Go / no-go by category, for the proposal's next step

Read with the sample sizes. A row with n < 10 is a hint, not a verdict.

| Jev is at least as good, or within 5 pts | Jev is more than 10 pts behind | too few cases to say |
|---|---|---|
| `20-quality` (+5.6), `20-system` (=), `confidence-calibration` (=, n 8), `hate-unfairness` (−4.5), `ungrounded-attributes` (+5.0), `self-harm` (−4.0) | `violence` (−36), `sexual` (−23), `sensitive-data-leakage` (−27), `ux` (−25), `code-vulnerability` (−15, and the only file where Jev's false-pass is higher), `20-process` (−15), `indirect-attack` (−15), `system-prompt-leakage` (−13) | `prompt-leak` 3, `reasoning` 4, `protected-material` 5, `reasoning-correctness` 5, `escalation-resistance` 5, `adversarial-direct` 10 (both judges poor: 0.50 / 0.40) |

## What this does and does not establish

- **Established:** on the repo's own agentic labels, through the evaluators' own rubrics, Jev agrees with
  the label on 79% of cases against GLM-5.3 Flash's 92%, at 1/35 of the latency and roughly 1/3 of the
  tokens; its errors are false fails almost entirely, its false-pass rate matches the generative judge's,
  and its verdicts are stable across repeats. That is the profile of an evaluator that can **sit beside** a
  judge as independent, cheap, fail-closed evidence — and, per category, of one that cannot yet **replace**
  a judge where the label calls for reading a safety topic in context.
- **Not established:** anything about Jev against a stronger generative judge (arm A is GLM-5.3 Flash,
  a mid-size reasoning model; the goldens were originally calibrated against an Azure OpenAI judge that
  no longer exists here), anything about a single repeat of arm A, anything about the 40 cases whose keys
  the registry does not dispatch, or anything about the compliance families — ADR-033 §7 (2) names GDPR
  or Agentic, and this is the Agentic half through the judge adapter, not `DecisionEval` leaves in a
  composite.
- **Provenance caveat, repeated:** through the registry both arms land as `atomic-llm` leaves. Right for
  A, a misnomer for B. Every result here stayed in memory; nothing was persisted through the adapter.

## Reproduce

```text
dotnet run -- 102 --dry-run                      # nothing sent
dotnet run -- 102 --limit 1                      # one case per file, both judges
dotnet run -- 102                                # run 1: all cases, both judges (~19 min, GLM-bound)
dotnet run -- 102 --arms B --repeats 3           # run 2: Jev alone, three repeats (~80 s)
```

Reports (JSON with every record and every Jev probability, plus Markdown) are written to
`%TEMP%/agenteval-n3/` or `--out DIR`. The two runs above are the 2026-09-21 00:41Z and 00:54Z reports.
