# OpenAI Evals

"OpenAI Evals" names two formats, and this page maps both.

| Format | Version read | Status |
|---|---|---|
| The open-source framework `openai/evals`: a JSONL recording per run | main at `8eac7a7de5215c907fbddc30efdaf316913eccdd` (2026-04-14). The recorder, `evals/record.py`, is unchanged since 2024-01-26. PyPI `evals` 3.0.1.post1 (2024-05-01). | Maintained but frozen. Its README sends users to the hosted product. |
| The hosted Evals API (`/v1/evals`) | `openai/openai-openapi` at `506aff0a8099581b50e119b87f8f2692cdad043f` (spec version 2.3.0); `openai-python` 3.26.1 | Current. No `/evals` operation is marked deprecated. |

New work targets the hosted API. The open-source format matters for importing older logs.

## The open-source recording format

`oaieval` writes one JSONL file per run (`evals/record.py`, `LocalRecorder`):

1. A first line `{"spec": …}`: `completion_fns`, `eval_name` (e.g. `test-match.s1.simple-v0`), `base_eval`, `split`,
   `run_config`, `created_by`, `run_id` (`%y%m%d%H%M%S` and eight base32 characters), `created_at`. `created_at` is
   `str(datetime.utcnow())`: no offset, a space between date and time.
2. One line per event: `run_id`, `event_id` (in order), `sample_id` (`<base_eval>.<split>.<index>`), `type`, `data`,
   `created_by`, `created_at`.

   | `type` | `data` |
   |---|---|
   | `sampling` | `prompt`, `sampled`, and from the chat completion `model`, `usage` |
   | `match` | `correct` (boolean), `expected`, `picked`, `sampled`, `options` |
   | `metrics` | free key-value pairs |
   | `error` | `type`, `message` |
   | `embedding`, `cond_logp`, `pick_option`, `function_call`, `raw_sample`, `extra` | as named |

3. A last line `{"final_report": …, "run_id": …}`, e.g. `{"accuracy": 1.0, "boostrap_std": 0.0}` (the key is spelled
   that way in `evals/elsuite/basic/match.py`).

A run has no status field, and a sample has no state beyond `match.correct`.

## The hosted Evals API

- An **eval** (`object: "eval"`) has a `data_source_config` (`custom` with a JSON Schema, `logs`, or
  `stored_completions`) and `testing_criteria`, which are graders:
  - `string_check`: an operation `eq`, `ne`, `like` or `ilike`;
  - `text_similarity`: an `evaluation_metric` and a `pass_threshold`;
  - `label_model`: `labels` and `passing_labels`;
  - `score_model`: a `range` (default `[0, 1]`) and a `pass_threshold`;
  - `python`: source code and a `pass_threshold`;
  - `multi`: a combination of graders.
- A **run** (`object: "eval.run"`) has:
  - `id`, `eval_id`, `name`, `model`, `created_at` (Unix seconds), `report_url`, `error`;
  - `status` (the list filter accepts `queued`, `in_progress`, `completed`, `canceled`, `failed`);
  - a `data_source` (`jsonl`, `completions` or `responses`);
  - `result_counts` (`total`, `errored`, `failed`, `passed`);
  - `per_model_usage` (`invocation_count`, `prompt_tokens`, `completion_tokens`, `cached_tokens`, …);
  - `per_testing_criteria_results` (`testing_criteria`, `passed`, `failed`).
- An **output item** (`object: "eval.run.output_item"`) has:
  - `datasource_item_id` (an integer), `datasource_item`, `status` (`pass` or `fail`);
  - `results[]`: per grader `name`, `type`, `score`, `passed`, and the grader's own `sample`;
  - `sample`: the generation's `input`, `output`, `usage`, `error`, `finish_reason`, `model`, `temperature`, `top_p`,
    `seed`, `max_completion_tokens`.
- Output items can only be read. No endpoint accepts results computed elsewhere: the hosted API always grades with its
  own graders. A run's `jsonl` data source accepts items as `{"item": {…}, "sample": {…}}`, so outputs produced
  elsewhere can be uploaded and graded.

## AEF → the open-source log

