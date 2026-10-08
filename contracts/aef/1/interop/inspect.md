# Inspect AI

**Version read:** `inspect_ai` 0.3.277 (tag `0.3.277`, commit `aa20052a65b13516f1ee79d10ccceda00c205cc6`, published on
PyPI on 2026-10-06). Sources: `src/inspect_ai/log/_log.py`, `log/_edit.py`, `scorer/_metric.py`,
`scorer/_reducer/reducer.py`, `core/_model_output.py`, `log/_recorders/eval.py`, `_util/zipfile.py`,
`docs/eval-logs.qmd`, and the test fixture `tests/log/test_eval_log/log_formats.json`. Log format version 2.

## The target

An Inspect run writes one **`EvalLog`** per task and model.

| Field | What it holds |
|---|---|
| `version` | the log format version, 2 |
| `status` | `started`, `success`, `cancelled` or `error` (the documentation lists three; the code has four) |
| `eval` (`EvalSpec`) | `eval_id`, `run_id`, `eval_set_id`, `created`, `task`, `task_version`, `task_registry_name`, `dataset` (`name`, `location`, `samples`, `sample_ids`), `model`, `model_base_url`, `model_roles` (for example a grader model), `model_generate_config`, `config` (`epochs`, `epochs_reducer`, limits), `revision` (git origin, commit), `packages`, `scorers`, `metadata` |
| `plan` | the solver steps and the generation config |
| `results` | `total_samples`, `completed_samples`, and per scorer an `EvalScore`: `name`, `scorer`, `reducer`, `scored_samples`, `unscored_samples`, `metrics` (each `name`, `value`, `params`), plus `headline` |
| `stats` | `started_at`, `completed_at`, `model_usage` and `role_usage` per model or role |
| `error` | `message`, `traceback` |
| `samples[]` (`EvalSample`) | `id` (int or string), `epoch` (1-based), `input`, `target`, `messages`, `output`, `scores` (a map from scorer name to `Score`), `metadata`, `events`, `model_usage`, `role_usage`, `started_at`, `completed_at`, `total_time`, `working_time`, `error`, `limit`, `invalidation` |
| `reductions` | per scorer and reducer, the per-sample score across epochs |
| `log_updates`, `tags`, `metadata` | post-run edits to tags and metadata, with their author and reason |

A **`Score`** has `value`, `answer`, `explanation`, `reason`, `metadata` and `history`. `value` is a string, a number,
a boolean, a list of those, or a map of those. The letters `C`, `I`, `P` and `N` mean correct, incorrect, partial and
no answer; metrics read them as 1, 0, 0.5 and 0. `reason` names an abnormal score. Three reasons blame the model under
test: `refusal`, `no_response`, `invalid_response_format`. Two blame the instrument: `grader_failed`,
`scoring_failed`.

An unscored sample has `value` NaN, which metrics and reducers skip. Inspect writes it as the bare token `NaN`, which
is not valid JSON. Every edit to a score is appended to `history` with a `provenance` (`author`, `reason`,
`timestamp`), and the edit rewrites the log file in place.

Epoch reducers: `mode`, `majority`, `mean`, `median`, `max`, `at_least`, `pass_at`, `pass_k`, `collect`.

**Files.** `.eval` has been the default since v0.3.46. It is a ZIP holding `header.json`, `samples/<id>_epoch_<n>.json`,
`summaries.json`, `reductions.json` and a `_journal/` folder, and its entries are compressed with zstd. A ZIP reader
without zstd support cannot open it. `.json` holds the same `EvalLog` as one JSON document. Inspect's documentation
tells other languages to get JSON with `inspect log dump` or `inspect log convert`.

## AEF → Inspect

One AEF run gives one `EvalLog` in `.json` form. Writing `.eval` also needs zstd.

