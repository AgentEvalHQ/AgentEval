# OpenTelemetry

**Version read:** `open-telemetry/semantic-conventions-genai` at commit `06ec68e722c45a7218e23ea1bc1339fe4e21ecae`
(2026-10-07). That repository has no release yet. Its schema URL is
`https://opentelemetry.io/schemas/gen-ai-dev/1.42.0-dev`, and it builds on the core semantic conventions v1.44.0. The
GenAI conventions moved out of the core repository in core v1.42.0 (2026-06-12). The evaluation event first appeared
in core v1.38.0 (2025-10-29). OTLP specification 1.11.0. Everything on this page has Development stability and can
still change.

## The target

### The evaluation event

An evaluation result is an **event**: an OTLP log record with `eventName` set to `gen_ai.evaluation.result`. Its
requirement level is Recommended. The convention says: "This event SHOULD be parented to GenAI operation span being
evaluated when possible or set `gen_ai.response.id` when span id is not available." A parented event carries that
span's `traceId` and `spanId`. No body is defined.

| Attribute | Type | Requirement | Meaning |
|---|---|---|---|
| `gen_ai.evaluation.name` | string | Required | the name of the evaluation metric, e.g. `Relevance` |
| `gen_ai.evaluation.score.value` | double | Conditionally Required, "if applicable" | the score the evaluator returned |
| `gen_ai.evaluation.score.label` | string | Conditionally Required, "if applicable" | a human-readable, low-cardinality label, e.g. `pass`, `fail`, `relevant`. "Implementations SHOULD document the possible values." |
| `gen_ai.evaluation.explanation` | string | Recommended | the evaluator's free-form explanation |
| `gen_ai.response.id` | string | Recommended when available | the completion evaluated, when no span id is available |
| `error.type` | string | Conditionally Required, if the evaluation ended in an error | a low-cardinality error class; `_OTHER` as fallback |

The repository's own reference scenarios (`reference/scenarios/azure-ai-evaluation`, `deepeval`) emit the event inside
an evaluation span. So in practice the parent is often the evaluation's own span.

Open proposals that touch the mapping:

| Item | Proposes | State on 2026-10-08 |
|---|---|---|
| PR #359 | `gen_ai.evaluation.evaluator.id`, `.version`, `.type` (`llm_judge`, `deterministic`, `human`), `gen_ai.evaluation.reference_set.id` | open |
| #79, split into #422 | `test.suite.run.id`, `test.case.id` | open |
| #39 | `gen_ai.evaluation.score.range` | open |
| #43 | a confidence interval on the event | open |
| #33 | evaluation spans beside the events | open |
| #470 | an evidence reference (URI, hash, media type) on the event | closed as not planned |

The core registry already has `test.case.name`, `test.suite.name`, `test.case.result.status` (`pass`, `fail`) and
`test.suite.run.status` (core v1.44.0, Development).

### Traces and usage

GenAI spans carry token counts. On inference and agent spans `gen_ai.usage.input_tokens` and
`gen_ai.usage.output_tokens` are Recommended. Finer counts are subsets of those two:
`gen_ai.usage.cache_read.input_tokens`, `gen_ai.usage.cache_write.input_tokens`,
`gen_ai.usage.reasoning.output_tokens`, and per-modality counts. Prompt and completion content lives in
`gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`, `gen_ai.tool.call.arguments` and
`gen_ai.tool.call.result`.

In OTLP/JSON, trace and span ids are hex strings, 64-bit integers are decimal strings, and keys are lowerCamelCase.
OpenTelemetry's file exporter writes one JSON object per line and one signal per file: traces, metrics or logs.

## How AEF uses OpenTelemetry today