| AEF | Open-source log | Fidelity |
|---|---|---|
| `runId` | `spec.run_id`, and `run_id` on every event | exact |
| `startedAt` | `spec.created_at` (UTC, written without offset) | exact |
| `subject.ref` | `spec.completion_fns` | lossy |
| `suite.ref`, `suite.version` | `spec.base_eval`, `spec.eval_name` | lossy |
| `config` | `spec.run_config` | exact |
| `producer` | `created_by` | lossy |
| `caseId` | `sample_id` | exact |
| `state` `passed` / `failed` | a `match` event with `correct` `true` / `false` | exact |
| `state` `warn`, `inconclusive` | `match` with `correct: false` | lossy: the state survives only in `data` |
| typed absences | an `error` event (for `error`) or no event | lossy |
| `scores[]` | a `metrics` event, metric id to value | exact |
| `path`, `resultId` | `data` of the event | none: no reader of the format looks there |
| `reasoning` and other blobs (capture `on`) | `sampling` events | lossy |
| `usage` | `sampling.data.usage` (`prompt_tokens`, `completion_tokens`) | exact |
| `summary.json` | `final_report` | lossy: N, n, verdicts, intervals are lost |
| the result tree, severity, judges, evidence, gates, seal, overlays | none | none |

## The open-source log → AEF

