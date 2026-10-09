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

A `ModelUsage` has `input_tokens`, `output_tokens`, `total_tokens`, `input_tokens_cache_read`,
`input_tokens_cache_write`, `reasoning_tokens` and `total_cost`.

Epoch reducers: `mode`, `majority`, `mean`, `median`, `max`, `at_least`, `pass_at`, `pass_k`, `collect`.

**Files.** `.eval` has been the default since v0.3.46. It is a ZIP holding `header.json`,
`samples/<id>_epoch_<n>.json`, `summaries.json`, `reductions.json` and a `_journal/` folder, and its entries are
compressed with zstd, so a ZIP reader without zstd cannot open it. `.json` holds the same `EvalLog` as one JSON
document. Inspect's documentation tells other languages to get JSON with `inspect log dump` or `inspect log convert`.

## AEF → Inspect

One AEF run gives one `EvalLog` in `.json` form. Writing `.eval` also needs zstd. The rules and refusals below the
table cover what the table does not; the reference converter, `tools/aef_interop.py to-inspect`, follows both.

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
| `suite.executionPolicy.aggregation` | `eval.config.epochs_reducer`: `MajorityVote` → `majority`, `Mean` → `mean`, `Median` → `median`, `Max` → `max`, `PassAtK` → `pass_at_<k>`, `AnyPass` → `at_least_1`, `AllPass` → `at_least_<n>` | exact |
| `execution`, `contentCapture`, `deployment`, `imported` | `eval.metadata` | none |
| a result line's `caseId` | `samples[].id` | exact |
| `trial` | `samples[].epoch` = `trial` + 1 | exact |
| `path` | the key in `samples[].scores` | exact |
| `scores[0].value` | `Score.value` | exact for one score; a second score on the same line goes in a map value |
| `scores[].label` (e.g. `C`) | `Score.value` as that string | exact |
| `state` | `Score.metadata` | none: Inspect has no verdict field |
| `reason`, or the `reasoning` blob's text | `Score.explanation` | exact text |
| typed absences (`not_measured`, `not_applicable`, `skipped`, `error`) | `Score.value` NaN, with the state name in `Score.reason` | lossy: one unscored value for four states, and NaN is not JSON |
| `parentResultId`, `component`, `aggregation`, `verdictRule`, `severity`, `annotator` | `Score.metadata` | none |
| a `trials` rollup | `reductions[]` with the matching reducer | exact |
| `usage` entries | `samples[].role_usage` keyed by `role`, tokens and `costUsd` (as `total_cost`) | exact for tokens and cost |
| a `usage` entry's `model` ([RES-10](../spec/03-run.md#345-facts-about-a-result)), or `annotator.model` for a judge's entry without one | `samples[].model_usage` keyed by model | exact when each role used one model; for a panel of judges, Inspect keeps totals per role and per model, not per role and model |
| `startedAt`, `endedAt` of a case's root line | `samples[].started_at`, `completed_at` | exact; per-score times have no field |
| `durationMs` | `samples[].total_time` (seconds) | exact |
| evidence `input`, `expected` | `samples[].input`, `target` (required) | exact when the run captured them; empty otherwise |
| evidence `output`, `transcript` | `samples[].output`, `messages` | exact when captured |
| other evidence, blobs | none | none |
| `summary.json` entries | `results.scores[].metrics` (the value, `stderr`) | exact for the value and standard error; `N`, `notMeasured`, `verdict`, `rule`, `ci` go to metadata |
| `summary.json` `usage` ([SUM-7](../spec/03-run.md#36-summaryjson)) | `stats.role_usage` keyed by `role`, and `stats.model_usage` keyed by `model`; tokens as for a sample, `costUsd` as `total_cost` | exact when each role used one model; otherwise the tokens and cost survive, but Inspect keeps totals per role and per model, not per role and model |
| `traces.otlp.jsonl`, `logs.otlp.jsonl` | none: Inspect records its own events, which are not OTLP | none |
| `gates.ndjson` | none | none |
| overlays `override`, `adjudicate` ([OVL-1](../spec/04-integrity.md#42-overlays)) | `Score.history` entries with `provenance` (`author` from `by.identity`, `reason`, `timestamp` from `at`) | lossy: Inspect recomputes metrics after an edit; AEF's effective view does not recompute summaries ([OVL-7](../spec/04-integrity.md#43-the-effective-view)) |
| overlays `approve`, `reject`, `waive`, `annotate` | `log_updates` | lossy |
| `seal.json`, signatures, the overlay chain | none: an Inspect log is edited in place | none |

**Beyond the table** (settled 10-09; the ids are those of the former "Still open" items):

- **The eval header Inspect requires** (IN-1). `eval.task` is the suite's ref without `suite:`. `eval.model` is a
  model subject's ref without `model:`, and for any other subject (an agent, a workflow) the subject's `ref` as
  written. The converter writes `eval.dataset` with the number of cases and their ids, and `eval.model_roles` with the
  run's judge under the role `judge`, the name AEF's `usage` gives it.
- **A measured line without a score** (IN-2). A `passed`, `failed`, `warn`, `inconclusive` or `scored` line without
  `scores` (a code check's verdict, a split panel) is `Score.value` NaN with the state name in `Score.reason`, as a
  typed absence is.
- **The shape of `results`** (IN-3). The converter writes one `EvalScore` per summary entry: `name` and `scorer` the
  entry's path, `scored_samples` its `n`, `unscored_samples` its `notMeasured`, `metrics` its value under `mean` (or
  under its `aggregate`'s method, with the aggregate's other members as `params`; NaN when the value is null) and its
  `stderr`; the entry's lane, metric, `N`, `notMeasured`, `verdict`, `rule` and `ci` under `metadata.aef`.
  `total_samples` and `completed_samples` are the number of samples.
- **A sample's usage** (IN-5) is the sum of its lines' entries, per role and per model, as Inspect keeps it.
- **Only a run that verifies** (IN-11, as OT-8 in [opentelemetry.md](opentelemetry.md); R7N-6). The converter
  converts a run only when `tools/aef_verify.py run` finds it `intact` or `unsealed`, with no problem but an
  authorized withhold, and exports its sealed lines: overlays are not applied (R7N-5).

**Refused** (IN-1, IN-3 to IN-5, IN-11; settled 10-09). The converter refuses these, naming the rule, and writes
nothing:

- a run that does not verify (IN-11);
- a run without a `suite`, or whose suite ref is not `suite:<task>`: `eval.task` is required (IN-1);
- a run with more than one judge: a role of `eval.model_roles` holds one model (IN-1);
- two summary entries at one path, since Inspect has one `EvalScore` per scorer; and an entry for a metric of kind
  `count`, whose value is a sum, not a mean (IN-3);
- a run with overlay events: the table sends `override` and `adjudicate` to `Score.history` and the other kinds to
  `log_updates`, but not the shape of either entry. Asked to leave them out (`--ignore-overlays`), as
  [`examples/aef-inspect/`](examples/aef-inspect/) does, the converter converts the rest (IN-4);
- `output` or `transcript` evidence: `samples[].output` is a `ModelOutput` and `messages` a list of `ChatMessage`, and
  the table does not say how a blob's text becomes either (IN-5);
- `input` or `expected` evidence that is not a blob of the run, or two such records with different text for one
  sample (IN-5);
- a sample whose root lines carry two `startedAt`, `endedAt` or `durationMs`: the table takes them from "a case's
  root line", and a case can have several roots (IN-5).

## Inspect → AEF

The converted run names the converter in `producer` and the source in `imported`
(`from: "inspect_ai 0.3.277"`), and lists in `imported.asserted` each `run.json` field it had to supply
([RUN-15](../spec/03-run.md#32-runjson)).

| Inspect | AEF | Fidelity |
|---|---|---|
| `eval.eval_id` (or `eval.run_id` when `eval_id` is empty, in older logs) | `runId` | exact |
| `eval.run_id`, `eval.eval_set_id` | `ext` | none |
| `status` | `success` → `completed`; `error` → `aborted` with `abortReason` = `error.message`; `cancelled` → `aborted` with `abortReason` "cancelled" ([RUN-5](../spec/03-run.md#32-runjson)); `started` → `running` | exact |
| `eval.created`, `stats.started_at`, `stats.completed_at` (with an offset) | `startedAt`, `endedAt` in UTC | exact instant |
| `eval.packages.inspect_ai` | `imported.from` | exact |
| `eval.task`, `eval.task_version` | `suite.ref` (`suite:<task>`), `suite.version` | exact |
| `eval.dataset` | `ext`; `suite.digest` stays empty, since Inspect records no digest of the dataset | none |
| `eval.model`, `eval.solver`, `plan` | `subject` (`kind: model`, `ref: model:<provider/model>`), listed in `imported.asserted` | lossy: an Inspect subject is a solver and a model; AEF has one `ref` |
| (Inspect does not say how the target was driven) | `execution.targetMode`, listed in `imported.asserted` | the converter's claim |
| `eval.model_roles` | `judges[]` (`model`) | exact |
| `eval.config.epochs` | `suite.executionPolicy.trialsPerCase` | exact |
| `eval.config.epochs_reducer` | `trials.aggregation` and `executionPolicy.aggregation`: `majority` and `mode` → `MajorityVote`, `mean` → `Mean`, `median` → `Median`, `max` → `Max`, `pass_at_<k>` → `PassAtK` with `k`, `at_least_1` → `AnyPass`, `at_least_<n>` with n = epochs → `AllPass` | exact for these; `at_least_<k>` for another k, `pass_k` and `collect` have no value |
| `samples[].id` | `caseId` (as a string) | exact |
| `samples[].epoch` | `trial` = `epoch` − 1 on every line of the sample, when there is more than one epoch | exact |
| `reductions` (a sample's score across epochs, per scorer) | one rollup line per case and path, with the reduced score and `trials`: `n` the epochs, `passed` the epochs whose line is `passed` ([RES-8](../spec/03-run.md#344-repeated-trials)) | exact |
| a key of `samples[].scores` | `path`; the scorer's registry name to `evaluator.id` | exact |
| `Score.value` as a number | `scores[].value`, `state: scored` ([RES-1](../spec/03-run.md#341-states)) | exact: Inspect sets no pass threshold |
| `Score.value` `C` / `I` | `state` `passed` / `failed`; `scores[]` with value 1 / 0 and `label` `C` / `I` | exact |
| `Score.value` `P`, `N` | `scores[]` with value 0.5 or 0 and `label` `P` or `N`; `state` `warn` for `P`, `failed` for `N` | exact: the letter is kept |
| `Score.value` as a map | one `scores[]` entry per member: numbers as `value`, strings as `label` beside a value the converter chooses | lossy for string members |
| `Score.value` as a list | `ext` | none |
| `Score.value` NaN with `reason` `grader_failed` or `scoring_failed` | `state` `error` or `not_measured`, with a `reason` ([RES-2](../spec/03-run.md#341-states)) | exact |
| `Score.reason` `refusal`, `no_response`, `invalid_response_format` | `state: failed`, with the reason in `reason` | exact |
| `Score.explanation` | `reason`, or a `reasoning` blob when `contentCapture` is `on` | exact |
| `Score.answer`, `Score.metadata` | `ext` | none |
| `Score.history` edits | overlay `override` events targeting the result, with `by.identity` = `author`, `reason`, `at` = `timestamp`; the sealed line holds the original score | lossy: summaries are not recomputed in AEF ([OVL-7](../spec/04-integrity.md#43-the-effective-view)) |
| `samples[].error`, `samples[].limit` | `state: error` (or `not_measured` for a limit), with the message or limit in `reason` | exact |
| `samples[].input`, `target`, `output`, `messages` | blobs cited by evidence of kind `input`, `expected`, `output`, `transcript` ([EVD-1](../spec/03-run.md#37-evidencendjson-and-blobs)), only with `contentCapture: on` ([RUN-11](../spec/03-run.md#32-runjson)) | exact when captured |
| `samples[].events` | none: Inspect's events are not OTLP spans | none |
| `samples[].role_usage` | `usage`, one entry per role: `gen_ai.usage.input_tokens`, `output_tokens`, `cache_read.input_tokens`, `cache_write.input_tokens`, `reasoning.output_tokens`, `costUsd` ([RES-10](../spec/03-run.md#345-facts-about-a-result)) | lossy: Inspect's role names become `agent`, `judge`, `attacker` or `other` |
| `samples[].model_usage` | each `usage` entry's `model` ([RES-10](../spec/03-run.md#345-facts-about-a-result)): the model `eval.model_roles` gives the entry's role, `eval.model` for `agent`; `annotator.model` for a judge | exact |
| `samples[].started_at`, `completed_at` | `startedAt`, `endedAt` on the case's lines | exact |
| `samples[].total_time` | `durationMs` | exact |
| `samples[].working_time` | none | none |
| `samples[].invalidation`, `log_updates` | overlay `annotate` events | lossy: AEF has no "invalidated" state |
| `results.scores[].metrics` that are the mean of the sample values (`accuracy`, `mean`) | a `summary.json` entry for a metric of kind `score`: `n` = `scored_samples`, `notMeasured` = `unscored_samples` | exact: the verifier's recomputation ([SUM-5](../spec/03-run.md#36-summaryjson)) gives the same mean. A metric of kind `rate` would count `P` as 0, where Inspect counts 0.5. |
| `stderr` | the entry's `stderr` | exact |
| other metrics (`pass_at_k`, a median, a custom metric) | a summary entry with `aggregate` (`method`, `k`): a median, minimum or maximum is recomputed; `pass_at_k` or a custom metric is the producer's, shown as written, and no lane reads it ([SUM-8](../spec/03-run.md#36-summaryjson)) | exact |
| (Inspect has no pass rule for a metric) | the summary entry's `verdict: scored`, and no `rule`: measured, no rule applied ([SUM-6](../spec/03-run.md#36-summaryjson)) | exact |
| `results.headline` | `ext` | none |
| `stats.model_usage`, `role_usage` (run totals) | `summary.json` `usage`: one entry per role and model, tokens named as on the result lines and `total_cost` as `costUsd` ([SUM-7](../spec/03-run.md#36-summaryjson)); the role from `eval.model_roles`, `agent` for `eval.model`; `cost.totalUsd`, the sum of `total_cost` | lossy: Inspect's role names become `agent`, `judge`, `attacker` or `other`; two roles that become one AEF role with the same model are added together ([SUM-9](../spec/03-run.md#36-summaryjson)) |

The reference converter, `tools/aef_interop.py from-inspect`, follows the table and these rules (settled 10-09):

- **The run header** (IN-6). `runId` is `eval.eval_id` (or `eval.run_id`). `startedAt` is `stats.started_at`, or
  `eval.created` when the log has none (then listed in `imported.asserted`); `endedAt` is `stats.completed_at`.
  `abortReason` is `error.message`. `imported.from` is `inspect_ai` and the version `eval.packages` gives, or
  `inspect_ai` alone. The names of `suite.ref` and `subject.ref` are encoded as
  [ENC-13](../spec/02-encoding.md#24-identifiers-and-names) says, and `suite.version` is `eval.task_version` as text
  (Inspect's default is 0). `execution.targetMode` and `contentCapture` come from the person converting (`on` unless
  asked otherwise); both are listed in `imported.asserted`, with `subject.ref` and `subject.kind`. `eval.run_id`,
  `eval.eval_set_id`, `eval.dataset` and `results.headline` go to `run.json`'s `ext."inspect_ai"`. A `started` log
  gives a running run, which is not sealed: only a closed run is ([SEAL-1](../spec/04-integrity.md#41-sealing-a-run)).
  The run it writes is verified, and refused (nothing written) when it does not verify (IN-11).
- **Scores** (IN-7). A score's metric is its key; a map member's metric is the member's key, a letter `C`, `I`, `P`
  or `N` in a map is the value its row gives with the letter as label, and a map's line is `scored`. NaN with
  `grader_failed` or `scoring_failed` is `error`; NaN with any other reason, or none, is `not_measured`; the three
  reasons that blame the model make the line `failed` whatever its value. A line's `reason` is the `Score.reason`
  name, or "Inspect recorded no value (NaN) and no reason" for an unscored value without one. A list value goes to
  the line's `ext`, and the line is `scored` without a score. `Score.answer` and `Score.metadata` go to the line's
  `ext."inspect_ai"`, so the `metadata.aef` of an AEF export comes back as data, not as the line's state.
  `evaluator.id` is the `scorer` of the `results.scores` entry named like the key, or the key itself. With
  `contentCapture: off`, the explanation and `Score.answer` are left out: a run that keeps no content keeps no judge
  reasoning and no response, in `reason` or `ext` either ([RUN-11](../spec/03-run.md#32-runjson)), and the
  `Score.explanation` row yields to it.
- **Samples** (IN-8). `trial` is `epoch` − 1 when `eval.config.epochs` is above 1. A sample's times go on each of its
  lines; its `total_time` (as `durationMs`) and its usage go on its first line. The usage is one entry per
  `role_usage` role (`agent`, `judge` and `attacker` as they are, a role of `eval.model_roles` as `judge`, any other
  as `other`), with the role's model when the sample's `model_usage` has it; and an `agent` entry from `model_usage`
  under `eval.model` when no role is the agent. A sample with `error` or `limit` puts each of its lines in `error`
  (`not_measured` for a limit), without scores, with the message in `reason`; one that stopped before it was scored
  gets a line per scorer of `eval.scorers`. With `contentCapture: on`, `input`, `target`, `output` (when it has
  choices) and `messages` are blobs, as written when they are text and as compact JSON otherwise, cited by every line
  of the sample, one evidence record per kind and content. With more than one epoch, each reduction gives the case's
  rollup at its scorer: the reduced score read as a sample's is, `trials.aggregation` and `k` from its reducer.
- **The summary** (IN-9). One lane, `main`. Each `results.scores` entry gives one summary entry at its name for the
  metric of that name: its `accuracy` or `mean` is the entry's mean, and any other metric beside it goes to
  `summary.json`'s `ext."inspect_ai"`; without a mean, its one other metric is the entry's `aggregate`. `N`, `n`,
  `notMeasured`, `sum` and the value are computed from the lines ([SUM-5](../spec/03-run.md#36-summaryjson)), and
  `stderr` is Inspect's. Every metric a line or the summary names is declared with kind `score`, direction `none`
  and scale `unbounded`.

**Refused** (IN-6 to IN-10; settled 10-09). The converter refuses these, naming the rule, and writes nothing:

- an `eval_id` that is not an AEF id; a time without an offset; a closed log without `stats.completed_at`, or one
  that ends before it starts; an `error` log without a message (IN-6);
- a value that is a boolean, or a string other than `C`, `I`, `P` and `N` (IN-7);
- more than one epochs reducer; an epoch beyond `eval.config.epochs`; a sample without scores (or, for one that
  stopped, in a log that names no scorer); two scores of one case, path and epoch; a reducer without an AEF value, a
  reduction with no epoch lines, and, in a closed log, a case's path with epoch lines and no reduction
  ([RES-8](../spec/03-run.md#344-repeated-trials)) (IN-8);
- a `results.scores` entry with both `accuracy` and `mean`, or with no mean and more than one other metric, or whose
  metric cannot be an `aggregate` method; and a mean, median, minimum or maximum Inspect gives that the lines do
  not (IN-9);
- `Score.history` edits, `samples[].invalidation` and `log_updates`: the table makes them overlay events, and the
  reference converter writes no overlays (IN-10).

[`examples/inspect-aef/`](examples/inspect-aef/) imports a hand-written log, and holds refused inputs for each item of
this list.

## What does not carry over

[`examples/aef-inspect/`](examples/aef-inspect/) takes a corpus run to Inspect and back, and `tools/check_interop.py`
checks, field by field, that what the trip loses is what the first list says.

**AEF → Inspect.**

- The state: Inspect has no verdict, and one unscored value, NaN, for every typed absence. The table keeps the state
  in `Score.metadata`, which comes back as `ext` (IN-7): a number comes back `scored`, NaN `not_measured`, `error` or
  `failed`.
- A typed absence's reason: it travels as the explanation, and comes back as a `reasoning` blob (not at all with
  `contentCapture: off`), with the state's name as `reason`.
- The result tree and its aggregation, severity, verdict rules, thresholds and uncertainty.
- Evaluator and annotator identity: an evaluator comes back as its scorer (IN-7).
- A score's metric and `normalized` value: a single score's metric comes back as its key, the path (IN-7).
- A line's `turns`, `attack` and `lane`.
- Which line of a case a `usage` entry was on, and its `costSource`: Inspect keeps a sample's usage per role and per
  model, and it comes back on the sample's first line (IN-8).
- The times and duration of a line that is not the case's root.
- A rollup's `n`, `passed` and `agree`: they come back counted from the epochs' lines.
- Evidence links and digests, and the case content unless the run captured it; traces and trace links; gate
  decisions.
- The seal, signatures and the overlay chain: an Inspect log is mutable.
- The summary (its lanes, `sum`, `sumSq`, verdicts, rules and `cost`) and the metric declarations: they come back
  recomputed from the lines, in one lane `main`, with every metric of kind `score` (IN-9).
- The rest of `run.json`: the subject but its ref (which comes back as `model:<ref>`, IN-6), the producer, the
  deployment, `execution`, the judges but their models, `imported`, the suite's digest and `frozen`, `requirePasses`,
  `config`, `otel`, `costPolicy`, `provenance`, `contentCapture` and `ext`.

**Inspect → AEF.**

- List-valued scores, kept in `ext` (IN-7).
- Reducers without an AEF value (`at_least` for a k other than 1 or all, `pass_k`, `collect`): refused with more than
  one epoch (IN-8).
- `working_time`.
- Inspect's event transcript as structured events.
- Groups of runs (`eval_set_id`, kept in `ext`: AEF has no field for a group of sibling runs,
  [Gaps](README.md#gaps-found-by-these-mappings)).
- Inspect's role names beyond the four AEF roles.
- The case content, when the converter writes `contentCapture: off`.
- A sample's `metadata`, `eval.metadata`, the plan and the solver.
- `Score.explanation` and `Score.answer`, when the converter writes `contentCapture: off` (IN-7).

## Worked example

The corpus line for `triage/policy` of `case-17`. Its `resultId` recomputes with
[RES-4](../spec/03-run.md#342-result-ids):

```json
{"schemaVersion":"1.0","resultId":"r_bb4438fbedb43552a9fe55695931d5e4","parentResultId":"r_479d157f3423e95d566bcbfc0c6d2461","caseId":"case-17","path":"triage/policy","evaluator":{"id":"code:refund-escalation","version":"1"},"state":"passed","scores":[{"metric":"policy","value":1.0,"normalized":1.0}],"annotator":{"kind":"CODE"},"component":{"weight":0.5,"required":true}}
```

It becomes the `triage/policy` score of the Inspect sample for `case-17`. The case's other scored lines, `triage`
(`r_479d157f3423e95d566bcbfc0c6d2461`) and `triage/helpfulness` (`r_1264eeb36620c9cbe97b71ffdbcfd331`), go into the
same sample. The run has one trial per case, so the sample is epoch 1. The run kept no input or target for the case,
so those required fields are empty. The `usage` entries of `triage/helpfulness` become `role_usage`, with cache and
reasoning tokens, and the judge's entry is also `model_usage` under the judge's model. What the table sends to
`Score.metadata` goes under `metadata.aef`, with the line's `resultId`, `state` and `evaluator`. The sample is the
first of [`examples/aef-inspect/inspect.json`](examples/aef-inspect/inspect.json), which converts the whole run, and
`tools/check_interop.py` fails when this block differs from it:

```json
{
  "id": "case-17",
  "epoch": 1,
  "input": "",
  "target": "",
  "scores": {
    "triage": {
      "value": 0.55,
      "metadata": {"aef": {"resultId": "r_479d157f3423e95d566bcbfc0c6d2461", "state": "failed", "evaluator": {"id": "composite:triage", "version": "2"}, "severity": "medium", "verdictRule": {"expr": "triage >= threshold", "threshold": 0.8, "source": "run.config.thresholds.triage"}, "aggregation": {"strategy": "WeightedSum", "threshold": 0.8, "score": 0.55, "rulePath": "threshold", "measured": 2, "total": 3, "minimumMeasuredShare": 0.5, "unmeasured": {"not_measured": 0, "not_applicable": 1, "skipped": 0, "error": 0}, "decisive": ["r_1264eeb36620c9cbe97b71ffdbcfd331"]}}}
    },
    "triage/policy": {
      "value": 1.0,
      "metadata": {"aef": {"resultId": "r_bb4438fbedb43552a9fe55695931d5e4", "state": "passed", "evaluator": {"id": "code:refund-escalation", "version": "1"}, "parentResultId": "r_479d157f3423e95d566bcbfc0c6d2461", "annotator": {"kind": "CODE"}, "component": {"weight": 0.5, "required": true}}}
    },
    "triage/helpfulness": {
      "value": 0.1,
      "explanation": "The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n",
      "metadata": {"aef": {"resultId": "r_1264eeb36620c9cbe97b71ffdbcfd331", "state": "failed", "evaluator": {"id": "llm:helpfulness", "version": "3"}, "parentResultId": "r_479d157f3423e95d566bcbfc0c6d2461", "severity": "medium", "verdictRule": {"expr": "helpfulness >= threshold", "threshold": 0.7, "source": "suite"}, "annotator": {"kind": "LLM", "model": "gpt-5.1", "promptHash": "sha256:cf07194ee232eb531e15f690000d19846dea69cf05504782658afcfacb9228a2", "rubricDigest": "sha256:29fd018a9848938bc2b0e33fffa32bde2827e81388d0e03195919be5835c3605"}, "component": {"weight": 0.5, "required": false}}}
    }
  },
  "model_usage": {"gpt-5.1": {"input_tokens": 1747, "output_tokens": 488, "total_tokens": 2235, "reasoning_tokens": 301, "total_cost": 0.012}},
  "role_usage": {
    "agent": {"input_tokens": 912, "output_tokens": 214, "total_tokens": 1126, "input_tokens_cache_read": 640},
    "judge": {"input_tokens": 1747, "output_tokens": 488, "total_tokens": 2235, "reasoning_tokens": 301, "total_cost": 0.012}
  },
  "total_time": 4.21
}
```

The fourth line of the case, `triage/groundedness` (`not_applicable`), has no score. Inspect would write it with the
NaN token, which is not JSON, so it is shown apart:

```text
"triage/groundedness": {"value": NaN, "reason": "not_applicable", "explanation": "No retrieved context was recorded for this case: nothing to ground against.", "metadata": {"aef": {"resultId": "r_306364ba4d940f649ea348dadfbe17de", "state": "not_applicable", "evaluator": {"id": "llm:groundedness", "version": "1"}, "parentResultId": "r_479d157f3423e95d566bcbfc0c6d2461", "component": {"weight": 0.0, "required": false}}}}
```

Read back, `id` gives `caseId` `case-17`, each score key gives the `path`, and a single epoch gives no `trial`. RES-4
then gives the same three ids as the corpus: `r_479d157f3423e95d566bcbfc0c6d2461`, `r_bb4438fbedb43552a9fe55695931d5e4`
and `r_1264eeb36620c9cbe97b71ffdbcfd331`. The converter reads `Score.metadata` as `ext` (IN-7), so the three numeric
values come back `scored`, with the AEF facts kept beside them as data. Converted back with
`tools/aef_interop.py from-inspect` (the second line of
[`examples/aef-inspect/run/results.ndjson`](examples/aef-inspect/run/results.ndjson)), the `triage/policy` score is:

```json
{"schemaVersion":"1.0","resultId":"r_bb4438fbedb43552a9fe55695931d5e4","caseId":"case-17","path":"triage/policy","evaluator":{"id":"triage/policy"},"state":"scored","scores":[{"metric":"triage/policy","value":1.0}],"ext":{"inspect_ai":{"metadata":{"aef":{"resultId":"r_bb4438fbedb43552a9fe55695931d5e4","state":"passed","evaluator":{"id":"code:refund-escalation","version":"1"},"parentResultId":"r_479d157f3423e95d566bcbfc0c6d2461","annotator":{"kind":"CODE"},"component":{"weight":0.5,"required":true}}}}}}
```

[`examples/aef-inspect-trials/`](examples/aef-inspect-trials/) converts a running run in three trials per case: each
trial is a sample at epoch `trial` + 1, and the rollup a reduction with the reducer `majority`.