| AEF | Rule | What it holds |
|---|---|---|
| `traces.otlp.jsonl` | [RUN-14](../spec/03-run.md#310-traces) | OTLP/JSON traces, one `TracesData` object per line (the JSON of an `ExportTraceServiceRequest`), sealed like every file ([SEAL-2](../spec/04-integrity.md#41-sealing-a-run)) |
| `traceLink` on a result | [RES-10](../spec/03-run.md#345-facts-about-a-result) | the span of the operation the result evaluates (or, with `traceId` only, its trace); checked against `traces.otlp.jsonl` when present (`trace-link`, [§3.9](../spec/03-run.md#39-rules-across-files)) |
| evidence with a span link | [EVD-1, EVD-2](../spec/03-run.md#37-evidencendjson-and-blobs) | a span as evidence; a span link carries no digest |
| `usage` on a result | [RES-10](../spec/03-run.md#345-facts-about-a-result) | one entry per party (`role`): `gen_ai.usage.input_tokens`, `output_tokens`, `cache_read.input_tokens`, `cache_write.input_tokens`, `reasoning.output_tokens`, `costUsd`, `costSource` |
| `subject.telemetry` in `run.json` | [RUN-6](../spec/03-run.md#32-runjson) | `agentId` and `serviceName`, the keys that join the subject to `gen_ai.agent.id` and `service.name` |
| `otel` in `run.json` | [RUN-14](../spec/03-run.md#310-traces) | `semconvVersion` (`MAJOR.MINOR[.PATCH]`) and `dialects` |
| `contentCapture: off` | [RUN-11](../spec/03-run.md#32-runjson), [§8.4](../spec/08-security.md#84-privacy) | traces carry none of the content attributes listed above |

In the corpus run, `traces.otlp.jsonl` holds two spans: `invoke_agent support-triage`, the agent's invocation that
`triage/helpfulness` evaluates (its `traceLink` names it), and its child `chat gpt-5.1`, the judge's call that
graded it (cited as evidence `E-1`).

## AEF → OpenTelemetry

An exporter writes one event per score of a result line. A line without scores gives one event without
`gen_ai.evaluation.score.value`. The events go to an OTLP logs endpoint or a logs file. AEF has no run file for them
([I3](README.md#gaps-found-by-these-mappings)).

| AEF | OpenTelemetry | Fidelity |
|---|---|---|
| `traceLink.traceId`, `traceLink.spanId` | log record `traceId`, `spanId` | exact: `traceLink` names the evaluated operation (RES-10), the span OpenTelemetry parents an evaluation event to |
| `scores[].metric` | `gen_ai.evaluation.name` | exact |
| `scores[].value` | `gen_ai.evaluation.score.value` | exact |
| `scores[].normalized` | none | none |
| `state` | `gen_ai.evaluation.score.label`, written as the AEF state name | exact only for a reader that knows this vocabulary; AEF has not published it ([I3](README.md#gaps-found-by-these-mappings)) |
| `reason` | `gen_ai.evaluation.explanation` | exact |
| `reasoning` (a blob, with `contentCapture: on`) | `gen_ai.evaluation.explanation`, when the line has no `reason` | lossy: the text survives; the blob's digest does not |
| `state: error` | `error.type: _OTHER` | lossy: AEF records no error class |
| `caseId` | `test.case.name` (core registry) | exact |
| `path` | none | none |
| `runId` | none (`test.suite.run.id` is proposed) | none |
| `trial`, `trials` | none | none |
| `evaluator.id`, `evaluator.version` | none (`gen_ai.evaluation.evaluator.id`, `.version` are proposed in PR #359) | none |
| `annotator.kind` | none (`gen_ai.evaluation.evaluator.type` is proposed; it has no value for `HYBRID`) | none |
| `annotator.model`, `promptHash`, `rubricDigest`, `panel` | none | none |
| `verdictRule`, `run.json` `config.thresholds` | none | none |
| `uncertainty` | none | none |
| `parentResultId`, `component`, `aggregation` | none | none |
| `severity`, `durationMs`, `turns`, `attack`, `lane` | none | none |
| `evidence` | none | none |
| `usage` | the attributes of the same names on the graded operation's span | exact; the judge's span (`chat gpt-5.1`) in `traces.otlp.jsonl` already carries them |
| `run.json` `subject.telemetry.serviceName` | resource attribute `service.name` | exact |
| (no time on a result line) | `timeUnixNano`: the exporter has to choose, e.g. `run.json` `endedAt` | lossy ([I6](README.md#gaps-found-by-these-mappings)) |
| `traces.otlp.jsonl` | OTLP traces, sent as they are | exact |

## OpenTelemetry → AEF

| OpenTelemetry | AEF | Fidelity |
|---|---|---|
| log record `traceId`, `spanId` | `traceLink`; an evidence record of kind `span` without a digest ([EVD-2](../spec/03-run.md#37-evidencendjson-and-blobs)) | exact |
| `gen_ai.evaluation.name` | `scores[].metric`, and `path` when nothing better is known | exact for the metric |
| (no metric declaration) | a `metrics.json` entry ([SUM-1](../spec/03-run.md#35-metricsjson)) with `kind: score`, `direction: none`, `scale: unbounded` | lossy: the event gives no kind, direction or range |
| `gen_ai.evaluation.score.value` | `scores[].value` | exact |
| `gen_ai.evaluation.score.label` | `state`, when the label is an AEF state name or the importer declares a mapping (`pass` → `passed`, `fail` → `failed`) | lossy: other labels have no field ([I1](README.md#gaps-found-by-these-mappings)) |
| a value and no label | `state` has no source | none ([I1](README.md#gaps-found-by-these-mappings)) |
| `gen_ai.evaluation.explanation` | `reason` (at most 4096 characters) | exact up to that length |
| `error.type` | `state: error`, with the type in `reason` | lossy |
| `test.case.name` | `caseId` | exact |
| `gen_ai.response.id` | `caseId`, when no case name is present | lossy |
| `timeUnixNano` | none | none ([I6](README.md#gaps-found-by-these-mappings)) |
| resource `service.name`; `gen_ai.agent.id` on the parent span | `subject.telemetry.serviceName`, `subject.telemetry.agentId` | exact |
| the parent span and its trace | lines of `traces.otlp.jsonl` | exact |
| the log record itself | no AEF file holds OTLP logs | none ([I3](README.md#gaps-found-by-these-mappings)) |

## What does not carry over

**AEF → OpenTelemetry.** The result tree (`parentResultId`, `component`, `aggregation`). The distinction between
typed absences, except through a label vocabulary nobody has published yet. Trials and their rollups. Thresholds,
verdict rules and uncertainty. Metric declarations. The summary and gate decisions. The run header: producer, suite
and its digest, judges and their calibration, `execution.targetMode`. Evidence digests, the seal, overlays and
signatures.

**OpenTelemetry → AEF.** The event as a record, because AEF stores traces only ([I3](README.md#gaps-found-by-these-mappings)). The time
of each event ([I6](README.md#gaps-found-by-these-mappings)). A label that is not a verdict, and a score without a verdict
([I1](README.md#gaps-found-by-these-mappings)). The run and the result path: the event has no attribute for either today, so an importer
builds `runId` and `path` itself, and the result id differs from the original's.

## Worked example

The corpus line for `triage/helpfulness` of `case-17` (run `01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10`). Its `resultId`
recomputes with [RES-4](../spec/03-run.md#342-result-ids):

```json
{"schemaVersion":"1.0","resultId":"r_1264eeb36620c9cbe97b71ffdbcfd331","parentResultId":"r_479d157f3423e95d566bcbfc0c6d2461","caseId":"case-17","path":"triage/helpfulness","evaluator":{"id":"llm:helpfulness","version":"3"},"state":"failed","severity":"medium","scores":[{"metric":"helpfulness","value":0.1,"normalized":0.1}],"verdictRule":{"expr":"helpfulness >= threshold","threshold":0.7,"source":"suite"},"annotator":{"kind":"LLM","model":"gpt-5.1","promptHash":"sha256:cf07194ee232eb531e15f690000d19846dea69cf05504782658afcfacb9228a2","rubricDigest":"sha256:29fd018a9848938bc2b0e33fffa32bde2827e81388d0e03195919be5835c3605"},"reasoning":{"blob":"sha256:635221c9c64f48e2843e4186b0a1b66f07a1492c14dcb866bb83dba6a5e5fef5","bytes":122},"usage":[{"role":"agent","gen_ai.usage.input_tokens":912,"gen_ai.usage.output_tokens":214,"gen_ai.usage.cache_read.input_tokens":640},{"role":"judge","gen_ai.usage.input_tokens":1747,"gen_ai.usage.output_tokens":488,"gen_ai.usage.reasoning.output_tokens":301,"costUsd":0.012,"costSource":"price-table:2026-09-30"}],"startedAt":"2026-10-02T14:02:11.120Z","endedAt":"2026-10-02T14:02:15Z","traceLink":{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7"},"component":{"weight":0.5,"required":false},"evidence":["E-2"]}
```

Exported as one OTLP/JSON logs line. The explanation is the reasoning blob's text, because the run has
`contentCapture: on` and the line has no `reason`. The time is `run.json` `endedAt`. The parent is the evaluated
operation's span, `invoke_agent support-triage`:

```json
{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"support-api"}}]},"scopeLogs":[{"scope":{"name":"agenteval"},"logRecords":[{"timeUnixNano":"1790949983004000000","eventName":"gen_ai.evaluation.result","traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7","attributes":[{"key":"gen_ai.evaluation.name","value":{"stringValue":"helpfulness"}},{"key":"gen_ai.evaluation.score.value","value":{"doubleValue":0.1}},{"key":"gen_ai.evaluation.score.label","value":{"stringValue":"failed"}},{"key":"gen_ai.evaluation.explanation","value":{"stringValue":"The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n"}},{"key":"test.case.name","value":{"stringValue":"case-17"}}]}]}]}]}
```

Lost on the way: the run id, the path `triage/helpfulness`, the parent, the weight 0.5 and `required: false`, the
threshold 0.7, the severity, the evaluator id and version, and the judge model and digests. The usage is already on
the judge span in `traces.otlp.jsonl`.

Imported back into a new run `otel-import-0001`, the event gives this line, valid against the writer schema. The run
id is new and the path is the metric name, so the result id changes from `r_1264eeb36620c9cbe97b71ffdbcfd331` to
`r_66dbca71f5b161285e50f3acef16a88d`:

```json
{"schemaVersion":"1.0","resultId":"r_66dbca71f5b161285e50f3acef16a88d","parentResultId":null,"caseId":"case-17","path":"helpfulness","evaluator":{"id":"helpfulness"},"state":"failed","reason":"The answer escalated the refund — as policy §4 requires · « correct » ✓ 👍 مرحبا\r\nSecond line (CRLF kept).\n","scores":[{"metric":"helpfulness","value":0.1}],"traceLink":{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7"}}
```

## Open gaps

- [I1](README.md#gaps-found-by-these-mappings): an event with a score and no label has no AEF state; a label that is not a verdict has no
  field.
- [I3](README.md#gaps-found-by-these-mappings): no logs file in a run; `otel.semconvVersion` cannot name the GenAI registry (its schema
  URL is `gen-ai-dev/1.42.0-dev`, which the version pattern refuses); no published label vocabulary.
- [I6](README.md#gaps-found-by-these-mappings): a result line has no time to give an event.