| Open-source log | AEF | Fidelity |
|---|---|---|
| `spec.run_id` | `runId` | exact |
| `spec.created_at` | `startedAt` (append `Z`, replace the space with `T`) | exact, since `utcnow` is UTC |
| the last event's `created_at` | `endedAt` | lossy |
| `spec.base_eval`, `spec.split`, `spec.eval_name` | `suite.ref` (`suite:openai-evals/<base_eval>`), `suite.version` (the rest of `eval_name`) | lossy: no digest of the samples file ([I7](README.md#gaps-found-by-these-mappings)) |
| `spec.completion_fns` | `subject` (`kind: model`) | lossy: AEF has one subject ([I7](README.md#gaps-found-by-these-mappings)) |
| `spec.run_config`, `created_by` | `config`, `ext` | exact as data |
| (the log does not name the framework version) | `producer.version` | none: the importer supplies it ([I7](README.md#gaps-found-by-these-mappings)) |
| `sample_id` | `caseId` | exact |
| `match` | a line with `state` `passed` or `failed`, and a score of 1 or 0 | exact |
| `match.expected`, `picked`, `sampled`; `sampling.prompt`, `sampled` | blobs with evidence, only with `contentCapture: on` | lossy ([I2](README.md#gaps-found-by-these-mappings)) |
| `sampling.usage` | `usage` (`role: agent`) | lossy: cached tokens have no field ([I4](README.md#gaps-found-by-these-mappings)) |
| `metrics` | `scores[]` | exact, but `state` has no source ([I1](README.md#gaps-found-by-these-mappings)) |
| `error` | `state: error`, `reason` = `type: message` | exact |
| `function_call` | evidence of kind `tool_call` (capture `on`) | lossy |
| `cond_logp`, `pick_option`, `embedding`, `raw_sample`, `extra` | `ext` | none |
| `event_id`, each event's `created_at` | none | none ([I6](README.md#gaps-found-by-these-mappings)) |
| `final_report.accuracy` | a summary entry for a metric of kind `rate`, which the verifier recomputes from the `match` lines ([SUM-5](../spec/03-run.md#36-summaryjson)) | exact |
| `final_report.boostrap_std` | the entry's `stderr` | lossy: it is a bootstrap estimate under another name |
| other `final_report` keys | `ext` | none ([I1](README.md#gaps-found-by-these-mappings)) |
| (no run status) | `status: completed` when `final_report` is present, else `aborted` | lossy |

## AEF → the hosted API

AEF results cannot be uploaded: the hosted API records only its own graders' results. What an exporter can do is
create an eval with a `custom` data source and start a run whose `jsonl` items carry each case's content, so that
OpenAI's graders grade AEF's recorded outputs again. That needs `contentCapture: on`
([RUN-11](../spec/03-run.md#32-runjson)) and gives OpenAI's results. None of AEF's scores, states, typed absences,
judges, tree or seal travels.

## The hosted API → AEF

| Hosted API | AEF | Fidelity |
|---|---|---|
| run `id` | `runId` | exact |
| run `status` | `completed` → `completed`; `failed`, `canceled` → `aborted` (with `error.message` or "canceled" as `abortReason`); `queued`, `in_progress` → `running` | exact |
| run `created_at` | `startedAt` | exact to the second; the run has no end time, so `endedAt` is inferred |
| `eval_id`, the eval's `name` | `suite.ref` (`suite:openai/<eval_id>`) | exact |
| (an eval has no version) | `suite.version` and `suite.digest`, computed from the eval's `testing_criteria` and `data_source_config` | lossy ([I7](README.md#gaps-found-by-these-mappings)) |
| run `model` | `subject` (`kind: model`, `ref: model:<model>`) | exact |
| run `data_source.type` | `execution.targetMode`: `live` for `completions` and `responses`; `replayed` for `jsonl` items that carry a `sample` | exact |
| `testing_criteria[]` | `metrics.json` entries (`scale` from `score_model.range`, else 0 to 1; `direction: higher_better`) and `config.thresholds` from `pass_threshold` | exact; `label_model` labels have no field ([I1](README.md#gaps-found-by-these-mappings)) |
| the graders' `model` | `judges[].model` | exact |
| `report_url` | an evidence record with a URI link | exact |
| `per_testing_criteria_results` | summary entries of kind `rate` | exact; the entry's `verdict` has no source ([I1](README.md#gaps-found-by-these-mappings)) |
| `result_counts.errored` | lines in state `error` | exact |
| `per_model_usage` | none | none ([I4](README.md#gaps-found-by-these-mappings)) |
| output item `datasource_item_id` | `caseId` (as a string) | exact |
| output item `status` `pass` / `fail`; `sample.error` set | a root line in `passed` / `failed`; `error` | exact |
| `results[]` | one child line per grader: `path` = `output_item/<name>`, `evaluator.id` = `openai:<type>`, `scores[].value` = `score`, `state` from `passed` | exact |
| (how item status follows from the graders) | `aggregation` on the root | lossy: OpenAI does not document the rule; the example below records `Min` |
| `results[].sample` (the grader's own output) | a `reasoning` blob (capture `on`) | lossy ([I2](README.md#gaps-found-by-these-mappings)) |
| `sample.input`, `sample.output`, `datasource_item` | blobs with evidence (capture `on`) | lossy ([I2](README.md#gaps-found-by-these-mappings)) |
| `sample.usage` | `usage` on the root (`role: agent`) | lossy: `cached_tokens` has no field ([I4](README.md#gaps-found-by-these-mappings)) |
| `sample.temperature`, `top_p`, `seed`, `max_completion_tokens`, `finish_reason` | `ext` | none: `config` is run-level |
| output item `created_at` | none | none ([I6](README.md#gaps-found-by-these-mappings)) |

## What does not carry over

**AEF → OpenAI Evals.** In the open-source log: typed absences, `warn` and `inconclusive` as states, the result tree,
severity, judges, evidence, gate decisions, the seal and overlays. In the hosted API: everything AEF graded, because
the API grades itself.

**OpenAI Evals → AEF.** A verdict for scores and metrics without a pass rule, and categorical labels
([I1](README.md#gaps-found-by-these-mappings)). The case content without capture ([I2](README.md#gaps-found-by-these-mappings)). Cached tokens and run-level
usage per model ([I4](README.md#gaps-found-by-these-mappings)). Per-event and per-item times ([I6](README.md#gaps-found-by-these-mappings)). A suite version
and the framework's own version, which the importer has to supply ([I7](README.md#gaps-found-by-these-mappings)). Per-item sampling
parameters.

## Worked examples

### An AEF line in the open-source log

The corpus line for `case-18`. Its `resultId` recomputes with [RES-4](../spec/03-run.md#342-result-ids):

```json
{"schemaVersion":"1.0","resultId":"r_3b156bcb57417545db27c39e78c955bd","parentResultId":null,"caseId":"case-18","path":"triage","evaluator":{"id":"composite:triage","version":"2"},"state":"warn","severity":"low","scores":[{"metric":"triage","value":0.78,"normalized":0.78}]}
```

It becomes two events. `warn` is not a pass, so `correct` is `false`; the state and the path survive only in `data`.
A result line has no time, so both events carry `run.json` `endedAt` ([I6](README.md#gaps-found-by-these-mappings)):

```jsonl
{"run_id":"01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10","event_id":0,"sample_id":"case-18","type":"match","data":{"correct":false,"expected":null,"picked":null,"aef":{"path":"triage","state":"warn","resultId":"r_3b156bcb57417545db27c39e78c955bd"}},"created_by":"agenteval-cli 1.0.0","created_at":"2026-10-02 14:06:23.004000+00:00"}
{"run_id":"01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10","event_id":1,"sample_id":"case-18","type":"metrics","data":{"triage":0.78,"aef":{"path":"triage"}},"created_by":"agenteval-cli 1.0.0","created_at":"2026-10-02 14:06:23.004000+00:00"}
```

Read back with `data.aef.path`, the line gets the same id, `r_3b156bcb57417545db27c39e78c955bd`. An importer that
ignores `data.aef` gets `state: failed` and has to choose a path itself.

### A hosted output item in AEF

The output item in OpenAI's own OpenAPI example (`EvalRunOutputItem`):

```json
{"object":"eval.run.output_item","id":"outputitem_67abd55eb6548190bb580745d5644a33","run_id":"evalrun_67abd54d60ec8190832b46859da808f7","eval_id":"eval_67abd54d9b0081909a86353f6fb9317a","created_at":1739314509,"status":"pass","datasource_item_id":137,"datasource_item":{"teacher":"To grade essays, I only check for style, content, and grammar.","student":"I am a student who is trying to write the best essay."},"results":[{"name":"String Check Grader","type":"string-check-grader","score":1.0,"passed":true}],"sample":{"input":[{"role":"system","content":"You are an evaluator bot..."},{"role":"user","content":"You are assessing..."}],"output":[{"role":"assistant","content":"The rubric is not clear nor concise."}],"finish_reason":"stop","model":"gpt-6-astra","usage":{"total_tokens":521,"completion_tokens":2,"prompt_tokens":519,"cached_tokens":0},"error":null,"temperature":1.0,"max_completion_tokens":2048,"top_p":1.0,"seed":42}}
```

It becomes a root line and one child line in a run whose `runId` is the item's `run_id`. Both are valid against the
writer schema, and both ids are RES-4 hashes of that run id, case `137` and the path. `Min` and the component's weight
and `required` are the importer's reading, because OpenAI does not document how an item's status follows from its
graders. `cached_tokens` and the sampling parameters are lost:

```jsonl
{"schemaVersion":"1.0","resultId":"r_2df32507b8f62ab0e259c92d6fff6628","parentResultId":null,"caseId":"137","path":"output_item","evaluator":{"id":"openai:eval_67abd54d9b0081909a86353f6fb9317a"},"state":"passed","aggregation":{"strategy":"Min","rulePath":"threshold","measured":1,"total":1},"usage":[{"gen_ai.usage.input_tokens":519,"gen_ai.usage.output_tokens":2,"role":"agent"}]}
{"schemaVersion":"1.0","resultId":"r_38b7751f11a7b7e5e5bfad37e1a79f44","parentResultId":"r_2df32507b8f62ab0e259c92d6fff6628","caseId":"137","path":"output_item/String Check Grader","evaluator":{"id":"openai:string-check-grader"},"state":"passed","scores":[{"metric":"String Check Grader","value":1.0}],"annotator":{"kind":"CODE"},"component":{"weight":1,"required":true}}
```

## Open gaps

- [I1](README.md#gaps-found-by-these-mappings): `metrics` events and `final_report` keys without a pass rule; label graders.
- [I2](README.md#gaps-found-by-these-mappings): prompts, outputs and data-source items have a place only as captured blobs.
- [I4](README.md#gaps-found-by-these-mappings): cached tokens, `per_model_usage`.
- [I6](README.md#gaps-found-by-these-mappings): event and item times.
- [I7](README.md#gaps-found-by-these-mappings): the suite version, subject and framework version an importer supplies.