| AEF | Inspect | Fidelity |
|---|---|---|
| `runId` | `eval.eval_id` and `eval.run_id` | exact |
| `status` | `completed` → `success`; `aborted` → `error`, with `abortReason` as `error.message`; `running` → `started` | exact |
| `startedAt`, `endedAt` | `eval.created`, `stats.started_at`, `stats.completed_at` | exact |
| `producer` | `eval.packages` | lossy: Inspect has no field for the producing tool |
| `suite.ref`, `suite.version` | `eval.task`, `eval.task_version` | exact |
| `suite.digest` | `eval.metadata` | none |
| `subject` | `eval.model` (a `provider/model` string) | lossy: an agent or workflow subject has no natural value |
| `judges[].model` | `eval.model_roles` | exact for the model; `calibration` and `rubricDigest` go to `eval.metadata` |
| `suite.executionPolicy.trialsPerCase` | `eval.config.epochs` | exact |
| `execution`, `contentCapture`, `deployment` | `eval.metadata` | none |
| a result line's `caseId` | `samples[].id` | exact |
| `trial` | `samples[].epoch` = `trial` + 1 | exact |
| `path` | the key in `samples[].scores` | exact |
| `scores[0].value` | `Score.value` | exact for one score; a second score on the same line has no place |
| `state` | `Score.metadata` | none: Inspect has no verdict field |
| `reason`, or the `reasoning` blob's text | `Score.explanation` | exact text |
| typed absences (`not_measured`, `not_applicable`, `skipped`, `error`) | `Score.value` NaN, with the AEF state in `Score.reason` | lossy: one unscored value for four states, and NaN is not JSON |
| `parentResultId`, `component`, `aggregation`, `verdictRule`, `severity`, `annotator` | `Score.metadata` | none |
| `trials` rollup with `MajorityVote` | `reductions[]` with reducer `majority` | exact |
| `usage` | `samples[].model_usage`, `samples[].role_usage` | exact for tokens and `costUsd` (`total_cost`) |
| `durationMs` | `samples[].total_time` (seconds) | exact |
| `summary.json` entries | `results.scores[].metrics` (`mean`, `stderr`) | exact for the value and standard error; `N`, `notMeasured`, `verdict`, `rule`, `ci` go to metadata |
| `evidence`, blobs | none | none |
| `traces.otlp.jsonl` | none: Inspect records its own events, which are not OTLP | none |
| `gates.ndjson` | none | none |
| overlays `override`, `adjudicate` ([OVL-1](../spec/04-integrity.md#42-overlays)) | `Score.history` entries with `provenance` (`author` from `by.identity`, `reason`, `timestamp` from `at`) | lossy: Inspect recomputes metrics after an edit; AEF's effective view does not recompute summaries ([OVL-7](../spec/04-integrity.md#43-the-effective-view)) |
| overlays `approve`, `reject`, `waive`, `annotate` | `log_updates` | lossy |
| `seal.json`, signatures, the overlay chain | none: an Inspect log is edited in place | none |
| (case content) | `samples[].input`, `target` are required; AEF has them only as blobs | lossy ([I2](README.md#gaps-found-by-these-mappings)) |

## Inspect → AEF

| Inspect | AEF | Fidelity |
|---|---|---|
| `eval.eval_id` (or `eval.run_id` when `eval_id` is empty, in older logs) | `runId` | exact |
| `eval.run_id`, `eval.eval_set_id` | `ext` | none |
| `status` | `success` → `completed`; `error` → `aborted` with `abortReason` = `error.message`; `cancelled` → `aborted`; `started` → `running` | exact |
| `eval.created`, `stats.started_at`, `stats.completed_at` (with an offset) | `startedAt`, `endedAt` in UTC | exact instant |
| `eval.packages.inspect_ai` | `producer` (`name: inspect_ai`, `version`) | exact |
| `eval.task`, `eval.task_version` | `suite.ref` (`suite:<task>`), `suite.version` | exact |
| `eval.dataset` | `ext` | none: Inspect records no dataset digest, so `suite.digest` stays empty |
| `eval.model`, `eval.solver`, `plan` | `subject` (`kind: model`, `ref: model:<provider/model>`) | lossy: an Inspect subject is a solver and a model; AEF has one `ref` ([I7](README.md#gaps-found-by-these-mappings)) |
| `eval.model_roles` | `judges[]` (`model`) | exact |
| `eval.config.epochs` | `suite.executionPolicy.trialsPerCase` | exact |
| `eval.config.epochs_reducer` | `trials.aggregation` | lossy: only `majority` and `mode` have an AEF value (`MajorityVote`) ([I5](README.md#gaps-found-by-these-mappings)) |
| `samples[].id` | `caseId` (as a string) | exact |
| `samples[].epoch` | `trial` = `epoch` − 1, when there is more than one epoch | exact |
| a key of `samples[].scores` | `path`; the scorer's registry name to `evaluator.id` | exact |
| `Score.value` as a number | `scores[].value` | exact, but `state` has no source: Inspect sets no pass threshold ([I1](README.md#gaps-found-by-these-mappings)) |
| `Score.value` `C` / `I` | `state` `passed` / `failed`; `scores[].value` 1 / 0 | exact |
| `Score.value` `P`, `N` | `state` `warn` or `failed`; value 0.5 or 0 | lossy: the letter has no field ([I1](README.md#gaps-found-by-these-mappings)) |
| `Score.value` as a map | one `scores[]` entry per numeric member | lossy: string members have no field |
| `Score.value` as a list, or another string | `ext` | none ([I1](README.md#gaps-found-by-these-mappings)) |
| `Score.value` NaN with `reason` `grader_failed` or `scoring_failed` | `state` `error` or `not_measured`, with a `reason` ([RES-2](../spec/03-run.md#341-states)) | exact |
| `Score.reason` `refusal`, `no_response`, `invalid_response_format` | `state: failed`, with the reason in `reason` | exact |
| `Score.explanation` | `reason`, or a `reasoning` blob when `contentCapture` is `on` | exact |
| `Score.answer`, `Score.metadata` | `ext` | none |
| `Score.history` edits | overlay `override` events targeting the result, with `by.identity` = `author`, `reason`, `at` = `timestamp`; the sealed line holds the original score | lossy: summaries are not recomputed in AEF ([OVL-7](../spec/04-integrity.md#43-the-effective-view)) |
| `samples[].error`, `samples[].limit` | `state: error` (or `not_measured` for a limit), with the message or limit in `reason` | exact |
| `samples[].input`, `target`, `messages`, `output` | blobs with evidence records, only when `contentCapture` is `on` | lossy ([I2](README.md#gaps-found-by-these-mappings)) |
| `samples[].events` | none: Inspect's events are not OTLP spans | none |
| `samples[].model_usage`, `role_usage` | `usage` (input and output tokens, `costUsd`, one `role`) | lossy: cache and reasoning tokens, and a second role, have no field ([I4](README.md#gaps-found-by-these-mappings)) |
| `samples[].total_time` | `durationMs` | exact |
| `samples[].started_at`, `completed_at`, `working_time` | none | none ([I6](README.md#gaps-found-by-these-mappings)) |
| `samples[].invalidation`, `log_updates` | overlay `annotate` events | lossy: AEF has no "invalidated" state |
| `results.scores[].metrics` that are the mean of the sample values (`accuracy`, `mean`) | a `summary.json` entry for a metric of kind `score`: `n` = `scored_samples`, `notMeasured` = `unscored_samples` | exact, because the verifier's recomputation ([SUM-5](../spec/03-run.md#36-summaryjson)) gives the same mean. A metric of kind `rate` would count `P` as 0, where Inspect counts 0.5. |
| `stderr` | the entry's `stderr` | exact |
| other metrics (custom, grouped, bootstrap, `pass_at_k`), `headline` | `ext` | none ([I1](README.md#gaps-found-by-these-mappings)) |
| (no verdict) | the summary entry's `verdict` has no source | none ([I1](README.md#gaps-found-by-these-mappings)) |
| `stats.model_usage` | `summary.cost.totalUsd` from `total_cost` | lossy: run-level tokens have no field ([I4](README.md#gaps-found-by-these-mappings)) |

## What does not carry over

**AEF → Inspect.** Which typed absence a result was: Inspect has one unscored value. `warn` and `inconclusive`. The
result tree and its aggregation, except as metadata. Severity, verdict rules, thresholds and uncertainty. Evidence
links and digests, traces, gate decisions. The seal, signatures and the overlay chain: an Inspect log is mutable.
`execution.targetMode`, judge calibration. The case content, unless the run captured it.

**Inspect → AEF.** A verdict for numeric scores, and categorical or list values ([I1](README.md#gaps-found-by-these-mappings)). Metrics
that are not a mean of the sample values ([I1](README.md#gaps-found-by-these-mappings)). Reducers other than majority
([I5](README.md#gaps-found-by-these-mappings)). Sample and run timing ([I6](README.md#gaps-found-by-these-mappings)). Cache and reasoning token counts and
run-level usage ([I4](README.md#gaps-found-by-these-mappings)). The case content when the importer writes `contentCapture: off`
([I2](README.md#gaps-found-by-these-mappings)). Inspect's event transcript. Groups of runs (`eval_set_id`).

## Worked example

The corpus line for `triage/policy` of `case-17`. Its `resultId` recomputes with
[RES-4](../spec/03-run.md#342-result-ids):

```json
{"schemaVersion":"1.0","resultId":"r_bb4438fbedb43552a9fe55695931d5e4","parentResultId":"r_479d157f3423e95d566bcbfc0c6d2461","caseId":"case-17","path":"triage/policy","evaluator":{"id":"code:refund-escalation","version":"1"},"state":"passed","scores":[{"metric":"policy","value":1.0,"normalized":1.0}],"annotator":{"kind":"CODE"},"component":{"weight":0.5,"required":true}}
```

It becomes the `triage/policy` score of the Inspect sample for `case-17`. The case's other scored lines, `triage`
(`r_479d157f3423e95d566bcbfc0c6d2461`) and `triage/helpfulness` (`r_1264eeb36620c9cbe97b71ffdbcfd331`), go into the
same sample. The run has one trial per case, so the sample is epoch 1. AEF keeps no input or target for the case, so
those required fields are empty ([I2](README.md#gaps-found-by-these-mappings)). The AEF facts Inspect has no field for go under
`metadata.aef`:

```json
{
  "id": "case-17",
  "epoch": 1,
  "input": "",
  "target": "",
  "scores": {
    "triage": {
      "value": 0.55,
      "metadata": {"aef": {"resultId": "r_479d157f3423e95d566bcbfc0c6d2461", "state": "failed", "evaluator": {"id": "composite:triage", "version": "2"}, "severity": "medium"}}
    },
    "triage/policy": {
      "value": 1.0,
      "metadata": {"aef": {"resultId": "r_bb4438fbedb43552a9fe55695931d5e4", "state": "passed", "evaluator": {"id": "code:refund-escalation", "version": "1"}, "parentResultId": "r_479d157f3423e95d566bcbfc0c6d2461", "component": {"weight": 0.5, "required": true}}}
    },
    "triage/helpfulness": {
      "value": 0.1,
      "explanation": "The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n",
      "metadata": {"aef": {"resultId": "r_1264eeb36620c9cbe97b71ffdbcfd331", "state": "failed", "evaluator": {"id": "llm:helpfulness", "version": "3"}, "parentResultId": "r_479d157f3423e95d566bcbfc0c6d2461", "severity": "medium", "component": {"weight": 0.5, "required": false}}}
    }
  },
  "model_usage": {"gpt-5.1": {"input_tokens": 1747, "output_tokens": 488, "total_tokens": 2235, "total_cost": 0.012}},
  "role_usage": {"judge": {"input_tokens": 1747, "output_tokens": 488, "total_tokens": 2235, "total_cost": 0.012}},
  "total_time": 4.21
}
```

The fourth line of the case, `triage/groundedness` (`not_applicable`), has no score. Inspect would write it with the
NaN token, which is not JSON, so it is shown apart:

```text
"triage/groundedness": {"value": NaN, "reason": "not_applicable", "explanation": "No retrieved context was recorded for this case: nothing to ground against."}
```

Read back, `id` gives `caseId` `case-17`, each score key gives the `path`, and a single epoch gives no `trial`. RES-4
then gives the same three ids as the corpus: `r_479d157f3423e95d566bcbfc0c6d2461`, `r_bb4438fbedb43552a9fe55695931d5e4`
and `r_1264eeb36620c9cbe97b71ffdbcfd331`. The states come back only from `metadata.aef`.

## Open gaps

- [I1](README.md#gaps-found-by-these-mappings): numeric scores without a pass rule, letters and maps, and metrics that are not means.
- [I2](README.md#gaps-found-by-these-mappings): Inspect requires `input` and `target`; AEF has them only as captured blobs.
- [I4](README.md#gaps-found-by-these-mappings): cache and reasoning tokens, usage of several roles, run-level usage.
- [I5](README.md#gaps-found-by-these-mappings): Inspect's reducers other than majority.
- [I6](README.md#gaps-found-by-these-mappings): sample start and end times.
- [I7](README.md#gaps-found-by-these-mappings): the subject and target mode an importer supplies.
